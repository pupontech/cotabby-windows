using System.Runtime.InteropServices;
using Cotabby.Core.Suggestions;
using Cotabby.Core.Support;

namespace Cotabby.App.Power;

/// <summary>Cheap cached native power reads shared by energy-saver and opt-in battery policies.</summary>
public sealed class LowPowerModeMonitor : ILowPowerModeProvider, IPowerSnapshotProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _lastPollUtc;
    private bool _lastState;
    private PowerSnapshot _snapshot;
    public TimeSpan RefreshWindowSeconds { get; set; } = TimeSpan.FromSeconds(2);

    public bool IsLowPowerModeEnabled { get { lock (_gate) { Read(); return _lastState; } } }
    public PowerSnapshot Snapshot { get { lock (_gate) { Read(); return _snapshot; } } }

    private void Read()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastPollUtc < RefreshWindowSeconds) return;
        _lastPollUtc = now;
        _snapshot = default; // Unknown battery state must never keep a stale deferral.
        try
        {
            var status = new SYSTEM_POWER_STATUS();
            if (!GetSystemPowerStatus(ref status)) return;
            _lastState = status.SystemStatusFlag == 1;
            if (status.ACLineStatus > 1 || status.BatteryFlag == 255 || (status.BatteryFlag & 128) != 0
                || status.BatteryLifePercent > 100) return;
            _snapshot = new PowerSnapshot(status.ACLineStatus == 1, status.BatteryLifePercent,
                (status.BatteryFlag & 8) != 0);
        }
        catch (Exception) { /* Unknown battery -> Normal; energy saver keeps last known state. */ }
    }

    // SYSTEM_POWER_STATUS is exactly 12 bytes; energy saver occupies byte 3,
    // not a field appended after BatteryFullLifeTime.
    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(ref SYSTEM_POWER_STATUS lpSystemPowerStatus);
}
