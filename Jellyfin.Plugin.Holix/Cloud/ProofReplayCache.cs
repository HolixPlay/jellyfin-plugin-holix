using System;
using System.Collections.Concurrent;
using System.Linq;

namespace Jellyfin.Plugin.Holix.Cloud;

/// <summary>
/// Used proof ids (<c>jti</c>), kept in memory until the proof expires. Proofs live at most 60 s
/// (plus clock skew), so memory is enough; the list is cleared on server restart (see README: a proof
/// captured in that window could be used once more, still only together with the owner's token).
/// </summary>
public sealed class ProofReplayCache
{
    private const int PruneThreshold = 1024;
    private const int MaxEntries = 100_000;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _used = new(StringComparer.Ordinal);

    /// <summary>Gets the number of remembered ids.</summary>
    public int Count => _used.Count;

    /// <summary>Marks the id as used.</summary>
    /// <param name="jti">The proof id.</param>
    /// <param name="forgetAfter">May be forgotten after this instant (the proof has expired by then).</param>
    /// <param name="now">The current time.</param>
    /// <returns><c>true</c> on first use; <c>false</c> if already used.</returns>
    public bool TryUse(string jti, DateTimeOffset forgetAfter, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(jti);
        if (_used.Count >= PruneThreshold)
        {
            Prune(now);
        }

        if (_used.Count >= MaxEntries)
        {
            // Bound memory under attack or bugs: fail closed (new proofs are rejected too).
            return false;
        }

        return _used.TryAdd(jti, forgetAfter);
    }

    /// <summary>Removes expired entries.</summary>
    /// <param name="now">The current time.</param>
    public void Prune(DateTimeOffset now)
    {
        foreach (var kv in _used.Where(kv => kv.Value <= now).ToList())
        {
            _used.TryRemove(kv.Key, out _);
        }
    }
}
