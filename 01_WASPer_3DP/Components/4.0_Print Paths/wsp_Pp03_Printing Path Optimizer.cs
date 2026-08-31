using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

using Rhino;
using Rhino.Geometry;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using WASPer_3DP;

namespace WASPer_3DP_Components._4_0_Print_Paths
{
    public class WSP_Pp03_PrintingPathOptimizer : GH_Component
    {
        // --------------------------------------------------------------------
        // Version tag (taken from WASPer_3DP assembly)
        // --------------------------------------------------------------------
        private readonly string _versionTag;

        /// <summary>
        /// Constructor
        /// </summary>
        public WSP_Pp03_PrintingPathOptimizer()
          : base(
                "wsp_Pp03_Printing Path Optimizer",    // Name
                "Path Optimizer",                      // Nickname
                "Optimizes curve order inside each layer using a packed WASPer Print Path. " +
                "Canonical point planes carry both path locations (their origins) and orientations; " +
                "flows, layer heights, widths, speeds, and other aligned metadata are reordered with them. " +
                "wsp_path is the primary input and output; diagnostic trees are hidden by default.\r\n\r\n" +
                "Please use the Pp01 WASPer Path from Curves before using this component.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,                         // Category
                "4.0_Print Paths"                             // Subcategory
            )
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null
                ? $"v{v.Major}.{v.Minor}.{v.Build}"
                : "v1.0.x";

