using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Holix.Configuration;

/// <summary>What happens to playstate on guest profiles.</summary>
public enum GuestBehavior
{
    /// <summary>Playback position, played flag, play count and last played date are not kept (default).</summary>
    DiscardPlaystate = 0,

    /// <summary>In addition to the above, favorites, likes and ratings are not kept either.</summary>
    DiscardAllUserData = 1,

    /// <summary>Guests are recorded like regular users (protection off).</summary>
    KeepPlaystate = 2,
}

/// <summary>
/// Plugin settings (Dashboard → Plugins → Holix). Household/profile data is NOT stored here but in
/// <c>households.json</c> in the plugin configuration directory, so saving the settings page never
/// overwrites household data.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the holix-cloud base URL. The default is the hosted Holix Play cloud
    /// (<c>https://api.holixplay.com</c>); admins may point it at another holix-cloud deployment.
    /// It is announced to clients in <c>/Holix/Info</c>. When linking a household the plugin only contacts
    /// this URL (link code + JWKS); the URL is never taken from a request, only set by an admin.
    /// </summary>
    public string CloudUrl { get; set; } = "https://api.holixplay.com";

    /// <summary>
    /// Gets or sets a value indicating whether a cloud-linked household needs a holix-cloud proof
    /// (<c>JellyfinProof</c>) to open a profile session. On by default.
    /// </summary>
    public bool RequireCloudProof { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether households that are not linked to holix-cloud may not open
    /// profile sessions at all. Off by default (transition period: unlinked households keep the old behaviour plus a warning).
    /// </summary>
    public bool RequireCloudLink { get; set; }

    /// <summary>Gets or sets the guest profile behaviour.</summary>
    public GuestBehavior GuestBehavior { get; set; } = GuestBehavior.DiscardPlaystate;

    /// <summary>Gets or sets the default rating system for new households (<c>TR</c>, <c>US</c>, <c>DE</c>).</summary>
    public string DefaultRatingSystem { get; set; } = "TR";

    /// <summary>
    /// Gets or sets a value indicating whether non-administrator Jellyfin users may create a household.
    /// When off (default) only administrators or users in <see cref="AllowedOwnerUserIds"/> can own a
    /// household, because a household owner can create new users on the server.
    /// </summary>
    public bool AllowNonAdminOwners { get; set; }

    /// <summary>Gets or sets the explicitly allowed owner user ids (for non-administrators).</summary>
#pragma warning disable CA1819 // XML serialization requires arrays.
    public string[] AllowedOwnerUserIds { get; set; } = [];

    /// <summary>Gets or sets the tags blocked by default for child profiles.</summary>
    public string[] DefaultChildBlockedTags { get; set; } = [];

    /// <summary>
    /// Gets or sets the addresses (CIDR, or single IPs) of the reverse proxies allowed to call
    /// <c>GET /Holix/StreamAuth</c> (forwardAuth / <c>auth_request</c>). Empty (default): loopback and private
    /// networks (10/8, 172.16/12, 192.168/16, fc00::/7). Other callers get 404.
    /// </summary>
    public string[] StreamAuthAllowedProxies { get; set; } = [];
#pragma warning restore CA1819

    /// <summary>Gets or sets the maximum profiles per household (including the owner); same as holix-cloud (10).</summary>
    public int MaxProfilesPerHousehold { get; set; } = 10;

    /// <summary>Gets or sets a value indicating whether child profiles hide unrated items.</summary>
    public bool BlockUnratedForChildren { get; set; } = true;
}
