// wsp_Fi3d02_Mesh from Field.cs
// WASPer_3DP - Subcategory: 2.3_Fields_3D
//
// Samples one or more WASPer 3D fields and extracts an iso-surface mesh.
// Field convention: negative = material / inside, positive = void / outside.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP.Components._2_3_Fields_3D
{
    public class wsp_Fi3d02_MeshFromField : GH_Component
    {
        private const string NAME   = "wsp_Fi3d02_Mesh from Field";
        private const string NICK   = "Mesh Field";
        private const string CAT    = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "2.3_Fields_3D";
        private const long   MAX_SAMPLES_PER_FIELD = 20000000;

        private readonly string _versionTag;

        public wsp_Fi3d02_MeshFromField()
            : base(
                NAME,
                NICK,
                "Extracts an iso-surface mesh from one or more WASPer 3D fields.",
                CAT,
                SUBCAT)
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("D8C9F0B2-3A5E-4F7A-9C1D-6B2E8A4F0C73");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Fi3d02_Mesh from Field.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "field", "field",
                "One or more WASPer 3D fields to polygonize.\n" +
                "Use Fi3d01 or SDF infill components as input.\n" +
                "Convention: negative = material / inside, positive = outside.",
                GH_ParamAccess.list);

            pManager.AddBoxParameter(
                "sample_box", "box",
                "Optional sampling box. If empty, each field domain is used and padded automatically.\n" +
                "Use this to crop, enlarge, or orient the polygonization domain.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "iso_level", "iso",
                "Field value to extract.\n" +
                "0.0 extracts the real SDF boundary. Positive values offset outward, negative values inward.",
                GH_ParamAccess.item,
                0.0);

            pManager.AddNumberParameter(
                "resolution", "res",
                "Sampling resolution in model units. Smaller values are more detailed but slower.",
                GH_ParamAccess.item,
                2.0);

            pManager.AddBooleanParameter(
                "disjoin_mesh", "disjoin",
                "If true, disconnected mesh islands are output as separate mesh items.",
                GH_ParamAccess.item,
                false);

            pManager.AddBooleanParameter(
                "clean_mesh", "clean",
                "Clean, weld, unify normals, and remove very small fragments.",
                GH_ParamAccess.item,
                true);

            pManager[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "mesh_out", "mesh",
                "Extracted iso-surface mesh. If disjoin_mesh is true, disconnected islands are output separately.",
                GH_ParamAccess.list);

            pManager.AddBoxParameter(
                "sample_box", "box",
                "Sampling box used for each input field.",
                GH_ParamAccess.list);

            pManager.AddTextParameter(
                "info", "info",
                "Meshing diagnostics.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var goos = new List<IGH_Goo>();
            if (!DA.GetDataList(0, goos) || goos.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No field input provided.");
                return;
            }

            Box userBox = new Box();
            bool hasUserBox = DA.GetData(1, ref userBox) && userBox.IsValid;

            double isoLevel = 0.0;
            double res = 2.0;
            bool disjoinMesh = false;
            bool cleanMesh = true;

            DA.GetData(2, ref isoLevel);
            DA.GetData(3, ref res);
            DA.GetData(4, ref disjoinMesh);
            DA.GetData(5, ref cleanMesh);

            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;
            res = Math.Max(res, tol * 10.0);

            var fields = new List<WasperField>();
            int rejected = 0;
            foreach (var goo in goos)
            {
                var field = ExtractField(goo);
                if (field != null && field.Evaluator != null) fields.Add(field);
                else rejected++;
            }

            if (rejected > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"{rejected} input item(s) were not valid WASPer fields and were ignored.");

            if (fields.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No valid WASPer fields found.");
                return;
            }

            var allMeshes = new List<Mesh>();
            var usedBoxes = new List<Box>();
            var infoLines = new List<string>();

            var totalWatch = Stopwatch.StartNew();
            int removedFragmentsTotal = 0;
            long totalSamples = 0;
            int totalFaces = 0;
            int totalVertices = 0;

            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                Box sampleBox;
                if (!TryGetSampleBox(field, hasUserBox, userBox, res, isoLevel, out sampleBox))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"Field {i} has no valid domain and no valid sample_box was provided.");
                    continue;
                }

                usedBoxes.Add(sampleBox);

                int nx, ny, nz;
                BuildGridCounts(sampleBox, res, out nx, out ny, out nz);

                long samples = (long)nx * ny * nz;
                totalSamples += samples;

                if (samples > MAX_SAMPLES_PER_FIELD)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        $"Field {i} would create {samples:N0} samples. Increase resolution or provide a smaller sample_box.");
                    continue;
                }

                var scalarWatch = Stopwatch.StartNew();
                var scalars = new double[(int)samples];
                var points = new Point3d[(int)samples];

                SampleField(field, sampleBox, isoLevel, nx, ny, nz, scalars, points);
                scalarWatch.Stop();

                var meshWatch = Stopwatch.StartNew();
                Mesh mesh = null;
                try
                {
                    mesh = WasperMarchingCubes.Extract(
                        scalars,
                        points,
                        nx,
                        ny,
                        nz,
                        0.0,
                        Math.Max(tol * 0.25, 1e-7));
                }
                catch (Exception ex)
                {
                    meshWatch.Stop();
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        $"Marching Cubes failed on field {i} ({nx}x{ny}x{nz} grid): {ex.Message}");
                    continue;
                }
                meshWatch.Stop();

                int removed = 0;
                List<Mesh> fieldMeshes;

                if (mesh == null || mesh.Faces.Count == 0)
                {
                    fieldMeshes = new List<Mesh>();
                }
                else if (disjoinMesh)
                {
                    fieldMeshes = CleanAndSplitResultMeshes(
                        mesh,
                        cleanMesh,
                        180.0,
                        8,
                        out removed);
                }
                else
                {
                    if (cleanMesh)
                        CleanResultMesh(mesh, 180.0, 8, out removed);

                    fieldMeshes = new List<Mesh>();
                    if (mesh.Faces.Count > 0) fieldMeshes.Add(mesh);
                }

                removedFragmentsTotal += removed;
                foreach (var m in fieldMeshes)
                {
                    if (m == null || m.Faces.Count == 0) continue;
                    allMeshes.Add(m);
                    totalFaces += m.Faces.Count;
                    totalVertices += m.Vertices.Count;
                }

                string label = string.IsNullOrEmpty(field.Label) ? $"field_{i}" : field.Label;
                infoLines.Add(
                    $"{label}: grid={nx}x{ny}x{nz}, samples={samples:N0}, " +
                    $"meshes={fieldMeshes.Count}, sample_ms={scalarWatch.ElapsedMilliseconds}, mc_ms={meshWatch.ElapsedMilliseconds}");
            }

            totalWatch.Stop();

            DA.SetDataList(0, allMeshes);
            DA.SetDataList(1, usedBoxes);

            string info =
                $"Mesh from Field | {_versionTag}\n" +
                $"fields          : {fields.Count}\n" +
                $"iso_level       : {isoLevel:F4}\n" +
                $"resolution      : {res:F4}\n" +
                $"disjoin_mesh    : {disjoinMesh}\n" +
                $"sample_box      : {(hasUserBox ? "input" : "field domain + padding")}\n" +
                $"total samples   : {totalSamples:N0}\n" +
                $"output meshes   : {allMeshes.Count}\n" +
                $"vertices/faces   : {totalVertices:N0} / {totalFaces:N0}\n" +
                $"removed frags   : {removedFragmentsTotal}\n" +
                $"elapsed_ms      : {totalWatch.ElapsedMilliseconds}\n" +
                string.Join("\n", infoLines);

            DA.SetData(2, info);
            Message = $"{_versionTag} | {allMeshes.Count} mesh";
        }

        private static WasperField ExtractField(IGH_Goo goo)
        {
            if (goo == null) return null;

            if (goo is WasperFieldGoo fg) return fg.Value;

            object sv = null;
            try { sv = goo.ScriptVariable(); } catch { sv = null; }

            if (sv is WasperField f) return f;
            if (sv is WasperFieldGoo fgoo) return fgoo.Value;

            var wrapper = goo as GH_ObjectWrapper;
            if (wrapper != null)
            {
                if (wrapper.Value is WasperField wf) return wf;
                if (wrapper.Value is WasperFieldGoo wg) return wg.Value;
            }

            return null;
        }

        private static bool TryGetSampleBox(
            WasperField field,
            bool hasUserBox,
            Box userBox,
            double res,
            double isoLevel,
            out Box sampleBox)
        {
            if (hasUserBox && userBox.IsValid)
            {
                sampleBox = userBox;
                return true;
            }

            sampleBox = new Box();
            if (field == null || !field.Domain.IsValid) return false;

            var bb = field.Domain;
            double pad = Math.Max(res * 2.0, Math.Abs(isoLevel) + res);
            bb.Inflate(pad);

            if (!bb.IsValid) return false;
            sampleBox = new Box(
                Plane.WorldXY,
                new Interval(bb.Min.X, bb.Max.X),
                new Interval(bb.Min.Y, bb.Max.Y),
                new Interval(bb.Min.Z, bb.Max.Z));
            return sampleBox.IsValid;
        }

        private static void BuildGridCounts(Box box, double res, out int nx, out int ny, out int nz)
        {
            double sx = Math.Abs(box.X.Length);
            double sy = Math.Abs(box.Y.Length);
            double sz = Math.Abs(box.Z.Length);

            nx = Math.Max(2, (int)Math.Ceiling(sx / res) + 1);
            ny = Math.Max(2, (int)Math.Ceiling(sy / res) + 1);
            nz = Math.Max(2, (int)Math.Ceiling(sz / res) + 1);
        }

        private static void SampleField(
            WasperField field,
            Box box,
            double isoLevel,
            int nx,
            int ny,
            int nz,
            double[] scalars,
            Point3d[] points)
        {
            Parallel.For(0, nz, iz =>
            {
                double w = nz <= 1 ? 0.0 : (double)iz / (nz - 1);
                for (int iy = 0; iy < ny; iy++)
                {
                    double v = ny <= 1 ? 0.0 : (double)iy / (ny - 1);
                    for (int ix = 0; ix < nx; ix++)
                    {
                        double u = nx <= 1 ? 0.0 : (double)ix / (nx - 1);
                        int idx = Index(ix, iy, iz, nx, ny);
                        Point3d p = box.PointAt(u, v, w);
                        points[idx] = p;

                        double value;
                        try { value = field.Evaluate(p) - isoLevel; }
                        catch { value = double.PositiveInfinity; }

                        if (double.IsNaN(value) || double.IsInfinity(value))
                            value = double.PositiveInfinity;

                        scalars[idx] = value;
                    }
                }
            });
        }

        private static Mesh MarchingCubes(
            double[] scalars,
            Point3d[] points,
            int nx,
            int ny,
            int nz,
            double tol)
        {
            var mesh = new Mesh();
            var vertexMap = new Dictionary<VertexKey, int>();
            double keyTol = Math.Max(tol * 0.25, 1e-7);

            int[,] edgeCorners =
            {
                { 0, 1 }, { 1, 2 }, { 2, 3 }, { 3, 0 },
                { 4, 5 }, { 5, 6 }, { 6, 7 }, { 7, 4 },
                { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 }
            };

            int[] dx = { 0, 1, 1, 0, 0, 1, 1, 0 };
            int[] dy = { 0, 0, 1, 1, 0, 0, 1, 1 };
            int[] dz = { 0, 0, 0, 0, 1, 1, 1, 1 };

            var cp = new Point3d[8];
            var sv = new double[8];
            var edgePoints = new Point3d[12];
            var edgeReady = new bool[12];
            var tri = MarchingCubesClassicTable.TriTable;

            for (int iz = 0; iz < nz - 1; iz++)
            for (int iy = 0; iy < ny - 1; iy++)
            for (int ix = 0; ix < nx - 1; ix++)
            {
                int cubeIndex = 0;
                for (int c = 0; c < 8; c++)
                {
                    int idx = Index(ix + dx[c], iy + dy[c], iz + dz[c], nx, ny);
                    cp[c] = points[idx];
                    sv[c] = scalars[idx];
                    if (sv[c] < 0.0) cubeIndex |= 1 << c;
                }

                if (cubeIndex == 0 || cubeIndex == 255) continue;

                Array.Clear(edgeReady, 0, edgeReady.Length);

                for (int t = 0; t < 15; t += 3)
                {
                    int e0 = tri[cubeIndex, t];
                    if (e0 < 0) break;

                    int e1 = tri[cubeIndex, t + 1];
                    int e2 = tri[cubeIndex, t + 2];
                    if (e1 < 0 || e2 < 0) break;
                    if (!IsValidEdge(e0) || !IsValidEdge(e1) || !IsValidEdge(e2)) continue;

                    int a = AddEdgeVertex(e0);
                    int b = AddEdgeVertex(e1);
                    int c = AddEdgeVertex(e2);

                    if (a != b && b != c && c != a)
                        mesh.Faces.AddFace(a, b, c);
                }

                int AddEdgeVertex(int edge)
                {
                    if (!edgeReady[edge])
                    {
                        int c0 = edgeCorners[edge, 0];
                        int c1 = edgeCorners[edge, 1];
                        edgePoints[edge] = Interpolate(cp[c0], cp[c1], sv[c0], sv[c1]);
                        edgeReady[edge] = true;
                    }

                    return WasperMcHelpers.AddVertex(mesh, vertexMap, edgePoints[edge], keyTol);
                }
            }

            if (mesh.Faces.Count == 0) return mesh;

            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();
            mesh.UnifyNormals();
            mesh.Normals.ComputeNormals();
            mesh.Compact();
            return mesh;
        }

        private static Point3d Interpolate(Point3d a, Point3d b, double va, double vb)
        {
            double denom = va - vb;
            if (Math.Abs(denom) < 1e-12)
                return new Point3d(
                    0.5 * (a.X + b.X),
                    0.5 * (a.Y + b.Y),
                    0.5 * (a.Z + b.Z));

            double t = va / denom;
            if (t < 0.0) t = 0.0;
            else if (t > 1.0) t = 1.0;

            return a + (b - a) * t;
        }

        private static bool IsValidEdge(int edge)
        {
            return edge >= 0 && edge < 12;
        }

        private static List<Mesh> CleanAndSplitResultMeshes(
            Mesh mesh,
            bool clean,
            double weldAngleDeg,
            int minFragFaces,
            out int removedFragments)
        {
            removedFragments = 0;
            if (mesh == null || mesh.Faces.Count == 0) return new List<Mesh>();

            if (clean)
            {
                mesh.Vertices.CombineIdentical(true, true);
                mesh.Faces.CullDegenerateFaces();
                mesh.Vertices.CullUnused();
                mesh.Compact();
            }

            var result = SplitConnectedComponents(mesh, minFragFaces, out removedFragments);
            if (!clean) return result;

            double weldAngle = RhinoMath.ToRadians(Clamp(weldAngleDeg, 0.0, 180.0));

            foreach (var component in result)
            {
                component.Vertices.CombineIdentical(true, true);
                component.Faces.CullDegenerateFaces();
                component.Vertices.CullUnused();
                component.UnifyNormals();
                component.Weld(weldAngle);
                component.Normals.ComputeNormals();
                component.Compact();
            }

            return result;
        }

        private static List<Mesh> SplitConnectedComponents(
            Mesh mesh,
            int minFaces,
            out int removedFragments)
        {
            removedFragments = 0;
            var result = new List<Mesh>();
            if (mesh == null || mesh.Faces.Count == 0) return result;

            int faceCount = mesh.Faces.Count;
            var v2f = new Dictionary<int, List<int>>();

            for (int fi = 0; fi < faceCount; fi++)
            {
                MeshFace f = mesh.Faces[fi];
                AddFV(v2f, f.A, fi);
                AddFV(v2f, f.B, fi);
                AddFV(v2f, f.C, fi);
                if (f.IsQuad) AddFV(v2f, f.D, fi);
            }

            var visited = new bool[faceCount];
            var queue = new Queue<int>();

            for (int seed = 0; seed < faceCount; seed++)
            {
                if (visited[seed]) continue;

                var compFaces = new List<int>();
                visited[seed] = true;
                queue.Enqueue(seed);

                while (queue.Count > 0)
                {
                    int fi = queue.Dequeue();
                    compFaces.Add(fi);
                    MeshFace f = mesh.Faces[fi];
                    EnqueueNeighbors(f.A);
                    EnqueueNeighbors(f.B);
                    EnqueueNeighbors(f.C);
                    if (f.IsQuad) EnqueueNeighbors(f.D);

                    void EnqueueNeighbors(int vi)
                    {
                        if (!v2f.TryGetValue(vi, out var faces)) return;
                        foreach (int nf in faces)
                        {
                            if (visited[nf]) continue;
                            visited[nf] = true;
                            queue.Enqueue(nf);
                        }
                    }
                }

                if (compFaces.Count < minFaces)
                {
                    removedFragments++;
                    continue;
                }

                var component = new Mesh();
                var vMap = new Dictionary<int, int>();

                int MapVertex(int oldIndex)
                {
                    if (vMap.TryGetValue(oldIndex, out int newIndex)) return newIndex;
                    newIndex = component.Vertices.Add(mesh.Vertices[oldIndex]);
                    vMap[oldIndex] = newIndex;
                    return newIndex;
                }

                foreach (int fi in compFaces)
                {
                    MeshFace f = mesh.Faces[fi];
                    int a = MapVertex(f.A);
                    int b = MapVertex(f.B);
                    int c = MapVertex(f.C);
                    if (f.IsQuad) component.Faces.AddFace(a, b, c, MapVertex(f.D));
                    else component.Faces.AddFace(a, b, c);
                }

                result.Add(component);
            }

            return result;
        }

        private static void CleanResultMesh(
            Mesh mesh,
            double weldAngleDeg,
            int minFragFaces,
            out int removedFragments)
        {
            removedFragments = 0;
            if (mesh == null || mesh.Faces.Count == 0) return;

            mesh.Vertices.CombineIdentical(true, true);
            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();

            if (minFragFaces > 0)
            {
                var components = SplitConnectedComponents(mesh, minFragFaces, out removedFragments);
                mesh.Vertices.Clear();
                mesh.Faces.Clear();
                foreach (var component in components)
                    mesh.Append(component);
            }

            mesh.Vertices.CombineIdentical(true, true);
            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();
            mesh.UnifyNormals();
            mesh.Weld(RhinoMath.ToRadians(Clamp(weldAngleDeg, 0.0, 180.0)));
            mesh.Normals.ComputeNormals();
            mesh.Compact();
        }

        private static void AddFV(Dictionary<int, List<int>> map, int vertex, int face)
        {
            if (!map.TryGetValue(vertex, out var list))
            {
                list = new List<int>();
                map[vertex] = list;
            }
            list.Add(face);
        }

        private static int Index(int ix, int iy, int iz, int nx, int ny)
        {
            return ix + nx * (iy + ny * iz);
        }

        private static double Clamp(double v, double a, double b)
        {
            if (v < a) return a;
            if (v > b) return b;
            return v;
        }
    }
}
