namespace StellarisDownloader.Core.Models;

public sealed record RedownloadResult(
    DeleteResult DeleteResult,
    DownloadResult? DownloadResult);
