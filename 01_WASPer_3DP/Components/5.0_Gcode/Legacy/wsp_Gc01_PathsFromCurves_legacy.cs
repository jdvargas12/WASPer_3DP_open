// -----------------------------------------------------------------------------
//  wsp_Gc01_Path from Curves
//  -----------------------------------------------------------------------------
//  - Uses input DataTree<Curve> structure to define logical layers.
//  - Optional DataTree<Plane>: one reference plane per layer (for diagnostics
//    and layer-height direction).
//  - SAFE: Subdivide each curve by arclength (no geometry edits).
//  - Flow modes 1/2/3 as in v3, but Mode 1 only supports global or per-layer
//    multipliers (no per-curve mapping).
//  - Computes per-point layer_height:
//        - Layer 0: distance to the base reference plane (World XY by default).
//        - Planar/tilted layers use the fitted-plane correspondence method.
//        - Genuinely non-planar layers use locally registered cross-layer
//          vectors with their path-tangent component removed.
// -----------------------------------------------------------------------------

#region Usings
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
#endregion

namespace WASPer_3DP.Components._5_0_Gcode
{
    public class wsp_Gc01_PathsFromCurves : GH_Component
    {
        // cached version tag from the WASPer_3DP assembly
        private readonly string _versionTag;

        public wsp_Gc01_PathsFromCurves()
          : base(
                "wsp_Gc01_WASPer Path from Curves",
                "PathCrvs",
                "Constructs a point-based WASPer printing path from input curves.\r\n" +
                "- Uses the unsimplified curve tree to define logical layers and stable\r\n" +
                "  source-curve correspondence between consecutive layers.\r\n" +
                "- Optional: one reference plane per layer, used for diagnostics and as the\n" +
                "  direction for layer-height estimation.\r\n" +
                "- Optional: one base ref_plane, used as the first-layer height datum and\n" +
                "  bed/reference plane instead of always using World XY.\r\n" +
                "- Subdivides curves by arclength, assigns flow per point (modes 1/2/3), and\n" +
                "  estimates local layer height from the corresponding source curve in the\n" +
                "  previous layer, with closest-geometry fallback. Auto mode preserves fitted-\r\n" +
                "  plane behavior for planar/tilted layers and switches to locally registered\r\n" +
                "  cross-layer vectors when a layer is genuinely non-planar.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "5.0_Gcode")
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = (v != null)
                ? $"v{v.Major}.{v.Minor}.{v.Build}"
                : "v1.0.x";

            this.Message = $"{_versionTag} - PathCrvs";
        }

        // GUID preserved for Grasshopper document compatibility.
        public override Guid ComponentGuid => new Guid("AF0A6B9F-2604-4EE3-9088-78C96F997B41");

