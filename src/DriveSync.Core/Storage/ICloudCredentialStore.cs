using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DriveSync.Core.Storage;

public class CloudAccountCredential
{
    public string AccountId { get; set; } = string.Empty;
    public string ProviderType { get; set; } = string.Empty; // "OneDrive" or "GoogleDrive"
    public string DisplayName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string EncryptedTokenData { get; set; } = string.Empty;
    public DateTimeOffset LastRefreshedUtc { get; set; } = DateTimeOffset.UtcNow;
}

public interface ICloudCredentialStore
{
    Task SaveCredentialAsync(string accountId, string providerType, string displayName, string email, string rawToken, CancellationToken ct = default);
    Task<string?> GetRawTokenAsync(string accountId, CancellationToken ct = default);
    Task<IReadOnlyList<CloudAccountCredential>> GetAllAccountsAsync(string? providerType = null, CancellationToken ct = default);
    Task DeleteCredentialAsync(string accountId, CancellationToken ct = default);
}
