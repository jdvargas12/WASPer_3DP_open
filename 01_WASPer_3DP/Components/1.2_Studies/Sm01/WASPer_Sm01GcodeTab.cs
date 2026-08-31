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
            private Control CreateGcodePanel()
            {
                var header = new FlowLayoutPanel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Top,
                    Padding = new Padding(8, 6, 8, 4),
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = true
                };
                header.Controls.Add(new Label
                {
                    Text = "Input branch",
                    AutoSize = true,
                    Margin = new Padding(3, 6, 5, 0)
                });
                header.Controls.Add(_gcodeBranch);
                header.Controls.Add(_saveGcode);

                var footer = new Panel
                {
                    Dock = DockStyle.Bottom,
                    Height = 32,
                    Padding = new Padding(9, 3, 9, 3)
                };
                footer.Controls.Add(_gcodeStatus);

                var viewerBox = new GroupBox
                {
                    Text = "Current recomputed G-code",
                    Dock = DockStyle.Fill,
                    Padding = new Padding(8)
                };
                viewerBox.Controls.Add(_gcodeViewer);

                var panel = new Panel { Dock = DockStyle.Fill };
                panel.Controls.Add(viewerBox);
                panel.Controls.Add(header);
                panel.Controls.Add(footer);
                return panel;
            }

            public void UpdateGcode(
                IEnumerable<List<string>> branches,
                IEnumerable<string> capturedFiles)
            {
                int selected = Math.Max(0, _gcodeBranch.SelectedIndex);
                _displayedGcodeBranches = (branches ?? Enumerable.Empty<List<string>>())
                    .Select(branch => branch?.ToList() ?? new List<string>())
                    .ToList();

                _gcodeBranch.BeginUpdate();
                _gcodeBranch.Items.Clear();
                for (int index = 0; index < _displayedGcodeBranches.Count; index++)
                {
                    _gcodeBranch.Items.Add(
                        $"Branch {index} ({_displayedGcodeBranches[index].Count:N0} lines)");
                }
                _gcodeBranch.EndUpdate();
                if (_gcodeBranch.Items.Count > 0)
                    _gcodeBranch.SelectedIndex = Math.Min(selected, _gcodeBranch.Items.Count - 1);
                // Always refresh, even when the selected index is unchanged: the branch at that
                // index may have new content after a Grasshopper recompute, and the Save button's
                // enabled state has to track it either way.
                RenderGcodeBranch();

                List<string> files = (capturedFiles ?? Enumerable.Empty<string>()).ToList();
                string capture = files.Count == 0
                    ? "No iteration G-code has been captured yet."
                    : $"Latest iteration: {files.Count} G-code file(s) saved in {Path.GetDirectoryName(files[0])}.";
                _gcodeStatus.Text = _displayedGcodeBranches.Count == 0
                    ? "Connect Gc03 g_code to the Sm01 gcode input. " + capture
                    : $"{_displayedGcodeBranches.Count} input branch(es). " + capture;
            }

            private void RenderGcodeBranch()
            {
                int index = _gcodeBranch.SelectedIndex;
                if (index < 0 || index >= _displayedGcodeBranches.Count)
                {
                    _gcodeViewer.Clear();
                    _saveGcode.Enabled = false;
                    return;
                }
                _saveGcode.Enabled = _displayedGcodeBranches[index]
                    .Any(line => !string.IsNullOrWhiteSpace(line));

                const int maximumLines = 20000;
                const int maximumCharacters = 2000000;
                var builder = new StringBuilder();
                int displayedLines = 0;
                foreach (string line in _displayedGcodeBranches[index])
                {
                    if (displayedLines >= maximumLines ||
                        builder.Length + (line?.Length ?? 0) + Environment.NewLine.Length > maximumCharacters)
                    {
                        builder.AppendLine("; Preview truncated to keep the manager responsive.");
                        break;
                    }
                    builder.AppendLine(line ?? string.Empty);
                    displayedLines++;
                }
                _gcodeViewer.Text = builder.ToString();
                _gcodeViewer.SelectionStart = 0;
                _gcodeViewer.ScrollToCaret();
            }

            /// <summary>
            /// Manual, on-demand save of the branch currently shown in the viewer. This is
            /// independent of the automatic per-iteration G-code capture (Assets.SaveIterationGcode)
            /// used while a study runs; it just writes whatever is on screen to a file the user
            /// picks.
            /// </summary>
            private void SaveGcodeClicked(object sender, EventArgs eventArgs)
            {
                int index = _gcodeBranch.SelectedIndex;
                if (index < 0 || index >= _displayedGcodeBranches.Count)
                    return;
                List<string> lines = _displayedGcodeBranches[index];
                if (lines == null || !lines.Any(line => !string.IsNullOrWhiteSpace(line)))
                {
                    _gcodeStatus.Text = "Nothing to save: the selected branch has no G-code lines.";
                    return;
                }

                string suggestedName = _gcodeBranch.Items.Count > 1
                    ? $"Gcode_Branch{index + 1}.gcode"
                    : "Gcode.gcode";
                using var dialog = new SaveFileDialog
                {
                    AddExtension = true,
                    DefaultExt = "gcode",
                    FileName = suggestedName,
                    Filter = "G-code files (*.gcode)|*.gcode|All files (*.*)|*.*",
                    InitialDirectory = Directory.Exists(_filePath.Text)
                        ? _filePath.Text
                        : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    Title = "Save G-code"
                };
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                try
                {
                    File.WriteAllLines(dialog.FileName, lines, new UTF8Encoding(false));
                    _gcodeStatus.Text = $"Saved G-code to {dialog.FileName}.";
                }
                catch (Exception exception)
                {
                    string message = "G-code save failed: " + exception.Message;
                    _gcodeStatus.Text = message;
                    MessageBox.Show(
                        this,
                        message,
                        "Save G-code",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }

        }
    }
}
