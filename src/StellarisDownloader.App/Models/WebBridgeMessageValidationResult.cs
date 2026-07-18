using System.Collections.ObjectModel;

namespace StellarisDownloader.App.Models;

public sealed class WebBridgeMessageValidationResult
{
    private static readonly IReadOnlyList<string> NoWorkshopIds =
        Array.AsReadOnly(Array.Empty<string>());

    private WebBridgeMessageValidationResult(
        WebBridgeMessageError error,
        IReadOnlyList<string> workshopIds)
    {
        Error = error;
        WorkshopIds = workshopIds;
    }

    public bool IsValid => Error == WebBridgeMessageError.None;

    public WebBridgeMessageError Error { get; }

    public IReadOnlyList<string> WorkshopIds { get; }

    internal static WebBridgeMessageValidationResult Accepted(List<string> workshopIds) =>
        new(WebBridgeMessageError.None, new ReadOnlyCollection<string>(workshopIds));

    internal static WebBridgeMessageValidationResult Rejected(WebBridgeMessageError error) =>
        new(error, NoWorkshopIds);
}
