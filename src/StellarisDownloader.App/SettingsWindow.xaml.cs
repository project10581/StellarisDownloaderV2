using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Models;

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
        viewModel.Saved += OnSaved;
        viewModel.Cancelled += OnCancelled;
        SetResourceReference(
            TitleProperty,
            viewModel.IsInitialization ? "Settings.InitializationTitle" : "Settings.Title");
    }

    protected override void OnClosed(EventArgs e)
    {
        viewModel.Saved -= OnSaved;
        viewModel.Cancelled -= OnCancelled;
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

    private void OnSaved(SettingsSaveResult result)
    {
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

    private void OnCancelled() => DialogResult = false;

    private string GetString(string key) => TryFindResource(key) as string ?? key;
}
