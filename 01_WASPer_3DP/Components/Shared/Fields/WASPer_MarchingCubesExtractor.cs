// WASPer_MarchingCubesExtractor.cs
// Shared Marching Cubes extraction from pre-sampled 3D scalar grids.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Rhino.Geometry;

namespace WASPer_3DP
{
    internal static class WasperMarchingCubes
    {
        private static readonly int[,] CubeCorners =
        {
            {0,0,0}, {1,0,0}, {1,1,0}, {0,1,0},
            {0,0,1}, {1,0,1}, {1,1,1}, {0,1,1}
        };

        private static readonly int[,] EdgeCorners =
        {
            {0,1}, {1,2}, {2,3}, {3,0},
            {4,5}, {5,6}, {6,7}, {7,4},
            {0,4}, {1,5}, {2,6}, {3,7}
        };

        public static Mesh Extract(
            double[] scalars,
            Point3d[] points,
            int nx,
            int ny,
            int nz,
            double isoLevel,
            double keyTol,
            int maxDegreeOfParallelism = 0,
            bool skipInvalidPoints = false,
            double skipScalarAbove = double.PositiveInfinity)
        {
            if (scalars == null || points == null) return null;
            if (nx < 2 || ny < 2 || nz < 2) return null;

            int expected = nx * ny * nz;
            if (scalars.Length < expected || points.Length < expected) return null;

            int sliceCount = nz - 1;
            var sliceMeshes = new Mesh[sliceCount];
            int threads = maxDegreeOfParallelism > 0
                ? maxDegreeOfParallelism
                : Math.Max(1, Environment.ProcessorCount - 1);

            Parallel.For(0, sliceCount, new ParallelOptions { MaxDegreeOfParallelism = threads }, iz =>
            {
                var localMesh = new Mesh();
                var localVertexMap = new Dictionary<VertexKey, int>();

                ProcessSlice(
                    scalars,
                    points,
                    nx,
                    ny,
                    iz,
                    isoLevel,
                    keyTol,
                    skipInvalidPoints,
                    skipScalarAbove,
                    localMesh,
                    localVertexMap);

                if (localMesh.Faces.Count > 0)
                {
                    localMesh.Vertices.CombineIdentical(true, true);
                    localMesh.Faces.CullDegenerateFaces();
                    localMesh.Vertices.CullUnused();
                    localMesh.Compact();
                    sliceMeshes[iz] = localMesh;
                }
            });

            var mesh = new Mesh();
            foreach (Mesh slice in sliceMeshes)
            {
                if (slice != null && slice.Faces.Count > 0)
                    mesh.Append(slice);
            }

            if (mesh.Faces.Count == 0) return null;

            mesh.Vertices.CombineIdentical(true, true);
            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();
            mesh.Weld(Math.PI);
            mesh.UnifyNormals();
            mesh.Normals.ComputeNormals();
            mesh.FaceNormals.ComputeFaceNormals();
            mesh.Compact();
            return mesh;
        }

