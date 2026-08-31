#region Component Description
/*
    Component Name:
        wsp_Da03_Box Plot

    Nickname:
        Box Plot

    Category / Subcategory:
        WASPerformance / 1.1_Data Vis

    Description:
        Creates native .NET box-and-whisker plots from Grasshopper data trees.
        Each input branch becomes one box. The component computes quartiles,
        whiskers, outliers, and emits the statistics alongside the image.

    Inputs:
        chart_rect  : optional planar rectangle for Rhino viewport preview
        values      : number tree; each branch is one box/category and items are samples
        labels      : optional box/category labels in branch order
        mode        : 0 Tukey whiskers (1.5 IQR), 1 min/max whiskers
        orientation : 0 vertical boxes, 1 horizontal boxes
        box_colors  : optional native colours in box order
        marker_p    : optional Da09 Marker + Line Params for outlier markers and strokes
        axis_p      : optional Da10 Chart Axis Params
        legend_p    : optional Da11 Chart Legend Params
        layout_p    : optional Da12 Chart Layout Params
        refresh     : inert trigger for recomputing

    Outputs:
        box_plot : absolute path to the generated PNG or JPEG
        stats    : tree {box} -> [min, q1, median, q3, max, lower_whisker, upper_whisker]
        outliers : tree {box} -> sample values outside Tukey whiskers; empty for min/max mode
*/
#endregion

#region Usings
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
#endregion

namespace WASPer_3DP.Components._1_1_Data_Vis
{
    public sealed class wsp_Da03_Box_Plot : GH_Component
    {
        private static readonly Color[] Palette =
        {
            Color.FromArgb(31, 119, 180), Color.FromArgb(255, 127, 14),
            Color.FromArgb(44, 160, 44), Color.FromArgb(214, 39, 40),
            Color.FromArgb(148, 103, 189), Color.FromArgb(140, 86, 75),
            Color.FromArgb(227, 119, 194), Color.FromArgb(127, 127, 127)
        };

        private readonly string _version;
        private Mesh _mesh;
        private DisplayMaterial _material;

        public wsp_Da03_Box_Plot()
            : base(
                "wsp_Da03_Box Plot", "Box Plot",
                "Creates native box-and-whisker plots from value-tree branches and outputs the computed statistics.", global::WASPer_3DP.WASPerPalette.Performance, "1.1_Data Vis")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _version = v == null ? "v1.0.5" : $"v{v.Major}.{v.Minor}.{v.Build}";
            Message = _version;
        }

