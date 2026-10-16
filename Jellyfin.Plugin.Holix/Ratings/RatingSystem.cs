using System;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.Holix.Ratings;

/// <summary>
/// Which country's rating system the age rating is translated into.
/// </summary>
public enum RatingSystem
{
    /// <summary>Turkey (RTÜK smart signs: Genel İzleyici, 6+, 10+, 13+, 16+, 18+).</summary>
    TR = 0,

    /// <summary>United States (MPA + TV Parental Guidelines).</summary>
    US = 1,

    /// <summary>Germany (FSK).</summary>
    DE = 2,
}

/// <summary>Helpers.</summary>
public static class RatingSystems
{
    /// <summary>Parses a country code (case-insensitive).</summary>
    /// <param name="value">E.g. <c>"tr"</c>.</param>
    /// <param name="system">The result.</param>
    /// <returns><c>true</c> if valid.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out RatingSystem? system)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Enum.TryParse<RatingSystem>(value.Trim(), ignoreCase: true, out var s)
            && Enum.IsDefined(s)
            && !int.TryParse(value, out _))
        {
            system = s;
            return true;
        }

        system = null;
        return false;
    }
}
