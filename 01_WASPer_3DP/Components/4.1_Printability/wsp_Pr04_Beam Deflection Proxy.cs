using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;
using WASPer_3DP;

namespace WASPer_3DP.Components._4_1_Printability
{
    /// <summary>
    /// Ordered fresh-filament deformation proxy for unsupported spans in printing
    /// paths. Cantilevers use Euler-Bernoulli bending; two-anchor bridges use a
    /// reduced energy model combining bending with axial tension transmitted by
    /// previously deposited neighbors in the same curve.
    /// Same-layer curves that connect end-to-start (seam-split contours, common in
    /// TPMS/slicer output) are stitched into continuous chains before span
    /// extraction, so spans crossing curve seams are not misclassified as
    /// cantilevers. Comparative ranking tool, not a millimetre-accurate
    /// deflection prediction.
    /// </summary>
    public sealed class wsp_Pr04_Beam_Deflection_Proxy : GH_Component
    {
        private const double G_CONST = 9.80665;   // m/s2
        private const double SQRT3 = 1.7320508075688772; // von Mises: sigma_y = sqrt(3) * tau_y
        private const double CurvedSpanChordRatio = 0.85; // below this chord/length ratio a bridge is flagged as plan-curved
        private const double SeamTol = 0.01;      // [mm] endpoint coincidence tolerance for stitching same-layer curves
        private const int MaxSettleIterations = 200; // per-layer settling cap; runs re-anchor point by point, so long free runs need many iterations (early exit when nothing moves)
        private const double CantileverDetachFactor = 4.0; // one-anchor runs longer than this x bead width tear at the root and fall rigidly instead of hanging from the anchor

        public wsp_Pr04_Beam_Deflection_Proxy()
            : base(
                "wsp_Pr04_Beam Deflection Proxy",
                "Beam Proxy",
                "Deposits ordered seam-stitched curves layer by layer. Direct lower-layer contacts are grouped into sampling-independent patches and checked against age-dependent fresh interface capacity as load accumulates during fabrication. Failed contacts lose vertical support and enter the existing cantilever, bridge, tear, and collapse solver; no rigid-body sliding is assumed. Deformed segment lengths are free to change. This remains an uncalibrated comparative proxy, not nonlinear FEM or fresh-material flow simulation.",
                WASPerPalette.DesignFabrication,
                "4.1_Printability")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            Message = v == null ? "v1.0.x" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }

