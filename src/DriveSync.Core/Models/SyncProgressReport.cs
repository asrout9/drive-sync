namespace DriveSync.Core.Models;

public class SyncProgressReport
{
    public string PairId { get; set; } = string.Empty;
    public string CurrentOperationDescription { get; set; } = string.Empty;
    public string CurrentItemPath { get; set; } = string.Empty;
    public int ProcessedFilesCount { get; set; }
    public int TotalFilesCount { get; set; }
    public long BytesTransferred { get; set; }
    public long TotalBytesToTransfer { get; set; }
    public double BytesPerSecond { get; set; }
    public double ProgressPercentage => TotalBytesToTransfer > 0 
        ? Math.Min(100.0, (double)BytesTransferred / TotalBytesToTransfer * 100.0) 
        : (TotalFilesCount > 0 ? (double)ProcessedFilesCount / TotalFilesCount * 100.0 : 0.0);
}
