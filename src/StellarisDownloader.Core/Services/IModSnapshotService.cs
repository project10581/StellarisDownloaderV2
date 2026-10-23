using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Services;

public interface IModSnapshotService
{
    Task<ModSnapshot> CaptureAsync(CancellationToken cancellationToken = default);
}
