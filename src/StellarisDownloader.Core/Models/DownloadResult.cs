namespace StellarisDownloader.Core.Models;

public sealed record DownloadResult
{
    public required string WorkshopId { get; init; }

    public required OperationStatus Status { get; init; }

    public required string ContentPath { get; init; }

    public bool FolderExists { get; init; }

    public bool FolderNonEmpty { get; init; }

    public int? ExitCode { get; init; }

    public bool TimedOut { get; init; }

    public IReadOnlyList<string> OutputLines { get; init; } = [];

    public string? Error { get; init; }
}
