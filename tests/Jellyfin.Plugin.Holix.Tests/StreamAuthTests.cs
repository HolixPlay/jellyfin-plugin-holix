using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Holix.Ratings;
using Jellyfin.Plugin.Holix.StreamAuth;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.Holix.Tests;

public class StreamPathTests
{
    private const string Id = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";
    private static readonly Guid Expected = Guid.ParseExact(Id, "N");

    [Theory]
    [InlineData("/Videos/" + Id + "/stream")]
    [InlineData("/Videos/" + Id + "/stream.mkv?static=true&api_key=x")]
    [InlineData("/videos/" + Id + "/STREAM.MP4")]
    [InlineData("/Videos/0a1b2c3d-4e5f-6071-8293-a4b5c6d7e8f9/stream")]
    [InlineData("/Videos/0A1B2C3D-4E5F-6071-8293-A4B5C6D7E8F9/master.m3u8?MediaSourceId=x")]
    [InlineData("/Videos/" + Id + "/main.m3u8")]
    [InlineData("/Videos/" + Id + "/hls1/main/0.mp4?runtimeTicks=0")]
    [InlineData("/Videos/" + Id + "/hls1/main/12.ts")]
    [InlineData("/Videos/" + Id + "/" + Id + "/Subtitles/2/0/Stream.vtt")]
    [InlineData("/Videos/" + Id + "/ffffffffffffffffffffffffffffffff/Attachments/1")]
    [InlineData("/Audio/" + Id + "/stream")]
    [InlineData("/Audio/" + Id + "/stream.flac")]
    [InlineData("/audio/" + Id + "/Universal?UserId=x")]
    [InlineData("/Audio/" + Id + "/hls1/main/3.aac")]
    [InlineData("/jellyfin/Videos/" + Id + "/stream")]
    [InlineData("https://jf.example.com/Videos/" + Id + "/stream?api_key=x")]
    [InlineData("/Videos/" + Id + "%2Fstream")]
    public void Stream_paths_are_recognised(string path)
    {
        Assert.True(StreamPath.TryGetItemId(path, out var id));
        Assert.Equal(Expected, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/Holix/StreamAuth")]
    [InlineData("/Videos/" + Id)]
    [InlineData("/Videos/" + Id + "/AdditionalParts")]
    [InlineData("/Videos/not-a-guid/stream")]
    [InlineData("/Videos/" + Id + "/streamer")]
    [InlineData("/Videos/" + Id + "/stream.")]
    [InlineData("/Audio/" + Id + "/Lyrics")]
    [InlineData("/Audio/" + Id + "/other/thing")]
    [InlineData("/Items/" + Id + "/Images/Primary")]
    [InlineData("/Users/" + Id + "/Items/" + Id)]
    [InlineData("/web/index.html")]
    public void Other_paths_are_not(string? path)
    {
        Assert.False(StreamPath.TryGetItemId(path, out _));
    }
}

public class StreamTokenReaderTests
{
    private static HttpRequest Request(string uri, params (string Name, string Value)[] headers)
    {
        var ctx = new DefaultHttpContext();
        var q = uri.IndexOf('?', StringComparison.Ordinal);
        ctx.Request.Path = q < 0 ? uri : uri[..q];
        if (q >= 0)
        {
            ctx.Request.QueryString = new QueryString(uri[q..]);
        }

        foreach (var (name, value) in headers)
        {
            ctx.Request.Headers[name] = value;
        }

        return ctx.Request;
    }

    [Fact]
    public void Authorization_header_token()
    {
        var r = StreamRequest.From(Request(
            "/Holix/StreamAuth",
            ("Authorization", "MediaBrowser Client=\"Holix Play\", Device=\"Apple TV\", DeviceId=\"abc\", Version=\"1.0\", Token=\"tok-1\""),
            ("X-Forwarded-Uri", "/Videos/x/stream"),
            ("X-Forwarded-Method", "GET")));
        Assert.Equal("tok-1", r.Token);
        Assert.Equal("/Videos/x/stream", r.Uri);
        Assert.Equal("GET", r.Method);
    }

    [Theory]
    [InlineData("Authorization", "Emby Token=\"tok-2\"")]
    [InlineData("X-Emby-Authorization", "MediaBrowser Client=\"x\", Token=\"tok-2\"")]
    [InlineData("X-Emby-Token", "tok-2")]
    [InlineData("X-MediaBrowser-Token", "tok-2")]
    public void Other_headers(string name, string value)
    {
        Assert.Equal("tok-2", StreamRequest.From(Request("/Holix/StreamAuth", (name, value))).Token);
    }

    [Fact]
    public void Header_order_matches_jellyfin()
    {
        var r = StreamRequest.From(Request(
            "/Holix/StreamAuth?api_key=from-query",
            ("X-Emby-Token", "from-x-emby"),
            ("Authorization", "MediaBrowser Token=\"from-auth\""),
            ("X-Forwarded-Uri", "/Videos/x/stream?api_key=from-uri")));
        Assert.Equal("from-auth", r.Token);
    }

    [Theory]
    [InlineData("/Videos/x/stream.mp4?Static=true&api_key=tok-3")]
    [InlineData("/Videos/x/main.m3u8?ApiKey=tok-3&DeviceId=d")]
    [InlineData("/Videos/x/main.m3u8?apikey=tok-3")]
    public void Forwarded_uri_query(string uri)
    {
        var r = StreamRequest.From(Request("/Holix/StreamAuth", ("X-Forwarded-Uri", uri)));
        Assert.Equal("tok-3", r.Token);
        Assert.Equal(uri, r.Uri);
    }

    [Fact]
    public void Own_path_and_query_when_not_forwarded()
    {
        var r = StreamRequest.From(Request("/Videos/x/stream?api_key=tok-4"));
        Assert.Equal("tok-4", r.Token);
        Assert.Equal("/Videos/x/stream?api_key=tok-4", r.Uri);
        Assert.Equal("GET", r.Method);
    }

    [Fact]
    public void No_token()
    {
        Assert.Null(StreamRequest.From(Request("/Holix/StreamAuth", ("X-Forwarded-Uri", "/Videos/x/stream"), ("Authorization", "Bearer abc"))).Token);
        Assert.Null(StreamTokenReader.FromAuthorization("MediaBrowser Client=\"x\", Token=\"\""));
    }
}

public class ProxyAllowListTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.1.2.3", true)]
    [InlineData("172.20.0.5", true)]
    [InlineData("192.168.95.10", true)]
    [InlineData("fd12::5", true)]
    [InlineData("::ffff:10.0.0.7", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("2001:db8::1", false)]
    public void Default_is_loopback_and_private(string ip, bool allowed)
    {
        var list = ProxyAllowList.Parse([]);
        Assert.True(list.IsDefault);
        Assert.Equal(allowed, list.Contains(IPAddress.Parse(ip)));
    }

    [Fact]
    public void Configured_list_replaces_default()
    {
        var list = ProxyAllowList.Parse(["10.0.10.0/24", " 203.0.113.7 ", "bogus", ""]);
        Assert.False(list.IsDefault);
        Assert.True(list.Contains(IPAddress.Parse("10.0.10.20")));
        Assert.True(list.Contains(IPAddress.Parse("203.0.113.7")));
        Assert.False(list.Contains(IPAddress.Parse("10.0.11.1")));
        Assert.False(list.Contains(IPAddress.Parse("127.0.0.1")));
        Assert.Equal(["bogus"], list.Invalid);
        Assert.False(list.Contains(null));
    }

    [Fact]
    public void Only_invalid_entries_fail_closed()
    {
        var list = ProxyAllowList.Parse(["nope"]);
        Assert.False(list.Contains(IPAddress.Loopback));
    }

    [Fact]
    public void Peer_recorded_before_forwarded_headers_wins()
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.9"); // client address after X-Forwarded-For
        Assert.Equal(IPAddress.Parse("198.51.100.9"), StreamAuthPeerStartupFilter.Peer(ctx));
        ctx.Items[StreamAuthPeerStartupFilter.PeerItemKey] = IPAddress.Parse("10.0.10.5"); // the proxy
        Assert.Equal(IPAddress.Parse("10.0.10.5"), StreamAuthPeerStartupFilter.Peer(ctx));
    }
}

/// <summary>
/// End-to-end decisions with real Jellyfin entities (<see cref="Movie"/>, <see cref="User"/>) and Jellyfin's own
/// visibility check (<c>BaseItem.IsVisibleStandalone</c>), behind a fake token lookup.
/// </summary>
[Collection(nameof(BaseItemStatics))]
public class StreamAuthorizerTests
{
    private const string ChildToken = "child-token";
    private const string AdultToken = "adult-token";
    private const string GuestToken = "guest-token";
    private const string DisabledToken = "disabled-token";
    private const string ApiKey = "api-key";

