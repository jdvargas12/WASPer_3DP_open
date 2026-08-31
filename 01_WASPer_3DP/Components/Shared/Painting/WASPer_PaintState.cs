using System;

namespace WASPer_3DP.Painting
{
    internal sealed class WasperPaintState
    {
        public int Version;
        public string OwnerInstanceGuid;
        public DateTime SavedUtc;
        public string Signature;
        public string TopologySignature;
        public string[] BranchGeometryKeys;
        public int[] BranchCounts;
        public double[] Values;
        public double[] AppliedValues;
        public bool Preview = true;
        public double Radius = 10.0;
        public double BrushStrength = 0.2;
        public double SmoothStrength = 0.5;
        public double Falloff = 2.0;
        public string TextureSourceKey;
        public bool TexturePlacementInitialized;
        public double TextureMinX;
        public double TextureMinY;
        public double TextureMaxX;
        public double TextureMaxY;
        public double[] TextureCorners;
        public bool AtlasFlipMap;
        public int AtlasQuarterTurns;
        // Retained for compatibility with sessions written before atlas flipping.
        public bool TextureFlipMap = false;
        public bool TextureVisible = true;
        public int ActiveTextureLayer;
        public WasperPaintTextureLayerState[] TextureLayers;
    }

    internal sealed class WasperPaintTextureLayerState
    {
        public string SourceKey;
        public bool PlacementInitialized;
        public double MinX;
        public double MinY;
        public double MaxX;
        public double MaxY;
        public double[] Corners;
        public bool Visible = true;
        public double Opacity = 1.0;
        public bool IsText;
        public string TextContent;
        public string FontName;
        public double FontSize = 10.0;
        public bool TextCommitted;
    }
}
