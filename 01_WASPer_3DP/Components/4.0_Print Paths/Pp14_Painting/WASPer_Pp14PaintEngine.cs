using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

using Newtonsoft.Json;

using Rhino;
using Rhino.Display;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
using Rhino.UI;

using WASPer_3DP;

namespace WASPer_3DP_Components._4_0_Print_Paths
{
    public sealed partial class wsp_Pp14_Fuzzy_Skin_from_Paint
    {
        internal void ToggleTool(WasperPaintTool tool)
        {
            _tool = _tool == tool ? WasperPaintTool.None : tool;
            if (_tool == WasperPaintTool.None)
                StopPainting();
            else
                StartPainting();
            UpdateConduit();
            Instances.ActiveCanvas?.Invalidate();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal void SetSmoothRegionShape(WasperSmoothRegionShape shape)
        {
            _smoothRegionShape = shape;
            if (_tool != WasperPaintTool.Smooth)
                ToggleTool(WasperPaintTool.Smooth);
            _paintForm?.RefreshCanvas();
        }

        internal void ApplySmoothRegion(IList<Point3d> boundary)
        {
            if (boundary == null || boundary.Count < 3 || _atlasPoints.Count == 0)
                return;
            var selected = new HashSet<int>(
                _atlasPoints
                    .Where(pair => pair.Key >= 0 && pair.Key < _locations.Count &&
                        _locations[pair.Key].Eligible &&
                        WasperPaintRegion.Contains(pair.Value, boundary, _previewPlane))
                    .Select(pair => pair.Key));
            if (selected.Count == 0)
                return;
            RecordUndoEvent("Smooth paint region");
            double[] before = (double[])_values.Clone();
            double[] sourceValues = (double[])_values.Clone();
            bool changed = false;
            foreach (int index in selected)
            {
                PaintLocation location = _locations[index];
                var neighbors = selected.Where(candidate =>
                        candidate != index &&
                        _locations[candidate].Stack == location.Stack &&
                        ((_locations[candidate].Path.Equals(location.Path) &&
                          Math.Abs(_locations[candidate].Item - location.Item) == 1) ||
                         Math.Abs(_locations[candidate].Layer - location.Layer) == 1))
                    .OrderBy(candidate => _atlasPoints[candidate]
                        .DistanceToSquared(_atlasPoints[index]))
                    .Take(4)
                    .ToList();
                double sum = sourceValues[index] +
                    neighbors.Sum(neighbor => sourceValues[neighbor]);
                double average = sum / (neighbors.Count + 1);
                double value = WasperPaintBrushKernel.SmoothValue(
                    sourceValues[index], average, _smoothStrength, _domain);
                if (Math.Abs(value - _values[index]) <= 1e-12)
                    continue;
                _values[index] = value;
                changed = true;
            }
            if (!changed)
                return;
            PushPaintUndo(before);
            _painterVisualRevision++;
            PaintStateChanged();
            UpdateConduit();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal void TogglePreview()
        {
            RecordUndoEvent("Toggle paint preview");
            _preview = !_preview;
            UpdateConduit();
            Instances.ActiveCanvas?.Invalidate();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal void ToggleLive()
        {
            RecordUndoEvent("Toggle live paint updates");
            _live = !_live;
            if (_live)
            {
                ApplyPreviewState();
                ScheduleSolution();
            }
            Instances.ActiveCanvas?.Invalidate();
            _paintForm?.RefreshCanvas();
        }

        internal void UpdateAlgorithm()
        {
            if (_live)
                return;
            RecordUndoEvent("Apply painted state");
            ApplyPreviewState();
            ScheduleSolution();
            Instances.ActiveCanvas?.Invalidate();
            _paintForm?.RefreshCanvas();
        }

        internal void UndoPaint()
        {
            if (_painterUndo.Count == 0)
                return;
            RecordUndoEvent("Undo painter change");
            _painterRedo.Push(CapturePainterUndoState());
            RestorePainterUndoState(_painterUndo.Pop());
        }

        internal void RedoPaint()
        {
            if (_painterRedo.Count == 0)
                return;
            RecordUndoEvent("Redo painter change");
            _painterUndo.Push(CapturePainterUndoState());
            RestorePainterUndoState(_painterRedo.Pop());
        }

        internal void ClearPaint()
        {
            if (_values.Length == 0 || _values.All(value => Math.Abs(value) <= 1e-12))
                return;
            RecordUndoEvent("Clear paint field");
            PushPaintUndo((double[])_values.Clone());
            Array.Clear(_values, 0, _values.Length);
            _painterVisualRevision++;
            PaintStateChanged();
            UpdateConduit();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal void UpdateShellMeshOutput()
        {
            RecordUndoEvent("Update Shell mesh output");
            _shellMeshUpdateRequested = true;
            ExpireSolution(true);
        }

        private bool PainterInputEditable(int index)
        {
            return index >= 0 &&
                   index < Params.Input.Count &&
                   Params.Input[index].SourceCount == 0 &&
                   Params.Input[index] is Param_Number;
        }

        internal void PreviewPainterSettings(
            double radius,
            double brushStrength,
            double smoothStrength)
        {
            if (PainterRadiusEditable &&
                double.IsFinite(radius) &&
                radius > RhinoMath.ZeroTolerance)
            {
                _radius = radius;
                _uiRadiusOverride = _radius;
            }
            if (PainterBrushStrengthEditable && double.IsFinite(brushStrength))
            {
                _strength = Math.Max(0.0, Math.Min(1.0, brushStrength));
                _uiStrengthOverride = _strength;
            }
            if (PainterSmoothStrengthEditable && double.IsFinite(smoothStrength))
            {
                _smoothStrength = Math.Max(0.0, Math.Min(1.0, smoothStrength));
                _uiSmoothOverride = _smoothStrength;
            }
            UpdateHoverConduit();
            RequestHoverRedraw();
        }

        internal void CommitPainterSettings(
            double radius,
            double brushStrength,
            double smoothStrength)
        {
            RecordUndoEvent("Change Painter brush settings");
            PreviewPainterSettings(radius, brushStrength, smoothStrength);
            SetPersistentNumber(1, radius);
            SetPersistentNumber(2, brushStrength);
            SetPersistentNumber(3, smoothStrength);
            // These values affect only the interactive painter. Keep the
            // unconnected input defaults in sync without recomputing Pp13.
        }

        private void SetPersistentNumber(int index, double value)
        {
            if (!PainterInputEditable(index) ||
                !double.IsFinite(value) ||
                !(Params.Input[index] is Param_Number parameter))
                return;
            parameter.PersistentData.Clear();
            parameter.PersistentData.Append(new GH_Number(value));
        }

        private void CaptureShellMeshFromPaint(
            IDictionary<string, ShellDirection> directionByPath,
            double tolerance)
        {
            Mesh topology = null;
            IList<int> sourceIndices = null;
            if (_previewMesh != null &&
                _previewMesh.IsValid &&
                _previewMesh.Vertices.Count == _previewSourceIndices.Count)
            {
                topology = _previewMesh;
                sourceIndices = _previewSourceIndices;
            }
            else if (_painterMesh != null &&
                     _painterMesh.IsValid &&
                     _painterMesh.Vertices.Count == _painterSourceIndices.Count)
            {
                topology = _painterMesh;
                sourceIndices = _painterSourceIndices;
            }

            Mesh captured;
            if (topology != null)
            {
                captured = topology.DuplicateMesh();
                for (int vertex = 0; vertex < captured.Vertices.Count; vertex++)
                {
                    int sourceIndex = sourceIndices[vertex];
                    captured.Vertices.SetVertex(
                        vertex,
                        MovedPaintPoint(sourceIndex, directionByPath, tolerance));
                }
            }
            else
            {
                captured = BuildShellMeshFromElevationRows(
                    directionByPath,
                    tolerance);
            }
            if (captured == null || captured.Faces.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "A Shell mesh could not be formed. Update Mesh found fewer than two " +
                    "physical elevations containing at least three eligible Shell points.");
                return;
            }

            captured.Vertices.CombineIdentical(true, true);
            captured.Faces.CullDegenerateFaces();
            captured.Weld(Math.PI);
            captured.Normals.ComputeNormals();
            captured.Compact();
            _outputShellMesh = captured;
        }

        private Mesh BuildShellMeshFromElevationRows(
            IDictionary<string, ShellDirection> directionByPath,
            double tolerance)
        {
            List<int> eligible = _locations
                .Where(location => location.RoleEligible)
                .Select(location => location.Linear)
                .OrderBy(index => _locations[index].Point.Z)
                .ToList();
            if (eligible.Count < 6)
                return null;

            double elevationTolerance = Math.Max(tolerance * 10.0, 1e-7);
            var levels = new List<List<int>>();
            foreach (int index in eligible)
            {
                double z = _locations[index].Point.Z;
                if (levels.Count == 0 ||
                    Math.Abs(
                        z -
                        levels[levels.Count - 1]
                            .Average(item => _locations[item].Point.Z)) >
                    elevationTolerance)
                {
                    levels.Add(new List<int>());
                }
                levels[levels.Count - 1].Add(index);
            }

            levels = levels
                .Select(level => UniquePointIndices(level, tolerance))
                .Where(level => level.Count >= 3)
                .ToList();
            if (levels.Count < 2)
                return null;

            foreach (List<int> level in levels)
            {
                Point3d center = new Point3d(
                    level.Average(index => _locations[index].Point.X),
                    level.Average(index => _locations[index].Point.Y),
                    level.Average(index => _locations[index].Point.Z));
                level.Sort((first, second) =>
                    Math.Atan2(
                            _locations[first].Point.Y - center.Y,
                            _locations[first].Point.X - center.X)
                        .CompareTo(Math.Atan2(
                            _locations[second].Point.Y - center.Y,
                            _locations[second].Point.X - center.X)));
            }

            int samples = Math.Min(
                256,
                Math.Max(3, levels.Max(level => level.Count)));
            var mesh = new Mesh();
            var starts = new int[levels.Count];
            for (int levelIndex = 0; levelIndex < levels.Count; levelIndex++)
            {
                List<int> level = levels[levelIndex];
                starts[levelIndex] = mesh.Vertices.Count;
                for (int column = 0; column <= samples; column++)
                {
                    double u = column / (double)samples;
                    int item = column == samples
                        ? 0
                        : Math.Min(
                            level.Count - 1,
                            (int)Math.Floor(u * level.Count));
                    int sourceIndex = level[item];
                    mesh.Vertices.Add(
                        MovedPaintPoint(sourceIndex, directionByPath, tolerance));
                    mesh.VertexColors.Add(PaintColorAt(sourceIndex));
                }
            }
            for (int levelIndex = 1; levelIndex < levels.Count; levelIndex++)
            {
                for (int column = 0; column < samples; column++)
                {
                    mesh.Faces.AddFace(
                        starts[levelIndex - 1] + column,
                        starts[levelIndex - 1] + column + 1,
                        starts[levelIndex] + column + 1,
                        starts[levelIndex] + column);
                }
            }
            return mesh;
        }

        private List<int> UniquePointIndices(
            IEnumerable<int> indices,
            double tolerance)
        {
            var unique = new List<int>();
            double squared = tolerance * tolerance;
            foreach (int index in indices)
            {
                Point3d point = _locations[index].Point;
                if (unique.Any(existing =>
                        _locations[existing].Point.DistanceToSquared(point) <= squared))
                    continue;
                unique.Add(index);
            }
            return unique;
        }

        private Point3d MovedPaintPoint(
            int sourceIndex,
            IDictionary<string, ShellDirection> directionByPath,
            double tolerance)
        {
            sourceIndex = CanonicalClosureSourceIndex(sourceIndex, tolerance);
            if (sourceIndex < 0 || sourceIndex >= _locations.Count)
                return Point3d.Unset;
            PaintLocation location = _locations[sourceIndex];
            Point3d point = location.Point;
            double displacement = sourceIndex < _values.Length
                ? _values[sourceIndex]
                : 0.0;
            if (Math.Abs(displacement) <= tolerance)
                return point;
            IList<Plane> branch = _source?.PtPlanes?.Branch(location.Path);
            if (branch == null || branch.Count < 2)
                return point;
            int logicalCount = branch.Count -
                (IsDuplicateClosure(branch, tolerance) ? 1 : 0);
            Vector3d tangent = LocalTangent(
                branch,
                location.Item,
                logicalCount);
            Vector3d inward;
            if (directionByPath != null &&
                directionByPath.TryGetValue(
                    location.Path.ToString(),
                    out ShellDirection direction))
            {
                inward = direction.InwardFromTangent(
                    point,
                    tangent,
                    tolerance);
            }
            else
            {
                inward = Vector3d.CrossProduct(
                    location.Plane.ZAxis,
                    tangent);
            }
            return inward.Unitize()
                ? point - inward * displacement
                : point;
        }

        private Color PaintColorAt(int sourceIndex)
        {
            sourceIndex = CanonicalClosureSourceIndex(sourceIndex, 1e-9);
            double value = sourceIndex >= 0 && sourceIndex < _values.Length
                ? _values[sourceIndex]
                : 0.0;
            return WasperPaintColors.ForValue(value, _domain);
        }

        private int CanonicalClosureSourceIndex(
            int sourceIndex,
            double tolerance)
        {
            if (sourceIndex < 0 || sourceIndex >= _locations.Count)
                return sourceIndex;
            PaintLocation location = _locations[sourceIndex];
            IList<Plane> branch = _source?.PtPlanes?.Branch(location.Path);
            if (branch == null ||
                location.Item != branch.Count - 1 ||
                !IsDuplicateClosure(branch, tolerance))
                return sourceIndex;
            int first = sourceIndex - location.Item;
            return first >= 0 && first < _locations.Count
                ? first
                : sourceIndex;
        }

        internal void TogglePainterForm()
        {
            if (_paintForm == null || _paintForm.IsClosed)
                _paintForm = new WasperEtoPaintForm(new WasperPp13PainterHost(this));
            _paintForm.ShowNearCursor();
        }

        internal bool PainterBeginStroke(Point3d atlasPoint)
        {
            if (_tool == WasperPaintTool.None ||
                !TryAtlasPick(atlasPoint, out int index, out Point3d point))
                return false;
            SetPainterHover(index);
            RecordUndoEvent($"{_tool} atlas paint stroke");
            _strokeBefore = (double[])_values.Clone();
            _strokeChanged = false;
            _strokeActive = true;
            ResetStrokeContinuity();
            _strokeVisualDirty = false;
            _lastStrokeVisualUpdateMs = 0;
            ApplyBrush(index, point, _previewPlane.ZAxis, true);
            return true;
        }

        internal void PainterContinueStroke(Point3d atlasPoint)
        {
            if (!_strokeActive)
                return;
            if (!TryAtlasPick(atlasPoint, out int index, out Point3d point))
            {
                SuspendStrokeContinuity();
                return;
            }
            if (_strokeSuspended ||
                !IsStrokeContinuation(index, point, _previewPlane.ZAxis))
                ResetStrokeContinuity();
            SetPainterHover(index);
            if (!_lastSample.IsValid ||
                point.DistanceToSquared(_lastSample) >=
                Math.Pow(Math.Max(_radius * 0.1, 1e-6), 2))
                ApplyBrush(index, point, _previewPlane.ZAxis, true);
        }

        internal void PainterEndStroke()
        {
            CommitStroke();
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
            SetPainterHover(index);
        }

        internal void ClearPainterHover()
        {
            if (!_hasHover)
                return;
            _hasHover = false;
            _hoverIndex = -1;
            _hoverPoint = Point3d.Unset;
            _hoverNormal = Vector3d.Unset;
            UpdateHoverConduit();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        private void SetPainterHover(int sourceIndex)
        {
            if (sourceIndex < 0 || sourceIndex >= _locations.Count)
            {
                ClearPainterHover();
                return;
            }

            PaintLocation location = _locations[sourceIndex];
            Point3d surfacePoint = _previewPoints.TryGetValue(
                sourceIndex,
                out Point3d previewPoint)
                ? previewPoint
                : location.Point + _previewMove;
            Vector3d surfaceNormal =
                (surfacePoint - _previewMove) - location.Point;
            if (!surfaceNormal.Unitize())
            {
                IList<Plane> branch = _source?.PtPlanes?.Branch(location.Path);
                int logicalCount = branch == null
                    ? 0
                    : branch.Count - (IsDuplicateClosure(
                        branch,
                        RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6) ? 1 : 0);
                Vector3d tangent = branch != null && logicalCount >= 2
                    ? LocalTangent(branch, location.Item, logicalCount)
                    : Vector3d.XAxis;
                surfaceNormal = Vector3d.CrossProduct(location.Plane.ZAxis, tangent);
                if (!surfaceNormal.Unitize())
                    surfaceNormal = location.Plane.YAxis;
            }

            if (sourceIndex < _values.Length)
                surfacePoint += surfaceNormal * _values[sourceIndex];

            double markerGap = Math.Max(
                RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance * 5.0 ?? 5e-6,
                _radius * 0.01);
            Point3d markerPoint = surfacePoint + surfaceNormal * markerGap;
            bool changed = !_hasHover ||
                           _hoverIndex != sourceIndex ||
                           !_hoverPoint.EpsilonEquals(markerPoint, markerGap * 0.1);
            _hasHover = true;
            _hoverIndex = sourceIndex;
            _hoverPoint = markerPoint;
            _hoverNormal = surfaceNormal;
            if (!changed)
                return;
            UpdateHoverConduit();
            RequestHoverRedraw();
        }

        private void UpdateHoverConduit()
        {
            if (_conduit == null)
                _conduit = new WasperPaintConduit();
            _conduit.IsActiveDocument = () =>
                ReferenceEquals(OnPingDocument(), Instances.ActiveCanvas?.Document);
            _conduit.HasHit = _hasHover &&
                _tool != WasperPaintTool.None &&
                _tool != WasperPaintTool.Smooth;
            _conduit.HitPoint = _hoverPoint;
            _conduit.HitNormal = _hoverNormal;
            _conduit.Radius = _radius;
            _conduit.Tool = _tool;
            _conduit.Enabled =
                _preview ||
                _tool != WasperPaintTool.None ||
                _referenceMarkers.Count > 0;
        }

        private void RequestHoverRedraw()
        {
            const long minimumFrameMilliseconds = 25;
            long now = Environment.TickCount64;
            if (now - _lastHoverRedrawMs < minimumFrameMilliseconds)
                return;
            _lastHoverRedrawMs = now;
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal void SetPainterTool(WasperPaintTool tool)
        {
            if (_tool != tool)
                ToggleTool(tool);
            _paintForm?.RefreshCanvas();
        }

        private bool TryAtlasPick(
            Point3d atlasPoint,
            out int index,
            out Point3d snapped)
        {
            index = -1;
            snapped = Point3d.Unset;
            if (_painterMesh == null || !_painterMesh.IsValid)
                return false;
            MeshPoint meshPoint = _painterMesh.ClosestMeshPoint(
                atlasPoint,
                Math.Max(_radius * 0.5, 1e-6));
            if (meshPoint == null)
                return false;
            snapped = meshPoint.Point;
            MeshFace face = _painterMesh.Faces[meshPoint.FaceIndex];
            int[] vertices = face.IsQuad
                ? new[] { face.A, face.B, face.C, face.D }
                : new[] { face.A, face.B, face.C };
            double closest = double.PositiveInfinity;
            foreach (int vertex in vertices)
            {
                if (vertex < 0 || vertex >= _painterSourceIndices.Count)
                    continue;
                int source = _painterSourceIndices[vertex];
                if (source < 0 || source >= _locations.Count ||
                    !_locations[source].Eligible)
                    continue;
                Point3d vertexPoint = _painterMesh.Vertices[vertex];
                double distance = vertexPoint.DistanceToSquared(snapped);
                if (distance >= closest)
                    continue;
                closest = distance;
                index = source;
            }
            return index >= 0;
        }

        internal void HandleMouseMove(MouseCallbackEventArgs e)
        {
            if (_tool == WasperPaintTool.None ||
                _tool == WasperPaintTool.Smooth ||
                e?.View == null)
                return;
            bool hit = TryPick(e, out int index, out Point3d point, out Vector3d normal);
            if (_strokeActive)
            {
                if ((Control.MouseButtons & MouseButtons.Left) == 0)
                {
                    CommitStroke();
                    return;
                }
                e.Cancel = true;
                if (_strokeSuspended ||
                    !hit ||
                    !IsStrokeContinuation(
                        index,
                        point,
                        normal))
                {
                    _strokeSuspended = true;
                    SetHover(false, -1, Point3d.Unset, Vector3d.Unset);
                    RhinoDoc.ActiveDoc?.Views.Redraw();
                    return;
                }
                SetHover(true, index, point, normal);
                if (!_lastSample.IsValid ||
                    point.DistanceToSquared(_lastSample) >=
                    Math.Pow(Math.Max(_radius * 0.15, 1e-6), 2))
                    ApplyBrush(
                        index,
                        point,
                        normal,
                        false);
            }
            else
            {
                SetHover(hit, index, point, normal);
            }
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal void HandleMouseDown(MouseCallbackEventArgs e)
        {
            if (_tool == WasperPaintTool.None ||
                _tool == WasperPaintTool.Smooth ||
                e == null || e.Button != MouseButtons.Left)
                return;
            if (!TryPick(e, out int index, out Point3d point, out Vector3d normal))
                return;
            RecordUndoEvent($"{_tool} paint stroke");
            _strokeBefore = (double[])_values.Clone();
            _strokeChanged = false;
            _strokeActive = true;
            ResetStrokeContinuity();
            _strokeVisualDirty = false;
            _lastStrokeVisualUpdateMs = 0;
            SetHover(true, index, point, normal);
            ApplyBrush(
                index,
                point,
                normal,
                false);
            e.Cancel = true;
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal void HandleMouseUp(MouseCallbackEventArgs e)
        {
            if (!_strokeActive || e == null || e.Button != MouseButtons.Left)
                return;
            e.Cancel = true;
            CommitStroke();
        }

        private void CommitStroke()
        {
            if (!_strokeActive)
                return;
            _strokeActive = false;
            ResetStrokeContinuity();
            if (_strokeChanged && _strokeBefore != null)
                PushPaintUndo(_strokeBefore);
            _strokeBefore = null;
            if (_strokeChanged)
                PaintStateChanged();
            if (!FlushStrokeVisuals())
                UpdateConduit();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        private void PushPaintUndo(double[] state)
        {
            PushPainterUndo(CapturePainterUndoState(state));
        }

        private void PaintStateChanged()
        {
            if (_live)
            {
                ApplyPreviewState();
                ScheduleSolution();
            }
            Instances.ActiveCanvas?.Invalidate();
        }

        private void ApplyPreviewState()
        {
            _paintValues.ApplyWorkingValues();
        }

        private void ApplyBrush(
            int hitIndex,
            Point3d surfacePoint,
            Vector3d surfaceNormal,
            bool atlasMode)
        {
            if (hitIndex < 0 || hitIndex >= _locations.Count)
                return;
            PaintLocation hit = _locations[hitIndex];
            if (!hit.Eligible)
                return;

            List<Point3d> sweep = BuildStrokeSweep(hitIndex, surfacePoint);
            IDictionary<int, Point3d> paintPoints = atlasMode
                ? _atlasPoints
                : _previewPoints;
            double[] smoothSource = _tool == WasperPaintTool.Smooth
                ? (double[])_values.Clone()
                : null;
            bool brushChanged = false;
            if (!_eligibleByStack.TryGetValue(
                    hit.Stack ?? string.Empty,
                    out List<int> candidates))
                return;

            foreach (int i in candidates)
            {
                PaintLocation location = _locations[i];
                if (!paintPoints.TryGetValue(i, out Point3d paintPoint))
                    continue;
                double distance = i == hitIndex
                    ? 0.0
                    : atlasMode
                        ? AtlasDistanceToSweep(location, paintPoint, sweep)
                        : DistanceToPolyline(paintPoint, sweep);
                if (distance > _radius)
                    continue;
                double influence = WasperPaintBrushKernel.Influence(
                    distance,
                    _radius,
                    _falloff);
                double amount = _strength * influence;
                if (amount <= 0.0)
                    continue;
                double oldValue = _values[i];
                double newValue;
                if (_tool == WasperPaintTool.Smooth)
                {
                    double weightedSum = 0.0;
                    double weightSum = 0.0;
                    foreach (int j in candidates)
                    {
                        if (!paintPoints.TryGetValue(j, out Point3d neighborPoint))
                            continue;
                        double neighborDistance = atlasMode
                                ? AtlasPointDistance(location, paintPoint, neighborPoint)
                                : paintPoint.DistanceTo(neighborPoint);
                        if (neighborDistance > _radius)
                            continue;
                        double weight = WasperPaintBrushKernel.Influence(
                            neighborDistance,
                            _radius,
                            _falloff);
                        if (j == i)
                            weight = Math.Max(weight, 1.0);
                        if (weight <= 0.0)
                            continue;
                        weightedSum += smoothSource[j] * weight;
                        weightSum += weight;
                    }
                    double smoothAmount = _smoothStrength * influence;
                    newValue = weightSum > 0.0
                        ? WasperPaintBrushKernel.SmoothValue(
                            oldValue,
                            weightedSum / weightSum,
                            smoothAmount,
                            _domain)
                        : oldValue;
                }
                else
                {
                    newValue = WasperPaintBrushKernel.DirectionalValue(
                        oldValue,
                        _tool,
                        _domain,
                        amount);
                }
                if (Math.Abs(newValue - oldValue) <= 1e-12)
                    continue;
                _values[i] = newValue;
                _strokeChanged = true;
                brushChanged = true;
            }
            _lastSample = surfacePoint;
            _lastHitIndex = hitIndex;
            _lastHitStack = hit.Stack ?? string.Empty;
            _lastStrokeNormal = surfaceNormal;
            _lastStrokeNormal.Unitize();
            if (brushChanged)
            {
                _strokeVisualDirty = true;
                RequestStrokeVisualUpdate();
            }
        }

        private double AtlasDistanceToSweep(
            PaintLocation location,
            Point3d atlasPoint,
            IList<Point3d> sweep)
        {
            double distance = DistanceToPolyline(atlasPoint, sweep);
            if (!_atlasRowWidths.TryGetValue(
                    location.Path.ToString(),
                    out double width) ||
                width <= RhinoMath.ZeroTolerance)
                return distance;
            Vector3d wrap = _previewPlane.XAxis * width;
            return Math.Min(
                distance,
                Math.Min(
                    DistanceToPolyline(atlasPoint - wrap, sweep),
                    DistanceToPolyline(atlasPoint + wrap, sweep)));
        }

        private double AtlasPointDistance(
            PaintLocation location,
            Point3d first,
            Point3d second)
        {
            double distance = first.DistanceTo(second);
            if (!_atlasRowWidths.TryGetValue(
                    location.Path.ToString(),
                    out double width) ||
                width <= RhinoMath.ZeroTolerance)
                return distance;
            Vector3d wrap = _previewPlane.XAxis * width;
            return Math.Min(
                distance,
                Math.Min(
                    (first - wrap).DistanceTo(second),
                    (first + wrap).DistanceTo(second)));
        }

        private bool IsStrokeContinuation(
            int hitIndex,
            Point3d surfacePoint,
            Vector3d surfaceNormal)
        {
            if (!_lastSample.IsValid || _lastHitIndex < 0)
                return true;
            if (hitIndex < 0 || hitIndex >= _locations.Count)
                return false;

            PaintLocation hit = _locations[hitIndex];
            if (!string.Equals(
                    _lastHitStack,
                    hit.Stack ?? string.Empty,
                    StringComparison.Ordinal))
                return false;

            double spatialJump = _lastSample.DistanceTo(surfacePoint);
            if (spatialJump > Math.Max(_radius * 4.0, 1e-6))
                return false;

            Vector3d nextNormal = surfaceNormal;
            if (_lastStrokeNormal.IsValid &&
                _lastStrokeNormal.SquareLength > 1e-18 &&
                nextNormal.Unitize() &&
                nextNormal * _lastStrokeNormal < 0.0)
                return false;

            return true;
        }

        private List<Point3d> BuildStrokeSweep(int hitIndex, Point3d surfacePoint)
        {
            PaintLocation hit = _locations[hitIndex];
            var sweep = new List<Point3d>();
            bool canBridge =
                _lastSample.IsValid &&
                _lastHitIndex >= 0 &&
                _lastHitIndex < _locations.Count &&
                string.Equals(_lastHitStack, hit.Stack ?? string.Empty, StringComparison.Ordinal) &&
                _lastSample.DistanceTo(surfacePoint) <= Math.Max(_radius * 4.0, 1e-6);

            if (!canBridge)
            {
                sweep.Add(surfacePoint);
                return sweep;
            }

            AddDistinctPoint(sweep, _lastSample);
            AddDistinctPoint(sweep, surfacePoint);
            return sweep;
        }

        private static void AddDistinctPoint(List<Point3d> points, Point3d point)
        {
            if (!point.IsValid)
                return;
            if (points.Count == 0 ||
                points[points.Count - 1].DistanceToSquared(point) > 1e-18)
                points.Add(point);
        }

        private static Point3d LerpPoint(Point3d from, Point3d to, double t)
        {
            return from + (to - from) * t;
        }

        private static double DistanceToPolyline(Point3d point, IList<Point3d> polyline)
        {
            if (polyline == null || polyline.Count == 0)
                return double.PositiveInfinity;
            if (polyline.Count == 1)
                return point.DistanceTo(polyline[0]);

            double closest = double.PositiveInfinity;
            for (int i = 1; i < polyline.Count; i++)
            {
                Point3d start = polyline[i - 1];
                Point3d end = polyline[i];
                Vector3d segment = end - start;
                double lengthSquared = segment.SquareLength;
                double t = lengthSquared <= 1e-18
                    ? 0.0
                    : Math.Max(0.0, Math.Min(1.0, ((point - start) * segment) / lengthSquared));
                double distance = point.DistanceTo(start + segment * t);
                if (distance < closest)
                    closest = distance;
            }
            return closest;
        }

        private void ResetStrokeContinuity()
        {
            _strokeSuspended = false;
            _lastSample = Point3d.Unset;
            _lastHitIndex = -1;
            _lastHitStack = string.Empty;
            _lastStrokeNormal = Vector3d.Unset;
        }

        private void SuspendStrokeContinuity()
        {
            _strokeSuspended = true;
            _lastSample = Point3d.Unset;
            _lastHitIndex = -1;
            _lastHitStack = string.Empty;
            _lastStrokeNormal = Vector3d.Unset;
        }

        private void RequestStrokeVisualUpdate()
        {
            const long minimumFrameMilliseconds = 25;
            long now = Environment.TickCount64;
            if (now - _lastStrokeVisualUpdateMs < minimumFrameMilliseconds)
                return;
            FlushStrokeVisuals();
        }

        private bool FlushStrokeVisuals()
        {
            if (!_strokeVisualDirty)
                return false;
            _strokeVisualDirty = false;
            _lastStrokeVisualUpdateMs = Environment.TickCount64;
            _painterVisualRevision++;
            UpdateConduit();
            _paintForm?.PresentCanvasFrame();
            return true;
        }

        private bool TryPick(
            MouseCallbackEventArgs e,
            out int index,
            out Point3d point,
            out Vector3d normal)
        {
            index = -1;
            point = Point3d.Unset;
            normal = Vector3d.Unset;
            if (e?.View?.ActiveViewport == null ||
                !e.View.ActiveViewport.GetFrustumLine(
                    e.ViewportPoint.X,
                    e.ViewportPoint.Y,
                    out Line rayLine))
                return false;
            Vector3d direction = rayLine.Direction;
            if (!direction.Unitize())
                return false;

            if (_previewMesh != null && _previewMesh.IsValid &&
                _previewMesh.Faces.Count > 0)
            {
                var ray = new Ray3d(rayLine.From, direction);
                double rayParameter = Intersection.MeshRay(
                    _previewMesh,
                    ray,
                    out int[] faceIndices);
                if (double.IsFinite(rayParameter) && rayParameter >= 0.0)
                {
                    point = ray.PointAt(rayParameter);
                    MeshPoint meshPoint = _previewMesh.ClosestMeshPoint(
                        point,
                        Math.Max(_radius, 1e-6));
                    if (meshPoint != null)
                    {
                        normal = _previewMesh.NormalAt(meshPoint);
                        normal.Unitize();
                        if (normal * direction > 0.0)
                            normal.Reverse();
                        MeshFace face = _previewMesh.Faces[meshPoint.FaceIndex];
                        int[] vertices = face.IsQuad
                            ? new[] { face.A, face.B, face.C, face.D }
                            : new[] { face.A, face.B, face.C };
                        double closest = double.PositiveInfinity;
                        foreach (int vertex in vertices)
                        {
                            if (vertex < 0 || vertex >= _previewSourceIndices.Count)
                                continue;
                            int sourceIndex = _previewSourceIndices[vertex];
                            if (sourceIndex < 0 || sourceIndex >= _locations.Count ||
                                !_locations[sourceIndex].Eligible)
                                continue;
                            Point3d previewVertex = _previewMesh.Vertices[vertex];
                            double distance = previewVertex.DistanceToSquared(point);
                            if (distance >= closest)
                                continue;
                            closest = distance;
                            index = sourceIndex;
                        }
                        if (index >= 0)
                            return true;
                    }
                }
            }

            double threshold = Math.Max(_radius * 0.35, 1e-6);
            double best = double.PositiveInfinity;
            double bestDepth = double.PositiveInfinity;
            for (int i = 0; i < _locations.Count; i++)
            {
                PaintLocation location = _locations[i];
                if (!location.Eligible)
                    continue;
                Point3d displayPoint = _previewPoints.TryGetValue(
                    location.Linear,
                    out Point3d mappedPoint)
                    ? mappedPoint
                    : _previewPlane.ClosestPoint(location.Point);
                Vector3d delta = displayPoint - rayLine.From;
                double depth = delta * direction;
                if (depth < 0.0)
                    continue;
                Point3d projected = rayLine.From + depth * direction;
                double distance = projected.DistanceTo(displayPoint);
                if (distance > threshold ||
                    distance > best + 1e-9 ||
                    (Math.Abs(distance - best) <= 1e-9 && depth >= bestDepth))
                    continue;
                best = distance;
                bestDepth = depth;
                index = i;
                point = displayPoint;
                IList<Plane> branch = _source?.PtPlanes?.Branch(location.Path);
                int logicalCount = branch == null
                    ? 0
                    : branch.Count - (IsDuplicateClosure(branch, 1e-9) ? 1 : 0);
                Vector3d tangent = LocalTangent(branch, location.Item, logicalCount);
                normal = Vector3d.CrossProduct(location.Plane.ZAxis, tangent);
                if (!normal.Unitize())
                    normal = location.Plane.YAxis;
            }
            if (index >= 0 && normal * direction > 0.0)
                normal.Reverse();
            return index >= 0;
        }

        private void SetHover(
            bool hit,
            int index,
            Point3d point,
            Vector3d normal)
        {
            _hasHover = hit;
            _hoverIndex = hit ? index : -1;
            _hoverNormal = hit ? normal : Vector3d.Unset;
            if (hit)
            {
                Vector3d displayNormal = normal;
                if (!displayNormal.Unitize())
                    displayNormal = Vector3d.ZAxis;
                double tolerance = Math.Max(
                    RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001,
                    1e-9);
                double displayOffset = Math.Max(
                    tolerance * 5.0,
                    _radius * 0.015);
                _hoverPoint = point + displayNormal * displayOffset;
            }
            else
            {
                _hoverPoint = Point3d.Unset;
            }
            UpdateConduit();
        }

        private void StartPainting()
        {
            WasperPaintSession.Activate(this, StopFromManager);
            if (_conduit == null)
                _conduit = new WasperPaintConduit();
            _conduit.Enabled = true;
            UpdateConduit();
        }

        internal void StopFromManager()
        {
            StopPainting();
            _tool = WasperPaintTool.None;
            Instances.ActiveCanvas?.Invalidate();
        }

        private void StopPainting()
        {
            if (_conduit != null)
                _conduit.Enabled = _preview;
            _strokeActive = false;
            _strokeBefore = null;
            WasperPaintSession.Release(this);
        }

        private void UpdateConduit()
        {
            if (_conduit == null)
                _conduit = new WasperPaintConduit();
            _conduit.IsActiveDocument = () =>
                ReferenceEquals(OnPingDocument(), Instances.ActiveCanvas?.Document);
            UpdatePreviewColors();
            _conduit.PreviewMesh = _previewMesh;
            _conduit.AtlasMarkers = null;
            _conduit.ReferenceMarkers = _referenceMarkers;
            _conduit.ShowField = _preview;
            _conduit.ShowReferences = _referenceMarkers.Count > 0;
            _conduit.HasHit = _hasHover &&
                _tool != WasperPaintTool.None &&
                _tool != WasperPaintTool.Smooth;
            _conduit.HitPoint = _hoverPoint;
            _conduit.HitNormal = _hoverNormal;
            _conduit.Radius = _radius;
            _conduit.Tool = _tool;
            _conduit.Enabled =
                _preview ||
                _tool != WasperPaintTool.None ||
                _conduit.ShowReferences;
            _paintForm?.RefreshCanvas();
        }

    }
}
