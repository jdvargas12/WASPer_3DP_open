using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using Rhino.Geometry;

namespace WASPer_3DP.Painting
{
    internal static class WasperPaintTextureSource
    {
        private static readonly string[] ImageExtensions =
            { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };

        internal static bool TryDescribe(
            object raw,
            out string sourceKey,
            out string description,
            out string error)
        {
            sourceKey = string.Empty;
            description = "none";
            error = null;
            if (raw is string filePath)
            {
                if (!TryImageFile(filePath, out string fullPath, out FileInfo info, out error))
                    return false;
                sourceKey = $"file|{fullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                description = $"image file ({Path.GetFileName(fullPath)})";
                return true;
            }
            if (raw is Bitmap bitmap)
            {
                sourceKey = $"bitmap|{RuntimeHelpers.GetHashCode(bitmap)}|{bitmap.Width}|{bitmap.Height}";
                description = $"bitmap ({bitmap.Width}x{bitmap.Height})";
                return true;
            }
            if (raw is Image image)
            {
                sourceKey = $"image|{RuntimeHelpers.GetHashCode(image)}|{image.Width}|{image.Height}";
                description = $"image ({image.Width}x{image.Height})";
                return true;
            }
            if (raw is Mesh mesh)
            {
                if (!HasVertexColors(mesh, out error))
                    return false;
                sourceKey = MeshKey(mesh);
                description = $"vertex-colored mesh ({mesh.Vertices.Count} vertices)";
                return true;
            }
            error =
                "Unsupported texture input. Supply an image file path, Image/Bitmap, " +
                "or a vertex-colored Mesh.";
            return false;
        }

        internal static bool TryCreateBitmap(
            object raw,
            out object source,
            out string sourceKey,
            out string description,
            out Bitmap bitmap,
            out string error)
        {
            source = null;
            sourceKey = string.Empty;
            description = "none";
            bitmap = null;
            error = null;
            if (raw is string filePath)
            {
                if (!TryImageFile(filePath, out string fullPath, out FileInfo info, out error))
                    return false;
                using Image loaded = Image.FromFile(fullPath);
                bitmap = new Bitmap(loaded);
                source = fullPath;
                sourceKey = $"file|{fullPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                description =
                    $"image file ({Path.GetFileName(fullPath)}, {bitmap.Width}x{bitmap.Height})";
                return true;
            }
            if (raw is Bitmap inputBitmap)
            {
                bitmap = new Bitmap(inputBitmap);
                source = inputBitmap;
                sourceKey =
                    $"bitmap|{RuntimeHelpers.GetHashCode(inputBitmap)}|" +
                    $"{inputBitmap.Width}|{inputBitmap.Height}";
                description = $"bitmap ({bitmap.Width}x{bitmap.Height})";
                return true;
            }
            if (raw is Image image)
            {
                bitmap = new Bitmap(image);
                source = image;
                sourceKey = $"image|{RuntimeHelpers.GetHashCode(image)}|{image.Width}|{image.Height}";
                description = $"image ({bitmap.Width}x{bitmap.Height})";
                return true;
            }
            if (raw is Mesh mesh)
            {
                if (!HasVertexColors(mesh, out error))
                    return false;
                Mesh copy = mesh.DuplicateMesh();
                bitmap = RasterizeColoredMesh(copy);
                source = copy;
                sourceKey = MeshKey(copy);
                description =
                    $"vertex-colored mesh ({copy.Vertices.Count} vertices, " +
                    $"{bitmap.Width}x{bitmap.Height} projection)";
                return true;
            }
            error =
                "Unsupported texture input. Supply an image file path, Image/Bitmap, " +
                "or a vertex-colored Mesh.";
            return false;
        }

