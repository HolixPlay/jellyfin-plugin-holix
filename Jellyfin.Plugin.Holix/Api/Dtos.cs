using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using MediaBrowser.Controller.Authentication;

namespace Jellyfin.Plugin.Holix.Api;

#pragma warning disable CA1819 // DTOs carry arrays for JSON.

/// <summary>Response of <c>GET /Holix/Info</c>.</summary>
public sealed class InfoDto
{
    /// <summary>Gets or sets the plugin name.</summary>
    public string Name { get; set; } = "Holix";

    /// <summary>Gets or sets the plugin version.</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Gets or sets the API version (bumped on breaking changes).</summary>
    public int ApiVersion { get; set; } = 1;

    /// <summary>Gets or sets the feature flags.</summary>
    public IReadOnlyList<string> Features { get; set; } = [];

    /// <summary>Gets or sets the holix-cloud URL (empty = not configured).</summary>
    public string? CloudUrl { get; set; }

    /// <summary>Gets or sets the guest behaviour.</summary>
    public string GuestBehavior { get; set; } = string.Empty;

    /// <summary>Gets or sets the supported rating systems.</summary>
    public IReadOnlyList<string> RatingSystems { get; set; } = [];

    /// <summary>Gets or sets the default rating system.</summary>
    public string DefaultRatingSystem { get; set; } = string.Empty;

    /// <summary>Gets or sets the accepted age ratings.</summary>
    public IReadOnlyList<string> AgeRatings { get; set; } = [];

    /// <summary>Gets or sets the max profiles per household (including the owner).</summary>
    public int MaxProfiles { get; set; }

    /// <summary>Gets or sets a value indicating whether linked households need a cloud proof for sessions.</summary>
    public bool RequireCloudProof { get; set; }

    /// <summary>Gets or sets a value indicating whether unlinked households are refused sessions.</summary>
    public bool RequireCloudLink { get; set; }
}

/// <summary>Body of <c>POST /Holix/Household/Link</c>.</summary>
public sealed class LinkRequest
{
    /// <summary>Gets or sets the one-time code from holix-cloud <c>POST /v1/jellyfin/link-code</c>.</summary>
    public string? Code { get; set; }
}

/// <summary>The household's cloud link (contains no secrets).</summary>
public sealed class CloudLinkDto
{
    /// <summary>Gets or sets the holix-cloud URL.</summary>
    public string CloudUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the holix-cloud household id.</summary>
    public string HouseholdId { get; set; } = string.Empty;

    /// <summary>Gets or sets the issuer.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Gets or sets the cached key ids.</summary>
    public IReadOnlyList<string> KeyIds { get; set; } = [];

    /// <summary>Gets or sets the link time.</summary>
    public DateTime LinkedUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether sessions need a proof (setting).</summary>
    public bool ProofRequired { get; set; }
}

/// <summary>Body of <c>POST /Holix/Household</c>.</summary>
public sealed class CreateHouseholdRequest
{
    /// <summary>Gets or sets the rating system (<c>TR</c>/<c>US</c>/<c>DE</c>).</summary>
    public string? RatingSystem { get; set; }

    /// <summary>Gets or sets the owner's holix-cloud profile id.</summary>
    public string? OwnerHolixProfileId { get; set; }
}

/// <summary>Household view.</summary>
public sealed class HouseholdDto
{
    /// <summary>Gets or sets the caller's role (<c>owner</c>/<c>profile</c>).</summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>Gets or sets the owner user id.</summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>Gets or sets the owner user name.</summary>
    public string OwnerName { get; set; } = string.Empty;

    /// <summary>Gets or sets the rating system.</summary>
    public string RatingSystem { get; set; } = string.Empty;

    /// <summary>Gets or sets the creation time.</summary>
    public DateTime CreatedUtc { get; set; }

    /// <summary>Gets or sets the profiles (owner first). In the profile role, only the caller's own profile.</summary>
    public IReadOnlyList<ProfileDto> Profiles { get; set; } = [];

    /// <summary>Gets or sets the holix-cloud link (<c>null</c> = not linked; hidden in the profile role).</summary>
    public CloudLinkDto? Cloud { get; set; }
}

