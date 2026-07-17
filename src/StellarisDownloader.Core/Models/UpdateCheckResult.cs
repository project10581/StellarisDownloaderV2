namespace StellarisDownloader.Core.Models;

public sealed record UpdateCheckResult
{
    public required string WorkshopId { get; init; }

    public string? Title { get; init; }

    public required UpdateState State { get; init; }

    public DateTimeOffset? LatestRemoteUpdatedAtUtc { get; init; }

    public DateTimeOffset? InstalledWorkshopUpdatedAtUtc { get; init; }

    public bool UsesApproximateLocalTimestamp { get; init; }

    public string? Error { get; init; }
}
