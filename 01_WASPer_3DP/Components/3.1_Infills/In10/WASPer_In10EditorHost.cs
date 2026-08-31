using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json;
using Rhino;
using Rhino.Geometry;
using WASPer_3DP.PatternEditing;

namespace WASPer_3DP.Components._3_1_Infills
{
    public partial class wsp_In10_Layered_Multi_Infill_From_Curves
    {
        private static int GuideAnchorCountForParams(
            IWasperInfillParams parameters,
            int controlsPerCell = 0)
        {
            if (parameters is WasperInfill2DParams infill2D)
                return Clamp(Math.Max(1, infill2D.Count) + 2, 3, 130);
            double cycles = 1.0;
            if (parameters is WasperTpmsInfillParams tpms)
                cycles = Math.Abs(tpms.CountX);
            else if (parameters is WasperTurtleInfillParams turtle)
                cycles = Math.Max(1, turtle.CountX);
            int automatic = parameters is WasperTurtleInfillParams ? 1 : 4;
            int density = controlsPerCell > 0 ? controlsPerCell : automatic;
            int segments = Math.Max(1, (int)Math.Ceiling(cycles * density));
            return Clamp(segments + 1, 2, 257);
        }

        private static IReadOnlyList<IReadOnlyList<double>> BuildGuideSourceStations(
            int guideCount,
            IReadOnlyList<IWasperInfillParams> infillParams,
            IReadOnlyDictionary<int, int> densityOverrides,
            out IReadOnlyList<IReadOnlyList<bool>> primaryStations)
        {
            var stations = Enumerable.Range(0, Math.Max(0, guideCount))
                .Select(_ => new List<(double u, bool primary)>
                {
                    (0.0, true),
                    (1.0, true)
                })
                .ToList();
            if (guideCount < 2 || infillParams == null || infillParams.Count == 0)
            {
                primaryStations = stations
                    .Select(guide => (IReadOnlyList<bool>)guide.Select(item => item.primary).ToList())
                    .ToList();
                return stations
                    .Select(guide => (IReadOnlyList<double>)guide.Select(item => item.u).ToList())
                    .ToList();
            }

            void Add(int guide, double u, bool primary)
            {
                if (guide < 0 || guide >= stations.Count)
                    return;
                u = Math.Max(0.0, Math.Min(1.0, u));
                int existing = stations[guide].FindIndex(item => Math.Abs(item.u - u) <= 1e-8);
                if (existing < 0)
                    stations[guide].Add((u, primary));
                else if (primary && !stations[guide][existing].primary)
                    stations[guide][existing] = (stations[guide][existing].u, true);
            }

            double Wrapped(double value)
            {
                value -= Math.Floor(value);
                return value < 1e-10 || value > 1.0 - 1e-10 ? 0.0 : value;
            }

            for (int domain = 0; domain < guideCount - 1; domain++)
            {
                IWasperInfillParams parameters = infillParams[domain % infillParams.Count];
                int Density(int guide, int automatic) =>
                    densityOverrides != null && densityOverrides.TryGetValue(guide, out int value)
                        ? Clamp(value, 1, 32)
                        : automatic;
                if (parameters is WasperInfill2DParams infill2D)
                {
                    int count = Math.Max(1, infill2D.Count);
                    double phase = infill2D.Flip
                        ? -Wrap01(infill2D.PhaseShift)
                        : Wrap01(infill2D.PhaseShift);
                    for (int cell = 0; cell < count; cell++)
                    {
                        if (infill2D.Type == 2)
                        {
                            double stick = Wrapped((cell + 0.5 + phase) / count);
                            Add(domain, stick, true);
                            Add(domain + 1, stick, true);
                            continue;
                        }

                        double lowVertex = Wrapped((cell - phase) / count);
                        double highVertex = Wrapped((cell + 0.5 - phase) / count);
                        if (infill2D.Type == 1)
                        {
                            // Square-S has two transverse landings per cell: the
                            // cell boundary and the half-cell side transition.
                            // Both ends of both lines are primary controls so the
                            // editor can move every square interval independently.
                            Add(domain, lowVertex, true);
                            Add(domain + 1, lowVertex, true);
                            Add(domain, highVertex, true);
                            Add(domain + 1, highVertex, true);
                        }
                        else if (infill2D.Flip)
                        {
                            Add(domain, highVertex, true);
                            Add(domain + 1, lowVertex, true);
                        }
                        else
                        {
                            Add(domain, lowVertex, true);
                            Add(domain + 1, highVertex, true);
                        }
                    }
                    continue;
                }

                if (parameters is WasperTurtleInfillParams turtle)
                {
                    int count = Math.Max(1, turtle.CountX);
                    for (int side = 0; side < 2; side++)
                    {
                        int guide = domain + side;
                        int density = Density(guide, 1);
                        for (int cell = 0; cell < count; cell++)
                        {
                            for (int control = 0; control < density; control++)
                            {
                                double centre = (cell + (control + 0.5) / density) / count;
                                Add(guide, centre, control == density / 2);
                            }
                        }
                    }
                    continue;
                }

                for (int side = 0; side < 2; side++)
                {
                    int guide = domain + side;
                    int density = Density(guide, 4);
                    int anchorCount = GuideAnchorCountForParams(parameters, density);
                    for (int anchor = 1; anchor < anchorCount - 1; anchor++)
                    {
                        double u = (double)anchor / (anchorCount - 1);
                        bool primary = false;
                        if (parameters is WasperTpmsInfillParams tpms)
                        {
                            double position = u * Math.Max(1e-9, Math.Abs(tpms.CountX));
                            double local = position - Math.Floor(position);
                            primary = Math.Abs(local - 0.5) <= 0.5 / density + 1e-6;
                        }
                        Add(guide, u, primary);
                    }
                }
            }

            foreach (List<(double u, bool primary)> guideStations in stations)
                guideStations.Sort((a, b) => a.u.CompareTo(b.u));
            primaryStations = stations
                .Select(guide => (IReadOnlyList<bool>)guide.Select(item => item.primary).ToList())
                .ToList();
            return stations
                .Select(guide => (IReadOnlyList<double>)guide.Select(item => item.u).ToList())
                .ToList();
        }

