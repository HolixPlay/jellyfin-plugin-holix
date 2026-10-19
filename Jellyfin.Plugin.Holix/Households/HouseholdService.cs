using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Holix.Api;
using Jellyfin.Plugin.Holix.Cloud;
using Jellyfin.Plugin.Holix.Configuration;
using Jellyfin.Plugin.Holix.Images;
using Jellyfin.Plugin.Holix.Ratings;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Users;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Holix.Households;

/// <summary>
/// Household/profile operations: creates Jellyfin users, writes their policies and opens sessions
/// on behalf of profiles. Authorization (caller = household owner) is enforced here, not in the controller.
/// </summary>
public sealed class HouseholdService
{
    /// <summary>Minimum interval between JWKS refetches triggered by an unknown <c>kid</c>.</summary>
    private static readonly TimeSpan JwksRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly IServerApplicationHost _appHost;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly CloudClient _cloud;
    private readonly ProofReplayCache _replay;
    private readonly ILogger<HouseholdService> _logger;

    /// <summary>Initializes a new instance of the <see cref="HouseholdService"/> class.</summary>
    /// <param name="userManager">User manager.</param>
    /// <param name="sessionManager">Session manager.</param>
    /// <param name="appHost">Server host (its id is compared with <c>jf_sid</c> in the proof).</param>
    /// <param name="serverConfig">Server configuration (user image directory).</param>
    /// <param name="cloud">holix-cloud client (linking + JWKS).</param>
    /// <param name="replay">Used proofs.</param>
    /// <param name="logger">Logger.</param>
    public HouseholdService(
        IUserManager userManager,
        ISessionManager sessionManager,
        IServerApplicationHost appHost,
        IServerConfigurationManager serverConfig,
        CloudClient cloud,
        ProofReplayCache replay,
        ILogger<HouseholdService> logger)
    {
        _userManager = userManager;
        _sessionManager = sessionManager;
        _appHost = appHost;
        _serverConfig = serverConfig;
        _cloud = cloud;
        _replay = replay;
        _logger = logger;
    }

    private static Plugin P => Plugin.Instance ?? throw new HolixException("The plugin is not loaded.");

    private static HouseholdStore Store => P.Households;

    private static PluginConfiguration Config => P.Configuration;

    /// <summary>Returns the caller's household (owner: all profiles; profile: only itself).</summary>
    /// <param name="caller">Caller.</param>
    /// <returns>The household.</returns>
    public HouseholdDto GetHousehold(User caller)
    {
        PruneDeleted();
        var m = Store.Resolve(caller.Id);
        return m.Role switch
        {
            HouseholdRole.Owner => ToDto(m.Household!, "owner", ProfilesOf(m.Household!)),
            HouseholdRole.Profile => ToDto(m.Household!, "profile", [ToDto(m.Household!, m.Profile!)]),
            _ => throw HolixException.NotFound("This user has no Holix household."),
        };
    }

    /// <summary>Administrator view: all households.</summary>
    /// <returns>The households.</returns>
    public IReadOnlyList<HouseholdDto> ListAllHouseholds()
    {
        PruneDeleted();
        return Store.All().Select(h => ToDto(h, "admin", ProfilesOf(h))).ToList();
    }

    /// <summary>Makes the caller a household owner (returns the existing household if any, updating the given fields).</summary>
    /// <param name="caller">Caller.</param>
    /// <param name="request">Request body.</param>
    /// <returns>The household.</returns>
    public async Task<HouseholdDto> CreateHouseholdAsync(User caller, CreateHouseholdRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var m = Store.Resolve(caller.Id);
        if (m.Role == HouseholdRole.Profile)
        {
            throw HolixException.Forbidden("A profile user cannot create a household.");
        }

        if (m.Role == HouseholdRole.None && !CanOwn(caller))
        {
            throw new HolixException(403, "household_not_allowed", "Creating a household is limited on this server; ask the server administrator (Dashboard → Plugins → Holix).");
        }

        RatingSystem? system = null;
        if (request.RatingSystem is not null)
        {
            system = RatingSystems.TryParse(request.RatingSystem, out var s)
                ? s
                : throw HolixException.BadRequest("invalid_rating_system", "ratingSystem: TR, US or DE.");
        }

        var h = Store.Update(caller.Id, existing =>
        {
            existing ??= new Household
            {
                OwnerUserId = caller.Id,
                RatingSystem = DefaultSystem(),
                CreatedUtc = DateTime.UtcNow,
            };
            if (system is not null)
            {
                existing.RatingSystem = system.Value;
            }

            if (request.OwnerHolixProfileId is not null)
            {
                var next = EmptyToNull(request.OwnerHolixProfileId);
                CheckMappingChange(existing, existing.OwnerUserId, existing.OwnerHolixProfileId, next, Config.RequireCloudProof);
                existing.OwnerHolixProfileId = next;
            }

            return existing;
        })!;

        if (system is not null && m.Role == HouseholdRole.Owner)
        {
            // If the system changed, recompute the threshold of every profile.
            await SyncPoliciesAsync(caller.Id).ConfigureAwait(false);
        }

        return ToDto(h, "owner", ProfilesOf(h));
    }

