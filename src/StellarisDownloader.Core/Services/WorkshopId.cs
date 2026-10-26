using System.Diagnostics.CodeAnalysis;

namespace StellarisDownloader.Core.Services;

internal static class WorkshopId
{
    public static bool IsValid([NotNullWhen(true)] string? value) =>
        !string.IsNullOrEmpty(value) && value.All(char.IsAsciiDigit);
}
