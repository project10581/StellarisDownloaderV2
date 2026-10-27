using System.Globalization;
using System.IO;
using System.Security;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;
using StellarisDownloader.Core.Services;

namespace StellarisDownloader.App.ViewModels;

public sealed partial class ModSnapshotsViewModel(
    IModSnapshotService snapshotService,
    ModSnapshotHistory history,
    Func<string, string> text) : ObservableObject
{
    private CancellationTokenSource? operationCancellation;
    private bool hasLoadedHistory;
    private ModSnapshot? liveSnapshot;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSourceLabel))]
    public partial bool IsFileComparison { get; private set; }

    public string CurrentSourceLabel => IsFileComparison ? text("Snapshots.FileComparison") : text("Snapshots.LiveLibrary");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanExportReport), nameof(CanCopyMissing))]
    [NotifyCanExecuteChangedFor(nameof(LoadHistoryCommand), nameof(SaveVersionCommand), nameof(SelectBaselineCommand),
        nameof(ExportReportCommand), nameof(CancelCommand))]
    public partial bool IsBusy { get; private set; }

    public bool IsIdle => !IsBusy;
    public bool CanExportReport => IsIdle && Comparison is not null;
    public bool CanCopyMissing => CanExportReport && Missing.Count > 0;
    public string HistoryDirectory => history.DirectoryPath;
    public string DefaultExportDirectory => history.ExportsDirectory;

    [ObservableProperty]
    public partial string HistoryWarning { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<SnapshotSourceViewModel> HistoryVersions { get; private set; } = [];

    [ObservableProperty]
    public partial SnapshotSourceViewModel? SelectedBaselineVersion { get; set; }

    partial void OnSelectedBaselineVersionChanged(SnapshotSourceViewModel? oldValue, SnapshotSourceViewModel? newValue)
    {
        if (oldValue?.FilePath != newValue?.FilePath) { ClearResults(); }
    }

    [ObservableProperty]
    public partial string? LastError { get; private set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    [ObservableProperty]
    public partial string BaselineSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentSummary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string Warnings { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExportReport), nameof(CanCopyMissing))]
    [NotifyCanExecuteChangedFor(nameof(ExportReportCommand))]
    public partial ModSnapshotComparison? Comparison { get; private set; }

    partial void OnComparisonChanged(ModSnapshotComparison? value)
    {
        Added = ToItems(value?.Added);
        Missing = ToItems(value?.Missing);
        Retained = ToItems(value?.Retained);
        BaselineItems = BuildSide(value?.Baseline.Mods, value?.Missing, ModSnapshotChange.Missing);
        CurrentItems = BuildSide(value?.Current.Mods, value?.Added, ModSnapshotChange.Added);
    }

    [ObservableProperty]
    public partial IReadOnlyList<ModSnapshotItemViewModel> Added { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopyMissing))]
    public partial IReadOnlyList<ModSnapshotItemViewModel> Missing { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ModSnapshotItemViewModel> Retained { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ModSnapshotItemViewModel> BaselineItems { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ModSnapshotItemViewModel> CurrentItems { get; private set; } = [];

    [RelayCommand(CanExecute = nameof(IsIdle), AllowConcurrentExecutions = false)]
    public Task LoadHistoryAsync() => RunAsync(async token =>
    {
        ClearResults();
        liveSnapshot = null;
        await RefreshHistoryAsync(token);
        liveSnapshot = await snapshotService.CaptureAsync(token);
        await ShowLiveComparisonAsync(token);
        hasLoadedHistory = true;
    });

    public Task InitializeAsync() => hasLoadedHistory ? Task.CompletedTask : LoadHistoryAsync();

    [RelayCommand(CanExecute = nameof(IsIdle), AllowConcurrentExecutions = false)]
    public Task SaveVersionAsync() => RunAsync(async token =>
    {
        var snapshot = await snapshotService.CaptureAsync(token);
        var path = await history.SaveAsync(snapshot, token);
        // The version is committed. Finish refreshing even if cancellation arrives afterwards.
        await RefreshHistoryAsync(CancellationToken.None);
        liveSnapshot = snapshot;
        await ShowLiveComparisonAsync(CancellationToken.None);
        StatusMessage = Format("Snapshots.VersionSaved", path);
    });

    [RelayCommand(CanExecute = nameof(IsIdle), AllowConcurrentExecutions = false)]
    public Task SelectBaselineAsync(SnapshotSourceViewModel? baseline) => RunAsync(async token =>
    {
        SelectedBaselineVersion = baseline;
        liveSnapshot ??= await snapshotService.CaptureAsync(token);
        await ShowLiveComparisonAsync(token);
    });

    private async Task ShowLiveComparisonAsync(CancellationToken token)
    {
        IsFileComparison = false;
        ClearResults();
        if (liveSnapshot is null)
        {
            return;
        }

        CurrentItems = BuildSide(liveSnapshot.Mods, null, ModSnapshotChange.Unchanged);
        CurrentSummary = Describe(liveSnapshot);
        Warnings = DescribeWarnings(liveSnapshot);
        StatusMessage = text("Snapshots.FirstSaveHint");
        if (SelectedBaselineVersion?.FilePath is { } path)
        {
            var baseline = await ReadSnapshotAsync(path, token);
            ShowComparison(baseline, liveSnapshot);
        }
    }

    public Task ExportCurrentAsync(string path, bool asText) => RunAsync(async token =>
    {
        RejectManagedVersionOverwrite(path);
        var snapshot = await snapshotService.CaptureAsync(token);
        if (asText)
        {
            await ModSnapshotFile.WriteTextAsync(path, BuildSnapshotText(snapshot), token);
        }
        else
        {
            await ModSnapshotFile.WriteAsync(path, snapshot, token);
        }

        liveSnapshot = snapshot;
        await ShowLiveComparisonAsync(token);
        StatusMessage = Format("Snapshots.Saved", path);
    });

    public Task CompareCurrentAsync(string baselinePath) => RunAsync(async token =>
    {
        SelectedBaselineVersion = HistoryVersions.FirstOrDefault(item => item.FilePath == baselinePath);
        ClearResults();
        var baseline = await ReadSnapshotAsync(baselinePath, token);
        var current = await snapshotService.CaptureAsync(token);
        liveSnapshot = current;
        IsFileComparison = false;
        ShowComparison(baseline, current);
    });

    public Task CompareFilesAsync(string baselinePath, string currentPath) => RunAsync(async token =>
    {
        SelectedBaselineVersion = HistoryVersions.FirstOrDefault(item => item.FilePath == baselinePath);
        ClearResults();
        IsFileComparison = true;
        var baseline = await ReadSnapshotAsync(baselinePath, token);
        var current = await ReadSnapshotAsync(currentPath, token);
        ShowComparison(baseline, current);
    });

    [RelayCommand(CanExecute = nameof(CanExportReport), AllowConcurrentExecutions = false)]
    public Task ExportReportAsync(string path) => RunAsync(async token =>
    {
        RejectManagedVersionOverwrite(path);
        if (Comparison is null)
        {
            return;
        }

        var report = new StringBuilder();
        report.AppendLine(text("Snapshots.Title"));
        report.AppendLine(text("Snapshots.Baseline") + ": " + Describe(Comparison.Baseline));
        report.AppendLine(text("Snapshots.Current") + ": " + Describe(Comparison.Current));
        report.AppendLine(text("Snapshots.Note"));
        report.AppendLine(Warnings);
        AppendExcludedDirectories(report, Comparison.Baseline, text("Snapshots.Baseline"));
        AppendExcludedDirectories(report, Comparison.Current, text("Snapshots.Current"));
        AppendMods(report, text("Snapshots.Added"), Comparison.Added);
        AppendMods(report, text("Snapshots.Missing"), Comparison.Missing);
        AppendMods(report, text("Snapshots.Retained"), Comparison.Retained);
        await ModSnapshotFile.WriteTextAsync(path, report.ToString(), token);
        StatusMessage = Format("Snapshots.Saved", path);
    });

    public string GetMissingIds() => string.Join(Environment.NewLine, Missing.Select(mod => mod.WorkshopId));

    [RelayCommand(CanExecute = nameof(IsBusy))]
    public void Cancel() => operationCancellation?.Cancel();

    private async Task RefreshHistoryAsync(CancellationToken cancellationToken)
    {
        var previousBaseline = SelectedBaselineVersion?.FilePath;
        var result = await history.ListAsync(cancellationToken);
        HistoryVersions = result.Entries.Select(entry =>
        {
            var versionName = Path.GetFileNameWithoutExtension(entry.FilePath);
            var shortVersion = versionName[Math.Max(0, versionName.Length - 8)..];
            return new SnapshotSourceViewModel(entry.FilePath,
                Format("Snapshots.VersionLabel", entry.CapturedAtUtc.ToLocalTime(), entry.ModCount, shortVersion),
                entry.SourceLibrary);
        }).ToArray();
        SelectedBaselineVersion = HistoryVersions.FirstOrDefault(item => item.FilePath == previousBaseline)
            ?? (HistoryVersions.Count > 0 ? HistoryVersions[0] : null);
        HistoryWarning = result.UnreadableCount == 0 ? string.Empty : Format("Snapshots.UnreadableHistory", result.UnreadableCount);
    }

    private void RejectManagedVersionOverwrite(string path)
    {
        if (history.IsManagedVersionPath(path))
        {
            throw new StellarisOperationException(text("Snapshots.ProtectedHistory"));
        }
    }

    private static async Task<ModSnapshot> ReadSnapshotAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await ModSnapshotFile.ReadAsync(path, cancellationToken);
        }
        catch (InvalidDataException exception)
        {
            throw new StellarisOperationException("该快照文件已损坏", exception);
        }
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (IsBusy)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        operationCancellation = cancellation;
        IsBusy = true;
        LastError = null;
        StatusMessage = text("Snapshots.Working");
        try
        {
            await operation(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = text("Snapshots.Cancelled");
        }
        catch (Exception exception) when (OperationalFailure.IsRecoverable(exception, cancellation.Token))
        {
            LastError = exception.Message;
            StatusMessage = text("Snapshots.Error");
        }
        finally
        {
            operationCancellation = null;
            IsBusy = false;
        }
    }

    private void ClearResults()
    {
        if (Comparison is null) { OnComparisonChanged(null); }
        else { Comparison = null; }
        BaselineSummary = string.Empty;
        CurrentSummary = string.Empty;
        Warnings = string.Empty;
    }

    private void ShowComparison(ModSnapshot baseline, ModSnapshot current)
    {
        Comparison = ModSnapshotComparer.Compare(baseline, current);
        BaselineSummary = Describe(baseline);
        CurrentSummary = Describe(current);
        Warnings = text("Snapshots.Baseline") + ": " + DescribeWarnings(baseline)
            + Environment.NewLine + text("Snapshots.Current") + ": " + DescribeWarnings(current);
        StatusMessage = Format("Snapshots.Compared", Added.Count, Missing.Count, Retained.Count);
    }

    private string Describe(ModSnapshot snapshot) => Format(
        "Snapshots.Summary", SingleLine(snapshot.SourceLibrary), snapshot.CapturedAtUtc.ToLocalTime(), snapshot.Mods.Count);

    private string DescribeWarnings(ModSnapshot snapshot) => Format(
        "Snapshots.Excluded", snapshot.EmptyWorkshopIds.Count, snapshot.LinkedWorkshopIds.Count,
        snapshot.Mods.Count(mod => string.IsNullOrWhiteSpace(mod.Title)))
        + (snapshot.MetadataUnavailable ? " " + text("Snapshots.MetadataUnavailable") : string.Empty);

    private string BuildSnapshotText(ModSnapshot snapshot)
    {
        var result = new StringBuilder();
        result.AppendLine(text("Snapshots.Title"));
        result.AppendLine(Describe(snapshot));
        result.AppendLine(text("Snapshots.Note"));
        result.AppendLine(DescribeWarnings(snapshot));
        AppendExcludedDirectories(result, snapshot, text("Snapshots.Current"));
        AppendMods(result, text("Snapshots.Mods"), snapshot.Mods);
        return result.ToString();
    }

    private void AppendExcludedDirectories(StringBuilder result, ModSnapshot snapshot, string source)
    {
        if (snapshot.EmptyWorkshopIds.Count > 0)
        {
            result.AppendLine(source + " / " + text("Snapshots.EmptyDirectories") + ": "
                + string.Join(", ", snapshot.EmptyWorkshopIds));
        }

        if (snapshot.LinkedWorkshopIds.Count > 0)
        {
            result.AppendLine(source + " / " + text("Snapshots.LinkedDirectories") + ": "
                + string.Join(", ", snapshot.LinkedWorkshopIds));
        }
    }

    private static void AppendMods(StringBuilder result, string label, IReadOnlyList<ModSnapshotEntry> mods)
    {
        result.AppendLine();
        result.AppendLine(CultureInfo.CurrentCulture, $"{label} ({mods.Count})");
        foreach (var mod in mods.OrderBy(mod => mod.WorkshopId, StringComparer.Ordinal))
        {
            var item = new ModSnapshotItemViewModel(mod);
            result.AppendLine(SingleLine(item.DisplayTitle) + " | " + item.WorkshopId + " | " + item.WorkshopUrl);
        }
    }

    private string Format(string key, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, text(key), values);

    private static string SingleLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');

    private static ModSnapshotItemViewModel[] ToItems(IReadOnlyList<ModSnapshotEntry>? entries) =>
        entries?.Select(entry => new ModSnapshotItemViewModel(entry)).ToArray() ?? [];

    private static ModSnapshotItemViewModel[] BuildSide(
        IReadOnlyList<ModSnapshotEntry>? entries, IReadOnlyList<ModSnapshotEntry>? changed, ModSnapshotChange change)
    {
        var changedIds = changed?.Select(entry => entry.WorkshopId).ToHashSet(StringComparer.Ordinal) ?? [];
        return entries?.Select(entry => new ModSnapshotItemViewModel(
                entry, changedIds.Contains(entry.WorkshopId) ? change : ModSnapshotChange.Unchanged))
            .OrderByDescending(item => item.IsChanged)
            .ThenBy(item => item.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.WorkshopId, StringComparer.Ordinal).ToArray() ?? [];
    }
}

public sealed class ModSnapshotItemViewModel(ModSnapshotEntry entry, ModSnapshotChange change = ModSnapshotChange.Unchanged)
{
    public string WorkshopId => entry.WorkshopId;

    public string DisplayTitle => string.IsNullOrWhiteSpace(entry.Title) ? entry.WorkshopId : entry.Title;

    public string WorkshopUrl => $"https://steamcommunity.com/sharedfiles/filedetails/?id={WorkshopId}";

    public bool IsMissing => change == ModSnapshotChange.Missing;

    public bool IsAdded => change == ModSnapshotChange.Added;

    public bool IsChanged => change != ModSnapshotChange.Unchanged;
}

public enum ModSnapshotChange { Unchanged, Added, Missing }

public sealed record SnapshotSourceViewModel(string? FilePath, string DisplayName, string SourceLibrary);
