using System;

namespace DriveSync.Core.Models;

public class SyncPair
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Default Sync Pair";
    public SyncEndpoint Source { get; set; } = new();
    public SyncEndpoint Destination { get; set; } = new();
    
    // Explicitly Two-Way sync
    public SyncDirection Direction { get; set; } = SyncDirection.TwoWay;
    public string DirectionDescription => "Two-Way (Changes, renames, and deletions propagate between both sides)";

    public bool IsEnabled { get; set; } = true;
    public bool IsPaused { get; set; } = false;
    public bool IsDryRunOnly { get; set; } = false;
    public bool HasCompletedInitialSync { get; set; } = false;

    // Safety policies
    public DeletionPolicy DeletionPolicy { get; set; } = DeletionPolicy.MoveToDeletedItems;
    public ConflictPolicy ConflictPolicy { get; set; } = ConflictPolicy.ManualReview;
    public RetentionPolicy RetentionPolicy { get; set; } = new();

    // Automation
    public bool EnableRealtimeWatcher { get; set; } = true;
    public int ScheduledIntervalSeconds { get; set; } = 60; // 0 = disabled

    // Current Status
    public SyncStatus Status { get; set; } = SyncStatus.Idle;
    public DateTimeOffset? LastSyncTimeUtc { get; set; }
    public string? LastSyncSummary { get; set; }
    public int ActiveConflictCount { get; set; }
    public string? LastErrorMessage { get; set; }

    public override string ToString() => $"{Name}: {Source} <==> {Destination} ({Status})";
}
