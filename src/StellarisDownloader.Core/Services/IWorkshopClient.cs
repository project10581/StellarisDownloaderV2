using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Services;

public interface IWorkshopClient
{
    Task<IReadOnlyDictionary<string, WorkshopMetadata>> GetMetadataBatchAsync(
        IReadOnlyCollection<string> workshopIds,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
