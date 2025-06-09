namespace Jellyfin.Plugin.Meilisearch;

public record MeilisearchItem(
    string Guid,
    string? Type,
    string? ParentId,
    string? Name,
    string? Overview,
    string? OriginalTitle,
    string? SeriesName,
    int? ProductionYear,
    string[]? Artists,
    string[]? AlbumArtists,
    string[]? Genres,
    string[]? Studios,
    string[]? Tags,
    bool? IsFolder,
    float? CommunityRating,
    float? CriticRating,
    string? Path
);