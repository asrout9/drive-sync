using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DriveSync.Core.Models;
using DriveSync.Providers;
using Xunit;

namespace DriveSync.Tests;

public class ProviderTests : IDisposable
{
    private readonly string _testDir;
    private readonly DpapiCloudCredentialStore _credStore;

    public ProviderTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "DriveSyncProviderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        _credStore = new DpapiCloudCredentialStore(Path.Combine(_testDir, "vault"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
            {
                Directory.Delete(_testDir, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task FileSystemProvider_PerformsAtomicWriteAndEnumeration()
    {
        var endpoint = new SyncEndpoint
        {
            ProviderType = ProviderType.Local,
            RootPath = _testDir,
            DisplayName = "Test Local"
        };
        using var provider = new FileSystemProvider(endpoint);

        bool access = await provider.ValidateAccessAsync();
        Assert.True(access);

        // Write a test file
        byte[] content = Encoding.UTF8.GetBytes("Hello DriveSync Atomic Write!");
        using (var mem = new MemoryStream(content))
        {
            await provider.WriteFileAsync("folder/hello.txt", mem, new SyncItemMetadata
            {
                SizeBytes = content.Length,
                LastModifiedUtc = DateTimeOffset.UtcNow
            });
        }

        string fullPath = Path.Combine(_testDir, "folder", "hello.txt");
        Assert.True(File.Exists(fullPath));
        string readBack = await File.ReadAllTextAsync(fullPath);
        Assert.Equal("Hello DriveSync Atomic Write!", readBack);

        // Verify enumeration
        var items = await provider.EnumerateItemsAsync();
        Assert.Contains(items, i => i.RelativePath == "folder/hello.txt");
        Assert.Contains(items, i => i.RelativePath == "folder" && i.IsDirectory);

        // Read stream
        using (var stream = await provider.OpenReadAsync("folder/hello.txt"))
        using (var reader = new StreamReader(stream))
        {
            string fromStream = await reader.ReadToEndAsync();
            Assert.Equal("Hello DriveSync Atomic Write!", fromStream);
        }

        // Delete file
        await provider.DeleteFileAsync("folder/hello.txt");
        Assert.False(File.Exists(fullPath));
    }

    [Fact]
    public async Task DpapiCloudCredentialStore_EncryptsAndRestoresTokensSecurely()
    {
        string accountId = "user_onedrive_123";
        string secretToken = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.sensitive_oauth_bearer_token";

        await _credStore.SaveCredentialAsync(
            accountId,
            "OneDrive",
            "Personal OneDrive",
            "user@outlook.com",
            secretToken);

        // Raw token should be restored accurately by current Windows user
        string? restored = await _credStore.GetRawTokenAsync(accountId);
        Assert.Equal(secretToken, restored);

        // Stored file should NOT contain the secret token in plain text!
        string vaultFile = Path.Combine(_testDir, "vault", "credentials.dat");
        Assert.True(File.Exists(vaultFile));
        string fileContent = await File.ReadAllTextAsync(vaultFile);
        Assert.DoesNotContain(secretToken, fileContent); // Verifies DPAPI encryption!

        // List accounts
        var accounts = await _credStore.GetAllAccountsAsync();
        Assert.Single(accounts);
        Assert.Equal("Personal OneDrive", accounts[0].DisplayName);
        Assert.Equal("user@outlook.com", accounts[0].Email);

        // Delete credential
        await _credStore.DeleteCredentialAsync(accountId);
        string? deleted = await _credStore.GetRawTokenAsync(accountId);
        Assert.Null(deleted);
    }

    [Fact]
    public async Task WindowsSsdDetector_TokenInitializationAndVerification()
    {
        using var detector = new WindowsSsdDetector();

        // Initialize DriveSync token on test directory
        string token = await detector.InitializeDriveSyncTokenAsync(_testDir);
        Assert.False(string.IsNullOrWhiteSpace(token));

        // Verify with matching token
        bool valid = await detector.VerifyDriveIdentityAsync(_testDir, expectedSerialNumber: null, expectedToken: token);
        Assert.True(valid);

        // Verify with wrong token -> must reject!
        bool invalid = await detector.VerifyDriveIdentityAsync(_testDir, expectedSerialNumber: null, expectedToken: "wrong_token_123");
        Assert.False(invalid);
    }
}
