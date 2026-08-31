// WASPer_FrameSequence.cs
// Shared helpers for generating an ordered target frame sequence along a
// reference curve, and for distributing a known number of source layers along
// that curve.
//
// Extracted verbatim from wsp_Sl06_Orient Printing Paths so that the curve
// version (Sl06, Curve trees) and the packed version (Pp21, wsp_path) cannot
// drift apart. The math is intentionally container agnostic: callers pass the
// ordered source frame origins and receive normalized curve parameters and
// planes back.
//
// Frame convention: plane Z follows the reference-curve tangent, and the local
// XY orientation is transported from frame to frame (rotation minimizing), so
// consecutive plane-to-plane transforms do not introduce spurious roll.

using System;
using System.Collections.Generic;

using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP
{
    internal static class WasperFrameSequence
    {
        /// <summary>Distribution modes accepted by BuildDistributionParameters.</summary>
        public const int DistributionSourceSpacing = 0;
        public const int DistributionUniform = 1;
        public const int DistributionCurvatureWeighted = 2;

        /// <summary>Clamps a raw distribution input to the supported range.</summary>
        public static int ClampDistribution(int distribution)
        {
            if (distribution < DistributionSourceSpacing) return DistributionSourceSpacing;
            if (distribution > DistributionCurvatureWeighted) return DistributionCurvatureWeighted;
            return distribution;
        }

        public static string DistributionName(int distribution)
        {
            switch (distribution)
            {
                case DistributionUniform: return "1 uniform";
                case DistributionCurvatureWeighted: return "2 curvature weighted";
                default: return "0 source spacing ratios";
            }
        }

        /// <summary>
        /// Normalized 0..1 reference-curve parameters, one per source frame, in
        /// source order. sourceOrigins is only consulted by the source-spacing mode.
        /// </summary>
        public static List<double> BuildDistributionParameters(
            IList<Point3d> sourceOrigins,
            Curve refCurve,
            int distribution,
            double curvWeight,
            double tol)
        {
            int count = sourceOrigins != null ? sourceOrigins.Count : 0;
            if (distribution == DistributionSourceSpacing)
                return BuildSourceSpacingParameters(sourceOrigins);
            if (distribution == DistributionCurvatureWeighted && curvWeight > 0.0)
                return BuildCurvatureWeightedParameters(refCurve, count, curvWeight, tol);
            return BuildIndexParameters(count);
        }

        /// <summary>
        /// Preserves the relative spacing measured between consecutive source
        /// frame origins. Degenerate stacks fall back to uniform spacing.
        /// </summary>
        public static List<double> BuildSourceSpacingParameters(IList<Point3d> sourceOrigins)
        {
            int n = sourceOrigins != null ? sourceOrigins.Count : 0;
            var result = new List<double>(Math.Max(0, n));
            if (n <= 0) return result;
            if (n == 1)
            {
                result.Add(0.0);
                return result;
            }

            var cumulative = new double[n];
            for (int i = 1; i < n; i++)
            {
                Point3d a = sourceOrigins[i - 1];
                Point3d b = sourceOrigins[i];
                cumulative[i] = cumulative[i - 1] + a.DistanceTo(b);
            }

            double total = cumulative[n - 1];
            if (total <= 1e-12)
                return BuildIndexParameters(n);

            for (int i = 0; i < n; i++)
                result.Add(cumulative[i] / total);
            return result;
        }

        /// <summary>Evenly spaced normalized parameters, 0..1 inclusive.</summary>
        public static List<double> BuildIndexParameters(int count)
        {
            var result = new List<double>(Math.Max(0, count));
            if (count <= 0) return result;
            if (count == 1)
            {
                result.Add(0.0);
                return result;
            }

            for (int i = 0; i < count; i++)
                result.Add((double)i / (double)(count - 1));
            return result;
        }

        /// <summary>
        /// Places more frames where the reference curve bends more. weight = 0
        /// behaves like uniform spacing.
        /// </summary>
        public static List<double> BuildCurvatureWeightedParameters(
            Curve curve,
            int count,
            double weight,
            double tol)
        {
            if (count <= 1 || curve == null)
                return BuildIndexParameters(count);

            int samples = Math.Max(64, Math.Min(1000, count * 24));
            double length = curve.GetLength();
            if (length <= tol)
                return BuildIndexParameters(count);

            var t = new double[samples + 1];
            var ds = new double[samples + 1];
            var curv = new double[samples + 1];
            double maxCurv = 0.0;

            for (int i = 0; i <= samples; i++)
            {
                double s = length * i / samples;
                if (!curve.LengthParameter(s, out t[i]))
                    t[i] = curve.Domain.ParameterAt((double)i / samples);
                Vector3d k = curve.CurvatureAt(t[i]);
                curv[i] = k.IsValid ? k.Length : 0.0;
                if (curv[i] > maxCurv) maxCurv = curv[i];
                if (i > 0)
                    ds[i] = curve.PointAt(t[i]).DistanceTo(curve.PointAt(t[i - 1]));
            }

            var metric = new double[samples + 1];
            for (int i = 1; i <= samples; i++)
            {
                double c0 = maxCurv > 0.0 ? curv[i - 1] / maxCurv : 0.0;
                double c1 = maxCurv > 0.0 ? curv[i] / maxCurv : 0.0;
                double factor = 1.0 + weight * 0.5 * (c0 + c1);
                metric[i] = metric[i - 1] + ds[i] * factor;
            }

            double total = metric[samples];
            if (total <= tol)
                return BuildIndexParameters(count);

            var result = new List<double>(count);
            for (int i = 0; i < count; i++)
            {
                double target = total * i / (count - 1);
                int hi = Array.BinarySearch(metric, target);
                if (hi < 0) hi = ~hi;
                hi = Math.Max(1, Math.Min(samples, hi));
                int lo = hi - 1;
                double span = metric[hi] - metric[lo];
                double f = span <= 1e-12 ? 0.0 : (target - metric[lo]) / span;
                double sNorm = ((lo + f) / samples);
                result.Add(Math.Max(0.0, Math.Min(1.0, sNorm)));
            }
            return result;
        }

        /// <summary>
        /// Builds one plane per normalized parameter. Origins lie on the curve,
        /// Z follows the tangent, and the XY orientation is transported along the
        /// curve from the first frame, which is seeded from the up vector.
        /// </summary>
        public static List<Plane> BuildFramesOnCurve(
            Curve curve,
            IList<double> normalized,
            Vector3d up,
            double tol)
        {
            int count = normalized != null ? normalized.Count : 0;
            var frames = new List<Plane>(Math.Max(0, count));
            if (curve == null || count == 0)
                return frames;

            Vector3d previousY = Vector3d.Unset;
            Vector3d previousZ = Vector3d.Unset;
            double length = curve.GetLength();

            for (int i = 0; i < count; i++)
            {
                double u = Math.Max(0.0, Math.Min(1.0, normalized[i]));
                double targetLength = length * u;
                double t;
                if (!curve.LengthParameter(targetLength, out t))
                    t = curve.Domain.ParameterAt(u);

                Point3d origin = curve.PointAt(t);
                Vector3d z = curve.TangentAt(t);
                if (!z.IsValid || z.Length <= tol)
                    z = previousZ.IsValid ? previousZ : Vector3d.ZAxis;
                z.Unitize();

                Vector3d y;
                if (previousY.IsValid && previousZ.IsValid)
                {
                    y = TransportVector(previousY, previousZ, z, tol);
                    y = ProjectToPlane(y, z);
                    if (!y.IsValid || y.Length <= tol)
                        y = ProjectToPlane(up, z);
                }
                else
                {
                    y = ProjectToPlane(up, z);
                }

                if (!y.IsValid || y.Length <= tol)
                    y = StablePerpendicular(z);
                y.Unitize();

                Vector3d x = Vector3d.CrossProduct(y, z);
                if (!x.IsValid || x.Length <= tol)
                    x = StablePerpendicular(z);
                x.Unitize();

                y = Vector3d.CrossProduct(z, x);
                if (!y.IsValid || y.Length <= tol)
                    y = ProjectToPlane(up, z);
                if (!y.IsValid || y.Length <= tol)
                    y = StablePerpendicular(z);
                y.Unitize();

                Plane plane = new Plane(origin, x, y);
                if (!plane.IsValid)
                {
                    plane = Plane.WorldXY;
                    plane.Origin = origin;
                }

                frames.Add(plane);
                previousY = plane.YAxis;
                previousZ = plane.ZAxis;
            }

            return frames;
        }

        /// <summary>
        /// Curve parameter at a normalized 0..1 arc-length position, using the
        /// same resolution rule as BuildFramesOnCurve. Pass the curve length when
        /// it is already known to avoid recomputing it.
        /// </summary>
        public static double CurveParameterAtNormalized(Curve curve, double u, double length)
        {
            if (curve == null)
                return 0.0;

            u = Math.Max(0.0, Math.Min(1.0, u));
            if (length <= 0.0)
                length = curve.GetLength();

            double t;
            if (!curve.LengthParameter(length * u, out t))
                t = curve.Domain.ParameterAt(u);
            return t;
        }

        /// <summary>
        /// Local radius of curvature of the reference curve at a curve parameter,
        /// or double.PositiveInfinity where the curve is locally straight.
        /// </summary>
        public static double CurvatureRadiusAt(Curve curve, double t)
        {
            if (curve == null)
                return double.PositiveInfinity;

            Vector3d k = curve.CurvatureAt(t);
            if (!k.IsValid)
                return double.PositiveInfinity;

            double magnitude = k.Length;
            return magnitude <= 1e-12 ? double.PositiveInfinity : 1.0 / magnitude;
        }

        /// <summary>
        /// Rotates each frame about its own Z axis. One twist value is global;
        /// several values cycle by frame order.
        /// </summary>
        public static void ApplyTwist(IList<Plane> planes, IList<double> twistDeg)
        {
            if (planes == null || twistDeg == null || twistDeg.Count == 0)
                return;

            for (int i = 0; i < planes.Count; i++)
            {
                double deg = twistDeg[i % twistDeg.Count];
                if (double.IsNaN(deg) || Math.Abs(deg) <= 1e-12) continue;

                Plane plane = planes[i];
                plane.Rotate(RhinoMath.ToRadians(deg), plane.ZAxis, plane.Origin);
                planes[i] = plane;
            }
        }

        /// <summary>
        /// Sanitizes an up vector input: invalid or degenerate vectors fall back
        /// to World Z, and the result is unitized.
        /// </summary>
        public static Vector3d SanitizeUpVector(Vector3d up)
        {
            if (!up.IsValid || up.Length <= 1e-12)
                up = Vector3d.ZAxis;
            up.Unitize();
            return up;
        }

        /// <summary>
        /// Parallel transports a vector from one tangent direction to another by
        /// the minimal rotation between them.
        /// </summary>
        public static Vector3d TransportVector(Vector3d vector, Vector3d fromZ, Vector3d toZ, double tol)
        {
            if (!vector.IsValid || !fromZ.IsValid || !toZ.IsValid)
                return vector;

            fromZ.Unitize();
            toZ.Unitize();

            Vector3d axis = Vector3d.CrossProduct(fromZ, toZ);
            double axisLength = axis.Length;
            double dot = Math.Max(-1.0, Math.Min(1.0, fromZ * toZ));

            if (axisLength <= tol)
            {
                if (dot > 0.0)
                    return vector;

                Vector3d fallbackAxis = StablePerpendicular(fromZ);
                var halfTurn = Transform.Rotation(Math.PI, fallbackAxis, Point3d.Origin);
                Vector3d reversed = vector;
                reversed.Transform(halfTurn);
                return reversed;
            }

            axis /= axisLength;
            double angle = Math.Atan2(axisLength, dot);
            var rotation = Transform.Rotation(angle, axis, Point3d.Origin);
            Vector3d result = vector;
            result.Transform(rotation);
            return result;
        }

        /// <summary>Removes the normal component of a vector.</summary>
        public static Vector3d ProjectToPlane(Vector3d vector, Vector3d normal)
        {
            if (!vector.IsValid || !normal.IsValid)
                return Vector3d.Unset;

            Vector3d n = normal;
            if (!n.Unitize())
                return Vector3d.Unset;

            return vector - n * (vector * n);
        }

        /// <summary>Any unit vector perpendicular to z, chosen without degeneracy.</summary>
        public static Vector3d StablePerpendicular(Vector3d z)
        {
            Vector3d n = z;
            if (!n.IsValid || !n.Unitize())
                n = Vector3d.ZAxis;

            Vector3d seed = Math.Abs(n * Vector3d.ZAxis) < 0.9 ? Vector3d.ZAxis : Vector3d.XAxis;
            Vector3d result = ProjectToPlane(seed, n);
            if (result.IsValid && result.Length > 1e-12)
                return result;

            seed = Math.Abs(n * Vector3d.YAxis) < 0.9 ? Vector3d.YAxis : Vector3d.XAxis;
            result = ProjectToPlane(seed, n);
            return result.IsValid && result.Length > 1e-12 ? result : Vector3d.XAxis;
        }
    }
}
