namespace WASPer.XR.Core;

/// <summary>
/// Coarse machine/job status carried by WASPerProcessState. Deliberately
/// small -- richer detail (warnings, telemetry) is a later phase per the
/// plan (Phase 10, Cerebro telemetry) and does not belong on this enum.
/// </summary>
public enum ProcessStatus
{
    Idle,
    Printing,
    Paused,
    Completed,
    Error
}
