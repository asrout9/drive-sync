using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DriveSync.Core.Engine;
using DriveSync.Core.Models;
using DriveSync.Core.Storage;
using DriveSync.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DriveSync.App.Services;

public class SyncCoordinatorService : IDisposable
{
    private readonly ISyncEngine _syncEngine;
    private readonly ISyncStateStore _stateStore;
    private readonly IJournalStore _journalStore;
    private readonly ISsdDetector _ssdDetector;
    private readonly ICloudCredentialStore _credentialStore;
    private readonly ILogger<SyncCoordinatorService> _logger;

    private readonly ConcurrentDictionary<string, SyncPair> _pairs = new();
    private readonly ConcurrentDictionary<string, IFileWatcher> _activeWatchers = new();
    private readonly ConcurrentDictionary<string, System.Timers.Timer> _scheduledTimers = new();
    private readonly System.Timers.Timer _backgroundHealthTimer;

    public event Action? PairsChanged;
    public event Action<SyncProgressReport>? ProgressChanged;
    public event Action<string, SyncResult>? SyncCompleted;
    public event Action<SyncStatus, string>? GlobalStatusChanged;

    public IReadOnlyList<SyncPair> Pairs => _pairs.Values.ToList();
    public SyncStatus OverallStatus { get; private set; } = SyncStatus.Idle;
    public string OverallStatusDescription { get; private set; } = "Ready";

    public SyncCoordinatorService(
        ISyncEngine syncEngine,
        ISyncStateStore stateStore,
        IJournalStore journalStore,
        ISsdDetector ssdDetector,
        ICloudCredentialStore credentialStore,
        ILogger<SyncCoordinatorService>? logger = null)
    {
        _syncEngine = syncEngine;
        _stateStore = stateStore;
        _journalStore = journalStore;
        _ssdDetector = ssdDetector;
        _credentialStore = credentialStore;
        _logger = logger ?? NullLogger<SyncCoordinatorService>.Instance;

        _ssdDetector.DriveDisconnected += OnDriveDisconnected;
        _ssdDetector.DriveConnected += OnDriveConnected;

        _backgroundHealthTimer = new System.Timers.Timer(10000);
        _backgroundHealthTimer.Elapsed += (s, e) => UpdateOverallStatus();
        _backgroundHealthTimer.Start();
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await _stateStore.InitializeAsync(ct).ConfigureAwait(false);
        await _journalStore.InitializeAsync(ct).ConfigureAwait(false);
        await ReloadPairsAsync(ct).ConfigureAwait(false);
    }

    public async Task ReloadPairsAsync(CancellationToken ct = default)
    {
        var list = await _stateStore.GetAllPairsAsync(ct).ConfigureAwait(false);
        _pairs.Clear();
        foreach (var p in list)
        {
            _pairs[p.Id] = p;
            SetupPairAutomation(p);
        }
        UpdateOverallStatus();
        PairsChanged?.Invoke();
    }

    public async Task SavePairAsync(SyncPair pair, CancellationToken ct = default)
    {
        await _stateStore.SavePairAsync(pair, ct).ConfigureAwait(false);
        _pairs[pair.Id] = pair;
        SetupPairAutomation(pair);
        UpdateOverallStatus();
        PairsChanged?.Invoke();
    }

    public async Task DeletePairAsync(string pairId, CancellationToken ct = default)
    {
        StopPairAutomation(pairId);
        _pairs.TryRemove(pairId, out _);
        await _stateStore.DeletePairAsync(pairId, ct).ConfigureAwait(false);
        UpdateOverallStatus();
        PairsChanged?.Invoke();
    }

