using StellarisDownloader.Core.Models;

namespace StellarisDownloader.App.ViewModels;

public sealed class ModListItemViewModel
{
    public ModListItemViewModel(ModRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;
    }

    public ModRecord Record { get; }

    public string WorkshopId => Record.WorkshopId;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Record.Title) ? "—" : Record.Title;

    public DateTimeOffset? RemoteUpdatedAtUtc => Record.InstalledWorkshopUpdatedAtUtc;

    public DateTimeOffset LastDownloadedAtUtc => Record.ImportedOrDownloadedAtUtc;

    public long FileSize => Record.FileSize ?? 0;

    public string WorkshopUrl =>
        $"https://steamcommunity.com/sharedfiles/filedetails/?id={Record.WorkshopId}";
}
