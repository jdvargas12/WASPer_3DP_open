namespace WASPer.XR.Core;

/// <summary>
/// Job Data (plan Phase 1.1): sent once when a print is loaded. Combines
/// what today lives in a .wasperxr package and a timed wsp_path into one
/// platform-independent model every viewer client consumes identically.
/// </summary>
public sealed record WASPerPrintJob(
    PrintJobMetadata Metadata,
    IReadOnlyList<PathBranch> Branches,
    IReadOnlyList<PathSegment> Segments,
    IReadOnlyList<Layer> Layers,
    BeadProperties DefaultBead,
    PrintJobStatistics Statistics,
    IReadOnlyList<PrintJobKpi> Kpis,
    IReadOnlyList<ContextMesh>? ContextMeshes = null,
    ViewerStyle? ViewerStyle = null);
