using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

public class UpdateMeilisearchIndexTask(
    ILogger<UpdateMeilisearchIndexTask> logger,
    Indexer indexer,
    MeilisearchClientHolder clientHolder)
    : ILibraryPostScanTask
{
    public async Task Run(IProgress<double> progress, CancellationToken cancellationToken)
    {
        logger.LogInformation("Updating Meilisearch index");
        if (!clientHolder.Ok && Plugin.Instance is { } plugin)
        {
            // Connecting reindexes too, and logs its own errors.
            logger.LogInformation("Meilisearch is not connected, reconnecting");
            await plugin.TryCreateMeilisearchClient();
            return;
        }

        try
        {
            await indexer.Index();
        }
        catch (Exception e)
        {
            logger.LogError(e, "Failed to update Meilisearch index");
        }
    }
}