            this.Message = _versionTag;
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("7C3A1E53-3CD1-4830-A665-85F4B826FB40"); }
        }

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.04_PpathOpt.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }


        private int _visibleOutputsMask;
        private const string ShowAllOutputsKey = "wsp_gc05_show_all_outputs";
        private const string VisibleOutputsMaskKey = "wsp_gc05_visible_outputs_mask";

        private static readonly string[] OutputCatalog =
            WasperPathDebugOutputs.CoreNickNames.Concat(new[] { "source_indices" }).ToArray();
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
                RegisterCompactOutputParams();
            }

            RegisterDebugOutputParams();
            Params.OnParametersChanged();
        }

        private void RegisterCompactOutputParams()
        {
            Params.RegisterOutputParam(new Param_GenericObject { Name = "wsp_path", NickName = "wsp_path", Description = "Optimized WASPer Print Path object.", Access = GH_ParamAccess.item });
            Params.RegisterOutputParam(new Param_String { Name = "summary", NickName = "summary", Description = "Optimization summary.", Access = GH_ParamAccess.item });
        }

        private void RegisterDebugOutputParams()
        {
            WasperPathDebugOutputs.RegisterCore(this, IsOutputVisible);
            if (IsOutputVisible("source_indices"))
                Params.RegisterOutputParam(new Param_Integer { Name = "source_indices", NickName = "source_indices", Description = "Original curve indices for each optimized branch.", Access = GH_ParamAccess.tree });
        }

        /// <summary>
        /// Register input parameters
        /// </summary>
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "WASPer Print Path object to optimize. Supplies canonical point planes, flows, layer_h, and all packed path metadata. Please use the Pp01 WASPer Path from Curves before using this component.",
                GH_ParamAccess.item);

            pManager.AddPointParameter(
                "ref_point",
                "ref_pt",
                "Optional reference point for starting the optimization. If unset, uses the first point of the first curve.",
                GH_ParamAccess.item,
                new Point3d(double.NaN, double.NaN, double.NaN));
            pManager[1].Optional = true;

            pManager.AddParameter(global::WASPer_3DP.WasperTargetRolesParam.Create(
                "Selects which semantic path branches are optimized. 0 = All paths (default), " +
                "1 = Shell, 2 = Infill, 3 = Partition, 4 = Support, 5 = Transition, 6 = Undefined. Supply several role-specific values to " +
                "include them and exclude the others. All paths (0) cannot be combined. Non-target " +
                "branches keep their original slots and relative order."));
        }

        /// <summary>
        /// Register output parameters
        /// </summary>
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "Optimized WASPer Print Path object carrying reordered canonical point planes, flows, layer heights, and packed metadata.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "summary",
                "summary",
                "Optimization summary.",
                GH_ParamAccess.item);

            // Optional debug outputs are added dynamically by RebuildOutputs()/RegisterDebugOutputParams(),
            // based on the persisted _visibleOutputsMask. RegisterOutputParams() runs once at construction,
            // before Read() has restored any persisted state, so a mask-gated branch here would never fire.
        }

        /// <summary>
        /// Main solve method
        /// </summary>
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // Read inputs
            GH_Structure<GH_Point> ghPoints = null;
            GH_Structure<GH_Plane> ghPointPlanes = null;
            GH_Structure<GH_Number> ghFlows = null;
            GH_Structure<GH_Number> ghLayerHeights = null;
            Point3d refPoint = Point3d.Unset;
            var targetRoles = new List<int>();
            WasperPrintPath packedPath = null;

            bool hasPackedPath = WasperGcodeTreeUtil.TryGetPrintPath(DA, 0, out packedPath);
            if (!hasPackedPath || packedPath == null || !packedPath.HasPoints)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Pp03 requires a valid wsp_path input. Please use the Pp01 WASPer Path from Curves before using this component.");
                return;
            }

            ghPoints = WasperGcodeTreeUtil.ToPointStructure(packedPath.Points);
            ghPointPlanes = packedPath.HasPlanes ? WasperGcodeTreeUtil.ToPlaneStructure(packedPath.PtPlanes) : null;
            ghFlows = packedPath.HasFlows ? WasperGcodeTreeUtil.ToNumberStructure(packedPath.Flows) : null;
            ghLayerHeights = packedPath.HasLayerH ? WasperGcodeTreeUtil.ToNumberStructure(packedPath.LayerH) : null;

            bool hasPointPlanes = ghPointPlanes != null && ghPointPlanes.PathCount > 0;
            bool hasLayerHeights = ghLayerHeights != null && ghLayerHeights.PathCount > 0;
            if (ghFlows == null || ghFlows.PathCount == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Input wsp_path does not contain flows.");
                return;
            }

            DA.GetData(1, ref refPoint);
            DA.GetDataList(2, targetRoles);
            if (!WasperGcodeTreeUtil.TryNormalizeTargetRoles(
                    targetRoles,
                    out targetRoles,
                    out string roleError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, roleError);
                return;
            }

            // Prepare output data trees
            DataTree<Point3d> optPointsTree = new DataTree<Point3d>();
            DataTree<Plane> optPointPlanesTree = new DataTree<Plane>();
            DataTree<double> optFlowsTree = new DataTree<double>();
            DataTree<double> optLayerHeightsTree = new DataTree<double>();
            DataTree<int> indicesTree = new DataTree<int>();
            DataTree<double> optLayerWTree = packedPath?.LayerW == null ? null : new DataTree<double>();
            DataTree<double> optLayerWfTree = packedPath?.LayerWf == null ? null : new DataTree<double>();
            DataTree<double> optPrintVolTree = packedPath?.PrintVol == null ? null : new DataTree<double>();
            DataTree<double> optPrintSpeed = packedPath?.PrintSpeed == null ? null : new DataTree<double>();
            DataTree<double> optPrintLoc = packedPath?.PrintLoc == null ? null : new DataTree<double>();
            DataTree<bool> optPrintGlob = packedPath?.PrintGlob == null ? null : new DataTree<bool>();
            DataTree<Point3d> optSupportPts = packedPath?.SupportPts == null ? null : new DataTree<Point3d>();
            DataTree<Vector3d> optSupportVects = packedPath?.SupportVects == null ? null : new DataTree<Vector3d>();
            DataTree<double> optAngles = packedPath?.Angles == null ? null : new DataTree<double>();
            DataTree<double> optContactWidths = packedPath?.ContactWidths == null ? null : new DataTree<double>();
            DataTree<double> optRiskMaterial = packedPath?.RiskMaterial == null ? null : new DataTree<double>();
            DataTree<double> optRiskComb = packedPath?.RiskComb == null ? null : new DataTree<double>();
            DataTree<double> optLoad = packedPath?.Load == null ? null : new DataTree<double>();
            DataTree<double> optCapacity = packedPath?.Capacity == null ? null : new DataTree<double>();
            DataTree<double> optDRatio = packedPath?.DRatio == null ? null : new DataTree<double>();
            DataTree<double> optDLoaded = packedPath?.DLoaded == null ? null : new DataTree<double>();
            DataTree<double> optBendRatio = packedPath?.BendRatio == null ? null : new DataTree<double>();
            DataTree<int> optSpanClass = packedPath?.SpanClass == null ? null : new DataTree<int>();
            DataTree<double> optSpanLen = packedPath?.SpanLen == null ? null : new DataTree<double>();
            DataTree<bool> optCollapsed = packedPath?.Collapsed == null ? null : new DataTree<bool>();
            DataTree<bool> optCascade = packedPath?.Cascade == null ? null : new DataTree<bool>();
            DataTree<int> optCollapseGen = packedPath?.CollapseGen == null ? null : new DataTree<int>();
            DataTree<bool> optTorn = packedPath?.Torn == null ? null : new DataTree<bool>();
            DataTree<double> optInterfaceRatio = packedPath?.InterfaceRatio == null ? null : new DataTree<double>();
            DataTree<double> optOverturnRatio = packedPath?.OverturnRatio == null ? null : new DataTree<double>();
            DataTree<int> optFailureFlags = packedPath?.FailureFlags == null ? null : new DataTree<int>();
            DataTree<int> optPathRoles = packedPath?.PathRoles == null ? null : new DataTree<int>();

            // Build layer dictionary: layer index -> list of CurveData
            Dictionary<int, List<CurveData>> layers = new Dictionary<int, List<CurveData>>();

            // Iterate over each path (single-threaded, GH_Structure is not thread-safe)
            var paths = ghPoints.Paths;
            foreach (GH_Path path in paths)
            {
                // Expect path format {layer; curve}
                if (path.Indices.Length < 2)
                    continue;

                int layerIndex = path.Indices[0];
                int curveIndex = path.Indices[1];

                var ptBranch = ghPoints.get_Branch(path);
                var flBranch = ghFlows.get_Branch(path);

                // Convert GH_Point -> Point3d
                List<Point3d> pts = ptBranch
                    .OfType<GH_Point>()
                    .Select(gp => gp.Value)
                    .ToList();

                // Convert GH_Number -> double (flows)
                List<double> flx = flBranch
                    .OfType<GH_Number>()
                    .Select(fn => fn.Value)
                    .ToList();

                // Convert point_planes if available
                List<Plane> pointPlanes = null;
                if (hasPointPlanes && ghPointPlanes != null && ghPointPlanes.PathExists(path))
                {
                    var pBranch = ghPointPlanes.get_Branch(path);
                    if (pBranch != null)
                    {
                        pointPlanes = pBranch
                            .OfType<GH_Plane>()
                            .Select(p => p.Value)
                            .ToList();
                    }
                }

                // Fallback / length consistency for point_planes
                if (pointPlanes == null)
                {
                    pointPlanes = Enumerable.Repeat(Plane.Unset, pts.Count).ToList();
                }
                else if (pointPlanes.Count != pts.Count)
                {
                    Plane fallbackVal = pointPlanes.Count > 0 ? pointPlanes[0] : Plane.Unset;
                    if (pointPlanes.Count < pts.Count)
                    {
                        int diff = pts.Count - pointPlanes.Count;
                        var padded = new List<Plane>(pointPlanes);
                        padded.AddRange(Enumerable.Repeat(fallbackVal, diff));
                        pointPlanes = padded;
                    }
                    else
                    {
                        pointPlanes = pointPlanes.Take(pts.Count).ToList();
                    }
                }

                // Convert layer_height if available
                List<double> layerHs = null;
                if (hasLayerHeights && ghLayerHeights != null && ghLayerHeights.PathExists(path))
                {
                    var hBranch = ghLayerHeights.get_Branch(path);
                    if (hBranch != null)
                    {
                        layerHs = hBranch
                            .OfType<GH_Number>()
                            .Select(h => h.Value)
                            .ToList();
                    }
                }

                // Fallback / length consistency for layerHs
                if (layerHs == null)
                {
                    layerHs = Enumerable.Repeat(0.0, pts.Count).ToList();
                }
                else if (layerHs.Count != pts.Count)
                {
                    double fallbackVal = layerHs.Count > 0 ? layerHs[0] : 0.0;
                    if (layerHs.Count < pts.Count)
                    {
                        int diff = pts.Count - layerHs.Count;
                        var padded = new List<double>(layerHs);
                        padded.AddRange(Enumerable.Repeat(fallbackVal, diff));
                        layerHs = padded;
                    }
                    else
                    {
                        layerHs = layerHs.Take(pts.Count).ToList();
                    }
                }

                // Create a new CurveData
                CurveData cd = new CurveData
                {
                    OriginalIndex = curveIndex,
                    Points = pts,
                    PointPlanes = pointPlanes,
                    Flows = flx,
                    LayerHeights = layerHs,
                    Role = WasperGcodeTreeUtil.PathRoleAt(packedPath.PathRoles, path)
                };

                // Insert into dictionary
                if (!layers.ContainsKey(layerIndex))
                    layers[layerIndex] = new List<CurveData>();

                layers[layerIndex].Add(cd);
            }

            if (layers.Count == 0)
                return;

            int availableTargetCount = layers.Values
                .SelectMany(layer => layer)
                .Count(curve => WasperGcodeTreeUtil.MatchesTargetRoles(
                    curve.Role,
                    targetRoles));
            if (availableTargetCount == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"No {WasperGcodeTreeUtil.TargetRoleNames(targetRoles)} branches were found. " +
                    "The input wsp_path passes through unchanged.");
                DA.SetData(0, new WasperPrintPathGoo(packedPath));
                DA.SetData(
                    1,
                    $"OK | target_roles={WasperGcodeTreeUtil.TargetRoleNames(targetRoles)} | " +
                    "targeted_branches=0 | unchanged");
                WasperPathDebugOutputs.SetCore(DA, this, packedPath);
                return;
            }

            // Sort layer indices
            var layerKeys = layers.Keys.ToList();
            layerKeys.Sort();

            // If ref_point is invalid, set it to the first point of the first curve
            if (!refPoint.IsValid)
            {
                int firstLayer = layerKeys.FirstOrDefault();
                CurveData firstCurve = layers[firstLayer].FirstOrDefault();
                if (firstCurve != null && firstCurve.Points.Count > 0)
                {
                    refPoint = firstCurve.Points[0];
                }
            }

            // For each layer, do the nearest neighbor optimization
            Point3d currentPoint = refPoint;
            int targetedBranchCount = availableTargetCount;
            foreach (int layerIndex in layerKeys)
            {
                List<CurveData> curvesInLayer = layers[layerIndex];

                List<CurveData> sortedCurves;
                if (WasperGcodeTreeUtil.TargetsAllRoles(targetRoles))
                {
                    // Preserve the established all-path behavior exactly.
                    sortedCurves = SortCurvesNearestNeighbor(curvesInLayer, ref currentPoint);
                }
                else
                {
                    // Optimize only matching curves, then put the reordered curves
                    // back into the original matching-role slots. Non-target curves
                    // never move and retain their relative order.
                    var targeted = curvesInLayer
                        .Where(curve => WasperGcodeTreeUtil.MatchesTargetRoles(
                            curve.Role,
                            targetRoles))
                        .ToList();
                    List<CurveData> sortedTargets = targeted.Count > 0
                        ? SortCurvesNearestNeighbor(targeted, ref currentPoint)
                        : targeted;
                    int targetIndex = 0;
                    sortedCurves = curvesInLayer
                        .Select(curve =>
                            WasperGcodeTreeUtil.MatchesTargetRoles(
                                curve.Role,
                                targetRoles)
                                ? sortedTargets[targetIndex++]
                                : curve)
                        .ToList();

                    // The next layer starts from the actual last emitted path,
                    // including any non-target branch occupying the final slot.
                    CurveData last = sortedCurves.LastOrDefault();
                    if (last?.Points != null && last.Points.Count > 0)
                        currentPoint = last.Points[last.Points.Count - 1];
                }

                // Re-insert them into the output data tree
                int curveCounter = 0;
                foreach (CurveData cd in sortedCurves)
                {
                    GH_Path newPath = new GH_Path(layerIndex, curveCounter);

                    int n = cd.Points != null ? cd.Points.Count : 0;
                    for (int i = 0; i < n; i++)
                    {
                        optPointsTree.Add(cd.Points[i], newPath);

                        // point planes
                        if (cd.PointPlanes != null && cd.PointPlanes.Count == n)
                            optPointPlanesTree.Add(cd.PointPlanes[i], newPath);
                        else
                            optPointPlanesTree.Add(Plane.Unset, newPath);

                        // flows
                        if (cd.Flows != null && cd.Flows.Count == n)
                            optFlowsTree.Add(cd.Flows[i], newPath);
                        else
                            optFlowsTree.Add(0.0, newPath);

                        // layer heights
                        if (cd.LayerHeights != null && cd.LayerHeights.Count == n)
                            optLayerHeightsTree.Add(cd.LayerHeights[i], newPath);
                        else
                            optLayerHeightsTree.Add(0.0, newPath);

                        // indices (same for all points in curve)
                        indicesTree.Add(cd.OriginalIndex, newPath);
                    }

                    // Reorder point-aligned metadata with the same curve permutation. The
                    // source path is stable because OriginalIndex is the original curve index.
                    GH_Path sourcePath = new GH_Path(layerIndex, cd.OriginalIndex);
                    RemapBranch(packedPath?.LayerW, optLayerWTree, sourcePath, newPath, n);
                    RemapBranch(packedPath?.LayerWf, optLayerWfTree, sourcePath, newPath, n);
                    RemapBranch(packedPath?.PrintVol, optPrintVolTree, sourcePath, newPath, n);
                    RemapBranch(packedPath?.PrintSpeed, optPrintSpeed, sourcePath, newPath, n);
                    RemapBranch(packedPath?.PrintLoc, optPrintLoc, sourcePath, newPath, n);
                    RemapBranch(packedPath?.PrintGlob, optPrintGlob, sourcePath, newPath, n);
                    RemapBranch(packedPath?.SupportPts, optSupportPts, sourcePath, newPath, n);
                    RemapBranch(packedPath?.SupportVects, optSupportVects, sourcePath, newPath, n);
                    RemapBranch(packedPath?.Angles, optAngles, sourcePath, newPath, n);
                    RemapBranch(packedPath?.ContactWidths, optContactWidths, sourcePath, newPath, n);
                    RemapBranch(packedPath?.RiskMaterial, optRiskMaterial, sourcePath, newPath, n);
                    RemapBranch(packedPath?.RiskComb, optRiskComb, sourcePath, newPath, n);
                    RemapBranch(packedPath?.Load, optLoad, sourcePath, newPath, n);
                    RemapBranch(packedPath?.Capacity, optCapacity, sourcePath, newPath, n);
                    RemapBranch(packedPath?.DRatio, optDRatio, sourcePath, newPath, n);
                    RemapBranch(packedPath?.DLoaded, optDLoaded, sourcePath, newPath, n);
                    RemapBranch(packedPath?.BendRatio, optBendRatio, sourcePath, newPath, n);
                    RemapBranch(packedPath?.SpanClass, optSpanClass, sourcePath, newPath, n);
                    RemapBranch(packedPath?.SpanLen, optSpanLen, sourcePath, newPath, n);
                    RemapBranch(packedPath?.Collapsed, optCollapsed, sourcePath, newPath, n);
                    RemapBranch(packedPath?.Cascade, optCascade, sourcePath, newPath, n);
                    RemapBranch(packedPath?.CollapseGen, optCollapseGen, sourcePath, newPath, n);
                    RemapBranch(packedPath?.Torn, optTorn, sourcePath, newPath, n);
                    RemapBranch(packedPath?.InterfaceRatio, optInterfaceRatio, sourcePath, newPath, n);
                    RemapBranch(packedPath?.OverturnRatio, optOverturnRatio, sourcePath, newPath, n);
                    RemapBranch(packedPath?.FailureFlags, optFailureFlags, sourcePath, newPath, n);
                    RemapBranch(packedPath?.PathRoles, optPathRoles, sourcePath, newPath, 1);

                    curveCounter++;
                }
            }

            var outPath = new WasperPrintPath(
                optPointsTree, optPointPlanesTree, optFlowsTree, optLayerHeightsTree,
                printSpeed: optPrintSpeed,
                printLoc: optPrintLoc,
                printGlob: optPrintGlob,
                supportPts: optSupportPts,
                supportVects: optSupportVects,
                angles: optAngles,
                contactWidths: optContactWidths,
                riskMaterial: optRiskMaterial,
                riskComb: optRiskComb,
                load: optLoad,
                capacity: optCapacity,
                nozzleDiam: packedPath?.NozzleDiam,
                dRatio: optDRatio,
                dLoaded: optDLoaded,
                bendRatio: optBendRatio,
                spanClass: optSpanClass,
                spanLen: optSpanLen,
                collapsed: optCollapsed,
                cascade: optCascade,
                collapseGen: optCollapseGen,
                layerW: optLayerWTree,
                layerWf: optLayerWfTree,
                printVol: optPrintVolTree,
                torn: optTorn,
                interfaceRatio: optInterfaceRatio,
                overturnRatio: optOverturnRatio,
                failureFlags: optFailureFlags,
                pathRoles: optPathRoles,
                layerPlanes: packedPath?.LayerPlanes);

            DA.SetData(0, new WasperPrintPathGoo(outPath));
            DA.SetData(
                1,
                $"OK | input={optPointsTree.DataCount} plane locations | branches={optPointsTree.BranchCount} | " +
                $"target_roles={WasperGcodeTreeUtil.TargetRoleNames(targetRoles)} | targeted_branches={targetedBranchCount} | " +
                $"indices debug={(IsOutputVisible("source_indices") ? "shown" : "hidden")}");
            WasperPathDebugOutputs.SetCore(DA, this, outPath);
            int sourceIndicesIndex =
                WasperPathDebugOutputs.OutputIndex(this, "source_indices");
            if (sourceIndicesIndex >= 0)
                DA.SetDataTree(sourceIndicesIndex, indicesTree);
                

            // Ensure message shows current version
            this.Message = _versionTag;
        }

        #region Helper Classes and Methods

        private static void RemapBranch<T>(DataTree<T> source, DataTree<T> target,
            GH_Path sourcePath, GH_Path targetPath, int count)
        {
            if (source == null || target == null || !source.PathExists(sourcePath)) return;
            IList<T> values = source.Branch(sourcePath);
            if (values == null) return;
            int n = Math.Min(count, values.Count);
            for (int i = 0; i < n; i++) target.Add(values[i], targetPath);
        }

        class CurveData
        {
            public int OriginalIndex;
            public List<Point3d> Points;
            public List<Plane> PointPlanes;
            public List<double> Flows;
            public List<double> LayerHeights;
            public WasperPathRole Role;
            public bool Reversed = false;
        }

        private List<CurveData> SortCurvesNearestNeighbor(List<CurveData> curves, ref Point3d currentPoint)
        {
            List<CurveData> sorted = new List<CurveData>();
            List<CurveData> unvisited = new List<CurveData>(curves);

            Point3d current = currentPoint;

            while (unvisited.Count > 0)
            {
                double bestDistance = double.MaxValue;
                CurveData bestCurve = null;
                bool flipBest = false;

                object lockObj = new object();
                Point3d anchor = current;

                Parallel.ForEach(unvisited, cd =>
                {
                    if (cd.Points == null || cd.Points.Count == 0)
                        return;

                    Point3d startPt = cd.Points[0];
                    Point3d endPt = cd.Points[cd.Points.Count - 1];

                    double distStart = anchor.DistanceTo(startPt);
                    double distEnd = anchor.DistanceTo(endPt);

                    bool localFlip;
                    double localBestDist;
                    if (distStart <= distEnd)
                    {
                        localBestDist = distStart;
                        localFlip = false;
                    }
                    else
                    {
                        localBestDist = distEnd;
                        localFlip = true;
                    }

                    lock (lockObj)
                    {
                        if (localBestDist < bestDistance)
                        {
                            bestDistance = localBestDist;
                            bestCurve = cd;
                            flipBest = localFlip;
                        }
                    }
                });

                if (bestCurve == null)
                    break;

                unvisited.Remove(bestCurve);

                // Flip curve if needed
                if (flipBest)
                {
                    bestCurve.Points.Reverse();

                    // Reverse point planes with points.
                    if (bestCurve.PointPlanes != null && bestCurve.PointPlanes.Count > 1)
                        bestCurve.PointPlanes.Reverse();

                    bestCurve.Flows = ReverseFluxListKeepZero(bestCurve.Flows);

                    if (bestCurve.LayerHeights != null && bestCurve.LayerHeights.Count > 1)
                        bestCurve.LayerHeights.Reverse();

                    bestCurve.Reversed = !bestCurve.Reversed;
                }

                sorted.Add(bestCurve);
                current = bestCurve.Points.Last();
            }

            currentPoint = current;
            return sorted;
        }

        private List<double> ReverseFluxListKeepZero(List<double> originalFlows)
        {
            if (originalFlows == null || originalFlows.Count <= 1)
                return originalFlows != null ? new List<double>(originalFlows) : new List<double>();

            List<double> reversed = new List<double>();
            reversed.Add(originalFlows[0]); // keep first (usually 0.0)
            reversed.AddRange(originalFlows.Skip(1).Reverse());
            return reversed;
        }

        #endregion
    }
}
