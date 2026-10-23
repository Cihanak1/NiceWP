using NiceWP.Core;
using NiceWP.Core.Personalization;
using NiceWP.Infrastructure.Library;

namespace NiceWP.Infrastructure.Personalization;

/// <summary>
/// Computes bounded, deterministic, and explainable personalization score adjustments
/// for candidate wallpapers based on explicit user feedback and learned feature tendencies.
/// </summary>
public static class WallpaperPersonalizationScorer
{
    public const double MaxContribution = 20.0;
    public const double MinContribution = -20.0;

    /// <summary>
    /// Evaluates personalization contribution for a candidate wallpaper.
    /// If personalization is disabled, returns empty zero score immediately.
    /// </summary>
    public static PersonalizationScoreResult Score(
        WallpaperLibraryItem wallpaper,
        WallpaperAnalysisMetadata? metadata,
        PersonalizationProfile? profile,
        DateTimeOffset? nowUtc = null)
    {
        if (wallpaper == null || profile == null || !profile.IsEnabled)
        {
            return PersonalizationScoreResult.Empty;
        }

        var now = nowUtc ?? DateTimeOffset.UtcNow;
        var reasonsEn = new List<string>(2);
        var reasonsTr = new List<string>(2);

        // 1. Direct per-wallpaper preference
        double wallpaperPref = 0.0;
        if (!string.IsNullOrWhiteSpace(wallpaper.Id) &&
            profile.WallpaperPreferences.TryGetValue(wallpaper.Id, out var record) &&
            record != null)
        {
            double decayed = PersonalizationDecayPolicy.GetDecayedWeight(record.Weight, record.LastInteractedAtUtc, now);
            wallpaperPref = Math.Round(decayed * 10.0, 1); // Up to ±10.0 pts

            if (decayed >= 0.25)
            {
                reasonsEn.Add("Previously marked positive");
                reasonsTr.Add("Daha önce olumlu işaretlendi");
            }
            else if (decayed <= -0.25)
            {
                reasonsEn.Add("Marked less preferred");
                reasonsTr.Add("Daha az tercih edildi");
            }
        }

        var fw = profile.FeatureWeights;
        double mediaPref = 0.0;
        double brightnessPref = 0.0;
        double motionPref = 0.0;
        double colorPref = 0.0;
        double resourcePref = 0.0;

        if (fw != null)
        {
            // 2. Media Type (Static vs Video)
            bool isVideo = string.Equals(wallpaper.Type, "video", StringComparison.OrdinalIgnoreCase);
            double mediaWeight = isVideo ? fw.VideoPreference : fw.StaticPreference;
            mediaPref = Math.Round(mediaWeight * 3.0, 1);

            if (metadata != null)
            {
                // 3. Brightness Tendency
                if (metadata.IsDark)
                {
                    brightnessPref = Math.Round(fw.DarkPreference * 3.0, 1);
                    if (fw.DarkPreference >= 0.35)
                    {
                        reasonsEn.Add("Dark style preferred");
                        reasonsTr.Add("Koyu stil tercihi");
                    }
                }
                else if (metadata.IsLight)
                {
                    brightnessPref = Math.Round(fw.LightPreference * 3.0, 1);
                    if (fw.LightPreference >= 0.35)
                    {
                        reasonsEn.Add("Bright style preferred");
                        reasonsTr.Add("Açık stil tercihi");
                    }
                }

                // 4. Motion Level Tendency
                if (metadata.MotionLevel is WallpaperMotionLevel.None or WallpaperMotionLevel.Low)
                {
                    motionPref = Math.Round(fw.LowMotionPreference * 3.0, 1);
                }
                else if (metadata.MotionLevel == WallpaperMotionLevel.High)
                {
                    motionPref = Math.Round(fw.HighMotionPreference * 3.0, 1);
                }

                // 5. Color Temperature Tendency
                double colorWeight = metadata.ColorTemperature switch
                {
                    WallpaperColorTemperature.Warm => fw.WarmPreference,
                    WallpaperColorTemperature.Cool => fw.CoolPreference,
                    _ => fw.NeutralPreference
                };
                colorPref = Math.Round(colorWeight * 2.0, 1);

                // 6. Resource Class Tendency
                double resourceWeight = metadata.ResourceClass switch
                {
                    WallpaperCost.Light => fw.LightResourcePreference,
                    WallpaperCost.Heavy => fw.HeavyResourcePreference,
                    _ => fw.ModerateResourcePreference
                };
                resourcePref = Math.Round(resourceWeight * 3.0, 1);
            }
        }

        double rawTotal = wallpaperPref + mediaPref + brightnessPref + motionPref + colorPref + resourcePref;
        double totalClamped = Math.Round(Math.Clamp(rawTotal, MinContribution, MaxContribution), 1);

        if (totalClamped >= 4.0 && reasonsEn.Count == 0)
        {
            reasonsEn.Add("Matches your preferences");
            reasonsTr.Add("Tercihlerinize uygun");
        }

        return new PersonalizationScoreResult
        {
            Total = totalClamped,
            WallpaperPreference = wallpaperPref,
            MediaPreference = mediaPref,
            BrightnessPreference = brightnessPref,
            MotionPreference = motionPref,
            ColorPreference = colorPref,
            ResourcePreference = resourcePref,
            ReasonsEn = reasonsEn,
            ReasonsTr = reasonsTr
        };
    }
}
