using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

using Rhino.Geometry;

namespace WASPer_3DP.Painting
{
    internal sealed class WasperPaintTextureLayer : IDisposable
    {
        internal object Source;
        internal string SourceDescription = "none";
        internal string SourceKey = string.Empty;
        internal string IgnoredSourceKey = string.Empty;
        internal Bitmap Bitmap;
        internal bool Visible = true;
        internal bool EditMode;
        internal bool DistortMode;
        internal bool RotateMode;
        internal readonly WasperPaintTexturePlacement Placement =
            new WasperPaintTexturePlacement();
        internal int Revision;
        internal double Opacity = 1.0;
        internal bool IsText;
        internal string TextContent = string.Empty;
        internal string FontName = "Arial";
        internal double FontSize = 10.0;
        internal bool TextCommitted;

        internal bool HasSource => Bitmap != null;

        internal void Clear()
        {
            Bitmap?.Dispose();
            Bitmap = null;
            Source = null;
            SourceDescription = "none";
            SourceKey = string.Empty;
            IgnoredSourceKey = string.Empty;
            Visible = true;
            EditMode = false;
            DistortMode = false;
            RotateMode = false;
            IsText = false;
            TextContent = string.Empty;
            FontName = "Arial";
            FontSize = 10.0;
            TextCommitted = false;
            Placement.Initialized = false;
            Placement.EndTransform();
            Revision++;
        }

        public void Dispose()
        {
            Bitmap?.Dispose();
            Bitmap = null;
        }
    }

    internal sealed class WasperPaintTexturePlacement
    {
        internal bool Initialized;
        internal double MinX;
        internal double MinY;
        internal double MaxX;
        internal double MaxY;
        internal readonly Point2d[] Corners = new Point2d[4];
        internal Point2d[] TransformStartCorners;
        internal Point2d TransformStartPoint = Point2d.Unset;
        internal int TransformCorner = -1;

        internal bool IsDistorted
        {
            get
            {
                const double tolerance = 1e-9;
                return
                    Corners[0].DistanceTo(new Point2d(MinX, MinY)) > tolerance ||
                    Corners[1].DistanceTo(new Point2d(MaxX, MinY)) > tolerance ||
                    Corners[2].DistanceTo(new Point2d(MaxX, MaxY)) > tolerance ||
                    Corners[3].DistanceTo(new Point2d(MinX, MaxY)) > tolerance;
            }
        }

        internal void ResetCornersFromBounds()
        {
            Corners[0] = new Point2d(MinX, MinY);
            Corners[1] = new Point2d(MaxX, MinY);
            Corners[2] = new Point2d(MaxX, MaxY);
            Corners[3] = new Point2d(MinX, MaxY);
        }

        internal void UpdateBoundsFromCorners()
        {
            MinX = Corners.Min(point => point.X);
            MinY = Corners.Min(point => point.Y);
            MaxX = Corners.Max(point => point.X);
            MaxY = Corners.Max(point => point.Y);
        }

        internal void EndTransform()
        {
            TransformStartCorners = null;
            TransformStartPoint = Point2d.Unset;
            TransformCorner = -1;
        }

        internal static bool IsConvexQuad(Point2d[] corners)
        {
            if (corners == null || corners.Length != 4)
                return false;
            double sign = 0.0;
            for (int i = 0; i < 4; i++)
            {
                Point2d a = corners[i];
                Point2d b = corners[(i + 1) % 4];
                Point2d c = corners[(i + 2) % 4];
                double cross =
                    (b.X - a.X) * (c.Y - b.Y) -
                    (b.Y - a.Y) * (c.X - b.X);
                if (Math.Abs(cross) <= 1e-9)
                    return false;
                if (i == 0)
                    sign = Math.Sign(cross);
                else if (Math.Sign(cross) != sign)
                    return false;
            }
            return true;
        }

        internal bool TryCoordinates(Point2d target, out double u, out double v)
        {
            u = (MaxX - MinX) <= 1e-12 ? 0.5 : (target.X - MinX) / (MaxX - MinX);
            v = (MaxY - MinY) <= 1e-12 ? 0.5 : 1.0 - (target.Y - MinY) / (MaxY - MinY);
            Point2d topLeft = Corners[3];
            Vector2d across = Corners[2] - topLeft;
            Vector2d down = Corners[0] - topLeft;
            Vector2d warp = new Vector2d(
                topLeft.X - Corners[2].X - Corners[0].X + Corners[1].X,
                topLeft.Y - Corners[2].Y - Corners[0].Y + Corners[1].Y);
            for (int iteration = 0; iteration < 12; iteration++)
            {
                Point2d mapped = new Point2d(
                    topLeft.X + across.X * u + down.X * v + warp.X * u * v,
                    topLeft.Y + across.Y * u + down.Y * v + warp.Y * u * v);
                double errorX = mapped.X - target.X;
                double errorY = mapped.Y - target.Y;
                if (errorX * errorX + errorY * errorY <= 1e-18)
                    break;
                double duX = across.X + warp.X * v;
                double duY = across.Y + warp.Y * v;
                double dvX = down.X + warp.X * u;
                double dvY = down.Y + warp.Y * u;
                double determinant = duX * dvY - duY * dvX;
                if (Math.Abs(determinant) <= 1e-14)
                    return false;
                u -= (errorX * dvY - errorY * dvX) / determinant;
                v -= (duX * errorY - duY * errorX) / determinant;
            }
            const double tolerance = 1e-6;
            if (!double.IsFinite(u) || !double.IsFinite(v) ||
                u < -tolerance || u > 1.0 + tolerance ||
                v < -tolerance || v > 1.0 + tolerance)
                return false;
            u = Math.Max(0.0, Math.Min(1.0, u));
            v = Math.Max(0.0, Math.Min(1.0, v));
            return true;
        }
    }

