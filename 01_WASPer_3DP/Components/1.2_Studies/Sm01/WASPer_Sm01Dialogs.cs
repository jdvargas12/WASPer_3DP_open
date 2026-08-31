using System;
using System.Globalization;

using Eto.Drawing;
using Eto.Forms;
using Grasshopper;
using Rhino;
using Rhino.UI;

namespace WASPer_3DP.Components._1_2_Studies
{
    public sealed partial class wsp_Sm01_WASPer_Study_Manager
    {
        private static Control Sm01DialogOwner()
        {
            RhinoDoc document = Instances.ActiveCanvas?.Document?.RhinoDocument ?? RhinoDoc.ActiveDoc;
            return document == null ? null : RhinoEtoApp.MainWindowForDocument(document);
        }

        private static void ShowSm01Dialog(Dialog dialog)
        {
            Control owner = Sm01DialogOwner();
            if (owner != null)
                dialog.ShowModal(owner);
            else
                dialog.ShowModal();
        }

        private static void ShowSm01Error(string message, string title)
        {
            Control owner = Sm01DialogOwner();
            if (owner != null)
            {
                MessageBox.Show(
                    owner,
                    message,
                    title,
                    MessageBoxButtons.OK,
                    MessageBoxType.Error);
                return;
            }

            MessageBox.Show(message, title, MessageBoxType.Error);
        }

        private static void ShowSm01Warning(string message, string title)
        {
            Control owner = Sm01DialogOwner();
            if (owner != null)
            {
                MessageBox.Show(
                    owner,
                    message,
                    title,
                    MessageBoxButtons.OK,
                    MessageBoxType.Warning);
                return;
            }

            MessageBox.Show(message, title, MessageBoxType.Warning);
        }

        private static bool ConfirmSm01Warning(string message, string title)
        {
            Control owner = Sm01DialogOwner();
            DialogResult result = owner != null
                ? MessageBox.Show(
                    owner,
                    message,
                    title,
                    MessageBoxButtons.YesNo,
                    MessageBoxType.Warning,
                    MessageBoxDefaultButton.No)
                : MessageBox.Show(
                    message,
                    title,
                    MessageBoxButtons.YesNo,
                    MessageBoxType.Warning,
                    MessageBoxDefaultButton.No);
            return result == DialogResult.Yes;
        }

        private static string SelectSm01StudyFile(string initialDirectory)
        {
            using var dialog = new Eto.Forms.OpenFileDialog
            {
                CheckFileExists = true,
                Directory = string.IsNullOrWhiteSpace(initialDirectory)
                    ? null
                    : new Uri(initialDirectory),
                MultiSelect = false,
                Title = "Browse for a WASPer study"
            };
            dialog.Filters.Add(new FileFilter("WASPer study (study.json)", ".json"));
            dialog.Filters.Add(new FileFilter("All files", ".*"));
            return dialog.ShowDialog(Sm01DialogOwner()) == DialogResult.Ok
                ? dialog.FileName
                : null;
        }

        /// <summary>
        /// Editor for one chart's title and axis names. Blank fields preserve automatic labels or
        /// limits, matching the previous Study Manager behavior.
        /// </summary>
        private sealed class ChartLabelsDialog : Dialog
        {
            private static Size _preferredSize = Size.Empty;

            private readonly TextBox _title = new TextBox();
            private readonly TextBox _xTitle = new TextBox();
            private readonly TextBox _yTitle = new TextBox();
            private readonly TextBox _xMinimum = new TextBox();
            private readonly TextBox _xMaximum = new TextBox();
            private readonly TextBox _yMinimum = new TextBox();
            private readonly TextBox _yMaximum = new TextBox();
            private readonly bool _showRange;
            private bool _confirmed;

            private ChartLabelsDialog(
                string chartName,
                WasperChartLabels labels,
                bool showXTitle,
                bool showYTitle,
                bool showRange)
            {
                _showRange = showRange;
                Title = chartName + (showRange ? " labels and range" : " labels");
                Resizable = true;
                ShowInTaskbar = false;
                MinimumSize = new Size(360, 210);
                this.UseRhinoStyle();

                _title.Text = labels?.Title ?? string.Empty;
                _xTitle.Text = labels?.XTitle ?? string.Empty;
                _yTitle.Text = labels?.YTitle ?? string.Empty;
                _xMinimum.Text = FormatLimit(labels?.XMinimum);
                _xMaximum.Text = FormatLimit(labels?.XMaximum);
                _yMinimum.Text = FormatLimit(labels?.YMinimum);
                _yMaximum.Text = FormatLimit(labels?.YMaximum);

                var fields = new DynamicLayout
                {
                    Padding = new Padding(12, 12, 12, 4),
                    Spacing = new Size(10, 8)
                };
                AddRow(fields, "Title", _title);
                if (showXTitle)
                    AddRow(fields, "X axis", _xTitle);
                if (showYTitle)
                    AddRow(fields, "Y axis", _yTitle);
                if (showRange)
                {
                    AddRow(fields, "X range", Pair(_xMinimum, _xMaximum));
                    AddRow(fields, "Y range", Pair(_yMinimum, _yMaximum));
                }
                fields.AddSpace();

                var hint = new Label
                {
                    Text = showRange
                        ? "Leave a box empty to keep the automatic label or axis limit."
                        : "Leave a box empty to keep the automatic label.",
                    VerticalAlignment = VerticalAlignment.Center
                };

                var reset = CommandButton("Reset");
                var cancel = CommandButton("Cancel");
                var ok = CommandButton("OK");
                reset.Click += (sender, args) => ClearEditors();
                cancel.Click += (sender, args) => Close();
                ok.Click += (sender, args) =>
                {
                    _confirmed = true;
                    Close();
                };

                DefaultButton = ok;
                AbortButton = cancel;
                Content = DialogRoot(fields, hint, reset, cancel, ok);

                int rows = 1 + (showXTitle ? 1 : 0) + (showYTitle ? 1 : 0) +
                    (showRange ? 2 : 0);
                ClientSize = _preferredSize.IsEmpty
                    ? new Size(460, 108 + (rows * 36))
                    : _preferredSize;
                Closing += (sender, args) =>
                {
                    if (ClientSize.Width > 0 && ClientSize.Height > 0)
                        _preferredSize = ClientSize;
                };
            }

