using System.Collections.Generic;
using System.Drawing;

using Rhino.Geometry;

using WASPer_3DP.Painting;

namespace WASPer_3DP_Components._4_0_Print_Paths
{
    /// <summary>
    /// Keeps Pp13's path-specific implementation behind the geometry-neutral
    /// painter contract used by the reusable form and canvas.
    /// </summary>
    internal sealed class WasperPp13PainterHost : IWasperPainterHost
    {
        private readonly wsp_Pp14_Fuzzy_Skin_from_Paint _component;

        internal WasperPp13PainterHost(wsp_Pp14_Fuzzy_Skin_from_Paint component)
        {
            _component = component;
        }

        public string PainterTitle => "WASPer Shell Painter";
        public string PullToolLabel => "Pull";
        public string PushToolLabel => "Push";
        public Color PullToolColor => WasperPaintColors.Pulled;
        public Color PushToolColor => WasperPaintColors.Pushed;
        public string PainterLegend =>
            "Push  GREEN    \u2190    Zero  BLUE    \u2192    Pull  RED";
        public bool SupportsZeroTool => false;
        public Color ZeroToolColor => WasperPaintColors.Neutral;
        public WasperPaintTool ActiveTool => _component.ActiveTool;
        public WasperSmoothRegionShape SmoothRegionShape =>
            _component.SmoothRegionShape;
        public bool PreviewEnabled => _component.PreviewEnabled;
        public bool LiveEnabled => _component.LiveEnabled;
        public bool UpdateEnabled => _component.UpdateEnabled;
        public bool HasPendingUpdate => _component.HasPendingUpdate;
        public Mesh PainterMesh => _component.PainterMesh;
        public Plane PainterPlane => _component.PainterPlane;
        public IList<WasperPaintMarker> PainterMarkers => _component.PainterMarkers;
        public bool ShowAtlasDimensions => false;
        public IList<WasperPaintAtlasBounds> AtlasDimensionBounds => null;
        public double PainterRadius => _component.PainterRadius;
        public double PainterBrushStrength => _component.PainterBrushStrength;
        public double PainterSmoothStrength => _component.PainterSmoothStrength;
        public bool PainterRadiusEditable => _component.PainterRadiusEditable;
        public bool PainterBrushStrengthEditable => _component.PainterBrushStrengthEditable;
        public bool PainterSmoothStrengthEditable => _component.PainterSmoothStrengthEditable;
        public int PainterVisualRevision => _component.PainterVisualRevision;
        public bool CanUndoPaint => _component.CanUndoPaint;
        public bool CanRedoPaint => _component.CanRedoPaint;
        public bool SupportsTextures => true;
        public bool SupportsTextTextures => true;
        public bool SupportsFieldCollection => false;
        public bool SupportsAtlasTransforms => true;
        public int FieldCount => 1;
        public int ActiveFieldIndex => 0;
        public double FieldOffset => 0.0;
        public double FieldResolution => 1.0;
        public double FieldFrameSize => 1.0;
        public bool FieldArrangeMode => false;

        public int TextureLayerCount => _component.TextureLayerCount;
        public int ActiveTextureLayer => _component.ActiveTextureLayer;
        public IList<WasperPaintTextureLayer> TextureLayers => _component.TextureLayers;
        public int TextTextureLayerCount => _component.TextTextureLayerCount;
        public int ActiveTextTextureLayer => _component.ActiveTextTextureLayer;
        public IList<WasperPaintTextureLayer> TextTextureLayers =>
            _component.TextTextureLayers;
        public bool HasTextureSource => _component.HasTextureSource;
        public Bitmap TextureBitmap => _component.TextureBitmap;
        public bool TextureVisible => _component.TextureVisible;
        public bool TextureEditMode => _component.TextureEditMode;
        public bool TextureDistortMode => _component.TextureDistortMode;
        public bool TextureRotateMode => _component.TextureRotateMode;
        public bool TextureHandlesVisible => _component.TextureHandlesVisible;
        public bool SupportsTextureEdgeHandles => true;
        public bool TextureIsDistorted => _component.TextureIsDistorted;
        public int TextureRevision => _component.TextureRevision;
        public bool AtlasFlipMap => _component.AtlasFlipMap;
        public int AtlasQuarterTurns => _component.AtlasQuarterTurns;
        public double AtlasMirrorCenterX => _component.AtlasMirrorCenterX;
        public IList<Point2d> TextureCorners => _component.TextureCorners;

        public void TogglePreview() => _component.TogglePreview();
        public void ToggleLive() => _component.ToggleLive();
        public void UpdateAlgorithm() => _component.UpdateAlgorithm();
        public void UndoPaint() => _component.UndoPaint();
        public void RedoPaint() => _component.RedoPaint();
        public void ClearPaint() => _component.ClearPaint();

        public void PreviewPainterSettings(
            double radius,
            double brushStrength,
            double smoothStrength) =>
            _component.PreviewPainterSettings(radius, brushStrength, smoothStrength);

        public void CommitPainterSettings(
            double radius,
            double brushStrength,
            double smoothStrength) =>
            _component.CommitPainterSettings(radius, brushStrength, smoothStrength);

