using System;

namespace Jellyfin.Plugin.Holix.StreamAuth;

/// <summary>
/// Recognises Jellyfin media stream paths and extracts the item id. Matching is case-insensitive and allows a
/// base-URL prefix (e.g. <c>/jellyfin/Videos/…</c>). Recognised (<c>{id}</c> = GUID with or without dashes):
/// <list type="bullet">
/// <item><c>/Videos/{id}/stream</c>, <c>/Videos/{id}/stream.{ext}</c>, <c>/Videos/{id}/master.m3u8</c>,
/// <c>/Videos/{id}/main.m3u8</c>, <c>/Videos/{id}/live.m3u8</c></item>
/// <item><c>/Videos/{id}/hls1/…</c>, <c>/Videos/{id}/hls/…</c> and <c>/Videos/{id}/{mediaSourceId}/…</c>
/// (any deeper path: segments, subtitles, attachments, trickplay)</item>
/// <item><c>/Audio/{id}/stream</c>, <c>/Audio/{id}/stream.{ext}</c>, <c>/Audio/{id}/universal</c>,
/// <c>/Audio/{id}/master.m3u8</c>, <c>/Audio/{id}/main.m3u8</c>, <c>/Audio/{id}/hls1/…</c></item>
/// </list>
/// </summary>
public static class StreamPath
{
    /// <summary>Extracts the item id from a stream path.</summary>
    /// <param name="pathAndQuery">The path, optionally with a query string or fragment.</param>
    /// <param name="itemId">The item id.</param>
    /// <returns><c>true</c> if the path is a recognised stream path.</returns>
    public static bool TryGetItemId(string? pathAndQuery, out Guid itemId)
    {
        itemId = Guid.Empty;
        if (string.IsNullOrEmpty(pathAndQuery))
        {
            return false;
        }

        var path = pathAndQuery;
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            path = path[..cut];
        }

        // Absolute URI (some proxies send one).
        if (path.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(path, UriKind.Absolute, out var abs))
        {
            path = abs.AbsolutePath;
        }

        path = Uri.UnescapeDataString(path);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // The first "Videos"/"Audio" segment followed by a GUID decides (a base URL may precede it).
        for (var i = 0; i + 2 < segments.Length; i++)
        {
            var isVideo = segments[i].Equals("Videos", StringComparison.OrdinalIgnoreCase);
            var isAudio = !isVideo && segments[i].Equals("Audio", StringComparison.OrdinalIgnoreCase);
            if (!isVideo && !isAudio)
            {
                continue;
            }

            if (!TryParseId(segments[i + 1], out var id))
            {
                continue;
            }

            var rest = segments.AsSpan(i + 2);
            if (Matches(rest, isVideo))
            {
                itemId = id;
                return true;
            }

            return false;
        }

        return false;
    }

    /// <summary>Parses a GUID with or without dashes (<c>N</c> or <c>D</c> format).</summary>
    /// <param name="value">The value.</param>
    /// <param name="id">The GUID.</param>
    /// <returns><c>true</c> if valid.</returns>
    public static bool TryParseId(string value, out Guid id) =>
        Guid.TryParseExact(value, "N", out id) || Guid.TryParseExact(value, "D", out id);

    private static bool Matches(ReadOnlySpan<string> rest, bool isVideo)
    {
        var first = rest[0];
        if (rest.Length == 1)
        {
            return IsStream(first)
                || first.Equals("master.m3u8", StringComparison.OrdinalIgnoreCase)
                || first.Equals("main.m3u8", StringComparison.OrdinalIgnoreCase)
                || (isVideo && first.Equals("live.m3u8", StringComparison.OrdinalIgnoreCase))
                || (!isVideo && first.Equals("universal", StringComparison.OrdinalIgnoreCase));
        }

        // Deeper paths: HLS segments for both; for video also /{mediaSourceId}/… (and hls/…, Trickplay/…).
        return isVideo || first.Equals("hls1", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsStream(string s)
    {
        if (s.Equals("stream", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!s.StartsWith("stream.", StringComparison.OrdinalIgnoreCase) || s.Length == "stream.".Length)
        {
            return false;
        }

        foreach (var ch in s.AsSpan("stream.".Length))
        {
            if (!char.IsAsciiLetterOrDigit(ch))
            {
                return false;
            }
        }

        return true;
    }
}
