// WASPer_FieldOps.cs
// WASPer_3DP - Shared procedural operations for WasperField wrappers.

using System;
using System.Collections.Generic;
using Rhino.Geometry;

namespace WASPer_3DP
{
    public enum WasperFieldBooleanOperation
    {
        Union = 1,
        Difference = 2,
        Intersection = 3,
        Xor = 4,
        Invert = 5
    }

    /// <summary>
    /// Lightweight procedural operations for WasperField objects.
    /// These methods return evaluator wrappers and do not sample dense grids.
    /// </summary>
    public static class WasperFieldOps
    {
        public static WasperField Transform(
            WasperField source,
            Transform transform,
            Transform inverse,
            double scaleCorrection,
            string labelSuffix = " [T]")
        {
            if (source == null || source.Evaluator == null) return null;

            WasperField src = source;
            Transform inv = inverse;
            double scale = scaleCorrection;
            BoundingBox domain = TransformDomain(source.Domain, transform);
            string label = AppendLabel(source.Label, "transformed", labelSuffix);
            string op = $"Transform(scale_correction={scaleCorrection:F6})";

            return new WasperField(
                p =>
                {
                    Point3d pulled = p;
                    pulled.Transform(inv);
                    return scale * SafeEvaluate(src, pulled);
                },
                domain,
                label,
                AppendOperation(source, op, QualityAfterTransform(source)),
                QualityAfterTransform(source),
                source.OperationCount + 1,
                source.CurveThickenCount);
        }

        public static WasperField Offset(
            WasperField source,
            double offset,
            WasperField boundField = null,
            BoundingBox? domainOverride = null,
            string labelSuffix = "_offset")
        {
            if (source == null || source.Evaluator == null) return null;

            WasperField src = source;
            WasperField bound = boundField;
            double off = offset;
            BoundingBox domain = domainOverride ?? BuildDomain(source, boundField, Math.Abs(offset));
            string label = AppendLabel(source.Label, "field", labelSuffix);
            string op = boundField == null
                ? $"Offset(offset={offset:F6})"
                : $"Offset(offset={offset:F6}, bound_field={FieldName(boundField)})";

            return new WasperField(
                p =>
                {
                    double value = SafeEvaluate(src, p) - off;
                    if (bound != null && bound.Evaluator != null)
                        value = Math.Max(value, SafeEvaluate(bound, p));
                    return value;
                },
                domain,
                label,
                AppendOperation(source, op, QualityAfterOffset(source)),
                QualityAfterOffset(source),
                source.OperationCount + 1,
                source.CurveThickenCount);
        }

        public static WasperField CurveWeightedOffset(
            WasperField source,
            Curve curve,
            IList<double> thicknessValues,
            double radius,
            int falloffType,
            WasperField boundField = null,
            BoundingBox? domainOverride = null,
            string labelSuffix = "_curve_thickened")
        {
            if (source == null || source.Evaluator == null) return null;
            if (curve == null || !curve.IsValid) return null;
            if (thicknessValues == null || thicknessValues.Count == 0) return null;

            WasperField src = source;
            WasperField bound = boundField;
            Curve crv = curve.DuplicateCurve();
            double[] values = new double[thicknessValues.Count];
            for (int i = 0; i < thicknessValues.Count; i++)
                values[i] = thicknessValues[i];

            double influenceRadius = Math.Max(radius, 1e-9);
            double totalLength = Math.Max(crv.GetLength(), 1e-9);
            BoundingBox domain = domainOverride ?? BuildCurveOffsetDomain(source, boundField, crv, influenceRadius, MaxAbs(values));
            string label = AppendLabel(source.Label, "field", labelSuffix);
            string op = boundField == null
                ? $"CurveThicken(radius={influenceRadius:F6}, values={FormatValues(values)}, falloff={FalloffName(falloffType)})"
                : $"CurveThicken(radius={influenceRadius:F6}, values={FormatValues(values)}, falloff={FalloffName(falloffType)}, bound_field={FieldName(boundField)})";

            return new WasperField(
                p =>
                {
                    double value = ApproxSignedDistance(src, p);

                    double t;
                    if (crv.ClosestPoint(p, out t))
                    {
                        Point3d cp = crv.PointAt(t);
                        double d = p.DistanceTo(cp);
                        if (d <= influenceRadius)
                        {
                            double u = CurveLengthParameter(crv, t, totalLength);
                            double thickness = InterpolateValues(values, u);
                            double weight = FalloffWeight(d, influenceRadius, falloffType);
                            value -= thickness * weight;
                        }
                    }

                    if (bound != null && bound.Evaluator != null)
                        value = Math.Max(value, ApproxSignedDistance(bound, p));

                    return value;
                },
                domain,
                label,
                AppendOperation(source, op, WasperFieldSdfQuality.ImplicitScalarField),
                WasperFieldSdfQuality.ImplicitScalarField,
                source.OperationCount + 1,
                source.CurveThickenCount + 1);
        }

