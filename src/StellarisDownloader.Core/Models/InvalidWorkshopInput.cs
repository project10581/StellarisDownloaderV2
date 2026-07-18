namespace StellarisDownloader.Core.Models;

public sealed record InvalidWorkshopInput(
    int LineNumber,
    string Input,
    string Error);
