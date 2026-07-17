namespace StellarisDownloader.Core.Models;

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public const string DefaultLanguage = "en";
    public const string SimplifiedChineseLanguage = "zh-CN";

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public string? LibraryRoot { get; init; }

    public string Language { get; init; } = DefaultLanguage;

    public bool RefreshLibraryOnStartup { get; init; }

    public bool CheckModUpdatesOnStartup { get; init; }

    public bool CheckAppUpdatesOnStartup { get; init; }
}
