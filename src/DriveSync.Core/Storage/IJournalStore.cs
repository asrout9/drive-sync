using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DriveSync.Core.Models;

namespace DriveSync.Core.Storage;

public class DailySyncSummary
{
    public DateTime Date { get; set; }
    public int FilesCreated { get; set; }
    public int FilesUpdated { get; set; }
    public int FilesDeleted { get; set; }
    public int FilesRenamed { get; set; }
    public int FilesRestored { get; set; }
    public int FilesSkipped { get; set; }
    public int ConflictsDetected { get; set; }
    public int ConflictsResolved { get; set; }
    public long TotalBytesTransferred { get; set; }
}

public class JournalFilter
{
    public string? PairId { get; set; }
    public SyncActionType? ActionType { get; set; }
    public OperationStatus? Status { get; set; }
    public DateTimeOffset? FromDateUtc { get; set; }
    public DateTimeOffset? ToDateUtc { get; set; }
    public string? SearchQuery { get; set; }
    public int Limit { get; set; } = 500;
}

public interface IJournalStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task LogOperationAsync(SyncOperation operation, CancellationToken ct = default);
    Task LogOperationsBatchAsync(IEnumerable<SyncOperation> operations, CancellationToken ct = default);
    Task<SyncOperation?> GetOperationAsync(string operationId, CancellationToken ct = default);
    Task MarkOperationUndoneAsync(string operationId, CancellationToken ct = default);
    Task<IReadOnlyList<SyncOperation>> QueryOperationsAsync(JournalFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<DailySyncSummary>> GetDailySummariesAsync(string? pairId = null, int days = 30, CancellationToken ct = default);
    Task<string> ExportSanitizedReportAsync(JournalFilter filter, CancellationToken ct = default);
    Task PruneOldOperationsAsync(int retentionDays, CancellationToken ct = default);
}
