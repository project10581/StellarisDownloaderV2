using CommunityToolkit.Mvvm.ComponentModel;
using StellarisDownloader.Core.Models;

namespace StellarisDownloader.App.ViewModels;

public sealed partial class UpdateCheckItemViewModel : ObservableObject
{
    private bool isSelected;

    public UpdateCheckItemViewModel(UpdateCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        WorkshopId = result.WorkshopId;
        Title = result.Title;
        State = result.State;
        LatestRemoteUpdatedAtUtc = result.LatestRemoteUpdatedAtUtc;
        InstalledWorkshopUpdatedAtUtc = result.InstalledWorkshopUpdatedAtUtc;
        UsesApproximateLocalTimestamp = result.UsesApproximateLocalTimestamp;
        Error = result.Error;
        isSelected = result.State == UpdateState.UpdateAvailable;
    }

    public string WorkshopId { get; }

    public string? Title { get; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? WorkshopId : Title;

    public UpdateState State { get; }

    public DateTimeOffset? LatestRemoteUpdatedAtUtc { get; }

    public DateTimeOffset? InstalledWorkshopUpdatedAtUtc { get; }

    public bool UsesApproximateLocalTimestamp { get; }

    public string? Error { get; }

    public bool IsSelectable => State == UpdateState.UpdateAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectionEnabled))]
    public partial bool SelectionEnabled { get; private set; } = true;

    public bool IsSelectionEnabled => SelectionEnabled && IsSelectable;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (!IsSelectionEnabled)
            {
                OnPropertyChanged(nameof(IsSelected));
                return;
            }

            SetProperty(ref isSelected, value);
        }
    }

    [ObservableProperty]
    public partial OperationStatus? LastOperationStatus { get; private set; }

    [ObservableProperty]
    public partial string? LastOperationError { get; private set; }

    internal void SetSelectionEnabled(bool value) => SelectionEnabled = value;

    internal void SetSelected(bool value) => SetProperty(ref isSelected, value, nameof(IsSelected));

    internal void ResetOperationResult()
    {
        LastOperationStatus = null;
        LastOperationError = null;
    }

    internal void SetOperationResult(OperationStatus status, string? error)
    {
        LastOperationStatus = status;
        LastOperationError = error;
        if (status == OperationStatus.Succeeded)
        {
            SetSelected(false);
        }
    }
}
