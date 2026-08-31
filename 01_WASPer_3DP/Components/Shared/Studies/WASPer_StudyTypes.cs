using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Newtonsoft.Json;

namespace WASPer_3DP
{
    public sealed class WasperStudyParameter
    {
        public Guid SliderId { get; set; }
        public string Name { get; set; } = string.Empty;
        public double Minimum { get; set; }
        public double Maximum { get; set; }
        public int Samples { get; set; } = 3;
        public bool SamplesAreManual { get; set; }
        public string SliderAccuracy { get; set; } = string.Empty;
        public int SliderDecimalPlaces { get; set; }
        public bool Enabled { get; set; } = true;
        public double OriginalValue { get; set; }

        /// <summary>
        /// Spacing between the values a slider of this accuracy can actually take: 1 for Integer,
        /// 2 for Even and Odd, and 0 for Float, which is continuous and has no grid.
        /// </summary>
        [JsonIgnore]
        public double AccuracyStep => StepFor(SliderAccuracy);

        /// <summary>Grid origin for the accuracy: Odd values sit on 1, 3, 5..., everything else on 0.</summary>
        [JsonIgnore]
        public double AccuracyOffset => OffsetFor(SliderAccuracy);

        public static double StepFor(string sliderAccuracy)
        {
            switch ((sliderAccuracy ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "integer": return 1.0;
                case "even":
                case "odd": return 2.0;
                default: return 0.0;
            }
        }

        public static double OffsetFor(string sliderAccuracy) =>
            string.Equals((sliderAccuracy ?? string.Empty).Trim(), "Odd", StringComparison.OrdinalIgnoreCase)
                ? 1.0
                : 0.0;

        /// <summary>
        /// Number of distinct values the accuracy grid allows inside [minimum, maximum], or 0 when
        /// the parameter is continuous. An Integer slider spanning 0..7 therefore reports 8 - the
        /// count of usable values - rather than the slider's drag-tick resolution.
        /// </summary>
        public static int DiscreteValueCount(
            double minimum,
            double maximum,
            double step,
            double offset)
        {
            if (step <= 0.0 || double.IsNaN(minimum) || double.IsNaN(maximum))
                return 0;
            if (maximum < minimum)
                (minimum, maximum) = (maximum, minimum);
            const double tolerance = 1e-9;
            double first = Math.Ceiling(((minimum - offset) / step) - tolerance);
            double last = Math.Floor(((maximum - offset) / step) + tolerance);
            double count = (last - first) + 1.0;
            return count < 1.0 ? 0 : (int)Math.Min(int.MaxValue, count);
        }

        /// <summary>Distinct values available across this parameter's own study range.</summary>
        public int DiscreteValueCount() =>
            DiscreteValueCount(Minimum, Maximum, AccuracyStep, AccuracyOffset);

        /// <summary>
        /// The values the study will sweep. Samples are spaced evenly across the study range and
        /// then snapped onto the slider's accuracy grid, because an Integer, Even, or Odd slider
        /// silently rounds anything else - which would otherwise run the same geometry twice under
        /// two different recorded values. Snapping can collapse neighbours, so duplicates are
        /// dropped and the sweep returns the distinct values only.
        /// </summary>
        public IEnumerable<double> Values()
        {
            int count = Math.Max(1, Samples);
            double step = AccuracyStep;
            double offset = AccuracyOffset;
            if (count == 1 || Math.Abs(Maximum - Minimum) <= 1e-12)
            {
                yield return Snap(Minimum, step, offset);
                yield break;
            }
            var seen = new HashSet<double>();
            for (int index = 0; index < count; index++)
            {
                double raw = Minimum + ((Maximum - Minimum) * index / (count - 1));
                double value = Snap(raw, step, offset);
                if (seen.Add(value))
                    yield return value;
            }
        }

        private double Snap(double value, double step, double offset)
        {
            double low = Math.Min(Minimum, Maximum);
            double high = Math.Max(Minimum, Maximum);
            if (step <= 0.0)
                return Math.Max(low, Math.Min(high, value));
            const double tolerance = 1e-9;
            double firstIndex = Math.Ceiling(((low - offset) / step) - tolerance);
            double lastIndex = Math.Floor(((high - offset) / step) + tolerance);
            if (lastIndex < firstIndex)
                return Math.Max(low, Math.Min(high, value));
            double index = Math.Round((value - offset) / step, MidpointRounding.AwayFromZero);
            index = Math.Max(firstIndex, Math.Min(lastIndex, index));
            return offset + (index * step);
        }
    }

