using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;

namespace WASPer_3DP
{
    /// <summary>
    /// Shared Cartesian line/marker renderer used by Data Vis and Study Manager hosts.
    /// All interaction geometry is returned in bitmap pixel coordinates.
    /// </summary>
    public sealed class WasperScatterChartRenderer : IWasperChartRenderer<IEnumerable<WasperChartSeries>>
    {
        public WasperChartRenderResult Render(
            IEnumerable<WasperChartSeries> data,
            WasperChartRenderOptions options)
        {
            options ??= new WasperChartRenderOptions();
            List<WasperChartSeries> series = (data ?? Enumerable.Empty<WasperChartSeries>())
                .Where(item => item != null)
                .ToList();
            List<WasperChartPoint> points = series
                .SelectMany(item => item.Points ?? new List<WasperChartPoint>())
                .Where(point => point?.IsValid == true)
                .ToList();

            var result = new WasperChartRenderResult
            {
                PixelScale = options.SafePixelScale
            };
            var bitmap = options.CreateBitmap();
            result.Bitmap = bitmap;

            using Graphics graphics = Graphics.FromImage(bitmap);
            options.PrepareGraphics(graphics);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = options.SafePixelScale > 1f
                ? System.Drawing.Text.TextRenderingHint.AntiAliasGridFit
                : System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            graphics.Clear(options.Layout?.TransparentBackground == true
                ? Color.Transparent
                : Color.White);

            RectangleF plot = PlotRectangle(options);
            result.PlotBounds = plot;
            DrawTitle(graphics, options);
            if (points.Count == 0)
            {
                DrawEmpty(graphics, plot);
                result.Warnings.Add("No finite samples are available for this chart.");
                return result;
            }

            Bounds bounds = ResolveBounds(points, options);
            DrawAxes(graphics, plot, bounds, options);
            foreach (WasperChartSeries item in series)
                DrawSeries(graphics, plot, bounds, item, options, result.HitTargets);
            // After every series, so collision avoidance sees all of them at once.
            DrawPointLabels(graphics, plot, bounds, series, options, result.HitTargets);
            DrawLegend(graphics, plot, options);
            return result;
        }

        private static RectangleF PlotRectangle(WasperChartRenderOptions options)
        {
            // The text-dependent part of each margin scales with TextScale so larger fonts do
            // not overrun the tick labels or the rotated Y title. At scale 1 these reduce to the
            // original 76 / 55 / 26 / 64 margins.
            float scale = options.SafeTextScale;
            float left = 30f + (46f * scale);
            float top = string.IsNullOrWhiteSpace(options.Layout?.Title) ? 28f : 20f + (35f * scale);
            float right = 26f;
            float bottom = 20f + (44f * scale);
            return new RectangleF(
                left,
                top,
                Math.Max(20f, options.SafeWidth - left - right),
                Math.Max(20f, options.SafeHeight - top - bottom));
        }

        private static void DrawTitle(Graphics graphics, WasperChartRenderOptions options)
        {
            string title = options.Layout?.Title?.Trim() ?? string.Empty;
            if (title.Length == 0)
                return;
            using var font = new Font(
                SystemFonts.MessageBoxFont.FontFamily,
                options.ScaledFont(Math.Max(7.0, options.Layout.TitleSize)),
                FontStyle.Bold);
            using var brush = new SolidBrush(Color.FromArgb(45, 45, 45));
            var bounds = new RectangleF(0f, 8f, options.SafeWidth, 34f * options.SafeTextScale);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            graphics.DrawString(title, font, brush, bounds, format);
        }

        private static void DrawEmpty(Graphics graphics, RectangleF plot)
        {
            using var pen = new Pen(Color.LightGray);
            using var brush = new SolidBrush(Color.DimGray);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            graphics.DrawRectangle(pen, plot.X, plot.Y, plot.Width, plot.Height);
            graphics.DrawString(
                "No numeric data",
                SystemFonts.MessageBoxFont,
                brush,
                plot,
                format);
        }

