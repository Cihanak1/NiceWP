using NiceWP.Core.Personalization;
using NiceWP.Infrastructure.Personalization;
using NiceWP.Core;
using NiceWP.Infrastructure.Library;

namespace NiceWP.Infrastructure.SmartScenes;

/// <summary>
/// Evaluates and scores wallpaper candidates against an active Smart Scene context
/// with deterministic rules, resource sensitivity, favorite weighting, and recent history penalties.
/// </summary>
public static class WallpaperSceneScorer
{
    public const double MaxTotalScore = 100.0;
    public const double MinTotalScore = 0.0;
    public const double BaseScore = 50.0;

    /// <summary>
    /// Computes a deterministic score (0 - 100) and rationale for a candidate wallpaper.
    /// </summary>
    public static WallpaperSceneScore Score(
        WallpaperLibraryItem wallpaper,
        WallpaperAnalysisMetadata? metadata,
        SmartSceneContext context,
        PersonalizationProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        ArgumentNullException.ThrowIfNull(context);

        var scene = context.EffectiveScene;
        if (scene == SmartSceneMode.Off)
        {
            return CreateNeutralScore(wallpaper, context, profile);
        }

        bool isImage = string.Equals(wallpaper.Type, "Image", StringComparison.OrdinalIgnoreCase);
        var cost = metadata?.ResourceClass ?? (isImage ? WallpaperCost.Light : WallpaperCost.Moderate);
        var motion = metadata?.MotionLevel ?? (isImage ? WallpaperMotionLevel.None : WallpaperMotionLevel.Medium);
        double brightness = metadata?.Brightness ?? 0.5;
        var temp = metadata?.ColorTemperature ?? WallpaperColorTemperature.Neutral;

        double resourceScore = 0.0;
        double brightnessScore = 0.0;
        double motionScore = 0.0;
        double sceneMatchScore = 0.0;

        var reasonsEn = new List<string>();
        var reasonsTr = new List<string>();

        switch (scene)
        {
            case SmartSceneMode.Focus:
                // Focus: Dark/medium brightness, low motion, light/moderate resource
                if (brightness < 0.40)
                {
                    brightnessScore += 18.0;
                    reasonsEn.Add("Dark");
                    reasonsTr.Add("Koyu");
                }
                else if (brightness <= 0.65)
                {
                    brightnessScore += 10.0;
                }
                else
                {
                    brightnessScore -= 15.0; // High brightness distracts
                }

                if (motion is WallpaperMotionLevel.None or WallpaperMotionLevel.Low)
                {
                    motionScore += 20.0;
                    reasonsEn.Add("Low motion");
                    reasonsTr.Add("Düşük hareket");
                }
                else if (motion == WallpaperMotionLevel.High)
                {
                    motionScore -= 25.0;
                }

                if (cost == WallpaperCost.Light)
                {
                    resourceScore += 15.0;
                    reasonsEn.Add("Light resource usage");
                    reasonsTr.Add("Hafif kaynak kullanımı");
                }
                else if (cost == WallpaperCost.Heavy)
                {
                    resourceScore -= 20.0;
                }

                sceneMatchScore = 10.0;
                break;

            case SmartSceneMode.Gaming:
                // Gaming: Light resource preferred, static preferred, low motion, heavy penalized
                if (cost == WallpaperCost.Light)
                {
                    resourceScore += 25.0;
                    reasonsEn.Add("Light resource usage");
                    reasonsTr.Add("Hafif kaynak kullanımı");
                }
                else if (cost == WallpaperCost.Heavy)
                {
                    resourceScore -= 50.0; // Heavy video destroys gaming frame times
                }

                if (isImage)
                {
                    sceneMatchScore += 15.0;
                    reasonsEn.Add("Static");
                    reasonsTr.Add("Statik");
                }

                if (motion is WallpaperMotionLevel.None or WallpaperMotionLevel.Low)
                {
                    motionScore += 15.0;
                }
                else if (motion == WallpaperMotionLevel.High)
                {
                    motionScore -= 30.0;
                }
                break;

            case SmartSceneMode.Relax:
                // Relax: Warm/neutral, medium brightness, static or low/medium motion
                if (temp is WallpaperColorTemperature.Warm or WallpaperColorTemperature.Neutral)
                {
                    sceneMatchScore += 15.0;
                    reasonsEn.Add(temp == WallpaperColorTemperature.Warm ? "Warm tone" : "Neutral tone");
                    reasonsTr.Add(temp == WallpaperColorTemperature.Warm ? "Sıcak ton" : "Dengeli ton");
                }

                if (brightness is >= 0.25 and <= 0.70)
                {
                    brightnessScore += 15.0;
                }

                if (motion is WallpaperMotionLevel.None or WallpaperMotionLevel.Low or WallpaperMotionLevel.Medium)
                {
                    motionScore += 12.0;
                    reasonsEn.Add("Calm motion");
                    reasonsTr.Add("Sakin hareket");
                }
                else
                {
                    motionScore -= 20.0;
                }

                if (cost != WallpaperCost.Heavy)
                {
                    resourceScore += 8.0;
                }
                break;

            case SmartSceneMode.Night:
                // Night: Dark, low brightness, low motion, avoid heavy
                if (brightness < 0.35)
                {
                    brightnessScore += 30.0;
                    reasonsEn.Add("Dark");
                    reasonsTr.Add("Koyu");
                }
                else if (brightness < 0.50)
                {
                    brightnessScore += 10.0;
                }
                else
                {
                    brightnessScore -= 30.0; // Avoid glaring bright screens at night
                }

                if (motion is WallpaperMotionLevel.None or WallpaperMotionLevel.Low)
                {
                    motionScore += 15.0;
                    reasonsEn.Add("Low motion");
                    reasonsTr.Add("Düşük hareket");
                }
                else
                {
                    motionScore -= 20.0;
                }

                if (cost == WallpaperCost.Light)
                {
                    resourceScore += 10.0;
                }
                else if (cost == WallpaperCost.Heavy)
                {
                    resourceScore -= 30.0;
                }
                break;

            case SmartSceneMode.BatterySaver:
                // Battery Saver: Static, light resource, low motion, heavy strongly excluded
                if (isImage)
                {
                    sceneMatchScore += 35.0;
                    reasonsEn.Add("Static");
                    reasonsTr.Add("Statik");
                }
                else
                {
                    sceneMatchScore -= 30.0; // Videos consume decoder power
                }

                if (cost == WallpaperCost.Light)
                {
                    resourceScore += 25.0;
                    reasonsEn.Add("Light resource usage");
                    reasonsTr.Add("Hafif kaynak kullanımı");
                }
                else if (cost == WallpaperCost.Heavy)
                {
                    resourceScore -= 70.0; // Never run heavy video on battery saver
                }

                if (motion is WallpaperMotionLevel.None or WallpaperMotionLevel.Low)
                {
                    motionScore += 10.0;
                }
                else
                {
                    motionScore -= 35.0;
                }
                break;

            default:
                sceneMatchScore = 10.0;
                break;
        }

        // Personalization contribution
        double personalizationScore = 0.0;
        if (profile != null && profile.IsEnabled)
        {
            var pResult = WallpaperPersonalizationScorer.Score(wallpaper, metadata, profile);
            personalizationScore = pResult.Total;

            // Enforce Scene Priority Over Personalization (Hard constraints override preferences)
            if (scene == SmartSceneMode.BatterySaver && (cost == WallpaperCost.Heavy || isVideo))
            {
                personalizationScore = Math.Min(0.0, personalizationScore);
            }
            else if (scene == SmartSceneMode.Gaming && cost == WallpaperCost.Heavy)
            {
                personalizationScore = Math.Min(0.0, personalizationScore);
            }
            else if (scene == SmartSceneMode.Focus && motion == WallpaperMotionLevel.High)
            {
                personalizationScore = Math.Min(0.0, personalizationScore);
            }
            else if (scene == SmartSceneMode.Night && metadata?.IsLight == true)
            {
                personalizationScore = Math.Min(0.0, personalizationScore);
            }

            if (personalizationScore >= 3.0 && pResult.ReasonsEn.Count > 0)
            {
                reasonsEn.AddRange(pResult.ReasonsEn);
                reasonsTr.AddRange(pResult.ReasonsTr);
            }
        }

        // Favorite bonus
        double favoriteBonus = 0.0;
        if (wallpaper.IsFavorite && context.PreferFavorites)
        {
            favoriteBonus = 10.0;
            reasonsEn.Add("Favorite");
            reasonsTr.Add("Favori");
        }

        // Recent history penalty
        double recentPenalty = 0.0;
        double diversityScore = 0.0;
        if (context.AvoidRecent && context.RecentWallpaperIds != null && context.RecentWallpaperIds.Count > 0)
        {
            int recentIndex = -1;
            for (int i = 0; i < context.RecentWallpaperIds.Count; i++)
            {
                if (string.Equals(context.RecentWallpaperIds[i], wallpaper.Id, StringComparison.OrdinalIgnoreCase))
                {
                    recentIndex = i;
                    break;
                }
            }

            if (recentIndex >= 0)
            {
                recentPenalty = recentIndex switch
                {
                    0 => 35.0, // Immediately preceding wallpaper
                    1 => 25.0,
                    2 => 15.0,
                    3 => 10.0,
                    _ => 5.0
                };
            }
            else
            {
                diversityScore = 8.0; // Bonus for wallpapers not seen recently
            }
        }

        double total = BaseScore + sceneMatchScore + resourceScore + brightnessScore + motionScore + diversityScore + favoriteBonus + personalizationScore - recentPenalty;
        double clampedTotal = Math.Round(Math.Clamp(total, MinTotalScore, MaxTotalScore), 1);

        if (reasonsEn.Count == 0)
        {
            reasonsEn.Add("Balanced candidate");
            reasonsTr.Add("Dengeli seçim");
        }

        return new WallpaperSceneScore
        {
            TotalScore = clampedTotal,
            SceneMatchScore = Math.Round(sceneMatchScore, 1),
            ResourceScore = Math.Round(resourceScore, 1),
            BrightnessScore = Math.Round(brightnessScore, 1),
            MotionScore = Math.Round(motionScore, 1),
            DiversityScore = Math.Round(diversityScore, 1),
            FavoriteBonus = Math.Round(favoriteBonus, 1),
            PersonalizationScore = Math.Round(personalizationScore, 1),
            RecentPenalty = Math.Round(recentPenalty, 1),
            ReasonSummary = string.Join(" · ", reasonsEn),
            ReasonSummaryTr = string.Join(" · ", reasonsTr)
        };
    }

