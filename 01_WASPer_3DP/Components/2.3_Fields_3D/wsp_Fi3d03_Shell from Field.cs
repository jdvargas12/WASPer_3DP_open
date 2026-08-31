// wsp_Fi3d03_Shell from Field.cs
// WASPer_3DP - Subcategory: 2.3_Fields_3D
//
// Builds an inward shell field from an existing WASPer 3D field.
// Input field convention: negative = material / inside, positive = outside.
// Shell convention: negative = shell material, positive = outside shell.

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
    public class wsp_Fi3d03_ShellFromField : GH_Component
    {
        private const string NAME   = "wsp_Fi3d03_Shell from Field";
        private const string NICK   = "Shell Field";
        private const string CAT    = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "2.3_Fields_3D";
        private const long   MAX_SAMPLES = 20000000;
        private const double CAP_NORMAL_THRESHOLD = 0.70;

        private readonly string _versionTag;

        public wsp_Fi3d03_ShellFromField()
            : base(
                NAME,
                NICK,
                "Creates an inward shell from a WASPer 3D field, optionally extracting it as a mesh.",
                CAT,
                SUBCAT)
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("9D5DFD3B-9ABD-4F43-840C-0F11B21D7817");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Fi3d03_Shell from Field.png"))
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
                "Input WASPer 3D field.\n" +
                "Convention: negative = source material / inside, positive = outside.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "shell_t", "shell_t",
                "Inward shell thickness in model units.",
                GH_ParamAccess.item,
                5.0);

            pManager.AddBooleanParameter(
                "caps", "caps",
                "If true, removes horizontal-ish cap regions using the field gradient.\n" +
                "This is field-based, not Brep-face based, because a field has no topology.",
                GH_ParamAccess.item,
                false);

            pManager.AddNumberParameter(
                "res", "res",
                "Mesh sampling resolution in model units. Smaller values are more detailed but slower.",
                GH_ParamAccess.item,
                2.0);

            pManager.AddBooleanParameter(
                "mesh", "mesh?",
                "If true, extract shell_mesh immediately. If false, only output shell_field.",
                GH_ParamAccess.item,
                true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "shell_mesh", "mesh",
                "Extracted shell mesh. Empty when mesh? is false.",
                GH_ParamAccess.item);

            pManager.AddGenericParameter(
                "shell_field", "field",
                "WASPer shell field. Negative values are inside the shell material.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "info", "info",
                "Shell field and meshing diagnostics.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            IGH_Goo goo = null;
            if (!DA.GetData(0, ref goo) || goo == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No field input provided.");
                return;
            }

            double shellT = 5.0;
            bool removeCaps = false;
            double res = 2.0;
            bool makeMesh = true;

            DA.GetData(1, ref shellT);
            DA.GetData(2, ref removeCaps);
            DA.GetData(3, ref res);
            DA.GetData(4, ref makeMesh);

            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;
            shellT = Math.Max(shellT, 0.0);
            res = Math.Max(res, tol * 10.0);

            WasperField source = ExtractField(goo);
            if (source == null || source.Evaluator == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is not a valid WASPer field.");
                return;
            }

            if (shellT <= tol)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "shell_t must be larger than model tolerance.");
                return;
            }

            double gradStep = Math.Max(tol * 10.0, Math.Min(shellT * 0.25, res * 0.5));
            WasperField shellField = CreateShellField(source, shellT, removeCaps, gradStep);

            DA.SetData(1, new WasperFieldGoo(shellField));

            Mesh shellMesh = null;
            string meshInfo = "mesh extraction skipped";
            long samples = 0;
            int nx = 0, ny = 0, nz = 0;
            long sampleMs = 0;
            long mcMs = 0;

            var totalWatch = Stopwatch.StartNew();

            if (makeMesh)
            {
                Box sampleBox;
                if (!TryGetSampleBox(source, shellT, res, out sampleBox))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input field has no valid domain.");
                    return;
                }

                BuildGridCounts(sampleBox, res, out nx, out ny, out nz);
                samples = (long)nx * ny * nz;

                if (samples > MAX_SAMPLES)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        $"Shell mesh would create {samples:N0} samples. Increase res or reduce the field domain.");
                    DA.SetData(2, BuildInfo(source, shellT, removeCaps, res, makeMesh, nx, ny, nz, samples, 0, 0, "sample cap exceeded", totalWatch.ElapsedMilliseconds));
                    return;
                }

                var scalars = new double[(int)samples];
                var points = new Point3d[(int)samples];

                var sampleWatch = Stopwatch.StartNew();
                SampleField(shellField, sampleBox, nx, ny, nz, scalars, points);
                sampleWatch.Stop();
                sampleMs = sampleWatch.ElapsedMilliseconds;

                var mcWatch = Stopwatch.StartNew();
                shellMesh = WasperMarchingCubes.Extract(
                    scalars,
                    points,
                    nx,
                    ny,
                    nz,
                    0.0,
                    Math.Max(tol * 0.25, 1e-7));
                mcWatch.Stop();
                mcMs = mcWatch.ElapsedMilliseconds;

                if (shellMesh != null && shellMesh.Faces.Count > 0)
                {
                    CleanResultMesh(shellMesh);
                    meshInfo = $"mesh vertices/faces: {shellMesh.Vertices.Count:N0} / {shellMesh.Faces.Count:N0}";
                    DA.SetData(0, shellMesh);
                }
                else
                {
                    meshInfo = "no shell surface found";
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No shell mesh was generated. Check shell_t, res, and field sign convention.");
                }
            }

            totalWatch.Stop();

            string info = BuildInfo(
                source,
                shellT,
                removeCaps,
                res,
                makeMesh,
                nx,
                ny,
                nz,
                samples,
                sampleMs,
                mcMs,
                meshInfo,
                totalWatch.ElapsedMilliseconds);

            DA.SetData(2, info);
            Message = makeMesh
                ? $"{_versionTag} | {(shellMesh != null ? shellMesh.Faces.Count : 0)} f"
                : $"{_versionTag} | field";
        }

        private static WasperField CreateShellField(WasperField source, double shellT, bool removeCaps, double gradStep)
        {
            string label = string.IsNullOrEmpty(source.Label)
                ? "shell_field"
                : source.Label + "_shell";

            BoundingBox domain = source.Domain;

            return new WasperField(
                p =>
                {
                    double f = SafeEvaluate(source, p);
                    double shell = Math.Max(f, -f - shellT);

                    if (!removeCaps) return shell;

                    Vector3d g = EstimateGradient(source, p, gradStep);
                    if (!g.IsValid || g.IsZero) return shell;

                    g.Unitize();

                    // Positive for horizontal-ish source boundaries, negative for side walls.
                    // Intersecting with this removes top/bottom cap regions from the shell.
                    double capField = (Math.Abs(g.Z) - CAP_NORMAL_THRESHOLD) * shellT;
                    return Math.Max(shell, capField);
                },
                domain,
                label);
        }

        private static double SafeEvaluate(WasperField field, Point3d point)
        {
            try
            {
                double v = field.Evaluate(point);
                if (double.IsNaN(v) || double.IsInfinity(v)) return double.PositiveInfinity;
                return v;
            }
            catch
            {
                return double.PositiveInfinity;
            }
        }

        private static Vector3d EstimateGradient(WasperField field, Point3d p, double h)
        {
            h = Math.Max(h, 1e-9);

            double dx = SafeEvaluate(field, new Point3d(p.X + h, p.Y, p.Z)) -
                        SafeEvaluate(field, new Point3d(p.X - h, p.Y, p.Z));
            double dy = SafeEvaluate(field, new Point3d(p.X, p.Y + h, p.Z)) -
                        SafeEvaluate(field, new Point3d(p.X, p.Y - h, p.Z));
            double dz = SafeEvaluate(field, new Point3d(p.X, p.Y, p.Z + h)) -
                        SafeEvaluate(field, new Point3d(p.X, p.Y, p.Z - h));

            return new Vector3d(dx, dy, dz);
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

        private static bool TryGetSampleBox(WasperField field, double shellT, double res, out Box sampleBox)
        {
            sampleBox = new Box();
            if (field == null || !field.Domain.IsValid) return false;

            BoundingBox bb = field.Domain;
            double pad = Math.Max(res * 2.0, shellT * 0.25 + res);
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
                        scalars[idx] = SafeEvaluate(field, p);
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

        private static int Index(int ix, int iy, int iz, int nx, int ny)
        {
            return ix + nx * (iy + ny * iz);
        }

        private static void CleanResultMesh(Mesh mesh)
        {
            if (mesh == null) return;

            mesh.Vertices.CombineIdentical(true, true);
            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();
            mesh.UnifyNormals();
            mesh.Weld(Math.PI);
            mesh.Normals.ComputeNormals();
            mesh.Compact();
        }

        private static string BuildInfo(
            WasperField source,
            double shellT,
            bool caps,
            double res,
            bool mesh,
            int nx,
            int ny,
            int nz,
            long samples,
            long sampleMs,
            long mcMs,
            string meshInfo,
            long elapsedMs)
        {
            BoundingBox bb = source.Domain;

            return
                "Shell from Field\n" +
                $"version         : {(Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown")}\n" +
                $"source label    : {(string.IsNullOrEmpty(source.Label) ? "(none)" : source.Label)}\n" +
                $"shell_t         : {shellT:F4}\n" +
                $"caps_removed    : {caps}\n" +
                $"caps method     : {(caps ? "field-gradient horizontal removal" : "full closed shell")}\n" +
                $"resolution      : {res:F4}\n" +
                $"mesh?           : {mesh}\n" +
                $"domain min      : {bb.Min.X:F3}, {bb.Min.Y:F3}, {bb.Min.Z:F3}\n" +
                $"domain max      : {bb.Max.X:F3}, {bb.Max.Y:F3}, {bb.Max.Z:F3}\n" +
                $"grid            : {(mesh ? $"{nx}x{ny}x{nz}" : "(not sampled)")}\n" +
                $"samples         : {samples:N0}\n" +
                $"sample_ms       : {sampleMs}\n" +
                $"mc_ms           : {mcMs}\n" +
                $"elapsed_ms      : {elapsedMs}\n" +
                $"{meshInfo}";
        }
    }
}
