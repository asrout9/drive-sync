using System.IO;

namespace DriveSync.Core.Models;

public class SyncItem
{
    public string RelativePath { get; set; } = string.Empty;
    public string StableId { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public long SizeBytes { get; set; }
    public DateTimeOffset LastModifiedUtc { get; set; }
    public string? ContentHashSha256 { get; set; }
    public string? VersionTag { get; set; }
    public FileAttributes Attributes { get; set; } = FileAttributes.Normal;

    public override string ToString() => $"{(IsDirectory ? "[DIR]" : "[FILE]")} {RelativePath} ({SizeBytes} B, {LastModifiedUtc:u})";
}

public class SyncItemMetadata
{
    public long SizeBytes { get; set; }
    public DateTimeOffset LastModifiedUtc { get; set; }
    public string? ContentHashSha256 { get; set; }
    public FileAttributes Attributes { get; set; } = FileAttributes.Normal;
}
