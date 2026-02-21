using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Meilisearch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using Index = Meilisearch.Index;

namespace Jellyfin.Plugin.Meilisearch;

// ReSharper disable once ClassNeverInstantiated.Global
public class MeilisearchMutateFilter(
    MeilisearchClientHolder ch,
    ILogger<MeilisearchMutateFilter> logger,
    ILibraryManager libraryManager,
    IUserManager userManager,
    IDtoService dtoService
) : IAsyncActionFilter
{
    // Build the Jellyfin type map dynamically
    private IReadOnlyDictionary<string, string> JellyfinTypeMap { get; } = new Dictionary<string, string>()
    {
        { "AggregateFolder", typeof(AggregateFolder).FullName! },
        { "Audio", typeof(Audio).FullName! },
        { "AudioBook", typeof(AudioBook).FullName! },
        { "BasePluginFolder", typeof(BasePluginFolder).FullName! },
        { "Book", typeof(Book).FullName! },
        { "BoxSet", typeof(BoxSet).FullName! },
        { "Channel", typeof(Channel).FullName! },
        { "CollectionFolder", typeof(CollectionFolder).FullName! },
        { "Episode", typeof(Episode).FullName! },
        { "Folder", typeof(Folder).FullName! },
        { "Genre", typeof(Genre).FullName! },
        { "Movie", typeof(Movie).FullName! },
        { "LiveTvChannel", typeof(LiveTvChannel).FullName! },
        { "LiveTvProgram", typeof(LiveTvProgram).FullName! },
        { "MusicAlbum", typeof(MusicAlbum).FullName! },
        { "MusicArtist", typeof(MusicArtist).FullName! },
        { "MusicGenre", typeof(MusicGenre).FullName! },
        { "MusicVideo", typeof(MusicVideo).FullName! },
        { "Person", typeof(Person).FullName! },
        { "Photo", typeof(Photo).FullName! },
        { "PhotoAlbum", typeof(PhotoAlbum).FullName! },
        { "Playlist", typeof(Playlist).FullName! },
        { "PlaylistsFolder", "Emby.Server.Implementations.Playlists.PlaylistsFolder" },
        { "Season", typeof(Season).FullName! },
        { "Series", typeof(Series).FullName! },
        { "Studio", typeof(Studio).FullName! },
        { "Trailer", typeof(Trailer).FullName! },
        { "TvChannel", typeof(LiveTvChannel).FullName! },
        { "TvProgram", typeof(LiveTvProgram).FullName! },
        { "UserRootFolder", typeof(UserRootFolder).FullName! },
        { "UserView", typeof(UserView).FullName! },
        { "Video", typeof(Video).FullName! },
        { "Year", typeof(Year).FullName! }
    }.ToFrozenDictionary();

    /// <summary>
    /// check if the user is api key https://github.com/jellyfin/jellyfin/blob/master/Jellyfin.Api/Extensions/ClaimsPrincipalExtensions.cs #GetIsApiKey
    /// </summary>
    /// <param name="context">The action context</param>
    /// <param name="next">The action execution delegate</param>
    /// <returns>bool if the user is api-key</returns>
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.GetIsApiKey())
        {
            // we not handle the request if user is authenticated by api key
            await next();
            return;
        }

        var user = context.GetUser(userManager);
        if (user is null)
        {
            // we not handle the request if user is null
            await next();
            return;
        }

        var path = context.HttpContext.Request.Path.ToString();
        logger.LogDebug("path={path} query={query}", path, context.HttpContext.Request.QueryString);

        var searchTerm = context.GetSearchTerm();
        if (!string.IsNullOrEmpty(searchTerm))
        {
            logger.LogDebug("path={path} searchTerm={searchTerm}", path, searchTerm);
            var stopwatch = Stopwatch.StartNew();
            var result = await Mutate(context, user, searchTerm);
            stopwatch.Stop();
            Plugin.Instance?.UpdateAverageSearchTime(stopwatch.ElapsedMilliseconds);

            if (result.SkipNext)
            {
                context.HttpContext.Response.Headers.Add(new KeyValuePair<string, StringValues>(
                    "x-meilisearch-result",
                    $"{stopwatch.ElapsedMilliseconds}ms, count={result.Count}, skip={result.SkipNext}"));
                return;
            }
        }

        await next();
    }


    private async Task<IReadOnlyCollection<MeilisearchItem>> Search(
        Index index,
        string searchTerm,
        List<string> itemTypes,
        List<KeyValuePair<string, string>> additionalFilters,
        int limitPerType
    )
    {
        List<MeilisearchItem> items = [];
        try
        {
            var additionQuery = additionalFilters.Select(it => $"{it.Key} = {it.Value}").ToList();
            var additionQueryStr = string.Join(" AND ", additionQuery);
            var itemTypeFilter = itemTypes.Select(it => $"type = \"{it}\"").ToList();
            var itemTypeFilterStr = string.Join(" OR ", itemTypeFilter);

            var filter = "";
            if(additionQuery.Count > 0 && itemTypes.Count > 0) filter = $"({itemTypeFilterStr}) AND {additionQueryStr}";
            else if(additionQuery.Count > 0) filter = additionQueryStr;
            else if(itemTypes.Count > 0) filter = itemTypeFilterStr;

            var results = await index.SearchAsync<MeilisearchItem>(
                searchTerm,
                new SearchQuery
                {
                    Filter = filter,
                    Limit = limitPerType,
                    AttributesToSearchOn = Plugin.Instance?.Configuration.AttributesToSearchOn
                }
            );
            items.AddRange(results.Hits);
        }
        catch (MeilisearchCommunicationError e)
        {
            logger.LogError(e, "Meilisearch communication error");
            ch.Unset();
        }

        return items;
    }


    /// <summary>
    ///     Mutates the current search request context by overriding the ids with the results of the Meilisearch query.
    ///     This part code now is somewhat copied or adapted from Jellysearch.
    /// </summary>
    /// <param name="context">The action context.</param>
    /// <param name="user">User who doing the search</param>
    /// <param name="searchTerm">The search term.</param>
    /// <remarks>
    ///     If the search term is empty, or if there are no results, the method does nothing.
    /// </remarks>
    /// <returns>A task representing the asynchronous operation.</returns>
    private async Task<MutateResult> Mutate(ActionExecutingContext context, User user, string searchTerm)
    {
        if (!ch.Ok || ch.Index == null)
        {
            logger.LogWarning(
                "Meilisearch is not configured or unable to connect, skipping search mutation, will fallback to Jellyfin");
            Plugin.Instance?.TryCreateMeilisearchClient(false);
            return new MutateResult(false, 0);
        }

        var filteredTypes = new List<string>();
        var additionalFilters = new List<KeyValuePair<string, string>>();

        // includeItemTypes add types from the search
        var includeItemTypes = context.GetQueryCommaOrMulti("includeItemTypes");
        logger.LogDebug("includeItemTypes={includeItemTypes}", string.Join(", ", includeItemTypes));
        filteredTypes.AddRange(ToJellyfinTypes(includeItemTypes));

        // excludeItemTypes remove types from the search
        var excludeItemTypes = context.GetQueryCommaOrMulti("excludeItemTypes");
        logger.LogDebug("excludeItemTypes={excludeItemTypes}", string.Join(", ", excludeItemTypes));
        ToJellyfinTypes(excludeItemTypes).ToImmutableList().ForEach(it => filteredTypes.Remove(it));


        // mediaTypes add types from the search
        var mediaTypes = context.GetQueryCommaOrMulti("mediaTypes");
        logger.LogDebug("mediaTypes={mediaTypes}", string.Join(", ", mediaTypes));
        if (mediaTypes != null && mediaTypes.Count > 0)
        {
            // If mediaTypes is set, we only search for those types
            filteredTypes.AddRange(ToJellyfinTypes(mediaTypes));
        }
        else
        {
            var path = context.HttpContext.Request.Path.ToString();
            // Handle direct endpoints and their types
            if (path.EndsWith("/Persons", true, CultureInfo.InvariantCulture))
            {
                filteredTypes.Add(JellyfinTypeMap["Person"]);
            }
            else if (path.EndsWith("/Artists", true, CultureInfo.InvariantCulture))
            {
                filteredTypes.Add(JellyfinTypeMap["MusicArtist"]);
            }
            else if (path.EndsWith("/AlbumArtists", true, CultureInfo.InvariantCulture))
            {
                // Album artists are marked as folder
                filteredTypes.Add(JellyfinTypeMap["MusicArtist"]);
                additionalFilters.Add(new KeyValuePair<string, string>("isFolder", "true"));
            }
            else if (path.EndsWith("/Genres", true, CultureInfo.InvariantCulture))
            {
                filteredTypes.Add(JellyfinTypeMap["Genre"]); // TODO: Handle genre search properly
            }
        }

        // Default to common types if no types were specified
        if (filteredTypes.Count == 0)
        {
            // Use TryGetValue for safer access to JellyfinTypeMap
            var defaultTypeKeys = new[] { "Movie", "Episode", "Series", "Person" };
            foreach (var key in defaultTypeKeys)
            {
                if (JellyfinTypeMap.TryGetValue(key, out var typeValue))
                {
                    filteredTypes.Add(typeValue);
                }
                else
                {
                    logger.LogWarning("JellyfinTypeMap is missing expected key: {key}", key);
                }
            }
        }

        var limit = context.ActionArguments.TryGetValue("limit", out var limitObj)
            ? (int)limitObj!
            : 0;
        var limitPreItem = filteredTypes.Count > 0 && limit > 0
            ? Math.Clamp(limit / filteredTypes.Count, 30, 100)
            : 30;
        var meilisearchItems = await Search(ch.Index, searchTerm, filteredTypes, additionalFilters, limitPreItem);

        // remove items that are not visible to the user
        var items = meilisearchItems.Select(it =>
        {
            var item = libraryManager.GetItemById(Guid.Parse(it.Guid));
            return (item?.IsVisibleStandalone(user) ?? false) ? item : null;
        }).Where(it => it is not null)!.ToImmutableList<BaseItem>();


        var notFallback = !(Plugin.Instance?.Configuration.FallbackToJellyfin ?? false);
        if (items.Count > 0 || notFallback)
        {
            SetQueryResult(context, user, items);
            return new MutateResult(true, items.Count);
        }

        logger.LogDebug("Not mutate request: results={hits}, fallback={fallback}", items.Count, !notFallback);
        return new MutateResult(notFallback, items.Count);
    }

    private IEnumerable<string> ToJellyfinTypes(IEnumerable<string> types)
    {
        foreach (var type in types)
        {
            if (JellyfinTypeMap.TryGetValue(type, out var jellyfinType))
            {
                yield return jellyfinType;
            }
            else
            {
                logger.LogWarning("ToJellyfinTypes: no mapping for '{mediaType}'", type);
            }
        }
    }

    private record MutateResult(bool SkipNext, int Count);

    private void SetQueryResult(ActionExecutingContext context, User user, ImmutableList<BaseItem> items)
    {
        var fields = context.ActionArguments.TryGetValue("fields", out var fieldsObj)
            ? (ItemFields[])fieldsObj!
            : [];
        if (user.GetPreference(PreferenceKind.AllowedTags).Length != 0 && !fields.Contains(ItemFields.Tags))
        {
            fields = [..fields, ItemFields.Tags];
        }

        // ReSharper disable once SimplifyConditionalTernaryExpression for readability
        var enableImages = context.ActionArguments.TryGetValue("enableImages", out var enableImagesObj)
            ? (bool)enableImagesObj!
            : true;
        var enableUserData = context.ActionArguments.TryGetValue("enableUserData", out var enableUserDataObj)
            ? (bool?)enableUserDataObj
            : null;
        var imageTypeLimit = context.ActionArguments.TryGetValue("imageTypeLimit", out var imageTypeLimitObj)
            ? (int?)imageTypeLimitObj
            : null;
        var enableImageTypes = context.ActionArguments.TryGetValue("enableImageTypes", out var enableImageTypesObj)
            ? (ImageType[])enableImageTypesObj!
            : [];
        var dtoOptions = new DtoOptions { Fields = fields }
            .AddClientFields(context)
            .AddAdditionalDtoOptions(enableImages, enableUserData, imageTypeLimit, enableImageTypes);
        context.Result = new OkObjectResult(new QueryResult<BaseItemDto>
        {
            Items = dtoService.GetBaseItemDtos(items, dtoOptions),
            TotalRecordCount = items.Count,
            StartIndex = 0
        });
    }
}

