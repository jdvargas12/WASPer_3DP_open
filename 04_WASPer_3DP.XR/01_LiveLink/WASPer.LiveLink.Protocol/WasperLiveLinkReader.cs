using System;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Threading;

namespace WASPer.LiveLink
{
    /// <summary>
    /// Lock-free subscriber. Polls a revision counter, and deserializes only when
    /// it changes. Keeps the last valid frame when the writer is idle, stopped, or
    /// gone, so a closed sender leaves the last picture on screen rather than an
    /// empty viewport.
    /// </summary>
    public sealed class WasperLiveLinkReader : IDisposable
    {
        private WasperMappedRegion _region;
        private readonly byte[] _magicScratch = new byte[WasperLiveLinkProtocol.ControlMagic.Length];
        private byte[] _scratch = new byte[WasperLiveLinkProtocol.FrameHeaderBytes];
        private int _slotBytes;
        private long _lastRevision;
        private bool _disposed;

        public WasperLiveLinkReader(
            string channel = WasperLiveLinkProtocol.DefaultChannel,
            bool global = false)
        {
            WasperLiveLinkProtocol.ValidateChannelName(channel);

            Channel = WasperLiveLinkProtocol.NormalizeChannel(channel);
            IsGlobalNamespace = global;
            MappingName = WasperLiveLinkProtocol.MappingName(Channel, global);
            MutexName = WasperLiveLinkProtocol.MutexName(Channel, global);
        }

        public string Channel { get; }
        public bool IsGlobalNamespace { get; }
        public string MappingName { get; }
        public string MutexName { get; }

        public bool IsConnected => _region != null;

        /// <summary>Last accepted frame, or null before the first one arrives.</summary>
        public WasperLiveFrame LastFrame { get; private set; }

        public long LastRevision => _lastRevision;

        /// <summary>Cumulative torn reads. A steadily rising count is the signal to
        /// raise the slot count from 2 to 3.</summary>
        public long TornReadCount { get; private set; }

        public int LastFrameBytes { get; private set; }

        public string LastError { get; private set; }

        /// <summary>Age of the writer's heartbeat, or null when not connected.</summary>
        public TimeSpan? WriterHeartbeatAge
        {
            get
            {
                if (_region == null) return null;
                long ticks = _region.ReadInt64(WasperLiveLinkProtocol.OffWriterHeartbeatUtc);
                long age = DateTime.UtcNow.Ticks - ticks;
                return TimeSpan.FromTicks(age < 0 ? 0 : age);
            }
        }

        public int WriterState =>
            _region == null ? WasperLiveLinkProtocol.WriterStateClosed
                            : _region.ReadInt32(WasperLiveLinkProtocol.OffWriterState);

        /// <summary>
        /// Attempts to attach. Cheap to call repeatedly: a missing mapping simply
        /// means the sender has not started yet, which is a normal state, not an error.
        /// </summary>
        public bool TryConnect()
        {
            ThrowIfDisposed();
            if (_region != null) return true;

            try
            {
                MemoryMappedFile map = MemoryMappedFile.OpenExisting(MappingName, MemoryMappedFileRights.Read);
                _region = new WasperMappedRegion(map, MemoryMappedFileAccess.Read);
            }
            catch (FileNotFoundException)
            {
                Detach();
                LastError = "No publisher on '" + MappingName + "' yet.";
                return false;
            }
            catch (UnauthorizedAccessException ex)
            {
                Detach();
                LastError =
                    "Access denied opening '" + MappingName + "'. If Rhino and this process run at " +
                    "different elevations, the Local\\ namespace resolves to two separate namespaces. " +
                    ex.Message;
                return false;
            }

            if (!HasValidControlMagic())
            {
                Detach();
                LastError = "Mapping '" + MappingName + "' exists but carries no valid WSPLINK1 header.";
                return false;
            }

            _slotBytes = _region.ReadInt32(WasperLiveLinkProtocol.OffSlotBytes);
            if (_slotBytes < WasperLiveLinkProtocol.MinSlotBytes ||
                WasperLiveLinkProtocol.ControlHeaderBytes + 2L * _slotBytes > _region.Capacity)
            {
                Detach();
                LastError = "Control header declares an implausible slot size of " + _slotBytes + " bytes.";
                return false;
            }

            LastError = null;
            return true;
        }

        private bool HasValidControlMagic()
        {
            _region.ReadBytes(WasperLiveLinkProtocol.OffControlMagic, _magicScratch, 0, _magicScratch.Length);

            for (int i = 0; i < _magicScratch.Length; i++)
                if (_magicScratch[i] != WasperLiveLinkProtocol.ControlMagic[i]) return false;

            return _region.ReadInt32(WasperLiveLinkProtocol.OffProtocolVersion)
                   == WasperLiveLinkProtocol.ProtocolVersion;
        }