        private static List<int> BuildGuideAutomaticDensities(
            int guideCount,
            IReadOnlyList<IWasperInfillParams> infillParams)
        {
            var result = Enumerable.Repeat(0, Math.Max(0, guideCount)).ToList();
            if (infillParams == null || infillParams.Count == 0)
                return result;
            for (int domain = 0; domain < guideCount - 1; domain++)
            {
                IWasperInfillParams parameters = infillParams[domain % infillParams.Count];
                int automatic = parameters is WasperTpmsInfillParams
                    ? 4
                    : parameters is WasperTurtleInfillParams ? 1 : 0;
                if (automatic <= 0)
                    continue;
                result[domain] = Math.Max(result[domain], automatic);
                result[domain + 1] = Math.Max(result[domain + 1], automatic);
            }
            return result;
        }

        private static IReadOnlyList<IReadOnlyList<IReadOnlyList<PointF>>> BuildLayerGuideEditorCurves(
            GH_Structure<GH_Curve> guideTree,
            double tolerance,
            out IReadOnlyList<IReadOnlyList<Curve>> previewCurves)
        {
            previewCurves = Array.Empty<IReadOnlyList<Curve>>();
            if (guideTree == null)
                return Array.Empty<IReadOnlyList<IReadOnlyList<PointF>>>();
            var allEditorCurves = new List<IReadOnlyList<IReadOnlyList<PointF>>>(guideTree.PathCount);
            var allPreviewCurves = new List<IReadOnlyList<Curve>>(guideTree.PathCount);
            for (int branchIndex = 0; branchIndex < guideTree.PathCount; branchIndex++)
            {
                List<GH_Curve> branch = guideTree.Branches[branchIndex];
                if (branch == null || branch.Count < 2)
                {
                    allEditorCurves.Add(Array.Empty<IReadOnlyList<PointF>>());
                    allPreviewCurves.Add(Array.Empty<Curve>());
                    continue;
                }
                var curves = new List<Curve>(branch.Count);
                bool valid = true;
                foreach (GH_Curve goo in branch)
                {
                    Curve curve = goo?.Value?.DuplicateCurve();
                    if (curve == null || !curve.IsValid)
                    {
                        valid = false;
                        break;
                    }
                    curves.Add(curve);
                }
                if (!valid)
                {
                    allEditorCurves.Add(Array.Empty<IReadOnlyList<PointF>>());
                    allPreviewCurves.Add(Array.Empty<Curve>());
                    continue;
                }

                AlignGuideDirections(curves, tolerance);
                allPreviewCurves.Add(curves.Select(curve => curve.DuplicateCurve()).ToList());

                Plane plane = EditorProjectionPlane(
                    WasperLayerPlaneTools.EstimateLayerPlaneFromGhCurves(
                        branch,
                        tolerance));
                var layerResult = new List<IReadOnlyList<PointF>>(curves.Count);
                const int sampleCount = 121;
                foreach (Curve curve in curves)
                {
                    double length = curve.GetLength();
                    var samples = new PointF[sampleCount];
                    for (int i = 0; i < sampleCount; i++)
                    {
                        double u = (double)i / (sampleCount - 1);
                        if (!curve.LengthParameter(u * length, out double parameter))
                            parameter = curve.Domain.ParameterAt(u);
                        Point3d point = curve.PointAt(parameter);
                        if (!plane.ClosestParameter(point, out double x, out double y))
                        {
                            x = point.X;
                            y = point.Y;
                        }
                        samples[i] = new PointF((float)x, (float)y);
                    }
                    layerResult.Add(samples);
                }
                allEditorCurves.Add(layerResult);
            }
            previewCurves = allPreviewCurves;
            return allEditorCurves;
        }

        private static IReadOnlyList<IReadOnlyList<IReadOnlyList<PointF>>> BuildLayerShellEditorCurves(
            IReadOnlyList<List<PolylineCurve>> shellLayers,
            GH_Structure<GH_Curve> guideTree,
            double tolerance)
        {
            if (shellLayers == null || guideTree == null)
                return Array.Empty<IReadOnlyList<IReadOnlyList<PointF>>>();
            var result = new List<IReadOnlyList<IReadOnlyList<PointF>>>(shellLayers.Count);
            for (int layer = 0; layer < shellLayers.Count; layer++)
            {
                List<PolylineCurve> shells = shellLayers[layer];
                if (shells == null || shells.Count == 0 || layer >= guideTree.PathCount)
                {
                    result.Add(Array.Empty<IReadOnlyList<PointF>>());
                    continue;
                }
                Plane plane = EditorProjectionPlane(
                    WasperLayerPlaneTools.EstimateLayerPlaneFromGhCurves(
                        guideTree.Branches[layer],
                        tolerance));
                var projected = new List<IReadOnlyList<PointF>>(shells.Count);
                foreach (PolylineCurve shell in shells)
                {
                    if (shell == null || !shell.IsValid)
                        continue;
                    Polyline polyline = shell.ToPolyline();
                    var points = new List<PointF>(polyline.Count);
                    foreach (Point3d point in polyline)
                    {
                        if (!plane.ClosestParameter(point, out double x, out double y))
                        {
                            x = point.X;
                            y = point.Y;
                        }
                        points.Add(new PointF((float)x, (float)y));
                    }
                    if (points.Count >= 2)
                        projected.Add(points);
                }
                result.Add(projected);
            }
            return result;
        }

