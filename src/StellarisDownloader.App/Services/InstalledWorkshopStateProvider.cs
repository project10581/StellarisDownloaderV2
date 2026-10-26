using System.IO;
using System.Security;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App.Services;

public sealed class InstalledWorkshopStateProvider : IInstalledWorkshopStateProvider
{
    private readonly ISettingsStore settingsStore;
    private readonly IModRepository modRepository;

    public InstalledWorkshopStateProvider(
        ISettingsStore settingsStore,
        IModRepository modRepository)
    {
        this.settingsStore = settingsStore;
        this.modRepository = modRepository;
    }

    public async Task<IReadOnlyList<string>> GetInstalledWorkshopIdsAsync(
        CancellationToken cancellationToken = default)
    {
        var loadResult = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loadResult.RequiresInitialization
            || string.IsNullOrWhiteSpace(loadResult.Settings.LibraryRoot)
            || !TryNormalizePath(loadResult.Settings.LibraryRoot, out var libraryRoot))
        {
            return [];
        }

        var cacheState = await modRepository.GetCacheStateAsync(
            libraryRoot,
            cancellationToken).ConfigureAwait(false);
        if (cacheState.State != CacheState.Valid)
        {
            return [];
        }

        var records = await modRepository.ListAsync(
            libraryRoot,
            cancellationToken).ConfigureAwait(false);
        var installedIds = new List<string>(records.Count);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var record in records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (seenIds.Contains(record.WorkshopId)
                || !IsInstalledDirectChild(record, libraryRoot))
            {
                continue;
            }

            seenIds.Add(record.WorkshopId);
            installedIds.Add(record.WorkshopId);
        }

        return installedIds;
    }

    private static bool IsInstalledDirectChild(ModRecord record, string libraryRoot)
    {
        if (!WorkshopId.IsValid(record.WorkshopId))
        {
            return false;
        }

        try
        {
            var expectedPath = PathUtil.Normalize(Path.Combine(libraryRoot, record.WorkshopId));
            if (!TryNormalizePath(record.ContentPath, out var contentPath))
            {
                return false;
            }
            var parentPath = Path.GetDirectoryName(expectedPath);
            if (!PathUtil.Equal(contentPath, expectedPath)
                || !PathUtil.Equal(parentPath, libraryRoot))
            {
                return false;
            }

            return LocalModDirectoryScanner.IsRegularNonEmptyDirectory(expectedPath);
        }
        catch (Exception exception) when (OperationalFailure.IsRecoverable(exception))
        {
            return false;
        }
    }

    private static bool TryNormalizePath(string path, out string normalizedPath)
    {
        try
        {
            normalizedPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            // Path.GetFullPath rejects malformed paths read from settings or cached external records.
            normalizedPath = string.Empty;
            return false;
        }

        normalizedPath = Path.TrimEndingDirectorySeparator(normalizedPath);
        return true;
    }

}
