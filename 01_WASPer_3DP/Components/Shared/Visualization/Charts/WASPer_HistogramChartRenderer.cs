using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;

namespace WASPer_3DP
{
    /// <summary>
    /// Draw style for <see cref="WasperHistogramRenderer"/>.
    /// Bars and Region are both binned views of the same counts - Bars draws one rectangle per bin,
    /// Region joins the bin centres as a frequency polygon - so both are governed by the bin count.
    /// Density is bin-free: it estimates a smooth curve from the raw values and is governed by a
    /// bandwidth instead.
    /// </summary>
    public enum WasperHistogramMode
    {
        Bars,
        Region,
        Density
    }

    /// <summary>
    /// Render request for a single-variable distribution. The renderer stays stateless: the
    /// dataset, the variable to plot, the bin count, the bandwidth, and the draw mode all travel
    /// with the request.
    /// </summary>
    public sealed class WasperHistogramRequest
    {
        public WasperChartDataset Dataset { get; set; }
        public string VariableKey { get; set; } = string.Empty;
        public int BinCount { get; set; } = 12;
        public WasperHistogramMode Mode { get; set; } = WasperHistogramMode.Bars;

        /// <summary>
        /// Multiplier on the automatically chosen kernel bandwidth. 1.0 uses Silverman's rule of
        /// thumb; higher is smoother. Only meaningful in Density mode.
        /// </summary>
        public double BandwidthScale { get; set; } = 1.0;

        public int SafeBinCount => Math.Max(2, Math.Min(60, BinCount));
        public double SafeBandwidthScale => Math.Max(0.1, Math.Min(3.0, BandwidthScale));

        /// <summary>True when the mode is governed by bins rather than by a bandwidth.</summary>
        public bool UsesBins => Mode != WasperHistogramMode.Density;
    }

    /// <summary>
    /// One binned column. Retains the individual IDs it contains so a host can drive linked
    /// selection from a bin click.
    /// </summary>
    public sealed class WasperHistogramBin
    {
        public int Index { get; set; }
        public double Lower { get; set; }
        public double Upper { get; set; }
        public List<int> IndividualIds { get; set; } = new List<int>();
        public int Count => IndividualIds?.Count ?? 0;
        public double Center => (Lower + Upper) / 2.0;
    }

    /// <summary>
    /// Distribution renderer for one dataset variable. Shares the hit-target, selection, and
    /// render-result contracts used by the scatter, correlation, and parallel-coordinate engines,
    /// so the Sm01 Dashboard can link bin clicks to the same individual selection.
    /// </summary>
    public sealed class WasperHistogramRenderer : IWasperChartRenderer<WasperHistogramRequest>
    {
        /// <summary>Metadata key carrying the comma-separated individual IDs inside a bin.</summary>
        public const string IndividualIdsMetadataKey = "individual_ids";

        /// <summary>Points at which the density curve is evaluated across the data range.</summary>
        private const int DensityResolution = 256;

        public WasperChartRenderResult Render(
            WasperHistogramRequest data,
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

            WasperChartVariable variable = data?.Dataset?.FindVariable(data.VariableKey);
            float scale = options.SafeTextScale;
            using var titleFont = new Font(
                SystemFonts.MessageBoxFont.FontFamily, options.ScaledFont(10.0), FontStyle.Bold);
            using var labelFont = new Font(
                SystemFonts.MessageBoxFont.FontFamily, options.ScaledFont(7.5));
            using var dark = new SolidBrush(Color.FromArgb(55, 55, 55));
            using var centered = new StringFormat { Alignment = StringAlignment.Center };
            graphics.DrawString(
                string.IsNullOrWhiteSpace(options.Layout?.Title) ? "Histogram" : options.Layout.Title,
                titleFont,
                dark,
                new RectangleF(0f, 7f, options.SafeWidth, 25f * scale),
                centered);

            List<KeyValuePair<int, double>> samples = Samples(data);
            if (samples.Count == 0)
            {
                DrawEmpty(graphics, options, "No numeric values for the selected variable");
                return result;
            }
            ResolveRange(samples, out double minimum, out double maximum);
            // An explicit X range re-frames the whole distribution, so it is applied before the
            // bins are cut rather than by clipping the drawing afterwards.
            options.ApplyXLimits(ref minimum, ref maximum);

            float left = 16f + (36f * scale);
            float head = 12f + (28f * scale);
            float foot = 46f * scale;
            var plot = new RectangleF(
                left,
                head,
                Math.Max(40f, options.SafeWidth - left - 16f),
                Math.Max(40f, options.SafeHeight - head - foot));
            result.PlotBounds = plot;

            var selected = options.SelectedIndividualIds ?? new HashSet<int>();
            Color fill = Color.FromArgb(190, 79, 129, 189);
            Color emphasis = Color.FromArgb(225, 230, 120, 20);

            if (data.Mode == WasperHistogramMode.Density)
            {
                RenderDensity(
                    graphics, plot, options, data, samples, minimum, maximum,
                    labelFont, dark, scale, fill, emphasis, selected);
            }
            else
            {
                RenderBinned(
                    graphics, plot, options, data, samples, minimum, maximum,
                    labelFont, dark, scale, fill, emphasis, selected, result);
            }

            string xTitle = string.IsNullOrWhiteSpace(options.Axis?.XTitle)
                ? variable?.DisplayName ?? string.Empty
                : options.Axis.XTitle;
            if (!string.IsNullOrWhiteSpace(xTitle))
            {
                graphics.DrawString(
                    xTitle,
                    labelFont,
                    dark,
                    new RectangleF(
                        plot.Left,
                        options.SafeHeight - (18f * scale),
                        plot.Width,
                        16f * scale),
                    centered);
            }
            return result;
        }

