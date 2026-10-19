using System.Diagnostics;

namespace NiceWP.Infrastructure.Library;

/// <summary>
/// Scans a local directory for supported wallpaper image and video files (.png, .jpg, .jpeg, .mp4).
/// Protects against symlink and directory junction infinite loops by skipping reparse points.
/// </summary>
public static class WallpaperFolderScanner
{
    public static readonly IReadOnlySet<string> SupportedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".mp4"
    };

    public const int DefaultMaxDepth = 16;
    public const int DefaultMaxFiles = 5000;

    /// <summary>
    /// Scans the given directory for supported wallpaper files.
    /// </summary>
    /// <param name="folderPath">The folder to scan.</param>
    /// <param name="recursive">Whether to scan subdirectories.</param>
    /// <param name="maxDepth">Maximum subdirectory depth when recursive is true.</param>
    /// <param name="maxFiles">Maximum total files to collect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of verified full paths to supported wallpaper files.</returns>
    public static IReadOnlyList<string> Scan(
        string? folderPath,
        bool recursive = false,
        int maxDepth = DefaultMaxDepth,
        int maxFiles = DefaultMaxFiles,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return Array.Empty<string>();
        }

        var fullRootPath = Path.GetFullPath(folderPath);
        var results = new List<string>();
        var seenCanonicalDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!recursive)
        {
            try
            {
                var files = Directory.EnumerateFiles(fullRootPath);
                foreach (var file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ext = Path.GetExtension(file);
                    if (SupportedExtensions.Contains(ext))
                    {
                        results.Add(Path.GetFullPath(file));
                        if (results.Count >= maxFiles)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Error scanning folder '{fullRootPath}': {ex.Message}");
            }

            return results;
        }

        // Recursive scan with junction and symlink loop protection
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((fullRootPath, 0));
        seenCanonicalDirs.Add(fullRootPath);

        while (queue.Count > 0 && results.Count < maxFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (currentDir, currentDepth) = queue.Dequeue();

            try
            {
                // Enumerate files in current directory
                foreach (var file in Directory.EnumerateFiles(currentDir))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ext = Path.GetExtension(file);
                    if (SupportedExtensions.Contains(ext))
                    {
                        results.Add(Path.GetFullPath(file));
                        if (results.Count >= maxFiles)
                        {
                            break;
                        }
                    }
                }

                if (results.Count >= maxFiles || currentDepth >= maxDepth)
                {
                    continue;
                }

                // Enumerate subdirectories, skipping reparse points (symlinks / junctions)
                foreach (var subDir in Directory.EnumerateDirectories(currentDir))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        var dirInfo = new DirectoryInfo(subDir);
                        // Loop protection: skip reparse points (junctions, symlinks)
                        if ((dirInfo.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            Trace.WriteLine($"Skipping reparse point / junction: {subDir}");
                            continue;
                        }

                        var canonicalSubDir = Path.GetFullPath(subDir);
                        if (seenCanonicalDirs.Add(canonicalSubDir))
                        {
                            queue.Enqueue((canonicalSubDir, currentDepth + 1));
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        Trace.WriteLine($"Cannot access subdirectory '{subDir}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Trace.WriteLine($"Cannot access directory '{currentDir}': {ex.Message}");
            }
        }

        return results;
    }
}
