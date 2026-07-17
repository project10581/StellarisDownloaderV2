namespace StellarisDownloader.Core.Models;

public sealed record ModRecord
{
    public required string WorkshopId { get; init; }

    public int AppId { get; init; } = 281990;

    public string? Title { get; init; }

    public string? Description { get; init; }

    public string? PreviewUrl { get; init; }

    public string? CreatorId { get; init; }

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public required string ContentPath { get; init; }

    public long? FileSize { get; init; }

    public required DateTimeOffset ImportedOrDownloadedAtUtc { get; init; }

    public DateTimeOffset? InstalledWorkshopUpdatedAtUtc { get; init; }

    public LocalModState LocalState { get; init; } = LocalModState.Available;

    public OperationStatus LastOperationStatus { get; init; } = OperationStatus.Succeeded;

    public string? LastError { get; init; }

    public required DateTimeOffset LastScannedAtUtc { get; init; }
}