        // -----------------------------------------------------------------------------------
        // Binned modes
        // -----------------------------------------------------------------------------------

        private static void RenderBinned(
            Graphics graphics,
            RectangleF plot,
            WasperChartRenderOptions options,
            WasperHistogramRequest data,
            IReadOnlyList<KeyValuePair<int, double>> samples,
            double minimum,
            double maximum,
            Font labelFont,
            Brush dark,
            float scale,
            Color fill,
            Color emphasis,
            HashSet<int> selected,
            WasperChartRenderResult result)
        {
            List<WasperHistogramBin> bins = BuildBins(samples, minimum, maximum, data.SafeBinCount);
            double peakValue = Math.Max(1, bins.Max(bin => bin.Count));
            double axisFloor = 0.0;
            options.ApplyYLimits(ref axisFloor, ref peakValue);
            int peak = Math.Max(1, (int)Math.Round(peakValue));
            DrawValueAxis(graphics, plot, labelFont, dark, scale, peak, Math.Min(peak, 5), true);
            DrawAxisLines(graphics, plot);

            float binWidth = plot.Width / bins.Count;
            bool bars = data.Mode == WasperHistogramMode.Bars;
            if (!bars)
                DrawRegion(graphics, plot, bins, peak, fill);

            for (int index = 0; index < bins.Count; index++)
            {
                WasperHistogramBin bin = bins[index];
                float height = (float)(bin.Count / (double)peak * plot.Height);
                var bounds = new RectangleF(
                    plot.Left + (index * binWidth),
                    plot.Bottom - height,
                    binWidth,
                    height);
                bool binSelected = bin.IndividualIds.Any(selected.Contains);
                if (bars && bin.Count > 0)
                {
                    using var barBrush = new SolidBrush(binSelected ? emphasis : fill);
                    using var barPen = new Pen(Color.FromArgb(210, 45, 45, 45), binSelected ? 1.6f : 0.8f);
                    var barBounds = new RectangleF(
                        bounds.X + 1f,
                        bounds.Y,
                        Math.Max(1f, bounds.Width - 2f),
                        Math.Max(1f, bounds.Height));
                    graphics.FillRectangle(barBrush, barBounds);
                    graphics.DrawRectangle(barPen, barBounds.X, barBounds.Y, barBounds.Width, barBounds.Height);
                }
                else if (binSelected && bin.Count > 0)
                {
                    using var markerPen = new Pen(emphasis, 2f);
                    using var markerBrush = new SolidBrush(Color.FromArgb(70, emphasis));
                    graphics.FillRectangle(
                        markerBrush,
                        new RectangleF(
                            bounds.X + 1f,
                            plot.Top,
                            Math.Max(1f, bounds.Width - 2f),
                            plot.Height));
                    graphics.DrawLine(
                        markerPen,
                        bounds.X + (bounds.Width / 2f),
                        plot.Bottom - height,
                        bounds.X + (bounds.Width / 2f),
                        plot.Bottom);
                }

                var target = new WasperChartHitTarget
                {
                    Kind = WasperChartHitKind.Cell,
                    IndividualId = bin.IndividualIds.Count > 0 ? bin.IndividualIds[0] : -1,
                    DataIndex = index,
                    SeriesKey = "histogram",
                    Label = $"[{FormatValue(bin.Lower)}, {FormatValue(bin.Upper)}) n={bin.Count}",
                    XValue = bin.Center,
                    YValue = bin.Count,
                    Bounds = new RectangleF(bounds.X, plot.Top, binWidth, plot.Height),
                    Anchor = new PointF(bounds.X + (binWidth / 2f), plot.Bottom - (height / 2f))
                };
                target.Metadata[IndividualIdsMetadataKey] = string.Join(",", bin.IndividualIds);
                result.HitTargets.Add(target);
            }
            DrawBinEdgeLabels(graphics, plot, bins, labelFont, dark, maximum, scale);
        }

