using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DriveSync.Core.Models;
using DriveSync.Core.Security;
using DriveSync.Core.Storage;
using DriveSync.Core.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveSync.Core.Engine;

public class TwoWaySyncEngine : ISyncEngine
{
    private readonly ISyncStateStore _stateStore;
    private readonly IJournalStore _journalStore;
    private readonly IVersionManager _versionManager;
    private readonly IConflictResolver _conflictResolver;
    private readonly ILogger<TwoWaySyncEngine> _logger;

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningCts = new();
    private readonly ConcurrentDictionary<string, ManualResetEventSlim> _pauseEvents = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pairLocks = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pathLocks = new();

    public TwoWaySyncEngine(
        ISyncStateStore stateStore,
        IJournalStore journalStore,
        IVersionManager versionManager,
        IConflictResolver conflictResolver,
        ILogger<TwoWaySyncEngine>? logger = null)
    {
        _stateStore = stateStore;
        _journalStore = journalStore;
        _versionManager = versionManager;
        _conflictResolver = conflictResolver;
        _logger = logger ?? NullLogger<TwoWaySyncEngine>.Instance;
    }

    public async Task<SyncPlan> AnalyzeAsync(
        SyncPair pair,
        ISyncProvider sourceProvider,
        ISyncProvider destinationProvider,
        bool isDryRun = false,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Starting sync analysis for pair '{PairName}' (DryRun: {DryRun})", pair.Name, isDryRun);

        // Enumerate both sides in parallel
        var sourceTask = sourceProvider.EnumerateItemsAsync("", ct);
        var destTask = destinationProvider.EnumerateItemsAsync("", ct);
        var snapshotsTask = _stateStore.GetSnapshotsAsync(pair.Id, ct);

        await Task.WhenAll(sourceTask, destTask, snapshotsTask).ConfigureAwait(false);

        var sourceItems = (await sourceTask.ConfigureAwait(false)).ToDictionary(i => i.RelativePath, StringComparer.OrdinalIgnoreCase);
        var destItems = (await destTask.ConfigureAwait(false)).ToDictionary(i => i.RelativePath, StringComparer.OrdinalIgnoreCase);
        var baseSnapshots = await snapshotsTask.ConfigureAwait(false);

        var plan = _conflictResolver.EvaluateSyncPlan(pair, sourceItems, destItems, baseSnapshots, isDryRun);

        try
        {
            plan.DestinationAvailableBytes = await destinationProvider.GetAvailableFreeSpaceAsync(ct).ConfigureAwait(false);
            plan.SourceAvailableBytes = await sourceProvider.GetAvailableFreeSpaceAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve available storage capacity");
        }

        return plan;
    }

