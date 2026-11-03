using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StellarisDownloader.App.ViewModels;

namespace StellarisDownloader.App;

public partial class DownloadQueueWindow : Window
{
    internal event Action<string>? WorkshopRequested;

    public DownloadQueueWindow(DownloadQueueViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }

    private void QueueItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left
            && sender is ListBoxItem { Content: DownloadQueueItemViewModel item })
        {
            WorkshopRequested?.Invoke(item.WorkshopId);
            e.Handled = true;
        }
    }
}
