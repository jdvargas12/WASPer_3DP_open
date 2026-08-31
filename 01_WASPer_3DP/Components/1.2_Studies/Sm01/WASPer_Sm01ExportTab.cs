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
            private Control CreateExportPanel()
            {
                _format.Items.AddRange(new object[] { "CSV", "Excel", "JSON", "All" });
                _format.SelectedItem = "All";
                _exportLayout.Items.AddRange(new object[]
                {
                    "Iterations in rows",
                    "KPIs in rows"
                });
                _exportLayout.SelectedItem = "Iterations in rows";

                var commandBar = new FlowLayoutPanel
                {
                    AutoSize = true,
                    Dock = DockStyle.Top,
                    FlowDirection = FlowDirection.LeftToRight,
                    Padding = new Padding(10, 7, 10, 3),
                    WrapContents = true
                };
                commandBar.Controls.Add(new Label
                {
                    Text = "Study name",
                    AutoSize = true,
                    Margin = new Padding(3, 6, 3, 0)
                });
                commandBar.Controls.Add(_fileName);
                commandBar.Controls.Add(new Label
                {
                    Text = "Save path",
                    AutoSize = true,
                    Margin = new Padding(12, 6, 3, 0)
                });
                commandBar.Controls.Add(_filePath);
                commandBar.Controls.Add(_browse);
                commandBar.Controls.Add(new Label
                {
                    Text = "Format",
                    AutoSize = true,
                    Margin = new Padding(12, 6, 3, 0)
                });
                commandBar.Controls.Add(_format);
                commandBar.Controls.Add(new Label
                {
                    Text = "Layout",
                    AutoSize = true,
                    Margin = new Padding(12, 6, 3, 0)
                });
                commandBar.Controls.Add(_exportLayout);
                commandBar.Controls.Add(_resetFiles);
                commandBar.Controls.Add(_writeFiles);

                var settings = new TableLayoutPanel
                {
                    AutoSize = true,
                    ColumnCount = 2,
                    Dock = DockStyle.Top,
                    Padding = new Padding(8, 4, 8, 4),
                    RowCount = 9
                };
                settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
                settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                settings.Controls.Add(_snapshotEnabled, 0, 0);
                settings.SetColumnSpan(_snapshotEnabled, 2);
                AddSnapshotRow(settings, 1, "Visualization", VisualizationLinkControls());
                AddSnapshotRow(settings, 2, "Viewport", SnapshotViewportControls());
                AddSnapshotRow(settings, 3, "Width (px)", _snapshotWidth);
                AddSnapshotRow(settings, 4, "Height (px)", _snapshotHeight);
                AddSnapshotRow(settings, 5, "DPI", _snapshotDpi);
                AddSnapshotRow(settings, 6, "Wait (ms)", _snapshotWait);
                AddSnapshotRow(settings, 7, string.Empty, _previewSnapshot);
                settings.Controls.Add(new Label
                {
                    AutoSize = true,
                    MaximumSize = new Size(385, 0),
                    Text = "To link a visualization, select one component on the Grasshopper " +
                        "canvas and then click 'Link vis component'. The linked component " +
                        "supplies a readiness and visibility check. " +
                        "Captured PNG files are saved in the run's Snapshots folder. " +
                        "They reuse G-code base names when G-code exists, or the sample name " +
                        "when no G-code is connected."
                }, 0, 8);
                settings.SetColumnSpan(settings.GetControlFromPosition(0, 8), 2);

                var previewPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
                previewPanel.Controls.Add(_snapshotPreview);
                previewPanel.Controls.Add(_snapshotStatus);
                var split = new SplitContainer
                {
                    Dock = DockStyle.Fill,
                    FixedPanel = FixedPanel.Panel1,
                    IsSplitterFixed = false,
                    Size = new Size(900, 500),
                    Panel1MinSize = 180,
                    Panel2MinSize = 180,
                    SplitterDistance = 360
                };
                split.Panel1.Controls.Add(settings);
                split.Panel2.Controls.Add(previewPanel);

                var snapshotBox = new GroupBox
                {
                    Dock = DockStyle.Fill,
                    Padding = new Padding(8),
                    Text = "Iteration viewport snapshots"
                };
                snapshotBox.Controls.Add(split);

                var statusPanel = new Panel
                {
                    Dock = DockStyle.Top,
                    Height = 28,
                    Padding = new Padding(12, 1, 12, 1)
                };
                _exportStatus.Dock = DockStyle.Fill;
                statusPanel.Controls.Add(_exportStatus);

                var panel = new Panel { Dock = DockStyle.Fill };
                panel.Controls.Add(snapshotBox);
                panel.Controls.Add(statusPanel);
                panel.Controls.Add(commandBar);
                return panel;
            }

            private void SetWriteWithRun(bool enabled)
            {
                _writeFiles.Checked = enabled;
                StyleWriteWithRun();
                _toolTip.SetToolTip(
                    _writeFiles,
                    "When enabled, the selected CSV/Excel/JSON formats are written " +
                    "automatically when a study completes, stops, or captures a manual iteration.");
            }

            private void StyleWriteWithRun()
            {
                if (_writeFiles.Checked)
                {
                    _writeFiles.Text = "Write with run: Yes";
                    _writeFiles.BackColor = Color.FromArgb(45, 125, 210);
                    _writeFiles.ForeColor = Color.White;
                    _writeFiles.FlatStyle = FlatStyle.Flat;
                    _writeFiles.FlatAppearance.BorderColor = Color.FromArgb(30, 90, 165);
                }
                else
                {
                    _writeFiles.Text = "Write with run: No";
                    _writeFiles.BackColor = SystemColors.Control;
                    _writeFiles.ForeColor = SystemColors.ControlText;
                    _writeFiles.FlatStyle = FlatStyle.Standard;
                }
            }

            private Control SnapshotViewportControls()
            {
                var panel = new FlowLayoutPanel
                {
                    AutoSize = true,
                    FlowDirection = FlowDirection.LeftToRight,
                    Margin = Padding.Empty,
                    WrapContents = false
                };
                panel.Controls.Add(_snapshotViewport);
                panel.Controls.Add(_refreshViewports);
                return panel;
            }

            private Control VisualizationLinkControls()
            {
                var panel = new FlowLayoutPanel
                {
                    AutoSize = true,
                    FlowDirection = FlowDirection.LeftToRight,
                    Margin = Padding.Empty,
                    WrapContents = true,
                    Width = 285
                };
                panel.Controls.Add(_linkVisualization);
                panel.Controls.Add(_unlinkVisualization);
                panel.Controls.Add(_linkedVisualizationStatus);
                return panel;
            }

            private static void AddSnapshotRow(
                TableLayoutPanel layout,
                int row,
                string label,
                Control control)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(new Label
                {
                    AutoSize = true,
                    Margin = new Padding(3, 7, 8, 5),
                    Text = label
                }, 0, row);
                control.Margin = new Padding(3, 3, 3, 3);
                layout.Controls.Add(control, 1, row);
            }

            public void UpdateSnapshotSettings(WasperSnapshotSettings settings)
            {
                settings ??= new WasperSnapshotSettings();
                _updatingSnapshotControls = true;
                try
                {
                    _snapshotEnabled.Checked = settings.Enabled;
                    _snapshotWidth.Value = ClampDecimal(settings.Width, _snapshotWidth);
                    _snapshotHeight.Value = ClampDecimal(settings.Height, _snapshotHeight);
                    _snapshotDpi.Value = ClampDecimal(settings.Dpi, _snapshotDpi);
                    _snapshotWait.Value = ClampDecimal(settings.WaitMilliseconds, _snapshotWait);
                    _linkedVisualizationId = settings.VisualizationComponentId;
                    _linkedVisualizationName = settings.VisualizationComponentName?.Trim() ??
                        string.Empty;
                    _linkedVisualizationStatus.Text = _linkedVisualizationId == Guid.Empty
                        ? "No visualization component linked."
                        : "Linked: " + (string.IsNullOrWhiteSpace(_linkedVisualizationName)
                            ? _linkedVisualizationId.ToString("D")
                            : _linkedVisualizationName);
                    _unlinkVisualization.Enabled = _linkedVisualizationId != Guid.Empty;
                    RefreshViewportChoices(settings.ViewportName);
                }
                finally
                {
                    _updatingSnapshotControls = false;
                }
                if (_snapshotPreview.Image == null && IsHandleCreated)
                    BeginInvoke((Action)RefreshSnapshotPreview);
            }

            private static decimal ClampDecimal(int value, NumericUpDown control)
            {
                return Math.Max(control.Minimum, Math.Min(control.Maximum, value));
            }

            private void RefreshViewportChoices(string requested = null)
            {
                string selected = requested;
                if (selected == null)
                {
                    selected = string.Equals(
                        Convert.ToString(_snapshotViewport.SelectedItem),
                        WasperViewportCapture.ActiveViewportLabel,
                        StringComparison.OrdinalIgnoreCase)
                        ? string.Empty
                        : Convert.ToString(_snapshotViewport.SelectedItem);
                }

                bool previousUpdate = _updatingSnapshotControls;
                _updatingSnapshotControls = true;
                try
                {
                    _snapshotViewport.BeginUpdate();
                    _snapshotViewport.Items.Clear();
                    _snapshotViewport.Items.Add(WasperViewportCapture.ActiveViewportLabel);
                    foreach (string name in WasperViewportCapture.ViewportNames())
                        _snapshotViewport.Items.Add(name);
                    string target = string.IsNullOrWhiteSpace(selected)
                        ? WasperViewportCapture.ActiveViewportLabel
                        : selected.Trim();
                    int index = _snapshotViewport.FindStringExact(target);
                    _snapshotViewport.SelectedIndex = index >= 0 ? index : 0;
                    _snapshotViewport.EndUpdate();
                }
                finally
                {
                    _updatingSnapshotControls = previousUpdate;
                }
            }

            private void SnapshotSettingChanged(object sender, EventArgs eventArgs)
            {
                if (_updatingSnapshotControls)
                    return;
                SnapshotSettingsChanged?.Invoke(ReadSnapshotSettings());
            }

            private void SnapshotViewportChanged(object sender, EventArgs eventArgs)
            {
                if (_updatingSnapshotControls)
                    return;
                SnapshotSettingsChanged?.Invoke(ReadSnapshotSettings());
                BeginInvoke((Action)RefreshSnapshotPreview);
            }

            private WasperSnapshotSettings ReadSnapshotSettings()
            {
                string viewport = Convert.ToString(_snapshotViewport.SelectedItem);
                if (string.Equals(
                    viewport,
                    WasperViewportCapture.ActiveViewportLabel,
                    StringComparison.OrdinalIgnoreCase))
                {
                    viewport = string.Empty;
                }
                return new WasperSnapshotSettings
                {
                    Enabled = _snapshotEnabled.Checked,
                    ViewportName = viewport ?? string.Empty,
                    Width = Decimal.ToInt32(_snapshotWidth.Value),
                    Height = Decimal.ToInt32(_snapshotHeight.Value),
                    Dpi = Decimal.ToInt32(_snapshotDpi.Value),
                    WaitMilliseconds = Decimal.ToInt32(_snapshotWait.Value),
                    VisualizationComponentId = _linkedVisualizationId,
                    VisualizationComponentName = _linkedVisualizationName
                };
            }

            private void RefreshSnapshotPreview()
            {
                try
                {
                    WasperSnapshotSettings requested = ReadSnapshotSettings();
                    double scale = Math.Min(
                        1.0,
                        Math.Min(1200.0 / requested.Width, 800.0 / requested.Height));
                    var previewSettings = new WasperSnapshotSettings
                    {
                        ViewportName = requested.ViewportName,
                        Width = Math.Max(64, (int)Math.Round(requested.Width * scale)),
                        Height = Math.Max(64, (int)Math.Round(requested.Height * scale)),
                        Dpi = requested.Dpi,
                        WaitMilliseconds = 0
                    };
                    Bitmap preview = WasperViewportCapture.Capture(previewSettings, false);
                    Image previous = _snapshotPreview.Image;
                    _snapshotPreview.Image = preview;
                    previous?.Dispose();
                    string view = string.IsNullOrWhiteSpace(requested.ViewportName)
                        ? "active viewport"
                        : requested.ViewportName;
                    _snapshotStatus.Text = $"Frame preview: {view} · {requested.Width} × {requested.Height}px · {requested.Dpi} DPI";
                }
                catch (Exception exception)
                {
                    _snapshotStatus.Text = "Preview unavailable: " + exception.Message;
                }
            }

            public void UpdateExportControls(
                string fileName,
                string filePath,
                bool filePathIsDefault,
                string format,
                string layout,
                bool fileNameConnected,
                string status)
            {
                _updatingExportControls = true;
                if (_fileName.Text != (fileName ?? string.Empty))
                    _fileName.Text = fileName ?? string.Empty;
                if (_filePath.Text != (filePath ?? string.Empty))
                    _filePath.Text = filePath ?? string.Empty;
                _filePathShowingDefault = filePathIsDefault;
                string normalized = NormalizeFormat(format);
                if (!string.Equals(SelectedFormat(), normalized, StringComparison.OrdinalIgnoreCase))
                    _format.SelectedItem = normalized;
                string normalizedLayout = NormalizeExportLayout(layout);
                if (!string.Equals(
                    SelectedExportLayout(),
                    normalizedLayout,
                    StringComparison.OrdinalIgnoreCase))
                {
                    _exportLayout.SelectedItem = normalizedLayout;
                }

                _fileName.Enabled = !fileNameConnected;
                _filePath.Enabled = true;
                _browse.Enabled = true;
                _resetFiles.Visible = true;
                _writeFiles.Visible = true;
                _exportStatus.Text = status ?? string.Empty;
                _updatingExportControls = false;
            }

            public void SetWriteStatus(string status)
            {
                _exportStatus.Text = status ?? string.Empty;
            }
            private void ExportSettingChanged(object sender, EventArgs eventArgs)
            {
                if (_updatingExportControls)
                    return;
                ExportSettingsChanged?.Invoke(_fileName.Text, ExportFilePathValue(), SelectedFormat());
            }

            private void BrowseFolder(object sender, EventArgs eventArgs)
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "Select the KPI export folder",
                    ShowNewFolderButton = true
                };
                if (Directory.Exists(_filePath.Text))
                    dialog.SelectedPath = _filePath.Text;
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                _filePathShowingDefault = false;
                _filePath.Text = dialog.SelectedPath;
                ExportSettingChanged(_filePath, EventArgs.Empty);
            }

            private string ExportFilePathValue()
            {
                return _filePathShowingDefault ? string.Empty : _filePath.Text;
            }

            private string SelectedFormat()
            {
                return _format.SelectedItem?.ToString() ?? "All";
            }

            private string SelectedExportLayout()
            {
                return _exportLayout.SelectedItem?.ToString() ?? "Iterations in rows";
            }

        }
    }
}
