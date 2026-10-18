using System;
using System.Text.Json;
using Jellyfin.Plugin.Holix.Api;
using Jellyfin.Plugin.Holix.Configuration;

namespace Jellyfin.Plugin.Holix.Tests;

public class InfoTests
{
    [Theory]
    [InlineData("https://jf.example.com/holix-clips", "https://jf.example.com/holix-clips")]
    [InlineData("  https://jf.example.com/holix-clips/  ", "https://jf.example.com/holix-clips")]
    [InlineData("http://192.168.1.10:8085", "http://192.168.1.10:8085")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("/holix-clips", null)]
    [InlineData("ftp://jf.example.com/clips", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("https://jf.example.com/clips?x=1", null)]
    [InlineData("https://user:pw@jf.example.com/clips", null)]
    public void Clips_url_is_normalized(string? input, string? expected)
    {
        Assert.Equal(expected, HolixController.NormalizeClipsUrl(input));
    }

    [Fact]
    public void Info_omits_clips_url_when_not_set()
    {
        var info = HolixController.BuildInfo(new PluginConfiguration(), new Version(0, 5, 0, 0));
        Assert.Null(info.ClipsUrl);
        Assert.Contains("clips-url", info.Features);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(info));
        Assert.False(doc.RootElement.TryGetProperty("ClipsUrl", out _));
        Assert.Equal("https://api.holixplay.com", doc.RootElement.GetProperty("CloudUrl").GetString());
        Assert.Equal("0.5.0.0", doc.RootElement.GetProperty("Version").GetString());
    }

    [Fact]
    public void Info_announces_configured_clips_url()
    {
        var config = new PluginConfiguration { ClipsUrl = "https://demo.example.com/holix-clips/" };
        var info = HolixController.BuildInfo(config, null);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(info));
        Assert.Equal("https://demo.example.com/holix-clips", doc.RootElement.GetProperty("ClipsUrl").GetString());
    }
}
