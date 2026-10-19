using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DriveSync.Core.Storage;

namespace DriveSync.Providers;

public class DpapiCloudCredentialStore : ICloudCredentialStore
{
    private readonly string _storeFilePath;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DriveSync.DPAPI.CredentialVault.2026");

    public DpapiCloudCredentialStore(string? baseDir = null)
    {
        string dir = baseDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DriveSync", "Vault");

        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _storeFilePath = Path.Combine(dir, "credentials.dat");
    }

    public async Task SaveCredentialAsync(string accountId, string providerType, string displayName, string email, string rawToken, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var accounts = await ReadAccountsInternalAsync(ct).ConfigureAwait(false);
            byte[] rawBytes = Encoding.UTF8.GetBytes(rawToken);
            byte[] cipherBytes = ProtectedData.Protect(rawBytes, Entropy, DataProtectionScope.CurrentUser);
            string encryptedBase64 = Convert.ToBase64String(cipherBytes);

            var existing = accounts.Find(a => a.AccountId == accountId);
            if (existing != null)
            {
                existing.DisplayName = displayName;
                existing.Email = email;
                existing.EncryptedTokenData = encryptedBase64;
                existing.LastRefreshedUtc = DateTimeOffset.UtcNow;
            }
            else
            {
                accounts.Add(new CloudAccountCredential
                {
                    AccountId = accountId,
                    ProviderType = providerType,
                    DisplayName = displayName,
                    Email = email,
                    EncryptedTokenData = encryptedBase64,
                    LastRefreshedUtc = DateTimeOffset.UtcNow
                });
            }

            await WriteAccountsInternalAsync(accounts, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string?> GetRawTokenAsync(string accountId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var accounts = await ReadAccountsInternalAsync(ct).ConfigureAwait(false);
            var item = accounts.Find(a => a.AccountId == accountId);
            if (item == null || string.IsNullOrEmpty(item.EncryptedTokenData))
            {
                return null;
            }

            byte[] cipherBytes = Convert.FromBase64String(item.EncryptedTokenData);
            byte[] decrypted = ProtectedData.Unprotect(cipherBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch
        {
            return null;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<CloudAccountCredential>> GetAllAccountsAsync(string? providerType = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var accounts = await ReadAccountsInternalAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(providerType))
            {
                return accounts;
            }
            return accounts.FindAll(a => string.Equals(a.ProviderType, providerType, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteCredentialAsync(string accountId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var accounts = await ReadAccountsInternalAsync(ct).ConfigureAwait(false);
            accounts.RemoveAll(a => a.AccountId == accountId);
            await WriteAccountsInternalAsync(accounts, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<List<CloudAccountCredential>> ReadAccountsInternalAsync(CancellationToken ct)
    {
        if (!File.Exists(_storeFilePath))
        {
            return new List<CloudAccountCredential>();
        }

        try
        {
            string json = await File.ReadAllTextAsync(_storeFilePath, ct).ConfigureAwait(false);
            return JsonSerializer.Deserialize<List<CloudAccountCredential>>(json) ?? new List<CloudAccountCredential>();
        }
        catch
        {
            return new List<CloudAccountCredential>();
        }
    }

    private async Task WriteAccountsInternalAsync(List<CloudAccountCredential> accounts, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(accounts, new JsonSerializerOptions { WriteIndented = true });
        string tempFile = _storeFilePath + ".tmp";
        await File.WriteAllTextAsync(tempFile, json, ct).ConfigureAwait(false);
        File.Move(tempFile, _storeFilePath, overwrite: true);
    }
}
