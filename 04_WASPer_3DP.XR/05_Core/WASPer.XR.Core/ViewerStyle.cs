namespace WASPer.XR.Core;

/// <summary>
/// Compact presentation metadata captured from WASPer when a job is exported.
/// Colors are RGB integers (0xRRGGBB), indexed explicitly by semantic path role.
/// </summary>
public sealed record ViewerStyle(
    string PaletteName,
    int ShellColor,
    int InfillColor,
    int PartitionColor,
    int SupportColor,
    int TransitionColor,
    int UndefinedColor);
