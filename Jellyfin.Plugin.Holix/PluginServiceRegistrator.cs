using Jellyfin.Plugin.Holix.Cloud;
using Jellyfin.Plugin.Holix.Guest;
using Jellyfin.Plugin.Holix.Households;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Holix;

/// <summary>Registers the plugin services in Jellyfin's DI container.</summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ProofReplayCache>();
        serviceCollection.AddSingleton<CloudClient>();
        serviceCollection.AddSingleton<HouseholdService>();
        serviceCollection.AddHostedService<GuestPlaystateGuard>();
        serviceCollection.AddHostedService<OwnerPolicySync>();
    }
}
