using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Holix.Households;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Jellyfin.Plugin.Holix.Cloud;

/// <summary>The claims of a holix-cloud-signed proof (holix-cloud's proof format).</summary>
public sealed class ProofClaims
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

    /// <summary>Gets or sets the household id.</summary>
    [JsonPropertyName("hid")]
    public string? HouseholdId { get; set; }

    /// <summary>Gets or sets the cloud profile id.</summary>
    [JsonPropertyName("pid")]
    public string? ProfileId { get; set; }

    /// <summary>Gets or sets the profile kind.</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    /// <summary>Gets or sets the max age rating.</summary>
    [JsonPropertyName("max_age_rating")]
    public string? MaxAgeRating { get; set; }

    /// <summary>Gets or sets the Jellyfin server id.</summary>
    [JsonPropertyName("jf_sid")]
    public string? ServerId { get; set; }

    /// <summary>Gets or sets the Jellyfin user id.</summary>
    [JsonPropertyName("jf_uid")]
    public string? UserId { get; set; }

    /// <summary>Gets or sets the PIN time.</summary>
    [JsonPropertyName("auth_time")]
    public long? AuthTime { get; set; }
}

/// <summary>
/// Verifies a holix-cloud profile session proof (<c>jellyfin_proof</c>): JWS Compact, <c>alg: EdDSA</c>
/// (Ed25519), <c>typ: holix-jellyfin-proof+jwt</c>. Pure class with no network or storage access (see
/// <see cref="HouseholdService"/>). Every failure is a 403 <see cref="HolixException"/>; an unknown
/// <c>kid</c> has its own code (<see cref="UnknownKeyCode"/>) so the caller can refresh the JWKS and retry.
/// </summary>
public static class CloudProofVerifier
{
    /// <summary>The proof audience; holix-cloud's <c>PROOF_AUDIENCE</c>.</summary>
    public const string Audience = "holix-jellyfin-plugin";

    /// <summary>The type in the JWS header.</summary>
    public const string TokenType = "holix-jellyfin-proof+jwt";

    /// <summary>The default JWKS path.</summary>
    public const string DefaultJwksPath = "/v1/.well-known/jwks.json";

    /// <summary>The longest accepted proof lifetime (seconds).</summary>
    public const long MaxLifetimeSeconds = 60;

    /// <summary>The allowed clock skew (seconds).</summary>
    public const long ClockSkewSeconds = 10;

    /// <summary>The error code for an unknown key.</summary>
    public const string UnknownKeyCode = "proof_unknown_key";

    private const int MaxTokenLength = 4096;

    /// <summary>Verifies the proof; on success consumes its <c>jti</c> (a second use is rejected).</summary>
    /// <param name="token">The proof.</param>
    /// <param name="link">The household's cloud link (household id, <c>iss</c>, keys).</param>
    /// <param name="expected">The expected profile values.</param>
    /// <param name="now">The current time.</param>
    /// <param name="replay">The replay cache.</param>
    /// <returns>The verified claims.</returns>
    public static ProofClaims Verify(string? token, CloudLink link, ProofExpectation expected, DateTimeOffset now, ProofReplayCache replay)
    {
        ArgumentNullException.ThrowIfNull(link);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replay);

        var claims = VerifySignature(token, link);
        CheckClaims(claims, link, expected, now);
        if (!replay.TryUse(claims.Jti!, DateTimeOffset.FromUnixTimeSeconds(claims.Expires!.Value + ClockSkewSeconds), now))
        {
            throw Fail("proof_replayed", "This proof has already been used; get a new proof from holix-cloud.");
        }

