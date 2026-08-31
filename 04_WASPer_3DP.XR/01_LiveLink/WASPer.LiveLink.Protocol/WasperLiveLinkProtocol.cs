using System;

namespace WASPer.LiveLink
{
    /// <summary>
    /// WSPLINK1 wire format. This class is the authority for the layout: every
    /// offset and size below is a named constant, and nothing else in the
    /// solution may hardcode a literal offset.
    /// </summary>
    /// <remarks>
    /// <para>Little-endian throughout. Readers must not assume struct packing.</para>
    ///
    /// <para><b>Control header, 128 bytes at offset 0 of the mapping.</b></para>
    /// <code>
    /// off  size  field
    ///   0     8  magic, ASCII "WSPLINK1"
    ///   8     4  protocol_version, int32, currently 1
    ///  12     4  header_bytes, int32, 128
    ///  16     4  slot_bytes, int32
    ///  20     4  slot_count, int32, 2
    ///  24     4  active_slot, int32
    ///  28     4  writer_pid, int32
    ///  32     8  revision, int64, monotonic
    ///  40     8  writer_heartbeat_utc, int64 ticks
    ///  48     4  writer_state, int32: 0 idle, 1 publishing, 2 closed
    ///  52    76  reserved, zero
    /// </code>
    ///
    /// <para><b>Frame header, 72 bytes at the start of each slot.</b></para>
    /// <code>
    /// off  size  field
    ///   0     4  frame_magic, ASCII "WSPF"
    ///   4     4  frame_flags, uint32, reserved, zero
    ///   8     8  revision, int64, equals the control revision it was published under
    ///  16     8  timestamp_utc, int64 ticks
    ///  24     8  unit_scale_to_metres, float64
    ///  32    24  origin, float64[3]
    ///  56     4  channel_count, int32
    ///  60     4  payload_bytes, int32, bytes following this header
    ///  64     4  payload_crc32, uint32, over exactly payload_bytes
    ///  68     4  reserved, zero
    /// </code>
    ///
    /// <para><b>Channel block header, 16 bytes.</b></para>
    /// <code>
    /// off  size  field
    ///   0     2  channel_id, uint16: 1 Geometry, 2 Points, 3 Numbers, 4 Text
    ///   2     2  element_type, uint16
    ///   4     4  branch_count, int32
    ///   8     4  item_count, int32
    ///  12     4  block_bytes, int32, bytes following this header
    /// </code>
    ///
    /// <para>Branch table entry, variable length, repeated branch_count times:</para>
    /// <code>
    /// path_length        uint16
    /// path_components    int32[path_length]
    /// first_item_index   int32
    /// item_count         int32
    /// </code>
    ///
    /// <para>Items follow the branch table contiguously. Encodings:</para>
    /// <code>
    /// Mesh
    ///   vertex_count   int32
    ///   face_count     int32
    ///   flags          uint32   bit0 normals, bit1 colors, bit2 uvs
    ///   vertices       float32[3 * vertex_count]   offsets from origin
    ///   faces          int32[4 * face_count]       quads native, triangle as A,B,C,C
    ///   normals        float32[3 * vertex_count]   if bit0
    ///   colors         uint8[4 * vertex_count]     RGBA, if bit1
    ///   uvs            float32[2 * vertex_count]   if bit2
    ///
    /// Polyline
    ///   point_count    int32
    ///   flags          uint32   bit0 closed
    ///   points         float32[3 * point_count]    offsets from origin
    ///
    /// Point3
    ///   point          float32[3]                  offset from origin
    ///
    /// Float64
    ///   value          float64
    ///
    /// Utf8String
    ///   byte_count     int32
    ///   bytes          uint8[byte_count]
    /// </code>
    ///
    /// <para>
    /// All vertex and point coordinates are float32 <i>offsets from the frame
    /// origin</i>, which is float64. This is what makes float32 safe: a model at
    /// real survey coordinates would lose millimetre precision in bare float32.
    /// Any code path that writes coordinates without subtracting the origin is a
    /// bug that only shows up on site-coordinate models.
    /// </para>
    ///
    /// <para>
    /// Coordinates stay in Rhino model units and Rhino's Z-up orientation. The
    /// sender does not rotate, scale, or convert to metres. It reports
    /// unit_scale_to_metres and the receiver decides.
    /// </para>
    /// </remarks>
    public static class WasperLiveLinkProtocol
    {
        /// <summary>ASCII "WSPLINK1", the first eight bytes of the mapping.</summary>
        public static readonly byte[] ControlMagic =
        {
            (byte)'W', (byte)'S', (byte)'P', (byte)'L',
            (byte)'I', (byte)'N', (byte)'K', (byte)'1'
        };

