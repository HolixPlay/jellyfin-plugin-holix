using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Holix.Households;
using Jellyfin.Plugin.Holix.Ratings;

namespace Jellyfin.Plugin.Holix.Tests;

public class HouseholdSessionLimitTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 20, 0, 0, DateTimeKind.Utc);

    private readonly Guid _owner = Guid.NewGuid();
    private readonly Guid _kids = Guid.NewGuid();
    private readonly Guid _mom = Guid.NewGuid();
    private readonly Guid _dad = Guid.NewGuid();

    private Household Home()
    {
        var h = new Household { OwnerUserId = _owner, RatingSystem = RatingSystem.TR };
        h.Profiles.Add(new HouseholdProfile { UserId = _kids, Name = "Kids", Kind = ProfileKind.Child, AgeRating = AgeRating.Age7 });
        h.Profiles.Add(new HouseholdProfile { UserId = _mom, Name = "Mom" });
        h.Profiles.Add(new HouseholdProfile { UserId = _dad, Name = "Dad" });
        return h;
    }

    private static SessionSnapshot Owner(Guid user, string device, int idleMinutes = 0, bool playing = false) =>
        new(user, device, Now.AddMinutes(-idleMinutes), playing);

    private static SessionSnapshot Profile(Guid user, string baseDevice, int idleMinutes = 0, bool playing = false) =>
        new(user, HouseholdService.DeriveDeviceId(baseDevice, user), Now.AddMinutes(-idleMinutes), playing);

    private void Open(List<SessionSnapshot> sessions, int max, Guid profile, string baseDevice) =>
        HouseholdSessionLimit.Check(Home(), max, sessions, baseDevice, HouseholdService.DeriveDeviceId(baseDevice, profile), Now);

    private static void AssertRefused(Action open)
    {
        var ex = Assert.Throws<HolixException>(open);
        Assert.Equal(409, ex.Status);
        Assert.Equal(HouseholdSessionLimit.ErrorCode, ex.Code);
    }

    [Fact]
    public void Owner_profile_sessions_on_one_device_are_one_screen()
    {
        var sessions = new List<SessionSnapshot>
        {
            Owner(_owner, "tv"),
            Profile(_kids, "tv", playing: true),
            Profile(_mom, "tv", idleMinutes: 3),
        };
        Assert.Equal(["tv"], HouseholdSessionLimit.ActiveScreens(Home(), sessions, Now));

        // Switching profiles on the same TV is always allowed, even with a limit of 1.
        Open(sessions, 1, _dad, "tv");
    }

    [Fact]
    public void New_screen_is_refused_when_the_owner_limit_is_used()
    {
        var sessions = new List<SessionSnapshot>
        {
            Owner(_owner, "tv"),
            Profile(_kids, "tv", playing: true),
            Owner(_owner, "ipad"),
            Profile(_mom, "ipad", playing: true),
        };

        // The owner allows 2 screens; a third device (any device id sent in the body) is refused …
        AssertRefused(() => Open(sessions, 2, _dad, "phone"));

        // … while 3 allowed screens, unlimited (0) and the devices already in use are fine.
        Open(sessions, 3, _dad, "phone");
        Open(sessions, 0, _dad, "phone");
        Open(sessions, 2, _dad, "tv");
        Open(sessions, 2, _dad, "ipad");
    }

    [Fact]
    public void Ten_profiles_cannot_stream_on_ten_devices()
    {
        // Device ids invented by a client: each profile session is its own screen.
        var sessions = new List<SessionSnapshot> { Owner(_owner, "tv") };
        Open(sessions, 2, _kids, "x1");
        sessions.Add(Profile(_kids, "x1", playing: true));
        AssertRefused(() => Open(sessions, 2, _mom, "x2"));

        // Re-opening the same profile on the same device replaces its session.
        Open(sessions, 2, _kids, "x1");
    }

    [Fact]
    public void Other_jellyfin_clients_of_the_owner_count()
    {
        var sessions = new List<SessionSnapshot>
        {
            Owner(_owner, "jellyfin-web-laptop", playing: true),
            Owner(_owner, "tv"),
        };
        Assert.Equal(2, HouseholdSessionLimit.ActiveScreens(Home(), sessions, Now).Count);
        AssertRefused(() => Open(sessions, 2, _kids, "phone"));
        Open(sessions, 2, _kids, "tv");
    }

    [Fact]
    public void Idle_sessions_do_not_count_playing_ones_do()
    {
        var sessions = new List<SessionSnapshot>
        {
            Owner(_owner, "tv"),
            Owner(_owner, "old-ipad", idleMinutes: 16),
            Profile(_mom, "old-phone", idleMinutes: 120),
            Profile(_kids, "car", idleMinutes: 40, playing: true),
        };

        // Screens: tv and the playing car session (no owner session there, so its own device id). Idle ones are free.
        var screens = HouseholdSessionLimit.ActiveScreens(Home(), sessions, Now);
        Assert.Equal(2, screens.Count);
        Assert.Contains("tv", screens);
        Assert.Contains(HouseholdService.DeriveDeviceId("car", _kids), screens);
        Open(sessions, 3, _dad, "phone");
        AssertRefused(() => Open(sessions, 2, _dad, "phone"));
    }

    [Fact]
    public void Sessions_of_other_users_are_ignored()
    {
        var sessions = new List<SessionSnapshot>
        {
            Owner(Guid.NewGuid(), "neighbour-tv", playing: true),
            Owner(Guid.NewGuid(), "neighbour-ipad", playing: true),
            Owner(_owner, "tv"),
        };
        Open(sessions, 2, _kids, "phone");
    }

    [Fact]
    public void Already_over_the_limit_refuses_even_on_a_used_device()
    {
        // E.g. the limit was lowered while three screens were in use.
        var sessions = new List<SessionSnapshot>
        {
            Owner(_owner, "tv", playing: true),
            Owner(_owner, "ipad", playing: true),
            Owner(_owner, "laptop", playing: true),
        };
        AssertRefused(() => Open(sessions, 2, _kids, "tv"));
    }
}
