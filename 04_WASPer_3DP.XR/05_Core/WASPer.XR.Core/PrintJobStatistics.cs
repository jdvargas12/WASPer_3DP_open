namespace WASPer.XR.Core;

/// <summary>Mirrors the .wasperxr schema's "summary" object.</summary>
public sealed record PrintJobStatistics(
    double TotalLengthModelUnits,
    double ExtrusionLengthModelUnits,
    double TravelLengthModelUnits,
    int LayerCount,
    double EstimatedDurationSeconds);
