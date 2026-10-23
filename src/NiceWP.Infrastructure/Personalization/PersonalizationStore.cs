using System.Diagnostics;
using System.Text.Json;
using NiceWP.Core.Personalization;
using NiceWP.Infrastructure.Library;

namespace NiceWP.Infrastructure.Personalization;

/// <summary>
/// Manages thread-safe, atomic, and debounced local persistence for PersonalizationProfile
/// at %LocalAppData%\NiceWP\personalization.json.
/// Handles versioning, corrupt file recovery, and write amplification protection.
/// </summary>
public sealed class PersonalizationStore
{
    public const string DefaultFileName = "personalization.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _saveLock = new();
    private CancellationTokenSource? _debounceCts;
    private int _isDirty;

    public string FilePath { get; }
    public PersonalizationProfile Profile { get; private set; }
    public bool IsDirty => Volatile.Read(ref _isDirty) != 0;

    public PersonalizationStore(string? filePath = null)
    {
        FilePath = string.IsNullOrWhiteSpace(filePath)
            ? GetDefaultStorePath()
            : Path.GetFullPath(filePath);

        Profile = PersonalizationProfile.CreateDefault();
    }

    public static string GetDefaultStorePath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            WallpaperLibraryService.DefaultFolderName,
            DefaultFileName);

    /// <summary>
    /// Loads the persisted personalization profile from disk. If the file is missing or corrupted,
    /// a safe fallback default is initialized without crashing.
    /// </summary>
    public async Task<PersonalizationProfile> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(FilePath))
            {
                Profile = PersonalizationProfile.CreateDefault();
                Interlocked.Exchange(ref _isDirty, 0);
                return Profile;
            }

            try
            {
                await using var stream = new FileStream(
                    FilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    useAsync: true);

                var loaded = await JsonSerializer.DeserializeAsync<PersonalizationProfile>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);

                if (loaded != null)
                {
                    loaded.FeatureWeights?.ClampAll();
                    Profile = loaded;
                    Interlocked.Exchange(ref _isDirty, 0);
                    return Profile;
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                Trace.WriteLine($"[PersonalizationStore] Corrupted profile detected: {ex.Message}. Recovering safely...");
                RecoverCorruptedFile();
            }

            Profile = PersonalizationProfile.CreateDefault();
            Interlocked.Exchange(ref _isDirty, 0);
            return Profile;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Atomically persists the in-memory personalization profile to disk via temporary file swap.
    /// </summary>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Profile.UpdatedAtUtc = DateTimeOffset.UtcNow;
            Profile.FeatureWeights?.ClampAll();

            var tempPath = $"{FilePath}.{Guid.NewGuid():N}.tmp";

            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            await using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                JsonSerializer.Serialize(writer, Profile, JsonOptions);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, FilePath, overwrite: true);
            Interlocked.Exchange(ref _isDirty, 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _isDirty, 1);
            Trace.WriteLine($"[PersonalizationStore] Failed to save profile: {ex.Message}");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Debounces saving profile mutations to coalesce rapid updates and prevent disk write amplification.
    /// </summary>
    public void RequestSave(int delayMs = 1000)
    {
        Interlocked.Exchange(ref _isDirty, 1);
        lock (_saveLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delayMs, token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested)
                    {
                        await SaveAsync(token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[PersonalizationStore] Debounced save failed: {ex.Message}");
                }
            }, token);
        }
    }

    /// <summary>
    /// Resets all personalization data to default and saves immediately.
    /// Does NOT touch favorites, library items, or application settings.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        lock (_saveLock)
        {
            _debounceCts?.Cancel();
        }

        Profile = PersonalizationProfile.CreateDefault();
        Interlocked.Exchange(ref _isDirty, 1);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public void MarkDirty()
    {
        Interlocked.Exchange(ref _isDirty, 1);
    }

    private void RecoverCorruptedFile()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var backupPath = $"{FilePath}.corrupt.{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}.bak";
                File.Move(FilePath, backupPath, overwrite: true);
                Trace.WriteLine($"[PersonalizationStore] Corrupted profile quarantined to: {backupPath}");
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[PersonalizationStore] Failed to quarantine corrupt file: {ex.Message}");
        }
    }
}
