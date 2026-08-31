using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using WASPer_3DP;

namespace WASPer_3DP.Components._4_1_Printability
{
    public sealed class wsp_Pr03_Fresh_Deformation_Risk : GH_Component
    {
        private const double G_CONST = 9.80665;      // m/s2
        private const int ParallelThreshold = 2048;  // nodes per layer before parallel search

        public wsp_Pr03_Fresh_Deformation_Risk()
            : base(
                "wsp_Pr03_Fresh Deformation Risk",
                "Fresh Risk",
                "Estimates comparative fresh-state deformation risk from the Pr01 support chain, path dimensions, self-weight, and optional fresh material properties. This is an uncalibrated risk proxy, not a FEM or deformation prediction.",
                WASPerPalette.DesignFabrication,
                "4.1_Printability")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            Message = v == null ? "v1.0.x" : $"v{v.Major}.{v.Minor}.{v.Build}";
        }

        public override Guid ComponentGuid => new("B4E2F1A7-7A1F-4F16-9F2F-8C0AF3F7C4D2");
        public override GH_Exposure Exposure => GH_Exposure.primary;
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Pr03_Fresh Deformation Risk.png");
                    return stream == null ? null : new System.Drawing.Bitmap(stream);
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("wsp_path", "wsp_path", "Enriched WASPer Print Path produced by Pr01.", GH_ParamAccess.item);
            p.AddGenericParameter("wsp_mat", "wsp_mat", "Optional WASPer Material containing fresh-state properties.", GH_ParamAccess.item);
            p.AddGenericParameter("3dp_props", "3dp_props", "Optional direct Ma12 properties. These override matching material properties.", GH_ParamAccess.item);
            p.AddNumberParameter("layer_time", "la_time", "Time between layers in seconds (scalar).", GH_ParamAccess.item, 60.0);
            p.AddVectorParameter("gravity", "gravity", "Gravity direction. Default is world -Z. Magnitude is ignored; g = 9.80665 m/s2 is applied internally.", GH_ParamAccess.item, new Vector3d(0, 0, -1));
            p.AddNumberParameter("limit", "limit", "Critical combined-risk threshold.", GH_ParamAccess.item, 1.0);
            p[1].Optional = true;
            p[2].Optional = true;
            p[3].Optional = true;
            p[4].Optional = true;
            p[5].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("wsp_path", "wsp_path", "Print path enriched with Pr03 fresh-state results: material risk, combined risk, accumulated load, and contact capacity, in addition to the incoming Pr01 assessment. Pass this object to downstream path-processing or inspection components to keep the complete live analysis record together.", GH_ParamAccess.item);
            p.AddNumberParameter("risk_mat", "risk_mat", "Fresh material risk per point, calculated as accumulated fresh self-weight demand divided by estimated contact capacity. This dimensionless comparative index describes the material/load contribution only: values below 1 indicate demand below the estimated capacity, values near 1 indicate a critical condition, and values above 1 indicate increasing deformation risk. It is not a prediction of displacement in millimetres.", GH_ParamAccess.tree);
            p.AddNumberParameter("risk_comb", "risk_comb", "Combined fresh-state deformation risk per point, calculated as the maximum of risk_mat and the geometric risk derived from Pr01 printability. This conservative index combines material self-weight effects with layer-to-layer support geometry: values below 1 indicate lower comparative risk, values near 1 indicate a threshold condition, and values above 1 identify regions requiring attention.", GH_ParamAccess.tree);
            p.AddNumberParameter("load", "load", "Accumulated fresh load transmitted through the Pr01 support chain at each point, in newtons. The value includes the local deposited segment self-weight and the load contributed by supported material above it, using wet density, bead-width/layer-height estimates, gravity, and the layer sequence.", GH_ParamAccess.tree);
            p.AddNumberParameter("capacity", "capacity", "Estimated fresh contact capacity at each point, in newtons. It is calculated from the time-dependent fresh yield stress and the estimated contact area between the deposited segment and its support. This is a simplified stability capacity, not a calibrated structural resistance.", GH_ParamAccess.tree);
            p.AddBooleanParameter("critical", "critical", "Boolean classification of risk_comb against the user-defined limit input. True means the point reaches or exceeds the selected threshold and should be reviewed as a potentially unstable region; false means it remains below that threshold.", GH_ParamAccess.tree);
            p.AddCurveParameter("segments", "segments", "Printing-path line segments corresponding to the evaluated path branches. Use these curves with risk_comb or risk_mat for gradient visualization, custom preview styling, or further geometric filtering.", GH_ParamAccess.list);
            p.AddTextParameter("summary", "summary", "Text report of the resolved fresh material properties, layer time, gravity constant, units conversion, number of evaluated points, maximum risk, and number of critical points. Use this output for quick inspection or logging.", GH_ParamAccess.item);
            p.AddTextParameter("warnings", "warnings", "List of diagnostic messages generated during evaluation, such as missing Pr01 support data, dry-density fallback, missing support correspondence, invalid inputs, or fallback bead-width estimates. An empty list means no diagnostic warnings were generated.", GH_ParamAccess.list);
            p.AddGenericParameter("risk_kpis", "risk_kpis", "Global fresh-deformation KPI set for the complete evaluation: risk statistics, critical fraction/count, load and capacity statistics, and accumulated self-weight.", GH_ParamAccess.item);
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
            double timeBetweenLayers = 60.0;
            Vector3d gravity = new Vector3d(0, 0, -1);
            double limit = 1.0;
            da.GetData(3, ref timeBetweenLayers);
            da.GetData(4, ref gravity);
            da.GetData(5, ref limit);

