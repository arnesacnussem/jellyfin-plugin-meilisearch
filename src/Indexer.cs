using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using Meilisearch;
using Microsoft.Extensions.Logging;
using Index = Meilisearch.Index;

namespace Jellyfin.Plugin.Meilisearch;

public abstract class Indexer(MeilisearchClientHolder clientHolder, ILogger<Indexer> logger)
{
    private const int BatchSize = 5000;

    // A database read that returns less than this share of the previous reindex is treated as incomplete
    // once: its items are added, but nothing is removed from the index.
    private const double MinimumItemRatio = 0.1;

    private static readonly TimeSpan TaskTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TaskPollInterval = TimeSpan.FromSeconds(1);

    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly object _removedLock = new();
    private HashSet<string>? _removedDuringReindex;

    public ConcurrentDictionary<string, string> Status { get; } = new();

    public async Task Index()
    {
        await _indexLock.WaitAsync();
        try
        {
            var task = clientHolder.Call(IndexInternal);
            if (task == null)
            {
                logger.LogWarning("Meilisearch is not configured, skipping index update");
                return;
            }

            await task;
        }
        finally
        {
            _indexLock.Release();
        }
    }

    /// <summary>
    /// Removes documents from the index. Removals made while a full reindex is running are applied again
    /// once it has written its documents.
    /// </summary>
    public async Task Remove(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        lock (_removedLock)
        {
            _removedDuringReindex?.UnionWith(ids);
        }

        var task = clientHolder.Call(async (_, index) => { await index.DeleteDocumentsAsync(ids); });
        if (task != null)
        {
            await task;
        }
    }

    /// <summary>
    /// Reads the live document count from Meilisearch into <see cref="Status"/>.
    /// </summary>
    public async Task UpdateIndexStats()
    {
        try
        {
            var task = clientHolder.Call(async (_, index) =>
            {
                var stats = await index.GetStatsAsync();
                Status["Documents"] = stats.NumberOfDocuments.ToString(CultureInfo.InvariantCulture);
                Status["IsIndexing"] = stats.IsIndexing.ToString();
            });
            if (task != null)
            {
                await task;
                return;
            }
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Failed to read Meilisearch index stats");
        }

        // Do not show a count that can no longer be checked.
        Status.TryRemove("Documents", out _);
        Status.TryRemove("IsIndexing", out _);
    }

    private async Task IndexInternal(MeilisearchClient meilisearchClient, Index index)
    {
        // Track removals from before the database read: an item read here and removed afterwards would
        // otherwise be written back by this reindex.
        lock (_removedLock)
        {
            _removedDuringReindex = [];
        }

        try
        {
            var items = await GetItems(TypeHelper.TypeFullNames);

            if (items.Count <= 0)
            {
                logger.LogInformation("No items to index");
                return;
            }

            var previousCount = Plugin.Instance?.Configuration.LastIndexedItemCount ?? 0;
            SaveLastIndexedItemCount(items.Count);

            if (items.Count < previousCount * MinimumItemRatio)
            {
                // Only add: if the read was incomplete, rebuilding from it would empty the index. The count is
                // saved anyway, so the next reindex rebuilds if the database still returns this many items.
                logger.LogWarning(
                    "Database returned {COUNT} items, far fewer than the {PREVIOUS} of the last reindex; " +
                    "adding them without removing stale documents",
                    items.Count, previousCount);
                await AddDocuments(meilisearchClient, index, items);
            }
            else
            {
                try
                {
                    await Rebuild(meilisearchClient, index, items);
                }
                catch (Exception e) when (!MeilisearchClientHolder.IsReconnectable(e))
                {
                    // Keep the index current even when it cannot be rebuilt, for example with an API key
                    // that lacks the index actions.
                    logger.LogError(e,
                        "Failed to rebuild Meilisearch index {INDEX}; adding items without removing stale " +
                        "documents. The API key needs the indexes.create, indexes.delete and indexes.swap actions",
                        index.Uid);
                    await AddDocuments(meilisearchClient, index, items);
                }
            }

            string[] removed;
            lock (_removedLock)
            {
                removed = [.. _removedDuringReindex!];
                _removedDuringReindex = null;
            }

            if (removed.Length > 0)
            {
                await WaitForTask(meilisearchClient, await index.DeleteDocumentsAsync(removed));
            }

            logger.LogInformation("Upload {COUNT} items to Meilisearch", items.Count);
            Status["Items"] = items.Count.ToString();
            Status["LastIndexed"] = DateTime.Now.ToString(CultureInfo.CurrentCulture);
            await UpdateIndexStats();
        }
        finally
        {
            lock (_removedLock)
            {
                _removedDuringReindex = null;
            }
        }
    }

