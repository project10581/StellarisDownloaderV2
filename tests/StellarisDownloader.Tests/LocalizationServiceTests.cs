using System.Collections.Concurrent;
using System.Windows;
using StellarisDownloader.App.Services;
using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Tests;

public sealed class LocalizationServiceTests
{
    [Fact]
    public void EnglishAndChineseResourceKeysAreIdentical()
    {
        RunInSta(() =>
        {
            var english = LocalizationService.LoadDictionary(AppSettings.DefaultLanguage);
            var chinese = LocalizationService.LoadDictionary(AppSettings.SimplifiedChineseLanguage);

            var englishKeys = english.Keys.Cast<object>().Select(key => key.ToString()).ToHashSet();
            var chineseKeys = chinese.Keys.Cast<object>().Select(key => key.ToString()).ToHashSet();

            Assert.NotEmpty(englishKeys);
            Assert.Equal(englishKeys, chineseKeys);
        });
    }

    [Fact]
    public void SwitchingLanguageReplacesOnlyTheLocalizationDictionary()
    {
        RunInSta(() =>
        {
            var resources = new ResourceDictionary();
            var unrelated = new ResourceDictionary();
            unrelated["Test.Key"] = "keep";
            resources.MergedDictionaries.Add(unrelated);
            var service = new LocalizationService(resources);

            service.SetLanguage(AppSettings.DefaultLanguage);
            service.SetLanguage(AppSettings.SimplifiedChineseLanguage);

            Assert.Equal(AppSettings.SimplifiedChineseLanguage, service.CurrentLanguage);
            Assert.Contains(unrelated, resources.MergedDictionaries);
            Assert.Equal(2, resources.MergedDictionaries.Count);
            Assert.Equal(
                "设置",
                resources.MergedDictionaries.Single(
                    dictionary => dictionary.Source is not null)["Settings.Title"]);
        });
    }

    [Fact]
    public void UnsupportedLanguageIsRejected()
    {
        RunInSta(() => Assert.Throws<ArgumentOutOfRangeException>(
            () => LocalizationService.LoadDictionary("unsupported")));
    }

    private static void RunInSta(Action action)
    {
        var exceptions = new ConcurrentQueue<Exception>();
        var thread = new Thread(() =>
        {
            Application? application = null;
            try
            {
                if (Application.Current is null)
                {
                    application = new Application();
                }

                action();
            }
            catch (Exception exception)
            {
                exceptions.Enqueue(exception);
            }
            finally
            {
                application?.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exceptions.TryDequeue(out var exception))
        {
            throw new InvalidOperationException("STA test action failed.", exception);
        }
    }
}