        private static IReadOnlyList<IReadOnlyList<IReadOnlyList<PointF>>> BuildLayerPartitionEditorCurves(
            IReadOnlyList<List<(GH_Path path, Curve crv)>> partitionLayers,
            GH_Structure<GH_Curve> guideTree,
            double tolerance)
        {
            if (partitionLayers == null || guideTree == null)
                return Array.Empty<IReadOnlyList<IReadOnlyList<PointF>>>();
            var result = new List<IReadOnlyList<IReadOnlyList<PointF>>>(partitionLayers.Count);
            for (int layer = 0; layer < partitionLayers.Count; layer++)
            {
                List<(GH_Path path, Curve crv)> partitions = partitionLayers[layer];
                if (partitions == null || partitions.Count == 0 || layer >= guideTree.PathCount)
                {
                    result.Add(Array.Empty<IReadOnlyList<PointF>>());
                    continue;
                }
                Plane plane = EditorProjectionPlane(
                    WasperLayerPlaneTools.EstimateLayerPlaneFromGhCurves(
                        guideTree.Branches[layer],
                        tolerance));
                var projected = new List<IReadOnlyList<PointF>>(partitions.Count);
                foreach ((GH_Path path, Curve crv) item in partitions)
                {
                    Curve partition = item.crv;
                    if (partition == null || !partition.IsValid)
                        continue;
                    double length = partition.GetLength();
                    int sampleCount = Math.Max(2, Math.Min(241, (int)Math.Ceiling(length / Math.Max(tolerance * 10.0, length / 120.0)) + 1));
                    var points = new PointF[sampleCount];
                    for (int i = 0; i < sampleCount; i++)
                    {
                        double u = (double)i / (sampleCount - 1);
                        Point3d point = PointAtNormalizedLength(partition, length, u, tolerance);
                        if (!plane.ClosestParameter(point, out double x, out double y))
                        {
                            x = point.X;
                            y = point.Y;
                        }
                        points[i] = new PointF((float)x, (float)y);
                    }
                    projected.Add(points);
                }
                result.Add(projected);
            }
            return result;
        }

        private static Plane EditorProjectionPlane(Plane layerPlane)
        {
            Vector3d normal = layerPlane.Normal;
            if (!normal.Unitize())
                normal = Vector3d.ZAxis;
            Vector3d xAxis = Vector3d.XAxis -
                normal * Vector3d.Multiply(Vector3d.XAxis, normal);
            if (!xAxis.Unitize())
            {
                xAxis = Vector3d.YAxis -
                    normal * Vector3d.Multiply(Vector3d.YAxis, normal);
                if (!xAxis.Unitize())
                    xAxis = layerPlane.XAxis;
            }
            Vector3d yAxis = Vector3d.CrossProduct(normal, xAxis);
            if (!yAxis.Unitize())
                yAxis = layerPlane.YAxis;
            return new Plane(layerPlane.Origin, xAxis, yAxis);
        }

        private static IReadOnlyList<double> GuideWarpForLayer(
            IReadOnlyDictionary<int, List<double>> globalSnapshot,
            IReadOnlyDictionary<int, Dictionary<int, List<double>>> layerSnapshot,
            int layer,
            int guide,
            IReadOnlyList<double> sourceStations)
        {
            if (layerSnapshot != null &&
                layerSnapshot.TryGetValue(layer, out Dictionary<int, List<double>> guides) &&
                guides.TryGetValue(guide, out List<double> layerValues))
                return FitWarpToStations(layerValues, sourceStations);
            if (globalSnapshot != null && globalSnapshot.TryGetValue(guide, out List<double> globalValues))
                return FitWarpToStations(globalValues, sourceStations);
            return sourceStations?.ToArray() ?? new[] { 0.0, 1.0 };
        }

        private IReadOnlyList<double> GetEffectiveGuideWarp(
            int layer,
            int guide,
            IReadOnlyList<double> sourceStations)
        {
            if (_layerGuideWarps.TryGetValue(layer, out Dictionary<int, List<double>> guides) &&
                guides.TryGetValue(guide, out List<double> values))
                return FitWarpToStations(values, sourceStations);
            return _guideWarp.Get(guide, sourceStations);
        }