        public override Guid ComponentGuid => new Guid("5B2F4F33-13C9-4F4E-A4E2-8F522D420C9B");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Da03_Box Plot.png");
                    return s == null ? null : new Bitmap(s);
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddRectangleParameter("chart_rect", "chart_rect", "Optional planar rectangle for an aspect-preserving chart preview.", GH_ParamAccess.item);
            p.AddNumberParameter("values", "values", "Number tree for the plot. Each branch becomes one box/category; items are samples used to compute quartiles and whiskers.", GH_ParamAccess.tree);
            p.AddTextParameter("labels", "labels", "Optional box/category labels in branch order. Missing labels become 1, 2, 3...", GH_ParamAccess.list);
            p.AddIntegerParameter("mode", "mode", "Whisker mode: 0 = Tukey 1.5 IQR whiskers with outliers; 1 = min/max whiskers with no outliers. Default 0.", GH_ParamAccess.item, 0);
            p.AddIntegerParameter("orientation", "orient", "Box orientation: 0 = vertical boxes; 1 = horizontal boxes. Default 0.", GH_ParamAccess.item, 0);
            p.AddColourParameter("box_colors", "colors", "Optional colours in box order. If fewer colours than boxes are supplied, colours are interpolated across all boxes; one colour is repeated and an empty list uses the default palette.", GH_ParamAccess.list);
            p[5].DataMapping = GH_DataMapping.Flatten;
            p.AddGenericParameter("marker_params", "marker_p", "Optional Da09 Marker + Line Params. Uses marker style for outliers and line style for box/whisker strokes.", GH_ParamAccess.item);
            p.AddGenericParameter("axis_params", "axis_p", "Optional Da10 Chart Axis Params. Vertical plots use Y range/ticks/title for values; horizontal plots use X range/ticks/title for values.", GH_ParamAccess.item);
            p.AddGenericParameter("legend_params", "legend_p", "Optional Da11 Chart Legend Params. Reserved for future grouped box plots; currently accepted for interface consistency.", GH_ParamAccess.item);
            p.AddGenericParameter("layout_params", "layout_p", "Optional Da12 Chart Layout Params. Disconnected uses 160;100 mm, 150 DPI, untitled white PNG.", GH_ParamAccess.item);
            p.AddBooleanParameter("refresh", "refresh", "Inert Button/Timer trigger for regenerating and overwriting the current output file.", GH_ParamAccess.item, false);
            for (int i = 0; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter("box_plot", "box_plot", "Absolute path to the generated PNG or JPEG box plot.", GH_ParamAccess.item);
            p.AddNumberParameter("stats", "stats", "Per-box statistics tree. Branch {box}: [0] min, [1] q1, [2] median, [3] q3, [4] max, [5] lower_whisker, [6] upper_whisker.", GH_ParamAccess.tree);
            p.AddNumberParameter("outliers", "outliers", "Per-box outlier values. Branch {box}; empty branches mean no outliers or min/max mode.", GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            ResetPreview();

            Rectangle3d rect = Rectangle3d.Unset;
            bool hasRect = da.GetData(0, ref rect) && rect.IsValid;

            GH_Structure<GH_Number> tree;
            if (!da.GetDataTree(1, out tree) || tree == null || tree.DataCount == 0)
            {
                Error(da, "Supply at least one values branch.");
                return;
            }

            var labels = new List<string>();
            var colors = new List<Color>();
            int mode = 0, orientation = 0;
            object rawMarker = null, rawAxis = null, rawLegend = null, rawLayout = null;
            bool refresh = false;

            da.GetDataList(2, labels);
            da.GetData(3, ref mode);
            da.GetData(4, ref orientation);
            da.GetDataList(5, colors);
            da.GetData(6, ref rawMarker);
            da.GetData(7, ref rawAxis);
            da.GetData(8, ref rawLegend);
            da.GetData(9, ref rawLayout);
            da.GetData(10, ref refresh);

            mode = Math.Max(0, Math.Min(1, mode));
            orientation = Math.Max(0, Math.Min(1, orientation));
            colors = WasperChartSettingsTools.ResolveSeriesColors(colors.Select(c => c.ToArgb()).ToList(), tree.PathCount, Palette);
            var marker = WasperChartSettingsTools.MarkerLine(rawMarker) ?? new WasperChartMarkerLineSettings();
            var axis = WasperChartSettingsTools.Axis(rawAxis) ?? new WasperChartAxisSettings();
            var layout = WasperChartSettingsTools.Layout(rawLayout) ?? new WasperChartLayoutSettings();

            List<BoxStats> boxes = BuildBoxes(tree, mode);
            if (boxes.Count == 0)
            {
                Error(da, "No finite sample values were found.");
                return;
            }
            while (labels.Count < boxes.Count)
                labels.Add((labels.Count + 1).ToString(CultureInfo.InvariantCulture));

            string rangeText = orientation == 0 ? axis.YRange : axis.XRange;
            double dataMin = boxes.Min(b => mode == 0 ? Math.Min(b.LowerWhisker, b.Outliers.DefaultIfEmpty(b.LowerWhisker).Min()) : b.Min);
            double dataMax = boxes.Max(b => mode == 0 ? Math.Max(b.UpperWhisker, b.Outliers.DefaultIfEmpty(b.UpperWhisker).Max()) : b.Max);
            if (!ResolveRange(rangeText, dataMin, dataMax, out double min, out double max))
            {
                Error(da, "Invalid value range; use 'minimum;maximum'.");
                return;
            }
            double tick = orientation == 0
                ? (axis.YTickInterval > 0 ? axis.YTickInterval : Nice((max - min) / 8))
                : (axis.XTickInterval > 0 ? axis.XTickInterval : Nice((max - min) / 8));

            if (!Dims(layout.Dimensions, out double mmW, out double mmH))
            {
                Error(da, "Layout dimensions must be 'width_mm;height_mm'.");
                return;
            }
            int dpi = Math.Max(36, Math.Min(1200, layout.Dpi));
            int pxW = Math.Max(64, (int)Math.Round(mmW / 25.4 * dpi));
            int pxH = Math.Max(64, (int)Math.Round(mmH / 25.4 * dpi));

            string ext = Extension(layout.FileName);
            string path;
            try
            {
                string dir = DirectoryPath(layout.FilePath);
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, FileName(layout.FileName, layout.Title, ext));
            }
            catch (Exception ex)
            {
                Error(da, "Could not prepare output path: " + ex.Message);
                return;
            }

            try
            {
                using Bitmap bmp = Render(pxW, pxH, dpi, boxes, labels, colors, marker, axis, layout, orientation, min, max, tick);
                bmp.SetResolution(dpi, dpi);
                if (ext == ".png")
                {
                    bmp.Save(path, ImageFormat.Png);
                }
                else
                {
                    using var flat = new Bitmap(pxW, pxH);
                    using var g = Graphics.FromImage(flat);
                    g.Clear(Color.White);
                    g.DrawImageUnscaled(bmp, 0, 0);
                    flat.Save(path, ImageFormat.Jpeg);
                }
            }
            catch (Exception ex)
            {
                Error(da, "Render failed: " + ex.Message);
                return;
            }

            if (!hasRect)
            {
                rect = new Rectangle3d(Plane.WorldXY, new Interval(-mmW * .5, mmW * .5), new Interval(-mmH * .5, mmH * .5));
                hasRect = true;
            }
            if (hasRect && !Preview(rect, pxW / (double)pxH, path))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Rectangle preview failed; image was saved.");

            SetStatsOutputs(da, boxes);
            Message = $"{_version} | {boxes.Count} boxes";
            da.SetData(0, path);
        }

