using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Meilisearch;

// ReSharper disable once UnusedType.Global
public class PluginRegister : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<UpdateMeilisearchIndexTask>();
        serviceCollection.AddSingleton<MeilisearchClientHolder>();
        serviceCollection.AddSingleton<Indexer, EfCoreIndexer>();

        // Register the Meilisearch search provider with Jellyfin's search infrastructure.
        // Jellyfin discovers ISearchProvider exports via GetExports<ISearchProvider>() and
        // passes them to ISearchManager.AddParts() during startup.
        serviceCollection.AddSingleton<MeilisearchSearchProvider>();
        serviceCollection.AddSingleton<ISearchProvider>(sp =>
            sp.GetRequiredService<MeilisearchSearchProvider>());
    }
}
