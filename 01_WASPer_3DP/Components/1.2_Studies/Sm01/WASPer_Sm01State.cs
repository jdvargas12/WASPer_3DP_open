using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using ClosedXML.Excel;
using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json;
using Rhino.Geometry;

namespace WASPer_3DP.Components._1_2_Studies
{
    internal sealed class SampleNamePropertyOption
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;

        /// <summary>
        /// Category shown as a togglable group in the Sample Name tab's left-hand available-tokens
        /// panel (mirrors the Dashboard's KPI group filter). "General" for the single iteration
        /// token, "Infill" for the hardcoded X/Y/Z cell-count shortcuts, "Parameters" for linked
        /// sliders, and each numeric KPI's own DisplayGroup otherwise.
        /// </summary>
        public string Group { get; set; } = string.Empty;

        public override string ToString() => Label;
    }

    public sealed partial class wsp_Sm01_WASPer_Study_Manager
    {
        private const string StudyJsonKey = "wasper_study_state_json";

        /// <summary>
        /// Prefix marking a sample-name token as a user-typed literal segment rather than a
        /// reference into <see cref="SampleNameOptions"/> (iteration/param:/kpi:). The remainder of
        /// the token, verbatim, is the literal text - see the Sample Name tab's "Insert text"
        /// control (WASPer_Sm01SampleNameTab.cs) and <see cref="ResolveSampleNameToken"/> below.
        /// </summary>
        internal const string SampleNameTextPrefix = "text:";

        internal static bool IsSampleNameTextToken(string token) =>
            token != null && token.StartsWith(SampleNameTextPrefix, StringComparison.Ordinal);
        private bool _dashboardSettingsApplied;

        private WasperStudy _study = new WasperStudy();
        private readonly List<Guid> _linkedSliderIds = new List<Guid>();
        private readonly Dictionary<Guid, decimal> _originalSliderValues =
            new Dictionary<Guid, decimal>();
        private List<IReadOnlyList<double>> _studyCombinations =
            new List<IReadOnlyList<double>>();
        private List<WasperStudyParameter> _activeStudyParameters =
            new List<WasperStudyParameter>();
        private int _studyCombinationIndex = -1;
        private bool _studyRunning;
        private bool _capturePending;
        private string _studyStatus = "Idle. Link Number Sliders to begin.";
        private string _studyFolder = string.Empty;
        private string _lastReportPath = string.Empty;
        private string _reportStatus = "Ready to generate a PDF study report.";
        private List<List<string>> _currentGcodeBranches = new List<List<string>>();

        internal int CurrentStudyIteration => _study?.Iterations?.Count > 0
            ? _study.Iterations.Count - 1
            : -1;

        internal double StudyProgress
        {
            get
            {
                if (_studyCombinations.Count == 0)
                    return _study?.Iterations?.Count > 0 ? 1.0 : 0.0;
                return Math.Max(
                    0.0,
                    Math.Min(1.0, (_study?.Iterations?.Count ?? 0) / (double)_studyCombinations.Count));
            }
        }

        internal bool IsStudyRunning => _studyRunning;
        internal string StudyStatus => _studyStatus;

        internal void StoreSampleNameTemplate(IEnumerable<string> tokens)
        {
            List<string> selected = (tokens ?? Enumerable.Empty<string>())
                .Where(token => !string.IsNullOrEmpty(token))
                .Select(token => IsSampleNameTextToken(token) ? token : token.Trim())
                .Where(token => !string.IsNullOrWhiteSpace(token))
                .ToList();
            // Catalog tokens (iteration/param:/kpi:) are deduplicated - referencing the same
            // slider or KPI twice makes no sense. Free-text segments (Sample Name tab's "Insert
            // text") are user-authored literal separators and are legitimately allowed to repeat,
            // e.g. the same "_" or "-" appearing more than once in a template.
            var seenCatalogKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deduped = new List<string>();
            foreach (string token in selected)
            {
                if (IsSampleNameTextToken(token) || seenCatalogKeys.Add(token))
                    deduped.Add(token);
            }
            if (deduped.Count == 0)
                deduped.Add("iteration");
            _sampleNameTokens.Clear();
            _sampleNameTokens.AddRange(deduped);
            OnObjectChanged(GH_ObjectEventType.Options);
            UpdateStudyWindow();
        }

        internal void StoreSnapshotSettings(WasperSnapshotSettings settings)
        {
            settings ??= new WasperSnapshotSettings();
            _study ??= new WasperStudy();
            _study.SchemaVersion = Math.Max(3, _study.SchemaVersion);
            _study.Snapshot = new WasperSnapshotSettings
            {
                Enabled = settings.Enabled,
                ViewportName = settings.ViewportName?.Trim() ?? string.Empty,
                Width = Math.Max(64, Math.Min(16384, settings.Width)),
                Height = Math.Max(64, Math.Min(16384, settings.Height)),
                Dpi = Math.Max(1, Math.Min(1200, settings.Dpi)),
                WaitMilliseconds = Math.Max(0, Math.Min(10000, settings.WaitMilliseconds)),
                VisualizationComponentId = settings.VisualizationComponentId,
                VisualizationComponentName = settings.VisualizationComponentName?.Trim() ??
                    string.Empty
            };
            _study.UpdatedUtc = DateTime.UtcNow;
            OnObjectChanged(GH_ObjectEventType.Options);
        }

        internal IReadOnlyList<string> CurrentGcodeFiles()
        {
            return _study?.Iterations?.LastOrDefault()?.GcodeFiles ?? new List<string>();
        }

        internal string StudyInfo(int enabledKpiCount)
        {
            int totalKpis = _currentSet?.Items?.Count ?? 0;
            int gcodeBranches = _currentGcodeBranches?.Count ?? 0;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}/{1} KPIs enabled. {2} G-code branch(es). {3} {4} {5}",
                enabledKpiCount,
                totalKpis,
                gcodeBranches,
                _studyStatus,
                _lastWriteInfo,
                XrSceneInfo());
        }

        private string XrSceneInfo()
        {
            WasperXrScenePack scenePack = _currentXrScenePack;
            string sessionLabel = LiveViewerSessionLabel();
            if (scenePack == null)
                return "XR scene not connected. Session " + sessionLabel + ".";

            string playback = scenePack.SimulationParameterConnected
                ? string.Format(
                    CultureInfo.InvariantCulture,
                    "external sim_par {0:0.00}",
                    scenePack.SimulationParameter)
                : "local playback";

            return string.Format(
                CultureInfo.InvariantCulture,
                "XR: {0} object(s) -> {1} display mesh(es), {2} material(s), {3}. Session {4}.",
                scenePack.ContextGeometry?.Count ?? 0,
                scenePack.ContextMeshes?.Count ?? 0,
                scenePack.Materials?.Count ?? 0,
                playback,
                sessionLabel);
        }

        internal IReadOnlyList<WasperStudyParameter> StudyParameters()
        {
            SyncLinkedParameters();
            return _study.Parameters.ToList();
        }

        internal IReadOnlyList<GH_NumberSlider> LinkedSliders()
        {
            GH_Document document = OnPingDocument() ?? Instances.ActiveCanvas?.Document;
            if (document == null)
                return Array.Empty<GH_NumberSlider>();
            return _linkedSliderIds
                .Select(id => document.FindObject(id, true) as GH_NumberSlider)
                .Where(slider => slider != null)
                .ToList();
        }

        internal IReadOnlyList<RectangleF> LinkedSliderBounds()
        {
            return LinkedSliders()
                .Where(slider => slider.Attributes != null)
                .Select(slider => slider.Attributes.Bounds)
                .ToList();
        }

        internal RectangleF? LinkedVisualizationBounds()
        {
            GH_Document document = OnPingDocument() ?? Instances.ActiveCanvas?.Document;
            GH_Component component = document?.FindObject(
                _study?.Snapshot?.VisualizationComponentId ?? Guid.Empty,
                true) as GH_Component;
            return component?.Attributes == null
                ? (RectangleF?)null
                : component.Attributes.Bounds;
        }

        internal void LinkSelectedVisualizationComponent()
        {
            GH_Document document = OnPingDocument() ?? Instances.ActiveCanvas?.Document;
            if (document == null)
            {
                _studyStatus = "No active Grasshopper document was found.";
                UpdateStudyWindow();
                return;
            }

            List<GH_Component> selected = document.Objects
                .OfType<GH_Component>()
                .Where(component => component.InstanceGuid != InstanceGuid)
                .Where(component => component.Attributes?.Selected == true)
                .OrderBy(component => component.Attributes.Bounds.Y)
                .ThenBy(component => component.Attributes.Bounds.X)
                .ToList();
            if (selected.Count != 1)
            {
                _studyStatus = selected.Count == 0
                    ? "Select one visualization component on the canvas, then click Link vis component."
                    : "Select only one visualization component before linking it.";
                UpdateStudyWindow();
                return;
            }

            GH_Component source = selected[0];
            _study.Snapshot ??= new WasperSnapshotSettings();
            _study.Snapshot.VisualizationComponentId = source.InstanceGuid;
            _study.Snapshot.VisualizationComponentName = string.IsNullOrWhiteSpace(source.NickName)
                ? source.Name
                : source.NickName;
            _studyStatus = $"Linked visualization component: {_study.Snapshot.VisualizationComponentName}.";
            _study.UpdatedUtc = DateTime.UtcNow;
            OnObjectChanged(GH_ObjectEventType.Options);
            Instances.RedrawCanvas();
            UpdateStudyWindow();
        }

        internal void UnlinkVisualizationComponent()
        {
            _study.Snapshot ??= new WasperSnapshotSettings();
            _study.Snapshot.VisualizationComponentId = Guid.Empty;
            _study.Snapshot.VisualizationComponentName = string.Empty;
            _studyStatus = "Visualization component unlinked; snapshots use the selected viewport directly.";
            _study.UpdatedUtc = DateTime.UtcNow;
            OnObjectChanged(GH_ObjectEventType.Options);
            Instances.RedrawCanvas();
            UpdateStudyWindow();
        }

        internal void LinkSelectedSliders()
        {
            GH_Document document = OnPingDocument() ?? Instances.ActiveCanvas?.Document;
            if (document == null)
            {
                _studyStatus = "No active Grasshopper document was found.";
                return;
            }

            List<GH_NumberSlider> selected = document.Objects
                .OfType<GH_NumberSlider>()
                .Where(slider => slider.Attributes?.Selected == true)
                .OrderBy(slider => slider.Attributes.Bounds.Y)
                .ThenBy(slider => slider.Attributes.Bounds.X)
                .ToList();
            foreach (GH_NumberSlider slider in selected)
            {
                if (!_linkedSliderIds.Contains(slider.InstanceGuid))
                    _linkedSliderIds.Add(slider.InstanceGuid);
            }
            SyncLinkedParameters();
            _studyStatus = selected.Count == 0
                ? "Select one or more Number Sliders on the canvas, then click Link selected."
                : $"Linked {selected.Count} selected slider(s).";
            OnObjectChanged(GH_ObjectEventType.Options);
            Instances.RedrawCanvas();
            UpdateStudyWindow();
        }

        internal void UnlinkSliders(IEnumerable<Guid> sliderIds)
        {
            var remove = new HashSet<Guid>(sliderIds ?? Enumerable.Empty<Guid>());
            _linkedSliderIds.RemoveAll(remove.Contains);
            _study.Parameters.RemoveAll(parameter => remove.Contains(parameter.SliderId));
            _studyStatus = $"Unlinked {remove.Count} slider(s).";
            OnObjectChanged(GH_ObjectEventType.Options);
            Instances.RedrawCanvas();
            UpdateStudyWindow();
        }

        private void UpdateStudyWindow()
        {
            if (_form == null || _form.IsClosed)
                return;
            bool viewingSavedStudy = _viewedStudy != null;
            WasperStudy displayedStudy = _viewedStudy ?? _study;
            _form.UpdateStudy(
                displayedStudy?.Parameters ?? new List<WasperStudyParameter>(),
                displayedStudy?.Iterations ?? new List<WasperStudyIteration>(),
                viewingSavedStudy
                    ? $"Viewing saved study '{displayedStudy.RunName}' (read-only)."
                    : _studyStatus,
                viewingSavedStudy ? 1.0 : StudyProgress,
                viewingSavedStudy ? false : _studyRunning,
                viewingSavedStudy);
            _form.UpdateSampleNameComposer(
                SampleNameOptions(),
                _sampleNameTokens,
                _sampleNameInputConnected,
                _currentSampleNameOverride,
                PreviewSampleName());
            _form.UpdateGcode(_currentGcodeBranches, CurrentGcodeFiles());
            _form.UpdateSnapshotSettings(_study.Snapshot);
            // The Dashboard needs the folder to find snapshots for studies captured before their
            // paths were recorded on the iteration.
            _form.UpdateDashboardSnapshotFolder(Path.Combine(
                ResolveStudyFolder(displayedStudy?.RunName ?? _study.RunName, _currentFilePath),
                "Snapshots"));
            _form.UpdateReport(_study.Report, _reportStatus);
            if (!_dashboardSettingsApplied)
            {
                // Applied once per window so the user's live edits are not overwritten by every
                // subsequent solve; the settings object itself is shared from here on.
                _dashboardSettingsApplied = true;
                _form.ApplyDashboardSettings(_study.Dashboard ??= new WasperDashboardSettings());
            }
        }

        private List<SampleNamePropertyOption> SampleNameOptions()
        {
            var options = new List<SampleNamePropertyOption>
            {
                new SampleNamePropertyOption { Key = "iteration", Label = "Iteration number", Group = "General" },
                new SampleNamePropertyOption { Key = "kpi:infill.cell_name_short", Label = "Infill: Cell name (short)", Group = "Infill" },
                new SampleNamePropertyOption { Key = "kpi:infill.cell_count_x", Label = "Infill: Cell count X", Group = "Infill" },
                new SampleNamePropertyOption { Key = "kpi:infill.cell_count_y", Label = "Infill: Cell count Y", Group = "Infill" },
                new SampleNamePropertyOption { Key = "kpi:infill.cell_count_z", Label = "Infill: Cell count Z", Group = "Infill" }
            };
            foreach (WasperStudyParameter parameter in _study?.Parameters ?? new List<WasperStudyParameter>())
            {
                options.Add(new SampleNamePropertyOption
                {
                    Key = "param:" + parameter.Name,
                    Label = "Parameter: " + parameter.Name,
                    Group = "Parameters"
                });
            }
            foreach (WasperKpi kpi in _currentSet?.Items ?? new List<WasperKpi>())
            {
                options.Add(new SampleNamePropertyOption
                {
                    Key = "kpi:" + kpi.Key,
                    Label = kpi.DisplayGroup + ": " + kpi.Label,
                    Group = string.IsNullOrWhiteSpace(kpi.DisplayGroup) ? "KPIs" : kpi.DisplayGroup
                });
            }
            return options
                .GroupBy(option => option.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(option => SampleNameOptionOrder(option.Key))
                .ThenBy(option => option.Label)
                .ToList();
        }

        private static int SampleNameOptionOrder(string key)
        {
            if (string.Equals(key, "iteration", StringComparison.OrdinalIgnoreCase)) return 0;
            if (string.Equals(key, "kpi:infill.cell_name_short", StringComparison.OrdinalIgnoreCase)) return 1;
            if (string.Equals(key, "kpi:infill.cell_count_x", StringComparison.OrdinalIgnoreCase)) return 2;
            if (string.Equals(key, "kpi:infill.cell_count_y", StringComparison.OrdinalIgnoreCase)) return 3;
            if (string.Equals(key, "kpi:infill.cell_count_z", StringComparison.OrdinalIgnoreCase)) return 4;
            if (key?.StartsWith("param:", StringComparison.OrdinalIgnoreCase) == true) return 10;
            return 20;
        }

        private string PreviewSampleName()
        {
            if (!string.IsNullOrWhiteSpace(_currentSampleNameOverride))
                return CleanSampleName(_currentSampleNameOverride);
            var parameters = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (WasperStudyParameter parameter in _study?.Parameters ?? new List<WasperStudyParameter>())
            {
                GH_NumberSlider slider = ResolveSlider(parameter.SliderId);
                if (slider?.Slider != null)
                    parameters[parameter.Name] = (double)slider.Slider.Value;
            }
            return ComposeSampleName(
                _study?.Iterations?.Count ?? 0,
                parameters,
                _currentSet?.Items);
        }

        private string ComposeSampleName(
            int iterationIndex,
            IDictionary<string, double> parameters,
            IEnumerable<WasperKpi> kpis)
        {
            Dictionary<string, WasperKpi> kpiLookup = (kpis ?? Enumerable.Empty<WasperKpi>())
                .Where(kpi => kpi != null && !string.IsNullOrWhiteSpace(kpi.Key))
                .GroupBy(kpi => kpi.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            var segments = new List<string>();
            var countValues = new List<string>();
            int countInsertIndex = -1;

            foreach (string token in _sampleNameTokens)
            {
                if (IsCellCountToken(token))
                {
                    string countValue = ResolveSampleNameToken(
                        token,
                        iterationIndex,
                        parameters,
                        kpiLookup);
                    if (!string.IsNullOrWhiteSpace(countValue))
                    {
                        if (countInsertIndex < 0)
                            countInsertIndex = segments.Count;
                        countValues.Add(countValue);
                    }
                    continue;
                }

                string value = ResolveSampleNameToken(
                    token,
                    iterationIndex,
                    parameters,
                    kpiLookup);
                if (!string.IsNullOrWhiteSpace(value))
                    segments.Add(value);
            }

            if (countValues.Count > 0)
                segments.Insert(Math.Max(0, countInsertIndex), string.Join(".", countValues));
            string composed = string.Join("_", segments);
            return CleanSampleName(string.IsNullOrWhiteSpace(composed)
                ? (iterationIndex + 1).ToString(CultureInfo.InvariantCulture)
                : composed);
        }

        private static bool IsCellCountToken(string token)
        {
            return string.Equals(token, "kpi:infill.cell_count_x", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "kpi:infill.cell_count_y", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "kpi:infill.cell_count_z", StringComparison.OrdinalIgnoreCase);
        }

        private static string ResolveSampleNameToken(
            string token,
            int iterationIndex,
            IDictionary<string, double> parameters,
            IDictionary<string, WasperKpi> kpis)
        {
            if (string.Equals(token, "iteration", StringComparison.OrdinalIgnoreCase))
                return (iterationIndex + 1).ToString(CultureInfo.InvariantCulture);
            if (IsSampleNameTextToken(token))
                return token.Substring(SampleNameTextPrefix.Length);
            if (token?.StartsWith("param:", StringComparison.OrdinalIgnoreCase) == true)
            {
                string key = token.Substring("param:".Length);
                return parameters != null && parameters.TryGetValue(key, out double value)
                    ? value.ToString("0.###", CultureInfo.InvariantCulture)
                    : string.Empty;
            }
            if (token?.StartsWith("kpi:", StringComparison.OrdinalIgnoreCase) == true)
            {
                string key = token.Substring("kpi:".Length);
                if (kpis == null || !kpis.TryGetValue(key, out WasperKpi kpi))
                    return string.Empty;
                return kpi.Value.HasValue
                    ? kpi.Value.Value.ToString("0.###", CultureInfo.InvariantCulture)
                    : kpi.TextValue ?? string.Empty;
            }
            return string.Empty;
        }

        private static string CleanSampleName(string value)
        {
            string clean = (value ?? string.Empty).Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                clean = clean.Replace(invalid, '-');
            clean = string.Join("-", clean
                .Split((char[])null, StringSplitOptions.RemoveEmptyEntries));
            while (clean.Contains("__"))
                clean = clean.Replace("__", "_");
            return clean.Trim('_', '.', '-', ' ');
        }
    }
}
