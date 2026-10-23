using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Persistence;

public static class ModSnapshotFile
{
    public const int MaximumFileBytes = 16 * 1024 * 1024;
    private const int MaximumEntries = 100_000;
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        MaxDepth = 16,
    };

    public static async Task<ModSnapshot> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("The mod snapshot exceeds the 16 MiB size limit.");
        }

        try
        {
            var snapshot = await JsonSerializer.DeserializeAsync<ModSnapshot>(
                stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The file does not contain a mod snapshot.");
            Validate(snapshot);
            return snapshot;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The mod snapshot is not valid JSON or is missing required fields.", exception);
        }
    }

    public static Task WriteAsync(string path, ModSnapshot snapshot, CancellationToken cancellationToken = default)
        => WriteSnapshotAsync(path, snapshot, overwrite: true, cancellationToken);

    public static Task WriteNewAsync(string path, ModSnapshot snapshot, CancellationToken cancellationToken = default)
        => WriteSnapshotAsync(path, snapshot, overwrite: false, cancellationToken);

    private static Task WriteSnapshotAsync(string path, ModSnapshot snapshot, bool overwrite, CancellationToken cancellationToken)
    {
        Validate(snapshot);
        var sorted = snapshot with
        {
            Mods = snapshot.Mods.OrderBy(mod => mod.WorkshopId, StringComparer.Ordinal).ToArray(),
            EmptyWorkshopIds = snapshot.EmptyWorkshopIds.Order(StringComparer.Ordinal).ToArray(),
            LinkedWorkshopIds = snapshot.LinkedWorkshopIds.Order(StringComparer.Ordinal).ToArray(),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(sorted, SerializerOptions);
        if (bytes.Length > MaximumFileBytes)
        {
            throw new InvalidDataException("The mod snapshot exceeds the 16 MiB size limit.");
        }

        return WriteAtomicallyAsync(path, bytes, cancellationToken, overwrite);
    }

    public static Task WriteTextAsync(string path, string text, CancellationToken cancellationToken = default) =>
        WriteAtomicallyAsync(path, Encoding.UTF8.GetBytes(text), cancellationToken);

    public static void Validate(ModSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion != ModSnapshot.CurrentSchemaVersion || snapshot.AppId != 281990)
        {
            throw new InvalidDataException("Unsupported mod snapshot version or game. A Stellaris snapshot is required.");
        }

        if (snapshot.CapturedAtUtc == default || string.IsNullOrWhiteSpace(snapshot.SourceLibrary)
            || snapshot.SourceLibrary.Length > 32768 || snapshot.Mods is null
            || snapshot.EmptyWorkshopIds is null || snapshot.LinkedWorkshopIds is null
            || (long)snapshot.Mods.Count + snapshot.EmptyWorkshopIds.Count + snapshot.LinkedWorkshopIds.Count > MaximumEntries)
        {
            throw new InvalidDataException("The mod snapshot has invalid or missing fields.");
        }

        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var mod in snapshot.Mods)
        {
            if (mod is null || mod.Title?.Length > 4096)
            {
                throw new InvalidDataException("The mod snapshot contains an invalid mod entry.");
            }

            ValidateId(mod.WorkshopId, identifiers);
        }

        foreach (var id in snapshot.EmptyWorkshopIds.Concat(snapshot.LinkedWorkshopIds))
        {
            ValidateId(id, identifiers);
        }
    }

    private static void ValidateId(string id, HashSet<string> identifiers)
    {
        if (string.IsNullOrEmpty(id) || id.Length > 255 || !id.All(char.IsAsciiDigit) || !identifiers.Add(id))
        {
            throw new InvalidDataException("The mod snapshot contains an invalid or duplicate Workshop ID.");
        }
    }

    private static async Task WriteAtomicallyAsync(
        string path, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken, bool overwrite = true)
    {
        var destination = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(destination)
            ?? throw new InvalidOperationException("The output directory could not be resolved.");
        var temporaryPath = Path.Combine(directory, $"{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (overwrite && File.Exists(destination))
            {
                File.Replace(temporaryPath, destination, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, destination);
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}
