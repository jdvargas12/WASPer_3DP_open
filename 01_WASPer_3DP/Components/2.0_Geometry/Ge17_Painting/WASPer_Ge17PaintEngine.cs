using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;

using WASPer_3DP.Painting;

namespace WASPer_3DP_Components._2_0_Geometry
{
    public sealed partial class wsp_Ge17_Paint_Mesh_Field
    {
        internal WasperPaintTool ActiveTool => _tool;
        internal WasperSmoothRegionShape SmoothRegionShape =>
            _smoothRegionShape;
        internal bool PreviewEnabled => _preview;
        internal bool LiveEnabled => _live;
        internal bool UpdateEnabled => !_live;
        internal bool HasPendingUpdate =>
            !_live && !WasperPaintUtilities.ValuesEqual(_values, _appliedValues);
        internal Mesh PainterMesh => _painterMesh;
        internal double PainterRadius => _radius;
        internal double PainterBrushStrength => _strength;
        internal double PainterSmoothStrength => _smoothStrength;
        internal int PainterVisualRevision => _painterVisualRevision;
        internal bool CanUndo => _undo.Count > 0;
        internal bool CanRedo => _redo.Count > 0;
        internal IList<WasperPaintTextureLayer> TextureLayers => _textureLayers;
        internal int ActiveTextureLayer => _activeTextureLayer;
        internal bool TextureVisible =>
            ActiveTexture.Bitmap != null && ActiveTexture.Visible;
        internal bool TextureEditMode =>
            TextureVisible && ActiveTexture.EditMode;
        internal bool TextureDistortMode =>
            TextureVisible && ActiveTexture.DistortMode;
        internal bool TextureRotateMode =>
            TextureVisible && ActiveTexture.RotateMode;
        internal bool TextureHandlesVisible =>
            TextureVisible &&
            (TextureEditMode || TextureDistortMode || TextureRotateMode);
        internal bool AtlasFlipMap => _atlasFlipMap;
        internal int AtlasQuarterTurns => _atlasQuarterTurns;
        internal IList<WasperPaintMarker> AtlasMarkers => _atlasMarkers;

        internal bool PainterInputEditable(int index)
        {
            return index >= 0 && index < Params.Input.Count &&
                   Params.Input[index].SourceCount == 0 &&
                   Params.Input[index] is Param_Number;
        }

        internal void TogglePainterForm()
        {
            if (_paintForm == null || _paintForm.IsClosed)
                _paintForm = new WasperEtoPaintForm(new Ge17v2PainterHost(this));
            _paintForm.ShowNearCursor();
        }