    public async Task<SyncResult> TriggerSyncAsync(string pairId, bool dryRun = false, CancellationToken ct = default)
    {
        if (!_pairs.TryGetValue(pairId, out var pair))
        {
            throw new ArgumentException($"Sync pair '{pairId}' not found", nameof(pairId));
        }

        if (pair.IsPaused)
        {
            throw new InvalidOperationException($"Pair '{pair.Name}' is currently paused. Please resume first.");
        }

        pair.Status = SyncStatus.Scanning;
        UpdateOverallStatus();
        PairsChanged?.Invoke();

        ISyncProvider sourceProvider = null!;
        ISyncProvider destProvider = null!;

        try
        {
            // Verify SSD availability if applicable
            if (pair.Source.ProviderType == ProviderType.ExternalSsd)
            {
                bool valid = await _ssdDetector.VerifyDriveIdentityAsync(
                    pair.Source.RootPath, pair.Source.VolumeSerialNumber, pair.Source.SsdUniqueId, ct).ConfigureAwait(false);
                if (!valid)
                {
                    pair.Status = SyncStatus.Offline;
                    pair.LastErrorMessage = "External SSD is disconnected or identity could not be verified.";
                    UpdateOverallStatus();
                    PairsChanged?.Invoke();
                    return new SyncResult { PairId = pairId, Success = false, ErrorMessages = { pair.LastErrorMessage } };
                }
            }

            if (pair.Destination.ProviderType == ProviderType.ExternalSsd)
            {
                bool valid = await _ssdDetector.VerifyDriveIdentityAsync(
                    pair.Destination.RootPath, pair.Destination.VolumeSerialNumber, pair.Destination.SsdUniqueId, ct).ConfigureAwait(false);
                if (!valid)
                {
                    pair.Status = SyncStatus.Offline;
                    pair.LastErrorMessage = "External SSD is disconnected or identity could not be verified.";
                    UpdateOverallStatus();
                    PairsChanged?.Invoke();
                    return new SyncResult { PairId = pairId, Success = false, ErrorMessages = { pair.LastErrorMessage } };
                }
            }

            sourceProvider = CreateProvider(pair.Source);
            destProvider = CreateProvider(pair.Destination);

            // Analysis phase
            pair.Status = SyncStatus.Scanning;
            UpdateOverallStatus();
            PairsChanged?.Invoke();
            ProgressChanged?.Invoke(new SyncProgressReport
            {
                PairId = pair.Id,
                CurrentOperationDescription = "Analyzing directory structures and calculating differences...",
                ProcessedFilesCount = 0,
                TotalFilesCount = 0
            });

            var plan = await _syncEngine.AnalyzeAsync(pair, sourceProvider, destProvider, dryRun, ct).ConfigureAwait(false);

            if (plan.Conflicts.Count > 0)
            {
                pair.ActiveConflictCount = plan.Conflicts.Count;
            }

            pair.Status = SyncStatus.Syncing;
            UpdateOverallStatus();
            PairsChanged?.Invoke();

            var progress = new Progress<SyncProgressReport>(report =>
            {
                ProgressChanged?.Invoke(report);
            });

            if (plan.Operations.Count > 0)
            {
                ProgressChanged?.Invoke(new SyncProgressReport
                {
                    PairId = pair.Id,
                    CurrentOperationDescription = $"Synchronizing {plan.Operations.Count} items...",
                    ProcessedFilesCount = 0,
                    TotalFilesCount = plan.Operations.Count,
                    TotalBytesToTransfer = plan.TotalBytesToTransfer
                });
            }

            // Execution phase
            var result = await _syncEngine.ExecuteSyncAsync(pair, sourceProvider, destProvider, plan, progress, ct).ConfigureAwait(false);

            pair.LastSyncTimeUtc = DateTimeOffset.UtcNow;
            pair.HasCompletedInitialSync = true;
            pair.ActiveConflictCount = result.ConflictsDetected;
            pair.LastSyncSummary = $"{result.FilesCreated} created, {result.FilesUpdated} updated, {result.FilesDeleted} deleted";

            if (result.Success)
            {
                pair.Status = result.HasConflicts ? SyncStatus.ConflictsDetected : SyncStatus.UpToDate;
                pair.LastErrorMessage = null;
            }
            else
            {
                pair.Status = result.Cancelled ? SyncStatus.Paused : (result.HasConflicts ? SyncStatus.ConflictsDetected : SyncStatus.Error);
                pair.LastErrorMessage = string.Join("; ", result.ErrorMessages);
            }

            await _stateStore.SavePairAsync(pair, CancellationToken.None).ConfigureAwait(false);
            SyncCompleted?.Invoke(pairId, result);
            return result;
        }
        finally
        {
            if (sourceProvider != null) await sourceProvider.DisposeAsync().ConfigureAwait(false);
            if (destProvider != null) await destProvider.DisposeAsync().ConfigureAwait(false);

            UpdateOverallStatus();
            PairsChanged?.Invoke();
        }
    }

