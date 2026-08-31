using System;
using System.Collections.Generic;
using System.Linq;

using Newtonsoft.Json;
using Rhino.Geometry;

namespace WASPer_3DP.PatternEditing
{
    /// <summary>
    /// Versioned seam intent carried by a shell curve. The canonical pre-seam
    /// polyline is retained so an intentionally open X-seam can be edited again
    /// without accumulating transformations or guessing the original loop.
    /// </summary>
    public sealed class WasperShellSeamRecord
    {
        public int SchemaVersion { get; set; } = WasperShellSeamMetadata.CurrentSchemaVersion;
        public double SeamU { get; set; }
        public bool XSeam { get; set; }
        public double StartOffset { get; set; }
        public double EndOffset { get; set; }
        public double StartTangentialOffset { get; set; }
        public double EndTangentialOffset { get; set; }
        public double FilletRadius { get; set; }
        public bool AppliedToGeometry { get; set; }
        public List<double[]> BasePoints { get; set; } = new List<double[]>();

        public WasperShellSeamSettings ToSettings()
        {
            return new WasperShellSeamSettings
            {
                SeamU = SeamU,
                XSeam = XSeam,
                StartOffset = StartOffset,
                EndOffset = EndOffset,
                StartTangentialOffset = StartTangentialOffset,
                EndTangentialOffset = EndTangentialOffset,
                FilletRadius = FilletRadius
            };
        }

        public PolylineCurve CreateBaseCurve()
        {
            if (BasePoints == null || BasePoints.Count < 2)
                return null;

            var points = new List<Point3d>(BasePoints.Count);
            foreach (double[] xyz in BasePoints)
            {
                if (xyz == null || xyz.Length < 3 ||
                    !IsFinite(xyz[0]) || !IsFinite(xyz[1]) || !IsFinite(xyz[2]))
                    return null;
                points.Add(new Point3d(xyz[0], xyz[1], xyz[2]));
            }
            return new PolylineCurve(points);
        }

        private static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }

    /// <summary>
    /// Shared Rhino-curve metadata bridge for transferring shell-seam intent
    /// between curve-producing components and WasperPrintPath construction.
    /// </summary>
    public static class WasperShellSeamMetadata
    {
        public const int CurrentSchemaVersion = 1;
        public const string MetadataKey = "WASPer.ShellSeam";

        public static void Set(
            Curve effectiveCurve,
            Curve canonicalBaseCurve,
            WasperShellSeamSettings settings,
            bool appliedToGeometry)
        {
            if (effectiveCurve == null || canonicalBaseCurve == null || settings == null)
                return;

            Polyline basePolyline;
            if (!canonicalBaseCurve.TryGetPolyline(out basePolyline))
                basePolyline = SampleCurve(canonicalBaseCurve);
            if (basePolyline == null || basePolyline.Count < 2)
                return;

            var record = new WasperShellSeamRecord
            {
                SeamU = Wrap01(settings.SeamU),
                XSeam = settings.XSeam,
                StartOffset = settings.StartOffset,
                EndOffset = settings.EndOffset,
                StartTangentialOffset = settings.StartTangentialOffset,
                EndTangentialOffset = settings.EndTangentialOffset,
                FilletRadius = Math.Max(0.0, settings.FilletRadius),
                AppliedToGeometry = appliedToGeometry
            };
            foreach (Point3d point in basePolyline)
                record.BasePoints.Add(new[] { point.X, point.Y, point.Z });

            effectiveCurve.SetUserString(
                MetadataKey,
                JsonConvert.SerializeObject(record, Formatting.None));
        }