        private static Bounds ResolveBounds(
            IReadOnlyList<WasperChartPoint> points,
            WasperChartRenderOptions options)
        {
            double minX = points.Min(point => point.X);
            double maxX = points.Max(point => point.X);
            double minY = points.Min(point => point.Y);
            double maxY = points.Max(point => point.Y);
            Expand(ref minX, ref maxX);
            Expand(ref minY, ref maxY);
            // Explicit limits are applied after the automatic padding, so a supplied bound is the
            // exact edge of the axis rather than a padded version of itself.
            options.ApplyXLimits(ref minX, ref maxX);
            options.ApplyYLimits(ref minY, ref maxY);
            return new Bounds(minX, maxX, minY, maxY);
        }

        private static void Expand(ref double minimum, ref double maximum)
        {
            if (Math.Abs(maximum - minimum) <= 1e-12)
            {
                double padding = Math.Max(1.0, Math.Abs(minimum) * 0.05);
                minimum -= padding;
                maximum += padding;
                return;
            }
            double margin = (maximum - minimum) * 0.04;
            minimum -= margin;
            maximum += margin;
        }

        private static void DrawAxes(
            Graphics graphics,
            RectangleF plot,
            Bounds bounds,
            WasperChartRenderOptions options)
        {
            using var border = new Pen(Color.FromArgb(80, 80, 80), 1f);
            using var grid = new Pen(Color.FromArgb(225, 225, 225), 1f);
            using var textBrush = new SolidBrush(Color.FromArgb(70, 70, 70));
            float scale = options.SafeTextScale;
            using var tickFont = new Font(
                SystemFonts.MessageBoxFont.FontFamily,
                options.ScaledFont(Math.Max(7.0, options.Axis?.XTextSize ?? 9.0)));
            using var centered = new StringFormat { Alignment = StringAlignment.Center };
            using var right = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center
            };

            for (int index = 0; index <= 5; index++)
            {
                float fraction = index / 5f;
                float x = plot.Left + (plot.Width * fraction);
                float y = plot.Bottom - (plot.Height * fraction);
                if (options.Layout?.ShowReferences != false)
                {
                    graphics.DrawLine(grid, x, plot.Top, x, plot.Bottom);
                    graphics.DrawLine(grid, plot.Left, y, plot.Right, y);
                }
                string xText = FormatNumber(bounds.MinX + ((bounds.MaxX - bounds.MinX) * fraction));
                string yText = FormatNumber(bounds.MinY + ((bounds.MaxY - bounds.MinY) * fraction));
                graphics.DrawString(xText, tickFont, textBrush, x, plot.Bottom + (7f * scale), centered);
                graphics.DrawString(
                    yText,
                    tickFont,
                    textBrush,
                    new RectangleF(0f, y - (11f * scale), plot.Left - 8f, 22f * scale),
                    right);
            }
            graphics.DrawRectangle(border, plot.X, plot.Y, plot.Width, plot.Height);
            DrawAxisTitles(graphics, plot, options, textBrush);
        }

        private static void DrawAxisTitles(
            Graphics graphics,
            RectangleF plot,
            WasperChartRenderOptions options,
            Brush brush)
        {
            string xTitle = options.Axis?.XTitle?.Trim() ?? string.Empty;
            string yTitle = options.Axis?.YTitle?.Trim() ?? string.Empty;
            float scale = options.SafeTextScale;
            using var xFont = new Font(
                SystemFonts.MessageBoxFont.FontFamily,
                options.ScaledFont(Math.Max(7.0, options.Axis?.XTitleSize ?? 10.0)));
            using var yFont = new Font(
                SystemFonts.MessageBoxFont.FontFamily,
                options.ScaledFont(Math.Max(7.0, options.Axis?.YTitleSize ?? 10.0)));
            using var centered = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            if (xTitle.Length > 0)
            {
                graphics.DrawString(
                    xTitle,
                    xFont,
                    brush,
                    new RectangleF(plot.Left, plot.Bottom + (31f * scale), plot.Width, 25f * scale),
                    centered);
            }
            if (yTitle.Length > 0)
            {
                GraphicsState state = graphics.Save();
                graphics.TranslateTransform(17f * scale, plot.Top + (plot.Height / 2f));
                graphics.RotateTransform(-90f);
                graphics.DrawString(
                    yTitle,
                    yFont,
                    brush,
                    new RectangleF(-plot.Height / 2f, -14f * scale, plot.Height, 28f * scale),
                    centered);
                graphics.Restore(state);
            }
        }

