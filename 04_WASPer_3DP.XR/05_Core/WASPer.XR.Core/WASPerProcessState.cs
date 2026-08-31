namespace WASPer.XR.Core;

/// <summary>
/// Live State (plan Phase 1.2): sent continuously, kept small, never
/// repeating anything already carried once in WASPerPrintJob.
/// </summary>
public sealed record WASPerProcessState(
    DateTimeOffset Timestamp,
    ProcessStatus Status,
    double Progress,
    int CurrentSegmentIndex,
    double SegmentProgress,
    Vec3 CurrentPosition,
    int CurrentLayer,
    double ElapsedTimeSeconds,
    double RemainingTimeSeconds,
    double Speed,
    double Flow,
    bool ExtrusionOn);
