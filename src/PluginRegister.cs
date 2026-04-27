using MediaBrowser.Controller;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

// ReSharper disable once UnusedType.Global
public class PluginRegister : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<UpdateMeilisearchIndexTask>();
        serviceCollection.AddSingleton<MeilisearchClientHolder>();
        serviceCollection.AddSingleton<Indexer, DbIndexer>();

        var descriptor = serviceCollection.FirstOrDefault(d => d.ServiceType == typeof(IItemRepository));
        if (descriptor is not null)
        {
            serviceCollection.Remove(descriptor);
            serviceCollection.AddSingleton<IItemRepository>(sp =>
            {
                IItemRepository original;
                if (descriptor.ImplementationInstance is not null)
                    original = (IItemRepository)descriptor.ImplementationInstance;
                else if (descriptor.ImplementationFactory is not null)
                    original = (IItemRepository)descriptor.ImplementationFactory(sp);
                else
                    original = (IItemRepository)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
                return new MeilisearchRepositoryDecorator(
                    original,
                    sp.GetRequiredService<MeilisearchClientHolder>(),
                    sp.GetRequiredService<ILogger<MeilisearchRepositoryDecorator>>()
                );
            });
        }
        else
        {
            Console.Error.WriteLine("[Meilisearch] WARNING: IItemRepository was not registered before the plugin — Meilisearch search decoration is disabled.");
        }
    }
}
