using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.Holix.Households;

/// <summary>A Jellyfin session as seen by the household session limit.</summary>
/// <param name="UserId">The session's user.</param>
/// <param name="DeviceId">The session's device id.</param>
/// <param name="LastActivityUtc">The last request made with the session.</param>
/// <param name="IsPlaying">Whether something is playing in the session.</param>
public readonly record struct SessionSnapshot(Guid UserId, string? DeviceId, DateTime LastActivityUtc, bool IsPlaying);

/// <summary>
/// Household-wide session limit: the owner and all profiles together may use at most the owner's
/// <c>MaxActiveSessions</c> screens (when it is &gt; 0). Pure function (unit tested).
/// <list type="bullet">
/// <item>Every Jellyfin session of the owner or a profile counts, from any client (Holix Play, Jellyfin web, …),
/// when it is playing or was active in the last <see cref="ActiveWindow"/>.</item>
/// <item>A screen is a physical device: the owner's session on a device and the profile sessions opened from
/// that device (their device ids are derived from it, see <see cref="HouseholdService.DeriveDeviceId"/>)
/// count once, so switching profiles on the same TV never hits the limit.</item>
/// </list>
/// </summary>
public static class HouseholdSessionLimit
{
    /// <summary>Error code returned when the limit is reached.</summary>
    public const string ErrorCode = "household_session_limit";

    /// <summary>A session counts when it is playing or made a request within this window (Jellyfin's dashboard uses 960 s).</summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(15);

    /// <summary>Whether a session is active (playing or recently used).</summary>
    /// <param name="s">The session.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <returns><c>true</c> if it counts.</returns>
    public static bool IsActive(SessionSnapshot s, DateTime nowUtc) =>
        s.IsPlaying || nowUtc - s.LastActivityUtc <= ActiveWindow;

    /// <summary>The screens (physical devices) the household uses right now.</summary>
    /// <param name="h">The household.</param>
    /// <param name="sessions">All Jellyfin sessions.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <returns>The distinct screen ids.</returns>
    public static HashSet<string> ActiveScreens(Household h, IEnumerable<SessionSnapshot> sessions, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(h);
        ArgumentNullException.ThrowIfNull(sessions);
        var members = new HashSet<Guid>(h.Profiles.Select(p => p.UserId)) { h.OwnerUserId };
        var mine = sessions.Where(s => members.Contains(s.UserId) && !string.IsNullOrEmpty(s.DeviceId)).ToList();

        // Base devices: wherever the owner has a session (active or not). A profile session whose device id was
        // derived from one of them is on the same physical device.
        var bases = mine.Where(s => s.UserId == h.OwnerUserId).Select(s => s.DeviceId!).ToHashSet(StringComparer.Ordinal);

        var screens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in mine.Where(s => IsActive(s, nowUtc)))
        {
            var device = s.DeviceId!;
            var screen = bases.Contains(device)
                ? device
                : bases.FirstOrDefault(b => string.Equals(HouseholdService.DeriveDeviceId(b, s.UserId), device, StringComparison.Ordinal)) ?? device;
            screens.Add(screen);
        }

        return screens;
    }

    /// <summary>
    /// Checks whether a new profile session may be opened; throws 409 <see cref="ErrorCode"/> when the household
    /// already uses all of the owner's screens on other devices. Allowed without counting: a device the household
    /// is already on (the owner's or another profile's session there), and re-opening the same profile on the same
    /// device (Jellyfin replaces that session).
    /// </summary>
    /// <param name="h">The household.</param>
    /// <param name="ownerMaxActiveSessions">The owner's <c>MaxActiveSessions</c> (0 = unlimited).</param>
    /// <param name="sessions">All Jellyfin sessions.</param>
    /// <param name="baseDevice">The device the session is opened for (request <c>DeviceId</c>, else the caller's).</param>
    /// <param name="newDeviceId">The device id the new session gets (derived per profile).</param>
    /// <param name="nowUtc">The current time.</param>
    public static void Check(Household h, int ownerMaxActiveSessions, IReadOnlyCollection<SessionSnapshot> sessions, string baseDevice, string newDeviceId, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        if (ownerMaxActiveSessions <= 0)
        {
            return;
        }

        var screens = ActiveScreens(h, sessions, nowUtc);
        var sameScreen = screens.Contains(baseDevice) || screens.Contains(newDeviceId);
        if (screens.Count < ownerMaxActiveSessions || (sameScreen && screens.Count <= ownerMaxActiveSessions))
        {
            return;
        }

        throw HolixException.Conflict(
            ErrorCode,
            $"This household is already watching on {screens.Count} of {ownerMaxActiveSessions} allowed screens. Stop playback on another device and try again.");
    }
}
