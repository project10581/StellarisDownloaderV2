namespace StellarisDownloader.Core.Models;

public sealed record LibraryScanResult
{
    public required OperationStatus Status { get; init; }

    public required string LibraryRoot { get; init; }

    public IReadOnlyList<ModRecord> Records { get; init; } = [];

    public IReadOnlyList<string> AddedWorkshopIds { get; init; } = [];

    public IReadOnlyList<string> RemovedWorkshopIds { get; init; } = [];

    public IReadOnlyList<string> EmptyWorkshopIds { get; init; } = [];

    public int IgnoredDirectoryCount { get; init; }

    public int ImportedCount => Records.Count;

    public string? Error { get; init; }
}
