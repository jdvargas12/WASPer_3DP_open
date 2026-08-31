// wsp_Fa03_Facade Panelling.cs
// WASPer_3DP — Subcategory: 2.1_Facades
//
// Region-based facade discretization.
// Accepts a flat trimmed Brep. The outer Brep loop defines the facade boundary.
// Inner Brep loops are automatically detected as openings.
//
// Core logic:
//   1. Extract outer boundary and inner opening loops.
//   2. Build opening-aligned U/V grid coordinates.
//   3. Generate rectangular candidate grid cells.
//   4. Classify each candidate cell by intersecting it with the real facade material region:
//        Material = Cell ∩ OuterBoundary - Openings
//   5. Optionally merge vertically adjacent FULL cells when max_h allows.
//   6. Recompute final panel regions after merging.
//   7. Output final geometry, types, UV cell domains, and keep mask.
//
// Classification:
//   0 = Full material panel
//   1 = Trimmed material panel
//   2 = Void/opening cell
//   3 = Outside facade boundary
//
// UV cell outputs:
//   uv_cell = normalized UV domains for all FINAL grid cells.
//   uv_bool = boolean list aligned with uv_cell, grid, and pa_types.
//             true  = cell contains valid facade material.
//             false = cell is void/opening or outside the facade.
//
// Each uv_cell branch contains four numbers:
//        u0, u1, v0, v1
//
// Vertical merging:
//   If merge_vert is true, only vertically adjacent FULL cells are merged.
//   Merging stops if the combined height would exceed max_h.
//   Trimmed panels are deliberately not merged in this version to avoid accidental
//   complex L-shaped pieces around openings.
//
// Author: Juan Diego Vargas
// Created/revised: 2026-05-08

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino.Geometry;

namespace WASPer_3DP.Components._2_1_Facades
{
    public class wsp_Fa03_FacadePanelling : GH_Component
    {
        private readonly string _versionTag;

        // ─── Constructor ────────────────────────────────────────────────────────────

