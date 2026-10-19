using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DriveSync.App.Services;
using DriveSync.Core.Engine;
using DriveSync.Core.Models;
using DriveSync.Core.Storage;
using DriveSync.Providers;

namespace DriveSync.App.ViewModels;

public class NewPairWizardViewModel : ViewModelBase
{
    private readonly SyncCoordinatorService _coordinator;
    private readonly ISsdDetector _ssdDetector;
    private readonly ICloudCredentialStore _credStore;
    private readonly Action _onSuccess;

    private int _currentStep = 1;
    private string _pairName = "My Sync Pair";

    // Source selection
    private ProviderType _sourceType = ProviderType.Local;
    private string _sourcePath = string.Empty;
    private string _sourceDisplayName = "Local Folder";

    // Destination selection
    private ProviderType _destType = ProviderType.ExternalSsd;
    private string _destPath = string.Empty;
    private string _destDisplayName = "External SSD";

    // SSD Selection
    private DriveInfoModel? _selectedSsd;
    public ObservableCollection<DriveInfoModel> AvailableSsds { get; } = new();

    // Cloud Accounts
    public ObservableCollection<CloudAccountCredential> CloudAccounts { get; } = new();
    private CloudAccountCredential? _selectedCloudAccount;

    // Policies
    private DeletionPolicy _deletionPolicy = DeletionPolicy.MoveToDeletedItems;
    private ConflictPolicy _conflictPolicy = ConflictPolicy.ManualReview;
    private bool _enableRealTimeWatcher = true;
    private int _scheduledIntervalSeconds = 60;

    // Pre-sync preview
    private bool _isComparing;
    private SyncPlan? _previewPlan;
    private string _comparisonSummary = "Click 'Analyze Pre-Sync Preview' to check changes before first sync.";
    private bool _hasConfirmedFirstSync;

    public int CurrentStep
    {
        get => _currentStep;
        set => SetProperty(ref _currentStep, value);
    }

    public string PairName
    {
        get => _pairName;
        set => SetProperty(ref _pairName, value);
    }

    public ProviderType SourceType
    {
        get => _sourceType;
        set
        {
            if (SetProperty(ref _sourceType, value))
            {
                UpdateDefaultPaths();
            }
        }
    }

    public string SourcePath
    {
        get => _sourcePath;
        set => SetProperty(ref _sourcePath, value);
    }

    public string SourceDisplayName
    {
        get => _sourceDisplayName;
        set => SetProperty(ref _sourceDisplayName, value);
    }

    public ProviderType DestType
    {
        get => _destType;
        set
        {
            if (SetProperty(ref _destType, value))
            {
                UpdateDefaultPaths();
            }
        }
    }

    public string DestPath
    {
        get => _destPath;
        set => SetProperty(ref _destPath, value);
    }

    public string DestDisplayName
    {
        get => _destDisplayName;
        set => SetProperty(ref _destDisplayName, value);
    }

    public DriveInfoModel? SelectedSsd
    {
        get => _selectedSsd;
        set
        {
            if (SetProperty(ref _selectedSsd, value) && value != null)
            {
                if (DestType == ProviderType.ExternalSsd)
                {
                    DestPath = Path.Combine(value.DriveLetter, "DriveSync_Folder");
                    DestDisplayName = $"{value.DisplayName}";
                }
                else if (SourceType == ProviderType.ExternalSsd)
                {
                    SourcePath = Path.Combine(value.DriveLetter, "DriveSync_Folder");
                    SourceDisplayName = $"{value.DisplayName}";
                }
            }
        }
    }

    public CloudAccountCredential? SelectedCloudAccount
    {
        get => _selectedCloudAccount;
        set => SetProperty(ref _selectedCloudAccount, value);
    }

    public DeletionPolicy DeletionPolicy
    {
        get => _deletionPolicy;
        set => SetProperty(ref _deletionPolicy, value);
    }

    public ConflictPolicy ConflictPolicy
    {
        get => _conflictPolicy;
        set => SetProperty(ref _conflictPolicy, value);
    }

    public bool EnableRealTimeWatcher
    {
        get => _enableRealTimeWatcher;
        set => SetProperty(ref _enableRealTimeWatcher, value);
    }

    public int ScheduledIntervalSeconds
    {
        get => _scheduledIntervalSeconds;
        set => SetProperty(ref _scheduledIntervalSeconds, value);
    }

