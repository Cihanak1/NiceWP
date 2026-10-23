using System.Diagnostics;
using NiceWP.Core;
using NiceWP.Core.Personalization;
using NiceWP.Infrastructure.Library;

namespace NiceWP.Infrastructure.Personalization;

/// <summary>
/// Coordinates deterministic, local-only personalization signals, learning step sizes,
/// wallpaper exclusions, and preference persistence.
/// </summary>
public sealed class PersonalizationService
{
    private const double MoreLikeThisItemStep = 0.20;
    private const double MoreLikeThisFeatureStep = 0.08;
    private const double LessLikeThisItemStep = -0.20;
    private const double LessLikeThisFeatureStep = -0.08;
    private const double FavoriteStep = 0.10;
    private const double FavoriteFeatureStep = 0.03;
    private const double ManualSelectionStep = 0.02;
    private const double MaxManualSelectionAccumulated = 0.10;

    private readonly PersonalizationStore _store;
    private readonly Func<bool>? _getIsEnabled;
    private readonly Func<bool>? _getUseFavorites;
    private readonly Func<bool>? _getLearnManual;
    private readonly Func<bool>? _getAvoidExcluded;

    public PersonalizationStore Store => _store;
    public PersonalizationProfile Profile => _store.Profile;

    public event EventHandler<string>? PreferenceUpdated;
    public event EventHandler<string>? ExclusionChanged;
    public event EventHandler? ResetCompleted;

    public PersonalizationService(
        PersonalizationStore? store = null,
        Func<bool>? getIsEnabled = null,
        Func<bool>? getUseFavorites = null,
        Func<bool>? getLearnManual = null,
        Func<bool>? getAvoidExcluded = null)
    {
        _store = store ?? new PersonalizationStore();
        _getIsEnabled = getIsEnabled;
        _getUseFavorites = getUseFavorites;
        _getLearnManual = getLearnManual;
        _getAvoidExcluded = getAvoidExcluded;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    public bool IsEnabled => _getIsEnabled?.Invoke() ?? Profile.IsEnabled;
    public bool UseFavorites => _getUseFavorites?.Invoke() ?? true;
    public bool LearnManual => _getLearnManual?.Invoke() ?? true;
    public bool AvoidExcluded => _getAvoidExcluded?.Invoke() ?? true;

    /// <summary>
    /// Checks whether an item is excluded from smart/automatic rotation.
    /// </summary>
    public bool IsExcluded(string? wallpaperId)
    {
        if (string.IsNullOrWhiteSpace(wallpaperId) || !AvoidExcluded) return false;
        lock (_store)
        {
            return Profile.ExcludedWallpaperIds.Contains(wallpaperId);
        }
    }

    /// <summary>
    /// Explicitly excludes or removes exclusion for a wallpaper item.
    /// </summary>
    public void SetExcluded(string wallpaperId, bool excluded)
    {
        if (string.IsNullOrWhiteSpace(wallpaperId)) return;

        lock (_store)
        {
            if (excluded)
            {
                Profile.ExcludedWallpaperIds.Add(wallpaperId);
                Profile.InteractionSummary.ExcludedCount++;
                var record = GetOrCreateRecord(wallpaperId);
                record.IsExcluded = true;
                record.LastInteractedAtUtc = DateTimeOffset.UtcNow;
            }
            else
            {
                Profile.ExcludedWallpaperIds.Remove(wallpaperId);
                if (Profile.WallpaperPreferences.TryGetValue(wallpaperId, out var record))
                {
                    record.IsExcluded = false;
                    record.LastInteractedAtUtc = DateTimeOffset.UtcNow;
                }
            }

            Profile.InteractionSummary.TotalExplicitInteractions++;
            Profile.InteractionSummary.LastInteractionAtUtc = DateTimeOffset.UtcNow;
        }

        // Exclusion changes trigger immediate save for safety
        _ = _store.SaveAsync(CancellationToken.None);
        ExclusionChanged?.Invoke(this, wallpaperId);
    }

    /// <summary>
    /// Records an explicit user action (More Like This, Less Like This, Never Show Again, Remove Exclusion).
    /// </summary>
    public void RecordFeedback(
        string wallpaperId,
        WallpaperPreferenceAction action,
        WallpaperAnalysisMetadata? metadata = null)
    {
        if (string.IsNullOrWhiteSpace(wallpaperId)) return;

        lock (_store)
        {
            var summary = Profile.InteractionSummary;
            summary.TotalExplicitInteractions++;
            summary.LastInteractionAtUtc = DateTimeOffset.UtcNow;

            switch (action)
            {
                case WallpaperPreferenceAction.MoreLikeThis:
                    summary.MoreLikeThisCount++;
                    AdjustWallpaperWeight(wallpaperId, MoreLikeThisItemStep);
                    if (metadata != null)
                    {
                        AdjustFeatureWeights(metadata, MoreLikeThisFeatureStep);
                    }
                    _store.RequestSave();
                    break;

                case WallpaperPreferenceAction.LessLikeThis:
                    summary.LessLikeThisCount++;
                    AdjustWallpaperWeight(wallpaperId, LessLikeThisItemStep);
                    if (metadata != null)
                    {
                        AdjustFeatureWeights(metadata, LessLikeThisFeatureStep);
                    }
                    _store.RequestSave();
                    break;

                case WallpaperPreferenceAction.NeverShowAgain:
                    SetExcluded(wallpaperId, true);
                    return;

                case WallpaperPreferenceAction.RemoveExclusion:
                    SetExcluded(wallpaperId, false);
                    return;
            }
        }

        PreferenceUpdated?.Invoke(this, wallpaperId);
    }

    /// <summary>
    /// Records favorite toggle signal if favorite learning is enabled.
    /// </summary>
    public void RecordFavoriteSignal(
        string wallpaperId,
        bool isFavorite,
        WallpaperAnalysisMetadata? metadata = null)
    {
        if (!IsEnabled || !UseFavorites || string.IsNullOrWhiteSpace(wallpaperId)) return;

        lock (_store)
        {
            double step = isFavorite ? FavoriteStep : -FavoriteStep;
            AdjustWallpaperWeight(wallpaperId, step);

            if (metadata != null)
            {
                double traitStep = isFavorite ? FavoriteFeatureStep : -FavoriteFeatureStep;
                AdjustFeatureWeights(metadata, traitStep);
            }

            _store.RequestSave();
        }

        PreferenceUpdated?.Invoke(this, wallpaperId);
    }

    /// <summary>
    /// Records manual user wallpaper selection. Only applies to deliberate manual selection,
    /// never automatic rotation or startup restoration.
    /// </summary>
    public void RecordManualSelection(
        WallpaperLibraryItem item,
        WallpaperAnalysisMetadata? metadata = null)
    {
        if (!IsEnabled || !LearnManual || item == null || string.IsNullOrWhiteSpace(item.Id)) return;

        lock (_store)
        {
            var record = GetOrCreateRecord(item.Id);
            record.ManualApplyCount++;
            Profile.InteractionSummary.ManualSelectionCount++;
            Profile.InteractionSummary.LastInteractionAtUtc = DateTimeOffset.UtcNow;

            // Incremental capped nudge (+0.02, up to 5 manual applies = +0.10 max)
            if (record.ManualApplyCount <= 5)
            {
                record.Weight = Math.Clamp(record.Weight + ManualSelectionStep, -1.0, 1.0);
                record.Level = WallpaperPreferenceRecord.ResolveLevel(record.Weight);
            }
            record.LastInteractedAtUtc = DateTimeOffset.UtcNow;

            if (metadata != null && record.ManualApplyCount <= 3)
            {
                AdjustFeatureWeights(metadata, 0.02);
            }

            _store.RequestSave();
        }

        PreferenceUpdated?.Invoke(this, item.Id);
    }

    /// <summary>
    /// Computes personalization score for a candidate wallpaper.
    /// </summary>
    public PersonalizationScoreResult Score(
        WallpaperLibraryItem item,
        WallpaperAnalysisMetadata? metadata)
    {
        if (!IsEnabled)
        {
            return PersonalizationScoreResult.Empty;
        }

        return WallpaperPersonalizationScorer.Score(item, metadata, Profile);
    }

    /// <summary>
    /// Resets all personalization data while leaving favorites and library untouched.
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _store.ResetAsync(cancellationToken).ConfigureAwait(false);
        ResetCompleted?.Invoke(this, EventArgs.Empty);
    }

