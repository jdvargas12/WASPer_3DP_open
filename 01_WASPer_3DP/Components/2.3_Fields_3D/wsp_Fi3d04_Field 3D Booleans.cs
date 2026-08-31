// wsp_Fi3d04_Field 3D Booleans.cs
// WASPer_3DP - Subcategory: 2.3_Fields_3D
//
// Boolean operations on multiple WASPer 3D fields.
// Field convention: negative = material / inside, positive = void / outside.
// SDF boolean convention:
//   union        = min(A, B, C...)
//   subtraction  = max(A, -union(B...))
//   intersection = max(A, B, C...)

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP.Components._2_3_Fields_3D
{
    public class wsp_Fi3d04_Field3DBooleans : GH_Component
    {
        private const string NAME   = "wsp_Fi3d04_Field 3D Booleans";
        private const string NICK   = "Field3D Bool";
        private const string CAT    = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "2.3_Fields_3D";
        private const long   MAX_SAMPLES = 20000000;

        private readonly string _versionTag;

        public wsp_Fi3d04_Field3DBooleans()
            : base(
                NAME,
                NICK,
                "Boolean operations on multiple WASPer 3D fields and optional result mesh extraction.\n" +
                "Field convention: negative = material / inside, positive = outside.\n" +
                "boolean_op:\n" +
                "  1 = Union (min)\n" +
                "  2 = Subtraction (A - union(B...))\n" +
                "  3 = Intersection (max)\n" +
                "  4 = XOR (A ⊕ B, first two fields)\n" +
                "  5 = Negate (-A)\n\n" +
                "Default op = 3, preserving the original intersection behavior.",
                CAT,
                SUBCAT)
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("B72D3F61-0BC4-44C6-B1D4-5D62366E9347");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Fi3d04_Field 3D Booleans.png"))
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
                "fields", "fields",
                "WASPer 3D fields to combine.\n" +
                "Field convention: negative = material / inside, positive = outside.\n" +
                "For subtraction and negate, the first field is treated as A.",
                GH_ParamAccess.list);
            pManager[0].DataMapping = GH_DataMapping.Flatten;

            pManager.AddNumberParameter(
                "res", "res",
                "Sampling resolution in model units for result_mesh. Smaller values are more detailed but slower.",
                GH_ParamAccess.item,
                2.0);

            pManager.AddBooleanParameter(
                "mesh", "mesh?",
                "If true, extract result_mesh immediately. If false, only output result_field.",
                GH_ParamAccess.item,
                true);

            pManager.AddIntegerParameter(
                "boolean_op", "op",
                "Boolean operation:\n" +
                "1 = Union (min)\n" +
                "2 = Subtraction (A - union(B...))\n" +
                "3 = Intersection (max)\n" +
                "4 = XOR (A ⊕ B, first two fields)\n" +
                "5 = Negate (-A)\n\n" +
                "Default = 3, matching the previous intersection-only behavior.",
                GH_ParamAccess.item,
                3);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "field_result", "field",
                "Result boolean field. Negative values are inside the extracted material/region.",
                GH_ParamAccess.item);

            pManager.AddMeshParameter(
                "result_mesh", "mesh",
                "Extracted mesh from the result boolean field. Empty when mesh? is false.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            var goos = new List<IGH_Goo>();
            if (!DA.GetDataList(0, goos) || goos.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No field input provided.");
                return;
            }

            double res = 2.0;
            bool makeMesh = true;
            int booleanOp = 3;

            DA.GetData(1, ref res);
            DA.GetData(2, ref makeMesh);
            DA.GetData(3, ref booleanOp);

            if (booleanOp < 1 || booleanOp > 5)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "boolean_op out of range. Using 3 = Intersection.");
                booleanOp = 3;
            }

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

            if ((booleanOp == 2 || booleanOp == 4) && fields.Count < 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "Selected boolean operation requires at least 2 fields. Falling back to 3 = Intersection.");
                booleanOp = 3;
            }

            if (booleanOp == 4 && fields.Count > 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "XOR uses only the first two valid fields; extra fields are ignored.");
            }

            BoundingBox domain;
            if (!TryGetBooleanDomain(fields, booleanOp, res, out domain))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not build a valid result domain for the selected boolean operation.");
                return;
            }

            WasperField resultField = CreateBooleanField(fields, domain, booleanOp);
            DA.SetData(0, new WasperFieldGoo(resultField));

            if (!makeMesh)
            {
                Message = $"{_versionTag} | {OpShortName(booleanOp)} | field";
                return;
            }

            Box sampleBox = BoxFromBoundingBox(domain);
            int nx, ny, nz;
            BuildGridCounts(sampleBox, res, out nx, out ny, out nz);

            long samples = (long)nx * ny * nz;
            if (samples > MAX_SAMPLES)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"Result mesh would create {samples:N0} samples. Increase res or simplify the field domains.");
                Message = $"{_versionTag} | sample cap";
                return;
            }

            var scalars = new double[(int)samples];
            var points = new Point3d[(int)samples];

            SampleField(resultField, sampleBox, nx, ny, nz, scalars, points);

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
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"Marching Cubes failed on result field ({nx}x{ny}x{nz} grid): {ex.Message}");
                return;
            }

            if (mesh != null && mesh.Faces.Count > 0)
            {
                CleanResultMesh(mesh, resultField, Math.Max(res * 0.5, tol * 10.0));
                DA.SetData(1, mesh);
                Message = $"{_versionTag} | {OpShortName(booleanOp)} | {mesh.Faces.Count} f";
            }
            else
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "No result mesh was generated. The result may be empty at this resolution.");
                Message = $"{_versionTag} | {OpShortName(booleanOp)} | empty";
            }
        }

        private static WasperField CreateBooleanField(List<WasperField> fields, BoundingBox domain, int booleanOp)
        {
            return WasperFieldOps.Boolean(fields, domain, ToFieldOperation(booleanOp));
        }

        private static WasperFieldBooleanOperation ToFieldOperation(int booleanOp)
        {
            switch (booleanOp)
            {
                case 1: return WasperFieldBooleanOperation.Union;
                case 2: return WasperFieldBooleanOperation.Difference;
                case 3: return WasperFieldBooleanOperation.Intersection;
                case 4: return WasperFieldBooleanOperation.Xor;
                case 5: return WasperFieldBooleanOperation.Invert;
                default: return WasperFieldBooleanOperation.Intersection;
            }
        }

        private static string OpShortName(int booleanOp)
        {
            switch (booleanOp)
            {
                case 1: return "union";
                case 2: return "A-B";
                case 3: return "intersect";
                case 4: return "xor";
                case 5: return "negate";
                default: return "intersect";
            }
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

        private static bool TryGetBooleanDomain(List<WasperField> fields, int booleanOp, double res, out BoundingBox domain)
        {
            domain = BoundingBox.Unset;
            if (fields == null || fields.Count == 0) return false;

            if (booleanOp == 1)
                return TryGetUnionDomain(fields, res, out domain);

            if (booleanOp == 2 || booleanOp == 5)
                return TryGetBaseDomain(fields[0], res, out domain);

            if (booleanOp == 4)
            {
                var firstTwo = new List<WasperField>();
                if (fields.Count > 0) firstTwo.Add(fields[0]);
                if (fields.Count > 1) firstTwo.Add(fields[1]);
                return TryGetUnionDomain(firstTwo, res, out domain);
            }

            return TryGetIntersectionDomain(fields, res, out domain);
        }

        private static bool TryGetBaseDomain(WasperField field, double res, out BoundingBox domain)
        {
            domain = BoundingBox.Unset;
            if (field == null || !field.Domain.IsValid) return false;

            domain = field.Domain;
            double pad = Math.Max(res, 1e-6);
            domain.Inflate(pad);
            return domain.IsValid;
        }

        private static bool TryGetUnionDomain(List<WasperField> fields, double res, out BoundingBox domain)
        {
            domain = BoundingBox.Unset;
            if (fields == null || fields.Count == 0) return false;

            bool started = false;
            foreach (var field in fields)
            {
                if (field == null || !field.Domain.IsValid) continue;

                domain = started ? BoundingBox.Union(domain, field.Domain) : field.Domain;
                started = true;
            }

            if (!started) return false;

            double pad = Math.Max(res, 1e-6);
            domain.Inflate(pad);
            return domain.IsValid;
        }

        private static bool TryGetIntersectionDomain(List<WasperField> fields, double res, out BoundingBox domain)
        {
            domain = BoundingBox.Unset;
            if (fields == null || fields.Count == 0) return false;

            bool started = false;
            Point3d min = Point3d.Unset;
            Point3d max = Point3d.Unset;

            foreach (var field in fields)
            {
                if (field == null || !field.Domain.IsValid) continue;

                var bb = field.Domain;
                if (!started)
                {
                    min = bb.Min;
                    max = bb.Max;
                    started = true;
                }
                else
                {
                    min = new Point3d(
                        Math.Max(min.X, bb.Min.X),
                        Math.Max(min.Y, bb.Min.Y),
                        Math.Max(min.Z, bb.Min.Z));

                    max = new Point3d(
                        Math.Min(max.X, bb.Max.X),
                        Math.Min(max.Y, bb.Max.Y),
                        Math.Min(max.Z, bb.Max.Z));
                }
            }

            if (!started) return false;
            if (max.X <= min.X || max.Y <= min.Y || max.Z <= min.Z) return false;

            domain = new BoundingBox(min, max);

            // A small padding helps Marching Cubes see zero crossings that lie close to the domain boundary.
            double pad = Math.Max(res, 1e-6);
            domain.Inflate(pad);
            return domain.IsValid;
        }

        private static Box BoxFromBoundingBox(BoundingBox bb)
        {
            return new Box(
                Plane.WorldXY,
                new Interval(bb.Min.X, bb.Max.X),
                new Interval(bb.Min.Y, bb.Max.Y),
                new Interval(bb.Min.Z, bb.Max.Z));
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

        private static double SafeEvaluate(WasperField field, Point3d p)
        {
            if (field == null || field.Evaluator == null) return double.PositiveInfinity;

            double value;
            try { value = field.Evaluate(p); }
            catch { value = double.PositiveInfinity; }

            if (double.IsNaN(value) || double.IsInfinity(value))
                return double.PositiveInfinity;

            return value;
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

        private static void CleanResultMesh(Mesh mesh, WasperField field, double gradientStep)
        {
            if (mesh == null || mesh.Faces.Count == 0) return;

            mesh.Vertices.CombineIdentical(true, true);
            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();

            WasperFieldNormalTools.OrientFacesByFieldGradient(mesh, field, gradientStep);
            mesh.UnifyNormals();
            WasperFieldNormalTools.OrientFacesByFieldGradient(mesh, field, gradientStep);

            mesh.Weld(Math.PI);
            mesh.Normals.ComputeNormals();
            mesh.Compact();
        }

        // OrientFacesByFieldGradient, FaceCenter, and IsFinite removed.
        // Fi3d04 now delegates to WasperFieldNormalTools.OrientFacesByFieldGradient
        // (see Components/Shared/Fields/WASPer_FieldNormalTools.cs) which uses the
        // directional-derivative + Parallel.For implementation.

        private static int Index(int ix, int iy, int iz, int nx, int ny)
        {
            return ix + nx * (iy + ny * iz);
        }
    }
}