        public override Guid ComponentGuid => new("E7D31C58-4B6F-4A2E-9C81-2F5B7A94D0E3");
        public override GH_Exposure Exposure => GH_Exposure.primary;
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Pr04_Beam Deflection Proxy.png");
                    return stream == null ? null : new System.Drawing.Bitmap(stream);
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("wasper_path", "wsp_path", "WASPer Print Path enriched by Pr01 (required for span extraction). If it was also processed by Pr03, the packed accumulated load enables the d_loaded output.", GH_ParamAccess.item);
            p.AddGenericParameter("wasper_material", "wsp_mat", "Optional WASPer Material carrying fresh-state 3DP properties (E_fresh, E_rate, tau_y0, A_thix, density_wet, k_shape, k_fix, tau_interface, A_interface).", GH_ParamAccess.item);
            p.AddGenericParameter("3dp_props", "3dp_props", "Optional direct Ma12 properties. These override matching material properties. tau_interface and A_interface control fresh-contact capacity; when tau_interface is absent, tau_y0/A_thix are used as an explicitly uncalibrated fallback.", GH_ParamAccess.item);
            p.AddNumberParameter("layer_time", "la_time", "Time between layers in seconds (scalar). Used for the time-dependent stiffness E(t) = E_fresh + E_rate * t and yield stress tau_y(t) = tau_y0 + A_thix * t, with t = layer_index * layer_time.", GH_ParamAccess.item, 60.0);
            p.AddVectorParameter("gravity", "gravity", "Gravity direction. Default is world -Z. Magnitude is ignored; g = 9.80665 m/s2 is applied internally.", GH_ParamAccess.item, new Vector3d(0, 0, -1));
            p.AddNumberParameter("k_shape", "k_shape", "Bead section shape factor applied to both the cross-section area and the second moment of area, absorbing the difference between the idealized rectangular section (width x layer_h) and the real flattened-ellipse bead. Default 1.0 = rectangular. Deflection scales with 1/k_shape, so this factor directly calibrates the proxy once bridging tests exist.", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("k_fix", "k_fix", "End fixity factor multiplying the deflection. Default 1.0 = pinned ends (conservative). Fresh material embedding the span ends provides partial rotational restraint; a fully clamped bridge would be 0.2. Leave at 1.0 until calibrated.", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("limit", "limit", "Critical threshold for the governing deflection ratio delta/layer_h. Default 0.5: at half a layer height of sag, bonding with the next layer becomes unreliable; at 1.0 gaps and nozzle collisions are expected.", GH_ParamAccess.item, 0.5);
            p.AddNumberParameter("deflection_scale", "def_scale", "Deflection exaggeration factor for the def_pts/def_paths visualization outputs, like the deformation scale of an FEM viewer. Default 1.0 = true (unexaggerated) scale; fresh-state deflections are often sub-millimetre, so values of 10-50 make the sagging readable. Affects visualization outputs only — d_ratio, bend_ratio, and critical always use the true values.", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("support_thr", "sup_thr", "Support threshold on the Pr01 local printability score (print_loc, 0-1). A point joins an unsupported span when it has NO support point OR its print_loc is <= sup_thr. Pr01 records a support point even at beyond-critical overhang and expresses the problem through print_loc = 0, so with the default 0.0 every locally unprintable point (angle >= critical or zero contact width) is treated as spanning — physically it hangs rather than bears. Raise toward ~0.3 to also treat high-risk partial contact as spanning (more conservative, longer spans); set negative to restore the pure no-support-point criterion.", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("bead_width", "bead_w", "Nominal bead width used by the layer-by-layer contact pass. Default 0 derives max(2 x layer_h, Pr01 Wc). Deformed segment lengths are never constrained to their original values.", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("critical_angle", "crit_angle", "Legacy compatibility input retained so existing Grasshopper definitions keep their wires. The ordered same-curve chain solver does not use a user-defined critical angle: deformation depends on gravity, geometry, fresh stiffness, density, contact, and tensile capacity. This value is currently ignored.", GH_ParamAccess.item, 45.0);
            p.AddGenericParameter("sim_path", "sim", "Either a fabrication-progress fraction from 0.0 to 1.0 or the Program (P) output from Robots Program Simulation. For a Robots Program, Pr04 matches ordered program target coordinates to wsp_path points, so home, approach, travel, and hop targets do not shift the deposition cutoff. Points beyond the cutoff are omitted from def_wsp_path and visualization outputs p_pts through critical; the primary wsp_path output remains complete. Default 1.0 = complete print.", GH_ParamAccess.item);
            for (int i = 1; i <= 12; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("wasper_path", "wsp_path", "Complete print path enriched with Pr04 beam/collapse results plus fabrication-history interface_ratio and failure_flags bit 8. Interface failure means loss of vertical fresh-contact support, not sideways sliding. Pass this object to Gc13 or downstream path-aware components.", GH_ParamAccess.item);
            p.AddGenericParameter("def_wsp_path", "def_wsp_path", "Visualization-only path containing only the filament deposited up to sim_path, at its def_scale-adjusted collapsed position. Its point planes are regenerated along the deflected centerline so Gc10 keeps the original bead dimensions while orienting each profile approximately perpendicular to the collapsed filament. Frames reset after torn edges. Connect directly to Gc10 with its own simulation control left at full path. Segment lengths may change after collapse; do not use this path as fabrication input.", GH_ParamAccess.item);
            p.AddPointParameter("printing_pts", "p_pts", "Original undeformed printing points for the filament deposited up to sim_path. Branches and point counts match def_pts exactly, allowing direct comparison without unpacking wsp_path. Use the complete wsp_path output when the full original path is required.", GH_ParamAccess.tree);
            p.AddPointParameter("deflection_pts", "def_pts", "Layer-by-layer, curve-by-curve deposited points. Unsupported curve starts and trailing runs settle as cantilevers; internal bridge points are held by bending plus axial tension from previously deposited neighbors in the same curve. Torn bridges become two cantilevers. Neighbor distances are recomputed and original segment lengths are not rigidly preserved. def_scale affects visualization only.", GH_ParamAccess.tree);
            p.AddVectorParameter("deflection_vect", "def_vect", "Deflection vector per point: def_pts - p_pts, i.e. the (clamped, def_scale-exaggerated) displacement along the gravity direction. Zero for supported points. Use the vector length for gradient coloring or filtering, or the vectors themselves for arrow displays.", GH_ParamAccess.tree);
            p.AddBooleanParameter("collapse_loc", "collapse_loc", "Local mechanical collapse from this point's own bridge/cantilever span: governing d/layer_h >= 1 or bend_ratio >= 1.", GH_ParamAccess.tree);
            p.AddBooleanParameter("collapse_glob", "collapse_glob", "Fabrication-order collapse from the ordered same-curve chain solver. Includes unsupported starts, contact-limited cantilevers, tensile bridge failure, loss of contact caused by deformed lower paths, and contacts rejected after fresh interface-capacity failure. No rigid segment-length constraint is imposed.", GH_ParamAccess.tree);
            p.AddIntegerParameter("collapse_gen", "collapse_gen", "Global collapse generation: -1 stable, 0 local/direct collapse, 1 supported by generation 0, and 2+ later upward generations.", GH_ParamAccess.tree);
            p.AddBooleanParameter("torn", "torn", "True where the bead between this point and the NEXT point separated because the axial tension required by a two-anchor bridge exceeded the current tau_y-derived tensile-capacity proxy. The failed bridge is re-solved as two cantilevers, and the torn edge is excluded from contact geometry for upper layers. Use this flag to split def paths before Gc10 visualization.", GH_ParamAccess.tree);
            p.AddNumberParameter("deflection_ratio", "d_ratio", "Deflection ratio delta/layer_h per point from span self-weight at deposition. Supported points output 0. Cantilevers use delta = qL^4/(8 E(t) I). Two-anchor bridges minimize a reduced bending-plus-axial energy, so previously deposited neighbors in the same curve develop tension and reduce sag. k_fix calibrates rotational restraint. This is an unclamped comparative index: ~0.5 indicates bonding risk and >= 1 indicates failure.", GH_ParamAccess.tree);
            p.AddNumberParameter("deflection_loaded", "d_loaded", "Deflection ratio delta/layer_h per point using the Pr03 accumulated load as distributed line load (span total transmitted weight / span length) instead of bare self-weight. Only produced when the incoming wsp_path carries Pr03 fresh-risk data; otherwise empty. It represents the span state after upper layers have been deposited, and is the governing value for the critical output when present. Not clamped — see d_ratio.", GH_ParamAccess.tree);
            p.AddNumberParameter("bend_ratio", "bend_ratio", "Plastic bending check per point: maximum span bending stress sigma = M*(layer_h/2)/I (bridge M = qL^2/8, cantilever M = qL^2/2) divided by the tau_y-derived flexural strength sqrt(3)*tau_y(t) (von Mises). Values >= 1 indicate the span may yield in bending — a failure mode Pr03's axial demand/capacity does not cover. Empty when tau_y0 is not available.", GH_ParamAccess.tree);
            p.AddIntegerParameter("span_class", "span_class", "Span classification per point: 0 = supported (on the previous layer or the bed), 1 = bridge (unsupported run anchored at both ends), 2 = cantilever (anchored at one end only; fully unsupported chains are conservatively classified as cantilevers and reported in warnings). Spans are evaluated on seam-stitched chains, so classes are continuous across curve seams.", GH_ParamAccess.tree);
            p.AddNumberParameter("span_len", "span_len", "Unsupported span length per point in model units (millimetres), measured along the stitched path polyline from anchor to anchor. Supported points output 0. Deflection scales with L^4, so this is usually the dominant driver of d_ratio.", GH_ParamAccess.tree);
            p.AddBooleanParameter("critical", "critical", "True where the governing deflection ratio reaches limit, bend_ratio reaches 1.0, collapse occurs, or the fabrication-history interface ratio reaches 1.0. Interface diagnostics are packed in wsp_path and can be inspected with Gc13.", GH_ParamAccess.tree);
            p.AddTextParameter("summary", "summary", "Text report of resolved material properties, span statistics (bridge/cantilever counts, longest span, maximum ratios), seam-stitching statistics, support criterion, plan-curvature flags (arching action is ignored, so curved bridges are conservative), critical point count, and unit conventions.", GH_ParamAccess.item);
            p.AddTextParameter("warnings", "warnings", "Aggregated diagnostic messages: missing Pr01 assessment, missing or fallback material properties, isolated fully-unsupported chains, skipped spans, bead-width fallbacks, far-beyond-validity deflection ratios, and an explicit notice when sim_path clips outputs p_pts through critical.", GH_ParamAccess.list);
            p.AddGenericParameter("deflection_kpis", "def_kpis", "Global beam/deformation KPI set: maximum deflection ratios, bend ratio, span statistics, bridge/cantilever counts, and critical/collapse counts for the complete evaluation.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            IGH_Goo pathGoo = null;
            if (!da.GetData(0, ref pathGoo) || !TryExtractPath(pathGoo, out var path) || path == null || !path.HasPoints)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "wsp_path is required and must be a valid WASPer Print Path.");
                return;
            }
            if (!path.HasPrintAssessment)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "wsp_path has no Pr01 assessment data. Connect the path through Pr01_Printability Assessment first. The path is passed through unchanged.");
                da.SetData(0, new WasperPrintPathGoo(path));
                return;
            }

            IGH_Goo matGoo = null;
            da.GetData(1, ref matGoo);
            IGH_Goo propsGoo = null;
            da.GetData(2, ref propsGoo);
            double layerTime = 60.0, directKShape = 1.0, directKFix = 1.0, limit = 0.5, defScale = 1.0, supThr = 0.0, beadWidth = 0.0, critAngle = 45.0, simPath = 1.0;
            global::WASPer_3DP.WasperRobotProgramAdapter robotProgram;
            string simError;
            Vector3d gravity = new Vector3d(0, 0, -1);
            da.GetData(3, ref layerTime);
            da.GetData(4, ref gravity);
            da.GetData(5, ref directKShape);
            da.GetData(6, ref directKFix);
            da.GetData(7, ref limit);
            da.GetData(8, ref defScale);
            da.GetData(9, ref supThr);
            da.GetData(10, ref beadWidth);
            da.GetData(11, ref critAngle);
            if (!global::WASPer_3DP.WasperGcodeTreeUtil.TryGetSimulationInput(
                da, 12, out simPath, out robotProgram, out simError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, simError);
                return;
            }
            bool simFromRobotProgram = robotProgram != null;

            var warnings = new List<string>();
            if (layerTime < 0 || !double.IsFinite(layerTime)) { warnings.Add("layer_time was invalid and has been set to 60 seconds."); layerTime = 60.0; }
            if (!gravity.Unitize()) { warnings.Add("gravity was invalid and has been set to world -Z."); gravity = new Vector3d(0, 0, -1); }
            if (limit <= 0 || !double.IsFinite(limit)) { warnings.Add("limit was invalid and has been set to 0.5."); limit = 0.5; }
            if (defScale <= 0 || !double.IsFinite(defScale)) { warnings.Add("def_scale must be > 0 and has been set to 1.0."); defScale = 1.0; }
            if (!double.IsFinite(supThr) || supThr >= 1.0) { warnings.Add("sup_thr must be < 1.0 and has been set to 0.0."); supThr = 0.0; }
            if (!double.IsFinite(beadWidth) || beadWidth < 0.0) { warnings.Add("bead_w was invalid and automatic widths are used."); beadWidth = 0.0; }
            if (!double.IsFinite(critAngle)) critAngle = 45.0; // legacy display value only; ignored by mechanics
            if (!double.IsFinite(simPath)) { warnings.Add("sim_path was invalid and has been set to 1.0."); simPath = 1.0; }
            simPath = Math.Max(0.0, Math.Min(1.0, simPath));

            var material = ExtractMaterial(matGoo);
            var directProps = Extract3dpProperties(propsGoo);
            var resolved = ResolveProperties(material, directProps, out var propertySources);
            double kShape = ResolveCalibrationProperty(
                resolved, propertySources, "k_shape",
                Params.Input[5].SourceCount > 0, directKShape, 1.0,
                warnings, out string kShapeSource);
            double kFix = ResolveCalibrationProperty(
                resolved, propertySources, "k_fix",
                Params.Input[6].SourceCount > 0, directKFix, 1.0,
                warnings, out string kFixSource);

            bool hasTauInterface = TryPositive(resolved, "tau_interface", out double tauInterface);
            string tauInterfaceSource = hasTauInterface && propertySources.TryGetValue("tau_interface", out var tauSource)
                ? tauSource
                : "not supplied";
            if (resolved.ContainsKey("tau_interface") && !hasTauInterface)
                warnings.Add("tau_interface was supplied but is not positive; Pr04 will use the tau_y0/A_thix fallback when available, otherwise interface-capacity failure is disabled.");

            double aInterface = 0.0;
            string aInterfaceSource = "default 0.0";
            if (resolved.TryGetValue("A_interface", out var suppliedAInterface))
            {
                if (double.IsFinite(suppliedAInterface) && suppliedAInterface >= 0.0)
                {
                    aInterface = suppliedAInterface;
                    if (propertySources.TryGetValue("A_interface", out var source)) aInterfaceSource = source;
                }
                else
                {
                    warnings.Add("A_interface was invalid and has been set to 0.0 Pa/s for reporting.");
                    aInterfaceSource = "fallback 0.0";
                }
            }
            if (!TryPositive(resolved, "E_fresh", out double eFresh))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "A positive E_fresh fresh elastic modulus is required from 3dp_props or wsp_mat. Unlike density there is no safe substitute for stiffness.");
                return;
            }
            double eRate = GetPositiveOrDefault(resolved, "E_rate", 0.0);
            double aThix = GetPositiveOrDefault(resolved, "A_thix", 0.0);
            bool hasTau = TryPositive(resolved, "tau_y0", out double tauY0);
            if (!hasTau) warnings.Add("tau_y0 is not available; the bend_ratio output is empty.");
            else warnings.Add("Pr04 currently uses tau_y(t) as an uncalibrated fresh tensile-strength proxy for same-curve bridge tearing. Add calibrated tensile data before interpreting tears predictively.");
            bool hasInterfaceStrength = hasTauInterface || hasTau;
            double interfaceTau0 = hasTauInterface ? tauInterface : tauY0;
            double interfaceRate = hasTauInterface ? aInterface : aThix;
            string interfaceStrengthSource = hasTauInterface
                ? $"dedicated tau_interface from {tauInterfaceSource}"
                : hasTau
                    ? "uncalibrated tau_y0/A_thix fallback"
                    : "unavailable";
            if (!hasTauInterface && hasTau)
                warnings.Add("tau_interface is not supplied; interface-capacity failure uses tau_y0 + A_thix*t as an explicitly uncalibrated fallback.");
            if (!hasInterfaceStrength)
                warnings.Add("Neither tau_interface nor tau_y0 is available; interface-capacity failure is disabled.");
            double rhoWet;
            if (!TryPositive(resolved, "density_wet", out rhoWet))
            {
                if (TryPositive(resolved, "density", out rhoWet))
                    warnings.Add("density_wet is missing; dry density was used for span self-weight.");
                else
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "A positive density_wet or density property is required.");
                    return;
                }
            }

            bool hasLoad = path.HasFreshRisk && path.Load != null && path.Load.BranchCount > 0;
            if (!hasLoad) warnings.Add("wsp_path carries no Pr03 fresh-risk data; d_loaded is empty. Route the path through Pr03 first to include upper-layer loads.");
            if (path.PrintLoc != null && path.PrintLoc.DataCount != path.Points.DataCount)
                warnings.Add($"Pr01 assessment carries {path.PrintLoc.DataCount} values for {path.Points.DataCount} points. Re-run the current Pr01 build (older builds thinned the packed assessment when red_viz < 1); misaligned data corrupts span detection.");

            // ---- load per-branch data ---------------------------------------
            int branchCount = path.Points.BranchCount;
            int prefixLen = WasperGcodeTreeUtil.CommonPathPrefixLength(path.Points.Paths);
            var bPath = new GH_Path[branchCount];
            var bPts = new List<Point3d>[branchCount];
            var bSupported = new bool[branchCount][];
            var bWidths = new List<double>[branchCount];
            var bHeights = new List<double>[branchCount];
            var bLoads = new List<double>[branchCount];
            var bLayer = new int[branchCount];
            int unsupportedCount = 0;

            for (int b = 0; b < branchCount; b++)
            {
                bPath[b] = path.Points.Paths[b];
                bPts[b] = path.Points.Branch(b);
                var supports = BranchAt(path.SupportPts, bPath[b]);
                var locs = BranchAt(path.PrintLoc, bPath[b]);
                bWidths[b] = BranchAt(path.ContactWidths, bPath[b]);
                bHeights[b] = BranchAt(path.LayerH, bPath[b]);
                bLoads[b] = hasLoad ? BranchAt(path.Load, bPath[b]) : null;
                bLayer[b] = WasperGcodeTreeUtil.LayerFromPath(bPath[b], prefixLen);

                int n = bPts[b].Count;
                bSupported[b] = new bool[n];
                for (int i = 0; i < n; i++)
                {
                    Point3d s = supports != null && supports.Count > 0 ? supports[Math.Min(i, supports.Count - 1)] : Point3d.Unset;
                    // A point is supported only when a support point exists AND its
                    // local printability exceeds sup_thr: Pr01 records a support
                    // point even at beyond-critical overhang (print_loc = 0), but a
                    // bead bearing at critical overhang hangs rather than bears.
                    double loc = ValueAt(locs, i, 1.0);
                    bSupported[b][i] = s.IsValid && loc > supThr;
                    if (!bSupported[b][i]) unsupportedCount++;
                }
            }

            if (robotProgram != null)
            {
                double simulationTolerance = RhinoDoc.ActiveDoc != null
                    ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
                    : RhinoMath.SqrtEpsilon;
                if (global::WASPer_3DP.WasperGcodeTreeUtil.TryGetRobotSimulationCut(
                    robotProgram,
                    bPts,
                    simulationTolerance,
                    out var robotCut,
                    out string mappingError))
                {
                    simPath = robotCut.Progress;
                    if (robotCut.MatchedPointCount < robotCut.TotalPointCount)
                    {
                        AddRuntimeMessage(
                            GH_RuntimeMessageLevel.Warning,
                            $"Robots simulation matched {robotCut.MatchedPointCount}/{robotCut.TotalPointCount} " +
                            "ordered wsp_path points. Progress is reliable through the matched prefix; " +
                            "verify that the same path and point order generated the program.");
                    }
                }
                else
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        mappingError + " Falling back to normalized program time.");
                }
            }

            // ---- per-branch result arrays ------------------------------------
            var bDRatio = new double[branchCount][];
            var bDLoaded = new double[branchCount][];
            var bBend = new double[branchCount][];
            var bClass = new int[branchCount][];
            var bSpanLen = new double[branchCount][];
            var bDefl = new double[branchCount][];
            var bCollapseLocal = new bool[branchCount][];
            var bCollapseGlobal = new bool[branchCount][];
            var bCollapseGen = new int[branchCount][];
            var bTorn = new bool[branchCount][];
            for (int b = 0; b < branchCount; b++)
            {
                int n = bPts[b].Count;
                bDRatio[b] = new double[n];
                bDLoaded[b] = new double[n];
                bBend[b] = new double[n];
                bClass[b] = new int[n];
                bSpanLen[b] = new double[n];
                bDefl[b] = new double[n];
                bCollapseLocal[b] = new bool[n];
                bCollapseGlobal[b] = new bool[n];
                bCollapseGen[b] = Enumerable.Repeat(-1, n).ToArray();
                bTorn[b] = new bool[n];
            }

            // ---- stitch same-layer curves into chains ------------------------
            var layerGroups = new Dictionary<int, List<int>>();
            for (int b = 0; b < branchCount; b++)
            {
                if (bPts[b].Count == 0) continue;
                if (!layerGroups.TryGetValue(bLayer[b], out var list)) layerGroups[bLayer[b]] = list = new List<int>();
                list.Add(b);
            }

            int bridgeCount = 0, cantileverCount = 0, isolatedCount = 0, skippedCount = 0, widthFallbacks = 0, curvedCount = 0, criticalCount = 0;
            int stitchedChains = 0, stitchedCurves = 0;
            double maxDRatio = 0.0, maxDLoaded = 0.0, maxBend = 0.0, maxBridgeTension = 0.0, longestSpan = 0.0;

            foreach (var group in layerGroups.Values)
            {
                var chains = BuildChains(group, bPts, SeamTol);
                foreach (var chain in chains)
                {
                    if (chain.Count > 1) { stitchedChains++; stitchedCurves += chain.Count; }

                    // flatten the chain into (branch, local index) references
                    var map = new List<(int b, int i)>();
                    foreach (int b in chain)
                        for (int i = 0; i < bPts[b].Count; i++) map.Add((b, i));
                    int n = map.Count;
                    if (n == 0) continue;

                    Point3d PtAt(int k) => bPts[map[k].b][map[k].i];
                    bool SupAt(int k) => bSupported[map[k].b][map[k].i];
                    double WAt(int k) => ValueAt(bWidths[map[k].b], map[k].i, 0.0);
                    double HAt(int k) => ValueAt(bHeights[map[k].b], map[k].i, 0.0);
                    double LoadAt(int k) => ValueAt(bLoads[map[k].b], map[k].i, 0.0);

                    int layer = bLayer[chain[0]];
                    double age = Math.Max(0, layer) * layerTime;
                    double eT = eFresh + eRate * age;                    // Pa
                    double tauT = hasTau ? Math.Max(0.0, tauY0 + aThix * age) : 0.0; // Pa

                    int idx = 0;
                    while (idx < n)
                    {
                        if (SupAt(idx)) { idx++; continue; }

                        int start = idx;
                        while (idx < n && !SupAt(idx)) idx++;
                        int end = idx - 1;

                        bool anchorA = start > 0;                        // supported neighbor before the run
                        bool anchorB = end < n - 1;                      // supported neighbor after the run
                        int from = anchorA ? start - 1 : start;
                        int to = anchorB ? end + 1 : end;

                        double lengthMm = 0.0;
                        for (int k = from; k < to; k++) lengthMm += PtAt(k).DistanceTo(PtAt(k + 1));
                        double chordMm = PtAt(from).DistanceTo(PtAt(to));

                        double lh = 0.0, width = 0.0;
                        int lhCount = 0, wCount = 0;
                        for (int k = start; k <= end; k++)
                        {
                            double h = HAt(k); if (double.IsFinite(h) && h > 0) { lh += h; lhCount++; }
                            double w = WAt(k); if (double.IsFinite(w) && w > 0) { width += w; wCount++; }
                        }
                        lh = lhCount > 0 ? lh / lhCount : 0.0;
                        width = wCount > 0 ? width / wCount : 0.0;
                        if (width <= 0) { width = lh; widthFallbacks++; }

                        int cls;
                        if (anchorA && anchorB) { cls = 1; bridgeCount++; }
                        else if (anchorA || anchorB) { cls = 2; cantileverCount++; }
                        else { cls = 2; cantileverCount++; isolatedCount++; }

                        double ratioSelf = 0.0, ratioLoaded = 0.0, bendRatio = 0.0, tensileRatio = 0.0;
                        if (lh <= RhinoMath.ZeroTolerance || lengthMm <= RhinoMath.ZeroTolerance || eT <= 0)
                        {
                            skippedCount++;
                        }
                        else
                        {
                            // SI section properties from mm inputs
                            double area = kShape * width * lh * 1e-6;                 // m2
                            double inertia = kShape * width * lh * lh * lh / 12.0 * 1e-12; // m4
                            double lM = lengthMm * 1e-3;                              // m
                            double qSelf = rhoWet * G_CONST * area;                   // N/m
                            double momCoef = cls == 1 ? 1.0 / 8.0 : 1.0 / 2.0;

                            BridgeResponse selfResponse = cls == 1
                                ? SolveBridgeResponse(qSelf, lM, eT * inertia, eT * area, kFix)
                                : new BridgeResponse(1.0 / 8.0 * qSelf * Math.Pow(lM, 4) / (eT * inertia) * kFix, 0.0);
                            double delta = selfResponse.Delta; // m
                            ratioSelf = delta / (lh * 1e-3);
                            maxBridgeTension = Math.Max(maxBridgeTension, selfResponse.Tension);
                            if (cls == 1 && hasTau && tauT > RhinoMath.ZeroTolerance)
                                tensileRatio = selfResponse.Tension / Math.Max(tauT * area, 1e-12);

                            double qGoverning = qSelf;
                            if (hasLoad)
                            {
                                double totalLoad = 0.0;                               // N over the run
                                for (int k = start; k <= end; k++) totalLoad += LoadAt(k);
                                double qLoaded = Math.Max(qSelf, totalLoad / lM);     // Pr03 load includes self-weight
                                BridgeResponse loadedResponse = cls == 1
                                    ? SolveBridgeResponse(qLoaded, lM, eT * inertia, eT * area, kFix)
                                    : new BridgeResponse(1.0 / 8.0 * qLoaded * Math.Pow(lM, 4) / (eT * inertia) * kFix, 0.0);
                                double deltaLoaded = loadedResponse.Delta;
                                ratioLoaded = deltaLoaded / (lh * 1e-3);
                                qGoverning = qLoaded;
                                maxBridgeTension = Math.Max(maxBridgeTension, loadedResponse.Tension);
                                if (cls == 1 && hasTau && tauT > RhinoMath.ZeroTolerance)
                                    tensileRatio = Math.Max(tensileRatio, loadedResponse.Tension / Math.Max(tauT * area, 1e-12));
                            }

                            if (hasTau && tauT > RhinoMath.ZeroTolerance)
                            {
                                double moment = momCoef * qGoverning * lM * lM;       // N.m
                                double sigma = moment * (lh * 1e-3 / 2.0) / inertia;  // Pa
                                bendRatio = sigma / (SQRT3 * tauT);
                            }

                            if (cls == 1 && lengthMm > RhinoMath.ZeroTolerance && chordMm / lengthMm < CurvedSpanChordRatio) curvedCount++;
                            longestSpan = Math.Max(longestSpan, lengthMm);
                            maxDRatio = Math.Max(maxDRatio, ratioSelf);
                            maxDLoaded = Math.Max(maxDLoaded, ratioLoaded);
                            maxBend = Math.Max(maxBend, bendRatio);
                        }

                        // per-point deflection from the analytical span shape.
                        // Bridge (pinned-pinned, UDL): f(xi) = 3.2*(xi - 2*xi^3 + xi^4), max 1 at mid-span.
                        // Cantilever (fixed at anchor, UDL): f(xi) = xi^2*(6 - 4*xi + xi^2)/3, max 1 at free end.
                        // The VISUALIZED sag is clamped at one layer height: delta scales with
                        // L^4, so failed spans produce ratios in the hundreds or thousands and
                        // meaningless kilometre-scale preview geometry. Beyond ~1xLH the bead
                        // has failed/detached anyway; d_ratio/d_loaded keep the raw values.
                        double governingRatio = ratioLoaded > 0 ? ratioLoaded : ratioSelf;
                        bool localCollapse = governingRatio >= 1.0 || (hasTau && (bendRatio >= 1.0 || tensileRatio >= 1.0));
                        double deltaSpanMm = Math.Min(governingRatio, 1.0) * lh;
                        double s = anchorA ? PtAt(from).DistanceTo(PtAt(start)) : 0.0;
                        bool fixedAtFrom = anchorA || !anchorB; // cantilever anchored at B measures xi from B
                        for (int k = start; k <= end; k++)
                        {
                            if (k > start) s += PtAt(k - 1).DistanceTo(PtAt(k));
                            double defl = 0.0;
                            if (deltaSpanMm > 0 && lengthMm > RhinoMath.ZeroTolerance)
                            {
                                double xi = Math.Max(0.0, Math.Min(1.0, s / lengthMm));
                                if (cls == 2 && !fixedAtFrom) xi = 1.0 - xi;
                                double shape = cls == 1
                                    ? 3.2 * (xi - 2.0 * xi * xi * xi + xi * xi * xi * xi)
                                    : xi * xi * (6.0 - 4.0 * xi + xi * xi) / 3.0;
                                defl = deltaSpanMm * Math.Max(0.0, Math.Min(1.0, shape));
                            }

                            var (bb, ii) = map[k];
                            bDRatio[bb][ii] = ratioSelf;
                            bDLoaded[bb][ii] = ratioLoaded;
                            bBend[bb][ii] = bendRatio;
                            bClass[bb][ii] = cls;
                            bSpanLen[bb][ii] = lengthMm;
                            bDefl[bb][ii] = defl;
                            bCollapseLocal[bb][ii] = localCollapse;
                            bCollapseGlobal[bb][ii] = localCollapse;
                            bCollapseGen[bb][ii] = localCollapse ? 0 : -1;
                        }
                    }
                }
            }

            if (unsupportedCount == 0) warnings.Add("No unsupported points were found — every point has Pr01 support, so d_ratio is 0 everywhere. If you expected bridges or cantilevers, check the Pr01 assessment (support search tolerance, tree structure) with Gc15/summary diagnostics.");
            if (isolatedCount > 0) warnings.Add($"{isolatedCount} fully unsupported chain run(s) had no supported anchor on either side; they were conservatively classified as cantilevers over their full length.");
            if (skippedCount > 0) warnings.Add($"{skippedCount} span(s) had invalid layer height, length, or stiffness and output 0.");
            if (widthFallbacks > 0) warnings.Add($"{widthFallbacks} span(s) had no positive Pr01 contact width; layer height was used as the bead-width estimate (conservative section).");

            bool[][] ScratchBools() => bPts.Select(branch => new bool[branch.Count]).ToArray();
            int[][] ScratchGenerations() => bPts.Select(branch => Enumerable.Repeat(-1, branch.Count).ToArray()).ToArray();

            // Predictor: simulate the ordered deposition once under bead self-weight and
            // retain the actual support ancestry. This converts each deposited segment's
            // own weight into the cumulative load carried by the segments below it.
            SimulateSameCurveChainDeposition(
                layerGroups, bPts, bHeights, bWidths,
                beadWidth, gravity,
                ScratchBools(), ScratchBools(), ScratchGenerations(),
                ScratchBools(), simPath,
                eFresh, eRate, kShape, kFix,
                hasTau, tauY0, aThix, layerTime, rhoWet,
                null, true,
                new InterfaceSettings(hasInterfaceStrength, interfaceTau0, interfaceRate),
                null, true,
                out List<double>[] internalLoads,
                out InterfaceFailureSchedule interfaceSchedule,
                out List<double>[] bInterfaceRatios,
                out ChainSimulationStats predictorStats);

            double internalLoadAudit = internalLoads.Sum(branch => branch?.Sum(value => Math.Max(0.0, value)) ?? 0.0);
            double pr03LoadAudit = hasLoad
                ? bLoads.Sum(branch => branch?.Sum(value => Math.Max(0.0, value)) ?? 0.0)
                : 0.0;

            // When Pr03 loads are available they remain authoritative. Otherwise Pr04's
            // fabrication-order support graph supplies an internal fresh-load estimate.
            var simulationLoads = new List<double>[branchCount];
            for (int b = 0; b < branchCount; b++)
            {
                simulationLoads[b] = new List<double>(bPts[b].Count);
                for (int i = 0; i < bPts[b].Count; i++)
                {
                    double internalLoad = ValueAt(internalLoads[b], i, 0.0);
                    double pr03Load = hasLoad ? ValueAt(bLoads[b], i, 0.0) : 0.0;
                    simulationLoads[b].Add(hasLoad ? pr03Load : internalLoad);
                }
            }

            // Corrector: repeat the same fabrication sequence with cumulative carried
            // loads. Lower cantilevers can now fail when later layers increase demand;
            // their displaced geometry then changes support for the layers above.
            List<Point3d>[] bGlobalPts = SimulateSameCurveChainDeposition(
                layerGroups, bPts, bHeights, bWidths,
                beadWidth, gravity,
                bCollapseLocal, bCollapseGlobal, bCollapseGen,
                bTorn, simPath,
                eFresh, eRate, kShape, kFix,
                hasTau, tauY0, aThix, layerTime, rhoWet,
                simulationLoads, false,
                new InterfaceSettings(hasInterfaceStrength, interfaceTau0, interfaceRate),
                interfaceSchedule, false,
                out _,
                out _,
                out _,
                out ChainSimulationStats chainStats);

            // ---- outputs -----------------------------------------------------
            var dRatioTree = new DataTree<double>();
            var dLoadedTree = hasLoad ? new DataTree<double>() : null;
            var bendTree = hasTau ? new DataTree<double>() : null;
            var classTree = new DataTree<int>();
            var spanLenTree = new DataTree<double>();
            var criticalTree = new DataTree<bool>();
            var defPtsTree = new DataTree<Point3d>();
            var defVectTree = new DataTree<Vector3d>();
            var collapseLocalTree = new DataTree<bool>();
            var collapseGlobalTree = new DataTree<bool>();
            var collapseGenTree = new DataTree<int>();
            var cascadeTree = new DataTree<bool>();
            var tornTree = new DataTree<bool>();
            var interfaceRatioTree = new DataTree<double>();
            var failureFlagsTree = new DataTree<int>();
            int localCollapseCount = 0, globalCollapseCount = 0, cascadeCollapseCount = 0, tornCount = 0;

            for (int b = 0; b < branchCount; b++)
            {
                int n = bPts[b].Count;
                List<int> existingFlags = BranchAt(path.FailureFlags, bPath[b]);
                for (int i = 0; i < n; i++)
                {
                    dRatioTree.Add(bDRatio[b][i], bPath[b]);
                    dLoadedTree?.Add(bDLoaded[b][i], bPath[b]);
                    bendTree?.Add(bBend[b][i], bPath[b]);
                    classTree.Add(bClass[b][i], bPath[b]);
                    spanLenTree.Add(bSpanLen[b][i], bPath[b]);

                    Vector3d physicalDisplacement = bGlobalPts[b][i] - bPts[b][i];
                    Vector3d displacement = physicalDisplacement * defScale;
                    Point3d moved = bPts[b][i] + displacement;
                    // floor clamp: the print bed at z = 0 is the lowest a point can
                    // physically go — shorten the displacement so the deflected
                    // point never drops below it (works for any gravity direction).
                    if (moved.Z < 0.0 && displacement.Z < -RhinoMath.ZeroTolerance)
                    {
                        double t = Math.Max(0.0, Math.Min(1.0, -bPts[b][i].Z / displacement.Z));
                        displacement *= t;
                        moved = bPts[b][i] + displacement;
                    }
                    defPtsTree.Add(moved, bPath[b]);
                    defVectTree.Add(displacement, bPath[b]);

                    bool collapseLocal = bCollapseLocal[b][i];
                    bool collapseGlobal = bCollapseGlobal[b][i];
                    bool cascade = collapseGlobal && !collapseLocal;
                    collapseLocalTree.Add(collapseLocal, bPath[b]);
                    collapseGlobalTree.Add(collapseGlobal, bPath[b]);
                    collapseGenTree.Add(bCollapseGen[b][i], bPath[b]);
                    cascadeTree.Add(cascade, bPath[b]);
                    tornTree.Add(bTorn[b][i], bPath[b]);
                    double interfaceRatio = ValueAt(bInterfaceRatios[b], i, 0.0);
                    bool interfaceFailed = interfaceSchedule?.IsFailedPoint(b, i) == true;
                    int failureFlags = existingFlags != null && existingFlags.Count > 0
                        ? existingFlags[Math.Min(i, existingFlags.Count - 1)]
                        : 0;
                    if (interfaceFailed) failureFlags |= 8;
                    interfaceRatioTree.Add(interfaceRatio, bPath[b]);
                    failureFlagsTree.Add(failureFlags, bPath[b]);
                    if (collapseLocal) localCollapseCount++;
                    if (collapseGlobal) globalCollapseCount++;
                    if (cascade) cascadeCollapseCount++;
                    if (bTorn[b][i]) tornCount++;

                    double governing = hasLoad && bDLoaded[b][i] > 0 ? bDLoaded[b][i] : bDRatio[b][i];
                    bool isCritical = collapseGlobal || interfaceFailed || governing >= limit || (hasTau && bBend[b][i] >= 1.0);
                    if (isCritical) criticalCount++;
                    criticalTree.Add(isCritical, bPath[b]);
                }
            }

            double govMaxRatio = hasLoad ? Math.Max(maxDRatio, maxDLoaded) : maxDRatio;
            if (govMaxRatio > 1.0)
                warnings.Add($"Governing deflection ratios reach {govMaxRatio:0.#} — far beyond the model's validity (delta scales with L^4). Treat those spans as FAILED regions, not as magnitudes; def_pts sag is clamped at one layer height there. Long unprintable runs (see span_len) and tile-border cut-offs classified as cantilevers are the usual drivers.");

            var enriched = new WasperPrintPath(
                path.Points, path.PtPlanes, path.Flows, path.LayerH, path.PrintSpeed,
                path.PrintLoc, path.PrintGlob, path.SupportPts, path.SupportVects,
                path.Angles, path.ContactWidths,
                path.RiskMaterial, path.RiskComb, path.Load, path.Capacity,
                path.NozzleDiam,
                dRatioTree, dLoadedTree, bendTree, classTree, spanLenTree,
                collapseGlobalTree, cascadeTree, collapseGenTree,
                path.LayerW, path.LayerWf, path.PrintVol,
                tornTree, interfaceRatioTree, path.OverturnRatio, failureFlagsTree,
                pathRoles: path.PathRoles,
                layerPlanes: path.LayerPlanes);

            int[] depositedCounts = PrintedCounts(layerGroups, bPts, simPath);
            DataTree<Point3d> clippedOriginalPoints = ClipTree(path.Points, bPath, depositedCounts);
            DataTree<Point3d> clippedDefPoints = ClipTree(defPtsTree, bPath, depositedCounts);
            if (clippedDefPoints.BranchCount == 0 && bPath.Length > 0)
            {
                clippedOriginalPoints.EnsurePath(bPath[0]);
                clippedDefPoints.EnsurePath(bPath[0]);
            }
            DataTree<Vector3d> clippedDefVectors = ClipTree(defVectTree, bPath, depositedCounts);
            DataTree<bool> clippedCollapseLocal = ClipTree(collapseLocalTree, bPath, depositedCounts);
            DataTree<bool> clippedCollapseGlobal = ClipTree(collapseGlobalTree, bPath, depositedCounts);
            DataTree<int> clippedCollapseGeneration = ClipTree(collapseGenTree, bPath, depositedCounts);
            DataTree<bool> clippedTorn = ClipTree(tornTree, bPath, depositedCounts);
            DataTree<double> clippedDRatio = ClipTree(dRatioTree, bPath, depositedCounts);
            DataTree<double> clippedDLoaded = ClipTree(dLoadedTree, bPath, depositedCounts);
            DataTree<double> clippedBend = ClipTree(bendTree, bPath, depositedCounts);
            DataTree<int> clippedClass = ClipTree(classTree, bPath, depositedCounts);
            DataTree<double> clippedSpanLength = ClipTree(spanLenTree, bPath, depositedCounts);
            DataTree<bool> clippedCritical = ClipTree(criticalTree, bPath, depositedCounts);
            DataTree<bool> clippedCascade = ClipTree(cascadeTree, bPath, depositedCounts);
            DataTree<Plane> clippedOriginalPlanes = ClipTree(path.PtPlanes, bPath, depositedCounts);
            DataTree<Plane> deflectedPlanes = BuildDeflectedPlanes(
                clippedDefPoints, clippedOriginalPlanes, clippedTorn,
                out int planeFallbackCount, out int planeResetCount);

            var deflected = new WasperPrintPath(
                clippedDefPoints,
                deflectedPlanes,
                ClipTree(path.Flows, bPath, depositedCounts),
                ClipTree(path.LayerH, bPath, depositedCounts),
                ClipTree(path.PrintSpeed, bPath, depositedCounts),
                ClipTree(path.PrintLoc, bPath, depositedCounts),
                ClipTree(path.PrintGlob, bPath, depositedCounts),
                ClipTree(path.SupportPts, bPath, depositedCounts),
                ClipTree(path.SupportVects, bPath, depositedCounts),
                ClipTree(path.Angles, bPath, depositedCounts),
                ClipTree(path.ContactWidths, bPath, depositedCounts),
                ClipTree(path.RiskMaterial, bPath, depositedCounts),
                ClipTree(path.RiskComb, bPath, depositedCounts),
                ClipTree(path.Load, bPath, depositedCounts),
                ClipTree(path.Capacity, bPath, depositedCounts),
                path.NozzleDiam,
                clippedDRatio,
                clippedDLoaded,
                clippedBend,
                clippedClass,
                clippedSpanLength,
                clippedCollapseGlobal,
                clippedCascade,
                clippedCollapseGeneration,
                ClipTree(path.LayerW, bPath, depositedCounts),
                ClipTree(path.LayerWf, bPath, depositedCounts),
                ClipTree(path.PrintVol, bPath, depositedCounts),
                clippedTorn,
                ClipTree(interfaceRatioTree, bPath, depositedCounts),
                ClipTree(path.OverturnRatio, bPath, depositedCounts),
                ClipTree(failureFlagsTree, bPath, depositedCounts),
                pathRoles: WasperGcodeTreeUtil.FilterPathRoles(
                    path.PathRoles,
                    clippedDefPoints.Paths),
                layerPlanes: WasperGcodeTreeUtil.FilterLayerPlanes(
                    path.LayerPlanes,
                    clippedDefPoints.Paths,
                    WasperGcodeTreeUtil.CommonPathPrefixLength(
                        path.Points.Paths.ToList())));

            string summary = string.Format(CultureInfo.InvariantCulture,
                "wsp_Pr04_Beam Deflection Proxy\npoints: {0}\nE_fresh [Pa]: {1:R}\nE_rate [Pa/s]: {2:R}\ntau_y0 [Pa]: {3}\nA_thix [Pa/s]: {4:R}\nrho_wet [kg/m3]: {5:R}\nlayer_time [s]: {6:R}\nk_shape [-]: {7:R} | k_fix [-]: {8:R}\nseam stitching: {9} chain(s) from {10} seam-connected curve(s), tol {11:R} mm\nsupport criterion: support point valid AND print_loc > {24:R} (unsupported points: {25})\nbridges: {12} | cantilevers: {13} (isolated: {14})\nlongest span [mm]: {15:0.##}\nmax d_ratio [-]: {16:0.###}\nmax d_loaded [-]: {17}\nmax bend_ratio [-]: {18}\ncurved bridges (chord/length < {19:0.##}): {20} — arching action ignored, straight-beam result is conservative\ncritical points (limit {21:R}): {22}\ndef_scale [-]: {23:R} (def_pts/def_vect only; governing load case, analytical span shapes, sag clamped at 1x layer_h before scaling, floor-clamped at z = 0)\nunits: path mm -> SI internally; ratios dimensionless\nnote: comparative ranking proxy; def_wsp_path is visualization-only and must not be used as fabrication input; small-deflection theory at low fresh stiffness; calibrate k_shape/k_fix with bridging tests",
                dRatioTree.DataCount, eFresh, eRate,
                hasTau ? tauY0.ToString("R", CultureInfo.InvariantCulture) : "not supplied",
                aThix, rhoWet, layerTime, kShape, kFix,
                stitchedChains, stitchedCurves, SeamTol,
                bridgeCount, cantileverCount, isolatedCount, longestSpan, maxDRatio,
                hasLoad ? maxDLoaded.ToString("0.###", CultureInfo.InvariantCulture) : "n/a",
                hasTau ? maxBend.ToString("0.###", CultureInfo.InvariantCulture) : "n/a",
                CurvedSpanChordRatio, curvedCount, limit, criticalCount, defScale,
                supThr, unsupportedCount);
            summary += string.Format(CultureInfo.InvariantCulture,
                "\nlocal collapse points: {0}\nglobal collapse points: {1}\npropagated/cascade points: {2}\ntorn segments: {3}\nsim_path [-]: {4:R} (fabrication-order cutoff; analysis outputs always describe the full print)\nordered-chain deposition: {5} physical curve chain(s), {6} unsupported start(s), {7} bridge run(s), {8} trailing cantilever run(s), {9} completely free curve(s)\nmax bridge tension [N]: {10:0.######} | active tau_y-derived tensile proxy\ncorrector solve time [ms]: {11:0.###}\nload predictor time [ms]: {13:0.###} | loaded runs rechecked: {14} | max carried/self load factor: {15:0.###}\ncollapse geometry: two fabrication-order passes; the predictor records as-built support ancestry and cumulative fresh weight, then the corrector rechecks local bridges/cantilevers and deposits their corrected geometry for upper layers\nsegment lengths: unconstrained and recomputed from displaced points\nlegacy crit_angle input: {12:R} deg (retained for wire compatibility; ignored by the physical calculation)",
                localCollapseCount, globalCollapseCount, cascadeCollapseCount,
                tornCount, simPath,
                chainStats.Curves, chainStats.UnsupportedStarts, chainStats.BridgeRuns,
                chainStats.CantileverRuns, chainStats.FreeCurves,
                Math.Max(maxBridgeTension, chainStats.MaxBridgeTension), chainStats.ElapsedMs,
                critAngle, predictorStats.ElapsedMs, chainStats.LoadedRuns, chainStats.MaxLoadFactor);
            summary += string.Format(CultureInfo.InvariantCulture,
                "\ndeposited visualization prefix: {0}/{1} points ({2:P1}); wsp_path remains complete; outputs p_pts through critical follow this prefix",
                clippedDefPoints.DataCount, dRatioTree.DataCount,
                dRatioTree.DataCount > 0 ? (double)clippedDefPoints.DataCount / dRatioTree.DataCount : 0.0);
            summary += string.Format(CultureInfo.InvariantCulture,
                "\ndef_wsp_path planes: rotation-minimizing frames on the deflected centerline; torn-edge resets: {0}; fallback frames: {1}; bead profile dimensions unchanged",
                planeResetCount, planeFallbackCount);
            summary += string.Format(CultureInfo.InvariantCulture,
                "\nPhase 3 interface-capacity history: {0} patch(es), {1} failed patch(es), {2} failed point(s)\nmax interface_ratio [-]: {3:0.###}\ninterface strength source: {4}\ntau_interface0 [Pa]: {5}\ninterface structuration [Pa/s]: {6:R} (source: {7})\ninterface load source: Pr04 layer-resolved internal support DAG; Pr03 final loads are not used to schedule failure\nfinal-load audit sums [N]: internal {12:0.######} | Pr03 {13}\ninterface timing [ms]: load graph {14:0.###} | patch evaluation {15:0.###}\ncontact fallbacks: {8} width, {9} zero-tributary-area\ndelayed-failure replay: {10} same-curve run(s), {11} moved point(s) at their recorded failure layer\nconsequence: failed fresh contacts lose vertical support and are re-settled with printed same-curve neighbors acting as anchors; corrected segments replace stale contact geometry for later layers; no sideways sliding model",
                predictorStats.InterfacePatches,
                interfaceSchedule?.FailedPatchCount ?? 0,
                interfaceSchedule?.FailedPointCount ?? 0,
                predictorStats.MaxInterfaceRatio,
                interfaceStrengthSource,
                hasInterfaceStrength ? interfaceTau0.ToString("R", CultureInfo.InvariantCulture) : "unavailable",
                interfaceRate,
                hasTauInterface ? aInterfaceSource : hasTau ? "A_thix fallback" : "unavailable",
                predictorStats.InterfaceWidthFallbacks,
                predictorStats.InterfaceAreaFallbacks,
                chainStats.InterfaceReplayRuns,
                chainStats.InterfaceReplayPoints,
                internalLoadAudit,
                hasLoad ? pr03LoadAudit.ToString("0.######", CultureInfo.InvariantCulture) : "n/a",
                predictorStats.InterfaceLoadGraphMs,
                predictorStats.InterfaceEvaluationMs);
            summary += string.Format(CultureInfo.InvariantCulture,
                "\ncalibration resolution: k_shape source = {0}; k_fix source = {1}\ndedicated tau_interface [Pa]: {2} (source: {3})\nA_interface [Pa/s]: {4:R} (source: {5})",
                kShapeSource, kFixSource,
                hasTauInterface ? tauInterface.ToString("R", CultureInfo.InvariantCulture) : "not supplied",
                tauInterfaceSource, aInterface, aInterfaceSource);
            if ((interfaceSchedule?.FailedPatchCount ?? 0) > 0)
                warnings.Add($"Interface capacity failed in {interfaceSchedule.FailedPatchCount} connected patch(es), affecting {interfaceSchedule.FailedPointCount} point(s). These contacts were removed in the bounded corrector and may produce further collapse.");
            if (planeFallbackCount > 0)
                warnings.Add($"def_wsp_path required {planeFallbackCount} fallback frame(s) where a deflected tangent or transported height direction was degenerate.");

            if (simPath < 1.0 - 1e-9)
            {
                string prefixWarning = string.Format(CultureInfo.InvariantCulture,
                    "sim_path is {0:P1}: visualization outputs p_pts through critical contain only the deposited prefix ({1}/{2} points). wsp_path remains complete.",
                    Math.Max(0.0, Math.Min(1.0, simPath)), clippedDefPoints.DataCount, dRatioTree.DataCount);
                warnings.Add(prefixWarning);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, prefixWarning);
            }

            da.SetData(0, new WasperPrintPathGoo(enriched));
            da.SetData(1, new WasperPrintPathGoo(deflected));
            da.SetDataTree(2, clippedOriginalPoints);
            da.SetDataTree(3, clippedDefPoints);
            da.SetDataTree(4, clippedDefVectors);
            da.SetDataTree(5, clippedCollapseLocal);
            da.SetDataTree(6, clippedCollapseGlobal);
            da.SetDataTree(7, clippedCollapseGeneration);
            da.SetDataTree(8, clippedTorn);
            da.SetDataTree(9, clippedDRatio);
            if (clippedDLoaded != null) da.SetDataTree(10, clippedDLoaded);
            if (clippedBend != null) da.SetDataTree(11, clippedBend);
            da.SetDataTree(12, clippedClass);
            da.SetDataTree(13, clippedSpanLength);
            da.SetDataTree(14, clippedCritical);
            da.SetData(15, summary);
            da.SetDataList(16, warnings);
            var deflectionKpis = new WasperKpiSet { SourceComponent = Name, SourceVersion = Message };
            deflectionKpis.Add(WasperKpi.Scalar("deformation.max_deflection_ratio", "Maximum deflection ratio", "Printability", "-", maxDRatio, "Maximum self-weight deflection ratio delta/layer_h.", Name));
            if (hasLoad)
                deflectionKpis.Add(WasperKpi.Scalar("deformation.max_loaded_deflection_ratio", "Maximum loaded deflection ratio", "Printability", "-", maxDLoaded, "Maximum deflection ratio using accumulated Pr03 load.", Name));
            deflectionKpis.Add(WasperKpi.Scalar("deformation.max_bend_ratio", "Maximum bend ratio", "Printability", "-", maxBend, "Maximum bending stress ratio across evaluated spans.", Name));
            deflectionKpis.Add(WasperKpi.Scalar("deformation.longest_span", "Longest unsupported span", "Printability", "mm", longestSpan, "Longest detected bridge or cantilever span.", Name));
            deflectionKpis.Add(WasperKpi.Scalar("deformation.bridge_count", "Bridge count", "Printability", "count", bridgeCount, "Number of evaluated bridge spans.", Name));
            deflectionKpis.Add(WasperKpi.Scalar("deformation.cantilever_count", "Cantilever count", "Printability", "count", cantileverCount, "Number of evaluated cantilever spans.", Name));
            deflectionKpis.Add(WasperKpi.Scalar("deformation.critical_count", "Critical point count", "Printability", "count", criticalCount, "Number of points exceeding deformation, bending, collapse, or interface criteria.", Name));
            da.SetData(17, new WasperKpiSetGoo(deflectionKpis, this));

            Message = $"{(criticalCount > 0 ? "critical" : "stable")} | {clippedDefPoints.DataCount}/{dRatioTree.DataCount} pts" +
                (simFromRobotProgram ? " | robot" : "");
        }

        private static List<Point3d>[] SimulateSameCurveChainDeposition(
            Dictionary<int, List<int>> layerGroups,
            List<Point3d>[] nominalPoints,
            List<double>[] heights,
            List<double>[] contactWidths,
            double suppliedBeadWidth,
            Vector3d gravity,
            bool[][] localCollapse,
            bool[][] globalCollapse,
            int[][] collapseGeneration,
            bool[][] torn,
            double simPath,
            double eFresh,
            double eRate,
            double kShape,
            double kFix,
            bool hasTau,
            double tauY0,
            double aThix,
            double layerTime,
            double rhoWet,
            List<double>[] simulationLoads,
            bool collectLoads,
            InterfaceSettings interfaceSettings,
            InterfaceFailureSchedule rejectionSchedule,
            bool collectInterface,
            out List<double>[] accumulatedLoads,
            out InterfaceFailureSchedule generatedSchedule,
            out List<double>[] interfaceRatios,
            out ChainSimulationStats stats)
        {
            var watch = Stopwatch.StartNew();
            var current = nominalPoints.Select(branch => new List<Point3d>(branch)).ToArray();
            var beadWidths = new double[nominalPoints.Length][];
            var layerHeights = new double[nominalPoints.Length][];
            for (int b = 0; b < nominalPoints.Length; b++)
            {
                beadWidths[b] = new double[nominalPoints[b].Count];
                layerHeights[b] = new double[nominalPoints[b].Count];
                for (int i = 0; i < nominalPoints[b].Count; i++)
                {
                    double h = Math.Max(0.0, ValueAt(heights[b], i, 0.0));
                    double wc = Math.Max(0.0, ValueAt(contactWidths[b], i, 0.0));
                    layerHeights[b][i] = h;
                    beadWidths[b][i] = suppliedBeadWidth > RhinoMath.ZeroTolerance
                        ? suppliedBeadWidth
                        : Math.Max(2.0 * h, wc);
                }
            }

            int totalPoints = layerGroups.OrderBy(pair => pair.Key)
                .SelectMany(pair => pair.Value)
                .Sum(b => nominalPoints[b].Count);
            int printCutoff = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, simPath)) * totalPoints);
            var printed = new bool[nominalPoints.Length][];
            for (int b = 0; b < nominalPoints.Length; b++) printed[b] = new bool[nominalPoints[b].Count];
            int fabricationIndex = 0;
            var branchLayers = Enumerable.Repeat(-1, nominalPoints.Length).ToArray();
            foreach (var pair in layerGroups.OrderBy(pair => pair.Key))
                foreach (int b in pair.Value)
                {
                    branchLayers[b] = pair.Key;
                    for (int i = 0; i < nominalPoints[b].Count; i++)
                        printed[b][i] = fabricationIndex++ < printCutoff;
                }

            Vector3d up = -gravity;
            double docTol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
            var deposited = new DepositedGeometry();
            var simStats = new ChainSimulationStats();
            var supportParents = new Dictionary<(int b, int i), int>();
            var supportPatches = new Dictionary<(int b, int i), int>();
            generatedSchedule = collectInterface && interfaceSettings.Enabled
                ? new InterfaceFailureSchedule()
                : rejectionSchedule;
            interfaceRatios = nominalPoints
                .Select(branch => Enumerable.Repeat(0.0, branch.Count).ToList())
                .ToArray();
            var interfaceHistory = collectInterface && interfaceSettings.Enabled
                ? new InterfaceHistory(generatedSchedule, interfaceRatios)
                : null;

            foreach (var layer in layerGroups.OrderBy(pair => pair.Key))
            {
                double age = Math.Max(0, layer.Key) * layerTime;
                double eT = Math.Max(1e-9, eFresh + eRate * age);
                double tauT = hasTau ? Math.Max(0.0, tauY0 + aThix * age) : 0.0;
                if (rejectionSchedule != null)
                    ApplyScheduledInterfaceFailures(
                        rejectionSchedule, layer.Key,
                        nominalPoints, current, beadWidths, layerHeights,
                        branchLayers, printed, torn, gravity, up,
                        deposited, globalCollapse, collapseGeneration,
                        simStats, docTol);
                var chains = BuildChains(layer.Value, nominalPoints, SeamTol);

                foreach (var chain in chains)
                {
                    var map = new List<(int b, int i)>();
                    bool reachedCutoff = false;
                    foreach (int b in chain)
                    {
                        for (int i = 0; i < nominalPoints[b].Count; i++)
                        {
                            if (!printed[b][i]) { reachedCutoff = true; break; }
                            map.Add((b, i));
                        }
                        if (reachedCutoff) break;
                    }
                    if (map.Count == 0) continue;
                    simStats.Curves++;

                    var direct = new bool[map.Count];
                    var directHits = new ContactHit[map.Count];

                    ContactHit ContactAt(int k, Point3d queryPoint, bool currentGeometry)
                    {
                        var reference = map[k];
                        double h = layerHeights[reference.b][reference.i];
                        double contactTol = Math.Max(docTol * 2.0, h * 0.05);
                        Func<DepositedSegment, bool> reject = rejectionSchedule == null
                            ? null
                            : segment => rejectionSchedule.IsRejected(
                                reference.b, reference.i, layer.Key, segment);
                        return FindContactBelow(
                            queryPoint, beadWidths[reference.b][reference.i], h,
                            deposited, currentGeometry, up, contactTol, reject);
                    }

                    for (int k = 0; k < map.Count; k++)
                    {
                        var r = map[k];
                        ContactHit hit = ContactAt(k, current[r.b][r.i], true);
                        directHits[k] = hit;
                        direct[k] = hit.IsContact;
                        if (hit.IsContact && hit.SegmentId >= 0)
                            supportParents[(r.b, r.i)] = hit.SegmentId;
                        if (direct[k]) simStats.DirectSupportPoints++;
                    }
                    interfaceHistory?.RegisterContacts(
                        map, current, beadWidths, directHits, layer.Key,
                        deposited, supportPatches, simStats, docTol);
                    if (!direct[0]) simStats.UnsupportedStarts++;

                    double SegmentLength(int a, int b)
                    {
                        var ra = map[a];
                        var rb = map[b];
                        return current[ra.b][ra.i].DistanceTo(current[rb.b][rb.i]);
                    }

                    (double width, double height) RunSection(int start, int end)
                    {
                        double width = 0.0, height = 0.0;
                        int count = 0;
                        for (int k = start; k <= end; k++)
                        {
                            var r = map[k];
                            if (beadWidths[r.b][r.i] <= docTol || layerHeights[r.b][r.i] <= docTol) continue;
                            width += beadWidths[r.b][r.i];
                            height += layerHeights[r.b][r.i];
                            count++;
                        }
                        return count > 0 ? (width / count, height / count) : (0.0, 0.0);
                    }

                    double RunLineLoad(int start, int end, double selfWeight, double lengthMm)
                    {
                        if (simulationLoads == null || lengthMm <= docTol) return selfWeight;
                        double totalLoad = 0.0;
                        for (int k = start; k <= end; k++)
                        {
                            var r = map[k];
                            totalLoad += Math.Max(0.0, ValueAt(simulationLoads[r.b], r.i, 0.0));
                        }
                        double loaded = totalLoad / (lengthMm * 1e-3);
                        double governing = Math.Max(selfWeight, loaded);
                        if (governing > selfWeight * (1.0 + 1e-9)) simStats.LoadedRuns++;
                        if (selfWeight > 1e-12)
                            simStats.MaxLoadFactor = Math.Max(simStats.MaxLoadFactor, governing / selfWeight);
                        return governing;
                    }

                    int FailureGeneration(int k)
                    {
                        var r = map[k];
                        if (localCollapse[r.b][r.i]) return 0;
                        ContactHit nominalHit = ContactAt(k, nominalPoints[r.b][r.i], false);
                        ContactHit currentHit = ContactAt(k, current[r.b][r.i], true);
                        return nominalHit.IsContact && !currentHit.IsContact
                            ? Math.Max(0, nominalHit.Generation + 1)
                            : 0;
                    }

                    void ApplyDrop(int k, double requestedDrop, bool failed)
                    {
                        if (!double.IsFinite(requestedDrop) || requestedDrop <= docTol) return;
                        var r = map[k];
                        double h = layerHeights[r.b][r.i];
                        double contactTol = Math.Max(docTol * 2.0, h * 0.05);
                        ContactHit hit = ContactAt(k, current[r.b][r.i], true);
                        double drop = Math.Min(requestedDrop, hit.Drop);
                        if (drop <= docTol) return;
                        int generation = FailureGeneration(k);
                        current[r.b][r.i] += gravity * drop;
                        simStats.MaxDrop = Math.Max(simStats.MaxDrop, drop);
                        if (hit.Drop <= requestedDrop + contactTol) simStats.ContactLimitedPoints++;
                        if (failed || localCollapse[r.b][r.i] || drop >= Math.Max(contactTol, 0.95 * h))
                        {
                            globalCollapse[r.b][r.i] = true;
                            collapseGeneration[r.b][r.i] = collapseGeneration[r.b][r.i] < 0
                                ? generation
                                : Math.Min(collapseGeneration[r.b][r.i], generation);
                        }
                    }

                    void ApplyCantilever(int anchor, int freeEnd)
                    {
                        int step = freeEnd > anchor ? 1 : -1;
                        int start = Math.Min(anchor, freeEnd);
                        int end = Math.Max(anchor, freeEnd);
                        var section = RunSection(start, end);
                        if (section.width <= docTol || section.height <= docTol) return;
                        double area = kShape * section.width * section.height * 1e-6;
                        double inertia = kShape * section.width * Math.Pow(section.height, 3) / 12.0 * 1e-12;
                        if (inertia <= 1e-24) return;
                        double length = 0.0;
                        for (int k = anchor; k != freeEnd; k += step) length += SegmentLength(k, k + step);
                        if (length <= docTol) return;
                        double qSelf = rhoWet * G_CONST * area;
                        double q = RunLineLoad(start, end, qSelf, length);
                        double x = 0.0;
                        for (int k = anchor + step; ; k += step)
                        {
                            x += SegmentLength(k - step, k);
                            double drop = CantileverDrop(q, x * 1e-3, length * 1e-3, eT * inertia, kFix) * 1000.0;
                            ApplyDrop(k, drop, drop >= section.height);
                            if (k == freeEnd) break;
                        }
                    }

                    var supports = Enumerable.Range(0, map.Count).Where(k => direct[k]).ToList();
                    if (supports.Count == 0)
                    {
                        simStats.FreeCurves++;
                        for (int k = 0; k < map.Count; k++)
                        {
                            var r = map[k];
                            ContactHit hit = ContactAt(k, current[r.b][r.i], true);
                            ApplyDrop(k, hit.Drop, true);
                        }
                    }
                    else
                    {
                        int firstSupport = supports[0];
                        if (firstSupport > 0)
                        {
                            simStats.StartRuns++;
                            ApplyCantilever(firstSupport, 0);
                        }

                        for (int s = 0; s + 1 < supports.Count; s++)
                        {
                            int left = supports[s];
                            int right = supports[s + 1];
                            if (right - left <= 1) continue;
                            simStats.BridgeRuns++;

                            var section = RunSection(left, right);
                            if (section.width <= docTol || section.height <= docTol) continue;
                            double area = kShape * section.width * section.height * 1e-6;
                            double inertia = kShape * section.width * Math.Pow(section.height, 3) / 12.0 * 1e-12;
                            if (inertia <= 1e-24) continue;
                            var arc = new double[right - left + 1];
                            for (int k = left + 1; k <= right; k++)
                                arc[k - left] = arc[k - left - 1] + SegmentLength(k - 1, k);
                            double length = arc[arc.Length - 1];
                            if (length <= docTol) continue;
                            double qSelf = rhoWet * G_CONST * area;
                            double q = RunLineLoad(left, right, qSelf, length);

                            BridgeResponse response = SolveBridgeResponse(
                                q, length * 1e-3, eT * inertia, eT * area, kFix);
                            simStats.MaxBridgeTension = Math.Max(simStats.MaxBridgeTension, response.Tension);
                            double capacity = hasTau ? tauT * area : double.PositiveInfinity;
                            bool didTear = hasTau && response.Tension > capacity;

                            if (!didTear)
                            {
                                for (int k = left + 1; k < right; k++)
                                {
                                    double xi = arc[k - left] / length;
                                    double drop = 4.0 * response.Delta * xi * (1.0 - xi) * 1000.0;
                                    ApplyDrop(k, drop, drop >= section.height);
                                }
                                continue;
                            }

                            simStats.TornRuns++;
                            int tearLocal = 0;
                            double half = 0.5 * length;
                            for (int k = 0; k + 1 < arc.Length; k++)
                                if (Math.Abs(arc[k] - half) < Math.Abs(arc[tearLocal] - half)) tearLocal = k;
                            int tearIndex = Math.Max(left, Math.Min(right - 1, left + tearLocal));
                            var tearRef = map[tearIndex];
                            torn[tearRef.b][tearRef.i] = true;
                            ApplyCantilever(left, tearIndex);
                            ApplyCantilever(right, tearIndex + 1);
                            for (int k = left + 1; k < right; k++)
                            {
                                var r = map[k];
                                globalCollapse[r.b][r.i] = true;
                                if (collapseGeneration[r.b][r.i] < 0) collapseGeneration[r.b][r.i] = FailureGeneration(k);
                            }
                        }

                        int lastSupport = supports[supports.Count - 1];
                        if (lastSupport < map.Count - 1)
                        {
                            simStats.CantileverRuns++;
                            ApplyCantilever(lastSupport, map.Count - 1);
                        }
                    }

                    foreach (int b in chain)
                    {
                        for (int i = 0; i + 1 < current[b].Count; i++)
                        {
                            if (!printed[b][i] || !printed[b][i + 1] || torn[b][i]) continue;
                            double width = 0.5 * (beadWidths[b][i] + beadWidths[b][i + 1]);
                            double height = 0.5 * (layerHeights[b][i] + layerHeights[b][i + 1]);
                            if (width <= docTol || height <= docTol) continue;
                            var segment = new DepositedSegment
                            {
                                NominalA = nominalPoints[b][i],
                                NominalB = nominalPoints[b][i + 1],
                                CurrentA = current[b][i],
                                CurrentB = current[b][i + 1],
                                Width = width,
                                Height = height,
                                CollapseGeneration = Math.Max(collapseGeneration[b][i], collapseGeneration[b][i + 1]),
                                Layer = layer.Key,
                                Branch = b,
                                PointA = i,
                                PointB = i + 1
                            };
                            AddParentLink(b, i);
                            AddParentLink(b, i + 1);
                            double area = kShape * width * height * 1e-6;
                            double lengthM = segment.CurrentA.DistanceTo(segment.CurrentB) * 1e-3;
                            segment.OwnWeight = rhoWet * G_CONST * area * lengthM;
                            segment.AccumulatedLoad = segment.OwnWeight;
                            int segmentId = deposited.Add(segment);
                            interfaceHistory?.AttachChildSegment(segment, segmentId);

                            void AddParentLink(int branch, int pointIndex)
                            {
                                if (!supportParents.TryGetValue((branch, pointIndex), out int parentId)) return;
                                segment.ParentIds.Add(parentId);
                                int patchId = supportPatches.TryGetValue((branch, pointIndex), out int resolvedPatch)
                                    ? resolvedPatch
                                    : -1;
                                if (!segment.ParentLinks.Any(link => link.ParentId == parentId && link.PatchId == patchId))
                                    segment.ParentLinks.Add(new ParentLink(parentId, patchId));
                                if (patchId >= 0) segment.PatchIds.Add(patchId);
                            }
                        }
                    }
                }
                interfaceHistory?.EvaluateLayer(
                    deposited, layer.Key, layerTime,
                    interfaceSettings.Tau0, interfaceSettings.Rate,
                    simStats);
            }

            watch.Stop();
            accumulatedLoads = nominalPoints
                .Select(branch => Enumerable.Repeat(0.0, branch.Count).ToList())
                .ToArray();
            if (collectLoads)
            {
                if (interfaceHistory == null)
                {
                    for (int id = deposited.Segments.Count - 1; id >= 0; id--)
                    {
                        DepositedSegment segment = deposited.Segments[id];
                        if (segment.ParentIds.Count > 0)
                        {
                            double share = segment.AccumulatedLoad / segment.ParentIds.Count;
                            foreach (int parentId in segment.ParentIds)
                                if (parentId >= 0 && parentId < deposited.Segments.Count)
                                    deposited.Segments[parentId].AccumulatedLoad += share;
                        }
                    }
                }
                foreach (DepositedSegment segment in deposited.Segments)
                {
                    double half = 0.5 * segment.AccumulatedLoad;
                    accumulatedLoads[segment.Branch][segment.PointA] += half;
                    accumulatedLoads[segment.Branch][segment.PointB] += half;
                }
            }
            simStats.ElapsedMs = watch.Elapsed.TotalMilliseconds;
            stats = simStats;
            return current;
        }

        private static void ApplyScheduledInterfaceFailures(
            InterfaceFailureSchedule schedule,
            int currentLayer,
            List<Point3d>[] nominal,
            List<Point3d>[] current,
            double[][] beadWidths,
            double[][] layerHeights,
            int[] branchLayers,
            bool[][] printed,
            bool[][] torn,
            Vector3d gravity,
            Vector3d up,
            DepositedGeometry deposited,
            bool[][] globalCollapse,
            int[][] collapseGeneration,
            ChainSimulationStats stats,
            double tolerance)
        {
            Dictionary<int, List<int>> failures = schedule.FailuresAtLayer(currentLayer);
            foreach (var item in failures)
            {
                int branch = item.Key;
                if (branch < 0 || branch >= current.Length || branchLayers[branch] >= currentLayer) continue;
                var failed = new HashSet<int>(item.Value.Where(index =>
                    index >= 0 && index < current[branch].Count && printed[branch][index]));
                if (failed.Count == 0) continue;

                var ordered = failed.OrderBy(index => index).ToList();
                int cursor = 0;
                bool branchMoved = false;
                while (cursor < ordered.Count)
                {
                    int start = ordered[cursor];
                    int end = start;
                    while (cursor + 1 < ordered.Count && ordered[cursor + 1] == end + 1)
                    {
                        cursor++;
                        end = ordered[cursor];
                    }
                    cursor++;

                    bool anchorLeft = start > 0 && printed[branch][start - 1] && !failed.Contains(start - 1);
                    bool anchorRight = end + 1 < current[branch].Count && printed[branch][end + 1] && !failed.Contains(end + 1);
                    int from = anchorLeft ? start - 1 : start;
                    int to = anchorRight ? end + 1 : end;
                    var arc = new double[to - from + 1];
                    for (int i = from + 1; i <= to; i++)
                        arc[i - from] = arc[i - from - 1] + current[branch][i - 1].DistanceTo(current[branch][i]);
                    double length = arc[arc.Length - 1];

                    var drops = new Dictionary<int, double>();
                    var shapes = new Dictionary<int, double>();
                    double amplitude = double.PositiveInfinity;
                    for (int i = start; i <= end; i++)
                    {
                        double height = layerHeights[branch][i];
                        double contactTol = Math.Max(tolerance * 2.0, height * 0.05);
                        ContactHit hit = FindContactBelow(
                            current[branch][i], beadWidths[branch][i], height,
                            deposited, true, up, contactTol,
                            segment => schedule.IsRejected(branch, i, currentLayer, segment));
                        drops[i] = hit.Drop;

                        double xi = length > tolerance ? arc[i - from] / length : 0.5;
                        double shape;
                        if (!anchorLeft && !anchorRight) shape = 1.0;
                        else if (anchorLeft && anchorRight) shape = 4.0 * xi * (1.0 - xi);
                        else
                        {
                            if (!anchorLeft) xi = 1.0 - xi;
                            shape = xi * xi * (6.0 - 4.0 * xi + xi * xi) / 3.0;
                        }
                        shape = Math.Max(0.0, Math.Min(1.0, shape));
                        shapes[i] = shape;
                        if (shape > 1e-9) amplitude = Math.Min(amplitude, hit.Drop / shape);
                    }

                    if (!double.IsFinite(amplitude) || amplitude <= tolerance) continue;
                    bool runMoved = false;
                    for (int i = start; i <= end; i++)
                    {
                        double drop = Math.Min(drops[i], amplitude * shapes[i]);
                        if (drop <= tolerance) continue;
                        current[branch][i] += gravity * drop;
                        globalCollapse[branch][i] = true;
                        collapseGeneration[branch][i] = collapseGeneration[branch][i] < 0
                            ? 0
                            : Math.Min(collapseGeneration[branch][i], 0);
                        stats.MaxDrop = Math.Max(stats.MaxDrop, drop);
                        stats.InterfaceReplayPoints++;
                        runMoved = true;
                    }
                    if (runMoved)
                    {
                        stats.InterfaceReplayRuns++;
                        branchMoved = true;
                    }
                }

                if (!branchMoved) continue;
                deposited.DeactivateBranch(branch);
                for (int i = 0; i + 1 < current[branch].Count; i++)
                {
                    if (!printed[branch][i] || !printed[branch][i + 1] || torn[branch][i]) continue;
                    double width = 0.5 * (beadWidths[branch][i] + beadWidths[branch][i + 1]);
                    double height = 0.5 * (layerHeights[branch][i] + layerHeights[branch][i + 1]);
                    if (width <= tolerance || height <= tolerance) continue;
                    deposited.Add(new DepositedSegment
                    {
                        NominalA = nominal[branch][i],
                        NominalB = nominal[branch][i + 1],
                        CurrentA = current[branch][i],
                        CurrentB = current[branch][i + 1],
                        Width = width,
                        Height = height,
                        Layer = branchLayers[branch],
                        CollapseGeneration = Math.Max(collapseGeneration[branch][i], collapseGeneration[branch][i + 1]),
                        Branch = branch,
                        PointA = i,
                        PointB = i + 1
                    });
                }
            }
        }

        private static BridgeResponse SolveBridgeResponse(
            double lineWeight,
            double length,
            double ei,
            double ea,
            double kFix)
        {
            if (lineWeight <= 0.0 || length <= 1e-12 || ei <= 1e-24)
                return new BridgeResponse(0.0, 0.0);
            double effectiveEi = ei / Math.Max(kFix, 1e-9);
            double lo = 0.0;
            double hi = Math.Max(length, 1e-6);
            double Residual(double delta)
                => 64.0 * effectiveEi * delta
                    + 128.0 / 9.0 * Math.Max(0.0, ea) * delta * delta * delta
                    - 2.0 / 3.0 * lineWeight * Math.Pow(length, 4);
            while (Residual(hi) < 0.0 && hi < 10.0 * length) hi *= 2.0;
            for (int i = 0; i < 60; i++)
            {
                double mid = 0.5 * (lo + hi);
                if (Residual(mid) > 0.0) hi = mid;
                else lo = mid;
            }
            double delta = 0.5 * (lo + hi);
            double tension = Math.Max(0.0, ea) * 8.0 * delta * delta / (3.0 * length * length);
            return new BridgeResponse(delta, tension);
        }

        private static double CantileverDrop(
            double lineWeight,
            double x,
            double length,
            double ei,
            double kFix)
        {
            if (lineWeight <= 0.0 || x <= 0.0 || length <= 0.0 || ei <= 1e-24) return 0.0;
            x = Math.Min(x, length);
            return lineWeight * x * x * (6.0 * length * length - 4.0 * length * x + x * x)
                / (24.0 * ei) * Math.Max(kFix, 1e-9);
        }

        private readonly struct BridgeResponse
        {
            public BridgeResponse(double delta, double tension)
            {
                Delta = delta;
                Tension = tension;
            }

            public double Delta { get; }
            public double Tension { get; }
        }

        private sealed class ChainSimulationStats
        {
            public int Curves { get; set; }
            public int DirectSupportPoints { get; set; }
            public int UnsupportedStarts { get; set; }
            public int StartRuns { get; set; }
            public int BridgeRuns { get; set; }
            public int CantileverRuns { get; set; }
            public int FreeCurves { get; set; }
            public int TornRuns { get; set; }
            public int ContactLimitedPoints { get; set; }
            public int LoadedRuns { get; set; }
            public int InterfacePatches { get; set; }
            public int InterfaceFailures { get; set; }
            public int InterfaceWidthFallbacks { get; set; }
            public int InterfaceAreaFallbacks { get; set; }
            public int InterfaceReplayRuns { get; set; }
            public int InterfaceReplayPoints { get; set; }
            public double MaxBridgeTension { get; set; }
            public double MaxDrop { get; set; }
            public double MaxLoadFactor { get; set; } = 1.0;
            public double MaxInterfaceRatio { get; set; }
            public double MaxInterfaceFailureDemand { get; set; }
            public double InterfaceLoadGraphMs { get; set; }
            public double InterfaceEvaluationMs { get; set; }
            public double ElapsedMs { get; set; }
        }

        private static ContactHit FindContactBelow(
            Point3d point,
            double width,
            double height,
            DepositedGeometry geometry,
            bool current,
            Vector3d up,
            double contactTol,
            Func<DepositedSegment, bool> reject = null)
        {
            double bedGap = Vector3d.Multiply(point - Point3d.Origin, up) - height;
            var best = new ContactHit
            {
                Drop = Math.Max(0.0, bedGap),
                IsBed = true,
                Generation = -1,
                SegmentId = -1,
                ParentLayer = -1,
                ParentBranch = -1,
                ContactPoint = point - up * Math.Max(0.0, bedGap),
                CurrentOverlapWidth = Math.Max(0.0, width)
            };
            double bestTransverse = double.PositiveInfinity;
            int bestSegmentId = int.MaxValue;

            var candidates = new List<int>();
            if (geometry.Segments.Count > 0)
            {
                Point3d bedPoint = point - up * Math.Max(0.0, bedGap);
                // BoundingBox(min, max) does NOT sort its corners: bedPoint lies
                // below point, so the two-corner constructor builds an inverted,
                // invalid box and RTree.Search silently finds nothing. The
                // point-collection constructor computes proper min/max.
                var box = new BoundingBox(new[] { point, bedPoint });
                box.Inflate(width * 0.5 + contactTol);
                RTree tree = current ? geometry.CurrentTree : geometry.NominalTree;
                tree.Search(box, (sender, args) => candidates.Add(args.Id));
            }

            foreach (int id in candidates)
            {
                if (id < 0 || id >= geometry.Segments.Count) continue;
                DepositedSegment segment = geometry.Segments[id];
                if (!segment.Active) continue;
                if (reject != null && reject(segment)) continue;
                Point3d a = current ? segment.CurrentA : segment.NominalA;
                Point3d b = current ? segment.CurrentB : segment.NominalB;
                Vector3d ab = b - a;
                Vector3d abPlan = ab - up * Vector3d.Multiply(ab, up);
                Vector3d ap = point - a;
                Vector3d apPlan = ap - up * Vector3d.Multiply(ap, up);
                double denominator = abPlan.SquareLength;
                double t = denominator > RhinoMath.ZeroTolerance
                    ? Math.Max(0.0, Math.Min(1.0, Vector3d.Multiply(apPlan, abPlan) / denominator))
                    : 0.5;
                Point3d q = a + ab * t;
                Vector3d delta = point - q;
                double vertical = Vector3d.Multiply(delta, up);
                double gap = vertical - height;
                if (gap < -contactTol) continue;
                Vector3d transverse = delta - up * vertical;
                double overlap = Math.Min(
                    Math.Min(width, segment.Width),
                    (width + segment.Width) * 0.5 - transverse.Length);
                if (overlap <= contactTol) continue;
                double drop = Math.Max(0.0, gap);
                double transverseDistance = transverse.Length;
                bool better = drop < best.Drop - contactTol;
                if (!best.IsBed && Math.Abs(drop - best.Drop) <= contactTol)
                {
                    better = transverseDistance < bestTransverse - contactTol ||
                             (Math.Abs(transverseDistance - bestTransverse) <= contactTol && id < bestSegmentId);
                }
                if (better)
                {
                    best.Drop = drop;
                    best.IsBed = false;
                    best.Generation = segment.CollapseGeneration;
                    best.SegmentId = id;
                    best.ParentLayer = segment.Layer;
                    best.ParentBranch = segment.Branch;
                    best.ContactPoint = q;
                    best.CurrentOverlapWidth = overlap;
                    bestTransverse = transverseDistance;
                    bestSegmentId = id;
                }
            }

            best.IsContact = best.Drop <= contactTol;
            return best;
        }

        private readonly struct InterfaceSettings
        {
            public InterfaceSettings(bool enabled, double tau0, double rate)
            {
                Enabled = enabled;
                Tau0 = tau0;
                Rate = rate;
            }

            public bool Enabled { get; }
            public double Tau0 { get; }
            public double Rate { get; }
        }

        private readonly struct ParentLink
        {
            public ParentLink(int parentId, int patchId)
            {
                ParentId = parentId;
                PatchId = patchId;
            }

            public int ParentId { get; }
            public int PatchId { get; }
        }

        private readonly struct InterfacePointRef
        {
            public InterfacePointRef(int branch, int index)
            {
                Branch = branch;
                Index = index;
            }

            public int Branch { get; }
            public int Index { get; }
        }

        private sealed class InterfaceContactPatch
        {
            public int Id { get; set; }
            public int ChildLayer { get; set; }
            public int SupportLayer { get; set; }
            public int ParentBranch { get; set; }
            public int ParentPointMin { get; set; } = int.MaxValue;
            public int ParentPointMax { get; set; } = int.MinValue;
            public int LastParentPointA { get; set; } = -1;
            public int LastParentPointB { get; set; } = -1;
            public double ContactArea { get; set; }
            public double MaxRatio { get; set; }
            public double DemandAtFailure { get; set; }
            public double CapacityAtFailure { get; set; }
            public int FirstFailureLayer { get; set; } = int.MaxValue;
            public List<InterfacePointRef> Points { get; } = new List<InterfacePointRef>();
            public HashSet<int> ChildSegmentIds { get; } = new HashSet<int>();
        }

        private sealed class RejectedContactRule
        {
            public int FailureLayer { get; set; }
            public int ParentBranch { get; set; }
            public int ParentPointMin { get; set; }
            public int ParentPointMax { get; set; }
        }

        private sealed class InterfaceFailureSchedule
        {
            private readonly Dictionary<(int branch, int point), List<RejectedContactRule>> _rules
                = new Dictionary<(int branch, int point), List<RejectedContactRule>>();
            private readonly HashSet<(int branch, int point)> _failedPoints
                = new HashSet<(int branch, int point)>();

            public int FailedPatchCount { get; private set; }
            public int FailedPointCount => _failedPoints.Count;

            public void Add(InterfaceContactPatch patch)
            {
                FailedPatchCount++;
                foreach (InterfacePointRef point in patch.Points)
                {
                    var key = (point.Branch, point.Index);
                    if (!_rules.TryGetValue(key, out var rules))
                        _rules[key] = rules = new List<RejectedContactRule>();
                    rules.Add(new RejectedContactRule
                    {
                        FailureLayer = patch.FirstFailureLayer,
                        ParentBranch = patch.ParentBranch,
                        ParentPointMin = patch.ParentPointMin,
                        ParentPointMax = patch.ParentPointMax
                    });
                    _failedPoints.Add(key);
                }
            }

            public bool IsFailedPoint(int branch, int point) => _failedPoints.Contains((branch, point));

            public Dictionary<int, List<int>> FailuresAtLayer(int layer)
            {
                var result = new Dictionary<int, List<int>>();
                foreach (var item in _rules)
                {
                    if (!item.Value.Any(rule => rule.FailureLayer == layer)) continue;
                    if (!result.TryGetValue(item.Key.branch, out var points))
                        result[item.Key.branch] = points = new List<int>();
                    points.Add(item.Key.point);
                }
                foreach (List<int> points in result.Values) points.Sort();
                return result;
            }

            public bool IsRejected(int branch, int point, int currentLayer, DepositedSegment parent)
            {
                if (parent == null || !_rules.TryGetValue((branch, point), out var rules)) return false;
                foreach (RejectedContactRule rule in rules)
                {
                    if (currentLayer < rule.FailureLayer || parent.Branch != rule.ParentBranch) continue;
                    if (parent.PointB >= rule.ParentPointMin && parent.PointA <= rule.ParentPointMax) return true;
                }
                return false;
            }
        }

        private sealed class InterfaceHistory
        {
            private readonly InterfaceFailureSchedule _schedule;
            private readonly List<double>[] _ratios;
            private readonly List<InterfaceContactPatch> _patches = new List<InterfaceContactPatch>();

            public InterfaceHistory(InterfaceFailureSchedule schedule, List<double>[] ratios)
            {
                _schedule = schedule;
                _ratios = ratios;
            }

            public void RegisterContacts(
                List<(int b, int i)> map,
                List<Point3d>[] current,
                double[][] beadWidths,
                ContactHit[] hits,
                int childLayer,
                DepositedGeometry geometry,
                Dictionary<(int b, int i), int> supportPatches,
                ChainSimulationStats stats,
                double tolerance)
            {
                InterfaceContactPatch active = null;
                for (int k = 0; k < map.Count; k++)
                {
                    ContactHit hit = hits[k];
                    if (hit == null || !hit.IsContact || hit.IsBed || hit.SegmentId < 0 ||
                        hit.SegmentId >= geometry.Segments.Count)
                    {
                        active = null;
                        continue;
                    }

                    DepositedSegment parent = geometry.Segments[hit.SegmentId];
                    double previousLength = k > 0
                        ? current[map[k - 1].b][map[k - 1].i].DistanceTo(current[map[k].b][map[k].i])
                        : 0.0;
                    double nextLength = k + 1 < map.Count
                        ? current[map[k].b][map[k].i].DistanceTo(current[map[k + 1].b][map[k + 1].i])
                        : 0.0;
                    double tributaryLength = 0.5 * Math.Max(0.0, previousLength) +
                                             0.5 * Math.Max(0.0, nextLength);
                    if (tributaryLength <= tolerance)
                    {
                        stats.InterfaceAreaFallbacks++;
                        active = null;
                        continue;
                    }

                    double overlap = hit.CurrentOverlapWidth;
                    if (!double.IsFinite(overlap) || overlap <= tolerance)
                    {
                        overlap = Math.Min(beadWidths[map[k].b][map[k].i], parent.Width);
                        stats.InterfaceWidthFallbacks++;
                    }
                    if (!double.IsFinite(overlap) || overlap <= tolerance)
                    {
                        active = null;
                        continue;
                    }

                    bool adjacentParent = active != null &&
                        active.ChildLayer == childLayer &&
                        active.SupportLayer == parent.Layer &&
                        active.ParentBranch == parent.Branch &&
                        parent.PointA <= active.LastParentPointB + 1 &&
                        parent.PointB >= active.LastParentPointA - 1;
                    if (!adjacentParent)
                    {
                        active = new InterfaceContactPatch
                        {
                            Id = _patches.Count,
                            ChildLayer = childLayer,
                            SupportLayer = parent.Layer,
                            ParentBranch = parent.Branch
                        };
                        _patches.Add(active);
                        stats.InterfacePatches++;
                    }

                    active.ContactArea += overlap * tributaryLength * 1e-6;
                    active.ParentPointMin = Math.Min(active.ParentPointMin, parent.PointA);
                    active.ParentPointMax = Math.Max(active.ParentPointMax, parent.PointB);
                    active.LastParentPointA = parent.PointA;
                    active.LastParentPointB = parent.PointB;
                    var pointRef = new InterfacePointRef(map[k].b, map[k].i);
                    active.Points.Add(pointRef);
                    supportPatches[(pointRef.Branch, pointRef.Index)] = active.Id;
                }
            }

            public void AttachChildSegment(DepositedSegment segment, int segmentId)
            {
                foreach (int patchId in segment.PatchIds)
                    if (patchId >= 0 && patchId < _patches.Count)
                        _patches[patchId].ChildSegmentIds.Add(segmentId);
            }

            public void EvaluateLayer(
                DepositedGeometry geometry,
                int currentLayer,
                double layerTime,
                double tau0,
                double rate,
                ChainSimulationStats stats)
            {
                var loadWatch = Stopwatch.StartNew();
                RecomputeLoads(geometry, currentLayer);
                loadWatch.Stop();
                stats.InterfaceLoadGraphMs += loadWatch.Elapsed.TotalMilliseconds;
                var evaluationWatch = Stopwatch.StartNew();
                foreach (InterfaceContactPatch patch in _patches)
                {
                    if (patch.ChildLayer > currentLayer || patch.FirstFailureLayer != int.MaxValue ||
                        patch.ContactArea <= 1e-12 || patch.ChildSegmentIds.Count == 0)
                        continue;

                    double demand = 0.0;
                    foreach (int childId in patch.ChildSegmentIds)
                    {
                        if (childId < 0 || childId >= geometry.Segments.Count) continue;
                        DepositedSegment child = geometry.Segments[childId];
                        int activePatchCount = child.PatchIds.Count(id => IsPatchActive(id, currentLayer));
                        if (activePatchCount > 0) demand += child.AccumulatedLoad / activePatchCount;
                    }

                    double age = Math.Max(0, currentLayer - patch.SupportLayer) * layerTime;
                    double strength = Math.Max(0.0, tau0 + rate * age);
                    double capacity = strength * patch.ContactArea;
                    double ratio = capacity > 1e-12 ? demand / capacity : double.PositiveInfinity;
                    patch.MaxRatio = Math.Max(patch.MaxRatio, ratio);
                    stats.MaxInterfaceRatio = Math.Max(stats.MaxInterfaceRatio, ratio);
                    foreach (InterfacePointRef point in patch.Points)
                        _ratios[point.Branch][point.Index] = Math.Max(_ratios[point.Branch][point.Index], ratio);

                    if (ratio < 1.0) continue;
                    patch.FirstFailureLayer = currentLayer;
                    patch.DemandAtFailure = demand;
                    patch.CapacityAtFailure = capacity;
                    stats.InterfaceFailures++;
                    stats.MaxInterfaceFailureDemand = Math.Max(stats.MaxInterfaceFailureDemand, demand);
                    _schedule.Add(patch);
                }
                evaluationWatch.Stop();
                stats.InterfaceEvaluationMs += evaluationWatch.Elapsed.TotalMilliseconds;
            }

            private bool IsPatchActive(int patchId, int currentLayer)
                => patchId >= 0 && patchId < _patches.Count &&
                   _patches[patchId].FirstFailureLayer > currentLayer;

            private void RecomputeLoads(DepositedGeometry geometry, int currentLayer)
            {
                foreach (DepositedSegment segment in geometry.Segments)
                    segment.AccumulatedLoad = segment.OwnWeight;

                for (int id = geometry.Segments.Count - 1; id >= 0; id--)
                {
                    DepositedSegment segment = geometry.Segments[id];
                    List<int> activeParents = segment.ParentLinks
                        .Where(link => link.ParentId >= 0 && link.ParentId < geometry.Segments.Count &&
                                       (link.PatchId < 0 || IsPatchActive(link.PatchId, currentLayer)))
                        .Select(link => link.ParentId)
                        .Distinct()
                        .ToList();
                    if (activeParents.Count == 0) continue;
                    double share = segment.AccumulatedLoad / activeParents.Count;
                    foreach (int parentId in activeParents)
                        geometry.Segments[parentId].AccumulatedLoad += share;
                }
            }
        }

        private sealed class DepositedSegment
        {
            public Point3d NominalA { get; set; }
            public Point3d NominalB { get; set; }
            public Point3d CurrentA { get; set; }
            public Point3d CurrentB { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public bool Active { get; set; } = true;
            public int CollapseGeneration { get; set; }
            public int Layer { get; set; } = -1;
            public int Branch { get; set; } = -1;
            public int PointA { get; set; } = -1;
            public int PointB { get; set; } = -1;
            public HashSet<int> ParentIds { get; } = new HashSet<int>();
            public List<ParentLink> ParentLinks { get; } = new List<ParentLink>();
            public HashSet<int> PatchIds { get; } = new HashSet<int>();
            public double OwnWeight { get; set; }
            public double AccumulatedLoad { get; set; }
        }

        private sealed class DepositedGeometry
        {
            public List<DepositedSegment> Segments { get; } = new List<DepositedSegment>();
            public RTree NominalTree { get; } = new RTree();
            public RTree CurrentTree { get; } = new RTree();

            public int Add(DepositedSegment segment)
            {
                int id = Segments.Count;
                Segments.Add(segment);
                // segment endpoints arrive in path order, so the (min, max)
                // BoundingBox constructor would build inverted/invalid boxes for
                // roughly half of them and the RTree would miss them silently.
                var nominalBox = new BoundingBox(new[] { segment.NominalA, segment.NominalB });
                var currentBox = new BoundingBox(new[] { segment.CurrentA, segment.CurrentB });
                double inflate = Math.Max(0.0, segment.Width * 0.5);
                nominalBox.Inflate(inflate);
                currentBox.Inflate(inflate);
                NominalTree.Insert(nominalBox, id);
                CurrentTree.Insert(currentBox, id);
                return id;
            }

            public void DeactivateBranch(int branch)
            {
                foreach (DepositedSegment segment in Segments)
                    if (segment.Active && segment.Branch == branch)
                        segment.Active = false;
            }
        }

        private sealed class ContactHit
        {
            public bool IsContact { get; set; }
            public bool IsBed { get; set; }
            public double Drop { get; set; }
            public int Generation { get; set; }
            public int SegmentId { get; set; } = -1;
            public int ParentLayer { get; set; } = -1;
            public int ParentBranch { get; set; } = -1;
            public Point3d ContactPoint { get; set; } = Point3d.Unset;
            public double CurrentOverlapWidth { get; set; }
        }

        // ---- seam stitching ---------------------------------------------------

        /// <summary>
        /// Groups same-layer branches into ordered chains by matching each branch's
        /// end point to another branch's start point within tol (quantized-key
        /// lookup). Only unique, unambiguous end-to-start matches are linked, so
        /// Y-junctions (one point shared by several branch starts) break chains
        /// rather than guessing. Closed multi-branch loops are emitted as open
        /// chains starting at an arbitrary member (conservative for span anchors).
        /// </summary>
        private static List<List<int>> BuildChains(List<int> branchIds, List<Point3d>[] pts, double tol)
        {
            (long, long, long) Key(Point3d p) => (
                (long)Math.Round(p.X / tol),
                (long)Math.Round(p.Y / tol),
                (long)Math.Round(p.Z / tol));

            var startMap = new Dictionary<(long, long, long), List<int>>();
            foreach (int b in branchIds)
            {
                if (pts[b].Count == 0) continue;
                var key = Key(pts[b][0]);
                if (!startMap.TryGetValue(key, out var list)) startMap[key] = list = new List<int>();
                list.Add(b);
            }

            var next = new Dictionary<int, int>();
            var hasIncoming = new HashSet<int>();
            foreach (int b in branchIds)
            {
                if (pts[b].Count == 0) continue;
                var key = Key(pts[b][pts[b].Count - 1]);
                if (!startMap.TryGetValue(key, out var candidates)) continue;
                int match = -1, count = 0;
                foreach (int c in candidates)
                {
                    if (c == b) continue; // closed single curve: do not self-link
                    count++;
                    match = c;
                }
                if (count == 1 && !hasIncoming.Contains(match))
                {
                    next[b] = match;
                    hasIncoming.Add(match);
                }
            }

            var chains = new List<List<int>>();
            var visited = new HashSet<int>();
            // open chains first (branches nobody links into)
            foreach (int b in branchIds)
            {
                if (visited.Contains(b) || hasIncoming.Contains(b)) continue;
                chains.Add(WalkChain(b, next, visited));
            }
            // remaining branches belong to closed multi-branch loops
            foreach (int b in branchIds)
            {
                if (visited.Contains(b)) continue;
                chains.Add(WalkChain(b, next, visited));
            }
            return chains;
        }

        private static List<int> WalkChain(int startBranch, Dictionary<int, int> next, HashSet<int> visited)
        {
            var chain = new List<int>();
            int current = startBranch;
            while (current >= 0 && !visited.Contains(current))
            {
                chain.Add(current);
                visited.Add(current);
                current = next.TryGetValue(current, out var nx) ? nx : -1;
            }
            return chain;
        }

        // ---- helpers ---------------------------------------------------------

        private static List<T> BranchAt<T>(DataTree<T> tree, GH_Path branchPath)
            => tree != null && tree.PathExists(branchPath) ? tree.Branch(branchPath) : null;

        private static int[] PrintedCounts(
            Dictionary<int, List<int>> layerGroups,
            List<Point3d>[] points,
            double simPath)
        {
            var counts = new int[points.Length];
            int total = points.Sum(branch => branch.Count);
            int remaining = (int)Math.Round(Math.Max(0.0, Math.Min(1.0, simPath)) * total);
            foreach (var layer in layerGroups.OrderBy(pair => pair.Key))
            {
                foreach (int branch in layer.Value)
                {
                    int count = Math.Min(points[branch].Count, Math.Max(0, remaining));
                    counts[branch] = count;
                    remaining -= count;
                    if (remaining <= 0) return counts;
                }
            }
            return counts;
        }

        private static DataTree<T> ClipTree<T>(
            DataTree<T> source,
            GH_Path[] paths,
            int[] counts)
        {
            if (source == null) return null;
            var clipped = new DataTree<T>();
            int branchCount = Math.Min(paths.Length, counts.Length);
            for (int b = 0; b < branchCount; b++)
            {
                int count = counts[b];
                if (count <= 0) continue;
                List<T> sourceBranch = BranchAt(source, paths[b]);
                if (sourceBranch == null || sourceBranch.Count == 0) continue;
                clipped.AddRange(sourceBranch.Take(Math.Min(count, sourceBranch.Count)), paths[b]);
            }
            return clipped;
        }

        private static DataTree<Plane> BuildDeflectedPlanes(
            DataTree<Point3d> points,
            DataTree<Plane> originalPlanes,
            DataTree<bool> torn,
            out int fallbackCount,
            out int resetCount)
        {
            var result = new DataTree<Plane>();
            fallbackCount = 0;
            resetCount = 0;
            if (points == null) return result;

            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;

            bool ProjectPerpendicular(Vector3d candidate, Vector3d tangent, out Vector3d projected)
            {
                projected = candidate - tangent * Vector3d.Multiply(candidate, tangent);
                return projected.Unitize() && !projected.IsTiny(tol);
            }

            foreach (GH_Path path in points.Paths)
            {
                List<Point3d> branch = points.Branch(path);
                if (branch == null || branch.Count == 0)
                {
                    result.EnsurePath(path);
                    continue;
                }

                List<Plane> originals = BranchAt(originalPlanes, path);
                List<bool> tornBranch = BranchAt(torn, path);
                int n = branch.Count;
                bool closed = n > 2 && branch[0].DistanceTo(branch[n - 1]) <= tol;
                int workCount = closed ? n - 1 : n;
                var generated = new List<Plane>(n);
                Vector3d previousTangent = Vector3d.Unset;
                Vector3d previousHeight = Vector3d.Unset;

                Vector3d TangentAt(int index)
                {
                    if (workCount <= 1)
                    {
                        if (originals != null && originals.Count > index && originals[index].IsValid)
                            return originals[index].XAxis;
                        return Vector3d.XAxis;
                    }

                    Vector3d tangent;
                    if (closed)
                    {
                        int previous = (index - 1 + workCount) % workCount;
                        int next = (index + 1) % workCount;
                        tangent = branch[next] - branch[previous];
                    }
                    else if (index == 0)
                    {
                        tangent = branch[1] - branch[0];
                    }
                    else if (index == workCount - 1)
                    {
                        tangent = branch[workCount - 1] - branch[workCount - 2];
                    }
                    else
                    {
                        tangent = branch[index + 1] - branch[index - 1];
                    }

                    if (tangent.Unitize() && !tangent.IsTiny(tol)) return tangent;
                    for (int offset = 1; offset < workCount; offset++)
                    {
                        int before = index - offset;
                        int after = index + offset;
                        if (before >= 0)
                        {
                            tangent = branch[index] - branch[before];
                            if (tangent.Unitize() && !tangent.IsTiny(tol)) return tangent;
                        }
                        if (after < workCount)
                        {
                            tangent = branch[after] - branch[index];
                            if (tangent.Unitize() && !tangent.IsTiny(tol)) return tangent;
                        }
                    }
                    return Vector3d.XAxis;
                }

                Vector3d PreferredHeight(int index)
                {
                    if (originals != null && originals.Count > index && originals[index].IsValid)
                    {
                        Vector3d originalZ = originals[index].ZAxis;
                        if (originalZ.Unitize() && !originalZ.IsTiny(tol)) return originalZ;
                    }
                    return Vector3d.ZAxis;
                }

                for (int i = 0; i < workCount; i++)
                {
                    Vector3d tangent = TangentAt(i);
                    bool reset = i == 0 || (tornBranch != null && i - 1 < tornBranch.Count && tornBranch[i - 1]);
                    if (reset && i > 0) resetCount++;

                    Vector3d height;
                    bool validHeight = false;
                    if (!reset && previousTangent.IsValid && previousHeight.IsValid)
                    {
                        height = previousHeight;
                        Transform rotation = Transform.Rotation(previousTangent, tangent, Point3d.Origin);
                        height.Transform(rotation);
                        validHeight = ProjectPerpendicular(height, tangent, out height);
                    }
                    else
                    {
                        height = Vector3d.Unset;
                    }

                    if (!validHeight)
                    {
                        Vector3d preferred = PreferredHeight(i);
                        validHeight = ProjectPerpendicular(preferred, tangent, out height);
                    }
                    if (!validHeight && originals != null && originals.Count > i && originals[i].IsValid)
                        validHeight = ProjectPerpendicular(originals[i].YAxis, tangent, out height);
                    if (!validHeight)
                    {
                        Vector3d fallback = Math.Abs(tangent.Z) < 0.9 ? Vector3d.ZAxis : Vector3d.XAxis;
                        validHeight = ProjectPerpendicular(fallback, tangent, out height);
                        fallbackCount++;
                    }

                    if (!reset && previousHeight.IsValid && Vector3d.Multiply(height, previousHeight) < 0.0)
                        height.Reverse();

                    Vector3d yAxis = Vector3d.CrossProduct(height, tangent);
                    if (!yAxis.Unitize() || yAxis.IsTiny(tol))
                    {
                        yAxis = Vector3d.CrossProduct(height, Vector3d.XAxis);
                        if (!yAxis.Unitize() || yAxis.IsTiny(tol)) yAxis = Vector3d.YAxis;
                        fallbackCount++;
                    }

                    Plane frame = new Plane(branch[i], tangent, yAxis);
                    if (!frame.IsValid)
                    {
                        frame = new Plane(branch[i], height);
                        fallbackCount++;
                    }
                    generated.Add(frame);
                    previousTangent = tangent;
                    previousHeight = frame.ZAxis;
                }

                if (closed)
                {
                    Plane seam = generated.Count > 0 ? generated[0] : new Plane(branch[n - 1], Vector3d.ZAxis);
                    seam.Origin = branch[n - 1];
                    generated.Add(seam);
                }
                result.AddRange(generated, path);
            }
            return result;
        }

        private static double ValueAt(IList<double> values, int index, double fallback)
        {
            if (values == null || values.Count == 0) return fallback;
            return values[Math.Min(index, values.Count - 1)];
        }

        private static Dictionary<string, double> ResolveProperties(
            WasperMaterial material,
            Wasper3dpProperties direct,
            out Dictionary<string, string> sources)
        {
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (material != null)
            {
                foreach (var key in new[] { "E_fresh", "E_rate", "tau_y0", "A_thix", "density_wet", "density", "k_shape", "k_fix", "tau_interface", "A_interface" })
                {
                    if (!material.TryGetDouble(key, out var value)) continue;
                    result[key] = value;
                    sources[key] = "wsp_mat";
                }
            }
            if (direct != null)
            {
                foreach (var item in direct.Properties)
                {
                    if (!double.TryParse(item.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) continue;
                    result[item.Key] = value;
                    sources[item.Key] = "3dp_props";
                }
            }
            return result;
        }

        private static double ResolveCalibrationProperty(
            Dictionary<string, double> values,
            Dictionary<string, string> sources,
            string key,
            bool hasWiredValue,
            double wiredValue,
            double fallback,
            ICollection<string> warnings,
            out string source)
        {
            double candidate;
            if (hasWiredValue)
            {
                candidate = wiredValue;
                source = "wired Pr04 input";
            }
            else if (values.TryGetValue(key, out candidate))
            {
                source = sources.TryGetValue(key, out var resolvedSource) ? resolvedSource : "resolved property";
            }
            else
            {
                return ReturnFallback(out source);
            }

            if (candidate > 0.0 && double.IsFinite(candidate)) return candidate;

            warnings.Add($"{key} from {source} must be positive and finite; the fallback {fallback:R} is used.");
            source = $"fallback {fallback:R} after invalid {source}";
            return fallback;

            double ReturnFallback(out string fallbackSource)
            {
                fallbackSource = $"default {fallback:R}";
                return fallback;
            }
        }

        private static WasperMaterial ExtractMaterial(IGH_Goo goo)
        {
            if (goo is WasperMaterialGoo materialGoo) return materialGoo.Value;
            if (goo is GH_ObjectWrapper wrapper) return wrapper.Value as WasperMaterial;
            return null;
        }

        private static Wasper3dpProperties Extract3dpProperties(IGH_Goo goo)
        {
            if (goo is Wasper3dpPropertiesGoo propsGoo) return propsGoo.Value;
            if (goo is GH_ObjectWrapper wrapper) return wrapper.Value as Wasper3dpProperties;
            return null;
        }

        private static bool TryExtractPath(IGH_Goo goo, out WasperPrintPath path)
        {
            path = null;
            if (goo is WasperPrintPathGoo pathGoo) path = pathGoo.Value;
            else if (goo is GH_ObjectWrapper wrapper) path = wrapper.Value as WasperPrintPath;
            return path != null;
        }

        private static double GetPositiveOrDefault(Dictionary<string, double> values, string key, double fallback) => TryPositive(values, key, out var value) ? value : fallback;
        private static bool TryPositive(Dictionary<string, double> values, string key, out double value) => values.TryGetValue(key, out value) && value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
