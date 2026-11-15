using System.Collections.Immutable;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

public class EfCoreIndexer(
    IJellyfinDatabaseProvider dbProvider,
    MeilisearchClientHolder clientHolder,
    ILogger<EfCoreIndexer> logger
) : Indexer(clientHolder, logger)
{
    protected override async Task<ImmutableList<MeilisearchItem>> GetItems(
        IReadOnlySet<string> includedTypes, IReadOnlyCollection<Guid>? ids = null)
    {
        using var context = dbProvider.DbContextFactory!.CreateDbContext();

        var query = context.BaseItems
            .AsNoTracking()
            .Where(x => x.Type != null && includedTypes.Contains(x.Type));

        if (ids != null)
        {
            query = query.Where(x => ids.Contains(x.Id));
        }

        // Only the indexed columns: a full row carries many fields that are never indexed.
        var rows = await query
            .Select(x => new
            {
                x.Id,
                x.Type,
                x.ParentId,
                x.Name,
                x.Overview,
                x.OriginalTitle,
                x.SeriesName,
                x.Studios,
                x.Genres,
                x.Tags,
                x.CommunityRating,
                x.ProductionYear,
                x.Path,
                x.Artists,
                x.AlbumArtists,
                x.CriticRating,
                x.IsFolder,
                x.Tagline,
                x.SortName
            })
            .ToListAsync();

        var ancestors = await GetAncestorIds(context, ids);

        return rows.Select(item => new MeilisearchItem(
            Guid: item.Id.ToString(),
            Type: item.Type,
            ParentId: item.ParentId.ToString(),
            AncestorIds: ancestors.TryGetValue(item.Id, out var itemAncestors) ? [.. itemAncestors] : [],
            Name: item.Name,
            Overview: item.Overview,
            OriginalTitle: item.OriginalTitle,
            SeriesName: item.SeriesName,
            Studios: item.Studios?.Split('|'),
            Genres: item.Genres?.Split('|'),
            Tags: item.Tags?.Split('|'),
            CommunityRating: item.CommunityRating,
            ProductionYear: item.ProductionYear,
            Path: string.IsNullOrEmpty(item.Path) || item.Path[0] != '%' ? item.Path : null,
            Artists: item.Artists?.Split('|'),
            AlbumArtists: item.AlbumArtists?.Split('|'),
            CriticRating: item.CriticRating,
            IsFolder: item.IsFolder,
            Tagline: item.Tagline,
            SortName: item.SortName
        )).ToImmutableList();
    }

    /// <summary>
    /// Reads every item's ancestors and the collections or playlists that link it, so a search scoped to a
    /// library, series or box set matches everything beneath it, as Jellyfin's own traversal does.
    /// </summary>
    private static async Task<Dictionary<Guid, HashSet<string>>> GetAncestorIds(
        JellyfinDbContext context, IReadOnlyCollection<Guid>? ids)
    {
        var ancestorQuery = context.AncestorIds.AsNoTracking();
        var linkQuery = context.LinkedChildren.AsNoTracking()
            .Where(l => l.ChildType == LinkedChildType.Manual || l.ChildType == LinkedChildType.Shortcut);
        if (ids != null)
        {
            ancestorQuery = ancestorQuery.Where(a => ids.Contains(a.ItemId));
            linkQuery = linkQuery.Where(l => ids.Contains(l.ChildId));
        }

        var pairs = await ancestorQuery.Select(a => new { a.ItemId, ParentId = a.ParentItemId }).ToListAsync();
        pairs.AddRange(await linkQuery.Select(l => new { ItemId = l.ChildId, l.ParentId }).ToListAsync());

        // Few distinct parents, many items: share one string per parent.
        var parentStrings = new Dictionary<Guid, string>();
        var result = new Dictionary<Guid, HashSet<string>>();
        foreach (var pair in pairs)
        {
            if (!parentStrings.TryGetValue(pair.ParentId, out var parent))
            {
                parent = pair.ParentId.ToString();
                parentStrings[pair.ParentId] = parent;
            }

            if (!result.TryGetValue(pair.ItemId, out var set))
            {
                set = [];
                result[pair.ItemId] = set;
            }

            set.Add(parent);
        }

        return result;
    }
}
