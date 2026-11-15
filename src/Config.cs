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
        SearchPeopleByNameOnly = false;
        SearchTimeoutSeconds = 5;
    }

    public string ApiKey { get; set; }
    public string Url { get; set; }

    public bool Debug { get; set; }
    public string IndexName { get; set; }

    /// <summary>
    /// Meilisearch matchingStrategy: "last", "all", or "frequency".
    /// </summary>
    public string MatchingStrategy { get; set; }

    /// <summary>
    /// Whether a search for people alone matches only their names, not their biographies.
    /// </summary>
    public bool SearchPeopleByNameOnly { get; set; }

    /// <summary>
    /// Seconds a search may take before it counts as failed, so Jellyfin can answer from its own database instead.
    /// 1 to 60. Other values use the default of 5.
    /// </summary>
    public int SearchTimeoutSeconds { get; set; }

    /// <summary>
    /// Number of items read from the database by the last full reindex. Not shown on the config page.
    /// </summary>
    public int LastIndexedItemCount { get; set; }
}
