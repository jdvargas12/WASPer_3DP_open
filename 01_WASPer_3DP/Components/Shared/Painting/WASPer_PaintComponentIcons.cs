using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace WASPer_3DP
{
    /// <summary>
    /// Matched toolbar icons for components that use the shared WASPer painter.
    /// The brush is identical; the geometry beneath it identifies the target.
    /// </summary>
    internal static class WasperPaintComponentIcons
    {
        private static readonly Lazy<Bitmap> PrintPathIcon =
            new Lazy<Bitmap>(() => Create(PaintTarget.PrintPath), true);
        private static readonly Lazy<Bitmap> MeshIcon =
            new Lazy<Bitmap>(() => Create(PaintTarget.Mesh), true);

        internal static Bitmap PrintPath => PrintPathIcon.Value;
        internal static Bitmap Mesh => MeshIcon.Value;

        private enum PaintTarget
        {
            PrintPath,
            Mesh
        }

        private static Bitmap Create(PaintTarget target)
        {
            var bitmap = new Bitmap(24, 24);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

            if (target == PaintTarget.PrintPath)
                DrawPrintPath(graphics);
            else
                DrawMesh(graphics);

            DrawBrush(graphics);
            return bitmap;
        }

        private static void DrawPrintPath(Graphics graphics)
        {
            using var shadow = new Pen(Color.FromArgb(42, 76, 91), 3.5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            using var path = new Pen(Color.FromArgb(61, 157, 221), 2.0f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            graphics.DrawBezier(shadow, 2.0f, 19.0f, 4.5f, 12.5f, 10.0f, 21.5f, 16.0f, 16.5f);
            graphics.DrawBezier(path, 2.0f, 19.0f, 4.5f, 12.5f, 10.0f, 21.5f, 16.0f, 16.5f);
        }

        private static void DrawMesh(Graphics graphics)
        {
            PointF[] boundary =
            {
                new PointF(2.0f, 15.5f),
                new PointF(10.5f, 12.5f),
                new PointF(16.5f, 18.0f),
                new PointF(6.0f, 21.5f)
            };

            using var fill = new SolidBrush(Color.FromArgb(72, 61, 157, 221));
            using var grid = new Pen(Color.FromArgb(42, 76, 91), 1.25f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            using var accent = new Pen(Color.FromArgb(61, 157, 221), 1.15f);

            graphics.FillPolygon(fill, boundary);
            graphics.DrawPolygon(grid, boundary);
            graphics.DrawLine(accent, 6.2f, 14.0f, 11.5f, 19.7f);
            graphics.DrawLine(accent, 12.8f, 14.6f, 4.0f, 18.8f);
        }

        private static void DrawBrush(Graphics graphics)
        {
            using var handleOutline = new Pen(Color.FromArgb(31, 55, 72), 5.0f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            using var handle = new Pen(Color.FromArgb(246, 181, 57), 3.0f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };

            graphics.DrawLine(handleOutline, 20.0f, 3.0f, 13.2f, 10.0f);
            graphics.DrawLine(handle, 20.0f, 3.0f, 13.2f, 10.0f);

            PointF[] ferrule =
            {
                new PointF(11.7f, 8.6f),
                new PointF(15.4f, 12.3f),
                new PointF(12.7f, 15.0f),
                new PointF(9.0f, 11.3f)
            };
            using var ferruleFill = new SolidBrush(Color.FromArgb(221, 230, 235));
            using var ferruleOutline = new Pen(Color.FromArgb(31, 55, 72), 1.2f);
            graphics.FillPolygon(ferruleFill, ferrule);
            graphics.DrawPolygon(ferruleOutline, ferrule);

            PointF[] bristles =
            {
                new PointF(9.0f, 11.4f),
                new PointF(12.6f, 15.0f),
                new PointF(9.4f, 17.0f),
                new PointF(7.4f, 16.7f),
                new PointF(7.8f, 14.6f)
            };
            using var bristleFill = new SolidBrush(Color.FromArgb(238, 125, 45));
            using var bristleOutline = new Pen(Color.FromArgb(31, 55, 72), 1.2f);
            graphics.FillPolygon(bristleFill, bristles);
            graphics.DrawPolygon(bristleOutline, bristles);
        }
    }
}
