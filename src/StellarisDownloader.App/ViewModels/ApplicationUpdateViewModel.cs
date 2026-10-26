using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App.ViewModels;

public sealed partial class ApplicationUpdateViewModel : ObservableObject, IDisposable
{
    private readonly IAppUpdateService appUpdateService;
    private readonly Func<Task>? prepareForRestart;
    private CancellationTokenSource? activeCancellation;
    private bool disposed;

    public ApplicationUpdateViewModel(IAppUpdateService appUpdateService, Func<Task>? prepareForRestart = null)
    {
        ArgumentNullException.ThrowIfNull(appUpdateService);
        this.appUpdateService = appUpdateService;
        this.prepareForRestart = prepareForRestart;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentVersion), nameof(LatestVersion), nameof(ReleaseNotes),
        nameof(IsInstalled), nameof(IsUpdateAvailable), nameof(IsDownloaded),
        nameof(CanDownload), nameof(CanApplyAndRestart), nameof(CanDefer))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(DownloadCommand), nameof(ApplyAndRestartCommand),
        nameof(CancelCommand), nameof(DeferCommand))]
    public partial AppUpdateInfo? UpdateInfo { get; private set; }

    public string? CurrentVersion => UpdateInfo?.CurrentVersion;
    public string? LatestVersion => UpdateInfo?.LatestVersion;
    public string? ReleaseNotes => UpdateInfo?.ReleaseNotes;
    public bool IsInstalled => UpdateInfo?.IsInstalled == true;
    public bool IsUpdateAvailable => UpdateInfo?.IsUpdateAvailable == true;
    public bool IsDownloaded => UpdateInfo?.IsDownloaded == true;

    [ObservableProperty]
    public partial bool HasChecked { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCheck), nameof(CanDownload), nameof(CanCancel),
        nameof(CanApplyAndRestart), nameof(CanDefer))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand), nameof(DownloadCommand), nameof(ApplyAndRestartCommand),
        nameof(CancelCommand), nameof(DeferCommand))]
    public partial bool IsBusy { get; private set; }

    public bool CanCheck => !IsBusy;
    public bool CanDownload => !IsBusy && IsInstalled && IsUpdateAvailable && !IsDownloaded;
    public bool CanCancel => IsBusy && activeCancellation is { IsCancellationRequested: false };
    public bool CanApplyAndRestart => !IsBusy && IsDownloaded;
    public bool CanDefer => !IsBusy && IsDownloaded;

    [ObservableProperty]
    public partial string? CurrentStage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercentage))]
    public partial int ProgressCompleted { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressPercentage))]
    public partial int ProgressTotal { get; private set; }

    public int ProgressPercentage => ProgressTotal <= 0
        ? 0
        : Math.Clamp((int)Math.Round(ProgressCompleted * 100d / ProgressTotal, MidpointRounding.AwayFromZero), 0, 100);

    [ObservableProperty]
    public partial string? ProgressMessage { get; private set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    [ObservableProperty]
    public partial string? LastError { get; private set; }

    [RelayCommand(CanExecute = nameof(CanCheck), AllowConcurrentExecutions = false)]
    public Task CheckAsync(CancellationToken cancellationToken = default) =>
        RunAsync(CanCheck, "CheckingApplicationUpdates", "Checking for application updates.",
            "The application update check was cancelled.", async (progress, token) =>
            {
                var result = await appUpdateService.CheckAsync(progress, token);
                await Task.Yield();
                ApplyCheckResult(result);
            }, clearUpdateInfo: true, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanDownload), AllowConcurrentExecutions = false)]
    public Task DownloadAsync(CancellationToken cancellationToken = default) =>
        RunAsync(CanDownload, "DownloadingApplicationUpdate", "Downloading the application update.",
            "The application update download was cancelled.", async (progress, token) =>
            {
                var result = await appUpdateService.DownloadAsync(progress, token);
                await Task.Yield();
                ApplyDownloadResult(result);
            }, clearUpdateInfo: false, cancellationToken);

    [RelayCommand(CanExecute = nameof(CanApplyAndRestart), AllowConcurrentExecutions = false)]
    public Task ApplyAndRestartAsync(CancellationToken cancellationToken = default) =>
        RunAsync(CanApplyAndRestart, "ApplyingApplicationUpdate", "Preparing to restart and apply the application update.",
            "Restarting for the application update was cancelled.", async (progress, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (prepareForRestart is not null) { await prepareForRestart(); }
                token.ThrowIfCancellationRequested();
                await appUpdateService.ApplyAndRestartAsync(progress, token);
                await Task.Yield();
                CurrentStage = "RestartRequested";
                StatusMessage = "The application update is ready and restart was requested.";
                LastError = null;
            }, clearUpdateInfo: false, cancellationToken);

    private async Task RunAsync(
        bool canStart, string stage, string status, string cancelledMessage,
        Func<IProgress<OperationProgress>, CancellationToken, Task> action,
        bool clearUpdateInfo, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (!canStart || IsBusy) { return; }
        using var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        activeCancellation = source;
        if (clearUpdateInfo) { UpdateInfo = null; HasChecked = false; }
        IsBusy = true;
        ResetOperationState(stage, status);
        var progress = new Progress<OperationProgress>(ApplyProgress);
        try
        {
            await action(progress, source.Token);
        }
        catch (OperationCanceledException exception) when (!OperationalFailure.IsTimeout(exception, source.Token))
        {
            await Task.Yield();
            SetCancelled(cancelledMessage);
        }
        catch (Exception exception) when (OperationalFailure.IsRecoverable(exception, source.Token))
        {
            await Task.Yield();
            SetFailure(exception.Message);
        }
        finally
        {
            activeCancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel()
    {
        ThrowIfDisposed();
        if (activeCancellation is not { IsCancellationRequested: false }) { return; }
        activeCancellation.Cancel();
        StatusMessage = "Cancellation requested.";
        OnPropertyChanged(nameof(CanCancel));
        CancelCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanDefer))]
    public void Defer()
    {
        ThrowIfDisposed();
        if (!CanDefer) { return; }
        CurrentStage = "Deferred";
        StatusMessage = "The downloaded application update will be applied later.";
        LastError = null;
    }

    public void Dispose()
    {
        if (disposed) { return; }
        disposed = true;
        activeCancellation?.Cancel();
        activeCancellation?.Dispose();
        activeCancellation = null;
        GC.SuppressFinalize(this);
    }

    private void ApplyCheckResult(AppUpdateInfo result)
    {
        UpdateInfo = result;
        HasChecked = true;
        CurrentStage = "CheckCompleted";
        StatusMessage = !result.IsInstalled
            ? "Application updates are available only in an installed release build."
            : result.IsUpdateAvailable
                ? $"Application update {result.LatestVersion ?? string.Empty} is available."
                : "The application is up to date.";
        LastError = null;
    }

    private void ApplyDownloadResult(AppUpdateInfo result)
    {
        UpdateInfo = result;
        if (!result.IsDownloaded)
        {
            SetFailure("The update service completed without downloading the application update.");
            return;
        }

        CurrentStage = "DownloadCompleted";
        StatusMessage = "The application update was downloaded. Restart now or apply it later.";
        LastError = null;
    }

    private void ApplyProgress(OperationProgress progress)
    {
        CurrentStage = progress.Stage;
        ProgressCompleted = progress.Completed;
        ProgressTotal = progress.Total;
        ProgressMessage = progress.Message;
    }

    private void SetCancelled(string message)
    {
        CurrentStage = "Cancelled";
        StatusMessage = message;
        LastError = null;
    }

    private void SetFailure(string error)
    {
        HasChecked = HasChecked || UpdateInfo is null;
        CurrentStage = "Failed";
        StatusMessage = error;
        LastError = error;
    }

    private void ResetOperationState(string stage, string status)
    {
        CurrentStage = stage;
        ProgressCompleted = 0;
        ProgressTotal = 0;
        ProgressMessage = null;
        StatusMessage = status;
        LastError = null;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);
}
