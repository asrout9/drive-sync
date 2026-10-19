using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DriveSync.Core.Engine;
using DriveSync.Core.Models;
using DriveSync.Core.Security;
using DriveSync.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveSync.Providers;

public class OneDriveProvider : ISyncProvider
{
    private readonly SyncEndpoint _endpoint;
    private readonly ICloudCredentialStore _credentialStore;
    private readonly ILogger<OneDriveProvider> _logger;
    private readonly HttpClient _httpClient;
    private readonly FileSystemProvider? _localFallbackProvider;

    public ProviderType ProviderType => ProviderType.OneDrive;
    public SyncEndpoint Endpoint => _endpoint;
    public bool SupportsChangeFeed => true;

    public OneDriveProvider(
        SyncEndpoint endpoint,
        ICloudCredentialStore credentialStore,
        ILogger<OneDriveProvider>? logger = null,
        HttpClient? httpClient = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _logger = logger ?? NullLogger<OneDriveProvider>.Instance;
        _httpClient = httpClient ?? new HttpClient();

        // If local sync folder exists, initialize local fallback provider
        if (!string.IsNullOrEmpty(_endpoint.RootPath) && Directory.Exists(_endpoint.RootPath))
        {
            _localFallbackProvider = new FileSystemProvider(_endpoint);
        }
    }

