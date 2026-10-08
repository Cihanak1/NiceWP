using Xunit;

namespace NiceWP.Core.Tests;

public sealed class VideoWallpaperTests
{
    [Theory]
    [InlineData(".mp4")]
    [InlineData(".MP4")]
    public void AcceptsExistingSupportedPaths(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + extension);
        try
        {
            File.WriteAllBytes(path, []);
            Assert.True(VideoWallpaper.Validate(path, out var normalized).Success);
            Assert.Equal(Path.GetFullPath(path), normalized);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("video.avi")]
    [InlineData("video.mkv")]
    [InlineData("video.mov")]
    [InlineData("video.webm")]
    [InlineData("video.gif")]
    public void RejectsUnsupportedExtensions(string path) =>
        Assert.Equal(WallpaperError.UnsupportedFormat, VideoWallpaper.Validate(path, out _).Error);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad\0.mp4")]
    public void RejectsInvalidPaths(string? path) =>
        Assert.Equal(WallpaperError.InvalidPath, VideoWallpaper.Validate(path, out _).Error);

    [Fact]
    public void RejectsMissingFile() => Assert.Equal(WallpaperError.FileNotFound,
        VideoWallpaper.Validate(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4"), out _).Error);
}
