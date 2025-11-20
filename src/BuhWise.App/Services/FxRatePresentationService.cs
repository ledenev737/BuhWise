using System;
using System.Collections.Generic;
using BuhWise.Models;

namespace BuhWise.Services
{
    public enum FxRateDisplayMode
    {
        Direct,
        Inverted
    }

    public interface IFxRatePresentationService
    {
        FxRateDisplayMode GetDisplayMode(Currency fromCurrency, Currency toCurrency);

        double ToDisplayRate(double internalRate, Currency fromCurrency, Currency toCurrency);

        double ToInternalRate(double displayRate, Currency fromCurrency, Currency toCurrency);
    }

    public class FxRatePresentationService : IFxRatePresentationService
    {
        private readonly HashSet<(Currency From, Currency To)> _invertedPairs = new()
        {
            (Currency.RUB, Currency.USD)
        };

        public FxRateDisplayMode GetDisplayMode(Currency fromCurrency, Currency toCurrency)
        {
            return _invertedPairs.Contains((fromCurrency, toCurrency))
                ? FxRateDisplayMode.Inverted
                : FxRateDisplayMode.Direct;
        }

        public double ToDisplayRate(double internalRate, Currency fromCurrency, Currency toCurrency)
        {
            if (internalRate <= 0)
            {
                return 0;
            }

            var mode = GetDisplayMode(fromCurrency, toCurrency);
            return mode == FxRateDisplayMode.Inverted
                ? 1d / internalRate
                : internalRate;
        }

        public double ToInternalRate(double displayRate, Currency fromCurrency, Currency toCurrency)
        {
            if (displayRate <= 0)
            {
                throw new InvalidOperationException("Курс должен быть больше нуля");
            }

            var mode = GetDisplayMode(fromCurrency, toCurrency);
            return mode == FxRateDisplayMode.Inverted
                ? 1d / displayRate
                : displayRate;
        }
    }
}
