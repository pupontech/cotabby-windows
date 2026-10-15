using System;
using System.Runtime.InteropServices;
using Cotabby.Core.Suggestions;

namespace Cotabby.App.Power;

/// <summary>
/// Reads the Windows energy-saver ("Low Power Mode") state the same way the
/// macOS monitor reads <c>ProcessInfo.isLowPowerModeEnabled</c>: one cheap
/// poll per availability evaluation, no notification channel needed. A read
/// failure keeps the last known state rather than flipping suggestions off.
/// </summary>
/// <remarks>
/// PowerRegisterSuspendResumeNotification would give push updates, but the
/// coordinator already re-evaluates availability on every keystroke, so a
/// 32-byte poll (cached in-process per <see cref="RefreshWindowSeconds"/>) is
/// simpler and cannot leak a registration on dispose.
/// </remarks>
public sealed class LowPowerModeMonitor : ILowPowerModeProvider
{
    private TimeSpan _refreshWindow = TimeSpan.FromSeconds(2);
    private DateTimeOffset _lastPollUtc;
    private bool _lastState;

    /// <summary>How long a poll result is trusted before re-reading. Purely a CPU guard.</summary>
    public TimeSpan RefreshWindowSeconds
    {
        get => _refreshWindow;
        set => _refreshWindow = value;
    }

    /// <summary>True while Windows energy saver is on. Never throws.</summary>
    public bool IsLowPowerModeEnabled => Read();

    private bool Read()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPollUtc < _refreshWindow) return _lastState;

        _lastPollUtc = now;
        try
        {
            var sps = new SYSTEM_POWER_STATUS();
            if (!GetSystemPowerStatus(ref sps)) return _lastState;
            _lastState = sps.SystemStatusFlag != 0;
        }
        catch (Exception)
        {
            // A failed read keeps the previous answer; suggestions keep flowing
            // on the last known state rather than silently stopping.
        }
        return _lastState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte Reserved1;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
        /// <summary>Non-zero while energy saver ("battery saver") is engaged.</summary>
        public byte SystemStatusFlag;
        public byte Reserved2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS lpSystemPowerStatus);
}
