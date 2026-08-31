#region Component Description
/*
	Component Name:
		wsp_In16_Multi-Infill 2D

	Nickname:
		MultiInfill2D

	Version:
		v1.0.5 - 260420

	Category / Subcategory:
		WASPer_3DP / 2_Infills

	Description:
		Generates S-like infill paths between N co-planar guide curves (>=2).
		Each consecutive pair [i]/[i+1] is treated as one "domain" and can receive
		its own settings by cycling list inputs.

		Four pattern types are supported:
		  1 = Square S  (continuous step / rectangular wave)
		  2 = Sticks    (isolated perpendicular segments, separate curves)
		  3 = Triangle  (sharp triangular wave)
		  4 = Sine      (smooth sinusoidal wave)

		New in v1.0.5
		--------------
		1. Added "shell" output as the first output.
		   - One closed polyline per input branch / layer.
		   - Built from the outermost guide curves of the branch.
		   - Shell generation ignores shorten; shorten only affects infill.
		   - Output structure is always one branch per layer.

		2. Added "partitions" output after shell.
		   - Outputs the interior guide curves, excluding the two outer shell guides.
		   - Output structure is always one branch per layer.

		3. Added "trim_paths" boolean input.
		   - false: {layer ; domain ; path}
		   - true:  {layer}

		4. Expanded per-domain list behaviour.
		   The following inputs now accept LISTS and cycle per domain:
		   - shorten
		   - inset
		   - clear
		   - type
		   - flip
		   - count
		   - phase_shift
		   - res, as approximate sample spacing in model units

		5. Added default values to every input except guide_curves for easier use.
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
using Grasshopper.Kernel.Parameters;
#endregion

namespace WASPer_3DP.Components._3_1_Infills
{
	public class wsp_In16_Multi_Infill_2D_260420 : GH_Component
	{
		private readonly string _versionTag;
		private const int PARALLEL_THRESHOLD = 8;

		public wsp_In16_Multi_Infill_2D_260420()
			: base(
				"wsp_In16_Multi-Infill 2D",
				"MultiInfill2D",
				"Generates S-like infill paths between N co-planar guide curves (>=2).\n" +
				"Each consecutive pair [i]/[i+1] is one domain and can receive its own settings.\n\n" +
				"Pattern types:\n" +
				"  1 = Square S\n" +
				"  2 = Sticks\n" +
				"  3 = Triangle\n" +
				"  4 = Sine\n\n" +
				"List inputs cycle per domain: shorten, inset, clear, type, flip, count, phase, res.\n" +
				"trim_paths:\n" +
				"  false = {layer;domain;path}\n" +
				"  true  = {layer}\n\n" +
				"shell output:\n" +
				"  one closed polyline per layer, built from the outermost guides.",
				global::WASPer_3DP.WASPerPalette.DesignFabrication,
				"3.1_Infills")
		{
			var asm = System.Reflection.Assembly.GetExecutingAssembly();
			var v = asm.GetName().Version;
			_versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
			Message = _versionTag;
		}

		public override Guid ComponentGuid =>
			new Guid("7C2A1B3D-4E5F-4A6B-8C9D-1E2F3A4B5C8D");

		public override GH_Exposure Exposure => GH_Exposure.hidden;

		protected override System.Drawing.Bitmap Icon
		{
			get
			{
       try
       {
    				var asm = System.Reflection.Assembly.GetExecutingAssembly();
    				using (var s = asm.GetManifestResourceStream(
    					"WASPer_3DP.Resources.Icons.wsp_In16_Multi-Infill 2D.png"))
    					return s != null ? new System.Drawing.Bitmap(s) : null;
       }
       catch { }
       return null;
			}
		}

		// -- IO ----------------------------------------------------------------
		protected override void RegisterInputParams(GH_InputParamManager pManager)
		{
			pManager.AddCurveParameter(
				"guide_curves", "guides",
				"Guide curves as a DataTree. Each branch = one layer.\n" +
				"Each branch must contain >= 2 curves, ordered from one side to the other.\n" +
				"The component auto-aligns all curves to the same direction.\n" +
				"Pattern is generated between pairs [0?1], [1?2], etc.",
				GH_ParamAccess.tree);

			AddNumberListParam(
				pManager,
				"w_shell", "w_shell",
				"Shell line width in model units (Slicer Plus convention).\n" +
				"Shell line i is centred at (i - 0.5) × w_shell inward from the outermost guide.\n" +
				"Default 5. 0 = no shell output. LIST. Cycles per domain.",
				5.0);

			AddIntegerListParam(
				pManager,
				"n_shell", "n_shell",
				"Number of shell lines per outermost guide. Minimum 1. LIST. Cycles per domain.",
				1);

			AddNumberListParam(
				pManager,
				"clear_long", "clear_long",
				"Arc-length clearance trimmed from each end of both guides (model units).\n" +
				"Only affects infill — shell uses full guides. LIST. Cycles per domain.\n" +
				"Default formula when shell active: max(0, w_shell × n_shell - w_shell / 2).",
				0.0);

			AddNumberListParam(
				pManager,
				"clear_guide", "clear_guide",
				"Gap clearance from the OUTERMOST guide boundaries (model units).\n" +
				"Only affects infill — shell is built before this is applied. LIST. Cycles per domain.\n" +
				"Default formula when shell active: max(0, w_shell × n_shell - w_shell / 2).",
				0.0);

			AddNumberListParam(
				pManager,
				"clear_in", "clear_in",
				"Gap clearance from INTERMEDIATE guide boundaries (model units).\n" +
				"LIST. Cycles per domain.",
				0.0);

			AddIntegerListParam(
				pManager,
				"type", "type",
				"Pattern type per domain — accepts a LIST and cycles.\n" +
				"  1 = Square S\n" +
				"  2 = Sticks\n" +
				"  3 = Triangle\n" +
				"  4 = Sine",
				4);

			AddBooleanListParam(
				pManager,
				"flip", "flip",
				"Flip state per domain — accepts a LIST and cycles.\n" +
				"false = normal (curve A -> curve B)\n" +
				"true  = flipped (curve B -> curve A)",
				false);

			AddIntegerListParam(
				pManager,
				"count", "count",
				"Number of cells per domain — accepts a LIST and cycles.\n" +
				"Minimum 1.",
				4);

			AddNumberListParam(
				pManager,
				"phase_shift", "phase",
				"Phase offset [0..1] per domain. 0.5 = half-cell shift; 1.0 wraps to 0.0.\n" +
				"Accepts a LIST and cycles per domain.",
				0.0);

			pManager.AddBooleanParameter(
				"trim_paths", "trim",
				"Output tree structure for shell, infill, partitions, and points.\n" +
				"False = {layer;domain;path}, True = {layer}. Default true.",
				GH_ParamAccess.item,
				true);

			pManager.AddBooleanParameter(
				"close_shell", "close_shell",
				"If true, each shell polyline is closed (first point appended at the end).\n" +
				"When n_shell > 1 each individual shell line is closed independently. Default true.",
				GH_ParamAccess.item,
				true);

			pManager.AddNumberParameter(
				"res", "res",
				"Sampling resolution in model units for shell and infill curves. Smaller values create smoother curves and heavier computation.\n" +
				"If <= 0 or unwired, the component auto-derives a spacing from the guide lengths to keep roughly the previous 64-sample quality.",
				GH_ParamAccess.item,
				0.0);
			pManager[12].Optional = true;
		}

		protected override void RegisterOutputParams(GH_OutputParamManager pManager)
		{
			pManager.AddCurveParameter(
				"shell", "shell",
				"Shell polylines offset inward from outermost guides (n_shell lines per guide, Slicer Plus convention).\n" +
				"One branch per layer.",
				GH_ParamAccess.tree);

			pManager.AddCurveParameter(
				"infill", "infill",
				"Generated infill paths as PolylineCurves.\n" +
				"trim_paths=false: {layer;domain;path}\n" +
				"trim_paths=true : {layer}.",
				GH_ParamAccess.tree);

			pManager.AddCurveParameter(
				"partitions", "partitions",
				"Interior guide curves, excluding the two outer shell guides.\n" +
				"One branch per layer.",
				GH_ParamAccess.tree);

			pManager.AddPointParameter(
				"pts", "pts",
				"Polyline points matching the infill output. Same tree structure as infill.",
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

		// -- Solve -------------------------------------------------------------
		protected override void SolveInstance(IGH_DataAccess DA)
		{
			GH_Structure<GH_Curve> guideTree = null;

			var wShellList    = new List<double>();
			var nShellList    = new List<int>();
			var clearLongList = new List<double>();
			var clearGuideList= new List<double>();
			var clearInList   = new List<double>();
			var typeList  = new List<int>();
			var flipList  = new List<bool>();
			var countList = new List<int>();
			var phaseList = new List<double>();
			bool trimPaths  = true;
			bool closeShell = true;
			double res = 0.0;

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
			DA.GetDataList(3,  clearLongList);
			DA.GetDataList(4,  clearGuideList);
			DA.GetDataList(5,  clearInList);
			DA.GetDataList(6,  typeList);
			DA.GetDataList(7,  flipList);
			DA.GetDataList(8,  countList);
			DA.GetDataList(9,  phaseList);
			DA.GetData(10, ref trimPaths);
			DA.GetData(11, ref closeShell);
			DA.GetData(12, ref res);

			// -- Sanitize lists -------------------------------------------------
			if (wShellList.Count    == 0) wShellList.Add(5.0);
			if (nShellList.Count    == 0) nShellList.Add(1);
			// clearLongList / clearGuideList: no static default — dynamic per domain (w_shell × n_shell - w_shell / 2)
			if (clearInList.Count   == 0) clearInList.Add(0.0);
			if (typeList.Count  == 0) typeList.Add(4);
			if (flipList.Count  == 0) flipList.Add(false);
			if (countList.Count == 0) countList.Add(4);
			if (phaseList.Count == 0) phaseList.Add(0.0);
			for (int i = 0; i < wShellList.Count;     i++) wShellList[i]    = Math.Max(0.0, wShellList[i]);
			for (int i = 0; i < nShellList.Count;     i++) nShellList[i]    = Math.Max(1,   nShellList[i]);
			for (int i = 0; i < clearLongList.Count;  i++) clearLongList[i] = Math.Max(0.0, clearLongList[i]);
			for (int i = 0; i < clearGuideList.Count; i++) clearGuideList[i]= Math.Max(0.0, clearGuideList[i]);
			for (int i = 0; i < clearInList.Count;    i++) clearInList[i]   = Math.Max(0.0, clearInList[i]);
			for (int i = 0; i < typeList.Count; i++) typeList[i] = Clamp(typeList[i], 1, 4);
			for (int i = 0; i < countList.Count; i++) countList[i] = Math.Max(1, countList[i]);
			for (int i = 0; i < phaseList.Count; i++) phaseList[i] = Wrap01(phaseList[i]);
			string typeTag = BuildListTag(typeList, v => v == 1 ? "Sq" : v == 2 ? "Stk" : v == 3 ? "Tri" : "Sin");
			string flipTag = BuildListTag(flipList, v => v ? "Flip" : "NoFlip");
			Message = $"{_versionTag} | {typeTag} | {flipTag}";

			double tol = RhinoDoc.ActiveDoc != null
				? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
				: 1e-6;
			double eps = Math.Max(1e-9, tol * 0.1);

			int branchCount = guideTree.PathCount;

			var perBranchShells     = new List<(GH_Path path, PolylineCurve crv)>[branchCount];
			var perBranchPartitions = new List<(GH_Path path, Curve crv)>[branchCount];
			var perBranchCurves     = new List<(GH_Path path, PolylineCurve crv)>[branchCount];
			var perBranchPts        = new List<(GH_Path path, Point3d pt)>[branchCount];
			var perBranchLog        = new string[branchCount];
			var perBranchPor        = new double[branchCount];

			for (int i = 0; i < branchCount; i++)
			{
				perBranchShells[i] = new List<(GH_Path, PolylineCurve)>();
				perBranchPartitions[i] = new List<(GH_Path, Curve)>();
				perBranchCurves[i] = new List<(GH_Path, PolylineCurve)>();
				perBranchPts[i] = new List<(GH_Path, Point3d)>();
				perBranchLog[i] = "";
			}

			int skipped = 0;
			int totalShells = 0;
			int totalPaths = 0;

			// -- Capture locals for lambda -------------------------------------
			var _wShellList    = wShellList;
			var _nShellList    = nShellList;
			var _clearLongList = clearLongList;
			var _clearGuideList= clearGuideList;
			var _clearInList   = clearInList;
			var _typeList  = typeList;
			var _flipList  = flipList;
			var _countList = countList;
			var _phaseList = phaseList;
			double _res    = res;
			bool _trimPaths  = trimPaths;
			bool _closeShell = closeShell;

			Action<int> processBranch = bi =>
			{
				var log = new StringBuilder();
				var shells = new List<(GH_Path, PolylineCurve)>();
				var partitions = new List<(GH_Path, Curve)>();
				var crvs = new List<(GH_Path, PolylineCurve)>();
				var pts = new List<(GH_Path, Point3d)>();

				GH_Path brPath = guideTree.Paths[bi];
				GH_Path layerPath = _trimPaths ? new GH_Path(bi) : new GH_Path(brPath.Indices);
				var br = guideTree.Branches[bi];

				void Bail(string msg)
				{
					if (!string.IsNullOrEmpty(msg))
						log.AppendLine(msg);

					Interlocked.Increment(ref skipped);
					perBranchShells[bi] = shells;
					perBranchPartitions[bi] = partitions;
					perBranchCurves[bi] = crvs;
					perBranchPts[bi] = pts;
					perBranchLog[bi] = log.ToString();
				}

				if (br == null || br.Count < 2)
				{
					Bail($"Branch {brPath}: needs >= 2 curves. Skipped.");
					return;
				}

				int nCurves = br.Count;
				int nPairs = nCurves - 1;

				var curves = new List<Curve>(nCurves);
				var lengths = new double[nCurves];

				for (int ci = 0; ci < nCurves; ci++)
				{
					Curve c = br[ci]?.Value?.DuplicateCurve();
					if (c == null || !c.IsValid)
					{
						Bail($"Branch {brPath}: curve [{ci}] is null or invalid. Skipped.");
						return;
					}

					curves.Add(c);
				}

				// Align directions
				Vector3d refTan = curves[0].TangentAt(curves[0].Domain.T0);
				if (refTan.IsValid) refTan.Unitize();

				for (int ci = 1; ci < curves.Count; ci++)
				{
					Vector3d tan = curves[ci].TangentAt(curves[ci].Domain.T0);
					if (!tan.IsValid) continue;

					tan.Unitize();
					if (Vector3d.Multiply(refTan, tan) < 0.0)
						curves[ci].Reverse();
				}

				for (int ci = 0; ci < curves.Count; ci++)
				{
					lengths[ci] = curves[ci].GetLength();
					if (lengths[ci] <= tol)
					{
						Bail($"Branch {brPath}: curve [{ci}] is too short. Skipped.");
						return;
					}
				}

				// -- Shell paths (Slicer Plus convention) + porosity accumulators -
				double branchPrintArea = 0.0;
				double branchTotalArea = 0.0;
				{
					double tol15 = tol;
					int    lastDi  = Math.Max(0, nCurves - 2);
					double wShell0 = _wShellList.Count > 0 ? Math.Max(0.0, _wShellList[0 % _wShellList.Count]) : 0.0;
					int    nShell0 = _nShellList.Count  > 0 ? Math.Max(1,   _nShellList[0 % _nShellList.Count])  : 1;
					double wShellN = _wShellList.Count > 0 ? Math.Max(0.0, _wShellList[lastDi % _wShellList.Count]) : 0.0;
					int    nShellN = _nShellList.Count  > 0 ? Math.Max(1,   _nShellList[lastDi % _nShellList.Count])  : 1;

					if (_closeShell && wShell0 > tol15 && wShellN > tol15)
					{
						int nS  = Math.Min(nShell0, nShellN);
						var sp0 = BuildShellPaths(curves[0], curves[1], nS, wShell0, _res, tol15, true);
						var spN = BuildShellPaths(curves[nCurves - 1], curves[nCurves - 2], nS, wShellN, _res, tol15, true);
						foreach (var c in CloseShellPairs(sp0, spN, tol15))
						{
							shells.Add((layerPath, c));
							Interlocked.Increment(ref totalShells);
						}
					}
					else
					{
						if (wShell0 > tol15)
						{
							var sp0 = BuildShellPaths(curves[0], curves[1], nShell0, wShell0, _res, tol15);
							foreach (var sp in sp0)
							{
								shells.Add((layerPath, sp));
								Interlocked.Increment(ref totalShells);
							}
						}
						if (wShellN > tol15)
						{
							var spN = BuildShellPaths(curves[nCurves - 1], curves[nCurves - 2], nShellN, wShellN, _res, tol15);
							foreach (var sp in spN)
							{
								shells.Add((layerPath, sp));
								Interlocked.Increment(ref totalShells);
							}
						}
					}
					branchPrintArea += lengths[0] * wShell0 * nShell0;
					branchPrintArea += lengths[nCurves - 1] * wShellN * nShellN;
				}

				// Inner guides as partitions — shortened by (w_shell × n_shell - w_shell / 3) per domain
				for (int ci = 1; ci < nCurves - 1; ci++)
				{
					double wShP15 = _wShellList.Count > 0 ? Math.Max(0.0, _wShellList[ci % _wShellList.Count]) : 0.0;
					int    nShP15 = _nShellList.Count  > 0 ? Math.Max(1,   _nShellList[ci % _nShellList.Count])  : 1;
					double partShorten15 = Math.Max(0.0, wShP15 * nShP15 - wShP15 / 3.0);
					Curve pCrv = curves[ci].DuplicateCurve();
					if (pCrv != null && pCrv.IsValid)
					{
						if (partShorten15 > tol)
						{
							double pLen = pCrv.GetLength();
							if (pLen > 2.0 * partShorten15 + tol)
							{
								double tS, tE;
								if (!pCrv.LengthParameter(partShorten15, out tS)) tS = pCrv.Domain.ParameterAt(partShorten15 / pLen);
								if (!pCrv.LengthParameter(pLen - partShorten15, out tE)) tE = pCrv.Domain.ParameterAt((pLen - partShorten15) / pLen);
								if (tS < tE - 1e-12) { Curve tr = pCrv.Trim(tS, tE); if (tr != null && tr.IsValid) pCrv = tr; }
							}
						}
						partitions.Add((layerPath, pCrv));
					}
				}

				// -- Domains ---------------------------------------------------
				int localMade = 0;

				for (int pi = 0; pi < nPairs; pi++)
				{
					int domType = _typeList[pi % _typeList.Count];
					bool doFlip = _flipList[pi % _flipList.Count];
					int domCount = _countList[pi % _countList.Count];
					double domPhase = _phaseList[pi % _phaseList.Count];
					double _wSh15  = _wShellList.Count > 0 ? Math.Max(0.0, _wShellList[pi % _wShellList.Count]) : 0.0;
					int    _nSh15  = _nShellList.Count  > 0 ? Math.Max(1,   _nShellList[pi % _nShellList.Count])  : 1;
					double dynDef15 = Math.Max(0.0, _wSh15 * _nSh15 - _wSh15 * 0.5);
					double domShorten = (_clearLongList.Count  == 0) ? dynDef15 : _clearLongList [pi % _clearLongList.Count];
					double domInset   = (_clearGuideList.Count == 0) ? dynDef15 : _clearGuideList[pi % _clearGuideList.Count];
					double domClear     = _clearInList   [pi % _clearInList.Count];

					Curve crvA = curves[pi];
					Curve crvB = curves[pi + 1];
					double lenA = lengths[pi];
					double lenB = lengths[pi + 1];
					double domRes = EffectiveRes(_res, lenA, lenB, tol);

					// Accumulate domain area for porosity estimate
					{
						double sumGap = 0.0;
						int nSamp = 5;
						for (int gi = 0; gi < nSamp; gi++)
						{
							double s = (double)gi / (nSamp - 1);
							Point3d pA15 = PointAtNormalizedTrimmed(crvA, lenA, s, 0.0);
							Point3d pB15 = PointAtNormalizedTrimmed(crvB, lenB, s, 0.0);
							sumGap += pA15.DistanceTo(pB15);
						}
						branchTotalArea += (sumGap / nSamp) * lenA;
					}

					if (lenA <= 2.0 * domShorten + tol)
					{
						log.AppendLine($"Branch {brPath} domain [{pi}]: shorten too large for curve A. Skipped.");
						continue;
					}
					if (lenB <= 2.0 * domShorten + tol)
					{
						log.AppendLine($"Branch {brPath} domain [{pi}]: shorten too large for curve B. Skipped.");
						continue;
					}

					bool isOuterA = (pi == 0);
					bool isOuterB = (pi == nPairs - 1);
					double gapInsetA = isOuterA ? domInset : domClear;
					double gapInsetB = isOuterB ? domInset : domClear;

					GH_Path domPath = BuildOutputPath(brPath, layerPath, pi, -1, _trimPaths);

					Point3d BlendPoint(double s01, double yN)
					{
						Point3d pA = PointAtNormalizedTrimmed(crvA, lenA, s01, domShorten);
						Point3d pB = PointAtNormalizedTrimmed(crvB, lenB, s01, domShorten);

						Vector3d vAB = pB - pA;
						double gap = vAB.Length;
						if (gap <= tol)
							return pA;

						vAB.Unitize();

						double maxInset = Math.Max(0.0, gap * 0.5 - eps);
						double ia = Math.Min(gapInsetA, maxInset);
						double ib = Math.Min(gapInsetB, maxInset);

						Point3d pAin = pA + vAB * ia;
						double usable = Math.Max(0.0, gap - ia - ib);
						return pAin + vAB * (yN * usable);
					}

					if (domType == 2)
					{
						for (int si = 0; si < domCount; si++)
						{
							double sCentre = (((si + 0.5 + (doFlip ? -domPhase : domPhase)) % domCount) + domCount) % domCount / domCount;
							Point3d stPtA = BlendPoint(sCentre, doFlip ? 1.0 : 0.0);
							Point3d stPtB = BlendPoint(sCentre, doFlip ? 0.0 : 1.0);

							if (stPtA.DistanceTo(stPtB) <= tol)
								continue;

							GH_Path stickPath = BuildOutputPath(brPath, layerPath, pi, si, _trimPaths);
							var stickPl = new Polyline(new[] { stPtA, stPtB });
							if (!stickPl.IsValid || stickPl.Length <= tol)
								continue;

							var stickCrv = new PolylineCurve(stickPl);
							if (stickCrv == null || !stickCrv.IsValid)
								continue;

							crvs.Add((stickPath, stickCrv));
							pts.Add((stickPath, stPtA));
							pts.Add((stickPath, stPtB));
							branchPrintArea += stPtA.DistanceTo(stPtB) * domRes;
							localMade++;
						}

						continue;
					}

					double usableA = Math.Max(tol, lenA - 2.0 * domShorten);
					double usableB = Math.Max(tol, lenB - 2.0 * domShorten);
					double avgCellLength = 0.5 * (usableA + usableB) / Math.Max(1, domCount);
					int samplesPerCell = Math.Max(2, (int)Math.Ceiling(avgCellLength / Math.Max(domRes, tol * 10.0)));
					int nSamples = Math.Max(2, domCount * samplesPerCell + 1);
					var polyPts = new List<Point3d>(nSamples + 4);

					for (int si = 0; si < nSamples; si++)
					{
						double s01 = (double)si / (double)(nSamples - 1);
						double patternPos = (((domCount * s01 + (doFlip ? -domPhase : domPhase)) % domCount) + domCount) % domCount;
						double cellT = patternPos - Math.Floor(patternPos);

						double yN = ShapeValue(domType, cellT);
						if (doFlip) yN = 1.0 - yN;

						polyPts.Add(BlendPoint(s01, yN));
					}

					if (!TryMakeValidPolyline(polyPts, tol, out Polyline pl))
					{
						log.AppendLine($"Branch {brPath} domain [{pi}]: could not build valid polyline.");
						continue;
					}

					pl.CollapseShortSegments(tol);
					if (!pl.IsValid || pl.Count < 2 || pl.Length <= tol)
						continue;

					var plc = new PolylineCurve(pl);
					if (plc == null || !plc.IsValid)
						continue;

					crvs.Add((domPath, plc));
					for (int p = 0; p < pl.Count; p++)
						pts.Add((domPath, pl[p]));
					branchPrintArea += pl.Length * domRes;

					localMade++;
				}

				if (localMade == 0)
				{
					log.AppendLine($"Branch {brPath}: no paths generated. Check guides and per-domain values.");
				}

				log.AppendLine($"Branch {brPath}: {nCurves} guides -> {nPairs} domains -> {localMade} paths.");

				Interlocked.Add(ref totalPaths, localMade);

				perBranchPor[bi] = branchTotalArea > 0.0
					? Math.Max(0.0, Math.Min(1.0, 1.0 - branchPrintArea / branchTotalArea))
					: 0.0;
				perBranchShells[bi]     = shells;
				perBranchPartitions[bi] = partitions;
				perBranchCurves[bi]     = crvs;
				perBranchPts[bi]        = pts;
				perBranchLog[bi]        = log.ToString();
			};

			if (branchCount < PARALLEL_THRESHOLD)
			{
				for (int bi = 0; bi < branchCount; bi++)
					processBranch(bi);
			}
			else
			{
				var po = new ParallelOptions
				{
					MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1)
				};
				Parallel.For(0, branchCount, po, bi => processBranch(bi));
			}

			// -- Assemble outputs ---------------------------------------------
			var outShells     = new DataTree<Curve>();
			var outPartitions = new DataTree<Curve>();
			var outCrvs       = new DataTree<Curve>();
			var outPts        = new DataTree<Point3d>();
			var outPlanes     = new DataTree<Plane>();
			var porLayer      = new List<double>(branchCount);
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

				foreach (var item in perBranchShells[bi])
					outShells.Add(item.crv, item.path);

				foreach (var item in perBranchPartitions[bi])
					outPartitions.Add(item.crv, item.path);

				foreach (var item in perBranchCurves[bi])
					outCrvs.Add(item.crv, item.path);

				foreach (var item in perBranchPts[bi])
					outPts.Add(item.pt, item.path);

				porLayer.Add(perBranchPor[bi]);
			}
			double porAvg = porLayer.Count > 0 ? porLayer.Average() : 0.0;

			var infoSb = new StringBuilder();
			infoSb.AppendLine("wsp_In16_Multi-Infill 2D  v1.3.0");
			infoSb.AppendLine("------------------------------");
			infoSb.AppendLine($"branches_in    : {branchCount}");
			infoSb.AppendLine($"skipped        : {skipped}");
			infoSb.AppendLine($"shells_made    : {totalShells}");
			infoSb.AppendLine($"paths_made     : {totalPaths}");
			infoSb.AppendLine($"non_planar_layers : {nonPlanarLayers}");
			infoSb.AppendLine($"max_plane_deviation: {maxLayerPlaneDeviation:0.###}");
			infoSb.AppendLine($"trim_paths     : {trimPaths}");
			infoSb.AppendLine($"close_shell    : {closeShell}");
			infoSb.AppendLine($"type           : [{string.Join(", ", typeList)}]  ({typeTag})");
			infoSb.AppendLine($"flip           : [{string.Join(", ", flipList.ConvertAll(f => f ? "true" : "false"))}]  ({flipTag})");
			infoSb.AppendLine($"count          : [{string.Join(", ", countList)}]");
			infoSb.AppendLine($"phase_shift    : [{string.Join(", ", phaseList.ConvertAll(p => p.ToString("0.###")))}]");
			infoSb.AppendLine($"clear_long     : [{string.Join(", ", clearLongList.ConvertAll(v => v.ToString("0.###")))}]");
			infoSb.AppendLine($"clear_guide    : [{string.Join(", ", clearGuideList.ConvertAll(v => v.ToString("0.###")))}]");
			infoSb.AppendLine($"clear_in       : [{string.Join(", ", clearInList.ConvertAll(v => v.ToString("0.###")))}]");
			infoSb.AppendLine($"por_avg        : {porAvg:0.####}");
			infoSb.AppendLine($"res_model_units: {(res > 0.0 ? res.ToString("0.###") : "auto")}");
			infoSb.AppendLine(branchCount < PARALLEL_THRESHOLD
				? $"parallel       : OFF (< {PARALLEL_THRESHOLD} branches — sequential)"
				: $"parallel       : ON  (max threads {Math.Max(1, Environment.ProcessorCount - 1)})");
			infoSb.AppendLine("------------------------------");

			for (int bi = 0; bi < branchCount; bi++)
			{
				if (!string.IsNullOrEmpty(perBranchLog[bi]))
					infoSb.Append(perBranchLog[bi]);
			}

			DA.SetDataTree(0, outShells);
			DA.SetDataTree(1, outCrvs);
			DA.SetDataTree(2, outPartitions);
			DA.SetDataTree(3, outPts);
			DA.SetData(4, infoSb.ToString());
			DA.SetDataList(5, porLayer);
			DA.SetData(6, porAvg);
			DA.SetDataTree(7, outPlanes);
		}

		// -- Shape functions ---------------------------------------------------
		private static double ShapeValue(int type, double cellT)
		{
			switch (type)
			{
				case 1:
					return cellT < 0.5 ? 0.0 : 1.0;

				case 3:
					return cellT < 0.5
						? 2.0 * cellT
						: 2.0 * (1.0 - cellT);

				case 4:
				default:
					return 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * cellT));
			}
		}

		// -- Helpers -----------------------------------------------------------
		private static void AddNumberListParam(
			GH_InputParamManager pManager,
			string name,
			string nick,
			string description,
			double defaultValue)
		{
			var p = new Param_Number
			{
				Name = name,
				NickName = nick,
				Description = description,
				Access = GH_ParamAccess.list,
				Optional = true
			};
			p.PersistentData.Append(new GH_Number(defaultValue));
			pManager.AddParameter(p);
		}

		private static void AddIntegerListParam(
			GH_InputParamManager pManager,
			string name,
			string nick,
			string description,
			int defaultValue)
		{
			var p = new Param_Integer
			{
				Name = name,
				NickName = nick,
				Description = description,
				Access = GH_ParamAccess.list,
				Optional = true
			};
			p.PersistentData.Append(new GH_Integer(defaultValue));
			pManager.AddParameter(p);
		}

		private static void AddBooleanListParam(
			GH_InputParamManager pManager,
			string name,
			string nick,
			string description,
			bool defaultValue)
		{
			var p = new Param_Boolean
			{
				Name = name,
				NickName = nick,
				Description = description,
				Access = GH_ParamAccess.list,
				Optional = true
			};
			p.PersistentData.Append(new GH_Boolean(defaultValue));
			pManager.AddParameter(p);
		}

		private static double Wrap01(double v)
		{
			v = v - Math.Floor(v);
			if (v >= 1.0) return 0.0;
			if (v < 0.0) return 0.0;
			return v;
		}

		private static GH_Path BuildOutputPath(GH_Path basePath, GH_Path trimPath, int domainIndex, int stickIndex, bool trimPaths)
		{
			if (trimPaths)
				return new GH_Path(trimPath.Indices);

			int[] baseIdx = basePath.Indices;
			if (stickIndex >= 0)
			{
				int[] idx = new int[baseIdx.Length + 2];
				for (int i = 0; i < baseIdx.Length; i++) idx[i] = baseIdx[i];
				idx[baseIdx.Length] = domainIndex;
				idx[baseIdx.Length + 1] = stickIndex;
				return new GH_Path(idx);
			}
			else
			{
				int[] idx = new int[baseIdx.Length + 1];
				for (int i = 0; i < baseIdx.Length; i++) idx[i] = baseIdx[i];
				idx[baseIdx.Length] = domainIndex;
				return new GH_Path(idx);
			}
		}

		private static Point3d PointAtNormalizedTrimmed(Curve crv, double totalLength, double s01, double shorten)
		{
			double usable = totalLength - 2.0 * shorten;
			double targetLength = shorten + Math.Max(0.0, Math.Min(1.0, s01)) * usable;

			double t;
			if (!crv.LengthParameter(targetLength, out t))
				t = crv.Domain.ParameterAt(targetLength / totalLength);

			return crv.PointAt(t);
		}

		private static int SampleCountForSpacing(double length, double spacing, int minSamples)
		{
			if (length <= RhinoMath.ZeroTolerance)
				return Math.Max(2, minSamples);

			if (spacing <= RhinoMath.ZeroTolerance)
				spacing = 2.0;

			int count = (int)Math.Ceiling(length / spacing) + 1;
			return Math.Max(Math.Max(2, minSamples), count);
		}

		private static void AppendTrimmedCurveSamples(
			List<Point3d> pts,
			Curve crv,
			double totalLength,
			double shorten,
			int sampleCount,
			bool reverse)
		{
			if (pts == null || crv == null || !crv.IsValid)
				return;

			if (sampleCount < 2)
				sampleCount = 2;

			if (totalLength <= 2.0 * shorten)
				return;

			if (!reverse)
			{
				for (int i = 0; i < sampleCount; i++)
				{
					double s01 = (double)i / (double)(sampleCount - 1);
					pts.Add(PointAtNormalizedTrimmed(crv, totalLength, s01, shorten));
				}
			}
			else
			{
				for (int i = sampleCount - 1; i >= 0; i--)
				{
					double s01 = (double)i / (double)(sampleCount - 1);
					pts.Add(PointAtNormalizedTrimmed(crv, totalLength, s01, shorten));
				}
			}
		}

		private static bool TryMakeValidPolyline(List<Point3d> pts, double tol, out Polyline pl)
		{
			pl = new Polyline();
			if (pts == null || pts.Count < 2) return false;

			var dedup = new List<Point3d>(pts.Count);
			for (int i = 0; i < pts.Count; i++)
			{
				if (!pts[i].IsValid)
					continue;

				if (dedup.Count == 0 || pts[i].DistanceTo(dedup[dedup.Count - 1]) > tol)
					dedup.Add(pts[i]);
			}

			if (dedup.Count < 2) return false;

			pl = new Polyline(dedup);
			return pl.IsValid && pl.Count >= 2 && pl.Length > tol;
		}

		private static bool TryMakeValidClosedPolyline(List<Point3d> pts, double tol, out Polyline pl)
		{
			pl = new Polyline();
			if (pts == null || pts.Count < 3) return false;

			var dedup = new List<Point3d>(pts.Count + 1);
			for (int i = 0; i < pts.Count; i++)
			{
				if (!pts[i].IsValid)
					continue;

				if (dedup.Count == 0 || pts[i].DistanceTo(dedup[dedup.Count - 1]) > tol)
					dedup.Add(pts[i]);
			}

			if (dedup.Count < 3)
				return false;

			if (dedup[0].DistanceTo(dedup[dedup.Count - 1]) > tol)
				dedup.Add(dedup[0]);

			pl = new Polyline(dedup);
			pl.CollapseShortSegments(tol);

			return pl.IsValid && pl.IsClosed && pl.Count >= 4 && pl.Length > tol;
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

		private static int Clamp(int value, int min, int max)
		{
			if (value < min) return min;
			if (value > max) return max;
			return value;
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
		/// Shell line i (1…nShell) is centred at (i - 0.5) × wShell inward (Slicer Plus convention).
		/// </summary>
		private static List<PolylineCurve> BuildShellPaths(
			Curve outerGuide,
			Curve innerGuide,
			int nShell,
			double wShell,
			double sampleRes,
			double tol,
			bool insetEnds = false)
		{
			var result = new List<PolylineCurve>(nShell);

			if (outerGuide == null || !outerGuide.IsValid ||
				innerGuide == null || !innerGuide.IsValid ||
				nShell < 1 || wShell <= tol)
				return result;

			double lenOuter = outerGuide.GetLength();
			double lenInner = innerGuide.GetLength();
			if (lenOuter <= tol || lenInner <= tol)
				return result;
			int sampleCount = ShellSampleCount(lenOuter, lenInner, sampleRes, tol);

			for (int si = 1; si <= nShell; si++)
			{
				double targetOffset = (si - 0.5) * wShell;
				var pts = new List<Point3d>(sampleCount);

				for (int k = 0; k < sampleCount; k++)
				{
					double s01 = (double)k / (sampleCount - 1);

					double sOuter = s01 * lenOuter;
					double sInner = s01 * lenInner;
					if (insetEnds)
					{
						double endOuter = Math.Min(targetOffset, Math.Max(0.0, 0.5 * lenOuter - tol));
						double endInner = Math.Min(targetOffset, Math.Max(0.0, 0.5 * lenInner - tol));
						sOuter = endOuter + s01 * Math.Max(0.0, lenOuter - 2.0 * endOuter);
						sInner = endInner + s01 * Math.Max(0.0, lenInner - 2.0 * endInner);
					}

					double tOuter;
					if (!outerGuide.LengthParameter(sOuter, out tOuter))
						tOuter = outerGuide.Domain.ParameterAt(s01);
					Point3d pOuter = outerGuide.PointAt(tOuter);

					double tInner;
					if (!innerGuide.LengthParameter(sInner, out tInner))
						tInner = innerGuide.Domain.ParameterAt(s01);
					Point3d pInner = innerGuide.PointAt(tInner);

					Vector3d vOI = pInner - pOuter;
					double gap = vOI.Length;

					double offset = gap > tol ? Math.Min(targetOffset, gap) : 0.0;

					Point3d p;
					if (gap > tol)
					{
						vOI.Unitize();
						p = pOuter + vOI * offset;
					}
					else
					{
						p = pOuter;
					}

					pts.Add(p);
				}

				// Deduplicate and validate
				var dedup = new List<Point3d>(pts.Count);
				foreach (var p in pts)
				{
					if (!p.IsValid) continue;
					if (dedup.Count == 0 || p.DistanceTo(dedup[dedup.Count - 1]) > tol)
						dedup.Add(p);
				}

				if (dedup.Count < 2) continue;

				var pl = new Polyline(dedup);
				if (!pl.IsValid || pl.Length <= tol) continue;

				var plc = new PolylineCurve(pl);
				if (plc != null && plc.IsValid)
					result.Add(plc);
			}

			return result;
		}

		private static int ShellSampleCount(double lenA, double lenB, double sampleRes, double tol)
		{
			double len = Math.Max(lenA, lenB);
			if (len <= tol) return 2;

			double res = EffectiveRes(sampleRes, lenA, lenB, tol);
			return Math.Max(8, (int)Math.Ceiling(len / res) + 1);
		}

		private static double EffectiveRes(double sampleRes, double lenA, double lenB, double tol)
		{
			double len = Math.Max(lenA, lenB);
			double res = sampleRes;
			if (double.IsNaN(res) || double.IsInfinity(res) || res <= tol)
				res = Math.Max(tol * 10.0, len / 63.0);

			return Math.Max(tol * 10.0, res);
		}
	}
}
