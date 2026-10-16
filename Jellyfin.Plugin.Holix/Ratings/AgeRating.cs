using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.Holix.Ratings;

/// <summary>
/// Holix age rating. Same set as holix-cloud's <c>max_age_rating</c>
/// (<c>all</c>, <c>7+</c>, <c>13+</c>, <c>16+</c>, <c>18+</c>). The birth date never reaches the server;
/// the client sends this value as received from the cloud.
/// </summary>
public enum AgeRating
{
    /// <summary>General audience (all ages).</summary>
    All = 0,

    /// <summary>Ages 7 and up.</summary>
    Age7 = 7,

    /// <summary>Ages 13 and up.</summary>
    Age13 = 13,

    /// <summary>Ages 16 and up.</summary>
    Age16 = 16,

    /// <summary>Adult: no limit.</summary>
    Age18 = 18,
}

/// <summary>
/// Conversions between <see cref="AgeRating"/> and the wire format (<c>"all"</c>, <c>"7+"</c> …).
/// </summary>
public static class AgeRatings
{
    private static readonly Dictionary<string, AgeRating> Parse = new(StringComparer.OrdinalIgnoreCase)
    {
        ["all"] = AgeRating.All,
        ["0+"] = AgeRating.All,
        ["7+"] = AgeRating.Age7,
        ["13+"] = AgeRating.Age13,
        ["16+"] = AgeRating.Age16,
        ["18+"] = AgeRating.Age18,
    };

    /// <summary>Gets the wire values in ascending order.</summary>
    public static IReadOnlyList<string> WireValues { get; } = ["all", "7+", "13+", "16+", "18+"];

    /// <summary>Parses a wire value.</summary>
    /// <param name="value">E.g. <c>"13+"</c>.</param>
    /// <param name="rating">The result.</param>
    /// <returns><c>true</c> if valid.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out AgeRating? rating)
    {
        if (value is not null && Parse.TryGetValue(value.Trim(), out var r))
        {
            rating = r;
            return true;
        }

        rating = null;
        return false;
    }

    /// <summary>Converts to the wire format.</summary>
    /// <param name="rating">The rating.</param>
    /// <returns>E.g. <c>"13+"</c>.</returns>
    public static string ToWire(this AgeRating rating) => rating switch
    {
        AgeRating.All => "all",
        AgeRating.Age7 => "7+",
        AgeRating.Age13 => "13+",
        AgeRating.Age16 => "16+",
        AgeRating.Age18 => "18+",
        _ => throw new ArgumentOutOfRangeException(nameof(rating)),
    };
}
