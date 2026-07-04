using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Meilisearch;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Meilisearch;

/// <summary>
/// Implements <see cref="IExternalSearchProvider"/> using Meilisearch for fast, typo-tolerant search.
/// </summary>
public class MeilisearchSearchProvider : IExternalSearchProvider
{
    private readonly MeilisearchClientHolder _clientHolder;
    private readonly ILogger<MeilisearchSearchProvider> _logger;

    public MeilisearchSearchProvider(
        MeilisearchClientHolder clientHolder,
        ILogger<MeilisearchSearchProvider> logger)
    {
        _clientHolder = clientHolder;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Meilisearch";

    /// <inheritdoc />
    public MetadataPluginType Type => MetadataPluginType.SearchProvider;

    /// <inheritdoc />
    public int Priority => 10; // Lower number = higher priority; runs before the built-in SQL provider (100)

    /// <inheritdoc />
    public bool CanSearch(SearchProviderQuery query) => _clientHolder.Ok;

    /// <inheritdoc />
    public async IAsyncEnumerable<SearchResult> SearchAsync(
        SearchProviderQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!_clientHolder.Ok || _clientHolder.Index is null)
        {
            yield break;
        }

        var filter = BuildFilter(query);
        var limit = query.Limit ?? 50;
        var matchingStrategy = Plugin.Instance?.Configuration.MatchingStrategy ?? "last";

        IReadOnlyList<(Guid Id, float Score)> results;
        try
        {
            results = await SearchInternalAsync(
                query.SearchTerm, limit, filter, matchingStrategy, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Meilisearch search failed for term '{SearchTerm}'", query.SearchTerm);
            yield break;
        }

        foreach (var (id, score) in results)
        {
            yield return new SearchResult(id, score);
        }
    }

    /// <inheritdoc />
    async Task<IReadOnlyList<SearchResult>> ISearchProvider.SearchAsync(
        SearchProviderQuery query,
        CancellationToken cancellationToken)
    {
        var list = new List<SearchResult>();
        await foreach (var result in SearchAsync(query, cancellationToken).ConfigureAwait(false))
        {
            list.Add(result);
        }

        return list;
    }

    private async Task<IReadOnlyList<(Guid Id, float Score)>> SearchInternalAsync(
        string searchTerm,
        int limit,
        string? filter,
        string matchingStrategy,
        CancellationToken cancellationToken)
    {
        var index = _clientHolder.Index!;
        var searchQuery = new SearchQuery
        {
            Filter = filter,
            Limit = limit,
            AttributesToRetrieve = ["guid"],
            ShowRankingScore = true,
            MatchingStrategy = matchingStrategy,
        };

        var result = await index.SearchAsync<MeilisearchSearchHit>(
            searchTerm, searchQuery, cancellationToken).ConfigureAwait(false);

        var results = new List<(Guid, float)>(result.Hits.Count);
        foreach (var hit in result.Hits)
        {
            if (Guid.TryParse(hit.Guid, out var guid) && guid != Guid.Empty)
            {
                // Meilisearch ranking scores are [0.0, 1.0]; multiply to a [0, 100] scale.
                results.Add((guid, (float)(hit.RankingScore * 100.0)));
            }
        }

        return results;
    }

    private static string? BuildFilter(SearchProviderQuery query)
    {
        var parts = new List<string>();

        var includeTypes = ResolveTypes(query);
        if (includeTypes.Count > 0)
        {
            // Escape and join as: (type = "A" OR type = "B")
            var typeConditions = new List<string>(includeTypes.Count);
            foreach (var t in includeTypes)
            {
                typeConditions.Add($"type = \"{t}\"");
            }

            parts.Add($"({string.Join(" OR ", typeConditions)})");
        }
        else if (query.ExcludeItemTypes.Length > 0)
        {
            // No include constraints — emit explicit != clauses for each excluded type.
            foreach (var typeName in TypeHelper.MapTypeKeys(query.ExcludeItemTypes))
            {
                parts.Add($"type != \"{typeName}\"");
            }
        }

        if (query.ParentId.HasValue && query.ParentId.Value != Guid.Empty)
        {
            parts.Add($"parentId = \"{query.ParentId.Value}\"");
        }

        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }

    private static IReadOnlyList<string> ResolveTypes(SearchProviderQuery query)
    {
        var kinds = new HashSet<BaseItemKind>();

        foreach (var kind in query.IncludeItemTypes)
        {
            kinds.Add(kind);
        }

        foreach (var mediaType in query.MediaTypes)
        {
            foreach (var kind in MapMediaTypeToItemKinds(mediaType))
            {
                kinds.Add(kind);
            }
        }

        if (kinds.Count > 0)
        {
            foreach (var excluded in query.ExcludeItemTypes)
            {
                kinds.Remove(excluded);
            }
        }

        return new List<string>(TypeHelper.MapTypeKeys(kinds));
    }

    private static BaseItemKind[] MapMediaTypeToItemKinds(MediaType mediaType) =>
        mediaType switch
        {
            MediaType.Video => [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video, BaseItemKind.MusicVideo],
            MediaType.Audio => [BaseItemKind.Audio, BaseItemKind.MusicAlbum, BaseItemKind.MusicArtist],
            MediaType.Photo => [BaseItemKind.Photo],
            MediaType.Book => [BaseItemKind.Book, BaseItemKind.AudioBook],
            _ => []
        };
}