    public void PausePair(string pairId)
    {
        if (_pairs.TryGetValue(pairId, out var pair))
        {
            pair.IsPaused = true;
            pair.Status = SyncStatus.Paused;
            _syncEngine.Pause(pairId);
            _stateStore.SavePairAsync(pair).GetAwaiter().GetResult();
            UpdateOverallStatus();
            PairsChanged?.Invoke();
        }
    }

    public void ResumePair(string pairId)
    {
        if (_pairs.TryGetValue(pairId, out var pair))
        {
            pair.IsPaused = false;
            pair.Status = SyncStatus.Idle;
            _syncEngine.Resume(pairId);
            _stateStore.SavePairAsync(pair).GetAwaiter().GetResult();
            UpdateOverallStatus();
            PairsChanged?.Invoke();
        }
    }

    public void CancelPair(string pairId)
    {
        _syncEngine.Cancel(pairId);
    }

    private ISyncProvider CreateProvider(SyncEndpoint endpoint)
    {
        return endpoint.ProviderType switch
        {
            ProviderType.OneDrive => new OneDriveProvider(endpoint, _credentialStore),
            ProviderType.GoogleDrive => new GoogleDriveProvider(endpoint, _credentialStore),
            _ => new FileSystemProvider(endpoint)
        };
    }

    private void SetupPairAutomation(SyncPair pair)
    {
        StopPairAutomation(pair.Id);

        if (!pair.IsEnabled) return;

        // 1. File watcher for live sync
        if (pair.EnableRealtimeWatcher && pair.Source.ProviderType is ProviderType.Local or ProviderType.ExternalSsd)
        {
            try
            {
                var prov = new FileSystemProvider(pair.Source);
                var watcher = prov.CreateWatcher(e =>
                {
                    if (!pair.IsPaused && !_syncEngine.IsRunning(pair.Id))
                    {
                        _ = TriggerSyncAsync(pair.Id);
                    }
                });
                watcher?.Start();
                if (watcher != null) _activeWatchers[pair.Id] = watcher;
            }
            catch { }
        }

        // 2. Periodic scheduled sync
        if (pair.ScheduledIntervalSeconds > 0)
        {
            var timer = new System.Timers.Timer(pair.ScheduledIntervalSeconds * 1000.0);
            timer.AutoReset = true;
            timer.Elapsed += (s, e) =>
            {
                if (!pair.IsPaused && !_syncEngine.IsRunning(pair.Id))
                {
                    _ = TriggerSyncAsync(pair.Id);
                }
            };
            timer.Start();
            _scheduledTimers[pair.Id] = timer;
        }
    }

    private void StopPairAutomation(string pairId)
    {
        if (_activeWatchers.TryRemove(pairId, out var w))
        {
            w.Stop();
            w.Dispose();
        }
        if (_scheduledTimers.TryRemove(pairId, out var t))
        {
            t.Stop();
            t.Dispose();
        }
    }

