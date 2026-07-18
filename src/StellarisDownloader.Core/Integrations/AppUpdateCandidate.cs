namespace StellarisDownloader.Core.Integrations;

internal sealed record AppUpdateCandidate(
    string Version,
    string? ReleaseNotes,
    object NativeUpdate);
