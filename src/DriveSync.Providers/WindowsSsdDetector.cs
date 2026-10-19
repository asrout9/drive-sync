using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DriveSync.Core.Engine;
using DriveSync.Core.Models;
using DriveSync.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveSync.Providers;

public class WindowsSsdDetector : ISsdDetector
{
    private const string DriveIdFileName = "drive.id";
    private readonly ILogger<WindowsSsdDetector> _logger;
    private readonly System.Timers.Timer _pollingTimer;
    private readonly HashSet<string> _knownDrives = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _modelCache = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool GetVolumeInformation(
        string lpRootPathName,
        StringBuilder? lpVolumeNameBuffer,
        int nVolumeNameSize,
        out uint lpVolumeSerialNumber,
        out uint lpMaximumComponentLength,
        out uint lpFileSystemFlags,
        StringBuilder? lpFileSystemNameBuffer,
        int nFileSystemNameSize);

    public event EventHandler<DriveInfoModel>? DriveConnected;
    public event EventHandler<string>? DriveDisconnected;

    public WindowsSsdDetector(ILogger<WindowsSsdDetector>? logger = null)
    {
        _logger = logger ?? NullLogger<WindowsSsdDetector>.Instance;

        // Poll every 3 seconds for removable drive arrival/removal
        _pollingTimer = new System.Timers.Timer(3000);
        _pollingTimer.Elapsed += (s, e) => CheckDrives();
        _pollingTimer.Start();

        // Initial snapshot in background task to avoid blocking constructor
        Task.Run(() => CheckDrives(initial: true));
    }

