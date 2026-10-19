using System;
using System.IO;
using System.Windows;
using DriveSync.App.Services;
using DriveSync.App.ViewModels;
using DriveSync.App.Views;
using DriveSync.Core.Engine;
using DriveSync.Core.Storage;
using DriveSync.Core.Versioning;
using DriveSync.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace DriveSync.App;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;
    private TrayService? _trayService;
    private TrayFlyoutWindow? _trayFlyout;
    private MainWindow? _mainWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DriveSync", "data");
        if (!Directory.Exists(appData)) Directory.CreateDirectory(appData);

        var services = new ServiceCollection();

        // Core singletons
        services.AddSingleton<ISyncStateStore>(_ => new SqliteSyncStateStore(Path.Combine(appData, "state.db")));
        services.AddSingleton<IJournalStore>(_ => new SqliteJournalStore(Path.Combine(appData, "journal.db")));
        services.AddSingleton<IVersionManager, VersionManager>();
        services.AddSingleton<IConflictResolver, ThreeWayConflictResolver>();
        services.AddSingleton<ISyncEngine, TwoWaySyncEngine>();
        services.AddSingleton<ISsdDetector, WindowsSsdDetector>();
        services.AddSingleton<ICloudCredentialStore>(_ => new DpapiCloudCredentialStore(Path.Combine(appData, "vault")));

        // Services
        services.AddSingleton<SyncCoordinatorService>();

        // ViewModels
        services.AddSingleton<SyncPairsViewModel>();
        services.AddTransient<NewPairWizardViewModel>(sp =>
        {
            var coord = sp.GetRequiredService<SyncCoordinatorService>();
            var ssd = sp.GetRequiredService<ISsdDetector>();
            var cred = sp.GetRequiredService<ICloudCredentialStore>();
            var mainVM = sp.GetRequiredService<MainViewModel>();
            return new NewPairWizardViewModel(coord, ssd, cred, () => mainVM.NavigateCommand.Execute("Pairs"));
        });
        services.AddSingleton<ConflictsViewModel>();
        services.AddSingleton<ActivityJournalViewModel>();
        services.AddSingleton<RecoveryViewModel>();
        services.AddSingleton<SsdManagerViewModel>();
        services.AddSingleton<CloudAccountsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();

        // Views
        services.AddSingleton<MainWindow>();

        _serviceProvider = services.BuildServiceProvider();

        // Initialize background stores and sync coordinator
        var coordinator = _serviceProvider.GetRequiredService<SyncCoordinatorService>();
        await coordinator.InitializeAsync();

        _mainWindow = _serviceProvider.GetRequiredService<MainWindow>();

        // Setup System Tray
        _trayFlyout = new TrayFlyoutWindow(coordinator, ShowMainWindow);

        _trayService = new TrayService();
        _trayService.Initialize(
            onOpenApp: ShowMainWindow,
            onToggleFlyout: () =>
            {
                if (_trayFlyout.IsVisible) _trayFlyout.Hide();
                else _trayFlyout.ShowNearTray();
            },
            onReviewConflicts: () =>
            {
                ShowMainWindow();
                var mainVM = _serviceProvider.GetRequiredService<MainViewModel>();
                mainVM.NavigateCommand.Execute("Conflicts");
            },
            onPauseResumeAll: () =>
            {
                if (coordinator.OverallStatus == Core.Models.SyncStatus.Paused)
                {
                    foreach (var p in coordinator.Pairs) coordinator.ResumePair(p.Id);
                }
                else
                {
                    foreach (var p in coordinator.Pairs) coordinator.PausePair(p.Id);
                }
            },
            onSyncAllNow: () =>
            {
                foreach (var p in coordinator.Pairs) _ = coordinator.TriggerSyncAsync(p.Id);
            },
            onExit: ExitApplication);

        coordinator.GlobalStatusChanged += (status, desc) =>
        {
            _trayService.UpdateStatus(status, desc);
        };

        // Determine if starting in background (e.g., from Windows Run key)
        bool startInBackground = false;
        foreach (var arg in e.Args)
        {
            if (string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase))
            {
                startInBackground = true;
                break;
            }
        }

        if (!startInBackground)
        {
            ShowMainWindow();
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow != null)
        {
            _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }
            _mainWindow.Activate();
        }
    }

    private void ExitApplication()
    {
        _trayService?.Dispose();
        _trayFlyout?.Close();
        _mainWindow?.ForceClose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayService?.Dispose();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
