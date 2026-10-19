using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DriveSync.Core.Models;
using Microsoft.Data.Sqlite;

namespace DriveSync.Core.Storage;

public class SqliteJournalStore : IJournalStore, IAsyncDisposable, IDisposable
{
    private readonly string _connectionString;
    private readonly string _dbFilePath;
    private SqliteConnection? _connection;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _initialized;

    public SqliteJournalStore(string dbFilePath)
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

            using (var walCmd = _connection.CreateCommand())
            {
                walCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                await walCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE IF NOT EXISTS SyncJournal (
                    Id TEXT PRIMARY KEY,
                    PairId TEXT NOT NULL,
                    RelativePath TEXT NOT NULL,
                    NewRelativePath TEXT,
                    ActionType INTEGER NOT NULL,
                    SourceSide INTEGER NOT NULL,
                    TargetSide INTEGER NOT NULL,
                    IsDirectory INTEGER NOT NULL,
                    BytesTransferred INTEGER NOT NULL,
                    SourceHash TEXT,
                    TargetHash TEXT,
                    BackupVersionPath TEXT,
                    TimestampUtc TEXT NOT NULL,
                    Status INTEGER NOT NULL,
                    ErrorMessage TEXT,
                    WasUndone INTEGER NOT NULL DEFAULT 0,
                    UndoneAtUtc TEXT
                );

                CREATE INDEX IF NOT EXISTS IX_Journal_Pair_Time ON SyncJournal(PairId, TimestampUtc DESC);
                CREATE INDEX IF NOT EXISTS IX_Journal_Path ON SyncJournal(RelativePath);
                CREATE INDEX IF NOT EXISTS IX_Journal_Action ON SyncJournal(ActionType);
                CREATE INDEX IF NOT EXISTS IX_Journal_Status ON SyncJournal(Status);
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

    public async Task LogOperationAsync(SyncOperation operation, CancellationToken ct = default)
    {
        await LogOperationsBatchAsync(new[] { operation }, ct).ConfigureAwait(false);
    }

    public async Task LogOperationsBatchAsync(IEnumerable<SyncOperation> operations, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var tx = _connection!.BeginTransaction();
            using var cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                INSERT INTO SyncJournal (
                    Id, PairId, RelativePath, NewRelativePath, ActionType, SourceSide, TargetSide,
                    IsDirectory, BytesTransferred, SourceHash, TargetHash, BackupVersionPath,
                    TimestampUtc, Status, ErrorMessage, WasUndone, UndoneAtUtc
                ) VALUES (
                    @Id, @PairId, @RelativePath, @NewRelativePath, @ActionType, @SourceSide, @TargetSide,
                    @IsDirectory, @BytesTransferred, @SourceHash, @TargetHash, @BackupVersionPath,
                    @TimestampUtc, @Status, @ErrorMessage, @WasUndone, @UndoneAtUtc
                ) ON CONFLICT(Id) DO UPDATE SET
                    Status = excluded.Status,
                    ErrorMessage = excluded.ErrorMessage,
                    BackupVersionPath = excluded.BackupVersionPath,
                    WasUndone = excluded.WasUndone,
                    UndoneAtUtc = excluded.UndoneAtUtc;
            ";

            var pId = cmd.Parameters.Add("@Id", SqliteType.Text);
            var pPairId = cmd.Parameters.Add("@PairId", SqliteType.Text);
            var pPath = cmd.Parameters.Add("@RelativePath", SqliteType.Text);
            var pNewPath = cmd.Parameters.Add("@NewRelativePath", SqliteType.Text);
            var pAction = cmd.Parameters.Add("@ActionType", SqliteType.Integer);
            var pSrc = cmd.Parameters.Add("@SourceSide", SqliteType.Integer);
            var pTgt = cmd.Parameters.Add("@TargetSide", SqliteType.Integer);
            var pIsDir = cmd.Parameters.Add("@IsDirectory", SqliteType.Integer);
            var pBytes = cmd.Parameters.Add("@BytesTransferred", SqliteType.Integer);
            var pSrcHash = cmd.Parameters.Add("@SourceHash", SqliteType.Text);
            var pTgtHash = cmd.Parameters.Add("@TargetHash", SqliteType.Text);
            var pBackup = cmd.Parameters.Add("@BackupVersionPath", SqliteType.Text);
            var pTime = cmd.Parameters.Add("@TimestampUtc", SqliteType.Text);
            var pStatus = cmd.Parameters.Add("@Status", SqliteType.Integer);
            var pErr = cmd.Parameters.Add("@ErrorMessage", SqliteType.Text);
            var pUndone = cmd.Parameters.Add("@WasUndone", SqliteType.Integer);
            var pUndoneAt = cmd.Parameters.Add("@UndoneAtUtc", SqliteType.Text);

            foreach (var op in operations)
            {
                pId.Value = op.Id;
                pPairId.Value = op.PairId;
                pPath.Value = op.RelativePath;
                pNewPath.Value = (object?)op.NewRelativePath ?? DBNull.Value;
                pAction.Value = (int)op.ActionType;
                pSrc.Value = (int)op.SourceSide;
                pTgt.Value = (int)op.TargetSide;
                pIsDir.Value = op.IsDirectory ? 1 : 0;
                pBytes.Value = op.BytesTransferred;
                pSrcHash.Value = (object?)op.SourceHash ?? DBNull.Value;
                pTgtHash.Value = (object?)op.TargetHash ?? DBNull.Value;
                pBackup.Value = (object?)op.BackupVersionPath ?? DBNull.Value;
                pTime.Value = op.TimestampUtc.ToString("O");
                pStatus.Value = (int)op.Status;
                pErr.Value = (object?)op.ErrorMessage ?? DBNull.Value;
                pUndone.Value = op.WasUndone ? 1 : 0;
                pUndoneAt.Value = op.UndoneAtUtc.HasValue ? (object)op.UndoneAtUtc.Value.ToString("O") : DBNull.Value;

                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            tx.Commit();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<SyncOperation?> GetOperationAsync(string operationId, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "SELECT Id, PairId, RelativePath, NewRelativePath, ActionType, SourceSide, TargetSide, IsDirectory, BytesTransferred, SourceHash, TargetHash, BackupVersionPath, TimestampUtc, Status, ErrorMessage, WasUndone, UndoneAtUtc FROM SyncJournal WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@Id", operationId);
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return MapReaderToOperation(reader);
            }
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task MarkOperationUndoneAsync(string operationId, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "UPDATE SyncJournal SET WasUndone = 1, UndoneAtUtc = @Now, Status = @Status WHERE Id = @Id";
            cmd.Parameters.AddWithValue("@Now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@Status", (int)OperationStatus.Undone);
            cmd.Parameters.AddWithValue("@Id", operationId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<SyncOperation>> QueryOperationsAsync(JournalFilter filter, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            var sql = new StringBuilder("SELECT Id, PairId, RelativePath, NewRelativePath, ActionType, SourceSide, TargetSide, IsDirectory, BytesTransferred, SourceHash, TargetHash, BackupVersionPath, TimestampUtc, Status, ErrorMessage, WasUndone, UndoneAtUtc FROM SyncJournal WHERE 1=1");

            if (!string.IsNullOrEmpty(filter.PairId))
            {
                sql.Append(" AND PairId = @PairId");
                cmd.Parameters.AddWithValue("@PairId", filter.PairId);
            }
            if (filter.ActionType.HasValue)
            {
                sql.Append(" AND ActionType = @ActionType");
                cmd.Parameters.AddWithValue("@ActionType", (int)filter.ActionType.Value);
            }
            if (filter.Status.HasValue)
            {
                sql.Append(" AND Status = @Status");
                cmd.Parameters.AddWithValue("@Status", (int)filter.Status.Value);
            }
            if (filter.FromDateUtc.HasValue)
            {
                sql.Append(" AND TimestampUtc >= @FromDate");
                cmd.Parameters.AddWithValue("@FromDate", filter.FromDateUtc.Value.ToString("O"));
            }
            if (filter.ToDateUtc.HasValue)
            {
                sql.Append(" AND TimestampUtc <= @ToDate");
                cmd.Parameters.AddWithValue("@ToDate", filter.ToDateUtc.Value.ToString("O"));
            }
            if (!string.IsNullOrWhiteSpace(filter.SearchQuery))
            {
                sql.Append(" AND (RelativePath LIKE @Query OR ErrorMessage LIKE @Query)");
                cmd.Parameters.AddWithValue("@Query", $"%{filter.SearchQuery.Trim()}%");
            }

            sql.Append(" ORDER BY TimestampUtc DESC LIMIT @Limit");
            cmd.Parameters.AddWithValue("@Limit", Math.Max(1, Math.Min(5000, filter.Limit)));

            cmd.CommandText = sql.ToString();
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var list = new List<SyncOperation>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(MapReaderToOperation(reader));
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<DailySyncSummary>> GetDailySummariesAsync(string? pairId = null, int days = 30, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            var sql = @"
                SELECT 
                    substr(TimestampUtc, 1, 10) as Day,
                    SUM(CASE WHEN ActionType = 1 AND Status = 3 THEN 1 ELSE 0 END) as CreatedCount,
                    SUM(CASE WHEN ActionType = 2 AND Status = 3 THEN 1 ELSE 0 END) as UpdatedCount,
                    SUM(CASE WHEN ActionType = 3 AND Status = 3 THEN 1 ELSE 0 END) as DeletedCount,
                    SUM(CASE WHEN ActionType = 4 AND Status = 3 THEN 1 ELSE 0 END) as RenamedCount,
                    SUM(CASE WHEN ActionType = 7 AND Status = 6 THEN 1 ELSE 0 END) as RestoredCount,
                    SUM(CASE WHEN ActionType = 6 THEN 1 ELSE 0 END) as SkippedCount,
                    SUM(CASE WHEN ActionType = 5 THEN 1 ELSE 0 END) as ConflictsDetected,
                    SUM(CASE WHEN Status = 3 THEN BytesTransferred ELSE 0 END) as TotalBytes
                FROM SyncJournal
                WHERE TimestampUtc >= @StartDate
            ";

            if (!string.IsNullOrEmpty(pairId))
            {
                sql += " AND PairId = @PairId";
                cmd.Parameters.AddWithValue("@PairId", pairId);
            }

            sql += " GROUP BY Day ORDER BY Day DESC LIMIT @Days;";
            cmd.CommandText = sql;
            cmd.Parameters.AddWithValue("@StartDate", DateTimeOffset.UtcNow.AddDays(-days).ToString("O"));
            cmd.Parameters.AddWithValue("@Days", days);

            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var list = new List<DailySyncSummary>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                list.Add(new DailySyncSummary
                {
                    Date = DateTime.Parse(reader.GetString(0)),
                    FilesCreated = reader.GetInt32(1),
                    FilesUpdated = reader.GetInt32(2),
                    FilesDeleted = reader.GetInt32(3),
                    FilesRenamed = reader.GetInt32(4),
                    FilesRestored = reader.GetInt32(5),
                    FilesSkipped = reader.GetInt32(6),
                    ConflictsDetected = reader.GetInt32(7),
                    TotalBytesTransferred = reader.GetInt64(8)
                });
            }
            return list;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> ExportSanitizedReportAsync(JournalFilter filter, CancellationToken ct = default)
    {
        var operations = await QueryOperationsAsync(filter, ct).ConfigureAwait(false);
        var sb = new StringBuilder();
        sb.AppendLine("Timestamp (UTC),Pair ID,Action,Relative Path,Source Side,Target Side,Status,Bytes Transferred,Was Undone,Error");
        foreach (var op in operations)
        {
            // Sanitize paths and errors (no tokens or credentials)
            string safePath = op.RelativePath.Replace("\"", "\"\"");
            string safeError = (op.ErrorMessage ?? string.Empty).Replace("\"", "\"\"");
            sb.AppendLine($"\"{op.TimestampUtc:u}\",\"{op.PairId}\",\"{op.ActionType}\",\"{safePath}\",\"{op.SourceSide}\",\"{op.TargetSide}\",\"{op.Status}\",{op.BytesTransferred},{op.WasUndone},\"{safeError}\"");
        }
        return sb.ToString();
    }

    public async Task PruneOldOperationsAsync(int retentionDays, CancellationToken ct = default)
    {
        EnsureInitialized();
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = _connection!.CreateCommand();
            cmd.CommandText = "DELETE FROM SyncJournal WHERE TimestampUtc < @Cutoff";
            cmd.Parameters.AddWithValue("@Cutoff", DateTimeOffset.UtcNow.AddDays(-retentionDays).ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static SyncOperation MapReaderToOperation(SqliteDataReader reader)
    {
        return new SyncOperation
        {
            Id = reader.GetString(0),
            PairId = reader.GetString(1),
            RelativePath = reader.GetString(2),
            NewRelativePath = reader.IsDBNull(3) ? null : reader.GetString(3),
            ActionType = (SyncActionType)reader.GetInt32(4),
            SourceSide = (EndpointSide)reader.GetInt32(5),
            TargetSide = (EndpointSide)reader.GetInt32(6),
            IsDirectory = reader.GetInt32(7) == 1,
            BytesTransferred = reader.GetInt64(8),
            SourceHash = reader.IsDBNull(9) ? null : reader.GetString(9),
            TargetHash = reader.IsDBNull(10) ? null : reader.GetString(10),
            BackupVersionPath = reader.IsDBNull(11) ? null : reader.GetString(11),
            TimestampUtc = DateTimeOffset.Parse(reader.GetString(12)),
            Status = (OperationStatus)reader.GetInt32(13),
            ErrorMessage = reader.IsDBNull(14) ? null : reader.GetString(14),
            WasUndone = reader.GetInt32(15) == 1,
            UndoneAtUtc = reader.IsDBNull(16) ? null : DateTimeOffset.Parse(reader.GetString(16))
        };
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
