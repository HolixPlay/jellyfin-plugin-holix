using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Holix.Households;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Holix.Guest;

/// <summary>
/// Keeps no playstate for guest profiles. Subscribes to <see cref="IUserDataManager.UserDataSaved"/>; when the
/// saved data belongs to a guest user and contains playstate, it scrubs and rewrites the data within the
/// same request (the event is raised synchronously).
/// </summary>
/// <remarks>
/// Why this approach: the ONLY choke point that writes playstate in Jellyfin is
/// <c>IUserDataManager.SaveUserData</c>; <c>/Sessions/Playing*</c>, <c>/UserPlayedItems</c>,
/// <c>/UserItems/{id}/UserData</c>, WebSocket, SyncPlay and imports all end up there and all raise
/// <c>UserDataSaved</c>. An endpoint filter would miss some paths, and blocking
/// <c>/Sessions/Playing/Stopped</c> would break transcode cleanup and "now playing" info. The rewrite
/// raises the event again (<c>UpdateUserData</c>), but the data is clean by then, so there is no loop.
/// </remarks>
public sealed class GuestPlaystateGuard : IHostedService
{
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<GuestPlaystateGuard> _logger;

    /// <summary>Initializes a new instance of the <see cref="GuestPlaystateGuard"/> class.</summary>
    /// <param name="userDataManager">The user data manager.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="logger">The logger.</param>
    public GuestPlaystateGuard(IUserDataManager userDataManager, IUserManager userManager, ILogger<GuestPlaystateGuard> logger)
    {
        _userDataManager = userDataManager;
        _userManager = userManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userDataManager.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        try
        {
            var plugin = Plugin.Instance;
            if (plugin is null || e.UserData is null || e.Item is null)
            {
                return;
            }

            var behavior = plugin.Configuration.GuestBehavior;
            if (!PlaystateScrubber.NeedsScrub(e.UserData, behavior) || !plugin.Households.IsGuest(e.UserId))
            {
                return;
            }

            var user = _userManager.GetUserById(e.UserId);
            if (user is null)
            {
                return;
            }

            PlaystateScrubber.Scrub(e.UserData, behavior);
            _userDataManager.SaveUserData(user, e.Item, e.UserData, UserDataSaveReason.UpdateUserData, CancellationToken.None);
            _logger.LogDebug("Holix: discarded playstate of {Item} for guest {UserId} ({Reason})", e.UserId, e.Item.Id, e.SaveReason);
        }
#pragma warning disable CA1031 // The event handler must not fail the request.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Holix: failed to scrub guest playstate");
        }
    }
}
