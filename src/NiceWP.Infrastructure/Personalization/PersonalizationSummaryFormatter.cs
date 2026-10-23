namespace NiceWP.Infrastructure.Personalization;

using System;
using System.Collections.Generic;
using NiceWP.Core.Personalization;

/// <summary>
/// Formats learned personalization tendencies and statistics for UI presentation.
/// </summary>
public static class PersonalizationSummaryFormatter
{
    public const double SignificanceThreshold = 0.25;

    public static IReadOnlyList<string> GetTopTendencies(PersonalizationFeatureWeights weights, bool isTr)
    {
        if (weights == null) return Array.Empty<string>();

        var candidates = new List<(string Label, double Weight)>();

        void Check(double val, string en, string tr)
        {
            if (val >= SignificanceThreshold)
            {
                candidates.Add((isTr ? tr : en, val));
            }
        }

        Check(weights.DarkPreference, "Dark wallpapers \u2191", "Koyu duvar k\u00e2\u011f\u0131tlar\u0131 \u2191");
        Check(weights.LightPreference, "Light wallpapers \u2191", "A\u00e7\u0131k tonlu duvar k\u00e2\u011f\u0131tlar\u0131 \u2191");
        Check(weights.StaticPreference, "Static wallpapers \u2191", "Statik duvar k\u00e2\u011f\u0131tlar\u0131 \u2191");
        Check(weights.VideoPreference, "Video wallpapers \u2191", "Video duvar k\u00e2\u011f\u0131tlar\u0131 \u2191");
        Check(weights.LowMotionPreference, "Low motion \u2191", "D\u00fc\u015f\u00fck hareket \u2191");
        Check(weights.HighMotionPreference, "High motion \u2191", "Y\u00fcksek hareket \u2191");
        Check(weights.WarmPreference, "Warm tones \u2191", "S\u0131cak renk tonlar\u0131 \u2191");
        Check(weights.CoolPreference, "Cool tones \u2191", "So\u011fuk renk tonlar\u0131 \u2191");
        Check(weights.NeutralPreference, "Balanced tones \u2191", "Dengeli tonlar \u2191");
        Check(weights.LightResourcePreference, "Light resource footprint \u2191", "Hafif sistem y\u00fck\u00fc \u2191");
        Check(weights.HeavyResourcePreference, "Rich dynamic wallpapers \u2191", "Zengin dinamik efektler \u2191");

        candidates.Sort((a, b) => b.Weight.CompareTo(a.Weight));
        var count = Math.Min(4, candidates.Count);
        var result = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            result.Add(candidates[i].Label);
        }

        return result;
    }
}