    public Task<IReadOnlyList<DriveInfoModel>> DetectRemovableDrivesAsync(CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var results = new List<DriveInfoModel>();

            var drives = DriveInfo.GetDrives();
            foreach (var d in drives)
            {
                ct.ThrowIfCancellationRequested();

                // Check if ready
                if (!d.IsReady) continue;

                // We want Removable drives, or Fixed external USB drives
                bool isPotentialSsd = d.DriveType == DriveType.Removable || (d.DriveType == DriveType.Fixed && d.Name != "C:\\");

                if (isPotentialSsd)
                {
                    var model = BuildDriveModel(d);
                    results.Add(model);
                }
            }

            return (IReadOnlyList<DriveInfoModel>)results;
        }, ct);
    }

    public async Task<bool> VerifyDriveIdentityAsync(string rootPath, string? expectedSerialNumber, string? expectedToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return false;

        string driveRoot = Path.GetPathRoot(rootPath) ?? rootPath;
        var drive = new DriveInfo(driveRoot);
        if (!drive.IsReady) return false;

        // 1. Verify Volume Serial Number if available
        if (!string.IsNullOrEmpty(expectedSerialNumber))
        {
            string actualSerial = GetVolumeSerialNumber(drive.Name);
            if (!string.IsNullOrEmpty(actualSerial) &&
                !string.Equals(actualSerial, expectedSerialNumber, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Drive volume serial mismatch on '{RootPath}'. Expected: {Expected}, Actual: {Actual}", rootPath, expectedSerialNumber, actualSerial);
                return false;
            }
        }

        // 2. Verify DriveSync Token if available
        if (!string.IsNullOrEmpty(expectedToken))
        {
            string tokenPath = Path.Combine(drive.RootDirectory.FullName, PathSafety.DriveSyncMetaFolder, DriveIdFileName);
            if (!File.Exists(tokenPath))
            {
                _logger.LogWarning("DriveSync token file missing on '{RootPath}'", rootPath);
                return false;
            }

            string actualToken = (await File.ReadAllTextAsync(tokenPath, ct).ConfigureAwait(false)).Trim();
            if (!string.Equals(actualToken, expectedToken, StringComparison.Ordinal))
            {
                _logger.LogWarning("DriveSync token mismatch on '{RootPath}'. Expected: {Expected}, Actual: {Actual}", rootPath, expectedToken, actualToken);
                return false;
            }
        }

        return true;
    }

    public async Task<string> InitializeDriveSyncTokenAsync(string rootPath, CancellationToken ct = default)
    {
        string driveRoot = Path.GetPathRoot(rootPath) ?? rootPath;
        string metaDir = Path.Combine(driveRoot, PathSafety.DriveSyncMetaFolder);
        if (!Directory.Exists(metaDir))
        {
            Directory.CreateDirectory(metaDir);
            try
            {
                // Set hidden attribute on .drivesync folder
                File.SetAttributes(metaDir, FileAttributes.Hidden);
            }
            catch { }
        }

        string tokenPath = Path.Combine(metaDir, DriveIdFileName);
        string token;
        if (File.Exists(tokenPath))
        {
            token = (await File.ReadAllTextAsync(tokenPath, ct).ConfigureAwait(false)).Trim();
        }
        else
        {
            token = Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(tokenPath, token, ct).ConfigureAwait(false);
        }

        return token;
    }

    private DriveInfoModel BuildDriveModel(DriveInfo d)
    {
        var model = new DriveInfoModel
        {
            DriveLetter = d.Name,
            VolumeLabel = d.VolumeLabel,
            TotalSizeBytes = d.TotalSize,
            FreeSpaceBytes = d.AvailableFreeSpace,
            IsRemovable = d.DriveType == DriveType.Removable,
            IsReady = d.IsReady
        };

        try
        {
            model.VolumeSerialNumber = GetVolumeSerialNumber(d.Name);
            model.DeviceModel = QueryDiskModel(d.Name);
            model.IsBitLockerEncrypted = CheckBitLockerStatus(d.Name);

            // Read token if already initialized
            string tokenPath = Path.Combine(d.RootDirectory.FullName, PathSafety.DriveSyncMetaFolder, DriveIdFileName);
            if (File.Exists(tokenPath))
            {
                model.DriveSyncToken = File.ReadAllText(tokenPath).Trim();
            }
        }
        catch { }

        return model;
    }

    private string GetVolumeSerialNumber(string driveLetter)
    {
        try
        {
            string root = driveLetter.EndsWith("\\") ? driveLetter : driveLetter + "\\";
            if (GetVolumeInformation(root, null, 0, out uint serial, out _, out _, null, 0))
            {
                return serial.ToString("X8");
            }
        }
        catch { }

        // Fallback to WMI if kernel32 fails
        try
        {
            string clean = driveLetter.TrimEnd('\\');
            using var searcher = new ManagementObjectSearcher($"SELECT VolumeSerialNumber FROM Win32_LogicalDisk WHERE DeviceID = '{clean}'");
            foreach (ManagementObject disk in searcher.Get())
            {
                return disk["VolumeSerialNumber"]?.ToString() ?? string.Empty;
            }
        }
        catch { }
        return string.Empty;
    }

    private string QueryDiskModel(string driveLetter)
    {
        string clean = driveLetter.TrimEnd('\\');
        if (_modelCache.TryGetValue(clean, out var cached)) return cached;

        try
        {
            var task = Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher(
                    $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{clean}'}} WHERE AssocClass = Win32_LogicalDiskToPartition");
                foreach (ManagementObject partition in searcher.Get())
                {
                    using var diskSearcher = new ManagementObjectSearcher(
                        $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition");
                    foreach (ManagementObject drive in diskSearcher.Get())
                    {
                        return drive["Model"]?.ToString() ?? "External Drive";
                    }
                }
                return "External Drive";
            });

            if (task.Wait(1000))
            {
                _modelCache[clean] = task.Result;
                return task.Result;
            }
        }
        catch { }

        return "External Drive";
    }

    private bool CheckBitLockerStatus(string driveLetter)
    {
        try
        {
            string clean = driveLetter.TrimEnd('\\');
            var task = Task.Run(() =>
            {
                using var searcher = new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption",
                    $"SELECT ProtectionStatus FROM Win32_EncryptableVolume WHERE DriveLetter = '{clean}'");
                foreach (ManagementObject vol in searcher.Get())
                {
                    var status = Convert.ToInt32(vol["ProtectionStatus"]);
                    return status == 1; // 1 = Protected (Encrypted)
                }
                return false;
            });

            if (task.Wait(800))
            {
                return task.Result;
            }
        }
        catch { }
        return false;
    }

    private void CheckDrives(bool initial = false)
    {
        try
        {
            var currentReadyDrives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => d.Name.ToUpperInvariant())
                .ToHashSet();

            if (!initial)
            {
                // Check removed drives
                foreach (var known in _knownDrives)
                {
                    if (!currentReadyDrives.Contains(known))
                    {
                        _logger.LogInformation("Drive disconnected: {Drive}", known);
                        DriveDisconnected?.Invoke(this, known);
                    }
                }

                // Check newly arrived drives
                foreach (var current in currentReadyDrives)
                {
                    if (!_knownDrives.Contains(current))
                    {
                        _logger.LogInformation("Drive connected: {Drive}", current);
                        try
                        {
                            var di = new DriveInfo(current);
                            var model = BuildDriveModel(di);
                            DriveConnected?.Invoke(this, model);
                        }
                        catch { }
                    }
                }
            }

            _knownDrives.Clear();
            foreach (var d in currentReadyDrives) _knownDrives.Add(d);
        }
        catch { }
    }

    public void Dispose()
    {
        _pollingTimer.Stop();
        _pollingTimer.Dispose();
    }
}
