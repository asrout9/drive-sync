namespace DriveSync.Core.Models;

public class SyncSnapshot
{
    public string PairId { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string StableId { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset LastModifiedUtc { get; set; }
    public string? ContentHashSha256 { get; set; }
    public string? VersionTag { get; set; }
    public DateTimeOffset LastSyncedUtc { get; set; }
}
