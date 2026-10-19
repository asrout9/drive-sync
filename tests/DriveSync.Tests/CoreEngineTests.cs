using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DriveSync.Core.Engine;
using DriveSync.Core.Models;
using DriveSync.Core.Security;
using DriveSync.Core.Storage;
using DriveSync.Core.Versioning;
using Xunit;

namespace DriveSync.Tests;

public class CoreEngineTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _leftRoot;
    private readonly string _rightRoot;
    private readonly string _dbPath;
    private readonly SqliteSyncStateStore _stateStore;
    private readonly SqliteJournalStore _journalStore;
    private readonly VersionManager _versionManager;
    private readonly ThreeWayConflictResolver _conflictResolver;

    public CoreEngineTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "DriveSyncTests_" + Guid.NewGuid().ToString("N"));
        _leftRoot = Path.Combine(_testRoot, "Left");
        _rightRoot = Path.Combine(_testRoot, "Right");
        _dbPath = Path.Combine(_testRoot, "state.db");

        Directory.CreateDirectory(_leftRoot);
        Directory.CreateDirectory(_rightRoot);

        _stateStore = new SqliteSyncStateStore(_dbPath);
        _stateStore.InitializeAsync().GetAwaiter().GetResult();

        _journalStore = new SqliteJournalStore(_dbPath);
        _journalStore.InitializeAsync().GetAwaiter().GetResult();

        _versionManager = new VersionManager();
        _conflictResolver = new ThreeWayConflictResolver();
    }

    public void Dispose()
    {
        _stateStore.Dispose();
        _journalStore.Dispose();
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public void PathSafety_ValidatesAndRejectsPathTraversal()
    {
        Assert.Throws<UnauthorizedAccessException>(() => PathSafety.NormalizeRelativePath("../secret.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => PathSafety.NormalizeRelativePath("foo/../../secret.txt"));
        Assert.Throws<InvalidOperationException>(() => PathSafety.NormalizeRelativePath("CON.txt"));
        Assert.Throws<InvalidOperationException>(() => PathSafety.NormalizeRelativePath("folder/AUX"));

        string valid = PathSafety.NormalizeRelativePath("folder\\subfolder/file.txt");
        Assert.Equal("folder/subfolder/file.txt", valid);
    }

    [Fact]
    public void ThreeWayReconciliation_DetectsOneSidedNewFile()
    {
        var pair = CreateTestPair();
        var sourceItems = new Dictionary<string, SyncItem>
        {
            ["file1.txt"] = new SyncItem { RelativePath = "file1.txt", SizeBytes = 100, ContentHashSha256 = "hash1" }
        };
        var destItems = new Dictionary<string, SyncItem>();
        var baseSnapshots = new Dictionary<string, SyncSnapshot>();

        var plan = _conflictResolver.EvaluateSyncPlan(pair, sourceItems, destItems, baseSnapshots, isDryRun: false);

        Assert.Single(plan.Operations);
        Assert.Empty(plan.Conflicts);
        Assert.Equal(SyncActionType.Create, plan.Operations[0].ActionType);
        Assert.Equal(EndpointSide.Source, plan.Operations[0].SourceSide);
        Assert.Equal(EndpointSide.Destination, plan.Operations[0].TargetSide);
        Assert.Equal("file1.txt", plan.Operations[0].RelativePath);
    }

    [Fact]
    public void ThreeWayReconciliation_DetectsOneSidedModification()
    {
        var pair = CreateTestPair();
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-10);

        var baseSnapshots = new Dictionary<string, SyncSnapshot>
        {
            ["doc.pdf"] = new SyncSnapshot
            {
                PairId = pair.Id,
                RelativePath = "doc.pdf",
                SizeBytes = 500,
                ContentHashSha256 = "basehash",
                LastModifiedUtc = baseTime,
                LastSyncedUtc = baseTime
            }
        };

        // Source modified, Destination unchanged from base
        var sourceItems = new Dictionary<string, SyncItem>
        {
            ["doc.pdf"] = new SyncItem
            {
                RelativePath = "doc.pdf",
                SizeBytes = 800,
                ContentHashSha256 = "newhash",
                LastModifiedUtc = DateTimeOffset.UtcNow
            }
        };

        var destItems = new Dictionary<string, SyncItem>
        {
            ["doc.pdf"] = new SyncItem
            {
                RelativePath = "doc.pdf",
                SizeBytes = 500,
                ContentHashSha256 = "basehash",
                LastModifiedUtc = baseTime
            }
        };

        var plan = _conflictResolver.EvaluateSyncPlan(pair, sourceItems, destItems, baseSnapshots, isDryRun: false);

        Assert.Single(plan.Operations);
        Assert.Empty(plan.Conflicts);
        Assert.Equal(SyncActionType.Update, plan.Operations[0].ActionType);
        Assert.Equal(EndpointSide.Source, plan.Operations[0].SourceSide);
        Assert.Equal(EndpointSide.Destination, plan.Operations[0].TargetSide);
    }

    [Fact]
    public void ThreeWayReconciliation_DetectsConcurrentModificationConflict()
    {
        var pair = CreateTestPair();
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-30);

        var baseSnapshots = new Dictionary<string, SyncSnapshot>
        {
            ["shared.docx"] = new SyncSnapshot
            {
                PairId = pair.Id,
                RelativePath = "shared.docx",
                SizeBytes = 1000,
                ContentHashSha256 = "basehash",
                LastModifiedUtc = baseTime,
                LastSyncedUtc = baseTime
            }
        };

        // Both sides modified since base with different contents
        var sourceItems = new Dictionary<string, SyncItem>
        {
            ["shared.docx"] = new SyncItem
            {
                RelativePath = "shared.docx",
                SizeBytes = 1200,
                ContentHashSha256 = "sourcehash",
                LastModifiedUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
            }
        };

        var destItems = new Dictionary<string, SyncItem>
        {
            ["shared.docx"] = new SyncItem
            {
                RelativePath = "shared.docx",
                SizeBytes = 1400,
                ContentHashSha256 = "desthash",
                LastModifiedUtc = DateTimeOffset.UtcNow.AddMinutes(-2)
            }
        };

        var plan = _conflictResolver.EvaluateSyncPlan(pair, sourceItems, destItems, baseSnapshots, isDryRun: false);

        Assert.Empty(plan.Operations);
        Assert.Single(plan.Conflicts);
        Assert.Equal("shared.docx", plan.Conflicts[0].RelativePath);
        Assert.Contains("Conflicting edits", plan.Conflicts[0].ConflictReason);
    }

    [Fact]
    public void ThreeWayReconciliation_DetectsDeleteVsModifyConflict()
    {
        var pair = CreateTestPair();
        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-30);

        var baseSnapshots = new Dictionary<string, SyncSnapshot>
        {
            ["notes.txt"] = new SyncSnapshot
            {
                PairId = pair.Id,
                RelativePath = "notes.txt",
                SizeBytes = 200,
                ContentHashSha256 = "basehash",
                LastModifiedUtc = baseTime,
                LastSyncedUtc = baseTime
            }
        };

        // Source deleted the file, but Destination modified it!
        var sourceItems = new Dictionary<string, SyncItem>();
        var destItems = new Dictionary<string, SyncItem>
        {
            ["notes.txt"] = new SyncItem
            {
                RelativePath = "notes.txt",
                SizeBytes = 350,
                ContentHashSha256 = "modifiedhash",
                LastModifiedUtc = DateTimeOffset.UtcNow
            }
        };

        var plan = _conflictResolver.EvaluateSyncPlan(pair, sourceItems, destItems, baseSnapshots, isDryRun: false);

        Assert.Empty(plan.Operations);
        Assert.Single(plan.Conflicts);
        Assert.Contains("Delete vs Modify conflict", plan.Conflicts[0].ConflictReason);
    }

    [Fact]
    public void ConflictResolution_KeepBothWithRename_GeneratesRenamedOperations()
    {
        var pair = CreateTestPair();
        var conflict = new SyncConflict
        {
            PairId = pair.Id,
            RelativePath = "report.xlsx",
            SourceItem = new SyncItem { RelativePath = "report.xlsx", SizeBytes = 2000, ContentHashSha256 = "h1" },
            DestinationItem = new SyncItem { RelativePath = "report.xlsx", SizeBytes = 2500, ContentHashSha256 = "h2" }
        };

        var ops = _conflictResolver.ResolveConflict(pair, conflict, ConflictResolutionChoice.KeepBothWithRename);

        Assert.Equal(3, ops.Count);
        // 1. Rename destination to conflicted copy
        Assert.Equal(SyncActionType.Rename, ops[0].ActionType);
        Assert.Contains("Conflicted copy", ops[0].NewRelativePath);
        // 2. Copy the conflicted copy to source
        Assert.Equal(SyncActionType.Create, ops[1].ActionType);
        // 3. Copy original source version to destination
        Assert.Equal(SyncActionType.Create, ops[2].ActionType);
        Assert.Equal("report.xlsx", ops[2].RelativePath);
    }

    [Fact]
    public async Task VersionManager_PreservesPreviousVersionBeforeOverwrite()
    {
        string filePath = Path.Combine(_leftRoot, "important.txt");
        await File.WriteAllTextAsync(filePath, "Original content 12345");

        string? versionPath = await _versionManager.PreserveVersionBeforeOverwriteAsync(_leftRoot, "important.txt");

        Assert.NotNull(versionPath);
        Assert.True(File.Exists(versionPath));
        string saved = await File.ReadAllTextAsync(versionPath);
        Assert.Equal("Original content 12345", saved);

        // Verify version file is inside .drivesync/versions
        Assert.Contains(PathSafety.DriveSyncMetaFolder, versionPath);
        Assert.Contains("versions", versionPath);
    }

    [Fact]
    public async Task VersionManager_MovesDeletedItemToRecoverableDatedArea()
    {
        string filePath = Path.Combine(_rightRoot, "todelete.txt");
        await File.WriteAllTextAsync(filePath, "To be safely deleted");

        string? deletedPath = await _versionManager.MoveToDeletedItemsAsync(_rightRoot, "todelete.txt");

        Assert.NotNull(deletedPath);
        Assert.False(File.Exists(filePath)); // Original removed from active workspace
        Assert.True(File.Exists(deletedPath)); // Preserved in .drivesync/deleted/
        Assert.Contains("deleted", deletedPath);
        string recovered = await File.ReadAllTextAsync(deletedPath);
        Assert.Equal("To be safely deleted", recovered);
    }

    [Fact]
    public async Task VersionManager_UndoSafetyChecks_RejectsWhenFileWasExternallyModified()
    {
        string filePath = Path.Combine(_leftRoot, "work.txt");
        await File.WriteAllTextAsync(filePath, "Initial Version");
        string originalHash = await VersionManager.ComputeSha256Async(filePath);

        var op = new SyncOperation
        {
            PairId = "pair1",
            RelativePath = "work.txt",
            ActionType = SyncActionType.Create,
            SourceSide = EndpointSide.Destination,
            TargetSide = EndpointSide.Source,
            SourceHash = originalHash,
            TargetHash = originalHash
        };

        // Valid undo initially
        bool canUndo = await _versionManager.CanUndoOperationAsync(_leftRoot, op);
        Assert.True(canUndo);

        // User modifies file externally
        await File.WriteAllTextAsync(filePath, "Externally Modified by User!");

        // Undo MUST be rejected to protect user's new work!
        bool canUndoAfterEdit = await _versionManager.CanUndoOperationAsync(_leftRoot, op);
        Assert.False(canUndoAfterEdit);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _versionManager.UndoOperationAsync(_leftRoot, op));
    }

    [Fact]
    public async Task SqliteStateStore_PersistsSnapshotsAndConflictsDurably()
    {
        string pairId = Guid.NewGuid().ToString("N");
        var snapshot = new SyncSnapshot
        {
            PairId = pairId,
            RelativePath = "folder/file.bin",
            SizeBytes = 4096,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            ContentHashSha256 = "sha256abc123",
            LastSyncedUtc = DateTimeOffset.UtcNow
        };

        await _stateStore.SaveSnapshotAsync(snapshot);

        var loaded = await _stateStore.GetSnapshotAsync(pairId, "folder/file.bin");
        Assert.NotNull(loaded);
        Assert.Equal("sha256abc123", loaded.ContentHashSha256);
        Assert.Equal(4096, loaded.SizeBytes);

        // Conflict persistence
        var conflict = new SyncConflict
        {
            PairId = pairId,
            RelativePath = "folder/file.bin",
            ConflictReason = "Test conflict",
            Resolution = ConflictResolutionChoice.Unresolved
        };
        await _stateStore.SaveConflictAsync(conflict);

        var conflicts = await _stateStore.GetConflictsAsync(pairId, unresolvedOnly: true);
        Assert.Single(conflicts);
        Assert.Equal("Test conflict", conflicts[0].ConflictReason);

        await _stateStore.ResolveConflictAsync(conflict.Id, ConflictResolutionChoice.KeepSource, "User chose left");
        var resolvedList = await _stateStore.GetConflictsAsync(pairId, unresolvedOnly: false);
        Assert.Equal(ConflictResolutionChoice.KeepSource, resolvedList[0].Resolution);
    }

    [Fact]
    public async Task SqliteJournalStore_LogsAndQueriesDailySummaries()
    {
        string pairId = "pair-summary";
        var op1 = new SyncOperation
        {
            PairId = pairId,
            RelativePath = "pic1.jpg",
            ActionType = SyncActionType.Create,
            BytesTransferred = 50000,
            Status = OperationStatus.Completed
        };
        var op2 = new SyncOperation
        {
            PairId = pairId,
            RelativePath = "pic2.jpg",
            ActionType = SyncActionType.Update,
            BytesTransferred = 75000,
            Status = OperationStatus.Completed
        };

        await _journalStore.LogOperationsBatchAsync(new[] { op1, op2 });

        var summaries = await _journalStore.GetDailySummariesAsync(pairId, days: 7);
        Assert.NotEmpty(summaries);
        Assert.Equal(1, summaries[0].FilesCreated);
        Assert.Equal(1, summaries[0].FilesUpdated);
        Assert.Equal(125000, summaries[0].TotalBytesTransferred);

        string csv = await _journalStore.ExportSanitizedReportAsync(new JournalFilter { PairId = pairId });
        Assert.Contains("pic1.jpg", csv);
        Assert.Contains("pic2.jpg", csv);
    }

    private static SyncPair CreateTestPair()
    {
        return new SyncPair
        {
            Id = "pair-1",
            Name = "Test Pair",
            Source = new SyncEndpoint { DisplayName = "Source", RootPath = "C:\\Source" },
            Destination = new SyncEndpoint { DisplayName = "Destination", RootPath = "D:\\Destination" },
            Direction = SyncDirection.TwoWay,
            DeletionPolicy = DeletionPolicy.MoveToDeletedItems,
            ConflictPolicy = ConflictPolicy.ManualReview
        };
    }
}
