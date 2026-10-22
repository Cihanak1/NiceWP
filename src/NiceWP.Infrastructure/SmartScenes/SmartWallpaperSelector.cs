using NiceWP.Core;
using NiceWP.Infrastructure.Library;
using NiceWP.Infrastructure.Rotation;

namespace NiceWP.Infrastructure.SmartScenes;

public sealed record ScoredWallpaperCandidate(
    WallpaperLibraryItem Item,
    WallpaperAnalysisMetadata? Metadata,
    WallpaperSceneScore Score);

public sealed record SmartWallpaperSelectionResult(
    WallpaperLibraryItem SelectedItem,
    WallpaperSceneScore Score,
    IReadOnlyList<ScoredWallpaperCandidate> ScoredCandidates);

/// <summary>
/// Selects the best contextual wallpaper from eligible library items using Smart Scene scoring
/// and diversified selection to prevent monotonous immediate repetitions.
/// </summary>
public static class SmartWallpaperSelector
{
    public const double TopTierScoreTolerance = 5.0;

    /// <summary>
    /// Evaluates eligible library candidates and selects the winning wallpaper.
    /// Returns null if no eligible candidates exist.
    /// </summary>
    public static SmartWallpaperSelectionResult? Select(
        IReadOnlyList<WallpaperLibraryItem> candidates,
        Func<string, WallpaperAnalysisMetadata?> metadataLookup,
        SmartSceneContext context,
        IRotationRandomProvider? randomProvider = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(metadataLookup);
        ArgumentNullException.ThrowIfNull(context);

        var eligible = candidates
            .Where(item => item != null && item.FileExists())
            .ToList();

        if (eligible.Count == 0)
        {
            return null;
        }

        var scored = new List<ScoredWallpaperCandidate>(eligible.Count);
        foreach (var item in eligible)
        {
            var meta = metadataLookup(item.Id);
            var score = WallpaperSceneScorer.Score(item, meta, context);
            scored.Add(new ScoredWallpaperCandidate(item, meta, score));
        }

        // Order descending by score, then tie-break by oldest last-used or random
        var sorted = scored.OrderByDescending(c => c.Score.TotalScore).ToList();
        if (sorted.Count == 1)
        {
            return new SmartWallpaperSelectionResult(sorted[0].Item, sorted[0].Score, sorted);
        }

        double highestScore = sorted[0].Score.TotalScore;

        // Group top-tier candidates within TopTierScoreTolerance (e.g. 5.0 pts)
        var topTier = sorted
            .Where(c => highestScore - c.Score.TotalScore <= TopTierScoreTolerance)
            .Take(3)
            .ToList();

        ScoredWallpaperCandidate winner;
        if (topTier.Count == 1 || randomProvider == null)
        {
            winner = topTier[0];
        }
        else
        {
            int pickIndex = randomProvider.Next(topTier.Count);
            winner = topTier[pickIndex];
        }

        return new SmartWallpaperSelectionResult(winner.Item, winner.Score, sorted);
    }
}
