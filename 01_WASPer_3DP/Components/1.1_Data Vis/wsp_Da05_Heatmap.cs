#region Component Description
/*
    Component Name: wsp_Da05_Heatmap
    Nickname: Heatmap
    Category / Subcategory: WASPerformance / 1.1_Data Vis

    Description:
        Creates a native heatmap from a numeric data tree. Each branch is a row and each item is a column.
        Values are mapped to a built-in blue-white-red gradient unless min/max are supplied.

    Inputs:
        chart_rect  : optional planar rectangle for Rhino viewport preview
        values      : matrix tree; each branch is one heatmap row
        x_labels    : optional column labels
        y_labels    : optional row labels
        min_max     : optional color scale range as "minimum;maximum"; empty uses data range
        show_values : draw numeric values inside cells
        axis_p      : optional Da10 Chart Axis Params
        layout_p    : optional Da12 Chart Layout Params
        refresh     : inert recompute trigger

    Outputs:
        heatmap : absolute path to the generated PNG or JPEG
        range   : [min, max] color scale used by the renderer
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
    public sealed class wsp_Da05_Heatmap : GH_Component
    {
        private readonly string _version;
        private Mesh _mesh;
        private DisplayMaterial _material;

        public wsp_Da05_Heatmap()
            : base(
                "wsp_Da05_Heatmap",
                "Heatmap",
                "Creates native heatmap charts from matrix-like Grasshopper data trees.",
                global::WASPer_3DP.WASPerPalette.Performance,
                "1.1_Data Vis"
            )
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _version = v == null ? "v1.0.5" : $"v{v.Major}.{v.Minor}.{v.Build}";
            Message = _version;
        }

        public override Guid ComponentGuid => new Guid("15AC71E7-9BA6-4B99-9449-1C56CF14F8D9");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    using var s = Assembly
                        .GetExecutingAssembly()
                        .GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Da05_Heatmap.png");
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
            p.AddNumberParameter(
                "values",
                "values",
                "Matrix tree. Each branch is one heatmap row; each item is one column value.",
                GH_ParamAccess.tree
            );
            p.AddTextParameter(
                "x_labels",
                "x_labels",
                "Optional column labels in item order. Missing labels become 1, 2, 3...",
                GH_ParamAccess.list
            );
            p.AddTextParameter(
                "y_labels",
                "y_labels",
                "Optional row labels in branch order. Missing labels become 1, 2, 3...",
                GH_ParamAccess.list
            );
            p.AddTextParameter(
                "min_max",
                "min_max",
                "Optional color scale range as 'minimum;maximum'. Empty uses the finite data range.",
                GH_ParamAccess.item,
                ""
            );
            p.AddBooleanParameter(
                "show_values",
                "values?",
                "Draw numeric values in heatmap cells. Default false.",
                GH_ParamAccess.item,
                false
            );
            p.AddGenericParameter(
                "axis_params",
                "axis_p",
                "Optional Da10 Chart Axis Params. X/Y titles and text sizes are used.",
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
                "heatmap",
                "heatmap",
                "Absolute path to the generated PNG or JPEG heatmap.",
                GH_ParamAccess.item
            );
            p.AddNumberParameter(
                "range",
                "range",
                "Color scale range used by the renderer: [0] minimum, [1] maximum.",
                GH_ParamAccess.list
            );
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            ResetPreview();
            Rectangle3d rect = Rectangle3d.Unset;
            bool hasRect = da.GetData(0, ref rect) && rect.IsValid;
            GH_Structure<GH_Number> tree;
            if (!da.GetDataTree(1, out tree) || tree == null || tree.DataCount == 0)
            {
                Error(da, "Supply a values tree with at least one finite number.");
                return;
            }

            var xLabels = new List<string>();
            var yLabels = new List<string>();
            string minMax = "";
            bool showValues = false,
                refresh = false;
            object rawAxis = null,
                rawLayout = null;
            da.GetDataList(2, xLabels);
            da.GetDataList(3, yLabels);
            da.GetData(4, ref minMax);
            da.GetData(5, ref showValues);
            da.GetData(6, ref rawAxis);
            da.GetData(7, ref rawLayout);
            da.GetData(8, ref refresh);

            var axis = WasperChartSettingsTools.Axis(rawAxis) ?? new WasperChartAxisSettings();
            var layout = WasperChartSettingsTools.Layout(rawLayout) ?? new WasperChartLayoutSettings();

            int rows = tree.PathCount;
            int cols = tree.Branches.Max(b => b.Count);
            if (cols == 0)
            {
                Error(da, "Values branches are empty.");
                return;
            }

            double[,] values = new double[rows, cols];
            bool[,] valid = new bool[rows, cols];
            var finite = new List<double>();
            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    if (c < tree.Branches[r].Count && tree.Branches[r][c] != null && Finite(tree.Branches[r][c].Value))
                    {
                        values[r, c] = tree.Branches[r][c].Value;
                        valid[r, c] = true;
                        finite.Add(values[r, c]);
                    }
                }
            }
            if (finite.Count == 0)
            {
                Error(da, "No finite heatmap values were found.");
                return;
            }
            if (!ResolveRange(minMax, finite.Min(), finite.Max(), out double min, out double max))
            {
                Error(da, "Invalid min_max; use 'minimum;maximum'.");
                return;
            }

            while (xLabels.Count < cols)
            {
                xLabels.Add((xLabels.Count + 1).ToString(CultureInfo.InvariantCulture));
            }
            while (yLabels.Count < rows)
            {
                yLabels.Add((yLabels.Count + 1).ToString(CultureInfo.InvariantCulture));
            }
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
                using Bitmap bmp = Render(pxW, pxH, dpi, values, valid, xLabels, yLabels, min, max, showValues, axis, layout);
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
            Message = $"{_version} | {rows}x{cols}";
            da.SetData(0, path);
            da.SetDataList(1, new[] { min, max });
        }

        private static Bitmap Render(
            int w,
            int h,
            int dpi,
            double[,] values,
            bool[,] valid,
            IList<string> xLabels,
            IList<string> yLabels,
            double min,
            double max,
            bool showValues,
            WasperChartAxisSettings axis,
            WasperChartLayoutSettings layout
        )
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(layout.TransparentBackground ? Color.Transparent : Color.White);
            float sc = dpi / 72f;
            using var titleFont = FontOf(layout.TitleSize);
            using var tickFont = FontOf(axis.XTextSize);
            using var axisFont = FontOf(axis.XTitleSize);
            using var textBrush = new SolidBrush(Color.FromArgb(35, 35, 35));
            using var edgePen = new Pen(Color.FromArgb(230, 255, 255, 255), Math.Max(1f, .5f * sc));
            int rows = values.GetLength(0),
                cols = values.GetLength(1);
            float left = Math.Max(52 * sc, yLabels.Max(l => g.MeasureString(l, tickFont).Width) + 10 * sc);
            float right = 52 * sc;
            float top = string.IsNullOrWhiteSpace(layout.Title) ? 14 * sc : titleFont.GetHeight(g) + 16 * sc;
            float bottom = tickFont.GetHeight(g) + axisFont.GetHeight(g) + 28 * sc;
            var plot = RectangleF.FromLTRB(left, top, w - right, h - bottom);
            float cw = plot.Width / cols,
                ch = plot.Height / rows;
            using var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var rightAlign = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    var cell = new RectangleF(plot.Left + c * cw, plot.Top + r * ch, cw, ch);
                    using var brush = new SolidBrush(valid[r, c] ? Gradient(values[r, c], min, max) : Color.FromArgb(235, 235, 235));
                    g.FillRectangle(brush, cell);
                    g.DrawRectangle(edgePen, cell.X, cell.Y, cell.Width, cell.Height);
                    if (showValues && valid[r, c] && cw > 20 * sc && ch > 12 * sc)
                    {
                        Color tc = Luminance(Gradient(values[r, c], min, max)) < 120 ? Color.White : Color.FromArgb(35, 35, 35);
                        using var vb = new SolidBrush(tc);
                        g.DrawString(values[r, c].ToString("0.###", CultureInfo.InvariantCulture), tickFont, vb, cell, center);
                    }
                }
            }

            for (int c = 0; c < cols; c++)
            {
                g.DrawString(
                    xLabels[c],
                    tickFont,
                    textBrush,
                    new RectangleF(plot.Left + c * cw, plot.Bottom + 4 * sc, cw, tickFont.GetHeight(g) * 2),
                    center
                );
            }
            for (int r = 0; r < rows; r++)
            {
                g.DrawString(
                    yLabels[r],
                    tickFont,
                    textBrush,
                    new RectangleF(0, plot.Top + r * ch, plot.Left - 6 * sc, ch),
                    rightAlign
                );
            }
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
            if (!string.IsNullOrWhiteSpace(axis.XTitle))
            {
                g.DrawString(
                    axis.XTitle,
                    axisFont,
                    textBrush,
                    new RectangleF(plot.Left, h - axisFont.GetHeight(g) - 3 * sc, plot.Width, axisFont.GetHeight(g)),
                    new StringFormat { Alignment = StringAlignment.Center }
                );
            }
            if (!string.IsNullOrWhiteSpace(axis.YTitle))
            {
                GraphicsState st = g.Save();
                g.TranslateTransform(axisFont.GetHeight(g), plot.Top + plot.Height / 2);
                g.RotateTransform(-90);
                g.DrawString(axis.YTitle, axisFont, textBrush, new RectangleF(-plot.Height / 2, 0, plot.Height, axisFont.GetHeight(g)), center);
                g.Restore(st);
            }

            DrawColorBar(g, new RectangleF(plot.Right + 12 * sc, plot.Top, 12 * sc, plot.Height), min, max, tickFont, textBrush, sc);
            return bmp;
        }

        private static void DrawColorBar(Graphics g, RectangleF bar, double min, double max, Font font, Brush textBrush, float sc)
        {
            int steps = Math.Max(8, (int)bar.Height);
            for (int i = 0; i < steps; i++)
            {
                double t = 1.0 - i / (double)(steps - 1);
                using var b = new SolidBrush(Gradient(min + t * (max - min), min, max));
                g.FillRectangle(b, bar.Left, bar.Top + i * bar.Height / steps, bar.Width, bar.Height / steps + 1);
            }
            using var pen = new Pen(Color.FromArgb(80, 80, 80), Math.Max(1f, .6f * sc));
            g.DrawRectangle(pen, bar.X, bar.Y, bar.Width, bar.Height);
            g.DrawString(max.ToString("0.###", CultureInfo.InvariantCulture), font, textBrush, bar.Right + 3 * sc, bar.Top - font.GetHeight(g) / 2);
            g.DrawString(min.ToString("0.###", CultureInfo.InvariantCulture), font, textBrush, bar.Right + 3 * sc, bar.Bottom - font.GetHeight(g) / 2);
        }

        private static Color Gradient(double v, double min, double max)
        {
            double t = max > min ? Math.Max(0, Math.Min(1, (v - min) / (max - min))) : .5;
            if (t < .5)
            {
                double u = t / .5;
                return Mix(Color.FromArgb(49, 130, 189), Color.White, u);
            }
            return Mix(Color.White, Color.FromArgb(203, 24, 29), (t - .5) / .5);
        }

        private static Color Mix(Color a, Color b, double t) =>
            Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t)
            );

        private static int Luminance(Color c) => (int)(0.299 * c.R + 0.587 * c.G + 0.114 * c.B);

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

        private static bool ResolveRange(string text, double dmin, double dmax, out double min, out double max)
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
                s = "Heatmap";
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

        private static Font FontOf(double s) => new Font(FontFamily.GenericSansSerif, (float)Math.Max(1, s), GraphicsUnit.Point);

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);

        private void Error(IGH_DataAccess da, string m)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, m);
            Message = $"{_version} | error";
            da.SetData(0, null);
        }
    }
}