    /// <summary>Deletes the household and, optionally, its profile users.</summary>
    /// <param name="caller">Owner.</param>
    /// <param name="deleteProfileUsers">Whether to delete the profile users.</param>
    /// <returns>A task.</returns>
    public async Task DeleteHouseholdAsync(User caller, bool deleteProfileUsers)
    {
        var h = RequireOwner(caller);
        Store.Update(caller.Id, _ => null);
        if (deleteProfileUsers)
        {
            foreach (var p in h.Profiles)
            {
                await DeleteUserQuietlyAsync(p.UserId).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Lists the profiles (owner first).</summary>
    /// <param name="caller">Owner.</param>
    /// <returns>The profiles.</returns>
    public IReadOnlyList<ProfileDto> ListProfiles(User caller)
    {
        PruneDeleted();
        return ProfilesOf(RequireOwner(caller));
    }

    /// <summary>A single profile.</summary>
    /// <param name="caller">Owner.</param>
    /// <param name="profileId">Profile (Jellyfin user) id.</param>
    /// <returns>The profile.</returns>
    public ProfileDto GetProfile(User caller, Guid profileId)
    {
        var h = RequireOwner(caller);
        if (profileId == h.OwnerUserId)
        {
            return OwnerDto(h);
        }

        return ToDto(h, FindProfile(h, profileId));
    }

    /// <summary>New profile = new Jellyfin user.</summary>
    /// <param name="caller">Owner.</param>
    /// <param name="request">Request body.</param>
    /// <returns>The profile.</returns>
    public async Task<ProfileDto> CreateProfileAsync(User caller, ProfileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var h = RequireOwner(caller);
        if (h.Profiles.Count + 1 >= Math.Max(1, Config.MaxProfilesPerHousehold))
        {
            throw HolixException.Conflict("profile_limit", $"At most {Config.MaxProfilesPerHousehold} profiles per household (including the owner).");
        }

        var name = ProfileNaming.NormalizeDisplayName(request.Name)
            ?? throw HolixException.BadRequest("invalid_name", $"name: 1–{ProfileNaming.MaxNameLength} characters.");
        var kind = ParseKind(request.Kind) ?? ProfileKind.Adult;
        var profile = new HouseholdProfile
        {
            Name = name,
            Kind = kind,
            AgeRating = ParseAgeRating(request.AgeRating) ?? DefaultAgeRating(kind),
            RatingSystem = ParseSystemOrNull(request.RatingSystem),
            BlockedTags = CleanTags(request.BlockedTags),
            HolixProfileId = EmptyToNull(request.HolixProfileId),
            CreatedUtc = DateTime.UtcNow,
        };
        CheckMappingChange(h, Guid.Empty, null, profile.HolixProfileId, Config.RequireCloudProof);

        var userName = ProfileNaming.BuildUserName(caller.Username, name, n => _userManager.GetUserByName(n) is not null);
        var user = await _userManager.CreateUserAsync(userName).ConfigureAwait(false);
        try
        {
            // A passwordless user could be signed in via AuthenticateByName with an empty password, so a random,
            // never-stored password is set. The only way into a profile is /Holix/.../Session with the owner token.
            await _userManager.ChangePassword(user.Id, RandomSecret()).ConfigureAwait(false);
            profile.UserId = user.Id;
            await ApplyPolicyAsync(caller, h.RatingSystem, profile).ConfigureAwait(false);
            h = Store.Update(caller.Id, current =>
            {
                if (current is null)
                {
                    throw HolixException.NotFound("The household has been deleted.");
                }

                current.Profiles.Add(profile);
                return current;
            })!;
        }
        catch
        {
            await DeleteUserQuietlyAsync(user.Id).ConfigureAwait(false);
            throw;
        }

        _logger.LogInformation("Holix: added {Kind} profile {UserName} to household of {Owner}", caller.Username, kind, userName);
        return ToDto(h, FindProfile(h, user.Id));
    }

    /// <summary>Updates the profile (given fields only).</summary>
    /// <param name="caller">Owner.</param>
    /// <param name="profileId">Profile id.</param>
    /// <param name="request">Request body.</param>
    /// <returns>The profile.</returns>
    public async Task<ProfileDto> UpdateProfileAsync(User caller, Guid profileId, ProfileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var h = RequireOwner(caller);
        if (profileId == h.OwnerUserId)
        {
            // The owner is its own user; its policy is managed in the Jellyfin dashboard. Only the cloud mapping changes here.
            if (request.HolixProfileId is not null)
            {
                h = Store.Update(caller.Id, cur =>
                {
                    var next = EmptyToNull(request.HolixProfileId);
                    CheckMappingChange(cur!, cur!.OwnerUserId, cur.OwnerHolixProfileId, next, Config.RequireCloudProof);
                    cur.OwnerHolixProfileId = next;
                    return cur;
                })!;
            }

            return OwnerDto(h);
        }

        var existing = FindProfile(h, profileId);
        var updated = Copy(existing);
        if (request.Name is not null)
        {
            updated.Name = ProfileNaming.NormalizeDisplayName(request.Name)
                ?? throw HolixException.BadRequest("invalid_name", $"name: 1–{ProfileNaming.MaxNameLength} characters.");
        }

        if (request.Kind is not null)
        {
            updated.Kind = ParseKind(request.Kind)!.Value;
        }

        if (request.AgeRating is not null)
        {
            updated.AgeRating = ParseAgeRating(request.AgeRating)!.Value;
        }

        if (request.RatingSystem is not null)
        {
            updated.RatingSystem = ParseSystemOrNull(request.RatingSystem);
        }

        if (request.BlockedTags is not null)
        {
            updated.BlockedTags = CleanTags(request.BlockedTags);
        }

        if (request.HolixProfileId is not null)
        {
            updated.HolixProfileId = EmptyToNull(request.HolixProfileId);
            CheckMappingChange(h, profileId, existing.HolixProfileId, updated.HolixProfileId, Config.RequireCloudProof);
        }

        var user = _userManager.GetUserById(profileId) ?? throw HolixException.NotFound("Profile user not found.");
        if (!string.Equals(updated.Name, existing.Name, StringComparison.Ordinal))
        {
            var newUserName = ProfileNaming.BuildUserName(
                caller.Username,
                updated.Name,
                n => !string.Equals(n, user.Username, StringComparison.OrdinalIgnoreCase) && _userManager.GetUserByName(n) is not null);
            if (!string.Equals(newUserName, user.Username, StringComparison.Ordinal))
            {
                await _userManager.RenameUser(user.Id, user.Username, newUserName).ConfigureAwait(false);
            }
        }

        await ApplyPolicyAsync(caller, h.RatingSystem, updated).ConfigureAwait(false);
        h = Store.Update(caller.Id, cur =>
        {
            var list = cur!.Profiles;
            list[list.FindIndex(p => p.UserId == profileId)] = updated;
            return cur;
        })!;

        return ToDto(h, FindProfile(h, profileId));
    }

    /// <summary>Deletes the profile and its Jellyfin user.</summary>
    /// <param name="caller">Owner.</param>
    /// <param name="profileId">Profile id.</param>
    /// <returns>A task.</returns>
    public async Task DeleteProfileAsync(User caller, Guid profileId)
    {
        var h = RequireOwner(caller);
        if (profileId == h.OwnerUserId)
        {
            throw HolixException.BadRequest("owner_profile", "The owner profile cannot be deleted; delete the household instead.");
        }

        FindProfile(h, profileId);
        Store.Update(caller.Id, cur => { cur!.Profiles.RemoveAll(p => p.UserId == profileId); return cur; });
        await DeleteUserQuietlyAsync(profileId).ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a Jellyfin session on behalf of a profile user (passwordless; owner token only).
    /// </summary>
    /// <param name="caller">Owner.</param>
    /// <param name="callerDeviceId">Caller's device id.</param>
    /// <param name="callerClient">Caller's client name.</param>
    /// <param name="callerVersion">Caller's client version.</param>
    /// <param name="callerDevice">Caller's device name.</param>
    /// <param name="remoteEndPoint">Remote address.</param>
    /// <param name="profileId">Profile id.</param>
    /// <param name="request">Request body.</param>
    /// <returns>The session.</returns>
    public async Task<ProfileSessionDto> OpenSessionAsync(
        User caller,
        string? callerDeviceId,
        string? callerClient,
        string? callerVersion,
        string? callerDevice,
        string? remoteEndPoint,
        Guid profileId,
        SessionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var h = RequireOwner(caller);
        var dto = profileId == h.OwnerUserId ? OwnerDto(h) : ToDto(h, FindProfile(h, profileId));

        var baseDevice = EmptyToNull(request.DeviceId) ?? EmptyToNull(callerDeviceId)
            ?? throw HolixException.BadRequest("device_required", "deviceId is required (body or Authorization header).");
        var deviceId = profileId == h.OwnerUserId && request.DeviceId is not null
            ? baseDevice
            : DeriveDeviceId(baseDevice, profileId);

        // Household-wide screen limit, checked before the proof so a refused request does not burn it.
        var now = DateTime.UtcNow;
        var sessions = _sessionManager.Sessions.ToList();
        var ownerMax = _userManager.GetUserDto(caller).Policy?.MaxActiveSessions ?? 0;
        HouseholdSessionLimit.Check(h, ownerMax, sessions.Select(Snapshot).ToList(), baseDevice, deviceId, now);

        var (proofVerified, warning) = await CheckProofAsync(h, profileId, request.JellyfinProof).ConfigureAwait(false);

        if (profileId != h.OwnerUserId)
        {
            await EndReplaceableSessionsAsync(sessions, profileId, deviceId, now).ConfigureAwait(false);
        }

        AuthenticationResult result;
        try
        {
            result = await _sessionManager.AuthenticateDirect(new AuthenticationRequest
            {
                UserId = profileId,
                DeviceId = deviceId,
                DeviceName = EmptyToNull(request.DeviceName) ?? EmptyToNull(callerDevice) ?? "Holix Play",
                App = EmptyToNull(request.App) ?? EmptyToNull(callerClient) ?? "Holix Play",
                AppVersion = EmptyToNull(request.AppVersion) ?? EmptyToNull(callerVersion) ?? "0",
                RemoteEndPoint = remoteEndPoint ?? string.Empty,
            }).ConfigureAwait(false);
        }
        catch (SecurityException ex)
        {
            // Jellyfin's own per-user limit (the profile copies the owner's MaxActiveSessions) or device access.
            throw HolixException.Conflict(HouseholdSessionLimit.ErrorCode, "Jellyfin refused the session: " + ex.Message);
        }

        _logger.LogInformation("Holix: {Owner} opened a session for profile {Profile} (cloud proof: {Verified})", caller.Username, dto.UserName, proofVerified);
        return new ProfileSessionDto
        {
            Profile = dto,
            AccessToken = result.AccessToken,
            DeviceId = deviceId,
            ServerId = result.ServerId,
            Authentication = result,
            ProofVerified = proofVerified,
            Warning = warning,
        };
    }

    /// <summary>
    /// Account-first sign-in: opens a session with a holix-cloud session proof instead of the owner's password. The
    /// proof names the household (<c>hid</c>), this server and the device; the household must be linked to that cloud
    /// household (the owner did it with their own token). Without <see cref="CloudSessionRequest.ProfileId"/> (or with
    /// the owner's id) the owner's session is opened; for a profile the usual profile session rules apply, including
    /// the PIN proof (<see cref="CloudSessionRequest.ProfileProof"/>). The response carries the household so the
    /// device gets the profile list too. No Holix Pro check (client licence).
    /// </summary>
    /// <param name="request">Request body.</param>
    /// <param name="remoteEndPoint">Remote address.</param>
    /// <returns>The session and the household.</returns>
    public async Task<ProfileSessionDto> OpenCloudSessionAsync(CloudSessionRequest request, string? remoteEndPoint)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Config.AllowCloudSessions)
        {
            throw new HolixException(403, "cloud_session_disabled", "Signing in with the Holix account is turned off on this server; sign in with the Jellyfin password.");
        }

        var deviceId = EmptyToNull(request.DeviceId)
            ?? throw HolixException.BadRequest("device_required", "DeviceId is required (the same value given to holix-cloud).");
        if (string.IsNullOrWhiteSpace(request.Proof))
        {
            throw HolixException.BadRequest("proof_required", "Proof is required (holix-cloud POST /v1/servers/{id}/session-proof).");
        }

        PruneDeleted();
        var hid = CloudSessionVerifier.ReadHouseholdId(request.Proof);
        var h = Store.All()
            .Where(x => x.Cloud is not null && string.Equals(x.Cloud.HouseholdId, hid, StringComparison.Ordinal))
            .OrderByDescending(x => x.Cloud!.LinkedUtc)
            .FirstOrDefault()
            ?? throw new HolixException(404, "household_not_linked", "No household on this server is linked to this Holix account.");

        SessionProofClaims claims;
        try
        {
            claims = CloudSessionVerifier.Check(request.Proof, h.Cloud!, ServerId, deviceId, DateTimeOffset.UtcNow);
        }
        catch (HolixException ex) when (ex.Code == CloudProofVerifier.UnknownKeyCode)
        {
            var refreshed = await RefreshKeysAsync(h.OwnerUserId, h.Cloud!).ConfigureAwait(false);
            if (refreshed is null)
            {
                throw;
            }

            claims = CloudSessionVerifier.Check(request.Proof, refreshed, ServerId, deviceId, DateTimeOffset.UtcNow);
        }

        var owner = _userManager.GetUserById(h.OwnerUserId)
            ?? throw new HolixException(404, "household_not_linked", "The household owner no longer exists on this server.");
        var ownerPolicy = _userManager.GetUserDto(owner).Policy;
        if (ownerPolicy?.IsDisabled == true)
        {
            throw new HolixException(403, "owner_disabled", "The household owner's Jellyfin account is disabled.");
        }

        var profileId = request.ProfileId is { } p && p != Guid.Empty ? p : h.OwnerUserId;
        var isOwner = profileId == h.OwnerUserId;

        // Household-wide screen limit before the proof is consumed, so a refused request does not burn it.
        var now = DateTime.UtcNow;
        var sessions = _sessionManager.Sessions.ToList();
        HouseholdSessionLimit.Check(
            h,
            ownerPolicy?.MaxActiveSessions ?? 0,
            sessions.Select(Snapshot).ToList(),
            deviceId,
            isOwner ? deviceId : DeriveDeviceId(deviceId, profileId),
            now);
        CloudSessionVerifier.Consume(claims, DateTimeOffset.UtcNow, _replay);

        ProfileSessionDto session;
        if (!isOwner)
        {
            var pid = profileId;
            session = await OpenSessionAsync(
                owner,
                deviceId,
                request.App,
                request.AppVersion,
                request.DeviceName,
                remoteEndPoint,
                pid,
                new SessionRequest
                {
                    DeviceId = deviceId,
                    DeviceName = request.DeviceName,
                    App = request.App,
                    AppVersion = request.AppVersion,
                    JellyfinProof = request.ProfileProof,
                }).ConfigureAwait(false);
        }
        else
        {
            AuthenticationResult result;
            try
            {
                result = await _sessionManager.AuthenticateDirect(new AuthenticationRequest
                {
                    UserId = owner.Id,
                    DeviceId = deviceId,
                    DeviceName = EmptyToNull(request.DeviceName) ?? "Holix Play",
                    App = EmptyToNull(request.App) ?? "Holix Play",
                    AppVersion = EmptyToNull(request.AppVersion) ?? "0",
                    RemoteEndPoint = remoteEndPoint ?? string.Empty,
                }).ConfigureAwait(false);
            }
            catch (SecurityException ex)
            {
                // Jellyfin's own per-user limit (MaxActiveSessions) or device access policy.
                throw HolixException.Conflict(HouseholdSessionLimit.ErrorCode, "Jellyfin refused the session: " + ex.Message);
            }

            session = new ProfileSessionDto
            {
                Profile = OwnerDto(h),
                AccessToken = result.AccessToken,
                DeviceId = deviceId,
                ServerId = result.ServerId,
                Authentication = result,
                ProofVerified = true,
            };
        }

        _logger.LogInformation("Holix: device {Device} signed in to household of {Owner} with a holix-cloud session proof (profile {Profile})", EmptyToNull(request.DeviceName) ?? deviceId, owner.Username, session.Profile.UserName);
        session.Household = ToDto(h, "owner", ProfilesOf(h));
        return session;
    }

    /// <summary>
    /// Links the household to holix-cloud (owner, only while not linked). The code is obtained from the cloud
    /// (<c>POST /v1/jellyfin/link-code</c>); the cloud URL comes only from the plugin settings.
    /// </summary>
    /// <param name="caller">Owner.</param>
    /// <param name="request">Request body.</param>
    /// <returns>The household.</returns>
    public async Task<HouseholdDto> LinkCloudAsync(User caller, LinkRequest request)
    {
        var h = RequireOwner(caller);
        if (h.Cloud is not null)
        {
            // Otherwise anyone holding the owner token could relink the household to another cloud/account and mint proofs themselves.
            throw HolixException.Conflict("already_linked", "The household is already linked to holix-cloud; only a Jellyfin administrator can relink it (plugin settings page).");
        }

        await LinkAsync(caller.Id, request).ConfigureAwait(false);

        // Same body as GET /Holix/Household (including the link and current profiles).
        return GetHousehold(caller);
    }

    /// <summary>Administrator: links or relinks the household.</summary>
    /// <param name="ownerUserId">Household owner.</param>
    /// <param name="request">Request body.</param>
    /// <returns>The household.</returns>
    public async Task<HouseholdDto> AdminLinkCloudAsync(Guid ownerUserId, LinkRequest request)
    {
        if (Store.Resolve(ownerUserId).Role != HouseholdRole.Owner)
        {
            throw HolixException.NotFound("Household not found.");
        }

        var linked = await LinkAsync(ownerUserId, request).ConfigureAwait(false);
        return ToDto(linked, "admin", ProfilesOf(linked));
    }

    /// <summary>Administrator: removes the cloud link (the household reverts to legacy behaviour; mappings become editable again).</summary>
    /// <param name="ownerUserId">Household owner.</param>
    /// <returns>The household.</returns>
    public HouseholdDto AdminUnlinkCloud(Guid ownerUserId)
    {
        var h = Store.Update(ownerUserId, cur =>
        {
            if (cur is null)
            {
                throw HolixException.NotFound("Household not found.");
            }

            cur.Cloud = null;
            return cur;
        })!;
        _logger.LogWarning("Holix: holix-cloud link of household {Owner} removed by an administrator", ownerUserId);
        return ToDto(h, "admin", ProfilesOf(h));
    }

    private string ServerId =>
        CloudProofVerifier.NormalizeId(_appHost.SystemId)
        ?? throw new HolixException("Could not read the server id (SystemId).");

    private async Task<Household> LinkAsync(Guid ownerUserId, LinkRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = EmptyToNull(request.Code)
            ?? throw HolixException.BadRequest("code_required", "code is required (holix-cloud POST /v1/jellyfin/link-code).");
        if (code.Length > 32)
        {
            throw HolixException.BadRequest("invalid_link_code", "Invalid link code.");
        }

        var baseUrl = CloudClient.ValidateBaseUrl(Config.CloudUrl);
        var link = await _cloud.RedeemAsync(baseUrl, code, ServerId, _appHost.FriendlyName, CancellationToken.None).ConfigureAwait(false);
        var h = Store.Update(ownerUserId, cur =>
        {
            if (cur is null)
            {
                throw HolixException.NotFound("The household has been deleted.");
            }

            cur.Cloud = link;
            return cur;
        })!;
        _logger.LogInformation("Holix: household {Owner} linked to holix-cloud household {HouseholdId} ({Url})", ownerUserId, link.HouseholdId, link.CloudUrl);
        return h;
    }

    /// <summary>
    /// Checks the cloud proof for a profile session. In a linked household (with the setting on) the proof is
    /// required; an unlinked household gets the transitional behaviour (no proof, with a warning) or is
    /// rejected when <c>RequireCloudLink</c> is set.
    /// </summary>
    private async Task<(bool Verified, string? Warning)> CheckProofAsync(Household h, Guid profileId, string? proof)
    {
        var link = h.Cloud;
        if (link is null)
        {
            if (Config.RequireCloudLink)
            {
                throw new HolixException(403, "cloud_link_required", "On this server the household must be linked to holix-cloud to open profile sessions (POST /Holix/Household/Link).");
            }

            _logger.LogWarning("Holix: household {Owner} is not linked to holix-cloud; opening profile session without PIN proof (transitional)", h.OwnerUserId);
            return (false, "cloud_not_linked");
        }

        if (string.IsNullOrWhiteSpace(proof))
        {
            if (Config.RequireCloudProof)
            {
                throw new HolixException(403, "proof_required", "The household is linked to holix-cloud: a profile session requires a proof issued by holix-cloud (JellyfinProof).");
            }

            _logger.LogWarning("Holix: household {Owner} is linked but proof is not required; opening profile session without proof", h.OwnerUserId);
            return (false, "proof_missing");
        }

        var isOwner = profileId == h.OwnerUserId;
        var profile = isOwner ? null : FindProfile(h, profileId);
        var holixId = isOwner ? h.OwnerHolixProfileId : profile!.HolixProfileId;
        if (string.IsNullOrEmpty(holixId))
        {
            throw new HolixException(403, "profile_not_mapped", "The profile is not mapped to a holix-cloud profile (HolixProfileId); the proof cannot be verified.");
        }

        var expected = new ProofExpectation(
            ServerId,
            profileId,
            holixId,
            profile?.Kind.ToString().ToLowerInvariant(),
            profile?.AgeRating.ToWire());
        try
        {
            CloudProofVerifier.Verify(proof, link, expected, DateTimeOffset.UtcNow, _replay);
        }
        catch (HolixException ex) when (ex.Code == CloudProofVerifier.UnknownKeyCode)
        {
            // The cloud may have rotated its key: refresh the JWKS (rate limited) and retry once.
            var refreshed = await RefreshKeysAsync(h.OwnerUserId, link).ConfigureAwait(false);
            if (refreshed is null)
            {
                throw;
            }

            CloudProofVerifier.Verify(proof, refreshed, expected, DateTimeOffset.UtcNow, _replay);
        }

        return (true, null);
    }

    private async Task<CloudLink?> RefreshKeysAsync(Guid ownerUserId, CloudLink link)
    {
        if (DateTime.UtcNow - link.KeysFetchedUtc < JwksRefreshInterval)
        {
            return null;
        }

        IReadOnlyList<CloudKey> keys = [];
        try
        {
            keys = await _cloud.FetchKeysAsync(link, CancellationToken.None).ConfigureAwait(false);
        }
        catch (HolixException ex)
        {
            _logger.LogWarning("Holix: failed to refresh holix-cloud JWKS: {Message}", ex.Message);
        }

        var h = Store.Update(ownerUserId, cur =>
        {
            if (cur?.Cloud is null || cur.Cloud.HouseholdId != link.HouseholdId)
            {
                return cur;
            }

            cur.Cloud.KeysFetchedUtc = DateTime.UtcNow;
            if (keys.Count > 0)
            {
                cur.Cloud.Keys.Clear();
                cur.Cloud.Keys.AddRange(keys);
            }

            return cur;
        });
        return keys.Count > 0 ? h?.Cloud : null;
    }

    /// <summary>
    /// Validates a cloud mapping (<c>HolixProfileId</c>) change: it must be unique in the household, and in a
    /// linked household (while proof is required) an existing mapping cannot be changed; otherwise anyone
    /// holding the owner token could map a PIN-protected profile to a PIN-less cloud profile and open it
    /// with that profile's proof.
    /// </summary>
    internal static void CheckMappingChange(Household h, Guid profileId, string? current, string? next, bool requireProof)
    {
        if (string.Equals(current, next, StringComparison.Ordinal))
        {
            return;
        }

        if (h.Cloud is not null && requireProof && current is not null)
        {
            throw HolixException.Conflict("mapping_locked", "An existing HolixProfileId cannot be changed while the household is linked to holix-cloud; if needed, a Jellyfin administrator can unlink and relink it.");
        }

        if (next is not null
            && ((profileId != h.OwnerUserId && string.Equals(h.OwnerHolixProfileId, next, StringComparison.Ordinal))
                || h.Profiles.Any(p => p.UserId != profileId && string.Equals(p.HolixProfileId, next, StringComparison.Ordinal))))
        {
            throw HolixException.Conflict("mapping_in_use", "This holix-cloud profile is already mapped to another profile in the household.");
        }
    }

    /// <summary>Revokes all tokens of the profile (after a PIN change or a lost device).</summary>
    /// <param name="caller">Owner.</param>
    /// <param name="profileId">Profile id.</param>
    /// <returns>A task.</returns>
    public async Task RevokeSessionsAsync(User caller, Guid profileId)
    {
        var h = RequireOwner(caller);
        if (profileId == h.OwnerUserId)
        {
            throw HolixException.BadRequest("owner_profile", "The owner's sessions are managed in Jellyfin.");
        }

        FindProfile(h, profileId);
        await _sessionManager.RevokeUserTokens(profileId, string.Empty).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the profile image as the Jellyfin user image (<c>UserImage</c>) so Jellyfin web and other
    /// clients show the avatar. Owner token only (no proof needed: an image grants no access). Does what
    /// Jellyfin's <c>POST /Users/{id}/Images/Primary</c> does:
    /// <c>{UserConfigurationDirectoryPath}/{user name}/profile.jpg|png</c> + <c>User.ProfileImage</c>.
    /// </summary>
    /// <param name="caller">Owner.</param>
    /// <param name="profileId">User id of the profile (or of the owner).</param>
    /// <param name="data">Raw JPEG/PNG bytes.</param>
    /// <returns>The updated profile.</returns>
    public async Task<ProfileDto> SetProfileImageAsync(User caller, Guid profileId, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var h = RequireOwner(caller);
        if (profileId != h.OwnerUserId)
        {
            FindProfile(h, profileId);
        }

        if (data.Length > ProfileImageFormat.MaxBytes)
        {
            throw new HolixException(413, "image_too_large", "The image must be at most 2 MB.");
        }

        var format = ProfileImageFormat.Detect(data)
            ?? throw new HolixException(415, "unsupported_image", "Only JPEG or PNG is accepted.");
        var user = _userManager.GetUserById(profileId) ?? throw HolixException.NotFound("Profile user not found.");

        var dir = UserImageDirectory(_serverConfig.ApplicationPaths.UserConfigurationDirectoryPath, user.Username);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "profile" + format.Extension);
        var tmp = path + ".holix-tmp";
        await File.WriteAllBytesAsync(tmp, data).ConfigureAwait(false);

        var old = user.ProfileImage?.Path;
        if (user.ProfileImage is not null)
        {
            await _userManager.ClearProfileImageAsync(user).ConfigureAwait(false);
        }

        File.Move(tmp, path, overwrite: true);
        if (old is not null && !string.Equals(old, path, StringComparison.Ordinal))
        {
            TryDelete(old);
        }

        // A new ImageInfo carries a new LastModified → PrimaryImageTag changes and client caches refresh.
        user.ProfileImage = new ImageInfo(path);
        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
        _logger.LogInformation("Holix: updated image of profile {Profile} ({Mime}, {Bytes} bytes)", profileId, format.MimeType, data.Length);
        return profileId == h.OwnerUserId ? OwnerDto(h) : ToDto(h, FindProfile(h, profileId));
    }

    /// <summary>Removes the profile's Jellyfin user image (owner only).</summary>
    /// <param name="caller">Owner.</param>
    /// <param name="profileId">Profile id.</param>
    /// <returns>A task.</returns>
    public async Task DeleteProfileImageAsync(User caller, Guid profileId)
    {
        var h = RequireOwner(caller);
        if (profileId != h.OwnerUserId)
        {
            FindProfile(h, profileId);
        }

        var user = _userManager.GetUserById(profileId) ?? throw HolixException.NotFound("Profile user not found.");
        if (user.ProfileImage is null)
        {
            return;
        }

        var old = user.ProfileImage.Path;
        await _userManager.ClearProfileImageAsync(user).ConfigureAwait(false);
        TryDelete(old);
    }

    /// <summary>
    /// Resolves the per-user image directory and makes sure it stays inside the user configuration
    /// directory. Jellyfin user names may contain dots (for example <c>..</c>), so a crafted owner
    /// name must not be able to point the write outside that directory.
    /// </summary>
    /// <param name="root">Jellyfin user configuration directory.</param>
    /// <param name="userName">Jellyfin user name.</param>
    /// <returns>The absolute directory path.</returns>
    internal static string UserImageDirectory(string root, string userName)
    {
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var dir = Path.GetFullPath(Path.Combine(rootFull, userName));
        if (string.IsNullOrWhiteSpace(userName)
            || !string.Equals(Path.GetDirectoryName(dir), rootFull, StringComparison.Ordinal))
        {
            throw HolixException.BadRequest("invalid_user_name", "The user name cannot be used as an image directory.");
        }

        return dir;
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Holix: failed to delete old profile image: {Path}", path);
        }
    }

    /// <summary>Rewrites all profile policies from the owner's current policy.</summary>
    /// <param name="ownerUserId">Owner.</param>
    /// <returns>A task.</returns>
    public async Task SyncPoliciesAsync(Guid ownerUserId)
    {
        var m = Store.Resolve(ownerUserId);
        if (m.Role != HouseholdRole.Owner)
        {
            return;
        }

        var owner = _userManager.GetUserById(ownerUserId);
        if (owner is null)
        {
            return;
        }

        foreach (var p in m.Household!.Profiles)
        {
            if (_userManager.GetUserById(p.UserId) is not null)
            {
                await ApplyPolicyAsync(owner, m.Household.RatingSystem, p).ConfigureAwait(false);
            }
        }
    }

    private static SessionSnapshot Snapshot(SessionInfo s) =>
        new(s.UserId, s.DeviceId, s.LastActivityDate, s.NowPlayingItem is not null);

    /// <summary>
    /// Ends (in memory only; tokens stay valid) the profile's sessions that the new one replaces: the one on the same
    /// device, and idle ones that the household limit no longer counts. Otherwise Jellyfin's per-user
    /// <c>MaxActiveSessions</c> check, which counts every session in memory, would refuse re-opening a profile on
    /// the same TV. A client that comes back with its old token simply gets a new session.
    /// </summary>
    private async Task EndReplaceableSessionsAsync(IEnumerable<SessionInfo> sessions, Guid profileId, string deviceId, DateTime now)
    {
        foreach (var s in sessions.Where(s => s.UserId == profileId
            && (string.Equals(s.DeviceId, deviceId, StringComparison.Ordinal) || !HouseholdSessionLimit.IsActive(Snapshot(s), now))))
        {
            try
            {
                await _sessionManager.ReportSessionEnded(s.Id).ConfigureAwait(false);
            }
#pragma warning disable CA1031
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogWarning(ex, "Holix: could not end session {SessionId} of profile {Profile}", s.Id, profileId);
            }
        }
    }