            var warnings = new List<string>();
            if (timeBetweenLayers < 0 || double.IsNaN(timeBetweenLayers) || double.IsInfinity(timeBetweenLayers))
            {
                warnings.Add("T was invalid and has been set to 60 seconds.");
                timeBetweenLayers = 60.0;
            }
            if (!gravity.Unitize())
            {
                warnings.Add("G was invalid and has been set to world -Z.");
                gravity = new Vector3d(0, 0, -1);
                gravity.Unitize();
            }
            if (limit <= 0 || double.IsNaN(limit) || double.IsInfinity(limit))
            {
                warnings.Add("limit was invalid and has been set to 1.0.");
                limit = 1.0;
            }

            var material = ExtractMaterial(matGoo);
            var directProps = Extract3dpProperties(propsGoo);
            var resolved = ResolveProperties(material, directProps, warnings);
            if (!resolved.TryGetValue("tau_y0", out var tauY0) || tauY0 <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "A positive tau_y0 fresh yield stress is required from 3dp_props or wsp_mat.");
                return;
            }

            double aThix = GetPositiveOrDefault(resolved, "A_thix", 0.0);
            double rhoWet;
            if (!TryPositive(resolved, "density_wet", out rhoWet))
            {
                if (TryPositive(resolved, "density", out rhoWet))
                    warnings.Add("density_wet is missing; dry density was used for fresh self-weight.");
                else
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "A positive density_wet or density property is required.");
                    return;
                }
            }

            var branches = Flatten(path);
            var nodes = branches.SelectMany(b => b).ToList();
            if (nodes.Count == 0) return;

            if (path.PrintLoc != null && path.PrintLoc.DataCount != path.Points.DataCount)
                warnings.Add($"Pr01 assessment carries {path.PrintLoc.DataCount} values for {path.Points.DataCount} points. Re-run the current Pr01 build (older builds thinned the packed assessment when red_viz < 1); misaligned data corrupts support lookups.");

            int layerCount = nodes.Select(n => n.Layer).Distinct().Count();
            if (layerCount <= 1)
                warnings.Add("Only one layer was detected from the tree paths — no load accumulation is possible. Check that the p_points tree separates layers (e.g. {layer;curve} or a grafted {0;layer}).");

            ComputeLocalDemandCapacity(branches, rhoWet, tauY0, aThix, timeBetweenLayers);
            double totalSelfWeight = nodes.Sum(n => n.Load);
            int orphanCount = AccumulateLoads(nodes);
            if (orphanCount > 0)
                warnings.Add($"{orphanCount} point(s) above the bottom layer had no lower-layer support correspondence; their load stayed local and is NOT transmitted downward (mass-conservation gap in the summary).");
            if (nodes.Any(n => n.WidthFallback))
                warnings.Add("Some points had no positive Pr01 contact width; layer height was used as the bead-width estimate.");

            int minLayer = nodes.Min(n => n.Layer);
            double bottomCarried = nodes.Where(n => n.Layer == minLayer).Sum(n => n.Load);

            // ---- outputs + packed trees ------------------------------------
            var riskComb = new GH_Structure<GH_Number>();
            var critical = new GH_Structure<GH_Boolean>();
            var packedRiskMat = new DataTree<double>();
            var packedRiskComb = new DataTree<double>();
            var packedLoad = new DataTree<double>();
            var packedCapacity = new DataTree<double>();
            var segments = new List<Curve>();

            double maxFiniteRisk = 0.0;
            int infCount = 0;
            int criticalCount = 0;
            var materialRiskValues = new List<double>();
            var combinedRiskValues = new List<double>();
            var loadValues = new List<double>();
            var capacityValues = new List<double>();

            foreach (var branch in branches)
            {
                for (int i = 0; i < branch.Count; i++)
                {
                    var node = branch[i];
                    double materialRisk = node.Capacity > 1e-12
                        ? node.Load / node.Capacity
                        : (node.Load > 0 ? double.PositiveInfinity : 0.0);
                    double geoRisk = Math.Max(0.0, 1.0 - node.PrintLoc);
                    if (!node.PrintGlob) geoRisk = 1.0;
                    double combined = Math.Max(materialRisk, geoRisk);

                    if (double.IsInfinity(combined)) infCount++;
                    else maxFiniteRisk = Math.Max(maxFiniteRisk, combined);
                    bool isCritical = combined >= limit;
                    if (isCritical) criticalCount++;
                    if (!double.IsNaN(materialRisk) && !double.IsInfinity(materialRisk)) materialRiskValues.Add(materialRisk);
                    if (!double.IsNaN(combined) && !double.IsInfinity(combined)) combinedRiskValues.Add(combined);
                    if (!double.IsNaN(node.Load) && !double.IsInfinity(node.Load)) loadValues.Add(node.Load);
                    if (!double.IsNaN(node.Capacity) && !double.IsInfinity(node.Capacity)) capacityValues.Add(node.Capacity);

                    riskComb.Append(new GH_Number(combined), node.Path);
                    critical.Append(new GH_Boolean(isCritical), node.Path);
                    packedRiskMat.Add(materialRisk, node.Path);
                    packedRiskComb.Add(combined, node.Path);
                    packedLoad.Add(node.Load, node.Path);
                    packedCapacity.Add(node.Capacity, node.Path);

                    if (i < branch.Count - 1)
                        segments.Add(new PolylineCurve(new[] { node.Point, branch[i + 1].Point }));
                }
            }

            var enriched = new WasperPrintPath(
                path.Points, path.PtPlanes, path.Flows, path.LayerH, path.PrintSpeed,
                path.PrintLoc, path.PrintGlob, path.SupportPts, path.SupportVects,
                path.Angles, path.ContactWidths,
                packedRiskMat, packedRiskComb, packedLoad, packedCapacity,
                path.NozzleDiam,
                path.DRatio, path.DLoaded, path.BendRatio, path.SpanClass, path.SpanLen,
                path.Collapsed, path.Cascade, path.CollapseGen,
                path.LayerW, path.LayerWf, path.PrintVol,
                path.Torn, path.InterfaceRatio, path.OverturnRatio, path.FailureFlags,
                pathRoles: path.PathRoles,
                layerPlanes: path.LayerPlanes);

            string maxRiskText = infCount > 0
                ? $"inf ({infCount} zero-capacity point(s)); max finite {maxFiniteRisk.ToString("R", CultureInfo.InvariantCulture)}"
                : maxFiniteRisk.ToString("R", CultureInfo.InvariantCulture);
            string summary = string.Format(CultureInfo.InvariantCulture,
                "wsp_Pr03_Fresh Deformation Risk\npoints: {0}\nlayers detected: {1} (bottom layer id: {2})\nrho_wet [kg/m3]: {3:R}\ntau_y0 [Pa]: {4:R}\nA_thix [Pa/s]: {5:R}\nE_fresh [Pa]: {6}\nT [s]: {7:R}\ng [m/s2]: {8:R}\nlimit [-]: {9:R}\nmax risk [-]: {10}\ncritical points [-]: {11}\nself-weight total [N]: {12:0.####} | carried by bottom layer [N]: {13:0.####} (ratio {14:0.##} — should approach 1.0 when accumulation works; orphans reduce it)\nload transfer: 2-nearest IDW + lateral smoothing (~ +/- layer_h along branch, mass-conserving)\nunits: path mm -> SI internally (volume 1e-9, area 1e-6)",
                nodes.Count, layerCount, minLayer, rhoWet, tauY0, aThix,
                resolved.TryGetValue("E_fresh", out var eFresh) ? eFresh.ToString("R", CultureInfo.InvariantCulture) : "not supplied",
                timeBetweenLayers, G_CONST, limit, maxRiskText, criticalCount,
                totalSelfWeight, bottomCarried, totalSelfWeight > 1e-12 ? bottomCarried / totalSelfWeight : 0.0);

            da.SetData(0, new WasperPrintPathGoo(enriched));
            da.SetDataTree(1, packedRiskMat);
            da.SetDataTree(2, riskComb);
            da.SetDataTree(3, packedLoad);
            da.SetDataTree(4, packedCapacity);
            da.SetDataTree(5, critical);
            da.SetDataList(6, segments);
            da.SetData(7, summary);
            da.SetDataList(8, warnings);
            var riskKpis = new WasperKpiSet { SourceComponent = Name, SourceVersion = Message };
            AddRiskStats(riskKpis, "risk.material", "Material risk", materialRiskValues);
            AddRiskStats(riskKpis, "risk.combined", "Combined risk", combinedRiskValues);
            AddRiskStats(riskKpis, "risk.load", "Accumulated load", loadValues, "N");
            AddRiskStats(riskKpis, "risk.capacity", "Contact capacity", capacityValues, "N");
            riskKpis.Add(WasperKpi.Scalar("risk.critical_fraction", "Critical fraction", "Printability", "-", nodes.Count == 0 ? 0.0 : criticalCount / (double)nodes.Count, "Fraction of evaluated points at or above the selected fresh-risk limit.", Name));
            riskKpis.Add(WasperKpi.Scalar("risk.critical_count", "Critical point count", "Printability", "count", criticalCount, "Number of evaluated points at or above the selected fresh-risk limit.", Name));
            riskKpis.Add(WasperKpi.Scalar("risk.self_weight_total", "Total self-weight", "Fabrication", "N", totalSelfWeight, "Total fresh self-weight evaluated by the component.", Name));
            da.SetData(9, new WasperKpiSetGoo(riskKpis, this));

            bool anyCritical = criticalCount > 0 || infCount > 0;
            Message = $"{(anyCritical ? "critical" : "stable")} | max {(infCount > 0 ? "inf" : maxFiniteRisk.ToString("0.00", CultureInfo.InvariantCulture))}";
        }

        private void AddRiskStats(WasperKpiSet set, string key, string label, IList<double> values, string unit = "-")
        {
            if (values == null || values.Count == 0)
                return;
            set.Add(WasperKpi.Scalar(key + ".mean", label + " (mean)", "Printability", unit, values.Average(), "Global statistics across all evaluated points.", Name));
            set.Add(WasperKpi.Scalar(key + ".max", label + " (maximum)", "Printability", unit, values.Max(), "Global statistics across all evaluated points.", Name));
        }

        // ---- flattening -----------------------------------------------------

        private static List<List<Node>> Flatten(WasperPrintPath path)
        {
            var result = new List<List<Node>>();
            // Layer = first varying path index after any common grafted prefix
            // (Gc01 convention); bare Indices[0] collapses grafted {0;layer} trees
            // into a single layer and silently disables load accumulation.
            int prefix = WasperGcodeTreeUtil.CommonPathPrefixLength(path.Points.Paths);
            for (int b = 0; b < path.Points.BranchCount; b++)
            {
                GH_Path branchPath = path.Points.Paths[b];
                var points = path.Points.Branch(b);
                // look branches up by PATH, not by positional index: producer trees
                // may store branches in a different order than p_points
                var locs = BranchAt(path.PrintLoc, branchPath);
                var globals = BranchAt(path.PrintGlob, branchPath);
                var supports = BranchAt(path.SupportPts, branchPath);
                var widths = BranchAt(path.ContactWidths, branchPath);
                var heights = BranchAt(path.LayerH, branchPath);

                var branchNodes = new List<Node>(points.Count);
                for (int i = 0; i < points.Count; i++)
                {
                    double layerH = ValueAt(heights, i, 1.0);
                    double width = ValueAt(widths, i, 0.0);
                    branchNodes.Add(new Node
                    {
                        Path = branchPath,
                        Index = i,
                        Layer = WasperGcodeTreeUtil.LayerFromPath(branchPath, prefix),
                        Point = points[i],
                        PrintLoc = Clamp01(ValueAt(locs, i, 0.0)),
                        PrintGlob = BoolAt(globals, i, false),
                        Support = PointAt(supports, i, points[i]),
                        Width = width,
                        WidthFallback = width <= 0,
                        LayerH = Math.Max(layerH, 0.0)
                    });
                }

                // segment length per node in one pass (last node reuses previous segment)
                for (int i = 0; i < branchNodes.Count; i++)
                {
                    if (branchNodes.Count == 1) branchNodes[i].SegLen = branchNodes[i].LayerH;
                    else if (i < branchNodes.Count - 1) branchNodes[i].SegLen = branchNodes[i].Point.DistanceTo(branchNodes[i + 1].Point);
                    else branchNodes[i].SegLen = branchNodes[i - 1].SegLen;
                }
                result.Add(branchNodes);
            }
            return result;
        }

        // ---- demand and capacity --------------------------------------------

        private static void ComputeLocalDemandCapacity(List<List<Node>> branches, double rhoWet, double tauY0, double aThix, double time)
        {
            void Process(List<Node> branch)
            {
                foreach (var node in branch)
                {
                    double width = node.Width > 0 ? node.Width : node.LayerH;
                    double volume = node.SegLen * width * node.LayerH * 1e-9;          // mm3 -> m3
                    node.Load = rhoWet * G_CONST * volume;                             // N
                    double age = Math.Max(0, node.Layer) * time;                       // s
                    double tau = Math.Max(0, tauY0 + aThix * age);                     // Pa
                    node.Capacity = tau * Math.Max(width * node.SegLen * 1e-6, 1e-12); // N
                }
            }

            if (branches.Count >= 8) Parallel.ForEach(branches, Process);
            else foreach (var branch in branches) Process(branch);
        }

        /// <summary>
        /// Sweeps layers top-down and transfers each node's accumulated load to the
        /// nearest previous-layer node around its Pr01 support point. One RTree per
        /// layer; O(n log n). Returns the count of unresolved correspondences.
        /// </summary>
        private static int AccumulateLoads(List<Node> nodes)
        {
            var byLayer = nodes.GroupBy(n => n.Layer).ToDictionary(g => g.Key, g => g.ToList());
            var layerKeys = byLayer.Keys.OrderByDescending(k => k).ToList();

            var trees = new Dictionary<int, RTree>();
            foreach (var kv in byLayer)
            {
                var tree = new RTree();
                for (int i = 0; i < kv.Value.Count; i++) tree.Insert(kv.Value[i].Point, i);
                trees[kv.Key] = tree;
            }

            int orphans = 0;
            for (int k = 0; k < layerKeys.Count - 1; k++)
            {
                int layer = layerKeys[k];
                int targetLayer = layerKeys[k + 1];
                var layerNodes = byLayer[layer];
                // all inbound transfers into this layer are complete (top-down sweep):
                // redistribute laterally along each branch before passing the load on.
                SmoothLayerLoads(layerNodes);
                var targetNodes = byLayer[targetLayer];
                var targetTree = trees[targetLayer];
                // two nearest support nodes with inverse-distance weights: dumping the
                // whole load on one node concentrates accumulation into a few chains
                // and leaves neighboring columns artificially unloaded
                var target1 = new int[layerNodes.Count];
                var target2 = new int[layerNodes.Count];
                var weight1 = new double[layerNodes.Count];

                void FindTargets(int i)
                {
                    var node = layerNodes[i];
                    double radius = Math.Max(node.SegLen * 2.0, Math.Max(node.LayerH * 4.0, 1.0));
                    int best = -1, second = -1;
                    double bestDist = double.MaxValue, secondDist = double.MaxValue;
                    for (int attempt = 0; attempt < 3 && best < 0; attempt++, radius *= 4.0)
                    {
                        bestDist = double.MaxValue;
                        secondDist = double.MaxValue;
                        second = -1;
                        var box = new BoundingBox(
                            node.Support - new Vector3d(radius, radius, radius),
                            node.Support + new Vector3d(radius, radius, radius));
                        targetTree.Search(box, (s, a) =>
                        {
                            double d = targetNodes[a.Id].Point.DistanceToSquared(node.Support);
                            if (d < bestDist)
                            {
                                secondDist = bestDist; second = best;
                                bestDist = d; best = a.Id;
                            }
                            else if (d < secondDist && a.Id != best)
                            {
                                secondDist = d; second = a.Id;
                            }
                        });
                    }
                    target1[i] = best;
                    target2[i] = second;
                    if (best >= 0 && second >= 0)
                    {
                        double d1 = Math.Sqrt(bestDist) + 1e-9;
                        double d2 = Math.Sqrt(secondDist) + 1e-9;
                        weight1[i] = (1.0 / d1) / (1.0 / d1 + 1.0 / d2);
                    }
                    else weight1[i] = 1.0;
                }

                if (layerNodes.Count >= ParallelThreshold) Parallel.For(0, layerNodes.Count, FindTargets);
                else for (int i = 0; i < layerNodes.Count; i++) FindTargets(i);

                for (int i = 0; i < layerNodes.Count; i++)
                {
                    double load = layerNodes[i].Load;
                    if (target1[i] >= 0)
                    {
                        targetNodes[target1[i]].Load += load * weight1[i];
                        if (target2[i] >= 0) targetNodes[target2[i]].Load += load * (1.0 - weight1[i]);
                    }
                    else orphans++;
                }
            }
            if (layerKeys.Count > 0) SmoothLayerLoads(byLayer[layerKeys[layerKeys.Count - 1]]);
            return orphans;
        }

        /// <summary>
        /// Mass-conserving lateral redistribution of accumulated load along each
        /// branch. Physically, a continuous fresh bead spreads point loads through
        /// shear at roughly a 45-degree cone, i.e. about one layer height sideways
        /// per layer dropped; without this, nearest-node transfer concentrates the
        /// whole column weight into a few chain points while neighboring points
        /// stay at bare self-weight. Each node's load is shared equally over the
        /// window of neighbors within +/- one layer height along the branch
        /// (clamped at branch ends), so the branch total is preserved exactly.
        /// </summary>
        private static void SmoothLayerLoads(List<Node> layerNodes)
        {
            int start = 0;
            while (start < layerNodes.Count)
            {
                int end = start;
                while (end + 1 < layerNodes.Count && ReferenceEquals(layerNodes[end + 1].Path, layerNodes[start].Path)) end++;
                int n = end - start + 1;
                if (n >= 3)
                {
                    double seg = Math.Max(layerNodes[start].SegLen, 1e-9);
                    int halfWidth = Math.Min(8, Math.Max(1, (int)Math.Round(layerNodes[start].LayerH / seg)));
                    var smoothed = new double[n];
                    for (int i = 0; i < n; i++)
                    {
                        int lo = Math.Max(0, i - halfWidth);
                        int hi = Math.Min(n - 1, i + halfWidth);
                        double share = layerNodes[start + i].Load / (hi - lo + 1);
                        for (int j = lo; j <= hi; j++) smoothed[j] += share;
                    }
                    for (int i = 0; i < n; i++) layerNodes[start + i].Load = smoothed[i];
                }
                start = end + 1;
            }
        }

        // ---- extraction and helpers ------------------------------------------

        private static Dictionary<string, double> ResolveProperties(WasperMaterial material, Wasper3dpProperties direct, List<string> warnings)
        {
            var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (material != null)
            {
                foreach (var key in new[] { "tau_y0", "A_thix", "E_fresh", "density_wet", "density" })
                    if (material.TryGetDouble(key, out var value)) result[key] = value;
            }
            if (direct != null)
            {
                foreach (var item in direct.Properties)
                    if (double.TryParse(item.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) result[item.Key] = value;
            }
            return result;
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

        private static List<T> BranchAt<T>(DataTree<T> tree, GH_Path branchPath)
            => tree != null && tree.PathExists(branchPath) ? tree.Branch(branchPath) : null;

        private static double ValueAt(IList<double> values, int index, double fallback)
        {
            if (values == null || values.Count == 0) return fallback;
            return values[Math.Min(index, values.Count - 1)];
        }

        private static bool BoolAt(IList<bool> values, int index, bool fallback) => values == null || values.Count == 0 ? fallback : values[Math.Min(index, values.Count - 1)];
        private static Point3d PointAt(IList<Point3d> values, int index, Point3d fallback) => values == null || values.Count == 0 ? fallback : values[Math.Min(index, values.Count - 1)];
        private static double Clamp01(double value) => Math.Max(0.0, Math.Min(1.0, value));
        private static double GetPositiveOrDefault(Dictionary<string, double> values, string key, double fallback) => TryPositive(values, key, out var value) ? value : fallback;
        private static bool TryPositive(Dictionary<string, double> values, string key, out double value) => values.TryGetValue(key, out value) && value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);

        private sealed class Node
        {
            public GH_Path Path;
            public int Index, Layer;
            public Point3d Point, Support;
            public double PrintLoc, Width, LayerH, SegLen, Load, Capacity;
            public bool PrintGlob, WidthFallback;
        }
    }
}
