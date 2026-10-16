using System;

namespace Jellyfin.Plugin.Holix.Tests;

public class PluginTests
{
    [Fact]
    public void Config_page_is_embedded_resource()
    {
        var asm = typeof(Plugin).Assembly;
        var name = typeof(Plugin).Namespace + ".Configuration.configPage.html";
        Assert.Contains(name, asm.GetManifestResourceNames());
    }

    [Fact]
    public void Guid_matches_build_yaml()
    {
        Assert.Equal(Guid.Parse("06f19487-387d-4e2f-be1d-71f44efff069"), Guid.Parse(Plugin.PluginGuid));
        Assert.Contains(typeof(Plugin).Assembly.GetTypes(), t => t.Name == "HolixController");
    }
}
