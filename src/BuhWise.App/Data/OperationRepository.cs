using System;
using System.Collections.Generic;
using System.Globalization;
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

        public List<Operation> GetOperations()
        {
            var operations = new List<Operation>();
            using var connection = _database.GetConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT Id, Date, Type, SourceCurrency, SourceAmount, TargetCurrency, TargetAmount, Rate, Commission, UsdEquivalent FROM Operations ORDER BY Date DESC, Id DESC";

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
                    UsdEquivalent = reader.GetDouble(9)
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

            InsertOperation(operation, connection, transaction);
            UpdateBalances(operation, connection, transaction);
            UpdateRates(operation, connection, transaction);

            transaction.Commit();
            return operation;
        }

        private static void InsertOperation(Operation operation, SqliteConnection connection, SqliteTransaction transaction)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"INSERT INTO Operations (Date, Type, SourceCurrency, SourceAmount, TargetCurrency, TargetAmount, Rate, Commission, UsdEquivalent)
                                    VALUES ($date, $type, $sourceCurrency, $sourceAmount, $targetCurrency, $targetAmount, $rate, $commission, $usdEquivalent);
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

            var id = (long)(command.ExecuteScalar() ?? 0L);
            operation.Id = id;
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

        private void UpdateRates(Operation operation, SqliteConnection connection, SqliteTransaction transaction)
        {
            // Persist last-known USD-cross rates to simplify cross-currency USD equivalents.
            void UpsertRate(Currency currency, double rateToUsd)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
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
                UsdEquivalent = usdEquivalent
            };
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
    }
}
