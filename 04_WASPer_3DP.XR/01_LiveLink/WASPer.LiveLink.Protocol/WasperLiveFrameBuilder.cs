using System;
using System.Collections.Generic;

namespace WASPer.LiveLink
{
    /// <summary>
    /// The only public way to construct a frame. Every coordinate enters as a
    /// world-space double and leaves as a float32 offset from the frame origin,
    /// so there is no API surface through which a caller can write unoffset
    /// coordinates. That is deliberate: the resulting jitter appears only on
    /// models placed at real survey coordinates, which is exactly the kind of bug
    /// that survives local testing.
    /// </summary>
    public sealed class WasperLiveFrameBuilder
    {
        private readonly double _anchorX;
        private readonly double _anchorY;
        private readonly double _anchorZ;
        private readonly double _scale;
        private readonly WasperLiveFrame _frame;
        private readonly Dictionary<long, WasperLiveChannel> _channels;
        private readonly Dictionary<long, Dictionary<string, WasperLiveBranch>> _branches;

        /// <summary>
        /// The origin is required up front rather than settable later, so that no
        /// ordering mistake can leave geometry offset against the wrong origin.
        /// </summary>
        /// <param name="geometryScale">
        /// Applied where the origin is subtracted, so coordinates are scaled about
        /// the anchor rather than about the world origin. Use 1.0 to send model
        /// units unchanged, or 0.001 to convert millimetres to metres, in which
        /// case <paramref name="unitScaleToMetres"/> must be 1.0 — the geometry is
        /// already in metres and declaring otherwise would have the receiver scale
        /// it a second time.
        /// </param>
        public WasperLiveFrameBuilder(
            double unitScaleToMetres,
            double originX,
            double originY,
            double originZ,
            double geometryScale = 1.0)
        {
            if (!IsFinite(unitScaleToMetres) || unitScaleToMetres <= 0.0)
                throw new ArgumentOutOfRangeException(
                    nameof(unitScaleToMetres), "Unit scale must be finite and positive.");

            if (!IsFinite(originX) || !IsFinite(originY) || !IsFinite(originZ))
                throw new ArgumentException("Origin must be finite.");

            if (!IsFinite(geometryScale) || geometryScale <= 0.0)
                throw new ArgumentOutOfRangeException(
                    nameof(geometryScale), "Geometry scale must be finite and positive.");

            _anchorX = originX;
            _anchorY = originY;
            _anchorZ = originZ;
            _scale = geometryScale;

            // The header origin is the anchor expressed in output units, so a
            // receiver reconstructing origin + local lands on scaled world
            // coordinates: anchor*s + (p - anchor)*s == p*s.
            _frame = new WasperLiveFrame(
                unitScaleToMetres,
                originX * geometryScale,
                originY * geometryScale,
                originZ * geometryScale);
            _channels = new Dictionary<long, WasperLiveChannel>();
            _branches = new Dictionary<long, Dictionary<string, WasperLiveBranch>>();
        }

        public WasperLiveFrame Build() => _frame;

        /// <summary>
        /// Factor applied to coordinates. Lengths carried as attributes rather than
        /// as geometry — bead width, layer height — must be multiplied by this too,
        /// or a 6 mm bead becomes a 6 metre one.
        /// </summary>
        public double GeometryScale => _scale;

        /// <summary>
        /// Suggested origin for a bounding box: the centre, rounded to whole model
        /// units so it stays stable across small camera or geometry nudges and does
        /// not churn the cached geometry block.
        /// </summary>
        public static void SuggestOrigin(
            double minX, double minY, double minZ,
            double maxX, double maxY, double maxZ,
            out double originX, out double originY, out double originZ)
        {
            originX = Math.Round((minX + maxX) * 0.5);
            originY = Math.Round((minY + maxY) * 0.5);
            originZ = Math.Round((minZ + maxZ) * 0.5);

            if (!IsFinite(originX)) originX = 0.0;
            if (!IsFinite(originY)) originY = 0.0;
            if (!IsFinite(originZ)) originZ = 0.0;
        }

