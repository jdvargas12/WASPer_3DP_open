using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace WASPer.LiveLink
{
    /// <summary>
    /// Single-writer publisher over a named memory-mapped file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no per-frame lock. The named mutex is a <i>channel claim</i>, held
    /// for the writer's whole lifetime, whose only job is to guarantee one writer
    /// per channel so a second Grasshopper document fails loudly at open time
    /// instead of interleaving frames invisibly.
    /// </para>
    /// <para>
    /// Correctness comes from double buffering plus validation after copy. The
    /// writer fills the inactive slot, publishes the slot index, then publishes the
    /// revision. Both of those are volatile writes, so the release semantics that
    /// make the payload visible before the slot index, and the slot index visible
    /// before the revision, come from the writes themselves rather than from
    /// separate fences around opaque calls.
    /// </para>
    /// <para>
    /// With two slots a writer publishing twice during one read can overwrite the
    /// slot being read. The reader's CRC and revision checks catch it, so a torn
    /// read costs a retry and never reaches the renderer.
    /// </para>
    /// </remarks>
    public sealed class WasperLiveLinkWriter : IDisposable
    {
        private readonly WasperBufferWriter _scratch = new WasperBufferWriter();
        private readonly byte[] _magicScratch = new byte[WasperLiveLinkProtocol.ControlMagic.Length];

        private Mutex _mutex;
        private WasperMappedRegion _region;
        private bool _ownsMutex;
        private int _activeSlot;
        private long _revision;
        private bool _disposed;

        /// <summary>Claims the channel and opens or adopts its mapping.</summary>
        public WasperLiveLinkWriter(
            string channel = WasperLiveLinkProtocol.DefaultChannel,
            int slotBytes = WasperLiveLinkProtocol.DefaultSlotBytes,
            bool global = false)
        {
            WasperLiveLinkProtocol.ValidateChannelName(channel);

            Channel = WasperLiveLinkProtocol.NormalizeChannel(channel);
            IsGlobalNamespace = global;
            MappingName = WasperLiveLinkProtocol.MappingName(Channel, global);
            MutexName = WasperLiveLinkProtocol.MutexName(Channel, global);
            SlotBytes = WasperLiveLinkProtocol.ClampSlotBytes(slotBytes);

            ClaimChannel();

            try
            {
                OpenOrCreateMapping();
            }
            catch
            {
                ReleaseMutex();
                throw;
            }
        }

        /// <summary>Channel suffix, with any redundant prefix stripped.</summary>
        public string Channel { get; }
        public bool IsGlobalNamespace { get; }
        public string MappingName { get; }
        /// <summary>Fully qualified channel-ownership mutex name.</summary>
        public string MutexName { get; }

        /// <summary>Effective slot size. May differ from the requested size when an
        /// existing mapping was adopted from a crashed writer.</summary>
        public int SlotBytes { get; private set; }

        /// <summary>True when the previous writer died and this one took the channel over.</summary>
        public bool RecoveredAbandonedChannel { get; private set; }

        /// <summary>True when an existing mapping was adopted rather than created.</summary>
        public bool AdoptedExistingMapping { get; private set; }

        public long Revision => _revision;

        public int MaxPayloadBytes => WasperLiveLinkProtocol.MaxPayloadBytes(SlotBytes);

        /// <summary>Bytes written by the most recent successful publish.</summary>
        public int LastFrameBytes { get; private set; }

        private void ClaimChannel()
        {
            _mutex = new Mutex(false, MutexName, out _);

            try
            {
                _ownsMutex = _mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // The previous writer died, typically a Rhino crash, and may have
                // left a half-written slot behind. We take ownership and rebuild
                // the control header below.
                _ownsMutex = true;
                RecoveredAbandonedChannel = true;
            }

            if (!_ownsMutex)
            {
                _mutex.Dispose();
                _mutex = null;
                throw new WasperLiveLinkException(
                    "Channel '" + Channel + "' is already owned by another publisher. " +
                    "Mutex: " + MutexName + ". Close the other Grasshopper document, or " +
                    "give this component a different channel name.");
            }
        }

        private void OpenOrCreateMapping()
        {
            MemoryMappedFile map;
            bool existing;

            try
            {
                map = MemoryMappedFile.OpenExisting(MappingName, MemoryMappedFileRights.ReadWrite);
                existing = true;
            }
            catch (FileNotFoundException)
            {
                map = MemoryMappedFile.CreateNew(MappingName, WasperLiveLinkProtocol.MappingBytes(SlotBytes));
                existing = false;
            }

            AdoptedExistingMapping = existing;
            _region = new WasperMappedRegion(map, MemoryMappedFileAccess.ReadWrite);

            long capacity = _region.Capacity;
            if (capacity < WasperLiveLinkProtocol.ControlHeaderBytes + 2L * WasperLiveLinkProtocol.MinSlotBytes)
            {
                throw new WasperLiveLinkException(
                    "Mapping '" + MappingName + "' is only " + capacity + " bytes, too small for a live link.");
            }

            long previousRevision = 0;
            bool headerIsValid = existing && HasValidControlMagic();

            if (headerIsValid)
            {
                // A reader is still attached to a mapping created by a writer that
                // has since exited. The mapping cannot be resized while any handle
                // is open, so adopt its geometry rather than fight it.
                int existingSlotBytes = _region.ReadInt32(WasperLiveLinkProtocol.OffSlotBytes);
                int existingSlotCount = _region.ReadInt32(WasperLiveLinkProtocol.OffSlotCount);

                if (existingSlotCount == WasperLiveLinkProtocol.SlotCount &&
                    existingSlotBytes >= WasperLiveLinkProtocol.MinSlotBytes &&
                    WasperLiveLinkProtocol.ControlHeaderBytes + 2L * existingSlotBytes <= capacity)
                {
                    SlotBytes = existingSlotBytes;
                    previousRevision = _region.ReadInt64(WasperLiveLinkProtocol.OffRevision);
                }
                else
                {
                    headerIsValid = false;
                }
            }

            if (!headerIsValid)
            {
                long usable = capacity - WasperLiveLinkProtocol.ControlHeaderBytes;
                int fitted = (int)Math.Min(SlotBytes, usable / WasperLiveLinkProtocol.SlotCount);
                SlotBytes = WasperLiveLinkProtocol.ClampSlotBytes(fitted);
            }

            // Continue the revision sequence rather than restarting it, so an
            // attached reader sees a normal increment instead of a reset.
            _revision = previousRevision;
            _activeSlot = 0;

            WriteControlHeader();
        }

        private bool HasValidControlMagic()
        {
            _region.ReadBytes(WasperLiveLinkProtocol.OffControlMagic, _magicScratch, 0, _magicScratch.Length);

            for (int i = 0; i < _magicScratch.Length; i++)
                if (_magicScratch[i] != WasperLiveLinkProtocol.ControlMagic[i]) return false;

            return _region.ReadInt32(WasperLiveLinkProtocol.OffProtocolVersion)
                   == WasperLiveLinkProtocol.ProtocolVersion;
        }

        private void WriteControlHeader()
        {
            _region.WriteBytes(
                WasperLiveLinkProtocol.OffControlMagic,
                WasperLiveLinkProtocol.ControlMagic,
                0,
                WasperLiveLinkProtocol.ControlMagic.Length);

            _region.WriteInt32(WasperLiveLinkProtocol.OffProtocolVersion, WasperLiveLinkProtocol.ProtocolVersion);
            _region.WriteInt32(WasperLiveLinkProtocol.OffHeaderBytes, WasperLiveLinkProtocol.ControlHeaderBytes);
            _region.WriteInt32(WasperLiveLinkProtocol.OffSlotBytes, SlotBytes);
            _region.WriteInt32(WasperLiveLinkProtocol.OffSlotCount, WasperLiveLinkProtocol.SlotCount);
            _region.WriteInt32(WasperLiveLinkProtocol.OffActiveSlot, _activeSlot);
            _region.WriteInt32(WasperLiveLinkProtocol.OffWriterPid, Environment.ProcessId);
            _region.WriteInt64(WasperLiveLinkProtocol.OffRevision, _revision);
            _region.WriteInt64(WasperLiveLinkProtocol.OffWriterHeartbeatUtc, DateTime.UtcNow.Ticks);
            _region.WriteInt32(WasperLiveLinkProtocol.OffWriterState, WasperLiveLinkProtocol.WriterStateIdle);
        }

        /// <summary>
        /// Serializes and publishes a frame. Returns the new revision.
        /// </summary>
        public long Publish(WasperLiveFrame frame)
        {
            ThrowIfDisposed();

            WasperLiveLinkSerializer.Serialize(frame, _scratch);

            if (_scratch.Length > SlotBytes)
            {
                int needed = WasperLiveLinkProtocol.MinSlotBytes;
                while (needed < _scratch.Length + WasperLiveLinkProtocol.FrameHeaderBytes &&
                       needed < WasperLiveLinkProtocol.MaxSlotBytes)
                {
                    needed *= 2;
                }

                throw new WasperLiveLinkOversizeException(
                    "Serialized frame is " + WasperLiveLinkSerializer.DescribeBytes(_scratch.Length) +
                    " but the slot holds " + WasperLiveLinkSerializer.DescribeBytes(SlotBytes) +
                    ". Set the slot to " + WasperLiveLinkSerializer.DescribeBytes(needed) +
                    " or larger. Nothing was published." + Environment.NewLine +
                    "Blocks: " + WasperLiveLinkSerializer.DescribeBlocks(_scratch.Buffer, _scratch.Length) +
                    Environment.NewLine +
                    "Disconnect whichever input dominates, raise the slot size, or send heavy " +
                    "static geometry to the viewer by another route instead of re-sending it in " +
                    "every frame.",
                    _scratch.Length,
                    needed >= _scratch.Length + WasperLiveLinkProtocol.FrameHeaderBytes ? needed : 0);
            }

            long revision = _revision + 1;
            long timestamp = DateTime.UtcNow.Ticks;
            WasperLiveLinkSerializer.PatchRevisionAndTimestamp(_scratch.Buffer, revision, timestamp);

            int target = 1 - _activeSlot;

            _region.WriteInt32(WasperLiveLinkProtocol.OffWriterState, WasperLiveLinkProtocol.WriterStatePublishing);

            _region.WriteBytes(
                WasperLiveLinkProtocol.SlotOffset(target, SlotBytes),
                _scratch.Buffer,
                0,
                _scratch.Length);

            // Both of these are volatile writes. Publishing the slot has release
            // semantics, so the payload above is visible first; publishing the
            // revision then makes the slot index visible. A reader that observes
            // the new revision is guaranteed to observe the new slot.
            _region.WriteInt32(WasperLiveLinkProtocol.OffActiveSlot, target);
            _region.WriteInt64(WasperLiveLinkProtocol.OffRevision, revision);

            _region.WriteInt64(WasperLiveLinkProtocol.OffWriterHeartbeatUtc, timestamp);
            _region.WriteInt32(WasperLiveLinkProtocol.OffWriterState, WasperLiveLinkProtocol.WriterStateIdle);

            _activeSlot = target;
            _revision = revision;
            LastFrameBytes = _scratch.Length;

            frame.Revision = revision;
            frame.TimestampUtc = timestamp;

            return revision;
        }

        /// <summary>Per-block size breakdown of the most recent serialized frame.</summary>
        public string DescribeLastFrameBlocks()
        {
            return LastFrameBytes == 0
                ? string.Empty
                : WasperLiveLinkSerializer.DescribeBlocks(_scratch.Buffer, LastFrameBytes);
        }

        /// <summary>
        /// Refreshes the heartbeat without publishing, so a receiver can tell an
        /// idle writer from a dead one while <c>send</c> is false.
        /// </summary>
        public void Heartbeat()
        {
            if (_disposed || _region == null) return;
            _region.WriteInt64(WasperLiveLinkProtocol.OffWriterHeartbeatUtc, DateTime.UtcNow.Ticks);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WasperLiveLinkWriter));
        }

        private void ReleaseMutex()
        {
            if (_mutex == null) return;

            try
            {
                if (_ownsMutex) _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not the owning thread. Nothing useful to do during teardown.
            }
            finally
            {
                _mutex.Dispose();
                _mutex = null;
                _ownsMutex = false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_region != null && _region.IsValid)
                {
                    _region.WriteInt32(WasperLiveLinkProtocol.OffWriterState, WasperLiveLinkProtocol.WriterStateClosed);
                    _region.WriteInt64(WasperLiveLinkProtocol.OffWriterHeartbeatUtc, DateTime.UtcNow.Ticks);
                }
            }
            catch (Exception)
            {
                // The mapping may already be gone. Teardown continues regardless.
            }

            _region?.Dispose();
            _region = null;

            ReleaseMutex();
        }
    }
}
