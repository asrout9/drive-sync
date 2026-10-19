using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DriveSync.Core.Models;

namespace DriveSync.Core.Engine;

public class ThreeWayConflictResolver : IConflictResolver
{
    public SyncPlan EvaluateSyncPlan(
        SyncPair pair,
        IReadOnlyDictionary<string, SyncItem> sourceItems,
        IReadOnlyDictionary<string, SyncItem> destinationItems,
        IReadOnlyDictionary<string, SyncSnapshot> baseSnapshots,
        bool isDryRun)
    {
        var plan = new SyncPlan
        {
            PairId = pair.Id,
            IsDryRun = isDryRun,
            GeneratedAtUtc = DateTimeOffset.UtcNow
        };

        // Gather all distinct relative paths across source, destination, and base snapshots
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in sourceItems.Keys) allPaths.Add(p);
        foreach (var p in destinationItems.Keys) allPaths.Add(p);
        foreach (var p in baseSnapshots.Keys) allPaths.Add(p);

        // Sort so directories are created before files and deleted after files
        var sortedPaths = allPaths.OrderBy(p => p.Count(c => c == '/')).ToList();

        foreach (var path in sortedPaths)
        {
            sourceItems.TryGetValue(path, out var left);
            destinationItems.TryGetValue(path, out var right);
            baseSnapshots.TryGetValue(path, out var @base);

            // CASE 1: No previous common snapshot exists (first sync or new file)
            if (@base == null)
            {
                if (left != null && right == null)
                {
                    // Exists only on Left -> Propagate Left to Right
                    plan.Operations.Add(new SyncOperation
                    {
                        PairId = pair.Id,
                        RelativePath = path,
                        ActionType = SyncActionType.Create,
                        SourceSide = EndpointSide.Source,
                        TargetSide = EndpointSide.Destination,
                        IsDirectory = left.IsDirectory,
                        BytesTransferred = left.SizeBytes,
                        SourceHash = left.ContentHashSha256
                    });
                }
                else if (left == null && right != null)
                {
                    // Exists only on Right -> Propagate Right to Left
                    plan.Operations.Add(new SyncOperation
                    {
                        PairId = pair.Id,
                        RelativePath = path,
                        ActionType = SyncActionType.Create,
                        SourceSide = EndpointSide.Destination,
                        TargetSide = EndpointSide.Source,
                        IsDirectory = right.IsDirectory,
                        BytesTransferred = right.SizeBytes,
                        SourceHash = right.ContentHashSha256
                    });
                }
                else if (left != null && right != null)
                {
                    // Exists on both sides without prior common state!
                    if (left.IsDirectory && right.IsDirectory)
                    {
                        // Directory on both sides - safe convergence
                        continue;
                    }

                    bool identical = AreItemsIdentical(left, right);
                    if (identical)
                    {
                        // Converged! Both sides already match.
                        // We will record snapshot during execution without transferring data.
                    }
                    else
                    {
                        // Conflict: Two different files with the same name created independently
                        var conflict = new SyncConflict
                        {
                            PairId = pair.Id,
                            RelativePath = path,
                            SourceItem = left,
                            DestinationItem = right,
                            BaseSnapshot = null,
                            ConflictReason = $"File created independently on both '{pair.Source.DisplayName}' ({left.SizeBytes} B) and '{pair.Destination.DisplayName}' ({right.SizeBytes} B) before synchronization."
                        };
                        plan.Conflicts.Add(conflict);
                    }
                }
                continue;
            }

            // CASE 2: Previous common snapshot exists (@base != null)
            bool leftExists = left != null;
            bool rightExists = right != null;

            if (leftExists && rightExists)
            {
                if (left!.IsDirectory && right!.IsDirectory)
                {
                    continue;
                }

                bool leftModified = IsModifiedFromBase(left, @base);
                bool rightModified = IsModifiedFromBase(right!, @base);

                if (!leftModified && !rightModified)
                {
                    // Neither side changed - no action needed
                    continue;
                }

                if (leftModified && !rightModified)
                {
                    // Modified only on Left -> Propagate Left to Right
                    plan.Operations.Add(new SyncOperation
                    {
                        PairId = pair.Id,
                        RelativePath = path,
                        ActionType = SyncActionType.Update,
                        SourceSide = EndpointSide.Source,
                        TargetSide = EndpointSide.Destination,
                        IsDirectory = left.IsDirectory,
                        BytesTransferred = left.SizeBytes,
                        SourceHash = left.ContentHashSha256,
                        TargetHash = right!.ContentHashSha256
                    });
                }
                else if (!leftModified && rightModified)
                {
                    // Modified only on Right -> Propagate Right to Left
                    plan.Operations.Add(new SyncOperation
                    {
                        PairId = pair.Id,
                        RelativePath = path,
                        ActionType = SyncActionType.Update,
                        SourceSide = EndpointSide.Destination,
                        TargetSide = EndpointSide.Source,
                        IsDirectory = right!.IsDirectory,
                        BytesTransferred = right.SizeBytes,
                        SourceHash = right.ContentHashSha256,
                        TargetHash = left.ContentHashSha256
                    });
                }
                else // leftModified && rightModified
                {
                    // Both sides changed! Check if they changed to the exact same content
                    if (AreItemsIdentical(left, right!))
                    {
                        // Converged independently to identical content
                        continue;
                    }

                    // Genuine Conflict: both modified concurrently
                    var conflict = new SyncConflict
                    {
                        PairId = pair.Id,
                        RelativePath = path,
                        SourceItem = left,
                        DestinationItem = right,
                        BaseSnapshot = @base,
                        ConflictReason = $"Conflicting edits: modified on '{pair.Source.DisplayName}' ({left.LastModifiedUtc:g}, {left.SizeBytes} B) and on '{pair.Destination.DisplayName}' ({right!.LastModifiedUtc:g}, {right.SizeBytes} B) since last common version ({@base.LastSyncedUtc:g})."
                    };
                    plan.Conflicts.Add(conflict);
                }
            }
            else if (leftExists && !rightExists)
            {
                // Item is on Left, but missing on Right
                bool leftModified = IsModifiedFromBase(left!, @base);
                if (leftModified)
                {
                    // Conflict: Right deleted the item, but Left modified it!
                    var conflict = new SyncConflict
                    {
                        PairId = pair.Id,
                        RelativePath = path,
                        SourceItem = left,
                        DestinationItem = null,
                        BaseSnapshot = @base,
                        ConflictReason = $"Delete vs Modify conflict: item was deleted on '{pair.Destination.DisplayName}', but concurrently modified on '{pair.Source.DisplayName}'."
                    };
                    plan.Conflicts.Add(conflict);
                }
                else
                {
                    // Right deliberately deleted it, Left was not modified
                    if (pair.DeletionPolicy != DeletionPolicy.NeverDelete)
                    {
                        plan.Operations.Add(new SyncOperation
                        {
                            PairId = pair.Id,
                            RelativePath = path,
                            ActionType = SyncActionType.Delete,
                            SourceSide = EndpointSide.Destination,
                            TargetSide = EndpointSide.Source,
                            IsDirectory = left!.IsDirectory,
                            BytesTransferred = 0,
                            SourceHash = left.ContentHashSha256
                        });
                    }
                }
            }
            else if (!leftExists && rightExists)
            {
                // Item is missing on Left, but present on Right
                bool rightModified = IsModifiedFromBase(right!, @base);
                if (rightModified)
                {
                    // Conflict: Left deleted the item, but Right modified it!
                    var conflict = new SyncConflict
                    {
                        PairId = pair.Id,
                        RelativePath = path,
                        SourceItem = null,
                        DestinationItem = right,
                        BaseSnapshot = @base,
                        ConflictReason = $"Delete vs Modify conflict: item was deleted on '{pair.Source.DisplayName}', but concurrently modified on '{pair.Destination.DisplayName}'."
                    };
                    plan.Conflicts.Add(conflict);
                }
                else
                {
                    // Left deliberately deleted it, Right was not modified
                    if (pair.DeletionPolicy != DeletionPolicy.NeverDelete)
                    {
                        plan.Operations.Add(new SyncOperation
                        {
                            PairId = pair.Id,
                            RelativePath = path,
                            ActionType = SyncActionType.Delete,
                            SourceSide = EndpointSide.Source,
                            TargetSide = EndpointSide.Destination,
                            IsDirectory = right!.IsDirectory,
                            BytesTransferred = 0,
                            SourceHash = right.ContentHashSha256
                        });
                    }
                }
            }
            else // !leftExists && !rightExists
            {
                // Deleted on both sides! The base snapshot should simply be purged.
                // No sync operation needed.
            }
        }

