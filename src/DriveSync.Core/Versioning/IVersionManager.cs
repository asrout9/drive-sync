using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DriveSync.Core.Models;

namespace DriveSync.Core.Versioning;

public class StorageUsageSummary
{
    public string RootPath { get; set; } = string.Empty;
    public long ContentSizeBytes { get; set; }
    public long VersionHistorySizeBytes { get; set; }
    public long DeletedItemsSizeBytes { get; set; }
    public long TotalDriveSizeBytes { get; set; }
    public long FreeDriveSizeBytes { get; set; }

    public int PreservedVersionsCount { get; set; }
    public int DeletedItemsCount { get; set; }
}

public class RecoverableItem
{
    public string RelativePath { get; set; } = string.Empty;
    public string ArchiveFullPath { get; set; } = string.Empty;
    public DateTimeOffset ArchivedAtUtc { get; set; }
    public long SizeBytes { get; set; }
    public bool IsDeletedItem { get; set; } // true = from deleted items, false = version history
    public string? ContentHash { get; set; }
}

public interface IVersionManager
{
    Task<string?> PreserveVersionBeforeOverwriteAsync(string rootPath, string relativePath, CancellationToken ct = default);
    Task<string?> MoveToDeletedItemsAsync(string rootPath, string relativePath, CancellationToken ct = default);
    Task<bool> CanUndoOperationAsync(string rootPath, SyncOperation operation, CancellationToken ct = default);
    Task UndoOperationAsync(string rootPath, SyncOperation operation, CancellationToken ct = default);
    Task<IReadOnlyList<RecoverableItem>> GetRecoverableItemsAsync(string rootPath, CancellationToken ct = default);
    Task RestoreRecoverableItemAsync(string rootPath, RecoverableItem item, string targetRelativePath, CancellationToken ct = default);
    Task<StorageUsageSummary> GetStorageUsageAsync(string rootPath, CancellationToken ct = default);
    Task<int> EnforceRetentionPolicyAsync(string rootPath, RetentionPolicy policy, CancellationToken ct = default);
}
