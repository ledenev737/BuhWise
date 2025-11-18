using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using BuhWise.Data;
using BuhWise.Models;

namespace BuhWise
{
    public partial class MainWindow : Window
    {
        private readonly OperationRepository _repository;
        private readonly ObservableCollection<Operation> _operations = new();

        public MainWindow()
        {
            InitializeComponent();
            var dbPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "buhwise.db");
            _repository = new OperationRepository(new DatabaseService(dbPath));

            UpdateFieldStates();
            LoadOperations();
            RefreshBalances();
        }

        private void LoadOperations()
        {
            _operations.Clear();
            foreach (var op in _repository.GetOperations())
            {
                _operations.Add(op);
            }

            OperationsGrid.ItemsSource = _operations;
        }

        private void RefreshBalances()
        {
            var balances = _repository.GetBalances();
            UsdBalance.Text = balances.TryGetValue(Currency.USD, out var usd) ? usd.ToString("F2") : "0";
            EurBalance.Text = balances.TryGetValue(Currency.EUR, out var eur) ? eur.ToString("F2") : "0";
            RubBalance.Text = balances.TryGetValue(Currency.RUB, out var rub) ? rub.ToString("F2") : "0";
        }

        private void AddOperation_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var type = ParseOperationType(OperationTypeBox);
                var sourceCurrency = ParseCurrency(SourceCurrencyBox);
                var isExchange = type == OperationType.Exchange;
                var targetCurrency = isExchange ? ParseCurrency(TargetCurrencyBox) : sourceCurrency;
                var amount = ParseDouble(AmountBox.Text, "сумма");
                var rate = isExchange ? ParseDouble(RateBox.Text, "курс") : GetCachedRateOrDefault(sourceCurrency);
                var commission = isExchange && !string.IsNullOrWhiteSpace(CommissionBox.Text)
                    ? ParseDouble(CommissionBox.Text, "комиссия")
                    : (double?)null;
                var date = DateBox.SelectedDate ?? DateTime.Today;

                var draft = new OperationDraft
                {
                    Date = date,
                    Type = type,
                    SourceCurrency = sourceCurrency,
                    TargetCurrency = targetCurrency,
                    SourceAmount = amount,
                    Rate = rate,
                    Commission = commission
                };

                var operation = _repository.AddOperation(draft);
                _operations.Insert(0, operation);
                RefreshBalances();
                ClearInputs();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private double GetCachedRateOrDefault(Currency sourceCurrency)
        {
            if (sourceCurrency == Currency.USD)
            {
                return 1d;
            }

            var cached = _repository.GetUsdRates();
            return cached.TryGetValue(sourceCurrency, out var rate) ? rate : 0d;
        }

        private static Currency ParseCurrency(ComboBox combo)
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                return Enum.Parse<Currency>(tag);
            }

            throw new InvalidOperationException("Выберите валюту");
        }

        private static OperationType ParseOperationType(ComboBox combo)
        {
            var item = combo.SelectedItem as ComboBoxItem;
            if (item?.Tag is string tag)
            {
                return Enum.Parse<OperationType>(tag);
            }

            throw new InvalidOperationException("Выберите тип операции");
        }

        private static double ParseDouble(string input, string fieldName)
        {
            if (!double.TryParse(input.Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                throw new InvalidOperationException($"Некорректное значение поля \"{fieldName}\"");
            }

            return value;
        }

        private void ClearInputs()
        {
            AmountBox.Text = string.Empty;
            RateBox.Text = string.Empty;
            CommissionBox.Text = string.Empty;
            DateBox.SelectedDate = DateTime.Today;
            UpdateFieldStates();
        }

        private void OperationTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateFieldStates();
        }

        private void UpdateFieldStates()
        {
            var isExchange = GetSelectedOperationType() == OperationType.Exchange;

            TargetCurrencyBox.IsEnabled = isExchange;
            RateBox.IsEnabled = isExchange;
            CommissionBox.IsEnabled = isExchange;
        }

        private OperationType GetSelectedOperationType()
        {
            if (OperationTypeBox.SelectedItem is ComboBoxItem item && item.Tag is string tag && Enum.TryParse(tag, out OperationType parsed))
            {
                return parsed;
            }

            return OperationType.Income;
        }
    }
}
