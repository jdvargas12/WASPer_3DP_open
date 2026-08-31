// wsp_Fa06_Random Weighted Tile Panelizer.cs
// WASPer_3DP - Subcategory: 2.1_Facades
//
// Compiled version of the Random Weighted Tile Panelizer Grasshopper script.
// The component:
//   1. Splits a base surface into a target number of large panels.
//   2. Assigns one weighted tile type to each large panel.
//   3. Subdivides each large panel by the assigned tile module.
//   4. Orients copied tile geometries into each generated panel tile cell.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP.Components._2_1_Facades
{
    public class wsp_Fa06_RandomWeightedTilePanelizer : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Fa06_RandomWeightedTilePanelizer()
            : base(
                "wsp_Fa06_Random Weighted Tile Panelizer",
                "TilePanelizer",
                BuildDescription(),
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "2.1_Facades")
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("6D62F4C1-7B56-4A4D-9C32-E2DF6D77A5F1");

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Fa06_Random Weighted Tile Panelizer.png"))
                    {
                        if (s != null) return new Bitmap(s);
                    }
                }
                catch { }

                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "base_surf",
                "base_surf",
                "Base surface or single-face Brep to panelize.\n" +
                "Best results are obtained with simple rectangular, untrimmed surfaces.\n" +
                "If a trimmed Brep is provided, the underlying face surface is used and trims may be ignored.",
                GH_ParamAccess.item);

            pManager.AddGenericParameter(
                "base_tiles",
                "base_tiles",
                "Data tree of base tile geometries.\n" +
                "Each branch is one tile type: {0}=type 0, {1}=type 1, etc.\n" +
                "Each branch can contain one or multiple geometries, allowing compound tile modules.",
                GH_ParamAccess.tree);

            pManager.AddNumberParameter(
                "tile_weights",
                "weights",
                "Optional weights controlling the probability of each tile branch being assigned to a large panel.\n" +
                "Provide one value per base_tiles branch. Missing values default to 1.0.\n" +
                "Negative, NaN, and infinite values are treated as 0.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter(
                "panel_count",
                "count",
                "Target number of large panels.\n" +
                "The component repeatedly splits the currently largest panel until this count is reached, or until no valid split is possible.",
                GH_ParamAccess.item,
                8);

            pManager.AddIntegerParameter(
                "seed",
                "seed",
                "Random seed. Same seed plus same inputs gives the same panel split and tile assignment.",
                GH_ParamAccess.item,
                1);

            pManager.AddIntegerParameter(
                "fit_mode",
                "fit",
                "Tile fitting mode. Provide one value for all tile branches, or one value per base_tiles branch.\n" +
                "0 = Fit to panel: tiles are non-uniformly scaled to fully cover the panel subdivision.\n" +
                "1 = Preserve tile size: original World XY tile dimensions are preserved as much as possible, with centered leftovers.\n" +
                "2 = Preserve aspect ratio: tiles are uniformly scaled inside each cell, preserving proportions.",
                GH_ParamAccess.list);

            pManager[2].Optional = true;
            pManager[5].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter(
                "panels",
                "panels",
                "Flat list of generated large panels as Breps.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter(
                "panel_t_index",
                "panel_t_index",
                "Flat list of selected tile branch indices.\n" +
                "One integer per generated large panel.",
                GH_ParamAccess.list);

            pManager.AddBrepParameter(
                "panels_tiles",
                "panels_tiles",
                "Panel-tile subdivision surfaces as a DataTree.\n" +
                "Path structure: {panel_index}.",
                GH_ParamAccess.tree);

            pManager.AddGeometryParameter(
                "tiles_oriented",
                "tiles_oriented",
                "Copied tile geometries oriented into the generated panel-tile cells.\n" +
                "Path structure: {panel_index; tile_type_index; tile_cell_index}.",
                GH_ParamAccess.tree);

            pManager.AddTextParameter(
                "summary",
                "summary",
                "Text report with generation information, tile assignments, grid sizes, fit modes, and warnings.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            var outPanels = new List<Brep>();
            var outPanelTileIndex = new List<int>();
            var outPanelTiles = new DataTree<Brep>();
            var outTilesOriented = new DataTree<GeometryBase>();
            var report = new StringBuilder();

            object baseSurfInput = null;
            GH_Structure<IGH_Goo> baseTilesTree;
            var tileWeights = new List<double>();
            int panelCount = 8;
            int seed = 1;
            var fitMode = new List<int>();

            if (!DA.GetData(0, ref baseSurfInput))
            {
                SetEmptyOutputs(DA, outPanels, outPanelTileIndex, outPanelTiles, outTilesOriented,
                    "ERROR: base_surf is required. Use a Surface or single-face Brep.");
                return;
            }

            if (!DA.GetDataTree(1, out baseTilesTree) || baseTilesTree == null || baseTilesTree.PathCount == 0)
            {
                SetEmptyOutputs(DA, outPanels, outPanelTileIndex, outPanelTiles, outTilesOriented,
                    "ERROR: base_tiles is empty. Provide a data tree where each branch is one tile type.");
                return;
            }

            DA.GetDataList(2, tileWeights);
            DA.GetData(3, ref panelCount);
            DA.GetData(4, ref seed);
            DA.GetDataList(5, fitMode);

            Surface baseSurface = GetSurfaceFromObject(baseSurfInput);
            if (baseSurface == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "base_surf could not be converted to a Rhino Surface. Use a Surface or single-face Brep.");
                SetEmptyOutputs(DA, outPanels, outPanelTileIndex, outPanelTiles, outTilesOriented,
                    "ERROR: base_surf could not be converted to a Rhino Surface. Use a Surface or single-face Brep.");
                return;
            }

            int targetPanelCount = Math.Max(1, panelCount);
            var rnd = new Random(seed);
            var warnings = new List<string>();

            var tileBranches = new List<List<GeometryBase>>();
            var tileBBoxes = new List<BoundingBox>();
            var tileWidths = new List<double>();
            var tileHeights = new List<double>();

            for (int i = 0; i < baseTilesTree.PathCount; i++)
            {
                var branchGeos = new List<GeometryBase>();
                IList<IGH_Goo> branch = baseTilesTree.Branches[i];

                if (branch != null)
                {
                    for (int j = 0; j < branch.Count; j++)
                    {
                        GeometryBase g = GetGeometryFromObject(branch[j]);
                        if (g != null) branchGeos.Add(g);
                    }
                }

                BoundingBox bbox = GetCombinedBoundingBox(branchGeos);

                double w = 1.0;
                double h = 1.0;

                if (bbox.IsValid)
                {
                    w = bbox.Max.X - bbox.Min.X;
                    h = bbox.Max.Y - bbox.Min.Y;
                }
                else
                {
                    warnings.Add("Tile branch " + i + " has no valid geometry or bounding box. Width and height were set to 1.0.");
                }

                if (w <= RhinoMath.ZeroTolerance)
                {
                    w = 1.0;
                    warnings.Add("Tile branch " + i + " has near-zero width. Width was set to 1.0.");
                }

                if (h <= RhinoMath.ZeroTolerance)
                {
                    h = 1.0;
                    warnings.Add("Tile branch " + i + " has near-zero height. Height was set to 1.0.");
                }

                tileBranches.Add(branchGeos);
                tileBBoxes.Add(bbox);
                tileWidths.Add(w);
                tileHeights.Add(h);
            }

            int tileTypeCount = tileBranches.Count;
            if (tileTypeCount == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "base_tiles has no usable branches.");
                SetEmptyOutputs(DA, outPanels, outPanelTileIndex, outPanelTiles, outTilesOriented,
                    "ERROR: base_tiles has no usable branches.");
                return;
            }

            List<double> weights = PrepareWeights(tileWeights, tileTypeCount);
            double totalWeight = weights.Sum();

            if (totalWeight <= RhinoMath.ZeroTolerance)
            {
                for (int i = 0; i < weights.Count; i++) weights[i] = 1.0;
                totalWeight = weights.Sum();
                warnings.Add("All tile weights were zero or invalid. All tile branches were treated equally.");
            }

            List<int> tileFitModes = PrepareFitModes(fitMode, tileTypeCount);
            List<Surface> panelSurfaces = GeneratePanels(baseSurface, targetPanelCount, rnd, warnings);

            for (int i = 0; i < panelSurfaces.Count; i++)
            {
                Brep b = panelSurfaces[i]?.ToBrep();
                if (b != null) outPanels.Add(b);
            }

            var panelGridInfo = new List<string>();

            for (int p = 0; p < panelSurfaces.Count; p++)
            {
                Surface panelSrf = panelSurfaces[p];
                if (panelSrf == null) continue;

                int tileTypeIndex = SelectWeightedIndex(weights, totalWeight, rnd);
                int currentFitMode = tileFitModes[tileTypeIndex];

                outPanelTileIndex.Add(tileTypeIndex);

                List<GeometryBase> selectedTileGeos = tileBranches[tileTypeIndex];
                BoundingBox selectedTileBBox = tileBBoxes[tileTypeIndex];

                double tileW = tileWidths[tileTypeIndex];
                double tileH = tileHeights[tileTypeIndex];

                PanelSize ps = GetPanelSize(panelSrf);
                PanelTilingLayout layout = GetPanelTilingLayout(ps.ULength, ps.VLength, tileW, tileH, currentFitMode);

                panelGridInfo.Add(
                    "Panel " + p +
                    " -> tile branch " + tileTypeIndex +
                    ", fit_mode " + currentFitMode +
                    ", grid " + layout.CountU + " x " + layout.CountV +
                    ", used tile size approx. " + Math.Round(layout.UsedTileW, 3) + " x " + Math.Round(layout.UsedTileH, 3)
                );

                Interval uDom = panelSrf.Domain(0);
                Interval vDom = panelSrf.Domain(1);

                int cellIndex = 0;

                for (int u = 0; u < layout.CountU; u++)
                {
                    for (int v = 0; v < layout.CountV; v++)
                    {
                        double u0Ratio = layout.OffsetU + ((double)u * layout.StepU);
                        double v0Ratio = layout.OffsetV + ((double)v * layout.StepV);
                        double u1Ratio = u0Ratio + layout.TileRatioU;
                        double v1Ratio = v0Ratio + layout.TileRatioV;

                        if (u0Ratio < -RhinoMath.ZeroTolerance || v0Ratio < -RhinoMath.ZeroTolerance)
                        {
                            continue;
                        }

                        if (u1Ratio > 1.0 + RhinoMath.ZeroTolerance || v1Ratio > 1.0 + RhinoMath.ZeroTolerance)
                        {
                            continue;
                        }

                        u0Ratio = Clamp01(u0Ratio);
                        v0Ratio = Clamp01(v0Ratio);
                        u1Ratio = Clamp01(u1Ratio);
                        v1Ratio = Clamp01(v1Ratio);

                        double u0 = uDom.T0 + ((uDom.T1 - uDom.T0) * u0Ratio);
                        double u1 = uDom.T0 + ((uDom.T1 - uDom.T0) * u1Ratio);
                        double v0 = vDom.T0 + ((vDom.T1 - vDom.T0) * v0Ratio);
                        double v1 = vDom.T0 + ((vDom.T1 - vDom.T0) * v1Ratio);

                        Interval cellUDom = new Interval(u0, u1);
                        Interval cellVDom = new Interval(v0, v1);

                        Surface cellSurface = panelSrf.Trim(cellUDom, cellVDom);

                        if (cellSurface != null)
                        {
                            Brep cellBrep = cellSurface.ToBrep();
                            if (cellBrep != null) outPanelTiles.Add(cellBrep, new GH_Path(p));
                        }

                        Plane targetPlane;
                        double targetW;
                        double targetH;

                        bool gotTarget = GetCellTargetPlane(panelSrf, u0, u1, v0, v1, out targetPlane, out targetW, out targetH);

                        if (gotTarget && selectedTileGeos.Count > 0 && selectedTileBBox.IsValid)
                        {
                            Plane sourcePlane = new Plane(
                                new Point3d(selectedTileBBox.Min.X, selectedTileBBox.Min.Y, 0.0),
                                Vector3d.XAxis,
                                Vector3d.YAxis
                            );

                            double sx = targetW / tileW;
                            double sy = targetH / tileH;

                            if (currentFitMode == 1)
                            {
                                sx = 1.0;
                                sy = 1.0;
                            }
                            else if (currentFitMode == 2)
                            {
                                double sUniform = Math.Min(sx, sy);
                                sx = sUniform;
                                sy = sUniform;
                            }

                            Transform orient = Transform.PlaneToPlane(sourcePlane, targetPlane);
                            Transform scale = Transform.Scale(targetPlane, sx, sy, 1.0);
                            Transform finalXform = scale * orient;

                            GH_Path tilePath = new GH_Path(p, tileTypeIndex, cellIndex);

                            for (int g = 0; g < selectedTileGeos.Count; g++)
                            {
                                GeometryBase geoCopy = selectedTileGeos[g].Duplicate();

                                if (geoCopy != null)
                                {
                                    geoCopy.Transform(finalXform);
                                    outTilesOriented.Add(geoCopy, tilePath);
                                }
                            }
                        }

                        cellIndex++;
                    }
                }
            }

            BuildReport(report, targetPanelCount, panelSurfaces.Count, outPanelTileIndex,
                tileTypeCount, seed, tileBranches, tileWidths, tileHeights, weights, tileFitModes,
                panelGridInfo, warnings);

            if (panelSurfaces.Count < targetPanelCount)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "Could not reach target panel_count. Some surface splits may have failed.");
            }

            foreach (string warning in warnings)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, warning);
            }

            DA.SetDataList(0, outPanels);
            DA.SetDataList(1, outPanelTileIndex);
            DA.SetDataTree(2, outPanelTiles);
            DA.SetDataTree(3, outTilesOriented);
            DA.SetData(4, report.ToString());

            Message = $"{_versionTag} | {outPanels.Count} panels";
        }

        private static void SetEmptyOutputs(
            IGH_DataAccess da,
            List<Brep> panels,
            List<int> panelTileIndex,
            DataTree<Brep> panelTiles,
            DataTree<GeometryBase> tilesOriented,
            string summary)
        {
            da.SetDataList(0, panels);
            da.SetDataList(1, panelTileIndex);
            da.SetDataTree(2, panelTiles);
            da.SetDataTree(3, tilesOriented);
            da.SetData(4, summary);
        }

        private static string BuildDescription()
        {
            return
                "Creates a randomized weighted tile-panel layout from a base surface.\n\n" +
                "The component first subdivides base_surf into a target number of large panels, " +
                "then assigns one base_tiles branch to each panel using tile_weights. " +
                "Each assigned panel is subdivided by the selected tile module size and copied tile geometries are oriented into each generated cell.\n\n" +
                "Tile branches are measured from their combined World XY bounding box. " +
                "This is an orientation and scale workflow, not a true surface morph.";
        }

        private static void BuildReport(
            StringBuilder report,
            int targetPanelCount,
            int generatedPanelCount,
            List<int> outPanelTileIndex,
            int tileTypeCount,
            int seed,
            List<List<GeometryBase>> tileBranches,
            List<double> tileWidths,
            List<double> tileHeights,
            List<double> weights,
            List<int> tileFitModes,
            List<string> panelGridInfo,
            List<string> warnings)
        {
            report.AppendLine("wsp_Fa06 Random Weighted Tile Panelizer");
            report.AppendLine("----------------------------------------");
            report.AppendLine("Target panel count: " + targetPanelCount);
            report.AppendLine("Generated panels: " + generatedPanelCount);
            report.AppendLine("panel_t_index count: " + outPanelTileIndex.Count);
            report.AppendLine("Tile type branches: " + tileTypeCount);
            report.AppendLine("Seed: " + seed);
            report.AppendLine("");

            report.AppendLine("Fit modes:");
            report.AppendLine("    0 = Fit to panel / non-uniform scale / full coverage");
            report.AppendLine("    1 = Preserve tile size / centered leftovers");
            report.AppendLine("    2 = Preserve aspect ratio / uniform scale");
            report.AppendLine("");

            report.AppendLine("Tile branch data:");
            for (int i = 0; i < tileTypeCount; i++)
            {
                report.AppendLine(
                    "    Branch " + i +
                    " -> geos: " + tileBranches[i].Count +
                    ", bbox XY: " + Math.Round(tileWidths[i], 3) + " x " + Math.Round(tileHeights[i], 3) +
                    ", weight: " + Math.Round(weights[i], 3) +
                    ", fit_mode: " + tileFitModes[i]
                );
            }

            report.AppendLine("");
            report.AppendLine("Panel tile assignments:");
            for (int i = 0; i < panelGridInfo.Count; i++)
            {
                report.AppendLine("    " + panelGridInfo[i]);
            }

            report.AppendLine("");
            report.AppendLine("panel_t_index:");
            report.AppendLine("    [" + string.Join(", ", outPanelTileIndex.Select(x => x.ToString()).ToArray()) + "]");

            if (generatedPanelCount < targetPanelCount)
            {
                warnings.Add("Could not reach target panel_count. Some surface splits may have failed.");
            }

            if (warnings.Count > 0)
            {
                report.AppendLine("");
                report.AppendLine("Warnings:");
                for (int i = 0; i < warnings.Count; i++)
                {
                    report.AppendLine("    - " + warnings[i]);
                }
            }

            report.AppendLine("");
            report.AppendLine("Output tree paths:");
            report.AppendLine("    panels: flat list");
            report.AppendLine("    panel_t_index: flat list, one tile branch index per panel");
            report.AppendLine("    panels_tiles: {panel_index}");
            report.AppendLine("    tiles_oriented: {panel_index; tile_type_index; tile_cell_index}");
            report.AppendLine("");
            report.AppendLine("Limitations:");
            report.AppendLine("    - Base tile geometries are assumed to be modeled flat in World XY.");
            report.AppendLine("    - Tile dimensions are taken from each branch's combined World XY bounding box.");
            report.AppendLine("    - This is an orientation and scale workflow, not a true surface morph.");
            report.AppendLine("    - For curved surfaces, tile placement is locally oriented to each UV cell, but geometry is not bent.");
            report.AppendLine("    - If base_surf is a trimmed Brep, the underlying face surface is used, so trims may be ignored.");
        }

        private struct PanelSize
        {
            public double ULength;
            public double VLength;
            public double ApproxArea;

            public PanelSize(double uLength, double vLength)
            {
                ULength = uLength;
                VLength = vLength;
                ApproxArea = uLength * vLength;
            }
        }

        private struct PanelTilingLayout
        {
            public int CountU;
            public int CountV;
            public double OffsetU;
            public double OffsetV;
            public double StepU;
            public double StepV;
            public double TileRatioU;
            public double TileRatioV;
            public double UsedTileW;
            public double UsedTileH;

            public PanelTilingLayout(
                int countU,
                int countV,
                double offsetU,
                double offsetV,
                double stepU,
                double stepV,
                double tileRatioU,
                double tileRatioV,
                double usedTileW,
                double usedTileH)
            {
                CountU = countU;
                CountV = countV;
                OffsetU = offsetU;
                OffsetV = offsetV;
                StepU = stepU;
                StepV = stepV;
                TileRatioU = tileRatioU;
                TileRatioV = tileRatioV;
                UsedTileW = usedTileW;
                UsedTileH = usedTileH;
            }
        }

        private static Surface GetSurfaceFromObject(object obj)
        {
            if (obj == null) return null;

            if (obj is Surface surface)
            {
                return surface;
            }

            if (obj is Brep brep && brep.Faces.Count > 0)
            {
                Surface srf = brep.Faces[0].UnderlyingSurface();
                return srf;
            }

            if (obj is BrepFace face)
            {
                Surface srf = face.UnderlyingSurface();
                return srf;
            }

            if (obj is GH_ObjectWrapper wrapper)
            {
                return GetSurfaceFromObject(wrapper.Value);
            }

            if (obj is GH_Surface ghSurface && ghSurface.Value != null)
            {
                return GetSurfaceFromObject(ghSurface.Value);
            }

            if (obj is GH_Brep ghBrep && ghBrep.Value != null)
            {
                return GetSurfaceFromObject(ghBrep.Value);
            }

            if (obj is IGH_Goo goo)
            {
                object scriptVar = goo.ScriptVariable();
                if (!ReferenceEquals(scriptVar, obj))
                {
                    return GetSurfaceFromObject(scriptVar);
                }
            }

            return null;
        }

        private static GeometryBase GetGeometryFromObject(object obj)
        {
            if (obj == null) return null;

            if (obj is GeometryBase geometry)
            {
                return geometry.Duplicate();
            }

            if (obj is Point3d point)
            {
                return new Rhino.Geometry.Point(point);
            }

            if (obj is Line line)
            {
                return new LineCurve(line);
            }

            if (obj is Polyline polyline)
            {
                return new PolylineCurve(polyline);
            }

            if (obj is Arc arc)
            {
                return new ArcCurve(arc);
            }

            if (obj is Circle circle)
            {
                return new ArcCurve(circle);
            }

            if (obj is GH_ObjectWrapper wrapper)
            {
                return GetGeometryFromObject(wrapper.Value);
            }

            if (obj is IGH_Goo goo)
            {
                object scriptVar = goo.ScriptVariable();
                if (!ReferenceEquals(scriptVar, obj))
                {
                    return GetGeometryFromObject(scriptVar);
                }
            }

            return null;
        }

        private static BoundingBox GetCombinedBoundingBox(List<GeometryBase> geos)
        {
            BoundingBox combined = BoundingBox.Empty;

            if (geos == null || geos.Count == 0) return combined;

            for (int i = 0; i < geos.Count; i++)
            {
                if (geos[i] == null) continue;

                BoundingBox bbox = geos[i].GetBoundingBox(true);
                if (bbox.IsValid) combined.Union(bbox);
            }

            return combined;
        }

        private static List<double> PrepareWeights(List<double> inputWeights, int count)
        {
            var weights = new List<double>();

            for (int i = 0; i < count; i++)
            {
                double w = 1.0;

                if (inputWeights != null && i < inputWeights.Count)
                {
                    w = inputWeights[i];
                }

                if (double.IsNaN(w) || double.IsInfinity(w) || w < 0.0)
                {
                    w = 0.0;
                }

                weights.Add(w);
            }

            return weights;
        }

        private static List<int> PrepareFitModes(List<int> inputModes, int count)
        {
            var modes = new List<int>();

            for (int i = 0; i < count; i++)
            {
                int m = 0;

                if (inputModes != null && inputModes.Count > 0)
                {
                    if (inputModes.Count == 1)
                    {
                        m = inputModes[0];
                    }
                    else if (i < inputModes.Count)
                    {
                        m = inputModes[i];
                    }
                    else
                    {
                        m = inputModes[inputModes.Count - 1];
                    }
                }

                if (m < 0 || m > 2) m = 0;
                modes.Add(m);
            }

            return modes;
        }

        private static List<Surface> GeneratePanels(Surface baseSurface, int targetPanelCount, Random rnd, List<string> warnings)
        {
            var panelSurfaces = new List<Surface>();
            panelSurfaces.Add(baseSurface);

            int maxAttempts = Math.Max(10, targetPanelCount * 30);
            int attempts = 0;

            while (panelSurfaces.Count < targetPanelCount && attempts < maxAttempts)
            {
                attempts++;

                int largestIndex = FindLargestPanelIndex(panelSurfaces);
                if (largestIndex < 0) break;

                Surface panelToSplit = panelSurfaces[largestIndex];
                PanelSize ps = GetPanelSize(panelToSplit);

                int splitDirection = ps.ULength >= ps.VLength ? 0 : 1;
                Interval dom = panelToSplit.Domain(splitDirection);

                if (Math.Abs(dom.T1 - dom.T0) <= RhinoMath.ZeroTolerance)
                {
                    warnings.Add("A panel had a near-zero domain and could not be split.");
                    break;
                }

                double splitRatio = 0.35 + (rnd.NextDouble() * 0.30);
                double t = dom.T0 + ((dom.T1 - dom.T0) * splitRatio);

                Surface[] splitResult = panelToSplit.Split(splitDirection, t);
                if (splitResult == null || splitResult.Length < 2) continue;

                panelSurfaces.RemoveAt(largestIndex);

                for (int i = 0; i < splitResult.Length; i++)
                {
                    if (splitResult[i] != null) panelSurfaces.Add(splitResult[i]);
                }
            }

            return panelSurfaces;
        }

        private static int FindLargestPanelIndex(List<Surface> panels)
        {
            if (panels == null || panels.Count == 0) return -1;

            int largestIndex = -1;
            double largestArea = double.MinValue;

            for (int i = 0; i < panels.Count; i++)
            {
                if (panels[i] == null) continue;

                PanelSize ps = GetPanelSize(panels[i]);
                if (ps.ApproxArea > largestArea)
                {
                    largestArea = ps.ApproxArea;
                    largestIndex = i;
                }
            }

            return largestIndex;
        }

        private static PanelSize GetPanelSize(Surface srf)
        {
            if (srf == null) return new PanelSize(1.0, 1.0);

            Interval uDom = srf.Domain(0);
            Interval vDom = srf.Domain(1);

            double uMid = 0.5 * (uDom.T0 + uDom.T1);
            double vMid = 0.5 * (vDom.T0 + vDom.T1);

            Point3d pU0 = srf.PointAt(uDom.T0, vMid);
            Point3d pU1 = srf.PointAt(uDom.T1, vMid);
            Point3d pV0 = srf.PointAt(uMid, vDom.T0);
            Point3d pV1 = srf.PointAt(uMid, vDom.T1);

            double uLength = pU0.DistanceTo(pU1);
            double vLength = pV0.DistanceTo(pV1);

            if (uLength <= RhinoMath.ZeroTolerance) uLength = 1.0;
            if (vLength <= RhinoMath.ZeroTolerance) vLength = 1.0;

            return new PanelSize(uLength, vLength);
        }

        private static PanelTilingLayout GetPanelTilingLayout(double panelW, double panelH, double tileW, double tileH, int fitMode)
        {
            if (panelW <= RhinoMath.ZeroTolerance) panelW = 1.0;
            if (panelH <= RhinoMath.ZeroTolerance) panelH = 1.0;
            if (tileW <= RhinoMath.ZeroTolerance) tileW = panelW;
            if (tileH <= RhinoMath.ZeroTolerance) tileH = panelH;

            if (fitMode == 0)
            {
                int countU = Math.Max(1, (int)Math.Round(panelW / tileW));
                int countV = Math.Max(1, (int)Math.Round(panelH / tileH));

                double stepU = 1.0 / countU;
                double stepV = 1.0 / countV;

                double usedTileW = panelW / countU;
                double usedTileH = panelH / countV;

                return new PanelTilingLayout(
                    countU,
                    countV,
                    0.0,
                    0.0,
                    stepU,
                    stepV,
                    stepU,
                    stepV,
                    usedTileW,
                    usedTileH
                );
            }

            if (fitMode == 1)
            {
                int countU = Math.Max(1, (int)Math.Floor(panelW / tileW));
                int countV = Math.Max(1, (int)Math.Floor(panelH / tileH));

                if (tileW > panelW) countU = 1;
                if (tileH > panelH) countV = 1;

                double tileRatioU = Math.Min(tileW / panelW, 1.0);
                double tileRatioV = Math.Min(tileH / panelH, 1.0);

                double stepU = tileRatioU;
                double stepV = tileRatioV;

                double offsetU = Math.Max(0.0, (1.0 - (countU * tileRatioU)) * 0.5);
                double offsetV = Math.Max(0.0, (1.0 - (countV * tileRatioV)) * 0.5);

                double usedTileW = Math.Min(tileW, panelW);
                double usedTileH = Math.Min(tileH, panelH);

                return new PanelTilingLayout(
                    countU,
                    countV,
                    offsetU,
                    offsetV,
                    stepU,
                    stepV,
                    tileRatioU,
                    tileRatioV,
                    usedTileW,
                    usedTileH
                );
            }

            int countU2 = Math.Max(1, (int)Math.Round(panelW / tileW));
            int countV2 = Math.Max(1, (int)Math.Round(panelH / tileH));

            double cellW = panelW / countU2;
            double cellH = panelH / countV2;

            double uniformScale = Math.Min(cellW / tileW, cellH / tileH);

            double usedTileW2 = tileW * uniformScale;
            double usedTileH2 = tileH * uniformScale;

            double tileRatioU2 = usedTileW2 / panelW;
            double tileRatioV2 = usedTileH2 / panelH;

            double stepU2 = 1.0 / countU2;
            double stepV2 = 1.0 / countV2;

            double localMarginU = Math.Max(0.0, (stepU2 - tileRatioU2) * 0.5);
            double localMarginV = Math.Max(0.0, (stepV2 - tileRatioV2) * 0.5);

            return new PanelTilingLayout(
                countU2,
                countV2,
                localMarginU,
                localMarginV,
                stepU2,
                stepV2,
                tileRatioU2,
                tileRatioV2,
                usedTileW2,
                usedTileH2
            );
        }

        private static bool GetCellTargetPlane(
            Surface srf,
            double u0,
            double u1,
            double v0,
            double v1,
            out Plane targetPlane,
            out double cellWidth,
            out double cellHeight)
        {
            targetPlane = Plane.WorldXY;
            cellWidth = 1.0;
            cellHeight = 1.0;

            if (srf == null) return false;

            Point3d p00 = srf.PointAt(u0, v0);
            Point3d p10 = srf.PointAt(u1, v0);
            Point3d p01 = srf.PointAt(u0, v1);

            Vector3d xAxis = p10 - p00;
            Vector3d yAxis = p01 - p00;

            cellWidth = xAxis.Length;
            cellHeight = yAxis.Length;

            if (cellWidth <= RhinoMath.ZeroTolerance || cellHeight <= RhinoMath.ZeroTolerance) return false;

            xAxis.Unitize();
            yAxis.Unitize();

            targetPlane = new Plane(p00, xAxis, yAxis);
            return targetPlane.IsValid;
        }

        private static int SelectWeightedIndex(List<double> weights, double totalWeight, Random rnd)
        {
            if (weights == null || weights.Count == 0) return 0;
            if (rnd == null) rnd = new Random();

            double r = rnd.NextDouble() * totalWeight;
            double cumulative = 0.0;

            for (int i = 0; i < weights.Count; i++)
            {
                cumulative += weights[i];
                if (r <= cumulative) return i;
            }

            return weights.Count - 1;
        }

        private static double Clamp01(double value)
        {
            if (value < 0.0) return 0.0;
            if (value > 1.0) return 1.0;
            return value;
        }
    }
}
