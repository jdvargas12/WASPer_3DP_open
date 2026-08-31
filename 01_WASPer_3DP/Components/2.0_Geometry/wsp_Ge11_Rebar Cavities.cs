#region Component Description
/*
Component: wsp_Ge11_Rebar Cavities

Generates a normalized XY grid of rebar cavity solids inside a reference Box.
The cavity envelope is nominal rebar diameter plus two printing-path widths.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;

using Grasshopper.Kernel;
using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP.Components._3_Geometry
{
    public sealed class wsp_Ge11_Rebar_Cavities : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Ge11_Rebar_Cavities()
            : base(
                "wsp_Ge11_Rebar Cavities",
                "Rebar Cavity",
                "Generates circular or square rebar cavity solids on a normalized XY grid inside a reference bounding box.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "2.0_Geometry")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("F2C0B2A1-7A33-4A4B-9D9F-8A6B6A0B11E4");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ge11_Rebar Cavities.png"))
                        return stream != null ? new Bitmap(stream) : null;
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBoxParameter(
                "b_box", "box",
                "Reference bounding box. Its local X/Y directions define the normalized grid and local Z defines cavity depth.",
                GH_ParamAccess.item);

            pManager.AddIntegerParameter(
                "cx", "cx",
                "Number of X intervals. Must be at least 1.",
                GH_ParamAccess.item, 1);

            pManager.AddIntegerParameter(
                "cy", "cy",
                "Number of Y intervals. Must be at least 1.",
                GH_ParamAccess.item, 1);

            pManager.AddIntegerParameter(
                "mx", "mx",
                "X placement mode: 0 = interval centres, producing cx positions; 1 = interval boundaries, producing cx + 1 positions.",
                GH_ParamAccess.item, 0);

            pManager.AddIntegerParameter(
                "my", "my",
                "Y placement mode: 0 = interval centres, producing cy positions; 1 = interval boundaries, producing cy + 1 positions.",
                GH_ParamAccess.item, 0);

            pManager.AddBooleanParameter(
                "cull_pat", "cull",
                "Optional keep-pattern, evaluated row-major with X changing fastest. True keeps a cavity; False culls it. " +
                "For cx=3, cy=2, mx=0, my=0 use [True,False,True, True,True,False]. Pattern length must equal the generated position count.",
                GH_ParamAccess.list);
            pManager[5].Optional = true;

            pManager.AddNumberParameter(
                "diam", "diam",
                "Nominal rebar diameter in model units.",
                GH_ParamAccess.item, 12.0);

            pManager.AddNumberParameter(
                "p_path_w", "p_w",
                "Printing-path width reserved radially on each side of the rebar. Cavity diameter/side = diam + 2 * p_path_w.",
                GH_ParamAccess.item, 0.0);

            pManager.AddIntegerParameter(
                "type", "type",
                "Cavity section type: 0 = circle; 1 = square.",
                GH_ParamAccess.item, 0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddBrepParameter(
                "cavity_geo", "cavity",
                "Capped cavity solids to subtract from the printable component.",
                GH_ParamAccess.list);

            pManager.AddTextParameter(
                "summary", "summary",
                "Grid, culling, dimensions, and retained cavity summary.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            Box box = Box.Unset;
            int cx = 1, cy = 1, mx = 0, my = 0, type = 0;
            double diam = 12.0, pathWidth = 0.0;
            var pattern = new List<bool>();

            if (!DA.GetData("b_box", ref box) || !box.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Provide a valid b_box.");
                return;
            }

            DA.GetData("cx", ref cx);
            DA.GetData("cy", ref cy);
            DA.GetData("mx", ref mx);
            DA.GetData("my", ref my);
            DA.GetDataList("cull_pat", pattern);
            DA.GetData("diam", ref diam);
            DA.GetData("p_path_w", ref pathWidth);
            DA.GetData("type", ref type);

            if (cx < 1 || cy < 1)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "cx and cy must both be at least 1.");
                return;
            }
            if (diam <= RhinoMath.ZeroTolerance)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "diam must be greater than zero.");
                return;
            }

            mx = mx == 1 ? 1 : 0;
            my = my == 1 ? 1 : 0;
            type = type == 1 ? 1 : 0;
            pathWidth = Math.Max(0.0, pathWidth);

            int nx = mx == 1 ? cx + 1 : cx;
            int ny = my == 1 ? cy + 1 : cy;
            int candidateCount = checked(nx * ny);

            if (pattern.Count > 0 && pattern.Count != candidateCount)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    $"cull_pat must contain exactly {candidateCount} Boolean values for the current cx/cy/mx/my settings. " +
                    "True keeps a cavity; False culls it; X changes fastest.");
                return;
            }

            double envelope = diam + 2.0 * pathWidth;
            double tol = RhinoDoc.ActiveDoc != null
                ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
                : 0.01;
            double depth = Math.Abs(box.Z.Length);
            if (depth <= tol)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The reference b_box must have a non-zero Z depth.");
                return;
            }

            var cavities = new List<Brep>();
            int kept = 0;
            int index = 0;

            for (int iy = 0; iy < ny; iy++)
            for (int ix = 0; ix < nx; ix++, index++)
            {
                bool keep = pattern.Count == 0 || pattern[index];
                if (!keep) continue;

                double u = mx == 0 ? (ix + 0.5) / cx : (double)ix / cx;
                double v = my == 0 ? (iy + 0.5) / cy : (double)iy / cy;
                double x = box.X.T0 + u * box.X.Length;
                double y = box.Y.T0 + v * box.Y.Length;
                double z0 = box.Z.T0;
                Point3d origin = box.Plane.PointAt(x, y, z0);

                Brep cavity = type == 0
                    ? CreateCircularCavity(box.Plane, origin, envelope * 0.5, depth)
                    : CreateSquareCavity(box.Plane, origin, envelope, depth);

                if (cavity != null && cavity.IsValid)
                {
                    cavities.Add(cavity);
                    kept++;
                }
            }

            DA.SetDataList("cavity_geo", cavities);
            DA.SetData(
                "summary",
                $"Grid {nx} x {ny}; candidates={candidateCount}; kept={kept}; culled={candidateCount - kept}; " +
                $"modeX={mx}; modeY={my}; type={(type == 0 ? "circle" : "square")}; " +
                $"diam={diam:0.###}; p_path_w={pathWidth:0.###}; envelope={envelope:0.###}; " +
                "cull_pat=True keeps, False culls, row-major/X-fastest.");
        }

        private static Brep CreateCircularCavity(Plane boxPlane, Point3d origin, double radius, double depth)
        {
            Plane basePlane = boxPlane;
            basePlane.Origin = origin;
            var circle = new Circle(basePlane, radius);
            return Brep.CreateFromCylinder(new Cylinder(circle, depth), true, true);
        }

        private static Brep CreateSquareCavity(Plane boxPlane, Point3d origin, double side, double depth)
        {
            Plane basePlane = boxPlane;
            basePlane.Origin = origin;
            double half = side * 0.5;
            var squareBox = new Box(
                basePlane,
                new Interval(-half, half),
                new Interval(-half, half),
                new Interval(0.0, depth));
            return Brep.CreateFromBox(squareBox);
        }
    }
}
