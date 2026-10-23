using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Services;

public static class LocalModDirectoryScanner
{
    public static LocalLibraryInventory Scan(
        string libraryRoot,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot));
        // Enumerating a missing or inaccessible root must fail, never produce an empty snapshot.
        var directories = new DirectoryInfo(root).EnumerateDirectories()
            .OrderBy(directory => directory.Name, StringComparer.Ordinal).ToArray();
        var mods = new List<DirectoryInfo>();
        var emptyIds = new List<string>();
        var linkedIds = new List<string>();
        var ignoredCount = 0;
        for (var index = 0; index < directories.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = directories[index];
            if (!directory.Name.All(char.IsAsciiDigit))
            {
                ignoredCount++;
            }
            else if ((File.GetAttributes(directory.FullName) & FileAttributes.ReparsePoint) != 0)
            {
                linkedIds.Add(directory.Name);
            }
            else if (IsRegularNonEmptyDirectory(directory.FullName))
            {
                mods.Add(directory);
            }
            else
            {
                emptyIds.Add(directory.Name);
            }

            progress?.Report(new OperationProgress(
                "ScanningLibrary", index + 1, directories.Length, directory.Name,
                $"Scanned library directory {directory.Name}."));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new LocalLibraryInventory(root, mods, emptyIds, linkedIds, ignoredCount);
    }

    public static bool IsRegularNonEmptyDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        return (attributes & FileAttributes.Directory) != 0
            && (attributes & FileAttributes.ReparsePoint) == 0
            && Directory.EnumerateFileSystemEntries(path).Any();
    }
}

public sealed record LocalLibraryInventory(
    string LibraryRoot,
    IReadOnlyList<DirectoryInfo> Directories,
    IReadOnlyList<string> EmptyWorkshopIds,
    IReadOnlyList<string> LinkedWorkshopIds,
    int IgnoredDirectoryCount);
