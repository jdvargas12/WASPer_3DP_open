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
            private Control CreateReportPanel()
            {
                _reportPageSize.Items.AddRange(new object[] { "A4", "A3", "Letter", "Legal" });
                _reportOrientation.Items.AddRange(new object[] { "Portrait", "Landscape" });
                _reportPageSize.SelectedItem = "A4";
                _reportOrientation.SelectedItem = "Portrait";
                _reportSnapshot.Checked = true;
                _reportIterations.Checked = true;

                var layout = new TableLayoutPanel
                {
                    AutoSize = true,
                    ColumnCount = 2,
                    Dock = DockStyle.Top,
                    Padding = new Padding(14),
                    RowCount = 8
                };
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                AddReportRow(layout, 0, "Report title", _reportTitle);
                AddReportRow(layout, 1, "Subtitle", _reportSubtitle);
                AddReportRow(layout, 2, "Page size", _reportPageSize);
                AddReportRow(layout, 3, "Orientation", _reportOrientation);
                AddReportRow(layout, 4, "Viewport image", _reportSnapshot);
                AddReportRow(layout, 5, "Iteration data", _reportIterations);
                AddReportRow(layout, 6, string.Empty, _generateReport);
                AddReportRow(layout, 7, "Status / PDF", _reportStatus);

                var help = new Label
                {
                    AutoSize = true,
                    Dock = DockStyle.Top,
                    MaximumSize = new Size(820, 0),
                    Padding = new Padding(14, 8, 14, 0),
                    Text = "Creates a native PDF in WASPer_<run name>\\Reports. The report " +
                        "summarizes the study, enabled KPI groups, and an optional preview of " +
                        "captured iterations. Full iteration data remains available through " +
                        "the CSV, Excel, and JSON exports."
                };

                var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
                panel.Controls.Add(layout);
                panel.Controls.Add(help);
                return panel;
            }

            private static void AddReportRow(
                TableLayoutPanel layout,
                int row,
                string label,
                Control control)
            {
                layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                layout.Controls.Add(new Label
                {
                    Text = label,
                    AutoSize = true,
                    Margin = new Padding(3, 7, 8, 7)
                }, 0, row);
                control.Margin = new Padding(3, 4, 3, 4);
                layout.Controls.Add(control, 1, row);
            }

            private WasperReportSettings ReadReportSettings()
            {
                return new WasperReportSettings
                {
                    Title = string.IsNullOrWhiteSpace(_reportTitle.Text)
                        ? "WASPer Study Report"
                        : _reportTitle.Text.Trim(),
                    Subtitle = _reportSubtitle.Text?.Trim() ?? string.Empty,
                    PageSize = _reportPageSize.SelectedItem?.ToString() ?? "A4",
                    Landscape = string.Equals(
                        _reportOrientation.SelectedItem?.ToString(),
                        "Landscape",
                        StringComparison.OrdinalIgnoreCase),
                    IncludeSnapshot = _reportSnapshot.Checked,
                    IncludeIterationTable = _reportIterations.Checked
                };
            }

            private void ReportSettingChanged(object sender, EventArgs eventArgs)
            {
                if (!_updatingReportControls)
                    ReportSettingsChanged?.Invoke(ReadReportSettings());
            }

            public void UpdateReport(WasperReportSettings settings, string status)
            {
                settings ??= new WasperReportSettings();
                _updatingReportControls = true;
                if (!_reportTitle.Focused)
                    _reportTitle.Text = settings.Title ?? "WASPer Study Report";
                if (!_reportSubtitle.Focused)
                    _reportSubtitle.Text = settings.Subtitle ?? string.Empty;
                _reportPageSize.SelectedItem = settings.PageSize ?? "A4";
                _reportOrientation.SelectedItem = settings.Landscape ? "Landscape" : "Portrait";
                _reportSnapshot.Checked = settings.IncludeSnapshot;
                _reportIterations.Checked = settings.IncludeIterationTable;
                _reportStatus.Text = status ?? string.Empty;
                _updatingReportControls = false;
            }

        }
    }
}
