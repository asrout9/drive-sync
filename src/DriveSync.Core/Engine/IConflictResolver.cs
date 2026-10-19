using System;
using System.Collections.Generic;
using DriveSync.Core.Models;

namespace DriveSync.Core.Engine;

public interface IConflictResolver
{
    SyncPlan EvaluateSyncPlan(
        SyncPair pair,
        IReadOnlyDictionary<string, SyncItem> sourceItems,
        IReadOnlyDictionary<string, SyncItem> destinationItems,
        IReadOnlyDictionary<string, SyncSnapshot> baseSnapshots,
        bool isDryRun);

    IReadOnlyList<SyncOperation> ResolveConflict(
        SyncPair pair,
        SyncConflict conflict,
        ConflictResolutionChoice choice);
}
