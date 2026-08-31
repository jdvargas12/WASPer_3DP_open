using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;

namespace WASPer_3DP
{
    public sealed class WasperCorrelationHeatmapRenderer : IWasperChartRenderer<WasperChartDataset>
    {
        /// <summary>
        /// Upper bound on plotted variables. Hosts that expose their own variable/group filter
        /// can raise it; the renderer never draws more cells than this per side.
        /// </summary>
        public int MaxVariables { get; set; } = 10;

        public WasperChartRenderResult Render(
            WasperChartDataset data,
            WasperChartRenderOptions options)
        {
            options ??= new WasperChartRenderOptions();
            var result = new WasperChartRenderResult
            {
                Bitmap = options.CreateBitmap(),
                PixelScale = options.SafePixelScale
            };
            using Graphics graphics = Graphics.FromImage(result.Bitmap);
            options.PrepareGraphics(graphics);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.White);
            List<WasperChartVariable> variables = (data?.Variables ?? new List<WasperChartVariable>())
                .Take(Math.Max(2, MaxVariables))
                .ToList();
            if (variables.Count == 0)
            {
                DrawEmpty(graphics, options, "No numeric variables");
                return result;
            }
            // Correlation is undefined for fewer than 2 samples (Correlation() below returns NaN
            // for every cell in that case), which would otherwise render as a uniformly blank grey
            // grid with no numbers - easy to mistake for "nothing loaded" rather than "not enough
            // captured iterations yet".
            if ((data?.Individuals?.Count ?? 0) < 2)
            {
                DrawEmpty(graphics, options, "Need at least 2 captured iterations to compute correlation");
                return result;
            }

            float scale = options.SafeTextScale;
            float label = Math.Min(
                110f * scale,
                Math.Min(options.SafeWidth * 0.23f, options.SafeHeight * 0.28f));
            float top = 12f + (26f * scale);
            float available = Math.Min(
                options.SafeWidth - label - 14f,
                options.SafeHeight - top - label - 8f);
            float cell = Math.Max(7f, available / variables.Count);
            RectangleF matrix = new RectangleF(label, top, cell * variables.Count, cell * variables.Count);
            result.PlotBounds = matrix;
            using var titleFont = new Font(
                SystemFonts.MessageBoxFont.FontFamily, options.ScaledFont(10.0), FontStyle.Bold);
            using var labelFont = new Font(
                SystemFonts.MessageBoxFont.FontFamily, options.ScaledFont(7.5));
            using var dark = new SolidBrush(Color.FromArgb(55, 55, 55));
            using var centered = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            graphics.DrawString(
                options.Layout?.Title ?? "Correlation heatmap",
                titleFont,
                dark,
                new RectangleF(0f, 7f, options.SafeWidth, 25f * scale),
                centered);

            for (int row = 0; row < variables.Count; row++)
            {
                DrawVariableLabel(graphics, variables[row].Name, labelFont, dark,
                    new RectangleF(2f, top + (row * cell), label - 6f, cell), false, scale);
                DrawVariableLabel(graphics, variables[row].Name, labelFont, dark,
                    new RectangleF(label + (row * cell), matrix.Bottom + 3f, cell, label - 5f), true, scale);
                for (int column = 0; column < variables.Count; column++)
                {
                    double correlation = Correlation(data, variables[column].Key, variables[row].Key);
                    RectangleF bounds = new RectangleF(
                        matrix.Left + (column * cell),
                        matrix.Top + (row * cell),
                        cell,
                        cell);
                    using var fill = new SolidBrush(CorrelationColor(correlation));
                    using var border = new Pen(Color.FromArgb(235, 235, 235));
                    graphics.FillRectangle(fill, bounds);
                    graphics.DrawRectangle(border, bounds.X, bounds.Y, bounds.Width, bounds.Height);
                    if (cell >= 31f * scale && !double.IsNaN(correlation))
                    {
                        using var valueBrush = new SolidBrush(Math.Abs(correlation) > 0.55
                            ? Color.White
                            : Color.FromArgb(45, 45, 45));
                        graphics.DrawString(
                            correlation.ToString("0.00", CultureInfo.InvariantCulture),
                            labelFont,
                            valueBrush,
                            bounds,
                            centered);
                    }
                    result.HitTargets.Add(new WasperChartHitTarget
                    {
                        Kind = WasperChartHitKind.Cell,
                        SeriesKey = variables[column].Key + "|" + variables[row].Key,
                        Label = variables[column].DisplayName + " / " + variables[row].DisplayName,
                        XValue = correlation,
                        Bounds = bounds,
                        Anchor = new PointF(bounds.Left + (bounds.Width / 2f), bounds.Top + (bounds.Height / 2f))
                    });
                }
            }
            DrawLegend(graphics, matrix, options, labelFont, dark, scale);
            return result;
        }

