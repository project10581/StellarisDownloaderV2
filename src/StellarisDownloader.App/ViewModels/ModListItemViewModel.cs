using System.Globalization;
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

    public string DisplayAuthor => !string.IsNullOrWhiteSpace(Record.CreatorName)
        ? Record.CreatorName
        : !string.IsNullOrWhiteSpace(Record.CreatorId)
            ? Record.CreatorId
            : "—";

    public string DisplayFileSize => FormatFileSize(Record.FileSize);

    public DateTimeOffset? RemoteUpdatedAtUtc => Record.InstalledWorkshopUpdatedAtUtc;

    public DateTimeOffset LastDownloadedAtUtc => Record.ImportedOrDownloadedAtUtc;

    public long FileSize => Record.FileSize ?? 0;

    public string WorkshopUrl =>
        $"https://steamcommunity.com/sharedfiles/filedetails/?id={Record.WorkshopId}";

    private static string FormatFileSize(long? value)
    {
        if (value is null || value < 0)
        {
            return "—";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        var scaledValue = (double)value.Value;
        var unitIndex = 0;
        while (scaledValue >= 1024 && unitIndex < units.Length - 1)
        {
            scaledValue /= 1024;
            unitIndex++;
        }

        return unitIndex == 0
            ? string.Format(CultureInfo.CurrentCulture, "{0:N0} {1}", scaledValue, units[unitIndex])
            : string.Format(CultureInfo.CurrentCulture, "{0:0.##} {1}", scaledValue, units[unitIndex]);
    }
}
