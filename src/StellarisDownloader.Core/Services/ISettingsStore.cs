using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Services;

public interface ISettingsStore
{
    Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}
