using System.Diagnostics;
using NiceWP.Core;
using static NiceWP.Platform.Windows.DesktopNativeMethods;

namespace NiceWP.Platform.Windows;

/// <summary>
/// Queries Windows power and battery state via Win32 GetSystemPowerStatus.
/// </summary>
public sealed class WindowsPowerStatusProvider : IPowerStatusProvider
{
    public const int LowBatteryThresholdPercent = PowerStatus.LowBatteryThresholdPercent;

    private readonly Func<PowerStatus>? customProvider;

    public WindowsPowerStatusProvider()
    {
    }

    public WindowsPowerStatusProvider(Func<PowerStatus> customProvider)
    {
        ArgumentNullException.ThrowIfNull(customProvider);
        this.customProvider = customProvider;
    }

    public PowerStatus GetPowerStatus()
    {
        if (customProvider != null)
        {
            return customProvider();
        }

        try
        {
            if (!GetSystemPowerStatus(out var native))
            {
                return PowerStatus.DesktopDefault;
            }

            return FromNative(
                native.ACLineStatus,
                native.BatteryFlag,
                native.BatteryLifePercent,
                native.SystemStatusFlag);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Error querying Windows power status: {ex}");
            return PowerStatus.DesktopDefault;
        }
    }

    public static PowerStatus FromNative(
        byte acLineStatus,
        byte batteryFlag,
        byte batteryLifePercent,
        byte systemStatusFlag)
    {
        var hasBattery = batteryFlag != BatteryStatusUnknown &&
                         (batteryFlag & BatteryFlagNoSystemBattery) == 0;

        if (!hasBattery)
        {
            return PowerStatus.DesktopDefault;
        }

        var isAcConnected = acLineStatus == AcLineOnline;
        int? percentage = batteryLifePercent <= 100 ? batteryLifePercent : null;
        var isBatterySaverActive = systemStatusFlag == SystemStatusBatterySaverOn;

        return new PowerStatus(
            IsAcPowerConnected: isAcConnected,
            HasBattery: true,
            BatteryLifePercent: percentage,
            IsBatterySaverActive: isBatterySaverActive);
    }
}
