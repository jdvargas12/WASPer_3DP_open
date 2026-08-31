using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper;
using Grasshopper.Kernel.Data;
using Rhino.Geometry;

namespace WASPer_3DP
{
    internal sealed class WasperPrintPathBeadFieldResult
    {
        internal WasperField Field;
        internal int InputBranches;
        internal int IncludedBranches;
        internal int Segments;
        internal int SkippedShortBranches;
        internal int SkippedInvalidSegments;
        internal int SkippedTornSegments;
        internal int WidthFallbacks;
        internal int HeightFallbacks;
        internal double SourcePrintVolume;
        internal double MinimumWidth = double.PositiveInfinity;
        internal double MaximumWidth;
        internal double MinimumHeight = double.PositiveInfinity;
        internal double MaximumHeight;
    }

    internal static class WasperPrintPathBeadFieldBuilder
    {
        internal static WasperPrintPathBeadFieldResult Build(
            WasperPrintPath path,
            IList<int> targetRoles,
            double profileExponent,
            double bondOverlap,
            double tolerance)
        {
            var result = new WasperPrintPathBeadFieldResult();
            if (path == null || !path.HasPlanes)
                return result;

            profileExponent = Math.Max(1.0, profileExponent);
            bondOverlap = Math.Max(0.0, bondOverlap);
            tolerance = Math.Max(1e-9, tolerance);
            var segments = new List<BeadSegment>();

            for (int branchIndex = 0; branchIndex < path.PtPlanes.BranchCount; branchIndex++)
            {
                result.InputBranches++;
                GH_Path branchPath = path.PtPlanes.Paths[branchIndex];
                if (!WasperGcodeTreeUtil.MatchesTargetRoles(path.PathRoles, branchPath, targetRoles))
                    continue;

                IList<Plane> sourcePlanes = path.PtPlanes.Branch(branchPath);
                if (sourcePlanes == null || sourcePlanes.Count < 2)
                {
                    result.SkippedShortBranches++;
                    continue;
                }

                bool closed = sourcePlanes.Count > 2 &&
                    sourcePlanes[0].Origin.DistanceTo(sourcePlanes[sourcePlanes.Count - 1].Origin) <= tolerance;
                int count = closed ? sourcePlanes.Count - 1 : sourcePlanes.Count;
                if (count < 2)
                {
                    result.SkippedShortBranches++;
                    continue;
                }

                var frames = new BeadFrame[count];
                for (int i = 0; i < count; i++)
                {
                    Vector3d tangent = PointTangent(sourcePlanes, count, i, closed, tolerance);
                    Vector3d heightDirection = -sourcePlanes[i].ZAxis;
                    if (!heightDirection.Unitize()) heightDirection = -Vector3d.ZAxis;
                    Vector3d widthDirection = Vector3d.CrossProduct(heightDirection, tangent);
                    if (!widthDirection.Unitize())
                    {
                        widthDirection = Vector3d.CrossProduct(tangent, Vector3d.XAxis);
                        if (!widthDirection.Unitize()) widthDirection = Vector3d.YAxis;
                    }

                    double height = PositiveTreeValue(path.LayerH, branchPath, i, tolerance);
                    if (!(height > tolerance))
                    {
                        height = Math.Max(tolerance * 10.0, 1.0);
                        result.HeightFallbacks++;
                    }

                    double width = PositiveTreeValue(path.LayerWf, branchPath, i, tolerance);
                    if (!(width > tolerance))
                    {
                        double nominal = PositiveTreeValue(path.LayerW, branchPath, i, tolerance);
                        if (!(nominal > tolerance)) nominal = height * 2.5;
                        double flow = PositiveTreeValue(path.Flows, branchPath, i, tolerance);
                        if (!(flow > tolerance)) flow = 1.0;
                        width = EstimateFlowAdjustedWidth(nominal, height, flow, tolerance);
                        result.WidthFallbacks++;
                    }

                    frames[i] = new BeadFrame(
                        sourcePlanes[i].Origin,
                        tangent,
                        widthDirection,
                        heightDirection,
                        width,
                        height);
                    result.MinimumWidth = Math.Min(result.MinimumWidth, width);
                    result.MaximumWidth = Math.Max(result.MaximumWidth, width);
                    result.MinimumHeight = Math.Min(result.MinimumHeight, height);
                    result.MaximumHeight = Math.Max(result.MaximumHeight, height);
                }

                int segmentCount = closed ? count : count - 1;
                int addedForBranch = 0;
                for (int i = 0; i < segmentCount; i++)
                {
                    int next = (i + 1) % count;
                    if (TreeBoolAt(path.Torn, branchPath, i, false))
                    {
                        result.SkippedTornSegments++;
                        continue;
                    }

                    BeadSegment segment = BeadSegment.Create(
                        frames[i], frames[next], profileExponent, bondOverlap, tolerance);
                    if (segment == null)
                    {
                        result.SkippedInvalidSegments++;
                        continue;
                    }
                    segments.Add(segment);
                    addedForBranch++;
                }

                if (addedForBranch > 0)
                {
                    result.IncludedBranches++;
                    result.SourcePrintVolume += SumPositiveBranch(path.PrintVol, branchPath);
                }
            }

            result.Segments = segments.Count;
            if (segments.Count == 0)
                return result;

            BoundingBox domain = segments[0].Bounds;
            for (int i = 1; i < segments.Count; i++) domain.Union(segments[i].Bounds);
            BeadBvh bvh = BeadBvh.Build(segments);
            result.Field = new WasperField(
                p => bvh.Evaluate(p),
                domain,
                "WASPer path bead field",
                $"PathBeads(segments={segments.Count}, superellipse_n={profileExponent:F3}, vertical_bond_overlap={bondOverlap:F6})",
                WasperFieldSdfQuality.ApproximateSdf,
                1,
                0);
            return result;
        }