    private static WallpaperSceneScore CreateNeutralScore(
        WallpaperLibraryItem wallpaper,
        SmartSceneContext context,
        PersonalizationProfile? profile = null)
    {
        double favoriteBonus = (wallpaper.IsFavorite && context.PreferFavorites) ? 10.0 : 0.0;
        double personalizationScore = 0.0;
        var reasonsEn = new List<string> { "Standard selection" };
        var reasonsTr = new List<string> { "Standart seçim" };

        if (profile != null && profile.IsEnabled)
        {
            var pResult = WallpaperPersonalizationScorer.Score(wallpaper, null, profile);
            personalizationScore = pResult.Total;
            if (personalizationScore >= 4.0 && pResult.ReasonsEn.Count > 0)
            {
                reasonsEn.AddRange(pResult.ReasonsEn);
                reasonsTr.AddRange(pResult.ReasonsTr);
            }
        }

        double recentPenalty = 0.0;

        if (context.AvoidRecent && context.RecentWallpaperIds != null && context.RecentWallpaperIds.Count > 0)
        {
            int idx = -1;
            for (int i = 0; i < context.RecentWallpaperIds.Count; i++)
            {
                if (string.Equals(context.RecentWallpaperIds[i], wallpaper.Id, StringComparison.OrdinalIgnoreCase))
                {
                    idx = i;
                    break;
                }
            }

            if (idx == 0) recentPenalty = 25.0;
            else if (idx == 1) recentPenalty = 15.0;
            else if (idx >= 2) recentPenalty = 5.0;
        }

        double total = Math.Clamp(BaseScore + favoriteBonus + personalizationScore - recentPenalty, MinTotalScore, MaxTotalScore);
        return new WallpaperSceneScore
        {
            TotalScore = Math.Round(total, 1),
            FavoriteBonus = favoriteBonus,
            PersonalizationScore = Math.Round(personalizationScore, 1),
            RecentPenalty = recentPenalty,
            ReasonSummary = string.Join(" · ", reasonsEn),
            ReasonSummaryTr = string.Join(" · ", reasonsTr)
        };
    }
}
