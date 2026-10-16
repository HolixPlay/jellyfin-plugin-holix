using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.Holix.Configuration;
using Jellyfin.Plugin.Holix.Households;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Holix;

/// <summary>
/// The Holix Play plugin: household/profile mapping, profile sessions, no history for guests, discovery.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>The plugin id (same as in manifest.json and build.yaml).</summary>
    public const string PluginGuid = "06f19487-387d-4e2f-be1d-71f44efff069";

    private readonly Lazy<HouseholdStore> _households;

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        ArgumentNullException.ThrowIfNull(applicationPaths);
        Instance = this;

        // DataFolderPath is the versioned plugin directory (plugins/Holix_0.1.0.0) and is deleted on update,
        // so the household mapping lives in the configurations directory, which survives updates.
        _households = new Lazy<HouseholdStore>(() => new HouseholdStore(
            Path.Combine(applicationPaths.PluginConfigurationsPath, "Jellyfin.Plugin.Holix", "households.json")));
    }

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "Holix";

    /// <inheritdoc />
    public override string Description => "Holix Play: household profiles as hidden Jellyfin users, age-rating parental limits, PIN-protected profile sessions, no history for guests.";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid);

    /// <summary>Gets the household store.</summary>
    public HouseholdStore Households => _households.Value;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "Holix Play",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
            },
        ];
    }
}
