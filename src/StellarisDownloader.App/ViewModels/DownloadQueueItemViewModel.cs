using CommunityToolkit.Mvvm.ComponentModel;
using StellarisDownloader.Core.Models;

namespace StellarisDownloader.App.ViewModels;

public sealed class DownloadQueueItemViewModel : ObservableObject
{
    private string? title;
    private DownloadQueueItemStatus status = DownloadQueueItemStatus.Pending;
    private string? error;
    private string? metadataError;

    public DownloadQueueItemViewModel(string workshopId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workshopId);
        WorkshopId = workshopId;
    }

    public string WorkshopId { get; }

    public string? Title
    {
        get => title;
        private set
        {
            if (SetProperty(ref title, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? WorkshopId : Title;

    public DownloadQueueItemStatus Status
    {
        get => status;
        private set
        {
            if (SetProperty(ref status, value))
            {
                OnPropertyChanged(nameof(IsTerminal));
            }
        }
    }

    public string? Error
    {
        get => error;
        private set => SetProperty(ref error, value);
    }

    public string? MetadataError
    {
        get => metadataError;
        private set => SetProperty(ref metadataError, value);
    }

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
