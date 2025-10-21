using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Meilisearch;

public class Config : BasePluginConfiguration
{
    public static readonly string[] DefaultAttributesToSearchOn =
    [
        "name", "artists", "albumArtists", "originalTitle", "productionYear", "seriesName", "genres", "tags",
        "studios", "overview", "path"
    ];

    public Config()
    {
        ApiKey = string.Empty;
        Url = string.Empty;
        Debug = false;
        IndexName = string.Empty;
        AttributesToSearchOn = DefaultAttributesToSearchOn;
        FallbackToJellyfin = false;
    }

    public string ApiKey { get; set; }
    public string Url { get; set; }

    public bool Debug { get; set; }
    public string IndexName { get; set; }
    public string[] AttributesToSearchOn { get; set; }
    public bool FallbackToJellyfin { get; set; }
}