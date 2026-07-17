namespace StellarisDownloader.Core.Models;

public sealed record DownloadBatchResult
{
    public IReadOnlyList<DownloadResult> Results { get; init; } = [];

    public int SucceededCount => Results.Count(result => result.Status == OperationStatus.Succeeded);

    public int FailedCount => Results.Count(result => result.Status == OperationStatus.Failed);

    public int CancelledCount => Results.Count(result => result.Status == OperationStatus.Cancelled);
}
