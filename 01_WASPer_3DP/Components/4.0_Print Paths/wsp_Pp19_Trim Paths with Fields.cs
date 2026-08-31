// wsp_Pp19_Trim Paths with Fields.cs
// WASPer_3DP - Subcategory: 4.0_Print Paths
//
// wsp_path counterpart of wsp_Sl05_Trim Crvs with Field: trims a packed WASPer
// Print Path (wsp_path) against one or more WasperFields instead of a Curve tree.
// Default field convention: negative = inside / keep, positive = outside / remove.
// If invert is true, the kept/removed side is flipped.
//
// RESOLUTION NOTE: trimming happens at the existing wsp_path point resolution.
// Each canonical point is kept or dropped by evaluating the field at its
// location; no new interpolated boundary point is inserted at the crossing
// (unlike Sl05, which splits NURBS curves at a bisected zero-crossing
// parameter). For a cleaner cut boundary, make sure the incoming wsp_path has
// enough point density (e.g. via Pp01's sampling or an upstream resample)
// before trimming.
//
// BRANCH SPLITTING: a branch whose kept points form more than one contiguous
// run is split into multiple output branches (source_path -> 0, 1, 2, ...),
// since a single wsp_path branch must remain one continuous polyline. A
// branch reduced to a single contiguous run keeps its original path, mirroring
// Pp07 Reduce Path Points.
//
// PATH ROLE METADATA: WasperPathRole / WasperPathRoleMetadata role tags
// travel on curve UserStrings for Curve-tree components. wsp_path carries the
// analogous per-branch PathRoles / StrokeIds DataTree<int> fields instead;
// every kept/split branch inherits its source branch's stored role and
// stroke id unchanged, since trimming does not change what a branch
// semantically is.

#region Usings
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
using WASPer_3DP;
#endregion

namespace WASPer_3DP_Components._4_0_Print_Paths
{
    public class wsp_Pp19_Trim_Paths_with_Fields : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Pp19_Trim_Paths_with_Fields()
            : base(
                "wsp_Pp19_Trim Paths with Fields",
                "TrimPathField",
                "Trims a WASPer Print Path (wsp_path) against one or more WASPer 3D fields, dropping or keeping " +
                "canonical points and splitting branches at the field boundary.\n" +
                "Multiple fields are combined as an SDF union (minimum value): by default, a point " +
                "is kept when it is inside any supplied field.\n" +
                "Default field convention: field <= 0 is inside / kept; field > 0 is outside / removed.\n" +
                "Set invert=true to keep the outside field side instead.\n" +
                "Use target_roles to apply the field trim only to selected semantic path roles; " +
                "non-target branches pass through unchanged.\n" +
                "Optional close_shell pairs retained Shell pieces from different source curves on each layer, " +
                "bridging their corresponding endpoints into one closed boundary. Unpaired pieces fall back to self-closure. " +
                "Only Infill branches meeting those new closing segments are re-trimmed to the new closed Shell regions, using the same " +
                "signed-distance constraint as Pp11-Pp14.\n" +
                "Trimming works at the existing wsp_path point resolution; no new interpolated boundary " +
                "point is inserted.\n\n" +
                "Please use the Pp01 WASPer Path from Curves before using this component.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "4.0_Print Paths")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("71D89DC8-3A6C-4648-B1D5-CA17E0C51513");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Pp19_Trim Paths with Fields.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        private int _visibleOutputsMask;
        private const string ShowAllOutputsKey = "wsp_pp19_show_all_outputs";
        private const string VisibleOutputsMaskKey = "wsp_pp19_visible_outputs_mask";

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
            while (Params.Output.Count > 2)
                Params.UnregisterOutputParameter(Params.Output[Params.Output.Count - 1], true);

            if (Params.Output.Count < 2)
            {
                while (Params.Output.Count > 0)
                    Params.UnregisterOutputParameter(Params.Output[Params.Output.Count - 1], true);
                Params.RegisterOutputParam(new Param_GenericObject { Name = "wsp_path", NickName = "wsp_path", Description = "Trimmed WASPer Print Path object.", Access = GH_ParamAccess.item });
                Params.RegisterOutputParam(new Param_String { Name = "summary", NickName = "summary", Description = "Trim summary.", Access = GH_ParamAccess.item });
            }
            WasperPathDebugOutputs.RegisterCore(this, IsOutputVisible);
            Params.OnParametersChanged();
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "WASPer Print Path object to trim. Its canonical pt_planes/point origins are evaluated " +
                "against trim_field. Please use the Pp01 WASPer Path from Curves before using this component.",
                GH_ParamAccess.item);

