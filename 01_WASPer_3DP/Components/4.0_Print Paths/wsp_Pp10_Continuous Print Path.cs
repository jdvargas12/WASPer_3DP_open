using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Rhino;
using Rhino.Geometry;
using WASPer_3DP;

namespace WASPer_3DP_Components._4_0_Print_Paths
{
    public sealed class wsp_Pp10_Continuous_Print_Path : GH_Component
    {
        // Legacy all-or-nothing key, still read for backward compatibility with saved files.
        private const string ShowAllOutputsKey = "wsp_pp10_continuous_show_all_outputs";
        private const string VisibleOutputsMaskKey = "wsp_pp10_continuous_visible_outputs_mask";
        private static readonly string[] OutputCatalog = WasperPathDebugOutputs.CoreNickNames
            .Concat(new[] { "strokes", "links", "seam_pts" })
            .ToArray();
        private static readonly int AllOutputsMask = (1 << OutputCatalog.Length) - 1;
        private readonly string _versionTag;
        private int _visibleOutputsMask;
        private readonly List<Polyline> _previewPolylines = new List<Polyline>();
        private readonly List<WasperPathRole> _previewRoles = new List<WasperPathRole>();
        private BoundingBox _previewBounds = BoundingBox.Empty;

        public wsp_Pp10_Continuous_Print_Path()
            : base(
                "wsp_Pp10_Continuous Print Path",
                "Continuous Path",
                "Builds best-effort uninterrupted extrusion strokes from a packed WASPer Print Path.\r\n\r\n" +
                "MODE 0 — PER LAYER\r\n" +
                "Reorders and reverses eligible branches inside each logical layer. Endpoints within link_dist are joined by explicit Transition branches.\r\n\r\n" +
                "MODE 1 — FULL 3D\r\n" +
                "Also links consecutive layers. Compatible one-Shell-per-layer closed stacks keep the first layer flat, then ramp upward. The accepted Shell stack is concatenated into one actual wsp_path branch, rather than remaining as visually touching layer branches. max_slope determines ramp length: shallow slopes use more of the loop; steep slopes use less. Local layer_h and print_vol are recalculated from the resulting geometry.\r\n\r\n" +
                "MODE 2 — SHELL + 2D INFILL\r\n" +
                "For layers containing exactly one targeted closed Shell and at least one targeted Infill branch, places two seams from the provisional infill-chain ends and splits the Shell into two open arcs. Consecutive layers may link, but Shells are not spiralized. At each seam, tail_l shortens both the participating Shell end and the corresponding Infill end by the same projected distance, then joins their new endpoints with one straight Transition segment.\r\n\r\n" +
                "MODE 3 — SHELL + 2D INFILL, STACKED\r\n" +
                "For layers containing exactly one targeted Shell (closed, or open such as In10 X-seam paths, which are re-closed by depositing across the input seam gap) and at least one targeted Infill branch: the Shell loop is re-seamed at the Infill end nearest the previous layer's exit, tail_l shortens both re-seamed Shell ends and the Infill's near end, and the layer prints Shell loop, then a Transition, then the Infill toward its far end. Each layer therefore exits at the opposite Infill end from where it entered, so the seam side and the Infill direction alternate per layer and the whole stack becomes one continuous stroke. One short Transition connector is forced between consecutive stacked layers regardless of link_dist, and no connector is generated after the final layer.\r\n\r\n" +
                "Original branches retain Shell, Infill, Partition, Support, Transition, or Undefined roles. Added deposited links use Transition. Branch-aligned stroke_id metadata tells Gc03 which consecutive branches form one continuous extrusion stroke. Unsafe or distant candidates start a new stroke instead of being forced.\r\n\r\n" +
                "PRINTABILITY WARNING\r\nA continuous rising Shell does not automatically adapt the interior. Separate Infill, Partition, Support, or Undefined paths may intersect the Shell, protrude outside it, lose support, or cause nozzle collisions. Mode 2 deliberately introduces two seam/cold-joint locations per eligible layer; their positions follow the infill and can drift vertically. Inspect the complete result in Pp04 and adapt the interior before generating G-code.\r\n\r\n" +
                "Use this near the end of the Print Paths workflow; later geometry changes invalidate continuity. Please use the Gc01 WASPer Path from Curves before using this component.",
                WASPerPalette.DesignFabrication,
                "4.0_Print Paths")
        {
            Version v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v == null ? "v1.0.x" : $"v{v.Major}.{v.Minor}.{v.Build}";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("C2AD21AD-120A-4E18-9319-4C4887A6D6CC");
        public override GH_Exposure Exposure => GH_Exposure.secondary;
        protected override Bitmap Icon => ContinuousPathIcon.Create();

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path", "wsp_path",
                "WASPer Print Path to connect. Canonical pt_planes, roles, flow, height, width, and speed are reordered together. Please use the Gc01 WASPer Path from Curves before using this component.",
                GH_ParamAccess.item);
            p.AddIntegerParameter(
                "mode", "mode",
                "Continuity mode. 0 = Per layer. 1 = Full 3D spiral Shell continuity. 2 = Shell + 2D infill double seam; links consecutive layers without spiralization. 3 = Shell + 2D infill stacked; each layer's Shell loop (open input Shells are re-closed) is re-seamed at the Infill end nearest the previous layer's exit and printed as Shell, Transition, Infill toward the far end, so seam side and Infill direction alternate and consecutive layers join into one continuous stack via forced Transition connectors.",
                GH_ParamAccess.item, 0);
            if (p[1] is Param_Integer modeParameter)
            {
                modeParameter.AddNamedValue("Per layer", 0);
                modeParameter.AddNamedValue("Full 3D", 1);
                modeParameter.AddNamedValue("Shell + 2D infill", 2);
                modeParameter.AddNamedValue("Shell + 2D infill stacked", 3);
            }
            p.AddParameter(WasperTargetRolesParam.Create(
                "Roles eligible for ordering and continuity. 0 = All paths (default), 1 = Shell, 2 = Infill, 3 = Partition, 4 = Support, 5 = Transition, 6 = Undefined. Selecting several roles allows continuity between them; original role identity is preserved."));
            p.AddNumberParameter(
                "link_distance", "link_dist",
                "Maximum deposited straight-link distance in model units. 0 permits only coincident endpoints and spiralized compatible Shell stacks; no spanning Transition is introduced.",
                GH_ParamAccess.item, 0.0);
            p.AddNumberParameter(
                "maximum_slope", "max_slope",
                "Maximum absolute link/ramp slope angle in degrees relative to the local horizontal. It also determines spiral ramp length: a shallow slope creates a longer transition and a steep slope a shorter one. Range >0 to <90. Default 45.",
                GH_ParamAccess.item, 45.0);
            int refIndex = p.AddPointParameter(
                "reference_point", "ref_pt",
                "Optional start/seam preference. When absent, ordering begins from the first valid incoming branch start.",
                GH_ParamAccess.item);
            p[refIndex].Optional = true;
            p.AddNumberParameter(
                "tail_length", "tail_l",
                "Modes 2 and 3 only. Matched projected in-plane length removed from both the participating Shell end and corresponding Infill end at each seam. Their shortened endpoints are joined by one straight Transition segment. This is not an overlap or fillet distance. 0 disables paired shortening.",
                GH_ParamAccess.item, 0.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("wsp_path", "wsp_path", "WASPer Print Path carrying reordered branches, Transition links, and branch-aligned stroke_id continuity metadata.", GH_ParamAccess.item);
            // Pp10 draws the packed path itself so its preview remains available
            // even when the generic output parameter does not forward IGH_PreviewData.
            // Hide only the parameter-owned preview to avoid drawing it twice.
            if (p[0] is IGH_PreviewObject outputPreview)
                outputPreview.Hidden = true;
            p.AddTextParameter("summary", "summary", "Continuity result, inserted links, spiralized layers, rejected candidates, and resulting stroke count.", GH_ParamAccess.item);
            // Debug outputs are NOT registered here. RegisterOutputParams only runs once at
            // construction, before Read() has restored any persisted state, so a
            // _visibleOutputsMask-gated branch here would never execute; the real,
            // runtime-toggleable output set is entirely owned by RebuildOutputs(), driven by the
            // right-click "Debug Outputs" submenu.
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            WasperPathDebugOutputs.AppendOutputVisibilityMenu(
                this,
                menu,
                "Debug Outputs",
                OutputCatalog,
                () => _visibleOutputsMask,
                mask =>
                {
                    RecordUndoEvent("Toggle Pp10 outputs");
                    _visibleOutputsMask = mask;
                    RebuildOutputs();
                    ExpireSolution(true);
                });
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetInt32(VisibleOutputsMaskKey, _visibleOutputsMask);
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
            RebuildOutputs();
            return base.Read(reader);
        }

        private void RebuildOutputs()
        {
            WasperPathDebugOutputs.Rebuild(
                this,
                _visibleOutputsMask,
                "Continuity result, inserted links, spiralized layers, rejected candidates, and resulting stroke count.",
                OutputCatalog,
                registerExtras: RegisterDebugOutputs);
            if (Params.Output.Count > 0 && Params.Output[0] is IGH_PreviewObject outputPreview)
                outputPreview.Hidden = true;
        }

