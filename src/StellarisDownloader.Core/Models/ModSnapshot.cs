namespace StellarisDownloader.Core.Models;

public sealed record ModSnapshot
{
    public const int CurrentSchemaVersion = 1;

    public required int SchemaVersion { get; init; }

    public required int AppId { get; init; }

    public required DateTimeOffset CapturedAtUtc { get; init; }

    public required string SourceLibrary { get; init; }

    public required IReadOnlyList<ModSnapshotEntry> Mods { get; init; }

    public IReadOnlyList<string> EmptyWorkshopIds { get; init; } = [];

    public IReadOnlyList<string> LinkedWorkshopIds { get; init; } = [];

    public bool MetadataUnavailable { get; init; }
}

public sealed record ModSnapshotEntry
{
    public required string WorkshopId { get; init; }

    public string? Title { get; init; }

    public DateTimeOffset? InstalledWorkshopUpdatedAtUtc { get; init; }
}

public sealed record ModSnapshotComparison(
    ModSnapshot Baseline,
    ModSnapshot Current,
    IReadOnlyList<ModSnapshotEntry> Added,
    IReadOnlyList<ModSnapshotEntry> Missing,
    IReadOnlyList<ModSnapshotEntry> Retained);