            p.AddGenericParameter(
                "trim_field", "field",
                "One or more WASPer 3D fields used as separated or overlapping trimming volumes. Fields are combined by minimum value (SDF union). Default: a location is kept when it is inside any field (combined field <= 0). Use invert=true to keep locations outside the complete union instead.",
                GH_ParamAccess.list);

            p.AddBooleanParameter(
                "invert",
                "invert",
                "Invert the trim side. False keeps field <= 0 (inside) and removes field > 0. True keeps field > 0 (outside) and removes field <= 0.",
                GH_ParamAccess.item,
                false);

            p.AddBooleanParameter(
                "close_shell",
                "close_shell",
                "Try to re-close trimmed Shell paths. On each logical layer, open Shell pieces from different source curves are paired by the shortest compatible endpoint bridges and combined into one closed boundary. This is especially useful when two closed reference curves define opposite shell boundaries. An unpaired piece closes back to itself as a fallback. Infill is subsequently constrained to the resulting closed Shell regions using the shared fuzzy-component signed-distance trimmer. Partition, Support, Transition, and Undefined paths are never closed. False by default because this adds fabrication geometry along the trim boundary.",
                GH_ParamAccess.item,
                false);

            p.AddParameter(global::WASPer_3DP.WasperTargetRolesParam.Create(
                "Selects which semantic path branches are trimmed by the fields. " +
                "0 = All paths (default), 1 = Shell, 2 = Infill, 3 = Partition, " +
                "4 = Support, 5 = Transition, 6 = Undefined. Supply several role-specific " +
                "values to include them and exclude the others. All paths (0) cannot be combined. " +
                "Non-target branches retain their complete geometry and metadata. When close_shell " +
                "is enabled, only targeted Shell branches are closed; the resulting local Infill " +
                "constraint remains a derived closure correction."));
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("wsp_path", "wsp_path", "Trimmed WASPer Print Path object.", GH_ParamAccess.item);
            p.AddTextParameter("summary", "summary", "Trim summary.", GH_ParamAccess.item);
            // Optional debug outputs are added dynamically by RebuildOutputs(), based on the
            // persisted _visibleOutputsMask. RegisterOutputParams() runs once at construction,
            // before Read() has restored any persisted state, so a mask-gated branch here would
            // never fire.
        }

        private sealed class Piece
        {
            public GH_Path OutPath;
            public GH_Path SourcePath;
            public List<int> Indices;
            public List<PieceSample> Samples;
            public bool ClosesShell;
            public bool EligibleShellClosure;
            public List<BoundingBox> ClosureZones;
        }

        private sealed class PieceSample
        {
            public GH_Path SourcePath;
            public int Index;
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            global::WASPer_3DP.WasperPrintPath packedPath = null;
            bool hasPackedPath = global::WASPer_3DP.WasperGcodeTreeUtil.TryGetPrintPath(DA, 0, out packedPath);

            if (!hasPackedPath || packedPath == null || !packedPath.HasPoints)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Pp19 requires a valid wsp_path input. Please use the Pp01 WASPer Path from Curves before using this component.");
                return;
            }

            var fieldGoos = new List<IGH_Goo>();
            if (!DA.GetDataList(1, fieldGoos) || fieldGoos.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No trim_field was provided.");
                return;
            }

            var fields = new List<WasperField>(fieldGoos.Count);
            for (int i = 0; i < fieldGoos.Count; i++)
            {
                WasperField field = ExtractField(fieldGoos[i]);
                if (field == null || field.Evaluator == null)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        $"trim_field item {i} is not a valid WASPer field. Use fields from Fi3d01, In08-In12, or another 2.3_Fields_3D component.");
                    return;
                }
                fields.Add(field);
            }

            bool invert = false;
            DA.GetData(2, ref invert);
            bool closeShell = false;
            DA.GetData(3, ref closeShell);
            var targetRoles = new List<int>();
            DA.GetDataList(4, targetRoles);
            if (!global::WASPer_3DP.WasperGcodeTreeUtil.TryNormalizeTargetRoles(
                    targetRoles,
                    out targetRoles,
                    out string roleError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, roleError);
                return;
            }

            double tol = RhinoDoc.ActiveDoc != null
                ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
                : 1e-6;
            tol = Math.Max(tol, 1e-9);

            int branchCount = packedPath.Points.BranchCount;
            var branchPaths = new GH_Path[branchCount];
            var branchRoles = new WasperPathRole[branchCount];
            var targetedBranches = new bool[branchCount];
            var keepPerBranch = new bool[branchCount][];
            int targetedBranchCount = 0;

            for (int bi = 0; bi < branchCount; bi++)
            {
                GH_Path path = packedPath.Points.Paths[bi];
                branchPaths[bi] = path;
                WasperPathRole role = global::WASPer_3DP.WasperGcodeTreeUtil.PathRoleAt(
                    packedPath.PathRoles,
                    path);
                branchRoles[bi] = role;
                bool targeted = global::WASPer_3DP.WasperGcodeTreeUtil.MatchesTargetRoles(
                    role,
                    targetRoles);
                targetedBranches[bi] = targeted;
                if (targeted)
                    targetedBranchCount++;
            }

            if (targetedBranchCount == 0)
            {
                string roleNames = global::WASPer_3DP.WasperGcodeTreeUtil.TargetRoleNames(targetRoles);
                string unchangedSummary =
                    $"OK | target_roles={roleNames} | targeted_branches=0 | unchanged";
                WasperPathDebugOutputs.Set(DA, this, packedPath, unchangedSummary);
                Message = $"{_versionTag} | 0 targeted";
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"No {roleNames} branches were found. The input wsp_path passes through unchanged.");
                return;
            }

            Action<int> evalBranch = bi =>
            {
                GH_Path srcPath = packedPath.Points.Paths[bi];
                IList<Point3d> pts = packedPath.Points.Branch(srcPath);
                int n = pts != null ? pts.Count : 0;
                var keep = new bool[n];
                for (int i = 0; i < n; i++)
                    keep[i] = targetedBranches[bi]
                        ? pts[i].IsValid && IsKept(fields, pts[i], tol, invert)
                        : true;
                keepPerBranch[bi] = keep;
            };

            bool doParallel = branchCount >= 4;
            if (doParallel)
                Parallel.For(0, branchCount, evalBranch);
            else
                for (int bi = 0; bi < branchCount; bi++) evalBranch(bi);

            var pieces = new List<Piece>();
            int inputPointCount = 0;
            int outputPointCount = 0;
            int branchesKept = 0;
            int branchesDropped = 0;
            int branchesSplit = 0;
            int shellPiecesClosed = 0;
            int shellPairsClosed = 0;
            int shellSelfClosed = 0;

            for (int bi = 0; bi < branchCount; bi++)
            {
                GH_Path srcPath = branchPaths[bi];
                bool[] keep = keepPerBranch[bi];
                int n = keep.Length;
                inputPointCount += n;
                if (n == 0) continue;

                var runs = new List<List<int>>();
                List<int> current = null;
                for (int i = 0; i < n; i++)
                {
                    if (keep[i])
                    {
                        if (current == null) { current = new List<int>(); runs.Add(current); }
                        current.Add(i);
                    }
                    else current = null;
                }

                IList<Point3d> sourcePoints = packedPath.Points.Branch(srcPath);
                bool sourceClosed = IsClosed(sourcePoints, tol);
                if (sourceClosed && runs.Count > 1 && keep[0] && keep[n - 1])
                {
                    List<int> first = runs[0];
                    List<int> last = runs[runs.Count - 1];
                    var merged = new List<int>(last.Count + Math.Max(0, first.Count - 1));
                    merged.AddRange(last);
                    for (int i = 1; i < first.Count; i++)
                        merged.Add(first[i]);
                    runs[0] = merged;
                    runs.RemoveAt(runs.Count - 1);
                }
                runs.RemoveAll(r => r.Count < 2);

                if (runs.Count == 0)
                {
                    branchesDropped++;
                    continue;
                }

                WasperPathRole sourceRole = branchRoles[bi];
                bool eligibleShellSource =
                    closeShell &&
                    targetedBranches[bi] &&
                    sourceRole == WasperPathRole.Shell;

                branchesKept++;
                if (runs.Count == 1)
                {
                    pieces.Add(new Piece
                    {
                        OutPath = srcPath,
                        SourcePath = srcPath,
                        Indices = runs[0],
                        EligibleShellClosure = eligibleShellSource &&
                            IsOpenRun(sourcePoints, runs[0], tol)
                    });
                }
                else
                {
                    branchesSplit++;
                    for (int k = 0; k < runs.Count; k++)
                    {
                        pieces.Add(new Piece
                        {
                            OutPath = srcPath.AppendElement(k),
                            SourcePath = srcPath,
                            Indices = runs[k],
                            EligibleShellClosure = eligibleShellSource &&
                                IsOpenRun(sourcePoints, runs[k], tol)
                        });
                    }
                }
            }

            if (closeShell && pieces.Count > 0)
            {
                CloseShellPiecesByLayer(
                    packedPath,
                    pieces,
                    tol,
                    out shellPairsClosed,
                    out shellSelfClosed);
                shellPiecesClosed = shellPairsClosed + shellSelfClosed;
            }
            outputPointCount = pieces.Sum(PieceSampleCount);

            if (pieces.Count == 0)
            {
                var emptyPath = new global::WASPer_3DP.WasperPrintPath(
                    new DataTree<Point3d>(),
                    new DataTree<Plane>(),
                    null,
                    null,
                    nozzleDiam: packedPath.NozzleDiam,
                    layerPlanes: packedPath.LayerPlanes,
                    isPartial: true);
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    invert
                        ? "No path points were outside the combined field union; the trimmed wsp_path is empty."
                        : "No path points were inside any supplied field; the trimmed wsp_path is empty.");
                WasperPathDebugOutputs.Set(
                    DA, this, emptyPath,
                    $"OK | 0/{inputPointCount} points kept | fields={fields.Count} | invert={invert} | empty");
                Message = $"{_versionTag} | 0/{inputPointCount}";
                return;
            }

            DataTree<Point3d> outPts = SplitOptionalTree(packedPath.Points, pieces);
            DataTree<Plane> outPlanes = packedPath.HasPlanes ? SplitOptionalTree(packedPath.PtPlanes, pieces) : null;
            DataTree<double> outFlows = packedPath.HasFlows ? SplitOptionalTree(packedPath.Flows, pieces) : null;
            DataTree<double> outLayerH = packedPath.HasLayerH ? SplitOptionalTree(packedPath.LayerH, pieces) : null;
            DataTree<double> outPrintSpeed = SplitOptionalTree(packedPath.PrintSpeed, pieces);
            DataTree<double> outLayerW = SplitOptionalTree(packedPath.LayerW, pieces);
            DataTree<double> outLayerWf = SplitOptionalTree(packedPath.LayerWf, pieces);
            bool addedClosingSegments = shellPiecesClosed > 0;

            if (addedClosingSegments)
            {
                UsePreviousValueForClosingSegment(outFlows, pieces);
                UsePreviousValueForClosingSegment(outPrintSpeed, pieces);
                UsePreviousValueForClosingSegment(outLayerWf, pieces);
            }

            DataTree<double> outPrintVol = addedClosingSegments
                ? RebuildPrintVolume(outPts, outLayerH, outLayerWf, tol)
                : SplitOptionalTree(packedPath.PrintVol, pieces);

            DataTree<int> outPathRoles = null;
            if (packedPath.HasPathRoles)
            {
                outPathRoles = new DataTree<int>();
                foreach (Piece piece in pieces)
                {
                    WasperPathRole role = global::WASPer_3DP.WasperGcodeTreeUtil.PathRoleAt(packedPath.PathRoles, piece.SourcePath);
                    outPathRoles.Add((int)role, piece.OutPath);
                }
            }

            DataTree<int> outStrokeIds = null;
            if (packedPath.HasStrokeIds)
            {
                outStrokeIds = new DataTree<int>();
                foreach (Piece piece in pieces)
                {
                    int sid = global::WASPer_3DP.WasperGcodeTreeUtil.StrokeIdAt(packedPath.StrokeIds, piece.SourcePath);
                    if (sid >= 0)
                        outStrokeIds.Add(sid, piece.OutPath);
                }
            }

            var outPath = new global::WASPer_3DP.WasperPrintPath(
                outPts,
                outPlanes,
                outFlows,
                outLayerH,
                printSpeed: outPrintSpeed,
                printLoc: addedClosingSegments ? null : SplitOptionalTree(packedPath.PrintLoc, pieces),
                printGlob: addedClosingSegments ? null : SplitOptionalTree(packedPath.PrintGlob, pieces),
                supportPts: addedClosingSegments ? null : SplitOptionalTree(packedPath.SupportPts, pieces),
                supportVects: addedClosingSegments ? null : SplitOptionalTree(packedPath.SupportVects, pieces),
                angles: addedClosingSegments ? null : SplitOptionalTree(packedPath.Angles, pieces),
                contactWidths: addedClosingSegments ? null : SplitOptionalTree(packedPath.ContactWidths, pieces),
                riskMaterial: addedClosingSegments ? null : SplitOptionalTree(packedPath.RiskMaterial, pieces),
                riskComb: addedClosingSegments ? null : SplitOptionalTree(packedPath.RiskComb, pieces),
                load: addedClosingSegments ? null : SplitOptionalTree(packedPath.Load, pieces),
                capacity: addedClosingSegments ? null : SplitOptionalTree(packedPath.Capacity, pieces),
                nozzleDiam: packedPath.NozzleDiam,
                dRatio: addedClosingSegments ? null : SplitOptionalTree(packedPath.DRatio, pieces),
                dLoaded: addedClosingSegments ? null : SplitOptionalTree(packedPath.DLoaded, pieces),
                bendRatio: addedClosingSegments ? null : SplitOptionalTree(packedPath.BendRatio, pieces),
                spanClass: addedClosingSegments ? null : SplitOptionalTree(packedPath.SpanClass, pieces),
                spanLen: addedClosingSegments ? null : SplitOptionalTree(packedPath.SpanLen, pieces),
                collapsed: addedClosingSegments ? null : SplitOptionalTree(packedPath.Collapsed, pieces),
                cascade: addedClosingSegments ? null : SplitOptionalTree(packedPath.Cascade, pieces),
                collapseGen: addedClosingSegments ? null : SplitOptionalTree(packedPath.CollapseGen, pieces),
                layerW: outLayerW,
                layerWf: outLayerWf,
                printVol: outPrintVol,
                torn: addedClosingSegments ? null : SplitOptionalTree(packedPath.Torn, pieces),
                interfaceRatio: addedClosingSegments ? null : SplitOptionalTree(packedPath.InterfaceRatio, pieces),
                overturnRatio: addedClosingSegments ? null : SplitOptionalTree(packedPath.OverturnRatio, pieces),
                failureFlags: addedClosingSegments ? null : SplitOptionalTree(packedPath.FailureFlags, pieces),
                pathRoles: outPathRoles,
                layerPlanes: packedPath.LayerPlanes,
                strokeIds: outStrokeIds,
                isPartial: true);

            WasperInfillConstraintReport infillConstraintReport = null;
            if (addedClosingSegments)
            {
                var newlyClosedShellPaths = new List<GH_Path>();
                foreach (Piece piece in pieces)
                {
                    if (piece.ClosesShell)
                        newlyClosedShellPaths.Add(piece.OutPath);
                }
                Dictionary<GH_Path, IList<BoundingBox>> closureZonesByPath =
                    BuildClosureZonesByPath(pieces);
                outPath = WasperInfillShellConstraint.ApplyCurrentShellUnion(
                    outPath,
                    newlyClosedShellPaths,
                    tol,
                    out infillConstraintReport,
                    null,
                    closureZonesByPath);
            }

            string summary =
                $"OK | points={outputPointCount}/{inputPointCount} | " +
                $"branches={branchesKept}/{branchCount} (dropped={branchesDropped}, split={branchesSplit}) | " +
                $"fields={fields.Count} | invert={invert} | " +
                $"target_roles={WASPer_3DP.WasperGcodeTreeUtil.TargetRoleNames(targetRoles)} | " +
                $"targeted_branches={targetedBranchCount} | shell_loops_closed={shellPiecesClosed} " +
                $"(paired={shellPairsClosed}, self={shellSelfClosed}) | " +
                $"{(addedClosingSegments ? infillConstraintReport?.Summary ?? "infill constraint: not applicable" : "infill constraint: not required")}";

            WasperPathDebugOutputs.Set(DA, this, outPath, summary);

            Message = doParallel ? $"{_versionTag} | {outputPointCount}/{inputPointCount} | P" : $"{_versionTag} | {outputPointCount}/{inputPointCount}";

            if (branchesSplit > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    $"{branchesSplit} input branch(es) were split into multiple output branches by the trim field boundary.");
            if (addedClosingSegments)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"Closed {shellPiecesClosed} Shell loop(s): {shellPairsClosed} paired across different source curves " +
                    $"and {shellSelfClosed} self-closed fallback(s). " +
                    "Print volume was recomputed where layer_h/layer_wf were available; geometry-derived analysis fields were cleared and should be recomputed downstream.");
            if (infillConstraintReport?.LayersSkipped > 0)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{infillConstraintReport.LayersSkipped} logical layer(s) could not constrain Infill " +
                    "because no valid closed planar Shell boundary was available on that layer.");
        }

        private static bool IsOpenRun(
            IList<Point3d> sourcePoints,
            IList<int> run,
            double tolerance)
        {
            if (sourcePoints == null || run == null || run.Count < 2)
                return false;
            int first = run[0];
            int last = run[run.Count - 1];
            return first >= 0 && first < sourcePoints.Count &&
                   last >= 0 && last < sourcePoints.Count &&
                   sourcePoints[first].IsValid && sourcePoints[last].IsValid &&
                   sourcePoints[first].DistanceTo(sourcePoints[last]) > tolerance;
        }

        private static int PieceSampleCount(Piece piece)
        {
            if (piece?.Samples != null)
                return piece.Samples.Count;
            return piece?.Indices?.Count ?? 0;
        }

        private static List<PieceSample> PieceSamples(Piece piece, bool reverse = false)
        {
            var samples = piece?.Samples != null
                ? piece.Samples.Select(sample => new PieceSample
                {
                    SourcePath = sample.SourcePath,
                    Index = sample.Index
                }).ToList()
                : (piece?.Indices ?? new List<int>()).Select(index => new PieceSample
                {
                    SourcePath = piece.SourcePath,
                    Index = index
                }).ToList();
            if (reverse)
                samples.Reverse();
            return samples;
        }

        private static Point3d SamplePoint(
            WasperPrintPath source,
            PieceSample sample)
        {
            if (source?.Points == null || sample?.SourcePath == null ||
                !source.Points.PathExists(sample.SourcePath))
                return Point3d.Unset;
            IList<Point3d> branch = source.Points.Branch(sample.SourcePath);
            return branch != null && sample.Index >= 0 && sample.Index < branch.Count
                ? branch[sample.Index]
                : Point3d.Unset;
        }

        private static void CloseShellPiecesByLayer(
            WasperPrintPath source,
            List<Piece> pieces,
            double tolerance,
            out int pairedLoops,
            out int selfClosedLoops)
        {
            pairedLoops = 0;
            selfClosedLoops = 0;
            if (source?.Points == null || pieces == null || pieces.Count == 0)
                return;

            int commonPrefix = WasperGcodeTreeUtil.CommonPathPrefixLength(
                source.Points.Paths.ToList());
            var candidatesByLayer = pieces
                .Where(piece => piece != null && piece.EligibleShellClosure)
                .GroupBy(piece => ShellLayerKey(
                    source,
                    piece.SourcePath,
                    commonPrefix))
                .Select(group => group.ToList())
                .ToList();

            foreach (List<Piece> layerGroup in candidatesByLayer)
            {
                var candidates = new List<Piece>(layerGroup);
                while (candidates.Count >= 2)
                {
                    Piece bestA = null;
                    Piece bestB = null;
                    bool reverseB = false;
                    double bestCost = double.PositiveInfinity;

                    for (int i = 0; i < candidates.Count - 1; i++)
                    for (int j = i + 1; j < candidates.Count; j++)
                    {
                        Piece first = candidates[i];
                        Piece second = candidates[j];
                        if (first.SourcePath.Equals(second.SourcePath))
                            continue;
                        if (!TryPairCost(
                                source,
                                first,
                                second,
                                out double cost,
                                out bool candidateReverseB))
                            continue;
                        if (cost < bestCost)
                        {
                            bestCost = cost;
                            bestA = first;
                            bestB = second;
                            reverseB = candidateReverseB;
                        }
                    }

                    if (bestA == null || bestB == null)
                        break;

                    MergeShellPair(source, bestA, bestB, reverseB);
                    pieces.Remove(bestB);
                    candidates.Remove(bestA);
                    candidates.Remove(bestB);
                    pairedLoops++;
                }

                foreach (Piece remaining in candidates)
                {
                    SelfClosePiece(source, remaining);
                    selfClosedLoops++;
                }
            }
        }

        private static string ShellLayerKey(
            WasperPrintPath source,
            GH_Path path,
            int commonPrefix)
        {
            if (source?.HasLayerPlanes == true && path != null)
            {
                GH_Path best = null;
                foreach (GH_Path layerPath in source.LayerPlanes.Paths)
                {
                    if (IsPathPrefix(layerPath, path) &&
                        (best == null || layerPath.Length > best.Length))
                        best = layerPath;
                }
                if (best != null)
                    return best.ToString();
            }
            return WasperGcodeTreeUtil.LayerPlanePath(path, commonPrefix).ToString();
        }

        private static bool IsPathPrefix(GH_Path prefix, GH_Path path)
        {
            if (prefix == null || path == null || prefix.Length > path.Length)
                return false;
            for (int i = 0; i < prefix.Length; i++)
                if (prefix[i] != path[i])
                    return false;
            return true;
        }

        private static bool TryPairCost(
            WasperPrintPath source,
            Piece first,
            Piece second,
            out double cost,
            out bool reverseSecond)
        {
            cost = double.PositiveInfinity;
            reverseSecond = false;
            List<PieceSample> a = PieceSamples(first);
            List<PieceSample> b = PieceSamples(second);
            if (a.Count < 2 || b.Count < 2)
                return false;

            Point3d aStart = SamplePoint(source, a[0]);
            Point3d aEnd = SamplePoint(source, a[a.Count - 1]);
            Point3d bStart = SamplePoint(source, b[0]);
            Point3d bEnd = SamplePoint(source, b[b.Count - 1]);
            if (!aStart.IsValid || !aEnd.IsValid || !bStart.IsValid || !bEnd.IsValid)
                return false;

            double traverseBForward = aEnd.DistanceTo(bStart) + bEnd.DistanceTo(aStart);
            double traverseBReverse = aEnd.DistanceTo(bEnd) + bStart.DistanceTo(aStart);
            reverseSecond = traverseBReverse <= traverseBForward;
            cost = reverseSecond ? traverseBReverse : traverseBForward;
            return double.IsFinite(cost);
        }

        private static void MergeShellPair(
            WasperPrintPath source,
            Piece first,
            Piece second,
            bool reverseSecond)
        {
            List<PieceSample> a = PieceSamples(first);
            List<PieceSample> b = PieceSamples(second, reverseSecond);
            Point3d bridgeOneA = SamplePoint(source, a[a.Count - 1]);
            Point3d bridgeOneB = SamplePoint(source, b[0]);
            Point3d bridgeTwoA = SamplePoint(source, b[b.Count - 1]);
            Point3d bridgeTwoB = SamplePoint(source, a[0]);

            var merged = new List<PieceSample>(a.Count + b.Count + 1);
            merged.AddRange(a);
            merged.AddRange(b);
            merged.Add(new PieceSample
            {
                SourcePath = a[0].SourcePath,
                Index = a[0].Index
            });

            first.Samples = merged;
            first.Indices = null;
            first.ClosesShell = true;
            first.EligibleShellClosure = false;
            first.ClosureZones = new List<BoundingBox>
            {
                SegmentBounds(bridgeOneA, bridgeOneB),
                SegmentBounds(bridgeTwoA, bridgeTwoB)
            };
        }

        private static void SelfClosePiece(WasperPrintPath source, Piece piece)
        {
            List<PieceSample> samples = PieceSamples(piece);
            if (samples.Count < 2)
                return;
            Point3d from = SamplePoint(source, samples[samples.Count - 1]);
            Point3d to = SamplePoint(source, samples[0]);
            samples.Add(new PieceSample
            {
                SourcePath = samples[0].SourcePath,
                Index = samples[0].Index
            });
            piece.Samples = samples;
            piece.Indices = null;
            piece.ClosesShell = true;
            piece.EligibleShellClosure = false;
            piece.ClosureZones = new List<BoundingBox> { SegmentBounds(from, to) };
        }

        private static BoundingBox SegmentBounds(Point3d from, Point3d to)
        {
            var bounds = BoundingBox.Empty;
            if (from.IsValid) bounds.Union(from);
            if (to.IsValid) bounds.Union(to);
            return bounds;
        }

        private static Dictionary<GH_Path, IList<BoundingBox>> BuildClosureZonesByPath(
            IEnumerable<Piece> pieces)
        {
            var result = new Dictionary<GH_Path, IList<BoundingBox>>();
            foreach (Piece piece in pieces ?? Enumerable.Empty<Piece>())
            {
                if (!piece.ClosesShell || piece.OutPath == null ||
                    piece.ClosureZones == null || piece.ClosureZones.Count == 0)
                    continue;
                result[piece.OutPath] = piece.ClosureZones
                    .Where(bounds => bounds.IsValid)
                    .ToList();
            }
            return result;
        }

        private static DataTree<T> SplitOptionalTree<T>(DataTree<T> source, List<Piece> pieces)
        {
            if (source == null || pieces == null || pieces.Count == 0) return null;

            var result = new DataTree<T>();
            bool any = false;
            foreach (Piece piece in pieces)
            {
                result.EnsurePath(piece.OutPath);
                if (piece.Samples != null)
                {
                    GH_Path currentPath = null;
                    IList<T> values = null;
                    foreach (PieceSample sample in piece.Samples)
                    {
                        if (sample?.SourcePath == null)
                            continue;
                        if (currentPath == null || !currentPath.Equals(sample.SourcePath))
                        {
                            currentPath = sample.SourcePath;
                            values = source.PathExists(currentPath)
                                ? source.Branch(currentPath)
                                : null;
                        }
                        if (values == null || sample.Index < 0 || sample.Index >= values.Count)
                            continue;
                        result.Add(values[sample.Index], piece.OutPath);
                        any = true;
                    }
                    continue;
                }

                if (piece.SourcePath == null || piece.Indices == null ||
                    !source.PathExists(piece.SourcePath))
                    continue;
                IList<T> sourceValues = source.Branch(piece.SourcePath);
                if (sourceValues == null)
                    continue;
                foreach (int index in piece.Indices)
                {
                    if (index < 0 || index >= sourceValues.Count)
                        continue;
                    result.Add(sourceValues[index], piece.OutPath);
                    any = true;
                }
            }
            return any ? result : null;
        }

        private static void UsePreviousValueForClosingSegment<T>(
            DataTree<T> tree,
            IList<Piece> pieces)
        {
            if (tree == null || pieces == null)
                return;

            foreach (Piece piece in pieces)
            {
                if (!piece.ClosesShell || !tree.PathExists(piece.OutPath))
                    continue;
                IList<T> values = tree.Branch(piece.OutPath);
                if (values != null && values.Count >= 2)
                    values[values.Count - 1] = values[values.Count - 2];
            }
        }

        private static DataTree<double> RebuildPrintVolume(
            DataTree<Point3d> points,
            DataTree<double> layerH,
            DataTree<double> layerWf,
            double tolerance)
        {
            if (points == null || layerH == null || layerWf == null)
                return null;

            var result = new DataTree<double>();
            for (int branchIndex = 0; branchIndex < points.BranchCount; branchIndex++)
            {
                GH_Path path = points.Path(branchIndex);
                if (!layerH.PathExists(path) || !layerWf.PathExists(path))
                    continue;

                IList<Point3d> pointValues = points.Branch(branchIndex);
                IList<double> heightValues = layerH.Branch(path);
                IList<double> widthValues = layerWf.Branch(path);
                if (pointValues == null || heightValues == null || widthValues == null)
                    continue;

                for (int i = 0; i < pointValues.Count; i++)
                {
                    double volume = 0.0;
                    if (i > 0 && i < heightValues.Count && i < widthValues.Count)
                    {
                        double length = pointValues[i - 1].DistanceTo(pointValues[i]);
                        double area = BeadArea(widthValues[i], heightValues[i], tolerance);
                        if (double.IsFinite(length) && length > tolerance && area > 0.0)
                            volume = length * area;
                    }
                    result.Add(volume, path);
                }
            }

            return result.DataCount > 0 ? result : null;
        }

        private static double BeadArea(double width, double height, double tolerance)
        {
            if (!double.IsFinite(width) || !double.IsFinite(height) ||
                width <= tolerance || height <= tolerance)
                return 0.0;

            double effectiveWidth = Math.Max(width, height * (1.0 - Math.PI / 4.0));
            double area = height * (effectiveWidth - height) + Math.PI * height * height / 4.0;
            return double.IsFinite(area) && area > 0.0 ? area : 0.0;
        }

        private static bool IsClosed(IList<Point3d> points, double tolerance)
        {
            return points != null &&
                   points.Count >= 3 &&
                   points[0].IsValid &&
                   points[points.Count - 1].IsValid &&
                   points[0].DistanceTo(points[points.Count - 1]) <= tolerance;
        }

        private static bool IsKept(
            IList<WasperField> fields,
            Point3d point,
            double tol,
            bool invert)
        {
            double threshold = Math.Max(tol, 1e-7);
            for (int i = 0; i < (fields?.Count ?? 0); i++)
            {
                if (SafeEvaluate(fields[i], point) <= threshold)
                    return !invert;
            }
            return invert;
        }

        private static double SafeEvaluate(WasperField field, Point3d point)
        {
            try
            {
                double value = field.Evaluate(point);
                return (double.IsNaN(value) || double.IsInfinity(value))
                    ? double.PositiveInfinity
                    : value;
            }
            catch
            {
                return double.PositiveInfinity;
            }
        }

        private static WasperField ExtractField(IGH_Goo goo)
        {
            if (goo == null) return null;

            if (goo is WasperFieldGoo fg) return fg.Value;

            object sv = null;
            try { sv = goo.ScriptVariable(); } catch { sv = null; }

            if (sv is WasperField f) return f;
            if (sv is WasperFieldGoo fgoo) return fgoo.Value;

            if (goo is GH_ObjectWrapper wrapper)
            {
                if (wrapper.Value is WasperField wf) return wf;
                if (wrapper.Value is WasperFieldGoo wg) return wg.Value;
            }

            return null;
        }
    }
}
