using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Holix.Configuration;
using Jellyfin.Plugin.Holix.Households;
using Jellyfin.Plugin.Holix.Images;
using Jellyfin.Plugin.Holix.Ratings;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Jellyfin.Plugin.Holix.Api;

/// <summary>
/// Holix Play endpoints. Everything except <c>/Holix/Info</c> requires a Jellyfin user token
/// (<c>Authorization: MediaBrowser Token="…"</c> or <c>X-Emby-Token</c>); API keys are not accepted
/// because every operation is tied to a household owner.
/// </summary>
[ApiController]
[Route("Holix")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class HolixController : ControllerBase
{
    private static readonly string[] FeatureList =
    [
        "household",
        "profiles",
        "profile-session",
        "parental-rating:TR,US,DE",
        "guest-no-history",
        "owner-policy-sync",
        "cloud-proof",
        "profile-image",
        "stream-auth",
        "tr-rating-aliases",
        "clips-url",
    ];

    private readonly HouseholdService _service;
    private readonly IAuthorizationContext _authContext;

    /// <summary>Initializes a new instance of the <see cref="HolixController"/> class.</summary>
    /// <param name="service">The household service.</param>
    /// <param name="authContext">The authorization context.</param>
    public HolixController(HouseholdService service, IAuthorizationContext authContext)
    {
        _service = service;
        _authContext = authContext;
    }

    /// <summary>Plugin discovery: clients probe for the plugin and fall back to single-profile mode on 404.</summary>
    /// <returns>Version and features.</returns>
    [HttpGet("Info")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
#pragma warning disable CA1822 // MVC actions must be instance methods.
    public ActionResult<InfoDto> GetInfo()
#pragma warning restore CA1822
    {
        var plugin = Plugin.Instance;
        return BuildInfo(plugin?.Configuration, plugin?.Version);
    }

    /// <summary>Builds the <c>/Holix/Info</c> body from the plugin settings.</summary>
    /// <param name="config">The plugin configuration (null before the plugin is loaded).</param>
    /// <param name="version">The plugin version.</param>
    /// <returns>The info body.</returns>
    internal static InfoDto BuildInfo(PluginConfiguration? config, Version? version)
    {
        return new InfoDto
        {
            Version = version?.ToString() ?? "0.0.0.0",
            Features = FeatureList,
            CloudUrl = string.IsNullOrWhiteSpace(config?.CloudUrl) ? null : config.CloudUrl.Trim(),
            ClipsUrl = NormalizeClipsUrl(config?.ClipsUrl),
            GuestBehavior = config?.GuestBehavior.ToString() ?? string.Empty,
            RatingSystems = Enum.GetNames<RatingSystem>(),
            DefaultRatingSystem = RatingSystems.TryParse(config?.DefaultRatingSystem, out var s) ? s.Value.ToString() : nameof(RatingSystem.TR),
            AgeRatings = AgeRatings.WireValues,
            MaxProfiles = config?.MaxProfilesPerHousehold ?? 10,
            RequireCloudProof = config?.RequireCloudProof ?? true,
            RequireCloudLink = config?.RequireCloudLink ?? false,
        };
    }

    /// <summary>
    /// Normalizes the administrator's holix-clips URL: an absolute http(s) URL without query, fragment or user
    /// info, without a trailing <c>/</c>. Anything else (empty, relative, other schemes) gives null, so the
    /// field is left out of <c>/Holix/Info</c>.
    /// </summary>
    /// <param name="url">The configured URL.</param>
    /// <returns>The URL to announce, or null.</returns>
    internal static string? NormalizeClipsUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var trimmed = url.Trim().TrimEnd('/');
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        return trimmed;
    }

    /// <summary>The caller's household (owner: all profiles; profile: only itself).</summary>
    /// <returns>The household.</returns>
    [HttpGet("Household")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<HouseholdDto>> GetHousehold() =>
        Run(caller => Task.FromResult(_service.GetHousehold(caller)));

    /// <summary>Makes the caller a household owner (idempotent).</summary>
    /// <param name="request">The request body.</param>
    /// <returns>The household.</returns>
    [HttpPost("Household")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public Task<ActionResult<HouseholdDto>> CreateHousehold([FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] CreateHouseholdRequest? request) =>
        Run(caller => _service.CreateHouseholdAsync(caller, request ?? new CreateHouseholdRequest()));

    /// <summary>Deletes the household.</summary>
    /// <param name="deleteProfileUsers">Whether to also delete the profiles' Jellyfin users (default: yes).</param>
    /// <returns>204.</returns>
    [HttpDelete("Household")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<ActionResult> DeleteHousehold([FromQuery] bool deleteProfileUsers = true) =>
        RunNoContent(caller => _service.DeleteHouseholdAsync(caller, deleteProfileUsers));

    /// <summary>
    /// Links the household to a holix-cloud household (only while unlinked; relinking is done from the admin
    /// configuration page). Once linked, profile sessions require a proof signed by holix-cloud.
    /// </summary>
    /// <param name="request">Body: <c>{Code}</c> (from holix-cloud <c>POST /v1/jellyfin/link-code</c>).</param>
    /// <returns>The household.</returns>
    [HttpPost("Household/Link")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<HouseholdDto>> LinkHousehold([FromBody] LinkRequest request) =>
        Run(caller => _service.LinkCloudAsync(caller, request));

    /// <summary>Manually re-syncs profiles after the owner's policy changed (normally automatic).</summary>
    /// <returns>204.</returns>
    [HttpPost("Household/Sync")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<ActionResult> SyncHousehold() =>
        RunNoContent(async caller =>
        {
            _service.ListProfiles(caller); // ownership check
            await _service.SyncPoliciesAsync(caller.Id).ConfigureAwait(false);
        });

    /// <summary>Lists the profiles (owner first).</summary>
    /// <returns>The profiles.</returns>
    [HttpGet("Profiles")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<ActionResult<IReadOnlyList<ProfileDto>>> GetProfiles() =>
        Run(caller => Task.FromResult(_service.ListProfiles(caller)));

    /// <summary>Gets a single profile.</summary>
    /// <param name="id">The profile (Jellyfin user) id.</param>
    /// <returns>The profile.</returns>
    [HttpGet("Profiles/{id}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult<ProfileDto>> GetProfile([FromRoute] Guid id) =>
        Run(caller => Task.FromResult(_service.GetProfile(caller, id)));

    /// <summary>Creates a profile (a new Jellyfin user).</summary>
    /// <param name="request">The request body.</param>
    /// <returns>The profile.</returns>
    [HttpPost("Profiles")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProfileDto>> CreateProfile([FromBody] ProfileRequest request)
    {
        var result = await Run(caller => _service.CreateProfileAsync(caller, request)).ConfigureAwait(false);
        return result.Value is { } dto
            ? CreatedAtAction(nameof(GetProfile), new { id = dto.Id }, dto)
            : result;
    }

    /// <summary>Updates a profile (only the given fields).</summary>
    /// <param name="id">The profile id.</param>
    /// <param name="request">The request body.</param>
    /// <returns>The profile.</returns>
    [HttpPatch("Profiles/{id}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<ActionResult<ProfileDto>> UpdateProfile([FromRoute] Guid id, [FromBody] ProfileRequest request) =>
        Run(caller => _service.UpdateProfileAsync(caller, id, request));

    /// <summary>Deletes the profile and its Jellyfin user.</summary>
    /// <param name="id">The profile id.</param>
    /// <returns>204.</returns>
    [HttpDelete("Profiles/{id}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<ActionResult> DeleteProfile([FromRoute] Guid id) =>
        RunNoContent(caller => _service.DeleteProfileAsync(caller, id));

    /// <summary>
    /// Issues a Jellyfin access token for the profile. Requires the household owner's token and, when the
    /// household is linked to holix-cloud, the proof holix-cloud signs after PIN verification (<c>JellyfinProof</c>).
    /// </summary>
    /// <param name="id">The profile id.</param>
    /// <param name="request">Device info (optional).</param>
    /// <returns>The session.</returns>
    [HttpPost("Profiles/{id}/Session")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ProfileSessionDto>> OpenSession([FromRoute] Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SessionRequest? request)
    {
        var info = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var remote = HttpContext.GetNormalizedRemoteIP().ToString();
        return await Run(caller => _service.OpenSessionAsync(
            caller,
            info.DeviceId,
            info.Client,
            info.Version,
            info.Device,
            remote,
            id,
            request ?? new SessionRequest())).ConfigureAwait(false);
    }

    /// <summary>
    /// Stores the profile picture (the photo uploaded to holix-cloud or a rendered stock avatar) as the
    /// Jellyfin user image. The body is raw JPEG/PNG (not base64), at most 2 MB. Requires the household
    /// owner's token; no proof needed.
    /// </summary>
    /// <param name="id">The profile's (or owner's) user id.</param>
    /// <returns>The updated profile (with a new <c>PrimaryImageTag</c>).</returns>
    [HttpPut("Profiles/{id}/Image")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    public Task<ActionResult<ProfileDto>> SetProfileImage([FromRoute] Guid id) =>
        Run(async caller =>
        {
            if (!ProfileImageFormat.AcceptsContentType(Request.ContentType))
            {
                throw new HolixException(415, "unsupported_image", "Content-Type must be image/jpeg or image/png.");
            }

            if (Request.ContentLength > ProfileImageFormat.MaxBytes)
            {
                throw new HolixException(413, "image_too_large", "The image must be at most 2 MB.");
            }

            var data = await ReadLimitedAsync(Request.Body, ProfileImageFormat.MaxBytes, HttpContext.RequestAborted).ConfigureAwait(false);
            return await _service.SetProfileImageAsync(caller, id, data).ConfigureAwait(false);
        });

    /// <summary>Removes the profile's Jellyfin user image.</summary>
    /// <param name="id">The profile id.</param>
    /// <returns>204.</returns>
    [HttpDelete("Profiles/{id}/Image")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<ActionResult> DeleteProfileImage([FromRoute] Guid id) =>
        RunNoContent(caller => _service.DeleteProfileImageAsync(caller, id));

    /// <summary>Revokes all Jellyfin tokens of the profile.</summary>
    /// <param name="id">The profile id.</param>
    /// <returns>204.</returns>
    [HttpDelete("Profiles/{id}/Session")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public Task<ActionResult> RevokeSessions([FromRoute] Guid id) =>
        RunNoContent(caller => _service.RevokeSessionsAsync(caller, id));

    /// <summary>Admin: all households (read-only, for the configuration page).</summary>
    /// <returns>The households.</returns>
    [HttpGet("Admin/Households")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<HouseholdDto>> GetAllHouseholds() => Ok(_service.ListAllHouseholds());

    /// <summary>Admin: links or relinks a household to holix-cloud (configuration page).</summary>
    /// <param name="ownerId">The household owner's user id.</param>
    /// <param name="request">Body: <c>{Code}</c>.</param>
    /// <returns>The household.</returns>
    [HttpPost("Admin/Households/{ownerId}/Link")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<ActionResult<HouseholdDto>> AdminLinkHousehold([FromRoute] Guid ownerId, [FromBody] LinkRequest request) =>
        Guard(() => _service.AdminLinkCloudAsync(ownerId, request));

    /// <summary>Admin: removes a household's holix-cloud link (lifting the proof requirement for that household).</summary>
    /// <param name="ownerId">The household owner's user id.</param>
    /// <returns>The household.</returns>
    [HttpDelete("Admin/Households/{ownerId}/Link")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public Task<ActionResult<HouseholdDto>> AdminUnlinkHousehold([FromRoute] Guid ownerId) =>
        Guard(() => Task.FromResult(_service.AdminUnlinkCloud(ownerId)));

    /// <summary>Reads the stream up to <paramref name="limit"/> bytes; throws 413 when exceeded.</summary>
    /// <param name="body">The stream.</param>
    /// <param name="limit">The byte limit.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The bytes read.</returns>
    internal static async Task<byte[]> ReadLimitedAsync(Stream body, int limit, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > limit)
            {
                throw new HolixException(413, "image_too_large", "The image must be at most 2 MB.");
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    private async Task<User> CallerAsync()
    {
        var info = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        if (info.IsApiKey || info.User is null)
        {
            throw HolixException.Forbidden("A Jellyfin user token is required (not an API key).");
        }

        return info.User;
    }

    private async Task<ActionResult<T>> Run<T>(Func<User, Task<T>> action)
    {
        try
        {
            var caller = await CallerAsync().ConfigureAwait(false);
            return await action(caller).ConfigureAwait(false);
        }
        catch (HolixException ex)
        {
            return StatusCode(ex.Status, new ErrorDto(ex.Code, ex.Message));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ErrorDto("invalid_request", ex.Message));
        }
    }

    private async Task<ActionResult<T>> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (HolixException ex)
        {
            return StatusCode(ex.Status, new ErrorDto(ex.Code, ex.Message));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new ErrorDto("invalid_request", ex.Message));
        }
    }

    private async Task<ActionResult> RunNoContent(Func<User, Task> action)
    {
        var result = await Run<bool>(async caller =>
        {
            await action(caller).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
        return result.Result ?? NoContent();
    }
}
