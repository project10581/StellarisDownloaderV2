namespace StellarisDownloader.Core.Models;

public sealed record OperationProgress(
    string Stage,
    int Completed,
    int Total,
    string? WorkshopId,
    string Message)
{
    // Report verified download results separately from informational SteamCMD/metadata progress.
    public DownloadResult? ItemResult { get; init; }
}
