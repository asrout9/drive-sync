namespace DriveSync.Core.Models;

public class SyncEndpoint
{
    public ProviderType ProviderType { get; set; } = ProviderType.Local;
    public string RootPath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? AccountId { get; set; }
    public string? AccountEmail { get; set; }
    public string? VolumeSerialNumber { get; set; }
    public string? SsdUniqueId { get; set; }

    public override string ToString()
    {
        return ProviderType switch
        {
            ProviderType.ExternalSsd => $"[SSD] {DisplayName} ({RootPath})",
            ProviderType.OneDrive => $"[OneDrive: {AccountEmail ?? AccountId}] {DisplayName} ({RootPath})",
            ProviderType.GoogleDrive => $"[Google Drive: {AccountEmail ?? AccountId}] {DisplayName} ({RootPath})",
            _ => $"[Local] {RootPath}"
        };
    }
}
