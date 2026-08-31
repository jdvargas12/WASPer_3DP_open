using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using GH_IO.Serialization;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json;

namespace WASPer_3DP
{
    public sealed class WasperChartAxisSettings
    {
        public string XTitle { get; set; } = string.Empty;
        public string YTitle { get; set; } = string.Empty;
        public string ZTitle { get; set; } = string.Empty;
        public string XRange { get; set; } = string.Empty;
        public string YRange { get; set; } = string.Empty;
        public string ZRange { get; set; } = string.Empty;
        public double XTickInterval { get; set; }
        public double YTickInterval { get; set; }
        public double ZTickInterval { get; set; }
        public bool XTicksInteger { get; set; } = true;
        public bool YTicksInteger { get; set; } = true;
        public bool ZTicksInteger { get; set; } = true;
        public double XTitleSize { get; set; } = 12.0;
        public double YTitleSize { get; set; } = 12.0;
        public double XTextSize { get; set; } = 10.0;
        public double YTextSize { get; set; } = 10.0;

        // Extra gap (typographic points) between tick labels and axis titles, on top of the
        // renderer's built-in default spacing. Shared across X, Y, and Y2. 0 = default spacing.
        public double TitleOffset { get; set; }
        public int LineType { get; set; }

        // ── secondary (right-hand) Y axis; only drawn when a chart supplies axis-2 series ──
        public string Y2Title { get; set; } = string.Empty;
        public string Y2Range { get; set; } = string.Empty;
        public double Y2TickInterval { get; set; }
        public bool Y2TicksInteger { get; set; } = true;

        public override string ToString() => "WASPer Chart Axis Params";
    }

    public sealed class WasperChartLegendSettings
    {
        public List<string> Labels { get; set; } = new List<string>();
        public int Location { get; set; } = 5;
        public double Distance { get; set; } = 0.2;
        public bool WrapRows { get; set; }
        public int Columns { get; set; } = 3;
        public double TextSize { get; set; } = 10.0;
        public override string ToString() => $"WASPer Chart Legend Params ({Labels?.Count ?? 0} labels)";
    }

    public sealed class WasperChartMarkerLineSettings
    {
        public List<int> MarkerColorsArgb { get; set; } = new List<int>();
        public List<double> MarkerSizes { get; set; } = new List<double>();
        public List<int> MarkerTypes { get; set; } = new List<int>();
        public List<int> LineColorsArgb { get; set; } = new List<int>();
        public List<double> LineWidths { get; set; } = new List<double>();
        public List<int> LineTypes { get; set; } = new List<int>();

        public override string ToString() => "WASPer Marker + Line Params (colour anchors interpolate to series count)";

        public static int ToArgb(Color color) => color.ToArgb();
        public static Color FromArgb(int argb) => Color.FromArgb(argb);
    }

    public sealed class WasperChartLayoutSettings
    {
        public string Title { get; set; } = string.Empty;
        public double TitleSize { get; set; } = 14.0;
        public string Dimensions { get; set; } = "160;100";
        public int Dpi { get; set; } = 150;
        public bool TransparentBackground { get; set; }
        public bool ShowReferences { get; set; } = true;
        public string FileName { get; set; } = string.Empty;
        public string FilePath { get; set; } = string.Empty;
        public override string ToString() => $"WASPer Chart Layout Params ({Dimensions} mm, {Dpi} DPI)";
    }

