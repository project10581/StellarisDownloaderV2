namespace StellarisDownloader.Core.Models;

public sealed record ProcessOutputLine(long Sequence, string Source, string Text);
