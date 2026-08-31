using System;
using System.Collections.Generic;
using System.Globalization;
using Rhino.Geometry;

namespace WASPer_3DP
{
    /// <summary>
    /// Lightweight deterministic signatures for component-local caches. Geometry is
    /// hashed by Rhino's content CRC; scalar fields also include deterministic samples
    /// so equivalent recreated field objects can reuse cached preparation.
    /// </summary>
    internal struct WasperCacheSignature
    {
        private const ulong Offset = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;
        private ulong _hash;

        internal static WasperCacheSignature Create()
        {
            return new WasperCacheSignature { _hash = Offset };
        }

        internal void Add(bool value) => Add(value ? 1 : 0);
        internal void Add(int value) => Add(unchecked((ulong)(uint)value));
        internal void Add(uint value) => Add((ulong)value);
        internal void Add(long value) => Add(unchecked((ulong)value));
        internal void Add(double value) => Add(BitConverter.DoubleToInt64Bits(value));

        internal void Add(string value)
        {
            if (value == null)
            {
                Add(-1);
                return;
            }

            Add(value.Length);
            for (int i = 0; i < value.Length; i++)
                Add((int)value[i]);
        }

        internal void Add(Point3d point)
        {
            Add(point.X);
            Add(point.Y);
            Add(point.Z);
        }

        internal void Add(Vector3d vector)
        {
            Add(vector.X);
            Add(vector.Y);
            Add(vector.Z);
        }

        internal void Add(Plane plane)
        {
            Add(plane.Origin);
            Add(plane.XAxis);
            Add(plane.YAxis);
        }

        internal void Add(BoundingBox box)
        {
            Add(box.IsValid);
            if (!box.IsValid) return;
            Add(box.Min);
            Add(box.Max);
        }

        internal void Add(GeometryBase geometry)
        {
            Add(geometry != null);
            if (geometry == null) return;
            Add(geometry.GetType().FullName);
            Add(geometry.DataCRC(0));
        }

        internal void Add(IEnumerable<double> values)
        {
            if (values == null)
            {
                Add(-1);
                return;
            }

            int count = 0;
            foreach (double value in values)
            {
                Add(value);
                count++;
            }
            Add(count);
        }

        internal void Add(IEnumerable<int> values)
        {
            if (values == null)
            {
                Add(-1);
                return;
            }

            int count = 0;
            foreach (int value in values)
            {
                Add(value);
                count++;
            }
            Add(count);
        }

        internal void Add(WasperField field)
        {
            Add(field != null);
            if (field == null) return;

            Add(field.Domain);
            Add(field.Label);
            Add(field.OperationTrace);
            Add((int)field.SdfQuality);
            Add(field.OperationCount);
            Add(field.CurveThickenCount);

            if (!field.Domain.IsValid || field.Evaluator == null) return;

            Point3d min = field.Domain.Min;
            Point3d max = field.Domain.Max;
            for (int z = 0; z < 3; z++)
            for (int y = 0; y < 3; y++)
            for (int x = 0; x < 3; x++)
            {
                var point = new Point3d(
                    min.X + (max.X - min.X) * x * 0.5,
                    min.Y + (max.Y - min.Y) * y * 0.5,
                    min.Z + (max.Z - min.Z) * z * 0.5);
                double value;
                try { value = field.Evaluate(point); }
                catch { value = double.NaN; }
                Add(value);
            }
        }

        internal string Finish() => _hash.ToString("X16", CultureInfo.InvariantCulture);

        private void Add(ulong value)
        {
            if (_hash == 0) _hash = Offset;
            for (int i = 0; i < 8; i++)
            {
                _hash ^= (byte)(value & 0xff);
                _hash *= Prime;
                value >>= 8;
            }
        }
    }
}
