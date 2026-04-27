using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Meilisearch;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

public class MeilisearchRepositoryDecorator(
    IItemRepository inner,
    MeilisearchClientHolder clientHolder,
    ILogger<MeilisearchRepositoryDecorator> logger
) : IItemRepository
{
    public QueryResult<BaseItem> GetItems(InternalItemsQuery filter)
    {
        if (string.IsNullOrEmpty(filter.SearchTerm) || !clientHolder.Ok || clientHolder.Index == null)
            return inner.GetItems(filter);

        // IItemRepository.GetItems is synchronous; Task.Run avoids a deadlock by running
        // SearchAsync on a thread-pool thread without an ASP.NET SynchronizationContext.
        var ids = Task.Run(() => SearchAsync(filter)).GetAwaiter().GetResult();

        if (ids.Count == 0)
            return inner.GetItems(filter); // fall back to Jellyfin native search

        filter.ItemIds = [.. ids];
        filter.SearchTerm = null;
        return inner.GetItems(filter);
    }

    private async Task<IReadOnlyList<Guid>> SearchAsync(InternalItemsQuery filter)
    {
        var types = BuildTypeList(filter);
        if (types.Count == 0) return [];

        var limit = filter.Limit is > 0 ? filter.Limit.Value : 30;
        var limitPerType = Math.Clamp(limit / types.Count, 30, 100);
        try
        {
            var tasks = types.Select(type => clientHolder.Index!.SearchAsync<MeilisearchItem>(
                filter.SearchTerm,
                new SearchQuery
                {
                    Filter = $"type = \"{type}\"",
                    Limit = limitPerType,
                }
            ));
            var results = await Task.WhenAll(tasks);
            return results
                .SelectMany(r => r.Hits)
                .DistinctBy(h => h.Guid)
                .Select(h => Guid.Parse(h.Guid))
                .ToList();
        }
        catch (MeilisearchCommunicationError e)
        {
            logger.LogError(e, "Meilisearch communication error");
            clientHolder.Unset();
            return [];
        }
    }

    private static List<string> BuildTypeList(InternalItemsQuery filter)
    {
        List<string> types;
        if (filter.IncludeItemTypes is { Length: > 0 })
        {
            types = TypeHelper.MapTypeKeys(filter.IncludeItemTypes)
                .Where(TypeHelper.TypeFullNames.Contains)
                .ToList();
        }
        else
        {
            types = [.. TypeHelper.TypeFullNames];
        }

        if (filter.ExcludeItemTypes is { Length: > 0 })
        {
            var excludeNames = TypeHelper.MapTypeKeys(filter.ExcludeItemTypes).ToHashSet();
            types.RemoveAll(excludeNames.Contains);
        }

        return types;
    }

    public Task ReattachUserDataAsync(BaseItem item, CancellationToken cancellationToken) => inner.ReattachUserDataAsync(item, cancellationToken);
    public void DeleteItem(IReadOnlyList<Guid> ids) => inner.DeleteItem(ids);
    public void SaveItems(IReadOnlyList<BaseItem> items, CancellationToken ct) => inner.SaveItems(items, ct);
    public void SaveImages(BaseItem item) => inner.SaveImages(item);
    public BaseItem? RetrieveItem(Guid id) => inner.RetrieveItem(id);
    public int GetCount(InternalItemsQuery filter) => inner.GetCount(filter);
    public ItemCounts GetItemCounts(InternalItemsQuery filter) => inner.GetItemCounts(filter);
    public IReadOnlyList<Guid> GetItemIdsList(InternalItemsQuery filter) => inner.GetItemIdsList(filter);
    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery filter) => inner.GetItemList(filter);
    public IReadOnlyList<BaseItem> GetLatestItemList(InternalItemsQuery filter, CollectionType ct) => inner.GetLatestItemList(filter, ct);
    public IReadOnlyList<string> GetNextUpSeriesKeys(InternalItemsQuery filter, DateTime dateCutoff) => inner.GetNextUpSeriesKeys(filter, dateCutoff);
    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetGenres(InternalItemsQuery filter) => inner.GetGenres(filter);
    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetMusicGenres(InternalItemsQuery filter) => inner.GetMusicGenres(filter);
    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetStudios(InternalItemsQuery filter) => inner.GetStudios(filter);
    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetArtists(InternalItemsQuery filter) => inner.GetArtists(filter);
    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAlbumArtists(InternalItemsQuery filter) => inner.GetAlbumArtists(filter);
    public QueryResult<(BaseItem Item, ItemCounts ItemCounts)> GetAllArtists(InternalItemsQuery filter) => inner.GetAllArtists(filter);
    public IReadOnlyList<string> GetMusicGenreNames() => inner.GetMusicGenreNames();
    public IReadOnlyList<string> GetStudioNames() => inner.GetStudioNames();
    public IReadOnlyList<string> GetGenreNames() => inner.GetGenreNames();
    public IReadOnlyList<string> GetAllArtistNames() => inner.GetAllArtistNames();
    public IReadOnlyDictionary<string, MusicArtist[]> FindArtists(IReadOnlyList<string> artistNames) => inner.FindArtists(artistNames);
    public void UpdateInheritedValues() => inner.UpdateInheritedValues();
    public Task<bool> ItemExistsAsync(Guid id) => inner.ItemExistsAsync(id);
    public bool GetIsPlayed(User user, Guid id, bool recursive) => inner.GetIsPlayed(user, id, recursive);
}