        private static void RegisterDebugOutputs(GH_Component component, Func<string, bool> isVisible)
        {
            if (isVisible("strokes"))
                component.Params.RegisterOutputParam(new Param_Curve
                {
                    Name = "continuous_strokes",
                    NickName = "strokes",
                    Description = "Polyline branches of the outgoing path, grouped by logical layer and ordered for printing.",
                    Access = GH_ParamAccess.tree
                });
            if (isVisible("links"))
                component.Params.RegisterOutputParam(new Param_Curve
                {
                    Name = "transition_links",
                    NickName = "links",
                    Description = "New deposited Transition links only.",
                    Access = GH_ParamAccess.tree
                });
            if (isVisible("seam_pts"))
                component.Params.RegisterOutputParam(new Param_Point
                {
                    Name = "seam_pts",
                    NickName = "seam_pts",
                    Description = "Mode 2 seam points. Each split-layer branch contains original Shell seams A/B followed, when tail_l > 0, by shortened endpoints in pairs: Shell A_t, Infill A_t, Shell B_t, Infill B_t.",
                    Access = GH_ParamAccess.tree
                });
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            var totalWatch = Stopwatch.StartNew();
            ClearPreview();
            if (!WasperGcodeTreeUtil.TryGetPrintPath(da, 0, out WasperPrintPath source) ||
                source == null || !source.HasPlanes)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "wsp_path must be a valid WASPer Print Path containing pt_planes. Please use the Pp01 WASPer Path from Curves before using this component.");
                return;
            }

            int mode = 0;
            var rawRoles = new List<int>();
            double linkDistance = 0.0;
            double maxSlope = 45.0;
            Point3d reference = Point3d.Unset;
            double tailLength = 0.0;
            da.GetData(1, ref mode);
            da.GetDataList(2, rawRoles);
            da.GetData(3, ref linkDistance);
            da.GetData(4, ref maxSlope);
            da.GetData(5, ref reference);
            da.GetData(6, ref tailLength);

