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

        public static async Task<SteamCmdServiceFixture> CreateAsync(ProcessRunResult processResult)
        {
            var temporaryDirectory = new TemporaryDirectory();
            var steamCmdDirectory = temporaryDirectory.GetPath("steamcmd");
            Directory.CreateDirectory(steamCmdDirectory);
            await File.WriteAllTextAsync(Path.Combine(steamCmdDirectory, "steamcmd.exe"), "placeholder");
            var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
                StubHttpMessageHandler.Response(HttpStatusCode.InternalServerError)));
            var httpClient = new HttpClient(handler);
            var runner = new StubProcessRunner(processResult);
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
