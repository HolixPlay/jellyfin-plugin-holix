using System;

namespace Jellyfin.Plugin.Holix.Ratings;

/// <summary>
/// The upper limit written to the Jellyfin policy. <see cref="Score"/> = <c>UserPolicy.MaxParentalRating</c>,
/// <see cref="SubScore"/> = <c>UserPolicy.MaxParentalSubRating</c>; both <c>null</c> means no limit.
/// </summary>
/// <param name="Score">The highest allowed rating score (in Jellyfin, score ≈ age).</param>
/// <param name="SubScore">The sub-score limit at that score (e.g. TR "6A"=0, "6+"=1).</param>
/// <param name="Label">The name of the highest allowed rating in the selected system (for the UI).</param>
public sealed record RatingLimit(int? Score, int? SubScore, string Label)
{
    /// <summary>Gets a value indicating whether the limit is unrestricted.</summary>
    public bool IsUnrestricted => Score is null;
}

/// <summary>
/// Holix age rating → Jellyfin <c>MaxParentalRating</c>/<c>MaxParentalSubRating</c>.
/// </summary>
/// <remarks>
/// Scores are the <c>ratingScore</c> values from Jellyfin 12.2.0's
/// <c>Emby.Server.Implementations/Localization/Ratings/{tr,us,de}.json</c>. Jellyfin filters an item like this
/// (<c>BaseItem.IsParentalAllowed</c>): allowed if the item score &lt; the limit; if equal, allowed if the
/// item's sub-score ≤ the limit's sub-score. An item's score comes from its <b>own</b> metadata country
/// (a US series still carries TV-Y7 = 7/0 on a TR server).
/// <para>
/// Rule (0.3): the threshold is the band's age and covers every sub-score defined at that age — since
/// score ≈ age in Jellyfin, "an N-year-old sees N-rated content". This way the threshold is not rounded
/// down to the selected country's step (TR 6+, FSK 6, FSK 12, TV-14) and does not cut off another
/// country's rating at the same age (US TV-Y7 7/0, PG-13 13/0; TR 13+ 13/1; FSK 16). Thresholds are the
/// same in all three systems: <c>all</c> 0/0 · <c>7+</c> 7/1 · <c>13+</c> 13/1 · <c>16+</c> 16/0 ·
/// <c>18+</c> unrestricted. The system only determines the label (the name of the highest allowed rating
/// in that country). The sub-score is never left <c>null</c>, so we do not depend on how Jellyfin
/// interprets a <c>null</c> sub-limit.
/// </para>
/// </remarks>
public static class AgeRatingMapper
{
    // Thresholds (score/sub-score): the band's age + the highest sub-score at that age (tr/us/de.json):
    // 0: G/TV-Y/TV-G/Genel/FSK 0 → 0/0 · 7: TV-Y7 7/0, TV-Y7-FV 7/1 → 7/1
    // 13: PG-13 13/0, TR 13A 13/0, TR 13+ 13/1 → 13/1 · 16: TR 16+ 16/0, FSK 16 → 16/0
    private static (int Score, int Sub) Threshold(AgeRating rating) => rating switch
    {
        AgeRating.All => (0, 0),
        AgeRating.Age7 => (7, 1),
        AgeRating.Age13 => (13, 1),
        AgeRating.Age16 => (16, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(rating)),
    };

    /// <summary>Computes the mapping.</summary>
    /// <param name="rating">The Holix age rating.</param>
    /// <param name="system">The country system (label only).</param>
    /// <returns>The policy limit.</returns>
    public static RatingLimit Map(AgeRating rating, RatingSystem system)
    {
        if (rating == AgeRating.Age18)
        {
            return new RatingLimit(null, null, Unrestricted(system));
        }

        var (score, sub) = Threshold(rating);
        return new RatingLimit(score, sub, Label(rating, system));
    }

    // Name of the highest rating below the threshold in the selected system.
    // TR: Genel 0/0 · 6A 6/0 · 6+ 6/1 · 10A 10/0 · 10+ 10/1 · 13A 13/0 · 13+ 13/1 · 16+ 16/0 · 18+ 18/0
    // US: G/TV-Y/TV-G 0/0 · TV-Y7 7/0 · TV-Y7-FV 7/1 · PG 10/0 · TV-PG-* 10/1 · PG-13 13/0
    //     · TV-14 14/0 · TV-14-* 14/1 · R 17/0 · NC-17/TV-MA 17/1 · TV-X 18/0
    // DE (no sub-scores): FSK 0 · 6 · 12 · 16 · 18
    private static string Label(AgeRating rating, RatingSystem system) => (system, rating) switch
    {
        (RatingSystem.TR, AgeRating.All) => "Genel İzleyici Kitlesi",
        (RatingSystem.TR, AgeRating.Age7) => "6+",
        (RatingSystem.TR, AgeRating.Age13) => "13+",
        (RatingSystem.TR, AgeRating.Age16) => "16+",
        (RatingSystem.US, AgeRating.All) => "G",
        (RatingSystem.US, AgeRating.Age7) => "TV-Y7-FV",
        (RatingSystem.US, AgeRating.Age13) => "PG-13",
        (RatingSystem.US, AgeRating.Age16) => "TV-14",
        (RatingSystem.DE, AgeRating.All) => "FSK 0",
        (RatingSystem.DE, AgeRating.Age7) => "FSK 6",
        (RatingSystem.DE, AgeRating.Age13) => "FSK 12",
        (RatingSystem.DE, AgeRating.Age16) => "FSK 16",
        _ => throw new ArgumentOutOfRangeException(nameof(rating), $"{system}/{rating}"),
    };

    private static string Unrestricted(RatingSystem system) => system switch
    {
        RatingSystem.TR => "18+",
        RatingSystem.US => "Unrestricted",
        RatingSystem.DE => "FSK 18",
        _ => "18+",
    };
}
