namespace StellarisDownloader.App.ViewModels;

public sealed record LibrarySwitchSummary(
    int AddedCount,
    int RemovedCount,
    int EmptyDirectoryCount,
    int IgnoredDirectoryCount);