        /// <summary>
        /// Curve-local offset on the RAW field value (no gradient normalisation), so it
        /// behaves exactly like <see cref="Offset"/> but applied only near a curve with a
        /// radial falloff and length-interpolated offset values. The result is clipped with
        /// max(field, bound_field), matching the Offset/Infill boundary convention.
        ///   output(p) = field(p) - interp(values, u(p)) * falloff(distance_to_curve)
        /// then max(output, bound_field).
        /// </summary>
        public static WasperField CurveOffset(
            WasperField source,
            Curve curve,
            IList<double> offsetValues,
            double radius,
            int falloffType,
            WasperField boundField = null,
            BoundingBox? domainOverride = null,
            string labelSuffix = "_curve_offset")
        {
            if (source == null || source.Evaluator == null) return null;
            if (curve == null || !curve.IsValid) return null;
            if (offsetValues == null || offsetValues.Count == 0) return null;

            WasperField src = source;
            WasperField bound = boundField;
            Curve crv = curve.DuplicateCurve();
            double[] values = new double[offsetValues.Count];
            for (int i = 0; i < offsetValues.Count; i++)
                values[i] = offsetValues[i];

            double influenceRadius = Math.Max(radius, 1e-9);
            double totalLength = Math.Max(crv.GetLength(), 1e-9);
            BoundingBox domain = domainOverride ?? BuildCurveOffsetDomain(source, boundField, crv, influenceRadius, MaxAbs(values));
            string label = AppendLabel(source.Label, "field", labelSuffix);
            string op = boundField == null
                ? $"CurveOffset(radius={influenceRadius:F6}, values={FormatValues(values)}, falloff={FalloffName(falloffType)})"
                : $"CurveOffset(radius={influenceRadius:F6}, values={FormatValues(values)}, falloff={FalloffName(falloffType)}, bound_field={FieldName(boundField)})";

            return new WasperField(
                p =>
                {
                    double value = SafeEvaluate(src, p);

                    double t;
                    if (crv.ClosestPoint(p, out t))
                    {
                        Point3d cp = crv.PointAt(t);
                        double d = p.DistanceTo(cp);
                        if (d <= influenceRadius)
                        {
                            double u = CurveLengthParameter(crv, t, totalLength);
                            double off = InterpolateValues(values, u);
                            double weight = FalloffWeight(d, influenceRadius, falloffType);
                            value -= off * weight;
                        }
                    }

                    if (bound != null && bound.Evaluator != null)
                        value = Math.Max(value, SafeEvaluate(bound, p));

                    return value;
                },
                domain,
                label,
                AppendOperation(source, op, QualityAfterOffset(source)),
                QualityAfterOffset(source),
                source.OperationCount + 1,
                source.CurveThickenCount + 1);
        }

        public static WasperField Invert(
            WasperField source,
            WasperField boundField = null,
            BoundingBox? domainOverride = null,
            bool invert = true)
        {
            if (source == null || source.Evaluator == null) return null;

            WasperField src = source;
            WasperField bound = boundField;
            BoundingBox domain = domainOverride ?? BuildDomain(source, boundField, 0.0);
            string baseLabel = string.IsNullOrEmpty(src.Label) ? "field" : src.Label;
            string label = invert ? baseLabel + "_inverted" : baseLabel + "_bounded";
            string op = invert ? "Invert()" : "BoundOnly()";
            if (boundField != null)
                op += $" + Clip(bound_field={FieldName(boundField)})";

            return new WasperField(
                p =>
                {
                    double value = SafeEvaluate(src, p);
                    if (invert) value = -value;

                    if (bound != null && bound.Evaluator != null)
                        value = Math.Max(value, SafeEvaluate(bound, p));

                    return value;
                },
                domain,
                label,
                AppendOperation(source, op, source.SdfQuality),
                source.SdfQuality,
                source.OperationCount + 1,
                source.CurveThickenCount);
        }

