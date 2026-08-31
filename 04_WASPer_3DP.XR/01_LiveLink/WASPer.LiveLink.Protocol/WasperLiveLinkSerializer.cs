using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace WASPer.LiveLink
{
    /// <summary>
    /// Frame to bytes and back. Pure: it touches no shared memory, no mutex, and
    /// no Rhino type, so it is fully testable headless. The transport layer in
    /// <see cref="WasperLiveLinkWriter"/> only copies the buffer this produces.
    /// </summary>
    public static class WasperLiveLinkSerializer
    {
        /// <summary>
        /// Serializes a complete frame, header included. Revision and timestamp are
        /// written as zero and patched at publish time by
        /// <see cref="PatchRevisionAndTimestamp"/>; both sit outside the CRC range,
        /// so patching them never invalidates the checksum.
        /// </summary>
        public static void Serialize(WasperLiveFrame frame, WasperBufferWriter writer)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (writer == null) throw new ArgumentNullException(nameof(writer));

            writer.Reset();

            writer.WriteUInt32(WasperLiveLinkProtocol.FrameMagic);
            writer.WriteUInt32(0u);                       // frame_flags, reserved
            writer.WriteInt64(0L);                        // revision, patched at publish
            writer.WriteInt64(0L);                        // timestamp_utc, patched at publish
            writer.WriteDouble(frame.UnitScaleToMetres);
            writer.WriteDouble(frame.OriginX);
            writer.WriteDouble(frame.OriginY);
            writer.WriteDouble(frame.OriginZ);
            writer.WriteInt32(frame.Channels.Count);

            int payloadBytesPosition = writer.ReserveInt32();
            int crcPosition = writer.ReserveInt32();
            writer.WriteInt32(0);                         // reserved

            if (writer.Length != WasperLiveLinkProtocol.FrameHeaderBytes)
            {
                throw new WasperLiveLinkException(
                    "Frame header wrote " + writer.Length + " bytes, expected " +
                    WasperLiveLinkProtocol.FrameHeaderBytes + ". The layout constants and the " +
                    "writer have gone out of step.");
            }

            for (int i = 0; i < frame.Channels.Count; i++)
                WriteChannel(frame.Channels[i], writer);

            int payloadBytes = writer.Length - WasperLiveLinkProtocol.FrameHeaderBytes;
            uint crc = WasperLiveLinkCrc32.Compute(
                writer.Buffer, WasperLiveLinkProtocol.FrameHeaderBytes, payloadBytes);

            writer.PatchInt32(payloadBytesPosition, payloadBytes);
            writer.PatchUInt32(crcPosition, crc);
        }

        private static void WriteChannel(WasperLiveChannel channel, WasperBufferWriter writer)
        {
            writer.WriteUInt16((ushort)channel.ChannelId);
            writer.WriteUInt16((ushort)channel.ElementType);
            writer.WriteInt32(channel.Branches.Count);
            writer.WriteInt32(channel.ItemCount);

            int blockBytesPosition = writer.ReserveInt32();
            int blockStart = writer.Length;

            int firstItemIndex = 0;
            for (int b = 0; b < channel.Branches.Count; b++)
            {
                WasperLiveBranch branch = channel.Branches[b];

                if (branch.Path.Length > ushort.MaxValue)
                    throw new WasperLiveLinkException("Branch path is too deep to encode.");

                writer.WriteUInt16((ushort)branch.Path.Length);
                for (int p = 0; p < branch.Path.Length; p++)
                    writer.WriteInt32(branch.Path[p]);

                writer.WriteInt32(firstItemIndex);
                writer.WriteInt32(branch.Items.Count);
                firstItemIndex += branch.Items.Count;
            }

            for (int b = 0; b < channel.Branches.Count; b++)
            {
                List<IWasperLiveItem> items = channel.Branches[b].Items;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items[i].ElementType != channel.ElementType)
                    {
                        throw new WasperLiveLinkException(
                            "Item of type " + items[i].ElementType + " found in a " +
                            channel.ElementType + " channel.");
                    }
                    items[i].Write(writer);
                }
            }

            writer.PatchInt32(blockBytesPosition, writer.Length - blockStart);
        }

        /// <summary>
        /// Walks the block headers of a serialized frame and reports the size of
        /// each one. Cheap — it reads headers only, never items — and it is the
        /// difference between "the frame is too big" and knowing which input to
        /// disconnect.
        /// </summary>
        public static string DescribeBlocks(byte[] buffer, int totalLength)
        {
            if (buffer == null || totalLength < WasperLiveLinkProtocol.FrameHeaderBytes)
                return "(no frame)";

            int channelCount = BinaryPrimitives.ReadInt32LittleEndian(
                buffer.AsSpan(WasperLiveLinkProtocol.OffChannelCount, 4));

            var text = new System.Text.StringBuilder();
            int position = WasperLiveLinkProtocol.FrameHeaderBytes;

            for (int c = 0; c < channelCount; c++)
            {
                if (position + WasperLiveLinkProtocol.ChannelHeaderBytes > totalLength) break;

                var channelId = (WasperLiveChannelId)BinaryPrimitives.ReadUInt16LittleEndian(
                    buffer.AsSpan(position + WasperLiveLinkProtocol.OffChannelId, 2));
                var elementType = (WasperLiveElementType)BinaryPrimitives.ReadUInt16LittleEndian(
                    buffer.AsSpan(position + WasperLiveLinkProtocol.OffElementType, 2));
                int branchCount = BinaryPrimitives.ReadInt32LittleEndian(
                    buffer.AsSpan(position + WasperLiveLinkProtocol.OffBranchCount, 4));
                int itemCount = BinaryPrimitives.ReadInt32LittleEndian(
                    buffer.AsSpan(position + WasperLiveLinkProtocol.OffItemCount, 4));
                int blockBytes = BinaryPrimitives.ReadInt32LittleEndian(
                    buffer.AsSpan(position + WasperLiveLinkProtocol.OffBlockBytes, 4));

                if (blockBytes < 0) break;

                if (text.Length > 0) text.Append("; ");
                text.Append(channelId).Append('/').Append(elementType).Append(' ')
                    .Append(DescribeBytes(blockBytes + WasperLiveLinkProtocol.ChannelHeaderBytes))
                    .Append(" (").Append(itemCount).Append(" items in ")
                    .Append(branchCount).Append(" branches)");

                position += WasperLiveLinkProtocol.ChannelHeaderBytes + blockBytes;
            }

            return text.Length == 0 ? "(no blocks)" : text.ToString();
        }

        /// <summary>Human-readable byte count.</summary>
        public static string DescribeBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L)
                return (bytes / (1024.0 * 1024.0)).ToString("F1",
                    System.Globalization.CultureInfo.InvariantCulture) + " MB";
            if (bytes >= 1024L)
                return (bytes / 1024.0).ToString("F1",
                    System.Globalization.CultureInfo.InvariantCulture) + " kB";
            return bytes + " B";
        }

        /// <summary>
        /// Stamps revision and timestamp into an already-serialized buffer. Both
        /// fields sit before the CRC range, so the checksum stays valid.
        /// </summary>
        public static void PatchRevisionAndTimestamp(byte[] buffer, long revision, long timestampUtc)
        {
            BinaryPrimitives.WriteInt64LittleEndian(
                buffer.AsSpan(WasperLiveLinkProtocol.OffFrameRevision, 8), revision);
            BinaryPrimitives.WriteInt64LittleEndian(
                buffer.AsSpan(WasperLiveLinkProtocol.OffFrameTimestampUtc, 8), timestampUtc);
        }

        /// <summary>
        /// Parses and fully validates a frame. Returns false rather than throwing
        /// for the failures a reader must tolerate routinely: wrong magic, a
        /// payload length that does not fit, or a CRC mismatch. All three mean the
        /// same thing in practice, that the writer swapped slots mid-copy, and the
        /// reader's answer is always to retry rather than to fail.
        /// </summary>
        public static bool TryDeserialize(
            byte[] buffer,
            int available,
            out WasperLiveFrame frame,
            out string error)
        {
            frame = null;
            error = null;

            if (buffer == null || available < WasperLiveLinkProtocol.FrameHeaderBytes)
            {
                error = "Buffer is shorter than a frame header.";
                return false;
            }

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(
                buffer.AsSpan(WasperLiveLinkProtocol.OffFrameMagic, 4));
            if (magic != WasperLiveLinkProtocol.FrameMagic)
            {
                error = "Frame magic mismatch.";
                return false;
            }

            int payloadBytes = BinaryPrimitives.ReadInt32LittleEndian(
                buffer.AsSpan(WasperLiveLinkProtocol.OffPayloadBytes, 4));
            if (payloadBytes < 0 ||
                payloadBytes > available - WasperLiveLinkProtocol.FrameHeaderBytes)
            {
                error = "Payload length " + payloadBytes + " does not fit the available " +
                        (available - WasperLiveLinkProtocol.FrameHeaderBytes) + " bytes.";
                return false;
            }

            uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
                buffer.AsSpan(WasperLiveLinkProtocol.OffPayloadCrc32, 4));
            uint actualCrc = WasperLiveLinkCrc32.Compute(
                buffer, WasperLiveLinkProtocol.FrameHeaderBytes, payloadBytes);
            if (expectedCrc != actualCrc)
            {
                error = "Payload CRC mismatch.";
                return false;
            }

            double unitScale = BinaryPrimitives.ReadDoubleLittleEndian(
                buffer.AsSpan(WasperLiveLinkProtocol.OffUnitScaleToMetres, 8));
            double originX = BinaryPrimitives.ReadDoubleLittleEndian(
                buffer.AsSpan(WasperLiveLinkProtocol.OffOrigin, 8));
            double originY = BinaryPrimitives.ReadDoubleLittleEndian(
                buffer.AsSpan(WasperLiveLinkProtocol.OffOrigin + 8, 8));
            double originZ = BinaryPrimitives.ReadDoubleLittleEndian(
                buffer.AsSpan(WasperLiveLinkProtocol.OffOrigin + 16, 8));
            int channelCount = BinaryPrimitives.ReadInt32LittleEndian(
                buffer.AsSpan(WasperLiveLinkProtocol.OffChannelCount, 4));

            if (channelCount < 0 || channelCount > 64)
            {
                error = "Implausible channel count " + channelCount + ".";
                return false;
            }

            var parsed = new WasperLiveFrame(unitScale, originX, originY, originZ)
            {
                Revision = BinaryPrimitives.ReadInt64LittleEndian(
                    buffer.AsSpan(WasperLiveLinkProtocol.OffFrameRevision, 8)),
                TimestampUtc = BinaryPrimitives.ReadInt64LittleEndian(
                    buffer.AsSpan(WasperLiveLinkProtocol.OffFrameTimestampUtc, 8))
            };

            try
            {
                var reader = new WasperBufferReader(
                    buffer, WasperLiveLinkProtocol.FrameHeaderBytes, payloadBytes);

                for (int c = 0; c < channelCount; c++)
                    parsed.Channels.Add(ReadChannel(reader));
            }
            catch (WasperLiveLinkException ex)
            {
                error = ex.Message;
                return false;
            }

            frame = parsed;
            return true;
        }

        private static WasperLiveChannel ReadChannel(WasperBufferReader reader)
        {
            var channelId = (WasperLiveChannelId)reader.ReadUInt16();
            var elementType = (WasperLiveElementType)reader.ReadUInt16();
            int branchCount = reader.ReadInt32();
            int itemCount = reader.ReadInt32();
            int blockBytes = reader.ReadInt32();

            if (branchCount < 0 || itemCount < 0 || blockBytes < 0 || blockBytes > reader.Remaining)
                throw new WasperLiveLinkException("Channel block header is out of range.");

            var channel = new WasperLiveChannel(channelId, elementType);

            var paths = new int[branchCount][];
            var counts = new int[branchCount];

            for (int b = 0; b < branchCount; b++)
            {
                int pathLength = reader.ReadUInt16();
                paths[b] = reader.ReadInt32Array(pathLength);
                reader.ReadInt32();                 // first_item_index, implied by order
                counts[b] = reader.ReadInt32();

                if (counts[b] < 0)
                    throw new WasperLiveLinkException("Negative branch item count.");
            }

            int declared = 0;
            for (int b = 0; b < branchCount; b++) declared += counts[b];
            if (declared != itemCount)
                throw new WasperLiveLinkException(
                    "Branch table declares " + declared + " items but the block header says " + itemCount + ".");

            for (int b = 0; b < branchCount; b++)
            {
                var branch = new WasperLiveBranch(paths[b]);
                for (int i = 0; i < counts[b]; i++)
                    branch.Items.Add(ReadItem(reader, elementType));
                channel.Branches.Add(branch);
            }

            return channel;
        }

        private static IWasperLiveItem ReadItem(WasperBufferReader reader, WasperLiveElementType elementType)
        {
            switch (elementType)
            {
                case WasperLiveElementType.Mesh:
                {
                    int vertexCount = reader.ReadInt32();
                    int faceCount = reader.ReadInt32();
                    uint flags = reader.ReadUInt32();

                    if (vertexCount < 0 || faceCount < 0)
                        throw new WasperLiveLinkException("Negative mesh counts.");

                    float[] vertices = reader.ReadSingleArray(vertexCount * 3);
                    int[] faces = reader.ReadInt32Array(faceCount * 4);

                    float[] normals = (flags & WasperLiveLinkProtocol.MeshFlagNormals) != 0
                        ? reader.ReadSingleArray(vertexCount * 3) : null;
                    byte[] colors = (flags & WasperLiveLinkProtocol.MeshFlagColors) != 0
                        ? reader.ReadByteArray(vertexCount * 4) : null;
                    float[] uvs = (flags & WasperLiveLinkProtocol.MeshFlagTextureCoordinates) != 0
                        ? reader.ReadSingleArray(vertexCount * 2) : null;

                    return new WasperMeshItem(vertexCount, faceCount, vertices, faces, normals, colors, uvs);
                }

                case WasperLiveElementType.Polyline:
                {
                    int pointCount = reader.ReadInt32();
                    uint flags = reader.ReadUInt32();
                    if (pointCount < 0)
                        throw new WasperLiveLinkException("Negative polyline point count.");

                    float[] points = reader.ReadSingleArray(pointCount * 3);
                    bool closed = (flags & WasperLiveLinkProtocol.PolylineFlagClosed) != 0;
                    return new WasperPolylineItem(pointCount, points, closed);
                }

                case WasperLiveElementType.Point3:
                    return new WasperPointItem(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

                case WasperLiveElementType.Float64:
                    return new WasperNumberItem(reader.ReadDouble());

                case WasperLiveElementType.Utf8String:
                    return new WasperTextItem(reader.ReadUtf8());

                default:
                    throw new WasperLiveLinkException(
                        "Unknown element type " + (ushort)elementType +
                        ". This frame was written by a newer protocol version.");
            }
        }
    }
}