        /// <summary>
        /// Checks for a new frame. Returns true only when a new revision was
        /// accepted, so callers can convert to renderer meshes on the true edge and
        /// skip the work entirely otherwise.
        /// </summary>
        public bool Poll()
        {
            ThrowIfDisposed();
            if (!TryConnect()) return false;

            long available;
            try
            {
                available = _region.ReadInt64(WasperLiveLinkProtocol.OffRevision);
            }
            catch (Exception ex)
            {
                LastError = "Lost the mapping: " + ex.Message;
                Detach();
                return false;
            }

            if (available == _lastRevision) return false;

            if (available < _lastRevision)
            {
                // The writer restarted on a freshly created mapping. Drop the cache
                // and take whatever it publishes next.
                _lastRevision = 0;
                LastFrame = null;
            }

            for (int attempt = 0; attempt < WasperLiveLinkProtocol.MaxReadAttempts; attempt++)
            {
                if (TryReadOnce(out WasperLiveFrame frame, out string error))
                {
                    LastFrame = frame;
                    _lastRevision = frame.Revision;
                    LastError = null;
                    return true;
                }

                TornReadCount++;
                LastError = error;
            }

            // Four failed attempts. Keep the previous frame; the next poll retries.
            return false;
        }

        private bool TryReadOnce(out WasperLiveFrame frame, out string error)
        {
            frame = null;

            // Volatile reads: acquire semantics mean everything the writer
            // released before publishing this revision is visible below.
            long r1 = _region.ReadInt64(WasperLiveLinkProtocol.OffRevision);
            int slot = _region.ReadInt32(WasperLiveLinkProtocol.OffActiveSlot);
            if (slot < 0 || slot >= WasperLiveLinkProtocol.SlotCount)
            {
                error = "Active slot index " + slot + " is out of range.";
                return false;
            }

            long slotOffset = WasperLiveLinkProtocol.SlotOffset(slot, _slotBytes);

            EnsureScratch(WasperLiveLinkProtocol.FrameHeaderBytes);
            _region.ReadBytes(slotOffset, _scratch, 0, WasperLiveLinkProtocol.FrameHeaderBytes);

            int payloadBytes = BitConverter.ToInt32(_scratch, WasperLiveLinkProtocol.OffPayloadBytes);
            if (payloadBytes < 0 || payloadBytes > WasperLiveLinkProtocol.MaxPayloadBytes(_slotBytes))
            {
                error = "Frame declares " + payloadBytes + " payload bytes, which does not fit a " +
                        _slotBytes + " byte slot.";
                return false;
            }

            int total = WasperLiveLinkProtocol.FrameHeaderBytes + payloadBytes;
            EnsureScratch(total);
            _region.ReadBytes(
                slotOffset + WasperLiveLinkProtocol.FrameHeaderBytes,
                _scratch,
                WasperLiveLinkProtocol.FrameHeaderBytes,
                payloadBytes);

            long r2 = _region.ReadInt64(WasperLiveLinkProtocol.OffRevision);

            if (r1 != r2)
            {
                error = "Writer swapped slots during the read.";
                return false;
            }

            if (!WasperLiveLinkSerializer.TryDeserialize(_scratch, total, out frame, out error))
                return false;

            if (frame.Revision != r1)
            {
                error = "Frame revision " + frame.Revision + " does not match control revision " + r1 + ".";
                frame = null;
                return false;
            }

            LastFrameBytes = total;
            error = null;
            return true;
        }

        private void EnsureScratch(int required)
        {
            if (_scratch.Length >= required) return;

            int capacity = _scratch.Length;
            while (capacity < required) capacity *= 2;
            Array.Resize(ref _scratch, capacity);
        }

        /// <summary>Human-readable state for the Status pin.</summary>
        public string DescribeStatus()
        {
            if (!IsConnected)
                return "Not connected. " + MappingName + ". " + (LastError ?? "Waiting for a publisher.");

            TimeSpan? age = WriterHeartbeatAge;
            string writer = WriterState == WasperLiveLinkProtocol.WriterStateClosed
                ? "writer closed"
                : age.HasValue && age.Value > TimeSpan.FromSeconds(2)
                    ? "writer stale (" + age.Value.TotalSeconds.ToString("F1") + "s)"
                    : "writer live";

            return "Connected to " + MappingName +
                   ". Revision " + _lastRevision +
                   ", " + LastFrameBytes + " bytes" +
                   ", torn reads " + TornReadCount +
                   ", " + writer +
                   (LastError == null ? string.Empty : ". Last error: " + LastError);
        }

        private void Detach()
        {
            _region?.Dispose();
            _region = null;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WasperLiveLinkReader));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Detach();
        }
    }
}
