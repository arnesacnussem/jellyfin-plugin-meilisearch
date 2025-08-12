using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Meilisearch;

[Route("meilisearch")]
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
public class Controller(MeilisearchClientHolder clientHolder) : ControllerBase
{
    [HttpGet("status")]
    public ActionResult GetStatus()
    {
        return new JsonResult(new
        {
            meilisearch = clientHolder.Status,
            meilisearchOk = clientHolder.Ok,
            averageSearchTime = $"{Plugin.Instance!.AverageSearchTime}ms",
            indexStatus = Plugin.Instance.Indexer.Status
        });
    }

    [HttpGet("reconnect")]
    public async Task<ActionResult> Reconnect()
    {
        if (!clientHolder.Ok) await Plugin.Instance!.TryCreateMeilisearchClient();
        return GetStatus();
    }

    [HttpGet("reindex")]
    public async Task<ActionResult> Reindex()
    {
        await Plugin.Instance!.Indexer.Index();
        return GetStatus();
    }
}