    private static readonly Guid Library = Guid.NewGuid();
    private static readonly Guid OtherLibrary = Guid.NewGuid();

    private readonly Clock _clock = new();
    private readonly Backend _backend = new();
    private readonly StreamAuthorizer _authorizer;
    private readonly Dictionary<Guid, BaseItem> _items = [];

    public StreamAuthorizerTests()
    {
        BaseItemStatics.Install();
        _authorizer = new StreamAuthorizer(_backend, _clock);
        _backend.Items = _items;

        _backend.Users[ChildToken] = Profile("kid", AgeRating.Age7, child: true);
        _backend.Users[AdultToken] = Profile("adult", AgeRating.Age18, child: false);
        _backend.Users[GuestToken] = Profile("guest", AgeRating.Age13, child: false);
        var disabled = Profile("disabled", AgeRating.Age18, child: false);
        disabled.SetPermission(PermissionKind.IsDisabled, true);
        _backend.Users[DisabledToken] = disabled;
    }

    private static User Profile(string name, AgeRating band, bool child)
    {
        var user = new User(name, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider");
        user.AddDefaultPermissions();
        user.AddDefaultPreferences();
        var limit = AgeRatingMapper.Map(band, RatingSystem.TR);
        user.MaxParentalRatingScore = limit.Score;
        user.MaxParentalRatingSubScore = limit.SubScore;
        user.SetPermission(PermissionKind.EnableAllFolders, false);
        user.SetPreference(PreferenceKind.EnabledFolders, [Library.ToString("N")]);
        if (child)
        {
            user.SetPreference(PreferenceKind.BlockUnratedItems, [nameof(UnratedItem.Movie), nameof(UnratedItem.Series)]);
            user.SetPreference(PreferenceKind.BlockedTags, ["horror"]);
        }

        return user;
    }

    private Guid AddMovie(string? rating, Guid? library = null, params string[] tags)
    {
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Movie " + rating,
            OfficialRating = rating,
            PreferredMetadataCountryCode = "us",
            Path = "/media/movies/" + Guid.NewGuid().ToString("N") + ".mkv",
            Tags = tags,
        };
        _items[movie.Id] = movie;
        BaseItemStatics.CollectionFolder[movie.Id] = library ?? Library;
        return movie.Id;
    }

