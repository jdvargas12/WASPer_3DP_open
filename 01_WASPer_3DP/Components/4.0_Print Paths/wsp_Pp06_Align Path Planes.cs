#region Component Description
/*
Component: wsp_Pp06_Align Path Planes
Nickname: Align Planes
Category: WASPer_3DP
SubCategory: 5.0_Gcode

Automatically improves correspondence between path-plane locations on consecutive
layers while resampling the exact Pp01 source curves. It is intended directly
after Pp01 for shells and well-stacked paths. Complex topology-changing TPMS
infills should bypass it. Confident one-to-one stacks recursively inherit the
aligned location count from the layer below. The component exposes no algorithm modes.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
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
    public sealed class wsp_Pp06_Align_Path_Planes : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Pp06_Align_Path_Planes()
            : base(
                "wsp_Pp06_Align Path Planes",
                "Align Planes",
                "Automatically improves vertical/local correspondence between path-plane locations " +
                "on consecutive layers while evaluating every new location on its exact Pp01 source curve. " +
                "Designed for use directly after Pp01. Ordinary stacks are aligned where correspondence " +
                "is clear. This component is intended for shells and well-stacked paths; it is not " +
                "recommended for complex TPMS split, merge, appearing, or disappearing topology. " +
                "Confident one-to-one stacks recursively add or remove locations to match the aligned " +
                "count below; topology-sensitive regions retain their original count. " +
                "Alignment changes sampling positions, not the source path shape. Per-location layer_h, " +
                "flow is remapped per segment; layer_h, layer_w, and print_speed are interpolated; " +
                "layer_wf and print_vol are recalculated. Downstream analysis and motion/KPI fields " +
                "are cleared if geometry changes. Right-click Show all outputs to inspect the aligned " +
                "pt_planes, flows, and layer_h trees.\r\n\r\n" +
                "Please use the Pp01 WASPer Path from Curves before using this component.",
                WASPerPalette.DesignFabrication,
                "4.0_Print Paths")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version == null
                ? "v1.0.x"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("0E4F3B41-9344-4A3D-8CBB-924221A21BF7");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon => AlignPlanesIcon.Bitmap;

        private int _visibleOutputsMask;
        private const string ShowAllOutputsKey = "wsp_gc16_show_all_outputs";
        private const string VisibleOutputsMaskKey = "wsp_gc16_visible_outputs_mask";

        private static readonly string[] OutputCatalog = WasperPathDebugOutputs.CoreNickNames;
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
            // Keep the two fixed parameters registered so their downstream wires survive
            // toggling the diagnostic outputs.
            while (Params.Output.Count > 2)
                Params.UnregisterOutputParameter(
                    Params.Output[Params.Output.Count - 1],
                    true);

            if (Params.Output.Count < 2)
            {
                while (Params.Output.Count > 0)
                    Params.UnregisterOutputParameter(
                        Params.Output[Params.Output.Count - 1],
                        true);
                RegisterCompactOutputParams();
            }
            else
            {
                Params.Output[1].Name = "summary";
                Params.Output[1].NickName = "summary";
                Params.Output[1].Description =
                    "Alignment summary including matched, changed, unchanged, rejected, and topology-sensitive branches.";
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
                Description =
                    "Aligned WASPer Print Path. Tree paths are preserved. Confident one-to-one stacks " +
                    "recursively inherit the aligned count below, so locations may be added or removed. Core process " +
                    "point fields—including layer_h—are interpolated, flow is remapped per segment, " +
                    "and layer_wf/print_vol are recalculated.",
                Access = GH_ParamAccess.item
            });
            Params.RegisterOutputParam(new Param_String
            {
                Name = "summary",
                NickName = "summary",
                Description =
                    "Alignment summary including matched, changed, unchanged, rejected, and topology-sensitive branches.",
                Access = GH_ParamAccess.item
            });
        }

        private void RegisterDiagnosticOutputParams()
        {
            WasperPathDebugOutputs.RegisterCore(this, IsOutputVisible);
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "WASPer Print Path to align. Please use the Pp01 WASPer Path from Curves before using this component. Intended directly after Pp01, before printability, " +
                "G-code, KPI, or simulation components. Canonical geometry comes from pt_planes; " +
                "Pp01 source-curve provenance enables lossless resampling. Legacy paths without " +
                "source curves use their incoming polylines and cannot add/remove locations.",
                GH_ParamAccess.item);

            p.AddNumberParameter(
                "strength",
                "strength",
                "Alignment strength from 0 to 1. 0 preserves the incoming sampling and 1 applies " +
                "the full safe alignment. Default: 1.",
                GH_ParamAccess.item,
                1.0);

            int maxShiftIndex = p.AddNumberParameter(
                "maximum shift",
                "max_shift",
                "Optional maximum distance, in model units, that a path-plane location may move " +
                "along its existing path. If omitted, a conservative automatic limit of approximately " +
                "one local segment is used. This is a safety bound, not a correspondence search radius.",
                GH_ParamAccess.item);
            p[maxShiftIndex].Optional = true;

            p.AddParameter(global::WASPer_3DP.WasperTargetRolesParam.Create(
                "Selects which semantic path branches are aligned. 0 = All paths (default), " +
                "1 = Shell, 2 = Infill, 3 = Partition, 4 = Support, 5 = Transition, 6 = Undefined. Supply several role-specific values to " +
                "include them and exclude the others. All paths (0) cannot be combined. Non-target " +
                "branches and fields pass through unchanged."));
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "Aligned WASPer Print Path. Tree paths are preserved. Confident one-to-one stacks " +
                "recursively inherit the aligned count below, so locations may be added or removed. Core process " +
                "point fields—including layer_h—are interpolated, flow is remapped per segment, " +
                "and layer_wf/print_vol are recalculated.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "summary",
                "summary",
                "Alignment summary including matched, changed, unchanged, rejected, and topology-sensitive branches.",
                GH_ParamAccess.item);

            // Optional debug outputs are added dynamically by RebuildOutputs()/RegisterDiagnosticOutputParams(),
            // based on the persisted _visibleOutputsMask. RegisterOutputParams() runs once at construction,
            // before Read() has restored any persisted state, so a mask-gated branch here would never fire.
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            Message = _versionTag;

            if (!WasperGcodeTreeUtil.TryGetPrintPath(da, 0, out WasperPrintPath source) ||
                source == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Supply a valid wsp_path. Please use the Pp01 WASPer Path from Curves before using this component.");
                return;
            }

            if (!source.HasPlanes || source.PtPlanes.DataCount == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "wsp_path does not contain canonical pt_planes. Please use the Pp01 WASPer Path from Curves before using this component.");
                return;
            }

            double strength = 1.0;
            da.GetData(1, ref strength);
            if (!double.IsFinite(strength))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "strength must be finite.");
                return;
            }

            double requestedStrength = strength;
            strength = Math.Max(0.0, Math.Min(1.0, strength));
            if (Math.Abs(requestedStrength - strength) > 1e-12)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"strength was clamped from {requestedStrength:0.###} to {strength:0.###}.");
            }

            double maxShift = 0.0;
            bool hasMaxShift = da.GetData(2, ref maxShift);
            if (hasMaxShift && (!double.IsFinite(maxShift) || maxShift <= 0.0))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "max_shift must be a positive finite value when supplied.");
                return;
            }

            var targetRoles = new List<int>();
            da.GetDataList(3, targetRoles);
            if (!WasperGcodeTreeUtil.TryNormalizeTargetRoles(
                    targetRoles,
                    out targetRoles,
                    out string roleError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, roleError);
                return;
            }

            double tolerance = Math.Max(
                RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001,
                1e-9);

            var branches = BuildBranches(source.PtPlanes, source.SourceCurves, tolerance);
            if (branches.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No valid pt_plane branches were found.");
                return;
            }

            int commonPrefix = WasperGcodeTreeUtil.CommonPathPrefixLength(
                branches.Select(branch => branch.Path).ToList());

            foreach (BranchGeometry branch in branches)
            {
                branch.Layer = LogicalLayer(branch.Path, commonPrefix);
                branch.Role = WasperGcodeTreeUtil.PathRoleAt(
                    source.PathRoles,
                    branch.Path);
            }

            var targetedBranches = branches
                .Where(branch => WasperGcodeTreeUtil.MatchesTargetRoles(
                    source.PathRoles,
                    branch.Path,
                    targetRoles))
                .ToList();
            if (targetedBranches.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"No {WasperGcodeTreeUtil.TargetRoleNames(targetRoles)} branches were found. " +
                    "The input wsp_path passes through unchanged.");
                SetOutputs(
                    da,
                    source,
                    $"Automatic path-plane alignment | target_roles={WasperGcodeTreeUtil.TargetRoleNames(targetRoles)} | " +
                    $"branches={branches.Count} | targeted=0 | unchanged");
                return;
            }

            var layers = targetedBranches
                .GroupBy(branch => branch.Layer)
                .OrderBy(group => group.Key)
                .ToList();

            var registrations = new Dictionary<GH_Path, Registration>();
            foreach (BranchGeometry branch in branches)
                registrations[branch.Path] = Registration.Identity(branch);
            int matched = 0;
            int changed = 0;
            int rejected = 0;
            int topologySensitive = 0;
            int countMatched = 0;
            int addedLocations = 0;
            int removedLocations = 0;
            int movedLocations = 0;
            double maximumAppliedShift = 0.0;
            double totalAppliedShift = 0.0;
            bool constrainMatchesByRole =
                !WasperGcodeTreeUtil.TargetsAllRoles(targetRoles) &&
                targetRoles.Count > 1;

            for (int layerIndex = 0; layerIndex < layers.Count; layerIndex++)
            {
                List<BranchGeometry> current = layers[layerIndex].ToList();
                if (layerIndex == 0)
                {
                    foreach (BranchGeometry branch in current)
                        registrations[branch.Path] = Registration.Identity(branch);
                    continue;
                }

                if (!WasperGcodeTreeUtil.TargetsAllRoles(targetRoles) &&
                    layers[layerIndex].Key != layers[layerIndex - 1].Key + 1)
                {
                    rejected += current.Count;
                    continue;
                }

                List<BranchGeometry> previous = layers[layerIndex - 1]
                    .Select(branch => RegisteredGeometry(branch, registrations, tolerance))
                    .ToList();
                var referenceUseCount = new Dictionary<GH_Path, int>();
                var reverseBestMatches = previous.ToDictionary(
                    branch => branch.Path,
                    branch => FindBestMatch(branch, current, constrainMatchesByRole));
                var layerMatches = new List<LayerMatch>();

                foreach (BranchGeometry branch in current)
                {
                    BranchMatch best = FindBestMatch(
                        branch,
                        previous,
                        constrainMatchesByRole);
                    double spacing = branch.RepresentativeSpacing(tolerance);
                    double height = RepresentativeValue(source.LayerH, branch.Path, tolerance);
                    double acceptanceDistance = Math.Max(
                        spacing * 2.0,
                        height > tolerance ? height * 4.0 : spacing * 2.0);

                    bool accepted =
                        best.Reference != null &&
                        double.IsFinite(best.Score) &&
                        best.Score <= acceptanceDistance;

                    layerMatches.Add(new LayerMatch(
                        branch,
                        best,
                        spacing,
                        height,
                        accepted));

                    if (accepted)
                    {
                        referenceUseCount.TryGetValue(best.Reference.Path, out int useCount);
                        referenceUseCount[best.Reference.Path] = useCount + 1;
                    }
                }

                foreach (LayerMatch layerMatch in layerMatches)
                {
                    BranchGeometry branch = layerMatch.Current;
                    BranchMatch best = layerMatch.Match;
                    if (!layerMatch.Accepted)
                    {
                        registrations[branch.Path] = Registration.Identity(branch);
                        rejected++;
                        continue;
                    }

                    matched++;
                    bool oneToOne = referenceUseCount[best.Reference.Path] == 1;
                    bool mutualNearest =
                        reverseBestMatches.TryGetValue(
                            best.Reference.Path,
                            out BranchMatch reverseBest) &&
                        reverseBest.Reference != null &&
                        reverseBest.Reference.Path.Equals(branch.Path);
                    double lengthRatio = branch.Length / Math.Max(best.Reference.Length, tolerance);
                    double inheritedSpacing = branch.Length / Math.Max(
                        1,
                        best.Reference.UniqueCount - (branch.Closed ? 0 : 1));
                    double densityRatio = inheritedSpacing / Math.Max(
                        layerMatch.Spacing,
                        tolerance);
                    double strongDistance = Math.Max(
                        layerMatch.Spacing,
                        layerMatch.Height > tolerance
                            ? layerMatch.Height * 2.5
                            : layerMatch.Spacing);

                    bool stableCountMatch =
                        strength > 1e-12 &&
                        oneToOne &&
                        mutualNearest &&
                        branch.HasExactSource &&
                        branch.Closed == best.Reference.Closed &&
                        lengthRatio >= 0.75 &&
                        lengthRatio <= 1.333333333 &&
                        densityRatio >= 0.70 &&
                        densityRatio <= 1.40 &&
                        best.Score <= strongDistance;

                    int targetUniqueCount = stableCountMatch
                        ? best.Reference.UniqueCount
                        : branch.UniqueCount;

                    double branchLimit = hasMaxShift
                        ? maxShift
                        : Math.Max(layerMatch.Spacing, tolerance);

                    Registration registration = AlignBranch(
                        branch,
                        best.Reference,
                        targetUniqueCount,
                        strength,
                        branchLimit,
                        tolerance);

                    registrations[branch.Path] = registration;
                    if (stableCountMatch)
                        countMatched++;
                    int countDelta = registration.Planes.Count - branch.OriginalPlanes.Count;
                    if (countDelta > 0) addedLocations += countDelta;
                    if (countDelta < 0) removedLocations -= countDelta;

                    if (registration.Changed)
                    {
                        changed++;
                        movedLocations += registration.MovedCount;
                        maximumAppliedShift = Math.Max(
                            maximumAppliedShift,
                            registration.MaximumShift);
                        totalAppliedShift += registration.TotalShift;
                    }
                }

                topologySensitive += referenceUseCount.Count(pair => pair.Value > 1);
                topologySensitive += layerMatches.Count(layerMatch =>
                    layerMatch.Accepted &&
                    reverseBestMatches.TryGetValue(
                        layerMatch.Match.Reference.Path,
                        out BranchMatch reverseBest) &&
                    (reverseBest.Reference == null ||
                     !reverseBest.Reference.Path.Equals(layerMatch.Current.Path)));
            }

            var alignedPlanes = new DataTree<Plane>();
            foreach (BranchGeometry branch in branches)
            {
                Registration registration = registrations.TryGetValue(branch.Path, out Registration value)
                    ? value
                    : Registration.Identity(branch);

                alignedPlanes.EnsurePath(branch.Path);
                foreach (Plane plane in registration.Planes)
                    alignedPlanes.Add(plane, branch.Path);
            }

            DataTree<double> flows = ResampleFlowTree(source.Flows, branches, registrations);
            DataTree<double> layerH = ResampleTree(source.LayerH, branches, registrations);
            DataTree<double> printSpeed = ResampleTree(source.PrintSpeed, branches, registrations);
            DataTree<double> layerW = ResampleTree(source.LayerW, branches, registrations);
            DataTree<double> layerWf = RebuildFlowAdjustedWidth(
                layerW,
                layerH,
                flows,
                tolerance) ?? ResampleTree(source.LayerWf, branches, registrations);
            DataTree<double> printVol = RebuildPrintVolume(
                alignedPlanes,
                layerH,
                layerWf,
                tolerance);

            flows = PreserveNonTargetBranches(
                flows, source.Flows, source.PathRoles, targetRoles, branches);
            layerH = PreserveNonTargetBranches(
                layerH, source.LayerH, source.PathRoles, targetRoles, branches);
            printSpeed = PreserveNonTargetBranches(
                printSpeed, source.PrintSpeed, source.PathRoles, targetRoles, branches);
            layerW = PreserveNonTargetBranches(
                layerW, source.LayerW, source.PathRoles, targetRoles, branches);
            layerWf = PreserveNonTargetBranches(
                layerWf, source.LayerWf, source.PathRoles, targetRoles, branches);
            printVol = PreserveNonTargetBranches(
                printVol, source.PrintVol, source.PathRoles, targetRoles, branches);

            bool clearedDerivedData = changed > 0 && HasDerivedData(source);
            if (clearedDerivedData)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Alignment changed path sampling after downstream analysis. Spatial printability, " +
                    "motion-plan, and KPI fields were cleared because they no longer describe the new " +
                    "locations. Place Pp06 directly after Pp01 and rerun downstream components.");
            }

            if (source.IsPartial)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "The input wsp_path is partial. Alignment used only the available consecutive layers, " +
                    "so correspondence at the partial boundary cannot be verified against the full path.");
            }

            int missingSourceCurveCount = targetedBranches.Count(branch => !branch.HasExactSource);
            if (missingSourceCurveCount > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{missingSourceCurveCount} branch(es) do not carry Pp01 source-curve provenance. " +
                    "Their locations can slide on the incoming polyline, but recursive add/remove count " +
                    "matching is disabled so bends cannot be shortcut. Recompute the input with the updated Pp01.");
            }

            if (rejected > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{rejected} branch(es) had no sufficiently plausible previous-layer match and were preserved unchanged.");
            }

            if (topologySensitive > 0)
            {
                bool complexTopology = topologySensitive >= Math.Max(3, targetedBranches.Count * 0.05);
                AddRuntimeMessage(
                    complexTopology
                        ? GH_RuntimeMessageLevel.Warning
                        : GH_RuntimeMessageLevel.Remark,
                    $"{topologySensitive} possible split/merge correspondence(s) were detected. " +
                    (complexTopology
                        ? "Pp06 is intended for shells and well-stacked paths; bypass it for complex topology-changing infills."
                        : "Review these locally before fabrication."));
            }

            WasperPrintPath output = changed == 0
                ? source
                : new WasperPrintPath(
                    points: null,
                    ptPlanes: alignedPlanes,
                    flows: flows,
                    layerH: layerH,
                    printSpeed: printSpeed,
                    nozzleDiam: source.NozzleDiam,
                    layerW: layerW,
                    layerWf: layerWf,
                    printVol: printVol,
                    travelSpeed: source.TravelSpeed,
                    zHop: source.ZHop,
                    zHopSpeed: source.ZHopSpeed,
                    isPartial: source.IsPartial,
                    sourceCurves: source.SourceCurves,
                    pathRoles: source.PathRoles,
                    layerPlanes: source.LayerPlanes);

            double meanAppliedShift = movedLocations > 0
                ? totalAppliedShift / movedLocations
                : 0.0;

            string limitLabel = hasMaxShift
                ? $"{maxShift:0.###} model units"
                : "automatic (~1 local segment)";

            string info =
                $"Automatic path-plane alignment | layers={layers.Count} | branches={branches.Count} | " +
                $"target_roles={WasperGcodeTreeUtil.TargetRoleNames(targetRoles)} | targeted={targetedBranches.Count} | " +
                $"passthrough={branches.Count - targetedBranches.Count} | matched={matched} | changed={changed} | " +
                $"targeted unchanged={targetedBranches.Count - changed} | " +
                $"rejected={rejected} | " +
                $"recursive count matches={countMatched} | added={addedLocations} | removed={removedLocations} | " +
                $"moved locations={movedLocations} | mean shift={meanAppliedShift:0.###} | " +
                $"max shift={maximumAppliedShift:0.###} | strength={strength:0.###} | " +
                $"shift limit={limitLabel} | possible split/merge references={topologySensitive}. " +
                $"exact target source curves={targetedBranches.Count - missingSourceCurveCount}/{targetedBranches.Count}. " +
                "Stable one-to-one stacks inherited the aligned count below; uncertain regions kept their original count.";

            SetOutputs(da, output, info);
        }

        private void SetOutputs(
            IGH_DataAccess da,
            WasperPrintPath path,
            string summary)
        {
            da.SetData(0, new WasperPrintPathGoo(path));
            da.SetData(1, summary);
            WasperPathDebugOutputs.SetCore(da, this, path);
        }

        private static DataTree<T> PreserveNonTargetBranches<T>(
            DataTree<T> computed,
            DataTree<T> original,
            DataTree<int> roles,
            IList<int> targetRoles,
            IEnumerable<BranchGeometry> branches)
        {
            if (WasperGcodeTreeUtil.TargetsAllRoles(targetRoles))
                return computed;

            var result = new DataTree<T>();
            foreach (BranchGeometry branch in branches)
            {
                bool isTarget = WasperGcodeTreeUtil.MatchesTargetRoles(
                    roles,
                    branch.Path,
                    targetRoles);
                DataTree<T> sourceTree = isTarget ? computed : original;
                if (sourceTree == null || !sourceTree.PathExists(branch.Path))
                    continue;

                IList<T> values = sourceTree.Branch(branch.Path);
                if (values == null)
                    continue;

                result.EnsurePath(branch.Path);
                foreach (T value in values)
                    result.Add(value, branch.Path);
            }

            return result.BranchCount > 0 ? result : null;
        }

        private static List<BranchGeometry> BuildBranches(
            DataTree<Plane> planes,
            DataTree<Curve> sourceCurves,
            double tolerance)
        {
            var result = new List<BranchGeometry>();
            for (int b = 0; b < planes.BranchCount; b++)
            {
                GH_Path path = planes.Paths[b];
                IList<Plane> values = planes.Branches[b];
                if (values == null || values.Count == 0)
                    continue;

                var valid = values.Where(plane => plane.IsValid).ToList();
                if (valid.Count == 0)
                    continue;

                Curve sourceCurve = null;
                if (sourceCurves != null && sourceCurves.PathExists(path))
                {
                    IList<Curve> curveBranch = sourceCurves.Branch(path);
                    if (curveBranch != null && curveBranch.Count > 0 &&
                        curveBranch[0] != null && curveBranch[0].IsValid)
                        sourceCurve = curveBranch[0];
                }

                result.Add(new BranchGeometry(path, valid, tolerance, sourceCurve));
            }
            return result;
        }

        private static int LogicalLayer(GH_Path path, int commonPrefix)
        {
            if (path == null || path.Length == 0)
                return 0;
            int index = Math.Max(0, Math.Min(commonPrefix, path.Length - 1));
            return path.Indices[index];
        }

        private static BranchGeometry RegisteredGeometry(
            BranchGeometry source,
            IReadOnlyDictionary<GH_Path, Registration> registrations,
            double tolerance)
        {
            if (!registrations.TryGetValue(source.Path, out Registration registration))
                return source;

            var geometry = new BranchGeometry(
                source.Path,
                registration.Planes.ToList(),
                tolerance,
                source.SourceCurve)
            {
                Layer = source.Layer,
                Role = source.Role
            };
            return geometry;
        }

        private static BranchMatch FindBestMatch(
            BranchGeometry current,
            IList<BranchGeometry> candidates,
            bool constrainRole)
        {
            BranchGeometry best = null;
            double bestScore = double.PositiveInfinity;

            foreach (BranchGeometry candidate in candidates)
            {
                if (constrainRole && candidate.Role != current.Role)
                    continue;
                double score = SymmetricProximity(current, candidate, 12);
                if (score < bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            return new BranchMatch(best, bestScore);
        }

        private static double SymmetricProximity(
            BranchGeometry a,
            BranchGeometry b,
            int sampleCount)
        {
            double sum = 0.0;
            int count = 0;

            for (int i = 0; i < sampleCount; i++)
            {
                double u = sampleCount == 1 ? 0.0 : (double)i / (sampleCount - 1);
                Point3d pa = a.PointAt(u);
                Point3d pb = b.PointAt(u);

                sum += pa.DistanceTo(b.PointAt(b.ClosestU(pa)));
                sum += pb.DistanceTo(a.PointAt(a.ClosestU(pb)));
                count += 2;
            }

            return count > 0 ? sum / count : double.PositiveInfinity;
        }

        private static Registration AlignBranch(
            BranchGeometry current,
            BranchGeometry reference,
            int targetUniqueCount,
            double strength,
            double maxShift,
            double tolerance)
        {
            if (strength <= 1e-12 || current.UniqueCount < 2 ||
                reference.UniqueCount < 2 || targetUniqueCount < 2)
                return Registration.Identity(current);

            Mapping mapping = FindMapping(current, reference);
            var targetU = new double[targetUniqueCount];
            var originalU = new double[targetUniqueCount];
            for (int i = 0; i < targetUniqueCount; i++)
            {
                originalU[i] = current.Closed
                    ? (double)i / targetUniqueCount
                    : (double)i / (targetUniqueCount - 1);
            }

            if (current.Closed)
            {
                double maxParamShift = current.Length > tolerance
                    ? maxShift / current.Length
                    : 0.0;

                for (int i = 0; i < targetUniqueCount; i++)
                {
                    double u = originalU[i];
                    Point3d target = reference.IndexedPointAt(mapping.Map(u));
                    double q = current.ClosestU(target);
                    double delta = PeriodicDelta(q - u);
                    delta = Math.Max(-maxParamShift, Math.Min(maxParamShift, delta));
                    targetU[i] = u + strength * delta;
                }

                EnforceClosedOrder(targetU, originalU, current.Length, tolerance);
            }
            else
            {
                targetU[0] = 0.0;
                targetU[targetUniqueCount - 1] = 1.0;
                double minimumParamGap = 0.35 / Math.Max(1, targetUniqueCount - 1);
                double maximumParamGap = 1.80 / Math.Max(1, targetUniqueCount - 1);

                for (int i = 1; i < targetUniqueCount - 1; i++)
                {
                    double u = originalU[i];
                    Point3d target = reference.IndexedPointAt(mapping.Map(u));
                    double q = current.ClosestU(target);
                    double maxParamShift = current.Length > tolerance
                        ? maxShift / current.Length
                        : 0.0;
                    double delta = Math.Max(
                        -maxParamShift,
                        Math.Min(maxParamShift, q - u));
                    targetU[i] = u + strength * delta;
                }

                for (int i = 1; i < targetUniqueCount - 1; i++)
                {
                    double minimum = targetU[i - 1] + minimumParamGap;
                    double maximum = targetU[i - 1] + maximumParamGap;
                    targetU[i] = Math.Max(minimum, Math.Min(maximum, targetU[i]));
                }

                for (int i = targetUniqueCount - 2; i > 0; i--)
                {
                    double maximum = targetU[i + 1] - minimumParamGap;
                    double minimum = targetU[i + 1] - maximumParamGap;
                    targetU[i] = Math.Max(minimum, Math.Min(maximum, targetU[i]));
                }
            }

            var outputPlanes = new List<Plane>();
            var sampleStations = new List<double>();
            int movedCount = 0;
            double maximumApplied = 0.0;
            double totalApplied = 0.0;

            for (int i = 0; i < targetUniqueCount; i++)
            {
                Plane plane = current.PlaneAt(targetU[i]);
                outputPlanes.Add(plane);
                sampleStations.Add(targetU[i]);

                double shift = current.PointAt(originalU[i]).DistanceTo(plane.Origin);
                if (shift > tolerance)
                {
                    movedCount++;
                    maximumApplied = Math.Max(maximumApplied, shift);
                    totalApplied += shift;
                }
            }

            if (current.Closed)
            {
                Plane seam = outputPlanes[0];
                outputPlanes.Add(seam);
                sampleStations.Add(sampleStations[0]);
            }

            return new Registration(
                outputPlanes,
                sampleStations,
                movedCount,
                maximumApplied,
                totalApplied,
                current.OriginalPlanes.Count);
        }

        private static void EnforceClosedOrder(
            double[] stations,
            double[] original,
            double length,
            double tolerance)
        {
            if (stations == null || original == null ||
                stations.Length < 2 || stations.Length != original.Length)
                return;

            double parameterTolerance = tolerance / Math.Max(length, tolerance);
            double first = stations[0];
            double minimumParamGap = Math.Max(
                0.35 / stations.Length,
                parameterTolerance);
            double maximumParamGap = Math.Max(
                1.80 / stations.Length,
                minimumParamGap);

            // Unwrap each station into the same cycle as the first one. The raw
            // values are already close to their original stations, but a target
            // close to the seam may legitimately fall just below zero or above one.
            for (int i = 1; i < stations.Length; i++)
            {
                while (stations[i] < first - parameterTolerance)
                    stations[i] += 1.0;
                while (stations[i] >= first + 1.0 + parameterTolerance)
                    stations[i] -= 1.0;
            }

            // Retain five percent of every original interval. This allows local
            // locations to slide substantially while preventing index reversal or
            // coincident planes.
            for (int i = 1; i < stations.Length; i++)
            {
                stations[i] = Math.Max(
                    stations[i - 1] + minimumParamGap,
                    Math.Min(
                        stations[i - 1] + maximumParamGap,
                        stations[i]));
            }

            double closingLowerBound = first + 1.0 - maximumParamGap;
            double closingUpperBound = first + 1.0 - minimumParamGap;
            if (stations[stations.Length - 1] < closingLowerBound ||
                stations[stations.Length - 1] > closingUpperBound)
            {
                stations[stations.Length - 1] = Math.Max(
                    closingLowerBound,
                    Math.Min(closingUpperBound, stations[stations.Length - 1]));
                for (int i = stations.Length - 2; i > 0; i--)
                {
                    stations[i] = Math.Max(
                        stations[i + 1] - maximumParamGap,
                        Math.Min(
                            stations[i + 1] - minimumParamGap,
                            stations[i]));
                }
            }

            // If an extremely compressed loop cannot satisfy the requested local
            // shifts and the ordering bounds simultaneously, retain the original
            // spacing with only the computed seam displacement. This is safer than
            // emitting reversed points.
            if (stations[1] <= first + parameterTolerance)
            {
                double seamShift = first - original[0];
                for (int i = 0; i < stations.Length; i++)
                    stations[i] = original[i] + seamShift;
            }

            for (int i = 0; i < stations.Length; i++)
                stations[i] = Wrap01(stations[i]);
        }

        private static Mapping FindMapping(
            BranchGeometry current,
            BranchGeometry reference)
        {
            if (!current.Closed || !reference.Closed)
            {
                double direct = MappingCost(current, reference, false, 0.0, 12);
                double reverse = MappingCost(current, reference, true, 0.0, 12);
                return direct <= reverse
                    ? new Mapping(false, 0.0, false)
                    : new Mapping(true, 0.0, false);
            }

            Mapping best = new Mapping(false, 0.0, true);
            double bestCost = double.PositiveInfinity;
            int steps = Math.Max(8, Math.Min(32, reference.UniqueCount));

            for (int reverseIndex = 0; reverseIndex < 2; reverseIndex++)
            {
                bool reverse = reverseIndex == 1;
                for (int shiftIndex = 0; shiftIndex < steps; shiftIndex++)
                {
                    double offset = (double)shiftIndex / steps;
                    double cost = MappingCost(current, reference, reverse, offset, 16);
                    if (cost < bestCost)
                    {
                        bestCost = cost;
                        best = new Mapping(reverse, offset, true);
                    }
                }
            }

            return best;
        }

        private static double MappingCost(
            BranchGeometry current,
            BranchGeometry reference,
            bool reverse,
            double offset,
            int samples)
        {
            double sum = 0.0;
            var mapping = new Mapping(reverse, offset, current.Closed && reference.Closed);
            for (int i = 0; i < samples; i++)
            {
                double u = current.Closed
                    ? (double)i / samples
                    : (double)i / (samples - 1);
                sum += current.PointAt(u).DistanceTo(reference.IndexedPointAt(mapping.Map(u)));
            }
            return sum / samples;
        }

        private static DataTree<double> ResampleTree(
            DataTree<double> source,
            IEnumerable<BranchGeometry> branches,
            IReadOnlyDictionary<GH_Path, Registration> registrations)
        {
            if (source == null || source.BranchCount == 0)
                return null;

            var output = new DataTree<double>();
            foreach (BranchGeometry branch in branches)
            {
                output.EnsurePath(branch.Path);
                if (!source.PathExists(branch.Path))
                    continue;

                IList<double> values = source.Branch(branch.Path);
                if (values == null || values.Count == 0)
                    continue;

                Registration registration = registrations.TryGetValue(branch.Path, out Registration value)
                    ? value
                    : Registration.Identity(branch);

                for (int i = 0; i < registration.SampleStations.Count; i++)
                {
                    double station = registration.SampleStations[i];
                    output.Add(InterpolateValue(values, branch, station), branch.Path);
                }
            }
            return output;
        }

        private static double InterpolateValue(
            IList<double> values,
            BranchGeometry geometry,
            double station)
        {
            if (values.Count == 1)
                return values[0];

            int usableCount = geometry.Closed && values.Count == geometry.OriginalPlanes.Count
                ? Math.Max(1, values.Count - 1)
                : Math.Min(values.Count, geometry.UniqueCount);

            if (usableCount == 1)
                return values[0];

            geometry.SegmentAt(station, out int index, out double local);
            int a = Math.Max(0, Math.Min(index, usableCount - 1));
            int b = geometry.Closed
                ? (a + 1) % usableCount
                : Math.Min(a + 1, usableCount - 1);

            double va = values[a];
            double vb = values[b];
            if (!double.IsFinite(va)) return vb;
            if (!double.IsFinite(vb)) return va;
            return va + (vb - va) * local;
        }

        private static DataTree<double> ResampleFlowTree(
            DataTree<double> source,
            IEnumerable<BranchGeometry> branches,
            IReadOnlyDictionary<GH_Path, Registration> registrations)
        {
            if (source == null || source.BranchCount == 0)
                return null;

            var output = new DataTree<double>();
            foreach (BranchGeometry branch in branches)
            {
                output.EnsurePath(branch.Path);
                if (!source.PathExists(branch.Path))
                    continue;

                IList<double> originalFlows = source.Branch(branch.Path);
                if (originalFlows == null || originalFlows.Count == 0)
                    continue;

                Registration registration = registrations.TryGetValue(
                    branch.Path,
                    out Registration value)
                        ? value
                        : Registration.Identity(branch);

                int outputCount = registration.SampleStations.Count;
                if (outputCount == 0)
                    continue;

                // Flow is stored on the segment ending at each location, not at
                // the location itself. The first item is therefore always zero.
                output.Add(0.0, branch.Path);
                if (outputCount == 1)
                    continue;

                int uniqueOutputCount = branch.Closed
                    ? outputCount - 1
                    : outputCount;
                var unwrapped = new double[uniqueOutputCount];
                for (int i = 0; i < uniqueOutputCount; i++)
                {
                    double station = registration.SampleStations[i];
                    if (i > 0)
                        while (station <= unwrapped[i - 1])
                            station += 1.0;
                    unwrapped[i] = station;
                }

                for (int i = 1; i < uniqueOutputCount; i++)
                {
                    double midpoint = 0.5 * (unwrapped[i - 1] + unwrapped[i]);
                    output.Add(
                        SourceSegmentFlowAt(originalFlows, branch, Wrap01(midpoint)),
                        branch.Path);
                }

                if (branch.Closed)
                {
                    double closingMidpoint = 0.5 * (
                        unwrapped[uniqueOutputCount - 1] +
                        unwrapped[0] + 1.0);
                    output.Add(
                        SourceSegmentFlowAt(
                            originalFlows,
                            branch,
                            Wrap01(closingMidpoint)),
                        branch.Path);
                }
            }

            return output;
        }

        private static double SourceSegmentFlowAt(
            IList<double> flows,
            BranchGeometry geometry,
            double station)
        {
            if (flows == null || flows.Count == 0)
                return 1.0;
            if (flows.Count == 1)
                return double.IsFinite(flows[0]) ? flows[0] : 1.0;

            IList<double> sourceStations = geometry.UniqueStations;
            int uniqueCount = geometry.UniqueCount;

            // Segment i ends at source location i. Item zero is deliberately
            // excluded because it is the no-preceding-segment marker.
            for (int i = 1; i < uniqueCount; i++)
            {
                if (station <= sourceStations[i])
                {
                    int flowIndex = Math.Min(i, flows.Count - 1);
                    double flow = flows[flowIndex];
                    return double.IsFinite(flow) ? flow : 1.0;
                }
            }

            if (geometry.Closed)
            {
                // The final repeated seam item carries the closing segment flow.
                double closing = flows[flows.Count - 1];
                return double.IsFinite(closing) ? closing : 1.0;
            }

            double last = flows[flows.Count - 1];
            return double.IsFinite(last) ? last : 1.0;
        }

        private static DataTree<double> RebuildPrintVolume(
            DataTree<Plane> planes,
            DataTree<double> heights,
            DataTree<double> widths,
            double tolerance)
        {
            if (planes == null || heights == null || widths == null)
                return null;

            var output = new DataTree<double>();
            for (int b = 0; b < planes.BranchCount; b++)
            {
                GH_Path path = planes.Paths[b];
                output.EnsurePath(path);
                IList<Plane> planeBranch = planes.Branch(path);
                IList<double> heightBranch = heights.PathExists(path) ? heights.Branch(path) : null;
                IList<double> widthBranch = widths.PathExists(path) ? widths.Branch(path) : null;

                for (int i = 0; i < planeBranch.Count; i++)
                {
                    double volume = 0.0;
                    if (i > 0 && heightBranch != null && widthBranch != null &&
                        heightBranch.Count > 0 && widthBranch.Count > 0)
                    {
                        double height = heightBranch[Math.Min(i, heightBranch.Count - 1)];
                        double width = widthBranch[Math.Min(i, widthBranch.Count - 1)];
                        double length = planeBranch[i - 1].Origin.DistanceTo(planeBranch[i].Origin);
                        double area = BeadArea(width, height, tolerance);
                        if (double.IsFinite(length) && length > tolerance)
                            volume = length * area;
                    }
                    output.Add(volume, path);
                }
            }
            return output;
        }

        private static DataTree<double> RebuildFlowAdjustedWidth(
            DataTree<double> widths,
            DataTree<double> heights,
            DataTree<double> flows,
            double tolerance)
        {
            if (widths == null || heights == null || flows == null)
                return null;

            var output = new DataTree<double>();
            foreach (GH_Path path in widths.Paths)
            {
                output.EnsurePath(path);
                if (!heights.PathExists(path) || !flows.PathExists(path))
                    continue;

                IList<double> widthBranch = widths.Branch(path);
                IList<double> heightBranch = heights.Branch(path);
                IList<double> flowBranch = flows.Branch(path);
                int count = Math.Min(
                    widthBranch?.Count ?? 0,
                    Math.Min(heightBranch?.Count ?? 0, flowBranch?.Count ?? 0));

                for (int i = 0; i < count; i++)
                {
                    double nominalWidth = widthBranch[i];
                    double height = heightBranch[i];
                    double flow = flowBranch[i];
                    output.Add(
                        EstimateFlowAdjustedWidth(
                            nominalWidth,
                            height,
                            flow,
                            tolerance),
                        path);
                }
            }
            return output;
        }

        private static double EstimateFlowAdjustedWidth(
            double nominalWidth,
            double height,
            double flow,
            double tolerance)
        {
            if (nominalWidth <= tolerance || height <= tolerance || flow <= tolerance ||
                !double.IsFinite(nominalWidth) || !double.IsFinite(height) || !double.IsFinite(flow))
                return nominalWidth * Math.Max(flow, 1.0);

            double referenceWidth = Math.Max(nominalWidth, height);
            double referenceArea = BeadArea(referenceWidth, height, tolerance);
            return (flow * referenceArea) / height
                + height * (1.0 - Math.PI / 4.0);
        }

        private static double BeadArea(double width, double height, double tolerance)
        {
            if (!double.IsFinite(width) || !double.IsFinite(height) ||
                width <= tolerance || height <= tolerance)
                return 0.0;

            double effectiveWidth = Math.Max(
                width,
                height * (1.0 - Math.PI / 4.0));
            double area = height * (effectiveWidth - height)
                + Math.PI * height * height / 4.0;
            return double.IsFinite(area) && area > 0.0 ? area : 0.0;
        }

        private static double RepresentativeValue(
            DataTree<double> tree,
            GH_Path path,
            double fallback)
        {
            if (tree == null || !tree.PathExists(path))
                return fallback;

            var values = tree.Branch(path)
                .Where(value => double.IsFinite(value) && value > 0.0)
                .OrderBy(value => value)
                .ToList();
            return values.Count == 0 ? fallback : values[values.Count / 2];
        }

        private static bool HasDerivedData(WasperPrintPath path)
        {
            return path.PrintLoc != null || path.PrintGlob != null ||
                   path.SupportPts != null || path.SupportVects != null ||
                   path.Angles != null || path.ContactWidths != null ||
                   path.RiskMaterial != null || path.RiskComb != null ||
                   path.Load != null || path.Capacity != null ||
                   path.DRatio != null || path.DLoaded != null ||
                   path.BendRatio != null || path.SpanClass != null ||
                   path.SpanLen != null || path.Collapsed != null ||
                   path.Cascade != null || path.CollapseGen != null ||
                   path.Torn != null || path.InterfaceRatio != null ||
                   path.OverturnRatio != null || path.FailureFlags != null ||
                   path.MotionPlan != null || path.KpiSegmentLength != null ||
                   path.KpiPrintSpeed != null || path.KpiPrintVol != null ||
                   path.KpiTimeMin.HasValue || path.KpiPathLength.HasValue ||
                   path.KpiVolume.HasValue || path.KpiLayers.HasValue;
        }

        private static double Wrap01(double value)
        {
            value -= Math.Floor(value);
            return value >= 1.0 ? 0.0 : value;
        }

        private static double PeriodicDelta(double value)
        {
            value -= Math.Floor(value + 0.5);
            return value;
        }

        private readonly struct BranchMatch
        {
            public BranchMatch(
                BranchGeometry reference,
                double score)
            {
                Reference = reference;
                Score = score;
            }

            public BranchGeometry Reference { get; }
            public double Score { get; }
        }

        private sealed class LayerMatch
        {
            public LayerMatch(
                BranchGeometry current,
                BranchMatch match,
                double spacing,
                double height,
                bool accepted)
            {
                Current = current;
                Match = match;
                Spacing = spacing;
                Height = height;
                Accepted = accepted;
            }

            public BranchGeometry Current { get; }
            public BranchMatch Match { get; }
            public double Spacing { get; }
            public double Height { get; }
            public bool Accepted { get; }
        }

        private readonly struct Mapping
        {
            public Mapping(bool reverse, double offset, bool periodic)
            {
                Reverse = reverse;
                Offset = offset;
                Periodic = periodic;
            }

            private bool Reverse { get; }
            private double Offset { get; }
            private bool Periodic { get; }

            public double Map(double u)
            {
                double value = (Reverse ? 1.0 - u : u) + Offset;
                return Periodic ? Wrap01(value) : Math.Max(0.0, Math.Min(1.0, value));
            }
        }

        private sealed class Registration
        {
            public Registration(
                List<Plane> planes,
                List<double> sampleStations,
                int movedCount,
                double maximumShift,
                double totalShift,
                int originalCount)
            {
                Planes = planes;
                SampleStations = sampleStations;
                MovedCount = movedCount;
                MaximumShift = maximumShift;
                TotalShift = totalShift;
                OriginalCount = originalCount;
            }

            public List<Plane> Planes { get; }
            public List<double> SampleStations { get; }
            public int MovedCount { get; }
            public double MaximumShift { get; }
            public double TotalShift { get; }
            public int OriginalCount { get; }
            public bool Changed => MovedCount > 0 || Planes.Count != OriginalCount;

            public static Registration Identity(BranchGeometry branch)
            {
                return new Registration(
                    branch.OriginalPlanes.ToList(),
                    branch.OriginalStations.ToList(),
                    0,
                    0.0,
                    0.0,
                    branch.OriginalPlanes.Count);
            }
        }

        private sealed class BranchGeometry
        {
            private readonly List<Plane> _uniquePlanes;
            private readonly List<double> _cumulative;
            private readonly double _tolerance;

            public BranchGeometry(
                GH_Path path,
                List<Plane> planes,
                double tolerance,
                Curve sourceCurve = null)
            {
                Path = path;
                OriginalPlanes = planes;
                _tolerance = tolerance;
                SourceCurve = sourceCurve != null && sourceCurve.IsValid
                    ? sourceCurve
                    : null;
                Closed = planes.Count > 2 &&
                         planes[0].Origin.DistanceTo(planes[planes.Count - 1].Origin) <= tolerance;
                _uniquePlanes = Closed
                    ? planes.Take(planes.Count - 1).ToList()
                    : planes.ToList();

                if (_uniquePlanes.Count == 0)
                    _uniquePlanes.Add(planes[0]);

                double exactLength = SourceCurve?.GetLength() ?? 0.0;
                if (SourceCurve != null && double.IsFinite(exactLength) && exactLength > tolerance)
                {
                    Length = exactLength;
                    _cumulative = BuildSourceCurveStations(SourceCurve, _uniquePlanes, exactLength);
                }
                else
                {
                    _cumulative = new List<double> { 0.0 };
                    double length = 0.0;
                    for (int i = 1; i < _uniquePlanes.Count; i++)
                    {
                        length += _uniquePlanes[i - 1].Origin.DistanceTo(_uniquePlanes[i].Origin);
                        _cumulative.Add(length);
                    }

                    if (Closed && _uniquePlanes.Count > 1)
                        length += _uniquePlanes[_uniquePlanes.Count - 1].Origin.DistanceTo(_uniquePlanes[0].Origin);

                    Length = Math.Max(length, tolerance);
                }

                UniqueStations = _cumulative.Select(value => value / Length).ToList();
                OriginalStations = Closed
                    ? UniqueStations.Concat(new[] { UniqueStations[0] }).ToList()
                    : UniqueStations.ToList();
            }

            public GH_Path Path { get; }
            public List<Plane> OriginalPlanes { get; }
            public List<double> UniqueStations { get; }
            public List<double> OriginalStations { get; }
            public Curve SourceCurve { get; }
            public bool HasExactSource => SourceCurve != null;
            public bool Closed { get; }
            public int UniqueCount => _uniquePlanes.Count;
            public double Length { get; }
            public int Layer { get; set; }
            public WasperPathRole Role { get; set; }

            public double RepresentativeSpacing(double fallback)
            {
                if (UniqueCount < 2)
                    return fallback;

                var lengths = new List<double>();
                for (int i = 1; i < UniqueCount; i++)
                    lengths.Add(_uniquePlanes[i - 1].Origin.DistanceTo(_uniquePlanes[i].Origin));
                if (Closed)
                    lengths.Add(_uniquePlanes[UniqueCount - 1].Origin.DistanceTo(_uniquePlanes[0].Origin));

                lengths = lengths
                    .Where(value => double.IsFinite(value) && value > _tolerance)
                    .OrderBy(value => value)
                    .ToList();
                return lengths.Count == 0 ? fallback : lengths[lengths.Count / 2];
            }

            public Point3d PointAt(double station)
            {
                if (SourceCurve != null)
                {
                    double u = Closed
                        ? Wrap01(station)
                        : Math.Max(0.0, Math.Min(1.0, station));
                    if (SourceCurve.NormalizedLengthParameter(u, out double parameter))
                        return SourceCurve.PointAt(parameter);
                }

                SegmentAt(station, out int index, out double local);
                int next = Closed
                    ? (index + 1) % UniqueCount
                    : Math.Min(index + 1, UniqueCount - 1);
                return _uniquePlanes[index].Origin +
                       (_uniquePlanes[next].Origin - _uniquePlanes[index].Origin) * local;
            }

            public Point3d IndexedPointAt(double station)
            {
                if (UniqueCount <= 1)
                    return _uniquePlanes[0].Origin;

                double u = Closed
                    ? Wrap01(station)
                    : Math.Max(0.0, Math.Min(1.0, station));
                double scaled = Closed
                    ? u * UniqueCount
                    : u * (UniqueCount - 1);
                int index = (int)Math.Floor(scaled);
                if (!Closed && index >= UniqueCount - 1)
                    return _uniquePlanes[UniqueCount - 1].Origin;

                index = Math.Max(0, Math.Min(index, UniqueCount - 1));
                int next = Closed
                    ? (index + 1) % UniqueCount
                    : Math.Min(index + 1, UniqueCount - 1);
                double local = scaled - Math.Floor(scaled);
                return _uniquePlanes[index].Origin +
                       (_uniquePlanes[next].Origin - _uniquePlanes[index].Origin) * local;
            }

            public Plane PlaneAt(double station)
            {
                SegmentAt(station, out int index, out double local);
                int next = Closed
                    ? (index + 1) % UniqueCount
                    : Math.Min(index + 1, UniqueCount - 1);

                Plane a = _uniquePlanes[index];
                Plane b = _uniquePlanes[next];
                Point3d origin = a.Origin + (b.Origin - a.Origin) * local;
                Vector3d x = a.XAxis * (1.0 - local) + b.XAxis * local;
                Vector3d y = a.YAxis * (1.0 - local) + b.YAxis * local;

                if (!x.Unitize() || !y.Unitize() || Math.Abs(x * y) > 0.999)
                {
                    Plane fallback = local < 0.5 ? a : b;
                    fallback.Origin = origin;
                    return fallback;
                }

                var plane = new Plane(origin, x, y);
                if (plane.IsValid)
                    return plane;

                Plane nearest = local < 0.5 ? a : b;
                nearest.Origin = origin;
                return nearest;
            }

            public void SegmentAt(double station, out int index, out double local)
            {
                if (UniqueCount <= 1)
                {
                    index = 0;
                    local = 0.0;
                    return;
                }

                double u = Closed
                    ? Wrap01(station)
                    : Math.Max(0.0, Math.Min(1.0, station));
                double distance = u * Length;

                if (Closed && distance >= _cumulative[_cumulative.Count - 1])
                {
                    index = UniqueCount - 1;
                    double start = _cumulative[_cumulative.Count - 1];
                    double segmentLength = Length - start;
                    local = segmentLength > _tolerance
                        ? (distance - start) / segmentLength
                        : 0.0;
                    return;
                }

                index = 0;
                while (index + 1 < _cumulative.Count &&
                       _cumulative[index + 1] < distance)
                    index++;

                int nextIndex = Math.Min(index + 1, _cumulative.Count - 1);
                double startDistance = _cumulative[index];
                double endDistance = _cumulative[nextIndex];
                double span = endDistance - startDistance;
                local = span > _tolerance
                    ? (distance - startDistance) / span
                    : 0.0;
            }

            public double ClosestU(Point3d point)
            {
                if (SourceCurve != null &&
                    SourceCurve.ClosestPoint(point, out double parameter))
                {
                    Interval domain = SourceCurve.Domain;
                    double partialLength = parameter <= domain.T0
                        ? 0.0
                        : parameter >= domain.T1
                            ? Length
                            : SourceCurve.GetLength(new Interval(domain.T0, parameter));
                    if (double.IsFinite(partialLength))
                    {
                        double normalized = partialLength / Length;
                        return Closed
                            ? Wrap01(normalized)
                            : Math.Max(0.0, Math.Min(1.0, normalized));
                    }
                }

                if (UniqueCount <= 1)
                    return 0.0;

                double bestDistance = double.PositiveInfinity;
                double bestLength = 0.0;
                int segmentCount = Closed ? UniqueCount : UniqueCount - 1;

                for (int i = 0; i < segmentCount; i++)
                {
                    int next = (i + 1) % UniqueCount;
                    Point3d a = _uniquePlanes[i].Origin;
                    Point3d b = _uniquePlanes[next].Origin;
                    Vector3d ab = b - a;
                    double lengthSquared = ab.SquareLength;
                    double t = lengthSquared > _tolerance * _tolerance
                        ? Math.Max(0.0, Math.Min(1.0, ((point - a) * ab) / lengthSquared))
                        : 0.0;
                    Point3d closest = a + ab * t;
                    double distance = closest.DistanceToSquared(point);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        double segmentStart = i < _cumulative.Count
                            ? _cumulative[i]
                            : _cumulative[_cumulative.Count - 1];
                        bestLength = segmentStart + Math.Sqrt(lengthSquared) * t;
                    }
                }

                double result = bestLength / Length;
                return Closed ? Wrap01(result) : Math.Max(0.0, Math.Min(1.0, result));
            }

            private static List<double> BuildSourceCurveStations(
                Curve curve,
                IList<Plane> planes,
                double length)
            {
                var stations = new List<double>(planes.Count);
                Interval domain = curve.Domain;
                double previous = 0.0;

                for (int i = 0; i < planes.Count; i++)
                {
                    double normalized = planes.Count <= 1
                        ? 0.0
                        : (double)i / planes.Count;

                    if (curve.ClosestPoint(planes[i].Origin, out double parameter))
                    {
                        double partialLength = parameter <= domain.T0
                            ? 0.0
                            : parameter >= domain.T1
                                ? length
                                : curve.GetLength(new Interval(domain.T0, parameter));
                        if (double.IsFinite(partialLength))
                            normalized = partialLength / length;
                    }

                    if (i == 0)
                        normalized = 0.0;
                    else if (normalized < previous)
                        normalized = previous;

                    normalized = Math.Max(0.0, Math.Min(1.0, normalized));
                    stations.Add(normalized * length);
                    previous = normalized;
                }

                return stations;
            }
        }

        private static class AlignPlanesIcon
        {
            private static Bitmap _bitmap;

            public static Bitmap Bitmap
            {
                get
                {
                    if (_bitmap != null)
                        return _bitmap;

                    _bitmap = new Bitmap(24, 24);
                    using Graphics graphics = Graphics.FromImage(_bitmap);
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.Clear(Color.Transparent);

                    using var pathPen = new Pen(Color.FromArgb(45, 70, 88), 2.0f);
                    using var guidePen = new Pen(Color.FromArgb(52, 166, 202), 1.2f)
                    {
                        DashStyle = DashStyle.Dash
                    };
                    using var pointBrush = new SolidBrush(Color.FromArgb(241, 146, 35));

                    graphics.DrawArc(pathPen, 2, 3, 19, 7, 5, 170);
                    graphics.DrawArc(pathPen, 2, 13, 19, 7, 185, 170);
                    graphics.DrawLine(guidePen, 7, 7, 7, 17);
                    graphics.DrawLine(guidePen, 12, 6, 12, 18);
                    graphics.DrawLine(guidePen, 17, 7, 17, 17);

                    foreach (PointF point in new[]
                    {
                        new PointF(7, 7), new PointF(12, 6), new PointF(17, 7),
                        new PointF(7, 17), new PointF(12, 18), new PointF(17, 17)
                    })
                    {
                        graphics.FillEllipse(pointBrush, point.X - 1.7f, point.Y - 1.7f, 3.4f, 3.4f);
                    }

                    return _bitmap;
                }
            }
        }
    }
}