        private static List<BoxStats> BuildBoxes(GH_Structure<GH_Number> tree, int mode)
        {
            var boxes = new List<BoxStats>();
            for (int i = 0; i < tree.PathCount; i++)
            {
                List<double> values = tree.Branches[i]
                    .Where(n => n != null && Finite(n.Value))
                    .Select(n => n.Value)
                    .OrderBy(v => v)
                    .ToList();
                if (values.Count == 0)
                    continue;

                double q1 = Percentile(values, 0.25);
                double med = Percentile(values, 0.50);
                double q3 = Percentile(values, 0.75);
                double min = values.First();
                double max = values.Last();
                double low = min, high = max;
                var outliers = new List<double>();
                if (mode == 0)
                {
                    double iqr = q3 - q1;
                    double fenceLow = q1 - 1.5 * iqr;
                    double fenceHigh = q3 + 1.5 * iqr;
                    low = values.First(v => v >= fenceLow);
                    high = values.Last(v => v <= fenceHigh);
                    outliers = values.Where(v => v < low || v > high).ToList();
                }

                boxes.Add(new BoxStats { Index = boxes.Count, Min = min, Q1 = q1, Median = med, Q3 = q3, Max = max, LowerWhisker = low, UpperWhisker = high, Outliers = outliers });
            }
            return boxes;
        }

        private static double Percentile(IList<double> sorted, double p)
        {
            if (sorted.Count == 1) return sorted[0];
            double pos = p * (sorted.Count - 1);
            int lo = (int)Math.Floor(pos);
            int hi = (int)Math.Ceiling(pos);
            double t = pos - lo;
            return sorted[lo] * (1 - t) + sorted[hi] * t;
        }

        private static void SetStatsOutputs(IGH_DataAccess da, IList<BoxStats> boxes)
        {
            var stats = new GH_Structure<GH_Number>();
            var outliers = new GH_Structure<GH_Number>();
            foreach (BoxStats b in boxes)
            {
                var path = new GH_Path(b.Index);
                stats.Append(new GH_Number(b.Min), path);
                stats.Append(new GH_Number(b.Q1), path);
                stats.Append(new GH_Number(b.Median), path);
                stats.Append(new GH_Number(b.Q3), path);
                stats.Append(new GH_Number(b.Max), path);
                stats.Append(new GH_Number(b.LowerWhisker), path);
                stats.Append(new GH_Number(b.UpperWhisker), path);
                foreach (double value in b.Outliers)
                    outliers.Append(new GH_Number(value), path);
                outliers.EnsurePath(path);
            }
            da.SetDataTree(1, stats);
            da.SetDataTree(2, outliers);
        }

        private static Bitmap Render(int w, int h, int dpi, IList<BoxStats> boxes, IList<string> labels, IList<Color> colors, WasperChartMarkerLineSettings marker,
            WasperChartAxisSettings axis, WasperChartLayoutSettings layout, int orientation, double min, double max, double tick)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(layout.TransparentBackground ? Color.Transparent : Color.White);

