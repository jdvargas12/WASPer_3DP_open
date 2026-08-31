// WASPer_GridTools.cs
// WASPer_3DP - shared point-grid helpers used by public geometry workflows.

using System;
using System.Collections.Generic;
using System.Globalization;

using Rhino.Geometry;

namespace WASPer_3DP
{
    internal readonly struct WasperGridKey : IEquatable<WasperGridKey>
    {
        public readonly long X;
        public readonly long Y;
        public readonly long Z;

        public WasperGridKey(long x, long y, long z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public bool Equals(WasperGridKey other) => X == other.X && Y == other.Y && Z == other.Z;
        public override bool Equals(object obj) => obj is WasperGridKey other && Equals(other);
        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + X.GetHashCode();
                hash = hash * 31 + Y.GetHashCode();
                hash = hash * 31 + Z.GetHashCode();
                return hash;
            }
        }

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "{0},{1},{2}", X, Y, Z);
    }

    internal readonly struct WasperGridSpacing
    {
        public readonly double Dx;
        public readonly double Dy;
        public readonly double Dz;

        public WasperGridSpacing(double dx, double dy, double dz)
        {
            Dx = dx;
            Dy = dy;
            Dz = dz;
        }
    }

    internal static class WasperGridTools
    {
        public static WasperGridKey Key(Point3d p, int decimals)
        {
            double scale = Math.Pow(10.0, Clamp(decimals, 0, 10));
            return new WasperGridKey(
                (long)Math.Round(p.X * scale, MidpointRounding.AwayFromZero),
                (long)Math.Round(p.Y * scale, MidpointRounding.AwayFromZero),
                (long)Math.Round(p.Z * scale, MidpointRounding.AwayFromZero));
        }

        public static Point3d RoundPoint(Point3d p, int decimals)
        {
            decimals = Clamp(decimals, 0, 10);
            return new Point3d(
                Math.Round(p.X, decimals),
                Math.Round(p.Y, decimals),
                Math.Round(p.Z, decimals));
        }

        public static WasperGridSpacing EstimateMedianSpacing(IEnumerable<Point3d> points, int decimals)
        {
            var xs = new SortedSet<double>();
            var ys = new SortedSet<double>();
            var zs = new SortedSet<double>();

            foreach (Point3d p in points)
            {
                xs.Add(Math.Round(p.X, decimals));
                ys.Add(Math.Round(p.Y, decimals));
                zs.Add(Math.Round(p.Z, decimals));
            }

            return new WasperGridSpacing(
                MedianPositiveGap(xs),
                MedianPositiveGap(ys),
                MedianPositiveGap(zs));
        }

        public static List<(int i, int j, int axis)> BuildSixNeighborLinks(
            IReadOnlyList<Point3d> points,
            IReadOnlyDictionary<WasperGridKey, int> pointIndex,
            WasperGridSpacing spacing,
            int decimals)
        {
            var links = new List<(int i, int j, int axis)>();
            double[] step = { spacing.Dx, spacing.Dy, spacing.Dz };

            for (int i = 0; i < points.Count; i++)
            {
                Point3d p = points[i];

                for (int axis = 0; axis < 3; axis++)
                {
                    if (!IsFinite(step[axis]) || step[axis] <= 1e-12)
                        continue;

                    Point3d q = p;
                    if (axis == 0) q.X += step[axis];
                    else if (axis == 1) q.Y += step[axis];
                    else q.Z += step[axis];

                    if (pointIndex.TryGetValue(Key(q, decimals), out int j) && i < j)
                        links.Add((i, j, axis));
                }
            }

            return links;
        }

        public static bool IsFinite(double value) =>
            !(double.IsNaN(value) || double.IsInfinity(value));

        public static int Clamp(int value, int min, int max) =>
            value < min ? min : value > max ? max : value;

        private static double MedianPositiveGap(SortedSet<double> values)
        {
            if (values.Count < 2) return 0.0;

            var gaps = new List<double>();
            bool first = true;
            double prev = 0.0;

            foreach (double v in values)
            {
                if (first)
                {
                    prev = v;
                    first = false;
                    continue;
                }

                double gap = v - prev;
                if (gap > 1e-12) gaps.Add(gap);
                prev = v;
            }

            if (gaps.Count == 0) return 0.0;
            gaps.Sort();

            int mid = gaps.Count / 2;
            return (gaps.Count % 2 == 1)
                ? gaps[mid]
                : 0.5 * (gaps[mid - 1] + gaps[mid]);
        }
    }
}