            private static void AddRow(DynamicLayout layout, string caption, Control editor)
            {
                layout.AddRow(
                    new Label
                    {
                        Text = caption,
                        VerticalAlignment = VerticalAlignment.Center,
                        Width = 72
                    },
                    editor);
            }

            private static Control Pair(Control minimum, Control maximum)
            {
                return new TableLayout
                {
                    Spacing = new Size(8, 0),
                    Rows =
                    {
                        new TableRow(
                            new TableCell(minimum, true),
                            new TableCell(maximum, true))
                    }
                };
            }

            private static Control DialogRoot(
                Control body,
                Control hint,
                Button reset,
                Button cancel,
                Button ok)
            {
                var footer = new TableLayout
                {
                    Padding = new Padding(12, 8),
                    Spacing = new Size(8, 0),
                    Rows =
                    {
                        new TableRow(
                            new TableCell(reset, false),
                            new TableCell(null, true),
                            new TableCell(cancel, false),
                            new TableCell(ok, false))
                    }
                };

                return new TableLayout
                {
                    Rows =
                    {
                        new TableRow(new TableCell(body, true)) { ScaleHeight = true },
                        new TableRow(new TableCell(
                            new Panel
                            {
                                Padding = new Padding(14, 0, 12, 0),
                                Content = hint
                            },
                            true)),
                        new TableRow(new TableCell(footer, true))
                    }
                };
            }

            private void ClearEditors()
            {
                foreach (TextBox box in new[]
                {
                    _title, _xTitle, _yTitle, _xMinimum, _xMaximum, _yMinimum, _yMaximum
                })
                {
                    box.Text = string.Empty;
                }
            }

            private static string FormatLimit(double? value) => value.HasValue
                ? value.Value.ToString("0.######", CultureInfo.InvariantCulture)
                : string.Empty;

            private static double? ParseLimit(string text)
            {
                return double.TryParse(
                    (text ?? string.Empty).Trim(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double value) && !double.IsNaN(value) && !double.IsInfinity(value)
                    ? value
                    : (double?)null;
            }

            private WasperChartLabels ReadResult() => new WasperChartLabels
            {
                Title = _title.Text?.Trim() ?? string.Empty,
                XTitle = _xTitle.Text?.Trim() ?? string.Empty,
                YTitle = _yTitle.Text?.Trim() ?? string.Empty,
                XMinimum = _showRange ? ParseLimit(_xMinimum.Text) : null,
                XMaximum = _showRange ? ParseLimit(_xMaximum.Text) : null,
                YMinimum = _showRange ? ParseLimit(_yMinimum.Text) : null,
                YMaximum = _showRange ? ParseLimit(_yMaximum.Text) : null
            };

            public static WasperChartLabels Show(
                string chartName,
                WasperChartLabels labels,
                bool showXTitle,
                bool showYTitle,
                bool showRange)
            {
                using var dialog = new ChartLabelsDialog(
                    chartName,
                    labels,
                    showXTitle,
                    showYTitle,
                    showRange);
                ShowSm01Dialog(dialog);
                return dialog._confirmed ? dialog.ReadResult() : null;
            }
        }

        private enum StudyCollisionChoice
        {
            Cancel,
            Override,
            Serialize
        }

        private sealed class StudyCollisionDialog : Dialog
        {
            private StudyCollisionChoice _choice = StudyCollisionChoice.Cancel;

