using System.Collections.Concurrent;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App.ViewModels;

public sealed class ApplicationUpdateViewModel : ObservableObject, IDisposable
{
    private readonly IAppUpdateService appUpdateService;
    private readonly Func<Task>? prepareForRestart;
    private readonly SynchronizationContext uiContext;
    private readonly int uiThreadId;
    private readonly AsyncRelayCommand checkCommand;
    private readonly AsyncRelayCommand downloadCommand;
    private readonly AsyncRelayCommand applyAndRestartCommand;
    private readonly RelayCommand cancelCommand;
    private readonly RelayCommand deferCommand;
    private CancellationTokenSource? activeCancellation;
    private AppUpdateInfo? updateInfo;
    private bool hasChecked;
    private bool isBusy;
    private string? currentStage;
    private int progressCompleted;
    private int progressTotal;
    private string? progressMessage;
    private string? statusMessage;
    private string? lastError;
    private bool disposed;

    public ApplicationUpdateViewModel(
        IAppUpdateService appUpdateService,
        Func<Task>? prepareForRestart = null,
        SynchronizationContext? uiContext = null)
    {
        ArgumentNullException.ThrowIfNull(appUpdateService);

        this.appUpdateService = appUpdateService;
        this.prepareForRestart = prepareForRestart;
        this.uiContext = uiContext
            ?? SynchronizationContext.Current
            ?? new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher);
        uiThreadId = Environment.CurrentManagedThreadId;

