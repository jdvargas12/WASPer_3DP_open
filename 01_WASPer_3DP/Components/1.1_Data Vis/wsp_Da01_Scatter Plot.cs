#region Component Description
/*
    Component Name:
        wsp_Da01_Scatter Plot

    Nickname:
        Scatter Plot

    Version:
        Assembly-derived (vMajor.Minor.Build)

    Category / Subcategory:
        WASPerformance / 1.1_Data Vis

    Description:
        Draws and saves a multi-dataset scatter/line chart using native .NET drawing only.
        No Python, matplotlib, NumPy, or separately installed plotting library is required.
        x_vals is a single shared sample axis; each y_vals branch is one series and corresponding
        items are XY points. The shared X list is clipped against each Y branch to the shortest
        available length.
        If only one coordinate tree is supplied, the missing coordinate is generated as 0..N-1
        independently inside each branch.
        An optional first rectangle input displays the saved chart in the Rhino viewport.
        An optional sample index highlights the item at that position in every series with a ring
        marker and its data value(s): both x and y when both are real data, or just whichever
        coordinate is real if the other was auto-generated as 0..N-1.
        The mode input controls drawing style: 0 = markers, 1 = line and markers, 2 = lines.
        Marker/line visual settings are supplied by Da09 Marker + Line Params.
        An optional y_vals_2 tree adds series plotted against a secondary (right-hand) Y axis and
        uses the same shared x_vals axis; the secondary axis only appears when y_vals_2 actually
        carries data, and its own label/range/ticks come from Da10's y2_* inputs.

    Outputs:
        scatter_plot : full path of the generated PNG or JPEG image
        data_x       : plotted X values as a tree with branches {axis;series}
        data_y       : plotted Y values as a tree with branches {axis;series}
        range_x      : X min/max per branch as {axis;series} -> [0] min, [1] max
        range_y      : Y min/max per branch as {axis;series} -> [0] min, [1] max
*/
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Display;
using Rhino.Geometry;

namespace WASPer_3DP.Components._1_1_Data_Vis
{
    public sealed class wsp_Da01_Scatter_Plot : GH_Component
    {
        private const string NAME = "wsp_Da01_Scatter Plot";
        private const string NICK = "Scatter Plot";
        private const string CAT = global::WASPer_3DP.WASPerPalette.Performance;
        private const string SUBCAT = "1.1_Data Vis";
        private const int MaxTicksPerAxis = 500;

        private static readonly Color[] DefaultColors =
        {
            Color.FromArgb(31, 119, 180), Color.FromArgb(255, 127, 14),
            Color.FromArgb(44, 160, 44), Color.FromArgb(214, 39, 40),
            Color.FromArgb(148, 103, 189), Color.FromArgb(140, 86, 75),
            Color.FromArgb(227, 119, 194), Color.FromArgb(127, 127, 127),
            Color.FromArgb(188, 189, 34), Color.FromArgb(23, 190, 207)
        };

        private readonly string _versionTag;
        private Mesh _previewMesh;
        private DisplayMaterial _previewMaterial;

        public wsp_Da01_Scatter_Plot()
            : base(
                NAME,
                NICK,
                "Creates a publication-ready scatter or line chart from paired Grasshopper data-tree branches and saves it as PNG or JPEG. " +
                "The renderer is implemented entirely in compiled C# with native .NET drawing; it requires no Python, matplotlib, NumPy, or extra plotting installation.",
                CAT,
                SUBCAT)
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version != null
                ? $"v{version.Major}.{version.Minor}.{version.Build}"
                : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("77AF58EF-B23F-4095-BBFE-4F1163D34D3F");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Da01_Scatter Plot.png"))
                        return stream != null ? new Bitmap(stream) : null;
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddRectangleParameter(
                "chart_rect", "chart_rect",
                "Optional planar Rhino rectangle for an aspect-preserving chart preview. The renderer automatically uses normal or 90-degree orientation to maximize the occupied rectangle area.",
                GH_ParamAccess.item);

            p.AddNumberParameter(
                "x_vals", "x_vals",
                "Optional shared X-coordinate list. This input is flattened by default and reused for every Y branch. Each series is clipped to the shorter of the X-list and its Y branch; an orange warning reports clipping. If omitted while y_vals is supplied, X is generated as 0..N-1 independently in every Y branch.",
                GH_ParamAccess.tree);
            p[1].DataMapping = GH_DataMapping.Flatten;

            p.AddNumberParameter(
                "y_vals_1", "y_vals_1",
                "Optional primary Y-coordinate data tree, plotted against the left Y axis. Each branch is one dataset and is paired with the shared x_vals list. If omitted while x_vals is supplied, Y is generated as 0..N-1.",
                GH_ParamAccess.tree);

            p.AddNumberParameter(
                "y_vals_2", "y_vals_2",
                "Optional secondary Y-coordinate data tree, plotted against the right Y axis. Each branch is one dataset paired with the shared x_vals list and clipped to the shortest length. The secondary axis is only drawn when this tree carries data; its label/range/ticks come from Da10's y2_* inputs.",
                GH_ParamAccess.tree);

            p.AddIntegerParameter(
                "highlight_ind", "highlight",
                "Optional zero-based item index to highlight with a ring marker at that position in every series, labelled with its data value(s) (x and y if both are real data, otherwise just the real one). Disconnected or negative disables highlighting.",
                GH_ParamAccess.item, -1);

            p.AddIntegerParameter(
                "mode", "mode",
                "Chart drawing mode. 0 = markers only; 1 = line and markers; 2 = lines only. Lines connect points in existing item order and are not sorted by X. Default 0.",
                GH_ParamAccess.item, 0);

            p.AddGenericParameter(
                "marker_params", "marker_p",
                "Optional reusable settings from wsp_Da09_Marker + Line Params. Controls marker colours/sizes/types and line colours/widths/patterns. If fewer marker or line colours than plotted series are supplied, colour anchors are interpolated across the series; one colour is repeated and disconnected uses the default palette.",
                GH_ParamAccess.item);

            p.AddGenericParameter(
                "axis_params", "axis_p",
                "Optional reusable settings from wsp_Da10_Chart Axis Params. When disconnected, Da01 uses clean automatic ranges/ticks, no axis titles, 12 pt titles, and 10 pt tick text.",
                GH_ParamAccess.item);

            p.AddGenericParameter(
                "legend_params", "legend_p",
                "Optional reusable settings from wsp_Da11_Chart Legend Params. When disconnected, no legend is drawn.",
                GH_ParamAccess.item);

