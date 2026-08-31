#region Component Description
/* Da07 maps list/tree point data into optional box coordinates and creates native 2D/3D graph geometry. */
#endregion
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;
using Rhino.Display;

namespace WASPer_3DP.Components._1_1_Data_Vis
{
    public sealed class wsp_Da07_3D_Graph : GH_Component
    {
        private readonly string _version;
        private readonly List<Mesh> _previewMeshes = new List<Mesh>();
        private readonly List<DisplayMaterial> _previewMaterials = new List<DisplayMaterial>();

        private static readonly Color[] DefaultGroupColors =
        {
            Color.FromArgb(31, 119, 180),
            Color.FromArgb(255, 127, 14),
            Color.FromArgb(44, 160, 44),
            Color.FromArgb(214, 39, 40),
            Color.FromArgb(148, 103, 189),
            Color.FromArgb(140, 86, 75),
        };

        private static class MeshingParameters
        {
            public static Rhino.Geometry.MeshingParameters Fast => Rhino.Geometry.MeshingParameters.Default;
        }

        public wsp_Da07_3D_Graph()
            : base(
                "wsp_Da07_3D Graph",
                "3D Graph",
                "Creates native 2D or 3D graph points, markers, axes, and labels from point data.",
                global::WASPer_3DP.WASPerPalette.Performance,
                "1.1_Data Vis"
            )
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _version = v == null ? "v1.0.5" : $"v{v.Major}.{v.Minor}.{v.Build}";
            Message = _version;
        }

        public override Guid ComponentGuid => new Guid("5C0C54E9-C51C-46A4-80D7-7C8DE5BFE70F");
        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    using var s = Assembly
                        .GetExecutingAssembly()
                        .GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Da07_3D Graph.png");
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
            p.AddPointParameter(
                "samp_pts",
                "samp_pts",
                "Points to plot as a list or tree. Example: {0}=[0,0,0], {1}=[1,4,2]. Z=0 for every point produces a 2D graph.",
                GH_ParamAccess.tree
            );
            p.AddBoxParameter(
                "box",
                "box",
                "Optional graph volume. Points are remapped independently to fill its X/Y/Z spans; for example a 100x60x30 Box makes the plotted bounds fill exactly 100, 60, and 30 units.",
                GH_ParamAccess.item
            );
            p.AddPlaneParameter(
                "ref_plane",
                "ref_plane",
                "Optional graph frame. Its origin is the graph vertex and its axes control orientation/flipping; connect a rotated or mirrored Plane to reorient the graph.",
                GH_ParamAccess.item
            );
            p.AddNumberParameter(
                "rotate",
                "rotate",
                "Additional rotation in degrees around ref_plane.Z. Example 90 rotates the graph a quarter turn; -90 flips the direction.",
                GH_ParamAccess.item,
                0.0
            );
            p.AddGenericParameter(
                "marker_params",
                "marker_p",
                "Optional Da09 settings. For this graph marker_type 0=none, 1=cube, 2=sphere; marker sizes and colours may be single values or branch/group lists.",
                GH_ParamAccess.item
            );
            p.AddGenericParameter(
                "axis_params",
                "axis_p",
                "Optional Da10 settings. X/Y/Z titles, ranges, tick intervals, text sizes, and line type drive the generated axes and tick labels.",
                GH_ParamAccess.item
            );
            p.AddIntegerParameter(
                "axis_ori",
                "axis_ori",
                "Box vertex index 0..7 used as the axis origin. Example 0 uses the minimum corner; 7 uses the opposite corner and reverses all axis directions inward.",
                GH_ParamAccess.item,
                0
            );
            p.AddBooleanParameter(
                "refresh",
                "refresh",
                "Optional Button/Timer trigger when the viewport or referenced inputs need a redraw.",
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
            p.AddPointParameter(
                "graph_pts",
                "graph_pts",
                "Mapped plot points. With a Box, their bounds fill the Box axes; without a Box, they remain in input coordinates.",
                GH_ParamAccess.list
            );
            p.AddBrepParameter(
                "markers_geo",
                "markers_geo",
                "Cube/sphere Breps generated at graph_pts; connect to Custom Preview with marker_colors for coloured downstream geometry.",
                GH_ParamAccess.list
            );
            p.AddColourParameter(
                "marker_colors",
                "marker_colors",
                "Resolved colour per graph point. Defaults cycle by input branch/group and can drive Custom Preview.",
                GH_ParamAccess.list
            );
            p.AddIntegerParameter(
                "axis_ori",
                "axis_ori",
                "Resolved Box axis-origin vertex index used by the graph; useful for confirming the selected corner.",
                GH_ParamAccess.item
            );
            p.AddCurveParameter(
                "axis_geo",
                "axis_geo",
                "Axis lines plus generated tick-mark curves. Connect to a Curve parameter or preview directly.",
                GH_ParamAccess.list
            );
            p.AddGeometryParameter(
                "axis_tags",
                "axis_tags",
                "TextDot geometry containing axis titles and numeric tick labels from axis_p or derived ranges.",
                GH_ParamAccess.list
            );
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            GH_Structure<GH_Point> tree;
            if (!da.GetDataTree(0, out tree) || tree == null || tree.DataCount == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Connect samp_pts as a point list or tree to generate the graph."
                );
                return;
            }

