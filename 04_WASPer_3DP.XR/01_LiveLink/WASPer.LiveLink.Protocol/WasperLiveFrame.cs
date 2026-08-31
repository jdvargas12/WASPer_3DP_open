using System;
using System.Collections.Generic;

namespace WASPer.LiveLink
{
    /// <summary>One item inside a channel block.</summary>
    public interface IWasperLiveItem
    {
        WasperLiveElementType ElementType { get; }
        void Write(WasperBufferWriter writer);
    }

    /// <summary>
    /// Mesh in frame-local coordinates. Vertices are float32 offsets from the
    /// frame origin; construction goes through <see cref="WasperLiveFrameBuilder"/>,
    /// which subtracts the origin. The constructor is internal precisely so no
    /// caller outside this assembly can supply raw world coordinates by mistake.
    /// </summary>
    public sealed class WasperMeshItem : IWasperLiveItem
    {
        internal WasperMeshItem(
            int vertexCount,
            int faceCount,
            float[] vertices,
            int[] faces,
            float[] normals,
            byte[] colors,
            float[] textureCoordinates)
        {
            VertexCount = vertexCount;
            FaceCount = faceCount;
            Vertices = vertices;
            Faces = faces;
            Normals = normals;
            Colors = colors;
            TextureCoordinates = textureCoordinates;
        }

        public int VertexCount { get; }
        public int FaceCount { get; }

        /// <summary>float32[3 * VertexCount], offsets from the frame origin.</summary>
        public float[] Vertices { get; }

        /// <summary>int32[4 * FaceCount]. Quads are native; a triangle is A,B,C,C.</summary>
        public int[] Faces { get; }

        public float[] Normals { get; }
        public byte[] Colors { get; }
        public float[] TextureCoordinates { get; }

        public WasperLiveElementType ElementType => WasperLiveElementType.Mesh;

        public uint Flags
        {
            get
            {
                uint flags = 0;
                if (Normals != null) flags |= WasperLiveLinkProtocol.MeshFlagNormals;
                if (Colors != null) flags |= WasperLiveLinkProtocol.MeshFlagColors;
                if (TextureCoordinates != null) flags |= WasperLiveLinkProtocol.MeshFlagTextureCoordinates;
                return flags;
            }
        }

        public void Write(WasperBufferWriter writer)
        {
            writer.WriteInt32(VertexCount);
            writer.WriteInt32(FaceCount);
            writer.WriteUInt32(Flags);

            for (int i = 0; i < Vertices.Length; i++) writer.WriteSingle(Vertices[i]);
            for (int i = 0; i < Faces.Length; i++) writer.WriteInt32(Faces[i]);

            if (Normals != null)
                for (int i = 0; i < Normals.Length; i++) writer.WriteSingle(Normals[i]);

            if (Colors != null)
                writer.WriteBytes(Colors);

            if (TextureCoordinates != null)
                for (int i = 0; i < TextureCoordinates.Length; i++) writer.WriteSingle(TextureCoordinates[i]);
        }
    }

    /// <summary>
    /// Polyline in frame-local coordinates. First class rather than a degenerate
    /// mesh: WASPer print paths are the dominant payload and a polyline block is
    /// roughly an order of magnitude smaller than the equivalent tube mesh.
    /// </summary>
    public sealed class WasperPolylineItem : IWasperLiveItem
    {
        internal WasperPolylineItem(int pointCount, float[] points, bool closed)
        {
            PointCount = pointCount;
            Points = points;
            Closed = closed;
        }

        public int PointCount { get; }

        /// <summary>float32[3 * PointCount], offsets from the frame origin.</summary>
        public float[] Points { get; }

        public bool Closed { get; }

        public WasperLiveElementType ElementType => WasperLiveElementType.Polyline;

        public void Write(WasperBufferWriter writer)
        {
            writer.WriteInt32(PointCount);
            writer.WriteUInt32(Closed ? WasperLiveLinkProtocol.PolylineFlagClosed : 0u);
            for (int i = 0; i < Points.Length; i++) writer.WriteSingle(Points[i]);
        }
    }

    /// <summary>Single point in frame-local coordinates.</summary>
    public sealed class WasperPointItem : IWasperLiveItem
    {
        internal WasperPointItem(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public float X { get; }
        public float Y { get; }
        public float Z { get; }

        public WasperLiveElementType ElementType => WasperLiveElementType.Point3;

        public void Write(WasperBufferWriter writer)
        {
            writer.WriteSingle(X);
            writer.WriteSingle(Y);
            writer.WriteSingle(Z);
        }
    }

