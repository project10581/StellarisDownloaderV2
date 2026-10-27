using CommunityToolkit.Mvvm.ComponentModel;
using StellarisDownloader.Core.Models;

namespace StellarisDownloader.App.ViewModels;

public sealed partial class DownloadQueueItemViewModel : ObservableObject
{
    public DownloadQueueItemViewModel(string workshopId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workshopId);
        WorkshopId = workshopId;
    }

    public string WorkshopId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayTitle))]
    public partial string? Title { get; private set; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? WorkshopId : Title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTerminal))]
    public partial DownloadQueueItemStatus Status { get; private set; } = DownloadQueueItemStatus.Pending;

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    public partial string? MetadataError { get; private set; }

    public bool IsTerminal => Status is DownloadQueueItemStatus.Succeeded
        or DownloadQueueItemStatus.Failed
        or DownloadQueueItemStatus.Cancelled;

    internal void BeginMetadataLookup()
    {
        Status = DownloadQueueItemStatus.ResolvingMetadata;
        MetadataError = null;
    }

    internal void CompleteMetadataLookup(WorkshopMetadata? metadata, string? lookupError = null)
    {
        Title = metadata?.Title;
        MetadataError = lookupError;
        Status = DownloadQueueItemStatus.Ready;
    }

    internal void MarkReady()
    {
        Status = DownloadQueueItemStatus.Ready;
        Error = null;
    }

    internal void MarkDownloading()
    {
        Status = DownloadQueueItemStatus.Downloading;
        Error = null;
    }

    internal void Complete(DownloadResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        Status = result.Status switch
        {
            OperationStatus.Succeeded => DownloadQueueItemStatus.Succeeded,
            OperationStatus.Failed => DownloadQueueItemStatus.Failed,
            OperationStatus.Cancelled => DownloadQueueItemStatus.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(result)),
        };
        Error = result.Error;
    }

    internal void Fail(string error)
    {
        Status = DownloadQueueItemStatus.Failed;
        Error = error;
    }

    internal void Cancel(string error)
    {
        Status = DownloadQueueItemStatus.Cancelled;
        Error = error;
    }
}
