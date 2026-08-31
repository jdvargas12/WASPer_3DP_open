namespace WASPer.XR.Core;

/// <summary>
/// Semantic printing role. Integer values are pinned to match
/// WASPer_3DP.WasperPathRole (01_WASPer_3DP/Components/Shared/Geometry/
/// WASPer_PathRole.cs) and the "role" field in the .wasperxr schema (both
/// the 0.1.0 JSON and the 0.2.0 binary container) -- do not renumber these
/// without updating all three places together.
/// </summary>
public enum PathRole
{
    Undefined = 0,
    Shell = 1,
    Infill = 2,
    Partition = 3,
    Support = 4,
    Transition = 5
}
