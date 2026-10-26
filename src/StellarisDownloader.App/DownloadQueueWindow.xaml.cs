using System.Windows;
using StellarisDownloader.App.ViewModels;

namespace StellarisDownloader.App;

public partial class DownloadQueueWindow : Window
{
    public DownloadQueueWindow(DownloadQueueViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        DataContext = viewModel;
    }
}
