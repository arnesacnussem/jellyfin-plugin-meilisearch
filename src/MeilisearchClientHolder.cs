using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MediaBrowser.Controller;
using Meilisearch;
using Microsoft.Extensions.Logging;
using Index = Meilisearch.Index;

namespace Jellyfin.Plugin.Meilisearch;

public class MeilisearchClientHolder(ILogger<MeilisearchClientHolder> logger, IServerApplicationHost applicationHost)
{
    private static readonly string[] SearchableAttributes =
    [
        "name", "sortName", "artists", "albumArtists", "originalTitle", "productionYear", "seriesName", "genres",
        "tags", "studios", "overview", "path", "tagline"
    ];

    private readonly SemaphoreSlim _reconnectLock = new(1, 1);
    private static readonly HttpClient SwapHttpClient = new();
    private volatile Config? _lastConfiguration;
    private string? _url;
    private string? _apiKey;

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
        var client = Client;
        var index = Index;
        if (client == null || index == null)
        {
            _ = TryReconnectInBackground("Call invoked while not connected");
            return null;
        }

        return ExecuteWithReconnectRetryAsync(client, index, func);
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
            _url = url;
            _apiKey = apiKey;
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
        await ApplySettingsAsync(index);
        return index;
    }

    /// <summary>
    /// Enqueues the plugin's index settings. Returns the enqueued tasks so callers can wait for them.
    /// </summary>
    public static async Task<IReadOnlyList<TaskInfo>> ApplySettingsAsync(Index index)
    {
        return
        [
            await index.UpdateFilterableAttributesAsync(
                ["type", "parentId", "isFolder"]
            ),
            await index.UpdateSortableAttributesAsync(
                ["communityRating", "criticRating"]
            ),
            await index.UpdateSearchableAttributesAsync(SearchableAttributes),
            await index.UpdateDisplayedAttributesAsync(SearchableAttributes.Concat(["guid", "type"])),
            // Set ranking rules to add critic rating
            await index.UpdateRankingRulesAsync(
                [
                    "words", "typo", "proximity", "attribute", "sort", "exactness", "communityRating:desc",
                    "criticRating:desc"
                ]
            )
        ];
    }

    /// <summary>
    /// Enqueues a swap of two indexes and returns its task uid. The SDK's SwapIndexesAsync always sends the
    /// "rename" field, which older Meilisearch versions (such as 1.12) reject, so the request is sent directly.
    /// </summary>
    public async Task<int> SwapIndexesAsync(string first, string second)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _url!.TrimEnd('/') + "/swap-indexes");
        if (!string.IsNullOrEmpty(_apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        request.Content = JsonContent.Create(new[] { new { indexes = new[] { first, second } } });
        using var response = await SwapHttpClient.SendAsync(request).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Meilisearch index swap failed ({(int)response.StatusCode}): {body}");
        }

        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("taskUid").GetInt32();
    }

    private async Task<T> ExecuteWithReconnectRetryAsync<T>(
        MeilisearchClient client, Index index, Func<MeilisearchClient, Index, Task<T>> func)
    {
        try
        {
            return await func(client, index).ConfigureAwait(false);
        }
        catch (Exception e) when (IsReconnectable(e))
        {
            logger.LogWarning(e, "Meilisearch request failed; will reset client and try reconnect");
            Unset();
            await TryReconnectAsync("request failed", e).ConfigureAwait(false);

            var retryClient = Client;
            var retryIndex = Index;
            if (retryClient == null || retryIndex == null)
            {
                throw;
            }

            return await func(retryClient, retryIndex).ConfigureAwait(false);
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

    public static bool IsReconnectable(Exception e)
    {
        return e is MeilisearchCommunicationError
               || e is HttpRequestException
               || e is TaskCanceledException
               || e is TimeoutException;
    }
}