            Box box = Box.Unset;
            Plane refPlane = Plane.Unset;
            double rotate = 0;
            da.GetData(1, ref box);
            da.GetData(2, ref refPlane);
            da.GetData(3, ref rotate);

            object rawMarker = null,
                rawAxis = null;
            int axisOri = 0;
            bool refresh = false;
            da.GetData(4, ref rawMarker);
            da.GetData(5, ref rawAxis);
            da.GetData(6, ref axisOri);
            da.GetData(7, ref refresh);
            axisOri = Math.Max(0, Math.Min(7, axisOri));
            var marker = WasperChartSettingsTools.MarkerLine(rawMarker) ?? new WasperChartMarkerLineSettings();
            var axis = WasperChartSettingsTools.Axis(rawAxis) ?? new WasperChartAxisSettings();

            var input = tree
                .Branches.SelectMany(b => b ?? new List<GH_Point>())
                .Where(g => g != null && g.Value.IsValid)
                .Select(g => g.Value)
                .ToList();
            if (input.Count == 0)
                return;

            var graph = Map(input, box, refPlane, rotate);
            var markers = new List<Brep>();
            var markerColors = new List<Color>();
            var axes = new List<Curve>();
            var tags = new List<GeometryBase>();

            _previewMeshes.Clear();
            _previewMaterials.Clear();
            for (int i = 0; i < graph.Count; i++)
            {
                int branch = BranchOf(tree, i);
                int type = Pick(marker.MarkerTypes, branch, 2);
                double size = Math.Max(.001, Pick(marker.MarkerSizes, branch, 1.0));
                Color color =
                    marker.MarkerColorsArgb != null && marker.MarkerColorsArgb.Count > 0
                        ? Color.FromArgb(
                            Pick(marker.MarkerColorsArgb, branch, DefaultGroupColors[branch % DefaultGroupColors.Length].ToArgb())
                        )
                        : DefaultGroupColors[branch % DefaultGroupColors.Length];
                markerColors.Add(color);

                Brep brep = null;
                if (type == 1)
                {
                    brep = new Box(
                        Plane.WorldXY,
                        new Interval(graph[i].X - size / 2, graph[i].X + size / 2),
                        new Interval(graph[i].Y - size / 2, graph[i].Y + size / 2),
                        new Interval(graph[i].Z - size / 2, graph[i].Z + size / 2)
                    ).ToBrep();
                }
                else if (type == 2)
                {
                    brep = new Sphere(graph[i], size / 2).ToBrep();
                }

                if (brep != null)
                {
                    markers.Add(brep);
                    var pieces = Mesh.CreateFromBrep(brep, MeshingParameters.Fast);
                    if (pieces != null)
                    {
                        foreach (var mesh in pieces)
                        {
                            _previewMeshes.Add(mesh);
                            _previewMaterials.Add(new DisplayMaterial(color));
                        }
                    }
                }
            }

            Plane frame = Frame(box, refPlane, rotate);
            Point3d origin;
            Vector3d ex,
                ey,
                ez;
            double sx,
                sy,
                sz;
            AxisGeometry(frame, box, graph, axisOri, out origin, out ex, out ey, out ez, out sx, out sy, out sz);

            axes.Add(new LineCurve(new Line(origin, origin + ex * sx)));
            axes.Add(new LineCurve(new Line(origin, origin + ey * sy)));
            axes.Add(new LineCurve(new Line(origin, origin + ez * sz)));

