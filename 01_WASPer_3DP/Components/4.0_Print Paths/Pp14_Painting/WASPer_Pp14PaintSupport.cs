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
        public override bool Write(GH_IWriter writer)
        {
            writer.SetInt32(VisibleOutputsMaskKey, _visibleOutputsMask);
            writer.SetBoolean(ConstrainInfillKey, _constrainInfill);
            writer.SetBoolean(PreviewKey, _preview);
            writer.SetBoolean(LiveKey, _live);
            try
            {
                WasperPaintState state = CapturePaintState();
                writer.SetString(
                    PaintStateKey,
                    WasperPaintPersistence.SerializeEmbedded(state));
                if (_outputShellMesh != null && _outputShellMesh.IsValid)
                {
                    GH_IWriter meshChunk = writer.CreateChunk(ShellMeshKey);
                    new GH_Mesh(_outputShellMesh).Write(meshChunk);
                }
            }
            catch
            {
            }
            return base.Write(writer);
        }

        private WasperPaintState CapturePaintState()
        {
            return new WasperPaintState
            {
                Version = 4,
                OwnerInstanceGuid = InstanceGuid.ToString("D"),
                SavedUtc = DateTime.UtcNow,
                Signature = _signature,
                TopologySignature = _topologySignature,
                BranchGeometryKeys = _paintBranchLayout
                    .Select(item => item.GeometryKey)
                    .ToArray(),
                BranchCounts = _paintBranchLayout
                    .Select(item => item.Count)
                    .ToArray(),
                Values = _values == null
                    ? Array.Empty<double>()
                    : (double[])_values.Clone(),
                AppliedValues = _appliedValues == null
                    ? Array.Empty<double>()
                    : (double[])_appliedValues.Clone(),
                Preview = _preview,
                Radius = _radius,
                BrushStrength = _strength,
                SmoothStrength = _smoothStrength,
                Falloff = _falloff,
                TextureSourceKey = _textureLayers[0].SourceKey,
                TexturePlacementInitialized = _textureLayers[0].Placement.Initialized,
                TextureMinX = _textureLayers[0].Placement.MinX,
                TextureMinY = _textureLayers[0].Placement.MinY,
                TextureMaxX = _textureLayers[0].Placement.MaxX,
                TextureMaxY = _textureLayers[0].Placement.MaxY,
                TextureCorners = _textureLayers[0].Placement.Corners
                    .SelectMany(point => new[] { point.X, point.Y })
                    .ToArray(),
                AtlasFlipMap = _atlasFlipMap,
                AtlasQuarterTurns = _atlasQuarterTurns,
                TextureVisible = _textureLayers[0].Visible,
                ActiveTextureLayer = _activeTextureLayer,
                TextureLayers = _textureLayers
                    .Select(CaptureTextureLayerState)
                    .ToArray()
            };
        }

        private static WasperPaintTextureLayerState CaptureTextureLayerState(
            WasperPaintTextureLayer layer)
        {
            return new WasperPaintTextureLayerState
            {
                SourceKey = layer.SourceKey,
                PlacementInitialized = layer.Placement.Initialized,
                MinX = layer.Placement.MinX,
                MinY = layer.Placement.MinY,
                MaxX = layer.Placement.MaxX,
                MaxY = layer.Placement.MaxY,
                Corners = layer.Placement.Corners
                    .SelectMany(point => new[] { point.X, point.Y })
                    .ToArray(),
                Visible = layer.Visible,
                Opacity = layer.Opacity,
                IsText = layer.IsText,
                TextContent = layer.TextContent,
                FontName = layer.FontName,
                FontSize = layer.FontSize,
                TextCommitted = layer.TextCommitted
            };
        }

        internal void SavePainterSession()
        {
            try
            {
                string directory = DefaultMeshPaintDirectory();
                Directory.CreateDirectory(directory);
                using var dialog = new Eto.Forms.SaveFileDialog
                {
                    Title = "Save WASPer Painter Session",
                    FileName = DefaultPainterStem() + ".wasperpaint.json.gz",
                    Directory = new Uri(directory)
                };
                dialog.Filters.Add(new Eto.Forms.FileFilter(
                    "WASPer Painter session (*.wasperpaint.json.gz)", ".wasperpaint.json.gz"));
                if (dialog.ShowDialog(_paintForm) != Eto.Forms.DialogResult.Ok)
                    return;

                WasperPaintPersistence.SaveSession(
                    dialog.FileName,
                    CapturePaintState());
                ShowPainterMessage(
                    $"Session saved:\n{dialog.FileName}",
                    Eto.Forms.MessageBoxType.Information);
            }
            catch (Exception exception)
            {
                ShowPainterMessage(
                    "The Painter session could not be saved:\n" +
                    exception.Message,
                    Eto.Forms.MessageBoxType.Error);
            }
        }

        internal void LoadPainterSession()
        {
            try
            {
                string directory = DefaultMeshPaintDirectory();
                Directory.CreateDirectory(directory);
                using var dialog = new Eto.Forms.OpenFileDialog
                {
                    Title = "Load WASPer Painter Session",
                    Directory = new Uri(directory)
                };
                dialog.Filters.Add(new Eto.Forms.FileFilter(
                    "WASPer Painter session (*.wasperpaint.json.gz)", ".wasperpaint.json.gz"));
                dialog.Filters.Add(new Eto.Forms.FileFilter(
                    "Compressed JSON (*.json.gz)", ".json.gz"));
                if (dialog.ShowDialog(_paintForm) != Eto.Forms.DialogResult.Ok)
                    return;

                WasperPaintState state =
                    WasperPaintPersistence.LoadSession(dialog.FileName);
                double[] loadedValues = RestoreSavedBranchValues(
                    state,
                    _paintBranchLayout,
                    state?.Values) ?? state?.Values;
                if (state == null ||
                    loadedValues == null ||
                    loadedValues.Length != _locations.Count ||
                    !(string.Equals(
                          state.Signature,
                          _signature,
                          StringComparison.Ordinal) ||
                      (state.Version < 3 &&
                       string.Equals(
                           state.TopologySignature,
                           _topologySignature,
                           StringComparison.Ordinal))))
                {
                    ShowPainterMessage(
                        "This session does not match the current Shell topology and was not loaded.",
                        Eto.Forms.MessageBoxType.Warning);
                    return;
                }

                PushPaintUndo((double[])_values.Clone());
                _values = (double[])loadedValues.Clone();
                double[] loadedApplied = RestoreSavedBranchValues(
                    state,
                    _paintBranchLayout,
                    state.AppliedValues) ?? state.AppliedValues;
                _appliedValues = loadedApplied != null &&
                                 loadedApplied.Length == _values.Length
                    ? (double[])loadedApplied.Clone()
                    : (double[])_values.Clone();
                _preview = state.Preview;
                _atlasFlipMap =
                    state.AtlasFlipMap ||
                    state.TextureFlipMap;
                _atlasQuarterTurns = ((state.AtlasQuarterTurns % 4) + 4) % 4;
                RestoreTexturePlacement(state);
                SetPersistentNumber(1, state.Radius > 0.0 ? state.Radius : _radius);
                SetPersistentNumber(
                    2,
                    double.IsFinite(state.BrushStrength)
                        ? Math.Max(0.0, Math.Min(1.0, state.BrushStrength))
                        : _strength);
                SetPersistentNumber(
                    3,
                    double.IsFinite(state.SmoothStrength)
                        ? Math.Max(0.0, Math.Min(1.0, state.SmoothStrength))
                        : _smoothStrength);
                SetPersistentNumber(
                    4,
                    state.Falloff > 0.0 ? state.Falloff : _falloff);
                if (_live)
                    ApplyPreviewState();
                _painterVisualRevision++;
                UpdatePreviewColors();
                UpdateConduit();
                _paintForm?.RefreshCanvas();
                ExpireSolution(true);
                ShowPainterMessage(
                    $"Session loaded:\n{dialog.FileName}",
                    Eto.Forms.MessageBoxType.Information);
            }
            catch (Exception exception)
            {
                ShowPainterMessage(
                    "The Painter session could not be loaded:\n" +
                    exception.Message,
                    Eto.Forms.MessageBoxType.Error);
            }
        }

        private void RestoreTexturePlacement(WasperPaintState state)
        {
            if (state == null)
                return;
            WasperPaintTextureLayerState[] layers = state.TextureLayers;
            if (layers == null || layers.Length == 0)
            {
                layers = new[]
                {
                    new WasperPaintTextureLayerState
                    {
                        SourceKey = state.TextureSourceKey,
                        PlacementInitialized = state.TexturePlacementInitialized,
                        MinX = state.TextureMinX,
                        MinY = state.TextureMinY,
                        MaxX = state.TextureMaxX,
                        MaxY = state.TextureMaxY,
                        Corners = state.TextureCorners,
                        Visible = state.TextureVisible,
                        Opacity = 1.0
                    }
                };
            }
            for (int index = 0;
                 index < Math.Min(layers.Length, _textureLayers.Count);
                 index++)
            {
                RestoreTextureLayerState(_textureLayers[index], layers[index]);
            }
            _activeTextureLayer = Math.Max(
                0,
                Math.Min(_textureLayers.Count - 1, state.ActiveTextureLayer));
        }

        private void RestoreTextureLayerState(
            WasperPaintTextureLayer layer,
            WasperPaintTextureLayerState state)
        {
            if (layer != null && state?.IsText == true)
            {
                layer.Bitmap?.Dispose();
                layer.Bitmap = string.IsNullOrEmpty(state.TextContent)
                    ? null
                    : RasterizeTextTexture(
                        state.TextContent,
                        string.IsNullOrWhiteSpace(state.FontName)
                            ? "Arial"
                            : state.FontName);
                layer.Source = state.TextContent;
                layer.SourceDescription = $"text: {state.TextContent}";
                layer.FontName = string.IsNullOrWhiteSpace(state.FontName)
                    ? "Arial"
                    : state.FontName;
                layer.FontSize = double.IsFinite(state.FontSize) && state.FontSize > 0.0
                    ? state.FontSize
                    : 10.0;
                layer.SourceKey = string.IsNullOrEmpty(state.TextContent)
                    ? string.Empty
                    : $"text|{layer.FontName}|{state.TextContent}";
                layer.IsText = true;
                layer.TextContent = state.TextContent ?? string.Empty;
                layer.TextCommitted = state.TextCommitted;
            }
            if (layer == null || state == null ||
                !state.PlacementInitialized ||
                string.IsNullOrEmpty(state.SourceKey) ||
                !string.Equals(state.SourceKey, layer.SourceKey, StringComparison.Ordinal))
                return;
            layer.Placement.MinX = state.MinX;
            layer.Placement.MinY = state.MinY;
            layer.Placement.MaxX = state.MaxX;
            layer.Placement.MaxY = state.MaxY;
            layer.Placement.Initialized =
                state.MaxX > state.MinX && state.MaxY > state.MinY;
            if (state.Corners != null && state.Corners.Length == 8)
            {
                for (int corner = 0; corner < 4; corner++)
                {
                    layer.Placement.Corners[corner] = new Point2d(
                        state.Corners[corner * 2],
                        state.Corners[corner * 2 + 1]);
                }
                if (!WasperPaintTexturePlacement.IsConvexQuad(
                        layer.Placement.Corners))
                    layer.Placement.ResetCornersFromBounds();
            }
            else
            {
                layer.Placement.ResetCornersFromBounds();
            }
            layer.Visible = state.Visible;
            layer.Opacity = double.IsFinite(state.Opacity)
                ? Math.Max(0.0, Math.Min(1.0, state.Opacity))
                : 1.0;
            layer.Revision++;
        }

        internal void SavePainterBitmap(Bitmap bitmap)
        {
            if (bitmap == null)
                return;
            try
            {
                string directory = DefaultMeshPaintDirectory();
                Directory.CreateDirectory(directory);
                using var dialog = new Eto.Forms.SaveFileDialog
                {
                    Title = "Save Painter Bitmap",
                    FileName = DefaultPainterStem() + "_atlas.png",
                    Directory = new Uri(directory)
                };
                dialog.Filters.Add(new Eto.Forms.FileFilter("PNG image (*.png)", ".png"));
                if (dialog.ShowDialog(_paintForm) != Eto.Forms.DialogResult.Ok)
                    return;
                WasperPaintPersistence.SaveBitmap(dialog.FileName, bitmap);
                ShowPainterMessage(
                    $"Bitmap saved:\n{dialog.FileName}",
                    Eto.Forms.MessageBoxType.Information);
            }
            catch (Exception exception)
            {
                ShowPainterMessage(
                    "The Painter bitmap could not be saved:\n" +
                    exception.Message,
                    Eto.Forms.MessageBoxType.Error);
            }
        }

        private string DefaultMeshPaintDirectory()
        {
            return WasperPaintPersistence.DefaultDirectory(OnPingDocument());
        }

        private string DefaultPainterStem()
        {
            return WasperPaintPersistence.DefaultStem("Gc20", InstanceGuid);
        }

        // Fully qualified Eto.Forms.SaveFileDialog/OpenFileDialog/DialogResult/MessageBox/
        // MessageBoxButtons/MessageBoxType above and below (System.Windows.Forms is still open in
        // this file, and several of these names exist in both namespaces). _paintForm is now
        // WasperEtoPaintForm (Eto.Forms.Form), which the WinForms dialogs cannot own -- same
        // CS0104-avoidance pattern the maintainer's build already confirmed for Sm01EtoManagerForm.
        // Eto.Forms.SaveFileDialog/OpenFileDialog use a Filters list of FileFilter and a Directory
        // Uri instead of WinForms' pipe-delimited Filter/InitialDirectory strings (matches the
        // in-repo Sm01 precedent); AddExtension/OverwritePrompt/CheckFileExists/Multiselect have no
        // confirmed Eto equivalent property and are dropped rather than guessed. Not build-verified.
        private void ShowPainterMessage(
            string message,
            Eto.Forms.MessageBoxType icon)
        {
            Eto.Forms.MessageBox.Show(
                _paintForm,
                message,
                "WASPer Shell Painter",
                Eto.Forms.MessageBoxButtons.OK,
                icon);
        }

        public override bool Read(GH_IReader reader)
        {
            // Rebuild the output topology BEFORE base.Read() restores saved wires, so the
            // optional debug outputs already exist when Grasshopper reconnects them (otherwise
            // saved wires to them are silently dropped on file open). Everything else below
            // that doesn't affect output wire restoration keeps running after base.Read(), as
            // before this fix.
            //
            // _visibleOutputsMask migration: files saved before per-output toggles existed only
            // have the legacy boolean ShowOutputsKey. Map "Show all outputs" = true to every bit
            // set, so old files keep showing everything they used to.
            if (reader.ItemExists(VisibleOutputsMaskKey))
                _visibleOutputsMask = reader.GetInt32(VisibleOutputsMaskKey);
            else if (reader.ItemExists(ShowOutputsKey) && reader.GetBoolean(ShowOutputsKey))
                _visibleOutputsMask = AllOutputsMask;
            else
                _visibleOutputsMask = 0;
            RebuildOutputs();

            bool result = base.Read(reader);
            EnsureFixedInputLayout();
            _constrainInfill = !reader.ItemExists(ConstrainInfillKey) ||
                               reader.GetBoolean(ConstrainInfillKey);
            _preview = !reader.ItemExists(PreviewKey) ||
                       reader.GetBoolean(PreviewKey);
            _live = !reader.ItemExists(LiveKey) ||
                    reader.GetBoolean(LiveKey);
            try
            {
                if (reader.ItemExists(PaintStateKey))
                {
                    _pendingState = WasperPaintPersistence.DeserializeEmbedded(
                        reader.GetString(PaintStateKey));
                }
                GH_IReader meshChunk = reader.FindChunk(ShellMeshKey);
                if (meshChunk != null)
                {
                    var meshGoo = new GH_Mesh();
                    if (meshGoo.Read(meshChunk) && meshGoo.Value != null)
                        _outputShellMesh = meshGoo.Value.DuplicateMesh();
                }
            }
            catch
            {
                _pendingState = null;
            }
            _signature = string.Empty;
            _topologySignature = string.Empty;
            _paintBranchLayout.Clear();
            return result;
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            StopPainting();
            if (_conduit != null)
                _conduit.Enabled = false;
            if (_paintForm != null && !_paintForm.IsClosed)
                _paintForm.Visible = false;
            foreach (WasperPaintTextureLayer layer in _textureLayers)
                layer.Dispose();
            base.RemovedFromDocument(document);
        }

        private void ScheduleSolution()
        {
            GH_Document document = OnPingDocument();
            if (document == null)
                ExpireSolution(true);
            else
                document.ScheduleSolution(1, _ => ExpireSolution(false));
        }

        private static DataTree<Plane> DuplicatePlanes(DataTree<Plane> source)
        {
            var result = new DataTree<Plane>();
            for (int b = 0; b < source.BranchCount; b++)
            {
                GH_Path path = source.Paths[b];
                foreach (Plane plane in source.Branches[b])
                    result.Add(plane, path);
            }
            return result;
        }

        private static Dictionary<string, List<Point3d>> BuildInfillPoints(WasperPrintPath path)
        {
            int commonPrefix = WasperGcodeTreeUtil.CommonPathPrefixLength(path.PtPlanes.Paths);
            var result = new Dictionary<string, List<Point3d>>();
            for (int b = 0; b < path.PtPlanes.BranchCount; b++)
            {
                GH_Path treePath = path.PtPlanes.Paths[b];
                if (WasperGcodeTreeUtil.PathRoleAt(path.PathRoles, treePath) != WasperPathRole.Infill)
                    continue;
                int layer = WasperGcodeTreeUtil.LayerFromPath(treePath, commonPrefix);
                string key = layer.ToString();
                if (!result.TryGetValue(key, out List<Point3d> points))
                    result[key] = points = new List<Point3d>();
                points.AddRange(path.PtPlanes.Branches[b].Select(plane => plane.Origin));
            }
            return result;
        }

        private static Curve CurveAt(WasperPrintPath path, GH_Path treePath)
        {
            if (path.SourceCurves != null && path.SourceCurves.PathExists(treePath))
            {
                IList<Curve> curves = path.SourceCurves.Branch(treePath);
                if (curves != null && curves.Count > 0 && curves[0] != null)
                    return curves[0];
            }
            IList<Plane> planes = path.PtPlanes.Branch(treePath);
            return planes == null || planes.Count < 2
                ? null
                : new PolylineCurve(planes.Select(plane => plane.Origin));
        }

        private static Vector3d LocalTangent(IList<Plane> planes, int index, int count)
        {
            if (planes == null || count < 2)
                return Vector3d.Unset;
            bool cyclic = count > 2;
            if (cyclic)
            {
                int previous = (index - 1 + count) % count;
                int next = (index + 1) % count;
                return planes[next].Origin - planes[previous].Origin;
            }
            if (index <= 0) return planes[1].Origin - planes[0].Origin;
            if (index >= count - 1) return planes[count - 1].Origin - planes[count - 2].Origin;
            return planes[index + 1].Origin - planes[index - 1].Origin;
        }

        private static bool IsDuplicateClosure(IList<Plane> planes, double tolerance)
        {
            return planes != null && planes.Count > 2 &&
                   planes[0].Origin.DistanceTo(planes[planes.Count - 1].Origin) <= tolerance;
        }

        private static string StackSignature(GH_Path path, int layerPosition)
        {
            var values = new List<int>();
            for (int i = 0; i < path.Length; i++)
                if (i != layerPosition)
                    values.Add(path[i]);
            return string.Join(";", values);
        }

        private static DataTree<Curve> BuildSourceCurves(
            DataTree<Plane> planes,
            DataTree<Curve> original,
            double tolerance)
        {
            var result = new DataTree<Curve>();
            for (int b = 0; b < planes.BranchCount; b++)
            {
                GH_Path path = planes.Paths[b];
                IList<Plane> branch = planes.Branches[b];
                if (branch == null || branch.Count < 2)
                    continue;
                var points = branch.Select(plane => plane.Origin).ToList();
                bool closed = original != null &&
                              original.PathExists(path) &&
                              original.Branch(path).Count > 0 &&
                              original.Branch(path)[0]?.IsClosed == true;
                if (closed && points[0].DistanceTo(points[points.Count - 1]) > tolerance)
                    points.Add(points[0]);
                result.Add(new PolylineCurve(points), path);
            }
            return result.BranchCount > 0 ? result : null;
        }

        private static bool HasDerivedData(WasperPrintPath path)
        {
            return path.PrintVol != null || path.PrintLoc != null || path.PrintGlob != null ||
                   path.RiskMaterial != null || path.DRatio != null || path.MotionPlan != null ||
                   path.KpiSegmentLength != null || path.KpiTimeMin.HasValue ||
                   path.KpiPathLength.HasValue || path.KpiVolume.HasValue;
        }

        private static int CountSelectedPoints(WasperPrintPath path, IList<int> targetRoles)
        {
            if (path?.PtPlanes == null) return 0;
            int count = 0;
            for (int b = 0; b < path.PtPlanes.BranchCount; b++)
            {
                GH_Path branchPath = path.PtPlanes.Paths[b];
                if (WasperGcodeTreeUtil.MatchesTargetRoles(path.PathRoles, branchPath, targetRoles))
                    count += path.PtPlanes.Branches[b].Count;
            }
            return count;
        }

        private static List<PaintBranchLayout> BuildPaintBranchLayout(
            WasperPrintPath path,
            IList<int> targetRoles)
        {
            var result = new List<PaintBranchLayout>();
            if (path?.PtPlanes == null)
                return result;

            int offset = 0;
            for (int b = 0; b < path.PtPlanes.BranchCount; b++)
            {
                GH_Path branchPath = path.PtPlanes.Paths[b];
                if (!WasperGcodeTreeUtil.MatchesTargetRoles(
                        path.PathRoles,
                        branchPath,
                        targetRoles))
                    continue;

                IList<Plane> branch = path.PtPlanes.Branches[b];
                result.Add(new PaintBranchLayout
                {
                    GeometryKey = ComputeBranchGeometryKey(branch),
                    PathKey = branchPath.ToString(),
                    Count = branch.Count,
                    Offset = offset
                });
                offset += branch.Count;
            }
            return result;
        }

        private static string ComputeBranchGeometryKey(IList<Plane> branch)
        {
            using var memory = new MemoryStream();
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
            {
                writer.Write(branch?.Count ?? 0);
                if (branch != null)
                {
                    foreach (Plane plane in branch)
                    {
                        writer.Write(plane.OriginX);
                        writer.Write(plane.OriginY);
                        writer.Write(plane.OriginZ);
                        writer.Write(plane.XAxis.X);
                        writer.Write(plane.XAxis.Y);
                        writer.Write(plane.XAxis.Z);
                        writer.Write(plane.YAxis.X);
                        writer.Write(plane.YAxis.Y);
                        writer.Write(plane.YAxis.Z);
                    }
                }
            }
            memory.Position = 0;
            using SHA256 sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(memory));
        }

        private static string ComputeSignature(IList<PaintBranchLayout> layout)
        {
            using var memory = new MemoryStream();
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
            {
                List<PaintBranchLayout> stable = layout
                    .OrderBy(item => item.GeometryKey, StringComparer.Ordinal)
                    .ThenBy(item => item.Count)
                    .ToList();
                writer.Write(stable.Count);
                foreach (PaintBranchLayout branch in stable)
                {
                    writer.Write(branch.GeometryKey);
                    writer.Write(branch.Count);
                }
            }
            memory.Position = 0;
            using SHA256 sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(memory));
        }

        private static string ComputeTopologySignature(IList<PaintBranchLayout> layout)
        {
            using var memory = new MemoryStream();
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
            {
                List<PaintBranchLayout> stable = layout
                    .OrderBy(item => item.GeometryKey, StringComparer.Ordinal)
                    .ThenBy(item => item.Count)
                    .ToList();
                writer.Write(stable.Count);
                foreach (PaintBranchLayout branch in stable)
                {
                    writer.Write(branch.GeometryKey);
                    writer.Write(branch.Count);
                }
            }
            memory.Position = 0;
            using SHA256 sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(memory));
        }

        private static bool SameLinearGeometryOrder(
            IList<PaintBranchLayout> previous,
            IList<PaintBranchLayout> current)
        {
            if (previous == null || current == null || previous.Count != current.Count)
                return false;
            for (int i = 0; i < previous.Count; i++)
            {
                if (previous[i].Count != current[i].Count ||
                    !string.Equals(
                        previous[i].GeometryKey,
                        current[i].GeometryKey,
                        StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static bool SamePathOrder(
            IList<PaintBranchLayout> previous,
            IList<PaintBranchLayout> current)
        {
            if (previous == null || current == null || previous.Count != current.Count)
                return false;
            for (int i = 0; i < previous.Count; i++)
            {
                if (!string.Equals(
                        previous[i].PathKey,
                        current[i].PathKey,
                        StringComparison.Ordinal))
                    return false;
            }
            return true;
        }

        private static bool TryRemapBranchValues(
            IList<PaintBranchLayout> previous,
            IList<PaintBranchLayout> current,
            double[] source,
            out double[] remapped)
        {
            remapped = null;
            if (previous == null || current == null || source == null ||
                source.Length != previous.Sum(item => item.Count))
                return false;

            var available = new Dictionary<string, Queue<PaintBranchLayout>>(
                StringComparer.Ordinal);
            foreach (PaintBranchLayout branch in previous)
            {
                string key = branch.GeometryKey + ":" + branch.Count;
                if (!available.TryGetValue(key, out Queue<PaintBranchLayout> matches))
                {
                    matches = new Queue<PaintBranchLayout>();
                    available[key] = matches;
                }
                matches.Enqueue(branch);
            }

            remapped = new double[current.Sum(item => item.Count)];
            foreach (PaintBranchLayout branch in current)
            {
                string key = branch.GeometryKey + ":" + branch.Count;
                if (!available.TryGetValue(key, out Queue<PaintBranchLayout> matches) ||
                    matches.Count == 0)
                {
                    remapped = null;
                    return false;
                }
                PaintBranchLayout sourceBranch = matches.Dequeue();
                Array.Copy(
                    source,
                    sourceBranch.Offset,
                    remapped,
                    branch.Offset,
                    branch.Count);
            }

            if (available.Values.Any(matches => matches.Count > 0))
            {
                remapped = null;
                return false;
            }
            return true;
        }

        private static List<PaintBranchLayout> BuildSavedBranchLayout(
            WasperPaintState state)
        {
            var result = new List<PaintBranchLayout>();
            if (state?.BranchGeometryKeys == null || state.BranchCounts == null ||
                state.BranchGeometryKeys.Length != state.BranchCounts.Length)
                return result;

            int offset = 0;
            for (int i = 0; i < state.BranchGeometryKeys.Length; i++)
            {
                int count = state.BranchCounts[i];
                if (string.IsNullOrEmpty(state.BranchGeometryKeys[i]) || count < 0)
                    return new List<PaintBranchLayout>();
                result.Add(new PaintBranchLayout
                {
                    GeometryKey = state.BranchGeometryKeys[i],
                    Count = count,
                    Offset = offset
                });
                offset += count;
            }
            return result;
        }

        private static double[] RestoreSavedBranchValues(
            WasperPaintState state,
            IList<PaintBranchLayout> current,
            double[] saved)
        {
            List<PaintBranchLayout> savedLayout = BuildSavedBranchLayout(state);
            return savedLayout.Count > 0 &&
                   TryRemapBranchValues(savedLayout, current, saved, out double[] remapped)
                ? remapped
                : null;
        }

        private static string ComputeLegacySignature(WasperPrintPath path)
        {
            using var memory = new MemoryStream();
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
            {
                writer.Write(path.PtPlanes.BranchCount);
                for (int b = 0; b < path.PtPlanes.BranchCount; b++)
                {
                    writer.Write(path.PtPlanes.Paths[b].ToString());
                    writer.Write(path.PtPlanes.Branches[b].Count);
                    foreach (Plane plane in path.PtPlanes.Branches[b])
                    {
                        writer.Write(plane.OriginX);
                        writer.Write(plane.OriginY);
                        writer.Write(plane.OriginZ);
                    }
                }
            }
            memory.Position = 0;
            using SHA256 sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(memory));
        }

        private static string ComputePathDependentSelectedSignature(
            WasperPrintPath path,
            IList<int> targetRoles)
        {
            using var memory = new MemoryStream();
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
            {
                int selectedBranches = 0;
                for (int b = 0; b < path.PtPlanes.BranchCount; b++)
                {
                    GH_Path branchPath = path.PtPlanes.Paths[b];
                    if (!WasperGcodeTreeUtil.MatchesTargetRoles(
                            path.PathRoles,
                            branchPath,
                            targetRoles))
                        continue;
                    selectedBranches++;
                    writer.Write(branchPath.ToString());
                    writer.Write(path.PtPlanes.Branches[b].Count);
                    foreach (Plane plane in path.PtPlanes.Branches[b])
                    {
                        writer.Write(plane.OriginX);
                        writer.Write(plane.OriginY);
                        writer.Write(plane.OriginZ);
                    }
                }
                writer.Write(selectedBranches);
            }
            memory.Position = 0;
            using SHA256 sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(memory));
        }

        private static string ComputePathDependentSelectedTopologySignature(
            WasperPrintPath path,
            IList<int> targetRoles)
        {
            using var memory = new MemoryStream();
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
            {
                int selectedBranches = 0;
                for (int b = 0; b < path.PtPlanes.BranchCount; b++)
                {
                    GH_Path branchPath = path.PtPlanes.Paths[b];
                    if (!WasperGcodeTreeUtil.MatchesTargetRoles(
                            path.PathRoles,
                            branchPath,
                            targetRoles))
                        continue;
                    selectedBranches++;
                    writer.Write(branchPath.ToString());
                    writer.Write(path.PtPlanes.Branches[b].Count);
                }
                writer.Write(selectedBranches);
            }
            memory.Position = 0;
            using SHA256 sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(memory));
        }

        private static string ComputeLegacyTopologySignature(WasperPrintPath path)
        {
            using var memory = new MemoryStream();
            using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
            {
                writer.Write(path.PtPlanes.BranchCount);
                for (int b = 0; b < path.PtPlanes.BranchCount; b++)
                {
                    writer.Write(path.PtPlanes.Paths[b].ToString());
                    writer.Write(path.PtPlanes.Branches[b].Count);
                }
            }
            memory.Position = 0;
            using SHA256 sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(memory));
        }

        private sealed class PaintBranchLayout
        {
            public string GeometryKey = string.Empty;
            public string PathKey = string.Empty;
            public int Count;
            public int Offset;
        }

        internal sealed class PaintLocation
        {
            public int Linear;
            public GH_Path Path;
            public int Item;
            public string Stack;
            public int Layer;
            public Point3d Point;
            public Plane Plane;
            public bool RoleEligible;
            public bool Eligible;
        }

        private sealed class PreviewRow
        {
            public string Stack;
            public int Layer;
            public List<PaintLocation> Locations;
        }

        private sealed class PreviewRowDescriptor
        {
            public PreviewRow Row;
            public Point3d Center;
            public double Length;
            public bool Closed;
        }

        private sealed class AtlasRow
        {
            public PreviewRow Source;
            public bool Closed;
            public int LogicalCount;
            public double[] Cumulative;
            public double Length;
            public double Shift;
            public double Height;
            private double _previewPhase;
            private bool _previewReversed;

            public static AtlasRow Create(
                PreviewRow source,
                double tolerance)
            {
                if (source?.Locations == null || source.Locations.Count < 2)
                    return null;
                bool closed = RowIsClosed(source.Locations, tolerance);
                int logicalCount = source.Locations.Count - (closed ? 1 : 0);
                if (logicalCount < 2)
                    return null;

                var cumulative = new double[logicalCount];
                for (int i = 1; i < logicalCount; i++)
                {
                    cumulative[i] = cumulative[i - 1] +
                                    source.Locations[i - 1].Point.DistanceTo(
                                        source.Locations[i].Point);
                }
                double length = cumulative[logicalCount - 1];
                if (closed)
                {
                    length += source.Locations[logicalCount - 1].Point.DistanceTo(
                        source.Locations[0].Point);
                }
                if (!double.IsFinite(length) || length <= tolerance)
                    return null;
                return new AtlasRow
                {
                    Source = source,
                    Closed = closed,
                    LogicalCount = logicalCount,
                    Cumulative = cumulative,
                    Length = length
                };
            }

            public double ArcAtItem(int item)
            {
                if (Closed && item >= LogicalCount)
                    return Length;
                return Cumulative[Math.Max(0, Math.Min(item, LogicalCount - 1))];
            }

            public Point3d PointAt(double u)
            {
                SegmentAt(u, out int start, out int end, out double t);
                return LerpPoint(
                    Source.Locations[start].Point,
                    Source.Locations[end].Point,
                    t);
            }

            public Vector3d TangentAt(double u)
            {
                SegmentAt(u, out int start, out int end, out _);
                return Source.Locations[end].Point - Source.Locations[start].Point;
            }

            public int SourceIndexAt(double u)
            {
                if (Closed && u >= 1.0 - 1e-12)
                {
                    return Source.Locations.Count > LogicalCount
                        ? Source.Locations[Source.Locations.Count - 1].Linear
                        : Source.Locations[0].Linear;
                }
                SegmentAt(u, out int start, out int end, out double t);
                return Source.Locations[t <= 0.5 ? start : end].Linear;
            }

            public int PreviewSourceIndexAt(double u)
            {
                SegmentAt(PreviewParameter(u), out int start, out int end, out double t);
                return Source.Locations[t <= 0.5 ? start : end].Linear;
            }

            public void AlignPreviewTo(AtlasRow previous)
            {
                if (previous == null)
                    return;
                if (!Closed || !previous.Closed)
                {
                    double same =
                        previous.PointAt(0.0).DistanceTo(PointAt(0.0)) +
                        previous.PointAt(1.0).DistanceTo(PointAt(1.0));
                    double reversed =
                        previous.PointAt(0.0).DistanceTo(PointAt(1.0)) +
                        previous.PointAt(1.0).DistanceTo(PointAt(0.0));
                    _previewPhase = reversed < same ? 1.0 : 0.0;
                    _previewReversed = reversed < same;
                    return;
                }

                double bestScore = double.PositiveInfinity;
                double bestPhase = 0.0;
                bool bestReversed = false;
                const int probes = 32;
                for (int candidate = 0; candidate < LogicalCount; candidate++)
                {
                    double phase = Cumulative[candidate] / Length;
                    for (int orientation = 0; orientation < 2; orientation++)
                    {
                        _previewPhase = phase;
                        _previewReversed = orientation == 1;
                        double score = 0.0;
                        for (int probe = 0; probe < probes; probe++)
                        {
                            double u = probe / (double)probes;
                            score += previous.PreviewPointAt(u).DistanceToSquared(
                                PreviewPointAt(u));
                        }
                        if (score >= bestScore)
                            continue;
                        bestScore = score;
                        bestPhase = phase;
                        bestReversed = _previewReversed;
                    }
                }
                _previewPhase = bestPhase;
                _previewReversed = bestReversed;
            }

            private Point3d PreviewPointAt(double u)
            {
                SegmentAt(PreviewParameter(u), out int start, out int end, out double t);
                return LerpPoint(
                    Source.Locations[start].Point,
                    Source.Locations[end].Point,
                    t);
            }

            private double PreviewParameter(double u)
            {
                double mapped = _previewReversed
                    ? _previewPhase - u
                    : _previewPhase + u;
                if (Closed)
                    return mapped - Math.Floor(mapped);
                return Math.Max(0.0, Math.Min(1.0, mapped));
            }

            private void SegmentAt(
                double u,
                out int start,
                out int end,
                out double t)
            {
                double clamped = Math.Max(0.0, Math.Min(1.0, u));
                double target = clamped * Length;
                if (!Closed && target >= Length)
                {
                    start = LogicalCount - 2;
                    end = LogicalCount - 1;
                    t = 1.0;
                    return;
                }

                for (int i = 0; i < LogicalCount - 1; i++)
                {
                    double segmentEnd = Cumulative[i + 1];
                    if (target > segmentEnd)
                        continue;
                    start = i;
                    end = i + 1;
                    double segmentLength = segmentEnd - Cumulative[i];
                    t = segmentLength <= 1e-12
                        ? 0.0
                        : (target - Cumulative[i]) / segmentLength;
                    return;
                }

                start = LogicalCount - 1;
                end = Closed ? 0 : LogicalCount - 1;
                double closingLength = Length - Cumulative[start];
                t = closingLength <= 1e-12
                    ? 0.0
                    : (target - Cumulative[start]) / closingLength;
            }
        }

        private sealed class ShellDirection
        {
            private readonly Curve _curve;
            private readonly Plane _plane;
            private readonly bool _materialInside;

            private ShellDirection(Curve curve, Plane plane, bool materialInside)
            {
                _curve = curve;
                _plane = plane;
                _materialInside = materialInside;
            }

            public static ShellDirection Create(
                WasperPrintPath path,
                GH_Path treePath,
                IDictionary<string, List<Point3d>> infillByLayer,
                double tolerance)
            {
                Curve curve = CurveAt(path, treePath);
                if (curve == null || !curve.IsClosed ||
                    !curve.TryGetPlane(out Plane plane, tolerance * 10.0))
                    return null;
                Curve projected = Curve.ProjectToPlane(curve, plane);
                if (projected == null || !projected.IsClosed)
                    return null;
                int prefix = WasperGcodeTreeUtil.CommonPathPrefixLength(path.PtPlanes.Paths);
                int layer = WasperGcodeTreeUtil.LayerFromPath(treePath, prefix);
                bool materialInside = true;
                if (infillByLayer.TryGetValue(layer.ToString(), out List<Point3d> infill))
                {
                    materialInside = infill.Any(point =>
                        projected.Contains(point, plane, tolerance) == PointContainment.Inside);
                }
                return new ShellDirection(projected, plane, materialInside);
            }

            public Vector3d InwardDirection(Point3d point, Vector3d lateral, double tolerance)
            {
                double probe = Math.Max(tolerance * 10.0, 1e-6);
                bool plusInside =
                    _curve.Contains(point + probe * lateral, _plane, tolerance) ==
                    PointContainment.Inside;
                return plusInside == _materialInside ? lateral : -lateral;
            }

            public Vector3d InwardFromTangent(
                Point3d point,
                Vector3d tangent,
                double tolerance)
            {
                Vector3d lateral = Vector3d.CrossProduct(_plane.Normal, tangent);
                if (!lateral.Unitize())
                    return Vector3d.Unset;
                return InwardDirection(point, lateral, tolerance);
            }
        }

        private static class PaintFuzzyIcon
        {
            public static Bitmap Bitmap => WasperPaintComponentIcons.PrintPath;
        }

    }

    internal sealed class PaintAttributes : GH_ComponentAttributes
    {
        private const int ButtonHeight = 18;
        private const int ButtonGap = 2;
        private RectangleF _painterButtonBounds;
        private RectangleF _meshButtonBounds;
        private int _pressedButton = -1;
        private wsp_Pp14_Fuzzy_Skin_from_Paint Component =>
            Owner as wsp_Pp14_Fuzzy_Skin_from_Paint;

        public PaintAttributes(wsp_Pp14_Fuzzy_Skin_from_Paint owner) : base(owner) { }

        protected override void Layout()
        {
            base.Layout();

            const int margin = 3;
            Rectangle bounds = GH_Convert.ToRectangle(Bounds);
            Rectangle button = bounds;
            button.X += margin;
            button.Width -= margin * 2;
            button.Y = bounds.Bottom;
            button.Height = ButtonHeight;
            Rectangle meshButton = button;
            meshButton.Y += ButtonHeight + ButtonGap;
            bounds.Height += ButtonHeight * 2 + ButtonGap + margin;

            Bounds = bounds;
            _painterButtonBounds = button;
            _meshButtonBounds = meshButton;
        }

        protected override void Render(
            GH_Canvas canvas,
            Graphics graphics,
            GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);
            if (channel != GH_CanvasChannel.Objects)
                return;

            Font prototype = GH_FontServer.StandardAdjusted;
            Font font = GH_FontServer.NewFont(
                prototype,
                6f / GH_GraphicsUtil.UiScale);
            using GH_Capsule painterButton = GH_Capsule.CreateTextCapsule(
                _painterButtonBounds,
                _painterButtonBounds,
                GH_Palette.Black,
                "Open Painter",
                font,
                3,
                _pressedButton == 0 ? 0 : 8);
            painterButton.Render(graphics, false, Owner.Locked, false);
            using GH_Capsule meshButton = GH_Capsule.CreateTextCapsule(
                _meshButtonBounds,
                _meshButtonBounds,
                GH_Palette.Black,
                "Update Mesh",
                font,
                3,
                _pressedButton == 1 ? 0 : 8);
            meshButton.Render(graphics, false, Owner.Locked, false);
            font.Dispose();
        }

        public override GH_ObjectResponse RespondToMouseDown(
            GH_Canvas sender,
            GH_CanvasMouseEvent e)
        {
            if (!Owner.Locked &&
                e.Button == MouseButtons.Left)
            {
                if (_painterButtonBounds.Contains(e.CanvasLocation))
                    _pressedButton = 0;
                else if (_meshButtonBounds.Contains(e.CanvasLocation))
                    _pressedButton = 1;
                if (_pressedButton >= 0)
                {
                    sender.Invalidate();
                    return GH_ObjectResponse.Capture;
                }
            }
            return base.RespondToMouseDown(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseUp(
            GH_Canvas sender,
            GH_CanvasMouseEvent e)
        {
            if (_pressedButton >= 0)
            {
                int releasedButton = _pressedButton;
                _pressedButton = -1;
                sender.Invalidate();
                if (releasedButton == 0 &&
                    _painterButtonBounds.Contains(e.CanvasLocation))
                    Component.TogglePainterForm();
                else if (releasedButton == 1 &&
                         _meshButtonBounds.Contains(e.CanvasLocation))
                    Component.UpdateShellMeshOutput();
                return GH_ObjectResponse.Release;
            }
            return base.RespondToMouseUp(sender, e);
        }
    }
}
