using System.Diagnostics;
using NiceWP.Infrastructure.Ipc;

namespace NiceWP.Infrastructure.Remote;

/// <summary>
/// Specifies the detected binary media format.
/// </summary>
public enum DetectedMediaType
{
    Unknown,
    Png,
    Jpeg,
    Mp4
}

/// <summary>
/// Inspects file byte signatures (magic bytes) to accurately verify media content
/// independently of server-provided Content-Type or file extensions.
/// </summary>
public static class MediaSignatureDetector
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0xFF];
    private static readonly byte[] FtypMagic = [(byte)'f', (byte)'t', (byte)'y', (byte)'p'];

    /// <summary>
    /// Reads the initial bytes of the file and determines if it is a valid, supported media file.
    /// Rejects executables, ELF binaries, HTML pages, XML, and scripts immediately.
    /// </summary>
    public static DetectedMediaType Detect(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return DetectedMediaType.Unknown;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length < 8)
            {
                return DetectedMediaType.Unknown;
            }

            var header = new byte[32];
            var bytesRead = stream.Read(header, 0, header.Length);
            if (bytesRead < 8)
            {
                return DetectedMediaType.Unknown;
            }

            // Dangerous headers check:
            // MZ (Windows PE Executable)
            if (header[0] == 0x4D && header[1] == 0x5A)
            {
                return DetectedMediaType.Unknown;
            }

            // ELF (Linux binary)
            if (header[0] == 0x7F && header[1] == 0x45 && header[2] == 0x4C && header[3] == 0x46)
            {
                return DetectedMediaType.Unknown;
            }

            // HTML / XML / Script check
            if (header[0] == (byte)'<' && bytesRead >= 4)
            {
                var prefix = System.Text.Encoding.ASCII.GetString(header, 0, Math.Min(bytesRead, 16)).ToLowerInvariant();
                if (prefix.StartsWith("<!do") || prefix.StartsWith("<htm") || prefix.StartsWith("<?xm") || prefix.StartsWith("<scr"))
                {
                    return DetectedMediaType.Unknown;
                }
            }

            // PNG check
            if (bytesRead >= 8 && header.AsSpan(0, 8).SequenceEqual(PngMagic))
            {
                return DetectedMediaType.Png;
            }

            // JPEG check
            if (bytesRead >= 3 && header.AsSpan(0, 3).SequenceEqual(JpegMagic))
            {
                return DetectedMediaType.Jpeg;
            }

            // MP4 check: bytes 4..7 == "ftyp"
            if (bytesRead >= 8 && header.AsSpan(4, 4).SequenceEqual(FtypMagic))
            {
                return DetectedMediaType.Mp4;
            }

            return DetectedMediaType.Unknown;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Media signature detection failed: {ex}");
            return DetectedMediaType.Unknown;
        }
    }

    public static string GetExtension(DetectedMediaType mediaType) => mediaType switch
    {
        DetectedMediaType.Png => ".png",
        DetectedMediaType.Jpeg => ".jpg",
        DetectedMediaType.Mp4 => ".mp4",
        _ => string.Empty
    };

    public static string GetWallpaperType(DetectedMediaType mediaType) => mediaType switch
    {
        DetectedMediaType.Png or DetectedMediaType.Jpeg => IpcConstants.WallpaperTypeImage,
        DetectedMediaType.Mp4 => IpcConstants.WallpaperTypeVideo,
        _ => IpcConstants.WallpaperTypeNone
    };
}
