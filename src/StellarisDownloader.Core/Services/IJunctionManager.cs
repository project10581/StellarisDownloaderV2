using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Services;

public interface IJunctionManager
{
    Task<JunctionState> InspectAsync(
        string junctionPath,
        CancellationToken cancellationToken = default);

    Task<JunctionUpdate> SetTargetAsync(
        string junctionPath,
        string targetPath,
        CancellationToken cancellationToken = default);

    Task RestoreAsync(
        JunctionUpdate update,
        CancellationToken cancellationToken = default);
}
