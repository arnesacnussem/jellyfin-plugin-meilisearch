using Meilisearch;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Index = Meilisearch.Index;

namespace Jellyfin.Plugin.Meilisearch;

/**
 * Following code is somewhat copy-pasted or adapted from Jellysearch.
 */
public class DbIndexer(MeilisearchClientHolder clientHolder, ILogger<DbIndexer> logger) : Indexer(clientHolder, logger)
{
    // private readonly string[] _bitField = ["IsFolder"];
    //
    // private readonly string[] _floatField = ["CommunityRating", "CriticRating"];
    //
    // private readonly string[] _guidField = ["guid"];
    // private readonly string[] _intField = ["ProductionYear"];
    //
    // private readonly string[] _textArrayField =
    // [
    //     "Genres",
    //     "Studios",
    //     "Tags",
    //     "Artists",
    //     "AlbumArtists"
    // ];
    //
    // private readonly string[] _textField =
    // [
    //     "type",
    //     "ParentId",
    //     "Name",
    //     "Overview",
    //     "OriginalTitle",
    //     "SeriesName",
    // ];

    public override DateTimeOffset? LastIndex { get; protected set; }
    public override long LastIndexCount { get; protected set; }


    protected override async Task IndexInternal(MeilisearchClient meilisearch, Index index)
    {
        var dbPath = Plugin.Instance?.DbPath;
        logger.LogInformation("Indexing items from database: {DB}", dbPath);

        // Open Jellyfin library
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString());
        await connection.OpenAsync();

        // Query all base items
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT guid, type, ParentId, CommunityRating, Name, Overview, ProductionYear, Genres, Studios, Tags, IsFolder, CriticRating, OriginalTitle, SeriesName, Artists, AlbumArtists, Path FROM TypedBaseItems";

        await using var reader = await command.ExecuteReaderAsync();
        var items = new List<MeilisearchItem>();
        while (await reader.ReadAsync())
        {
            var item = new MeilisearchItem(
                reader.GetGuid(0).ToString(),
                !reader.IsDBNull(1) ? reader.GetString(1) : null,
                !reader.IsDBNull(2) ? reader.GetString(2) : null,
                CommunityRating: !reader.IsDBNull(3) ? reader.GetDouble(3) : null,
                Name: !reader.IsDBNull(4) ? reader.GetString(4) : null,
                Overview: !reader.IsDBNull(5) ? reader.GetString(5) : null,
                ProductionYear: !reader.IsDBNull(6) ? reader.GetInt32(6) : null,
                Genres: !reader.IsDBNull(7) ? reader.GetString(7).Split('|') : null,
                Studios: !reader.IsDBNull(8) ? reader.GetString(8).Split('|') : null,
                Tags: !reader.IsDBNull(9) ? reader.GetString(9).Split('|') : null,
                IsFolder: !reader.IsDBNull(10) ? reader.GetBoolean(10) : null,
                CriticRating: !reader.IsDBNull(11) ? reader.GetDouble(11) : null,
                OriginalTitle: !reader.IsDBNull(12) ? reader.GetString(12) : null,
                SeriesName: !reader.IsDBNull(13) ? reader.GetString(13) : null,
                Artists: !reader.IsDBNull(14) ? reader.GetString(14).Split('|') : null,
                AlbumArtists: !reader.IsDBNull(15) ? reader.GetString(15).Split('|') : null,
                Path: !reader.IsDBNull(16) ? reader.GetString(16) : null
            );
            if (item.Path?[0] == '%') item = item with { Path = null };
            items.Add(item);
        }

        if (items.Count <= 0)
        {
            logger.LogInformation("No items to index");
            return;
        }

        await index.AddDocumentsInBatchesAsync(items, 5000, "guid");
        logger.LogInformation("Upload {COUNT} items to Meilisearch", items.Count);
        LastIndex = DateTimeOffset.Now;
        LastIndexCount = items.Count;
    }
}