    private WallpaperPreferenceRecord GetOrCreateRecord(string wallpaperId)
    {
        if (!Profile.WallpaperPreferences.TryGetValue(wallpaperId, out var record) || record == null)
        {
            record = WallpaperPreferenceRecord.Create(wallpaperId, 0.0);
            Profile.WallpaperPreferences[wallpaperId] = record;
        }
        return record;
    }

    private void AdjustWallpaperWeight(string wallpaperId, double delta)
    {
        var record = GetOrCreateRecord(wallpaperId);
        record.Weight = Math.Clamp(record.Weight + delta, -1.0, 1.0);
        record.Level = WallpaperPreferenceRecord.ResolveLevel(record.Weight);
        record.LastInteractedAtUtc = DateTimeOffset.UtcNow;
    }

    private void AdjustFeatureWeights(WallpaperAnalysisMetadata meta, double delta)
    {
        var fw = Profile.FeatureWeights;
        if (fw == null) return;

        if (meta.IsDark) fw.DarkPreference += delta;
        if (meta.IsLight) fw.LightPreference += delta;

        if (meta.MediaType == WallpaperMediaType.Video) fw.VideoPreference += delta;
        else fw.StaticPreference += delta;

        if (meta.MotionLevel is WallpaperMotionLevel.None or WallpaperMotionLevel.Low) fw.LowMotionPreference += delta;
        else if (meta.MotionLevel == WallpaperMotionLevel.High) fw.HighMotionPreference += delta;

        switch (meta.ColorTemperature)
        {
            case WallpaperColorTemperature.Warm: fw.WarmPreference += delta; break;
            case WallpaperColorTemperature.Cool: fw.CoolPreference += delta; break;
            default: fw.NeutralPreference += delta; break;
        }

        switch (meta.ResourceClass)
        {
            case WallpaperCost.Light: fw.LightResourcePreference += delta; break;
            case WallpaperCost.Heavy: fw.HeavyResourcePreference += delta; break;
            default: fw.ModerateResourcePreference += delta; break;
        }

        fw.ClampAll();
    }
}
