// wsp_Fi3d10_Isopod to WASPer Field.cs
// WASPer_3DP - Subcategory: 2.3_Fields_3D

using System;
using System.Diagnostics;
using System.Reflection;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP.Components._2_3_Fields_3D
{
    public class wsp_Fi3d10_IsopodToWasperField : GH_Component
    {
        private const string Cat = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string Subcategory = "2.3_Fields_3D";
        private const long MaxSamples = 20000000;
        private readonly string _versionTag;

        public wsp_Fi3d10_IsopodToWasperField()
            : base(
                "wsp_Fi3d10_Isopod to WASPer Field",
                "Isopod -> WASPer",
                "Wraps an Isopod Field/IField as a live WASPer 3D implicit field and optionally extracts its zero iso-surface mesh. " +
                "The supplied box defines the finite domain used by WASPer meshing and field operations.",
                Cat,
                Subcategory)
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version != null
                ? $"v{version.Major}.{version.Minor}.{version.Build}"
                : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("BB8DDB0E-6ECD-49D4-A9F4-723AA20ED9FD");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Fi3d10_Isopod to WASPer Field.png"))
                    using (var bitmap = stream != null ? new System.Drawing.Bitmap(stream) : null)
                        return bitmap != null ? new System.Drawing.Bitmap(bitmap) : null;
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "isopod_field",
                "isopod",
                "Isopod Field or IField object exposing double ValueAt(Point3d).",
                GH_ParamAccess.item);

            pManager.AddBoxParameter(
                "domain",
                "domain",
                "Finite evaluation/meshing domain for the resulting WASPer field.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "res",
                "res",
                "Mesh sampling resolution in model units. Smaller values are more detailed but slower.",
                GH_ParamAccess.item,
                2.0);

            pManager.AddBooleanParameter(
                "mesh",
                "mesh?",
                "If true, extract the Isopod field's zero iso-surface mesh. If false, only output the WASPer field.",
                GH_ParamAccess.item,
                true);

            pManager.AddTextParameter(
                "label",
                "label",
                "Optional WASPer field label.",
                GH_ParamAccess.item,
                "Isopod field");
            pManager[4].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "mesh",
                "mesh",
                "Extracted zero iso-surface mesh. Empty when mesh? is false.",
                GH_ParamAccess.item);

            pManager.AddGenericParameter(
                "wasper_field",
                "field",
                "Live WASPer 3D implicit field backed by the Isopod evaluator.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "source_type",
                "type",
                "Detected Isopod runtime type.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "info",
                "info",
                "Bridge and optional mesh-extraction diagnostics.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            IGH_Goo goo = null;
            Box domain = Box.Unset;
            double resolution = 2.0;
            bool makeMesh = true;
            string label = "Isopod field";

            if (!DA.GetData(0, ref goo) || goo == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Connect an Isopod Field/IField.");
                return;
            }

            if (!DA.GetData(1, ref domain) || !domain.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Connect a valid domain box.");
                return;
            }

            DA.GetData(2, ref resolution);
            DA.GetData(3, ref makeMesh);
            DA.GetData(4, ref label);
            if (string.IsNullOrWhiteSpace(label))
                label = "Isopod field";

            double tolerance = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;
            resolution = Math.Max(resolution, tolerance * 10.0);

            object source = WasperIsopodBridge.Unwrap(goo);
            if (!WasperIsopodBridge.TryCreateWasperEvaluator(
                    source,
                    out Func<Point3d, double> evaluator,
                    out string sourceType,
                    out string error))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error);
                return;
            }

            var field = new WasperField(
                evaluator,
                domain.BoundingBox,
                label,
                $"Source: Isopod field [{sourceType}]",
                WasperFieldSdfQuality.ImplicitScalarField);

            DA.SetData(1, new WasperFieldGoo(field));
            DA.SetData(2, sourceType);

            int nx = 0;
            int ny = 0;
            int nz = 0;
            long samples = 0;
            long sampleMs = 0;
            long meshMs = 0;
            string meshSummary = "mesh extraction skipped";
            var totalWatch = Stopwatch.StartNew();

            if (makeMesh)
            {
                var meshOptions = new WasperUniformFieldMeshOptions
                {
                    Resolution = resolution,
                    IsoLevel = 0.0,
                    KeyTolerance = Math.Max(tolerance * 0.25, 1e-7),
                    SlabDepth = 24,
                    HardMaxSamples = MaxSamples
                };

                try
                {
                    if (!WasperUniformFieldMesher.TryExtract(field, domain, meshOptions, out Mesh mesh, out WasperUniformFieldMeshStats meshStats, out string meshError))
                    {
                        nx = meshStats?.Nx ?? 0;
                        ny = meshStats?.Ny ?? 0;
                        nz = meshStats?.Nz ?? 0;
                        samples = meshStats?.Samples ?? 0;

                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, meshError);
                        DA.SetData(3, BuildInfo(sourceType, domain, resolution, true, nx, ny, nz, samples, 0, 0, "sample cap exceeded", totalWatch.ElapsedMilliseconds));
                        return;
                    }

                    nx = meshStats.Nx;
                    ny = meshStats.Ny;
                    nz = meshStats.Nz;
                    samples = meshStats.Samples;
                    sampleMs = meshStats.SampleMs;
                    meshMs = meshStats.MeshMs;

                    if (mesh != null && mesh.Faces.Count > 0)
                    {
                        CleanResultMesh(mesh, field, Math.Max(tolerance * 10.0, resolution * 0.25));
                        DA.SetData(0, mesh);
                        meshSummary = $"mesh vertices/faces: {mesh.Vertices.Count:N0} / {mesh.Faces.Count:N0}";
                    }
                    else
                    {
                        meshSummary = "no zero iso-surface found";
                        AddRuntimeMessage(
                            GH_RuntimeMessageLevel.Warning,
                            "No mesh was generated. Check that the Isopod field crosses zero inside the domain.");
                    }
                }
                catch (Exception ex)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        "Isopod field sampling failed: " + WasperUniformFieldMesher.InnermostMessage(ex));
                    meshSummary = "sampling failed";
                }
            }

            totalWatch.Stop();
            DA.SetData(3, BuildInfo(
                sourceType,
                domain,
                resolution,
                makeMesh,
                nx,
                ny,
                nz,
                samples,
                sampleMs,
                meshMs,
                meshSummary,
                totalWatch.ElapsedMilliseconds));

            Message = makeMesh
                ? _versionTag + " | linked + mesh"
                : _versionTag + " | linked";
        }

        private static void CleanResultMesh(Mesh mesh, WasperField field, double gradientStep)
        {
            mesh.Vertices.CombineIdentical(true, true);
            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();
            WasperFieldNormalTools.OrientFacesByFieldGradient(mesh, field, gradientStep);
            mesh.Weld(Math.PI);
            mesh.Normals.ComputeNormals();
            mesh.Compact();
        }

        private static string BuildInfo(
            string sourceType,
            Box domain,
            double resolution,
            bool makeMesh,
            int nx,
            int ny,
            int nz,
            long samples,
            long sampleMs,
            long meshMs,
            string meshSummary,
            long elapsedMs)
        {
            BoundingBox bounds = domain.BoundingBox;
            return
                "Isopod to WASPer Field\n" +
                $"source type     : {sourceType}\n" +
                "field quality   : ImplicitScalarField\n" +
                $"resolution      : {resolution:F4}\n" +
                $"mesh?           : {makeMesh}\n" +
                $"domain min      : {bounds.Min.X:F3}, {bounds.Min.Y:F3}, {bounds.Min.Z:F3}\n" +
                $"domain max      : {bounds.Max.X:F3}, {bounds.Max.Y:F3}, {bounds.Max.Z:F3}\n" +
                $"grid            : {(makeMesh ? $"{nx}x{ny}x{nz}" : "(not sampled)")}\n" +
                $"samples         : {samples:N0}\n" +
                $"sample_ms       : {sampleMs}\n" +
                $"mesh_ms         : {meshMs}\n" +
                $"elapsed_ms      : {elapsedMs}\n" +
                meshSummary;
        }
    }
}
