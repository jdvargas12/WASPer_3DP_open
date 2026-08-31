using System;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;

namespace WASPer.LiveLink
{
    /// <summary>
    /// A mapped view held open with its base pointer acquired, so frames move with
    /// <see cref="Buffer.MemoryCopy"/> instead of through
    /// <see cref="MemoryMappedViewAccessor"/>'s element-wise marshalling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The accessor path measured roughly 7.7 ms per MB, which meant an 8 MB frame
    /// cost about 62 ms — past interactive before the frame budget itself was even
    /// reached. That was an artifact of the API, not of the hardware.
    /// </para>
    /// <para>
    /// The pointer is acquired once for the lifetime of this object and released on
    /// dispose, rather than per frame: <c>AcquirePointer</c> takes a reference count
    /// on the safe handle and doing that thirty times a second is pure overhead.
    /// </para>
    /// <para>
    /// Control-header fields are read and written through <see cref="Volatile"/>,
    /// which gives the acquire and release semantics the publish sequence depends
    /// on directly, rather than fencing separately around opaque accessor calls.
    /// All header fields are naturally aligned and the mapping base is page
    /// aligned, so those reads and writes are atomic.
    /// </para>
    /// </remarks>
    internal sealed unsafe class WasperMappedRegion : IDisposable
    {
        private MemoryMappedFile _map;
        private MemoryMappedViewAccessor _accessor;
        private byte* _base;
        private bool _acquired;

        public WasperMappedRegion(MemoryMappedFile map, MemoryMappedFileAccess access)
        {
            _map = map ?? throw new ArgumentNullException(nameof(map));

            try
            {
                _accessor = _map.CreateViewAccessor(0, 0, access);
                Capacity = _accessor.Capacity;

                byte* pointer = null;
                _accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);

                if (pointer == null)
                    throw new WasperLiveLinkException("Could not acquire a pointer to the mapped view.");

                _acquired = true;
                _base = pointer + _accessor.PointerOffset;
            }
            catch
            {
                // Own the mapping from here on, so a failure part way through does
                // not leak the handle the caller has already handed over.
                Dispose();
                throw;
            }
        }

        public long Capacity { get; }

        public bool IsValid => _base != null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadInt32(long offset) => Volatile.Read(ref *(int*)(_base + offset));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public long ReadInt64(long offset) => Volatile.Read(ref *(long*)(_base + offset));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteInt32(long offset, int value) => Volatile.Write(ref *(int*)(_base + offset), value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteInt64(long offset, long value) => Volatile.Write(ref *(long*)(_base + offset), value);

        /// <summary>Non-volatile read, for payload bytes where ordering is
        /// established by the surrounding revision checks.</summary>
        public byte ReadByte(long offset) => *(_base + offset);

        public void WriteBytes(long offset, byte[] source, int sourceOffset, int count)
        {
            if (count == 0) return;
            if (offset < 0 || count < 0 || offset + count > Capacity)
                throw new WasperLiveLinkException("Write of " + count + " bytes at " + offset + " leaves the mapping.");

            fixed (byte* src = &source[sourceOffset])
            {
                Buffer.MemoryCopy(src, _base + offset, Capacity - offset, count);
            }
        }

        public void ReadBytes(long offset, byte[] destination, int destinationOffset, int count)
        {
            if (count == 0) return;
            if (offset < 0 || count < 0 || offset + count > Capacity)
                throw new WasperLiveLinkException("Read of " + count + " bytes at " + offset + " leaves the mapping.");

            fixed (byte* dst = &destination[destinationOffset])
            {
                Buffer.MemoryCopy(_base + offset, dst, destination.Length - destinationOffset, count);
            }
        }

        public void Dispose()
        {
            if (_acquired)
            {
                try
                {
                    _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
                }
                catch (Exception)
                {
                    // Teardown continues regardless; the handle is going away anyway.
                }

                _acquired = false;
            }

            _base = null;

            _accessor?.Dispose();
            _accessor = null;

            _map?.Dispose();
            _map = null;
        }
    }
}
