using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DriveSync.Core.Engine;
using DriveSync.Core.Models;
using DriveSync.Core.Storage;
using DriveSync.Core.Versioning;
using DriveSync.Providers;
using Xunit;

namespace DriveSync.Tests;

public class EndToEndSyncTests : IDisposable
{
    private readonly string _testBaseDir;
    private readonly string _sourceDir;
    private readonly string _destDir;
    private readonly string _dbPath;
    private readonly SqliteSyncStateStore _stateStore;
    private readonly SqliteJournalStore _journalStore;
    private readonly VersionManager _versionManager;
    private readonly ThreeWayConflictResolver _conflictResolver;
    private readonly TwoWaySyncEngine _engine;

    public EndToEndSyncTests()
    {
        _testBaseDir = Path.Combine(Path.GetTempPath(), "DriveSyncE2E_" + Guid.NewGuid().ToString("N"));
        _sourceDir = Path.Combine(_testBaseDir, "LocalSource");
        _destDir = Path.Combine(_testBaseDir, "ExternalSsdDest");
        _dbPath = Path.Combine(_testBaseDir, "state.db");

        Directory.CreateDirectory(_sourceDir);
        Directory.CreateDirectory(_destDir);

        _stateStore = new SqliteSyncStateStore(_dbPath);
        _stateStore.InitializeAsync().GetAwaiter().GetResult();

        _journalStore = new SqliteJournalStore(_dbPath);
        _journalStore.InitializeAsync().GetAwaiter().GetResult();

        _versionManager = new VersionManager();
        _conflictResolver = new ThreeWayConflictResolver();
        _engine = new TwoWaySyncEngine(_stateStore, _journalStore, _versionManager, _conflictResolver);
    }

