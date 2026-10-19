namespace DriveSync.Core.Models;

public class SyncConflict
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PairId { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public SyncItem? SourceItem { get; set; }
    public SyncItem? DestinationItem { get; set; }
    public SyncSnapshot? BaseSnapshot { get; set; }
    public string ConflictReason { get; set; } = string.Empty;
    public DateTimeOffset DetectedTimeUtc { get; set; } = DateTimeOffset.UtcNow;
    public ConflictResolutionChoice Resolution { get; set; } = ConflictResolutionChoice.Unresolved;
    public DateTimeOffset? ResolvedTimeUtc { get; set; }
    public string? ResolutionNotes { get; set; }

    public override string ToString() => $"[Conflict] {RelativePath}: {ConflictReason} (Resolution: {Resolution})";
}
