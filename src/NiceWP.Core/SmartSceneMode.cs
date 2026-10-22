namespace NiceWP.Core;

/// <summary>
/// Defines the active Smart Scene behavioral modes that guide wallpaper selection based on desktop context.
/// </summary>
public enum SmartSceneMode
{
    Off = 0,
    Automatic = 1,
    Focus = 2,
    Gaming = 3,
    Relax = 4,
    Night = 5,
    BatterySaver = 6
}

public static class SmartSceneModeExtensions
{
    public const string SceneOff = "Off";
    public const string SceneAutomatic = "Automatic";
    public const string SceneFocus = "Focus";
    public const string SceneGaming = "Gaming";
    public const string SceneRelax = "Relax";
    public const string SceneNight = "Night";
    public const string SceneBatterySaver = "BatterySaver";

    public static string ToConfigString(this SmartSceneMode mode) => mode switch
    {
        SmartSceneMode.Automatic => SceneAutomatic,
        SmartSceneMode.Focus => SceneFocus,
        SmartSceneMode.Gaming => SceneGaming,
        SmartSceneMode.Relax => SceneRelax,
        SmartSceneMode.Night => SceneNight,
        SmartSceneMode.BatterySaver => SceneBatterySaver,
        _ => SceneOff
    };

    public static string ToDisplayString(this SmartSceneMode mode, bool isTurkish = false) => mode switch
    {
        SmartSceneMode.Automatic => isTurkish ? "Otomatik" : "Automatic",
        SmartSceneMode.Focus => isTurkish ? "Odak" : "Focus",
        SmartSceneMode.Gaming => isTurkish ? "Oyun" : "Gaming",
        SmartSceneMode.Relax => isTurkish ? "Rahatlama" : "Relax",
        SmartSceneMode.Night => isTurkish ? "Gece" : "Night",
        SmartSceneMode.BatterySaver => isTurkish ? "Pil Tasarrufu" : "Battery Saver",
        _ => isTurkish ? "Kapalı" : "Off"
    };

    public static SmartSceneMode ParseMode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return SmartSceneMode.Automatic;

        var trimmed = value.Trim();
        if (string.Equals(trimmed, SceneOff, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Kapalı", StringComparison.OrdinalIgnoreCase))
        {
            return SmartSceneMode.Off;
        }

        if (string.Equals(trimmed, SceneFocus, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Odak", StringComparison.OrdinalIgnoreCase))
        {
            return SmartSceneMode.Focus;
        }

        if (string.Equals(trimmed, SceneGaming, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Oyun", StringComparison.OrdinalIgnoreCase))
        {
            return SmartSceneMode.Gaming;
        }

        if (string.Equals(trimmed, SceneRelax, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Rahatlama", StringComparison.OrdinalIgnoreCase))
        {
            return SmartSceneMode.Relax;
        }

        if (string.Equals(trimmed, SceneNight, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Gece", StringComparison.OrdinalIgnoreCase))
        {
            return SmartSceneMode.Night;
        }

        if (string.Equals(trimmed, SceneBatterySaver, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Battery Saver", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, "Pil Tasarrufu", StringComparison.OrdinalIgnoreCase))
        {
            return SmartSceneMode.BatterySaver;
        }

        return SmartSceneMode.Automatic;
    }
}
