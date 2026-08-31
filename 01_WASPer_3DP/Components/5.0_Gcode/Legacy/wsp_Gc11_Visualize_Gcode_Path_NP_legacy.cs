#region Component Description
/*
    Component Name:
      wsp_Gc11_Visualize Gcode Path (Bead, Parallel, Non-Planar)

    Description:
      Visualizes planar or non-planar 3D-printing G-code paths as solid
      filament meshes.

      The component:
        - Accepts printing points p_points in a tree {layer; curve}.
        - Optionally accepts flow multipliers per point in a matching tree.
        - Optionally accepts per-point filament heights (layer_h tree).
        - Optionally accepts per-point planes (point_planes tree)
          to support non-planar toolpaths.

        - For every point i in a toolpath, the local bead **width** is computed as:
              
              width_i = layer_w * flux[i]

          where:
            * layer_w is the base filament width input.
            * flux[i] is the flow multiplier assigned to point i
              (flux[0] is ignored intentionally).
            * If flow data is missing, mismatched, or empty, a default list is used:
                  flux[0] = 0
                  flux[i>0] = 1

          This scaling reproduces how real G-code flow/pressure variation changes
          the *effective extrusion width*, and allows visualizing widening or
          narrowing tracks along the print.

        - At each point the component builds a rounded-square (superellipse)
          bead cross-section:
             width  = layer_w * flux[i]    (perpendicular to the path)
             height = layer_h[i]           (along the local height axis)

          The bead body is always generated *below* the path, along -heightAxis.

        - All cross-sections are lofted into a closed mesh per path.
        - Closed toolpaths (first point ˜ last point) are detected automatically
          and lofted as a seamless ring (no end caps, last section wraps to first).
        - All branches are processed in parallel for high performance.

      Geometry assumptions:
        - Input points represent the toolpath centerline.
        - The filament is modelled as a rounded-square bead.
        - No material is placed above the path (the path lies at the bead top).

    Inputs:
      - p_points (Tree<Point3d>):
          Printing points organised as {layer; curve}. Each branch must
          contain at least 2 points.

      - flows (Tree<double>):
          Flow multipliers per point, matching p_points.
          Used to compute bead width as:
              
              width_i = layer_w * flux[i]
          
          Rules:
            * flux[0] is always ignored.
            * Segment [i-1 ? i] uses flow[i].
            * If missing or mismatched:
                  flux[0] = 0
                  flux[i>0] = 1

      - layer_h (Tree<double>):
          Filament height per point.
          Per-branch behavior:
            * match count ? use per-point height
            * single value ? replicated to all points
            * mismatch/missing ? defaults to height = 1.0

      - layer_w (Double):
          Base filament width.
          A scalar value applied to every point, but **modulated point-by-point**
          using the flow multipliers:
              
              width_i = layer_w * flux[i]

          Must be > 0.

      - point_planes (Tree<Plane>):
          Optional local point planes. Plane Z is used as the local height axis.
          If missing or mismatched, the component falls back to global Z.
          A warning is shown only if the user supplied a tree but sizes mismatch.

      - high_res (Boolean):
          TRUE  ? high-resolution bead cross-section  
          FALSE ? coarse profile (faster)

      - sim_path (Generic):
          Either global path simulation progress from 0.0 to 1.0, or the
          Program (P) output from Robots Program Simulation. Program target
          indices and coordinates are matched to wsp_path points.

    Outputs:
      - gcode_mesh (Tree<Mesh>):
          One closed filament mesh per {layer; curve} branch.

      - dbg_paths (Tree<Curve>):
          PolylineCurve of the toolpath.

      - dbg_profiles (Tree<Curve>):
          Cross-section profiles at each point.
*/
#endregion


#region Usings
using System;
using System.Collections;               // for non-generic IList
using System.Collections.Generic;
using System.Threading.Tasks;

using Rhino;
using Rhino.Geometry;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
#endregion

