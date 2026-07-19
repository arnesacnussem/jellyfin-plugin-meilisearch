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
            TryCreateMeilisearchClient().Wait();
        };

        hostApplicationLifetime.ApplicationStarted.Register(() => { _ = TryCreateMeilisearchClient(false); });
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

        Configuration = config;
        SaveConfiguration(Configuration);
        ConfigurationChanged?.Invoke(this, configuration);
        if (!skipReload)
            ReloadMeilisearch.Invoke(this, configuration);
    }

    private readonly object _updatingTaskLock = new();
    private Task? _updatingTask;

    public async Task TryCreateMeilisearchClient(bool join = true)
    {
        Task? task;
        bool startedNew;
        lock (_updatingTaskLock)
        {
            startedNew = _updatingTask == null;
            if (startedNew)
            {
                _updatingTask = _TryCreateMeilisearchClient();
            }

            task = _updatingTask;
        }

        if (!startedNew)
        {
            _logger.LogWarning("Meilisearch client configuration is still updating，skipping");
            if (join) await task!;
            return;
        }

        try
        {
            await task!;
        }
        finally
        {
            lock (_updatingTaskLock)
            {
                _updatingTask = null;
            }
        }
    }

    private async Task _TryCreateMeilisearchClient()
    {
        await _clientHolder.Set(Configuration);
        await Indexer.Index();
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
