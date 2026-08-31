global using WASPer_3DP.Painting;

using System.Collections.Generic;
using System.Drawing;
using System.Runtime.CompilerServices;

using Rhino.Geometry;

namespace WASPer_3DP.Painting
{
    internal enum WasperPaintTool
    {
        None,
        Pull,
        Push,
        Zero,
        Smooth,
        Erase
    }

    internal enum WasperSmoothRegionShape
    {
        Square,
        Freeform
    }

    internal sealed class WasperPaintMarker
    {
        public Line Line;
        public Color Color;
        public int Thickness;
    }

    internal sealed class WasperPaintAtlasBounds
    {
        public double MinX;
        public double MinY;
        public double MaxX;
        public double MaxY;
    }

    /// <summary>
    /// Geometry-neutral API consumed by the shared painter window. Components
    /// keep ownership of their paint field and geometry mapping behind this
    /// contract.
    /// </summary>
    internal interface IWasperPainterHost
    {
        string PainterTitle { get; }
        string PullToolLabel { get; }
        string PushToolLabel { get; }
        Color PullToolColor { get; }
        Color PushToolColor { get; }
        string PainterLegend { get; }
        bool SupportsZeroTool { get; }
        Color ZeroToolColor { get; }
        WasperPaintTool ActiveTool { get; }
        WasperSmoothRegionShape SmoothRegionShape { get; }
        bool PreviewEnabled { get; }
        bool LiveEnabled { get; }
        bool UpdateEnabled { get; }
        bool HasPendingUpdate { get; }
        Mesh PainterMesh { get; }
        Plane PainterPlane { get; }
        IList<WasperPaintMarker> PainterMarkers { get; }
        bool ShowAtlasDimensions { get; }
        IList<WasperPaintAtlasBounds> AtlasDimensionBounds { get; }
        double PainterRadius { get; }
        double PainterBrushStrength { get; }
        double PainterSmoothStrength { get; }
        bool PainterRadiusEditable { get; }
        bool PainterBrushStrengthEditable { get; }
        bool PainterSmoothStrengthEditable { get; }
        int PainterVisualRevision { get; }
        bool CanUndoPaint { get; }
        bool CanRedoPaint { get; }
        bool SupportsTextures { get; }
        bool SupportsTextTextures { get; }
        bool SupportsFieldCollection { get; }
        bool SupportsAtlasTransforms { get; }
        int FieldCount { get; }
        int ActiveFieldIndex { get; }
        double FieldOffset { get; }
        double FieldResolution { get; }
        double FieldFrameSize { get; }
        bool FieldArrangeMode { get; }

        int TextureLayerCount { get; }
        int ActiveTextureLayer { get; }
        IList<WasperPaintTextureLayer> TextureLayers { get; }
        int TextTextureLayerCount { get; }
        int ActiveTextTextureLayer { get; }
        IList<WasperPaintTextureLayer> TextTextureLayers { get; }
        bool HasTextureSource { get; }
        Bitmap TextureBitmap { get; }
        bool TextureVisible { get; }
        bool TextureEditMode { get; }
        bool TextureDistortMode { get; }
        bool TextureRotateMode { get; }
        bool TextureHandlesVisible { get; }
        bool SupportsTextureEdgeHandles { get; }
        bool TextureIsDistorted { get; }
        int TextureRevision { get; }
        bool AtlasFlipMap { get; }
        int AtlasQuarterTurns { get; }
        double AtlasMirrorCenterX { get; }
        IList<Point2d> TextureCorners { get; }

        void TogglePreview();
        void ToggleLive();
        void UpdateAlgorithm();
        void UndoPaint();
        void RedoPaint();
        void ClearPaint();
        void PreviewPainterSettings(double radius, double brushStrength, double smoothStrength);
        void CommitPainterSettings(double radius, double brushStrength, double smoothStrength);
        void SetPainterTool(WasperPaintTool tool);
        void SetSmoothRegionShape(WasperSmoothRegionShape shape);
        void ApplySmoothRegion(IList<Point3d> boundary);
        bool PainterBeginStroke(Point3d atlasPoint);
        void PainterContinueStroke(Point3d atlasPoint);
        void PainterEndStroke();
        void PainterHover(Point3d atlasPoint);
        void ClearPainterHover();
        void AddNewField();
        void DuplicateActiveField();
        void RemoveActiveField();
        void SelectPreviousField();
        void SelectNextField();
        void MoveActiveFieldUp();
        void MoveActiveFieldDown();
        void PreviewFieldSettings(double offset, double resolution, double frameSize);
        void CommitFieldSettings(double offset, double resolution, double frameSize);
        void ToggleFieldArrangeMode();
        bool SelectFieldAt(Point3d atlasPoint);
        bool BeginFieldDrag(Point3d atlasPoint);
        void MoveFieldDrag(Point3d atlasPoint);
        void EndFieldDrag();

