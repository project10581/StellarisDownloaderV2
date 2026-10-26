using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Services;

public interface ISteamCmdService
{
    Task<IReadOnlyList<DownloadResult>> DownloadBatchAsync(
        IReadOnlyList<DownloadRequest> requests,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<SteamCmdInstallationResult> EnsureInstalledAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
