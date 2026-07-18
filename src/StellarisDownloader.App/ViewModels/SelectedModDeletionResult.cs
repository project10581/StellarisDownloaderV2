using StellarisDownloader.Core.Models;

namespace StellarisDownloader.App.ViewModels;

public sealed record SelectedModDeletionResult
{
    public required bool Started { get; init; }

    public DeleteResult? DeleteResult { get; init; }

    public bool QueuedForRedownload { get; init; }

    public string? Error { get; init; }

    public bool CanRetryPermanently => DeleteResult?.CanRetryPermanently ?? false;

    public bool RequiresRescan => DeleteResult?.RequiresRescan ?? false;

    public static SelectedModDeletionResult Rejected(string error) => new()
    {
        Started = false,
        Error = error,
    };
}
