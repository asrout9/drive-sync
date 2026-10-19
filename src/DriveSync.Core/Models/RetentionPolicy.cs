namespace DriveSync.Core.Models;

public class RetentionPolicy
{
    public int MaxVersionsPerFile { get; set; } = 5;
    public int MaxRetentionDays { get; set; } = 30;
    public long MaxStorageBytes { get; set; } = 10L * 1024 * 1024 * 1024; // 10 GB default
    public bool AutoCleanupEnabled { get; set; } = true;
}
