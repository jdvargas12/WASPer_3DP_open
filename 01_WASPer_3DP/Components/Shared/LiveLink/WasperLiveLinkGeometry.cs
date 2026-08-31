using System;
using System.Collections.Generic;
using System.Drawing;

using Rhino.Geometry;

using WASPer.LiveLink;

namespace WASPer_3DP.Components.Shared.LiveLink
{
    /// <summary>
    /// Converts Rhino geometry into protocol items. The only place in WASPer that
    /// knows both RhinoCommon and the wire format.
    /// </summary>
    /// <remarks>
    /// Meshing, not serialization, is the expensive step, so render meshes are
    /// cached by source reference identity. The fast path holds whenever upstream
    /// did not recompute, which is the common case when only the camera moved.
    /// </remarks>
    internal sealed class WasperLiveLinkGeometry
    {
        private const int MaxCacheEntries = 4096;

        private readonly Dictionary<object, Mesh[]> _meshCache =
            new Dictionary<object, Mesh[]>(ReferenceEqualityComparer.Instance);

        private double _cacheTolerance = double.NaN;

        private readonly List<string> _skipped = new List<string>();
        private readonly Dictionary<string, int> _skippedCounts =
            new Dictionary<string, int>(StringComparer.Ordinal);

        public IReadOnlyList<string> SkippedSummary => _skipped;

        public int MeshedObjectCount { get; private set; }

        public int CacheHitCount { get; private set; }

        /// <summary>Meshes sent in this frame.</summary>
        public int MeshCount { get; private set; }

        /// <summary>
        /// Meshes that carried vertex colours. A mesh coloured only through Rhino's
        /// display pipeline has none, and arrives at the viewer white — which looks
        /// like the link losing colour rather than the mesh never having any.
        /// </summary>
        public int MeshesWithVertexColors { get; private set; }

        public void BeginFrame(double tolerance)
        {
            if (!EqualsWithin(tolerance, _cacheTolerance))
            {
                // Tolerance drives tessellation, so every cached mesh is stale.
                _meshCache.Clear();
                _cacheTolerance = tolerance;
            }

            if (_meshCache.Count > MaxCacheEntries) _meshCache.Clear();

            _skipped.Clear();
            _skippedCounts.Clear();
            MeshedObjectCount = 0;
            CacheHitCount = 0;
            MeshCount = 0;
            MeshesWithVertexColors = 0;
        }

        public void EndFrame()
        {
            foreach (KeyValuePair<string, int> pair in _skippedCounts)
                _skipped.Add(pair.Value + " x " + pair.Key);
        }

        public void Clear()
        {
            _meshCache.Clear();
            _cacheTolerance = double.NaN;
        }

        /// <summary>
        /// Appends one object. Returns false when the type is not transportable, in
        /// which case it has been recorded for the component's warning.
        /// </summary>
        public bool Append(WasperLiveFrameBuilder builder, int[] path, object obj, double tolerance)
        {
            switch (obj)
            {
                case null:
                    return true;

                case Mesh mesh:
                    AppendMesh(builder, path, mesh);
                    return true;

                case Point3d point:
                    builder.AddPoint(path, point.X, point.Y, point.Z);
                    return true;

                // Fully qualified: System.Drawing.Point is also in scope in this
                // file and a bare `Point` is ambiguous.
                case Rhino.Geometry.Point point:
                    builder.AddPoint(path, point.Location.X, point.Location.Y, point.Location.Z);
                    return true;

                case PointCloud cloud:
                    AppendPointCloud(builder, path, cloud);
                    return true;

                case Curve curve:
                    AppendCurve(builder, path, curve, tolerance);
                    return true;

                case Brep brep:
                    AppendMeshes(builder, path, GetRenderMeshes(brep, tolerance));
                    return true;

                case SubD subd:
                    AppendMeshes(builder, path, GetRenderMeshes(subd, tolerance));
                    return true;

                case Extrusion extrusion:
                    AppendMeshes(builder, path, GetRenderMeshes(extrusion, tolerance));
                    return true;

                case Surface surface:
                    AppendMeshes(builder, path, GetRenderMeshes(surface, tolerance));
                    return true;

                default:
                    RecordSkipped(obj.GetType().Name);
                    return false;
            }
        }

