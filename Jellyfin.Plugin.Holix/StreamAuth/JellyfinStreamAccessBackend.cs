using System;
using System.Net;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Jellyfin.Plugin.Holix.StreamAuth;

/// <summary>
/// <see cref="IStreamAccessBackend"/> on top of Jellyfin: tokens are validated by Jellyfin's own
/// <see cref="IAuthorizationContext"/> (sessions/devices and API keys), item access uses the same check as
/// <c>GET /Users/{userId}/Items/{itemId}</c> (<c>ILibraryManager.GetItemById(id, user)</c> →
/// <c>BaseItem.IsVisibleStandalone(user)</c>: library access, parental rating, blocked/allowed tags, unrated
/// blocking, parent folders).
/// </summary>
public sealed class JellyfinStreamAccessBackend : IStreamAccessBackend
{
    private readonly IAuthorizationContext _authContext;
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;

    /// <summary>Initializes a new instance of the <see cref="JellyfinStreamAccessBackend"/> class.</summary>
    /// <param name="authContext">The authorization context.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="libraryManager">The library manager.</param>
    public JellyfinStreamAccessBackend(IAuthorizationContext authContext, IUserManager userManager, ILibraryManager libraryManager)
    {
        _authContext = authContext;
        _userManager = userManager;
        _libraryManager = libraryManager;
    }

    /// <inheritdoc />
    public async Task<StreamPrincipal?> ResolveTokenAsync(string token)
    {
        // A synthetic request carrying only the token, so Jellyfin's exact validation applies. A Client value is
        // set so Jellyfin does not rewrite the device record (it only updates name/version when they are sent).
        var context = new DefaultHttpContext();
        context.Request.Headers[HeaderNames.Authorization] =
            $"MediaBrowser Client=\"Holix%20StreamAuth\", Token=\"{WebUtility.UrlEncode(token)}\"";
        var info = await _authContext.GetAuthorizationInfo(context.Request).ConfigureAwait(false);
        if (!info.IsAuthenticated)
        {
            return null;
        }

        if (info.IsApiKey)
        {
            return new StreamPrincipal(null, true);
        }

        return info.User is null ? null : new StreamPrincipal(info.User.Id, false);
    }

    /// <inheritdoc />
    public int CheckAccess(Guid userId, Guid itemId) =>
        Decide(_userManager.GetUserById(userId), _libraryManager.GetItemById(itemId));

    /// <summary>The access rule (separate for tests).</summary>
    /// <param name="user">The user (<c>null</c> if deleted).</param>
    /// <param name="item">The item (<c>null</c> if it does not exist).</param>
    /// <returns>The HTTP status.</returns>
    public static int Decide(User? user, BaseItem? item)
    {
        if (user is null)
        {
            return StatusCodes.Status401Unauthorized;
        }

        if (user.HasPermission(PermissionKind.IsDisabled))
        {
            return StatusCodes.Status403Forbidden;
        }

        if (item is null)
        {
            return StatusCodes.Status404NotFound;
        }

        // Same as LibraryManager.ItemIsVisible (used by GetItemById(id, user)).
        return item is UserRootFolder || item.IsVisibleStandalone(user)
            ? StatusCodes.Status204NoContent
            : StatusCodes.Status403Forbidden;
    }
}
