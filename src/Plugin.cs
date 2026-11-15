using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

// ReSharper disable once ClassNeverInstantiated.Global
public class Plugin : BasePlugin<Config>, IHasWebPages
{
    private readonly MeilisearchClientHolder _clientHolder;
    private readonly ILogger<Plugin> _logger;
    public readonly Indexer Indexer;

    private readonly object _avgLock = new();
    private double _averageSearchTime;
    private bool _hasSearchTime;

    /// <summary>
    /// Exponential moving average of recent search durations, in milliseconds.
    /// The most recent sample is weighted at 50%; this is a smoothed figure, not a mean over all searches.
    /// </summary>
    public long AverageSearchTime
    {
        get
        {
            lock (_avgLock)
            {
                return (long)Math.Round(_averageSearchTime);
            }
        }
    }

    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger,
        MeilisearchClientHolder clientHolder,
        Indexer indexer,
        IHostApplicationLifetime hostApplicationLifetime
    ) : base(
        applicationPaths,
        xmlSerializer)
    {
        _logger = logger;
        _clientHolder = clientHolder;
        Indexer = indexer;
        Instance = this;

        ReloadMeilisearch += (_, _) =>
        {
            logger.LogInformation("Configuration changed, reloading meilisearch...");
            // Not awaited: saving the configuration should not wait for a full reindex.
            _ = TryCreateMeilisearchClient();
        };

        hostApplicationLifetime.ApplicationStarted.Register(() => { _ = TryCreateMeilisearchClient(); });
    }

    private EventHandler<BasePluginConfiguration> ReloadMeilisearch { get; }

    public override string Name => "Meilisearch";
    public override Guid Id => Guid.Parse("974395db-b31d-46a2-bc86-ef9aa5ac04dd");
    public static Plugin? Instance { get; private set; }


    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.config.html",
                    GetType().Namespace)
            }
        ];
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var config = (Config)configuration;
        var skipReload = Configuration.Url == config.Url
                         && Configuration.ApiKey == config.ApiKey
                         && Configuration.IndexName == config.IndexName;

        // Written by the indexer; the config page posts back whatever value it loaded.
        config.LastIndexedItemCount = Configuration.LastIndexedItemCount;
        Configuration = config;
        SaveConfiguration(Configuration);
        ConfigurationChanged?.Invoke(this, configuration);
        if (!skipReload)
            ReloadMeilisearch.Invoke(this, configuration);
    }

    private readonly object _reloadLock = new();
    private Task? _reloadTask;
    private bool _reloadPending;

    /// <summary>
    /// Connects to Meilisearch with the current configuration and reindexes. A request made while a reload is
    /// running starts one more once it ends, so a configuration saved meanwhile is applied. Errors are logged, not
    /// thrown.
    /// </summary>
    public Task TryCreateMeilisearchClient()
    {
        lock (_reloadLock)
        {
            if (_reloadTask != null)
            {
                _reloadPending = true;
                return _reloadTask;
            }

            // Task.Run, so the loop cannot finish and clear _reloadTask before it is assigned here.
            _reloadTask = Task.Run(ReloadLoopAsync);
            return _reloadTask;
        }
    }

    private async Task ReloadLoopAsync()
    {
        while (true)
        {
            try
            {
                await _clientHolder.Set(Configuration);
                await Indexer.Index();
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Failed to connect to Meilisearch and reindex");
            }

            lock (_reloadLock)
            {
                if (!_reloadPending)
                {
                    _reloadTask = null;
                    return;
                }

                _reloadPending = false;
            }
        }
    }


    public void UpdateAverageSearchTime(long searchTime)
    {
        lock (_avgLock)
        {
            _averageSearchTime = _hasSearchTime
                ? (searchTime + _averageSearchTime) / 2
                : searchTime;
            _hasSearchTime = true;
        }
    }
}