    /// <summary>
    /// Float64 value. Numbers stay double precision because the Gc08 camera
    /// packet needs it; only display coordinates are demoted to float32.
    /// </summary>
    public sealed class WasperNumberItem : IWasperLiveItem
    {
        internal WasperNumberItem(double value) { Value = value; }

        public double Value { get; }

        public WasperLiveElementType ElementType => WasperLiveElementType.Float64;

        public void Write(WasperBufferWriter writer) => writer.WriteDouble(Value);
    }

    /// <summary>UTF-8 string.</summary>
    public sealed class WasperTextItem : IWasperLiveItem
    {
        internal WasperTextItem(string value) { Value = value ?? string.Empty; }

        public string Value { get; }

        public WasperLiveElementType ElementType => WasperLiveElementType.Utf8String;

        public void Write(WasperBufferWriter writer) => writer.WriteUtf8(Value);
    }

    /// <summary>One Grasshopper branch: a path plus its items, in order.</summary>
    public sealed class WasperLiveBranch
    {
        internal WasperLiveBranch(int[] path)
        {
            Path = path ?? Array.Empty<int>();
            Items = new List<IWasperLiveItem>();
        }

        public int[] Path { get; }
        public List<IWasperLiveItem> Items { get; }
    }

    /// <summary>One channel block: a semantic id, a single element type, and branches.</summary>
    public sealed class WasperLiveChannel
    {
        internal WasperLiveChannel(WasperLiveChannelId channelId, WasperLiveElementType elementType)
        {
            ChannelId = channelId;
            ElementType = elementType;
            Branches = new List<WasperLiveBranch>();
        }

        public WasperLiveChannelId ChannelId { get; }
        public WasperLiveElementType ElementType { get; }
        public List<WasperLiveBranch> Branches { get; }

        public int ItemCount
        {
            get
            {
                int total = 0;
                for (int i = 0; i < Branches.Count; i++) total += Branches[i].Items.Count;
                return total;
            }
        }
    }

    /// <summary>
    /// A complete frame. Produced by <see cref="WasperLiveFrameBuilder"/> on the
    /// sending side and by <see cref="WasperLiveLinkSerializer.Deserialize"/> on
    /// the receiving side.
    /// </summary>
    public sealed class WasperLiveFrame
    {
        internal WasperLiveFrame(
            double unitScaleToMetres,
            double originX,
            double originY,
            double originZ)
        {
            UnitScaleToMetres = unitScaleToMetres;
            OriginX = originX;
            OriginY = originY;
            OriginZ = originZ;
            Channels = new List<WasperLiveChannel>();
        }

        /// <summary>Model units to metres. The sender never applies this; the receiver decides.</summary>
        public double UnitScaleToMetres { get; }

        public double OriginX { get; }
        public double OriginY { get; }
        public double OriginZ { get; }

        public List<WasperLiveChannel> Channels { get; }

        /// <summary>Set by the writer at publish time, and by the reader on parse.</summary>
        public long Revision { get; internal set; }

        /// <summary>UTC ticks. Set by the writer at publish time.</summary>
        public long TimestampUtc { get; internal set; }

        /// <summary>First block carrying this channel id, or null. A channel id may
        /// appear in more than one block when it carries more than one element type,
        /// so prefer the two-argument overload when the type matters.</summary>
        public WasperLiveChannel FindChannel(WasperLiveChannelId channelId)
        {
            for (int i = 0; i < Channels.Count; i++)
                if (Channels[i].ChannelId == channelId) return Channels[i];
            return null;
        }

        /// <summary>The block carrying this channel id and element type, or null.</summary>
        public WasperLiveChannel FindChannel(WasperLiveChannelId channelId, WasperLiveElementType elementType)
        {
            for (int i = 0; i < Channels.Count; i++)
                if (Channels[i].ChannelId == channelId && Channels[i].ElementType == elementType)
                    return Channels[i];
            return null;
        }

        /// <summary>Restores a world coordinate from a frame-local one.</summary>
        public void ToWorld(float lx, float ly, float lz, out double x, out double y, out double z)
        {
            x = OriginX + lx;
            y = OriginY + ly;
            z = OriginZ + lz;
        }
    }
}