    public bool IsComparing
    {
        get => _isComparing;
        set => SetProperty(ref _isComparing, value);
    }

    public SyncPlan? PreviewPlan
    {
        get => _previewPlan;
        set => SetProperty(ref _previewPlan, value);
    }

    public string ComparisonSummary
    {
        get => _comparisonSummary;
        set => SetProperty(ref _comparisonSummary, value);
    }

    public bool HasConfirmedFirstSync
    {
        get => _hasConfirmedFirstSync;
        set => SetProperty(ref _hasConfirmedFirstSync, value);
    }

    public ICommand NextStepCommand { get; }
    public ICommand PrevStepCommand { get; }
    public ICommand RunPreSyncComparisonCommand { get; }
    public ICommand ConfirmAndSavePairCommand { get; }
    public ICommand BrowseSourceFolderCommand { get; }
    public ICommand BrowseDestFolderCommand { get; }

    public NewPairWizardViewModel(
        SyncCoordinatorService coordinator,
        ISsdDetector ssdDetector,
        ICloudCredentialStore credStore,
        Action onSuccess)
    {
        _coordinator = coordinator;
        _ssdDetector = ssdDetector;
        _credStore = credStore;
        _onSuccess = onSuccess;

        NextStepCommand = new RelayCommand(NextStep);
        PrevStepCommand = new RelayCommand(PrevStep);
        RunPreSyncComparisonCommand = new RelayCommand(async () => await RunPreSyncComparisonAsync());
        ConfirmAndSavePairCommand = new RelayCommand(async () => await ConfirmAndSavePairAsync(), () => HasConfirmedFirstSync);
        BrowseSourceFolderCommand = new RelayCommand(BrowseSourceFolder);
        BrowseDestFolderCommand = new RelayCommand(BrowseDestFolder);

        _ = LoadInitialDataAsync();
    }

    private async Task LoadInitialDataAsync()
    {
        var ssds = await _ssdDetector.DetectRemovableDrivesAsync();
        AvailableSsds.Clear();
        foreach (var s in ssds) AvailableSsds.Add(s);
        if (AvailableSsds.Count > 0) SelectedSsd = AvailableSsds[0];

        var accounts = await _credStore.GetAllAccountsAsync();
        CloudAccounts.Clear();
        foreach (var a in accounts) CloudAccounts.Add(a);
        if (CloudAccounts.Count > 0) SelectedCloudAccount = CloudAccounts[0];

        UpdateDefaultPaths();
    }

    private void UpdateDefaultPaths()
    {
        if (SourceType == ProviderType.Local && string.IsNullOrEmpty(SourcePath))
        {
            SourcePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "SyncSource");
            SourceDisplayName = "Local Documents";
        }
        else if (SourceType == ProviderType.OneDrive && string.IsNullOrEmpty(SourcePath))
        {
            SourcePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OneDrive", "SyncSource");
            SourceDisplayName = "OneDrive Folder";
        }
        else if (SourceType == ProviderType.GoogleDrive && string.IsNullOrEmpty(SourcePath))
        {
            SourcePath = @"G:\My Drive\SyncSource";
            SourceDisplayName = "Google Drive Folder";
        }

