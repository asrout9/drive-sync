using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DriveSync.Core.Engine;
using DriveSync.Core.Models;
using DriveSync.Core.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveSync.Providers;

public class FileSystemProvider : ISyncProvider
{
    private readonly SyncEndpoint _endpoint;
    private readonly ILogger<FileSystemProvider> _logger;
    private readonly ConcurrentDictionary<string, (long Size, DateTimeOffset MTime, string Hash)> _hashCache = new(StringComparer.OrdinalIgnoreCase);

    public ProviderType ProviderType => _endpoint.ProviderType;
    public SyncEndpoint Endpoint => _endpoint;
    public bool SupportsChangeFeed => false;

    public FileSystemProvider(SyncEndpoint endpoint, ILogger<FileSystemProvider>? logger = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _logger = logger ?? NullLogger<FileSystemProvider>.Instance;
    }

    public Task<bool> ValidateAccessAsync(CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_endpoint.RootPath)) return Task.FromResult(false);
            if (!Directory.Exists(_endpoint.RootPath)) return Task.FromResult(false);

            // Verify write permission with a lightweight temp probe in .drivesync folder
            string testFolder = Path.Combine(_endpoint.RootPath, PathSafety.DriveSyncMetaFolder);
            if (!Directory.Exists(testFolder))
            {
                Directory.CreateDirectory(testFolder);
            }
            string probeFile = Path.Combine(testFolder, $".probe_{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probeFile, "ok");
            File.Delete(probeFile);

            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to validate access to root '{RootPath}'", _endpoint.RootPath);
            return Task.FromResult(false);
        }
    }

    public Task<long> GetAvailableFreeSpaceAsync(CancellationToken ct = default)
    {
        try
        {
            string root = Path.GetPathRoot(_endpoint.RootPath) ?? _endpoint.RootPath;
            var drive = new DriveInfo(root);
            if (drive.IsReady)
            {
                return Task.FromResult(drive.AvailableFreeSpace);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not determine free space for '{RootPath}'", _endpoint.RootPath);
        }
        return Task.FromResult(long.MaxValue);
    }

    public async Task<IReadOnlyList<SyncItem>> EnumerateItemsAsync(string relativeSubfolder = "", CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<SyncItem>();
            string targetFolder = string.IsNullOrEmpty(relativeSubfolder)
                ? _endpoint.RootPath
                : PathSafety.ValidateAndResolvePath(_endpoint.RootPath, relativeSubfolder);

            if (!Directory.Exists(targetFolder))
            {
                return (IReadOnlyList<SyncItem>)results;
            }

            var dirInfo = new DirectoryInfo(targetFolder);

            // Enumerate directories
            foreach (var dir in dirInfo.EnumerateDirectories("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(_endpoint.RootPath, dir.FullName).Replace('\\', '/');

                if (PathSafety.IsMetadataPath(rel)) continue;

                results.Add(new SyncItem
                {
                    RelativePath = rel,
                    StableId = rel,
                    IsDirectory = true,
                    SizeBytes = 0,
                    LastModifiedUtc = dir.LastWriteTimeUtc,
                    Attributes = dir.Attributes
                });
            }

            // Enumerate files
            foreach (var file in dirInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string rel = Path.GetRelativePath(_endpoint.RootPath, file.FullName).Replace('\\', '/');

                if (PathSafety.IsMetadataPath(rel)) continue;

                string? hash = null;
                // Fast hash lookup from cache or compute if file <= 50MB
                if (_hashCache.TryGetValue(rel, out var cached) && cached.Size == file.Length && cached.MTime == file.LastWriteTimeUtc)
                {
                    hash = cached.Hash;
                }
                else if (file.Length <= 50L * 1024 * 1024)
                {
                    try
                    {
                        hash = ComputeFileHash(file.FullName);
                        _hashCache[rel] = (file.Length, file.LastWriteTimeUtc, hash);
                    }
                    catch { }
                }

                results.Add(new SyncItem
                {
                    RelativePath = rel,
                    StableId = rel,
                    IsDirectory = false,
                    SizeBytes = file.Length,
                    LastModifiedUtc = file.LastWriteTimeUtc,
                    ContentHashSha256 = hash,
                    Attributes = file.Attributes
                });
            }

            return (IReadOnlyList<SyncItem>)results;
        }, ct).ConfigureAwait(false);
    }

    public Task<SyncItemMetadata?> GetItemMetadataAsync(string relativePath, CancellationToken ct = default)
    {
        string full = PathSafety.ValidateAndResolvePath(_endpoint.RootPath, relativePath);
        if (File.Exists(full))
        {
            var fi = new FileInfo(full);
            string? hash = null;
            if (_hashCache.TryGetValue(relativePath, out var cached) && cached.Size == fi.Length && cached.MTime == fi.LastWriteTimeUtc)
            {
                hash = cached.Hash;
            }
            else
            {
                try
                {
                    hash = ComputeFileHash(full);
                    _hashCache[relativePath] = (fi.Length, fi.LastWriteTimeUtc, hash);
                }
                catch { }
            }

            return Task.FromResult<SyncItemMetadata?>(new SyncItemMetadata
            {
                SizeBytes = fi.Length,
                LastModifiedUtc = fi.LastWriteTimeUtc,
                ContentHashSha256 = hash,
                Attributes = fi.Attributes
            });
        }
        else if (Directory.Exists(full))
        {
            var di = new DirectoryInfo(full);
            return Task.FromResult<SyncItemMetadata?>(new SyncItemMetadata
            {
                SizeBytes = 0,
                LastModifiedUtc = di.LastWriteTimeUtc,
                Attributes = di.Attributes
            });
        }
        return Task.FromResult<SyncItemMetadata?>(null);
    }

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default)
    {
        string full = PathSafety.ValidateAndResolvePath(_endpoint.RootPath, relativePath);
        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        return Task.FromResult<Stream>(stream);
    }

    public async Task WriteFileAsync(string relativePath, Stream content, SyncItemMetadata metadata, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        string targetFull = PathSafety.ValidateAndResolvePath(_endpoint.RootPath, relativePath);
        string parentDir = Path.GetDirectoryName(targetFull)!;
        if (!Directory.Exists(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        // Atomic write via temporary file
        string tempFile = Path.Combine(parentDir, $".drivesync_tmp_{Guid.NewGuid():N}.tmp");

        try
        {
            using (var writeStream = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                byte[] buffer = new byte[64 * 1024];
                int read;
                long totalBytes = 0;
                while ((read = await content.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                {
                    await writeStream.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                    totalBytes += read;
                    progress?.Report(totalBytes);
                }
                await writeStream.FlushAsync(ct).ConfigureAwait(false);
            }

            // Restore timestamps if specified
            if (metadata.LastModifiedUtc > DateTimeOffset.MinValue)
            {
                File.SetLastWriteTimeUtc(tempFile, metadata.LastModifiedUtc.UtcDateTime);
            }

            // Atomic replace / move
            if (File.Exists(targetFull))
            {
                File.Replace(tempFile, targetFull, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempFile, targetFull);
            }

            // Update hash cache
            if (!string.IsNullOrEmpty(metadata.ContentHashSha256))
            {
                _hashCache[relativePath] = (metadata.SizeBytes, metadata.LastModifiedUtc, metadata.ContentHashSha256);
            }
        }
        catch
        {
            try
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
            catch { }
            throw;
        }
    }

    public Task CreateFolderAsync(string relativePath, CancellationToken ct = default)
    {
        string full = PathSafety.ValidateAndResolvePath(_endpoint.RootPath, relativePath);
        if (!Directory.Exists(full))
        {
            Directory.CreateDirectory(full);
        }
        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(string relativePath, CancellationToken ct = default)
    {
        string full = PathSafety.ValidateAndResolvePath(_endpoint.RootPath, relativePath);
        if (File.Exists(full))
        {
            File.Delete(full);
            _hashCache.TryRemove(relativePath, out _);
        }
        return Task.CompletedTask;
    }

    public Task DeleteFolderAsync(string relativePath, CancellationToken ct = default)
    {
        string full = PathSafety.ValidateAndResolvePath(_endpoint.RootPath, relativePath);
        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
        }
        return Task.CompletedTask;
    }

    public Task MoveOrRenameAsync(string oldRelativePath, string newRelativePath, CancellationToken ct = default)
    {
        string oldFull = PathSafety.ValidateAndResolvePath(_endpoint.RootPath, oldRelativePath);
        string newFull = PathSafety.ValidateAndResolvePath(_endpoint.RootPath, newRelativePath);

        string parent = Path.GetDirectoryName(newFull)!;
        if (!Directory.Exists(parent))
        {
            Directory.CreateDirectory(parent);
        }

        if (File.Exists(oldFull))
        {
            File.Move(oldFull, newFull, overwrite: true);
            if (_hashCache.TryRemove(oldRelativePath, out var cached))
            {
                _hashCache[newRelativePath] = cached;
            }
        }
        else if (Directory.Exists(oldFull))
        {
            Directory.Move(oldFull, newFull);
        }
        return Task.CompletedTask;
    }

    public Task<ChangeFeedResult> FetchChangesAsync(string? cursor, CancellationToken ct = default)
    {
        throw new NotSupportedException("Local filesystem does not support cursor-based change feeds; use FileSystemWatcher or reconciliation scan.");
    }

    public IFileWatcher? CreateWatcher(Action<FileSystemChangeEventArgs> onChange)
    {
        if (!Directory.Exists(_endpoint.RootPath)) return null;
        return new LocalFileSystemWatcher(_endpoint.RootPath, onChange, _logger);
    }

    private static string ComputeFileHash(string fullPath)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
        byte[] hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _hashCache.Clear();
    }

    private class LocalFileSystemWatcher : IFileWatcher
    {
        private readonly string _rootPath;
        private readonly Action<FileSystemChangeEventArgs> _onChange;
        private readonly ILogger _logger;
        private FileSystemWatcher? _fsw;
        private readonly System.Timers.Timer _debounceTimer;
        private readonly ConcurrentDictionary<string, SyncActionType> _pendingEvents = new(StringComparer.OrdinalIgnoreCase);

        public LocalFileSystemWatcher(string rootPath, Action<FileSystemChangeEventArgs> onChange, ILogger logger)
        {
            _rootPath = rootPath;
            _onChange = onChange;
            _logger = logger;

            _debounceTimer = new System.Timers.Timer(500); // 500ms debounce
            _debounceTimer.AutoReset = false;
            _debounceTimer.Elapsed += (s, e) => FlushPendingEvents();
        }

        public void Start()
        {
            if (_fsw != null) return;
            try
            {
                _fsw = new FileSystemWatcher(_rootPath)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };

                _fsw.Created += (s, e) => OnWatcherEvent(e.FullPath, SyncActionType.Create);
                _fsw.Changed += (s, e) => OnWatcherEvent(e.FullPath, SyncActionType.Update);
                _fsw.Deleted += (s, e) => OnWatcherEvent(e.FullPath, SyncActionType.Delete);
                _fsw.Renamed += (s, e) => OnWatcherRenamed(e.OldFullPath, e.FullPath);

                _fsw.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start FileSystemWatcher for '{RootPath}'", _rootPath);
            }
        }

        public void Stop()
        {
            if (_fsw != null)
            {
                _fsw.EnableRaisingEvents = false;
                _fsw.Dispose();
                _fsw = null;
            }
            _debounceTimer.Stop();
        }

        private void OnWatcherEvent(string fullPath, SyncActionType type)
        {
            try
            {
                string rel = Path.GetRelativePath(_rootPath, fullPath).Replace('\\', '/');
                if (PathSafety.IsMetadataPath(rel)) return;

                _pendingEvents[rel] = type;
                _debounceTimer.Stop();
                _debounceTimer.Start();
            }
            catch { }
        }

        private void OnWatcherRenamed(string oldFullPath, string fullPath)
        {
            try
            {
                string oldRel = Path.GetRelativePath(_rootPath, oldFullPath).Replace('\\', '/');
                string newRel = Path.GetRelativePath(_rootPath, fullPath).Replace('\\', '/');
                if (PathSafety.IsMetadataPath(oldRel) && PathSafety.IsMetadataPath(newRel)) return;

                _onChange(new FileSystemChangeEventArgs
                {
                    OldRelativePath = oldRel,
                    RelativePath = newRel,
                    ChangeType = SyncActionType.Rename
                });
            }
            catch { }
        }

        private void FlushPendingEvents()
        {
            var snapshot = _pendingEvents.ToArray();
            _pendingEvents.Clear();

            foreach (var kvp in snapshot)
            {
                try
                {
                    _onChange(new FileSystemChangeEventArgs
                    {
                        RelativePath = kvp.Key,
                        ChangeType = kvp.Value
                    });
                }
                catch { }
            }
        }

        public void Dispose()
        {
            Stop();
            _debounceTimer.Dispose();
        }
    }
}
