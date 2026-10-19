namespace DriveSync.Core.Models;

public class DriveInfoModel
{
    public string DriveLetter { get; set; } = string.Empty;
    public string VolumeLabel { get; set; } = string.Empty;
    public string VolumeSerialNumber { get; set; } = string.Empty;
    public string DeviceModel { get; set; } = string.Empty;
    public string BusType { get; set; } = string.Empty; // USB, NVMe, etc.
    public long TotalSizeBytes { get; set; }
    public long FreeSpaceBytes { get; set; }
    public bool IsRemovable { get; set; }
    public bool IsReady { get; set; }
    public string? DriveSyncToken { get; set; }
    public bool IsBitLockerEncrypted { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(VolumeLabel)
        ? $"{DriveLetter} ({DeviceModel})"
        : $"{VolumeLabel} ({DriveLetter})";

    public string FormattedSize => $"{TotalSizeBytes / (1024.0 * 1024 * 1024):F1} GB";
    public string FormattedFreeSpace => $"{FreeSpaceBytes / (1024.0 * 1024 * 1024):F1} GB free";
}
