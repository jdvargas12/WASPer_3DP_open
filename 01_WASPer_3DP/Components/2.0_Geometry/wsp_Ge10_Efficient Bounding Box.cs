#region Component Description
/*
Component: wsp_Ge10_Efficient Bounding Box
Nickname: EfficientBBox
Category: WASPer_3DP
SubCategory: 2.0_Geometry

GENERAL DESCRIPTION
Builds a low-volume oriented bounding box from input geometry. It is similar to
Grasshopper's Bounding Box component, but it can rotate the box to reduce volume.

Modes:
0) World Z rotation only. Useful for print-bed footprint and slicing workflows.
1) Full 3D PCA orientation. Useful for general compact bounds.
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

using Rhino;
using Rhino.Geometry;

using Grasshopper.Kernel;
#endregion

namespace WASPer_3DP.Components._3_Geometry
{
    public class wsp_Ge10_Efficient_Bounding_Box : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Ge10_Efficient_Bounding_Box()
          : base(
              "wsp_Ge10_Efficient Bounding Box",
              "EfficientBBox",
              "Generates a low-volume oriented bounding box. Mode 0 keeps World Z vertical; mode 1 uses full 3D PCA.",
              global::WASPer_3DP.WASPerPalette.DesignFabrication,
              "2.0_Geometry")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("B8E7B6D9-3A44-4D1E-97D2-0B9E6DFD7E41");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ge10_Efficient Bounding Box.png"))
                    {
                        return stream != null ? new Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGeometryParameter(
                "geometry",
                "geo",
                "Geometry to bound. Accepts Mesh, Brep, Surface, Extrusion, Curve, Point, and common Grasshopper geometry wrappers.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter(
                "mode",
                "mode",
                "Orientation mode. 0 = rotate around World Z only. 1 = full 3D PCA orientation.",
                GH_ParamAccess.item,
                0);

            pManager.AddBooleanParameter(
                "refine",
                "refine",
                "If true, tests nearby rotations and keeps the lowest-volume box.",
                GH_ParamAccess.item,
                true);

            pManager.AddIntegerParameter(
                "angle_steps",
                "steps",
                "Refinement step count. Higher values test more candidate rotations but are slower.",
                GH_ParamAccess.item,
                24);

            pManager.AddIntegerParameter(
                "samples",
                "samples",
                "Maximum representative points per input item for heavy Breps/curves. Mesh vertices are reduced when needed.",
                GH_ParamAccess.item,
                2000);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBoxParameter("box", "box", "Lowest-volume oriented bounding box found.", GH_ParamAccess.item);
            pManager.AddPlaneParameter("plane", "plane", "Box plane/orientation.", GH_ParamAccess.item);
            pManager.AddNumberParameter("volume", "volume", "Box volume.", GH_ParamAccess.item);
            pManager.AddVectorParameter("dimensions", "dims", "Box dimensions as X, Y, Z vector.", GH_ParamAccess.item);
            pManager.AddTextParameter("info", "info", "Search summary.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            var geometry = new List<GeometryBase>();
            int mode = 0;
            bool refine = true;
            int angleSteps = 24;
            int maxSamples = 2000;

            DA.GetDataList(0, geometry);
            DA.GetData(1, ref mode);
            DA.GetData(2, ref refine);
            DA.GetData(3, ref angleSteps);
            DA.GetData(4, ref maxSamples);

            mode = mode == 1 ? 1 : 0;
            angleSteps = Math.Max(4, Math.Min(angleSteps, 180));
            maxSamples = Math.Max(16, maxSamples);

            var points = CollectPoints(geometry, maxSamples);
            if (points.Count < 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Not enough valid points could be sampled from the input geometry.");
                DA.SetData(4, "No box: fewer than 2 sampled points.");
                return;
            }

            Box worldBox = BoundingBoxToBox(BoundingBoxFromPoints(points), Plane.WorldXY);
            Candidate best = EvaluatePlane(points, Plane.WorldXY);
            int tested = 1;

            if (mode == 0)
            {
                int coarse = refine ? Math.Max(angleSteps * 2, 36) : Math.Max(angleSteps, 12);
                for (int i = 0; i < coarse; i++)
                {
                    double a = Math.PI * i / coarse;
                    Plane plane = RotatedWorldZPlane(a);
                    Candidate c = EvaluatePlane(points, plane);
                    tested++;
                    if (c.Volume < best.Volume) best = c;
                }
            }
            else
            {
                Plane pca = BuildPcaPlane(points);
                best = BestOf(best, EvaluatePlane(points, pca));
                tested++;

                if (refine)
                {
                    double maxAngle = Math.PI / 12.0;
                    int steps = Math.Max(3, angleSteps / 4);
                    for (int ax = -steps; ax <= steps; ax++)
                    for (int ay = -steps; ay <= steps; ay++)
                    for (int az = -steps; az <= steps; az++)
                    {
                        if (ax == 0 && ay == 0 && az == 0) continue;
                        double rx = maxAngle * ax / steps;
                        double ry = maxAngle * ay / steps;
                        double rz = maxAngle * az / steps;
                        Plane candidatePlane = RotatePlaneLocal(pca, rx, ry, rz);
                        Candidate c = EvaluatePlane(points, candidatePlane);
                        tested++;
                        if (c.Volume < best.Volume) best = c;
                    }
                }
            }

            if (refine && mode == 0)
            {
                double baseAngle = Math.Atan2(best.Box.Plane.XAxis.Y, best.Box.Plane.XAxis.X);
                double span = Math.PI / Math.Max(8, angleSteps);
                for (int i = -angleSteps; i <= angleSteps; i++)
                {
                    double a = baseAngle + span * i / angleSteps;
                    Candidate c = EvaluatePlane(points, RotatedWorldZPlane(a));
                    tested++;
                    if (c.Volume < best.Volume) best = c;
                }
            }

            double worldVolume = Math.Max(worldBox.X.Length, 0) * Math.Max(worldBox.Y.Length, 0) * Math.Max(worldBox.Z.Length, 0);
            double saving = worldVolume > RhinoMath.ZeroTolerance ? 100.0 * (1.0 - best.Volume / worldVolume) : 0.0;

            Vector3d dims = new Vector3d(best.Box.X.Length, best.Box.Y.Length, best.Box.Z.Length);

            Message = _versionTag + " | " + (mode == 0 ? "Z" : "3D");
            DA.SetData(0, best.Box);
            DA.SetData(1, best.Box.Plane);
            DA.SetData(2, best.Volume);
            DA.SetData(3, dims);
            DA.SetData(4, $"mode={(mode == 0 ? "WorldZ" : "PCA3D")}, points={points.Count}, candidates={tested}, volume={best.Volume:F3}, world_volume={worldVolume:F3}, saving={saving:F1}%");
        }

        private static List<Point3d> CollectPoints(IEnumerable<GeometryBase> geometry, int maxSamplesPerItem)
        {
            var points = new List<Point3d>();
            if (geometry == null) return points;

            foreach (GeometryBase g in geometry)
            {
                if (g == null || !g.IsValid) continue;
                var local = new List<Point3d>();

                if (g is Rhino.Geometry.Point point)
                {
                    local.Add(point.Location);
                }
                else if (g is PointCloud cloud)
                {
                    int step = Math.Max(1, cloud.Count / maxSamplesPerItem);
                    for (int i = 0; i < cloud.Count; i += step)
                        local.Add(cloud[i].Location);
                }
                else if (g is Mesh mesh)
                {
                    int step = Math.Max(1, mesh.Vertices.Count / maxSamplesPerItem);
                    for (int i = 0; i < mesh.Vertices.Count; i += step)
                        local.Add(mesh.Vertices[i]);
                }
                else if (g is Curve curve)
                {
                    int count = Math.Max(8, Math.Min(maxSamplesPerItem, 256));
                    for (int i = 0; i <= count; i++)
                        local.Add(curve.PointAtNormalizedLength((double)i / count));
                }
                else
                {
                    Brep brep = null;
                    if (g is Brep b) brep = b;
                    else if (g is Surface s) brep = Brep.CreateFromSurface(s);
                    else if (g is Extrusion e) brep = e.ToBrep();

                    if (brep != null && brep.IsValid)
                    {
                        Mesh[] meshes = Mesh.CreateFromBrep(brep, MeshingParameters.FastRenderMesh);
                        if (meshes != null && meshes.Length > 0)
                        {
                            foreach (Mesh m in meshes)
                            {
                                int step = Math.Max(1, m.Vertices.Count / Math.Max(1, maxSamplesPerItem / meshes.Length));
                                for (int i = 0; i < m.Vertices.Count; i += step)
                                    local.Add(m.Vertices[i]);
                            }
                        }
                    }
                }

                if (local.Count == 0)
                {
                    BoundingBox bb = g.GetBoundingBox(true);
                    if (bb.IsValid) local.AddRange(bb.GetCorners());
                }

                points.AddRange(local.Where(p => p.IsValid));
            }

            return points;
        }

        private static Candidate EvaluatePlane(List<Point3d> points, Plane plane)
        {
            double minX = double.PositiveInfinity, minY = double.PositiveInfinity, minZ = double.PositiveInfinity;
            double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity, maxZ = double.NegativeInfinity;

            foreach (Point3d p in points)
            {
                Vector3d d = p - plane.Origin;
                double x = d * plane.XAxis;
                double y = d * plane.YAxis;
                double z = d * plane.ZAxis;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
                if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
            }

            var box = new Box(plane, new Interval(minX, maxX), new Interval(minY, maxY), new Interval(minZ, maxZ));
            double volume = Math.Max(0, maxX - minX) * Math.Max(0, maxY - minY) * Math.Max(0, maxZ - minZ);
            return new Candidate(box, volume);
        }

        private static Plane RotatedWorldZPlane(double angle)
        {
            Transform rot = Transform.Rotation(angle, Vector3d.ZAxis, Point3d.Origin);
            Vector3d x = Vector3d.XAxis;
            Vector3d y = Vector3d.YAxis;
            x.Transform(rot);
            y.Transform(rot);
            return new Plane(Point3d.Origin, x, y);
        }

        private static Plane BuildPcaPlane(List<Point3d> points)
        {
            Point3d c = Centroid(points);
            double xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (Point3d p in points)
            {
                double x = p.X - c.X;
                double y = p.Y - c.Y;
                double z = p.Z - c.Z;
                xx += x * x; xy += x * y; xz += x * z;
                yy += y * y; yz += y * z; zz += z * z;
            }

            Vector3d axisX = PowerEigen(xx, xy, xz, yy, yz, zz, Vector3d.XAxis);
            Vector3d axisY = PowerEigen(xx, xy, xz, yy, yz, zz, Vector3d.YAxis);
            axisY = axisY - (axisY * axisX) * axisX;
            if (!axisY.Unitize())
            {
                axisY = Vector3d.CrossProduct(Vector3d.ZAxis, axisX);
                if (!axisY.Unitize()) axisY = Vector3d.YAxis;
            }

            Vector3d axisZ = Vector3d.CrossProduct(axisX, axisY);
            if (!axisZ.Unitize()) axisZ = Vector3d.ZAxis;
            axisY = Vector3d.CrossProduct(axisZ, axisX);
            axisY.Unitize();

            return new Plane(c, axisX, axisY);
        }

        private static Vector3d PowerEigen(double xx, double xy, double xz, double yy, double yz, double zz, Vector3d seed)
        {
            Vector3d v = seed;
            if (!v.Unitize()) v = Vector3d.XAxis;
            for (int i = 0; i < 32; i++)
            {
                Vector3d n = new Vector3d(
                    xx * v.X + xy * v.Y + xz * v.Z,
                    xy * v.X + yy * v.Y + yz * v.Z,
                    xz * v.X + yz * v.Y + zz * v.Z);
                if (!n.Unitize()) break;
                v = n;
            }
            return v;
        }

        private static Plane RotatePlaneLocal(Plane basePlane, double rx, double ry, double rz)
        {
            Plane p = basePlane;
            Transform tx = Transform.Rotation(rx, p.XAxis, p.Origin);
            Transform ty = Transform.Rotation(ry, p.YAxis, p.Origin);
            Transform tz = Transform.Rotation(rz, p.ZAxis, p.Origin);
            p.Transform(tx);
            p.Transform(ty);
            p.Transform(tz);
            return p;
        }

        private static Point3d Centroid(List<Point3d> points)
        {
            double x = 0, y = 0, z = 0;
            foreach (Point3d p in points)
            {
                x += p.X; y += p.Y; z += p.Z;
            }
            double inv = 1.0 / points.Count;
            return new Point3d(x * inv, y * inv, z * inv);
        }

        private static BoundingBox BoundingBoxFromPoints(IEnumerable<Point3d> points)
        {
            BoundingBox bb = BoundingBox.Empty;
            foreach (Point3d p in points) bb.Union(p);
            return bb;
        }

        private static Box BoundingBoxToBox(BoundingBox bb, Plane plane)
        {
            return new Box(plane, new Interval(bb.Min.X, bb.Max.X), new Interval(bb.Min.Y, bb.Max.Y), new Interval(bb.Min.Z, bb.Max.Z));
        }

        private static Candidate BestOf(Candidate a, Candidate b)
        {
            return b.Volume < a.Volume ? b : a;
        }

        private sealed class Candidate
        {
            public readonly Box Box;
            public readonly double Volume;

            public Candidate(Box box, double volume)
            {
                Box = box;
                Volume = volume;
            }
        }
    }
}
