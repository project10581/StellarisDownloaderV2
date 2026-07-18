using StellarisDownloader.Core.Integrations;
using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Core.Services;

public sealed class AppUpdateService : IAppUpdateService, IDisposable
{
    public const string RepositoryUrl =
        "https://github.com/project10581/StellarisDownloaderV2";

    private readonly IAppUpdateClient client;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private AppUpdateCandidate? availableUpdate;
    private bool isDownloaded;
    private bool disposed;

    public AppUpdateService()
        : this(new VelopackUpdateClient(RepositoryUrl))
    {
    }

    internal AppUpdateService(IAppUpdateClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        this.client = client;
    }

    public async Task<AppUpdateInfo> CheckAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            progress?.Report(new OperationProgress(
                "CheckingAppUpdate",
                Completed: 0,
                Total: 1,
                WorkshopId: null,
                Message: "Checking for an application update."));

            if (!client.IsInstalled)
            {
                availableUpdate = null;
                isDownloaded = false;
                progress?.Report(new OperationProgress(
                    "AppUpdateCheckCompleted",
                    Completed: 1,
                    Total: 1,
                    WorkshopId: null,
                    Message: "Application updates are available from packaged builds."));
                return BuildInfo();
            }

            availableUpdate = await client.CheckAsync(cancellationToken).ConfigureAwait(false);
            isDownloaded = false;
            progress?.Report(new OperationProgress(
                "AppUpdateCheckCompleted",
                Completed: 1,
                Total: 1,
                WorkshopId: null,
                Message: availableUpdate is null
                    ? "The application is up to date."
                    : $"Application version {availableUpdate.Version} is available."));
            return BuildInfo();
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<AppUpdateInfo> DownloadAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var update = availableUpdate
                ?? throw new InvalidOperationException(
                    "Check for an application update before downloading it.");

            isDownloaded = false;
            var progressCallback = new Action<int>(value =>
            {
                var completed = Math.Clamp(value, 0, 100);
                progress?.Report(new OperationProgress(
                    "DownloadingAppUpdate",
                    completed,
                    Total: 100,
                    WorkshopId: null,
                    Message: $"Downloading application update: {completed}%."));
            });
            progressCallback(0);
            await client.DownloadAsync(update, progressCallback, cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            isDownloaded = true;
            progressCallback(100);
            return BuildInfo();
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task ApplyAndRestartAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var update = availableUpdate;
            if (update is null || !isDownloaded)
            {
                throw new InvalidOperationException(
                    "Download the application update before applying it.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new OperationProgress(
                "ApplyingAppUpdate",
                Completed: 1,
                Total: 1,
                WorkshopId: null,
                Message: "Restarting to apply the application update."));
            client.ApplyAndRestart(update);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        operationGate.Dispose();
        GC.SuppressFinalize(this);
    }

    private AppUpdateInfo BuildInfo() => new()
    {
        CurrentVersion = client.CurrentVersion ?? "development",
        LatestVersion = availableUpdate?.Version,
        ReleaseNotes = availableUpdate?.ReleaseNotes,
        IsInstalled = client.IsInstalled,
        IsUpdateAvailable = availableUpdate is not null,
        IsDownloaded = isDownloaded,
    };

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
