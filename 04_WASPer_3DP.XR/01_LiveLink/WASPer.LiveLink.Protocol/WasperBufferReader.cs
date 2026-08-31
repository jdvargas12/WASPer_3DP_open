using System;
using System.Buffers.Binary;
using System.Text;

namespace WASPer.LiveLink
{
    /// <summary>
    /// Little-endian reader over a byte range, bounds-checked on every read.
    /// Internal: the serializer is the only caller, and nothing outside this
    /// assembly has any business parsing raw frame bytes.
    /// </summary>
    internal sealed class WasperBufferReader
    {
        private readonly byte[] _buffer;
        private readonly int _end;
        private int _position;

        public WasperBufferReader(byte[] buffer, int offset, int count)
        {
            _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(count));
            _position = offset;
            _end = offset + count;
        }

        public int Position => _position;

        public int Remaining => _end - _position;

        private void Demand(int count)
        {
            if (count < 0 || _position + count > _end)
                throw new WasperLiveLinkException(
                    "Frame is truncated: needed " + count + " bytes at offset " + _position +
                    " but only " + Remaining + " remain.");
        }

        public byte ReadByte()
        {
            Demand(1);
            return _buffer[_position++];
        }

        public ushort ReadUInt16()
        {
            Demand(2);
            ushort value = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(_position, 2));
            _position += 2;
            return value;
        }

        public int ReadInt32()
        {
            Demand(4);
            int value = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_position, 4));
            _position += 4;
            return value;
        }

        public uint ReadUInt32()
        {
            Demand(4);
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_position, 4));
            _position += 4;
            return value;
        }

        public long ReadInt64()
        {
            Demand(8);
            long value = BinaryPrimitives.ReadInt64LittleEndian(_buffer.AsSpan(_position, 8));
            _position += 8;
            return value;
        }

        public float ReadSingle()
        {
            Demand(4);
            float value = BinaryPrimitives.ReadSingleLittleEndian(_buffer.AsSpan(_position, 4));
            _position += 4;
            return value;
        }

        public double ReadDouble()
        {
            Demand(8);
            double value = BinaryPrimitives.ReadDoubleLittleEndian(_buffer.AsSpan(_position, 8));
            _position += 8;
            return value;
        }

        public float[] ReadSingleArray(int count)
        {
            var values = new float[count];
            for (int i = 0; i < count; i++) values[i] = ReadSingle();
            return values;
        }

        public int[] ReadInt32Array(int count)
        {
            var values = new int[count];
            for (int i = 0; i < count; i++) values[i] = ReadInt32();
            return values;
        }

        public byte[] ReadByteArray(int count)
        {
            Demand(count);
            var values = new byte[count];
            System.Buffer.BlockCopy(_buffer, _position, values, 0, count);
            _position += count;
            return values;
        }

        public string ReadUtf8()
        {
            int byteCount = ReadInt32();
            if (byteCount < 0)
                throw new WasperLiveLinkException("Negative string length in frame.");
            Demand(byteCount);
            string value = Encoding.UTF8.GetString(_buffer, _position, byteCount);
            _position += byteCount;
            return value;
        }
    }
}
