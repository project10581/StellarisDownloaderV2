namespace StellarisDownloader.Core.Services;

public sealed class StellarisOperationException : Exception
{
    public StellarisOperationException() { }

    public StellarisOperationException(string message) : base(message) { }

    public StellarisOperationException(string message, Exception innerException) : base(message, innerException) { }
}