        void SavePainterSession();
        void LoadPainterSession();
        void SavePainterBitmap(Bitmap bitmap);

        void ToggleTextureVisibility();
        void ToggleTextureLayerVisibility(int layerIndex);
        void ToggleTextureEdit();
        void ToggleTextureDistort();
        void ToggleTextureRotate();
        void ToggleAtlasFlipMap();
        void RotateAtlasClockwise();
        void FitTextureToAtlas();
        void ApplyTextureToPaint();
        void ApplyTextureCompositeToPaint();
        void RemoveTextureOverlay();
        void SelectTextureLayer(int layerIndex);
        void SelectTextTextureLayer(int layerIndex);
        void ToggleTextTextureLayerVisibility(int layerIndex);
        void PreviewTextTexture(string text, string fontName, double fontSize);
        void CommitTextTexture(string text, string fontName, double fontSize);
        void DuplicateTextTextureLayer();
        void RemoveTextTextureLayer();
        void MoveTextTextureLayer(int direction);
        void BeginTextureTransform(int corner);
        void BeginTextureMove(Point3d atlasPoint);
        void MoveTextureCorner(int corner, Point3d atlasPoint, bool ortho);
        void MoveTexture(Point3d atlasPoint);
        void EndTextureTransform();
        Point3d MirrorAtlasPoint(Point3d point);
        Point3d TransformAtlasPoint(Point3d point);
        Point3d InverseTransformAtlasPoint(Point3d point);
    }

    internal static class WasperPaintAtlasTransform
    {
        private sealed class AtlasCenter
        {
            internal double X;
            internal double Y;
        }

        private static readonly ConditionalWeakTable<Mesh, AtlasCenter> Centers =
            new ConditionalWeakTable<Mesh, AtlasCenter>();

        internal static Point3d Transform(
            Point3d point,
            Plane plane,
            Mesh mesh,
            bool flip,
            int quarterTurns)
        {
            return Rotate(point, plane, mesh, flip, quarterTurns, false);
        }

        internal static Point3d Inverse(
            Point3d point,
            Plane plane,
            Mesh mesh,
            bool flip,
            int quarterTurns)
        {
            return Rotate(point, plane, mesh, flip, quarterTurns, true);
        }

        private static Point3d Rotate(
            Point3d point,
            Plane plane,
            Mesh mesh,
            bool flip,
            int quarterTurns,
            bool inverse)
        {
            int turns = ((quarterTurns % 4) + 4) % 4;
            if (!flip && turns == 0)
                return point;
            if (mesh == null || !mesh.IsValid)
                return point;
            if (!Centers.TryGetValue(mesh, out AtlasCenter center))
            {
                BoundingBox box = mesh.GetBoundingBox(plane);
                center = new AtlasCenter
                {
                    X = (box.Min.X + box.Max.X) * 0.5,
                    Y = (box.Min.Y + box.Max.Y) * 0.5
                };
                try
                {
                    Centers.Add(mesh, center);
                }
                catch (System.ArgumentException)
                {
                    Centers.TryGetValue(mesh, out center);
                }
            }
            double centerX = center.X;
            double centerY = center.Y;
            Vector3d delta = point - plane.Origin;
            double x = delta * plane.XAxis;
            double y = delta * plane.YAxis;
            if (inverse)
            {
                for (int turn = 0; turn < turns; turn++)
                {
                    double dx = x - centerX;
                    double dy = y - centerY;
                    x = centerX - dy;
                    y = centerY + dx;
                }
                if (flip)
                    x = centerX * 2.0 - x;
            }
            else
            {
                if (flip)
                    x = centerX * 2.0 - x;
                for (int turn = 0; turn < turns; turn++)
                {
                    double dx = x - centerX;
                    double dy = y - centerY;
                    x = centerX + dy;
                    y = centerY - dx;
                }
            }
            return plane.Origin + plane.XAxis * x + plane.YAxis * y;
        }
    }
}
