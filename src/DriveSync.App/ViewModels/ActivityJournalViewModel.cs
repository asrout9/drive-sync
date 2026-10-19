using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DriveSync.Core.Models;
using DriveSync.Core.Storage;
using DriveSync.Core.Versioning;

namespace DriveSync.App.ViewModels;

public class ActivityJournalViewModel : ViewModelBase
{
    private readonly IJournalStore _journalStore;
    private readonly IVersionManager _versionManager;
    private readonly ISyncStateStore _stateStore;

    private string _searchQuery = string.Empty;
    private SyncActionType? _filterAction;
    private bool _isBusy;
    private string _statusMessage = string.Empty;

    public ObservableCollection<SyncOperation> Operations { get; } = new();
    public ObservableCollection<DailySyncSummary> DailySummaries { get; } = new();

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                _ = LoadOperationsAsync();
            }
        }
    }

    public SyncActionType? FilterAction
    {
        get => _filterAction;
        set
        {
            if (SetProperty(ref _filterAction, value))
            {
                _ = LoadOperationsAsync();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public ICommand UndoOperationCommand { get; }
    public ICommand ExportReportCommand { get; }
    public ICommand RefreshCommand { get; }

    public ActivityJournalViewModel(
        IJournalStore journalStore,
        IVersionManager versionManager,
        ISyncStateStore stateStore)
    {
        _journalStore = journalStore;
        _versionManager = versionManager;
        _stateStore = stateStore;

        UndoOperationCommand = new RelayCommand<SyncOperation>(async op => await UndoOperationAsync(op));
        ExportReportCommand = new RelayCommand(async () => await ExportReportAsync());
        RefreshCommand = new RelayCommand(async () => await LoadDataAsync());

        _ = LoadDataAsync();
    }

    public async Task LoadDataAsync()
    {
        await LoadSummariesAsync();
        await LoadOperationsAsync();
    }

    private async Task LoadSummariesAsync()
    {
        try
        {
            var summaries = await _journalStore.GetDailySummariesAsync(days: 14);
            DailySummaries.Clear();
            foreach (var s in summaries) DailySummaries.Add(s);
        }
        catch { }
    }

    public async Task LoadOperationsAsync()
    {
        IsBusy = true;
        try
        {
            var filter = new JournalFilter
            {
                SearchQuery = string.IsNullOrWhiteSpace(SearchQuery) ? null : SearchQuery,
                ActionType = FilterAction,
                Limit = 300
            };

            var ops = await _journalStore.QueryOperationsAsync(filter);
            Operations.Clear();
            foreach (var op in ops) Operations.Add(op);
            StatusMessage = $"Displaying {Operations.Count} operations";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading journal: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UndoOperationAsync(SyncOperation? operation)
    {
        if (operation == null) return;

        if (operation.WasUndone)
        {
            MessageBox.Show("This sync operation has already been undone.", "Already Undone", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var pair = await _stateStore.GetPairAsync(operation.PairId);
        if (pair == null)
        {
            MessageBox.Show("Could not find sync pair definition for this operation.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        string root = operation.TargetSide == EndpointSide.Destination ? pair.Destination.RootPath : pair.Source.RootPath;

        // Safety verify that the file was not modified after synchronization!
        bool canUndo = await _versionManager.CanUndoOperationAsync(root, operation);
        if (!canUndo)
        {
            MessageBox.Show(
                $"Safety Lock Triggered:\n\nThe item at '{operation.RelativePath}' has been modified by you or another process since this sync operation took place, or the backup archive has been purged.\n\nDriveSync will not silently overwrite external changes.",
                "Cannot Undo — Modified Externally",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show(
            $"Are you sure you want to undo the '{operation.ActionType}' operation on '{operation.RelativePath}'?",
            "Confirm Undo Operation",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            await _versionManager.UndoOperationAsync(root, operation);
            await _journalStore.MarkOperationUndoneAsync(operation.Id);
            operation.WasUndone = true;
            operation.Status = OperationStatus.Undone;
            await LoadOperationsAsync();
            MessageBox.Show($"Operation '{operation.ActionType}' on '{operation.RelativePath}' successfully undone!", "Undo Completed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Undo failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ExportReportAsync()
    {
        try
        {
            using var sfd = new System.Windows.Forms.SaveFileDialog
            {
                Filter = "CSV Report (*.csv)|*.csv|All Files (*.*)|*.*",
                FileName = $"DriveSync_Audit_Report_{DateTime.Now:yyyyMMdd_HHmm}.csv",
                Title = "Export Sanitized Sync Audit Report"
            };

            if (sfd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string csv = await _journalStore.ExportSanitizedReportAsync(new JournalFilter { Limit = 5000 });
                await File.WriteAllTextAsync(sfd.FileName, csv);
                MessageBox.Show($"Sanitized audit report exported successfully to:\n{sfd.FileName}", "Export Completed", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Export error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
