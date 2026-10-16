using Jellyfin.Plugin.Holix.Ratings;

namespace Jellyfin.Plugin.Holix.Tests;

public class AgeRatingMapperTests
{
    [Theory]
    [InlineData(AgeRating.All, 0, 0, "Genel İzleyici Kitlesi")]
    [InlineData(AgeRating.Age7, 7, 1, "6+")]
    [InlineData(AgeRating.Age13, 13, 1, "13+")]
    [InlineData(AgeRating.Age16, 16, 0, "16+")]
    public void Turkey(AgeRating rating, int score, int sub, string label)
    {
        var l = AgeRatingMapper.Map(rating, RatingSystem.TR);
        Assert.Equal(score, l.Score);
        Assert.Equal(sub, l.SubScore);
        Assert.Equal(label, l.Label);
    }

    [Theory]
    [InlineData(AgeRating.All, 0, 0, "G")]
    [InlineData(AgeRating.Age7, 7, 1, "TV-Y7-FV")]
    [InlineData(AgeRating.Age13, 13, 1, "PG-13")]
    [InlineData(AgeRating.Age16, 16, 0, "TV-14")]
    public void United_states(AgeRating rating, int score, int sub, string label)
    {
        var l = AgeRatingMapper.Map(rating, RatingSystem.US);
        Assert.Equal(score, l.Score);
        Assert.Equal(sub, l.SubScore);
        Assert.Equal(label, l.Label);
    }

    [Theory]
    [InlineData(AgeRating.All, 0, 0, "FSK 0")]
    [InlineData(AgeRating.Age7, 7, 1, "FSK 6")]
    [InlineData(AgeRating.Age13, 13, 1, "FSK 12")]
    [InlineData(AgeRating.Age16, 16, 0, "FSK 16")]
    public void Germany(AgeRating rating, int score, int sub, string label)
    {
        var l = AgeRatingMapper.Map(rating, RatingSystem.DE);
        Assert.Equal(score, l.Score);
        Assert.Equal(sub, l.SubScore);
        Assert.Equal(label, l.Label);
    }

    [Theory]
    [InlineData(AgeRating.All)]
    [InlineData(AgeRating.Age7)]
    [InlineData(AgeRating.Age13)]
    [InlineData(AgeRating.Age16)]
    public void Threshold_is_system_independent_and_sub_score_never_null(AgeRating rating)
    {
        var tr = AgeRatingMapper.Map(rating, RatingSystem.TR);
        foreach (var system in new[] { RatingSystem.US, RatingSystem.DE })
        {
            var l = AgeRatingMapper.Map(rating, system);
            Assert.Equal(tr.Score, l.Score);
            Assert.Equal(tr.SubScore, l.SubScore);
        }

        Assert.NotNull(tr.SubScore);
        Assert.Equal((int)rating, tr.Score);
    }

    [Theory]
    [InlineData(RatingSystem.TR)]
    [InlineData(RatingSystem.US)]
    [InlineData(RatingSystem.DE)]
    public void Adult_is_unrestricted(RatingSystem system)
    {
        var l = AgeRatingMapper.Map(AgeRating.Age18, system);
        Assert.True(l.IsUnrestricted);
        Assert.Null(l.SubScore);
    }

    [Theory]
    [InlineData(RatingSystem.TR)]
    [InlineData(RatingSystem.US)]
    [InlineData(RatingSystem.DE)]
    public void Thresholds_increase_with_age(RatingSystem system)
    {
        var previous = -1;
        foreach (var r in new[] { AgeRating.All, AgeRating.Age7, AgeRating.Age13, AgeRating.Age16 })
        {
            var score = AgeRatingMapper.Map(r, system).Score!.Value;
            Assert.True(score > previous, $"{system}/{r}");
            Assert.True(score <= (int)r, $"{system}/{r}: threshold must not exceed the age");
            previous = score;
        }
    }

    // Exact copy of Jellyfin's comparison (BaseItem.IsParentalAllowed): verifies the mapping gives the
    // expected result when applied to real ratings. Scores come from Jellyfin 12.2.0 Ratings/*.json.
    private static bool Allowed(RatingLimit limit, int itemScore, int? itemSub)
    {
        if (limit.Score is null)
        {
            return true;
        }

        if (itemScore != limit.Score)
        {
            return itemScore < limit.Score;
        }

        return limit.SubScore is null || (itemSub ?? 0) <= limit.SubScore;
    }