        internal void TogglePreview()
        {
            _preview = !_preview;
            UpdateConduit();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal void ToggleLive()
        {
            _live = !_live;
            if (_live)
            {
                ApplyWorkingValues();
                ScheduleSolution();
            }
            _paintForm?.RefreshCanvas();
        }

        internal void UpdateAlgorithm()
        {
            if (_live)
                return;
            ApplyWorkingValues();
            _meshUpdateRequested = true;
            RebuildDisplayMeshes();
            ScheduleSolution();
            _paintForm?.RefreshCanvas();
        }

        internal void UpdateMeshOutput()
        {
            ApplyWorkingValues();
            _meshUpdateRequested = true;
            RebuildDisplayMeshes();
            UpdateConduit();
            ScheduleSolution();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal void SetPainterTool(WasperPaintTool tool)
        {
            _tool = _tool == tool ? WasperPaintTool.None : tool;
            UpdateConduit();
            _paintForm?.RefreshCanvas();
        }

        internal void SetSmoothRegionShape(WasperSmoothRegionShape shape)
        {
            _smoothRegionShape = shape;
            _tool = WasperPaintTool.Smooth;
            UpdateConduit();
            _paintForm?.RefreshCanvas();
        }

        internal void ApplySmoothRegion(IList<Point3d> boundary)
        {
            if (_painterMesh == null || _sourceMesh == null ||
                boundary == null || boundary.Count < 3)
                return;
            var selected = new HashSet<int>();
            for (int atlasIndex = 0; atlasIndex < _painterMesh.Vertices.Count; atlasIndex++)
            {
                if (!WasperPaintRegion.Contains(
                        _painterMesh.Vertices.Point3dAt(atlasIndex),
                        boundary,
                        Plane.WorldXY))
                    continue;
                int source = _painterSourceIndices[atlasIndex];
                if (source >= 0 && source < _values.Length && _eligible[source])
                    selected.Add(source);
            }
            if (selected.Count == 0)
                return;
            Ge17v2PainterSnapshot before = CaptureSnapshot();
            double[] sourceValues = (double[])_values.Clone();
            bool changed = false;
            foreach (int index in selected)
            {
                int[] neighbors = _sourceMesh.Vertices.GetConnectedVertices(index);
                double sum = sourceValues[index];
                int count = 1;
                foreach (int neighbor in neighbors)
                {
                    if (neighbor < 0 || neighbor >= sourceValues.Length)
                        continue;
                    sum += sourceValues[neighbor];
                    count++;
                }
                double value = WasperPaintBrushKernel.SmoothValue(
                    sourceValues[index],
                    sum / count,
                    _smoothStrength,
                    _domain);
                if (Math.Abs(value - _values[index]) <= 1e-12)
                    continue;
                _values[index] = value;
                changed = true;
            }
            if (!changed)
                return;
            PushUndo(before);
            if (_live)
            {
                ApplyWorkingValues();
                ScheduleSolution();
            }
            RebuildDisplayMeshes();
            UpdateConduit();
            _paintForm?.RefreshCanvas();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal void PreviewPainterSettings(
            double radius,
            double brushStrength,
            double smoothStrength)
        {
            if (PainterInputEditable(1))
            {
                _radius = Math.Max(0.001, radius);
                _uiRadiusOverride = _radius;
            }
            if (PainterInputEditable(2))
            {
                _strength = Math.Max(0.0, Math.Min(1.0, brushStrength));
                _uiStrengthOverride = _strength;
            }
            if (PainterInputEditable(3))
            {
                _smoothStrength = Math.Max(0.0, Math.Min(1.0, smoothStrength));
                _uiSmoothOverride = _smoothStrength;
            }
        }

        internal void CommitPainterSettings(
            double radius,
            double brushStrength,
            double smoothStrength)
        {
            PreviewPainterSettings(radius, brushStrength, smoothStrength);
            SetPersistentNumber(1, Math.Max(0.001, radius));
            SetPersistentNumber(2, Math.Max(0.0, Math.Min(1.0, brushStrength)));
            SetPersistentNumber(3, Math.Max(0.0, Math.Min(1.0, smoothStrength)));
            // These values affect only the interactive painter. Keep the
            // unconnected input defaults in sync without recomputing Ge17.
        }

        private void SetPersistentNumber(int index, double value)
        {
            if (!PainterInputEditable(index) ||
                !(Params.Input[index] is Param_Number parameter))
                return;
            parameter.PersistentData.Clear();
            parameter.PersistentData.Append(new GH_Number(value));
        }

        private void BuildAtlas(double tolerance)
        {
            if (_sourceMesh == null)
                return;
            _painterSourceIndices.Clear();
            _atlasMarkers.Clear();
            _referenceMarkers.Clear();

            Mesh mapped = _sourceMesh.DuplicateMesh();
            var seamSourceEdges = new HashSet<long>();
            var seamTopologyEdges = FindSeamTopologyEdges(mapped, tolerance);
            List<List<int>> referenceRows = FindReferenceRows(
                mapped,
                tolerance,
                seamTopologyEdges);
            bool usedSeam = seamTopologyEdges.Count > 0;
            if (usedSeam)
            {
                foreach (int edgeIndex in seamTopologyEdges)
                {
                    IndexPair topology = mapped.TopologyEdges.GetTopologyVertices(edgeIndex);
                    int a = mapped.TopologyVertices.MeshVertexIndices(topology.I)[0];
                    int b = mapped.TopologyVertices.MeshVertexIndices(topology.J)[0];
                    seamSourceEdges.Add(VertexPairKey(a, b));
                }
                mapped.TextureCoordinates.Clear();
                usedSeam = mapped.UnweldEdge(seamTopologyEdges, false);
            }

            bool usedExisting = !usedSeam && HasUsableTextureCoordinates(mapped);
            if (!usedExisting)
            {
                try
                {
                    using var unwrapper = new MeshUnwrapper(mapped);
                    unwrapper.Unwrap(MeshUnwrapMethod.LSCM);
                }
                catch
                {
                    // The planar fallback below remains deterministic.
                }
            }

            BuildPainterSourceMap(mapped, tolerance);
            bool hasUv = HasUsableTextureCoordinates(mapped) &&
                         _painterSourceIndices.Count == mapped.Vertices.Count;
            var atlasPoints = new Point3d[mapped.Vertices.Count];
            if (hasUv)
            {
                double scale = AtlasUvScale(mapped);
                for (int index = 0; index < atlasPoints.Length; index++)
                {
                    Point2f uv = mapped.TextureCoordinates[index];
                    atlasPoints[index] = new Point3d(uv.X * scale, uv.Y * scale, 0.0);
                }
                _atlasNotice = usedSeam
                    ? $"The atlas was cut along {seamTopologyEdges.Count} mesh edge(s) and unwrapped with Rhino LSCM."
                    : usedExisting
                        ? "The painter atlas uses the mesh's existing texture coordinates."
                        : _seamCurves.Count > 0
                            ? "No connected mesh-edge seam could be resolved; Rhino LSCM generated the atlas without an explicit cut."
                            : "The painter atlas was generated with Rhino's LSCM mesh unwrapper.";
            }
            else
            {
                Plane plane;
                Point3d[] points = _sourceMesh.Vertices
                    .Select(vertex => (Point3d)vertex)
                    .ToArray();
                if (Plane.FitPlaneToPoints(points, out plane) != PlaneFitResult.Success)
                    plane = Plane.WorldXY;
                for (int index = 0; index < atlasPoints.Length; index++)
                {
                    Point3d point = mapped.Vertices.Point3dAt(index);
                    plane.ClosestParameter(point, out double x, out double y);
                    atlasPoints[index] = new Point3d(x, y, 0.0);
                }
                _atlasNotice =
                    "Mesh unwrapping was unavailable; the painter is using a best-fit planar atlas.";
            }

            _painterMesh = new Mesh();
            foreach (Point3d point in atlasPoints)
                _painterMesh.Vertices.Add(point);
            foreach (MeshFace face in mapped.Faces)
                _painterMesh.Faces.AddFace(face);
            _painterMesh.Normals.ComputeNormals();
            UpdatePainterColors();
            BuildReferenceMarkers(seamSourceEdges, referenceRows);
            _painterVisualRevision++;
        }

        private List<int> FindSeamTopologyEdges(Mesh mesh, double tolerance)
        {
            var result = new HashSet<int>();
            foreach (Curve seam in _seamCurves)
            {
                PolylineCurve pulled = mesh.PullCurve(seam, tolerance);
                if (pulled == null || !pulled.TryGetPolyline(out Polyline polyline) ||
                    polyline.Count < 2)
                    continue;
                var topologyVertices = new List<int>();
                foreach (Point3d point in polyline)
                {
                    int nearest = ClosestTopologyVertex(mesh, point);
                    if (nearest >= 0 &&
                        (topologyVertices.Count == 0 || topologyVertices.Last() != nearest))
                        topologyVertices.Add(nearest);
                }
                for (int index = 1; index < topologyVertices.Count; index++)
                {
                    foreach (int edge in ShortestTopologyPath(
                                 mesh,
                                 topologyVertices[index - 1],
                                 topologyVertices[index]))
                        result.Add(edge);
                }
            }
            return result.ToList();
        }

        private static int ClosestTopologyVertex(Mesh mesh, Point3d point)
        {
            int closestIndex = -1;
            double closestDistance = double.PositiveInfinity;
            for (int index = 0; index < mesh.TopologyVertices.Count; index++)
            {
                double distance = ((Point3d)mesh.TopologyVertices[index])
                    .DistanceToSquared(point);
                if (distance >= closestDistance)
                    continue;
                closestDistance = distance;
                closestIndex = index;
            }
            return closestIndex;
        }

        private static IEnumerable<int> ShortestTopologyPath(
            Mesh mesh,
            int start,
            int end)
        {
            if (start == end)
                return Array.Empty<int>();
            int count = mesh.TopologyVertices.Count;
            var distance = Enumerable.Repeat(double.PositiveInfinity, count).ToArray();
            var previousVertex = Enumerable.Repeat(-1, count).ToArray();
            var previousEdge = Enumerable.Repeat(-1, count).ToArray();
            var visited = new bool[count];
            var queue = new PriorityQueue<int, double>();
            distance[start] = 0.0;
            queue.Enqueue(start, 0.0);
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                if (visited[current])
                    continue;
                visited[current] = true;
                if (current == end)
                    break;
                int[] neighbors = mesh.TopologyVertices.ConnectedTopologyVertices(current);
                if (neighbors == null)
                    continue;
                foreach (int neighbor in neighbors)
                {
                    int edge = mesh.TopologyEdges.GetEdgeIndex(current, neighbor);
                    if (edge < 0)
                        continue;
                    double candidate = distance[current] + mesh.TopologyEdges.EdgeLine(edge).Length;
                    if (candidate >= distance[neighbor])
                        continue;
                    distance[neighbor] = candidate;
                    previousVertex[neighbor] = current;
                    previousEdge[neighbor] = edge;
                    queue.Enqueue(neighbor, candidate);
                }
            }
            if (previousEdge[end] < 0)
                return Array.Empty<int>();
            var path = new List<int>();
            for (int current = end; current != start; current = previousVertex[current])
            {
                if (current < 0 || previousEdge[current] < 0)
                    return Array.Empty<int>();
                path.Add(previousEdge[current]);
            }
            path.Reverse();
            return path;
        }

        private List<List<int>> FindReferenceRows(
            Mesh mesh,
            double tolerance,
            IList<int> seamTopologyEdges)
        {
            var seamVertices = new HashSet<int>();
            foreach (int edge in seamTopologyEdges)
            {
                IndexPair pair = mesh.TopologyEdges.GetTopologyVertices(edge);
                seamVertices.Add(pair.I);
                seamVertices.Add(pair.J);
            }
            var rows = new List<List<int>>();
            foreach (Curve edgeCurve in _referenceEdgeCurves)
            {
                PolylineCurve pulled = mesh.PullCurve(edgeCurve, tolerance);
                if (pulled == null || !pulled.TryGetPolyline(out Polyline polyline) ||
                    polyline.Count < 2)
                    continue;
                var anchors = new List<int>();
                foreach (Point3d point in polyline)
                {
                    int nearest = ClosestTopologyVertex(mesh, point);
                    if (nearest >= 0 && (anchors.Count == 0 || anchors.Last() != nearest))
                        anchors.Add(nearest);
                }
                var topologyRow = new List<int>();
                if (anchors.Count > 0)
                    topologyRow.Add(anchors[0]);
                for (int index = 1; index < anchors.Count; index++)
                {
                    int current = topologyRow.Last();
                    foreach (int pathEdge in ShortestTopologyPath(
                                 mesh,
                                 current,
                                 anchors[index]))
                    {
                        IndexPair pair = mesh.TopologyEdges.GetTopologyVertices(pathEdge);
                        current = pair.I == current ? pair.J : pair.I;
                        if (topologyRow.Last() != current)
                            topologyRow.Add(current);
                    }
                }
                if (topologyRow.Count < 2)
                    continue;
                bool closed = topologyRow[0] == topologyRow[topologyRow.Count - 1] ||
                              pulled.IsClosed;
                if (closed && topologyRow[0] == topologyRow[topologyRow.Count - 1])
                    topologyRow.RemoveAt(topologyRow.Count - 1);
                if (closed && seamVertices.Count > 0)
                {
                    int start = 0;
                    double best = double.PositiveInfinity;
                    for (int index = 0; index < topologyRow.Count; index++)
                    {
                        Point3d point = (Point3d)mesh.TopologyVertices[topologyRow[index]];
                        foreach (int seamVertex in seamVertices)
                        {
                            double distance = point.DistanceToSquared(
                                (Point3d)mesh.TopologyVertices[seamVertex]);
                            if (distance < best)
                            {
                                best = distance;
                                start = index;
                            }
                        }
                    }
                    topologyRow = topologyRow.Skip(start)
                        .Concat(topologyRow.Take(start))
                        .ToList();
                }
                var sourceRow = topologyRow
                    .Select(vertex => mesh.TopologyVertices.MeshVertexIndices(vertex)[0])
                    .ToList();
                if (closed)
                    sourceRow.Add(sourceRow[0]);
                rows.Add(sourceRow);
            }
            AlignReferenceRowDirections(rows);
            return rows;
        }

        private void AlignReferenceRowDirections(IList<List<int>> rows)
        {
            for (int row = 1; row < rows.Count; row++)
            {
                if (rows[row - 1].Count < 3 || rows[row].Count < 3)
                    continue;
                Point3d previousQuarter = SampleSourceRow(rows[row - 1], 0.25);
                Point3d forwardQuarter = SampleSourceRow(rows[row], 0.25);
                Point3d reverseQuarter = SampleSourceRow(rows[row], 0.75);
                if (previousQuarter.DistanceToSquared(reverseQuarter) >=
                    previousQuarter.DistanceToSquared(forwardQuarter))
                    continue;
                bool closed = rows[row][0] == rows[row][rows[row].Count - 1];
                if (closed)
                    rows[row].RemoveAt(rows[row].Count - 1);
                int first = rows[row][0];
                rows[row].Reverse();
                int firstIndex = rows[row].IndexOf(first);
                rows[row] = rows[row].Skip(firstIndex)
                    .Concat(rows[row].Take(firstIndex))
                    .ToList();
                if (closed)
                    rows[row].Add(rows[row][0]);
            }
        }

        private Point3d SampleSourceRow(IList<int> row, double fraction)
        {
            if (row == null || row.Count == 0)
                return Point3d.Unset;
            if (row.Count == 1)
                return _sourceMesh.Vertices.Point3dAt(row[0]);
            var lengths = new double[row.Count];
            for (int index = 1; index < row.Count; index++)
            {
                lengths[index] = lengths[index - 1] +
                    _sourceMesh.Vertices.Point3dAt(row[index - 1]).DistanceTo(
                        _sourceMesh.Vertices.Point3dAt(row[index]));
            }
            double target = lengths[lengths.Length - 1] *
                            Math.Max(0.0, Math.Min(1.0, fraction));
            for (int index = 1; index < row.Count; index++)
            {
                if (lengths[index] < target)
                    continue;
                double segment = lengths[index] - lengths[index - 1];
                double t = segment <= 1e-12
                    ? 0.0
                    : (target - lengths[index - 1]) / segment;
                Point3d a = _sourceMesh.Vertices.Point3dAt(row[index - 1]);
                Point3d b = _sourceMesh.Vertices.Point3dAt(row[index]);
                return a + (b - a) * t;
            }
            return _sourceMesh.Vertices.Point3dAt(row[row.Count - 1]);
        }

        private void BuildPainterSourceMap(Mesh mapped, double tolerance)
        {
            double cell = Math.Max(tolerance, 1e-7);
            var buckets = new Dictionary<(long, long, long), List<int>>();
            for (int index = 0; index < _sourceMesh.Vertices.Count; index++)
            {
                var key = PointKey(_sourceMesh.Vertices.Point3dAt(index), cell);
                if (!buckets.TryGetValue(key, out List<int> indices))
                    buckets[key] = indices = new List<int>();
                indices.Add(index);
            }
            for (int index = 0; index < mapped.Vertices.Count; index++)
            {
                Point3d point = mapped.Vertices.Point3dAt(index);
                var key = PointKey(point, cell);
                int closest = -1;
                double best = double.PositiveInfinity;
                if (buckets.TryGetValue(key, out List<int> candidates))
                {
                    foreach (int source in candidates)
                    {
                        double distance = point.DistanceToSquared(
                            _sourceMesh.Vertices.Point3dAt(source));
                        if (distance < best)
                        {
                            best = distance;
                            closest = source;
                        }
                    }
                }
                if (closest < 0)
                {
                    for (int source = 0; source < _sourceMesh.Vertices.Count; source++)
                    {
                        double distance = point.DistanceToSquared(
                            _sourceMesh.Vertices.Point3dAt(source));
                        if (distance < best)
                        {
                            best = distance;
                            closest = source;
                        }
                    }
                }
                _painterSourceIndices.Add(Math.Max(0, closest));
            }
        }

        private static (long, long, long) PointKey(Point3d point, double cell)
        {
            return (
                (long)Math.Round(point.X / cell),
                (long)Math.Round(point.Y / cell),
                (long)Math.Round(point.Z / cell));
        }

        private static bool HasUsableTextureCoordinates(Mesh mesh)
        {
            if (mesh == null ||
                mesh.TextureCoordinates.Count != mesh.Vertices.Count ||
                mesh.TextureCoordinates.Count == 0)
                return false;
            float minX = float.PositiveInfinity;
            float minY = float.PositiveInfinity;
            float maxX = float.NegativeInfinity;
            float maxY = float.NegativeInfinity;
            foreach (Point2f point in mesh.TextureCoordinates)
            {
                if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
                    return false;
                minX = Math.Min(minX, point.X);
                minY = Math.Min(minY, point.Y);
                maxX = Math.Max(maxX, point.X);
                maxY = Math.Max(maxY, point.Y);
            }
            return maxX - minX > 1e-6f && maxY - minY > 1e-6f;
        }

        private double AtlasUvScale(Mesh mapped)
        {
            var ratios = new List<double>();
            var edges = new HashSet<long>();
            foreach (MeshFace face in mapped.Faces)
            {
                int[] vertices = face.IsQuad
                    ? new[] { face.A, face.B, face.C, face.D }
                    : new[] { face.A, face.B, face.C };
                for (int edge = 0; edge < vertices.Length; edge++)
                {
                    int a = vertices[edge];
                    int b = vertices[(edge + 1) % vertices.Length];
                    int min = Math.Min(a, b);
                    int max = Math.Max(a, b);
                    long key = ((long)min << 32) | (uint)max;
                    if (!edges.Add(key))
                        continue;
                    int sourceA = _painterSourceIndices[a];
                    int sourceB = _painterSourceIndices[b];
                    double world = _sourceMesh.Vertices.Point3dAt(sourceA)
                        .DistanceTo(_sourceMesh.Vertices.Point3dAt(sourceB));
                    Point2f first = mapped.TextureCoordinates[a];
                    Point2f second = mapped.TextureCoordinates[b];
                    double uv = Math.Sqrt(
                        Math.Pow(first.X - second.X, 2) +
                        Math.Pow(first.Y - second.Y, 2));
                    if (world > 1e-9 && uv > 1e-9)
                        ratios.Add(world / uv);
                }
            }
            if (ratios.Count == 0)
                return 1.0;
            ratios.Sort();
            return ratios[ratios.Count / 2];
        }

        private static long VertexPairKey(int a, int b)
        {
            int min = Math.Min(a, b);
            int max = Math.Max(a, b);
            return ((long)min << 32) | (uint)max;
        }

        private void BuildReferenceMarkers(
            HashSet<long> seamSourceEdges,
            IList<List<int>> referenceRows)
        {
            if (_painterMesh == null)
                return;
            var red = Color.FromArgb(235, 55, 55);
            if (seamSourceEdges.Count > 0)
            {
                var atlasEdges = new HashSet<long>();
                foreach (MeshFace face in _painterMesh.Faces)
                {
                    int[] vertices = FaceVertices(face);
                    for (int edge = 0; edge < vertices.Length; edge++)
                    {
                        int a = vertices[edge];
                        int b = vertices[(edge + 1) % vertices.Length];
                        if (!seamSourceEdges.Contains(VertexPairKey(
                                _painterSourceIndices[a], _painterSourceIndices[b])) ||
                            !atlasEdges.Add(VertexPairKey(a, b)))
                            continue;
                        _atlasMarkers.Add(new WasperPaintMarker
                        {
                            Line = new Line(
                                _painterMesh.Vertices.Point3dAt(a),
                                _painterMesh.Vertices.Point3dAt(b)),
                            Color = red,
                            Thickness = 4
                        });
                    }
                }
                foreach (long key in seamSourceEdges)
                {
                    int a = (int)(key >> 32);
                    int b = (int)(key & uint.MaxValue);
                    _referenceMarkers.Add(new WasperPaintMarker
                    {
                        Line = new Line(
                            _sourceMesh.Vertices.Point3dAt(a) + _previewMove,
                            _sourceMesh.Vertices.Point3dAt(b) + _previewMove),
                        Color = red,
                        Thickness = 5
                    });
                }
            }

            if (referenceRows != null && referenceRows.Count > 0)
            {
                BuildTopologyOrientedReferences(referenceRows);
                if (referenceRows.Count > 1)
                    return;
            }

            BoundingBox bounds = _painterMesh.GetBoundingBox(true);
            if (!bounds.IsValid || bounds.Diagonal.X <= 1e-9)
                return;
            double[] sections = { 0.25, 0.5, 0.75 };
            Color[] colors =
            {
                Color.FromArgb(240, 190, 45),
                Color.FromArgb(245, 245, 245),
                Color.FromArgb(45, 205, 205)
            };
            for (int section = 0; section < sections.Length; section++)
            {
                double x = bounds.Min.X + bounds.Diagonal.X * sections[section];
                foreach (MeshFace face in _painterMesh.Faces)
                {
                    int[] vertices = FaceVertices(face);
                    var hits = new List<Tuple<Point3d, Point3d>>();
                    for (int edge = 0; edge < vertices.Length; edge++)
                    {
                        int a = vertices[edge];
                        int b = vertices[(edge + 1) % vertices.Length];
                        Point3d pa = _painterMesh.Vertices.Point3dAt(a);
                        Point3d pb = _painterMesh.Vertices.Point3dAt(b);
                        double denominator = pb.X - pa.X;
                        if (Math.Abs(denominator) <= 1e-12)
                            continue;
                        double t = (x - pa.X) / denominator;
                        if (t < 0.0 || t > 1.0)
                            continue;
                        Point3d atlas = pa + (pb - pa) * t;
                        Point3d sourceA = _sourceMesh.Vertices.Point3dAt(_painterSourceIndices[a]);
                        Point3d sourceB = _sourceMesh.Vertices.Point3dAt(_painterSourceIndices[b]);
                        Point3d source = sourceA + (sourceB - sourceA) * t + _previewMove;
                        if (!hits.Any(hit => hit.Item1.DistanceToSquared(atlas) < 1e-16))
                            hits.Add(Tuple.Create(atlas, source));
                    }
                    if (hits.Count != 2)
                        continue;
                    _atlasMarkers.Add(new WasperPaintMarker
                    {
                        Line = new Line(hits[0].Item1, hits[1].Item1),
                        Color = colors[section],
                        Thickness = 3
                    });
                    _referenceMarkers.Add(new WasperPaintMarker
                    {
                        Line = new Line(hits[0].Item2, hits[1].Item2),
                        Color = colors[section],
                        Thickness = 4
                    });
                }
            }
        }

        private void BuildTopologyOrientedReferences(IList<List<int>> rows)
        {
            Color rowColor = Color.FromArgb(155, 35, 45, 65);
            foreach (List<int> row in rows)
            {
                for (int index = 1; index < row.Count; index++)
                {
                    int sourceA = row[index - 1];
                    int sourceB = row[index];
                    if (TryAtlasEdge(sourceA, sourceB, out Point3d atlasA, out Point3d atlasB))
                    {
                        _atlasMarkers.Add(new WasperPaintMarker
                        {
                            Line = new Line(atlasA, atlasB),
                            Color = rowColor,
                            Thickness = 1
                        });
                    }
                    _referenceMarkers.Add(new WasperPaintMarker
                    {
                        Line = new Line(
                            _sourceMesh.Vertices.Point3dAt(sourceA) + _previewMove,
                            _sourceMesh.Vertices.Point3dAt(sourceB) + _previewMove),
                        Color = rowColor,
                        Thickness = 2
                    });
                }
            }

            double[] sections = { 0.25, 0.5, 0.75 };
            Color[] colors =
            {
                Color.FromArgb(240, 190, 45),
                Color.FromArgb(245, 245, 245),
                Color.FromArgb(45, 205, 205)
            };
            for (int section = 0; section < sections.Length; section++)
            {
                for (int row = 1; row < rows.Count; row++)
                {
                    Point3d sourceA = SampleSourceRow(rows[row - 1], sections[section]);
                    Point3d sourceB = SampleSourceRow(rows[row], sections[section]);
                    _referenceMarkers.Add(new WasperPaintMarker
                    {
                        Line = new Line(sourceA + _previewMove, sourceB + _previewMove),
                        Color = colors[section],
                        Thickness = 4
                    });
                    if (TrySampleAtlasRow(rows[row - 1], sections[section], out Point3d atlasA) &&
                        TrySampleAtlasRow(rows[row], sections[section], out Point3d atlasB))
                    {
                        _atlasMarkers.Add(new WasperPaintMarker
                        {
                            Line = new Line(atlasA, atlasB),
                            Color = colors[section],
                            Thickness = 3
                        });
                    }
                }
            }
        }

        private bool TrySampleAtlasRow(
            IList<int> row,
            double fraction,
            out Point3d point)
        {
            point = Point3d.Unset;
            if (row == null || row.Count < 2)
                return false;
            var lengths = new double[row.Count];
            for (int index = 1; index < row.Count; index++)
            {
                lengths[index] = lengths[index - 1] +
                    _sourceMesh.Vertices.Point3dAt(row[index - 1]).DistanceTo(
                        _sourceMesh.Vertices.Point3dAt(row[index]));
            }
            double target = lengths[lengths.Length - 1] * fraction;
            for (int index = 1; index < row.Count; index++)
            {
                if (lengths[index] < target)
                    continue;
                double segment = lengths[index] - lengths[index - 1];
                double t = segment <= 1e-12
                    ? 0.0
                    : (target - lengths[index - 1]) / segment;
                if (!TryAtlasEdge(row[index - 1], row[index], out Point3d a, out Point3d b))
                    return false;
                point = a + (b - a) * t;
                return true;
            }
            return false;
        }

        private bool TryAtlasEdge(
            int sourceA,
            int sourceB,
            out Point3d atlasA,
            out Point3d atlasB)
        {
            atlasA = Point3d.Unset;
            atlasB = Point3d.Unset;
            foreach (MeshFace face in _painterMesh.Faces)
            {
                int[] vertices = FaceVertices(face);
                for (int edge = 0; edge < vertices.Length; edge++)
                {
                    int a = vertices[edge];
                    int b = vertices[(edge + 1) % vertices.Length];
                    int mappedA = _painterSourceIndices[a];
                    int mappedB = _painterSourceIndices[b];
                    if (mappedA == sourceA && mappedB == sourceB)
                    {
                        atlasA = _painterMesh.Vertices.Point3dAt(a);
                        atlasB = _painterMesh.Vertices.Point3dAt(b);
                        return true;
                    }
                    if (mappedA == sourceB && mappedB == sourceA)
                    {
                        atlasA = _painterMesh.Vertices.Point3dAt(b);
                        atlasB = _painterMesh.Vertices.Point3dAt(a);
                        return true;
                    }
                }
            }
            return false;
        }

        private static int[] FaceVertices(MeshFace face)
        {
            return face.IsQuad
                ? new[] { face.A, face.B, face.C, face.D }
                : new[] { face.A, face.B, face.C };
        }

        private void RebuildDisplayMeshes()
        {
            if (_sourceMesh == null)
                return;
            _previewMesh = DisplacedMesh(_values, _previewMove);
            if (_outputMesh == null || _meshUpdateRequested)
            {
                _outputMesh = DisplacedMesh(_appliedValues, Vector3d.Zero);
                _meshUpdateRequested = false;
            }
            UpdatePainterColors();
            _painterVisualRevision++;
        }

        private Mesh DisplacedMesh(IList<double> values, Vector3d move)
        {
            Mesh result = _sourceMesh.DuplicateMesh();
            EnsureNormals(result);
            for (int index = 0; index < result.Vertices.Count; index++)
            {
                Vector3d normal = index < _sourceMesh.Normals.Count
                    ? (Vector3d)_sourceMesh.Normals[index]
                    : Vector3d.ZAxis;
                if (!normal.Unitize())
                    normal = Vector3d.ZAxis;
                double displacement = index < values.Count ? values[index] : 0.0;
                Point3d point = _sourceMesh.Vertices.Point3dAt(index) +
                                normal * displacement + move;
                result.Vertices.SetVertex(index, point);
            }
            result.Normals.ComputeNormals();
            UpdatePaintColors(result, values);
            return result;
        }

        private void UpdatePaintColors(Mesh mesh, IList<double> values)
        {
            if (mesh == null || values == null)
                return;
            mesh.VertexColors.Clear();
            for (int index = 0; index < mesh.Vertices.Count; index++)
            {
                double value = index < values.Count ? values[index] : 0.0;
                mesh.VertexColors.Add(Ge17DisplayColor(value));
            }
        }

        private void UpdatePainterColors()
        {
            if (_painterMesh == null)
                return;
            _painterMesh.VertexColors.Clear();
            for (int index = 0; index < _painterMesh.Vertices.Count; index++)
            {
                int source = index < _painterSourceIndices.Count
                    ? _painterSourceIndices[index]
                    : index;
                double value = source >= 0 && source < _values.Length
                    ? _values[source]
                    : 0.0;
                _painterMesh.VertexColors.Add(Ge17DisplayColor(value));
            }
        }

        private Color Ge17DisplayColor(double value)
        {
            double limit = value < 0.0
                ? Math.Abs(Math.Min(_domain.T0, _domain.T1))
                : Math.Max(_domain.T0, _domain.T1);
            double normalized = limit > 1e-12
                ? Math.Min(1.0, Math.Abs(value) / limit)
                : 0.0;
            double visible = Math.Sqrt(normalized);
            Color target = value < 0.0
                ? WasperPaintColors.Pushed
                : WasperPaintColors.Pulled;
            Color neutral = WasperPaintColors.Neutral;
            return Color.FromArgb(
                255,
                (int)Math.Round(neutral.R + (target.R - neutral.R) * visible),
                (int)Math.Round(neutral.G + (target.G - neutral.G) * visible),
                (int)Math.Round(neutral.B + (target.B - neutral.B) * visible));
        }

        internal bool PainterBeginStroke(Point3d atlasPoint)
        {
            if (_tool == WasperPaintTool.None || !TryAtlasPick(atlasPoint, out int index, out Point3d point))
                return false;
            _strokeBefore = CaptureSnapshot();
            _strokeActive = true;
            _strokeChanged = false;
            _lastAtlasSample = Point3d.Unset;
            _strokeVisualDirty = false;
            _lastStrokeVisualUpdateMs = 0;
            SetPreviewBrush(index, true);
            ApplyBrush(index, point);
            return true;
        }

        internal void PainterContinueStroke(Point3d atlasPoint)
        {
            if (!_strokeActive)
                return;
            if (!TryAtlasPick(atlasPoint, out int index, out Point3d point))
            {
                _lastAtlasSample = Point3d.Unset;
                ClearPainterHover();
                return;
            }
            SetPreviewBrush(index, false);
            if (!_lastAtlasSample.IsValid ||
                point.DistanceToSquared(_lastAtlasSample) >=
                Math.Pow(Math.Max(_radius * 0.1, 1e-6), 2))
                ApplyBrush(index, point);
        }

        internal void PainterEndStroke()
        {
            if (!_strokeActive)
                return;
            _strokeActive = false;
            if (_strokeChanged)
            {
                PushUndo(_strokeBefore);
                if (_live)
                {
                    ApplyWorkingValues();
                    ScheduleSolution();
                }
            }
            _strokeBefore = null;
            FlushStrokeVisuals(true);
        }

        private void ApplyBrush(int hitIndex, Point3d point)
        {
            if (hitIndex < 0 || hitIndex >= _values.Length || !_eligible[hitIndex])
                return;
            Point3d start = _lastAtlasSample.IsValid ? _lastAtlasSample : point;
            double[] smoothSource = _tool == WasperPaintTool.Smooth
                ? (double[])_values.Clone()
                : null;
            var influenceBySource = new double[_values.Length];
            for (int atlasIndex = 0; atlasIndex < _painterMesh.Vertices.Count; atlasIndex++)
            {
                int source = _painterSourceIndices[atlasIndex];
                if (source < 0 || source >= _values.Length || !_eligible[source])
                    continue;
                Point3d sample = _painterMesh.Vertices.Point3dAt(atlasIndex);
                double distance = DistanceToSegment(sample, start, point);
                if (distance > _radius)
                    continue;
                double influence = WasperPaintBrushKernel.Influence(
                    distance,
                    _radius,
                    _falloff);
                influenceBySource[source] = Math.Max(influenceBySource[source], influence);
            }
            bool changed = false;
            for (int index = 0; index < _values.Length; index++)
            {
                double influence = influenceBySource[index];
                if (influence <= 0.0)
                    continue;
                double oldValue = _values[index];
                double newValue = oldValue;
                if (_tool == WasperPaintTool.Smooth)
                {
                    double sum = 0.0;
                    int[] neighbors = _sourceMesh.Vertices.GetConnectedVertices(index);
                    foreach (int neighbor in neighbors.Append(index))
                    {
                        if (neighbor < 0 || neighbor >= smoothSource.Length)
                            continue;
                        sum += smoothSource[neighbor];
                    }
                    int count = neighbors.Length + 1;
                    if (count > 0)
                    {
                        newValue = WasperPaintBrushKernel.SmoothValue(
                            oldValue,
                            sum / count,
                            _smoothStrength * influence,
                            _domain);
                    }
                }
                else
                {
                    newValue = WasperPaintBrushKernel.DirectionalValue(
                        oldValue,
                        _tool,
                        _domain,
                        _strength * influence);
                }
                if (Math.Abs(newValue - oldValue) <= 1e-12)
                    continue;
                _values[index] = newValue;
                changed = true;
            }
            _lastAtlasSample = point;
            if (!changed)
                return;
            _strokeChanged = true;
            _strokeVisualDirty = true;
            FlushStrokeVisuals(false);
        }

        private void FlushStrokeVisuals(bool force)
        {
            if (!_strokeVisualDirty)
                return;
            long now = Environment.TickCount64;
            if (!force && now - _lastStrokeVisualUpdateMs < 25)
                return;
            _strokeVisualDirty = false;
            _lastStrokeVisualUpdateMs = now;
            RebuildDisplayMeshes();
            UpdateConduit();
            _paintForm?.PresentCanvasFrame();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        private bool TryAtlasPick(Point3d point, out int index, out Point3d snapped)
        {
            index = -1;
            snapped = Point3d.Unset;
            if (_painterMesh == null)
                return false;
            MeshPoint hit = _painterMesh.ClosestMeshPoint(
                point,
                Math.Max(_radius, 1e-6));
            if (hit == null)
                return false;
            snapped = hit.Point;
            MeshFace face = _painterMesh.Faces[hit.FaceIndex];
            int[] vertices = face.IsQuad
                ? new[] { face.A, face.B, face.C, face.D }
                : new[] { face.A, face.B, face.C };
            double closest = double.PositiveInfinity;
            foreach (int vertex in vertices)
            {
                double distance = _painterMesh.Vertices.Point3dAt(vertex)
                    .DistanceToSquared(snapped);
                if (distance >= closest)
                    continue;
                closest = distance;
                index = vertex < _painterSourceIndices.Count
                    ? _painterSourceIndices[vertex]
                    : vertex;
            }
            return index >= 0;
        }

        private static double DistanceToSegment(Point3d point, Point3d start, Point3d end)
        {
            Vector3d segment = end - start;
            double lengthSquared = segment.SquareLength;
            if (lengthSquared <= 1e-18)
                return point.DistanceTo(start);
            double t = Math.Max(0.0, Math.Min(1.0, ((point - start) * segment) / lengthSquared));
            return point.DistanceTo(start + segment * t);
        }

        internal void PainterHover(Point3d atlasPoint)
        {
            if (_tool == WasperPaintTool.None ||
                _tool == WasperPaintTool.Smooth ||
                !TryAtlasPick(atlasPoint, out int index, out _))
            {
                ClearPainterHover();
                return;
            }
            SetPreviewBrush(index, true);
        }

        private void SetPreviewBrush(int sourceIndex, bool forceRedraw)
        {
            _hasHover = sourceIndex >= 0 && sourceIndex < _values.Length;
            _hoverIndex = _hasHover ? sourceIndex : -1;
            long now = Environment.TickCount64;
            if (!forceRedraw && now - _lastPreviewBrushRedrawMs < 25)
                return;
            _lastPreviewBrushRedrawMs = now;
            UpdateConduit();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal void ClearPainterHover()
        {
            if (!_hasHover)
                return;
            _hasHover = false;
            _hoverIndex = -1;
            UpdateConduit();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal void ClearPaint()
        {
            if (_values.All(value => Math.Abs(value) <= 1e-12))
                return;
            PushUndo(CaptureSnapshot());
            Array.Clear(_values, 0, _values.Length);
            if (_live)
                ApplyWorkingValues();
            RebuildDisplayMeshes();
            UpdateConduit();
            ScheduleSolution();
        }

        internal void UndoPaint()
        {
            if (_undo.Count == 0)
                return;
            _redo.Push(CaptureSnapshot());
            RestoreSnapshot(_undo.Pop());
        }

        internal void RedoPaint()
        {
            if (_redo.Count == 0)
                return;
            _undo.Push(CaptureSnapshot());
            RestoreSnapshot(_redo.Pop());
        }

        private void PushUndo(Ge17v2PainterSnapshot snapshot)
        {
            if (snapshot == null)
                return;
            _undo.Push(snapshot);
            _redo.Clear();
        }

        private Ge17v2PainterSnapshot CaptureSnapshot()
        {
            return new Ge17v2PainterSnapshot
            {
                Values = (double[])_values.Clone(),
                AppliedValues = (double[])_appliedValues.Clone(),
                ActiveTextureLayer = _activeTextureLayer,
                AtlasFlipMap = _atlasFlipMap,
                AtlasQuarterTurns = _atlasQuarterTurns,
                TextureLayers = _textureLayers.Select(layer =>
                    new Ge17v2TextureSnapshot
                    {
                        SourceKey = layer.SourceKey,
                        Initialized = layer.Placement.Initialized,
                        Corners = (Point2d[])layer.Placement.Corners.Clone(),
                        Visible = layer.Visible,
                        EditMode = layer.EditMode,
                        DistortMode = layer.DistortMode,
                        RotateMode = layer.RotateMode,
                        Opacity = layer.Opacity
                    }).ToArray()
            };
        }

        private void RestoreSnapshot(Ge17v2PainterSnapshot snapshot)
        {
            if (snapshot?.Values?.Length != _values.Length)
                return;
            _values = (double[])snapshot.Values.Clone();
            _appliedValues = snapshot.AppliedValues?.Length == _values.Length
                ? (double[])snapshot.AppliedValues.Clone()
                : (double[])_values.Clone();
            _activeTextureLayer = Math.Max(
                0,
                Math.Min(_textureLayers.Count - 1, snapshot.ActiveTextureLayer));
            _atlasFlipMap = snapshot.AtlasFlipMap;
            _atlasQuarterTurns = snapshot.AtlasQuarterTurns;
            for (int index = 0;
                 index < Math.Min(_textureLayers.Count, snapshot.TextureLayers?.Length ?? 0);
                 index++)
            {
                WasperPaintTextureLayer layer = _textureLayers[index];
                Ge17v2TextureSnapshot saved = snapshot.TextureLayers[index];
                if (!string.Equals(layer.SourceKey, saved.SourceKey, StringComparison.Ordinal))
                    continue;
                layer.Placement.Initialized = saved.Initialized;
                if (saved.Corners?.Length == 4)
                    Array.Copy(saved.Corners, layer.Placement.Corners, 4);
                layer.Placement.UpdateBoundsFromCorners();
                layer.Placement.EndTransform();
                layer.Visible = saved.Visible;
                layer.EditMode = saved.EditMode;
                layer.DistortMode = saved.DistortMode;
                layer.RotateMode = saved.RotateMode;
                layer.Opacity = saved.Opacity;
                layer.Revision++;
            }
            RebuildDisplayMeshes();
            UpdateConduit();
            ScheduleSolution();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        private void ApplyWorkingValues()
        {
            _appliedValues = (double[])_values.Clone();
        }

        private void UpdateConduit()
        {
            if (_conduit == null)
                _conduit = new WasperPaintConduit();
            _conduit.IsActiveDocument = () =>
                ReferenceEquals(OnPingDocument(), Instances.ActiveCanvas?.Document);
            _conduit.PreviewMesh = _previewMesh;
            // The flat unwrapped atlas is not drawn in the Rhino viewport: painting happens
            // on the painter form's own 2D canvas, so the atlas markers only added a second
            // floating copy of the model beside the 3D preview. _atlasMarkers is still built
            // and still feeds that canvas and TryAtlasPick. Matches wsp_Pp14.
            _conduit.AtlasMarkers = null;
            _conduit.ReferenceMarkers = _referenceMarkers;
            _conduit.ShowField = _preview;
            _conduit.ShowReferences = _referenceMarkers.Count > 0;
            _conduit.HasHit = _hasHover &&
                _tool != WasperPaintTool.None &&
                _tool != WasperPaintTool.Smooth &&
                              _hoverIndex >= 0 && _hoverIndex < _sourceMesh?.Vertices.Count;
            if (_conduit.HasHit)
            {
                _conduit.HitPoint = _previewMesh.Vertices.Point3dAt(_hoverIndex);
                _conduit.HitNormal = _previewMesh.Normals.Count > _hoverIndex
                    ? (Vector3d)_previewMesh.Normals[_hoverIndex]
                    : Vector3d.ZAxis;
            }
            _conduit.Radius = _radius;
            _conduit.Tool = _tool;
            _conduit.Enabled = _preview || _tool != WasperPaintTool.None;
            _paintForm?.RefreshCanvas();
        }

        private void StopPainting()
        {
            _tool = WasperPaintTool.None;
            _strokeActive = false;
            _strokeBefore = null;
            if (_conduit != null)
                _conduit.Enabled = _preview;
        }

        private void ScheduleSolution()
        {
            GH_Document document = OnPingDocument();
            if (document == null)
                return;
            document.ScheduleSolution(1, _ => ExpireSolution(false));
        }
    }

    internal sealed class Ge17v2PainterSnapshot
    {
        internal double[] Values;
        internal double[] AppliedValues;
        internal int ActiveTextureLayer;
        internal bool AtlasFlipMap;
        internal int AtlasQuarterTurns;
        internal Ge17v2TextureSnapshot[] TextureLayers;
    }

    internal sealed class Ge17v2TextureSnapshot
    {
        internal string SourceKey;
        internal bool Initialized;
        internal Point2d[] Corners;
        internal bool Visible;
        internal bool EditMode;
        internal bool DistortMode;
        internal bool RotateMode;
        internal double Opacity;
    }
}
