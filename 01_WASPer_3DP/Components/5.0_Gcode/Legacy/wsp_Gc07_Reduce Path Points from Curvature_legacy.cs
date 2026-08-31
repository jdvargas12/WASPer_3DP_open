// -----------------------------------------------------------------------------
//  wsp_Gc07_Reduce Path Points from Curvature
// -----------------------------------------------------------------------------
//  Reduces Gc01 point paths while preserving high-curvature regions.
//
//  The component keeps paired per-point data aligned:
//      p_points, point_planes, flows, layer_height
//
//  Strategy:
//      1) Protect endpoints and points whose turn angle is above curv_angle.
//      2) Apply Ramer-Douglas-Peucker simplification to the low-curvature spans
//         between protected points using deviation_tol in model units.
//      3) Optionally restore source points so no simplified span exceeds max_spacing.
//      4) Carry all optional per-point values by kept source index.
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

namespace WASPer_3DP_Components._5_0_Gcode
{
    public class wsp_Gc07_ReducePathPointsFromCurvature_Legacy : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Gc07_ReducePathPointsFromCurvature_Legacy()
            : base(
                "wsp_Gc07_Reduce Path Points from Curvature Legacy",
                "Reduce Path Pts",
                "Reduces the number of printing path points while preserving high-curvature regions.\n" +
                "Designed to sit after Gc01: p_points, point_planes, flows, and layer_height are reduced together by kept index.\n" +
                "An optional max_spacing safeguard can retain intermediate source points on long straight segments for a less aggressive reduction.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "5.0_Gcode")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("8B74341A-0B55-4F30-8BE1-E8D1FF4B0C8F");

        public override GH_Exposure Exposure => GH_Exposure.hidden;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Gc07_Reduce Path Points from Curvature.png"))
                    {
                        return s != null ? new System.Drawing.Bitmap(s) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddPointParameter(
                "printing_points",
                "p_points",
                "Printing points from Gc01, usually structured as {layer; curve}.",
                GH_ParamAccess.tree);
            p[0].Optional = true;

            p.AddPlaneParameter(
                "point_planes",
                "pt_planes",
                "Optional point planes from Gc01. Reduced by the same kept indices as p_points.",
                GH_ParamAccess.tree);
            p[1].Optional = true;

            p.AddNumberParameter(
                "flows",
                "flows",
                "Optional flow multiplier values from Gc01. Reduced by the same kept indices as p_points.",
                GH_ParamAccess.tree);
            p[2].Optional = true;

            p.AddNumberParameter(
                "layer_height",
                "layer_h",
                "Optional layer height values from Gc01. Reduced by the same kept indices as p_points.",
                GH_ParamAccess.tree);
            p[3].Optional = true;

            p.AddNumberParameter(
                "deviation_tol",
                "dev_tol",
                "Maximum allowed point-to-chord deviation in model units for low-curvature spans.\n" +
                "Larger values remove more points. Default: 0.5.",
                GH_ParamAccess.item,
                0.5);

            p.AddNumberParameter(
                "curv_angle",
                "curv_ang",
                "Turn angle in degrees above which a point is protected as high-curvature.\n" +
                "Smaller values preserve more points around bends. Default: 5 degrees.",
                GH_ParamAccess.item,
                5.0);

            p.AddNumberParameter(
                "max_spacing",
                "max_sp",
                "Optional maximum accumulated path distance between consecutive retained source points, in model units.\n" +
                "Use a positive value to restore evenly distributed original points on long straight spans. Smaller values give a gentler reduction.\n" +
                "Set to 0 to disable this safeguard and use the original curvature/RDP-only behavior. Default: 0.",
                GH_ParamAccess.item,
                0.0);

            p.AddGenericParameter(
                "wasper_path",
                "wsp_path",
                "Optional WASPer Print Path object. Explicit legacy trees override the corresponding packed fields.",
                GH_ParamAccess.item);
            p[7].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddPointParameter(
                "printing_points",
                "p_points",
                "Reduced printing points. Tree structure matches the input branches.",
                GH_ParamAccess.tree);

            p.AddPlaneParameter(
                "point_planes",
                "pt_planes",
                "Reduced point planes, aligned with output p_points when point_planes input is provided.",
                GH_ParamAccess.tree);

            p.AddNumberParameter(
                "flows",
                "flows",
                "Reduced flow multiplier values, aligned with output p_points when flows input is provided.",
                GH_ParamAccess.tree);

            p.AddNumberParameter(
                "layer_height",
                "layer_h",
                "Reduced layer height values, aligned with output p_points when layer_height input is provided.",
                GH_ParamAccess.tree);

            p.AddIntegerParameter(
                "kept_indices",
                "indices",
                "Original point indices kept in each branch.",
                GH_ParamAccess.tree);

            p.AddTextParameter(
                "info",
                "info",
                "Reduction summary.",
                GH_ParamAccess.item);

            p.AddGenericParameter(
                "wasper_path",
                "wsp_path",
                "Reduced WASPer Print Path object carrying the retained points, planes, flows and layer heights.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            global::WASPer_3DP.WasperPrintPath packedPath = null;
            bool hasPackedPath = global::WASPer_3DP.WasperGcodeTreeUtil.TryGetPrintPath(DA, 7, out packedPath);

            GH_Structure<GH_Point> pointsTree;
            if (!DA.GetDataTree(0, out pointsTree) || pointsTree == null || pointsTree.PathCount == 0)
            {
                if (!hasPackedPath || packedPath.Points == null || packedPath.Points.BranchCount == 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No printing_points or wsp_path were provided.");
                    return;
                }
                pointsTree = global::WASPer_3DP.WasperGcodeTreeUtil.ToPointStructure(packedPath.Points);
            }

            GH_Structure<GH_Plane> planeTree = null;
            GH_Structure<GH_Number> fluxTree = null;
            GH_Structure<GH_Number> heightTree = null;

            bool hasPlanes = DA.GetDataTree(1, out planeTree) && planeTree != null && planeTree.PathCount > 0;
            bool hasFlows = DA.GetDataTree(2, out fluxTree) && fluxTree != null && fluxTree.PathCount > 0;
            bool hasHeights = DA.GetDataTree(3, out heightTree) && heightTree != null && heightTree.PathCount > 0;

            if (!hasPlanes && hasPackedPath && packedPath.HasPlanes)
            {
                planeTree = global::WASPer_3DP.WasperGcodeTreeUtil.ToPlaneStructure(packedPath.PtPlanes);
                hasPlanes = planeTree != null && planeTree.PathCount > 0;
            }
            if (!hasFlows && hasPackedPath && packedPath.HasFlows)
            {
                fluxTree = global::WASPer_3DP.WasperGcodeTreeUtil.ToNumberStructure(packedPath.Flows);
                hasFlows = fluxTree != null && fluxTree.PathCount > 0;
            }
            if (!hasHeights && hasPackedPath && packedPath.HasLayerH)
            {
                heightTree = global::WASPer_3DP.WasperGcodeTreeUtil.ToNumberStructure(packedPath.LayerH);
                hasHeights = heightTree != null && heightTree.PathCount > 0;
            }

            double deviationTol = 0.5;
            double curvAngleDeg = 5.0;
            double maxSpacing = 0.0;
            DA.GetData(4, ref deviationTol);
            DA.GetData(5, ref curvAngleDeg);
            DA.GetData(6, ref maxSpacing);

            double docTol = RhinoDoc.ActiveDoc != null ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 1e-6;
            double tol = Math.Max(docTol, 1e-9);
            double tinyLen = Math.Max(tol * 10.0, 1e-9);

            if (deviationTol <= 0.0)
            {
                deviationTol = Math.Max(tol * 10.0, 1e-6);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "deviation_tol was <= 0. Using a small document-tolerance based value.");
            }

            if (curvAngleDeg <= 0.0 || curvAngleDeg > 180.0)
            {
                curvAngleDeg = 5.0;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "curv_angle should be in (0, 180]. Reset to 5 degrees.");
            }

            if (maxSpacing < 0.0)
            {
                maxSpacing = 0.0;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "max_spacing cannot be negative. Reset to 0 (disabled).");
            }
            else if (maxSpacing > 0.0 && maxSpacing <= tinyLen)
            {
                maxSpacing = tinyLen;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "max_spacing was below geometric tolerance and has been raised to the tolerance-based minimum.");
            }

            BranchData[] branches = ExtractBranches(pointsTree, planeTree, fluxTree, heightTree, hasPlanes, hasFlows, hasHeights);
            int branchCount = branches.Length;

            var reduced = new ReducedBranch[branchCount];
            double angleTolRad = RhinoMath.ToRadians(curvAngleDeg);

            bool doParallel = branchCount >= 4 || branches.Sum(b => b.Points.Count) >= 500;
            Action<int> work = i =>
            {
                BranchData b = branches[i];
                List<int> keep = ComputeKeptIndices(b.Points, deviationTol, angleTolRad, tinyLen);
                int spacingRestored = maxSpacing > 0.0
                    ? AddMaxSpacingIndices(b.Points, keep, maxSpacing, tinyLen)
                    : 0;
                reduced[i] = new ReducedBranch(b, keep, spacingRestored);
            };

            if (doParallel)
                Parallel.For(0, branchCount, work);
            else
                for (int i = 0; i < branchCount; i++) work(i);

            var outPts = new DataTree<Point3d>();
            var outPlanes = new DataTree<Plane>();
            var outFlux = new DataTree<double>();
            var outHeights = new DataTree<double>();
            var outIndices = new DataTree<int>();

            int inputCount = 0;
            int outputCount = 0;
            int spacingRestoredCount = 0;

            for (int i = 0; i < branchCount; i++)
            {
                ReducedBranch rb = reduced[i];
                GH_Path path = rb.Source.Path;

                outPts.EnsurePath(path);
                outPlanes.EnsurePath(path);
                outFlux.EnsurePath(path);
                outHeights.EnsurePath(path);
                outIndices.EnsurePath(path);

                inputCount += rb.Source.Points.Count;
                outputCount += rb.Kept.Count;
                spacingRestoredCount += rb.SpacingRestored;

                foreach (int idx in rb.Kept)
                {
                    outPts.Add(rb.Source.Points[idx], path);
                    outIndices.Add(idx, path);

                    if (rb.Source.Planes != null)
                        outPlanes.Add(rb.Source.Planes[idx], path);

                    if (rb.Source.Flows != null)
                        outFlux.Add(rb.Source.Flows[idx], path);

                    if (rb.Source.LayerHeights != null)
                        outHeights.Add(rb.Source.LayerHeights[idx], path);
                }
            }

            DataTree<double> outPrintSpeed = ReduceOptionalTree(packedPath?.PrintSpeed, reduced);
            DataTree<double> outPrintLoc = ReduceOptionalTree(packedPath?.PrintLoc, reduced);
            DataTree<bool> outPrintGlob = ReduceOptionalTree(packedPath?.PrintGlob, reduced);
            DataTree<Point3d> outSupportPts = ReduceOptionalTree(packedPath?.SupportPts, reduced);
            DataTree<Vector3d> outSupportVects = ReduceOptionalTree(packedPath?.SupportVects, reduced);
            DataTree<double> outAngles = ReduceOptionalTree(packedPath?.Angles, reduced);
            DataTree<double> outContactWidths = ReduceOptionalTree(packedPath?.ContactWidths, reduced);
            DataTree<double> outRiskMaterial = ReduceOptionalTree(packedPath?.RiskMaterial, reduced);
            DataTree<double> outRiskComb = ReduceOptionalTree(packedPath?.RiskComb, reduced);
            DataTree<double> outLoad = ReduceOptionalTree(packedPath?.Load, reduced);
            DataTree<double> outCapacity = ReduceOptionalTree(packedPath?.Capacity, reduced);
            DataTree<double> outDRatio = ReduceOptionalTree(packedPath?.DRatio, reduced);
            DataTree<double> outDLoaded = ReduceOptionalTree(packedPath?.DLoaded, reduced);
            DataTree<double> outBendRatio = ReduceOptionalTree(packedPath?.BendRatio, reduced);
            DataTree<int> outSpanClass = ReduceOptionalTree(packedPath?.SpanClass, reduced);
            DataTree<double> outSpanLen = ReduceOptionalTree(packedPath?.SpanLen, reduced);
            DataTree<bool> outCollapsed = ReduceOptionalTree(packedPath?.Collapsed, reduced);
            DataTree<bool> outCascade = ReduceOptionalTree(packedPath?.Cascade, reduced);
            DataTree<int> outCollapseGen = ReduceOptionalTree(packedPath?.CollapseGen, reduced);
            DataTree<double> outLayerW = ReduceOptionalTree(packedPath?.LayerW, reduced);
            DataTree<double> outLayerWf = ReduceOptionalTree(packedPath?.LayerWf, reduced);
            DataTree<double> outPrintVol = ReduceOptionalTree(packedPath?.PrintVol, reduced);
            DataTree<bool> outTorn = ReduceOptionalTree(packedPath?.Torn, reduced);
            DataTree<double> outInterfaceRatio = ReduceOptionalTree(packedPath?.InterfaceRatio, reduced);
            DataTree<double> outOverturnRatio = ReduceOptionalTree(packedPath?.OverturnRatio, reduced);
            DataTree<int> outFailureFlags = ReduceOptionalTree(packedPath?.FailureFlags, reduced);

            DA.SetDataTree(0, outPts);
            DA.SetDataTree(1, outPlanes);
            DA.SetDataTree(2, outFlux);
            DA.SetDataTree(3, outHeights);
            DA.SetDataTree(4, outIndices);

            double pct = inputCount > 0 ? 100.0 * (1.0 - (double)outputCount / inputCount) : 0.0;
            string maxSpacingInfo = maxSpacing > 0.0 ? maxSpacing.ToString("0.###") : "off";
            string info = $"OK | input={inputCount} | output={outputCount} | removed={pct:0.0}% | spacing_restored={spacingRestoredCount} | dev_tol={deviationTol:0.###} | curv_angle={curvAngleDeg:0.###} | max_spacing={maxSpacingInfo}";
            DA.SetData(5, info);
            DA.SetData(6, new global::WASPer_3DP.WasperPrintPathGoo(
                new global::WASPer_3DP.WasperPrintPath(
                    outPts, outPlanes, outFlux, outHeights,
                    printSpeed: outPrintSpeed,
                    printLoc: outPrintLoc,
                    printGlob: outPrintGlob,
                    supportPts: outSupportPts,
                    supportVects: outSupportVects,
                    angles: outAngles,
                    contactWidths: outContactWidths,
                    riskMaterial: outRiskMaterial,
                    riskComb: outRiskComb,
                    load: outLoad,
                    capacity: outCapacity,
                    nozzleDiam: packedPath?.NozzleDiam,
                    dRatio: outDRatio,
                    dLoaded: outDLoaded,
                    bendRatio: outBendRatio,
                    spanClass: outSpanClass,
                    spanLen: outSpanLen,
                    collapsed: outCollapsed,
                    cascade: outCascade,
                    collapseGen: outCollapseGen,
                    layerW: outLayerW,
                    layerWf: outLayerWf,
                    printVol: outPrintVol,
                    torn: outTorn,
                    interfaceRatio: outInterfaceRatio,
                    overturnRatio: outOverturnRatio,
                    failureFlags: outFailureFlags)));

            Message = doParallel
                ? $"{_versionTag} | {outputCount}/{inputCount} | P"
                : $"{_versionTag} | {outputCount}/{inputCount}";
        }

        private sealed class BranchData
        {
            public GH_Path Path;
            public List<Point3d> Points;
            public List<Plane> Planes;
            public List<double> Flows;
            public List<double> LayerHeights;
        }

        private sealed class ReducedBranch
        {
            public readonly BranchData Source;
            public readonly List<int> Kept;
            public readonly int SpacingRestored;

            public ReducedBranch(BranchData source, List<int> kept, int spacingRestored)
            {
                Source = source;
                Kept = kept ?? new List<int>();
                SpacingRestored = Math.Max(0, spacingRestored);
            }
        }

        private static DataTree<T> ReduceOptionalTree<T>(
            DataTree<T> source,
            IReadOnlyList<ReducedBranch> reduced)
        {
            if (source == null || reduced == null) return null;

            var result = new DataTree<T>();
            foreach (ReducedBranch rb in reduced)
            {
                if (rb?.Source?.Path == null || !source.PathExists(rb.Source.Path)) continue;
                IList<T> values = source.Branch(rb.Source.Path);
                if (values == null) continue;

                result.EnsurePath(rb.Source.Path);
                foreach (int index in rb.Kept)
                    if (index >= 0 && index < values.Count)
                        result.Add(values[index], rb.Source.Path);
            }
            return result.BranchCount > 0 ? result : null;
        }

        private static BranchData[] ExtractBranches(
            GH_Structure<GH_Point> pointsTree,
            GH_Structure<GH_Plane> planeTree,
            GH_Structure<GH_Number> fluxTree,
            GH_Structure<GH_Number> heightTree,
            bool hasPlanes,
            bool hasFlows,
            bool hasHeights)
        {
            var branches = new BranchData[pointsTree.PathCount];

            for (int i = 0; i < pointsTree.PathCount; i++)
            {
                GH_Path path = pointsTree.Paths[i];
                var pts = ExtractPoints(pointsTree.get_Branch(path));
                int n = pts.Count;

                branches[i] = new BranchData
                {
                    Path = path,
                    Points = pts,
                    Planes = hasPlanes ? AlignList(ExtractPlanes(GetBranchOrNull(planeTree, path)), n, Plane.Unset) : null,
                    Flows = hasFlows ? AlignList(ExtractNumbers(GetBranchOrNull(fluxTree, path)), n, 0.0) : null,
                    LayerHeights = hasHeights ? AlignList(ExtractNumbers(GetBranchOrNull(heightTree, path)), n, 0.0) : null
                };
            }

            return branches;
        }

        private static System.Collections.IList GetBranchOrNull<T>(GH_Structure<T> tree, GH_Path path) where T : IGH_Goo
        {
            if (tree == null || !tree.PathExists(path)) return null;
            return tree.get_Branch(path);
        }

        private static List<Point3d> ExtractPoints(System.Collections.IList branch)
        {
            var result = new List<Point3d>();
            if (branch == null) return result;

            foreach (object obj in branch)
            {
                if (obj is GH_Point gp && gp.Value.IsValid)
                    result.Add(gp.Value);
            }

            return result;
        }

        private static List<Plane> ExtractPlanes(System.Collections.IList branch)
        {
            var result = new List<Plane>();
            if (branch == null) return result;

            foreach (object obj in branch)
            {
                if (obj is GH_Plane gp)
                    result.Add(gp.Value);
            }

            return result;
        }

        private static List<double> ExtractNumbers(System.Collections.IList branch)
        {
            var result = new List<double>();
            if (branch == null) return result;

            foreach (object obj in branch)
            {
                if (obj is GH_Number gn)
                    result.Add(gn.Value);
            }

            return result;
        }

        private static List<T> AlignList<T>(List<T> source, int count, T fallback)
        {
            var result = new List<T>(count);
            if (count <= 0) return result;

            if (source == null || source.Count == 0)
            {
                for (int i = 0; i < count; i++) result.Add(fallback);
                return result;
            }

            for (int i = 0; i < count; i++)
            {
                if (i < source.Count)
                    result.Add(source[i]);
                else
                    result.Add(source[source.Count - 1]);
            }

            return result;
        }

        private static List<int> ComputeKeptIndices(List<Point3d> pts, double deviationTol, double angleTolRad, double tinyLen)
        {
            var keep = new SortedSet<int>();
            int n = pts != null ? pts.Count : 0;

            if (n == 0) return new List<int>();
            if (n <= 2)
            {
                for (int i = 0; i < n; i++) keep.Add(i);
                return keep.ToList();
            }

            keep.Add(0);
            keep.Add(n - 1);

            for (int i = 1; i < n - 1; i++)
            {
                Vector3d a = pts[i] - pts[i - 1];
                Vector3d b = pts[i + 1] - pts[i];
                if (a.Length <= tinyLen || b.Length <= tinyLen) continue;

                double angle = Vector3d.VectorAngle(a, b);
                if (!double.IsNaN(angle) && angle >= angleTolRad)
                    keep.Add(i);
            }

            var protectedIds = keep.ToList();
            for (int i = 0; i < protectedIds.Count - 1; i++)
            {
                int a = protectedIds[i];
                int b = protectedIds[i + 1];
                AddRdpIndices(pts, a, b, deviationTol, keep);
            }

            return keep.ToList();
        }

        private static void AddRdpIndices(List<Point3d> pts, int start, int end, double tol, SortedSet<int> keep)
        {
            if (end <= start + 1) return;

            Point3d a = pts[start];
            Point3d b = pts[end];

            double bestDist = -1.0;
            int bestIndex = -1;

            for (int i = start + 1; i < end; i++)
            {
                double d = DistancePointToSegment(pts[i], a, b);
                if (d > bestDist)
                {
                    bestDist = d;
                    bestIndex = i;
                }
            }

            if (bestIndex >= 0 && bestDist > tol)
            {
                keep.Add(bestIndex);
                AddRdpIndices(pts, start, bestIndex, tol, keep);
                AddRdpIndices(pts, bestIndex, end, tol, keep);
            }
        }

        private static int AddMaxSpacingIndices(
            List<Point3d> pts,
            List<int> keptIndices,
            double maxSpacing,
            double tinyLen)
        {
            if (pts == null || pts.Count < 3 || keptIndices == null || keptIndices.Count < 2 || maxSpacing <= 0.0)
                return 0;

            var keep = new SortedSet<int>(keptIndices);
            List<int> anchors = keep.ToList();
            int restored = 0;

            for (int span = 0; span < anchors.Count - 1; span++)
            {
                int start = anchors[span];
                int end = anchors[span + 1];
                if (end <= start + 1) continue;

                int lastKept = start;
                double accumulated = 0.0;

                for (int sourceIndex = start + 1; sourceIndex <= end; sourceIndex++)
                {
                    double step = pts[sourceIndex].DistanceTo(pts[sourceIndex - 1]);

                    if (accumulated + step > maxSpacing + tinyLen)
                    {
                        int restoreIndex = sourceIndex - 1;
                        if (restoreIndex > lastKept && restoreIndex < end)
                        {
                            if (keep.Add(restoreIndex)) restored++;
                            lastKept = restoreIndex;
                            accumulated = step;
                            continue;
                        }
                    }

                    accumulated += step;
                }
            }

            keptIndices.Clear();
            keptIndices.AddRange(keep);
            return restored;
        }

        private static double DistancePointToSegment(Point3d p, Point3d a, Point3d b)
        {
            Vector3d ab = b - a;
            double len2 = ab.SquareLength;
            if (len2 <= 1e-24) return p.DistanceTo(a);

            double t = ((p - a) * ab) / len2;
            if (t < 0.0) t = 0.0;
            else if (t > 1.0) t = 1.0;

            Point3d q = a + t * ab;
            return p.DistanceTo(q);
        }
    }
}
