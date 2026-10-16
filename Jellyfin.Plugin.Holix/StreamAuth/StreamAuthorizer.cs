using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Holix.StreamAuth;

/// <summary>Who a token belongs to.</summary>
/// <param name="UserId">The Jellyfin user (<c>null</c> for an API key).</param>
/// <param name="IsApiKey">Whether the token is a server API key (full access).</param>
public sealed record StreamPrincipal(Guid? UserId, bool IsApiKey);

/// <summary>The Jellyfin side of the stream check (token lookup and item visibility).</summary>
public interface IStreamAccessBackend
{
    /// <summary>Looks up a token (may hit the database).</summary>
    /// <param name="token">The access token.</param>
    /// <returns>The principal, or <c>null</c> if the token is not valid.</returns>
    Task<StreamPrincipal?> ResolveTokenAsync(string token);

    /// <summary>Checks the user's access to an item, with Jellyfin's own rules.</summary>
    /// <param name="userId">The user.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>204, 401 (user gone), 403 (disabled or not visible) or 404 (no such item).</returns>
    int CheckAccess(Guid userId, Guid itemId);
}

/// <summary>
/// The decision behind <c>GET /Holix/StreamAuth</c>, with a (token, item) → status cache (60 s, at most 10 000
/// entries) and a token → principal cache (same limits), because HLS players call it for every segment.
/// Consequence: revoking a token, disabling a user or changing a parental limit takes effect within 60 s.
/// </summary>
public sealed class StreamAuthorizer
{
    /// <summary>How long decisions are cached.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(60);

    /// <summary>The maximum number of cached decisions (and of cached tokens).</summary>
    public const int MaxEntries = 10_000;

    private readonly IStreamAccessBackend _backend;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<(string Token, Guid Item), Entry<int>> _decisions = new();
    private readonly ConcurrentDictionary<string, Entry<StreamPrincipal>> _tokens = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="StreamAuthorizer"/> class.</summary>
    /// <param name="backend">The Jellyfin backend.</param>
    /// <param name="time">The clock.</param>
    public StreamAuthorizer(IStreamAccessBackend backend, TimeProvider time)
    {
        _backend = backend;
        _time = time;
    }

    /// <summary>Gets the number of cached decisions.</summary>
    public int CachedDecisions => _decisions.Count;

    /// <summary>Decides a forwardAuth request.</summary>
    /// <param name="request">The original request.</param>
    /// <returns>The HTTP status: 204 allow, 401, 403, 404.</returns>
    public async Task<int> AuthorizeAsync(StreamRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // CORS pre-flight requests carry no credentials; Jellyfin answers them itself.
        if (HttpMethods.IsOptions(request.Method))
        {
            return StatusCodes.Status204NoContent;
        }

        // Only stream paths are checked; anything else is passed through to Jellyfin's own authorization.
        if (!StreamPath.TryGetItemId(request.Uri, out var itemId))
        {
            return StatusCodes.Status204NoContent;
        }

        if (string.IsNullOrEmpty(request.Token))
        {
            return StatusCodes.Status401Unauthorized;
        }

        var now = _time.GetUtcNow();
        var key = (request.Token, itemId);
        if (_decisions.TryGetValue(key, out var cached) && cached.Expires > now)
        {
            return cached.Value;
        }

        var principal = await ResolveAsync(request.Token, now).ConfigureAwait(false);
        int status;
        if (principal is null)
        {
            status = StatusCodes.Status401Unauthorized;
        }
        else if (principal.IsApiKey)
        {
            status = StatusCodes.Status204NoContent;
        }
        else
        {
            status = principal.UserId is { } uid ? _backend.CheckAccess(uid, itemId) : StatusCodes.Status401Unauthorized;
        }

        // 401s are not cached: random tokens must not be able to evict real entries.
        if (status != StatusCodes.Status401Unauthorized)
        {
            Put(_decisions, key, new Entry<int>(status, now + CacheLifetime), now);
        }

        return status;
    }

    private async Task<StreamPrincipal?> ResolveAsync(string token, DateTimeOffset now)
    {
        if (_tokens.TryGetValue(token, out var cached) && cached.Expires > now)
        {
            return cached.Value;
        }

        var principal = await _backend.ResolveTokenAsync(token).ConfigureAwait(false);
        if (principal is not null)
        {
            Put(_tokens, token, new Entry<StreamPrincipal>(principal, now + CacheLifetime), now);
        }
        else
        {
            _tokens.TryRemove(token, out _);
        }

        return principal;
    }

    private static void Put<TKey, TValue>(ConcurrentDictionary<TKey, Entry<TValue>> cache, TKey key, Entry<TValue> entry, DateTimeOffset now)
        where TKey : notnull
    {
        if (cache.Count >= MaxEntries && !cache.ContainsKey(key))
        {
            foreach (var kv in cache.Where(kv => kv.Value.Expires <= now).ToList())
            {
                cache.TryRemove(kv.Key, out _);
            }

            if (cache.Count >= MaxEntries)
            {
                // Still full of live entries: start over rather than grow (bounded memory; costs only lookups).
                cache.Clear();
            }
        }

        cache[key] = entry;
    }

    private readonly record struct Entry<T>(T Value, DateTimeOffset Expires);
}
