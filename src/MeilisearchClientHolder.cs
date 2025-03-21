﻿using MediaBrowser.Controller;
using Meilisearch;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Index = Meilisearch.Index;

namespace Jellyfin.Plugin.Meilisearch;

public class MeilisearchClientHolder(ILogger<MeilisearchClientHolder> logger, IServerApplicationHost applicationHost)
{
    public string Status { get; private set; } = "Not Configured";
    public bool Ok => Client != null && Index != null;
    public Index? Index { get; private set; }
    public MeilisearchClient? Client { get; private set; }

    public Task? Call(Func<MeilisearchClient, Index, Task> func)
    {
        return !Ok ? null : func(Client!, Index!);
    }

    public void Unset()
    {
        Client = null;
        Index = null;
    }

    public async Task Set(Config configuration)
    {
        if (configuration.Url.IsNullOrEmpty())
        {
            logger.LogWarning("Missing Meilisearch URL");
            Client = null;
            Index = null;
            Status = "Missing Meilisearch URL";
        }

        try
        {
            var apiKey = configuration.ApiKey.IsNullOrEmpty() ? null : configuration.ApiKey;
            Client = new MeilisearchClient(configuration.Url, apiKey);
            Index = await GetIndex(Client);
            UpdateMeilisearchHealth();
        }
        catch (Exception e)
        {
            Status = e.Message;
            Client = null;
            Index = null;
            logger.LogError(e, "Failed to create MeilisearchClient");
        }
    }

    private void UpdateMeilisearchHealth()
    {
        if (Client == null)
        {
            Status = "Server not configured";
            return;
        }

        var task = Client.HealthAsync();
        task.Wait();
        Status = task.IsCompletedSuccessfully ? $"Server: {task.Result.Status}" : $"Error: {task.Exception?.Message}";
    }

    private async Task<Index> GetIndex(MeilisearchClient meilisearch)
    {
        var configName = Plugin.Instance?.Configuration.IndexName;
        var sanitizedConfigName = applicationHost.FriendlyName.Replace(" ", "-");
        var index = meilisearch.Index(configName.IsNullOrEmpty() ? sanitizedConfigName : configName);

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
}