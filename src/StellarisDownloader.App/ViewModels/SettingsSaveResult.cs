namespace StellarisDownloader.App.ViewModels;

public sealed record SettingsSaveResult(
    bool Succeeded,
    bool SettingsCommitted,
    bool RequiresScanRetry,
    bool RequiresManualRepair,
    LibrarySwitchSummary? Summary,
    string? Error);
