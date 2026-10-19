using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using DriveSync.App.Services;
using DriveSync.Core.Engine;
using DriveSync.Core.Models;
using DriveSync.Core.Storage;
using DriveSync.Providers;

namespace DriveSync.App.ViewModels;

public class ConflictsViewModel : ViewModelBase
{
    private readonly ISyncStateStore _stateStore;
    private readonly SyncCoordinatorService _coordinator;
    private readonly IConflictResolver _conflictResolver;
    private SyncConflict? _selectedConflict;
    private bool _isBusy;
    private string _statusMessage = string.Empty;

    public ObservableCollection<SyncConflict> UnresolvedConflicts { get; } = new();

    public SyncConflict? SelectedConflict
    {
        get => _selectedConflict;
        set => SetProperty(ref _selectedConflict, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public ICommand KeepSourceCommand { get; }
    public ICommand KeepDestinationCommand { get; }
    public ICommand KeepBothCommand { get; }
    public ICommand RefreshCommand { get; }

    public ConflictsViewModel(
        ISyncStateStore stateStore,
        SyncCoordinatorService coordinator,
        IConflictResolver conflictResolver)
    {
        _stateStore = stateStore;
        _coordinator = coordinator;
        _conflictResolver = conflictResolver;

        KeepSourceCommand = new RelayCommand(async () => await ResolveSelectedAsync(ConflictResolutionChoice.KeepSource));
        KeepDestinationCommand = new RelayCommand(async () => await ResolveSelectedAsync(ConflictResolutionChoice.KeepDestination));
        KeepBothCommand = new RelayCommand(async () => await ResolveSelectedAsync(ConflictResolutionChoice.KeepBothWithRename));
        RefreshCommand = new RelayCommand(async () => await LoadConflictsAsync());

        _ = LoadConflictsAsync();
    }

    public async Task LoadConflictsAsync()
    {
        IsBusy = true;
        try
        {
            var conflicts = await _stateStore.GetConflictsAsync(unresolvedOnly: true);
            UnresolvedConflicts.Clear();
            foreach (var c in conflicts) UnresolvedConflicts.Add(c);
            StatusMessage = UnresolvedConflicts.Count == 0
                ? "No active conflicts detected. All synchronized items are consistent."
                : $"{UnresolvedConflicts.Count} conflict(s) require your review.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error loading conflicts: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ResolveSelectedAsync(ConflictResolutionChoice choice)
    {
        if (SelectedConflict == null) return;

        var pair = await _stateStore.GetPairAsync(SelectedConflict.PairId);
        if (pair == null)
        {
            MessageBox.Show("Associated sync pair could not be found.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        string choiceText = choice switch
        {
            ConflictResolutionChoice.KeepSource => $"Keep '{pair.Source.DisplayName}' version and overwrite other side",
            ConflictResolutionChoice.KeepDestination => $"Keep '{pair.Destination.DisplayName}' version and overwrite other side",
            ConflictResolutionChoice.KeepBothWithRename => "Keep both files by preserving destination with a timestamped rename copy",
            _ => "Manual"
        };

        var confirm = MessageBox.Show(
            $"Apply resolution for '{SelectedConflict.RelativePath}'?\n\nChoice: {choiceText}\n\nPrevious versions are preserved in version recovery before any overwrite.",
            "Confirm Conflict Resolution",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes) return;

        IsBusy = true;
        try
        {
            var ops = _conflictResolver.ResolveConflict(pair, SelectedConflict, choice);
            
            // Execute resolution operations
            using var srcProv = new FileSystemProvider(pair.Source);
            using var dstProv = new FileSystemProvider(pair.Destination);

            foreach (var op in ops)
            {
                var src = op.SourceSide == EndpointSide.Source ? srcProv : dstProv;
                var dst = op.TargetSide == EndpointSide.Destination ? dstProv : srcProv;

                if (op.ActionType == SyncActionType.Rename && !string.IsNullOrEmpty(op.NewRelativePath))
                {
                    await dst.MoveOrRenameAsync(op.RelativePath, op.NewRelativePath);
                }
                else if (op.ActionType is SyncActionType.Create or SyncActionType.Update)
                {
                    using var stream = await src.OpenReadAsync(op.RelativePath);
                    var meta = await src.GetItemMetadataAsync(op.RelativePath) ?? new SyncItemMetadata();
                    await dst.WriteFileAsync(op.RelativePath, stream, meta);
                }
                else if (op.ActionType == SyncActionType.Delete)
                {
                    await dst.DeleteFileAsync(op.RelativePath);
                }
            }

            await _stateStore.ResolveConflictAsync(SelectedConflict.Id, choice, $"Resolved manually via {choice}");
            await LoadConflictsAsync();
            MessageBox.Show($"Conflict for '{SelectedConflict.RelativePath}' resolved successfully!", "Conflict Resolved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to apply resolution: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