        /// <summary>ASCII "WSPF" as a little-endian uint32.</summary>
        public const uint FrameMagic = 0x46505357u;

        public const int ProtocolVersion = 1;

        public const int ControlHeaderBytes = 128;
        public const int FrameHeaderBytes = 72;
        public const int ChannelHeaderBytes = 16;

        public const int SlotCount = 2;

        public const int MinSlotBytes = 256 * 1024;

        /// <summary>
        /// Default slot size: the maximum, so a frame never fails to publish for
        /// want of a setting.
        /// </summary>
        /// <remarks>
        /// The mapping is 128 + 2 x slot_bytes, so this reserves about 1 GB. It is
        /// page-file backed and pages are only made resident when touched, so a
        /// small frame costs small memory — but it does count against the system
        /// commit limit for the full amount. Lower it in the component's right-click
        /// menu on a machine where that matters.
        /// </remarks>
        public const int DefaultSlotBytes = MaxSlotBytes;
        /// <summary>
        /// Upper bound on a menu-selected slot.
        /// </summary>
        /// <remarks>
        /// Large slots work, but stop being "live". The mapping is
        /// 128 + 2 x slot_bytes, page-file backed and committed lazily, so the
        /// address space is cheap; the cost is per frame. Serializing tens of
        /// megabytes of mesh dominates, and a 32 MB payload lands in the hundreds
        /// of milliseconds — a viewer you can look at rather than interact with.
        ///
        /// The ceiling exists to make that a deliberate choice rather than a
        /// surprise, not to stop you. When a frame is mostly static geometry, a
        /// second channel published once is nearly always the better answer than a
        /// large slot refreshed continuously.
        /// </remarks>
        public const int MaxSlotBytes = 512 * 1024 * 1024;

        /// <summary>Bounded retry count for a torn read before the reader gives up
        /// and keeps its previous frame.</summary>
        public const int MaxReadAttempts = 4;

        // Control header offsets.
        public const int OffControlMagic = 0;
        public const int OffProtocolVersion = 8;
        public const int OffHeaderBytes = 12;
        public const int OffSlotBytes = 16;
        public const int OffSlotCount = 20;
        public const int OffActiveSlot = 24;
        public const int OffWriterPid = 28;
        public const int OffRevision = 32;
        public const int OffWriterHeartbeatUtc = 40;
        public const int OffWriterState = 48;

        // Frame header offsets, relative to the start of a slot.
        public const int OffFrameMagic = 0;
        public const int OffFrameFlags = 4;
        public const int OffFrameRevision = 8;
        public const int OffFrameTimestampUtc = 16;
        public const int OffUnitScaleToMetres = 24;
        public const int OffOrigin = 32;
        public const int OffChannelCount = 56;
        public const int OffPayloadBytes = 60;
        public const int OffPayloadCrc32 = 64;

        // Channel block header offsets, relative to the start of a block.
        public const int OffChannelId = 0;
        public const int OffElementType = 2;
        public const int OffBranchCount = 4;
        public const int OffItemCount = 8;
        public const int OffBlockBytes = 12;

        // Mesh item flag bits.
        public const uint MeshFlagNormals = 1u << 0;
        public const uint MeshFlagColors = 1u << 1;
        public const uint MeshFlagTextureCoordinates = 1u << 2;

        // Polyline item flag bits.
        public const uint PolylineFlagClosed = 1u << 0;

        // Writer states.
        public const int WriterStateIdle = 0;
        public const int WriterStatePublishing = 1;
        public const int WriterStateClosed = 2;

        /// <summary>
        /// Channel names are suffixes. MappingName and MutexName supply the
        /// "WASPer.Live." prefix, so a channel called "WASPer.Live.0" would
        /// produce Local\\WASPer.Live.WASPer.Live.0.map.
        /// </summary>
        public const string DefaultChannel = "0";

        /// <summary>Float64 values per branch on the PathAttributes channel.</summary>
        public const int PathAttributeCount = 6;

