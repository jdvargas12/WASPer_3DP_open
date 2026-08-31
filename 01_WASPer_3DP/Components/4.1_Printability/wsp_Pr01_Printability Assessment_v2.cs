using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Collections;

namespace WASPer_3DP.Components._4_1_Printability
{
    public sealed class wsp_Pr01_Printability_Assessment_v2 : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Pr01_Printability_Assessment_v2()
            : base(
                "wsp_Pr01_Printability Assessment",
                "Printability",
                "Assesses printability between consecutive layers from a packed WASPer Print Path. The component reads p_points, pt_planes, flows, and layer_h directly from wsp_path, finds the closest physically valid support on the previous layer, evaluates collapse against world gravity, estimates contact width Wc, and writes the full-resolution printability fields back into wsp_path. Optional layer_w overrides the packed width for this assessment; optional noz_diam switches Wc from the geometric full-width baseline to the Alhussain et al. (2024) filament-shape regression. 1 is printable and 0 is not printable.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "4.1_Printability")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("243A9E57-91BB-4CF0-A62F-40C441B4F2E6");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var stream = asm.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Pr01_Printability Assessment.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch { }

                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("wasper_path", "wsp_path", "Required WASPer Print Path object. Pr01 v2 reads p_points, pt_planes, flows, and layer_h from this packed path and writes the resolved printability assessment back into the output wsp_path.", GH_ParamAccess.item);
            p.AddNumberParameter("layer_w", "layer_w", "Optional nominal/base bead width before flow adjustment, in model units. If connected, it overrides wsp_path.LayerW; otherwise the incoming path value is preserved when available. If neither is available, defaults to layer_h * 2.5. Pr01 stores this nominal width as LayerW, estimates LayerWf by scaling the bead cross-sectional area with local flow and recovering the equivalent deposited width from layer_h, and updates per-segment PrintVol.", GH_ParamAccess.tree);
            p[1].Optional = true;
            p.AddNumberParameter("noz_diam", "noz_diam", "Optional nozzle diameter D in model units (millimetres). If explicitly connected, it overrides wsp_path.NozzleDiam; if disconnected, the component uses the packed value. A resolved value greater than zero enables the Alhussain et al. (2024) Wc model; zero uses the geometric baseline.", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("angle_crit", "angle_crit", "Critical gravity-based overhang angle in degrees from world vertical. Supply one value for a hard critical threshold, or two values {risk_start, critical} for a gradual local-printability interval. Default behavior is {30,45}; values above 45 degrees are not accepted.", GH_ParamAccess.list);
            p[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("wasper_path", "wsp_path", "WASPer Print Path object carrying the evaluated points, planes, flows and layer heights.", GH_ParamAccess.item);
            p.AddPointParameter("points", "points", "Evaluated printing points. This is a full-resolution diagnostic tree whose paths match the p_points stored in wsp_path.", GH_ParamAccess.tree);
            p.AddPointParameter("closest_pts", "closest_pts", "Closest point on a neighboring support segment of the immediately previous layer. The segment is formed from the candidate point and its same-branch -1 or +1 neighbor. First-layer points output themselves because they are treated as bed-supported.", GH_ParamAccess.tree);
            p.AddVectorParameter("vects", "vects", "World-space support vector from closest_pts to points. Its component perpendicular to world Z describes the gravity-horizontal overhang, while its world-Z component describes vertical separation.", GH_ParamAccess.tree);
            p.AddNumberParameter("print_loc", "print_loc", "Local printability from 0 to 1. One is locally safe, values between zero and one indicate increasing collapse risk, and zero is not printable. The score combines gravity angle and geometric contact width.", GH_ParamAccess.tree);
            p.AddBooleanParameter("print_glob", "print_glob", "Global printability flag after support-chain propagation. A point is true only when its local geometric assessment is printable and both endpoints of the selected support segment are globally printable.", GH_ParamAccess.tree);
            p.AddNumberParameter("angle", "angle", "Overhang angle in degrees measured from the positive vertical support direction. Zero degrees is vertical support; larger values indicate more horizontal overhang.", GH_ParamAccess.tree);
            p.AddNumberParameter("layer_Wc", "layer_Wc", "Estimated contact width with the previous layer in model units: Wc = max(0, baseline - gravity-horizontal-overhang). The baseline is the contact width at perfect stacking: with noz_diam unset it is the geometric assumption baseline = layer_w.flows (full-width contact); with noz_diam set it is the Alhussain et al. (2024) closed-form prediction of the physically bonded width, equivalent to Wc ~ 0.9821.W - 0.3517.Hn - 0.1072.D bounded to [0, W], which accounts for the bead never bonding over its full width. The contact term of print_loc is normalized by the same baseline, so a perfectly stacked bead scores 1.0 in both modes; the absolute Wc (used by Pr03 for capacity) is smaller and more realistic in model mode. The resolved mode and D travel with the wsp_path output.", GH_ParamAccess.tree);
            p.AddGenericParameter("print_kpis", "print_kpis", "Global printability KPIs for the complete component evaluation: mean/min/max local printability, globally printable fraction, overhang-angle statistics, and contact-width statistics. These are scalar job-level records, not per-point data.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            global::WASPer_3DP.WasperPrintPath packedPath = null;
            bool hasPackedPath = global::WASPer_3DP.WasperGcodeTreeUtil.TryGetPrintPath(DA, 0, out packedPath);
            if (!hasPackedPath || packedPath == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Please provide a valid wsp_path.");
                return;
            }

            GH_Structure<GH_Point> pointTree = null;
            GH_Structure<GH_Plane> planeTree = null;
            GH_Structure<GH_Number> flowTree = null;
            GH_Structure<GH_Number> layerHTree = null;
            GH_Structure<GH_Number> layerWTree = null;
            var angleCrit = new List<double>();
            double angleMin = 30.0;
            double angleMax = 45.0;
            bool hardAngle = false;

            if (hasPackedPath)
                pointTree = global::WASPer_3DP.WasperGcodeTreeUtil.ToPointStructure(packedPath.Points);
            if (pointTree == null || pointTree.PathCount == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Please provide a valid wsp_path containing non-empty p_points.");
                return;
            }
            if (hasPackedPath && packedPath.HasPlanes) planeTree = global::WASPer_3DP.WasperGcodeTreeUtil.ToPlaneStructure(packedPath.PtPlanes);
            if (hasPackedPath && packedPath.HasFlows) flowTree = global::WASPer_3DP.WasperGcodeTreeUtil.ToNumberStructure(packedPath.Flows);
            if (hasPackedPath && packedPath.HasLayerH) layerHTree = global::WASPer_3DP.WasperGcodeTreeUtil.ToNumberStructure(packedPath.LayerH);
            bool explicitLayerW = Params.Input[1].SourceCount > 0 && DA.GetDataTree(1, out layerWTree) && layerWTree != null && layerWTree.PathCount > 0;
            bool layerWAlreadyEffective = false;
            if (!explicitLayerW && hasPackedPath && packedPath.HasLayerW)
                layerWTree = global::WASPer_3DP.WasperGcodeTreeUtil.ToNumberStructure(packedPath.LayerW);
            else if (!explicitLayerW && hasPackedPath && packedPath.HasLayerWf)
            {
                layerWTree = global::WASPer_3DP.WasperGcodeTreeUtil.ToNumberStructure(packedPath.LayerWf);
                layerWAlreadyEffective = true;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "wsp_path contains legacy layer_wf data but no LayerW; using layer_wf as the resolved width for this assessment.");
            }
            if (!explicitLayerW && (layerWTree == null || layerWTree.PathCount == 0) && layerHTree != null && layerHTree.PathCount > 0)
            {
                layerWTree = BuildDefaultLayerWidthTree(layerHTree, 2.5);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "wsp_path has no layer_w; using the default layer_w = layer_h * 2.5 for this assessment.");
            }
            if (planeTree == null || flowTree == null || layerHTree == null || layerWTree == null || layerWTree.PathCount == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "wsp_path must contain pt_planes, flows, and layer_h. layer_w may be packed or supplied as an override; otherwise Pr01 v2 can derive layer_w = layer_h * 2.5.");
                return;
            }
            double nozzleDiam = 0.0;
            bool explicitNozzle = Params.Input[2].SourceCount > 0 && DA.GetData(2, ref nozzleDiam);
            if (!explicitNozzle && hasPackedPath && packedPath.NozzleDiam.HasValue)
                nozzleDiam = packedPath.NozzleDiam.Value;
            if (explicitLayerW && hasPackedPath && packedPath.HasLayerW)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Override applied: explicit layer_w replaced wsp_path.LayerW; layer_wf and print_vol will be recomputed.");
            if (explicitNozzle && hasPackedPath && packedPath.NozzleDiam.HasValue)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Override applied: explicit noz_diam replaced wsp_path.NozzleDiam.");
            DA.GetDataList(3, angleCrit);

            if (!double.IsFinite(nozzleDiam) || nozzleDiam < 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "noz_diam must be a finite value >= 0. The geometric Wc baseline will be used.");
                nozzleDiam = 0.0;
            }

