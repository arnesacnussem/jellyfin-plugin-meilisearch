using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Meilisearch;

public class Config : BasePluginConfiguration
{
    public static readonly string[] DefaultAttributesToSearchOn =
    [
        "name", "artists", "albumArtists", "originalTitle", "productionYear", "seriesName", "genres", "tags",
        "studios", "overview", "path", "tagline"
    ];

    public static readonly string[] DefaultIncludedItemTypes =
    [
        "Movie", "Series", "Season", "Episode", "Person", "Genre", "Studio", "BoxSet", "Playlist", "Year"
    ];

    public Config()
    {
        ApiKey = string.Empty;
        Url = string.Empty;
        Debug = false;
        IndexName = string.Empty;
        AttributesToSearchOn = DefaultAttributesToSearchOn;
        IncludedItemTypes = DefaultIncludedItemTypes;
        FallbackToJellyfin = false;
    }

    public string ApiKey { get; set; }
    public string Url { get; set; }

    public bool Debug { get; set; }
    public string IndexName { get; set; }
    public string[] AttributesToSearchOn { get; set; }
    public string[] IncludedItemTypes { get; set; }
    public bool FallbackToJellyfin { get; set; }
}
