#region Component Description
/*
Component: wsp_Ge13_Variable Curve Offset

Builds a planar curve offset from one uniform distance or a distance profile
mapped along normalized curve length. Variable offsets are constructed from
sampled curve normals; uniform offsets use the same normalized construction so
both modes have identical side and cap semantics.
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
    public sealed class wsp_Ge13_Variable_Curve_Offset : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Ge13_Variable_Curve_Offset()
            : base(
                "wsp_Ge13_Variable Curve Offset",
                "Variable Offset",
                "Creates a planar curve offset from a uniform distance or a distance profile mapped along the input curve.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "2.0_Geometry")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("D4E99E1C-4D4E-4E1A-9C18-4DA7A5C7B1F2");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ge13_Variable Curve Offset.png"))
                        return stream != null ? new Bitmap(stream) : null;
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "curve", "crv",
                "Planar curve to offset. For open curves, caps are applied only when both sides are requested.",
                GH_ParamAccess.item);

            pManager.AddPlaneParameter(
                "plane", "pln",
                "Plane used for offset normals and side orientation. Default is World XY. The curve must lie on this plane within document tolerance.",
                GH_ParamAccess.item,
                Plane.WorldXY);

            pManager.AddNumberParameter(
                "dist", "dist",
                "Offset distance profile. One value gives a uniform offset. Multiple values are mapped at evenly spaced normalized curve positions from 0 to 1 and linearly interpolated between them. Signed values select a side; with both=true, their absolute magnitudes are used on both sides.",
                GH_ParamAccess.list);

            pManager.AddBooleanParameter(
                "both", "both",
                "Generate both sides of the curve. False outputs one offset using the signed distance; true generates positive and negative offsets. For an open curve, caps > 0 combine the two sides into one closed boundary.",
                GH_ParamAccess.item,
                false);

            pManager.AddIntegerParameter(
                "kinks", "kinks",
                "Corner treatment for sampled variable offsets: 0 = Sharp (polyline-like corners), 1 = Round (interpolated corner transitions), 2 = Smooth (fully interpolated profile), 3 = Chamfer (straight transition faces). Closed curves ignore caps but still use this kink treatment.",
                GH_ParamAccess.item,
                0);

            pManager.AddIntegerParameter(
                "caps", "caps",
                "End-cap treatment for open curves when both=true: 0 = None (two open offsets), 1 = Linear (straight end connections), 2 = Tangency (tangent-guided smooth connections), 3 = Curvature (curvature-style smooth connections). Ignored for closed curves and when both=false.",
                GH_ParamAccess.item,
                0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "crv", "crv",
                "Offset curve(s). One curve for single-side mode; two curves for uncapped both-side mode; one closed boundary when both-side caps are applied.",
                GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            Curve curve = null;
            Plane plane = Plane.WorldXY;
            var distances = new List<double>();
            bool both = false;
            int kinkMode = 0;
            int capMode = 0;

            if (!DA.GetData("curve", ref curve) || curve == null || !curve.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Provide one valid curve.");
                return;
            }
            DA.GetData("plane", ref plane);
            DA.GetDataList("dist", distances);
            DA.GetData("both", ref both);
            DA.GetData("kinks", ref kinkMode);
            DA.GetData("caps", ref capMode);

            if (distances.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Provide at least one dist value.");
                return;
            }

            kinkMode = Math.Max(0, Math.Min(3, kinkMode));
            capMode = Math.Max(0, Math.Min(3, capMode));
            double tol = RhinoDoc.ActiveDoc != null
                ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
                : 0.01;

            if (plane.Normal.IsZero)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "plane must be valid.");
                return;
            }
            if (!CurveLiesOnPlane(curve, plane, tol))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The input curve must lie on the selected plane within document tolerance.");
                return;
            }

            int sampleCount = Math.Max(64, Math.Min(512, distances.Count * 32));
            bool closed = curve.IsClosed;
            var positive = BuildOffsetPoints(curve, plane, distances, sampleCount, false);
            var negative = both ? BuildOffsetPoints(curve, plane, distances, sampleCount, true) : null;

            if (positive == null || positive.Points.Count < 2 || (both && (negative == null || negative.Points.Count < 2)))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Could not construct a valid offset from the curve and distance profile.");
                return;
            }

            var output = new List<Curve>();
            Curve positiveCurve = BuildCurve(positive.Points, closed, kinkMode);
            if (positiveCurve == null || !positiveCurve.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The positive offset was invalid.");
                return;
            }

            if (!both)
            {
                output.Add(positiveCurve);
            }
            else
            {
                Curve negativeCurve = BuildCurve(negative.Points, closed, kinkMode);
                if (negativeCurve == null || !negativeCurve.IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The negative offset was invalid.");
                    return;
                }

                if (!closed && capMode > 0)
                {
                    Curve boundary = BuildCappedBoundary(positive, negative, capMode, kinkMode);
                    if (boundary == null || !boundary.IsValid)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "The capped boundary could not be created; returning the two open offsets instead.");
                        output.Add(positiveCurve);
                        output.Add(negativeCurve);
                    }
                    else output.Add(boundary);
                }
                else
                {
                    output.Add(positiveCurve);
                    output.Add(negativeCurve);
                }
            }

            DA.SetDataList("crv", output);
        }

        private static bool CurveLiesOnPlane(Curve curve, Plane plane, double tolerance)
        {
            double[] parameters = { curve.Domain.T0, curve.Domain.Mid, curve.Domain.T1 };
            foreach (double t in parameters)
                if (Math.Abs(plane.DistanceTo(curve.PointAt(t))) > tolerance * 2.0) return false;
            return true;
        }

        private sealed class OffsetSample
        {
            public readonly List<Point3d> Points;
            public readonly Vector3d StartTangent;
            public readonly Vector3d EndTangent;

            public OffsetSample(List<Point3d> points, Vector3d startTangent, Vector3d endTangent)
            {
                Points = points;
                StartTangent = startTangent;
                EndTangent = endTangent;
            }
        }

        private static OffsetSample BuildOffsetPoints(
            Curve curve,
            Plane plane,
            IReadOnlyList<double> distances,
            int sampleCount,
            bool negative)
        {
            bool closed = curve.IsClosed;
            int count = closed ? sampleCount : sampleCount + 1;
            var points = new List<Point3d>(count + (closed ? 1 : 0));
            Vector3d startTangent = Vector3d.Unset;
            Vector3d endTangent = Vector3d.Unset;

            for (int i = 0; i < count; i++)
            {
                double u = closed ? (double)i / sampleCount : (double)i / sampleCount;
                if (!curve.NormalizedLengthParameter(u, out double parameter))
                    parameter = curve.Domain.T0 + (curve.Domain.T1 - curve.Domain.T0) * u;

                Point3d point = curve.PointAt(parameter);
                Vector3d tangent = curve.TangentAt(parameter);
                if (!tangent.Unitize()) return null;
                if (i == 0) startTangent = tangent;
                if (i == count - 1 || (closed && i == count - 1)) endTangent = tangent;

                Vector3d offsetDirection = Vector3d.CrossProduct(plane.Normal, tangent);
                if (!offsetDirection.Unitize()) return null;

                double distance = InterpolateDistance(distances, u);
                if (negative) distance = -Math.Abs(distance);
                else distance = Math.Abs(distance);
                points.Add(point + offsetDirection * distance);
            }

            if (closed) points.Add(points[0]);
            return new OffsetSample(points, startTangent, endTangent);
        }

        private static double InterpolateDistance(IReadOnlyList<double> values, double u)
        {
            if (values.Count == 1) return values[0];
            double x = Math.Max(0.0, Math.Min(1.0, u)) * (values.Count - 1);
            int i = Math.Min(values.Count - 2, (int)Math.Floor(x));
            double f = x - i;
            return values[i] * (1.0 - f) + values[i + 1] * f;
        }

        private static Curve BuildCurve(IReadOnlyList<Point3d> points, bool closed, int kinkMode)
        {
            if (points == null || points.Count < 2) return null;
            if (closed || kinkMode == 0 || kinkMode == 3)
                return new PolylineCurve(points);

            var interpolated = Curve.CreateInterpolatedCurve(
                points,
                kinkMode == 1 ? 3 : 5,
                CurveKnotStyle.Chord);
            return interpolated ?? new PolylineCurve(points);
        }

        private static Curve BuildCappedBoundary(OffsetSample positive, OffsetSample negative, int capMode, int kinkMode)
        {
            var boundary = new List<Point3d>();
            var left = positive.Points;
            var right = negative.Points;
            for (int i = 0; i < left.Count; i++) boundary.Add(left[i]);

            AppendCap(boundary, left[left.Count - 1], right[right.Count - 1], positive.EndTangent, capMode, false);
            // AppendCap already ends at right[last]; skip that point here to
            // avoid consecutive duplicate vertices in the closed polyline.
            for (int i = right.Count - 2; i >= 0; i--) boundary.Add(right[i]);
            AppendCap(boundary, right[0], left[0], positive.StartTangent, capMode, true);

            if (!boundary[0].EpsilonEquals(boundary[boundary.Count - 1], RhinoMath.ZeroTolerance))
                boundary.Add(boundary[0]);
            return BuildCurve(boundary, true, kinkMode);
        }

        private static void AppendCap(List<Point3d> output, Point3d a, Point3d b, Vector3d tangent, int capMode, bool reverse)
        {
            if (capMode == 1)
            {
                output.Add(b);
                return;
            }

            Vector3d t = tangent;
            if (!t.Unitize())
            {
                output.Add(b);
                return;
            }
            if (reverse) t = -t;
            double length = a.DistanceTo(b);
            double handle = capMode == 2 ? length * 0.5 : length * 0.75;
            Point3d c1 = a + t * handle;
            Point3d c2 = b + t * handle;
            const int steps = 8;
            for (int i = 1; i <= steps; i++)
            {
                double u = (double)i / steps;
                output.Add(Cubic(a, c1, c2, b, u));
            }
        }

        private static Point3d Cubic(Point3d a, Point3d b, Point3d c, Point3d d, double u)
        {
            double v = 1.0 - u;
            double v2 = v * v;
            double u2 = u * u;
            return new Point3d(
                a.X * v2 * v + 3.0 * b.X * v2 * u + 3.0 * c.X * v * u2 + d.X * u2 * u,
                a.Y * v2 * v + 3.0 * b.Y * v2 * u + 3.0 * c.Y * v * u2 + d.Y * u2 * u,
                a.Z * v2 * v + 3.0 * b.Z * v2 * u + 3.0 * c.Z * v * u2 + d.Z * u2 * u);
        }
    }
}
