using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DriveSync.App.Services;
using DriveSync.Core.Models;

namespace DriveSync.App.ViewModels;

public class SyncPairsViewModel : ViewModelBase
{
    private readonly SyncCoordinatorService _coordinator;
    private SyncPair? _selectedPair;
    private string _statusMessage = string.Empty;
    private bool _isBusy;
    private double _currentProgress;
    private string _progressText = string.Empty;
    private bool _isIndeterminateProgress = true;
    private string _progressPercentageText = string.Empty;
    private string _activeSyncingPairName = string.Empty;

    public ObservableCollection<SyncPair> Pairs { get; } = new();

    public SyncPair? SelectedPair
    {
        get => _selectedPair;
        set => SetProperty(ref _selectedPair, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public double CurrentProgress
    {
        get => _currentProgress;
        set => SetProperty(ref _currentProgress, value);
    }

    public string ProgressText
    {
        get => _progressText;
        set => SetProperty(ref _progressText, value);
    }

    public bool IsIndeterminateProgress
    {
        get => _isIndeterminateProgress;
        set => SetProperty(ref _isIndeterminateProgress, value);
    }

    public string ProgressPercentageText
    {
        get => _progressPercentageText;
        set => SetProperty(ref _progressPercentageText, value);
    }

    public string ActiveSyncingPairName
    {
        get => _activeSyncingPairName;
        set => SetProperty(ref _activeSyncingPairName, value);
    }

    public ICommand SyncNowCommand { get; }
    public ICommand DryRunCommand { get; }
    public ICommand TogglePauseCommand { get; }
    public ICommand DeletePairCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand RefreshCommand { get; }

    public SyncPairsViewModel(SyncCoordinatorService coordinator)
    {
        _coordinator = coordinator;

        SyncNowCommand = new RelayCommand<SyncPair>(async p => await ExecuteSyncAsync(p, dryRun: false));
        DryRunCommand = new RelayCommand<SyncPair>(async p => await ExecuteSyncAsync(p, dryRun: true));
        TogglePauseCommand = new RelayCommand<SyncPair>(TogglePause);
        DeletePairCommand = new RelayCommand<SyncPair>(async p => await DeletePairAsync(p));
        OpenFolderCommand = new RelayCommand<string>(OpenFolder);
        RefreshCommand = new RelayCommand(async () => await LoadPairsAsync());

        _coordinator.PairsChanged += () =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.HasShutdownStarted) return;
            if (dispatcher.CheckAccess()) RefreshPairsList();
            else dispatcher.BeginInvoke(RefreshPairsList);
        };
        _coordinator.ProgressChanged += OnProgressReport;

        RefreshPairsList();
    }

    public async Task LoadPairsAsync()
    {
        await _coordinator.ReloadPairsAsync();
        RefreshPairsList();
    }

    private void RefreshPairsList()
    {
        Pairs.Clear();
        foreach (var p in _coordinator.Pairs)
        {
            Pairs.Add(p);
        }
    }

    private async Task ExecuteSyncAsync(SyncPair? pair, bool dryRun)
    {
        if (pair == null) return;

        IsBusy = true;
        ActiveSyncingPairName = pair.Name;
        StatusMessage = dryRun ? $"Running pre-sync comparison for '{pair.Name}'..." : $"Synchronizing '{pair.Name}'...";
        CurrentProgress = 0;
        IsIndeterminateProgress = true;
        ProgressPercentageText = "Preparing...";
        ProgressText = "Analyzing changes and verifying directories...";

        try
        {
            var result = await Task.Run(() => _coordinator.TriggerSyncAsync(pair.Id, dryRun));
            RefreshPairsList();

            if (dryRun)
            {
                StatusMessage = $"Dry-run preview: {result.FilesCreated} to create, {result.FilesUpdated} to update, {result.FilesDeleted} to delete, {result.ConflictsDetected} conflicts.";
            }
            else
            {
                StatusMessage = result.Success
                    ? $"Sync completed successfully! {result.FilesCreated} created, {result.FilesUpdated} updated, {result.FilesDeleted} deleted ({result.BytesTransferred / 1024.0:F1} KB)."
                    : $"Sync completed with warnings/errors: {string.Join("; ", result.ErrorMessages)}";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void TogglePause(SyncPair? pair)
    {
        if (pair == null) return;

        if (pair.IsPaused)
        {
            _coordinator.ResumePair(pair.Id);
            StatusMessage = $"Resumed '{pair.Name}'";
        }
        else
        {
            _coordinator.PausePair(pair.Id);
            StatusMessage = $"Paused '{pair.Name}'";
        }
        RefreshPairsList();
    }

    private async Task DeletePairAsync(SyncPair? pair)
    {
        if (pair == null) return;

        var res = MessageBox.Show(
            $"Are you sure you want to remove sync pair '{pair.Name}'?\n\nNote: Your files on disk and on the SSD will NOT be deleted.",
            "Confirm Remove Pair",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (res == MessageBoxResult.Yes)
        {
            await _coordinator.DeletePairAsync(pair.Id);
            RefreshPairsList();
            StatusMessage = $"Removed pair '{pair.Name}'";
        }
    }

    private void OpenFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (Directory.Exists(path) || File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    private void OnProgressReport(SyncProgressReport report)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted) return;

        void ApplyReport()
        {
            CurrentProgress = report.ProgressPercentage;
            IsIndeterminateProgress = report.ProgressPercentage <= 0.0 && report.TotalFilesCount == 0;
            ProgressPercentageText = report.ProgressPercentage > 0 ? $"{report.ProgressPercentage:F0}%" : "";

            if (report.TotalBytesToTransfer > 0)
            {
                double transferredMb = report.BytesTransferred / (1024.0 * 1024.0);
                double totalMb = report.TotalBytesToTransfer / (1024.0 * 1024.0);
                ProgressText = $"{report.CurrentOperationDescription} • {report.ProcessedFilesCount}/{report.TotalFilesCount} files ({transferredMb:F1} MB / {totalMb:F1} MB)";
            }
            else if (report.TotalFilesCount > 0)
            {
                ProgressText = $"{report.CurrentOperationDescription} ({report.ProcessedFilesCount}/{report.TotalFilesCount} files)";
            }
            else
            {
                ProgressText = report.CurrentOperationDescription;
            }
        }

        if (dispatcher.CheckAccess()) ApplyReport();
        else dispatcher.BeginInvoke((Action)ApplyReport);
    }
}
