using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NiceWP.Infrastructure.Settings;

/// <summary>
/// Provides resilient local JSON persistence for <see cref="AppSettings"/> at %LocalAppData%\NiceWP\settings.json.
/// </summary>
public sealed class SettingsService
{
    public const string DefaultFolderName = "NiceWP";
    public const string DefaultFileName = "settings.json";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim gate = new(1, 1);

    public string FilePath { get; }

    public SettingsService(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? GetDefaultSettingsPath()
            : Path.GetFullPath(filePath);
    }

    public static string GetDefaultSettingsPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            DefaultFolderName,
            DefaultFileName);

    /// <summary>
    /// Loads settings from disk. Returns safe defaults if the file is missing, empty, or corrupt.
    /// Never deletes or overwrites a corrupt file during load.
    /// </summary>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath))
            {
                return AppSettings.CreateDefault();
            }

            var json = await File.ReadAllTextAsync(FilePath, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return AppSettings.CreateDefault();
            }

            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return AppSettings.CreateDefault();
            }

            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return loaded?.Normalize() ?? AppSettings.CreateDefault();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to load settings from '{FilePath}': {ex}");
            return AppSettings.CreateDefault();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Writes settings atomically via a temporary file in the same directory, flushing before replacing the target file.
    /// </summary>
    public async Task<bool> SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? tempFilePath = null;
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                Trace.WriteLine($"Cannot save settings: invalid directory for path '{FilePath}'.");
                return false;
            }

            Directory.CreateDirectory(directory);

            var normalized = settings.Normalize();
            var json = JsonSerializer.Serialize(normalized, JsonOptions);
            var bytes = Utf8NoBom.GetBytes(json);

            tempFilePath = Path.Combine(directory, $"{Path.GetFileName(FilePath)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                tempFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempFilePath, FilePath, overwrite: true);
            tempFilePath = null;
            return true;
        }
        catch (OperationCanceledException)
        {
            CleanupTempFile(tempFilePath);
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Failed to save settings to '{FilePath}': {ex}");
            CleanupTempFile(tempFilePath);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Resets settings to safe defaults and persists them to disk.
    /// </summary>
    public Task<bool> ResetAsync(CancellationToken cancellationToken = default) =>
        SaveAsync(AppSettings.CreateDefault(), cancellationToken);

    private static void CleanupTempFile(string? tempFilePath)
    {
        if (string.IsNullOrWhiteSpace(tempFilePath))
        {
            return;
        }

        try
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
        catch (Exception cleanupEx)
        {
            Trace.WriteLine($"Failed to clean up temporary settings file '{tempFilePath}': {cleanupEx}");
        }
    }
}
