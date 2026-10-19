using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DriveSync.Core.Engine;
using DriveSync.Core.Models;

namespace DriveSync.App.ViewModels;

public class SsdManagerViewModel : ViewModelBase
{
    private readonly ISsdDetector _ssdDetector;
    private DriveInfoModel? _selectedDrive;
    private bool _isBusy;
    private string _statusMessage = string.Empty;

    public ObservableCollection<DriveInfoModel> DetectedDrives { get; } = new();

    public DriveInfoModel? SelectedDrive
    {
        get => _selectedDrive;
        set => SetProperty(ref _selectedDrive, value);
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

    public ICommand RefreshDrivesCommand { get; }
    public ICommand InitializeTokenCommand { get; }

    public SsdManagerViewModel(ISsdDetector ssdDetector)
    {
        _ssdDetector = ssdDetector;

        RefreshDrivesCommand = new RelayCommand(async () => await RefreshDrivesAsync());
        InitializeTokenCommand = new RelayCommand<DriveInfoModel>(async d => await InitializeTokenAsync(d));

        _ssdDetector.DriveConnected += (s, d) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvoke(() => _ = RefreshDrivesAsync());
            }
        };
        _ssdDetector.DriveDisconnected += (s, d) =>
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.HasShutdownStarted)
            {
                dispatcher.BeginInvoke(() => _ = RefreshDrivesAsync());
            }
        };

        _ = RefreshDrivesAsync();
    }

    public async Task RefreshDrivesAsync()
    {
        IsBusy = true;
        StatusMessage = "Querying removable drives and external SSDs...";
        try
        {
            var drives = await _ssdDetector.DetectRemovableDrivesAsync();
            DetectedDrives.Clear();
            foreach (var d in drives) DetectedDrives.Add(d);
            StatusMessage = DetectedDrives.Count == 0
                ? "No removable drives or external SSDs detected. Connect your SSD via USB."
                : $"Detected {DetectedDrives.Count} external drive(s).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error detecting drives: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InitializeTokenAsync(DriveInfoModel? drive)
    {
        if (drive == null) return;

        try
        {
            string token = await _ssdDetector.InitializeDriveSyncTokenAsync(drive.DriveLetter);
            drive.DriveSyncToken = token;
            MessageBox.Show($"DriveSync token initialized on {drive.DriveLetter} ({token.Substring(0, 8)}...). The SSD can now be identified safely even if the drive letter changes upon replug.", "SSD Token Initialized", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshDrivesAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to initialize token: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
