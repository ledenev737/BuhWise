using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace BuhWise.Data
{
    public class DatabaseService
    {
        private readonly string _connectionString;

        public DatabaseService(string databasePath)
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                directory = ".";
            }

            Directory.CreateDirectory(directory);
            _connectionString = $"Data Source={databasePath}";
        }

        public SqliteConnection GetConnection() => new(_connectionString);

        public void EnsureCreated()
        {
            using var connection = GetConnection();
            connection.Open();

            CreateCurrencyTable(connection);
            CreateFxDisplayConfigTable(connection);

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"CREATE TABLE IF NOT EXISTS Operations (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Date TEXT NOT NULL,
                    Type TEXT NOT NULL,
                    SourceCurrency TEXT NOT NULL,
                    SourceAmount REAL NOT NULL,
                    TargetCurrency TEXT NOT NULL,
                    TargetAmount REAL NOT NULL,
                    Rate REAL NOT NULL,
                    Commission REAL NULL,
                    UsdEquivalent REAL NOT NULL,
                    ExpenseCategory TEXT NULL,
                    ExpenseComment TEXT NULL
                );";
                command.ExecuteNonQuery();
            }

            EnsureColumnExists(connection, "Operations", "ExpenseCategory", "TEXT NULL");
            EnsureColumnExists(connection, "Operations", "ExpenseComment", "TEXT NULL");

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"CREATE TABLE IF NOT EXISTS Balances (
                    Currency TEXT PRIMARY KEY,
                    Amount REAL NOT NULL
                );";
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"CREATE TABLE IF NOT EXISTS Rates (
                    Currency TEXT PRIMARY KEY,
                    RateToUsd REAL NOT NULL
                );";
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"CREATE TABLE IF NOT EXISTS ExchangeRateMemory (
                    FromCurrency TEXT NOT NULL,
                    ToCurrency TEXT NOT NULL,
                    LastRate REAL NOT NULL,
                    UpdatedAt TEXT NOT NULL,
                    PRIMARY KEY (FromCurrency, ToCurrency)
                );";
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"CREATE TABLE IF NOT EXISTS OperationChanges (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    OperationId INTEGER NULL,
                    Action TEXT NOT NULL,
                    Timestamp TEXT NOT NULL,
                    Details TEXT NOT NULL,
                    Reason TEXT NULL
                );";
                command.ExecuteNonQuery();
            }

            SeedCurrencyIfMissing(connection, "USD", "US Dollar");
            SeedCurrencyIfMissing(connection, "EUR", "Euro");
            SeedCurrencyIfMissing(connection, "RUB", "Russian Ruble");
            SeedFxDisplayDefaults(connection);
            EnsureCurrencyRows(connection);
        }

        private static void CreateCurrencyTable(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"CREATE TABLE IF NOT EXISTS Currencies (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Code TEXT NOT NULL UNIQUE,
                    Name TEXT NOT NULL,
                    IsActive INTEGER NOT NULL DEFAULT 1
                );";
            command.ExecuteNonQuery();
        }

        private static void CreateFxDisplayConfigTable(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"CREATE TABLE IF NOT EXISTS FxRateDisplayConfig (
                    FromCurrencyCode TEXT NOT NULL,
                    ToCurrencyCode TEXT NOT NULL,
                    DisplayMode TEXT NOT NULL,
                    UpdatedAt TEXT NULL,
                    PRIMARY KEY (FromCurrencyCode, ToCurrencyCode)
                );";
            command.ExecuteNonQuery();
        }

        private static void SeedCurrencyIfMissing(SqliteConnection connection, string code, string name)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = @"INSERT OR IGNORE INTO Currencies (Code, Name, IsActive) VALUES ($code, $name, 1)";
            insert.Parameters.AddWithValue("$code", code);
            insert.Parameters.AddWithValue("$name", name);
            insert.ExecuteNonQuery();
        }

        private static void SeedFxDisplayDefaults(SqliteConnection connection)
        {
            InsertDisplayConfig(connection, "RUB", "USD", "Inverted");
            InsertDisplayConfig(connection, "RUB", "EUR", "Inverted");
        }

        private static void InsertDisplayConfig(SqliteConnection connection, string from, string to, string mode)
        {
            using var command = connection.CreateCommand();
            command.CommandText = @"INSERT OR IGNORE INTO FxRateDisplayConfig (FromCurrencyCode, ToCurrencyCode, DisplayMode, UpdatedAt)
                                    VALUES ($from, $to, $mode, $updated)";
            command.Parameters.AddWithValue("$from", from);
            command.Parameters.AddWithValue("$to", to);
            command.Parameters.AddWithValue("$mode", mode);
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("o"));
            command.ExecuteNonQuery();
        }

        private static void EnsureCurrencyRows(SqliteConnection connection)
        {
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT Code FROM Currencies WHERE IsActive = 1";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var code = reader.GetString(0);
                EnsureCurrencyRows(connection, code);
            }
        }

        public static void EnsureCurrencyRows(SqliteConnection connection, string currency)
        {
            using (var insertBalance = connection.CreateCommand())
            {
                insertBalance.CommandText = "INSERT OR IGNORE INTO Balances (Currency, Amount) VALUES ($currency, 0)";
                insertBalance.Parameters.AddWithValue("$currency", currency);
                insertBalance.ExecuteNonQuery();
            }

            using (var insertRate = connection.CreateCommand())
            {
                insertRate.CommandText = "INSERT OR IGNORE INTO Rates (Currency, RateToUsd) VALUES ($currency, $rate)";
                insertRate.Parameters.AddWithValue("$currency", currency);
                insertRate.Parameters.AddWithValue("$rate", currency == "USD" ? 1 : 0);
                insertRate.ExecuteNonQuery();
            }
        }

        private static void EnsureColumnExists(SqliteConnection connection, string table, string column, string definition)
        {
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = $"PRAGMA table_info({table})";
                using var reader = pragma.ExecuteReader();
                while (reader.Read())
                {
                    if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }

            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
            alter.ExecuteNonQuery();
        }
    }
}
