namespace StellarisDownloader.App.Services;

public interface ILocalizationService
{
    string CurrentLanguage { get; }

    void SetLanguage(string language);
}
