using System.Collections.Generic;
using System.Linq;

namespace DriveSync.Core.Models;

public class SyncPlan
{
    public string PairId { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool IsDryRun { get; set; }
    public List<SyncOperation> Operations { get; set; } = new();
    public List<SyncConflict> Conflicts { get; set; } = new();

    public int NewFilesCount => Operations.Count(o => o.ActionType == SyncActionType.Create && !o.IsDirectory);
    public int ChangedFilesCount => Operations.Count(o => o.ActionType == SyncActionType.Update && !o.IsDirectory);
    public int DeletedFilesCount => Operations.Count(o => o.ActionType == SyncActionType.Delete);
    public int ConflictsCount => Conflicts.Count;
    public long TotalBytesToTransfer => Operations.Sum(o => o.BytesTransferred);

    public long DestinationAvailableBytes { get; set; }
    public long SourceAvailableBytes { get; set; }
    public bool HasSufficientCapacity => DestinationAvailableBytes <= 0 || TotalBytesToTransfer <= DestinationAvailableBytes;
}