        public static WasperField Shell(
            WasperField source,
            double thickness,
            BoundingBox? domainOverride = null,
            string labelSuffix = "_shell")
        {
            if (source == null || source.Evaluator == null) return null;

            WasperField src = source;
            double t = Math.Max(0.0, thickness);
            BoundingBox domain = domainOverride ?? BuildDomain(source, null, t);
            string label = AppendLabel(source.Label, "shell_field", labelSuffix);
            string op = $"Shell(thickness={t:F6})";

            return new WasperField(
                p =>
                {
                    double f = SafeEvaluate(src, p);
                    return Math.Max(f, -f - t);
                },
                domain,
                label,
                AppendOperation(source, op, QualityAfterShell(source)),
                QualityAfterShell(source),
                source.OperationCount + 1,
                source.CurveThickenCount);
        }

        public static WasperField Boolean(
            IList<WasperField> fields,
            BoundingBox domain,
            WasperFieldBooleanOperation operation)
        {
            if (fields == null || fields.Count == 0) return null;

            var validFields = new List<WasperField>();
            foreach (var field in fields)
            {
                if (field != null && field.Evaluator != null)
                    validFields.Add(field);
            }

            if (validFields.Count == 0) return null;

            string label = validFields.Count == 1
                ? AppendLabel(validFields[0].Label, "field", "_" + OperationLabel(operation))
                : OperationLabel(operation) + "_" + validFields.Count + "_fields";

            WasperField[] captured = validFields.ToArray();
            WasperField traceSource = captured[0];
            string op = $"Boolean({OperationLabel(operation)}, fields={captured.Length})";
            WasperFieldSdfQuality quality = WasperFieldSdfQuality.ApproximateSdf;

            return new WasperField(
                p =>
                {
                    switch (operation)
                    {
                        case WasperFieldBooleanOperation.Invert:
                            return -SafeEvaluate(captured[0], p);

                        case WasperFieldBooleanOperation.Xor:
                            if (captured.Length < 2) return SafeEvaluate(captured[0], p);
                            {
                                double a = SafeEvaluate(captured[0], p);
                                double b = SafeEvaluate(captured[1], p);
                                return Math.Max(Math.Min(a, -b), Math.Min(b, -a));
                            }

                        case WasperFieldBooleanOperation.Difference:
                            {
                                double a = SafeEvaluate(captured[0], p);
                                double bMin = double.PositiveInfinity;
                                for (int i = 1; i < captured.Length; i++)
                                {
                                    double b = SafeEvaluate(captured[i], p);
                                    if (b < bMin) bMin = b;
                                }

                                if (double.IsPositiveInfinity(bMin)) bMin = 1e9;
                                return Math.Max(a, -bMin);
                            }

                        case WasperFieldBooleanOperation.Union:
                            {
                                double value = double.PositiveInfinity;
                                for (int i = 0; i < captured.Length; i++)
                                {
                                    double v = SafeEvaluate(captured[i], p);
                                    if (v < value) value = v;
                                }
                                return value;
                            }

                        case WasperFieldBooleanOperation.Intersection:
                        default:
                            {
                                double value = double.NegativeInfinity;
                                for (int i = 0; i < captured.Length; i++)
                                {
                                    double v = SafeEvaluate(captured[i], p);
                                    if (v > value) value = v;
                                }
                                return value;
                            }
                    }
                },
                domain,
                label,
                AppendOperation(traceSource, op, quality),
                quality,
                MaxOperationCount(captured) + 1,
                SumCurveThickenCount(captured));
        }

        public static double SafeEvaluate(WasperField field, Point3d point)
        {
            if (field == null || field.Evaluator == null)
                return double.PositiveInfinity;

            try
            {
                double value = field.Evaluate(point);
                return IsFinite(value) ? value : double.PositiveInfinity;
            }
            catch
            {
                return double.PositiveInfinity;
            }
        }

