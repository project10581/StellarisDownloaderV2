using DesktopApp = StellarisDownloader.App.App;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Tests;

public sealed class AppStartupTests
{
    [Fact]
    public void ModUpdateCheckFailureDoesNotAbortStartupOrTheApplicationUpdateCheck()
    {
        WpfTestRunner.Run(async () =>
        {
            using var updates = new UpdateSelectionViewModel(
                new StubSettingsStore(new AppSettings { LibraryRoot = "C:\\Mods" }),
                new CompiledXamlTests.UnexpectedModOperationService());
            var checkedApplication = false;

            await DesktopApp.RunStartupChecksAsync(
                () => updates.CheckUpdatesAsync(),
                () => { checkedApplication = true; return Task.CompletedTask; });

            Assert.True(checkedApplication);
            Assert.False(updates.IsBusy);
        });
    }

    [Fact]
    public async Task ApplicationUpdateCheckFailureDoesNotEscapeStartupChecks()
    {
        var finishedModCheck = false;
        await DesktopApp.RunStartupChecksAsync(
            () => { finishedModCheck = true; return Task.CompletedTask; },
            () => throw new InvalidOperationException("Update check failed."));

        Assert.True(finishedModCheck);
    }
}
