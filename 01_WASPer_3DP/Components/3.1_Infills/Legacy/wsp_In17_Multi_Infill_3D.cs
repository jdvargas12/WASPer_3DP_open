#region Component Description
/*
    Component Name : wsp_In17_Multi-Infill 3D
    Nickname       : MultiInfill3D
    Version        : v2.0.0 - 260422

    Description:
        Dispatcher component that routes each domain in a layer to one of two
        infill engines, selected per-domain via the 'infill_type' list input.

        infill_type (list, cycles per domain):
            0 = Oct / Turtle Cells   (Truncated-Octa zig-zag)
            1 = TPMS iso-contours    (Gyroid, Schwarz-P/D, IWP, …)

        guide_curves tree:
            Each branch = one cavity / layer.
            Each branch must contain >= 2 curves ordered from one boundary to the other.
            Domains are formed between consecutive pairs: [0+1], [1+2], etc.
            Different domains within the same branch can use different engines.

        -- SHARED inputs (cycle per domain) ------------------------------------
            infill_type : 0 = Oct  |  1 = TPMS
            shorten     : arc-length trimmed from each end of the guide pair
            inset       : gap inset on the two OUTERMOST boundary curves only
            clear       : clearance from intermediate partition guides
                          TPMS ? gap inset on INTERMEDIATE curves [1..n-2]
            count_x     : Oct ? cells along the curve direction
                          TPMS ? period count along the curve direction
            count_y     : Oct ? band count across the gap (each band = 2 paths)
                          TPMS ? period count across the gap
            count_z     : Oct ? triangle-wave cycles across the layer stack (0 = flat)
                          TPMS ? period count in z / across the stack (0 ? clamped to 1)
            trim_paths  : true = {layer}; false = {layer;domain;path}
            close_shell : true = pair the two outer shell sides into closed loops

        -- OCT-SPECIFIC inputs (o_ prefix, list, cycle per domain) -------------
            o_path_width : nominal path width; controls internal xa / xr sizing
            o_bridge_0   : start bridge parameter [0..1]  (0 = bridge at bottom)
            o_bridge_1   : peak bridge parameter  [0..1]  (1 = bridge at top)
            o_extend     : extend first and last segment of each path by this length
            o_teeth      : enable bottom-teeth geometry

        -- TPMS-SPECIFIC inputs (p_ prefix) ------------------------------------
            p_type     : surface type (list, cycles):
                         0=P  1=D  2=Gyroid  3=IWP  4=Neovius  5=Lidinoid  6=FK-S  7=FK-Y
            p_level    : iso-level (list, cycles)
            p_phase_x  : phase offset in s  [0..1]  (list, cycles)
            p_phase_y  : phase offset in t  [0..1]  (list, cycles)
            p_phase_z  : phase offset in n  [0..1]  (list, cycles)
            p_spacing  : grid cell size in model units (item)
            p_min_pts  : discard contours with fewer polyline vertices (item)

        -- FINAL inputs -------------------------------------------------------
            trim_paths, close_shell

    Output tree structure (trim_paths = false):
        {layer ; domain ; path}

    Output tree structure (trim_paths = true):
        {layer}
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Grasshopper.Kernel.Parameters;

using Rhino;
using Rhino.Geometry;
#endregion

namespace WASPer_3DP.Components._3_1_Infills
{
    public class wsp_In17_Multi_Infill_3d : GH_Component
    {
        private readonly string _versionTag;
        private const int PARALLEL_THRESHOLD = 8;
        private const int TPMS_GRID_MIN      = 4;
        private const int TPMS_GRID_MAX      = 2000;

        public wsp_In17_Multi_Infill_3d()
            : base(
                "wsp_In17_Multi-Infill 3D",
                "MultiInfill3D",
                "Generates infill paths across multiple cavities (domains), dispatching each domain\n" +
                "to one of two engines based on 'infill_type':\n" +
                "  0 = Oct  — Truncated-Octa zig-zag (Turtle Cells)\n" +
                "  1 = TPMS — Triply Periodic Minimal Surface iso-contours\n\n" +
                "guide_curves tree: each branch = one layer, >= 2 curves.\n" +
                "Domains = consecutive pairs [i]/[i+1].  infill_type cycles per domain.\n\n" +
                "SHARED inputs (cycle per domain):\n" +
                "  shorten, inset, clear, count_x, count_y, count_z\n\n" +
                "OCT-specific (o_ prefix): o_path_width, o_bridge_0, o_bridge_1, o_extend, o_teeth\n\n" +
                "TPMS-specific (p_ prefix): p_type, p_level, p_phase_x/y/z, p_spacing, p_min_pts\n\n" +
                "count_x / count_y / count_z are interpreted per-engine:\n" +
                "  Oct  ? cells along curve | band count | z-wave cycles\n" +
                "  TPMS ? period cx        | period cy  | period cz (z-stack)",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "3.1_Infills")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v2.0.0";
            Message = _versionTag;
        }

        // Keep the same GUID as v1 so existing Grasshopper files resolve to this component.
        public override Guid ComponentGuid =>
            new Guid("9F2A3B4C-5D6E-4F80-9A1B-2C3D4E5F6A7B");

        public override GH_Exposure Exposure => GH_Exposure.hidden;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_In11_Multi_Infill.png"))
                        return s != null ? new Bitmap(s) : null;
                }
                catch { }
                return null;
            }
        }

        // -- IO ----------------------------------------------------------------

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            // -- SHARED ------------------------------------------------------
            // 0
            pManager.AddCurveParameter(
                "guide_curves", "guides",
                "Guide curves as a DataTree.  Each branch = one layer / cavity.\n" +
                "Each branch must have >= 2 curves ordered from one boundary to the other.\n" +
                "Domains = consecutive pairs [i]/[i+1].  Auto-aligned to same direction.",
                GH_ParamAccess.tree);
            pManager[0].Optional = true;

            // 1
            AddNumList(pManager, "w_shell", "w_shell",
                "Shell line width in model units (Slicer Plus convention).\n" +
                "Shell line i is centred at (i - 0.5) × w_shell inward from the outermost guide.\n" +
                "Default 5. 0 = no shell output. LIST. Cycles per domain.", 5.0);

            // 2
            AddIntList(pManager, "n_shell", "n_shell",
                "Number of shell lines per outermost guide. Minimum 1. LIST. Cycles per domain.", 1);

            // 3
            AddIntList(pManager, "infill_type", "type",
                "Engine selector per domain (list, cycles):\n  0 = Oct (Turtle Cells)\n  1 = TPMS iso-contours", 0);

            // 4
            AddNumList(pManager, "clear_long", "clear_long",
                "Arc-length clearance trimmed from each end of the guide pair per domain (model units).\n" +
                "Only affects infill — shell uses full guides. >= 0.", 0.0);

            // 5
            AddNumList(pManager, "clear_guide", "clear_guide",
                "Gap inset applied to the two OUTERMOST boundary curves only (model units).\n" +
                "Only affects infill — shell is built before this is applied. >= 0.", 0.0);

            // 6
            AddNumList(pManager, "clear_in", "clear_in",
                "Oct  ? clearance from INTERMEDIATE partition guides. Internal band clearance is auto-derived from o_path_width.\n" +
                "TPMS ? gap inset applied to all INTERMEDIATE curves [1..n-2].\n" +
                ">= 0.", 0.0);

            // 7
            AddIntList(pManager, "count_x", "cx",
                "Oct  ? cell count along the curve direction (min 1).\n" +
                "TPMS ? period count along the curve direction (min 1).\n" +
                "List, cycles per domain.  Default 4.", 4);

            // 8
            AddIntList(pManager, "count_y", "cy",
                "Oct  ? band count across the gap.  Each band = 2 paths.  Min 1.\n" +
                "TPMS ? period count across the gap.  Min 1.\n" +
                "List, cycles per domain.  Default 1.", 1);

            // 9  — Number so Oct's float cz works unchanged; TPMS rounds to int.
            AddNumList(pManager, "count_z", "cz",
                "Oct  ? triangle-wave cycles across the layer stack.  Default 1 (one full b0?b1?b0 cycle).  0 = lock bridge at b0.\n" +
                "TPMS ? period count in z / across the stack (rounded to int, min 1).\n" +
                "List, cycles per domain.", 1.0);

            // -- OCT-SPECIFIC -------------------------------------------------
            // 10
            AddNumList(pManager, "o_path_width", "o_pW",
                "[Oct]  Nominal path width (model units).  Controls internal xa / xr segment sizing.\n" +
                "Also used to derive automatic spacing between Oct bands. Must be > 0. Default 4.", 4.0);

            // 11
            AddNumList(pManager, "o_bridge_0", "o_b0",
                "[Oct]  Start bridge parameter [0..1].  0 = bridge sits at the band bottom.\n" +
                "Interpolated with o_bridge_1 via a triangle-wave driven by count_z.", 0.0);

            // 12
            AddNumList(pManager, "o_bridge_1", "o_b1",
                "[Oct]  Peak bridge parameter [0..1].  1 = bridge sits at the band top.\n" +
                "Interpolated with o_bridge_0 via a triangle-wave driven by count_z.", 1.0);

            // 13
            AddNumList(pManager, "o_extend", "o_ext",
                "[Oct]  Arc-length extension applied to the first and last segment of every\n" +
                "generated path.  Useful for improving connections at path endpoints.  >= 0.", 0.0);

            // 14
            AddBoolList(pManager, "o_teeth", "o_teeth",
                "[Oct]  If true, bottom-teeth geometry is added to each cell.", false);

            // -- TPMS-SPECIFIC -------------------------------------------------
            // 15
            AddIntList(pManager, "p_type", "p_type",
                "[TPMS]  Surface type per domain (list, cycles):\n" +
                "  0=P (Schwarz)  1=D (Diamond)  2=Gyroid  3=IWP\n" +
                "  4=Neovius      5=Lidinoid      6=FK-S    7=FK-Y\n" +
                "Default 2 (Gyroid).", 2);

            // 16
            AddNumList(pManager, "p_level", "p_level",
                "[TPMS]  Iso-level: contour is drawn where field = level.\n" +
                "Shift positive/negative to bias toward one side.  Default 0.", 0.0);

            // 17
            AddNumList(pManager, "p_phase_x", "p_px",
                "[TPMS]  Phase offset along the curve direction, s ? [0..1].  Default 0.", 0.0);

            // 18
            AddNumList(pManager, "p_phase_y", "p_py",
                "[TPMS]  Phase offset across the gap, t ? [0..1].  Default 0.", 0.0);

            // 19
            AddNumList(pManager, "p_phase_z", "p_pz",
                "[TPMS]  Phase offset across the stack, n ? [0..1].  Default 0.", 0.0);

            // 20
            pManager.AddNumberParameter(
                "p_spacing", "p_dx",
                "[TPMS]  Grid cell size (model units).  Smaller = finer marching-squares grid.\n" +
                "Drives nx and ny resolution.  Default 1.0.",
                GH_ParamAccess.item, 1.0);
            pManager[20].Optional = true;

            // 21
            pManager.AddIntegerParameter(
                "p_min_pts", "p_min",
                "[TPMS]  Discard contour polylines with fewer vertices than this.  Default 2.",
                GH_ParamAccess.item, 2);
            pManager[21].Optional = true;

            // -- OUTPUT STRUCTURE / SHELL OPTIONS ------------------------------
            // 22
            AddBoolItem(pManager,
                "trim_paths", "trim",
                "Output tree structure for shell, infill, partitions, and points.\n" +
                "False = {layer;domain;path}, True = {layer}. Default true.",
                true);

            // 23
            AddBoolItem(pManager,
                "close_shell", "close_shell",
                "If true, each shell polyline is closed (first point appended at the end).\n" +
                "When n_shell > 1 each individual shell line is closed independently. Default true.",
                true);

            // 24
            pManager.AddNumberParameter(
                "res", "res",
                "Shell sampling resolution in model units. Smaller values create smoother shell curves and heavier computation.\n" +
                "If <= 0 or unwired, the component auto-derives a spacing from the guide lengths to keep roughly the previous 64-sample shell quality.",
                GH_ParamAccess.item, 0.0);
            pManager[24].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "shell", "shell",
                "Shell polylines offset inward from outermost guides (n_shell lines per guide, Slicer Plus convention).\n" +
                "One branch per layer {layer}.",
                GH_ParamAccess.tree);

            pManager.AddCurveParameter(
                "infill", "infill",
                "Infill paths.\n" +
                "trim_paths=false: {layer;domain;path}\n" +
                "trim_paths=true : {layer}.",
                GH_ParamAccess.tree);

            pManager.AddCurveParameter(
                "partitions", "parts",
                "Inner guide curves that partition the space into domains — all guides except the two outermost ones ([1..n-2]).\n" +
                "One branch per layer {layer}.  Empty when each layer has exactly 2 guides.",
                GH_ParamAccess.tree);

            pManager.AddPointParameter(
                "pts", "pts",
                "Polyline vertices matching 'infill'.  Same tree structure.",
                GH_ParamAccess.tree);

            pManager.AddTextParameter(
                "info", "info",
                "Diagnostics: per-branch and per-domain stats.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "por_layer", "f_layer",
                "Estimated porosity per layer (0–1). 1 = fully void, 0 = fully solid.\n" +
                "Computed as 1 - printed_area / total_area.",
                GH_ParamAccess.list);

            pManager.AddNumberParameter(
                "por_avg", "f_avg",
                "Average estimated porosity across all layers (0–1).",
                GH_ParamAccess.item);

            pManager.AddPlaneParameter(
                "layer_planes", "la_planes",
                "Estimated source plane for each generated layer. One branch per layer.",
                GH_ParamAccess.tree);
        }

        // -- Solve -------------------------------------------------------------

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // -- Retrieve inputs ----------------------------------------------
            GH_Structure<GH_Curve> guideTree = null;

            var wShellList     = new List<double>();
            var nShellList     = new List<int>();
            var typeList       = new List<int>();
            var clearLongList  = new List<double>();
            var clearGuideList = new List<double>();
            var clearInList    = new List<double>();
            var cxList         = new List<int>();
            var cyList         = new List<int>();
            var czList         = new List<double>();  // Number: Oct=float, TPMS=rounded int
            bool trimPaths  = true;
            bool closeShell = true;
            double shellRes = 0.0;

            // Oct-specific
            var oPwList     = new List<double>();
            var oB0List     = new List<double>();
            var oB1List     = new List<double>();
            var oExtList    = new List<double>();
            var oTeethList  = new List<bool>();

            // TPMS-specific
            var pTypeList   = new List<int>();
            var pLevelList  = new List<double>();
            var pPxList     = new List<double>();
            var pPyList     = new List<double>();
            var pPzList     = new List<double>();
            double pSpacing = 1.0;
            int    pMinPts  = 2;

            if (!DA.GetDataTree(0, out guideTree) || guideTree == null || guideTree.PathCount == 0)
            {
                DA.SetDataTree(0, new DataTree<Curve>());
                DA.SetDataTree(1, new DataTree<Curve>());
                DA.SetDataTree(2, new DataTree<Curve>());
                DA.SetDataTree(3, new DataTree<Point3d>());
                DA.SetData(4, "Provide guide_curves as a DataTree.  Each branch must have >= 2 curves.");
                DA.SetDataList(5, new List<double>());
                DA.SetData(6, 0.0);
                DA.SetDataTree(7, new DataTree<Plane>());
                return;
            }

            DA.GetDataList(1,  wShellList);
            DA.GetDataList(2,  nShellList);
            DA.GetDataList(3,  typeList);
            DA.GetDataList(4,  clearLongList);
            DA.GetDataList(5,  clearGuideList);
            DA.GetDataList(6,  clearInList);
            DA.GetDataList(7,  cxList);
            DA.GetDataList(8,  cyList);
            DA.GetDataList(9,  czList);

            DA.GetDataList(10, oPwList);
            DA.GetDataList(11, oB0List);
            DA.GetDataList(12, oB1List);
            DA.GetDataList(13, oExtList);
            DA.GetDataList(14, oTeethList);

            DA.GetDataList(15, pTypeList);
            DA.GetDataList(16, pLevelList);
            DA.GetDataList(17, pPxList);
            DA.GetDataList(18, pPyList);
            DA.GetDataList(19, pPzList);
            DA.GetData    (20, ref pSpacing);
            DA.GetData    (21, ref pMinPts);
            DA.GetData    (22, ref trimPaths);
            DA.GetData    (23, ref closeShell);
            DA.GetData    (24, ref shellRes);

            // -- Sanitise shared lists ----------------------------------------
            if (wShellList.Count    == 0) wShellList.Add(5.0);
            if (nShellList.Count    == 0) nShellList.Add(1);
            for (int i = 0; i < wShellList.Count; i++) if (wShellList[i] < 0.0) wShellList[i] = 0.0;
            for (int i = 0; i < nShellList.Count; i++) if (nShellList[i] < 1)   nShellList[i] = 1;

            EnsureDefault(typeList,       0);
            // clearLongList / clearGuideList: no static default — dynamic per domain (w_shell × n_shell - w_shell / 2)
            EnsureDefault(clearInList,    0.0);
            EnsureDefault(cxList,         4);
            EnsureDefault(cyList,         1);
            EnsureDefault(czList,         1.0);

            for (int i = 0; i < typeList.Count;       i++) typeList[i]       = Clamp(typeList[i], 0, 1);
            for (int i = 0; i < clearLongList.Count;  i++) clearLongList[i]  = Math.Max(0.0, clearLongList[i]);
            for (int i = 0; i < clearGuideList.Count; i++) clearGuideList[i] = Math.Max(0.0, clearGuideList[i]);
            for (int i = 0; i < clearInList.Count;    i++) clearInList[i]    = Math.Max(0.0, clearInList[i]);
            for (int i = 0; i < cxList.Count;         i++) cxList[i]         = Math.Max(1, cxList[i]);
            for (int i = 0; i < cyList.Count;         i++) cyList[i]         = Math.Max(1, cyList[i]);
            for (int i = 0; i < czList.Count;         i++) czList[i]         = Math.Max(0.0, czList[i]);

            // -- Sanitise Oct lists -------------------------------------------
            EnsureDefault(oPwList,    4.0);
            EnsureDefault(oB0List,    0.0);
            EnsureDefault(oB1List,    1.0);
            EnsureDefault(oExtList,   0.0);
            EnsureDefault(oTeethList, false);

            for (int i = 0; i < oPwList.Count;  i++) { if (double.IsNaN(oPwList[i]) || oPwList[i] <= 0) oPwList[i] = 4.0; }
            for (int i = 0; i < oB0List.Count;  i++) oB0List[i]  = Clamp01(oB0List[i]);
            for (int i = 0; i < oB1List.Count;  i++) oB1List[i]  = Clamp01(oB1List[i]);
            for (int i = 0; i < oExtList.Count; i++) oExtList[i] = Math.Max(0.0, oExtList[i]);

            // -- Sanitise TPMS lists ------------------------------------------
            EnsureDefault(pTypeList,  2);
            EnsureDefault(pLevelList, 0.0);
            EnsureDefault(pPxList,    0.0);
            EnsureDefault(pPyList,    0.0);
            EnsureDefault(pPzList,    0.0);

            for (int i = 0; i < pTypeList.Count; i++) pTypeList[i] = Clamp(pTypeList[i], 0, 7);
            if (pSpacing <= 0.0) pSpacing = 1.0;
            if (pMinPts  <  2)   pMinPts  = 2;

            // -- Status message -----------------------------------------------
            string typeTag = BuildListTag(typeList, v => v == 0 ? "Oct" : "TPMS");
            Message = $"{_versionTag} | {typeTag}";

            // -- Tolerance ----------------------------------------------------
            double tol = RhinoDoc.ActiveDoc != null ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 1e-6;
            double eps = Math.Max(1e-9, tol * 0.1);

            // -- Per-branch accumulators --------------------------------------
            int branchCount = guideTree.PathCount;

            var perShells = new List<(GH_Path path, PolylineCurve crv)>[branchCount];
            var perParts  = new List<(GH_Path path, Curve crv)>[branchCount];
            var perCrvs   = new List<(GH_Path path, PolylineCurve crv)>[branchCount];
            var perPts    = new List<(GH_Path path, Point3d pt)>[branchCount];
            var perLog    = new string[branchCount];
            var perBranchPor = new double[branchCount];

            for (int i = 0; i < branchCount; i++)
            {
                perShells[i] = new List<(GH_Path, PolylineCurve)>();
                perParts [i] = new List<(GH_Path, Curve)>();
                perCrvs  [i] = new List<(GH_Path, PolylineCurve)>();
                perPts   [i] = new List<(GH_Path, Point3d)>();
                perLog   [i] = "";
            }

            int totalSkipped = 0;
            int totalPaths   = 0;
            int totalShells  = 0;

            // Capture lists for closures
            var _wShellList    = wShellList;
            var _nShellList    = nShellList;
            var _typeList      = typeList;
            var _clearLongList = clearLongList;
            var _clearGuideList= clearGuideList;
            var _clearInList   = clearInList;
            var _cxList        = cxList;
            var _cyList        = cyList;
            var _czList        = czList;
            bool _trim         = trimPaths;
            bool _closeShell   = closeShell;

            var _oPwList    = oPwList;
            var _oB0List    = oB0List;
            var _oB1List    = oB1List;
            var _oExtList   = oExtList;
            var _oTeethList = oTeethList;

            var _pTypeList  = pTypeList;
            var _pLevelList = pLevelList;
            var _pPxList    = pPxList;
            var _pPyList    = pPyList;
            var _pPzList    = pPzList;
            double _pSpacing = pSpacing;
            int    _pMinPts  = pMinPts;

            // -- Branch processor ---------------------------------------------
            Action<int> processBranch = bi =>
            {
                var log    = new StringBuilder();
                var shells = new List<(GH_Path, PolylineCurve)>();
                var parts  = new List<(GH_Path, Curve)>();
                var crvs   = new List<(GH_Path, PolylineCurve)>();
                var pts    = new List<(GH_Path, Point3d)>();

                GH_Path brPath = guideTree.Paths[bi];
                GH_Path layerPath = _trim ? new GH_Path(bi) : new GH_Path(brPath.Indices);
                var br         = guideTree.Branches[bi];

                void Bail(string msg)
                {
                    if (!string.IsNullOrEmpty(msg)) log.AppendLine(msg);
                    Interlocked.Increment(ref totalSkipped);
                    perShells[bi] = shells; perParts[bi] = parts;
                    perCrvs[bi]   = crvs;   perPts[bi]  = pts;
                    perLog[bi]    = log.ToString();
                }

                if (br == null || br.Count < 2)
                {
                    Bail($"Branch {brPath}: needs >= 2 curves. Skipped."); return;
                }

                int nCurves  = br.Count;
                int nDomains = nCurves - 1;
                var curves   = new List<Curve>(nCurves);

                for (int ci = 0; ci < nCurves; ci++)
                {
                    Curve c = br[ci]?.Value?.DuplicateCurve();
                    if (c == null || !c.IsValid)
                    {
                        Bail($"Branch {brPath}: curve [{ci}] is invalid. Skipped."); return;
                    }
                    curves.Add(c);
                }

                // Auto-align all curves to same direction
                Vector3d refTan = curves[0].TangentAt(curves[0].Domain.T0);
                if (refTan.IsValid) refTan.Unitize();
                for (int ci = 1; ci < nCurves; ci++)
                {
                    Vector3d t = curves[ci].TangentAt(curves[ci].Domain.T0);
                    if (t.IsValid)
                    {
                        t.Unitize();
                        if (Vector3d.Multiply(refTan, t) < 0.0) curves[ci].Reverse();
                    }
                }

                // Curve lengths
                var lengths = new double[nCurves];
                for (int ci = 0; ci < nCurves; ci++)
                {
                    lengths[ci] = curves[ci].GetLength();
                    if (lengths[ci] <= tol)
                    {
                        Bail($"Branch {brPath}: curve [{ci}] is too short. Skipped."); return;
                    }
                }

                // -- Shell paths (Slicer Plus convention) + porosity accumulators -
                double branchPrintArea = 0.0;
                double branchTotalArea = 0.0;
                {
                    int    lastDi  = Math.Max(0, nCurves - 2);
                    double wShell0 = _wShellList.Count > 0 ? Math.Max(0.0, _wShellList[0 % _wShellList.Count]) : 0.0;
                    int    nShell0 = _nShellList.Count  > 0 ? Math.Max(1,   _nShellList[0 % _nShellList.Count])  : 1;
                    double wShellN = _wShellList.Count > 0 ? Math.Max(0.0, _wShellList[lastDi % _wShellList.Count]) : 0.0;
                    int    nShellN = _nShellList.Count  > 0 ? Math.Max(1,   _nShellList[lastDi % _nShellList.Count])  : 1;

                    if (_closeShell && wShell0 > tol && wShellN > tol)
                    {
                        int nS  = Math.Min(nShell0, nShellN);
                        var sp0 = BuildShellPaths(curves[0], curves[1], nS, wShell0, shellRes, tol, true);
                        var spN = BuildShellPaths(curves[nCurves - 1], curves[nCurves - 2], nS, wShellN, shellRes, tol, true);
                        foreach (var c in CloseShellPairs(sp0, spN, tol))
                        {
                            shells.Add((layerPath, c));
                            Interlocked.Increment(ref totalShells);
                        }
                    }
                    else
                    {
                        if (wShell0 > tol)
                        {
                            var sp0 = BuildShellPaths(curves[0], curves[1], nShell0, wShell0, shellRes, tol);
                            foreach (var sp in sp0) { shells.Add((layerPath, sp)); Interlocked.Increment(ref totalShells); }
                        }
                        if (wShellN > tol)
                        {
                            var spN = BuildShellPaths(curves[nCurves - 1], curves[nCurves - 2], nShellN, wShellN, shellRes, tol);
                            foreach (var sp in spN) { shells.Add((layerPath, sp)); Interlocked.Increment(ref totalShells); }
                        }
                    }
                    branchPrintArea += lengths[0] * wShell0 * nShell0;
                    branchPrintArea += lengths[nCurves - 1] * wShellN * nShellN;
                }

                // -- Inner guides as partitions — shortened by (w_shell × n_shell - w_shell / 3) --
                for (int ci = 1; ci < nCurves - 1; ci++)
                {
                    double wShP16 = _wShellList.Count > 0 ? Math.Max(0.0, _wShellList[ci % _wShellList.Count]) : 0.0;
                    int    nShP16 = _nShellList.Count  > 0 ? Math.Max(1,   _nShellList[ci % _nShellList.Count])  : 1;
                    double partShorten16 = Math.Max(0.0, wShP16 * nShP16 - wShP16 / 3.0);
                    Curve partCrv = curves[ci].DuplicateCurve();
                    if (partShorten16 > tol)
                        partCrv = TrimCurveEnds(partCrv, partShorten16, tol);
                    if (partCrv != null && partCrv.IsValid)
                        parts.Add((layerPath, partCrv));
                }

                // Normalised layer position [0..1] used by z-modulation in both engines
                double n01 = (branchCount <= 1) ? 0.0 : (double)bi / (double)(branchCount - 1);

                int localPaths = 0;

                // -- Domain loop ----------------------------------------------
                for (int di = 0; di < nDomains; di++)
                {
                    int    engine     = GetCycled(_typeList,       di);
                    double _wSh16 = _wShellList.Count > 0 ? Math.Max(0.0, _wShellList[di % _wShellList.Count]) : 0.0;
                    int    _nSh16 = _nShellList.Count  > 0 ? Math.Max(1,   _nShellList[di % _nShellList.Count])  : 1;
                    double dynDef16  = Math.Max(0.0, _wSh16 * _nSh16 - _wSh16 * 0.5);
                    double clearLong  = (_clearLongList.Count  == 0) ? dynDef16 : GetCycled(_clearLongList,  di);
                    double clearGuide = (_clearGuideList.Count == 0) ? dynDef16 : GetCycled(_clearGuideList, di);
                    double clearIn    = GetCycled(_clearInList,    di);
                    int    cx         = GetCycled(_cxList,         di);
                    int    cy         = GetCycled(_cyList,         di);
                    double czRaw      = GetCycled(_czList,         di);

                    // Apply outer vs inner inset to guide pair edges
                    double gapInsetA = (di == 0)            ? clearGuide : clearIn;
                    double gapInsetB = (di == nDomains - 1) ? clearGuide : clearIn;

                    // Accumulate domain total area for porosity (use unshortened guides)
                    {
                        double sumGap = 0.0;
                        int nSamp = 5;
                        for (int gi = 0; gi < nSamp; gi++)
                        {
                            double s = (double)gi / (nSamp - 1);
                            double tAg;
                            if (!curves[di].LengthParameter(s * lengths[di], out tAg))
                                tAg = curves[di].Domain.ParameterAt(s);
                            double tBg;
                            if (!curves[di + 1].LengthParameter(s * lengths[di + 1], out tBg))
                                tBg = curves[di + 1].Domain.ParameterAt(s);
                            sumGap += curves[di].PointAt(tAg).DistanceTo(curves[di + 1].PointAt(tBg));
                        }
                        branchTotalArea += (sumGap / nSamp) * lengths[di];
                    }

                    Curve cA = curves[di].DuplicateCurve();
                    Curve cB = curves[di + 1].DuplicateCurve();

                    if (clearLong > tol)
                    {
                        cA = TrimCurveEnds(cA, clearLong, tol);
                        cB = TrimCurveEnds(cB, clearLong, tol);
                        if (cA == null || cB == null)
                        {
                            log.AppendLine($"Branch {brPath} domain [{di}]: clear_long too large. Skipped.");
                            continue;
                        }
                    }

                    double lenA = cA.GetLength();
                    double lenB = cB.GetLength();
                    if (lenA <= tol || lenB <= tol)
                    {
                        log.AppendLine($"Branch {brPath} domain [{di}]: guide(s) too short after shorten. Skipped.");
                        continue;
                    }

                    GH_Path domPath = _trim
                        ? layerPath
                        : AppendToPath(brPath, di);

                    int made = 0;

                    // --------------------------------------------------------
                    // ENGINE 0 — Oct / Turtle Cells
                    // --------------------------------------------------------
                    if (engine == 0)
                    {
                        double pw    = GetCycled(_oPwList,    di);
                        double b0    = GetCycled(_oB0List,    di);
                        double b1    = GetCycled(_oB1List,    di);
                        double ext   = GetCycled(_oExtList,   di);
                        bool   teeth = GetCycled(_oTeethList, di);

                        // clear_in is clearance from intermediate partition guides.
                        // Oct internal band spacing remains automatic from path width.
                        double bandInset = pw * 0.5;

                        // Z-modulation: triangle-wave driven by czRaw
                        double wave        = TriangleWave01(n01, czRaw);
                        double bridgeParam = (branchCount <= 1) ? b0 : Lerp(b0, b1, wave);
                        bridgeParam = Clamp01(bridgeParam);

                        const double epsZ = 1e-12;
                        bool zAt0 = Math.Abs(b0) <= epsZ;
                        bool zAt1 = Math.Abs(b1) <= epsZ;
                        bool strictZero = (branchCount == 1)
                            ? (zAt0 || zAt1)
                            : (zAt0 && zAt1  && Math.Abs(bridgeParam) <= epsZ)
                           || (zAt0 && !zAt1 && Math.Abs(wave) <= epsZ)
                           || (!zAt0 && zAt1 && Math.Abs(1.0 - wave) <= epsZ);

                        // Internal geometry sizing (xa, xr, xClosed)
                        double xaNom = pw * 1.4;
                        double xrNom = pw * 1.1;
                        double xrFlr = pw * 1.1;
                        double xClNom = 2.0 * xaNom + xrNom;

                        double avgGap   = O_EstimateAverageGap(cA, cB, tol);
                        double gapIn    = Math.Max(0.0, avgGap - gapInsetA - gapInsetB);
                        double bandH    = gapIn / Math.Max(1, cy);
                        double usableH  = Math.Max(0.0, bandH - 2.0 * bandInset);
                        double ampNom   = Math.Max(0.0, usableH * 0.5 - bandInset);

                        double curveLen = lenA;
                        int    denom    = 4 * cx - 1;
                        double xClStrict = (denom > 0) ? curveLen / (double)denom : xClNom;
                        xClStrict = Math.Max(0.0, xClStrict);

                        double xaMaxSpacing = double.MaxValue;
                        if (ampNom > tol && xClStrict > tol)
                        {
                            double hyp = Math.Sqrt(xClStrict * xClStrict + ampNom * ampNom);
                            if (hyp > tol)
                            {
                                double sinT = ampNom / hyp;
                                if (sinT > tol) xaMaxSpacing = pw / sinT;
                            }
                        }

                        double xaMaxBrFlr  = Math.Max(0.0, 0.5 * (xClStrict - xrFlr));
                        double xaMaxBudget = Math.Max(0.0, 0.5 * xClStrict);
                        double xaStrict    = xaNom;
                        xaStrict = Math.Min(xaStrict, xaMaxBudget);
                        xaStrict = Math.Min(xaStrict, xaMaxSpacing);
                        if (xClStrict >= xrFlr - tol) xaStrict = Math.Min(xaStrict, xaMaxBrFlr);
                        xaStrict = Math.Max(0.0, xaStrict);

                        double blend;
                        if      (zAt0 && zAt1)  blend = 0.0;
                        else if (zAt0 && !zAt1) blend = wave;
                        else if (!zAt0 && zAt1) blend = 1.0 - wave;
                        else                    blend = 1.0;
                        blend = Clamp01(blend);
                        blend = blend * blend * (3.0 - 2.0 * blend);
                        if (strictZero) blend = 0.0;

                        double xClUsed = Lerp(xClStrict, xClNom,   blend);
                        double xaUsed  = Lerp(xaStrict,  xaNom,    blend);
                        double xrUsed  = Math.Max(0.0, xClUsed - 2.0 * xaUsed);
                        if (xrUsed < xrFlr - tol && xClUsed >= xrFlr - tol)
                        {
                            xrUsed = xrFlr;
                            xaUsed = Math.Max(0.0, 0.5 * (xClUsed - xrUsed));
                        }

                        double totalMin = (2.0 * cx - 1.0) * xClUsed;
                        double leftover = curveLen - totalMin;
                        if (leftover < -tol)
                        {
                            log.AppendLine($"Branch {brPath} domain [{di}] (Oct): curve too short for cx={cx}. Skipped.");
                            continue;
                        }
                        leftover = Math.Max(0.0, leftover);
                        double xy    = leftover / (2.0 * cx);
                        double xOpen = xClUsed + 2.0 * xy;

                        double yaAbs  = (ampNom <= 0.0) ? 0.0 : Math.Min(pw * 0.8, 0.5 * ampNom);
                        double yaN    = (ampNom <= tol)  ? 0.0 : Math.Max(0.0, Math.Min(1.0, yaAbs / ampNom));
                        double yaB    = teeth ? yaN : 0.0;
                        double yBrN   = teeth ? (yaN * 0.5 + bridgeParam * (1.0 - yaN)) : bridgeParam;

                        // Build 2-D cell geometry
                        var cells = new O_CellGeom[cx];
                        double cursor = 0.0;
                        for (int ci = 0; ci < cx; ci++)
                        {
                            double cs   = cursor;
                            double ce   = cs + xOpen;
                            double ctr  = 0.5 * (cs + ce);
                            double topSp = Lerp(xOpen,   xClUsed, bridgeParam);
                            double botSp = Lerp(xClUsed, xOpen,   bridgeParam);
                            double xtL   = ctr - 0.5 * topSp;
                            double xtR   = ctr + 0.5 * topSp;
                            double xbL   = ctr - 0.5 * botSp;
                            double xbR   = ctr + 0.5 * botSp;
                            double xy2Dx = 0.5 * (topSp - xClUsed);
                            if (Math.Abs(xy2Dx) < eps) xy2Dx = 0.0;
                            cells[ci] = new O_CellGeom
                            {
                                cell_start = cs, cell_end = ce,
                                xt_L = xtL, xt_R = xtR,
                                xb_L = xbL, xb_R = xbR,
                                xy2_dx = xy2Dx
                            };
                            cursor += xOpen;
                            if (ci < cx - 1) cursor += xClUsed;
                        }

                        // Build 2-D pattern point list
                        var localXY = new List<Point2d>(12 * cx + 4 * Math.Max(0, cx - 1));
                        O_AddUnique(localXY, cells[0].xb_L, 0.0, eps);
                        for (int ci = 0; ci < cx; ci++)
                        {
                            var c = cells[ci];
                            O_AddUnique(localXY, c.xb_L, yaB, eps);
                            O_AddUnique(localXY, c.xt_L, 1.0 - yaB, eps);
                            O_AddUnique(localXY, c.xt_L, 1.0, eps);
                            O_AddUnique(localXY, c.xt_L + xaUsed, 1.0, eps);
                            O_AddUnique(localXY, c.xt_L + xaUsed + c.xy2_dx, yBrN, eps);
                            O_AddUnique(localXY, c.xt_L + xaUsed + c.xy2_dx + xrUsed, yBrN, eps);
                            O_AddUnique(localXY, c.xt_L + xaUsed + c.xy2_dx + xrUsed + c.xy2_dx, 1.0, eps);
                            O_AddUnique(localXY, c.xt_R, 1.0, eps);
                            O_AddUnique(localXY, c.xt_R, 1.0 - yaB, eps);
                            O_AddUnique(localXY, c.xb_R, yaB, eps);
                            O_AddUnique(localXY, c.xb_R, 0.0, eps);

                            if (ci < cx - 1)
                            {
                                var n2   = cells[ci + 1];
                                double s0   = c.xb_R;
                                double sE   = n2.xb_L;
                                double dyN  = Math.Max(0.0, (1.0 - yaB) - yBrN);
                                double yTr  = yaB + dyN;
                                double span = Math.Max(eps, sE - s0);
                                double xaEf = xaUsed;
                                double xrEf = xrUsed;
                                double maxXa = 0.5 * Math.Max(0.0, span - xrEf);
                                if (xaEf > maxXa) xaEf = maxXa;
                                if (span < xrEf)  { xrEf = span; xaEf = 0.0; }
                                double xa1E  = s0 + xaEf;
                                double xa2S  = sE - xaEf;
                                double free  = span - 2.0 * xaEf;
                                double xrSt  = s0 + xaEf + 0.5 * Math.Max(0.0, free - xrEf);
                                double xrEn  = xrSt + xrEf;
                                O_AddUnique(localXY, xa1E, 0.0, eps);
                                O_AddUnique(localXY, xrSt, yTr, eps);
                                O_AddUnique(localXY, xrEn, yTr, eps);
                                O_AddUnique(localXY, xa2S, 0.0, eps);
                                O_AddUnique(localXY, sE,   0.0, eps);
                            }
                        }
                        for (int i = 0; i < localXY.Count; i++)
                            localXY[i] = new Point2d(Math.Max(0.0, Math.Min(curveLen, localXY[i].X)), localXY[i].Y);

                        // Map 2-D pattern into 3-D bands
                        for (int ri = 0; ri < cy; ri++)
                        {
                            double f0 = (double)ri       / (double)cy;
                            double f1 = (double)(ri + 1) / (double)cy;
                            var pLow = new List<Point3d>(localXY.Count);
                            var pHi  = new List<Point3d>(localXY.Count);

                            for (int i = 0; i < localXY.Count; i++)
                            {
                                double si = localXY[i].X;
                                double yN = localXY[i].Y;

                                double tAp;
                                if (!cA.LengthParameter(si, out tAp))
                                    tAp = cA.Domain.ParameterAt((curveLen <= tol) ? 0.0 : si / curveLen);
                                Point3d pA3 = cA.PointAt(tAp);

                                double uLen = (curveLen <= tol) ? 0.0 : si / curveLen;
                                double tBp;
                                if (!cB.LengthParameter(uLen * lenB, out tBp))
                                    tBp = cB.Domain.ParameterAt(uLen);
                                Point3d pB3 = cB.PointAt(tBp);

                                Vector3d vAB = pB3 - pA3;
                                double gap3  = vAB.Length;
                                if (gap3 <= tol) { pLow.Add(pA3); pHi.Add(pA3); continue; }
                                vAB.Unitize();

                                double maxInset = Math.Max(0.0, 0.5 * gap3 - eps);
                                double insA = Math.Max(0.0, Math.Min(gapInsetA, maxInset));
                                double insB = Math.Max(0.0, Math.Min(gapInsetB, maxInset));
                                Point3d pAIn = pA3 + vAB * insA;
                                double gIn  = Math.Max(0.0, gap3 - insA - insB);
                                if (gIn <= tol) { pLow.Add(pAIn); pHi.Add(pAIn); continue; }

                                double abs0 = f0 * gIn + bandInset;
                                double abs1 = f1 * gIn - bandInset;
                                if (abs0 >= abs1 - eps)
                                {
                                    Point3d pMid = pAIn + vAB * (0.5 * (f0 + f1) * gIn);
                                    pLow.Add(pMid); pHi.Add(pMid); continue;
                                }
                                double bGap   = abs1 - abs0;
                                double ampBnd = Math.Max(0.0, bGap * 0.5 - bandInset);
                                double off    = yN * ampBnd;
                                pLow.Add(pAIn + vAB * abs0 + vAB * off);
                                pHi .Add(pAIn + vAB * abs1 - vAB * off);
                            }

                            O_ExtendPathEnds(pLow, ext, tol);
                            O_ExtendPathEnds(pHi,  ext, tol);

                            // Porosity: accumulate print area for both paths in this band
                            double pLowLen = 0.0; for (int _pi = 1; _pi < pLow.Count; _pi++) pLowLen += pLow[_pi].DistanceTo(pLow[_pi - 1]);
                            double pHiLen  = 0.0; for (int _pi = 1; _pi < pHi.Count;  _pi++) pHiLen  += pHi [_pi].DistanceTo(pHi [_pi - 1]);
                            branchPrintArea += (pLowLen + pHiLen) * pw;

                            GH_Path pathL = _trim ? domPath : AppendBandPath(brPath, di, ri, 0);
                            GH_Path pathU = _trim ? domPath : AppendBandPath(brPath, di, ri, 1);

                            crvs.Add((pathL, new PolylineCurve(new Polyline(pLow))));
                            crvs.Add((pathU, new PolylineCurve(new Polyline(pHi))));
                            for (int p = 0; p < pLow.Count; p++) pts.Add((pathL, pLow[p]));
                            for (int p = 0; p < pHi.Count;  p++) pts.Add((pathU, pHi[p]));
                            made += 2;
                        }

                        log.AppendLine(
                            $"Branch {brPath} domain [{di}] (Oct): cx={cx} cy={cy} cz={czRaw:0.##} " +
                            $"pW={pw:0.##} b0={b0:0.##} b1={b1:0.##} blend={blend:0.###} " +
                            $"strict={strictZero} gapInsetA={gapInsetA:0.###} gapInsetB={gapInsetB:0.###} bandInsetAuto={bandInset:0.###} ext={ext:0.##} " +
                            $"teeth={teeth} ? {made} path(s).");
                    }

                    // --------------------------------------------------------
                    // ENGINE 1 — TPMS iso-contours
                    // --------------------------------------------------------
                    else if (engine == 1)
                    {
                        int    pType  = Clamp(GetCycled(_pTypeList,  di), 0, 7);
                        double pLevel = GetCycled(_pLevelList, di);
                        double pPx    = GetCycled(_pPxList,    di);
                        double pPy    = GetCycled(_pPyList,    di);
                        double pPz    = GetCycled(_pPzList,    di);

                        // count_z rounded to int for TPMS z-period count (min 1)
                        int pCz = Math.Max(1, (int)Math.Round(czRaw));

                        double TWO_PI = 2.0 * Math.PI;

                        // Grid dimensions from spacing
                        int nx = Clamp((int)Math.Round(lenA / _pSpacing), TPMS_GRID_MIN, TPMS_GRID_MAX);
                        double avgGap2   = P_EstimateAverageGap(cA, cB, lenA, lenB);
                        double usableGap = Math.Max(0.0, avgGap2 - gapInsetA - gapInsetB);
                        int ny = Clamp((int)Math.Round(usableGap / _pSpacing), TPMS_GRID_MIN, TPMS_GRID_MAX);

                        // Pre-cache arc-length parameters for both guides
                        var paramsA = new double[nx + 1];
                        var paramsB = new double[nx + 1];
                        for (int ix = 0; ix <= nx; ix++)
                        {
                            double s01 = (double)ix / (double)nx;
                            if (!cA.LengthParameter(s01 * lenA, out paramsA[ix])) paramsA[ix] = cA.Domain.ParameterAt(s01);
                            if (!cB.LengthParameter(s01 * lenB, out paramsB[ix])) paramsB[ix] = cB.Domain.ParameterAt(s01);
                        }

                        // Evaluate TPMS field on 3-D grid
                        var F  = new double[ny + 1, nx + 1];
                        var P3 = new Point3d[ny + 1, nx + 1];

                        for (int iy = 0; iy <= ny; iy++)
                        {
                            double t01    = (double)iy / (double)ny;
                            double yPhase = TWO_PI * cy    * (t01 + pPy);
                            double zPhase = TWO_PI * pCz   * (n01 + pPz);

                            for (int ix = 0; ix <= nx; ix++)
                            {
                                double s01  = (double)ix / (double)nx;
                                Point3d pAt = cA.PointAt(paramsA[ix]);
                                Point3d pBt = cB.PointAt(paramsB[ix]);
                                Vector3d vAB = pBt - pAt;
                                double gap  = vAB.Length;
                                if (gap > tol)
                                {
                                    vAB.Unitize();
                                    double iA = Math.Min(gapInsetA, Math.Max(0.0, gap * 0.5 - eps));
                                    double iB = Math.Min(gapInsetB, Math.Max(0.0, gap * 0.5 - eps));
                                    pAt = pAt + vAB * iA;
                                    pBt = pBt - vAB * iB;
                                }
                                P3[iy, ix] = pAt + (pBt - pAt) * t01;
                                double xPhase = TWO_PI * cx * (s01 + pPx);
                                F [iy, ix]  = TPMS_Value(pType, xPhase, yPhase, zPhase) - pLevel;
                            }
                        }

                        // Marching squares
                        var segments = new List<(Point3d A, Point3d B)>();
                        for (int iy = 0; iy < ny; iy++)
                        {
                            for (int ix = 0; ix < nx; ix++)
                            {
                                double f0 = F[iy, ix],     f1 = F[iy, ix + 1];
                                double f2 = F[iy+1, ix+1], f3 = F[iy+1, ix];
                                Point3d p0 = P3[iy, ix],     p1 = P3[iy, ix + 1];
                                Point3d p2 = P3[iy+1, ix+1], p3 = P3[iy+1, ix];
                                int code = (f0>0?1:0)|(f1>0?2:0)|(f2>0?4:0)|(f3>0?8:0);
                                if (code == 0 || code == 15) continue;

                                Point3d EP(double fa, double fb, Point3d pa, Point3d pb)
                                {
                                    double d  = fb - fa;
                                    double tt = Math.Abs(d) < 1e-14 ? 0.5 : -fa / d;
                                    tt = tt < 0 ? 0 : tt > 1 ? 1 : tt;
                                    return pa + (pb - pa) * tt;
                                }

                                Point3d eB, eR, eT, eL;
                                switch (code)
                                {
                                    case  1: case 14: eB=EP(f0,f1,p0,p1); eL=EP(f0,f3,p0,p3); segments.Add((eB,eL)); break;
                                    case  2: case 13: eB=EP(f0,f1,p0,p1); eR=EP(f1,f2,p1,p2); segments.Add((eB,eR)); break;
                                    case  3: case 12: eL=EP(f0,f3,p0,p3); eR=EP(f1,f2,p1,p2); segments.Add((eL,eR)); break;
                                    case  4: case 11: eR=EP(f1,f2,p1,p2); eT=EP(f3,f2,p3,p2); segments.Add((eR,eT)); break;
                                    case 5:
                                        eB = EP(f0, f1, p0, p1);
                                        eR = EP(f1, f2, p1, p2);
                                        eT = EP(f3, f2, p3, p2);
                                        eL = EP(f0, f3, p0, p3);
                                        segments.Add((eB, eL));
                                        segments.Add((eR, eT));
                                        break;
                                    case  6: case  9: eB=EP(f0,f1,p0,p1); eT=EP(f3,f2,p3,p2); segments.Add((eB,eT)); break;
                                    case  7: case  8: eL=EP(f0,f3,p0,p3); eT=EP(f3,f2,p3,p2); segments.Add((eL,eT)); break;
                                    case 10:
                                        eB = EP(f0, f1, p0, p1);
                                        eR = EP(f1, f2, p1, p2);
                                        eT = EP(f3, f2, p3, p2);
                                        eL = EP(f0, f3, p0, p3);
                                        segments.Add((eB, eR));
                                        segments.Add((eT, eL));
                                        break;
                                }
                            }
                        }

                        // Chain segments into polylines
                        int ci2 = 0;
                        foreach (var chain in TPMS_ChainSegments(segments, tol))
                        {
                            if (chain == null || chain.Count < _pMinPts) continue;
                            if (!TryMakeValidPolyline(chain, tol, out Polyline pl)) continue;
                            pl.CollapseShortSegments(tol);
                            if (!pl.IsValid || pl.Count < 2 || pl.Length <= tol) continue;
                            var plc     = new PolylineCurve(pl);
                            GH_Path cp  = _trim ? domPath : AppendToPath(domPath, ci2);
                            crvs.Add((cp, plc));
                            for (int p = 0; p < pl.Count; p++) pts.Add((cp, pl[p]));
                            branchPrintArea += pl.Length * _pSpacing;
                            ci2++; made++;
                        }

                        log.AppendLine(
                            $"Branch {brPath} domain [{di}] (TPMS): grid {nx}×{ny} " +
                            $"type={TPMS_Tag(pType)} cx={cx} cy={cy} cz={pCz} " +
                            $"level={pLevel:0.###} ? {made} contour(s).");
                    }
                    else
                    {
                        log.AppendLine($"Branch {brPath} domain [{di}]: unknown infill_type={engine}. Skipped.");
                        continue;
                    }

                    localPaths += made;
                    Interlocked.Add(ref totalPaths, made);
                }

                if (localPaths == 0)
                    log.AppendLine($"Branch {brPath}: no paths generated.  Check guides and parameters.");
                else
                    log.AppendLine($"Branch {brPath}: {nCurves} guides ? {nDomains} domains ? {localPaths} path(s).");

                perBranchPor[bi] = branchTotalArea > 0.0
                    ? Math.Max(0.0, Math.Min(1.0, 1.0 - branchPrintArea / branchTotalArea))
                    : 0.0;
                perShells[bi] = shells; perParts[bi] = parts;
                perCrvs[bi]   = crvs;   perPts[bi]  = pts;
                perLog[bi]    = log.ToString();
            };

            // -- Dispatch (parallel above threshold) --------------------------
            if (branchCount < PARALLEL_THRESHOLD)
            {
                for (int bi = 0; bi < branchCount; bi++) processBranch(bi);
            }
            else
            {
                var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) };
                Parallel.For(0, branchCount, po, bi => processBranch(bi));
            }

            // -- Assemble outputs ---------------------------------------------
            var outShells = new DataTree<Curve>();
            var outParts  = new DataTree<Curve>();
            var outCrvs   = new DataTree<Curve>();
            var outPts    = new DataTree<Point3d>();
            var outPlanes = new DataTree<Plane>();
            var porLayer  = new List<double>(branchCount);
            int nonPlanarLayers = 0;
            double maxLayerPlaneDeviation = 0.0;

            for (int bi = 0; bi < branchCount; bi++)
            {
                GH_Path layerPath = trimPaths ? new GH_Path(bi) : guideTree.Paths[bi];
                Plane layerPlane = WasperLayerPlaneTools.EstimateLayerPlaneFromGhCurves(guideTree.Branches[bi], tol);
                double layerPlaneDev = WasperLayerPlaneTools.MaxDeviationFromPlane(guideTree.Branches[bi], layerPlane);
                maxLayerPlaneDeviation = Math.Max(maxLayerPlaneDeviation, layerPlaneDev);
                if (layerPlaneDev > WasperLayerPlaneTools.PlanarityWarningTolerance(tol))
                    nonPlanarLayers++;
                outPlanes.Add(layerPlane, layerPath);
                foreach (var item in perShells[bi]) outShells.Add(item.crv, item.path);
                foreach (var item in perParts [bi]) outParts .Add(item.crv, item.path);
                foreach (var item in perCrvs  [bi]) outCrvs  .Add(item.crv, item.path);
                foreach (var item in perPts   [bi]) outPts   .Add(item.pt,  item.path);
                porLayer.Add(perBranchPor[bi]);
            }
            double porAvg = porLayer.Count > 0 ? porLayer.Average() : 0.0;

            var infoSb = new StringBuilder();
            infoSb.AppendLine($"wsp_In17_Multi-Infill 3D  {_versionTag}  (Oct + TPMS)");
            infoSb.AppendLine("------------------------------------------------------");
            infoSb.AppendLine($"branches_in  : {branchCount}");
            infoSb.AppendLine($"skipped      : {totalSkipped}");
            infoSb.AppendLine($"shells_made  : {totalShells}");
            infoSb.AppendLine($"paths_made   : {totalPaths}");
            infoSb.AppendLine($"non_planar_layers : {nonPlanarLayers}");
            infoSb.AppendLine($"max_plane_deviation: {maxLayerPlaneDeviation:0.###}");
            infoSb.AppendLine($"trim_paths   : {trimPaths}");
            infoSb.AppendLine($"close_shell  : {closeShell}");
            infoSb.AppendLine($"infill_type  : [{string.Join(", ", typeList)}]  ({typeTag})");
            infoSb.AppendLine($"por_avg      : {porAvg:0.0000}");
            infoSb.AppendLine(branchCount < PARALLEL_THRESHOLD
                ? $"parallel     : OFF (< {PARALLEL_THRESHOLD} branches)"
                : $"parallel     : ON  (max {Math.Max(1, Environment.ProcessorCount - 1)} threads)");
            infoSb.AppendLine("------------------------------------------------------");
            for (int bi = 0; bi < branchCount; bi++)
                if (!string.IsNullOrEmpty(perLog[bi])) infoSb.Append(perLog[bi]);

            DA.SetDataTree(0, outShells);
            DA.SetDataTree(1, outCrvs);
            DA.SetDataTree(2, outParts);
            DA.SetDataTree(3, outPts);
            DA.SetData    (4, infoSb.ToString());
            DA.SetDataList(5, porLayer);
            DA.SetData    (6, porAvg);
            DA.SetDataTree(7, outPlanes);
        }

        // ====================================================================
        // SHARED HELPERS
        // ====================================================================

        private static GH_Path AppendToPath(GH_Path basePath, int index)
        {
            int[] bi = basePath.Indices;
            int[] idx = new int[bi.Length + 1];
            for (int i = 0; i < bi.Length; i++) idx[i] = bi[i];
            idx[bi.Length] = index;
            return new GH_Path(idx);
        }

        private static GH_Path AppendBandPath(GH_Path basePath, int domain, int band, int which)
        {
            int[] bi = basePath.Indices;
            int[] idx = new int[bi.Length + 3];
            for (int i = 0; i < bi.Length; i++) idx[i] = bi[i];
            idx[bi.Length]     = domain;
            idx[bi.Length + 1] = band;
            idx[bi.Length + 2] = which;
            return new GH_Path(idx);
        }

        /// <summary>
        /// Pairs open shell polylines from two opposite outermost guides into closed loops.
        /// side-0 forward ? end cap ? side-N reversed ? start cap ? close.
        /// Produces Math.Min(side0.Count, sideN.Count) closed curves.
        /// Each successive loop is shorter because it sits further inward (higher shell index).
        /// </summary>
        private static List<PolylineCurve> CloseShellPairs(List<PolylineCurve> side0, List<PolylineCurve> sideN, double tol)
        {
            var result = new List<PolylineCurve>();
            int n = Math.Min(side0.Count, sideN.Count);
            for (int i = 0; i < n; i++)
            {
                var poly0 = side0[i].ToPolyline();
                var polyN = sideN[i].ToPolyline();
                if (poly0 == null || polyN == null) continue;
                var pts0 = new List<Point3d>(poly0);
                var ptsN = new List<Point3d>(polyN);
                if (pts0.Count < 2 || ptsN.Count < 2) continue;

                bool reverseN = pts0[pts0.Count - 1].DistanceTo(ptsN[ptsN.Count - 1]) <=
                                pts0[pts0.Count - 1].DistanceTo(ptsN[0]);

                var combined = new List<Point3d>(pts0.Count + ptsN.Count + 1);
                combined.AddRange(pts0);
                if (reverseN)
                    for (int k = ptsN.Count - 1; k >= 0; k--) combined.Add(ptsN[k]);
                else
                    combined.AddRange(ptsN);
                combined.Add(pts0[0]);

                if (combined.Count >= 3)
                    result.Add(new PolylineCurve(combined));
            }
            return result;
        }

        /// <summary>
        /// Builds nShell open polylines offset inward from outerGuide toward innerGuide.
        /// Shell line i (i = 1 … nShell) is centred at (i - 0.5) × wShell from outerGuide
        /// (Slicer Plus convention).
        /// </summary>
        private static List<PolylineCurve> BuildShellPaths(
            Curve outerGuide, Curve innerGuide,
            int nShell, double wShell,
            double sampleRes, double tol,
            bool insetEnds = false)
        {
            var result = new List<PolylineCurve>(nShell);
            if (outerGuide == null || !outerGuide.IsValid ||
                innerGuide == null || !innerGuide.IsValid ||
                nShell < 1 || wShell <= tol) return result;

            double lenO = outerGuide.GetLength();
            double lenI = innerGuide.GetLength();
            if (lenO <= tol || lenI <= tol) return result;
            int    n    = ShellSampleCount(lenO, lenI, sampleRes, tol);

            for (int si = 1; si <= nShell; si++)
            {
                double offset = (si - 0.5) * wShell;
                var pts = new List<Point3d>(n);

                for (int j = 0; j < n; j++)
                {
                    double u = (n == 1) ? 0.5 : (double)j / (double)(n - 1);

                    double sO = u * lenO;
                    double sI = u * lenI;
                    if (insetEnds)
                    {
                        double endO = Math.Min(offset, Math.Max(0.0, 0.5 * lenO - tol));
                        double endI = Math.Min(offset, Math.Max(0.0, 0.5 * lenI - tol));
                        sO = endO + u * Math.Max(0.0, lenO - 2.0 * endO);
                        sI = endI + u * Math.Max(0.0, lenI - 2.0 * endI);
                    }

                    double tO;
                    if (!outerGuide.LengthParameter(sO, out tO))
                        tO = outerGuide.Domain.ParameterAt(u);
                    Point3d pO = outerGuide.PointAt(tO);

                    double tI;
                    if (!innerGuide.LengthParameter(sI, out tI))
                        tI = innerGuide.Domain.ParameterAt(u);
                    Point3d pI = innerGuide.PointAt(tI);

                    Vector3d vOI = pI - pO;
                    double   gap = vOI.Length;
                    if (gap <= tol) { pts.Add(pO); continue; }
                    vOI.Unitize();
                    pts.Add(pO + vOI * Math.Min(offset, gap));
                }

                if (pts.Count >= 2)
                    result.Add(new PolylineCurve(new Polyline(pts)));
            }

            return result;
        }

        private static int ShellSampleCount(double lenA, double lenB, double sampleRes, double tol)
        {
            double len = Math.Max(lenA, lenB);
            if (len <= tol) return 2;

            double res = sampleRes;
            if (double.IsNaN(res) || double.IsInfinity(res) || res <= tol)
                res = Math.Max(tol * 10.0, len / 63.0);

            return Math.Max(8, (int)Math.Ceiling(len / res) + 1);
        }

        private static Curve TrimCurveEnds(Curve crv, double amount, double tol)
        {
            double len = crv.GetLength();
            if (len <= 2.0 * amount + tol) return null;
            double t0, t1;
            if (!crv.LengthParameter(amount, out t0))       t0 = crv.Domain.ParameterAt(amount / len);
            if (!crv.LengthParameter(len - amount, out t1)) t1 = crv.Domain.ParameterAt((len - amount) / len);
            if (t1 <= t0 + 1e-12) return null;
            return crv.Trim(t0, t1);
        }

        private static bool TryMakeValidPolyline(List<Point3d> pts, double tol, out Polyline pl)
        {
            pl = new Polyline();
            if (pts == null || pts.Count < 2) return false;
            var dedup = new List<Point3d>(pts.Count);
            dedup.Add(pts[0]);
            for (int i = 1; i < pts.Count; i++)
                if (pts[i].IsValid && pts[i].DistanceTo(dedup[dedup.Count - 1]) > tol)
                    dedup.Add(pts[i]);
            if (dedup.Count < 2) return false;
            pl = new Polyline(dedup);
            return pl.IsValid && pl.Count >= 2 && pl.Length > tol;
        }

        private static void EnsureDefault<T>(List<T> list, T def)
        {
            if (list != null && list.Count == 0) list.Add(def);
        }

        private static T GetCycled<T>(List<T> list, int index)
        {
            return list[index % list.Count];
        }

        private static int    Clamp(int v, int lo, int hi)       => v < lo ? lo : v > hi ? hi : v;
        private static double Clamp01(double x)                   => x < 0.0 ? 0.0 : x > 1.0 ? 1.0 : x;
        private static double Lerp(double a, double b, double t)  => a + (b - a) * t;

        private static double TriangleWave01(double t01, double cycles)
        {
            if (cycles <= 0.0) return 0.0;
            double f = (t01 * cycles) - Math.Floor(t01 * cycles);
            return Clamp01(1.0 - Math.Abs(2.0 * f - 1.0));
        }

        private static string BuildListTag<T>(List<T> list, Func<T, string> fmt)
        {
            if (list == null || list.Count == 0) return "-";
            if (list.Count == 1) return fmt(list[0]);
            var sb = new StringBuilder("[");
            for (int i = 0; i < list.Count; i++) { if (i > 0) sb.Append(", "); sb.Append(fmt(list[i])); }
            sb.Append("]");
            return sb.ToString();
        }

        // -- Input param helpers -----------------------------------------------

        private static void AddNumList(GH_InputParamManager pm, string name, string nick, string desc, double def)
        {
            var p = new Param_Number
            {
                Name = name, NickName = nick, Description = desc,
                Access = GH_ParamAccess.list, Optional = true
            };
            p.PersistentData.Append(new GH_Number(def));
            pm.AddParameter(p);
        }

        private static void AddIntList(GH_InputParamManager pm, string name, string nick, string desc, int def)
        {
            var p = new Param_Integer
            {
                Name = name, NickName = nick, Description = desc,
                Access = GH_ParamAccess.list, Optional = true
            };
            p.PersistentData.Append(new GH_Integer(def));
            pm.AddParameter(p);
        }

        private static void AddBoolList(GH_InputParamManager pm, string name, string nick, string desc, bool def)
        {
            var p = new Param_Boolean
            {
                Name = name, NickName = nick, Description = desc,
                Access = GH_ParamAccess.list, Optional = true
            };
            p.PersistentData.Append(new GH_Boolean(def));
            pm.AddParameter(p);
        }

        private static void AddBoolItem(GH_InputParamManager pm, string name, string nick, string desc, bool def)
        {
            var p = new Param_Boolean
            {
                Name = name, NickName = nick, Description = desc,
                Access = GH_ParamAccess.item, Optional = true
            };
            p.PersistentData.Append(new GH_Boolean(def));
            pm.AddParameter(p);
        }

        // ====================================================================
        // OCT ENGINE HELPERS
        // ====================================================================

        private struct O_CellGeom
        {
            public double cell_start, cell_end;
            public double xt_L, xt_R, xb_L, xb_R, xy2_dx;
        }

        private static double O_EstimateAverageGap(Curve bottom, Curve top, double tol)
        {
            double lenB = bottom.GetLength(), lenT = top.GetLength();
            if (lenB <= tol || lenT <= tol) return 0.0;
            double[] u   = { 0.0, 0.25, 0.5, 0.75, 1.0 };
            double sum = 0.0;
            foreach (double ui in u)
            {
                double tB, tT;
                if (!bottom.LengthParameter(ui * lenB, out tB)) tB = bottom.Domain.ParameterAt(ui);
                if (!top   .LengthParameter(ui * lenT, out tT)) tT = top   .Domain.ParameterAt(ui);
                sum += bottom.PointAt(tB).DistanceTo(top.PointAt(tT));
            }
            return sum / u.Length;
        }

        private static void O_AddUnique(List<Point2d> pts, double x, double y, double tol)
        {
            Point2d p = new Point2d(x, y);
            if (pts.Count == 0 || pts[pts.Count - 1].DistanceTo(p) > tol) pts.Add(p);
        }

        private static void O_ExtendPathEnds(List<Point3d> pts, double len, double tol)
        {
            if (pts == null || pts.Count < 2 || len <= tol) return;
            Vector3d vs = pts[0] - pts[1];
            if (vs.IsValid && vs.Length > tol) { vs.Unitize(); pts[0] = pts[0] + vs * len; }
            int last = pts.Count - 1;
            Vector3d ve = pts[last] - pts[last - 1];
            if (ve.IsValid && ve.Length > tol) { ve.Unitize(); pts[last] = pts[last] + ve * len; }
        }

        // ====================================================================
        // TPMS ENGINE HELPERS
        // ====================================================================

        private static double P_EstimateAverageGap(Curve bottom, Curve top, double lenB, double lenT)
        {
            double[] u = { 0.0, 0.25, 0.5, 0.75, 1.0 };
            double sum = 0.0; int n = 0;
            foreach (double uu in u)
            {
                double tB, tT;
                if (!bottom.LengthParameter(uu * lenB, out tB)) tB = bottom.Domain.ParameterAt(uu);
                if (!top   .LengthParameter(uu * lenT, out tT)) tT = top   .Domain.ParameterAt(uu);
                sum += bottom.PointAt(tB).DistanceTo(top.PointAt(tT)); n++;
            }
            return n > 0 ? sum / n : 0.0;
        }

        private static double TPMS_Value(int type, double x, double y, double z)
        {
            switch (type)
            {
                case 0: return Math.Cos(x) + Math.Cos(y) + Math.Cos(z);
                case 1: return Math.Sin(x)*Math.Sin(y)*Math.Sin(z) + Math.Sin(x)*Math.Cos(y)*Math.Cos(z) + Math.Cos(x)*Math.Sin(y)*Math.Cos(z) + Math.Cos(x)*Math.Cos(y)*Math.Sin(z);
                case 2: return Math.Sin(x)*Math.Cos(y) + Math.Sin(y)*Math.Cos(z) + Math.Sin(z)*Math.Cos(x);
                case 3: return -2.0*(Math.Cos(x)*Math.Cos(y)+Math.Cos(y)*Math.Cos(z)+Math.Cos(z)*Math.Cos(x)) + (Math.Cos(2*x)+Math.Cos(2*y)+Math.Cos(2*z));
                case 4: return 3.0*(Math.Cos(x)+Math.Cos(y)+Math.Cos(z)) + 4.0*Math.Cos(x)*Math.Cos(y)*Math.Cos(z);
                case 5: return 0.5*(Math.Sin(2*x)*Math.Cos(y)*Math.Sin(z)+Math.Sin(2*y)*Math.Cos(z)*Math.Sin(x)+Math.Sin(2*z)*Math.Cos(x)*Math.Sin(y)) - 0.5*(Math.Cos(2*x)*Math.Cos(2*y)+Math.Cos(2*y)*Math.Cos(2*z)+Math.Cos(2*z)*Math.Cos(2*x));
                case 6: return Math.Sin(x)*Math.Cos(y)*Math.Cos(2*z)+Math.Cos(2*x)*Math.Sin(y)*Math.Cos(z)+Math.Cos(x)*Math.Cos(2*y)*Math.Sin(z);
                case 7: return Math.Sin(x)*Math.Sin(y)*Math.Sin(z)+Math.Cos(x)*Math.Cos(y)*Math.Cos(z)+Math.Sin(2*x)*Math.Sin(y)+Math.Cos(x)*Math.Sin(2*y)+Math.Sin(x)*Math.Sin(2*z)+Math.Sin(2*x)*Math.Cos(z)+Math.Sin(2*y)*Math.Sin(z)+Math.Cos(y)*Math.Sin(2*z);
                default: return 0.0;
            }
        }

        private static string TPMS_Tag(int t)
        {
            switch (t)
            {
                case 0: return "Prim";  case 1: return "Diam";  case 2: return "Gyr";
                case 3: return "IWP";   case 4: return "Neo";   case 5: return "Lidi";
                case 6: return "FK-S";  case 7: return "FK-Y";  default: return "?";
            }
        }

        private static List<List<Point3d>> TPMS_ChainSegments(List<(Point3d A, Point3d B)> segs, double tol)
        {
            var result = new List<List<Point3d>>();
            if (segs == null || segs.Count == 0) return result;

            double invTol = 1.0 / Math.Max(tol, 1e-12);
            long Key(Point3d p)
            {
                long xi = (long)Math.Round(p.X * invTol);
                long yi = (long)Math.Round(p.Y * invTol);
                long zi = (long)Math.Round(p.Z * invTol);
                unchecked { long h = xi * 1000003L + yi; h = h * 1000003L + zi; return h; }
            }

            var endMap = new Dictionary<long, List<int>>(segs.Count * 2);
            void Register(int idx, Point3d pt)
            {
                long k = Key(pt);
                if (!endMap.TryGetValue(k, out var lst)) endMap[k] = lst = new List<int>(2);
                lst.Add(idx);
            }
            void Unregister(int idx, Point3d pt)
            {
                long k = Key(pt);
                if (endMap.TryGetValue(k, out var lst)) { lst.Remove(idx); if (lst.Count == 0) endMap.Remove(k); }
            }

            var alive = new bool[segs.Count];
            for (int i = 0; i < segs.Count; i++) { alive[i] = true; Register(i, segs[i].A); Register(i, segs[i].B); }

            int FindNeighbour(Point3d pt)
            {
                long k = Key(pt);
                if (!endMap.TryGetValue(k, out var lst)) return -1;
                for (int j = lst.Count - 1; j >= 0; j--)
                {
                    int idx = lst[j];
                    if (!alive[idx]) { lst.RemoveAt(j); continue; }
                    return idx;
                }
                return -1;
            }

            for (int i = 0; i < segs.Count; i++)
            {
                if (!alive[i]) continue;
                var chain = new List<Point3d> { segs[i].A, segs[i].B };
                alive[i] = false; Unregister(i, segs[i].A); Unregister(i, segs[i].B);
                bool extended = true;
                while (extended)
                {
                    extended = false;
                    Point3d tail = chain[chain.Count - 1];
                    int ni = FindNeighbour(tail);
                    if (ni >= 0)
                    {
                        Point3d a = segs[ni].A, b = segs[ni].B;
                        alive[ni] = false; Unregister(ni, a); Unregister(ni, b);
                        chain.Add(tail.DistanceToSquared(a) < tail.DistanceToSquared(b) ? b : a);
                        extended = true;
                    }
                    Point3d head = chain[0];
                    ni = FindNeighbour(head);
                    if (ni >= 0)
                    {
                        Point3d a = segs[ni].A, b = segs[ni].B;
                        alive[ni] = false; Unregister(ni, a); Unregister(ni, b);
                        chain.Insert(0, head.DistanceToSquared(a) < head.DistanceToSquared(b) ? b : a);
                        extended = true;
                    }
                }
                result.Add(chain);
            }
            return result;
        }
    }
}
