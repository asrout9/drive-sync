using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DriveSync.Core.Models;

namespace DriveSync.Core.Storage;

public interface ISyncStateStore
{
    Task InitializeAsync(CancellationToken ct = default);

    // Sync Pair definitions
    Task<IReadOnlyList<SyncPair>> GetAllPairsAsync(CancellationToken ct = default);
    Task<SyncPair?> GetPairAsync(string pairId, CancellationToken ct = default);
    Task SavePairAsync(SyncPair pair, CancellationToken ct = default);
    Task DeletePairAsync(string pairId, CancellationToken ct = default);

    // Snapshots (Last Common State)
    Task<IReadOnlyDictionary<string, SyncSnapshot>> GetSnapshotsAsync(string pairId, CancellationToken ct = default);
    Task<SyncSnapshot?> GetSnapshotAsync(string pairId, string relativePath, CancellationToken ct = default);
    Task SaveSnapshotAsync(SyncSnapshot snapshot, CancellationToken ct = default);
    Task SaveSnapshotsBatchAsync(string pairId, IEnumerable<SyncSnapshot> snapshots, CancellationToken ct = default);
    Task DeleteSnapshotAsync(string pairId, string relativePath, CancellationToken ct = default);
    Task ClearSnapshotsAsync(string pairId, CancellationToken ct = default);

    // Conflicts
    Task<IReadOnlyList<SyncConflict>> GetConflictsAsync(string? pairId = null, bool unresolvedOnly = true, CancellationToken ct = default);
    Task<SyncConflict?> GetConflictAsync(string conflictId, CancellationToken ct = default);
    Task SaveConflictAsync(SyncConflict conflict, CancellationToken ct = default);
    Task ResolveConflictAsync(string conflictId, ConflictResolutionChoice choice, string? notes = null, CancellationToken ct = default);
    Task DeleteConflictAsync(string conflictId, CancellationToken ct = default);

    // Provider Change cursors
    Task<string?> GetChangeCursorAsync(string pairId, EndpointSide side, CancellationToken ct = default);
    Task SaveChangeCursorAsync(string pairId, EndpointSide side, string cursor, CancellationToken ct = default);
}