            if (mode != 0 && mode != 1 && mode != 2 && mode != 3)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "mode must be 0 (Per layer), 1 (Full 3D), 2 (Shell + 2D infill), or 3 (Shell + 2D infill stacked).");
                return;
            }
            if (!double.IsFinite(tailLength) || tailLength < 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "tail_l must be finite and >= 0.");
                return;
            }
            if (mode < 2 && tailLength > 0.0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "tail_l is only used by modes 2 and 3 and is ignored in the selected mode.");
            if (!double.IsFinite(linkDistance) || linkDistance < 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "link_dist must be finite and >= 0.");
                return;
            }
            if (!double.IsFinite(maxSlope) || maxSlope <= 0.0 || maxSlope >= 90.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "max_slope must be finite, > 0, and < 90 degrees.");
                return;
            }
            if (!WasperGcodeTreeUtil.TryNormalizeTargetRoles(rawRoles, out List<int> targetRoles, out string roleError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, roleError);
                return;
            }

            double tolerance = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? RhinoMath.SqrtEpsilon;
            List<GH_Path> sourcePaths = source.PtPlanes.Paths.ToList();
            int prefixLength = WasperGcodeTreeUtil.CommonPathPrefixLength(sourcePaths);
            var records = new List<BranchRecord>();
            for (int b = 0; b < source.PtPlanes.BranchCount; b++)
            {
                GH_Path path = source.PtPlanes.Paths[b];
                IList<Plane> planes = source.PtPlanes.Branch(path);
                if (planes == null || planes.Count < 2)
                    continue;
                WasperPathRole role = WasperGcodeTreeUtil.PathRoleAt(source.PathRoles, path);
                records.Add(BranchRecord.From(source, path, planes, role,
                    WasperGcodeTreeUtil.LayerFromPath(path, prefixLength),
                    WasperGcodeTreeUtil.MatchesTargetRoles(role, targetRoles)));
            }
            if (records.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "wsp_path contains no branches with at least two valid path planes.");
                return;
            }
            int inputBranchCount = records.Count;

            var seamPoints = new DataTree<Point3d>();
            var seamStats = new DoubleSeamStats();
            var splitLayers = new HashSet<int>();
            if (mode == 2)
                SplitLayersForInfillSeams(records, reference, tailLength, tolerance, seamPoints, seamStats, splitLayers);
            if (mode == 3)
                BuildStackedSingleSeamLayers(records, reference, tailLength, tolerance, seamPoints, seamStats, splitLayers);

            int spiralized = 0;
            int rejectedSpirals = 0;
            int recalculatedHeightPoints = 0;
            double minimumRecalculatedHeight = double.PositiveInfinity;
            double maximumRecalculatedHeight = 0.0;
            double minimumRampFraction = double.PositiveInfinity;
            double maximumRampFraction = 0.0;
            if (mode == 1)
                SpiralizeCompatibleShellStack(
                    records, maxSlope, tolerance,
                    ref spiralized, ref rejectedSpirals,
                    ref recalculatedHeightPoints,
                    ref minimumRecalculatedHeight,
                    ref maximumRecalculatedHeight,
                    ref minimumRampFraction,
                    ref maximumRampFraction);
            int mergedShellBranches = mode == 1
                ? MergeSpiralizedShellChains(records, tolerance)
                : 0;

            var orderedOutput = new List<BranchRecord>();
            var transitionOutput = new List<BranchRecord>();
            int insertedLinks = 0;
            int rejectedLinks = 0;
            int forcedStackConnectors = 0;
            int rejectedStackConnectors = 0;
            int strokeId = -1;
            BranchRecord previous = null;
            int previousLayer = int.MinValue;

            foreach (IGrouping<int, BranchRecord> layerGroup in records.GroupBy(r => r.Layer).OrderBy(g => g.Key))
            {
                List<BranchRecord> layerRecords = OrderLayer(layerGroup.ToList(),
                    previous != null && mode != 0 ? previous.End : reference);

                if (mode == 0)
                    previous = null;

                foreach (BranchRecord record in layerRecords)
                {
                    bool canTargetLink = previous != null && previous.Targeted && record.Targeted;
                    double distance = previous == null ? double.PositiveInfinity : previous.End.DistanceTo(record.Start);
                    bool layerAllowed = mode != 0 || previousLayer == record.Layer;

                    // Mode 3: the connector between consecutive split layers is
                    // structural, so it bypasses link_dist. The slope guard is
                    // kept for printability; a rejected connector degrades to a
                    // new stroke and is reported instead of forcing unsafe
                    // geometry. No connector forms after the final layer
                    // because there is simply no following record.
                    bool stackConnector = mode == 3 &&
                        canTargetLink &&
                        record.Layer != previousLayer &&
                        splitLayers.Contains(record.Layer) &&
                        splitLayers.Contains(previousLayer);
                    bool withinDistance = stackConnector ||
                        distance <= Math.Max(tolerance, linkDistance);
                    bool slopeAllowed = previous == null || LinkSlopeDegrees(previous.End, record.Start) <= maxSlope + 1e-9;
                    bool continues = canTargetLink && layerAllowed && withinDistance && slopeAllowed;

                    if (!continues)
                    {
                        strokeId++;
                        if (canTargetLink && layerAllowed && (!withinDistance || !slopeAllowed))
                            rejectedLinks++;
                        if (stackConnector && !slopeAllowed)
                            rejectedStackConnectors++;
                    }
                    else if (distance > tolerance)
                    {
                        BranchRecord link = BranchRecord.Transition(previous, record, record.Layer);
                        link.StrokeId = strokeId;
                        orderedOutput.Add(link);
                        transitionOutput.Add(link);
                        insertedLinks++;
                        if (stackConnector)
                            forcedStackConnectors++;
                    }

                    record.StrokeId = strokeId;
                    orderedOutput.Add(record);
                    previous = record;
                    previousLayer = record.Layer;
                }
            }

            BuildTrees(orderedOutput, source, prefixLength, spiralized > 0,
                out WasperPrintPath result,
                out DataTree<Curve> strokeCurves,
                out DataTree<Curve> linkCurves);
            UpdatePreview(result);

            int strokeCount = result.StrokeIds == null
                ? 0
                : result.StrokeIds.Branches.SelectMany(branch => branch).Where(id => id >= 0).Distinct().Count();
            var summary = new StringBuilder();
            summary.AppendLine(mode == 0 ? "Continuous path mode: Per layer" :
                mode == 1 ? "Continuous path mode: Full 3D" :
                mode == 2 ? "Continuous path mode: Shell + 2D infill" :
                "Continuous path mode: Shell + 2D infill stacked");
            summary.AppendLine($"input/output branches: {inputBranchCount}/{orderedOutput.Count}");
            summary.AppendLine($"continuous strokes: {strokeCount}");
            summary.AppendLine($"Transition links inserted: {insertedLinks}");
            summary.AppendLine($"link candidates rejected: {rejectedLinks}");
            summary.AppendLine($"closed Shell layers spiralized/rejected: {spiralized}/{rejectedSpirals}");
            summary.AppendLine($"Shell layer branches merged into continuous branches: {mergedShellBranches}");
            if (mode == 2 || mode == 3)
            {
                summary.AppendLine($"layers double-seam split: {seamStats.Split} | single-seam rotated: {seamStats.Rotated} | not split: {seamStats.NotSplit}");
                if (mode == 3)
                    summary.AppendLine(
                        $"stacked layers (single alternating seam): {splitLayers.Count} | " +
                        $"forced cross-layer connectors: {forcedStackConnectors} | " +
                        $"connectors rejected by max_slope: {rejectedStackConnectors}");
                summary.AppendLine($"not split reasons: no shell={seamStats.NoShell} | multiple shells={seamStats.MultipleShells} | no infill={seamStats.NoInfill} | degenerate={seamStats.Degenerate}");
                if (seamStats.Split > 0)
                    summary.AppendLine($"seam separation range: {seamStats.MinSeparation:0.###} to {seamStats.MaxSeparation:0.###} model units");
                if (tailLength > 0.0)
                    summary.AppendLine(
                        $"tail_l Shell removal: {tailLength:0.###} model units per junction | " +
                        $"total Shell removed={seamStats.ShellLengthRemoved:0.###} | " +
                        $"paired Transition segments={seamStats.PairedTransitions} | " +
                        $"pair failures={seamStats.PairFailures} | tails clamped={seamStats.TailsClamped}");
                summary.AppendLine($"Shell split/shortening length conservation: max |ΔL|={seamStats.MaxLengthError:0.######} model units");
            }
            if (recalculatedHeightPoints > 0)
            {
                summary.AppendLine(
                    $"slope-derived ramp fraction: {minimumRampFraction:0.###} to {maximumRampFraction:0.###}");
                summary.AppendLine(
                    $"spiral layer_h recalculated: {recalculatedHeightPoints} locations | " +
                    $"range={minimumRecalculatedHeight:0.###} to {maximumRecalculatedHeight:0.###} model units");
            }
            summary.AppendLine($"link_dist={linkDistance:0.###} | max_slope={maxSlope:0.###}°");
            totalWatch.Stop();
            summary.AppendLine(mode == 1
                ? "The first Shell layer remains planar. Original semantic roles were retained; new connector branches are Transition."
                : "Original semantic roles were retained; new connector branches are Transition.");
            summary.Append($"performance [ms]: total={totalWatch.Elapsed.TotalMilliseconds:0.###}");

            WasperPathDebugOutputs.Set(da, this, result, summary.ToString());
            int strokesIndex = WasperPathDebugOutputs.OutputIndex(this, "strokes");
            if (strokesIndex >= 0) da.SetDataTree(strokesIndex, strokeCurves);
            int linksIndex = WasperPathDebugOutputs.OutputIndex(this, "links");
            if (linksIndex >= 0) da.SetDataTree(linksIndex, linkCurves);
            int idsIndex = WasperPathDebugOutputs.OutputIndex(this, "stroke_id");
            if (idsIndex >= 0) da.SetDataTree(idsIndex, result.StrokeIds);
            int seamsIndex = WasperPathDebugOutputs.OutputIndex(this, "seam_pts");
            if (seamsIndex >= 0) da.SetDataTree(seamsIndex, seamPoints);

            if (insertedLinks > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    $"Inserted {insertedLinks} deposited Transition link(s). Inspect the links debug output before generating G-code.");
            if (rejectedLinks > 0 || rejectedSpirals > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Continuity remained best-effort: rejected links={rejectedLinks}, rejected spiral layers={rejectedSpirals}. Multiple strokes were preserved rather than forcing unsafe geometry.");
            if (WasperGcodeTreeUtil.TryGetContinuousShellInteriorWarning(result, out string printabilityWarning))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, printabilityWarning);
            if (recalculatedHeightPoints > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    $"The first Shell layer was kept flat.\n" +
                    $"Recalculated layer_h at {recalculatedHeightPoints} spiral location(s)\n" +
                    $"from the actual preceding-loop separation ({minimumRecalculatedHeight:0.###} to {maximumRecalculatedHeight:0.###} model units).");
            if (mode >= 2 && seamStats.Split + seamStats.Rotated > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "Double seams placed at infill chain ends. Seam positions follow the infill; inspect vertical seam drift and the repeated cold-joint locations in Pp04 before generating G-code.");
            if (mode >= 2 && seamStats.NotSplit > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Mode {mode} left {seamStats.NotSplit} layer(s) unsplit; those layers fall back to ordinary per-layer ordering" +
                    (mode == 3 ? " and interrupt the continuous stack." : "."));
            if (mode >= 2 && seamStats.PairFailures > 0)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"Mode {mode} could not construct {seamStats.PairFailures} matched Shell/Infill shortening pair(s). " +
                    "Inspect seam_pts: successful pairs contain distinct shortened Shell and Infill endpoints.");
            if (mode == 3 && forcedStackConnectors > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    $"Stacked {forcedStackConnectors + 1} layer(s) into one continuous path with a single " +
                    "alternating seam per layer. Each layer enters through the Infill end nearest the previous " +
                    "layer's exit, so seam side and Infill direction alternate. Open input Shells were re-closed " +
                    "by depositing across their input seam gap.");
            if (mode == 3 && rejectedStackConnectors > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"{rejectedStackConnectors} cross-layer connector(s) exceeded max_slope and were not deposited; " +
                    "the stack is interrupted there. Increase max_slope or inspect the seam positions.");
            Message = $"{_versionTag} | {(mode == 0 ? "Layer" : mode == 1 ? "3D" : mode == 2 ? "2D-Infill" : "2D-Stack")} | {strokeCount} stroke(s)";
        }

        public override BoundingBox ClippingBox =>
            WasperPrintPathPreviewSettings.Enabled ? _previewBounds : BoundingBox.Empty;

        public override void DrawViewportWires(IGH_PreviewArgs args)
        {
            base.DrawViewportWires(args);
            if (!WasperPrintPathPreviewSettings.Enabled || _previewPolylines.Count == 0)
                return;

            bool selected = Attributes != null && Attributes.Selected;
            int thickness = WasperPrintPathPreviewSettings.Thickness;
            for (int i = 0; i < _previewPolylines.Count; i++)
            {
                Color color = selected
                    ? args.WireColour
                    : WasperPrintPathPreviewSettings.ResolveColor(_previewRoles[i]);
                args.Display.DrawPolyline(_previewPolylines[i], color, thickness);
            }
        }

        private void ClearPreview()
        {
            _previewPolylines.Clear();
            _previewRoles.Clear();
            _previewBounds = BoundingBox.Empty;
        }

        private void UpdatePreview(WasperPrintPath path)
        {
            if (path?.Points == null)
                return;

            foreach (GH_Path branchPath in path.Points.Paths)
            {
                IList<Point3d> points = path.Points.Branch(branchPath);
                if (points == null || points.Count < 2)
                    continue;

                var polyline = new Polyline(points.Where(point => point.IsValid));
                if (polyline.Count < 2)
                    continue;

                _previewPolylines.Add(polyline);
                _previewRoles.Add(WasperGcodeTreeUtil.PathRoleAt(path.PathRoles, branchPath));
                BoundingBox bounds = polyline.BoundingBox;
                if (bounds.IsValid)
                    _previewBounds.Union(bounds);
            }
        }

        private sealed class DoubleSeamStats
        {
            public int Split;
            public int Rotated;
            public int NotSplit;
            public int NoShell;
            public int MultipleShells;
            public int NoInfill;
            public int Degenerate;
            public int TailsClamped;
            public int PairedTransitions;
            public int PairFailures;
            public double MinSeparation = double.PositiveInfinity;
            public double MaxSeparation;
            public double MaxLengthError;
            public double ShellLengthRemoved;
        }

        private static void SplitLayersForInfillSeams(
            List<BranchRecord> records,
            Point3d reference,
            double tailLength,
            double tolerance,
            DataTree<Point3d> seamPoints,
            DoubleSeamStats stats,
            HashSet<int> splitLayers = null)
        {
            Point3d seed = reference;
            foreach (var group in records.GroupBy(r => r.Layer).OrderBy(g => g.Key).ToList())
            {
                List<BranchRecord> layer = group.ToList();
                List<BranchRecord> shells = layer
                    .Where(r => r.Targeted && r.Role == WasperPathRole.Shell && r.Closed)
                    .ToList();
                List<BranchRecord> infills = layer
                    .Where(r => r.Targeted && r.Role == WasperPathRole.Infill)
                    .ToList();

                if (shells.Count == 0)
                {
                    stats.NotSplit++; stats.NoShell++;
                    continue;
                }
                if (shells.Count > 1)
                {
                    stats.NotSplit++; stats.MultipleShells++;
                    continue;
                }
                if (infills.Count == 0)
                {
                    stats.NotSplit++; stats.NoInfill++;
                    continue;
                }

                BranchRecord shell = shells[0];
                List<BranchRecord> chain = OrderLayer(infills, seed.IsValid ? seed : infills[0].Start);
                BranchRecord first = chain[0];
                BranchRecord last = chain[chain.Count - 1];
                Point3d a = first.Start;
                Point3d b = last.End;
                double tA = shell.ClosestNormalizedParameter(a);
                double tB = shell.ClosestNormalizedParameter(b);
                Point3d seamA = shell.PointAt(tA);
                Point3d seamB = shell.PointAt(tB);
                double dt = Math.Abs(tA - tB);
                double separation = Math.Min(dt, 1.0 - dt) * shell.Length;
                double meanSegment = shell.Length / Math.Max(1, shell.Planes.Count - 1);
                double minimumSeparation = Math.Max(tolerance, meanSegment);

                GH_Path seamPath = new GH_Path(Math.Max(0, group.Key));
                if (separation <= minimumSeparation)
                {
                    BranchRecord rotated = shell.RotateClosedLoopSeamTo(tA, tolerance);
                    if (rotated != null)
                    {
                        int shellIndex = records.IndexOf(shell);
                        records.Remove(shell);
                        records.Insert(Math.Max(0, shellIndex), rotated);
                        seamPoints.Add(seamA, seamPath);
                        stats.Rotated++;
                    }
                    else
                    {
                        stats.NotSplit++; stats.Degenerate++;
                    }
                    seed = seamA;
                    continue;
                }

                double originalLength = shell.Length;
                if (!shell.SplitClosedShellAt(tA, tB, tolerance, out BranchRecord arcAB, out BranchRecord arcBA))
                {
                    stats.NotSplit++; stats.Degenerate++;
                    continue;
                }
                Point3d shellJunctionA = seamA;
                Point3d infillJunctionA = a;
                Point3d shellJunctionB = seamB;
                Point3d infillJunctionB = b;
                double removedShellLength = 0.0;
                bool pairedA = false;
                bool pairedB = false;
                if (tailLength > tolerance)
                {
                    Vector3d normal = shell.Planes[0].ZAxis;
                    double infillAvailableA = first.ProjectedLength(normal);
                    double infillAvailableB = last.ProjectedLength(normal);
                    double requestA = MatchedTailLength(
                        tailLength,
                        arcBA.ProjectedLength(normal),
                        infillAvailableA,
                        tolerance);
                    double requestB = MatchedTailLength(
                        tailLength,
                        arcAB.ProjectedLength(normal),
                        infillAvailableB,
                        tolerance);

                    if (ReferenceEquals(first, last))
                    {
                        double combinedAvailable = Math.Max(
                            0.0,
                            first.ProjectedLength(normal) - 2.0 * tolerance);
                        double requestedCombined = requestA + requestB;
                        if (requestedCombined > combinedAvailable && requestedCombined > tolerance)
                        {
                            double scale = combinedAvailable / requestedCombined;
                            requestA *= scale;
                            requestB *= scale;
                        }
                    }
                    if (requestA + tolerance < tailLength) stats.TailsClamped++;
                    if (requestB + tolerance < tailLength) stats.TailsClamped++;

                    double beforeA = arcBA.Length;
                    BranchRecord removedShellA = requestA > tolerance
                        ? arcBA.TrimProjectedFromEnd(requestA, normal, tolerance, out _)
                        : null;
                    BranchRecord removedInfillA = requestA > tolerance
                        ? first.TrimProjectedFromStart(requestA, normal, tolerance, out _)
                        : null;
                    if (removedShellA != null && removedInfillA != null)
                    {
                        removedShellLength += Math.Max(0.0, beforeA - arcBA.Length);
                        shellJunctionA = arcBA.End;
                        infillJunctionA = first.Start;
                        pairedA = true;
                    }
                    else if (requestA > tolerance)
                    {
                        stats.PairFailures++;
                    }

                    double beforeB = arcAB.Length;
                    BranchRecord removedShellB = requestB > tolerance
                        ? arcAB.TrimProjectedFromEnd(requestB, normal, tolerance, out _)
                        : null;
                    BranchRecord removedInfillB = requestB > tolerance
                        ? last.TrimProjectedFromEnd(requestB, normal, tolerance, out _)
                        : null;
                    if (removedShellB != null && removedInfillB != null)
                    {
                        removedShellLength += Math.Max(0.0, beforeB - arcAB.Length);
                        shellJunctionB = arcAB.End;
                        infillJunctionB = last.End;
                        pairedB = true;
                    }
                    else if (requestB > tolerance)
                    {
                        stats.PairFailures++;
                    }
                }

                stats.ShellLengthRemoved += removedShellLength;
                stats.MaxLengthError = Math.Max(
                    stats.MaxLengthError,
                    Math.Abs((arcAB.Length + arcBA.Length + removedShellLength) - originalLength));

                // Do not rely on distance ties here. Without tails, arcAB ends
                // exactly where arcBA starts, so a purely greedy pass can close
                // the Shell before visiting the infill. Freeze the intended
                // double-seam subsequence explicitly.
                int forced = 0;
                arcAB.ForcedOrder = forced++;
                BranchRecord transitionB = null;
                BranchRecord transitionA = null;
                for (int i = chain.Count - 1; i >= 0; i--)
                {
                    chain[i].Reverse();
                }
                if (pairedB)
                {
                    transitionB = BranchRecord.Transition(
                        arcAB,
                        chain[chain.Count - 1],
                        shell.Layer);
                    transitionB.ForcedOrder = forced++;
                    stats.PairedTransitions++;
                }
                for (int i = chain.Count - 1; i >= 0; i--)
                    chain[i].ForcedOrder = forced++;
                arcBA.Reverse();
                if (pairedA)
                {
                    transitionA = BranchRecord.Transition(
                        chain[0],
                        arcBA,
                        shell.Layer);
                    transitionA.ForcedOrder = forced++;
                    stats.PairedTransitions++;
                }
                arcBA.ForcedOrder = forced++;

                int replaceIndex = records.IndexOf(shell);
                records.Remove(shell);
                replaceIndex = Math.Max(0, replaceIndex);
                records.Insert(replaceIndex, arcAB);
                int insertIndex = replaceIndex + 1;
                if (transitionB != null)
                    records.Insert(insertIndex++, transitionB);
                if (transitionA != null)
                    records.Insert(insertIndex++, transitionA);
                records.Insert(insertIndex, arcBA);
                seamPoints.Add(seamA, seamPath);
                seamPoints.Add(seamB, seamPath);
                if (tailLength > tolerance)
                {
                    seamPoints.Add(shellJunctionA, seamPath);
                    seamPoints.Add(infillJunctionA, seamPath);
                    seamPoints.Add(shellJunctionB, seamPath);
                    seamPoints.Add(infillJunctionB, seamPath);
                }
                stats.Split++;
                splitLayers?.Add(group.Key);
                stats.MinSeparation = Math.Min(stats.MinSeparation, separation);
                stats.MaxSeparation = Math.Max(stats.MaxSeparation, separation);
                seed = seamB;
            }
        }

        /// <summary>
        /// Mode 3: rebuilds each eligible layer with a single alternating seam
        /// and chains the layers into one continuous stack.
        ///
        /// Unlike Mode 2 (two fixed seams, one at each Infill chain end), each
        /// stacked layer gets ONE seam, placed at the Infill end nearest the
        /// previous layer's exit. The Shell is treated as a loop (open input
        /// Shells, such as In10 X-seam paths, are re-closed by depositing
        /// across their input gap), the loop seam is rotated to that point,
        /// tail_l shortens both re-seamed loop ends and the Infill's near end,
        /// and the forced layer order becomes Shell loop, Transition, Infill
        /// toward the far end. The layer exits at the opposite Infill end from
        /// where it entered, so seam side and Infill direction alternate per
        /// layer automatically and every element is printed exactly once with
        /// no travel inside the layer.
        /// </summary>
        private static void BuildStackedSingleSeamLayers(
            List<BranchRecord> records,
            Point3d reference,
            double tailLength,
            double tolerance,
            DataTree<Point3d> seamPoints,
            DoubleSeamStats stats,
            HashSet<int> stackedLayers)
        {
            Point3d entry = reference;
            foreach (var group in records.GroupBy(r => r.Layer).OrderBy(g => g.Key).ToList())
            {
                List<BranchRecord> layer = group.ToList();
                List<BranchRecord> shells = layer
                    .Where(r => r.Targeted && r.Role == WasperPathRole.Shell)
                    .ToList();
                List<BranchRecord> infills = layer
                    .Where(r => r.Targeted && r.Role == WasperPathRole.Infill)
                    .ToList();

                if (shells.Count == 0)
                {
                    stats.NotSplit++; stats.NoShell++;
                    continue;
                }
                if (shells.Count > 1)
                {
                    stats.NotSplit++; stats.MultipleShells++;
                    continue;
                }
                if (infills.Count == 0)
                {
                    stats.NotSplit++; stats.NoInfill++;
                    continue;
                }

                BranchRecord shell = shells[0];

                // Order the Infill chain, then orient it so the chain STARTS at
                // the end nearest the previous layer's exit. The layer will
                // then exit at the opposite end, alternating the stack.
                List<BranchRecord> chain = OrderLayer(
                    infills,
                    entry.IsValid ? entry : infills[0].Start);
                if (entry.IsValid &&
                    entry.DistanceTo(chain[chain.Count - 1].End) + tolerance <
                    entry.DistanceTo(chain[0].Start))
                {
                    foreach (BranchRecord piece in chain)
                        piece.Reverse();
                    chain.Reverse();
                }

                // Re-close open input Shells (deposits across the input seam
                // gap) so the seam can be relocated freely.
                BranchRecord loopSource = shell.Closed ? shell : shell.CloseLoop(tolerance);
                if (loopSource == null)
                {
                    stats.NotSplit++; stats.Degenerate++;
                    continue;
                }

                double tSeam = loopSource.ClosestNormalizedParameter(chain[0].Start);
                BranchRecord rotated = loopSource.RotateClosedLoopSeamTo(tSeam, tolerance);
                if (rotated == null)
                {
                    stats.NotSplit++; stats.Degenerate++;
                    continue;
                }
                Point3d seam = rotated.Start;

                // Tail shortening: the exit-side loop end is paired with the
                // Infill's near end (Mode 2 semantics); the entry-side loop end
                // is trimmed alone, its transition being the incoming
                // cross-layer connector.
                if (tailLength > tolerance)
                {
                    Vector3d normal = rotated.Planes[0].ZAxis;
                    double requestExit = MatchedTailLength(
                        tailLength,
                        rotated.ProjectedLength(normal) * 0.5,
                        chain[0].ProjectedLength(normal),
                        tolerance);
                    BranchRecord removedShell = requestExit > tolerance
                        ? rotated.TrimProjectedFromEnd(requestExit, normal, tolerance, out _)
                        : null;
                    BranchRecord removedInfill = requestExit > tolerance
                        ? chain[0].TrimProjectedFromStart(requestExit, normal, tolerance, out _)
                        : null;
                    if (removedShell != null && removedInfill != null)
                    {
                        stats.PairedTransitions++;
                        stats.ShellLengthRemoved += requestExit;
                    }
                    else if (requestExit > tolerance)
                    {
                        stats.PairFailures++;
                    }
                    if (requestExit + tolerance < tailLength) stats.TailsClamped++;

                    double requestEntry = MatchedTailLength(
                        tailLength,
                        rotated.ProjectedLength(normal) * 0.5,
                        double.MaxValue,
                        tolerance);
                    if (requestEntry > tolerance)
                    {
                        rotated.TrimProjectedFromStart(requestEntry, normal, tolerance, out _);
                        stats.ShellLengthRemoved += requestEntry;
                    }
                }

                // Freeze the layer order: Shell loop, Transition, Infill chain.
                int forced = 0;
                rotated.ForcedOrder = forced++;
                BranchRecord transition = null;
                if (rotated.End.DistanceTo(chain[0].Start) > tolerance)
                {
                    transition = BranchRecord.Transition(rotated, chain[0], shell.Layer);
                    transition.ForcedOrder = forced++;
                }
                foreach (BranchRecord piece in chain)
                    piece.ForcedOrder = forced++;

                int replaceIndex = records.IndexOf(shell);
                records.Remove(shell);
                replaceIndex = Math.Max(0, replaceIndex);
                records.Insert(replaceIndex, rotated);
                if (transition != null)
                    records.Insert(replaceIndex + 1, transition);

                GH_Path seamPath = new GH_Path(Math.Max(0, group.Key));
                seamPoints.Add(seam, seamPath);
                if (tailLength > tolerance)
                {
                    seamPoints.Add(rotated.Start, seamPath);
                    seamPoints.Add(rotated.End, seamPath);
                    seamPoints.Add(chain[0].Start, seamPath);
                }

                stats.Rotated++;
                stackedLayers.Add(group.Key);
                entry = chain[chain.Count - 1].End;
            }
        }

        private static double MatchedTailLength(
            double requested,
            double shellAvailable,
            double infillAvailable,
            double tolerance)
        {
            double shellLimit = Math.Max(0.0, shellAvailable - tolerance);
            double infillLimit = Math.Max(0.0, infillAvailable - tolerance);
            return Math.Max(0.0, Math.Min(requested, Math.Min(shellLimit, infillLimit)));
        }

        private static List<BranchRecord> OrderLayer(List<BranchRecord> input, Point3d startPreference)
        {
            var remaining = input.Where(record => record.Targeted).ToList();
            if (remaining.Count == 0)
                return input;

            // Non-targeted branches are fixed anchors. Only eligible slots are
            // filled from the greedy ordering, so role filtering never silently
            // reorders or reverses excluded geometry.
            var orderedTargets = new List<BranchRecord>(remaining.Count);
            var ordered = new List<BranchRecord>(input.Count);
            Point3d current = startPreference.IsValid
                ? startPreference
                : remaining[0].Start;

            List<BranchRecord> forced = remaining
                .Where(record => record.ForcedOrder >= 0)
                .OrderBy(record => record.ForcedOrder)
                .ToList();
            if (forced.Count > 0)
            {
                orderedTargets.AddRange(forced);
                foreach (BranchRecord record in forced) remaining.Remove(record);
                current = forced[forced.Count - 1].End;
            }

            while (remaining.Count > 0)
            {
                BranchRecord best = null;
                bool reverse = false;
                double bestDistance = double.PositiveInfinity;
                foreach (BranchRecord candidate in remaining)
                {
                    double startDistance = current.DistanceToSquared(candidate.Start);
                    if (startDistance < bestDistance)
                    {
                        bestDistance = startDistance;
                        best = candidate;
                        reverse = false;
                    }
                    if (!candidate.Closed)
                    {
                        double endDistance = current.DistanceToSquared(candidate.End);
                        if (endDistance < bestDistance)
                        {
                            bestDistance = endDistance;
                            best = candidate;
                            reverse = true;
                        }
                    }
                }
                remaining.Remove(best);
                if (reverse) best.Reverse();
                orderedTargets.Add(best);
                current = best.End;
            }
            int targetIndex = 0;
            foreach (BranchRecord record in input)
                ordered.Add(record.Targeted ? orderedTargets[targetIndex++] : record);
            return ordered;
        }

        private static void SpiralizeCompatibleShellStack(
            List<BranchRecord> records,
            double maxSlope,
            double tolerance,
            ref int accepted,
            ref int rejected,
            ref int recalculatedHeightPoints,
            ref double minimumRecalculatedHeight,
            ref double maximumRecalculatedHeight,
            ref double minimumRampFraction,
            ref double maximumRampFraction)
        {
            List<IGrouping<int, BranchRecord>> layers = records
                .GroupBy(record => record.Layer)
                .OrderBy(group => group.Key)
                .ToList();

            var acceptedLayers = new HashSet<int>();

            // Build from the top down. This leaves all lower source loops intact
            // while their upper neighbour is generated. The first layer is never
            // modified and therefore remains a complete flat foundation loop.
            for (int i = layers.Count - 1; i >= 1; i--)
            {
                List<BranchRecord> lowerLayer = layers[i - 1].Where(r => r.Targeted).ToList();
                List<BranchRecord> upperLayer = layers[i].Where(r => r.Targeted).ToList();
                if (lowerLayer.Count != 1 || upperLayer.Count != 1)
                    continue;
                BranchRecord lower = lowerLayer[0];
                BranchRecord upper = upperLayer[0];
                if (!lower.Closed || !upper.Closed ||
                    lower.Role != WasperPathRole.Shell || upper.Role != WasperPathRole.Shell)
                    continue;

                double rampFraction = RampFractionForSlope(lower, upper, maxSlope, tolerance);
                if (!double.IsFinite(rampFraction) || rampFraction > 1.0 + 1e-9)
                {
                    rejected++;
                    continue;
                }
                rampFraction = Math.Max(1.0 / Math.Max(2, upper.Planes.Count - 1), rampFraction);
                rampFraction = Math.Min(1.0, rampFraction);

                List<Plane> candidate = upper.SpiralCandidateFrom(lower, rampFraction);
                if (candidate == null || candidate.Count < 2 || MaxSlope(candidate) > maxSlope + 1e-9)
                {
                    rejected++;
                    continue;
                }
                upper.Planes = candidate;
                upper.Closed = false;
                upper.Spiralized = true;
                acceptedLayers.Add(i);
                minimumRampFraction = Math.Min(minimumRampFraction, rampFraction);
                maximumRampFraction = Math.Max(maximumRampFraction, rampFraction);
                accepted++;
            }

            // Once all ramped geometry is known, measure every accepted loop
            // against the actual preceding output loop. This is essential for
            // the first ramp, whose layer height grows gradually from the flat
            // foundation to the nominal upper-layer separation.
            for (int i = 1; i < layers.Count; i++)
            {
                if (!acceptedLayers.Contains(i))
                    continue;
                BranchRecord lower = layers[i - 1].Where(r => r.Targeted).Single();
                BranchRecord upper = layers[i].Where(r => r.Targeted).Single();
                upper.RecalculateLayerHeightFrom(
                    lower, tolerance,
                    ref recalculatedHeightPoints,
                    ref minimumRecalculatedHeight,
                    ref maximumRecalculatedHeight);
            }
        }

        private static int MergeSpiralizedShellChains(
            List<BranchRecord> records,
            double tolerance)
        {
            List<BranchRecord> shells = records
                .Where(record => record.Targeted && record.Role == WasperPathRole.Shell)
                .OrderBy(record => record.Layer)
                .ToList();
            if (shells.Count < 2)
                return 0;

            var chains = new List<List<BranchRecord>>();
            int index = 0;
            while (index < shells.Count - 1)
            {
                if (!shells[index + 1].Spiralized ||
                    shells[index + 1].Layer != shells[index].Layer + 1)
                {
                    index++;
                    continue;
                }

                var chain = new List<BranchRecord> { shells[index] };
                int cursor = index + 1;
                while (cursor < shells.Count &&
                       shells[cursor].Spiralized &&
                       shells[cursor].Layer == chain[chain.Count - 1].Layer + 1)
                {
                    chain.Add(shells[cursor]);
                    cursor++;
                }
                chains.Add(chain);
                index = cursor;
            }

            int removedBranches = 0;
            foreach (List<BranchRecord> chain in chains)
            {
                BranchRecord merged = BranchRecord.MergeContinuousShell(chain, tolerance);
                if (merged == null)
                    continue;
                foreach (BranchRecord record in chain)
                    records.Remove(record);
                records.Add(merged);
                removedBranches += chain.Count - 1;
            }
            return removedBranches;
        }

        private static double RampFractionForSlope(
            BranchRecord lower,
            BranchRecord upper,
            double maxSlope,
            double tolerance)
        {
            double perimeter = upper.Length;
            if (perimeter <= tolerance)
                return double.PositiveInfinity;

            double rise = upper.RepresentativeNormalGapFrom(lower);
            if (rise <= tolerance)
                return 1.0 / Math.Max(2, upper.Planes.Count - 1);

            double tangent = Math.Tan(RhinoMath.ToRadians(maxSlope));
            if (!double.IsFinite(tangent) || tangent <= 0.0)
                return double.PositiveInfinity;

            return (rise / tangent) / perimeter;
        }

        private static double MaxSlope(IList<Plane> planes)
        {
            double maximum = 0.0;
            for (int i = 1; i < planes.Count; i++)
                maximum = Math.Max(maximum, LinkSlopeDegrees(planes[i - 1].Origin, planes[i].Origin));
            return maximum;
        }

        private static double LinkSlopeDegrees(Point3d a, Point3d b)
        {
            double horizontal = Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
            return RhinoMath.ToDegrees(Math.Atan2(Math.Abs(b.Z - a.Z), horizontal));
        }

        private static void BuildTrees(
            List<BranchRecord> ordered,
            WasperPrintPath source,
            int prefixLength,
            bool hasCrossLayerShellContinuity,
            out WasperPrintPath result,
            out DataTree<Curve> strokeCurves,
            out DataTree<Curve> linkCurves)
        {
            var planes = new DataTree<Plane>();
            var flows = new DataTree<double>();
            var heights = new DataTree<double>();
            var widths = new DataTree<double>();
            var effectiveWidths = new DataTree<double>();
            var printVolumes = new DataTree<double>();
            var speeds = new DataTree<double>();
            var roles = new DataTree<int>();
            var strokeIds = new DataTree<int>();
            var curves = new DataTree<Curve>();
            strokeCurves = new DataTree<Curve>();
            linkCurves = new DataTree<Curve>();
            var orderByLayer = new Dictionary<string, int>();

            foreach (BranchRecord record in ordered)
            {
                GH_Path layerPath = record.SourcePath != null
                    ? WasperGcodeTreeUtil.LayerPlanePath(record.SourcePath, prefixLength)
                    : new GH_Path(Math.Max(0, record.Layer));
                string key = layerPath.ToString();
                int order = orderByLayer.TryGetValue(key, out int existing) ? existing : 0;
                orderByLayer[key] = order + 1;
                GH_Path outputPath = new GH_Path(layerPath.Indices.Concat(new[] { order }).ToArray());

                planes.AddRange(record.Planes, outputPath);
                flows.AddRange(record.Flows, outputPath);
                heights.AddRange(record.LayerH, outputPath);
                widths.AddRange(record.LayerW, outputPath);
                effectiveWidths.AddRange(record.LayerWf, outputPath);
                if (record.PrintVol != null) printVolumes.AddRange(record.PrintVol, outputPath);
                if (record.PrintSpeed != null) speeds.AddRange(record.PrintSpeed, outputPath);
                roles.Add((int)record.Role, outputPath);
                strokeIds.Add(record.StrokeId, outputPath);
                Curve curve = new PolylineCurve(record.Planes.Select(plane => plane.Origin));
                curves.Add(curve, outputPath);
                strokeCurves.Add(curve, outputPath);
                if (record.Role == WasperPathRole.Transition)
                    linkCurves.Add(curve, outputPath);
            }

            result = new WasperPrintPath(
                points: null,
                ptPlanes: planes,
                flows: flows,
                layerH: heights,
                printSpeed: speeds.DataCount > 0 ? speeds : null,
                nozzleDiam: source.NozzleDiam,
                layerW: widths,
                layerWf: effectiveWidths,
                printVol: printVolumes.DataCount > 0 ? printVolumes : null,
                isPartial: source.IsPartial,
                sourceCurves: curves,
                pathRoles: roles,
                layerPlanes: source.LayerPlanes,
                strokeIds: strokeIds,
                hasCrossLayerShellContinuity: hasCrossLayerShellContinuity);
        }

        private sealed class BranchRecord
        {
            public GH_Path SourcePath;
            public int Layer;
            public WasperPathRole Role;
            public bool Targeted;
            public bool Closed;
            public bool Spiralized;
            public int ForcedOrder = -1;
            public int StrokeId;
            public List<Plane> Planes;
            public List<double> Flows;
            public List<double> LayerH;
            public List<double> LayerW;
            public List<double> LayerWf;
            public List<double> PrintVol;
            public List<double> PrintSpeed;

            public Point3d Start => Planes[0].Origin;
            public Point3d End => Planes[Planes.Count - 1].Origin;
            public double Length
            {
                get
                {
                    double length = 0.0;
                    for (int i = 1; i < Planes.Count; i++)
                        length += Planes[i - 1].Origin.DistanceTo(Planes[i].Origin);
                    return length;
                }
            }

            public static BranchRecord From(
                WasperPrintPath source,
                GH_Path path,
                IList<Plane> planes,
                WasperPathRole role,
                int layer,
                bool targeted)
            {
                int count = planes.Count;
                return new BranchRecord
                {
                    SourcePath = path,
                    Layer = layer,
                    Role = role,
                    Targeted = targeted,
                    Closed = planes[0].Origin.DistanceToSquared(planes[count - 1].Origin) <= 1e-12,
                    Planes = planes.ToList(),
                    Flows = Values(source.Flows, path, count, 1.0),
                    LayerH = Values(source.LayerH, path, count, 1.0),
                    LayerW = Values(source.LayerW, path, count, double.NaN),
                    LayerWf = Values(source.LayerWf, path, count, double.NaN),
                    PrintVol = source.PrintVol == null ? null : Values(source.PrintVol, path, count, double.NaN),
                    PrintSpeed = source.PrintSpeed == null ? null : Values(source.PrintSpeed, path, count, double.NaN)
                }.ResolveWidthFallbacks();
            }

            private BranchRecord ResolveWidthFallbacks()
            {
                for (int i = 0; i < Planes.Count; i++)
                {
                    if (!double.IsFinite(LayerW[i]) || LayerW[i] <= 0.0)
                        LayerW[i] = Math.Max(1e-9, LayerH[i] * 2.5);
                    if (!double.IsFinite(LayerWf[i]) || LayerWf[i] <= 0.0)
                        LayerWf[i] = LayerW[i];
                }
                return this;
            }

            public static BranchRecord MergeContinuousShell(
                IList<BranchRecord> chain,
                double tolerance)
            {
                if (chain == null || chain.Count < 2)
                    return null;

                var merged = new BranchRecord
                {
                    SourcePath = chain[0].SourcePath,
                    Layer = chain[0].Layer,
                    Role = WasperPathRole.Shell,
                    Targeted = true,
                    Closed = false,
                    Spiralized = true,
                    Planes = new List<Plane>(),
                    Flows = new List<double>(),
                    LayerH = new List<double>(),
                    LayerW = new List<double>(),
                    LayerWf = new List<double>(),
                    PrintVol = chain.All(record => record.PrintVol != null)
                        ? new List<double>()
                        : null,
                    PrintSpeed = chain.All(record => record.PrintSpeed != null)
                        ? new List<double>()
                        : null
                };

                for (int branchIndex = 0; branchIndex < chain.Count; branchIndex++)
                {
                    BranchRecord record = chain[branchIndex];
                    int start = 0;
                    if (merged.Planes.Count > 0 && record.Planes.Count > 0 &&
                        merged.Planes[merged.Planes.Count - 1].Origin.DistanceToSquared(record.Planes[0].Origin) <= tolerance * tolerance)
                    {
                        start = 1;
                    }

                    AppendFrom(merged.Planes, record.Planes, start);
                    AppendFrom(merged.Flows, record.Flows, start);
                    AppendFrom(merged.LayerH, record.LayerH, start);
                    AppendFrom(merged.LayerW, record.LayerW, start);
                    AppendFrom(merged.LayerWf, record.LayerWf, start);
                    if (merged.PrintVol != null) AppendFrom(merged.PrintVol, record.PrintVol, start);
                    if (merged.PrintSpeed != null) AppendFrom(merged.PrintSpeed, record.PrintSpeed, start);
                }

                return merged.Planes.Count >= 2 ? merged : null;
            }

            private static void AppendFrom<T>(
                List<T> target,
                IList<T> source,
                int start)
            {
                if (target == null || source == null)
                    return;
                for (int i = Math.Max(0, start); i < source.Count; i++)
                    target.Add(source[i]);
            }

            public static BranchRecord Transition(BranchRecord previous, BranchRecord next, int layer)
            {
                Vector3d tangent = next.Start - previous.End;
                if (!tangent.Unitize()) tangent = previous.Planes.Last().XAxis;
                Vector3d z = previous.Planes.Last().ZAxis + next.Planes.First().ZAxis;
                if (!z.Unitize()) z = Vector3d.ZAxis;
                Vector3d y = Vector3d.CrossProduct(z, tangent);
                if (!y.Unitize()) y = previous.Planes.Last().YAxis;
                Vector3d x = Vector3d.CrossProduct(y, z);
                if (!x.Unitize()) x = tangent;
                Plane a = new Plane(previous.End, x, y);
                Plane b = new Plane(next.Start, x, y);
                return new BranchRecord
                {
                    SourcePath = next.SourcePath,
                    Layer = layer,
                    Role = WasperPathRole.Transition,
                    Targeted = true,
                    Closed = false,
                    Planes = new List<Plane> { a, b },
                    Flows = Pair(previous.Flows.Last(), next.Flows.First()),
                    LayerH = Pair(previous.LayerH.Last(), next.LayerH.First()),
                    LayerW = Pair(previous.LayerW.Last(), next.LayerW.First()),
                    LayerWf = Pair(previous.LayerWf.Last(), next.LayerWf.First()),
                    PrintVol = previous.PrintVol == null || next.PrintVol == null
                        ? null
                        : Pair(
                            0.0,
                            0.5 * (previous.LayerWf.Last() + next.LayerWf.First()) *
                            0.5 * (previous.LayerH.Last() + next.LayerH.First()) *
                            previous.End.DistanceTo(next.Start)),
                    PrintSpeed = previous.PrintSpeed == null || next.PrintSpeed == null
                        ? null
                        : Pair(previous.PrintSpeed.Last(), next.PrintSpeed.First())
                };
            }

            public double ClosestNormalizedParameter(Point3d point)
            {
                if (Planes.Count < 2 || Length <= 1e-12) return 0.0;
                double bestDistance = double.PositiveInfinity;
                double bestAlong = 0.0;
                double accumulated = 0.0;
                for (int i = 1; i < Planes.Count; i++)
                {
                    Point3d p0 = Planes[i - 1].Origin;
                    Vector3d d = Planes[i].Origin - p0;
                    double segmentLength = d.Length;
                    if (segmentLength <= 1e-12) continue;
                    double u = ((point - p0) * d) / (segmentLength * segmentLength);
                    u = Math.Max(0.0, Math.Min(1.0, u));
                    Point3d q = p0 + d * u;
                    double distance = point.DistanceToSquared(q);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestAlong = accumulated + u * segmentLength;
                    }
                    accumulated += segmentLength;
                }
                return accumulated <= 1e-12 ? 0.0 : bestAlong / accumulated;
            }

            public Point3d PointAt(double t) => PointAtNormalized(Planes, t);

            public bool SplitClosedShellAt(
                double tA, double tB, double tolerance,
                out BranchRecord arcAB, out BranchRecord arcBA)
            {
                arcAB = null;
                arcBA = null;
                if (!Closed || Planes.Count < 4) return false;
                tA = Wrap01(tA);
                tB = Wrap01(tB);
                if (Math.Min(Math.Abs(tA - tB), 1.0 - Math.Abs(tA - tB)) * Length <= tolerance)
                    return false;
                arcAB = ExtractCyclicArc(tA, tB);
                arcBA = ExtractCyclicArc(tB, tA);
                return arcAB != null && arcBA != null;
            }

            public BranchRecord RotateClosedLoopSeamTo(double t, double tolerance)
            {
                if (!Closed || Planes.Count < 4 || Length <= tolerance) return null;
                BranchRecord rotated = ExtractCyclicArc(Wrap01(t), Wrap01(t));
                if (rotated != null) rotated.Closed = false;
                return rotated;
            }

            /// <summary>
            /// Returns a closed copy of this open branch by appending the first
            /// vertex at the end (a deposited segment across the input seam
            /// gap, e.g. an In10 X-seam opening). Null when degenerate.
            /// </summary>
            public BranchRecord CloseLoop(double tolerance)
            {
                if (Planes.Count < 3) return null;
                var samples = new List<Sample>(Planes.Count + 1);
                for (int i = 0; i < Planes.Count; i++)
                    samples.Add(SampleAtVertex(i));
                if (Start.DistanceTo(End) > tolerance)
                    samples.Add(SampleAtVertex(0));
                return FromSamples(samples, true, false);
            }

            private BranchRecord ExtractCyclicArc(double start, double end)
            {
                double[] parameters = NormalizedParameters(Planes);
                double endUnwrapped = end;
                if (endUnwrapped <= start + 1e-12) endUnwrapped += 1.0;
                // Equal parameters mean a requested full turn (single-seam rotation).
                if (Math.Abs(end - start) <= 1e-12) endUnwrapped = start + 1.0;

                var samples = new List<Sample> { SampleAtNormalized(start) };
                var interior = new List<Tuple<double, int>>();
                for (int i = 1; i < Planes.Count - 1; i++)
                {
                    double u = parameters[i];
                    while (u <= start + 1e-12) u += 1.0;
                    if (u < endUnwrapped - 1e-12)
                        interior.Add(Tuple.Create(u, i));
                }
                foreach (var item in interior.OrderBy(x => x.Item1))
                    samples.Add(SampleAtVertex(item.Item2));
                samples.Add(SampleAtNormalized(Wrap01(endUnwrapped)));
                return FromSamples(samples, false, false);
            }

            public double ProjectedLength(Vector3d normal)
            {
                if (!normal.Unitize()) normal = Vector3d.ZAxis;
                double length = 0.0;
                for (int i = 1; i < Planes.Count; i++)
                {
                    Vector3d d = Planes[i].Origin - Planes[i - 1].Origin;
                    double axial = d * normal;
                    length += Math.Sqrt(Math.Max(0.0, d.SquareLength - axial * axial));
                }
                return length;
            }

            public BranchRecord TrimProjectedFromStart(
                double requested, Vector3d normal, double tolerance, out bool clamped)
            {
                clamped = false;
                if (requested <= tolerance || Planes.Count < 2) return null;
                if (!normal.Unitize()) normal = Vector3d.ZAxis;
                double available = ProjectedLength(normal);
                double target = Math.Min(requested, Math.Max(0.0, available - tolerance));
                clamped = target + tolerance < requested;
                if (target <= tolerance) return null;

                double accumulated = 0.0;
                int cutSegment = -1;
                double cutU = 0.0;
                for (int i = 1; i < Planes.Count; i++)
                {
                    Vector3d d = Planes[i].Origin - Planes[i - 1].Origin;
                    double axial = d * normal;
                    double projected = Math.Sqrt(Math.Max(0.0, d.SquareLength - axial * axial));
                    if (accumulated + projected >= target - 1e-12 && projected > 1e-12)
                    {
                        cutSegment = i;
                        cutU = Math.Max(0.0, Math.Min(1.0, (target - accumulated) / projected));
                        break;
                    }
                    accumulated += projected;
                }
                if (cutSegment < 1) return null;

                Sample cut = Interpolate(cutSegment - 1, cutSegment, cutU);
                var tailSamples = new List<Sample>();
                for (int i = 0; i < cutSegment; i++) tailSamples.Add(SampleAtVertex(i));
                tailSamples.Add(cut);
                BranchRecord tail = FromSamples(tailSamples, false, false);

                var kept = new List<Sample> { cut };
                for (int i = cutSegment; i < Planes.Count; i++) kept.Add(SampleAtVertex(i));
                ReplaceWithSamples(kept);
                Closed = false;
                Spiralized = false;
                return tail;
            }

            public BranchRecord TrimProjectedFromEnd(
                double requested, Vector3d normal, double tolerance, out bool clamped)
            {
                Reverse();
                BranchRecord tail = TrimProjectedFromStart(requested, normal, tolerance, out clamped);
                Reverse();
                // tail intentionally remains oriented old-end -> pulled point.
                return tail;
            }

            private sealed class Sample
            {
                public Plane Plane;
                public double Flow, H, W, Wf, Speed;
            }

            private Sample SampleAtVertex(int i) => new Sample
            {
                Plane = Planes[i],
                Flow = Flows[i],
                H = LayerH[i],
                W = LayerW[i],
                Wf = LayerWf[i],
                Speed = PrintSpeed == null ? double.NaN : PrintSpeed[i]
            };

            private Sample SampleAtNormalized(double t)
            {
                t = Math.Max(0.0, Math.Min(1.0, t));
                double[] p = NormalizedParameters(Planes);
                if (t <= 1e-12) return SampleAtVertex(0);
                if (t >= 1.0 - 1e-12) return SampleAtVertex(Planes.Count - 1);
                int i = 1;
                while (i < p.Length && p[i] < t) i++;
                i = Math.Min(i, p.Length - 1);
                double span = p[i] - p[i - 1];
                double u = span > 1e-12 ? (t - p[i - 1]) / span : 0.0;
                return Interpolate(i - 1, i, u);
            }

            private Sample Interpolate(int a, int b, double u)
            {
                u = Math.Max(0.0, Math.Min(1.0, u));
                Plane plane = Planes[a];
                plane.Origin = Planes[a].Origin + (Planes[b].Origin - Planes[a].Origin) * u;
                return new Sample
                {
                    Plane = plane,
                    Flow = Lerp(Flows[a], Flows[b], u),
                    H = Lerp(LayerH[a], LayerH[b], u),
                    W = Lerp(LayerW[a], LayerW[b], u),
                    Wf = Lerp(LayerWf[a], LayerWf[b], u),
                    Speed = PrintSpeed == null ? double.NaN : Lerp(PrintSpeed[a], PrintSpeed[b], u)
                };
            }

            private BranchRecord FromSamples(IList<Sample> samples, bool closed, bool spiralized)
            {
                if (samples == null || samples.Count < 2) return null;
                var result = new BranchRecord
                {
                    SourcePath = SourcePath,
                    Layer = Layer,
                    Role = Role,
                    Targeted = Targeted,
                    Closed = closed,
                    Spiralized = spiralized,
                    Planes = samples.Select(s => s.Plane).ToList(),
                    Flows = samples.Select(s => s.Flow).ToList(),
                    LayerH = samples.Select(s => s.H).ToList(),
                    LayerW = samples.Select(s => s.W).ToList(),
                    LayerWf = samples.Select(s => s.Wf).ToList(),
                    PrintVol = PrintVol == null ? null : new List<double>(),
                    PrintSpeed = PrintSpeed == null ? null : samples.Select(s => s.Speed).ToList()
                };
                result.RecalculatePrintVolume();
                return result;
            }

            private void ReplaceWithSamples(IList<Sample> samples)
            {
                Planes = samples.Select(s => s.Plane).ToList();
                Flows = samples.Select(s => s.Flow).ToList();
                LayerH = samples.Select(s => s.H).ToList();
                LayerW = samples.Select(s => s.W).ToList();
                LayerWf = samples.Select(s => s.Wf).ToList();
                if (PrintSpeed != null) PrintSpeed = samples.Select(s => s.Speed).ToList();
                RecalculatePrintVolume();
            }

            private void RecalculatePrintVolume()
            {
                if (PrintVol == null) return;
                PrintVol = new List<double>(Planes.Count) { 0.0 };
                for (int i = 1; i < Planes.Count; i++)
                    PrintVol.Add(LayerWf[i] * LayerH[i] * Planes[i - 1].Origin.DistanceTo(Planes[i].Origin));
            }

            private static double Lerp(double a, double b, double t) => a + (b - a) * t;
            private static double Wrap01(double t)
            {
                t %= 1.0;
                if (t < 0.0) t += 1.0;
                return t;
            }

            public void Reverse()
            {
                Planes.Reverse();
                Flows.Reverse();
                LayerH.Reverse();
                LayerW.Reverse();
                LayerWf.Reverse();
                PrintSpeed?.Reverse();
                RecalculatePrintVolume();
            }

            public List<Plane> SpiralCandidateFrom(BranchRecord lower, double rampFraction)
            {
                if (Planes.Count < 2 || lower == null || lower.Planes.Count < 2)
                    return null;
                var result = new List<Plane>(Planes.Count);
                double[] parameters = NormalizedParameters(Planes);
                for (int i = 0; i < Planes.Count; i++)
                {
                    double t = parameters[i];
                    double rise = Math.Max(0.0, Math.Min(1.0, t / rampFraction));
                    Plane plane = Planes[i];
                    Point3d lowerPoint = PointAtNormalized(lower.Planes, t);
                    plane.Origin = lowerPoint + (plane.Origin - lowerPoint) * rise;
                    result.Add(plane);
                }
                return result;
            }

            public double RepresentativeNormalGapFrom(BranchRecord lower)
            {
                if (lower == null || Planes.Count == 0 || lower.Planes.Count == 0)
                    return 0.0;

                var gaps = new List<double>(Planes.Count);
                double[] parameters = NormalizedParameters(Planes);
                for (int i = 0; i < Planes.Count; i++)
                {
                    double t = parameters[i];
                    Point3d below = PointAtNormalized(lower.Planes, t);
                    Vector3d normal = Planes[i].ZAxis;
                    if (!normal.Unitize()) normal = Vector3d.ZAxis;
                    gaps.Add(Math.Abs((Planes[i].Origin - below) * normal));
                }
                gaps.Sort();
                return gaps[gaps.Count / 2];
            }

            public void RecalculateLayerHeightFrom(
                BranchRecord lower,
                double tolerance,
                ref int count,
                ref double minimum,
                ref double maximum)
            {
                if (lower == null || Planes.Count < 2 || lower.Planes.Count < 2)
                    return;

                var nodeGaps = new double[Planes.Count];
                double[] parameters = NormalizedParameters(Planes);
                for (int i = 0; i < Planes.Count; i++)
                {
                    double t = parameters[i];
                    Point3d below = PointAtNormalized(lower.Planes, t);
                    Vector3d normal = Planes[i].ZAxis;
                    if (!normal.Unitize()) normal = Vector3d.ZAxis;
                    nodeGaps[i] = Math.Abs((Planes[i].Origin - below) * normal);
                }

                var heights = new List<double>(Planes.Count);
                double positiveFloor = Math.Max(tolerance, RhinoMath.ZeroTolerance);
                for (int i = 0; i < Planes.Count; i++)
                {
                    // Gc03 applies height item i to segment i-1 -> i, so use
                    // the mean separation across that deposited segment.
                    double height = i == 0
                        ? 0.5 * (nodeGaps[0] + nodeGaps[1])
                        : 0.5 * (nodeGaps[i - 1] + nodeGaps[i]);
                    height = Math.Max(positiveFloor, height);
                    heights.Add(height);
                    count++;
                    minimum = Math.Min(minimum, height);
                    maximum = Math.Max(maximum, height);
                }
                LayerH = heights;

                if (PrintVol != null)
                {
                    PrintVol = new List<double>(Planes.Count) { 0.0 };
                    for (int i = 1; i < Planes.Count; i++)
                    {
                        double segmentLength = Planes[i - 1].Origin.DistanceTo(Planes[i].Origin);
                        PrintVol.Add(LayerWf[i] * LayerH[i] * segmentLength);
                    }
                }
            }

            private static double[] NormalizedParameters(IList<Plane> planes)
            {
                var parameters = new double[planes.Count];
                double total = 0.0;
                for (int i = 1; i < planes.Count; i++)
                {
                    total += planes[i - 1].Origin.DistanceTo(planes[i].Origin);
                    parameters[i] = total;
                }
                if (total <= 1e-12)
                    return parameters;
                for (int i = 1; i < parameters.Length; i++)
                    parameters[i] /= total;
                return parameters;
            }

            private static Point3d PointAtNormalized(IList<Plane> planes, double t)
            {
                if (t <= 0.0) return planes[0].Origin;
                if (t >= 1.0) return planes[planes.Count - 1].Origin;
                var lengths = new double[planes.Count];
                double total = 0.0;
                for (int i = 1; i < planes.Count; i++)
                {
                    total += planes[i - 1].Origin.DistanceTo(planes[i].Origin);
                    lengths[i] = total;
                }
                if (total <= 1e-12)
                    return planes[0].Origin;
                double target = t * total;
                int segment = 1;
                while (segment < lengths.Length && lengths[segment] < target) segment++;
                segment = Math.Min(segment, lengths.Length - 1);
                double a = lengths[segment - 1];
                double b = lengths[segment];
                double local = b > a ? (target - a) / (b - a) : 0.0;
                Point3d start = planes[segment - 1].Origin;
                return start + (planes[segment].Origin - start) * local;
            }

            private static List<double> Values(DataTree<double> tree, GH_Path path, int count, double fallback)
            {
                IList<double> branch = tree != null && tree.PathExists(path) ? tree.Branch(path) : null;
                var values = new List<double>(count);
                for (int i = 0; i < count; i++)
                {
                    double value = branch == null || branch.Count == 0
                        ? fallback
                        : branch[branch.Count == 1 ? 0 : Math.Min(i, branch.Count - 1)];
                    values.Add(value);
                }
                return values;
            }

            private static List<double> Pair(double a, double b) => new List<double> { a, b };
        }

        private static class ContinuousPathIcon
        {
            public static Bitmap Create()
            {
                var bitmap = new Bitmap(24, 24);
                using Graphics graphics = Graphics.FromImage(bitmap);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                using var shadow = new Pen(Color.FromArgb(38, 75, 88), 4.0f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                };
                using var spring = new Pen(Color.FromArgb(238, 158, 65), 2.2f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round
                };
                PointF[] points =
                {
                    new PointF(4, 3), new PointF(19, 3),
                    new PointF(5, 7), new PointF(19, 10),
                    new PointF(5, 13), new PointF(19, 16),
                    new PointF(5, 20), new PointF(20, 21)
                };
                graphics.DrawCurve(shadow, points, 0.48f);
                graphics.DrawCurve(spring, points, 0.48f);
                return bitmap;
            }
        }
    }
}