    internal static string DeriveDeviceId(string baseDeviceId, Guid profileId)
    {
        // Give each profile on the same device its own Jellyfin device/session record: when Jellyfin issues a
        // new token for the same DeviceId it revokes that user's previous token on that device.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(baseDeviceId + "|holix|" + profileId.ToString("N")));
        return "holix-" + Convert.ToHexStringLower(bytes.AsSpan(0, 16));
    }

    private static string RandomSecret() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    private static RatingSystem DefaultSystem() =>
        RatingSystems.TryParse(Config.DefaultRatingSystem, out var s) ? s.Value : RatingSystem.TR;

    private static AgeRating DefaultAgeRating(ProfileKind kind) => kind switch
    {
        ProfileKind.Child => throw HolixException.BadRequest("age_rating_required", "ageRating is required for child profiles (all, 7+, 13+, 16+)."),
        _ => AgeRating.Age18,
    };

    private static ProfileKind? ParseKind(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null => null,
        "adult" => ProfileKind.Adult,
        "child" => ProfileKind.Child,
        "guest" => ProfileKind.Guest,
        _ => throw HolixException.BadRequest("invalid_kind", "kind: adult, child or guest."),
    };

    private static AgeRating? ParseAgeRating(string? value)
    {
        if (value is null)
        {
            return null;
        }

        return AgeRatings.TryParse(value, out var r)
            ? r
            : throw HolixException.BadRequest("invalid_age_rating", "ageRating: all, 7+, 13+, 16+ or 18+.");
    }

