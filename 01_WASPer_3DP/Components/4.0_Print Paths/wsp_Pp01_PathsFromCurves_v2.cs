// -----------------------------------------------------------------------------
//  wsp_Pp01_Path from Curves
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
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
#endregion

namespace WASPer_3DP.Components._4_0_Print_Paths
{
    public partial class wsp_Pp01_PathsFromCurves_v2 : GH_Component
    {
        // cached version tag from the WASPer_3DP assembly
        private readonly string _versionTag;
        private int _visibleOutputsMask;
        private string _cachedInputSignature = string.Empty;
        private global::WASPer_3DP.WasperPrintPath _cachedPath;
        private string _cachedSummary = string.Empty;
        private const string ShowAllOutputsKey = "wsp_gc01_v2_show_all_outputs";
        private const string VisibleOutputsMaskKey = "wsp_gc01_v2_visible_outputs_mask";

        private static readonly string[] OutputCatalog = global::WASPer_3DP.WasperPathDebugOutputs.CoreNickNames;
        private static readonly int AllOutputsMask = (1 << OutputCatalog.Length) - 1;
        private bool IsOutputVisible(string nickName)
        {
            int bit = Array.IndexOf(OutputCatalog, nickName);
            return bit >= 0 && (_visibleOutputsMask & (1 << bit)) != 0;
        }

        public wsp_Pp01_PathsFromCurves_v2()
          : base(
                "wsp_Pp01_WASPer Path from Curves v3",
                "PathCrvs v3",
                "Clean WASPer path constructor from print curves.\r\n" +
                "- Uses the unsimplified curve tree to define logical layers and stable\r\n" +
                "  source-curve correspondence between consecutive layers.\r\n" +
                "- Optional: one authoritative reference plane per layer, stored in wsp_path,\r\n" +
                "  used for diagnostics and as the direction for layer-height estimation.\r\n" +
                "- Optional: one or more flow_p strategies from Pp05_Define Flow. Each strategy may target one or several roles, but role assignments may not overlap. Roles without a matching strategy use flow 1.\r\n" +
                "- Optional: one base ref_plane, used as the first-layer height datum and\n" +
                "  bed/reference plane instead of always using World XY.\r\n" +
                "- plane_mode controls whether plane Z/layer-height directions follow the\n" +
                "  current automatic multi-axis logic or remain fixed to ref_plane Z for\n" +
                "  3-axis printing. Supplied la_planes are audited against generated frames.\r\n" +
                "- Subdivides curves by arclength, assigns flow per point (modes 1/2/3), and\n" +
                "  estimates local layer height from the corresponding source curve in the\n" +
                "  previous layer, with closest-geometry fallback. Auto mode preserves fitted-\r\n" +
                "  plane behavior for planar/tilted layers. Exact per-curve planarity is\r\n" +
                "  evaluated before any aggregate fit: coplanar and multi-planar collections\r\n" +
                "  retain stable curve normals, while only curves that are themselves\r\n" +
                "  genuinely non-planar use locally registered cross-layer vectors.\r\n" +
                "- Shell curves carrying WASPer.ShellSeam metadata retain their canonical pre-seam loop and can be re-edited from the component's Shell Seam Editor without applying the seam twice.\r\n" +
                "- Outputs wsp_path first. Extra diagnostic tree outputs are hidden by default and can be enabled from the component menu.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "4.0_Print Paths")
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = (v != null)
                ? $"v{v.Major}.{v.Minor}.{v.Build}"
                : "v1.0.x";

            this.Message = $"{_versionTag} - PathCrvs v3";
        }

        // GUID preserved for Grasshopper document compatibility.
        public override Guid ComponentGuid => new Guid("6AB6E12C-5FC4-4E0F-AE00-4744CE81B769");

        public override GH_Exposure Exposure => GH_Exposure.primary;

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


        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            global::WASPer_3DP.WasperPathDebugOutputs.AppendOutputVisibilityMenu(
                this,
                menu,
                "Debug Outputs",
                OutputCatalog,
                () => _visibleOutputsMask,
                mask =>
                {
                    RecordUndoEvent("Toggle outputs");
                    _visibleOutputsMask = mask;
                    RebuildOutputs();
                    ExpireSolution(true);
                });
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetInt32(VisibleOutputsMaskKey, _visibleOutputsMask);
            WriteSeamEditorState(writer);
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            // Rebuild the output topology BEFORE base.Read() restores saved wires, so the
            // optional outputs already exist when Grasshopper reconnects them (otherwise
            // saved wires to them are silently dropped on file open).
            //
            // _visibleOutputsMask migration: files saved before per-output toggles existed only
            // have the legacy boolean ShowAllOutputsKey. Map "Show all outputs" = true to every
            // bit set, so old files keep showing everything they used to.
            if (reader.ItemExists(VisibleOutputsMaskKey))
                _visibleOutputsMask = reader.GetInt32(VisibleOutputsMaskKey);
            else if (reader.ItemExists(ShowAllOutputsKey) && reader.GetBoolean(ShowAllOutputsKey))
                _visibleOutputsMask = AllOutputsMask;
            else
                _visibleOutputsMask = 0;
            ReadSeamEditorState(reader);
            _cachedInputSignature = string.Empty;
            _cachedPath = null;
            _cachedSummary = string.Empty;
            RebuildOutputs();
            return base.Read(reader);
        }

        private void RebuildOutputs()
        {
            while (Params.Output.Count > 2)
                Params.UnregisterOutputParameter(Params.Output[Params.Output.Count - 1], true);

            if (Params.Output.Count < 2)
            {
                while (Params.Output.Count > 0)
                    Params.UnregisterOutputParameter(Params.Output[Params.Output.Count - 1], true);
                RegisterCompactOutputParams();
            }

            RegisterDiagnosticOutputParams();

            Params.OnParametersChanged();
        }

        private void RegisterCompactOutputParams()
        {
            Params.RegisterOutputParam(new Param_GenericObject
            {
                Name = "wsp_path",
                NickName = "wsp_path",
                Description = "Packed WASPer Print Path using pt_planes as canonical path geometry, optional authoritative la_planes, flows, layer_h, nominal layer_w, flow-adjusted layer_wf, print_vol, exact source-curve provenance, semantic roles, and transferable Shell seam metadata retained on SourceCurves.",
                Access = GH_ParamAccess.item
            });

            Params.RegisterOutputParam(new Param_String
            {
                Name = "summary",
                NickName = "summary",
                Description = "Summary of generated path branches, plane locations, flow mode, and output state.",
                Access = GH_ParamAccess.item
            });
        }

        private void RegisterDiagnosticOutputParams()
        {
            global::WASPer_3DP.WasperPathDebugOutputs.RegisterCore(this, IsOutputVisible);
        }

        #region Register IO
        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddCurveParameter(
                "printing_path",
                "p_path",
                "Data tree of curves representing the printing path. The first varying index after any common grafted prefix defines the logical layer. Keep remaining path indices plus item order stable for reliable layer-height correspondence. Hidden WASPer.PathRole metadata is read and stored in wsp_path. Shell curves from In10 may additionally carry WASPer.ShellSeam metadata; Pp01 v3 rebuilds them from their canonical pre-seam loop and allows continued editing through its Shell Seam Editor.",
                GH_ParamAccess.tree);

            p.AddPlaneParameter(
                "layer_planes",
                "la_planes",
                "Optional data tree of authoritative reference planes, one per logical layer. If present, the first valid plane per matching logical layer supplies the preferred stacking direction for layer-height estimation and is stored in wsp_path.LayerPlanes on canonical {layer} branches. Missing layers remain absent; Pp01 does not silently store fitted replacements.",
                GH_ParamAccess.tree);
            p[1].Optional = true;

            p.AddGenericParameter(
                "flow_params",
                "flow_p",
                "Optional list of packed flow strategies from Pp05_Define Flow. Use separate strategies for Shell, Infill, Partition, Support, Transition, or Undefined. A role may be targeted by only one strategy; All conflicts with every other strategy. Roles not targeted by any supplied strategy retain neutral flow 1. If not connected, all paths use flow 1.",
                GH_ParamAccess.list);
            p[2].Optional = true;

            p.AddNumberParameter(
                "segment_len",
                "seg_len",
                "Target subdivision length in model units. Each curve is subdivided by arclength into segments of approximately this length (SAFE: no geometry edits).",
                GH_ParamAccess.item,
                2.0);

            p.AddPlaneParameter(
                "ref_plane",
                "ref_plane",
                "Optional base/reference plane for the print. This replaces the implicit World XY/Z=0 datum used for first-layer height and bed/reference-plane safety checks. If omitted, World XY is used.",
                GH_ParamAccess.item,
                Plane.WorldXY);
            p[4].Optional = true;

            p.AddNumberParameter(
                "layer_w",
                "layer_w",
                "Optional nominal/base bead width before flow adjustment, in model units. Accepts a single value, one value per branch, or a tree matching the generated pt_planes. If omitted, defaults to layer_h * 2.5. Pp01_v2 stores this as wsp_path.LayerW, estimates LayerWf by scaling the bead cross-sectional area with local flow and recovering the equivalent deposited width from layer_h, and stores per-segment PrintVol.",
                GH_ParamAccess.tree);
            p[5].Optional = true;