        public static double ApproxSignedDistance(WasperField field, Point3d point, double gradientStep = 0.0)
        {
            double value = SafeEvaluate(field, point);
            if (!IsFinite(value)) return value;

            double h = gradientStep;
            if (h <= 0.0 && field != null && field.Domain.IsValid)
                h = Math.Max(field.Domain.Diagonal.Length * 1e-6, 1e-6);
            if (h <= 0.0) h = 1e-6;

            double fx0 = SafeEvaluate(field, new Point3d(point.X - h, point.Y, point.Z));
            double fx1 = SafeEvaluate(field, new Point3d(point.X + h, point.Y, point.Z));
            double fy0 = SafeEvaluate(field, new Point3d(point.X, point.Y - h, point.Z));
            double fy1 = SafeEvaluate(field, new Point3d(point.X, point.Y + h, point.Z));
            double fz0 = SafeEvaluate(field, new Point3d(point.X, point.Y, point.Z - h));
            double fz1 = SafeEvaluate(field, new Point3d(point.X, point.Y, point.Z + h));

            if (!IsFinite(fx0) || !IsFinite(fx1) ||
                !IsFinite(fy0) || !IsFinite(fy1) ||
                !IsFinite(fz0) || !IsFinite(fz1))
                return value;

            double dx = (fx1 - fx0) / (2.0 * h);
            double dy = (fy1 - fy0) / (2.0 * h);
            double dz = (fz1 - fz0) / (2.0 * h);
            double grad = Math.Sqrt(dx * dx + dy * dy + dz * dz);

            return grad > 1e-12 ? value / grad : value;
        }

        public static BoundingBox TransformDomain(BoundingBox domain, Transform transform)
        {
            if (!domain.IsValid) return BoundingBox.Empty;

            Point3d[] corners = domain.GetCorners();
            for (int i = 0; i < corners.Length; i++)
                corners[i].Transform(transform);

            return new BoundingBox(corners);
        }

        public static BoundingBox BuildDomain(
            WasperField source,
            WasperField boundField,
            double padding)
        {
            BoundingBox domain = BoundingBox.Unset;

            if (boundField != null && boundField.Domain.IsValid)
                domain = boundField.Domain;
            else if (source != null && source.Domain.IsValid)
                domain = source.Domain;

            if (!domain.IsValid) return domain;

            double pad = Math.Max(padding, 1e-6);
            domain.Inflate(pad);
            return domain;
        }

        public static BoundingBox BuildCurveOffsetDomain(
            WasperField source,
            WasperField boundField,
            Curve curve,
            double radius,
            double maxThickness)
        {
            if (boundField != null && boundField.Domain.IsValid)
                return boundField.Domain;

            BoundingBox domain = BoundingBox.Unset;
            bool started = false;

            if (source != null && source.Domain.IsValid)
            {
                domain = source.Domain;
                started = true;
            }

            if (curve != null && curve.IsValid)
            {
                BoundingBox curveBox = curve.GetBoundingBox(true);
                if (curveBox.IsValid)
                {
                    double curvePad = Math.Max(radius + maxThickness, 1e-6);
                    curveBox.Inflate(curvePad);
                    domain = started ? BoundingBox.Union(domain, curveBox) : curveBox;
                    started = true;
                }
            }

            if (!started || !domain.IsValid) return BoundingBox.Unset;

            domain.Inflate(Math.Max(radius + maxThickness, 1e-6));
            return domain;
        }

        public static string OperationLabel(WasperFieldBooleanOperation operation)
        {
            switch (operation)
            {
                case WasperFieldBooleanOperation.Union: return "union";
                case WasperFieldBooleanOperation.Difference: return "subtraction";
                case WasperFieldBooleanOperation.Intersection: return "intersection";
                case WasperFieldBooleanOperation.Xor: return "xor";
                case WasperFieldBooleanOperation.Invert: return "negate";
                default: return "intersection";
            }
        }

        private static string AppendOperation(
            WasperField source,
            string operation,
            WasperFieldSdfQuality resultingQuality)
        {
            string trace = source == null || string.IsNullOrWhiteSpace(source.OperationTrace)
                ? "WasperField source"
                : source.OperationTrace;

            int next = source == null ? 1 : source.OperationCount + 1;
            return trace + Environment.NewLine + $"{next}. {operation} | quality={resultingQuality}";
        }

        private static WasperFieldSdfQuality QualityAfterTransform(WasperField source)
        {
            if (source == null) return WasperFieldSdfQuality.Unknown;
            if (source.SdfQuality == WasperFieldSdfQuality.ExactSdf)
                return WasperFieldSdfQuality.ApproximateSdf;
            return source.SdfQuality;
        }

