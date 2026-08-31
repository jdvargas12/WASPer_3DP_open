// Shared segment-data builder for meshless GPU printing-path previews.
//
// Each bead segment is a swept ellipse between two path points. Every point
// carries its own smoothed (central-difference) tangent frame and extents, so
// consecutive segments share their boundary section exactly: joints render
// seamlessly instead of showing mitred creases, and Pp04-style flow-tapered,
// non-planar beads are supported by the same layout. Ellipsoid end caps are
// only emitted at true stroke ends (open paths); closed loops wrap without
// caps.

using System;
using System.Collections.Generic;
using System.Drawing;

using Rhino.Geometry;

namespace WASPer_3DP
{
    internal sealed class WasperPrintPathPreviewBatch
    {
        // RGBA32F texels per segment:
        //   t0: A.xyz,  halfWidthA      t1: B.xyz,  halfWidthB
        //   t2: WA.xyz, halfHeightA     t3: HA.xyz, halfHeightB
        //   t4: WB.xyz, capRadius       t5: HB.xyz, capFlags (bit0 A, bit1 B)
        internal const int TexelsPerSegment = 6;
        internal const int FloatsPerSegment = TexelsPerSegment * 4;

        internal readonly float[] SegmentData;
        internal readonly int SegmentCount;
        internal readonly Color Color;
        internal readonly BoundingBox Bounds;

        internal WasperPrintPathPreviewBatch(
            float[] segmentData,
            int segmentCount,
            Color color,
            BoundingBox bounds)
        {
            SegmentData = segmentData;
            SegmentCount = segmentCount;
            Color = color;
            Bounds = bounds;
        }
    }

    /// <summary>
    /// One continuous printing stroke: ordered path points with per-point bead
    /// width, bead height, and bead height direction (unit vector from the path
    /// point into the deposited material, typically -Z or -plane.ZAxis).
    /// </summary>
    internal sealed class WasperPrintPathPreviewStroke
    {
        internal readonly IReadOnlyList<Point3d> Points;
        internal readonly IReadOnlyList<double> Widths;
        internal readonly IReadOnlyList<double> Heights;
        internal readonly IReadOnlyList<Vector3d> HeightDirections;
        internal readonly bool Closed;

        internal WasperPrintPathPreviewStroke(
            IReadOnlyList<Point3d> points,
            IReadOnlyList<double> widths,
            IReadOnlyList<double> heights,
            IReadOnlyList<Vector3d> heightDirections,
            bool closed)
        {
            Points = points;
            Widths = widths;
            Heights = heights;
            HeightDirections = heightDirections;
            Closed = closed;
        }
    }

    internal static class WasperPrintPathPreviewBuilder
    {
        // Six RGBA32F texels are used per segment. Keeping batches modest avoids
        // texture-width limits while still reducing draw-call overhead.
        private const int MaxSegmentsPerBatch = 2048;

        internal static List<WasperPrintPathPreviewBatch> BuildPlanar(
            IReadOnlyList<Curve> curves,
            double layerWidth,
            double layerHeight,
            double segmentLength,
            double tolerance,
            Color color)
        {
            var strokes = new List<WasperPrintPathPreviewStroke>();
            if (curves == null || curves.Count == 0 ||
                layerWidth <= tolerance || layerHeight <= tolerance)
                return new List<WasperPrintPathPreviewBatch>();

            for (int i = 0; i < curves.Count; i++)
            {
                Curve curve = curves[i];
                if (curve == null || !curve.IsValid)
                    continue;

                List<Point3d> points = SampleCurve(curve, segmentLength, tolerance, out bool closed);
                if (points.Count < 2)
                    continue;

                int count = points.Count;
                var widths = new double[count];
                var heights = new double[count];
                var heightDirections = new Vector3d[count];
                for (int j = 0; j < count; j++)
                {
                    widths[j] = layerWidth;
                    heights[j] = layerHeight;
                    heightDirections[j] = -Vector3d.ZAxis;
                }

                strokes.Add(new WasperPrintPathPreviewStroke(
                    points, widths, heights, heightDirections, closed));
            }

            return Build(strokes, tolerance, color);
        }

