namespace StellarisDownloader.Core.Integrations;

internal interface IAppUpdateClient
{
    bool IsInstalled { get; }

    string? CurrentVersion { get; }

    Task<AppUpdateCandidate?> CheckAsync(CancellationToken cancellationToken);

    Task DownloadAsync(
        AppUpdateCandidate candidate,
        Action<int> progress,
        CancellationToken cancellationToken);

    void ApplyAndRestart(AppUpdateCandidate candidate);
}
