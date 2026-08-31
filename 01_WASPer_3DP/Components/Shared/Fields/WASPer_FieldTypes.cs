// WASPer_FieldTypes.cs
// WASPer_3DP — Shared field data types
//
// WasperField     : 3-D signed-distance field evaluator (negative inside, positive outside).
// WasperFieldGoo  : Grasshopper Goo wrapper for wiring fields between components.
//
// All types are internal to the WASPer_3DP assembly (namespace WASPer_3DP).

using System;
using Rhino.Geometry;
using Grasshopper.Kernel.Types;
using GH_IO.Serialization;

namespace WASPer_3DP
{
    public enum WasperFieldSdfQuality
    {
        Unknown = 0,
        ExactSdf = 1,
        ApproximateSdf = 2,
        ImplicitScalarField = 3
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WasperField
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A 3-D signed-distance field.  Negative values are inside the solid,
    /// positive values are outside.  The evaluator is a captured closure —
    /// it references the geometry that was alive when the field was created.
    /// </summary>
    public sealed class WasperField
    {
        public readonly Func<Point3d, double> Evaluator;
        public readonly BoundingBox            Domain;
        public readonly string                 Label;
        public readonly string                 OperationTrace;
        public readonly WasperFieldSdfQuality  SdfQuality;
        public readonly int                    OperationCount;
        public readonly int                    CurveThickenCount;

        public WasperField(
            Func<Point3d, double> evaluator,
            BoundingBox domain,
            string label = "",
            string operationTrace = null,
            WasperFieldSdfQuality sdfQuality = WasperFieldSdfQuality.Unknown,
            int operationCount = 0,
            int curveThickenCount = 0)
        {
            Evaluator         = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
            Domain            = domain;
            Label             = label ?? "";
            OperationTrace    = string.IsNullOrWhiteSpace(operationTrace) ? BuildDefaultTrace(Label, sdfQuality) : operationTrace;
            SdfQuality        = sdfQuality;
            OperationCount    = Math.Max(0, operationCount);
            CurveThickenCount = Math.Max(0, curveThickenCount);
        }

        /// <summary>Evaluate the field at world point <paramref name="p"/>.</summary>
        public double Evaluate(Point3d p) => Evaluator(p);

        private static string BuildDefaultTrace(string label, WasperFieldSdfQuality sdfQuality)
        {
            string name = string.IsNullOrWhiteSpace(label) ? "WasperField source" : label;
            return $"{name} | quality={sdfQuality}";
        }

        // ── Factories ────────────────────────────────────────────────────────

        /// <summary>Build an exact signed-distance field from an oriented Rhino box.</summary>
        public static WasperField FromBox(Box box, string label = "")
        {
            if (!box.IsValid) return null;

            Box b = box;
            double cx = 0.5 * (b.X.T0 + b.X.T1);
            double cy = 0.5 * (b.Y.T0 + b.Y.T1);
            double cz = 0.5 * (b.Z.T0 + b.Z.T1);
            double hx = 0.5 * Math.Abs(b.X.T1 - b.X.T0);
            double hy = 0.5 * Math.Abs(b.Y.T1 - b.Y.T0);
            double hz = 0.5 * Math.Abs(b.Z.T1 - b.Z.T0);

            if (hx < 1e-12 || hy < 1e-12 || hz < 1e-12)
                return null;

            return new WasperField(
                p =>
                {
                    Vector3d delta = p - b.Plane.Origin;
                    double dx = Math.Abs(delta * b.Plane.XAxis - cx) - hx;
                    double dy = Math.Abs(delta * b.Plane.YAxis - cy) - hy;
                    double dz = Math.Abs(delta * b.Plane.ZAxis - cz) - hz;

                    double ox = Math.Max(dx, 0.0);
                    double oy = Math.Max(dy, 0.0);
                    double oz = Math.Max(dz, 0.0);
                    double outside = Math.Sqrt(ox * ox + oy * oy + oz * oz);
                    double inside = Math.Min(Math.Max(dx, Math.Max(dy, dz)), 0.0);
                    return outside + inside;
                },
                b.BoundingBox,
                label,
                $"Source: Box field [{(string.IsNullOrEmpty(label) ? "box" : label)}]",
                WasperFieldSdfQuality.ExactSdf);
        }

