using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Holix.Cloud;
using Jellyfin.Plugin.Holix.Households;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Jellyfin.Plugin.Holix.Tests;

/// <summary>Verification of the holix-cloud session proof (account-first sign-in, <c>CloudSession</c>).</summary>
public sealed class CloudSessionVerifierTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_010);
    private const string Server = "6f9619ff8b86d011b42d00c04fc964ff";
    private const string Device = "tv-salon";
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

    private CloudLink Link()
    {
        var link = new CloudLink { CloudUrl = "https://cloud.test", HouseholdId = "household-1", Issuer = "holix-cloud" };
        link.Keys.Add(new CloudKey { Kid = Kid, X = PublicX(_seed) });
        return link;
    }

    private static Dictionary<string, object?> Claims() => new()
    {
        ["iss"] = "holix-cloud",
        ["aud"] = "holix-jellyfin-plugin",
        ["iat"] = 1_800_000_000L,
        ["nbf"] = 1_800_000_000L,
        ["exp"] = 1_800_000_060L,
        ["jti"] = Guid.NewGuid().ToString(),
        ["sub"] = "household-1",
        ["hid"] = "household-1",
        ["jf_sid"] = "6F9619FF-8B86-D011-B42D-00C04FC964FF",
        ["device_id"] = Device,
    };

    private static string Sign(Dictionary<string, object?> claims, byte[] seed, string typ = CloudSessionVerifier.TokenType, string kid = Kid)
    {
        var header = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["alg"] = "EdDSA", ["typ"] = typ, ["kid"] = kid }));
        var body = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        var input = Encoding.ASCII.GetBytes(header + "." + body);
        var signer = new Ed25519Signer();
        signer.Init(true, new Ed25519PrivateKeyParameters(seed));
        signer.BlockUpdate(input, 0, input.Length);
        return header + "." + body + "." + Base64Url.Encode(signer.GenerateSignature());
    }

    private string Token(Action<Dictionary<string, object?>>? edit = null, string typ = CloudSessionVerifier.TokenType, byte[]? seed = null)
    {
        var c = Claims();
        edit?.Invoke(c);
        return Sign(c, seed ?? _seed, typ);
    }

    private string Rejected(string token, string device = Device, DateTimeOffset? now = null, CloudLink? link = null)
    {
        var ex = Assert.Throws<HolixException>(() => CloudSessionVerifier.Verify(token, link ?? Link(), Server, device, now ?? Now, _replay));
        Assert.Equal(403, ex.Status);
        return ex.Code;
    }

    [Fact]
    public void Valid_proof_is_accepted_once()
    {
        var token = Token();
        Assert.Equal("household-1", CloudSessionVerifier.ReadHouseholdId(token));
        var claims = CloudSessionVerifier.Verify(token, Link(), Server, Device, Now, _replay);
        Assert.Equal(Device, claims.DeviceId);
        Assert.Equal("household-1", claims.Subject);
        Assert.Equal("proof_replayed", Rejected(token));
    }

    [Fact]
    public void Check_does_not_consume_the_proof_until_consume()
    {
        // The service checks the household screen limit between Check and Consume: a refused request keeps the proof.
        var token = Token();
        var claims = CloudSessionVerifier.Check(token, Link(), Server, Device, Now);
        CloudSessionVerifier.Check(token, Link(), Server, Device, Now);
        CloudSessionVerifier.Consume(claims, Now, _replay);
        var ex = Assert.Throws<HolixException>(() => CloudSessionVerifier.Consume(claims, Now, _replay));
        Assert.Equal("proof_replayed", ex.Code);
    }

    [Fact]
    public void Proof_is_bound_to_device_server_and_household()
    {
        Assert.Equal("proof_wrong_device", Rejected(Token(), device: "baska-cihaz"));
        Assert.Equal("proof_wrong_server", Rejected(Token(c => c["jf_sid"] = new string('b', 32))));
        Assert.Equal("proof_wrong_household", Rejected(Token(c => c["hid"] = "household-2")));
        Assert.Equal("proof_wrong_issuer", Rejected(Token(c => c["iss"] = "evil-cloud")));
        Assert.Equal("proof_wrong_audience", Rejected(Token(c => c["aud"] = "something-else")));
    }

    [Fact]
    public void Time_rules_and_lifetime_are_enforced()
    {
        Assert.Equal("proof_expired", Rejected(Token(), now: DateTimeOffset.FromUnixTimeSeconds(1_800_000_070)));
        Assert.Equal("proof_not_yet_valid", Rejected(Token(), now: DateTimeOffset.FromUnixTimeSeconds(1_799_999_980)));
        Assert.Equal("invalid_proof", Rejected(Token(c => c["exp"] = 1_800_000_061L)));
        Assert.Equal("invalid_proof", Rejected(Token(c => c.Remove("device_id"))));
        Assert.Equal("invalid_proof", Rejected(Token(c => c.Remove("sub"))));
    }

    [Fact]
    public void Profile_proof_cannot_stand_in_for_a_session_proof_and_vice_versa()
    {
        // Same key, same household, but typ holix-jellyfin-proof+jwt.
        Assert.Equal("invalid_proof", Rejected(Token(typ: CloudProofVerifier.TokenType)));

        var session = Token();
        var expected = new ProofExpectation(Server, Guid.NewGuid(), "cloud-profile-1", null, null);
        var ex = Assert.Throws<HolixException>(() => CloudProofVerifier.Verify(session, Link(), expected, Now, _replay));
        Assert.Equal("invalid_proof", ex.Code);
    }

    [Fact]
    public void Forged_or_unknown_key_proofs_are_rejected()
    {
        Assert.Equal("invalid_proof", Rejected(Token(seed: Seed(8))));

        var parts = Token().Split('.');
        var forged = Claims();
        forged["device_id"] = "attacker";
        parts[1] = Base64Url.Encode(JsonSerializer.SerializeToUtf8Bytes(forged));
        Assert.Equal("invalid_proof", Rejected(string.Join('.', parts), device: "attacker"));

        Assert.Equal(CloudProofVerifier.UnknownKeyCode, Rejected(Sign(Claims(), _seed, kid: "rotated")));
        Assert.Equal("invalid_proof", Assert.Throws<HolixException>(() => CloudSessionVerifier.ReadHouseholdId("abc")).Code);
        Assert.Equal("invalid_proof", Assert.Throws<HolixException>(() => CloudSessionVerifier.ReadHouseholdId(null)).Code);
    }

    /// <summary>
    /// Golden session proof produced by holix-cloud 0.6.0 (Rust) with seed [7; 32]
    /// (<c>tests/fixtures/golden_session_proof.txt</c> there): both implementations agree on the format.
    /// </summary>
    [Fact]
    public void Rust_holix_cloud_golden_session_proof_is_verified()
    {
        const string Golden = "eyJhbGciOiJFZERTQSIsInR5cCI6ImhvbGl4LWplbGx5ZmluLXNlc3Npb24rand0Iiwia2lkIjoiLS02SU01bDBPb3NMajl5V3NrSVNZaFVBM25fM0NVUlFrbXJZTVNoYV9jayJ9.eyJpc3MiOiJob2xpeC1jbG91ZCIsImF1ZCI6ImhvbGl4LWplbGx5ZmluLXBsdWdpbiIsImlhdCI6MTgwMDAwMDAwMCwibmJmIjoxODAwMDAwMDAwLCJleHAiOjE4MDAwMDAwNjAsImp0aSI6InMtMSIsInN1YiI6ImhhbmUiLCJoaWQiOiJoYW5lIiwiamZfc2lkIjoiYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWEiLCJkZXZpY2VfaWQiOiJ0di1zYWxvbiJ9.I9NH8piXDeh7jNPCXEr0RW2s2QozVJNFg-kvz0MVsTimF5maZSa3nnqePuDnmJFejSFJXBnYoPZk_nxlozqNAw";
        var link = new CloudLink { HouseholdId = "hane", Issuer = "holix-cloud" };
        link.Keys.Add(new CloudKey { Kid = "--6IM5l0OosLj9yWskISYhUA3n_3CURQkmrYMSha_ck", X = "6kpsY-KcUgq-9VB7Ey7F-ZVHdq6-vnuSQh7qaRRG0iw" });

        Assert.Equal("hane", CloudSessionVerifier.ReadHouseholdId(Golden));
        var claims = CloudSessionVerifier.Verify(Golden, link, new string('a', 32), "tv-salon", Now, _replay);
        Assert.Equal("s-1", claims.Jti);
        Assert.Equal("hane", claims.Subject);
    }
}