    private Task<int> Check(string? token, string uri, string method = "GET") =>
        _authorizer.AuthorizeAsync(new StreamRequest(method, uri, token));

    private static string Stream(Guid id) => $"/Videos/{id:N}/stream.mkv?Static=true";

    [Fact]
    public async Task No_token_is_401()
    {
        Assert.Equal(401, await Check(null, Stream(AddMovie("PG"))));
        Assert.Equal(0, _backend.TokenLookups);
    }

    [Fact]
    public async Task Invalid_token_is_401_and_not_cached()
    {
        var id = AddMovie("PG");
        Assert.Equal(401, await Check("nope", Stream(id)));
        Assert.Equal(401, await Check("nope", Stream(id)));
        Assert.Equal(2, _backend.TokenLookups);
        Assert.Equal(0, _authorizer.CachedDecisions);
    }

    [Fact]
    public async Task Child_and_r_movie_is_403()
    {
        Assert.Equal(403, await Check(ChildToken, Stream(AddMovie("R"))));
        Assert.Equal(403, await Check(ChildToken, Stream(AddMovie("PG-13"))));
    }

    [Fact]
    public async Task Child_and_suitable_movie_is_204()
    {
        Assert.Equal(204, await Check(ChildToken, Stream(AddMovie("G"))));
        Assert.Equal(204, await Check(ChildToken, Stream(AddMovie("TV-Y7"))));
    }