        /// <summary>
        /// Build an SDF from a closed mesh.
        /// Query cost: one ClosestPoint + one IsPointInside call per evaluation.
        /// </summary>
        public static WasperField FromMesh(
            Mesh mesh,
            double tolerance = 0.001,
            string label = "",
            string operationTrace = null,
            WasperFieldSdfQuality sdfQuality = WasperFieldSdfQuality.ExactSdf)
        {
            if (mesh == null || mesh.Faces.Count == 0) return null;

            var   m   = mesh;                               // closure capture
            double tol = Math.Max(tolerance, 1e-9);
            var   dom = mesh.GetBoundingBox(true);

            return new WasperField(
                p =>
                {
                    Point3d cp = m.ClosestPoint(p);
                    double  d  = p.DistanceTo(cp);
                    return m.IsPointInside(p, tol, true) ? -d : d;
                },
                dom,
                label,
                operationTrace ?? $"Source: Mesh field [{(string.IsNullOrEmpty(label) ? "mesh" : label)}]",
                sdfQuality);
        }

        /// <summary>
        /// Build an SDF from a closed Brep solid.
        /// Query cost: one ClosestPoint + one IsPointInside call per evaluation.
        /// </summary>
        public static WasperField FromBrep(Brep brep, double tolerance = 0.001, string label = "")
        {
            if (brep == null || !brep.IsValid) return null;

            var    b   = brep;
            double tol = Math.Max(tolerance, 1e-9);
            var    dom = brep.GetBoundingBox(true);

            return new WasperField(
                p =>
                {
                    Point3d cp;
                    ComponentIndex ci;
                    double s, t;
                    Vector3d n;
                    b.ClosestPoint(p, out cp, out ci, out s, out t, double.MaxValue, out n);
                    double d = p.DistanceTo(cp);
                    return b.IsPointInside(p, tol, false) ? -d : d;
                },
                dom,
                label,
                $"Source: Brep field [{(string.IsNullOrEmpty(label) ? "brep" : label)}]",
                WasperFieldSdfQuality.ExactSdf);
        }

        /// <summary>
        /// Build a field by trilinear interpolation from a pre-sampled axis-aligned
        /// box grid.  <paramref name="scalars"/> is indexed as
        /// <c>ix + nx*(iy + ny*iz)</c>, where ix/iy/iz map linearly to the box extents.
        /// </summary>
        public static WasperField FromBoxGrid(
            double[] scalars, int nx, int ny, int nz,
            Box box, string label = "")
        {
            var s   = (double[])scalars.Clone();
            int cnx = nx, cny = ny, cnz = nz;
            Box b   = box;
            var dom = box.BoundingBox;

            return new WasperField(
                p =>
                {
                    Vector3d d  = p - b.Plane.Origin;
                    double   lx = d * b.Plane.XAxis;
                    double   ly = d * b.Plane.YAxis;
                    double   lz = d * b.Plane.ZAxis;

                    double xLen = b.X.Length; if (xLen < 1e-12) xLen = 1e-12;
                    double yLen = b.Y.Length; if (yLen < 1e-12) yLen = 1e-12;
                    double zLen = b.Z.Length; if (zLen < 1e-12) zLen = 1e-12;

                    double u = (lx - b.X.T0) / xLen;
                    double v = (ly - b.Y.T0) / yLen;
                    double w = (lz - b.Z.T0) / zLen;

                    return TrilinearSample(s, cnx, cny, cnz, u, v, w);
                },
                dom,
                label,
                $"Source: BoxGrid field [{(string.IsNullOrEmpty(label) ? "box grid" : label)}]",
                WasperFieldSdfQuality.ApproximateSdf);
        }