    private async Task<string?> GetAccessTokenAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_endpoint.AccountId)) return null;
        return await _credentialStore.GetRawTokenAsync(_endpoint.AccountId, ct).ConfigureAwait(false);
    }

    public async Task<bool> ValidateAccessAsync(CancellationToken ct = default)
    {
        string? token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/drive");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                if (res.IsSuccessStatusCode) return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Graph API access check failed; checking local sync folder fallback");
            }
        }

        if (_localFallbackProvider != null)
        {
            return await _localFallbackProvider.ValidateAccessAsync(ct).ConfigureAwait(false);
        }

        return false;
    }

    public async Task<long> GetAvailableFreeSpaceAsync(CancellationToken ct = default)
    {
        string? token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://graph.microsoft.com/v1.0/me/drive");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("quota", out var quota))
                    {
                        if (quota.TryGetProperty("remaining", out var rem))
                        {
                            return rem.GetInt64();
                        }
                    }
                }
            }
            catch { }
        }

        if (_localFallbackProvider != null)
        {
            return await _localFallbackProvider.GetAvailableFreeSpaceAsync(ct).ConfigureAwait(false);
        }

        return long.MaxValue;
    }

    public Task<IReadOnlyList<SyncItem>> EnumerateItemsAsync(string relativeSubfolder = "", CancellationToken ct = default)
        => EnumerateItemsAsync(relativeSubfolder, null, ct);

    public async Task<IReadOnlyList<SyncItem>> EnumerateItemsAsync(string relativeSubfolder, IProgress<string>? progress, CancellationToken ct = default)
    {
        string? token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token) && _localFallbackProvider != null)
        {
            _logger.LogInformation("OneDrive: Using local synced folder mode for {Path}", _endpoint.RootPath);
            return await _localFallbackProvider.EnumerateItemsAsync(relativeSubfolder, progress, ct).ConfigureAwait(false);
        }

        var items = new List<SyncItem>();
        try
        {
            string url = "https://graph.microsoft.com/v1.0/me/drive/root/children?$top=100";
            while (!string.IsNullOrEmpty(url))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) break;

                string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("value", out var array))
                {
                    foreach (var elem in array.EnumerateArray())
                    {
                        string name = elem.GetProperty("name").GetString() ?? "";
                        bool isDir = elem.TryGetProperty("folder", out _);
                        long size = elem.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                        DateTimeOffset mtime = elem.TryGetProperty("lastModifiedDateTime", out var m) ? m.GetDateTimeOffset() : DateTimeOffset.UtcNow;
                        string id = elem.GetProperty("id").GetString() ?? "";

                        string? sha256 = null;
                        if (elem.TryGetProperty("file", out var f) && f.TryGetProperty("hashes", out var hashes))
                        {
                            if (hashes.TryGetProperty("sha256Hash", out var h))
                            {
                                sha256 = h.GetString();
                            }
                        }

                        items.Add(new SyncItem
                        {
                            RelativePath = name,
                            StableId = id,
                            IsDirectory = isDir,
                            SizeBytes = size,
                            LastModifiedUtc = mtime,
                            ContentHashSha256 = sha256
                        });
                    }
                }

                url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() ?? "" : "";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Graph API list error, falling back to local folder if present");
            if (_localFallbackProvider != null)
            {
                return await _localFallbackProvider.EnumerateItemsAsync(relativeSubfolder, ct).ConfigureAwait(false);
            }
        }

        return items;
    }

    public async Task<SyncItemMetadata?> GetItemMetadataAsync(string relativePath, CancellationToken ct = default)
    {
        if (_localFallbackProvider != null)
        {
            return await _localFallbackProvider.GetItemMetadataAsync(relativePath, ct).ConfigureAwait(false);
        }
        return null;
    }

    public async Task<Stream> OpenReadAsync(string relativePath, CancellationToken ct = default)
    {
        if (_localFallbackProvider != null)
        {
            return await _localFallbackProvider.OpenReadAsync(relativePath, ct).ConfigureAwait(false);
        }

        string? token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        string url = $"https://graph.microsoft.com/v1.0/me/drive/root:/{Uri.EscapeDataString(relativePath)}:/content";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
    }

    public async Task WriteFileAsync(string relativePath, Stream content, SyncItemMetadata metadata, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (_localFallbackProvider != null)
        {
            await _localFallbackProvider.WriteFileAsync(relativePath, content, metadata, progress, ct).ConfigureAwait(false);
            return;
        }

        string? token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        string url = $"https://graph.microsoft.com/v1.0/me/drive/root:/{Uri.EscapeDataString(relativePath)}:/content";
        using var req = new HttpRequestMessage(HttpMethod.Put, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Content = new StreamContent(content);
        var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();
    }

    public async Task CreateFolderAsync(string relativePath, CancellationToken ct = default)
    {
        if (_localFallbackProvider != null)
        {
            await _localFallbackProvider.CreateFolderAsync(relativePath, ct).ConfigureAwait(false);
        }
    }

    public async Task DeleteFileAsync(string relativePath, CancellationToken ct = default)
    {
        if (_localFallbackProvider != null)
        {
            await _localFallbackProvider.DeleteFileAsync(relativePath, ct).ConfigureAwait(false);
        }
    }

    public async Task DeleteFolderAsync(string relativePath, CancellationToken ct = default)
    {
        if (_localFallbackProvider != null)
        {
            await _localFallbackProvider.DeleteFolderAsync(relativePath, ct).ConfigureAwait(false);
        }
    }

    public async Task MoveOrRenameAsync(string oldRelativePath, string newRelativePath, CancellationToken ct = default)
    {
        if (_localFallbackProvider != null)
        {
            await _localFallbackProvider.MoveOrRenameAsync(oldRelativePath, newRelativePath, ct).ConfigureAwait(false);
        }
    }

    public async Task<ChangeFeedResult> FetchChangesAsync(string? cursor, CancellationToken ct = default)
    {
        string? token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(token))
        {
            return new ChangeFeedResult();
        }

        string url = string.IsNullOrEmpty(cursor)
            ? "https://graph.microsoft.com/v1.0/me/drive/root/delta"
            : cursor;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return new ChangeFeedResult();

        string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var changed = new List<SyncItem>();
        var deleted = new List<string>();

        if (doc.RootElement.TryGetProperty("value", out var items))
        {
            foreach (var elem in items.EnumerateArray())
            {
                string name = elem.GetProperty("name").GetString() ?? "";
                if (elem.TryGetProperty("deleted", out _))
                {
                    deleted.Add(name);
                }
                else
                {
                    bool isDir = elem.TryGetProperty("folder", out _);
                    long size = elem.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                    DateTimeOffset mtime = elem.TryGetProperty("lastModifiedDateTime", out var m) ? m.GetDateTimeOffset() : DateTimeOffset.UtcNow;
                    string id = elem.GetProperty("id").GetString() ?? "";

                    changed.Add(new SyncItem
                    {
                        RelativePath = name,
                        StableId = id,
                        IsDirectory = isDir,
                        SizeBytes = size,
                        LastModifiedUtc = mtime
                    });
                }
            }
        }

        string? nextCursor = doc.RootElement.TryGetProperty("@odata.deltaLink", out var dLink)
            ? dLink.GetString()
            : (doc.RootElement.TryGetProperty("@odata.nextLink", out var nLink) ? nLink.GetString() : null);

        return new ChangeFeedResult
        {
            ChangedItems = changed,
            DeletedItemPaths = deleted,
            NewCursor = nextCursor,
            HasMoreChanges = doc.RootElement.TryGetProperty("@odata.nextLink", out _)
        };
    }

    public IFileWatcher? CreateWatcher(Action<FileSystemChangeEventArgs> onChange)
    {
        return _localFallbackProvider?.CreateWatcher(onChange);
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        _localFallbackProvider?.Dispose();
    }
}
