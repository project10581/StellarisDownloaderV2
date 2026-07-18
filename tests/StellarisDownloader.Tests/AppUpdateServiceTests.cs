using StellarisDownloader.Core.Integrations;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.Tests;

public sealed class AppUpdateServiceTests
{
    [Fact]
    public async Task UnpackagedBuildDoesNotContactTheUpdateSource()
    {
        var client = new StubAppUpdateClient
        {
            IsInstalled = false,
            CurrentVersion = "0.1.0",
        };
        using var service = new AppUpdateService(client);

        var result = await service.CheckAsync();

        Assert.Equal("0.1.0", result.CurrentVersion);
        Assert.False(result.IsInstalled);
        Assert.False(result.IsUpdateAvailable);
        Assert.False(result.IsDownloaded);
        Assert.Equal(0, client.CheckCallCount);
    }

    [Fact]
    public async Task CheckMapsAvailableVersionAndReleaseNotes()
    {
        var client = new StubAppUpdateClient
        {
            Candidate = Candidate("2.0.0", "## Improvements"),
        };
        using var service = new AppUpdateService(client);
        var progress = new RecordingProgress<OperationProgress>();

        var result = await service.CheckAsync(progress);

        Assert.True(result.IsInstalled);
        Assert.True(result.IsUpdateAvailable);
        Assert.False(result.IsDownloaded);
        Assert.Equal("1.0.0", result.CurrentVersion);
        Assert.Equal("2.0.0", result.LatestVersion);
        Assert.Equal("## Improvements", result.ReleaseNotes);
        Assert.Equal(1, client.CheckCallCount);
        Assert.Equal(
            ["CheckingAppUpdate", "AppUpdateCheckCompleted"],
            progress.Values.Select(value => value.Stage));
    }

    [Fact]
    public async Task NoUpdateIsReportedAsCurrent()
    {
        var client = new StubAppUpdateClient();
        using var service = new AppUpdateService(client);

        var result = await service.CheckAsync();

        Assert.True(result.IsInstalled);
        Assert.False(result.IsUpdateAvailable);
        Assert.Null(result.LatestVersion);
        Assert.Null(result.ReleaseNotes);
    }

    [Fact]
    public async Task DownloadReportsRealProgressAndEnablesRestart()
    {
        var client = new StubAppUpdateClient
        {
            Candidate = Candidate("2.0.0", null),
            DownloadHandler = (_, progress, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress(23);
                progress(81);
                return Task.CompletedTask;
            },
        };
        using var service = new AppUpdateService(client);
        var progress = new RecordingProgress<OperationProgress>();
        await service.CheckAsync();

        var result = await service.DownloadAsync(progress);

        Assert.True(result.IsDownloaded);
        Assert.Equal(1, client.DownloadCallCount);
        Assert.Equal([0, 23, 81, 100], progress.Values.Select(value => value.Completed));
        Assert.All(progress.Values, value => Assert.Equal(100, value.Total));
    }

    [Fact]
    public async Task DownloadFailureDoesNotMarkTheUpdateAsDownloaded()
    {
        var client = new StubAppUpdateClient
        {
            Candidate = Candidate("2.0.0", null),
            DownloadHandler = (_, _, _) => throw new HttpRequestException("Offline."),
        };
        using var service = new AppUpdateService(client);
        await service.CheckAsync();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => service.DownloadAsync());

        Assert.Equal("Offline.", exception.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApplyAndRestartAsync());
    }

    [Fact]
    public async Task DownloadHonorsCancellation()
    {
        var entered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new StubAppUpdateClient
        {
            Candidate = Candidate("2.0.0", null),
            DownloadHandler = async (_, _, cancellationToken) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
        };
        using var service = new AppUpdateService(client);
        await service.CheckAsync();
        using var cancellation = new CancellationTokenSource();

        var download = service.DownloadAsync(cancellationToken: cancellation.Token);
        await entered.Task;
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
    }

    [Fact]
    public async Task ApplyRequiresDownloadAndUsesTheCheckedCandidate()
    {
        var candidate = Candidate("2.0.0", null);
        var client = new StubAppUpdateClient { Candidate = candidate };
        using var service = new AppUpdateService(client);
        await service.CheckAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApplyAndRestartAsync());
        await service.DownloadAsync();
        await service.ApplyAndRestartAsync();

        Assert.Same(candidate, client.AppliedCandidate);
        Assert.Equal(1, client.ApplyCallCount);
    }

    private static AppUpdateCandidate Candidate(string version, string? releaseNotes) =>
        new(version, releaseNotes, new object());

    private sealed class StubAppUpdateClient : IAppUpdateClient
    {
        public bool IsInstalled { get; init; } = true;

        public string? CurrentVersion { get; init; } = "1.0.0";

        public AppUpdateCandidate? Candidate { get; init; }

        public Func<AppUpdateCandidate, Action<int>, CancellationToken, Task>
            DownloadHandler
        { get; init; } = (_, _, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };

        public int CheckCallCount { get; private set; }

        public int DownloadCallCount { get; private set; }

        public int ApplyCallCount { get; private set; }

        public AppUpdateCandidate? AppliedCandidate { get; private set; }

        public Task<AppUpdateCandidate?> CheckAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckCallCount++;
            return Task.FromResult(Candidate);
        }

        public Task DownloadAsync(
            AppUpdateCandidate candidate,
            Action<int> progress,
            CancellationToken cancellationToken)
        {
            DownloadCallCount++;
            return DownloadHandler(candidate, progress, cancellationToken);
        }

        public void ApplyAndRestart(AppUpdateCandidate candidate)
        {
            ApplyCallCount++;
            AppliedCandidate = candidate;
        }
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Values { get; } = [];

        public void Report(T value)
        {
            Values.Add(value);
        }
    }
}