        /// <summary>
        /// Draws the distribution as a frequency polygon: straight segments joining the bin
        /// centres, anchored to zero half a bin outside the first and last centre.
        /// A smoothed spline was tried here first and was abandoned - a cardinal spline through bin
        /// counts overshoots its control points, so it invented peaks taller than the true count and
        /// dipped below zero between empty bins. A polygon cannot misrepresent its own counts;
        /// Density mode exists for callers who want a genuinely smooth estimate.
        /// </summary>
        private static void DrawRegion(
            Graphics graphics,
            RectangleF plot,
            IReadOnlyList<WasperHistogramBin> bins,
            int peak,
            Color fill)
        {
            float binWidth = plot.Width / bins.Count;
            // The plot edges already sit half a bin outside the outer centres, so anchoring there
            // closes the polygon at zero exactly as a frequency polygon requires.
            var points = new List<PointF> { new PointF(plot.Left, plot.Bottom) };
            for (int index = 0; index < bins.Count; index++)
            {
                float height = (float)(bins[index].Count / (double)peak * plot.Height);
                points.Add(new PointF(
                    plot.Left + (index * binWidth) + (binWidth / 2f),
                    plot.Bottom - height));
            }
            points.Add(new PointF(plot.Right, plot.Bottom));
            if (points.Count < 3)
                return;
            using var path = new GraphicsPath();
            path.AddLines(points.ToArray());
            path.CloseFigure();
            using var areaBrush = new SolidBrush(Color.FromArgb(110, fill));
            using var outlinePen = new Pen(Color.FromArgb(230, fill), 1.8f);
            graphics.FillPath(areaBrush, path);
            graphics.DrawPath(outlinePen, path);
            // Bin centres, so it stays readable which counts the polygon interpolates between.
            using var vertexBrush = new SolidBrush(Color.FromArgb(235, fill));
            const float radius = 2.2f;
            for (int index = 1; index < points.Count - 1; index++)
            {
                graphics.FillEllipse(
                    vertexBrush,
                    points[index].X - radius,
                    points[index].Y - radius,
                    radius * 2f,
                    radius * 2f);
            }
        }

        // -----------------------------------------------------------------------------------
        // Density mode
        // -----------------------------------------------------------------------------------

        /// <summary>
        /// Gaussian kernel density estimate over the raw values. No bins are involved: the curve is
        /// governed by the bandwidth alone. A rug along the baseline shows where the actual samples
        /// lie, which keeps the estimate honest when the sample count is small enough that the
        /// smoothness is mostly a property of the bandwidth.
        /// </summary>
        private static void RenderDensity(
            Graphics graphics,
            RectangleF plot,
            WasperChartRenderOptions options,
            WasperHistogramRequest data,
            IReadOnlyList<KeyValuePair<int, double>> samples,
            double minimum,
            double maximum,
            Font labelFont,
            Brush dark,
            float scale,
            Color fill,
            Color emphasis,
            HashSet<int> selected)
        {
            double bandwidth = Bandwidth(samples, minimum, maximum) * data.SafeBandwidthScale;
            var densities = new double[DensityResolution];
            double span = maximum - minimum;
            double peak = 0.0;
            for (int index = 0; index < DensityResolution; index++)
            {
                double x = minimum + (span * index / (DensityResolution - 1.0));
                double sum = 0.0;
                foreach (KeyValuePair<int, double> sample in samples)
                {
                    double u = (x - sample.Value) / bandwidth;
                    sum += Math.Exp(-0.5 * u * u);
                }
                densities[index] = sum / (samples.Count * bandwidth * Math.Sqrt(2.0 * Math.PI));
                if (densities[index] > peak)
                    peak = densities[index];
            }
            if (peak <= 0.0)
                peak = 1.0;
            double densityFloor = 0.0;
            options.ApplyYLimits(ref densityFloor, ref peak);

            DrawValueAxis(graphics, plot, labelFont, dark, scale, peak, 5, false);
            DrawAxisLines(graphics, plot);

            var points = new List<PointF> { new PointF(plot.Left, plot.Bottom) };
            for (int index = 0; index < DensityResolution; index++)
            {
                points.Add(new PointF(
                    plot.Left + (plot.Width * index / (DensityResolution - 1f)),
                    plot.Bottom - (float)(densities[index] / peak * plot.Height)));
            }
            points.Add(new PointF(plot.Right, plot.Bottom));
            using (var path = new GraphicsPath())
            {
                path.AddLines(points.ToArray());
                path.CloseFigure();
                using var areaBrush = new SolidBrush(Color.FromArgb(110, fill));
                using var outlinePen = new Pen(Color.FromArgb(230, fill), 1.8f);
                graphics.FillPath(areaBrush, path);
                graphics.DrawPath(outlinePen, path);
            }

            float rug = 7f * scale;
            using var rugPen = new Pen(Color.FromArgb(170, 60, 60, 60), 1f);
            using var rugSelectedPen = new Pen(emphasis, 2.2f);
            foreach (KeyValuePair<int, double> sample in samples)
            {
                float x = plot.Left + (float)((sample.Value - minimum) / span * plot.Width);
                graphics.DrawLine(
                    selected.Contains(sample.Key) ? rugSelectedPen : rugPen,
                    x,
                    plot.Bottom,
                    x,
                    plot.Bottom - rug);
            }
            DrawRangeLabels(graphics, plot, labelFont, dark, scale, minimum, maximum);
        }

