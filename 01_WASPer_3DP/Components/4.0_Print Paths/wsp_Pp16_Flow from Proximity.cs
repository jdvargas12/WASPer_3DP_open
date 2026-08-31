// -----------------------------------------------------------------------------
// wsp_Pp16_Flow from Proximity  — v6  (hybrid)
// -----------------------------------------------------------------------------
// ARCHITECTURE
// -----------------------------------------------------------------------------
//
// Step 1  Project points to layer plane.
//
// Step 2  Compute per-vertex turning angle (pure acos, no arc-length division).
//         Density-independent, globally consistent.
//
// Step 3  Detect bend peaks — NMS (non-maximum suppression):
//         • group contiguous regions where turn >= curv_threshold (radians)
//         • keep the single highest-angle vertex per region
//         ? exactly one peak per geometric bend, regardless of point density
//
// Step 4  Expand each peak into a TURNING ZONE:
//         zone = [ peak - zone_half_width , peak + zone_half_width ]
//         Overlapping zones are merged.
//         These points are EXCLUDED from self-clearance — they are separators,
//         not usable self-comparison geometry.
//
// Step 5  Build RUNS as the index intervals between turning zones.
//         Each run is a stable low-curvature stretch.
//
// Step 6  Self-clearance: for each query point P, evaluate only segments that:
//         (a) belong to a DIFFERENT run than P, AND
//         (b) do NOT touch a turning-zone point.
//         This prevents bend-adjacent geometry from contaminating the signal.
//
// Step 7  Other-clearance: all segments of other curves in the same layer,
//         no exclusion.
//
// Step 8  Smooth self_clearance and other_clearance SEPARATELY (O(n) box filter).
//
// Step 9  Combine: min(self, other) for mode=2, or whichever is selected.
//
// Step 10 Map combined clearance to risk via smooth-step, then to flux.
//
// INPUTS
//   p_points, planes, min_flux, max_flux, d_crit, d_safe,
//   cluster_mode, curv_threshold (rad), zone_half_width (pts)
//
// OUTPUTS
//   flows, clearance, risk, self_clearance, other_clearance, cluster_id
// -----------------------------------------------------------------------------

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

using Rhino.Geometry;
using WASPer_3DP;
#endregion

