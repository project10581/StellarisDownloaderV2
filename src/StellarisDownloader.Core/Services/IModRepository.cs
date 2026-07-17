using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Services;

public interface IModRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModRecord>> ListAsync(
        string libraryRoot,
        CancellationToken cancellationToken = default);

    Task<ModRecord?> GetAsync(
        string libraryRoot,
        string workshopId,
        CancellationToken cancellationToken = default);

    Task UpsertFinalResultAsync(
        string libraryRoot,
        ModRecord record,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string libraryRoot,
        string workshopId,
        CancellationToken cancellationToken = default);

    Task ReplaceSnapshotAsync(
        string libraryRoot,
        IReadOnlyCollection<ModRecord> records,
        DateTimeOffset rebuiltAtUtc,
        CancellationToken cancellationToken = default);

    Task<CacheStateInfo> GetCacheStateAsync(
        string? expectedLibraryRoot,
        CancellationToken cancellationToken = default);

    Task MarkCacheStaleAsync(CancellationToken cancellationToken = default);
}
