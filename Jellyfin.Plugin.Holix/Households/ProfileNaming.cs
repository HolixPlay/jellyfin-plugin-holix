using System;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Holix.Households;

/// <summary>
/// Builds a unique, Jellyfin-compatible user name from a profile name:
/// <c>{owner}.{profile}</c> (e.g. <c>alex.Kids</c>). Jellyfin rule
/// (<c>UserManager.ValidUsernameRegex</c>): <c>^(?!\s)[\w\ \-'._@+]+(?&lt;!\s)$</c>.
/// </summary>
public static partial class ProfileNaming
{
    /// <summary>Maximum profile name length.</summary>
    public const int MaxNameLength = 32;

    /// <summary>Validates and trims a profile display name.</summary>
    /// <param name="name">Input.</param>
    /// <returns>The clean name, or <c>null</c> if invalid.</returns>
    public static string? NormalizeDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var trimmed = WhitespaceRun().Replace(name.Trim(), " ");
        if (trimmed.Length > MaxNameLength)
        {
            return null;
        }

        foreach (var ch in trimmed)
        {
            if (char.IsControl(ch))
            {
                return null;
            }
        }

        return trimmed;
    }

    /// <summary>Builds a Jellyfin user name.</summary>
    /// <param name="ownerUserName">The owner's Jellyfin user name.</param>
    /// <param name="profileName">Profile name.</param>
    /// <param name="isTaken">Whether a name is taken (should be checked case-insensitively).</param>
    /// <returns>A unique user name.</returns>
    public static string BuildUserName(string ownerUserName, string profileName, Func<string, bool> isTaken)
    {
        ArgumentNullException.ThrowIfNull(isTaken);
        var owner = Sanitize(ownerUserName);
        var profile = Sanitize(profileName);
        if (owner.Length == 0)
        {
            owner = "holix";
        }

        if (profile.Length == 0)
        {
            profile = "profile";
        }

        var baseName = owner + "." + profile;
        if (!isTaken(baseName))
        {
            return baseName;
        }

        for (var i = 2; i < 1000; i++)
        {
            var candidate = baseName + "-" + i.ToString(CultureInfo.InvariantCulture);
            if (!isTaken(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Could not generate a unique user name.");
    }

    /// <summary>Whether the value is a valid Jellyfin user name.</summary>
    /// <param name="userName">Name.</param>
    /// <returns><c>true</c> if valid.</returns>
    public static bool IsValidJellyfinUserName(string userName) =>
        JellyfinUserName().IsMatch(userName) && userName is not "." and not "..";

    internal static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.Normalize(NormalizationForm.FormC))
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '-' or '\'' or '.' or '@' or '+')
            {
                sb.Append(ch);
            }
            else if (char.IsWhiteSpace(ch))
            {
                sb.Append('_');
            }
        }

        return sb.ToString().Trim('.', '_');
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"^(?!\s)[\w\ \-'._@+]+(?<!\s)$")]
    private static partial Regex JellyfinUserName();
}
