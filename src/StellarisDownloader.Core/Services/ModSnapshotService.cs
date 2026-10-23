using System.Security;
using Microsoft.Data.Sqlite;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;

namespace StellarisDownloader.Core.Services;

public sealed class ModSnapshotService(
    ISettingsStore settingsStore,
    IModRepository modRepository,
    WriteOperationCoordinator writeCoordinator,
    TimeProvider? timeProvider = null) : IModSnapshotService
{
    public Task<ModSnapshot> CaptureAsync(CancellationToken cancellationToken = default) =>
        writeCoordinator.ExecuteAsync(
            token => Task.Run(() => CaptureCoreAsync(token), token), cancellationToken);

    private async Task<ModSnapshot> CaptureCoreAsync(CancellationToken cancellationToken)
    {
        var settings = await settingsStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (settings.RequiresInitialization || string.IsNullOrWhiteSpace(settings.Settings.LibraryRoot))
        {
            throw new InvalidOperationException("Select a mod library in Settings before exporting or comparing it.");
        }

        var inventory = LocalModDirectoryScanner.Scan(
            settings.Settings.LibraryRoot, cancellationToken: cancellationToken);
        IReadOnlyList<ModRecord> records = [];
        var metadataUnavailable = false;
        try
        {
            records = await modRepository.ListAsync(inventory.LibraryRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException
                or SqliteException or InvalidOperationException)
        {
            // Disk membership is still usable when the disposable metadata cache cannot be read.
            metadataUnavailable = true;
        }

        var recordsById = records.ToDictionary(record => record.WorkshopId, StringComparer.Ordinal);
        var snapshot = new ModSnapshot
        {
            SchemaVersion = ModSnapshot.CurrentSchemaVersion,
            AppId = 281990,
            CapturedAtUtc = (timeProvider ?? TimeProvider.System).GetUtcNow(),
            SourceLibrary = inventory.LibraryRoot,
            Mods = inventory.Directories.Select(directory =>
            {
                var cached = recordsById.GetValueOrDefault(directory.Name);
                return new ModSnapshotEntry
                {
                    WorkshopId = directory.Name,
                    Title = cached?.Title,
                    InstalledWorkshopUpdatedAtUtc = cached?.InstalledWorkshopUpdatedAtUtc,
                };
            }).ToArray(),
            EmptyWorkshopIds = inventory.EmptyWorkshopIds,
            LinkedWorkshopIds = inventory.LinkedWorkshopIds,
            MetadataUnavailable = metadataUnavailable,
        };
        cancellationToken.ThrowIfCancellationRequested();
        ModSnapshotFile.Validate(snapshot);
        return snapshot;
    }
}