        public void SetPainterTool(WasperPaintTool tool) => _component.SetPainterTool(tool);
        public void SetSmoothRegionShape(WasperSmoothRegionShape shape) =>
            _component.SetSmoothRegionShape(shape);
        public void ApplySmoothRegion(IList<Point3d> boundary) =>
            _component.ApplySmoothRegion(boundary);
        public bool PainterBeginStroke(Point3d atlasPoint) => _component.PainterBeginStroke(atlasPoint);
        public void PainterContinueStroke(Point3d atlasPoint) => _component.PainterContinueStroke(atlasPoint);
        public void PainterEndStroke() => _component.PainterEndStroke();
        public void PainterHover(Point3d atlasPoint) => _component.PainterHover(atlasPoint);
        public void ClearPainterHover() => _component.ClearPainterHover();
        public void AddNewField() { }
        public void DuplicateActiveField() { }
        public void RemoveActiveField() { }
        public void SelectPreviousField() { }
        public void SelectNextField() { }
        public void MoveActiveFieldUp() { }
        public void MoveActiveFieldDown() { }
        public void PreviewFieldSettings(double offset, double resolution, double frameSize) { }
        public void CommitFieldSettings(double offset, double resolution, double frameSize) { }
        public void ToggleFieldArrangeMode() { }
        public bool SelectFieldAt(Point3d atlasPoint) => false;
        public bool BeginFieldDrag(Point3d atlasPoint) => false;
        public void MoveFieldDrag(Point3d atlasPoint) { }
        public void EndFieldDrag() { }

        public void SavePainterSession() => _component.SavePainterSession();
        public void LoadPainterSession() => _component.LoadPainterSession();
        public void SavePainterBitmap(Bitmap bitmap) => _component.SavePainterBitmap(bitmap);

        public void ToggleTextureVisibility()
        {
            _component.ToggleTextureVisibility();
            _component.RefreshTextureReferencePreview();
        }
        public void ToggleTextureLayerVisibility(int layerIndex)
        {
            _component.ToggleTextureLayerVisibility(layerIndex);
            _component.RefreshTextureReferencePreview();
        }
        public void ToggleTextureEdit()
        {
            _component.ToggleTextureEdit();
            _component.RefreshTextureReferencePreview();
        }
        public void ToggleTextureDistort()
        {
            _component.ToggleTextureDistort();
            _component.RefreshTextureReferencePreview();
        }
        public void ToggleTextureRotate()
        {
            _component.ToggleTextureRotate();
            _component.RefreshTextureReferencePreview();
        }
        public void ToggleAtlasFlipMap()
        {
            _component.ToggleAtlasFlipMap();
            _component.RefreshTextureReferencePreview();
        }
        public void RotateAtlasClockwise()
        {
            _component.RotateAtlasClockwise();
            _component.RefreshTextureReferencePreview();
        }
        public void FitTextureToAtlas()
        {
            _component.FitTextureToAtlas();
            _component.RefreshTextureReferencePreview();
        }
        public void ApplyTextureToPaint() => _component.ApplyTextureToPaint();
        public void ApplyTextureCompositeToPaint() =>
            _component.ApplyTextureCompositeToPaint();
        public void RemoveTextureOverlay()
        {
            _component.RemoveTextureOverlay();
            _component.RefreshTextureReferencePreview();
        }
        public void SelectTextureLayer(int layerIndex)
        {
            _component.SelectTextureLayer(layerIndex);
            _component.RefreshTextureReferencePreview();
        }
        public void SelectTextTextureLayer(int layerIndex)
        {
            _component.SelectTextTextureLayer(layerIndex);
            _component.RefreshTextureReferencePreview();
        }
        public void ToggleTextTextureLayerVisibility(int layerIndex)
        {
            _component.ToggleTextTextureLayerVisibility(layerIndex);
            _component.RefreshTextureReferencePreview();
        }
        public void PreviewTextTexture(string text, string fontName, double fontSize) =>
            _component.PreviewTextTexture(text, fontName, fontSize);
        public void CommitTextTexture(string text, string fontName, double fontSize) =>
            _component.CommitTextTexture(text, fontName, fontSize);
        public void DuplicateTextTextureLayer() =>
            _component.DuplicateTextTextureLayer();
        public void RemoveTextTextureLayer() =>
            _component.RemoveTextTextureLayer();
        public void MoveTextTextureLayer(int direction) =>
            _component.MoveTextTextureLayer(direction);
        public void BeginTextureTransform(int corner) => _component.BeginTextureTransform(corner);
        public void BeginTextureMove(Point3d atlasPoint) => _component.BeginTextureMove(atlasPoint);
        public void MoveTextureCorner(int corner, Point3d atlasPoint, bool ortho)
        {
            _component.MoveTextureCorner(corner, atlasPoint, ortho);
            _component.RefreshTextureReferencePreview();
        }
        public void MoveTexture(Point3d atlasPoint)
        {
            _component.MoveTexture(atlasPoint);
            _component.RefreshTextureReferencePreview();
        }
        public void EndTextureTransform() => _component.EndTextureTransform();
        public Point3d MirrorAtlasPoint(Point3d point) => _component.MirrorAtlasPoint(point);
        public Point3d TransformAtlasPoint(Point3d point) =>
            _component.TransformAtlasPoint(point);
        public Point3d InverseTransformAtlasPoint(Point3d point) =>
            _component.InverseTransformAtlasPoint(point);
    }
}
