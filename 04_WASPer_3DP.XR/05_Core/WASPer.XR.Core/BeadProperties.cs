namespace WASPer.XR.Core;

/// <summary>
/// Job-wide default bead dimensions (the plan's Phase 1.1 "bead { width,
/// height }" block), used when a PathBranch's per-point LayerWidth*/
/// LayerHeight series is empty. Per-branch series always take precedence
/// when present, matching how wsp_path already carries width/height as
/// per-branch trees rather than one global constant.
/// </summary>
public sealed record BeadProperties(double NominalWidth, double NominalHeight);
