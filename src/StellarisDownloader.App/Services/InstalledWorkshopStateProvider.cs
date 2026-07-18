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
        if (string.IsNullOrEmpty(record.WorkshopId)
            || !record.WorkshopId.All(char.IsAsciiDigit))
        {
            return false;
        }

        try
        {
            var expectedPath = NormalizePath(Path.Combine(libraryRoot, record.WorkshopId));
            var contentPath = NormalizePath(record.ContentPath);
            var parentPath = Path.GetDirectoryName(expectedPath);
            if (!PathsEqual(contentPath, expectedPath)
                || !PathsEqual(parentPath, libraryRoot))
            {
                return false;
            }

            var attributes = File.GetAttributes(expectedPath);
            if ((attributes & FileAttributes.Directory) == 0
                || (attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            using var entries = Directory.EnumerateFileSystemEntries(expectedPath).GetEnumerator();
            return entries.MoveNext();
        }
        catch (Exception exception) when (IsPerItemFileOrPathException(exception))
        {
            return false;
        }
    }

    private static bool TryNormalizePath(string path, out string normalizedPath)
    {
        try
        {
            normalizedPath = NormalizePath(path);
            return true;
        }
        catch (Exception exception) when (IsPerItemFileOrPathException(exception))
        {
            normalizedPath = string.Empty;
            return false;
        }
    }

    private static bool IsPerItemFileOrPathException(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or SecurityException
            or ArgumentException
            or NotSupportedException;

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool PathsEqual(string? left, string? right) =>
        left is not null
        && right is not null
        && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