        /// <summary>
        /// Builds preview batches from arbitrary strokes with per-point width,
        /// height, and height direction (non-planar / flow-tapered paths).
        /// </summary>
        internal static List<WasperPrintPathPreviewBatch> Build(
            IReadOnlyList<WasperPrintPathPreviewStroke> strokes,
            double tolerance,
            Color color)
        {
            var result = new List<WasperPrintPathPreviewBatch>();
            if (strokes == null || strokes.Count == 0)
                return result;

            var segments = new List<PreviewSegment>();
            foreach (WasperPrintPathPreviewStroke stroke in strokes)
                AppendStrokeSegments(stroke, tolerance, segments);

            for (int start = 0; start < segments.Count; start += MaxSegmentsPerBatch)
            {
                int count = Math.Min(MaxSegmentsPerBatch, segments.Count - start);
                var data = new float[count * WasperPrintPathPreviewBatch.FloatsPerSegment];
                BoundingBox bounds = BoundingBox.Empty;

                for (int i = 0; i < count; i++)
                {
                    PreviewSegment segment = segments[start + i];
                    int offset = i * WasperPrintPathPreviewBatch.FloatsPerSegment;

                    PackTexel(data, offset, segment.CenterA, segment.HalfWidthA);
                    PackTexel(data, offset + 4, segment.CenterB, segment.HalfWidthB);
                    PackTexel(data, offset + 8, segment.WidthDirectionA, segment.HalfHeightA);
                    PackTexel(data, offset + 12, segment.HeightDirectionA, segment.HalfHeightB);
                    PackTexel(data, offset + 16, segment.WidthDirectionB, segment.CapRadius);
                    PackTexel(data, offset + 20, segment.HeightDirectionB, segment.CapFlags);

                    bounds.Union(segment.Bounds);
                }

                result.Add(new WasperPrintPathPreviewBatch(data, count, color, bounds));
            }

            return result;
        }

        private static void PackTexel(float[] data, int offset, Point3d point, double w)
        {
            data[offset] = (float)point.X;
            data[offset + 1] = (float)point.Y;
            data[offset + 2] = (float)point.Z;
            data[offset + 3] = (float)w;
        }

        private static void PackTexel(float[] data, int offset, Vector3d vector, double w)
        {
            data[offset] = (float)vector.X;
            data[offset + 1] = (float)vector.Y;
            data[offset + 2] = (float)vector.Z;
            data[offset + 3] = (float)w;
        }

        private static void AppendStrokeSegments(
            WasperPrintPathPreviewStroke stroke,
            double tolerance,
            List<PreviewSegment> segments)
        {
            if (stroke?.Points == null || stroke.Points.Count < 2 ||
                stroke.Widths == null || stroke.Widths.Count != stroke.Points.Count ||
                stroke.Heights == null || stroke.Heights.Count != stroke.Points.Count ||
                stroke.HeightDirections == null || stroke.HeightDirections.Count != stroke.Points.Count)
                return;

            IReadOnlyList<Point3d> points = stroke.Points;
            int count = points.Count;
            bool closed = stroke.Closed && count > 2;

            // Smoothed per-point frames: central-difference tangents (wrapping on
            // closed loops) make consecutive segments share their boundary
            // section exactly, which is what removes the joint creases.
            var frames = new PointFrame[count];
            Vector3d lastTangent = Vector3d.XAxis;
            bool anyFrame = false;
            for (int i = 0; i < count; i++)
            {
                Vector3d tangent = SmoothedTangent(points, i, closed);
                if (!tangent.Unitize() || tangent.IsTiny(tolerance))
                    tangent = lastTangent;
                else
                    lastTangent = tangent;

                if (!TryBuildFrame(
                        tangent,
                        stroke.HeightDirections[i],
                        stroke.Widths[i],
                        stroke.Heights[i],
                        tolerance,
                        out frames[i]))
                {
                    frames[i].Valid = false;
                    continue;
                }

                frames[i].Center = points[i] + frames[i].HeightDirection * frames[i].HalfHeight;
                frames[i].Valid = true;
                anyFrame = true;
            }

            if (!anyFrame)
                return;

            int pairCount = closed ? count : count - 1;
            for (int i = 0; i < pairCount; i++)
            {
                int j = (i + 1) % count;
                if (!frames[i].Valid || !frames[j].Valid)
                    continue;

                bool capA = !closed && i == 0;
                bool capB = !closed && i == pairCount - 1;
                PreviewSegment segment = PreviewSegment.Create(
                    frames[i], frames[j], capA, capB, tolerance);
                if (segment != null)
                    segments.Add(segment);
            }
        }

        private static Vector3d SmoothedTangent(IReadOnlyList<Point3d> points, int index, bool closed)
        {
            int count = points.Count;
            if (closed)
            {
                int previous = (index - 1 + count) % count;
                int next = (index + 1) % count;
                return points[next] - points[previous];
            }

            if (index <= 0)
                return points[1] - points[0];
            if (index >= count - 1)
                return points[count - 1] - points[count - 2];
            return points[index + 1] - points[index - 1];
        }

