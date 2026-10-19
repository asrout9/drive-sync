namespace DriveSync.Core.Models;

public enum SyncDirection
{
    TwoWay = 1
}

public enum ProviderType
{
    Local = 1,
    ExternalSsd = 2,
    OneDrive = 3,
    GoogleDrive = 4
}

public enum SyncActionType
{
    Create = 1,
    Update = 2,
    Delete = 3,
    Rename = 4,
    Conflict = 5,
    Skip = 6,
    Undo = 7
}

public enum EndpointSide
{
    Source = 1,      // Left side
    Destination = 2  // Right side
}

public enum ConflictResolutionChoice
{
    Unresolved = 0,
    KeepSource = 1,
    KeepDestination = 2,
    KeepBothWithRename = 3,
    Manual = 4
}

public enum DeletionPolicy
{
    MoveToDeletedItems = 1,  // Default safety: move to .drivesync/deleted/
    PermanentDelete = 2,
    NeverDelete = 3          // Ignore deletions
}

public enum ConflictPolicy
{
    ManualReview = 1,        // Default safety: stop and report conflict
    KeepSource = 2,
    KeepDestination = 3,
    KeepBothWithRename = 4
}

public enum SyncStatus
{
    Idle = 1,
    Scanning = 2,
    Syncing = 3,
    UpToDate = 4,
    ConflictsDetected = 5,
    Paused = 6,
    Offline = 7,
    AttentionRequired = 8,
    Error = 9
}

public enum OperationStatus
{
    Pending = 1,
    Executing = 2,
    Completed = 3,
    Failed = 4,
    Skipped = 5,
    Undone = 6
}
