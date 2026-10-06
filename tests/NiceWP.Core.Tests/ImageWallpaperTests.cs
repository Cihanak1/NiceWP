using Xunit;

namespace NiceWP.Core.Tests;

public sealed class ImageWallpaperTests
{
    [Theory]
    [InlineData(".png")]
    [InlineData(".JPG")]
    [InlineData(".jpeg")]
    public void AcceptsExistingSupportedPaths(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + extension);
        try
        {
            File.WriteAllBytes(path, []);
            Assert.True(ImageWallpaper.Validate(path, out var normalized).Success);
            Assert.Equal(Path.GetFullPath(path), normalized);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("image.gif")]
    [InlineData("image.webp")]
    [InlineData("image.svg")]
    [InlineData("image.mp4")]
    public void RejectsUnsupportedExtensions(string path) =>
        Assert.Equal(WallpaperError.UnsupportedFormat, ImageWallpaper.Validate(path, out _).Error);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad\0.png")]
    public void RejectsInvalidPaths(string? path) =>
        Assert.Equal(WallpaperError.InvalidPath, ImageWallpaper.Validate(path, out _).Error);

    [Fact]
    public void RejectsMissingFile() => Assert.Equal(WallpaperError.FileNotFound,
        ImageWallpaper.Validate(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png"), out _).Error);
}
