using System.Text.Json;
using StellarisDownloader.Core.Integrations;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class ModSnapshotTests
{
    private static readonly JsonSerializerOptions TestSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task CaptureUsesDiskMembershipWithOptionalCachedNamesAndDoesNotRewriteTheCache()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.GetPath("library");
        CreateMod(root, "100");
        CreateMod(root, "200");
        CreateMod(root, "notes");
        Directory.CreateDirectory(Path.Combine(root, "400"));
        File.WriteAllText(Path.Combine(root, "500"), "not a directory");
        using var repository = new SqliteModRepository(temporary.GetPath("cache.db"));
        await repository.InitializeAsync();
        await repository.ReplaceSnapshotAsync(root, [Record(root, "100"), Record(root, "300")], DateTimeOffset.UtcNow);
        using var coordinator = new WriteOperationCoordinator();
        var settings = new StubSettingsStore(new AppSettings { LibraryRoot = root });
        var service = new ModSnapshotService(settings, repository, coordinator);

        var snapshot = await service.CaptureAsync();

        Assert.Equal(["100", "200"], snapshot.Mods.Select(mod => mod.WorkshopId));
        Assert.Equal("中文模组 100", snapshot.Mods[0].Title);
        Assert.Null(snapshot.Mods[1].Title);
        Assert.Equal(["400"], snapshot.EmptyWorkshopIds);
        Assert.False(snapshot.MetadataUnavailable);
        Assert.Equal(root, snapshot.SourceLibrary);
        Assert.Equal(["100", "300"], (await repository.ListAsync(root)).Select(mod => mod.WorkshopId));
        Assert.Equal(0, settings.SaveCount);
    }

    [Fact]
    public async Task UnavailableCacheDoesNotPreventExportingDiskIds()
    {
        using var temporary = new TemporaryDirectory();
        CreateMod(temporary.Path, "100");
        using var repository = new SqliteModRepository(temporary.GetPath("not-initialized.db"));
        using var coordinator = new WriteOperationCoordinator();
        var service = new ModSnapshotService(
            new StubSettingsStore(new AppSettings { LibraryRoot = temporary.Path }), repository, coordinator);

        var snapshot = await service.CaptureAsync();

        Assert.Equal("100", Assert.Single(snapshot.Mods).WorkshopId);
        Assert.True(snapshot.MetadataUnavailable);
        Assert.False(File.Exists(temporary.GetPath("not-initialized.db")));
    }

    [Fact]
    public async Task MissingRootFailsWithoutCreatingItWhileAnExistingEmptyLibraryIsValid()
    {
        using var temporary = new TemporaryDirectory();
        var missing = temporary.GetPath("missing");
        using var repository = new SqliteModRepository(temporary.GetPath("cache.db"));
        using var coordinator = new WriteOperationCoordinator();
        var settings = new StubSettingsStore(new AppSettings { LibraryRoot = missing });
        var service = new ModSnapshotService(settings, repository, coordinator);

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => service.CaptureAsync());
        Assert.False(Directory.Exists(missing));
        Directory.CreateDirectory(missing);
        Assert.Empty((await service.CaptureAsync()).Mods);
        Assert.False(coordinator.IsBusy);
    }

    [Fact]
    public async Task CaptureWaitsForTheWriteGateAndReadsSettingsAfterAcquiringIt()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.GetPath("library");
        using var repository = new SqliteModRepository(temporary.GetPath("cache.db"));
        using var coordinator = new WriteOperationCoordinator();
        var settings = new StubSettingsStore(new AppSettings());
        var service = new ModSnapshotService(settings, repository, coordinator);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = coordinator.ExecuteAsync(_ => release.Task);
        try
        {
            var capture = service.CaptureAsync();
            Assert.False(capture.IsCompleted);
            CreateMod(root, "200");
            await settings.SaveAsync(new AppSettings { LibraryRoot = root });
            release.SetResult(true);
            await writer;

            Assert.Equal("200", Assert.Single((await capture).Mods).WorkshopId);
        }
        finally
        {
            release.TrySetResult(true);
            await writer;
        }
    }

    [Fact]
    public async Task CaptureCanBeCancelledWhileWaitingForTheWriteGate()
    {
        using var temporary = new TemporaryDirectory();
        using var repository = new SqliteModRepository(temporary.GetPath("cache.db"));
        using var coordinator = new WriteOperationCoordinator();
        using var cancellation = new CancellationTokenSource();
        var service = new ModSnapshotService(new StubSettingsStore(new AppSettings()), repository, coordinator);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = coordinator.ExecuteAsync(_ => release.Task);
        try
        {
            var capture = service.CaptureAsync(cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => capture);
        }
        finally
        {
            release.TrySetResult(true);
            await writer;
        }
    }

    [Fact]
    public async Task SnapshotAndLibraryScanBothExcludeDirectoryJunctionsWithoutFollowingThem()
    {
        using var temporary = new TemporaryDirectory();
        var root = temporary.GetPath("library");
        CreateMod(root, "100");
        var target = temporary.GetPath("external");
        CreateMod(target, "200");
        var junctions = new WindowsJunctionManager();
        var update = await junctions.SetTargetAsync(Path.Combine(root, "300"), target);
        try
        {
            using var repository = new SqliteModRepository(temporary.GetPath("cache.db"));
            await repository.InitializeAsync();
            using var coordinator = new WriteOperationCoordinator();
            var settings = new StubSettingsStore(new AppSettings { LibraryRoot = root });
            var snapshot = await new ModSnapshotService(settings, repository, coordinator).CaptureAsync();
            var service = new LibraryService(settings, repository, new StubWorkshopClient(),
                junctions, coordinator, temporary.GetPath("steamcmd-junction"));
            var scan = await service.ScanAsync(root);

            Assert.Equal(["300"], snapshot.LinkedWorkshopIds);
            Assert.Equal("100", Assert.Single(snapshot.Mods).WorkshopId);
            Assert.Equal("100", Assert.Single(scan.Records).WorkshopId);
            Assert.Equal(1, scan.IgnoredDirectoryCount);
            Assert.True(File.Exists(Path.Combine(target, "200", "descriptor.mod")));
        }
        finally
        {
            await junctions.RestoreAsync(update, CancellationToken.None);
        }
    }

    [Fact]
    public async Task JsonRoundTripPreservesUnicodeNamesAndTimestampsWithStableOrdering()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath("snapshot.json");
        var snapshot = Snapshot("200", "100") with
        {
            Mods = [new ModSnapshotEntry { WorkshopId = "200", Title = "中文, \"name\"\n第二行", InstalledWorkshopUpdatedAtUtc = DateTimeOffset.UnixEpoch },
                new ModSnapshotEntry { WorkshopId = "100" }],
            EmptyWorkshopIds = ["400"],
        };
        await ModSnapshotFile.WriteAsync(path, snapshot);
        var restored = await ModSnapshotFile.ReadAsync(path);

        Assert.Equal(["100", "200"], restored.Mods.Select(mod => mod.WorkshopId));
        Assert.Equal(snapshot.Mods[0], restored.Mods[1]);
        Assert.Equal(snapshot.CapturedAtUtc, restored.CapturedAtUtc);
        Assert.Equal(snapshot.SourceLibrary, restored.SourceLibrary);
        Assert.Equal(["400"], restored.EmptyWorkshopIds);
        Assert.Contains("中文", await File.ReadAllTextAsync(path), StringComparison.Ordinal);
        var firstContent = await File.ReadAllBytesAsync(path);
        await ModSnapshotFile.WriteAsync(path, restored);
        Assert.Equal(firstContent, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public void ComparisonUsesIdsAndKeepsOldNamesForMissingMods()
    {
        var baseline = Snapshot("100", "200", "400");
        var current = Snapshot("300", "100", "400") with
        {
            Mods = [new ModSnapshotEntry { WorkshopId = "300", Title = "New" },
                new ModSnapshotEntry { WorkshopId = "100", Title = "Renamed" },
                new ModSnapshotEntry { WorkshopId = "400" }],
        };

        var result = ModSnapshotComparer.Compare(baseline, current);

        Assert.Equal("300", Assert.Single(result.Added).WorkshopId);
        Assert.Equal(baseline.Mods[1], Assert.Single(result.Missing));
        Assert.Equal(["100", "400"], result.Retained.Select(mod => mod.WorkshopId));
        Assert.Equal("Renamed", result.Retained[0].Title);
        Assert.Equal(baseline.Mods[2].Title, result.Retained[1].Title);
    }

    [Theory]
    [InlineData("", "", 0, 0, 0)]
    [InlineData("100", "", 0, 1, 0)]
    [InlineData("", "100", 1, 0, 0)]
    [InlineData("200,100", "100,200", 0, 0, 2)]
    public void ComparisonHandlesEmptyAndReorderedLists(string before, string after, int added, int missing, int retained)
    {
        var result = ModSnapshotComparer.Compare(
            Snapshot(before.Split(',', StringSplitOptions.RemoveEmptyEntries)),
            Snapshot(after.Split(',', StringSplitOptions.RemoveEmptyEntries)));

        Assert.Equal(added, result.Added.Count);
        Assert.Equal(missing, result.Missing.Count);
        Assert.Equal(retained, result.Retained.Count);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("invalid-id")]
    [InlineData("unicode-id")]
    [InlineData("null-id")]
    [InlineData("null-entry")]
    [InlineData("null-list")]
    [InlineData("version")]
    [InlineData("game")]
    [InlineData("source")]
    [InlineData("time")]
    [InlineData("overlap")]
    public async Task InvalidSnapshotsAreRejectedBeforeComparison(string invalidCase)
    {
        using var temporary = new TemporaryDirectory();
        var valid = Snapshot("100");
        var invalid = invalidCase switch
        {
            "duplicate" => Snapshot("100", "100"),
            "invalid-id" => Snapshot("../outside"),
            "unicode-id" => Snapshot("１２３"),
            "null-id" => valid with { Mods = [new ModSnapshotEntry { WorkshopId = null! }] },
            "null-entry" => valid with { Mods = [null!] },
            "null-list" => valid with { Mods = null! },
            "version" => valid with { SchemaVersion = 99 },
            "game" => valid with { AppId = 1 },
            "source" => valid with { SourceLibrary = " " },
            "time" => valid with { CapturedAtUtc = default },
            "overlap" => valid with { EmptyWorkshopIds = ["100"] },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase)),
        };
        var path = temporary.GetPath("invalid.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(invalid, TestSerializerOptions));

        await Assert.ThrowsAsync<InvalidDataException>(() => ModSnapshotFile.ReadAsync(path));
        Assert.Throws<InvalidDataException>(() => ModSnapshotComparer.Compare(valid, invalid));
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("{\"mods\":[]}")]
    public async Task MalformedOrIncompleteDocumentsAreRejected(string json)
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath("invalid.json");
        await File.WriteAllTextAsync(path, json);

        await Assert.ThrowsAsync<InvalidDataException>(() => ModSnapshotFile.ReadAsync(path));
    }

    [Fact]
    public async Task OversizedDocumentsAreRejectedBeforeParsing()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath("large.json");
        using (var stream = File.Create(path))
        {
            stream.SetLength(ModSnapshotFile.MaximumFileBytes + 1L);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => ModSnapshotFile.ReadAsync(path));
    }

    [Fact]
    public async Task CancellationOrFailedReplacementPreservesThePreviousSnapshotAndCleansTemporaryFiles()
    {
        using var temporary = new TemporaryDirectory();
        var path = temporary.GetPath("snapshot.json");
        await ModSnapshotFile.WriteAsync(path, Snapshot("100"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ModSnapshotFile.WriteAsync(path, Snapshot("200"), cancelled.Token));
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => ModSnapshotFile.WriteAsync(path, Snapshot("300")));
        }

        Assert.Equal("100", Assert.Single((await ModSnapshotFile.ReadAsync(path)).Mods).WorkshopId);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, "*.tmp"));
    }

    internal static ModSnapshot Snapshot(params string[] ids) => new()
    {
        SchemaVersion = ModSnapshot.CurrentSchemaVersion,
        AppId = 281990,
        CapturedAtUtc = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero),
        SourceLibrary = "C:\\Mods",
        Mods = ids.Select(id => new ModSnapshotEntry { WorkshopId = id, Title = "名称 " + id }).ToArray(),
    };

    private static void CreateMod(string root, string id)
    {
        var path = Path.Combine(root, id);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "descriptor.mod"), "content");
    }

    private static ModRecord Record(string root, string id) => new()
    {
        WorkshopId = id,
        Title = "中文模组 " + id,
        ContentPath = Path.Combine(root, id),
        ImportedOrDownloadedAtUtc = DateTimeOffset.UnixEpoch,
        LastScannedAtUtc = DateTimeOffset.UnixEpoch,
    };
}
