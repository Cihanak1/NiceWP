using NiceWP.Core;
using NiceWP.Infrastructure.Ipc;

namespace NiceWP.Infrastructure.Adaptive;

/// <summary>
/// Dictates runtime execution parameters, polling intervals, and aggressiveness
/// based on the configured or dynamically evaluated <see cref="ResourceBudget"/>.
/// </summary>
public static class ResourceBudgetPolicy
{
    public static TimeSpan GetGracePeriod(ResourceBudget budget) => budget switch
    {
        ResourceBudget.Low => TimeSpan.FromSeconds(10),
        ResourceBudget.High => TimeSpan.FromMinutes(2),
        _ => TimeSpan.FromSeconds(30) // Balanced or Automatic default
    };

    public static int GetBackgroundPollIntervalMs(ResourceBudget budget) => budget switch
    {
        ResourceBudget.Low => 5000,
        ResourceBudget.High => 1000,
        _ => 2000 // Balanced
    };

    public static bool ShouldPauseOnFocusLoss(ResourceBudget budget, string? wallpaperType)
    {
        if (budget != ResourceBudget.Low) return false;
        return string.Equals(wallpaperType, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase);
    }

    public static bool ShouldDeepFreezeOnBackground(ResourceBudget budget, string? wallpaperType)
    {
        if (budget != ResourceBudget.Low) return false;
        return string.Equals(wallpaperType, IpcConstants.WallpaperTypeVideo, StringComparison.OrdinalIgnoreCase);
    }

    public static ResourceBudget ResolveAutomaticBudget(bool isOnBattery, bool isBatterySaver, bool isPressureSustained)
    {
        if (isBatterySaver || isPressureSustained)
        {
            return ResourceBudget.Low;
        }

        return ResourceBudget.Balanced;
    }
}
