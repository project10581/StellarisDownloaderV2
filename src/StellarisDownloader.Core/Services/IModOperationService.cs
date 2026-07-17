using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Services;

public interface IModOperationService
{
    Task<DownloadBatchResult> DownloadBatchAsync(
        IReadOnlyCollection<DownloadRequest> requests,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UpdateCheckResult>> CheckUpdatesAsync(
        string libraryRoot,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<DownloadBatchResult> UpdateSelectedAsync(
        string libraryRoot,
        IReadOnlyCollection<string> workshopIds,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