    internal static class WasperPaintTextureSampler
    {
        internal static bool TrySampleCompositeColor(
            IList<WasperPaintTextureLayer> layers,
            Point2d sample,
            out Color color)
        {
            color = Color.Transparent;
            if (layers == null)
                return false;

            double alpha = 0.0;
            double premultipliedRed = 0.0;
            double premultipliedGreen = 0.0;
            double premultipliedBlue = 0.0;
            foreach (WasperPaintTextureLayer layer in layers)
            {
                if (layer?.Bitmap == null || !layer.Visible ||
                    (layer.IsText && !layer.TextCommitted) ||
                    !layer.Placement.Initialized || layer.Opacity <= 0.0 ||
                    !layer.Placement.TryCoordinates(sample, out double u, out double v))
                    continue;
                int pixelX = Math.Max(
                    0,
                    Math.Min(layer.Bitmap.Width - 1,
                        (int)Math.Round(u * (layer.Bitmap.Width - 1))));
                int pixelY = Math.Max(
                    0,
                    Math.Min(layer.Bitmap.Height - 1,
                        (int)Math.Round(v * (layer.Bitmap.Height - 1))));
                Color sampled = layer.Bitmap.GetPixel(pixelX, pixelY);
                double layerAlpha =
                    sampled.A / 255.0 * Math.Max(0.0, Math.Min(1.0, layer.Opacity));
                if (layerAlpha <= 0.0)
                    continue;
                premultipliedRed =
                    sampled.R / 255.0 * layerAlpha +
                    premultipliedRed * (1.0 - layerAlpha);
                premultipliedGreen =
                    sampled.G / 255.0 * layerAlpha +
                    premultipliedGreen * (1.0 - layerAlpha);
                premultipliedBlue =
                    sampled.B / 255.0 * layerAlpha +
                    premultipliedBlue * (1.0 - layerAlpha);
                alpha = layerAlpha + alpha * (1.0 - layerAlpha);
            }
            if (alpha <= 0.0)
                return false;

            color = Color.FromArgb(
                (int)Math.Round(alpha * 255.0),
                (int)Math.Round(premultipliedRed / alpha * 255.0),
                (int)Math.Round(premultipliedGreen / alpha * 255.0),
                (int)Math.Round(premultipliedBlue / alpha * 255.0));
            return true;
        }

        internal static int ApplyToValues(
            Bitmap bitmap,
            WasperPaintTexturePlacement placement,
            IEnumerable<KeyValuePair<int, Point2d>> samples,
            Func<int, bool> isEligible,
            Interval domain,
            double[] values)
        {
            if (bitmap == null || placement == null || !placement.Initialized ||
                samples == null || values == null)
                return 0;
            int changed = 0;
            foreach (KeyValuePair<int, Point2d> pair in samples)
            {
                int index = pair.Key;
                if (index < 0 || index >= values.Length ||
                    (isEligible != null && !isEligible(index)) ||
                    !placement.TryCoordinates(pair.Value, out double u, out double v))
                    continue;
                int pixelX = Math.Max(
                    0,
                    Math.Min(bitmap.Width - 1,
                        (int)Math.Round(u * (bitmap.Width - 1))));
                int pixelY = Math.Max(
                    0,
                    Math.Min(bitmap.Height - 1,
                        (int)Math.Round(v * (bitmap.Height - 1))));
                Color color = bitmap.GetPixel(pixelX, pixelY);
                double alpha = color.A / 255.0;
                if (alpha <= 0.0)
                    continue;
                double luminance =
                    (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) /
                    255.0;
                double target = domain.T0 + luminance * (domain.T1 - domain.T0);
                double value = values[index] * (1.0 - alpha) + target * alpha;
                if (Math.Abs(value - values[index]) <= 1e-12)
                    continue;
                values[index] = value;
                changed++;
            }
            return changed;
        }

        internal static int ApplyCompositeToValues(
            IList<WasperPaintTextureLayer> layers,
            IEnumerable<KeyValuePair<int, Point2d>> samples,
            Func<int, bool> isEligible,
            Interval domain,
            double[] values)
        {
            if (layers == null || samples == null || values == null)
                return 0;
            int changed = 0;
            foreach (KeyValuePair<int, Point2d> pair in samples)
            {
                int index = pair.Key;
                if (index < 0 || index >= values.Length ||
                    (isEligible != null && !isEligible(index)))
                    continue;
                if (!TrySampleCompositeColor(layers, pair.Value, out Color color))
                    continue;
                double alpha = color.A / 255.0;
                double luminance =
                    (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) /
                    255.0;
                double target = domain.T0 + luminance * (domain.T1 - domain.T0);
                double value = values[index] * (1.0 - alpha) + target * alpha;
                if (Math.Abs(value - values[index]) <= 1e-12)
                    continue;
                values[index] = value;
                changed++;
            }
            return changed;
        }
    }
}
