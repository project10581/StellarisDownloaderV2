namespace StellarisDownloader.Core.Models;

public sealed record DownloadRequest
{
    public required string WorkshopId { get; init; }

    public required string LibraryRoot { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(60);
}
