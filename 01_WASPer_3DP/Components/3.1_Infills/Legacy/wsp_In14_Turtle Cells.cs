// wsp_In14_Turtle Cells (Trunc. Octa)
// Compiled Grasshopper component (VS) from your Script_Instance v260212f
// UPDATED v260420:
//	- supports MORE THAN 2 guide curves per branch (each consecutive pair = one domain)
//	- most scalar inputs now accept LIST access and cycle per domain
//	- new input: trim_paths
//		false = {layer;domain;path}
//		true  = {layer}
//	- new first output: shell
//		one branch per layer, built from the OUTERMOST guides of each layer
//	- default values added to all inputs except guide_curves
//
// ============================================================
// KEY GEOMETRY VARIABLES (local 2D space, before 3D mapping)
// ============================================================
//
//	The pattern is built in a local 2D coordinate system where:
//		X = arc-length along the bottom guide curve
//		Y = normalized position across the band gap (0=lower boundary, 1=upper boundary)
//
//	x_open		: total horizontal width of ONE cell (the "open" span)
//	x_closed	: horizontal width of the TRANSITION between two adjacent cells (the "closed" span)
//	xy			: horizontal run of the CELL INTERNAL diagonal
//				= (x_open - x_closed) / 2
//	xa			: horizontal length of the SHOULDER segments
//	xr			: horizontal length of the BRIDGE segment
//	x_closed	= 2*xa + xr
//
//	ampBand		: physical half-amplitude of the zig-zag within a band
//	yBridgeN	: normalized Y height of the bridge segment (0..1 within band)
//				  modulated by bridge_param between bottom and top
//
//	bridge_param : 0 = bridge at bottom
//				   1 = bridge at top
//
//	STRICT ZERO-STATE TARGET:
//		At the discrete zero-state layers of the modulation, we want:
//			xy = x_closed
//
//		From:
//			curve_len = cell_count_tan * x_open + (cell_count_tan - 1) * x_closed
//			x_open = x_closed + 2*xy
//
//		If xy = x_closed:
//			x_open = 3*x_closed
//			curve_len = (4*cell_count_tan - 1) * x_closed
//			x_closed_strict_ref = curve_len / (4*cell_count_tan - 1)
//
//	SMOOTH TRANSITION:
//		To avoid a discrete snap at strict layers, we blend:
//			x_closed_used = Lerp(x_closed_strict_ref, x_closed_nominal, blend)
//			xa_used	   = Lerp(xa_strict,		 xa_nominal,	   blend)
//			xr_used	   = x_closed_used - 2*xa_used
//
//		This preserves:
//			x_closed_used = 2*xa_used + xr_used
//
//	END EXTENSION:
//		extend_ends extends the first and last segment of each generated path
//		along their own direction. This does NOT deform the internal pattern;
//		it only extends the endpoints to improve connection with adjacent paths.
//
// ============================================================
// OUTPUT TOPOLOGY
// ============================================================
// shell:
//	{layer}
//
// path_crvs / path_pts when trim_paths = false:
//	{layer;domain;path}
//
// path_crvs / path_pts when trim_paths = true:
//	{layer}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP.Components._3_1_Infills
{
	public class wsp_In14_Turtle_Cells_260420 : GH_Component
	{
		private readonly string _versionTag;

		public wsp_In14_Turtle_Cells_260420()
		  : base(
				"wsp_In14_Turtle Cells from Slices (Trunc. Octa)",
				"TurtleCells",
				"Generates 'turtle-graphics' infill paths between consecutive guide-curve pairs in each branch.\n\n" +
				"How it works:\n" +
				"• Input is a tree of curves. Each branch = one layer.\n" +
				"• Each branch can contain 2 OR MORE curves. Each consecutive pair defines one domain: [0]-[1], [1]-[2], [2]-[3], ...\n" +
				"• Most numeric / boolean inputs accept LIST access and cycle per domain.\n" +
				"• The gap between every guide pair is split into 'cell_count_perp' bands.\n" +
				"• For each band, the component outputs TWO polylines: a lower path and an upper path.\n" +
				"• 'inset_guide' shrinks the usable gap (by 2×inset_guide) along the local bottom?top direction.\n" +
				"• 'clearance' is applied both WITHIN each band and BETWEEN bands.\n" +
				"• 'shorten_guides' trims both guide curves at each end.\n" +
				"• 'cell_count_z' modulates the bridge parameter across the stack using a triangle-wave.\n" +
				"• 'extend_ends' extends the first and last segment of each path along their own direction.\n" +
				"• 'trim_paths' restructures path outputs as one branch per layer.\n" +
				"• 'shell' outputs a closed polyline from the outermost guides of each layer, with one branch per layer.\n\n" +
				"Reference: 'Additive Manufacturing of Thermally Enhanced Lightweight Concrete Wall Elements with Closed Cellular Structures' Dielemans 2021",
				global::WASPer_3DP.WASPerPalette.DesignFabrication,
				"3.1_Infills")
		{
			var asm = System.Reflection.Assembly.GetExecutingAssembly();
			var v = asm.GetName().Version;
			_versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
			Message = _versionTag;
		}

		public override GH_Exposure Exposure => GH_Exposure.hidden;

		public override Guid ComponentGuid => new Guid("2A0E2B9C-1A1C-4AF9-9F0E-1F6B5F2F4E24");

		protected override Bitmap Icon
		{
			get
			{
       try
       {
    				var assembly = System.Reflection.Assembly.GetExecutingAssembly();
    				using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_In08_Turtle Cells.png"))
    				{
    					return stream != null ? new Bitmap(stream) : null;
    				}
       }
       catch { }
       return null;
			}
		}

		protected override void RegisterInputParams(GH_InputParamManager pManager)
		{
			pManager.AddCurveParameter(
				"guide_curves", "guides",
				"Guide curves as a DATA TREE. Each branch = one layer. Each branch must contain at least 2 curves. If a branch contains more than 2 curves, each consecutive pair becomes one domain: [0]-[1], [1]-[2], [2]-[3], ...",
				GH_ParamAccess.tree);

			pManager.AddNumberParameter(
				"w_shell", "w_shell",
				"Shell line width in model units. Shell paths are offset inward from each outermost guide,\n" +
				"centred at (i - 0.5) × w_shell for i = 1 … n_shell (Slicer Plus convention).\n" +
				"Default 5. 0 = no shell output. LIST access. Cycles per domain. Must be >= 0.",
				GH_ParamAccess.list, 5.0);
			pManager[1].Optional = true;

			pManager.AddIntegerParameter(
				"n_shell", "n_shell",
				"Number of shell lines per outermost guide. Minimum 1. LIST access. Cycles per domain.",
				GH_ParamAccess.list);
			pManager[2].Optional = true;

			pManager.AddNumberParameter(
				"longitudinal_clearance", "clear_long",
				"Arc-length clearance trimmed from EACH END of BOTH guide curves (model units).\n" +
				"Only affects infill — shell paths use the full, unshortened guides.\n" +
				"LIST access. Cycles per domain. Must be >= 0.\n" +
				"Default when unwired = max(0, w_shell × n_shell - w_shell / 2).",
				GH_ParamAccess.list);
			pManager[3].Optional = true;

			pManager.AddNumberParameter(
				"guide_clearance", "clear_guide",
				"Inset clearance along the local bottom?top direction at the OUTERMOST guide boundaries (model units).\n" +
				"Only affects infill — shell paths are built before this clearance is applied.\n" +
				"LIST access. Cycles per domain. Must be >= 0.\n" +
				"Default when unwired = max(0, w_shell × n_shell - w_shell / 2).",
				GH_ParamAccess.list);
			pManager[4].Optional = true;

			pManager.AddNumberParameter(
				"clearance_infill", "clear_in",
				"Clearance applied WITHIN each band and BETWEEN bands. LIST access. Cycles per domain. Must be >= 0.",
				GH_ParamAccess.list);
			pManager[5].Optional = true;

			pManager.AddNumberParameter(
				"extend_ends", "ext",
				"Extends the first and last segment of each generated path. LIST access. Cycles per domain. Must be >= 0.",
				GH_ParamAccess.list);
			pManager[6].Optional = true;

			pManager.AddNumberParameter(
				"p_width", "p_width",
				"Controls internal turtle geometry sizing. LIST access. Cycles per domain. Must be > 0.",
				GH_ParamAccess.list);
			pManager[7].Optional = true;

			pManager.AddIntegerParameter(
				"cell_count_tan", "cx",
				"Number of cells along the curve direction. LIST access. Cycles per domain. Minimum is 1.",
				GH_ParamAccess.list);
			pManager[8].Optional = true;

			pManager.AddIntegerParameter(
				"cell_count_perp", "cy",
				"Number of bands across the gap. LIST access. Cycles per domain. Minimum is 1.",
				GH_ParamAccess.list);
			pManager[9].Optional = true;

			pManager.AddNumberParameter(
				"cell_count_z", "cz",
				"Triangle-wave cycles across the stack. LIST access. Cycles per domain. Default 1 (one full cycle from b0?b1?b0). Use 0 to lock bridge at b0.",
				GH_ParamAccess.list);
			pManager[10].Optional = true;

			pManager.AddNumberParameter(
				"bridge_p_0", "b0",
				"Start bridge parameter (0..1). LIST access. Cycles per domain.",
				GH_ParamAccess.list);
			pManager[11].Optional = true;

			pManager.AddNumberParameter(
				"bridge_p_1", "b1",
				"Peak bridge parameter (0..1). LIST access. Cycles per domain.",
				GH_ParamAccess.list);
			pManager[12].Optional = true;

			pManager.AddBooleanParameter(
				"teeth", "teeth",
				"If true: bottom teeth enabled. LIST access. Cycles per domain.",
				GH_ParamAccess.list);
			pManager[13].Optional = true;

			pManager.AddBooleanParameter(
				"trim_paths", "trim",
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
			pManager[16].Optional = true;
		}

		protected override void RegisterOutputParams(GH_OutputParamManager pManager)
		{
			pManager.AddCurveParameter(
				"shell", "shell",
				"Shell polylines offset inward from the outermost guides (n_shell lines per guide, Slicer Plus convention). One branch per layer.",
				GH_ParamAccess.tree);

			pManager.AddCurveParameter(
				"infill", "infill",
				"Generated infill paths. If trim_paths=false: {layer;domain;path}. If trim_paths=true: {layer}.",
				GH_ParamAccess.tree);

			pManager.AddCurveParameter(
				"partitions", "parts",
				"Inner guide curves that partition the space into domains — all guides except the two outermost ones ([1..n-2]). One branch per layer {layer}. Empty when each layer has exactly 2 guides.",
				GH_ParamAccess.tree);

			pManager.AddPointParameter(
				"path_pts", "pts",
				"All polyline points. Same topology as infill.",
				GH_ParamAccess.tree);

			pManager.AddTextParameter(
				"info", "info",
				"Debug / summary info (counts, parameters used, warnings).",
				GH_ParamAccess.item);

			pManager.AddNumberParameter(
				"por_layer", "f_layer",
				"Estimated porosity per layer (0–1). 1 = fully void, 0 = fully solid.\n" +
				"Computed as 1 - printed_area / total_area, where printed_area accounts for infill paths and shell lines.",
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
			GH_Structure<GH_Curve> guideTree;
			var wShellList     = new List<double>();
			var nShellList     = new List<int>();
			var clearLongList  = new List<double>();
			var clearGuideList = new List<double>();
			var clearInList    = new List<double>();
			var extendList     = new List<double>();
			var pathWidthList  = new List<double>();
			var cellTanList    = new List<int>();
			var cellPerpList   = new List<int>();
			var cellZList      = new List<double>();
			var bridge0List    = new List<double>();
			var bridge1List    = new List<double>();
			var teethList      = new List<bool>();
			bool trimPaths  = true;
			bool closeShell = true;
			double shellRes = 0.0;

			if (!DA.GetDataTree(0, out guideTree) || guideTree == null || guideTree.PathCount == 0)
			{
				DA.SetData(4, "Provide guide_curves as a DataTree. Each branch must contain at least 2 curves.");
				DA.SetDataTree(7, new DataTree<Plane>());
				return;
			}

			DA.GetDataList(1,  wShellList);
			DA.GetDataList(2,  nShellList);
			DA.GetDataList(3,  clearLongList);
			DA.GetDataList(4,  clearGuideList);
			DA.GetDataList(5,  clearInList);
			DA.GetDataList(6,  extendList);
			DA.GetDataList(7,  pathWidthList);
			DA.GetDataList(8,  cellTanList);
			DA.GetDataList(9,  cellPerpList);
			DA.GetDataList(10, cellZList);
			DA.GetDataList(11, bridge0List);
			DA.GetDataList(12, bridge1List);
			DA.GetDataList(13, teethList);
			DA.GetData(14, ref trimPaths);
			DA.GetData(15, ref closeShell);
			DA.GetData(16, ref shellRes);

			EnsureDefaults(wShellList,    5.0);
			EnsureDefaults(nShellList,    1);
			// clearLongList / clearGuideList: no static default — dynamic per domain
			EnsureDefaults(clearInList,   0.0);
			EnsureDefaults(extendList,    0.0);
			EnsureDefaults(pathWidthList, 4.0);
			EnsureDefaults(cellTanList,   6);
			EnsureDefaults(cellPerpList,  1);
			EnsureDefaults(cellZList,     1.0);
			EnsureDefaults(bridge0List,   0.0);
			EnsureDefaults(bridge1List,   1.0);
			EnsureDefaults(teethList,     false);

			double tol = RhinoDoc.ActiveDoc != null ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 1e-6;
			double eps = Math.Max(1e-9, tol * 0.1);
			double epsInputZero = 1e-12;

			var outShell = new DataTree<Curve>();
			var outParts = new DataTree<Curve>();
			var outCrvs  = new DataTree<Curve>();
			var outPts   = new DataTree<Point3d>();
			var outPlanes = new DataTree<Plane>();
			var sb       = new StringBuilder();
			var warnings = new List<string>();
			var porLayer = new List<double>();

			int layerCount = guideTree.PathCount;
			int totalDomains = 0;
			int made = 0;
			int skippedLayers = 0;
			int skippedDomains = 0;
			int overpackedCellDomains = 0;
			int tightCellDomains = 0;
			int nonPlanarLayers = 0;
			double maxLayerPlaneDeviation = 0.0;

			for (int li = 0; li < layerCount; li++)
			{
				GH_Path inPath = guideTree.Paths[li];
				var brObj = guideTree.get_Branch(inPath);

				if (brObj == null || brObj.Count < 2)
				{
					sb.AppendLine($"Layer {inPath}: needs at least 2 curves.");
					skippedLayers++;
					continue;
				}

				var guides = new List<Curve>();
				for (int i = 0; i < brObj.Count; i++)
				{
					Curve c = (brObj[i] as GH_Curve)?.Value;
					if (c != null && c.IsValid)
						guides.Add(c.DuplicateCurve());
				}

				if (guides.Count < 2)
				{
					sb.AppendLine($"Layer {inPath}: valid guides after cleanup < 2.");
					skippedLayers++;
					continue;
				}

				OrientGuideChain(guides);

				GH_Path layerOutPath = trimPaths ? new GH_Path(li) : new GH_Path(inPath.Indices);
				Plane layerPlane = WasperLayerPlaneTools.EstimateLayerPlane(guides, tol);
				double layerPlaneDev = WasperLayerPlaneTools.MaxDeviationFromPlane(guides, layerPlane);
				maxLayerPlaneDeviation = Math.Max(maxLayerPlaneDeviation, layerPlaneDev);
				if (layerPlaneDev > WasperLayerPlaneTools.PlanarityWarningTolerance(tol))
				{
					nonPlanarLayers++;
					AddLimitedWarning(warnings, $"Layer {inPath}: guide curves deviate from fitted layer plane by {layerPlaneDev:0.###} model units.");
				}
				outPlanes.Add(layerPlane, layerOutPath);

				// Inner guides as partitions — shortened by (w_shell × n_shell - w_shell / 3) per domain
				for (int i = 1; i < guides.Count - 1; i++)
				{
					double wShP = SanitizeNonNegative(GetCycled(wShellList, i), 0.0);
					int    nShP = Math.Max(1, GetCycled(nShellList, i));
					double partShorten = Math.Max(0.0, wShP * nShP - wShP / 3.0);
					Curve partCrv = guides[i].DuplicateCurve();
					if (partShorten > tol)
						partCrv = ShortenCurve(partCrv, partShorten, tol, eps);
					if (partCrv != null && partCrv.IsValid)
						outParts.Add(partCrv, layerOutPath);
				}

				int domainCount = guides.Count - 1;
				totalDomains += domainCount;

				double layerPrintArea = 0.0;
				double layerTotalArea = 0.0;

				// Shell paths: n_shell offset polylines inward from each outermost guide
				{
					double wShell0 = SanitizeNonNegative(GetCycled(wShellList, 0), 0.0);
					int    nShell0 = Math.Max(1, GetCycled(nShellList, 0));
					int    lastDi  = Math.Max(0, guides.Count - 2);
					double wShellN = SanitizeNonNegative(GetCycled(wShellList, lastDi), 0.0);
					int    nShellN = Math.Max(1, GetCycled(nShellList, lastDi));

					if (closeShell && wShell0 > tol && wShellN > tol)
					{
						// Pair side-0 and side-N open lines into closed loops
						int nS  = Math.Min(nShell0, nShellN);
						var sp0 = BuildShellPaths(guides[0], guides[1], nS, wShell0, shellRes, tol, true);
						var spN = BuildShellPaths(guides[guides.Count - 1], guides[guides.Count - 2], nS, wShellN, shellRes, tol, true);
						foreach (var c in CloseShellPairs(sp0, spN, tol))
							outShell.Add(c, layerOutPath);
					}
					else
					{
						if (wShell0 > tol)
						{
							var sp0 = BuildShellPaths(guides[0], guides[1], nShell0, wShell0, shellRes, tol);
							foreach (var sp in sp0) outShell.Add(sp, layerOutPath);
						}
						if (wShellN > tol)
						{
							var spN = BuildShellPaths(guides[guides.Count - 1], guides[guides.Count - 2], nShellN, wShellN, shellRes, tol);
							foreach (var sp in spN) outShell.Add(sp, layerOutPath);
						}
					}
					layerPrintArea += guides[0].GetLength() * wShell0 * nShell0;
					layerPrintArea += guides[guides.Count - 1].GetLength() * wShellN * nShellN;
				}

				for (int di = 0; di < domainCount; di++)
				{
					Curve crvB = guides[di].DuplicateCurve();
					Curve crvT = guides[di + 1].DuplicateCurve();

					double wShellD    = SanitizeNonNegative(GetCycled(wShellList, di), 0.0);
					int    nShellD    = Math.Max(1, GetCycled(nShellList, di));
					double dynDef     = Math.Max(0.0, wShellD * nShellD - wShellD * 0.5);
					double clearLong  = (clearLongList.Count  == 0) ? dynDef : SanitizeNonNegative(GetCycled(clearLongList,  di), dynDef);
					double clearGuide = (clearGuideList.Count == 0) ? dynDef : SanitizeNonNegative(GetCycled(clearGuideList, di), dynDef);
					double clearIn    = SanitizeNonNegative(GetCycled(clearInList, di), 0.0);
					int cellCountTan  = Math.Max(1, GetCycled(cellTanList, di));
					int cellCountPerp = Math.Max(1, GetCycled(cellPerpList, di));
					double cellCountZ = SanitizeNonNegative(GetCycled(cellZList, di), 0.0);
					double pathWidth  = GetCycled(pathWidthList, di);
					if (double.IsNaN(pathWidth) || pathWidth <= 0.0) pathWidth = 4.0;
					double clearance  = clearIn;
					double extendEnds = SanitizeNonNegative(GetCycled(extendList, di), 0.0);
					double bridgeP0   = Clamp01(GetCycled(bridge0List, di));
					double bridgeP1   = Clamp01(GetCycled(bridge1List, di));
					bool teeth        = GetCycled(teethList, di);

					if (clearLong > tol)
					{
						crvB = ShortenCurve(crvB, clearLong, tol, eps);
						crvT = ShortenCurve(crvT, clearLong, tol, eps);

						if (crvB == null || crvT == null)
						{
							sb.AppendLine($"Layer {inPath} domain {di}: clear_long too large.");
							skippedDomains++;
							continue;
						}
					}

					double lenB = crvB.GetLength();
					double lenT = crvT.GetLength();
					if (lenB <= tol || lenT <= tol)
					{
						sb.AppendLine($"Layer {inPath} domain {di}: guide curve(s) too short.");
						skippedDomains++;
						continue;
					}

					double z01 = (layerCount <= 1) ? 0.0 : (double)li / (double)(layerCount - 1);
					double wave = TriangleWave01(z01, cellCountZ);
					double bridgeParam = (layerCount <= 1) ? bridgeP0 : Lerp(bridgeP0, bridgeP1, wave);
					bridgeParam = Clamp01(bridgeParam);

					bool zeroAtB0 = Math.Abs(bridgeP0) <= epsInputZero;
					bool zeroAtB1 = Math.Abs(bridgeP1) <= epsInputZero;
					bool strictZeroLayer = (layerCount == 1)
						? (zeroAtB0 || zeroAtB1)
						: (zeroAtB0 && zeroAtB1 && Math.Abs(bridgeParam) <= epsInputZero)
						  || (zeroAtB0 && !zeroAtB1 && Math.Abs(wave) <= epsInputZero)
						  || (!zeroAtB0 && zeroAtB1 && Math.Abs(1.0 - wave) <= epsInputZero);

					double xaNominal = pathWidth * 1.4;
					double xrNominal = pathWidth * 1.1;
					double xrFloor = pathWidth * 1.1;
					double xClosedNominal = 2.0 * xaNominal + xrNominal;

					double avgGap = EstimateAverageGap(crvB, crvT, tol);
					layerTotalArea += ((lenB + lenT) * 0.5) * avgGap;
					double avgGapIn = Math.Max(0.0, avgGap - 2.0 * clearGuide);
					double bandHeight = avgGapIn / Math.Max(1, cellCountPerp);
					double usableBandH = Math.Max(0.0, bandHeight - 2.0 * clearance);
					double ampNom = Math.Max(0.0, usableBandH * 0.5 - clearance);

					double curveLen = lenB;
					int denom = 4 * cellCountTan - 1;
					double xClosedStrictRef = (denom > 0) ? curveLen / (double)denom : xClosedNominal;
					xClosedStrictRef = Math.Max(0.0, xClosedStrictRef);

					double sinTheta = 0.0;
					double xaMaxFromSpacing = double.MaxValue;
					double dParallel = 0.0;

					if (ampNom > tol && xClosedStrictRef > tol)
					{
						double hyp = Math.Sqrt(xClosedStrictRef * xClosedStrictRef + ampNom * ampNom);
						if (hyp > tol)
						{
							sinTheta = ampNom / hyp;
							dParallel = xClosedStrictRef * sinTheta;
							if (sinTheta > tol)
								xaMaxFromSpacing = pathWidth / sinTheta;
						}
					}

					double xaMaxFromBridgeFloor = Math.Max(0.0, 0.5 * (xClosedStrictRef - xrFloor));
					double xaMaxFromBudget = Math.Max(0.0, 0.5 * xClosedStrictRef);
					double xaStrict = xaNominal;
					xaStrict = Math.Min(xaStrict, xaMaxFromBudget);
					xaStrict = Math.Min(xaStrict, xaMaxFromSpacing);
					if (xClosedStrictRef >= xrFloor - tol)
						xaStrict = Math.Min(xaStrict, xaMaxFromBridgeFloor);
					xaStrict = Math.Max(0.0, xaStrict);

					double blend;
					if (zeroAtB0 && zeroAtB1)
						blend = 0.0;
					else if (zeroAtB0 && !zeroAtB1)
						blend = wave;
					else if (!zeroAtB0 && zeroAtB1)
						blend = 1.0 - wave;
					else
						blend = 1.0;

					blend = Clamp01(blend);
					blend = blend * blend * (3.0 - 2.0 * blend);
					if (strictZeroLayer)
						blend = 0.0;

					double xClosedUsed = Lerp(xClosedStrictRef, xClosedNominal, blend);
					double xaUsed = Lerp(xaStrict, xaNominal, blend);
					double xrUsed = Math.Max(0.0, xClosedUsed - 2.0 * xaUsed);
					if (xrUsed < xrFloor - tol && xClosedUsed >= xrFloor - tol)
					{
						xrUsed = xrFloor;
						xaUsed = Math.Max(0.0, 0.5 * (xClosedUsed - xrUsed));
					}

					double totalMin = (2.0 * cellCountTan - 1.0) * xClosedUsed;
					double leftover = curveLen - totalMin;
					if (leftover < -tol)
					{
						int maxSafeCx = EstimateMaxCellCountTan(curveLen, xClosedUsed);
						string msg =
							$"Layer {inPath} domain {di}: cell_count_tan/cx={cellCountTan} is too high for the guide length and path width. " +
							$"curve_len={curveLen:0.###}, required_min={totalMin:0.###}, p_width={pathWidth:0.###}, " +
							$"x_closed={xClosedUsed:0.###}, max_safe_cx˜{maxSafeCx}. Reduce cx or p_width, or use longer guide curves.";
						sb.AppendLine(msg);
						AddLimitedWarning(warnings, msg);
						overpackedCellDomains++;
						skippedDomains++;
						continue;
					}

					leftover = Math.Max(0.0, leftover);
					double tightMargin = Math.Max(pathWidth, curveLen * 0.02);
					if (cellCountTan > 1 && leftover <= tightMargin)
					{
						int maxSafeCx = EstimateMaxCellCountTan(curveLen, xClosedUsed);
						string msg =
							$"Layer {inPath} domain {di}: cell_count_tan/cx={cellCountTan} is close to the path-length limit. " +
							$"leftover={leftover:0.###}, p_width={pathWidth:0.###}, max_safe_cx˜{maxSafeCx}. " +
							"Small changes in bridge, clearance, or path width may skip this domain.";
						AddLimitedWarning(warnings, msg);
						tightCellDomains++;
					}

					double xy = leftover / (2.0 * cellCountTan);
					double xOpen = xClosedUsed + 2.0 * xy;

					double yaAbs = (ampNom <= 0.0) ? 0.0 : Math.Min(pathWidth * 0.8, 0.5 * ampNom);
					double yaN = (ampNom <= tol) ? 0.0 : Math.Max(0.0, Math.Min(1.0, yaAbs / ampNom));
					double yaB = teeth ? yaN : 0.0;
					double yBridgeN = teeth ? (yaN * 0.5 + bridgeParam * (1.0 - yaN)) : bridgeParam;

					var cells = new CellGeom[cellCountTan];
					double cursor = 0.0;
					for (int ci = 0; ci < cellCountTan; ci++)
					{
						double cellStart = cursor;
						double cellEnd = cellStart + xOpen;
						double center = 0.5 * (cellStart + cellEnd);

						double topSpan = Lerp(xOpen, xClosedUsed, bridgeParam);
						double botSpan = Lerp(xClosedUsed, xOpen, bridgeParam);

						double xtL = center - 0.5 * topSpan;
						double xtR = center + 0.5 * topSpan;
						double xbL = center - 0.5 * botSpan;
						double xbR = center + 0.5 * botSpan;
						double xy2Dx = 0.5 * (topSpan - xClosedUsed);
						if (Math.Abs(xy2Dx) < eps) xy2Dx = 0.0;

						cells[ci] = new CellGeom
						{
							cell_start = cellStart,
							cell_end = cellEnd,
							top_span = topSpan,
							bot_span = botSpan,
							xt_L = xtL,
							xt_R = xtR,
							xb_L = xbL,
							xb_R = xbR,
							xy2_dx = xy2Dx
						};

						cursor += xOpen;
						if (ci < cellCountTan - 1)
							cursor += xClosedUsed;
					}

					var localXY = new List<Point2d>(12 * cellCountTan + 4 * Math.Max(0, cellCountTan - 1));
					AddUniquePoint(localXY, cells[0].xb_L, 0.0, eps);

					for (int ci = 0; ci < cellCountTan; ci++)
					{
						var c = cells[ci];

						AddUniquePoint(localXY, c.xb_L, yaB, eps);
						AddUniquePoint(localXY, c.xt_L, 1.0 - yaB, eps);
						AddUniquePoint(localXY, c.xt_L, 1.0, eps);
						AddUniquePoint(localXY, c.xt_L + xaUsed, 1.0, eps);
						AddUniquePoint(localXY, c.xt_L + xaUsed + c.xy2_dx, yBridgeN, eps);
						AddUniquePoint(localXY, c.xt_L + xaUsed + c.xy2_dx + xrUsed, yBridgeN, eps);
						AddUniquePoint(localXY, c.xt_L + xaUsed + c.xy2_dx + xrUsed + c.xy2_dx, 1.0, eps);
						AddUniquePoint(localXY, c.xt_R, 1.0, eps);
						AddUniquePoint(localXY, c.xt_R, 1.0 - yaB, eps);
						AddUniquePoint(localXY, c.xb_R, yaB, eps);
						AddUniquePoint(localXY, c.xb_R, 0.0, eps);

						if (ci < cellCountTan - 1)
						{
							var n = cells[ci + 1];
							double s0 = c.xb_R;
							double sE = n.xb_L;
							double dyN = Math.Max(0.0, (1.0 - yaB) - yBridgeN);
							double yTr = yaB + dyN;

							double span = Math.Max(eps, sE - s0);
							double xaEff = xaUsed;
							double xrEff = xrUsed;

							double maxXa = 0.5 * Math.Max(0.0, span - xrEff);
							if (xaEff > maxXa) xaEff = maxXa;

							if (span < xrEff)
							{
								xrEff = span;
								xaEff = 0.0;
							}

							double xa1End = s0 + xaEff;
							double xa2Start = sE - xaEff;
							double freeSpan = span - 2.0 * xaEff;
							double xrStart = s0 + xaEff + 0.5 * Math.Max(0.0, freeSpan - xrEff);
							double xrEnd = xrStart + xrEff;

							AddUniquePoint(localXY, xa1End, 0.0, eps);
							AddUniquePoint(localXY, xrStart, yTr, eps);
							AddUniquePoint(localXY, xrEnd, yTr, eps);
							AddUniquePoint(localXY, xa2Start, 0.0, eps);
							AddUniquePoint(localXY, sE, 0.0, eps);
						}
					}

					for (int i = 0; i < localXY.Count; i++)
					{
						double xi = Math.Max(0.0, Math.Min(curveLen, localXY[i].X));
						localXY[i] = new Point2d(xi, localXY[i].Y);
					}

					for (int ri = 0; ri < cellCountPerp; ri++)
					{
						double f0Raw = (double)ri / (double)cellCountPerp;
						double f1Raw = (double)(ri + 1) / (double)cellCountPerp;

						var ptsLower = new List<Point3d>(localXY.Count);
						var ptsUpper = new List<Point3d>(localXY.Count);

						for (int i = 0; i < localXY.Count; i++)
						{
							double si = localXY[i].X;
							double yN = localXY[i].Y;

							double tB;
							if (!crvB.LengthParameter(si, out tB))
								tB = crvB.Domain.ParameterAt((curveLen <= tol) ? 0.0 : si / curveLen);
							Point3d pB = crvB.PointAt(tB);

							double uLen = (curveLen <= tol) ? 0.0 : si / curveLen;
							double tT;
							if (!crvT.LengthParameter(uLen * lenT, out tT))
								tT = crvT.Domain.ParameterAt(uLen);
							Point3d pT = crvT.PointAt(tT);

							Vector3d vBT = pT - pB;
							double gap = vBT.Length;
							if (gap <= tol)
							{
								ptsLower.Add(pB);
								ptsUpper.Add(pB);
								continue;
							}
							vBT.Unitize();

							double inset = Math.Max(0.0, Math.Min(clearGuide, 0.5 * gap - eps));
							Point3d pBIn = pB + vBT * inset;
							double gapIn = Math.Max(0.0, gap - 2.0 * inset);
							if (gapIn <= tol)
							{
								ptsLower.Add(pBIn);
								ptsUpper.Add(pBIn);
								continue;
							}

							double abs0 = f0Raw * gapIn + clearance;
							double abs1 = f1Raw * gapIn - clearance;
							if (abs0 >= abs1 - eps)
							{
								Point3d pMid = pBIn + vBT * (0.5 * (f0Raw + f1Raw) * gapIn);
								ptsLower.Add(pMid);
								ptsUpper.Add(pMid);
								continue;
							}

							double bandGap = abs1 - abs0;
							double ampBand = Math.Max(0.0, bandGap * 0.5 - clearance);
							double off = yN * ampBand;

							Point3d p0 = pBIn + vBT * abs0;
							Point3d p1 = pBIn + vBT * abs1;

							ptsLower.Add(p0 + vBT * off);
							ptsUpper.Add(p1 - vBT * off);
						}

						ExtendPathEnds(ptsLower, extendEnds, tol);
						ExtendPathEnds(ptsUpper, extendEnds, tol);

						layerPrintArea += PolylineLength(ptsLower) * pathWidth;
						layerPrintArea += PolylineLength(ptsUpper) * pathWidth;

						GH_Path pathL = trimPaths ? layerOutPath : AppendPath(inPath, di, ri, 0);
						GH_Path pathU = trimPaths ? layerOutPath : AppendPath(inPath, di, ri, 1);

						outCrvs.Add(new PolylineCurve(new Polyline(ptsLower)), pathL);
						outCrvs.Add(new PolylineCurve(new Polyline(ptsUpper)), pathU);

						for (int i = 0; i < ptsLower.Count; i++) outPts.Add(ptsLower[i], pathL);
						for (int i = 0; i < ptsUpper.Count; i++) outPts.Add(ptsUpper[i], pathU);

						made += 2;
					}

					sb.AppendLine(
						$"Layer {inPath} domain {di}: cx={cellCountTan}, cy={cellCountPerp}, cz={cellCountZ:0.###}, w_shell={wShellD:0.###}, n_shell={nShellD}, clear_long={clearLong:0.###}, clear_guide={clearGuide:0.###}, clear_in={clearIn:0.###}, ext={extendEnds:0.###}, pW={pathWidth:0.###}, b0={bridgeP0:0.###}, b1={bridgeP1:0.###}, teeth={teeth}, strict={strictZeroLayer}, blend={blend:0.###}, x_closed={xClosedUsed:0.###}, xa={xaUsed:0.###}, xr={xrUsed:0.###}, d_parallel={dParallel:0.###}"
					);
				}

				// Per-layer porosity estimate
				double layerPor = (layerTotalArea > 0.0)
					? Math.Max(0.0, Math.Min(1.0, 1.0 - layerPrintArea / layerTotalArea))
					: 0.0;
				porLayer.Add(layerPor);
				sb.AppendLine($"Layer {inPath}: porosity_est={layerPor:0.0000}  (printed_area={layerPrintArea:0.##}, total_area={layerTotalArea:0.##})");
			}

			double porAvg = (porLayer.Count > 0) ? porLayer.Average() : 0.0;

			if (warnings.Count > 0)
			{
				sb.AppendLine();
				sb.AppendLine("Warnings:");
				for (int i = 0; i < warnings.Count; i++)
					sb.AppendLine("  - " + warnings[i]);
			}

			sb.Insert(0,
				"wsp_In14_Turtle Cells (Trunc. Octa)\n" +
				"-----------------------------------\n" +
				$"layers_in              : {layerCount}\n" +
				$"domains_total          : {totalDomains}\n" +
				$"made_paths             : {made}\n" +
				$"skipped_layers         : {skippedLayers}\n" +
				$"skipped_domains        : {skippedDomains}\n" +
				$"cell_count_warnings    : overpacked={overpackedCellDomains}, tight={tightCellDomains}\n" +
				$"non_planar_layers      : {nonPlanarLayers}\n" +
				$"max_plane_deviation    : {maxLayerPlaneDeviation:0.###}\n" +
				$"trim_paths             : {trimPaths}\n" +
				$"por_avg                : {porAvg:0.0000}\n" +
				$"defaults               : w_shell=5, n_shell=1, clear_long/clear_guide=dynamic, clear_in=0, ext=0, p_width=4, cx=6, cy=1, cz=0, b0=0, b1=1, teeth=false\n\n"
			);

			if (overpackedCellDomains > 0)
				AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
					$"{overpackedCellDomains} domain(s) were skipped because cell_count_tan/cx is too high for the guide length and path width. See summary for max_safe_cx.");
			else if (tightCellDomains > 0)
				AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
					$"{tightCellDomains} domain(s) are close to the cell-count/path-width length limit. See summary for max_safe_cx.");

			DA.SetDataTree(0, outShell);
			DA.SetDataTree(1, outCrvs);
			DA.SetDataTree(2, outParts);
			DA.SetDataTree(3, outPts);
			DA.SetData(4, sb.ToString());
			DA.SetDataList(5, porLayer);
			DA.SetData(6, porAvg);
			DA.SetDataTree(7, outPlanes);
		}

		private struct CellGeom
		{
			public double cell_start;
			public double cell_end;
			public double top_span;
			public double bot_span;
			public double xt_L;
			public double xt_R;
			public double xb_L;
			public double xb_R;
			public double xy2_dx;
		}

		private static void EnsureDefaults<T>(List<T> list, T defaultValue)
		{
			if (list == null) return;
			if (list.Count == 0) list.Add(defaultValue);
		}

		private static T GetCycled<T>(List<T> list, int index)
		{
			if (list == null || list.Count == 0)
				throw new InvalidOperationException("Input list must contain at least one value.");
			return list[index % list.Count];
		}

		private static double SanitizeNonNegative(double value, double fallback)
		{
			if (double.IsNaN(value) || value < 0.0) return fallback;
			return value;
		}

		private static int EstimateMaxCellCountTan(double curveLen, double xClosedUsed)
		{
			if (curveLen <= 0.0 || xClosedUsed <= 0.0)
				return 0;

			return Math.Max(0, (int)Math.Floor(0.5 * (curveLen / xClosedUsed + 1.0)));
		}

		private static void AddLimitedWarning(List<string> warnings, string message, int maxCount = 12)
		{
			if (warnings == null || string.IsNullOrWhiteSpace(message))
				return;

			if (warnings.Count < maxCount)
			{
				warnings.Add(message);
			}
			else if (warnings.Count == maxCount)
			{
				warnings.Add("Additional cell-count/path-width warnings were omitted from the summary.");
			}
		}

		private static void OrientGuideChain(List<Curve> guides)
		{
			if (guides == null || guides.Count < 2) return;

			Vector3d refTan = guides[0].TangentAt(guides[0].Domain.T0);
			if (!refTan.IsValid) return;
			refTan.Unitize();

			for (int i = 1; i < guides.Count; i++)
			{
				Vector3d t = guides[i].TangentAt(guides[i].Domain.T0);
				if (!t.IsValid) continue;
				t.Unitize();
				if (Vector3d.Multiply(refTan, t) < 0.0)
					guides[i].Reverse();
			}
		}

		/// <summary>
		/// Generates n shell polylines offset inward from outerGuide toward innerGuide.
		/// Shell line i (i = 1 … nShell) is centred at (i - 0.5) × wShell from outerGuide
		/// (Slicer Plus convention). Each polyline follows the arc-length parameterisation
		/// of outerGuide, with corresponding points found on innerGuide.
		/// </summary>
		/// <summary>
		/// Pairs open shell polylines from two opposite outermost guides into closed loops.
		/// side-0 forward ? end cap ? side-N reversed ? start cap ? close.
		/// Produces Math.Min(side0.Count, sideN.Count) closed curves.
		/// Each successive loop is shorter because it sits further inward (higher shell index).
		/// </summary>
		private static List<Curve> CloseShellPairs(List<Curve> side0, List<Curve> sideN, double tol)
		{
			var result = new List<Curve>();
			int n = Math.Min(side0.Count, sideN.Count);
			for (int i = 0; i < n; i++)
			{
				var pc0 = side0[i] as PolylineCurve;
				var pcN = sideN[i] as PolylineCurve;
				if (pc0 == null || pcN == null) continue;
				var poly0 = pc0.ToPolyline();
				var polyN = pcN.ToPolyline();
				if (poly0 == null || polyN == null) continue;
				var pts0 = new List<Point3d>(poly0);
				var ptsN = new List<Point3d>(polyN);
				if (pts0.Count < 2 || ptsN.Count < 2) continue;

				// Orient sideN so its far end connects to the far end of side0
				bool reverseN = pts0[pts0.Count - 1].DistanceTo(ptsN[ptsN.Count - 1]) <=
				                pts0[pts0.Count - 1].DistanceTo(ptsN[0]);

				var combined = new List<Point3d>(pts0.Count + ptsN.Count + 1);
				combined.AddRange(pts0);
				if (reverseN)
					for (int k = ptsN.Count - 1; k >= 0; k--) combined.Add(ptsN[k]);
				else
					combined.AddRange(ptsN);
				combined.Add(pts0[0]); // close

				if (combined.Count >= 3)
					result.Add(new PolylineCurve(combined));
			}
			return result;
		}

		private static List<Curve> BuildShellPaths(
			Curve outerGuide, Curve innerGuide,
			int nShell, double wShell,
			double sampleRes, double tol,
			bool insetEnds = false)
		{
			var result = new List<Curve>();
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

		private static Curve ShortenCurve(Curve crv, double amount, double tol, double eps)
		{
			double len = crv.GetLength();
			double endLen = len - amount;
			if (endLen <= amount + tol) return null;

			double tStart, tEnd;
			if (!crv.LengthParameter(amount, out tStart)) tStart = crv.Domain.ParameterAt(amount / len);
			if (!crv.LengthParameter(endLen, out tEnd)) tEnd = crv.Domain.ParameterAt(endLen / len);
			if (tStart >= tEnd - eps) return null;

			Curve trimmed = crv.Trim(tStart, tEnd);
			return (trimmed != null && trimmed.IsValid) ? trimmed : null;
		}

		private static GH_Path AppendPath(GH_Path basePath, int domain, int band, int which)
		{
			int[] baseIdx = basePath.Indices;
			int[] idx = new int[baseIdx.Length + 3];
			for (int k = 0; k < baseIdx.Length; k++) idx[k] = baseIdx[k];
			idx[baseIdx.Length] = domain;
			idx[baseIdx.Length + 1] = band;
			idx[baseIdx.Length + 2] = which;
			return new GH_Path(idx);
		}

		private static void AddUniquePoint(List<Point2d> pts, double x, double y, double tol)
		{
			Point2d p = new Point2d(x, y);
			if (pts.Count == 0)
			{
				pts.Add(p);
				return;
			}

			if (pts[pts.Count - 1].DistanceTo(p) > tol)
				pts.Add(p);
		}

		private static void ExtendPathEnds(List<Point3d> pts, double extendLen, double tol)
		{
			if (pts == null || pts.Count < 2 || extendLen <= tol)
				return;

			Vector3d vStart = pts[0] - pts[1];
			if (vStart.IsValid && vStart.Length > tol)
			{
				vStart.Unitize();
				pts[0] = pts[0] + vStart * extendLen;
			}

			int last = pts.Count - 1;
			Vector3d vEnd = pts[last] - pts[last - 1];
			if (vEnd.IsValid && vEnd.Length > tol)
			{
				vEnd.Unitize();
				pts[last] = pts[last] + vEnd * extendLen;
			}
		}

		private static double Clamp01(double x)
		{
			return x < 0.0 ? 0.0 : x > 1.0 ? 1.0 : x;
		}

		private static double Lerp(double a, double b, double t)
		{
			return a + (b - a) * t;
		}

		private static double TriangleWave01(double t01, double cycles)
		{
			if (cycles <= 0.0) return 0.0;
			double f = (t01 * cycles) - Math.Floor(t01 * cycles);
			return Clamp01(1.0 - Math.Abs(2.0 * f - 1.0));
		}

		private static double PolylineLength(IList<Point3d> pts)
		{
			double len = 0.0;
			for (int i = 1; i < pts.Count; i++)
				len += pts[i].DistanceTo(pts[i - 1]);
			return len;
		}

		private static double EstimateAverageGap(Curve bottom, Curve top, double tol)
		{
			double lenB = bottom.GetLength();
			double lenT = top.GetLength();
			if (lenB <= tol || lenT <= tol) return 0.0;

			double[] u = { 0.0, 0.25, 0.5, 0.75, 1.0 };
			double sum = 0.0;

			foreach (double ui in u)
			{
				double tB, tT;
				if (!bottom.LengthParameter(ui * lenB, out tB)) tB = bottom.Domain.ParameterAt(ui);
				if (!top.LengthParameter(ui * lenT, out tT)) tT = top.Domain.ParameterAt(ui);
				sum += bottom.PointAt(tB).DistanceTo(top.PointAt(tT));
			}

			return sum / u.Length;
		}
	}
}