        private static bool TryImageFile(
            string filePath,
            out string fullPath,
            out FileInfo info,
            out string error)
        {
            fullPath = string.Empty;
            info = null;
            error = null;
            filePath = filePath?.Trim() ?? string.Empty;
            if (filePath.Length == 0)
                return false;
            if (!File.Exists(filePath))
            {
                error = $"Texture image was not found: {filePath}";
                return false;
            }
            if (!ImageExtensions.Contains(Path.GetExtension(filePath).ToLowerInvariant()))
            {
                error =
                    "The texture path must reference a PNG, JPG, JPEG, BMP, GIF, TIF, or TIFF image.";
                return false;
            }
            fullPath = Path.GetFullPath(filePath);
            info = new FileInfo(fullPath);
            return true;
        }

        private static bool HasVertexColors(Mesh mesh, out string error)
        {
            error = null;
            if (mesh != null && mesh.Vertices.Count > 0 &&
                mesh.VertexColors.Count == mesh.Vertices.Count)
                return true;
            error = "A texture Mesh must contain one vertex color for every mesh vertex.";
            return false;
        }

        private static Bitmap RasterizeColoredMesh(Mesh mesh)
        {
            const int longestSide = 768;
            BoundingBox box = mesh.GetBoundingBox(true);
            double[] spans =
                { box.Max.X - box.Min.X, box.Max.Y - box.Min.Y, box.Max.Z - box.Min.Z };
            int dropAxis = Array.IndexOf(spans, spans.Min());
            int axisU = dropAxis == 0 ? 1 : 0;
            int axisV = dropAxis == 2 ? 1 : 2;
            if (dropAxis == 1)
            {
                axisU = 0;
                axisV = 2;
            }
            double Coordinate(Point3d point, int axis) =>
                axis == 0 ? point.X : axis == 1 ? point.Y : point.Z;
            double minU = mesh.Vertices.Select(v => Coordinate(v, axisU)).Min();
            double maxU = mesh.Vertices.Select(v => Coordinate(v, axisU)).Max();
            double minV = mesh.Vertices.Select(v => Coordinate(v, axisV)).Min();
            double maxV = mesh.Vertices.Select(v => Coordinate(v, axisV)).Max();
            double spanU = Math.Max(maxU - minU, 1e-9);
            double spanV = Math.Max(maxV - minV, 1e-9);
            int width = spanU >= spanV
                ? longestSide
                : Math.Max(32, (int)Math.Round(longestSide * spanU / spanV));
            int height = spanV >= spanU
                ? longestSide
                : Math.Max(32, (int)Math.Round(longestSide * spanV / spanU));
            var result = new Bitmap(width, height);
            using Graphics graphics = Graphics.FromImage(result);
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (MeshFace face in mesh.Faces)
            {
                int[] indices = face.IsQuad
                    ? new[] { face.A, face.B, face.C, face.D }
                    : new[] { face.A, face.B, face.C };
                var polygon = new PointF[indices.Length];
                int alpha = 0;
                int red = 0;
                int green = 0;
                int blue = 0;
                for (int i = 0; i < indices.Length; i++)
                {
                    Point3d point = mesh.Vertices[indices[i]];
                    double u = (Coordinate(point, axisU) - minU) / spanU;
                    double v = (Coordinate(point, axisV) - minV) / spanV;
                    polygon[i] = new PointF(
                        (float)(u * (width - 1)),
                        (float)((1.0 - v) * (height - 1)));
                    Color color = mesh.VertexColors[indices[i]];
                    alpha += color.A;
                    red += color.R;
                    green += color.G;
                    blue += color.B;
                }
                using var brush = new SolidBrush(Color.FromArgb(
                    alpha / indices.Length,
                    red / indices.Length,
                    green / indices.Length,
                    blue / indices.Length));
                graphics.FillPolygon(brush, polygon);
            }
            return result;
        }

        private static string MeshKey(Mesh mesh)
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + mesh.Vertices.Count;
                hash = hash * 31 + mesh.Faces.Count;
                for (int i = 0; i < mesh.Vertices.Count; i++)
                {
                    hash = hash * 31 + mesh.Vertices[i].GetHashCode();
                    hash = hash * 31 + mesh.VertexColors[i].ToArgb();
                }
                return $"mesh|{hash}|{mesh.Vertices.Count}|{mesh.Faces.Count}";
            }
        }
    }
}
