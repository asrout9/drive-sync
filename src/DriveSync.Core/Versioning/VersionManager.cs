using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DriveSync.Core.Models;
using DriveSync.Core.Security;

namespace DriveSync.Core.Versioning;

public class VersionManager : IVersionManager
{
    private const string VersionsDirName = "versions";
    private const string DeletedDirName = "deleted";

    private static string GetVersionsRoot(string rootPath) =>
        Path.Combine(rootPath, PathSafety.DriveSyncMetaFolder, VersionsDirName);

    private static string GetDeletedRoot(string rootPath) =>
        Path.Combine(rootPath, PathSafety.DriveSyncMetaFolder, DeletedDirName);

    public async Task<string?> PreserveVersionBeforeOverwriteAsync(string rootPath, string relativePath, CancellationToken ct = default)
    {
        string fullSource = PathSafety.ValidateAndResolvePath(rootPath, relativePath);
        if (!File.Exists(fullSource)) return null;

        string versionsRoot = GetVersionsRoot(rootPath);
        if (!Directory.Exists(versionsRoot))
        {
            Directory.CreateDirectory(versionsRoot);
        }

        string hash = await ComputeSha256Async(fullSource, ct).ConfigureAwait(false);
        string safeRel = relativePath.Replace('/', '_').Replace('\\', '_');
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd_HHmmssfff");
        string ext = Path.GetExtension(relativePath);
        string baseName = Path.GetFileNameWithoutExtension(safeRel);

        string versionFileName = $"{baseName}_{timestamp}_{hash.Substring(0, 8)}{ext}";
        string fullVersionPath = Path.Combine(versionsRoot, versionFileName);

        File.Copy(fullSource, fullVersionPath, overwrite: true);
        return fullVersionPath;
    }

    public async Task<string?> MoveToDeletedItemsAsync(string rootPath, string relativePath, CancellationToken ct = default)
    {
        string fullSource = PathSafety.ValidateAndResolvePath(rootPath, relativePath);
        if (!File.Exists(fullSource) && !Directory.Exists(fullSource)) return null;

        string deletedRoot = GetDeletedRoot(rootPath);
        string dateFolder = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        string targetDir = Path.Combine(deletedRoot, dateFolder);

        if (!Directory.Exists(targetDir))
        {
            Directory.CreateDirectory(targetDir);
        }

        string safeRel = relativePath.Replace('/', '_').Replace('\\', '_');
        string timestamp = DateTimeOffset.UtcNow.ToString("HHmmssfff");
        string ext = Path.GetExtension(relativePath);
        string baseName = Path.GetFileNameWithoutExtension(safeRel);
        string deletedFileName = $"{baseName}_{timestamp}{ext}";
        string targetFullPath = Path.Combine(targetDir, deletedFileName);

        await Task.Run(() =>
        {
            if (File.Exists(fullSource))
            {
                File.Move(fullSource, targetFullPath, overwrite: true);
            }
            else if (Directory.Exists(fullSource))
            {
                Directory.Move(fullSource, targetFullPath);
            }
        }, ct).ConfigureAwait(false);

        return targetFullPath;
    }