        private static Vector3d PointTangent(
            IList<Plane> planes,
            int count,
            int index,
            bool closed,
            double tolerance)
        {
            Vector3d tangent;
            if (closed)
            {
                int previous = (index - 1 + count) % count;
                int next = (index + 1) % count;
                tangent = planes[next].Origin - planes[previous].Origin;
            }
            else if (index == 0)
            {
                tangent = planes[1].Origin - planes[0].Origin;
            }
            else if (index == count - 1)
            {
                tangent = planes[count - 1].Origin - planes[count - 2].Origin;
            }
            else
            {
                tangent = planes[index + 1].Origin - planes[index - 1].Origin;
            }
            if (!tangent.Unitize() || tangent.IsTiny(tolerance)) tangent = Vector3d.XAxis;
            return tangent;
        }

        private static double EstimateFlowAdjustedWidth(
            double nominalWidth,
            double height,
            double flow,
            double tolerance)
        {
            if (!(nominalWidth > tolerance) || !(height > tolerance) || !(flow > tolerance) ||
                !double.IsFinite(nominalWidth) || !double.IsFinite(height) || !double.IsFinite(flow))
                return nominalWidth * Math.Max(flow, 1.0);

            double referenceWidth = Math.Max(nominalWidth, height);
            double referenceArea = height * (referenceWidth - height) +
                Math.PI * height * height / 4.0;
            return (flow * referenceArea) / height + height * (1.0 - Math.PI / 4.0);
        }

        private static double PositiveTreeValue(
            DataTree<double> tree,
            GH_Path path,
            int index,
            double tolerance)
        {
            if (tree == null || path == null || !tree.PathExists(path)) return double.NaN;
            IList<double> branch = tree.Branch(path);
            if (branch == null || branch.Count == 0) return double.NaN;
            int resolved = branch.Count == 1 ? 0 : Math.Min(index, branch.Count - 1);
            double value = branch[resolved];
            if (double.IsFinite(value) && value > tolerance) return value;
            for (int radius = 1; radius < branch.Count; radius++)
            {
                int left = resolved - radius;
                if (left >= 0 && double.IsFinite(branch[left]) && branch[left] > tolerance)
                    return branch[left];
                int right = resolved + radius;
                if (right < branch.Count && double.IsFinite(branch[right]) && branch[right] > tolerance)
                    return branch[right];
            }
            return double.NaN;
        }

        private static bool TreeBoolAt(
            DataTree<bool> tree,
            GH_Path path,
            int index,
            bool fallback)
        {
            if (tree == null || path == null || !tree.PathExists(path)) return fallback;
            IList<bool> branch = tree.Branch(path);
            if (branch == null || branch.Count == 0) return fallback;
            return branch[branch.Count == 1 ? 0 : Math.Min(index, branch.Count - 1)];
        }

