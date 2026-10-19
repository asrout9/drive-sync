using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DriveSync.Core.Models;

namespace DriveSync.Core.Engine;

public interface ISsdDetector : IDisposable
{
    Task<IReadOnlyList<DriveInfoModel>> DetectRemovableDrivesAsync(CancellationToken ct = default);
    Task<bool> VerifyDriveIdentityAsync(string rootPath, string? expectedSerialNumber, string? expectedToken, CancellationToken ct = default);
    Task<string> InitializeDriveSyncTokenAsync(string rootPath, CancellationToken ct = default);

    event EventHandler<DriveInfoModel>? DriveConnected;
    event EventHandler<string>? DriveDisconnected; // Passes drive letter or volume serial
}