    /// <summary>
    /// Points at one plottable Dashboard variable. Key alone is ambiguous because a study input and
    /// a KPI may share a key, so the kind travels with it.
    /// </summary>
    public sealed class WasperDashboardVariableRef
    {
        public string Key { get; set; } = string.Empty;
        public bool IsInput { get; set; }

        [JsonIgnore]
        public bool IsEmpty => string.IsNullOrWhiteSpace(Key);

        public static WasperDashboardVariableRef Create(string key, bool isInput) =>
            string.IsNullOrWhiteSpace(key)
                ? null
                : new WasperDashboardVariableRef { Key = key, IsInput = isInput };
    }

    /// <summary>
    /// User-typed overrides for one chart's title and axis names. A blank entry means the chart
    /// keeps its automatic label, so clearing a box restores the default rather than blanking it.
    /// </summary>
    public sealed class WasperChartLabels
    {
        public string Title { get; set; } = string.Empty;
        public string XTitle { get; set; } = string.Empty;
        public string YTitle { get; set; } = string.Empty;

        /// <summary>
        /// Explicit axis limits. Null leaves that end of the axis automatic, so one bound can be
        /// pinned without fixing the other.
        /// </summary>
        public double? XMinimum { get; set; }
        public double? XMaximum { get; set; }
        public double? YMinimum { get; set; }
        public double? YMaximum { get; set; }

        [JsonIgnore]
        public bool HasRange =>
            XMinimum.HasValue || XMaximum.HasValue || YMinimum.HasValue || YMaximum.HasValue;

        [JsonIgnore]
        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(Title) &&
            string.IsNullOrWhiteSpace(XTitle) &&
            string.IsNullOrWhiteSpace(YTitle) &&
            !HasRange;

        public WasperChartLabels Clone() => new WasperChartLabels
        {
            Title = Title ?? string.Empty,
            XTitle = XTitle ?? string.Empty,
            YTitle = YTitle ?? string.Empty,
            XMinimum = XMinimum,
            XMaximum = XMaximum,
            YMinimum = YMinimum,
            YMaximum = YMaximum
        };

        /// <summary>Returns the override when supplied, otherwise the chart's automatic label.</summary>
        public static string Resolve(string custom, string automatic) =>
            string.IsNullOrWhiteSpace(custom) ? automatic ?? string.Empty : custom.Trim();
    }

    /// <summary>
    /// Everything the Dashboard tab lets the user change. Stored on the study so it survives the
    /// window closing, the Grasshopper file reopening, and Rhino restarting.
    /// </summary>
    public sealed class WasperDashboardSettings
    {
        public const string HistoryChart = "history";
        public const string ScatterChart = "scatter";
        public const string HeatmapChart = "heatmap";
        public const string ParallelChart = "parallel";
        public const string HistogramChart = "histogram";

        public WasperDashboardVariableRef HistoryKpi { get; set; }
        public WasperDashboardVariableRef ScatterX { get; set; }
        public WasperDashboardVariableRef ScatterY { get; set; }
        public WasperDashboardVariableRef ScatterColor { get; set; }
        public WasperDashboardVariableRef HistogramVariable { get; set; }
        public List<string> HiddenGroups { get; set; } = new List<string>();
        public int TextSizePercent { get; set; } = 100;
        public int HistogramBins { get; set; } = 12;
        public string HistogramMode { get; set; } = "Bars";

        /// <summary>Kernel bandwidth as a percentage of the automatic rule-of-thumb value.</summary>
        public int HistogramBandwidthPercent { get; set; } = 100;
        public string ScatterStyle { get; set; } = "Markers";

