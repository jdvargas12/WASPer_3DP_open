// wsp_Fi3d06_Invert Field.cs
// WASPer_3DP - Subcategory: 2.3_Fields_3D
//
// Flips the sign of one or more WASPer 3D fields.
// Field convention: negative = material / selected region, positive = outside.

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
    public class wsp_Fi3d06_InvertField : GH_Component
    {
        private const string NAME = "wsp_Fi3d06_Invert Field";
        private const string NICK = "Invert Field";
        private const string CAT = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "2.3_Fields_3D";
        private const long MAX_SAMPLES_PER_FIELD = 20000000;

        private readonly string _versionTag;

        public wsp_Fi3d06_InvertField()
            : base(
                NAME,
                NICK,
                "Inverts one or more WASPer 3D fields by flipping the field sign.\n\n" +
                "Field convention: negative = selected material/region, positive = outside.\n" +
                "When Invert is true, the output field is -field. If bound_field is connected, " +
                "the inverted result is clipped to that boundary field using max(result, bound_field). " +
                "Use bound_field to keep the complement finite before mesh extraction.",
                CAT,
                SUBCAT)
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("3B4E541B-2FE0-4F20-9E9D-23F9B23E8E26");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Fi3d06_Invert Field.png"))
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
                "fields",
                "fields",
                "WASPer 3D field or fields to flip.\n" +
                "Convention before inversion: negative = material / selected region, positive = outside.",
                GH_ParamAccess.list);
            pManager[0].DataMapping = GH_DataMapping.Flatten;

            pManager.AddGenericParameter(
                "bound_field",
                "bound_field",
                "Optional boundary field used to clip the inverted region.\n" +
                "The boundary field should be negative inside the desired domain and positive outside.\n" +
                "When connected, the output is max(flipped_field, bound_field), so mesh extraction remains finite.",
                GH_ParamAccess.item);
            pManager[1].Optional = true;

            pManager.AddNumberParameter(
                "res",
                "res",
                "Sampling resolution in model units for mesh_inverted. Smaller values are more detailed but slower.",
                GH_ParamAccess.item,
                2.0);

            pManager.AddBooleanParameter(
                "mesh",
                "mesh?",
                "If true, extract mesh_inverted immediately. If false, only output field_inverted.",
                GH_ParamAccess.item,
                true);

            pManager.AddBooleanParameter(
                "Invert",
                "Invert",
                "If true, the output field is the negative of the input field.\n" +
                "If false, the input field sign is preserved, but bound_field is still applied when connected.",
                GH_ParamAccess.item,
                true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "field_inverted",
                "field",
                "Inverted WASPer field or fields. Negative values are inside the selected output region.",
                GH_ParamAccess.list);

            pManager.AddMeshParameter(
                "mesh_inverted",
                "mesh",
                "Extracted mesh from the inverted field or fields. Empty when mesh? is false.",
                GH_ParamAccess.list);
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

            IGH_Goo boundGoo = null;
            DA.GetData(1, ref boundGoo);

            double res = 2.0;
            bool makeMesh = true;
            bool invert = true;

            DA.GetData(2, ref res);
            DA.GetData(3, ref makeMesh);
            DA.GetData(4, ref invert);

            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;
            res = Math.Max(res, tol * 10.0);

            WasperField boundField = ExtractField(boundGoo);
            if (boundGoo != null && boundField == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "bound_field was connected but is not a valid WASPer field. It was ignored.");
            }

            var sources = new List<WasperField>();
            int rejected = 0;
            foreach (var goo in goos)
            {
                var field = ExtractField(goo);
                if (field != null && field.Evaluator != null) sources.Add(field);
                else rejected++;
            }

            if (rejected > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"{rejected} input item(s) were not valid WASPer fields and were ignored.");

            if (sources.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No valid WASPer fields found.");
                return;
            }

            if (makeMesh && invert && boundField == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "No bound_field connected. The inverted field is infinite; mesh extraction is limited to each source field domain.");
            }

            var outFields = new List<WasperFieldGoo>();
            var outMeshes = new List<Mesh>();
            int totalFaces = 0;

            for (int i = 0; i < sources.Count; i++)
            {
                WasperField source = sources[i];
                BoundingBox domain = BuildOutputDomain(source, boundField, invert, res);
                WasperField result = CreateInvertedField(source, boundField, domain, invert);
                outFields.Add(new WasperFieldGoo(result));

                if (!makeMesh)
                    continue;

                if (!domain.IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"Field {i} has no valid domain for mesh extraction.");
                    continue;
                }

                int nx, ny, nz;
                Box sampleBox = BoxFromBoundingBox(domain);
                BuildGridCounts(sampleBox, res, out nx, out ny, out nz);

                long samples = (long)nx * ny * nz;
                if (samples > MAX_SAMPLES_PER_FIELD)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        $"Field {i} would create {samples:N0} samples. Increase res or use a smaller bound_field.");
                    continue;
                }

                var scalars = new double[(int)samples];
                var points = new Point3d[(int)samples];
                SampleField(result, sampleBox, nx, ny, nz, scalars, points);

                Mesh mesh = null;
                try
                {
                    int threads = Math.Max(1, Environment.ProcessorCount - 1);
                    mesh = WasperMarchingCubes.Extract(
                        scalars,
                        points,
                        nx,
                        ny,
                        nz,
                        0.0,
                        Math.Max(res * 1e-6, tol * 0.25),
                        threads);
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        $"Marching Cubes failed on field {i} ({nx}x{ny}x{nz} grid): {ex.Message}");
                    continue;
                }

                if (mesh == null || mesh.Faces.Count == 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"No mesh was generated for field {i}. The result may be empty at this resolution.");
                    continue;
                }

                CleanResultMesh(mesh, result, Math.Max(res * 0.75, tol * 10.0));
                outMeshes.Add(mesh);
                totalFaces += mesh.Faces.Count;
            }

            DA.SetDataList(0, outFields);
            DA.SetDataList(1, outMeshes);

            Message = makeMesh
                ? $"{_versionTag} | {outMeshes.Count} mesh | {totalFaces:N0} f"
                : $"{_versionTag} | field";
        }

        private static WasperField CreateInvertedField(
            WasperField source,
            WasperField boundField,
            BoundingBox domain,
            bool invert)
        {
            return WasperFieldOps.Invert(source, boundField, domain, invert);
        }

        private static BoundingBox BuildOutputDomain(
            WasperField source,
            WasperField boundField,
            bool invert,
            double res)
        {
            BoundingBox domain = BoundingBox.Unset;

            if (boundField != null && boundField.Domain.IsValid)
            {
                domain = boundField.Domain;
            }
            else if (source != null && source.Domain.IsValid)
            {
                domain = source.Domain;
            }

            if (!domain.IsValid) return domain;

            double pad = Math.Max(res, 1e-6);
            domain.Inflate(pad);
            return domain;
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
            mesh.FaceNormals.ComputeFaceNormals();
            mesh.Compact();
        }

        private static double SafeEvaluate(WasperField field, Point3d p)
        {
            if (field == null || field.Evaluator == null) return double.PositiveInfinity;

            double value;
            try { value = field.Evaluate(p); }
            catch { value = double.PositiveInfinity; }

            return IsFinite(value) ? value : double.PositiveInfinity;
        }

        private static bool IsFinite(double value)
        {
            return !(double.IsNaN(value) || double.IsInfinity(value));
        }

        private static int Index(int ix, int iy, int iz, int nx, int ny)
        {
            return ix + nx * (iy + ny * iz);
        }
    }
}