        /// <summary>
        /// Build a field by trilinear interpolation from a pre-sampled curvilinear
        /// grid.  Uses the world bounding box as an approximation for the UVW mapping.
        /// </summary>
        public static WasperField FromCurvilinearGrid(
            double[] scalars, int nx, int ny, int nz,
            BoundingBox domain, string label = "")
        {
            var s   = (double[])scalars.Clone();
            int cnx = nx, cny = ny, cnz = nz;
            var d   = domain;

            return new WasperField(
                p =>
                {
                    double xLen = d.Max.X - d.Min.X; if (xLen < 1e-12) xLen = 1e-12;
                    double yLen = d.Max.Y - d.Min.Y; if (yLen < 1e-12) yLen = 1e-12;
                    double zLen = d.Max.Z - d.Min.Z; if (zLen < 1e-12) zLen = 1e-12;

                    double u = (p.X - d.Min.X) / xLen;
                    double v = (p.Y - d.Min.Y) / yLen;
                    double w = (p.Z - d.Min.Z) / zLen;

                    return TrilinearSample(s, cnx, cny, cnz, u, v, w);
                },
                domain,
                label,
                $"Source: CurvilinearGrid field [{(string.IsNullOrEmpty(label) ? "curvilinear grid" : label)}]",
                WasperFieldSdfQuality.ApproximateSdf);
        }

        /// <summary>
        /// Build a sampled field directly from a scalar grid and its world-space grid points.
        /// This avoids mesh closest-point queries for downstream field consumers.
        /// </summary>
        public static WasperField FromSampledPointGrid(
            double[] scalars,
            Point3d[] points,
            int nx,
            int ny,
            int nz,
            string label = "")
        {
            if (scalars == null || points == null) return null;
            int expected = nx * ny * nz;
            if (nx < 2 || ny < 2 || nz < 2 || scalars.Length < expected || points.Length < expected)
                return null;

            var s = (double[])scalars.Clone();
            var p = (Point3d[])points.Clone();
            int cnx = nx, cny = ny, cnz = nz;

            BoundingBox domain = BoundingBox.Empty;
            for (int i = 0; i < expected; i++)
            {
                if (p[i].IsValid)
                    domain.Union(p[i]);
            }

            return new WasperField(
                q =>
                {
                    int nearest = FindNearestGridIndex(p, expected, q);
                    int ix, iy, iz;
                    UnpackIndex(nearest, cnx, cny, out ix, out iy, out iz);

                    double wSum = 0.0;
                    double vSum = 0.0;

                    for (int dz = -1; dz <= 1; dz++)
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int x = ix + dx;
                        int y = iy + dy;
                        int z = iz + dz;
                        if (x < 0 || y < 0 || z < 0 || x >= cnx || y >= cny || z >= cnz)
                            continue;

                        int id = x + cnx * (y + cny * z);
                        double d2 = q.DistanceToSquared(p[id]);
                        if (d2 < 1e-18)
                            return s[id];

                        double w = 1.0 / d2;
                        wSum += w;
                        vSum += w * s[id];
                    }

                    return wSum > 0.0 ? vSum / wSum : s[nearest];
                },
                domain,
                label,
                $"Source: SampledPointGrid field [{(string.IsNullOrEmpty(label) ? "sampled point grid" : label)}]",
                WasperFieldSdfQuality.ApproximateSdf);
        }

        // ── Internal helpers ─────────────────────────────────────────────────

