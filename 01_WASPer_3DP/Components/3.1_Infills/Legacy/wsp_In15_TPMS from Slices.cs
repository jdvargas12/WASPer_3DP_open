#region Component Description
/*
	Component Name:
		wsp_In12_TPMS from Slices

	Nickname:
		TPMS_Slice

	Version:
		v1.0.6 - 260420

	Category / Subcategory:
		WASPer_3DP / 2_Infills

	Description:
		Generates TPMS iso-contour polylines between N guide curves (>= 2) per layer.
		Each consecutive pair [i]/[i+1] is a "domain" and runs an independent TPMS field.
		Grid resolution is driven by sample_spacing (model units).

		Multi-curve / multi-domain inputs:
			tpms_type, level, count_x, count_y, count_z, phase_x, phase_y, phase_z
			accept a LIST — one value per domain. If the list is shorter than the
			number of domains the values cycle. A single value is treated as a global
			constant across all domains.

		inset vs clear:
			inset_guide	: gap inset applied only to the two OUTERMOST curves [0] and [-1].
			clear			: gap inset applied to every INTERMEDIATE curve ([1]..[n-2]).
						  This effectively creates a clearance gap around interior guides.

		trim_layers (bool, default true):
			false	original topology:	{layer ; domain ; path}
			true	flat topology:		{layer}  (one branch per layer, all contours listed)

		shell output (always {layer}, one per layer):
			Closed polyline built from the endpoints of the two outermost guide curves
			(curves[0] and curves[-1]), before any shortening is applied.

		Defaults:
			All inputs except guide_curves are optional.
			When a list input is left empty, a single default value is inserted and cycled.
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Rhino;
using Rhino.Geometry;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
#endregion

namespace WASPer_3DP.Components._3_1_Infills
{
    public class wsp_In15_TPMS_from_Slices_260420 : GH_Component
    {
        private readonly string _versionTag;
        private const int PARALLEL_THRESHOLD = 8;

        public wsp_In15_TPMS_from_Slices_260420()
            : base(
                "wsp_In15_TPMS Cells from Slices",
                "TPMS_Slice",
                "Generates TPMS iso-contour polylines between N guide curves (>= 2) per layer.\n" +
                "Each consecutive pair [i] / [i+1] is an independent TPMS domain.\n\n" +
                "Per-domain list inputs (cycle if shorter than domain count):\n" +
                "  tpms_type, level, count_x, count_y, count_z, phase_x, phase_y, phase_z\n\n" +
                "Other optional inputs:\n" +
                "  shorten, inset, clear, sample_spacing, min_pts, trim_layers\n\n" +
                "inset  : applied to the two OUTERMOST curves [0] and [-1] only.\n" +
                "clear  : applied to all INTERMEDIATE curves ([1]..[n-2]).\n\n" +
                "trim_layers (bool, default true):\n" +
                "  false  {layer ; domain ; path}\n" +
                "  true   {layer}  (flat: one branch per layer, all contours listed)\n\n" +
                "shell output: closed polyline from outermost guide endpoints, always {layer}.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "3.1_Infills")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("5A4F3C1F-3E2D-4D57-A6B4-05F9B7B1F3EA");

        public override GH_Exposure Exposure => GH_Exposure.hidden;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_In09_TPMS_from_Slices.png"))
                        return s != null ? new System.Drawing.Bitmap(s) : null;
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "guide_curves", "guides",
                "Guide curves as a DataTree. Each branch = one layer.\n" +
                "Each branch must contain >= 2 curves ordered from one boundary to the other.\n" +
                "Domains are formed between consecutive pairs: [0+1], [1+2], etc.\n" +
                "All curves in a branch are auto-aligned to the same direction.",
                GH_ParamAccess.tree);

            pManager.AddNumberParameter(
                "w_shell", "w_shell",
                "Shell line width in model units. Shell paths are offset inward from each outermost guide,\n" +
                "centred at (i - 0.5) × w_shell for i = 1 … n_shell (Slicer Plus convention).\n" +
                "Default 5. 0 = no shell output. LIST access. Cycles per domain. Must be >= 0.",
                GH_ParamAccess.list, 5.0);

            pManager.AddIntegerParameter(
                "n_shell", "n_shell",
                "Number of shell lines per outermost guide. Minimum 1. LIST access. Cycles per domain.",
                GH_ParamAccess.list);

            pManager.AddNumberParameter(
                "longitudinal_clearance", "clear_long",
                "Arc-length clearance trimmed from EACH END of ALL guide curves (model units).\n" +
                "Only affects infill — shell paths use the full guides. Default 0.\n" +
                "Suggested default when shell is active: max(0, w_shell × n_shell - w_shell / 2).",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "guide_clearance", "clear_guide",
                "Gap inset from the two OUTERMOST curves [0] and [-1] only (model units).\n" +
                "Only affects infill — shell paths are built before this is applied. Default 0.\n" +
                "Suggested default when shell is active: max(0, w_shell × n_shell - w_shell / 2).",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "clearance_infill", "clear_in",
                "Gap inset applied to every INTERMEDIATE curve [1..n-2] (model units).\n" +
                "Ignored when only 2 curves are provided. Default 0.",
                GH_ParamAccess.item);

            pManager.AddIntegerParameter(
                "tpms_type", "type",
                "TPMS surface type. LIST: one value per domain (cycles). 0=P, 1=D, 2=Gyroid, 3=IWP, 4=Neovius, 5=Lidinoid, 6=FK-S, 7=FK-Y.",
                GH_ParamAccess.list);

            pManager.AddNumberParameter(
                "level", "level",
                "Iso-level list. Default 0.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter(
                "count_x", "cx",
                "TPMS count along curve direction. LIST. Default 3.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter(
                "count_y", "cy",
                "TPMS count across gap. LIST. Default 1.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter(
                "count_z", "cz",
                "TPMS count across layers / z-phase. LIST. Default 4.",
                GH_ParamAccess.list);

            pManager.AddNumberParameter(
                "phase_x", "px",
                "Phase offset in s [0..1]. LIST. Default 0.",
                GH_ParamAccess.list);

            pManager.AddNumberParameter(
                "phase_y", "py",
                "Phase offset in t [0..1]. LIST. Default 0.",
                GH_ParamAccess.list);

            pManager.AddNumberParameter(
                "phase_z", "pz",
                "Phase offset in n [0..1]. LIST. Default 0.",
                GH_ParamAccess.list);

            pManager.AddNumberParameter(
                "sample_spacing", "dx",
                "Grid cell size. Default 1.0.",
                GH_ParamAccess.item);

            pManager.AddIntegerParameter(
                "min_pts", "min",
                "Discard contours with fewer polyline vertices than this. Default 2.",
                GH_ParamAccess.item);

            pManager.AddBooleanParameter(
                "trim_layers", "trim",
                "Output tree structure for shell, infill, partitions, and points.\n" +
                "False = {layer;domain;path}, True = {layer}. Default true.",
                GH_ParamAccess.item, true);

            pManager.AddBooleanParameter(
                "close_shell", "close_shell",
                "If true, each shell polyline is closed (first point appended at the end).\n" +
                "When n_shell > 1 each individual shell line is closed independently. Default true.",
                GH_ParamAccess.item, true);

            pManager.AddNumberParameter(
                "res", "res",
                "Shell sampling resolution in model units. Smaller values create smoother shell curves and heavier computation.\n" +
                "If <= 0 or unwired, the component auto-derives a spacing from the guide lengths to keep roughly the previous 64-sample shell quality.",
                GH_ParamAccess.item, 0.0);

            for (int i = 1; i < pManager.ParamCount; i++)
                pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "shell", "shell",
                "Shell polylines offset inward from outermost guides (n_shell lines per guide, Slicer Plus convention). One branch per layer.",
                GH_ParamAccess.tree);

            pManager.AddCurveParameter(
                "infill", "infill",
                "TPMS contours as PolylineCurves.\n" +
                "trim_layers=false: {layer;domain;path}\n" +
                "trim_layers=true : {layer}.",
                GH_ParamAccess.tree);

            pManager.AddCurveParameter(
                "partitions", "parts",
                "Inner guide curves that partition the space into domains ([1..n-2]). One branch per layer. Empty when each layer has exactly 2 guides.",
                GH_ParamAccess.tree);

            pManager.AddPointParameter(
                "contour_pts", "pts",
                "Contour polyline vertices. Same topology as infill.",
                GH_ParamAccess.tree);

            pManager.AddTextParameter(
                "info", "info",
                "Diagnostics / per-branch stats.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "por_layer", "f_layer",
                "Estimated porosity per layer (0–1). 1 = fully void, 0 = fully solid.",
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

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_Structure<GH_Curve> guideTree = null;

            var wShellList  = new List<double>();
            var nShellList  = new List<int>();
            double clearLong    = 0.0;
            double clearGuide   = 0.0;
            double clearIn      = 0.0;
            double sampleSpacing = 1.0;
            int minPts = 2;
            bool trimLayers = true;
            bool closeShell = true;
            double shellRes = 0.0;

            var typeList  = new List<int>();
            var levelList = new List<double>();
            var cxList    = new List<int>();
            var cyList    = new List<int>();
            var czList    = new List<int>();
            var pxList    = new List<double>();
            var pyList    = new List<double>();
            var pzList    = new List<double>();

            if (!DA.GetDataTree(0, out guideTree) || guideTree == null || guideTree.PathCount == 0)
            {
                for (int _i = 0; _i < 4; _i++) DA.SetDataTree(_i, new DataTree<Curve>());
                DA.SetData(4, "Provide guide_curves as a DataTree. Each branch must have >= 2 curves.");
                DA.SetDataList(5, new List<double>());
                DA.SetData(6, 0.0);
                DA.SetDataTree(7, new DataTree<Plane>());
                return;
            }

            DA.GetDataList(1,  wShellList);
            DA.GetDataList(2,  nShellList);
            bool clearLongExplicit  = DA.GetData(3,  ref clearLong);
            bool clearGuideExplicit = DA.GetData(4,  ref clearGuide);
            DA.GetData(5,  ref clearIn);
            DA.GetDataList(6,  typeList);
            DA.GetDataList(7,  levelList);
            DA.GetDataList(8,  cxList);
            DA.GetDataList(9,  cyList);
            DA.GetDataList(10, czList);
            DA.GetDataList(11, pxList);
            DA.GetDataList(12, pyList);
            DA.GetDataList(13, pzList);
            DA.GetData(14, ref sampleSpacing);
            DA.GetData(15, ref minPts);
            DA.GetData(16, ref trimLayers);
            DA.GetData(17, ref closeShell);
            DA.GetData(18, ref shellRes);

            if (wShellList.Count == 0) wShellList.Add(5.0);
            if (nShellList.Count == 0) nShellList.Add(1);
            for (int i = 0; i < wShellList.Count; i++) if (wShellList[i] < 0.0) wShellList[i] = 0.0;
            for (int i = 0; i < nShellList.Count; i++) if (nShellList[i] < 1)   nShellList[i] = 1;
            // Dynamic default: (w_shell × n_shell) - (w_shell / 2) using first list entries
            {
                double w0 = wShellList[0];
                int    n0 = nShellList[0];
                double dynDef14 = Math.Max(0.0, w0 * n0 - w0 * 0.5);
                if (!clearLongExplicit)  clearLong  = dynDef14;
                if (!clearGuideExplicit) clearGuide = dynDef14;
            }
            if (clearLong  < 0.0) clearLong  = 0.0;
            if (clearGuide < 0.0) clearGuide = 0.0;
            if (clearIn    < 0.0) clearIn    = 0.0;
            if (sampleSpacing <= 0.0) sampleSpacing = 1.0;
            if (minPts < 2) minPts = 2;

            EnsureListDefault(typeList, 2);
            EnsureListDefault(levelList, 0.0);
            EnsureListDefault(cxList, 3);
            EnsureListDefault(cyList, 1);
            EnsureListDefault(czList, 4);
            EnsureListDefault(pxList, 0.0);
            EnsureListDefault(pyList, 0.0);
            EnsureListDefault(pzList, 0.0);

            for (int i = 0; i < typeList.Count; i++) typeList[i] = Clamp(typeList[i], 0, 7);
            for (int i = 0; i < cxList.Count; i++) cxList[i] = Math.Max(1, cxList[i]);
            for (int i = 0; i < cyList.Count; i++) cyList[i] = Math.Max(1, cyList[i]);
            for (int i = 0; i < czList.Count; i++) czList[i] = Math.Max(1, czList[i]);

            string typeTag = BuildListTag(typeList, t => TPMSTag(t));
            Message = $"{_versionTag} | {typeTag}";

            double tol = RhinoDoc.ActiveDoc != null ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 1e-6;
            double eps = Math.Max(1e-9, tol * 0.1);
            const int GRID_MIN = 4;
            const int GRID_MAX = 2000;
            double TWO_PI = 2.0 * Math.PI;
            int branchCount = guideTree.PathCount;

            var perBranchShells = new List<PolylineCurve>[branchCount];
            var perBranchParts  = new List<(GH_Path path, Curve crv)>[branchCount];
            var perBranchCurves = new List<(GH_Path path, PolylineCurve crv)>[branchCount];
            var perBranchPts    = new List<(GH_Path path, Point3d pt)>[branchCount];
            var perBranchLog    = new string[branchCount];
            var perBranchPor    = new double[branchCount];

            for (int i = 0; i < branchCount; i++)
            {
                perBranchShells[i] = new List<PolylineCurve>();
                perBranchParts [i] = new List<(GH_Path, Curve)>();
                perBranchCurves[i] = new List<(GH_Path, PolylineCurve)>();
                perBranchPts   [i] = new List<(GH_Path, Point3d)>();
                perBranchLog   [i] = "";
            }

            int skipped = 0;
            int totalContours = 0;

            Action<int> processBranch = bi =>
            {
                var localLog    = new StringBuilder();
                var localShells = new List<PolylineCurve>();
                var localParts  = new List<(GH_Path, Curve)>();
                var localCrvs   = new List<(GH_Path, PolylineCurve)>();
                var localPts    = new List<(GH_Path, Point3d)>();

                GH_Path brPath = guideTree.Paths[bi];
                GH_Path layerPath = trimLayers ? new GH_Path(bi) : brPath;
                List<GH_Curve> br = guideTree.Branches[bi];

                void Bail(string msg)
                {
                    if (msg != null) localLog.AppendLine(msg);
                    Interlocked.Increment(ref skipped);
                    perBranchShells[bi] = localShells;
                    perBranchParts [bi] = localParts;
                    perBranchCurves[bi] = localCrvs;
                    perBranchPts   [bi] = localPts;
                    perBranchLog   [bi] = localLog.ToString();
                }
                ;

                if (br == null || br.Count < 2)
                {
                    Bail($"Branch {brPath}: needs >= 2 curves. Skipped.");
                    return;
                }

                int nCurves = br.Count;
                int nDomains = nCurves - 1;
                var rawCurves = new Curve[nCurves];

                for (int ci = 0; ci < nCurves; ci++)
                {
                    Curve c = br[ci]?.Value?.DuplicateCurve();
                    if (c == null || !c.IsValid)
                    {
                        Bail($"Branch {brPath}: curve [{ci}] invalid. Skipped.");
                        return;
                    }
                    rawCurves[ci] = c;
                }

                Vector3d refTan = rawCurves[0].TangentAt(rawCurves[0].Domain.T0);
                if (refTan.IsValid) refTan.Unitize();
                for (int ci = 1; ci < nCurves; ci++)
                {
                    Vector3d t = rawCurves[ci].TangentAt(rawCurves[ci].Domain.T0);
                    if (t.IsValid)
                    {
                        t.Unitize();
                        if (Vector3d.Multiply(refTan, t) < 0.0)
                            rawCurves[ci].Reverse();
                    }
                }

                // Shell paths + porosity area tracking
                double branchPrintArea = 0.0;
                double branchTotalArea = 0.0;
                {
                    int    lastDi  = Math.Max(0, nCurves - 2);
                    double wShell0 = wShellList.Count > 0 ? Math.Max(0.0, wShellList[0 % wShellList.Count]) : 0.0;
                    int    nShell0 = nShellList.Count  > 0 ? Math.Max(1,   nShellList[0 % nShellList.Count])  : 1;
                    double wShellN = wShellList.Count > 0 ? Math.Max(0.0, wShellList[lastDi % wShellList.Count]) : 0.0;
                    int    nShellN = nShellList.Count  > 0 ? Math.Max(1,   nShellList[lastDi % nShellList.Count])  : 1;

                    if (closeShell && wShell0 > tol && wShellN > tol)
                    {
                        int nS  = Math.Min(nShell0, nShellN);
                        var sp0 = BuildShellPaths(rawCurves[0], rawCurves[1], nS, wShell0, shellRes, tol, true);
                        var spN = BuildShellPaths(rawCurves[nCurves - 1], rawCurves[nCurves - 2], nS, wShellN, shellRes, tol, true);
                        foreach (var c in CloseShellPairs(sp0, spN, tol))
                            localShells.Add(c);
                    }
                    else
                    {
                        if (wShell0 > tol)
                        {
                            var sp0 = BuildShellPaths(rawCurves[0], rawCurves[1], nShell0, wShell0, shellRes, tol);
                            foreach (var sp in sp0) localShells.Add(sp);
                        }
                        if (wShellN > tol)
                        {
                            var spN = BuildShellPaths(rawCurves[nCurves - 1], rawCurves[nCurves - 2], nShellN, wShellN, shellRes, tol);
                            foreach (var sp in spN) localShells.Add(sp);
                        }
                    }
                    branchPrintArea += rawCurves[0].GetLength() * wShell0 * nShell0;
                    branchPrintArea += rawCurves[nCurves - 1].GetLength() * wShellN * nShellN;
                }

                // Inner guides as partitions — shortened by (w_shell × n_shell - w_shell / 3)
                {
                    double w0p = wShellList.Count > 0 ? Math.Max(0.0, wShellList[0]) : 0.0;
                    int    n0p = nShellList.Count  > 0 ? Math.Max(1,   nShellList[0]) : 1;
                    double partShorten14 = Math.Max(0.0, w0p * n0p - w0p / 3.0);
                    for (int ci = 1; ci < nCurves - 1; ci++)
                    {
                        Curve partCrv = rawCurves[ci].DuplicateCurve();
                        if (partShorten14 > tol)
                            partCrv = TrimCurveEnds(partCrv, partShorten14, tol);
                        if (partCrv != null && partCrv.IsValid)
                            localParts.Add((layerPath, partCrv));
                    }
                }

                var curves = new Curve[nCurves];
                Array.Copy(rawCurves, curves, nCurves);

                if (clearLong > tol)
                {
                    for (int ci = 0; ci < nCurves; ci++)
                    {
                        curves[ci] = TrimCurveEnds(curves[ci], clearLong, tol);
                        if (curves[ci] == null)
                        {
                            Bail($"Branch {brPath}: clear_long too large for curve [{ci}]. Skipped.");
                            return;
                        }
                    }
                }

                var lengths = new double[nCurves];
                for (int ci = 0; ci < nCurves; ci++)
                {
                    lengths[ci] = curves[ci].GetLength();
                    if (lengths[ci] <= tol)
                    {
                        Bail($"Branch {brPath}: curve [{ci}] too short after shorten. Skipped.");
                        return;
                    }
                }

                int nxMax = GRID_MIN;
                for (int di = 0; di < nDomains; di++)
                {
                    int nx_d = Clamp((int)Math.Round(lengths[di] / sampleSpacing), GRID_MIN, GRID_MAX);
                    if (nx_d > nxMax) nxMax = nx_d;
                }

                var paramCache = new double[nCurves][];
                for (int ci = 0; ci < nCurves; ci++)
                {
                    paramCache[ci] = new double[nxMax + 1];
                    for (int ix = 0; ix <= nxMax; ix++)
                    {
                        double s01 = (double)ix / (double)nxMax;
                        if (!curves[ci].LengthParameter(s01 * lengths[ci], out paramCache[ci][ix]))
                            paramCache[ci][ix] = curves[ci].Domain.ParameterAt(s01);
                    }
                }

                double n01 = (branchCount <= 1) ? 0.0 : (double)bi / (double)(branchCount - 1);

                for (int di = 0; di < nDomains; di++)
                {
                    int domType = typeList[di % typeList.Count];
                    double domLevel = levelList[di % levelList.Count];
                    int domCx = cxList[di % cxList.Count];
                    int domCy = cyList[di % cyList.Count];
                    int domCz = czList[di % czList.Count];
                    double domPx = pxList[di % pxList.Count];
                    double domPy = pyList[di % pyList.Count];
                    double domPz = pzList[di % pzList.Count];

                    Curve cA = curves[di];
                    Curve cB = curves[di + 1];
                    double lenA = lengths[di];
                    double lenB = lengths[di + 1];
                    double insetA = (di == 0) ? clearGuide : clearIn;
                    double insetB = (di == nDomains - 1) ? clearGuide : clearIn;
                    double avgGap = EstimateAverageGap(cA, cB, lenA, lenB);
                    branchTotalArea += avgGap * lenA;
                    double usableGap = Math.Max(0.0, avgGap - insetA - insetB);
                    int nx = Clamp((int)Math.Round(lenA / sampleSpacing), GRID_MIN, GRID_MAX);
                    int ny = Clamp((int)Math.Round(usableGap / sampleSpacing), GRID_MIN, GRID_MAX);

                    double[,] F = new double[ny + 1, nx + 1];
                    Point3d[,] P3 = new Point3d[ny + 1, nx + 1];

                    for (int iy = 0; iy <= ny; iy++)
                    {
                        double t01 = (double)iy / (double)ny;
                        double yPhase = TWO_PI * domCy * (t01 + domPy);
                        double zPhase = TWO_PI * domCz * (n01 + domPz);

                        for (int ix = 0; ix <= nx; ix++)
                        {
                            double s01 = (double)ix / (double)nx;
                            int cIdx = (int)Math.Round(s01 * nxMax);
                            cIdx = cIdx < 0 ? 0 : cIdx > nxMax ? nxMax : cIdx;

                            Point3d pA = cA.PointAt(paramCache[di][cIdx]);
                            Point3d pB = cB.PointAt(paramCache[di + 1][cIdx]);
                            Vector3d vAB = pB - pA;
                            double gap = vAB.Length;
                            if (gap > tol)
                            {
                                vAB.Unitize();
                                double iA = Math.Min(insetA, Math.Max(0.0, gap * 0.5 - eps));
                                double iB = Math.Min(insetB, Math.Max(0.0, gap * 0.5 - eps));
                                pA = pA + vAB * iA;
                                pB = pB - vAB * iB;
                            }

                            P3[iy, ix] = pA + (pB - pA) * t01;
                            double xPhase = TWO_PI * domCx * (s01 + domPx);
                            F[iy, ix] = TPMSValue(domType, xPhase, yPhase, zPhase) - domLevel;
                        }
                    }

                    var segments = new List<(Point3d A, Point3d B)>();
                    for (int iy = 0; iy < ny; iy++)
                    {
                        for (int ix = 0; ix < nx; ix++)
                        {
                            double f0 = F[iy, ix];
                            double f1 = F[iy, ix + 1];
                            double f2 = F[iy + 1, ix + 1];
                            double f3 = F[iy + 1, ix];
                            Point3d p0 = P3[iy, ix];
                            Point3d p1 = P3[iy, ix + 1];
                            Point3d p2 = P3[iy + 1, ix + 1];
                            Point3d p3 = P3[iy + 1, ix];

                            int code = (f0 > 0 ? 1 : 0) | (f1 > 0 ? 2 : 0) | (f2 > 0 ? 4 : 0) | (f3 > 0 ? 8 : 0);
                            if (code == 0 || code == 15) continue;

                            Point3d EP(double fa, double fb, Point3d pa, Point3d pb)
                            {
                                double d = fb - fa;
                                double tt = Math.Abs(d) < 1e-14 ? 0.5 : -fa / d;
                                tt = tt < 0.0 ? 0.0 : tt > 1.0 ? 1.0 : tt;
                                return pa + (pb - pa) * tt;
                            }

                            Point3d eB, eR, eT, eL;
                            switch (code)
                            {
                                case 1: case 14: eB = EP(f0, f1, p0, p1); eL = EP(f0, f3, p0, p3); segments.Add((eB, eL)); break;
                                case 2: case 13: eB = EP(f0, f1, p0, p1); eR = EP(f1, f2, p1, p2); segments.Add((eB, eR)); break;
                                case 3: case 12: eL = EP(f0, f3, p0, p3); eR = EP(f1, f2, p1, p2); segments.Add((eL, eR)); break;
                                case 4: case 11: eR = EP(f1, f2, p1, p2); eT = EP(f3, f2, p3, p2); segments.Add((eR, eT)); break;
                                case 5:
                                    eB = EP(f0, f1, p0, p1);
                                    eR = EP(f1, f2, p1, p2);
                                    eT = EP(f3, f2, p3, p2);
                                    eL = EP(f0, f3, p0, p3);
                                    segments.Add((eB, eL));
                                    segments.Add((eR, eT));
                                    break;
                                case 6: case 9: eB = EP(f0, f1, p0, p1); eT = EP(f3, f2, p3, p2); segments.Add((eB, eT)); break;
                                case 7: case 8: eL = EP(f0, f3, p0, p3); eT = EP(f3, f2, p3, p2); segments.Add((eL, eT)); break;
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

                    int contourIndex = 0;
                    foreach (var chain in ChainSegmentsHashed(segments, tol))
                    {
                        if (chain == null || chain.Count < minPts) continue;
                        if (!TryMakeValidPolyline(chain, tol, out Polyline pl)) continue;
                        pl.CollapseShortSegments(tol);
                        if (!pl.IsValid || pl.Count < 2 || pl.Length <= tol) continue;

                        var plc = new PolylineCurve(pl);
                        GH_Path outPath = trimLayers ? layerPath : new GH_Path(brPath.AppendElement(di).AppendElement(contourIndex));
                        localCrvs.Add((outPath, plc));
                        for (int p = 0; p < pl.Count; p++) localPts.Add((outPath, pl[p]));
                        branchPrintArea += pl.Length * sampleSpacing;
                        contourIndex++;
                        Interlocked.Increment(ref totalContours);
                    }

                    localLog.AppendLine($"Branch {brPath} domain [{di}]: grid {nx}x{ny}  type={TPMSTag(domType)}  cx={domCx}  cy={domCy}  cz={domCz}");
                }

                perBranchPor   [bi] = branchTotalArea > 0.0
                    ? Math.Max(0.0, Math.Min(1.0, 1.0 - branchPrintArea / branchTotalArea))
                    : 0.0;
                perBranchShells[bi] = localShells;
                perBranchParts [bi] = localParts;
                perBranchCurves[bi] = localCrvs;
                perBranchPts   [bi] = localPts;
                perBranchLog   [bi] = localLog.ToString();
            };

            if (branchCount < PARALLEL_THRESHOLD)
            {
                for (int bi = 0; bi < branchCount; bi++) processBranch(bi);
            }
            else
            {
                Parallel.For(0, branchCount, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, bi => processBranch(bi));
            }

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
                GH_Path layerPath = trimLayers ? new GH_Path(bi) : guideTree.Paths[bi];
                Plane layerPlane = WasperLayerPlaneTools.EstimateLayerPlaneFromGhCurves(guideTree.Branches[bi], tol);
                double layerPlaneDev = WasperLayerPlaneTools.MaxDeviationFromPlane(guideTree.Branches[bi], layerPlane);
                maxLayerPlaneDeviation = Math.Max(maxLayerPlaneDeviation, layerPlaneDev);
                if (layerPlaneDev > WasperLayerPlaneTools.PlanarityWarningTolerance(tol))
                    nonPlanarLayers++;
                outPlanes.Add(layerPlane, layerPath);
                foreach (var s    in perBranchShells[bi]) outShells.Add(s,        layerPath);
                foreach (var item in perBranchParts [bi]) outParts .Add(item.crv, item.path);
                foreach (var item in perBranchCurves[bi]) outCrvs  .Add(item.crv, item.path);
                foreach (var item in perBranchPts   [bi]) outPts   .Add(item.pt,  item.path);
                porLayer.Add(perBranchPor[bi]);
            }
            double porAvg = porLayer.Count > 0 ? porLayer.Average() : 0.0;

            var infoSb = new StringBuilder();
            infoSb.AppendLine("wsp_In15_TPMS_from_Slices  v1.0.6 - 260420");
            infoSb.AppendLine($"branches_in    : {branchCount}");
            infoSb.AppendLine($"skipped        : {skipped}");
            infoSb.AppendLine($"contours_made  : {totalContours}");
            infoSb.AppendLine($"non_planar_layers : {nonPlanarLayers}");
            infoSb.AppendLine($"max_plane_deviation: {maxLayerPlaneDeviation:0.###}");
            infoSb.AppendLine($"tpms_type      : {BuildListTag(typeList, t => $"{t}({TPMSTag(t)})")}");
            infoSb.AppendLine($"level          : {BuildListTag(levelList, v => v.ToString("0.###"))}");
            infoSb.AppendLine($"count_x        : {BuildListTag(cxList, v => v.ToString())}");
            infoSb.AppendLine($"count_y        : {BuildListTag(cyList, v => v.ToString())}");
            infoSb.AppendLine($"count_z        : {BuildListTag(czList, v => v.ToString())}");
            infoSb.AppendLine($"phase_x        : {BuildListTag(pxList, v => v.ToString("0.###"))}");
            infoSb.AppendLine($"phase_y        : {BuildListTag(pyList, v => v.ToString("0.###"))}");
            infoSb.AppendLine($"phase_z        : {BuildListTag(pzList, v => v.ToString("0.###"))}");
            infoSb.AppendLine($"clear_long     : {clearLong:0.###}");
            infoSb.AppendLine($"clear_guide    : {clearGuide:0.###}");
            infoSb.AppendLine($"clear_in       : {clearIn:0.###}");
            infoSb.AppendLine($"sample_spacing : {sampleSpacing:0.###}");
            infoSb.AppendLine($"min_pts        : {minPts}");
            infoSb.AppendLine($"trim_layers    : {trimLayers}");
            infoSb.AppendLine($"close_shell    : {closeShell}");
            for (int bi = 0; bi < branchCount; bi++) if (!string.IsNullOrEmpty(perBranchLog[bi])) infoSb.Append(perBranchLog[bi]);

            DA.SetDataTree(0, outShells);
            DA.SetDataTree(1, outCrvs);
            DA.SetDataTree(2, outParts);
            DA.SetDataTree(3, outPts);
            DA.SetData(4, infoSb.ToString());
            DA.SetDataList(5, porLayer);
            DA.SetData(6, porAvg);
            DA.SetDataTree(7, outPlanes);
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

        private static List<PolylineCurve> BuildShellPaths(
            Curve outerGuide, Curve innerGuide,
            int nShell, double wShell,
            double sampleRes, double tol,
            bool insetEnds = false)
        {
            var result = new List<PolylineCurve>();
            if (outerGuide == null || innerGuide == null) return result;
            if (!outerGuide.IsValid || !innerGuide.IsValid) return result;
            if (nShell < 1 || wShell <= tol) return result;

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

        private static void EnsureListDefault<T>(List<T> list, T defaultValue)
        {
            if (list != null && list.Count == 0) list.Add(defaultValue);
        }

        private static List<List<Point3d>> ChainSegmentsHashed(List<(Point3d A, Point3d B)> segs, double tol)
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
                if (!endMap.TryGetValue(k, out var lst))
                    endMap[k] = lst = new List<int>(2);
                lst.Add(idx);
            }
            void Unregister(int idx, Point3d pt)
            {
                long k = Key(pt);
                if (endMap.TryGetValue(k, out var lst))
                {
                    lst.Remove(idx);
                    if (lst.Count == 0)
                        endMap.Remove(k);
                }
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
                        Point3d a = segs[ni].A; Point3d b = segs[ni].B; alive[ni] = false; Unregister(ni, a); Unregister(ni, b);
                        chain.Add(tail.DistanceToSquared(a) < tail.DistanceToSquared(b) ? b : a);
                        extended = true;
                    }
                    Point3d head = chain[0];
                    ni = FindNeighbour(head);
                    if (ni >= 0)
                    {
                        Point3d a = segs[ni].A; Point3d b = segs[ni].B; alive[ni] = false; Unregister(ni, a); Unregister(ni, b);
                        chain.Insert(0, head.DistanceToSquared(a) < head.DistanceToSquared(b) ? b : a);
                        extended = true;
                    }
                }
                result.Add(chain);
            }

            return result;
        }

        private static bool TryMakeValidPolyline(List<Point3d> pts, double tol, out Polyline pl)
        {
            pl = new Polyline();
            if (pts == null || pts.Count < 2) return false;
            var dedup = new List<Point3d>(pts.Count);
            dedup.Add(pts[0]);
            for (int i = 1; i < pts.Count; i++) if (pts[i].IsValid && pts[i].DistanceTo(dedup[dedup.Count - 1]) > tol) dedup.Add(pts[i]);
            if (dedup.Count < 2) return false;
            pl = new Polyline(dedup);
            return pl.IsValid && pl.Count >= 2 && pl.Length > tol;
        }

        private static double EstimateAverageGap(Curve bottom, Curve top, double lenB, double lenT)
        {
            double[] u = { 0.0, 0.25, 0.5, 0.75, 1.0 };
            double sum = 0.0; int n = 0;
            foreach (double uu in u)
            {
                double tB, tT;
                if (!bottom.LengthParameter(uu * lenB, out tB)) tB = bottom.Domain.ParameterAt(uu);
                if (!top.LengthParameter(uu * lenT, out tT)) tT = top.Domain.ParameterAt(uu);
                sum += bottom.PointAt(tB).DistanceTo(top.PointAt(tT));
                n++;
            }
            return n > 0 ? sum / n : 0.0;
        }

        private static Curve TrimCurveEnds(Curve crv, double amount, double tol)
        {
            double len = crv.GetLength();
            if (len <= 2.0 * amount + tol) return null;
            double t0, t1;
            if (!crv.LengthParameter(amount, out t0)) t0 = crv.Domain.ParameterAt(amount / len);
            if (!crv.LengthParameter(len - amount, out t1)) t1 = crv.Domain.ParameterAt((len - amount) / len);
            if (t1 <= t0 + tol) return null;
            return crv.Trim(t0, t1);
        }

        private static double TPMSValue(int type, double x, double y, double z)
        {
            switch (type)
            {
                case 0: return Math.Cos(x) + Math.Cos(y) + Math.Cos(z);
                case 1: return Math.Sin(x) * Math.Sin(y) * Math.Sin(z) + Math.Sin(x) * Math.Cos(y) * Math.Cos(z) + Math.Cos(x) * Math.Sin(y) * Math.Cos(z) + Math.Cos(x) * Math.Cos(y) * Math.Sin(z);
                case 2: return Math.Sin(x) * Math.Cos(y) + Math.Sin(y) * Math.Cos(z) + Math.Sin(z) * Math.Cos(x);
                case 3: return -2.0 * (Math.Cos(x) * Math.Cos(y) + Math.Cos(y) * Math.Cos(z) + Math.Cos(z) * Math.Cos(x)) + (Math.Cos(2 * x) + Math.Cos(2 * y) + Math.Cos(2 * z));
                case 4: return 3.0 * (Math.Cos(x) + Math.Cos(y) + Math.Cos(z)) + 4.0 * Math.Cos(x) * Math.Cos(y) * Math.Cos(z);
                case 5: return 0.5 * (Math.Sin(2 * x) * Math.Cos(y) * Math.Sin(z) + Math.Sin(2 * y) * Math.Cos(z) * Math.Sin(x) + Math.Sin(2 * z) * Math.Cos(x) * Math.Sin(y)) - 0.5 * (Math.Cos(2 * x) * Math.Cos(2 * y) + Math.Cos(2 * y) * Math.Cos(2 * z) + Math.Cos(2 * z) * Math.Cos(2 * x));
                case 6: return Math.Sin(x) * Math.Cos(y) * Math.Cos(2 * z) + Math.Cos(2 * x) * Math.Sin(y) * Math.Cos(z) + Math.Cos(x) * Math.Cos(2 * y) * Math.Sin(z);
                case 7: return Math.Sin(x) * Math.Sin(y) * Math.Sin(z) + Math.Cos(x) * Math.Cos(y) * Math.Cos(z) + Math.Sin(2 * x) * Math.Sin(y) + Math.Cos(x) * Math.Sin(2 * y) + Math.Sin(x) * Math.Sin(2 * z) + Math.Sin(2 * x) * Math.Cos(z) + Math.Sin(2 * y) * Math.Sin(z) + Math.Cos(y) * Math.Sin(2 * z);
                default: return 0.0;
            }
        }

        private static string TPMSTag(int t)
        {
            switch (t)
            {
                case 0: return "Prim";
                case 1: return "Diam";
                case 2: return "Gyr";
                case 3: return "IWP";
                case 4: return "Neo";
                case 5: return "Lidi";
                case 6: return "FK-S";
                case 7: return "FK-Y";
                default: return "?";
            }
        }

        private static string BuildListTag<T>(List<T> list, Func<T, string> fmt)
        {
            if (list == null || list.Count == 0) return "-";
            if (list.Count == 1) return fmt(list[0]);
            var sb = new StringBuilder("[");
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(fmt(list[i]));
            }
            sb.Append("]");
            return sb.ToString();
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : v > hi ? hi : v;
        }
    }
}
