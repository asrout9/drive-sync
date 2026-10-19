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

public class GoogleDriveProvider : ISyncProvider
{
    private readonly SyncEndpoint _endpoint;
    private readonly ICloudCredentialStore _credentialStore;
    private readonly ILogger<GoogleDriveProvider> _logger;
    private readonly HttpClient _httpClient;
    private readonly FileSystemProvider? _localFallbackProvider;

    private static readonly Dictionary<string, (string ExportMime, string Extension)> GoogleDocExportMap = new()
    {
        ["application/vnd.google-apps.document"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", ".docx"),
        ["application/vnd.google-apps.spreadsheet"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx"),
        ["application/vnd.google-apps.presentation"] = ("application/vnd.openxmlformats-officedocument.presentationml.presentation", ".pptx")
    };

    public ProviderType ProviderType => ProviderType.GoogleDrive;
    public SyncEndpoint Endpoint => _endpoint;
    public bool SupportsChangeFeed => true;

    public GoogleDriveProvider(
        SyncEndpoint endpoint,
        ICloudCredentialStore credentialStore,
        ILogger<GoogleDriveProvider>? logger = null,
        HttpClient? httpClient = null)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        _logger = logger ?? NullLogger<GoogleDriveProvider>.Instance;
        _httpClient = httpClient ?? new HttpClient();

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
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/about?fields=user,storageQuota");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                if (res.IsSuccessStatusCode) return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Google Drive API access check failed; checking local sync folder fallback");
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
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/about?fields=storageQuota");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                if (res.IsSuccessStatusCode)
                {
                    string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("storageQuota", out var quota))
                    {
                        if (quota.TryGetProperty("limit", out var limitElem) && quota.TryGetProperty("usage", out var usageElem))
                        {
                            long limit = long.Parse(limitElem.GetString() ?? "0");
                            long usage = long.Parse(usageElem.GetString() ?? "0");
                            if (limit > usage) return limit - usage;
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
            _logger.LogInformation("Google Drive: Using local folder mode for {Path}", _endpoint.RootPath);
            return await _localFallbackProvider.EnumerateItemsAsync(relativeSubfolder, progress, ct).ConfigureAwait(false);
        }

        var items = new List<SyncItem>();
        try
        {
            string url = "https://www.googleapis.com/drive/v3/files?pageSize=100&fields=nextPageToken,files(id,name,mimeType,size,modifiedTime,md5Checksum,trashed)&q=trashed=false";
            string? pageToken = null;

            do
            {
                string requestUrl = string.IsNullOrEmpty(pageToken) ? url : $"{url}&pageToken={pageToken}";
                using var req = new HttpRequestMessage(HttpMethod.Get, requestUrl);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode) break;

                string json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("files", out var fileArray))
                {
                    foreach (var elem in fileArray.EnumerateArray())
                    {
                        string id = elem.GetProperty("id").GetString() ?? "";
                        string name = elem.GetProperty("name").GetString() ?? "";
                        string mimeType = elem.GetProperty("mimeType").GetString() ?? "";
                        bool isDir = mimeType == "application/vnd.google-apps.folder";
                        long size = elem.TryGetProperty("size", out var s) ? long.Parse(s.GetString() ?? "0") : 0;
                        DateTimeOffset mtime = elem.TryGetProperty("modifiedTime", out var m) ? m.GetDateTimeOffset() : DateTimeOffset.UtcNow;
                        string? md5 = elem.TryGetProperty("md5Checksum", out var h) ? h.GetString() : null;

                        // Google Docs export mapping
                        if (GoogleDocExportMap.TryGetValue(mimeType, out var map))
                        {
                            if (!name.EndsWith(map.Extension, StringComparison.OrdinalIgnoreCase))
                            {
                                name += map.Extension;
                            }
                        }

                        items.Add(new SyncItem
                        {
                            RelativePath = name,
                            StableId = id,
                            IsDirectory = isDir,
                            SizeBytes = size,
                            LastModifiedUtc = mtime,
                            ContentHashSha256 = md5
                        });
                    }
                }

                pageToken = doc.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            } while (!string.IsNullOrEmpty(pageToken));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google Drive API error, falling back to local folder if present");
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

        throw new NotSupportedException("Direct streaming requires known file ID; please use local folder mode or full item sync.");
    }

    public async Task WriteFileAsync(string relativePath, Stream content, SyncItemMetadata metadata, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        if (_localFallbackProvider != null)
        {
            await _localFallbackProvider.WriteFileAsync(relativePath, content, metadata, progress, ct).ConfigureAwait(false);
            return;
        }

        string? token = await GetAccessTokenAsync(ct).ConfigureAwait(false);
        string uploadUrl = $"https://www.googleapis.com/upload/drive/v3/files?uploadType=media";
        using var req = new HttpRequestMessage(HttpMethod.Post, uploadUrl);
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
        if (string.IsNullOrEmpty(token)) return new ChangeFeedResult();

        string pageToken = cursor ?? "";
        if (string.IsNullOrEmpty(pageToken))
        {
            // Get start token
            using var startReq = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/drive/v3/changes/startPageToken");
            startReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var startRes = await _httpClient.SendAsync(startReq, ct).ConfigureAwait(false);
            if (startRes.IsSuccessStatusCode)
            {
                var doc = JsonDocument.Parse(await startRes.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                pageToken = doc.RootElement.GetProperty("startPageToken").GetString() ?? "";
            }
        }

        string url = $"https://www.googleapis.com/drive/v3/changes?pageToken={pageToken}&fields=newStartPageToken,nextPageToken,changes(fileId,removed,file(id,name,mimeType,size,modifiedTime))";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var res = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) return new ChangeFeedResult();

        var changeDoc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var changed = new List<SyncItem>();
        var deleted = new List<string>();

        if (changeDoc.RootElement.TryGetProperty("changes", out var changesArray))
        {
            foreach (var ch in changesArray.EnumerateArray())
            {
                bool removed = ch.TryGetProperty("removed", out var r) && r.GetBoolean();
                if (removed)
                {
                    deleted.Add(ch.GetProperty("fileId").GetString() ?? "");
                }
                else if (ch.TryGetProperty("file", out var f))
                {
                    string name = f.GetProperty("name").GetString() ?? "";
                    string mime = f.GetProperty("mimeType").GetString() ?? "";
                    bool isDir = mime == "application/vnd.google-apps.folder";
                    long size = f.TryGetProperty("size", out var s) ? long.Parse(s.GetString() ?? "0") : 0;
                    DateTimeOffset mtime = f.TryGetProperty("modifiedTime", out var m) ? m.GetDateTimeOffset() : DateTimeOffset.UtcNow;
                    string id = f.GetProperty("id").GetString() ?? "";

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

        string? nextToken = changeDoc.RootElement.TryGetProperty("nextPageToken", out var np)
            ? np.GetString()
            : (changeDoc.RootElement.TryGetProperty("newStartPageToken", out var nst) ? nst.GetString() : null);

        return new ChangeFeedResult
        {
            ChangedItems = changed,
            DeletedItemPaths = deleted,
            NewCursor = nextToken,
            HasMoreChanges = changeDoc.RootElement.TryGetProperty("nextPageToken", out _)
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