        /// <summary>
        /// Order of the PathAttributes tuple. Absent values are written as -1
        /// rather than omitted, so the tuple is fixed width and a receiver can
        /// index it without parsing.
        /// </summary>
        /// <remarks>
        /// Per-point width and speed are deliberately not here. They would multiply
        /// the attribute payload by the point count, and nothing in the viewer needs
        /// them yet. When something does, they belong on a new channel id rather
        /// than widened into this tuple, which keeps existing receivers working.
        /// </remarks>
        /// <summary>Values packed per path point on the PathPointSection channel.</summary>
        /// <remarks>
        /// Packed into the x, y and z slots of a polyline point, which is a small
        /// abuse of the type but keeps the wire format free of a new element kind and
        /// keeps the data as one contiguous float32 run. A receiver that does not
        /// know this channel skips it cleanly, as it would any other.
        /// </remarks>
        public static readonly string[] PathPointAttributeNames =
        {
            "layer_width",   // deposited width at this point, -1 when absent
            "layer_height",  // layer height at this point, -1 when absent
            "print_speed"    // speed at this point, -1 when absent
        };

        public static readonly string[] PathAttributeNames =
        {
            "role",          // 0 undefined, 1 shell, 2 infill, 3 partition, 4 support, 5 transition
            "stroke_id",     // continuity group, -1 when absent
            "layer_height",  // model units, -1 when absent
            "layer_width",   // flow-adjusted if available, else nominal, -1 when absent
            "print_speed",   // branch mean, -1 when absent
            "closed"         // 1 when the branch closes on itself, else 0
        };

        /// <summary>Total mapping size for a given slot size.</summary>
        public static long MappingBytes(int slotBytes)
        {
            return ControlHeaderBytes + (long)SlotCount * slotBytes;
        }

        /// <summary>Byte offset of a slot within the mapping.</summary>
        public static long SlotOffset(int slotIndex, int slotBytes)
        {
            return ControlHeaderBytes + (long)slotIndex * slotBytes;
        }

        /// <summary>Largest serialized payload that fits in a slot of this size.</summary>
        public static int MaxPayloadBytes(int slotBytes)
        {
            return slotBytes - FrameHeaderBytes;
        }

        /// <summary>
        /// Fully-qualified mapping name. Printed verbatim in diagnostics: if Rhino
        /// and Gamma run at different elevations, "Local\" resolves to two separate
        /// namespaces and the link silently never connects, with no error on either
        /// side. Seeing both names side by side is the only cheap way to spot it.
        /// </summary>
        public static string MappingName(string channel, bool global)
        {
            return (global ? "Global\\" : "Local\\") + Prefix + NormalizeChannel(channel) + ".map";
        }

        /// <summary>Fully-qualified channel-ownership mutex name.</summary>
        public static string MutexName(string channel, bool global)
        {
            return (global ? "Global\\" : "Local\\") + Prefix + NormalizeChannel(channel) + ".owner";
        }

        private const string Prefix = "WASPer.Live.";

        /// <summary>
        /// Strips a redundant "WASPer.Live." prefix from a channel name.
        /// </summary>
        /// <remarks>
        /// The prefix is added by MappingName and MutexName, so a channel called
        /// "WASPer.Live.0" would otherwise produce
        /// Local\WASPer.Live.WASPer.Live.0.map. Definitions saved while
        /// DefaultChannel still carried the prefix keep that literal string in the
        /// file, so normalizing here means those keep working untouched instead of
        /// silently opening a different channel from a freshly placed component.
        /// </remarks>
        public static string NormalizeChannel(string channel)
        {
            if (string.IsNullOrWhiteSpace(channel)) return DefaultChannel;

            channel = channel.Trim();
            while (channel.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                channel = channel.Substring(Prefix.Length);

            return channel.Length == 0 ? DefaultChannel : channel;
        }

        public static void ValidateChannelName(string channel)
        {
            if (string.IsNullOrWhiteSpace(channel))
                throw new ArgumentException("Channel name must not be empty.", nameof(channel));

            if (channel.IndexOf('\\') >= 0)
                throw new ArgumentException(
                    "Channel name must not contain a backslash; the namespace prefix is added automatically.",
                    nameof(channel));

            if (channel.Length > 96)
                throw new ArgumentException("Channel name must be 96 characters or fewer.", nameof(channel));
        }

        public static int ClampSlotBytes(int requested)
        {
            if (requested < MinSlotBytes) return MinSlotBytes;
            if (requested > MaxSlotBytes) return MaxSlotBytes;
            return requested;
        }
    }

