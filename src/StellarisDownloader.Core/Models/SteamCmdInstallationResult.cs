namespace StellarisDownloader.Core.Models;

public sealed record SteamCmdInstallationResult(
    OperationStatus Status,
    string ExecutablePath,
    bool InstalledNow,
    string? Error);
