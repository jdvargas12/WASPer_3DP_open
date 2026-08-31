using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Rhino.Geometry;

namespace WASPer_3DP
{
    internal enum WasperFieldBitmapMode
    {
        Binary = 0,
        Linear = 1
    }

    internal sealed class WasperFieldBitmapLayout
    {
        internal Plane BasePlane;
        internal double XMin;
        internal double YMin;
        internal int Width;
        internal int Height;
        internal double RequestedPixelSize;
        internal double PixelSizeX;
        internal double PixelSizeY;
        internal double ZMin;
        internal double ZMax;
        internal double LayerHeight;
        internal int LayerCount;

        internal double SizeX => Width * PixelSizeX;
        internal double SizeY => Height * PixelSizeY;

        internal Plane LayerPlane(int index)
        {
            var plane = new Plane(BasePlane);
            double z = ZMin + (index + 0.5) * LayerHeight;
            plane.Translate(BasePlane.ZAxis * z);
            return plane;
        }

        internal Rectangle3d LayerFrame(int index)
        {
            return new Rectangle3d(
                LayerPlane(index),
                new Interval(XMin, XMin + SizeX),
                new Interval(YMin, YMin + SizeY));
        }
    }

    internal static class WasperFieldBitmapRasterizer
    {
        internal const long MaxPixelsPerLayer = 100_000_000;

        internal static bool TryCreateLayout(
            WasperField field,
            Rectangle3d requestedFrame,
            bool hasRequestedFrame,
            double layerHeight,
            double pixelSize,
            double margin,
            out WasperFieldBitmapLayout layout,
            out string error)
        {
            layout = null;
            error = string.Empty;

            if (field == null || field.Evaluator == null || !field.Domain.IsValid)
            {
                error = "field must be a valid WASPer 3D Field with a valid domain.";
                return false;
            }

            if (!(layerHeight > Rhino.RhinoMath.ZeroTolerance) ||
                double.IsNaN(layerHeight) || double.IsInfinity(layerHeight))
            {
                error = "layer_h must be a finite number greater than zero.";
                return false;
            }

            if (!(pixelSize > Rhino.RhinoMath.ZeroTolerance) ||
                double.IsNaN(pixelSize) || double.IsInfinity(pixelSize))
            {
                error = "pixel_size must be a finite number greater than zero.";
                return false;
            }

            margin = Math.Max(0.0, margin);
            Plane basePlane = hasRequestedFrame ? requestedFrame.Plane : Plane.WorldXY;
            if (!basePlane.IsValid)
            {
                error = "print_frame has an invalid plane.";
                return false;
            }

            ProjectDomain(
                field.Domain,
                basePlane,
                out double domainXMin,
                out double domainXMax,
                out double domainYMin,
                out double domainYMax,
                out double zMin,
                out double zMax);

            if (!(zMax > zMin))
            {
                error = "The field domain has no thickness along the print-frame Z axis.";
                return false;
            }

            double xMin;
            double xMax;
            double yMin;
            double yMax;
            if (hasRequestedFrame)
            {
                if (!requestedFrame.IsValid ||
                    requestedFrame.Width <= Rhino.RhinoMath.ZeroTolerance ||
                    requestedFrame.Height <= Rhino.RhinoMath.ZeroTolerance)
                {
                    error = "print_frame must be a valid, non-degenerate rectangle.";
                    return false;
                }

                xMin = Math.Min(requestedFrame.X.T0, requestedFrame.X.T1);
                xMax = Math.Max(requestedFrame.X.T0, requestedFrame.X.T1);
                yMin = Math.Min(requestedFrame.Y.T0, requestedFrame.Y.T1);
                yMax = Math.Max(requestedFrame.Y.T0, requestedFrame.Y.T1);
            }
            else
            {
                // Keep the gross physical bounds independent of pixel pitch.
                // The integer raster resolution is fitted inside these exact bounds below.
                xMin = domainXMin - margin;
                xMax = domainXMax + margin;
                yMin = domainYMin - margin;
                yMax = domainYMax + margin;
            }

            int width = Math.Max(1, (int)Math.Ceiling((xMax - xMin) / pixelSize));
            int height = Math.Max(1, (int)Math.Ceiling((yMax - yMin) / pixelSize));
            double resolvedPixelSizeX = (xMax - xMin) / width;
            double resolvedPixelSizeY = (yMax - yMin) / height;
            long pixels = (long)width * height;
            if (pixels > MaxPixelsPerLayer)
            {
                error = $"Resolved bitmap is {width} x {height} ({pixels:N0} pixels), above the " +
                        $"safety limit of {MaxPixelsPerLayer:N0} pixels per layer. Increase pixel_size or reduce print_frame.";
                return false;
            }

            int layerCount = Math.Max(1, (int)Math.Ceiling((zMax - zMin) / layerHeight));
            layout = new WasperFieldBitmapLayout
            {
                BasePlane = basePlane,
                XMin = xMin,
                YMin = yMin,
                Width = width,
                Height = height,
                RequestedPixelSize = pixelSize,
                PixelSizeX = resolvedPixelSizeX,
                PixelSizeY = resolvedPixelSizeY,
                ZMin = zMin,
                ZMax = zMax,
                LayerHeight = layerHeight,
                LayerCount = layerCount
            };
            return true;
        }

