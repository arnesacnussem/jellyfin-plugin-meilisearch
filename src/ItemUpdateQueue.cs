using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

/// <summary>
/// Indexes items added to or updated in the library, so they can be found before the next full reindex.
/// </summary>
public sealed class ItemUpdateQueue(
    ILibraryManager libraryManager,
    Indexer indexer,
    ILogger<ItemUpdateQueue> logger
) : BatchingQueue<Guid>(TimeSpan.FromSeconds(5))
{
    protected override void Subscribe()
    {
        libraryManager.ItemAdded += OnItemChanged;
        libraryManager.ItemUpdated += OnItemChanged;
    }

    protected override void Unsubscribe()
    {
        libraryManager.ItemAdded -= OnItemChanged;
        libraryManager.ItemUpdated -= OnItemChanged;
    }

    protected override async Task ProcessBatchAsync(IReadOnlyCollection<Guid> batch)
    {
        var count = await indexer.Update(batch);
        logger.LogDebug("Updated {COUNT} of {CHANGED} changed items in Meilisearch", count, batch.Count);
    }

    protected override void OnBatchFailed(IReadOnlyCollection<Guid> batch, Exception exception)
        => logger.LogWarning(exception, "Failed to update {COUNT} items in Meilisearch", batch.Count);

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        // Types that are never indexed would only cost a database read.
        if (TypeHelper.TypeFullNames.Contains(e.Item.GetType().FullName!))
        {
            Enqueue(e.Item.Id);
        }
    }
}
