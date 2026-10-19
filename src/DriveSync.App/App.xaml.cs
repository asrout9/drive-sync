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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Catch unhandled UI exceptions gracefully
        DispatcherUnhandledException += (s, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"[DriveSync UI Exception] {args.Exception}");
            args.Handled = true;
        };

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
        services.AddSingleton<NewPairWizardViewModel>();
        services.AddSingleton<ConflictsViewModel>();
        services.AddSingleton<ActivityJournalViewModel>();
        services.AddSingleton<RecoveryViewModel>();
        services.AddSingleton<SsdManagerViewModel>();
        services.AddSingleton<CloudAccountsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<MainViewModel>();

        // Views
        services.AddSingleton<MainWindow>();

        string logFile = Path.Combine(appData, "startup.log");
        void Log(string msg) {
            try { File.AppendAllText(logFile, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}"); } catch { }
        }

        Log("Entering OnStartup...");

        _serviceProvider = services.BuildServiceProvider();
        Log("ServiceProvider built successfully.");

        SyncCoordinatorService coordinator;
        try
        {
            coordinator = _serviceProvider.GetRequiredService<SyncCoordinatorService>();
            Log("SyncCoordinatorService resolved.");
        }
        catch (Exception ex)
        {
            Log($"FATAL: Failed to resolve SyncCoordinatorService: {ex}");
            throw;
        }

        try
        {
            Log("Resolving MainWindow...");
            _mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            MainWindow = _mainWindow;
            Log("MainWindow resolved successfully.");
        }
        catch (Exception ex)
        {
            Log($"FATAL: Failed to resolve MainWindow: {ex}");
            throw;
        }

        try
        {
            Log("Creating TrayFlyoutWindow...");
            _trayFlyout = new TrayFlyoutWindow(coordinator, ShowMainWindow);
            Log("TrayFlyoutWindow created.");

            Log("Initializing TrayService...");
            _trayService = new TrayService();
            _trayService.Initialize(
                onOpenApp: ShowMainWindow,
                onToggleFlyout: () =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_trayFlyout.IsVisible) _trayFlyout.Hide();
                        else _trayFlyout.ShowNearTray();
                    });
                },
                onReviewConflicts: () =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        ShowMainWindow();
                        var mainVM = _serviceProvider.GetRequiredService<MainViewModel>();
                        mainVM.NavigateCommand.Execute("Conflicts");
                    });
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
                Dispatcher.Invoke(() => _trayService?.UpdateStatus(status, desc));
            };
            Log("TrayService initialized.");
        }
        catch (Exception ex)
        {
            Log($"Error initializing Tray components: {ex}");
        }

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
            Log("Calling ShowMainWindow()...");
            ShowMainWindow();
            Log("ShowMainWindow() returned.");
        }

        // Initialize background stores and sync coordinator asynchronously
        _ = Task.Run(async () =>
        {
            try
            {
                await coordinator.InitializeAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error initializing sync coordinator: {ex.Message}");
            }
        });
    }

    public void ShowMainWindow()
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

    public void ExitApplication()
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