namespace WASPer_3DP_Components._4_0_Print_Paths
{
    public class wsp_Pp16_Flow_From_Proximity : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Pp16_Flow_From_Proximity()
            : base(
                "wsp_Pp16_Flow from Proximity",
                "ProxFlow",
                "Assigns per-point local flow based on proximity-driven crowding risk.\n\n" +
                "Bend detection: absolute turning-angle threshold (rad) + NMS ? one peak\n" +
                "per bend.  Each peak is expanded into an excluded turning zone.\n" +
                "Self-clearance is evaluated only across run boundaries, excluding zone\n" +
                "geometry.  Self and other clearances are smoothed independently before\n" +
                "combining.\n\n" +
                "v6: hybrid NMS peaks + excluded turning zones + separate smoothing.\n\n" +
                "Please use the Pp01 WASPer Path from Curves before using this component.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "4.0_Print Paths")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("66F1854B-B9C0-4AA7-8730-919DC8B7F7F8");

        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        protected override Bitmap Icon
        {
            get
            {
                using (var s = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Gc06_FlowFromProximity.png"))
                    return s != null ? new Bitmap(s) : null;
            }
        }


        private int _visibleOutputsMask;
        private const string ShowAllOutputsKey = "wsp_gc06_show_all_outputs";
        private const string VisibleOutputsMaskKey = "wsp_gc06_visible_outputs_mask";

        private static readonly string[] OutputCatalog = WasperPathDebugOutputs.CoreNickNames
            .Concat(new[] { "clearance", "risk", "self_clr", "other_clr", "cluster_id" })
            .ToArray();
        private static readonly int AllOutputsMask = (1 << OutputCatalog.Length) - 1;

        private bool IsOutputVisible(string nickName)
        {
            int bit = Array.IndexOf(OutputCatalog, nickName);
            return bit >= 0 && (_visibleOutputsMask & (1 << bit)) != 0;
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
                    RecordUndoEvent("Toggle outputs");
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
            while (Params.Output.Count > 2)
                Params.UnregisterOutputParameter(Params.Output[Params.Output.Count - 1], true);

            if (Params.Output.Count < 2)
            {
                while (Params.Output.Count > 0)
                    Params.UnregisterOutputParameter(Params.Output[Params.Output.Count - 1], true);
                Params.RegisterOutputParam(new Param_GenericObject { Name = "wsp_path", NickName = "wsp_path", Description = "WASPer Print Path carrying updated proximity-based flows.", Access = GH_ParamAccess.item });
                Params.RegisterOutputParam(new Param_String { Name = "summary", NickName = "summary", Description = "Flow/proximity summary.", Access = GH_ParamAccess.item });
            }
            WasperPathDebugOutputs.RegisterCore(this, IsOutputVisible);
            if (IsOutputVisible("clearance"))
                Params.RegisterOutputParam(new Param_Number { Name = "clearance", NickName = "clearance", Description = "Combined clearance.", Access = GH_ParamAccess.tree });
            if (IsOutputVisible("risk"))
                Params.RegisterOutputParam(new Param_Number { Name = "risk", NickName = "risk", Description = "Crowding risk [0,1].", Access = GH_ParamAccess.tree });
            if (IsOutputVisible("self_clr"))
                Params.RegisterOutputParam(new Param_Number { Name = "self_clearance", NickName = "self_clr", Description = "Self clearance.", Access = GH_ParamAccess.tree });
            if (IsOutputVisible("other_clr"))
                Params.RegisterOutputParam(new Param_Number { Name = "other_clearance", NickName = "other_clr", Description = "Other-curve clearance.", Access = GH_ParamAccess.tree });
            if (IsOutputVisible("cluster_id"))
                Params.RegisterOutputParam(new Param_Integer { Name = "cluster_id", NickName = "cluster_id", Description = "Run / turning-zone id.", Access = GH_ParamAccess.tree });
            Params.OnParametersChanged();
        }

        // =====================================================================
        // I / O
        // =====================================================================
        #region IO
        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path", "wsp_path",
                "WASPer Print Path object. Its canonical point-plane origins supply the path geometry; output stores the calculated flows back into wsp_path. Please use the Pp01 WASPer Path from Curves before using this component.",
                GH_ParamAccess.item);

            p.AddNumberParameter("min_flux", "min_flux", "Flow value in maximally crowded regions.", GH_ParamAccess.item, 0.60);
            p.AddNumberParameter("max_flux", "max_flux", "Flow value in safe open regions.", GH_ParamAccess.item, 1.00);
            p.AddNumberParameter("d_crit", "d_crit", "Clearance at or below which risk = 1 (min_flux).", GH_ParamAccess.item, 2.0);
            p.AddNumberParameter("d_safe", "d_safe", "Clearance at or above which risk = 0 (max_flux).", GH_ParamAccess.item, 6.0);
            p.AddIntegerParameter("cluster_mode", "mode", "0 = other curves only\n1 = self only\n2 = both, min() (default)", GH_ParamAccess.item, 2);
            p.AddNumberParameter("curv_threshold", "curv_th", "Absolute turning-angle threshold in radians for bend detection.", GH_ParamAccess.item, 0.3);
            p[6].Optional = true;
            p.AddIntegerParameter("zone_half_width", "zone_hw", "Number of points on each side of a detected bend peak to mark as turning-zone geometry.", GH_ParamAccess.item, 2);
            p[7].Optional = true;
            p.AddParameter(global::WASPer_3DP.WasperTargetRolesParam.Create(
                "Selects which semantic path branches receive calculated flow values. 0 = All paths " +
                "(default), 1 = Shell, 2 = Infill, 3 = Partition, 4 = Support, 5 = Transition, 6 = Undefined. Supply several role-specific " +
                "values to include them and exclude the others. All paths (0) cannot be combined. " +
                "Non-target branches preserve incoming flows."));
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("wsp_path", "wsp_path", "WASPer Print Path object carrying updated proximity-based flows.", GH_ParamAccess.item);
            p.AddTextParameter("summary", "summary", "Flow/proximity summary.", GH_ParamAccess.item);
            // Optional debug outputs are added dynamically by RebuildOutputs(), based on the
            // persisted _visibleOutputsMask. RegisterOutputParams() runs once at construction,
            // before Read() has restored any persisted state, so a mask-gated branch here would
            // never fire.
        }
        #endregion

