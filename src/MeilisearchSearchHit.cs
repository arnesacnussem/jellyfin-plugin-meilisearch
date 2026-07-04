using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Meilisearch;

/// <summary>
/// Minimal Meilisearch hit projection used during search (guid + ranking score only).
/// </summary>
internal sealed record MeilisearchSearchHit
{
    [JsonPropertyName("guid")]
    public string? Guid { get; init; }

    [JsonPropertyName("_rankingScore")]
    public double RankingScore { get; init; }
}
