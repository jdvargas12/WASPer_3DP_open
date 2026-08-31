using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;

using WASPer_3DP.Painting;

namespace WASPer_3DP_Components._2_0_Geometry
{
    public sealed partial class wsp_Ge17_Paint_Mesh_Field
    {
        internal double AtlasMirrorCenterX
        {
            get
            {
                if (_painterMesh == null)
                    return 0.0;
                BoundingBox box = _painterMesh.GetBoundingBox(Plane.WorldXY);
                return (box.Min.X + box.Max.X) * 0.5;
            }
        }

        private void PrepareTextureSources(IList<object> rawTextures, out string error)
        {
            int previous = _activeTextureLayer;
            var errors = new List<string>();
            for (int layerIndex = 0; layerIndex < MaximumTextureLayers; layerIndex++)
            {
                WasperPaintTextureLayer layer = _textureLayers[layerIndex];
                object raw = rawTextures != null && layerIndex < rawTextures.Count
                    ? rawTextures[layerIndex]
                    : null;
                if (raw is IGH_Goo goo)
                    raw = goo.ScriptVariable();
                if (raw == null)
                {
                    if (layer.Bitmap != null || layer.Source != null)
                        layer.Clear();
                    continue;
                }
                if (!WasperPaintTextureSource.TryDescribe(
                        raw,
                        out string key,
                        out string description,
                        out string describeError))
                {
                    errors.Add($"Texture layer {layerIndex + 1}: {describeError}");
                    continue;
                }
                if (string.Equals(key, layer.IgnoredSourceKey, StringComparison.Ordinal))
                    continue;
                if (string.Equals(key, layer.SourceKey, StringComparison.Ordinal) &&
                    layer.Bitmap != null)
                    continue;
                if (!WasperPaintTextureSource.TryCreateBitmap(
                        raw,
                        out object source,
                        out string sourceKey,
                        out string sourceDescription,
                        out Bitmap bitmap,
                        out string createError))
                {
                    errors.Add($"Texture layer {layerIndex + 1}: {createError}");
                    continue;
                }
                layer.Bitmap?.Dispose();
                layer.Source = source;
                layer.SourceKey = sourceKey;
                layer.SourceDescription = sourceDescription ?? description;
                layer.Bitmap = bitmap;
                layer.IgnoredSourceKey = string.Empty;
                layer.Visible = true;
                layer.EditMode = layerIndex == previous;
                layer.DistortMode = false;
                layer.RotateMode = false;
                layer.Placement.Initialized = false;
                layer.Revision++;
            }
            _activeTextureLayer = Math.Max(
                0,
                Math.Min(_textureLayers.Count - 1, previous));
            error = string.Join(Environment.NewLine, errors);
        }

        private void EnsureTexturePlacement()
        {
            if (_painterMesh == null)
                return;
            for (int index = 0; index < _textureLayers.Count; index++)
            {
                WasperPaintTextureLayer layer = _textureLayers[index];
                if (layer.Bitmap == null || layer.Placement.Initialized)
                    continue;
                FitLayerToAtlas(layer);
            }
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
            if (ActiveTexture.Bitmap != null)
            {
                ActiveTexture.Visible = true;
                ActiveTexture.EditMode = true;
                _tool = WasperPaintTool.None;
            }
            ActiveTexture.Revision++;
            _paintForm?.RefreshCanvas();
        }