        private void RecordSkipped(string typeName)
        {
            _skippedCounts.TryGetValue(typeName, out int count);
            _skippedCounts[typeName] = count + 1;
        }

        private void AppendMeshes(WasperLiveFrameBuilder builder, int[] path, Mesh[] meshes)
        {
            if (meshes == null) return;
            for (int i = 0; i < meshes.Length; i++)
                AppendMesh(builder, path, meshes[i]);
        }

        private void AppendMesh(WasperLiveFrameBuilder builder, int[] path, Mesh mesh)
        {
            if (mesh == null) return;

            int vertexCount = mesh.Vertices.Count;
            int faceCount = mesh.Faces.Count;
            if (vertexCount == 0 || faceCount == 0) return;

            // Point3dAt rather than the Point3f indexer, so double-precision
            // vertices are honoured when the mesh carries them. The builder then
            // subtracts the frame origin before demoting to float32.
            var vertices = new double[vertexCount * 3];
            for (int i = 0; i < vertexCount; i++)
            {
                Point3d p = mesh.Vertices.Point3dAt(i);
                vertices[i * 3 + 0] = p.X;
                vertices[i * 3 + 1] = p.Y;
                vertices[i * 3 + 2] = p.Z;
            }

            // Quads stay quads. WASPer path ribbons and shell meshes are largely
            // quads and triangulating them here would inflate the face array by a
            // third for no visual gain. Rhino already stores a triangle as D == C.
            var faces = new int[faceCount * 4];
            for (int i = 0; i < faceCount; i++)
            {
                MeshFace face = mesh.Faces[i];
                faces[i * 4 + 0] = face.A;
                faces[i * 4 + 1] = face.B;
                faces[i * 4 + 2] = face.C;
                faces[i * 4 + 3] = face.D;
            }

            MeshCount++;
            if (mesh.VertexColors.Count == vertexCount) MeshesWithVertexColors++;

            float[] normals = null;
            if (mesh.Normals.Count == vertexCount)
            {
                normals = new float[vertexCount * 3];
                for (int i = 0; i < vertexCount; i++)
                {
                    Vector3f n = mesh.Normals[i];
                    normals[i * 3 + 0] = n.X;
                    normals[i * 3 + 1] = n.Y;
                    normals[i * 3 + 2] = n.Z;
                }
            }

            byte[] colors = null;
            if (mesh.VertexColors.Count == vertexCount)
            {
                colors = new byte[vertexCount * 4];
                for (int i = 0; i < vertexCount; i++)
                {
                    Color c = mesh.VertexColors[i];
                    colors[i * 4 + 0] = c.R;
                    colors[i * 4 + 1] = c.G;
                    colors[i * 4 + 2] = c.B;
                    colors[i * 4 + 3] = c.A;
                }
            }

            float[] uvs = null;
            if (mesh.TextureCoordinates.Count == vertexCount)
            {
                uvs = new float[vertexCount * 2];
                for (int i = 0; i < vertexCount; i++)
                {
                    Point2f uv = mesh.TextureCoordinates[i];
                    uvs[i * 2 + 0] = uv.X;
                    uvs[i * 2 + 1] = uv.Y;
                }
            }

            builder.AddMesh(path, vertices, faces, normals, colors, uvs);
        }

        private static void AppendPointCloud(WasperLiveFrameBuilder builder, int[] path, PointCloud cloud)
        {
            Point3d[] points = cloud.GetPoints();
            if (points == null) return;

            for (int i = 0; i < points.Length; i++)
                builder.AddPoint(path, points[i].X, points[i].Y, points[i].Z);
        }

