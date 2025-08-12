using System.Collections.Immutable;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

public class EfCoreIndexer(
    IJellyfinDatabaseProvider dbProvider,
    MeilisearchClientHolder clientHolder,
    ILogger<DbIndexer> logger
) : Indexer(clientHolder, logger)
{
    protected override Task<ImmutableList<MeilisearchItem>> GetItems()
    {
        var context = dbProvider.DbContextFactory!.CreateDbContext();
        Status["Database"] = context.Database.GetDbConnection().ConnectionString;

        return Task.FromResult(context.BaseItems.ToImmutableList().Select(ToMeilisearchItem).ToImmutableList());
    }


    private static MeilisearchItem ToMeilisearchItem(BaseItemEntity item)
    {
        return new MeilisearchItem(
            Guid: item.Id.ToString(),
            Type: item.Type,
            ParentId: item.ParentId.ToString(),
            Name: item.Name,
            Overview: item.Overview,
            OriginalTitle: item.OriginalTitle,
            SeriesName: item.SeriesName,
            Studios: item.Studios?.Split('|'),
            Genres: item.Genres?.Split('|'),
            Tags: item.Tags?.Split('|'),
            CommunityRating: item.CommunityRating,
            ProductionYear: item.ProductionYear,
            Path: item.Path?[0] == '%' ? null : item.Path,
            Artists: item.Artists?.Split('|'),
            AlbumArtists: item.AlbumArtists?.Split('|'),
            CriticRating: item.CriticRating,
            IsFolder: item.IsFolder
        );
    }
}