            if (angleCrit.Count == 0)
                angleCrit.AddRange(new[] { 30.0, 45.0 });
            if (angleCrit.Count > 2 || angleCrit.Any(v => !double.IsFinite(v)))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "angle_crit must contain one or two finite values.");
                return;
            }
            if (angleCrit.Count == 1)
            {
                hardAngle = true;
                angleMin = 0.0;
                angleMax = angleCrit[0];
            }
            else
            {
                angleMin = angleCrit[0];
                angleMax = angleCrit[1];
            }

            if (!double.IsFinite(angleMin) || !double.IsFinite(angleMax) || angleMin < 0.0 || angleMax <= angleMin)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "angle_crit must satisfy 0 <= risk_start < critical for two values, or critical > 0 for one value.");
                return;
            }
            if (angleMax > 45.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "angle_crit cannot exceed 45 degrees; using 45 degrees for the critical value.");
                angleMax = 45.0;
                if (angleCrit.Count == 2 && angleMin >= angleMax) angleMin = 30.0;
            }
            int samplingInterval = 1;
            double tol = RhinoDoc.ActiveDoc != null ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 1e-6;

            var branches = BuildPointBranches(pointTree);
            if (branches.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "p_points contains no valid points.");
                return;
            }

            // Resolve process trees once per branch. The assessment loop can visit
            // millions of points, so performing get_Branch/OfType/ToList for every
            // point creates avoidable allocations and tree traversal overhead.
            foreach (PointBranch branch in branches)
            {
                branch.Flows = ResolveNumberValues(flowTree, branch.Path, branch.Points.Count, 1.0, value => Math.Max(0.0, value));
                branch.LayerHeights = ResolveNumberValues(layerHTree, branch.Path, branch.Points.Count, 0.0, value => value > RhinoMath.ZeroTolerance ? value : 0.0);
                branch.LayerWidths = ResolveNumberValues(layerWTree, branch.Path, branch.Points.Count, 0.0, value => value > RhinoMath.ZeroTolerance ? value : 0.0);
            }
            var planeLookup = BuildPlaneLookup(planeTree);
            int planeFallbacks = 0;
            foreach (PointBranch branch in branches)
                for (int i = 0; i < branch.Points.Count; i++)
                {
                    Plane unused;
                    if (!TryGetPlane(planeLookup, branch.Path, i, out unused)) planeFallbacks++;
                }
            if (planeFallbacks > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"pt_planes is missing or invalid for {planeFallbacks} point(s). World Z will be used for those points.");

            var layers = branches
                .GroupBy(b => b.Layer)
                .OrderBy(g => g.Key)
                .ToList();

            // full-resolution diagnostic trees
            var outPts = new DataTree<Point3d>();
            var outClosest = new DataTree<Point3d>();
            var outVects = new DataTree<Vector3d>();
            var outLocal = new DataTree<double>();
            var outGlobal = new DataTree<bool>();
            var outAngle = new DataTree<double>();
            var outWc = new DataTree<double>();
            // full-resolution trees packed into wsp_path (never thinned: downstream
            // components align them 1:1 with p_points)
            var fullClosest = new DataTree<Point3d>();
            var fullVects = new DataTree<Vector3d>();
            var fullLocal = new DataTree<double>();
            var fullGlobal = new DataTree<bool>();
            var fullAngle = new DataTree<double>();
            var fullWc = new DataTree<double>();
            var fullWf = new DataTree<double>();

            var previousPoints = new List<PointRecord>();
            bool[] previousOk = null;
            RTree previousTree = null;
            // [0] Hn* >= 1.6, [1] implied V* >= 2.0, [2] W* regression denominator fallback
            var wcCounters = new int[3];

            foreach (var layerGroup in layers)
            {
                var layerBranches = layerGroup.OrderBy(b => b.Path.ToString()).ToList();
                var currentPoints = FlattenLayer(layerBranches, planeLookup);
                var currentOk = Enumerable.Repeat(true, currentPoints.Count).ToArray();
                bool hasSupport = previousTree != null && previousPoints.Count > 0;

                var localBuckets = new ResultBucket[layerBranches.Count];

                Action<int> processBranch = bi =>
                {
                    var branch = layerBranches[bi];
                    var bucket = new ResultBucket(branch.Points.Count);
                    localBuckets[bi] = bucket;

                    for (int pi = 0; pi < branch.Points.Count; pi++)
                    {
                        int globalIndex = branch.StartIndex + pi;
                        Point3d point = branch.Points[pi];
                        ResultItem item = EvaluatePoint(
                            point,
                            branch.Path,
                            pi,
                            hasSupport,
                            previousTree,
                            previousPoints,
                            previousOk,
                            currentPoints[globalIndex].Plane,
                            branch.Flows[pi],
                            branch.LayerHeights[pi],
                            branch.LayerWidths[pi],
                            layerWAlreadyEffective,
                            angleMin,
                            angleMax,
                            hardAngle,
                            nozzleDiam,
                            wcCounters,
                            tol);

                        currentOk[globalIndex] = item.GlobalOk;

                        // every item is kept so the diagnostics and wsp_path stay aligned
                        bucket.Items.Add(item);
                    }
                };

                if (currentPoints.Count >= 500 && layerBranches.Count >= 2)
                    Parallel.For(0, layerBranches.Count, processBranch);
                else
                    for (int i = 0; i < layerBranches.Count; i++) processBranch(i);

                foreach (var bucket in localBuckets)
                {
                    if (bucket == null) continue;

                    int li = 0;
                    foreach (ResultItem item in bucket.Items)
                    {
                        fullClosest.Add(item.ClosestPoint, item.Path);
                        fullVects.Add(item.VectorToPoint, item.Path);
                        fullLocal.Add(Round(1.0 - item.LocalRisk), item.Path);
                        fullGlobal.Add(item.GlobalOk, item.Path);
                        fullAngle.Add(Round(item.AngleDeg), item.Path);
                        fullWc.Add(Round(item.ContactWidth), item.Path);
                        fullWf.Add(item.EffectiveWidth, item.Path);

                        if (li % samplingInterval == 0)
                        {
                            outPts.Add(item.Point, item.Path);
                            outClosest.Add(item.ClosestPoint, item.Path);
                            outVects.Add(item.VectorToPoint, item.Path);
                            outLocal.Add(Round(1.0 - item.LocalRisk), item.Path);
                            outGlobal.Add(item.GlobalOk, item.Path);
                            outAngle.Add(Round(item.AngleDeg), item.Path);
                            outWc.Add(Round(item.ContactWidth), item.Path);
                        }
                        li++;
                    }
                }

                previousPoints = currentPoints;
                previousOk = currentOk;
                previousTree = new RTree();
                for (int i = 0; i < previousPoints.Count; i++)
                    previousTree.Insert(previousPoints[i].Point, i);
            }

            if (nozzleDiam > 0.0)
            {
                if (wcCounters[2] > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"{wcCounters[2]} point(s) had width/height ratios outside the Alhussain W* regression validity (non-positive denominator); the geometric Wc baseline was used for those points.");
                if (wcCounters[0] > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"{wcCounters[0]} point(s) have Hn* = layer_h/noz_diam >= 1.6, outside the Alhussain et al. (2024) confidence range; their Wc is extrapolated.");
                if (wcCounters[1] > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                        $"{wcCounters[1]} point(s) imply V* >= 2.0 for the requested width, outside the Alhussain et al. (2024) confidence range; their Wc is extrapolated.");
            }

            DataTree<double> resolvedLayerW = global::WASPer_3DP.WasperGcodeTreeUtil.ToDoubleTree(layerWTree);
            DataTree<double> resolvedPrintVol = BuildPrintVolumeTree(
                pointTree,
                layerHTree, fullWf);
            DA.SetData(0, new global::WASPer_3DP.WasperPrintPathGoo(
                new global::WASPer_3DP.WasperPrintPath(
                    global::WASPer_3DP.WasperGcodeTreeUtil.ToPointTree(pointTree),
                    hasPackedPath ? packedPath.PtPlanes : null,
                    hasPackedPath ? packedPath.Flows : null,
                    hasPackedPath ? packedPath.LayerH : null,
                    hasPackedPath ? packedPath.PrintSpeed : null,
                    fullLocal,
                    fullGlobal,
                    fullClosest,
                    fullVects,
                    fullAngle,
                    fullWc,
                    riskMaterial: hasPackedPath ? packedPath.RiskMaterial : null,
                    riskComb: hasPackedPath ? packedPath.RiskComb : null,
                    load: hasPackedPath ? packedPath.Load : null,
                    capacity: hasPackedPath ? packedPath.Capacity : null,
                    nozzleDiam: nozzleDiam > 0.0 ? nozzleDiam : (double?)null,
                    dRatio: hasPackedPath ? packedPath.DRatio : null,
                    dLoaded: hasPackedPath ? packedPath.DLoaded : null,
                    bendRatio: hasPackedPath ? packedPath.BendRatio : null,
                    spanClass: hasPackedPath ? packedPath.SpanClass : null,
                    spanLen: hasPackedPath ? packedPath.SpanLen : null,
                    collapsed: hasPackedPath ? packedPath.Collapsed : null,
                    cascade: hasPackedPath ? packedPath.Cascade : null,
                    collapseGen: hasPackedPath ? packedPath.CollapseGen : null,
                    layerW: resolvedLayerW,
                    layerWf: fullWf,
                    printVol: resolvedPrintVol,
                    torn: hasPackedPath ? packedPath.Torn : null,
                    interfaceRatio: hasPackedPath ? packedPath.InterfaceRatio : null,
                    overturnRatio: hasPackedPath ? packedPath.OverturnRatio : null,
                    failureFlags: hasPackedPath ? packedPath.FailureFlags : null,
                    pathRoles: hasPackedPath
                        ? global::WASPer_3DP.WasperGcodeTreeUtil.FilterPathRoles(
                            packedPath.PathRoles,
                            pointTree.Paths)
                        : null,
                    layerPlanes: hasPackedPath
                        ? global::WASPer_3DP.WasperGcodeTreeUtil.FilterLayerPlanes(
                            packedPath.LayerPlanes,
                            pointTree.Paths,
                            global::WASPer_3DP.WasperGcodeTreeUtil.CommonPathPrefixLength(
                                pointTree.Paths.ToList()))
                        : null)));
            DA.SetDataTree(1, outPts);
            DA.SetDataTree(2, outClosest);
            DA.SetDataTree(3, outVects);
            DA.SetDataTree(4, outLocal);
            DA.SetDataTree(5, outGlobal);
            DA.SetDataTree(6, outAngle);
            DA.SetDataTree(7, outWc);
            DA.SetData(8, new global::WASPer_3DP.WasperKpiSetGoo(
                BuildPrintabilityKpis(outLocal, outGlobal, outAngle, outWc),
                this));
            Message = nozzleDiam > 0.0 ? _versionTag + " | Wc model" : _versionTag + " | Wc geometric";
        }

        private WasperKpiSet BuildPrintabilityKpis(
            DataTree<double> local,
            DataTree<bool> global,
            DataTree<double> angles,
            DataTree<double> contactWidths)
        {
            var set = new WasperKpiSet
            {
                SourceComponent = Name,
                SourceVersion = _versionTag
            };
            AddStats(set, "printability.local", "Local printability", "Printability", "-", Values(local), "Local geometric printability score across all evaluated points.");
            AddStats(set, "printability.overhang_angle", "Overhang angle", "Printability", "deg", Values(angles), "Overhang angle statistics across all evaluated points.");
            AddStats(set, "printability.contact_width", "Contact width", "Printability", "mm", Values(contactWidths), "Estimated contact-width statistics across all evaluated points.");

            List<bool> flags = global?.Branches
                .SelectMany(branch => branch ?? Enumerable.Empty<bool>())
                .ToList() ?? new List<bool>();
            if (flags.Count > 0)
            {
                set.Add(WasperKpi.Scalar(
                    "printability.globally_printable_fraction",
                    "Globally printable fraction",
                    "Printability",
                    "-",
                    flags.Count(value => value) / (double)flags.Count,
                    "Fraction of evaluated points that pass the propagated global support-chain test.",
                    Name));
                set.Add(WasperKpi.Scalar(
                    "printability.critical_point_count",
                    "Critical point count",
                    "Printability",
                    "count",
                    flags.Count(value => !value),
                    "Number of evaluated points that fail the global printability test.",
                    Name));
            }
            return set;
        }

        private static IEnumerable<double> Values(DataTree<double> tree)
        {
            return tree?.Branches
                .SelectMany(branch => branch ?? Enumerable.Empty<double>())
                .Where(item => !double.IsNaN(item) && !double.IsInfinity(item)) ?? Enumerable.Empty<double>();
        }

        private void AddStats(WasperKpiSet set, string key, string label, string group, string unit, IEnumerable<double> values, string description)
        {
            List<double> list = values.ToList();
            if (list.Count == 0)
                return;
            set.Add(WasperKpi.Scalar(key + ".mean", label + " (mean)", group, unit, list.Average(), description, Name));
            set.Add(WasperKpi.Scalar(key + ".min", label + " (minimum)", group, unit, list.Min(), description, Name));
            set.Add(WasperKpi.Scalar(key + ".max", label + " (maximum)", group, unit, list.Max(), description, Name));
        }

        private static List<PointBranch> BuildPointBranches(GH_Structure<GH_Point> tree)
        {
            var branches = new List<PointBranch>();
            if (tree == null || tree.PathCount == 0) return branches;

            // Layers are the first varying path index after any common grafted prefix
            // (Gc01 convention), so grafted {0;layer} trees resolve correctly.
            int prefix = global::WASPer_3DP.WasperGcodeTreeUtil.CommonPathPrefixLength(tree.Paths.ToList());

            for (int i = 0; i < tree.PathCount; i++)
            {
                GH_Path path = tree.get_Path(i);
                var pts = new List<Point3d>();
                foreach (GH_Point ghPoint in tree.get_Branch(path).OfType<GH_Point>())
                {
                    if (ghPoint != null && ghPoint.Value.IsValid)
                        pts.Add(ghPoint.Value);
                }

                if (pts.Count > 0)
                    branches.Add(new PointBranch(path, global::WASPer_3DP.WasperGcodeTreeUtil.LayerFromPath(path, prefix), pts));
            }

            return branches;
        }

        private static List<PointBranch> BuildPointBranchesFromCurves(GH_Structure<GH_Curve> tree, double segmentLength)
        {
            var branches = new List<PointBranch>();
            if (tree == null || tree.PathCount == 0) return branches;

            int prefix = global::WASPer_3DP.WasperGcodeTreeUtil.CommonPathPrefixLength(tree.Paths.ToList());

            for (int i = 0; i < tree.PathCount; i++)
            {
                GH_Path path = tree.get_Path(i);
                var pts = new List<Point3d>();
                foreach (GH_Curve ghCurve in tree.get_Branch(path).OfType<GH_Curve>())
                {
                    Curve curve = ghCurve?.Value;
                    if (curve == null || !curve.IsValid) continue;

                    double length;
                    try { length = curve.GetLength(); }
                    catch { continue; }

                    if (length <= RhinoMath.ZeroTolerance)
                    {
                        pts.Add(curve.PointAtStart);
                        continue;
                    }

                    int divisions = Math.Max(1, (int)Math.Ceiling(length / Math.Max(segmentLength, RhinoMath.ZeroTolerance)));
                    double[] parameters = curve.DivideByCount(divisions, true);
                    if (parameters == null || parameters.Length == 0)
                    {
                        pts.Add(curve.PointAtStart);
                        pts.Add(curve.PointAtEnd);
                        continue;
                    }

                    foreach (double t in parameters)
                        pts.Add(curve.PointAt(t));
                }

                if (pts.Count > 0)
                    branches.Add(new PointBranch(path, global::WASPer_3DP.WasperGcodeTreeUtil.LayerFromPath(path, prefix), pts));
            }

            return branches;
        }

        private static List<PointRecord> FlattenLayer(List<PointBranch> branches, PlaneLookup planeLookup)
        {
            var records = new List<PointRecord>();
            foreach (PointBranch branch in branches)
            {
                branch.StartIndex = records.Count;
                for (int i = 0; i < branch.Points.Count; i++)
                {
                    Plane plane;
                    if (!TryGetPlane(planeLookup, branch.Path, i, out plane))
                        plane = Plane.WorldXY;
                    records.Add(new PointRecord(branch.Points[i], branch.Path, i, plane));
                }
            }

            return records;
        }

        private static ResultItem EvaluatePoint(
            Point3d point,
            GH_Path path,
            int itemIndex,
            bool hasSupport,
            RTree previousTree,
            List<PointRecord> previousPoints,
            bool[] previousOk,
            Plane currentPlane,
            double flow,
            double layerH,
            double layerW,
            bool layerWAlreadyEffective,
            double angleMin,
            double angleMax,
            bool hardAngle,
            double nozzleDiam,
            int[] wcCounters,
            double tol)
        {
            double effectiveWidth = layerWAlreadyEffective
                ? layerW
                : EstimateFlowAdjustedWidth(layerW, layerH, flow, tol);
            double baseWc = ComputeBaseContactWidth(effectiveWidth, layerH, nozzleDiam, wcCounters);

            if (!hasSupport || layerH <= RhinoMath.ZeroTolerance || effectiveWidth <= RhinoMath.ZeroTolerance)
            {
                return new ResultItem(
                    path,
                    point,
                    point,
                    Vector3d.Zero,
                    0.0, baseWc, 0.0, true, true, effectiveWidth);
            }

            Point3d bestSupportPoint = Point3d.Unset;
            int bestSegmentA = -1;
            int bestSegmentB = -1;
            double bestSupportCost = double.MaxValue;
            double searchRadius = Math.Max(layerH * Math.Tan(angleMax * Math.PI / 180.0) * 1.5, effectiveWidth * 2.0);
            double verticalSearch = Math.Max(layerH * 2.0, searchRadius * 1.5);
            var bbox = new BoundingBox(
                new Point3d(point.X - searchRadius, point.Y - searchRadius, point.Z - verticalSearch),
                new Point3d(point.X + searchRadius, point.Y + searchRadius, point.Z + tol));

            previousTree.Search(bbox, (sender, args) =>
            {
                int candidate = args.Id;
                if (candidate < 0 || candidate >= previousPoints.Count) return;

                for (int side = 0; side < 2; side++)
                {
                    int a = side == 0 ? candidate - 1 : candidate;
                    int b = side == 0 ? candidate : candidate + 1;
                    if (a < 0 || b >= previousPoints.Count) continue;
                    if (!previousPoints[a].Path.Equals(previousPoints[b].Path)) continue;

                    PointRecord recordA = previousPoints[a];
                    PointRecord recordB = previousPoints[b];
                    Point3d support = ClosestPointOnSegment(point, recordA.Point, recordB.Point);
                    Vector3d vector = point - support;
                    double vertical = Vector3d.Multiply(vector, Vector3d.ZAxis);
                    if (vertical <= tol) continue;

                    Vector3d supportNormal = AverageUpwardNormal(recordA.Plane, recordB.Plane, currentPlane);
                    double localGap = Vector3d.Multiply(vector, supportNormal);
                    if (localGap <= tol || Math.Abs(localGap - layerH) > Math.Max(layerH * 0.5, tol * 10.0)) continue;

                    double horizontal = Math.Sqrt(Math.Max(0.0, vector.X * vector.X + vector.Y * vector.Y));
                    Vector3d segmentTangent = recordB.Point - recordA.Point;
                    if (!segmentTangent.Unitize()) segmentTangent = PathTangent(recordA.Plane);
                    Vector3d currentTangent = PathTangent(currentPlane);
                    double tangentAlignment = Math.Abs(Vector3d.Multiply(segmentTangent, currentTangent));
                    double gapError = Math.Abs(localGap - layerH);
                    double supportCost = horizontal * horizontal
                        + 0.25 * gapError * gapError
                        + (1.0 - tangentAlignment) * effectiveWidth * effectiveWidth * 0.05;
                    if (supportCost < bestSupportCost)
                    {
                        bestSupportCost = supportCost;
                        bestSupportPoint = support;
                        bestSegmentA = a;
                        bestSegmentB = b;
                    }
                }
            });

            if (bestSegmentA < 0)
            {
                return new ResultItem(
                    path,
                    point,
                    Point3d.Unset,
                    Vector3d.Unset,
                    1.0, 0.0, angleMax, false, false, effectiveWidth);
            }

            Point3d closest = bestSupportPoint;
            Vector3d vector = point - closest;
            double verticalComponent = Math.Max(0.0, Vector3d.Multiply(vector, Vector3d.ZAxis));
            double horizontal = Math.Sqrt(Math.Max(0.0, vector.X * vector.X + vector.Y * vector.Y));
            double angle = vector.IsTiny() ? 0.0 : Math.Atan2(horizontal, Math.Max(verticalComponent, tol)) * 180.0 / Math.PI;
            double contactWidth = Math.Max(0.0, baseWc - horizontal);
            double angleRisk = hardAngle
                ? (angle >= angleMax ? 1.0 : 0.0)
                : (angle <= angleMin ? 0.0 : Math.Min(1.0, (angle - angleMin) / Math.Max(angleMax - angleMin, RhinoMath.ZeroTolerance)));
            double contactRisk = baseWc > RhinoMath.ZeroTolerance ? 1.0 - Math.Min(1.0, contactWidth / baseWc) : 1.0;
            double localRisk = Math.Max(angleRisk, contactRisk);
            bool geometricOk = localRisk < 1.0 - 1e-12;
            bool previousChainOk = previousOk == null
                || (bestSegmentA >= 0 && bestSegmentB >= 0
                    && bestSegmentA < previousOk.Length && bestSegmentB < previousOk.Length
                    && previousOk[bestSegmentA] && previousOk[bestSegmentB]);
            bool combined = geometricOk && previousChainOk;

            return new ResultItem(
                path,
                point,
                closest,
                vector,
                    localRisk,
                    contactWidth,
                    angle,
                    geometricOk,
                    combined,
                    effectiveWidth);
        }

        /// <summary>
        /// Baseline contact width at perfect stacking (no lateral stagger).
        /// Geometric mode (nozzleDiam &lt;= 0): the full effective width — the historical
        /// assumption that a perfectly aligned bead bonds over its whole width.
        /// Model mode (nozzleDiam &gt; 0): Alhussain et al. (2024) filament-shape regressions,
        /// combined so printing speed cancels out. From the paper (same constants as Pr02):
        ///   W*  = 0.0139 + 0.7188/(V*.Hn*) + 0.2784.Hn*   (filament width regression)
        ///   Wc* = 0.7059/(V*.Hn*) - 0.0783.Hn* - 0.0935   (contact width regression)
        /// Solving the first for V*.Hn* from the TARGET width (W* = effectiveWidth/D,
        /// Hn* = layerH/D) and substituting into the second eliminates the velocity:
        ///   Wc* = (0.7059/0.7188).(W* - 0.0139 - 0.2784.Hn*) - 0.0783.Hn* - 0.0935
        /// i.e. approximately Wc = 0.9821.W - 0.3517.Hn - 0.1072.D, bounded to [0, W].
        /// Counters record paper-domain violations (Hn* >= 1.6, implied V* >= 2.0) and
        /// denominator fallbacks for aggregated warnings; the regression is mortar-fitted
        /// (3DCP) and indicative for clay/paste until calibrated.
        /// </summary>
        private static double ComputeBaseContactWidth(double effectiveWidth, double layerH, double nozzleDiam, int[] wcCounters)
        {
            if (nozzleDiam <= 0.0 || effectiveWidth <= RhinoMath.ZeroTolerance || layerH <= RhinoMath.ZeroTolerance)
                return effectiveWidth;

            double hStar = layerH / nozzleDiam;
            double wStar = effectiveWidth / nozzleDiam;
            double denominator = wStar - 0.0139 - 0.2784 * hStar;
            if (!double.IsFinite(denominator) || denominator <= 1e-9)
            {
                Interlocked.Increment(ref wcCounters[2]);
                return effectiveWidth; // geometric fallback
            }

            double vhStar = 0.7188 / denominator; // = V* . Hn*
            if (hStar >= 1.6) Interlocked.Increment(ref wcCounters[0]);
            if (vhStar / Math.Max(hStar, RhinoMath.ZeroTolerance) >= 2.0) Interlocked.Increment(ref wcCounters[1]);

            double wcStar = 0.7059 / vhStar - 0.0783 * hStar - 0.0935;
            return Math.Max(0.0, Math.Min(wcStar * nozzleDiam, effectiveWidth));
        }

        private static PlaneLookup BuildPlaneLookup(GH_Structure<GH_Plane> tree)
        {
            var lookup = new PlaneLookup();
            if (tree == null || tree.PathCount == 0) return lookup;

            for (int i = 0; i < tree.PathCount; i++)
            {
                GH_Path path = tree.get_Path(i);
                var branch = tree.get_Branch(path);
                var planes = branch == null
                    ? new List<Plane>()
                    : branch.OfType<GH_Plane>().Where(p => p != null && p.Value.IsValid).Select(p => p.Value).ToList();
                lookup.Branches[path.ToString()] = planes;
                lookup.ValidCount += planes.Count;
                if (tree.PathCount == 1)
                    lookup.Global = planes;
            }

            return lookup;
        }

        private static bool TryGetPlane(PlaneLookup lookup, GH_Path path, int index, out Plane plane)
        {
            plane = Plane.Unset;
            if (lookup == null) return false;

            List<Plane> branch;
            if (!lookup.Branches.TryGetValue(path.ToString(), out branch) || branch.Count == 0)
                branch = lookup.Global;
            if (branch == null || branch.Count == 0) return false;

            plane = branch[Math.Min(index, branch.Count - 1)];
            return plane.IsValid;
        }

        private static Vector3d UpwardNormal(Plane plane)
        {
            Vector3d normal = plane.IsValid ? plane.ZAxis : Vector3d.ZAxis;
            if (!normal.Unitize()) normal = Vector3d.ZAxis;
            if (Vector3d.Multiply(normal, Vector3d.ZAxis) < 0.0) normal.Reverse();
            return normal;
        }

        private static Vector3d AverageUpwardNormal(Plane supportPlane, Plane currentPlane)
        {
            Vector3d support = UpwardNormal(supportPlane);
            Vector3d current = UpwardNormal(currentPlane);
            Vector3d average = support + current;
            if (!average.Unitize()) return support;
            return average;
        }

        private static Vector3d AverageUpwardNormal(Plane firstPlane, Plane secondPlane, Plane thirdPlane)
        {
            Vector3d average = UpwardNormal(firstPlane) + UpwardNormal(secondPlane) + UpwardNormal(thirdPlane);
            if (!average.Unitize()) return UpwardNormal(firstPlane);
            return average;
        }

        private static Vector3d PathTangent(Plane plane)
        {
            Vector3d tangent = plane.IsValid ? plane.YAxis : Vector3d.XAxis;
            if (!tangent.Unitize()) tangent = Vector3d.XAxis;
            return tangent;
        }

        private static Point3d ClosestPointOnSegment(Point3d point, Point3d start, Point3d end)
        {
            Vector3d segment = end - start;
            double lengthSquared = segment.SquareLength;
            if (lengthSquared <= RhinoMath.ZeroTolerance * RhinoMath.ZeroTolerance)
                return start;

            double t = Vector3d.Multiply(point - start, segment) / lengthSquared;
            t = Math.Max(0.0, Math.Min(1.0, t));
            return start + segment * t;
        }

        private static bool TryGetNumber(GH_Structure<GH_Number> tree, GH_Path path, int index, out double value)
        {
            value = 0.0;
            if (tree == null || tree.PathCount == 0) return false;

            var rawBranch = tree.get_Branch(path);
            IList<GH_Number> branch = rawBranch == null
                ? new List<GH_Number>()
                : rawBranch.OfType<GH_Number>().ToList();
            if (branch.Count == 0 && tree.PathCount == 1)
            {
                var globalBranch = tree.get_Branch(tree.get_Path(0));
                branch = globalBranch == null ? new List<GH_Number>() : globalBranch.OfType<GH_Number>().ToList();
            }
            if (branch.Count == 0) return false;

            GH_Number number = branch[Math.Min(index, branch.Count - 1)];
            if (number == null) return false;

            value = number.Value;
            return double.IsFinite(value);
        }

        private static double[] ResolveNumberValues(
            GH_Structure<GH_Number> tree,
            GH_Path path,
            int count,
            double fallback,
            Func<double, double> normalize)
        {
            var values = new double[Math.Max(0, count)];
            if (values.Length == 0) return values;
            for (int i = 0; i < values.Length; i++) values[i] = fallback;
            if (tree == null || tree.PathCount == 0) return values;

            var rawBranch = tree.get_Branch(path);
            if (rawBranch == null || rawBranch.Count == 0)
            {
                if (tree.PathCount != 1) return values;
                rawBranch = tree.get_Branch(tree.get_Path(0));
            }
            if (rawBranch == null || rawBranch.Count == 0) return values;

            var numbers = rawBranch.OfType<GH_Number>().Where(number => number != null).ToList();
            if (numbers.Count == 0) return values;
            for (int i = 0; i < values.Length; i++)
            {
                double candidate = numbers[Math.Min(i, numbers.Count - 1)].Value;
                if (double.IsFinite(candidate)) values[i] = normalize(candidate);
            }
            return values;
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

        private static GH_Structure<GH_Number> BuildDefaultLayerWidthTree(GH_Structure<GH_Number> heights, double multiplier)
        {
            var result = new GH_Structure<GH_Number>();
            if (heights == null) return result;

            for (int b = 0; b < heights.PathCount; b++)
            {
                GH_Path path = heights.Paths[b];
                var branch = heights.get_Branch(path);
                if (branch == null) continue;

                foreach (GH_Number number in branch.OfType<GH_Number>())
                {
                    double h = number != null && double.IsFinite(number.Value) ? number.Value : 0.0;
                    result.Append(new GH_Number(Math.Max(0.0, h * multiplier)), path);
                }
            }

            return result;
        }

        private static DataTree<double> BuildPrintVolumeTree(
            GH_Structure<GH_Point> points,
            GH_Structure<GH_Number> heights,
            DataTree<double> widths)
        {
            var result = new DataTree<double>();
            if (points == null) return result;

            for (int b = 0; b < points.PathCount; b++)
            {
                GH_Path path = points.Paths[b];
                var pointBranch = points.get_Branch(path);
                var widthBranch = widths != null && widths.PathExists(path) ? widths.Branch(path) : null;
                int count = pointBranch?.Count ?? 0;

                for (int i = 0; i < count; i++)
                {
                    double volume = 0.0;
                    if (i > 0 && pointBranch[i - 1] is GH_Point previous && pointBranch[i] is GH_Point current)
                    {
                        double width = widthBranch != null && widthBranch.Count > 0
                            ? widthBranch[Math.Min(i, widthBranch.Count - 1)]
                            : 0.0;
                        double height = TryGetNumber(heights, path, i, out double h) ? h : 0.0;
                        double length = previous.Value.DistanceTo(current.Value);
                        if (width > 0.0 && height > 0.0 && double.IsFinite(length))
                        {
                            double area = BeadArea(width, height, 1e-9);
                            if (area > 0.0 && double.IsFinite(area))
                                volume = length * area;
                        }
                    }
                    result.Add(volume, path);
                }
            }

            return result;
        }

        private static double Round(double value)
        {
            return double.IsFinite(value) ? Math.Round(value, 4) : value;
        }

        private sealed class PointBranch
        {
            public PointBranch(GH_Path path, int layer, List<Point3d> points)
            {
                Path = path;
                Layer = layer;
                Points = points;
            }

            public GH_Path Path { get; }
            public int Layer { get; }
            public List<Point3d> Points { get; }
            public int StartIndex { get; set; }
            public double[] Flows { get; set; }
            public double[] LayerHeights { get; set; }
            public double[] LayerWidths { get; set; }
        }

        private sealed class PointRecord
        {
            public PointRecord(Point3d point, GH_Path path, int index, Plane plane)
            {
                Point = point;
                Path = path;
                Index = index;
                Plane = plane;
            }

            public Point3d Point { get; }
            public GH_Path Path { get; }
            public int Index { get; }
            public Plane Plane { get; }
        }

        private sealed class PlaneLookup
        {
            public Dictionary<string, List<Plane>> Branches { get; } = new Dictionary<string, List<Plane>>();
            public List<Plane> Global { get; set; }
            public int ValidCount { get; set; }
        }

        private sealed class ResultBucket
        {
            public ResultBucket(int capacity)
            {
                Items = new List<ResultItem>(Math.Max(0, capacity));
            }

            public List<ResultItem> Items { get; }
        }

        private sealed class ResultItem
        {
            public ResultItem(
                GH_Path path,
                Point3d point,
                Point3d closestPoint,
                Vector3d vectorToPoint,
                double localRisk,
                double contactWidth,
                double angleDeg,
                bool geometricOk,
                bool globalOk,
                double effectiveWidth = 0.0)
            {
                Path = path;
                Point = point;
                ClosestPoint = closestPoint;
                VectorToPoint = vectorToPoint;
                LocalRisk = localRisk;
                ContactWidth = contactWidth;
                AngleDeg = angleDeg;
                GeometricOk = geometricOk;
                GlobalOk = globalOk;
                EffectiveWidth = effectiveWidth;
            }

            public GH_Path Path { get; }
            public Point3d Point { get; }
            public Point3d ClosestPoint { get; }
            public Vector3d VectorToPoint { get; }
            public double LocalRisk { get; }
            public double ContactWidth { get; }
            public double AngleDeg { get; }
            public bool GeometricOk { get; }
            public bool GlobalOk { get; }
            public double EffectiveWidth { get; }
        }
    }
}
