using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using StellarisDownloader.App.ViewModels;

namespace StellarisDownloader.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
        SetResourceReference(
            TitleProperty,
            viewModel.IsInitialization ? "Settings.InitializationTitle" : "Settings.Title");
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.Dispose();
        base.OnClosed(e);
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = GetString("Settings.LibraryRoot"),
            Multiselect = false,
        };
        if (Directory.Exists(viewModel.LibraryRoot))
        {
            dialog.InitialDirectory = viewModel.LibraryRoot;
        }

        if (dialog.ShowDialog(this) is true)
        {
            viewModel.LibraryRoot = dialog.FolderName;
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var confirmation = MessageBox.Show(
            this,
            GetString("Settings.ConfirmMessage"),
            GetString("Settings.ConfirmTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var result = await viewModel.SaveAsync().ConfigureAwait(true);
        if (!result.Succeeded)
        {
            MessageBox.Show(
                this,
                result.Error ?? GetString("Settings.SaveFailed"),
                GetString("Settings.ErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (result.RequiresScanRetry)
        {
            MessageBox.Show(
                this,
                GetString("Settings.ScanRetry"),
                GetString("Settings.ErrorTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        else if (result.Summary is not null)
        {
            var format = GetString("Settings.SwitchSummary");
            var message = string.Format(
                CultureInfo.CurrentCulture,
                format,
                result.Summary.AddedCount,
                result.Summary.RemovedCount,
                result.Summary.EmptyDirectoryCount,
                result.Summary.IgnoredDirectoryCount);
            MessageBox.Show(
                this,
                message,
                GetString("Settings.SuccessTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        viewModel.Cancel();
        DialogResult = false;
    }

    private string GetString(string key) => TryFindResource(key) as string ?? key;
}