    public abstract class WasperJsonGoo<T> : GH_Goo<T> where T : class
    {
        protected WasperJsonGoo() : base((T)null) { }
        protected WasperJsonGoo(T value) : base(value) { }
        protected abstract string StorageKey { get; }
        protected abstract WasperJsonGoo<T> Create(T value);
        public override bool IsValid => Value != null;
        public override IGH_Goo Duplicate() => Create(Value == null ? null : JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(Value)));
        public override string ToString() => Value?.ToString() ?? $"Null {TypeName}";
        public override bool Write(GH_IWriter writer) { if (Value != null) writer.SetString(StorageKey, JsonConvert.SerializeObject(Value)); return true; }
        public override bool Read(GH_IReader reader) { if (reader.ItemExists(StorageKey)) Value = JsonConvert.DeserializeObject<T>(reader.GetString(StorageKey)); return true; }
        public override bool CastTo<Q>(ref Q target) { if (typeof(Q) == typeof(T) && Value != null) { target = (Q)(object)Value; return true; } return base.CastTo(ref target); }
        public override bool CastFrom(object source) { if (source is T value) { Value = value; return true; } if (source is WasperJsonGoo<T> goo) { Value = goo.Value; return true; } return false; }
    }

    public sealed class WasperChartAxisSettingsGoo : WasperJsonGoo<WasperChartAxisSettings>
    {
        public WasperChartAxisSettingsGoo() { }
        public WasperChartAxisSettingsGoo(WasperChartAxisSettings value) : base(value) { }
        protected override string StorageKey => "chart_axis_json";
        protected override WasperJsonGoo<WasperChartAxisSettings> Create(WasperChartAxisSettings value) => new WasperChartAxisSettingsGoo(value);
        public override string TypeName => "WASPer Chart Axis Params";
        public override string TypeDescription => "Reusable axis labels, ranges, ticks, and typography for WASPer charts.";
    }

    public sealed class WasperChartLegendSettingsGoo : WasperJsonGoo<WasperChartLegendSettings>
    {
        public WasperChartLegendSettingsGoo() { }
        public WasperChartLegendSettingsGoo(WasperChartLegendSettings value) : base(value) { }
        protected override string StorageKey => "chart_legend_json";
        protected override WasperJsonGoo<WasperChartLegendSettings> Create(WasperChartLegendSettings value) => new WasperChartLegendSettingsGoo(value);
        public override string TypeName => "WASPer Chart Legend Params";
        public override string TypeDescription => "Reusable legend labels, position, wrapping, and typography for WASPer charts.";
    }

    public sealed class WasperChartLayoutSettingsGoo : WasperJsonGoo<WasperChartLayoutSettings>
    {
        public WasperChartLayoutSettingsGoo() { }
        public WasperChartLayoutSettingsGoo(WasperChartLayoutSettings value) : base(value) { }
        protected override string StorageKey => "chart_layout_json";
        protected override WasperJsonGoo<WasperChartLayoutSettings> Create(WasperChartLayoutSettings value) => new WasperChartLayoutSettingsGoo(value);
        public override string TypeName => "WASPer Chart Layout Params";
        public override string TypeDescription => "Reusable title, dimensions, DPI, background, and file settings for WASPer charts.";
    }

    public sealed class WasperChartMarkerLineSettingsGoo : WasperJsonGoo<WasperChartMarkerLineSettings>
    {
        public WasperChartMarkerLineSettingsGoo() { }
        public WasperChartMarkerLineSettingsGoo(WasperChartMarkerLineSettings value) : base(value) { }
        protected override string StorageKey => "chart_marker_line_json";
        protected override WasperJsonGoo<WasperChartMarkerLineSettings> Create(WasperChartMarkerLineSettings value) => new WasperChartMarkerLineSettingsGoo(value);
        public override string TypeName => "WASPer Marker + Line Params";
        public override string TypeDescription => "Reusable marker and line colours, sizes, symbols, widths, and dash patterns for WASPer charts.";
    }

    public static class WasperChartSettingsTools
    {
        public static string DefaultOutputDirectory(string ghPath)
        {
            if (!string.IsNullOrWhiteSpace(ghPath))
            {
                string parent = Path.GetDirectoryName(ghPath);
                string fileName = Path.GetFileNameWithoutExtension(ghPath);
                if (!string.IsNullOrWhiteSpace(parent) && !string.IsNullOrWhiteSpace(fileName))
                    return Path.Combine(parent, "WASPer_" + fileName, "Plots");
            }

            return Path.Combine(Path.GetTempPath(), "WASPer_Charts", "Plots");
        }

        public static List<Color> ResolveSeriesColors(IList<int> argbValues, int seriesCount, IList<Color> fallback)
        {
            var result = new List<Color>();
            if (seriesCount <= 0)
                return result;
            if (argbValues == null || argbValues.Count == 0)
            {
                for (int i = 0; i < seriesCount; i++)
                    result.Add(fallback != null && fallback.Count > 0 ? fallback[i % fallback.Count] : Color.Gray);
                return result;
            }
            var anchors = argbValues.Select(Color.FromArgb).ToList();
            if (anchors.Count == 1)
            {
                for (int i = 0; i < seriesCount; i++)
                    result.Add(anchors[0]);
                return result;
            }
            if (anchors.Count >= seriesCount)
            {
                for (int i = 0; i < seriesCount; i++)
                    result.Add(anchors[i]);
                return result;
            }
            for (int i = 0; i < seriesCount; i++)
            {
                double t = seriesCount == 1 ? 0 : i / (double)(seriesCount - 1);
                double position = t * (anchors.Count - 1);
                int a = Math.Min(anchors.Count - 1, (int)Math.Floor(position));
                int b = Math.Min(anchors.Count - 1, a + 1);
                double local = position - a;
                result.Add(Color.FromArgb(
                    (int)Math.Round(anchors[a].A + (anchors[b].A - anchors[a].A) * local),
                    (int)Math.Round(anchors[a].R + (anchors[b].R - anchors[a].R) * local),
                    (int)Math.Round(anchors[a].G + (anchors[b].G - anchors[a].G) * local),
                    (int)Math.Round(anchors[a].B + (anchors[b].B - anchors[a].B) * local)));
            }
            return result;
        }

        public static WasperChartAxisSettings Axis(object input) => Unwrap<WasperChartAxisSettings, WasperChartAxisSettingsGoo>(input, g => g.Value);
        public static WasperChartLegendSettings Legend(object input) => Unwrap<WasperChartLegendSettings, WasperChartLegendSettingsGoo>(input, g => g.Value);
        public static WasperChartLayoutSettings Layout(object input) => Unwrap<WasperChartLayoutSettings, WasperChartLayoutSettingsGoo>(input, g => g.Value);
        public static WasperChartMarkerLineSettings MarkerLine(object input) => Unwrap<WasperChartMarkerLineSettings, WasperChartMarkerLineSettingsGoo>(input, g => g.Value);
        private static T Unwrap<T, TGoo>(object input, Func<TGoo, T> getter) where T : class where TGoo : class
        {
            if (input is T direct) return direct;
            if (input is TGoo goo) return getter(goo);
            if (input is GH_ObjectWrapper wrapper) return Unwrap<T, TGoo>(wrapper.Value, getter);
            return null;
        }
    }
}
