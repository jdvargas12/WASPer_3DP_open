namespace WASPer.XR.Core;

/// <summary>
/// One resolved machine movement between two points -- the animation unit
/// Phase 5 of the plan describes ("find active segment, find local segment
/// time, calculate interpolation factor, P = A + t(B-A)"). Mirrors
/// WasperMotion (01_WASPer_3DP/Components/Shared/Fabrication/
/// WASPer_GcodeTypes.cs) and the .wasperxr schema's "motion" object, with
/// cumulative start/end times carried explicitly so a viewer can locate the
/// active segment for a given elapsed time without re-summing durations on
/// every frame.
/// </summary>
public sealed record PathSegment(
    int Index,
    MotionType Type,
    int LayerIndex,
    int BranchIndex,
    string? BranchPath,
    int PointIndex,
    PathRole Role,
    Vec3 From,
    Vec3 To,
    double FeedrateMmPerMinute,
    double LengthModelUnits,
    double StartTimeSeconds,
    double EndTimeSeconds)
{
    public double DurationSeconds => EndTimeSeconds - StartTimeSeconds;
}