    [Fact]
    public async Task Child_unrated_and_blocked_tag_are_403()
    {
        Assert.Equal(403, await Check(ChildToken, Stream(AddMovie(null))));
        Assert.Equal(403, await Check(ChildToken, Stream(AddMovie("G", null, "Horror"))));
        Assert.Equal(204, await Check(AdultToken, Stream(AddMovie(null))));
    }

    [Fact]
    public async Task Child_genel_izleyici_is_204_with_turkish_aliases()
    {
        // "Genel İzleyici" is not in Jellyfin's tr.json; with the plugin's aliases it scores 0 (all ages).
        Assert.Equal(204, await Check(ChildToken, Stream(AddMovie("Genel İzleyici"))));
        Assert.Equal(204, await Check(ChildToken, Stream(AddMovie("GENEL"))));
        Assert.Equal(403, await Check(ChildToken, Stream(AddMovie("13 yaş ve üzeri"))));
    }

    [Fact]
    public async Task Guest_profile_follows_its_band()
    {
        Assert.Equal(204, await Check(GuestToken, Stream(AddMovie("PG-13"))));
        Assert.Equal(403, await Check(GuestToken, Stream(AddMovie("R"))));
    }

    [Fact]
    public async Task Library_access_is_enforced()
    {
        Assert.Equal(403, await Check(AdultToken, Stream(AddMovie("G", OtherLibrary))));
        Assert.Equal(204, await Check(AdultToken, Stream(AddMovie("G", Library))));
    }

    [Fact]
    public async Task Disabled_user_is_403_missing_item_404()
    {
        Assert.Equal(403, await Check(DisabledToken, Stream(AddMovie("G"))));
        Assert.Equal(404, await Check(AdultToken, Stream(Guid.NewGuid())));
    }

    [Fact]
    public async Task Api_key_is_allowed()
    {
        Assert.Equal(204, await Check(ApiKey, Stream(AddMovie("R"))));
    }

    [Fact]
    public async Task Segment_path_and_dashless_guid()
    {
        var r = AddMovie("R");
        var g = AddMovie("G");
        Assert.Equal(403, await Check(ChildToken, $"/Videos/{r:N}/hls1/main/7.mp4?runtimeTicks=70000000&api_key={ChildToken}"));
        Assert.Equal(204, await Check(ChildToken, $"/videos/{g:N}/HLS1/main/7.mp4"));
        Assert.Equal(204, await Check(ChildToken, $"/Videos/{g:D}/master.m3u8"));
        Assert.Equal(403, await Check(ChildToken, $"/Audio/{r:N}/universal"));
    }

    [Fact]
    public async Task Unrecognised_path_passes_without_token()
    {
        Assert.Equal(204, await Check(null, "/Items/abc/Images/Primary"));
        Assert.Equal(204, await Check(null, "/Holix/StreamAuth"));
        Assert.Equal(204, await Check(null, Stream(AddMovie("R")), "OPTIONS"));
        Assert.Equal(0, _backend.TokenLookups);
    }

    [Fact]
    public async Task Decisions_are_cached_for_sixty_seconds()
    {
        var id = AddMovie("G");
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(204, await Check(ChildToken, $"/Videos/{id:N}/hls1/main/{i}.mp4"));
        }

