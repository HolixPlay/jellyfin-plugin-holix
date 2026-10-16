using System;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.Holix.StreamAuth;

/// <summary>
/// Records the TCP peer address of <c>/Holix/StreamAuth</c> requests before Jellyfin's forwarded-headers
/// middleware replaces it with the client address from <c>X-Forwarded-For</c> (when the proxy is in Jellyfin's
/// known proxies). The allow list must be checked against the proxy that is actually connected, not the
/// end-user address the proxy forwards.
/// </summary>
public sealed class StreamAuthPeerStartupFilter : IStartupFilter
{
    /// <summary>The <see cref="HttpContext.Items"/> key holding the peer <see cref="IPAddress"/>.</summary>
    public const string PeerItemKey = "Holix.StreamAuth.Peer";

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Path.Value?.EndsWith("/Holix/StreamAuth", StringComparison.OrdinalIgnoreCase) == true)
                {
                    context.Items[PeerItemKey] = context.Connection.RemoteIpAddress;
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }

    /// <summary>The address of the directly connected peer.</summary>
    /// <param name="context">The context.</param>
    /// <returns>The address recorded before forwarded-headers processing, else the connection's address.</returns>
    public static IPAddress? Peer(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(PeerItemKey, out var v) && v is IPAddress ip
            ? ip
            : context.Connection.RemoteIpAddress;
    }
}
