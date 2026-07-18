namespace StellarisDownloader.App.Services;

public interface IInstalledWorkshopStateProvider
{
    Task<IReadOnlyList<string>> GetInstalledWorkshopIdsAsync(
        CancellationToken cancellationToken = default);
}
