using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace WASPer_3DP
{
    /// <summary>
    /// Host-neutral rendering options. Grasshopper components and Dashboard controls can supply
    /// different pixel sizes while sharing the same visual settings and selected individuals.
    /// </summary>
    public sealed class WasperChartRenderOptions
    {
        public int Width { get; set; } = 960;
        public int Height { get; set; } = 600;
        public int Dpi { get; set; } = 96;

        /// <summary>
        /// Physical pixels rendered for each logical chart unit. Interactive Eto hosts set this
        /// from their window's logical-pixel size so Retina/high-DPI displays receive a sharp
        /// bitmap without changing chart geometry or hit-test coordinates.
        /// </summary>
        public double PixelScale { get; set; } = 1.0;
        public WasperChartAxisSettings Axis { get; set; } = new WasperChartAxisSettings();
        public WasperChartLegendSettings Legend { get; set; } = new WasperChartLegendSettings();
        public WasperChartMarkerLineSettings MarkerLine { get; set; } =
            new WasperChartMarkerLineSettings();
        public WasperChartLayoutSettings Layout { get; set; } = new WasperChartLayoutSettings();
        public HashSet<int> SelectedIndividualIds { get; set; } = new HashSet<int>();

        /// <summary>
        /// Uniform multiplier applied by every renderer to font sizes and to the text-dependent
        /// plot margins. 1.0 is the renderer's designed size; hosts that expose a "text size"
        /// control set this rather than editing each individual font setting.
        /// </summary>
        public double TextScale { get; set; } = 1.0;

        /// <summary>
        /// Swatch/label pairs drawn as an on-chart legend. Empty means no legend is drawn, which
        /// keeps every existing caller unchanged.
        /// </summary>
        public List<WasperChartLegendEntry> LegendEntries { get; set; } =
            new List<WasperChartLegendEntry>();

        /// <summary>
        /// Draws each point's label next to its marker. Labels that would overlap one already
        /// placed are skipped, so a dense chart stays readable instead of turning into a smear.
        /// </summary>
        public bool ShowPointLabels { get; set; }

        /// <summary>
        /// Per-individual pixel nudges applied to point labels, so a host can let the user drag a
        /// label clear of its neighbours. Missing entries use the automatic placement.
        /// </summary>
        public Dictionary<int, PointF> PointLabelOffsets { get; set; } =
            new Dictionary<int, PointF>();

        /// <summary>
        /// Explicit axis limits. Null keeps the renderer's data-driven bounds, so a caller can pin
        /// one end of an axis and leave the other automatic.
        /// </summary>
        public double? XMinimum { get; set; }
        public double? XMaximum { get; set; }
        public double? YMinimum { get; set; }
        public double? YMaximum { get; set; }

        /// <summary>Applies whichever limits were supplied over a data-derived range.</summary>
        public void ApplyXLimits(ref double minimum, ref double maximum) =>
            ApplyLimits(XMinimum, XMaximum, ref minimum, ref maximum);

        public void ApplyYLimits(ref double minimum, ref double maximum) =>
            ApplyLimits(YMinimum, YMaximum, ref minimum, ref maximum);

        private static void ApplyLimits(
            double? lower,
            double? upper,
            ref double minimum,
            ref double maximum)
        {
            if (lower.HasValue && !double.IsNaN(lower.Value))
                minimum = lower.Value;
            if (upper.HasValue && !double.IsNaN(upper.Value))
                maximum = upper.Value;
            if (maximum > minimum)
                return;
            // An inverted or collapsed range would divide by zero downstream; widen it instead of
            // silently discarding the limits the caller asked for.
            double pad = Math.Max(1e-9, Math.Abs(minimum) * 0.05);
            maximum = minimum + pad;
        }

        public int SafeWidth => Math.Max(64, Math.Min(16384, Width));
        public int SafeHeight => Math.Max(64, Math.Min(16384, Height));
        public int SafeDpi => Math.Max(1, Math.Min(1200, Dpi));
        public float SafePixelScale => (float)Math.Max(1.0, Math.Min(4.0, PixelScale));
        public int SafePixelWidth => Math.Max(
            64,
            Math.Min(16384, (int)Math.Round(SafeWidth * SafePixelScale)));
        public int SafePixelHeight => Math.Max(
            64,
            Math.Min(16384, (int)Math.Round(SafeHeight * SafePixelScale)));
        public float SafeTextScale => (float)Math.Max(0.5, Math.Min(3.0, TextScale));

        public Bitmap CreateBitmap()
        {
            var bitmap = new Bitmap(SafePixelWidth, SafePixelHeight);
            bitmap.SetResolution(SafeDpi, SafeDpi);
            return bitmap;
        }

        public void PrepareGraphics(Graphics graphics)
        {
            if (graphics == null)
                return;
            float scale = SafePixelScale;
            if (Math.Abs(scale - 1f) > 0.001f)
                graphics.ScaleTransform(scale, scale);
        }

        /// <summary>Scales a designed font size and keeps it above the legible minimum.</summary>
        public float ScaledFont(double baseSize) =>
            (float)Math.Max(5.5, baseSize * SafeTextScale);
    }

    /// <summary>One legend row: a colour swatch and the category it stands for.</summary>
    public sealed class WasperChartLegendEntry
    {
        public string Label { get; set; } = string.Empty;
        public Color Color { get; set; } = Color.SteelBlue;
    }

    /// <summary>
    /// Bitmap plus plot-space metadata used for hover, click selection, tooltips, and linked views.
    /// The result owns its Bitmap and must be disposed by the host.
    /// </summary>
    public sealed class WasperChartRenderResult : IDisposable
    {
        public Bitmap Bitmap { get; set; }
        public float PixelScale { get; set; } = 1f;
        public RectangleF PlotBounds { get; set; } = RectangleF.Empty;
        public List<WasperChartHitTarget> HitTargets { get; set; } =
            new List<WasperChartHitTarget>();
        public List<string> Warnings { get; set; } = new List<string>();

        public WasperChartHitTarget HitTest(
            PointF location,
            float tolerancePixels = 8f,
            Func<WasperChartHitTarget, bool> filter = null)
        {
            return WasperChartHitTester.FindNearest(
                HitTargets,
                location,
                tolerancePixels,
                filter);
        }

        public void Dispose()
        {
            Bitmap?.Dispose();
            Bitmap = null;
        }
    }

    /// <summary>
    /// Renderer contract implemented by shared chart engines. Renderers know nothing about
    /// Grasshopper, Rhino preview meshes, files, PictureBox controls, or WasperStudy persistence.
    /// </summary>
    public interface IWasperChartRenderer<in TData>
    {
        WasperChartRenderResult Render(TData data, WasperChartRenderOptions options);
    }
}
