using System.Collections.ObjectModel;
using System.Windows;
using BuhWise.Data;
using BuhWise.Models;
using System;

namespace BuhWise
{
    public partial class HistoryWindow : Window
    {
        private readonly OperationRepository _repository;
        private readonly ObservableCollection<OperationChange> _changes = new();

        public event EventHandler? OperationRestored;

        public HistoryWindow(OperationRepository repository)
        {
            InitializeComponent();
            _repository = repository;
            Loaded += HistoryWindow_Loaded;
        }

        private void HistoryWindow_Loaded(object sender, RoutedEventArgs e)
        {
            HistoryGrid.ItemsSource = _changes;
            LoadHistory();
            UpdateRestoreButtonState();
        }

        private void LoadHistory()
        {
            _changes.Clear();
            foreach (var change in _repository.GetOperationChanges())
            {
                _changes.Add(change);
            }
        }

        private void HistoryGrid_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateRestoreButtonState();
        }

        private void UpdateRestoreButtonState()
        {
            if (RestoreButton == null)
            {
                return;
            }

            var selected = HistoryGrid?.SelectedItem as OperationChange;
            RestoreButton.IsEnabled = selected != null && string.Equals(selected.Action, "Delete", StringComparison.OrdinalIgnoreCase);
        }

        private void RestoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (HistoryGrid?.SelectedItem is not OperationChange change || !string.Equals(change.Action, "Delete", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var confirm = MessageBox.Show(
                "Восстановить выбранную операцию и пересчитать балансы?",
                "Подтверждение",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                _repository.RestoreOperationFromChange(change);
                LoadHistory();
                UpdateRestoreButtonState();
                OperationRestored?.Invoke(this, EventArgs.Empty);
                MessageBox.Show("Операция восстановлена.", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
