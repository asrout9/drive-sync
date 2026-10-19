using System;
using System.IO;
using System.Text;

namespace DriveSync.Core.Security;

public static class PathSafety
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public const string DriveSyncMetaFolder = ".drivesync";

    /// <summary>
    /// Normalizes relative path: removes leading/trailing slashes, normalizes to standard forward slashes,
    /// applies Unicode NFC normalization, and validates that it does not escape root.
    /// </summary>
    public static string NormalizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return string.Empty;

        // Unicode NFC normalization
        string normalized = relativePath.Normalize(NormalizationForm.FormC);

        // Replace backslashes with forward slashes for internal consistency
        normalized = normalized.Replace('\\', '/');

        // Check for invalid control characters or null bytes
        foreach (char c in normalized)
        {
            if (c < 32 || c == 0 || c == '<' || c == '>' || c == '"' || c == '|' || c == '?' || c == '*')
            {
                throw new InvalidOperationException($"Path contains illegal character (code: {(int)c}): '{relativePath}'");
            }
        }

        // Split segments and validate
        string[] rawSegments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        List<string> cleanSegments = new();

        foreach (string segment in rawSegments)
        {
            string trimmed = segment.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed == ".")
                continue;

            if (trimmed == "..")
            {
                throw new UnauthorizedAccessException($"Path traversal attempt detected with '..' in path: '{relativePath}'");
            }

            // Check Windows reserved names
            string baseName = Path.GetFileNameWithoutExtension(trimmed);
            if (ReservedDeviceNames.Contains(baseName))
            {
                throw new InvalidOperationException($"Path contains Windows reserved device name '{trimmed}' in: '{relativePath}'");
            }

            cleanSegments.Add(trimmed);
        }

        return string.Join('/', cleanSegments);
    }

    /// <summary>
    /// Validates that a target full path resides strictly inside rootPath.
    /// Throws UnauthorizedAccessException if path traversal occurs.
    /// </summary>
    public static string ValidateAndResolvePath(string rootPath, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            throw new ArgumentException("Root path cannot be empty", nameof(rootPath));

        string normalizedRelative = NormalizeRelativePath(relativePath);
        
        string fullRoot = Path.GetFullPath(rootPath);
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            fullRoot += Path.DirectorySeparatorChar;
        }

        // Convert standard relative path to OS path
        string osRelative = normalizedRelative.Replace('/', Path.DirectorySeparatorChar);
        string combined = Path.GetFullPath(Path.Combine(fullRoot, osRelative));

        if (!combined.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) && combined != fullRoot.TrimEnd(Path.DirectorySeparatorChar))
        {
            throw new UnauthorizedAccessException(
                $"Access denied: target path '{combined}' escapes configured root '{fullRoot}'");
        }

        return combined;
    }

    private static readonly HashSet<string> IgnoredSystemRootEntries = new(StringComparer.OrdinalIgnoreCase)
    {
        "System Volume Information",
        "$RECYCLE.BIN",
        "$Recycle.Bin",
        "Recovery",
        "$WinREAgent",
        "pagefile.sys",
        "hiberfil.sys",
        "swapfile.sys",
        "dumpstack.log.tmp",
        "Thumbs.db",
        "desktop.ini",
        DriveSyncMetaFolder
    };

    /// <summary>
    /// Returns true if the path points to internal metadata folder (.drivesync).
    /// </summary>
    public static bool IsMetadataPath(string relativePath)
    {
        string norm = NormalizeRelativePath(relativePath);
        return norm.Equals(DriveSyncMetaFolder, StringComparison.OrdinalIgnoreCase) ||
               norm.StartsWith(DriveSyncMetaFolder + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true if the relative path belongs to OS system volume folders, recycle bins,
    /// page/hibernation files, or internal DriveSync metadata.
    /// </summary>
    public static bool IsIgnoredSystemPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;

        string norm;
        try
        {
            norm = NormalizeRelativePath(relativePath);
        }
        catch
        {
            return true; // Ignore invalid paths safely
        }

        if (IsMetadataPath(norm)) return true;

        string firstSegment = norm.Split('/')[0];
        if (IgnoredSystemRootEntries.Contains(firstSegment)) return true;

        string fileName = Path.GetFileName(norm);
        if (IgnoredSystemRootEntries.Contains(fileName)) return true;

        return false;
    }
}
