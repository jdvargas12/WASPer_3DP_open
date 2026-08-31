// WASPer_PointFieldColorMapper.cs
// WASPer_3DP - shared point-field to mesh-vertex color mapping helpers.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;

using Grasshopper;
using Grasshopper.Kernel.Data;

using Rhino.Geometry;

namespace WASPer_3DP
{
    internal sealed class WasperPointFieldColorResult
    {
        public readonly List<Mesh> Meshes = new List<Mesh>();
        public readonly DataTree<double> VertexValues = new DataTree<double>();
        public readonly DataTree<Color> VertexColors = new DataTree<Color>();
        public int MeshInputCount;
        public int SkippedMeshCount;
        public int TotalVertices;
        public int ColoredVertices;
        public int ParallelWorkers;
    }

    internal static class WasperPointFieldColorMapper
    {
        public static WasperPointFieldColorResult ColorMeshes(
            IList<Mesh> meshes,
            IList<Point3d> points,
            IList<double> values,
            IList<Color> gradient,
            double valueMin,
            double valueMax,
            bool useAverage,
            bool singleColorMode,
            int averageK,
            IList<string> warnings)
        {
            var result = new WasperPointFieldColorResult
            {
                MeshInputCount = meshes?.Count ?? 0,
                ParallelWorkers = Math.Max(1, Environment.ProcessorCount)
            };

            AddScaleMismatchWarning(meshes, points, warnings);

            var spatialIndex = new PointValueKdTree(points, values);

            for (int m = 0; m < (meshes?.Count ?? 0); m++)
            {
                Mesh source = meshes[m];
                if (source == null || !source.IsValid || source.Vertices.Count == 0)
                {
                    result.SkippedMeshCount++;
                    warnings?.Add($"Mesh {m} was null, invalid, or had no vertices. It was skipped.");
                    continue;
                }

                Mesh mesh = source.DuplicateMesh();
                mesh.VertexColors.CreateMonotoneMesh(Color.White);

                GH_Path path = new GH_Path(m);
                int vertexCount = mesh.Vertices.Count;
                result.TotalVertices += vertexCount;

                var vertexValues = new double[vertexCount];
                var vertexColors = new Color[vertexCount];

                Parallel.For(
                    0,
                    vertexCount,
                    new ParallelOptions { MaxDegreeOfParallelism = result.ParallelWorkers },
                    v =>
                    {
                        Point3f pf = mesh.Vertices[v];
                        var vertexPoint = new Point3d(pf.X, pf.Y, pf.Z);

                        double vertexValue = spatialIndex.MapValue(
                            vertexPoint,
                            useAverage ? averageK : 1,
                            useAverage);

                        Color vertexColor = singleColorMode
                            ? gradient[0]
                            : ColorFromGradient(vertexValue, valueMin, valueMax, gradient);

                        vertexValues[v] = vertexValue;
                        vertexColors[v] = vertexColor;
                    });

                for (int v = 0; v < vertexCount; v++)
                {
                    mesh.VertexColors[v] = vertexColors[v];
                    result.VertexValues.Add(vertexValues[v], path);
                    result.VertexColors.Add(vertexColors[v], path);
                    result.ColoredVertices++;
                }

                mesh.Normals.ComputeNormals();
                mesh.Compact();
                result.Meshes.Add(mesh);
            }

            return result;
        }

        private static void AddScaleMismatchWarning(IList<Mesh> meshes, IList<Point3d> points, IList<string> warnings)
        {
            if (warnings == null || meshes == null || points == null || meshes.Count == 0 || points.Count == 0)
                return;

            BoundingBox meshBox = BoundingBox.Empty;
            for (int i = 0; i < meshes.Count; i++)
            {
                Mesh mesh = meshes[i];
                if (mesh != null && mesh.IsValid && mesh.Vertices.Count > 0)
                    meshBox.Union(mesh.GetBoundingBox(false));
            }

            if (!meshBox.IsValid)
                return;

            var pointBox = new BoundingBox(points);
            if (!pointBox.IsValid)
                return;

            double meshDiag = meshBox.Diagonal.Length;
            double pointDiag = pointBox.Diagonal.Length;
            if (meshDiag <= 1e-12 || pointDiag <= 1e-12)
                return;

            double ratio = meshDiag > pointDiag ? meshDiag / pointDiag : pointDiag / meshDiag;
            if (ratio > 50.0)
            {
                warnings.Add(
                    "Mesh and point-field bounding boxes differ by a scale ratio of about " +
                    ratio.ToString("0.#") +
                    ". Check that mesh vertices and pts use the same units; unit mismatch can make nearest-neighbor mapping very slow and visually incorrect.");
            }
        }

        public static List<Color> DefaultGradient()
        {
            return new List<Color>
            {
                Color.Blue,
                Color.Cyan,
                Color.Yellow,
                Color.Red
            };
        }

        public static double[] MapValues(
            IList<Point3d> sourcePoints,
            IList<double> sourceValues,
            IList<Point3d> targetPoints,
            bool useAverage,
            int averageK = 4)
        {
            var mapped = new double[targetPoints?.Count ?? 0];
            if (mapped.Length == 0) return mapped;
            var spatialIndex = new PointValueKdTree(sourcePoints, sourceValues);
            int k = useAverage ? Math.Max(1, averageK) : 1;
            Parallel.For(0, mapped.Length, i =>
                mapped[i] = spatialIndex.MapValue(targetPoints[i], k, useAverage));
            return mapped;
        }

