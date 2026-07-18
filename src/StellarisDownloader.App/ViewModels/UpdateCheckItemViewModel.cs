using CommunityToolkit.Mvvm.ComponentModel;
using StellarisDownloader.Core.Models;

namespace StellarisDownloader.App.ViewModels;

public sealed class UpdateCheckItemViewModel : ObservableObject
{
    private bool isSelected;
    private bool selectionEnabled = true;
    private OperationStatus? lastOperationStatus;
    private string? lastOperationError;

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

    public bool IsSelectionEnabled => selectionEnabled && IsSelectable;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (!IsSelectionEnabled)
            {
                return;
            }

            SetProperty(ref isSelected, value);
        }
    }

    public OperationStatus? LastOperationStatus
    {
        get => lastOperationStatus;
        private set => SetProperty(ref lastOperationStatus, value);
    }

    public string? LastOperationError
    {
        get => lastOperationError;
        private set => SetProperty(ref lastOperationError, value);
    }

    internal void SetSelectionEnabled(bool value)
    {
        if (SetProperty(ref selectionEnabled, value))
        {
            OnPropertyChanged(nameof(IsSelectionEnabled));
        }
    }

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
