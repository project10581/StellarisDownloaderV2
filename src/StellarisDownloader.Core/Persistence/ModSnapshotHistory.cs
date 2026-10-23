using System.Globalization;
using System.Security;
using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Persistence;

public sealed class ModSnapshotHistory
{
    public ModSnapshotHistory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = Path.GetFullPath(directoryPath);
    }

    public string DirectoryPath { get; }

    public string ExportsDirectory => Path.Combine(DirectoryPath, "exports");

    public async Task<string> SaveAsync(ModSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ModSnapshotFile.Validate(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(DirectoryPath);
        var timestamp = snapshot.CapturedAtUtc.UtcDateTime.ToString("yyyy-MM-dd-HHmmss-fffffff", CultureInfo.InvariantCulture);
        var path = Path.Combine(DirectoryPath, $"stellaris-mods-{timestamp}-{Guid.NewGuid():N}.json");
        // Never replace a saved version, including captures made within the same clock tick.
        await ModSnapshotFile.WriteNewAsync(path, snapshot, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public Task<ModSnapshotHistoryResult> ListAsync(CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            Directory.CreateDirectory(DirectoryPath);
            var entries = new List<ModSnapshotHistoryEntry>();
            var unreadableCount = 0;
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var snapshot = await ModSnapshotFile.ReadAsync(path, cancellationToken).ConfigureAwait(false);
                    entries.Add(new ModSnapshotHistoryEntry(path, snapshot.CapturedAtUtc, snapshot.SourceLibrary, snapshot.Mods.Count));
                }
                catch (Exception exception) when (
                    exception is IOException or InvalidDataException or UnauthorizedAccessException or SecurityException)
                {
                    unreadableCount++;
                }
            }

            return new ModSnapshotHistoryResult(
                entries.OrderByDescending(entry => entry.CapturedAtUtc)
                    .ThenBy(entry => entry.FilePath, StringComparer.Ordinal).ToArray(), unreadableCount);
        }, cancellationToken);

    public bool IsManagedVersionPath(string path) => string.Equals(
        Path.GetDirectoryName(Path.GetFullPath(path)),
        Path.TrimEndingDirectorySeparator(DirectoryPath), StringComparison.OrdinalIgnoreCase);
}

public sealed record ModSnapshotHistoryEntry(string FilePath, DateTimeOffset CapturedAtUtc, string SourceLibrary, int ModCount);

public sealed record ModSnapshotHistoryResult(IReadOnlyList<ModSnapshotHistoryEntry> Entries, int UnreadableCount);