        private static bool TryBuildFrame(
            Vector3d tangent,
            Vector3d heightHint,
            double width,
            double height,
            double tolerance,
            out PointFrame frame)
        {
            frame = default;
            if (width <= tolerance || height <= tolerance ||
                !double.IsFinite(width) || !double.IsFinite(height))
                return false;

            Vector3d hint = heightHint;
            if (!hint.IsValid || hint.IsTiny(tolerance) || !hint.Unitize())
                hint = -Vector3d.ZAxis;

            Vector3d widthDirection = Vector3d.CrossProduct(tangent, hint);
            if (!widthDirection.Unitize())
            {
                // Tangent parallel to the height hint: fall back to any
                // perpendicular so steep/vertical segments stay non-degenerate.
                widthDirection = Vector3d.CrossProduct(tangent, Vector3d.XAxis);
                if (!widthDirection.Unitize())
                {
                    widthDirection = Vector3d.CrossProduct(tangent, Vector3d.YAxis);
                    if (!widthDirection.Unitize())
                        return false;
                }
            }

            Vector3d heightDirection = Vector3d.CrossProduct(widthDirection, tangent);
            if (!heightDirection.Unitize())
                return false;
            if (Vector3d.Multiply(heightDirection, hint) < 0.0)
            {
                heightDirection = -heightDirection;
                widthDirection = -widthDirection;
            }

            frame.Tangent = tangent;
            frame.WidthDirection = widthDirection;
            frame.HeightDirection = heightDirection;
            frame.HalfWidth = width * 0.5;
            frame.HalfHeight = height * 0.5;
            return true;
        }

        private static List<Point3d> SampleCurve(
            Curve curve,
            double segmentLength,
            double tolerance,
            out bool closed)
        {
            var points = new List<Point3d>();
            closed = false;

            double length = curve.GetLength();
            if (!double.IsFinite(length) || length <= tolerance)
                return points;

            closed = curve.IsClosed ||
                     curve.PointAtStart.DistanceTo(curve.PointAtEnd) <= segmentLength * 1.6;

            points.Add(curve.PointAtStart);
            for (double distance = segmentLength; distance < length - tolerance; distance += segmentLength)
            {
                if (curve.LengthParameter(distance, out double parameter))
                    points.Add(curve.PointAt(parameter));
                else
                    break;
            }

            if (!closed)
                points.Add(curve.PointAtEnd);

            return points;
        }

        private struct PointFrame
        {
            internal bool Valid;
            internal Point3d Center;
            internal Vector3d Tangent;
            internal Vector3d WidthDirection;
            internal Vector3d HeightDirection;
            internal double HalfWidth;
            internal double HalfHeight;
        }

        private sealed class PreviewSegment
        {
            internal readonly Point3d CenterA;
            internal readonly Point3d CenterB;
            internal readonly Vector3d WidthDirectionA;
            internal readonly Vector3d HeightDirectionA;
            internal readonly Vector3d WidthDirectionB;
            internal readonly Vector3d HeightDirectionB;
            internal readonly double HalfWidthA;
            internal readonly double HalfWidthB;
            internal readonly double HalfHeightA;
            internal readonly double HalfHeightB;
            internal readonly double CapRadius;
            internal readonly double CapFlags;
            internal readonly BoundingBox Bounds;

            private PreviewSegment(
                PointFrame a,
                PointFrame b,
                double capRadius,
                double capFlags,
                BoundingBox bounds)
            {
                CenterA = a.Center;
                CenterB = b.Center;
                WidthDirectionA = a.WidthDirection;
                HeightDirectionA = a.HeightDirection;
                WidthDirectionB = b.WidthDirection;
                HeightDirectionB = b.HeightDirection;
                HalfWidthA = a.HalfWidth;
                HalfWidthB = b.HalfWidth;
                HalfHeightA = a.HalfHeight;
                HalfHeightB = b.HalfHeight;
                CapRadius = capRadius;
                CapFlags = capFlags;
                Bounds = bounds;
            }

            internal static PreviewSegment Create(
                PointFrame a,
                PointFrame b,
                bool capA,
                bool capB,
                double tolerance)
            {
                Vector3d chord = b.Center - a.Center;
                if (chord.Length <= tolerance)
                    return null;

                double capRadius = Math.Min(
                    Math.Min(a.HalfWidth, a.HalfHeight),
                    Math.Min(b.HalfWidth, b.HalfHeight));
                double capFlags = (capA ? 1.0 : 0.0) + (capB ? 2.0 : 0.0);

                // Conservative bounds: both endpoint sections inflated laterally
                // (frames tilt slightly between endpoints) and extended along the
                // section normals by the cap radius.
                const double inflate = 1.2;
                var corners = new List<Point3d>(16);
                AddSectionCorners(corners, a, capRadius, inflate, -1.0);
                AddSectionCorners(corners, b, capRadius, inflate, 1.0);

                return new PreviewSegment(a, b, capRadius, capFlags, new BoundingBox(corners));
            }

            private static void AddSectionCorners(
                List<Point3d> corners,
                PointFrame frame,
                double capRadius,
                double inflate,
                double outwardSign)
            {
                Point3d center = frame.Center + frame.Tangent * (outwardSign * capRadius);
                Vector3d w = frame.WidthDirection * (frame.HalfWidth * inflate);
                Vector3d h = frame.HeightDirection * (frame.HalfHeight * inflate);
                corners.Add(center + w + h);
                corners.Add(center + w - h);
                corners.Add(center - w + h);
                corners.Add(center - w - h);
            }
        }
    }
}