    public async Task<bool> CanUndoOperationAsync(string rootPath, SyncOperation operation, CancellationToken ct = default)
    {
        if (operation.WasUndone) return false;

        string targetFullPath = PathSafety.ValidateAndResolvePath(rootPath, operation.RelativePath);

        switch (operation.ActionType)
        {
            case SyncActionType.Create:
                // For a create, undo means delete.
                // It is safe to undo ONLY IF target file has not been modified since creation.
                if (!File.Exists(targetFullPath))
                {
                    // File is already gone, undo is satisfied
                    return true;
                }
                string currentHash = await ComputeSha256Async(targetFullPath, ct).ConfigureAwait(false);
                return string.Equals(currentHash, operation.SourceHash, StringComparison.OrdinalIgnoreCase);

            case SyncActionType.Update:
                // For update, undo means restore previous version.
                // Safe ONLY IF target file currently matches TargetHash (i.e. not modified again)
                // and BackupVersionPath exists.
                if (string.IsNullOrEmpty(operation.BackupVersionPath) || !File.Exists(operation.BackupVersionPath))
                {
                    return false;
                }
                if (!File.Exists(targetFullPath))
                {
                    return false;
                }
                string updateHash = await ComputeSha256Async(targetFullPath, ct).ConfigureAwait(false);
                return string.Equals(updateHash, operation.TargetHash ?? operation.SourceHash, StringComparison.OrdinalIgnoreCase);

            case SyncActionType.Delete:
                // For delete, undo means restoring the deleted file from backup
                if (string.IsNullOrEmpty(operation.BackupVersionPath))
                {
                    return false;
                }
                // Safe if target file doesn't already exist or can be replaced
                return File.Exists(operation.BackupVersionPath) || Directory.Exists(operation.BackupVersionPath);

            case SyncActionType.Rename:
                // Undo rename: move back from NewRelativePath to RelativePath
                if (string.IsNullOrEmpty(operation.NewRelativePath)) return false;
                string newFull = PathSafety.ValidateAndResolvePath(rootPath, operation.NewRelativePath);
                return File.Exists(newFull) && !File.Exists(targetFullPath);

            default:
                return false;
        }
    }

    public async Task UndoOperationAsync(string rootPath, SyncOperation operation, CancellationToken ct = default)
    {
        bool canUndo = await CanUndoOperationAsync(rootPath, operation, ct).ConfigureAwait(false);
        if (!canUndo)
        {
            throw new InvalidOperationException($"Cannot undo operation {operation.Id}: target file at '{operation.RelativePath}' has been modified by the user or another process since synchronization, or backup version is missing.");
        }

        string targetFullPath = PathSafety.ValidateAndResolvePath(rootPath, operation.RelativePath);

        switch (operation.ActionType)
        {
            case SyncActionType.Create:
                if (File.Exists(targetFullPath))
                {
                    // Move to deleted items instead of raw delete for safety
                    await MoveToDeletedItemsAsync(rootPath, operation.RelativePath, ct).ConfigureAwait(false);
                }
                break;

            case SyncActionType.Update:
                if (!string.IsNullOrEmpty(operation.BackupVersionPath) && File.Exists(operation.BackupVersionPath))
                {
                    File.Copy(operation.BackupVersionPath, targetFullPath, overwrite: true);
                }
                break;

            case SyncActionType.Delete:
                if (!string.IsNullOrEmpty(operation.BackupVersionPath) && File.Exists(operation.BackupVersionPath))
                {
                    string parent = Path.GetDirectoryName(targetFullPath)!;
                    if (!Directory.Exists(parent)) Directory.CreateDirectory(parent);
                    File.Copy(operation.BackupVersionPath, targetFullPath, overwrite: true);
                }
                break;

            case SyncActionType.Rename:
                if (!string.IsNullOrEmpty(operation.NewRelativePath))
                {
                    string currentFull = PathSafety.ValidateAndResolvePath(rootPath, operation.NewRelativePath);
                    if (File.Exists(currentFull))
                    {
                        File.Move(currentFull, targetFullPath, overwrite: false);
                    }
                }
                break;
        }
    }

    public Task<IReadOnlyList<RecoverableItem>> GetRecoverableItemsAsync(string rootPath, CancellationToken ct = default)
    {
        var items = new List<RecoverableItem>();

        string versionsRoot = GetVersionsRoot(rootPath);
        if (Directory.Exists(versionsRoot))
        {
            foreach (var fi in new DirectoryInfo(versionsRoot).EnumerateFiles())
            {
                ct.ThrowIfCancellationRequested();
                items.Add(new RecoverableItem
                {
                    RelativePath = fi.Name,
                    ArchiveFullPath = fi.FullName,
                    ArchivedAtUtc = fi.LastWriteTimeUtc,
                    SizeBytes = fi.Length,
                    IsDeletedItem = false
                });
            }
        }

        string deletedRoot = GetDeletedRoot(rootPath);
        if (Directory.Exists(deletedRoot))
        {
            foreach (var fi in new DirectoryInfo(deletedRoot).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                items.Add(new RecoverableItem
                {
                    RelativePath = Path.GetRelativePath(deletedRoot, fi.FullName).Replace('\\', '/'),
                    ArchiveFullPath = fi.FullName,
                    ArchivedAtUtc = fi.LastWriteTimeUtc,
                    SizeBytes = fi.Length,
                    IsDeletedItem = true
                });
            }
        }

        return Task.FromResult<IReadOnlyList<RecoverableItem>>(items.OrderByDescending(i => i.ArchivedAtUtc).ToList());
    }