        private static IReadOnlyList<double> FitWarpToStations(
            IReadOnlyList<double> values,
            IReadOnlyList<double> sourceStations)
        {
            if (sourceStations == null || sourceStations.Count < 2)
                return new[] { 0.0, 1.0 };
            if (values == null || values.Count < 2)
                return sourceStations.ToArray();
            if (values.Count == sourceStations.Count)
                return values;
            var result = new double[sourceStations.Count];
            for (int i = 0; i < result.Length; i++)
            {
                double position = sourceStations[i] * (values.Count - 1);
                int index = Math.Min(values.Count - 2, Math.Max(0, (int)Math.Floor(position)));
                double local = position - index;
                result[i] = values[index] + (values[index + 1] - values[index]) * local;
            }
            result[0] = 0.0;
            result[result.Length - 1] = 1.0;
            return result;
        }

        private static Dictionary<int, Dictionary<int, List<double>>> CloneLayerGuideWarps(
            IReadOnlyDictionary<int, Dictionary<int, List<double>>> source)
        {
            var clone = new Dictionary<int, Dictionary<int, List<double>>>();
            if (source == null)
                return clone;
            foreach (KeyValuePair<int, Dictionary<int, List<double>>> layer in source)
                clone[layer.Key] = layer.Value.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToList());
            return clone;
        }

        private static double MapGuideWarp(
            IReadOnlyList<double> sourceStations,
            IReadOnlyList<double> warp,
            double source)
        {
            source = Math.Max(0.0, Math.Min(1.0, source));
            if (sourceStations == null || warp == null ||
                sourceStations.Count < 2 || sourceStations.Count != warp.Count)
                return source;
            int index = 0;
            while (index < sourceStations.Count - 2 && source > sourceStations[index + 1])
                index++;
            double span = sourceStations[index + 1] - sourceStations[index];
            double local = span <= 1e-12
                ? 0.0
                : (source - sourceStations[index]) / span;
            local = Math.Max(0.0, Math.Min(1.0, local));
            return warp[index] + (warp[index + 1] - warp[index]) * local;
        }

        private sealed class GuideWarpHistorySnapshot
        {
            public GuideWarpHistorySnapshot() { }
            public Dictionary<int, List<double>> Global { get; set; }
            public Dictionary<int, Dictionary<int, List<double>>> Layers { get; set; }
            public Dictionary<int, int> Density { get; set; }
            public WasperShellSeamSettings ShellGlobal { get; set; }
            public Dictionary<int, WasperShellSeamSettings> ShellLayers { get; set; }
        }