        return plan;
    }

    public IReadOnlyList<SyncOperation> ResolveConflict(
        SyncPair pair,
        SyncConflict conflict,
        ConflictResolutionChoice choice)
    {
        var ops = new List<SyncOperation>();
        string path = conflict.RelativePath;

        switch (choice)
        {
            case ConflictResolutionChoice.KeepSource:
                if (conflict.SourceItem != null)
                {
                    ops.Add(new SyncOperation
                    {
                        PairId = pair.Id,
                        RelativePath = path,
                        ActionType = conflict.DestinationItem != null ? SyncActionType.Update : SyncActionType.Create,
                        SourceSide = EndpointSide.Source,
                        TargetSide = EndpointSide.Destination,
                        IsDirectory = conflict.SourceItem.IsDirectory,
                        BytesTransferred = conflict.SourceItem.SizeBytes,
                        SourceHash = conflict.SourceItem.ContentHashSha256
                    });
                }
                else if (conflict.DestinationItem != null)
                {
                    // Source deleted it, keep source means delete on destination
                    ops.Add(new SyncOperation
                    {
                        PairId = pair.Id,
                        RelativePath = path,
                        ActionType = SyncActionType.Delete,
                        SourceSide = EndpointSide.Source,
                        TargetSide = EndpointSide.Destination,
                        IsDirectory = conflict.DestinationItem.IsDirectory,
                        BytesTransferred = 0
                    });
                }
                break;

            case ConflictResolutionChoice.KeepDestination:
                if (conflict.DestinationItem != null)
                {
                    ops.Add(new SyncOperation
                    {
                        PairId = pair.Id,
                        RelativePath = path,
                        ActionType = conflict.SourceItem != null ? SyncActionType.Update : SyncActionType.Create,
                        SourceSide = EndpointSide.Destination,
                        TargetSide = EndpointSide.Source,
                        IsDirectory = conflict.DestinationItem.IsDirectory,
                        BytesTransferred = conflict.DestinationItem.SizeBytes,
                        SourceHash = conflict.DestinationItem.ContentHashSha256
                    });
                }
                else if (conflict.SourceItem != null)
                {
                    // Destination deleted it, keep destination means delete on source
                    ops.Add(new SyncOperation
                    {
                        PairId = pair.Id,
                        RelativePath = path,
                        ActionType = SyncActionType.Delete,
                        SourceSide = EndpointSide.Destination,
                        TargetSide = EndpointSide.Source,
                        IsDirectory = conflict.SourceItem.IsDirectory,
                        BytesTransferred = 0
                    });
                }
                break;

            case ConflictResolutionChoice.KeepBothWithRename:
                if (conflict.SourceItem != null && conflict.DestinationItem != null)
                {
                    // Rename destination copy with timestamp and provider label
                    string ext = Path.GetExtension(path);
                    string dir = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
                    string baseName = Path.GetFileNameWithoutExtension(path);
                    string timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd_HHmm");
                    string renamedFileName = $"{baseName} (Conflicted copy {pair.Destination.DisplayName} {timestamp}){ext}";
                    string newRelPath = string.IsNullOrEmpty(dir) ? renamedFileName : $"{dir}/{renamedFileName}";

                    // Rename destination item to the conflicted copy
                    ops.Add(new SyncOperation
                    {
                        PairId = pair.Id,
                        RelativePath = path,
                        NewRelativePath = newRelPath,
                        ActionType = SyncActionType.Rename,
                        SourceSide = EndpointSide.Destination,
                        TargetSide = EndpointSide.Destination,
                        IsDirectory = conflict.DestinationItem.IsDirectory
                    });

                    // Also copy the renamed file to the source so both sides have both copies!
                    ops.Add(new SyncOperation
                    {
                        PairId = pair.Id,
                        RelativePath = newRelPath,
                        ActionType = SyncActionType.Create,
                        SourceSide = EndpointSide.Destination,
                        TargetSide = EndpointSide.Source,
                        IsDirectory = conflict.DestinationItem.IsDirectory,
                        BytesTransferred = conflict.DestinationItem.SizeBytes,
                        SourceHash = conflict.DestinationItem.ContentHashSha256
                    });

                    // Propagate the original source version to the destination
                    ops.Add(new SyncOperation
                    {
                        PairId = pair.Id,
                        RelativePath = path,
                        ActionType = SyncActionType.Create,
                        SourceSide = EndpointSide.Source,
                        TargetSide = EndpointSide.Destination,
                        IsDirectory = conflict.SourceItem.IsDirectory,
                        BytesTransferred = conflict.SourceItem.SizeBytes,
                        SourceHash = conflict.SourceItem.ContentHashSha256
                    });
                }
                break;

            case ConflictResolutionChoice.Manual:
            default:
                // Left for interactive review
                break;
        }

        return ops;
    }

    private static bool IsModifiedFromBase(SyncItem current, SyncSnapshot @base)
    {
        if (current.SizeBytes != @base.SizeBytes) return true;
        if (!string.IsNullOrEmpty(current.ContentHashSha256) && !string.IsNullOrEmpty(@base.ContentHashSha256))
        {
            return !string.Equals(current.ContentHashSha256, @base.ContentHashSha256, StringComparison.OrdinalIgnoreCase);
        }
        // Use 2-second timestamp tolerance for FAT32 / network clock skew
        return Math.Abs((current.LastModifiedUtc - @base.LastModifiedUtc).TotalSeconds) > 2.0;
    }

    private static bool AreItemsIdentical(SyncItem item1, SyncItem item2)
    {
        if (item1.IsDirectory != item2.IsDirectory) return false;
        if (item1.IsDirectory) return true;
        if (item1.SizeBytes != item2.SizeBytes) return false;
        if (!string.IsNullOrEmpty(item1.ContentHashSha256) && !string.IsNullOrEmpty(item2.ContentHashSha256))
        {
            return string.Equals(item1.ContentHashSha256, item2.ContentHashSha256, StringComparison.OrdinalIgnoreCase);
        }
        return Math.Abs((item1.LastModifiedUtc - item2.LastModifiedUtc).TotalSeconds) <= 2.0;
    }
}
