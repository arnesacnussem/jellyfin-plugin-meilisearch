using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

[Route("meilisearch")]
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
// Jellyfin 12's Swagger generator rejects plugin actions without an explicit tag.
[Tags("Meilisearch")]
public class Controller(MeilisearchClientHolder clientHolder, ILogger<Controller> logger) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult> GetStatus()
    {
        await Plugin.Instance!.Indexer.UpdateIndexStats();
        return new JsonResult(new
        {
            meilisearch = clientHolder.Status,
            meilisearchOk = clientHolder.Ok,
            indexing = Plugin.Instance.Indexer.IsIndexing,
            averageSearchTime = $"{Plugin.Instance!.AverageSearchTime}ms",
            indexStatus = Plugin.Instance.Indexer.Status
        });
    }

    /// <summary>
    /// Starts a reconnect, which reindexes too, and returns the status without waiting for it.
    /// </summary>
    [HttpPost("reconnect")]
    public async Task<ActionResult> Reconnect()
    {
        if (!clientHolder.Ok)
        {
            // Logs its own errors.
            _ = Plugin.Instance!.TryCreateMeilisearchClient();
        }

        return await GetStatus();
    }

    /// <summary>
    /// Starts a full reindex unless one is running, and returns the status without waiting for it.
    /// </summary>
    [HttpPost("reindex")]
    public async Task<ActionResult> Reindex()
    {
        var indexer = Plugin.Instance!.Indexer;
        if (!indexer.IsIndexing)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await indexer.Index();
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Failed to reindex Meilisearch");
                }
            });
        }

        return await GetStatus();
    }
}