        private GuideWarpHistorySnapshot CaptureGuideWarpHistory() =>
            new GuideWarpHistorySnapshot
            {
                Global = _guideWarp.Snapshot(),
                Layers = CloneLayerGuideWarps(_layerGuideWarps),
                Density = _guideDensityOverrides.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value),
                ShellGlobal = _shellSeam.Clone(),
                ShellLayers = CloneLayerShellSeams(_layerShellSeams)
            };

        private static GuideWarpHistorySnapshot CloneGuideWarpHistory(
            GuideWarpHistorySnapshot snapshot) =>
            snapshot == null
                ? null
                : new GuideWarpHistorySnapshot
                {
                    Global = snapshot.Global?.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value?.ToList() ?? new List<double>()),
                    Layers = CloneLayerGuideWarps(snapshot.Layers),
                    Density = snapshot.Density?.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value) ?? new Dictionary<int, int>(),
                    ShellGlobal = snapshot.ShellGlobal?.Clone() ??
                        new WasperShellSeamSettings(),
                    ShellLayers = CloneLayerShellSeams(snapshot.ShellLayers)
                };

        private static bool GuideWarpHistoriesEqual(
            GuideWarpHistorySnapshot left,
            GuideWarpHistorySnapshot right)
        {
            if (ReferenceEquals(left, right))
                return true;
            if (left == null || right == null)
                return false;
            return string.Equals(
                JsonConvert.SerializeObject(left),
                JsonConvert.SerializeObject(right),
                StringComparison.Ordinal);
        }

        private static Dictionary<int, WasperShellSeamSettings> CloneLayerShellSeams(
            IReadOnlyDictionary<int, WasperShellSeamSettings> source)
        {
            var clone = new Dictionary<int, WasperShellSeamSettings>();
            if (source == null)
                return clone;
            foreach (KeyValuePair<int, WasperShellSeamSettings> pair in source)
                if (pair.Key >= 0 && pair.Value != null)
                    clone[pair.Key] = pair.Value.Clone();
            return clone;
        }

        private void RestoreGuideWarpHistory(GuideWarpHistorySnapshot snapshot)
        {
            _guideWarp.Restore(snapshot?.Global);
            _guideDensityOverrides.Clear();
            if (snapshot?.Density != null)
                foreach (KeyValuePair<int, int> pair in snapshot.Density)
                    if (pair.Key >= 0)
                        _guideDensityOverrides[pair.Key] = Clamp(pair.Value, 1, 32);
            _layerGuideWarps.Clear();
            if (snapshot?.Layers != null)
                foreach (KeyValuePair<int, Dictionary<int, List<double>>> layer in snapshot.Layers)
                    _layerGuideWarps[layer.Key] = layer.Value.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value.ToList());
            _shellSeam = snapshot?.ShellGlobal?.Clone() ?? new WasperShellSeamSettings();
            _layerShellSeams.Clear();
            if (snapshot?.ShellLayers != null)
                foreach (KeyValuePair<int, WasperShellSeamSettings> layer in snapshot.ShellLayers)
                    if (layer.Key >= 0 && layer.Value != null)
                        _layerShellSeams[layer.Key] = layer.Value.Clone();
        }

        private void ApplyCurrentGuideState()
        {
            _appliedGuideState = CloneGuideWarpHistory(CaptureGuideWarpHistory());
            _appliedGuideStateInitialized = true;
            _hasPendingGuideUpdate = false;
            UpdateLiveMessage();
        }

        private void EditorStateChanged(bool recomputeWhenLive = true)
        {
            if (_guideLive)
            {
                ApplyCurrentGuideState();
                if (recomputeWhenLive)
                    ExpireSolution(true);
            }
            else
            {
                _hasPendingGuideUpdate = true;
                UpdateLiveMessage();
                Instances.ActiveCanvas?.Invalidate();
                RhinoDoc.ActiveDoc?.Views.Redraw();
            }
            _guideEditor?.Refresh();
        }

        private void UpdateLiveMessage()
        {
            string mode = _guideLive
                ? "Live"
                : _hasPendingGuideUpdate ? "Paused*" : "Paused";
            Message = string.IsNullOrWhiteSpace(_lastTypeTag)
                ? $"{_versionTag} | {mode}"
                : $"{_versionTag} | {_lastTypeTag} | {mode}";
        }

        private void NormalizeGuideLayerScope(int layerCount)
        {
            int maximum = Math.Max(0, layerCount - 1);
            _guideLayerFrom = Clamp(_guideLayerFrom, 0, maximum);
            _guideLayerTo = Clamp(_guideLayerTo, 0, maximum);
            if (_guideLayerFrom > _guideLayerTo)
                (_guideLayerFrom, _guideLayerTo) = (_guideLayerTo, _guideLayerFrom);
            _guideDisplayLayer = Clamp(_guideDisplayLayer, 0, maximum);
            if (_guideLayerScope == WasperGuideLayerScope.All)
            {
                _guideLayerFrom = 0;
                _guideLayerTo = maximum;
            }
            else if (_guideLayerScope == WasperGuideLayerScope.Single)
            {
                _guideLayerTo = _guideLayerFrom;
                _guideDisplayLayer = _guideLayerFrom;
            }
            else
            {
                _guideDisplayLayer = Clamp(
                    _guideDisplayLayer,
                    _guideLayerFrom,
                    _guideLayerTo);
            }
        }

        private void SelectGuideDisplayLayer(int layer)
        {
            int maximum = Math.Max(0, _layerGuideEditorCurves.Count - 1);
            _guideDisplayLayer = Clamp(layer, 0, maximum);
            _guideEditorCurves = _guideDisplayLayer < _layerGuideEditorCurves.Count
                ? _layerGuideEditorCurves[_guideDisplayLayer]
                : Array.Empty<IReadOnlyList<PointF>>();
            _guidePreviewCurves = _guideDisplayLayer < _layerGuidePreviewCurves.Count
                ? _layerGuidePreviewCurves[_guideDisplayLayer]
                : Array.Empty<Curve>();
            _shellEditorCurves = _guideDisplayLayer < _layerShellEditorCurves.Count
                ? _layerShellEditorCurves[_guideDisplayLayer]
                : Array.Empty<IReadOnlyList<PointF>>();
            _partitionEditorCurves = _guideDisplayLayer < _layerPartitionEditorCurves.Count
                ? _layerPartitionEditorCurves[_guideDisplayLayer]
                : Array.Empty<IReadOnlyList<PointF>>();
        }

        private int ResolveGuideDisplayLayer()
        {
            int maximum = Math.Max(0, _layerGuidePreviewCurves.Count - 1);
            if (_guideLayerScope == WasperGuideLayerScope.Single)
                return Clamp(_guideLayerFrom, 0, maximum);
            if (_guideLayerScope == WasperGuideLayerScope.Range)
                return Clamp(
                    _guideDisplayLayer,
                    Clamp(_guideLayerFrom, 0, maximum),
                    Clamp(_guideLayerTo, 0, maximum));
            return Clamp(_guideDisplayLayer, 0, maximum);
        }

        private IEnumerable<int> ScopedLayers()
        {
            int count = _layerGuideEditorCurves.Count;
            if (count <= 0)
                yield break;
            if (_guideLayerScope == WasperGuideLayerScope.All)
            {
                for (int layer = 0; layer < count; layer++)
                    yield return layer;
                yield break;
            }
            int from = Clamp(_guideLayerFrom, 0, count - 1);
            int to = _guideLayerScope == WasperGuideLayerScope.Single
                ? from
                : Clamp(_guideLayerTo, from, count - 1);
            for (int layer = from; layer <= to; layer++)
                yield return layer;
        }

        private WasperShellSeamSettings GetEffectiveShellSeam(int layer)
        {
            if (_layerShellSeams.TryGetValue(layer, out WasperShellSeamSettings settings) &&
                settings != null)
                return settings.Clone();
            return _shellSeam.Clone();
        }

        private void UpdateShellSeamScope(Action<WasperShellSeamSettings> update)
        {
            if (update == null)
                return;
            if (_guideLayerScope == WasperGuideLayerScope.All)
            {
                update(_shellSeam);
                foreach (WasperShellSeamSettings settings in _layerShellSeams.Values)
                    update(settings);
                return;
            }
            foreach (int layer in ScopedLayers())
            {
                WasperShellSeamSettings settings = GetEffectiveShellSeam(layer);
                update(settings);
                _layerShellSeams[layer] = settings;
            }
        }

        private void ResetShellSeamScope()
        {
            if (_guideLayerScope == WasperGuideLayerScope.All)
            {
                _shellSeam = new WasperShellSeamSettings();
                _layerShellSeams.Clear();
                return;
            }
            foreach (int layer in ScopedLayers())
                _layerShellSeams.Remove(layer);
        }

        private void ApplyGuideAnchorToScope(
            int guide,
            IReadOnlyList<double> sourceStations,
            int anchor,
            double value)
        {
            if (_guideLayerScope == WasperGuideLayerScope.All)
            {
                _guideWarp.SetAnchor(guide, sourceStations, anchor, value);
                foreach (KeyValuePair<int, Dictionary<int, List<double>>> layer in _layerGuideWarps)
                    if (layer.Value.ContainsKey(guide))
                        SetLayerGuideAnchor(layer.Key, guide, sourceStations, anchor, value);
                return;
            }
            foreach (int layer in ScopedLayers())
                SetLayerGuideAnchor(layer, guide, sourceStations, anchor, value);
        }

        private void SetLayerGuideAnchor(
            int layer,
            int guide,
            IReadOnlyList<double> sourceStations,
            int anchor,
            double value)
        {
            List<double> values = GetEffectiveGuideWarp(layer, guide, sourceStations).ToList();
            if (anchor <= 0 || anchor >= values.Count - 1)
                return;
            const double minimumGap = 0.0025;
            values[anchor] = Math.Max(
                values[anchor - 1] + minimumGap,
                Math.Min(values[anchor + 1] - minimumGap, value));
            if (!_layerGuideWarps.TryGetValue(layer, out Dictionary<int, List<double>> guides))
            {
                guides = new Dictionary<int, List<double>>();
                _layerGuideWarps[layer] = guides;
            }
            guides[guide] = values;
        }

        private void ResetGuideWarpForScope(int guide)
        {
            if (_guideLayerScope == WasperGuideLayerScope.All)
            {
                _guideWarp.Reset(guide);
                foreach (Dictionary<int, List<double>> guides in _layerGuideWarps.Values)
                    guides.Remove(guide);
            }
            else
            {
                foreach (int layer in ScopedLayers())
                    if (_layerGuideWarps.TryGetValue(layer, out Dictionary<int, List<double>> guides))
                        guides.Remove(guide);
            }
            foreach (int layer in _layerGuideWarps.Keys.ToList())
                if (_layerGuideWarps[layer].Count == 0)
                    _layerGuideWarps.Remove(layer);
        }

        internal bool CanOpenGuideEditor =>
            _guideDomainCount > 0 && !_hasMixedGuideTopology;

        internal void ToggleGuideEditor()
        {
            if (!CanOpenGuideEditor)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    _hasMixedGuideTopology
                        ? "Guide Editor is disabled while corresponding guides change between open and closed topology. " +
                          "Open guide geometry is used directly; editing is unavailable only because station topology changes across layers."
                        : "Solve at least one valid guide-curve branch before opening the Guide Editor.");
                return;
            }
            try
            {
                if (_guideEditor == null || _guideEditor.IsClosed)
                    _guideEditor = new WasperEtoGuideWarpEditorForm(this);
                if (_guideConduit == null)
                    _guideConduit = new In10v2GuideConduit(this);
                _guideConduit.Enabled = true;
                _guideEditor.ActivateEditor();
                RhinoDoc.ActiveDoc?.Views.Redraw();
            }
            catch (Exception exception)
            {
                if (_guideConduit != null)
                    _guideConduit.Enabled = false;
                _guideEditor = null;
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    $"Could not open the Guide + Shell Editor: {exception.Message}");
            }
        }

        string IWasperShellSeamEditorHost.GuideEditorTitle =>
            "WASPer Layered Infill + Shell Editor";
        bool IWasperGuideWarpEditorHost.GuideLiveEnabled => _guideLive;
        bool IWasperGuideWarpEditorHost.HasPendingGuideUpdate =>
            _hasPendingGuideUpdate;
        void IWasperGuideWarpEditorHost.ToggleGuideLive()
        {
            RecordUndoEvent("Toggle live guide editor updates");
            _guideLive = !_guideLive;
            if (_guideLive)
            {
                ApplyCurrentGuideState();
                ExpireSolution(true);
            }
            else
            {
                ApplyCurrentGuideState();
                UpdateLiveMessage();
            }
            _guideVisualRevision++;
            Instances.ActiveCanvas?.Invalidate();
            _guideEditor?.Refresh();
        }
        void IWasperGuideWarpEditorHost.ApplyPendingGuideUpdate()
        {
            if (_guideLive)
                return;
            RecordUndoEvent("Apply pending guide editor changes");
            ApplyCurrentGuideState();
            _guideVisualRevision++;
            ExpireSolution(true);
            Instances.ActiveCanvas?.Invalidate();
            _guideEditor?.Refresh();
        }
        int IWasperGuideWarpEditorHost.GuideDomainCount => _guideDomainCount;
        int IWasperShellSeamEditorHost.GuideVisualRevision => _guideVisualRevision;
        IReadOnlyList<IReadOnlyList<PointF>> IWasperGuideWarpEditorHost.GuideEditorCurves =>
            _guideEditorCurves;
        IReadOnlyList<IReadOnlyList<PointF>> IWasperShellSeamEditorHost.ShellEditorCurves =>
            _shellEditorCurves;
        IReadOnlyList<IReadOnlyList<PointF>> IWasperShellSeamEditorHost.ShellPartitionEditorCurves =>
            _partitionEditorCurves;
        WasperShellSeamSettings IWasperShellSeamEditorHost.ShellSeamSettings =>
            GetEffectiveShellSeam(ResolveGuideDisplayLayer());
        int IWasperGuideWarpEditorHost.GetGuideAnchorCount(int guide) =>
            guide >= 0 && guide < _guideAnchorCounts.Count
                ? _guideAnchorCounts[guide]
                : WasperGuideWarpState.DefaultAnchorCount;
        IReadOnlyList<double> IWasperGuideWarpEditorHost.GetGuideSourceStations(int guide) =>
            guide >= 0 && guide < _guideSourceStations.Count
                ? _guideSourceStations[guide]
                : new[] { 0.0, 1.0 };
        IReadOnlyList<double> IWasperGuideWarpEditorHost.GetGuideWarp(int domain) =>
            GetEffectiveGuideWarp(
                _guideDisplayLayer,
                domain,
                domain >= 0 && domain < _guideSourceStations.Count
                    ? _guideSourceStations[domain]
                    : new[] { 0.0, 1.0 });
        bool IWasperGuideWarpEditorHost.IsGuidePrimaryStation(int guide, int stationIndex)
        {
            if (guide < 0 || guide >= _guidePrimaryStations.Count)
                return true;
            IReadOnlyList<bool> primary = _guidePrimaryStations[guide];
            if (stationIndex < 0 || stationIndex >= primary.Count)
                return true;
            return primary[stationIndex];
        }
        void IWasperGuideWarpEditorHost.SelectGuide(int guide)
        {
            _activeGuideIndex = Clamp(guide, 0, Math.Max(0, _guideDomainCount - 1));
            _guideVisualRevision++;
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }
        bool IWasperGuideWarpEditorHost.GuideSupportsControlDensity(int guide) =>
            guide >= 0 && guide < _guideAutomaticDensities.Count &&
            _guideAutomaticDensities[guide] > 0;
        int IWasperGuideWarpEditorHost.GetGuideControlDensity(int guide)
        {
            if (_guideDensityOverrides.TryGetValue(guide, out int value))
                return Clamp(value, 1, 32);
            return guide >= 0 && guide < _guideAutomaticDensities.Count &&
                _guideAutomaticDensities[guide] > 0
                ? _guideAutomaticDensities[guide]
                : 1;
        }
        bool IWasperGuideWarpEditorHost.HasGuideControlDensityOverride(int guide) =>
            _guideDensityOverrides.ContainsKey(guide);
        void IWasperGuideWarpEditorHost.SetGuideControlDensity(int guide, int density)
        {
            if (guide < 0 || guide >= _guideAutomaticDensities.Count ||
                _guideAutomaticDensities[guide] <= 0)
                return;
            density = Clamp(density, 1, 32);
            if (_guideDensityOverrides.TryGetValue(guide, out int current) && current == density)
                return;
            RecordUndoEvent("Change guide editor control density");
            _guideDensityOverrides[guide] = density;
            _guideVisualRevision++;
            EditorStateChanged();
        }
        void IWasperGuideWarpEditorHost.ResetGuideControlDensity(int guide)
        {
            if (!_guideDensityOverrides.ContainsKey(guide))
                return;
            RecordUndoEvent("Reset guide editor control density");
            _guideDensityOverrides.Remove(guide);
            _guideVisualRevision++;
            EditorStateChanged();
        }
        int IWasperShellSeamEditorHost.GuideLayerCount => _layerGuideEditorCurves.Count;
        WasperGuideLayerScope IWasperShellSeamEditorHost.GuideLayerScope => _guideLayerScope;
        int IWasperShellSeamEditorHost.GuideLayerFrom => _guideLayerFrom;
        int IWasperShellSeamEditorHost.GuideLayerTo => _guideLayerTo;
        int IWasperShellSeamEditorHost.GuideDisplayLayer => _guideDisplayLayer;
        void IWasperShellSeamEditorHost.SetGuideLayerScope(
            WasperGuideLayerScope scope,
            int fromLayer,
            int toLayer,
            int displayLayer)
        {
            _guideLayerScope = scope;
            _guideLayerFrom = fromLayer;
            _guideLayerTo = toLayer;
            _guideDisplayLayer = displayLayer;
            NormalizeGuideLayerScope(_layerGuideEditorCurves.Count);
            SelectGuideDisplayLayer(_guideDisplayLayer);
            _guideVisualRevision++;
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }
        void IWasperShellSeamEditorHost.GuideEditorClosed()
        {
            if (_guideConduit != null)
                _guideConduit.Enabled = false;
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }
        bool IWasperGuideWarpEditorHost.CanUndoGuideWarp => _guideUndo.Count > 0;
        bool IWasperGuideWarpEditorHost.CanRedoGuideWarp => _guideRedo.Count > 0;

        void IWasperGuideWarpEditorHost.BeginGuideWarpEdit()
        {
            if (_guideEditBefore != null)
                return;
            RecordUndoEvent("Edit shared infill guide layout");
            _guideEditBefore = CaptureGuideWarpHistory();
        }

        void IWasperGuideWarpEditorHost.PreviewGuideWarpAnchor(
            int domain,
            int anchor,
            double value)
        {
            IReadOnlyList<double> sourceStations = domain >= 0 && domain < _guideSourceStations.Count
                ? _guideSourceStations[domain]
                : new[] { 0.0, 1.0 };
            ApplyGuideAnchorToScope(domain, sourceStations, anchor, value);
            _guideVisualRevision++;
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        void IWasperGuideWarpEditorHost.CommitGuideWarpEdit()
        {
            if (_guideEditBefore != null)
            {
                _guideUndo.Push(_guideEditBefore);
                _guideRedo.Clear();
                _guideEditBefore = null;
            }
            _guideVisualRevision++;
            EditorStateChanged();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        void IWasperGuideWarpEditorHost.CancelGuideWarpEdit()
        {
            if (_guideEditBefore == null)
                return;
            RestoreGuideWarpHistory(_guideEditBefore);
            _guideEditBefore = null;
            _guideVisualRevision++;
        }

        void IWasperGuideWarpEditorHost.UndoGuideWarp()
        {
            if (_guideUndo.Count == 0)
                return;
            RecordUndoEvent("Undo shared infill guide layout");
            _guideRedo.Push(CaptureGuideWarpHistory());
            RestoreGuideWarpHistory(_guideUndo.Pop());
            _guideVisualRevision++;
            EditorStateChanged();
        }

        void IWasperGuideWarpEditorHost.RedoGuideWarp()
        {
            if (_guideRedo.Count == 0)
                return;
            RecordUndoEvent("Redo shared infill guide layout");
            _guideUndo.Push(CaptureGuideWarpHistory());
            RestoreGuideWarpHistory(_guideRedo.Pop());
            _guideVisualRevision++;
            EditorStateChanged();
        }

        void IWasperGuideWarpEditorHost.ResetGuideWarp(int domain)
        {
            RecordUndoEvent("Reset shared infill guide layout");
            _guideUndo.Push(CaptureGuideWarpHistory());
            _guideRedo.Clear();
            ResetGuideWarpForScope(domain);
            _guideVisualRevision++;
            EditorStateChanged();
        }

        void IWasperGuideWarpEditorHost.ResetAllGuideWarps()
        {
            RecordUndoEvent("Reset all shared infill guides");
            _guideUndo.Push(CaptureGuideWarpHistory());
            _guideRedo.Clear();
            for (int guide = 0; guide < _guideDomainCount; guide++)
                ResetGuideWarpForScope(guide);
            _guideVisualRevision++;
            EditorStateChanged();
        }

        void IWasperShellSeamEditorHost.BeginShellSeamEdit()
        {
            if (_guideEditBefore != null)
                return;
            RecordUndoEvent("Edit shell seam");
            _guideEditBefore = CaptureGuideWarpHistory();
        }

        void IWasperShellSeamEditorHost.PreviewShellSeam(double seamU)
        {
            UpdateShellSeamScope(settings => settings.SeamU = Wrap01(seamU));
            _guideVisualRevision++;
        }

        void IWasperShellSeamEditorHost.PreviewShellOffset(
            bool startPoint,
            double inwardOffset,
            double tangentialOffset)
        {
            if (double.IsNaN(inwardOffset) || double.IsInfinity(inwardOffset) ||
                double.IsNaN(tangentialOffset) || double.IsInfinity(tangentialOffset))
                return;
            UpdateShellSeamScope(settings =>
            {
                if (startPoint)
                {
                    settings.StartOffset = inwardOffset;
                    settings.StartTangentialOffset = tangentialOffset;
                }
                else
                {
                    settings.EndOffset = inwardOffset;
                    settings.EndTangentialOffset = tangentialOffset;
                }
            });
            _guideVisualRevision++;
        }

        void IWasperShellSeamEditorHost.CommitShellSeamEdit()
        {
            if (_guideEditBefore != null)
            {
                _guideUndo.Push(_guideEditBefore);
                _guideRedo.Clear();
                _guideEditBefore = null;
            }
            _guideVisualRevision++;
            EditorStateChanged();
        }

        void IWasperShellSeamEditorHost.CancelShellSeamEdit()
        {
            if (_guideEditBefore == null)
                return;
            RestoreGuideWarpHistory(_guideEditBefore);
            _guideEditBefore = null;
            _guideVisualRevision++;
        }

        void IWasperShellSeamEditorHost.SetShellXSeam(bool enabled)
        {
            RecordUndoEvent("Toggle X seam");
            _guideUndo.Push(CaptureGuideWarpHistory());
            _guideRedo.Clear();
            UpdateShellSeamScope(settings => settings.XSeam = enabled);
            _guideVisualRevision++;
            EditorStateChanged();
        }

        void IWasperShellSeamEditorHost.SetShellFilletRadius(double radius)
        {
            if (double.IsNaN(radius) || double.IsInfinity(radius))
                return;
            RecordUndoEvent("Set shell seam fillet radius");
            _guideUndo.Push(CaptureGuideWarpHistory());
            _guideRedo.Clear();
            UpdateShellSeamScope(settings => settings.FilletRadius = Math.Max(0.0, radius));
            _guideVisualRevision++;
            EditorStateChanged();
        }

        void IWasperShellSeamEditorHost.ResetShellSeam()
        {
            RecordUndoEvent("Reset shell seam");
            _guideUndo.Push(CaptureGuideWarpHistory());
            _guideRedo.Clear();
            ResetShellSeamScope();
            _guideVisualRevision++;
            EditorStateChanged();
        }

    }
}
