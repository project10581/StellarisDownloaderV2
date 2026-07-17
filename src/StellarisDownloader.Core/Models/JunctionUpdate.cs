namespace StellarisDownloader.Core.Models;

public sealed record JunctionUpdate(
    JunctionState PreviousState,
    string TargetPath,
    bool Changed);
