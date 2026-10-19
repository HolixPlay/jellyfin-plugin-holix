using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Holix.Configuration;

namespace Jellyfin.Plugin.Holix.Households;

/// <summary>
/// Who may create a household (<see cref="PluginConfiguration.HouseholdOwners"/>). Only creating a new household is
/// checked; existing households keep working whatever the mode is. Pure functions (unit tested).
/// </summary>
public static class OwnerAccess
{
    /// <summary>
    /// Moves the 0.5.1 settings (<c>AllowNonAdminOwners</c> + <c>AllowedOwnerUserIds</c>) to the owner mode:
    /// on → <see cref="HouseholdOwnerMode.Everyone"/>; off with users listed → <see cref="HouseholdOwnerMode.SelectedUsers"/>
    /// (the same users stay selected); off with no users → <see cref="HouseholdOwnerMode.AdminsOnly"/> (same behaviour as
    /// before). A settings file without the legacy value (new install, or already migrated) is left alone.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <returns><c>true</c> if something changed and the settings should be saved.</returns>
    public static bool Migrate(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (config.AllowNonAdminOwners is not { } legacy)
        {
            return false;
        }

        config.HouseholdOwners = LegacyMode(legacy, config.AllowedOwnerUserIds);
        config.AllowedOwnerUserIds = SelectedUserIds(config).Select(g => g.ToString("N")).ToArray();
        config.AllowNonAdminOwners = null;
        return true;
    }

    /// <summary>The mode in effect (also correct for a settings file that has not been migrated yet).</summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The mode.</returns>
    public static HouseholdOwnerMode EffectiveMode(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.AllowNonAdminOwners is { } legacy
            ? LegacyMode(legacy, config.AllowedOwnerUserIds)
            : config.HouseholdOwners;
    }

    /// <summary>The selected users (valid ids only, in either <c>N</c> or <c>D</c> format, without duplicates).</summary>
    /// <param name="config">The configuration.</param>
    /// <returns>The user ids.</returns>
    public static IReadOnlyList<Guid> SelectedUserIds(PluginConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return (config.AllowedOwnerUserIds ?? [])
            .Select(x => Guid.TryParse(x?.Trim(), out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .Distinct()
            .ToList();
    }

    /// <summary>Whether the user may create a household. Administrators always may.</summary>
    /// <param name="config">The configuration.</param>
    /// <param name="userId">The user.</param>
    /// <param name="isAdministrator">Whether the user is a Jellyfin administrator.</param>
    /// <returns><c>true</c> if allowed.</returns>
    public static bool CanCreateHousehold(PluginConfiguration config, Guid userId, bool isAdministrator)
    {
        if (isAdministrator)
        {
            return true;
        }

        return EffectiveMode(config) switch
        {
            HouseholdOwnerMode.Everyone => true,
            HouseholdOwnerMode.SelectedUsers => SelectedUserIds(config).Contains(userId),
            _ => false,
        };
    }

    private static HouseholdOwnerMode LegacyMode(bool allowNonAdmins, string[]? ids)
    {
        if (allowNonAdmins)
        {
            return HouseholdOwnerMode.Everyone;
        }

        return (ids ?? []).Any(x => Guid.TryParse(x?.Trim(), out _))
            ? HouseholdOwnerMode.SelectedUsers
            : HouseholdOwnerMode.AdminsOnly;
    }
}
