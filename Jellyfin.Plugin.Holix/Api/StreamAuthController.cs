using System.Threading.Tasks;
using Jellyfin.Plugin.Holix.StreamAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Holix.Api;

/// <summary>
/// <c>GET /Holix/StreamAuth</c>: forwardAuth (Traefik) / <c>auth_request</c> (nginx) endpoint that closes
/// Jellyfin's unauthenticated stream URLs at the reverse proxy. The proxy sends the client's headers plus
/// <c>X-Forwarded-Uri</c> / <c>X-Forwarded-Method</c>; the answer is 204 (allow), 401 (no or invalid token),
/// 403 (user disabled or item not visible to the user) or 404 (no such item). Paths that are not stream paths
/// get 204. Only callable from <c>StreamAuthAllowedProxies</c> (default: loopback and private networks); any
/// other caller gets 404.
/// </summary>
[ApiController]
[Route("Holix/StreamAuth")]
public sealed class StreamAuthController : ControllerBase
{
    private readonly StreamAuthorizer _authorizer;
    private readonly ILogger<StreamAuthController> _logger;

    /// <summary>Initializes a new instance of the <see cref="StreamAuthController"/> class.</summary>
    /// <param name="authorizer">The authorizer.</param>
    /// <param name="logger">The logger.</param>
    public StreamAuthController(StreamAuthorizer authorizer, ILogger<StreamAuthController> logger)
    {
        _authorizer = authorizer;
        _logger = logger;
    }

    /// <summary>Authorizes a media stream request on behalf of a reverse proxy.</summary>
    /// <returns>204, 401, 403 or 404 with an empty body.</returns>
    [AcceptVerbs("GET", "HEAD")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Authorize()
    {
        var peer = StreamAuthPeerStartupFilter.Peer(HttpContext);
        var allowList = ProxyAllowList.ForConfig(Plugin.Instance?.Configuration.StreamAuthAllowedProxies);
        if (!allowList.Contains(peer))
        {
            _logger.LogDebug("Holix StreamAuth: caller {Peer} is not an allowed proxy", peer);
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        var status = await _authorizer.AuthorizeAsync(StreamRequest.From(Request)).ConfigureAwait(false);
        return StatusCode(status);
    }
}