/// <summary>Profile view.</summary>
public sealed class ProfileDto
{
    /// <summary>Gets or sets the Jellyfin user id (the profile id).</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the holix-cloud profile id.</summary>
    public string? HolixProfileId { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin user name.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets the kind (<c>adult</c>/<c>child</c>/<c>guest</c>).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this is the owner.</summary>
    public bool IsOwner { get; set; }

    /// <summary>Gets or sets the age rating (<c>all</c> … <c>18+</c>).</summary>
    public string AgeRating { get; set; } = string.Empty;

    /// <summary>Gets or sets the effective rating system.</summary>
    public string RatingSystem { get; set; } = string.Empty;

    /// <summary>Gets or sets the label of the highest allowed rating.</summary>
    public string RatingLabel { get; set; } = string.Empty;

    /// <summary>Gets or sets the effective Jellyfin MaxParentalRating.</summary>
    public int? MaxParentalRating { get; set; }

    /// <summary>Gets or sets the effective Jellyfin MaxParentalSubRating.</summary>
    public int? MaxParentalSubRating { get; set; }

    /// <summary>Gets or sets the blocked tags (effective policy).</summary>
    public string[] BlockedTags { get; set; } = [];

    /// <summary>Gets or sets the primary image tag (if set, see <c>/Users/{id}/Images/Primary</c>).</summary>
    public string? PrimaryImageTag { get; set; }
}

/// <summary>Body of <c>POST /Holix/Profiles</c> and <c>PATCH /Holix/Profiles/{id}</c>.</summary>
public sealed class ProfileRequest
{
    /// <summary>Gets or sets the display name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the kind (<c>adult</c>/<c>child</c>/<c>guest</c>).</summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets the age rating (<c>all</c>, <c>7+</c>, <c>13+</c>, <c>16+</c>, <c>18+</c>). The birth date is never sent.</summary>
    public string? AgeRating { get; set; }

    /// <summary>Gets or sets the rating system override (<c>TR</c>/<c>US</c>/<c>DE</c>); empty = household default.</summary>
    public string? RatingSystem { get; set; }

    /// <summary>Gets or sets the extra blocked tags (on PATCH, replaces the whole list when given).</summary>
    public string[]? BlockedTags { get; set; }

    /// <summary>Gets or sets the holix-cloud profile id.</summary>
    public string? HolixProfileId { get; set; }
}

/// <summary>Body of <c>POST /Holix/Profiles/{id}/Session</c> (all fields optional).</summary>
public sealed class SessionRequest
{
    /// <summary>Gets or sets the device id; derived from the caller's device id when empty.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Gets or sets the device name.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Gets or sets the app name.</summary>
    public string? App { get; set; }

    /// <summary>Gets or sets the app version.</summary>
    public string? AppVersion { get; set; }

    /// <summary>
    /// Gets or sets the holix-cloud proof (<c>jellyfin_proof</c>, ≤ 60 s, single use). Required when the
    /// household is cloud-linked (and <c>RequireCloudProof</c> is on).
    /// </summary>
    public string? JellyfinProof { get; set; }
}

/// <summary>Profile session.</summary>
public sealed class ProfileSessionDto
{
    /// <summary>Gets or sets the profile.</summary>
    public ProfileDto Profile { get; set; } = new();

    /// <summary>Gets or sets the Jellyfin access token (issued for this profile user).</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Gets or sets the device id the token is bound to (the client must send it together with the token).</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the server id.</summary>
    public string ServerId { get; set; } = string.Empty;

    /// <summary>Gets or sets the raw Jellyfin authentication result (same shape as <c>/Users/AuthenticateByName</c>).</summary>
    public AuthenticationResult? Authentication { get; set; }

    /// <summary>Gets or sets a value indicating whether the session was opened with a verified holix-cloud proof.</summary>
    public bool ProofVerified { get; set; }

    /// <summary>Gets or sets a warning (e.g. <c>cloud_not_linked</c>: transition period, opened without a proof).</summary>
    public string? Warning { get; set; }

    /// <summary>
    /// Gets or sets the household (owner view, same as <c>GET /Holix/Household</c>); only in the
    /// <c>POST /Holix/Household/CloudSession</c> response, so a new device gets the profile list without a password.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HouseholdDto? Household { get; set; }
}

/// <summary>
/// Body of <c>POST /Holix/Household/CloudSession</c>: opens a session with a holix-cloud session proof instead of the
/// owner's password (account-first sign-in).
/// </summary>
public sealed class CloudSessionRequest
{
    /// <summary>Gets or sets the session proof (holix-cloud <c>POST /v1/servers/{id}/session-proof</c>, ≤ 60 s, single use).</summary>
    public string? Proof { get; set; }

    /// <summary>Gets or sets the device id; must equal the proof's <c>device_id</c>.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Gets or sets the device name.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Gets or sets the app name.</summary>
    public string? App { get; set; }

    /// <summary>Gets or sets the app version.</summary>
    public string? AppVersion { get; set; }

    /// <summary>Gets or sets the profile (Jellyfin user id) to open; empty = the household owner.</summary>
    public Guid? ProfileId { get; set; }

    /// <summary>
    /// Gets or sets the profile proof (<c>jellyfin_proof</c> from holix-cloud's profile session, after the PIN) when
    /// <see cref="ProfileId"/> is a profile; same rules as <c>POST /Holix/Profiles/{id}/Session</c>.
    /// </summary>
    public string? ProfileProof { get; set; }
}

/// <summary>Error body (same shape as holix-cloud's).</summary>
/// <param name="Error">Error code.</param>
/// <param name="Message">Human-readable description.</param>
public sealed record ErrorDto(string Error, string Message);
