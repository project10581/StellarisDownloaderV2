namespace StellarisDownloader.Core.Persistence;

public sealed class AppDataPaths
{
    public const string ApplicationDirectoryName = "StellarisDownloaderV2";

    public AppDataPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        RootDirectory = Path.GetFullPath(rootDirectory);
        SettingsFile = Path.Combine(RootDirectory, "settings.json");
        DatabaseFile = Path.Combine(RootDirectory, "library.db");
        SteamCmdDirectory = Path.Combine(RootDirectory, "steamcmd");
        CacheDirectory = Path.Combine(RootDirectory, "cache");
        PreviewCacheDirectory = Path.Combine(CacheDirectory, "previews");
        LogsDirectory = Path.Combine(RootDirectory, "logs");
    }

    public string RootDirectory { get; }

    public string SettingsFile { get; }

    public string DatabaseFile { get; }

    public string SteamCmdDirectory { get; }

    public string CacheDirectory { get; }

    public string PreviewCacheDirectory { get; }

    public string LogsDirectory { get; }

    public static AppDataPaths CreateDefault()
    {
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        if (string.IsNullOrWhiteSpace(localApplicationData))
        {
            throw new InvalidOperationException("The local application data directory is unavailable.");
        }

        return new AppDataPaths(Path.Combine(localApplicationData, ApplicationDirectoryName));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(SteamCmdDirectory);
        Directory.CreateDirectory(PreviewCacheDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