    // Real ratings (Jellyfin 12.2.0 tr/us/de.json scores) and the lowest Holix band that can see them.
    // Expectation: in every system an item is visible when the profile band ≥ the required band. E.g.
    // US TV-Y7 (7/0) is visible to a 7-year-old under TR, US and DE; TR 10A (10/0) is not.
    public static TheoryData<string, int, int?, AgeRating> RealRatings => new()
    {
        { "TR General Audience", 0, 0, AgeRating.All },
        { "TR 6A", 6, 0, AgeRating.Age7 },
        { "TR 6+", 6, 1, AgeRating.Age7 },
        { "TR 10A", 10, 0, AgeRating.Age13 },
        { "TR 10+", 10, 1, AgeRating.Age13 },
        { "TR 13A", 13, 0, AgeRating.Age13 },
        { "TR 13+", 13, 1, AgeRating.Age13 },
        { "TR 16+", 16, 0, AgeRating.Age16 },
        { "TR 18+", 18, 0, AgeRating.Age18 },
        { "US G", 0, 0, AgeRating.All },
        { "US TV-Y", 0, 0, AgeRating.All },
        { "US TV-G", 0, 0, AgeRating.All },
        { "US TV-Y7", 7, 0, AgeRating.Age7 },
        { "US TV-Y7-FV", 7, 1, AgeRating.Age7 },
        { "US PG", 10, 0, AgeRating.Age13 },
        { "US TV-PG-V", 10, 1, AgeRating.Age13 },
        { "US PG-13", 13, 0, AgeRating.Age13 },
        { "US TV-14", 14, 0, AgeRating.Age16 },
        { "US TV-14-V", 14, 1, AgeRating.Age16 },
        { "US R", 17, 0, AgeRating.Age18 },
        { "US NC-17", 17, 1, AgeRating.Age18 },
        { "US TV-MA", 17, 1, AgeRating.Age18 },
        { "US TV-X", 18, 0, AgeRating.Age18 },
        { "DE FSK 0", 0, null, AgeRating.All },
        { "DE FSK 6", 6, null, AgeRating.Age7 },
        { "DE FSK 12", 12, null, AgeRating.Age13 },
        { "DE FSK 16", 16, null, AgeRating.Age16 },
        { "DE FSK 18", 18, null, AgeRating.Age18 },
    };

    [Theory]
    [MemberData(nameof(RealRatings))]
    public void All_bands_all_systems_with_real_ratings(string name, int itemScore, int? itemSub, AgeRating minimumBand)
    {
        foreach (var system in new[] { RatingSystem.TR, RatingSystem.US, RatingSystem.DE })
        {
            foreach (var band in new[] { AgeRating.All, AgeRating.Age7, AgeRating.Age13, AgeRating.Age16, AgeRating.Age18 })
            {
                var expected = band >= minimumBand;
                Assert.True(
                    expected == Allowed(AgeRatingMapper.Map(band, system), itemScore, itemSub),
                    $"{name} ({itemScore}/{itemSub}) {system}/{band.ToWire()}: expected {(expected ? "visible" : "hidden")}");
            }
        }
    }

    [Theory]
    [InlineData(RatingSystem.TR)]
    [InlineData(RatingSystem.US)]
    [InlineData(RatingSystem.DE)]
    public void Us_tv_y7_is_visible_to_seven_year_old(RatingSystem system)
    {
        var limit = AgeRatingMapper.Map(AgeRating.Age7, system);
        Assert.True(Allowed(limit, 7, 0));  // TV-Y7
        Assert.True(Allowed(limit, 7, 1));  // TV-Y7-FV
        Assert.False(Allowed(limit, 10, 0)); // PG / TR 10A
        Assert.False(Allowed(AgeRatingMapper.Map(AgeRating.All, system), 7, 0));
    }

    [Theory]
    [InlineData("all", AgeRating.All)]
    [InlineData("7+", AgeRating.Age7)]
    [InlineData(" 13+ ", AgeRating.Age13)]
    [InlineData("16+", AgeRating.Age16)]
    [InlineData("18+", AgeRating.Age18)]
    [InlineData("ALL", AgeRating.All)]
    public void Wire_format_is_parsed(string wire, AgeRating expected)
    {
        Assert.True(AgeRatings.TryParse(wire, out var r));
        Assert.Equal(expected, r);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12+")]
    [InlineData("13")]
    [InlineData("PG-13")]
    public void Invalid_wire_is_rejected(string? wire)
    {
        Assert.False(AgeRatings.TryParse(wire, out _));
    }

    [Fact]
    public void Wire_round_trip()
    {
        foreach (var w in AgeRatings.WireValues)
        {
            Assert.True(AgeRatings.TryParse(w, out var r));
            Assert.Equal(w, r.Value.ToWire());
        }
    }

    [Theory]
    [InlineData("tr", RatingSystem.TR)]
    [InlineData("US", RatingSystem.US)]
    [InlineData(" de ", RatingSystem.DE)]
    public void System_is_parsed(string value, RatingSystem expected)
    {
        Assert.True(RatingSystems.TryParse(value, out var s));
        Assert.Equal(expected, s);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("FR")]
    [InlineData("")]
    public void Invalid_system(string value)
    {
        Assert.False(RatingSystems.TryParse(value, out _));
    }
}
