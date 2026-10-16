using System;
using System.IO;
using Jellyfin.Plugin.Holix.Households;
using Jellyfin.Plugin.Holix.Ratings;

namespace Jellyfin.Plugin.Holix.Tests;

public sealed class HouseholdMappingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "holix-test-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "households.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static Household NewHousehold(Guid owner) => new()
    {
        OwnerUserId = owner,
        RatingSystem = RatingSystem.TR,
        CreatedUtc = DateTime.UtcNow,
    };

    [Fact]
    public void Owner_and_profile_resolve_and_persist()
    {
        var owner = Guid.NewGuid();
        var child = Guid.NewGuid();
        var guest = Guid.NewGuid();
        var store = new HouseholdStore(FilePath);
        store.Update(owner, h =>
        {
            h = NewHousehold(owner);
            h.Profiles.Add(new HouseholdProfile { UserId = child, Name = "Kids", Kind = ProfileKind.Child, AgeRating = AgeRating.Age7, HolixProfileId = "cloud-1" });
            h.Profiles.Add(new HouseholdProfile { UserId = guest, Name = "Guest", Kind = ProfileKind.Guest });
            return h;
        });

        // A new instance reads from the file.
        var reloaded = new HouseholdStore(FilePath);
        Assert.Equal(HouseholdRole.Owner, reloaded.Resolve(owner).Role);
        var m = reloaded.Resolve(child);
        Assert.Equal(HouseholdRole.Profile, m.Role);
        Assert.Equal(owner, m.Household!.OwnerUserId);
        Assert.Equal("cloud-1", m.Profile!.HolixProfileId);
        Assert.Equal(AgeRating.Age7, m.Profile.AgeRating);
        Assert.Equal(HouseholdRole.None, reloaded.Resolve(Guid.NewGuid()).Role);

        Assert.True(reloaded.IsGuest(guest));
        Assert.False(reloaded.IsGuest(child));
        Assert.False(reloaded.IsGuest(owner));
    }

    [Fact]
    public void Json_enums_are_written_as_strings()
    {
        var owner = Guid.NewGuid();
        var store = new HouseholdStore(FilePath);
        store.Update(owner, _ =>
        {
            var h = NewHousehold(owner);
            h.Profiles.Add(new HouseholdProfile { UserId = Guid.NewGuid(), Name = "A", Kind = ProfileKind.Child, AgeRating = AgeRating.Age13, RatingSystem = RatingSystem.US });
            return h;
        });
        var json = File.ReadAllText(FilePath);
        Assert.Contains("\"Child\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Age13\"", json, StringComparison.Ordinal);
        Assert.Contains("\"US\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("BirthDate", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void User_cannot_belong_to_two_households()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var shared = Guid.NewGuid();
        var store = new HouseholdStore(FilePath);
        store.Update(a, _ =>
        {
            var h = NewHousehold(a);
            h.Profiles.Add(new HouseholdProfile { UserId = shared, Name = "X" });
            return h;
        });

        Assert.Throws<InvalidOperationException>(() => store.Update(b, _ =>
        {
            var h = NewHousehold(b);
            h.Profiles.Add(new HouseholdProfile { UserId = shared, Name = "X" });
            return h;
        }));

        // The owner of another household cannot become a profile.
        Assert.Throws<InvalidOperationException>(() => store.Update(b, _ =>
        {
            var h = NewHousehold(b);
            h.Profiles.Add(new HouseholdProfile { UserId = a, Name = "A" });
            return h;
        }));

        // The owner cannot be a profile in its own household.
        Assert.Throws<InvalidOperationException>(() => store.Update(b, _ =>
        {
            var h = NewHousehold(b);
            h.Profiles.Add(new HouseholdProfile { UserId = b, Name = "B" });
            return h;
        }));

        // Failed updates must not have written anything.
        Assert.Equal(HouseholdRole.None, new HouseholdStore(FilePath).Resolve(b).Role);
    }

    [Fact]
    public void External_mutation_does_not_change_store()
    {
        var owner = Guid.NewGuid();
        var store = new HouseholdStore(FilePath);
        store.Update(owner, _ => NewHousehold(owner));
        var copy = store.Resolve(owner).Household!;
        copy.Profiles.Add(new HouseholdProfile { UserId = Guid.NewGuid(), Name = "Ghost" });
        Assert.Empty(store.Resolve(owner).Household!.Profiles);
    }

    [Fact]
    public void Deleted_users_are_pruned()
    {
        var owner = Guid.NewGuid();
        var gone = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var otherOwner = Guid.NewGuid();
        var store = new HouseholdStore(FilePath);
        store.Update(owner, _ =>
        {
            var h = NewHousehold(owner);
            h.Profiles.Add(new HouseholdProfile { UserId = gone, Name = "A" });
            h.Profiles.Add(new HouseholdProfile { UserId = kept, Name = "B" });
            return h;
        });
        store.Update(otherOwner, _ => NewHousehold(otherOwner));

        var removed = store.Prune(id => id != gone && id != otherOwner);
        Assert.Equal(2, removed);
        Assert.Equal(HouseholdRole.None, store.Resolve(gone).Role);
        Assert.Equal(HouseholdRole.Profile, store.Resolve(kept).Role);
        Assert.Equal(HouseholdRole.None, store.Resolve(otherOwner).Role);
    }

    [Fact]
    public void Household_is_deleted()
    {
        var owner = Guid.NewGuid();
        var store = new HouseholdStore(FilePath);
        store.Update(owner, _ => NewHousehold(owner));
        store.Update(owner, _ => null);
        Assert.Empty(new HouseholdStore(FilePath).All());
    }

    [Fact]
    public void User_name_has_owner_prefix_and_is_unique()
    {
        var taken = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase) { "alex.Kids" };
        Assert.Equal("alex.Guest", ProfileNaming.BuildUserName("alex", "Guest", taken.Contains));
        Assert.Equal("alex.Kids-2", ProfileNaming.BuildUserName("alex", "Kids", taken.Contains));
        Assert.Equal("alex.Zoë_Ann", ProfileNaming.BuildUserName("alex", "Zoë Ann", _ => false));
        Assert.Equal("alex.profile", ProfileNaming.BuildUserName("alex", "🙂", _ => false));
        Assert.True(ProfileNaming.IsValidJellyfinUserName(ProfileNaming.BuildUserName("Alex Smith", "Zoë <3", _ => false)));
    }

    [Theory]
    [InlineData("  Kids  ", "Kids")]
    [InlineData("Zoë   Ann", "Zoë Ann")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("a\u0001b", null)]
    [InlineData("123456789012345678901234567890123", null)]
    public void Display_name_is_validated(string input, string? expected)
    {
        Assert.Equal(expected, ProfileNaming.NormalizeDisplayName(input));
    }

    [Fact]
    public void Profile_device_id_is_derived()
    {
        var p1 = Guid.NewGuid();
        var p2 = Guid.NewGuid();
        var a = HouseholdService.DeriveDeviceId("appletv-1", p1);
        Assert.Equal(a, HouseholdService.DeriveDeviceId("appletv-1", p1));
        Assert.NotEqual(a, HouseholdService.DeriveDeviceId("appletv-1", p2));
        Assert.NotEqual(a, HouseholdService.DeriveDeviceId("iphone-1", p1));
        Assert.StartsWith("holix-", a, StringComparison.Ordinal);
    }

    [Fact]
    public void Cloud_link_persists_and_returns_copy()
    {
        var owner = Guid.NewGuid();
        var store = new HouseholdStore(FilePath);
        store.Update(owner, _ =>
        {
            var h = NewHousehold(owner);
            h.Cloud = new Cloud.CloudLink { CloudUrl = "https://cloud.test", HouseholdId = "household-1", Issuer = "holix-cloud", LinkedUtc = DateTime.UtcNow };
            h.Cloud.Keys.Add(new Cloud.CloudKey { Kid = "k1", X = "x" });
            return h;
        });

        var reloaded = new HouseholdStore(FilePath).Resolve(owner).Household!;
        Assert.Equal("household-1", reloaded.Cloud!.HouseholdId);
        Assert.Equal("k1", reloaded.Cloud.Keys[0].Kid);
        reloaded.Cloud.Keys.Clear();
        Assert.Single(new HouseholdStore(FilePath).Resolve(owner).Household!.Cloud!.Keys);
    }

    [Fact]
    public void Existing_mapping_is_locked_in_linked_household()
    {
        var owner = Guid.NewGuid();
        var child = Guid.NewGuid();
        var h = NewHousehold(owner);
        h.OwnerHolixProfileId = "cloud-owner";
        h.Profiles.Add(new HouseholdProfile { UserId = child, Kind = ProfileKind.Child, HolixProfileId = "cloud-child" });

        // Can be changed in an unlinked household (transitional).
        HouseholdService.CheckMappingChange(h, child, "cloud-child", "cloud-new", requireProof: true);

        h.Cloud = new Cloud.CloudLink { HouseholdId = "household-1" };
        var ex = Assert.Throws<HolixException>(() => HouseholdService.CheckMappingChange(h, child, "cloud-child", "cloud-new", requireProof: true));
        Assert.Equal("mapping_locked", ex.Code);
        Assert.Equal("mapping_locked", Assert.Throws<HolixException>(() => HouseholdService.CheckMappingChange(h, child, "cloud-child", null, true)).Code);
        Assert.Equal("mapping_locked", Assert.Throws<HolixException>(() => HouseholdService.CheckMappingChange(h, owner, "cloud-owner", "cloud-child", true)).Code);
        // Same value and first-time fill of an empty mapping are allowed; no lock when proof is not required.
        HouseholdService.CheckMappingChange(h, child, "cloud-child", "cloud-child", true);
        HouseholdService.CheckMappingChange(h, Guid.NewGuid(), null, "cloud-new", true);
        HouseholdService.CheckMappingChange(h, child, "cloud-child", "cloud-new", requireProof: false);
    }

    [Fact]
    public void Cloud_profile_maps_to_one_profile_per_household()
    {
        var owner = Guid.NewGuid();
        var child = Guid.NewGuid();
        var h = NewHousehold(owner);
        h.OwnerHolixProfileId = "cloud-owner";
        h.Profiles.Add(new HouseholdProfile { UserId = child, HolixProfileId = "cloud-child" });

        Assert.Equal("mapping_in_use", Assert.Throws<HolixException>(() => HouseholdService.CheckMappingChange(h, Guid.Empty, null, "cloud-child", true)).Code);
        Assert.Equal("mapping_in_use", Assert.Throws<HolixException>(() => HouseholdService.CheckMappingChange(h, Guid.Empty, null, "cloud-owner", true)).Code);
        Assert.Equal("mapping_in_use", Assert.Throws<HolixException>(() => HouseholdService.CheckMappingChange(h, owner, null, "cloud-child", true)).Code);
    }
}
