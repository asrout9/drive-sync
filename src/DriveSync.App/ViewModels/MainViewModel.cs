using System;
using System.Windows.Input;
using DriveSync.App.Services;

namespace DriveSync.App.ViewModels;

public class MainViewModel : ViewModelBase
{
    private readonly SyncCoordinatorService _coordinator;
    private ViewModelBase _currentView;

    public SyncPairsViewModel SyncPairsVM { get; }
    public NewPairWizardViewModel NewPairWizardVM { get; }
    public ConflictsViewModel ConflictsVM { get; }
    public ActivityJournalViewModel ActivityJournalVM { get; }
    public RecoveryViewModel RecoveryVM { get; }
    public SsdManagerViewModel SsdManagerVM { get; }
    public CloudAccountsViewModel CloudAccountsVM { get; }
    public SettingsViewModel SettingsVM { get; }

    public ViewModelBase CurrentView
    {
        get => _currentView;
        set => SetProperty(ref _currentView, value);
    }

    public string OverallStatus => _coordinator.OverallStatusDescription;

    public ICommand NavigateCommand { get; }

    public MainViewModel(
        SyncCoordinatorService coordinator,
        SyncPairsViewModel syncPairsVM,
        NewPairWizardViewModel newPairWizardVM,
        ConflictsViewModel conflictsVM,
        ActivityJournalViewModel activityJournalVM,
        RecoveryViewModel recoveryVM,
        SsdManagerViewModel ssdManagerVM,
        CloudAccountsViewModel cloudAccountsVM,
        SettingsViewModel settingsVM)
    {
        _coordinator = coordinator;
        SyncPairsVM = syncPairsVM;
        NewPairWizardVM = newPairWizardVM;
        ConflictsVM = conflictsVM;
        ActivityJournalVM = activityJournalVM;
        RecoveryVM = recoveryVM;
        SsdManagerVM = ssdManagerVM;
        CloudAccountsVM = cloudAccountsVM;
        SettingsVM = settingsVM;

        _currentView = SyncPairsVM;

        NavigateCommand = new RelayCommand<string>(viewName =>
        {
            CurrentView = viewName switch
            {
                "Pairs" => SyncPairsVM,
                "NewPair" => NewPairWizardVM,
                "Conflicts" => ConflictsVM,
                "Activity" => ActivityJournalVM,
                "Recovery" => RecoveryVM,
                "Ssd" => SsdManagerVM,
                "Cloud" => CloudAccountsVM,
                "Settings" => SettingsVM,
                _ => SyncPairsVM
            };
        });

        NewPairWizardVM.OnSuccess = () => NavigateCommand.Execute("Pairs");

        _coordinator.GlobalStatusChanged += (s, desc) => OnPropertyChanged(nameof(OverallStatus));
    }
}
