using System.Windows;
using StellarisDownloader.App.ViewModels;

namespace StellarisDownloader.App;

public partial class DownloadQueueWindow : Window
{
    private readonly DownloadQueueViewModel viewModel;

    public DownloadQueueWindow(DownloadQueueViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var workshopIds = QueueList.SelectedItems
            .OfType<DownloadQueueItemViewModel>()
            .Select(item => item.WorkshopId)
            .ToArray();
        viewModel.Remove(workshopIds);
    }

    private void ClearQueue_Click(object sender, RoutedEventArgs e) => viewModel.Clear();
}
