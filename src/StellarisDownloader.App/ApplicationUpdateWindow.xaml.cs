using System.ComponentModel;
using System.Windows;
using StellarisDownloader.App.ViewModels;

namespace StellarisDownloader.App;

public partial class ApplicationUpdateWindow : Window
{
    private readonly ApplicationUpdateViewModel viewModel;
    private readonly bool disposeViewModel;

    public ApplicationUpdateWindow(
        ApplicationUpdateViewModel viewModel,
        bool disposeViewModel = true)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        this.viewModel = viewModel;
        this.disposeViewModel = disposeViewModel;
        DataContext = viewModel;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (viewModel.IsBusy)
        {
            viewModel.Cancel();
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (disposeViewModel)
        {
            viewModel.Dispose();
        }

        base.OnClosed(e);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!viewModel.HasChecked && !viewModel.IsBusy)
        {
            await viewModel.CheckAsync().ConfigureAwait(true);
        }
    }
}