        public wsp_Fa03_FacadePanelling()
            : base(
                "wsp_Fa03_Facade Panelling",
                "FaPanelling",
                "Discretizes a flat facade into real panel regions using an opening-aligned grid.\n" +
                "The component extracts the outer facade boundary and inner opening loops from\n" +
                "a trimmed Brep, builds a constrained grid, and classifies each cell using planar\n" +
                "region Boolean operations.\n\n" +
                "Unlike a corner-based classifier, this detects openings crossing through the\n" +
                "middle of a panel and outputs actual trimmed panel curves.\n\n" +
                "Optionally merges vertically adjacent full cells when the resulting panel height\n" +
                "does not exceed max_h.\n\n" +
                "Also outputs normalized UV domains for all final grid cells, plus a boolean mask\n" +
                "telling whether each cell should be kept as facade material.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "2.1_Facades")
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        // ─── Identity ────────────────────────────────────────────────────────────────

        public override Guid ComponentGuid =>
            new Guid("3F8C47B2-9A1E-4D65-8B23-C5F17E0D9A4B");

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Fa03_Facade Panelling.png"))
                    {
                        if (s != null) return new Bitmap(s);
                    }
                    return null;
                }
                catch { }
                return null;
            }
        }

        // ─── Params ─────────────────────────────────────────────────────────────────

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBrepParameter(
                "facade",
                "facade",
                "Flat facade surface or trimmed Brep. The outer loop defines the facade boundary.\n" +
                "Inner loops are automatically used as openings.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "Max Panel Width",
                "max_w",
                "Maximum panel width in model units along the local facade U direction.\n" +
                "The grid is subdivided so no generated interval exceeds this value.",
                GH_ParamAccess.item,
                600.0);

            pManager.AddNumberParameter(
                "Max Panel Height",
                "max_h",
                "Maximum final panel height in model units along the local facade V direction.\n" +
                "The initial grid is subdivided so no interval exceeds this value.\n" +
                "If merge_vert is true, vertically adjacent full cells may be merged only when\n" +
                "the merged height still does not exceed this value.",
                GH_ParamAccess.item,
                1200.0);

            pManager.AddBooleanParameter(
                "Align Openings",
                "align",
                "If true, grid lines are inserted at the left/right/top/bottom extents of each opening.",
                GH_ParamAccess.item,
                true);

            pManager.AddBooleanParameter(
                "Merge Vertical Panels",
                "merge_vert",
                "If true, vertically adjacent full material cells in the same column are merged\n" +
                "when their combined height does not exceed max_h.\n" +
                "Trimmed cells are not merged in this version.",
                GH_ParamAccess.item,
                false);

            pManager.AddNumberParameter(
                "Tolerance",
                "tol",
                "Model tolerance used for curve Boolean operations, area comparisons, and coordinate snapping.",
                GH_ParamAccess.item,
                0.001);

            pManager[3].Optional = true;
            pManager[4].Optional = true;
            pManager[5].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            // ── 1. Main panel geometry ─────────────────────────────────────────────

            pManager.AddCurveParameter(
                "All Material Panels",
                "pa_valid",
                "All valid facade material panel regions. Includes full and trimmed panels.\n" +
                "This is the main output to use downstream for facade discretization.",
                GH_ParamAccess.list);

            pManager.AddCurveParameter(
                "Trimmed Panels",
                "pa_trim",
                "Actual trimmed panel regions created by intersecting final grid cells with the facade material region.",
                GH_ParamAccess.list);

            pManager.AddCurveParameter(
                "Void Cells",
                "pa_void",
                "Final grid cells that fall mostly inside openings.",
                GH_ParamAccess.list);

            // ── 2. Panel type/classification outputs ──────────────────────────────

            pManager.AddIntegerParameter(
                "Panel Types",
                "pa_types",
                "Classification per FINAL grid cell, in V-major order:\n" +
                "0 = Full material panel\n" +
                "1 = Trimmed material panel\n" +
                "2 = Void/opening cell\n" +
                "3 = Outside facade boundary",
                GH_ParamAccess.list);

            pManager.RegisterParam(
                new Grasshopper.Kernel.Parameters.Param_Curve(),
                "Panel Clusters",
                "pa_clusters",
                "DataTree grouping repeated full rectangular panels by approximate width and height.\n" +
                "Branch 0 = most common cluster. Trimmed panels are not included in this clustering.",
                GH_ParamAccess.tree);

            // ── 3. Panel metadata ─────────────────────────────────────────────────

            pManager.AddTextParameter(
                "Panel IDs",
                "IDs",
                "Panel identifiers for all valid material panels.",
                GH_ParamAccess.list);

            pManager.AddNumberParameter(
                "Panel Areas",
                "areas",
                "Area of each valid material panel region.",
                GH_ParamAccess.list);

            // ── 4. Reference geometry ─────────────────────────────────────────────

            pManager.AddCurveParameter(
                "Detected Openings",
                "openings",
                "Opening curves extracted from the inner Brep loops.",
                GH_ParamAccess.list);

            // ── 5. Grid/debug geometry ────────────────────────────────────────────

            pManager.AddCurveParameter(
                "Grid Cells",
                "grid",
                "Final rectangular candidate grid cells after optional vertical merging.",
                GH_ParamAccess.list);

            pManager.AddCurveParameter(
                "Grid Lines",
                "grid_lin",
                "Opening-aligned initial grid lines on the facade plane.\n" +
                "Note: if merge_vert is true, some horizontal grid lines may no longer represent final panel joints.",
                GH_ParamAccess.list);

            // ── 6. Summary ────────────────────────────────────────────────────────

            pManager.AddTextParameter(
                "Info",
                "info",
                "Summary of computed grid, panel counts, opening count, merge count, and Boolean status.",
                GH_ParamAccess.item);

            // ── 7. Normalized UV cell data ────────────────────────────────────────

            pManager.AddNumberParameter(
                "UV Cell Domains",
                "uv_cell",
                "DataTree of normalized UV domains for every FINAL grid cell.\n" +
                "Each branch contains four numbers: u0, u1, v0, v1.\n" +
                "This output is aligned with grid, pa_types, and uv_bool.",
                GH_ParamAccess.tree);

            pManager.AddBooleanParameter(
                "UV Keep Mask",
                "uv_bool",
                "Boolean list aligned with uv_cell, grid, and pa_types.\n" +
                "True = this final grid cell contains valid facade material.\n" +
                "False = this final grid cell is void/opening or outside the facade.",
                GH_ParamAccess.list);
        }

        // ─── Solve ───────────────────────────────────────────────────────────────────

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            // ── 1. Inputs ──────────────────────────────────────────────────────────

            Brep facade = null;
            double maxW = 600.0;
            double maxH = 1200.0;
            bool alignOpenings = true;
            bool mergeVert = false;
            double tol = 0.001;

            if (!DA.GetData(0, ref facade) || facade == null) return;
            DA.GetData(1, ref maxW);
            DA.GetData(2, ref maxH);
            DA.GetData(3, ref alignOpenings);
            DA.GetData(4, ref mergeVert);
            DA.GetData(5, ref tol);

            if (maxW <= 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Max Panel Width must be > 0.");
                return;
            }

            if (maxH <= 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Max Panel Height must be > 0.");
                return;
            }

            if (tol <= 0.0)
            {
                tol = Rhino.RhinoDoc.ActiveDoc != null
                    ? Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
                    : 0.001;
            }

            if (facade.Faces.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Facade Brep has no faces.");
                return;
            }

            if (facade.Faces.Count > 1)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"Facade has {facade.Faces.Count} faces. Only face 0 is used.");
            }

            BrepFace face = facade.Faces[0];

            // ── 2. Get facade plane / local frame ─────────────────────────────────

            if (!face.TryGetPlane(out Plane facadePlane, tol))
            {
                if (!face.FrameAt(face.Domain(0).Mid, face.Domain(1).Mid, out facadePlane))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Could not extract a planar frame from the facade face.");
                    return;
                }

                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Face is not perfectly planar within tolerance. Using the mid-frame plane.");
            }

            Interval domU = face.Domain(0);
            Interval domV = face.Domain(1);

            Point3d s00 = face.PointAt(domU.Min, domV.Min);
            Point3d s10 = face.PointAt(domU.Max, domV.Min);
            Point3d s01 = face.PointAt(domU.Min, domV.Max);

            Vector3d uDir = s10 - s00;
            Vector3d vDir = s01 - s00;

            double rawW = uDir.Length;
            double rawH = vDir.Length;

            if (rawW <= tol || rawH <= tol)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Facade local dimensions are near zero.");
                return;
            }

            uDir.Unitize();
            vDir.Unitize();

            Point3d localOrigin = s00;

            // ── 3. Extract outer loop and opening loops ───────────────────────────

            Curve outerCurve = null;
            var openingCurves = new List<Curve>();

            foreach (BrepLoop loop in face.Loops)
            {
                Curve loopCurve = loop.To3dCurve();
                if (loopCurve == null) continue;

                if (!loopCurve.IsClosed)
                {
                    Curve closed = TryCloseCurve(loopCurve, tol);
                    if (closed != null) loopCurve = closed;
                }

                if (!loopCurve.IsClosed) continue;

                if (loop.LoopType == BrepLoopType.Outer)
                {
                    if (outerCurve == null) outerCurve = loopCurve;
                }
                else if (loop.LoopType == BrepLoopType.Inner)
                {
                    openingCurves.Add(loopCurve);
                }
            }

            if (outerCurve == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Could not extract the outer facade boundary loop.");
                return;
            }

            if (openingCurves.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "No inner loops detected. The facade will be discretized without openings.");
            }

            // ── 4. Estimate local facade extents from outer boundary ──────────────

            LocalBounds outerBounds = GetLocalBounds(outerCurve, localOrigin, uDir, vDir, tol);
            double uMin = outerBounds.UMin;
            double uMax = outerBounds.UMax;
            double vMin = outerBounds.VMin;
            double vMax = outerBounds.VMax;

            double facadeW = uMax - uMin;
            double facadeH = vMax - vMin;

            if (facadeW <= tol || facadeH <= tol)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Outer facade boundary has near-zero width or height.");
                return;
            }

            // ── 5. Build constrained U/V grid coordinates ─────────────────────────

            var uCoords = new List<double> { uMin, uMax };
            var vCoords = new List<double> { vMin, vMax };
            var openingBounds = new List<LocalBounds>();

            foreach (Curve opening in openingCurves)
            {
                LocalBounds b = GetLocalBounds(opening, localOrigin, uDir, vDir, tol);

                b.UMin = Clamp(b.UMin, uMin, uMax);
                b.UMax = Clamp(b.UMax, uMin, uMax);
                b.VMin = Clamp(b.VMin, vMin, vMax);
                b.VMax = Clamp(b.VMax, vMin, vMax);

                if (b.UMax - b.UMin > tol && b.VMax - b.VMin > tol)
                {
                    openingBounds.Add(b);

                    if (alignOpenings)
                    {
                        uCoords.Add(b.UMin);
                        uCoords.Add(b.UMax);
                        vCoords.Add(b.VMin);
                        vCoords.Add(b.VMax);
                    }
                }
            }

            uCoords = BuildSubdividedCoordinates(uCoords, maxW, tol);
            vCoords = BuildSubdividedCoordinates(vCoords, maxH, tol);

            int nU = uCoords.Count - 1;
            int nV = vCoords.Count - 1;

            if (nU <= 0 || nV <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Grid generation failed.");
                return;
            }

            // ── 6. Initial classification for merge decisions ─────────────────────
            // This pass only determines which original cells are full/trimmed/void/outside.
            // Final geometry is recomputed after optional merging.

            var initialTypes = new int[nU, nV];

            int initialBoolFailures = 0;

            for (int j = 0; j < nV; j++)
            {
                for (int i = 0; i < nU; i++)
                {
                    CellSpan span = new CellSpan(
                        i,
                        j,
                        i,
                        j,
                        uCoords[i],
                        uCoords[i + 1],
                        vCoords[j],
                        vCoords[j + 1]);

                    CellClassification cls = ClassifyCellSpan(
                        span,
                        localOrigin,
                        uDir,
                        vDir,
                        outerCurve,
                        openingCurves,
                        facadePlane,
                        tol,
                        ref initialBoolFailures);

                    initialTypes[i, j] = cls.Type;
                }
            }

            // ── 7. Build final spans after optional vertical merging ───────────────

            int mergedGroups = 0;
            int mergedCellsConsumed = 0;

            List<CellSpan> finalSpans = mergeVert
                ? BuildVerticallyMergedSpans(uCoords, vCoords, initialTypes, maxH, tol, out mergedGroups, out mergedCellsConsumed)
                : BuildUnmergedSpans(uCoords, vCoords);

            // ── 8. Final classification and outputs ──────────────────────────────

            var fullPanels = new List<Curve>();
            var trimmedPanels = new List<Curve>();
            var voidCells = new List<Curve>();
            var allMaterialPanels = new List<Curve>();
            var gridCells = new List<Curve>();
            var panelTypes = new List<int>();
            var uvKeepMask = new List<bool>();

            var panelIDs = new List<string>();
            var panelAreas = new List<double>();
            var fullDims = new List<PanelDim>();

            var uvCellDomains = new GH_Structure<GH_Number>();

            int boolFailures = 0;
            int outsideCells = 0;
            int panelCounter = 0;
            int cellCounter = 0;

            foreach (CellSpan span in finalSpans)
            {
                CellClassification cls = ClassifyCellSpan(
                    span,
                    localOrigin,
                    uDir,
                    vDir,
                    outerCurve,
                    openingCurves,
                    facadePlane,
                    tol,
                    ref boolFailures);

                Curve cell = MakeLocalRectangle(
                    span.UMin,
                    span.UMax,
                    span.VMin,
                    span.VMax,
                    localOrigin,
                    uDir,
                    vDir);

                gridCells.Add(cell);
                panelTypes.Add(cls.Type);

                NormalizedDomain cellUvDomain = GetNormalizedUvDomainFromLocalValues(
                    span.UMin,
                    span.UMax,
                    span.VMin,
                    span.VMax,
                    uMin,
                    vMin,
                    facadeW,
                    facadeH);

                AppendNormalizedDomain(uvCellDomains, new GH_Path(cellCounter), cellUvDomain);

                if (cls.Type == 0 || cls.Type == 1)
                {
                    uvKeepMask.Add(true);

                    foreach (Curve piece in cls.MaterialPieces)
                    {
                        string id = $"P_{panelCounter:0000}_i{span.I0}_j{span.J0}";
                        if (span.J1 > span.J0) id += $"_to_j{span.J1}";
                        panelCounter++;

                        allMaterialPanels.Add(piece);
                        panelIDs.Add(id);
                        panelAreas.Add(CurveArea(piece));
                    }

                    if (cls.Type == 0 && cls.MaterialPieces.Count == 1)
                    {
                        fullPanels.Add(cls.MaterialPieces[0]);
                        fullDims.Add(new PanelDim(
                            Math.Abs(span.UMax - span.UMin),
                            Math.Abs(span.VMax - span.VMin)));
                    }
                    else
                    {
                        foreach (Curve piece in cls.MaterialPieces)
                        {
                            trimmedPanels.Add(piece);
                        }
                    }
                }
                else
                {
                    uvKeepMask.Add(false);

                    if (cls.Type == 2)
                    {
                        voidCells.Add(cell);
                    }
                    else
                    {
                        outsideCells++;
                    }
                }

                cellCounter++;
            }

            // ── 9. Full-panel clustering ──────────────────────────────────────────

            GH_Structure<GH_Curve> panelClusters = BuildFullPanelClusterTree(fullPanels, fullDims, tol);

            // ── 10. Initial grid lines ────────────────────────────────────────────

            var gridLines = new List<Curve>();

            foreach (double v in vCoords)
            {
                gridLines.Add(MakeLocalLine(uMin, v, uMax, v, localOrigin, uDir, vDir));
            }

            foreach (double u in uCoords)
            {
                gridLines.Add(MakeLocalLine(u, vMin, u, vMax, localOrigin, uDir, vDir));
            }

            // ── 11. Outputs ───────────────────────────────────────────────────────

            DA.SetDataList(0, allMaterialPanels);
            DA.SetDataList(1, trimmedPanels);
            DA.SetDataList(2, voidCells);

            DA.SetDataList(3, panelTypes);
            DA.SetDataTree(4, panelClusters);

            DA.SetDataList(5, panelIDs);
            DA.SetDataList(6, panelAreas);

            DA.SetDataList(7, openingCurves);

            DA.SetDataList(8, gridCells);
            DA.SetDataList(9, gridLines);

            string info =
                $"Initial grid: {nU} × {nV} = {nU * nV} cells  |  Final cells: {gridCells.Count}  |  " +
                $"Facade bbox: {facadeW:F2} × {facadeH:F2}  |  Max panel: {maxW:F2} × {maxH:F2}  |  " +
                $"Openings: {openingCurves.Count}  |  Align openings: {alignOpenings}  |  Merge vertical: {mergeVert}  |  " +
                $"Merged groups: {mergedGroups}  Merged consumed cells: {mergedCellsConsumed}  |  " +
                $"Full: {fullPanels.Count}  Trimmed pieces: {trimmedPanels.Count}  Void cells: {voidCells.Count}  " +
                $"Outside cells: {outsideCells}  All material panels: {allMaterialPanels.Count}  |  " +
                $"Panel clusters: {panelClusters.PathCount}  |  UV cell domains: {uvCellDomains.PathCount}  " +
                $"UV keep true: {uvKeepMask.Count(x => x)} / {uvKeepMask.Count}  |  " +
                $"Initial Boolean fallback events: {initialBoolFailures}  Final Boolean fallback events: {boolFailures}";

            DA.SetData(10, info);

            DA.SetDataTree(11, uvCellDomains);
            DA.SetDataList(12, uvKeepMask);
        }

        // ─── Data structures ───────────────────────────────────────────────────────

        private struct LocalBounds
        {
            public double UMin;
            public double UMax;
            public double VMin;
            public double VMax;
        }

        private struct PanelDim
        {
            public double W;
            public double H;

            public PanelDim(double w, double h)
            {
                W = w;
                H = h;
            }
        }

        private struct NormalizedDomain
        {
            public double U0;
            public double U1;
            public double V0;
            public double V1;

            public NormalizedDomain(double u0, double u1, double v0, double v1)
            {
                U0 = u0;
                U1 = u1;
                V0 = v0;
                V1 = v1;
            }
        }

        private struct CellSpan
        {
            public int I0;
            public int J0;
            public int I1;
            public int J1;

            public double UMin;
            public double UMax;
            public double VMin;
            public double VMax;

            public CellSpan(
                int i0,
                int j0,
                int i1,
                int j1,
                double uMin,
                double uMax,
                double vMin,
                double vMax)
            {
                I0 = i0;
                J0 = j0;
                I1 = i1;
                J1 = j1;
                UMin = uMin;
                UMax = uMax;
                VMin = vMin;
                VMax = vMax;
            }

            public double Width
            {
                get { return UMax - UMin; }
            }

            public double Height
            {
                get { return VMax - VMin; }
            }
        }

        private class CellClassification
        {
            public int Type;
            public List<Curve> MaterialPieces;

            public CellClassification(int type, List<Curve> materialPieces)
            {
                Type = type;
                MaterialPieces = materialPieces ?? new List<Curve>();
            }
        }

        // ─── Cell classification ───────────────────────────────────────────────────

        private static CellClassification ClassifyCellSpan(
            CellSpan span,
            Point3d localOrigin,
            Vector3d uDir,
            Vector3d vDir,
            Curve outerCurve,
            List<Curve> openingCurves,
            Plane facadePlane,
            double tol,
            ref int boolFailures)
        {
            Curve cell = MakeLocalRectangle(
                span.UMin,
                span.UMax,
                span.VMin,
                span.VMax,
                localOrigin,
                uDir,
                vDir);

            double areaTol = Math.Max(tol * tol, 1e-9);
            double cellArea = CurveArea(cell);

            if (cellArea <= areaTol)
            {
                return new CellClassification(3, null);
            }

            List<Curve> clippedToOuter = BooleanIntersectionSafe(cell, outerCurve, facadePlane, tol);

            if (clippedToOuter.Count == 0)
            {
                return new CellClassification(3, null);
            }

            var materialPieces = new List<Curve>();

            foreach (Curve outerPiece in clippedToOuter)
            {
                List<Curve> pieces = BooleanDifferenceSequentialSafe(
                    outerPiece,
                    openingCurves,
                    facadePlane,
                    tol,
                    ref boolFailures);

                foreach (Curve p in pieces)
                {
                    if (p != null && p.IsClosed && CurveArea(p) > areaTol)
                    {
                        materialPieces.Add(p);
                    }
                }
            }

            double materialArea = materialPieces.Sum(CurveArea);
            double openingOverlapArea = EstimateOpeningOverlapArea(cell, openingCurves, facadePlane, tol);

            if (materialArea <= areaTol)
            {
                if (openingOverlapArea > areaTol)
                {
                    return new CellClassification(2, null);
                }

                return new CellClassification(3, null);
            }

            double materialRatio = materialArea / cellArea;
            bool isFull = materialRatio >= 1.0 - 1e-4;

            if (isFull && materialPieces.Count == 1)
            {
                return new CellClassification(0, materialPieces);
            }

            return new CellClassification(1, materialPieces);
        }

        // ─── Merge logic ───────────────────────────────────────────────────────────

        private static List<CellSpan> BuildUnmergedSpans(
            List<double> uCoords,
            List<double> vCoords)
        {
            var spans = new List<CellSpan>();

            int nU = uCoords.Count - 1;
            int nV = vCoords.Count - 1;

            for (int j = 0; j < nV; j++)
            {
                for (int i = 0; i < nU; i++)
                {
                    spans.Add(new CellSpan(
                        i,
                        j,
                        i,
                        j,
                        uCoords[i],
                        uCoords[i + 1],
                        vCoords[j],
                        vCoords[j + 1]));
                }
            }

            return spans;
        }

        private static List<CellSpan> BuildVerticallyMergedSpans(
            List<double> uCoords,
            List<double> vCoords,
            int[,] initialTypes,
            double maxH,
            double tol,
            out int mergedGroups,
            out int mergedCellsConsumed)
        {
            var spans = new List<CellSpan>();

            mergedGroups = 0;
            mergedCellsConsumed = 0;

            int nU = uCoords.Count - 1;
            int nV = vCoords.Count - 1;

            for (int i = 0; i < nU; i++)
            {
                int j = 0;

                while (j < nV)
                {
                    int currentType = initialTypes[i, j];

                    if (currentType != 0)
                    {
                        spans.Add(new CellSpan(
                            i,
                            j,
                            i,
                            j,
                            uCoords[i],
                            uCoords[i + 1],
                            vCoords[j],
                            vCoords[j + 1]));

                        j++;
                        continue;
                    }

                    int startJ = j;
                    int endJ = j;

                    double startV = vCoords[startJ];
                    double candidateEndV = vCoords[endJ + 1];

                    while (endJ + 1 < nV)
                    {
                        int nextJ = endJ + 1;

                        if (initialTypes[i, nextJ] != 0)
                        {
                            break;
                        }

                        double nextEndV = vCoords[nextJ + 1];
                        double mergedHeight = nextEndV - startV;

                        if (mergedHeight > maxH + tol)
                        {
                            break;
                        }

                        endJ = nextJ;
                        candidateEndV = nextEndV;
                    }

                    if (endJ > startJ)
                    {
                        mergedGroups++;
                        mergedCellsConsumed += (endJ - startJ + 1);
                    }

                    spans.Add(new CellSpan(
                        i,
                        startJ,
                        i,
                        endJ,
                        uCoords[i],
                        uCoords[i + 1],
                        startV,
                        candidateEndV));

                    j = endJ + 1;
                }
            }

            // Restore V-major order after column-based merging.
            spans = spans
                .OrderBy(s => s.J0)
                .ThenBy(s => s.I0)
                .ToList();

            return spans;
        }

        // ─── Local geometry helpers ────────────────────────────────────────────────

        private static LocalBounds GetLocalBounds(
            Curve crv,
            Point3d origin,
            Vector3d uDir,
            Vector3d vDir,
            double tol)
        {
            var bounds = new LocalBounds
            {
                UMin = double.MaxValue,
                UMax = double.MinValue,
                VMin = double.MaxValue,
                VMax = double.MinValue
            };

            var pts = new List<Point3d>();

            Polyline pl;
            if (crv.TryGetPolyline(out pl))
            {
                pts.AddRange(pl);
            }
            else
            {
                int count = 96;
                double[] ts = crv.DivideByCount(count, true);

                if (ts != null)
                {
                    foreach (double t in ts)
                    {
                        pts.Add(crv.PointAt(t));
                    }
                }

                pts.Add(crv.PointAtStart);
                pts.Add(crv.PointAtEnd);
            }

            foreach (Point3d p in pts)
            {
                Vector3d rel = p - origin;
                double u = rel * uDir;
                double v = rel * vDir;

                if (u < bounds.UMin) bounds.UMin = u;
                if (u > bounds.UMax) bounds.UMax = u;
                if (v < bounds.VMin) bounds.VMin = v;
                if (v > bounds.VMax) bounds.VMax = v;
            }

            if (bounds.UMin == double.MaxValue)
            {
                bounds.UMin = 0.0;
                bounds.UMax = 0.0;
                bounds.VMin = 0.0;
                bounds.VMax = 0.0;
            }

            return bounds;
        }

        private static Point3d LocalPoint(
            double u,
            double v,
            Point3d origin,
            Vector3d uDir,
            Vector3d vDir)
        {
            return origin + uDir * u + vDir * v;
        }

        private static Curve MakeLocalRectangle(
            double u0,
            double u1,
            double v0,
            double v1,
            Point3d origin,
            Vector3d uDir,
            Vector3d vDir)
        {
            Point3d p00 = LocalPoint(u0, v0, origin, uDir, vDir);
            Point3d p10 = LocalPoint(u1, v0, origin, uDir, vDir);
            Point3d p11 = LocalPoint(u1, v1, origin, uDir, vDir);
            Point3d p01 = LocalPoint(u0, v1, origin, uDir, vDir);

            return new Polyline(new[] { p00, p10, p11, p01, p00 }).ToNurbsCurve();
        }

        private static Curve MakeLocalLine(
            double u0,
            double v0,
            double u1,
            double v1,
            Point3d origin,
            Vector3d uDir,
            Vector3d vDir)
        {
            Point3d a = LocalPoint(u0, v0, origin, uDir, vDir);
            Point3d b = LocalPoint(u1, v1, origin, uDir, vDir);

            return new LineCurve(a, b);
        }

        private static NormalizedDomain GetNormalizedUvDomainFromLocalValues(
            double u0Local,
            double u1Local,
            double v0Local,
            double v1Local,
            double uMin,
            double vMin,
            double facadeW,
            double facadeH)
        {
            double u0 = (u0Local - uMin) / facadeW;
            double u1 = (u1Local - uMin) / facadeW;
            double v0 = (v0Local - vMin) / facadeH;
            double v1 = (v1Local - vMin) / facadeH;

            u0 = Clamp(u0, 0.0, 1.0);
            u1 = Clamp(u1, 0.0, 1.0);
            v0 = Clamp(v0, 0.0, 1.0);
            v1 = Clamp(v1, 0.0, 1.0);

            return new NormalizedDomain(u0, u1, v0, v1);
        }

        private static void AppendNormalizedDomain(
            GH_Structure<GH_Number> tree,
            GH_Path path,
            NormalizedDomain domain)
        {
            tree.Append(new GH_Number(domain.U0), path);
            tree.Append(new GH_Number(domain.U1), path);
            tree.Append(new GH_Number(domain.V0), path);
            tree.Append(new GH_Number(domain.V1), path);
        }

        // ─── Grid helpers ──────────────────────────────────────────────────────────

        private static List<double> BuildSubdividedCoordinates(
            List<double> mandatory,
            double maxStep,
            double tol)
        {
            var sorted = mandatory
                .Where(x => !double.IsNaN(x) && !double.IsInfinity(x))
                .OrderBy(x => x)
                .ToList();

            var clean = new List<double>();

            foreach (double x in sorted)
            {
                if (clean.Count == 0 || Math.Abs(x - clean[clean.Count - 1]) > tol)
                {
                    clean.Add(x);
                }
            }

            if (clean.Count < 2) return clean;

            var result = new List<double>();
            result.Add(clean[0]);

            for (int i = 0; i < clean.Count - 1; i++)
            {
                double a = clean[i];
                double b = clean[i + 1];
                double len = b - a;

                if (len <= tol) continue;

                int divs = Math.Max(1, (int)Math.Ceiling(len / maxStep));
                double step = len / divs;

                for (int k = 1; k <= divs; k++)
                {
                    double x = a + step * k;

                    if (result.Count == 0 || Math.Abs(x - result[result.Count - 1]) > tol)
                    {
                        result.Add(x);
                    }
                }
            }

            return result;
        }

        // ─── Boolean helpers ───────────────────────────────────────────────────────

        private static List<Curve> BooleanIntersectionSafe(
            Curve a,
            Curve b,
            Plane plane,
            double tol)
        {
            var result = new List<Curve>();

            try
            {
                Curve[] intersections = Curve.CreateBooleanIntersection(a, b, tol);

                if (intersections != null && intersections.Length > 0)
                {
                    foreach (Curve c in intersections)
                    {
                        if (c != null && c.IsClosed && CurveArea(c) > tol * tol)
                        {
                            result.Add(c);
                        }
                    }

                    return result;
                }
            }
            catch
            {
                // Fall back below.
            }

            Point3d test = AreaCentroid(a);
            PointContainment containment = b.Contains(test, plane, tol);

            if (containment == PointContainment.Inside || containment == PointContainment.Coincident)
            {
                result.Add(a.DuplicateCurve());
            }

            return result;
        }

        private static List<Curve> BooleanDifferenceSequentialSafe(
            Curve source,
            List<Curve> cutters,
            Plane plane,
            double tol,
            ref int fallbackEvents)
        {
            var current = new List<Curve> { source.DuplicateCurve() };

            if (cutters == null || cutters.Count == 0)
            {
                return current;
            }

            foreach (Curve cutter in cutters)
            {
                if (cutter == null || !cutter.IsClosed) continue;

                var next = new List<Curve>();

                foreach (Curve region in current)
                {
                    if (region == null || !region.IsClosed) continue;

                    bool handled = false;

                    try
                    {
                        Curve[] diff = Curve.CreateBooleanDifference(region, cutter, tol);

                        if (diff != null)
                        {
                            foreach (Curve d in diff)
                            {
                                if (d != null && d.IsClosed && CurveArea(d) > tol * tol)
                                {
                                    next.Add(d);
                                }
                            }

                            handled = true;
                        }
                    }
                    catch
                    {
                        handled = false;
                    }

                    if (!handled)
                    {
                        fallbackEvents++;

                        Point3d centroid = AreaCentroid(region);
                        PointContainment c = cutter.Contains(centroid, plane, tol);

                        if (c == PointContainment.Inside || c == PointContainment.Coincident)
                        {
                            continue;
                        }

                        next.Add(region);
                    }
                }

                current = next;

                if (current.Count == 0) break;
            }

            return current;
        }

        private static double EstimateOpeningOverlapArea(
            Curve cell,
            List<Curve> openings,
            Plane plane,
            double tol)
        {
            double area = 0.0;

            foreach (Curve opening in openings)
            {
                if (opening == null || !opening.IsClosed) continue;

                List<Curve> pieces = BooleanIntersectionSafe(cell, opening, plane, tol);

                foreach (Curve p in pieces)
                {
                    area += CurveArea(p);
                }
            }

            return area;
        }

        // ─── Area / clustering helpers ─────────────────────────────────────────────

        private static double CurveArea(Curve crv)
        {
            if (crv == null || !crv.IsClosed) return 0.0;

            AreaMassProperties amp = AreaMassProperties.Compute(crv);
            if (amp == null) return 0.0;

            return Math.Abs(amp.Area);
        }

        private static Point3d AreaCentroid(Curve crv)
        {
            AreaMassProperties amp = AreaMassProperties.Compute(crv);

            if (amp != null)
            {
                return amp.Centroid;
            }

            return crv.PointAtNormalizedLength(0.5);
        }

        private static GH_Structure<GH_Curve> BuildFullPanelClusterTree(
            List<Curve> fullPanels,
            List<PanelDim> dims,
            double tol)
        {
            double dimTol = Math.Max(tol, 1.0);
            var keys = new List<PanelDim>();
            var groups = new List<List<int>>();

            for (int i = 0; i < fullPanels.Count; i++)
            {
                PanelDim d = dims[i];
                int match = -1;

                for (int k = 0; k < keys.Count; k++)
                {
                    if (Math.Abs(d.W - keys[k].W) <= dimTol &&
                        Math.Abs(d.H - keys[k].H) <= dimTol)
                    {
                        match = k;
                        break;
                    }
                }

                if (match < 0)
                {
                    keys.Add(d);
                    groups.Add(new List<int> { i });
                }
                else
                {
                    groups[match].Add(i);
                }
            }

            var order = Enumerable.Range(0, groups.Count)
                .OrderByDescending(i => groups[i].Count)
                .ToList();

            var tree = new GH_Structure<GH_Curve>();

            for (int newIndex = 0; newIndex < order.Count; newIndex++)
            {
                int oldIndex = order[newIndex];
                GH_Path path = new GH_Path(newIndex);

                foreach (int panelIndex in groups[oldIndex])
                {
                    tree.Append(new GH_Curve(fullPanels[panelIndex]), path);
                }
            }

            return tree;
        }

        // ─── Utility helpers ───────────────────────────────────────────────────────

        private static Curve TryCloseCurve(Curve crv, double tol)
        {
            if (crv == null) return null;
            if (crv.IsClosed) return crv;

            Point3d a = crv.PointAtStart;
            Point3d b = crv.PointAtEnd;

            if (a.DistanceTo(b) > tol) return null;

            PolylineCurve plc = crv as PolylineCurve;
            if (plc != null)
            {
                Polyline pl;
                if (plc.TryGetPolyline(out pl))
                {
                    pl.Add(pl[0]);
                    return pl.ToNurbsCurve();
                }
            }

            Curve dup = crv.DuplicateCurve();
            if (dup.MakeClosed(tol)) return dup;

            return null;
        }

        private static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }
    }
}