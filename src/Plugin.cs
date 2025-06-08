using System.Globalization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

// ReSharper disable once ClassNeverInstantiated.Global
public class Plugin : BasePlugin<Config>, IHasWebPages
{
    private readonly MeilisearchClientHolder _clientHolder;
    private readonly ILogger<Plugin> _logger;
    public readonly Indexer Indexer;
    public long AverageSearchTime;

    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILogger<Plugin> logger,
        IServiceProvider serviceProvider,
        MeilisearchClientHolder clientHolder,
        Indexer indexer,
        IActionDescriptorCollectionProvider provider,
        IHostApplicationLifetime hostApplicationLifetime
    ) : base(
        applicationPaths,
        xmlSerializer)
    {
        _logger = logger;
        _clientHolder = clientHolder;
        Indexer = indexer;

        DbPath = Path.Combine(applicationPaths.DataPath, "jellyfin.db");
        logger.LogInformation("db_path={DB}", DbPath);
        Instance = this;

        ReloadMeilisearch += (_, _) =>
        {
            logger.LogInformation("Configuration changed, reloading meilisearch...");
            TryCreateMeilisearchClient().Wait();
        };

        hostApplicationLifetime.ApplicationStarted.Register(() => { _ = TryCreateMeilisearchClient(false); });
        hostApplicationLifetime.ApplicationStarted.Register(() => { TryAddFilter(provider, serviceProvider); });
    }

    private EventHandler<BasePluginConfiguration> ReloadMeilisearch { get; }

    public string DbPath { get; }

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

    private void TryAddFilter(IActionDescriptorCollectionProvider provider, IServiceProvider serviceProvider)
    {
        // I guess this would work purely because this is a plugin and is loaded after ItemsController
        var count = provider.AddDynamicFilter<MeilisearchMutateFilter>(serviceProvider, t =>
        {
            var sig = $"{t.ControllerTypeInfo.FullName}#{t.MethodInfo.Name}";
            if (Configuration.Debug) Console.WriteLine($"\tmethod: {sig}");

            return sig == "Jellyfin.Api.Controllers.ItemsController#GetItems";
        });
        _logger.LogInformation("Added {Count} action filters", count);
    }

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var config = (Config)configuration;
        var skipReload = Configuration.Url == config.Url && Configuration.ApiKey == config.ApiKey;

        Configuration = config;
        SaveConfiguration(Configuration);
        ConfigurationChanged?.Invoke(this, configuration);
        if (!skipReload)
            ReloadMeilisearch.Invoke(this, configuration);
    }

    private volatile Task? _updatingTask;

    public async Task TryCreateMeilisearchClient(bool join = true)
    {
        if (_updatingTask != null)
        {
            _logger.LogWarning("Meilisearch client configuration is still updating，skipping");
            if (join) await _updatingTask;
            return;
        }

        try
        {
            _updatingTask = _TryCreateMeilisearchClient();
            await _updatingTask;
        }
        finally
        {
            _updatingTask = null;
        }
    }

    private async Task _TryCreateMeilisearchClient()
    {
        await _clientHolder.Set(Configuration);
        await Indexer.Index();
    }


    public void UpdateAverageSearchTime(long averageSearchTime)
    {
        lock (this)
        {
            if (AverageSearchTime == 0) AverageSearchTime = averageSearchTime;
            AverageSearchTime = (averageSearchTime + AverageSearchTime) / 2;
        }
    }
}