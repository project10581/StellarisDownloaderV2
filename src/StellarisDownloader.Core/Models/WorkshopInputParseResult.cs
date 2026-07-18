namespace StellarisDownloader.Core.Models;

public sealed record WorkshopInputParseResult(
    IReadOnlyList<string> WorkshopIds,
    IReadOnlyList<InvalidWorkshopInput> InvalidInputs);