            private StudyCollisionDialog(string studyName)
            {
                Title = "Existing WASPer Study";
                ClientSize = new Size(560, 190);
                Resizable = false;
                ShowInTaskbar = false;
                this.UseRhinoStyle();

                var cancel = ChoiceButton("Cancel", StudyCollisionChoice.Cancel);
                var overwrite = ChoiceButton("Override", StudyCollisionChoice.Override);
                var serialize = ChoiceButton("Serialize", StudyCollisionChoice.Serialize);
                DefaultButton = cancel;
                AbortButton = cancel;

                Content = new TableLayout
                {
                    Padding = new Padding(18),
                    Spacing = new Size(8, 16),
                    Rows =
                    {
                        new TableRow(new TableCell(
                            new Label
                            {
                                Text = $"There is already a WASPer Study with the name '{studyName}'." +
                                    Environment.NewLine + Environment.NewLine +
                                    "Do you want to cancel, override the existing study, or serialize " +
                                    "the new study with the next available suffix?",
                                Wrap = WrapMode.Word
                            },
                            true)) { ScaleHeight = true },
                        new TableRow(
                            new TableCell(null, true),
                            new TableCell(cancel, false),
                            new TableCell(overwrite, false),
                            new TableCell(serialize, false))
                    }
                };
            }

            private Button ChoiceButton(string text, StudyCollisionChoice choice)
            {
                Button button = CommandButton(text);
                button.Click += (sender, args) =>
                {
                    _choice = choice;
                    Close();
                };
                return button;
            }

            public static StudyCollisionChoice Show(string studyName)
            {
                using var dialog = new StudyCollisionDialog(studyName);
                ShowSm01Dialog(dialog);
                return dialog._choice;
            }
        }

        private sealed class RunStudyOptions
        {
            public bool IncludeGcode { get; set; }
            public bool IncludeSnapshots { get; set; }
            public bool IncludeXrPaths { get; set; }
        }

        private sealed class RunStudyOptionsDialog : Dialog
        {
            private readonly CheckBox _gcode;
            private readonly CheckBox _snapshots;
            private readonly CheckBox _xrPaths;
            private readonly Label _xrWarning;
            private bool _confirmed;

            private RunStudyOptionsDialog(bool gcodeEnabled, bool snapshotsEnabled, bool xrPathsEnabled)
            {
                Title = "Run Study";
                ClientSize = new Size(500, 280);
                Resizable = false;
                ShowInTaskbar = false;
                this.UseRhinoStyle();

                _gcode = new CheckBox
                {
                    Checked = gcodeEnabled,
                    Text = "G-code"
                };
                _snapshots = new CheckBox
                {
                    Checked = snapshotsEnabled,
                    Text = "Viewport snapshots"
                };
                _xrPaths = new CheckBox
                {
                    Checked = xrPathsEnabled,
                    Text = "wsp_paths (XR packages, for the Process Viewer / Dashboard)"
                };
                _xrWarning = new Label
                {
                    TextColor = Colors.DarkOrange,
                    Text = "This will significantly increase the study's file size (a full print " +
                        "path package per iteration) and add time to every iteration.",
                    Visible = xrPathsEnabled,
                    Wrap = WrapMode.Word
                };
                _xrPaths.CheckedChanged += (sender, args) =>
                    _xrWarning.Visible = _xrPaths.Checked == true;

                var content = new DynamicLayout
                {
                    Padding = new Padding(18, 16, 18, 8),
                    Spacing = new Size(8, 10)
                };
                content.AddRow(new Label
                {
                    Text = "Choose what to save with each iteration of this study run.",
                    Wrap = WrapMode.Word
                });
                content.AddRow(_gcode);
                content.AddRow(_snapshots);
                content.AddRow(_xrPaths);
                content.AddRow(new Panel
                {
                    Padding = new Padding(20, 0, 0, 0),
                    Content = _xrWarning
                });
                content.AddSpace();

                var run = CommandButton("Run study", 96);
                var cancel = CommandButton("Cancel");
                run.Click += (sender, args) =>
                {
                    _confirmed = true;
                    Close();
                };
                cancel.Click += (sender, args) => Close();
                DefaultButton = run;
                AbortButton = cancel;

                Content = new TableLayout
                {
                    Rows =
                    {
                        new TableRow(new TableCell(content, true)) { ScaleHeight = true },
                        new TableRow(
                            new TableCell(null, true),
                            new TableCell(cancel, false),
                            new TableCell(run, false))
                    },
                    Padding = new Padding(0, 0, 12, 12),
                    Spacing = new Size(8, 0)
                };
            }

            public static RunStudyOptions Show(
                bool gcodeEnabled,
                bool snapshotsEnabled,
                bool xrPathsEnabled)
            {
                using var dialog = new RunStudyOptionsDialog(
                    gcodeEnabled,
                    snapshotsEnabled,
                    xrPathsEnabled);
                ShowSm01Dialog(dialog);
                if (!dialog._confirmed)
                    return null;
                return new RunStudyOptions
                {
                    IncludeGcode = dialog._gcode.Checked == true,
                    IncludeSnapshots = dialog._snapshots.Checked == true,
                    IncludeXrPaths = dialog._xrPaths.Checked == true
                };
            }
        }

        private static Button CommandButton(string text, int minimumWidth = 88)
        {
            return new Button
            {
                Text = text,
                MinimumSize = new Size(minimumWidth, 30)
            };
        }
    }
}
