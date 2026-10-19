using System;
using System.IO;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.Holix.Configuration;
using Jellyfin.Plugin.Holix.Households;

namespace Jellyfin.Plugin.Holix.Tests;

public class OwnerAccessTests
{
    private static readonly Guid Alice = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
    private static readonly Guid Bob = Guid.NewGuid();

    private static PluginConfiguration Load(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml));
        return Assert.IsType<PluginConfiguration>(new XmlSerializer(typeof(PluginConfiguration)).Deserialize(reader));
    }

    private static string Save(PluginConfiguration config)
    {
        using var writer = new StringWriter();
        new XmlSerializer(typeof(PluginConfiguration)).Serialize(writer, config);
        return writer.ToString();
    }

    private static string Legacy(bool allow, params string[] ids) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <AllowNonAdminOwners>{(allow ? "true" : "false")}</AllowNonAdminOwners>
          <AllowedOwnerUserIds>{string.Concat(Array.ConvertAll(ids, i => $"<string>{i}</string>"))}</AllowedOwnerUserIds>
          <MaxProfilesPerHousehold>6</MaxProfilesPerHousehold>
        </PluginConfiguration>
        """;

    [Fact]
    public void New_install_defaults_to_everyone()
    {
        var config = new PluginConfiguration();
        Assert.Equal(HouseholdOwnerMode.Everyone, config.HouseholdOwners);
        Assert.False(OwnerAccess.Migrate(config));
        Assert.DoesNotContain("AllowNonAdminOwners", Save(config), StringComparison.Ordinal);
        Assert.True(OwnerAccess.CanCreateHousehold(config, Bob, isAdministrator: false));
    }

    [Theory]
    [InlineData(true, new string[0], HouseholdOwnerMode.Everyone)]
    [InlineData(true, new[] { "6f9619ff8b86d011b42d00c04fc964ff" }, HouseholdOwnerMode.Everyone)]
    [InlineData(false, new[] { "6F9619FF-8B86-D011-B42D-00C04FC964FF" }, HouseholdOwnerMode.SelectedUsers)]
    [InlineData(false, new[] { "not-an-id" }, HouseholdOwnerMode.AdminsOnly)]
    [InlineData(false, new string[0], HouseholdOwnerMode.AdminsOnly)]
    public void Settings_from_0_5_1_are_migrated(bool allow, string[] ids, HouseholdOwnerMode expected)
    {
        var config = Load(Legacy(allow, ids));

        // Before the start-up migration the old settings are already honoured.
        Assert.Equal(expected, OwnerAccess.EffectiveMode(config));

        Assert.True(OwnerAccess.Migrate(config));
        Assert.Equal(expected, config.HouseholdOwners);
        Assert.Null(config.AllowNonAdminOwners);
        Assert.Equal(6, config.MaxProfilesPerHousehold);
        if (ids.Length == 1 && Guid.TryParse(ids[0], out _))
        {
            Assert.Equal(["6f9619ff8b86d011b42d00c04fc964ff"], config.AllowedOwnerUserIds);
        }

        // Saved without the old element, read back unchanged, and migrating again is a no-op.
        var saved = Save(config);
        Assert.DoesNotContain("AllowNonAdminOwners", saved, StringComparison.Ordinal);
        var reloaded = Load(saved);
        Assert.Equal(expected, reloaded.HouseholdOwners);
        Assert.False(OwnerAccess.Migrate(reloaded));
    }

    [Fact]
    public void Can_create_household_per_mode()
    {
        var config = new PluginConfiguration { AllowedOwnerUserIds = ["6F9619FF-8B86-D011-B42D-00C04FC964FF", "junk", " "] };

        config.HouseholdOwners = HouseholdOwnerMode.Everyone;
        Assert.True(OwnerAccess.CanCreateHousehold(config, Bob, false));

        config.HouseholdOwners = HouseholdOwnerMode.AdminsOnly;
        Assert.False(OwnerAccess.CanCreateHousehold(config, Alice, false));
        Assert.True(OwnerAccess.CanCreateHousehold(config, Bob, true));

        config.HouseholdOwners = HouseholdOwnerMode.SelectedUsers;
        Assert.True(OwnerAccess.CanCreateHousehold(config, Alice, false));
        Assert.False(OwnerAccess.CanCreateHousehold(config, Bob, false));
        Assert.True(OwnerAccess.CanCreateHousehold(config, Bob, true));
        Assert.Equal([Alice], OwnerAccess.SelectedUserIds(config));
    }

    [Fact]
    public void Mode_is_written_as_a_name()
    {
        var config = new PluginConfiguration { HouseholdOwners = HouseholdOwnerMode.SelectedUsers };
        Assert.Contains("<HouseholdOwners>SelectedUsers</HouseholdOwners>", Save(config), StringComparison.Ordinal);
    }
}
