namespace StellarisDownloader.Core.Models;

public sealed record WorkshopMetadata
{
    public required string WorkshopId { get; init; }

    public int AppId { get; init; } = 281990;

    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? PreviewUrl { get; init; }

    public string? CreatorId { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public DateTimeOffset? LatestRemoteUpdatedAtUtc { get; init; }

    public long? FileSize { get; init; }
}
