namespace WASPer.XR.Core;

/// <summary>
/// Mirrors the top-level fields of the .wasperxr schema (jobId, revision,
/// timestampUtc, pluginVersion) plus the coordinate frame every position in
/// the job is expressed in.
/// </summary>
public sealed record PrintJobMetadata(
    string JobId,
    string Name,
    int Revision,
    DateTimeOffset TimestampUtc,
    string PluginVersion,
    CoordinateFrame Coordinates,
    // Added for Sm05 XR Scene Params (2026-08-19): true when the exporting Sm01 had an
    // external simulation parameter connected (Sm05's sim_par, typically fed by Gc05), so an
    // external source already owns the simulated print position. Viewers should hide their
    // own Play/Stop/time-slider controls when this is set, rather than run a second,
    // conflicting clock. Defaults to false so existing callers/fixtures are unaffected.
    bool DisablePlayback = false,
    // Added same day, right after DisablePlayback: the actual 0-1 progress value behind that
    // flag (Sm05's sim_par, typically driven by Gc05's own playback). Only meaningful when
    // DisablePlayback is true; a viewer should multiply this by its own job duration to pick
    // a print-progress time and reuse whatever "printed so far" rendering it already has for
    // local scrubbing, rather than trimming path data server-side. Defaults to 1.0 ("fully
    // printed"), matching the writer's own default.
    double SimulationParameter = 1.0);
