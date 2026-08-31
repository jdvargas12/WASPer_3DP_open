namespace WASPer.XR.Core;

/// <summary>
/// Mirrors WasperMotionType (01_WASPer_3DP/Components/Shared/Fabrication/
/// WASPer_GcodeTypes.cs) and the .wasperxr binary container's motion type
/// byte code (0 print, 1 travel, 2 Z-hop, per WASPER_XR_BINARY_0.2.md).
/// </summary>
public enum MotionType
{
    Print = 0,
    Travel = 1,
    ZHop = 2
}
