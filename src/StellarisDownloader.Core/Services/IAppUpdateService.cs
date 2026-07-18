using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Services;

public interface IAppUpdateService
{
    Task<AppUpdateInfo> CheckAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<AppUpdateInfo> DownloadAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task ApplyAndRestartAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