        internal static Bitmap RasterizeLayer(
            WasperField field,
            WasperFieldBitmapLayout layout,
            int layerIndex,
            WasperFieldBitmapMode mode,
            double threshold,
            Interval fieldRange,
            bool invert,
            out int invalidSamples,
            bool parallel = false)
        {
            byte[] pixels = RasterizeLayerBytes(
                field, layout, layerIndex, mode, threshold, fieldRange, invert,
                out invalidSamples, parallel);
            var bitmap = new Bitmap(layout.Width, layout.Height, PixelFormat.Format8bppIndexed);
            SetGrayscalePalette(bitmap);

            BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, layout.Width, layout.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format8bppIndexed);

            try
            {
                var rowBytes = new byte[Math.Abs(data.Stride)];
                for (int imageRow = 0; imageRow < layout.Height; imageRow++)
                {
                    Array.Clear(rowBytes, 0, rowBytes.Length);
                    Buffer.BlockCopy(pixels, imageRow * layout.Width, rowBytes, 0, layout.Width);

                    IntPtr target = IntPtr.Add(data.Scan0, imageRow * data.Stride);
                    Marshal.Copy(rowBytes, 0, target, rowBytes.Length);
                }
            }
            catch
            {
                bitmap.UnlockBits(data);
                bitmap.Dispose();
                throw;
            }

            bitmap.UnlockBits(data);
            return bitmap;
        }

