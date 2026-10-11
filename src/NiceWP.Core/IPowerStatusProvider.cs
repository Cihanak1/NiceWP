namespace NiceWP.Core;

/// <summary>
/// Represents a snapshot of system power and battery state.
/// </summary>
public readonly record struct PowerStatus(
    bool IsAcPowerConnected,
    bool HasBattery,
    int? BatteryLifePercent,
    bool IsBatterySaverActive)
{
    public const int LowBatteryThresholdPercent = 20;

    public static readonly PowerStatus DesktopDefault = new(
        IsAcPowerConnected: true,
        HasBattery: false,
        BatteryLifePercent: null,
        IsBatterySaverActive: false);

    /// <summary>
    /// Determines whether the current power status requires automatic wallpaper pause:
    /// 1. Battery Saver / Energy Saver is active, OR
    /// 2. Device is running on battery (not AC connected) and battery percentage is at or below 20%.
    /// Desktop PCs without a battery never trigger a power pause.
    /// </summary>
    public bool ShouldPauseWallpaper(int lowBatteryThresholdPercent = LowBatteryThresholdPercent)
    {
        if (!HasBattery)
        {
            return false;
        }

        if (IsBatterySaverActive)
        {
            return true;
        }

        if (IsAcPowerConnected)
        {
            return false;
        }

        return BatteryLifePercent is { } percent &&
               percent >= 0 &&
               percent <= lowBatteryThresholdPercent;
    }
}

/// <summary>
/// Provides current system power and battery status.
/// </summary>
public interface IPowerStatusProvider
{
    PowerStatus GetPowerStatus();
}
