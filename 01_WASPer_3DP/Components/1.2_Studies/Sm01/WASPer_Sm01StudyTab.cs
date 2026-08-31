using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

using ClosedXML.Excel;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace WASPer_3DP.Components._1_2_Studies
{
    public sealed partial class wsp_Sm01_WASPer_Study_Manager
    {
        private sealed partial class KpiManagerForm
        {
            private Control CreateStudyPanel()
            {
                ConfigureStudyGrids();

                var commandBar = new FlowLayoutPanel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Top,
                    FlowDirection = FlowDirection.LeftToRight,
                    Padding = new Padding(6, 5, 6, 3),
                    WrapContents = true
                };
                commandBar.Controls.Add(_linkSelected);
                commandBar.Controls.Add(_unlinkSelected);
                commandBar.Controls.Add(_restoreDefaults);
                commandBar.Controls.Add(_runStudy);
                commandBar.Controls.Add(_resumeStudy);
                commandBar.Controls.Add(_stopStudy);
                commandBar.Controls.Add(_captureIteration);
                commandBar.Controls.Add(_clearIterations);
                commandBar.Controls.Add(_saveStudy);
                commandBar.Controls.Add(_studyProgress);

                var libraryBar = new FlowLayoutPanel
                {
                    AutoSize = true,
                    Dock = DockStyle.Top,
                    FlowDirection = FlowDirection.LeftToRight,
                    Padding = new Padding(6, 4, 6, 1),
                    WrapContents = true
                };
                libraryBar.Controls.Add(new Label
                {
                    AutoSize = true,
                    Margin = new Padding(3, 7, 4, 0),
                    Text = "Study"
                });
                libraryBar.Controls.Add(_studyLibrary);
                libraryBar.Controls.Add(_refreshStudies);
                libraryBar.Controls.Add(_browseStudy);
                libraryBar.Controls.Add(_forgetStudy);
                libraryBar.Controls.Add(_studyCompatibility);
                libraryBar.Controls.Add(_loadStudy);
                libraryBar.Controls.Add(_resumeSavedStudy);
                var libraryGroup = new GroupBox
                {
                    Dock = DockStyle.Top,
                    Height = 82,
                    Padding = new Padding(6),
                    Text = "Study Library - auto-detected from the current save path, plus anything pinned via Browse..."
                };
                libraryGroup.Controls.Add(libraryBar);
                libraryGroup.Controls.Add(_studyLibraryStatus);

                var statusBar = new Panel
                {
                    Dock = DockStyle.Top,
                    Height = 28,
                    Padding = new Padding(4, 1, 4, 2)
                };
                statusBar.Controls.Add(_studyStatus);
                var activityGroup = new GroupBox
                {
                    Dock = DockStyle.Bottom,
                    Height = 140,
                    Padding = new Padding(8),
                    Text = "Study activity"
                };
                activityGroup.Controls.Add(_studyLog);
                activityGroup.Controls.Add(statusBar);

                var split = new SplitContainer
                {
                    Dock = DockStyle.Fill,
                    Orientation = Orientation.Horizontal,
                    SplitterDistance = 230
                };
                var parameterGroup = new GroupBox
                {
                    Text = "Linked Number Sliders - edit study minimum, maximum, and sample count",
                    Dock = DockStyle.Fill,
                    Padding = new Padding(8)
                };
                parameterGroup.Controls.Add(_parameterGrid);
                parameterGroup.Controls.Add(_estimatedIterations);
                var iterationGroup = new GroupBox
                {
                    Text = "Captured iterations",
                    Dock = DockStyle.Fill,
                    Padding = new Padding(8)
                };
                iterationGroup.Controls.Add(_iterationGrid);
                split.Panel1.Controls.Add(parameterGroup);
                split.Panel2.Controls.Add(iterationGroup);

                var panel = new Panel { Dock = DockStyle.Fill };
                panel.Controls.Add(split);
                panel.Controls.Add(libraryGroup);
                panel.Controls.Add(commandBar);
                panel.Controls.Add(activityGroup);
                return panel;
            }

            public void UpdateStudyLibrary(
                IEnumerable<WasperStudyCatalogEntry> entries,
                string selectedPath)
            {
                _updatingStudyLibrary = true;
                List<WasperStudyCatalogEntry> available =
                    (entries ?? Enumerable.Empty<WasperStudyCatalogEntry>()).ToList();
                _studyLibrary.Items.Clear();
                _dashboardStudyLibrary.Items.Clear();
                foreach (WasperStudyCatalogEntry entry in available)
                {
                    _studyLibrary.Items.Add(entry);
                    _dashboardStudyLibrary.Items.Add(entry);
                }
                WasperStudyCatalogEntry selected = available.FirstOrDefault(entry =>
                    string.Equals(
                        entry.FilePath ?? string.Empty,
                        selectedPath ?? string.Empty,
                        StringComparison.OrdinalIgnoreCase));
                _studyLibrary.SelectedItem = selected ?? available.FirstOrDefault();
                _dashboardStudyLibrary.SelectedItem = selected ?? available.FirstOrDefault();
                _updatingStudyLibrary = false;
                UpdateStudyLibrarySelection();
            }

            private WasperStudyCatalogEntry SelectedStudyEntry()
            {
                return _studyLibrary.SelectedItem as WasperStudyCatalogEntry;
            }

            private void StudyLibrarySelectionChangedFrom(ComboBox source)
            {
                WasperStudyCatalogEntry entry = source?.SelectedItem as WasperStudyCatalogEntry;
                _updatingStudyLibrary = true;
                _studyLibrary.SelectedItem = entry;
                _dashboardStudyLibrary.SelectedItem = entry;
                _updatingStudyLibrary = false;
                UpdateStudyLibrarySelection();
                StudyLibrarySelectionChanged?.Invoke(entry);
            }

            private void UpdateStudyLibrarySelection()
            {
                WasperStudyCatalogEntry entry = SelectedStudyEntry();
                if (entry == null)
                {
                    _studyLibraryStatus.Text = "No studies found automatically. Use Browse... to " +
                        "pin one saved from a different, renamed, or relocated .gh file.";
                    _loadStudy.Enabled = false;
                    _resumeSavedStudy.Enabled = false;
                    _studyCompatibility.Enabled = false;
                    _forgetStudy.Enabled = false;
                    return;
                }
                string issue = entry.Issues?.FirstOrDefault();
                _studyLibraryStatus.Text = entry.IsCurrent
                    ? "Live study connected to this Sm01 component."
                    : $"{entry.StatusLabel}" +
                        (string.IsNullOrWhiteSpace(issue) ? string.Empty : " - " + issue);
                _loadStudy.Enabled = !entry.IsCurrent && entry.CanResume;
                _resumeSavedStudy.Enabled = !entry.IsCurrent && entry.CanResume;
                _studyCompatibility.Enabled = !entry.IsCurrent;
                _forgetStudy.Enabled = !entry.IsCurrent && entry.IsPinned;
            }

            private void ShowSelectedStudyCompatibility(object sender, EventArgs eventArgs)
            {
                WasperStudyCatalogEntry entry = SelectedStudyEntry();
                if (entry == null || entry.IsCurrent)
                    return;
                string details = entry.Issues == null || entry.Issues.Count == 0
                    ? "No compatibility issues were detected."
                    : string.Join(Environment.NewLine, entry.Issues.Select(issue => "- " + issue));
                MessageBox.Show(
                    this,
                    $"Status: {entry.StatusLabel}{Environment.NewLine}{Environment.NewLine}{details}",
                    "Study compatibility",
                    MessageBoxButtons.OK,
                    entry.CanResume ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }

            private void RunStudyClicked(object sender, EventArgs eventArgs)
            {
                List<string> disabledSources = DisabledKpiSourceNames();
                if (disabledSources.Count > 0)
                {
                    string bullets = string.Join(
                        Environment.NewLine,
                        disabledSources.Select(name => "\u2022 " + name));
                    string message =
                        "The following KPI components are disabled:" +
                        Environment.NewLine + Environment.NewLine +
                        bullets +
                        Environment.NewLine + Environment.NewLine +
                        "Their KPIs will not be included in the study." +
                        Environment.NewLine + Environment.NewLine +
                        "Do you want to continue?";
                    AppendStudyLog(
                        "Run requested with disabled KPI components: " +
                        string.Join(", ", disabledSources) + ".");
                    DialogResult result = MessageBox.Show(
                        this,
                        message,
                        "Disabled KPI components",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (result != DialogResult.Yes)
                    {
                        AppendStudyLog("Study start cancelled by the user.");
                        return;
                    }
                    AppendStudyLog("User confirmed that the study should continue.");
                }
                else
                {
                    AppendStudyLog("Run study requested. All KPI components are enabled.");
                }
                RunStudyRequested?.Invoke(ReadParameterConfiguration());
            }

            private List<string> DisabledKpiSourceNames()
            {
                return _sourceToggles
                    .Where(pair => pair.Value.Any(button => !button.SourceEnabled))
                    .Select(pair => _sourceToggleNames.TryGetValue(pair.Key, out string name)
                        ? name
                        : pair.Key.ToString("D"))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            private void AppendStudyLog(string message)
            {
                if (string.IsNullOrWhiteSpace(message))
                    return;
                string line = $"[{DateTime.Now:HH:mm:ss}] {message.Trim()}";
                if (_studyLog.TextLength > 0)
                    _studyLog.AppendText(Environment.NewLine);
                _studyLog.AppendText(line);
                _studyLog.SelectionStart = _studyLog.TextLength;
                _studyLog.ScrollToCaret();
            }

            private void ConfigureStudyGrids()
            {
                _parameterGrid.Columns.Add(new DataGridViewCheckBoxColumn
                {
                    Name = "Enabled",
                    HeaderText = "Use",
                    FillWeight = 35
                });
                _parameterGrid.Columns.Add("Parameter", "Parameter");
                _parameterGrid.Columns.Add("Current", "Current");
                _parameterGrid.Columns.Add("Minimum", "Study min");
                _parameterGrid.Columns.Add("Maximum", "Study max");
                _parameterGrid.Columns.Add("Samples", "Samples");
                _parameterGrid.Columns[1].ReadOnly = true;
                _parameterGrid.Columns[2].ReadOnly = true;
                _parameterGrid.Columns[0].FillWeight = 35;
                _parameterGrid.Columns[1].FillWeight = 120;
                _parameterGrid.Columns[2].FillWeight = 55;
                _parameterGrid.Columns[3].FillWeight = 55;
                _parameterGrid.Columns[4].FillWeight = 55;
                _parameterGrid.Columns[5].FillWeight = 45;

                _iterationGrid.Columns.Add("Index", "ID");
                _iterationGrid.Columns.Add("SampleName", "Sample name");
                _iterationGrid.Columns.Add("Parameters", "Parameters");
                _iterationGrid.Columns.Add("Kpis", "KPIs");
                _iterationGrid.Columns.Add("Status", "Status");
                _iterationGrid.Columns.Add("Captured", "Captured UTC");
                _iterationGrid.Columns[0].FillWeight = 30;
                _iterationGrid.Columns[1].FillWeight = 90;
                _iterationGrid.Columns[2].FillWeight = 140;
                _iterationGrid.Columns[3].FillWeight = 40;
                _iterationGrid.Columns[4].FillWeight = 50;
                _iterationGrid.Columns[5].FillWeight = 90;
            }

            public void UpdateStudy(
                IEnumerable<WasperStudyParameter> parameters,
                IEnumerable<WasperStudyIteration> iterations,
                string status,
                double progress,
                bool running,
                bool viewingSavedStudy)
            {
                List<WasperStudyParameter> parameterList =
                    (parameters ?? Enumerable.Empty<WasperStudyParameter>()).ToList();
                List<WasperStudyIteration> iterationList =
                    (iterations ?? Enumerable.Empty<WasperStudyIteration>()).ToList();
                if (!_parameterGrid.IsCurrentCellInEditMode)
                {
                    _parameterGrid.Rows.Clear();
                    foreach (WasperStudyParameter parameter in parameterList)
                    {
                        int rowIndex = _parameterGrid.Rows.Add(
                            parameter.Enabled,
                            parameter.Name,
                            parameter.OriginalValue.ToString("0.######", CultureInfo.InvariantCulture),
                            parameter.Minimum.ToString("0.######", CultureInfo.InvariantCulture),
                            parameter.Maximum.ToString("0.######", CultureInfo.InvariantCulture),
                            parameter.Samples);
                        _parameterGrid.Rows[rowIndex].Tag = parameter.SliderId;
                    }
                    UpdateEstimatedIterationCount();
                }

                _iterationGrid.Rows.Clear();
                foreach (WasperStudyIteration iteration in
                    iterationList.AsEnumerable().Reverse().Take(250))
                {
                    string parameterText = string.Join(
                        "; ",
                        iteration.Parameters.Select(pair => $"{pair.Key}={pair.Value:0.####}"));
                    _iterationGrid.Rows.Add(
                        iteration.Index,
                        iteration.SampleName,
                        parameterText,
                        iteration.Kpis?.Count ?? 0,
                        iteration.Status,
                        iteration.CapturedUtc.ToString("u"));
                }

                _studyStatus.Text = status ?? string.Empty;
                string normalizedStatus = status?.Trim() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(normalizedStatus) &&
                    !string.Equals(
                        normalizedStatus,
                        _lastLoggedStudyStatus,
                        StringComparison.Ordinal))
                {
                    _lastLoggedStudyStatus = normalizedStatus;
                    AppendStudyLog(normalizedStatus);
                }
                int numericKpis = iterationList
                    .SelectMany(iteration => iteration.Kpis ?? new List<WasperKpi>())
                    .Where(kpi => kpi?.Value.HasValue == true)
                    .Select(kpi => kpi.Key)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                _dashboardStatus.Text = iterationList.Count == 0
                    ? "No captured iterations yet. Run or capture a study to populate the dashboard."
                    : $"{iterationList.Count} captured iteration(s) · " +
                        $"{parameterList.Count(parameter => parameter.Enabled)} active parameter(s) · " +
                        $"{numericKpis} numeric KPI series";
                UpdateDashboardData(parameterList, iterationList);
                _studyProgress.Value = Math.Max(0, Math.Min(100, (int)Math.Round(progress * 100.0)));
                _runStudy.Enabled = !running && !viewingSavedStudy;
                _resumeStudy.Enabled = !running && !viewingSavedStudy && iterationList.Count > 0;
                _linkSelected.Enabled = !running && !viewingSavedStudy;
                _unlinkSelected.Enabled = !running && !viewingSavedStudy;
                _restoreDefaults.Enabled = !running && !viewingSavedStudy;
                _clearIterations.Enabled = !running && !viewingSavedStudy;
                _stopStudy.Enabled = running;
                _captureIteration.Enabled = !running && !viewingSavedStudy;
                _saveStudy.Enabled = !viewingSavedStudy;
                _parameterGrid.ReadOnly = running || viewingSavedStudy;
            }

            private List<Guid> SelectedSliderIds()
            {
                return _parameterGrid.SelectedRows
                    .Cast<DataGridViewRow>()
                    .Select(row => row.Tag)
                    .OfType<Guid>()
                    .ToList();
            }

            private List<WasperStudyParameter> ReadParameterConfiguration()
            {
                _parameterGrid.EndEdit();
                var parameters = new List<WasperStudyParameter>();
                foreach (DataGridViewRow row in _parameterGrid.Rows)
                {
                    if (row.Tag is not Guid id)
                        continue;
                    parameters.Add(new WasperStudyParameter
                    {
                        SliderId = id,
                        Name = Convert.ToString(row.Cells["Parameter"].Value),
                        Enabled = Convert.ToBoolean(row.Cells["Enabled"].Value ?? true),
                        Minimum = ParseNumber(row.Cells["Minimum"].Value),
                        Maximum = ParseNumber(row.Cells["Maximum"].Value),
                        Samples = Math.Max(1, ParseInteger(row.Cells["Samples"].Value, 3))
                    });
                }
                return parameters;
            }

            /// <summary>
            /// Live "how many iterations would this study run" preview under the parameter grid.
            /// Reads straight from the grid via ReadParameterConfiguration() (not the component's
            /// saved _study.Parameters), so it reflects unsaved edits immediately, and multiplies
            /// each enabled parameter's actual WasperStudyParameter.Values().Count() - not its raw
            /// Samples number - since coarse slider accuracy can snap several requested samples onto
            /// the same value and Values() is exactly what StartStudy's WasperStudyStorage.
            /// CartesianValues cartesian-multiplies at run time (see WASPer_Sm01StudyRunner.cs).
            /// </summary>
            private void UpdateEstimatedIterationCount()
            {
                List<WasperStudyParameter> parameters = ReadParameterConfiguration();
                List<WasperStudyParameter> enabled = parameters.Where(p => p.Enabled).ToList();
                if (enabled.Count == 0)
                {
                    _estimatedIterations.Text = "Estimated iterations: 0 (no parameters enabled)";
                    return;
                }

                const long displayCap = 2_000_000;
                long count = 1;
                foreach (WasperStudyParameter parameter in enabled)
                {
                    int values = Math.Max(1, parameter.Values().Count());
                    count = count > displayCap / Math.Max(1, values) ? displayCap + 1 : count * values;
                    if (count > displayCap)
                        break;
                }
                _estimatedIterations.Text = count > displayCap
                    ? $"Estimated iterations: {displayCap:N0}+ across {enabled.Count} enabled " +
                        "parameter(s) - consider reducing samples"
                    : $"Estimated iterations: {count:N0} across {enabled.Count} enabled parameter(s)";
            }

            private static double ParseNumber(object value)
            {
                string text = Convert.ToString(value, CultureInfo.InvariantCulture);
                return double.TryParse(
                    text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double number)
                    ? number
                    : 0.0;
            }

            private static int ParseInteger(object value, int fallback)
            {
                string text = Convert.ToString(value, CultureInfo.InvariantCulture);
                return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                    ? number
                    : fallback;
            }

        }
    }
}