    /// <summary>
    /// Builds a fresh index from <paramref name="items"/> and swaps it with the live one, so documents of
    /// removed items do not survive a full reindex.
    /// </summary>
    private async Task Rebuild(MeilisearchClient client, Index index, IReadOnlyCollection<MeilisearchItem> items)
    {
        var tempUid = index.Uid + "_rebuild";

        try
        {
            // Left over from an interrupted rebuild, or absent.
            await WaitForTask(client, await client.DeleteIndexAsync(tempUid), requireSuccess: false);
            await WaitForTask(client, await client.CreateIndexAsync(tempUid, "guid"));

            var temp = client.Index(tempUid);
            foreach (var task in await MeilisearchClientHolder.ApplySettingsAsync(temp))
            {
                await WaitForTask(client, task);
            }

            await AddDocuments(client, temp, items);

            // Swapping needs both indexes; the live one fails with "already exists" when present.
            await WaitForTask(client, await client.CreateIndexAsync(index.Uid, "guid"), requireSuccess: false);
            await WaitForTask(client, await clientHolder.SwapIndexesAsync(index.Uid, tempUid));
        }
        catch
        {
            await TryDeleteIndex(client, tempUid);
            throw;
        }

        // After the swap the temporary name holds the old documents.
        await TryDeleteIndex(client, tempUid);
        logger.LogInformation("Rebuilt Meilisearch index {INDEX}", index.Uid);
    }

    private static async Task AddDocuments(
        MeilisearchClient client, Index index, IReadOnlyCollection<MeilisearchItem> items)
    {
        foreach (var task in await index.AddDocumentsInBatchesAsync(items, batchSize: BatchSize, primaryKey: "guid"))
        {
            await WaitForTask(client, task);
        }
    }

    private async Task TryDeleteIndex(MeilisearchClient client, string uid)
    {
        try
        {
            await client.DeleteIndexAsync(uid);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Failed to delete Meilisearch index {INDEX}", uid);
        }
    }

    private static Task WaitForTask(MeilisearchClient client, TaskInfo task, bool requireSuccess = true)
        => WaitForTask(client, task.TaskUid, requireSuccess);

    private static async Task WaitForTask(MeilisearchClient client, int taskUid, bool requireSuccess = true)
    {
        var result = await client.WaitForTaskAsync(
            taskUid, TaskTimeout.TotalMilliseconds, (int)TaskPollInterval.TotalMilliseconds);
        if (requireSuccess && result.Status != TaskInfoStatus.Succeeded)
        {
            throw new InvalidOperationException(
                $"Meilisearch task {taskUid} ({result.Type}) {result.Status}: " +
                result.Error?.GetValueOrDefault("message"));
        }
    }

    private static void SaveLastIndexedItemCount(int count)
    {
        var plugin = Plugin.Instance;
        if (plugin == null || plugin.Configuration.LastIndexedItemCount == count)
        {
            return;
        }

        plugin.Configuration.LastIndexedItemCount = count;
        plugin.SaveConfiguration();
    }

    protected abstract Task<ImmutableList<MeilisearchItem>> GetItems(IReadOnlySet<string> includedTypes);
}