        private static double SumPositiveBranch(DataTree<double> tree, GH_Path path)
        {
            if (tree == null || path == null || !tree.PathExists(path)) return 0.0;
            IList<double> branch = tree.Branch(path);
            if (branch == null) return 0.0;
            double sum = 0.0;
            for (int i = 0; i < branch.Count; i++)
                if (double.IsFinite(branch[i]) && branch[i] > 0.0) sum += branch[i];
            return sum;
        }

        private readonly struct BeadFrame
        {
            internal readonly Point3d Point;
            internal readonly Vector3d Tangent;
            internal readonly Vector3d WidthDirection;
            internal readonly Vector3d HeightDirection;
            internal readonly double Width;
            internal readonly double Height;

            internal BeadFrame(
                Point3d point,
                Vector3d tangent,
                Vector3d widthDirection,
                Vector3d heightDirection,
                double width,
                double height)
            {
                Point = point;
                Tangent = tangent;
                WidthDirection = widthDirection;
                HeightDirection = heightDirection;
                Width = width;
                Height = height;
            }
        }

        private sealed class BeadSegment
        {
            private readonly BeadFrame _a;
            private readonly BeadFrame _b;
            private readonly Vector3d _axis;
            private readonly double _length;
            private readonly double _exponent;
            private readonly double _verticalOverlap;
            internal readonly BoundingBox Bounds;
            internal Point3d BoundsCenter => Bounds.Center;

            private BeadSegment(
                BeadFrame a,
                BeadFrame b,
                Vector3d axis,
                double length,
                double exponent,
                double verticalOverlap,
                BoundingBox bounds)
            {
                _a = a;
                _b = b;
                _axis = axis;
                _length = length;
                _exponent = exponent;
                _verticalOverlap = verticalOverlap;
                Bounds = bounds;
            }

            internal static BeadSegment Create(
                BeadFrame a,
                BeadFrame b,
                double exponent,
                double verticalOverlap,
                double tolerance)
            {
                Vector3d axis = b.Point - a.Point;
                double length = axis.Length;
                if (!(length > tolerance) || !axis.Unitize()) return null;
                double radius = 0.5 * Math.Max(
                    Math.Max(a.Width, b.Width),
                    Math.Max(a.Height, b.Height) + verticalOverlap);
                var bounds = new BoundingBox(new[] { a.Point, b.Point });
                bounds.Inflate(Math.Max(radius, tolerance));
                return new BeadSegment(a, b, axis, length, exponent, verticalOverlap, bounds);
            }

            internal double Evaluate(Point3d point)
            {
                Vector3d fromStart = point - _a.Point;
                double s = fromStart * _axis;
                double t = Math.Max(0.0, Math.Min(1.0, s / _length));
                Point3d axisPoint = _a.Point + (_b.Point - _a.Point) * t;

                Vector3d heightDirection = Lerp(_a.HeightDirection, _b.HeightDirection, t);
                if (!heightDirection.Unitize()) heightDirection = _a.HeightDirection;
                Vector3d widthDirection = Lerp(_a.WidthDirection, _b.WidthDirection, t);
                widthDirection -= _axis * (widthDirection * _axis);
                widthDirection -= heightDirection * (widthDirection * heightDirection);
                if (!widthDirection.Unitize())
                {
                    widthDirection = Vector3d.CrossProduct(heightDirection, _axis);
                    if (!widthDirection.Unitize()) widthDirection = _a.WidthDirection;
                }

                double width = Lerp(_a.Width, _b.Width, t);
                double height = Lerp(_a.Height, _b.Height, t);
                double halfWidth = Math.Max(1e-9, width * 0.5);
                // Pp04 places the bead from the path plane toward -plane.Z. Extend only
                // in that previous-layer direction so bonding does not widen the sides.
                double bondedHeight = height + _verticalOverlap;
                double halfHeight = Math.Max(1e-9, bondedHeight * 0.5);
                Point3d center = axisPoint + heightDirection * halfHeight;
                Vector3d local = point - center;
                double x = Math.Abs(local * widthDirection) / halfWidth;
                double y = Math.Abs(local * heightDirection) / halfHeight;
                double norm = Math.Pow(Math.Pow(x, _exponent) + Math.Pow(y, _exponent), 1.0 / _exponent);
                double crossDistance = (norm - 1.0) * Math.Min(halfWidth, halfHeight);
                double axialDistance = Math.Max(-s, s - _length);
                double outsideCross = Math.Max(crossDistance, 0.0);
                double outsideAxial = Math.Max(axialDistance, 0.0);
                double distance = Math.Sqrt(
                    outsideCross * outsideCross + outsideAxial * outsideAxial) +
                    Math.Min(Math.Max(crossDistance, axialDistance), 0.0);
                return distance;
            }

