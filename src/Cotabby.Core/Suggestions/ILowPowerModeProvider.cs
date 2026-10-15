namespace Cotabby.Core.Suggestions;

/// <summary>
/// Platform adapter for the upstream Low Power Mode check (#812). Core keeps
/// the gate pure; the App layer binds the OS-specific reader (Windows energy
/// saver via GetSystemPowerStatus) so tests can drive the state machine with
/// a fixed value.
/// </summary>
public interface ILowPowerModeProvider
{
    /// <summary>True while the OS energy-saver ("Low Power Mode") state is active.</summary>
    bool IsLowPowerModeEnabled { get; }
}
