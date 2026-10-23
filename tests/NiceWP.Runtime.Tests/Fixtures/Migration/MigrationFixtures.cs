namespace NiceWP.Runtime.Tests.Fixtures.Migration;

/// <summary>
/// Historical schema payloads from earlier versions of NiceWP (0.0.15 - 0.0.22)
/// used to verify forward-compatibility, safe defaults, and corrupt data recovery.
/// </summary>
public static class MigrationFixtures
{
    /// <summary>
    /// NiceWP 0.0.15 legacy settings format:
    /// Basic media controls, volume, launch on startup. Missing modern adaptive, smart scenes, and personalization.
    /// </summary>
    public const string Settings_v0_15 = """
    {
      "RestoreLastWallpaper": true,
      "LastWallpaperType": "Image",
      "LastWallpaperPath": "C:\\Wallpapers\\nature.jpg",
      "Theme": "Dark"
    }
    """;

    /// <summary>
    /// NiceWP 0.0.18 interactive rotation settings format:
    /// Adds corner drag thresholds, rotation mode, and interval.
    /// </summary>
    public const string Settings_v0_18 = """
    {
      "RestoreLastWallpaper": true,
      "LastWallpaperType": "Video",
      "LastWallpaperPath": "C:\\Wallpapers\\abstract.mp4",
      "RotationMode": "Sequential",
      "RotationIntervalMinutes": 15,
      "InteractiveTransitionEnabled": true
    }
    """;

    /// <summary>
    /// NiceWP 0.0.20 Discover & Online Library settings format:
    /// Adds provider toggles, safe search, and download directory overrides.
    /// </summary>
    public const string Settings_v0_20 = """
    {
      "RestoreLastWallpaper": true,
      "LastWallpaperType": "Image",
      "LastWallpaperPath": "C:\\Wallpapers\\openverse_lake.jpg",
      "RotationMode": "Smart",
      "RotationIntervalMinutes": 30,
      "InteractiveTransitionEnabled": true,
      "Language": "tr-TR",
      "OnlineOpenverseEnabled": true,
      "OnlineWikimediaEnabled": true,
      "OnlineNasaEnabled": true
    }
    """;

    /// <summary>
    /// NiceWP 0.0.22 Personalization Engine settings format:
    /// Includes Smart Scenes, learning toggles, and privacy controls.
    /// </summary>
    public const string Settings_v0_22 = """
    {
      "RestoreLastWallpaper": true,
      "LastWallpaperType": "Image",
      "LastWallpaperPath": "C:\\Wallpapers\\mountain.jpg",
      "RotationMode": "Smart",
      "RotationIntervalMinutes": 20,
      "InteractiveTransitionEnabled": true,
      "Language": "en-US",
      "SmartScenesEnabled": true,
      "SmartScene": "Automatic",
      "PersonalizationEnabled": true,
      "PersonalizationUseFavorites": true,
      "PersonalizationLearnManualSelections": true,
      "PersonalizationAvoidExcluded": true
    }
    """;

    /// <summary>
    /// Malformed/truncated settings JSON for corruption recovery testing.
    /// </summary>
    public const string Settings_Corrupt = """
    {
      "LastWallpaperPath": "C:\\Wallpapers\\broken.jpg",
      "BrokenField": { unterminated object
    """;

    /// <summary>
    /// NiceWP 0.0.15 legacy library.json format:
    /// Simple item entries without origin/provenance metadata.
    /// </summary>
    public const string Library_v0_15 = """
    {
      "Version": 1,
      "Items": [
        {
          "Id": "item-legacy-1",
          "FilePath": "C:\\Wallpapers\\forest.jpg",
          "DisplayName": "Forest Morning",
          "Type": "Image",
          "IsFavorite": false,
          "AddedAtUtc": "2026-09-01T10:00:00Z"
        },
        {
          "Id": "item-legacy-2",
          "FilePath": "C:\\Wallpapers\\ocean.mp4",
          "DisplayName": "Calm Waves",
          "Type": "Video",
          "IsFavorite": true,
          "AddedAtUtc": "2026-09-05T12:00:00Z"
        }
      ]
    }
    """;

    /// <summary>
    /// NiceWP 0.0.20 online provenance library format:
    /// Includes Origin with license code, source URL, author, and provider.
    /// </summary>
    public const string Library_v0_20 = """
    {
      "Version": 1,
      "Items": [
        {
          "Id": "item-online-1",
          "FilePath": "C:\\Wallpapers\\nebula.jpg",
          "DisplayName": "Carina Nebula",
          "Type": "Image",
          "IsFavorite": true,
          "AddedAtUtc": "2026-10-15T08:30:00Z",
          "Origin": {
            "Provider": "NASA",
            "RemoteId": "PIA12345",
            "OriginalUrl": "https://images.nasa.gov/details-PIA12345",
            "Creator": "NASA / JPL",
            "License": "Public Domain",
            "LicenseUrl": "https://www.nasa.gov/multimedia/guidelines/index.html"
          }
        }
      ]
    }
    """;

    /// <summary>
    /// Corrupt library JSON with syntax error.
    /// </summary>
    public const string Library_Corrupt = """
    {
      "Version": 1,
      "Items": [
        { "Id": "corrupt-1", "FilePath": "C:\\lost.jpg"
    """;

    /// <summary>
    /// Legacy wallpaper analysis store format with older metadata fields.
    /// </summary>
    public const string Analysis_Legacy = """
    {
      "Version": 1,
      "Entries": {
        "item-legacy-1": {
          "WallpaperId": "item-legacy-1",
          "Brightness": 0.35,
          "IsDark": true,
          "IsLight": false,
          "ColorTemperature": 2,
          "ResourceClass": 0,
          "MotionLevel": 0
        }
      }
    }
    """;
}
