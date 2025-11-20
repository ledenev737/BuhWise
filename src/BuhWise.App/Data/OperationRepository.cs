using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using BuhWise.Models;
using Microsoft.Data.Sqlite;
using System.Text.Json;

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

        public IReadOnlyDictionary<Currency, double> GetBalances()
        {
            var balances = new Dictionary<Currency, double>();
            using var connection = _database.GetConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Currency, Amount FROM Balances";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var currency = Enum.Parse<Currency>(reader.GetString(0));
                var amount = reader.GetDouble(1);
                balances[currency] = amount;
            }

            return balances;
        }

        public IReadOnlyDictionary<Currency, double> GetUsdRates()
        {
            var rates = new Dictionary<Currency, double>();
            using var connection = _database.GetConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Currency, RateToUsd FROM Rates";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var currency = Enum.Parse<Currency>(reader.GetString(0));
                var rate = reader.GetDouble(1);
                rates[currency] = rate;
            }

            return rates;
        }

        public double? GetLastPairRate(Currency from, Currency to)
        {
            using var connection = _database.GetConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT LastRate FROM ExchangeRateMemory
                                    WHERE FromCurrency = $from AND ToCurrency = $to";
            command.Parameters.AddWithValue("$from", from.ToString());
            command.Parameters.AddWithValue("$to", to.ToString());

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
                operations.Add(new Operation
                {
                    Id = reader.GetInt64(0),
                    Date = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                    Type = Enum.Parse<OperationType>(reader.GetString(2)),
                    SourceCurrency = Enum.Parse<Currency>(reader.GetString(3)),
                    SourceAmount = reader.GetDouble(4),
                    TargetCurrency = Enum.Parse<Currency>(reader.GetString(5)),
                    TargetAmount = reader.GetDouble(6),
                    Rate = reader.GetDouble(7),
                    Commission = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                    UsdEquivalent = reader.GetDouble(9),
                    ExpenseCategory = reader.IsDBNull(10) ? null : reader.GetString(10),
                    ExpenseComment = reader.IsDBNull(11) ? null : reader.GetString(11)
                });
            }

            return operations;
        }

        public Operation AddOperation(OperationDraft draft)
        {
            var operation = BuildOperation(draft);

            using var connection = _database.GetConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

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

        public void ReplaceAllOperations(IEnumerable<Operation> operations)
        {
            var ordered = operations
                .OrderBy(o => o.Date)
                .ThenBy(o => o.Id)
                .ToList();

            using var connection = _database.GetConnection();
            connection.Open();
            using var transaction = connection.BeginTransaction();

            ClearOperations(connection, transaction);
            ResetBalances(connection, transaction);
            ResetRates(connection, transaction);
            ResetRateMemory(connection, transaction);

            foreach (var operation in ordered)
            {
                InsertOperation(operation, connection, transaction);
                UpdateBalances(operation, connection, transaction);
                UpdateRates(operation, connection, transaction);
                UpdateRateMemory(operation, connection, transaction);
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
            command.Parameters.AddWithValue("$sourceCurrency", operation.SourceCurrency.ToString());
            command.Parameters.AddWithValue("$sourceAmount", operation.SourceAmount);
            command.Parameters.AddWithValue("$targetCurrency", operation.TargetCurrency.ToString());
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

        private static void ResetRates(SqliteConnection connection, SqliteTransaction transaction)
        {
            using (var clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM Rates";
                clear.ExecuteNonQuery();
            }

            foreach (var currency in Enum.GetValues<Currency>())
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT INTO Rates (Currency, RateToUsd) VALUES ($currency, $rate)";
                insert.Parameters.AddWithValue("$currency", currency.ToString());
                insert.Parameters.AddWithValue("$rate", currency == Currency.USD ? 1 : 0);
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

        private static void UpdateBalances(Operation operation, SqliteConnection connection, SqliteTransaction transaction)
        {
            void ApplyDelta(Currency currency, double delta)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE Balances SET Amount = Amount + $delta WHERE Currency = $currency";
                command.Parameters.AddWithValue("$delta", delta);
                command.Parameters.AddWithValue("$currency", currency.ToString());
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
            void ApplyDelta(Currency currency, double delta)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE Balances SET Amount = Amount + $delta WHERE Currency = $currency";
                command.Parameters.AddWithValue("$delta", delta);
                command.Parameters.AddWithValue("$currency", currency.ToString());
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

        private void UpdateRates(Operation operation, SqliteConnection connection, SqliteTransaction transaction)
        {
            // Persist last-known USD-cross rates to simplify cross-currency USD equivalents.
            void UpsertRate(Currency currency, double rateToUsd)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"INSERT OR REPLACE INTO Rates (Currency, RateToUsd) VALUES ($currency, $rate)";
                command.Parameters.AddWithValue("$currency", currency.ToString());
                command.Parameters.AddWithValue("$rate", rateToUsd);
                command.ExecuteNonQuery();
            }

            if (operation.Type == OperationType.Exchange)
            {
                if (operation.TargetCurrency == Currency.USD && operation.Rate > 0)
                {
                    UpsertRate(operation.SourceCurrency, operation.Rate);
                }
                else if (operation.SourceCurrency == Currency.USD && operation.Rate > 0)
                {
                    var inverted = operation.Rate > 0 ? 1 / operation.Rate : 0;
                    UpsertRate(operation.TargetCurrency, inverted);
                }
            }
            else if (operation.SourceCurrency != Currency.USD && operation.Rate > 0)
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
            command.Parameters.AddWithValue("$from", operation.SourceCurrency.ToString());
            command.Parameters.AddWithValue("$to", operation.TargetCurrency.ToString());
            command.Parameters.AddWithValue("$rate", operation.Rate);
            command.Parameters.AddWithValue("$updatedAt", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }

        private Operation BuildOperation(OperationDraft draft)
        {
            var targetCurrency = draft.Type == OperationType.Exchange ? draft.TargetCurrency : draft.SourceCurrency;
            var targetAmount = draft.Type == OperationType.Exchange ? CalculateExchangeAmount(draft) : draft.SourceAmount;
            var usdEquivalent = CalculateUsdEquivalent(draft, targetAmount);

            return new Operation
            {
                Date = draft.Date,
                Type = draft.Type,
                SourceCurrency = draft.SourceCurrency,
                SourceAmount = draft.SourceAmount,
                TargetCurrency = targetCurrency,
                TargetAmount = targetAmount,
                Rate = draft.Rate,
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
                SourceCurrency = operation.SourceCurrency.ToString(),
                operation.SourceAmount,
                TargetCurrency = operation.TargetCurrency.ToString(),
                operation.TargetAmount,
                operation.Rate,
                operation.Commission,
                operation.UsdEquivalent,
                operation.ExpenseCategory,
                operation.ExpenseComment
            };

            return JsonSerializer.Serialize(payload);
        }

        private double CalculateExchangeAmount(OperationDraft draft)
        {
            var converted = draft.SourceAmount * draft.Rate;
            if (draft.Commission is { } commission && commission > 0)
            {
                converted -= commission;
            }

            return Math.Max(0, converted);
        }

        private double CalculateUsdEquivalent(OperationDraft draft, double targetAmount)
        {
            var rates = GetUsdRates();
            double GetRate(Currency c) => rates.TryGetValue(c, out var r) ? r : 0;

            return draft.Type switch
            {
                OperationType.Income => draft.SourceCurrency == Currency.USD ? draft.SourceAmount : draft.SourceAmount * draft.Rate,
                OperationType.Expense => draft.SourceCurrency == Currency.USD ? draft.SourceAmount : draft.SourceAmount * draft.Rate,
                OperationType.Exchange => draft.TargetCurrency switch
                {
                    Currency.USD => targetAmount,
                    _ when draft.SourceCurrency == Currency.USD => draft.SourceAmount,
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

            return new Operation
            {
                Id = reader.GetInt64(0),
                Date = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                Type = Enum.Parse<OperationType>(reader.GetString(2)),
                SourceCurrency = Enum.Parse<Currency>(reader.GetString(3)),
                SourceAmount = reader.GetDouble(4),
                TargetCurrency = Enum.Parse<Currency>(reader.GetString(5)),
                TargetAmount = reader.GetDouble(6),
                Rate = reader.GetDouble(7),
                Commission = reader.IsDBNull(8) ? null : reader.GetDouble(8),
                UsdEquivalent = reader.GetDouble(9),
                ExpenseCategory = reader.IsDBNull(10) ? null : reader.GetString(10),
                ExpenseComment = reader.IsDBNull(11) ? null : reader.GetString(11)
            };
        }

        private static double GetBalance(SqliteConnection connection, SqliteTransaction transaction, Currency currency)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT Amount FROM Balances WHERE Currency = $currency";
            command.Parameters.AddWithValue("$currency", currency.ToString());
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
    }
}
