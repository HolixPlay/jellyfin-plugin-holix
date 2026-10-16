using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Holix.Cloud;
using Jellyfin.Plugin.Holix.Ratings;

namespace Jellyfin.Plugin.Holix.Households;

/// <summary>Profile kind; same as <c>kind</c> in holix-cloud.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProfileKind>))]
public enum ProfileKind
{
    /// <summary>Adult.</summary>
    Adult = 0,

    /// <summary>Child: age rating required, unrated content hidden by default.</summary>
    Child = 1,

    /// <summary>Guest: playback state is not kept.</summary>
    Guest = 2,
}

/// <summary>
/// A household: one owner Jellyfin user and the profile users attached to it.
/// The owner is a profile too (synthesised in listings with <c>isOwner=true</c>, not stored here).
/// </summary>
public sealed class Household
{
    /// <summary>Gets or sets the owner Jellyfin user id.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Gets or sets the rating system used for new profiles.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<RatingSystem>))]
    public RatingSystem RatingSystem { get; set; }

    /// <summary>Gets or sets the holix-cloud profile id of the owner (optional mapping).</summary>
    public string? OwnerHolixProfileId { get; set; }

    /// <summary>Gets or sets the creation time.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>
    /// Gets or sets the holix-cloud link (<c>null</c> = not linked). In a linked household, profile sessions
    /// require a proof signed by the cloud (<see cref="Configuration.PluginConfiguration.RequireCloudProof"/>).
    /// </summary>
    public CloudLink? Cloud { get; set; }

    /// <summary>Gets the sub profiles (excluding the owner).</summary>
    public List<HouseholdProfile> Profiles { get; init; } = [];
}

/// <summary>A sub profile within a household = one Jellyfin user.</summary>
public sealed class HouseholdProfile
{
    /// <summary>Gets or sets the Jellyfin user id of the profile.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the display name (the Jellyfin user name is derived from it).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind.</summary>
    public ProfileKind Kind { get; set; }

    /// <summary>Gets or sets the age rating sent by the client.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<AgeRating>))]
    public AgeRating AgeRating { get; set; } = AgeRating.Age18;

    /// <summary>Gets or sets the per-profile rating system override; <c>null</c> = the household's system.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<RatingSystem>))]
    public RatingSystem? RatingSystem { get; set; }

    /// <summary>Gets or sets the extra blocked tags.</summary>
#pragma warning disable CA1819
    public string[] BlockedTags { get; set; } = [];
#pragma warning restore CA1819

    /// <summary>Gets or sets the holix-cloud profile id (<c>holixProfileId ↔ jellyfinUserId</c>).</summary>
    public string? HolixProfileId { get; set; }

    /// <summary>Gets or sets the creation time.</summary>
    public DateTime CreatedUtc { get; set; }
}

/// <summary>A user's role in a household.</summary>
public enum HouseholdRole
{
    /// <summary>Not attached to any household.</summary>
    None = 0,

    /// <summary>Household owner.</summary>
    Owner = 1,

    /// <summary>Sub profile.</summary>
    Profile = 2,
}

/// <summary>Result of resolving a user to a household.</summary>
/// <param name="Role">Role.</param>
/// <param name="Household">Household (<c>null</c> when the role is <see cref="HouseholdRole.None"/>).</param>
/// <param name="Profile">Sub profile (only for <see cref="HouseholdRole.Profile"/>).</param>
public sealed record HouseholdMembership(HouseholdRole Role, Household? Household, HouseholdProfile? Profile)
{
    /// <summary>Gets the empty membership.</summary>
    public static HouseholdMembership None { get; } = new(HouseholdRole.None, null, null);
}
