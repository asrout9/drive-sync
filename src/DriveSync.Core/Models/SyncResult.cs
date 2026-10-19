using System;
using System.Collections.Generic;

namespace DriveSync.Core.Models;

public class SyncResult
{
    public string PairId { get; set; } = string.Empty;
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
    public bool Success { get; set; }
    public bool Cancelled { get; set; }
    public bool HasConflicts { get; set; }
    public int FilesCreated { get; set; }
    public int FilesUpdated { get; set; }
    public int FilesDeleted { get; set; }
    public int ConflictsDetected { get; set; }
    public int OperationsSkipped { get; set; }
    public int ErrorsCount { get; set; }
    public long BytesTransferred { get; set; }
    public TimeSpan Duration => CompletedAtUtc - StartedAtUtc;
    public List<string> ErrorMessages { get; set; } = new();

    public override string ToString() =>
        $"SyncResult: Success={Success}, Created={FilesCreated}, Updated={FilesUpdated}, Deleted={FilesDeleted}, Conflicts={ConflictsDetected}, Errors={ErrorsCount}, Bytes={BytesTransferred}, Duration={Duration.TotalSeconds:F1}s";
}
