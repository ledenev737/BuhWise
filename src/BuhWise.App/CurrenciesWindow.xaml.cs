using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using BuhWise.Data;
using BuhWise.Models;

namespace BuhWise
{
    public partial class CurrenciesWindow : Window
    {
        private readonly OperationRepository _repository;
        private readonly ObservableCollection<Currency> _currencies = new();

        public CurrenciesWindow(OperationRepository repository)
        {
            InitializeComponent();
            _repository = repository;
            Loaded += CurrenciesWindow_Loaded;
        }

        private void CurrenciesWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Reload();
        }

        private void Reload()
        {
            _currencies.Clear();
            foreach (var currency in _repository.GetCurrencies(false))
            {
                _currencies.Add(currency);
            }

            CurrencyGrid.ItemsSource = _currencies;
        }

        private void AddCurrency_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new CurrencyDialog { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                var currency = new Currency
                {
                    Code = dialog.Code,
                    Name = dialog.CurrencyName,
                    IsActive = dialog.IsActive
                };

                _repository.AddCurrency(currency);
                Reload();
            }
        }

        private void EditCurrency_Click(object sender, RoutedEventArgs e)
        {
            if (CurrencyGrid.SelectedItem is not Currency selected)
            {
                return;
            }

            var dialog = new CurrencyDialog
            {
                Owner = this,
                Code = selected.Code,
                CurrencyName = selected.Name,
                IsActive = selected.IsActive,
                IsCodeReadOnly = true
            };

            if (dialog.ShowDialog() == true)
            {
                selected.Name = dialog.CurrencyName;
                selected.IsActive = dialog.IsActive;
                _repository.UpdateCurrency(selected);
                Reload();
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
