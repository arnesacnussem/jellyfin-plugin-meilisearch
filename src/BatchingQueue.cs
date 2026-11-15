using System.Threading.Channels;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.Meilisearch;

/// <summary>
/// Collects library changes and hands them on in batches, because a library scan or a Live TV guide refresh
/// changes thousands of items one event at a time.
/// </summary>
public abstract class BatchingQueue<T>(TimeSpan batchDelay) : IHostedService, IDisposable
{
    private const int MaxBatchSize = 5000;

    private readonly Channel<T> _queue =
        Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true });

    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Subscribe();
        _worker = Task.Run(() => ProcessAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
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
            // Anything still queued is handled by the next full reindex.
        }
        finally
        {
            await _stopping.CancelAsync();
        }
    }

    public void Dispose() => _stopping.Dispose();

    protected void Enqueue(T item) => _queue.Writer.TryWrite(item);

    protected abstract void Subscribe();

    protected abstract void Unsubscribe();

    /// <summary>
    /// Handles one batch. Exceptions are caught by the caller; the batch is then left to the next full reindex.
    /// </summary>
    protected abstract Task ProcessBatchAsync(IReadOnlyCollection<T> batch);

    private async Task ProcessAsync(CancellationToken cancellationToken)
    {
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken))
            {
                await Task.Delay(batchDelay, cancellationToken);

                var batch = new HashSet<T>();
                while (batch.Count < MaxBatchSize && reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                try
                {
                    await ProcessBatchAsync(batch);
                }
                catch (Exception e)
                {
                    OnBatchFailed(batch, e);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    protected abstract void OnBatchFailed(IReadOnlyCollection<T> batch, Exception exception);
}
