using MediaBrowser.Controller;
using Meilisearch;
using Microsoft.Extensions.Logging;
using Index = Meilisearch.Index;

namespace Jellyfin.Plugin.Meilisearch;

public class MeilisearchClientHolder(ILogger<MeilisearchClientHolder> logger, IServerApplicationHost applicationHost)
{
    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private volatile Config? _lastConfiguration;

    public string Status { get; private set; } = "Not Configured";
    public bool Ok => Client != null && Index != null;
    public Index? Index { get; private set; }
    public MeilisearchClient? Client { get; private set; }

    public Task? Call(Func<MeilisearchClient, Index, Task> func)
        => Call(async (client, index) =>
        {
            await func(client, index).ConfigureAwait(false);
            return true;
        });

    public Task<T>? Call<T>(Func<MeilisearchClient, Index, Task<T>> func)
    {
        if (!Ok)
        {
            _ = TryReconnectInBackground("Call invoked while not connected");
            return null;
        }

        return ExecuteWithReconnectRetryAsync(func);
    }

    public void Unset()
    {
        Client = null;
        Index = null;
        Status = "Disconnected";
    }

    public async Task Set(Config configuration)
    {
        _lastConfiguration = configuration;
        if (string.IsNullOrEmpty(configuration.Url) && 
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MEILI_URL")))
        {
            logger.LogWarning("Missing Meilisearch URL");
            Client = null;
            Index = null;
            Status = "Missing Meilisearch URL";
            return;
        }

        try
        {
            // Check for environment variable first
            var envApiKey = Environment.GetEnvironmentVariable("MEILI_MASTER_KEY");
            // Use API key from config if env var is not set
            var apiKey = string.IsNullOrEmpty(envApiKey)
                ? (string.IsNullOrEmpty(configuration.ApiKey) ? null : configuration.ApiKey)
                : envApiKey;
            // Check for environment variable first
            var envURL = Environment.GetEnvironmentVariable("MEILI_URL");
            // Use URL from config if env var is not set
            var url = string.IsNullOrEmpty(envURL) 
                ? (string.IsNullOrEmpty(configuration.Url) ? null : configuration.Url)
                : envURL;
            Client = new MeilisearchClient(url, apiKey);
            Index = await GetIndex(Client, configuration.IndexName).ConfigureAwait(false);
            await UpdateMeilisearchHealthAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Status = e.Message;
            Client = null;
            Index = null;
            logger.LogError(e, "Failed to create MeilisearchClient");
        }
    }

    private async Task UpdateMeilisearchHealthAsync()
    {
        if (Client == null)
        {
            Status = "Server not configured";
            return;
        }

        try
        {
            var health = await Client.HealthAsync().ConfigureAwait(false);
            Status = $"Server: {health.Status}";
        }
        catch (Exception e)
        {
            Status = $"Error: {e.Message}";
            logger.LogWarning(e, "Meilisearch health check failed");
        }
    }

    private async Task<Index> GetIndex(MeilisearchClient meilisearch, string? configuredIndexName)
    {
        var sanitizedConfigName = applicationHost.FriendlyName.Replace(" ", "-");
        var indexName = string.IsNullOrEmpty(configuredIndexName) ? sanitizedConfigName : configuredIndexName;
        var index = meilisearch.Index(indexName);

        await index.UpdateFilterableAttributesAsync(
            ["type", "parentId", "isFolder"]
        );

        await index.UpdateSortableAttributesAsync(
            ["communityRating", "criticRating"]
        );

        await index.UpdateSearchableAttributesAsync(Config.DefaultAttributesToSearchOn);
        await index.UpdateDisplayedAttributesAsync(Config.DefaultAttributesToSearchOn.Concat(["guid", "type"]));

        // Set ranking rules to add critic rating
        await index.UpdateRankingRulesAsync(
            [
                "words", "typo", "proximity", "attribute", "sort", "exactness", "communityRating:desc",
                "criticRating:desc"
            ]
        );
        return index;
    }

    private async Task<T> ExecuteWithReconnectRetryAsync<T>(Func<MeilisearchClient, Index, Task<T>> func)
    {
        try
        {
            return await func(Client!, Index!).ConfigureAwait(false);
        }
        catch (Exception e) when (IsReconnectable(e))
        {
            logger.LogWarning(e, "Meilisearch request failed; will reset client and try reconnect");
            Unset();
            await TryReconnectAsync("request failed", e).ConfigureAwait(false);

            if (!Ok)
            {
                throw;
            }

            return await func(Client!, Index!).ConfigureAwait(false);
        }
    }

    private async Task TryReconnectAsync(string reason, Exception? exception = null)
    {
        var configuration = _lastConfiguration;
        if (configuration == null)
        {
            logger.LogDebug("Skipping reconnect: no configuration cached (reason={Reason})", reason);
            return;
        }

        await _reconnectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Ok)
            {
                return;
            }

            logger.LogInformation(exception, "Reconnecting to Meilisearch (reason={Reason})", reason);
            await Set(configuration).ConfigureAwait(false);
        }
        finally
        {
            _reconnectLock.Release();
        }
    }

    private Task TryReconnectInBackground(string reason)
    {
        if (_lastConfiguration == null)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() => TryReconnectAsync(reason));
    }

    private static bool IsReconnectable(Exception e)
    {
        return e is MeilisearchCommunicationError
               || e is HttpRequestException
               || e is TaskCanceledException
               || e is TimeoutException;
    }
}