        public void AddMesh(
            int[] path,
            double[] worldVertices,
            int[] faces,
            float[] normals = null,
            byte[] colors = null,
            float[] textureCoordinates = null)
        {
            if (worldVertices == null) throw new ArgumentNullException(nameof(worldVertices));
            if (faces == null) throw new ArgumentNullException(nameof(faces));

            if (worldVertices.Length % 3 != 0)
                throw new ArgumentException("Vertex array length must be a multiple of 3.", nameof(worldVertices));
            if (faces.Length % 4 != 0)
                throw new ArgumentException(
                    "Face array length must be a multiple of 4; a triangle is encoded as A,B,C,C.", nameof(faces));

            int vertexCount = worldVertices.Length / 3;
            int faceCount = faces.Length / 4;

            if (normals != null && normals.Length != vertexCount * 3)
                throw new ArgumentException("Normal array must hold 3 floats per vertex.", nameof(normals));
            if (colors != null && colors.Length != vertexCount * 4)
                throw new ArgumentException("Colour array must hold 4 bytes per vertex.", nameof(colors));
            if (textureCoordinates != null && textureCoordinates.Length != vertexCount * 2)
                throw new ArgumentException("Texture coordinate array must hold 2 floats per vertex.", nameof(textureCoordinates));

            for (int i = 0; i < faces.Length; i++)
            {
                if (faces[i] < 0 || faces[i] >= vertexCount)
                    throw new ArgumentException(
                        "Face index " + faces[i] + " is outside the vertex range 0.." + (vertexCount - 1) + ".",
                        nameof(faces));
            }

            var local = new float[worldVertices.Length];
            for (int i = 0; i < vertexCount; i++)
            {
                local[i * 3 + 0] = (float)((worldVertices[i * 3 + 0] - _anchorX) * _scale);
                local[i * 3 + 1] = (float)((worldVertices[i * 3 + 1] - _anchorY) * _scale);
                local[i * 3 + 2] = (float)((worldVertices[i * 3 + 2] - _anchorZ) * _scale);
            }

            var copiedFaces = new int[faces.Length];
            Array.Copy(faces, copiedFaces, faces.Length);

            Add(WasperLiveChannelId.Geometry, WasperLiveElementType.Mesh, path,
                new WasperMeshItem(vertexCount, faceCount, local, copiedFaces, normals, colors, textureCoordinates));
        }

        public void AddPolyline(int[] path, double[] worldPoints, bool closed)
        {
            if (worldPoints == null) throw new ArgumentNullException(nameof(worldPoints));
            if (worldPoints.Length % 3 != 0)
                throw new ArgumentException("Point array length must be a multiple of 3.", nameof(worldPoints));

            int pointCount = worldPoints.Length / 3;
            var local = new float[worldPoints.Length];
            for (int i = 0; i < pointCount; i++)
            {
                local[i * 3 + 0] = (float)((worldPoints[i * 3 + 0] - _anchorX) * _scale);
                local[i * 3 + 1] = (float)((worldPoints[i * 3 + 1] - _anchorY) * _scale);
                local[i * 3 + 2] = (float)((worldPoints[i * 3 + 2] - _anchorZ) * _scale);
            }

            Add(WasperLiveChannelId.Geometry, WasperLiveElementType.Polyline, path,
                new WasperPolylineItem(pointCount, local, closed));
        }

        public void AddPoint(int[] path, double worldX, double worldY, double worldZ)
        {
            Add(WasperLiveChannelId.Points, WasperLiveElementType.Point3, path,
                new WasperPointItem(
                    (float)((worldX - _anchorX) * _scale),
                    (float)((worldY - _anchorY) * _scale),
                    (float)((worldZ - _anchorZ) * _scale)));
        }

        public void AddNumber(int[] path, double value)
        {
            Add(WasperLiveChannelId.Numbers, WasperLiveElementType.Float64, path, new WasperNumberItem(value));
        }

