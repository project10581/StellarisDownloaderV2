using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class SteamCmdServiceTests
{
    [Fact]
    public async Task EnsureInstalledDownloadsExtractsAndLaunchVerifiesSteamCmd()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = CreateSteamCmdArchive();
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            StubHttpMessageHandler.Response(
                HttpStatusCode.OK,
                CreateBinaryContent(archive))));
        using var httpClient = new HttpClient(handler);
        var runner = new StubProcessRunner(new ProcessRunResult { ExitCode = 0 });
        var steamCmdDirectory = temporaryDirectory.GetPath("steamcmd");
        using var service = new SteamCmdService(httpClient, runner, steamCmdDirectory);

        var result = await service.EnsureInstalledAsync();

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.True(result.InstalledNow);
        Assert.True(File.Exists(Path.Combine(steamCmdDirectory, "steamcmd.exe")));
        Assert.Equal(1, runner.CallCount);
        Assert.Equal(["+quit"], runner.LastArguments);
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(steamCmdDirectory),
            path => Path.GetFileName(path).StartsWith(".install-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnsureInstalledRetriesVerificationAfterSteamCmdSelfUpdate()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = CreateSteamCmdArchive();
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            StubHttpMessageHandler.Response(
                HttpStatusCode.OK,
                CreateBinaryContent(archive))));
        using var httpClient = new HttpClient(handler);
        ProcessRunResult[] verificationResults =
        [
            new() { ExitCode = 7 },
            new() { ExitCode = 0 },
        ];
        var resultIndex = 0;
        var runner = new StubProcessRunner((_, _) => verificationResults[resultIndex++]);
        var steamCmdDirectory = temporaryDirectory.GetPath("steamcmd");
        using var service = new SteamCmdService(httpClient, runner, steamCmdDirectory);

        var result = await service.EnsureInstalledAsync();

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.True(result.InstalledNow);
        Assert.True(File.Exists(Path.Combine(steamCmdDirectory, "steamcmd.exe")));
        Assert.Equal(2, runner.CallCount);
    }

    [Fact]
    public async Task RepeatedSelfUpdateRestartExitStopsAfterBoundedVerificationAttempts()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = CreateSteamCmdArchive();
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            StubHttpMessageHandler.Response(
                HttpStatusCode.OK,
                CreateBinaryContent(archive))));
        using var httpClient = new HttpClient(handler);
        var runner = new StubProcessRunner(new ProcessRunResult { ExitCode = 7 });
        var steamCmdDirectory = temporaryDirectory.GetPath("steamcmd");
        using var service = new SteamCmdService(httpClient, runner, steamCmdDirectory);

        var result = await service.EnsureInstalledAsync();

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.False(result.InstalledNow);
        Assert.Contains("3 verification attempts", result.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(steamCmdDirectory, "steamcmd.exe")));
        Assert.Equal(3, runner.CallCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task VerificationFailsWhenExecutableDisappears(int exitCode)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = CreateSteamCmdArchive();
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            StubHttpMessageHandler.Response(
                HttpStatusCode.OK,
                CreateBinaryContent(archive))));
        using var httpClient = new HttpClient(handler);
        var steamCmdDirectory = temporaryDirectory.GetPath("steamcmd");
        var executablePath = Path.Combine(steamCmdDirectory, "steamcmd.exe");
        var runner = new StubProcessRunner((_, _) =>
        {
            File.Delete(executablePath);
            return new ProcessRunResult { ExitCode = exitCode };
        });
        using var service = new SteamCmdService(httpClient, runner, steamCmdDirectory);

        var result = await service.EnsureInstalledAsync();

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains("missing", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(executablePath));
        Assert.Equal(1, runner.CallCount);
    }

    [Theory]
    [InlineData(true, false, OperationStatus.Failed)]
    [InlineData(false, true, OperationStatus.Cancelled)]
    public async Task VerificationTimeoutAndCancellationAfterSelfUpdateFailWithoutFurtherRetrying(
        bool timedOut,
        bool cancelled,
        OperationStatus expectedStatus)
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = CreateSteamCmdArchive();
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            StubHttpMessageHandler.Response(
                HttpStatusCode.OK,
                CreateBinaryContent(archive))));
        using var httpClient = new HttpClient(handler);
        ProcessRunResult[] verificationResults =
        [
            new() { ExitCode = 7 },
            new()
            {
                ExitCode = 7,
                TimedOut = timedOut,
                Cancelled = cancelled,
            },
        ];
        var resultIndex = 0;
        var runner = new StubProcessRunner((_, _) => verificationResults[resultIndex++]);
        var steamCmdDirectory = temporaryDirectory.GetPath("steamcmd");
        using var service = new SteamCmdService(httpClient, runner, steamCmdDirectory);

        var result = await service.EnsureInstalledAsync();

        Assert.Equal(expectedStatus, result.Status);
        Assert.False(File.Exists(Path.Combine(steamCmdDirectory, "steamcmd.exe")));
        Assert.Equal(2, runner.CallCount);
    }

    [Fact]
    public async Task VerificationLaunchFailureReturnsRetryableInstallationFailure()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = CreateSteamCmdArchive();
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            StubHttpMessageHandler.Response(
                HttpStatusCode.OK,
                CreateBinaryContent(archive))));
        using var httpClient = new HttpClient(handler);
        var runner = new StubProcessRunner((_, _) =>
            throw new System.ComponentModel.Win32Exception("The executable could not start."));
        var steamCmdDirectory = temporaryDirectory.GetPath("steamcmd");
        using var service = new SteamCmdService(httpClient, runner, steamCmdDirectory);

        var result = await service.EnsureInstalledAsync();

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.False(result.InstalledNow);
        Assert.Contains("could not start", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(steamCmdDirectory, "steamcmd.exe")));
        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public async Task InvalidInstallerArchiveFailsWithoutLeavingAnExecutable()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            StubHttpMessageHandler.Response(
                HttpStatusCode.OK,
                CreateBinaryContent("not a zip"u8.ToArray()))));
        using var httpClient = new HttpClient(handler);
        var runner = new StubProcessRunner(new ProcessRunResult { ExitCode = 0 });
        var steamCmdDirectory = temporaryDirectory.GetPath("steamcmd");
        using var service = new SteamCmdService(httpClient, runner, steamCmdDirectory);

        var result = await service.EnsureInstalledAsync();

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.False(File.Exists(Path.Combine(steamCmdDirectory, "steamcmd.exe")));
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task LaunchVerificationFailureRemovesExecutableSoInstallationCanRetry()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        var archive = CreateSteamCmdArchive();
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            StubHttpMessageHandler.Response(HttpStatusCode.OK, CreateBinaryContent(archive))));
        using var httpClient = new HttpClient(handler);
        var runner = new StubProcessRunner(new ProcessRunResult { ExitCode = 1 });
        var steamCmdDirectory = temporaryDirectory.GetPath("steamcmd");
        using var service = new SteamCmdService(httpClient, runner, steamCmdDirectory);

        var result = await service.EnsureInstalledAsync();

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.False(File.Exists(Path.Combine(steamCmdDirectory, "steamcmd.exe")));
        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public async Task MissingExecutableAndInstallerHttpFailureReturnDownloadFailure()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        using var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            StubHttpMessageHandler.Response(HttpStatusCode.ServiceUnavailable)));
        using var httpClient = new HttpClient(handler);
        var runner = new StubProcessRunner(new ProcessRunResult { ExitCode = 0 });
        using var service = new SteamCmdService(
            httpClient,
            runner,
            temporaryDirectory.GetPath("steamcmd"));

        var result = await service.DownloadAsync(new DownloadRequest
        {
            WorkshopId = "123",
            LibraryRoot = temporaryDirectory.GetPath("library"),
        });

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains("503", result.Error, StringComparison.Ordinal);
        Assert.Equal(0, runner.CallCount);
    }

    [Fact]
    public async Task SuccessRequiresFinalSuccessTerminalAndNonEmptyFolderRegardlessOfExitCode()
    {
        using var fixture = await SteamCmdServiceFixture.CreateAsync(new ProcessRunResult
        {
            ExitCode = 9,
            Output =
            [
                new(1, "stderr", "ERROR! Download item 123 failed (Timeout)."),
                new(2, "stdout", "Success. Downloaded item 123 to a folder."),
            ],
        });
        await fixture.CreateModFolderAsync("123", nonEmpty: true);

        var result = await fixture.Service.DownloadAsync(fixture.Request("123"));

        Assert.Equal(OperationStatus.Succeeded, result.Status);
        Assert.Equal(9, result.ExitCode);
        Assert.True(result.FolderExists);
        Assert.True(result.FolderNonEmpty);
        Assert.Equal(
            ["+login", "anonymous", "+workshop_download_item", "281990", "123", "+quit"],
            fixture.Runner.LastArguments);
    }

    [Theory]
    [InlineData(false, false, "does not exist")]
    [InlineData(true, false, "empty")]
    public async Task SuccessOutputWithoutAValidFolderFails(
        bool createFolder,
        bool nonEmpty,
        string expectedError)
    {
        using var fixture = await SteamCmdServiceFixture.CreateAsync(new ProcessRunResult
        {
            ExitCode = 0,
            Output = [new(1, "stdout", "Success. Downloaded item 123 to a folder.")],
        });
        if (createFolder)
        {
            await fixture.CreateModFolderAsync("123", nonEmpty);
        }

        var result = await fixture.Service.DownloadAsync(fixture.Request("123"));

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains(expectedError, result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FinalFailureAfterSuccessFailsAndPreservesExistingFolder()
    {
        using var fixture = await SteamCmdServiceFixture.CreateAsync(new ProcessRunResult
        {
            ExitCode = 0,
            Output =
            [
                new(1, "stdout", "Success. Downloaded item 123 to a folder."),
                new(2, "stderr", "ERROR! Download item 123 failed (I/O Operation Failed)."),
            ],
        });
        await fixture.CreateModFolderAsync("123", nonEmpty: true);
        var sentinel = Path.Combine(fixture.LibraryRoot, "123", "existing.txt");

        var result = await fixture.Service.DownloadAsync(fixture.Request("123"));

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.True(File.Exists(sentinel));
    }

    [Fact]
    public async Task UnclassifiedOutputFailsWithoutDeletingExistingFolder()
    {
        using var fixture = await SteamCmdServiceFixture.CreateAsync(new ProcessRunResult
        {
            ExitCode = 0,
            Output = [new(1, "stdout", "SteamCMD finished without a clear result.")],
        });
        await fixture.CreateModFolderAsync("123", nonEmpty: true);

        var result = await fixture.Service.DownloadAsync(fixture.Request("123"));

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Contains("recognized terminal", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(fixture.LibraryRoot, "123", "existing.txt")));
    }

    [Theory]
    [InlineData(true, false, OperationStatus.Failed)]
    [InlineData(false, true, OperationStatus.Cancelled)]
    public async Task TimeoutAndCancellationReturnDistinctStatuses(
        bool timedOut,
        bool cancelled,
        OperationStatus expectedStatus)
    {
        using var fixture = await SteamCmdServiceFixture.CreateAsync(new ProcessRunResult
        {
            ExitCode = -1,
            TimedOut = timedOut,
            Cancelled = cancelled,
        });

        var result = await fixture.Service.DownloadAsync(fixture.Request("123"));

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(timedOut, result.TimedOut);
    }

    [Fact]
    public async Task InvalidWorkshopIdDoesNotStartSteamCmd()
    {
        using var fixture = await SteamCmdServiceFixture.CreateAsync(new ProcessRunResult { ExitCode = 0 });

        var result = await fixture.Service.DownloadAsync(fixture.Request("not-numeric"));

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal(0, fixture.Runner.CallCount);
    }

    [Theory]
    [InlineData(3, 1)]
    [InlineData(60, 3)]
    public async Task BatchUsesOrderedSequentialChunks(int count, int expectedProcesses)
    {
        using var fixture = await SteamCmdServiceFixture.CreateAsync((_, arguments) => new ProcessRunResult
        {
            ExitCode = 0,
            Output = RequestedIds(arguments).Select((id, index) =>
                new ProcessOutputLine(index, "stdout", $"Success. Downloaded item {id}.")).ToArray(),
        });
        var ids = Enumerable.Range(100, count).Select(id => id.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        foreach (var id in ids) { await fixture.CreateModFolderAsync(id, nonEmpty: true); }

        var results = await fixture.Service.DownloadBatchAsync(ids.Select(fixture.Request).ToArray());

        Assert.Equal(expectedProcesses, fixture.Runner.CallCount);
        Assert.Equal(ids, fixture.Runner.Calls.SelectMany(RequestedIds));
        Assert.Equal(ids.Chunk(25).Select(chunk => chunk.Length), fixture.Runner.Calls.Select(call => RequestedIds(call).Length));
        Assert.All(results, result => Assert.Equal(OperationStatus.Succeeded, result.Status));
        Assert.All(fixture.Runner.Calls, arguments =>
        {
            Assert.Equal(["+login", "anonymous"], arguments.Take(2));
            Assert.Equal("+quit", arguments[^1]);
            for (var index = 2; index < arguments.Count - 1; index += 3)
            {
                Assert.Equal("+workshop_download_item", arguments[index]);
                Assert.Equal("281990", arguments[index + 1]);
            }
        });
        Assert.True(fixture.Runner.LastIdleTimeout);
        Assert.Equal(TimeSpan.FromMinutes(10), fixture.Runner.LastTimeout);
    }

    [Fact]
    public async Task MixedBatchReportsTerminalLinesImmediatelyAndRetriesOnlyUnknown()
    {
        using var fixture = await SteamCmdServiceFixture.CreateAsync((_, arguments) => new ProcessRunResult
        {
            Output = RequestedIds(arguments).Length == 3
                ? [new(1, "stdout", "Success. Downloaded item 100."), new(2, "stderr", "Download item 200 failed.")]
                : [new(1, "stdout", "Success. Downloaded item 300.")],
        });
        await fixture.CreateModFolderAsync("100", nonEmpty: true);
        await fixture.CreateModFolderAsync("300", nonEmpty: true);
        var progress = new ItemProgress();
        var received = 0;
        fixture.Runner.AfterOutput = _ => Assert.Equal(++received, progress.Results.Count);

        var results = await fixture.Service.DownloadBatchAsync(
            [fixture.Request("100"), fixture.Request("200"), fixture.Request("300")], progress);

        Assert.Equal([OperationStatus.Succeeded, OperationStatus.Failed, OperationStatus.Succeeded], results.Select(result => result.Status));
        Assert.Equal(2, fixture.Runner.CallCount);
        Assert.Equal(["300"], RequestedIds(fixture.Runner.Calls[1]));
        Assert.Equal(["100", "200", "300"], progress.Results.Select(result => result.WorkshopId));
    }

    [Fact]
    public async Task UnknownRetriesOnceBeforeNextChunkAndKeepsExistingErrorIfStillUnknown()
    {
        using var fixture = await SteamCmdServiceFixture.CreateAsync((_, arguments) => new ProcessRunResult
        {
            Output = RequestedIds(arguments).Where(id => id != "100").Select((id, index) =>
                new ProcessOutputLine(index, "stderr", $"Download item {id} failed.")).ToArray(),
        });
        var requests = Enumerable.Range(100, 26).Select(id => fixture.Request(id.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();

        var results = await fixture.Service.DownloadBatchAsync(requests);

        Assert.Equal([25, 1, 1], fixture.Runner.Calls.Select(call => RequestedIds(call).Length));
        Assert.Equal(["100"], RequestedIds(fixture.Runner.Calls[1]));
        Assert.Equal(["125"], RequestedIds(fixture.Runner.Calls[2]));
        Assert.Equal(OperationStatus.Failed, results[0].Status);
        Assert.Equal("SteamCMD output did not contain a recognized terminal result for this Workshop ID.", results[0].Error);
    }

    [Fact]
    public async Task BatchSuccessWithEmptyDirectoryFailsImmediatelyWithoutRetry()
    {
        using var fixture = await SteamCmdServiceFixture.CreateAsync(new ProcessRunResult
        {
            Output = [new(1, "stdout", "Success. Downloaded item 100.")],
        });
        await fixture.CreateModFolderAsync("100", nonEmpty: false);
        var progress = new ItemProgress();

        var result = Assert.Single(await fixture.Service.DownloadBatchAsync([fixture.Request("100")], progress));

        Assert.Equal(OperationStatus.Failed, result.Status);
        Assert.Equal("SteamCMD reported success, but the Workshop directory is empty.", result.Error);
        Assert.Equal(OperationStatus.Failed, Assert.Single(progress.Results).Status);
        Assert.Equal(1, fixture.Runner.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedBatchPreservesTerminalResultsAndDoesNotRetryRemainder(bool timedOut)
    {
        using var fixture = await SteamCmdServiceFixture.CreateAsync(new ProcessRunResult
        {
            Cancelled = !timedOut,
            TimedOut = timedOut,
            Output = [new(1, "stdout", "Success. Downloaded item 100."), new(2, "stderr", "Download item 200 failed.")],
        });
        await fixture.CreateModFolderAsync("100", nonEmpty: true);
        var progress = new ItemProgress();

        var results = await fixture.Service.DownloadBatchAsync(
            [fixture.Request("100"), fixture.Request("200"), fixture.Request("300")], progress);

        Assert.Equal(OperationStatus.Succeeded, results[0].Status);
        Assert.Equal(OperationStatus.Failed, results[1].Status);
        Assert.False(results[0].TimedOut);
        Assert.False(results[1].TimedOut);
        Assert.Equal(timedOut ? OperationStatus.Failed : OperationStatus.Cancelled, results[2].Status);
        Assert.Equal(timedOut, results[2].TimedOut);
        Assert.Equal(1, fixture.Runner.CallCount);
        Assert.Equal(3, progress.Results.Count);
    }

    [Fact]
    public async Task CancellationDuringOutputKeepsCompletedItemAndDoesNotStartNextChunk()
    {
        using var fixture = await SteamCmdServiceFixture.CreateAsync(new ProcessRunResult
        {
            Output = [new(1, "stdout", "Success. Downloaded item 100.")],
        });
        using var cancellation = new CancellationTokenSource();
        await fixture.CreateModFolderAsync("100", nonEmpty: true);
        fixture.Runner.AfterOutput = _ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };
        var requests = Enumerable.Range(100, 26).Select(id => fixture.Request(id.ToString(System.Globalization.CultureInfo.InvariantCulture))).ToArray();

        var results = await fixture.Service.DownloadBatchAsync(requests, cancellationToken: cancellation.Token);

        Assert.Equal(OperationStatus.Succeeded, results[0].Status);
        Assert.All(results.Skip(1), result => Assert.Equal(OperationStatus.Cancelled, result.Status));
        Assert.Equal(1, fixture.Runner.CallCount);
    }

    private static string[] RequestedIds(IReadOnlyList<string> arguments) =>
        Enumerable.Range(0, arguments.Count).Where(index => arguments[index] == "+workshop_download_item")
            .Select(index => arguments[index + 2]).ToArray();

    private sealed class ItemProgress : IProgress<OperationProgress>
    {
        public List<DownloadResult> Results { get; } = [];
        public void Report(OperationProgress value)
        {
            if (value.ItemResult is { } result) { Results.Add(result); }
        }
    }

    private static byte[] CreateSteamCmdArchive()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("steamcmd.exe");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("test executable");
        }

        return stream.ToArray();
    }

    private static ByteArrayContent CreateBinaryContent(byte[] content)
    {
        var result = new ByteArrayContent(content);
        result.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return result;
    }

    private sealed class SteamCmdServiceFixture : IDisposable
    {
        private readonly TemporaryDirectory temporaryDirectory;
        private readonly HttpClient httpClient;

        private SteamCmdServiceFixture(
            TemporaryDirectory temporaryDirectory,
            HttpClient httpClient,
            StubProcessRunner runner,
            SteamCmdService service,
            string libraryRoot)
        {
            this.temporaryDirectory = temporaryDirectory;
            this.httpClient = httpClient;
            Runner = runner;
            Service = service;
            LibraryRoot = libraryRoot;
        }

        public StubProcessRunner Runner { get; }

        public SteamCmdService Service { get; }

        public string LibraryRoot { get; }

        public static Task<SteamCmdServiceFixture> CreateAsync(ProcessRunResult processResult) =>
            CreateAsync((_, _) => processResult);

        public static async Task<SteamCmdServiceFixture> CreateAsync(
            Func<string, IReadOnlyList<string>, ProcessRunResult> resultFactory)
        {
            var temporaryDirectory = new TemporaryDirectory();
            var steamCmdDirectory = temporaryDirectory.GetPath("steamcmd");
            Directory.CreateDirectory(steamCmdDirectory);
            await File.WriteAllTextAsync(Path.Combine(steamCmdDirectory, "steamcmd.exe"), "placeholder");
            var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
                StubHttpMessageHandler.Response(HttpStatusCode.InternalServerError)));
            var httpClient = new HttpClient(handler);
            var runner = new StubProcessRunner(resultFactory);
            var service = new SteamCmdService(httpClient, runner, steamCmdDirectory);
            var libraryRoot = temporaryDirectory.GetPath("library");
            Directory.CreateDirectory(libraryRoot);
            return new SteamCmdServiceFixture(
                temporaryDirectory,
                httpClient,
                runner,
                service,
                libraryRoot);
        }

        public DownloadRequest Request(string workshopId) => new()
        {
            WorkshopId = workshopId,
            LibraryRoot = LibraryRoot,
        };

        public async Task CreateModFolderAsync(string workshopId, bool nonEmpty)
        {
            var path = Path.Combine(LibraryRoot, workshopId);
            Directory.CreateDirectory(path);
            if (nonEmpty)
            {
                await File.WriteAllTextAsync(Path.Combine(path, "existing.txt"), "keep");
            }
        }

        public void Dispose()
        {
            Service.Dispose();
            httpClient.Dispose();
            temporaryDirectory.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
