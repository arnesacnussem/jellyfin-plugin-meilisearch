using System.Threading.Channels;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

/// <summary>
/// Indexes items added to or updated in the library, so they can be found before the next full reindex.
/// Changes are batched, because a library scan or a Live TV guide refresh changes thousands of items one
/// event at a time.
/// </summary>
public sealed class ItemUpdateQueue(
    ILibraryManager libraryManager,
    Indexer indexer,
    ILogger<ItemUpdateQueue> logger
) : IHostedService, IDisposable
{
    private const int MaxBatchSize = 5000;
    private static readonly TimeSpan BatchDelay = TimeSpan.FromSeconds(5);

    private readonly Channel<Guid> _queue =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemAdded += OnItemChanged;
        libraryManager.ItemUpdated += OnItemChanged;
        _worker = Task.Run(() => ProcessAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        libraryManager.ItemAdded -= OnItemChanged;
        libraryManager.ItemUpdated -= OnItemChanged;
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
            // Anything still queued is picked up by the next full reindex.
        }
        finally
        {
            await _stopping.CancelAsync();
        }
    }

    public void Dispose() => _stopping.Dispose();

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        _queue.Writer.TryWrite(e.Item.Id);
    }

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                await Task.Delay(BatchDelay, cancellationToken);

                var batch = new HashSet<Guid>();
                while (batch.Count < MaxBatchSize && reader.TryRead(out var id))
                {
                    batch.Add(id);
                }

                try
                {
                    var count = await indexer.Update(batch);
                    logger.LogDebug("Updated {COUNT} of {CHANGED} changed items in Meilisearch", count, batch.Count);
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Failed to update {COUNT} items in Meilisearch", batch.Count);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