        private static void ProcessSlice(
            double[] scalars,
            Point3d[] points,
            int nx,
            int ny,
            int iz,
            double isoLevel,
            double keyTol,
            bool skipInvalidPoints,
            double skipScalarAbove,
            Mesh mesh,
            Dictionary<VertexKey, int> vertexMap)
        {
            double[] sv = new double[8];
            Point3d[] cp = new Point3d[8];
            Point3d[] edgeVerts = new Point3d[12];
            double tol = Math.Max(keyTol, 1e-12);

            for (int iy = 0; iy < ny - 1; iy++)
            for (int ix = 0; ix < nx - 1; ix++)
            {
                int cubeIndex = 0;
                bool skipCube = false;

                for (int c = 0; c < 8; c++)
                {
                    int gx = ix + CubeCorners[c, 0];
                    int gy = iy + CubeCorners[c, 1];
                    int gz = iz + CubeCorners[c, 2];
                    int idx = Idx(gx, gy, gz, nx, ny);

                    double raw = scalars[idx];
                    cp[c] = points[idx];

                    if ((skipInvalidPoints && !cp[c].IsValid) || raw > skipScalarAbove)
                    {
                        skipCube = true;
                        break;
                    }

                    sv[c] = raw - isoLevel;

                    if (sv[c] < 0.0)
                        cubeIndex |= 1 << c;
                }

                if (skipCube)
                    continue;

                if (cubeIndex == 0 || cubeIndex == 255)
                    continue;

                for (int e = 0; e < 12; e++)
                    edgeVerts[e] = Point3d.Unset;

                for (int t = 0; t <= 12; t += 3)
                {
                    int e0 = MarchingCubesClassicTable.TriTable[cubeIndex, t];
                    if (e0 < 0) break;

                    int e1 = MarchingCubesClassicTable.TriTable[cubeIndex, t + 1];
                    int e2 = MarchingCubesClassicTable.TriTable[cubeIndex, t + 2];
                    if (e0 < 0 || e0 >= 12 || e1 < 0 || e1 >= 12 || e2 < 0 || e2 >= 12)
                        break;

                    if (!edgeVerts[e0].IsValid)
                        edgeVerts[e0] = InterpolateEdge(e0, cp, sv);
                    if (!edgeVerts[e1].IsValid)
                        edgeVerts[e1] = InterpolateEdge(e1, cp, sv);
                    if (!edgeVerts[e2].IsValid)
                        edgeVerts[e2] = InterpolateEdge(e2, cp, sv);

                    // SafeEvaluate represents failed/out-of-domain samples as +Infinity. Without
                    // this guard, interpolation across a finite/infinite edge can produce NaN
                    // coordinates, which makes the entire Rhino mesh invalid.
                    if (!edgeVerts[e0].IsValid || !edgeVerts[e1].IsValid || !edgeVerts[e2].IsValid)
                        continue;

                    int a = WasperMcHelpers.AddVertex(mesh, vertexMap, edgeVerts[e0], tol);
                    int b = WasperMcHelpers.AddVertex(mesh, vertexMap, edgeVerts[e1], tol);
                    int c = WasperMcHelpers.AddVertex(mesh, vertexMap, edgeVerts[e2], tol);
                    if (a == b || b == c || c == a) continue;

                    mesh.Faces.AddFace(a, c, b);
                }
            }
        }

        private static Point3d InterpolateEdge(int edgeIndex, Point3d[] cp, double[] sv)
        {
            int a = EdgeCorners[edgeIndex, 0];
            int b = EdgeCorners[edgeIndex, 1];
            double sa = sv[a];
            double sb = sv[b];

            // A non-finite scalar is WASPer's outside/failed-evaluation sentinel. Put the
            // crossing halfway along the edge instead of evaluating Infinity / Infinity.
            // This is symmetric with respect to edge direction and keeps generated vertices
            // finite while still bounding the valid portion of the sampled field.
            if (!IsFinite(sa) || !IsFinite(sb))
                return Midpoint(cp[a], cp[b]);

            double d = sa - sb;
            if (Math.Abs(d) < 1e-14)
                return Midpoint(cp[a], cp[b]);

            double t = sa / d;
            if (!IsFinite(t))
                return Midpoint(cp[a], cp[b]);
            if (t < 0.0) t = 0.0;
            else if (t > 1.0) t = 1.0;
            return cp[a] + t * (cp[b] - cp[a]);
        }

        private static Point3d Midpoint(Point3d a, Point3d b)
        {
            return new Point3d(
                0.5 * (a.X + b.X),
                0.5 * (a.Y + b.Y),
                0.5 * (a.Z + b.Z));
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static int Idx(int ix, int iy, int iz, int nx, int ny)
        {
            return ix + nx * (iy + ny * iz);
        }
    }
}
