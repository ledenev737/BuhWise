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
                    UsdEquivalent REAL NOT NULL
                );";
                command.ExecuteNonQuery();
            }

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

            EnsureCurrencyRows(connection, "USD");
            EnsureCurrencyRows(connection, "EUR");
            EnsureCurrencyRows(connection, "RUB");
        }

        private static void EnsureCurrencyRows(SqliteConnection connection, string currency)
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
    }
}