namespace WASPer_3DP.Components._5_0_Gcode
{
    public class wsp_Gc11_Visualize_Gcode_Path_NP : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Gc11_Visualize_Gcode_Path_NP()
          : base(
                "wsp_Gc11_Visualize Path",
                "GcPath Vis",
                "Visualizes (non-)planar G-code printing paths as solid filament meshes using flow-scaled rounded-square bead profiles below each path. \n" +
                "Supports per-point flow multipliers, heights, and point planes. This allows the visualization of NON-HOMOGENEOUS and/or NON-PLANAR printing paths.\n" +
                "Closed toolpaths (first ˜ last point) are automatically detected and lofted as seamless rings. sim_path previews global print progress.\n",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "5.0_Gcode")
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
            get { return new Guid("F0B3E4A4-1B47-4D96-9D19-9EFD48A7DA8E"); }
        }

        public override GH_Exposure Exposure => GH_Exposure.hidden;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Gc11_Visualize_Gcode_Path_and_Fluxes.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        // --------------------------------------------------------------------
        // IO
        // --------------------------------------------------------------------
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            // 0) points
            pManager.AddPointParameter(
                "p_points",
                "p_points",
                "Printing points organised as {layer; curve}. Each branch defines one toolpath polyline.",
                GH_ParamAccess.tree);

            // 1) point_planes (tree, OPTIONAL)
            pManager.AddPlaneParameter(
                "point_planes",
                "pt_planes",
                "Optional point planes per point. Plane Z is used as local +Z. If missing or mismatched, the global Z-axis is used and a warning is shown.",
                GH_ParamAccess.tree);
            pManager[1].Optional = true;

            // 2) flows
            pManager.AddNumberParameter(
                "flows",
                "flows",
                "Flow multipliers per point, matching p_points. Used with nominal layer_w and layer_h to estimate flow-adjusted LayerWf.",
                GH_ParamAccess.tree);
            pManager[2].Optional = true;

            // 3) layer_h as TREE (per-point)
            pManager.AddNumberParameter(
                "layer_h",
                "layer_h",
                "Filament height per point [model units], matching the p_points tree. If empty, a default value of 1.0 is used.",
                GH_ParamAccess.tree);
            pManager[3].Optional = true;

            // 4) layer_w (item)
            pManager.AddNumberParameter(
                "layer_w",
                "layer_w",
                "Optional nominal/base bead width before flow adjustment, in model units. If connected, it overrides wsp_path.LayerW; otherwise the incoming path value is preserved when available. If neither is available, defaults to layer_h * 2.5. The outgoing wsp_path stores this nominal width as LayerW, estimates LayerWf by scaling the bead cross-sectional area with local flow and recovering the equivalent deposited width from layer_h, and updates per-segment PrintVol.",
                GH_ParamAccess.item,
                0.0);

            // 5) high_res
            pManager.AddBooleanParameter(
                "high_res",
                "hi_res",
                "If TRUE, outputs a high-resolution bead mesh. If FALSE, uses a coarser cross-section for faster computation.",
                GH_ParamAccess.item,
                false);

            // 6) sim_path
            pManager.AddGenericParameter(
                "sim_path",
                "sim",
                "Either global path progress from 0.0 to 1.0, or the Program (P) output from Robots Program Simulation. Program targets are matched in order to wsp_path points, so extra home, approach, travel, and hop targets do not shift deposition progress.",
                GH_ParamAccess.item);
            pManager[6].Optional = true;

            pManager.AddGenericParameter(
                "wasper_path",
                "wsp_path",
                "Optional WASPer Print Path object. Explicit legacy trees override its corresponding fields.",
                GH_ParamAccess.item);
            pManager[7].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "gcode_mesh",
                "g_mesh",
                "Closed filament meshes with the same tree structure as the input points.",
                GH_ParamAccess.tree);

            pManager.AddCurveParameter(
                "dbg_paths",
                "paths",
                "Reconstructed toolpath polylines for debugging (PolylineCurve).",
                GH_ParamAccess.tree);

            pManager.AddCurveParameter(
                "dbg_profiles",
                "profiles",
                "Bead cross-section profiles for debugging (PolylineCurve), one per point.",
                GH_ParamAccess.tree);

            pManager.AddGenericParameter(
                "wasper_path",
                "wsp_path",
                "WASPer Print Path object carrying the visualized points and inherited path metadata.",
                GH_ParamAccess.item);
        }

        // --------------------------------------------------------------------
        // Main solve
        // --------------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            this.Message = _versionTag;

            global::WASPer_3DP.WasperPrintPath packedPath = null;
            bool hasPackedPath = global::WASPer_3DP.WasperGcodeTreeUtil.TryGetPrintPath(DA, 7, out packedPath);

            // --- Inputs ---
            // 0) p_points
            GH_Structure<GH_Point> ghPoints;
            if (!DA.GetDataTree(0, out ghPoints) || ghPoints == null || ghPoints.PathCount == 0)
            {
                if (!hasPackedPath || packedPath.Points == null || packedPath.Points.BranchCount == 0) return;
                ghPoints = global::WASPer_3DP.WasperGcodeTreeUtil.ToPointStructure(packedPath.Points);
            }

            // 1) point_planes (OPTIONAL)
            GH_Structure<GH_Plane> ghPointPlanes;
            bool hasPointPlaneTree = DA.GetDataTree(1, out ghPointPlanes) && ghPointPlanes != null && ghPointPlanes.PathCount > 0;
            if (!hasPointPlaneTree && hasPackedPath && packedPath.HasPlanes)
            {
                ghPointPlanes = global::WASPer_3DP.WasperGcodeTreeUtil.ToPlaneStructure(packedPath.PtPlanes);
                hasPointPlaneTree = ghPointPlanes != null && ghPointPlanes.PathCount > 0;
            }

            // 2) flows
            GH_Structure<GH_Number> ghFlows;
            bool hasFluxTree = DA.GetDataTree(2, out ghFlows) && ghFlows != null && ghFlows.PathCount > 0;
            if (!hasFluxTree && hasPackedPath && packedPath.HasFlows)
            {
                ghFlows = global::WASPer_3DP.WasperGcodeTreeUtil.ToNumberStructure(packedPath.Flows);
                hasFluxTree = ghFlows != null && ghFlows.PathCount > 0;
            }

            // 3) layer_h
            GH_Structure<GH_Number> ghHeights;
            bool hasHeightTree = DA.GetDataTree(3, out ghHeights) && ghHeights != null && ghHeights.PathCount > 0;
            if (!hasHeightTree && hasPackedPath && packedPath.HasLayerH)
            {
                ghHeights = global::WASPer_3DP.WasperGcodeTreeUtil.ToNumberStructure(packedPath.LayerH);
                hasHeightTree = ghHeights != null && ghHeights.PathCount > 0;
            }

            // 4) layer_w
            double layer_w = 0.0;
            bool explicitLayerW = Params.Input[4].SourceCount > 0 && DA.GetData(4, ref layer_w);

            // 5) high_res
            bool highRes = false;
            DA.GetData(5, ref highRes);

            // 6) sim_path
            double simPath = 1.0;
            global::WASPer_3DP.WasperRobotProgramAdapter robotProgram;
            string simError;
            if (!global::WASPer_3DP.WasperGcodeTreeUtil.TryGetSimulationInput(
                DA, 6, out simPath, out robotProgram, out simError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, simError);
                return;
            }
            bool simFromRobotProgram = robotProgram != null;

            int sectionSegs = highRes ? 24 : 10;
            const double defaultHeight = 1.0;

            double tol = RhinoDoc.ActiveDoc != null
                ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
                : RhinoMath.SqrtEpsilon;

            double fallbackHeight = RepresentativeLayerHeight(ghHeights, defaultHeight, tol);
            if (!explicitLayerW && hasPackedPath && packedPath.HasLayerW)
                layer_w = RepresentativeLayerWidth(global::WASPer_3DP.WasperGcodeTreeUtil.ToNumberStructure(packedPath.LayerW), 0.0, tol);
            if (explicitLayerW && hasPackedPath && packedPath.HasLayerW)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Override applied: explicit layer_w replaced wsp_path.LayerW; layer_wf and print_vol were recomputed.");

            if (layer_w <= tol)
                layer_w = Math.Max(tol * 10.0, fallbackHeight * 2.5);

            int branchCount = ghPoints.PathCount;
            if (branchCount == 0)
            {
                DA.SetDataTree(0, new GH_Structure<GH_Mesh>());
                DA.SetDataTree(1, new GH_Structure<GH_Curve>());
                DA.SetDataTree(2, new GH_Structure<GH_Curve>());
                return;
            }

            // Copy data into plain structures (thread-safe later)
            IList<GH_Path> paths = ghPoints.Paths;

            var pointBranches = new List<List<Point3d>>(branchCount);
            var fluxBranchesRaw = new List<List<double>>(branchCount);
            var heightBranchesRaw = new List<List<double>>(branchCount);
            var pointPlaneBranchesRaw = new List<List<Plane>>(branchCount);

            bool[] branchUsesDefaultPlane = new bool[branchCount];
            bool anyDefaultPlane = false;
            int fluxIndexFallbacks = 0;
            int heightIndexFallbacks = 0;
            int planeIndexFallbacks = 0;
            int invalidPlaneNormalCount = 0;
            int downwardPlaneNormalCount = 0;
            int inconsistentPlaneNormalBranches = 0;

            for (int b = 0; b < branchCount; b++)
            {
                GH_Path path = paths[b];

                // Points
                IList ptBranchRaw = ghPoints.get_Branch(path);
                var ptList = new List<Point3d>();
                if (ptBranchRaw != null)
                {
                    foreach (object goo in ptBranchRaw)
                    {
                        GH_Point ghp = goo as GH_Point;
                        if (ghp == null) continue;
                        Point3d p = ghp.Value;
                        if (!p.IsValid) continue;
                        ptList.Add(p);
                    }
                }
                pointBranches.Add(ptList);
                int nPts = ptList.Count;

                // Flows
                List<double> fluxList = null;
                if (hasFluxTree)
                {
                    bool usedIndexFallback;
                    IList flBranchRaw = GetMatchingBranch(ghFlows, path, b, out usedIndexFallback);
                    if (usedIndexFallback) fluxIndexFallbacks++;
                    if (flBranchRaw != null)
                    {
                        fluxList = new List<double>();
                        foreach (object goo in flBranchRaw)
                        {
                            GH_Number ghn = goo as GH_Number;
                            if (ghn == null) continue;
                            fluxList.Add(ghn.Value);
                        }
                    }
                }
                fluxBranchesRaw.Add(fluxList);

                // Heights
                List<double> hList = null;

                // If the user wired a single number (flat tree, one path, one item)
                // promote it to a global scalar so every branch receives it.
                double? globalHeightScalar = null;
                if (hasHeightTree && ghHeights.PathCount == 1)
                {
                    IList onlyBranch = ghHeights.get_Branch(ghHeights.Paths[0]);
                    if (onlyBranch != null && onlyBranch.Count == 1)
                    {
                        GH_Number ghn = onlyBranch[0] as GH_Number;
                        if (ghn != null) globalHeightScalar = ghn.Value;
                    }
                }

                if (globalHeightScalar.HasValue)
                {
                    // Single scalar ? replicate for every point in this branch
                    hList = new List<double>(nPts);
                    for (int i = 0; i < nPts; i++)
                        hList.Add(globalHeightScalar.Value);
                }
                else if (hasHeightTree && b < ghHeights.PathCount)
                {
                    bool usedIndexFallback;
                    IList hBranchRaw = GetMatchingBranch(ghHeights, path, b, out usedIndexFallback);
                    if (usedIndexFallback) heightIndexFallbacks++;
                    if (hBranchRaw != null)
                    {
                        hList = new List<double>();
                        foreach (object goo in hBranchRaw)
                        {
                            GH_Number ghn = goo as GH_Number;
                            if (ghn == null) continue;
                            hList.Add(ghn.Value);
                        }
                    }
                }
                heightBranchesRaw.Add(hList);

                // Point planes
                List<Plane> planeList = null;
                if (hasPointPlaneTree)
                {
                    bool usedIndexFallback;
                    IList planeBranchRaw = GetMatchingBranch(ghPointPlanes, path, b, out usedIndexFallback);
                    if (usedIndexFallback) planeIndexFallbacks++;
                    if (planeBranchRaw != null)
                    {
                        planeList = new List<Plane>();
                        foreach (object goo in planeBranchRaw)
                        {
                            GH_Plane ghp = goo as GH_Plane;
                            if (ghp == null) continue;
                            planeList.Add(ghp.Value);
                        }
                    }
                }

                // Check if point planes are usable; if not, mark fallback
                if (hasPointPlaneTree && planeList != null && planeList.Count == nPts)
                {
                    branchUsesDefaultPlane[b] = false;
                    InspectPlaneNormals(
                        planeList,
                        tol,
                        ref invalidPlaneNormalCount,
                        ref downwardPlaneNormalCount,
                        ref inconsistentPlaneNormalBranches);
                }
                else
                {
                    if (hasPointPlaneTree)
                    {
                        branchUsesDefaultPlane[b] = true;
                        anyDefaultPlane = true;
                    }
                    planeList = null;
                }
                pointPlaneBranchesRaw.Add(planeList);
            }

            if (anyDefaultPlane)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "point_planes not supplied or size-mismatched for one or more branches. Falling back to global Z-axis for those points.");
            }

            if (invalidPlaneNormalCount > 0 || downwardPlaneNormalCount > 0 || inconsistentPlaneNormalBranches > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"Suspicious point_planes normals detected: invalid={invalidPlaneNormalCount}, " +
                    $"downward={downwardPlaneNormalCount}, inconsistent_branches={inconsistentPlaneNormalBranches}. " +
                    "Gc11 uses plane Z as the local layer/height direction; inverted normals can flip bead visualization or create apparent gaps.");
            }

            if (fluxIndexFallbacks > 0 || heightIndexFallbacks > 0 || planeIndexFallbacks > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"Some auxiliary branches did not have matching paths and were matched by branch index instead. " +
                    $"flows={fluxIndexFallbacks}, layer_h={heightIndexFallbacks}, point_planes={planeIndexFallbacks}. " +
                    "If layers look wrong, graft/simplify the auxiliary trees so their paths match p_points.");
            }

            int totalPointCount = 0;
            for (int b = 0; b < branchCount; b++)
            {
                if (pointBranches[b] != null)
                    totalPointCount += pointBranches[b].Count;
            }

            global::WASPer_3DP.WasperRobotSimulationCut robotCut = null;
            int selectedPointCount;
            if (robotProgram != null)
            {
                string mappingError;
                if (global::WASPer_3DP.WasperGcodeTreeUtil.TryGetRobotSimulationCut(
                    robotProgram, pointBranches, tol, out robotCut, out mappingError))
                {
                    selectedPointCount = robotCut.CompletedPointCount;
                    simPath = robotCut.Progress;
                    if (robotCut.MatchedPointCount < robotCut.TotalPointCount)
                    {
                        AddRuntimeMessage(
                            GH_RuntimeMessageLevel.Warning,
                            $"Robots simulation matched {robotCut.MatchedPointCount}/{robotCut.TotalPointCount} " +
                            "ordered wsp_path points. Progress is reliable through the matched prefix; " +
                            "verify that the same path and point order generated the program.");
                    }
                }
                else
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        mappingError + " Falling back to normalized program time.");
                    selectedPointCount = simPath >= 1.0
                        ? totalPointCount
                        : (int)Math.Floor(simPath * totalPointCount);
                }
            }
            else
            {
                selectedPointCount = simPath >= 1.0
                    ? totalPointCount
                    : (int)Math.Floor(simPath * totalPointCount);
            }
            if (selectedPointCount < 0) selectedPointCount = 0;
            if (selectedPointCount > totalPointCount) selectedPointCount = totalPointCount;

            if (robotCut != null)
            {
                ApplyRobotSimulationCut(
                    pointBranches,
                    fluxBranchesRaw,
                    heightBranchesRaw,
                    pointPlaneBranchesRaw,
                    robotCut,
                    tol);
            }
            else if (selectedPointCount < totalPointCount)
            {
                ApplyGlobalSimulationTrim(
                    pointBranches,
                    fluxBranchesRaw,
                    heightBranchesRaw,
                    pointPlaneBranchesRaw,
                    selectedPointCount);
            }

            this.Message = simPath >= 1.0
                ? _versionTag
                : $"{_versionTag} | {(simFromRobotProgram ? "robot " : "sim ")}{simPath:0.##}";

            // ----------------------------------------------------------------
            // Parallel per-branch processing
            // ----------------------------------------------------------------
            Mesh[] branchMeshes = new Mesh[branchCount];
            Polyline[] branchPaths = new Polyline[branchCount];
            List<Polyline>[] branchProfiles = new List<Polyline>[branchCount];
            bool[] skippedShortBranch = new bool[branchCount];
            bool[] failedMeshBranch = new bool[branchCount];
            int[] invalidFluxSections = new int[branchCount];
            int[] invalidHeightSections = new int[branchCount];
            int[] missingProfileSections = new int[branchCount];

            Vector3d worldZ = Vector3d.ZAxis;

            Parallel.For(0, branchCount, b =>
            {
                List<Point3d> pts = pointBranches[b];
                if (pts == null || pts.Count < 2)
                {
                    skippedShortBranch[b] = true;
                    return;
                }

                int nPts = pts.Count;

                // Closed source curves often arrive from Gc01 without a duplicated
                // final point, so duplicate-endpoint tolerance is too strict here.
                // Use bead-width proximity to restore seamless visualization.
                double closeTol = Math.Max(tol * 10.0, layer_w * 0.8);
                bool isClosed = nPts > 3 && pts[0].DistanceTo(pts[nPts - 1]) <= closeTol;

                List<Point3d> workPts;   // points actually used for section generation
                if (isClosed && nPts > 2)
                {
                    // Remove the duplicate closing point; the loft wraps back itself.
                    workPts = new List<Point3d>(pts);
                    workPts.RemoveAt(workPts.Count - 1);
                }
                else
                {
                    workPts = pts;
                    isClosed = false; // can't close with only 2 unique points
                }

                int nWork = workPts.Count;

                // Path polyline for debug (keep original pts so curve shows closed)
                Polyline pathPoly = new Polyline(pts);
                branchPaths[b] = pathPoly;

                // Flux branch (or default) — sized to nPts (original)
                List<double> fluxBranch = fluxBranchesRaw[b];
                if (fluxBranch == null || fluxBranch.Count != nPts)
                {
                    fluxBranch = new List<double>(nPts);
                    fluxBranch.Add(0.0);
                    for (int i = 1; i < nPts; i++)
                        fluxBranch.Add(1.0);
                }

                // Height per point (or default) — sized to nPts
                List<double> hRaw = heightBranchesRaw[b];
                double[] heights = new double[nPts];
                if (hRaw != null)
                {
                    if (hRaw.Count == nPts)
                    {
                        for (int i = 0; i < nPts; i++)
                            heights[i] = hRaw[i];
                    }
                    else if (hRaw.Count == 1)
                    {
                        double hh = hRaw[0];
                        for (int i = 0; i < nPts; i++)
                            heights[i] = hh;
                    }
                    else
                    {
                        for (int i = 0; i < nPts; i++)
                            heights[i] = defaultHeight;
                    }
                }
                else
                {
                    for (int i = 0; i < nPts; i++)
                        heights[i] = defaultHeight;
                }

                // Point planes per point (may be null ? fallback)
                List<Plane> planeBranch = pointPlaneBranchesRaw[b];
                bool hasPlanesForBranch = planeBranch != null && planeBranch.Count == nPts;

                // Build sections — iterate over workPts
                List<Polyline> sections = new List<Polyline>();
                branchProfiles[b] = sections;
                Vector3d lastTangent = Vector3d.XAxis;

                for (int i = 0; i < nWork; i++)
                {
                    Point3d pt = workPts[i];

                    // ----------------------------------------------------------
                    // Tangent — wrap-around aware
                    // For open paths: clamp at endpoints.
                    // For closed paths: indices wrap modulo nWork so the
                    // first and last sections share a smooth tangent with their
                    // neighbours across the seam.
                    // ----------------------------------------------------------
                    Vector3d tan;
                    if (isClosed)
                    {
                        // Both neighbours always exist via modular arithmetic
                        int prev = (i - 1 + nWork) % nWork;
                        int next = (i + 1) % nWork;
                        tan = workPts[next] - workPts[prev];
                    }
                    else
                    {
                        if (i > 0 && i < nWork - 1)
                            tan = workPts[i + 1] - workPts[i - 1];
                        else if (i == 0)
                            tan = workPts[1] - workPts[0];
                        else
                            tan = workPts[i] - workPts[i - 1];
                    }

                    if (!tan.Unitize() || tan.IsTiny(tol))
                        tan = lastTangent;
                    else
                        lastTangent = tan;

                    // Height direction (local +Z of frame, bead below ? -heightDir)
                    // For closed paths, index i maps directly to the original pts list
                    // (we only stripped the last duplicate, so indices 0..nWork-1 are safe).
                    Vector3d heightDir;
                    if (hasPlanesForBranch)
                    {
                        heightDir = planeBranch[i].ZAxis;
                        if (!heightDir.Unitize() || heightDir.IsTiny(tol))
                            heightDir = -worldZ;
                        else
                            heightDir = -heightDir;
                    }
                    else
                    {
                        heightDir = -worldZ;
                    }

                    // Width direction: perpendicular to both heightDir and tangent
                    Vector3d widthDir = Vector3d.CrossProduct(heightDir, tan);
                    if (!widthDir.Unitize() || widthDir.IsTiny(tol))
                    {
                        widthDir = Vector3d.CrossProduct(tan, Vector3d.XAxis);
                        if (!widthDir.Unitize() || widthDir.IsTiny(tol))
                            widthDir = Vector3d.YAxis;
                    }

                    // ----------------------------------------------------------
                    // Visual-only endpoint flux fix (open paths only).
                    // For closed loops flux[0] == 0 is still borrowed from flux[1]
                    // at i==0 to avoid a zero-width seam section.
                    // ----------------------------------------------------------
                    double fluxVis = fluxBranch[i];

                    if (i == 0 && nWork > 1 && fluxVis <= tol)
                        fluxVis = fluxBranch[1];

                    if (!isClosed && i == nWork - 1 && nWork > 1 && fluxVis <= tol)
                        fluxVis = fluxBranch[nWork - 2];

                    if (double.IsNaN(fluxVis) || double.IsInfinity(fluxVis) || fluxVis <= tol)
                    {
                        fluxVis = ResolvePositiveFlux(fluxBranch, i, tol);
                        invalidFluxSections[b]++;
                    }

                    double w = layer_w * fluxVis;
                    double h = heights[i];
                    if (double.IsNaN(h) || double.IsInfinity(h) || h <= tol)
                    {
                        h = fallbackHeight;
                        invalidHeightSections[b]++;
                    }

                    Polyline section = GenerateRoundedSquareSection(
                        pt, widthDir, heightDir, w, h, sectionSegs, 4.0);

                    if (section != null && section.Count >= 4)
                        sections.Add(section);
                }

                if (sections.Count != nWork)
                    missingProfileSections[b] = Math.Max(0, nWork - sections.Count);

                if (sections.Count < 2)
                {
                    failedMeshBranch[b] = true;
                    return;
                }

                Mesh m = LoftSectionPolylinesToMesh(sections, isClosed);
                if (m != null && m.Vertices.Count > 0 && m.Faces.Count > 0)
                {
                    branchMeshes[b] = m;
                }
                else
                {
                    failedMeshBranch[b] = true;
                }
            });

            // ----------------------------------------------------------------
            // Collect results into GH trees
            // ----------------------------------------------------------------
            GH_Structure<GH_Mesh> meshTree = new GH_Structure<GH_Mesh>();
            GH_Structure<GH_Curve> pathTree = new GH_Structure<GH_Curve>();
            GH_Structure<GH_Curve> profileTree = new GH_Structure<GH_Curve>();

            for (int b = 0; b < branchCount; b++)
            {
                GH_Path path = paths[b];

                if (branchPaths[b] != null && branchPaths[b].Count > 1)
                {
                    var c = new PolylineCurve(branchPaths[b]);
                    pathTree.Append(new GH_Curve(c), path);
                }

                var profList = branchProfiles[b];
                if (profList != null)
                {
                    foreach (Polyline pl in profList)
                    {
                        if (pl.Count > 1)
                        {
                            var c = new PolylineCurve(pl);
                            profileTree.Append(new GH_Curve(c), path);
                        }
                    }
                }

                if (branchMeshes[b] != null)
                    meshTree.Append(new GH_Mesh(branchMeshes[b]), path);
            }

            if (meshTree.IsEmpty)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Resulting gcode mesh tree is empty.");
            }
            else
            {
                int shortBranches = CountTrue(skippedShortBranch);
                int failedBranches = CountTrue(failedMeshBranch);
                int fluxFallbacks = Sum(invalidFluxSections);
                int heightFallbacks = Sum(invalidHeightSections);
                int missingProfiles = Sum(missingProfileSections);
                int meshBranches = meshTree.PathCount;
                if (meshBranches < branchCount || shortBranches > 0 || failedBranches > 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Remark,
                        $"Visualized {meshBranches}/{branchCount} point branches. " +
                        $"short_after_sim={shortBranches}, mesh_failed={failedBranches}. " +
                        "Branches with fewer than 2 points, zero/invalid height, or zero/invalid flow cannot create bead meshes.");
                }
                if (missingProfiles > 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Remark,
                        $"Generated fewer profiles than points in some branches: missing_profiles={missingProfiles}. " +
                        "This means some bead sections still failed after visual fallbacks.");
                }
                if (fluxFallbacks > 0 || heightFallbacks > 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Remark,
                        $"Visual fallback dimensions were used for invalid bead sections: " +
                        $"flux={fluxFallbacks}, layer_h={heightFallbacks}. " +
                        "This usually means some incoming flow or layer_h values are zero, NaN, or rounded too close to zero.");
                }
            }

            GH_Structure<GH_Number> layerWTree = explicitLayerW || !hasPackedPath || !packedPath.HasLayerW
                ? BuildConstantTree(ghPoints, layer_w)
                : global::WASPer_3DP.WasperGcodeTreeUtil.ToNumberStructure(packedPath.LayerW);
            GH_Structure<GH_Number> layerWfTree =
                explicitLayerW || !hasPackedPath || !packedPath.HasLayerWf
                    ? BuildLayerWidthTree(ghPoints, ghFlows, ghHeights, layer_w, fallbackHeight, tol)
                    : global::WASPer_3DP.WasperGcodeTreeUtil.ToNumberStructure(packedPath.LayerWf);
            DataTree<double> printVolTree = BuildPrintVolumeTree(ghPoints, ghHeights, layerWfTree, tol);
            DataTree<double> layerWData = global::WASPer_3DP.WasperGcodeTreeUtil.ToDoubleTree(layerWTree);
            DataTree<double> layerWfData = global::WASPer_3DP.WasperGcodeTreeUtil.ToDoubleTree(layerWfTree);

            DA.SetDataTree(0, meshTree);
            DA.SetDataTree(1, pathTree);
            DA.SetDataTree(2, profileTree);
            DA.SetData(3, new global::WASPer_3DP.WasperPrintPathGoo(
                new global::WASPer_3DP.WasperPrintPath(
                    global::WASPer_3DP.WasperGcodeTreeUtil.ToPointTree(ghPoints),
                    hasPackedPath ? packedPath.PtPlanes : null,
                    hasPackedPath ? packedPath.Flows : null,
                    hasPackedPath ? packedPath.LayerH : null,
                    printSpeed: packedPath?.PrintSpeed,
                    printLoc: packedPath?.PrintLoc,
                    printGlob: packedPath?.PrintGlob,
                    supportPts: packedPath?.SupportPts,
                    supportVects: packedPath?.SupportVects,
                    angles: packedPath?.Angles,
                    contactWidths: packedPath?.ContactWidths,
                    riskMaterial: packedPath?.RiskMaterial,
                    riskComb: packedPath?.RiskComb,
                    load: packedPath?.Load,
                    capacity: packedPath?.Capacity,
                    nozzleDiam: packedPath?.NozzleDiam,
                    dRatio: packedPath?.DRatio,
                    dLoaded: packedPath?.DLoaded,
                    bendRatio: packedPath?.BendRatio,
                    spanClass: packedPath?.SpanClass,
                    spanLen: packedPath?.SpanLen,
                    collapsed: packedPath?.Collapsed,
                    cascade: packedPath?.Cascade,
                    collapseGen: packedPath?.CollapseGen,
                    layerW: layerWData,
                    layerWf: layerWfData,
                    printVol: printVolTree,
                    torn: packedPath?.Torn,
                    interfaceRatio: packedPath?.InterfaceRatio,
                    overturnRatio: packedPath?.OverturnRatio,
                    failureFlags: packedPath?.FailureFlags)));
        }

        private static IList GetMatchingBranch<T>(
            GH_Structure<T> tree,
            GH_Path preferredPath,
            int fallbackIndex,
            out bool usedIndexFallback)
            where T : IGH_Goo
        {
            usedIndexFallback = false;
            if (tree == null || tree.PathCount == 0)
                return null;

            if (preferredPath != null && tree.PathExists(preferredPath))
                return tree.get_Branch(preferredPath);

            if (fallbackIndex >= 0 && fallbackIndex < tree.PathCount)
            {
                usedIndexFallback = true;
                return tree.get_Branch(tree.Paths[fallbackIndex]);
            }

            return null;
        }

        private static int CountTrue(bool[] values)
        {
            if (values == null) return 0;
            int count = 0;
            for (int i = 0; i < values.Length; i++)
                if (values[i]) count++;
            return count;
        }

        private static int Sum(int[] values)
        {
            if (values == null) return 0;
            int sum = 0;
            for (int i = 0; i < values.Length; i++)
                sum += values[i];
            return sum;
        }

        private static void InspectPlaneNormals(
            IList<Plane> planes,
            double tol,
            ref int invalidCount,
            ref int downwardCount,
            ref int inconsistentBranchCount)
        {
            if (planes == null || planes.Count == 0)
                return;

            Vector3d reference = Vector3d.Unset;
            bool branchInconsistent = false;

            for (int i = 0; i < planes.Count; i++)
            {
                Vector3d z = planes[i].ZAxis;
                if (!z.IsValid || z.Length <= tol || !z.Unitize())
                {
                    invalidCount++;
                    continue;
                }

                if (Vector3d.Multiply(z, Vector3d.ZAxis) < -0.1)
                    downwardCount++;

                if (!reference.IsValid)
                {
                    reference = z;
                    continue;
                }

                if (Vector3d.Multiply(reference, z) < 0.0)
                    branchInconsistent = true;
            }

            if (branchInconsistent)
                inconsistentBranchCount++;
        }

        // ==================================================================
        // Geometry helpers
        // ==================================================================
        private static void ApplyGlobalSimulationTrim(
            List<List<Point3d>> pointBranches,
            List<List<double>> fluxBranches,
            List<List<double>> heightBranches,
            List<List<Plane>> pointPlaneBranches,
            int selectedPointCount)
        {
            int remaining = Math.Max(0, selectedPointCount);
            int branchCount = pointBranches != null ? pointBranches.Count : 0;

            for (int b = 0; b < branchCount; b++)
            {
                List<Point3d> pts = pointBranches[b];
                int count = pts != null ? pts.Count : 0;
                int take = Math.Min(count, remaining);

                if (pts != null && take < count)
                    pointBranches[b] = pts.GetRange(0, take);

                TrimMatchingBranch(fluxBranches, b, take);
                TrimMatchingBranch(heightBranches, b, take);
                TrimMatchingBranch(pointPlaneBranches, b, take);

                remaining -= take;
                if (remaining < 0) remaining = 0;
            }
        }

        private static void ApplyRobotSimulationCut(
            List<List<Point3d>> pointBranches,
            List<List<double>> fluxBranches,
            List<List<double>> heightBranches,
            List<List<Plane>> pointPlaneBranches,
            global::WASPer_3DP.WasperRobotSimulationCut cut,
            double tolerance)
        {
            int branch = cut.PartialBranchIndex;
            int point = cut.PartialPointIndex;
            double? flux = ValueAt(fluxBranches, branch, point);
            double? height = ValueAt(heightBranches, branch, point);
            Plane? plane = ValueAt(pointPlaneBranches, branch, point);

            ApplyGlobalSimulationTrim(
                pointBranches,
                fluxBranches,
                heightBranches,
                pointPlaneBranches,
                cut.CompletedPointCount);

            if (!cut.HasPartialPoint ||
                branch < 0 ||
                branch >= pointBranches.Count ||
                pointBranches[branch] == null ||
                pointBranches[branch].Count == 0 ||
                pointBranches[branch][pointBranches[branch].Count - 1]
                    .DistanceTo(cut.PartialPoint) <= tolerance)
            {
                return;
            }

            pointBranches[branch].Add(cut.PartialPoint);
            if (flux.HasValue && fluxBranches[branch] != null)
                fluxBranches[branch].Add(flux.Value);
            if (height.HasValue && heightBranches[branch] != null)
                heightBranches[branch].Add(height.Value);
            if (plane.HasValue && pointPlaneBranches[branch] != null)
            {
                Plane partialPlane = plane.Value;
                partialPlane.Origin = cut.PartialPoint;
                pointPlaneBranches[branch].Add(partialPlane);
            }
        }

        private static T? ValueAt<T>(
            List<List<T>> branches,
            int branch,
            int index)
            where T : struct
        {
            if (branches == null ||
                branch < 0 ||
                branch >= branches.Count ||
                branches[branch] == null ||
                index < 0 ||
                index >= branches[branch].Count)
            {
                return null;
            }

            return branches[branch][index];
        }

        private static void TrimMatchingBranch<T>(List<List<T>> branches, int index, int count)
        {
            if (branches == null || index < 0 || index >= branches.Count) return;

            List<T> branch = branches[index];
            if (branch == null) return;

            if (count <= 0)
            {
                branches[index] = new List<T>();
                return;
            }

            if (branch.Count > count)
                branches[index] = branch.GetRange(0, count);
        }

        private static double Clamp01(double value)
        {
            if (value < 0.0) return 0.0;
            if (value > 1.0) return 1.0;
            return value;
        }

        private static GH_Structure<GH_Number> BuildConstantTree(
            GH_Structure<GH_Point> points,
            double value)
        {
            var result = new GH_Structure<GH_Number>();
            if (points == null) return result;

            for (int b = 0; b < points.PathCount; b++)
            {
                GH_Path path = points.Paths[b];
                IList branch = points.get_Branch(path);
                int count = branch?.Count ?? 0;
                for (int i = 0; i < count; i++) result.Append(new GH_Number(value), path);
            }

            return result;
        }

        private static GH_Structure<GH_Number> BuildLayerWidthTree(
            GH_Structure<GH_Point> points,
            GH_Structure<GH_Number> flows,
            GH_Structure<GH_Number> heights,
            double layerWidth,
            double fallbackHeight,
            double tol)
        {
            var result = new GH_Structure<GH_Number>();
            if (points == null) return result;

            bool hasGlobalFlow = TrySingleValue(flows, out double globalFlow);
            if (!hasGlobalFlow) globalFlow = 1.0;

            for (int b = 0; b < points.PathCount; b++)
            {
                GH_Path path = points.Paths[b];
                IList pointBranch = points.get_Branch(path);
                IList flowBranch = !hasGlobalFlow && flows != null && flows.PathExists(path) ? flows.get_Branch(path) : null;
                IList heightBranch = heights != null && heights.PathExists(path) ? heights.get_Branch(path) : null;
                int count = pointBranch?.Count ?? 0;

                for (int i = 0; i < count; i++)
                {
                    double flow = globalFlow;
                    if (!hasGlobalFlow && flowBranch != null && i < flowBranch.Count &&
                        flowBranch[i] is GH_Number number &&
                        number.Value > tol &&
                        !double.IsNaN(number.Value) &&
                        !double.IsInfinity(number.Value))
                        flow = number.Value;

                    double height = NumberAt(heightBranch, i);
                    if (height <= tol) height = fallbackHeight;
                    result.Append(new GH_Number(EstimateFlowAdjustedWidth(layerWidth, height, flow, tol)), path);
                }
            }

            return result;
        }

        private static DataTree<double> BuildPrintVolumeTree(
            GH_Structure<GH_Point> points,
            GH_Structure<GH_Number> heights,
            GH_Structure<GH_Number> widths,
            double tol)
        {
            var result = new DataTree<double>();
            if (points == null) return result;

            for (int b = 0; b < points.PathCount; b++)
            {
                GH_Path path = points.Paths[b];
                IList pointBranch = points.get_Branch(path);
                IList widthBranch = widths != null && widths.PathExists(path) ? widths.get_Branch(path) : null;
                IList heightBranch = heights != null && heights.PathExists(path) ? heights.get_Branch(path) : null;
                int count = pointBranch?.Count ?? 0;

                for (int i = 0; i < count; i++)
                {
                    double volume = 0.0;
                    if (i > 0 && pointBranch[i - 1] is GH_Point previous && pointBranch[i] is GH_Point current)
                    {
                        double width = NumberAt(widthBranch, i);
                        double height = NumberAt(heightBranch, i);
                        double length = previous.Value.DistanceTo(current.Value);
                        if (width > tol && height > tol && double.IsFinite(length))
                        {
                            double area = BeadArea(width, height, tol);
                            if (area > 0.0 && double.IsFinite(area))
                                volume = length * area;
                        }
                    }
                    result.Add(volume, path);
                }
            }

            return result;
        }

        private static double EstimateFlowAdjustedWidth(double nominalWidth, double height,
            double flow, double tol)
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

        private static double NumberAt(IList branch, int index)
        {
            if (branch == null || branch.Count == 0) return 0.0;
            int resolved = branch.Count == 1 ? 0 : Math.Min(index, branch.Count - 1);
            return branch[resolved] is GH_Number number && double.IsFinite(number.Value) ? number.Value : 0.0;
        }

        private static bool TrySingleValue(GH_Structure<GH_Number> tree, out double value)
        {
            value = 0.0;
            if (tree == null || tree.DataCount != 1) return false;
            foreach (GH_Number number in tree.AllData(true))
            {
                if (number != null && double.IsFinite(number.Value))
                {
                    value = number.Value;
                    return true;
                }
            }
            return false;
        }

        private static double RepresentativeLayerWidth(
            GH_Structure<GH_Number> widths,
            double fallback,
            double tol)
        {
            if (widths == null || widths.PathCount == 0)
                return fallback;

            for (int p = 0; p < widths.PathCount; p++)
            {
                IList branch = widths.get_Branch(widths.Paths[p]);
                if (branch == null) continue;

                foreach (object goo in branch)
                {
                    GH_Number number = goo as GH_Number;
                    if (number != null && number.Value > tol)
                        return number.Value;
                }
            }

            return fallback;
        }

        private static double RepresentativeLayerHeight(
            GH_Structure<GH_Number> heights,
            double fallback,
            double tol)
        {
            if (heights == null || heights.PathCount == 0)
                return fallback;

            for (int p = 0; p < heights.PathCount; p++)
            {
                IList branch = heights.get_Branch(heights.Paths[p]);
                if (branch == null) continue;

                foreach (object goo in branch)
                {
                    GH_Number number = goo as GH_Number;
                    if (number != null && number.Value > tol)
                        return number.Value;
                }
            }

            return fallback;
        }

        private static double ResolvePositiveFlux(IList<double> flows, int index, double tol)
        {
            if (flows != null)
            {
                int n = flows.Count;
                for (int offset = 1; offset < n; offset++)
                {
                    int lo = index - offset;
                    if (lo >= 0)
                    {
                        double f = flows[lo];
                        if (!double.IsNaN(f) && !double.IsInfinity(f) && f > tol)
                            return f;
                    }

                    int hi = index + offset;
                    if (hi < n)
                    {
                        double f = flows[hi];
                        if (!double.IsNaN(f) && !double.IsInfinity(f) && f > tol)
                            return f;
                    }
                }
            }

            return 1.0;
        }

        private Polyline GenerateRoundedSquareSection(
            Point3d pt,
            Vector3d widthDir,
            Vector3d heightDir,
            double width,
            double height,
            int segs,
            double power)
        {
            if (width <= 0.0 || height <= 0.0)
                return null;

            if (segs < 8) segs = 8;
            if (power < 2.0) power = 2.0;

            Polyline pl = new Polyline();

            double a = width * 0.5;
            double b = height * 0.5;
            double centerY = b; // y ? [0, height]

            for (int i = 0; i < segs; i++)
            {
                double t = (2.0 * Math.PI * i) / segs;

                double cosT = Math.Cos(t);
                double sinT = Math.Sin(t);

                double xUnit = Math.Sign(cosT) * Math.Pow(Math.Abs(cosT), 2.0 / power);
                double yUnit = Math.Sign(sinT) * Math.Pow(Math.Abs(sinT), 2.0 / power);

                double xLocal = a * xUnit;
                double yRel = b * yUnit;
                double yLocal = centerY + yRel; // [0, height]

                Point3d p = pt + widthDir * xLocal + heightDir * yLocal;
                pl.Add(p);
            }

            pl.Add(pl[0]); // close the profile loop
            return pl;
        }

        /// <summary>
        /// Lofts a list of closed cross-section polylines into a mesh.
        /// 
        /// When <paramref name="isClosed"/> is true the path itself is a loop:
        ///   - An extra ring of quad faces connects the LAST section back to the
        ///     FIRST section, sealing the tube into a torus-like solid.
        ///   - No flat end-caps are added (the tube has no open ends).
        /// 
        /// When <paramref name="isClosed"/> is false the original behaviour is
        /// preserved: open barrel + two flat end-caps.
        /// </summary>
        private Mesh LoftSectionPolylinesToMesh(List<Polyline> sections, bool isClosed)
        {
            if (sections == null || sections.Count < 2)
                return null;

            int profileCount = sections.Count;
            int vertPerProfile = SectionVertexCount(sections[0]);
            if (vertPerProfile < 3)
                return null;

            for (int i = 1; i < profileCount; i++)
            {
                if (SectionVertexCount(sections[i]) != vertPerProfile)
                    return null;
            }

            Mesh mesh = new Mesh();

            int[,] idx = new int[profileCount, vertPerProfile];
            for (int i = 0; i < profileCount; i++)
            {
                Polyline pl = sections[i];
                for (int j = 0; j < vertPerProfile; j++)
                    idx[i, j] = mesh.Vertices.Add(pl[j]);
            }

            for (int i = 0; i < profileCount - 1; i++)
            {
                for (int j = 0; j < vertPerProfile; j++)
                {
                    int jNext = (j + 1) % vertPerProfile;
                    int i0 = idx[i, j];
                    int i1 = idx[i, jNext];
                    int i2 = idx[i + 1, jNext];
                    int i3 = idx[i + 1, j];

                    if (i0 == i1 || i1 == i2 || i2 == i3 || i3 == i0)
                        continue;

                    mesh.Faces.AddFace(i0, i1, i2, i3);
                }
            }

            if (isClosed)
            {
                // ----------------------------------------------------------------
                // Wrap-around ring: connect last section ? first section.
                // This closes the tube into a seamless ring (torus topology).
                // ----------------------------------------------------------------
                int last = profileCount - 1;
                for (int j = 0; j < vertPerProfile; j++)
                {
                    int jNext = (j + 1) % vertPerProfile;
                    int i0 = idx[last, j];
                    int i1 = idx[last, jNext];
                    int i2 = idx[0, jNext];
                    int i3 = idx[0, j];

                    if (i0 == i1 || i1 == i2 || i2 == i3 || i3 == i0)
                        continue;

                    mesh.Faces.AddFace(i0, i1, i2, i3);
                }
                // No caps — the tube is fully closed by the ring above.
            }
            else
            {
                // ----------------------------------------------------------------
                // Open path: add flat end-caps as before.
                // ----------------------------------------------------------------
                int startCenter = mesh.Vertices.Add(GetSectionCenter(sections[0], vertPerProfile));
                for (int j = 0; j < vertPerProfile; j++)
                {
                    int jNext = (j + 1) % vertPerProfile;
                    mesh.Faces.AddFace(startCenter, idx[0, jNext], idx[0, j]);
                }

                int last = profileCount - 1;
                int endCenter = mesh.Vertices.Add(GetSectionCenter(sections[last], vertPerProfile));
                for (int j = 0; j < vertPerProfile; j++)
                {
                    int jNext = (j + 1) % vertPerProfile;
                    mesh.Faces.AddFace(endCenter, idx[last, j], idx[last, jNext]);
                }
            }

            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CombineIdentical(true, true);
            mesh.Vertices.CullUnused();
            mesh.Weld(Math.PI);
            mesh.Normals.ComputeNormals();
            mesh.Compact();

            if (mesh.Vertices.Count == 0 || mesh.Faces.Count == 0)
                return null;

            return mesh;
        }

        private static int SectionVertexCount(Polyline section)
        {
            if (section == null || section.Count == 0)
                return 0;

            int count = section.Count;
            if (count > 1 && section[0].DistanceToSquared(section[count - 1]) <= RhinoMath.SqrtEpsilon)
                count--;

            return count;
        }

        private static Point3d GetSectionCenter(Polyline section, int count)
        {
            if (section == null || count <= 0)
                return Point3d.Origin;

            double x = 0.0;
            double y = 0.0;
            double z = 0.0;

            for (int i = 0; i < count; i++)
            {
                x += section[i].X;
                y += section[i].Y;
                z += section[i].Z;
            }

            double inv = 1.0 / count;
            return new Point3d(x * inv, y * inv, z * inv);
        }
    }
}
