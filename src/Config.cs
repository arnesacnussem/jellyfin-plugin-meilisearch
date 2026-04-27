using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Meilisearch;

public class Config : BasePluginConfiguration
{
    public Config()
    {
        ApiKey = string.Empty;
        Url = string.Empty;
        Debug = false;
        IndexName = string.Empty;
        MatchingStrategy = "last";
    }

    public string ApiKey { get; set; }
    public string Url { get; set; }

    public bool Debug { get; set; }
    public string IndexName { get; set; }

    /// <summary>
    /// Meilisearch matchingStrategy: "last", "all", or "frequency".
    /// </summary>
    public string MatchingStrategy { get; set; }
}