        private void AppendCurve(WasperLiveFrameBuilder builder, int[] path, Curve curve, double tolerance)
        {
            // The common WASPer case: a print path is already a polyline, so this
            // is exact and free.
            if (curve.TryGetPolyline(out Polyline polyline))
            {
                AppendPolyline(builder, path, polyline, curve.IsClosed);
                return;
            }

            // Sample by length rather than adaptively. Display-only, and it uses
            // nothing beyond the most stable part of the Curve API. An adaptive
            // ToPolyline is a later refinement if curved input ever becomes common.
            double length = curve.GetLength();
            if (length <= 0.0 || double.IsNaN(length)) return;

            double step = tolerance > 0.0 ? Math.Max(tolerance * 20.0, length / 4096.0) : length / 256.0;
            int segments = (int)Math.Ceiling(length / step);
            if (segments < 2) segments = 2;
            if (segments > 4096) segments = 4096;

            double[] parameters = curve.DivideByCount(segments, true, out Point3d[] points);
            if (parameters == null || points == null || points.Length < 2) return;

            var flat = new double[points.Length * 3];
            for (int i = 0; i < points.Length; i++)
            {
                flat[i * 3 + 0] = points[i].X;
                flat[i * 3 + 1] = points[i].Y;
                flat[i * 3 + 2] = points[i].Z;
            }

            builder.AddPolyline(path, flat, curve.IsClosed);
        }

        private static void AppendPolyline(WasperLiveFrameBuilder builder, int[] path, Polyline polyline, bool closed)
        {
            if (polyline == null || polyline.Count < 2) return;

            var flat = new double[polyline.Count * 3];
            for (int i = 0; i < polyline.Count; i++)
            {
                Point3d p = polyline[i];
                flat[i * 3 + 0] = p.X;
                flat[i * 3 + 1] = p.Y;
                flat[i * 3 + 2] = p.Z;
            }

            builder.AddPolyline(path, flat, closed || polyline.IsClosed);
        }

        private Mesh[] GetRenderMeshes(object source, double tolerance)
        {
            if (_meshCache.TryGetValue(source, out Mesh[] cached))
            {
                CacheHitCount++;
                return cached;
            }

            Mesh[] meshes = BuildRenderMeshes(source, tolerance);
            MeshedObjectCount++;

            if (_meshCache.Count < MaxCacheEntries)
                _meshCache[source] = meshes;

            return meshes;
        }

        private static Mesh[] BuildRenderMeshes(object source, double tolerance)
        {
            MeshingParameters parameters = MeshingParameters.FastRenderMesh;
            if (tolerance > 0.0) parameters.Tolerance = tolerance;

            switch (source)
            {
                case Brep brep:
                {
                    // Prefer meshes Rhino already generated for display. When every
                    // face has one this costs nothing, which is the whole point of
                    // preferring the workflow's existing display meshes.
                    var existing = new List<Mesh>(brep.Faces.Count);
                    bool complete = brep.Faces.Count > 0;

                    for (int i = 0; i < brep.Faces.Count; i++)
                    {
                        Mesh face = brep.Faces[i].GetMesh(MeshType.Render);
                        if (face == null || face.Faces.Count == 0)
                        {
                            complete = false;
                            break;
                        }
                        existing.Add(face);
                    }

                    if (complete) return existing.ToArray();

                    return Mesh.CreateFromBrep(brep, parameters) ?? Array.Empty<Mesh>();
                }

                case SubD subd:
                {
                    Mesh mesh = Mesh.CreateFromSubD(subd, 1);
                    return mesh == null ? Array.Empty<Mesh>() : new[] { mesh };
                }

                case Extrusion extrusion:
                {
                    Mesh mesh = extrusion.GetMesh(MeshType.Render);
                    if (mesh != null && mesh.Faces.Count > 0) return new[] { mesh };

                    Brep brep = extrusion.ToBrep();
                    return brep == null
                        ? Array.Empty<Mesh>()
                        : Mesh.CreateFromBrep(brep, parameters) ?? Array.Empty<Mesh>();
                }

                case Surface surface:
                {
                    Brep brep = surface.ToBrep();
                    return brep == null
                        ? Array.Empty<Mesh>()
                        : Mesh.CreateFromBrep(brep, parameters) ?? Array.Empty<Mesh>();
                }

                default:
                    return Array.Empty<Mesh>();
            }
        }

        private static bool EqualsWithin(double a, double b)
        {
            if (double.IsNaN(a) || double.IsNaN(b)) return double.IsNaN(a) && double.IsNaN(b);
            return Math.Abs(a - b) < 1e-12;
        }
    }
}
