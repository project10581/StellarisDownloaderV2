using Velopack;
using Velopack.Sources;

namespace StellarisDownloader.Core.Integrations;

internal sealed class VelopackUpdateClient : IAppUpdateClient
{
    private readonly UpdateManager updateManager;

    public VelopackUpdateClient(string repositoryUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);
        updateManager = new UpdateManager(
            new GithubSource(repositoryUrl, accessToken: null, prerelease: false));
    }

    public bool IsInstalled => updateManager.IsInstalled;

    public string? CurrentVersion => updateManager.CurrentVersion?.ToString();

    public async Task<AppUpdateCandidate?> CheckAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var update = await updateManager.CheckForUpdatesAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return update is null
            ? null
            : new AppUpdateCandidate(
                update.TargetFullRelease.Version.ToString(),
                update.TargetFullRelease.NotesMarkdown,
                update);
    }

    public Task DownloadAsync(
        AppUpdateCandidate candidate,
        Action<int> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(progress);
        var update = GetNativeUpdate(candidate);
        return updateManager.DownloadUpdatesAsync(update, progress, cancellationToken);
    }

    public void ApplyAndRestart(AppUpdateCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var update = GetNativeUpdate(candidate);
        updateManager.ApplyUpdatesAndRestart(update.TargetFullRelease);
    }

    private static UpdateInfo GetNativeUpdate(AppUpdateCandidate candidate) =>
        candidate.NativeUpdate as UpdateInfo
        ?? throw new InvalidOperationException("The update candidate did not originate from Velopack.");
}