            p.AddGenericParameter(
                "layout_params", "layout_p",
                "Optional reusable settings from wsp_Da12_Chart Layout Params. When disconnected, Da01 uses no title, 160;100 mm, 150 DPI, white PNG, and an automatic output folder/name.",
                GH_ParamAccess.item);

            p.AddBooleanParameter(
                "refresh", "refresh",
                "Inert compatibility trigger. Toggle it, connect a Button, or attach a Timer to regenerate and overwrite the current output file.",
                GH_ParamAccess.item, false);

            for (int i = 0; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter(
                "scatter_plot", "scatter_plot",
                "Absolute path to the generated PNG or JPEG chart. The file is overwritten when the component recomputes with the same path.",
                GH_ParamAccess.item);

            p.AddNumberParameter(
                "data_x", "data_x",
                "Plotted X values as a data tree. Branch path {axis;series}: axis 0 = primary Y-axis series, axis 1 = secondary Y-axis series; items are samples in plotted order.",
                GH_ParamAccess.tree);

            p.AddNumberParameter(
                "data_y", "data_y",
                "Plotted Y values as a data tree. Branch path {axis;series}: axis 0 = primary Y-axis series, axis 1 = secondary Y-axis series; items are samples in plotted order.",
                GH_ParamAccess.tree);

            p.AddNumberParameter(
                "range_x", "range_x",
                "Per-series X data range as a tree. Branch path {axis;series}; item 0 is minimum X and item 1 is maximum X.",
                GH_ParamAccess.tree);

            p.AddNumberParameter(
                "range_y", "range_y",
                "Per-series Y data range as a tree. Branch path {axis;series}; item 0 is minimum Y and item 1 is maximum Y.",
                GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            ResetPreview();

            Rectangle3d chartRectangle = Rectangle3d.Unset;
            bool hasChartRectangle = DA.GetData(0, ref chartRectangle) && chartRectangle.IsValid;

            GH_Structure<GH_Number> xTree;
            GH_Structure<GH_Number> y1Tree;
            GH_Structure<GH_Number> y2Tree;
            bool hasXTree = DA.GetDataTree(1, out xTree) && xTree != null && xTree.DataCount > 0;
            bool hasY1Tree = DA.GetDataTree(2, out y1Tree) && y1Tree != null && y1Tree.DataCount > 0;
            bool hasY2Tree = DA.GetDataTree(3, out y2Tree) && y2Tree != null && y2Tree.DataCount > 0;
            if (!hasXTree && !hasY1Tree && !hasY2Tree)
            {
                SetError(DA, "Supply at least one coordinate tree. When only x_vals or only y_vals_1 is supplied, the missing coordinate is generated as 0..N-1 per branch.");
                return;
            }
            if (!hasXTree) xTree = null;
            if (!hasY1Tree) y1Tree = null;
            if (!hasY2Tree) y2Tree = null;

            int sampleIndex = -1;
            int mode = 0;
            bool refresh = false;
            object rawMarker = null, rawAxis = null, rawLegend = null, rawLayout = null;

            DA.GetData(4, ref sampleIndex);
            DA.GetData(5, ref mode);
            DA.GetData(6, ref rawMarker);
            DA.GetData(7, ref rawAxis);
            DA.GetData(8, ref rawLegend);
            DA.GetData(9, ref rawLayout);
            DA.GetData(10, ref refresh);

            mode = Math.Max(0, Math.Min(2, mode));
            WasperChartMarkerLineSettings markerLine = WasperChartSettingsTools.MarkerLine(rawMarker) ?? new WasperChartMarkerLineSettings();
            WasperChartAxisSettings axis = WasperChartSettingsTools.Axis(rawAxis) ?? new WasperChartAxisSettings();
            WasperChartLegendSettings legend = WasperChartSettingsTools.Legend(rawLegend) ?? new WasperChartLegendSettings();
            WasperChartLayoutSettings layout = WasperChartSettingsTools.Layout(rawLayout) ?? new WasperChartLayoutSettings();

            string xTitle = axis.XTitle ?? string.Empty;
            string yTitle = axis.YTitle ?? string.Empty;
            string xMinMax = axis.XRange ?? string.Empty;
            string yMinMax = axis.YRange ?? string.Empty;
            double xTickInterval = axis.XTickInterval;
            double yTickInterval = axis.YTickInterval;
            bool xTicksInteger = axis.XTicksInteger;
            bool yTicksInteger = axis.YTicksInteger;
            double xTitleSize = axis.XTitleSize;
            double yTitleSize = axis.YTitleSize;
            double xTextSize = axis.XTextSize;
            double yTextSize = axis.YTextSize;
            double titleOffset = axis.TitleOffset;

            string y2Title = axis.Y2Title ?? string.Empty;
            string y2MinMax = axis.Y2Range ?? string.Empty;
            double y2TickInterval = axis.Y2TickInterval;
            bool y2TicksInteger = axis.Y2TicksInteger;

            List<string> legendLabels = legend.Labels ?? new List<string>();
            int legendLocation = legend.Location;
            double legendDistance = legend.Distance;
            bool legendRows = legend.WrapRows;
            int legendColumns = legend.Columns;
            double legendTextSize = legend.TextSize;

            int dpi = layout.Dpi;
            bool transparentBackground = layout.TransparentBackground;
            string chartDims = layout.Dimensions ?? "160;100";
            string chartTitle = layout.Title ?? string.Empty;
            double chartTitleSize = layout.TitleSize;
            string fileName = layout.FileName ?? string.Empty;
            string filePath = layout.FilePath ?? string.Empty;

            var warnings = new List<string>();
            List<SeriesData> primarySeries = BuildSeries(xTree, y1Tree, legendLabels, markerLine, warnings, 0, 1);
            List<SeriesData> secondarySeries = hasY2Tree
                ? BuildSeries(xTree, y2Tree, legendLabels, markerLine, warnings, primarySeries.Count, 2)
                : new List<SeriesData>();
            List<SeriesData> series = primarySeries.Concat(secondarySeries).ToList();
            var markerColors = WasperChartSettingsTools.ResolveSeriesColors(markerLine?.MarkerColorsArgb, series.Count, DefaultColors.ToList());
            var lineColors = WasperChartSettingsTools.ResolveSeriesColors(markerLine?.LineColorsArgb, series.Count, markerColors);
            for (int i = 0; i < series.Count; i++)
            {
                series[i].MarkerColor = markerColors[i];
                series[i].LineColor = lineColors[i];
            }
            bool hasPrimaryPoints = primarySeries.Sum(s => s.Points.Count) > 0;
            bool hasSecondaryPoints = secondarySeries.Sum(s => s.Points.Count) > 0;
            bool hasY2Axis = hasSecondaryPoints;

            if (!hasPrimaryPoints && !hasSecondaryPoints)
            {
                SetError(DA, "No paired finite XY values were found. Supply matching numeric branches to x_vals and y_vals_1/y_vals_2.");
                return;
            }

            double dataXMin = series.SelectMany(s => s.Points).Min(p => p.X);
            double dataXMax = series.SelectMany(s => s.Points).Max(p => p.X);
            double dataYMin = hasPrimaryPoints ? primarySeries.SelectMany(s => s.Points).Min(p => p.Y) : 0.0;
            double dataYMax = hasPrimaryPoints ? primarySeries.SelectMany(s => s.Points).Max(p => p.Y) : 1.0;

            if (!ResolveRange(xMinMax, dataXMin, dataXMax, out double xMin, out double xMax, out string xRangeError))
            {
                SetError(DA, "x_min_max: " + xRangeError);
                return;
            }
            if (!ResolveRange(yMinMax, dataYMin, dataYMax, out double yMin, out double yMax, out string yRangeError))
            {
                SetError(DA, "y_min_max: " + yRangeError);
                return;
            }

            xTickInterval = ResolveTickInterval(xTickInterval, xMax - xMin);
            yTickInterval = ResolveTickInterval(yTickInterval, yMax - yMin);
            if (!ValidateTickCount(xMin, xMax, xTickInterval, out _) ||
                !ValidateTickCount(yMin, yMax, yTickInterval, out _))
            {
                SetError(DA, $"Tick interval creates too many ticks. Maximum is {MaxTicksPerAxis} per axis.");
                return;
            }

            double y2Min = 0, y2Max = 1, y2Tick = 1;
            if (hasY2Axis)
            {
                double dataY2Min = secondarySeries.SelectMany(s => s.Points).Min(p => p.Y);
                double dataY2Max = secondarySeries.SelectMany(s => s.Points).Max(p => p.Y);
                if (!ResolveRange(y2MinMax, dataY2Min, dataY2Max, out y2Min, out y2Max, out string y2RangeError))
                {
                    SetError(DA, "y2_min_max: " + y2RangeError);
                    return;
                }
                y2Tick = ResolveTickInterval(y2TickInterval, y2Max - y2Min);
                if (!ValidateTickCount(y2Min, y2Max, y2Tick, out _))
                {
                    SetError(DA, $"Secondary Y tick interval creates too many ticks. Maximum is {MaxTicksPerAxis} per axis.");
                    return;
                }
            }
            if (!TryParseDimensions(chartDims, out double widthMm, out double heightMm))
            {
                SetError(DA, "chart dimensions must be 'width_mm;height_mm'.");
                return;
            }
            dpi = Math.Max(36, Math.Min(1200, dpi));
            int widthPx = Math.Max(64, (int)Math.Round(widthMm / 25.4 * dpi));
            int heightPx = Math.Max(64, (int)Math.Round(heightMm / 25.4 * dpi));
            if (widthPx > 16000 || heightPx > 16000 || (long)widthPx * heightPx > 100_000_000L)
            {
                SetError(DA, "Requested bitmap is too large.");
                return;
            }

            string extension = ResolveExtension(fileName);
            bool jpeg = extension == ".jpg" || extension == ".jpeg";
            if (jpeg && transparentBackground) warnings.Add("JPEG transparency was flattened onto white.");

            string fullPath;
            try
            {
                string dir = ResolveOutputDirectory(filePath);
                Directory.CreateDirectory(dir);
                fullPath = Path.Combine(dir, ResolveFileName(fileName, chartTitle, extension));
            }
            catch (Exception ex)
            {
                SetError(DA, "Could not prepare output path: " + ex.Message);
                return;
            }

            var settings = new ChartSettings
            {
                XTitle = xTitle,
                YTitle = yTitle,
                Title = chartTitle,
                Mode = mode,
                LegendVisible = legendLabels.Count > 0,
                LegendLocation = Math.Max(0, Math.Min(11, legendLocation)),
                LegendDistance = Math.Max(0, Math.Min(.5, legendDistance)),
                LegendRows = legendRows,
                LegendColumns = Math.Max(1, legendColumns),
                LegendTextSize = PositiveOrDefault(legendTextSize, 10),
                XTitleSize = PositiveOrDefault(xTitleSize, 12),
                YTitleSize = PositiveOrDefault(yTitleSize, 12),
                XTextSize = PositiveOrDefault(xTextSize, 10),
                YTextSize = PositiveOrDefault(yTextSize, 10),
                TitleSize = PositiveOrDefault(chartTitleSize, 14),
                XMin = xMin,
                XMax = xMax,
                YMin = yMin,
                YMax = yMax,
                XTick = xTickInterval,
                YTick = yTickInterval,
                XTicksInteger = xTicksInteger,
                YTicksInteger = yTicksInteger,
                Transparent = transparentBackground && !jpeg,
                ShowReferences = layout.ShowReferences,
                Dpi = dpi,
                HighlightIndex = sampleIndex,
                TitleOffset = titleOffset,
                HasY2 = hasY2Axis,
                Y2Title = y2Title,
                Y2Min = y2Min,
                Y2Max = y2Max,
                Y2Tick = y2Tick,
                Y2TicksInteger = y2TicksInteger
            };
            try
            {
                using Bitmap bitmap = RenderChart(widthPx, heightPx, series, settings);
                bitmap.SetResolution(dpi, dpi);
                if (jpeg)
                {
                    using var flat = new Bitmap(widthPx, heightPx, PixelFormat.Format24bppRgb);
                    using Graphics g = Graphics.FromImage(flat);
                    g.Clear(Color.White);
                    g.DrawImageUnscaled(bitmap, 0, 0);
                    flat.SetResolution(dpi, dpi);
                    flat.Save(fullPath, ImageFormat.Jpeg);
                }
                else
                {
                    bitmap.Save(fullPath, ImageFormat.Png);
                }
            }
            catch (Exception ex)
            {
                SetError(DA, "Chart rendering or save failed: " + ex.Message);
                return;
            }

            if (!hasChartRectangle)
            {
                chartRectangle = new Rectangle3d(Plane.WorldXY, new Interval(-widthMm * .5, widthMm * .5), new Interval(-heightMm * .5, heightMm * .5));
                hasChartRectangle = true;
            }
            if (hasChartRectangle && !CreatePreview(chartRectangle, widthPx / (double)heightPx, fullPath))
                warnings.Add("Rectangle preview failed; image file was saved.");
            foreach (string warning in warnings)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, warning);
            Message = $"{_versionTag} | {series.Count} sets";
            DA.SetData(0, fullPath);
            SetDataOutputs(DA, series);
        }