        internal void ToggleTextureVisibility()
        {
            List<WasperPaintTextureLayer> loaded = _textureLayers
                .Where(layer => layer.Bitmap != null)
                .ToList();
            if (loaded.Count == 0)
                return;
            EndTextureTransform();
            PushUndo(CaptureSnapshot());
            bool show = !loaded.Any(layer => layer.Visible);
            foreach (WasperPaintTextureLayer layer in loaded)
            {
                layer.Visible = show;
                if (!show)
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
            if (layerIndex < 0 || layerIndex >= _textureLayers.Count ||
                _textureLayers[layerIndex].Bitmap == null)
                return;
            EndTextureTransform();
            PushUndo(CaptureSnapshot());
            WasperPaintTextureLayer layer = _textureLayers[layerIndex];
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
            if (ActiveTexture.Bitmap == null)
                return;
            EndTextureTransform();
            ActiveTexture.Visible = true;
            ActiveTexture.EditMode = !ActiveTexture.EditMode;
            ActiveTexture.DistortMode = false;
            ActiveTexture.RotateMode = false;
            if (ActiveTexture.EditMode)
                _tool = WasperPaintTool.None;
            ActiveTexture.Revision++;
            _paintForm?.RefreshCanvas();
        }

        internal void ToggleTextureDistort()
        {
            if (ActiveTexture.Bitmap == null)
                return;
            EndTextureTransform();
            ActiveTexture.Visible = true;
            ActiveTexture.DistortMode = !ActiveTexture.DistortMode;
            ActiveTexture.EditMode = false;
            ActiveTexture.RotateMode = false;
            if (ActiveTexture.DistortMode)
                _tool = WasperPaintTool.None;
            ActiveTexture.Revision++;
            _paintForm?.RefreshCanvas();
        }

        internal void ToggleTextureRotate()
        {
            if (ActiveTexture.Bitmap == null)
                return;
            EndTextureTransform();
            ActiveTexture.Visible = true;
            ActiveTexture.RotateMode = !ActiveTexture.RotateMode;
            ActiveTexture.EditMode = false;
            ActiveTexture.DistortMode = false;
            if (ActiveTexture.RotateMode)
                _tool = WasperPaintTool.None;
            ActiveTexture.Revision++;
            _paintForm?.RefreshCanvas();
        }

        internal void ToggleAtlasFlipMap()
        {
            if (_painterMesh == null)
                return;
            PushUndo(CaptureSnapshot());
            _atlasFlipMap = !_atlasFlipMap;
            _painterVisualRevision++;
            _paintForm?.RefreshCanvas();
        }

        internal void RotateAtlasClockwise()
        {
            if (_painterMesh == null)
                return;
            PushUndo(CaptureSnapshot());
            _atlasQuarterTurns = (_atlasQuarterTurns + 1) % 4;
            _painterVisualRevision++;
            _paintForm?.FitCanvas();
        }

        internal void FitTextureToAtlas()
        {
            if (ActiveTexture.Bitmap == null || _painterMesh == null)
                return;
            EndTextureTransform();
            PushUndo(CaptureSnapshot());
            FitLayerToAtlas(ActiveTexture);
            _paintForm?.RefreshCanvas();
        }

        private void FitLayerToAtlas(WasperPaintTextureLayer layer)
        {
            BoundingBox box = new BoundingBox(_painterMesh.Vertices
                .Select(vertex => TransformAtlasPoint(vertex)));
            double width = Math.Max(box.Max.X - box.Min.X, 1e-6);
            double height = Math.Max(box.Max.Y - box.Min.Y, 1e-6);
            double imageAspect = layer.Bitmap.Width / (double)layer.Bitmap.Height;
            double atlasAspect = width / height;
            if (imageAspect > atlasAspect)
            {
                double fittedHeight = width / imageAspect;
                layer.Placement.MinX = box.Min.X;
                layer.Placement.MaxX = box.Max.X;
                layer.Placement.MinY = (box.Min.Y + box.Max.Y - fittedHeight) * 0.5;
                layer.Placement.MaxY = layer.Placement.MinY + fittedHeight;
            }
            else
            {
                double fittedWidth = height * imageAspect;
                layer.Placement.MinY = box.Min.Y;
                layer.Placement.MaxY = box.Max.Y;
                layer.Placement.MinX = (box.Min.X + box.Max.X - fittedWidth) * 0.5;
                layer.Placement.MaxX = layer.Placement.MinX + fittedWidth;
            }
            layer.Placement.ResetCornersFromBounds();
            layer.Placement.Initialized = true;
            layer.Visible = true;
            layer.Revision++;
        }

        internal void BeginTextureTransform(int corner)
        {
            if (!TextureHandlesVisible || corner < 0 || corner > 3)
                return;
            _textureTransformBefore = CaptureSnapshot();
            ActiveTexture.Placement.TransformStartCorners =
                (Point2d[])ActiveTexture.Placement.Corners.Clone();
            ActiveTexture.Placement.TransformCorner = corner;
        }

        internal void BeginTextureMove(Point3d point)
        {
            if (!TextureHandlesVisible || !ActiveTexture.Placement.Initialized)
                return;
            _textureTransformBefore = CaptureSnapshot();
            ActiveTexture.Placement.TransformStartPoint = new Point2d(point.X, point.Y);
            ActiveTexture.Placement.TransformStartCorners =
                (Point2d[])ActiveTexture.Placement.Corners.Clone();
            ActiveTexture.Placement.TransformCorner = -2;
        }

        internal void MoveTextureCorner(int corner, Point3d point)
        {
            WasperPaintTexturePlacement placement = ActiveTexture.Placement;
            if (!TextureHandlesVisible || !placement.Initialized || corner < 0 || corner > 3)
                return;
            if (TextureDistortMode)
            {
                var candidate = (Point2d[])placement.Corners.Clone();
                candidate[corner] = new Point2d(point.X, point.Y);
                if (!WasperPaintTexturePlacement.IsConvexQuad(candidate))
                    return;
                placement.Corners[corner] = candidate[corner];
            }
            else
            {
                Point2d[] start = placement.TransformStartCorners;
                if (start == null || placement.TransformCorner != corner)
                    return;
                int opposite = (corner + 2) % 4;
                Point2d anchor = start[opposite];
                Vector2d initial = start[corner] - anchor;
                Vector2d current = new Point2d(point.X, point.Y) - anchor;
                double scale = initial.Length <= 1e-9
                    ? 1.0
                    : Math.Max(1e-4, current.Length / initial.Length);
                for (int index = 0; index < 4; index++)
                    placement.Corners[index] = anchor + (start[index] - anchor) * scale;
            }
            placement.UpdateBoundsFromCorners();
            ActiveTexture.Revision++;
            _paintForm?.RefreshCanvas();
        }

        internal void MoveTexture(Point3d point)
        {
            WasperPaintTexturePlacement placement = ActiveTexture.Placement;
            if (placement.TransformCorner != -2 || placement.TransformStartCorners == null)
                return;
            Point2d current = new Point2d(point.X, point.Y);
            if (TextureRotateMode)
            {
                Point2d center = new Point2d(
                    placement.TransformStartCorners.Average(corner => corner.X),
                    placement.TransformStartCorners.Average(corner => corner.Y));
                Vector2d startVector = placement.TransformStartPoint - center;
                Vector2d currentVector = current - center;
                if (startVector.Length <= 1e-9 || currentVector.Length <= 1e-9)
                    return;
                double angle = Math.Atan2(currentVector.Y, currentVector.X) -
                               Math.Atan2(startVector.Y, startVector.X);
                double cosine = Math.Cos(angle);
                double sine = Math.Sin(angle);
                for (int index = 0; index < 4; index++)
                {
                    Vector2d offset = placement.TransformStartCorners[index] - center;
                    placement.Corners[index] = new Point2d(
                        center.X + offset.X * cosine - offset.Y * sine,
                        center.Y + offset.X * sine + offset.Y * cosine);
                }
            }
            else
            {
                Vector2d movement = current - placement.TransformStartPoint;
                for (int index = 0; index < 4; index++)
                    placement.Corners[index] = placement.TransformStartCorners[index] + movement;
            }
            placement.UpdateBoundsFromCorners();
            ActiveTexture.Revision++;
            _paintForm?.RefreshCanvas();
        }

        internal void EndTextureTransform()
        {
            ActiveTexture.Placement.EndTransform();
            if (_textureTransformBefore == null)
                return;
            Ge17v2TextureSnapshot before =
                _textureTransformBefore.TextureLayers[_textureTransformBefore.ActiveTextureLayer];
            bool changed = before.Corners == null ||
                           before.Corners.Where((point, index) =>
                               point.DistanceTo(ActiveTexture.Placement.Corners[index]) > 1e-12)
                               .Any();
            if (changed)
                PushUndo(_textureTransformBefore);
            _textureTransformBefore = null;
        }

        internal void ApplyTextureToPaint()
        {
            if (ActiveTexture.Bitmap == null ||
                !ActiveTexture.Placement.Initialized || _values.Length == 0)
                return;
            Ge17v2PainterSnapshot before = CaptureSnapshot();
            int changed = WasperPaintTextureSampler.ApplyToValues(
                ActiveTexture.Bitmap,
                ActiveTexture.Placement,
                BuildTextureSamples(),
                index => index >= 0 && index < _eligible.Length && _eligible[index],
                _domain,
                _values);
            if (changed == 0)
                return;
            PushUndo(before);
            TextureApplied();
        }

        internal void ApplyTextureCompositeToPaint()
        {
            if (_values.Length == 0)
                return;
            Ge17v2PainterSnapshot before = CaptureSnapshot();
            int changed = WasperPaintTextureSampler.ApplyCompositeToValues(
                _textureLayers,
                BuildTextureSamples(),
                index => index >= 0 && index < _eligible.Length && _eligible[index],
                _domain,
                _values);
            if (changed == 0)
                return;
            PushUndo(before);
            TextureApplied();
        }

        private void TextureApplied()
        {
            if (_live)
            {
                ApplyWorkingValues();
                ScheduleSolution();
            }
            RebuildDisplayMeshes();
            UpdateConduit();
            RhinoDoc.ActiveDoc?.Views.Redraw();
        }

        private List<KeyValuePair<int, Point2d>> BuildTextureSamples()
        {
            var result = new List<KeyValuePair<int, Point2d>>(_painterMesh.Vertices.Count);
            var sampledSources = new HashSet<int>();
            for (int index = 0; index < _painterMesh.Vertices.Count; index++)
            {
                int source = index < _painterSourceIndices.Count
                    ? _painterSourceIndices[index]
                    : index;
                if (!sampledSources.Add(source))
                    continue;
                Point3d point = _painterMesh.Vertices.Point3dAt(index);
                point = TransformAtlasPoint(point);
                result.Add(new KeyValuePair<int, Point2d>(
                    source,
                    new Point2d(point.X, point.Y)));
            }
            return result;
        }

        internal void RemoveTextureOverlay()
        {
            if (ActiveTexture.Bitmap == null)
                return;
            EndTextureTransform();
            ActiveTexture.IgnoredSourceKey = ActiveTexture.SourceKey;
            ActiveTexture.Bitmap.Dispose();
            ActiveTexture.Bitmap = null;
            ActiveTexture.Visible = false;
            ActiveTexture.EditMode = false;
            ActiveTexture.DistortMode = false;
            ActiveTexture.RotateMode = false;
            ActiveTexture.SourceDescription += " (overlay removed)";
            ActiveTexture.Revision++;
            _paintForm?.RefreshCanvas();
        }

        internal Point3d MirrorAtlasPoint(Point3d point)
        {
            return WasperPaintAtlasTransform.Transform(
                point,
                Plane.WorldXY,
                _painterMesh,
                true,
                0);
        }

        internal Point3d TransformAtlasPoint(Point3d point)
        {
            return WasperPaintAtlasTransform.Transform(
                point,
                Plane.WorldXY,
                _painterMesh,
                _atlasFlipMap,
                _atlasQuarterTurns);
        }

        internal Point3d InverseTransformAtlasPoint(Point3d point)
        {
            return WasperPaintAtlasTransform.Inverse(
                point,
                Plane.WorldXY,
                _painterMesh,
                _atlasFlipMap,
                _atlasQuarterTurns);
        }
    }
}
