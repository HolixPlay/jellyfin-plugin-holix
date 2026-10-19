using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Holix.Households;

namespace Jellyfin.Plugin.Holix.Cloud;

/// <summary>The claims of a holix-cloud session proof (<c>POST /v1/servers/{id}/session-proof</c>).</summary>
public sealed class SessionProofClaims
{
    /// <summary>Gets or sets the issuer.</summary>
    [JsonPropertyName("iss")]
    public string? Issuer { get; set; }

    /// <summary>Gets or sets the audience.</summary>
    [JsonPropertyName("aud")]
    public string? Audience { get; set; }

    /// <summary>Gets or sets the issued-at (Unix seconds).</summary>
    [JsonPropertyName("iat")]
    public long? IssuedAt { get; set; }

    /// <summary>Gets or sets the not-before (Unix seconds).</summary>
    [JsonPropertyName("nbf")]
    public long? NotBefore { get; set; }

    /// <summary>Gets or sets the expiry (Unix seconds).</summary>
    [JsonPropertyName("exp")]
    public long? Expires { get; set; }

    /// <summary>Gets or sets the unique id.</summary>
    [JsonPropertyName("jti")]
    public string? Jti { get; set; }

    /// <summary>Gets or sets the Holix account id.</summary>
    [JsonPropertyName("sub")]
    public string? Subject { get; set; }

    /// <summary>Gets or sets the household id.</summary>
    [JsonPropertyName("hid")]
    public string? HouseholdId { get; set; }

    /// <summary>Gets or sets the Jellyfin server id.</summary>
    [JsonPropertyName("jf_sid")]
    public string? ServerId { get; set; }

    /// <summary>Gets or sets the device the session is for.</summary>
    [JsonPropertyName("device_id")]
    public string? DeviceId { get; set; }
}

/// <summary>
/// Verifies a holix-cloud session proof: JWS Compact, <c>alg: EdDSA</c>, <c>typ: holix-jellyfin-session+jwt</c>,
/// signed with the same keys as the profile proof. A device signed in to the household's Holix account uses it to
/// open the owner's Jellyfin session without the owner's password (<c>POST /Holix/Household/CloudSession</c>).
/// The proof is bound to the household (<c>hid</c>), this server (<c>jf_sid</c>) and the device (<c>device_id</c>),
/// lives at most 60 s and is single use. Pure class; every failure is a 403 <see cref="HolixException"/>.
/// </summary>
public static class CloudSessionVerifier
{
    /// <summary>The type in the JWS header.</summary>
    public const string TokenType = "holix-jellyfin-session+jwt";

    /// <summary>
    /// Reads the household id from an unverified proof, only to find which household's keys verify it.
    /// </summary>
    /// <param name="token">The proof.</param>
    /// <returns>The <c>hid</c> claim.</returns>
    public static string ReadHouseholdId(string? token)
    {
        var parts = token?.Trim().Split('.');
        if (parts is not { Length: 3 } || token!.Length > 4096)
        {
            throw CloudProofVerifier.Invalid("The proof is not in JWS Compact format.");
        }

        try
        {
            var claims = JsonSerializer.Deserialize<SessionProofClaims>(Base64Url.Decode(parts[1]));
            return string.IsNullOrEmpty(claims?.HouseholdId)
                ? throw CloudProofVerifier.Invalid("The proof is missing required claims.")
                : claims.HouseholdId;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw CloudProofVerifier.Invalid("Could not decode the payload.");
        }
    }

    /// <summary>Verifies the proof; on success consumes its <c>jti</c> (a second use is rejected).</summary>
    /// <param name="token">The proof.</param>
    /// <param name="link">The household's cloud link.</param>
    /// <param name="serverId">This server's id (lowercase, no dashes).</param>
    /// <param name="deviceId">The device id in the request.</param>
    /// <param name="now">The current time.</param>
    /// <param name="replay">The replay cache.</param>
    /// <returns>The verified claims.</returns>
    public static SessionProofClaims Verify(string? token, CloudLink link, string serverId, string deviceId, DateTimeOffset now, ProofReplayCache replay)
    {
        var c = Check(token, link, serverId, deviceId, now);
        Consume(c, now, replay);
        return c;
    }