        public void AddText(int[] path, string value)
        {
            Add(WasperLiveChannelId.Text, WasperLiveElementType.Utf8String, path, new WasperTextItem(value));
        }

        /// <summary>
        /// Writes the fixed-width print-path attribute tuple for one branch. Call
        /// once per Geometry polyline branch and with the same path, so a receiver
        /// can zip attributes to polylines by branch path alone.
        /// </summary>
        public void AddPathAttributes(
            int[] path,
            double role,
            double strokeId,
            double layerHeight,
            double layerWidth,
            double printSpeed,
            bool closed)
        {
            Add(WasperLiveChannelId.PathAttributes, WasperLiveElementType.Float64, path, new WasperNumberItem(role));
            Add(WasperLiveChannelId.PathAttributes, WasperLiveElementType.Float64, path, new WasperNumberItem(strokeId));
            Add(WasperLiveChannelId.PathAttributes, WasperLiveElementType.Float64, path, new WasperNumberItem(layerHeight));
            Add(WasperLiveChannelId.PathAttributes, WasperLiveElementType.Float64, path, new WasperNumberItem(layerWidth));
            Add(WasperLiveChannelId.PathAttributes, WasperLiveElementType.Float64, path, new WasperNumberItem(printSpeed));
            Add(WasperLiveChannelId.PathAttributes, WasperLiveElementType.Float64, path, new WasperNumberItem(closed ? 1.0 : 0.0));
        }

        /// <summary>
        /// Per-point section for one branch: width, height and speed packed three to
        /// a point. Values are lengths, so they are scaled with the geometry; -1
        /// marks absent and is left alone.
        /// </summary>
        public void AddPathPointSection(int[] path, double[] widths, double[] heights, double[] speeds)
        {
            if (widths == null) throw new ArgumentNullException(nameof(widths));

            int count = widths.Length;
            var packed = new float[count * 3];

            for (int i = 0; i < count; i++)
            {
                double w = widths[i];
                double h = heights != null && i < heights.Length ? heights[i] : -1.0;
                double s = speeds != null && i < speeds.Length ? speeds[i] : -1.0;

                packed[i * 3 + 0] = (float)(w > 0.0 ? w * _scale : -1.0);
                packed[i * 3 + 1] = (float)(h > 0.0 ? h * _scale : -1.0);
                packed[i * 3 + 2] = (float)s;
            }

            Add(WasperLiveChannelId.PathPointSection, WasperLiveElementType.Polyline, path,
                new WasperPolylineItem(count, packed, false));
        }

        /// <summary>
        /// Per-point layer normal for one branch, from the wsp_path PtPlanes.
        /// </summary>
        /// <remarks>
        /// Directions, not positions: neither the origin offset nor the scale is
        /// applied, since both would change a unit vector into something that is no
        /// longer one.
        /// </remarks>
        public void AddPathPointNormals(int[] path, double[] normals)
        {
            if (normals == null) throw new ArgumentNullException(nameof(normals));
            if (normals.Length % 3 != 0)
                throw new ArgumentException("Normal array length must be a multiple of 3.", nameof(normals));

            int count = normals.Length / 3;
            var packed = new float[normals.Length];

            for (int i = 0; i < normals.Length; i++)
                packed[i] = (float)normals[i];

            Add(WasperLiveChannelId.PathPointNormal, WasperLiveElementType.Polyline, path,
                new WasperPolylineItem(count, packed, false));
        }

        /// <summary>
        /// Colour for one mesh, as RGBA in 0..1, written in the same branch and the
        /// same order as the mesh it belongs to.
        /// </summary>
        public void AddMeshColor(int[] path, double r, double g, double b, double a)
        {
            Add(WasperLiveChannelId.MeshColor, WasperLiveElementType.Float64, path, new WasperNumberItem(r));
            Add(WasperLiveChannelId.MeshColor, WasperLiveElementType.Float64, path, new WasperNumberItem(g));
            Add(WasperLiveChannelId.MeshColor, WasperLiveElementType.Float64, path, new WasperNumberItem(b));
            Add(WasperLiveChannelId.MeshColor, WasperLiveElementType.Float64, path, new WasperNumberItem(a));
        }