        /// <summary>
        /// Vertical color-scale key to the right of the matrix, spanning the same -1..+1 range as
        /// <see cref="CorrelationColor"/>. Skipped when the card is too narrow or too short for the
        /// bar plus its tick labels to read cleanly, mirroring the value-label cutoff on the cells
        /// themselves.
        /// </summary>
        private static void DrawLegend(
            Graphics graphics,
            RectangleF matrix,
            WasperChartRenderOptions options,
            Font labelFont,
            Brush textBrush,
            float scale)
        {
            float barWidth = 12f * scale;
            float gap = 12f * scale;
            float tickTextWidth = 26f * scale;
            float left = matrix.Right + gap;
            if (matrix.Height < 40f * scale ||
                left + barWidth + tickTextWidth + (4f * scale) > options.SafeWidth)
            {
                return;
            }

            var bar = new RectangleF(left, matrix.Top, barWidth, matrix.Height);
            const int steps = 48;
            float stepHeight = bar.Height / steps;
            for (int i = 0; i < steps; i++)
            {
                // Top of the bar is +1 (strongest positive), bottom is -1 (strongest negative),
                // matching how CorrelationColor shades the matrix cells themselves.
                double value = 1.0 - (2.0 * i / (steps - 1));
                using var stepBrush = new SolidBrush(CorrelationColor(value));
                // The +0.75f overlap hides the antialiased seams between adjacent steps.
                graphics.FillRectangle(
                    stepBrush,
                    bar.X,
                    bar.Y + (i * stepHeight),
                    bar.Width,
                    stepHeight + 0.75f);
            }
            using var border = new Pen(Color.FromArgb(180, 180, 180));
            graphics.DrawRectangle(border, bar.X, bar.Y, bar.Width, bar.Height);

            using var tickFormat = new StringFormat { LineAlignment = StringAlignment.Center };
            var tickBounds = new RectangleF(bar.Right + (3f * scale), 0f, tickTextWidth, 14f * scale);
            DrawLegendTick(graphics, "+1", labelFont, textBrush, tickBounds, bar.Top, tickFormat);
            DrawLegendTick(graphics, "0", labelFont, textBrush, tickBounds, bar.Top + (bar.Height / 2f), tickFormat);
            DrawLegendTick(graphics, "-1", labelFont, textBrush, tickBounds, bar.Bottom, tickFormat);
        }

        private static void DrawLegendTick(
            Graphics graphics,
            string text,
            Font font,
            Brush brush,
            RectangleF bounds,
            float centerY,
            StringFormat format)
        {
            bounds.Y = centerY - (bounds.Height / 2f);
            graphics.DrawString(text, font, brush, bounds, format);
        }

        private static double Correlation(WasperChartDataset data, string xKey, string yKey)
        {
            var pairs = new List<Tuple<double, double>>();
            foreach (WasperChartIndividual individual in data?.Individuals ?? new List<WasperChartIndividual>())
            {
                if (individual.TryGetValue(xKey, out double x) && individual.TryGetValue(yKey, out double y))
                    pairs.Add(Tuple.Create(x, y));
            }
            if (pairs.Count < 2)
                return double.NaN;
            double meanX = pairs.Average(pair => pair.Item1);
            double meanY = pairs.Average(pair => pair.Item2);
            double numerator = 0.0;
            double sumX = 0.0;
            double sumY = 0.0;
            foreach (Tuple<double, double> pair in pairs)
            {
                double dx = pair.Item1 - meanX;
                double dy = pair.Item2 - meanY;
                numerator += dx * dy;
                sumX += dx * dx;
                sumY += dy * dy;
            }
            double denominator = Math.Sqrt(sumX * sumY);
            return denominator <= 1e-15 ? double.NaN : numerator / denominator;
        }