    /// <summary>
    /// Channel identifiers. A block carries exactly one element type, but a
    /// channel id may appear in several blocks: Gc07 display geometry is routinely
    /// meshes and print-path polylines together.
    /// </summary>
    /// <remarks>
    /// Adding an id is forward-compatible and does not change protocol_version.
    /// Parsing depends only on element_type, so a reader that does not recognise
    /// an id can still skip its block cleanly.
    /// </remarks>
    public enum WasperLiveChannelId : ushort
    {
        Geometry = 1,
        Points = 2,
        Numbers = 3,
        Text = 4,

        /// <summary>
        /// Per-branch print-path attributes, Float64, branch-aligned with the
        /// Geometry polyline block so a receiver can zip the two by branch path.
        /// Exactly <see cref="WasperLiveLinkProtocol.PathAttributeCount"/> values
        /// per branch, in the order given by
        /// <see cref="WasperLiveLinkProtocol.PathAttributeNames"/>.
        ///
        /// These live on their own id rather than on Numbers because Numbers
        /// carries the Gc08 camera packet: merging them would leave a receiver
        /// unable to tell a bead width from a focal length.
        /// </summary>
        PathAttributes = 5,

        /// <summary>
        /// Job-level print-path metadata, UTF-8, at branch {0}. Currently one item,
        /// the WasperPrintPath ContentSignature, so a viewer can correlate a live
        /// frame with a .wasperxr package. Separate from Text for the same reason
        /// PathAttributes is separate from Numbers.
        /// </summary>
        PathMeta = 6,

        /// <summary>
        /// Per-point print-path attributes, Polyline-encoded, branch-aligned with
        /// the Geometry polyline block. Exactly one item per branch, whose point
        /// count matches that branch's polyline, packing three values per path
        /// point in the order given by
        /// <see cref="WasperLiveLinkProtocol.PathPointAttributeNames"/>.
        ///
        /// Carried as a Polyline rather than a Float64 run purely so it stays a
        /// flat float32 array: at three values per point across tens of thousands
        /// of points, float64 would double the cost for precision that a bead
        /// section does not need.
        /// </summary>
        PathPointSection = 7,

        /// <summary>
        /// Per-point layer normal, taken from the wsp_path PtPlanes, Polyline-encoded
        /// and branch-aligned exactly as <see cref="PathPointSection"/> is. Unit
        /// vectors, not positions: the origin offset is not applied to them.
        ///
        /// This is what makes non-planar layers render correctly. Without it a
        /// viewer has to assume the bead's up direction is world Z, which is only
        /// true for flat layers.
        /// </summary>
        PathPointNormal = 8,

        /// <summary>
        /// One RGBA colour per mesh, Float64, four values per item, branch-aligned
        /// with the Geometry mesh block.
        /// </summary>
        /// <remarks>
        /// A separate channel rather than per-vertex colours on the meshes
        /// themselves. A colour that is uniform per mesh costs four numbers here
        /// against four bytes per vertex there — on a Pp04 preview that is the
        /// difference between a few kilobytes and a few megabytes, for identical
        /// output.
        /// </remarks>
        MeshColor = 9
    }

    /// <summary>
    /// Element types. Integer and boolean are deliberately absent: everything
    /// currently needed is expressible as Float64 or a string, and an unused type
    /// is a type that gets specified wrong. Add them when something needs them.
    /// </summary>
    public enum WasperLiveElementType : ushort
    {
        Mesh = 1,
        Polyline = 2,
        Point3 = 3,
        Float64 = 4,
        Utf8String = 5
    }

    /// <summary>Raised for protocol, capacity, and channel-ownership failures.</summary>
    public class WasperLiveLinkException : Exception
    {
        public WasperLiveLinkException(string message) : base(message) { }
        public WasperLiveLinkException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// A frame did not fit its slot. Carries the slot size that would hold it, so a
    /// caller can reopen at that size and retry rather than making the user work it
    /// out and set it by hand.
    /// </summary>
    public sealed class WasperLiveLinkOversizeException : WasperLiveLinkException
    {
        public WasperLiveLinkOversizeException(string message, int frameBytes, int requiredSlotBytes)
            : base(message)
        {
            FrameBytes = frameBytes;
            RequiredSlotBytes = requiredSlotBytes;
        }

        public int FrameBytes { get; }

        /// <summary>Zero when even the maximum slot would not hold the frame.</summary>
        public int RequiredSlotBytes { get; }
    }
}
