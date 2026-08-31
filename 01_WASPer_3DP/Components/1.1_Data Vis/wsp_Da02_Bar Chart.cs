#region Component Description
/*
    Component Name:
        wsp_Da02_Bar Chart

    Nickname:
        Bar Chart

    Version:
        Assembly-derived (vMajor.Minor.Build)

    Category / Subcategory:
        WASPerformance / 1.1_Data Vis

    Description:
        Creates grouped or stacked multi-dataset bar charts with native .NET drawing.
        Reuses the same optional Axis, Legend, and Layout parameter objects as
        Da01 Scatter Plot; no Python or external plotting library is required.

    Inputs:
        chart_rect   : optional planar rectangle for an aspect-preserving Rhino viewport preview
        values       : bar values tree; each branch is one dataset/legend series
        categories   : optional category labels in item order; missing labels become 1, 2, 3...
        bar_colors   : optional native colours in dataset order; missing colours use the built-in palette
        stacked      : false draws datasets side-by-side; true stacks datasets within each category
        bar_width    : fraction of each category slot occupied by bars, from 0.05 to 1
        axis_params  : optional Da10 Chart Axis Params
        legend_params: optional Da11 Chart Legend Params
        layout_params: optional Da12 Chart Layout Params
        refresh      : inert Button/Timer trigger for regenerating the file

    Output:
        bar_plot : absolute path to the generated PNG or JPEG bar chart
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
    public sealed class wsp_Da02_Bar_Chart : GH_Component
    {
        private static readonly Color[] Palette =
        {
            Color.FromArgb(31, 119, 180), Color.FromArgb(255, 127, 14),
            Color.FromArgb(44, 160, 44), Color.FromArgb(214, 39, 40),
            Color.FromArgb(148, 103, 189)
        };

        private readonly string _version;
        private Mesh _mesh;
        private DisplayMaterial _material;

        public wsp_Da02_Bar_Chart()
            : base(
                "wsp_Da02_Bar Chart", "Bar Chart",
                "Creates grouped or stacked multi-dataset bar charts with native .NET drawing. " +
                "Reuses the same optional Axis, Legend, and Layout parameter objects as Da01 Scatter Plot; " +
                "no Python or external plotting library is required.", global::WASPer_3DP.WASPerPalette.Performance, "1.1_Data Vis")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _version = v == null ? "v1.0.5" : $"v{v.Major}.{v.Minor}.{v.Build}";
            Message = _version;
        }

        public override Guid ComponentGuid => new Guid("DEFB7043-2041-4213-8EBC-7E94EFCECF8B");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Da02_Bar Chart.png");
                    return s == null ? null : new Bitmap(s);
                }
                catch { return null; }
            }
        }

        // ── inputs ────────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddRectangleParameter("chart_rect", "chart_rect", "Optional planar rectangle for an aspect-preserving Rhino viewport preview.", GH_ParamAccess.item);
            p.AddNumberParameter("values", "values", "Bar values tree. Each branch is one dataset/legend series; item position identifies the category.", GH_ParamAccess.tree);
            p.AddTextParameter("categories", "categories", "Optional category labels in item order. Missing labels become 1, 2, 3...", GH_ParamAccess.list);
            p.AddColourParameter("bar_colors", "colors", "Optional colours in dataset order. If fewer colours than value branches are supplied, colours are interpolated across all branches; one colour is repeated and an empty list uses the default palette.", GH_ParamAccess.list);
            p[3].DataMapping = GH_DataMapping.Flatten;
            p.AddBooleanParameter("stacked", "stacked", "False draws datasets side-by-side; true stacks datasets within each category. Default false.", GH_ParamAccess.item, false);
            p.AddNumberParameter("bar_width", "width", "Fraction of each category slot occupied by bars, from 0.05 to 1. Default 0.8.", GH_ParamAccess.item, .8);
            p.AddGenericParameter("axis_params", "axis", "Optional Da10 Chart Axis Params. Disconnected uses automatic Y range/ticks and no axis titles.", GH_ParamAccess.item);
            p.AddGenericParameter("legend_params", "legend", "Optional Da11 Chart Legend Params. Disconnected hides the legend.", GH_ParamAccess.item);
            p.AddGenericParameter("layout_params", "layout", "Optional Da12 Chart Layout Params. Disconnected uses 160;100 mm, 150 DPI, untitled white PNG.", GH_ParamAccess.item);
            p.AddBooleanParameter("refresh", "refresh", "Inert Button/Timer trigger for regenerating the file.", GH_ParamAccess.item, false);

            for (int i = 0; i < p.ParamCount; i++) p[i].Optional = true;
        }

        // ── outputs ───────────────────────────────────────────────────────────
        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddTextParameter("bar_plot", "bar_plot", "Absolute path to the generated PNG or JPEG bar chart.", GH_ParamAccess.item);

        // ── solve ─────────────────────────────────────────────────────────────
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

            var categories = new List<string>();
            var colors = new List<Color>();
            bool stacked = false, refresh = false;
            double width = .8;
            object ra = null, rl = null, rp = null;

            da.GetDataList(2, categories);
            da.GetDataList(3, colors);
            da.GetData(4, ref stacked);
            da.GetData(5, ref width);
            da.GetData(6, ref ra);
            da.GetData(7, ref rl);
            da.GetData(8, ref rp);
            da.GetData(9, ref refresh);

            width = Math.Max(.05, Math.Min(1, width));
            colors = WasperChartSettingsTools.ResolveSeriesColors(colors.Select(c => c.ToArgb()).ToList(), tree.PathCount, Palette);
            var axis = WasperChartSettingsTools.Axis(ra) ?? new WasperChartAxisSettings();
            var legend = WasperChartSettingsTools.Legend(rl) ?? new WasperChartLegendSettings();
            var layout = WasperChartSettingsTools.Layout(rp) ?? new WasperChartLayoutSettings();

            int seriesCount = tree.PathCount;
            int categoryCount = tree.Branches.Max(b => b.Count);
            if (categoryCount == 0)
            {
                Error(da, "Values branches are empty.");
                return;
            }

            while (categories.Count < categoryCount)
                categories.Add((categories.Count + 1).ToString(CultureInfo.InvariantCulture));

            var data = new double[seriesCount, categoryCount];
            for (int s = 0; s < seriesCount; s++)
                for (int c = 0; c < categoryCount; c++)
                    data[s, c] = c < tree.Branches[s].Count && Finite(tree.Branches[s][c].Value) ? tree.Branches[s][c].Value : 0;

            double min = 0, max = 0;
            if (stacked)
            {
                for (int c = 0; c < categoryCount; c++)
                {
                    double pos = 0, neg = 0;
                    for (int s = 0; s < seriesCount; s++)
                    {
                        double v = data[s, c];
                        if (v >= 0) pos += v; else neg += v;
                    }
                    max = Math.Max(max, pos);
                    min = Math.Min(min, neg);
                }
            }
            else
            {
                foreach (double v in data)
                {
                    max = Math.Max(max, v);
                    min = Math.Min(min, v);
                }
            }

            if (!ResolveRange(axis.YRange, min, max, out min, out max))
            {
                Error(da, "Invalid Y range; use 'minimum;maximum'.");
                return;
            }
            double tick = axis.YTickInterval > 0 ? axis.YTickInterval : Nice((max - min) / 8);

            if (!Dims(layout.Dimensions, out double mmW, out double mmH))
            {
                Error(da, "Layout dimensions must be 'width_mm;height_mm'.");
                return;
            }
            int dpi = Math.Max(36, Math.Min(1200, layout.Dpi));
            int pxW = Math.Max(64, (int)Math.Round(mmW / 25.4 * dpi));
            int pxH = Math.Max(64, (int)Math.Round(mmH / 25.4 * dpi));

            string ext = Extension(layout.FileName), path;
            try
            {
                string dir = DirectoryPath(layout.FilePath);
                Directory.CreateDirectory(dir);
                path = Path.Combine(dir, FileName(layout.FileName, layout.Title, ext));
            }
            catch (Exception ex)
            {
                Error(da, ex.Message);
                return;
            }

            try
            {
                using Bitmap bmp = Render(pxW, pxH, dpi, data, categories, colors, stacked, width, axis, legend, layout, min, max, tick);
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
            Message = $"{_version} | {seriesCount} sets";
            da.SetData(0, path);
        }

        // ── rendering ─────────────────────────────────────────────────────────
        private Bitmap Render(int w, int h, int dpi, double[,] d, IList<string> cats, IList<Color> colors, bool stacked, double width,
            WasperChartAxisSettings a, WasperChartLegendSettings l, WasperChartLayoutSettings p, double min, double max, double tick)
        {
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(b);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(p.TransparentBackground ? Color.Transparent : Color.White);
            float sc = dpi / 72f;

            using var title = FontOf(p.TitleSize);
            using var text = FontOf(a.XTextSize);
            using var axisTitle = FontOf(a.XTitleSize);
            using var legendFont = FontOf(l.TextSize);
            using var brush = new SolidBrush(Color.FromArgb(35, 35, 35));
            using var grid = new Pen(Color.LightGray, Math.Max(1, sc * .6f));
            using var edge = new Pen(Color.FromArgb(40, 40, 40), Math.Max(1, sc * .8f));

            float left = 60 * sc;
            float right = 18 * sc;
            float top = string.IsNullOrWhiteSpace(p.Title) ? 15 * sc : title.GetHeight(g) + 15 * sc;
            float bottom = text.GetHeight(g) + axisTitle.GetHeight(g) + 25 * sc;
            var plot = RectangleF.FromLTRB(left, top, w - right, h - bottom);
            using (var bg = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                g.FillRectangle(bg, plot);

            bool ints = a.YTicksInteger && tick >= 1;
            for (double v = Math.Ceiling(min / tick) * tick; v <= max + tick * 1e-8; v += tick)
            {
                float y = plot.Bottom - (float)((v - min) / (max - min)) * plot.Height;
                if (p.ShowReferences) g.DrawLine(grid, plot.Left, y, plot.Right, y);
                g.DrawString(ints ? Math.Round(v).ToString("0") : v.ToString("0.00"), text, brush,
                    new RectangleF(0, y - text.GetHeight(g), plot.Left - 6 * sc, text.GetHeight(g) * 2),
                    new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center });
            }

            int ns = d.GetLength(0), nc = d.GetLength(1);
            float slot = plot.Width / nc;
            float baseY = plot.Bottom - (float)((0 - min) / (max - min)) * plot.Height;

            for (int c = 0; c < nc; c++)
            {
                float pos = baseY, neg = baseY;
                for (int s = 0; s < ns; s++)
                {
                    double v = d[s, c];
                    float bw = slot * (float)width / (stacked ? 1 : ns);
                    float x = plot.Left + c * slot + slot * (1 - (float)width) / 2 + (stacked ? 0 : s * bw);
                    float vh = (float)(Math.Abs(v) / (max - min)) * plot.Height;
                    float y;
                    if (stacked)
                    {
                        y = v >= 0 ? pos - vh : neg;
                        if (v >= 0) pos = y; else neg += vh;
                    }
                    else
                    {
                        y = v >= 0 ? baseY - vh : baseY;
                    }
                    using var cb = new SolidBrush(s < colors.Count ? colors[s] : Palette[s % Palette.Length]);
                    g.FillRectangle(cb, x, y, bw, vh);
                    g.DrawRectangle(edge, x, y, bw, vh);
                }
                g.DrawString(cats[c], text, brush,
                    new RectangleF(plot.Left + c * slot, plot.Bottom + 5 * sc, slot, text.GetHeight(g) * 2),
                    new StringFormat { Alignment = StringAlignment.Center });
            }

            g.DrawRectangle(edge, plot.X, plot.Y, plot.Width, plot.Height);
            if (!string.IsNullOrWhiteSpace(p.Title))
                g.DrawString(p.Title, title, brush, new RectangleF(0, 3 * sc, w, title.GetHeight(g)),
                    new StringFormat { Alignment = StringAlignment.Center });
            if (!string.IsNullOrWhiteSpace(a.XTitle))
                g.DrawString(a.XTitle, axisTitle, brush, new RectangleF(plot.Left, h - axisTitle.GetHeight(g) - 2 * sc, plot.Width, axisTitle.GetHeight(g)),
                    new StringFormat { Alignment = StringAlignment.Center });

            if (!string.IsNullOrWhiteSpace(a.YTitle))
            {
                GraphicsState state = g.Save();
                g.TranslateTransform(axisTitle.GetHeight(g), plot.Top + plot.Height / 2);
                g.RotateTransform(-90);
                g.DrawString(a.YTitle, axisTitle, brush, new RectangleF(-plot.Height / 2, 0, plot.Height, axisTitle.GetHeight(g)),
                    new StringFormat { Alignment = StringAlignment.Center });
                g.Restore(state);
            }

            if ((l.Labels?.Count ?? 0) > 0)
            {
                float x = plot.Right - 120 * sc, y = plot.Top + 5 * sc;
                for (int s = 0; s < ns; s++)
                {
                    using var cb = new SolidBrush(s < colors.Count ? colors[s] : Palette[s % Palette.Length]);
                    g.FillRectangle(cb, x, y + s * 16 * sc, 10 * sc, 10 * sc);
                    g.DrawString(s < l.Labels.Count ? l.Labels[s] : $"Series {s + 1}", legendFont, brush, x + 14 * sc, y + s * 16 * sc);
                }
            }

            return b;
        }

        // ── helpers ───────────────────────────────────────────────────────────
        private static Font FontOf(double s) => new Font(FontFamily.GenericSansSerif, (float)Math.Max(1, s), GraphicsUnit.Point);

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

        public override void DrawViewportMeshes(IGH_PreviewArgs a)
        {
            base.DrawViewportMeshes(a);
            if (_mesh != null && _material != null) a.Display.DrawMeshShaded(_mesh, _material);
        }

        private void ResetPreview()
        {
            _mesh?.Dispose();
            _mesh = null;
            _material?.Dispose();
            _material = null;
        }

        private bool ResolveRange(string t, double dmin, double dmax, out double min, out double max)
        {
            min = dmin;
            max = dmax;
            if (!string.IsNullOrWhiteSpace(t))
            {
                var p = t.Split(';');
                return p.Length == 2
                    && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out min)
                    && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out max)
                    && max > min;
            }
            if (max <= min) max = min + 1;
            double pad = (max - min) * .05;
            min -= pad;
            max += pad;
            return true;
        }

        private static double Nice(double r)
        {
            if (r <= 0) return 1;
            double p = Math.Pow(10, Math.Floor(Math.Log10(r))), f = r / p;
            return (f <= 1 ? 1 : f <= 2 ? 2 : f <= 5 ? 5 : 10) * p;
        }

        private static bool Dims(string t, out double w, out double h)
        {
            w = h = 0;
            var p = (t ?? "").Split(';');
            return p.Length == 2
                && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out w)
                && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out h)
                && w > 0 && h > 0;
        }

        private string DirectoryPath(string p)
        {
            if (!string.IsNullOrWhiteSpace(p)) return Path.GetFullPath(p);
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
            if (string.IsNullOrWhiteSpace(s)) s = "Bar_Plot";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Replace(' ', '_') + e;
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private void Error(IGH_DataAccess d, string m)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, m);
            Message = $"{_version} | error";
            d.SetData(0, null);
        }
    }
}
