namespace WASPer.XR.Core;

/// <summary>Result of locating a point in time within a WASPerPrintJob's segments (plan Phase 5).</summary>
public readonly record struct PathLocation(int SegmentIndex, double LocalT, Vec3 Position);

/// <summary>
/// Implements Phase 5's process-time-based lookup: find the active segment
/// for a given elapsed time, then interpolate position within it. Lives in
/// Core so every viewer (web, vvvv, Unity, Godot) drives animation
/// identically instead of each reimplementing this slightly differently.
/// </summary>
public static class ProcessAnimation
{
    /// <summary>
    /// Segments must be in ascending time order, as built from an ordered
    /// WasperMotionPlan. Each segment owns the half-open interval
    /// [StartTimeSeconds, EndTimeSeconds) except the last, which also owns
    /// its own end -- so a time sitting exactly on a boundary between two
    /// segments belongs to the segment starting there, not the one ending
    /// there. A linear scan is fine at typical job sizes and keeps this
    /// dependency-free; switch to binary search if a profiled viewer needs
    /// it.
    /// </summary>
    public static PathLocation Locate(IReadOnlyList<PathSegment> segments, double timeSeconds)
    {
        if (segments is null || segments.Count == 0)
            return new PathLocation(-1, 0.0, Vec3.Zero);

        if (timeSeconds <= segments[0].StartTimeSeconds)
            return new PathLocation(0, 0.0, segments[0].From);

        PathSegment last = segments[^1];
        if (timeSeconds >= last.EndTimeSeconds)
            return new PathLocation(segments.Count - 1, 1.0, last.To);

        for (int i = 0; i < segments.Count; i++)
        {
            PathSegment segment = segments[i];
            if (timeSeconds < segment.StartTimeSeconds || timeSeconds >= segment.EndTimeSeconds)
                continue;

            double duration = segment.DurationSeconds;
            double t = duration > 0.0
                ? (timeSeconds - segment.StartTimeSeconds) / duration
                : 0.0;
            return new PathLocation(i, t, Vec3.Lerp(segment.From, segment.To, t));
        }

        return new PathLocation(segments.Count - 1, 1.0, last.To);
    }
}
