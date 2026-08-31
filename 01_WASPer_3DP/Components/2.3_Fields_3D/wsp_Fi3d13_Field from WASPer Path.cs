// wsp_Fi3d13_Field from WASPer Path.cs
// Builds one implicit bead solid from a packed WASPer printing path.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Threading.Tasks;
using Grasshopper.Kernel;
using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP.Components._2_3_Fields_3D
{
    public sealed class wsp_Fi3d13_FieldFromWasperPath : GH_Component
    {
        private const string NameText = "wsp_Fi3d13_Field from WASPer Path";
        private const string CategoryName = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string Subcategory = "2.3_Fields_3D";
        private const long MaximumSamples = 20000000;
        private readonly string _versionTag;

        public wsp_Fi3d13_FieldFromWasperPath()
            : base(
                NameText,
                "Path Field",
                "Converts a packed WASPer printing path into one unified 3D field. " +
                "Each path segment becomes a variable superellipse bead using its fabrication metadata; " +
                "all beads are joined by a sharp field union. An optional single mesh can be generated for thermal simulation.",
                CategoryName,
                Subcategory)
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version == null
                ? "v1.0.5"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("7D6F58A9-63B2-4D55-9AA9-2C6E84A10F31");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon => CreateIcon();

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "Packed WASPer printing path. Layer height, flow-adjusted width, path frames, roles, torn segments, " +
                "and PrintVol metadata are used to reconstruct the deposited beads.",
                GH_ParamAccess.item);

            pManager.AddParameter(WasperTargetRolesParam.Create(
                "Roles to include in the field: 0 All, 1 Shell, 2 Infill, 3 Partition, " +
                "4 Support, 5 Transition, or 6 Undefined."));

            pManager.AddNumberParameter(
                "profile_n",
                "prof_n",
                "Superellipse exponent for each bead profile. 2 is elliptical; 4 matches the rounded-square profile used by Pp04.",
                GH_ParamAccess.item,
                4.0);

            pManager.AddNumberParameter(
                "bond_overlap",
                "overlap",
                "Extra bead depth in model units, applied only toward the previous layer (-path plane Z). " +
                "This improves interlayer bonding without widening the bead sides. Use 0 to preserve the Pp04 envelope.",
                GH_ParamAccess.item,
                0.0);

            pManager.AddBoxParameter(
                "sample_box",
                "box",
                "Optional sampling box for mesh extraction. If omitted, the bead-field domain is padded automatically.",
                GH_ParamAccess.item);
            pManager[4].Optional = true;

            pManager.AddNumberParameter(
                "resolution",
                "res",
                "Mesh sampling resolution in model units. Smaller values preserve more bead detail but require more memory and time.",
                GH_ParamAccess.item,
                2.0);

            pManager.AddBooleanParameter(
                "mesh?",
                "mesh?",
                "Generate one unified iso-surface mesh. False keeps the component field-only and avoids the sampling cost.",
                GH_ParamAccess.item,
                false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "field",
                "field",
                "One WASPer 3D field containing the sharp union of all selected superellipse path beads. " +
                "Negative values are material; positive values are outside.",
                GH_ParamAccess.item);

            pManager.AddMeshParameter(
                "mesh",
                "mesh",
                "Optional single iso-surface mesh of the unified field. Empty when mesh? is false.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "info",
                "info",
                "Field construction, metadata, meshing, and volume diagnostics.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            if (!WasperGcodeTreeUtil.TryGetPrintPath(DA, 0, out WasperPrintPath path))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Connect one valid packed wsp_path.");
                return;
            }

            var rawRoles = new List<int>();
            DA.GetDataList(1, rawRoles);
            if (!WasperGcodeTreeUtil.TryNormalizeTargetRoles(rawRoles, out List<int> roles, out string roleError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, roleError);
                return;
            }

            double profileExponent = 4.0;
            double bondOverlap = 0.0;
            double resolution = 2.0;
            bool createMesh = false;
            DA.GetData(2, ref profileExponent);
            DA.GetData(3, ref bondOverlap);
            DA.GetData(5, ref resolution);
            DA.GetData(6, ref createMesh);

            if (!double.IsFinite(profileExponent) || profileExponent < 1.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "profile_n must be a finite number greater than or equal to 1.");
                return;
            }
            if (!double.IsFinite(bondOverlap) || bondOverlap < 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "bond_overlap must be a finite, non-negative distance.");
                return;
            }

            double tolerance = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;
            resolution = Math.Max(resolution, tolerance * 10.0);

            WasperPrintPathBeadFieldResult built = WasperPrintPathBeadFieldBuilder.Build(
                path,
                roles,
                profileExponent,
                bondOverlap,
                tolerance);

            if (built.Field == null || built.Segments == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "No valid path segments matched the selected roles. Check path planes, roles, dimensions, and torn metadata.");
                return;
            }

            var fieldGoo = new WasperFieldGoo(built.Field);
            DA.SetData(0, fieldGoo);

            Box userBox = new Box();
            bool hasUserBox = DA.GetData(4, ref userBox) && userBox.IsValid;
            Box sampleBox = ResolveSampleBox(built.Field, hasUserBox, userBox, resolution, bondOverlap);

            Mesh mesh = null;
            long sampleCount = 0;
            double meshVolume = double.NaN;

            if (createMesh)
            {
                BuildGridCounts(sampleBox, resolution, out int nx, out int ny, out int nz);
                sampleCount = (long)nx * ny * nz;
                if (sampleCount > MaximumSamples)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        $"The requested mesh needs {sampleCount:N0} samples. Increase res or provide a smaller sample_box.");
                    return;
                }

                var scalars = new double[(int)sampleCount];
                var points = new Point3d[(int)sampleCount];
                SampleField(built.Field, sampleBox, nx, ny, nz, scalars, points);
                mesh = WasperMarchingCubes.Extract(
                    scalars,
                    points,
                    nx,
                    ny,
                    nz,
                    0.0,
                    Math.Max(tolerance * 0.25, 1e-7));

                if (mesh != null && mesh.Faces.Count > 0)
                {
                    CleanMesh(mesh, built.Field, resolution, tolerance);
                    DA.SetData(1, mesh);
                    if (mesh.IsClosed)
                    {
                        using (VolumeMassProperties properties = VolumeMassProperties.Compute(mesh))
                            if (properties != null) meshVolume = Math.Abs(properties.Volume);
                    }
                }
                else
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        "The field was created, but no mesh surface was found in the sampling box.");
                }
            }

            double smallestFeature = Math.Min(built.MinimumWidth, built.MinimumHeight);
            if (createMesh && double.IsFinite(smallestFeature) && resolution > smallestFeature / 3.0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "The mesh resolution is coarse relative to the smallest bead dimension. " +
                    "Use approximately one third of the smallest layer height/width or finer for thermal geometry.");
            }

            string roleText = WasperGcodeTreeUtil.TargetsAllRoles(roles)
                ? "All"
                : string.Join(", ", roles.ConvertAll(WasperGcodeTreeUtil.TargetRoleName));
            string volumeText = double.IsFinite(meshVolume)
                ? $"mesh volume={meshVolume:F3}; mesh/source PrintVol={(built.SourcePrintVolume > tolerance ? meshVolume / built.SourcePrintVolume : double.NaN):F4}"
                : "mesh volume=n/a";

            string info =
                $"Field from WASPer Path | {_versionTag}\n" +
                $"roles            : {roleText}\n" +
                $"branches         : {built.IncludedBranches}/{built.InputBranches}\n" +
                $"bead segments    : {built.Segments:N0}\n" +
                $"profile_n        : {profileExponent:F3}\n" +
                $"bond_overlap     : {bondOverlap:F4}\n" +
                $"width range      : {built.MinimumWidth:F4} to {built.MaximumWidth:F4}\n" +
                $"height range     : {built.MinimumHeight:F4} to {built.MaximumHeight:F4}\n" +
                $"source PrintVol  : {built.SourcePrintVolume:F3}\n" +
                $"fallbacks w/h    : {built.WidthFallbacks}/{built.HeightFallbacks}\n" +
                $"skipped short/invalid/torn: {built.SkippedShortBranches}/{built.SkippedInvalidSegments}/{built.SkippedTornSegments}\n" +
                $"mesh             : {(createMesh ? (mesh == null ? "not generated" : $"{mesh.Vertices.Count:N0} vertices, {mesh.Faces.Count:N0} faces") : "disabled")}\n" +
                $"samples          : {sampleCount:N0}\n" +
                volumeText + "\n" +
                "Note: at bond_overlap=0 the field matches the Pp04 superellipse bead envelope. " +
                "Vertical bonding and sharp union can make mesh volume differ from summed PrintVol.";
            DA.SetData(2, info);
            Message = createMesh ? $"{_versionTag} | {built.Segments:N0} seg | mesh" : $"{_versionTag} | {built.Segments:N0} seg";
        }

        private static Box ResolveSampleBox(
            WasperField field,
            bool hasUserBox,
            Box userBox,
            double resolution,
            double bondOverlap)
        {
            if (hasUserBox) return userBox;
            BoundingBox bounds = field.Domain;
            bounds.Inflate(Math.Max(resolution * 2.0, bondOverlap + resolution));
            return new Box(
                Plane.WorldXY,
                new Interval(bounds.Min.X, bounds.Max.X),
                new Interval(bounds.Min.Y, bounds.Max.Y),
                new Interval(bounds.Min.Z, bounds.Max.Z));
        }

        private static void BuildGridCounts(Box box, double resolution, out int nx, out int ny, out int nz)
        {
            nx = Math.Max(2, (int)Math.Ceiling(Math.Abs(box.X.Length) / resolution) + 1);
            ny = Math.Max(2, (int)Math.Ceiling(Math.Abs(box.Y.Length) / resolution) + 1);
            nz = Math.Max(2, (int)Math.Ceiling(Math.Abs(box.Z.Length) / resolution) + 1);
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
                double w = (double)iz / (nz - 1);
                for (int iy = 0; iy < ny; iy++)
                {
                    double v = (double)iy / (ny - 1);
                    for (int ix = 0; ix < nx; ix++)
                    {
                        double u = (double)ix / (nx - 1);
                        int index = ix + nx * (iy + ny * iz);
                        Point3d point = box.PointAt(u, v, w);
                        points[index] = point;
                        double value;
                        try { value = field.Evaluate(point); }
                        catch { value = double.PositiveInfinity; }
                        scalars[index] = double.IsFinite(value) ? value : double.PositiveInfinity;
                    }
                }
            });
        }

        private static void CleanMesh(Mesh mesh, WasperField field, double resolution, double tolerance)
        {
            mesh.Vertices.CombineIdentical(true, true);
            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();
            mesh.UnifyNormals();
            mesh.Weld(Math.PI);
            WasperFieldNormalTools.OrientFacesByFieldGradient(
                mesh,
                field,
                Math.Max(resolution * 0.5, tolerance * 10.0));
            mesh.Normals.ComputeNormals();
            mesh.FaceNormals.ComputeFaceNormals();
            mesh.Compact();
        }

        private static Bitmap CreateIcon()
        {
            var bitmap = new Bitmap(24, 24);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (var pathPen = new Pen(Color.FromArgb(235, 113, 41), 2.4f))
            using (var fieldPen = new Pen(Color.FromArgb(46, 150, 92), 1.4f))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                graphics.DrawEllipse(fieldPen, 3, 3, 18, 18);
                graphics.DrawEllipse(fieldPen, 6, 6, 12, 12);
                graphics.DrawBezier(pathPen, 2, 17, 7, 3, 15, 22, 22, 7);
            }
            return bitmap;
        }
    }
}
