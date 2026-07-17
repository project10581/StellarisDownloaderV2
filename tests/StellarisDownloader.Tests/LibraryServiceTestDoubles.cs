using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

internal sealed class StubSettingsStore : ISettingsStore
{
    public StubSettingsStore(AppSettings settings)
    {
        Settings = settings;
    }

    public AppSettings Settings { get; private set; }

    public bool FailSave { get; set; }

    public int SaveCount { get; private set; }

    public Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SettingsLoadResult(
            Settings,
            RequiresInitialization: Settings.LibraryRoot is null,
            CorruptBackupPath: null));
    }

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SaveCount++;
        if (FailSave)
        {
            throw new IOException("Forced settings save failure.");
        }

        Settings = settings;
        return Task.CompletedTask;
    }
}

internal sealed class MemoryJunctionManager : IJunctionManager
{
    public MemoryJunctionManager(string junctionPath, string? targetPath = null)
    {
        State = targetPath is null
            ? new JunctionState(JunctionStateKind.Missing, junctionPath, TargetPath: null)
            : new JunctionState(JunctionStateKind.Junction, junctionPath, targetPath);
    }

    public JunctionState State { get; private set; }

    public bool FailSet { get; set; }

    public bool FailRestore { get; set; }

    public int RestoreCount { get; private set; }

    public void SetExternalTarget(string targetPath)
    {
        State = new JunctionState(JunctionStateKind.Junction, State.Path, targetPath);
    }

    public Task<JunctionState> InspectAsync(
        string junctionPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(State);
    }

    public Task<JunctionUpdate> SetTargetAsync(
        string junctionPath,
        string targetPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailSet)
        {
            throw new IOException("Forced junction failure.");
        }

        var previous = State;
        var changed = previous.Kind != JunctionStateKind.Junction
            || !string.Equals(previous.TargetPath, targetPath, StringComparison.OrdinalIgnoreCase);
        State = new JunctionState(JunctionStateKind.Junction, junctionPath, targetPath);
        return Task.FromResult(new JunctionUpdate(previous, targetPath, changed));
    }

    public Task RestoreAsync(
        JunctionUpdate update,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestoreCount++;
        if (FailRestore)
        {
            throw new IOException("Forced junction rollback failure.");
        }

        State = update.PreviousState;
        return Task.CompletedTask;
    }
}

internal sealed class FailingReplaceModRepository : IModRepository
{
    private readonly IModRepository inner;

    public FailingReplaceModRepository(IModRepository inner)
    {
        this.inner = inner;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        inner.InitializeAsync(cancellationToken);

    public Task<IReadOnlyList<ModRecord>> ListAsync(
        string libraryRoot,
        CancellationToken cancellationToken = default) =>
        inner.ListAsync(libraryRoot, cancellationToken);

    public Task<ModRecord?> GetAsync(
        string libraryRoot,
        string workshopId,
        CancellationToken cancellationToken = default) =>
        inner.GetAsync(libraryRoot, workshopId, cancellationToken);

    public Task UpsertFinalResultAsync(
        string libraryRoot,
        ModRecord record,
        CancellationToken cancellationToken = default) =>
        inner.UpsertFinalResultAsync(libraryRoot, record, cancellationToken);

    public Task<bool> DeleteAsync(
        string libraryRoot,
        string workshopId,
        CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(libraryRoot, workshopId, cancellationToken);

    public Task ReplaceSnapshotAsync(
        string libraryRoot,
        IReadOnlyCollection<ModRecord> records,
        DateTimeOffset rebuiltAtUtc,
        CancellationToken cancellationToken = default) =>
        throw new IOException("Forced cache rebuild failure.");

    public Task<CacheStateInfo> GetCacheStateAsync(
        string? expectedLibraryRoot,
        CancellationToken cancellationToken = default) =>
        inner.GetCacheStateAsync(expectedLibraryRoot, cancellationToken);

    public Task MarkCacheStaleAsync(CancellationToken cancellationToken = default) =>
        inner.MarkCacheStaleAsync(cancellationToken);
}

internal sealed class FailingDeleteModRepository : IModRepository
{
    private readonly IModRepository inner;

    public FailingDeleteModRepository(IModRepository inner)
    {
        this.inner = inner;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default) =>
        inner.InitializeAsync(cancellationToken);

    public Task<IReadOnlyList<ModRecord>> ListAsync(
        string libraryRoot,
        CancellationToken cancellationToken = default) =>
        inner.ListAsync(libraryRoot, cancellationToken);

    public Task<ModRecord?> GetAsync(
        string libraryRoot,
        string workshopId,
        CancellationToken cancellationToken = default) =>
        inner.GetAsync(libraryRoot, workshopId, cancellationToken);

    public Task UpsertFinalResultAsync(
        string libraryRoot,
        ModRecord record,
        CancellationToken cancellationToken = default) =>
        inner.UpsertFinalResultAsync(libraryRoot, record, cancellationToken);

    public Task<bool> DeleteAsync(
        string libraryRoot,
        string workshopId,
        CancellationToken cancellationToken = default) =>
        throw new IOException("Forced cache delete failure.");

    public Task ReplaceSnapshotAsync(
        string libraryRoot,
        IReadOnlyCollection<ModRecord> records,
        DateTimeOffset rebuiltAtUtc,
        CancellationToken cancellationToken = default) =>
        inner.ReplaceSnapshotAsync(libraryRoot, records, rebuiltAtUtc, cancellationToken);

    public Task<CacheStateInfo> GetCacheStateAsync(
        string? expectedLibraryRoot,
        CancellationToken cancellationToken = default) =>
        inner.GetCacheStateAsync(expectedLibraryRoot, cancellationToken);

    public Task MarkCacheStaleAsync(CancellationToken cancellationToken = default) =>
        inner.MarkCacheStaleAsync(cancellationToken);
}
