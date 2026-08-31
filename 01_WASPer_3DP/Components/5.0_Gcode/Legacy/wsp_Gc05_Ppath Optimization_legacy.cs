using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Rhino;
using Rhino.Geometry;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using WASPer_3DP;

namespace WASPer_3DP_Components._5_0_Gcode
{
    public class WSP_Gc05_PrintingPathOptimizer_v3_Legacy : GH_Component
    {
        // --------------------------------------------------------------------
        // Version tag (taken from WASPer_3DP assembly)
        // --------------------------------------------------------------------
        private readonly string _versionTag;

        /// <summary>
        /// Constructor
        /// </summary>
        public WSP_Gc05_PrintingPathOptimizer_v3_Legacy()
          : base(
                "wsp_Gc05_Printing Path Optimizer Legacy",    // Name
                "Path Optimizer",                      // Nickname
                "    Component Name:\r\n    Optimized c-Code Points, Vectors & Flows\r\n\r\n" +
                "    Description:\r\n" +
                "    This component takes in a data tree of printing points (and optional per-point planes),\r\n" +
                "    with their corresponding flow multiplier values, and optimizes the order of curves for 3D printing.\r\n" +
                "    It minimizes travel distances between curves in the same layer using a nearest neighbor algorithm\r\n" +
                "    and reorders the associated data consistently.\r\n\r\n" +
                "    Optionally, it can also take a tree of layer_height values matching the structure of printing_points,\r\n" +
                "    and will reorder these layer_height values consistently with the optimized paths.\r\n\r\n" +
                "    The output preserves the original data structure and logic, ensuring the flow multiplier value of 0.0\r\n" +
                "    remains at the first point of each curve.\r\n\r\n" +
                "    Inputs:\r\n" +
                "    0) printing_points (DataTree<Point3d>):\r\n" +
                "       The data tree of points representing the subdivided curves, structured as {layer; curve}.\r\n" +
                "    1) point_planes (DataTree<Plane>, optional):\r\n" +
                "       Per-point planes matching the structure of `printing_points`.\r\n" +
                "    2) flows (DataTree<double>):\r\n" +
                "       The corresponding data tree of flow multiplier values for the points, matching the structure of `printing_points`.\r\n" +
                "    3) layer_height (DataTree<double>, optional):\r\n" +
                "       Per-point layer height values matching the structure of `printing_points`.\r\n" +
                "    4) ref_point (Point3d):\r\n" +
                "       An optional reference point from which the optimization starts.\r\n" +
                "       If not provided, the optimization begins from the first point of the first curve in the first layer.\r\n\r\n" +
                "    Outputs:\r\n" +
                "    0) opt_printing_points (DataTree<Point3d>):\r\n" +
                "       The optimized data tree of points, reordered to minimize travel distances.\r\n" +
                "    1) opt_point_planes (DataTree<Plane>):\r\n" +
                "       The point planes reordered to match the optimized points.\r\n" +
                "    2) opt_flows (DataTree<double>):\r\n" +
                "       The flow multiplier values reordered to match the optimized points. First point in each curve has flux=0.0.\r\n" +
                "    3) opt_layer_heights (DataTree<double>):\r\n" +
                "       The layer_height values reordered to match the optimized points (if input is provided).\r\n" +
                "    4) indices (DataTree<int>):\r\n" +
                "       The original curve indices for each branch, useful for tracking or mapping.\r\n\r\n" +
                "    Version: 1.0.6 (assembly-driven)",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,                         // Category
                "5.0_Gcode"                             // Subcategory
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
            get { return new Guid("d9b6b4c4-1f2d-4cf3-a8af-9c2e7fe8f6b1"); }
        }

        public override GH_Exposure Exposure => GH_Exposure.hidden;

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

        /// <summary>
        /// Register input parameters
        /// </summary>
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            // 0) printing_points
            pManager.AddPointParameter(
                "printing_points",
                "p_points",
                "Data tree of subdivided printing points (structured as {layer; curve}).",
                GH_ParamAccess.tree
            );
            pManager[0].Optional = true;

            // 1) point_planes (optional)
            pManager.AddPlaneParameter(
                "point_planes",
                "pt_planes",
                "Optional data tree of per-point planes matching printing_points structure ({layer; curve}).",
                GH_ParamAccess.tree
            );
            pManager[1].Optional = true;

            // 2) flows
            pManager.AddNumberParameter(
                "flows",
                "flows",
                "Data tree of flow multiplier values matching printing_points structure.",
                GH_ParamAccess.tree
            );
            pManager[2].Optional = true;

            // 3) layer_height (optional)
            pManager.AddNumberParameter(
                "layer_height",
                "layer_h",
                "Optional data tree of layer height values per point, matching printing_points structure ({layer; curve}).",
                GH_ParamAccess.tree
            );
            pManager[3].Optional = true;

