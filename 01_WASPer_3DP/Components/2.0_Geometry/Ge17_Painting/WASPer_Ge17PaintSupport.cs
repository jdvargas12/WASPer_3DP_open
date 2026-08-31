using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

using GH_IO.Serialization;

using Rhino;
using Rhino.Geometry;

using WASPer_3DP.Painting;

namespace WASPer_3DP_Components._2_0_Geometry
{
    public sealed partial class wsp_Ge17_Paint_Mesh_Field
    {
        public override bool Write(GH_IWriter writer)
        {
            if (_values.Length > 0)
            {
                try
                {
                    writer.SetString(
                        PaintStateKey,
                        WasperPaintPersistence.SerializeEmbedded(CapturePaintState()));
                }
                catch (Exception exception)
                {
                    AddRuntimeMessage(
                        Grasshopper.Kernel.GH_RuntimeMessageLevel.Warning,
                        "Ge17 paint state could not be embedded: " + exception.Message);
                }
            }
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            bool result = base.Read(reader);
            if (reader.ItemExists(PaintStateKey))
            {
                try
                {
                    _pendingState = WasperPaintPersistence.DeserializeEmbedded(
                        reader.GetString(PaintStateKey));
                }
                catch
                {
                    _pendingState = null;
                }
            }
            return result;
        }

        private WasperPaintState CapturePaintState()
        {
            WasperPaintTextureLayer first = _textureLayers[0];
            return new WasperPaintState
            {
                Version = 2,
                OwnerInstanceGuid = InstanceGuid.ToString("D"),
                SavedUtc = DateTime.UtcNow,
                Signature = _meshSignature,
                TopologySignature = _topologySignature,
                Values = (double[])_values.Clone(),
                AppliedValues = (double[])_appliedValues.Clone(),
                Preview = _preview,
                Radius = _radius,
                BrushStrength = _strength,
                SmoothStrength = _smoothStrength,
                Falloff = _falloff,
                AtlasFlipMap = _atlasFlipMap,
                AtlasQuarterTurns = _atlasQuarterTurns,
                TextureSourceKey = first.SourceKey,
                TexturePlacementInitialized = first.Placement.Initialized,
                TextureMinX = first.Placement.MinX,
                TextureMinY = first.Placement.MinY,
                TextureMaxX = first.Placement.MaxX,
                TextureMaxY = first.Placement.MaxY,
                TextureCorners = FlattenCorners(first.Placement.Corners),
                TextureVisible = first.Visible,
                ActiveTextureLayer = _activeTextureLayer,
                TextureLayers = _textureLayers.Select(layer =>
                    new WasperPaintTextureLayerState
                    {
                        SourceKey = layer.SourceKey,
                        PlacementInitialized = layer.Placement.Initialized,
                        MinX = layer.Placement.MinX,
                        MinY = layer.Placement.MinY,
                        MaxX = layer.Placement.MaxX,
                        MaxY = layer.Placement.MaxY,
                        Corners = FlattenCorners(layer.Placement.Corners),
                        Visible = layer.Visible,
                        Opacity = layer.Opacity
                    }).ToArray()
            };
        }

        private static double[] FlattenCorners(Point2d[] corners)
        {
            return corners?.SelectMany(point => new[] { point.X, point.Y }).ToArray();
        }

        private void RestoreTextureState(WasperPaintState state)
        {
            if (state == null)
                return;
            WasperPaintTextureLayerState[] savedLayers = state.TextureLayers;
            if (savedLayers == null || savedLayers.Length == 0)
            {
                savedLayers = new[]
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
                 index < Math.Min(_textureLayers.Count, savedLayers.Length);
                 index++)
            {
                WasperPaintTextureLayer layer = _textureLayers[index];
                WasperPaintTextureLayerState saved = savedLayers[index];
                if (saved == null ||
                    !string.Equals(layer.SourceKey, saved.SourceKey, StringComparison.Ordinal))
                    continue;
                layer.Placement.MinX = saved.MinX;
                layer.Placement.MinY = saved.MinY;
                layer.Placement.MaxX = saved.MaxX;
                layer.Placement.MaxY = saved.MaxY;
                layer.Placement.Initialized = saved.PlacementInitialized;
                if (saved.Corners?.Length == 8)
                {
                    for (int corner = 0; corner < 4; corner++)
                    {
                        layer.Placement.Corners[corner] = new Point2d(
                            saved.Corners[corner * 2],
                            saved.Corners[corner * 2 + 1]);
                    }
                }
                else
                {
                    layer.Placement.ResetCornersFromBounds();
                }
                layer.Visible = saved.Visible;
                layer.Opacity = double.IsFinite(saved.Opacity)
                    ? Math.Max(0.0, Math.Min(1.0, saved.Opacity))
                    : 1.0;
                layer.Revision++;
            }
            _activeTextureLayer = Math.Max(
                0,
                Math.Min(_textureLayers.Count - 1, state.ActiveTextureLayer));
        }

