using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
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
        private readonly Dictionary<Currency, double> _balanceCache = new();

        public MainWindow()
        {
            InitializeComponent();
            var dbPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "buhwise.db");
            _repository = new OperationRepository(new DatabaseService(dbPath));

            UpdateFieldStates();
            LoadOperations();
            RefreshBalances();
            UpdateDeleteButtonState();
        }

        private void LoadOperations()
        {
            _operations.Clear();
            foreach (var op in _repository.GetOperations())
            {
                _operations.Add(op);
            }

            OperationsGrid.ItemsSource = _operations;
            OperationsGrid.SelectedItem = null;
        }

        private void RefreshBalances()
        {
            var balances = _repository.GetBalances();
            _balanceCache.Clear();
            foreach (var entry in balances)
            {
                _balanceCache[entry.Key] = entry.Value;
            }

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
                var isExpense = type == OperationType.Expense;
                var targetCurrency = isExchange ? ParseCurrency(TargetCurrencyBox) : sourceCurrency;
                var amount = ParseDouble(AmountBox.Text, "сумма");
                var rate = isExchange ? ParseDouble(RateBox.Text, "курс") : GetCachedRateOrDefault(sourceCurrency);
                var commission = isExchange && !string.IsNullOrWhiteSpace(FeeBox.Text)
                    ? ParseDouble(FeeBox.Text, "комиссия")
                    : (double?)null;
                var date = DateBox.SelectedDate ?? DateTime.Today;
                string? expenseCategory = null;
                string? expenseComment = null;

                if (isExpense)
                {
                    expenseCategory = GetSelectedExpenseCategory();
                    if (string.IsNullOrWhiteSpace(expenseCategory))
                    {
                        throw new InvalidOperationException("Выберите категорию расхода");
                    }

                    expenseComment = string.IsNullOrWhiteSpace(ExpenseCommentBox.Text)
                        ? null
                        : ExpenseCommentBox.Text.Trim();
                }

                if (isExchange)
                {
                    var available = GetAvailableBalance(sourceCurrency);
                    if (available <= 0)
                    {
                        throw new InvalidOperationException("Недостаточно средств для обмена в выбранной валюте");
                    }

                    if (amount > available)
                    {
                        amount = Math.Round(available, 2);
                        AmountBox.Text = amount.ToString("F2", CultureInfo.InvariantCulture);
                        MessageBox.Show(
                            "Сумма обмена была уменьшена до доступного остатка",
                            "Ограничение",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                }

                var draft = new OperationDraft
                {
                    Date = date,
                    Type = type,
                    SourceCurrency = sourceCurrency,
                    TargetCurrency = targetCurrency,
                    SourceAmount = amount,
                    Rate = rate,
                    Commission = commission,
                    ExpenseCategory = expenseCategory,
                    ExpenseComment = expenseComment
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
            FeeBox.Text = string.Empty;
            DateBox.SelectedDate = DateTime.Today;
            ExpenseCategoryBox.SelectedIndex = -1;
            ExpenseCommentBox.Text = string.Empty;
            UpdateFieldStates();
        }

        private void OperationTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateFieldStates();
        }

        private void OperationsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateDeleteButtonState();
        }

        private void DeleteOperation_Click(object sender, RoutedEventArgs e)
        {
            if (OperationsGrid.SelectedItem is not Operation selected)
            {
                return;
            }

            var result = MessageBox.Show(
                "Вы уверены, что хотите удалить выбранную операцию?",
                "Подтверждение",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            var reasonDialog = new InputDialog { Owner = this };
            if (reasonDialog.ShowDialog() != true)
            {
                return;
            }

            var reason = string.IsNullOrWhiteSpace(reasonDialog.Result) ? null : reasonDialog.Result;

            try
            {
                _repository.DeleteOperation(selected.Id, reason);
                LoadOperations();
                RefreshBalances();
                UpdateDeleteButtonState();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenHistory_Click(object sender, RoutedEventArgs e)
        {
            var history = new HistoryWindow(_repository) { Owner = this };
            history.ShowDialog();
        }

        private void UpdateFieldStates()
        {
            var type = GetSelectedOperationType();
            var isExchange = type == OperationType.Exchange;
            var isExpense = type == OperationType.Expense;

            TargetCurrencyBox.IsEnabled = isExchange;
            RateBox.IsEnabled = isExchange;
            FeeBox.IsEnabled = isExchange;
            MaxAmountButton.Visibility = isExchange ? Visibility.Visible : Visibility.Collapsed;

            ExpenseCategoryPanel.Visibility = isExpense ? Visibility.Visible : Visibility.Collapsed;
            ExpenseCommentPanel.Visibility = isExpense ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateDeleteButtonState()
        {
            if (DeleteOperationButton != null)
            {
                DeleteOperationButton.IsEnabled = OperationsGrid?.SelectedItem is Operation;
            }
        }

        private OperationType GetSelectedOperationType()
        {
            if (OperationTypeBox.SelectedItem is ComboBoxItem item && item.Tag is string tag && Enum.TryParse(tag, out OperationType parsed))
            {
                return parsed;
            }

            return OperationType.Income;
        }

        private string? GetSelectedExpenseCategory()
        {
            if (ExpenseCategoryBox.SelectedItem is ComboBoxItem item)
            {
                return item.Content?.ToString();
            }

            return null;
        }

        private double GetAvailableBalance(Currency currency)
        {
            return _balanceCache.TryGetValue(currency, out var value) ? value : 0d;
        }

        private void MaxAmountButton_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedOperationType() != OperationType.Exchange)
            {
                return;
            }

            try
            {
                var sourceCurrency = ParseCurrency(SourceCurrencyBox);
                var available = GetAvailableBalance(sourceCurrency);
                if (available <= 0)
                {
                    MessageBox.Show("Недостаточно средств в выбранной валюте", "Нет средств", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                AmountBox.Text = available.ToString("F2", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
