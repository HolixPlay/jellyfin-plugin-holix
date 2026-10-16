using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Holix.Households;

namespace Jellyfin.Plugin.Holix.Cloud;

/// <summary>
/// The only two calls made to holix-cloud: redeeming a link code (<c>POST /v1/jellyfin/link</c>) and
/// fetching the JWKS (<c>GET /v1/.well-known/jwks.json</c>). The plugin sends nothing else.
/// </summary>
public sealed class CloudClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Initializes a new instance of the <see cref="CloudClient"/> class.</summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public CloudClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Validates the cloud URL: https is required (plain http only for <c>localhost</c>/loopback).
    /// </summary>
    /// <param name="url">The URL.</param>
    /// <returns>The base URL without a trailing <c>/</c>.</returns>
    public static Uri ValidateBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim().TrimEnd('/'), UriKind.Absolute, out var uri))
        {
            throw HolixException.Conflict("cloud_not_configured", "The holix-cloud URL (CloudUrl) is not set in the plugin settings.");
        }

        var loopback = uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && loopback))
        {
            throw HolixException.BadRequest("insecure_cloud_url", "The holix-cloud URL must use https.");
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw HolixException.BadRequest("invalid_cloud_url", "The holix-cloud URL must not contain a query, fragment or user info.");
        }

        return uri;
    }

    /// <summary>Redeems a link code.</summary>
    /// <param name="baseUrl">The cloud base URL.</param>
    /// <param name="code">The link code.</param>
    /// <param name="serverId">This server's id.</param>
    /// <param name="serverName">The server name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The cloud link.</returns>
    public async Task<CloudLink> RedeemAsync(Uri baseUrl, string code, string serverId, string? serverName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseUrl);
        using var client = Create();
        using var response = await Send(() => client.PostAsJsonAsync(
            Combine(baseUrl, "/v1/jellyfin/link"),
            new RedeemRequest { Code = code, ServerId = serverId, ServerName = serverName },
            cancellationToken)).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
        {
            throw HolixException.BadRequest("invalid_link_code", "The link code is invalid or has expired.");
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new HolixException(429, "too_many_attempts", "holix-cloud: too many attempts; try again later.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HolixException(502, "cloud_error", $"holix-cloud link request failed ({(int)response.StatusCode}).");
        }

        var body = await Read<RedeemResponse>(response, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(body.HouseholdId) || string.IsNullOrEmpty(body.Issuer)
            || !string.Equals(body.Audience, CloudProofVerifier.Audience, StringComparison.Ordinal)
            || !string.Equals(CloudProofVerifier.NormalizeId(body.ServerId), serverId, StringComparison.Ordinal))
        {
            throw new HolixException(502, "cloud_error", "Unexpected holix-cloud link response.");
        }

        var keys = ToKeys(body.Jwks);
        if (keys.Count == 0)
        {
            throw new HolixException(502, "cloud_error", "holix-cloud publishes no Ed25519 keys.");
        }

        var now = DateTime.UtcNow;
        var link = new CloudLink
        {
            CloudUrl = Root(baseUrl),
            HouseholdId = body.HouseholdId,
            Issuer = body.Issuer,
            JwksPath = string.IsNullOrEmpty(body.JwksUri) || !body.JwksUri.StartsWith('/') ? CloudProofVerifier.DefaultJwksPath : body.JwksUri,
            LinkedUtc = now,
            KeysFetchedUtc = now,
        };
        link.Keys.AddRange(keys);
        return link;
    }

    /// <summary>Re-fetches the JWKS.</summary>
    /// <param name="link">The cloud link.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The Ed25519 keys.</returns>
    public async Task<IReadOnlyList<CloudKey>> FetchKeysAsync(CloudLink link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        var baseUrl = ValidateBaseUrl(link.CloudUrl);
        using var client = Create();
        using var response = await Send(() => client.GetAsync(Combine(baseUrl, link.JwksPath), cancellationToken)).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HolixException(502, "cloud_error", $"Could not fetch the holix-cloud JWKS ({(int)response.StatusCode}).");
        }

        return ToKeys(await Read<Jwks>(response, cancellationToken).ConfigureAwait(false));
    }

    internal static string Root(Uri baseUrl) => baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');

    internal static Uri Combine(Uri baseUrl, string path) => new(Root(baseUrl) + path);

    internal static List<CloudKey> ToKeys(Jwks? jwks) =>
        (jwks?.Keys ?? [])
            .Where(k => k.Kty == "OKP" && k.Crv == "Ed25519" && (k.Alg is null || k.Alg == "EdDSA")
                && (k.Use is null || k.Use == "sig") && !string.IsNullOrEmpty(k.Kid) && IsKey(k.X))
            .Select(k => new CloudKey { Kid = k.Kid!, X = k.X! })
            .ToList();

    private static bool IsKey(string? x)
    {
        try
        {
            return x is not null && Base64Url.Decode(x).Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static async Task<HttpResponseMessage> Send(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            return await send().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new HolixException(502, "cloud_unreachable", "holix-cloud is unreachable: " + ex.Message);
        }
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false)
                ?? throw new HolixException(502, "cloud_error", "holix-cloud returned an empty response.");
        }
        catch (JsonException)
        {
            throw new HolixException(502, "cloud_error", "Could not parse the holix-cloud response.");
        }
    }

    private HttpClient Create()
    {
        // Jellyfin's default named client (proxy settings, User-Agent).
        var client = _httpClientFactory.CreateClient(MediaBrowser.Common.Net.NamedClient.Default);
        client.Timeout = Timeout;
        return client;
    }

    private sealed class RedeemRequest
    {
        [JsonPropertyName("code")]
        public string Code { get; set; } = string.Empty;

        [JsonPropertyName("server_id")]
        public string ServerId { get; set; } = string.Empty;

        [JsonPropertyName("server_name")]
        public string? ServerName { get; set; }
    }

    private sealed class RedeemResponse
    {
        [JsonPropertyName("household_id")]
        public string? HouseholdId { get; set; }

        [JsonPropertyName("server_id")]
        public string? ServerId { get; set; }

        [JsonPropertyName("issuer")]
        public string? Issuer { get; set; }

        [JsonPropertyName("audience")]
        public string? Audience { get; set; }

        [JsonPropertyName("jwks_uri")]
        public string? JwksUri { get; set; }

        [JsonPropertyName("jwks")]
        public Jwks? Jwks { get; set; }
    }
}

/// <summary>A JWKS document.</summary>
public sealed class Jwks
{
    /// <summary>Gets or sets the keys.</summary>
    [JsonPropertyName("keys")]
    public IReadOnlyList<Jwk>? Keys { get; set; }
}

/// <summary>A key in a JWKS (OKP/Ed25519 fields only).</summary>
public sealed class Jwk
{
    /// <summary>Gets or sets the key type.</summary>
    [JsonPropertyName("kty")]
    public string? Kty { get; set; }

    /// <summary>Gets or sets the curve.</summary>
    [JsonPropertyName("crv")]
    public string? Crv { get; set; }

    /// <summary>Gets or sets the algorithm.</summary>
    [JsonPropertyName("alg")]
    public string? Alg { get; set; }

    /// <summary>Gets or sets the use.</summary>
    [JsonPropertyName("use")]
    public string? Use { get; set; }

    /// <summary>Gets or sets the key id.</summary>
    [JsonPropertyName("kid")]
    public string? Kid { get; set; }

    /// <summary>Gets or sets the public key.</summary>
    [JsonPropertyName("x")]
    public string? X { get; set; }
}
