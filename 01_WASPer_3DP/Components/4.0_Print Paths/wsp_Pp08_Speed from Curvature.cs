// -----------------------------------------------------------------------------
//  wsp_Pp08_Speed from Curvature  (Parallelised, with mode label & norm_curv)
//  -----------------------------------------------------------------------------
//  - Input geometry: canonical pt_planes origins {layer; curve}.
//  - Per branch:
//        * Compute local discrete curvature at each point.
//        * Optionally smooth curvature in index space (moving average).
//  - Global step:
//        * Collect curvature from all branches, compute a percentile-based k_max.
//  - Map curvature to speed [mm/min]:
//        * Straight segments (low curvature) -> s_max
//        * Tight bends (high curvature) -> s_min
//  - Outputs:
//        * speeds    (mm/min) per location, tree matches pt_planes.
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
    public class wsp_Pp08_Speed_From_Curvature : GH_Component
    {
        // cached version tag from WASPer_3DP assembly
        private readonly string _versionTag;

        public wsp_Pp08_Speed_From_Curvature()
          : base(
                "wsp_Pp08_Speed from Curvature",
                "CurvSpeed",
                "Assigns a per-location printing speed [mm/min] based on the local curvature\n" +
                "of each toolpath polyline. Higher curvature -> lower speed, while straight\n" +
                "segments -> higher speed. Path locations are derived from canonical pt_planes origins,\n" +
                "and the output tree matches the pt_planes\n" +
                "structure ({layer; curve}).\n\n" +
                "Please use the Pp01 WASPer Path from Curves before using this component.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "4.0_Print Paths")
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = (v != null)
                ? $"v{v.Major}.{v.Minor}.{v.Build}"
                : "v1.0.x";

            this.Message = _versionTag;
        }

        // GUID for this component (same as before, since this is an internal upgrade)
        public override Guid ComponentGuid => new Guid("A97E0375-3055-482C-BD17-5BC622F8C56E");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

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


        private int _visibleOutputsMask;
        private const string ShowAllOutputsKey = "wsp_gc08_show_all_outputs";
        private const string VisibleOutputsMaskKey = "wsp_gc08_visible_outputs_mask";

        private static readonly string[] OutputCatalog =
            WasperPathDebugOutputs.CoreNickNames.Concat(new[] { "crv" }).ToArray();
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
                Params.RegisterOutputParam(new Param_GenericObject { Name = "wsp_path", NickName = "wsp_path", Description = "WASPer Print Path carrying generated print_speed values.", Access = GH_ParamAccess.item });
                Params.RegisterOutputParam(new Param_String { Name = "summary", NickName = "summary", Description = "Speed-from-curvature summary.", Access = GH_ParamAccess.item });
            }
            WasperPathDebugOutputs.RegisterCore(this, IsOutputVisible);
            if (IsOutputVisible("crv"))
                Params.RegisterOutputParam(new Param_Number { Name = "curvature", NickName = "crv", Description = "Raw or normalized curvature.", Access = GH_ParamAccess.tree });
            Params.OnParametersChanged();
        }

        #region IO
        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "WASPer Print Path object. Its canonical point-plane origins supply the path geometry; output stores generated print_speed values back into wsp_path. Please use the Pp01 WASPer Path from Curves before using this component.",
                GH_ParamAccess.item);

            p.AddNumberParameter("s_min", "s_min", "Minimum printing speed [mm/min]. Used at highest curvature regions.", GH_ParamAccess.item, 600.0);
            p.AddNumberParameter("s_max", "s_max", "Maximum printing speed [mm/min]. Used along straight or nearly straight segments.", GH_ParamAccess.item, 3600.0);
            p.AddIntegerParameter("curv_mode", "crv_mode", "0 = XY curvature; 1 = 3D curvature.", GH_ParamAccess.item, 0);
            p.AddIntegerParameter("smooth_window", "smooth_w", "Half-window size for curvature smoothing in index space. 0 or 1 -> no smoothing.", GH_ParamAccess.item, 3);
            p.AddNumberParameter("curv_percentile", "crv_pct", "Percentile used to define maximum curvature for mapping. Valid range: (0, 1].", GH_ParamAccess.item, 0.95);
            p.AddBooleanParameter("norm_curv", "norm_curv", "If false, curvature debug output is raw [rad/unit_length]. If true, it is normalized [0,1].", GH_ParamAccess.item, false);
            p[6].Optional = true;
            p.AddParameter(global::WASPer_3DP.WasperTargetRolesParam.Create(
                "Selects which semantic path branches receive curvature-based print_speed. 0 = All " +
                "paths (default), 1 = Shell, 2 = Infill, 3 = Partition, 4 = Support, 5 = Transition, 6 = Undefined. Supply several role-specific " +
                "values to include them and exclude the others. All paths (0) cannot be combined. " +
                "Non-target speeds are preserved or remain unset."));
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("wsp_path", "wsp_path", "WASPer Print Path object carrying generated print_speed values.", GH_ParamAccess.item);
            p.AddTextParameter("summary", "summary", "Speed-from-curvature summary.", GH_ParamAccess.item);
            // Optional debug outputs are added dynamically by RebuildOutputs(), based on the
            // persisted _visibleOutputsMask. RegisterOutputParams() runs once at construction,
            // before Read() has restored any persisted state, so a mask-gated branch here would
            // never fire.
        }
        #endregion

        #region SolveInstance
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // --- Inputs ---
            global::WASPer_3DP.WasperPrintPath packedPath = null;
            bool hasPackedPath = global::WASPer_3DP.WasperGcodeTreeUtil.TryGetPrintPath(DA, 0, out packedPath);
            if (!hasPackedPath || packedPath == null || packedPath.Points == null || packedPath.Points.BranchCount == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Pp08 requires a valid wsp_path input. Please use the Pp01 WASPer Path from Curves before using this component.");
                return;
            }

            GH_Structure<GH_Point> pTree = global::WASPer_3DP.WasperGcodeTreeUtil.ToPointStructure(packedPath.Points);

            double sMin = 0.0;
            double sMax = 0.0;
            int curvMode = 0;
            int smoothWindow = 3;
            double curvPercentile = 0.95;
            bool normCurv = false;
            var targetRoles = new List<int>();

            if (!DA.GetData(1, ref sMin)) return;
            if (!DA.GetData(2, ref sMax)) return;
            if (!DA.GetData(3, ref curvMode)) return;
            if (!DA.GetData(4, ref smoothWindow)) return;
            if (!DA.GetData(5, ref curvPercentile)) return;
            DA.GetData(6, ref normCurv);
            DA.GetDataList(7, targetRoles);

            if (!global::WASPer_3DP.WasperGcodeTreeUtil.TryNormalizeTargetRoles(
                    targetRoles,
                    out targetRoles,
                    out string roleError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, roleError);
                return;
            }

            // Basic checks
            if (pTree == null || pTree.PathCount == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "No path locations found in pt_planes origins.");
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
            var targetBranches = new bool[branchCount];
            int targetedBranchCount = 0;
            for (int b = 0; b < branchCount; b++)
            {
                targetBranches[b] =
                    global::WASPer_3DP.WasperGcodeTreeUtil.MatchesTargetRoles(
                        packedPath.PathRoles,
                        paths[b],
                        targetRoles);
                if (targetBranches[b])
                    targetedBranchCount++;
            }

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

                if (!targetBranches[b])
                {
                    kappaRawBranches[b] = new double[n];
                    return;
                }

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
                if (targetBranches[b] && branchWarnings[b])
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
                if (!targetBranches[b])
                    continue;
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

                    if (!targetBranches[b])
                    {
                        AppendPreservedSpeedBranch(speedsTree, packedPath.PrintSpeed, path);
                        continue;
                    }

                    for (int i = 0; i < n; i++)
                    {
                        speedsTree.Append(new GH_Number(Math.Round(sMax)), path);
                        kappaTree.Append(new GH_Number(normCurv ? 0.0 : 0.0), path);
                    }
                }

                var straightPath = new global::WASPer_3DP.WasperPrintPath(
                    global::WASPer_3DP.WasperGcodeTreeUtil.ToPointTree(pTree),
                    packedPath.PtPlanes,
                    packedPath.Flows,
                    packedPath.LayerH,
                    global::WASPer_3DP.WasperGcodeTreeUtil.ToDoubleTree(speedsTree),
                    packedPath.PrintLoc, packedPath.PrintGlob,
                    packedPath.SupportPts, packedPath.SupportVects,
                    packedPath.Angles, packedPath.ContactWidths,
                    packedPath.RiskMaterial, packedPath.RiskComb,
                    packedPath.Load, packedPath.Capacity, packedPath.NozzleDiam,
                    packedPath.DRatio, packedPath.DLoaded, packedPath.BendRatio,
                    packedPath.SpanClass, packedPath.SpanLen,
                    packedPath.Collapsed, packedPath.Cascade, packedPath.CollapseGen,
                    packedPath.LayerW, packedPath.LayerWf, packedPath.PrintVol,
                    packedPath.Torn, packedPath.InterfaceRatio,
                    packedPath.OverturnRatio, packedPath.FailureFlags,
                    pathRoles: packedPath.PathRoles,
                    layerPlanes: packedPath.LayerPlanes,
                    strokeIds: packedPath.StrokeIds,
                    hasCrossLayerShellContinuity: packedPath.HasCrossLayerShellContinuity);
                DA.SetData(0, new global::WASPer_3DP.WasperPrintPathGoo(straightPath));
                DA.SetData(
                    1,
                    $"OK | plane locations={pTree.DataCount} | branches={pTree.PathCount} | " +
                    $"target_roles={WASPer_3DP.WasperGcodeTreeUtil.TargetRoleNames(targetRoles)} | " +
                    $"targeted_branches={targetedBranchCount} | targeted speeds=s_max ({sMax:0.###})");
                WasperPathDebugOutputs.SetCore(DA, this, straightPath);
                int curvatureIndex =
                    WasperPathDebugOutputs.OutputIndex(this, "crv");
                if (curvatureIndex >= 0)
                    DA.SetDataTree(curvatureIndex, kappaTree);
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
                if (!targetBranches[b])
                {
                    speedsBranches[b] = new double[0];
                    kappaNormBranches[b] = new double[0];
                    return;
                }
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
                if (!targetBranches[b])
                {
                    AppendPreservedSpeedBranch(speedsOut, packedPath.PrintSpeed, path);
                    continue;
                }
                if (n == 0 || kappa.Length != n || speeds.Length != n || kNorm.Length != n)
                    continue;

                for (int i = 0; i < n; i++)
                {
                    speedsOut.Append(new GH_Number(speeds[i]), path);

                    double kOut = normCurv ? kNorm[i] : kappa[i];
                    kappaOut.Append(new GH_Number(kOut), path);
                }
            }

            var outPath = new global::WASPer_3DP.WasperPrintPath(
                global::WASPer_3DP.WasperGcodeTreeUtil.ToPointTree(pTree),
                packedPath.PtPlanes,
                packedPath.Flows,
                packedPath.LayerH,
                global::WASPer_3DP.WasperGcodeTreeUtil.ToDoubleTree(speedsOut),
                packedPath.PrintLoc, packedPath.PrintGlob,
                packedPath.SupportPts, packedPath.SupportVects,
                packedPath.Angles, packedPath.ContactWidths,
                packedPath.RiskMaterial, packedPath.RiskComb,
                packedPath.Load, packedPath.Capacity, packedPath.NozzleDiam,
                packedPath.DRatio, packedPath.DLoaded, packedPath.BendRatio,
                packedPath.SpanClass, packedPath.SpanLen,
                packedPath.Collapsed, packedPath.Cascade, packedPath.CollapseGen,
                packedPath.LayerW, packedPath.LayerWf, packedPath.PrintVol,
                packedPath.Torn, packedPath.InterfaceRatio,
                packedPath.OverturnRatio, packedPath.FailureFlags,
                pathRoles: packedPath.PathRoles,
                layerPlanes: packedPath.LayerPlanes,
                strokeIds: packedPath.StrokeIds,
                hasCrossLayerShellContinuity: packedPath.HasCrossLayerShellContinuity);
            DA.SetData(0, new global::WASPer_3DP.WasperPrintPathGoo(outPath));
            DA.SetData(
                1,
                $"OK | plane locations={pTree.DataCount} | branches={pTree.PathCount} | " +
                $"target_roles={WASPer_3DP.WasperGcodeTreeUtil.TargetRoleNames(targetRoles)} | " +
                $"targeted_branches={targetedBranchCount} | speed=[{sMin:0.###}, {sMax:0.###}] | Curv: {modeLabel}");
            WasperPathDebugOutputs.SetCore(DA, this, outPath);
            int curvatureOutputIndex =
                WasperPathDebugOutputs.OutputIndex(this, "crv");
            if (curvatureOutputIndex >= 0)
                DA.SetDataTree(curvatureOutputIndex, kappaOut);
        }

        private static void AppendPreservedSpeedBranch(
            GH_Structure<GH_Number> output,
            DataTree<double> source,
            GH_Path path)
        {
            if (output == null || source == null || path == null ||
                !source.PathExists(path))
                return;

            IList<double> values = source.Branch(path);
            if (values == null)
                return;

            foreach (double value in values)
                output.Append(new GH_Number(value), path);
        }
        #endregion
    }
}
