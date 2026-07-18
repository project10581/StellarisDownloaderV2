namespace StellarisDownloader.Core.Models;

public sealed record AppUpdateInfo
{
    public required string CurrentVersion { get; init; }

    public string? LatestVersion { get; init; }

    public string? ReleaseNotes { get; init; }

    public bool IsInstalled { get; init; }

    public bool IsUpdateAvailable { get; init; }

    public bool IsDownloaded { get; init; }
}