        return claims;
    }

    /// <summary>Checks the format and signature and decodes the claims (no time/identity checks).</summary>
    /// <param name="token">The proof.</param>
    /// <param name="link">The link holding the keys.</param>
    /// <returns>The claims.</returns>
    public static ProofClaims VerifySignature(string? token, CloudLink link) =>
        VerifySignature<ProofClaims>(token, link, TokenType);

    /// <summary>Checks the format, the pinned <c>alg</c>/<c>typ</c> and the signature, and decodes the claims.</summary>
    /// <typeparam name="T">The claims type.</typeparam>
    /// <param name="token">The JWS.</param>
    /// <param name="link">The link holding the keys.</param>
    /// <param name="tokenType">The required <c>typ</c> header (proof kinds cannot stand in for each other).</param>
    /// <returns>The claims.</returns>
    internal static T VerifySignature<T>(string? token, CloudLink link, string tokenType)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(link);
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
        {
            throw Invalid("The proof is empty or too long.");
        }

        var parts = token.Trim().Split('.');
        if (parts.Length != 3)
        {
            throw Invalid("The proof is not in JWS Compact format.");
        }

        Header header;
        try
        {
            header = JsonSerializer.Deserialize<Header>(Base64Url.Decode(parts[0])) ?? throw Invalid("The header is empty.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw Invalid("Could not decode the header.");
        }

        // alg is pinned: "none", HS256 etc. are never accepted. crit means an unknown extension.
        if (!string.Equals(header.Alg, "EdDSA", StringComparison.Ordinal)
            || !string.Equals(header.Typ, tokenType, StringComparison.Ordinal)
            || header.Crit is not null
            || string.IsNullOrEmpty(header.Kid))
        {
            throw Invalid("Unexpected proof header (expected alg EdDSA, typ " + tokenType + ").");
        }

        var key = link.Keys.FirstOrDefault(k => string.Equals(k.Kid, header.Kid, StringComparison.Ordinal))
            ?? throw new HolixException(403, UnknownKeyCode, $"Unknown proof key ({header.Kid}).");

        byte[] signature;
        byte[] publicKey;
        try
        {
            signature = Base64Url.Decode(parts[2]);
            publicKey = Base64Url.Decode(key.X);
        }
        catch (FormatException)
        {
            throw Invalid("Could not decode the signature or key.");
        }

        if (signature.Length != 64 || publicKey.Length != 32 || !VerifyEd25519(publicKey, Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), signature))
        {
            throw Invalid("The proof signature is invalid.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(Base64Url.Decode(parts[1])) ?? throw Invalid("The payload is empty.");
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        {
            throw Invalid("Could not decode the payload.");
        }
    }

    /// <summary>Normalizes a Jellyfin id (UUID) to lowercase without dashes; <c>null</c> if invalid.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The canonical form.</returns>
    public static string? NormalizeId(string? id) =>
        Guid.TryParse(id, out var g) ? g.ToString("N") : null;

    private static void CheckClaims(ProofClaims c, CloudLink link, ProofExpectation expected, DateTimeOffset now)
    {
        if (c.Issuer is null || c.Audience is null || c.IssuedAt is null || c.Expires is null || string.IsNullOrEmpty(c.Jti)
            || c.HouseholdId is null || c.ProfileId is null || c.ServerId is null || c.UserId is null)
        {
            throw Invalid("The proof is missing required claims.");
        }

        if (!string.Equals(c.Audience, Audience, StringComparison.Ordinal))
        {
            throw Fail("proof_wrong_audience", "The proof was not issued for this plugin (aud).");
        }

        if (!string.Equals(c.Issuer, link.Issuer, StringComparison.Ordinal))
        {
            throw Fail("proof_wrong_issuer", "The proof was not issued by the linked holix-cloud (iss).");
        }

        var t = now.ToUnixTimeSeconds();
        var iat = c.IssuedAt.Value;
        var exp = c.Expires.Value;
        var nbf = c.NotBefore ?? iat;
        if (exp <= iat || exp - iat > MaxLifetimeSeconds)
        {
            throw Invalid($"The proof lifetime must be 1–{MaxLifetimeSeconds} s.");
        }

        if (iat > t + ClockSkewSeconds || nbf > t + ClockSkewSeconds)
        {
            throw Fail("proof_not_yet_valid", "The proof is not valid yet (check the clocks).");
        }

        if (t >= exp + ClockSkewSeconds)
        {
            throw Fail("proof_expired", "The proof has expired; get a new proof from holix-cloud.");
        }

        if (!string.Equals(c.HouseholdId, link.HouseholdId, StringComparison.Ordinal))
        {
            throw Fail("proof_wrong_household", "The proof does not belong to this household.");
        }

        if (!string.Equals(NormalizeId(c.ServerId), expected.ServerId, StringComparison.Ordinal))
        {
            throw Fail("proof_wrong_server", "The proof was not issued for this Jellyfin server.");
        }

        if (!string.Equals(NormalizeId(c.UserId), expected.JellyfinUserId.ToString("N"), StringComparison.Ordinal)
            || !string.Equals(c.ProfileId, expected.HolixProfileId, StringComparison.Ordinal))
        {
            throw Fail("proof_wrong_profile", "The proof was issued for a different profile.");
        }

        if (expected.Kind is not null && !string.Equals(c.Kind, expected.Kind, StringComparison.Ordinal))
        {
            throw Fail("proof_kind_mismatch", $"Profile kind is '{c.Kind}' in the cloud but '{expected.Kind}' on the server.");
        }

        // For children the cloud is authoritative: the rating must match exactly (the client syncs it via PATCH first).
        if (string.Equals(expected.Kind, "child", StringComparison.Ordinal)
            && !string.Equals(c.MaxAgeRating, expected.AgeRating, StringComparison.Ordinal))
        {
            throw Fail("age_rating_mismatch", $"Age rating is '{c.MaxAgeRating}' in the cloud but '{expected.AgeRating}' on the server; PATCH /Holix/Profiles/{{id}} {{AgeRating}} first.");
        }
    }

    private static bool VerifyEd25519(byte[] publicKey, byte[] message, byte[] signature)
    {
        try
        {
            var signer = new Ed25519Signer();
            signer.Init(false, new Ed25519PublicKeyParameters(publicKey));
            signer.BlockUpdate(message, 0, message.Length);
            return signer.VerifySignature(signature);
        }
        catch (ArgumentException)
        {
            // Public key not on the curve or malformed.
            return false;
        }
    }

    internal static HolixException Invalid(string message) => Fail("invalid_proof", message);

    internal static HolixException Fail(string code, string message) => new(403, code, message);

    private sealed class Header
    {
        [JsonPropertyName("alg")]
        public string? Alg { get; set; }

        [JsonPropertyName("typ")]
        public string? Typ { get; set; }

        [JsonPropertyName("kid")]
        public string? Kid { get; set; }

        [JsonPropertyName("crit")]
        public JsonElement? Crit { get; set; }
    }
}

/// <summary>base64url (RFC 4648 §5, unpadded).</summary>
public static class Base64Url
{
    /// <summary>Decodes.</summary>
    /// <param name="s">The text.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Decode(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (s.Contains('=', StringComparison.Ordinal) || s.Contains('+', StringComparison.Ordinal) || s.Contains('/', StringComparison.Ordinal))
        {
            throw new FormatException("base64url expected");
        }

        var b = s.Replace('-', '+').Replace('_', '/');
        b += (b.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            0 => string.Empty,
            _ => throw new FormatException("Invalid base64url length"),
        };
        return Convert.FromBase64String(b);
    }

    /// <summary>Encodes.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The text.</returns>
    public static string Encode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