    public Task RestoreRecoverableItemAsync(string rootPath, RecoverableItem item, string targetRelativePath, CancellationToken ct = default)
    {
        if (!File.Exists(item.ArchiveFullPath))
        {
            throw new FileNotFoundException("Archived file no longer exists", item.ArchiveFullPath);
        }

        string targetFull = PathSafety.ValidateAndResolvePath(rootPath, targetRelativePath);
        string parent = Path.GetDirectoryName(targetFull)!;
        if (!Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }

        File.Copy(item.ArchiveFullPath, targetFull, overwrite: true);
        return Task.CompletedTask;
    }

    public Task<StorageUsageSummary> GetStorageUsageAsync(string rootPath, CancellationToken ct = default)
    {
        var summary = new StorageUsageSummary { RootPath = rootPath };

        if (!Directory.Exists(rootPath)) return Task.FromResult(summary);

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(rootPath) ?? "C:\\");
            if (drive.IsReady)
            {
                summary.TotalDriveSizeBytes = drive.TotalSize;
                summary.FreeDriveSizeBytes = drive.AvailableFreeSpace;
            }
        }
        catch { }

        var enumOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false
        };

        // Content size
        try
        {
            foreach (var file in Directory.EnumerateFiles(rootPath, "*", enumOptions))
            {
                ct.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(rootPath, file);
                if (PathSafety.IsIgnoredSystemPath(rel)) continue;

                try
                {
                    summary.ContentSizeBytes += new FileInfo(file).Length;
                }
                catch { }
            }
        }
        catch { }

        // Versions size
        string versionsRoot = GetVersionsRoot(rootPath);
        if (Directory.Exists(versionsRoot))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(versionsRoot, "*", enumOptions))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var fi = new FileInfo(file);
                        summary.VersionHistorySizeBytes += fi.Length;
                        summary.PreservedVersionsCount++;
                    }
                    catch { }
                }
            }
            catch { }
        }

        // Deleted items size
        string deletedRoot = GetDeletedRoot(rootPath);
        if (Directory.Exists(deletedRoot))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(deletedRoot, "*", enumOptions))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var fi = new FileInfo(file);
                        summary.DeletedItemsSizeBytes += fi.Length;
                        summary.DeletedItemsCount++;
                    }
                    catch { }
                }
            }
            catch { }
        }

        return Task.FromResult(summary);
    }

    public Task<int> EnforceRetentionPolicyAsync(string rootPath, RetentionPolicy policy, CancellationToken ct = default)
    {
        int purgedCount = 0;
        string versionsRoot = GetVersionsRoot(rootPath);
        string deletedRoot = GetDeletedRoot(rootPath);

        DateTimeOffset cutoff = DateTimeOffset.UtcNow.AddDays(-policy.MaxRetentionDays);

        void CleanupDirectory(string dirPath)
        {
            if (!Directory.Exists(dirPath)) return;

            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true,
                ReturnSpecialDirectories = false
            };

            List<FileInfo> files;
            try
            {
                files = new DirectoryInfo(dirPath).EnumerateFiles("*", enumOptions)
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();
            }
            catch
            {
                return;
            }

            long totalSize = files.Sum(f => f.Length);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                bool shouldDelete = file.LastWriteTimeUtc < cutoff;
                if (!shouldDelete && totalSize > policy.MaxStorageBytes)
                {
                    shouldDelete = true;
                }

                if (shouldDelete)
                {
                    try
                    {
                        totalSize -= file.Length;
                        file.Delete();
                        purgedCount++;
                    }
                    catch { }
                }
            }
        }

        CleanupDirectory(versionsRoot);
        CleanupDirectory(deletedRoot);

        return Task.FromResult(purgedCount);
    }

    public static async Task<string> ComputeSha256Async(string fullPath, CancellationToken ct = default)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        byte[] hashBytes = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