        /// <summary>Job-level print-path metadata, conventionally at branch {0}.</summary>
        public void AddPathMeta(int[] path, string value)
        {
            Add(WasperLiveChannelId.PathMeta, WasperLiveElementType.Utf8String, path, new WasperTextItem(value));
        }

        /// <summary>
        /// Appends a channel block built for an earlier frame, skipping conversion
        /// entirely. This is how a camera-only update avoids re-meshing: the caller
        /// keeps the geometry blocks from the previous frame and only rebuilds the
        /// numbers.
        /// </summary>
        /// <remarks>
        /// The caller is responsible for confirming the block was built against
        /// <i>this</i> frame's origin. Reusing a block across an origin change puts
        /// the geometry in the wrong place, so callers must track the origin they
        /// converted against and discard the cache when it moves. The publisher in
        /// WASPer_3DP is the only intended caller.
        /// </remarks>
        public void AddPrebuiltChannel(WasperLiveChannel channel)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));

            long key = BlockKey(channel.ChannelId, channel.ElementType);
            if (_channels.ContainsKey(key))
            {
                throw new WasperLiveLinkException(
                    "A " + channel.ElementType + " block already exists on channel " +
                    channel.ChannelId + " in this frame.");
            }

            _channels.Add(key, channel);

            var map = new Dictionary<string, WasperLiveBranch>(StringComparer.Ordinal);
            for (int i = 0; i < channel.Branches.Count; i++)
                map[PathKey(channel.Branches[i].Path)] = channel.Branches[i];
            _branches.Add(key, map);

            _frame.Channels.Add(channel);
        }

        /// <summary>
        /// Registers a branch that carries no items, so empty branches survive the
        /// round trip instead of silently collapsing the tree.
        /// </summary>
        public void AddEmptyBranch(WasperLiveChannelId channelId, WasperLiveElementType elementType, int[] path)
        {
            GetBranch(channelId, elementType, path);
        }

        private void Add(
            WasperLiveChannelId channelId,
            WasperLiveElementType elementType,
            int[] path,
            IWasperLiveItem item)
        {
            GetBranch(channelId, elementType, path).Items.Add(item);
        }

        private WasperLiveBranch GetBranch(
            WasperLiveChannelId channelId,
            WasperLiveElementType elementType,
            int[] path)
        {
            // A block holds exactly one element type, but a channel id may appear
            // in several blocks. That matters for real WASPer output: Gc07 display
            // geometry is routinely meshes and print-path polylines together, and
            // forcing one type per channel would mean tubing every path.
            long key = BlockKey(channelId, elementType);

            if (!_channels.TryGetValue(key, out WasperLiveChannel channel))
            {
                channel = new WasperLiveChannel(channelId, elementType);
                _channels.Add(key, channel);
                _branches.Add(key, new Dictionary<string, WasperLiveBranch>(StringComparer.Ordinal));
                _frame.Channels.Add(channel);
            }

            int[] safePath = path ?? Array.Empty<int>();
            string pathKey = PathKey(safePath);

            Dictionary<string, WasperLiveBranch> map = _branches[key];
            if (!map.TryGetValue(pathKey, out WasperLiveBranch branch))
            {
                var copiedPath = new int[safePath.Length];
                Array.Copy(safePath, copiedPath, safePath.Length);
                branch = new WasperLiveBranch(copiedPath);
                map.Add(pathKey, branch);
                channel.Branches.Add(branch);
            }

            return branch;
        }

        private static long BlockKey(WasperLiveChannelId channelId, WasperLiveElementType elementType)
        {
            return ((long)(ushort)channelId << 16) | (ushort)elementType;
        }

        private static string PathKey(int[] path)
        {
            if (path.Length == 0) return string.Empty;
            var parts = new string[path.Length];
            for (int i = 0; i < path.Length; i++)
                parts[i] = path[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            return string.Join(";", parts);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
