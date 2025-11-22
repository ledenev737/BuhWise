using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using BuhWise.Models;
using Microsoft.Data.Sqlite;

namespace BuhWise.Data
{
    public class OperationRepository
    {
        private readonly DatabaseService _database;

        public OperationRepository(DatabaseService database)
        {
            _database = database;
            _database.EnsureCreated();
        }

        public IReadOnlyDictionary<string, double> GetBalances()
        {
            var balances = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            using var connection = _database.GetConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Currency, Amount FROM Balances";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var currency = reader.GetString(0);
                var amount = reader.GetDouble(1);
                balances[currency] = amount;
            }

            return balances;
        }

        public IReadOnlyDictionary<string, double> GetUsdRates()
        {
            var rates = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            using var connection = _database.GetConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Currency, RateToUsd FROM Rates";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rates[reader.GetString(0)] = reader.GetDouble(1);
            }

            return rates;
        }

        public double? GetLastPairRate(string from, string to)
        {
            using var connection = _database.GetConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT LastRate FROM ExchangeRateMemory
                                    WHERE FromCurrency = $from AND ToCurrency = $to";
            command.Parameters.AddWithValue("$from", from);
            command.Parameters.AddWithValue("$to", to);

            var result = command.ExecuteScalar();
            return result switch
            {
                double d => d,
                null => null,
                _ => Convert.ToDouble(result, CultureInfo.InvariantCulture)
            };
        }

        public List<Operation> GetOperations()
        {
            var operations = new List<Operation>();
            using var connection = _database.GetConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT Id, Date, Type, SourceCurrency, SourceAmount, TargetCurrency, TargetAmount, Rate, Commission, UsdEquivalent, ExpenseCategory, ExpenseComment FROM Operations ORDER BY Date DESC, Id DESC";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                operations.Add(MapOperation(reader));
            }

            return operations;
        }

        public IEnumerable<Currency> GetCurrencies(bool activeOnly = true)
        {
            var currencies = new List<Currency>();
            using var connection = _database.GetConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id, Code, Name, IsActive FROM Currencies" + (activeOnly ? " WHERE IsActive = 1" : string.Empty) + " ORDER BY Code";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                currencies.Add(new Currency
                {
                    Id = reader.GetInt64(0),
                    Code = reader.GetString(1),
                    Name = reader.GetString(2),
                    IsActive = reader.GetInt64(3) == 1
                });
            }

            return currencies;
        }

        public void AddCurrency(Currency currency)
        {
            using var connection = _database.GetConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = @"INSERT INTO Currencies (Code, Name, IsActive) VALUES ($code, $name, $active)";
                insert.Parameters.AddWithValue("$code", currency.Code);
                insert.Parameters.AddWithValue("$name", currency.Name);
                insert.Parameters.AddWithValue("$active", currency.IsActive ? 1 : 0);
                insert.ExecuteNonQuery();
            }

            if (currency.IsActive)
            {
                DatabaseService.EnsureCurrencyRows(connection, currency.Code);
            }

            transaction.Commit();
        }

        public void UpdateCurrency(Currency currency)
        {
            using var connection = _database.GetConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE Currencies SET Name = $name, IsActive = $active WHERE Code = $code";
                update.Parameters.AddWithValue("$name", currency.Name);
                update.Parameters.AddWithValue("$active", currency.IsActive ? 1 : 0);
                update.Parameters.AddWithValue("$code", currency.Code);
                update.ExecuteNonQuery();
            }

            if (currency.IsActive)
            {
                DatabaseService.EnsureCurrencyRows(connection, currency.Code);
            }

            transaction.Commit();
        }

        public Operation AddOperation(OperationDraft draft)
        {
            var operation = BuildOperation(draft);

            using var connection = _database.GetConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            EnsureCurrencyExists(connection, transaction, operation.SourceCurrency);
            EnsureCurrencyExists(connection, transaction, operation.TargetCurrency);

            EnsureSufficientFunds(operation, connection, transaction);
            InsertOperation(operation, connection, transaction);
            UpdateBalances(operation, connection, transaction);
            UpdateRates(operation, connection, transaction);
            UpdateRateMemory(operation, connection, transaction);
            TryLogOperationChange(operation, "Create", null, connection, transaction);

            transaction.Commit();
            return operation;
        }

        public void DeleteOperation(long operationId, string? reason = null)
        {
            using var connection = _database.GetConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            var operation = GetOperationById(operationId, connection, transaction);
            if (operation is null)
            {
                return;
            }

            ReverseBalances(operation, connection, transaction);

            using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM Operations WHERE Id = $id";
                deleteCommand.Parameters.AddWithValue("$id", operationId);
                deleteCommand.ExecuteNonQuery();
            }

            TryLogOperationChange(operation, "Delete", reason, connection, transaction);

            transaction.Commit();
        }

        public Operation RestoreOperationFromChange(OperationChange change)
        {
            if (!string.Equals(change.Action, "Delete", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Выбранная запись не относится к удалению и не может быть восстановлена.");
            }

            var restoredFromSnapshot = DeserializeOperationSnapshot(change.Details);
            if (restoredFromSnapshot is null)
            {
                throw new InvalidOperationException("Не удалось восстановить данные операции из истории.");
            }

            using var connection = _database.GetConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            EnsureCurrencyExists(connection, transaction, restoredFromSnapshot.SourceCurrency);
            EnsureCurrencyExists(connection, transaction, restoredFromSnapshot.TargetCurrency);

            // Повторное восстановление одной и той же записи допустимо и приведет к дубликату содержимого (новый Id).
            InsertOperation(restoredFromSnapshot, connection, transaction);
            RebuildDerivedState(connection, transaction);
            TryLogOperationChange(restoredFromSnapshot, "Restore", $"Restored from change #{change.Id}", connection, transaction);

            transaction.Commit();
            return restoredFromSnapshot;
        }

        public void ReplaceAllOperations(IEnumerable<Operation> operations)
        {
            var ordered = operations.OrderBy(o => o.Date).ThenBy(o => o.Id).ToList();

            using var connection = _database.GetConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            ClearOperations(connection, transaction);
            ResetBalances(connection, transaction);
            ResetRates(connection, transaction);
            ResetRateMemory(connection, transaction);

            foreach (var operation in ordered)
            {
                var normalized = NormalizeOperation(operation);

                EnsureCurrencyExists(connection, transaction, normalized.SourceCurrency);
                EnsureCurrencyExists(connection, transaction, normalized.TargetCurrency);

                InsertOperation(normalized, connection, transaction);
                UpdateBalances(normalized, connection, transaction);
                UpdateRates(normalized, connection, transaction);
                UpdateRateMemory(normalized, connection, transaction);
            }

            transaction.Commit();
        }

        public IEnumerable<OperationChange> GetOperationChanges(long? operationId = null)
        {
            var changes = new List<OperationChange>();

            using var connection = _database.GetConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT Id, OperationId, Action, Timestamp, Details, Reason
                                    FROM OperationChanges
                                    WHERE ($operationId IS NULL OR OperationId = $operationId)
                                    ORDER BY Timestamp DESC, Id DESC";
            command.Parameters.AddWithValue("$operationId", (object?)operationId ?? DBNull.Value);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                changes.Add(new OperationChange
                {
                    Id = reader.GetInt64(0),
                    OperationId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    Action = reader.GetString(2),
                    Timestamp = DateTime.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
                    Details = reader.GetString(4),
                    Reason = reader.IsDBNull(5) ? null : reader.GetString(5)
                });
            }

            return changes;
        }

        private static void InsertOperation(Operation operation, SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO Operations (Date, Type, SourceCurrency, SourceAmount, TargetCurrency, TargetAmount, Rate, Commission, UsdEquivalent, ExpenseCategory, ExpenseComment)
                                    VALUES ($date, $type, $sourceCurrency, $sourceAmount, $targetCurrency, $targetAmount, $rate, $commission, $usdEquivalent, $expenseCategory, $expenseComment);
                                    SELECT last_insert_rowid();";

            command.Parameters.AddWithValue("$date", operation.Date.ToString("O"));
            command.Parameters.AddWithValue("$type", operation.Type.ToString());
            command.Parameters.AddWithValue("$sourceCurrency", operation.SourceCurrency);
            command.Parameters.AddWithValue("$sourceAmount", operation.SourceAmount);
            command.Parameters.AddWithValue("$targetCurrency", operation.TargetCurrency);
            command.Parameters.AddWithValue("$targetAmount", operation.TargetAmount);
            command.Parameters.AddWithValue("$rate", operation.Rate);
            command.Parameters.AddWithValue("$commission", (object?)operation.Commission ?? DBNull.Value);
            command.Parameters.AddWithValue("$usdEquivalent", operation.UsdEquivalent);
            command.Parameters.AddWithValue("$expenseCategory", (object?)operation.ExpenseCategory ?? DBNull.Value);
            command.Parameters.AddWithValue("$expenseComment", (object?)operation.ExpenseComment ?? DBNull.Value);

            var id = (long)(command.ExecuteScalar() ?? 0L);
            operation.Id = id;
        }

        private static void ClearOperations(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var deleteOps = connection.CreateCommand();
            deleteOps.Transaction = transaction;
            deleteOps.CommandText = "DELETE FROM Operations";
            deleteOps.ExecuteNonQuery();

            using var deleteChanges = connection.CreateCommand();
            deleteChanges.Transaction = transaction;
            deleteChanges.CommandText = "DELETE FROM OperationChanges";
            deleteChanges.ExecuteNonQuery();
        }

        private static void ResetBalances(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var reset = connection.CreateCommand();
            reset.Transaction = transaction;
            reset.CommandText = "UPDATE Balances SET Amount = 0";
            reset.ExecuteNonQuery();
        }

        private void ResetRates(SqliteConnection connection, SqliteTransaction transaction)
        {
            using (var clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM Rates";
                clear.ExecuteNonQuery();
            }

            foreach (var code in GetActiveCurrencyCodes(connection, transaction))
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO Rates (Currency, RateToUsd) VALUES ($currency, $rate)";
                insert.Parameters.AddWithValue("$currency", code);
                insert.Parameters.AddWithValue("$rate", string.Equals(code, "USD", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
                insert.ExecuteNonQuery();
            }
        }

        private static void ResetRateMemory(SqliteConnection connection, SqliteTransaction transaction)
        {
            using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM ExchangeRateMemory";
            clear.ExecuteNonQuery();
        }

        private void RebuildDerivedState(SqliteConnection connection, SqliteTransaction transaction)
        {
            ResetBalances(connection, transaction);
            ResetRates(connection, transaction);
            ResetRateMemory(connection, transaction);

            foreach (var operation in GetOrderedOperations(connection, transaction))
            {
                UpdateBalances(operation, connection, transaction);
                UpdateRates(operation, connection, transaction);
                UpdateRateMemory(operation, connection, transaction);
            }
        }

        private static void UpdateBalances(Operation operation, SqliteConnection connection, SqliteTransaction transaction)
        {
            void ApplyDelta(string currency, double delta)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE Balances SET Amount = Amount + $delta WHERE Currency = $currency";
                command.Parameters.AddWithValue("$delta", delta);
                command.Parameters.AddWithValue("$currency", currency);
                command.ExecuteNonQuery();
            }

            switch (operation.Type)
            {
                case OperationType.Income:
                    ApplyDelta(operation.SourceCurrency, operation.SourceAmount);
                    break;
                case OperationType.Expense:
                    ApplyDelta(operation.SourceCurrency, -operation.SourceAmount);
                    break;
                case OperationType.Exchange:
                    ApplyDelta(operation.SourceCurrency, -operation.SourceAmount);
                    ApplyDelta(operation.TargetCurrency, operation.TargetAmount);
                    break;
            }
        }

        private static void ReverseBalances(Operation operation, SqliteConnection connection, SqliteTransaction transaction)
        {
            void ApplyDelta(string currency, double delta)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE Balances SET Amount = Amount + $delta WHERE Currency = $currency";
                command.Parameters.AddWithValue("$delta", delta);
                command.Parameters.AddWithValue("$currency", currency);
                command.ExecuteNonQuery();
            }

            switch (operation.Type)
            {
                case OperationType.Income:
                    ApplyDelta(operation.SourceCurrency, -operation.SourceAmount);
                    break;
                case OperationType.Expense:
                    ApplyDelta(operation.SourceCurrency, operation.SourceAmount);
                    break;
                case OperationType.Exchange:
                    ApplyDelta(operation.SourceCurrency, operation.SourceAmount);
                    ApplyDelta(operation.TargetCurrency, -operation.TargetAmount);
                    break;
            }
        }

        private static void UpdateRates(Operation operation, SqliteConnection connection, SqliteTransaction transaction)
        {
            void UpsertRate(string currency, double rateToUsd)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"INSERT OR REPLACE INTO Rates (Currency, RateToUsd) VALUES ($currency, $rate)";
                command.Parameters.AddWithValue("$currency", currency);
                command.Parameters.AddWithValue("$rate", rateToUsd);
                command.ExecuteNonQuery();
            }

            if (operation.Type == OperationType.Exchange)
            {
                if (string.Equals(operation.TargetCurrency, "USD", StringComparison.OrdinalIgnoreCase) && operation.Rate > 0)
                {
                    UpsertRate(operation.SourceCurrency, operation.Rate);
                }
                else if (string.Equals(operation.SourceCurrency, "USD", StringComparison.OrdinalIgnoreCase) && operation.Rate > 0)
                {
                    var inverted = operation.Rate > 0 ? 1 / operation.Rate : 0;
                    UpsertRate(operation.TargetCurrency, inverted);
                }
            }
            else if (!string.Equals(operation.SourceCurrency, "USD", StringComparison.OrdinalIgnoreCase) && operation.Rate > 0)
            {
                UpsertRate(operation.SourceCurrency, operation.Rate);
            }
        }

        private static void UpdateRateMemory(Operation operation, SqliteConnection connection, SqliteTransaction transaction)
        {
            if (operation.Type != OperationType.Exchange)
            {
                return;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT OR REPLACE INTO ExchangeRateMemory (FromCurrency, ToCurrency, LastRate, UpdatedAt)
                                    VALUES ($from, $to, $rate, $updatedAt)";
            command.Parameters.AddWithValue("$from", operation.SourceCurrency);
            command.Parameters.AddWithValue("$to", operation.TargetCurrency);
            command.Parameters.AddWithValue("$rate", operation.Rate);
            command.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        private Operation BuildOperation(OperationDraft draft)
        {
            var targetCurrency = draft.Type == OperationType.Exchange ? draft.TargetCurrency : draft.SourceCurrency;
            var (targetAmount, canonicalRate) = draft.Type == OperationType.Exchange
                ? CalculateExchange(draft)
                : (draft.SourceAmount, draft.Rate);
            var usdEquivalent = CalculateUsdEquivalent(draft, targetAmount);

            return new Operation
            {
                Date = draft.Date,
                Type = draft.Type,
                SourceCurrency = draft.SourceCurrency,
                SourceAmount = draft.SourceAmount,
                TargetCurrency = targetCurrency,
                TargetAmount = targetAmount,
                Rate = canonicalRate,
                Commission = draft.Commission,
                UsdEquivalent = usdEquivalent,
                ExpenseCategory = draft.Type == OperationType.Expense ? draft.ExpenseCategory : null,
                ExpenseComment = draft.Type == OperationType.Expense ? draft.ExpenseComment : null
            };
        }

        private static string BuildDetails(Operation operation)
        {
            var payload = new
            {
                operation.Id,
                Date = operation.Date.ToString("o", CultureInfo.InvariantCulture),
                Type = operation.Type.ToString(),
                SourceCurrency = operation.SourceCurrency,
                operation.SourceAmount,
                TargetCurrency = operation.TargetCurrency,
                operation.TargetAmount,
                operation.Rate,
                operation.Commission,
                operation.UsdEquivalent,
                operation.ExpenseCategory,
                operation.ExpenseComment
            };

            return JsonSerializer.Serialize(payload);
        }

        private static (double TargetAmount, double CanonicalRate) CalculateExchange(OperationDraft draft)
        {
            var baseConverted = draft.SourceAmount * draft.Rate;
            var fee = draft.Commission ?? 0;
            var finalTarget = Math.Max(0, baseConverted - fee);

            var canonicalRate = draft.SourceAmount > 0
                ? finalTarget / draft.SourceAmount
                : 0d;

            return (finalTarget, canonicalRate);
        }

        private double CalculateUsdEquivalent(OperationDraft draft, double targetAmount)
        {
            var rates = GetUsdRates();
            double GetRate(string c) => rates.TryGetValue(c, out var r) ? r : 0;

            return draft.Type switch
            {
                OperationType.Income => string.Equals(draft.SourceCurrency, "USD", StringComparison.OrdinalIgnoreCase)
                    ? draft.SourceAmount
                    : draft.SourceAmount * draft.Rate,
                OperationType.Expense => string.Equals(draft.SourceCurrency, "USD", StringComparison.OrdinalIgnoreCase)
                    ? draft.SourceAmount
                    : draft.SourceAmount * draft.Rate,
                OperationType.Exchange => draft.TargetCurrency switch
                {
                    var t when string.Equals(t, "USD", StringComparison.OrdinalIgnoreCase) => targetAmount,
                    _ when string.Equals(draft.SourceCurrency, "USD", StringComparison.OrdinalIgnoreCase) => draft.SourceAmount,
                    _ => targetAmount * GetRate(draft.TargetCurrency)
                },
                _ => 0
            };
        }

        private static Operation? GetOperationById(long id, SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"SELECT Id, Date, Type, SourceCurrency, SourceAmount, TargetCurrency, TargetAmount, Rate, Commission, UsdEquivalent, ExpenseCategory, ExpenseComment FROM Operations WHERE Id = $id";
            command.Parameters.AddWithValue("$id", id);

            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return MapOperation(reader);
        }

        private static Operation NormalizeOperation(Operation operation)
        {
            if (operation.Type == OperationType.Exchange && operation.SourceAmount > 0)
            {
                operation.Rate = operation.TargetAmount / operation.SourceAmount;
            }

            return operation;
        }

        private static double GetBalance(SqliteConnection connection, SqliteTransaction transaction, string currency)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT Amount FROM Balances WHERE Currency = $currency";
            command.Parameters.AddWithValue("$currency", currency);
            var result = command.ExecuteScalar();
            return result switch
            {
                double d => d,
                null => 0d,
                _ => Convert.ToDouble(result, CultureInfo.InvariantCulture)
            };
        }

        private static void EnsureSufficientFunds(Operation operation, SqliteConnection connection, SqliteTransaction transaction)
        {
            if (operation.Type == OperationType.Income)
            {
                return;
            }

            if (operation.Type is OperationType.Expense or OperationType.Exchange)
            {
                var balance = GetBalance(connection, transaction, operation.SourceCurrency);
                if (balance - operation.SourceAmount < -0.0001)
                {
                    throw new InvalidOperationException($"Недостаточно средств в {operation.SourceCurrency} для операции.");
                }
            }
        }

        private static void TryLogOperationChange(Operation operation, string action, string? reason, SqliteConnection connection, SqliteTransaction transaction)
        {
            try
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"INSERT INTO OperationChanges (OperationId, Action, Timestamp, Details, Reason)
                                            VALUES ($operationId, $action, $timestamp, $details, $reason)";
                command.Parameters.AddWithValue("$operationId", operation.Id);
                command.Parameters.AddWithValue("$action", action);
                command.Parameters.AddWithValue("$timestamp", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$details", BuildDetails(operation));
                command.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to log operation change: {ex.Message}");
            }
        }

        private static IEnumerable<string> GetActiveCurrencyCodes(SqliteConnection connection, SqliteTransaction transaction)
        {
            var codes = new List<string>();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT Code FROM Currencies WHERE IsActive = 1";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                codes.Add(reader.GetString(0));
            }

            return codes;
        }

        private static void EnsureCurrencyExists(SqliteConnection connection, SqliteTransaction transaction, string code)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "INSERT OR IGNORE INTO Currencies (Code, Name, IsActive) VALUES ($code, $name, 1)";
                command.Parameters.AddWithValue("$code", code);
                command.Parameters.AddWithValue("$name", code);
                command.ExecuteNonQuery();
            }

            DatabaseService.EnsureCurrencyRows(connection, code);
        }

        private static Operation? DeserializeOperationSnapshot(string details)
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<OperationSnapshot>(details);
                if (snapshot is null)
                {
                    return null;
                }

                return new Operation
                {
                    Id = snapshot.Id,
                    Date = DateTime.Parse(snapshot.Date, CultureInfo.InvariantCulture),
                    Type = Enum.Parse<OperationType>(snapshot.Type, true),
                    SourceCurrency = snapshot.SourceCurrency ?? string.Empty,
                    SourceAmount = snapshot.SourceAmount,
                    TargetCurrency = snapshot.TargetCurrency ?? snapshot.SourceCurrency ?? string.Empty,
                    TargetAmount = snapshot.TargetAmount,
                    Rate = snapshot.Rate,
                    Commission = snapshot.Commission,
                    UsdEquivalent = snapshot.UsdEquivalent,
                    ExpenseCategory = snapshot.ExpenseCategory,
                    ExpenseComment = snapshot.ExpenseComment
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to deserialize operation snapshot: {ex.Message}");
                return null;
            }
        }

        private static List<Operation> GetOrderedOperations(SqliteConnection connection, SqliteTransaction transaction)
        {
            var operations = new List<Operation>();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"SELECT Id, Date, Type, SourceCurrency, SourceAmount, TargetCurrency, TargetAmount, Rate, Commission, UsdEquivalent, ExpenseCategory, ExpenseComment
                                    FROM Operations
                                    ORDER BY Date, Id";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                operations.Add(MapOperation(reader));
            }

            return operations;
        }

        private static Operation MapOperation(SqliteDataReader reader)
        {
            return new Operation
            {
                Id = reader.GetInt64(0),
                Date = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                Type = Enum.Parse<OperationType>(reader.GetString(2)),
                SourceCurrency = reader.GetString(3),
                SourceAmount = reader.GetDouble(4),
                TargetCurrency = reader.GetString(5),
                TargetAmount = reader.GetDouble(6),
                Rate = reader.GetDouble(7),
                Commission = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                UsdEquivalent = reader.GetDouble(9),
                ExpenseCategory = reader.IsDBNull(10) ? null : reader.GetString(10),
                ExpenseComment = reader.IsDBNull(11) ? null : reader.GetString(11)
            };
        }

        private class OperationSnapshot
        {
            public long Id { get; set; }
            public string Date { get; set; } = string.Empty;
            public string Type { get; set; } = string.Empty;
            public string? SourceCurrency { get; set; }
            public double SourceAmount { get; set; }
            public string? TargetCurrency { get; set; }
            public double TargetAmount { get; set; }
            public double Rate { get; set; }
            public double? Commission { get; set; }
            public double UsdEquivalent { get; set; }
            public string? ExpenseCategory { get; set; }
            public string? ExpenseComment { get; set; }
        }
    }
}
