namespace WASPer.XR.Core;

/// <summary>
/// Mirrors the "coordinates" object in the .wasperxr JSON schema (0.1.0):
/// frame name, source units, metres-per-unit conversion, handedness and up
/// axis, plus the shared float64 origin the binary container (0.2.0) offsets
/// every position from. Every position in a WASPerPrintJob is expressed in
/// these units, relative to this origin.
/// </summary>
public sealed record CoordinateFrame(
    string Frame,
    string Units,
    double MetresPerUnit,
    string Handedness,
    string UpAxis,
    Vec3 Origin);