        private static void DrawSeries(
            Graphics graphics,
            RectangleF plot,
            Bounds bounds,
            WasperChartSeries series,
            WasperChartRenderOptions options,
            ICollection<WasperChartHitTarget> hitTargets)
        {
            List<WasperChartPoint> points = (series.Points ?? new List<WasperChartPoint>())
                .Where(point => point?.IsValid == true)
                .ToList();
            if (points.Count == 0)
                return;
            Color color = series.Color.IsEmpty ? Color.SteelBlue : series.Color;
            using var linePen = new Pen(color, (float)Math.Max(1.0, series.LineWidth));
            linePen.DashStyle = DashStyleFor(series.LineType);
            PointF[] pixels = points.Select(point => Map(point, plot, bounds)).ToArray();
            if (series.LineWidth > 0.0 && pixels.Length > 1)
            {
                graphics.DrawLines(linePen, pixels);
                for (int index = 1; index < pixels.Length; index++)
                {
                    hitTargets.Add(Target(
                        WasperChartHitKind.Segment,
                        points[index],
                        series,
                        pixels[index - 1],
                        pixels[index]));
                }
            }
            // MarkerSize 0 draws a line-only chart; the hit targets are still emitted so linked
            // selection keeps working without visible markers.
            bool drawMarkers = series.MarkerSize > 0.0;
            for (int index = 0; index < pixels.Length; index++)
            {
                WasperChartPoint point = points[index];
                bool selected = options.SelectedIndividualIds?.Contains(point.IndividualId) == true;
                Color pointColor = point.Color ?? color;
                float radius = (float)Math.Max(3.0, series.MarkerSize * 0.65) + (selected ? 2f : 0f);
                RectangleF marker = new RectangleF(
                    pixels[index].X - radius,
                    pixels[index].Y - radius,
                    radius * 2f,
                    radius * 2f);
                using var fill = new SolidBrush(selected ? Color.Gold : pointColor);
                using var outline = new Pen(selected ? Color.FromArgb(70, 45, 0) : Color.White, selected ? 2.5f : 1f);
                if (drawMarkers || selected)
                {
                    graphics.FillEllipse(fill, marker);
                    graphics.DrawEllipse(outline, marker);
                }
                WasperChartHitTarget target = Target(
                    WasperChartHitKind.Point,
                    point,
                    series,
                    pixels[index],
                    pixels[index]);
                target.Bounds = marker;
                hitTargets.Add(target);
            }
        }

