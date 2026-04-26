using System.Collections.Frozen;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Playlists;

namespace Jellyfin.Plugin.Meilisearch;

public static class TypeHelper
{
    public static IReadOnlyDictionary<string, string> JellyfinTypeMap { get; } = new Dictionary<string, string>()
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
}
