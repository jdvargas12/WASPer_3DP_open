#region Component Description
/*
Component: wsp_Ge09_Solid from Mesh Caps
Nickname: Solid Bound Geo (M)
Category: WASPer_3DP
SubCategory: 2.0_Geometry

GENERAL DESCRIPTION
Sorts mesh caps, conforms them to the most complex mesh topology, and builds
bounded mesh solids between caps.

IMPORTANT
The most complex valid mesh cap is automatically used as topology donor.
Complexity is ranked by vertex count, then face count, then naked edge count.
All output caps are rebuilt with this mesh topology by closest-point projection
onto each target cap. Use caps with similar shape and boundary layout for
predictable results.

Inputs:
0) mesh_caps : List<Mesh>
   Open, planar-ish cap meshes.

Outputs:
0) caps : List<Mesh>
   Conformed caps in sorted order.

1) solids : List<Mesh>
   Closed shell meshes between each consecutive sorted cap pair.

2) solid_box_bool : List<bool>
   True for each solid that is box-like.

3) shell_solid : Mesh
   Closed shell mesh between the two outermost sorted caps.

4) shell_open : Mesh
   Wall-only mesh from shell_solid.

5) partitions : List<Mesh>
   Unique interior conformed caps.
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;

using Rhino;
using Rhino.Geometry;

using Grasshopper.Kernel;
#endregion

namespace WASPer_3DP.Components._3_Geometry
{
    public class wsp_Ge09_Solid_From_Mesh_Caps : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Ge09_Solid_From_Mesh_Caps()
          : base(
              "wsp_Ge09_Solid from Mesh Caps",
              "Solid Bound Geo (M)",
              "Sorts mesh caps, conforms them to the most complex mesh topology, and builds mesh solids, shell_solid, shell_open, partitions, and box-like flags.",
              global::WASPer_3DP.WASPerPalette.DesignFabrication,
              "2.0_Geometry")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("78C34E9C-294A-4D78-8D82-2DE8F6A92D94");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ge09_Solid from Mesh Caps.png"))
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
            pManager.AddMeshParameter(
                "mesh_caps",
                "mesh_caps",
                "Open, planar-ish cap meshes. The most complex valid mesh is automatically used as topology donor.",
                GH_ParamAccess.list);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter("caps", "caps", "Conformed caps in sorted order.", GH_ParamAccess.list);
            pManager.AddMeshParameter("solids", "solids", "Closed shell meshes between consecutive sorted cap pairs.", GH_ParamAccess.list);
            pManager.AddBooleanParameter("solid_box_bool", "box", "True when the corresponding solid is box-like.", GH_ParamAccess.list);
            pManager.AddMeshParameter("shell_solid", "shell_solid", "Closed shell mesh between the two outermost sorted caps.", GH_ParamAccess.item);
            pManager.AddMeshParameter("shell_open", "shell_open", "Wall-only mesh from shell_solid.", GH_ParamAccess.item);
            pManager.AddMeshParameter("partitions", "partitions", "Unique interior conformed caps.", GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            var meshCaps = new List<Mesh>();
            DA.GetDataList(0, meshCaps);

            var outCaps = new List<Mesh>();
            var outSolids = new List<Mesh>();
            var outFlags = new List<bool>();
            var outParts = new List<Mesh>();
            Mesh outShellSolid = null;
            Mesh outShellOpen = null;

            double tol = RhinoDoc.ActiveDoc != null ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 1e-6;

            if (meshCaps == null || meshCaps.Count < 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Provide at least 2 mesh caps.");
                SetOutputs(DA, outCaps, outSolids, outFlags, outShellSolid, outShellOpen, outParts);
                return;
            }

            int donorIndex = FindTopologyDonorIndex(meshCaps);
            if (donorIndex < 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No valid mesh cap could be used as topology donor.");
                SetOutputs(DA, outCaps, outSolids, outFlags, outShellSolid, outShellOpen, outParts);
                return;
            }

            Mesh topoRef = meshCaps[donorIndex];
            Mesh topoDonor = topoRef.DuplicateMesh();
            topoDonor.Normals.ComputeNormals();
            topoDonor.Compact();
            Message = _versionTag + " | donor " + donorIndex;

            List<int> order = SortMeshesAlongAxis(meshCaps, tol);

            var conformed = new Mesh[meshCaps.Count];
            bool useParallel = meshCaps.Count >= 6;

            Action<int> conformWork = i =>
            {
                Mesh tgt = meshCaps[i];
                if (tgt == null || !tgt.IsValid)
                {
                    conformed[i] = null;
                    return;
                }
                conformed[i] = i == donorIndex ? topoDonor.DuplicateMesh() : ConformTopologyClosest(topoDonor, tgt);
            };

            if (useParallel) Parallel.For(0, meshCaps.Count, conformWork);
            else for (int i = 0; i < meshCaps.Count; i++) conformWork(i);

            for (int k = 0; k < order.Count; k++)
            {
                Mesh m = conformed[order[k]];
                if (m != null && m.IsValid) outCaps.Add(m);
            }

            for (int k = 0; k < order.Count - 1; k++)
            {
                Mesh a = conformed[order[k]];
                Mesh b = conformed[order[k + 1]];
                if (a == null || b == null) continue;

                Mesh vol = BuildShellClosed(a, b);
                if (vol != null && vol.IsValid)
                {
                    outSolids.Add(vol);
                    outFlags.Add(IsBoxLikeMesh(vol, tol, RhinoMath.ToRadians(2.0)));
                }
            }

            Mesh outerA = conformed[order[0]];
            Mesh outerB = conformed[order[order.Count - 1]];
            if (outerA != null && outerB != null)
            {
                outShellSolid = BuildShellClosed(outerA, outerB);
                if (outShellSolid != null && outShellSolid.IsValid)
                    outShellOpen = RemoveHorizontalFaces(outShellSolid, 0.7);
            }

            outParts = UniquePartitionCapsMesh(conformed, meshCaps, order, tol);
            SetOutputs(DA, outCaps, outSolids, outFlags, outShellSolid, outShellOpen, outParts);
        }

        private static void SetOutputs(
            IGH_DataAccess da,
            List<Mesh> caps,
            List<Mesh> solids,
            List<bool> flags,
            Mesh shellSolid,
            Mesh shellOpen,
            List<Mesh> partitions)
        {
            da.SetDataList(0, caps);
            da.SetDataList(1, solids);
            da.SetDataList(2, flags);
            da.SetData(3, shellSolid);
            da.SetData(4, shellOpen);
            da.SetDataList(5, partitions);
        }

        private static int FindTopologyDonorIndex(List<Mesh> meshCaps)
        {
            int bestIndex = -1;
            int bestVertexCount = -1;
            int bestFaceCount = -1;
            int bestNakedEdgeCount = -1;

            if (meshCaps == null) return bestIndex;

            for (int i = 0; i < meshCaps.Count; i++)
            {
                Mesh m = meshCaps[i];
                if (m == null || !m.IsValid) continue;

                int vertexCount = m.Vertices.Count;
                int faceCount = m.Faces.Count;
                int nakedEdgeCount = 0;
                try { nakedEdgeCount = GetNakedEdges(m).Count; } catch { nakedEdgeCount = 0; }

                bool isBetter =
                    vertexCount > bestVertexCount ||
                    (vertexCount == bestVertexCount && faceCount > bestFaceCount) ||
                    (vertexCount == bestVertexCount && faceCount == bestFaceCount && nakedEdgeCount > bestNakedEdgeCount);

                if (isBetter)
                {
                    bestIndex = i;
                    bestVertexCount = vertexCount;
                    bestFaceCount = faceCount;
                    bestNakedEdgeCount = nakedEdgeCount;
                }
            }

            return bestIndex;
        }

        private static Mesh ConformTopologyClosest(Mesh sourceMesh, Mesh targetMesh)
        {
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

        private static List<int> SortMeshesAlongAxis(List<Mesh> caps, double tol)
        {
            int n = caps.Count;
            var centers = new Point3d[n];
            var normals = new List<Vector3d>();

            for (int i = 0; i < n; i++)
            {
                Mesh m = caps[i];
                if (m == null || !m.IsValid)
                {
                    centers[i] = Point3d.Unset;
                    continue;
                }

                var amp = AreaMassProperties.Compute(m);
                centers[i] = amp != null ? amp.Centroid : m.GetBoundingBox(true).Center;

                Plane pl;
                if (TryFitPlaneToMesh(m, out pl))
                {
                    var nn = pl.Normal;
                    if (!nn.IsZero && nn.IsValid)
                    {
                        nn.Unitize();
                        normals.Add(nn);
                    }
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

        private static bool TryFitPlaneToMesh(Mesh m, out Plane pl)
        {
            pl = Plane.Unset;
            try
            {
                if (m == null || !m.IsValid) return false;
                var pts = m.Vertices.ToPoint3dArray();
                if (pts == null || pts.Length < 3) return false;

                Plane fp;
                if (Plane.FitPlaneToPoints(pts, out fp) == PlaneFitResult.Success && fp.IsValid)
                {
                    pl = fp;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static List<Mesh> UniquePartitionCapsMesh(Mesh[] conformed, List<Mesh> originals, List<int> order, double tol)
        {
            var res = new List<Mesh>();
            if (order == null || order.Count < 3) return res;

            var seenPlanes = new List<Plane>();
            double angTol = RhinoMath.ToRadians(1.5);

            for (int k = 1; k < order.Count - 1; k++)
            {
                int i = order[k];
                Mesh mOrig = originals[i];
                Mesh mConf = conformed[i];
                if (mOrig == null || !mOrig.IsValid || mConf == null || !mConf.IsValid) continue;

                Plane pl;
                if (!TryFitPlaneToMesh(mOrig, out pl) || !pl.IsValid || pl.Normal.IsZero)
                {
                    res.Add(mConf);
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
                    res.Add(mConf);
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

        private static Mesh BuildShellClosed(Mesh meshA, Mesh meshB)
        {
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
                int count = loopA.Count;
                for (int i = 0; i < count; i++)
                {
                    int next = (i + 1) % count;
                    int a0 = loopA[i];
                    int a1 = loopA[next];
                    int b0 = loopB[i] + offset;
                    int b1 = loopB[next] + offset;
                    vol.Faces.AddFace(a0, a1, b1, b0);
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

        private static Mesh RemoveHorizontalFaces(Mesh mesh, double dotThreshold)
        {
            if (mesh == null || !mesh.IsValid) return null;

            mesh.FaceNormals.ComputeFaceNormals();
            Vector3d worldZ = Vector3d.ZAxis;
            Mesh result = new Mesh();

            foreach (Point3f v in mesh.Vertices)
                result.Vertices.Add(v);

            for (int i = 0; i < mesh.Faces.Count; i++)
            {
                Vector3f fn = mesh.FaceNormals[i];
                Vector3d fnD = new Vector3d(fn.X, fn.Y, fn.Z);
                if (!fnD.Unitize()) continue;

                double dot = Math.Abs(Vector3d.Multiply(fnD, worldZ));
                if (dot < dotThreshold)
                {
                    MeshFace f = mesh.Faces[i];
                    if (f.IsQuad) result.Faces.AddFace(f.A, f.B, f.C, f.D);
                    else result.Faces.AddFace(f.A, f.B, f.C);
                }
            }

            result.Normals.ComputeNormals();
            result.Compact();
            return result;
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

            var loop = new List<int>();
            var visited = new HashSet<int>();
            int start = adj.Keys.First();
            int current = start;

            while (true)
            {
                loop.Add(current);
                visited.Add(current);
                var neighbors = adj[current].Where(n => !visited.Contains(n)).ToList();
                if (neighbors.Count == 0) break;
                current = neighbors[0];
                if (current == start) break;
            }

            return loop.Count > 0 ? loop : null;
        }

        private static List<Tuple<int, int>> GetNakedEdges(Mesh mesh)
        {
            var edgeStore = new Dictionary<string, Tuple<int, int>>();
            var edgeUse = new Dictionary<string, int>();

            foreach (MeshFace f in mesh.Faces)
            {
                int[] fv = f.IsQuad
                    ? new int[] { f.A, f.B, f.C, f.D }
                    : new int[] { f.A, f.B, f.C };

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

            var naked = new List<Tuple<int, int>>();
            foreach (var kvp in edgeUse)
                if (kvp.Value == 1)
                    naked.Add(edgeStore[kvp.Key]);

            return naked;
        }

        private static bool IsBoxLikeMesh(Mesh m, double tol, double angTolRad)
        {
            if (m == null || !m.IsValid || !m.IsClosed) return false;

            Brep b = null;
            try { b = Brep.CreateFromMesh(m, true); }
            catch { b = null; }

            return b != null && IsBoxLikeBrep(b, tol, angTolRad);
        }

        private static bool IsBoxLikeBrep(Brep b, double tol, double angTolRad)
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
