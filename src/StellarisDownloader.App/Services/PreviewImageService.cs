using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StellarisDownloader.App.Services;

public sealed class PreviewImageService : IPreviewImageService
{
    private const int MaximumImageBytes = 20 * 1024 * 1024;
    private readonly HttpClient httpClient;
    private readonly ConcurrentDictionary<string, Task<ImageSource?>> cache =
        new(StringComparer.OrdinalIgnoreCase);

    public PreviewImageService(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
    }

    public async Task<ImageSource?> LoadAsync(
        string? previewUrl,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(previewUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        var task = cache.GetOrAdd(uri.AbsoluteUri, _ => LoadCoreAsync(uri));
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsImageLoadFailure(exception))
        {
            cache.TryRemove(uri.AbsoluteUri, out _);
            return null;
        }
    }

    private async Task<ImageSource?> LoadCoreAsync(Uri uri)
    {
        using var response = await httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            CancellationToken.None).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumImageBytes)
        {
            throw new InvalidDataException("The preview image is larger than 20 MB.");
        }

        await using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await source.ReadAsync(chunk).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > MaximumImageBytes)
            {
                throw new InvalidDataException("The preview image is larger than 20 MB.");
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read)).ConfigureAwait(false);
        }

        buffer.Position = 0;
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = buffer;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static bool IsImageLoadFailure(Exception exception) =>
        exception is HttpRequestException
            or IOException
            or InvalidDataException
            or NotSupportedException;
}
