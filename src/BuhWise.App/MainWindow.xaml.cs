using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BuhWise.Data;
using BuhWise.Models;
using BuhWise.Services;
using Microsoft.Win32;

namespace BuhWise
{
    public partial class MainWindow : Window
    {
        private readonly OperationRepository _repository;
        private readonly ObservableCollection<Operation> _operations = new();
        private readonly Dictionary<string, double> _balanceCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly SpreadsheetService _spreadsheetService = new();
        private readonly IFxRatePresentationService _ratePresentationService;
        private readonly DatabaseService _database;
        private string? _currentRatePairKey;
        private bool _rateEditedByUser;
        private bool _suppressRateTextChange;

        public MainWindow()
        {
            InitializeComponent();

            var dbPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "buhwise.db");
            _database = new DatabaseService(dbPath);
            _repository = new OperationRepository(_database);
            _ratePresentationService = new FxRatePresentationService(_database);

            Loaded += MainWindow_Loaded;

            if (RateBox != null)
            {
                RateBox.TextChanged += RateBox_TextChanged;
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadCurrencies();
            LoadOperations();
            RefreshBalances();
            UpdateFieldStates();
            MaybePrefillRateFromMemory();
            UpdateDeleteButtonState();
        }

        private void LoadCurrencies()
        {
            var currencies = _repository.GetCurrencies().ToList();
            SourceCurrencyBox.ItemsSource = currencies;
            TargetCurrencyBox.ItemsSource = currencies;

            if (SourceCurrencyBox.SelectedItem == null && currencies.Count > 0)
            {
                SourceCurrencyBox.SelectedIndex = 0;
            }

            if (TargetCurrencyBox.SelectedItem == null && currencies.Count > 1)
            {
                TargetCurrencyBox.SelectedIndex = 1;
            }
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

            UsdBalance.Text = balances.TryGetValue("USD", out var usd) ? usd.ToString("F2") : "0";
            EurBalance.Text = balances.TryGetValue("EUR", out var eur) ? eur.ToString("F2") : "0";
            RubBalance.Text = balances.TryGetValue("RUB", out var rub) ? rub.ToString("F2") : "0";
        }

        private void AddOperation_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var type = ParseOperationType(OperationTypeBox);
                var sourceCurrency = GetSelectedCurrencyCode(SourceCurrencyBox);
                var isExchange = type == OperationType.Exchange;
                var isExpense = type == OperationType.Expense;
                var targetCurrency = isExchange ? GetSelectedCurrencyCode(TargetCurrencyBox) : sourceCurrency;
                var amount = ParseDouble(AmountBox.Text, "сумма");
                var rateInput = isExchange ? ParseDouble(RateBox.Text, "курс") : GetCachedRateOrDefault(sourceCurrency);
                var normalizedRate = isExchange
                    ? _ratePresentationService.ToInternalRate(rateInput, sourceCurrency, targetCurrency)
                    : rateInput;
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
                    Rate = normalizedRate,
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

        private double GetCachedRateOrDefault(string sourceCurrency)
        {
            if (string.Equals(sourceCurrency, "USD", StringComparison.OrdinalIgnoreCase))
            {
                return 1d;
            }

            var cached = _repository.GetUsdRates();
            return cached.TryGetValue(sourceCurrency, out var rate) ? rate : 0d;
        }

        private static string GetSelectedCurrencyCode(ComboBox combo)
        {
            if (combo.SelectedItem is Currency currency)
            {
                return currency.Code;
            }

            if (combo.SelectedValue is string code && !string.IsNullOrWhiteSpace(code))
            {
                return code;
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
            if (!double.TryParse(input.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
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
            _currentRatePairKey = null;
            _rateEditedByUser = false;
            UpdateFieldStates();
        }

        private void OperationTypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            UpdateFieldStates();
            MaybePrefillRateFromMemory();
        }

        private void CurrencyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            UpdateFieldStates();
            MaybePrefillRateFromMemory();
        }

        private void OperationsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

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
            history.OperationRestored += (_, _) =>
            {
                LoadOperations();
                RefreshBalances();
            };
            history.ShowDialog();
        }

        private void OpenCurrencies_Click(object sender, RoutedEventArgs e)
        {
            var window = new CurrenciesWindow(_repository) { Owner = this };
            window.ShowDialog();
            LoadCurrencies();
        }

        private void UpdateFieldStates()
        {
            if (!IsLoaded)
            {
                return;
            }

            var type = GetSelectedOperationType();
            var isExchange = type == OperationType.Exchange;
            var isExpense = type == OperationType.Expense;
            var hasSource = TryParseCurrency(SourceCurrencyBox, out var sourceCurrency);
            var hasTarget = TryParseCurrency(TargetCurrencyBox, out var targetCurrency);
            var mode = isExchange && hasSource && hasTarget
                ? _ratePresentationService.GetDisplayMode(sourceCurrency!, targetCurrency!)
                : FxRateDisplayMode.Direct;

            if (TargetCurrencyBox != null)
            {
                TargetCurrencyBox.IsEnabled = isExchange;
            }

            if (RateBox != null)
            {
                RateBox.IsEnabled = isExchange;
            }

            if (FeeBox != null)
            {
                FeeBox.IsEnabled = isExchange;
            }

            if (MaxAmountButton != null)
            {
                MaxAmountButton.Visibility = isExchange ? Visibility.Visible : Visibility.Collapsed;
            }

            if (InvertToggle != null)
            {
                InvertToggle.Visibility = isExchange ? Visibility.Visible : Visibility.Collapsed;
                InvertToggle.IsEnabled = isExchange && hasSource && hasTarget;
                InvertToggle.IsChecked = mode == FxRateDisplayMode.Inverted;
            }

            if (ExpenseCategoryPanel != null)
            {
                ExpenseCategoryPanel.Visibility = isExpense ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ExpenseCommentPanel != null)
            {
                ExpenseCommentPanel.Visibility = isExpense ? Visibility.Visible : Visibility.Collapsed;
            }

            if (RateLabel != null)
            {
                if (isExchange && hasSource && hasTarget)
                {
                    var label = mode == FxRateDisplayMode.Inverted
                        ? $"Курс ({targetCurrency}/{sourceCurrency})"
                        : "Курс";
                    RateLabel.Text = label;
                }
                else
                {
                    RateLabel.Text = "Курс";
                }
            }

            if (!isExchange)
            {
                _currentRatePairKey = null;
                _rateEditedByUser = false;
            }
        }

