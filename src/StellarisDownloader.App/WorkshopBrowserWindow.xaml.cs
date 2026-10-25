using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Data.Sqlite;
using Microsoft.Web.WebView2.Core;
using StellarisDownloader.App.Models;
using StellarisDownloader.App.Services;
using StellarisDownloader.App.ViewModels;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App;

public partial class WorkshopBrowserWindow : Window
{
    private static readonly Uri WorkshopHome = new(
        "https://steamcommunity.com/app/281990/workshop/");
    private static readonly Uri WebView2DownloadPage = new(
        "https://developer.microsoft.com/en-us/microsoft-edge/webview2/");

    private readonly DownloadQueueViewModel viewModel;
    private readonly IInstalledWorkshopStateProvider installedStateProvider;
    private readonly WorkshopFavoritesViewModel favorites;
    private int syncVersion;
    private bool closed;
    private bool waitingToClose;
    private IReadOnlyList<string> knownInstalledIds = [];
    private string? currentWorkshopId;
    private bool browserInitialized;

    public WorkshopBrowserWindow(
        DownloadQueueViewModel viewModel,
        IInstalledWorkshopStateProvider installedStateProvider,
        WorkshopFavoritesViewModel favorites)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(installedStateProvider);
        ArgumentNullException.ThrowIfNull(favorites);
        InitializeComponent();
        this.viewModel = viewModel;
        this.installedStateProvider = installedStateProvider;
        this.favorites = favorites;
        FavoritesPanel.DataContext = favorites;
        favorites.PropertyChanged += Favorites_PropertyChanged;
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        ((INotifyCollectionChanged)viewModel.Items).CollectionChanged += Items_CollectionChanged;
        DataContext = viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        closed = true;
        browserInitialized = false;
        favorites.PropertyChanged -= Favorites_PropertyChanged;
        viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        ((INotifyCollectionChanged)viewModel.Items).CollectionChanged -= Items_CollectionChanged;
        WorkshopWebView.Dispose();
        base.OnClosed(e);
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || !favorites.IsBusy)
        {
            return;
        }

        e.Cancel = true;
        if (waitingToClose)
        {
            return;
        }

        waitingToClose = true;
        await favorites.FlushAsync().ConfigureAwait(true);
        waitingToClose = false;
        if (!closed && favorites.LastError is null)
        {
            Close();
        }
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (browserInitialized)
        {
            return;
        }

        try
        {
            await favorites.LoadAsync().ConfigureAwait(true);
            if (closed) { return; }
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString(null);
            await WorkshopWebView.EnsureCoreWebView2Async().ConfigureAwait(true);
            if (closed) { return; }
            ConfigureBrowser();
            browserInitialized = true;
            WorkshopWebView.Visibility = Visibility.Visible;
            WorkshopWebView.CoreWebView2.Navigate(WorkshopHome.AbsoluteUri);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            RuntimeMissingPanel.Visibility = Visibility.Visible;
            BrowserStatusText.Visibility = Visibility.Visible;
        }
        catch (Exception exception) when (exception is InvalidOperationException or COMException)
        {
            BrowserStatusText.Text = exception.Message;
            BrowserStatusText.Visibility = Visibility.Visible;
        }
    }

    private void ConfigureBrowser()
    {
        var core = WorkshopWebView.CoreWebView2;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsWebMessageEnabled = true;
        core.NavigationStarting += Core_NavigationStarting;
        core.NavigationCompleted += Core_NavigationCompleted;
        core.SourceChanged += Core_SourceChanged;
        core.DocumentTitleChanged += Core_DocumentTitleChanged;
        core.HistoryChanged += Core_HistoryChanged;
        core.NewWindowRequested += Core_NewWindowRequested;
        core.WebMessageReceived += Core_WebMessageReceived;
    }

    private void Core_NavigationStarting(
        object? sender,
        CoreWebView2NavigationStartingEventArgs e)
    {
        var target = Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ? uri : null;
        var decision = SteamCommunitySecurityPolicy.DecideNavigation(target);
        if (decision.Disposition == BrowserNavigationDisposition.OpenInWebView)
        {
            BrowserStatusText.Visibility = Visibility.Collapsed;
            return;
        }

        e.Cancel = true;
        if (decision.Disposition == BrowserNavigationDisposition.OpenInSystemBrowser
            && decision.Target is not null)
        {
            OpenSystemBrowser(decision.Target);
        }
    }

    private async void Core_NavigationCompleted(
        object? sender,
        CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            BrowserStatusText.Visibility = Visibility.Visible;
            return;
        }

        BrowserStatusText.Visibility = Visibility.Collapsed;
        var source = GetCurrentSource();
        if (!SteamCommunitySecurityPolicy.IsTrustedMessageSource(source))
        {
            return;
        }

        try
        {
            await WorkshopWebView.CoreWebView2.ExecuteScriptAsync(
                WorkshopBridgeScriptLoader.Load()).ConfigureAwait(true);
            await SyncBrowserStateAsync().ConfigureAwait(true);
        }
        catch (InvalidOperationException exception)
        {
            BrowserStatusText.Text = exception.Message;
            BrowserStatusText.Visibility = Visibility.Visible;
        }
    }

    private void Core_SourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        var source = GetCurrentSource();
        CurrentUrlText.Text = source?.AbsoluteUri ?? string.Empty;
        currentWorkshopId = source is not null
            && SteamCommunitySecurityPolicy.IsTrustedMessageSource(source)
            && WorkshopIdParser.TryParse(source.AbsoluteUri, out var workshopId, out _)
                ? workshopId
                : null;
        AddCurrentButton.IsEnabled = currentWorkshopId is not null && !viewModel.IsBusy;
        UpdateFavoriteCurrentButton();
    }

    private void Core_DocumentTitleChanged(object? sender, object e)
    {
        PageTitleText.Text = WorkshopWebView.CoreWebView2.DocumentTitle;
    }

    private void Core_HistoryChanged(object? sender, object e)
    {
        BackButton.IsEnabled = WorkshopWebView.CoreWebView2.CanGoBack;
        ForwardButton.IsEnabled = WorkshopWebView.CoreWebView2.CanGoForward;
    }

    private void Core_NewWindowRequested(
        object? sender,
        CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        var target = Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ? uri : null;
        var decision = SteamCommunitySecurityPolicy.DecideNavigation(target);
        if (decision.Disposition == BrowserNavigationDisposition.OpenInWebView
            && decision.Target is not null)
        {
            WorkshopWebView.CoreWebView2.Navigate(decision.Target.AbsoluteUri);
        }
        else if (decision.Disposition == BrowserNavigationDisposition.OpenInSystemBrowser
                 && decision.Target is not null)
        {
            OpenSystemBrowser(decision.Target);
        }
    }

    private async void Core_WebMessageReceived(
        object? sender,
        CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source)
            || !IsCurrentDocument(source))
        {
            return;
        }

        var validation = WorkshopWebMessageValidator.Validate(source, e.WebMessageAsJson);
        if (!validation.IsValid)
        {
            return;
        }

        try
        {
            if (validation.MessageType == "enqueueWorkshopIds")
            {
                await viewModel.EnqueueAsync(
                    string.Join(Environment.NewLine, validation.WorkshopIds)).ConfigureAwait(true);
            }
            else
            {
                var id = validation.WorkshopIds[0];
                var saved = await favorites.SetFavoriteAsync(id,
                    validation.MessageType == "addWorkshopFavorite", validation.Title).ConfigureAwait(true);
                if (IsCurrentDocument(source))
                {
                    var confirmation = JsonSerializer.Serialize(new
                    {
                        id,
                        favorite = favorites.Contains(id),
                        error = saved ? null : favorites.LastError,
                    });
                    await WorkshopWebView.CoreWebView2.ExecuteScriptAsync(
                        $"window.__stellarisDownloaderWorkshopBridge?.confirmFavorite({confirmation});").ConfigureAwait(true);
                }
            }
            await SyncBrowserStateAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Closing or cancelling a lookup leaves the shared queue in a valid state.
        }
        catch (Exception exception) when (IsBrowserStateFailure(exception))
        {
            BrowserStatusText.Text = exception.Message;
            BrowserStatusText.Visibility = Visibility.Visible;
        }
    }

    private async void AddCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (currentWorkshopId is null)
        {
            return;
        }

        try
        {
            await viewModel.EnqueueAsync(currentWorkshopId).ConfigureAwait(true);
            await SyncBrowserStateAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The item remains safe to add again.
        }
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        var workshopIds = BrowserQueueList.SelectedItems
            .OfType<DownloadQueueItemViewModel>()
            .Select(item => item.WorkshopId)
            .ToArray();
        viewModel.Remove(workshopIds);
        _ = SyncBrowserStateAsync();
    }

    private void ClearQueue_Click(object sender, RoutedEventArgs e)
    {
        viewModel.Clear();
        _ = SyncBrowserStateAsync();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DownloadQueueViewModel.IsBusy))
        {
            AddCurrentButton.IsEnabled = currentWorkshopId is not null && !viewModel.IsBusy;
        }

        if (e.PropertyName is nameof(DownloadQueueViewModel.SucceededCount)
            or nameof(DownloadQueueViewModel.IsBusy))
        {
            _ = SyncBrowserStateAsync();
        }
    }

    private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        _ = SyncBrowserStateAsync();

    private async Task SyncBrowserStateAsync()
    {
        var version = ++syncVersion;
        var source = GetCurrentSource();
        if (!browserInitialized
            || favorites.IsBusy
            || source is null
            || !SteamCommunitySecurityPolicy.IsTrustedMessageSource(source))
        {
            return;
        }

        try
        {
            var installedIds = knownInstalledIds;
            try
            {
                installedIds = await installedStateProvider.GetInstalledWorkshopIdsAsync().ConfigureAwait(true);
            }
            catch (Exception exception) when (IsBrowserStateFailure(exception))
            {
                // A missing/unreadable mod library must not prevent restoring browser favorites.
                BrowserStatusText.Text = exception.Message;
                BrowserStatusText.Visibility = Visibility.Visible;
            }
            if (closed || version != syncVersion || favorites.IsBusy || !IsCurrentDocument(source))
            {
                return;
            }
            knownInstalledIds = installedIds;

            var queuedIds = viewModel.Items
                .Select(item => item.WorkshopId)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var favoriteIds = favorites.Items.Select(item => item.WorkshopId).ToArray();
            var stateJson = JsonSerializer.Serialize(new { queuedIds, installedIds, favoriteIds });
            await WorkshopWebView.CoreWebView2.ExecuteScriptAsync(
                $"window.__stellarisDownloaderWorkshopBridge?.syncState({stateJson});")
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // A later navigation or application close owns the browser state.
        }
        catch (Exception exception) when (IsBrowserStateFailure(exception))
        {
            BrowserStatusText.Text = exception.Message;
            BrowserStatusText.Visibility = Visibility.Visible;
        }
    }

    private void Favorites_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        UpdateFavoriteCurrentButton();
        if (e.PropertyName == nameof(WorkshopFavoritesViewModel.LastError) && favorites.LastError is { } error)
        {
            BrowserStatusText.Text = error;
            BrowserStatusText.Visibility = Visibility.Visible;
        }
        if (e.PropertyName == nameof(WorkshopFavoritesViewModel.IsBusy) && !favorites.IsBusy)
        {
            _ = SyncBrowserStateAsync();
        }
    }

    private void UpdateFavoriteCurrentButton()
    {
        FavoriteCurrentButton.IsEnabled = currentWorkshopId is not null && !favorites.IsBusy;
        var saved = currentWorkshopId is not null && favorites.Contains(currentWorkshopId);
        FavoriteCurrentButton.Content = TryFindResource(saved ? "Browser.UnfavoriteCurrent" : "Browser.FavoriteCurrent");
    }

    private async void FavoriteCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (currentWorkshopId is { } id)
        {
            var title = WorkshopWebView.CoreWebView2.DocumentTitle;
            await favorites.SetFavoriteAsync(id, !favorites.Contains(id), title[..Math.Min(title.Length, 1024)]).ConfigureAwait(true);
        }
    }

    private void OpenFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (browserInitialized && BrowserFavoritesList.SelectedItem is WorkshopFavorite favorite)
        {
            WorkshopWebView.CoreWebView2.Navigate($"https://steamcommunity.com/sharedfiles/filedetails/?id={favorite.WorkshopId}");
        }
    }

    private async void DownloadFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserFavoritesList.SelectedItem is WorkshopFavorite favorite)
        {
            try
            {
                await viewModel.EnqueueAsync(favorite.WorkshopId).ConfigureAwait(true);
                BrowserSidebar.SelectedIndex = 0;
                await SyncBrowserStateAsync().ConfigureAwait(true);
            }
            catch (OperationCanceledException) { }
        }
    }

    private async void RemoveFavorite_Click(object sender, RoutedEventArgs e)
    {
        if (BrowserFavoritesList.SelectedItem is WorkshopFavorite favorite)
        {
            await favorites.SetFavoriteAsync(favorite.WorkshopId, false).ConfigureAwait(true);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (browserInitialized && WorkshopWebView.CoreWebView2.CanGoBack)
        {
            WorkshopWebView.CoreWebView2.GoBack();
        }
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (browserInitialized && WorkshopWebView.CoreWebView2.CanGoForward)
        {
            WorkshopWebView.CoreWebView2.GoForward();
        }
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        if (browserInitialized)
        {
            WorkshopWebView.CoreWebView2.Reload();
        }
    }

    private void CurrentUrlText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !browserInitialized)
        {
            return;
        }

        e.Handled = true;
        if (SteamCommunitySecurityPolicy.TryResolveAddressBarTarget(CurrentUrlText.Text, out var target))
        {
            WorkshopWebView.CoreWebView2.Navigate(target.AbsoluteUri);
        }
    }

    private void InstallRuntime_Click(object sender, RoutedEventArgs e) =>
        OpenSystemBrowser(WebView2DownloadPage);

    private Uri? GetCurrentSource() =>
        browserInitialized
        && Uri.TryCreate(WorkshopWebView.CoreWebView2.Source, UriKind.Absolute, out var source)
            ? source
            : null;

    private bool IsCurrentDocument(Uri messageSource)
    {
        var current = GetCurrentSource();
        return current is not null
            && string.Equals(
                messageSource.GetComponents(
                    UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                    UriFormat.UriEscaped),
                current.GetComponents(
                    UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                    UriFormat.UriEscaped),
                StringComparison.Ordinal);
    }

    private void OpenSystemBrowser(Uri target)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target.AbsoluteUri)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            BrowserStatusText.Text = exception.Message;
            BrowserStatusText.Visibility = Visibility.Visible;
        }
    }

    private static bool IsBrowserStateFailure(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or ArgumentException
            or NotSupportedException
            or COMException
            or SqliteException;
}
