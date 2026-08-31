#region Component Description
/* Da08 renders a native parallel-coordinates chart from a solution data tree. */
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
    public sealed class wsp_Da08_Parallel_Coordinates : GH_Component
    {
        private readonly string _version;
        private Mesh _previewMesh;
        private DisplayMaterial _previewMaterial;

        public wsp_Da08_Parallel_Coordinates()
            : base(
                "wsp_Da08_Parallel Coordinates",
                "Parallel Coordinates",
                "Creates a native Design-Explorer-style parallel-coordinates chart from solution data.",
                global::WASPer_3DP.WASPerPalette.Performance,
                "1.1_Data Vis"
            )
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _version = v == null ? "v1.0.5" : $"v{v.Major}.{v.Minor}.{v.Build}";
            Message = _version;
        }

        public override Guid ComponentGuid => new Guid("D9BDF242-9DA8-4AE8-A1F0-4C0CC8E9D5F2");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    using var s = Assembly
                        .GetExecutingAssembly()
                        .GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Da08_Parallel Coordinates.png");
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
                "Optional Rhino rectangle for preview. If disconnected, a centered default rectangle based on layout dimensions is created automatically; connect a Rectangle to control its location and orientation.",
                GH_ParamAccess.item
            );
            p.AddNumberParameter(
                "samples",
                "samples",
                "Solutions as a tree: {0}=[10,3.2,0.4] and {1}=[12,3.6,0.7]. Each branch is one polyline and each item is one parameter; all branches must have equal length.",
                GH_ParamAccess.tree
            );
            p.AddTextParameter(
                "param_names",
                "param_names",
                "Axis names in item order, for example [\"cost\",\"energy\",\"comfort\"]. If omitted, axes are labelled P1, P2, P3, etc.",
                GH_ParamAccess.list
            );
            p.AddTextParameter(
                "samp_names",
                "samp_names",
                "Optional solution identifiers in branch order, for example [\"A\",\"B\",\"C\"]. They are retained as context for downstream workflows.",
                GH_ParamAccess.list
            );
            p.AddTextParameter(
                "norm_range",
                "norm_range",
                "Optional normalization target. Disconnect for raw axes; connect 10 for 0..10 or 0;1 for 0..1. Normalization is per parameter.",
                GH_ParamAccess.item,
                ""
            );
            p.AddIntegerParameter(
                "color_mode",
                "color_mode",
                "Line colours: 0 cycles the default/group palette; 1 maps color_param values to a continuous gradient; 2 uses pf_bool (blue=True Pareto, grey=False dominated).",
                GH_ParamAccess.item,
                0
            );
            p.AddIntegerParameter(
                "color_param",
                "color_param",
                "Zero-based parameter index used only when color_mode=1. Example color_param=2 colours every solution according to its third parameter.",
                GH_ParamAccess.item,
                0
            );
            p.AddBooleanParameter(
                "pf_bool",
                "pf_bool",
                "Optional Pareto flags in solution order, normally connected from Da06.pf_bool. Required for color_mode=2; True colours a Pareto solution blue and False colours it grey.",
                GH_ParamAccess.list
            );
            p.AddIntegerParameter(
                "highlight_inds",
                "highlight_inds",
                "Optional zero-based solution indices to draw thicker. Example [0,5,12] emphasizes those three polylines.",
                GH_ParamAccess.list
            );
            p.AddGenericParameter(
                "marker_params",
                "marker_p",
                "Optional Da09 settings. Line colour anchors are interpolated when fewer colours than solution lines are supplied; line width/type are reused and disconnected uses readable defaults.",
                GH_ParamAccess.item
            );
            p.AddGenericParameter(
                "axis_params",
                "axis_p",
                "Optional Da10 typography settings. Axis titles come from param_names because the chart supports any number of parameters.",
                GH_ParamAccess.item
            );
            p.AddGenericParameter(
                "layout_params",
                "layout_p",
                "Optional Da12 title, dimensions, DPI, background, and output-file settings. Disconnected uses a temporary PNG.",
                GH_ParamAccess.item
            );
            p.AddBooleanParameter(
                "refresh",
                "refresh",
                "Optional Button/Timer trigger for regenerating the chart after upstream changes.",
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
                "parallel_plot",
                "parallel_plot",
                "Absolute path to the generated parallel-coordinates PNG/JPEG; connect to a panel or image/file workflow.",
                GH_ParamAccess.item
            );
            p.AddNumberParameter(
                "normalized_samples",
                "normalized_samples",
                "Tree of plotted values after optional normalization; branch {i} corresponds to solution i in the chart.",
                GH_ParamAccess.tree
            );
            p.AddTextParameter(
                "parameter_ranges",
                "parameter_ranges",
                "One minimum;maximum string per parameter, for example [\"0;10\",\"2;8\"].",
                GH_ParamAccess.list
            );
            p.AddIntegerParameter(
                "sample_inds",
                "sample_inds",
                "Zero-based indices of solutions that were successfully plotted; invalid rows are omitted.",
                GH_ParamAccess.list
            );
            p.AddColourParameter(
                "line_colors",
                "line_colors",
                "Resolved line colour per plotted solution, matching sample_inds order.",
                GH_ParamAccess.list
            );
            p.AddTextParameter(
                "summary",
                "summary",
                "Chart report containing solution/parameter counts, colour mode, normalization state, and validation notes.",
                GH_ParamAccess.item
            );
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            Rectangle3d rect = Rectangle3d.Unset;
            da.GetData(0, ref rect);
            GH_Structure<GH_Number> tree;
            if (!da.GetDataTree(1, out tree) || tree == null || tree.PathCount == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Connect a samples data tree to generate the parallel-coordinates chart."
                );
                return;
            }

            var paramNames = new List<string>();
            var sampleNames = new List<string>();
            string normText = "";
            int mode = 0,
                colorParam = 0;
            var pf = new List<bool>();
            var highlight = new List<int>();
            object rawMarker = null,
                rawAxis = null,
                rawLayout = null;
            bool refresh = false;
            da.GetDataList(2, paramNames);
            da.GetDataList(3, sampleNames);
            da.GetData(4, ref normText);
            da.GetData(5, ref mode);
            da.GetData(6, ref colorParam);
            da.GetDataList(7, pf);
            da.GetDataList(8, highlight);
            da.GetData(9, ref rawMarker);
            da.GetData(10, ref rawAxis);
            da.GetData(11, ref rawLayout);
            da.GetData(12, ref refresh);

            int cols = tree.Branches.Max(b => b?.Count ?? 0);
            var rows = new List<double[]>();
            var notes = new List<string>();
            for (int i = 0; i < tree.PathCount; i++)
            {
                var b = tree.Branches[i];
                if (b == null || b.Count != cols || b.Any(x => double.IsNaN(x.Value) || double.IsInfinity(x.Value)))
                {
                    notes.Add($"Solution {i} is invalid or has a different parameter count and was skipped.");
                    continue;
                }
                rows.Add(b.Select(x => x.Value).ToArray());
            }
            if (rows.Count == 0 || cols == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No valid equally-sized samples were found.");
                return;
            }

            double loTarget = 0,
                hiTarget = 1;
            bool normalize = !string.IsNullOrWhiteSpace(normText);
            if (normalize && (!ParseRange(normText, ref loTarget, ref hiTarget) || hiTarget <= loTarget))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "norm_range must be a single maximum or minimum;maximum.");
                return;
            }

            var work = rows.Select(a => (double[])a.Clone()).ToList();
            var ranges = new List<string>();
            for (int d = 0; d < cols; d++)
            {
                double lo = rows.Min(a => a[d]),
                    hi = rows.Max(a => a[d]);
                ranges.Add(lo.ToString("G6", CultureInfo.InvariantCulture) + ";" + hi.ToString("G6", CultureInfo.InvariantCulture));
                if (normalize)
                {
                    for (int i = 0; i < work.Count; i++)
                    {
                        work[i][d] =
                            Math.Abs(hi - lo) < 1e-12
                                ? (loTarget + hiTarget) * .5
                                : loTarget + (work[i][d] - lo) / (hi - lo) * (hiTarget - loTarget);
                    }
                }
            }

            var marker = WasperChartSettingsTools.MarkerLine(rawMarker) ?? new WasperChartMarkerLineSettings();
            var axis = WasperChartSettingsTools.Axis(rawAxis) ?? new WasperChartAxisSettings();
            var layout = WasperChartSettingsTools.Layout(rawLayout) ?? new WasperChartLayoutSettings();
            var colors = ResolveColors(rows, work, marker, mode, colorParam, pf);

            if (!Dims(layout.Dimensions, out double mmW, out double mmH))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Layout dimensions must be width;height in millimetres.");
                return;
            }

            int dpi = Math.Max(36, Math.Min(1200, layout.Dpi));
            int w = Math.Max(320, (int)Math.Round(mmW / 25.4 * dpi)),
                h = Math.Max(180, (int)Math.Round(mmH / 25.4 * dpi));
            string ext = string.IsNullOrWhiteSpace(layout.FileName) || !Path.HasExtension(layout.FileName)
                ? ".png"
                : Path.GetExtension(layout.FileName);
            string dir = string.IsNullOrWhiteSpace(layout.FilePath)
                ? WasperChartSettingsTools.DefaultOutputDirectory(OnPingDocument()?.FilePath)
                : layout.FilePath;
            Directory.CreateDirectory(dir);
            string path = Path.Combine(
                dir,
                string.IsNullOrWhiteSpace(layout.FileName) ? "parallel_coordinates.png" : layout.FileName
            );

            using (var bmp = Render(w, h, rows, work, paramNames, colors, new HashSet<int>(highlight), axis, layout))
            {
                bmp.SetResolution(dpi, dpi);
                bmp.Save(path, ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ? ImageFormat.Jpeg : ImageFormat.Png);
            }

            if (!rect.IsValid)
            {
                rect = new Rectangle3d(Plane.WorldXY, new Interval(-mmW * .5, mmW * .5), new Interval(-mmH * .5, mmH * .5));
            }
            CreatePreview(rect, path);

            var output = new GH_Structure<GH_Number>();
            for (int i = 0; i < work.Count; i++)
            {
                var ghPath = new Grasshopper.Kernel.Data.GH_Path(i);
                foreach (double value in work[i])
                {
                    output.Append(new GH_Number(value), ghPath);
                }
            }

            string summary =
                $"{work.Count} solutions; {cols} parameters; color_mode={Math.Max(0, Math.Min(2, mode))}; normalization={(normalize ? loTarget.ToString("G4", CultureInfo.InvariantCulture) + ".." + hiTarget.ToString("G4", CultureInfo.InvariantCulture) : "off")}."
                + (notes.Count == 0 ? "" : " " + string.Join(" ", notes));
            if (notes.Count > 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, string.Join(" ", notes));
            }

            da.SetData(0, path);
            da.SetDataTree(1, output);
            da.SetDataList(2, ranges);
            da.SetDataList(3, Enumerable.Range(0, work.Count));
            da.SetDataList(4, colors);
            da.SetData(5, summary);
            Message = _version + " | " + work.Count + " solutions";
        }

        private static Bitmap Render(
            int w,
            int h,
            IList<double[]> raw,
            IList<double[]> values,
            IList<string> names,
            IList<Color> colors,
            ISet<int> highlight,
            WasperChartAxisSettings axis,
            WasperChartLayoutSettings layout
        )
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(layout.TransparentBackground ? Color.Transparent : Color.White);

            int left = 55,
                right = 25,
                top = string.IsNullOrWhiteSpace(layout.Title) ? 25 : 48,
                bottom = 38;
            float pw = Math.Max(20, w - left - right),
                ph = Math.Max(20, h - top - bottom);
            int n = values[0].Length;

            using var text = new SolidBrush(Color.FromArgb(40, 40, 40));
            using var font = new Font("Arial", (float)Math.Max(7, axis.XTextSize));
            using var titleFont = new Font("Arial", (float)Math.Max(8, layout.TitleSize));

            for (int d = 0; d < n; d++)
            {
                float x = left + (n == 1 ? pw * .5f : pw * d / (n - 1));
                using var pen = new Pen(Color.FromArgb(150, 150, 160), 1);
                g.DrawLine(pen, x, top, x, top + ph);
                string label = d < names.Count && !string.IsNullOrWhiteSpace(names[d]) ? names[d] : "P" + (d + 1);
                g.DrawString(label, font, text, x - 30, top - 20);
            }

            for (int i = 0; i < values.Count; i++)
            {
                using var pen = new Pen(colors[i], highlight.Contains(i) ? 2.6f : 1.1f);
                var pts = new PointF[n];
                for (int d = 0; d < n; d++)
                {
                    double lo = values.Min(a => a[d]),
                        hi = values.Max(a => a[d]);
                    double u = hi == lo ? .5 : (values[i][d] - lo) / (hi - lo);
                    float x = left + (n == 1 ? pw * .5f : pw * d / (n - 1)),
                        y = top + ph * (float)(1 - u);
                    pts[d] = new PointF(x, y);
                }
                if (pts.Length > 1)
                {
                    g.DrawLines(pen, pts);
                }
            }

            if (!string.IsNullOrWhiteSpace(layout.Title))
            {
                using var titleFormat = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Near,
                };
                g.DrawString(layout.Title, titleFont, text, new RectangleF(0, 4, w, titleFont.GetHeight(g) + 6), titleFormat);
            }

            return bmp;
        }

        private static List<Color> ResolveColors(
            IList<double[]> raw,
            IList<double[]> values,
            WasperChartMarkerLineSettings marker,
            int mode,
            int colorParam,
            IList<bool> pf
        )
        {
            var result = new List<Color>();
            Color[] palette =
            {
                Color.FromArgb(31, 119, 180),
                Color.FromArgb(255, 127, 14),
                Color.FromArgb(44, 160, 44),
                Color.FromArgb(214, 39, 40),
                Color.FromArgb(148, 103, 189),
            };

            mode = Math.Max(0, Math.Min(2, mode));
            int p = Math.Max(0, Math.Min(values[0].Length - 1, colorParam));
            double lo = values.Min(a => a[p]),
                hi = values.Max(a => a[p]);
            var resolvedLineColors = WasperChartSettingsTools.ResolveSeriesColors(marker.LineColorsArgb, values.Count, palette);

            for (int i = 0; i < values.Count; i++)
            {
                if (mode == 2)
                {
                    result.Add(i < pf.Count && pf[i] ? Color.FromArgb(31, 119, 180) : Color.FromArgb(190, 190, 190));
                }
                else if (mode == 1)
                {
                    double u = hi == lo ? .5 : (values[i][p] - lo) / (hi - lo);
                    result.Add(Color.FromArgb((int)(255 * u), (int)(80 + 130 * (1 - u)), (int)(255 * (1 - u))));
                }
                else
                {
                    result.Add(resolvedLineColors[i]);
                }
            }

            return result;
        }

        private void CreatePreview(Rectangle3d rect, string path)
        {
            _previewMesh = null;
            _previewMaterial?.Dispose();
            _previewMaterial = null;

            if (!rect.IsValid || !File.Exists(path))
                return;

            var mesh = new Mesh();
            for (int i = 0; i < 4; i++)
            {
                mesh.Vertices.Add(rect.Corner(i));
            }
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
                return;
            }

            _previewMesh = mesh;
            _previewMaterial = material;
        }

        public override BoundingBox ClippingBox => _previewMesh?.GetBoundingBox(false) ?? BoundingBox.Empty;

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            base.DrawViewportMeshes(args);
            if (_previewMesh != null && _previewMaterial != null)
            {
                args.Display.DrawMeshShaded(_previewMesh, _previewMaterial);
            }
        }

        private static bool ParseRange(string s, ref double a, ref double b)
        {
            var p = s.Split(';');
            if (p.Length == 1 && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double single))
            {
                a = 0;
                b = single;
                return true;
            }
            return p.Length == 2
                && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a)
                && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b);
        }

        private static bool Dims(string s, out double w, out double h)
        {
            w = 160;
            h = 100;
            if (string.IsNullOrWhiteSpace(s))
                return true;
            var p = s.Split(';');
            return p.Length == 2
                && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out w)
                && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out h)
                && w > 0
                && h > 0;
        }
    }
}
