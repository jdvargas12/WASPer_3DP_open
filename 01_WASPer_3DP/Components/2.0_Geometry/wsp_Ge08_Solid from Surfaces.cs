#region Component Description
/*
Component: wsp_Ge08_Solid from Surfaces
Nickname: Solid Bound Geo
Category: WASPer_3DP
SubCategory: 2.0_Geometry

GENERAL DESCRIPTION
From a list of trimmed surfaces, the component:
- Sorts caps along a robust axis.
- Outputs the sorted caps as Breps, one trimmed face each.
- Builds closed solids between each consecutive pair.
- Builds a single shell_solid between the two outermost caps.
- Builds shell_open as the walls only, removing near-horizontal faces.
- Collects interior partition caps, merging coplanar duplicates.
- Reports whether each consecutive solid is box-like.

Inputs:
0) srfs_caps : List<Surface>
   Trimmed cap surfaces. Order can be arbitrary.

Outputs:
0) caps : List<Brep>
   Sorted cap Breps.

1) solids : List<Brep>
   Closed Breps between consecutive sorted caps.

2) solid_box_bool : List<bool>
   True for each solid that is box-like.

3) shell_solid : Brep
   Closed shell between the two outermost sorted caps.

4) shell_open : Brep
   Open wall-only shell.

5) partitions : List<Brep>
   Unique interior cap partitions.
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
    public class wsp_Ge08_Solid_From_Surfaces : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Ge08_Solid_From_Surfaces()
          : base(
              "wsp_Ge08_Solid from Surfaces",
              "Solid Bound Geo",
              "Sorted caps + closed solids between consecutive caps + shell_solid + shell_open walls + unique partitions + box-like flags.",
              global::WASPer_3DP.WASPerPalette.DesignFabrication,
              "2.0_Geometry")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("48A1422C-4571-4A8C-A780-87B98F0419F8");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ge08_Solid from Surfaces.png"))
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
            pManager.AddSurfaceParameter(
                "srfs_caps",
                "srfs_caps",
                "Trimmed cap surfaces. Order can be arbitrary.",
                GH_ParamAccess.list);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter("caps", "caps", "Sorted cap Breps.", GH_ParamAccess.list);
            pManager.AddBrepParameter("solids", "solids", "Closed Breps between consecutive sorted caps.", GH_ParamAccess.list);
            pManager.AddBooleanParameter("solid_box_bool", "box", "True when the corresponding solid is box-like.", GH_ParamAccess.list);
            pManager.AddBrepParameter("shell_solid", "shell_solid", "Closed shell between the two outermost sorted caps.", GH_ParamAccess.item);
            pManager.AddBrepParameter("shell_open", "shell_open", "Open wall-only shell.", GH_ParamAccess.item);
            pManager.AddBrepParameter("partitions", "partitions", "Unique interior cap partitions.", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            var srfsCaps = new List<Surface>();
            DA.GetDataList(0, srfsCaps);

            var outCaps = new List<Brep>();
            var outSolids = new List<Brep>();
            var outFlags = new List<bool>();
            var outParts = new List<Brep>();
            Brep outShellSolid = null;
            Brep outShellOpen = null;

            double tol = RhinoDoc.ActiveDoc != null ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 1e-6;

            if (srfsCaps == null || srfsCaps.Count < 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Provide at least 2 surfaces in srfs_caps.");
                SetOutputs(DA, outCaps, outSolids, outFlags, outShellSolid, outShellOpen, outParts);
                return;
            }

            srfsCaps = srfsCaps.Where(s => s != null && s.IsValid).ToList();
            if (srfsCaps.Count < 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "At least 2 valid surfaces are required.");
                SetOutputs(DA, outCaps, outSolids, outFlags, outShellSolid, outShellOpen, outParts);
                return;
            }

            List<int> order = SortSurfacesAlongAxis(srfsCaps, tol);

            for (int k = 0; k < order.Count; k++)
            {
                Brep cap = MakeCap(srfsCaps[order[k]]);
                if (cap != null) outCaps.Add(cap);
            }

            for (int k = 0; k < order.Count - 1; k++)
            {
                Surface sA = srfsCaps[order[k]];
                Surface sB = srfsCaps[order[k + 1]];

                Brep b = BuildSolidBetweenSurfacesUsingEdges(sA, sB, tol);
                if (b != null)
                {
                    outSolids.Add(b);
                    outFlags.Add(IsBoxLike(b, tol, RhinoMath.ToRadians(2.0)));
                }
            }

            outShellSolid = BuildSolidBetweenSurfacesUsingEdges(srfsCaps[order[0]], srfsCaps[order[order.Count - 1]], tol);
            outShellOpen = RemoveHorizontalFacesFromBrep(outShellSolid, 0.7, tol);
            outParts = UniquePartitionCaps(srfsCaps, order, tol);

            SetOutputs(DA, outCaps, outSolids, outFlags, outShellSolid, outShellOpen, outParts);
        }

        private static void SetOutputs(
            IGH_DataAccess da,
            List<Brep> caps,
            List<Brep> solids,
            List<bool> flags,
            Brep shellSolid,
            Brep shellOpen,
            List<Brep> partitions)
        {
            da.SetDataList(0, caps);
            da.SetDataList(1, solids);
            da.SetDataList(2, flags);
            da.SetData(3, shellSolid);
            da.SetData(4, shellOpen);
            da.SetDataList(5, partitions);
        }

        private static List<int> SortSurfacesAlongAxis(List<Surface> srfs, double tol)
        {
            int n = srfs.Count;
            var centers = new Point3d[n];
            var normals = new List<Vector3d>();

            for (int i = 0; i < n; i++)
            {
                Brep cap = MakeCap(srfs[i]);
                if (cap == null)
                {
                    centers[i] = Point3d.Unset;
                    continue;
                }

                var amp = AreaMassProperties.Compute(cap);
                centers[i] = amp != null ? amp.Centroid : cap.GetBoundingBox(true).Center;

                Plane pl;
                if (TryGetAnyPlane(cap, srfs[i], out pl) && pl.IsValid && !pl.Normal.IsZero)
                {
                    var nrm = pl.Normal;
                    nrm.Unitize();
                    normals.Add(nrm);
                }
            }

            Vector3d axis = Vector3d.Unset;

            if (normals.Count >= 1)
            {
                Vector3d refN = normals[0];
                Vector3d sum = Vector3d.Zero;

                for (int i = 0; i < normals.Count; i++)
                {
                    var v = normals[i];
                    if (Vector3d.Multiply(v, refN) < 0) v = -v;
                    sum += v;
                }

                if (!sum.IsZero)
                {
                    sum.Unitize();
                    axis = sum;
                }
            }

            if (!axis.IsValid || axis.IsTiny(tol))
                axis = WidestCentroidAxis(centers);

            var pairs = new List<Tuple<int, double>>(n);
            for (int i = 0; i < n; i++)
            {
                var c = centers[i];
                double t = c.IsValid ? c.X * axis.X + c.Y * axis.Y + c.Z * axis.Z : 0.0;
                pairs.Add(Tuple.Create(i, t));
            }

            pairs.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            return pairs.Select(p => p.Item1).ToList();
        }

        private static Vector3d WidestCentroidAxis(Point3d[] centers)
        {
            double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
            double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
            double minZ = double.PositiveInfinity, maxZ = double.NegativeInfinity;

            foreach (var c in centers)
            {
                if (!c.IsValid) continue;
                minX = Math.Min(minX, c.X); maxX = Math.Max(maxX, c.X);
                minY = Math.Min(minY, c.Y); maxY = Math.Max(maxY, c.Y);
                minZ = Math.Min(minZ, c.Z); maxZ = Math.Max(maxZ, c.Z);
            }

            double dx = maxX - minX, dy = maxY - minY, dz = maxZ - minZ;
            if (dx >= dy && dx >= dz) return Vector3d.XAxis;
            if (dy >= dx && dy >= dz) return Vector3d.YAxis;
            return Vector3d.ZAxis;
        }

        private static bool TryGetAnyPlane(Brep cap, Surface srf, out Plane pl)
        {
            pl = Plane.Unset;

            try
            {
                if (cap != null && cap.Faces.Count > 0 && cap.Faces[0].TryGetPlane(out pl))
                    return true;
            }
            catch { pl = Plane.Unset; }

            try
            {
                if (srf != null && srf.TryGetPlane(out pl))
                    return true;
            }
            catch { pl = Plane.Unset; }

            return false;
        }

        private static List<Brep> UniquePartitionCaps(List<Surface> srfs, List<int> order, double tol)
        {
            var res = new List<Brep>();
            if (order == null || order.Count < 3) return res;

            var seenPlanes = new List<Plane>();
            double angTol = RhinoMath.ToRadians(1.5);

            for (int k = 1; k < order.Count - 1; k++)
            {
                int i = order[k];
                var cap = MakeCap(srfs[i]);
                if (cap == null) continue;

                Plane pl;
                bool hasPlane = TryGetAnyPlane(cap, srfs[i], out pl);

                if (!hasPlane || !pl.IsValid || pl.Normal.IsZero)
                {
                    res.Add(cap);
                    continue;
                }

                var n = pl.Normal;
                n.Unitize();
                if (n.Z < 0) pl.Flip();

                bool isNew = true;
                for (int j = 0; j < seenPlanes.Count; j++)
                {
                    if (AreCoplanar(seenPlanes[j], pl, tol, angTol))
                    {
                        isNew = false;
                        break;
                    }
                }

                if (isNew)
                {
                    seenPlanes.Add(pl);
                    res.Add(cap);
                }
            }

            return res;
        }

        private static bool AreCoplanar(Plane a, Plane b, double distTol, double angTolRad)
        {
            if (!a.IsValid || !b.IsValid) return false;

            var na = a.Normal;
            var nb = b.Normal;
            if (na.IsZero || nb.IsZero) return false;
            na.Unitize();
            nb.Unitize();

            double dot = Math.Abs(na * nb);
            if (dot < Math.Cos(angTolRad)) return false;

            double dist = Math.Abs((b.Origin - a.Origin) * na);
            return dist <= Math.Max(distTol * 5.0, RhinoMath.ZeroTolerance);
        }

        private static Brep BuildSolidBetweenSurfacesUsingEdges(Surface sA, Surface sB, double tol)
        {
            if (sA == null || sB == null) return null;

            Brep domainSolid = BuildSolidBetweenSurfacesUsingDomains(sA, sB, tol);
            if (domainSolid != null && domainSolid.IsValid)
                return domainSolid;

            Brep capA = MakeCap(sA);
            Brep capB = MakeCap(sB);
            if (capA == null || capB == null) return null;

            List<Curve> eA;
            List<Point3d> vA;
            List<Curve> eB;
            List<Point3d> vB;
            if (!OuterEdgesAndCorners(capA.Faces[0], tol, out eA, out vA)) return null;
            if (!OuterEdgesAndCorners(capB.Faces[0], tol, out eB, out vB)) return null;

            if (vA.Count != vB.Count || vA.Count < 3)
            {
                Brep loopSolid = CleanBrep(BuildSolidByLoopLoft(capA, capB, tol), tol);
                if (loopSolid != null && loopSolid.IsValid)
                {
                    OrientSolidOutward(loopSolid);
                    return loopSolid;
                }

                return BuildSolidBetweenSurfacesUsingMeshTopology(sA, sB, tol);
            }

            OrientLoopByPlane(capA.Faces[0], eA, vA);
            OrientLoopByPlane(capB.Faces[0], eB, vB);

            int shift;
            bool useReverse;
            Vector3d capAxis = AveragePoint(vB) - AveragePoint(vA);
            AlignBoundaryLoops(eA, vA, eB, vB, capAxis, out useReverse, out shift);
            ApplyLoopAlignment(ref eB, ref vB, useReverse, shift);

            for (int i = 0; i < eA.Count; i++)
            {
                if (eA[i].PointAtStart.DistanceTo(vA[i]) > tol) eA[i].Reverse();
                if (eB[i].PointAtStart.DistanceTo(vB[i]) > tol) eB[i].Reverse();
            }

            var walls = new List<Brep>();
            for (int i = 0; i < eA.Count; i++)
            {
                var side = Brep.CreateFromLoft(
                    new Curve[] { eA[i], eB[i] },
                    Point3d.Unset,
                    Point3d.Unset,
                    LoftType.Straight,
                    false);

                if (side != null && side.Length > 0 && side[0] != null)
                    walls.Add(side[0]);
            }

            var parts = new List<Brep> { capA, capB };
            parts.AddRange(walls);

            Brep joined = null;
            var j = Brep.JoinBreps(parts, tol);
            if (j != null && j.Length > 0) joined = j[0];
            else joined = Brep.MergeBreps(parts, tol);

            joined = CleanBrep(joined, tol);
            if (joined != null && joined.IsValid)
            {
                OrientSolidOutward(joined);
                return joined;
            }

            return BuildSolidBetweenSurfacesUsingMeshTopology(sA, sB, tol);
        }

        private static Brep BuildSolidBetweenSurfacesUsingDomains(Surface sA, Surface sB, double tol)
        {
            Brep capA = MakeCap(sA);
            Brep capB = MakeCap(sB);
            if (capA == null || capB == null) return null;

            SurfaceBoundaryData dataA;
            if (!TryBuildSurfaceBoundaryData(sA, 0, out dataA)) return null;

            int bestMode = FindBestSurfaceBoundaryMode(dataA, sB);
            SurfaceBoundaryData dataB;
            if (!TryBuildSurfaceBoundaryData(sB, bestMode, out dataB)) return null;

            var walls = new List<Brep>();
            for (int i = 0; i < 4; i++)
            {
                var side = Brep.CreateFromLoft(
                    new Curve[] { dataA.Edges[i], dataB.Edges[i] },
                    Point3d.Unset,
                    Point3d.Unset,
                    LoftType.Straight,
                    false);

                if (side != null && side.Length > 0 && side[0] != null)
                    walls.Add(side[0]);
            }

            if (walls.Count != 4) return null;

            var parts = new List<Brep> { capA, capB };
            parts.AddRange(walls);

            Brep joined = null;
            var j = Brep.JoinBreps(parts, tol);
            if (j != null && j.Length > 0) joined = j[0];
            else joined = Brep.MergeBreps(parts, tol);

            joined = CleanBrep(joined, tol);
            if (joined != null && joined.IsValid)
            {
                OrientSolidOutward(joined);
                return joined;
            }

            return null;
        }

        private struct SurfaceBoundaryData
        {
            public List<Point3d> Corners;
            public List<Curve> Edges;
        }

        private static int FindBestSurfaceBoundaryMode(SurfaceBoundaryData reference, Surface target)
        {
            int bestMode = 0;
            double bestScore = double.PositiveInfinity;

            for (int mode = 0; mode < 8; mode++)
            {
                SurfaceBoundaryData test;
                if (!TryBuildSurfaceBoundaryData(target, mode, out test)) continue;

                double score = 0.0;
                for (int i = 0; i < 4; i++)
                    score += reference.Corners[i].DistanceToSquared(test.Corners[i]);

                if (score < bestScore)
                {
                    bestScore = score;
                    bestMode = mode;
                }
            }

            return bestMode;
        }

        private static bool TryBuildSurfaceBoundaryData(Surface s, int mode, out SurfaceBoundaryData data)
        {
            data = new SurfaceBoundaryData
            {
                Corners = new List<Point3d>(),
                Edges = new List<Curve>()
            };

            if (s == null || !s.IsValid) return false;

            var uv = BoundaryUvLoop(s, mode);
            if (uv == null || uv.Length != 4) return false;

            for (int i = 0; i < 4; i++)
            {
                Point3d p = s.PointAt(uv[i].X, uv[i].Y);
                if (!p.IsValid) return false;
                data.Corners.Add(p);
            }

            for (int i = 0; i < 4; i++)
            {
                Point2d a = uv[i];
                Point2d b = uv[(i + 1) % 4];
                Curve edge = SurfaceEdgeCurve(s, a, b);
                if (edge == null || !edge.IsValid) return false;
                data.Edges.Add(edge);
            }

            return data.Corners.Count == 4 && data.Edges.Count == 4;
        }

        private static Point2d[] BoundaryUvLoop(Surface s, int mode)
        {
            Interval u = s.Domain(0);
            Interval v = s.Domain(1);

            Point2d p00 = new Point2d(u.T0, v.T0);
            Point2d p10 = new Point2d(u.T1, v.T0);
            Point2d p11 = new Point2d(u.T1, v.T1);
            Point2d p01 = new Point2d(u.T0, v.T1);

            switch (mode)
            {
                case 1: return new[] { p10, p11, p01, p00 };
                case 2: return new[] { p11, p01, p00, p10 };
                case 3: return new[] { p01, p00, p10, p11 };
                case 4: return new[] { p00, p01, p11, p10 };
                case 5: return new[] { p01, p11, p10, p00 };
                case 6: return new[] { p11, p10, p00, p01 };
                case 7: return new[] { p10, p00, p01, p11 };
                default: return new[] { p00, p10, p11, p01 };
            }
        }

        private static Curve SurfaceEdgeCurve(Surface s, Point2d a, Point2d b)
        {
            const double eps = 1e-12;

            if (Math.Abs(a.Y - b.Y) <= eps)
            {
                Curve c = s.IsoCurve(0, a.Y);
                if (c != null && a.X > b.X) c.Reverse();
                return c;
            }

            if (Math.Abs(a.X - b.X) <= eps)
            {
                Curve c = s.IsoCurve(1, a.X);
                if (c != null && a.Y > b.Y) c.Reverse();
                return c;
            }

            return new LineCurve(s.PointAt(a.X, a.Y), s.PointAt(b.X, b.Y));
        }

        private static Brep BuildSolidBetweenSurfacesUsingMeshTopology(Surface sA, Surface sB, double tol)
        {
            Mesh mA = SurfaceCapToMesh(sA, tol);
            Mesh mB = SurfaceCapToMesh(sB, tol);
            if (mA == null || !mA.IsValid || mB == null || !mB.IsValid) return null;

            Mesh donor = IsBetterTopologyDonor(mA, mB) ? mA : mB;

            Mesh cA = ReferenceEquals(donor, mA)
                ? donor.DuplicateMesh()
                : ConformTopologyClosest(donor, mA);

            Mesh cB = ReferenceEquals(donor, mB)
                ? donor.DuplicateMesh()
                : ConformTopologyClosest(donor, mB);

            if (cA == null || !cA.IsValid || cB == null || !cB.IsValid) return null;

            Mesh solidMesh = BuildShellClosed(cA, cB);
            if (solidMesh == null || !solidMesh.IsValid) return null;

            Brep b = null;
            try { b = Brep.CreateFromMesh(solidMesh, true); }
            catch { b = null; }

            b = CleanBrep(b, tol);
            OrientSolidOutward(b);
            return b;
        }

        private static Mesh SurfaceCapToMesh(Surface s, double tol)
        {
            Brep cap = MakeCap(s);
            if (cap == null || !cap.IsValid) return null;

            var mp = new MeshingParameters
            {
                JaggedSeams = false,
                RefineGrid = true,
                SimplePlanes = false,
                MinimumEdgeLength = Math.Max(tol * 2.0, RhinoMath.ZeroTolerance),
                MaximumEdgeLength = 0.0,
                GridMinCount = 8
            };

            Mesh[] meshes = null;
            try { meshes = Mesh.CreateFromBrep(cap, mp); }
            catch { meshes = null; }

            if (meshes == null || meshes.Length == 0) return null;

            var result = new Mesh();
            foreach (var m in meshes)
            {
                if (m == null || !m.IsValid) continue;
                result.Append(m);
            }

            result.Vertices.CombineIdentical(true, true);
            result.Normals.ComputeNormals();
            result.Compact();

            return result.IsValid && result.Vertices.Count >= 3 && result.Faces.Count > 0 ? result : null;
        }

        private static bool IsBetterTopologyDonor(Mesh a, Mesh b)
        {
            int av = a != null ? a.Vertices.Count : -1;
            int bv = b != null ? b.Vertices.Count : -1;
            if (av != bv) return av > bv;

            int af = a != null ? a.Faces.Count : -1;
            int bf = b != null ? b.Faces.Count : -1;
            if (af != bf) return af > bf;

            int ae = 0;
            int be = 0;
            try { ae = GetNakedEdges(a).Count; } catch { }
            try { be = GetNakedEdges(b).Count; } catch { }
            return ae >= be;
        }

        private static Mesh ConformTopologyClosest(Mesh sourceMesh, Mesh targetMesh)
        {
            if (sourceMesh == null || targetMesh == null) return null;

            Mesh source = sourceMesh.DuplicateMesh();
            source.Normals.ComputeNormals();

            Mesh target = targetMesh.DuplicateMesh();
            target.Normals.ComputeNormals();

            Point3d[] verts = source.Vertices.ToPoint3dArray();
            for (int i = 0; i < verts.Length; i++)
            {
                MeshPoint mp = target.ClosestMeshPoint(verts[i], 0.0);
                if (mp != null) verts[i] = target.PointAt(mp);
            }

            Mesh result = source.DuplicateMesh();
            for (int i = 0; i < verts.Length; i++)
                result.Vertices.SetVertex(i, verts[i]);

            result.Normals.ComputeNormals();
            result.Compact();
            return result;
        }

        private static Mesh BuildShellClosed(Mesh meshA, Mesh meshB)
        {
            if (meshA == null || meshB == null) return null;
            if (meshA.Vertices.Count != meshB.Vertices.Count) return null;

            Mesh vol = new Mesh();
            int offset = meshA.Vertices.Count;

            foreach (Point3f v in meshA.Vertices) vol.Vertices.Add(v);
            foreach (Point3f v in meshB.Vertices) vol.Vertices.Add(v);

            foreach (MeshFace f in meshA.Faces)
            {
                if (f.IsQuad) vol.Faces.AddFace(f.A, f.B, f.C, f.D);
                else vol.Faces.AddFace(f.A, f.B, f.C);
            }

            foreach (MeshFace f in meshB.Faces)
            {
                if (f.IsQuad) vol.Faces.AddFace(f.A + offset, f.D + offset, f.C + offset, f.B + offset);
                else vol.Faces.AddFace(f.A + offset, f.C + offset, f.B + offset);
            }

            var loopA = GetOrderedBoundaryLoop(meshA);
            var loopB = GetOrderedBoundaryLoop(meshB);

            if (loopA != null && loopB != null && loopA.Count == loopB.Count)
            {
                for (int i = 0; i < loopA.Count; i++)
                {
                    int next = (i + 1) % loopA.Count;
                    vol.Faces.AddFace(
                        loopA[i],
                        loopA[next],
                        loopB[next] + offset,
                        loopB[i] + offset);
                }
            }
            else
            {
                var nakedEdges = GetNakedEdges(meshA);
                foreach (var edge in nakedEdges)
                    vol.Faces.AddFace(edge.Item1, edge.Item2, edge.Item2 + offset, edge.Item1 + offset);
            }

            vol.Normals.ComputeNormals();
            vol.UnifyNormals();
            vol.Compact();
            return vol;
        }

        private static List<int> GetOrderedBoundaryLoop(Mesh mesh)
        {
            var nakedEdges = GetNakedEdges(mesh);
            if (nakedEdges.Count == 0) return null;

            var adj = new Dictionary<int, List<int>>();
            foreach (var e in nakedEdges)
            {
                if (!adj.ContainsKey(e.Item1)) adj[e.Item1] = new List<int>();
                if (!adj.ContainsKey(e.Item2)) adj[e.Item2] = new List<int>();
                adj[e.Item1].Add(e.Item2);
                adj[e.Item2].Add(e.Item1);
            }

            int start = adj.Keys.First();
            int current = start;
            int previous = -1;
            var loop = new List<int>();
            var guard = 0;

            while (guard++ < adj.Count + 2)
            {
                loop.Add(current);

                var neighbors = adj[current];
                int next = -1;
                foreach (int n in neighbors)
                {
                    if (n != previous)
                    {
                        next = n;
                        break;
                    }
                }

                if (next < 0 || next == start) break;

                previous = current;
                current = next;
            }

            return loop.Count > 2 ? loop : null;
        }

        private static List<Tuple<int, int>> GetNakedEdges(Mesh mesh)
        {
            var naked = new List<Tuple<int, int>>();
            if (mesh == null || !mesh.IsValid) return naked;

            var edgeStore = new Dictionary<string, Tuple<int, int>>();
            var edgeUse = new Dictionary<string, int>();

            foreach (MeshFace f in mesh.Faces)
            {
                int[] fv = f.IsQuad
                    ? new[] { f.A, f.B, f.C, f.D }
                    : new[] { f.A, f.B, f.C };

                for (int i = 0; i < fv.Length; i++)
                {
                    int v0 = fv[i];
                    int v1 = fv[(i + 1) % fv.Length];
                    int lo = Math.Min(v0, v1);
                    int hi = Math.Max(v0, v1);
                    string key = lo + "_" + hi;

                    if (!edgeStore.ContainsKey(key))
                    {
                        edgeStore[key] = Tuple.Create(v0, v1);
                        edgeUse[key] = 0;
                    }

                    edgeUse[key]++;
                }
            }

            foreach (var kvp in edgeUse)
                if (kvp.Value == 1)
                    naked.Add(edgeStore[kvp.Key]);

            return naked;
        }

        private static Brep RemoveHorizontalFacesFromBrep(Brep b, double dotThreshold, double tol)
        {
            if (b == null || !b.IsValid) return null;

            Vector3d worldZ = Vector3d.ZAxis;
            var keep = new List<Brep>();

            for (int i = 0; i < b.Faces.Count; i++)
            {
                var f = b.Faces[i];
                if (f == null) continue;

                Vector3d n;
                Plane pl;
                if (f.TryGetPlane(out pl))
                {
                    n = pl.Normal;
                }
                else
                {
                    var du = f.Domain(0);
                    var dv = f.Domain(1);
                    n = f.NormalAt(0.5 * (du.T0 + du.T1), 0.5 * (dv.T0 + dv.T1));
                }

                if (!n.IsValid || n.IsZero) continue;
                n.Unitize();

                double dot = Math.Abs(Vector3d.Multiply(n, worldZ));
                if (dot < dotThreshold)
                {
                    var faceBrep = f.DuplicateFace(true);
                    if (faceBrep != null && faceBrep.IsValid)
                        keep.Add(faceBrep);
                }
            }

            if (keep.Count == 0) return null;

            Brep joined = null;
            var j = Brep.JoinBreps(keep, tol);
            if (j != null && j.Length > 0) joined = j[0];
            else joined = Brep.MergeBreps(keep, tol);

            return CleanBrep(joined, tol);
        }

        private static Brep BuildSolidByLoopLoft(Brep capA, Brep capB, double tol)
        {
            Curve loopA = JoinLoopAsCurve(capA.Faces[0].OuterLoop, tol);
            Curve loopB = JoinLoopAsCurve(capB.Faces[0].OuterLoop, tol);
            if (loopA == null || loopB == null) return null;

            AlignClosedPair(loopA, loopB, tol);

            var loft = Brep.CreateFromLoft(
                new Curve[] { loopA, loopB },
                Point3d.Unset,
                Point3d.Unset,
                LoftType.Normal,
                false);

            if (loft == null || loft.Length == 0 || loft[0] == null) return null;

            var parts = new List<Brep> { capA, capB, loft[0] };
            var j = Brep.JoinBreps(parts, tol);
            return j != null && j.Length > 0 ? j[0] : Brep.MergeBreps(parts, tol);
        }

        private static Brep MakeCap(Surface s)
        {
            var b = s == null ? null : Brep.CreateFromSurface(s);
            if (b == null || b.Faces.Count == 0) return null;

            var cap = b.Faces[0].DuplicateFace(true);
            try { cap.Faces.ShrinkFaces(); } catch { }
            return cap;
        }

        private static bool OuterEdgesAndCorners(BrepFace face, double tol, out List<Curve> edges, out List<Point3d> corners)
        {
            edges = new List<Curve>();
            corners = new List<Point3d>();
            if (face == null || face.OuterLoop == null) return false;

            foreach (var t in face.OuterLoop.Trims)
            {
                var e = t.Edge;
                if (e == null) continue;

                bool rev = t.IsReversed();
                var vtx = rev ? e.EndVertex : e.StartVertex;
                if (vtx == null) continue;

                Point3d corner = vtx.Location;
                var c = e.DuplicateCurve();
                if (c == null || !c.IsValid) continue;
                if (rev) c.Reverse();

                if (c.PointAtStart.DistanceTo(corner) > tol)
                    c.Reverse();

                corners.Add(corner);
                edges.Add(c);
            }

            return edges.Count >= 3 && corners.Count == edges.Count;
        }

        private static void OrientLoopByPlane(BrepFace face, List<Curve> edges, List<Point3d> corners)
        {
            if (face == null || edges == null || corners == null) return;
            if (edges.Count != corners.Count || corners.Count < 3) return;

            Plane pl;
            if (!face.TryGetPlane(out pl) || !pl.IsValid) return;

            double area = SignedAreaOnPlane(corners, pl);
            if (area < 0.0)
            {
                ReverseLoop(edges, corners);
            }
        }

        private static double SignedAreaOnPlane(List<Point3d> pts, Plane pl)
        {
            if (pts == null || pts.Count < 3 || !pl.IsValid) return 0.0;

            double sum = 0.0;
            for (int i = 0; i < pts.Count; i++)
            {
                Point3d a = pts[i];
                Point3d b = pts[(i + 1) % pts.Count];

                Vector3d ra = a - pl.Origin;
                Vector3d rb = b - pl.Origin;

                double ax = ra * pl.XAxis;
                double ay = ra * pl.YAxis;
                double bx = rb * pl.XAxis;
                double by = rb * pl.YAxis;

                sum += ax * by - bx * ay;
            }

            return 0.5 * sum;
        }

        private static void AlignBoundaryLoops(
            List<Curve> edgesA,
            List<Point3d> cornersA,
            List<Curve> edgesB,
            List<Point3d> cornersB,
            Vector3d capAxis,
            out bool useReverse,
            out int shift)
        {
            useReverse = false;
            shift = 0;

            int n = cornersA != null ? cornersA.Count : 0;
            if (n == 0 || cornersB == null || cornersB.Count != n)
                return;

            double best = double.PositiveInfinity;
            if (!capAxis.IsZero) capAxis.Unitize();

            for (int reverse = 0; reverse <= 1; reverse++)
            {
                var testCorners = new List<Point3d>(cornersB);
                var testEdges = new List<Curve>(edgesB);
                if (reverse == 1) ReverseLoop(testEdges, testCorners);

                for (int s = 0; s < n; s++)
                {
                    double score = LoopAlignmentScore(edgesA, cornersA, testEdges, testCorners, s, capAxis);
                    if (score < best)
                    {
                        best = score;
                        useReverse = reverse == 1;
                        shift = s;
                    }
                }
            }
        }

        private static double LoopAlignmentScore(
            List<Curve> edgesA,
            List<Point3d> cornersA,
            List<Curve> edgesB,
            List<Point3d> cornersB,
            int shift,
            Vector3d capAxis)
        {
            int n = cornersA.Count;
            double score = 0.0;

            double diag = BoundingBoxDiagonal(cornersA, cornersB);
            double dirWeight = Math.Max(diag * diag, 1.0);
            bool hasAxis = capAxis.IsValid && !capAxis.IsZero;

            for (int i = 0; i < n; i++)
            {
                int j = (shift + i) % n;
                Point3d a0 = cornersA[i];
                Point3d a1 = cornersA[(i + 1) % n];
                Point3d b0 = cornersB[j];
                Point3d b1 = cornersB[(j + 1) % n];

                score += hasAxis
                    ? LateralDistanceSquared(a0, b0, capAxis) * 20.0
                    : a0.DistanceToSquared(b0);

                score += hasAxis
                    ? LateralDistanceSquared(a1, b1, capAxis) * 20.0
                    : a1.DistanceToSquared(b1);

                Point3d am = MidPoint(a0, a1);
                Point3d bm = MidPoint(b0, b1);
                score += hasAxis
                    ? LateralDistanceSquared(am, bm, capAxis) * 30.0
                    : am.DistanceToSquared(bm);

                double crossed =
                    (hasAxis ? LateralDistanceSquared(a0, b1, capAxis) : a0.DistanceToSquared(b1)) +
                    (hasAxis ? LateralDistanceSquared(a1, b0, capAxis) : a1.DistanceToSquared(b0));
                double direct =
                    (hasAxis ? LateralDistanceSquared(a0, b0, capAxis) : a0.DistanceToSquared(b0)) +
                    (hasAxis ? LateralDistanceSquared(a1, b1, capAxis) : a1.DistanceToSquared(b1));
                if (crossed < direct) score += dirWeight * 100.0;

                Vector3d da = a1 - a0;
                Vector3d db = b1 - b0;
                if (!da.IsZero && !db.IsZero)
                {
                    da.Unitize();
                    db.Unitize();
                    score += (1.0 - Math.Max(-1.0, Math.Min(1.0, da * db))) * dirWeight;
                }

                if (edgesA != null && edgesB != null && i < edgesA.Count && j < edgesB.Count)
                {
                    Vector3d ea = SafeCurveDirection(edgesA[i]);
                    Vector3d eb = SafeCurveDirection(edgesB[j]);
                    if (!ea.IsZero && !eb.IsZero)
                        score += (1.0 - Math.Max(-1.0, Math.Min(1.0, ea * eb))) * dirWeight;
                }
            }

            return score;
        }

        private static Point3d AveragePoint(List<Point3d> pts)
        {
            if (pts == null || pts.Count == 0) return Point3d.Origin;

            double x = 0.0;
            double y = 0.0;
            double z = 0.0;
            int count = 0;
            foreach (var pt in pts)
            {
                if (!pt.IsValid) continue;
                x += pt.X;
                y += pt.Y;
                z += pt.Z;
                count++;
            }

            return count > 0 ? new Point3d(x / count, y / count, z / count) : Point3d.Origin;
        }

        private static Point3d MidPoint(Point3d a, Point3d b)
        {
            return new Point3d(
                0.5 * (a.X + b.X),
                0.5 * (a.Y + b.Y),
                0.5 * (a.Z + b.Z));
        }

        private static double LateralDistanceSquared(Point3d a, Point3d b, Vector3d axis)
        {
            Vector3d v = b - a;
            double along = v * axis;
            Vector3d lateral = v - axis * along;
            return lateral.SquareLength;
        }

        private static double BoundingBoxDiagonal(List<Point3d> a, List<Point3d> b)
        {
            var bb = BoundingBox.Empty;
            if (a != null)
                foreach (var p in a)
                    if (p.IsValid) bb.Union(p);
            if (b != null)
                foreach (var p in b)
                    if (p.IsValid) bb.Union(p);

            return bb.IsValid ? bb.Diagonal.Length : 1.0;
        }

        private static Vector3d SafeCurveDirection(Curve c)
        {
            if (c == null || !c.IsValid) return Vector3d.Zero;
            Vector3d d = c.PointAtEnd - c.PointAtStart;
            if (!d.IsZero)
            {
                d.Unitize();
                return d;
            }

            d = c.TangentAtStart;
            if (!d.IsZero) d.Unitize();
            return d;
        }

        private static void ApplyLoopAlignment(
            ref List<Curve> edges,
            ref List<Point3d> corners,
            bool reverse,
            int shift)
        {
            if (reverse) ReverseLoop(edges, corners);
            if (shift != 0)
            {
                edges = Rotate(edges, shift);
                corners = Rotate(corners, shift);
            }
        }

        private static void ReverseLoop(List<Curve> edges, List<Point3d> corners)
        {
            if (edges == null || corners == null) return;

            edges.Reverse();
            for (int i = 0; i < edges.Count; i++)
                if (edges[i] != null) edges[i].Reverse();

            corners.Clear();
            foreach (var e in edges)
            {
                if (e != null && e.IsValid)
                    corners.Add(e.PointAtStart);
            }
        }

        private static void AlignCornerSets(List<Point3d> a, List<Point3d> b, out bool useReverse, out int shift)
        {
            useReverse = false;
            shift = 0;

            int n = a.Count;
            double bestF = double.PositiveInfinity;
            int sF = 0;

            for (int k = 0; k < n; k++)
            {
                double c = 0.0;
                for (int i = 0; i < n; i++)
                    c += a[i].DistanceToSquared(b[(k + i) % n]);
                if (c < bestF)
                {
                    bestF = c;
                    sF = k;
                }
            }

            var br = new List<Point3d>(b);
            br.Reverse();

            double bestR = double.PositiveInfinity;
            int sR = 0;
            for (int k = 0; k < n; k++)
            {
                double c = 0.0;
                for (int i = 0; i < n; i++)
                    c += a[i].DistanceToSquared(br[(k + i) % n]);
                if (c < bestR)
                {
                    bestR = c;
                    sR = k;
                }
            }

            if (bestR < bestF)
            {
                useReverse = true;
                shift = sR;
            }
            else
            {
                useReverse = false;
                shift = sF;
            }
        }

        private static Curve JoinLoopAsCurve(BrepLoop loop, double tol)
        {
            var segs = new List<Curve>();
            foreach (var t in loop.Trims)
            {
                var e = t.Edge;
                if (e == null) continue;

                var c = e.DuplicateCurve();
                if (c == null) continue;
                if (t.IsReversed()) c.Reverse();
                segs.Add(c);
            }
            if (segs.Count == 0) return null;

            var joined = Curve.JoinCurves(segs, tol, false);
            if (joined != null && joined.Length > 0)
            {
                Curve best = joined[0];
                double bestLen = best.GetLength();
                for (int i = 1; i < joined.Length; i++)
                {
                    double len = joined[i].GetLength();
                    if (len > bestLen)
                    {
                        best = joined[i];
                        bestLen = len;
                    }
                }
                if (!best.IsClosed) best.MakeClosed(tol);
                return best;
            }

            var pc = new PolyCurve();
            foreach (var c in segs) pc.AppendSegment(c);
            if (!pc.IsClosed) pc.MakeClosed(tol);
            return pc;
        }

        private static void AlignClosedPair(Curve c0, Curve c1, double tol)
        {
            if (c0 == null || c1 == null) return;
            if (!c0.IsClosed || !c1.IsClosed) return;

            double t1;
            if (c1.ClosestPoint(c0.PointAtStart, out t1))
                c1.ChangeClosedCurveSeam(t1);

            var t0 = c0.TangentAtStart;
            var t1v = c1.TangentAtStart;
            if (!t0.IsZero && !t1v.IsZero && Vector3d.Multiply(t0, t1v) < 0.0)
                c1.Reverse();
        }

        private static Brep CleanBrep(Brep b, double tol)
        {
            if (b == null) return null;
            try { b.Faces.ShrinkFaces(); } catch { }
            try { b.Faces.SplitKinkyFaces(RhinoMath.ToRadians(0.5), true); } catch { }
            try { b.MergeCoplanarFaces(tol); } catch { }
            try { b.Compact(); } catch { }
            return b;
        }

        private static void OrientSolidOutward(Brep b)
        {
            if (b == null || !b.IsValid || !b.IsSolid) return;

            try
            {
                if (b.SolidOrientation == BrepSolidOrientation.Inward)
                    b.Flip();
            }
            catch
            {
            }
        }

        private static List<T> Rotate<T>(List<T> list, int k)
        {
            int n = list.Count;
            var res = new List<T>(n);
            for (int i = 0; i < n; i++)
                res.Add(list[(k + i) % n]);
            return res;
        }

        private static bool IsBoxLike(Brep b, double tol, double angTolRad)
        {
            if (b == null || !b.IsSolid) return false;
            if (b.Faces.Count != 6) return false;

            var planes = new List<Plane>();
            foreach (var f in b.Faces)
            {
                Plane p;
                if (!f.TryGetPlane(out p)) return false;
                planes.Add(p);
            }

            var used = new bool[6];
            var dirs = new List<Vector3d>();

            for (int i = 0; i < 6; i++)
            {
                if (used[i]) continue;

                var ni = planes[i].Normal;
                ni.Unitize();

                int mate = -1;
                for (int j = i + 1; j < 6; j++)
                {
                    if (used[j]) continue;

                    var nj = planes[j].Normal;
                    nj.Unitize();

                    double d = Math.Abs(ni * nj);
                    if (d >= Math.Cos(angTolRad))
                    {
                        mate = j;
                        break;
                    }
                }
                if (mate < 0) return false;

                used[i] = used[mate] = true;

                var axis = ni;
                if (axis.Z < 0) axis = -axis;
                dirs.Add(axis);
            }

            if (dirs.Count != 3) return false;

            for (int a = 0; a < 3; a++)
            for (int b2 = a + 1; b2 < 3; b2++)
            {
                double d = Math.Abs(dirs[a] * dirs[b2]);
                if (d > Math.Cos(Math.PI / 2.0 - angTolRad)) return false;
            }

            return true;
        }
    }
}