        private void UpdateDeleteButtonState()
        {
            if (!IsLoaded)
            {
                return;
            }

            if (DeleteOperationButton != null)
            {
                DeleteOperationButton.IsEnabled = OperationsGrid?.SelectedItem is Operation;
            }
        }

        private OperationType GetSelectedOperationType()
        {
            if (OperationTypeBox?.SelectedItem is ComboBoxItem item && item.Tag is string tag && Enum.TryParse(tag, out OperationType parsed))
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

        private double GetAvailableBalance(string currency)
        {
            return _balanceCache.TryGetValue(currency, out var value) ? value : 0d;
        }

        private void RateBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            if (_suppressRateTextChange)
            {
                return;
            }

            _rateEditedByUser = true;
        }

        private void MaybePrefillRateFromMemory()
        {
            if (!IsLoaded)
            {
                return;
            }

            if (RateBox == null)
            {
                return;
            }

            var type = GetSelectedOperationType();
            if (type != OperationType.Exchange)
            {
                return;
            }

            if (!TryParseCurrency(SourceCurrencyBox, out var source) || !TryParseCurrency(TargetCurrencyBox, out var target))
            {
                return;
            }

            var pairKey = $"{source}-{target}";
            var pairChanged = _currentRatePairKey != pairKey;
            if (pairChanged)
            {
                _currentRatePairKey = pairKey;
                _rateEditedByUser = false;
            }

            if (_rateEditedByUser)
            {
                return;
            }

            var lastRate = _repository.GetLastPairRate(source!, target!);
            _suppressRateTextChange = true;
            if (lastRate.HasValue)
            {
                var displayRate = _ratePresentationService.ToDisplayRate(lastRate.Value, source!, target!);
                RateBox.Text = displayRate > 0
                    ? displayRate.ToString("F4", CultureInfo.InvariantCulture)
                    : string.Empty;
            }
            else
            {
                RateBox.Text = string.Empty;
            }

            _suppressRateTextChange = false;
            UpdateFieldStates();
        }

        private void MaxAmountButton_Click(object sender, RoutedEventArgs e)
        {
            if (GetSelectedOperationType() != OperationType.Exchange)
            {
                return;
            }

            try
            {
                var sourceCurrency = GetSelectedCurrencyCode(SourceCurrencyBox);
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

        private bool TryParseCurrency(ComboBox combo, out string? currency)
        {
            currency = null;

            if (combo?.SelectedItem is Currency item)
            {
                currency = item.Code;
                return true;
            }

            if (combo?.SelectedValue is string code && !string.IsNullOrWhiteSpace(code))
            {
                currency = code;
                return true;
            }

            return false;
        }

        private void ExportToXlsx_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "Excel (*.xlsx)|*.xlsx",
                DefaultExt = ".xlsx",
                FileName = $"transactions_{DateTime.Today:yyyy-MM-dd}.xlsx"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                var operations = _repository.GetOperations();
                _spreadsheetService.ExportOperations(dialog.FileName, operations);
                MessageBox.Show("Экспорт завершён", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка экспорта", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ImportFromXlsx_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Excel (*.xlsx)|*.xlsx",
                DefaultExt = ".xlsx"
            };

            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var confirm = MessageBox.Show(
                "Текущие операции будут заменены данными из файла. Продолжить?",
                "Подтверждение",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                var imported = _spreadsheetService.ImportOperations(dialog.FileName);
                _repository.ReplaceAllOperations(imported);
                LoadOperations();
                RefreshBalances();
                UpdateDeleteButtonState();
                MessageBox.Show("Импорт завершён", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка импорта", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void InvertToggle_Click(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            if (GetSelectedOperationType() != OperationType.Exchange)
            {
                return;
            }

            if (!TryParseCurrency(SourceCurrencyBox, out var source) || !TryParseCurrency(TargetCurrencyBox, out var target))
            {
                return;
            }

            var currentMode = _ratePresentationService.GetDisplayMode(source!, target!);
            var newMode = currentMode == FxRateDisplayMode.Direct ? FxRateDisplayMode.Inverted : FxRateDisplayMode.Direct;

            double? parsedDisplay = null;
            if (double.TryParse(RateBox.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                parsedDisplay = parsed;
            }

            if (parsedDisplay.HasValue && parsedDisplay.Value > 0)
            {
                var internalRate = _ratePresentationService.ToInternalRate(parsedDisplay.Value, source!, target!);
                var newDisplay = newMode == FxRateDisplayMode.Inverted ? 1d / internalRate : internalRate;
                _suppressRateTextChange = true;
                RateBox.Text = newDisplay.ToString("F4", CultureInfo.InvariantCulture);
                _suppressRateTextChange = false;
            }

            _ratePresentationService.SetDisplayMode(source!, target!, newMode);
            InvertToggle.IsChecked = newMode == FxRateDisplayMode.Inverted;
            UpdateFieldStates();
        }
    }
}