            float sc = dpi / 72f;
            using var titleFont = FontOf(layout.TitleSize);
            using var tickFont = FontOf(orientation == 0 ? axis.YTextSize : axis.XTextSize);
            using var catFont = FontOf(orientation == 0 ? axis.XTextSize : axis.YTextSize);
            using var axisFont = FontOf(orientation == 0 ? axis.YTitleSize : axis.XTitleSize);
            using var textBrush = new SolidBrush(Color.FromArgb(35, 35, 35));
            using var gridPen = new Pen(Color.FromArgb(215, 215, 215), Math.Max(1f, .6f * sc));
            using var edgePen = new Pen(Color.FromArgb(45, 45, 45), (float)Math.Max(1f, PickDouble(marker.LineWidths, 0, 1.2) * sc));
            ApplyDash(edgePen, PickInt(marker.LineTypes, 0, 0));

            float left = orientation == 0 ? 62f * sc : 88f * sc;
            float right = 16f * sc;
            float top = string.IsNullOrWhiteSpace(layout.Title) ? 14f * sc : titleFont.GetHeight(g) + 16f * sc;
            float bottom = orientation == 0 ? catFont.GetHeight(g) + axisFont.GetHeight(g) + 24f * sc : 24f * sc;
            if (orientation == 1) right += 10f * sc;
            var plot = RectangleF.FromLTRB(left, top, Math.Max(left + 10f, w - right), Math.Max(top + 10f, h - bottom));
            using (var bg = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                g.FillRectangle(bg, plot);

            bool integerTicks = orientation == 0 ? axis.YTicksInteger && tick >= 1 : axis.XTicksInteger && tick >= 1;
            using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var rightAlign = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };

            for (double v = Math.Ceiling(min / tick) * tick; v <= max + tick * 1e-8; v += tick)
            {
                if (orientation == 0)
                {
                    float y = MapY(v, plot, min, max);
                    if (layout.ShowReferences) g.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                    g.DrawString(FormatTick(v, integerTicks), tickFont, textBrush, new RectangleF(0, y - tickFont.GetHeight(g), plot.Left - 6 * sc, tickFont.GetHeight(g) * 2), rightAlign);
                }
                else
                {
                    float x = MapX(v, plot, min, max);
                    if (layout.ShowReferences) g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
                    g.DrawString(FormatTick(v, integerTicks), tickFont, textBrush, new RectangleF(x - 45 * sc, plot.Bottom + 4 * sc, 90 * sc, tickFont.GetHeight(g) * 1.5f), center);
                }
            }

            int n = boxes.Count;
            float slot = (orientation == 0 ? plot.Width : plot.Height) / n;
            float boxBreadth = slot * .45f;
            for (int i = 0; i < n; i++)
            {
                BoxStats b = boxes[i];
                Color color = i < colors.Count ? colors[i] : Palette[i % Palette.Length];
                using var fill = new SolidBrush(Color.FromArgb(170, color));
                using var outBrush = new SolidBrush(PickColor(marker.MarkerColorsArgb, i, color));
                float outD = Math.Max(3f * sc, (float)Math.Sqrt(PickDouble(marker.MarkerSizes, i, 18.0)) * sc);
                int markerType = PickInt(marker.MarkerTypes, i, 0);

                if (orientation == 0)
                    DrawVertical(g, b, plot, min, max, plot.Left + slot * (i + .5f), boxBreadth, fill, edgePen, outBrush, outD, markerType, sc);
                else
                    DrawHorizontal(g, b, plot, min, max, plot.Top + slot * (i + .5f), boxBreadth, fill, edgePen, outBrush, outD, markerType, sc);

                if (orientation == 0)
                    g.DrawString(labels[i], catFont, textBrush, new RectangleF(plot.Left + slot * i, plot.Bottom + 5 * sc, slot, catFont.GetHeight(g) * 2), center);
                else
                    g.DrawString(labels[i], catFont, textBrush, new RectangleF(0, plot.Top + slot * i, plot.Left - 6 * sc, slot), rightAlign);
            }

