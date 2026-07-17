namespace StellarisDownloader.Core.Models;

public sealed record SettingsLoadResult(
    AppSettings Settings,
    bool RequiresInitialization,
    string? CorruptBackupPath);
