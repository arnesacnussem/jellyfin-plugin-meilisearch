using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
    // After a timeout, searches skip Meilisearch for this long, so an outage costs one slow search per pause
    // instead of every search waiting for the full timeout.
    private static readonly TimeSpan TimeoutPause = TimeSpan.FromSeconds(30);
    private static readonly string[] PersonSearchAttributes = ["name", "sortName", "originalTitle"];
    private static readonly string PersonTypeKey = TypeHelper.MapTypeKeys([BaseItemKind.Person]).Single();

    private readonly MeilisearchClientHolder _clientHolder;
    private readonly ILogger<MeilisearchSearchProvider> _logger;
    private long _pausedUntilTicks;

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
    public bool CanSearch(SearchProviderQuery query)
    {
        if (!_clientHolder.Ok)
        {
            // Nothing else reconnects while the library is quiet, so searches ask for it.
            _clientHolder.RequestReconnect("search while disconnected");
            return false;
        }

        return Environment.TickCount64 >= Interlocked.Read(ref _pausedUntilTicks);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SearchResult> SearchAsync(
        SearchProviderQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!CanSearch(query) || _clientHolder.Index is null)
        {
            yield break;
        }

        var filter = BuildFilter(query);
        var searchPeopleByName = Plugin.Instance?.Configuration.SearchPeopleByNameOnly ?? false;
        var attributesToSearchOn = searchPeopleByName ? ResolveAttributesToSearchOn(query) : null;
        var limit = query.Limit ?? 50;
        var matchingStrategy = Plugin.Instance?.Configuration.MatchingStrategy ?? "last";

        IReadOnlyList<(Guid Id, float Score)> results;
        try
        {
            results = await SearchInternalAsync(
                query.SearchTerm, limit, filter, attributesToSearchOn, matchingStrategy, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            Interlocked.Exchange(ref _pausedUntilTicks, Environment.TickCount64 + (long)TimeoutPause.TotalMilliseconds);
            _logger.LogError(
                ex,
                "Meilisearch search timed out for term '{SearchTerm}'; skipping Meilisearch for {Pause} s",
                query.SearchTerm,
                TimeoutPause.TotalSeconds);
            yield break;
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
        IEnumerable<string>? attributesToSearchOn,
        string matchingStrategy,
        CancellationToken cancellationToken)
    {
        var index = _clientHolder.Index!;
        var searchQuery = new SearchQuery
        {
            Filter = filter,
            AttributesToSearchOn = attributesToSearchOn,
            Limit = limit,
            AttributesToRetrieve = ["guid"],
            ShowRankingScore = true,
            MatchingStrategy = matchingStrategy,
        };

        // Searches take milliseconds. Without a limit, a Meilisearch that is down or stuck holds every search for the
        // HTTP client's 100 s default, even when Jellyfin's own search has already answered. The limit stays below that
        // default, so the timeout reported is always the one that ended the search.
        var timeoutSeconds = Plugin.Instance?.Configuration.GetEffectiveSearchTimeoutSeconds()
                             ?? Config.DefaultSearchTimeoutSeconds;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        var stopwatch = Stopwatch.StartNew();
        ISearchable<MeilisearchSearchHit> result;
        try
        {
            result = await index.SearchAsync<MeilisearchSearchHit>(
                searchTerm, searchQuery, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
            when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Meilisearch did not answer within {timeoutSeconds} s", ex);
        }
        finally
        {
            // Failed and timed-out searches count too, so the average shows an outage. Searches the caller
            // cancelled are left out, since their time says nothing about Meilisearch.
            stopwatch.Stop();
            if (!cancellationToken.IsCancellationRequested)
            {
                Plugin.Instance?.UpdateAverageSearchTime(stopwatch.ElapsedMilliseconds);
            }
        }

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
            // The whole subtree, not only direct children: an episode is under its season, series and library.
            parts.Add($"ancestorIds = \"{query.ParentId.Value}\"");
        }

        return parts.Count == 0 ? null : string.Join(" AND ", parts);
    }

    /// <summary>
    /// Restricts a search for people alone to the name fields. A person's overview is their biography, so searching it
    /// matches anyone whose biography contains the term ("one of seven children") instead of people with that name.
    /// </summary>
    private static string[]? ResolveAttributesToSearchOn(SearchProviderQuery query)
    {
        var types = ResolveTypes(query);
        if (types.Count > 0 && types.All(t => t == PersonTypeKey))
        {
            return PersonSearchAttributes;
        }

        return null;
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
