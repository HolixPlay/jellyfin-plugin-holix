using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using Jellyfin.Data.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Holix.Households;

/// <summary>
/// Propagates the household owner's policy (library access, remote access, rating limit, …) to the
/// profiles when it changes in the Jellyfin dashboard. Profile policy updates fire the same event, but
/// a profile is never an owner, so there is no loop.
/// </summary>
public sealed class OwnerPolicySync : IHostedService
{
    private readonly IUserManager _userManager;
    private readonly HouseholdService _service;
    private readonly ILogger<OwnerPolicySync> _logger;

    /// <summary>Initializes a new instance of the <see cref="OwnerPolicySync"/> class.</summary>
    /// <param name="userManager">User manager.</param>
    /// <param name="service">Household service.</param>
    /// <param name="logger">Logger.</param>
    public OwnerPolicySync(IUserManager userManager, HouseholdService service, ILogger<OwnerPolicySync> logger)
    {
        _userManager = userManager;
        _service = service;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userManager.OnUserUpdated += OnUserUpdated;

        // Sync all households once at startup so thresholds changed by a plugin update (e.g. 7+ → 7/1
        // in 0.3) are applied to existing profiles too.
        var plugin = Plugin.Instance;
        if (plugin is not null)
        {
            var owners = plugin.Households.All().Select(h => h.OwnerUserId).ToList();
            _ = Task.Run(async () =>
            {
                foreach (var owner in owners)
                {
                    await SyncQuietlyAsync(owner).ConfigureAwait(false);
                }
            },
            CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userManager.OnUserUpdated -= OnUserUpdated;
        return Task.CompletedTask;
    }

    private void OnUserUpdated(object? sender, GenericEventArgs<User> e)
    {
        var plugin = Plugin.Instance;
        var userId = e.Argument?.Id ?? Guid.Empty;
        if (plugin is null || userId == Guid.Empty || plugin.Households.Resolve(userId).Role != HouseholdRole.Owner)
        {
            return;
        }

        _ = Task.Run(() => SyncQuietlyAsync(userId));
    }

    private async Task SyncQuietlyAsync(Guid userId)
    {
        try
        {
            await _service.SyncPoliciesAsync(userId).ConfigureAwait(false);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Holix: failed to propagate owner {UserId} policy to profiles", userId);
        }
    }
}
