using System;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Holix.Households;
using Jellyfin.Plugin.Holix.Ratings;
using MediaBrowser.Model.Users;

namespace Jellyfin.Plugin.Holix.Tests;

public class ProfilePolicyBuilderTests
{
    private static readonly Guid FolderA = Guid.NewGuid();

    private static UserPolicy Owner() => new()
    {
        IsAdministrator = true,
        IsHidden = false,
        EnableAllFolders = false,
        EnabledFolders = [FolderA],
        EnableAllChannels = false,
        EnabledChannels = [Guid.NewGuid()],
        EnableRemoteAccess = true,
        RemoteClientBitrateLimit = 8_000_000,
        EnableContentDeletion = true,
        EnableCollectionManagement = true,
        EnableContentDownloading = true,
        BlockedTags = ["owner-blocked"],
        MaxParentalRating = null,
    };

    private static UserPolicy Fresh() => new()
    {
        AuthenticationProviderId = "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider",
        PasswordResetProviderId = "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider",
    };

    private static HouseholdProfile Profile(ProfileKind kind, AgeRating rating, RatingSystem? system = null, params string[] tags) => new()
    {
        UserId = Guid.NewGuid(),
        Name = "Kids",
        Kind = kind,
        AgeRating = rating,
        RatingSystem = system,
        BlockedTags = tags,
    };

    [Fact]
    public void Library_access_is_copied_from_owner_permissions_are_not()
    {
        var owner = Owner();
        var p = ProfilePolicyBuilder.Build(owner, Fresh(), Profile(ProfileKind.Adult, AgeRating.Age18), RatingSystem.TR, [], true);

        Assert.False(p.IsAdministrator);
        Assert.True(p.IsHidden);
        Assert.False(p.EnableAllFolders);
        Assert.Equal(owner.EnabledFolders, p.EnabledFolders);
        Assert.Equal(owner.EnabledChannels, p.EnabledChannels);
        Assert.True(p.EnableRemoteAccess);
        Assert.Equal(8_000_000, p.RemoteClientBitrateLimit);
        Assert.False(p.EnableContentDeletion);
        Assert.False(p.EnableCollectionManagement);
        Assert.Null(p.MaxParentalRating);
        Assert.Equal(Fresh().AuthenticationProviderId, p.AuthenticationProviderId);
        Assert.Equal(Fresh().PasswordResetProviderId, p.PasswordResetProviderId);
    }

    [Fact]
    public void Child_rating_tags_and_unrated_block()
    {
        var p = ProfilePolicyBuilder.Build(Owner(), Fresh(), Profile(ProfileKind.Child, AgeRating.Age7, null, "horror"), RatingSystem.TR, ["violence"], true);

        Assert.Equal(7, p.MaxParentalRating);
        Assert.Equal(1, p.MaxParentalSubRating);
        Assert.Contains("horror", p.BlockedTags);
        Assert.Contains("violence", p.BlockedTags);
        Assert.Contains("owner-blocked", p.BlockedTags);
        Assert.Contains(UnratedItem.Movie, p.BlockUnratedItems);
        Assert.Contains(UnratedItem.Series, p.BlockUnratedItems);
        Assert.DoesNotContain(UnratedItem.Music, p.BlockUnratedItems);
        Assert.False(p.EnableContentDownloading);
    }

    [Fact]
    public void Child_unrated_block_can_be_disabled()
    {
        var p = ProfilePolicyBuilder.Build(Owner(), Fresh(), Profile(ProfileKind.Child, AgeRating.Age13), RatingSystem.US, [], false);
        Assert.Empty(p.BlockUnratedItems);
        Assert.Equal(13, p.MaxParentalRating);
        Assert.Equal(1, p.MaxParentalSubRating);
    }

    [Fact]
    public void Profile_system_overrides_household()
    {
        // The threshold is system independent (13/1); the profile system determines the label.
        var p = ProfilePolicyBuilder.Build(Owner(), Fresh(), Profile(ProfileKind.Adult, AgeRating.Age13, RatingSystem.DE), RatingSystem.TR, [], true);
        Assert.Equal(13, p.MaxParentalRating);
        Assert.Equal(1, p.MaxParentalSubRating);
        Assert.Equal("FSK 12", AgeRatingMapper.Map(AgeRating.Age13, RatingSystem.DE).Label);
        Assert.Equal("13+", AgeRatingMapper.Map(AgeRating.Age13, RatingSystem.TR).Label);
    }

    [Fact]
    public void Owner_stricter_limit_cannot_be_exceeded()
    {
        var owner = Owner();
        owner.MaxParentalRating = 13;
        owner.MaxParentalSubRating = 0;
        var p = ProfilePolicyBuilder.Build(owner, Fresh(), Profile(ProfileKind.Adult, AgeRating.Age16), RatingSystem.TR, [], true);
        Assert.Equal(13, p.MaxParentalRating);
        Assert.Equal(0, p.MaxParentalSubRating);

        var adult = ProfilePolicyBuilder.Build(owner, Fresh(), Profile(ProfileKind.Adult, AgeRating.Age18), RatingSystem.TR, [], true);
        Assert.Equal(13, adult.MaxParentalRating);
    }

    [Fact]
    public void Guest_restrictions()
    {
        var p = ProfilePolicyBuilder.Build(Owner(), Fresh(), Profile(ProfileKind.Guest, AgeRating.Age18), RatingSystem.TR, ["violence"], true);
        Assert.False(p.EnableContentDownloading);
        Assert.False(p.EnableUserPreferenceAccess);
        Assert.DoesNotContain("violence", p.BlockedTags); // child tags are not applied to guests
        Assert.Empty(p.BlockUnratedItems);
    }

    [Theory]
    [InlineData(null, null, 13, 0, 13, 0)]
    [InlineData(13, 1, null, null, 13, 1)]
    [InlineData(13, 1, 13, 0, 13, 0)]
    [InlineData(13, null, 13, 0, 13, 0)]
    [InlineData(6, 1, 13, 0, 6, 1)]
    [InlineData(16, 0, 12, null, 12, null)]
    public void Stricter_is_chosen(int? aS, int? aSub, int? bS, int? bSub, int? eS, int? eSub)
    {
        var (s, sub) = ProfilePolicyBuilder.Tighter((aS, aSub), (bS, bSub));
        Assert.Equal(eS, s);
        Assert.Equal(eSub, sub);
    }
}