public static class ActionExecutingContextExtensions
{
    public static string? GetUserClaimValue(this ActionExecutingContext context, string name) =>
        context.HttpContext.User.Claims
            .FirstOrDefault(claim => claim.Type.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    public static bool GetIsApiKey(this ActionExecutingContext context) =>
        bool.TryParse(context.GetUserClaimValue("Jellyfin-IsApiKey"), out var parsedClaimValue) && parsedClaimValue;

    public static User? GetUser(this ActionExecutingContext context, IUserManager userManager)
    {
        var userId = context.ActionArguments.TryGetValue("userId", out var value) ? (Guid?)value : null;
        var claimUserId = context.GetUserClaimValue("Jellyfin-UserId");
        var claimUserGuid = string.IsNullOrEmpty(claimUserId) ? Guid.Empty : Guid.Parse(claimUserId);

        // UserId not provided, fall back to authenticated user id.
        if (userId.IsNullOrEmpty())
        {
            return userManager.GetUserById(claimUserGuid);
        }

        // User must be administrator to access another user.
        var isAdministrator = context.HttpContext.User.IsInRole("Administrator");
        if (!userId.Value.Equals(claimUserGuid) && !isAdministrator)
        {
            throw new SecurityException("Forbidden");
        }

        return userManager.GetUserById(userId.Value);
    }

    /// should be ["/Users/{userId}/Items", "/Persons", "/Artists/AlbumArtists", "/Artists", "/Genres"];
    /// but now only /Items
    private static readonly Collection<string> MatchingPaths = ["/Items"];


    public static string? GetSearchTerm(this ActionExecutingContext context)
    {
        if (!MatchingPaths.Contains(context.HttpContext.Request.Path)) return null;
        if (!context.ActionArguments.TryGetValue("searchTerm", out var searchTermObj)) return null;
        var searchTerm = (string?)searchTermObj;
        return searchTerm is not { Length: > 0 } ? null : searchTerm;
    }

    /// <summary>
    /// Parse a query parameter that may contain comma delimited values or multiple values.
    /// </summary>
    /// <param name="context">The HttpContext extension.</param>
    /// <param name="key">The query parameter name.</param>
    /// <returns>The list of values.</returns>
    public static ImmutableList<string> GetQueryCommaOrMulti(this ActionExecutingContext context, string key)
    {
        if (!context.HttpContext.Request.Query.TryGetValue(key, out var values) || StringValues.IsNullOrEmpty(values))
            return ImmutableList<string>.Empty; // no values
        var types = values.SelectMany(it =>
            it?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []);
        return types.ToImmutableList();
    }
}

/// <summary>
/// copy from https://github.com/jellyfin/jellyfin/blob/master/Jellyfin.Api/Extensions/DtoExtensions.cs
/// </summary>
public static class DtoExtensions
{
    internal static DtoOptions AddClientFields(
        this DtoOptions dtoOptions, ActionExecutingContext context)
    {
        var client = context.GetUserClaimValue("Jellyfin-Client");

        // No client in claim
        if (string.IsNullOrEmpty(client))
        {
            return dtoOptions;
        }

        if (!dtoOptions.ContainsField(ItemFields.RecursiveItemCount))
        {
            if (client.Contains("kodi", StringComparison.OrdinalIgnoreCase) ||
                client.Contains("wmc", StringComparison.OrdinalIgnoreCase) ||
                client.Contains("media center", StringComparison.OrdinalIgnoreCase) ||
                client.Contains("classic", StringComparison.OrdinalIgnoreCase))
            {
                dtoOptions.Fields = [..dtoOptions.Fields, ItemFields.RecursiveItemCount];
            }
        }

        if (dtoOptions.ContainsField(ItemFields.ChildCount)) return dtoOptions;
        if (client.Contains("kodi", StringComparison.OrdinalIgnoreCase) ||
            client.Contains("wmc", StringComparison.OrdinalIgnoreCase) ||
            client.Contains("media center", StringComparison.OrdinalIgnoreCase) ||
            client.Contains("classic", StringComparison.OrdinalIgnoreCase) ||
            client.Contains("roku", StringComparison.OrdinalIgnoreCase) ||
            client.Contains("samsung", StringComparison.OrdinalIgnoreCase) ||
            client.Contains("androidtv", StringComparison.OrdinalIgnoreCase))
        {
            dtoOptions.Fields = [..dtoOptions.Fields, ItemFields.ChildCount];
        }

        return dtoOptions;
    }

    internal static DtoOptions AddAdditionalDtoOptions(
        this DtoOptions dtoOptions,
        bool? enableImages,
        bool? enableUserData,
        int? imageTypeLimit,
        IReadOnlyList<ImageType> enableImageTypes)
    {
        dtoOptions.EnableImages = enableImages ?? true;

        if (imageTypeLimit.HasValue)
        {
            dtoOptions.ImageTypeLimit = imageTypeLimit.Value;
        }

        if (enableUserData.HasValue)
        {
            dtoOptions.EnableUserData = enableUserData.Value;
        }

        if (enableImageTypes.Count != 0)
        {
            dtoOptions.ImageTypes = enableImageTypes;
        }

        return dtoOptions;
    }
}