        public override GH_Exposure Exposure => GH_Exposure.hidden;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    // Reuse existing icon or change to a dedicated one if you add it:
                    using (var s = asm.GetManifestResourceStream("WASPer_3DP.Resources.Icons.02_GenGcode.png"))
                        return s != null ? new System.Drawing.Bitmap(s) : null;
                }
                catch { }
                return null;
            }
        }

        #region Register IO
        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            // 0 - printing_path (tree)
            p.AddCurveParameter(
                "printing_path",
                "p_path",
                "Data tree of curves representing the printing path.\r\n" +
                "The first varying index after any common grafted prefix defines the\r\n" +
                "logical layer. For example, both {layer} and {0;layer} are supported.\r\n" +
                "All curves whose paths share that layer index belong to the same\n" +
                "non-planar layer (printing pass). Remaining path indices plus item order\r\n" +
                "identify corresponding curves between layers; keep them stable for the\r\n" +
                "most reliable layer-height estimate.",
                GH_ParamAccess.tree);

            // 1 - layer_planes (tree, optional)
            p.AddPlaneParameter(
                "layer_planes",
                "la_planes",
                "Optional data tree of reference planes, one per logical layer.\r\n" +
                "Its path structure should match p_path, including any grafted prefix.\r\n" +
                "The first plane in each matching logical-layer branch is used.\r\n" +
                "If present, its normal is used as the direction for layer-height\n" +
                "estimation. If absent or invalid, a fitted layer plane is tried first,\r\n" +
                "then layer-centroid displacement, and finally global Z.",
                GH_ParamAccess.tree);
            p[1].Optional = true;

            // 2 - flow_mode
            p.AddIntegerParameter(
                "flow_mode",
                "f_mode",
                "Flow assignment mode:\r\n" +
                "1 = m1_flow multipliers (global or per-layer).\r\n" +
                "2 = flow profile along each curve (0 to 1), sampled from m2_m3_flow_range.\r\n" +
                "3 = flow profile along a reference line (projection), sampled from\n" +
                "    m2_m3_flow_range.",
                GH_ParamAccess.item,
                1);

            // 3 - m1_flux
            p.AddNumberParameter(
                "m1_flow",
                "m1_fl",
                "Mode 1 flow multipliers.\r\n" +
                "Allowed sizes:\r\n" +
                "- 1 value -> global multiplier applied to all layers/curves.\r\n" +
                "- #layers -> one multiplier per detected logical layer.\r\n" +
                "Any other length will raise an error.\r\n" +
                "Ignored for flow_mode 2 and 3.",
                GH_ParamAccess.list,
                1.0); 
            p[3].DataMapping = GH_DataMapping.Flatten;

            // 4 - m2_m3_flux_range
            p.AddNumberParameter(
                "m2_m3_flow_range",
                "m2_m3_fl_range",
                "Flow profile values for modes 2 and 3.\r\n" +
                "Mode 2: sampled along each curve from its start (t=0) to end (t=1).\r\n" +
                "Mode 3: sampled along the projection of each point onto the reference\n" +
                "        reference Line or Curve (flow_crv), sampled from its start to end.\r\n" +
                "If a single value is provided, it acts as a constant profile.",
                GH_ParamAccess.list,
                1.0); 
            p[4].DataMapping = GH_DataMapping.Flatten;

            // 5 - flowx_line_vect (line or curve)
            p.AddGenericParameter(
                "flow_crv",
                "flow_crv",
                "Reference Line or Curve used only in flow_mode 3.\r\n" +
                "Lines and NURBS/other curves are accepted. Each point is projected onto the reference geometry,\r\n" +
                "and the flow profile is sampled by normalized arc length along it.\r\n" +
                "If reverse_vect is true, the reference direction is reversed internally.",
                GH_ParamAccess.item);
            p[5].Optional = true;

            // 6 - reverse_vect
            p.AddBooleanParameter(
                "reverse_crv",
                "rev_crv",
                "When true (flow_mode 3), the flow_crv is reversed internally,\r\n" +
                "so that the reference Line or Curve direction is reversed before computing projections.",
                GH_ParamAccess.item,
                false);

            // 7 - segment_len
            p.AddNumberParameter(
                "segment_len",
                "seg_len",
                "Target subdivision length in model units.\r\n" +
                "Each curve is subdivided by arclength into segments of approximately\n" +
                "this length (SAFE: no geometry edits).",
                GH_ParamAccess.item,
                1.0);

            // 8 - ref_plane (base/reference plane, optional)
            p.AddPlaneParameter(
                "ref_plane",
                "ref_plane",
                "Optional base/reference plane for the print. This replaces the implicit World XY/Z=0 datum used for first-layer height and bed/reference-plane safety checks.\r\n" +
                "Use it to lift or tilt the print reference without moving the input curves. If omitted, World XY is used.",
                GH_ParamAccess.item,
                Plane.WorldXY);
            p[8].Optional = true;

            // 9 - layer_w (nominal/base bead width, optional)
            p.AddNumberParameter(
                "layer_w",
                "layer_w",
                "Optional nominal/base bead width before flow adjustment, in model units. Accepts a single value, one value per branch, or a tree matching the generated printing points. If omitted, defaults to layer_h * 2.5. Gc01 stores this as wsp_path.LayerW, estimates LayerWf by scaling the bead cross-sectional area with local flow and recovering the equivalent deposited width from layer_h, and stores per-segment PrintVol.",
                GH_ParamAccess.tree);
            p[9].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            // 0 - printing_points
            p.AddPointParameter(
                "printing_points",
                "p_points",
                "Subdivided printing points organised as a DataTree.\r\n" +
                "Path structure: {layer; curve}.\r\n" +
                "- 'layer' is derived from the first varying path index after any common\r\n" +
                "  grafted prefix in p_path, ordered ascending.\r\n" +
                "- 'curve' is the index of the curve within that layer.",
                GH_ParamAccess.tree);

            // 1 - point_planes (local frame per point)
            p.AddPlaneParameter(
                "point_planes",
                "pt_planes",
                "Local frame per printing point. Plane Z is the stacking/height direction and plane Y follows the path tangent.\r\n" +
                "Tree structure matches p_points ({layer; curve}).\r\n" +
                "This can be fed into the Visualize Gcode Path component for non-planar bead orientation.",
                GH_ParamAccess.tree);

            // 3 - flows
            p.AddNumberParameter(
                "flows",
                "flows",
                "Flow multiplier value for each printing point.\r\n" +
                "Tree structure matches p_points ({layer; curve}).\r\n" +
                "By convention, the first point of each curve has flow = 0.0 and is\n" +
                "ignored when computing segment flow; each segment uses the flow of its\n" +
                "target point.",
                GH_ParamAccess.tree);

            // 4 - layer_height
            p.AddNumberParameter(
                "layer_height",
                "layer_h",
                "Estimated local layer height per point.\r\n" +
                "Tree structure matches p_points ({layer; curve}).\r\n" +
                "Layer 0 (first printing layer): perpendicular distance from each point to ref_plane\n" +
                "(World XY if ref_plane is omitted).\r\n" +
                "Layers L > 0 use automatic planar/non-planar evaluation. Planar or tilted\r\n" +
                "layers retain fitted-plane correspondence. Genuinely non-planar layers use\r\n" +
                "locally registered cross-layer vectors with their path-tangent component\r\n" +
                "removed. Missing or implausible matches fall back to the nearest previous-\r\n" +
                "layer polyline. Directions are lightly smoothed; heights are not globally\r\n" +
                "flattened. Inspect runtime correspondence diagnostics.",
                GH_ParamAccess.tree);

            // 5 - wasper_path (packed print-path object)
            p.AddGenericParameter(
                "wasper_path",
                "wsp_path",
                "WASPer Print Path object packing p_points, pt_planes, flows, layer_h,\r\n" +
                "nominal layer_w, flow-adjusted layer_wf, and per-segment print_vol\r\n" +
                "into a single wire. Optional shortcut for downstream Gcode components\r\n" +
                "(e.g. the Marlin G-code generator). The four tree outputs above remain\r\n" +
                "unchanged; inputs wired explicitly downstream always override the fields\r\n" +
                "of this object.",
                GH_ParamAccess.item);
        }
        #endregion

        #region SolveInstance
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // Inputs
            GH_Structure<GH_Curve> pTree;
            GH_Structure<GH_Plane> planeTree;
            int flow_mode = 1;
            var m1_flux = new List<double>();
            var flux_range = new List<double>();
            IGH_Goo flux_line_vect = null;
            bool reverse_vect = false;
            double segment_len = 1.0;
            Plane refPlane = Plane.WorldXY;
            GH_Structure<GH_Number> layerWInput = null;
            bool hasLayerWInput = false;

            if (!DA.GetDataTree(0, out pTree)) return;
            DA.GetDataTree(1, out planeTree); // optional; may be null/empty
            // Preserve original paths. The first varying index after a common
            // grafted prefix is the logical layer; the remaining suffix and
            // branch item order identify a source curve.

            if (!DA.GetData(2, ref flow_mode)) return;
            if (!DA.GetDataList(3, m1_flux)) return;
            if (!DA.GetDataList(4, flux_range)) return;
            bool hasFluxLineVect = DA.GetData(5, ref flux_line_vect); // optional Line or Curve
            if (!DA.GetData(6, ref reverse_vect)) return;
            if (!DA.GetData(7, ref segment_len)) return;
            bool hasRefPlane = DA.GetData(8, ref refPlane) && refPlane.IsValid;
            hasLayerWInput = DA.GetDataTree(9, out layerWInput) && HasNumberData(layerWInput);
            if (!hasRefPlane)
            {
                if (Params.Input[8].SourceCount > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "ref_plane was supplied but is invalid. Falling back to World XY.");
                refPlane = Plane.WorldXY;
            }

            this.Message = $"{_versionTag}\nPathCrvs | mode {flow_mode}";

            // Basic validation
            if (pTree == null || pTree.PathCount == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No curves provided in p_path.");
                return;
            }

            if (segment_len <= 1e-9)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "segment_len is too small or non-positive. Reset to 1.0.");
                segment_len = 1.0;
            }

            if (flow_mode < 1 || flow_mode > 3)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"flow_mode {flow_mode} is out of range [1..3]. Reset to 1.");
                flow_mode = 1;
            }

            if ((flow_mode == 2 || flow_mode == 3) && flux_range.Count == 0)
            {
                flux_range.Add(1.0);
            }

            double docTol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;

                        // Mode 3 prep (projection onto a line or arbitrary reference curve)
            Curve refCurve = null;
            double refCurveLength = 1.0;

            if (flow_mode == 3)
            {
                if (!hasFluxLineVect || !TryGetReferenceCurve(flux_line_vect, out refCurve) ||
                    refCurve == null || !refCurve.IsValid)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        "flow_mode = 3 requires a valid Line or Curve in flux_line_vect.");
                    return;
                }

                if (reverse_vect)
                    refCurve.Reverse();

                refCurveLength = refCurve.GetLength();
                if (refCurveLength <= docTol)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        "flow_mode = 3 requires a non-zero-length flux_line_vect curve.");
                    refCurve.Dispose();
                    return;
                }
            }

            //  Build layerToCurves
            // -----------------------------------------------------------------
            bool hasPlanes = planeTree != null && planeTree.PathCount > 0;
            var layerToCurves = new SortedDictionary<int, List<LayerCurveInput>>();
            int layerPathDimension = DetermineLayerPathDimension(pTree.Paths);

            if (layerPathDimension > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"Detected {layerPathDimension} common grafted path level(s). " +
                    $"Logical layers are read from path index {layerPathDimension}.");
            }

            // The tree is authoritative in both planar and non-planar workflows.
            // Never infer logical layers by Z: one non-planar pass may span a wide Z range.
            foreach (var path in pTree.Paths)
            {
                if (path.Indices.Length <= layerPathDimension)
                    continue;

                int layerIndex = path.Indices[layerPathDimension];
                var branch = pTree.get_Branch(path);
                if (branch == null)
                    continue;

                if (!layerToCurves.TryGetValue(layerIndex, out var list))
                {
                    list = new List<LayerCurveInput>();
                    layerToCurves[layerIndex] = list;
                }

                string suffix = path.Indices.Length > layerPathDimension + 1
                    ? string.Join(";", path.Indices.Skip(layerPathDimension + 1))
                    : "root";

                for (int itemIndex = 0; itemIndex < branch.Count; itemIndex++)
                {
                    var ghCrv = branch[itemIndex] as GH_Curve;
                    Curve curve = ghCrv?.Value;
                    if (curve == null || !curve.IsValid)
                        continue;

                    list.Add(new LayerCurveInput
                    {
                        Curve = curve,
                        CorrespondenceKey = suffix + "|" + itemIndex
                    });
                }
            }

            // Final sanity check
            if (layerToCurves.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "No valid curves after layer grouping.");
                return;
            }

            if (!hasPlanes)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "No layer planes supplied: stacking direction is estimated from fitted layer planes, " +
                    "then centroid displacement, with ref_plane normal only as the final fallback.");

            if (hasRefPlane)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "Using ref_plane as the base datum for first-layer height and bed/reference-plane checks.");

            // -------------------------------------------------------------
            // Optional safety: remove a first layer that lies on the base reference plane
            // -------------------------------------------------------------
            if (layerToCurves.Count > 0)
            {
                int firstKey = layerToCurves.Keys.First(); // SortedDictionary => first is lowest key
                var firstLayerCurves = layerToCurves[firstKey];

                // Tolerance for "close to the reference plane". Tune if needed.
                // docTol is usually tiny, so we set a practical lower bound too.
                double refPlaneTol = Math.Max(docTol * 50.0, 0.05); // 0.05 model units (e.g., 0.05 mm if units=mm)

                bool allCurvesNearReferencePlane = true;

                foreach (var entry in firstLayerCurves)
                {
                    var c = entry.Curve;
                    if (c == null || !c.IsValid) continue;

                    // If any curve extends noticeably away from the reference plane, we keep the layer.
                    if (!IsCurveNearPlane(c, refPlane, refPlaneTol, docTol))
                    {
                        allCurvesNearReferencePlane = false;
                        break;
                    }
                }

                if (allCurvesNearReferencePlane)
                {
                    layerToCurves.Remove(firstKey);

                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"Layer 0 was removed because it lies on the base reference plane (distance <= {refPlaneTol:0.###}). " +
                        "If you want this layer to be printed, move the reference plane or raise the object so the nozzle does not hit the printing plate."
                    );
                }

                // If removing made the dictionary empty, stop cleanly
                if (layerToCurves.Count == 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "All layers were removed or invalid after reference-plane safety check.");
                    return;
                }
            }


            int numLayers = layerToCurves.Count;
            int totalCurves = layerToCurves.Values.Sum(l => l.Count);

            // Validate m1_flux for Mode 1
            if (flow_mode == 1)
            {
                int c = m1_flux.Count;
                if (c == 0)
                {
                    m1_flux.Add(1.0);
                }
                else if (!(c == 1 || c == numLayers))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        $"m1_flux count {c} must be 1 (global) or #layers ({numLayers}).");
                    return;
                }
            }

            // Map layer index -> plane (if provided)
            var layerToPlane = new Dictionary<int, Plane>();
            if (planeTree != null && planeTree.PathCount > 0)
            {
                foreach (var path in planeTree.Paths)
                {
                    if (path.Indices.Length <= layerPathDimension) continue;
                    int li = path.Indices[layerPathDimension];

                    var branch = planeTree.get_Branch(path);
                    if (branch == null || branch.Count == 0) continue;

                    foreach (var item in branch)
                    {
                        var ghPl = item as GH_Plane;
                        if (ghPl == null) continue;

                        Plane pl = ghPl.Value;
                        if (!pl.IsValid) continue;

                        // First valid plane for this layer wins
                        if (!layerToPlane.ContainsKey(li))
                        {
                            layerToPlane[li] = pl;
                            break; // stop after first valid plane
                        }
                    }
                }
            }

            // Outputs
            var ptsTree = new DataTree<Point3d>();
            var fluxTree = new DataTree<double>();
            var hTree = new DataTree<double>();
            var planeOutTree = new DataTree<Plane>();
            var layerWTree = new DataTree<double>();
            var layerWfTree = new DataTree<double>();
            var printVolTree = new DataTree<double>();

            // Helper for flux range sampling
            double EvalRange(double t)
            {
                if (flux_range.Count == 1) return flux_range[0];

                t = Clamp01(t);
                double s = t * (flux_range.Count - 1);
                int i0 = (int)Math.Floor(s);
                if (i0 >= flux_range.Count - 1)
                    return flux_range[flux_range.Count - 1];

                double f = s - i0;
                return flux_range[i0] + f * (flux_range[i0 + 1] - flux_range[i0]);
            }

            // Flattened points of the previous layer, reused to compute heights
            List<List<Point3d>> prevLayerPtsPerCurve = null;
            Dictionary<string, PreviousCurveData> prevCurvesByKey = null;
            int nonPlanarLayerCount = 0;
            int localMatchCount = 0;
            int localFallbackCount = 0;
            double maximumPlanarityDeviation = 0.0;

            // Process detected logical layers in ascending order.
            var layerKeys = layerToCurves.Keys.ToList(); // already sorted (SortedDictionary)

            for (int layerOrderIdx = 0; layerOrderIdx < layerKeys.Count; layerOrderIdx++)
            {
                int layerKey = layerKeys[layerOrderIdx];
                var curveInputs = layerToCurves[layerKey];
                var curves = curveInputs.Select(x => x.Curve).ToList();
                var correspondenceKeys = curveInputs.Select(x => x.CorrespondenceKey).ToList();
                int curveCount = curves.Count;

                if (curveCount == 0)
                    continue;

                // Determine Mode 1 multiplier for this layer (if applicable)
                double layerFlux = 1.0;
                if (flow_mode == 1)
                {
                    if (m1_flux.Count == 1)
                        layerFlux = m1_flux[0];
                    else if (m1_flux.Count == numLayers)
                        layerFlux = m1_flux[layerOrderIdx];
                }

                // Determine layer direction for height estimation. Supplied planes
                // have priority, followed by a fitted plane, centroid displacement,
                // and finally world Z.
                Point3d currentCentroid;
                bool hasCurrentCentroid = TryCurveCentroid(curves, out currentCentroid);
                Point3d previousCentroid;
                bool hasPreviousCentroid = TryPointCentroid(prevLayerPtsPerCurve, out previousCentroid);
                Vector3d centroidDirection = (hasCurrentCentroid && hasPreviousCentroid)
                    ? currentCentroid - previousCentroid
                    : Vector3d.Unset;

                Plane fittedPlane;
                bool hasFittedPlane = TryFitLayerPlane(curves, docTol, out fittedPlane);
                double planarityDeviation = hasFittedPlane
                    ? MeasureLayerPlanarityDeviation(curves, fittedPlane)
                    : double.PositiveInfinity;
                double nonPlanarThreshold = Math.Max(docTol * 50.0, segment_len * 0.10);
                bool useLocalNonPlanarMode =
                    layerOrderIdx > 0 && planarityDeviation > nonPlanarThreshold;

                if (useLocalNonPlanarMode)
                {
                    nonPlanarLayerCount++;
                    if (!double.IsInfinity(planarityDeviation))
                        maximumPlanarityDeviation = Math.Max(
                            maximumPlanarityDeviation, planarityDeviation);
                }

                Vector3d layerDir;
                if (layerToPlane.TryGetValue(layerKey, out Plane pl))
                {
                    layerDir = pl.Normal;
                }
                else if (hasFittedPlane)
                    layerDir = fittedPlane.Normal;
                else if (centroidDirection.IsValid && !centroidDirection.IsZero)
                    layerDir = centroidDirection;
                else
                    layerDir = refPlane.Normal;

                if (!layerDir.Unitize())
                    layerDir = refPlane.Normal;

                Vector3d orientationReference = centroidDirection;
                if (!orientationReference.Unitize())
                    orientationReference = refPlane.Normal;
                if (layerDir * orientationReference < 0.0)
                    layerDir.Reverse();

                // Containers per curve for this layer
                var ptsPerCurve = new List<Point3d>[curveCount];
                var fluxPerCurve = new List<double>[curveCount];
                var hPerCurve = new List<double>[curveCount];

                // Parallel subdivision + flux assignment per curve
                Parallel.For(0, curveCount, i =>
                {
                    var crv = curves[i];
                    if (crv == null || !crv.IsValid)
                    {
                        ptsPerCurve[i] = null;
                        fluxPerCurve[i] = null;
                        hPerCurve[i] = null;
                        return;
                    }

                    // SAFE arclength subdivision
                    var pts = SubdivideByArcLength(crv, segment_len, docTol);
                    int n = pts.Count;
                    if (n < 2)
                    {
                        ptsPerCurve[i] = null;
                        fluxPerCurve[i] = null;
                        hPerCurve[i] = null;
                        return;
                    }

                    var fVals = new List<double>(n);
                    fVals.Add(0.0); // first point flux convention

                    if (flow_mode == 1)
                    {
                        // Mode 1: constant per layer/global multiplier
                        for (int k = 1; k < n; k++)
                            fVals.Add(layerFlux);
                    }
                    else if (flow_mode == 2)
                    {
                        // Mode 2: profile along curve
                        for (int k = 1; k < n; k++)
                        {
                            double t = (n == 2) ? 1.0 : (double)k / (n - 1);
                            fVals.Add(EvalRange(t));
                        }
                    }
                    else if (flow_mode == 3)
                    {
                        // Mode 3: profile along reference line
                        for (int k = 1; k < n; k++)
                        {
                            double curveParameter;
                            if (!refCurve.ClosestPoint(pts[k], out curveParameter))
                            {
                                fVals.Add(EvalRange(0.0));
                                continue;
                            }

                            Interval arcInterval = new Interval(refCurve.Domain.T0, curveParameter);
                            double arcLength = refCurve.GetLength(arcInterval);
                            double t = arcLength / refCurveLength;
                            fVals.Add(EvalRange(t));
                        }
                    }

                    ptsPerCurve[i] = pts;
                    fluxPerCurve[i] = fVals;
                    // We initialize height list later, when we have prevLayerPts available
                    hPerCurve[i] = new List<double>(new double[n]);
                });

                // For genuinely non-planar layers, register each current curve
                // locally against its corresponding previous source curve.
                LocalCorrespondence[][] localCorrespondence = null;
                if (useLocalNonPlanarMode)
                {
                    localCorrespondence = new LocalCorrespondence[curveCount][];
                    for (int i = 0; i < curveCount; i++)
                    {
                        var currentPoints = ptsPerCurve[i];
                        PreviousCurveData previousCurve;
                        if (currentPoints == null || prevCurvesByKey == null ||
                            !prevCurvesByKey.TryGetValue(correspondenceKeys[i], out previousCurve))
                            continue;

                        localCorrespondence[i] = BuildLocalCorrespondence(
                            currentPoints,
                            previousCurve.Points,
                            curves[i].IsClosed && previousCurve.IsClosed,
                            layerDir,
                            segment_len,
                            docTol);
                    }
                }

                // -----------------------------------------------------------------
                //  Compute layer_height for each point
                // -----------------------------------------------------------------

                // Prepare previous layer polylines (for L > 0)
                List<PrevCurveRef> prevRefs = null;
                RTree prevBbTree = null;

                if (layerOrderIdx > 0 && prevLayerPtsPerCurve != null && prevLayerPtsPerCurve.Count > 0)
                {
                    // Inflate bboxes a bit so candidates are not missed.
                    // Tie it to your segmentation and tolerance.
                    double inflate = Math.Max(docTol * 5.0, segment_len * 0.25);

                    prevRefs = BuildPrevPolylineRefs(prevLayerPtsPerCurve, inflate, out prevBbTree);
                }


                for (int i = 0; i < curveCount; i++)
                {
                    var pts = ptsPerCurve[i];
                    var fVals = fluxPerCurve[i];
                    var hVals = hPerCurve[i];

                    if (pts == null || fVals == null || hVals == null)
                        continue;

                    int n = pts.Count;

                    // Height per point
                    for (int k = 0; k < n; k++)
                    {
                        Point3d p = pts[k];
                        double h = 0.0;

                        // ---------------------------------------------------------
                        // Local stacking direction (localDir) per point
                        // - Based on curve tangent at this point
                        // - Oriented to be consistent with layerDir (no flips)
                        // ---------------------------------------------------------
                        Vector3d tan;
                        if (n >= 3)
                        {
                            Point3d pPrev = pts[Math.Max(k - 1, 0)];
                            Point3d pNext = pts[Math.Min(k + 1, n - 1)];
                            tan = pNext - pPrev;
                        }
                        else
                        {
                            tan = pts[n - 1] - pts[0];
                        }

                        if (!tan.Unitize())
                            tan = Vector3d.XAxis;

                        // Project layerDir onto plane perpendicular to tan:
                        // localDir = component of layerDir that is perpendicular to tan
                        Vector3d localDir = layerDir - Vector3d.Multiply(layerDir * tan, tan);

                        // If degenerate (tan parallel to layerDir), fallback to layerDir
                        if (!localDir.Unitize())
                            localDir = layerDir;

                        // Keep direction consistent: point roughly same way as layerDir
                        if (localDir * layerDir < 0.0)
                            localDir.Reverse();

                        LocalCorrespondence localMatch = null;
                        if (useLocalNonPlanarMode)
                        {
                            if (localCorrespondence != null &&
                                localCorrespondence[i] != null &&
                                k < localCorrespondence[i].Length)
                                localMatch = localCorrespondence[i][k];

                            if (localMatch != null && localMatch.Valid)
                            {
                                localDir = localMatch.Direction;
                                localMatchCount++;
                            }
                            else
                            {
                                localFallbackCount++;
                            }
                        }

                        // ---------------------------------------------------------
                        // Height computation
                        // ---------------------------------------------------------
                        if (layerOrderIdx == 0)
                        {
                            // Layer 0: distance to the base reference plane.
                            h = Math.Abs(refPlane.DistanceTo(p));
                        }
                        else if (localMatch != null && localMatch.Valid)
                        {
                            h = localMatch.Height;
                        }
                        else if (prevRefs != null && prevRefs.Count > 0 && prevBbTree != null)
                        {
                            // Prefer the same source curve at equal normalized arclength.
                            // Fall back to closest previous-layer geometry if the key is
                            // missing or the correspondence is geometrically implausible.
                            double searchR = Math.Max(segment_len * 2.5, docTol * 50.0);
                            double closestHeight = HeightClosestOnPrevPolylines(
                                p, localDir, prevRefs, prevBbTree, searchR, docTol);

                            PreviousCurveData previousCurve;
                            double correspondingHeight = 0.0;
                            bool correspondenceOk =
                                prevCurvesByKey != null &&
                                prevCurvesByKey.TryGetValue(correspondenceKeys[i], out previousCurve) &&
                                TryCorrespondingHeight(
                                    pts, k, previousCurve.Points, p, localDir,
                                    segment_len, docTol, out correspondingHeight);

                            h = correspondenceOk ? correspondingHeight : closestHeight;
                        }
                        else
                        {
                            // Fallback: no previous layer geometry
                            h = Math.Abs(refPlane.DistanceTo(p));
                        }

                        // Round to 3 decimals (and avoid -0.000)
                        double hClean = Math.Round(h, 3);
                        if (Math.Abs(hClean) < 1e-6) hClean = 0.0;

                        hVals[k] = hClean;

                        Plane pointPlane = PlaneFromPointTangentZ(p, tan, localDir);
                        planeOutTree.Add(pointPlane, new GH_Path(layerOrderIdx, i));
                    }
                }

                // Emit into DataTrees with path {layerOrderIdx; curveIndex}
                // Store this layer points per curve for the next iteration
                var thisLayerPtsPerCurve = new List<List<Point3d>>(curveCount);
                var thisCurvesByKey = new Dictionary<string, PreviousCurveData>();
                for (int i = 0; i < curveCount; i++)    
                {
                    var pts = ptsPerCurve[i];
                    if (pts == null) thisLayerPtsPerCurve.Add(null);
                    else
                    {
                        thisLayerPtsPerCurve.Add(pts);
                        thisCurvesByKey[correspondenceKeys[i]] = new PreviousCurveData
                        {
                            Points = pts,
                            IsClosed = curves[i].IsClosed
                        };
                    }
                }

                for (int i = 0; i < curveCount; i++)
                {
                    var pts = ptsPerCurve[i];
                    var fVals = fluxPerCurve[i];
                    var hVals = hPerCurve[i];
                    if (pts == null || fVals == null || hVals == null) continue;

                    if (pts.Count != fVals.Count || pts.Count != hVals.Count)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                            $"Layer {layerOrderIdx}, curve {i}: mismatched counts " +
                            $"(pts={pts.Count}, flux={fVals.Count}, h={hVals.Count}). " +
                            $"Skipping branch.");
                        continue;
                    }

                    var path = new GH_Path(layerOrderIdx, i);
                    for (int k = 0; k < pts.Count; k++)
                    {
                        ptsTree.Add(pts[k], path);
                        fluxTree.Add(fVals[k], path);
                        hTree.Add(hVals[k], path);
                    }
                }

                // Update prevLayerPts for next iteration
                prevLayerPtsPerCurve = thisLayerPtsPerCurve;
                prevCurvesByKey = thisCurvesByKey;
            }

            DA.SetDataTree(0, ptsTree);
            DA.SetDataTree(1, planeOutTree);
            DA.SetDataTree(2, fluxTree);
            DA.SetDataTree(3, hTree);
            if (!BuildWidthMetadataTrees(
                    ptsTree, fluxTree, hTree, layerWInput, hasLayerWInput, docTol,
                    layerWTree, layerWfTree, printVolTree))
                return;

            DA.SetData(4, new global::WASPer_3DP.WasperPrintPathGoo(
                new global::WASPer_3DP.WasperPrintPath(
                    ptsTree, planeOutTree, fluxTree, hTree,
                    layerW: layerWTree,
                    layerWf: layerWfTree,
                    printVol: printVolTree)));

            if (nonPlanarLayerCount > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"Auto layer-height mode used local non-planar correspondence on " +
                    $"{nonPlanarLayerCount} layer(s). Max fitted-plane deviation: " +
                    $"{maximumPlanarityDeviation:0.###}; local matches: {localMatchCount}; " +
                    $"fallbacks: {localFallbackCount}.");

                int localAttempts = localMatchCount + localFallbackCount;
                if (localAttempts > 0 && localFallbackCount > localAttempts * 0.10)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"Local non-planar correspondence fell back for " +
                        $"{100.0 * localFallbackCount / localAttempts:0.#}% of points. " +
                        "Check that path suffixes and branch item order remain stable between layers.");
                }
            }
        }
        #endregion

        #region Helpers

        private static bool HasNumberData(GH_Structure<GH_Number> tree)
        {
            return tree != null && tree.PathCount > 0 && !tree.IsEmpty;
        }

        private bool BuildWidthMetadataTrees(
            DataTree<Point3d> points,
            DataTree<double> flows,
            DataTree<double> heights,
            GH_Structure<GH_Number> layerWInput,
            bool hasLayerWInput,
            double tolerance,
            DataTree<double> layerW,
            DataTree<double> layerWf,
            DataTree<double> printVol)
        {
            double tol = Math.Max(tolerance, 1e-9);
            double? globalWidth = TryGetSingleNumber(layerWInput);

            for (int b = 0; b < points.BranchCount; b++)
            {
                GH_Path path = points.Paths[b];
                var pointBranch = points.Branch(path);
                var flowBranch = flows.PathExists(path) ? flows.Branch(path) : null;
                var heightBranch = heights.PathExists(path) ? heights.Branch(path) : null;
                var widthBranch = ResolveLayerWInputBranch(layerWInput, hasLayerWInput, path, b);
                int count = pointBranch?.Count ?? 0;

                var widths = new double[count];
                var widthsFlow = new double[count];

                for (int i = 0; i < count; i++)
                {
                    double height = GetTreeValue(heightBranch, i, 0.0);
                    double flow = GetTreeValue(flowBranch, i, 1.0);
                    double nominalWidth = hasLayerWInput
                        ? ResolveWidthValue(widthBranch, globalWidth, i, count, path)
                        : height * 2.5;

                    if (!IsPositiveFinite(nominalWidth))
                    {
                        AddRuntimeMessage(
                            GH_RuntimeMessageLevel.Error,
                            $"layer_w must be positive and finite. Bad value at generated branch {path}, item {i}.");
                        return false;
                    }

                    widths[i] = nominalWidth;
                    widthsFlow[i] = EstimateFlowAdjustedWidth(nominalWidth, height, flow, tol);
                    layerW.Add(widths[i], path);
                    layerWf.Add(widthsFlow[i], path);
                }

                for (int i = 0; i < count; i++)
                {
                    double volume = 0.0;
                    if (i > 0)
                    {
                        double length = pointBranch[i - 1].DistanceTo(pointBranch[i]);
                        double height = GetTreeValue(heightBranch, i, 0.0);
                        double area = BeadArea(widthsFlow[i], height, tol);
                        if (length > tol && area > 0.0 && double.IsFinite(length))
                            volume = length * area;
                    }
                    printVol.Add(volume, path);
                }
            }

            return true;
        }

        private object ResolveLayerWInputBranch(
            GH_Structure<GH_Number> layerWInput,
            bool hasLayerWInput,
            GH_Path path,
            int branchIndex)
        {
            if (!hasLayerWInput || layerWInput == null) return null;
            if (layerWInput.PathExists(path)) return layerWInput.get_Branch(path);
            if (branchIndex >= 0 && branchIndex < layerWInput.PathCount)
                return layerWInput.get_Branch(layerWInput.Paths[branchIndex]);
            return null;
        }

        private static double? TryGetSingleNumber(GH_Structure<GH_Number> tree)
        {
            if (tree == null || tree.DataCount != 1) return null;
            foreach (GH_Number number in tree.AllData(true))
            {
                if (number != null && double.IsFinite(number.Value))
                    return number.Value;
            }
            return null;
        }

        private double ResolveWidthValue(object branchObject, double? globalWidth, int itemIndex, int targetCount, GH_Path path)
        {
            if (branchObject is System.Collections.IList branch && branch.Count > 0)
            {
                if (branch.Count == 1)
                    return NumberFromObject(branch[0], double.NaN);
                if (branch.Count == targetCount)
                    return NumberFromObject(branch[itemIndex], double.NaN);

                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"layer_w branch {path} has {branch.Count} values but {targetCount} generated points. Using its first value for the whole branch.");
                return NumberFromObject(branch[0], double.NaN);
            }

            if (globalWidth.HasValue)
                return globalWidth.Value;

            return double.NaN;
        }

        private static double GetTreeValue(IList<double> branch, int itemIndex, double fallback)
        {
            if (branch == null || branch.Count == 0) return fallback;
            int index = branch.Count == 1 ? 0 : Math.Min(itemIndex, branch.Count - 1);
            double value = branch[index];
            return double.IsFinite(value) ? value : fallback;
        }

        private static double NumberFromObject(object item, double fallback)
        {
            return item is GH_Number number && double.IsFinite(number.Value) ? number.Value : fallback;
        }

        private static double EstimateFlowAdjustedWidth(double nominalWidth, double height, double flow, double tol)
        {
            if (nominalWidth <= tol || height <= tol || flow <= tol ||
                !double.IsFinite(nominalWidth) || !double.IsFinite(height) || !double.IsFinite(flow))
                return nominalWidth * Math.Max(flow, 1.0);

            double referenceWidth = Math.Max(nominalWidth, height);
            double referenceArea = BeadArea(referenceWidth, height, tol);
            return (flow * referenceArea) / height
                + height * (1.0 - Math.PI / 4.0);
        }

        private static double BeadArea(double width, double height, double tol)
        {
            if (width <= tol || height <= tol ||
                !double.IsFinite(width) || !double.IsFinite(height))
                return 0.0;

            double effectiveWidth = Math.Max(width, height * (1.0 - Math.PI / 4.0));
            double area = height * (effectiveWidth - height)
                + Math.PI * height * height / 4.0;
            return area > 0.0 && double.IsFinite(area) ? area : 0.0;
        }

        private static bool IsPositiveFinite(double value)
        {
            return value > 0.0 && double.IsFinite(value);
        }

        private static bool TryGetReferenceCurve(IGH_Goo goo, out Curve curve)
        {
            curve = null;

            if (goo is GH_Curve ghCurve && ghCurve.Value != null)
            {
                curve = ghCurve.Value.DuplicateCurve();
                return curve != null && curve.IsValid;
            }

            if (goo is GH_Line ghLine && ghLine.Value.IsValid)
            {
                curve = new LineCurve(ghLine.Value);
                return true;
            }

            if (goo is GH_ObjectWrapper wrapper)
            {
                if (wrapper.Value is Curve objectCurve)
                {
                    curve = objectCurve.DuplicateCurve();
                    return curve != null && curve.IsValid;
                }

                if (wrapper.Value is Line objectLine && objectLine.IsValid)
                {
                    curve = new LineCurve(objectLine);
                    return true;
                }
            }

            return false;
        }

        private static bool IsCurveNearPlane(Curve curve, Plane plane, double planeTol, double docTol)
        {
            if (curve == null || !curve.IsValid || !plane.IsValid)
                return false;

            var samples = new List<Point3d>();
            samples.Add(curve.PointAtStart);
            samples.Add(curve.PointAtEnd);

            double length = curve.GetLength();
            int sampleCount = length > docTol
                ? Math.Min(64, Math.Max(8, (int)Math.Ceiling(length / Math.Max(planeTol, docTol * 10.0))))
                : 8;

            for (int i = 1; i < sampleCount; i++)
            {
                double tNorm = (double)i / sampleCount;
                if (curve.LengthParameter(length * tNorm, out double t))
                    samples.Add(curve.PointAt(t));
            }

            foreach (Point3d p in samples)
            {
                if (Math.Abs(plane.DistanceTo(p)) > planeTol)
                    return false;
            }

            return true;
        }
        private sealed class LayerCurveInput
        {
            public Curve Curve;
            public string CorrespondenceKey;
        }

        private sealed class PreviousCurveData
        {
            public List<Point3d> Points;
            public bool IsClosed;
        }

        private sealed class LocalCorrespondence
        {
            public bool Valid;
            public Point3d PreviousPoint;
            public Vector3d Direction;
            public double Height;
        }

        private static int DetermineLayerPathDimension(IList<GH_Path> paths)
        {
            if (paths == null || paths.Count < 2)
                return 0;

            int minimumDepth = paths.Min(path => path?.Indices.Length ?? 0);
            if (minimumDepth <= 1)
                return 0;

            int commonDepth = 0;
            while (commonDepth < minimumDepth)
            {
                int value = paths[0].Indices[commonDepth];
                bool shared = true;
                for (int i = 1; i < paths.Count; i++)
                {
                    if (paths[i].Indices[commonDepth] != value)
                    {
                        shared = false;
                        break;
                    }
                }

                if (!shared)
                    break;
                commonDepth++;
            }

            // GH paths are unique, so multiple paths cannot share every index.
            // The first varying dimension after a common grafted prefix is the
            // logical layer dimension.
            return commonDepth < minimumDepth ? commonDepth : 0;
        }

        private static double MeasureLayerPlanarityDeviation(
            List<Curve> curves,
            Plane plane)
        {
            if (curves == null || !plane.IsValid)
                return double.PositiveInfinity;

            double maximum = 0.0;
            bool measured = false;
            foreach (Curve curve in curves)
            {
                if (curve == null || !curve.IsValid)
                    continue;

                double[] parameters = curve.DivideByCount(32, true);
                if (parameters == null || parameters.Length == 0)
                    parameters = new[] { curve.Domain.T0, curve.Domain.T1 };

                foreach (double parameter in parameters)
                {
                    double deviation = Math.Abs(plane.DistanceTo(curve.PointAt(parameter)));
                    if (double.IsNaN(deviation) || double.IsInfinity(deviation))
                        continue;
                    maximum = Math.Max(maximum, deviation);
                    measured = true;
                }
            }

            return measured ? maximum : double.PositiveInfinity;
        }

        private static LocalCorrespondence[] BuildLocalCorrespondence(
            List<Point3d> currentPoints,
            List<Point3d> previousPoints,
            bool closed,
            Vector3d coarseDirection,
            double segmentLength,
            double tolerance)
        {
            if (currentPoints == null || previousPoints == null ||
                currentPoints.Count < 2 || previousPoints.Count < 2)
                return null;

            if (!coarseDirection.Unitize())
                coarseDirection = Vector3d.ZAxis;

            double currentTotal;
            double previousTotal;
            double[] currentCumulative = BuildPolylineCumulative(
                currentPoints, closed, out currentTotal);
            double[] previousCumulative = BuildPolylineCumulative(
                previousPoints, closed, out previousTotal);

            if (currentTotal <= tolerance || previousTotal <= tolerance)
                return null;

            // Establish the seam in the plane transverse to the coarse stacking
            // direction. This is important for closed curves with changed 3D length.
            Point3d seamPoint;
            double seamNormalized;
            double seamLateral;
            if (!TryFindTransverseMatch(
                previousPoints, previousCumulative, previousTotal, closed,
                currentPoints[0], coarseDirection, 0.0,
                previousPoints.Count, 0.0,
                out seamPoint, out seamNormalized, out seamLateral))
                return null;

            Vector3d currentStartTangent = PointTangent(currentPoints, 0, closed);
            Vector3d previousStartTangent = PointTangentNearNormalized(
                previousPoints, seamNormalized, closed);
            bool reversed = currentStartTangent.IsValid &&
                            previousStartTangent.IsValid &&
                            currentStartTangent * previousStartTangent < 0.0;

            var matches = new LocalCorrespondence[currentPoints.Count];
            int searchWindow = Math.Max(3,
                (int)Math.Ceiling(previousPoints.Count * 0.08));

            for (int i = 0; i < currentPoints.Count; i++)
            {
                double normalized = Clamp01(currentCumulative[i] / currentTotal);
                double expected = reversed
                    ? seamNormalized - normalized
                    : seamNormalized + normalized;

                if (closed)
                    expected = expected - Math.Floor(expected);
                else
                    expected = Clamp01(expected);

                Point3d previousPoint;
                double matchedNormalized;
                double lateral;
                bool found = TryFindTransverseMatch(
                    previousPoints, previousCumulative, previousTotal, closed,
                    currentPoints[i], coarseDirection, expected,
                    searchWindow, 0.15,
                    out previousPoint, out matchedNormalized, out lateral);

                if (!found)
                {
                    matches[i] = new LocalCorrespondence { Valid = false };
                    continue;
                }

                Vector3d tangent = PointTangent(currentPoints, i, closed);
                if (!tangent.Unitize())
                    tangent = Vector3d.XAxis;

                Vector3d across = currentPoints[i] - previousPoint;
                Vector3d perpendicular =
                    across - Vector3d.Multiply(across * tangent, tangent);
                double height = perpendicular.Length;
                double plausibilityLimit = Math.Max(
                    segmentLength * 6.0, Math.Abs(across * coarseDirection) * 6.0);

                if (!perpendicular.Unitize() ||
                    double.IsNaN(height) || double.IsInfinity(height) ||
                    height <= tolerance || lateral > plausibilityLimit)
                {
                    matches[i] = new LocalCorrespondence { Valid = false };
                    continue;
                }

                if (perpendicular * coarseDirection < 0.0)
                    perpendicular.Reverse();

                matches[i] = new LocalCorrespondence
                {
                    Valid = true,
                    PreviousPoint = previousPoint,
                    Direction = perpendicular,
                    Height = height
                };
            }

            // A small directional average suppresses frame jitter without
            // flattening the actual height field.
            var smoothed = new Vector3d[matches.Length];
            for (int i = 0; i < matches.Length; i++)
            {
                if (matches[i] == null || !matches[i].Valid)
                    continue;

                Vector3d sum = Vector3d.Zero;
                int count = 0;
                for (int offset = -2; offset <= 2; offset++)
                {
                    int index = i + offset;
                    if (closed)
                    {
                        index %= matches.Length;
                        if (index < 0) index += matches.Length;
                    }
                    else if (index < 0 || index >= matches.Length)
                        continue;

                    LocalCorrespondence neighbor = matches[index];
                    if (neighbor == null || !neighbor.Valid)
                        continue;

                    Vector3d direction = neighbor.Direction;
                    if (direction * matches[i].Direction < 0.0)
                        direction.Reverse();
                    sum += direction;
                    count++;
                }

                if (count > 0 && sum.Unitize())
                    smoothed[i] = sum;
                else
                    smoothed[i] = matches[i].Direction;
            }

            for (int i = 0; i < matches.Length; i++)
            {
                if (matches[i] == null || !matches[i].Valid)
                    continue;

                Vector3d direction = smoothed[i];
                if (!direction.Unitize())
                    direction = matches[i].Direction;
                if (direction * coarseDirection < 0.0)
                    direction.Reverse();

                double projectedHeight = Math.Abs(
                    (currentPoints[i] - matches[i].PreviousPoint) * direction);
                if (projectedHeight <= tolerance ||
                    double.IsNaN(projectedHeight) ||
                    double.IsInfinity(projectedHeight))
                {
                    matches[i].Valid = false;
                    continue;
                }

                matches[i].Direction = direction;
                matches[i].Height = projectedHeight;
            }

            return matches;
        }

        private static double[] BuildPolylineCumulative(
            List<Point3d> points,
            bool closed,
            out double totalLength)
        {
            var cumulative = new double[points.Count];
            totalLength = 0.0;
            for (int i = 1; i < points.Count; i++)
            {
                totalLength += points[i - 1].DistanceTo(points[i]);
                cumulative[i] = totalLength;
            }

            if (closed && points.Count > 2)
                totalLength += points[points.Count - 1].DistanceTo(points[0]);

            return cumulative;
        }

        private static bool TryFindTransverseMatch(
            List<Point3d> points,
            double[] cumulative,
            double totalLength,
            bool closed,
            Point3d target,
            Vector3d coarseDirection,
            double expectedNormalized,
            int windowSegments,
            double parameterPenalty,
            out Point3d matchedPoint,
            out double matchedNormalized,
            out double lateralDistance)
        {
            matchedPoint = Point3d.Unset;
            matchedNormalized = 0.0;
            lateralDistance = double.PositiveInfinity;

            int segmentCount = closed ? points.Count : points.Count - 1;
            if (segmentCount <= 0)
                return false;

            int center = (int)Math.Floor(expectedNormalized * segmentCount);
            if (center >= segmentCount) center = segmentCount - 1;
            int radius = Math.Min(Math.Max(1, windowSegments), segmentCount);
            double bestCost = double.MaxValue;
            var visited = new HashSet<int>();

            for (int offset = -radius; offset <= radius; offset++)
            {
                int segment = center + offset;
                if (closed)
                {
                    segment %= segmentCount;
                    if (segment < 0) segment += segmentCount;
                }
                else if (segment < 0 || segment >= segmentCount)
                    continue;

                if (!visited.Add(segment))
                    continue;

                int next = (segment + 1) % points.Count;
                Point3d a = points[segment];
                Point3d b = points[next];
                Vector3d transverseSegment = b - a;
                transverseSegment -= Vector3d.Multiply(
                    transverseSegment * coarseDirection, coarseDirection);
                double transverseLengthSquared = transverseSegment.SquareLength;

                Vector3d toTarget = target - a;
                toTarget -= Vector3d.Multiply(
                    toTarget * coarseDirection, coarseDirection);

                double t = transverseLengthSquared > 1e-18
                    ? Clamp01((toTarget * transverseSegment) / transverseLengthSquared)
                    : 0.0;

                Point3d candidate = a + (b - a) * t;
                Vector3d lateralVector = target - candidate;
                lateralVector -= Vector3d.Multiply(
                    lateralVector * coarseDirection, coarseDirection);
                double lateralSquared = lateralVector.SquareLength;

                double segmentLength = a.DistanceTo(b);
                double candidateNormalized =
                    (cumulative[segment] + t * segmentLength) / totalLength;
                if (closed)
                    candidateNormalized -= Math.Floor(candidateNormalized);
                else
                    candidateNormalized = Clamp01(candidateNormalized);

                double normalizedOffset = Math.Abs(
                    candidateNormalized - expectedNormalized);
                if (closed)
                    normalizedOffset = Math.Min(normalizedOffset, 1.0 - normalizedOffset);
                double alongOffset = normalizedOffset * totalLength;
                double cost = lateralSquared +
                              parameterPenalty * parameterPenalty *
                              alongOffset * alongOffset;

                if (cost < bestCost)
                {
                    bestCost = cost;
                    matchedPoint = candidate;
                    matchedNormalized = candidateNormalized;
                    lateralDistance = Math.Sqrt(Math.Max(0.0, lateralSquared));
                }
            }

            return matchedPoint.IsValid;
        }

        private static Vector3d PointTangent(
            List<Point3d> points,
            int index,
            bool closed)
        {
            int previous = index - 1;
            int next = index + 1;
            if (closed)
            {
                if (previous < 0) previous = points.Count - 1;
                if (next >= points.Count) next = 0;
            }
            else
            {
                previous = Math.Max(0, previous);
                next = Math.Min(points.Count - 1, next);
            }

            Vector3d tangent = points[next] - points[previous];
            tangent.Unitize();
            return tangent;
        }

        private static Vector3d PointTangentNearNormalized(
            List<Point3d> points,
            double normalized,
            bool closed)
        {
            int count = points.Count;
            int index = (int)Math.Round(Clamp01(normalized) *
                (closed ? count : count - 1));
            if (closed) index %= count;
            else index = Math.Min(count - 1, index);
            return PointTangent(points, index, closed);
        }

        private static bool TryCurveCentroid(List<Curve> curves, out Point3d centroid)
        {
            centroid = Point3d.Unset;
            if (curves == null || curves.Count == 0)
                return false;

            double x = 0.0, y = 0.0, z = 0.0;
            int count = 0;
            foreach (Curve curve in curves)
            {
                if (curve == null || !curve.IsValid)
                    continue;

                Point3d p = curve.PointAt(curve.Domain.ParameterAt(0.5));
                if (!p.IsValid)
                    continue;

                x += p.X; y += p.Y; z += p.Z;
                count++;
            }

            if (count == 0)
                return false;

            centroid = new Point3d(x / count, y / count, z / count);
            return true;
        }

        private static bool TryPointCentroid(
            List<List<Point3d>> curves,
            out Point3d centroid)
        {
            centroid = Point3d.Unset;
            if (curves == null)
                return false;

            double x = 0.0, y = 0.0, z = 0.0;
            int count = 0;
            foreach (var points in curves)
            {
                if (points == null)
                    continue;

                foreach (Point3d p in points)
                {
                    if (!p.IsValid)
                        continue;
                    x += p.X; y += p.Y; z += p.Z;
                    count++;
                }
            }

            if (count == 0)
                return false;

            centroid = new Point3d(x / count, y / count, z / count);
            return true;
        }

        private static bool TryCorrespondingHeight(
            List<Point3d> currentPoints,
            int currentIndex,
            List<Point3d> previousPoints,
            Point3d currentPoint,
            Vector3d direction,
            double segmentLength,
            double tolerance,
            out double height)
        {
            height = 0.0;
            if (currentPoints == null || previousPoints == null ||
                currentPoints.Count < 2 || previousPoints.Count < 2 ||
                currentIndex < 0 || currentIndex >= currentPoints.Count)
                return false;

            double currentTotal = PolylineLength(currentPoints);
            double previousTotal = PolylineLength(previousPoints);
            if (currentTotal <= tolerance || previousTotal <= tolerance)
                return false;

            double currentLength = 0.0;
            for (int i = 1; i <= currentIndex; i++)
                currentLength += currentPoints[i - 1].DistanceTo(currentPoints[i]);

            double normalized = Clamp01(currentLength / currentTotal);

            // Preserve correspondence if curve direction is stable; reverse the
            // normalized coordinate when the opposite endpoint pairing is closer.
            double same = currentPoints[0].DistanceTo(previousPoints[0]) +
                          currentPoints[currentPoints.Count - 1].DistanceTo(previousPoints[previousPoints.Count - 1]);
            double reversed = currentPoints[0].DistanceTo(previousPoints[previousPoints.Count - 1]) +
                              currentPoints[currentPoints.Count - 1].DistanceTo(previousPoints[0]);
            if (reversed + tolerance < same)
                normalized = 1.0 - normalized;

            Point3d previousPoint;
            if (!PointAtNormalizedPolylineLength(previousPoints, normalized, previousTotal, out previousPoint))
                return false;

            Vector3d delta = currentPoint - previousPoint;
            double projected = Math.Abs(delta * direction);
            double lateralSquared = Math.Max(0.0, delta.SquareLength - projected * projected);
            double lateral = Math.Sqrt(lateralSquared);
            double plausibilityLimit = Math.Max(segmentLength * 4.0, projected * 4.0);

            if (double.IsNaN(projected) || double.IsInfinity(projected) ||
                projected <= tolerance || lateral > plausibilityLimit)
                return false;

            height = projected;
            return true;
        }

        private static double PolylineLength(List<Point3d> points)
        {
            double length = 0.0;
            for (int i = 1; i < points.Count; i++)
                length += points[i - 1].DistanceTo(points[i]);
            return length;
        }

        private static bool PointAtNormalizedPolylineLength(
            List<Point3d> points,
            double normalized,
            double totalLength,
            out Point3d point)
        {
            point = Point3d.Unset;
            if (points == null || points.Count < 2 || totalLength <= 0.0)
                return false;

            double target = Clamp01(normalized) * totalLength;
            double accumulated = 0.0;
            for (int i = 1; i < points.Count; i++)
            {
                Point3d a = points[i - 1];
                Point3d b = points[i];
                double length = a.DistanceTo(b);
                if (length <= 0.0)
                    continue;

                if (accumulated + length >= target)
                {
                    double t = Clamp01((target - accumulated) / length);
                    point = a + (b - a) * t;
                    return point.IsValid;
                }
                accumulated += length;
            }

            point = points[points.Count - 1];
            return point.IsValid;
        }

        private static bool AllValidLayersAreNonXy(
            SortedDictionary<int, List<Curve>> layerToCurves,
            double tol)
        {
            if (layerToCurves == null || layerToCurves.Count == 0)
                return false;

            int checkedLayers = 0;
            int nonXyLayers = 0;
            double normalTol = 0.996; // roughly within 5 degrees of world Z is considered XY-oriented.

            foreach (var kv in layerToCurves)
            {
                Plane fit;
                if (!TryFitLayerPlane(kv.Value, tol, out fit))
                    continue;

                Vector3d n = fit.Normal;
                if (!n.Unitize())
                    continue;

                checkedLayers++;

                if (Math.Abs(n * Vector3d.ZAxis) < normalTol)
                    nonXyLayers++;
            }

            return checkedLayers > 0 && nonXyLayers == checkedLayers;
        }

        private static bool TryFitLayerPlane(List<Curve> curves, double tol, out Plane plane)
        {
            plane = Plane.Unset;

            if (curves == null || curves.Count == 0)
                return false;

            var pts = new List<Point3d>();
            double tol2 = Math.Max(tol * tol, 1e-18);

            foreach (var c in curves)
            {
                if (c == null || !c.IsValid)
                    continue;

                AddUniquePoint(pts, c.PointAtStart, tol2);
                AddUniquePoint(pts, c.PointAtEnd, tol2);
                AddUniquePoint(pts, c.PointAt(c.Domain.ParameterAt(0.5)), tol2);

                double[] ts = c.DivideByCount(4, true);
                if (ts == null)
                    continue;

                foreach (double t in ts)
                    AddUniquePoint(pts, c.PointAt(t), tol2);
            }

            if (pts.Count < 3)
                return false;

            return Plane.FitPlaneToPoints(pts, out plane) == PlaneFitResult.Success && plane.IsValid;
        }

        private static void AddUniquePoint(List<Point3d> pts, Point3d p, double tol2)
        {
            if (pts == null || !p.IsValid)
                return;

            for (int i = 0; i < pts.Count; i++)
            {
                if (pts[i].DistanceToSquared(p) <= tol2)
                    return;
            }

            pts.Add(p);
        }

        private static Plane PlaneFromPointTangentZ(Point3d origin, Vector3d tangent, Vector3d zAxis)
        {
            if (!tangent.Unitize()) tangent = Vector3d.YAxis;
            if (!zAxis.Unitize()) zAxis = Vector3d.ZAxis;

            Vector3d yAxis = tangent - Vector3d.Multiply(tangent * zAxis, zAxis);
            if (!yAxis.Unitize())
            {
                yAxis = Vector3d.CrossProduct(zAxis, Vector3d.XAxis);
                if (!yAxis.Unitize())
                    yAxis = Vector3d.CrossProduct(zAxis, Vector3d.YAxis);
                yAxis.Unitize();
            }

            Vector3d xAxis = Vector3d.CrossProduct(yAxis, zAxis);
            if (!xAxis.Unitize()) xAxis = Vector3d.XAxis;

            return new Plane(origin, xAxis, yAxis);
        }

        // ------------------------------------------------------------
        // Height helpers (prev layer as polylines + RTree candidates)
        // ------------------------------------------------------------
        private class PrevCurveRef
        {
            public PolylineCurve Poly;
            public BoundingBox Bb;
            public int Id;
        }

        private static List<PrevCurveRef> BuildPrevPolylineRefs(
            List<List<Point3d>> prevPtsPerCurve,
            double inflate,
            out RTree bbTree)
        {
            var refs = new List<PrevCurveRef>();
            bbTree = new RTree();

            if (prevPtsPerCurve == null) return refs;

            int id = 0;
            foreach (var pts in prevPtsPerCurve)
            {
                if (pts == null || pts.Count < 2) continue;

                // Build polyline curve
                var pl = new Polyline(pts);
                if (!pl.IsValid || pl.Count < 2) continue;

                var plc = new PolylineCurve(pl);
                if (plc == null || !plc.IsValid) continue;

                // bbox (inflate for safer candidate catches)
                var bb = plc.GetBoundingBox(true);
                if (!bb.IsValid) continue;

                bb.Inflate(inflate);

                var r = new PrevCurveRef { Poly = plc, Bb = bb, Id = id };
                refs.Add(r);

                // Insert bbox center into RTree; we will query by bbox (search)
                bbTree.Insert(bb.Center, id);

                id++;
            }

            return refs;
        }

        private static double HeightClosestOnPrevPolylines(
            Point3d p,
            Vector3d layerDirUnit,
            List<PrevCurveRef> prevRefs,
            RTree bbTree,
            double searchRadius,
            double tol // kept for compatibility (not used by ClosestPoint anymore)
        )
        {
            if (prevRefs == null || prevRefs.Count == 0)
                return Math.Abs(p.Z);

            // 1) Candidate selection using bbox-center RTree:
            // (RTree in RhinoCommon is point-based, so we store bbox centers and filter by bbox.)
            var candIds = new List<int>();

            bbTree.Search(new Sphere(p, searchRadius), (sender, args) =>
            {
                candIds.Add(args.Id);
            });

            // Fallback if nothing found: widen search once
            if (candIds.Count == 0)
            {
                double r2 = searchRadius * 2.5;
                bbTree.Search(new Sphere(p, r2), (sender, args) => candIds.Add(args.Id));
            }

            bool useAll = candIds.Count == 0;

            double bestD2 = double.MaxValue;
            Point3d bestQ = p;

            if (useAll)
            {
                // brute-force over all polylines (rare)
                for (int i = 0; i < prevRefs.Count; i++)
                {
                    var prev = prevRefs[i];
                    if (prev?.Poly == null) continue;

                    double t;
                    if (!prev.Poly.ClosestPoint(p, out t)) continue; // <-- FIXED

                    var q = prev.Poly.PointAt(t);
                    double d2 = p.DistanceToSquared(q);
                    if (d2 < bestD2)
                    {
                        bestD2 = d2;
                        bestQ = q;
                    }
                }
            }
            else
            {
                var seen = new HashSet<int>();

                foreach (int id in candIds)
                {
                    if (!seen.Add(id)) continue;
                    if (id < 0 || id >= prevRefs.Count) continue;

                    var prev = prevRefs[id];
                    if (prev?.Poly == null) continue;

                    // bbox filter (cheap pre-check)
                    if (!prev.Bb.Contains(p)) continue;

                    double t;
                    if (!prev.Poly.ClosestPoint(p, out t)) continue; // <-- FIXED

                    var q = prev.Poly.PointAt(t);
                    double d2 = p.DistanceToSquared(q);
                    if (d2 < bestD2)
                    {
                        bestD2 = d2;
                        bestQ = q;
                    }
                }

                // If bbox filter was too strict and we got nothing, brute-force over all
                if (bestD2 == double.MaxValue)
                {
                    for (int i = 0; i < prevRefs.Count; i++)
                    {
                        var prev = prevRefs[i];
                        if (prev?.Poly == null) continue;

                        double t;
                        if (!prev.Poly.ClosestPoint(p, out t)) continue; // <-- FIXED

                        var q = prev.Poly.PointAt(t);
                        double d2 = p.DistanceToSquared(q);
                        if (d2 < bestD2)
                        {
                            bestD2 = d2;
                            bestQ = q;
                        }
                    }
                }
            }

            Vector3d d = p - bestQ;
            double h = Math.Abs(Vector3d.Multiply(d, layerDirUnit));
            return h;
        }

        private static double Clamp01(double t)
        {
            if (t < 0.0) return 0.0;
            if (t > 1.0) return 1.0;
            return t;
        }

        // SAFE non-destructive arclength subdivision
        private static List<Point3d> SubdivideByArcLength(Curve crv, double segLen, double tol)
        {
            var pts = new List<Point3d>();
            if (crv == null || !crv.IsValid) return pts;

            double L = 0.0;
            try { L = crv.GetLength(); }
            catch { L = 0.0; }

            double step = Math.Max(segLen, Math.Max(tol * 10.0, 1e-9));

            if (L <= step)
            {
                var a = crv.PointAtStart;
                var b = crv.PointAtEnd;

                if (a.DistanceToSquared(b) <= tol * tol)
                {
                    double tmid = crv.Domain.ParameterAt(0.5);
                    b = crv.PointAt(tmid);
                }

                pts.Add(a);
                pts.Add(b);

                if (pts.Count >= 2 &&
                    pts[0].DistanceToSquared(pts[pts.Count - 1]) <= tol * tol)
                {
                    double t75 = crv.Domain.ParameterAt(0.75);
                    pts[pts.Count - 1] = crv.PointAt(t75);
                }

                return pts;
            }

            int nSeg = Math.Max(1, (int)Math.Ceiling(L / step));
            int nPts = nSeg + 1;

            for (int i = 0; i < nPts; i++)
            {
                double s = (i == nPts - 1) ? L : (i * (L / nSeg));
                double t;

                if (!crv.LengthParameter(s, out t))
                {
                    t = crv.Domain.ParameterAt(L > 0 ? s / L : 0.0);
                }

                pts.Add(crv.PointAt(t));
            }

            if (crv.IsClosed && pts.Count >= 2)
            {
                if (pts[0].DistanceToSquared(pts[pts.Count - 1]) <= tol * tol)
                    pts.RemoveAt(pts.Count - 1);
            }

            if (pts.Count < 2 ||
                pts[0].DistanceToSquared(pts[pts.Count - 1]) <= tol * tol)
            {
                double tmid = crv.Domain.ParameterAt(0.5);
                Point3d mid = crv.PointAt(tmid);

                if (pts.Count == 0) pts.Add(crv.PointAtStart);

                pts.Add(mid);
            }

            return pts;
        }
        #endregion
    }
}
