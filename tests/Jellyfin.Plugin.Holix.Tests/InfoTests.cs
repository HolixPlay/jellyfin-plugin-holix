using System;
using System.IO;
using System.Text.Json;
using System.Xml;
using System.Xml.Serialization;
using Jellyfin.Plugin.Holix.Api;
using Jellyfin.Plugin.Holix.Configuration;

namespace Jellyfin.Plugin.Holix.Tests;

public class InfoTests
{
    [Fact]
    public void Info_has_no_clips_url()
    {
        var info = HolixController.BuildInfo(new PluginConfiguration(), new Version(0, 5, 1, 0));
        Assert.DoesNotContain("clips-url", info.Features);
        Assert.Contains("household-session-limit", info.Features);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(info));
        Assert.False(doc.RootElement.TryGetProperty("ClipsUrl", out _));
        Assert.Equal("https://api.holixplay.com", doc.RootElement.GetProperty("CloudUrl").GetString());
        Assert.Equal("0.5.1.0", doc.RootElement.GetProperty("Version").GetString());
    }

    [Fact]
    public void Old_config_with_clips_url_still_loads()
    {
        // A 0.5.0 settings file (plugins/configurations/Jellyfin.Plugin.Holix.xml) still has <ClipsUrl>.
        // Jellyfin reads it with XmlSerializer, which must skip the unknown element and keep the rest.
        const string Xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <CloudUrl>https://cloud.example.com</CloudUrl>
              <ClipsUrl>https://jf.example.com/holix-clips</ClipsUrl>
              <RequireCloudProof>false</RequireCloudProof>
              <RequireCloudLink>true</RequireCloudLink>
              <GuestBehavior>KeepPlaystate</GuestBehavior>
              <DefaultRatingSystem>DE</DefaultRatingSystem>
              <StreamAuthAllowedProxies>
                <string>10.0.0.0/8</string>
              </StreamAuthAllowedProxies>
              <MaxProfilesPerHousehold>6</MaxProfilesPerHousehold>
            </PluginConfiguration>
            """;

        var unknown = 0;
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        serializer.UnknownElement += (_, e) =>
        {
            Assert.Equal("ClipsUrl", e.Element.Name);
            unknown++;
        };

        using var reader = XmlReader.Create(new StringReader(Xml));
        var config = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.Equal(1, unknown);
        Assert.Equal("https://cloud.example.com", config.CloudUrl);
        Assert.False(config.RequireCloudProof);
        Assert.True(config.RequireCloudLink);
        Assert.Equal(GuestBehavior.KeepPlaystate, config.GuestBehavior);
        Assert.Equal("DE", config.DefaultRatingSystem);
        Assert.Equal(["10.0.0.0/8"], config.StreamAuthAllowedProxies);
        Assert.Equal(6, config.MaxProfilesPerHousehold);

        // Saving again drops the element.
        using var writer = new StringWriter();
        serializer.Serialize(writer, config);
        Assert.DoesNotContain("ClipsUrl", writer.ToString(), StringComparison.Ordinal);
    }
}
