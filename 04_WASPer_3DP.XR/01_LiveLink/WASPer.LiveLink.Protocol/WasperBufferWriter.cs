using System;
using System.Buffers.Binary;
using System.Text;

namespace WASPer.LiveLink
{
    /// <summary>
    /// Growable little-endian byte buffer with back-patching. Explicit
    /// BinaryPrimitives calls rather than BinaryWriter so endianness is stated at
    /// every call site and never inherited from the host architecture.
    /// </summary>
    public sealed class WasperBufferWriter
    {
        private byte[] _buffer;
        private int _length;

        public WasperBufferWriter(int initialCapacity = 64 * 1024)
        {
            if (initialCapacity < 64) initialCapacity = 64;
            _buffer = new byte[initialCapacity];
            _length = 0;
        }

        /// <summary>Bytes written so far.</summary>
        public int Length => _length;

        public byte[] Buffer => _buffer;

        public void Reset() => _length = 0;

        private void Ensure(int extra)
        {
            int required = _length + extra;
            if (required <= _buffer.Length) return;

            int capacity = _buffer.Length;
            while (capacity < required)
            {
                // Guard against overflow on very large frames; the caller enforces
                // the real payload ceiling before publishing.
                if (capacity > int.MaxValue / 2)
                {
                    capacity = int.MaxValue;
                    break;
                }
                capacity *= 2;
            }

            Array.Resize(ref _buffer, capacity);
        }

        /// <summary>Advances by <paramref name="count"/> zeroed bytes.</summary>
        public void Skip(int count)
        {
            Ensure(count);
            Array.Clear(_buffer, _length, count);
            _length += count;
        }

        /// <summary>Writes one byte.</summary>
        public void WriteByte(byte value)
        {
            Ensure(1);
            _buffer[_length++] = value;
        }

        public void WriteBytes(byte[] value)
        {
            if (value == null || value.Length == 0) return;
            Ensure(value.Length);
            System.Buffer.BlockCopy(value, 0, _buffer, _length, value.Length);
            _length += value.Length;
        }

        /// <summary>Writes a little-endian UInt16.</summary>
        public void WriteUInt16(ushort value)
        {
            Ensure(2);
            BinaryPrimitives.WriteUInt16LittleEndian(_buffer.AsSpan(_length, 2), value);
            _length += 2;
        }

        /// <summary>Writes a little-endian Int32.</summary>
        public void WriteInt32(int value)
        {
            Ensure(4);
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(_length, 4), value);
            _length += 4;
        }

        /// <summary>Writes a little-endian UInt32.</summary>
        public void WriteUInt32(uint value)
        {
            Ensure(4);
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(_length, 4), value);
            _length += 4;
        }

        /// <summary>Writes a little-endian Int64.</summary>
        public void WriteInt64(long value)
        {
            Ensure(8);
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(_length, 8), value);
            _length += 8;
        }

        /// <summary>Writes a little-endian float32.</summary>
        public void WriteSingle(float value)
        {
            Ensure(4);
            BinaryPrimitives.WriteSingleLittleEndian(_buffer.AsSpan(_length, 4), value);
            _length += 4;
        }

        /// <summary>Writes a little-endian float64.</summary>
        public void WriteDouble(double value)
        {
            Ensure(8);
            BinaryPrimitives.WriteDoubleLittleEndian(_buffer.AsSpan(_length, 8), value);
            _length += 8;
        }

        /// <summary>Writes a UTF-8 string as a byte count followed by its bytes.</summary>
        public void WriteUtf8(string value)
        {
            byte[] bytes = value == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(value);
            WriteInt32(bytes.Length);
            WriteBytes(bytes);
        }

        /// <summary>Reserves four bytes and returns their position for back-patching.</summary>
        public int ReserveInt32()
        {
            int position = _length;
            Skip(4);
            return position;
        }

        /// <summary>Overwrites a reserved Int32 at <paramref name="position"/>.</summary>
        public void PatchInt32(int position, int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_buffer.AsSpan(position, 4), value);
        }

        /// <summary>Overwrites a reserved UInt32 at <paramref name="position"/>.</summary>
        public void PatchUInt32(int position, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_buffer.AsSpan(position, 4), value);
        }

        /// <summary>Overwrites an Int64 at <paramref name="position"/>.</summary>
        public void PatchInt64(int position, long value)
        {
            BinaryPrimitives.WriteInt64LittleEndian(_buffer.AsSpan(position, 8), value);
        }

        /// <summary>Copies the written bytes into a right-sized array.</summary>
        public byte[] ToArray()
        {
            var result = new byte[_length];
            System.Buffer.BlockCopy(_buffer, 0, result, 0, _length);
            return result;
        }
    }
}
