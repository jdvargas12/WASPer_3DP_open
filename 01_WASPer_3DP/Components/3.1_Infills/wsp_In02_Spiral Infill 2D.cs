using System;
using System.Collections.Generic;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP.Components._3_1_Infills
{
    public class wsp_In02_Spiral_Infill_2D : GH_Component
    {
        private readonly string _versionTag;

        public wsp_In02_Spiral_Infill_2D()
          : base(
                "wsp_In02_Spiral Infill 2D",
                "Spiral Infill",
                "Generates spiral infill from untagged closed planar boundaries, or replaces curves tagged WASPer.PathRole=Infill inside metadata-tagged layer trees while preserving Shell, Partition, Support, and untagged paths. layer_crvs keeps its DataTree topology; la_index targets logical layers. rotation, distance, and clearance are independent flattened broadcast/cycle lists.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "3.1_Infills")
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version != null
                ? $"v{version.Major}.{version.Minor}.{version.Build}"
                : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("b7e2f7bd-cf59-4f0d-8e80-2f87a7d8f2e1");

        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_In02_Spiral Infill 2D.png"))
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                }
                catch
                {
                    return null;
                }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddCurveParameter(
                "layer_crvs",
                "layer_crvs",
                "Curve DataTree. Untagged closed planar curves use legacy boundary mode. If any curve carries WASPer.PathRole metadata, each branch is treated as a complete layer: Shell curves define replacement boundaries, Infill curves are replaced, and Shell/Partition/Support/untagged curves pass through unchanged.",
                GH_ParamAccess.tree);

            p.AddNumberParameter(
                "rotation",
                "rotation",
                "Flattened spiral rotation angles in degrees. One value broadcasts; multiple values cycle independently by selected layer (metadata mode) or source boundary (legacy mode).",
                GH_ParamAccess.list,
                0.0);
            p[1].DataMapping = GH_DataMapping.Flatten;

            p.AddNumberParameter(
                "distance",
                "distance",
                "Flattened spacing values between spiral turns. One value broadcasts; multiple values cycle independently by selected layer (metadata mode) or source boundary (legacy mode). Values <= 0 use 2.0.",
                GH_ParamAccess.list,
                2.0);
            p[2].DataMapping = GH_DataMapping.Flatten;

            p.AddNumberParameter(
                "clearance",
                "clear",
                "Flattened boundary inset values in model units. One value broadcasts; multiple values cycle independently by selected layer (metadata mode) or source boundary (legacy mode). 0 keeps the reference boundary.",
                GH_ParamAccess.list,
                0.0);
            p[3].DataMapping = GH_DataMapping.Flatten;
            p[3].Optional = true;

            p.AddIntegerParameter(
                "layer_index",
                "la_index",
                "Optional flattened logical-layer indices. Empty or -1 alone targets all layers. Otherwise nonnegative values target branches by their final path index (for example {object;layer}). Settings lists map in la_index order and cycle independently.",
                GH_ParamAccess.list);
            p[4].DataMapping = GH_DataMapping.Flatten;
            p[4].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddCurveParameter(
                "full_path",
                "full_path",
                "Metadata mode: complete input layers with tagged Infill curves replaced and all other paths and WASPer.PathRole values preserved. Legacy boundary mode: mirrors spiral_infill, tagged as Infill. Original branch paths are preserved.",
                GH_ParamAccess.tree);
            p.AddCurveParameter(
                "spiral_infill",
                "spiral_infill",
                "New spiral infill curves only. Every generated curve carries WASPer.PathRole=Infill metadata.",
                GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_Structure<GH_Curve> layerTree = null;
            List<double> rotations = new List<double>();
            List<double> distances = new List<double>();
            List<double> clearances = new List<double>();
            List<int> layerIndices = new List<int>();

            if (!DA.GetDataTree(0, out layerTree) || layerTree == null)
                return;
            if (!DA.GetDataList(1, rotations) ||
                !DA.GetDataList(2, distances) ||
                !DA.GetDataList(3, clearances))
                return;
            DA.GetDataList(4, layerIndices);

            if (rotations.Count == 0)
                rotations.Add(0.0);
            if (distances.Count == 0)
                distances.Add(2.0);
            if (clearances.Count == 0)
                clearances.Add(0.0);

            if (!WasperLayerInfillReplacement.TryNormalizeLayerIndices(
                    this,
                    layerIndices,
                    out HashSet<int> selectedLayers,
                    out Dictionary<int, int> selectionOrder))
                return;

            GH_Structure<GH_Curve> generated;
            GH_Structure<GH_Curve> fullPath;
            if (WasperLayerInfillReplacement.HasRoleMetadata(layerTree))
            {
                WasperLayerInfillReplacement.ProcessMetadataTree(
                    this,
                    layerTree,
                    selectedLayers,
                    selectionOrder,
                    rotations,
                    distances,
                    clearances,
                    GenerateSpiral,
                    out generated,
                    out fullPath);
                Message = _versionTag + " | Layer replace";
            }
            else
            {
                ProcessLegacyTree(
                    layerTree,
                    selectedLayers,
                    selectionOrder,
                    rotations,
                    distances,
                    clearances,
                    out generated,
                    out fullPath);
                Message = _versionTag + " | Boundaries";
            }

            DA.SetDataTree(0, fullPath);
            DA.SetDataTree(1, generated);
        }

        private void ProcessLegacyTree(
            GH_Structure<GH_Curve> input,
            HashSet<int> selectedLayers,
            IDictionary<int, int> selectionOrder,
            IList<double> rotations,
            IList<double> distances,
            IList<double> clearances,
            out GH_Structure<GH_Curve> generated,
            out GH_Structure<GH_Curve> fullPath)
        {
            generated = new GH_Structure<GH_Curve>();
            fullPath = new GH_Structure<GH_Curve>();
            bool legacyList = input.PathCount == 1;
            int allLayerOrdinal = 0;
            int boundaryOrdinal = 0;
            Dictionary<int, int> allLayerOrder = new Dictionary<int, int>();

            for (int branchIndex = 0; branchIndex < input.PathCount; branchIndex++)
            {
                GH_Path sourcePath = input.Paths[branchIndex];
                int layerIndex = WasperLayerInfillReplacement.LogicalLayerIndex(sourcePath);
                if (selectedLayers != null && !selectedLayers.Contains(layerIndex))
                    continue;

                int layerOrdinal;
                if (selectedLayers == null)
                {
                    if (!allLayerOrder.TryGetValue(layerIndex, out layerOrdinal))
                    {
                        layerOrdinal = allLayerOrdinal++;
                        allLayerOrder[layerIndex] = layerOrdinal;
                    }
                }
                else
                {
                    layerOrdinal = selectionOrder[layerIndex];
                }

                IList<GH_Curve> branch = input.Branches[branchIndex];
                for (int itemIndex = 0; itemIndex < branch.Count; itemIndex++)
                {
                    Curve source = branch[itemIndex]?.Value;
                    if (source == null)
                        continue;

                    int settingOrdinal = legacyList ? boundaryOrdinal++ : layerOrdinal;
                    Curve spiral = GenerateSpiral(
                        source,
                        WasperLayerInfillReplacement.Resolve(rotations, settingOrdinal, 0.0),
                        WasperLayerInfillReplacement.Resolve(distances, settingOrdinal, 2.0),
                        WasperLayerInfillReplacement.Resolve(clearances, settingOrdinal, 0.0),
                        legacyList ? $"curve {itemIndex}" : $"path {sourcePath}, item {itemIndex}");
                    if (spiral == null)
                        continue;

                    WasperPathRoleMetadata.Set(spiral, WasperPathRole.Infill);
                    GH_Path outputPath = legacyList ? new GH_Path(itemIndex) : sourcePath;
                    generated.Append(
                        new GH_Curve(WasperLayerInfillReplacement.DuplicateWithRole(spiral)),
                        outputPath);
                    fullPath.Append(new GH_Curve(spiral), outputPath);
                }
            }
        }

        private Curve GenerateSpiral(
            Curve source,
            double rotation,
            double distance,
            double clearance,
            string context)
        {
            if (source == null || !source.IsClosed || !source.IsPlanar())
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{context}: boundary is not closed and planar; ignored.");
                return null;
            }

            Curve boundary = Inset(source, clearance, context);
            if (boundary == null || !boundary.TryGetPlane(out Plane plane))
                return null;

            BoundingBox box = boundary.GetBoundingBox(plane);
            double xRadius = (box.Max.X - box.Min.X) * 0.5;
            double yRadius = (box.Max.Y - box.Min.Y) * 0.5;
            if (xRadius <= RhinoMath.ZeroTolerance || yRadius <= RhinoMath.ZeroTolerance)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{context}: boundary has zero area after clearance.");
                return null;
            }

            Point3d center = plane.PointAt(box.Center.X, box.Center.Y, box.Center.Z);
            double spacing = distance > RhinoMath.ZeroTolerance ? distance : 2.0;
            double phase = RhinoMath.ToRadians(rotation);
            double growth = spacing / (2.0 * Math.PI);
            List<Point3d> points = new List<Point3d>();

            for (double theta = 0.0; theta <= 200.0 * Math.PI; theta += 0.1)
            {
                double radius = 0.1 + growth * theta;
                double t = theta + phase;
                double x = radius * Math.Cos(t) * (xRadius > yRadius ? 1.0 : xRadius / yRadius);
                double y = radius * Math.Sin(t) * (yRadius > xRadius ? 1.0 : yRadius / xRadius);
                Point3d point = center + plane.XAxis * x + plane.YAxis * y;
                PointContainment containment = boundary.Contains(point, plane, 0.01);
                if (containment != PointContainment.Inside &&
                    containment != PointContainment.Coincident)
                    break;
                points.Add(point);
            }

            if (points.Count < 2)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{context}: no valid spiral infill was produced.");
                return null;
            }

            return new Polyline(points).ToNurbsCurve();
        }

        private Curve Inset(Curve source, double clearance, string context)
        {
            if (clearance <= RhinoMath.ZeroTolerance)
                return source.DuplicateCurve();
            if (!source.TryGetPlane(out Plane plane))
                return null;

            List<Curve> offsets = new List<Curve>();
            Curve[] positive = source.Offset(
                plane,
                clearance,
                0.001,
                CurveOffsetCornerStyle.Sharp);
            Curve[] negative = source.Offset(
                plane,
                -clearance,
                0.001,
                CurveOffsetCornerStyle.Sharp);
            if (positive != null) offsets.AddRange(positive);
            if (negative != null) offsets.AddRange(negative);

            Curve best = null;
            double bestArea = double.MaxValue;
            foreach (Curve candidate in offsets)
            {
                if (candidate == null || !candidate.IsClosed || !candidate.IsPlanar())
                    continue;
                AreaMassProperties amp = AreaMassProperties.Compute(candidate);
                double area = amp == null ? double.MaxValue : Math.Abs(amp.Area);
                if (area < bestArea)
                {
                    best = candidate;
                    bestArea = area;
                }
            }

            if (best == null)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{context}: clearance could not create a valid inward inset.");
            return best;
        }
    }
}
