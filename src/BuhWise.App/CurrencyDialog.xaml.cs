using System;
using System.Windows;

namespace BuhWise
{
    public partial class CurrencyDialog : Window
    {
        public string Code { get; set; } = string.Empty;
        public string CurrencyName { get; set; } = string.Empty;
        public bool IsActive { get; set; } = true;
        public bool IsCodeReadOnly { get; set; }

        public CurrencyDialog()
        {
            InitializeComponent();
            Loaded += CurrencyDialog_Loaded;
        }

        private void CurrencyDialog_Loaded(object sender, RoutedEventArgs e)
        {
            CodeBox.Text = Code;
            NameBox.Text = CurrencyName;
            ActiveBox.IsChecked = IsActive;
            CodeBox.IsReadOnly = IsCodeReadOnly;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CodeBox.Text))
            {
                MessageBox.Show("Укажите код валюты", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                MessageBox.Show("Укажите название валюты", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            Code = CodeBox.Text.Trim().ToUpperInvariant();
            CurrencyName = NameBox.Text.Trim();
            IsActive = ActiveBox.IsChecked ?? true;

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
