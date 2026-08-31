#region Component Description
/*
    Component Name:
        wsp_Sl06_Orient Printing Paths

    Nickname:
        OrientPaths

    Category / Subcategory:
        WASPer_3DP / 3.0_Slicing

    Description:
        Re-orients existing printing paths from their source layer planes onto a
        stable moving frame sequence. It is intended as the bridge between planar/path-based
        infill generation and curve-referenced printing strategies.

        The component does not create, remove, or reslice layers. The number of
        target frames comes from the detected source layers. Each input path is
        transformed from its source plane to the target frame assigned to the
        path's layer.

        Target frames can be generated along a reference curve, using:
          0 = source spacing ratios
          1 = uniform distribution by layer count
          2 = curvature-weighted distribution

        If target_planes are supplied, they override the reference-curve frame
        generation. This is useful for custom frame workflows and debugging.

    PATH ROLE METADATA
        Each re-oriented output curve explicitly carries over the shared
        WASPer.PathRole user-string tag (WasperPathRole / WasperPathRoleMetadata,
        Components\Shared\Geometry\WASPer_PathRole.cs) from its source curve, so
        role-aware downstream components (Sl07 Printing Path Visualizer, Gc11
        Visualize Path v2) keep auto-detecting shell/infill/partition after
        re-orientation.
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
#endregion

namespace WASPer_3DP.Components._3_0_Slicing
{
    public sealed class wsp_Sl06_Orient_Printing_Paths : GH_Component
    {
        private const string ComponentName = "wsp_Sl06_Orient Printing Paths";
        private const string ComponentNickname = "OrientPaths";
        private const string ComponentCategory = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string ComponentSubCategory = "3.0_Slicing";

        private readonly string _versionTag;

        public wsp_Sl06_Orient_Printing_Paths()
            : base(
                ComponentName,
                ComponentNickname,
                "Maps existing printing paths from source layer planes onto target frames along a reference curve or an explicit target-plane set.",
                ComponentCategory,
                ComponentSubCategory)
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("6F2E70CB-A7E4-4A89-9A74-7C8CB81CF4D2");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var stream = asm.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Sl05_Orient Printing Paths.png"))
                    {
                        return stream != null ? new Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "paths",
                "paths",
                "Printing path curves to re-orient. TREE access.\n" +
                "The first index of each branch is treated as the layer id, so both {layer} and {layer;domain;path} topologies are supported.\n" +
                "The input topology is preserved unless trim_paths is true.",
                GH_ParamAccess.tree);

            pManager.AddPlaneParameter(
                "source_planes",
                "src_pl",
                "Source layer planes for the incoming paths. TREE access.\n" +
                "Use the la_planes output from SlicerPlus or the path-based infill components.\n" +
                "One plane is expected per source layer. Branches can be {layer}, original layer paths, or a simple plane list.\n" +
                "If a layer plane is missing, the component attempts to infer a plane from the path curves in that layer.",
                GH_ParamAccess.tree);

            pManager.AddCurveParameter(
                "ref_curve",
                "ref",
                "Reference curve used to generate the target frame sequence.\n" +
                "The number of target frames is inherited from the source layers; this component does not create new layers.\n" +
                "Frame Z axes follow the curve tangent; local XY axes are transported along the curve for stable plane-to-plane mapping.\n" +
                "Optional when target_planes are supplied.",
                GH_ParamAccess.item);

            pManager.AddPlaneParameter(
                "target_planes",
                "tar_pl",
                "Optional explicit target planes. TREE access.\n" +
                "When supplied, these planes override ref_curve frame generation.\n" +
                "Use this for custom robot frames, manually edited planes, or debugging the plane-to-plane transform step.",
                GH_ParamAccess.tree);

            pManager.AddIntegerParameter(
                "distribution",
                "dist",
                "How target frames are distributed along ref_curve when target_planes are not supplied:\n" +
                "0 = source spacing ratios: preserve relative spacing measured between source plane origins.\n" +
                "1 = uniform: place all source layers evenly along the reference curve by arc length.\n" +
                "2 = curvature weighted: place more frames where the reference curve bends more.",
                GH_ParamAccess.item,
                0);

            pManager.AddNumberParameter(
                "curv_weight",
                "cW",
                "Curvature weighting strength used only when distribution = 2.\n" +
                "0 behaves like uniform distribution. Higher values attract more layer frames toward high-curvature regions.\n" +
                "A value around 1-3 is usually a good starting range.",
                GH_ParamAccess.item,
                1.0);

            pManager.AddVectorParameter(
                "up_vector",
                "up",
                "Reference up vector used to initialize and recover the target frame roll around the reference curve tangent.\n" +
                "The first generated frame projects this vector onto the plane perpendicular to the tangent as local Y; subsequent frames transport that XY orientation along the curve.\n" +
                "If the vector is parallel to the tangent, a safe perpendicular fallback is used.",
                GH_ParamAccess.item,
                Vector3d.ZAxis);

            pManager.AddNumberParameter(
                "twist",
                "twist",
                "Extra rotation around each target frame Z axis, in degrees.\n" +
                "Accepts a list: one value is global; multiple values cycle by source layer order.\n" +
                "Useful for tuning nozzle/road orientation after the tangent frame is generated.",
                GH_ParamAccess.list,
                0.0);

            pManager.AddBooleanParameter(
                "trim_paths",
                "trim",
                "Output tree mode.\n" +
                "False preserves each input branch path exactly.\n" +
                "True collapses output paths to {layer}, which is convenient for downstream printing-path components that expect one branch per layer.",
                GH_ParamAccess.item,
                false);

            pManager[1].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
            for (int i = 4; i < pManager.ParamCount; i++)
                pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "paths_oriented",
                "paths",
                "Re-oriented printing paths. Curves are duplicated and transformed from each layer's source plane to its target frame.\n" +
                "The tree is preserved from the input unless trim_paths is true, in which case branches collapse to {layer}.",
                GH_ParamAccess.tree);

            pManager.AddPlaneParameter(
                "target_planes",
                "tar_pl",
                "Target frame assigned to each source layer. One branch per layer.\n" +
                "When generated from ref_curve, each plane origin lies on the reference curve, Z follows the curve tangent, and XY is rotation-minimizing along the curve.",
                GH_ParamAccess.tree);

            pManager.AddPlaneParameter(
                "source_planes",
                "src_pl",
                "Resolved source plane used for each source layer. One branch per layer.\n" +
                "This may come from the source_planes input or from an inferred fallback plane when an input plane is missing.",
                GH_ParamAccess.tree);

            pManager.AddTransformParameter(
                "transforms",
                "xform",
                "Plane-to-plane transform assigned to each source layer, from resolved source plane to target plane.\n" +
                "One transform is emitted per layer.",
                GH_ParamAccess.tree);

            pManager.AddNumberParameter(
                "parameters",
                "t",
                "Normalized reference-curve parameters used for each target frame, in source-layer order.\n" +
                "Values are 0..1 along the reference curve domain after distribution has been applied.",
                GH_ParamAccess.list);

            pManager.AddTextParameter(
                "info",
                "info",
                "Detailed status report: layer count, frame source, distribution mode, skipped paths, and fallback plane usage.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            GH_Structure<GH_Curve> pathTree = null;
            GH_Structure<GH_Plane> sourcePlaneTree = null;
            GH_Structure<GH_Plane> targetPlaneTree = null;
            Curve refCurve = null;
            int distribution = 0;
            double curvWeight = 1.0;
            Vector3d up = Vector3d.ZAxis;
            var twistDeg = new List<double>();
            bool trimPaths = false;

            if (!DA.GetDataTree(0, out pathTree) || pathTree == null || pathTree.PathCount == 0)
            {
                DA.SetDataTree(0, new GH_Structure<GH_Curve>());
                DA.SetDataTree(1, new GH_Structure<GH_Plane>());
                DA.SetDataTree(2, new GH_Structure<GH_Plane>());
                DA.SetDataTree(3, new GH_Structure<GH_Transform>());
                DA.SetDataList(4, new List<double>());
                DA.SetData(5, "Provide paths as a curve tree.");
                return;
            }

            DA.GetDataTree(1, out sourcePlaneTree);
            DA.GetData(2, ref refCurve);
            DA.GetDataTree(3, out targetPlaneTree);
            DA.GetData(4, ref distribution);
            DA.GetData(5, ref curvWeight);
            DA.GetData(6, ref up);
            DA.GetDataList(7, twistDeg);
            DA.GetData(8, ref trimPaths);

            distribution = WasperFrameSequence.ClampDistribution(distribution);
            curvWeight = double.IsNaN(curvWeight) ? 0.0 : Math.Max(0.0, curvWeight);
            up = WasperFrameSequence.SanitizeUpVector(up);
            if (twistDeg.Count == 0) twistDeg.Add(0.0);

            double tol = RhinoDoc.ActiveDoc != null ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 1e-6;
            var info = new StringBuilder();
            var warnings = new List<string>();

            var layerIds = ExtractLayerIds(pathTree);
            if (layerIds.Count == 0)
            {
                DA.SetData(5, "No valid path branches were found.");
                return;
            }

            var sourceByLayer = ResolveSourcePlanes(layerIds, pathTree, sourcePlaneTree, tol, out int inferredCount);
            List<Plane> targetPlanes;
            List<double> normalizedParameters;
            string frameSource;

            if (TryResolveExplicitTargetPlanes(layerIds, targetPlaneTree, out targetPlanes))
            {
                normalizedParameters = WasperFrameSequence.BuildIndexParameters(layerIds.Count);
                frameSource = "target_planes";
            }
            else
            {
                if (refCurve == null || !refCurve.IsValid)
                {
                    DA.SetData(5, "Provide either ref_curve or target_planes.");
                    return;
                }

                var sourceOrigins = new List<Point3d>(layerIds.Count);
                for (int i = 0; i < layerIds.Count; i++)
                    sourceOrigins.Add(sourceByLayer[layerIds[i]].Origin);

                normalizedParameters = WasperFrameSequence.BuildDistributionParameters(
                    sourceOrigins, refCurve, distribution, curvWeight, tol);
                targetPlanes = WasperFrameSequence.BuildFramesOnCurve(refCurve, normalizedParameters, up, tol);
                frameSource = "ref_curve";
            }

            WasperFrameSequence.ApplyTwist(targetPlanes, twistDeg);

            var transformByLayer = new Dictionary<int, Transform>();
            var targetByLayer = new Dictionary<int, Plane>();
            for (int i = 0; i < layerIds.Count; i++)
            {
                int layer = layerIds[i];
                Plane src = sourceByLayer[layer];
                Plane dst = targetPlanes[i];
                targetByLayer[layer] = dst;
                transformByLayer[layer] = Transform.PlaneToPlane(src, dst);
            }

            var outPaths = new GH_Structure<GH_Curve>();
            int transformed = 0;
            int skipped = 0;

            for (int bi = 0; bi < pathTree.PathCount; bi++)
            {
                GH_Path inPath = pathTree.Paths[bi];
                int layer = WasperLayerPlaneTools.LayerIdFromPath(inPath, bi);
                if (!transformByLayer.TryGetValue(layer, out Transform xform))
                {
                    skipped += pathTree.get_Branch(inPath)?.Count ?? 0;
                    continue;
                }

                GH_Path outPath = trimPaths ? new GH_Path(layer) : new GH_Path(inPath.Indices);
                var branch = pathTree.get_Branch(inPath);
                if (branch == null) continue;

                foreach (object obj in branch)
                {
                    var ghCurve = obj as GH_Curve;
                    Curve curve = ghCurve?.Value;
                    if (curve == null || !curve.IsValid)
                    {
                        skipped++;
                        continue;
                    }

                    Curve dup = curve.DuplicateCurve();
                    if (dup == null || !dup.Transform(xform))
                    {
                        skipped++;
                        continue;
                    }

                    // Re-orienting a path does not change what it semantically is, so the
                    // shared WASPer.PathRole tag (if any) is carried over explicitly rather
                    // than relying on DuplicateCurve()/Transform() to preserve user strings.
                    global::WASPer_3DP.WasperPathRoleMetadata.Copy(curve, dup);

                    outPaths.Append(new GH_Curve(dup), outPath);
                    transformed++;
                }
            }

            var outTargetPlanes = new GH_Structure<GH_Plane>();
            var outSourcePlanes = new GH_Structure<GH_Plane>();
            var outTransforms = new GH_Structure<GH_Transform>();
            for (int i = 0; i < layerIds.Count; i++)
            {
                int layer = layerIds[i];
                GH_Path layerPath = new GH_Path(layer);
                outSourcePlanes.Append(new GH_Plane(sourceByLayer[layer]), layerPath);
                outTargetPlanes.Append(new GH_Plane(targetByLayer[layer]), layerPath);
                outTransforms.Append(new GH_Transform(transformByLayer[layer]), layerPath);
            }

            info.AppendLine("wsp_Sl06_Orient Printing Paths");
            info.AppendLine("--------------------------------");
            info.AppendLine($"layers              : {layerIds.Count}");
            info.AppendLine($"paths_transformed   : {transformed}");
            info.AppendLine($"paths_skipped       : {skipped}");
            info.AppendLine($"frame_source        : {frameSource}");
            info.AppendLine($"frame_orientation   : rotation-minimizing XY transport");
            info.AppendLine($"distribution        : {WasperFrameSequence.DistributionName(distribution)}");
            info.AppendLine($"curv_weight         : {curvWeight:0.###}");
            info.AppendLine($"trim_paths          : {trimPaths}");
            info.AppendLine($"source_planes_input : {(sourcePlaneTree != null && sourcePlaneTree.PathCount > 0)}");
            info.AppendLine($"source_inferred     : {inferredCount}");
            if (warnings.Count > 0)
            {
                info.AppendLine();
                info.AppendLine("Warnings:");
                foreach (string w in warnings) info.AppendLine("  - " + w);
            }

            DA.SetDataTree(0, outPaths);
            DA.SetDataTree(1, outTargetPlanes);
            DA.SetDataTree(2, outSourcePlanes);
            DA.SetDataTree(3, outTransforms);
            DA.SetDataList(4, normalizedParameters);
            DA.SetData(5, info.ToString());
        }

        private static List<int> ExtractLayerIds(GH_Structure<GH_Curve> tree)
        {
            var set = new SortedSet<int>();
            for (int i = 0; i < tree.PathCount; i++)
                set.Add(WasperLayerPlaneTools.LayerIdFromPath(tree.Paths[i], i));
            return set.ToList();
        }

        private static Dictionary<int, Plane> ResolveSourcePlanes(
            IList<int> layerIds,
            GH_Structure<GH_Curve> pathTree,
            GH_Structure<GH_Plane> planeTree,
            double tol,
            out int inferredCount)
        {
            var byLayer = new Dictionary<int, Plane>();
            inferredCount = 0;

            if (planeTree != null && planeTree.PathCount > 0)
            {
                for (int bi = 0; bi < planeTree.PathCount; bi++)
                {
                    var branch = planeTree.get_Branch(planeTree.Paths[bi]);
                    if (branch == null || branch.Count == 0) continue;
                    var ghPlane = branch[0] as GH_Plane;
                    if (ghPlane == null || !ghPlane.Value.IsValid) continue;

                    int layer = WasperLayerPlaneTools.LayerIdFromPath(planeTree.Paths[bi], bi);
                    if (!byLayer.ContainsKey(layer))
                        byLayer[layer] = ghPlane.Value;
                }

                for (int i = 0; i < layerIds.Count; i++)
                {
                    int layer = layerIds[i];
                    if (byLayer.ContainsKey(layer)) continue;
                    if (i < planeTree.PathCount)
                    {
                        var branch = planeTree.get_Branch(planeTree.Paths[i]);
                        var ghPlane = branch != null && branch.Count > 0 ? branch[0] as GH_Plane : null;
                        if (ghPlane != null && ghPlane.Value.IsValid)
                            byLayer[layer] = ghPlane.Value;
                    }
                }
            }

            for (int i = 0; i < layerIds.Count; i++)
            {
                int layer = layerIds[i];
                if (byLayer.ContainsKey(layer)) continue;
                byLayer[layer] = WasperLayerPlaneTools.EstimateLayerPlaneFromPathTreeLayer(pathTree, layer, tol);
                inferredCount++;
            }

            return byLayer;
        }

        private static bool TryResolveExplicitTargetPlanes(
            IList<int> layerIds,
            GH_Structure<GH_Plane> planeTree,
            out List<Plane> planes)
        {
            planes = new List<Plane>();
            if (planeTree == null || planeTree.PathCount == 0)
                return false;

            var byLayer = new Dictionary<int, Plane>();
            for (int bi = 0; bi < planeTree.PathCount; bi++)
            {
                var branch = planeTree.get_Branch(planeTree.Paths[bi]);
                if (branch == null || branch.Count == 0) continue;
                var ghPlane = branch[0] as GH_Plane;
                if (ghPlane == null || !ghPlane.Value.IsValid) continue;
                int layer = WasperLayerPlaneTools.LayerIdFromPath(planeTree.Paths[bi], bi);
                if (!byLayer.ContainsKey(layer))
                    byLayer[layer] = ghPlane.Value;
            }

            for (int i = 0; i < layerIds.Count; i++)
            {
                int layer = layerIds[i];
                if (byLayer.TryGetValue(layer, out Plane plane))
                {
                    planes.Add(plane);
                    continue;
                }

                if (i < planeTree.PathCount)
                {
                    var branch = planeTree.get_Branch(planeTree.Paths[i]);
                    var ghPlane = branch != null && branch.Count > 0 ? branch[0] as GH_Plane : null;
                    if (ghPlane != null && ghPlane.Value.IsValid)
                    {
                        planes.Add(ghPlane.Value);
                        continue;
                    }
                }

                return false;
            }

            return planes.Count == layerIds.Count;
        }
    }
}