    public void Dispose()
    {
        _stateStore.Dispose();
        _journalStore.Dispose();
        try
        {
            if (Directory.Exists(_testBaseDir))
            {
                Directory.Delete(_testBaseDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task EndToEnd_TwoWaySync_PropagatesCreationsBothWays()
    {
        var pair = CreatePair();

        // 1. Create file on Source
        await File.WriteAllTextAsync(Path.Combine(_sourceDir, "file_on_left.txt"), "Content from Left");

        using var srcProv1 = new FileSystemProvider(pair.Source);
        using var dstProv1 = new FileSystemProvider(pair.Destination);

        var plan1 = await _engine.AnalyzeAsync(pair, srcProv1, dstProv1);
        Assert.Single(plan1.Operations);
        Assert.Equal(SyncActionType.Create, plan1.Operations[0].ActionType);

        var result1 = await _engine.ExecuteSyncAsync(pair, srcProv1, dstProv1, plan1);
        Assert.True(result1.Success);
        Assert.True(File.Exists(Path.Combine(_destDir, "file_on_left.txt")));
        Assert.Equal("Content from Left", await File.ReadAllTextAsync(Path.Combine(_destDir, "file_on_left.txt")));

        // 2. Now create file on Destination (External SSD)
        await File.WriteAllTextAsync(Path.Combine(_destDir, "file_on_right.txt"), "Content from Right SSD");

        using var srcProv2 = new FileSystemProvider(pair.Source);
        using var dstProv2 = new FileSystemProvider(pair.Destination);

        var plan2 = await _engine.AnalyzeAsync(pair, srcProv2, dstProv2);
        Assert.Single(plan2.Operations);
        Assert.Equal(SyncActionType.Create, plan2.Operations[0].ActionType);
        Assert.Equal(EndpointSide.Destination, plan2.Operations[0].SourceSide);
        Assert.Equal(EndpointSide.Source, plan2.Operations[0].TargetSide);

        var result2 = await _engine.ExecuteSyncAsync(pair, srcProv2, dstProv2, plan2);
        Assert.True(result2.Success);
        Assert.True(File.Exists(Path.Combine(_sourceDir, "file_on_right.txt")));
        Assert.Equal("Content from Right SSD", await File.ReadAllTextAsync(Path.Combine(_sourceDir, "file_on_right.txt")));
    }

    [Fact]
    public async Task EndToEnd_TwoWaySync_ModificationsPropagateAndPreservePreviousVersions()
    {
        var pair = CreatePair();

        // Initial sync of a file
        string srcFile = Path.Combine(_sourceDir, "document.txt");
        string dstFile = Path.Combine(_destDir, "document.txt");
        await File.WriteAllTextAsync(srcFile, "Version 1.0 Original");

        using (var sp = new FileSystemProvider(pair.Source))
        using (var dp = new FileSystemProvider(pair.Destination))
        {
            var initialPlan = await _engine.AnalyzeAsync(pair, sp, dp);
            await _engine.ExecuteSyncAsync(pair, sp, dp, initialPlan);
        }

        Assert.Equal("Version 1.0 Original", await File.ReadAllTextAsync(dstFile));

        // Wait small delay so timestamp changes
        await Task.Delay(100);

        // Edit on Source
        await File.WriteAllTextAsync(srcFile, "Version 2.0 Edited on Source");

        using (var sp = new FileSystemProvider(pair.Source))
        using (var dp = new FileSystemProvider(pair.Destination))
        {
            var updatePlan = await _engine.AnalyzeAsync(pair, sp, dp);
            Assert.Single(updatePlan.Operations);
            Assert.Equal(SyncActionType.Update, updatePlan.Operations[0].ActionType);

            var updateResult = await _engine.ExecuteSyncAsync(pair, sp, dp, updatePlan);
            Assert.True(updateResult.Success);
            Assert.Equal("Version 2.0 Edited on Source", await File.ReadAllTextAsync(dstFile));

            // Verify previous version (Version 1.0) was preserved in version recovery!
            var recoveredItems = await _versionManager.GetRecoverableItemsAsync(_destDir);
            Assert.NotEmpty(recoveredItems);
            string backupContent = await File.ReadAllTextAsync(recoveredItems[0].ArchiveFullPath);
            Assert.Equal("Version 1.0 Original", backupContent);
        }
    }

    [Fact]
    public async Task EndToEnd_TwoWaySync_DeletionPolicyMovesToRecoverableArea()
    {
        var pair = CreatePair();
        pair.DeletionPolicy = DeletionPolicy.MoveToDeletedItems;

        string srcFile = Path.Combine(_sourceDir, "temp_data.csv");
        string dstFile = Path.Combine(_destDir, "temp_data.csv");
        await File.WriteAllTextAsync(srcFile, "col1,col2\n1,2");

        using (var sp = new FileSystemProvider(pair.Source))
        using (var dp = new FileSystemProvider(pair.Destination))
        {
            var plan = await _engine.AnalyzeAsync(pair, sp, dp);
            await _engine.ExecuteSyncAsync(pair, sp, dp, plan);
        }

        Assert.True(File.Exists(dstFile));

        // Delete from Source
        File.Delete(srcFile);

        using (var sp = new FileSystemProvider(pair.Source))
        using (var dp = new FileSystemProvider(pair.Destination))
        {
            var delPlan = await _engine.AnalyzeAsync(pair, sp, dp);
            Assert.Single(delPlan.Operations);
            Assert.Equal(SyncActionType.Delete, delPlan.Operations[0].ActionType);

            var delResult = await _engine.ExecuteSyncAsync(pair, sp, dp, delPlan);
            Assert.True(delResult.Success);

            // Active file should be gone from Destination
            Assert.False(File.Exists(dstFile));

            // But it MUST be safely preserved in .drivesync/deleted/
            var deletedItems = await _versionManager.GetRecoverableItemsAsync(_destDir);
            Assert.Contains(deletedItems, i => i.IsDeletedItem);
            var recovered = deletedItems.First(i => i.IsDeletedItem);
            string recoveredContent = await File.ReadAllTextAsync(recovered.ArchiveFullPath);
            Assert.Equal("col1,col2\n1,2", recoveredContent);
        }
    }

    [Fact]
    public async Task EndToEnd_TwoWaySync_ConcurrentEditsReportConflictWithoutSilentlyOverwriting()
    {
        var pair = CreatePair();

        string srcFile = Path.Combine(_sourceDir, "budget.xlsx");
        string dstFile = Path.Combine(_destDir, "budget.xlsx");
        await File.WriteAllTextAsync(srcFile, "Initial Budget: $1000");

        using (var sp = new FileSystemProvider(pair.Source))
        using (var dp = new FileSystemProvider(pair.Destination))
        {
            var plan = await _engine.AnalyzeAsync(pair, sp, dp);
            await _engine.ExecuteSyncAsync(pair, sp, dp, plan);
        }

        await Task.Delay(100);

        // Concurrently edit BOTH sides with differing contents
        await File.WriteAllTextAsync(srcFile, "Source Budget: $2500");
        await File.WriteAllTextAsync(dstFile, "SSD Budget: $3200");

        using (var sp = new FileSystemProvider(pair.Source))
        using (var dp = new FileSystemProvider(pair.Destination))
        {
            var conflictPlan = await _engine.AnalyzeAsync(pair, sp, dp);
            Assert.Empty(conflictPlan.Operations);
            Assert.Single(conflictPlan.Conflicts);
            Assert.Equal("budget.xlsx", conflictPlan.Conflicts[0].RelativePath);

            var syncResult = await _engine.ExecuteSyncAsync(pair, sp, dp, conflictPlan);
            Assert.True(syncResult.HasConflicts);

            // Neither file should have been silently overwritten!
            Assert.Equal("Source Budget: $2500", await File.ReadAllTextAsync(srcFile));
            Assert.Equal("SSD Budget: $3200", await File.ReadAllTextAsync(dstFile));

            // Unresolved conflict must be recorded in SQLite
            var storedConflicts = await _stateStore.GetConflictsAsync(pair.Id, unresolvedOnly: true);
            Assert.Single(storedConflicts);
            Assert.Equal("budget.xlsx", storedConflicts[0].RelativePath);
        }
    }

    [Fact]
    public async Task EndToEnd_CrashResilience_StateStoreSurvivesRestart()
    {
        string pairId = "crash-test-pair";
        var snap = new SyncSnapshot
        {
            PairId = pairId,
            RelativePath = "critical_database.sqlite",
            SizeBytes = 999999,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            LastSyncedUtc = DateTimeOffset.UtcNow,
            ContentHashSha256 = "hash999"
        };

        await _stateStore.SaveSnapshotAsync(snap);

        // Simulate crash: dispose store and reopen from disk
        _stateStore.Dispose();

        using var reopenedStore = new SqliteSyncStateStore(_dbPath);
        await reopenedStore.InitializeAsync();

        var loaded = await reopenedStore.GetSnapshotAsync(pairId, "critical_database.sqlite");
        Assert.NotNull(loaded);
        Assert.Equal("hash999", loaded.ContentHashSha256);
        Assert.Equal(999999, loaded.SizeBytes);
    }

    private SyncPair CreatePair()
    {
        return new SyncPair
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = "E2E Test Pair",
            Source = new SyncEndpoint { DisplayName = "Source", RootPath = _sourceDir, ProviderType = ProviderType.Local },
            Destination = new SyncEndpoint { DisplayName = "Destination SSD", RootPath = _destDir, ProviderType = ProviderType.ExternalSsd },
            Direction = SyncDirection.TwoWay,
            DeletionPolicy = DeletionPolicy.MoveToDeletedItems,
            ConflictPolicy = ConflictPolicy.ManualReview
        };
    }
}
