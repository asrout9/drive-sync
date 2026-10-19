using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using DriveSync.App.Services;
using DriveSync.Core.Models;

namespace DriveSync.App.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private bool _runAtStartup;
    private int _maxVersionsPerFile = 5;
    private int _retentionDays = 30;
    private int _maxStorageGb = 10;
    private DeletionPolicy _defaultDeletionPolicy = DeletionPolicy.MoveToDeletedItems;
    private ConflictPolicy _defaultConflictPolicy = ConflictPolicy.ManualReview;
    private bool _isDarkTheme = true;
    private string _statusMessage = string.Empty;

    public bool RunAtStartup
    {
        get => _runAtStartup;
        set
        {
            if (SetProperty(ref _runAtStartup, value))
            {
                StartupService.SetRunAtStartup(value);
            }
        }
    }

    public int MaxVersionsPerFile
    {
        get => _maxVersionsPerFile;
        set => SetProperty(ref _maxVersionsPerFile, value);
    }

    public int RetentionDays
    {
        get => _retentionDays;
        set => SetProperty(ref _retentionDays, value);
    }

    public int MaxStorageGb
    {
        get => _maxStorageGb;
        set => SetProperty(ref _maxStorageGb, value);
    }

    public DeletionPolicy DefaultDeletionPolicy
    {
        get => _defaultDeletionPolicy;
        set => SetProperty(ref _defaultDeletionPolicy, value);
    }

    public ConflictPolicy DefaultConflictPolicy
    {
        get => _defaultConflictPolicy;
        set => SetProperty(ref _defaultConflictPolicy, value);
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set => SetProperty(ref _isDarkTheme, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public ICommand ExportConfigCommand { get; }

    public SettingsViewModel()
    {
        _runAtStartup = StartupService.IsRunAtStartupEnabled();
        ExportConfigCommand = new RelayCommand(ExportSanitizedConfig);
    }

    private void ExportSanitizedConfig()
    {
        using var sfd = new System.Windows.Forms.SaveFileDialog
        {
            Filter = "JSON Config (*.json)|*.json",
            FileName = "DriveSync_Sanitized_Config.json",
            Title = "Export Sanitized DriveSync Configuration"
        };

        if (sfd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var config = new
            {
                ExportedAtUtc = DateTimeOffset.UtcNow,
                AppName = "DriveSync",
                Version = "1.0.0",
                RunAtStartup,
                MaxVersionsPerFile,
                RetentionDays,
                MaxStorageGb,
                DefaultDeletionPolicy = DefaultDeletionPolicy.ToString(),
                DefaultConflictPolicy = DefaultConflictPolicy.ToString()
            };

            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(sfd.FileName, json);
            MessageBox.Show($"Configuration exported to {sfd.FileName}", "Export Completed", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
