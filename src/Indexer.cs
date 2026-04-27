using System.Collections.Immutable;
using System.Globalization;
using Meilisearch;
using Microsoft.Extensions.Logging;
using Index = Meilisearch.Index;

namespace Jellyfin.Plugin.Meilisearch;

public abstract class Indexer(MeilisearchClientHolder clientHolder, ILogger<Indexer> logger)
{
    public Dictionary<string, string> Status { get; } = new();

    public async Task Index()
    {
        var task = clientHolder.Call(IndexInternal);
        if (task == null)
        {
            logger.LogWarning("Meilisearch is not configured, skipping index update");
            return;
        }

        await task;
    }

    private async Task IndexInternal(MeilisearchClient meilisearchClient, Index index)
    {
        var includedTypes = Plugin.Instance?.Configuration.IncludedItemTypes ?? Config.DefaultIncludedItemTypes;
        if (includedTypes.Length == 0) includedTypes = Config.DefaultIncludedItemTypes;
        var includedFullNames = includedTypes
            .Select(key => TypeHelper.JellyfinTypeMap.TryGetValue(key, out var typeValue) ? typeValue : null)
            .OfType<string>()
            .ToHashSet();

        var items = await GetItems(includedFullNames);

        if (items.Count <= 0)
        {
            logger.LogInformation("No items to index");
            return;
        }

        await index.AddDocumentsInBatchesAsync(items, batchSize: 5000, primaryKey: "guid");
        logger.LogInformation("Upload {COUNT} items to Meilisearch", items.Count);
        Status["Items"] = items.Count.ToString();
        Status["LastIndexed"] = DateTime.Now.ToString(CultureInfo.CurrentCulture);
    }

    protected abstract Task<ImmutableList<MeilisearchItem>> GetItems(IReadOnlySet<string> includedTypes);
}