using System;
using System.Threading;
using System.Threading.Tasks;
using DriveSync.Core.Models;

namespace DriveSync.Core.Engine;

public interface ISyncEngine
{
    Task<SyncPlan> AnalyzeAsync(
        SyncPair pair,
        ISyncProvider sourceProvider,
        ISyncProvider destinationProvider,
        bool isDryRun = false,
        CancellationToken ct = default);

    Task<SyncResult> ExecuteSyncAsync(
        SyncPair pair,
        ISyncProvider sourceProvider,
        ISyncProvider destinationProvider,
        SyncPlan plan,
        IProgress<SyncProgressReport>? progress = null,
        CancellationToken ct = default);

    void Pause(string pairId);
    void Resume(string pairId);
    void Cancel(string pairId);
    bool IsRunning(string pairId);
    bool IsPaused(string pairId);
}