            string xRange = string.IsNullOrWhiteSpace(axis.XRange) ? RangeString(input, 0) : axis.XRange;
            string yRange = string.IsNullOrWhiteSpace(axis.YRange) ? RangeString(input, 1) : axis.YRange;
            string zRange = string.IsNullOrWhiteSpace(axis.ZRange) ? RangeString(input, 2) : axis.ZRange;

            AddAxisTicks(
                origin,
                ex,
                ey,
                ez,
                sx,
                sy,
                sz,
                xRange,
                yRange,
                zRange,
                axis.XTickInterval,
                axis.YTickInterval,
                axis.ZTickInterval,
                axis.XTicksInteger,
                axis.YTicksInteger,
                axis.ZTicksInteger,
                axes,
                tags
            );

            tags.Add(new TextDot(string.IsNullOrWhiteSpace(axis.XTitle) ? "X" : axis.XTitle, origin + ex * sx));
            tags.Add(new TextDot(string.IsNullOrWhiteSpace(axis.YTitle) ? "Y" : axis.YTitle, origin + ey * sy));
            tags.Add(new TextDot(string.IsNullOrWhiteSpace(axis.ZTitle) ? "Z" : axis.ZTitle, origin + ez * sz));

            Message = _version + " | " + graph.Count + " pts";
            da.SetDataList(0, graph);
            da.SetDataList(1, markers);
            da.SetDataList(2, markerColors);
            da.SetData(3, axisOri);
            da.SetDataList(4, axes);
            da.SetDataList(5, tags);
        }

        private static List<Point3d> Map(IList<Point3d> pts, Box box, Plane refPlane, double rotate)
        {
            if (!box.IsValid)
                return pts.ToList();

            double xmin = pts.Min(p => p.X),
                xmax = pts.Max(p => p.X),
                ymin = pts.Min(p => p.Y),
                ymax = pts.Max(p => p.Y),
                zmin = pts.Min(p => p.Z),
                zmax = pts.Max(p => p.Z);
            double dx = xmax == xmin ? 1 : xmax - xmin,
                dy = ymax == ymin ? 1 : ymax - ymin,
                dz = zmax == zmin ? 1 : zmax - zmin;
            Plane f = Frame(box, refPlane, rotate);
            double sx = box.X.Length / dx,
                sy = box.Y.Length / dy,
                sz = box.Z.Length / dz;

            return pts
                .Select(p =>
                    f.Origin + f.XAxis * ((p.X - xmin) * sx) + f.YAxis * ((p.Y - ymin) * sy) + f.ZAxis * ((p.Z - zmin) * sz)
                )
                .ToList();
        }

        private static Plane Frame(Box b, Plane r, double rotate)
        {
            Plane f = r.IsValid ? r
                : b.IsValid ? b.Plane
                : Plane.WorldXY;
            if (Math.Abs(rotate) > 1e-12)
                f.Rotate(rotate * Math.PI / 180.0, f.ZAxis);
            return f;
        }

        private static void AxisGeometry(
            Plane frame,
            Box box,
            IList<Point3d> points,
            int corner,
            out Point3d origin,
            out Vector3d ex,
            out Vector3d ey,
            out Vector3d ez,
            out double sx,
            out double sy,
            out double sz
        )
        {
            bool bx = (corner & 1) != 0,
                by = (corner & 2) != 0,
                bz = (corner & 4) != 0;

            if (box.IsValid)
            {
                sx = box.X.Length;
                sy = box.Y.Length;
                sz = box.Z.Length;
                origin = frame.Origin + frame.XAxis * (bx ? sx : 0) + frame.YAxis * (by ? sy : 0) + frame.ZAxis * (bz ? sz : 0);
                ex = frame.XAxis * (bx ? -1 : 1);
                ey = frame.YAxis * (by ? -1 : 1);
                ez = frame.ZAxis * (bz ? -1 : 1);
                return;
            }

            double xmin = points.Min(p => p.X),
                xmax = points.Max(p => p.X),
                ymin = points.Min(p => p.Y),
                ymax = points.Max(p => p.Y),
                zmin = points.Min(p => p.Z),
                zmax = points.Max(p => p.Z);
            sx = Math.Max(xmax - xmin, 1e-6);
            sy = Math.Max(ymax - ymin, 1e-6);
            sz = Math.Max(zmax - zmin, 1e-6);
            origin = new Point3d(xmin, ymin, zmin);
            ex = frame.XAxis;
            ey = frame.YAxis;
            ez = frame.ZAxis;
        }

        private static void AddAxisTicks(
            Point3d o,
            Vector3d ex,
            Vector3d ey,
            Vector3d ez,
            double sx,
            double sy,
            double sz,
            string xr,
            string yr,
            string zr,
            double xi,
            double yi,
            double zi,
            bool xInt,
            bool yInt,
            bool zInt,
            IList<Curve> curves,
            IList<GeometryBase> tags
        )
        {
            AddTicks(o, ex, ey, sx, xr, xi, xInt, sy, curves, tags);
            AddTicks(o, ey, ex, sy, yr, yi, yInt, sx, curves, tags);
            AddTicks(o, ez, ex, sz, zr, zi, zInt, sx, curves, tags);
        }

        private static string RangeString(IList<Point3d> points, int axis)
        {
            double lo = points.Min(p => axis == 0 ? p.X
                : axis == 1 ? p.Y
                : p.Z),
                hi = points.Max(p => axis == 0 ? p.X
                    : axis == 1 ? p.Y
                    : p.Z);
            if (Math.Abs(hi - lo) < 1e-12)
                hi = lo + 1;
            return lo.ToString("G17", System.Globalization.CultureInfo.InvariantCulture)
                + ";"
                + hi.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void AddTicks(
            Point3d o,
            Vector3d axis,
            Vector3d tickDir,
            double length,
            string rangeText,
            double interval,
            bool integers,
            double tickScale,
            IList<Curve> curves,
            IList<GeometryBase> tags
        )
        {
            double lo = 0,
                hi = 1;
            if (!ParseRange(rangeText, out lo, out hi) || hi <= lo)
            {
                lo = 0;
                hi = 1;
            }

            double span = hi - lo;
            double step = interval > 1e-12 ? interval : NiceStep(span / 5.0);
            if (step <= 0)
                step = 1;
            double tickSize = Math.Max(length, tickScale) * 0.025;

            int guard = 0;
            for (double value = lo; value <= hi + step * 0.001 && guard++ < 100; value += step)
            {
                double u = (value - lo) / span;
                Point3d p = o + axis * (u * length);
                curves.Add(new LineCurve(new Line(p - tickDir * tickSize, p + tickDir * tickSize)));
                string label =
                    integers && Math.Abs(value - Math.Round(value)) < 1e-8
                        ? Math.Round(value).ToString("0", System.Globalization.CultureInfo.InvariantCulture)
                        : value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                tags.Add(new TextDot(label, p + tickDir * tickSize * 1.8));
            }
        }

        private static bool ParseRange(string text, out double lo, out double hi)
        {
            lo = 0;
            hi = 1;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            var p = text.Split(';');
            return p.Length == 2
                && double.TryParse(
                    p[0],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out lo
                )
                && double.TryParse(
                    p[1],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out hi
                );
        }

        private static double NiceStep(double value)
        {
            if (value <= 0)
                return 1;
            double p = Math.Pow(10, Math.Floor(Math.Log10(value)));
            double n = value / p;
            return (n <= 1 ? 1
                : n <= 2 ? 2
                : n <= 5 ? 5
                : 10) * p;
        }

        private static int BranchOf(GH_Structure<GH_Point> t, int flat)
        {
            int n = 0;
            for (int i = 0; i < t.Branches.Count; i++)
            {
                n += t.Branches[i].Count;
                if (flat < n)
                    return i;
            }
            return 0;
        }

        private static T Pick<T>(IList<T> list, int i, T fallback) =>
            list != null && list.Count > 0 ? list[Math.Min(i, list.Count - 1)] : fallback;

        public override BoundingBox ClippingBox =>
            _previewMeshes.Count == 0
                ? BoundingBox.Empty
                : new BoundingBox(_previewMeshes.SelectMany(m => m.Vertices.Select(v => (Point3d)v)));

        public override void DrawViewportMeshes(IGH_PreviewArgs args)
        {
            base.DrawViewportMeshes(args);
            for (int i = 0; i < _previewMeshes.Count; i++)
            {
                args.Display.DrawMeshShaded(_previewMeshes[i], _previewMaterials[i]);
            }
        }
    }
}