        if (DestType == ProviderType.ExternalSsd && SelectedSsd != null)
        {
            DestPath = Path.Combine(SelectedSsd.DriveLetter, "DriveSync_Target");
            DestDisplayName = $"{SelectedSsd.DisplayName}";
        }
        else if (DestType == ProviderType.Local && string.IsNullOrEmpty(DestPath))
        {
            DestPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "SyncTarget");
            DestDisplayName = "Local Target Folder";
        }
    }

    private void BrowseSourceFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select Source Folder for Two-Way Synchronization",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            SourcePath = dialog.SelectedPath;
            SourceDisplayName = Path.GetFileName(dialog.SelectedPath);
            if (string.IsNullOrEmpty(SourceDisplayName)) SourceDisplayName = dialog.SelectedPath;
        }
    }

    private void BrowseDestFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select Destination Folder for Two-Way Synchronization",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            DestPath = dialog.SelectedPath;
            DestDisplayName = Path.GetFileName(dialog.SelectedPath);
            if (string.IsNullOrEmpty(DestDisplayName)) DestDisplayName = dialog.SelectedPath;
        }
    }

    private void NextStep()
    {
        if (CurrentStep == 1)
        {
            if (string.IsNullOrWhiteSpace(SourcePath))
            {
                MessageBox.Show("Please specify or select a valid Source folder.", "Source Folder Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            CurrentStep = 2;
        }
        else if (CurrentStep == 2)
        {
            if (string.IsNullOrWhiteSpace(DestPath))
            {
                MessageBox.Show("Please specify or select a valid Destination folder.", "Destination Folder Required", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            CurrentStep = 3;
        }
        else if (CurrentStep == 3)
        {
            CurrentStep = 4;
            _ = RunPreSyncComparisonAsync();
        }
    }

    private void PrevStep()
    {
        if (CurrentStep > 1) CurrentStep--;
    }

    public async Task RunPreSyncComparisonAsync()
    {
        IsComparing = true;
        ComparisonSummary = "Scanning files on both sides and analyzing differences...";

        try
        {
            // Ensure directories exist
            if (!Directory.Exists(SourcePath)) Directory.CreateDirectory(SourcePath);
            if (!Directory.Exists(DestPath)) Directory.CreateDirectory(DestPath);

            var pair = BuildSyncPair();
            using var srcProv = new FileSystemProvider(pair.Source);
            using var dstProv = new FileSystemProvider(pair.Destination);
            var resolver = new ThreeWayConflictResolver();

            var srcItems = (await srcProv.EnumerateItemsAsync()).ToDictionary(i => i.RelativePath, StringComparer.OrdinalIgnoreCase);
            var dstItems = (await dstProv.EnumerateItemsAsync()).ToDictionary(i => i.RelativePath, StringComparer.OrdinalIgnoreCase);

            var plan = resolver.EvaluateSyncPlan(pair, srcItems, dstItems, new Dictionary<string, SyncSnapshot>(), isDryRun: true);
            plan.DestinationAvailableBytes = await dstProv.GetAvailableFreeSpaceAsync();
            PreviewPlan = plan;

            ComparisonSummary = $"Pre-Sync Analysis: {plan.NewFilesCount} new files, {plan.ChangedFilesCount} changed files, {plan.ConflictsCount} conflicts. Total data to transfer: {plan.TotalBytesToTransfer / 1024.0:F1} KB. Destination Free Space: {plan.DestinationAvailableBytes / (1024.0 * 1024 * 1024):F1} GB.";
        }
        catch (Exception ex)
        {
            ComparisonSummary = $"Comparison warning: {ex.Message}";
        }
        finally
        {
            IsComparing = false;
        }
    }

    public async Task ConfirmAndSavePairAsync()
    {
        if (!HasConfirmedFirstSync)
        {
            MessageBox.Show("Please check the confirmation box acknowledging that changes and configured deletions will propagate in two-way synchronization.", "Confirmation Required", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var pair = BuildSyncPair();

        // If External SSD, initialize token on the drive
        if (pair.Destination.ProviderType == ProviderType.ExternalSsd)
        {
            try
            {
                string token = await _ssdDetector.InitializeDriveSyncTokenAsync(pair.Destination.RootPath);
                pair.Destination.SsdUniqueId = token;
            }
            catch { }
        }

        await _coordinator.SavePairAsync(pair);
        MessageBox.Show($"Sync pair '{pair.Name}' successfully created! Two-way synchronization is active.", "DriveSync Configured", MessageBoxButton.OK, MessageBoxImage.Information);
        _onSuccess();
    }

    private SyncPair BuildSyncPair()
    {
        return new SyncPair
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = PairName,
            Source = new SyncEndpoint
            {
                ProviderType = SourceType,
                RootPath = SourcePath,
                DisplayName = SourceDisplayName,
                AccountId = SelectedCloudAccount?.AccountId,
                AccountEmail = SelectedCloudAccount?.Email
            },
            Destination = new SyncEndpoint
            {
                ProviderType = DestType,
                RootPath = DestPath,
                DisplayName = DestDisplayName,
                VolumeSerialNumber = SelectedSsd?.VolumeSerialNumber,
                SsdUniqueId = SelectedSsd?.DriveSyncToken,
                AccountId = SelectedCloudAccount?.AccountId,
                AccountEmail = SelectedCloudAccount?.Email
            },
            Direction = SyncDirection.TwoWay,
            DeletionPolicy = DeletionPolicy,
            ConflictPolicy = ConflictPolicy,
            EnableRealtimeWatcher = EnableRealTimeWatcher,
            ScheduledIntervalSeconds = ScheduledIntervalSeconds,
            HasCompletedInitialSync = false
        };
    }
}
