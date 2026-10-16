using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;

namespace Jellyfin.Plugin.Holix.StreamAuth;

/// <summary>
/// Which addresses may call <c>/Holix/StreamAuth</c>: the configured CIDR list, or (when empty) loopback and
/// private networks.
/// </summary>
public sealed class ProxyAllowList
{
    private static readonly IPNetwork[] PrivateNetworks =
    [
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("fc00::/7"),
    ];

    private static (string[]? Source, ProxyAllowList List)? _last;

    private readonly IPNetwork[] _networks;

    private ProxyAllowList(IPNetwork[] networks, IReadOnlyList<string> invalid)
    {
        _networks = networks;
        Invalid = invalid;
    }

    /// <summary>Gets the entries that could not be parsed (ignored).</summary>
    public IReadOnlyList<string> Invalid { get; }

    /// <summary>Gets a value indicating whether the default (loopback + private networks) is used.</summary>
    public bool IsDefault => ReferenceEquals(_networks, PrivateNetworks);

    /// <summary>Returns the parsed list for a configuration array, reusing the last result for the same array.</summary>
    /// <param name="entries">The configured entries (the configuration object is replaced when saved).</param>
    /// <returns>The allow list.</returns>
    public static ProxyAllowList ForConfig(string[]? entries)
    {
        var last = _last;
        if (last is not null && ReferenceEquals(last.Value.Source, entries))
        {
            return last.Value.List;
        }

        var list = Parse(entries);
        _last = (entries, list);
        return list;
    }

    /// <summary>Parses the configured list.</summary>
    /// <param name="entries">CIDR blocks or single addresses; <c>null</c>/empty → default.</param>
    /// <returns>The allow list.</returns>
    public static ProxyAllowList Parse(IEnumerable<string>? entries)
    {
        var list = (entries ?? []).Select(e => e?.Trim()).Where(e => !string.IsNullOrEmpty(e)).Cast<string>().ToList();
        if (list.Count == 0)
        {
            return new ProxyAllowList(PrivateNetworks, []);
        }

        var networks = new List<IPNetwork>();
        var invalid = new List<string>();
        foreach (var e in list)
        {
            if (IPNetwork.TryParse(e, out var net))
            {
                networks.Add(net);
            }
            else if (IPAddress.TryParse(e, out var ip))
            {
                networks.Add(new IPNetwork(Normalize(ip), Normalize(ip).GetAddressBytes().Length * 8));
            }
            else
            {
                invalid.Add(e);
            }
        }

        // Only invalid entries: fail closed (nobody), never fall back to the broader default.
        return new ProxyAllowList(networks.ToArray(), invalid);
    }

    /// <summary>Checks an address.</summary>
    /// <param name="address">The caller's address.</param>
    /// <returns><c>true</c> if allowed.</returns>
    public bool Contains(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        var ip = Normalize(address);
        return _networks.Any(n => n.Contains(ip));
    }

    private static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}