    /// <summary>Marks a checked proof as used; a second use is rejected (<c>proof_replayed</c>).</summary>
    /// <param name="claims">Claims returned by <see cref="Check"/>.</param>
    /// <param name="now">The current time.</param>
    /// <param name="replay">The replay cache.</param>
    public static void Consume(SessionProofClaims claims, DateTimeOffset now, ProofReplayCache replay)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(replay);
        if (!replay.TryUse(claims.Jti!, DateTimeOffset.FromUnixTimeSeconds(claims.Expires!.Value + CloudProofVerifier.ClockSkewSeconds), now))
        {
            throw CloudProofVerifier.Fail("proof_replayed", "This proof has already been used; get a new proof from holix-cloud.");
        }
    }

    /// <summary>
    /// Checks signature, claims and bindings without consuming the <c>jti</c> (the caller consumes it with
    /// <see cref="Consume"/> once nothing else can refuse the request, so a refused request does not burn the proof).
    /// </summary>
    /// <param name="token">The proof.</param>
    /// <param name="link">The household's cloud link.</param>
    /// <param name="serverId">This server's id (lowercase, no dashes).</param>
    /// <param name="deviceId">The device id in the request.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The checked claims.</returns>
    public static SessionProofClaims Check(string? token, CloudLink link, string serverId, string deviceId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(link);

        var c = CloudProofVerifier.VerifySignature<SessionProofClaims>(token, link, TokenType);
        if (c.Issuer is null || c.Audience is null || c.IssuedAt is null || c.Expires is null || string.IsNullOrEmpty(c.Jti)
            || c.HouseholdId is null || c.ServerId is null || string.IsNullOrEmpty(c.DeviceId) || string.IsNullOrEmpty(c.Subject))
        {
            throw CloudProofVerifier.Invalid("The proof is missing required claims.");
        }

        if (!string.Equals(c.Audience, CloudProofVerifier.Audience, StringComparison.Ordinal))
        {
            throw CloudProofVerifier.Fail("proof_wrong_audience", "The proof was not issued for this plugin (aud).");
        }

        if (!string.Equals(c.Issuer, link.Issuer, StringComparison.Ordinal))
        {
            throw CloudProofVerifier.Fail("proof_wrong_issuer", "The proof was not issued by the linked holix-cloud (iss).");
        }

        var t = now.ToUnixTimeSeconds();
        var iat = c.IssuedAt.Value;
        var exp = c.Expires.Value;
        var nbf = c.NotBefore ?? iat;
        if (exp <= iat || exp - iat > CloudProofVerifier.MaxLifetimeSeconds)
        {
            throw CloudProofVerifier.Invalid($"The proof lifetime must be 1–{CloudProofVerifier.MaxLifetimeSeconds} s.");
        }

        if (iat > t + CloudProofVerifier.ClockSkewSeconds || nbf > t + CloudProofVerifier.ClockSkewSeconds)
        {
            throw CloudProofVerifier.Fail("proof_not_yet_valid", "The proof is not valid yet (check the clocks).");
        }

        if (t >= exp + CloudProofVerifier.ClockSkewSeconds)
        {
            throw CloudProofVerifier.Fail("proof_expired", "The proof has expired; get a new proof from holix-cloud.");
        }

        if (!string.Equals(c.HouseholdId, link.HouseholdId, StringComparison.Ordinal))
        {
            throw CloudProofVerifier.Fail("proof_wrong_household", "The proof does not belong to this household.");
        }

        if (!string.Equals(CloudProofVerifier.NormalizeId(c.ServerId), serverId, StringComparison.Ordinal))
        {
            throw CloudProofVerifier.Fail("proof_wrong_server", "The proof was not issued for this Jellyfin server.");
        }

        if (!string.Equals(c.DeviceId, deviceId, StringComparison.Ordinal))
        {
            throw CloudProofVerifier.Fail("proof_wrong_device", "The proof was issued for a different device (DeviceId).");
        }

        return c;
    }
}