        /// <summary>Draws each sample's name beside its marker on the scatter chart.</summary>
        public bool ScatterShowNames { get; set; }

        /// <summary>Draws each sample's X and Y values beside its marker on the scatter chart.</summary>
        public bool ScatterShowValues { get; set; }
        public Dictionary<string, WasperChartLabels> Labels { get; set; } =
            new Dictionary<string, WasperChartLabels>(StringComparer.OrdinalIgnoreCase);

        /// <summary>User-dragged scatter label positions, as pixel offsets from their marker.</summary>
        public List<WasperLabelOffset> ScatterLabelOffsets { get; set; } =
            new List<WasperLabelOffset>();

        /// <summary>
        /// Chart row heights in pixels and the left-column share of the width. Empty and 0 mean the
        /// Dashboard sizes itself; any user drag pins the layout from then on.
        /// </summary>
        public List<float> RowHeights { get; set; } = new List<float>();
        public float ColumnRatio { get; set; }

        /// <summary>
        /// Width of the selected-sample snapshot panel beside the parallel chart. 0 means the
        /// Dashboard sizes it at a quarter of the row; any drag pins a pixel width.
        /// </summary>
        public int SnapshotPanelWidth { get; set; }

        public WasperChartLabels LabelsFor(string chartKey)
        {
            if (string.IsNullOrWhiteSpace(chartKey))
                return new WasperChartLabels();
            Labels ??= new Dictionary<string, WasperChartLabels>(StringComparer.OrdinalIgnoreCase);
            if (!Labels.TryGetValue(chartKey, out WasperChartLabels labels) || labels == null)
            {
                labels = new WasperChartLabels();
                Labels[chartKey] = labels;
            }
            return labels;
        }

        public void SetLabels(string chartKey, WasperChartLabels labels)
        {
            if (string.IsNullOrWhiteSpace(chartKey))
                return;
            Labels ??= new Dictionary<string, WasperChartLabels>(StringComparer.OrdinalIgnoreCase);
            if (labels == null || labels.IsEmpty)
                Labels.Remove(chartKey);
            else
                Labels[chartKey] = labels.Clone();
        }
    }

    /// <summary>One dragged label: the individual it belongs to and its pixel offset.</summary>
    public sealed class WasperLabelOffset
    {
        public int IndividualId { get; set; }
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
    }

