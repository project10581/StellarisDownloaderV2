namespace StellarisDownloader.App.ViewModels;

public enum DownloadQueueItemStatus
{
    Pending,
    ResolvingMetadata,
    Ready,
    Downloading,
    Succeeded,
    Failed,
    Cancelled,
}
