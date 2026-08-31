namespace WASPer.XR.Core;

/// <summary>
/// Viewer-ready context geometry supplied by Sm05. Indices are a flat triangle list and color
/// is stored once per object so static scene geometry stays compact.
/// </summary>
public sealed record ContextMesh(
    string Id,
    IReadOnlyList<Vec3> Vertices,
    IReadOnlyList<Vec3> Normals,
    IReadOnlyList<int> TriangleIndices,
    byte Red,
    byte Green,
    byte Blue,
    byte Alpha);
