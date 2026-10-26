using System.Text.Json;
using System.Text.Json.Serialization;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Core.Persistence;

public sealed class WorkshopFavoritesStore(string path, Action<string>? logWarning = null) : IWorkshopFavoritesStore, IDisposable
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private readonly string filePath = Path.GetFullPath(path);
    private readonly SemaphoreSlim gate = new(1, 1);

    public string? Warning { get; private set; }

    public async Task<IReadOnlyList<WorkshopFavorite>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<WorkshopFavorite>> SetFavoriteAsync(
        string workshopId, bool favorite, string? title = null, CancellationToken cancellationToken = default)
    {
        ValidateId(workshopId);
        if (title?.Length > 1024) { throw new ArgumentException("The Workshop title is too long.", nameof(title)); }
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = (await ReadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var existing = items.FindIndex(item => item.WorkshopId == workshopId);
            if (favorite && existing < 0)
            {
                if (items.Count >= 10000) { throw new StellarisOperationException("The favorites list is full."); }
                items.Insert(0, new WorkshopFavorite
                {
                    WorkshopId = workshopId,
                    Title = title?.Trim(),
                    SavedAtUtc = DateTimeOffset.UtcNow,
                });
            }
            else if (!favorite && existing >= 0)
            {
                items.RemoveAt(existing);
            }
            else
            {
                return items;
            }

            var directory = Path.GetDirectoryName(filePath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(directory, $"{Path.GetFileName(filePath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(stream, new FavoritesDocument
                    {
                        SchemaVersion = 1,
                        Favorites = items,
                    }, Options, cancellationToken).ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(filePath))
                {
                    File.Replace(temporaryPath, filePath, filePath + ".bak", ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(temporaryPath, filePath);
                }
            }
            finally { File.Delete(temporaryPath); }
            return items;
        }
        finally { gate.Release(); }
    }

    private async Task<IReadOnlyList<WorkshopFavorite>> ReadAsync(CancellationToken cancellationToken)
    {
        Warning = null;
        try
        {
            return await ParseAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd'T'HHmmssfffffff'Z'", System.Globalization.CultureInfo.InvariantCulture);
            var backupPath = Path.Combine(Path.GetDirectoryName(filePath)!,
                $"{Path.GetFileNameWithoutExtension(filePath)}.corrupt-{timestamp}.json");
            File.Move(filePath, backupPath);
            Warning = $"The favorites file was damaged. A backup was saved to {backupPath}.";
            logWarning?.Invoke(Warning);
            return [];
        }
    }

    private async Task<IReadOnlyList<WorkshopFavorite>> ParseAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath)) { return []; }
        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > 16 * 1024 * 1024) { throw new InvalidDataException("The favorites file is too large."); }
            var document = await JsonSerializer.DeserializeAsync<FavoritesDocument>(stream, Options, cancellationToken)
                .ConfigureAwait(false);
            if (document is null || document.SchemaVersion != 1 || document.Favorites is null || document.Favorites.Count > 10000)
            {
                throw new InvalidDataException("The favorites file has an unsupported format.");
            }
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var validItems = new List<WorkshopFavorite>();
            foreach (var item in document.Favorites)
            {
                if (item is null) { throw new InvalidDataException("The favorites file contains an invalid item."); }
                if (string.IsNullOrEmpty(item.WorkshopId) || item.WorkshopId.Length > 20 || !item.WorkshopId.All(char.IsAsciiDigit))
                {
                    logWarning?.Invoke($"Skipped a favorite with an invalid Workshop ID: {item.WorkshopId}");
                    continue;
                }
                if (!ids.Add(item.WorkshopId) || item.Title?.Length > 1024)
                {
                    throw new InvalidDataException("The favorites file contains duplicate or invalid items.");
                }
                validItems.Add(item);
            }
            return validItems.OrderByDescending(item => item.SavedAtUtc).ToArray();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The favorites file is invalid. Its contents have been preserved.", exception);
        }
    }

    private static void ValidateId(string id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 20 || !id.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("A favorite must have a numeric Workshop ID.", nameof(id));
        }
    }

    public void Dispose() => gate.Dispose();

    private sealed record FavoritesDocument
    {
        public required int SchemaVersion { get; init; }
        public required List<WorkshopFavorite> Favorites { get; init; }
    }
}
