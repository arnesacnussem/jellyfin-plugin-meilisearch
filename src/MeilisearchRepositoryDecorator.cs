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
    private sealed record SearchResultData(IReadOnlyList<Guid> OrderedIds, int TotalCount);

    public QueryResult<BaseItem> GetItems(InternalItemsQuery filter)
    {
        var searchResult = RunSearch(filter);
        if (ShouldFallbackToInnerSearch(searchResult))
            return inner.GetItems(filter);

        var materialized = MaterializeItems(filter, searchResult!);
        return new QueryResult<BaseItem>(
            filter.StartIndex,
            searchResult!.TotalCount,
            materialized);
    }

    private SearchResultData? RunSearch(InternalItemsQuery filter)
    {
        var searchTerm = filter.SearchTerm;
        var types = BuildTypeList(filter);
        if (string.IsNullOrWhiteSpace(searchTerm)
            || !clientHolder.Ok
            || clientHolder.Index is null
            || types.Count == 0)
        {
            return null;
        }

        // Jellyfin calls into this repository synchronously. Running the external async search
        // on a thread-pool thread reduces sync-over-async deadlock risk at this boundary.
        return Task.Run(() => SearchAsync(searchTerm, filter, types)).GetAwaiter().GetResult();
    }

    private async Task<SearchResultData?> SearchAsync(string searchTerm, InternalItemsQuery filter, IReadOnlyList<string> types)
    {
        var index = clientHolder.Index;
        if (!clientHolder.Ok || index is null)
            return null;

        try
        {
            var matchingStrategy = Plugin.Instance?.Configuration.MatchingStrategy ?? "last";
            var limit = Math.Max(filter.Limit is > 0 ? filter.Limit.Value : 30, 1);
            var typeFilter = string.Join(" OR ", types.Select(t => $"type = \"{t}\""));
            var result = await index.SearchAsync<MeilisearchItem>(
                searchTerm,
                new SearchQuery
                {
                    Filter = typeFilter,
                    Offset = filter.StartIndex ?? 0,
                    Limit = limit,
                    MatchingStrategy = matchingStrategy,
                }
            ).ConfigureAwait(false);

            List<Guid> ids = [];
            foreach (var hit in result.Hits)
            {
                if (Guid.TryParse(hit.Guid, out var id))
                    ids.Add(id);
                else
                    logger.LogWarning("Skipping Meilisearch hit with invalid GUID '{Guid}'", hit.Guid);
            }

            var totalCount = result is SearchResult<MeilisearchItem> searchResult
                ? searchResult.EstimatedTotalHits
                : ids.Count;
            return new SearchResultData(ids, totalCount);
        }
        catch (MeilisearchCommunicationError e)
        {
            logger.LogError(e, "Meilisearch communication error");
            clientHolder.Unset();
            return null;
        }
    }

    private IReadOnlyList<BaseItem> MaterializeItems(InternalItemsQuery filter, SearchResultData searchResult)
    {
        var materializedFilter = CreateMaterializedFilter(filter, searchResult.OrderedIds);
        var items = inner.GetItemList(materializedFilter);
        return ReorderItems(items, searchResult.OrderedIds);
    }

    private static bool ShouldFallbackToInnerSearch(SearchResultData? searchResult)
        => searchResult is null || searchResult.OrderedIds.Count == 0;

    private static InternalItemsQuery CreateMaterializedFilter(InternalItemsQuery filter, IReadOnlyList<Guid> orderedIds)
    {
        return new InternalItemsQuery(filter.User)
        {
            Recursive = filter.Recursive,
            StartIndex = null,
            Limit = null,
            IsFolder = filter.IsFolder,
            IsFavorite = filter.IsFavorite,
            IsFavoriteOrLiked = filter.IsFavoriteOrLiked,
            IsLiked = filter.IsLiked,
            IsPlayed = filter.IsPlayed,
            IsResumable = filter.IsResumable,
            IncludeItemsByName = filter.IncludeItemsByName,
            MediaTypes = filter.MediaTypes,
            IncludeItemTypes = filter.IncludeItemTypes,
            ExcludeItemTypes = filter.ExcludeItemTypes,
            ExcludeTags = filter.ExcludeTags,
            ExcludeInheritedTags = filter.ExcludeInheritedTags,
            IncludeInheritedTags = filter.IncludeInheritedTags,
            Genres = filter.Genres,
            IsSpecialSeason = filter.IsSpecialSeason,
            IsMissing = filter.IsMissing,
            IsUnaired = filter.IsUnaired,
            CollapseBoxSetItems = filter.CollapseBoxSetItems,
            NameStartsWithOrGreater = filter.NameStartsWithOrGreater,
            NameStartsWith = filter.NameStartsWith,
            NameLessThan = filter.NameLessThan,
            NameContains = filter.NameContains,
            MinSortName = filter.MinSortName,
            PresentationUniqueKey = filter.PresentationUniqueKey,
            Path = filter.Path,
            Name = filter.Name,
            UseRawName = filter.UseRawName,
            Person = filter.Person,
            PersonIds = filter.PersonIds,
            ItemIds = [.. orderedIds],
            ExcludeItemIds = filter.ExcludeItemIds,
            AdjacentTo = filter.AdjacentTo,
            PersonTypes = filter.PersonTypes,
            Is3D = filter.Is3D,
            IsHD = filter.IsHD,
            IsLocked = filter.IsLocked,
            IsPlaceHolder = filter.IsPlaceHolder,
            HasImdbId = filter.HasImdbId,
            HasOverview = filter.HasOverview,
            HasTmdbId = filter.HasTmdbId,
            HasOfficialRating = filter.HasOfficialRating,
            HasTvdbId = filter.HasTvdbId,
            HasThemeSong = filter.HasThemeSong,
            HasThemeVideo = filter.HasThemeVideo,
            HasSubtitles = filter.HasSubtitles,
            HasSpecialFeature = filter.HasSpecialFeature,
            HasTrailer = filter.HasTrailer,
            HasParentalRating = filter.HasParentalRating,
            StudioIds = filter.StudioIds,
            GenreIds = filter.GenreIds,
            ImageTypes = filter.ImageTypes,
            VideoTypes = filter.VideoTypes,
            BlockUnratedItems = filter.BlockUnratedItems,
            Years = filter.Years,
            Tags = filter.Tags,
            OfficialRatings = filter.OfficialRatings,
            MinPremiereDate = filter.MinPremiereDate,
            MaxPremiereDate = filter.MaxPremiereDate,
            MinStartDate = filter.MinStartDate,
            MaxStartDate = filter.MaxStartDate,
            MinEndDate = filter.MinEndDate,
            MaxEndDate = filter.MaxEndDate,
            IsAiring = filter.IsAiring,
            IsMovie = filter.IsMovie,
            IsSports = filter.IsSports,
            IsKids = filter.IsKids,
            IsNews = filter.IsNews,
            IsSeries = filter.IsSeries,
            MinIndexNumber = filter.MinIndexNumber,
            MinParentAndIndexNumber = filter.MinParentAndIndexNumber,
            AiredDuringSeason = filter.AiredDuringSeason,
            MinCriticRating = filter.MinCriticRating,
            MinCommunityRating = filter.MinCommunityRating,
            ChannelIds = filter.ChannelIds,
            ParentIndexNumber = filter.ParentIndexNumber,
            ParentIndexNumberNotEquals = filter.ParentIndexNumberNotEquals,
            IndexNumber = filter.IndexNumber,
            MinParentalRating = filter.MinParentalRating,
            MaxParentalRating = filter.MaxParentalRating,
            HasDeadParentId = filter.HasDeadParentId,
            IsVirtualItem = filter.IsVirtualItem,
            ParentId = filter.ParentId,
            ParentType = filter.ParentType,
            AncestorIds = filter.AncestorIds,
            TopParentIds = filter.TopParentIds,
            PresetViews = filter.PresetViews,
            TrailerTypes = filter.TrailerTypes,
            SourceTypes = filter.SourceTypes,
            SeriesStatuses = filter.SeriesStatuses,
            ExternalSeriesId = filter.ExternalSeriesId,
            ExternalId = filter.ExternalId,
            AlbumIds = filter.AlbumIds,
            ArtistIds = filter.ArtistIds,
            ExcludeArtistIds = filter.ExcludeArtistIds,
            AncestorWithPresentationUniqueKey = filter.AncestorWithPresentationUniqueKey,
            SeriesPresentationUniqueKey = filter.SeriesPresentationUniqueKey,
            GroupByPresentationUniqueKey = filter.GroupByPresentationUniqueKey,
            GroupBySeriesPresentationUniqueKey = filter.GroupBySeriesPresentationUniqueKey,
            EnableTotalRecordCount = false,
            ForceDirect = filter.ForceDirect,
            ExcludeProviderIds = filter.ExcludeProviderIds,
            EnableGroupByMetadataKey = filter.EnableGroupByMetadataKey,
            HasChapterImages = filter.HasChapterImages,
            OrderBy = filter.OrderBy,
            MinDateCreated = filter.MinDateCreated,
            MinDateLastSaved = filter.MinDateLastSaved,
            MinDateLastSavedForUser = filter.MinDateLastSavedForUser,
            DtoOptions = filter.DtoOptions,
            HasNoAudioTrackWithLanguage = filter.HasNoAudioTrackWithLanguage,
            HasNoInternalSubtitleTrackWithLanguage = filter.HasNoInternalSubtitleTrackWithLanguage,
            HasNoExternalSubtitleTrackWithLanguage = filter.HasNoExternalSubtitleTrackWithLanguage,
            HasNoSubtitleTrackWithLanguage = filter.HasNoSubtitleTrackWithLanguage,
            IsDeadArtist = filter.IsDeadArtist,
            IsDeadStudio = filter.IsDeadStudio,
            IsDeadGenre = filter.IsDeadGenre,
            IsDeadPerson = filter.IsDeadPerson,
            DisplayAlbumFolders = filter.DisplayAlbumFolders,
            HasAnyProviderId = filter.HasAnyProviderId,
            AlbumArtistIds = filter.AlbumArtistIds,
            BoxSetLibraryFolders = filter.BoxSetLibraryFolders,
            ContributingArtistIds = filter.ContributingArtistIds,
            HasAired = filter.HasAired,
            HasOwnerId = filter.HasOwnerId,
            Is4K = filter.Is4K,
            MaxHeight = filter.MaxHeight,
            MaxWidth = filter.MaxWidth,
            MinHeight = filter.MinHeight,
            MinWidth = filter.MinWidth,
            SearchTerm = null,
            SeriesTimerId = filter.SeriesTimerId,
            SkipDeserialization = filter.SkipDeserialization
        };
    }

    private static IReadOnlyList<BaseItem> ReorderItems(IReadOnlyList<BaseItem> items, IReadOnlyList<Guid> orderedIds)
    {
        var itemsById = items.ToDictionary(item => item.Id);
        var orderedItems = new List<BaseItem>(items.Count);
        foreach (var id in orderedIds)
        {
            if (itemsById.TryGetValue(id, out var item))
                orderedItems.Add(item);
        }

        return orderedItems;
    }

    private static List<string> BuildTypeList(InternalItemsQuery filter)
    {
        List<string> types;
        if (filter.IncludeItemTypes is { Length: > 0 })
        {
            types = TypeHelper.MapTypeKeys(filter.IncludeItemTypes).ToList();
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
    public int GetCount(InternalItemsQuery filter)
    {
        var searchResult = RunSearch(filter);
        return ShouldFallbackToInnerSearch(searchResult)
            ? inner.GetCount(filter)
            : searchResult!.TotalCount;
    }
    public ItemCounts GetItemCounts(InternalItemsQuery filter) => inner.GetItemCounts(filter);
    public IReadOnlyList<Guid> GetItemIdsList(InternalItemsQuery filter) => inner.GetItemIdsList(filter);
    public IReadOnlyList<BaseItem> GetItemList(InternalItemsQuery filter)
    {
        var searchResult = RunSearch(filter);
        return ShouldFallbackToInnerSearch(searchResult)
            ? inner.GetItemList(filter)
            : MaterializeItems(filter, searchResult!);
    }
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
