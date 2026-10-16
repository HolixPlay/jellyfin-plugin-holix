using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Holix.Ratings;
using MediaBrowser.Model.Users;

namespace Jellyfin.Plugin.Holix.Households;

/// <summary>
/// Builds the Jellyfin policy of a profile user. Pure function (unit tested).
/// </summary>
public static class ProfilePolicyBuilder
{
    /// <summary>Item types hidden for child profiles when unrated (music/books excluded).</summary>
    public static IReadOnlyList<UnratedItem> ChildUnratedBlock { get; } =
    [
        UnratedItem.Movie,
        UnratedItem.Trailer,
        UnratedItem.Series,
        UnratedItem.LiveTvChannel,
        UnratedItem.LiveTvProgram,
        UnratedItem.ChannelContent,
        UnratedItem.Other,
    ];

    /// <summary>Computes the policy.</summary>
    /// <param name="owner">The owner's current policy (library access is copied from it).</param>
    /// <param name="current">The profile user's current policy (authentication provider fields are kept).</param>
    /// <param name="profile">Profile.</param>
    /// <param name="householdSystem">The household's rating system.</param>
    /// <param name="defaultChildBlockedTags">Default blocked tags for children.</param>
    /// <param name="blockUnratedForChildren">Whether unrated content is hidden for children.</param>
    /// <returns>The new policy.</returns>
    public static UserPolicy Build(
        UserPolicy owner,
        UserPolicy current,
        HouseholdProfile profile,
        RatingSystem householdSystem,
        IReadOnlyCollection<string> defaultChildBlockedTags,
        bool blockUnratedForChildren)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(defaultChildBlockedTags);

        var limit = AgeRatingMapper.Map(profile.AgeRating, profile.RatingSystem ?? householdSystem);
        var isChild = profile.Kind == ProfileKind.Child;
        var isGuest = profile.Kind == ProfileKind.Guest;

        var tags = profile.BlockedTags
            .Concat(owner.BlockedTags ?? [])
            .Concat(isChild ? defaultChildBlockedTags : [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // If the owner's limit is stricter, the profile cannot exceed it.
        var (score, sub) = Tighter((limit.Score, limit.SubScore), (owner.MaxParentalRating, owner.MaxParentalSubRating));

        return new UserPolicy
        {
            // Permissions: a profile is never an administrator and is hidden from the login screen.
            IsAdministrator = false,
            IsHidden = true,
            IsDisabled = false,

            // Authentication providers are the profile user's own values (set on creation).
            AuthenticationProviderId = current.AuthenticationProviderId,
            PasswordResetProviderId = current.PasswordResetProviderId,
            InvalidLoginAttemptCount = current.InvalidLoginAttemptCount,
            LoginAttemptsBeforeLockout = current.LoginAttemptsBeforeLockout,

            // Parental control.
            MaxParentalRating = score,
            MaxParentalSubRating = sub,
            BlockedTags = tags,
            AllowedTags = owner.AllowedTags ?? [],
            BlockUnratedItems = isChild && blockUnratedForChildren
                ? ChildUnratedBlock.Union(owner.BlockUnratedItems ?? []).ToArray()
                : owner.BlockUnratedItems ?? [],
            AccessSchedules = owner.AccessSchedules ?? [],

            // Library/channel/device access is copied from the owner.
            EnableAllFolders = owner.EnableAllFolders,
            EnabledFolders = owner.EnabledFolders ?? [],
            EnableAllChannels = owner.EnableAllChannels,
            EnabledChannels = owner.EnabledChannels ?? [],
            EnableAllDevices = owner.EnableAllDevices,
            EnabledDevices = owner.EnabledDevices ?? [],

            // Playback capabilities come from the owner.
            EnableRemoteAccess = owner.EnableRemoteAccess,
            EnableMediaPlayback = owner.EnableMediaPlayback,
            EnableAudioPlaybackTranscoding = owner.EnableAudioPlaybackTranscoding,
            EnableVideoPlaybackTranscoding = owner.EnableVideoPlaybackTranscoding,
            EnablePlaybackRemuxing = owner.EnablePlaybackRemuxing,
            ForceRemoteSourceTranscoding = owner.ForceRemoteSourceTranscoding,
            RemoteClientBitrateLimit = owner.RemoteClientBitrateLimit,
            EnableLiveTvAccess = owner.EnableLiveTvAccess,
            SyncPlayAccess = isGuest ? SyncPlayUserAccessType.JoinGroups : owner.SyncPlayAccess,
            EnableContentDownloading = !isGuest && !isChild && owner.EnableContentDownloading,
            EnableSyncTranscoding = owner.EnableSyncTranscoding,
            EnableMediaConversion = owner.EnableMediaConversion,
            MaxActiveSessions = 0,

            // Management permissions are disabled for profiles.
            EnableContentDeletion = false,
            EnableContentDeletionFromFolders = [],
            EnableCollectionManagement = false,
            EnableSubtitleManagement = false,
            EnableLyricManagement = false,
            EnableLiveTvManagement = false,
            EnableRemoteControlOfOtherUsers = false,
            EnableSharedDeviceControl = !isChild && !isGuest && owner.EnableSharedDeviceControl,
            EnablePublicSharing = false,
            EnableUserPreferenceAccess = !isGuest,
        };
    }

    /// <summary>The more restrictive of two limits (null = unlimited).</summary>
    /// <param name="a">First.</param>
    /// <param name="b">Second.</param>
    /// <returns>The stricter limit.</returns>
    internal static (int? Score, int? Sub) Tighter((int? Score, int? Sub) a, (int? Score, int? Sub) b)
    {
        if (a.Score is null)
        {
            return b;
        }

        if (b.Score is null)
        {
            return a;
        }

        if (a.Score != b.Score)
        {
            return a.Score < b.Score ? a : b;
        }

        // Same score: the one with the lower sub score is stricter (null = any sub score at that score is allowed).
        if (a.Sub is null)
        {
            return b;
        }

        if (b.Sub is null)
        {
            return a;
        }

        return a.Sub <= b.Sub ? a : b;
    }
}
