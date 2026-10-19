using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DriveSync.Core.Models;
using Microsoft.Data.Sqlite;

namespace DriveSync.Core.Storage;

public class SqliteSyncStateStore : ISyncStateStore, IAsyncDisposable, IDisposable
{
    private readonly string _connectionString;
    private readonly string _dbFilePath;
    private SqliteConnection? _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized;

    public SqliteSyncStateStore(string dbFilePath)
    {
        _dbFilePath = Path.GetFullPath(dbFilePath);
        var dir = Path.GetDirectoryName(_dbFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbFilePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _connectionString = builder.ToString();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            _connection = new SqliteConnection(_connectionString);
            await _connection.OpenAsync(ct).ConfigureAwait(false);

            // Enable WAL mode for high concurrency and crash resilience
            using (var walCmd = _connection.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                await walCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS SyncPairs (
                    Id TEXT PRIMARY KEY,
                    JsonData TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS SyncSnapshots (
                    PairId TEXT NOT NULL,
                    RelativePath TEXT NOT NULL,
                    StableId TEXT,
                    IsDirectory INTEGER NOT NULL,
                    SizeBytes INTEGER NOT NULL,
                    LastModifiedUtc TEXT NOT NULL,
                    ContentHashSha256 TEXT,
                    VersionTag TEXT,
                    LastSyncedUtc TEXT NOT NULL,
                    PRIMARY KEY (PairId, RelativePath)
                );

                CREATE INDEX IF NOT EXISTS IX_Snapshots_PairId ON SyncSnapshots(PairId);

                CREATE TABLE IF NOT EXISTS SyncConflicts (
                    Id TEXT PRIMARY KEY,
                    PairId TEXT NOT NULL,
                    RelativePath TEXT NOT NULL,
                    SourceItemJson TEXT,
                    DestinationItemJson TEXT,
                    BaseSnapshotJson TEXT,
                    ConflictReason TEXT NOT NULL,
                    DetectedTimeUtc TEXT NOT NULL,
                    Resolution INTEGER NOT NULL,
                    ResolvedTimeUtc TEXT,
                    ResolutionNotes TEXT
                );

                CREATE INDEX IF NOT EXISTS IX_Conflicts_PairId ON SyncConflicts(PairId);
                CREATE INDEX IF NOT EXISTS IX_Conflicts_Resolution ON SyncConflicts(Resolution);

                CREATE TABLE IF NOT EXISTS ChangeCursors (
                    PairId TEXT NOT NULL,
                    Side INTEGER NOT NULL,
                    Cursor TEXT NOT NULL,
                    UpdatedAtUtc TEXT NOT NULL,
                    PRIMARY KEY (PairId, Side)
                );
            ";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _lock.Release();
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized || _connection == null)
        {
            InitializeAsync().GetAwaiter().GetResult();
        }
    }

    public async Task<IReadOnlyList<SyncPair>> GetAllPairsAsync(CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT JsonData FROM SyncPairs ORDER BY UpdatedAtUtc DESC";
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var list = new List<SyncPair>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string json = reader.GetString(0);
                var pair = JsonSerializer.Deserialize<SyncPair>(json);
                if (pair != null) list.Add(pair);
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SyncPair?> GetPairAsync(string pairId, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT JsonData FROM SyncPairs WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@Id", pairId);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string json = reader.GetString(0);
                return JsonSerializer.Deserialize<SyncPair>(json);
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SavePairAsync(SyncPair pair, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO SyncPairs (Id, JsonData, UpdatedAtUtc)
                VALUES (@Id, @JsonData, @UpdatedAtUtc)
                ON CONFLICT(Id) DO UPDATE SET
                    JsonData = excluded.JsonData,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
            ";
            cmd.Parameters.AddWithValue("@Id", pair.Id);
            cmd.Parameters.AddWithValue("@JsonData", JsonSerializer.Serialize(pair));
            cmd.Parameters.AddWithValue("@UpdatedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeletePairAsync(string pairId, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection!.BeginTransaction();
            using var cmd1 = _connection.CreateCommand();
            cmd1.Transaction = tx;
            cmd1.CommandText = "DELETE FROM SyncPairs WHERE Id = @Id; DELETE FROM SyncSnapshots WHERE PairId = @Id; DELETE FROM SyncConflicts WHERE PairId = @Id; DELETE FROM ChangeCursors WHERE PairId = @Id;";
            cmd1.Parameters.AddWithValue("@Id", pairId);
            await cmd1.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            tx.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyDictionary<string, SyncSnapshot>> GetSnapshotsAsync(string pairId, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT RelativePath, StableId, IsDirectory, SizeBytes, LastModifiedUtc, ContentHashSha256, VersionTag, LastSyncedUtc FROM SyncSnapshots WHERE PairId = @PairId";
            cmd.Parameters.AddWithValue("@PairId", pairId);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var dict = new Dictionary<string, SyncSnapshot>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var snap = new SyncSnapshot
                {
                    PairId = pairId,
                    RelativePath = reader.GetString(0),
                    StableId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    IsDirectory = reader.GetInt32(2) == 1,
                    SizeBytes = reader.GetInt64(3),
                    LastModifiedUtc = DateTimeOffset.Parse(reader.GetString(4)),
                    ContentHashSha256 = reader.IsDBNull(5) ? null : reader.GetString(5),
                    VersionTag = reader.IsDBNull(6) ? null : reader.GetString(6),
                    LastSyncedUtc = DateTimeOffset.Parse(reader.GetString(7))
                };
                dict[snap.RelativePath] = snap;
            }
            return dict;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SyncSnapshot?> GetSnapshotAsync(string pairId, string relativePath, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT RelativePath, StableId, IsDirectory, SizeBytes, LastModifiedUtc, ContentHashSha256, VersionTag, LastSyncedUtc FROM SyncSnapshots WHERE PairId = @PairId AND RelativePath = @RelativePath";
            cmd.Parameters.AddWithValue("@PairId", pairId);
            cmd.Parameters.AddWithValue("@RelativePath", relativePath);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return new SyncSnapshot
                {
                    PairId = pairId,
                    RelativePath = reader.GetString(0),
                    StableId = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    IsDirectory = reader.GetInt32(2) == 1,
                    SizeBytes = reader.GetInt64(3),
                    LastModifiedUtc = DateTimeOffset.Parse(reader.GetString(4)),
                    ContentHashSha256 = reader.IsDBNull(5) ? null : reader.GetString(5),
                    VersionTag = reader.IsDBNull(6) ? null : reader.GetString(6),
                    LastSyncedUtc = DateTimeOffset.Parse(reader.GetString(7))
                };
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveSnapshotAsync(SyncSnapshot snapshot, CancellationToken ct = default)
    {
        await SaveSnapshotsBatchAsync(snapshot.PairId, new[] { snapshot }, ct).ConfigureAwait(false);
    }

    public async Task SaveSnapshotsBatchAsync(string pairId, IEnumerable<SyncSnapshot> snapshots, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection!.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO SyncSnapshots (PairId, RelativePath, StableId, IsDirectory, SizeBytes, LastModifiedUtc, ContentHashSha256, VersionTag, LastSyncedUtc)
                VALUES (@PairId, @RelativePath, @StableId, @IsDirectory, @SizeBytes, @LastModifiedUtc, @ContentHashSha256, @VersionTag, @LastSyncedUtc)
                ON CONFLICT(PairId, RelativePath) DO UPDATE SET
                    StableId = excluded.StableId,
                    IsDirectory = excluded.IsDirectory,
                    SizeBytes = excluded.SizeBytes,
                    LastModifiedUtc = excluded.LastModifiedUtc,
                    ContentHashSha256 = excluded.ContentHashSha256,
                    VersionTag = excluded.VersionTag,
                    LastSyncedUtc = excluded.LastSyncedUtc;
            ";

            var pPairId = cmd.Parameters.Add("@PairId", SqliteType.Text);
            var pRel = cmd.Parameters.Add("@RelativePath", SqliteType.Text);
            var pStable = cmd.Parameters.Add("@StableId", SqliteType.Text);
            var pIsDir = cmd.Parameters.Add("@IsDirectory", SqliteType.Integer);
            var pSize = cmd.Parameters.Add("@SizeBytes", SqliteType.Integer);
            var pMod = cmd.Parameters.Add("@LastModifiedUtc", SqliteType.Text);
            var pHash = cmd.Parameters.Add("@ContentHashSha256", SqliteType.Text);
            var pTag = cmd.Parameters.Add("@VersionTag", SqliteType.Text);
            var pSync = cmd.Parameters.Add("@LastSyncedUtc", SqliteType.Text);

            foreach (var s in snapshots)
            {
                pPairId.Value = pairId;
                pRel.Value = s.RelativePath;
                pStable.Value = (object?)s.StableId ?? DBNull.Value;
                pIsDir.Value = s.IsDirectory ? 1 : 0;
                pSize.Value = s.SizeBytes;
                pMod.Value = s.LastModifiedUtc.ToString("O");
                pHash.Value = (object?)s.ContentHashSha256 ?? DBNull.Value;
                pTag.Value = (object?)s.VersionTag ?? DBNull.Value;
                pSync.Value = s.LastSyncedUtc.ToString("O");
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            tx.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteSnapshotAsync(string pairId, string relativePath, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM SyncSnapshots WHERE PairId = @PairId AND RelativePath = @RelativePath";
            cmd.Parameters.AddWithValue("@PairId", pairId);
            cmd.Parameters.AddWithValue("@RelativePath", relativePath);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ClearSnapshotsAsync(string pairId, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM SyncSnapshots WHERE PairId = @PairId";
            cmd.Parameters.AddWithValue("@PairId", pairId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<SyncConflict>> GetConflictsAsync(string? pairId = null, bool unresolvedOnly = true, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            var sql = "SELECT Id, PairId, RelativePath, SourceItemJson, DestinationItemJson, BaseSnapshotJson, ConflictReason, DetectedTimeUtc, Resolution, ResolvedTimeUtc, ResolutionNotes FROM SyncConflicts WHERE 1=1";
            if (!string.IsNullOrEmpty(pairId))
            {
                sql += " AND PairId = @PairId";
                cmd.Parameters.AddWithValue("@PairId", pairId);
            }
            if (unresolvedOnly)
            {
                sql += " AND Resolution = 0";
            }
            sql += " ORDER BY DetectedTimeUtc DESC";
            cmd.CommandText = sql;

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var list = new List<SyncConflict>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var c = new SyncConflict
                {
                    Id = reader.GetString(0),
                    PairId = reader.GetString(1),
                    RelativePath = reader.GetString(2),
                    SourceItem = reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<SyncItem>(reader.GetString(3)),
                    DestinationItem = reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<SyncItem>(reader.GetString(4)),
                    BaseSnapshot = reader.IsDBNull(5) ? null : JsonSerializer.Deserialize<SyncSnapshot>(reader.GetString(5)),
                    ConflictReason = reader.GetString(6),
                    DetectedTimeUtc = DateTimeOffset.Parse(reader.GetString(7)),
                    Resolution = (ConflictResolutionChoice)reader.GetInt32(8),
                    ResolvedTimeUtc = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
                    ResolutionNotes = reader.IsDBNull(10) ? null : reader.GetString(10)
                };
                list.Add(c);
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SyncConflict?> GetConflictAsync(string conflictId, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT Id, PairId, RelativePath, SourceItemJson, DestinationItemJson, BaseSnapshotJson, ConflictReason, DetectedTimeUtc, Resolution, ResolvedTimeUtc, ResolutionNotes FROM SyncConflicts WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@Id", conflictId);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return new SyncConflict
                {
                    Id = reader.GetString(0),
                    PairId = reader.GetString(1),
                    RelativePath = reader.GetString(2),
                    SourceItem = reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<SyncItem>(reader.GetString(3)),
                    DestinationItem = reader.IsDBNull(4) ? null : JsonSerializer.Deserialize<SyncItem>(reader.GetString(4)),
                    BaseSnapshot = reader.IsDBNull(5) ? null : JsonSerializer.Deserialize<SyncSnapshot>(reader.GetString(5)),
                    ConflictReason = reader.GetString(6),
                    DetectedTimeUtc = DateTimeOffset.Parse(reader.GetString(7)),
                    Resolution = (ConflictResolutionChoice)reader.GetInt32(8),
                    ResolvedTimeUtc = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9)),
                    ResolutionNotes = reader.IsDBNull(10) ? null : reader.GetString(10)
                };
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveConflictAsync(SyncConflict conflict, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO SyncConflicts (Id, PairId, RelativePath, SourceItemJson, DestinationItemJson, BaseSnapshotJson, ConflictReason, DetectedTimeUtc, Resolution, ResolvedTimeUtc, ResolutionNotes)
                VALUES (@Id, @PairId, @RelativePath, @SourceItemJson, @DestinationItemJson, @BaseSnapshotJson, @ConflictReason, @DetectedTimeUtc, @Resolution, @ResolvedTimeUtc, @ResolutionNotes)
                ON CONFLICT(Id) DO UPDATE SET
                    Resolution = excluded.Resolution,
                    ResolvedTimeUtc = excluded.ResolvedTimeUtc,
                    ResolutionNotes = excluded.ResolutionNotes;
            ";
            cmd.Parameters.AddWithValue("@Id", conflict.Id);
            cmd.Parameters.AddWithValue("@PairId", conflict.PairId);
            cmd.Parameters.AddWithValue("@RelativePath", conflict.RelativePath);
            cmd.Parameters.AddWithValue("@SourceItemJson", conflict.SourceItem == null ? (object)DBNull.Value : JsonSerializer.Serialize(conflict.SourceItem));
            cmd.Parameters.AddWithValue("@DestinationItemJson", conflict.DestinationItem == null ? (object)DBNull.Value : JsonSerializer.Serialize(conflict.DestinationItem));
            cmd.Parameters.AddWithValue("@BaseSnapshotJson", conflict.BaseSnapshot == null ? (object)DBNull.Value : JsonSerializer.Serialize(conflict.BaseSnapshot));
            cmd.Parameters.AddWithValue("@ConflictReason", conflict.ConflictReason);
            cmd.Parameters.AddWithValue("@DetectedTimeUtc", conflict.DetectedTimeUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@Resolution", (int)conflict.Resolution);
            cmd.Parameters.AddWithValue("@ResolvedTimeUtc", conflict.ResolvedTimeUtc.HasValue ? (object)conflict.ResolvedTimeUtc.Value.ToString("O") : DBNull.Value);
            cmd.Parameters.AddWithValue("@ResolutionNotes", (object?)conflict.ResolutionNotes ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ResolveConflictAsync(string conflictId, ConflictResolutionChoice choice, string? notes = null, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "UPDATE SyncConflicts SET Resolution = @Resolution, ResolvedTimeUtc = @ResolvedTimeUtc, ResolutionNotes = @Notes WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@Resolution", (int)choice);
            cmd.Parameters.AddWithValue("@ResolvedTimeUtc", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Notes", (object?)notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@Id", conflictId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteConflictAsync(string conflictId, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM SyncConflicts WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@Id", conflictId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string?> GetChangeCursorAsync(string pairId, EndpointSide side, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT Cursor FROM ChangeCursors WHERE PairId = @PairId AND Side = @Side";
            cmd.Parameters.AddWithValue("@PairId", pairId);
            cmd.Parameters.AddWithValue("@Side", (int)side);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return reader.GetString(0);
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveChangeCursorAsync(string pairId, EndpointSide side, string cursor, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = @"
                INSERT INTO ChangeCursors (PairId, Side, Cursor, UpdatedAtUtc)
                VALUES (@PairId, @Side, @Cursor, @UpdatedAtUtc)
                ON CONFLICT(PairId, Side) DO UPDATE SET
                    Cursor = excluded.Cursor,
                    UpdatedAtUtc = excluded.UpdatedAtUtc;
            ";
            cmd.Parameters.AddWithValue("@PairId", pairId);
            cmd.Parameters.AddWithValue("@Side", (int)side);
            cmd.Parameters.AddWithValue("@Cursor", cursor);
            cmd.Parameters.AddWithValue("@UpdatedAtUtc", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.CloseAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
            _connection = null;
        }
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        _connection?.Close();
        _connection?.Dispose();
        _connection = null;
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }
}
