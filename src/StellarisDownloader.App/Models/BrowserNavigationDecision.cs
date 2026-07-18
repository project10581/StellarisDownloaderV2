namespace StellarisDownloader.App.Models;

public sealed record BrowserNavigationDecision(
    BrowserNavigationDisposition Disposition,
    Uri? Target);
