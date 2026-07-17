using System.Globalization;
using System.Text.Json;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Core.Persistence;

public sealed class JsonSettingsStore : ISettingsStore, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string settingsPath;
    private readonly TimeProvider timeProvider;
    private readonly SemaphoreSlim saveGate = new(1, 1);

    public JsonSettingsStore(string settingsPath, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);

        this.settingsPath = Path.GetFullPath(settingsPath);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<SettingsLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(settingsPath))
        {
            return NewInitializationResult();
        }

        try
        {
            await using var stream = new FileStream(
                settingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);

            if (settings is null)
            {
                throw new InvalidDataException("The settings document did not contain an object.");
            }

            Validate(settings);
            return new SettingsLoadResult(settings, RequiresInitialization: false, CorruptBackupPath: null);
        }
        catch (Exception exception) when (
            exception is JsonException or InvalidDataException or NotSupportedException)
        {
            var backupPath = MoveCorruptSettingsAside();
            return new SettingsLoadResult(new AppSettings(), RequiresInitialization: true, backupPath);
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);

        await saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var content = JsonSerializer.SerializeToUtf8Bytes(settings, SerializerOptions);
            await WriteAtomicallyAsync(content, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            saveGate.Release();
        }
    }

    public void Dispose()
    {
        saveGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private static SettingsLoadResult NewInitializationResult()
    {
        return new SettingsLoadResult(
            new AppSettings(),
            RequiresInitialization: true,
            CorruptBackupPath: null);
    }

    private static void Validate(AppSettings settings)
    {
        if (settings.SchemaVersion != AppSettings.CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Unsupported settings schema version: {settings.SchemaVersion}.");
        }

        if (settings.Language is not AppSettings.DefaultLanguage and not AppSettings.SimplifiedChineseLanguage)
        {
            throw new InvalidDataException($"Unsupported language: {settings.Language}.");
        }

        if (settings.LibraryRoot is not null && string.IsNullOrWhiteSpace(settings.LibraryRoot))
        {
            throw new InvalidDataException("LibraryRoot cannot contain only whitespace.");
        }
    }

    private async Task WriteAtomicallyAsync(
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(settingsPath)
            ?? throw new InvalidOperationException("The settings directory could not be resolved.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $"{Path.GetFileName(settingsPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(settingsPath))
            {
                File.Replace(temporaryPath, settingsPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, settingsPath);
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private string MoveCorruptSettingsAside()
    {
        var timestamp = timeProvider.GetUtcNow().ToString(
            "yyyyMMdd'T'HHmmssfffffff'Z'",
            CultureInfo.InvariantCulture);
        var basePath = $"{settingsPath}.{timestamp}.corrupt";
        var backupPath = basePath;
        var suffix = 1;

        while (File.Exists(backupPath))
        {
            backupPath = $"{basePath}.{suffix}";
            suffix++;
        }

        File.Move(settingsPath, backupPath);
        return backupPath;
    }
}
