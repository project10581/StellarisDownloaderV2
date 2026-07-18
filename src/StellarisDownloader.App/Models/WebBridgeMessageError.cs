namespace StellarisDownloader.App.Models;

public enum WebBridgeMessageError
{
    None,
    UntrustedSource,
    InvalidJson,
    RootMustBeObject,
    UnknownProperty,
    DuplicateProperty,
    MissingProperty,
    InvalidMessageType,
    IdsMustBeArray,
    InvalidIdCount,
    InvalidWorkshopId,
}
