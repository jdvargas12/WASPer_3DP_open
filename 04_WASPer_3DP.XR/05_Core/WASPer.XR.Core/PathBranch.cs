namespace WASPer.XR.Core;

/// <summary>
/// One continuous stroke: the WASPer.XR.Core counterpart of a wsp_path
/// branch (WasperPrintPath.PtPlanes / PathRoles / StrokeIds / LayerH /
/// LayerW / LayerWf, one branch per GH_Path) and of a "path" entry in the
/// .wasperxr schema. Positions and orientation axes are parallel arrays, one
/// entry per point on the branch; the per-point series (height/width/speed)
/// are parallel too, and may be empty when a branch has no per-point data,
/// in which case a viewer falls back to WASPerPrintJob.DefaultBead.
/// </summary>
public sealed record PathBranch(
    int BranchIndex,
    string BranchPath,
    int LayerIndex,
    PathRole Role,
    int StrokeId,
    bool Closed,
    IReadOnlyList<Vec3> Positions,
    IReadOnlyList<Vec3> XAxes,
    IReadOnlyList<Vec3> YAxes,
    IReadOnlyList<Vec3> ZAxes,
    IReadOnlyList<double> LayerHeight,
    IReadOnlyList<double> LayerWidthNominal,
    IReadOnlyList<double> LayerWidthFlowAdjusted,
    IReadOnlyList<double> PrintSpeed);