        // ── preview ───────────────────────────────────────────────────────────
        public override BoundingBox ClippingBox => _previewMesh?.GetBoundingBox(false) ?? BoundingBox.Empty;

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            base.DrawViewportMeshes(args);
            if (_previewMesh != null && _previewMaterial != null) args.Display.DrawMeshShaded(_previewMesh, _previewMaterial);
        }

        private bool CreatePreview(Rectangle3d rectangle, double aspect, string path)
        {
            ResetPreview();
            if (!rectangle.IsValid || !IsFinite(aspect) || aspect <= 0 || !File.Exists(path)) return false;

            double aw = rectangle.Width, ah = rectangle.Height;
            if (aw <= 0 || ah <= 0) return false;

            // Cover the rectangle without rotating the chart. This keeps chart X
            // aligned with the rectangle/world X direction and chart Y with Y.
            FitAspect(aw, ah, aspect, out double fw, out double fh);
            Plane plane = rectangle.Plane;
            plane.Origin = rectangle.Center;

            var rect = new Rectangle3d(plane, new Interval(-fw * .5, fw * .5), new Interval(-fh * .5, fh * .5));
            var mesh = new Mesh();
            for (int i = 0; i < 4; i++) mesh.Vertices.Add(rect.Corner(i));
            mesh.Faces.AddFace(0, 1, 2, 3);

            mesh.TextureCoordinates.Add(0, 0);
            mesh.TextureCoordinates.Add(1, 0);
            mesh.TextureCoordinates.Add(1, 1);
            mesh.TextureCoordinates.Add(0, 1);

            mesh.Normals.ComputeNormals();
            mesh.Compact();
            var material = new DisplayMaterial(Color.White);
            if (!material.SetBitmapTexture(path, true))
            {
                material.Dispose();
                mesh.Dispose();
                return false;
            }
            _previewMesh = mesh;
            _previewMaterial = material;
            return true;
        }