    public sealed class WasperStudyIteration
    {
        public int Index { get; set; }
        public string SampleName { get; set; } = string.Empty;
        public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
        public string Status { get; set; } = "Complete";
        public Dictionary<string, double> Parameters { get; set; } =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public List<WasperKpi> Kpis { get; set; } = new List<WasperKpi>();
        public List<string> GcodeFiles { get; set; } = new List<string>();
        public List<string> SnapshotFiles { get; set; } = new List<string>();
        // .wasperxr package(s) captured for this iteration -- only populated
        // when the Run Study dialog's "wsp_paths" option was checked (see
        // RunStudyOptionsDialog/WasperStudy.XrPathsEnabled). Empty for every
        // iteration captured before this feature existed or with the option
        // left off, same as GcodeFiles/SnapshotFiles being empty when their
        // own capture is skipped or fails.
        public List<string> XrFiles { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class WasperSnapshotSettings
    {
        public bool Enabled { get; set; } = true;
        public string ViewportName { get; set; } = string.Empty;
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public int Dpi { get; set; } = 72;
        public int WaitMilliseconds { get; set; } = 500;
        public Guid VisualizationComponentId { get; set; } = Guid.Empty;
        public string VisualizationComponentName { get; set; } = string.Empty;
    }

    public sealed class WasperReportSettings
    {
        public string PageSize { get; set; } = "A4";
        public bool Landscape { get; set; }
        public string Title { get; set; } = "WASPer Study Report";
        public string Subtitle { get; set; } = string.Empty;
        public bool IncludeSnapshot { get; set; } = true;
        public bool IncludeIterationTable { get; set; } = true;
        public string OutputPath { get; set; } = string.Empty;
    }

    public sealed class WasperStudy
    {
        public int SchemaVersion { get; set; } = 4;
        public Guid StudyId { get; set; } = Guid.NewGuid();
        public string RunName { get; set; } = "WASPer_Study";
        public string DefinitionPath { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        public List<WasperStudyParameter> Parameters { get; set; } =
            new List<WasperStudyParameter>();
        public List<WasperStudyIteration> Iterations { get; set; } =
            new List<WasperStudyIteration>();
        public WasperReportSettings Report { get; set; } = new WasperReportSettings();
        public WasperSnapshotSettings Snapshot { get; set; } = new WasperSnapshotSettings();
        public WasperDashboardSettings Dashboard { get; set; } = new WasperDashboardSettings();

        // Run Study dialog selections (RunStudyOptionsDialog), remembered across
        // runs so the dialog reopens with whatever was last chosen rather than
        // always resetting to the hardcoded defaults. G-code has no prior
        // per-run toggle at all (SaveIterationGcode ran unconditionally before
        // this), so it defaults on to preserve existing behavior for anyone not
        // using the new dialog's option to turn it off. wsp_paths defaults off:
        // it is the heaviest of the three (a full print-path package per
        // iteration) and new, so it should be an explicit opt-in.
        public bool GcodeEnabled { get; set; } = true;
        public bool XrPathsEnabled { get; set; }

        [JsonIgnore]
        public int CompletedCount => Iterations?.Count ?? 0;

        public override string ToString()
        {
            return $"{RunName}: {CompletedCount} iterations, {Parameters?.Count ?? 0} parameters";
        }
    }

    public sealed class WasperStudyGoo : WasperJsonGoo<WasperStudy>
    {
        public WasperStudyGoo() { }
        public WasperStudyGoo(WasperStudy value) : base(value) { }

        protected override string StorageKey => "wasper_study_json";
        protected override WasperJsonGoo<WasperStudy> Create(WasperStudy value) =>
            new WasperStudyGoo(value);
        public override string TypeName => "WASPer Study";
        public override string TypeDescription =>
            "Versioned WASPer design-study dataset containing linked parameters and global KPI iterations.";
    }

    internal static class WasperStudyStorage
    {
        public static string Save(WasperStudy study, string folder)
        {
            if (study == null)
                throw new ArgumentNullException(nameof(study));
            if (string.IsNullOrWhiteSpace(folder))
                throw new ArgumentException("A study folder is required.", nameof(folder));

            Directory.CreateDirectory(folder);
            study.UpdatedUtc = DateTime.UtcNow;
            string finalPath = Path.Combine(folder, "study.json");
            string temporaryPath = finalPath + ".tmp";
            string json = JsonConvert.SerializeObject(study, Formatting.Indented);
            File.WriteAllText(temporaryPath, json, new UTF8Encoding(true));
            if (File.Exists(finalPath))
                File.Replace(temporaryPath, finalPath, finalPath + ".bak", true);
            else
                File.Move(temporaryPath, finalPath);
            return finalPath;
        }

        public static WasperStudy Load(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;
            return JsonConvert.DeserializeObject<WasperStudy>(File.ReadAllText(filePath));
        }

        public static List<IReadOnlyList<double>> CartesianValues(
            IEnumerable<WasperStudyParameter> parameters)
        {
            List<List<double>> domains = (parameters ?? Enumerable.Empty<WasperStudyParameter>())
                .Where(parameter => parameter.Enabled)
                .Select(parameter => parameter.Values().ToList())
                .ToList();
            if (domains.Count == 0)
                return new List<IReadOnlyList<double>> { Array.Empty<double>() };

            var combinations = new List<IReadOnlyList<double>> { Array.Empty<double>() };
            foreach (List<double> domain in domains)
            {
                var next = new List<IReadOnlyList<double>>();
                foreach (IReadOnlyList<double> prefix in combinations)
                {
                    foreach (double value in domain)
                    {
                        var combination = prefix.Concat(new[] { value }).ToArray();
                        next.Add(combination);
                    }
                }
                combinations = next;
            }
            return combinations;
        }
    }
}
