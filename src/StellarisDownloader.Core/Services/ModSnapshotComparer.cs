using StellarisDownloader.Core.Models;
using StellarisDownloader.Core.Persistence;

namespace StellarisDownloader.Core.Services;

public static class ModSnapshotComparer
{
    public static ModSnapshotComparison Compare(ModSnapshot baseline, ModSnapshot current)
    {
        ModSnapshotFile.Validate(baseline);
        ModSnapshotFile.Validate(current);
        var before = baseline.Mods.ToDictionary(mod => mod.WorkshopId, StringComparer.Ordinal);
        var after = current.Mods.ToDictionary(mod => mod.WorkshopId, StringComparer.Ordinal);
        return new ModSnapshotComparison(
            baseline,
            current,
            Sort(current.Mods.Where(mod => !before.ContainsKey(mod.WorkshopId))),
            Sort(baseline.Mods.Where(mod => !after.ContainsKey(mod.WorkshopId))),
            Sort(current.Mods.Where(mod => before.ContainsKey(mod.WorkshopId)).Select(mod => mod with
            {
                Title = string.IsNullOrWhiteSpace(mod.Title) ? before[mod.WorkshopId].Title : mod.Title,
            })));
    }

    private static ModSnapshotEntry[] Sort(IEnumerable<ModSnapshotEntry> mods) =>
        mods.OrderBy(mod => mod.WorkshopId, StringComparer.Ordinal).ToArray();
}
