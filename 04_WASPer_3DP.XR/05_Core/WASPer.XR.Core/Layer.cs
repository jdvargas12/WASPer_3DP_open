namespace WASPer.XR.Core;

/// <summary>
/// One logical print layer, indexing into WASPerPrintJob.Segments so a
/// viewer can jump to "layer 31" without scanning the whole segment list.
/// </summary>
public sealed record Layer(
    int Index,
    double HeightModelUnits,
    int FirstSegmentIndex,
    int LastSegmentIndex);