        private static double TrilinearSample(
            double[] s, int nx, int ny, int nz,
            double u, double v, double w)
        {
            u = u < 0 ? 0 : u > 1 ? 1 : u;
            v = v < 0 ? 0 : v > 1 ? 1 : v;
            w = w < 0 ? 0 : w > 1 ? 1 : w;

            double gx = u * (nx - 1);
            double gy = v * (ny - 1);
            double gz = w * (nz - 1);

            int ix0 = (int)gx; if (ix0 > nx - 2) ix0 = nx - 2; if (ix0 < 0) ix0 = 0;
            int iy0 = (int)gy; if (iy0 > ny - 2) iy0 = ny - 2; if (iy0 < 0) iy0 = 0;
            int iz0 = (int)gz; if (iz0 > nz - 2) iz0 = nz - 2; if (iz0 < 0) iz0 = 0;
            int ix1 = ix0 + 1, iy1 = iy0 + 1, iz1 = iz0 + 1;

            double tx = gx - ix0, ty = gy - iy0, tz = gz - iz0;

            double c000 = s[ix0 + nx * (iy0 + ny * iz0)];
            double c100 = s[ix1 + nx * (iy0 + ny * iz0)];
            double c010 = s[ix0 + nx * (iy1 + ny * iz0)];
            double c110 = s[ix1 + nx * (iy1 + ny * iz0)];
            double c001 = s[ix0 + nx * (iy0 + ny * iz1)];
            double c101 = s[ix1 + nx * (iy0 + ny * iz1)];
            double c011 = s[ix0 + nx * (iy1 + ny * iz1)];
            double c111 = s[ix1 + nx * (iy1 + ny * iz1)];

            double cx00 = c000 + tx * (c100 - c000);
            double cx10 = c010 + tx * (c110 - c010);
            double cx01 = c001 + tx * (c101 - c001);
            double cx11 = c011 + tx * (c111 - c011);

            double cxy0 = cx00 + ty * (cx10 - cx00);
            double cxy1 = cx01 + ty * (cx11 - cx01);

            return cxy0 + tz * (cxy1 - cxy0);
        }

        private static int FindNearestGridIndex(Point3d[] points, int count, Point3d q)
        {
            int best = 0;
            double bestD2 = double.PositiveInfinity;

            for (int i = 0; i < count; i++)
            {
                double d2 = q.DistanceToSquared(points[i]);
                if (d2 < bestD2)
                {
                    bestD2 = d2;
                    best = i;
                }
            }

            return best;
        }

        private static void UnpackIndex(int id, int nx, int ny, out int ix, out int iy, out int iz)
        {
            int slice = nx * ny;
            iz = id / slice;
            int rem = id - iz * slice;
            iy = rem / nx;
            ix = rem - iy * nx;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // WasperFieldGoo  —  Grasshopper data wrapper
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Grasshopper Goo wrapper so <see cref="WasperField"/> can be wired between
    /// components via generic parameters.
    /// Note: <see cref="WasperField.Evaluator"/> is a delegate and cannot be
    /// serialised.  The field output is re-computed on every solve; internalize
    /// data is not supported.
    /// </summary>
    public sealed class WasperFieldGoo : GH_Goo<WasperField>
    {
        public WasperFieldGoo()              : base((WasperField)null)  { }
        public WasperFieldGoo(WasperField f) : base(f)     { }

        public override bool   IsValid          => Value?.Evaluator != null;
        public override string TypeName         => "WASPer Field";
        public override string TypeDescription  => "A 3D signed distance field (negative inside, positive outside).";
        public override IGH_Goo Duplicate()     => new WasperFieldGoo(Value);

        public override string ToString()
        {
            if (Value == null) return "Null WASPer Field";
            string lbl = string.IsNullOrEmpty(Value.Label) ? "" : $" [{Value.Label}]";
            var bb = Value.Domain;
            return bb.IsValid
                ? $"WASPer Field{lbl}  {bb.Min.X:F1},{bb.Min.Y:F1},{bb.Min.Z:F1} … {bb.Max.X:F1},{bb.Max.Y:F1},{bb.Max.Z:F1}"
                : $"WASPer Field{lbl}  (no domain)";
        }

        // Func<> is not serialisable — field is re-computed on every solve
        public override bool Write(GH_IWriter writer) => true;
        public override bool Read(GH_IReader reader)  => true;

        public override bool CastTo<T>(ref T target)
        {
            if (typeof(T) == typeof(WasperField) && Value != null)
            {
                target = (T)(object)Value;
                return true;
            }
            return base.CastTo(ref target);
        }

        public override bool CastFrom(object source)
        {
            if (source is WasperField   f) { Value = f;       return true; }
            if (source is WasperFieldGoo g) { Value = g.Value; return true; }
            return false;
        }
    }
}
