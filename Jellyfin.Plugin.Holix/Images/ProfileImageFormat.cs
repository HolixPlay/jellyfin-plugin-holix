using System;

namespace Jellyfin.Plugin.Holix.Images;

/// <summary>
/// Profile image format checks (pure). The client sends the photo it uploaded to holix-cloud (the cloud
/// produces a 512×512 JPEG) or a PNG rendering of a stock avatar; only JPEG and PNG are accepted, and the
/// type is determined from the bytes, not from <c>Content-Type</c>.
/// </summary>
public static class ProfileImageFormat
{
    /// <summary>The maximum body size (same as holix-cloud's photo limit).</summary>
    public const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>Detects the format from the bytes.</summary>
    /// <param name="data">The image bytes.</param>
    /// <returns>The MIME type and file extension; <c>null</c> if unrecognized.</returns>
    public static (string MimeType, string Extension)? Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return ("image/jpeg", ".jpg");
        }

        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (data.Length >= png.Length && data[..png.Length].SequenceEqual(png))
        {
            return ("image/png", ".png");
        }

        return null;
    }

    /// <summary>Whether the client-declared type is acceptable (if missing or generic binary, the bytes decide).</summary>
    /// <param name="contentType">The request <c>Content-Type</c> header.</param>
    /// <returns><c>true</c> if acceptable.</returns>
    public static bool AcceptsContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return true;
        }

        var media = contentType.Split(';', 2)[0].Trim();
        return media.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)
            || media.Equals("image/png", StringComparison.OrdinalIgnoreCase)
            || media.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase);
    }
}