            // 4) ref_point
            pManager.AddPointParameter(
                "ref_point",
                "ref_pt",
                "Optional reference point for starting the optimization. If unset, uses the first point of the first curve.",
                GH_ParamAccess.item,
                new Point3d(double.NaN, double.NaN, double.NaN)
            );

            pManager.AddGenericParameter(
                "wasper_path",
                "wsp_path",
                "Optional WASPer Print Path object. Explicitly connected legacy trees override the corresponding packed fields.",
                GH_ParamAccess.item);
            pManager[5].Optional = true;
        }

        /// <summary>
        /// Register output parameters
        /// </summary>
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            // 0) opt_printing_points
            pManager.AddPointParameter(
                "opt_printing_points",
                "opt_p_points",
                "Optimized data tree of points, minimizing travel distances.",
                GH_ParamAccess.tree
            );

            // 1) opt_point_planes
            pManager.AddPlaneParameter(
                "opt_point_planes",
                "opt_pt_planes",
                "Point planes reordered to match the optimized points.",
                GH_ParamAccess.tree
            );

            // 2) opt_flows
            pManager.AddNumberParameter(
                "optimized_flows",
                "opt_flows",
                "Flow multiplier values reordered to match the optimized points. First point in each curve has flux=0.0.",
                GH_ParamAccess.tree
            );

            // 3) opt_layer_heights
            pManager.AddNumberParameter(
                "opt_layer_heights",
                "opt_layer_h",
                "Layer height values reordered to match the optimized points.",
                GH_ParamAccess.tree
            );

            // 4) indices
            pManager.AddIntegerParameter(
                "indices",
                "indices",
                "Original curve indices for each branch, useful for tracking or mapping.",
                GH_ParamAccess.tree
            );

            pManager.AddGenericParameter(
                "wasper_path",
                "wsp_path",
                "Optimized WASPer Print Path object carrying the optimized points, planes, flows and layer heights.",
                GH_ParamAccess.item);
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
            WasperPrintPath packedPath = null;

            // 0: printing_points (tree)
            bool hasPackedPath = WasperGcodeTreeUtil.TryGetPrintPath(DA, 5, out packedPath);
            if (!DA.GetDataTree(0, out ghPoints) || ghPoints == null || ghPoints.PathCount == 0)
            {
                if (!hasPackedPath) return;
                ghPoints = WasperGcodeTreeUtil.ToPointStructure(packedPath.Points);
            }

            // 1: point_planes (tree, optional)
            bool hasPointPlanes = DA.GetDataTree(1, out ghPointPlanes);
            if ((!hasPointPlanes || ghPointPlanes == null || ghPointPlanes.PathCount == 0) && hasPackedPath && packedPath.HasPlanes)
            {
                ghPointPlanes = WasperGcodeTreeUtil.ToPlaneStructure(packedPath.PtPlanes);
                hasPointPlanes = ghPointPlanes.PathCount > 0;
            }

            // 2: flows (tree)
            bool hasFlows = DA.GetDataTree(2, out ghFlows) && ghFlows != null && ghFlows.PathCount > 0;
            if (!hasFlows && hasPackedPath && packedPath.HasFlows)
            {
                ghFlows = WasperGcodeTreeUtil.ToNumberStructure(packedPath.Flows);
                hasFlows = ghFlows.PathCount > 0;
            }
            if (!hasFlows) return;

            // 3: layer_height (tree, optional)
            bool hasLayerHeights = DA.GetDataTree(3, out ghLayerHeights);
            if ((!hasLayerHeights || ghLayerHeights == null || ghLayerHeights.PathCount == 0) && hasPackedPath && packedPath.HasLayerH)
            {
                ghLayerHeights = WasperGcodeTreeUtil.ToNumberStructure(packedPath.LayerH);
                hasLayerHeights = ghLayerHeights.PathCount > 0;
            }

            // 4: ref_point (item, with default)
            DA.GetData(4, ref refPoint);

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
                    LayerHeights = layerHs
                };

                // Insert into dictionary
                if (!layers.ContainsKey(layerIndex))
                    layers[layerIndex] = new List<CurveData>();

                layers[layerIndex].Add(cd);
            }

            if (layers.Count == 0)
                return;

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
            foreach (int layerIndex in layerKeys)
            {
                List<CurveData> curvesInLayer = layers[layerIndex];

                // Nearest-neighbor sort with parallel candidate evaluation
                List<CurveData> sortedCurves = SortCurvesNearestNeighbor(curvesInLayer, ref currentPoint);

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

                    curveCounter++;
                }
            }

            // Set outputs (match RegisterOutputParams order)
            DA.SetDataTree(0, optPointsTree);
            DA.SetDataTree(1, optPointPlanesTree);
            DA.SetDataTree(2, optFlowsTree);
            DA.SetDataTree(3, optLayerHeightsTree);
            DA.SetDataTree(4, indicesTree);
            DA.SetData(5, new WasperPrintPathGoo(
                new WasperPrintPath(
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
                    failureFlags: optFailureFlags)));
                

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
