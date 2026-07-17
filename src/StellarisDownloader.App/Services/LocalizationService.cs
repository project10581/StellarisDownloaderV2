using System.Windows;
using StellarisDownloader.Core.Models;

namespace StellarisDownloader.App.Services;

public sealed class LocalizationService : ILocalizationService
{
    private const string ResourceMarker = "/Resources/Strings.";
    private readonly ResourceDictionary applicationResources;

    public LocalizationService(ResourceDictionary applicationResources)
    {
        ArgumentNullException.ThrowIfNull(applicationResources);
        this.applicationResources = applicationResources;
    }

    public string CurrentLanguage { get; private set; } = AppSettings.DefaultLanguage;

    public void SetLanguage(string language)
    {
        var dictionary = LoadDictionary(language);
        var localizedDictionaries = applicationResources.MergedDictionaries
            .Where(IsLocalizationDictionary)
            .ToArray();

        foreach (var existing in localizedDictionaries)
        {
            applicationResources.MergedDictionaries.Remove(existing);
        }

        applicationResources.MergedDictionaries.Add(dictionary);
        CurrentLanguage = language;
    }

    public static ResourceDictionary LoadDictionary(string language)
    {
        if (language is not AppSettings.DefaultLanguage and not AppSettings.SimplifiedChineseLanguage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(language),
                language,
                "Only English and Simplified Chinese are supported.");
        }

        return new ResourceDictionary
        {
            Source = new Uri(
                $"/StellarisDownloader.App;component/Resources/Strings.{language}.xaml",
                UriKind.Relative),
        };
    }

    private static bool IsLocalizationDictionary(ResourceDictionary dictionary) =>
        dictionary.Source?.OriginalString.Contains(ResourceMarker, StringComparison.Ordinal) is true;
}
