namespace StellarisDownloader.Core.Models;

public sealed record OperationProgress(
    string Stage,
    int Completed,
    int Total,
    string? WorkshopId,
    string Message);
