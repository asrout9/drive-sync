using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DriveSync.App.Services;
using DriveSync.Core.Models;
using DriveSync.Core.Versioning;

namespace DriveSync.App.ViewModels;

public class RecoveryViewModel : ViewModelBase
{
    private readonly IVersionManager _versionManager;
    private readonly SyncCoordinatorService _coordinator;
    private RecoverableItem? _selectedItem;
    private StorageUsageSummary? _storageUsage;
    private bool _isBusy;
    private string _statusMessage = string.Empty;

    public ObservableCollection<RecoverableItem> RecoverableItems { get; } = new();

    public RecoverableItem? SelectedItem
    {
        get => _selectedItem;
        set => SetProperty(ref _selectedItem, value);
    }

    public StorageUsageSummary? StorageUsage
    {
        get => _storageUsage;
        set => SetProperty(ref _storageUsage, value);
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

    public ICommand RestoreItemCommand { get; }
    public ICommand RunRetentionCleanupCommand { get; }
    public ICommand RefreshCommand { get; }

    public RecoveryViewModel(IVersionManager versionManager, SyncCoordinatorService coordinator)
    {
        _versionManager = versionManager;
        _coordinator = coordinator;

        RestoreItemCommand = new RelayCommand(async () => await RestoreSelectedItemAsync());
        RunRetentionCleanupCommand = new RelayCommand(async () => await RunRetentionCleanupAsync());
        RefreshCommand = new RelayCommand(async () => await LoadDataAsync());

        _ = LoadDataAsync();
    }

    public async Task LoadDataAsync()
    {
        IsBusy = true;
        StatusMessage = "Scanning recoverable archives...";
        RecoverableItems.Clear();

        try
        {
            var pairs = _coordinator.Pairs;
            long totalContent = 0, totalVersions = 0, totalDeleted = 0;
            int totalVersionCount = 0, totalDeletedCount = 0;

            foreach (var pair in pairs)
            {
                // Source
                if (Directory.Exists(pair.Source.RootPath))
                {
                    var items = await _versionManager.GetRecoverableItemsAsync(pair.Source.RootPath);
                    foreach (var i in items) RecoverableItems.Add(i);

                    var usage = await _versionManager.GetStorageUsageAsync(pair.Source.RootPath);
                    totalContent += usage.ContentSizeBytes;
                    totalVersions += usage.VersionHistorySizeBytes;
                    totalDeleted += usage.DeletedItemsSizeBytes;
                    totalVersionCount += usage.PreservedVersionsCount;
                    totalDeletedCount += usage.DeletedItemsCount;
                }

                // Destination
                if (Directory.Exists(pair.Destination.RootPath))
                {
                    var items = await _versionManager.GetRecoverableItemsAsync(pair.Destination.RootPath);
                    foreach (var i in items) RecoverableItems.Add(i);

                    var usage = await _versionManager.GetStorageUsageAsync(pair.Destination.RootPath);
                    totalContent += usage.ContentSizeBytes;
                    totalVersions += usage.VersionHistorySizeBytes;
                    totalDeleted += usage.DeletedItemsSizeBytes;
                    totalVersionCount += usage.PreservedVersionsCount;
                    totalDeletedCount += usage.DeletedItemsCount;
                }
            }

            StorageUsage = new StorageUsageSummary
            {
                ContentSizeBytes = totalContent,
                VersionHistorySizeBytes = totalVersions,
                DeletedItemsSizeBytes = totalDeleted,
                PreservedVersionsCount = totalVersionCount,
                DeletedItemsCount = totalDeletedCount
            };

            StatusMessage = $"Found {RecoverableItems.Count} recoverable files across configured sync endpoints.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading recovery data: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreSelectedItemAsync()
    {
        if (SelectedItem == null) return;

        using var sfd = new System.Windows.Forms.SaveFileDialog
        {
            Title = "Restore Recoverable File",
            FileName = Path.GetFileName(SelectedItem.RelativePath),
            Filter = "Original File Type|*.*"
        };

        if (sfd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            try
            {
                await Task.Run(() => File.Copy(SelectedItem.ArchiveFullPath, sfd.FileName, overwrite: true));
                MessageBox.Show($"File successfully restored to:\n{sfd.FileName}", "Restore Succeeded", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Restore failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async Task RunRetentionCleanupAsync()
    {
        var confirm = MessageBox.Show(
            "Enforce retention policy cleanup now?\n\nThis will safely purge archived versions and deleted items that exceed your configured retention period (e.g. older than 30 days or beyond the storage budget). Active sync files will never be touched.",
            "Run Retention Cleanup",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            int totalPurged = 0;
            var defaultPolicy = new RetentionPolicy();

            foreach (var pair in _coordinator.Pairs)
            {
                if (Directory.Exists(pair.Source.RootPath))
                {
                    totalPurged += await _versionManager.EnforceRetentionPolicyAsync(pair.Source.RootPath, pair.RetentionPolicy ?? defaultPolicy);
                }
                if (Directory.Exists(pair.Destination.RootPath))
                {
                    totalPurged += await _versionManager.EnforceRetentionPolicyAsync(pair.Destination.RootPath, pair.RetentionPolicy ?? defaultPolicy);
                }
            }

            await LoadDataAsync();
            MessageBox.Show($"Retention cleanup completed! {totalPurged} expired archive files pruned safely.", "Retention Cleanup", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Cleanup error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