        private static void FitAspect(double aw, double ah, double aspect, out double width, out double height)
        {
            width = aw;
            height = width / aspect;
            if (height < ah)
            {
                height = ah;
                width = height * aspect;
            }
        }

        private void ResetPreview()
        {
            _previewMesh?.Dispose();
            _previewMesh = null;
            _previewMaterial?.Dispose();
            _previewMaterial = null;
        }

        // ── series construction ───────────────────────────────────────────────
        // indexOffset lets a second call (e.g. for y_vals_2) continue the same global series
        // numbering/labels/style lookup instead of restarting at 0; axisIndex tags every
        // series produced by this call as belonging to the primary (1) or secondary (2) Y axis.
        private static List<SeriesData> BuildSeries(GH_Structure<GH_Number> xTree, GH_Structure<GH_Number> yTree, IList<string> labels, WasperChartMarkerLineSettings style, IList<string> warnings, int indexOffset, int axisIndex)
        {
            var result = new List<SeriesData>();
            if (xTree == null && yTree == null) return result;

            if (xTree == null)
            {
                warnings.Add("x_vals omitted; generated X=0..N-1 per branch.");
                for (int i = 0; i < yTree.PathCount; i++)
                {
                    IList<GH_Number> ys = yTree.Branches[i];
                    SeriesData data = CreateSeries(indexOffset + i, i, axisIndex, labels, style);
                    data.HasRealX = false;
                    int skipped = 0;
                    for (int j = 0; j < ys.Count; j++)
                    {
                        double y = ys[j]?.Value ?? double.NaN;
                        if (!IsFinite(y)) { skipped++; continue; }
                        data.Points.Add(new PointD(j, y));
                    }
                    if (skipped > 0) warnings.Add($"Series {indexOffset + i + 1}: ignored {skipped} non-finite Y values.");
                    result.Add(data);
                }
                return result;
            }

            if (yTree == null)
            {
                warnings.Add("y_vals was not supplied; generated Y=0..N-1 independently for each x_vals branch.");
                for (int i = 0; i < xTree.PathCount; i++)
                {
                    IList<GH_Number> xs = xTree.Branches[i];
                    SeriesData data = CreateSeries(indexOffset + i, i, axisIndex, labels, style);
                    data.HasRealY = false;
                    int skipped = 0;
                    for (int j = 0; j < xs.Count; j++)
                    {
                        double x = xs[j]?.Value ?? double.NaN;
                        if (!IsFinite(x)) { skipped++; continue; }
                        data.Points.Add(new PointD(x, j));
                    }
                    if (skipped > 0) warnings.Add($"Series {indexOffset + i + 1}: ignored {skipped} non-finite X values.");
                    result.Add(data);
                }
                return result;
            }

            bool sharedX = xTree.PathCount == 1 && yTree.PathCount > 1;
            int count = sharedX ? yTree.PathCount : Math.Min(xTree.PathCount, yTree.PathCount);
            if (xTree.PathCount != yTree.PathCount && !sharedX)
            {
                warnings.Add($"Branch count mismatch: x={xTree.PathCount}, y={yTree.PathCount}; paired first {count} branches by order.");
            }

            int skippedLength = 0;
            int skippedInvalid = 0;
            int clippedSeries = 0;
            for (int i = 0; i < count; i++)
            {
                IList<GH_Number> xs = sharedX ? xTree.Branches[0] : xTree.Branches[i];
                IList<GH_Number> ys = yTree.Branches[i];
                int paired = Math.Min(xs.Count, ys.Count);
                if (xs.Count != ys.Count)
                {
                    skippedLength += Math.Abs(xs.Count - ys.Count);
                    if (sharedX) clippedSeries++;
                }

                SeriesData data = CreateSeries(indexOffset + i, i, axisIndex, labels, style);

                for (int j = 0; j < paired; j++)
                {
                    double x = xs[j]?.Value ?? double.NaN;
                    double y = ys[j]?.Value ?? double.NaN;
                    if (!IsFinite(x) || !IsFinite(y))
                    {
                        skippedInvalid++;
                        continue;
                    }
                    data.Points.Add(new PointD(x, y));
                }
                result.Add(data);
            }

            if (sharedX && clippedSeries > 0)
                warnings.Add($"Shared x_vals was clipped to the shortest length for {clippedSeries} Y series; {skippedLength} tail values were omitted.");
            else if (skippedLength > 0)
                warnings.Add($"Ignored {skippedLength} unpaired branch-tail values.");
            if (skippedInvalid > 0) warnings.Add($"Ignored {skippedInvalid} non-finite XY pairs.");
            return result;
        }

