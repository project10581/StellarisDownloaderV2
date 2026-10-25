using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Services;

public interface IWorkshopFavoritesStore
{
    Task<IReadOnlyList<WorkshopFavorite>> LoadAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkshopFavorite>> SetFavoriteAsync(
        string workshopId, bool favorite, string? title = null, CancellationToken cancellationToken = default);
}
