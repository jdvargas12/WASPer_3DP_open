#region Component Description
/*
Component: wsp_Ge12_Rebar Cavities from Curves

Generates a normalized cavity grid between exactly two co-planar base curves.
The curves define the component base: tangent direction follows the curves and
normal direction interpolates from curve A to curve B. Cavity solids are
extruded from that base plane by height.
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
    public sealed class wsp_Ge12_Rebar_Cavities_From_Curves : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Ge12_Rebar_Cavities_From_Curves()
            : base(
                "wsp_Ge12_Rebar Cavities from Curves",
                "Rebar Cavity 2C",
                "Generates circular or square rebar cavity solids between exactly two co-planar component base curves.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "2.0_Geometry")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("A8A8E53A-2B57-4E16-9DB8-4F5E5E7C2A91");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ge12_Rebar Cavities from Curves.png"))
                        return stream != null ? new Bitmap(stream) : null;
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "base_curves", "curves",
                "Exactly two valid co-planar curves defining the component base. Curve 0 and curve 1 are the two boundaries of the strip.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter(
                "cx", "cx",
                "Number of intervals in the tangent direction, following the normalized length of both base curves. Mode mx=0 gives cx tangent positions; mx=1 gives cx+1 tangent boundary positions.",
                GH_ParamAccess.item, 1);

            pManager.AddIntegerParameter(
                "cy", "cy",
                "Number of intervals in the normal direction, interpolating from base curve 0 to base curve 1. Mode my=0 gives cy positions between the curves; my=1 includes both boundary curves, giving cy+1 positions.",
                GH_ParamAccess.item, 1);

            pManager.AddIntegerParameter(
                "mx", "mx",
                "Tangent placement mode: 0 = centres of tangent intervals; 1 = tangent interval boundaries, including both ends.",
                GH_ParamAccess.item, 0);

            pManager.AddIntegerParameter(
                "my", "my",
                "Normal placement mode: 0 = centres of intervals between the two base curves; 1 = interval boundaries, including both base curves.",
                GH_ParamAccess.item, 0);

            pManager.AddBooleanParameter(
                "cull_pat", "cull",
                "Optional keep-pattern, evaluated row-major with tangent position changing fastest. True keeps a cavity; False culls it. Pattern length must equal the generated tangent-position count multiplied by the normal-position count.",
                GH_ParamAccess.list);
            pManager[5].Optional = true;

            pManager.AddNumberParameter(
                "diam", "diam",
                "Nominal rebar diameter in model units.",
                GH_ParamAccess.item, 12.0);

            pManager.AddNumberParameter(
                "height", "height",
                "Cavity extrusion height normal to the co-planar base curves, starting at the base plane.",
                GH_ParamAccess.item, 100.0);

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
                "Curve validation, grid, culling, dimensions, and retained cavity summary.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            var curves = new List<Curve>();
            int cx = 1, cy = 1, mx = 0, my = 0, type = 0;
            double diam = 12.0, height = 100.0, pathWidth = 0.0;
            var pattern = new List<bool>();

            if (!DA.GetDataList("base_curves", curves) || curves.Count != 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Provide exactly two base_curves. Only two co-planar curves are accepted.");
                return;
            }

            DA.GetData("cx", ref cx);
            DA.GetData("cy", ref cy);
            DA.GetData("mx", ref mx);
            DA.GetData("my", ref my);
            DA.GetDataList("cull_pat", pattern);
            DA.GetData("diam", ref diam);
            DA.GetData("height", ref height);
            DA.GetData("p_path_w", ref pathWidth);
            DA.GetData("type", ref type);

            if (curves.Any(c => c == null || !c.IsValid || c.GetLength() <= RhinoMath.ZeroTolerance))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Both base_curves must be valid and have non-zero length.");
                return;
            }
            if (!curves[0].TryGetPlane(out Plane plane0) || !curves[1].TryGetPlane(out Plane plane1))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Both base_curves must be planar.");
                return;
            }

            double tol = RhinoDoc.ActiveDoc != null
                ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
                : 0.01;
            if (!AreCoPlanar(curves, plane0, plane1, tol))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The two base_curves must be co-planar.");
                return;
            }

            Curve curveA = curves[0].DuplicateCurve();
            Curve curveB = curves[1].DuplicateCurve();
            Vector3d tangentA = curveA.TangentAt(curveA.Domain.T0);
            Vector3d tangentB = curveB.TangentAt(curveB.Domain.T0);
            bool reversed = tangentA.IsValid && tangentB.IsValid && Vector3d.Multiply(tangentA, tangentB) < 0.0;
            if (reversed) curveB.Reverse();

            if (cx < 1 || cy < 1)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "cx and cy must both be at least 1.");
                return;
            }
            if (diam <= RhinoMath.ZeroTolerance || height <= RhinoMath.ZeroTolerance)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "diam and height must both be greater than zero.");
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
                    $"cull_pat must contain exactly {candidateCount} Boolean values for the current cx/cy/mx/my settings. True keeps; False culls; tangent changes fastest.");
                return;
            }

            double envelope = diam + 2.0 * pathWidth;
            var cavities = new List<Brep>();
            int kept = 0;
            int index = 0;

            for (int iy = 0; iy < ny; iy++)
            for (int ix = 0; ix < nx; ix++, index++)
            {
                if (pattern.Count > 0 && !pattern[index]) continue;

                double u = mx == 0 ? (ix + 0.5) / cx : (double)ix / cx;
                double v = my == 0 ? (iy + 0.5) / cy : (double)iy / cy;
                Point3d a = PointAtNormalizedLength(curveA, u);
                Point3d b = PointAtNormalizedLength(curveB, u);
                Point3d center = a + (b - a) * v;

                Brep cavity = type == 0
                    ? CreateCircularCavity(plane0, center, envelope * 0.5, height)
                    : CreateSquareCavity(plane0, center, envelope, height);
                if (cavity != null && cavity.IsValid)
                {
                    cavities.Add(cavity);
                    kept++;
                }
            }

            DA.SetDataList("cavity_geo", cavities);
            DA.SetData(
                "summary",
                $"2 co-planar base curves; grid {nx} x {ny}; candidates={candidateCount}; kept={kept}; culled={candidateCount - kept}; " +
                $"cx=tangent; cy=normal; modeX={mx}; modeY={my}; type={(type == 0 ? "circle" : "square")}; " +
                $"diam={diam:0.###}; height={height:0.###}; p_path_w={pathWidth:0.###}; envelope={envelope:0.###}; " +
                $"cull_pat=True keeps, False culls, tangent/X changes fastest; curve1_reversed={reversed}.");
        }

        private static bool AreCoPlanar(IReadOnlyList<Curve> curves, Plane p0, Plane p1, double tolerance)
        {
            if (Math.Abs(Vector3d.Multiply(p0.ZAxis, p1.ZAxis)) < 1.0 - 1e-8) return false;
            for (int i = 0; i < curves.Count; i++)
            {
                double[] samples = { curves[i].Domain.T0, curves[i].Domain.Mid, curves[i].Domain.T1 };
                foreach (double t in samples)
                    if (Math.Abs(p0.DistanceTo(curves[i].PointAt(t))) > tolerance * 2.0) return false;
            }
            return true;
        }

        private static Point3d PointAtNormalizedLength(Curve curve, double t01)
        {
            t01 = Math.Max(0.0, Math.Min(1.0, t01));
            if (!curve.NormalizedLengthParameter(t01, out double parameter))
                parameter = curve.Domain.T0 + (curve.Domain.T1 - curve.Domain.T0) * t01;
            return curve.PointAt(parameter);
        }

        private static Brep CreateCircularCavity(Plane basePlane, Point3d origin, double radius, double height)
        {
            Plane cavityPlane = basePlane;
            cavityPlane.Origin = origin;
            return Brep.CreateFromCylinder(new Cylinder(new Circle(cavityPlane, radius), height), true, true);
        }

        private static Brep CreateSquareCavity(Plane basePlane, Point3d origin, double side, double height)
        {
            Plane cavityPlane = basePlane;
            cavityPlane.Origin = origin;
            double half = side * 0.5;
            return Brep.CreateFromBox(new Box(
                cavityPlane,
                new Interval(-half, half),
                new Interval(-half, half),
                new Interval(0.0, height)));
        }
    }
}
