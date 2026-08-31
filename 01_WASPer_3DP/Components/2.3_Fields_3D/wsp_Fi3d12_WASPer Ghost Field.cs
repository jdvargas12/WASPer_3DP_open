// wsp_Fi3d12_WASPer Ghost Field.cs
// WASPer_3DP - Subcategory: 2.3_Fields_3D

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP.Components._2_3_Fields_3D
{
    public class wsp_Fi3d12_WASPerGhostField : GH_Component
    {
        private const string Cat = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string Subcategory = "2.3_Fields_3D";
        private static readonly System.Drawing.Color[] DefaultFieldColors =
        {
            System.Drawing.Color.FromArgb(54, 164, 221),
            System.Drawing.Color.FromArgb(213, 94, 0),
            System.Drawing.Color.FromArgb(0, 158, 115),
            System.Drawing.Color.FromArgb(230, 159, 0),
            System.Drawing.Color.FromArgb(204, 121, 167),
            System.Drawing.Color.FromArgb(86, 180, 233),
            System.Drawing.Color.FromArgb(120, 94, 196),
            System.Drawing.Color.FromArgb(230, 94, 132)
        };

        private readonly string _versionTag;
        private readonly List<WasperFieldPreviewRenderer> _renderers = new List<WasperFieldPreviewRenderer>();
        private BoundingBox _clippingBox = BoundingBox.Empty;

        private sealed class FieldEntry
        {
            internal WasperField Field;
            internal GH_Path Path;
            internal int BranchIndex;
            internal int ItemIndex;
            internal int FlatIndex;
        }

        public wsp_Fi3d12_WASPerGhostField()
            : base(
                "wsp_Fi3d12_WASPer Ghost Field",
                "WASPer Ghost Field",
                "Displays one or more WASPer 3D fields directly in the Rhino viewport from sampled GPU volumes, without requiring meshes. " +
                "Colors and opacities support list matching, and the same zero-level surfaces can optionally be extracted as meshes. " +
                "The idea for this WASPer-native component was inspired by Ghost Preview by AJ_Dayvie; " +
                "it is independently implemented and does not require Ghost Preview.",
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
            new Guid("EC51F27A-9B1D-4F4C-A862-AB1080C76F31");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Fi3d12_WASPer Ghost Field.png"))
                    using (var bitmap = stream != null ? new System.Drawing.Bitmap(stream) : null)
                        return bitmap != null ? new System.Drawing.Bitmap(bitmap) : null;
                }
                catch { return null; }
            }
        }

        public override BoundingBox ClippingBox => _clippingBox;

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "field",
                "field",
                "WASPer 3D fields. Tree branches and item order are preserved. " +
                "Every displayed surface uses the WASPer zero level.",
                GH_ParamAccess.tree);

            pManager.AddBoxParameter(
                "box",
                "box",
                "Optional finite box sampled for viewport preview and optional mesh extraction. " +
                "If empty, the union of every valid input field's domain is used and padded by 2x res on every side.",
                GH_ParamAccess.item);
            pManager[1].Optional = true;

            pManager.AddNumberParameter(
                "res",
                "res",
                "Field sampling resolution in model units. Smaller values are more detailed but use more memory and time.",
                GH_ParamAccess.item,
                4.0);

            pManager.AddBooleanParameter(
                "mesh",
                "mesh?",
                "If true, also extract the zero-level mesh from the sampled preview grid.",
                GH_ParamAccess.item,
                false);

            pManager.AddColourParameter(
                "color",
                "color",
                "Optional viewport preview colors. When empty, fields receive distinct default colors. " +
                "Trees match field branches by path, then branch order. A single supplied color applies to every field; " +
                "shorter branches repeat their last color.",
                GH_ParamAccess.tree);
            pManager[4].Optional = true;

            pManager.AddNumberParameter(
                "opacity",
                "opacity",
                "Viewport preview opacities from 0 to 1. Trees match field branches by path, then branch order. " +
                "A single value applies to every field; shorter branches repeat their last value.",
                GH_ParamAccess.tree,
                1.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "mesh",
                "mesh",
                "Optional zero-level meshes with the input field tree structure. Empty when mesh? is false.",
                GH_ParamAccess.tree);

            pManager.AddGenericParameter(
                "field",
                "field",
                "Unmodified WASPer fields with their input tree structure preserved.",
                GH_ParamAccess.tree);

            pManager.AddTextParameter(
                "info",
                "info",
                "Per-field sampling, GPU-volume, and optional meshing diagnostics.",
                GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;
            _clippingBox = BoundingBox.Empty;
            ClearRenderers();

            GH_Structure<IGH_Goo> fieldTree;
            GH_Structure<GH_Colour> colorTree = null;
            GH_Structure<GH_Number> opacityTree = null;
            Box box = Box.Unset;
            double resolution = 4.0;
            bool makeMesh = false;

            if (!DA.GetDataTree(0, out fieldTree) || fieldTree == null || fieldTree.DataCount == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Connect one or more WASPer 3D fields.");
                return;
            }

            // Avoid asking GH_DataAccess to collect a genuinely empty optional parameter, which
            // produces Grasshopper's yellow "failed to collect data" warning. A connected source is
            // always collected even if VolatileDataCount has not been populated yet in this solve.
            bool hasUserBox = false;
            bool boxInputAvailable = Params.Input[1].SourceCount > 0 ||
                                     Params.Input[1].VolatileDataCount > 0;
            if (boxInputAvailable)
                hasUserBox = DA.GetData(1, ref box) && box.IsValid;

            DA.GetData(2, ref resolution);
            DA.GetData(3, ref makeMesh);
            DA.GetDataTree(4, out colorTree);
            DA.GetDataTree(5, out opacityTree);

            bool useDefaultColors = Params.Input[4].SourceCount == 0 ||
                                    colorTree == null || colorTree.DataCount == 0;

            var fields = new List<FieldEntry>();
            int flatIndex = 0;
            for (int branchIndex = 0; branchIndex < fieldTree.PathCount; branchIndex++)
            {
                GH_Path path = fieldTree.Paths[branchIndex];
                System.Collections.IList branch = fieldTree.get_Branch(path);
                if (branch == null)
                    continue;

                for (int itemIndex = 0; itemIndex < branch.Count; itemIndex++)
                {
                    WasperField field = ExtractField(branch[itemIndex] as IGH_Goo);
                    if (field?.Evaluator == null)
                    {
                        AddRuntimeMessage(
                            GH_RuntimeMessageLevel.Warning,
                            $"Field {path}[{itemIndex}] is not a valid WASPer 3D field and was skipped.");
                        flatIndex++;
                        continue;
                    }

                    fields.Add(new FieldEntry
                    {
                        Field = field,
                        Path = path,
                        BranchIndex = branchIndex,
                        ItemIndex = itemIndex,
                        FlatIndex = flatIndex
                    });
                    flatIndex++;
                }
            }

            if (fields.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No valid WASPer 3D fields were supplied.");
                return;
            }

            double tolerance = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;
            resolution = Math.Max(resolution, tolerance * 10.0);

            if (!hasUserBox)
            {
                if (!TryInferBoxFromFieldDomains(fields, resolution, out box))
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        "Connect a valid sampling box, or connect field(s) with a valid domain.");
                    return;
                }
            }

            BuildGridCounts(box, resolution, out int nx, out int ny, out int nz);
            long samplesPerField = (long)nx * ny * nz;
            if (samplesPerField > int.MaxValue)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    $"One field preview requires {samplesPerField:N0} samples, which exceeds the CLR array-index limit. " +
                    "Increase res or reduce the box.");
                return;
            }

            var totalWatch = Stopwatch.StartNew();
            _clippingBox = box.BoundingBox;
            EnsureRendererCount(fields.Count);

            var meshOutputs = new GH_Structure<GH_Mesh>();
            var fieldOutputs = new GH_Structure<WasperFieldGoo>();
            var infoOutputs = new GH_Structure<GH_String>();

            for (int fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
            {
                FieldEntry entry = fields[fieldIndex];
                WasperField field = entry.Field;
                System.Drawing.Color color = useDefaultColors
                    ? DefaultFieldColors[fieldIndex % DefaultFieldColors.Length]
                    : ResolveTreeValue(
                        colorTree,
                        entry.Path,
                        entry.BranchIndex,
                        entry.ItemIndex,
                        entry.FlatIndex,
                        new GH_Colour(DefaultFieldColors[fieldIndex % DefaultFieldColors.Length])).Value;
                double opacity = Math.Max(
                    0.0,
                    Math.Min(
                        1.0,
                        ResolveTreeValue(
                            opacityTree,
                            entry.Path,
                            entry.BranchIndex,
                            entry.ItemIndex,
                            entry.FlatIndex,
                            new GH_Number(1.0)).Value));
                var values = new float[(int)samplesPerField];
                Point3d[] points = makeMesh ? new Point3d[(int)samplesPerField] : null;
                double[] meshValues = makeMesh ? new double[(int)samplesPerField] : null;

                var fieldWatch = Stopwatch.StartNew();
                var sampleWatch = Stopwatch.StartNew();
                SampleField(field, box, nx, ny, nz, values, points, meshValues, out float minimum, out float maximum);
                sampleWatch.Stop();

                var previewGrid = new WasperFieldPreviewGrid(
                    box,
                    nx,
                    ny,
                    nz,
                    values,
                    minimum,
                    maximum,
                    resolution);
                _renderers[fieldIndex].SetGrid(previewGrid, color, opacity);
                fieldOutputs.Append(new WasperFieldGoo(field), entry.Path);

                long meshMs = 0;
                string meshSummary = "mesh extraction skipped";
                Mesh mesh = null;
                if (makeMesh)
                {
                    var meshWatch = Stopwatch.StartNew();
                    mesh = WasperMarchingCubes.Extract(
                        meshValues,
                        points,
                        nx,
                        ny,
                        nz,
                        0.0,
                        Math.Max(tolerance * 0.25, 1e-7));
                    meshWatch.Stop();
                    meshMs = meshWatch.ElapsedMilliseconds;

                    if (mesh != null && mesh.Faces.Count > 0)
                    {
                        CleanResultMesh(mesh, field, Math.Max(tolerance * 10.0, resolution * 0.25));
                        meshSummary = $"mesh vertices/faces: {mesh.Vertices.Count:N0} / {mesh.Faces.Count:N0}";
                    }
                    else
                    {
                        mesh = null;
                        meshSummary = "no zero-level mesh found";
                        AddRuntimeMessage(
                            GH_RuntimeMessageLevel.Warning,
                            $"Field {fieldIndex} has no zero-level surface inside the box.");
                    }

                    meshOutputs.Append(new GH_Mesh(mesh), entry.Path);
                }

                if (!previewGrid.BracketsZero)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"Field {fieldIndex} does not cross zero inside the box " +
                        $"(range {minimum:G5} to {maximum:G5}).");
                }

                fieldWatch.Stop();
                double textureMiB = values.LongLength * sizeof(float) / (1024.0 * 1024.0);
                infoOutputs.Append(new GH_String(
                    $"WASPer Ghost Field [{fieldIndex}]\n" +
                    $"input            : {entry.Path}[{entry.ItemIndex}]\n" +
                    $"field            : {(string.IsNullOrWhiteSpace(field.Label) ? "(unnamed)" : field.Label)}\n" +
                    $"quality          : {field.SdfQuality}\n" +
                    $"color            : {color.R}, {color.G}, {color.B}\n" +
                    $"opacity          : {opacity:F3}\n" +
                    $"box source       : {(hasUserBox ? "input" : "field domain union + 2x res padding")}\n" +
                    $"resolution       : {resolution:F4}\n" +
                    $"grid             : {nx}x{ny}x{nz}\n" +
                    $"samples          : {samplesPerField:N0}\n" +
                    $"field range      : {minimum:G6} to {maximum:G6}\n" +
                    $"brackets zero    : {previewGrid.BracketsZero}\n" +
                    $"GPU texture      : {textureMiB:F2} MiB (R32F)\n" +
                    $"sample_ms        : {sampleWatch.ElapsedMilliseconds}\n" +
                    $"mesh?            : {makeMesh}\n" +
                    $"mesh_ms          : {meshMs}\n" +
                    $"elapsed_ms       : {fieldWatch.ElapsedMilliseconds}\n" +
                    meshSummary), entry.Path);
            }

            totalWatch.Stop();
            DA.SetDataTree(0, meshOutputs);
            DA.SetDataTree(1, fieldOutputs);
            DA.SetDataTree(2, infoOutputs);

            Message = makeMesh
                ? $"{_versionTag} | {fields.Count} fields + mesh | {totalWatch.ElapsedMilliseconds}ms"
                : $"{_versionTag} | {fields.Count} fields | {totalWatch.ElapsedMilliseconds}ms";
        }

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            // The optional mesh remains available as data, but drawing it together with
            // the raymarched surface creates a coincident white/colored double preview.
            bool drewAny = false;
            foreach (WasperFieldPreviewRenderer renderer in _renderers)
                drewAny |= renderer.Draw(args.Display);

            if (!drewAny)
                base.DrawViewportMeshes(args);
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            foreach (WasperFieldPreviewRenderer renderer in _renderers)
                renderer.Dispose();
            _renderers.Clear();
            base.RemovedFromDocument(document);
        }

        private void ClearRenderers()
        {
            foreach (WasperFieldPreviewRenderer renderer in _renderers)
                renderer.Clear();
        }

        private void EnsureRendererCount(int count)
        {
            while (_renderers.Count < count)
                _renderers.Add(new WasperFieldPreviewRenderer());

            while (_renderers.Count > count)
            {
                int index = _renderers.Count - 1;
                _renderers[index].Dispose();
                _renderers.RemoveAt(index);
            }
        }

        private static T ResolveTreeValue<T>(
            GH_Structure<T> tree,
            GH_Path fieldPath,
            int fieldBranchIndex,
            int fieldItemIndex,
            int fieldFlatIndex,
            T fallback)
            where T : class, IGH_Goo
        {
            if (tree == null || tree.DataCount == 0)
                return fallback;

            if (tree.PathCount == 1)
            {
                System.Collections.IList onlyBranch = tree.get_Branch(tree.Paths[0]);
                if (onlyBranch == null || onlyBranch.Count == 0)
                    return fallback;
                return onlyBranch[Math.Min(fieldFlatIndex, onlyBranch.Count - 1)] as T ?? fallback;
            }

            System.Collections.IList branch = tree.PathExists(fieldPath)
                ? tree.get_Branch(fieldPath)
                : tree.get_Branch(tree.Paths[Math.Min(fieldBranchIndex, tree.PathCount - 1)]);
            if (branch == null || branch.Count == 0)
                return fallback;
            return branch[Math.Min(fieldItemIndex, branch.Count - 1)] as T ?? fallback;
        }

        private static WasperField ExtractField(IGH_Goo goo)
        {
            object value = WasperIsopodBridge.Unwrap(goo);
            if (value is WasperField field) return field;
            if (value is WasperFieldGoo fieldGoo) return fieldGoo.Value;
            return null;
        }

        private static bool TryInferBoxFromFieldDomains(List<FieldEntry> fields, double resolution, out Box box)
        {
            box = Box.Unset;

            BoundingBox union = BoundingBox.Empty;
            foreach (FieldEntry entry in fields)
            {
                if (entry.Field?.Domain == null || !entry.Field.Domain.IsValid)
                    continue;
                union.Union(entry.Field.Domain);
            }

            if (!union.IsValid)
                return false;

            // Keep two complete sample cells around the field domain on every side. Tying the
            // margin to resolution provides consistent preview breathing room at every detail level.
            union.Inflate(Math.Max(resolution, 1e-9) * 2.0);
            if (!union.IsValid)
                return false;

            box = new Box(
                Plane.WorldXY,
                new Interval(union.Min.X, union.Max.X),
                new Interval(union.Min.Y, union.Max.Y),
                new Interval(union.Min.Z, union.Max.Z));
            return box.IsValid;
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
            float[] values,
            Point3d[] points,
            double[] meshValues,
            out float minimum,
            out float maximum)
        {
            object rangeLock = new object();
            float globalMinimum = float.PositiveInfinity;
            float globalMaximum = float.NegativeInfinity;

            Parallel.For(
                0,
                nz,
                () => (Minimum: float.PositiveInfinity, Maximum: float.NegativeInfinity),
                (iz, _, local) =>
                {
                    double w = nz <= 1 ? 0.0 : (double)iz / (nz - 1);
                    for (int iy = 0; iy < ny; iy++)
                    {
                        double v = ny <= 1 ? 0.0 : (double)iy / (ny - 1);
                        for (int ix = 0; ix < nx; ix++)
                        {
                            double u = nx <= 1 ? 0.0 : (double)ix / (nx - 1);
                            int index = ix + nx * (iy + ny * iz);
                            Point3d point = box.PointAt(u, v, w);
                            double scalar = WasperFieldOps.SafeEvaluate(field, point);
                            float value = (float)Math.Max(-1e30, Math.Min(1e30, scalar));

                            values[index] = value;
                            if (points != null) points[index] = point;
                            if (meshValues != null) meshValues[index] = scalar;
                            if (value < local.Minimum) local.Minimum = value;
                            if (value > local.Maximum) local.Maximum = value;
                        }
                    }

                    return local;
                },
                local =>
                {
                    lock (rangeLock)
                    {
                        if (local.Minimum < globalMinimum) globalMinimum = local.Minimum;
                        if (local.Maximum > globalMaximum) globalMaximum = local.Maximum;
                    }
                });

            minimum = globalMinimum;
            maximum = globalMaximum;
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
    }
}
