using System;
using Jellyfin.Plugin.Holix.Configuration;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.Holix.Guest;

/// <summary>
/// Rules for scrubbing guest user data. Pure functions (unit tested).
/// </summary>
public static class PlaystateScrubber
{
    /// <summary>Whether the data contains anything that must be removed.</summary>
    /// <param name="data">The user item data.</param>
    /// <param name="behavior">The guest behaviour.</param>
    /// <returns><c>true</c> if scrubbing is needed.</returns>
    public static bool NeedsScrub(UserItemData data, GuestBehavior behavior)
    {
        ArgumentNullException.ThrowIfNull(data);
        return behavior switch
        {
            GuestBehavior.KeepPlaystate => false,
            GuestBehavior.DiscardPlaystate => HasPlaystate(data),
            GuestBehavior.DiscardAllUserData => HasPlaystate(data) || data.IsFavorite || data.Likes.HasValue || data.Rating.HasValue,
            _ => false,
        };
    }

    /// <summary>Scrubs the data in place.</summary>
    /// <param name="data">The user item data.</param>
    /// <param name="behavior">The guest behaviour.</param>
    /// <returns><c>true</c> if anything changed.</returns>
    public static bool Scrub(UserItemData data, GuestBehavior behavior)
    {
        if (!NeedsScrub(data, behavior))
        {
            return false;
        }

        data.PlaybackPositionTicks = 0;
        data.PlayCount = 0;
        data.Played = false;
        data.LastPlayedDate = null;

        if (behavior == GuestBehavior.DiscardAllUserData)
        {
            data.IsFavorite = false;
            data.Likes = null;
            data.Rating = null;
        }

        return true;
    }

    private static bool HasPlaystate(UserItemData data) =>
        data.PlaybackPositionTicks != 0 || data.PlayCount != 0 || data.Played || data.LastPlayedDate.HasValue;
}
