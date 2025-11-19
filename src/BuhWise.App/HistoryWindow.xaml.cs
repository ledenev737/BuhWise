using System.Collections.ObjectModel;
using System.Windows;
using BuhWise.Data;
using BuhWise.Models;

namespace BuhWise
{
    public partial class HistoryWindow : Window
    {
        private readonly OperationRepository _repository;
        private readonly ObservableCollection<OperationChange> _changes = new();

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
        }

        private void LoadHistory()
        {
            _changes.Clear();
            foreach (var change in _repository.GetOperationChanges())
            {
                _changes.Add(change);
            }
        }
    }
}
