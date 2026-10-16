using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Holix.Cloud;
using Jellyfin.Plugin.Holix.Households;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Jellyfin.Plugin.Holix.Tests;

/// <summary>Verification of the holix-cloud proof (jellyfin_proof).</summary>
public sealed class CloudProofVerifierTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_010);
    private static readonly Guid ChildUser = Guid.Parse("0123456789abcdef0123456789abcdef");
    private const string Server = "6f9619ff8b86d011b42d00c04fc964ff";
    private const string Kid = "key-1";

    private readonly byte[] _seed = Seed(7);
    private readonly ProofReplayCache _replay = new();

    private static byte[] Seed(byte b)
    {
        var s = new byte[32];
        Array.Fill(s, b);
        return s;
    }

    private static string PublicX(byte[] seed) =>
        Base64Url.Encode(new Ed25519PrivateKeyParameters(seed).GeneratePublicKey().GetEncoded());

    private CloudLink Link() => LinkWith(Kid, _seed);

    private static CloudLink LinkWith(string kid, byte[] seed)
    {
        var link = new CloudLink { CloudUrl = "https://cloud.test", HouseholdId = "household-1", Issuer = "holix-cloud" };
        link.Keys.Add(new CloudKey { Kid = kid, X = PublicX(seed) });
        return link;
    }

    private static ProofExpectation Child(string rating = "7+") =>
        new(Server, ChildUser, "cloud-profile-1", "child", rating);

    private static Dictionary<string, object?> Claims() => new()
    {
        ["iss"] = "holix-cloud",
        ["aud"] = "holix-jellyfin-plugin",
        ["iat"] = 1_800_000_000L,
        ["nbf"] = 1_800_000_000L,
        ["exp"] = 1_800_000_060L,
        ["jti"] = Guid.NewGuid().ToString(),
        ["hid"] = "household-1",
        ["pid"] = "cloud-profile-1",
        ["kind"] = "child",
        ["max_age_rating"] = "7+",
        ["jf_sid"] = Server,
        ["jf_uid"] = "01234567-89ab-cdef-0123-456789abcdef",
        ["auth_time"] = 1_800_000_000L,
    };

    private static string Sign(Dictionary<string, object?> claims, byte[] seed, string kid = Kid, string alg = "EdDSA", string typ = "holix-jellyfin-proof+jwt")
    {
        var header = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["alg"] = alg, ["typ"] = typ, ["kid"] = kid }));
        var body = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        var input = Encoding.ASCII.GetBytes(header + "." + body);
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(seed));
        signer.BlockUpdate(input, 0, input.Length);
        return header + "." + body + "." + Base64Url.Encode(signer.GenerateSignature());
    }

    private string Token(Action<Dictionary<string, object?>>? edit = null)
    {
        var c = Claims();
        edit?.Invoke(c);
        return Sign(c, _seed);
    }

    private string Rejected(string token, ProofExpectation? expected = null, CloudLink? link = null, DateTimeOffset? now = null)
    {
        var ex = Assert.Throws<HolixException>(() =>
            CloudProofVerifier.Verify(token, link ?? Link(), expected ?? Child(), now ?? Now, _replay));
        Assert.Equal(403, ex.Status);
        return ex.Code;
    }

    [Fact]
    public void Valid_proof_is_accepted()
    {
        var claims = CloudProofVerifier.Verify(Token(), Link(), Child(), Now, _replay);
        Assert.Equal("cloud-profile-1", claims.ProfileId);
        Assert.Equal("child", claims.Kind);
        Assert.Equal("7+", claims.MaxAgeRating);
        Assert.Equal(1, _replay.Count);
    }

    [Fact]
    public void Expired_proof_is_rejected()
    {
        // After exp + 10 s clock skew.
        Assert.Equal("proof_expired", Rejected(Token(), now: DateTimeOffset.FromUnixTimeSeconds(1_800_000_070)));
        // Still valid within the skew.
        CloudProofVerifier.Verify(Token(), Link(), Child(), DateTimeOffset.FromUnixTimeSeconds(1_800_000_069), _replay);
        // Not yet valid (iat in the future).
        Assert.Equal("proof_not_yet_valid", Rejected(Token(), now: DateTimeOffset.FromUnixTimeSeconds(1_799_999_900)));
    }

    [Fact]
    public void Future_iat_skew_is_ten_seconds()
    {
        Assert.Equal(10, CloudProofVerifier.ClockSkewSeconds);

        // iat = now + 10 s is still accepted; now + 11 s is not.
        CloudProofVerifier.Verify(Token(), Link(), Child(), DateTimeOffset.FromUnixTimeSeconds(1_799_999_990), _replay);
        Assert.Equal("proof_not_yet_valid", Rejected(Token(), now: DateTimeOffset.FromUnixTimeSeconds(1_799_999_989)));
        Assert.Equal("proof_not_yet_valid", Rejected(Token(c => c["nbf"] = 1_800_000_021L)));
    }

    [Fact]
    public void Lifetime_over_sixty_seconds_is_rejected()
    {
        Assert.Equal("invalid_proof", Rejected(Token(c => c["exp"] = 1_800_000_061L)));
        Assert.Equal("invalid_proof", Rejected(Token(c => c["exp"] = 1_800_000_000L)));
    }

    [Fact]
    public void Wrong_aud_and_iss_are_rejected()
    {
        Assert.Equal("proof_wrong_audience", Rejected(Token(c => c["aud"] = "holix-cloud")));
        Assert.Equal("proof_wrong_issuer", Rejected(Token(c => c["iss"] = "fake-cloud")));
        // aud as an array is not accepted either (a single value is expected).
        Assert.Equal("invalid_proof", Rejected(Token(c => c["aud"] = new[] { "holix-jellyfin-plugin" })));
    }

    [Fact]
    public void Wrong_signature_is_rejected()
    {
        // Signed with another key, same kid.
        Assert.Equal("invalid_proof", Rejected(Sign(Claims(), Seed(8))));

        // Token with a tampered body (child → adult).
        var parts = Token().Split('.');
        var forged = Claims();
        forged["kind"] = "adult";
        forged["max_age_rating"] = "18+";
        parts[1] = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(forged));
        Assert.Equal("invalid_proof", Rejected(string.Join('.', parts), new ProofExpectation(Server, ChildUser, "cloud-profile-1", "adult", "18+")));

        // Corrupt signature, missing part, alg change.
        var t = Token();
        Assert.Equal("invalid_proof", Rejected(t[..^4] + "AAAA"));
        Assert.Equal("invalid_proof", Rejected(string.Join('.', t.Split('.')[..2])));
        Assert.Equal("invalid_proof", Rejected(Sign(Claims(), _seed, alg: "none")));
        Assert.Equal("invalid_proof", Rejected(Sign(Claims(), _seed, alg: "HS256")));
        Assert.Equal("invalid_proof", Rejected(Sign(Claims(), _seed, typ: "JWT")));
        Assert.Equal("invalid_proof", Rejected(string.Empty));
    }

    [Fact]
    public void Unknown_kid_returns_separate_code()
    {
        Assert.Equal(CloudProofVerifier.UnknownKeyCode, Rejected(Sign(Claims(), Seed(9), kid: "new-key")));
        // Accepted once the JWKS is refreshed (new key added): key rotation.
        var link = Link();
        link.Keys.Add(new CloudKey { Kid = "new-key", X = PublicX(Seed(9)) });
        CloudProofVerifier.Verify(Sign(Claims(), Seed(9), kid: "new-key"), link, Child(), Now, _replay);
    }

    [Fact]
    public void Replay_is_rejected()
    {
        var token = Token();
        CloudProofVerifier.Verify(token, Link(), Child(), Now, _replay);
        Assert.Equal("proof_replayed", Rejected(token));

        // Failed attempts do not consume the jti: the same proof is usable once the rating matches.
        var other = Token();
        Assert.Equal("age_rating_mismatch", Rejected(other, Child("13+")));
        CloudProofVerifier.Verify(other, Link(), Child(), Now, _replay);
    }

    [Fact]
    public void Wrong_profile_is_rejected()
    {
        // Proof issued for another Jellyfin user.
        Assert.Equal("proof_wrong_profile", Rejected(Token(), new ProofExpectation(Server, Guid.NewGuid(), "cloud-profile-1", "child", "7+")));
        // Correct Jellyfin user but different cloud profile (a PIN-less profile's proof used for a PIN-protected one).
        Assert.Equal("proof_wrong_profile", Rejected(Token(c => c["pid"] = "pinless-profile")));
        // Another household, another server.
        Assert.Equal("proof_wrong_household", Rejected(Token(c => c["hid"] = "neighbour-household")));
        Assert.Equal("proof_wrong_server", Rejected(Token(c => c["jf_sid"] = Guid.NewGuid().ToString("N"))));
        // Kind mismatch.
        Assert.Equal("proof_kind_mismatch", Rejected(Token(c => c["kind"] = "adult")));
    }

    [Fact]
    public void Child_rating_must_match_cloud()
    {
        // Cloud says 7+, plugin has 13+ (e.g. raised with the owner token): rejected.
        Assert.Equal("age_rating_mismatch", Rejected(Token(), Child("13+")));
        // Rating is not checked for adults (managed by the owner/settings).
        CloudProofVerifier.Verify(
            Token(c => { c["kind"] = "adult"; c["max_age_rating"] = "16+"; }),
            Link(),
            new ProofExpectation(Server, ChildUser, "cloud-profile-1", "adult", "18+"),
            Now,
            _replay);
        // Kind is not checked for the owner profile either.
        CloudProofVerifier.Verify(Token(), Link(), new ProofExpectation(Server, ChildUser, "cloud-profile-1", null, null), Now, _replay);
    }

    [Fact]
    public void Missing_claim_is_rejected()
    {
        Assert.Equal("invalid_proof", Rejected(Token(c => c.Remove("jti"))));
        Assert.Equal("invalid_proof", Rejected(Token(c => c.Remove("exp"))));
        Assert.Equal("invalid_proof", Rejected(Token(c => c.Remove("hid"))));
    }

    /// <summary>
    /// Golden proof produced by holix-cloud (Rust, ed25519-dalek) with seed [7; 32]. Shows that both
    /// implementations agree on the format.
    /// </summary>
    [Fact]
    public void Rust_holix_cloud_golden_proof_is_verified()
    {
        const string Golden = "eyJhbGciOiJFZERTQSIsInR5cCI6ImhvbGl4LWplbGx5ZmluLXByb29mK2p3dCIsImtpZCI6Ii0tNklNNWwwT29zTGo5eVdza0lTWWhVQTNuXzNDVVJRa21yWU1TaGFfY2sifQ.eyJpc3MiOiJob2xpeC1jbG91ZCIsImF1ZCI6ImhvbGl4LWplbGx5ZmluLXBsdWdpbiIsImlhdCI6MTgwMDAwMDAwMCwibmJmIjoxODAwMDAwMDAwLCJleHAiOjE4MDAwMDAwNjAsImp0aSI6ImotMSIsImhpZCI6ImhhbmUiLCJwaWQiOiJwcm9maWwiLCJraW5kIjoiY2hpbGQiLCJtYXhfYWdlX3JhdGluZyI6IjcrIiwiamZfc2lkIjoiYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWEiLCJqZl91aWQiOiJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYmJiYiIsImF1dGhfdGltZSI6MTgwMDAwMDAwMH0.nIMFXgUlJw2V0u0XExbuDSGge5iQxUlONw8kj9hYd4jG9IS1IA9ZF-mZP_5uyF1FLB1d0Z2EnETFpKj3rFLcCQ";
        var link = new CloudLink { HouseholdId = "hane", Issuer = "holix-cloud" };
        link.Keys.Add(new CloudKey { Kid = "--6IM5l0OosLj9yWskISYhUA3n_3CURQkmrYMSha_ck", X = "6kpsY-KcUgq-9VB7Ey7F-ZVHdq6-vnuSQh7qaRRG0iw" });
        Assert.Equal(link.Keys[0].X, PublicX(Seed(7)));

        var expected = new ProofExpectation(new string('a', 32), Guid.Parse(new string('b', 32)), "profil", "child", "7+");
        var claims = CloudProofVerifier.Verify(Golden, link, expected, Now, _replay);
        Assert.Equal("j-1", claims.Jti);
        Assert.Equal(1_800_000_000L, claims.AuthTime);

        // Signing the same body with the same seed in C# yields the same signature (Ed25519 is deterministic).
        var parts = Golden.Split('.');
        var input = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(Seed(7)));
        signer.BlockUpdate(input, 0, input.Length);
        Assert.Equal(parts[2], Base64Url.Encode(signer.GenerateSignature()));
    }

    [Fact]
    public void Replay_cache_forgets_expired_entries()
    {
        var cache = new ProofReplayCache();
        Assert.True(cache.TryUse("a", Now.AddSeconds(10), Now));
        Assert.False(cache.TryUse("a", Now.AddSeconds(10), Now));
        cache.Prune(Now.AddSeconds(11));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Jwks_takes_only_ed25519_signing_keys()
    {
        var keys = CloudClient.ToKeys(new Jwks
        {
            Keys =
            [
                new Jwk { Kty = "OKP", Crv = "Ed25519", Alg = "EdDSA", Use = "sig", Kid = "k1", X = PublicX(_seed) },
                new Jwk { Kty = "OKP", Crv = "X25519", Kid = "k2", X = PublicX(_seed) },
                new Jwk { Kty = "RSA", Kid = "k3", X = PublicX(_seed) },
                new Jwk { Kty = "OKP", Crv = "Ed25519", Use = "enc", Kid = "k4", X = PublicX(_seed) },
                new Jwk { Kty = "OKP", Crv = "Ed25519", Kid = "k5", X = "short" },
                new Jwk { Kty = "OKP", Crv = "Ed25519", X = PublicX(_seed) },
            ],
        });
        Assert.Equal(["k1"], keys.ConvertAll(k => k.Kid));
    }

    [Theory]
    [InlineData("https://api.holixplay.com", "https://api.holixplay.com")]
    [InlineData("https://api.holixplay.com/", "https://api.holixplay.com")]
    [InlineData("https://example.com/holix/", "https://example.com/holix")]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    [InlineData("http://127.0.0.1:8080", "http://127.0.0.1:8080")]
    public void Cloud_url_accepted(string url, string root)
    {
        var uri = CloudClient.ValidateBaseUrl(url);
        Assert.Equal(root, CloudClient.Root(uri));
        Assert.Equal(root + "/v1/jellyfin/link", CloudClient.Combine(uri, "/v1/jellyfin/link").ToString());
    }

    [Theory]
    [InlineData("http://api.holixplay.com", "insecure_cloud_url")]
    [InlineData("", "cloud_not_configured")]
    [InlineData("api.holixplay.com", "cloud_not_configured")]
    [InlineData("https://a:b@api.holixplay.com", "invalid_cloud_url")]
    public void Cloud_url_rejected(string url, string code)
    {
        Assert.Equal(code, Assert.Throws<HolixException>(() => CloudClient.ValidateBaseUrl(url)).Code);
    }
}