        public static bool TryGet(Curve curve, out WasperShellSeamRecord record)
        {
            record = null;
            if (curve == null)
                return false;

            string json = curve.GetUserString(MetadataKey);
            if (string.IsNullOrWhiteSpace(json))
                return false;
            try
            {
                WasperShellSeamRecord parsed =
                    JsonConvert.DeserializeObject<WasperShellSeamRecord>(json);
                if (parsed == null || parsed.SchemaVersion <= 0 ||
                    parsed.SchemaVersion > CurrentSchemaVersion ||
                    parsed.CreateBaseCurve() == null)
                    return false;
                parsed.SeamU = Wrap01(parsed.SeamU);
                parsed.FilletRadius = Math.Max(0.0, parsed.FilletRadius);
                record = parsed;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void Copy(Curve source, Curve target)
        {
            if (source == null || target == null)
                return;
            string json = source.GetUserString(MetadataKey);
            if (!string.IsNullOrWhiteSpace(json))
                target.SetUserString(MetadataKey, json);
        }

        public static PolylineCurve Apply(
            Curve canonicalBaseCurve,
            WasperShellSeamSettings settings,
            Plane layerPlane,
            double tolerance)
        {
            if (canonicalBaseCurve == null || !canonicalBaseCurve.IsValid)
                return null;
            double tol = Math.Max(1e-9, tolerance);
            try
            {
                Curve curve = canonicalBaseCurve.DuplicateCurve();
                if (settings == null ||
                    (!settings.XSeam && Math.Abs(Wrap01(settings.SeamU)) <= 1e-12))
                    return AsPolylineCurve(curve);

                // Metadata stores a sampled canonical polyline. Rhino can report that
                // loop as open after serialization even when its end points still agree
                // within document tolerance. Treat that as the same printable loop and
                // normalize the duplicate before applying either a regular or X seam.
                // Genuinely open shell paths must remain untouched.
                if (!curve.IsClosed)
                {
                    PolylineCurve sampled = AsPolylineCurve(curve);
                    Polyline polyline = sampled?.ToPolyline();
                    if (polyline == null || polyline.Count < 3 ||
                        polyline[0].DistanceTo(polyline[polyline.Count - 1]) > tol)
                        return sampled;
                    polyline[polyline.Count - 1] = polyline[0];
                    curve = new PolylineCurve(polyline);
                }

                double seamU = Wrap01(settings.SeamU);
                if (!curve.NormalizedLengthParameter(seamU, out double seamT))
                    seamT = curve.Domain.ParameterAt(seamU);
                if (!settings.XSeam)
                {
                    curve.ChangeClosedCurveSeam(seamT);
                    return AsPolylineCurve(curve);
                }

                double length = curve.GetLength();
                if (length <= tol)
                    return AsPolylineCurve(curve);
                double du = Math.Min(0.24, Math.Max(0.0, settings.FilletRadius) / length);
                double startU = Wrap01(seamU + du);
                double endU = Wrap01(seamU - du);
                Point3d seamPoint = PointAtLength(curve, length, seamU);
                Point3d attachStart = PointAtLength(curve, length, startU);
                Point3d attachEnd = PointAtLength(curve, length, endU);

                Vector3d tangent = curve.TangentAt(seamT);
                if (!tangent.Unitize()) tangent = layerPlane.XAxis;
                Vector3d normal = layerPlane.Normal;
                if (!normal.Unitize()) normal = Vector3d.ZAxis;
                Vector3d inward = Vector3d.CrossProduct(normal, tangent);
                if (!inward.Unitize()) inward = layerPlane.YAxis;
                Polyline basePolyline = AsPolylineCurve(curve).ToPolyline();
                Point3d center = basePolyline.Count > 0
                    ? new Point3d(
                        basePolyline.Average(point => point.X),
                        basePolyline.Average(point => point.Y),
                        basePolyline.Average(point => point.Z))
                    : curve.GetBoundingBox(false).Center;
                if (Vector3d.Multiply(center - seamPoint, inward) < 0.0)
                    inward.Reverse();

                Point3d startControl = seamPoint +
                    inward * settings.StartOffset +
                    tangent * settings.StartTangentialOffset;
                Point3d endControl = seamPoint +
                    inward * settings.EndOffset +
                    tangent * settings.EndTangentialOffset;
                int samples = Math.Max(16, basePolyline.Count);
                double travel = Math.Max(0.0, 1.0 - 2.0 * du);
                var points = new List<Point3d>(samples + 4) { startControl, attachStart };
                for (int i = 1; i < samples; i++)
                    points.Add(PointAtLength(curve, length, Wrap01(startU + travel * i / samples)));
                points.Add(attachEnd);
                points.Add(endControl);

                var clean = new List<Point3d>();
                foreach (Point3d point in points)
                    if (point.IsValid && (clean.Count == 0 || point.DistanceTo(clean[clean.Count - 1]) > tol))
                        clean.Add(point);
                return clean.Count >= 2 ? new PolylineCurve(clean) : AsPolylineCurve(curve);
            }
            catch
            {
                return AsPolylineCurve(canonicalBaseCurve);
            }
        }

        private static Polyline SampleCurve(Curve curve)
        {
            if (curve == null || !curve.IsValid)
                return null;
            int count = 64;
            var polyline = new Polyline(count + 1);
            for (int i = 0; i <= count; i++)
                polyline.Add(curve.PointAtNormalizedLength((double)i / count));
            return polyline;
        }

        private static PolylineCurve AsPolylineCurve(Curve curve)
        {
            if (curve == null) return null;
            if (curve.TryGetPolyline(out Polyline polyline))
                return new PolylineCurve(polyline);
            return new PolylineCurve(SampleCurve(curve));
        }

        private static Point3d PointAtLength(Curve curve, double length, double u)
        {
            double clamped = Math.Max(0.0, Math.Min(1.0, u));
            if (!curve.LengthParameter(clamped * length, out double parameter))
                parameter = curve.Domain.ParameterAt(clamped);
            return curve.PointAt(parameter);
        }

        private static double Wrap01(double value)
        {
            value -= Math.Floor(value);
            return value < 0.0 ? value + 1.0 : value;
        }
    }
}