        public static Color ColorFromGradient(double value, double valueMin, double valueMax, IList<Color> gradient)
        {
            if (gradient == null || gradient.Count == 0)
                gradient = DefaultGradient();

            if (gradient.Count == 1 || Math.Abs(valueMax - valueMin) <= 1e-12)
                return gradient[0];

            double u = Clamp01((value - valueMin) / (valueMax - valueMin));
            double scaled = u * (gradient.Count - 1);
            int i0 = (int)Math.Floor(scaled);
            int i1 = Math.Min(i0 + 1, gradient.Count - 1);
            double f = scaled - i0;

            return LerpColor(gradient[i0], gradient[i1], f);
        }

        private static Color LerpColor(Color a, Color b, double t)
        {
            t = Clamp01(t);

            int r = (int)Math.Round(a.R + (b.R - a.R) * t);
            int g = (int)Math.Round(a.G + (b.G - a.G) * t);
            int bch = (int)Math.Round(a.B + (b.B - a.B) * t);
            int alpha = (int)Math.Round(a.A + (b.A - a.A) * t);

            return Color.FromArgb(
                ClampByte(alpha),
                ClampByte(r),
                ClampByte(g),
                ClampByte(bch));
        }

        private static double Clamp01(double value)
        {
            if (double.IsNaN(value)) return 0.0;
            if (value < 0.0) return 0.0;
            if (value > 1.0) return 1.0;
            return value;
        }

        private static int ClampByte(int value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return value;
        }

        private sealed class PointValueKdTree
        {
            private const double ExactTol2 = 1e-18;

            private readonly IList<Point3d> _points;
            private readonly IList<double> _values;
            private readonly Node _root;

            public PointValueKdTree(IList<Point3d> points, IList<double> values)
            {
                _points = points ?? new List<Point3d>();
                _values = values ?? new List<double>();
                var indices = Enumerable.Range(0, _points.Count).ToList();
                _root = Build(indices, 0);
            }

            public double MapValue(Point3d query, int k, bool useAverage)
            {
                if (_points.Count == 0) return double.NaN;

                int targetCount = Math.Max(1, Math.Min(k, _points.Count));
                var best = new BestSet(targetCount);
                Search(_root, query, best);

                int first = best.Indices[0];
                if (first < 0) return _values[0];

                if (!useAverage || targetCount == 1 || best.Distances[0] <= ExactTol2)
                    return _values[first];

                double weightedValue = 0.0;
                double weightSum = 0.0;

                for (int i = 0; i < targetCount; i++)
                {
                    int index = best.Indices[i];
                    if (index < 0) continue;

                    double w = 1.0 / Math.Max(best.Distances[i], 1e-24);
                    weightedValue += _values[index] * w;
                    weightSum += w;
                }

                return weightSum > 1e-24 ? weightedValue / weightSum : _values[first];
            }

            private Node Build(List<int> indices, int depth)
            {
                if (indices == null || indices.Count == 0) return null;

                int axis = depth % 3;
                indices.Sort((a, b) => Coordinate(_points[a], axis).CompareTo(Coordinate(_points[b], axis)));

                int mid = indices.Count / 2;
                int pointIndex = indices[mid];

                return new Node
                {
                    Index = pointIndex,
                    Axis = axis,
                    Split = Coordinate(_points[pointIndex], axis),
                    Left = Build(indices.GetRange(0, mid), depth + 1),
                    Right = Build(indices.GetRange(mid + 1, indices.Count - mid - 1), depth + 1)
                };
            }

            private void Search(Node node, Point3d query, BestSet best)
            {
                if (node == null) return;

                double d2 = query.DistanceToSquared(_points[node.Index]);
                best.TryAdd(node.Index, d2);

                double delta = Coordinate(query, node.Axis) - node.Split;
                Node near = delta <= 0.0 ? node.Left : node.Right;
                Node far = delta <= 0.0 ? node.Right : node.Left;

                Search(near, query, best);

                if (delta * delta <= best.WorstDistance)
                    Search(far, query, best);
            }

            private static double Coordinate(Point3d p, int axis)
            {
                if (axis == 0) return p.X;
                if (axis == 1) return p.Y;
                return p.Z;
            }

            private sealed class Node
            {
                public int Index;
                public int Axis;
                public double Split;
                public Node Left;
                public Node Right;
            }

            private sealed class BestSet
            {
                public readonly int[] Indices;
                public readonly double[] Distances;

                public double WorstDistance => Distances[Distances.Length - 1];

                public BestSet(int count)
                {
                    Indices = new int[count];
                    Distances = new double[count];
                    for (int i = 0; i < count; i++)
                    {
                        Indices[i] = -1;
                        Distances[i] = double.PositiveInfinity;
                    }
                }

                public void TryAdd(int index, double distance)
                {
                    if (distance >= Distances[Distances.Length - 1])
                        return;

                    int insertAt = Distances.Length - 1;
                    while (insertAt > 0 && distance < Distances[insertAt - 1])
                    {
                        Distances[insertAt] = Distances[insertAt - 1];
                        Indices[insertAt] = Indices[insertAt - 1];
                        insertAt--;
                    }

                    Distances[insertAt] = distance;
                    Indices[insertAt] = index;
                }
            }
        }
    }
}
