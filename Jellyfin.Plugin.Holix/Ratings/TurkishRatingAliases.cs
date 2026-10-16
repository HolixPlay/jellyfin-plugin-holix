using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Holix.Ratings;

/// <summary>
/// Turkish rating labels that Jellyfin 12.2's <c>Localization/Ratings/tr.json</c> does not know. Jellyfin treats
/// an unknown <c>OfficialRating</c> as unrated, so e.g. a TV series rated "Genel İzleyici" (RTÜK's general
/// audience sign, also what TMDB returns for Turkish TV) is hidden from child profiles that block unrated
/// content. These aliases are added to Jellyfin's rating lookup by <see cref="RatingAliasInstaller"/>; item
/// metadata is never changed.
/// </summary>
/// <remarks>
/// Sources: RTÜK "Akıllı İşaretler Uygulama Esas ve İlkeleri" (in force since 9 January 2013) — age signs
/// <c>Genel İzleyici</c>, <c>7+</c>, <c>13+</c>, <c>18+</c>
/// (https://www.rtuk.gov.tr/Media/FM/Birimler/izleme/ai_rehber/1-_bolum_-_uygulama_esas_ve_ilkeleri.pdf);
/// content signs <c>Şiddet/Korku</c>, <c>Cinsellik</c>, <c>Olumsuz Örnek Oluşturabilecek Davranışlar</c>
/// (https://www.rtuk.gov.tr/haber-bultenleri-ve-haber-programlarina-akilli-isaret-kullanma-zorunlulugu-getirildi/4836).
/// Cinema labels (Genel İzleyici Kitlesi, 6A/6+, 10A/10+, 13A/13+, 16+, 18+) are already in Jellyfin's tr.json.
/// <para>
/// Scores follow tr.json: the "N yaş ve üzeri" spelling of a sign has the same score as its "N+" label
/// (<c>13+</c> = 13/1, <c>16+</c> = 16/0, <c>18+</c> = 18/0); RTÜK's <c>7+</c> has no tr.json counterpart and gets
/// 7/0 (same as US TV-Y7, and what Jellyfin's numeric parsing gives "7+"). A content sign on its own says the
/// programme contains harmful material but carries no age, so it fails closed as 18/0 (adult only).
/// Core entries always win: Jellyfin looks up its own rating systems before these aliases.
/// </para>
/// </remarks>
public static class TurkishRatingAliases
{
    /// <summary>The key under which the aliases are added to Jellyfin's rating systems.</summary>
    public const string SystemKey = "holix-tr";

    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    // Canonical spellings → score. Variants (case, Turkish/ASCII letters) are generated from these.
    private static readonly (string Label, int Score, int SubScore)[] Canonical =
    [
        // General audience (RTÜK TV sign, cinema label, short forms).
        ("Genel İzleyici", 0, 0),
        ("Genel İzleyici Kitlesi", 0, 0),
        ("Genel", 0, 0),
        ("Genel İzleyici Kitlesine Uygundur", 0, 0),
        ("Tüm Yaşlar", 0, 0),
        ("Her Yaş", 0, 0),

        // RTÜK age signs (TV / on-demand) in their "N+" and spelled-out forms.
        ("7+", 7, 0),
        ("7 Yaş ve Üzeri", 7, 0),
        ("7 Yaş ve Üstü", 7, 0),
        ("7 Yaş Üzeri", 7, 0),
        ("13+", 13, 1),
        ("13 Yaş ve Üzeri", 13, 1),
        ("13 Yaş ve Üstü", 13, 1),
        ("13 Yaş Üzeri", 13, 1),
        ("18+", 18, 0),
        ("18 Yaş ve Üzeri", 18, 0),
        ("18 Yaş ve Üstü", 18, 0),
        ("18 Yaş Üzeri", 18, 0),

        // Cinema labels spelled out (tr.json has 6+ / 10+ / 16+ only in short form).
        ("6 Yaş ve Üzeri", 6, 1),
        ("10 Yaş ve Üzeri", 10, 1),
        ("16 Yaş ve Üzeri", 16, 0),

        // RTÜK content signs without an age: fail closed (adult).
        ("Olumsuz Öğeler İçerir", 18, 0),
        ("Olumsuz Örnek Oluşturabilecek Davranışlar", 18, 0),
        ("Şiddet/Korku", 18, 0),
        ("Şiddet ve Korku", 18, 0),
        ("Cinsellik", 18, 0),
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, ParentalRatingScore>> LazyAll = new(Build);

    /// <summary>Gets every alias (canonical spellings plus case/ASCII variants), case-insensitive.</summary>
    public static IReadOnlyDictionary<string, ParentalRatingScore> All => LazyAll.Value;

    /// <summary>Gets the canonical labels (for documentation and tests).</summary>
    public static IReadOnlyList<string> CanonicalLabels { get; } = Canonical.Select(c => c.Label).ToArray();

    /// <summary>Resolves a Turkish label the way Jellyfin will once the aliases are installed.</summary>
    /// <param name="label">E.g. <c>"Genel İzleyici"</c>.</param>
    /// <param name="score">The score.</param>
    /// <returns><c>true</c> if the label is an alias.</returns>
    public static bool TryResolve(string? label, out ParentalRatingScore? score)
    {
        score = null;
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        return All.TryGetValue(label.Trim(), out score);
    }

    /// <summary>
    /// Spellings of a label that Jellyfin's case-insensitive (ordinal) lookup does not treat as equal:
    /// Turkish upper/lower case (İ/ı) and the ASCII transliteration.
    /// </summary>
    /// <param name="label">The canonical label.</param>
    /// <returns>Distinct variants, the label first.</returns>
    internal static IEnumerable<string> Variants(string label)
    {
        var forms = new List<string>
        {
            label,
            label.ToUpper(Turkish),
            label.ToLower(Turkish),
            Turkish.TextInfo.ToTitleCase(label.ToLower(Turkish)),
        };
        forms.AddRange(forms.Select(Ascii).ToList());
        return forms.Distinct(StringComparer.Ordinal);
    }

    private static string Ascii(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            sb.Append(ch switch
            {
                'İ' => 'I',
                'ı' => 'i',
                'Ş' => 'S',
                'ş' => 's',
                'Ğ' => 'G',
                'ğ' => 'g',
                'Ü' => 'U',
                'ü' => 'u',
                'Ö' => 'O',
                'ö' => 'o',
                'Ç' => 'C',
                'ç' => 'c',
                _ => ch,
            });
        }

        return sb.ToString();
    }

    private static Dictionary<string, ParentalRatingScore> Build()
    {
        // Same comparer as Jellyfin's rating dictionaries.
        var dict = new Dictionary<string, ParentalRatingScore>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, score, sub) in Canonical)
        {
            foreach (var v in Variants(label))
            {
                dict.TryAdd(v, new ParentalRatingScore(score, sub));
            }
        }

        return dict;
    }
}
