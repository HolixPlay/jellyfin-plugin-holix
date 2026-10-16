using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Holix.Cloud;

/// <summary>
/// A household's holix-cloud link (stored with the household record in <c>households.json</c>):
/// the cloud household id, the <c>iss</c> value and the public keys (JWKS) received when the link
/// code was redeemed. Contains no secrets.
/// </summary>
public sealed class CloudLink
{
    /// <summary>Gets or sets the holix-cloud base URL used when linking (JWKS refreshes use it).</summary>
    public string CloudUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets the holix-cloud household (account) id; a proof's <c>hid</c> must match it.</summary>
    public string HouseholdId { get; set; } = string.Empty;

    /// <summary>Gets or sets the expected <c>iss</c>.</summary>
    public string Issuer { get; set; } = string.Empty;

    /// <summary>Gets or sets the JWKS path (as announced by the cloud, e.g. <c>/v1/.well-known/jwks.json</c>).</summary>
    public string JwksPath { get; set; } = CloudProofVerifier.DefaultJwksPath;

    /// <summary>Gets the cached public keys.</summary>
    public List<CloudKey> Keys { get; init; } = [];

    /// <summary>Gets or sets the link time.</summary>
    public DateTime LinkedUtc { get; set; }

    /// <summary>Gets or sets the last JWKS fetch time (on an unknown <c>kid</c>, refreshed at most once every 5 minutes).</summary>
    public DateTime KeysFetchedUtc { get; set; }
}

/// <summary>An Ed25519 public key from the JWKS.</summary>
public sealed class CloudKey
{
    /// <summary>Gets or sets the key id.</summary>
    public string Kid { get; set; } = string.Empty;

    /// <summary>Gets or sets the base64url public key (32 bytes).</summary>
    public string X { get; set; } = string.Empty;
}

/// <summary>The expected values a proof is checked against for the plugin's profile.</summary>
/// <param name="ServerId">This Jellyfin server's id (lowercase, no dashes).</param>
/// <param name="JellyfinUserId">The profile user the session is opened for.</param>
/// <param name="HolixProfileId">The profile's cloud mapping (<c>pid</c> must match it).</param>
/// <param name="Kind">The profile kind (<c>adult</c>/<c>child</c>/<c>guest</c>); <c>null</c> = not checked (owner).</param>
/// <param name="AgeRating">The rating the plugin applied to the user (<c>all</c>…<c>18+</c>); checked for children only.</param>
public sealed record ProofExpectation(
    string ServerId,
    Guid JellyfinUserId,
    string HolixProfileId,
    string? Kind,
    string? AgeRating);