        /// <summary>
        /// Silverman's rule of thumb, using the smaller of the standard deviation and the
        /// interquartile-range estimate so one outlier cannot over-smooth the whole curve.
        /// </summary>
        private static double Bandwidth(
            IReadOnlyList<KeyValuePair<int, double>> samples,
            double minimum,
            double maximum)
        {
            int count = samples.Count;
            double fallback = Math.Max((maximum - minimum) / 10.0, 1e-9);
            if (count < 2)
                return fallback;
            List<double> values = samples.Select(sample => sample.Value).OrderBy(value => value).ToList();
            double mean = values.Average();
            double variance = values.Sum(value => (value - mean) * (value - mean)) / (count - 1);
            double deviation = Math.Sqrt(Math.Max(0.0, variance));
            double iqr = Quantile(values, 0.75) - Quantile(values, 0.25);
            double spread = iqr > 0.0 ? Math.Min(deviation, iqr / 1.34) : deviation;
            if (spread <= 0.0)
                spread = deviation;
            double bandwidth = 0.9 * spread * Math.Pow(count, -0.2);
            return bandwidth > 0.0 ? bandwidth : fallback;
        }

        private static double Quantile(IReadOnlyList<double> sorted, double fraction)
        {
            if (sorted.Count == 0)
                return 0.0;
            double position = fraction * (sorted.Count - 1);
            int lower = (int)Math.Floor(position);
            int upper = (int)Math.Ceiling(position);
            return lower == upper
                ? sorted[lower]
                : sorted[lower] + ((sorted[upper] - sorted[lower]) * (position - lower));
        }

        // -----------------------------------------------------------------------------------
        // Shared drawing
        // -----------------------------------------------------------------------------------

        private static void DrawAxisLines(Graphics graphics, RectangleF plot)
        {
            using var axisPen = new Pen(Color.FromArgb(115, 115, 115), 1f);
            graphics.DrawLine(axisPen, plot.Left, plot.Top, plot.Left, plot.Bottom);
            graphics.DrawLine(axisPen, plot.Left, plot.Bottom, plot.Right, plot.Bottom);
        }

        private static void DrawValueAxis(
            Graphics graphics,
            RectangleF plot,
            Font labelFont,
            Brush dark,
            float scale,
            double peak,
            int tickCount,
            bool integerTicks)
        {
            tickCount = Math.Max(1, tickCount);
            using var gridPen = new Pen(Color.FromArgb(232, 232, 232), 1f);
            using var rightAligned = new StringFormat
            {
                Alignment = StringAlignment.Far,
                LineAlignment = StringAlignment.Center
            };
            for (int tick = 0; tick <= tickCount; tick++)
            {
                double value = peak * tick / tickCount;
                float y = plot.Bottom - (float)(value / peak * plot.Height);
                if (tick > 0)
                    graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
                graphics.DrawString(
                    integerTicks
                        ? Math.Round(value).ToString("0", CultureInfo.InvariantCulture)
                        : FormatValue(value),
                    labelFont,
                    dark,
                    new RectangleF(2f, y - (7f * scale), plot.Left - 8f, 14f * scale),
                    rightAligned);
            }
        }

