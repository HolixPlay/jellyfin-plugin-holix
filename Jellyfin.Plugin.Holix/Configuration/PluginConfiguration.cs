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

/// <summary>Who may create a household (become a household owner).</summary>
public enum HouseholdOwnerMode
{
    /// <summary>Every Jellyfin user (default). Profiles never get more rights than their owner, and the owner's
    /// session limit covers the whole household.</summary>
    Everyone = 0,

    /// <summary>Only administrators.</summary>
    AdminsOnly = 1,

    /// <summary>Administrators and the users chosen on the settings page (<see cref="PluginConfiguration.AllowedOwnerUserIds"/>).</summary>
    SelectedUsers = 2,
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
    /// Gets or sets who may create a household (Dashboard → Plugins → Holix → "Who can create a household").
    /// Default <see cref="HouseholdOwnerMode.Everyone"/>. Changing it never affects existing households.
    /// </summary>
    public HouseholdOwnerMode HouseholdOwners { get; set; } = HouseholdOwnerMode.Everyone;

    /// <summary>
    /// Gets or sets a value indicating whether non-administrators may create a household. Up to 0.5.1 only; kept so
    /// an old settings file can be migrated to <see cref="HouseholdOwners"/> (see <see cref="Households.OwnerAccess.Migrate"/>),
    /// <c>null</c> afterwards and then no longer written.
    /// </summary>
    public bool? AllowNonAdminOwners { get; set; }

    /// <summary>
    /// Gets or sets the users allowed to create a household in <see cref="HouseholdOwnerMode.SelectedUsers"/> mode
    /// (Jellyfin user ids; the settings page manages them as a list of names).
    /// </summary>
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

    /// <summary>XmlSerializer: the legacy <see cref="AllowNonAdminOwners"/> element is written only until it is migrated.</summary>
    /// <returns>Whether to write it.</returns>
    public bool ShouldSerializeAllowNonAdminOwners() => AllowNonAdminOwners.HasValue;
}
