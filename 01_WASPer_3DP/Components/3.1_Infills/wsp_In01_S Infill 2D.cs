using System;
using System.Collections.Generic;
using System.Linq;
using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace WASPer_3DP.Components._3_1_Infills
{
    public class wsp_In01_S_Infill_2D : GH_Component
    {
        /// <summary>
        /// Constructor
        /// </summary>
        private readonly string _versionTag;

        public wsp_In01_S_Infill_2D()
          : base(
                "wsp_In01_S Infill 2D",  // Name
                "S infill",             // Nickname
                "Generates S-shaped infill from untagged closed planar boundaries, or replaces curves tagged WASPer.PathRole=Infill inside metadata-tagged layer trees while preserving Shell, Partition, Support, and untagged paths. layer_crvs keeps its DataTree topology; la_index targets logical layers. rotation, distance, and clearance are independent flattened broadcast/cycle lists.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,           // Category
                "3.1_Infills"             // Subcategory
            )
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v   = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        /// <summary>
        /// Provide a unique ID for this component
        /// </summary>
        public override Guid ComponentGuid
        {
            get { return new Guid("524e1dcd-4632-4d8f-9cc4-4d59f1455b8e"); }
        }

        /// <summary>
        /// Optional icon (24x24). Return null if you don’t have a custom icon.
        /// </summary>
        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = asm.GetManifestResourceStream("WASPer_3DP.Resources.Icons.11_S_Infill.png"))
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                }
                catch { return null; }
            }
        }

        /// <summary>
        /// Register input parameters
        /// </summary>
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "layer_crvs",
                "layer_crvs",
                "Curve DataTree. Untagged closed planar curves use legacy boundary mode. If any curve carries WASPer.PathRole metadata, each branch is treated as a complete layer: Shell curves define replacement boundaries, Infill curves are replaced, and Shell/Partition/Support/untagged curves pass through unchanged.",
                GH_ParamAccess.tree
            );

            pManager.AddNumberParameter(
                "rotation",
                "rotation",
                "Flattened rotation angles in degrees. One value broadcasts; multiple values cycle independently by selected layer (metadata mode) or source boundary (legacy mode).",
                GH_ParamAccess.list,
                0.0
            );
            pManager[1].DataMapping = GH_DataMapping.Flatten;

            pManager.AddNumberParameter(
                "distance",
                "distance",
                "Flattened S-line spacing values. One value broadcasts; multiple values cycle independently by selected layer (metadata mode) or source boundary (legacy mode). Values <= 0 use 2.0.",
                GH_ParamAccess.list,
                2.0
            );
            pManager[2].DataMapping = GH_DataMapping.Flatten;

            pManager.AddNumberParameter(
                "clearance",
                "clear",
                "Flattened boundary inset values in model units. One value broadcasts; multiple values cycle independently by selected layer (metadata mode) or source boundary (legacy mode). 0 keeps the reference boundary.",
                GH_ParamAccess.list,
                0.0
            );
            pManager[3].DataMapping = GH_DataMapping.Flatten;
            pManager[3].Optional = true;

            pManager.AddIntegerParameter(
                "layer_index",
                "la_index",
                "Optional flattened logical-layer indices. Empty or -1 alone targets all layers. Otherwise nonnegative values target branches by their final path index (for example {object;layer}). Settings lists map in la_index order and cycle independently.",
                GH_ParamAccess.list
            );
            pManager[4].DataMapping = GH_DataMapping.Flatten;
            pManager[4].Optional = true;
        }

        /// <summary>
        /// Register output parameters
        /// </summary>
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "full_path",
                "full_path",
                "Metadata mode: complete input layers with tagged Infill curves replaced and all other paths and WASPer.PathRole values preserved. Legacy boundary mode: mirrors infill_crvs, tagged as Infill. Original branch paths are preserved.",
                GH_ParamAccess.tree
            );
            pManager.AddCurveParameter(
                "infill_crvs",
                "infill_crvs",
                "New S-pattern infill curves only. Every generated curve carries WASPer.PathRole=Infill metadata.",
                GH_ParamAccess.tree
            );
        }

        /// <summary>
        /// Main solve logic
        /// </summary>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_Structure<GH_Curve> layerTree = null;
            List<double> rotation = new List<double>();
            List<double> distance = new List<double>();
            List<double> clearance = new List<double>();
            List<int> layerIndices = new List<int>();

            if (!DA.GetDataTree(0, out layerTree) || layerTree == null) return;
            if (!DA.GetDataList(1, rotation)) return;
            if (!DA.GetDataList(2, distance)) return;
            if (!DA.GetDataList(3, clearance)) return;
            DA.GetDataList(4, layerIndices);

            Message = _versionTag;

            if (rotation == null || rotation.Count == 0)
                rotation = new List<double> { 0.0 };
            if (distance == null || distance.Count == 0)
                distance = new List<double> { 2.0 };
            if (clearance == null || clearance.Count == 0)
                clearance = new List<double> { 0.0 };

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
                    rotation,
                    distance,
                    clearance,
                    GenerateSCurve,
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
                    rotation,
                    distance,
                    clearance,
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
                bool selected = selectedLayers == null || selectedLayers.Contains(layerIndex);
                if (!selected)
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
                    Curve infill = GenerateSCurve(
                        source,
                        WasperLayerInfillReplacement.Resolve(rotations, settingOrdinal, 0.0),
                        WasperLayerInfillReplacement.Resolve(distances, settingOrdinal, 2.0),
                        WasperLayerInfillReplacement.Resolve(clearances, settingOrdinal, 0.0),
                        legacyList ? $"curve {itemIndex}" : $"path {sourcePath}, item {itemIndex}");
                    if (infill == null)
                        continue;

                    WasperPathRoleMetadata.Set(infill, WasperPathRole.Infill);
                    GH_Path outputPath = legacyList ? new GH_Path(itemIndex) : sourcePath;
                    generated.Append(
                        new GH_Curve(WasperLayerInfillReplacement.DuplicateWithRole(infill)),
                        outputPath);
                    fullPath.Append(new GH_Curve(infill), outputPath);
                }
            }
        }

        private Curve GenerateSCurve(
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
            if (boundary == null || !boundary.TryGetPlane(out Plane basePlane))
                return null;

            Brep[] breps = Brep.CreatePlanarBreps(boundary, 0.001);
            if (breps == null || breps.Length == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{context}: unable to create a planar region.");
                return null;
            }

            BoundingBox baseBox = boundary.GetBoundingBox(basePlane);
            Point3d center = basePlane.PointAt(baseBox.Center.X, baseBox.Center.Y, baseBox.Center.Z);
            Plane referencePlane = new Plane(center, basePlane.XAxis, basePlane.YAxis);
            referencePlane.Rotate(RhinoMath.ToRadians(rotation), referencePlane.ZAxis);

            BoundingBox oriented = boundary.GetBoundingBox(referencePlane);
            Box box = new Box(
                referencePlane,
                new Interval(oriented.Min.X, oriented.Max.X),
                new Interval(oriented.Min.Y, oriented.Max.Y),
                new Interval(oriented.Min.Z, oriented.Max.Z));
            Line centerLine = new Line(box.PointAt(0.5, 0, 0.5), box.PointAt(0.5, 1, 0.5));
            if (centerLine.Length <= RhinoMath.ZeroTolerance)
                return null;

            double spacing = distance > RhinoMath.ZeroTolerance ? distance : 2.0;
            List<Line> lines = new List<Line>();
            for (double length = 0.0; length <= centerLine.Length; length += spacing)
            {
                Plane slice = new Plane(centerLine.PointAtLength(length), centerLine.Direction);
                if (!Intersection.BrepPlane(
                        breps[0],
                        slice,
                        0.001,
                        out Curve[] intersections,
                        out Point3d[] _))
                    continue;

                foreach (Curve intersection in intersections)
                    if (intersection != null && intersection.IsValid && intersection.IsLinear())
                        lines.Add(new Line(intersection.PointAtStart, intersection.PointAtEnd));
            }

            for (int i = 1; i < lines.Count; i++)
                if (lines[i - 1].Direction.IsParallelTo(lines[i].Direction, 0.001) == 1)
                    lines[i] = new Line(lines[i].To, lines[i].From);

            Polyline polyline = new Polyline();
            foreach (Line line in lines)
            {
                polyline.Add(line.From);
                polyline.Add(line.To);
            }

            if (!polyline.IsValid || polyline.Count < 2)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{context}: no valid S infill was produced.");
                return null;
            }

            return polyline.ToNurbsCurve();
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