        private static void DrawBinEdgeLabels(
            Graphics graphics,
            RectangleF plot,
            IReadOnlyList<WasperHistogramBin> bins,
            Font font,
            Brush brush,
            double maximum,
            float scale)
        {
            using var format = new StringFormat { Alignment = StringAlignment.Center };
            int step = Math.Max(
                1,
                (int)Math.Ceiling(bins.Count / Math.Max(1.0, plot.Width / (58f * scale))));
            float binWidth = plot.Width / bins.Count;
            for (int index = 0; index <= bins.Count; index += step)
            {
                double value = index >= bins.Count ? maximum : bins[index].Lower;
                float x = plot.Left + (index * binWidth);
                graphics.DrawString(
                    FormatValue(value),
                    font,
                    brush,
                    new RectangleF(
                        x - (29f * scale),
                        plot.Bottom + (3f * scale),
                        58f * scale,
                        14f * scale),
                    format);
            }
        }

        /// <summary>Evenly spaced X labels for the bin-free density view.</summary>
        private static void DrawRangeLabels(
            Graphics graphics,
            RectangleF plot,
            Font font,
            Brush brush,
            float scale,
            double minimum,
            double maximum)
        {
            using var format = new StringFormat { Alignment = StringAlignment.Center };
            int ticks = Math.Max(2, Math.Min(8, (int)(plot.Width / (62f * scale))));
            for (int index = 0; index <= ticks; index++)
            {
                double value = minimum + ((maximum - minimum) * index / ticks);
                float x = plot.Left + (plot.Width * index / ticks);
                graphics.DrawString(
                    FormatValue(value),
                    font,
                    brush,
                    new RectangleF(
                        x - (29f * scale),
                        plot.Bottom + (3f * scale),
                        58f * scale,
                        14f * scale),
                    format);
            }
        }

        // -----------------------------------------------------------------------------------
        // Data preparation
        // -----------------------------------------------------------------------------------

        private static List<KeyValuePair<int, double>> Samples(WasperHistogramRequest data)
        {
            var samples = new List<KeyValuePair<int, double>>();
            if (data?.Dataset == null || string.IsNullOrWhiteSpace(data.VariableKey))
                return samples;
            foreach (WasperChartIndividual individual in
                data.Dataset.Individuals ?? new List<WasperChartIndividual>())
            {
                if (individual != null && individual.TryGetValue(data.VariableKey, out double value))
                    samples.Add(new KeyValuePair<int, double>(individual.IndividualId, value));
            }
            return samples;
        }

        private static void ResolveRange(
            IReadOnlyList<KeyValuePair<int, double>> samples,
            out double minimum,
            out double maximum)
        {
            minimum = samples.Min(sample => sample.Value);
            maximum = samples.Max(sample => sample.Value);
            if (Math.Abs(maximum - minimum) > 1e-12)
                return;
            double pad = Math.Abs(minimum) <= 1e-12 ? 0.5 : Math.Abs(minimum) * 0.05;
            minimum -= pad;
            maximum += pad;
        }

        private static List<WasperHistogramBin> BuildBins(
            IReadOnlyList<KeyValuePair<int, double>> samples,
            double minimum,
            double maximum,
            int count)
        {
            double width = (maximum - minimum) / count;
            var bins = new List<WasperHistogramBin>(count);
            for (int index = 0; index < count; index++)
            {
                bins.Add(new WasperHistogramBin
                {
                    Index = index,
                    Lower = minimum + (index * width),
                    Upper = minimum + ((index + 1) * width)
                });
            }
            foreach (KeyValuePair<int, double> sample in samples)
            {
                int index = (int)Math.Floor((sample.Value - minimum) / width);
                index = Math.Max(0, Math.Min(count - 1, index));
                bins[index].IndividualIds.Add(sample.Key);
            }
            return bins;
        }

        private static string FormatValue(double value)
        {
            double magnitude = Math.Abs(value);
            string format = magnitude >= 1000.0 || (magnitude > 0.0 && magnitude < 0.01)
                ? "0.##E+0"
                : magnitude >= 100.0
                    ? "0"
                    : magnitude >= 1.0
                        ? "0.##"
                        : "0.###";
            return value.ToString(format, CultureInfo.InvariantCulture);
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
}