            g.DrawRectangle(edgePen, plot.X, plot.Y, plot.Width, plot.Height);
            if (!string.IsNullOrWhiteSpace(layout.Title))
            {
                using var titleFormat = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Near,
                };
                g.DrawString(layout.Title, titleFont, textBrush, new RectangleF(0, 3 * sc, w, titleFont.GetHeight(g) + 3 * sc), titleFormat);
            }

            string valueTitle = orientation == 0 ? axis.YTitle : axis.XTitle;
            if (!string.IsNullOrWhiteSpace(valueTitle))
            {
                if (orientation == 0)
                {
                    GraphicsState st = g.Save();
                    g.TranslateTransform(axisFont.GetHeight(g), plot.Top + plot.Height / 2);
                    g.RotateTransform(-90);
                    g.DrawString(valueTitle, axisFont, textBrush, new RectangleF(-plot.Height / 2, 0, plot.Height, axisFont.GetHeight(g)), center);
                    g.Restore(st);
                }
                else
                {
                    g.DrawString(valueTitle, axisFont, textBrush, new RectangleF(plot.Left, h - axisFont.GetHeight(g) - 3 * sc, plot.Width, axisFont.GetHeight(g)), new StringFormat { Alignment = StringAlignment.Center });
                }
            }

            return bmp;
        }

        private static void DrawVertical(Graphics g, BoxStats b, RectangleF plot, double min, double max, float cx, float bw, Brush fill, Pen pen, Brush outBrush, float outD, int markerType, float sc)
        {
            float q1 = MapY(b.Q1, plot, min, max), q3 = MapY(b.Q3, plot, min, max), med = MapY(b.Median, plot, min, max);
            float low = MapY(b.LowerWhisker, plot, min, max), high = MapY(b.UpperWhisker, plot, min, max);
            g.DrawLine(pen, cx, high, cx, q3);
            g.DrawLine(pen, cx, q1, cx, low);
            g.DrawLine(pen, cx - bw * .25f, high, cx + bw * .25f, high);
            g.DrawLine(pen, cx - bw * .25f, low, cx + bw * .25f, low);
            var box = RectangleF.FromLTRB(cx - bw / 2, q3, cx + bw / 2, q1);
            g.FillRectangle(fill, box);
            g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
            g.DrawLine(pen, cx - bw / 2, med, cx + bw / 2, med);
            foreach (double value in b.Outliers)
                DrawMarker(g, outBrush, pen, new PointF(cx, MapY(value, plot, min, max)), outD, markerType);
        }

        private static void DrawHorizontal(Graphics g, BoxStats b, RectangleF plot, double min, double max, float cy, float bh, Brush fill, Pen pen, Brush outBrush, float outD, int markerType, float sc)
        {
            float q1 = MapX(b.Q1, plot, min, max), q3 = MapX(b.Q3, plot, min, max), med = MapX(b.Median, plot, min, max);
            float low = MapX(b.LowerWhisker, plot, min, max), high = MapX(b.UpperWhisker, plot, min, max);
            g.DrawLine(pen, low, cy, q1, cy);
            g.DrawLine(pen, q3, cy, high, cy);
            g.DrawLine(pen, low, cy - bh * .25f, low, cy + bh * .25f);
            g.DrawLine(pen, high, cy - bh * .25f, high, cy + bh * .25f);
            var box = RectangleF.FromLTRB(q1, cy - bh / 2, q3, cy + bh / 2);
            g.FillRectangle(fill, box);
            g.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
            g.DrawLine(pen, med, cy - bh / 2, med, cy + bh / 2);
            foreach (double value in b.Outliers)
                DrawMarker(g, outBrush, pen, new PointF(MapX(value, plot, min, max), cy), outD, markerType);
        }

        private bool Preview(Rectangle3d r, double aspect, string path)
        {
            double aw = r.Width, ah = r.Height, w = aw, h = w / aspect;
            if (h < ah) { h = ah; w = h * aspect; }
            Plane p = r.Plane;
            p.Origin = r.Center;
            var q = new Rectangle3d(p, new Interval(-w / 2, w / 2), new Interval(-h / 2, h / 2));
            var m = new Mesh();
            for (int i = 0; i < 4; i++) m.Vertices.Add(q.Corner(i));
            m.Faces.AddFace(0, 1, 2, 3);
            m.TextureCoordinates.Add(0, 0);
            m.TextureCoordinates.Add(1, 0);
            m.TextureCoordinates.Add(1, 1);
            m.TextureCoordinates.Add(0, 1);
            m.Normals.ComputeNormals();
            var mat = new DisplayMaterial(Color.White);
            if (!mat.SetBitmapTexture(path, true))
            {
                m.Dispose();
                mat.Dispose();
                return false;
            }
            _mesh = m;
            _material = mat;
            return true;
        }

        public override BoundingBox ClippingBox => _mesh?.GetBoundingBox(false) ?? BoundingBox.Empty;
        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            base.DrawViewportMeshes(args);
            if (_mesh != null && _material != null) args.Display.DrawMeshShaded(_mesh, _material);
        }

        private void ResetPreview()
        {
            _mesh?.Dispose();
            _mesh = null;
            _material?.Dispose();
            _material = null;
        }

        private bool ResolveRange(string text, double dataMin, double dataMax, out double min, out double max)
        {
            min = dataMin;
            max = dataMax;
            if (!string.IsNullOrWhiteSpace(text))
            {
                string[] parts = text.Split(';');
                return parts.Length == 2
                    && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out min)
                    && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out max)
                    && Finite(min) && Finite(max) && max > min;
            }
            if (max <= min || !Finite(max - min))
            {
                double center = Finite(min) ? min : 0;
                double pad0 = Math.Max(1.0, Math.Abs(center) * .05);
                min = center - pad0;
                max = center + pad0;
                return true;
            }
            double pad = (max - min) * .05;
            min -= pad;
            max += pad;
            return true;
        }

        private string DirectoryPath(string p)
        {
            if (!string.IsNullOrWhiteSpace(p)) return Path.GetFullPath(p);
            return WasperChartSettingsTools.DefaultOutputDirectory(OnPingDocument()?.FilePath);
        }

        private static string Extension(string name)
        {
            string e = Path.GetExtension(name ?? string.Empty).ToLowerInvariant();
            return e == ".jpg" || e == ".jpeg" ? e : ".png";
        }

        private static string FileName(string name, string title, string ext)
        {
            string stem = string.IsNullOrWhiteSpace(name) ? title : Path.GetFileNameWithoutExtension(name);
            if (string.IsNullOrWhiteSpace(stem)) stem = "Box_Plot";
            foreach (char c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
            return stem.Replace(' ', '_') + ext;
        }

        private static bool Dims(string text, out double width, out double height)
        {
            width = height = 0;
            string[] parts = (text ?? string.Empty).Split(';');
            return parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out width)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out height)
                && width > 0 && height > 0;
        }

        private static double Nice(double raw)
        {
            if (!Finite(raw) || raw <= 0) return 1;
            double p = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double f = raw / p;
            return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * p;
        }

        private static Font FontOf(double size) => new Font(FontFamily.GenericSansSerif, (float)Math.Max(1, size), GraphicsUnit.Point);
        private static float MapX(double x, RectangleF plot, double min, double max) => plot.Left + (float)((x - min) / (max - min)) * plot.Width;
        private static float MapY(double y, RectangleF plot, double min, double max) => plot.Bottom - (float)((y - min) / (max - min)) * plot.Height;
        private static string FormatTick(double v, bool integer) => integer ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.00", CultureInfo.InvariantCulture);
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static Color PickColor(IList<int> argbValues, int index, Color fallback) =>
            argbValues != null && argbValues.Count > 0 ? WasperChartMarkerLineSettings.FromArgb(argbValues[Math.Min(index, argbValues.Count - 1)]) : fallback;
        private static double PickDouble(IList<double> values, int index, double fallback) =>
            values != null && values.Count > 0 && Finite(values[Math.Min(index, values.Count - 1)]) ? values[Math.Min(index, values.Count - 1)] : fallback;
        private static int PickInt(IList<int> values, int index, int fallback) =>
            values != null && values.Count > 0 ? values[Math.Min(index, values.Count - 1)] : fallback;

        private static void ApplyDash(Pen pen, int lineType)
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
                        diamond.AddPolygon(new[] { new PointF(center.X, center.Y - r), new PointF(center.X + r, center.Y), new PointF(center.X, center.Y + r), new PointF(center.X - r, center.Y) });
                        g.FillPath(brush, diamond);
                    }
                    break;
                case 3:
                    using (var tri = new GraphicsPath())
                    {
                        tri.AddPolygon(new[] { new PointF(center.X, center.Y - r), new PointF(center.X + r, center.Y + r), new PointF(center.X - r, center.Y + r) });
                        g.FillPath(brush, tri);
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

        private void Error(IGH_DataAccess da, string message)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, message);
            Message = $"{_version} | error";
            da.SetData(0, null);
        }

        private sealed class BoxStats
        {
            public int Index;
            public double Min, Q1, Median, Q3, Max, LowerWhisker, UpperWhisker;
            public List<double> Outliers = new List<double>();
        }
    }
}
