using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace WASPer_3DP.Components._3_Geometry
{
    internal static class WasperBitmapMeshTools
    {
        public static bool TryGetBitmap(
            object raw,
            out Bitmap bitmap,
            out string error)
        {
            bitmap = null;
            error = null;
            object value = Unwrap(raw);

            try
            {
                if (value is Bitmap sourceBitmap)
                {
                    bitmap = new Bitmap(sourceBitmap);
                    return true;
                }
                if (value is Image sourceImage)
                {
                    bitmap = new Bitmap(sourceImage);
                    return true;
                }
                if (value is string path)
                {
                    if (!File.Exists(path))
                    {
                        error = $"Image file does not exist: {path}";
                        return false;
                    }
                    bitmap = LoadUnlocked(path);
                    return true;
                }
            }
            catch (Exception exception)
            {
                error = $"Could not read bitmap: {exception.Message}";
                bitmap?.Dispose();
                bitmap = null;
                return false;
            }

            error = "Expected a Bitmap, Image, or image file path.";
            return false;
        }

        public static object Unwrap(object raw)
        {
            object current = raw;
            for (int i = 0; i < 4 && current is IGH_Goo goo; i++)
            {
                object next = goo.ScriptVariable();
                if (next == null || ReferenceEquals(next, current))
                    break;
                current = next;
            }
            return current;
        }

        public static Bitmap LoadUnlocked(string path)
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using Image source = Image.FromStream(stream);
            return new Bitmap(source);
        }

        public static Bitmap Resize(
            Bitmap source,
            int width,
            int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            var output = new Bitmap(
                width,
                height,
                PixelFormat.Format32bppArgb);
            if (source.HorizontalResolution > 0.0f &&
                source.VerticalResolution > 0.0f)
            {
                output.SetResolution(
                    source.HorizontalResolution,
                    source.VerticalResolution);
            }
            using Graphics graphics = Graphics.FromImage(output);
            graphics.CompositingMode =
                System.Drawing.Drawing2D.CompositingMode.SourceCopy;
            graphics.CompositingQuality = CompositingQuality.HighQuality;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.HighQuality;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(
                source,
                new Rectangle(0, 0, width, height),
                new Rectangle(0, 0, source.Width, source.Height),
                GraphicsUnit.Pixel);
            return output;
        }

        public static Bitmap ToGrayscale(Bitmap source)
        {
            var output = new Bitmap(
                source.Width,
                source.Height,
                PixelFormat.Format32bppArgb);
            Rectangle bounds = new Rectangle(0, 0, source.Width, source.Height);

            using var converted = new Bitmap(
                source.Width,
                source.Height,
                PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(converted))
            {
                graphics.CompositingMode =
                    System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                graphics.DrawImageUnscaled(source, 0, 0);
            }

            BitmapData inputData = converted.LockBits(
                bounds,
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            BitmapData outputData = output.LockBits(
                bounds,
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                int inputStride = Math.Abs(inputData.Stride);
                int outputStride = Math.Abs(outputData.Stride);
                var input = new byte[inputStride * source.Height];
                var result = new byte[outputStride * source.Height];
                Marshal.Copy(inputData.Scan0, input, 0, input.Length);

                for (int y = 0; y < source.Height; y++)
                {
                    int inputRow = inputData.Stride >= 0
                        ? y * inputStride
                        : (source.Height - 1 - y) * inputStride;
                    int outputRow = outputData.Stride >= 0
                        ? y * outputStride
                        : (source.Height - 1 - y) * outputStride;
                    for (int x = 0; x < source.Width; x++)
                    {
                        int sourceIndex = inputRow + x * 4;
                        int targetIndex = outputRow + x * 4;
                        int gray = (int)Math.Round(
                            0.2126 * input[sourceIndex + 2] +
                            0.7152 * input[sourceIndex + 1] +
                            0.0722 * input[sourceIndex]);
                        byte value = (byte)Math.Max(0, Math.Min(255, gray));
                        result[targetIndex] = value;
                        result[targetIndex + 1] = value;
                        result[targetIndex + 2] = value;
                        result[targetIndex + 3] = input[sourceIndex + 3];
                    }
                }
                Marshal.Copy(result, 0, outputData.Scan0, result.Length);
            }
            finally
            {
                converted.UnlockBits(inputData);
                output.UnlockBits(outputData);
            }
            return output;
        }

        public static Mesh BitmapToMesh(
            Bitmap bitmap,
            double sizeU,
            double sizeV,
            int step)
        {
            if (bitmap == null || bitmap.Width < 2 || bitmap.Height < 2)
                return null;

            step = Math.Max(1, step);
            List<int> xIndices = SampleIndices(bitmap.Width, step);
            List<int> yIndices = SampleIndices(bitmap.Height, step);
            var mesh = new Mesh();

            foreach (int y in yIndices)
            {
                double v = (1.0 - y / (double)(bitmap.Height - 1)) * sizeV;
                foreach (int x in xIndices)
                {
                    double u = x / (double)(bitmap.Width - 1) * sizeU;
                    mesh.Vertices.Add(u, v, 0.0);
                    mesh.VertexColors.Add(bitmap.GetPixel(x, y));
                }
            }

            int columns = xIndices.Count;
            int rows = yIndices.Count;
            for (int row = 0; row < rows - 1; row++)
            {
                for (int column = 0; column < columns - 1; column++)
                {
                    int a = row * columns + column;
                    int b = a + 1;
                    int d = a + columns;
                    int c = d + 1;
                    // Bitmap top maps to +Y. This order keeps mesh normals +Z.
                    mesh.Faces.AddFace(a, d, c, b);
                }
            }

            mesh.Normals.ComputeNormals();
            mesh.Compact();
            return mesh;
        }

        public static bool TryMeshToBitmap(
            Mesh mesh,
            Plane referencePlane,
            ref double sizeU,
            ref double sizeV,
            int resolutionU,
            int resolutionV,
            Color background,
            out Bitmap bitmap,
            out Plane resolvedPlane,
            out int hitPixels,
            out string error)
        {
            bitmap = null;
            resolvedPlane = referencePlane.IsValid
                ? referencePlane
                : Plane.WorldXY;
            hitPixels = 0;
            error = null;

            if (mesh == null || !mesh.IsValid ||
                mesh.Vertices.Count == 0 || mesh.Faces.Count == 0)
            {
                error = "mesh_col must be a valid non-empty mesh.";
                return false;
            }
            if (mesh.VertexColors.Count != mesh.Vertices.Count)
            {
                error = "mesh_col must contain one vertex color per vertex.";
                return false;
            }

            Vector3d axisX = resolvedPlane.XAxis;
            Vector3d axisY = resolvedPlane.YAxis;
            Vector3d axisZ = resolvedPlane.ZAxis;
            if (!axisX.Unitize() || !axisY.Unitize() || !axisZ.Unitize())
            {
                error = "ref_plane does not contain valid orthogonal axes.";
                return false;
            }

            Point3d origin = resolvedPlane.Origin;
            double minU = double.PositiveInfinity;
            double maxU = double.NegativeInfinity;
            double minV = double.PositiveInfinity;
            double maxV = double.NegativeInfinity;
            double minW = double.PositiveInfinity;
            double maxW = double.NegativeInfinity;
            foreach (Point3f vertex in mesh.Vertices)
            {
                Vector3d delta = (Point3d)vertex - origin;
                double u = delta * axisX;
                double v = delta * axisY;
                double w = delta * axisZ;
                minU = Math.Min(minU, u);
                maxU = Math.Max(maxU, u);
                minV = Math.Min(minV, v);
                maxV = Math.Max(maxV, v);
                minW = Math.Min(minW, w);
                maxW = Math.Max(maxW, w);
            }

            double tolerance = Math.Max(
                RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001,
                1e-9);
            if (sizeU <= 0.0)
                sizeU = Math.Max(tolerance, maxU - minU);
            if (sizeV <= 0.0)
                sizeV = Math.Max(tolerance, maxV - minV);
            if (!double.IsFinite(sizeU) || !double.IsFinite(sizeV) ||
                sizeU <= 0.0 || sizeV <= 0.0)
            {
                error = "size_u and size_v must resolve to positive finite values.";
                return false;
            }

            bool autoFit = minU < 0.0 || minV < 0.0 ||
                           sizeU <= maxU - minU + tolerance ||
                           sizeV <= maxV - minV + tolerance;
            if (autoFit)
                resolvedPlane.Origin = origin + axisX * minU + axisY * minV;

            resolutionU = Math.Max(2, resolutionU);
            resolutionV = Math.Max(2, resolutionV);
            bitmap = new Bitmap(
                resolutionU,
                resolutionV,
                PixelFormat.Format32bppArgb);
            Rectangle bounds = new Rectangle(0, 0, resolutionU, resolutionV);
            BitmapData data = bitmap.LockBits(
                bounds,
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                int stride = Math.Abs(data.Stride);
                var buffer = new byte[stride * resolutionV];
                double depthMargin = Math.Max(
                    tolerance * 10.0,
                    Math.Max(sizeU, sizeV) * 1e-6);

                for (int row = 0; row < resolutionV; row++)
                {
                    double v =
                        (1.0 - row / (double)(resolutionV - 1)) * sizeV;
                    int targetRow = data.Stride >= 0
                        ? row * stride
                        : (resolutionV - 1 - row) * stride;

                    for (int column = 0; column < resolutionU; column++)
                    {
                        double u =
                            column / (double)(resolutionU - 1) * sizeU;
                        Point3d planePoint =
                            resolvedPlane.Origin + axisX * u + axisY * v;
                        var projectionLine = new Line(
                            planePoint + axisZ * (minW - depthMargin),
                            planePoint + axisZ * (maxW + depthMargin));
                        Point3d[] intersections =
                            Intersection.MeshLine(mesh, projectionLine);

                        Color color = background;
                        if (intersections != null && intersections.Length > 0)
                        {
                            Point3d closest = intersections[0];
                            double closestDistance =
                                Math.Abs((closest - planePoint) * axisZ);
                            for (int i = 1; i < intersections.Length; i++)
                            {
                                double distance =
                                    Math.Abs((intersections[i] - planePoint) * axisZ);
                                if (distance < closestDistance)
                                {
                                    closest = intersections[i];
                                    closestDistance = distance;
                                }
                            }
                            MeshPoint meshPoint =
                                mesh.ClosestMeshPoint(closest, tolerance * 10.0);
                            if (meshPoint != null)
                            {
                                color = mesh.ColorAt(meshPoint);
                                hitPixels++;
                            }
                        }

                        int index = targetRow + column * 4;
                        buffer[index] = color.B;
                        buffer[index + 1] = color.G;
                        buffer[index + 2] = color.R;
                        buffer[index + 3] = color.A;
                    }
                }

                Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
            }
            catch
            {
                bitmap.UnlockBits(data);
                bitmap.Dispose();
                bitmap = null;
                throw;
            }

            bitmap.UnlockBits(data);
            return true;
        }

        private static List<int> SampleIndices(int count, int step)
        {
            var indices = new List<int>();
            for (int index = 0; index < count; index += step)
                indices.Add(index);
            if (indices.Count == 0 || indices[indices.Count - 1] != count - 1)
                indices.Add(count - 1);
            return indices;
        }
    }

    internal static class WasperBitmapMeshIcons
    {
        public static Bitmap ImageToBitmap() =>
            DrawIcon(Color.FromArgb(63, 137, 196), Color.FromArgb(238, 167, 54), 0);

        public static Bitmap MeshToBitmap() =>
            DrawIcon(Color.FromArgb(76, 155, 115), Color.FromArgb(63, 137, 196), 1);

        public static Bitmap BitmapToMesh() =>
            DrawIcon(Color.FromArgb(238, 167, 54), Color.FromArgb(76, 155, 115), 2);

        private static Bitmap DrawIcon(Color first, Color second, int mode)
        {
            var bitmap = new Bitmap(24, 24);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var border = new Pen(Color.FromArgb(55, 69, 78), 1.2f);
            using var firstBrush = new SolidBrush(first);
            using var secondBrush = new SolidBrush(second);
            graphics.FillRectangle(firstBrush, 2, 3, 9, 8);
            graphics.DrawRectangle(border, 2, 3, 9, 8);
            graphics.FillRectangle(secondBrush, 13, 13, 9, 8);
            graphics.DrawRectangle(border, 13, 13, 9, 8);

            using var arrow = new Pen(Color.FromArgb(55, 69, 78), 1.8f)
            {
                EndCap = LineCap.ArrowAnchor
            };
            graphics.DrawLine(arrow, 9, 10, 15, 15);

            if (mode > 0)
            {
                using var grid = new Pen(Color.FromArgb(240, 240, 240), 0.8f);
                Rectangle target = mode == 1
                    ? new Rectangle(2, 3, 9, 8)
                    : new Rectangle(13, 13, 9, 8);
                graphics.DrawLine(
                    grid,
                    target.Left + target.Width / 2,
                    target.Top,
                    target.Left + target.Width / 2,
                    target.Bottom);
                graphics.DrawLine(
                    grid,
                    target.Left,
                    target.Top + target.Height / 2,
                    target.Right,
                    target.Top + target.Height / 2);
            }
            return bitmap;
        }
    }
}
