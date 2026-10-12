using NiceWP.Core;
using NiceWP.Rendering;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.UI;
using Xunit;

namespace NiceWP.Runtime.Tests;

public sealed class VideoProfileTests
{
    internal static string ResolveValidationVideoDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "NiceWallpaper.sln")))
            {
                return Path.Combine(current.FullName, ".system_generated", "video-validation");
            }

            current = current.Parent;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".system_generated", "video-validation"));
    }

    internal static async Task EnsureValidH264SampleVideoAsync(string filePath, TimeSpan duration, Color color)
    {
        if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
        {
            return;
        }

        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempFileName = $"{Path.GetFileNameWithoutExtension(filePath)}_{Guid.NewGuid():N}.mp4";
        var folder = await StorageFolder.GetFolderFromPathAsync(directory!);
        var tempFile = await folder.CreateFileAsync(tempFileName, CreationCollisionOption.ReplaceExisting);

        try
        {
            var clip = MediaClip.CreateFromColor(color, duration);
            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            var encodingProfile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.Qvga);
            var result = await composition.RenderToFileAsync(
                tempFile,
                MediaTrimmingPreference.Fast,
                encodingProfile);

            Assert.Equal(TranscodeFailureReason.None, result);
            File.Move(tempFile.Path, filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempFile.Path))
            {
                try { File.Delete(tempFile.Path); } catch { }
            }
        }
    }

    [Fact]
    public async Task ValidMp4H264VideosPassValidation()
    {
        var basePath = ResolveValidationVideoDirectory();
        var sample5 = Path.Combine(basePath, "sample-5s.mp4");
        var sample10 = Path.Combine(basePath, "sample-10s.mp4");

        await EnsureValidH264SampleVideoAsync(sample5, TimeSpan.FromSeconds(5), Color.FromArgb(255, 36, 98, 178));
        await EnsureValidH264SampleVideoAsync(sample10, TimeSpan.FromSeconds(10), Color.FromArgb(255, 42, 132, 110));

        Assert.True(File.Exists(sample5), $"sample5 not found: {sample5}");
        Assert.True(File.Exists(sample10), $"sample10 not found: {sample10}");

        var file5 = await StorageFile.GetFileFromPathAsync(sample5);
        var profile5 = await MediaEncodingProfile.CreateFromFileAsync(file5);
        var result5 = VideoWallpaperRenderer.ValidateProfile(profile5);
        Assert.True(result5.Success, result5.Message);

        var file10 = await StorageFile.GetFileFromPathAsync(sample10);
        var profile10 = await MediaEncodingProfile.CreateFromFileAsync(file10);
        var result10 = VideoWallpaperRenderer.ValidateProfile(profile10);
        Assert.True(result10.Success, result10.Message);
    }

    [Fact]
    public void RejectsNullProfile()
    {
        var result = VideoWallpaperRenderer.ValidateProfile(null);
        Assert.Equal(WallpaperError.RendererFailed, result.Error);
        Assert.Equal("The video metadata could not be read.", result.Message);
    }

    [Fact]
    public void RejectsNonMp4Container()
    {
        var profile = new MediaEncodingProfile
        {
            Container = new ContainerEncodingProperties { Subtype = "AVI" },
            Video = VideoEncodingProperties.CreateH264()
        };
        var result = VideoWallpaperRenderer.ValidateProfile(profile);
        Assert.Equal(WallpaperError.UnsupportedFormat, result.Error);
        Assert.Equal("The file container must be MP4.", result.Message);
    }

    [Fact]
    public void RejectsMissingVideoTrack()
    {
        var profile = new MediaEncodingProfile
        {
            Container = new ContainerEncodingProperties { Subtype = MediaEncodingSubtypes.Mpeg4 },
            Video = null
        };
        var result = VideoWallpaperRenderer.ValidateProfile(profile);
        Assert.Equal(WallpaperError.UnsupportedFormat, result.Error);
        Assert.Equal("The file contains no video track.", result.Message);
    }

    [Fact]
    public void RejectsUnsupportedVideoCodec()
    {
        var profile = new MediaEncodingProfile
        {
            Container = new ContainerEncodingProperties { Subtype = "MPEG4" },
            Video = new VideoEncodingProperties { Subtype = "HEVC" }
        };
        var result = VideoWallpaperRenderer.ValidateProfile(profile);
        Assert.Equal(WallpaperError.UnsupportedFormat, result.Error);
        Assert.Equal("The video codec must be H.264.", result.Message);
    }

    [Fact]
    public async Task CorruptOrEmptyFileFailsExplicitly()
    {
        var tempEmpty = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4");
        var tempCorrupt = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4");
        try
        {
            await File.WriteAllBytesAsync(tempEmpty, []);
            await File.WriteAllBytesAsync(tempCorrupt, [0x00, 0x00, 0x00, 0x20, 0x66, 0x74, 0x79, 0x70]);

            var fileEmpty = await StorageFile.GetFileFromPathAsync(tempEmpty);
            var exEmpty = await Record.ExceptionAsync(() => MediaEncodingProfile.CreateFromFileAsync(fileEmpty).AsTask());

            var fileCorrupt = await StorageFile.GetFileFromPathAsync(tempCorrupt);
            var exCorrupt = await Record.ExceptionAsync(() => MediaEncodingProfile.CreateFromFileAsync(fileCorrupt).AsTask());

            Assert.NotNull(exEmpty);
            Assert.NotNull(exCorrupt);
        }
        finally
        {
            if (File.Exists(tempEmpty)) File.Delete(tempEmpty);
            if (File.Exists(tempCorrupt)) File.Delete(tempCorrupt);
        }
    }
}
