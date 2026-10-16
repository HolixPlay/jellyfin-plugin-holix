using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Holix.Api;
using Jellyfin.Plugin.Holix.Households;
using Jellyfin.Plugin.Holix.Images;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Holix.Tests;

public class ProfileImageTests
{
    [Fact]
    public void Jpeg_and_png_are_detected_from_bytes()
    {
        Assert.Equal(("image/jpeg", ".jpg"), ProfileImageFormat.Detect([0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10]));
        Assert.Equal(("image/png", ".png"), ProfileImageFormat.Detect([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0]));
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0xFF, 0xD8 })]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })] // GIF
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 })] // WebP
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67 })] // <svg
    public void Other_formats_are_rejected(byte[] data)
    {
        Assert.Null(ProfileImageFormat.Detect(data));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("image/jpeg", true)]
    [InlineData("IMAGE/PNG", true)]
    [InlineData("application/octet-stream", true)]
    [InlineData("image/png; charset=binary", true)]
    [InlineData("image/svg+xml", false)]
    [InlineData("application/json", false)]
    public void Content_type(string? value, bool ok)
    {
        Assert.Equal(ok, ProfileImageFormat.AcceptsContentType(value));
    }

    [Fact]
    public async Task Body_is_read_up_to_limit_413_when_exceeded()
    {
        var ok = await HolixController.ReadLimitedAsync(new MemoryStream(new byte[1000]), 1000, CancellationToken.None);
        Assert.Equal(1000, ok.Length);
        var ex = await Assert.ThrowsAsync<HolixException>(() => HolixController.ReadLimitedAsync(new MemoryStream(new byte[1001]), 1000, CancellationToken.None));
        Assert.Equal(413, ex.Status);
        Assert.Equal("image_too_large", ex.Code);
    }

    [Fact]
    public void Endpoints_are_defined()
    {
        var put = typeof(HolixController).GetMethod(nameof(HolixController.SetProfileImage))!;
        Assert.Equal("Profiles/{id}/Image", put.GetCustomAttribute<HttpPutAttribute>()!.Template);
        Assert.NotNull(put.GetCustomAttribute<AuthorizeAttribute>());
        Assert.Equal(typeof(Task<ActionResult<ProfileDto>>), put.ReturnType);
        var del = typeof(HolixController).GetMethod(nameof(HolixController.DeleteProfileImage))!;
        Assert.Equal("Profiles/{id}/Image", del.GetCustomAttribute<HttpDeleteAttribute>()!.Template);

        // The link response has the same body as GET /Holix/Household (HouseholdDto).
        var link = typeof(HolixController).GetMethod(nameof(HolixController.LinkHousehold))!;
        Assert.Equal(typeof(Task<ActionResult<HouseholdDto>>), link.ReturnType);
        Assert.Contains(typeof(HouseholdService).GetMethods(), m => m.Name == nameof(HouseholdService.SetProfileImageAsync));
    }

    [Fact]
    public void User_image_directory_stays_under_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "holix-users-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "alex"), HouseholdService.UserImageDirectory(root, "alex"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("a/../..")]
    [InlineData("")]
    public void User_image_directory_rejects_traversal(string userName)
    {
        var root = Path.Combine(Path.GetTempPath(), "holix-users-" + Guid.NewGuid().ToString("N"));
        var ex = Assert.Throws<HolixException>(() => HouseholdService.UserImageDirectory(root, userName));
        Assert.Equal("invalid_user_name", ex.Code);
    }
}
