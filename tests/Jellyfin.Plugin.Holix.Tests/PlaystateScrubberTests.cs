using System;
using Jellyfin.Plugin.Holix.Configuration;
using Jellyfin.Plugin.Holix.Guest;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Holix.Tests;

public class PlaystateScrubberTests
{
    private static UserItemData Watched() => new()
    {
        Key = "k",
        PlaybackPositionTicks = 12_000_000_000,
        PlayCount = 2,
        Played = true,
        LastPlayedDate = DateTime.UtcNow,
        IsFavorite = true,
        Rating = 9,
        AudioStreamIndex = 1,
    };

    [Fact]
    public void Playstate_is_cleared_favorite_is_kept()
    {
        var d = Watched();
        Assert.True(PlaystateScrubber.Scrub(d, GuestBehavior.DiscardPlaystate));
        Assert.Equal(0, d.PlaybackPositionTicks);
        Assert.Equal(0, d.PlayCount);
        Assert.False(d.Played);
        Assert.Null(d.LastPlayedDate);
        Assert.True(d.IsFavorite);
        Assert.Equal(9, d.Rating);
        Assert.Equal(1, d.AudioStreamIndex);

        // Second pass: clean data → no loop.
        Assert.False(PlaystateScrubber.NeedsScrub(d, GuestBehavior.DiscardPlaystate));
    }

    [Fact]
    public void All_user_data_is_cleared()
    {
        var d = Watched();
        Assert.True(PlaystateScrubber.Scrub(d, GuestBehavior.DiscardAllUserData));
        Assert.False(d.IsFavorite);
        Assert.Null(d.Rating);
        Assert.Null(d.Likes);
        Assert.False(PlaystateScrubber.NeedsScrub(d, GuestBehavior.DiscardAllUserData));
    }

    [Fact]
    public void Untouched_when_protection_is_off()
    {
        var d = Watched();
        Assert.False(PlaystateScrubber.Scrub(d, GuestBehavior.KeepPlaystate));
        Assert.True(d.Played);
    }

    [Fact]
    public void Favorite_alone_is_not_playstate()
    {
        var d = new UserItemData { Key = "k", IsFavorite = true };
        Assert.False(PlaystateScrubber.NeedsScrub(d, GuestBehavior.DiscardPlaystate));
        Assert.True(PlaystateScrubber.NeedsScrub(d, GuestBehavior.DiscardAllUserData));
    }
}
