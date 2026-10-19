using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DriveSync.Core.Models;

namespace DriveSync.Core.Engine;

public class ChangeFeedResult
{
    public IReadOnlyList<SyncItem> ChangedItems { get; set; } = Array.Empty<SyncItem>();
    public IReadOnlyList<string> DeletedItemPaths { get; set; } = Array.Empty<string>();
    public string? NewCursor { get; set; }
    public bool HasMoreChanges { get; set; }
}

public class FileSystemChangeEventArgs : EventArgs
{
    public string RelativePath { get; set; } = string.Empty;
    public string? OldRelativePath { get; set; }
    public SyncActionType ChangeType { get; set; }
}

public interface IFileWatcher : IDisposable
{
    void Start();
    void Stop();
}

public interface ISyncProvider : IAsyncDisposable, IDisposable
{
    ProviderType ProviderType { get; }
    SyncEndpoint Endpoint { get; }

    Task<bool> ValidateAccessAsync(CancellationToken ct = default);
    Task<long> GetAvailableFreeSpaceAsync(CancellationToken ct = default);

    Task<IReadOnlyList<SyncItem>> EnumerateItemsAsync(string relativeSubfolder = "", CancellationToken ct = default);
    Task<IReadOnlyList<SyncItem>> EnumerateItemsAsync(string relativeSubfolder, IProgress<string>? progress, CancellationToken ct = default);
    Task<SyncItemMetadata?> GetItemMetadataAsync(string relativePath, CancellationToken ct = default);

    Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default);
    Task WriteFileAsync(string relativePath, Stream content, SyncItemMetadata metadata, IProgress<long>? progress = null, CancellationToken ct = default);

    Task CreateFolderAsync(string relativePath, CancellationToken ct = default);
    Task DeleteFileAsync(string relativePath, CancellationToken ct = default);
    Task DeleteFolderAsync(string relativePath, CancellationToken ct = default);
    Task MoveOrRenameAsync(string oldRelativePath, string newRelativePath, CancellationToken ct = default);

    bool SupportsChangeFeed { get; }
    Task<ChangeFeedResult> FetchChangesAsync(string? cursor, CancellationToken ct = default);

    IFileWatcher? CreateWatcher(Action<FileSystemChangeEventArgs> onChange);
}