    private void OnDriveDisconnected(object? sender, string driveLetter)
    {
        _logger.LogInformation("SSD removed: {Drive}. Verifying active pairs...", driveLetter);
        foreach (var pair in _pairs.Values)
        {
            bool affectsSource = pair.Source.ProviderType == ProviderType.ExternalSsd &&
                                 pair.Source.RootPath.StartsWith(driveLetter, StringComparison.OrdinalIgnoreCase);
            bool affectsDest = pair.Destination.ProviderType == ProviderType.ExternalSsd &&
                               pair.Destination.RootPath.StartsWith(driveLetter, StringComparison.OrdinalIgnoreCase);

            if (affectsSource || affectsDest)
            {
                _logger.LogWarning("Pausing pair '{PairName}' because SSD {Drive} disconnected", pair.Name, driveLetter);
                _syncEngine.Pause(pair.Id);
                pair.Status = SyncStatus.Offline;
                pair.LastErrorMessage = $"External SSD ({driveLetter}) disconnected. Never treating as deletion.";
            }
        }
        UpdateOverallStatus();
        PairsChanged?.Invoke();
    }

    private void OnDriveConnected(object? sender, DriveInfoModel drive)
    {
        _logger.LogInformation("SSD connected: {Drive} ({Model}). Checking affected pairs...", drive.DriveLetter, drive.DeviceModel);
        foreach (var pair in _pairs.Values)
        {
            bool affectsSource = pair.Source.ProviderType == ProviderType.ExternalSsd &&
                                 pair.Source.RootPath.StartsWith(drive.DriveLetter, StringComparison.OrdinalIgnoreCase);
            bool affectsDest = pair.Destination.ProviderType == ProviderType.ExternalSsd &&
                               pair.Destination.RootPath.StartsWith(drive.DriveLetter, StringComparison.OrdinalIgnoreCase);

            if (affectsSource || affectsDest)
            {
                _logger.LogInformation("Resuming pair '{PairName}' after verifying SSD reconnection", pair.Name);
                pair.Status = SyncStatus.Idle;
                pair.LastErrorMessage = null;
                _syncEngine.Resume(pair.Id);
                // Trigger reconciliation scan on reconnect
                _ = TriggerSyncAsync(pair.Id);
            }
        }
        UpdateOverallStatus();
        PairsChanged?.Invoke();
    }

    private void UpdateOverallStatus()
    {
        var activePairs = _pairs.Values.ToList();
        if (activePairs.Count == 0)
        {
            OverallStatus = SyncStatus.Idle;
            OverallStatusDescription = "No sync pairs configured";
        }
        else if (activePairs.Any(p => p.Status == SyncStatus.Syncing || p.Status == SyncStatus.Scanning))
        {
            OverallStatus = SyncStatus.Syncing;
            OverallStatusDescription = "Synchronizing files...";
        }
        else if (activePairs.Any(p => p.ActiveConflictCount > 0 || p.Status == SyncStatus.ConflictsDetected))
        {
            OverallStatus = SyncStatus.ConflictsDetected;
            OverallStatusDescription = "Conflicts detected — attention required";
        }
        else if (activePairs.Any(p => p.Status == SyncStatus.Offline))
        {
            OverallStatus = SyncStatus.Offline;
            OverallStatusDescription = "External SSD offline or unreachable";
        }
        else if (activePairs.Any(p => p.Status == SyncStatus.Error))
        {
            OverallStatus = SyncStatus.Error;
            OverallStatusDescription = "Sync error detected";
        }
        else if (activePairs.Any(p => p.IsPaused || p.Status == SyncStatus.Paused))
        {
            OverallStatus = SyncStatus.Paused;
            OverallStatusDescription = "Synchronization paused";
        }
        else
        {
            OverallStatus = SyncStatus.UpToDate;
            OverallStatusDescription = "All locations up to date";
        }

        GlobalStatusChanged?.Invoke(OverallStatus, OverallStatusDescription);
    }

    public void Dispose()
    {
        _backgroundHealthTimer.Stop();
        _backgroundHealthTimer.Dispose();
        foreach (var w in _activeWatchers.Values) w.Dispose();
        _activeWatchers.Clear();
        foreach (var t in _scheduledTimers.Values) t.Dispose();
        _scheduledTimers.Clear();
    }
}