        // Fully qualified Eto.Forms.SaveFileDialog/OpenFileDialog/DialogResult/MessageBox/
        // MessageBoxButtons below (System.Windows.Forms is still open in this file). Both
        // namespaces declare types with these names, and _paintForm is now WasperEtoPaintForm
        // (Eto.Forms.Form), which the WinForms dialogs cannot own -- same CS0104-avoidance pattern
        // the maintainer's build already confirmed for Sm01EtoManagerForm (see
        // WASPER_CROSS_PLATFORM_ETO_UI_MIGRATION_PLAN.md, Workstream 1). Eto.Forms.SaveFileDialog/
        // OpenFileDialog use a Filters list of FileFilter and a Directory Uri instead of WinForms'
        // pipe-delimited Filter/InitialDirectory strings (matches the in-repo Sm01 precedent);
        // AddExtension/OverwritePrompt/CheckFileExists/Multiselect have no confirmed Eto equivalent
        // property and are dropped rather than guessed -- native Open/Save pickers handle existence
        // checks and overwrite prompts by default. Not build-verified.
        internal void SavePainterSession()
        {
            try
            {
                string directory = WasperPaintPersistence.DefaultDirectory(OnPingDocument());
                Directory.CreateDirectory(directory);
                using var dialog = new Eto.Forms.SaveFileDialog
                {
                    Title = "Save WASPer Mesh Painter Session",
                    FileName = WasperPaintPersistence.DefaultStem(
                        "Ge17",
                        InstanceGuid) + ".wasperpaint.json.gz",
                    Directory = new Uri(directory)
                };
                dialog.Filters.Add(new Eto.Forms.FileFilter(
                    "WASPer Painter session (*.wasperpaint.json.gz)", ".wasperpaint.json.gz"));
                if (dialog.ShowDialog(_paintForm) != Eto.Forms.DialogResult.Ok)
                    return;
                WasperPaintPersistence.SaveSession(dialog.FileName, CapturePaintState());
            }
            catch (Exception exception)
            {
                ShowPainterError("The session could not be saved", exception);
            }
        }

        internal void LoadPainterSession()
        {
            try
            {
                string directory = WasperPaintPersistence.DefaultDirectory(OnPingDocument());
                Directory.CreateDirectory(directory);
                using var dialog = new Eto.Forms.OpenFileDialog
                {
                    Title = "Load WASPer Mesh Painter Session",
                    Directory = new Uri(directory)
                };
                dialog.Filters.Add(new Eto.Forms.FileFilter(
                    "WASPer Painter session (*.wasperpaint.json.gz)", ".wasperpaint.json.gz"));
                dialog.Filters.Add(new Eto.Forms.FileFilter(
                    "Compressed JSON (*.json.gz)", ".json.gz"));
                if (dialog.ShowDialog(_paintForm) != Eto.Forms.DialogResult.Ok)
                    return;
                WasperPaintState state = WasperPaintPersistence.LoadSession(dialog.FileName);
                if (state?.Values == null || state.Values.Length != _values.Length ||
                    !string.Equals(
                        state.TopologySignature,
                        _topologySignature,
                        StringComparison.Ordinal))
                {
                    Eto.Forms.MessageBox.Show(
                        _paintForm,
                        "This session does not match the current mesh topology.",
                        "WASPer Mesh Painter",
                        Eto.Forms.MessageBoxButtons.OK,
                        Eto.Forms.MessageBoxType.Warning);
                    return;
                }
                PushUndo(CaptureSnapshot());
                _values = (double[])state.Values.Clone();
                _appliedValues = state.AppliedValues?.Length == _values.Length
                    ? (double[])state.AppliedValues.Clone()
                    : (double[])_values.Clone();
                _preview = state.Preview;
                _atlasFlipMap = state.AtlasFlipMap;
                _atlasQuarterTurns = ((state.AtlasQuarterTurns % 4) + 4) % 4;
                RestoreTextureState(state);
                RebuildDisplayMeshes();
                UpdateConduit();
                ScheduleSolution();
            }
            catch (Exception exception)
            {
                ShowPainterError("The session could not be loaded", exception);
            }
        }

        internal void SavePainterBitmap(Bitmap bitmap)
        {
            if (bitmap == null)
                return;
            try
            {
                string directory = WasperPaintPersistence.DefaultDirectory(OnPingDocument());
                Directory.CreateDirectory(directory);
                using var dialog = new Eto.Forms.SaveFileDialog
                {
                    Title = "Save Mesh Painter Bitmap",
                    FileName = WasperPaintPersistence.DefaultStem(
                        "Ge17",
                        InstanceGuid) + "_atlas.png",
                    Directory = new Uri(directory)
                };
                dialog.Filters.Add(new Eto.Forms.FileFilter("PNG image (*.png)", ".png"));
                if (dialog.ShowDialog(_paintForm) != Eto.Forms.DialogResult.Ok)
                    return;
                WasperPaintPersistence.SaveBitmap(dialog.FileName, bitmap);
            }
            catch (Exception exception)
            {
                ShowPainterError("The atlas bitmap could not be saved", exception);
            }
        }

        private void ShowPainterError(string message, Exception exception)
        {
            Eto.Forms.MessageBox.Show(
                _paintForm,
                message + ":\n" + exception.Message,
                "WASPer Mesh Painter",
                Eto.Forms.MessageBoxButtons.OK,
                Eto.Forms.MessageBoxType.Error);
        }
    }
}