        /// <summary>
        /// Draws each point's label beside its marker. Selected points are placed first so their
        /// label always wins a contested spot, then the rest in order; a label that cannot find a
        /// free position is dropped rather than overprinting one already drawn.
        /// </summary>
        private static void DrawPointLabels(
            Graphics graphics,
            RectangleF plot,
            Bounds bounds,
            IReadOnlyList<WasperChartSeries> series,
            WasperChartRenderOptions options,
            ICollection<WasperChartHitTarget> hitTargets)
        {
            if (!options.ShowPointLabels)
                return;
            float scale = options.SafeTextScale;
            using var font = new Font(
                SystemFonts.MessageBoxFont.FontFamily,
                options.ScaledFont(Math.Max(6.5, (options.Axis?.XTextSize ?? 9.0) - 1.0)));
            using var brush = new SolidBrush(Color.FromArgb(70, 70, 70));
            using var selectedBrush = new SolidBrush(Color.FromArgb(120, 60, 0));
            using var halo = new SolidBrush(Color.FromArgb(205, 255, 255, 255));
            using var format = new StringFormat
            {
                FormatFlags = StringFormatFlags.NoWrap,
                Trimming = StringTrimming.EllipsisCharacter
            };

            var candidates = new List<Tuple<WasperChartPoint, WasperChartSeries>>();
            foreach (WasperChartSeries item in series)
            {
                foreach (WasperChartPoint point in item.Points ?? new List<WasperChartPoint>())
                {
                    if (point?.IsValid == true && !string.IsNullOrWhiteSpace(point.Label))
                        candidates.Add(Tuple.Create(point, item));
                }
            }
            var selectedIds = options.SelectedIndividualIds ?? new HashSet<int>();
            var placed = new List<RectangleF>();
            foreach (Tuple<WasperChartPoint, WasperChartSeries> candidate in candidates
                .OrderByDescending(entry => selectedIds.Contains(entry.Item1.IndividualId)))
            {
                WasperChartPoint point = candidate.Item1;
                bool selected = selectedIds.Contains(point.IndividualId);
                PointF anchor = Map(point, plot, bounds);
                SizeF size = graphics.MeasureString(point.Label, font);
                float gap = (float)Math.Max(3.0, candidate.Item2.MarkerSize * 0.65) + (3f * scale);
                RectangleF? slot = null;
                // A user-dragged label is honoured verbatim: it was moved precisely to escape the
                // automatic placement, so neither the overlap test nor the fallback positions apply.
                if (options.PointLabelOffsets != null &&
                    options.PointLabelOffsets.TryGetValue(point.IndividualId, out PointF moved))
                {
                    slot = new RectangleF(
                        anchor.X + moved.X,
                        anchor.Y + moved.Y,
                        size.Width,
                        size.Height);
                }
                else
                {
                    foreach (PointF offset in new[]
                    {
                        new PointF(gap, -size.Height / 2f),
                        new PointF(-gap - size.Width, -size.Height / 2f),
                        new PointF(-size.Width / 2f, -gap - size.Height),
                        new PointF(-size.Width / 2f, gap)
                    })
                    {
                        var box = new RectangleF(
                            anchor.X + offset.X,
                            anchor.Y + offset.Y,
                            size.Width,
                            size.Height);
                        if (box.Left < plot.Left || box.Right > plot.Right ||
                            box.Top < plot.Top || box.Bottom > plot.Bottom)
                        {
                            continue;
                        }
                        if (placed.Any(existing => existing.IntersectsWith(box)))
                            continue;
                        slot = box;
                        break;
                    }
                }
                if (slot == null)
                    continue;
                placed.Add(slot.Value);
                // A light halo keeps the text legible where it crosses a line or a filled marker.
                graphics.FillRectangle(halo, slot.Value);
                graphics.DrawString(
                    point.Label,
                    font,
                    selected ? selectedBrush : brush,
                    slot.Value,
                    format);
                // A leader line keeps a dragged label tied to its marker.
                if (options.PointLabelOffsets != null &&
                    options.PointLabelOffsets.ContainsKey(point.IndividualId))
                {
                    using var leader = new Pen(Color.FromArgb(110, 110, 110), 1f)
                    {
                        DashStyle = DashStyle.Dot
                    };
                    graphics.DrawLine(
                        leader,
                        anchor,
                        new PointF(
                            Math.Max(slot.Value.Left, Math.Min(slot.Value.Right, anchor.X)),
                            Math.Max(slot.Value.Top, Math.Min(slot.Value.Bottom, anchor.Y))));
                }
                hitTargets.Add(new WasperChartHitTarget
                {
                    Kind = WasperChartHitKind.Label,
                    IndividualId = point.IndividualId,
                    DataIndex = point.DataIndex,
                    SeriesKey = candidate.Item2.Key,
                    Label = point.Label,
                    XValue = point.X,
                    YValue = point.Y,
                    Bounds = slot.Value,
                    Anchor = anchor,
                    End = new PointF(slot.Value.X, slot.Value.Y)
                });
            }
        }