            private static Vector3d Lerp(Vector3d a, Vector3d b, double t) => a + (b - a) * t;
            private static double Lerp(double a, double b, double t) => a + (b - a) * t;
        }

        private sealed class BeadBvh
        {
            private const int LeafSize = 8;
            private readonly Node _root;

            private BeadBvh(Node root) { _root = root; }

            internal static BeadBvh Build(List<BeadSegment> segments) =>
                new BeadBvh(BuildNode(segments.ToArray(), 0));

            internal double Evaluate(Point3d point)
            {
                double best = double.PositiveInfinity;
                EvaluateNode(_root, point, ref best);
                return best;
            }

            private static Node BuildNode(BeadSegment[] segments, int depth)
            {
                BoundingBox bounds = segments[0].Bounds;
                for (int i = 1; i < segments.Length; i++) bounds.Union(segments[i].Bounds);
                if (segments.Length <= LeafSize) return new Node(bounds, segments, null, null);

                Vector3d diagonal = bounds.Diagonal;
                int axis = diagonal.X >= diagonal.Y && diagonal.X >= diagonal.Z ? 0 :
                    diagonal.Y >= diagonal.Z ? 1 : 2;
                Array.Sort(segments, (a, b) => Coordinate(a.BoundsCenter, axis)
                    .CompareTo(Coordinate(b.BoundsCenter, axis)));
                int middle = segments.Length / 2;
                BeadSegment[] left = new BeadSegment[middle];
                BeadSegment[] right = new BeadSegment[segments.Length - middle];
                Array.Copy(segments, 0, left, 0, left.Length);
                Array.Copy(segments, middle, right, 0, right.Length);
                return new Node(bounds, null, BuildNode(left, depth + 1), BuildNode(right, depth + 1));
            }

            private static void EvaluateNode(Node node, Point3d point, ref double best)
            {
                if (node == null) return;
                double lower = DistanceToBounds(node.Bounds, point);
                if (best >= 0.0 && lower > best) return;
                if (best < 0.0 && lower > 0.0) return;

                if (node.Segments != null)
                {
                    for (int i = 0; i < node.Segments.Length; i++)
                    {
                        double value = node.Segments[i].Evaluate(point);
                        if (value < best) best = value;
                    }
                    return;
                }

                double leftDistance = DistanceToBounds(node.Left.Bounds, point);
                double rightDistance = DistanceToBounds(node.Right.Bounds, point);
                if (leftDistance <= rightDistance)
                {
                    EvaluateNode(node.Left, point, ref best);
                    EvaluateNode(node.Right, point, ref best);
                }
                else
                {
                    EvaluateNode(node.Right, point, ref best);
                    EvaluateNode(node.Left, point, ref best);
                }
            }

            private static double DistanceToBounds(BoundingBox bounds, Point3d p)
            {
                double dx = p.X < bounds.Min.X ? bounds.Min.X - p.X : p.X > bounds.Max.X ? p.X - bounds.Max.X : 0.0;
                double dy = p.Y < bounds.Min.Y ? bounds.Min.Y - p.Y : p.Y > bounds.Max.Y ? p.Y - bounds.Max.Y : 0.0;
                double dz = p.Z < bounds.Min.Z ? bounds.Min.Z - p.Z : p.Z > bounds.Max.Z ? p.Z - bounds.Max.Z : 0.0;
                return Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }

            private static double Coordinate(Point3d p, int axis) => axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;

            private sealed class Node
            {
                internal readonly BoundingBox Bounds;
                internal readonly BeadSegment[] Segments;
                internal readonly Node Left;
                internal readonly Node Right;

                internal Node(BoundingBox bounds, BeadSegment[] segments, Node left, Node right)
                {
                    Bounds = bounds;
                    Segments = segments;
                    Left = left;
                    Right = right;
                }
            }
        }
    }
}
