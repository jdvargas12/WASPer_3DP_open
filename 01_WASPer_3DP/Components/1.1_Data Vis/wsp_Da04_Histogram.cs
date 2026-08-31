#region Component Description
/*
    Component Name: wsp_Da04_Histogram
    Nickname: Histogram
    Category / Subcategory: WASPerformance / 1.1_Data Vis

    Description:
        Creates a native histogram from one or more value branches. Each branch is a dataset.
        Bins are shared across all datasets so distributions can be compared directly.

    Inputs:
        chart_rect  : optional planar rectangle for Rhino viewport preview
        values      : value tree; each branch is one dataset
        bins        : number of bins; 0 or less uses an automatic square-root rule
        normalized  : true outputs relative frequencies instead of counts
        orientation : 0 vertical bars/regions, 1 horizontal bars/regions
        type        : 0 bars, 1 region
        colors      : optional colours per dataset
        axis_p      : optional Da10 Chart Axis Params
        legend_p    : optional Da11 Chart Legend Params
        layout_p    : optional Da12 Chart Layout Params
        refresh     : inert recompute trigger

    Outputs:
        histogram   : absolute path to the generated PNG or JPEG
        counts      : tree {dataset} of bin counts/frequencies
        bin_edges   : list of bin edge values, length bins + 1
        bin_centers : list of bin center values, length bins
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
    public sealed class wsp_Da04_Histogram : GH_Component
    {
        private static readonly Color[] Palette =
        {
            Color.FromArgb(31, 119, 180),
            Color.FromArgb(255, 127, 14),
            Color.FromArgb(44, 160, 44),
            Color.FromArgb(214, 39, 40),
            Color.FromArgb(148, 103, 189),
            Color.FromArgb(140, 86, 75),
        };

        private readonly string _version;
        private Mesh _mesh;
        private DisplayMaterial _material;

        public wsp_Da04_Histogram()
            : base(
                "wsp_Da04_Histogram",
                "Histogram",
                "Creates native histogram charts and outputs bin counts/edges.",
                global::WASPer_3DP.WASPerPalette.Performance,
                "1.1_Data Vis"
            )
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _version = v == null ? "v1.0.5" : $"v{v.Major}.{v.Minor}.{v.Build}";
            Message = _version;
        }

        public override Guid ComponentGuid => new Guid("661E90BC-8712-4C3A-94FE-DAA78B5C94F9");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    using var s = Assembly
                        .GetExecutingAssembly()
                        .GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Da04_Histogram.png");
                    return s == null ? null : new Bitmap(s);
                }
                catch
                {
                    return null;
                }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddRectangleParameter(
                "chart_rect",
                "chart_rect",
                "Optional planar rectangle for an aspect-preserving chart preview.",
                GH_ParamAccess.item
            );
            p.AddNumberParameter("values", "values", "Value tree. Each branch is one histogram dataset.", GH_ParamAccess.tree);
            p.AddIntegerParameter(
                "bins",
                "bins",
                "Number of bins. 0 or less uses an automatic square-root rule. Default 0.",
                GH_ParamAccess.item,
                0
            );
            p.AddBooleanParameter(
                "normalized",
                "norm",
                "True outputs relative frequencies per dataset instead of raw counts. Default false.",
                GH_ParamAccess.item,
                false
            );
            p.AddIntegerParameter(
                "orientation",
                "orient",
                "Histogram orientation: 0 = vertical bars/regions, 1 = horizontal bars/regions. Default 0.",
                GH_ParamAccess.item,
                0
            );
            p.AddIntegerParameter(
                "type",
                "type",
                "Histogram display type: 0 = bars, 1 = region. Region draws a filled count/frequency curve over bin centers. Default 0.",
                GH_ParamAccess.item,
                0
            );
            p.AddColourParameter("colors", "colors", "Optional colours in dataset order. If fewer colours than datasets are supplied, colours are interpolated across all datasets; one colour is repeated and an empty list uses the default palette.", GH_ParamAccess.list);
            p[6].DataMapping = GH_DataMapping.Flatten;
            p.AddGenericParameter(
                "axis_params",
                "axis_p",
                "Optional Da10 Chart Axis Params. X range controls data domain; Y range controls count/frequency range.",
                GH_ParamAccess.item
            );
            p.AddGenericParameter(
                "legend_params",
                "legend_p",
                "Optional Da11 Chart Legend Params. Labels name datasets; disconnected hides legend.",
                GH_ParamAccess.item
            );
            p.AddGenericParameter(
                "layout_params",
                "layout_p",
                "Optional Da12 Chart Layout Params. Disconnected uses 160;100 mm, 150 DPI, untitled white PNG.",
                GH_ParamAccess.item
            );
            p.AddBooleanParameter(
                "refresh",
                "refresh",
                "Inert Button/Timer trigger for regenerating the file.",
                GH_ParamAccess.item,
                false
            );

            for (int i = 0; i < p.ParamCount; i++)
            {
                p[i].Optional = true;
            }
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter(
                "histogram",
                "histogram",
                "Absolute path to the generated PNG or JPEG histogram.",
                GH_ParamAccess.item
            );
            p.AddNumberParameter(
                "counts",
                "counts",
                "Bin counts or normalized frequencies. Branch {dataset}; items follow bin order.",
                GH_ParamAccess.tree
            );
            p.AddNumberParameter("bin_edges", "edges", "Bin edge values. Length is bins + 1.", GH_ParamAccess.list);
            p.AddNumberParameter("bin_centers", "centers", "Bin center values. Length is bins.", GH_ParamAccess.list);
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

            int bins = 0,
                orientation = 0,
                chartType = 0;
            bool normalized = false,
                refresh = false;
            var colors = new List<Color>();
            object rawAxis = null,
                rawLegend = null,
                rawLayout = null;
            da.GetData(2, ref bins);
            da.GetData(3, ref normalized);
            da.GetData(4, ref orientation);
            da.GetData(5, ref chartType);
            da.GetDataList(6, colors);
            da.GetData(7, ref rawAxis);
            da.GetData(8, ref rawLegend);
            da.GetData(9, ref rawLayout);
            da.GetData(10, ref refresh);
            orientation = Math.Max(0, Math.Min(1, orientation));
            chartType = Math.Max(0, Math.Min(1, chartType));
            colors = WasperChartSettingsTools.ResolveSeriesColors(colors.Select(c => c.ToArgb()).ToList(), tree.PathCount, Palette);

            var axis = WasperChartSettingsTools.Axis(rawAxis) ?? new WasperChartAxisSettings();
            var legend = WasperChartSettingsTools.Legend(rawLegend) ?? new WasperChartLegendSettings();
            var layout = WasperChartSettingsTools.Layout(rawLayout) ?? new WasperChartLayoutSettings();

            List<List<double>> datasets = new List<List<double>>();
            foreach (var branch in tree.Branches)
            {
                var values = branch.Where(n => n != null && Finite(n.Value)).Select(n => n.Value).ToList();
                if (values.Count > 0)
                {
                    datasets.Add(values);
                }
            }
            if (datasets.Count == 0)
            {
                Error(da, "No finite values were found.");
                return;
            }

            List<double> all = datasets.SelectMany(v => v).ToList();
            if (!ResolveRange(axis.XRange, all.Min(), all.Max(), out double minX, out double maxX))
            {
                Error(da, "Invalid X range; use 'minimum;maximum'.");
                return;
            }
            bins = bins > 0 ? bins : Math.Max(3, (int)Math.Ceiling(Math.Sqrt(all.Count)));
            bins = Math.Max(1, Math.Min(500, bins));
            double binWidth = (maxX - minX) / bins;
            double[] edges = Enumerable.Range(0, bins + 1).Select(i => minX + i * binWidth).ToArray();
            double[] centers = Enumerable.Range(0, bins).Select(i => (edges[i] + edges[i + 1]) / 2.0).ToArray();

            double[,] counts = new double[datasets.Count, bins];
            for (int s = 0; s < datasets.Count; s++)
            {
                foreach (double v in datasets[s])
                {
                    if (v < minX || v > maxX)
                        continue;
                    int b = v == maxX ? bins - 1 : (int)Math.Floor((v - minX) / binWidth);
                    if (b >= 0 && b < bins)
                    {
                        counts[s, b]++;
                    }
                }
                if (normalized && datasets[s].Count > 0)
                {
                    for (int b = 0; b < bins; b++)
                    {
                        counts[s, b] /= datasets[s].Count;
                    }
                }
            }

            double maxY = counts.Cast<double>().DefaultIfEmpty(1).Max();
            if (!ResolveRange(axis.YRange, 0, maxY, out double minY, out maxY))
            {
                Error(da, "Invalid Y range; use 'minimum;maximum'.");
                return;
            }
            double yTick = axis.YTickInterval > 0 ? axis.YTickInterval : Nice((maxY - minY) / 8);

            if (!Dims(layout.Dimensions, out double mmW, out double mmH))
            {
                Error(da, "Layout dimensions must be 'width_mm;height_mm'.");
                return;
            }

            int dpi = Math.Max(36, Math.Min(1200, layout.Dpi));
            int pxW = Math.Max(64, (int)Math.Round(mmW / 25.4 * dpi));
            int pxH = Math.Max(64, (int)Math.Round(mmH / 25.4 * dpi));
            string ext = Extension(layout.FileName),
                path;
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
                using Bitmap bmp = Render(pxW, pxH, dpi, counts, edges, colors, axis, legend, layout, minY, maxY, yTick, orientation, chartType);
                bmp.SetResolution(dpi, dpi);
                SaveBitmap(bmp, path, ext);
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
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Rectangle preview failed; image was saved.");
            }
            SetOutputs(da, path, counts, edges, centers);
            Message = $"{_version} | {datasets.Count} sets";
        }

        private static Bitmap Render(
            int w,
            int h,
            int dpi,
            double[,] counts,
            double[] edges,
            IList<Color> colors,
            WasperChartAxisSettings axis,
            WasperChartLegendSettings legend,
            WasperChartLayoutSettings layout,
            double minY,
            double maxY,
            double yTick,
            int orientation,
            int chartType
        )
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(layout.TransparentBackground ? Color.Transparent : Color.White);
            float sc = dpi / 72f;
            using var titleFont = FontOf(layout.TitleSize);
            using var tickFont = FontOf(axis.XTextSize);
            using var axisFont = FontOf(axis.XTitleSize);
            using var textBrush = new SolidBrush(Color.FromArgb(35, 35, 35));
            using var grid = new Pen(Color.FromArgb(215, 215, 215), Math.Max(1f, .6f * sc));
            using var edgePen = new Pen(Color.FromArgb(50, 50, 50), Math.Max(1f, .7f * sc));
            float left = orientation == 0 ? 60 * sc : 80 * sc,
                right = 18 * sc;
            float top = string.IsNullOrWhiteSpace(layout.Title) ? 14 * sc : titleFont.GetHeight(g) + 16 * sc;
            float bottom = tickFont.GetHeight(g) + axisFont.GetHeight(g) + 24 * sc;
            var plot = RectangleF.FromLTRB(left, top, w - right, h - bottom);
            using (var bg = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
            {
                g.FillRectangle(bg, plot);
            }

            int ns = counts.GetLength(0),
                nb = counts.GetLength(1);
            using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var rightAlign = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            bool ints = axis.YTicksInteger && yTick >= 1;
            for (double v = Math.Ceiling(minY / yTick) * yTick; v <= maxY + yTick * 1e-8; v += yTick)
            {
                if (orientation == 0)
                {
                    float y = MapY(v, plot, minY, maxY);
                    if (layout.ShowReferences) g.DrawLine(grid, plot.Left, y, plot.Right, y);
                    g.DrawString(
                        FormatTick(v, ints),
                        tickFont,
                        textBrush,
                        new RectangleF(0, y - tickFont.GetHeight(g), plot.Left - 6 * sc, tickFont.GetHeight(g) * 2),
                        rightAlign
                    );
                }
                else
                {
                    float x = MapX(v, plot, minY, maxY);
                    if (layout.ShowReferences) g.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                    g.DrawString(
                        FormatTick(v, ints),
                        tickFont,
                        textBrush,
                        new RectangleF(x - 45 * sc, plot.Bottom + 4 * sc, 90 * sc, tickFont.GetHeight(g) * 1.5f),
                        center
                    );
                }
            }

            float slot = (orientation == 0 ? plot.Width : plot.Height) / nb;
            if (chartType == 1)
            {
                DrawRegions(g, counts, plot, colors, minY, maxY, orientation, slot, sc);
            }
            else
            {
                for (int b = 0; b < nb; b++)
                {
                    for (int s = 0; s < ns; s++)
                    {
                        Color c = s < colors.Count ? colors[s] : Palette[s % Palette.Length];
                        using var brush = new SolidBrush(Color.FromArgb(ns == 1 ? 210 : 145, c));
                        if (orientation == 0)
                        {
                            float bw = slot * .85f / ns;
                            float x = plot.Left + b * slot + slot * .075f + s * bw;
                            float y = MapY(counts[s, b], plot, minY, maxY);
                            var r = RectangleF.FromLTRB(x, y, x + bw, plot.Bottom);
                            g.FillRectangle(brush, r);
                            g.DrawRectangle(edgePen, r.X, r.Y, r.Width, r.Height);
                        }
                        else
                        {
                            float bh = slot * .85f / ns;
                            float y = plot.Top + b * slot + slot * .075f + s * bh;
                            float x = MapX(counts[s, b], plot, minY, maxY);
                            var r = RectangleF.FromLTRB(plot.Left, y, x, y + bh);
                            g.FillRectangle(brush, r);
                            g.DrawRectangle(edgePen, r.X, r.Y, r.Width, r.Height);
                        }
                    }
                }
            }
            g.DrawRectangle(edgePen, plot.X, plot.Y, plot.Width, plot.Height);
            if (!string.IsNullOrWhiteSpace(layout.Title))
            {
                g.DrawString(
                    layout.Title,
                    titleFont,
                    textBrush,
                    new RectangleF(0, 3 * sc, w, titleFont.GetHeight(g) + 4 * sc),
                    new StringFormat { Alignment = StringAlignment.Center }
                );
            }
            string xTitle = orientation == 0 ? axis.XTitle : axis.YTitle;
            if (!string.IsNullOrWhiteSpace(xTitle))
            {
                g.DrawString(
                    xTitle,
                    axisFont,
                    textBrush,
                    new RectangleF(plot.Left, h - axisFont.GetHeight(g) - 3 * sc, plot.Width, axisFont.GetHeight(g)),
                    new StringFormat { Alignment = StringAlignment.Center }
                );
            }
            return bmp;
        }

        private static void DrawRegions(
            Graphics g,
            double[,] counts,
            RectangleF plot,
            IList<Color> colors,
            double minY,
            double maxY,
            int orientation,
            float slot,
            float scale
        )
        {
            int ns = counts.GetLength(0);
            int nb = counts.GetLength(1);
            double baseValue = minY <= 0 && maxY >= 0 ? 0 : minY;
            for (int s = 0; s < ns; s++)
            {
                Color color = s < colors.Count ? colors[s] : Palette[s % Palette.Length];
                using var fill = new SolidBrush(Color.FromArgb(85, color));
                using var line = new Pen(color, Math.Max(1.2f, 1.4f * scale));
                using var path = new GraphicsPath();
                if (orientation == 0)
                {
                    float baseY = MapY(baseValue, plot, minY, maxY);
                    path.StartFigure();
                    path.AddLine(plot.Left + slot * .5f, baseY, plot.Left + slot * .5f, MapY(counts[s, 0], plot, minY, maxY));
                    for (int b = 1; b < nb; b++)
                    {
                        path.AddLine(
                            plot.Left + slot * (b - .5f),
                            MapY(counts[s, b - 1], plot, minY, maxY),
                            plot.Left + slot * (b + .5f),
                            MapY(counts[s, b], plot, minY, maxY)
                        );
                    }
                    path.AddLine(plot.Left + slot * (nb - .5f), MapY(counts[s, nb - 1], plot, minY, maxY), plot.Left + slot * (nb - .5f), baseY);
                    path.CloseFigure();
                }
                else
                {
                    float baseX = MapX(baseValue, plot, minY, maxY);
                    path.StartFigure();
                    path.AddLine(baseX, plot.Top + slot * .5f, MapX(counts[s, 0], plot, minY, maxY), plot.Top + slot * .5f);
                    for (int b = 1; b < nb; b++)
                    {
                        path.AddLine(
                            MapX(counts[s, b - 1], plot, minY, maxY),
                            plot.Top + slot * (b - .5f),
                            MapX(counts[s, b], plot, minY, maxY),
                            plot.Top + slot * (b + .5f)
                        );
                    }
                    path.AddLine(MapX(counts[s, nb - 1], plot, minY, maxY), plot.Top + slot * (nb - .5f), baseX, plot.Top + slot * (nb - .5f));
                    path.CloseFigure();
                }
                g.FillPath(fill, path);
                g.DrawPath(line, path);
            }
        }

        private void SetOutputs(IGH_DataAccess da, string path, double[,] counts, double[] edges, double[] centers)
        {
            da.SetData(0, path);
            var tree = new GH_Structure<GH_Number>();
            for (int s = 0; s < counts.GetLength(0); s++)
            {
                var p = new GH_Path(s);
                for (int b = 0; b < counts.GetLength(1); b++)
                {
                    tree.Append(new GH_Number(counts[s, b]), p);
                }
            }
            da.SetDataTree(1, tree);
            da.SetDataList(2, edges);
            da.SetDataList(3, centers);
        }

        private bool Preview(Rectangle3d r, double aspect, string path)
        {
            double aw = r.Width,
                ah = r.Height,
                rw = aw,
                rh = rw / aspect;
            if (rh < ah)
            {
                rh = ah;
                rw = rh * aspect;
            }

            Plane p = r.Plane;
            p.Origin = r.Center;
            var q = new Rectangle3d(p, new Interval(-rw / 2, rw / 2), new Interval(-rh / 2, rh / 2));

            var m = new Mesh();
            for (int i = 0; i < 4; i++)
            {
                m.Vertices.Add(q.Corner(i));
            }
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
            if (_mesh != null && _material != null)
            {
                args.Display.DrawMeshShaded(_mesh, _material);
            }
        }

        private void ResetPreview()
        {
            _mesh?.Dispose();
            _mesh = null;
            _material?.Dispose();
            _material = null;
        }

        private bool ResolveRange(string text, double dmin, double dmax, out double min, out double max)
        {
            min = dmin;
            max = dmax;
            if (!string.IsNullOrWhiteSpace(text))
            {
                var p = text.Split(';');
                return p.Length == 2
                    && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out min)
                    && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out max)
                    && max > min;
            }
            if (max <= min)
            {
                double c = Finite(min) ? min : 0;
                min = c - 1;
                max = c + 1;
            }
            else
            {
                double pad = (max - min) * .05;
                min -= pad;
                max += pad;
            }
            return true;
        }

        private string DirectoryPath(string p)
        {
            if (!string.IsNullOrWhiteSpace(p))
                return Path.GetFullPath(p);
            return WasperChartSettingsTools.DefaultOutputDirectory(OnPingDocument()?.FilePath);
        }

        private static string Extension(string n)
        {
            string e = Path.GetExtension(n ?? "").ToLowerInvariant();
            return e == ".jpg" || e == ".jpeg" ? e : ".png";
        }

        private static string FileName(string n, string title, string e)
        {
            string s = string.IsNullOrWhiteSpace(n) ? title : Path.GetFileNameWithoutExtension(n);
            if (string.IsNullOrWhiteSpace(s))
                s = "Histogram";
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                s = s.Replace(c, '_');
            }
            return s.Replace(' ', '_') + e;
        }

        private static void SaveBitmap(Bitmap bmp, string path, string ext)
        {
            if (ext == ".png")
            {
                bmp.Save(path, ImageFormat.Png);
            }
            else
            {
                using var flat = new Bitmap(bmp.Width, bmp.Height);
                using var g = Graphics.FromImage(flat);
                g.Clear(Color.White);
                g.DrawImageUnscaled(bmp, 0, 0);
                flat.Save(path, ImageFormat.Jpeg);
            }
        }

        private static bool Dims(string t, out double w, out double h)
        {
            w = h = 0;
            var p = (t ?? "").Split(';');
            return p.Length == 2
                && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out w)
                && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out h)
                && w > 0
                && h > 0;
        }

        private static double Nice(double r)
        {
            if (!Finite(r) || r <= 0)
                return 1;
            double p = Math.Pow(10, Math.Floor(Math.Log10(r)));
            double f = r / p;
            return (f <= 1 ? 1
                : f <= 2 ? 2
                : f <= 5 ? 5
                : 10) * p;
        }

        private static Font FontOf(double s) => new Font(FontFamily.GenericSansSerif, (float)Math.Max(1, s), GraphicsUnit.Point);

        private static float MapX(double x, RectangleF plot, double min, double max) =>
            plot.Left + (float)((x - min) / (max - min)) * plot.Width;

        private static float MapY(double y, RectangleF plot, double min, double max) =>
            plot.Bottom - (float)((y - min) / (max - min)) * plot.Height;

        private static string FormatTick(double v, bool integer) =>
            integer ? Math.Round(v).ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.00", CultureInfo.InvariantCulture);

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private void Error(IGH_DataAccess da, string m)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, m);
            Message = $"{_version} | error";
            da.SetData(0, null);
        }
    }
}
