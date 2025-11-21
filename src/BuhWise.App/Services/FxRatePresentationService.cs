using System;
using BuhWise.Data;

namespace BuhWise.Services
{
    public enum FxRateDisplayMode
    {
        Direct,
        Inverted
    }

    public interface IFxRatePresentationService
    {
        FxRateDisplayMode GetDisplayMode(string fromCurrency, string toCurrency);
        double ToDisplayRate(double internalRate, string fromCurrency, string toCurrency);
        double ToInternalRate(double displayRate, string fromCurrency, string toCurrency);
        void SetDisplayMode(string fromCurrency, string toCurrency, FxRateDisplayMode mode);
    }

    public class FxRatePresentationService : IFxRatePresentationService
    {
        private readonly DatabaseService _database;

        public FxRatePresentationService(DatabaseService database)
        {
            _database = database;
        }

        public FxRateDisplayMode GetDisplayMode(string fromCurrency, string toCurrency)
        {
            if (string.IsNullOrWhiteSpace(fromCurrency) || string.IsNullOrWhiteSpace(toCurrency))
            {
                return FxRateDisplayMode.Direct;
            }

            using var connection = _database.GetConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"SELECT DisplayMode FROM FxRateDisplayConfig
                                    WHERE FromCurrencyCode = $from AND ToCurrencyCode = $to";
            command.Parameters.AddWithValue("$from", fromCurrency);
            command.Parameters.AddWithValue("$to", toCurrency);

            var result = command.ExecuteScalar();
            if (result is string text && Enum.TryParse(text, out FxRateDisplayMode parsed))
            {
                return parsed;
            }

            return FxRateDisplayMode.Direct;
        }

        public void SetDisplayMode(string fromCurrency, string toCurrency, FxRateDisplayMode mode)
        {
            if (string.IsNullOrWhiteSpace(fromCurrency) || string.IsNullOrWhiteSpace(toCurrency))
            {
                return;
            }

            using var connection = _database.GetConnection();
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = @"INSERT OR REPLACE INTO FxRateDisplayConfig (FromCurrencyCode, ToCurrencyCode, DisplayMode, UpdatedAt)
                                    VALUES ($from, $to, $mode, $updated)";
            command.Parameters.AddWithValue("$from", fromCurrency);
            command.Parameters.AddWithValue("$to", toCurrency);
            command.Parameters.AddWithValue("$mode", mode.ToString());
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("o"));
            command.ExecuteNonQuery();
        }

        public double ToDisplayRate(double internalRate, string fromCurrency, string toCurrency)
        {
            if (internalRate <= 0)
            {
                return 0;
            }

            var mode = GetDisplayMode(fromCurrency, toCurrency);
            return mode == FxRateDisplayMode.Inverted ? 1d / internalRate : internalRate;
        }

        public double ToInternalRate(double displayRate, string fromCurrency, string toCurrency)
        {
            if (displayRate <= 0)
            {
                throw new InvalidOperationException("Курс должен быть больше нуля");
            }

            var mode = GetDisplayMode(fromCurrency, toCurrency);
            return mode == FxRateDisplayMode.Inverted ? 1d / displayRate : displayRate;
        }
    }
}
