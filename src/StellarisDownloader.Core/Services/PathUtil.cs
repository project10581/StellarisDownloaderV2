namespace StellarisDownloader.Core.Services;

internal static class PathUtil
{
    public static string Normalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static bool Equal(string? left, string? right) =>
        left is not null && right is not null
        && string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
}