        private static Color CorrelationColor(double value)
        {
            if (double.IsNaN(value))
                return Color.FromArgb(235, 235, 235);
            double clamped = Math.Max(-1.0, Math.Min(1.0, value));
            Color neutral = Color.FromArgb(247, 247, 247);
            Color endpoint = clamped < 0.0
                ? Color.FromArgb(49, 96, 171)
                : Color.FromArgb(193, 50, 55);
            double amount = Math.Abs(clamped);
            return Color.FromArgb(
                (int)Math.Round(neutral.R + ((endpoint.R - neutral.R) * amount)),
                (int)Math.Round(neutral.G + ((endpoint.G - neutral.G) * amount)),
                (int)Math.Round(neutral.B + ((endpoint.B - neutral.B) * amount)));
        }

        private static void DrawVariableLabel(
            Graphics graphics,
            string text,
            Font font,
            Brush brush,
            RectangleF bounds,
            bool rotate,
            float scale)
        {
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter
            };
            if (!rotate)
            {
                graphics.DrawString(text ?? string.Empty, font, brush, bounds, format);
                return;
            }
            GraphicsState state = graphics.Save();
            graphics.TranslateTransform(bounds.Left + (bounds.Width / 2f), bounds.Top);
            graphics.RotateTransform(-55f);
            graphics.DrawString(
                text ?? string.Empty,
                font,
                brush,
                new RectangleF(-110f * scale, -8f * scale, 110f * scale, 18f * scale),
                format);
            graphics.Restore(state);
        }

        private static void DrawEmpty(Graphics graphics, WasperChartRenderOptions options, string text)
        {
            using var brush = new SolidBrush(Color.DimGray);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            graphics.DrawString(text, SystemFonts.MessageBoxFont, brush,
                new RectangleF(0f, 0f, options.SafeWidth, options.SafeHeight), format);
        }
    }

    public sealed class WasperParallelCoordinatesRenderer : IWasperChartRenderer<WasperChartDataset>
    {
        /// <summary>
        /// Upper bound on plotted axes. Hosts that expose their own variable/group filter can
        /// raise it; the renderer never draws more axes than this.
        /// </summary>
        public int MaxVariables { get; set; } = 10;

        public WasperChartRenderResult Render(
            WasperChartDataset data,
            WasperChartRenderOptions options)
        {
            options ??= new WasperChartRenderOptions();
            var result = new WasperChartRenderResult
            {
                Bitmap = options.CreateBitmap(),
                PixelScale = options.SafePixelScale
            };
            using Graphics graphics = Graphics.FromImage(result.Bitmap);
            options.PrepareGraphics(graphics);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.White);
            List<WasperChartVariable> variables = (data?.Variables ?? new List<WasperChartVariable>())
                .Take(Math.Max(2, MaxVariables))
                .ToList();
            if (variables.Count < 2)
            {
                DrawEmpty(graphics, options, "At least two numeric variables are required");
                return result;
            }

            // The first and last axis labels are centred on their axis and 104 px wide, so the
            // plot needs at least half a label of margin on each side or they clip at the edges.
            float scale = options.SafeTextScale;
            float side = Math.Min(options.SafeWidth * 0.18f, 56f * scale);
            float head = 14f + (34f * scale);
            float foot = 46f * scale;
            RectangleF plot = new RectangleF(
                side,
                head,
                Math.Max(40f, options.SafeWidth - (side * 2f)),
                Math.Max(40f, options.SafeHeight - head - foot));
            result.PlotBounds = plot;
            double[] minima = variables.Select(variable => Values(data, variable.Key).DefaultIfEmpty(0.0).Min()).ToArray();
            double[] maxima = variables.Select(variable => Values(data, variable.Key).DefaultIfEmpty(1.0).Max()).ToArray();
            using var axisPen = new Pen(Color.FromArgb(115, 115, 115), 1f);
            using var labelBrush = new SolidBrush(Color.FromArgb(55, 55, 55));
            using var labelFont = new Font(
                SystemFonts.MessageBoxFont.FontFamily, options.ScaledFont(7.5));
            using var titleFont = new Font(
                SystemFonts.MessageBoxFont.FontFamily, options.ScaledFont(10.0), FontStyle.Bold);
            using var centered = new StringFormat { Alignment = StringAlignment.Center };
            graphics.DrawString(options.Layout?.Title ?? "Parallel coordinates", titleFont, labelBrush,
                new RectangleF(0f, 7f, options.SafeWidth, 25f * scale), centered);
            for (int axis = 0; axis < variables.Count; axis++)
            {
                float x = AxisX(plot, axis, variables.Count);
                graphics.DrawLine(axisPen, x, plot.Top, x, plot.Bottom);
                graphics.DrawString(variables[axis].Name, labelFont, labelBrush,
                    new RectangleF(
                        x - (52f * scale),
                        plot.Bottom + (5f * scale),
                        104f * scale,
                        31f * scale),
                    centered);
            }

            // Unselected lines are drawn translucent so large individual counts don't overwhelm the
            // view, but that same fixed low alpha makes a study with only a handful of iterations
            // (most visibly just 1, which also lands dead-centre since min==max on every axis) look
            // like the chart rendered nothing at all. Scale the alpha up as the individual count
            // drops, so a few lines still read clearly while large studies keep the faint overlay.
            int individualCount = Math.Max(1, data?.Individuals?.Count ?? 0);
            int unselectedAlpha = Math.Max(48, Math.Min(220, 700 / individualCount));

            foreach (WasperChartIndividual individual in data?.Individuals ?? new List<WasperChartIndividual>())
            {
                var pixels = new List<PointF>();
                bool complete = true;
                for (int axis = 0; axis < variables.Count; axis++)
                {
                    if (!individual.TryGetValue(variables[axis].Key, out double value))
                    {
                        complete = false;
                        break;
                    }
                    double span = maxima[axis] - minima[axis];
                    double normalized = Math.Abs(span) <= 1e-12 ? 0.5 : (value - minima[axis]) / span;
                    pixels.Add(new PointF(
                        AxisX(plot, axis, variables.Count),
                        plot.Bottom - ((float)normalized * plot.Height)));
                }
                if (!complete || pixels.Count < 2)
                    continue;
                bool selected = options.SelectedIndividualIds?.Contains(individual.IndividualId) == true;
                using var pen = new Pen(
                    selected ? Color.FromArgb(230, 120, 20) : Color.FromArgb(unselectedAlpha, 75, 130, 150),
                    selected ? 3f : 1f);
                graphics.DrawLines(pen, pixels.ToArray());
                for (int index = 1; index < pixels.Count; index++)
                {
                    result.HitTargets.Add(new WasperChartHitTarget
                    {
                        Kind = WasperChartHitKind.Segment,
                        IndividualId = individual.IndividualId,
                        DataIndex = individual.IndividualId,
                        SeriesKey = "parallel",
                        Label = individual.Name,
                        Anchor = pixels[index - 1],
                        End = pixels[index]
                    });
                }
            }
            return result;
        }

        private static IEnumerable<double> Values(WasperChartDataset data, string key)
        {
            foreach (WasperChartIndividual individual in data?.Individuals ?? new List<WasperChartIndividual>())
            {
                if (individual.TryGetValue(key, out double value))
                    yield return value;
            }
        }

        private static float AxisX(RectangleF plot, int axis, int count) =>
            count <= 1 ? plot.Left : plot.Left + ((plot.Width * axis) / (count - 1));

        private static void DrawEmpty(Graphics graphics, WasperChartRenderOptions options, string text)
        {
            using var brush = new SolidBrush(Color.DimGray);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };
            graphics.DrawString(text, SystemFonts.MessageBoxFont, brush,
                new RectangleF(0f, 0f, options.SafeWidth, options.SafeHeight), format);
        }
    }
}