        // =====================================================================
        // Internal types
        // =====================================================================
        #region Internal types

        private sealed class BranchData
        {
            public GH_Path Path;
            public int Layer;
            public int BranchIndex;

            public Point3d[] WorldPts;
            public Point2d[] PlanePts;

            public double[] TurnAngle;   // per-vertex turning angle (rad)
            public bool[] IsZonePt;    // true = inside a turning zone ? excluded
            public int[] RunId;        // >=0 for run points; -1 for zone points

            // Segments flagged for self-clearance eligibility
            public Seg2d[] SelfSegs;    // segments NOT touching any zone point
            public Seg2d[] AllSegs;     // all valid segments (for other-clearance)

            public double[] SelfClearance;
            public double[] OtherClearance;
            public double[] Clearance;
            public double[] Risk;
            public double[] Flux;
        }

        private struct Seg2d
        {
            public double Ax, Ay, Bx, By;
            public int RunId;   // run of first endpoint (-1 if zone)

            public Seg2d(double ax, double ay, double bx, double by, int runId)
            { Ax = ax; Ay = ay; Bx = bx; By = by; RunId = runId; }
        }

        #endregion

        // =====================================================================
        // SolveInstance
        // =====================================================================
        #region SolveInstance
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_Structure<GH_Point> pTree;
            global::WASPer_3DP.WasperPrintPath packedPath = null;
            bool hasPackedPath = global::WASPer_3DP.WasperGcodeTreeUtil.TryGetPrintPath(DA, 0, out packedPath);
            if (!hasPackedPath || packedPath == null || !packedPath.HasPoints)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Pp16 requires a valid wsp_path input. Please use the Pp01 WASPer Path from Curves before using this component.");
                return;
            }
            pTree = global::WASPer_3DP.WasperGcodeTreeUtil.ToPointStructure(packedPath.Points);

            var inputPlanes = packedPath.HasPlanes ? ExtractLayerPlanes(packedPath.PtPlanes) : new List<Plane>();

            double minFlux = 0.60;
            double maxFlux = 1.00;
            double dCrit = 2.0;
            double dSafe = 6.0;
            int clusterMode = 2;
            double curvThr = 0.3;
            int zoneHalfW = 2;
            var targetRoles = new List<int>();

            if (!DA.GetData(1, ref minFlux)) return;
            if (!DA.GetData(2, ref maxFlux)) return;
            if (!DA.GetData(3, ref dCrit)) return;
            if (!DA.GetData(4, ref dSafe)) return;
            if (!DA.GetData(5, ref clusterMode)) return;
            DA.GetData(6, ref curvThr);
            DA.GetData(7, ref zoneHalfW);
            DA.GetDataList(8, targetRoles);

            Message = _versionTag;

            if (pTree == null || pTree.PathCount == 0)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No path locations found in pt_planes origins."); return; }
            if (minFlux < 0) minFlux = 0;
            if (maxFlux < 0) maxFlux = 0;
            if (minFlux > maxFlux) { double t = minFlux; minFlux = maxFlux; maxFlux = t; }
            if (dCrit < 0) dCrit = 0;
            if (dSafe < dCrit)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "d_safe < d_crit — setting equal."); dSafe = dCrit; }
            if (clusterMode < 0 || clusterMode > 2)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "cluster_mode must be 0/1/2 — using 2."); clusterMode = 2; }
            if (curvThr < 0) curvThr = 0;
            if (zoneHalfW < 0) zoneHalfW = 0;
            if (!global::WASPer_3DP.WasperGcodeTreeUtil.TryNormalizeTargetRoles(
                    targetRoles,
                    out targetRoles,
                    out string roleError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, roleError);
                return;
            }

            // ------------------------------------------------------------------
            var branches = ExtractBranches(pTree);
            if (branches.Count == 0)
            { AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No valid branches."); return; }

            int targetedBranchCount = branches.Count(branch =>
                global::WASPer_3DP.WasperGcodeTreeUtil.MatchesTargetRoles(
                    packedPath.PathRoles,
                    branch.Path,
                    targetRoles));
            if (targetedBranchCount == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"No {WASPer_3DP.WasperGcodeTreeUtil.TargetRoleNames(targetRoles)} branches were found. " +
                    "The input wsp_path passes through unchanged.");
                DA.SetData(0, new global::WASPer_3DP.WasperPrintPathGoo(packedPath));
                DA.SetData(
                    1,
                    $"OK | target_roles={WASPer_3DP.WasperGcodeTreeUtil.TargetRoleNames(targetRoles)} | " +
                    "targeted_branches=0 | unchanged");
                WasperPathDebugOutputs.SetCore(DA, this, packedPath);
                return;
            }

            int maxLayer = 0;
            foreach (var b in branches) if (b.Layer > maxLayer) maxLayer = b.Layer;

            var layerPlanes = BuildLayerPlanes(maxLayer + 1, inputPlanes);
            if (inputPlanes.Count > 0 && inputPlanes.Count < maxLayer + 1)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "Fewer planes than layers — last valid plane reused.");

            // Step 1 — project
            foreach (var br in branches)
                br.PlanePts = ProjectToPlane(br.WorldPts, layerPlanes[br.Layer]);

            // Step 2 — turning angles
            foreach (var br in branches)
                br.TurnAngle = ComputeTurnAngles(br.PlanePts);

            // Steps 3+4+5 — NMS peaks ? expanded zones ? runs
            foreach (var br in branches)
            {
                var peaks = DetectPeaksNMS(br.TurnAngle, curvThr);
                br.IsZonePt = BuildZoneMask(br.PlanePts.Length, peaks, zoneHalfW);
                br.RunId = BuildRunIds(br.PlanePts.Length, br.IsZonePt);
            }

            // Step 6 — build segment arrays
            foreach (var br in branches)
                BuildSegArrays(br);

            // Pre-flatten per-layer tagged arrays (all segs, for other-clearance)
            var layerTagged = new Dictionary<int, (int branchIdx, Seg2d seg)[]>();
            foreach (var grp in branches.GroupBy(b => b.Layer))
            {
                int total = grp.Sum(b => b.AllSegs.Length);
                var arr = new (int, Seg2d)[total];
                int idx = 0;
                foreach (var b in grp)
                    for (int s = 0; s < b.AllSegs.Length; s++)
                        arr[idx++] = (b.BranchIndex, b.AllSegs[s]);
                layerTagged[grp.Key] = arr;
            }

            const double Huge = 1e18;

            // Steps 6+7 — clearance (parallel)
            Parallel.ForEach(branches, br =>
            {
                int n = br.PlanePts.Length;
                var taggedLayer = layerTagged[br.Layer];
                var selfSegs = br.SelfSegs;
                int myIdx = br.BranchIndex;
                int[] runIds = br.RunId;

                br.SelfClearance = new double[n];
                br.OtherClearance = new double[n];

                for (int i = 0; i < n; i++)
                {
                    double px = br.PlanePts[i].X;
                    double py = br.PlanePts[i].Y;
                    int rid = runIds[i];   // -1 if zone point

                    // Self clearance
                    // Zone points get Huge (they are excluded separators)
                    double minSelf = Huge;
                    if ((clusterMode == 1 || clusterMode == 2) && rid >= 0)
                    {
                        for (int s = 0; s < selfSegs.Length; s++)
                        {
                            // Skip same run
                            if (selfSegs[s].RunId == rid) continue;

                            double d = DistPtSeg(px, py,
                                selfSegs[s].Ax, selfSegs[s].Ay,
                                selfSegs[s].Bx, selfSegs[s].By);
                            if (d < minSelf) minSelf = d;
                        }
                    }
                    br.SelfClearance[i] = minSelf;

                    // Other clearance — no exclusion, all other-curve segs
                    double minOther = Huge;
                    if (clusterMode == 0 || clusterMode == 2)
                    {
                        for (int s = 0; s < taggedLayer.Length; s++)
                        {
                            if (taggedLayer[s].branchIdx == myIdx) continue;
                            ref var seg = ref taggedLayer[s].seg;
                            double d = DistPtSeg(px, py,
                                seg.Ax, seg.Ay, seg.Bx, seg.By);
                            if (d < minOther) minOther = d;
                        }
                    }
                    br.OtherClearance[i] = minOther;
                }
            });

            // Step 8 — smooth self and other SEPARATELY before combining
            const int SmoothW = 3;
            foreach (var br in branches)
            {
                if (clusterMode == 1 || clusterMode == 2)
                    br.SelfClearance = SmoothDist(br.SelfClearance, SmoothW, Huge);
                if (clusterMode == 0 || clusterMode == 2)
                    br.OtherClearance = SmoothDist(br.OtherClearance, SmoothW, Huge);
            }

            // Steps 9+10 — combine, risk, smooth risk, flux
            foreach (var br in branches)
            {
                int n = br.PlanePts.Length;
                br.Clearance = new double[n];
                br.Risk = new double[n];
                br.Flux = new double[n];

                for (int i = 0; i < n; i++)
                {
                    double clr;
                    if (clusterMode == 0) clr = br.OtherClearance[i];
                    else if (clusterMode == 1) clr = br.SelfClearance[i];
                    else clr = Math.Min(br.SelfClearance[i], br.OtherClearance[i]);

                    br.Clearance[i] = clr;
                    br.Risk[i] = clr >= Huge * 0.5 ? 0.0 : ClearanceToRisk(clr, dCrit, dSafe);
                }

                br.Risk = SmoothLinear(br.Risk, 2);

                for (int i = 0; i < n; i++)
                {
                    double r = Clamp01(br.Risk[i]);
                    br.Flux[i] = maxFlux - r * (maxFlux - minFlux);
                }
            }

            // Outputs
            var fluxTree = new GH_Structure<GH_Number>();
            var clearTree = new GH_Structure<GH_Number>();
            var riskTree = new GH_Structure<GH_Number>();
            var selfClrTree = new GH_Structure<GH_Number>();
            var otherClrTree = new GH_Structure<GH_Number>();
            var ridTree = new GH_Structure<GH_Integer>();
            foreach (var br in branches)
            {
                int n = br.WorldPts.Length;
                var path = br.Path;
                bool isTarget = global::WASPer_3DP.WasperGcodeTreeUtil.MatchesTargetRoles(
                    packedPath.PathRoles,
                    path,
                    targetRoles);
                IList<double> incomingFlows =
                    packedPath.Flows != null && packedPath.Flows.PathExists(path)
                        ? packedPath.Flows.Branch(path)
                        : null;
                for (int i = 0; i < n; i++)
                {
                    double selfOut = isTarget && br.SelfClearance[i] < Huge * 0.5
                        ? br.SelfClearance[i]
                        : -1.0;
                    double otherOut = isTarget && br.OtherClearance[i] < Huge * 0.5
                        ? br.OtherClearance[i]
                        : -1.0;
                    double clrOut = isTarget && br.Clearance[i] < Huge * 0.5
                        ? br.Clearance[i]
                        : -1.0;
                    double outputFlux = isTarget
                        ? br.Flux[i]
                        : ValueAt(incomingFlows, i, 1.0);

                    fluxTree.Append(new GH_Number(outputFlux), path);
                    clearTree.Append(new GH_Number(clrOut), path);
                    riskTree.Append(new GH_Number(isTarget ? br.Risk[i] : 0.0), path);
                    selfClrTree.Append(new GH_Number(selfOut), path);
                    otherClrTree.Append(new GH_Number(otherOut), path);
                    ridTree.Append(new GH_Integer(isTarget ? br.RunId[i] : -1), path);
                }
            }

            var outPath = new global::WASPer_3DP.WasperPrintPath(
                global::WASPer_3DP.WasperGcodeTreeUtil.ToPointTree(pTree),
                packedPath.PtPlanes,
                global::WASPer_3DP.WasperGcodeTreeUtil.ToDoubleTree(fluxTree),
                packedPath.LayerH,
                printSpeed: packedPath.PrintSpeed,
                printLoc: packedPath.PrintLoc,
                printGlob: packedPath.PrintGlob,
                supportPts: packedPath.SupportPts,
                supportVects: packedPath.SupportVects,
                angles: packedPath.Angles,
                contactWidths: packedPath.ContactWidths,
                riskMaterial: packedPath.RiskMaterial,
                riskComb: packedPath.RiskComb,
                load: packedPath.Load,
                capacity: packedPath.Capacity,
                nozzleDiam: packedPath.NozzleDiam,
                dRatio: packedPath.DRatio,
                dLoaded: packedPath.DLoaded,
                bendRatio: packedPath.BendRatio,
                spanClass: packedPath.SpanClass,
                spanLen: packedPath.SpanLen,
                collapsed: packedPath.Collapsed,
                cascade: packedPath.Cascade,
                collapseGen: packedPath.CollapseGen,
                layerW: packedPath.LayerW,
                layerWf: packedPath.LayerWf,
                printVol: packedPath.PrintVol,
                torn: packedPath.Torn,
                interfaceRatio: packedPath.InterfaceRatio,
                overturnRatio: packedPath.OverturnRatio,
                failureFlags: packedPath.FailureFlags,
                pathRoles: packedPath.PathRoles,
                layerPlanes: packedPath.LayerPlanes,
                strokeIds: packedPath.StrokeIds,
                hasCrossLayerShellContinuity: packedPath.HasCrossLayerShellContinuity);

            DA.SetData(0, new global::WASPer_3DP.WasperPrintPathGoo(outPath));
            DA.SetData(
                1,
                $"OK | plane locations={pTree.DataCount} | branches={pTree.PathCount} | " +
                $"target_roles={WASPer_3DP.WasperGcodeTreeUtil.TargetRoleNames(targetRoles)} | " +
                $"targeted_branches={targetedBranchCount} | min_flux={minFlux:0.###} | max_flux={maxFlux:0.###}");
            WasperPathDebugOutputs.SetCore(DA, this, outPath);
            SetDebugTree(DA, "clearance", clearTree);
            SetDebugTree(DA, "risk", riskTree);
            SetDebugTree(DA, "self_clr", selfClrTree);
            SetDebugTree(DA, "other_clr", otherClrTree);
            SetDebugTree(DA, "cluster_id", ridTree);
        }

        private void SetDebugTree(
            IGH_DataAccess da,
            string nickName,
            IGH_Structure tree)
        {
            int index = WasperPathDebugOutputs.OutputIndex(this, nickName);
            if (index >= 0 && tree != null)
                da.SetDataTree(index, tree);
        }
        #endregion

        // =====================================================================
        // Helpers
        // =====================================================================
        #region Helpers

        private static double ValueAt(IList<double> values, int index, double fallback)
        {
            if (values == null || values.Count == 0)
                return fallback;
            double value = values.Count == 1
                ? values[0]
                : values[Math.Min(index, values.Count - 1)];
            return double.IsFinite(value) ? value : fallback;
        }

        private static List<BranchData> ExtractBranches(GH_Structure<GH_Point> pTree)
        {
            var result = new List<BranchData>(pTree.PathCount);
            int bIdx = 0;
            for (int i = 0; i < pTree.PathCount; i++)
            {
                var path = pTree.Paths[i];
                var raw = pTree.get_Branch(path);
                if (raw == null || raw.Count == 0) continue;
                var pts = new List<Point3d>(raw.Count);
                foreach (var obj in raw)
                    if (obj is GH_Point ghp && ghp.Value.IsValid)
                        pts.Add(ghp.Value);
                if (pts.Count == 0) continue;
                int layer = path.Length > 0 ? path.Indices[0] : 0;
                result.Add(new BranchData
                {
                    Path = path,
                    Layer = layer,
                    BranchIndex = bIdx++,
                    WorldPts = pts.ToArray()
                });
            }
            return result;
        }

        private static List<Plane> ExtractLayerPlanes(DataTree<Plane> tree)
        {
            var byLayer = new SortedDictionary<int, Plane>();
            if (tree == null) return new List<Plane>();

            for (int i = 0; i < tree.BranchCount; i++)
            {
                var path = tree.Paths[i];
                var branch = tree.Branch(path);
                if (branch == null || branch.Count == 0) continue;

                int layer = path.Length > 0 ? path.Indices[0] : i;
                if (!byLayer.ContainsKey(layer) && branch[0].IsValid)
                    byLayer[layer] = branch[0];
            }

            return byLayer.Values.ToList();
        }
        private static List<Plane> BuildLayerPlanes(int count, List<Plane> input)
        {
            var planes = new List<Plane>(count);
            var last = Plane.WorldXY;
            for (int i = 0; i < count; i++)
            {
                if (input != null && i < input.Count && input[i].IsValid)
                    last = input[i];
                planes.Add(last);
            }
            return planes;
        }

        private static Point2d[] ProjectToPlane(Point3d[] pts, Plane plane)
        {
            var r = new Point2d[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                Point3d q;
                if (!plane.RemapToPlaneSpace(pts[i], out q)) q = pts[i];
                r[i] = new Point2d(q.X, q.Y);
            }
            return r;
        }

        // Per-vertex turning angle — pure acos, no arc-length division
        private static double[] ComputeTurnAngles(Point2d[] pts)
        {
            int n = pts.Length;
            var t = new double[n];
            if (n < 3) return t;
            const double tiny = 1e-12;
            for (int i = 1; i < n - 1; i++)
            {
                double v1x = pts[i].X - pts[i - 1].X, v1y = pts[i].Y - pts[i - 1].Y;
                double v2x = pts[i + 1].X - pts[i].X, v2y = pts[i + 1].Y - pts[i].Y;
                double l1 = Math.Sqrt(v1x * v1x + v1y * v1y);
                double l2 = Math.Sqrt(v2x * v2x + v2y * v2y);
                if (l1 < tiny || l2 < tiny) continue;
                double cosA = (v1x * v2x + v1y * v2y) / (l1 * l2);
                if (cosA > 1.0) cosA = 1.0;
                if (cosA < -1.0) cosA = -1.0;
                t[i] = Math.Acos(cosA);
            }
            t[0] = t[1]; t[n - 1] = t[n - 2];
            return t;
        }

        // Step 3 — NMS peak detection
        // Returns one peak index per contiguous above-threshold region.
        private static List<int> DetectPeaksNMS(double[] turn, double threshold)
        {
            var peaks = new List<int>();
            int n = turn.Length;
            if (n == 0 || threshold <= 0) return peaks;

            bool inRegion = false;
            int rStart = 0;

            for (int i = 0; i <= n; i++)
            {
                bool above = (i < n) && (turn[i] >= threshold);

                if (above && !inRegion)
                {
                    inRegion = true;
                    rStart = i;
                }
                else if (!above && inRegion)
                {
                    inRegion = false;
                    int rEnd = i - 1;

                    // Find the single highest point in this region
                    int peakIdx = rStart;
                    double peakVal = turn[rStart];
                    for (int j = rStart + 1; j <= rEnd; j++)
                        if (turn[j] > peakVal) { peakVal = turn[j]; peakIdx = j; }

                    peaks.Add(peakIdx);
                }
            }

            return peaks;
        }

        // Step 4 — expand each peak into a zone, merge overlaps ? bool mask
        private static bool[] BuildZoneMask(int n, List<int> peaks, int halfW)
        {
            var mask = new bool[n];
            foreach (int pk in peaks)
            {
                int a = Math.Max(0, pk - halfW);
                int b = Math.Min(n - 1, pk + halfW);
                for (int i = a; i <= b; i++) mask[i] = true;
            }
            return mask;
        }

        // Step 5 — assign run IDs; zone points get -1
        // Runs are the intervals between (and before/after) turning zones.
        // Run ID increments each time a zone is crossed.
        private static int[] BuildRunIds(int n, bool[] isZone)
        {
            var ids = new int[n];
            int run = 0;

            for (int i = 0; i < n; i++)
            {
                if (isZone[i])
                {
                    ids[i] = -1;
                    // If next point starts a new non-zone run, increment
                    if (i + 1 < n && !isZone[i + 1])
                        run++;
                }
                else
                {
                    ids[i] = run;
                }
            }

            return ids;
        }

        // Build two segment arrays per branch:
        //   AllSegs  — every valid segment (for other-clearance)
        //   SelfSegs — only segments where NEITHER endpoint is a zone point
        //              AND both endpoints share the same run
        //              These are the only segments valid for self-clearance.
        private static void BuildSegArrays(BranchData br)
        {
            int n = br.PlanePts.Length;
            var allList = new List<Seg2d>(n);
            var selfList = new List<Seg2d>(n);
            const double tiny = 1e-12;

            for (int i = 0; i < n - 1; i++)
            {
                double ax = br.PlanePts[i].X, ay = br.PlanePts[i].Y;
                double bx = br.PlanePts[i + 1].X, by = br.PlanePts[i + 1].Y;
                double dx = bx - ax, dy = by - ay;
                if (dx * dx + dy * dy <= tiny * tiny) continue;

                int ridA = br.RunId[i];
                int ridB = br.RunId[i + 1];

                var seg = new Seg2d(ax, ay, bx, by, ridA);
                allList.Add(seg);

                // Eligible for self-comparison:
                // neither endpoint is a zone point, and both in same run
                bool valid = ridA >= 0 && ridB >= 0 && ridA == ridB;
                if (valid) selfList.Add(seg);
            }

            br.AllSegs = allList.ToArray();
            br.SelfSegs = selfList.ToArray();
        }

        // O(n) distance-array smoother that skips Huge sentinel values
        private static double[] SmoothDist(double[] v, int half, double huge)
        {
            int n = v.Length;
            if (n == 0 || half <= 0) return (double[])v.Clone();

            // Prefix sums ignoring Huge entries
            var sumPre = new double[n + 1];
            var cntPre = new int[n + 1];
            for (int i = 0; i < n; i++)
            {
                bool skip = v[i] >= huge * 0.5;
                sumPre[i + 1] = sumPre[i] + (skip ? 0 : v[i]);
                cntPre[i + 1] = cntPre[i] + (skip ? 0 : 1);
            }

            var r = new double[n];
            for (int i = 0; i < n; i++)
            {
                int lo = Math.Max(0, i - half);
                int hi = Math.Min(n - 1, i + half);
                int cnt = cntPre[hi + 1] - cntPre[lo];
                r[i] = cnt > 0
                    ? (sumPre[hi + 1] - sumPre[lo]) / cnt
                    : v[i];   // no valid neighbours ? keep original
            }
            return r;
        }

        // O(n) plain box filter (for risk smoothing)
        private static double[] SmoothLinear(double[] v, int half)
        {
            int n = v.Length;
            if (n == 0 || half <= 0) return (double[])v.Clone();
            var pre = new double[n + 1];
            for (int i = 0; i < n; i++) pre[i + 1] = pre[i] + v[i];
            var r = new double[n];
            for (int i = 0; i < n; i++)
            {
                int lo = Math.Max(0, i - half), hi = Math.Min(n - 1, i + half);
                r[i] = (pre[hi + 1] - pre[lo]) / (hi - lo + 1);
            }
            return r;
        }

        private static double ClearanceToRisk(double c, double dCrit, double dSafe)
        {
            if (c <= dCrit) return 1.0;
            if (c >= dSafe) return 0.0;
            double t = (c - dCrit) / Math.Max(1e-12, dSafe - dCrit);
            t = Clamp01(t);
            return 1.0 - t * t * (3.0 - 2.0 * t);
        }

        private static double DistPtSeg(
            double px, double py, double ax, double ay, double bx, double by)
        {
            double abx = bx - ax, aby = by - ay;
            double ab2 = abx * abx + aby * aby;
            if (ab2 <= 1e-16)
            { double ex = px - ax, ey = py - ay; return Math.Sqrt(ex * ex + ey * ey); }
            double t = ((px - ax) * abx + (py - ay) * aby) / ab2;
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double qx = ax + t * abx, qy = ay + t * aby;
            double dx = px - qx, dy = py - qy;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double Clamp01(double x) => x < 0 ? 0 : x > 1 ? 1 : x;

        #endregion
    }
}
