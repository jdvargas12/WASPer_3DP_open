namespace WASPer.XR.Core;

/// <summary>
/// Minimal double-precision 3D vector/point. WASPer.XR.Core does not
/// reference RhinoCommon or Rhino3dm, so every geometric value crossing this
/// boundary is expressed as this plain type instead, matching the plain
/// XYZ arrays already used by the .wasperxr schema and WSPLINK1.
/// </summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static readonly Vec3 Zero = new(0, 0, 0);

    public double DistanceTo(Vec3 other)
    {
        double dx = X - other.X;
        double dy = Y - other.Y;
        double dz = Z - other.Z;
        return Math.Sqrt((dx * dx) + (dy * dy) + (dz * dz));
    }

    public static Vec3 Lerp(Vec3 a, Vec3 b, double t) =>
        new(a.X + ((b.X - a.X) * t), a.Y + ((b.Y - a.Y) * t), a.Z + ((b.Z - a.Z) * t));
}
