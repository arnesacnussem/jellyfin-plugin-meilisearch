using System.Threading.Channels;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

/// <summary>
/// Removes documents of items deleted from the library. Removals are batched, because a Live TV guide
/// refresh removes thousands of programmes one event at a time.
/// </summary>
public sealed class ItemRemovalQueue(
    ILibraryManager libraryManager,
    Indexer indexer,
    ILogger<ItemRemovalQueue> logger
) : IHostedService, IDisposable
{
    private const int MaxBatchSize = 5000;
    private static readonly TimeSpan BatchDelay = TimeSpan.FromSeconds(2);

    private readonly Channel<string> _queue =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemRemoved += OnItemRemoved;
        _worker = Task.Run(() => ProcessAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemRemoved -= OnItemRemoved;
        _queue.Writer.TryComplete();
        if (_worker == null)
        {
            return;
        }

        try
        {
            await _worker.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Anything still queued is cleaned up by the next full reindex.
        }
        finally
        {
            await _stopping.CancelAsync();
        }
    }

    public void Dispose() => _stopping.Dispose();

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        _queue.Writer.TryWrite(e.Item.Id.ToString());
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                await Task.Delay(BatchDelay, cancellationToken);

                var batch = new HashSet<string>();
                while (batch.Count < MaxBatchSize && reader.TryRead(out var id))
                {
                    batch.Add(id);
                }

                try
                {
                    await indexer.Remove(batch);
                    logger.LogDebug("Removed {COUNT} documents from Meilisearch", batch.Count);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Failed to remove {COUNT} documents from Meilisearch", batch.Count);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
