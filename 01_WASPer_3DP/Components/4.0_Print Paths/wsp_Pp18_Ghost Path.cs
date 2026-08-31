using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;

using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP.Components._4_0_Print_Paths
{
    /// <summary>
    /// Minimal meshless preview and pass-through component for packed WASPer paths.
    /// </summary>
    public sealed class wsp_Pp18_GhostPath : GH_Component
    {
        private readonly List<global::WASPer_3DP.WasperPrintPathSegmentRenderer> _renderers =
            new List<global::WASPer_3DP.WasperPrintPathSegmentRenderer>();
        private BoundingBox _clippingBox = BoundingBox.Empty;
        private readonly string _versionTag;

        public wsp_Pp18_GhostPath()
            : base(
                "wsp_Pp18_Ghost Path",
                "Ghost Path",
                "Displays a packed WASPer Print Path directly in the Rhino viewport using the meshless GPU path renderer. " +
                "The input path is passed through unchanged so this component can remain inline in a fabrication workflow. \n\n" +
                "The idea for this WASPer-native component was inspired by Ghost Preview by AJ_Dayvie",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "4.0_Print Paths")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version != null
                ? $"v{version.Major}.{version.Minor}.{version.Build}"
                : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("6D830E77-1C39-49B7-ADCE-519BC62B75D4");

        public override GH_Exposure Exposure => GH_Exposure.secondary;
        public override BoundingBox ClippingBox => _clippingBox;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                               "WASPer_3DP.Resources.Icons.wsp_Fi3d12_WASPer Ghost Field.png"))
                    using (var bitmap = stream != null ? new Bitmap(stream) : null)
                        return bitmap != null ? new Bitmap(bitmap) : null;
                }
                catch
                {
                    return null;
                }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "wasper_path",
                "wsp_path",
                "Packed WASPer Print Path. Point planes, deposited widths, layer heights, and path roles drive the meshless preview.",
                GH_ParamAccess.item);

        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "wasper_path",
                "wsp_path",
                "The original packed WASPer Print Path, passed through unchanged.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            ClearRenderers();
            _clippingBox = BoundingBox.Empty;
            Message = _versionTag;

            global::WASPer_3DP.WasperPrintPath path = null;
            if (!global::WASPer_3DP.WasperGcodeTreeUtil.TryGetPrintPath(DA, 0, out path) ||
                path == null || !path.HasPoints)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "wsp_path must contain at least one branch with printing points.");
                return;
            }

            double tolerance = RhinoDoc.ActiveDoc != null
                ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
                : RhinoMath.SqrtEpsilon;

            var strokesByRole = new List<global::WASPer_3DP.WasperPrintPathPreviewStroke>[6];
            for (int i = 0; i < strokesByRole.Length; i++)
                strokesByRole[i] = new List<global::WASPer_3DP.WasperPrintPathPreviewStroke>();

            foreach (GH_Path branchPath in path.Points.Paths)
            {
                IList<Point3d> sourcePoints = path.Points.Branch(branchPath);
                if (sourcePoints == null || sourcePoints.Count < 2)
                    continue;

                var points = new List<Point3d>(sourcePoints);
                double fallbackHeight = ResolveRepresentative(path.LayerH, branchPath, 1.0, tolerance);
                double fallbackWidth = ResolveRepresentative(
                    path.LayerW,
                    branchPath,
                    Math.Max(tolerance * 10.0, fallbackHeight * 2.5),
                    tolerance);

                double[] heights = ResolveValues(path.LayerH, branchPath, points.Count, fallbackHeight, tolerance);
                double[] nominalWidths = ResolveValues(path.LayerW, branchPath, points.Count, fallbackWidth, tolerance);
                double[] widths = ResolveValues(path.LayerWf, branchPath, points.Count, double.NaN, tolerance);
                var heightDirections = ResolveHeightDirections(path.PtPlanes, branchPath, points.Count);

                for (int i = 0; i < widths.Length; i++)
                {
                    if (!double.IsFinite(widths[i]) || widths[i] <= tolerance)
                        widths[i] = nominalWidths[i];
                    if (!double.IsFinite(widths[i]) || widths[i] <= tolerance)
                        widths[i] = Math.Max(tolerance * 10.0, heights[i] * 2.5);
                }

                bool closed = IsClosed(path, branchPath, points, widths, tolerance);
                if (closed && points.Count > 2 &&
                    points[0].DistanceTo(points[points.Count - 1]) <=
                    Math.Max(tolerance * 10.0, widths[0] * 0.8))
                {
                    points.RemoveAt(points.Count - 1);
                    heights = TrimLast(heights);
                    widths = TrimLast(widths);
                    heightDirections = TrimLast(heightDirections);
                }

                if (points.Count < 2)
                    continue;

                global::WASPer_3DP.WasperPathRole role =
                    global::WASPer_3DP.WasperGcodeTreeUtil.PathRoleAt(path.PathRoles, branchPath);
                strokesByRole[RoleColorIndex(role)].Add(
                    new global::WASPer_3DP.WasperPrintPathPreviewStroke(
                        points,
                        widths,
                        heights,
                        heightDirections,
                        closed));
            }

            var batches = new List<global::WASPer_3DP.WasperPrintPathPreviewBatch>();
            for (int roleIndex = 0; roleIndex < strokesByRole.Length; roleIndex++)
            {
                if (strokesByRole[roleIndex].Count == 0)
                    continue;

                global::WASPer_3DP.WasperPathRole role = RoleFromColorIndex(roleIndex);
                Color color = global::WASPer_3DP.WasperPrintPathPreviewSettings.ResolveColor(role);
                List<global::WASPer_3DP.WasperPrintPathPreviewBatch> roleBatches =
                    global::WASPer_3DP.WasperPrintPathPreviewBuilder.Build(
                        strokesByRole[roleIndex],
                        tolerance,
                        color);

                foreach (global::WASPer_3DP.WasperPrintPathPreviewBatch batch in roleBatches)
                {
                    batches.Add(batch);
                    _clippingBox.Union(batch.Bounds);
                }
            }

            // Light/shading/profile-exponent uniforms are applied per-frame in
            // DrawViewportMeshes instead of here, so slider changes in the
            // WASPer display menu redraw live without re-running SolveInstance.
            EnsureRendererCount(batches.Count);
            int segmentCount = 0;
            for (int i = 0; i < batches.Count; i++)
            {
                _renderers[i].SetBatch(batches[i]);
                segmentCount += batches[i].SegmentCount;
            }

            DA.SetData(0, new global::WASPer_3DP.WasperPrintPathGoo(path));
            Message = $"{_versionTag} | {segmentCount:N0} segments";

            if (segmentCount == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "No valid preview segments could be generated from wsp_path.");
            }
        }

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            // Ambient/shade strength/light direction/bead profile exponent are
            // pure shader uniforms, not baked into the batch geometry, so they
            // are refreshed here on every redraw (cheap) instead of only in
            // SolveInstance. This lets the WASPer display menu sliders update
            // the preview live, without expiring the solution on every tick.
            bool drewAny = false;
            Vector3d lightDirection = global::WASPer_3DP.WasperPrintPathPreviewSettings.LightDirection;
            double ambient = global::WASPer_3DP.WasperPrintPathPreviewSettings.Ambient;
            double shadeStrength = global::WASPer_3DP.WasperPrintPathPreviewSettings.ShadeStrength;
            int profileExponent = global::WASPer_3DP.WasperPrintPathPreviewSettings.BeadProfileExponent;
            foreach (global::WASPer_3DP.WasperPrintPathSegmentRenderer renderer in _renderers)
            {
                renderer.SetLightDirection(lightDirection);
                renderer.SetShading(ambient, shadeStrength);
                renderer.SetProfileExponent(profileExponent);
                drewAny |= renderer.Draw(args.Display);
            }

            if (!drewAny)
                base.DrawViewportMeshes(args);
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            foreach (global::WASPer_3DP.WasperPrintPathSegmentRenderer renderer in _renderers)
                renderer.Dispose();
            _renderers.Clear();
            base.RemovedFromDocument(document);
        }

        private void ClearRenderers()
        {
            foreach (global::WASPer_3DP.WasperPrintPathSegmentRenderer renderer in _renderers)
                renderer.Clear();
        }

        private void EnsureRendererCount(int count)
        {
            while (_renderers.Count < count)
                _renderers.Add(new global::WASPer_3DP.WasperPrintPathSegmentRenderer());

            while (_renderers.Count > count)
            {
                int index = _renderers.Count - 1;
                _renderers[index].Dispose();
                _renderers.RemoveAt(index);
            }
        }

        private static double ResolveRepresentative(
            DataTree<double> tree,
            GH_Path path,
            double fallback,
            double tolerance)
        {
            if (tree != null && tree.PathExists(path))
            {
                IList<double> branch = tree.Branch(path);
                if (branch != null)
                {
                    for (int i = 0; i < branch.Count; i++)
                    {
                        double value = branch[i];
                        if (double.IsFinite(value) && value > tolerance)
                            return value;
                    }
                }
            }
            return fallback;
        }

        private static double[] ResolveValues(
            DataTree<double> tree,
            GH_Path path,
            int count,
            double fallback,
            double tolerance)
        {
            var result = new double[count];
            IList<double> branch = tree != null && tree.PathExists(path)
                ? tree.Branch(path)
                : null;

            for (int i = 0; i < count; i++)
            {
                double value = fallback;
                if (branch != null && branch.Count > 0)
                    value = branch[Math.Min(i, branch.Count - 1)];
                if ((!double.IsFinite(value) || value <= tolerance) && double.IsFinite(fallback))
                    value = fallback;
                result[i] = value;
            }
            return result;
        }

        private static Vector3d[] ResolveHeightDirections(
            DataTree<Plane> planes,
            GH_Path path,
            int count)
        {
            var result = new Vector3d[count];
            IList<Plane> branch = planes != null && planes.PathExists(path)
                ? planes.Branch(path)
                : null;

            for (int i = 0; i < count; i++)
            {
                Vector3d direction = -Vector3d.ZAxis;
                if (branch != null && branch.Count > 0)
                {
                    Plane plane = branch[Math.Min(i, branch.Count - 1)];
                    if (plane.IsValid)
                        direction = -plane.ZAxis;
                }
                result[i] = direction;
            }
            return result;
        }

        private static bool IsClosed(
            global::WASPer_3DP.WasperPrintPath path,
            GH_Path branchPath,
            IList<Point3d> points,
            IList<double> widths,
            double tolerance)
        {
            if (path.SourceCurves != null && path.SourceCurves.PathExists(branchPath))
            {
                IList<Curve> curves = path.SourceCurves.Branch(branchPath);
                if (curves != null && curves.Count > 0 && curves[0] != null)
                    return curves[0].IsClosed;
            }

            if (points.Count <= 3)
                return false;
            double width = widths != null && widths.Count > 0 ? widths[0] : tolerance * 10.0;
            return points[0].DistanceTo(points[points.Count - 1]) <=
                   Math.Max(tolerance * 10.0, width * 0.8);
        }

        private static double[] TrimLast(double[] values)
        {
            var result = new double[Math.Max(0, values.Length - 1)];
            Array.Copy(values, result, result.Length);
            return result;
        }

        private static Vector3d[] TrimLast(Vector3d[] values)
        {
            var result = new Vector3d[Math.Max(0, values.Length - 1)];
            Array.Copy(values, result, result.Length);
            return result;
        }

        private static int RoleColorIndex(global::WASPer_3DP.WasperPathRole role)
        {
            switch (role)
            {
                case global::WASPer_3DP.WasperPathRole.Shell: return 0;
                case global::WASPer_3DP.WasperPathRole.Infill: return 1;
                case global::WASPer_3DP.WasperPathRole.Partition: return 2;
                case global::WASPer_3DP.WasperPathRole.Support: return 3;
                case global::WASPer_3DP.WasperPathRole.Transition: return 4;
                default: return 5;
            }
        }

        private static global::WASPer_3DP.WasperPathRole RoleFromColorIndex(int index)
        {
            switch (index)
            {
                case 0: return global::WASPer_3DP.WasperPathRole.Shell;
                case 1: return global::WASPer_3DP.WasperPathRole.Infill;
                case 2: return global::WASPer_3DP.WasperPathRole.Partition;
                case 3: return global::WASPer_3DP.WasperPathRole.Support;
                case 4: return global::WASPer_3DP.WasperPathRole.Transition;
                default: return global::WASPer_3DP.WasperPathRole.Undefined;
            }
        }
    }
}