        private static SeriesData CreateSeries(int index, int axisSeriesIndex, int axisIndex, IList<string> labels, WasperChartMarkerLineSettings style)
        {
            Color markerColor = PickColor(style?.MarkerColorsArgb, index, DefaultColors[index % DefaultColors.Length]);
            var data = new SeriesData
            {
                AxisIndex = axisIndex,
                AxisSeriesIndex = axisSeriesIndex,
                Label = index < labels.Count && !string.IsNullOrWhiteSpace(labels[index]) ? labels[index] : $"Series {index + 1}",
                MarkerColor = markerColor,
                LineColor = PickColor(style?.LineColorsArgb, index, markerColor),
                MarkerArea = PickDouble(style?.MarkerSizes, index, 30.0),
                MarkerType = PickInt(style?.MarkerTypes, index, 0),
                LineWidth = PickDouble(style?.LineWidths, index, 1.2),
                LineType = PickInt(style?.LineTypes, index, 0)
            };
            data.MarkerArea = Math.Max(1.0, data.MarkerArea);
            data.LineWidth = Math.Max(0.1, data.LineWidth);
            data.MarkerType = Math.Max(0, Math.Min(5, data.MarkerType));
            data.LineType = Math.Max(0, Math.Min(3, data.LineType));
            return data;
        }

        private static Color PickColor(IList<int> argbValues, int index, Color fallback) =>
            argbValues != null && argbValues.Count > 0
                ? WasperChartMarkerLineSettings.FromArgb(argbValues[Math.Min(index, argbValues.Count - 1)])
                : fallback;

        private static double PickDouble(IList<double> values, int index, double fallback) =>
            values != null && values.Count > 0 && IsFinite(values[Math.Min(index, values.Count - 1)])
                ? values[Math.Min(index, values.Count - 1)]
                : fallback;

        private static int PickInt(IList<int> values, int index, int fallback) =>
            values != null && values.Count > 0 ? values[Math.Min(index, values.Count - 1)] : fallback;

        private static void SetDataOutputs(IGH_DataAccess da, IList<SeriesData> series)
        {
            var dataX = new GH_Structure<GH_Number>();
            var dataY = new GH_Structure<GH_Number>();
            var rangeX = new GH_Structure<GH_Number>();
            var rangeY = new GH_Structure<GH_Number>();

            foreach (SeriesData data in series.OrderBy(s => s.AxisIndex).ThenBy(s => s.AxisSeriesIndex))
            {
                var path = new GH_Path(Math.Max(0, data.AxisIndex - 1), data.AxisSeriesIndex);

                foreach (PointD point in data.Points)
                {
                    dataX.Append(new GH_Number(point.X), path);
                    dataY.Append(new GH_Number(point.Y), path);
                }

                if (data.Points.Count == 0)
                    continue;

                double minX = data.Points.Min(p => p.X);
                double maxX = data.Points.Max(p => p.X);
                double minY = data.Points.Min(p => p.Y);
                double maxY = data.Points.Max(p => p.Y);

                rangeX.Append(new GH_Number(minX), path);
                rangeX.Append(new GH_Number(maxX), path);
                rangeY.Append(new GH_Number(minY), path);
                rangeY.Append(new GH_Number(maxY), path);
            }

            da.SetDataTree(1, dataX);
            da.SetDataTree(2, dataY);
            da.SetDataTree(3, rangeX);
            da.SetDataTree(4, rangeY);
        }

        private static Bitmap RenderChart(int width, int height, IList<SeriesData> series, ChartSettings s)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(s.Transparent ? Color.Transparent : Color.White);

                float scale = s.Dpi / 72f;
                using var titleFont = CreateFont(s.TitleSize);
                using var xTitleFont = CreateFont(s.XTitleSize);
                using var yTitleFont = CreateFont(s.YTitleSize);
                using var xTickFont = CreateFont(s.XTextSize);
                using var yTickFont = CreateFont(s.YTextSize);
                using var legendFont = CreateFont(s.LegendTextSize);
                using var highlightFont = CreateFont(Math.Max(7.0, s.XTextSize * 0.85));
                using var highlightPen = new Pen(Color.FromArgb(220, 20, 60), Math.Max(1.5f, 1.2f * scale));

                bool useIntegerXLabels = s.XTicksInteger && s.XTick >= 1.0;
                bool useIntegerYLabels = s.YTicksInteger && s.YTick >= 1.0;
                bool useIntegerY2Labels = s.Y2TicksInteger && s.Y2Tick >= 1.0;

                // Extra breathing room between tick labels and axis titles. The title itself stays
                // pinned near the outer image edge (see the title-drawing blocks below), so growing
                // these margins pushes the tick-label band away from it without moving the title.
                float titleOffsetPx = (float)s.TitleOffset * scale;

                // Every axis reserves the same two things, in the same order, so title_offset (and
                // the built-in default spacing) reads identically on X, Y, and Y2: room for the tick
                // labels themselves, then a full title-font-height reservation for the axis title
                // (always, regardless of whether that title string is empty — matches the X-axis
                // convention below, so adding/removing a title text doesn't shift the plot area).
                float left = Math.Max(44f * scale, g.MeasureString(FormatTick(s.YMin, useIntegerYLabels), yTickFont).Width + 26f * scale) + yTitleFont.GetHeight(g) + titleOffsetPx;
                float right = 16f * scale;
                float top = string.IsNullOrWhiteSpace(s.Title) ? 14f * scale : titleFont.GetHeight(g) + 16f * scale;
                float bottom = xTickFont.GetHeight(g) + xTitleFont.GetHeight(g) + 22f * scale + titleOffsetPx;

                if (s.HasY2)
                {
                    float y2TickWidth = Math.Max(
                        g.MeasureString(FormatTick(s.Y2Min, useIntegerY2Labels), yTickFont).Width,
                        g.MeasureString(FormatTick(s.Y2Max, useIntegerY2Labels), yTickFont).Width);
                    right = Math.Max(44f * scale, y2TickWidth + 26f * scale) + yTitleFont.GetHeight(g) + titleOffsetPx;
                }