    public async Task<SyncResult> ExecuteSyncAsync(
        SyncPair pair,
        ISyncProvider sourceProvider,
        ISyncProvider destinationProvider,
        SyncPlan plan,
        IProgress<SyncProgressReport>? progress = null,
        CancellationToken ct = default)
    {
        var pairLock = _pairLocks.GetOrAdd(pair.Id, _ => new SemaphoreSlim(1, 1));
        await pairLock.WaitAsync(ct).ConfigureAwait(false);

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runningCts[pair.Id] = cts;
        var pauseEvent = _pauseEvents.GetOrAdd(pair.Id, _ => new ManualResetEventSlim(true));
        pauseEvent.Set(); // unpaused initially

        var result = new SyncResult
        {
            PairId = pair.Id,
            StartedAtUtc = DateTimeOffset.UtcNow
        };

        try
        {
            // Record any new unresolved conflicts into persistent store
            foreach (var conflict in plan.Conflicts)
            {
                await _stateStore.SaveConflictAsync(conflict, cts.Token).ConfigureAwait(false);
                result.ConflictsDetected++;
            }

            if (plan.Conflicts.Count > 0)
            {
                result.HasConflicts = true;
            }

            if (plan.IsDryRun)
            {
                _logger.LogInformation("Dry-run completed for pair '{PairName}': {OpCount} operations planned, {ConflictCount} conflicts", pair.Name, plan.Operations.Count, plan.Conflicts.Count);
                result.Success = true;
                result.CompletedAtUtc = DateTimeOffset.UtcNow;
                return result;
            }

            // Check capacity before transferring
            if (!plan.HasSufficientCapacity && plan.TotalBytesToTransfer > 0)
            {
                string msg = $"Insufficient storage on destination. Required: {plan.TotalBytesToTransfer} bytes, Available: {plan.DestinationAvailableBytes} bytes.";
                _logger.LogError(msg);
                result.ErrorMessages.Add(msg);
                result.Success = false;
                result.CompletedAtUtc = DateTimeOffset.UtcNow;
                return result;
            }

            // Bounded concurrency (e.g. 4 concurrent transfers max)
            using var concurrencySemaphore = new SemaphoreSlim(4, 4);
            int processedCount = 0;
            int filesCreated = 0;
            int filesUpdated = 0;
            int filesDeleted = 0;
            long totalTransferred = 0;
            var completedOperations = new List<SyncOperation>();
            var newSnapshots = new List<SyncSnapshot>();

            // Separate directories and files: create directories first, process files, then delete directories last
            var createDirs = plan.Operations.Where(o => o.ActionType == SyncActionType.Create && o.IsDirectory).ToList();
            var fileOps = plan.Operations.Where(o => !o.IsDirectory).ToList();
            var deleteDirs = plan.Operations.Where(o => o.ActionType == SyncActionType.Delete && o.IsDirectory)
                                           .OrderByDescending(o => o.RelativePath.Count(c => c == '/'))
                                           .ToList();

            // 1. Create directories
            foreach (var dirOp in createDirs)
            {
                cts.Token.ThrowIfCancellationRequested();
                WaitIfPaused(pair.Id);

                var targetProv = dirOp.TargetSide == EndpointSide.Destination ? destinationProvider : sourceProvider;
                await targetProv.CreateFolderAsync(dirOp.RelativePath, cts.Token).ConfigureAwait(false);
                dirOp.Status = OperationStatus.Completed;
                completedOperations.Add(dirOp);
            }

            // 2. Process file operations concurrently
            var tasks = fileOps.Select(async op =>
            {
                await concurrencySemaphore.WaitAsync(cts.Token).ConfigureAwait(false);
                try
                {
                    cts.Token.ThrowIfCancellationRequested();
                    WaitIfPaused(pair.Id);

                    // Acquire per-path lock
                    var pathLock = _pathLocks.GetOrAdd(op.RelativePath, _ => new SemaphoreSlim(1, 1));
                    await pathLock.WaitAsync(cts.Token).ConfigureAwait(false);
                    try
                    {
                        await ExecuteSingleOperationWithRetryAsync(
                            pair, op, sourceProvider, destinationProvider, cts.Token).ConfigureAwait(false);

                        if (op.Status == OperationStatus.Completed)
                        {
                            Interlocked.Increment(ref processedCount);
                            Interlocked.Add(ref totalTransferred, op.BytesTransferred);

                            lock (completedOperations)
                            {
                                completedOperations.Add(op);
                            }

                            // Update snapshot record
                            var snap = new SyncSnapshot
                            {
                                PairId = pair.Id,
                                RelativePath = op.RelativePath,
                                IsDirectory = op.IsDirectory,
                                SizeBytes = op.BytesTransferred,
                                LastModifiedUtc = DateTimeOffset.UtcNow,
                                ContentHashSha256 = op.SourceHash,
                                LastSyncedUtc = DateTimeOffset.UtcNow
                            };

                            lock (newSnapshots)
                            {
                                newSnapshots.Add(snap);
                            }

                            if (op.ActionType == SyncActionType.Create) Interlocked.Increment(ref filesCreated);
                            else if (op.ActionType == SyncActionType.Update) Interlocked.Increment(ref filesUpdated);
                            else if (op.ActionType == SyncActionType.Delete) Interlocked.Increment(ref filesDeleted);

                            progress?.Report(new SyncProgressReport
                            {
                                PairId = pair.Id,
                                CurrentOperationDescription = $"{op.ActionType} {op.RelativePath}",
                                CurrentItemPath = op.RelativePath,
                                ProcessedFilesCount = processedCount,
                                TotalFilesCount = fileOps.Count,
                                BytesTransferred = totalTransferred,
                                TotalBytesToTransfer = plan.TotalBytesToTransfer
                            });
                        }
                        else
                        {
                            lock (result.ErrorMessages)
                            {
                                result.ErrorMessages.Add($"[{op.RelativePath}] {op.ErrorMessage}");
                                result.ErrorsCount++;
                            }
                        }
                    }
                    finally
                    {
                        pathLock.Release();
                    }
                }
                finally
                {
                    concurrencySemaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // 3. Delete directories last
            foreach (var dirOp in deleteDirs)
            {
                cts.Token.ThrowIfCancellationRequested();
                WaitIfPaused(pair.Id);

                var targetProv = dirOp.TargetSide == EndpointSide.Destination ? destinationProvider : sourceProvider;
                try
                {
                    await targetProv.DeleteFolderAsync(dirOp.RelativePath, cts.Token).ConfigureAwait(false);
                    dirOp.Status = OperationStatus.Completed;
                    completedOperations.Add(dirOp);
                }
                catch (Exception ex)
                {
                    dirOp.Status = OperationStatus.Failed;
                    dirOp.ErrorMessage = ex.Message;
                }
            }

            // Persist journal entries and snapshots
            await _journalStore.LogOperationsBatchAsync(completedOperations, CancellationToken.None).ConfigureAwait(false);
            
            // For deleted files, remove from snapshots; for created/updated, save new snapshots
            foreach (var snap in newSnapshots)
            {
                await _stateStore.SaveSnapshotAsync(snap, CancellationToken.None).ConfigureAwait(false);
            }
            foreach (var delOp in completedOperations.Where(o => o.ActionType == SyncActionType.Delete))
            {
                await _stateStore.DeleteSnapshotAsync(pair.Id, delOp.RelativePath, CancellationToken.None).ConfigureAwait(false);
            }

            result.FilesCreated = filesCreated;
            result.FilesUpdated = filesUpdated;
            result.FilesDeleted = filesDeleted;
            result.BytesTransferred = totalTransferred;
            result.Success = result.ErrorsCount == 0;
            result.CompletedAtUtc = DateTimeOffset.UtcNow;

            _logger.LogInformation("Sync finished for pair '{PairName}': {Result}", pair.Name, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            result.Success = false;
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            _logger.LogWarning("Sync cancelled for pair '{PairName}'", pair.Name);
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.ErrorMessages.Add(ex.Message);
            result.ErrorsCount++;
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            _logger.LogError(ex, "Sync failed unexpectedly for pair '{PairName}'", pair.Name);
            return result;
        }
        finally
        {
            _runningCts.TryRemove(pair.Id, out _);
            _pauseEvents.TryRemove(pair.Id, out _);
            pairLock.Release();
        }
    }

    private async Task ExecuteSingleOperationWithRetryAsync(
        SyncPair pair,
        SyncOperation op,
        ISyncProvider sourceProvider,
        ISyncProvider destinationProvider,
        CancellationToken ct)
    {
        int maxRetries = 3;
        int delayMs = 500;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                op.Status = OperationStatus.Executing;

                var srcProv = op.SourceSide == EndpointSide.Source ? sourceProvider : destinationProvider;
                var tgtProv = op.TargetSide == EndpointSide.Destination ? destinationProvider : sourceProvider;

                switch (op.ActionType)
                {
                    case SyncActionType.Create:
                    case SyncActionType.Update:
                        // Preserve previous version on target if updating
                        if (op.ActionType == SyncActionType.Update)
                        {
                            try
                            {
                                op.BackupVersionPath = await _versionManager.PreserveVersionBeforeOverwriteAsync(
                                    tgtProv.Endpoint.RootPath, op.RelativePath, ct).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Failed to preserve previous version of '{Path}'", op.RelativePath);
                            }
                        }

                        // Read from source and write to target
                        using (var readStream = await srcProv.OpenReadAsync(op.RelativePath, ct).ConfigureAwait(false))
                        {
                            var meta = await srcProv.GetItemMetadataAsync(op.RelativePath, ct).ConfigureAwait(false)
                                       ?? new SyncItemMetadata { SizeBytes = op.BytesTransferred, LastModifiedUtc = DateTimeOffset.UtcNow };

                            await tgtProv.WriteFileAsync(op.RelativePath, readStream, meta, null, ct).ConfigureAwait(false);
                        }

                        // Checksum verification if hash is available
                        if (!string.IsNullOrEmpty(op.SourceHash))
                        {
                            var tgtMeta = await tgtProv.GetItemMetadataAsync(op.RelativePath, ct).ConfigureAwait(false);
                            if (tgtMeta != null && !string.IsNullOrEmpty(tgtMeta.ContentHashSha256))
                            {
                                if (!string.Equals(op.SourceHash, tgtMeta.ContentHashSha256, StringComparison.OrdinalIgnoreCase))
                                {
                                    throw new IOException($"Checksum mismatch after transfer for '{op.RelativePath}'. Expected: {op.SourceHash}, Actual: {tgtMeta.ContentHashSha256}");
                                }
                            }
                        }

                        op.Status = OperationStatus.Completed;
                        return;

                    case SyncActionType.Delete:
                        // Deletion policy handling
                        if (pair.DeletionPolicy == DeletionPolicy.MoveToDeletedItems)
                        {
                            op.BackupVersionPath = await _versionManager.MoveToDeletedItemsAsync(
                                tgtProv.Endpoint.RootPath, op.RelativePath, ct).ConfigureAwait(false);
                        }
                        else if (pair.DeletionPolicy == DeletionPolicy.PermanentDelete)
                        {
                            // Still preserve backup version just in case
                            op.BackupVersionPath = await _versionManager.PreserveVersionBeforeOverwriteAsync(
                                tgtProv.Endpoint.RootPath, op.RelativePath, ct).ConfigureAwait(false);
                            await tgtProv.DeleteFileAsync(op.RelativePath, ct).ConfigureAwait(false);
                        }

                        op.Status = OperationStatus.Completed;
                        return;

                    case SyncActionType.Rename:
                        if (!string.IsNullOrEmpty(op.NewRelativePath))
                        {
                            await tgtProv.MoveOrRenameAsync(op.RelativePath, op.NewRelativePath, ct).ConfigureAwait(false);
                            op.Status = OperationStatus.Completed;
                            return;
                        }
                        break;
                }

                op.Status = OperationStatus.Completed;
                return;
            }
            catch (Exception ex) when (attempt < maxRetries && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Transient error on attempt {Attempt}/{MaxRetries} for '{Path}'. Retrying in {DelayMs}ms...", attempt, maxRetries, op.RelativePath, delayMs);
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
                delayMs *= 2; // exponential backoff
            }
            catch (Exception ex)
            {
                op.Status = OperationStatus.Failed;
                op.ErrorMessage = ex.Message;
                _logger.LogError(ex, "Operation {OpType} failed for '{Path}' after {Attempts} attempts", op.ActionType, op.RelativePath, attempt);
                return;
            }
        }
    }

    private void WaitIfPaused(string pairId)
    {
        if (_pauseEvents.TryGetValue(pairId, out var ev))
        {
            ev.Wait();
        }
    }

    public void Pause(string pairId)
    {
        if (_pauseEvents.TryGetValue(pairId, out var ev))
        {
            ev.Reset();
            _logger.LogInformation("Pair '{PairId}' paused", pairId);
        }
    }

    public void Resume(string pairId)
    {
        if (_pauseEvents.TryGetValue(pairId, out var ev))
        {
            ev.Set();
            _logger.LogInformation("Pair '{PairId}' resumed", pairId);
        }
    }

    public void Cancel(string pairId)
    {
        if (_runningCts.TryGetValue(pairId, out var cts))
        {
            cts.Cancel();
            _logger.LogInformation("Pair '{PairId}' cancellation requested", pairId);
        }
    }

    public bool IsRunning(string pairId) => _runningCts.ContainsKey(pairId);

    public bool IsPaused(string pairId)
    {
        if (_pauseEvents.TryGetValue(pairId, out var ev))
        {
            return !ev.IsSet;
        }
        return false;
    }
}
