using System;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.Holix.StreamAuth;

/// <summary>What a forwardAuth request is about: the original method, path and the client's token.</summary>
/// <param name="Method">The original method (<c>X-Forwarded-Method</c>, else the request's own).</param>
/// <param name="Uri">The original path and query (<c>X-Forwarded-Uri</c>, else the request's own).</param>
/// <param name="Token">The Jellyfin token, if any.</param>
public sealed record StreamRequest(string Method, string Uri, string? Token)
{
    /// <summary>Reads a forwardAuth request.</summary>
    /// <param name="request">The request (from the reverse proxy).</param>
    /// <returns>The parsed request.</returns>
    public static StreamRequest From(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var forwardedUri = First(request.Headers["X-Forwarded-Uri"]);
        var uri = forwardedUri ?? (request.PathBase + request.Path + request.QueryString).ToString();
        var method = First(request.Headers["X-Forwarded-Method"]) ?? (string.IsNullOrEmpty(request.Method) ? HttpMethods.Get : request.Method);
        var token = StreamTokenReader.FromHeaders(request.Headers)
            ?? StreamTokenReader.FromUri(forwardedUri)
            ?? StreamTokenReader.FromQuery(request.Query);
        return new StreamRequest(method.Trim(), uri, token);
    }

    private static string? First(StringValues values)
    {
        var v = values.Count > 0 ? values[0] : null;
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }
}

/// <summary>
/// Extracts a Jellyfin access token like Jellyfin does: <c>Authorization: MediaBrowser … Token="…"</c>
/// (also the <c>Emby</c> scheme and <c>X-Emby-Authorization</c>), <c>X-Emby-Token</c>,
/// <c>X-MediaBrowser-Token</c>, then the <c>api_key</c> / <c>ApiKey</c> query parameter.
/// </summary>
public static class StreamTokenReader
{
    /// <summary>Reads the token from headers.</summary>
    /// <param name="headers">The headers.</param>
    /// <returns>The token or <c>null</c>.</returns>
    public static string? FromHeaders(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        return FromAuthorization(headers.Authorization)
            ?? FromAuthorization(headers["X-Emby-Authorization"])
            ?? NonEmpty(headers["X-Emby-Token"])
            ?? NonEmpty(headers["X-MediaBrowser-Token"]);
    }

    /// <summary>Reads <c>api_key</c> / <c>ApiKey</c> from a URI's query string.</summary>
    /// <param name="uri">Path and query (or an absolute URI).</param>
    /// <returns>The token or <c>null</c>.</returns>
    public static string? FromUri(string? uri)
    {
        if (string.IsNullOrEmpty(uri))
        {
            return null;
        }

        var q = uri.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            return null;
        }

        var query = uri[q..];
        var hash = query.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            query = query[..hash];
        }

        return FromQuery(new QueryCollection(QueryHelpers.ParseQuery(query)));
    }

    /// <summary>Reads <c>api_key</c> / <c>ApiKey</c> from a query collection (case-insensitive names).</summary>
    /// <param name="query">The query.</param>
    /// <returns>The token or <c>null</c>.</returns>
    public static string? FromQuery(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return NonEmpty(query["api_key"]) ?? NonEmpty(query["ApiKey"]);
    }

    /// <summary>Parses <c>MediaBrowser Client="…", Token="…"</c>.</summary>
    /// <param name="values">The header values.</param>
    /// <returns>The token or <c>null</c>.</returns>
    internal static string? FromAuthorization(StringValues values)
    {
        foreach (var header in values)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            var h = header.AsSpan().Trim();
            var space = h.IndexOf(' ');
            if (space < 0)
            {
                continue;
            }

            var scheme = h[..space];
            if (!scheme.Equals("MediaBrowser", StringComparison.OrdinalIgnoreCase)
                && !scheme.Equals("Emby", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var part in h[(space + 1)..].ToString().Split(','))
            {
                var eq = part.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0 || !part.AsSpan(0, eq).Trim().Equals("Token", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = WebUtility.UrlDecode(part[(eq + 1)..].Trim().Trim('"'));
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value.Trim();
                }
            }
        }

        return null;
    }

    private static string? NonEmpty(StringValues values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
            {
                return v.Trim();
            }
        }

        return null;
    }
}