    private static RatingSystem? ParseSystemOrNull(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return RatingSystems.TryParse(value, out var s)
            ? s
            : throw HolixException.BadRequest("invalid_rating_system", "ratingSystem: TR, US or DE.");
    }

    private static string[] CleanTags(string[]? tags) =>
        (tags ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();

    private static string? EmptyToNull(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static HouseholdProfile Copy(HouseholdProfile p) => new()
    {
        UserId = p.UserId,
        Name = p.Name,
        Kind = p.Kind,
        AgeRating = p.AgeRating,
        RatingSystem = p.RatingSystem,
        BlockedTags = [.. p.BlockedTags],
        HolixProfileId = p.HolixProfileId,
        CreatedUtc = p.CreatedUtc,
    };

    private static HouseholdProfile FindProfile(Household h, Guid profileId) =>
        h.Profiles.FirstOrDefault(p => p.UserId == profileId)
        ?? throw HolixException.NotFound("The profile is not in this household.");

    private static HouseholdDto ToDto(Household h, string role, IReadOnlyList<ProfileDto> profiles) => new()
    {
        Role = role,
        OwnerUserId = h.OwnerUserId,
        OwnerName = profiles.FirstOrDefault(p => p.IsOwner)?.UserName ?? string.Empty,
        RatingSystem = h.RatingSystem.ToString(),
        CreatedUtc = h.CreatedUtc,
        Profiles = profiles,
        Cloud = h.Cloud is null || role == "profile" ? null : new CloudLinkDto
        {
            CloudUrl = h.Cloud.CloudUrl,
            HouseholdId = h.Cloud.HouseholdId,
            Issuer = h.Cloud.Issuer,
            KeyIds = h.Cloud.Keys.Select(k => k.Kid).ToList(),
            LinkedUtc = h.Cloud.LinkedUtc,
            ProofRequired = Config.RequireCloudProof,
        },
    };

    private bool CanOwn(User caller) =>
        OwnerAccess.CanCreateHousehold(Config, caller.Id, _userManager.GetUserDto(caller).Policy?.IsAdministrator == true);

    private static Household RequireOwner(User caller)
    {
        var m = Store.Resolve(caller.Id);
        return m.Role switch
        {
            HouseholdRole.Owner => m.Household!,
            HouseholdRole.Profile => throw HolixException.Forbidden("This operation requires the household owner's token."),
            _ => throw HolixException.NotFound("Create a household first with POST /Holix/Household."),
        };
    }

    private async Task ApplyPolicyAsync(User owner, RatingSystem householdSystem, HouseholdProfile profile)
    {
        var ownerPolicy = _userManager.GetUserDto(owner).Policy ?? new UserPolicy();
        var profileUser = _userManager.GetUserById(profile.UserId) ?? throw HolixException.NotFound("Profile user not found.");
        var current = _userManager.GetUserDto(profileUser).Policy ?? new UserPolicy();
        var policy = ProfilePolicyBuilder.Build(
            ownerPolicy,
            current,
            profile,
            householdSystem,
            Config.DefaultChildBlockedTags,
            Config.BlockUnratedForChildren);
        await _userManager.UpdatePolicyAsync(profile.UserId, policy).ConfigureAwait(false);
    }

    private List<ProfileDto> ProfilesOf(Household h)
    {
        var list = new List<ProfileDto> { OwnerDto(h) };
        list.AddRange(h.Profiles.OrderBy(p => p.CreatedUtc).Select(p => ToDto(h, p)));
        return list;
    }

    private ProfileDto OwnerDto(Household h)
    {
        var user = _userManager.GetUserById(h.OwnerUserId);
        var dto = user is null ? null : _userManager.GetUserDto(user);
        var limit = AgeRatingMapper.Map(AgeRating.Age18, h.RatingSystem);
        return new ProfileDto
        {
            Id = h.OwnerUserId,
            HolixProfileId = h.OwnerHolixProfileId,
            Name = user?.Username ?? string.Empty,
            UserName = user?.Username ?? string.Empty,
            Kind = "adult",
            IsOwner = true,
            AgeRating = AgeRating.Age18.ToWire(),
            RatingSystem = h.RatingSystem.ToString(),
            RatingLabel = dto?.Policy?.MaxParentalRating is null ? limit.Label : string.Empty,
            MaxParentalRating = dto?.Policy?.MaxParentalRating,
            MaxParentalSubRating = dto?.Policy?.MaxParentalSubRating,
            BlockedTags = dto?.Policy?.BlockedTags ?? [],
            PrimaryImageTag = dto?.PrimaryImageTag,
        };
    }

    private ProfileDto ToDto(Household h, HouseholdProfile p)
    {
        var user = _userManager.GetUserById(p.UserId);
        var dto = user is null ? null : _userManager.GetUserDto(user);
        var system = p.RatingSystem ?? h.RatingSystem;
        return new ProfileDto
        {
            Id = p.UserId,
            HolixProfileId = p.HolixProfileId,
            Name = p.Name,
            UserName = user?.Username ?? string.Empty,
            Kind = p.Kind.ToString().ToLowerInvariant(),
            IsOwner = false,
            AgeRating = p.AgeRating.ToWire(),
            RatingSystem = system.ToString(),
            RatingLabel = AgeRatingMapper.Map(p.AgeRating, system).Label,
            MaxParentalRating = dto?.Policy?.MaxParentalRating,
            MaxParentalSubRating = dto?.Policy?.MaxParentalSubRating,
            BlockedTags = dto?.Policy?.BlockedTags ?? [],
            PrimaryImageTag = dto?.PrimaryImageTag,
        };
    }

    private void PruneDeleted()
    {
        var removed = Store.Prune(id => _userManager.GetUserById(id) is not null);
        if (removed > 0)
        {
            _logger.LogInformation("Holix: removed {Count} deleted users from the mapping", removed);
        }
    }

    private async Task DeleteUserQuietlyAsync(Guid userId)
    {
        try
        {
            if (_userManager.GetUserById(userId) is not null)
            {
                await _userManager.DeleteUserAsync(userId).ConfigureAwait(false);
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Holix: failed to delete user {UserId}", userId);
        }
    }
}