        private static WasperFieldSdfQuality QualityAfterOffset(WasperField source)
        {
            if (source == null) return WasperFieldSdfQuality.Unknown;
            if (source.SdfQuality == WasperFieldSdfQuality.ExactSdf)
                return WasperFieldSdfQuality.ExactSdf;
            return source.SdfQuality;
        }

        private static WasperFieldSdfQuality QualityAfterShell(WasperField source)
        {
            if (source == null) return WasperFieldSdfQuality.Unknown;
            return source.SdfQuality == WasperFieldSdfQuality.Unknown
                ? WasperFieldSdfQuality.Unknown
                : WasperFieldSdfQuality.ApproximateSdf;
        }

        private static int MaxOperationCount(WasperField[] fields)
        {
            int max = 0;
            if (fields == null) return max;
            for (int i = 0; i < fields.Length; i++)
                if (fields[i] != null && fields[i].OperationCount > max)
                    max = fields[i].OperationCount;
            return max;
        }

        private static int SumCurveThickenCount(WasperField[] fields)
        {
            int sum = 0;
            if (fields == null) return sum;
            for (int i = 0; i < fields.Length; i++)
                if (fields[i] != null)
                    sum += fields[i].CurveThickenCount;
            return sum;
        }

        private static string FieldName(WasperField field)
        {
            if (field == null) return "(none)";
            return string.IsNullOrWhiteSpace(field.Label) ? "field" : field.Label;
        }

        private static string FormatValues(double[] values)
        {
            if (values == null || values.Length == 0) return "[]";

            var parts = new string[values.Length];
            for (int i = 0; i < values.Length; i++)
                parts[i] = values[i].ToString("G6");
            return "[" + string.Join(",", parts) + "]";
        }

        private static string FalloffName(int falloffType)
        {
            switch (falloffType)
            {
                case 0: return "linear";
                case 2: return "gaussian";
                case 1:
                default: return "smooth";
            }
        }

        private static string AppendLabel(string sourceLabel, string fallback, string suffix)
        {
            return string.IsNullOrEmpty(sourceLabel)
                ? fallback
                : sourceLabel + suffix;
        }

        private static double CurveLengthParameter(Curve curve, double t, double totalLength)
        {
            try
            {
                Interval domain = curve.Domain;
                double clamped = Math.Max(domain.T0, Math.Min(domain.T1, t));
                double len = curve.GetLength(new Interval(domain.T0, clamped));
                double u = len / Math.Max(totalLength, 1e-9);
                return Clamp01(u);
            }
            catch
            {
                Interval domain = curve.Domain;
                double span = Math.Abs(domain.Length);
                if (span < 1e-12) return 0.0;
                return Clamp01((t - domain.T0) / span);
            }
        }

        private static double InterpolateValues(double[] values, double u)
        {
            if (values == null || values.Length == 0) return 0.0;
            if (values.Length == 1) return values[0];

            double x = Clamp01(u) * (values.Length - 1);
            int i0 = (int)Math.Floor(x);
            if (i0 >= values.Length - 1) return values[values.Length - 1];

            int i1 = i0 + 1;
            double f = x - i0;
            return values[i0] + f * (values[i1] - values[i0]);
        }

        private static double FalloffWeight(double distance, double radius, int falloffType)
        {
            if (radius <= 1e-12) return 0.0;
            double x = Clamp01(1.0 - distance / radius);

            switch (falloffType)
            {
                case 0:
                    return x;

                case 2:
                    {
                        double sigma = radius / 3.0;
                        double g = Math.Exp(-0.5 * distance * distance / (sigma * sigma));
                        return distance <= radius ? g : 0.0;
                    }

                case 1:
                default:
                    return x * x * (3.0 - 2.0 * x);
            }
        }

        private static double MaxAbs(double[] values)
        {
            if (values == null || values.Length == 0) return 0.0;

            double max = 0.0;
            for (int i = 0; i < values.Length; i++)
            {
                double a = Math.Abs(values[i]);
                if (a > max) max = a;
            }
            return max;
        }

        private static double Clamp01(double value)
        {
            if (value < 0.0) return 0.0;
            if (value > 1.0) return 1.0;
            return value;
        }

        private static bool IsFinite(double value)
        {
            return !(double.IsNaN(value) || double.IsInfinity(value));
        }
    }
}
