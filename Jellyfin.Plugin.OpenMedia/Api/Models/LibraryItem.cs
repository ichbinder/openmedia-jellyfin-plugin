using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.OpenMedia.Api.Models;

/// <summary>
/// Repräsentiert einen Film aus der openmedia-UserLibrary, geliefert von GET /jellyfin/library.
/// </summary>
public record LibraryItem(
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("tmdbId")] int TmdbId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("fileSize")] string FileSize,
    [property: JsonPropertyName("duration")] int? Duration,
    [property: JsonPropertyName("resolution")] string? Resolution);

/// <summary>Wrapper für die /jellyfin/library Response { items: [...] }.</summary>
internal sealed record LibraryResponse(
    [property: JsonPropertyName("items")] LibraryItem[] Items);