        /// <summary>
        /// Draws the categorical legend inside the top-right of the plot. Entries are supplied by
        /// the host, so the renderer stays unaware of what the categories mean.
        /// </summary>
        private static void DrawLegend(
            Graphics graphics,
            RectangleF plot,
            WasperChartRenderOptions options)
        {
            List<WasperChartLegendEntry> entries = (options.LegendEntries ??
                new List<WasperChartLegendEntry>())
                .Where(entry => entry != null)
                .ToList();
            if (entries.Count == 0)
                return;
            float scale = options.SafeTextScale;
            using var font = new Font(
                SystemFonts.MessageBoxFont.FontFamily,
                options.ScaledFont(Math.Max(6.5, options.Legend?.TextSize ?? 8.0)));
            using var textBrush = new SolidBrush(Color.FromArgb(55, 55, 55));
            float swatch = 9f * scale;
            float rowHeight = Math.Max(swatch + 3f, font.GetHeight(graphics) + 3f);
            float widest = 0f;
            foreach (WasperChartLegendEntry entry in entries)
            {
                float measured = graphics
                    .MeasureString(entry.Label ?? string.Empty, font)
                    .Width;
                if (measured > widest)
                    widest = measured;
            }
            float boxWidth = Math.Min(plot.Width * 0.45f, swatch + (6f * scale) + widest + (10f * scale));
            float boxHeight = Math.Min(plot.Height * 0.9f, (entries.Count * rowHeight) + (8f * scale));
            var box = new RectangleF(
                plot.Right - boxWidth - (8f * scale),
                plot.Top + (8f * scale),
                boxWidth,
                boxHeight);
            using var background = new SolidBrush(Color.FromArgb(232, 255, 255, 255));
            using var border = new Pen(Color.FromArgb(150, 150, 150), 1f);
            graphics.FillRectangle(background, box);
            graphics.DrawRectangle(border, box.X, box.Y, box.Width, box.Height);
            using var format = new StringFormat
            {
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
            float y = box.Top + (4f * scale);
            foreach (WasperChartLegendEntry entry in entries)
            {
                if (y + rowHeight > box.Bottom)
                    break;
                using var swatchBrush = new SolidBrush(entry.Color);
                var swatchBounds = new RectangleF(
                    box.Left + (5f * scale),
                    y + ((rowHeight - swatch) / 2f),
                    swatch,
                    swatch);
                graphics.FillRectangle(swatchBrush, swatchBounds);
                graphics.DrawRectangle(
                    border,
                    swatchBounds.X,
                    swatchBounds.Y,
                    swatchBounds.Width,
                    swatchBounds.Height);
                graphics.DrawString(
                    entry.Label ?? string.Empty,
                    font,
                    textBrush,
                    new RectangleF(
                        swatchBounds.Right + (5f * scale),
                        y,
                        box.Right - swatchBounds.Right - (9f * scale),
                        rowHeight),
                    format);
                y += rowHeight;
            }
        }

        private static WasperChartHitTarget Target(
            WasperChartHitKind kind,
            WasperChartPoint point,
            WasperChartSeries series,
            PointF anchor,
            PointF end)
        {
            return new WasperChartHitTarget
            {
                Kind = kind,
                IndividualId = point.IndividualId,
                DataIndex = point.DataIndex,
                SeriesKey = series.Key,
                Label = point.Label,
                XValue = point.X,
                YValue = point.Y,
                Anchor = anchor,
                End = end,
                Metadata = point.Metadata == null
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(point.Metadata, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static PointF Map(WasperChartPoint point, RectangleF plot, Bounds bounds)
        {
            float x = plot.Left + (float)((point.X - bounds.MinX) / (bounds.MaxX - bounds.MinX)) * plot.Width;
            float y = plot.Bottom - (float)((point.Y - bounds.MinY) / (bounds.MaxY - bounds.MinY)) * plot.Height;
            return new PointF(x, y);
        }

        private static DashStyle DashStyleFor(int lineType)
        {
            return lineType switch
            {
                1 => DashStyle.Dash,
                2 => DashStyle.Dot,
                3 => DashStyle.DashDot,
                _ => DashStyle.Solid
            };
        }

        private static string FormatNumber(double value)
        {
            double magnitude = Math.Abs(value);
            return magnitude > 0.0 && (magnitude >= 10000.0 || magnitude < 0.001)
                ? value.ToString("0.###E+0", CultureInfo.InvariantCulture)
                : value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private readonly struct Bounds
        {
            public Bounds(double minX, double maxX, double minY, double maxY)
            {
                MinX = minX;
                MaxX = maxX;
                MinY = minY;
                MaxY = maxY;
            }

            public double MinX { get; }
            public double MaxX { get; }
            public double MinY { get; }
            public double MaxY { get; }
        }
    }
}
