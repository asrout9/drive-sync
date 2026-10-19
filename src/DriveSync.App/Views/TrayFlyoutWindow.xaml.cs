using System;
using System.Linq;
using System.Windows;
using DriveSync.App.Services;
using DriveSync.Core.Models;

namespace DriveSync.App.Views;

public partial class TrayFlyoutWindow : Window
{
    private readonly SyncCoordinatorService _coordinator;
    private readonly Action _onOpenMainWindow;

    public TrayFlyoutWindow(SyncCoordinatorService coordinator, Action onOpenMainWindow)
    {
        InitializeComponent();
        _coordinator = coordinator;
        _onOpenMainWindow = onOpenMainWindow;

        _coordinator.GlobalStatusChanged += OnGlobalStatusChanged;
        _coordinator.ProgressChanged += OnProgressChanged;
        _coordinator.PairsChanged += OnPairsChanged;

        UpdateUI();
    }

    public void ShowNearTray()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 12;
        Top = workArea.Bottom - Height - 12;

        UpdateUI();
        Show();
        Activate();
    }

    private void UpdateUI()
    {
        StatusTitle.Text = _coordinator.OverallStatusDescription;
        StatusSubtitle.Text = $"Status: {_coordinator.OverallStatus}";

        StatusTitle.Foreground = _coordinator.OverallStatus switch
        {
            SyncStatus.UpToDate => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129)),
            SyncStatus.Syncing => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(14, 165, 233)),
            SyncStatus.Paused => System.Windows.Media.Brushes.Orange,
            SyncStatus.ConflictsDetected => System.Windows.Media.Brushes.DarkOrange,
            SyncStatus.Error or SyncStatus.Offline => System.Windows.Media.Brushes.Crimson,
            _ => System.Windows.Media.Brushes.LightGray
        };

        PairsList.ItemsSource = _coordinator.Pairs;
        PauseResumeBtn.Content = _coordinator.OverallStatus == SyncStatus.Paused ? "Resume All" : "Pause All";
    }

    private void OnGlobalStatusChanged(SyncStatus status, string desc)
    {
        Dispatcher.Invoke(UpdateUI);
    }

    private void OnProgressChanged(SyncProgressReport report)
    {
        Dispatcher.Invoke(() =>
        {
            TrayProgressBar.Value = report.ProgressPercentage;
            ProgressDescription.Text = $"{report.CurrentOperationDescription} ({report.ProcessedFilesCount}/{report.TotalFilesCount})";
        });
    }

    private void OnPairsChanged()
    {
        Dispatcher.Invoke(UpdateUI);
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        Hide();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }

    private void OpenApp_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        _onOpenMainWindow();
    }

    private void PauseResume_Click(object sender, RoutedEventArgs e)
    {
        if (_coordinator.OverallStatus == SyncStatus.Paused)
        {
            foreach (var p in _coordinator.Pairs) _coordinator.ResumePair(p.Id);
        }
        else
        {
            foreach (var p in _coordinator.Pairs) _coordinator.PausePair(p.Id);
        }
        UpdateUI();
    }
}