        checkCommand = new AsyncRelayCommand(
            () => CheckAsync(CancellationToken.None),
            () => CanCheck);
        downloadCommand = new AsyncRelayCommand(
            () => DownloadAsync(CancellationToken.None),
            () => CanDownload);
        applyAndRestartCommand = new AsyncRelayCommand(
            () => ApplyAndRestartAsync(CancellationToken.None),
            () => CanApplyAndRestart);
        cancelCommand = new RelayCommand(Cancel, () => CanCancel);
        deferCommand = new RelayCommand(Defer, () => CanDefer);
    }

    public IAsyncRelayCommand CheckCommand => checkCommand;

    public IAsyncRelayCommand DownloadCommand => downloadCommand;

    public IAsyncRelayCommand ApplyAndRestartCommand => applyAndRestartCommand;

    public IRelayCommand CancelCommand => cancelCommand;

    public IRelayCommand DeferCommand => deferCommand;

    public AppUpdateInfo? UpdateInfo
    {
        get => updateInfo;
        private set
        {
            if (SetProperty(ref updateInfo, value))
            {
                OnPropertyChanged(nameof(CurrentVersion));
                OnPropertyChanged(nameof(LatestVersion));
                OnPropertyChanged(nameof(ReleaseNotes));
                OnPropertyChanged(nameof(IsInstalled));
                OnPropertyChanged(nameof(IsUpdateAvailable));
                OnPropertyChanged(nameof(IsDownloaded));
                NotifyCommandStates();
            }
        }
    }

    public string? CurrentVersion => UpdateInfo?.CurrentVersion;

    public string? LatestVersion => UpdateInfo?.LatestVersion;

    public string? ReleaseNotes => UpdateInfo?.ReleaseNotes;

    public bool IsInstalled => UpdateInfo?.IsInstalled == true;

    public bool IsUpdateAvailable => UpdateInfo?.IsUpdateAvailable == true;

    public bool IsDownloaded => UpdateInfo?.IsDownloaded == true;

    public bool HasChecked
    {
        get => hasChecked;
        private set => SetProperty(ref hasChecked, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                OnPropertyChanged(nameof(CanCheck));
                OnPropertyChanged(nameof(CanDownload));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanApplyAndRestart));
                OnPropertyChanged(nameof(CanDefer));
                NotifyCommandStates();
            }
        }
    }

    public bool CanCheck => !IsBusy;

    public bool CanDownload => !IsBusy
        && IsInstalled
        && IsUpdateAvailable
        && !IsDownloaded;

    public bool CanCancel => IsBusy
        && activeCancellation is not null
        && !activeCancellation.IsCancellationRequested;

    public bool CanApplyAndRestart => !IsBusy && IsDownloaded;

    public bool CanDefer => !IsBusy && IsDownloaded;

    public string? CurrentStage
    {
        get => currentStage;
        private set => SetProperty(ref currentStage, value);
    }

    public int ProgressCompleted
    {
        get => progressCompleted;
        private set
        {
            if (SetProperty(ref progressCompleted, value))
            {
                OnPropertyChanged(nameof(ProgressPercentage));
            }
        }
    }

    public int ProgressTotal
    {
        get => progressTotal;
        private set
        {
            if (SetProperty(ref progressTotal, value))
            {
                OnPropertyChanged(nameof(ProgressPercentage));
            }
        }
    }

    public int ProgressPercentage => ProgressTotal <= 0
        ? 0
        : Math.Clamp(
            (int)Math.Round(
                ProgressCompleted * 100d / ProgressTotal,
                MidpointRounding.AwayFromZero),
            0,
            100);

    public string? ProgressMessage
    {
        get => progressMessage;
        private set => SetProperty(ref progressMessage, value);
    }

    public string? StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    public string? LastError
    {
        get => lastError;
        private set => SetProperty(ref lastError, value);
    }

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var operation = await BeginOperationAsync(
            CanCheck,
            "CheckingApplicationUpdates",
            "Checking for application updates.",
            cancellationToken,
            clearUpdateInfo: true).ConfigureAwait(false);
        if (operation is null)
        {
            return;
        }

        var pendingProgress = new ConcurrentQueue<Task>();
        var progress = CreateProgress(pendingProgress);
        try
        {
            var result = await appUpdateService.CheckAsync(
                progress,
                operation.Token).ConfigureAwait(false);
            await DrainProgressAsync(pendingProgress).ConfigureAwait(false);
            await InvokeOnUiAsync(() => ApplyCheckResult(result)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await InvokeOnUiAsync(() => SetCancelled("The application update check was cancelled."))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await InvokeOnUiAsync(() => SetFailure(exception.Message)).ConfigureAwait(false);
        }
        finally
        {
            await DrainProgressAsync(pendingProgress).ConfigureAwait(false);
            await FinishOperationAsync(operation).ConfigureAwait(false);
        }
    }

    public async Task DownloadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var operation = await BeginOperationAsync(
            CanDownload,
            "DownloadingApplicationUpdate",
            "Downloading the application update.",
            cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return;
        }

        var pendingProgress = new ConcurrentQueue<Task>();
        var progress = CreateProgress(pendingProgress);
        try
        {
            var result = await appUpdateService.DownloadAsync(
                progress,
                operation.Token).ConfigureAwait(false);
            await DrainProgressAsync(pendingProgress).ConfigureAwait(false);
            await InvokeOnUiAsync(() => ApplyDownloadResult(result)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await InvokeOnUiAsync(() => SetCancelled("The application update download was cancelled."))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await InvokeOnUiAsync(() => SetFailure(exception.Message)).ConfigureAwait(false);
        }
        finally
        {
            await DrainProgressAsync(pendingProgress).ConfigureAwait(false);
            await FinishOperationAsync(operation).ConfigureAwait(false);
        }
    }

    public async Task ApplyAndRestartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var operation = await BeginOperationAsync(
            CanApplyAndRestart,
            "ApplyingApplicationUpdate",
            "Preparing to restart and apply the application update.",
            cancellationToken).ConfigureAwait(false);
        if (operation is null)
        {
            return;
        }

        var pendingProgress = new ConcurrentQueue<Task>();
        var progress = CreateProgress(pendingProgress);
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            if (prepareForRestart is not null)
            {
                var preparation = await InvokeOnUiAsync(prepareForRestart).ConfigureAwait(false);
                await preparation.ConfigureAwait(false);
            }

            operation.Token.ThrowIfCancellationRequested();
            await appUpdateService.ApplyAndRestartAsync(
                progress,
                operation.Token).ConfigureAwait(false);
            await DrainProgressAsync(pendingProgress).ConfigureAwait(false);
            await InvokeOnUiAsync(() =>
            {
                CurrentStage = "RestartRequested";
                StatusMessage = "The application update is ready and restart was requested.";
                LastError = null;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await InvokeOnUiAsync(() => SetCancelled("Restarting for the application update was cancelled."))
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await InvokeOnUiAsync(() => SetFailure(exception.Message)).ConfigureAwait(false);
        }
        finally
        {
            await DrainProgressAsync(pendingProgress).ConfigureAwait(false);
            await FinishOperationAsync(operation).ConfigureAwait(false);
        }
    }

    public void Cancel()
    {
        ThrowIfDisposed();
        var cancellation = activeCancellation;
        if (cancellation is null || cancellation.IsCancellationRequested)
        {
            return;
        }

        cancellation.Cancel();
        _ = InvokeOnUiAsync(() =>
        {
            StatusMessage = "Cancellation requested.";
            cancelCommand.NotifyCanExecuteChanged();
        });
    }

    public void Defer()
    {
        ThrowIfDisposed();
        VerifyUiThread();
        if (!CanDefer)
        {
            return;
        }

        CurrentStage = "Deferred";
        StatusMessage = "The downloaded application update will be applied later.";
        LastError = null;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        activeCancellation?.Cancel();
        activeCancellation?.Dispose();
        activeCancellation = null;
        GC.SuppressFinalize(this);
    }

    private async Task<UpdateOperation?> BeginOperationAsync(
        bool canStart,
        string stage,
        string status,
        CancellationToken cancellationToken,
        bool clearUpdateInfo = false)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var started = await InvokeOnUiAsync(() =>
        {
            if (!canStart || IsBusy)
            {
                return false;
            }

            activeCancellation = source;
            if (clearUpdateInfo)
            {
                UpdateInfo = null;
                HasChecked = false;
            }

            IsBusy = true;
            ResetOperationState(stage, status);
            return true;
        }).ConfigureAwait(false);
        if (!started)
        {
            source.Dispose();
            return null;
        }

        return new UpdateOperation(source);
    }

    private async Task FinishOperationAsync(UpdateOperation operation)
    {
        await InvokeOnUiAsync(() =>
        {
            if (ReferenceEquals(activeCancellation, operation.Source))
            {
                activeCancellation = null;
            }

            IsBusy = false;
        }).ConfigureAwait(false);
        operation.Dispose();
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

    private CallbackProgress<OperationProgress> CreateProgress(
        ConcurrentQueue<Task> pendingProgress) =>
        new(value => pendingProgress.Enqueue(InvokeOnUiAsync(() => ApplyProgress(value))));

    private void NotifyCommandStates()
    {
        checkCommand.NotifyCanExecuteChanged();
        downloadCommand.NotifyCanExecuteChanged();
        applyAndRestartCommand.NotifyCanExecuteChanged();
        cancelCommand.NotifyCanExecuteChanged();
        deferCommand.NotifyCanExecuteChanged();
    }

    private Task<bool> InvokeOnUiAsync(Action action) =>
        InvokeOnUiAsync(() =>
        {
            action();
            return true;
        });

    private Task<T> InvokeOnUiAsync<T>(Func<T> action)
    {
        if (Environment.CurrentManagedThreadId == uiThreadId)
        {
            return Task.FromResult(action());
        }

        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        uiContext.Post(
            _ =>
            {
                try
                {
                    completion.SetResult(action());
                }
                catch (Exception exception)
                {
                    completion.SetException(exception);
                }
            },
            null);
        return completion.Task;
    }

    private static async Task DrainProgressAsync(ConcurrentQueue<Task> pendingProgress)
    {
        while (!pendingProgress.IsEmpty)
        {
            var pending = new List<Task>();
            while (pendingProgress.TryDequeue(out var update))
            {
                pending.Add(update);
            }

            if (pending.Count > 0)
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
            }
        }
    }

    private void VerifyUiThread()
    {
        if (Environment.CurrentManagedThreadId != uiThreadId)
        {
            throw new InvalidOperationException("This operation must run on the UI thread.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class UpdateOperation(CancellationTokenSource source) : IDisposable
    {
        public CancellationTokenSource Source { get; } = source;

        public CancellationToken Token => Source.Token;

        public void Dispose() => Source.Dispose();
    }
}