        internal static Bitmap RasterizeBinaryLayer(
            WasperField field,
            WasperFieldBitmapLayout layout,
            int layerIndex,
            WasperBinderSettings settings,
            out int invalidSamples,
            bool parallel = false)
        {
            byte[] packed = RasterizePackedLayer(
                field, layout, layerIndex, settings, out invalidSamples, parallel);
            var bitmap = new Bitmap(layout.Width, layout.Height, PixelFormat.Format1bppIndexed);
            ColorPalette palette = bitmap.Palette;
            palette.Entries[0] = Color.Black;
            palette.Entries[1] = Color.White;
            bitmap.Palette = palette;

            BitmapData data = bitmap.LockBits(
                new Rectangle(0, 0, layout.Width, layout.Height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format1bppIndexed);
            int packedStride = (layout.Width + 7) / 8;
            try
            {
                var row = new byte[Math.Abs(data.Stride)];
                for (int y = 0; y < layout.Height; y++)
                {
                    Array.Clear(row, 0, row.Length);
                    Buffer.BlockCopy(packed, y * packedStride, row, 0, packedStride);
                    Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                }
            }
            catch
            {
                bitmap.UnlockBits(data);
                bitmap.Dispose();
                throw;
            }
            bitmap.UnlockBits(data);
            return bitmap;
        }

        internal static byte[] RasterizePackedLayer(
            WasperField field,
            WasperFieldBitmapLayout layout,
            int layerIndex,
            WasperBinderSettings settings,
            out int invalidSamples,
            bool parallel = false)
        {
            settings ??= WasperBinderSettings.Default;
            byte[] gray = RasterizeLayerBytes(
                field, layout, layerIndex, settings.Mode, settings.Threshold,
                settings.FieldRange, settings.Invert, out invalidSamples, parallel);
            int stride = (layout.Width + 7) / 8;
            var packed = new byte[stride * layout.Height];
            for (int y = 0; y < layout.Height; y++)
            {
                int sourceRow = y * layout.Width;
                int targetRow = y * stride;
                for (int x = 0; x < layout.Width; x++)
                {
                    // 1bpp indexed convention: bit 0 is black and bit 1 is white.
                    if (gray[sourceRow + x] >= 128)
                        packed[targetRow + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                }
            }
            return packed;
        }

        internal static byte[] RasterizeLayerBytes(
            WasperField field,
            WasperFieldBitmapLayout layout,
            int layerIndex,
            WasperFieldBitmapMode mode,
            double threshold,
            Interval fieldRange,
            bool invert,
            out int invalidSamples,
            bool parallel = false)
        {
            if (field == null) throw new ArgumentNullException(nameof(field));
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            if (layerIndex < 0 || layerIndex >= layout.LayerCount)
                throw new ArgumentOutOfRangeException(nameof(layerIndex));

            var pixels = new byte[checked(layout.Width * layout.Height)];
            Plane plane = layout.LayerPlane(layerIndex);
            double low = Math.Min(fieldRange.T0, fieldRange.T1);
            double high = Math.Max(fieldRange.T0, fieldRange.T1);
            double span = high - low;
            int invalid = 0;

            Action<int> rasterizeRow = imageRow =>
            {
                int localInvalid = 0;
                int fieldRow = layout.Height - 1 - imageRow;
                double y = layout.YMin + (fieldRow + 0.5) * layout.PixelSizeY;
                int offset = imageRow * layout.Width;
                for (int column = 0; column < layout.Width; column++)
                {
                    double x = layout.XMin + (column + 0.5) * layout.PixelSizeX;
                    double value;
                    try { value = field.Evaluate(plane.PointAt(x, y)); }
                    catch { value = double.PositiveInfinity; }

                    double coverage;
                    if (!double.IsFinite(value))
                    {
                        localInvalid++;
                        coverage = 0.0;
                    }
                    else if (mode == WasperFieldBitmapMode.Binary)
                        coverage = value <= threshold ? 1.0 : 0.0;
                    else if (span <= Rhino.RhinoMath.ZeroTolerance)
                        coverage = value <= low ? 1.0 : 0.0;
                    else
                        coverage = Clamp01((high - value) / span);

                    double gray01 = invert ? coverage : 1.0 - coverage;
                    pixels[offset + column] = (byte)Math.Round(255.0 * Clamp01(gray01));
                }
                if (localInvalid > 0) Interlocked.Add(ref invalid, localInvalid);
            };

            if (parallel && layout.Height >= 32 && Environment.ProcessorCount > 1)
            {
                Parallel.For(
                    0,
                    layout.Height,
                    new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                    rasterizeRow);
            }
            else
            {
                for (int row = 0; row < layout.Height; row++) rasterizeRow(row);
            }

            invalidSamples = invalid;
            return pixels;
        }

        private static void SetGrayscalePalette(Bitmap bitmap)
        {
            ColorPalette palette = bitmap.Palette;
            for (int i = 0; i < palette.Entries.Length; i++)
                palette.Entries[i] = Color.FromArgb(255, i, i, i);
            bitmap.Palette = palette;
        }

        private static double Clamp01(double value)
        {
            if (value <= 0.0) return 0.0;
            if (value >= 1.0) return 1.0;
            return value;
        }

        private static void ProjectDomain(
            BoundingBox domain,
            Plane plane,
            out double xMin,
            out double xMax,
            out double yMin,
            out double yMax,
            out double zMin,
            out double zMax)
        {
            xMin = yMin = zMin = double.PositiveInfinity;
            xMax = yMax = zMax = double.NegativeInfinity;

            foreach (Point3d corner in domain.GetCorners())
            {
                Vector3d delta = corner - plane.Origin;
                double x = delta * plane.XAxis;
                double y = delta * plane.YAxis;
                double z = delta * plane.ZAxis;
                xMin = Math.Min(xMin, x);
                xMax = Math.Max(xMax, x);
                yMin = Math.Min(yMin, y);
                yMax = Math.Max(yMax, y);
                zMin = Math.Min(zMin, z);
                zMax = Math.Max(zMax, z);
            }
        }
    }
}
