using System;
using System.Collections.Generic;
using System.Reflection;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Holix.Ratings;

/// <summary>
/// Adds extra rating labels to Jellyfin's rating lookup (<c>LocalizationManager.GetRatingScore</c>), which is what
/// both the parental filter (<c>BaseItem.IsParentalAllowed</c>) and the stored item score
/// (<c>InheritedParentalRatingValue</c>, used by library queries) are computed from.
/// </summary>
/// <remarks>
/// Jellyfin 12.2 loads its rating systems from embedded resources only and has no extension point, and the
/// <c>ILocalizationManager</c> registration cannot be decorated (the server casts it to its concrete type at
/// start-up). The plugin therefore adds one extra rating system (<see cref="TurkishRatingAliases.SystemKey"/>) to
/// the manager's private <c>_allParentalRatings</c> map. Jellyfin consults it only after the item's own country,
/// the US system and every built-in system failed, so built-in ratings are never overridden. The map is replaced
/// as a whole (copy + one atomic field write), never mutated, so concurrent readers are safe. If the field is
/// not found (a future Jellyfin version), nothing is changed and the caller logs a warning.
/// </remarks>
public static class RatingAliasInstaller
{
    /// <summary>The private field of Jellyfin's <c>LocalizationManager</c>.</summary>
    internal const string FieldName = "_allParentalRatings";

    /// <summary>Installs the aliases into the given localization manager.</summary>
    /// <param name="localizationManager">Jellyfin's <c>LocalizationManager</c> instance.</param>
    /// <param name="systemKey">The key of the added rating system.</param>
    /// <param name="aliases">Label → score.</param>
    /// <param name="error">Why nothing was installed.</param>
    /// <returns><c>true</c> if installed (or already present).</returns>
    public static bool TryInstall(
        object localizationManager,
        string systemKey,
        IReadOnlyDictionary<string, ParentalRatingScore> aliases,
        out string? error)
    {
        ArgumentNullException.ThrowIfNull(localizationManager);
        ArgumentException.ThrowIfNullOrEmpty(systemKey);
        ArgumentNullException.ThrowIfNull(aliases);

        var field = FindField(localizationManager.GetType());
        if (field is null)
        {
            error = $"{localizationManager.GetType().FullName} has no field {FieldName} of the expected type";
            return false;
        }

        if (field.GetValue(localizationManager) is not Dictionary<string, Dictionary<string, ParentalRatingScore?>> current)
        {
            error = $"{FieldName} is not loaded yet";
            return false;
        }

        if (current.Count == 0)
        {
            error = $"{FieldName} is empty (rating systems not loaded yet)";
            return false;
        }

        var system = new Dictionary<string, ParentalRatingScore?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, score) in aliases)
        {
            system[label] = score;
        }

        // Copy (keeping the comparer and insertion order, so the added system is consulted last) and swap.
        var replacement = new Dictionary<string, Dictionary<string, ParentalRatingScore?>>(current, current.Comparer)
        {
            [systemKey] = system,
        };
        field.SetValue(localizationManager, replacement);
        error = null;
        return true;
    }

    private static FieldInfo? FindField(Type type)
    {
        for (var t = type; t is not null; t = t.BaseType)
        {
            var f = t.GetField(FieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f is not null && f.FieldType == typeof(Dictionary<string, Dictionary<string, ParentalRatingScore?>>))
            {
                return f;
            }
        }

        return null;
    }
}
