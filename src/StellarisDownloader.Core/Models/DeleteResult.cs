namespace StellarisDownloader.Core.Models;

public sealed record DeleteResult
{
    public required string WorkshopId { get; init; }

    public required OperationStatus Status { get; init; }

    public required string ContentPath { get; init; }

    public bool FilesRemoved { get; init; }

    public bool RecordRemoved { get; init; }

    public bool CanRetryPermanently { get; init; }

    public bool RequiresRescan { get; init; }

    public string? Error { get; init; }
}
