using System.Text.Json.Serialization;

namespace StellarisDownloader.Core.Models;

public sealed record WorkshopFavorite
{
    public required string WorkshopId { get; init; }
    public string? Title { get; init; }
    public required DateTimeOffset SavedAtUtc { get; init; }
    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? WorkshopId : Title;
}
