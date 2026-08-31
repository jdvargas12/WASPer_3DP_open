using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using Grasshopper;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP_Components._4_0_Print_Paths
{
    public sealed partial class wsp_Pp14_Fuzzy_Skin_from_Paint
    {
        internal bool TextureIsDistorted
            => _texturePlacement.IsDistorted;

        private void PrepareTextureSources(
            IList<object> rawTextures,
            out string error)
        {
            int previous = _activeTextureLayer;
            var errors = new List<string>();
            for (int layer = 0; layer < MaximumInputTextureLayers; layer++)
            {
                _activeTextureLayer = layer;
                object raw = rawTextures != null && layer < rawTextures.Count
                    ? rawTextures[layer]
                    : null;
                PrepareTextureSource(raw, out string layerError);
                if (!string.IsNullOrEmpty(layerError))
                    errors.Add($"Texture layer {layer + 1}: {layerError}");
                if (layer != previous)
                {
                    _textureEditMode = false;
                    _textureDistortMode = false;
                    _textureRotateMode = false;
                }
            }
            _activeTextureLayer = Math.Max(
                0,
                Math.Min(MaximumTextureLayers - 1, previous));
            error = string.Join(Environment.NewLine, errors);
        }

        private void PrepareTextureSource(object raw, out string error)
        {
            error = null;
            if (raw is IGH_Goo goo)
                raw = goo.ScriptVariable();
            if (raw == null)
            {
                ClearTextureSource();
                return;
            }

            try
            {
                if (!WasperPaintTextureSource.TryDescribe(
                        raw,
                        out string incomingKey,
                        out string incomingDescription,
                        out error))
                {
                    ClearTextureSource();
                    return;
                }

                _textureSource = raw;
                _textureSourceDescription = incomingDescription;
                if (string.Equals(
                        incomingKey,
                        _ignoredTextureSourceKey,
                        StringComparison.Ordinal))
                {
                    _textureBitmap?.Dispose();
                    _textureBitmap = null;
                    _textureSourceKey = incomingKey;
                    _textureEditMode = false;
                    _textureDistortMode = false;
                    _textureRotateMode = false;
                    _textureSourceDescription += " (overlay removed)";
                    return;
                }
                if (string.Equals(
                        incomingKey,
                        _textureSourceKey,
                        StringComparison.Ordinal) &&
                    _textureBitmap != null)
                    return;

                if (!WasperPaintTextureSource.TryCreateBitmap(
                        raw,
                        out object source,
                        out string sourceKey,
                        out string description,
                        out Bitmap bitmap,
                        out error))
                    return;

                _textureSource = source;
                _textureSourceDescription = description;
                _textureBitmap?.Dispose();
                _textureBitmap = bitmap;
                _textureSourceKey = sourceKey;
                _ignoredTextureSourceKey = string.Empty;
                _textureVisible = true;
                _textureEditMode = true;
                _textureDistortMode = false;
                _textureRotateMode = false;
                _texturePlacementInitialized = false;
                _textureRevision++;
            }
            catch (Exception exception)
            {
                error = $"Could not prepare the texture: {exception.Message}";
            }
        }

        private void ClearTextureSource()
        {
            if (_textureBitmap == null && _textureSource == null &&
                string.IsNullOrEmpty(_textureSourceKey))
                return;
            ActiveTexture.Clear();
        }

        internal void SelectTextureLayer(int layerIndex)
        {
            if (layerIndex < 0 || layerIndex >= _textureLayers.Count)
                return;
            EndTextureTransform();
            foreach (WasperPaintTextureLayer layer in _textureLayers)
            {
                layer.EditMode = false;
                layer.DistortMode = false;
                layer.RotateMode = false;
            }
            _activeTextureLayer = layerIndex;
            EnsureTexturePlacement();
            if (_textureBitmap != null)
            {
                _textureVisible = true;
                _textureEditMode = true;
                _textureDistortMode = false;
                _textureRotateMode = false;
                SetPainterTool(WasperPaintTool.None);
            }
            _textureRevision++;
            _paintForm?.RefreshCanvas();
        }

        internal void SelectTextTextureLayer(int textLayerIndex)
        {
            if (textLayerIndex < 0 || textLayerIndex >= MaximumTextTextureLayers)
                return;
            SelectTextureLayer(MaximumInputTextureLayers + textLayerIndex);
        }

        internal void ToggleTextTextureLayerVisibility(int textLayerIndex)
        {
            if (textLayerIndex < 0 || textLayerIndex >= MaximumTextTextureLayers)
                return;
            ToggleTextureLayerVisibility(MaximumInputTextureLayers + textLayerIndex);
        }

        internal void PreviewTextTexture(
            string text,
            string fontName,
            double fontSize) =>
            UpdateTextTexture(text, fontName, fontSize, false);

        internal void CommitTextTexture(
            string text,
            string fontName,
            double fontSize) =>
            UpdateTextTexture(text, fontName, fontSize, true);

        internal void DuplicateTextTextureLayer()
        {
            if (_activeTextureLayer < MaximumInputTextureLayers ||
                ActiveTexture.Bitmap == null)
                return;
            int destination = Enumerable.Range(
                    MaximumInputTextureLayers,
                    MaximumTextTextureLayers)
                .FirstOrDefault(index => _textureLayers[index].Bitmap == null);
            if (destination < MaximumInputTextureLayers)
            {
                AddRuntimeMessage(
                    Grasshopper.Kernel.GH_RuntimeMessageLevel.Remark,
                    "All five text texture slots are occupied.");
                return;
            }
            WasperPaintTextureLayer source = ActiveTexture;
            WasperPaintTextureLayer target = _textureLayers[destination];
            target.Clear();
            target.Bitmap = (Bitmap)source.Bitmap.Clone();
            target.Source = source.Source;
            target.SourceDescription = source.SourceDescription;
            target.SourceKey = source.SourceKey;
            target.Visible = source.Visible;
            target.IsText = true;
            target.TextContent = source.TextContent;
            target.FontName = source.FontName;
            target.FontSize = source.FontSize;
            target.TextCommitted = source.TextCommitted;
            target.Opacity = source.Opacity;
            target.Placement.Initialized = source.Placement.Initialized;
            target.Placement.MinX = source.Placement.MinX;
            target.Placement.MinY = source.Placement.MinY;
            target.Placement.MaxX = source.Placement.MaxX;
            target.Placement.MaxY = source.Placement.MaxY;
            Array.Copy(source.Placement.Corners, target.Placement.Corners, 4);
            target.Revision++;
            SelectTextureLayer(destination);
        }

        internal void RemoveTextTextureLayer()
        {
            if (_activeTextureLayer < MaximumInputTextureLayers)
                return;
            ActiveTexture.Clear();
            RefreshTextureReferencePreview();
            _paintForm?.RefreshCanvas();
        }

        internal void MoveTextTextureLayer(int direction)
        {
            if (_activeTextureLayer < MaximumInputTextureLayers || direction == 0)
                return;
            int destination = Math.Max(
                MaximumInputTextureLayers,
                Math.Min(MaximumTextureLayers - 1, _activeTextureLayer + Math.Sign(direction)));
            if (destination == _activeTextureLayer)
                return;
            WasperPaintTextureLayer moving = _textureLayers[_activeTextureLayer];
            _textureLayers[_activeTextureLayer] = _textureLayers[destination];
            _textureLayers[destination] = moving;
            _activeTextureLayer = destination;
            moving.Revision++;
            RefreshTextureReferencePreview();
            _paintForm?.RefreshCanvas();
        }

        private void UpdateTextTexture(
            string text,
            string fontName,
            double fontSize,
            bool committed)
        {
            if (_activeTextureLayer < MaximumInputTextureLayers)
                SelectTextTextureLayer(0);

            WasperPaintTextureLayer layer = ActiveTexture;
            string normalizedText = text ?? string.Empty;
            string normalizedFont = string.IsNullOrWhiteSpace(fontName)
                ? "Arial"
                : fontName.Trim();
            double normalizedSize = double.IsFinite(fontSize)
                ? Math.Max(0.01, fontSize)
                : 10.0;
            layer.Bitmap?.Dispose();
            layer.Bitmap = string.IsNullOrEmpty(normalizedText)
                ? null
                : RasterizeTextTexture(normalizedText, normalizedFont);
            layer.Source = normalizedText;
            layer.SourceDescription = string.IsNullOrEmpty(normalizedText)
                ? "empty text"
                : $"text: {normalizedText}";
            layer.SourceKey = string.IsNullOrEmpty(normalizedText)
                ? string.Empty
                : $"text|{normalizedFont}|{normalizedText}";
            layer.IgnoredSourceKey = string.Empty;
            layer.IsText = true;
            layer.TextContent = normalizedText;
            layer.FontName = normalizedFont;
            layer.FontSize = normalizedSize;
            layer.TextCommitted = committed && layer.Bitmap != null;
            layer.Visible = true;
            layer.EditMode = layer.Bitmap != null;
            layer.DistortMode = false;
            layer.RotateMode = false;
            layer.Revision++;

            if (layer.Bitmap != null)
                PlaceTextAtFontSize(layer, normalizedSize);
            RefreshTextureReferencePreview();
            _paintForm?.RefreshCanvas();
        }

        private void PlaceTextAtFontSize(
            WasperPaintTextureLayer layer,
            double fontSize)
        {
            if (layer?.Bitmap == null)
                return;

            Point2d center;
            Vector2d xAxis;
            Vector2d yAxis;
            if (layer.Placement.Initialized)
            {
                center = new Point2d(
                    layer.Placement.Corners.Average(point => point.X),
                    layer.Placement.Corners.Average(point => point.Y));
                xAxis = layer.Placement.Corners[1] - layer.Placement.Corners[0];
                yAxis = layer.Placement.Corners[3] - layer.Placement.Corners[0];
                if (!xAxis.Unitize())
                    xAxis = new Vector2d(1.0, 0.0);
                if (!yAxis.Unitize())
                    yAxis = new Vector2d(-xAxis.Y, xAxis.X);
                if (xAxis.X * yAxis.Y - xAxis.Y * yAxis.X < 0.0)
                    yAxis = -yAxis;
            }
            else
            {
                BoundingBox atlas = DisplayedAtlasBounds();
                center = new Point2d(
                    (atlas.Min.X + atlas.Max.X) * 0.5,
                    (atlas.Min.Y + atlas.Max.Y) * 0.5);
                xAxis = new Vector2d(1.0, 0.0);
                yAxis = new Vector2d(0.0, 1.0);
            }

            double height = Math.Max(0.01, fontSize);
            double width = height * layer.Bitmap.Width / layer.Bitmap.Height;
            Vector2d halfWidth = xAxis * (width * 0.5);
            Vector2d halfHeight = yAxis * (height * 0.5);
            layer.Placement.Corners[0] = center - halfWidth - halfHeight;
            layer.Placement.Corners[1] = center + halfWidth - halfHeight;
            layer.Placement.Corners[2] = center + halfWidth + halfHeight;
            layer.Placement.Corners[3] = center - halfWidth + halfHeight;
            layer.Placement.UpdateBoundsFromCorners();
            layer.Placement.Initialized = true;
        }

        private static Bitmap RasterizeTextTexture(string text, string fontName)
        {
            const float fontSize = 128f;
            Font font;
            try
            {
                font = new Font(fontName, fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            }
            catch
            {
                font = new Font("Arial", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
            }

            using (font)
            using (var measureBitmap = new Bitmap(1, 1))
            using (Graphics measure = Graphics.FromImage(measureBitmap))
            using (StringFormat format = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                SizeF measured = measure.MeasureString(text, font, 4096, format);
                int padding = Math.Max(8, (int)Math.Ceiling(fontSize * 0.12));
                int width = Math.Max(1, Math.Min(4096,
                    (int)Math.Ceiling(measured.Width) + padding * 2));
                int height = Math.Max(1, Math.Min(4096,
                    (int)Math.Ceiling(measured.Height) + padding * 2));
                var bitmap = new Bitmap(
                    width,
                    height,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using Graphics graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.Transparent);
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.DrawString(
                    text,
                    font,
                    Brushes.White,
                    new PointF(padding, padding),
                    format);
                return bitmap;
            }
        }

        internal void ToggleTextureVisibility()
        {
            List<WasperPaintTextureLayer> loadedLayers = _textureLayers
                .Where(layer => layer.Bitmap != null)
                .ToList();
            if (loadedLayers.Count == 0)
                return;

            bool showAll = !loadedLayers.Any(layer => layer.Visible);
            RecordUndoEvent(showAll ? "Show all texture layers" : "Hide all texture layers");
            EndTextureTransform();
            PushPainterUndo(CapturePainterUndoState());
            foreach (WasperPaintTextureLayer layer in loadedLayers)
            {
                layer.Visible = showAll;
                if (!showAll)
                {
                    layer.EditMode = false;
                    layer.DistortMode = false;
                    layer.RotateMode = false;
                }
                layer.Revision++;
            }
            _paintForm?.RefreshCanvas();
        }

        internal void ToggleTextureLayerVisibility(int layerIndex)
        {
            if (layerIndex < 0 || layerIndex >= _textureLayers.Count)
                return;
            WasperPaintTextureLayer layer = _textureLayers[layerIndex];
            if (layer.Bitmap == null)
                return;

            RecordUndoEvent($"Toggle texture layer {layerIndex + 1}");
            if (layerIndex == _activeTextureLayer)
                EndTextureTransform();
            PushPainterUndo(CapturePainterUndoState());
            layer.Visible = !layer.Visible;
            if (!layer.Visible)
            {
                layer.EditMode = false;
                layer.DistortMode = false;
                layer.RotateMode = false;
            }
            layer.Revision++;
            _paintForm?.RefreshCanvas();
        }

        internal void ToggleTextureEdit()
        {
            if (_textureBitmap == null)
                return;
            RecordUndoEvent("Toggle texture edit mode");
            EndTextureTransform();
            _textureVisible = true;
            _textureEditMode = !_textureEditMode;
            _textureDistortMode = false;
            _textureRotateMode = false;
            if (_textureEditMode)
                SetPainterTool(WasperPaintTool.None);
            _textureRevision++;
            _paintForm?.RefreshCanvas();
        }

        internal void ToggleTextureDistort()
        {
            if (_textureBitmap == null)
                return;
            RecordUndoEvent("Toggle texture distortion mode");
            EndTextureTransform();
            _textureVisible = true;
            _textureDistortMode = !_textureDistortMode;
            _textureEditMode = false;
            _textureRotateMode = false;
            if (_textureDistortMode)
                SetPainterTool(WasperPaintTool.None);
            _textureRevision++;
            _paintForm?.RefreshCanvas();
        }

        internal void ToggleTextureRotate()
        {
            if (_textureBitmap == null)
                return;
            RecordUndoEvent("Toggle texture rotation mode");
            EndTextureTransform();
            _textureVisible = true;
            _textureRotateMode = !_textureRotateMode;
            _textureEditMode = false;
            _textureDistortMode = false;
            if (_textureRotateMode)
                SetPainterTool(WasperPaintTool.None);
            _textureRevision++;
            _paintForm?.RefreshCanvas();
        }

        internal void ToggleAtlasFlipMap()
        {
            if (_painterMesh == null)
                return;
            RecordUndoEvent("Flip painter atlas");
            PushPainterUndo(CapturePainterUndoState());
            _atlasFlipMap = !_atlasFlipMap;
            _painterVisualRevision++;
            _paintForm?.RefreshCanvas();
        }

        internal void RotateAtlasClockwise()
        {
            if (_painterMesh == null)
                return;
            RecordUndoEvent("Rotate painter atlas 90 degrees");
            PushPainterUndo(CapturePainterUndoState());
            _atlasQuarterTurns = (_atlasQuarterTurns + 1) % 4;
            _painterVisualRevision++;
            _paintForm?.FitCanvas();
        }

        internal void FitTextureToAtlas()
        {
            FitTextureToAtlas(true);
        }

        private void FitTextureToAtlas(bool recordUndo)
        {
            if (_textureBitmap == null || _painterMesh == null)
                return;
            if (recordUndo)
                RecordUndoEvent("Fit texture overlay");
            EndTextureTransform();
            WasperPp13PainterUndoState before = recordUndo
                ? CapturePainterUndoState()
                : null;
            BoundingBox box = DisplayedAtlasBounds();
            double width = Math.Max(box.Max.X - box.Min.X, 1e-6);
            double height = Math.Max(box.Max.Y - box.Min.Y, 1e-6);
            double imageAspect = _textureBitmap.Width / (double)_textureBitmap.Height;
            double atlasAspect = width / height;
            if (imageAspect > atlasAspect)
            {
                double fittedHeight = width / imageAspect;
                _textureMinX = box.Min.X;
                _textureMaxX = box.Max.X;
                _textureMinY = (box.Min.Y + box.Max.Y - fittedHeight) * 0.5;
                _textureMaxY = _textureMinY + fittedHeight;
            }
            else
            {
                double fittedWidth = height * imageAspect;
                _textureMinY = box.Min.Y;
                _textureMaxY = box.Max.Y;
                _textureMinX = (box.Min.X + box.Max.X - fittedWidth) * 0.5;
                _textureMaxX = _textureMinX + fittedWidth;
            }
            ResetTextureCornersFromBounds();
            _texturePlacementInitialized = true;
            _textureVisible = true;
            _textureRevision++;
            if (recordUndo)
                PushPainterUndo(before);
            _paintForm?.RefreshCanvas();
        }

        private BoundingBox DisplayedAtlasBounds()
        {
            BoundingBox box = BoundingBox.Empty;
            foreach (Point3f vertex in _painterMesh.Vertices)
            {
                Point3d point = TransformAtlasPoint(vertex);
                Vector3d delta = point - _previewPlane.Origin;
                box.Union(new Point3d(
                    delta * _previewPlane.XAxis,
                    delta * _previewPlane.YAxis,
                    0.0));
            }
            return box;
        }

        private void EnsureTexturePlacement()
        {
            int active = _activeTextureLayer;
            for (int layer = 0; layer < _textureLayers.Count; layer++)
            {
                _activeTextureLayer = layer;
                if (_textureBitmap != null && !_texturePlacementInitialized)
                    FitTextureToAtlas(false);
            }
            _activeTextureLayer = active;
        }

        internal void MoveTextureCorner(
            int corner,
            Point3d atlasPoint,
            bool ortho)
        {
            if (!TextureHandlesVisible ||
                !_texturePlacementInitialized ||
                corner < 0 ||
                corner >= _textureCorners.Length * 2)
                return;
            Vector3d delta = atlasPoint - _previewPlane.Origin;
            double x = delta * _previewPlane.XAxis;
            double y = delta * _previewPlane.YAxis;
            if (TextureDistortMode)
            {
                Point2d[] start = _textureTransformStartCorners ??
                    (Point2d[])_textureCorners.Clone();
                var candidate = (Point2d[])_textureCorners.Clone();
                Point2d target = new Point2d(x, y);
                if (corner < _textureCorners.Length)
                {
                    if (ortho)
                    {
                        Vector2d movement = target - start[corner];
                        target = Math.Abs(movement.X) >= Math.Abs(movement.Y)
                            ? new Point2d(start[corner].X + movement.X, start[corner].Y)
                            : new Point2d(start[corner].X, start[corner].Y + movement.Y);
                    }
                    candidate[corner] = target;
                }
                else
                {
                    int edge = corner - _textureCorners.Length;
                    int next = (edge + 1) % _textureCorners.Length;
                    Point2d midpoint = new Point2d(
                        (start[edge].X + start[next].X) * 0.5,
                        (start[edge].Y + start[next].Y) * 0.5);
                    Vector2d movement = target - midpoint;
                    if (ortho)
                    {
                        Vector2d edgeDirection = start[next] - start[edge];
                        if (edgeDirection.Unitize())
                        {
                            var normal = new Vector2d(
                                -edgeDirection.Y,
                                edgeDirection.X);
                            movement = normal * (movement * normal);
                        }
                    }
                    candidate[edge] = start[edge] + movement;
                    candidate[next] = start[next] + movement;
                }
                if (!IsConvexTextureQuad(candidate))
                    return;
                for (int i = 0; i < _textureCorners.Length; i++)
                    _textureCorners[i] = candidate[i];
                UpdateTextureBoundsFromCorners();
                _textureRevision++;
                _paintForm?.RefreshCanvas();
                return;
            }

            if (TextureEditMode)
            {
                if (_textureTransformStartCorners == null ||
                    _textureTransformCorner != corner)
                {
                    _textureTransformStartCorners =
                        (Point2d[])_textureCorners.Clone();
                    _textureTransformCorner = corner;
                }
                int opposite = (corner + 2) % 4;
                Point2d anchor = _textureTransformStartCorners[opposite];
                Vector2d initial =
                    _textureTransformStartCorners[corner] - anchor;
                Vector2d current = new Point2d(x, y) - anchor;
                double initialLength = initial.Length;
                double scale = initialLength <= 1e-9
                    ? 1.0
                    : Math.Max(1e-4, current.Length / initialLength);
                for (int i = 0; i < _textureCorners.Length; i++)
                {
                    Vector2d fromAnchor =
                        _textureTransformStartCorners[i] - anchor;
                    _textureCorners[i] = anchor + fromAnchor * scale;
                }
                UpdateTextureBoundsFromCorners();
                _textureRevision++;
                _paintForm?.RefreshCanvas();
            }
        }

        internal void BeginTextureTransform(int corner)
        {
            if (TextureHandlesVisible)
            {
                RecordUndoEvent("Move texture corner");
                _textureTransformBefore = CapturePainterUndoState();
                _textureTransformStartCorners =
                    (Point2d[])_textureCorners.Clone();
                _textureTransformCorner = corner;
            }
        }

        internal void BeginTextureMove(Point3d atlasPoint)
        {
            if (!TextureHandlesVisible || !_texturePlacementInitialized)
                return;
            RecordUndoEvent("Move texture");
            _textureTransformBefore = CapturePainterUndoState();
            Vector3d delta = atlasPoint - _previewPlane.Origin;
            _textureTransformStartPoint = new Point2d(
                delta * _previewPlane.XAxis,
                delta * _previewPlane.YAxis);
            _textureTransformStartCorners =
                (Point2d[])_textureCorners.Clone();
            _textureTransformCorner = -2;
        }

        internal void MoveTexture(Point3d atlasPoint)
        {
            if (_textureTransformCorner != -2 ||
                _textureTransformStartCorners == null)
                return;
            Vector3d delta = atlasPoint - _previewPlane.Origin;
            Point2d current = new Point2d(
                delta * _previewPlane.XAxis,
                delta * _previewPlane.YAxis);
            if (TextureRotateMode)
            {
                Point2d center = new Point2d(
                    _textureTransformStartCorners.Average(corner => corner.X),
                    _textureTransformStartCorners.Average(corner => corner.Y));
                Vector2d startVector = _textureTransformStartPoint - center;
                Vector2d currentVector = current - center;
                if (startVector.Length <= 1e-9 || currentVector.Length <= 1e-9)
                    return;
                double angle = Math.Atan2(currentVector.Y, currentVector.X) -
                               Math.Atan2(startVector.Y, startVector.X);
                double cosine = Math.Cos(angle);
                double sine = Math.Sin(angle);
                for (int corner = 0; corner < _textureCorners.Length; corner++)
                {
                    Vector2d offset = _textureTransformStartCorners[corner] - center;
                    _textureCorners[corner] = new Point2d(
                        center.X + offset.X * cosine - offset.Y * sine,
                        center.Y + offset.X * sine + offset.Y * cosine);
                }
            }
            else
            {
                Vector2d movement = current - _textureTransformStartPoint;
                for (int corner = 0; corner < _textureCorners.Length; corner++)
                    _textureCorners[corner] =
                        _textureTransformStartCorners[corner] + movement;
            }
            UpdateTextureBoundsFromCorners();
            _textureRevision++;
            _paintForm?.RefreshCanvas();
        }

        internal void EndTextureTransform()
        {
            _texturePlacement.EndTransform();
            WasperPp13PainterUndoState before = _textureTransformBefore;
            _textureTransformBefore = null;
            int layerIndex = before?.ActiveTextureLayer ?? -1;
            if (layerIndex >= 0 && layerIndex < _textureLayers.Count &&
                !TexturePlacementMatches(
                    before.TextureLayers[layerIndex],
                    _textureLayers[layerIndex]))
            {
                PushPainterUndo(before);
            }
        }

        internal void ApplyTextureToPaint()
        {
            if (_textureBitmap == null ||
                (ActiveTexture.IsText && !ActiveTexture.TextCommitted) ||
                !_texturePlacementInitialized ||
                _values.Length == 0)
                return;

            RecordUndoEvent("Apply texture to paint");
            double[] before = (double[])_values.Clone();
            List<KeyValuePair<int, Point2d>> samples = BuildTextureSamples();
            int changed = WasperPaintTextureSampler.ApplyToValues(
                _textureBitmap,
                _texturePlacement,
                samples,
                index => index >= 0 &&
                         index < _locations.Count &&
                         _locations[index].Eligible,
                _domain,
                _values);

            if (changed == 0)
            {
                AddRuntimeMessage(
                    Grasshopper.Kernel.GH_RuntimeMessageLevel.Remark,
                    "The texture did not overlap any eligible atlas samples.");
                return;
            }

            PushPaintUndo(before);
            _painterVisualRevision++;
            PaintStateChanged();
            UpdateConduit();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal void ApplyTextureCompositeToPaint()
        {
            if (!_textureLayers.Any(layer =>
                    layer.Bitmap != null && layer.Visible &&
                    (!layer.IsText || layer.TextCommitted) &&
                    layer.Placement.Initialized) || _values.Length == 0)
                return;
            RecordUndoEvent("Apply texture composite to paint");
            double[] before = (double[])_values.Clone();
            int changed = WasperPaintTextureSampler.ApplyCompositeToValues(
                _textureLayers,
                BuildTextureSamples(),
                index => index >= 0 &&
                         index < _locations.Count &&
                         _locations[index].Eligible,
                _domain,
                _values);
            if (changed == 0)
            {
                AddRuntimeMessage(
                    Grasshopper.Kernel.GH_RuntimeMessageLevel.Remark,
                    "The visible texture composite did not overlap any eligible atlas samples.");
                return;
            }
            PushPaintUndo(before);
            _painterVisualRevision++;
            PaintStateChanged();
            UpdateConduit();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        private List<KeyValuePair<int, Point2d>> BuildTextureSamples()
        {
            var samples = new List<KeyValuePair<int, Point2d>>(_atlasPoints.Count);
            foreach (var pair in _atlasPoints)
            {
                Point3d samplePoint = TransformAtlasPoint(pair.Value);
                Vector3d delta = samplePoint - _previewPlane.Origin;
                samples.Add(new KeyValuePair<int, Point2d>(
                    pair.Key,
                    new Point2d(
                        delta * _previewPlane.XAxis,
                        delta * _previewPlane.YAxis)));
            }
            return samples;
        }

        private void EnsureTexturePreviewColors()
        {
            int signature = TexturePreviewSignature();
            if (signature == _texturePreviewSignature &&
                _texturePreviewColors.Length == _locations.Count)
                return;

            _texturePreviewSignature = signature;
            _texturePreviewColors = new Color[_locations.Count];
            if (!_textureLayers.Any(layer =>
                    layer.Bitmap != null && layer.Visible &&
                    (!layer.IsText || layer.TextCommitted) &&
                    layer.Placement.Initialized))
                return;

            foreach (KeyValuePair<int, Point2d> pair in BuildTextureSamples())
            {
                if (pair.Key < 0 || pair.Key >= _texturePreviewColors.Length)
                    continue;
                if (WasperPaintTextureSampler.TrySampleCompositeColor(
                        _textureLayers,
                        pair.Value,
                        out Color color))
                    _texturePreviewColors[pair.Key] = color;
            }
        }

        private int TexturePreviewSignature()
        {
            unchecked
            {
                int signature = 17;
                signature = signature * 31 + _atlasFlipMap.GetHashCode();
                signature = signature * 31 + _atlasQuarterTurns;
                signature = signature * 31 + _atlasPoints.Count;
                signature = signature * 31 +
                    (_painterMesh == null ? 0 : RuntimeHelpers.GetHashCode(_painterMesh));
                foreach (WasperPaintTextureLayer layer in _textureLayers)
                {
                    signature = signature * 31 + layer.Revision;
                    signature = signature * 31 + layer.Visible.GetHashCode();
                    signature = signature * 31 + layer.Opacity.GetHashCode();
                    signature = signature * 31 +
                        (layer.Bitmap == null ? 0 : RuntimeHelpers.GetHashCode(layer.Bitmap));
                }
                return signature;
            }
        }

        internal void RefreshTextureReferencePreview()
        {
            _texturePreviewSignature = int.MinValue;
            if (_conduit == null)
            {
                UpdateConduit();
            }
            else
            {
                ApplyReferencePreviewColors(
                    _previewMesh,
                    _previewSourceIndices);
                _conduit.PreviewMesh = _previewMesh;
                _paintForm?.RefreshCanvas();
            }
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        internal Point3d MirrorAtlasPoint(Point3d point)
        {
            if (_painterMesh == null || !_painterMesh.IsValid)
                return point;
            BoundingBox box = _painterMesh.GetBoundingBox(_previewPlane);
            Vector3d delta = point - _previewPlane.Origin;
            double x = delta * _previewPlane.XAxis;
            double y = delta * _previewPlane.YAxis;
            double mirroredX = box.Min.X + box.Max.X - x;
            return _previewPlane.Origin +
                   _previewPlane.XAxis * mirroredX +
                   _previewPlane.YAxis * y;
        }

        internal Point3d TransformAtlasPoint(Point3d point)
        {
            return WasperPaintAtlasTransform.Transform(
                point,
                _previewPlane,
                _painterMesh,
                _atlasFlipMap,
                _atlasQuarterTurns);
        }

        internal Point3d InverseTransformAtlasPoint(Point3d point)
        {
            return WasperPaintAtlasTransform.Inverse(
                point,
                _previewPlane,
                _painterMesh,
                _atlasFlipMap,
                _atlasQuarterTurns);
        }

        internal double AtlasMirrorCenterX
        {
            get
            {
                if (_painterMesh == null || !_painterMesh.IsValid)
                    return 0.0;
                BoundingBox box = _painterMesh.GetBoundingBox(_previewPlane);
                return (box.Min.X + box.Max.X) * 0.5;
            }
        }

        internal void RemoveTextureOverlay()
        {
            if (_textureBitmap == null)
                return;
            RecordUndoEvent("Remove texture overlay");
            EndTextureTransform();
            _ignoredTextureSourceKey = _textureSourceKey;
            _textureBitmap.Dispose();
            _textureBitmap = null;
            _textureVisible = false;
            _textureEditMode = false;
            _textureDistortMode = false;
            _textureRotateMode = false;
            _textureSourceDescription += " (overlay removed)";
            _textureRevision++;
            _paintForm?.RefreshCanvas();
        }

        private void ResetTextureCornersFromBounds()
        {
            _texturePlacement.ResetCornersFromBounds();
        }

        private void UpdateTextureBoundsFromCorners()
        {
            _texturePlacement.UpdateBoundsFromCorners();
        }

        private static bool IsConvexTextureQuad(IList<Point2d> corners)
        {
            return corners is Point2d[] array &&
                   WasperPaintTexturePlacement.IsConvexQuad(array);
        }

        private bool TryTextureCoordinates(
            Point2d target,
            out double u,
            out double v)
        {
            return _texturePlacement.TryCoordinates(target, out u, out v);
        }

        private WasperPp13PainterUndoState CapturePainterUndoState(
            double[] valuesOverride = null)
        {
            return new WasperPp13PainterUndoState
            {
                Values = valuesOverride == null
                    ? (double[])_values.Clone()
                    : (double[])valuesOverride.Clone(),
                ActiveTextureLayer = _activeTextureLayer,
                AtlasFlipMap = _atlasFlipMap,
                AtlasQuarterTurns = _atlasQuarterTurns,
                TextureLayers = _textureLayers
                    .Select(layer => new WasperPp13TextureUndoState
                    {
                        SourceKey = layer.SourceKey,
                        Initialized = layer.Placement.Initialized,
                        MinX = layer.Placement.MinX,
                        MinY = layer.Placement.MinY,
                        MaxX = layer.Placement.MaxX,
                        MaxY = layer.Placement.MaxY,
                        Corners = (Point2d[])layer.Placement.Corners.Clone(),
                        Visible = layer.Visible,
                        EditMode = layer.EditMode,
                        DistortMode = layer.DistortMode,
                        RotateMode = layer.RotateMode,
                        Opacity = layer.Opacity
                    })
                    .ToArray()
            };
        }

        private void PushPainterUndo(WasperPp13PainterUndoState state)
        {
            if (state == null)
                return;
            _painterUndo.Push(state);
            _painterRedo.Clear();
        }

        private void ClearPainterHistory()
        {
            _painterUndo.Clear();
            _painterRedo.Clear();
            _textureTransformBefore = null;
            _paintValues.ClearHistory();
        }

        private void RestorePainterUndoState(WasperPp13PainterUndoState state)
        {
            if (state == null)
                return;
            bool paintChanged = !WasperPaintUtilities.ValuesEqual(
                _values,
                state.Values);
            if (state.Values != null && state.Values.Length == _values.Length)
                _values = (double[])state.Values.Clone();
            _atlasFlipMap = state.AtlasFlipMap;
            _atlasQuarterTurns = state.AtlasQuarterTurns;
            _activeTextureLayer = Math.Max(
                0,
                Math.Min(_textureLayers.Count - 1, state.ActiveTextureLayer));
            for (int index = 0;
                 index < Math.Min(_textureLayers.Count, state.TextureLayers?.Length ?? 0);
                 index++)
            {
                WasperPaintTextureLayer layer = _textureLayers[index];
                WasperPp13TextureUndoState saved = state.TextureLayers[index];
                if (!string.Equals(layer.SourceKey, saved.SourceKey, StringComparison.Ordinal))
                    continue;
                layer.Placement.Initialized = saved.Initialized;
                layer.Placement.MinX = saved.MinX;
                layer.Placement.MinY = saved.MinY;
                layer.Placement.MaxX = saved.MaxX;
                layer.Placement.MaxY = saved.MaxY;
                if (saved.Corners != null && saved.Corners.Length == 4)
                    Array.Copy(saved.Corners, layer.Placement.Corners, 4);
                layer.Placement.EndTransform();
                layer.Visible = saved.Visible;
                layer.EditMode = saved.EditMode;
                layer.DistortMode = saved.DistortMode;
                layer.RotateMode = saved.RotateMode;
                layer.Opacity = saved.Opacity;
                layer.Revision++;
            }
            _textureTransformBefore = null;
            _painterVisualRevision++;
            if (paintChanged)
                PaintStateChanged();
            UpdateConduit();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        private static bool TexturePlacementMatches(
            WasperPp13TextureUndoState before,
            WasperPaintTextureLayer current)
        {
            if (before == null || current == null ||
                before.Initialized != current.Placement.Initialized)
                return false;
            for (int corner = 0; corner < 4; corner++)
            {
                if (before.Corners[corner].DistanceTo(
                        current.Placement.Corners[corner]) > 1e-12)
                    return false;
            }
            return true;
        }
    }

    internal sealed class WasperPp13PainterUndoState
    {
        internal double[] Values;
        internal int ActiveTextureLayer;
        internal bool AtlasFlipMap;
        internal int AtlasQuarterTurns;
        internal WasperPp13TextureUndoState[] TextureLayers;
    }

    internal sealed class WasperPp13TextureUndoState
    {
        internal string SourceKey;
        internal bool Initialized;
        internal double MinX;
        internal double MinY;
        internal double MaxX;
        internal double MaxY;
        internal Point2d[] Corners;
        internal bool Visible;
        internal bool EditMode;
        internal bool DistortMode;
        internal bool RotateMode;
        internal double Opacity;
    }
}