            int planeModeIndex = p.AddIntegerParameter(
                "plane_mode",
                "plane_mode",
                "Plane-normal and layer-height direction mode. 0 = Automatic / multi-axis: preserves fitted-layer and locally registered point-below directions. 1 = Fixed 3-axis: every pt_plane Z axis and every layer-height search uses ref_plane.ZAxis (World Z when ref_plane is omitted); X/Y may rotate around that fixed axis to follow the projected path tangent. Warnings report incompatibility with la_planes and non-planar layer geometry.",
                GH_ParamAccess.item,
                0);
            if (p[planeModeIndex] is Param_Integer planeModeParam)
            {
                planeModeParam.AddNamedValue("Automatic / multi-axis", 0);
                planeModeParam.AddNamedValue("Fixed 3-axis", 1);
            }
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "Packed WASPer Print Path using pt_planes as canonical path geometry, optional authoritative la_planes (one supplied reference plane per logical layer), flows, layer_h, nominal layer_w, flow-adjusted layer_wf, print_vol, exact source-curve provenance, semantic roles, and transferable Shell seam metadata retained on SourceCurves.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "summary",
                "summary",
                "Summary of generated path branches, plane locations, flow mode, and output state.",
                GH_ParamAccess.item);

            // Optional debug outputs are added dynamically by RebuildOutputs()/RegisterDiagnosticOutputParams(),
            // based on the persisted _visibleOutputsMask. RegisterOutputParams() runs once at construction,
            // before Read() has restored any persisted state, so a mask-gated branch here would never fire.
        }
        #endregion

        #region SolveInstance
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var totalWatch = Stopwatch.StartNew();
            double analysisMs = 0.0;
            double subdivisionMs = 0.0;
            double heightMs = 0.0;
            double packingMs = 0.0;
            int directCorrespondenceCount = 0;
            int spatialFallbackSearchCount = 0;
            int stablePlanarCurveCount = 0;
            int stablePlanarPointCount = 0;
            int incompatiblePlanarInfillCount = 0;
            int multiPlanarLayerCount = 0;

            // Inputs
            GH_Structure<GH_Curve> pTree;
            GH_Structure<GH_Plane> planeTree;
            var flowGoos = new List<IGH_Goo>();
            var flowParamsList = new List<global::WASPer_3DP.WasperFlowParams>();
            double segment_len = 1.0;
            Plane refPlane = Plane.WorldXY;
            int planeMode = 0;
            GH_Structure<GH_Number> layerWInput = null;
            bool hasLayerWInput = false;

            if (!DA.GetDataTree(0, out pTree)) return;
            DA.GetDataTree(1, out planeTree); // optional; may be null/empty
            bool hasFlowParams = DA.GetDataList(2, flowGoos) && flowGoos.Count > 0;
            if (hasFlowParams)
            {
                for (int i = 0; i < flowGoos.Count; i++)
                {
                    if (!TryGetFlowParams(flowGoos[i], out global::WASPer_3DP.WasperFlowParams parsed))
                    {
                        AddRuntimeMessage(
                            GH_RuntimeMessageLevel.Error,
                            $"flow_p item {i} is not a valid WASPer Flow Params object from Pp05.");
                        return;
                    }
                    flowParamsList.Add(parsed);
                }
            }

            if (!hasFlowParams)
                flowParamsList.Add(global::WASPer_3DP.WasperFlowParams.Default);

            if (!ValidateFlowRoleAssignments(flowParamsList, out string flowRoleError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, flowRoleError);
                return;
            }
            if (!DA.GetData(3, ref segment_len)) return;
            bool hasRefPlane = DA.GetData(4, ref refPlane) && refPlane.IsValid;
            hasLayerWInput = DA.GetDataTree(5, out layerWInput) && HasNumberData(layerWInput);
            DA.GetData(6, ref planeMode);

            if (planeMode < 0 || planeMode > 1)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "plane_mode must be 0 (Automatic / multi-axis) or 1 (Fixed 3-axis).");
                return;
            }
            bool fixedThreeAxis = planeMode == 1;

            if (!hasFlowParams)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "No flow_p connected: using default flow = 1.");

            if (!hasRefPlane)
            {
                if (Params.Input[4].SourceCount > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "ref_plane was supplied but is invalid. Falling back to World XY.");
                refPlane = Plane.WorldXY;
            }

            this.Message = fixedThreeAxis
                ? $"{_versionTag}\nPathCrvs v3 | fixed 3-axis"
                : $"{_versionTag}\nPathCrvs v3 | auto planes";

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

            for (int i = 0; i < flowParamsList.Count; i++)
            {
                int mode = flowParamsList[i]?.Mode ?? 1;
                if (mode < 1 || mode > 3)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        $"flow_p item {i} has mode {mode}; valid modes are 1, 2, or 3.");
                    return;
                }
            }

            double docTol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;

            string inputSignature = BuildInputCacheSignature(
                pTree,
                planeTree,
                flowParamsList,
                hasFlowParams,
                segment_len,
                refPlane,
                hasRefPlane,
                layerWInput,
                hasLayerWInput,
                planeMode,
                docTol);
            if (_cachedPath != null &&
                string.Equals(
                    inputSignature,
                    _cachedInputSignature,
                    StringComparison.Ordinal))
            {
                totalWatch.Stop();
                this.Message = fixedThreeAxis
                    ? $"{_versionTag}\nPathCrvs v3 | cached 3-axis"
                    : $"{_versionTag}\nPathCrvs v3 | cached auto";
                DA.SetData(0, new global::WASPer_3DP.WasperPrintPathGoo(_cachedPath));
                DA.SetData(
                    1,
                    _cachedSummary +
                    $"\npath cache: reused | lookup={totalWatch.Elapsed.TotalMilliseconds:0.###} ms");
                global::WASPer_3DP.WasperPathDebugOutputs.SetCore(DA, this, _cachedPath);
                return;
            }

            //  Build layerToCurves
            // -----------------------------------------------------------------
            bool hasPlanes = planeTree != null && planeTree.PathCount > 0;
            var layerToCurves = new SortedDictionary<int, List<LayerCurveInput>>();
            int layerPathDimension = DetermineLayerPathDimension(pTree.Paths);
            int invalidInputCurveCount = 0;

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
                    if (curve == null)
                        continue;
                    if (!curve.IsValid)
                    {
                        invalidInputCurveCount++;
                        continue;
                    }

                    list.Add(new LayerCurveInput
                    {
                        Curve = curve,
                        CorrespondenceKey = suffix + "|" + itemIndex,
                        Role = global::WASPer_3DP.WasperPathRoleMetadata.Get(curve)
                    });
                }
            }

            if (invalidInputCurveCount > 0)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"Pp01 ignored {invalidInputCurveCount} invalid input curve(s) before layer grouping.");

            // Final sanity check
            if (layerToCurves.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "No valid curves after layer grouping.");
                return;
            }

            if (!hasPlanes && !fixedThreeAxis)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "No layer planes supplied: stacking direction is estimated from fitted layer planes, " +
                    "then centroid displacement, with ref_plane normal only as the final fallback.");

            if (fixedThreeAxis)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "Fixed 3-axis mode: pt_plane Z axes and layer-height searches use ref_plane.ZAxis. " +
                    "Supplied la_planes remain stored as authoritative layer references and are audited for " +
                    "compatibility, but their normals do not override fixed-axis pt_plane orientation.");

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

            if (!TryBuildFlowStrategies(
                    flowParamsList,
                    numLayers,
                    docTol,
                    out List<ResolvedFlowStrategy> flowStrategies,
                    out string flowStrategyError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, flowStrategyError);
                return;
            }

            // Map layer index -> plane (if provided)
            var layerToPlane = new Dictionary<int, Plane>();
            int invalidLayerPlaneItemCount = 0;
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
                        if (ghPl == null)
                        {
                            invalidLayerPlaneItemCount++;
                            continue;
                        }

                        Plane pl = ghPl.Value;
                        if (!pl.IsValid)
                        {
                            invalidLayerPlaneItemCount++;
                            continue;
                        }

                        // First valid plane for this layer wins
                        if (!layerToPlane.ContainsKey(li))
                        {
                            layerToPlane[li] = pl;
                            break; // stop after first valid plane
                        }
                    }
                }
            }

            int seamMetadataCount = ApplySeamMetadataToLayers(
                layerToCurves,
                layerToPlane,
                refPlane,
                docTol);

            // Outputs
            var ptsTree = new DataTree<Point3d>();
            var fluxTree = new DataTree<double>();
            var hTree = new DataTree<double>();
            var planeOutTree = new DataTree<Plane>();
            var layerWTree = new DataTree<double>();
            var layerWfTree = new DataTree<double>();
            var printVolTree = new DataTree<double>();
            var sourceCurveTree = new DataTree<Curve>();
            var pathRoleTree = new DataTree<int>();
            var storedLayerPlaneTree = new DataTree<Plane>();

            // Flattened points of the previous layer, reused to compute heights
            List<List<Point3d>> prevLayerPtsPerCurve = null;
            Dictionary<string, PreviousCurveData> prevCurvesByKey = null;
            int nonPlanarLayerCount = 0;
            int localMatchCount = 0;
            int localFallbackCount = 0;
            double maximumPlanarityDeviation = 0.0;
            int detectedNonPlanarLayerCount = 0;
            var orientationAudits = new List<PlaneOrientationAudit>();
            Vector3d fixedAxis = refPlane.Normal;
            if (!fixedAxis.Unitize())
                fixedAxis = Vector3d.ZAxis;

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

                var analysisWatch = Stopwatch.StartNew();

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

                double nonPlanarThreshold = Math.Max(docTol * 50.0, segment_len * 0.10);
                LayerPlanarityKind planarityKind = ClassifyLayerPlanarity(
                    curves,
                    docTol,
                    out Plane fittedPlane,
                    out bool hasFittedPlane,
                    out double planarityDeviation,
                    out bool[] hasStableCurvePlane,
                    out Vector3d[] stableCurveNormals);
                bool geometryIsNonPlanar =
                    planarityKind == LayerPlanarityKind.GenuinelyNonPlanar;
                if (planarityKind == LayerPlanarityKind.MultiPlanar)
                    multiPlanarLayerCount++;
                bool useLocalNonPlanarMode =
                    !fixedThreeAxis && layerOrderIdx > 0 && geometryIsNonPlanar;

                if (geometryIsNonPlanar)
                {
                    detectedNonPlanarLayerCount++;
                    if (!double.IsInfinity(planarityDeviation))
                        maximumPlanarityDeviation = Math.Max(
                            maximumPlanarityDeviation, planarityDeviation);
                }
                bool hasSuppliedLayerPlane =
                    layerToPlane.TryGetValue(layerKey, out Plane suppliedLayerPlane);
                Vector3d layerDir;
                if (fixedThreeAxis)
                {
                    layerDir = fixedAxis;
                }
                else if (hasSuppliedLayerPlane)
                {
                    layerDir = suppliedLayerPlane.Normal;
                }
                else if (hasFittedPlane)
                    layerDir = fittedPlane.Normal;
                else if (centroidDirection.IsValid && !centroidDirection.IsZero)
                    layerDir = centroidDirection;
                else
                    layerDir = refPlane.Normal;

                if (!layerDir.Unitize())
                    layerDir = refPlane.Normal;

                if (!fixedThreeAxis)
                {
                    Vector3d orientationReference = centroidDirection;
                    if (!orientationReference.Unitize())
                        orientationReference = refPlane.Normal;
                    if (layerDir * orientationReference < 0.0)
                        layerDir.Reverse();
                }

                for (int i = 0; i < curveCount; i++)
                {
                    if (!hasStableCurvePlane[i])
                        continue;
                    Vector3d normal = stableCurveNormals[i];
                    if (normal * layerDir < 0.0)
                        normal.Reverse();
                    stableCurveNormals[i] = normal;
                    stablePlanarCurveCount++;
                }
                bool layerUsesLocalRegistration =
                    useLocalNonPlanarMode &&
                    hasStableCurvePlane.Any(isPlanar => !isPlanar);
                if (layerUsesLocalRegistration)
                    nonPlanarLayerCount++;

                var layerAudit = new PlaneOrientationAudit
                {
                    LayerOrder = layerOrderIdx,
                    LayerKey = layerKey,
                    HasInputPlane = hasSuppliedLayerPlane,
                    PlanarityKind = planarityKind,
                    GeometryIsNonPlanar = geometryIsNonPlanar,
                    PlanarityDeviation = planarityDeviation,
                    NonPlanarThreshold = nonPlanarThreshold,
                    FittedVsInputAngleDegrees =
                        hasSuppliedLayerPlane && hasFittedPlane
                            ? AcuteAngleDegrees(fittedPlane.Normal, suppliedLayerPlane.Normal)
                            : double.NaN,
                    FittedVsFixedAngleDegrees =
                        hasFittedPlane
                            ? AcuteAngleDegrees(fittedPlane.Normal, fixedAxis)
                            : double.NaN
                };
                orientationAudits.Add(layerAudit);

                // Containers per curve for this layer
                var ptsPerCurve = new List<Point3d>[curveCount];
                var fluxPerCurve = new List<double>[curveCount];
                var hPerCurve = new List<double>[curveCount];
                var planesPerCurve = new List<Plane>[curveCount];
                var cumulativePerCurve = new double[curveCount][];
                var totalLengthPerCurve = new double[curveCount];
                var allowDirectCorrespondence = Enumerable
                    .Repeat(true, curveCount)
                    .ToArray();

                // Cache the first (seam) plane per curve so closed curves can reuse
                // it when the seam vertex is re-emitted to close the loop (Option A).
                var firstPlanePerCurve = new Plane[curveCount];
                var hasFirstPlane = new bool[curveCount];
                var subdivisionFailure = new bool[curveCount];

                // Parallel subdivision + flux assignment per curve
                analysisWatch.Stop();
                analysisMs += analysisWatch.Elapsed.TotalMilliseconds;
                var subdivisionWatch = Stopwatch.StartNew();
                Parallel.For(0, curveCount, i =>
                {
                    var crv = curves[i];
                    if (crv == null || !crv.IsValid)
                    {
                        ptsPerCurve[i] = null;
                        fluxPerCurve[i] = null;
                        hPerCurve[i] = null;
                        subdivisionFailure[i] = true;
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
                        subdivisionFailure[i] = true;
                        return;
                    }

                    var fVals = new List<double>(n);
                    fVals.Add(0.0); // first point flux convention

                    ResolvedFlowStrategy flowStrategy = ResolveFlowStrategy(
                        curveInputs[i].Role,
                        flowStrategies);

                    if (flowStrategy == null)
                    {
                        // Roles not assigned to any strategy retain neutral flow.
                        for (int k = 1; k < n; k++)
                            fVals.Add(1.0);
                    }
                    else if (flowStrategy.Mode == 1)
                    {
                        double layerFlux = flowStrategy.LayerFlow(layerOrderIdx, numLayers);
                        for (int k = 1; k < n; k++)
                            fVals.Add(layerFlux);
                    }
                    else if (flowStrategy.Mode == 2)
                    {
                        for (int k = 1; k < n; k++)
                        {
                            double t = (n == 2) ? 1.0 : (double)k / (n - 1);
                            fVals.Add(flowStrategy.EvaluateProfile(t));
                        }
                    }
                    else
                    {
                        for (int k = 1; k < n; k++)
                            fVals.Add(flowStrategy.EvaluateAtPoint(pts[k]));
                    }

                    ptsPerCurve[i] = pts;
                    fluxPerCurve[i] = fVals;
                    // We initialize height list later, when we have prevLayerPts available
                    hPerCurve[i] = new List<double>(new double[n]);
                });
                subdivisionWatch.Stop();
                subdivisionMs += subdivisionWatch.Elapsed.TotalMilliseconds;

                for (int i = 0; i < curveCount; i++)
                {
                    List<Point3d> points = ptsPerCurve[i];
                    if (points == null || points.Count < 2)
                        continue;
                    cumulativePerCurve[i] = BuildPolylineCumulative(
                        points,
                        false,
                        out totalLengthPerCurve[i]);
                    planesPerCurve[i] = new List<Plane>(points.Count);
                }

                if (prevCurvesByKey != null)
                {
                    for (int i = 0; i < curveCount; i++)
                    {
                        if (!hasStableCurvePlane[i] ||
                            curveInputs[i].Role !=
                                global::WASPer_3DP.WasperPathRole.Infill ||
                            ptsPerCurve[i] == null ||
                            !prevCurvesByKey.TryGetValue(
                                correspondenceKeys[i],
                                out PreviousCurveData previousCurve))
                            continue;

                        allowDirectCorrespondence[i] =
                            IsPlanarInfillCorrespondenceCompatible(
                                ptsPerCurve[i],
                                cumulativePerCurve[i],
                                totalLengthPerCurve[i],
                                previousCurve,
                                stableCurveNormals[i],
                                segment_len,
                                docTol);
                        if (!allowDirectCorrespondence[i])
                            incompatiblePlanarInfillCount++;
                    }
                }

                int failedSubdivisionCount = subdivisionFailure.Count(failed => failed);
                if (failedSubdivisionCount > 0)
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"Logical layer {layerKey}: Pp01 could not subdivide {failedSubdivisionCount} valid curve(s) into printable points; those branches were skipped.");

                // For genuinely non-planar layers, register each current curve
                // locally against its corresponding previous source curve.
                var heightWatch = Stopwatch.StartNew();
                LocalCorrespondence[][] localCorrespondence = null;
                if (layerUsesLocalRegistration)
                {
                    localCorrespondence = new LocalCorrespondence[curveCount][];
                    for (int i = 0; i < curveCount; i++)
                    {
                        var currentPoints = ptsPerCurve[i];
                        PreviousCurveData previousCurve;
                        if (hasStableCurvePlane[i] ||
                            currentPoints == null || prevCurvesByKey == null ||
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
                    var planeVals = planesPerCurve[i];

                    if (pts == null || fVals == null || hVals == null || planeVals == null)
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

                        Vector3d localDir;
                        if (fixedThreeAxis)
                        {
                            // A 3-axis machine keeps the tool axis fixed. The path
                            // tangent is projected only when constructing plane X/Y;
                            // it must not tilt plane Z or the height-search direction.
                            localDir = fixedAxis;
                            if (Math.Abs(tan * fixedAxis) > 0.996194698)
                                layerAudit.FixedAxisTangentDegeneracies++;
                        }
                        else if (hasStableCurvePlane[i])
                        {
                            // Individually planar paths can coexist in a logical
                            // layer that is non-planar as a whole. Keep their own
                            // stable plane normal instead of registering arbitrary
                            // arclength locations against a rotated path below.
                            localDir = stableCurveNormals[i];
                            stablePlanarPointCount++;
                        }
                        else
                        {
                            // Project layerDir onto the plane perpendicular to tan:
                            // localDir = component of layerDir perpendicular to tan.
                            localDir = layerDir - Vector3d.Multiply(layerDir * tan, tan);

                            // If degenerate (tan parallel to layerDir), fallback to layerDir.
                            if (!localDir.Unitize())
                                localDir = layerDir;

                            // Keep direction consistent: point roughly same way as layerDir.
                            if (localDir * layerDir < 0.0)
                                localDir.Reverse();
                        }

                        LocalCorrespondence localMatch = null;
                        bool requiresLocalRegistration =
                            layerUsesLocalRegistration &&
                            !hasStableCurvePlane[i];
                        if (requiresLocalRegistration)
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
                            PreviousCurveData previousCurve;
                            double correspondingHeight = 0.0;
                            bool correspondenceOk =
                                allowDirectCorrespondence[i] &&
                                prevCurvesByKey != null &&
                                prevCurvesByKey.TryGetValue(correspondenceKeys[i], out previousCurve) &&
                                TryCorrespondingHeight(
                                    pts,
                                    cumulativePerCurve[i],
                                    totalLengthPerCurve[i],
                                    k,
                                    previousCurve,
                                    p,
                                    localDir,
                                    segment_len, docTol, out correspondingHeight);

                            if (correspondenceOk)
                            {
                                directCorrespondenceCount++;
                                h = correspondingHeight;
                            }
                            else
                            {
                                spatialFallbackSearchCount++;
                                double searchR = Math.Max(
                                    segment_len * 2.5,
                                    docTol * 50.0);
                                h = HeightClosestOnPrevPolylines(
                                    p,
                                    localDir,
                                    prevRefs,
                                    prevBbTree,
                                    searchR,
                                    docTol);
                            }
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
                        planeVals.Add(pointPlane);
                        if (hasSuppliedLayerPlane)
                            layerAudit.AddGeneratedNormal(
                                pointPlane.Normal,
                                suppliedLayerPlane.Normal);

                        if (k == 0)
                        {
                            firstPlanePerCurve[i] = pointPlane;
                            hasFirstPlane[i] = true;
                        }
                    }
                }
                heightWatch.Stop();
                heightMs += heightWatch.Elapsed.TotalMilliseconds;

                // Emit into DataTrees with path {layerOrderIdx; curveIndex}
                // Store this layer points per curve for the next iteration
                var packingWatch = Stopwatch.StartNew();
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
                            IsClosed = curves[i].IsClosed,
                            Cumulative = cumulativePerCurve[i],
                            TotalLength = totalLengthPerCurve[i]
                        };
                    }
                }

                for (int i = 0; i < curveCount; i++)
                {
                    var pts = ptsPerCurve[i];
                    var fVals = fluxPerCurve[i];
                    var hVals = hPerCurve[i];
                    var planeVals = planesPerCurve[i];
                    if (pts == null || fVals == null || hVals == null || planeVals == null)
                        continue;

                    if (pts.Count != fVals.Count || pts.Count != hVals.Count)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                            $"Layer {layerOrderIdx}, curve {i}: mismatched counts " +
                            $"(pts={pts.Count}, flux={fVals.Count}, h={hVals.Count}). " +
                            $"Skipping branch.");
                        continue;
                    }

                    var path = new GH_Path(layerOrderIdx, i);
                    if (curves[i] != null && curves[i].IsValid)
                    {
                        Curve sourceCopy = curves[i].DuplicateCurve();
                        global::WASPer_3DP.WasperPathRoleMetadata.Set(
                            sourceCopy,
                            curveInputs[i].Role);
                        global::WASPer_3DP.PatternEditing.WasperShellSeamMetadata.Copy(
                            curves[i],
                            sourceCopy);
                        sourceCurveTree.Add(sourceCopy, path);
                    }
                    pathRoleTree.Add((int)curveInputs[i].Role, path);
                    ptsTree.AddRange(pts, path);
                    fluxTree.AddRange(fVals, path);
                    hTree.AddRange(hVals, path);
                    planeOutTree.AddRange(planeVals, path);

                    // Close the loop for closed source curves: re-add the seam vertex
                    // (== first point) so downstream consumers print/render the final
                    // segment back to the start instead of leaving a gap of ~seg_len.
                    // Only the emitted path is closed here; the internal working lists
                    // (prevLayerPtsPerCurve, correspondence keys) stay OPEN so the
                    // layer-height correspondence math is not affected.
                    bool curveIsClosed = curves[i] != null && curves[i].IsClosed;
                    bool alreadyClosed =
                        pts[pts.Count - 1].DistanceToSquared(pts[0]) <= docTol * docTol;
                    if (curveIsClosed && pts.Count >= 2 && !alreadyClosed)
                    {
                        // Closing-segment flux: reuse the last real segment's flux so the
                        // closing move never under-extrudes. fVals[0] is 0 by the
                        // first-point convention and must NOT be used here.
                        double closingFlux = fVals[fVals.Count - 1];

                        ptsTree.Add(pts[0], path);
                        fluxTree.Add(closingFlux, path);
                        hTree.Add(hVals[0], path); // seam == pts[0], same local layer height

                        Plane seamPlane = hasFirstPlane[i]
                            ? firstPlanePerCurve[i]
                            : Plane.WorldXY;
                        planeOutTree.Add(seamPlane, path);
                    }
                }

                // Update prevLayerPts for next iteration
                prevLayerPtsPerCurve = thisLayerPtsPerCurve;
                prevCurvesByKey = thisCurvesByKey;
                packingWatch.Stop();
                packingMs += packingWatch.Elapsed.TotalMilliseconds;
            }

            var finalPackingWatch = Stopwatch.StartNew();
            if (!BuildWidthMetadataTrees(
                    ptsTree, fluxTree, hTree, layerWInput, hasLayerWInput, docTol,
                    layerWTree, layerWfTree, printVolTree))
                return;

            for (int layerOrderIdx = 0; layerOrderIdx < layerKeys.Count; layerOrderIdx++)
            {
                int sourceLayerKey = layerKeys[layerOrderIdx];
                if (layerToPlane.TryGetValue(sourceLayerKey, out Plane supplied) &&
                    supplied.IsValid)
                {
                    storedLayerPlaneTree.Add(supplied, new GH_Path(layerOrderIdx));
                }
            }

            var wspPath = new global::WASPer_3DP.WasperPrintPath(
                ptsTree, planeOutTree, fluxTree, hTree,
                layerW: layerWTree,
                layerWf: layerWfTree,
                printVol: printVolTree,
                sourceCurves: sourceCurveTree,
                pathRoles: pathRoleTree,
                layerPlanes: storedLayerPlaneTree.BranchCount > 0
                    ? storedLayerPlaneTree
                    : null,
                contentSignature: inputSignature);
            finalPackingWatch.Stop();
            packingMs += finalPackingWatch.Elapsed.TotalMilliseconds;

            int undefinedRoleBranches = pathRoleTree.AllData()
                .Count(value =>
                    value == (int)global::WASPer_3DP.WasperPathRole.Undefined);
            if (undefinedRoleBranches > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{undefinedRoleBranches} path branch(es) contained no WASPer role metadata " +
                    "and were stored as Undefined. General operations using All or Undefined " +
                    "remain available. Use Sl08 Assign Path Role before Pp01 when Shell, Infill, " +
                    "or Partition behavior is required; Pp15 Assign Print Path Roles can assign roles afterward.");
            }

            int missingLayerPlaneCount = hasPlanes
                ? layerKeys.Count(key => !layerToPlane.ContainsKey(key))
                : layerKeys.Count;

            totalWatch.Stop();
            this.Message = fixedThreeAxis
                ? $"{_versionTag}\nPathCrvs v3 | fixed 3-axis"
                : $"{_versionTag}\nPathCrvs v3 | auto planes";
            string performanceSummary =
                $"\nperformance [ms]: total={totalWatch.Elapsed.TotalMilliseconds:0.###}, " +
                $"layer_analysis={analysisMs:0.###}, subdivision_flow={subdivisionMs:0.###}, " +
                $"height={heightMs:0.###}, packing={packingMs:0.###}" +
                $"\nheight matching: direct={directCorrespondenceCount}, " +
                $"spatial_fallback={spatialFallbackSearchCount}" +
                $"\nplanar path stabilization: curves={stablePlanarCurveCount}, " +
                $"points={stablePlanarPointCount}, " +
                $"multi_planar_layers={multiPlanarLayerCount}, " +
                $"incompatible_infill_pairs={incompatiblePlanarInfillCount}";

            _cachedInputSignature = inputSignature;
            _cachedPath = wspPath;
            _cachedSummary =
                BuildSummary(
                    wspPath,
                    flowParamsList,
                    hasFlowParams,
                    planeMode,
                    orientationAudits,
                    invalidLayerPlaneItemCount,
                    missingLayerPlaneCount) +
                $" | shell seam metadata: {seamMetadataCount}";

            DA.SetData(0, new global::WASPer_3DP.WasperPrintPathGoo(wspPath));
            DA.SetData(
                1,
                _cachedSummary + performanceSummary + "\npath cache: rebuilt");

            global::WASPer_3DP.WasperPathDebugOutputs.SetCore(DA, this, wspPath);

            if (incompatiblePlanarInfillCount > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{incompatiblePlanarInfillCount} rotated or geometrically incompatible planar " +
                    "Infill curve pair(s) skipped same-arclength correspondence. Their stable " +
                    "per-curve normals and closest previous-layer height projection were used.");
            }

            if (multiPlanarLayerCount > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{multiPlanarLayerCount} logical layer(s) contain individually planar curves " +
                    "that do not share one common plane. They were classified as multi-planar, " +
                    "not genuinely non-planar; stable per-curve normals were preserved.");
            }

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

            EmitPlaneOrientationWarnings(
                planeMode,
                hasPlanes,
                orientationAudits,
                invalidLayerPlaneItemCount,
                missingLayerPlaneCount,
                detectedNonPlanarLayerCount,
                maximumPlanarityDeviation);
        }
        #endregion

        #region Helpers

        private static bool TryGetFlowParams(IGH_Goo goo, out global::WASPer_3DP.WasperFlowParams flowParams)
        {
            flowParams = null;
            if (goo is global::WASPer_3DP.WasperFlowParamsGoo flowGoo && flowGoo.Value != null)
            {
                flowParams = flowGoo.Value;
                return true;
            }

            if (goo is GH_ObjectWrapper wrapper)
            {
                if (wrapper.Value is global::WASPer_3DP.WasperFlowParams fp)
                {
                    flowParams = fp;
                    return true;
                }
                if (wrapper.Value is global::WASPer_3DP.WasperFlowParamsGoo wrappedGoo && wrappedGoo.Value != null)
                {
                    flowParams = wrappedGoo.Value;
                    return true;
                }
            }

            return false;
        }

        private static bool ValidateFlowRoleAssignments(
            IList<global::WASPer_3DP.WasperFlowParams> flowParams,
            out string error)
        {
            error = null;
            var roleOwner = new Dictionary<int, int>();

            for (int parameterIndex = 0; parameterIndex < (flowParams?.Count ?? 0); parameterIndex++)
            {
                global::WASPer_3DP.WasperFlowParams item = flowParams[parameterIndex];
                IList<int> selectors = item?.TargetRoles;
                if (selectors == null || selectors.Count == 0)
                    selectors = new[] { 0 };

                foreach (int selector in selectors)
                {
                    IEnumerable<int> claimedRoles = selector == 0
                        ? Enumerable.Range(1, 6)
                        : new[] { selector };

                    foreach (int claimedRole in claimedRoles)
                    {
                        if (roleOwner.TryGetValue(claimedRole, out int previousOwner))
                        {
                            error =
                                $"flow_p conflict: items {previousOwner} and {parameterIndex} both target " +
                                $"{WASPer_3DP.WasperGcodeTreeUtil.TargetRoleName(claimedRole)}. " +
                                "Each semantic role may be controlled by only one flow parameter set; " +
                                "All (0) therefore cannot be combined with another flow_p.";
                            return false;
                        }

                        roleOwner.Add(claimedRole, parameterIndex);
                    }
                }
            }

            return true;
        }

        private static bool TryBuildFlowStrategies(
            IList<global::WASPer_3DP.WasperFlowParams> flowParams,
            int layerCount,
            double tolerance,
            out List<ResolvedFlowStrategy> strategies,
            out string error)
        {
            strategies = new List<ResolvedFlowStrategy>();
            error = null;

            for (int i = 0; i < (flowParams?.Count ?? 0); i++)
            {
                global::WASPer_3DP.WasperFlowParams item = flowParams[i];
                if (item == null)
                {
                    error = $"flow_p item {i} is null.";
                    return false;
                }

                if (item.Mode == 1 &&
                    item.Mode1Flow.Count != 1 &&
                    item.Mode1Flow.Count != layerCount)
                {
                    error =
                        $"flow_p item {i} targets " +
                        $"{WASPer_3DP.WasperGcodeTreeUtil.TargetRoleNames(item.TargetRoles)} " +
                        $"but contains {item.Mode1Flow.Count} mode-1 values. Supply one global value " +
                        $"or exactly one value per logical layer ({layerCount}).";
                    return false;
                }

                Curve referenceCurve = null;
                double referenceLength = 1.0;
                if (item.Mode == 3)
                {
                    referenceCurve = item.ReferenceCurve?.DuplicateCurve();
                    if (referenceCurve == null || !referenceCurve.IsValid)
                    {
                        error =
                            $"flow_p item {i} uses mode 3 for " +
                            $"{WASPer_3DP.WasperGcodeTreeUtil.TargetRoleNames(item.TargetRoles)} " +
                            "but has no valid flow_crv.";
                        return false;
                    }

                    if (item.ReverseReference)
                        referenceCurve.Reverse();
                    referenceLength = referenceCurve.GetLength();
                    if (!double.IsFinite(referenceLength) || referenceLength <= tolerance)
                    {
                        referenceCurve.Dispose();
                        error = $"flow_p item {i} uses mode 3 but its flow_crv has zero length.";
                        return false;
                    }
                }

                strategies.Add(new ResolvedFlowStrategy(item, referenceCurve, referenceLength));
            }

            return true;
        }

        private static ResolvedFlowStrategy ResolveFlowStrategy(
            global::WASPer_3DP.WasperPathRole role,
            IList<ResolvedFlowStrategy> strategies)
        {
            for (int i = 0; i < (strategies?.Count ?? 0); i++)
            {
                if (global::WASPer_3DP.WasperGcodeTreeUtil.MatchesTargetRoles(
                        role,
                        strategies[i].TargetRoles))
                    return strategies[i];
            }
            return null;
        }

        private sealed class ResolvedFlowStrategy
        {
            private readonly List<double> _mode1Flow;
            private readonly List<double> _profile;
            private readonly Curve _referenceCurve;
            private readonly double _referenceLength;

            public ResolvedFlowStrategy(
                global::WASPer_3DP.WasperFlowParams source,
                Curve referenceCurve,
                double referenceLength)
            {
                Mode = source.Mode;
                TargetRoles = new List<int>(source.TargetRoles);
                _mode1Flow = new List<double>(source.Mode1Flow);
                _profile = new List<double>(source.Profile);
                _referenceCurve = referenceCurve;
                _referenceLength = referenceLength;
            }

            public int Mode { get; }
            public IList<int> TargetRoles { get; }

            public double LayerFlow(int layerIndex, int layerCount)
            {
                if (_mode1Flow.Count == 1)
                    return _mode1Flow[0];
                return _mode1Flow[Math.Min(Math.Max(0, layerIndex), layerCount - 1)];
            }

            public double EvaluateProfile(double t)
            {
                if (_profile.Count == 1)
                    return _profile[0];

                t = Clamp01(t);
                double scaled = t * (_profile.Count - 1);
                int lower = (int)Math.Floor(scaled);
                if (lower >= _profile.Count - 1)
                    return _profile[_profile.Count - 1];
                double fraction = scaled - lower;
                return _profile[lower] + fraction * (_profile[lower + 1] - _profile[lower]);
            }

            public double EvaluateAtPoint(Point3d point)
            {
                if (_referenceCurve == null ||
                    !_referenceCurve.ClosestPoint(point, out double parameter))
                    return EvaluateProfile(0.0);

                var interval = new Interval(_referenceCurve.Domain.T0, parameter);
                double arcLength = _referenceCurve.GetLength(interval);
                return EvaluateProfile(arcLength / _referenceLength);
            }
        }

        private enum LayerPlanarityKind
        {
            Planar,
            MultiPlanar,
            GenuinelyNonPlanar
        }

        private sealed class PlaneOrientationAudit
        {
            private readonly List<double> _generatedAngles = new List<double>();

            public int LayerOrder;
            public int LayerKey;
            public bool HasInputPlane;
            public LayerPlanarityKind PlanarityKind;
            public bool GeometryIsNonPlanar;
            public double PlanarityDeviation;
            public double NonPlanarThreshold;
            public double FittedVsInputAngleDegrees;
            public double FittedVsFixedAngleDegrees;
            public int ReversedGeneratedNormals;
            public int FixedAxisTangentDegeneracies;

            public int SampleCount => _generatedAngles.Count;
            public int ExceedFiveDegrees =>
                _generatedAngles.Count(value => value > 5.0);
            public double MaximumAngleDegrees =>
                _generatedAngles.Count == 0 ? double.NaN : _generatedAngles.Max();
            public double MeanAngleDegrees =>
                _generatedAngles.Count == 0 ? double.NaN : _generatedAngles.Average();
            public double P95AngleDegrees => Percentile(_generatedAngles, 0.95);
            public bool HasMaterialGeneratedMismatch =>
                SampleCount > 0 &&
                ((double.IsFinite(P95AngleDegrees) && P95AngleDegrees > 5.0) ||
                 (double.IsFinite(MaximumAngleDegrees) && MaximumAngleDegrees > 20.0));

            public void AddGeneratedNormal(Vector3d generated, Vector3d reference)
            {
                if (!generated.Unitize() || !reference.Unitize())
                    return;

                double dot = Math.Max(-1.0, Math.Min(1.0, generated * reference));
                if (dot < 0.0)
                    ReversedGeneratedNormals++;
                _generatedAngles.Add(
                    RhinoMath.ToDegrees(Math.Acos(Math.Abs(dot))));
            }

            private static double Percentile(List<double> values, double percentile)
            {
                if (values == null || values.Count == 0)
                    return double.NaN;

                var sorted = values.OrderBy(value => value).ToList();
                double index = Math.Max(0.0, Math.Min(1.0, percentile)) *
                    (sorted.Count - 1);
                int lower = (int)Math.Floor(index);
                int upper = Math.Min(sorted.Count - 1, lower + 1);
                double fraction = index - lower;
                return sorted[lower] +
                    fraction * (sorted[upper] - sorted[lower]);
            }
        }

        private static double AcuteAngleDegrees(Vector3d a, Vector3d b)
        {
            if (!a.Unitize() || !b.Unitize())
                return double.NaN;
            double dot = Math.Max(-1.0, Math.Min(1.0, a * b));
            return RhinoMath.ToDegrees(Math.Acos(Math.Abs(dot)));
        }

        private void EmitPlaneOrientationWarnings(
            int planeMode,
            bool hasLayerPlaneInput,
            List<PlaneOrientationAudit> audits,
            int invalidLayerPlaneItemCount,
            int missingLayerPlaneCount,
            int detectedNonPlanarLayerCount,
            double maximumPlanarityDeviation)
        {
            if (invalidLayerPlaneItemCount > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"la_planes contains {invalidLayerPlaneItemCount} invalid or non-plane item(s). " +
                    "Only the first valid plane associated with each logical layer is audited.");
            }

            if (hasLayerPlaneInput && missingLayerPlaneCount > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"la_planes has no valid matching plane for {missingLayerPlaneCount} logical layer(s). " +
                    (planeMode == 1
                        ? "Fixed 3-axis mode still uses ref_plane.ZAxis for those layers."
                        : "Automatic mode falls back to fitted geometry, centroid displacement, then ref_plane.ZAxis."));
            }

            var suppliedAudits = audits
                .Where(audit => audit.HasInputPlane)
                .ToList();
            var generatedMismatches = suppliedAudits
                .Where(audit => audit.HasMaterialGeneratedMismatch)
                .ToList();
            var suppliedNonPlanar = suppliedAudits
                .Where(audit => audit.GeometryIsNonPlanar)
                .ToList();
            var reversed = suppliedAudits
                .Where(audit => audit.ReversedGeneratedNormals > 0)
                .ToList();

            if (planeMode == 0 &&
                (generatedMismatches.Count > 0 || suppliedNonPlanar.Count > 0))
            {
                PlaneOrientationAudit worst = suppliedAudits
                    .Where(audit => audit.SampleCount > 0)
                    .OrderByDescending(audit => audit.P95AngleDegrees)
                    .ThenByDescending(audit => audit.MaximumAngleDegrees)
                    .FirstOrDefault();

                string worstText = worst == null
                    ? ""
                    : $" Worst layer: output {worst.LayerOrder} (input key {worst.LayerKey}); " +
                      $"{worst.ExceedFiveDegrees}/{worst.SampleCount} frames exceed 5 degrees, " +
                      $"mean={worst.MeanAngleDegrees:0.#} degrees, " +
                      $"p95={worst.P95AngleDegrees:0.#} degrees, " +
                      $"max={worst.MaximumAngleDegrees:0.#} degrees.";

                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"Automatic / multi-axis orientation departs materially from supplied la_planes " +
                    $"in {generatedMismatches.Count} layer(s); {suppliedNonPlanar.Count} supplied layer(s) " +
                    $"were detected as genuinely non-planar. Local point-below directions intentionally " +
                    $"replace the coarse la_plane normal on those paths.{worstText} See summary for the per-layer audit.");
            }

            if (planeMode == 1)
            {
                var fittedAxisMismatches = audits
                    .Where(audit =>
                        double.IsFinite(audit.FittedVsFixedAngleDegrees) &&
                        audit.FittedVsFixedAngleDegrees > 5.0)
                    .ToList();
                var allNonPlanar = audits
                    .Where(audit => audit.GeometryIsNonPlanar)
                    .ToList();

                if (generatedMismatches.Count > 0 ||
                    fittedAxisMismatches.Count > 0 ||
                    allNonPlanar.Count > 0)
                {
                    PlaneOrientationAudit worst = audits
                        .OrderByDescending(audit =>
                            audit.SampleCount > 0
                                ? audit.MaximumAngleDegrees
                                : audit.FittedVsFixedAngleDegrees)
                        .FirstOrDefault();
                    double worstAngle = worst == null
                        ? double.NaN
                        : worst.SampleCount > 0
                            ? worst.MaximumAngleDegrees
                            : worst.FittedVsFixedAngleDegrees;
                    string severity = double.IsFinite(worstAngle) && worstAngle >= 45.0
                        ? " This is a severe orientation mismatch (at least 45 degrees)."
                        : "";

                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"Fixed 3-axis mode forces every pt_plane Z axis and layer-height search " +
                        $"to ref_plane.ZAxis. {generatedMismatches.Count} supplied la_plane layer(s) " +
                        $"differ materially from that output axis; {fittedAxisMismatches.Count} fitted " +
                        $"layer plane(s) tilt more than 5 degrees from it; {allNonPlanar.Count} layer(s) " +
                        $"are genuinely non-planar. layer_h is measured along the fixed axis rather than " +
                        $"normal to those layers.{severity} See summary for the per-layer audit.");
                }

                int tangentDegeneracies = audits.Sum(
                    audit => audit.FixedAxisTangentDegeneracies);
                if (tangentDegeneracies > 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"{tangentDegeneracies} path tangent(s) are within 5 degrees of the fixed " +
                        "3-axis tool direction. Their projected in-plane tangent is degenerate, so " +
                        "Pp01 uses a stable fallback for plane X/Y rotation while keeping plane Z fixed.");
                }
            }

            if (reversed.Count > 0)
            {
                int reversedFrames = reversed.Sum(
                    audit => audit.ReversedGeneratedNormals);
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{reversedFrames} generated frame normal(s) oppose their supplied la_plane normal. " +
                    "Angular compatibility uses the acute plane angle; normal direction is governed by " +
                    (planeMode == 1 ? "ref_plane.ZAxis in fixed mode." : "the detected stacking direction in automatic mode."));
            }

            if (detectedNonPlanarLayerCount > 0 && !hasLayerPlaneInput)
            {
                AddRuntimeMessage(
                    planeMode == 1
                        ? GH_RuntimeMessageLevel.Warning
                        : GH_RuntimeMessageLevel.Remark,
                    $"{detectedNonPlanarLayerCount} layer(s) were detected as genuinely non-planar " +
                    $"without la_planes; maximum fitted-plane deviation={maximumPlanarityDeviation:0.###}. " +
                    (planeMode == 1
                        ? "Fixed 3-axis mode still forces ref_plane.ZAxis; check height and nozzle-clearance assumptions."
                        : "Automatic mode uses locally registered point-below directions where previous-layer correspondence is available."));
            }
        }

        private static string BuildSummary(
            global::WASPer_3DP.WasperPrintPath path,
            IList<global::WASPer_3DP.WasperFlowParams> flowParams,
            bool explicitFlow,
            int planeMode,
            List<PlaneOrientationAudit> audits,
            int invalidLayerPlaneItemCount,
            int missingLayerPlaneCount)
        {
            var summary = new StringBuilder();
            summary.AppendLine("wsp_Pp01_WASPer Path from Curves v2");
            summary.AppendLine($"plane locations: {path?.PointCount ?? 0}");
            summary.AppendLine($"branches: {path?.BranchCount ?? 0}");
            summary.AppendLine(
                $"authoritative layer planes: {path?.LayerPlanes?.DataCount ?? 0}");
            if (path?.HasPathRoles == true)
            {
                var roles = path.PathRoles.AllData().ToList();
                summary.AppendLine(
                    $"path roles: shell={roles.Count(value => value == (int)global::WASPer_3DP.WasperPathRole.Shell)}, " +
                    $"infill={roles.Count(value => value == (int)global::WASPer_3DP.WasperPathRole.Infill)}, " +
                    $"partition={roles.Count(value => value == (int)global::WASPer_3DP.WasperPathRole.Partition)}, " +
                    $"support={roles.Count(value => value == (int)global::WASPer_3DP.WasperPathRole.Support)}, " +
                    $"undefined={roles.Count(value => value == (int)global::WASPer_3DP.WasperPathRole.Undefined)}");
            }
            if (!explicitFlow)
            {
                summary.AppendLine("flow: default flow = 1 (all roles)");
            }
            else
            {
                summary.AppendLine($"flow strategies: {flowParams?.Count ?? 0}");
                for (int i = 0; i < (flowParams?.Count ?? 0); i++)
                {
                    global::WASPer_3DP.WasperFlowParams item = flowParams[i];
                    summary.AppendLine(
                        $"  flow_p[{i}]: mode {item.Mode}, roles=" +
                        global::WASPer_3DP.WasperGcodeTreeUtil.TargetRoleNames(item.TargetRoles));
                }
            }
            summary.AppendLine(
                planeMode == 1
                    ? "plane_mode: 1 Fixed 3-axis (pt_plane Z and layer_h direction = ref_plane.ZAxis)"
                    : "plane_mode: 0 Automatic / multi-axis (fitted and local point-below directions)");
            summary.AppendLine(
                $"la_planes audit: invalid_items={invalidLayerPlaneItemCount}, missing_logical_layers={missingLayerPlaneCount}");

            var noteworthy = (audits ?? new List<PlaneOrientationAudit>())
                .Where(audit =>
                    audit.GeometryIsNonPlanar ||
                    audit.HasMaterialGeneratedMismatch ||
                    (double.IsFinite(audit.FittedVsFixedAngleDegrees) &&
                     audit.FittedVsFixedAngleDegrees > 5.0) ||
                    audit.ReversedGeneratedNormals > 0 ||
                    audit.FixedAxisTangentDegeneracies > 0)
                .ToList();

            summary.AppendLine($"orientation audit: noteworthy_layers={noteworthy.Count}/{audits?.Count ?? 0}");
            foreach (PlaneOrientationAudit audit in noteworthy.Take(20))
            {
                string generated = audit.SampleCount > 0
                    ? $", generated_vs_la: mean={audit.MeanAngleDegrees:0.#}deg, p95={audit.P95AngleDegrees:0.#}deg, max={audit.MaximumAngleDegrees:0.#}deg, >5deg={audit.ExceedFiveDegrees}/{audit.SampleCount}, reversed={audit.ReversedGeneratedNormals}"
                    : ", generated_vs_la: n/a";
                summary.AppendLine(
                    $"  layer {audit.LayerOrder} (key {audit.LayerKey}): " +
                    $"planarity={audit.PlanarityKind}, fit_dev={audit.PlanarityDeviation:0.###}, " +
                    $"fit_threshold={audit.NonPlanarThreshold:0.###}, " +
                    $"fit_vs_la={FormatAngle(audit.FittedVsInputAngleDegrees)}, " +
                    $"fit_vs_fixed={FormatAngle(audit.FittedVsFixedAngleDegrees)}" +
                    generated +
                    $", fixed_tangent_fallbacks={audit.FixedAxisTangentDegeneracies}");
            }
            if (noteworthy.Count > 20)
                summary.AppendLine($"  ... {noteworthy.Count - 20} additional noteworthy layer(s) omitted.");

            summary.Append("outputs: wsp_path + summary");
            if (path == null)
                summary.Append("\nwarning: no path generated");
            return summary.ToString();
        }

        private static string FormatAngle(double value) =>
            double.IsFinite(value) ? $"{value:0.#}deg" : "n/a";

        private static bool HasNumberData(GH_Structure<GH_Number> tree)
        {
            return tree != null && tree.PathCount > 0 && !tree.IsEmpty;
        }

        private string BuildInputCacheSignature(
            GH_Structure<GH_Curve> curveTree,
            GH_Structure<GH_Plane> planeTree,
            IList<global::WASPer_3DP.WasperFlowParams> flowParams,
            bool hasFlowParams,
            double segmentLength,
            Plane referencePlane,
            bool hasReferencePlane,
            GH_Structure<GH_Number> layerWidthTree,
            bool hasLayerWidth,
            int planeMode,
            double tolerance)
        {
            global::WASPer_3DP.WasperCacheSignature signature =
                global::WASPer_3DP.WasperCacheSignature.Create();
            signature.Add("Pp01.PathCrvs.v3.cache.2.multi-role-flow");
            signature.Add(segmentLength);
            signature.Add(referencePlane);
            signature.Add(hasReferencePlane);
            signature.Add(planeMode);
            signature.Add(tolerance);
            signature.Add(hasFlowParams);

            signature.Add(curveTree?.PathCount ?? -1);
            if (curveTree != null)
            {
                for (int branchIndex = 0; branchIndex < curveTree.PathCount; branchIndex++)
                {
                    GH_Path path = curveTree.Paths[branchIndex];
                    signature.Add(path?.ToString());
                    IList<GH_Curve> branch = curveTree.Branches[branchIndex];
                    signature.Add(branch?.Count ?? -1);
                    if (branch == null) continue;
                    foreach (GH_Curve goo in branch)
                    {
                        Curve curve = goo?.Value;
                        signature.Add(curve);
                        signature.Add((int)global::WASPer_3DP.WasperPathRoleMetadata.Get(curve));
                        signature.Add(curve?.GetUserString(
                            global::WASPer_3DP.PatternEditing.WasperShellSeamMetadata.MetadataKey));
                    }
                }
            }

            signature.Add(planeTree?.PathCount ?? -1);
            if (planeTree != null)
            {
                for (int branchIndex = 0; branchIndex < planeTree.PathCount; branchIndex++)
                {
                    signature.Add(planeTree.Paths[branchIndex]?.ToString());
                    IList<GH_Plane> branch = planeTree.Branches[branchIndex];
                    signature.Add(branch?.Count ?? -1);
                    if (branch == null) continue;
                    foreach (GH_Plane plane in branch)
                    {
                        signature.Add(plane != null);
                        if (plane != null) signature.Add(plane.Value);
                    }
                }
            }

            signature.Add(hasLayerWidth);
            signature.Add(layerWidthTree?.PathCount ?? -1);
            if (layerWidthTree != null)
            {
                for (int branchIndex = 0; branchIndex < layerWidthTree.PathCount; branchIndex++)
                {
                    signature.Add(layerWidthTree.Paths[branchIndex]?.ToString());
                    IList<GH_Number> branch = layerWidthTree.Branches[branchIndex];
                    signature.Add(branch?.Count ?? -1);
                    if (branch == null) continue;
                    foreach (GH_Number number in branch)
                    {
                        signature.Add(number != null);
                        if (number != null) signature.Add(number.Value);
                    }
                }
            }

            signature.Add(flowParams?.Count ?? -1);
            if (flowParams != null)
            {
                foreach (global::WASPer_3DP.WasperFlowParams item in flowParams)
                {
                    signature.Add(item?.Mode ?? -1);
                    signature.Add(item?.Mode1Flow);
                    signature.Add(item?.Profile);
                    signature.Add(item?.ReferenceCurve);
                    signature.Add(item?.ReverseReference ?? false);
                    signature.Add(item?.TargetRoles);
                }
            }

            signature.Add(_seamOverrides.Count);
            foreach (KeyValuePair<int, global::WASPer_3DP.PatternEditing.WasperShellSeamSettings> item
                     in _seamOverrides.OrderBy(pair => pair.Key))
            {
                signature.Add(item.Key);
                global::WASPer_3DP.PatternEditing.WasperShellSeamSettings seam = item.Value;
                signature.Add(seam != null);
                if (seam == null) continue;
                signature.Add(seam.SeamU);
                signature.Add(seam.XSeam);
                signature.Add(seam.StartOffset);
                signature.Add(seam.StartTangentialOffset);
                signature.Add(seam.EndOffset);
                signature.Add(seam.EndTangentialOffset);
                signature.Add(seam.FilletRadius);
            }

            return signature.Finish();
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
            public global::WASPer_3DP.WasperPathRole Role;
        }

        private sealed class PreviousCurveData
        {
            public List<Point3d> Points;
            public bool IsClosed;
            public double[] Cumulative;
            public double TotalLength;
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

        private static bool IsPlanarInfillCorrespondenceCompatible(
            List<Point3d> currentPoints,
            double[] currentCumulative,
            double currentTotal,
            PreviousCurveData previousCurve,
            Vector3d planeNormal,
            double segmentLength,
            double tolerance)
        {
            List<Point3d> previousPoints = previousCurve?.Points;
            if (currentPoints == null || previousPoints == null ||
                currentPoints.Count < 2 || previousPoints.Count < 2 ||
                currentCumulative == null ||
                currentCumulative.Length != currentPoints.Count ||
                previousCurve.Cumulative == null ||
                previousCurve.Cumulative.Length != previousPoints.Count ||
                currentTotal <= tolerance ||
                previousCurve.TotalLength <= tolerance)
                return false;

            Vector3d currentTangent = PointTangent(
                currentPoints,
                0,
                false);
            Vector3d previousTangent = PointTangent(
                previousPoints,
                0,
                false);
            if (currentTangent.Unitize() && previousTangent.Unitize() &&
                Math.Abs(currentTangent * previousTangent) <
                    Math.Cos(RhinoMath.ToRadians(25.0)))
                return false;

            if (!planeNormal.Unitize())
                return false;
            double same =
                currentPoints[0].DistanceTo(previousPoints[0]) +
                currentPoints[currentPoints.Count - 1].DistanceTo(
                    previousPoints[previousPoints.Count - 1]);
            double reversed =
                currentPoints[0].DistanceTo(
                    previousPoints[previousPoints.Count - 1]) +
                currentPoints[currentPoints.Count - 1].DistanceTo(
                    previousPoints[0]);
            bool reverse = reversed + tolerance < same;

            var lateralDistances = new List<double>();
            int sampleCount = Math.Min(9, currentPoints.Count);
            for (int sample = 0; sample < sampleCount; sample++)
            {
                int index = sampleCount == 1
                    ? 0
                    : (int)Math.Round(
                        sample * (currentPoints.Count - 1.0) /
                        (sampleCount - 1.0));
                double normalized = Clamp01(
                    currentCumulative[index] / currentTotal);
                if (reverse)
                    normalized = 1.0 - normalized;
                if (!PointAtNormalizedPolylineLength(
                        previousPoints,
                        previousCurve.Cumulative,
                        normalized,
                        previousCurve.TotalLength,
                        out Point3d previousPoint))
                    return false;

                Vector3d delta = currentPoints[index] - previousPoint;
                double alongNormal = delta * planeNormal;
                Vector3d lateral = delta - alongNormal * planeNormal;
                if (lateral.IsValid)
                    lateralDistances.Add(lateral.Length);
            }
            if (lateralDistances.Count == 0)
                return false;
            lateralDistances.Sort();
            double median = lateralDistances[lateralDistances.Count / 2];
            double allowed = Math.Max(
                segmentLength * 2.5,
                tolerance * 50.0);
            return median <= allowed;
        }

        private static bool TryCorrespondingHeight(
            List<Point3d> currentPoints,
            double[] currentCumulative,
            double currentTotal,
            int currentIndex,
            PreviousCurveData previousCurve,
            Point3d currentPoint,
            Vector3d direction,
            double segmentLength,
            double tolerance,
            out double height)
        {
            height = 0.0;
            List<Point3d> previousPoints = previousCurve?.Points;
            if (currentPoints == null || previousPoints == null ||
                currentPoints.Count < 2 || previousPoints.Count < 2 ||
                currentCumulative == null ||
                currentCumulative.Length != currentPoints.Count ||
                previousCurve.Cumulative == null ||
                previousCurve.Cumulative.Length != previousPoints.Count ||
                currentIndex < 0 || currentIndex >= currentPoints.Count)
                return false;

            double previousTotal = previousCurve.TotalLength;
            if (currentTotal <= tolerance || previousTotal <= tolerance)
                return false;

            double normalized = Clamp01(
                currentCumulative[currentIndex] / currentTotal);

            // Preserve correspondence if curve direction is stable; reverse the
            // normalized coordinate when the opposite endpoint pairing is closer.
            double same = currentPoints[0].DistanceTo(previousPoints[0]) +
                          currentPoints[currentPoints.Count - 1].DistanceTo(previousPoints[previousPoints.Count - 1]);
            double reversed = currentPoints[0].DistanceTo(previousPoints[previousPoints.Count - 1]) +
                              currentPoints[currentPoints.Count - 1].DistanceTo(previousPoints[0]);
            if (reversed + tolerance < same)
                normalized = 1.0 - normalized;

            Point3d previousPoint;
            if (!PointAtNormalizedPolylineLength(
                    previousPoints,
                    previousCurve.Cumulative,
                    normalized,
                    previousTotal,
                    out previousPoint))
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

        private static bool PointAtNormalizedPolylineLength(
            List<Point3d> points,
            double[] cumulative,
            double normalized,
            double totalLength,
            out Point3d point)
        {
            point = Point3d.Unset;
            if (points == null || points.Count < 2 ||
                cumulative == null || cumulative.Length != points.Count ||
                totalLength <= 0.0)
                return false;

            double target = Clamp01(normalized) * totalLength;
            int index = Array.BinarySearch(cumulative, target);
            if (index >= 0)
            {
                point = points[Math.Min(index, points.Count - 1)];
                return point.IsValid;
            }
            int upper = ~index;
            if (upper <= 0)
            {
                point = points[0];
                return point.IsValid;
            }
            if (upper >= points.Count)
            {
                point = points[points.Count - 1];
                return point.IsValid;
            }
            int lower = upper - 1;
            double segmentLength = cumulative[upper] - cumulative[lower];
            if (segmentLength <= 0.0)
            {
                point = points[upper];
                return point.IsValid;
            }
            double t = Clamp01(
                (target - cumulative[lower]) / segmentLength);
            point = points[lower] + (points[upper] - points[lower]) * t;
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

        private static LayerPlanarityKind ClassifyLayerPlanarity(
            List<Curve> curves,
            double documentTolerance,
            out Plane representativePlane,
            out bool hasRepresentativePlane,
            out double maximumDeviation,
            out bool[] hasCurvePlane,
            out Vector3d[] curveNormals)
        {
            int count = curves?.Count ?? 0;
            hasCurvePlane = new bool[count];
            curveNormals = new Vector3d[count];
            representativePlane = Plane.Unset;
            hasRepresentativePlane = false;
            maximumDeviation = double.PositiveInfinity;
            if (count == 0)
                return LayerPlanarityKind.GenuinelyNonPlanar;

            double planeTolerance = Math.Max(documentTolerance * 10.0, 1e-6);
            var exactPlanes = new Plane[count];
            int planarCurveCount = 0;
            for (int i = 0; i < count; i++)
            {
                Curve curve = curves[i];
                if (curve == null ||
                    !curve.IsValid ||
                    !curve.TryGetPlane(out Plane curvePlane, planeTolerance))
                    continue;

                Vector3d normal = curvePlane.Normal;
                if (!normal.Unitize())
                    continue;
                exactPlanes[i] = curvePlane;
                hasCurvePlane[i] = true;
                curveNormals[i] = normal;
                planarCurveCount++;
            }

            if (planarCurveCount == count)
            {
                int first = Array.FindIndex(hasCurvePlane, value => value);
                Plane commonPlane = exactPlanes[first];
                Vector3d commonNormal = commonPlane.Normal;
                commonNormal.Unitize();
                double cosineTolerance = Math.Cos(RhinoMath.ToRadians(1.0));
                bool sharePlane = true;
                for (int i = 0; i < count; i++)
                {
                    Vector3d normal = curveNormals[i];
                    if (Math.Abs(normal * commonNormal) < cosineTolerance ||
                        Math.Abs(commonPlane.DistanceTo(exactPlanes[i].Origin)) > planeTolerance)
                    {
                        sharePlane = false;
                        break;
                    }
                }

                if (sharePlane)
                {
                    representativePlane = commonPlane;
                    hasRepresentativePlane = true;
                    maximumDeviation = MeasureLayerPlanarityDeviation(
                        curves,
                        representativePlane);
                    return LayerPlanarityKind.Planar;
                }

                // Every source curve is planar, but the logical layer occupies
                // more than one plane. This is multi-planar aggregate geometry,
                // not a genuinely non-planar curve.
                hasRepresentativePlane = TryFitLayerPlane(
                    curves,
                    documentTolerance,
                    out representativePlane);
                if (!hasRepresentativePlane)
                {
                    representativePlane = commonPlane;
                    hasRepresentativePlane = true;
                }
                maximumDeviation = hasRepresentativePlane
                    ? MeasureLayerPlanarityDeviation(curves, representativePlane)
                    : double.PositiveInfinity;
                return LayerPlanarityKind.MultiPlanar;
            }

            // At least one source curve is itself non-planar. Only this case is
            // allowed to activate local point-below correspondence.
            hasRepresentativePlane = TryFitLayerPlane(
                curves,
                documentTolerance,
                out representativePlane);
            maximumDeviation = hasRepresentativePlane
                ? MeasureLayerPlanarityDeviation(curves, representativePlane)
                : double.PositiveInfinity;
            return LayerPlanarityKind.GenuinelyNonPlanar;
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

                // Index the complete inflated box. Indexing only its centre
                // misses long curves whose geometry is near the query point
                // while their box centre is far away.
                bbTree.Insert(bb, id);

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

            // Candidate selection uses full inflated curve bounding boxes.
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
            double[] parameters = crv.DivideByCount(nSeg, true);
            if (parameters != null && parameters.Length >= 2)
            {
                for (int i = 0; i < parameters.Length; i++)
                    pts.Add(crv.PointAt(parameters[i]));
            }
            else
            {
                // Defensive compatibility fallback for curve types that cannot
                // return a native batch division.
                int nPts = nSeg + 1;
                for (int i = 0; i < nPts; i++)
                {
                    double s = (i == nPts - 1) ? L : (i * (L / nSeg));
                    if (!crv.LengthParameter(s, out double t))
                        t = crv.Domain.ParameterAt(L > 0 ? s / L : 0.0);
                    pts.Add(crv.PointAt(t));
                }
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
