// -----------------------------------------------------------------------------
//  wsp_Gc08_Speed from Curvature  (Parallelised, with mode label & norm_curv)
//  -----------------------------------------------------------------------------
//  - Input: p_points {layer; curve} as from wsp_Gc01_* (planar / non-planar).
//  - Per branch:
//        * Compute local discrete curvature at each point.
//        * Optionally smooth curvature in index space (moving average).
//  - Global step:
//        * Collect curvature from all branches, compute a percentile-based k_max.
//  - Map curvature to speed [mm/min]:
//        * Straight segments (low curvature) -> s_max
//        * Tight bends (high curvature) -> s_min
//  - Outputs:
//        * speeds    (mm/min) per point, tree matches p_points.
//        * curvature:
//             - raw curvature [rad/unit_length] if norm_curv = false
//             - normalised curvature in [0,1] if norm_curv = true
//  - Component message shows curvature mode: "Curv: XY" or "Curv: 3D".
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
    public class wsp_Gc08_SpeedFromCurvature_Legacy : GH_Component
    {
        // cached version tag from WASPer_3DP assembly
        private readonly string _versionTag;

        public wsp_Gc08_SpeedFromCurvature_Legacy()
          : base(
                "wsp_Gc08_Speed from Curvature Legacy",
                "CurvSpeed",
                "Assigns a per-point printing speed [mm/min] based on the local curvature\n" +
                "of each toolpath polyline. Higher curvature -> lower speed, while straight\n" +
                "segments -> higher speed. The output tree matches the p_points\n" +
                "structure ({layer; curve}).",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "5.0_Gcode")
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = (v != null)
                ? $"v{v.Major}.{v.Minor}.{v.Build}"
                : "v1.0.x";

            this.Message = _versionTag;
        }

        // GUID for this component (same as before, since this is an internal upgrade)
        public override Guid ComponentGuid => new Guid("5E2B4A3E-3F55-4F53-B5E4-8D7C71D866C1");

        public override GH_Exposure Exposure => GH_Exposure.hidden;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    // Update resource path if you add a dedicated icon
                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Gc08_SpeedFromCurvature.png"))
                    {
                        return s != null ? new System.Drawing.Bitmap(s) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        #region IO
        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            // 0) p_points
            p.AddPointParameter(
                "p_points",
                "p_points",
                "Printing points organised as a DataTree {layer; curve}.\r\n" +
                "Each branch represents one toolpath polyline (at least 2 points).\r\n" +
                "Typically this is the output of wsp_Gc01_* (planar or non-planar).",
                GH_ParamAccess.tree);
            p[0].Optional = true;

            // 1) s_min (mm/min)
            p.AddNumberParameter(
                "s_min",
                "s_min",
                "Minimum printing speed [mm/min].\r\n" +
                "Used at the highest curvature regions (tightest bends).",
                GH_ParamAccess.item,
                600.0); // 10 mm/s default

            // 2) s_max (mm/min)
            p.AddNumberParameter(
                "s_max",
                "s_max",
                "Maximum printing speed [mm/min].\r\n" +
                "Used along straight or nearly straight segments.",
                GH_ParamAccess.item,
                3600.0); // 60 mm/s default

            // 3) curvature mode
            p.AddIntegerParameter(
                "curv_mode",
                "crv_mode",
                "Curvature evaluation mode:\r\n" +
                "0 = XY curvature (project segments to XY before computing angles).\r\n" +
                "    Recommended for mostly planar or gently non-planar prints.\r\n" +
                "1 = 3D curvature (use full 3D vectors).\r\n" +
                "    More sensitive to Z variations.",
                GH_ParamAccess.item,
                0);

            // 4) smooth_window
            p.AddIntegerParameter(
                "smooth_window",
                "smooth_w",
                "Half-window size for curvature smoothing in index space.\r\n" +
                "Example: 3 -> each curvature value is averaged over points [i-3..i+3].\r\n" +
                "0 or 1 -> no smoothing.",
                GH_ParamAccess.item,
                3);

            // 5) curv_percentile
            p.AddNumberParameter(
                "curv_percentile",
                "crv_pct",
                "Percentile used to define the \"maximum\" curvature for mapping.\r\n" +
                "Example: 0.95 -> use the 95th-percentile curvature as the high-curvature\n" +
                "reference. Anything higher is clamped so that a single spike does\n" +
                "not collapse the speed range.\r\n" +
                "Valid range: (0, 1].",
                GH_ParamAccess.item,
                0.95);

            // 6) norm_curv (optional)
            p.AddBooleanParameter(
                "norm_curv",
                "norm_curv",
                "If FALSE (default), the curvature output (crv) is the smoothed raw\n" +
                "curvature in physical units [rad/unit_length].\r\n" +
                "If TRUE, the curvature output is the normalised value in [0,1]\n" +
                "used internally to map curvature -> speed (0 = straight, 1 = high curvature).",
                GH_ParamAccess.item,
                false);
            p[6].Optional = true;

            p.AddGenericParameter(
                "wasper_path",
                "wsp_path",
                "Optional WASPer Print Path object. It supplies p_points when the legacy point tree is not connected.",
                GH_ParamAccess.item);
            p[7].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            // 0) speeds
            p.AddNumberParameter(
                "speeds",
                "speeds",
                "Per-point printing speed [mm/min].\r\n" +
                "Tree structure matches p_points ({layer; curve}).\r\n" +
                "Straight segments -> values near s_max.\r\n" +
                "Tight bends -> values near s_min.",
                GH_ParamAccess.tree);

            // 1) curvature
            p.AddNumberParameter(
                "curvature",
                "crv",
                "Local curvature per point.\r\n" +
                "If norm_curv = FALSE -> smoothed raw curvature [rad/unit_length].\r\n" +
                "If norm_curv = TRUE  -> normalised curvature in [0,1] used internally\n" +
                "                       for speed mapping.\r\n" +
                "Tree structure matches p_points ({layer; curve}).",
                GH_ParamAccess.tree);

            p.AddGenericParameter(
                "wasper_path",
                "wsp_path",
                "WASPer Print Path object carrying the analyzed points, inherited path metadata, " +
                "and the generated per-point print_speed values.",
                GH_ParamAccess.item);
        }
        #endregion

        #region SolveInstance
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // --- Inputs ---
            global::WASPer_3DP.WasperPrintPath packedPath = null;
            bool hasPackedPath = global::WASPer_3DP.WasperGcodeTreeUtil.TryGetPrintPath(DA, 7, out packedPath);

            GH_Structure<GH_Point> pTree;
            if (!DA.GetDataTree(0, out pTree) || pTree == null || pTree.PathCount == 0)
            {
                if (!hasPackedPath || packedPath.Points == null || packedPath.Points.BranchCount == 0)
                    return;
                pTree = global::WASPer_3DP.WasperGcodeTreeUtil.ToPointStructure(packedPath.Points);
            }

            double sMin = 0.0;
            double sMax = 0.0;
            int curvMode = 0;
            int smoothWindow = 3;
            double curvPercentile = 0.95;
            bool normCurv = false;

            if (!DA.GetData(1, ref sMin)) return;
            if (!DA.GetData(2, ref sMax)) return;
            if (!DA.GetData(3, ref curvMode)) return;
            if (!DA.GetData(4, ref smoothWindow)) return;
            if (!DA.GetData(5, ref curvPercentile)) return;
            DA.GetData(6, ref normCurv); // optional

            // Basic checks
            if (pTree == null || pTree.PathCount == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "No points provided in p_points.");
                return;
            }

            if (sMin <= 0.0 || sMax <= 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "s_min and s_max should be > 0. Resetting to defaults (600 / 3600 mm/min).");
                sMin = 600.0;
                sMax = 3600.0;
            }

            if (sMin > sMax)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"s_min ({sMin}) is greater than s_max ({sMax}). Values will be swapped.");
                double tmp = sMin;
                sMin = sMax;
                sMax = tmp;
            }

            if (curvPercentile <= 0.0 || curvPercentile > 1.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"curv_percentile = {curvPercentile} is out of (0,1]. Resetting to 0.95.");
                curvPercentile = 0.95;
            }

            if (curvMode != 0 && curvMode != 1)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"curv_mode = {curvMode} is invalid. Using 0 (XY curvature).");
                curvMode = 0;
            }

            // Update component message (version + mode label)
            string modeLabel = (curvMode == 0) ? "XY" : "3D";
            this.Message = $"{_versionTag}\nCurv: {modeLabel}";

            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
            double tinyLen = Math.Max(1e-9, tol * 1e-3);

            int branchCount = pTree.PathCount;
            IList<GH_Path> paths = pTree.Paths;

            // -----------------------------------------------------------------
            // 1) Extract plain point lists (sequential)
            // -----------------------------------------------------------------
            var pointBranches = new List<Point3d>[branchCount];
            for (int b = 0; b < branchCount; b++)
            {
                var path = paths[b];
                var branchRaw = pTree.get_Branch(path);
                var pts = new List<Point3d>();

                if (branchRaw != null)
                {
                    foreach (var obj in branchRaw)
                    {
                        var ghp = obj as GH_Point;
                        if (ghp == null) continue;
                        var p = ghp.Value;
                        if (!p.IsValid) continue;
                        pts.Add(p);
                    }
                }

                pointBranches[b] = pts;
            }

            // -----------------------------------------------------------------
            // 2) Per-branch curvature (raw)  -- Parallel
            // -----------------------------------------------------------------
            var kappaRawBranches = new double[branchCount][];
            var branchWarnings = new bool[branchCount]; // for "few points" warnings

            Parallel.For(0, branchCount, b =>
            {
                var pts = pointBranches[b];
                int n = (pts != null) ? pts.Count : 0;

                if (n == 0)
                {
                    kappaRawBranches[b] = new double[0];
                    return;
                }

                if (n < 2)
                {
                    // No real path, just one point
                    kappaRawBranches[b] = new double[n];
                    branchWarnings[b] = true;
                    return;
                }

                var kappa = new double[n];

                if (n == 2)
                {
                    // Single segment: treat as straight
                    kappa[0] = 0.0;
                    kappa[1] = 0.0;
                    kappaRawBranches[b] = kappa;
                    return;
                }

                // Interior points: i = 1..n-2
                for (int i = 1; i < n - 1; i++)
                {
                    Point3d pPrev = pts[i - 1];
                    Point3d p = pts[i];
                    Point3d pNext = pts[i + 1];

                    Vector3d v1 = p - pPrev;
                    Vector3d v2 = pNext - p;

                    if (curvMode == 0)
                    {
                        // XY mode
                        v1.Z = 0.0;
                        v2.Z = 0.0;
                    }

                    double len1 = v1.Length;
                    double len2 = v2.Length;

                    if (len1 < tinyLen || len2 < tinyLen)
                    {
                        kappa[i] = 0.0;
                        continue;
                    }

                    double dot = v1 * v2;
                    double denom = len1 * len2;
                    double cosT = (denom > 1e-18) ? dot / denom : 1.0;
                    cosT = Math.Max(-1.0, Math.Min(1.0, cosT));

                    double theta = Math.Acos(cosT); // radians
                    double s = 0.5 * (len1 + len2);
                    if (s < tinyLen) s = tinyLen;

                    double k = theta / s; // rad per unit length
                    kappa[i] = k;
                }

                // Endpoints: copy neighbour values for smoother behaviour
                kappa[0] = kappa[1];
                kappa[n - 1] = kappa[n - 2];

                kappaRawBranches[b] = kappa;
            });

            // Emit "few points" warnings on main thread
            for (int b = 0; b < branchCount; b++)
            {
                if (branchWarnings[b])
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"Branch {b} has fewer than 2 points. Curvature set to 0, speed = s_max.");
                }
            }

            // -----------------------------------------------------------------
            // 3) Optional smoothing (per branch) -- Parallel
            // -----------------------------------------------------------------
            var kappaSmoothedBranches = new double[branchCount][];
            int W = smoothWindow;
            if (W <= 0) W = 0;

            Parallel.For(0, branchCount, b =>
            {
                var kappa = kappaRawBranches[b];
                int n = kappa.Length;

                if (n == 0 || W <= 0)
                {
                    // No smoothing
                    kappaSmoothedBranches[b] = kappa;
                    return;
                }

                var smooth = new double[n];
                for (int i = 0; i < n; i++)
                {
                    int i0 = Math.Max(0, i - W);
                    int i1 = Math.Min(n - 1, i + W);

                    double sum = 0.0;
                    int count = 0;
                    for (int j = i0; j <= i1; j++)
                    {
                        sum += kappa[j];
                        count++;
                    }

                    smooth[i] = (count > 0) ? (sum / count) : kappa[i];
                }

                kappaSmoothedBranches[b] = smooth;
            });

            // -----------------------------------------------------------------
            // 4) Global percentile k_max (single-thread)
            // -----------------------------------------------------------------
            var allK = new List<double>();
            const double kEps = 1e-9;

            for (int b = 0; b < branchCount; b++)
            {
                var kappa = kappaSmoothedBranches[b];
                for (int i = 0; i < kappa.Length; i++)
                {
                    double k = kappa[i];
                    if (k > kEps)
                        allK.Add(k);
                }
            }

            double kMax = 0.0;
            bool hasCurvature = allK.Count > 0;

            if (hasCurvature)
            {
                allK.Sort();
                int idx = (int)Math.Floor(curvPercentile * (allK.Count - 1));
                idx = Math.Max(0, Math.Min(allK.Count - 1, idx));
                kMax = allK[idx];

                if (kMax <= kEps)
                    hasCurvature = false;
            }

            if (!hasCurvature)
            {
                // Everything is effectively straight: all speeds = s_max, curvature = 0
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "Curvature values are effectively zero. All speeds set to s_max.");

                var speedsTree = new GH_Structure<GH_Number>();
                var kappaTree = new GH_Structure<GH_Number>();

                for (int b = 0; b < branchCount; b++)
                {
                    var path = paths[b];
                    var pts = pointBranches[b];
                    int n = (pts != null) ? pts.Count : 0;

                    for (int i = 0; i < n; i++)
                    {
                        speedsTree.Append(new GH_Number(Math.Round(sMax)), path);
                        kappaTree.Append(new GH_Number(normCurv ? 0.0 : 0.0), path);
                    }
                }

                DA.SetDataTree(0, speedsTree);
                DA.SetDataTree(1, kappaTree);
                DA.SetData(2, new global::WASPer_3DP.WasperPrintPathGoo(
                    new global::WASPer_3DP.WasperPrintPath(
                        global::WASPer_3DP.WasperGcodeTreeUtil.ToPointTree(pTree),
                        hasPackedPath ? packedPath.PtPlanes : null,
                        hasPackedPath ? packedPath.Flows : null,
                        hasPackedPath ? packedPath.LayerH : null,
                        global::WASPer_3DP.WasperGcodeTreeUtil.ToDoubleTree(speedsTree),
                        packedPath?.PrintLoc, packedPath?.PrintGlob,
                        packedPath?.SupportPts, packedPath?.SupportVects,
                        packedPath?.Angles, packedPath?.ContactWidths,
                        packedPath?.RiskMaterial, packedPath?.RiskComb,
                        packedPath?.Load, packedPath?.Capacity, packedPath?.NozzleDiam,
                        packedPath?.DRatio, packedPath?.DLoaded, packedPath?.BendRatio,
                        packedPath?.SpanClass, packedPath?.SpanLen,
                        packedPath?.Collapsed, packedPath?.Cascade, packedPath?.CollapseGen,
                        packedPath?.LayerW, packedPath?.LayerWf, packedPath?.PrintVol,
                        packedPath?.Torn, packedPath?.InterfaceRatio,
                        packedPath?.OverturnRatio, packedPath?.FailureFlags)));
                return;
            }

            // -----------------------------------------------------------------
            // 5) Map curvature to speeds [mm/min] and normalised curvature (per branch) -- Parallel
            // -----------------------------------------------------------------
            var speedsBranches = new double[branchCount][];
            var kappaNormBranches = new double[branchCount][];
            double sLo = sMin;
            double sHi = sMax;

            Parallel.For(0, branchCount, b =>
            {
                var pts = pointBranches[b];
                var kappa = kappaSmoothedBranches[b];

                int n = (pts != null) ? pts.Count : 0;
                if (n == 0 || kappa.Length != n)
                {
                    speedsBranches[b] = new double[0];
                    kappaNormBranches[b] = new double[0];
                    return;
                }

                var speeds = new double[n];
                var kNorm = new double[n];

                for (int i = 0; i < n; i++)
                {
                    double k = kappa[i];
                    double c = (kMax > 0.0) ? (k / kMax) : 0.0;
                    if (c < 0.0) c = 0.0;
                    if (c > 1.0) c = 1.0;

                    double s = sHi - c * (sHi - sLo); // high curvature -> closer to sLo
                    double sRounded = Math.Round(s);    // <-- zero decimals

                    speeds[i] = Math.Round(s);   // returns e.g. 2487.0 but GH shows 2487
                    kNorm[i] = c;
                }

                speedsBranches[b] = speeds;
                kappaNormBranches[b] = kNorm;
            });

            // -----------------------------------------------------------------
            // 6) Emit to GH trees (single-thread)
            // -----------------------------------------------------------------
            var speedsOut = new GH_Structure<GH_Number>();
            var kappaOut = new GH_Structure<GH_Number>();

            for (int b = 0; b < branchCount; b++)
            {
                var path = paths[b];
                var pts = pointBranches[b];
                var kappa = kappaSmoothedBranches[b];
                var kNorm = kappaNormBranches[b];
                var speeds = speedsBranches[b];

                int n = (pts != null) ? pts.Count : 0;
                if (n == 0 || kappa.Length != n || speeds.Length != n || kNorm.Length != n)
                    continue;

                for (int i = 0; i < n; i++)
                {
                    speedsOut.Append(new GH_Number(speeds[i]), path);

                    double kOut = normCurv ? kNorm[i] : kappa[i];
                    kappaOut.Append(new GH_Number(kOut), path);
                }
            }

            DA.SetDataTree(0, speedsOut);
            DA.SetDataTree(1, kappaOut);
            DA.SetData(2, new global::WASPer_3DP.WasperPrintPathGoo(
                new global::WASPer_3DP.WasperPrintPath(
                    global::WASPer_3DP.WasperGcodeTreeUtil.ToPointTree(pTree),
                    hasPackedPath ? packedPath.PtPlanes : null,
                    hasPackedPath ? packedPath.Flows : null,
                    hasPackedPath ? packedPath.LayerH : null,
                    global::WASPer_3DP.WasperGcodeTreeUtil.ToDoubleTree(speedsOut),
                    packedPath?.PrintLoc, packedPath?.PrintGlob,
                    packedPath?.SupportPts, packedPath?.SupportVects,
                    packedPath?.Angles, packedPath?.ContactWidths,
                    packedPath?.RiskMaterial, packedPath?.RiskComb,
                    packedPath?.Load, packedPath?.Capacity, packedPath?.NozzleDiam,
                    packedPath?.DRatio, packedPath?.DLoaded, packedPath?.BendRatio,
                    packedPath?.SpanClass, packedPath?.SpanLen,
                    packedPath?.Collapsed, packedPath?.Cascade, packedPath?.CollapseGen,
                    packedPath?.LayerW, packedPath?.LayerWf, packedPath?.PrintVol,
                    packedPath?.Torn, packedPath?.InterfaceRatio,
                    packedPath?.OverturnRatio, packedPath?.FailureFlags)));
        }
        #endregion
    }
}
