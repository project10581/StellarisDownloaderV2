namespace StellarisDownloader.Core.Services;

public interface IFileDeletionService
{
    Task SendDirectoryToRecycleBinAsync(
        string directoryPath,
        CancellationToken cancellationToken = default);

    Task DeleteDirectoryPermanentlyAsync(
        string directoryPath,
        CancellationToken cancellationToken = default);
}