                if (s.LegendVisible)
                {
                    if (s.LegendLocation == 8) right += Math.Min(width * 0.32f, 150f * scale);
                    if (s.LegendLocation == 9) left += Math.Min(width * 0.32f, 150f * scale);
                    if (s.LegendLocation == 10) top += legendFont.GetHeight(g) * 2.2f;
                    if (s.LegendLocation == 7 || s.LegendLocation == 11) bottom += legendFont.GetHeight(g) * 2.2f;
                }

                var plot = RectangleF.FromLTRB(left, top, Math.Max(left + 10f, width - right), Math.Max(top + 10f, height - bottom));
                Color plotBackground = s.Transparent ? Color.FromArgb(220, 255, 255, 255) : Color.White;
                using (var backgroundBrush = new SolidBrush(plotBackground)) g.FillRectangle(backgroundBrush, plot);

                List<double> xTicks = GenerateTicks(s.XMin, s.XMax, s.XTick);
                List<double> yTicks = GenerateTicks(s.YMin, s.YMax, s.YTick);
                using var gridPen = new Pen(Color.FromArgb(215, 215, 215), Math.Max(1f, 0.6f * scale));
                using var axisPen = new Pen(Color.FromArgb(45, 45, 45), Math.Max(1f, 0.9f * scale));
                using var textBrush = new SolidBrush(Color.FromArgb(35, 35, 35));
                using var centerFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };
                using var rightFormat = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };

                foreach (double tick in xTicks)
                {
                    float x = MapX(tick, plot, s.XMin, s.XMax);
                    if (s.ShowReferences) g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
                    g.DrawLine(axisPen, x, plot.Bottom, x, plot.Bottom + 4f * scale);
                    g.DrawString(FormatTick(tick, useIntegerXLabels), xTickFont, textBrush,
                        new RectangleF(x - 50f * scale, plot.Bottom + 5f * scale, 100f * scale, xTickFont.GetHeight(g) + 3f), centerFormat);
                }
                foreach (double tick in yTicks)
                {
                    float y = MapY(tick, plot, s.YMin, s.YMax);
                    if (s.ShowReferences) g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    g.DrawLine(axisPen, plot.Left - 4f * scale, y, plot.Left, y);
                    g.DrawString(FormatTick(tick, useIntegerYLabels), yTickFont, textBrush,
                        new RectangleF(0, y - yTickFont.GetHeight(g), plot.Left - 7f * scale, yTickFont.GetHeight(g) * 2f), rightFormat);
                }

                // ── secondary Y axis: ticks/labels only, no gridlines (avoids a second overlapping scale) ──
                if (s.HasY2)
                {
                    using var leftFormat = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };
                    foreach (double tick in GenerateTicks(s.Y2Min, s.Y2Max, s.Y2Tick))
                    {
                        float y = MapY(tick, plot, s.Y2Min, s.Y2Max);
                        g.DrawLine(axisPen, plot.Right, y, plot.Right + 4f * scale, y);
                        g.DrawString(FormatTick(tick, useIntegerY2Labels), yTickFont, textBrush,
                            new RectangleF(plot.Right + 5f * scale, y - yTickFont.GetHeight(g), 100f * scale, yTickFont.GetHeight(g) * 2f), leftFormat);
                    }
                }

                g.DrawRectangle(axisPen, plot.X, plot.Y, plot.Width, plot.Height);

                g.SetClip(plot);
                foreach (SeriesData data in series)
                {
                    using var pen = new Pen(data.LineColor, Math.Max(0.5f, (float)data.LineWidth * scale));
                    ApplyDashStyle(pen, data.LineType);
                    using var brush = new SolidBrush(data.MarkerColor);
                    double seriesYMin = data.AxisIndex == 2 ? s.Y2Min : s.YMin;
                    double seriesYMax = data.AxisIndex == 2 ? s.Y2Max : s.YMax;
                    var points = data.Points.Select(p => new PointF(MapX(p.X, plot, s.XMin, s.XMax), MapY(p.Y, plot, seriesYMin, seriesYMax))).ToArray();
                    if ((s.Mode == 1 || s.Mode == 2) && points.Length > 1) g.DrawLines(pen, points);
                    float diameter = Math.Max(2f * scale, (float)Math.Sqrt(data.MarkerArea) * scale);
                    if (s.Mode == 0 || s.Mode == 1)
                    {
                        foreach (PointF point in points)
                            DrawMarker(g, brush, pen, point, diameter, data.MarkerType);
                    }

                    if (s.HighlightIndex >= 0 && s.HighlightIndex < points.Length)
                    {
                        PointF hp = points[s.HighlightIndex];
                        PointD dp = data.Points[s.HighlightIndex];
                        float ringDiameter = diameter + 8f * scale;
                        g.DrawEllipse(highlightPen, hp.X - ringDiameter / 2f, hp.Y - ringDiameter / 2f, ringDiameter, ringDiameter);

                        string label;
                        int lineCount;
                        if (data.HasRealX && data.HasRealY)
                        {
                            label = $"x = {FormatValue(dp.X)}\ny = {FormatValue(dp.Y)}";
                            lineCount = 2;
                        }
                        else if (data.HasRealX)
                        {
                            label = $"x = {FormatValue(dp.X)}";
                            lineCount = 1;
                        }
                        else
                        {
                            label = $"y = {FormatValue(dp.Y)}";
                            lineCount = 1;
                        }
                        float textY = hp.Y - highlightFont.GetHeight(g) * lineCount / 2f;
                        g.DrawString(label, highlightFont, textBrush, hp.X + ringDiameter / 2f + 2f * scale, textY);
                    }
                }
                g.ResetClip();

                if (!string.IsNullOrWhiteSpace(s.Title))
                    g.DrawString(s.Title, titleFont, textBrush, new RectangleF(0, 3f * scale, width, titleFont.GetHeight(g) + 4f), centerFormat);
                if (!string.IsNullOrWhiteSpace(s.XTitle))
                    g.DrawString(s.XTitle, xTitleFont, textBrush,
                        new RectangleF(plot.Left, height - xTitleFont.GetHeight(g) - 3f * scale, plot.Width, xTitleFont.GetHeight(g) + 2f), centerFormat);
                if (!string.IsNullOrWhiteSpace(s.YTitle))
                {
                    GraphicsState state = g.Save();
                    g.TranslateTransform(yTitleFont.GetHeight(g) + 2f * scale, plot.Top + plot.Height / 2f);
                    g.RotateTransform(-90f);
                    g.DrawString(s.YTitle, yTitleFont, textBrush, new RectangleF(-plot.Height / 2f, 0, plot.Height, yTitleFont.GetHeight(g) + 2f), centerFormat);
                    g.Restore(state);
                }
                if (s.HasY2 && !string.IsNullOrWhiteSpace(s.Y2Title))
                {
                    GraphicsState state2 = g.Save();
                    g.TranslateTransform(width - yTitleFont.GetHeight(g) - 2f * scale, plot.Top + plot.Height / 2f);
                    g.RotateTransform(-90f);
                    g.DrawString(s.Y2Title, yTitleFont, textBrush, new RectangleF(-plot.Height / 2f, 0, plot.Height, yTitleFont.GetHeight(g) + 2f), centerFormat);
                    g.Restore(state2);
                }

                if (s.LegendVisible) DrawLegend(g, series, plot, width, height, legendFont, s, scale);
            }
            return bitmap;
        }

        private static void DrawLegend(Graphics g, IList<SeriesData> series, RectangleF plot, int width, int height, Font font, ChartSettings s, float scale)
        {
            int columns;
            if (s.LegendRows) columns = Math.Min(series.Count, s.LegendColumns);
            else if (s.LegendLocation == 5 || s.LegendLocation == 6 || s.LegendLocation == 7 || s.LegendLocation == 10 || s.LegendLocation == 11)
                columns = series.Count;
            else columns = 1;
            columns = Math.Max(1, columns);
            int rows = (int)Math.Ceiling(series.Count / (double)columns);

            float markerSpace = 18f * scale;
            float pad = 5f * scale;
            float rowHeight = font.GetHeight(g) + 5f * scale;
            float cellWidth = series.Max(item => g.MeasureString(item.Label, font).Width) + markerSpace + 8f * scale;
            float legendWidth = Math.Min(width - 4f * pad, columns * cellWidth + 2f * pad);
            float legendHeight = rows * rowHeight + 2f * pad;
            float distance = (float)s.LegendDistance * Math.Min(plot.Width, plot.Height);

            float x, y;
            switch (s.LegendLocation)
            {
                case 1: x = plot.Left + pad; y = plot.Top + pad; break;
                case 3: x = plot.Left + pad; y = plot.Bottom - legendHeight - pad; break;
                case 4: x = plot.Right - legendWidth - pad; y = plot.Bottom - legendHeight - pad; break;
                case 5: x = plot.Left + (plot.Width - legendWidth) / 2f; y = plot.Bottom - legendHeight - pad; break;
                case 6: x = plot.Left + (plot.Width - legendWidth) / 2f; y = plot.Top + pad; break;
                case 7:
                case 11: x = plot.Left + (plot.Width - legendWidth) / 2f; y = plot.Bottom + distance + 2f * scale; break;
                case 8: x = plot.Right + distance + 2f * scale; y = plot.Top + (plot.Height - legendHeight) / 2f; break;
                case 9: x = Math.Max(2f * scale, plot.Left - legendWidth - distance); y = plot.Top + (plot.Height - legendHeight) / 2f; break;
                case 10: x = plot.Left + (plot.Width - legendWidth) / 2f; y = Math.Max(2f * scale, plot.Top - legendHeight - distance); break;
                case 0:
                case 2:
                default: x = plot.Right - legendWidth - pad; y = plot.Top + pad; break;
            }

            x = Math.Max(1f, Math.Min(width - legendWidth - 1f, x));
            y = Math.Max(1f, Math.Min(height - legendHeight - 1f, y));
            var box = new RectangleF(x, y, legendWidth, legendHeight);
            using var boxBrush = new SolidBrush(Color.FromArgb(225, 255, 255, 255));
            using var borderPen = new Pen(Color.FromArgb(110, 80, 80, 80), Math.Max(1f, 0.6f * scale));
            using var textBrush = new SolidBrush(Color.FromArgb(35, 35, 35));
            g.FillRectangle(boxBrush, box);
            g.DrawRectangle(borderPen, box.X, box.Y, box.Width, box.Height);

            for (int i = 0; i < series.Count; i++)
            {
                int row = i / columns;
                int column = i % columns;
                float itemX = box.Left + pad + column * cellWidth;
                float itemY = box.Top + pad + row * rowHeight;
                using var markerBrush = new SolidBrush(series[i].MarkerColor);
                using var linePen = new Pen(series[i].LineColor, (float)Math.Max(0.5f, series[i].LineWidth * scale));
                ApplyDashStyle(linePen, series[i].LineType);
                float d = Math.Max(4f * scale, (float)Math.Sqrt(series[i].MarkerArea) * scale);
                float cy = itemY + rowHeight / 2f;
                if (s.Mode == 1 || s.Mode == 2)
                    g.DrawLine(linePen, itemX, cy, itemX + markerSpace - 4f * scale, cy);
                if (s.Mode == 0 || s.Mode == 1)
                    DrawMarker(g, markerBrush, linePen, new PointF(itemX + d / 2f, cy), d, series[i].MarkerType);
                g.DrawString(series[i].Label, font, textBrush, itemX + markerSpace, itemY + 1f * scale);
            }
        }

        private static void ApplyDashStyle(Pen pen, int lineType)
        {
            switch (lineType)
            {
                case 1: pen.DashStyle = DashStyle.Dash; break;
                case 2: pen.DashStyle = DashStyle.Dot; break;
                case 3: pen.DashStyle = DashStyle.DashDot; break;
                default: pen.DashStyle = DashStyle.Solid; break;
            }
        }

        private static void DrawMarker(Graphics g, Brush brush, Pen pen, PointF center, float diameter, int markerType)
        {
            float r = diameter / 2f;
            switch (markerType)
            {
                case 1:
                    g.FillRectangle(brush, center.X - r, center.Y - r, diameter, diameter);
                    break;
                case 2:
                    using (var diamond = new GraphicsPath())
                    {
                        diamond.AddPolygon(new[]
                        {
                            new PointF(center.X, center.Y - r),
                            new PointF(center.X + r, center.Y),
                            new PointF(center.X, center.Y + r),
                            new PointF(center.X - r, center.Y)
                        });
                        g.FillPath(brush, diamond);
                    }
                    break;
                case 3:
                    using (var triangle = new GraphicsPath())
                    {
                        triangle.AddPolygon(new[]
                        {
                            new PointF(center.X, center.Y - r),
                            new PointF(center.X + r, center.Y + r),
                            new PointF(center.X - r, center.Y + r)
                        });
                        g.FillPath(brush, triangle);
                    }
                    break;
                case 4:
                    g.DrawLine(pen, center.X - r, center.Y - r, center.X + r, center.Y + r);
                    g.DrawLine(pen, center.X - r, center.Y + r, center.X + r, center.Y - r);
                    break;
                case 5:
                    g.DrawLine(pen, center.X - r, center.Y, center.X + r, center.Y);
                    g.DrawLine(pen, center.X, center.Y - r, center.X, center.Y + r);
                    break;
                default:
                    g.FillEllipse(brush, center.X - r, center.Y - r, diameter, diameter);
                    break;
            }
        }

        private static Font CreateFont(double size) => new Font(FontFamily.GenericSansSerif, (float)Math.Max(1.0, size), FontStyle.Regular, GraphicsUnit.Point);
        private static float MapX(double x, RectangleF plot, double min, double max) => plot.Left + (float)((x - min) / (max - min)) * plot.Width;
        private static float MapY(double y, RectangleF plot, double min, double max) => plot.Bottom - (float)((y - min) / (max - min)) * plot.Height;
        private static string FormatTick(double value, bool integer) => integer ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.00", CultureInfo.InvariantCulture);
        private static string FormatValue(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

        private static List<double> GenerateTicks(double min, double max, double interval)
        {
            var ticks = new List<double>();
            double start = Math.Ceiling(min / interval - 1e-10) * interval;
            for (double value = start; value <= max + interval * 1e-9 && ticks.Count < MaxTicksPerAxis; value += interval)
                ticks.Add(Math.Abs(value) < interval * 1e-10 ? 0.0 : value);
            return ticks;
        }

        private static bool ValidateTickCount(double min, double max, double interval, out int count)
        {
            count = (int)Math.Floor((max - min) / interval + 1.0000001);
            return count > 0 && count <= MaxTicksPerAxis;
        }

        private static double ResolveTickInterval(double requested, double span) =>
            IsFinite(requested) && requested > 0 ? requested : NiceStep(span / 8.0);

        private static double NiceStep(double raw)
        {
            if (!IsFinite(raw) || raw <= 0) return 1.0;
            double power = Math.Pow(10.0, Math.Floor(Math.Log10(raw)));
            double fraction = raw / power;
            double nice = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
            return nice * power;
        }

        private static bool ResolveRange(string text, double dataMin, double dataMax, out double min, out double max, out string error)
        {
            error = string.Empty;
            if (!string.IsNullOrWhiteSpace(text))
            {
                string[] parts = text.Split(';');
                if (parts.Length != 2 ||
                    !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out min) ||
                    !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out max) ||
                    !IsFinite(min) || !IsFinite(max) || max <= min)
                {
                    min = max = 0;
                    error = "expected two finite numbers with maximum greater than minimum, formatted as 'minimum;maximum'.";
                    return false;
                }
                return true;
            }

            min = dataMin;
            max = dataMax;
            double span = max - min;
            if (span <= 0 || !IsFinite(span))
            {
                double center = IsFinite(min) ? min : 0.0;
                double padding = Math.Max(1.0, Math.Abs(center) * 0.05);
                min = center - padding;
                max = center + padding;
            }
            else
            {
                double padding = span * 0.05;
                min -= padding;
                max += padding;
            }
            return true;
        }

        private static bool TryParseDimensions(string text, out double width, out double height)
        {
            width = height = 0;
            string[] parts = (text ?? string.Empty).Split(';');
            return parts.Length == 2 &&
                   double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out width) &&
                   double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out height) &&
                   IsFinite(width) && IsFinite(height) && width > 0 && height > 0;
        }

        private string ResolveOutputDirectory(string requested)
        {
            if (!string.IsNullOrWhiteSpace(requested)) return Path.GetFullPath(requested.Trim());
            return WasperChartSettingsTools.DefaultOutputDirectory(OnPingDocument()?.FilePath);
        }

        private static string ResolveExtension(string fileName)
        {
            string extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
            return extension == ".jpg" || extension == ".jpeg" || extension == ".png" ? extension : ".png";
        }

        private static string ResolveFileName(string requested, string title, string extension)
        {
            string stem = string.IsNullOrWhiteSpace(requested) ? title : Path.GetFileName(requested.Trim());
            string existingExtension = Path.GetExtension(stem);
            if (!string.IsNullOrWhiteSpace(existingExtension)) stem = Path.GetFileNameWithoutExtension(stem);
            if (string.IsNullOrWhiteSpace(stem)) stem = "Scatter_Plot";
            foreach (char invalid in Path.GetInvalidFileNameChars()) stem = stem.Replace(invalid, '_');
            stem = stem.Replace(' ', '_');
            return stem + extension;
        }

        private static double PositiveOrDefault(double value, double fallback) => IsFinite(value) && value > 0 ? value : fallback;
        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private void SetError(IGH_DataAccess DA, string message)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, message);
            Message = $"{_versionTag} | error";
            DA.SetData(0, null);
        }

        private sealed class SeriesData
        {
            public string Label = string.Empty;
            public Color MarkerColor = Color.Blue;
            public Color LineColor = Color.Blue;
            public double MarkerArea = 30.0;
            public double LineWidth = 1.2;
            public int MarkerType;
            public int LineType;
            public int AxisIndex = 1;
            public int AxisSeriesIndex;
            public bool HasRealX = true;
            public bool HasRealY = true;
            public readonly List<PointD> Points = new List<PointD>();
        }

        private readonly struct PointD
        {
            public PointD(double x, double y) { X = x; Y = y; }
            public double X { get; }
            public double Y { get; }
        }

        private sealed class ChartSettings
        {
            public string XTitle, YTitle, Title;
            public bool LegendVisible, LegendRows, XTicksInteger, YTicksInteger, Transparent;
            public bool ShowReferences;
            public int Mode, LegendLocation, LegendColumns, Dpi;
            public double LegendDistance, LegendTextSize, XTitleSize, YTitleSize, XTextSize, YTextSize, TitleSize;
            public double XMin, XMax, YMin, YMax, XTick, YTick;
            public int HighlightIndex = -1;
            public double TitleOffset;

            // ── secondary (right-hand) Y axis; HasY2 is true only when axis-2 series exist ──
            public bool HasY2;
            public string Y2Title;
            public double Y2Min, Y2Max, Y2Tick;
            public bool Y2TicksInteger;
        }
    }
}
