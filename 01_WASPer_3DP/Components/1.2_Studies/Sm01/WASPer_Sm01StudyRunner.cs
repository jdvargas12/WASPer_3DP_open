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
    public sealed partial class wsp_Sm01_WASPer_Study_Manager
    {
        internal void StartStudy(IEnumerable<WasperStudyParameter> configuredParameters)
        {
            StartStudy(configuredParameters, false);
        }

        internal void ResumeStudy(IEnumerable<WasperStudyParameter> configuredParameters)
        {
            StartStudy(configuredParameters, true);
        }

        private void StartStudy(
            IEnumerable<WasperStudyParameter> configuredParameters,
            bool resume)
        {
            if (_studyRunning)
                return;
            ApplyParameterConfiguration(configuredParameters);
            List<GH_NumberSlider> sliders = ResolveActiveSliders();
            if (sliders.Count == 0)
            {
                _studyStatus = "No enabled, resolved sliders are available.";
                UpdateStudyWindow();
                return;
            }

            try
            {
                _activeStudyParameters = _study.Parameters
                    .Where(parameter => parameter.Enabled && sliders.Any(
                        slider => slider.InstanceGuid == parameter.SliderId))
                    .ToList();
                _studyCombinations = WasperStudyStorage.CartesianValues(_activeStudyParameters);
            }
            catch (Exception exception)
            {
                _studyStatus = exception.Message;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, exception.Message);
                UpdateStudyWindow();
                return;
            }

            _originalSliderValues.Clear();
            foreach (GH_NumberSlider slider in sliders)
                _originalSliderValues[slider.InstanceGuid] = slider.Slider.Value;

            if (!resume)
            {
                _study.Iterations.Clear();
                _study.CreatedUtc = DateTime.UtcNow;
            }
            _study.DefinitionPath = OnPingDocument()?.FilePath ?? string.Empty;
            _studyCombinationIndex = resume ? _study.Iterations.Count : 0;
            if (_studyCombinationIndex >= _studyCombinations.Count)
            {
                _studyStatus = "The saved study already contains every configured combination.";
                _originalSliderValues.Clear();
                UpdateStudyWindow();
                return;
            }
            _studyRunning = true;
            _capturePending = false;
            _studyStatus = resume
                ? $"Resuming {_study.Iterations.Count} / {_studyCombinations.Count} iterations."
                : $"Running 0 / {_studyCombinations.Count} iterations.";
            ScheduleCombination(_studyCombinationIndex);
            UpdateStudyWindow();
        }

        internal void StopStudy(bool restoreSliders)
        {
            bool wasRunning = _studyRunning;
            _studyRunning = false;
            _capturePending = false;
            if (restoreSliders)
                RestoreOriginalSliderValues();
            if (wasRunning)
            {
                _studyStatus = $"Stopped after {_study.Iterations.Count} captured iteration(s).";
                SaveStudySafely();
                WriteRunExportsIfEnabled();
                _activeRunNameOverride = string.Empty;
                RefreshStudyCatalog();
            }
            UpdateStudyWindow();
        }

        internal void CaptureCurrentStudyIteration()
        {
            SyncLinkedParameters();
            CaptureIteration();
            _studyStatus = $"Captured iteration {_study.Iterations.Count - 1}.";
            SaveStudySafely();
            WriteRunExportsIfEnabled();
            RefreshStudyCatalog();
            UpdateStudyWindow();
            ExpireSolution(true);
        }

        internal void ClearStudyIterations()
        {
            if (_studyRunning)
                return;
            _study.Iterations.Clear();
            _studyStatus = "Cleared captured study iterations.";
            SaveStudySafely();
            RefreshStudyCatalog();
            UpdateStudyWindow();
            ExpireSolution(true);
        }

        internal void GenerateStudyReport(WasperReportSettings settings)
        {
            if (_studyRunning)
            {
                _reportStatus = "Stop the running study before generating its report.";
                UpdateStudyWindow();
                return;
            }

            try
            {
                _study.Report = settings ?? new WasperReportSettings();
                string safeName = ResolveBaseName(_study.RunName);
                string reportFolder = Path.Combine(
                    ResolveStudyFolder(_study.RunName, _currentFilePath),
                    "Reports");
                string reportPath = Path.Combine(reportFolder, safeName + "_report.pdf");
                _lastReportPath = WasperStudyReportPdf.Write(
                    _study,
                    _currentSet ?? new WasperKpiSet(),
                    _study.Report,
                    reportPath);
                _study.Report.OutputPath = _lastReportPath;
                if (!_lastWrittenFiles.Contains(_lastReportPath, StringComparer.OrdinalIgnoreCase))
                    _lastWrittenFiles.Add(_lastReportPath);
                _reportStatus = "PDF generated: " + _lastReportPath;
                SaveStudySafely();
                RefreshStudyCatalog();
                OnObjectChanged(GH_ObjectEventType.Options);
            }
            catch (Exception exception)
            {
                _reportStatus = "PDF generation failed: " + exception.Message;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, _reportStatus);
            }

            UpdateStudyWindow();
            ExpireSolution(true);
        }

        /// <summary>
        /// Restores one captured iteration onto the linked sliders so the Grasshopper definition
        /// rebuilds that exact sample. Unlike the rest of the Dashboard, this does recompute the
        /// document. The original slider values are remembered on the first use so the canvas can
        /// be returned to where it was.
        /// </summary>
        internal void ShowIterationInGrasshopper(int iterationIndex)
        {
            if (_studyRunning)
            {
                _studyStatus = "Stop the running study before restoring an iteration.";
                UpdateStudyWindow();
                return;
            }
            WasperStudyIteration iteration = (_viewedStudy ?? _study)?.Iterations
                ?.FirstOrDefault(item => item != null && item.Index == iterationIndex);
            if (iteration?.Parameters == null || iteration.Parameters.Count == 0)
            {
                _studyStatus = $"Iteration {iterationIndex} has no recorded input values.";
                UpdateStudyWindow();
                return;
            }
            GH_Document document = OnPingDocument() ?? Instances.ActiveCanvas?.Document;
            if (document == null)
                return;

            List<KeyValuePair<Guid, double>> assignments = ResolveIterationAssignments(iteration);
            if (assignments.Count == 0)
            {
                _studyStatus =
                    $"None of iteration {iterationIndex}'s inputs match a linked slider.";
                UpdateStudyWindow();
                return;
            }

            document.ScheduleSolution(10, scheduledDocument =>
            {
                foreach (KeyValuePair<Guid, double> assignment in assignments)
                {
                    if (!(scheduledDocument.FindObject(assignment.Key, true) is GH_NumberSlider slider) ||
                        slider.Slider == null)
                    {
                        continue;
                    }
                    if (!_originalSliderValues.ContainsKey(assignment.Key))
                        _originalSliderValues[assignment.Key] = slider.Slider.Value;
                    decimal value = Math.Max(
                        slider.Slider.Minimum,
                        Math.Min(slider.Slider.Maximum, (decimal)assignment.Value));
                    slider.SetSliderValue(value);
                    slider.ExpireSolution(false);
                }
            });
            string name = string.IsNullOrWhiteSpace(iteration.SampleName)
                ? $"iteration {iteration.Index}"
                : iteration.SampleName;
            _studyStatus = $"Restored {name} onto {assignments.Count} slider(s).";
            UpdateStudyWindow();
            ExpireSolution(true);
        }

        /// <summary>
        /// Rebuilds the slider-to-value mapping for a captured iteration. Capture stores values
        /// under <see cref="UniqueParameterKey"/>, which only disambiguates on collision, so the
        /// same key sequence is regenerated here rather than assumed to equal the parameter name.
        /// </summary>
        private List<KeyValuePair<Guid, double>> ResolveIterationAssignments(
            WasperStudyIteration iteration)
        {
            var assignments = new List<KeyValuePair<Guid, double>>();
            var reserved = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (WasperStudyParameter parameter in _study.Parameters.Where(item => item.Enabled))
            {
                string key = UniqueParameterKey(reserved, parameter.Name, parameter.SliderId);
                reserved[key] = 0.0;
                if (iteration.Parameters.TryGetValue(key, out double value) ||
                    iteration.Parameters.TryGetValue(parameter.Name ?? string.Empty, out value))
                {
                    assignments.Add(new KeyValuePair<Guid, double>(parameter.SliderId, value));
                }
            }
            return assignments;
        }

        /// <summary>
        /// Persists the Dashboard display settings on the study, so they survive closing the
        /// window, reopening the Grasshopper file, and restarting Rhino. Captured iterations are
        /// untouched; this only records how the charts are configured.
        /// </summary>
        internal void StoreDashboardSettings(WasperDashboardSettings settings)
        {
            _study.Dashboard = settings ?? new WasperDashboardSettings();
            SaveStudySafely();
            OnObjectChanged(GH_ObjectEventType.Options);
        }

        internal void StoreReportSettings(WasperReportSettings settings)
        {
            settings ??= new WasperReportSettings();
            settings.OutputPath = _study?.Report?.OutputPath ?? string.Empty;
            _study.Report = settings;
            SaveStudySafely();
            OnObjectChanged(GH_ObjectEventType.Options);
        }

        private void HandleStudySolve(string runName, string filePath)
        {
            string effectiveRunName = string.IsNullOrWhiteSpace(_activeRunNameOverride)
                ? runName
                : _activeRunNameOverride;
            _study.RunName = string.IsNullOrWhiteSpace(effectiveRunName)
                ? "WASPer_Study"
                : effectiveRunName.Trim();
            _studyFolder = ResolveStudyFolder(_study.RunName, filePath);
            SyncLinkedParameters();

            if (_studyRunning && _capturePending)
            {
                _capturePending = false;
                CaptureIteration();
                SaveStudySafely();
                _studyStatus = $"Running {_study.Iterations.Count} / {_studyCombinations.Count} iterations.";
                _studyCombinationIndex++;
                if (_studyCombinationIndex < _studyCombinations.Count)
                    ScheduleCombination(_studyCombinationIndex);
                else
                    CompleteStudy();
            }
            UpdateStudyWindow();
        }

        private string StudyMessage(string baseMessage)
        {
            if (_studyRunning)
                return $"{_version} | study {_study.Iterations.Count}/{_studyCombinations.Count}";
            if (_study?.Iterations?.Count > 0)
                return $"{_version} | {_study.Iterations.Count} iterations";
            return baseMessage;
        }

        private void ScheduleCombination(int index)
        {
            GH_Document document = OnPingDocument() ?? Instances.ActiveCanvas?.Document;
            if (document == null || index < 0 || index >= _studyCombinations.Count)
            {
                StopStudy(true);
                return;
            }

            IReadOnlyList<double> values = _studyCombinations[index];
            document.ScheduleSolution(10, scheduledDocument =>
            {
                if (!_studyRunning)
                    return;
                for (int parameterIndex = 0; parameterIndex < _activeStudyParameters.Count; parameterIndex++)
                {
                    WasperStudyParameter parameter = _activeStudyParameters[parameterIndex];
                    GH_NumberSlider slider = scheduledDocument.FindObject(
                        parameter.SliderId,
                        true) as GH_NumberSlider;
                    if (slider?.Slider == null)
                        continue;
                    decimal minimum = slider.Slider.Minimum;
                    decimal maximum = slider.Slider.Maximum;
                    decimal value = (decimal)values[parameterIndex];
                    value = Math.Max(minimum, Math.Min(maximum, value));
                    // Assign the control value without asking Grasshopper to start a nested
                    // recomputation. This callback already runs immediately before the scheduled
                    // solution, so one explicit expiration is sufficient.
                    slider.Slider.Value = value;
                    slider.ExpireSolution(false);
                }
                _capturePending = true;
                ExpireSolution(false);
            });
        }

        private void CompleteStudy()
        {
            _studyRunning = false;
            _capturePending = false;
            _studyStatus = $"Completed {_study.Iterations.Count} iterations.";
            SaveStudySafely();
            WriteRunExportsIfEnabled();
            RestoreOriginalSliderValues();
            _activeRunNameOverride = string.Empty;
            RefreshStudyCatalog();
        }

        private void WriteRunExportsIfEnabled()
        {
            if (!_writeWithRun || _study?.Iterations?.Count <= 0)
                return;
            WriteExports(_study.RunName, _currentFilePath, _editorFormat);
            _form?.SetWriteStatus(_lastWriteInfo);
        }

        private void CaptureIteration()
        {
            var iteration = new WasperStudyIteration
            {
                Index = _study.Iterations.Count,
                CapturedUtc = DateTime.UtcNow,
                Status = _currentSet?.Warnings?.Count > 0 ? "Warning" : "Complete"
            };
            foreach (WasperStudyParameter parameter in _study.Parameters.Where(p => p.Enabled))
            {
                GH_NumberSlider slider = ResolveSlider(parameter.SliderId);
                if (slider?.Slider == null)
                {
                    iteration.Warnings.Add($"Slider '{parameter.Name}' could not be resolved.");
                    continue;
                }
                string key = UniqueParameterKey(iteration.Parameters, parameter.Name, parameter.SliderId);
                iteration.Parameters[key] = (double)slider.Slider.Value;
            }
            iteration.Kpis = CloneKpis(_currentSet?.EnabledItems);
            iteration.SampleName = !string.IsNullOrWhiteSpace(_currentSampleNameOverride)
                ? CleanSampleName(_currentSampleNameOverride)
                : ComposeSampleName(iteration.Index, iteration.Parameters, _currentSet?.Items);
            if (_currentSet?.Warnings != null)
                iteration.Warnings.AddRange(_currentSet.Warnings);
            iteration.GcodeFiles = SaveIterationGcode(
                iteration.SampleName,
                iteration.Index,
                iteration.Warnings);
            iteration.SnapshotFiles = SaveIterationSnapshots(
                iteration.GcodeFiles,
                iteration.SampleName,
                iteration.Index,
                iteration.Warnings);
            iteration.XrFiles = _study?.XrPathsEnabled == true
                ? SaveIterationXrPackage(
                    _currentProcessViewerPath,
                    iteration.SampleName,
                    iteration.Index,
                    iteration.Warnings)
                : new List<string>();
            if (_study?.Snapshot?.Enabled == true && iteration.SnapshotFiles.Count == 0 &&
                !iteration.Warnings.Any(warning => warning.StartsWith(
                    "Viewport snapshot",
                    StringComparison.OrdinalIgnoreCase)))
            {
                iteration.Warnings.Add("Viewport snapshot was enabled, but no PNG file was saved.");
            }
            if (iteration.Warnings.Count > 0)
                iteration.Status = "Warning";
            _lastWrittenFiles = iteration.GcodeFiles
                .Concat(iteration.SnapshotFiles)
                .Concat(iteration.XrFiles)
                .ToList();
            _study.Iterations.Add(iteration);
            _study.UpdatedUtc = DateTime.UtcNow;
        }

        private void ApplyParameterConfiguration(IEnumerable<WasperStudyParameter> configured)
        {
            Dictionary<Guid, WasperStudyParameter> incoming =
                (configured ?? Enumerable.Empty<WasperStudyParameter>())
                .Where(parameter => parameter != null)
                .ToDictionary(parameter => parameter.SliderId, parameter => parameter);
            foreach (WasperStudyParameter parameter in _study.Parameters)
            {
                if (!incoming.TryGetValue(parameter.SliderId, out WasperStudyParameter changed))
                    continue;
                parameter.Minimum = changed.Minimum;
                parameter.Maximum = changed.Maximum;
                parameter.Samples = Math.Max(1, changed.Samples);
                GH_NumberSlider slider = ResolveSlider(parameter.SliderId);
                // Compare against the default for the edited study range, so narrowing the range
                // on an automatic parameter does not accidentally mark it as manual.
                parameter.SamplesAreManual = slider?.Slider == null ||
                    parameter.Samples != DefaultSliderSamples(parameter, slider);
                parameter.Enabled = changed.Enabled;
            }
        }

        /// <summary>
        /// Resets Study min/max/samples back to the linked slider's own current range and its
        /// default sample count over that full range - i.e. what SyncLinkedParameters would have
        /// set for a freshly linked slider, before any manual edits in the grid. Enabled state and
        /// everything else about the parameter is left untouched. An empty <paramref
        /// name="sliderIds"/> restores every linked parameter rather than none, matching the "go
        /// back to default after modifying things" use case the Study tab's Restore defaults
        /// button exists for.
        /// </summary>
        internal void RestoreParameterDefaults(IEnumerable<Guid> sliderIds)
        {
            var targets = new HashSet<Guid>(sliderIds ?? Enumerable.Empty<Guid>());
            bool restoreAll = targets.Count == 0;
            int restored = 0;
            foreach (WasperStudyParameter parameter in _study.Parameters)
            {
                if (!restoreAll && !targets.Contains(parameter.SliderId))
                    continue;
                GH_NumberSlider slider = ResolveSlider(parameter.SliderId);
                if (slider?.Slider == null)
                    continue;
                parameter.Minimum = (double)slider.Slider.Minimum;
                parameter.Maximum = (double)slider.Slider.Maximum;
                parameter.SamplesAreManual = false;
                parameter.Samples = DefaultSliderSamples(slider);
                restored++;
            }
            _studyStatus = restored == 0
                ? "No linked sliders to restore."
                : restoreAll
                    ? $"Restored {restored} parameter(s) to their slider defaults."
                    : $"Restored {restored} selected parameter(s) to their slider defaults.";
            _study.UpdatedUtc = DateTime.UtcNow;
            OnObjectChanged(GH_ObjectEventType.Options);
            UpdateStudyWindow();
        }

        private void SyncLinkedParameters()
        {
            _linkedSliderIds.RemoveAll(id => ResolveSlider(id) == null);
            _study.Parameters.RemoveAll(parameter => !_linkedSliderIds.Contains(parameter.SliderId));
            foreach (Guid id in _linkedSliderIds)
            {
                GH_NumberSlider slider = ResolveSlider(id);
                if (slider?.Slider == null)
                    continue;
                WasperStudyParameter parameter = _study.Parameters.FirstOrDefault(
                    item => item.SliderId == id);
                if (parameter == null)
                {
                    parameter = new WasperStudyParameter
                    {
                        SliderId = id,
                        SamplesAreManual = false,
                        Enabled = true
                    };
                    _study.Parameters.Add(parameter);
                }
                parameter.Name = string.IsNullOrWhiteSpace(slider.NickName)
                    ? slider.Name
                    : slider.NickName;
                if (parameter.Minimum == 0.0 && parameter.Maximum == 0.0)
                {
                    parameter.Minimum = (double)slider.Slider.Minimum;
                    parameter.Maximum = (double)slider.Slider.Maximum;
                }
                parameter.SliderAccuracy = slider.Slider.Type.ToString();
                parameter.SliderDecimalPlaces = slider.Slider.DecimalPlaces;
                parameter.OriginalValue = (double)slider.Slider.Value;
                // Resolved after the range and accuracy are known, so the default reflects the
                // values the slider can actually take across the study range.
                if (!parameter.SamplesAreManual)
                    parameter.Samples = DefaultSliderSamples(parameter, slider);
            }
        }

        /// <summary>
        /// Upper bound on an automatically derived sample count. A wide Integer slider (0..1000)
        /// would otherwise default to 1001 samples and blow up the cartesian sweep.
        /// </summary>
        private const int MaximumDefaultSamples = 25;

        /// <summary>
        /// Default sample count for a linked slider. Discrete sliders report the number of values
        /// they can actually take over the study range - an Integer slider spanning 0..7 gives 8.
        /// `GH_SliderBase.TickCount` is the slider's drag resolution, not its value count, so it is
        /// only used as a fallback for continuous Float sliders.
        /// </summary>
        private static int DefaultSliderSamples(
            GH_NumberSlider slider,
            double? rangeMinimum = null,
            double? rangeMaximum = null)
        {
            Grasshopper.GUI.Base.GH_SliderBase bar = slider?.Slider;
            if (bar == null)
                return 1;
            double minimum = rangeMinimum ?? (double)bar.Minimum;
            double maximum = rangeMaximum ?? (double)bar.Maximum;
            string accuracy = bar.Type.ToString();
            int discrete = WasperStudyParameter.DiscreteValueCount(
                minimum,
                maximum,
                WasperStudyParameter.StepFor(accuracy),
                WasperStudyParameter.OffsetFor(accuracy));
            return discrete > 0
                ? Math.Min(discrete, MaximumDefaultSamples)
                : Math.Max(2, Math.Min(MaximumDefaultSamples, bar.TickCount + 1));
        }

        /// <summary>Default sample count measured over the parameter's edited study range.</summary>
        private static int DefaultSliderSamples(
            WasperStudyParameter parameter,
            GH_NumberSlider slider)
        {
            return parameter == null
                ? DefaultSliderSamples(slider)
                : DefaultSliderSamples(slider, parameter.Minimum, parameter.Maximum);
        }

        private List<GH_NumberSlider> ResolveActiveSliders()
        {
            return _study.Parameters
                .Where(parameter => parameter.Enabled)
                .Select(parameter => ResolveSlider(parameter.SliderId))
                .Where(slider => slider != null)
                .ToList();
        }

        private GH_NumberSlider ResolveSlider(Guid id)
        {
            GH_Document document = OnPingDocument() ?? Instances.ActiveCanvas?.Document;
            return document?.FindObject(id, true) as GH_NumberSlider;
        }

        private void RestoreOriginalSliderValues()
        {
            GH_Document document = OnPingDocument() ?? Instances.ActiveCanvas?.Document;
            if (document == null || _originalSliderValues.Count == 0)
                return;

            // CompleteStudy is called from SolveInstance. Restoring sliders there would expire
            // their entire downstream graph while Grasshopper is still processing it, producing
            // one breakpoint per affected object. Copy and clear the state now, then mutate the
            // sliders only in the callback that precedes the next scheduled solution.
            List<KeyValuePair<Guid, decimal>> values = _originalSliderValues.ToList();
            _originalSliderValues.Clear();
            document.ScheduleSolution(10, scheduledDocument =>
            {
                foreach (KeyValuePair<Guid, decimal> pair in values)
                {
                    GH_NumberSlider slider = scheduledDocument.FindObject(
                        pair.Key,
                        true) as GH_NumberSlider;
                    if (slider?.Slider == null)
                        continue;
                    slider.Slider.Value = pair.Value;
                    slider.ExpireSolution(false);
                }
            });
        }

    }
}
