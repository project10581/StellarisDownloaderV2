using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Microsoft.Win32;
using StellarisDownloader.App.ViewModels;

namespace StellarisDownloader.App;

public partial class ModSnapshotsWindow : Window
{
    private readonly ModSnapshotsViewModel viewModel;

    public ModSnapshotsWindow(ModSnapshotsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        this.viewModel = viewModel;
        DataContext = viewModel;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureExportDirectory())
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = Text("Snapshots.Export"),
            Filter = Text("Snapshots.ExportFilter"),
            FileName = "stellaris-mods-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture),
            DefaultExt = ".json",
            AddExtension = true,
            InitialDirectory = viewModel.DefaultExportDirectory,
        };
        if (dialog.ShowDialog(this) is true)
        {
            await viewModel.ExportCurrentAsync(dialog.FileName, asText: dialog.FilterIndex == 2).ConfigureAwait(true);
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) =>
        await viewModel.InitializeAsync().ConfigureAwait(true);

    private async void SaveVersion_Click(object sender, RoutedEventArgs e) =>
        await viewModel.SaveVersionAsync().ConfigureAwait(true);

    private async void Baseline_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (viewModel is not null && !viewModel.IsBusy && IsLoaded)
        {
            await viewModel.SelectBaselineAsync(BaselineSelector.SelectedItem as SnapshotSourceViewModel).ConfigureAwait(true);
        }
    }

    private async void RefreshHistory_Click(object sender, RoutedEventArgs e) =>
        await viewModel.LoadHistoryAsync().ConfigureAwait(true);

    private async void CompareFiles_Click(object sender, RoutedEventArgs e)
    {
        var baseline = ChooseSnapshot("Snapshots.ChooseBaseline");
        if (baseline is not null && ChooseSnapshot("Snapshots.ChooseCurrent") is { } current)
        {
            await viewModel.CompareFilesAsync(baseline, current).ConfigureAwait(true);
        }
    }

    private async void ExportReport_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureExportDirectory())
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = Text("Snapshots.ExportReport"),
            Filter = Text("Snapshots.TextFilter"),
            DefaultExt = ".txt",
            FileName = "stellaris-mod-changes-" + DateTime.Now.ToString("yyyy-MM-dd-HHmmss", CultureInfo.InvariantCulture),
            InitialDirectory = viewModel.DefaultExportDirectory,
        };
        if (dialog.ShowDialog(this) is true)
        {
            await viewModel.ExportReportAsync(dialog.FileName).ConfigureAwait(true);
        }
    }

    private void CopyMissing_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanCopyMissing)
        {
            return;
        }

        try
        {
            Clipboard.SetText(viewModel.GetMissingIds());
        }
        catch (ExternalException exception)
        {
            ShowError(exception.Message);
        }
    }

    private void OpenWorkshop_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Hyperlink { Tag: string url })
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            ShowError(exception.Message);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => viewModel.Cancel();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (viewModel.IsBusy)
        {
            viewModel.Cancel();
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    private string? ChooseSnapshot(string titleKey)
    {
        var dialog = new OpenFileDialog
        {
            Title = Text(titleKey),
            Filter = Text("Snapshots.JsonFilter"),
            InitialDirectory = viewModel.HistoryDirectory,
        };
        return dialog.ShowDialog(this) is true ? dialog.FileName : null;
    }

    private bool EnsureExportDirectory()
    {
        try
        {
            Directory.CreateDirectory(viewModel.DefaultExportDirectory);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowError(exception.Message);
            return false;
        }
    }

    private string Text(string key) => TryFindResource(key) as string ?? key;

    private void ShowError(string message) =>
        MessageBox.Show(this, message, Text("Snapshots.Error"), MessageBoxButton.OK, MessageBoxImage.Error);
}
