using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

/// <summary>
/// Removes documents of items deleted from the library, so they do not stay in search until the next full reindex.
/// </summary>
public sealed class ItemRemovalQueue(
    ILibraryManager libraryManager,
    Indexer indexer,
    ILogger<ItemRemovalQueue> logger
) : BatchingQueue<string>(TimeSpan.FromSeconds(2))
{
    protected override void Subscribe() => libraryManager.ItemRemoved += OnItemRemoved;

    protected override void Unsubscribe() => libraryManager.ItemRemoved -= OnItemRemoved;

    protected override async Task ProcessBatchAsync(IReadOnlyCollection<string> batch)
    {
        await indexer.Remove(batch);
        logger.LogDebug("Removed {COUNT} documents from Meilisearch", batch.Count);
    }

    protected override void OnBatchFailed(IReadOnlyCollection<string> batch, Exception exception)
        => logger.LogWarning(exception, "Failed to remove {COUNT} documents from Meilisearch", batch.Count);

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e) => Enqueue(e.Item.Id.ToString());
}
