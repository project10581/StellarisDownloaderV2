using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Models;

namespace StellarisDownloader.Tests;

public sealed class UpdateCheckItemViewModelTests
{
    [Fact]
    public void RejectedSelectionNotifiesBindingToRestoreTheActualValue()
    {
        var item = new UpdateCheckItemViewModel(new UpdateCheckResult
        {
            WorkshopId = "123",
            State = UpdateState.UpdateAvailable,
        });
        item.IsSelected = false;
        item.SetSelectionEnabled(false);
        var changes = new List<string?>();
        item.PropertyChanged += (_, args) => changes.Add(args.PropertyName);

        item.IsSelected = true;

        Assert.False(item.IsSelected);
        Assert.Equal([nameof(item.IsSelected)], changes);
    }
}
