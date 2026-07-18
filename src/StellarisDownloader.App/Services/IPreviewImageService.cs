using System.Windows.Media;

namespace StellarisDownloader.App.Services;

public interface IPreviewImageService
{
    Task<ImageSource?> LoadAsync(
        string? previewUrl,
        CancellationToken cancellationToken = default);
}
