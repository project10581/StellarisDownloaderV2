using StellarisDownloader.Core.Models;

namespace StellarisDownloader.App.ViewModels;

public sealed record DownloadQueueEnqueueResult(
    IReadOnlyList<string> AddedWorkshopIds,
    int DuplicateCount,
    IReadOnlyList<InvalidWorkshopInput> InvalidInputs)
{
    public int AddedCount => AddedWorkshopIds.Count;
}
