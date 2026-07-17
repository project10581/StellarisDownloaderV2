namespace StellarisDownloader.Core.Models;

public sealed record CacheStateInfo(
    int SchemaVersion,
    string? LibraryRoot,
    CacheState State,
    DateTimeOffset? LastRebuiltAtUtc);
