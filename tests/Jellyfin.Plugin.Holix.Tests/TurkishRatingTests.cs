using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Holix.Ratings;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Holix.Tests;

public class TurkishRatingTests
{
    [Theory]
    [InlineData("Genel İzleyici", 0, 0)]
    [InlineData("Genel İzleyici Kitlesi", 0, 0)]
    [InlineData("Genel izleyici", 0, 0)]
    [InlineData("GENEL İZLEYİCİ", 0, 0)]
    [InlineData("genel izleyici", 0, 0)]
    [InlineData("GENEL", 0, 0)]
    [InlineData("Genel", 0, 0)]
    [InlineData(" Genel İzleyici ", 0, 0)]
    [InlineData("7+", 7, 0)]
    [InlineData("7 yaş ve üzeri", 7, 0)]
    [InlineData("7 YAŞ VE ÜZERİ", 7, 0)]
    [InlineData("7 yas ve uzeri", 7, 0)]
    [InlineData("13+", 13, 1)]
    [InlineData("13 yaş ve üzeri", 13, 1)]
    [InlineData("18+", 18, 0)]
    [InlineData("18 yaş ve üzeri", 18, 0)]
    [InlineData("16 yaş ve üzeri", 16, 0)]
    [InlineData("Olumsuz Öğeler İçerir", 18, 0)]
    [InlineData("Olumsuz Örnek Oluşturabilecek Davranışlar", 18, 0)]
    [InlineData("Şiddet/Korku", 18, 0)]
    [InlineData("Cinsellik", 18, 0)]
    public void Labels_resolve(string label, int score, int sub)
    {
        Assert.True(TurkishRatingAliases.TryResolve(label, out var s));
        Assert.Equal(score, s!.Score);
        Assert.Equal(sub, s.SubScore);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("PG-13")]
    [InlineData("Genel Kültür")]
    [InlineData("Korku")]
    public void Unknown_labels_do_not_resolve(string? label)
    {
        Assert.False(TurkishRatingAliases.TryResolve(label, out _));
    }

    [Theory]
    [InlineData("Genel İzleyici", AgeRating.All)]
    [InlineData("GENEL", AgeRating.All)]
    [InlineData("7 yaş ve üzeri", AgeRating.Age7)]
    [InlineData("13 yaş ve üzeri", AgeRating.Age13)]
    [InlineData("18 yaş ve üzeri", AgeRating.Age18)]
    [InlineData("Olumsuz Öğeler İçerir", AgeRating.Age18)]
    public void Lowest_band_that_sees_the_label(string label, AgeRating minimumBand)
    {
        Assert.True(TurkishRatingAliases.TryResolve(label, out var s));
        foreach (var band in new[] { AgeRating.All, AgeRating.Age7, AgeRating.Age13, AgeRating.Age16, AgeRating.Age18 })
        {
            var limit = AgeRatingMapper.Map(band, RatingSystem.TR);
            var allowed = limit.Score is null
                || (s!.Score != limit.Score ? s.Score < limit.Score : (s.SubScore ?? 0) <= limit.SubScore);
            Assert.True(allowed == (band >= minimumBand), $"{label} / {band.ToWire()}");
        }
    }

    [Fact]
    public void Variants_cover_turkish_casing_and_ascii()
    {
        var v = new List<string>(TurkishRatingAliases.Variants("Genel İzleyici"));
        Assert.Contains("Genel İzleyici", v);
        Assert.Contains("GENEL İZLEYİCİ", v);
        Assert.Contains("genel izleyici", v);
        Assert.Contains("Genel Izleyici", v);
        Assert.Contains("GENEL IZLEYICI", v);
    }

    [Fact]
    public void Installer_adds_a_system_consulted_after_the_built_in_ones()
    {
        var lm = new FakeLocalizationManager();
        var before = lm.Systems;
        Assert.Null(lm.GetRatingScore("Genel İzleyici", "tr"));

        Assert.True(RatingAliasInstaller.TryInstall(lm, TurkishRatingAliases.SystemKey, TurkishRatingAliases.All, out var error), error);

        // The map was replaced, not mutated (readers holding the old one are unaffected).
        Assert.NotSame(before, lm.Systems);
        Assert.Equal(2, before.Count);
        Assert.Equal(3, lm.Systems.Count);

        Assert.Equal(0, lm.GetRatingScore("Genel İzleyici", "tr")!.Score);
        Assert.Equal(0, lm.GetRatingScore("genel izleyici", "us")!.Score);
        Assert.Equal(13, lm.GetRatingScore("13 yaş ve üzeri", "tr")!.Score);

        // Built-in entries win: tr.json "13+" is 13/1, "Genel İzleyici Kitlesi" stays 0/0, US "R" untouched.
        Assert.Equal(1, lm.GetRatingScore("13+", "tr")!.SubScore);
        Assert.Equal(17, lm.GetRatingScore("R", "tr")!.Score);
        Assert.Null(lm.GetRatingScore("Something else", "tr"));

        // Idempotent.
        Assert.True(RatingAliasInstaller.TryInstall(lm, TurkishRatingAliases.SystemKey, TurkishRatingAliases.All, out _));
        Assert.Equal(3, lm.Systems.Count);
    }

    [Fact]
    public void Installer_does_nothing_when_jellyfin_changed()
    {
        Assert.False(RatingAliasInstaller.TryInstall(new object(), TurkishRatingAliases.SystemKey, TurkishRatingAliases.All, out var error));
        Assert.NotNull(error);
        Assert.False(RatingAliasInstaller.TryInstall(new FakeLocalizationManager(empty: true), TurkishRatingAliases.SystemKey, TurkishRatingAliases.All, out _));
    }

    /// <summary>
    /// Same field and lookup order as Jellyfin 12.2's <c>LocalizationManager</c> (country system, then US, then all
    /// systems in insertion order).
    /// </summary>
    private sealed class FakeLocalizationManager
    {
#pragma warning disable IDE0044, CA1859 // Mirrors Jellyfin's private readonly field; set by reflection.
        private readonly Dictionary<string, Dictionary<string, ParentalRatingScore?>> _allParentalRatings = new(StringComparer.OrdinalIgnoreCase);
#pragma warning restore IDE0044, CA1859

        public FakeLocalizationManager(bool empty = false)
        {
            if (empty)
            {
                return;
            }

            _allParentalRatings["us"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["G"] = new(0, 0),
                ["R"] = new(17, 0),
            };
            _allParentalRatings["tr"] = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Genel İzleyici Kitlesi"] = new(0, 0),
                ["13+"] = new(13, 1),
                ["18+"] = new(18, 0),
            };
        }

        public Dictionary<string, Dictionary<string, ParentalRatingScore?>> Systems => _allParentalRatings;

        public ParentalRatingScore? GetRatingScore(string rating, string country)
        {
            if (_allParentalRatings.TryGetValue(country, out var c) && c.TryGetValue(rating, out var v))
            {
                return v;
            }

            if (_allParentalRatings.TryGetValue("us", out var us) && us.TryGetValue(rating, out var u))
            {
                return u;
            }

            foreach (var d in _allParentalRatings.Values)
            {
                if (d.TryGetValue(rating, out var x))
                {
                    return x;
                }
            }

            return null;
        }
    }
}
