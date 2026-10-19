namespace DriveSync.Core.Models;

public class SyncOperation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string PairId { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public string? NewRelativePath { get; set; }
    public SyncActionType ActionType { get; set; }
    public EndpointSide SourceSide { get; set; }
    public EndpointSide TargetSide { get; set; }
    public bool IsDirectory { get; set; }
    public long BytesTransferred { get; set; }
    public string? SourceHash { get; set; }
    public string? TargetHash { get; set; }
    public string? BackupVersionPath { get; set; }
    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;
    public OperationStatus Status { get; set; } = OperationStatus.Pending;
    public string? ErrorMessage { get; set; }
    public bool WasUndone { get; set; }
    public DateTimeOffset? UndoneAtUtc { get; set; }

    public override string ToString() => $"[{ActionType}] {RelativePath} ({SourceSide} -> {TargetSide}) - {Status}";
}
