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
        private void InitializePath(
            WasperPrintPath source,
            IList<int> targetRoles,
            string signature,
            string topologySignature,
            IList<PaintBranchLayout> branchLayout)
        {
            _signature = signature;
            _topologySignature = topologySignature;
            int count = CountSelectedPoints(source, targetRoles);
            _paintValues.Reset(count);
            ClearPainterHistory();
            bool selectedStateMatches = _pendingState != null &&
                (string.Equals(_pendingState.Signature, signature, StringComparison.Ordinal) ||
                 (_pendingState.Version < 3 &&
                  string.Equals(
                      _pendingState.TopologySignature,
                      topologySignature,
                      StringComparison.Ordinal)));
            bool legacyStateMatches = _pendingState != null &&
                (string.Equals(
                     _pendingState.Signature,
                     ComputePathDependentSelectedSignature(source, targetRoles),
                     StringComparison.Ordinal) ||
                 string.Equals(
                     _pendingState.TopologySignature,
                     ComputePathDependentSelectedTopologySignature(source, targetRoles),
                     StringComparison.Ordinal) ||
                 string.Equals(
                     _pendingState.Signature,
                     ComputeLegacySignature(source),
                     StringComparison.Ordinal) ||
                 string.Equals(
                     _pendingState.TopologySignature,
                     ComputeLegacyTopologySignature(source),
                     StringComparison.Ordinal));
            double[] restoredValues = selectedStateMatches
                ? RestoreSavedBranchValues(
                      _pendingState,
                      branchLayout,
                      _pendingState?.Values) ??
                  SelectSavedValues(source, targetRoles, _pendingState?.Values, count)
                : legacyStateMatches
                    ? SelectSavedValues(source, targetRoles, _pendingState?.Values, count)
                    : null;
            if (restoredValues != null)
            {
                _values = restoredValues;
                _appliedValues = RestoreSavedBranchValues(
                    _pendingState,
                    branchLayout,
                    _pendingState.AppliedValues) ??
                    SelectSavedValues(
                        source,
                        targetRoles,
                        _pendingState.AppliedValues,
                        count) ?? (double[])_values.Clone();
                _preview = _pendingState.Preview;
                _atlasFlipMap =
                    _pendingState.AtlasFlipMap ||
                    _pendingState.TextureFlipMap;
                _atlasQuarterTurns =
                    ((_pendingState.AtlasQuarterTurns % 4) + 4) % 4;
                RestoreTexturePlacement(_pendingState);
            }
            else if (_pendingState != null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "Saved paint values did not match the current path signature and were reset to zero.");
            }
            _pendingState = null;
        }

        private static double[] SelectSavedValues(
            WasperPrintPath source,
            IList<int> targetRoles,
            double[] saved,
            int selectedCount)
        {
            if (saved == null) return null;
            if (saved.Length == selectedCount)
                return (double[])saved.Clone();
            if (source?.PtPlanes == null || saved.Length != source.PtPlanes.DataCount)
                return null;

            var selected = new double[selectedCount];
            int global = 0;
            int local = 0;
            for (int b = 0; b < source.PtPlanes.BranchCount; b++)
            {
                GH_Path path = source.PtPlanes.Paths[b];
                bool include = WasperGcodeTreeUtil.MatchesTargetRoles(
                    source.PathRoles,
                    path,
                    targetRoles);
                int branchCount = source.PtPlanes.Branches[b].Count;
                if (include)
                {
                    for (int i = 0; i < branchCount && local < selected.Length; i++)
                        selected[local++] = saved[global + i];
                }
                global += branchCount;
            }
            return local == selected.Length ? selected : null;
        }

        private void BuildLocations(
            WasperPrintPath source,
            IList<int> targetRoles,
            IList<WasperPaintMaskRegion> masks,
            bool invert)
        {
            _locations.Clear();
            _eligibleByStack.Clear();
            int commonPrefix = WasperGcodeTreeUtil.CommonPathPrefixLength(source.PtPlanes.Paths);
            int linear = 0;
            for (int branchIndex = 0; branchIndex < source.PtPlanes.BranchCount; branchIndex++)
            {
                GH_Path path = source.PtPlanes.Paths[branchIndex];
                IList<Plane> branch = source.PtPlanes.Branches[branchIndex];
                bool roleMatch = WasperGcodeTreeUtil.MatchesTargetRoles(
                    source.PathRoles,
                    path,
                    targetRoles);
                if (!roleMatch)
                    continue;
                string stack = StackSignature(path, commonPrefix);
                int layer = WasperGcodeTreeUtil.LayerFromPath(path, commonPrefix);
                for (int item = 0; item < branch.Count; item++, linear++)
                {
                    Plane plane = branch[item];
                    bool maskMatch = masks.Count == 0 || masks.Any(mask => mask.Contains(plane.Origin));
                    if (masks.Count > 0 && invert)
                        maskMatch = !maskMatch;
                    var location = new PaintLocation
                    {
                        Linear = linear,
                        Path = path,
                        Item = item,
                        Stack = stack,
                        Layer = layer,
                        Point = plane.Origin,
                        Plane = plane,
                        RoleEligible = true,
                        Eligible = maskMatch
                    };
                    _locations.Add(location);
                    if (location.Eligible)
                    {
                        if (!_eligibleByStack.TryGetValue(
                                location.Stack,
                                out List<int> eligible))
                        {
                            eligible = new List<int>();
                            _eligibleByStack[location.Stack] = eligible;
                        }
                        eligible.Add(location.Linear);
                    }
                }
            }
        }

        private void BuildSurfacePreviewMesh(
            double tolerance,
            IDictionary<string, ShellDirection> directionByPath)
        {
            _previewMesh = new Mesh();
            _previewSourceIndices.Clear();
            _previewPoints.Clear();
            _referenceMarkers.Clear();
            foreach (PaintLocation location in _locations.Where(item => item.RoleEligible))
            {
                _previewPoints[location.Linear] = SurfacePreviewPointAt(
                    location.Linear,
                    tolerance,
                    directionByPath,
                    true);
            }

            List<List<PreviewRow>> stacks = BuildPreviewStacks(tolerance);

            Color[] sectionColors =
            {
                Color.FromArgb(235, 55, 55),
                Color.FromArgb(240, 190, 45),
                Color.FromArgb(245, 245, 245),
                Color.FromArgb(45, 205, 205),
                Color.FromArgb(190, 90, 235),
                Color.FromArgb(70, 220, 120),
                Color.FromArgb(255, 145, 45),
                Color.FromArgb(75, 145, 245)
            };
            foreach (List<PreviewRow> stack in stacks)
            {
                List<AtlasRow> rows = stack
                    .OrderBy(row => row.Layer)
                    .Select(row => AtlasRow.Create(row, tolerance))
                    .Where(row => row != null)
                    .ToList();
                if (rows.Count < 2)
                    continue;
                bool closed = rows.All(row => row.Closed);
                int logicalSamples = Math.Min(
                    256,
                    Math.Max(2, rows.Max(row => row.LogicalCount)));
                if (closed)
                    logicalSamples = Math.Max(3, logicalSamples);
                for (int rowIndex = 1; rowIndex < rows.Count; rowIndex++)
                    rows[rowIndex].AlignPreviewTo(rows[rowIndex - 1]);
                int columns = logicalSamples;
                var starts = new int[rows.Count];
                for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    starts[rowIndex] = _previewMesh.Vertices.Count;
                    for (int column = 0; column < columns; column++)
                    {
                        double u = closed
                            ? column / (double)columns
                            : column / (double)(columns - 1);
                        int source = rows[rowIndex].PreviewSourceIndexAt(u);
                        Point3d point = _previewPoints.TryGetValue(source, out Point3d mapped)
                            ? mapped
                            : _locations[source].Point + _previewMove;
                        _previewMesh.Vertices.Add(point);
                        _previewSourceIndices.Add(source);
                    }
                }
                for (int rowIndex = 1; rowIndex < rows.Count; rowIndex++)
                {
                    int faceColumns = closed ? columns : columns - 1;
                    for (int column = 0; column < faceColumns; column++)
                    {
                        int next = closed
                            ? (column + 1) % columns
                            : column + 1;
                        _previewMesh.Faces.AddFace(
                            starts[rowIndex - 1] + column,
                            starts[rowIndex - 1] + next,
                            starts[rowIndex] + next,
                            starts[rowIndex] + column);
                    }
                }

                double[] guideStations = DetectCornerGuideStations(rows);
                for (int section = 0; section < guideStations.Length; section++)
                {
                    double u = guideStations[section];
                    for (int row = 1; row < rows.Count; row++)
                    {
                        int first = rows[row - 1].PreviewSourceIndexAt(u);
                        int second = rows[row].PreviewSourceIndexAt(u);
                        _referenceMarkers.Add(new WasperPaintMarker
                        {
                            Line = new Line(
                                SurfacePreviewPointAt(first, tolerance, directionByPath, false),
                                SurfacePreviewPointAt(second, tolerance, directionByPath, false)),
                            Color = sectionColors[section % sectionColors.Length],
                            Thickness = section == 0 ? 4 : 3
                        });
                    }
                }
            }

            if (_previewMesh.Faces.Count == 0)
            {
                _previewMesh = null;
                _previewSourceIndices.Clear();
                _previewPoints.Clear();
                return;
            }
            _previewMesh.Normals.ComputeNormals();
            _previewMesh.Compact();
        }

        private Point3d SurfacePreviewPointAt(
            int sourceIndex,
            double tolerance,
            IDictionary<string, ShellDirection> directionByPath,
            bool includeMove)
        {
            if (sourceIndex < 0 || sourceIndex >= _locations.Count)
                return Point3d.Origin;
            PaintLocation location = _locations[sourceIndex];
            IList<Plane> branch = _source?.PtPlanes?.Branch(location.Path);
            if (branch == null || branch.Count < 2)
                return location.Point + (includeMove ? _previewMove : Vector3d.Zero);
            int logicalCount = branch.Count -
                               (IsDuplicateClosure(branch, tolerance) ? 1 : 0);
            Vector3d tangent = LocalTangent(branch, location.Item, logicalCount);
            Vector3d inward;
            if (directionByPath != null &&
                directionByPath.TryGetValue(
                    location.Path.ToString(),
                    out ShellDirection direction))
            {
                inward = direction.InwardFromTangent(
                    location.Point,
                    tangent,
                    tolerance);
            }
            else
            {
                inward = Vector3d.CrossProduct(location.Plane.ZAxis, tangent);
            }
            if (!inward.Unitize())
                return location.Point + (includeMove ? _previewMove : Vector3d.Zero);

            double width = PathValueAt(_source.LayerWf, location.Path, location.Item);
            if (!double.IsFinite(width) || width <= tolerance)
                width = PathValueAt(_source.LayerW, location.Path, location.Item);
            if (!double.IsFinite(width) || width <= tolerance)
            {
                double height = PathValueAt(_source.LayerH, location.Path, location.Item);
                width = double.IsFinite(height) && height > tolerance
                    ? height * 2.5
                    : 0.0;
            }
            double gap = Math.Max(tolerance * 5.0, width * 0.08);
            double offset = width > tolerance ? width * 0.5 + gap : gap;
            return location.Point - inward * offset +
                   (includeMove ? _previewMove : Vector3d.Zero);
        }

        private static double PathValueAt(
            DataTree<double> tree,
            GH_Path path,
            int item)
        {
            if (tree == null || !tree.PathExists(path))
                return double.NaN;
            IList<double> branch = tree.Branch(path);
            if (branch == null || branch.Count == 0)
                return double.NaN;
            return branch.Count == 1
                ? branch[0]
                : branch[Math.Max(0, Math.Min(item, branch.Count - 1))];
        }

        private void BuildPainterAtlas(double tolerance)
        {
            _painterMesh = new Mesh();
            _painterSourceIndices.Clear();
            _atlasPoints.Clear();
            _atlasMarkers.Clear();
            _atlasRowWidths.Clear();
            _atlasRmsDistortion = 0.0;
            _atlasMaxDistortion = 0.0;
            _atlasTileCount = 0;

            List<List<PreviewRow>> stacks = BuildPreviewStacks(tolerance);

            double tileCursor = 0.0;
            double distortionSquares = 0.0;
            int distortionCount = 0;
            foreach (List<PreviewRow> stackGroup in stacks)
            {
                List<AtlasRow> rows = stackGroup
                    .OrderBy(row => row.Layer)
                    .Select(row => AtlasRow.Create(row, tolerance))
                    .Where(row => row != null)
                    .ToList();
                if (rows.Count < 2)
                    continue;

                PositionAtlasRows(rows, tolerance);
                double minX = rows.Min(row => row.Shift);
                double maxX = rows.Max(row => row.Shift + row.Length);
                double width = Math.Max(maxX - minX, tolerance);
                double tileOrigin = tileCursor - minX;
                _atlasTileCount++;

                foreach (AtlasRow row in rows)
                {
                    _atlasRowWidths[row.Source.Locations[0].Path.ToString()] = row.Length;
                    for (int i = 0; i < row.Source.Locations.Count; i++)
                    {
                        PaintLocation location = row.Source.Locations[i];
                        double arc = row.ArcAtItem(i);
                        _atlasPoints[location.Linear] = AtlasPoint(
                            tileOrigin + row.Shift + arc,
                            row.Height);
                    }
                }

                AppendAtlasTile(
                    rows,
                    tileOrigin,
                    tolerance,
                    ref distortionSquares,
                    ref distortionCount);
                AppendAtlasMarkers(
                    rows,
                    tileOrigin,
                    DetectCornerGuideStations(rows));
                tileCursor += width + Math.Max(_radius * 2.0, width * 0.08);
            }

            if (_painterMesh.Faces.Count == 0)
            {
                _painterMesh = null;
                _painterSourceIndices.Clear();
                _atlasPoints.Clear();
                _painterVisualRevision++;
                return;
            }

            _atlasRmsDistortion = distortionCount > 0
                ? Math.Sqrt(distortionSquares / distortionCount)
                : 0.0;
            _painterMesh.Normals.ComputeNormals();
            _painterMesh.Compact();
            UpdatePreviewColors();
            _painterVisualRevision++;
        }

        private List<List<PreviewRow>> BuildPreviewStacks(double tolerance)
        {
            List<PreviewRow> rows = _locations
                .Where(location => location.RoleEligible)
                .GroupBy(location => location.Path.ToString())
                .Select(group => new PreviewRow
                {
                    Stack = group.First().Stack,
                    Layer = group.First().Layer,
                    Locations = group.OrderBy(location => location.Item).ToList()
                })
                .Where(row => row.Locations.Count >= 2)
                .ToList();
            List<List<PreviewRow>> result =
                GroupPreviewRowsGeometrically(rows, tolerance);
            _eligibleByStack.Clear();
            foreach (PaintLocation location in _locations.Where(item => item.Eligible))
            {
                if (!_eligibleByStack.TryGetValue(
                        location.Stack,
                        out List<int> eligible))
                {
                    eligible = new List<int>();
                    _eligibleByStack[location.Stack] = eligible;
                }
                eligible.Add(location.Linear);
            }
            NormalizePreviewStacks(result, tolerance);
            return result;
        }

        private void NormalizePreviewStacks(
            IList<List<PreviewRow>> stacks,
            double tolerance)
        {
            _normalizedRowReversals = 0;
            _normalizationAuthoritativePlanes = 0;
            _normalizationFittedPlanes = 0;
            _normalizationUnresolvedRows = 0;
            if (stacks == null || _source == null)
                return;

            int commonPrefix = WasperGcodeTreeUtil.CommonPathPrefixLength(
                _source.PtPlanes.Paths);
            foreach (List<PreviewRow> stack in stacks)
            {
                if (stack == null || stack.Count == 0)
                    continue;
                Vector3d referenceNormal = Vector3d.Unset;
                foreach (PreviewRow row in stack.OrderBy(item => item.Layer))
                {
                    if (!RowIsClosed(row.Locations, tolerance))
                        continue;
                    if (!TryNormalizationPlane(
                            row,
                            commonPrefix,
                            tolerance,
                            out Plane plane,
                            out bool authoritative))
                    {
                        _normalizationUnresolvedRows++;
                        continue;
                    }
                    if (authoritative)
                        _normalizationAuthoritativePlanes++;
                    else
                        _normalizationFittedPlanes++;

                    Vector3d normal = plane.ZAxis;
                    if (!normal.Unitize())
                    {
                        _normalizationUnresolvedRows++;
                        continue;
                    }
                    if (!referenceNormal.IsValid ||
                        referenceNormal.SquareLength <= 1e-18)
                    {
                        referenceNormal = CanonicalNormal(normal);
                    }
                    if (normal * referenceNormal < 0.0)
                        plane.Flip();

                    using Curve curve = RowCurve(row.Locations);
                    if (curve == null || !curve.IsValid || !curve.IsClosed)
                    {
                        _normalizationUnresolvedRows++;
                        continue;
                    }
                    CurveOrientation orientation =
                        curve.ClosedCurveOrientation(plane);
                    if (orientation != CurveOrientation.Clockwise)
                        continue;
                    ReverseClosedRowAtExistingSeam(row);
                    _normalizedRowReversals++;
                }
            }
        }

        private bool TryNormalizationPlane(
            PreviewRow row,
            int commonPrefix,
            double tolerance,
            out Plane plane,
            out bool authoritative)
        {
            plane = Plane.Unset;
            authoritative = false;
            GH_Path path = row?.Locations?
                .FirstOrDefault()?
                .Path;
            if (path != null &&
                WasperGcodeTreeUtil.TryLayerPlaneAt(
                    _source.LayerPlanes,
                    path,
                    commonPrefix,
                    out Plane supplied) &&
                supplied.IsValid)
            {
                plane = supplied;
                authoritative = true;
                return true;
            }

            using Curve curve = RowCurve(row?.Locations);
            if (curve != null &&
                curve.IsValid &&
                curve.TryGetPlane(
                    out Plane fitted,
                    Math.Max(tolerance * 10.0, 1e-8)) &&
                fitted.IsValid)
            {
                plane = fitted;
                return true;
            }
            return false;
        }

        private static Curve RowCurve(IList<PaintLocation> locations)
        {
            if (locations == null || locations.Count < 2)
                return null;
            return new PolylineCurve(
                locations.Select(location => location.Point));
        }

        private static Vector3d CanonicalNormal(Vector3d normal)
        {
            if (!normal.Unitize())
                return Vector3d.ZAxis;
            double x = Math.Abs(normal.X);
            double y = Math.Abs(normal.Y);
            double z = Math.Abs(normal.Z);
            double dominant = z >= x && z >= y
                ? normal.Z
                : x >= y
                    ? normal.X
                    : normal.Y;
            if (dominant < 0.0)
                normal.Reverse();
            return normal;
        }

        private static void ReverseClosedRowAtExistingSeam(PreviewRow row)
        {
            if (row?.Locations == null || row.Locations.Count < 4)
                return;
            int last = row.Locations.Count - 1;
            PaintLocation seamStart = row.Locations[0];
            PaintLocation seamEnd = row.Locations[last];
            var reversed = new List<PaintLocation>(row.Locations.Count)
            {
                seamStart
            };
            for (int index = last - 1; index >= 1; index--)
                reversed.Add(row.Locations[index]);
            reversed.Add(seamEnd);
            row.Locations = reversed;
        }

        private static List<List<PreviewRow>> GroupPreviewRowsGeometrically(
            IList<PreviewRow> rows,
            double tolerance)
        {
            var result = new List<List<PreviewRow>>();
            if (rows == null || rows.Count == 0)
                return result;

            List<PreviewRowDescriptor> descriptors = rows
                .Select(row => new PreviewRowDescriptor
                {
                    Row = row,
                    Center = new Point3d(
                        row.Locations.Average(location => location.Point.X),
                        row.Locations.Average(location => location.Point.Y),
                        row.Locations.Average(location => location.Point.Z)),
                    Length = RowLength(row),
                    Closed = RowIsClosed(row.Locations, tolerance)
                })
                .ToList();
            List<List<PreviewRowDescriptor>> levels = descriptors
                .GroupBy(descriptor => descriptor.Row.Layer)
                .OrderBy(group => group.Key)
                .Select(group => group.ToList())
                .ToList();

            var tracks = new List<List<PreviewRowDescriptor>>();
            for (int levelIndex = 0; levelIndex < levels.Count; levelIndex++)
            {
                List<PreviewRowDescriptor> level = levels[levelIndex];
                var candidates = new List<(double Score, int Track, int Row)>();
                for (int rowIndex = 0; rowIndex < level.Count; rowIndex++)
                {
                    PreviewRowDescriptor current = level[rowIndex];
                    for (int trackIndex = 0; trackIndex < tracks.Count; trackIndex++)
                    {
                        PreviewRowDescriptor previous =
                            tracks[trackIndex][tracks[trackIndex].Count - 1];
                        if (previous.Closed != current.Closed)
                            continue;
                        double score =
                            previous.Center.DistanceTo(current.Center) +
                            Math.Abs(previous.Length - current.Length) * 0.25;
                        candidates.Add((score, trackIndex, rowIndex));
                    }
                }

                var usedTracks = new HashSet<int>();
                var usedRows = new HashSet<int>();
                foreach ((double Score, int Track, int Row) candidate in
                         candidates.OrderBy(item => item.Score))
                {
                    if (usedTracks.Contains(candidate.Track) ||
                        usedRows.Contains(candidate.Row))
                        continue;
                    tracks[candidate.Track].Add(level[candidate.Row]);
                    usedTracks.Add(candidate.Track);
                    usedRows.Add(candidate.Row);
                }
                for (int rowIndex = 0; rowIndex < level.Count; rowIndex++)
                {
                    if (!usedRows.Contains(rowIndex))
                        tracks.Add(new List<PreviewRowDescriptor> { level[rowIndex] });
                }
            }

            for (int trackIndex = 0; trackIndex < tracks.Count; trackIndex++)
            {
                var track = new List<PreviewRow>();
                for (int rowIndex = 0; rowIndex < tracks[trackIndex].Count; rowIndex++)
                {
                    PreviewRow row = tracks[trackIndex][rowIndex].Row;
                    row.Layer = rowIndex;
                    row.Stack = $"geometric-{trackIndex}";
                    foreach (PaintLocation location in row.Locations)
                    {
                        location.Stack = row.Stack;
                        location.Layer = rowIndex;
                    }
                    track.Add(row);
                }
                result.Add(track);
            }
            return result;
        }

        private static double RowLength(PreviewRow row)
        {
            if (row?.Locations == null || row.Locations.Count < 2)
                return 0.0;
            double length = 0.0;
            for (int index = 1; index < row.Locations.Count; index++)
            {
                length += row.Locations[index - 1].Point.DistanceTo(
                    row.Locations[index].Point);
            }
            return length;
        }

        private void PositionAtlasRows(
            IList<AtlasRow> rows,
            double tolerance)
        {
            rows[0].Height = 0.0;
            rows[0].Shift = 0.0;
            for (int rowIndex = 1; rowIndex < rows.Count; rowIndex++)
            {
                AtlasRow lower = rows[rowIndex - 1];
                AtlasRow upper = rows[rowIndex];
                var verticalDistances = new List<double>();
                var tangentialDrifts = new List<double>();
                const int probes = 32;
                for (int i = 0; i < probes; i++)
                {
                    double u = i / (double)(probes - 1);
                    Point3d lowerPoint = lower.PointAt(u);
                    Point3d upperPoint = upper.PointAt(u);
                    Vector3d delta = upperPoint - lowerPoint;
                    Vector3d tangent = lower.TangentAt(u);
                    double drift = tangent.Unitize() ? delta * tangent : 0.0;
                    tangentialDrifts.Add(drift);
                    verticalDistances.Add(Math.Sqrt(Math.Max(
                        0.0,
                        delta.SquareLength - drift * drift)));
                }

                double rowShift = Median(tangentialDrifts);
                double separation = Median(verticalDistances);
                if (!double.IsFinite(separation) || separation <= tolerance)
                {
                    separation = Median(Enumerable.Range(0, probes)
                        .Select(i => lower.PointAt(i / (double)(probes - 1))
                            .DistanceTo(upper.PointAt(i / (double)(probes - 1))))
                        .ToList());
                }
                upper.Shift = lower.Shift + (double.IsFinite(rowShift) ? rowShift : 0.0);
                upper.Height = lower.Height + Math.Max(separation, tolerance);
            }
        }

        private void AppendAtlasTile(
            IList<AtlasRow> rows,
            double tileOrigin,
            double tolerance,
            ref double distortionSquares,
            ref int distortionCount)
        {
            bool closed = rows.All(row => row.Closed);
            int logicalSamples = Math.Min(
                256,
                Math.Max(2, rows.Max(row => row.LogicalCount)));
            int columns = closed ? logicalSamples + 1 : logicalSamples;
            var starts = new int[rows.Count];
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                AtlasRow row = rows[rowIndex];
                starts[rowIndex] = _painterMesh.Vertices.Count;
                for (int column = 0; column < columns; column++)
                {
                    double u = column / (double)(columns - 1);
                    int sourceIndex = row.SourceIndexAt(u);
                    Point3d flat = AtlasPoint(
                        tileOrigin + row.Shift + u * row.Length,
                        row.Height);
                    _painterMesh.Vertices.Add(flat);
                    _painterSourceIndices.Add(sourceIndex);

                    if (column > 0)
                    {
                        double previousU = (column - 1) / (double)(columns - 1);
                        AccumulateDistortion(
                            row.PointAt(previousU).DistanceTo(row.PointAt(u)),
                            row.Length / (columns - 1),
                            tolerance,
                            ref distortionSquares,
                            ref distortionCount);
                    }
                }
            }

            for (int rowIndex = 1; rowIndex < rows.Count; rowIndex++)
            {
                for (int column = 0; column < columns - 1; column++)
                {
                    _painterMesh.Faces.AddFace(
                        starts[rowIndex - 1] + column,
                        starts[rowIndex - 1] + column + 1,
                        starts[rowIndex] + column + 1,
                        starts[rowIndex] + column);
                }

                AtlasRow lower = rows[rowIndex - 1];
                AtlasRow upper = rows[rowIndex];
                for (int column = 0; column < columns; column++)
                {
                    double u = column / (double)(columns - 1);
                    double original = lower.PointAt(u).DistanceTo(upper.PointAt(u));
                    double flat = AtlasPoint(
                            tileOrigin + lower.Shift + u * lower.Length,
                            lower.Height)
                        .DistanceTo(AtlasPoint(
                            tileOrigin + upper.Shift + u * upper.Length,
                            upper.Height));
                    AccumulateDistortion(
                        original,
                        flat,
                        tolerance,
                        ref distortionSquares,
                        ref distortionCount);
                }
            }
        }

        private void AppendAtlasMarkers(
            IList<AtlasRow> rows,
            double tileOrigin,
            IReadOnlyList<double> guideStations)
        {
            Color[] colors =
            {
                Color.FromArgb(235, 55, 55),
                Color.FromArgb(240, 190, 45),
                Color.FromArgb(245, 245, 245),
                Color.FromArgb(45, 205, 205),
                Color.FromArgb(190, 90, 235),
                Color.FromArgb(70, 220, 120),
                Color.FromArgb(255, 145, 45),
                Color.FromArgb(75, 145, 245)
            };
            var sections = new List<double>(guideStations ?? Array.Empty<double>());
            if (sections.Count == 0)
                sections.AddRange(new[] { 0.0, 0.25, 0.5, 0.75 });
            bool hasSeam = sections.Any(value => CircularParameterDistance(value, 0.0) <= 1e-9);
            if (hasSeam)
                sections.Add(1.0);

            for (int section = 0; section < sections.Count; section++)
            {
                double u = sections[section];
                for (int row = 1; row < rows.Count; row++)
                {
                    _atlasMarkers.Add(new WasperPaintMarker
                    {
                        Line = new Line(
                            AtlasPoint(
                                tileOrigin + rows[row - 1].Shift + u * rows[row - 1].Length,
                                rows[row - 1].Height),
                            AtlasPoint(
                                tileOrigin + rows[row].Shift + u * rows[row].Length,
                                rows[row].Height)),
                        Color = u >= 1.0 - 1e-9
                            ? colors[0]
                            : colors[section % colors.Length],
                        Thickness = u <= 1e-9 || u >= 1.0 - 1e-9 ? 3 : 2
                    });
                }
            }

            int layerStep = Math.Max(1, rows.Count / 10);
            for (int row = 0; row < rows.Count; row += layerStep)
            {
                _atlasMarkers.Add(new WasperPaintMarker
                {
                    Line = new Line(
                        AtlasPoint(tileOrigin + rows[row].Shift, rows[row].Height),
                        AtlasPoint(
                            tileOrigin + rows[row].Shift + rows[row].Length,
                            rows[row].Height)),
                    Color = Color.FromArgb(150, 35, 45, 65),
                    Thickness = 1
                });
            }
        }

        private static double[] DetectCornerGuideStations(IList<AtlasRow> rows)
        {
            AtlasRow row = rows?
                .Where(item => item != null && item.Closed && item.LogicalCount >= 4)
                .OrderBy(item => Math.Abs(item.Source.Layer -
                    rows.Where(candidate => candidate != null)
                        .Select(candidate => candidate.Source.Layer)
                        .DefaultIfEmpty(0)
                        .Average()))
                .FirstOrDefault();
            if (row == null || row.Length <= 1e-12)
                return new[] { 0.0, 0.25, 0.5, 0.75 };

            var candidates = new List<(double Turn, double Station)>();
            for (int index = 0; index < row.LogicalCount; index++)
            {
                int previous = (index - 1 + row.LogicalCount) % row.LogicalCount;
                int next = (index + 1) % row.LogicalCount;
                Point3d before = row.Source.Locations[previous].Point;
                Point3d current = row.Source.Locations[index].Point;
                Point3d after = row.Source.Locations[next].Point;
                Vector3d incoming = current - before;
                Vector3d outgoing = after - current;
                if (!incoming.Unitize() || !outgoing.Unitize())
                    continue;
                double dot = Math.Max(-1.0, Math.Min(1.0, incoming * outgoing));
                double turn = Math.Acos(dot);
                if (turn < RhinoMath.ToRadians(12.0))
                    continue;
                candidates.Add((turn, row.Cumulative[index] / row.Length));
            }

            var selected = new List<double>();
            const double minimumSeparation = 0.035;
            foreach ((double Turn, double Station) candidate in
                     candidates.OrderByDescending(item => item.Turn))
            {
                if (selected.Any(station =>
                        CircularParameterDistance(station, candidate.Station) <
                        minimumSeparation))
                    continue;
                selected.Add(candidate.Station);
                if (selected.Count >= 8)
                    break;
            }

            return selected.Count >= 3
                ? selected.OrderBy(value => value).ToArray()
                : new[] { 0.0, 0.25, 0.5, 0.75 };
        }

        private static double CircularParameterDistance(double first, double second)
        {
            double distance = Math.Abs(first - second);
            return Math.Min(distance, 1.0 - Math.Min(1.0, distance));
        }

        private Point3d AtlasPoint(double x, double y)
        {
            return _previewPlane.Origin +
                   _previewPlane.XAxis * x +
                   _previewPlane.YAxis * y;
        }

        private void AccumulateDistortion(
            double original,
            double flattened,
            double tolerance,
            ref double squares,
            ref int count)
        {
            if (!double.IsFinite(original) ||
                !double.IsFinite(flattened) ||
                original <= tolerance)
                return;
            double relative = Math.Abs(flattened - original) / original;
            _atlasMaxDistortion = Math.Max(_atlasMaxDistortion, relative);
            squares += relative * relative;
            count++;
        }

        private static double Median(IList<double> values)
        {
            if (values == null || values.Count == 0)
                return 0.0;
            double[] sorted = values
                .Where(double.IsFinite)
                .OrderBy(value => value)
                .ToArray();
            if (sorted.Length == 0)
                return 0.0;
            int middle = sorted.Length / 2;
            return sorted.Length % 2 == 0
                ? (sorted[middle - 1] + sorted[middle]) * 0.5
                : sorted[middle];
        }

        private static bool RowIsClosed(
            IList<PaintLocation> row,
            double tolerance)
        {
            return row != null && row.Count > 2 &&
                   row[0].Point.DistanceTo(row[row.Count - 1].Point) <= tolerance;
        }

        private void UpdatePreviewColors()
        {
            UpdateSurfacePreviewGeometry();
            ApplyReferencePreviewColors(_previewMesh, _previewSourceIndices);
            ApplyPaintColors(_painterMesh, _painterSourceIndices);
        }

        private void ApplyReferencePreviewColors(
            Mesh mesh,
            IList<int> sourceIndices)
        {
            if (mesh == null || mesh.Vertices.Count != sourceIndices.Count)
                return;
            EnsureTexturePreviewColors();
            mesh.VertexColors.Clear();
            foreach (int sourceIndex in sourceIndices)
            {
                double value = sourceIndex >= 0 && sourceIndex < _values.Length
                    ? _values[sourceIndex]
                    : 0.0;
                Color paint = WasperPaintColors.ForValue(value, _domain);
                if (sourceIndex < 0 || sourceIndex >= _texturePreviewColors.Length ||
                    _texturePreviewColors[sourceIndex].A == 0)
                {
                    mesh.VertexColors.Add(paint);
                    continue;
                }
                Color texture = _texturePreviewColors[sourceIndex];
                double alpha = texture.A / 255.0;
                mesh.VertexColors.Add(Color.FromArgb(
                    255,
                    (int)Math.Round(paint.R * (1.0 - alpha) + texture.R * alpha),
                    (int)Math.Round(paint.G * (1.0 - alpha) + texture.G * alpha),
                    (int)Math.Round(paint.B * (1.0 - alpha) + texture.B * alpha)));
            }
        }

        private void UpdateSurfacePreviewGeometry()
        {
            if (_previewMesh == null ||
                _previewMesh.Vertices.Count != _previewSourceIndices.Count)
                return;
            for (int vertex = 0; vertex < _previewSourceIndices.Count; vertex++)
            {
                int source = _previewSourceIndices[vertex];
                if (source < 0 || source >= _locations.Count ||
                    !_previewPoints.TryGetValue(source, out Point3d basePoint))
                    continue;
                Vector3d outward =
                    (basePoint - _previewMove) - _locations[source].Point;
                if (!outward.Unitize())
                    outward = _locations[source].Plane.YAxis;
                double displacement = source < _values.Length
                    ? _values[source]
                    : 0.0;
                _previewMesh.Vertices.SetVertex(
                    vertex,
                    basePoint + outward * displacement);
            }
            _previewMesh.Normals.ComputeNormals();
        }

        private void ApplyPaintColors(
            Mesh mesh,
            IList<int> sourceIndices)
        {
            if (mesh == null || mesh.Vertices.Count != sourceIndices.Count)
                return;
            mesh.VertexColors.Clear();
            foreach (int sourceIndex in sourceIndices)
            {
                double value = sourceIndex >= 0 && sourceIndex < _values.Length
                    ? _values[sourceIndex]
                    : 0.0;
                mesh.VertexColors.Add(WasperPaintColors.ForValue(value, _domain));
            }
        }

        private void ClampValuesToDomain()
        {
            double min = Math.Min(_domain.T0, _domain.T1);
            double max = Math.Max(_domain.T0, _domain.T1);
            for (int i = 0; i < _values.Length; i++)
                _values[i] = Math.Max(min, Math.Min(max, _values[i]));
            for (int i = 0; i < _appliedValues.Length; i++)
                _appliedValues[i] = Math.Max(min, Math.Min(max, _appliedValues[i]));
        }

        private DataTree<double> BuildPaintTree(
            WasperPrintPath source,
            IList<double> values)
        {
            var tree = new DataTree<double>();
            var selected = _locations
                .GroupBy(location => location.Path.ToString())
                .ToDictionary(
                    group => group.Key,
                    group => group.ToDictionary(location => location.Item, location => location.Linear));
            for (int b = 0; b < source.PtPlanes.BranchCount; b++)
            {
                GH_Path path = source.PtPlanes.Paths[b];
                selected.TryGetValue(path.ToString(), out Dictionary<int, int> branchValues);
                for (int i = 0; i < source.PtPlanes.Branches[b].Count; i++)
                {
                    double value = branchValues != null &&
                                   branchValues.TryGetValue(i, out int selectedIndex) &&
                                   selectedIndex >= 0 &&
                                   selectedIndex < values.Count
                        ? values[selectedIndex]
                        : 0.0;
                    tree.Add(value, path);
                }
            }
            return tree;
        }

    }
}