        Assert.Equal(1, _backend.TokenLookups);
        Assert.Equal(1, _backend.AccessChecks);

        // A second item with the same token: the token is cached, the decision is not.
        Assert.Equal(204, await Check(ChildToken, Stream(AddMovie("G"))));
        Assert.Equal(1, _backend.TokenLookups);
        Assert.Equal(2, _backend.AccessChecks);

        // A cached decision survives a policy change until it expires.
        ((Movie)_items[id]).OfficialRating = "R";
        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Equal(204, await Check(ChildToken, Stream(id)));
        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(403, await Check(ChildToken, Stream(id)));
        Assert.Equal(2, _backend.TokenLookups);
        Assert.Equal(3, _backend.AccessChecks);
    }

    [Fact]
    public async Task Cache_is_bounded()
    {
        var id = AddMovie("G");
        for (var i = 0; i < StreamAuthorizer.MaxEntries + 5; i++)
        {
            _backend.Users["t" + i] = _backend.Users[AdultToken];
            await Check("t" + i, Stream(id));
        }

        Assert.True(_authorizer.CachedDecisions <= StreamAuthorizer.MaxEntries);
    }

    private sealed class Backend : IStreamAccessBackend
    {
        public Dictionary<string, User> Users { get; } = new(StringComparer.Ordinal);

        public Dictionary<Guid, BaseItem> Items { get; set; } = [];

        public int TokenLookups { get; private set; }

        public int AccessChecks { get; private set; }

        public Task<StreamPrincipal?> ResolveTokenAsync(string token)
        {
            TokenLookups++;
            if (token == ApiKey)
            {
                return Task.FromResult<StreamPrincipal?>(new StreamPrincipal(null, true));
            }

            return Task.FromResult(Users.TryGetValue(token, out var u) ? new StreamPrincipal(u.Id, false) : null);
        }

        public int CheckAccess(Guid userId, Guid itemId)
        {
            AccessChecks++;
            var user = Users.Values.FirstOrDefault(u => IdOf(u) == userId);
            return JellyfinStreamAccessBackend.Decide(user, Items.GetValueOrDefault(itemId));
        }

        private static Guid IdOf(User u) => u.Id;
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

/// <summary>Sets up the static services <see cref="BaseItem"/> uses for its parental and library checks.</summary>
[CollectionDefinition(nameof(BaseItemStatics), DisableParallelization = true)]
public sealed class BaseItemStatics
{
    // Ratings from Jellyfin 12.2.0 us.json (subset) plus the plugin's Turkish aliases, so a test lookup resolves
    // like the server once the aliases are installed.
    private static readonly Dictionary<string, ParentalRatingScore> Us = new(StringComparer.OrdinalIgnoreCase)
    {
        ["G"] = new(0, 0),
        ["TV-Y7"] = new(7, 0),
        ["PG"] = new(10, 0),
        ["PG-13"] = new(13, 0),
        ["R"] = new(17, 0),
    };

    public static Dictionary<Guid, Guid> CollectionFolder { get; } = [];

    public static void Install()
    {
        BaseItem.Logger = NullLogger<BaseItem>.Instance;
        Video.RecordingsManager = Fake<IRecordingsManager>.Create();
        BaseItem.LocalizationManager = Fake<ILocalizationManager>.Create(("GetRatingScore", RatingScore));
        BaseItem.LibraryManager = Fake<ILibraryManager>.Create(("GetCollectionFolders", CollectionFolders));
    }

    private static object? RatingScore(object?[] args)
    {
        var rating = (string)args[0]!;
        if (Us.TryGetValue(rating, out var s))
        {
            return s;
        }

        return TurkishRatingAliases.TryResolve(rating, out var t) ? t : null;
    }

    private static object? CollectionFolders(object?[] args)
    {
        var item = (BaseItem)args[0]!;
        var folders = new List<Folder>();
        if (CollectionFolder.TryGetValue(item.Id, out var lib))
        {
            folders.Add(new Folder { Id = lib });
        }

        return folders;
    }
}
