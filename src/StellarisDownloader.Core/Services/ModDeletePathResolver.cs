namespace StellarisDownloader.Core.Services;

public static class ModDeletePathResolver
{
    public static string Resolve(string libraryRoot, string workshopId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(workshopId);
        if (!workshopId.All(char.IsAsciiDigit))
        {
            throw new ArgumentException(
                "Workshop ID must contain only ASCII digits.",
                nameof(workshopId));
        }

        var normalizedRoot = NormalizePath(libraryRoot);
        var target = NormalizePath(Path.Combine(normalizedRoot, workshopId));
        var parent = Path.GetDirectoryName(target);
        if (!string.Equals(parent, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Refusing to delete a path outside the current library root: {target}");
        }

        if (string.Equals(target, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete the library root.");
        }

        if (TryGetAttributes(target, out var attributes))
        {
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Refusing to delete a reparse-point mod directory: {target}");
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                throw new InvalidOperationException(
                    $"Refusing to delete a mod target that is not a directory: {target}");
            }
        }

        return target;
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static string NormalizePath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
