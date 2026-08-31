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
            private Control CreateDashboardPanel()
            {
                var introduction = new Label
                {
                    AutoSize = true,
                    MaximumSize = new Size(1200, 0),
                    Padding = new Padding(12, 4, 12, 10),
                    Text = "Charts read captured study iterations directly. Click a point to " +
                        "select that individual in every Dashboard chart and in the captured " +
                        "iterations table; the Grasshopper definition is not recomputed. " +
                        "Show in Grasshopper restores that sample's slider values, which does " +
                        "recompute the definition."
                };
                // Absolute row heights (not percentages) keep the four lower cards close to square
                // on a wide window; the panel scrolls vertically once they no longer fit.
                // Splitter strips sit between the cards as their own row and column, so dragging one
                // only has to move the neighbouring style rather than reflow the whole grid.
                var charts = new TableLayoutPanel
                {
                    AutoScroll = true,
                    Dock = DockStyle.Fill,
                    ColumnCount = 3,
                    RowCount = 5,
                    Padding = new Padding(10),
                };
                charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
                charts.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, DashboardSplitterSize));
                charts.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
                charts.RowStyles.Add(new RowStyle(SizeType.Absolute, DashboardParallelRowMinimum));
                charts.RowStyles.Add(new RowStyle(SizeType.Absolute, DashboardSplitterSize));
                charts.RowStyles.Add(new RowStyle(SizeType.Absolute, DashboardSquareRowMinimum));
                charts.RowStyles.Add(new RowStyle(SizeType.Absolute, DashboardSplitterSize));
                charts.RowStyles.Add(new RowStyle(SizeType.Absolute, DashboardSquareRowMinimum));

                Control topRow = CreateDashboardTopRow();
                charts.Controls.Add(topRow, 0, 0);
                charts.SetColumnSpan(topRow, 3);
                charts.Controls.Add(CreateDashboardHistoryCard(), 0, 2);
                charts.Controls.Add(CreateDashboardScatterCard(), 2, 2);
                charts.Controls.Add(CreateDashboardHeatmapCard(), 0, 4);
                charts.Controls.Add(CreateDashboardHistogramCard(), 2, 4);

                Control topSplitter = CreateDashboardRowSplitter(0);
                charts.Controls.Add(topSplitter, 0, 1);
                charts.SetColumnSpan(topSplitter, 3);
                Control middleSplitter = CreateDashboardRowSplitter(2);
                charts.Controls.Add(middleSplitter, 0, 3);
                charts.SetColumnSpan(middleSplitter, 3);
                // One column splitter per card row rather than a single row-spanning strip: the
                // middle row splitter already spans all three columns, so a strip spanning rows
                // 2-4 would collide with it in cell (1, 3). TableLayoutPanel resolves a collision by
                // silently relocating the control, which cascades and wrecks the whole grid.
                charts.Controls.Add(CreateDashboardColumnSplitter(), 1, 2);
                charts.Controls.Add(CreateDashboardColumnSplitter(), 1, 4);

                _dashboardCharts = charts;
                charts.ClientSizeChanged += (sender, args) => LayoutDashboardRows();

                var textSizeBar = new FlowLayoutPanel
                {
                    AutoSize = true,
                    Dock = DockStyle.Right,
                    FlowDirection = FlowDirection.LeftToRight,
                    Padding = new Padding(0, 4, 12, 0),
                    WrapContents = false
                };
                textSizeBar.Controls.Add(new Label
                {
                    AutoSize = true,
                    Margin = new Padding(2, 7, 4, 2),
                    Text = "Text size"
                });
                textSizeBar.Controls.Add(_dashboardTextSize);
                textSizeBar.Controls.Add(new Label
                {
                    AutoSize = true,
                    Margin = new Padding(2, 7, 2, 2),
                    Text = "%"
                });
                textSizeBar.Controls.Add(_dashboardReset);

                var studyBar = new FlowLayoutPanel
                {
                    AutoSize = true,
                    Dock = DockStyle.Left,
                    FlowDirection = FlowDirection.LeftToRight,
                    Padding = new Padding(10, 4, 0, 0),
                    WrapContents = false
                };
                studyBar.Controls.Add(new Label
                {
                    AutoSize = true,
                    Margin = new Padding(2, 7, 4, 2),
                    Text = "Study"
                });
                studyBar.Controls.Add(_dashboardStudyLibrary);

                var header = new Panel { Dock = DockStyle.Top, Height = 92 };
                header.Controls.Add(introduction);
                header.Controls.Add(textSizeBar);
                header.Controls.Add(studyBar);
                header.Controls.Add(_dashboardStatus);
                introduction.Dock = DockStyle.Bottom;

                var selectionBar = new Panel { Dock = DockStyle.Bottom, Height = 42 };
                _dashboardShowInGrasshopper.Dock = DockStyle.Right;
                _dashboardShowInGrasshopper.Width = 165;
                selectionBar.Controls.Add(_dashboardSelectionDetails);
                selectionBar.Controls.Add(_dashboardShowInGrasshopper);
                _dashboardSelectionDetails.Dock = DockStyle.Fill;

                var panel = new Panel { Dock = DockStyle.Fill };
                panel.Controls.Add(charts);
                panel.Controls.Add(selectionBar);
                panel.Controls.Add(header);
                return panel;
            }

            /// <summary>Thickness of the draggable strips between the cards.</summary>
            private const float DashboardSplitterSize = 7f;

            /// <summary>Row style indices of the three card rows, skipping the splitter rows.</summary>
            private static readonly int[] DashboardCardRows = { 0, 2, 4 };

            /// <summary>Smallest usable height for one of the four square lower cards.</summary>
            private const float DashboardSquareRowMinimum = 300f;

            /// <summary>Largest height a square lower card grows to on a very wide window.</summary>
            private const float DashboardSquareRowMaximum = 560f;

            /// <summary>Height bounds for the full-width parallel-coordinates row.</summary>
            private const float DashboardParallelRowMinimum = 320f;
            private const float DashboardParallelRowMaximum = 520f;

            /// <summary>
            /// Sizes the chart rows from the current column width so the four lower cards stay
            /// roughly square instead of stretching flat. Percent rows would divide whatever height
            /// the window happens to have; absolute rows plus the panel's AutoScroll give a real
            /// vertical scrollbar when the cards no longer fit. Once the user drags a splitter the
            /// layout is theirs, and this stops adjusting it.
            /// </summary>
            private void LayoutDashboardRows()
            {
                if (_dashboardCharts == null || _updatingDashboardRows || _dashboardCharts.IsDisposed)
                    return;
                if (_dashboardSettings.RowHeights?.Count >= DashboardCardRows.Length)
                {
                    ApplyDashboardLayout();
                    return;
                }
                int available = _dashboardCharts.ClientSize.Width -
                    _dashboardCharts.Padding.Horizontal -
                    SystemInformation.VerticalScrollBarWidth;
                float column = Math.Max(240f, available / 2f);
                float square = Math.Min(
                    DashboardSquareRowMaximum,
                    Math.Max(DashboardSquareRowMinimum, column * 0.92f));
                float parallel = Math.Min(
                    DashboardParallelRowMaximum,
                    Math.Max(DashboardParallelRowMinimum, available * 0.26f));
                if (_dashboardCharts.RowStyles.Count <= DashboardCardRows[2] ||
                    (NearlyEqual(_dashboardCharts.RowStyles[0].Height, parallel) &&
                        NearlyEqual(_dashboardCharts.RowStyles[2].Height, square) &&
                        NearlyEqual(_dashboardCharts.RowStyles[4].Height, square)))
                {
                    return;
                }
                SetDashboardRowHeights(parallel, square, square);
            }

            private void SetDashboardRowHeights(float parallel, float middle, float bottom)
            {
                _updatingDashboardRows = true;
                try
                {
                    _dashboardCharts.SuspendLayout();
                    float[] heights = { parallel, middle, bottom };
                    for (int index = 0; index < DashboardCardRows.Length; index++)
                    {
                        RowStyle style = _dashboardCharts.RowStyles[DashboardCardRows[index]];
                        style.SizeType = SizeType.Absolute;
                        style.Height = heights[index];
                    }
                    _dashboardCharts.ResumeLayout(true);
                }
                finally
                {
                    _updatingDashboardRows = false;
                }
            }

            /// <summary>Restores the row heights and column split the user last dragged to.</summary>
            private void ApplyDashboardLayout()
            {
                if (_dashboardCharts == null || _dashboardCharts.IsDisposed)
                    return;
                List<float> heights = _dashboardSettings.RowHeights;
                if (heights == null || heights.Count < DashboardCardRows.Length)
                {
                    // Nothing pinned - typically after Reset Dashboard. Restore the even column
                    // split and hand the rows back to the automatic sizing.
                    _dashboardLayoutPinned = false;
                    if (_dashboardCharts.ColumnStyles.Count >= 3)
                    {
                        _dashboardCharts.ColumnStyles[0].Width = 50f;
                        _dashboardCharts.ColumnStyles[2].Width = 50f;
                    }
                    LayoutDashboardRows();
                    return;
                }
                _dashboardLayoutPinned = true;
                // Clamped on the way in as well as on the way out, so a stored value from a bad
                // layout cannot resurrect an unusable grid on the next load.
                SetDashboardRowHeights(
                    ClampRowHeight(heights[0]),
                    ClampRowHeight(heights[1]),
                    ClampRowHeight(heights[2]));
                float ratio = _dashboardSettings.ColumnRatio;
                if (ratio <= 0f || _dashboardCharts.ColumnStyles.Count < 3)
                    return;
                _updatingDashboardRows = true;
                try
                {
                    ratio = Math.Max(0.15f, Math.Min(0.85f, ratio));
                    _dashboardCharts.ColumnStyles[0].Width = ratio * 100f;
                    _dashboardCharts.ColumnStyles[2].Width = (1f - ratio) * 100f;
                }
                finally
                {
                    _updatingDashboardRows = false;
                }
            }

            /// <summary>Smallest height a card row can be dragged to.</summary>
            private const float DashboardRowFloor = 160f;

            /// <summary>Largest height a card row can be dragged or restored to.</summary>
            private const float DashboardRowCeiling = 1400f;

            private static float ClampRowHeight(float height) =>
                float.IsNaN(height)
                    ? DashboardSquareRowMinimum
                    : Math.Max(DashboardRowFloor, Math.Min(DashboardRowCeiling, height));

            /// <summary>
            /// Horizontal drag strip resizing the card row above it. The row is absolute-sized, so
            /// the drag only has to add the vertical delta to that one style.
            /// </summary>
            private Control CreateDashboardRowSplitter(int rowStyleIndex)
            {
                var strip = new Panel
                {
                    Cursor = Cursors.HSplit,
                    Dock = DockStyle.Fill,
                    Margin = new Padding(6, 0, 6, 0)
                };
                strip.Paint += (sender, args) => PaintDashboardGrip(args, strip.ClientRectangle, true);
                strip.MouseDown += (sender, args) =>
                {
                    if (args.Button != MouseButtons.Left)
                        return;
                    _splitterRowIndex = rowStyleIndex;
                    _splitterOrigin = strip.PointToScreen(args.Location);
                    _splitterStart = _dashboardCharts.RowStyles[rowStyleIndex].Height;
                };
                strip.MouseMove += (sender, args) =>
                {
                    if (!_splitterRowIndex.HasValue)
                        return;
                    int delta = strip.PointToScreen(args.Location).Y - _splitterOrigin.Y;
                    RowStyle style = _dashboardCharts.RowStyles[_splitterRowIndex.Value];
                    style.SizeType = SizeType.Absolute;
                    style.Height = ClampRowHeight(_splitterStart + delta);
                };
                strip.MouseUp += (sender, args) =>
                {
                    if (!_splitterRowIndex.HasValue)
                        return;
                    _splitterRowIndex = null;
                    _dashboardLayoutPinned = true;
                    CaptureDashboardLayout();
                    RaiseDashboardSettingsChanged();
                };
                return strip;
            }

            /// <summary>
            /// Vertical drag strip resizing the two card columns. The columns are percentage-sized,
            /// so the drag converts the cursor position into a share of the available width - that
            /// keeps the split proportional when the window is resized afterwards.
            /// </summary>
            private Control CreateDashboardColumnSplitter()
            {
                var strip = new Panel
                {
                    Cursor = Cursors.VSplit,
                    Dock = DockStyle.Fill,
                    Margin = new Padding(0, 6, 0, 6)
                };
                strip.Paint += (sender, args) => PaintDashboardGrip(args, strip.ClientRectangle, false);
                strip.MouseDown += (sender, args) =>
                {
                    if (args.Button == MouseButtons.Left)
                        _draggingColumnSplitter = true;
                };
                strip.MouseMove += (sender, args) =>
                {
                    if (!_draggingColumnSplitter)
                        return;
                    Point inGrid = _dashboardCharts.PointToClient(strip.PointToScreen(args.Location));
                    float usable = _dashboardCharts.ClientSize.Width -
                        _dashboardCharts.Padding.Horizontal -
                        DashboardSplitterSize;
                    if (usable <= 1f)
                        return;
                    float ratio = Math.Max(
                        0.15f,
                        Math.Min(0.85f, (inGrid.X - _dashboardCharts.Padding.Left) / usable));
                    _dashboardCharts.ColumnStyles[0].Width = ratio * 100f;
                    _dashboardCharts.ColumnStyles[2].Width = (1f - ratio) * 100f;
                };
                strip.MouseUp += (sender, args) =>
                {
                    if (!_draggingColumnSplitter)
                        return;
                    _draggingColumnSplitter = false;
                    _dashboardLayoutPinned = true;
                    CaptureDashboardLayout();
                    RaiseDashboardSettingsChanged();
                };
                return strip;
            }

            /// <summary>Three dots, so a strip that is only a few pixels wide still reads as a grip.</summary>
            private static void PaintDashboardGrip(PaintEventArgs args, Rectangle bounds, bool horizontal)
            {
                using var brush = new SolidBrush(Color.FromArgb(170, 170, 170));
                float centerX = bounds.Left + (bounds.Width / 2f);
                float centerY = bounds.Top + (bounds.Height / 2f);
                for (int step = -1; step <= 1; step++)
                {
                    float x = horizontal ? centerX + (step * 9f) : centerX;
                    float y = horizontal ? centerY : centerY + (step * 9f);
                    args.Graphics.FillEllipse(brush, x - 1.5f, y - 1.5f, 3f, 3f);
                }
            }

            private void CaptureDashboardLayout()
            {
                // Only a real drag pins the layout. Capturing on every settings change would store
                // the automatically computed sizes and permanently disable the automatic sizing.
                if (!_dashboardLayoutPinned ||
                    _dashboardCharts == null ||
                    _dashboardCharts.RowStyles.Count <= DashboardCardRows[2])
                {
                    return;
                }
                _dashboardSettings.RowHeights = DashboardCardRows
                    .Select(index => _dashboardCharts.RowStyles[index].Height)
                    .ToList();
                float left = _dashboardCharts.ColumnStyles[0].Width;
                float right = _dashboardCharts.ColumnStyles[2].Width;
                _dashboardSettings.ColumnRatio = left + right > 0f ? left / (left + right) : 0.5f;
            }

            private static bool NearlyEqual(float left, float right) =>
                Math.Abs(left - right) < 0.5f;

            /// <summary>
            /// Compact per-card button opening the title/axis-name editor. Charts without axes
            /// (parallel coordinates, correlation matrix) only offer the title.
            /// </summary>
            private Button DashboardLabelsButton(
                string chartKey,
                string chartName,
                bool showXTitle,
                bool showYTitle,
                bool showRange = false)
            {
                var button = new Button
                {
                    AutoSize = true,
                    Margin = new Padding(12, 2, 2, 2),
                    Text = "Labels..."
                };
                button.Click += (sender, args) =>
                {
                    WasperChartLabels edited = ChartLabelsDialog.Show(
                        chartName,
                        _dashboardSettings.LabelsFor(chartKey),
                        showXTitle,
                        showYTitle,
                        showRange);
                    if (edited == null)
                        return;
                    _dashboardSettings.SetLabels(chartKey, edited);
                    RenderDashboardCharts();
                    RaiseDashboardSettingsChanged();
                };
                return button;
            }

            private Control CreateDashboardHeatmapCard()
            {
                var box = new GroupBox
                {
                    Dock = DockStyle.Fill,
                    Margin = new Padding(6),
                    MinimumSize = new Size(0, 200),
                    Padding = new Padding(8),
                    Text = "Correlation heatmap"
                };
                var toolbar = new FlowLayoutPanel
                {
                    AutoSize = true,
                    Dock = DockStyle.Top,
                    FlowDirection = FlowDirection.LeftToRight,
                    Padding = new Padding(2),
                    WrapContents = true
                };
                toolbar.Controls.Add(new Label
                {
                    AutoSize = true,
                    Margin = new Padding(2, 7, 5, 2),
                    Text = "Follows the Parallel coordinates group filter"
                });
                toolbar.Controls.Add(DashboardLabelsButton(
                    WasperDashboardSettings.HeatmapChart, "Correlation heatmap", false, false));

                // Fixed height rather than AutoSize: an AutoSize label's preferred-size wrap
                // calculation ignores the width Dock later stretches it to, so its computed
                // height can be too short for the wrapped text at the card's actual width.
                var description = new Label
                {
                    Dock = DockStyle.Top,
                    ForeColor = Color.FromArgb(90, 90, 90),
                    Height = 36,
                    Padding = new Padding(2, 0, 2, 2),
                    Text = "Pearson correlation between the shown inputs and KPIs, from -1 to " +
                        "+1 (diagonal = 1.00). See the color scale on the chart for the key."
                };
                _toolTip.SetToolTip(
                    _dashboardHeatmapChart,
                    "Each cell is the Pearson correlation coefficient between its row and column " +
                    "variable across every captured iteration. Darker red = stronger positive " +
                    "correlation, darker blue = stronger negative correlation, near-white = little " +
                    "or no linear relationship.");

                box.Controls.Add(_dashboardHeatmapChart);
                box.Controls.Add(description);
                box.Controls.Add(toolbar);
                return box;
            }

            /// <summary>
            /// Top row: the selected sample's snapshot on the left, the parallel chart filling the
            /// rest. The split lives inside the row rather than using the grid's column splitter,
            /// so the preview can be narrow without forcing the lower cards to the same proportion.
            /// </summary>
            private Control CreateDashboardTopRow()
            {
                var row = new Panel { Dock = DockStyle.Fill };
                Control parallelCard = CreateDashboardParallelCard();

                _dashboardSnapshotPanel = new Panel
                {
                    Dock = DockStyle.Left,
                    Width = DashboardSnapshotWidth(_dashboardSettings.SnapshotPanelWidth)
                };
                _snapshotWidthPinned = _dashboardSettings.SnapshotPanelWidth > 0;
                // Until the user drags it, the preview takes a quarter of the row and tracks the
                // window; a stored width means they chose one, and that wins from then on.
                row.SizeChanged += (sender, args) =>
                {
                    if (_snapshotWidthPinned || row.ClientSize.Width <= 0)
                        return;
                    _dashboardSnapshotPanel.Width =
                        DashboardSnapshotWidth(row.ClientSize.Width / 4);
                };
                var snapshotBox = new GroupBox
                {
                    Dock = DockStyle.Fill,
                    Margin = new Padding(6),
                    Padding = new Padding(8),
                    Text = "Selected sample"
                };
                _dashboardSnapshotImage.Controls.Add(_dashboardSnapshotEmpty);
                snapshotBox.Controls.Add(_dashboardSnapshotImage);
                snapshotBox.Controls.Add(_dashboardSnapshotStatus);
                snapshotBox.Controls.Add(_dashboardSnapshotCaption);
                _dashboardSnapshotPanel.Controls.Add(snapshotBox);

                var splitter = new Panel
                {
                    Cursor = Cursors.VSplit,
                    Dock = DockStyle.Left,
                    Width = (int)DashboardSplitterSize
                };
                splitter.Paint += (sender, args) =>
                    PaintDashboardGrip(args, splitter.ClientRectangle, false);
                splitter.MouseDown += (sender, args) =>
                {
                    if (args.Button != MouseButtons.Left)
                        return;
                    _draggingSnapshotSplitter = true;
                    _snapshotSplitterOrigin = splitter.PointToScreen(args.Location);
                    _snapshotSplitterStartWidth = _dashboardSnapshotPanel.Width;
                };
                splitter.MouseMove += (sender, args) =>
                {
                    if (!_draggingSnapshotSplitter)
                        return;
                    int delta = splitter.PointToScreen(args.Location).X - _snapshotSplitterOrigin.X;
                    _dashboardSnapshotPanel.Width =
                        DashboardSnapshotWidth(_snapshotSplitterStartWidth + delta);
                };
                splitter.MouseUp += (sender, args) =>
                {
                    if (!_draggingSnapshotSplitter)
                        return;
                    _draggingSnapshotSplitter = false;
                    _snapshotWidthPinned = true;
                    RaiseDashboardSettingsChanged();
                };

                // Dock resolves from the highest control index down, so the Fill card is added
                // first and the left-hand strips afterwards.
                row.Controls.Add(parallelCard);
                row.Controls.Add(splitter);
                row.Controls.Add(_dashboardSnapshotPanel);
                return row;
            }

            /// <summary>Keeps the preview usable without letting it crowd out the parallel chart.</summary>
            private static int DashboardSnapshotWidth(int requested) =>
                Math.Max(140, Math.Min(700, requested <= 0 ? 260 : requested));

            /// <summary>
            /// Shows the selected sample's captured snapshot. The file is copied into memory before
            /// the stream closes, because Image.FromFile keeps the PNG locked for the lifetime of
            /// the bitmap and would block the study from overwriting it on a later run.
            /// The resolved path is cached: this runs on every chart render, including every
            /// resize, and re-decoding the PNG each time would be needless work.
            /// </summary>
            private void UpdateDashboardSnapshot(WasperStudyIteration iteration)
            {
                if (iteration == null)
                {
                    ClearDashboardSnapshot();
                    _dashboardSnapshotCaption.Text = "Select a sample to preview its snapshot.";
                    _dashboardSnapshotStatus.Text = string.Empty;
                    return;
                }
                string name = string.IsNullOrWhiteSpace(iteration.SampleName)
                    ? $"Iteration {iteration.Index}"
                    : iteration.SampleName;
                _dashboardSnapshotCaption.Text = name;

                List<string> recorded = (iteration.SnapshotFiles ?? new List<string>())
                    .Where(file => !string.IsNullOrWhiteSpace(file))
                    .ToList();
                // Studies captured before the snapshot paths were recorded on the iteration still
                // have their PNGs on disk, so fall back to finding them by sample name.
                string path =
                    recorded.FirstOrDefault(File.Exists) ??
                    recorded.Select(ResolveMovedSnapshot).FirstOrDefault(file => file != null) ??
                    DiscoverSnapshot(iteration, name);
                if (path == null)
                {
                    ClearDashboardSnapshot();
                    _dashboardSnapshotStatus.Text = recorded.Count == 0
                        ? "No snapshot recorded or found on disk."
                        : "Snapshot file not found: " + Path.GetFileName(recorded[0]);
                    return;
                }
                if (string.Equals(_dashboardSnapshotPath, path, StringComparison.OrdinalIgnoreCase) &&
                    _dashboardSnapshotImage.Image != null)
                {
                    return;
                }
                ClearDashboardSnapshot();
                try
                {
                    using var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite);
                    using var loaded = Image.FromStream(stream);
                    _dashboardSnapshotImage.Image = new Bitmap(loaded);
                    _dashboardSnapshotPath = path;
                    _dashboardSnapshotEmpty.Visible = false;
                    _dashboardSnapshotStatus.Text = Path.GetFileName(path);
                }
                catch (Exception exception) when (
                    exception is IOException ||
                    exception is UnauthorizedAccessException ||
                    exception is ArgumentException ||
                    exception is OutOfMemoryException)
                {
                    // OutOfMemoryException is what GDI+ throws for a corrupt or non-image file.
                    _dashboardSnapshotStatus.Text = "Snapshot could not be read.";
                }
            }

            /// <summary>
            /// Looks for the recorded snapshot beside the study that is loaded now. Renaming a run
            /// moves its Snapshots folder, which would otherwise orphan every recorded path.
            /// </summary>
            private string ResolveMovedSnapshot(string recordedPath)
            {
                string fileName = Path.GetFileName(recordedPath ?? string.Empty);
                if (string.IsNullOrWhiteSpace(fileName))
                    return null;
                foreach (string folder in DashboardSnapshotFolders())
                {
                    string candidate = Path.Combine(folder, fileName);
                    if (File.Exists(candidate))
                        return candidate;
                }
                return null;
            }

            /// <summary>
            /// Finds a snapshot for an iteration that never recorded one. Names are matched against
            /// the sample name first, then against the iteration's own G-code base names, then
            /// against the trailing index the capture code appends when a name collides.
            /// </summary>
            private string DiscoverSnapshot(WasperStudyIteration iteration, string sampleName)
            {
                var candidates = new List<string>();
                if (!string.IsNullOrWhiteSpace(sampleName))
                    candidates.Add(sampleName);
                foreach (string gcode in iteration.GcodeFiles ?? new List<string>())
                {
                    string baseName = Path.GetFileNameWithoutExtension(gcode ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(baseName))
                        candidates.Add(baseName);
                }
                foreach (string folder in DashboardSnapshotFolders())
                {
                    foreach (string candidate in candidates)
                    {
                        string exact = Path.Combine(folder, candidate + ".png");
                        if (File.Exists(exact))
                            return exact;
                        string suffixed = SafeEnumerateFiles(folder, candidate + "_*.png")
                            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                            .FirstOrDefault();
                        if (suffixed != null)
                            return suffixed;
                    }
                    string byIndex = SafeEnumerateFiles(folder, $"*_{iteration.Index + 1:0000}.png")
                        .FirstOrDefault();
                    if (byIndex != null)
                        return byIndex;
                }
                return null;
            }

            private static IEnumerable<string> SafeEnumerateFiles(string folder, string pattern)
            {
                try
                {
                    return Directory.Exists(folder)
                        ? Directory.EnumerateFiles(folder, pattern)
                        : Enumerable.Empty<string>();
                }
                catch (Exception exception) when (
                    exception is IOException ||
                    exception is UnauthorizedAccessException ||
                    exception is ArgumentException)
                {
                    return Enumerable.Empty<string>();
                }
            }

            /// <summary>
            /// Folders worth searching: the study folder the component reports, plus the folders of
            /// any recorded snapshot that still resolves.
            /// </summary>
            private IEnumerable<string> DashboardSnapshotFolders()
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(_dashboardSnapshotFolder) &&
                    seen.Add(_dashboardSnapshotFolder))
                {
                    yield return _dashboardSnapshotFolder;
                }
                foreach (WasperStudyIteration iteration in Enumerable.Reverse(_dashboardIterations))
                {
                    foreach (string file in iteration?.SnapshotFiles ?? new List<string>())
                    {
                        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                            continue;
                        string folder = Path.GetDirectoryName(file);
                        if (!string.IsNullOrWhiteSpace(folder) && seen.Add(folder))
                            yield return folder;
                    }
                }
            }

            private void ClearDashboardSnapshot()
            {
                Image previous = _dashboardSnapshotImage.Image;
                _dashboardSnapshotImage.Image = null;
                previous?.Dispose();
                _dashboardSnapshotPath = string.Empty;
                _dashboardSnapshotEmpty.Visible = true;
            }

            /// <summary>Folder the component reports for the study currently on screen.</summary>
            public void UpdateDashboardSnapshotFolder(string folder)
            {
                if (string.Equals(_dashboardSnapshotFolder, folder, StringComparison.OrdinalIgnoreCase))
                    return;
                _dashboardSnapshotFolder = folder ?? string.Empty;
                // A different study may resolve the same selection to a different file.
                _dashboardSnapshotPath = string.Empty;
            }

            private Control CreateDashboardParallelCard()
            {
                var box = new GroupBox
                {
                    Dock = DockStyle.Fill,
                    Margin = new Padding(6),
                    MinimumSize = new Size(0, 260),
                    Padding = new Padding(8),
                    Text = "Parallel coordinates"
                };
                var toolbar = new FlowLayoutPanel
                {
                    AutoSize = true,
                    Dock = DockStyle.Top,
                    FlowDirection = FlowDirection.LeftToRight,
                    Padding = new Padding(2),
                    WrapContents = true
                };
                toolbar.Controls.Add(new Label
                {
                    AutoSize = true,
                    Margin = new Padding(2, 7, 5, 2),
                    Text = "Groups"
                });
                toolbar.Controls.Add(_dashboardGroupFilter);
                toolbar.Controls.Add(new Label
                {
                    AutoSize = true,
                    Margin = new Padding(10, 7, 2, 2),
                    Text = "(also drives the correlation heatmap)"
                });
                toolbar.Controls.Add(DashboardLabelsButton(
                    WasperDashboardSettings.ParallelChart, "Parallel coordinates", false, false));
                box.Controls.Add(_dashboardParallelChart);
                box.Controls.Add(toolbar);
                return box;
            }

            private Control CreateDashboardHistogramCard()
            {
                var box = new GroupBox
                {
                    Dock = DockStyle.Fill,
                    Margin = new Padding(6),
                    MinimumSize = new Size(0, 200),
                    Padding = new Padding(8),
                    Text = "Distribution"
                };
                if (_dashboardHistogramMode.Items.Count == 0)
                {
                    _dashboardHistogramMode.Items.Add(
                        new DashboardHistogramModeOption(WasperHistogramMode.Bars, "Bars"));
                    _dashboardHistogramMode.Items.Add(
                        new DashboardHistogramModeOption(WasperHistogramMode.Region, "Region"));
                    _dashboardHistogramMode.Items.Add(
                        new DashboardHistogramModeOption(WasperHistogramMode.Density, "Density"));
                    _dashboardHistogramMode.SelectedIndex = 0;
                }
                var toolbar = new FlowLayoutPanel
                {
                    AutoSize = true,
                    Dock = DockStyle.Top,
                    FlowDirection = FlowDirection.LeftToRight,
                    Padding = new Padding(2),
                    WrapContents = true
                };
                toolbar.Controls.Add(new Label
                {
                    AutoSize = true,
                    Margin = new Padding(2, 7, 5, 2),
                    Text = "Variable"
                });
                toolbar.Controls.Add(_dashboardHistogramVariable);
                toolbar.Controls.Add(new Label
                {
                    AutoSize = true,
                    Margin = new Padding(12, 7, 5, 2),
                    Text = "Style"
                });
                toolbar.Controls.Add(_dashboardHistogramMode);
                // One caption for whichever control governs the current mode, so a spinner that no
                // longer applies is never left on screen looking ignored.
                toolbar.Controls.Add(_dashboardHistogramParameterLabel);
                toolbar.Controls.Add(_dashboardHistogramBins);
                toolbar.Controls.Add(_dashboardHistogramBandwidth);
                toolbar.Controls.Add(DashboardLabelsButton(
                    WasperDashboardSettings.HistogramChart, "Distribution", true, true, true));
                box.Controls.Add(_dashboardHistogramChart);
                box.Controls.Add(toolbar);
                return box;
            }

            private Control CreateDashboardHistoryCard()
            {
                var box = new GroupBox
                {
                    Dock = DockStyle.Fill,
                    Margin = new Padding(6),
                    MinimumSize = new Size(0, 200),
                    Padding = new Padding(8),
                    Text = "KPI history"
                };
                var toolbar = new FlowLayoutPanel
                {
                    AutoSize = true,
                    Dock = DockStyle.Top,
                    FlowDirection = FlowDirection.LeftToRight,
                    Padding = new Padding(2)
                };
                toolbar.Controls.Add(new Label
                {
                    AutoSize = true,
                    Margin = new Padding(2, 7, 5, 2),
                    Text = "KPI"
                });
                toolbar.Controls.Add(_dashboardHistoryKpi);
                toolbar.Controls.Add(DashboardLabelsButton(
                    WasperDashboardSettings.HistoryChart, "KPI history", true, true, true));
                box.Controls.Add(_dashboardHistoryChart);
                box.Controls.Add(toolbar);
                return box;
            }

            private Control CreateDashboardScatterCard()
            {
                var box = new GroupBox
                {
                    Dock = DockStyle.Fill,
                    Margin = new Padding(6),
                    MinimumSize = new Size(0, 200),
                    Padding = new Padding(8),
                    Text = "X vs Y"
                };
                var toolbar = new FlowLayoutPanel
                {
                    AutoSize = true,
                    Dock = DockStyle.Top,
                    FlowDirection = FlowDirection.LeftToRight,
                    Padding = new Padding(2),
                    WrapContents = true
                };
                toolbar.Controls.Add(new Label
                {
                    AutoSize = true,
                    Margin = new Padding(2, 7, 5, 2),
                    Text = "X"
                });
                toolbar.Controls.Add(_dashboardScatterX);
                toolbar.Controls.Add(new Label
                {
                    AutoSize = true,
                    Margin = new Padding(12, 7, 5, 2),
                    Text = "Y"
                });
                toolbar.Controls.Add(_dashboardScatterY);
                if (_dashboardScatterStyle.Items.Count == 0)
                {
                    _dashboardScatterStyle.Items.Add(
                        new DashboardScatterStyleOption(DashboardScatterStyle.Markers, "Markers"));
                    _dashboardScatterStyle.Items.Add(
                        new DashboardScatterStyleOption(DashboardScatterStyle.Line, "Line"));
                    _dashboardScatterStyle.Items.Add(
                        new DashboardScatterStyleOption(DashboardScatterStyle.Both, "Line + markers"));
                    _dashboardScatterStyle.SelectedIndex = 0;
                }
                toolbar.Controls.Add(new Label
                {
                    AutoSize = true,
                    Margin = new Padding(12, 7, 5, 2),
                    Text = "Style"
                });
                toolbar.Controls.Add(_dashboardScatterStyle);
                toolbar.Controls.Add(new Label
                {
                    AutoSize = true,
                    Margin = new Padding(12, 7, 5, 2),
                    Text = "Colour by"
                });
                toolbar.Controls.Add(_dashboardScatterColor);
                toolbar.Controls.Add(_dashboardScatterNames);
                toolbar.Controls.Add(_dashboardScatterValues);
                toolbar.Controls.Add(DashboardLabelsButton(
                    WasperDashboardSettings.ScatterChart, "X vs Y", true, true, true));
                toolbar.Controls.Add(_dashboardResetLabelPositions);
                box.Controls.Add(_dashboardScatterChart);
                box.Controls.Add(toolbar);
                return box;
            }

            private static ComboBox DashboardComboBox()
            {
                return new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Margin = new Padding(2),
                    Width = 220
                };
            }

            private static PictureBox DashboardPictureBox()
            {
                return new PictureBox
                {
                    BackColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle,
                    Cursor = Cursors.Hand,
                    Dock = DockStyle.Fill,
                    SizeMode = PictureBoxSizeMode.Normal
                };
            }

            private void UpdateDashboardData(
                IReadOnlyList<WasperStudyParameter> parameters,
                IReadOnlyList<WasperStudyIteration> iterations)
            {
                _dashboardParameters = (parameters ?? Array.Empty<WasperStudyParameter>()).ToList();
                _dashboardIterations = (iterations ?? Array.Empty<WasperStudyIteration>()).ToList();
                _updatingDashboard = true;
                try
                {
                    List<DashboardVariableOption> kpis = DashboardKpiOptions();
                    // Both scatter axes accept any variable, so Input vs KPI, KPI vs KPI, and
                    // Input vs Input all work from the same two lists.
                    List<DashboardVariableOption> all = DashboardAllVariableOptions();
                    // A stored selection wins over the live one, so reopening the window or the
                    // Grasshopper file restores the charts the user last configured.
                    SetDashboardOptions(
                        _dashboardScatterX,
                        all,
                        OptionFor(all, _dashboardSettings.ScatterX) ??
                            _dashboardScatterX.SelectedItem as DashboardVariableOption);
                    SetDashboardOptions(
                        _dashboardScatterY,
                        all,
                        OptionFor(all, _dashboardSettings.ScatterY) ??
                            _dashboardScatterY.SelectedItem as DashboardVariableOption,
                        kpis.FirstOrDefault());
                    SetDashboardOptions(
                        _dashboardHistoryKpi,
                        kpis,
                        OptionFor(kpis, _dashboardSettings.HistoryKpi) ??
                            _dashboardHistoryKpi.SelectedItem as DashboardVariableOption);
                    SetDashboardOptions(
                        _dashboardHistogramVariable,
                        all,
                        OptionFor(all, _dashboardSettings.HistogramVariable) ??
                            _dashboardHistogramVariable.SelectedItem as DashboardVariableOption);
                    List<DashboardVariableOption> colorOptions = DashboardColorOptions(all);
                    SetDashboardOptions(
                        _dashboardScatterColor,
                        colorOptions,
                        OptionFor(colorOptions, _dashboardSettings.ScatterColor) ??
                            _dashboardScatterColor.SelectedItem as DashboardVariableOption);
                    RefreshDashboardGroupList();
                }
                finally
                {
                    _updatingDashboard = false;
                }

                if (_dashboardSelection.PrimaryId.HasValue &&
                    !_dashboardIterations.Any(iteration =>
                        iteration.Index == _dashboardSelection.PrimaryId.Value))
                {
                    _dashboardSelection.Clear();
                }
                RenderDashboardCharts();
            }

            /// <summary>
            /// Display name of the pseudo-group holding the study inputs. The Dashboard and the
            /// chart labels say "Inputs"; CSV/XLSX headers and persisted keys are unaffected.
            /// </summary>
            private const string ParameterGroupName = "Inputs";

            private List<DashboardVariableOption> DashboardParameterOptions()
            {
                var result = new List<DashboardVariableOption>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (WasperStudyParameter parameter in _dashboardParameters)
                {
                    if (parameter == null || string.IsNullOrWhiteSpace(parameter.Name) ||
                        !seen.Add(parameter.Name))
                    {
                        continue;
                    }
                    result.Add(new DashboardVariableOption(
                        parameter.Name, parameter.Name, string.Empty, ParameterGroupName, true));
                }
                foreach (string key in _dashboardIterations
                    .SelectMany(iteration => iteration.Parameters?.Keys ?? Enumerable.Empty<string>()))
                {
                    if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                    {
                        result.Add(new DashboardVariableOption(
                            key, key, string.Empty, ParameterGroupName, true));
                    }
                }
                return result;
            }

            private List<DashboardVariableOption> DashboardKpiOptions()
            {
                var result = new List<DashboardVariableOption>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (WasperKpi kpi in _dashboardIterations
                    .SelectMany(iteration => iteration.Kpis ?? new List<WasperKpi>()))
                {
                    if (kpi?.Value.HasValue != true || string.IsNullOrWhiteSpace(kpi.Key) ||
                        !seen.Add(kpi.Key))
                    {
                        continue;
                    }
                    string group = string.IsNullOrWhiteSpace(kpi.DisplayGroup)
                        ? "Other"
                        : kpi.DisplayGroup;
                    result.Add(new DashboardVariableOption(
                        kpi.Key,
                        group + ": " + (string.IsNullOrWhiteSpace(kpi.Label) ? kpi.Key : kpi.Label),
                        kpi.Unit,
                        group));
                }
                return result;
            }

            /// <summary>
            /// Every plottable variable, inputs first, shared by the scatter axes and the
            /// distribution chart.
            /// </summary>
            private List<DashboardVariableOption> DashboardAllVariableOptions()
            {
                var result = new List<DashboardVariableOption>();
                result.AddRange(DashboardParameterOptions());
                result.AddRange(DashboardKpiOptions());
                return result;
            }

            /// <summary>
            /// Group names offered by the Dashboard group filter: the parameter pseudo-group
            /// followed by the KPI display groups in first-seen order.
            /// </summary>
            private List<string> DashboardGroupNames()
            {
                var result = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (DashboardParameterOptions().Count > 0 && seen.Add(ParameterGroupName))
                    result.Add(ParameterGroupName);
                foreach (DashboardVariableOption option in DashboardKpiOptions())
                {
                    if (seen.Add(option.Group))
                        result.Add(option.Group);
                }
                return result;
            }

            private void RefreshDashboardGroupList()
            {
                List<string> groups = DashboardGroupNames();
                _updatingDashboardGroups = true;
                try
                {
                    _dashboardGroupList.BeginUpdate();
                    _dashboardGroupList.Items.Clear();
                    foreach (string group in groups)
                    {
                        _dashboardGroupList.Items.Add(
                            group,
                            !_dashboardHiddenGroups.Contains(group));
                    }
                    _dashboardGroupList.EndUpdate();
                }
                finally
                {
                    _updatingDashboardGroups = false;
                }
                FitGroupListWidth(groups);
                UpdateDashboardGroupFilterText(groups);
            }

            /// <summary>
            /// Sizes the group popup to its longest group name, so compound names such as
            /// "Thermal - Numerical (ISO)" stay readable next to their checkbox.
            /// </summary>
            private void FitGroupListWidth(IReadOnlyList<string> groups)
            {
                int widest = 0;
                foreach (string group in groups ?? Array.Empty<string>())
                {
                    int measured = TextRenderer
                        .MeasureText(group ?? string.Empty, _dashboardGroupList.Font)
                        .Width;
                    if (measured > widest)
                        widest = measured;
                }
                // Checkbox glyph, its gap, and the list's own scrollbar.
                widest += 28 + SystemInformation.VerticalScrollBarWidth;
                int limit = Math.Max(250, (Screen.PrimaryScreen?.WorkingArea.Width ?? 1280) - 200);
                _dashboardGroupList.Width = Math.Max(250, Math.Min(widest, limit));
                if (_dashboardGroupPopup?.Items.Count > 0 &&
                    _dashboardGroupPopup.Items[0] is ToolStripControlHost host)
                {
                    host.Size = _dashboardGroupList.Size;
                }
            }

            private void UpdateDashboardGroupFilterText(IReadOnlyList<string> groups)
            {
                if (groups == null || groups.Count == 0)
                {
                    _dashboardGroupFilter.Text = "No groups ▾";
                    _dashboardGroupFilter.Enabled = false;
                    return;
                }
                _dashboardGroupFilter.Enabled = true;
                List<string> visible = groups
                    .Where(group => !_dashboardHiddenGroups.Contains(group))
                    .ToList();
                _dashboardGroupFilter.Text = visible.Count == groups.Count
                    ? $"All groups ({groups.Count}) ▾"
                    : visible.Count == 0
                        ? "No groups shown ▾"
                        : visible.Count == 1
                            ? visible[0] + " ▾"
                            : $"{visible.Count} of {groups.Count} groups ▾";
            }

            private void DashboardGroupFilterClicked(object sender, EventArgs eventArgs)
            {
                if (_dashboardGroupList.Items.Count == 0)
                    return;
                if (_dashboardGroupPopup == null)
                {
                    var host = new ToolStripControlHost(_dashboardGroupList)
                    {
                        AutoSize = false,
                        Margin = Padding.Empty,
                        Padding = Padding.Empty,
                        Size = _dashboardGroupList.Size
                    };
                    _dashboardGroupPopup = new ToolStripDropDown
                    {
                        AutoClose = true,
                        DropShadowEnabled = true,
                        Padding = Padding.Empty
                    };
                    _dashboardGroupPopup.Items.Add(host);
                }
                _dashboardGroupPopup.Show(
                    _dashboardGroupFilter,
                    new Point(0, _dashboardGroupFilter.Height));
            }

            private void DashboardGroupItemChecked(object sender, ItemCheckEventArgs eventArgs)
            {
                if (_updatingDashboardGroups)
                    return;
                string group = _dashboardGroupList.Items[eventArgs.Index] as string;
                if (string.IsNullOrEmpty(group))
                    return;
                if (eventArgs.NewValue == CheckState.Checked)
                    _dashboardHiddenGroups.Remove(group);
                else
                    _dashboardHiddenGroups.Add(group);
                // ItemCheck fires before the item's state is committed, so redraw on the next
                // message rather than inside the event.
                Action refresh = () =>
                {
                    UpdateDashboardGroupFilterText(DashboardGroupNames());
                    RenderDashboardCharts();
                    RaiseDashboardSettingsChanged();
                };
                if (IsHandleCreated && !IsDisposed)
                    BeginInvoke(refresh);
                else
                    refresh();
            }

            private bool IsDashboardGroupVisible(DashboardVariableOption option) =>
                option != null && !_dashboardHiddenGroups.Contains(option.Group);

            // ---------------------------------------------------------------------------------
            // Persisted settings
            // ---------------------------------------------------------------------------------

            /// <summary>
            /// Pushes the stored Dashboard settings into the controls. Called after the study loads
            /// so the tab comes back exactly as the user left it.
            /// </summary>
            public void ApplyDashboardSettings(WasperDashboardSettings settings)
            {
                _dashboardSettings = settings ?? new WasperDashboardSettings();
                _updatingDashboard = true;
                try
                {
                    _dashboardHiddenGroups.Clear();
                    foreach (string group in _dashboardSettings.HiddenGroups ?? new List<string>())
                    {
                        if (!string.IsNullOrWhiteSpace(group))
                            _dashboardHiddenGroups.Add(group);
                    }
                    _dashboardTextSize.Value = ClampDecimal(
                        _dashboardSettings.TextSizePercent,
                        _dashboardTextSize.Minimum,
                        _dashboardTextSize.Maximum);
                    _dashboardHistogramBins.Value = ClampDecimal(
                        _dashboardSettings.HistogramBins,
                        _dashboardHistogramBins.Minimum,
                        _dashboardHistogramBins.Maximum);
                    _dashboardScatterNames.Checked = _dashboardSettings.ScatterShowNames;
                    _dashboardScatterValues.Checked = _dashboardSettings.ScatterShowValues;
                    _dashboardHistogramBandwidth.Value = ClampDecimal(
                        _dashboardSettings.HistogramBandwidthPercent,
                        _dashboardHistogramBandwidth.Minimum,
                        _dashboardHistogramBandwidth.Maximum);
                    SelectComboItem(
                        _dashboardHistogramMode,
                        item => item is DashboardHistogramModeOption option &&
                            string.Equals(
                                option.Mode.ToString(),
                                _dashboardSettings.HistogramMode,
                                StringComparison.OrdinalIgnoreCase));
                    DashboardHistogramModeChanged(_dashboardHistogramMode, EventArgs.Empty);
                    SelectComboItem(
                        _dashboardScatterStyle,
                        item => item is DashboardScatterStyleOption option &&
                            string.Equals(
                                option.Style.ToString(),
                                _dashboardSettings.ScatterStyle,
                                StringComparison.OrdinalIgnoreCase));
                }
                finally
                {
                    _updatingDashboard = false;
                }
                ApplyDashboardLayout();
                // Variable selections are restored inside UpdateDashboardData, which is the only
                // place that knows which variables the captured iterations actually provide.
                UpdateDashboardData(_dashboardParameters, _dashboardIterations);
            }

            private static decimal ClampDecimal(int value, decimal minimum, decimal maximum) =>
                Math.Max(minimum, Math.Min(maximum, value));

            private static void SelectComboItem(ComboBox comboBox, Func<object, bool> match)
            {
                foreach (object item in comboBox.Items)
                {
                    if (!match(item))
                        continue;
                    comboBox.SelectedItem = item;
                    return;
                }
            }

            /// <summary>Captures the current control state back into the persisted settings.</summary>
            private void CaptureDashboardSettings()
            {
                CaptureDashboardLayout();
                // Only a dragged width is stored, so the quarter-width default keeps tracking the
                // window until the user actually chooses a size.
                if (_snapshotWidthPinned && _dashboardSnapshotPanel != null)
                    _dashboardSettings.SnapshotPanelWidth = _dashboardSnapshotPanel.Width;
                _dashboardSettings.HiddenGroups = _dashboardHiddenGroups.OrderBy(
                    group => group,
                    StringComparer.OrdinalIgnoreCase).ToList();
                _dashboardSettings.TextSizePercent = (int)_dashboardTextSize.Value;
                _dashboardSettings.HistogramBins = (int)_dashboardHistogramBins.Value;
                _dashboardSettings.HistogramBandwidthPercent =
                    (int)_dashboardHistogramBandwidth.Value;
                _dashboardSettings.ScatterShowNames = _dashboardScatterNames.Checked;
                _dashboardSettings.ScatterShowValues = _dashboardScatterValues.Checked;
                _dashboardSettings.HistogramMode =
                    (_dashboardHistogramMode.SelectedItem as DashboardHistogramModeOption)
                        ?.Mode.ToString() ?? "Bars";
                _dashboardSettings.ScatterStyle =
                    (_dashboardScatterStyle.SelectedItem as DashboardScatterStyleOption)
                        ?.Style.ToString() ?? "Markers";
                _dashboardSettings.HistoryKpi = VariableRef(_dashboardHistoryKpi);
                _dashboardSettings.ScatterX = VariableRef(_dashboardScatterX);
                _dashboardSettings.ScatterY = VariableRef(_dashboardScatterY);
                _dashboardSettings.ScatterColor = VariableRef(_dashboardScatterColor);
                _dashboardSettings.HistogramVariable = VariableRef(_dashboardHistogramVariable);
            }

            private static WasperDashboardVariableRef VariableRef(ComboBox comboBox)
            {
                return comboBox.SelectedItem is DashboardVariableOption option
                    ? WasperDashboardVariableRef.Create(option.Key, option.IsParameter)
                    : null;
            }

            private DashboardVariableOption OptionFor(
                IReadOnlyList<DashboardVariableOption> options,
                WasperDashboardVariableRef reference)
            {
                return reference == null || reference.IsEmpty
                    ? null
                    : options.FirstOrDefault(option =>
                        option.IsParameter == reference.IsInput &&
                        string.Equals(option.Key, reference.Key, StringComparison.OrdinalIgnoreCase));
            }

            private void RaiseDashboardSettingsChanged()
            {
                if (_updatingDashboard)
                    return;
                CaptureDashboardSettings();
                DashboardSettingsChanged?.Invoke(_dashboardSettings);
            }

            private void DashboardResetClicked(object sender, EventArgs eventArgs)
            {
                _dashboardSettings = new WasperDashboardSettings();
                _dashboardHiddenGroups.Clear();
                ApplyDashboardSettings(_dashboardSettings);
                DashboardSettingsChanged?.Invoke(_dashboardSettings);
            }

            // ---------------------------------------------------------------------------------
            // Categorical marker colouring
            // ---------------------------------------------------------------------------------

            /// <summary>Distinct categories a colour variable can resolve to before pooling.</summary>
            private const int DashboardMaxCategories = 10;

            private static readonly Color[] DashboardCategoryPalette =
            {
                Color.FromArgb(31, 119, 180),
                Color.FromArgb(255, 127, 14),
                Color.FromArgb(44, 160, 44),
                Color.FromArgb(214, 39, 40),
                Color.FromArgb(148, 103, 189),
                Color.FromArgb(140, 86, 75),
                Color.FromArgb(227, 119, 194),
                Color.FromArgb(127, 127, 127),
                Color.FromArgb(188, 189, 34),
                Color.FromArgb(23, 190, 207)
            };

            /// <summary>
            /// Colour sources: every numeric input and KPI plus the text-valued KPIs, which never
            /// appear in the plotting lists because they have no numeric value but make the most
            /// natural categories (cell name, infill type, and similar).
            /// </summary>
            private List<DashboardVariableOption> DashboardColorOptions(
                IReadOnlyList<DashboardVariableOption> numericOptions)
            {
                var result = new List<DashboardVariableOption> { DashboardNoColorOption };
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DashboardVariableOption option in numericOptions ??
                    Array.Empty<DashboardVariableOption>())
                {
                    result.Add(option);
                    if (!option.IsParameter)
                        seen.Add(option.Key);
                }
                foreach (WasperKpi kpi in _dashboardIterations
                    .SelectMany(iteration => iteration.Kpis ?? new List<WasperKpi>()))
                {
                    if (kpi == null ||
                        string.IsNullOrWhiteSpace(kpi.Key) ||
                        string.IsNullOrWhiteSpace(kpi.TextValue) ||
                        !seen.Add(kpi.Key))
                    {
                        continue;
                    }
                    string group = string.IsNullOrWhiteSpace(kpi.DisplayGroup) ? "Other" : kpi.DisplayGroup;
                    result.Add(new DashboardVariableOption(
                        kpi.Key,
                        group + ": " + (string.IsNullOrWhiteSpace(kpi.Label) ? kpi.Key : kpi.Label),
                        string.Empty,
                        group));
                }
                return result;
            }

            /// <summary>
            /// Orders the points of one scatter series. When a line is drawn it must run left to
            /// right along X, since the capture order of the iterations says nothing about the
            /// relationship being plotted. Marker-only charts keep capture order, which costs
            /// nothing and preserves the existing hit-target sequence.
            /// </summary>
            private static List<WasperChartPoint> DashboardLinePoints(
                IEnumerable<WasperChartPoint> points,
                bool joined)
            {
                IEnumerable<WasperChartPoint> source = points ?? Enumerable.Empty<WasperChartPoint>();
                return joined
                    ? source.OrderBy(point => point.X).ThenBy(point => point.DataIndex).ToList()
                    : source.ToList();
            }

            /// <summary>One resolved colour category: its label, sort position, colour, and members.</summary>
            private sealed class DashboardCategory
            {
                public string Label { get; set; } = string.Empty;
                public double Sort { get; set; } = double.NaN;
                public Color Color { get; set; } = Color.SteelBlue;
                public bool HasSort => !double.IsNaN(Sort);
                public HashSet<int> IndividualIds { get; } = new HashSet<int>();
            }

            /// <summary>
            /// Resolves the category one iteration falls into for the chosen colour variable. A KPI
            /// carrying text is categorised by that text; everything else by its numeric value.
            /// </summary>
            private static bool TryGetDashboardCategory(
                WasperStudyIteration iteration,
                DashboardVariableOption option,
                out string label,
                out double sort)
            {
                label = string.Empty;
                sort = double.NaN;
                if (iteration == null || option == null)
                    return false;
                if (!option.IsParameter)
                {
                    WasperKpi kpi = iteration.Kpis?.FirstOrDefault(item =>
                        item != null &&
                        string.Equals(item.Key, option.Key, StringComparison.OrdinalIgnoreCase));
                    if (kpi != null && !string.IsNullOrWhiteSpace(kpi.TextValue))
                    {
                        label = kpi.TextValue.Trim();
                        return true;
                    }
                }
                if (!TryGetDashboardValue(iteration, option, out double value))
                    return false;
                sort = value;
                label = value.ToString("0.####", CultureInfo.InvariantCulture);
                return true;
            }

            /// <summary>
            /// Groups the captured iterations by the chosen colour variable and assigns each group a
            /// palette colour. Numeric categories order ascending and text categories alphabetically
            /// after them; anything past the palette limit pools into "Other" so the legend stays
            /// readable. An empty result means "do not group".
            /// </summary>
            private List<DashboardCategory> DashboardCategories(
                DashboardVariableOption option,
                out List<WasperChartLegendEntry> legend)
            {
                legend = new List<WasperChartLegendEntry>();
                var empty = new List<DashboardCategory>();
                if (option == null || string.IsNullOrEmpty(option.Key))
                    return empty;
                var categories = new Dictionary<string, DashboardCategory>(StringComparer.Ordinal);
                foreach (WasperStudyIteration iteration in _dashboardIterations)
                {
                    if (!TryGetDashboardCategory(iteration, option, out string label, out double sort))
                        continue;
                    if (!categories.TryGetValue(label, out DashboardCategory category))
                    {
                        category = new DashboardCategory { Label = label, Sort = sort };
                        categories[label] = category;
                    }
                    category.IndividualIds.Add(iteration.Index);
                }
                if (categories.Count == 0)
                    return empty;
                List<DashboardCategory> ordered = categories.Values
                    .OrderBy(category => category.HasSort ? 0 : 1)
                    .ThenBy(category => category.HasSort ? category.Sort : 0.0)
                    .ThenBy(category => category.Label, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                int named = Math.Min(ordered.Count, DashboardMaxCategories);
                bool pooled = ordered.Count > DashboardMaxCategories;
                if (pooled)
                    named = DashboardMaxCategories - 1;
                var result = new List<DashboardCategory>();
                for (int index = 0; index < named; index++)
                {
                    ordered[index].Color =
                        DashboardCategoryPalette[index % DashboardCategoryPalette.Length];
                    result.Add(ordered[index]);
                    legend.Add(new WasperChartLegendEntry
                    {
                        Color = ordered[index].Color,
                        Label = ordered[index].Label
                    });
                }
                if (!pooled)
                    return result;
                // Everything past the palette limit collapses into one pooled category, so it still
                // draws as a single group rather than silently sharing a colour with a named one.
                var other = new DashboardCategory
                {
                    Label = $"Other ({ordered.Count - named})",
                    Color = DashboardCategoryPalette[
                        (DashboardMaxCategories - 1) % DashboardCategoryPalette.Length]
                };
                for (int index = named; index < ordered.Count; index++)
                {
                    foreach (int id in ordered[index].IndividualIds)
                        other.IndividualIds.Add(id);
                }
                result.Add(other);
                legend.Add(new WasperChartLegendEntry
                {
                    Color = other.Color,
                    Label = other.Label
                });
                return result;
            }

            private static void SetDashboardOptions(
                ComboBox comboBox,
                IReadOnlyList<DashboardVariableOption> options,
                DashboardVariableOption previous,
                DashboardVariableOption fallback = null)
            {
                comboBox.BeginUpdate();
                comboBox.Items.Clear();
                foreach (DashboardVariableOption option in options)
                    comboBox.Items.Add(option);
                // An input and a KPI can share a key, so identity is key + kind.
                DashboardVariableOption match = SameVariable(options, previous);
                comboBox.SelectedItem =
                    match ?? SameVariable(options, fallback) ?? options.FirstOrDefault();
                comboBox.Enabled = options.Count > 0;
                comboBox.EndUpdate();
                FitDropDownWidth(comboBox);
            }

            /// <summary>
            /// Widens the open drop-down list to fit its longest entry while leaving the closed
            /// control narrow. Grouped KPI names such as "Thermal - Numerical (ISO): ..." are far
            /// wider than the toolbar can afford, and would otherwise be clipped mid-name.
            /// </summary>
            private static void FitDropDownWidth(ComboBox comboBox)
            {
                if (comboBox == null || comboBox.Items.Count == 0)
                    return;
                int widest = 0;
                foreach (object item in comboBox.Items)
                {
                    int measured = TextRenderer
                        .MeasureText(item?.ToString() ?? string.Empty, comboBox.Font)
                        .Width;
                    if (measured > widest)
                        widest = measured;
                }
                widest += SystemInformation.VerticalScrollBarWidth + 10;
                int limit = Math.Max(
                    comboBox.Width,
                    (Screen.PrimaryScreen?.WorkingArea.Width ?? 1280) - 120);
                comboBox.DropDownWidth = Math.Max(comboBox.Width, Math.Min(widest, limit));
            }

            private static DashboardVariableOption SameVariable(
                IReadOnlyList<DashboardVariableOption> options,
                DashboardVariableOption wanted)
            {
                return wanted == null
                    ? null
                    : options.FirstOrDefault(option =>
                        option.IsParameter == wanted.IsParameter &&
                        string.Equals(option.Key, wanted.Key, StringComparison.OrdinalIgnoreCase));
            }

            /// <summary>
            /// Bars and Region are binned; Density is bin-free and governed by a bandwidth. Only the
            /// control that actually applies is shown, and the caption follows it.
            /// </summary>
            private void DashboardHistogramModeChanged(object sender, EventArgs eventArgs)
            {
                bool density =
                    (_dashboardHistogramMode.SelectedItem as DashboardHistogramModeOption)?.Mode ==
                    WasperHistogramMode.Density;
                _dashboardHistogramParameterLabel.Text = density ? "Smoothing" : "Bins";
                _dashboardHistogramBins.Visible = !density;
                _dashboardHistogramBandwidth.Visible = density;
            }

            private void DashboardSelectionChanged(object sender, EventArgs eventArgs)
            {
                if (_updatingDashboard)
                    return;
                RenderDashboardCharts();
                RaiseDashboardSettingsChanged();
            }

            private void DashboardChartResized(object sender, EventArgs eventArgs)
            {
                if (_updatingDashboard || !IsHandleCreated || WindowState == FormWindowState.Minimized)
                    return;
                BeginInvoke((Action)RenderDashboardCharts);
            }

            private void RenderDashboardCharts()
            {
                if (_updatingDashboard || IsDisposed)
                    return;

                // The heatmap/parallel-coordinates renders scale with
                // variables x iterations and previously ran unconditionally
                // on every UpdateDashboardData call -- i.e. every Grasshopper
                // solve, regardless of which tab the user was actually
                // looking at. Skipping the render while the Dashboard tab
                // isn't visible (and catching up via _dashboardDirty in
                // TabControl.SelectedIndexChanged, see WASPer_Sm01ManagerForm.cs)
                // removes that cost from every other tab -- it was being
                // perceived as "switching tabs is slow" even though the real
                // cost was solve-triggered, not tab-switch-triggered.
                if (_tabs != null && _dashboardTab != null && _tabs.SelectedTab != _dashboardTab)
                {
                    _dashboardDirty = true;
                    return;
                }
                _dashboardDirty = false;

                _updatingDashboard = true;
                try
                {
                    RenderDashboardHistory();
                    RenderDashboardScatter();
                    RenderDashboardHeatmap();
                    RenderDashboardParallel();
                    RenderDashboardHistogram();
                    UpdateDashboardSelectionDetails();
                }
                finally
                {
                    _updatingDashboard = false;
                }
            }

            private void RenderDashboardHistory()
            {
                DashboardVariableOption selected =
                    _dashboardHistoryKpi.SelectedItem as DashboardVariableOption;
                List<WasperChartPoint> points = selected == null
                    ? new List<WasperChartPoint>()
                    : _dashboardIterations
                        .Select((iteration, index) => DashboardKpiPoint(
                            iteration,
                            index,
                            selected.Key,
                            iteration.Index))
                        .Where(point => point != null)
                        .ToList();
                var series = new WasperChartSeries
                {
                    Key = selected?.Key ?? string.Empty,
                    Label = selected?.Label ?? string.Empty,
                    Color = Color.FromArgb(31, 119, 180),
                    LineWidth = 2.0,
                    MarkerSize = 5.0,
                    Points = points
                };
                WasperChartRenderOptions options = DashboardRenderOptions(
                    _dashboardHistoryChart,
                    WasperDashboardSettings.HistoryChart,
                    "KPI history",
                    "Iteration",
                    selected?.DisplayName ?? "KPI");
                ReplaceDashboardResult(
                    _dashboardHistoryChart,
                    ref _dashboardHistoryResult,
                    _dashboardRenderer.Render(new[] { series }, options));
            }

            private void RenderDashboardScatter()
            {
                DashboardVariableOption xOption =
                    _dashboardScatterX.SelectedItem as DashboardVariableOption;
                DashboardVariableOption yOption =
                    _dashboardScatterY.SelectedItem as DashboardVariableOption;
                var points = new List<WasperChartPoint>();
                if (xOption != null && yOption != null)
                {
                    for (int index = 0; index < _dashboardIterations.Count; index++)
                    {
                        WasperStudyIteration iteration = _dashboardIterations[index];
                        if (!TryGetDashboardValue(iteration, xOption, out double x) ||
                            !TryGetDashboardValue(iteration, yOption, out double y))
                        {
                            continue;
                        }
                        WasperChartPoint point = DashboardPoint(iteration, index, x, y);
                        if (point != null)
                            points.Add(point);
                    }
                }
                DashboardVariableOption colorOption =
                    _dashboardScatterColor.SelectedItem as DashboardVariableOption;
                if (colorOption != null && colorOption.Key.Length == 0)
                    colorOption = null;
                List<DashboardCategory> categories = DashboardCategories(
                    colorOption,
                    out List<WasperChartLegendEntry> legend);
                DashboardScatterStyle style =
                    (_dashboardScatterStyle.SelectedItem as DashboardScatterStyleOption)?.Style ??
                    DashboardScatterStyle.Markers;
                double lineWidth = style == DashboardScatterStyle.Markers ? 0.0 : 1.6;
                double markerSize = style == DashboardScatterStyle.Line ? 0.0 : 6.0;

                // Colouring by a variable also groups by it: each category becomes its own series,
                // so a line connects the points within one group instead of zig-zagging across all
                // of them in capture order.
                var seriesList = new List<WasperChartSeries>();
                if (categories.Count > 0)
                {
                    foreach (DashboardCategory category in categories)
                    {
                        List<WasperChartPoint> members = DashboardLinePoints(
                            points.Where(point => category.IndividualIds.Contains(point.IndividualId)),
                            lineWidth > 0.0);
                        if (members.Count == 0)
                            continue;
                        seriesList.Add(new WasperChartSeries
                        {
                            Key = (yOption?.Key ?? string.Empty) + "|" + category.Label,
                            Label = category.Label,
                            Color = category.Color,
                            LineWidth = lineWidth,
                            MarkerSize = markerSize,
                            Points = members
                        });
                    }
                }
                else
                {
                    seriesList.Add(new WasperChartSeries
                    {
                        Key = yOption?.Key ?? string.Empty,
                        Label = yOption?.Label ?? string.Empty,
                        Color = Color.FromArgb(214, 92, 37),
                        LineWidth = lineWidth,
                        MarkerSize = markerSize,
                        Points = DashboardLinePoints(points, lineWidth > 0.0)
                    });
                }
                // Labels carry the name, the X/Y values, or both, so one placement pass covers
                // every combination of the two toggles.
                bool showNames = _dashboardScatterNames.Checked;
                bool showValues = _dashboardScatterValues.Checked;
                if (showValues)
                {
                    foreach (WasperChartPoint point in points)
                    {
                        string values =
                            $"({DashboardFormatValue(point.X)}, {DashboardFormatValue(point.Y)})";
                        point.Label = showNames && !string.IsNullOrWhiteSpace(point.Label)
                            ? point.Label + " " + values
                            : values;
                    }
                }
                WasperChartRenderOptions options = DashboardRenderOptions(
                    _dashboardScatterChart,
                    WasperDashboardSettings.ScatterChart,
                    "X vs Y",
                    xOption?.DisplayName ?? "X",
                    yOption?.DisplayName ?? "Y");
                options.LegendEntries = legend;
                options.ShowPointLabels = showNames || showValues;
                options.PointLabelOffsets = DashboardLabelOffsets();
                _dashboardResetLabelPositions.Enabled = options.PointLabelOffsets.Count > 0;
                ReplaceDashboardResult(
                    _dashboardScatterChart,
                    ref _dashboardScatterResult,
                    _dashboardRenderer.Render(seriesList, options));
            }

            private void RenderDashboardHeatmap()
            {
                WasperChartRenderOptions options = DashboardRenderOptions(
                    _dashboardHeatmapChart,
                    WasperDashboardSettings.HeatmapChart,
                    "Correlation heatmap",
                    string.Empty,
                    string.Empty);
                ReplaceDashboardResult(
                    _dashboardHeatmapChart,
                    ref _dashboardHeatmapResult,
                    _dashboardHeatmapRenderer.Render(DashboardDataset(), options));
            }

            private void RenderDashboardParallel()
            {
                WasperChartRenderOptions options = DashboardRenderOptions(
                    _dashboardParallelChart,
                    WasperDashboardSettings.ParallelChart,
                    "Parallel coordinates",
                    string.Empty,
                    string.Empty);
                ReplaceDashboardResult(
                    _dashboardParallelChart,
                    ref _dashboardParallelResult,
                    _dashboardParallelRenderer.Render(DashboardDataset(), options));
            }

            private void RenderDashboardHistogram()
            {
                DashboardVariableOption selected =
                    _dashboardHistogramVariable.SelectedItem as DashboardVariableOption;
                var mode = _dashboardHistogramMode.SelectedItem is DashboardHistogramModeOption option
                    ? option.Mode
                    : WasperHistogramMode.Bars;
                string variableKey = selected == null
                    ? string.Empty
                    : (selected.IsParameter ? "parameter:" : "kpi:") + selected.Key;
                WasperChartDataset dataset = selected == null
                    ? new WasperChartDataset()
                    : selected.IsParameter
                        ? DashboardDataset(new[] { selected }, Array.Empty<DashboardVariableOption>())
                        : DashboardDataset(Array.Empty<DashboardVariableOption>(), new[] { selected });
                var request = new WasperHistogramRequest
                {
                    Dataset = dataset,
                    VariableKey = variableKey,
                    BinCount = (int)_dashboardHistogramBins.Value,
                    Mode = mode,
                    BandwidthScale = (double)_dashboardHistogramBandwidth.Value / 100.0
                };
                WasperChartRenderOptions options = DashboardRenderOptions(
                    _dashboardHistogramChart,
                    WasperDashboardSettings.HistogramChart,
                    "Distribution",
                    selected?.DisplayName ?? "Variable",
                    mode == WasperHistogramMode.Density ? "Density" : "Count");
                ReplaceDashboardResult(
                    _dashboardHistogramChart,
                    ref _dashboardHistogramResult,
                    _dashboardHistogramRenderer.Render(request, options));
            }

            /// <summary>Maximum axes/cells the multivariate charts stay readable with.</summary>
            private const int DashboardMultivariateLimit = 14;

            /// <summary>
            /// Multivariate dataset shared by the parallel-coordinate and correlation charts.
            /// Only variables whose group is checked in the Dashboard group filter are included;
            /// parameters keep priority so the axes always start from the study inputs.
            /// </summary>
            private WasperChartDataset DashboardDataset()
            {
                // Only variables from checked groups take part, and a variable that never changes
                // is dropped: its correlation is undefined (the grey bands) and its parallel axis
                // is degenerate. Unchecking a group therefore shrinks both charts rather than
                // backfilling the freed slots from the groups that are still checked.
                List<DashboardVariableOption> parameters = DashboardParameterOptions()
                    .Where(IsDashboardGroupVisible)
                    .Where(DashboardVariableVaries)
                    .ToList();
                List<DashboardVariableOption> kpis = DashboardKpiOptions()
                    .Where(IsDashboardGroupVisible)
                    .Where(DashboardVariableVaries)
                    .ToList();
                int parameterLimit = kpis.Count > 0
                    ? Math.Min(DashboardMultivariateLimit / 2, parameters.Count)
                    : Math.Min(DashboardMultivariateLimit, parameters.Count);
                List<DashboardVariableOption> selectedParameters = parameters.Take(parameterLimit).ToList();
                List<DashboardVariableOption> selectedKpis = kpis
                    .Take(Math.Max(0, DashboardMultivariateLimit - selectedParameters.Count))
                    .ToList();
                return DashboardDataset(selectedParameters, selectedKpis);
            }

            /// <summary>
            /// True when a variable takes more than one distinct value across the captured
            /// iterations. Constants carry no correlation and would render as grey bands.
            /// </summary>
            private bool DashboardVariableVaries(DashboardVariableOption option)
            {
                double first = double.NaN;
                bool seen = false;
                foreach (WasperStudyIteration iteration in _dashboardIterations)
                {
                    if (!TryGetDashboardValue(iteration, option, out double value))
                        continue;
                    if (!seen)
                    {
                        first = value;
                        seen = true;
                        continue;
                    }
                    if (Math.Abs(value - first) > 1e-12)
                        return true;
                }
                return false;
            }

            private WasperChartDataset DashboardDataset(
                IReadOnlyList<DashboardVariableOption> selectedParameters,
                IReadOnlyList<DashboardVariableOption> selectedKpis)
            {
                selectedParameters ??= Array.Empty<DashboardVariableOption>();
                selectedKpis ??= Array.Empty<DashboardVariableOption>();
                var dataset = new WasperChartDataset();
                foreach (DashboardVariableOption option in selectedParameters)
                {
                    dataset.Variables.Add(new WasperChartVariable
                    {
                        Key = "parameter:" + option.Key,
                        Name = option.Label,
                        Unit = option.Unit,
                        Group = option.Group,
                        IsParameter = true
                    });
                }
                foreach (DashboardVariableOption option in selectedKpis)
                {
                    dataset.Variables.Add(new WasperChartVariable
                    {
                        Key = "kpi:" + option.Key,
                        Name = option.Label,
                        Unit = option.Unit,
                        Group = option.Group
                    });
                }
                foreach (WasperStudyIteration iteration in _dashboardIterations)
                {
                    var individual = new WasperChartIndividual
                    {
                        IndividualId = iteration.Index,
                        Name = string.IsNullOrWhiteSpace(iteration.SampleName)
                            ? $"Iteration {iteration.Index}"
                            : iteration.SampleName
                    };
                    foreach (DashboardVariableOption option in selectedParameters)
                    {
                        if (iteration.Parameters?.TryGetValue(option.Key, out double value) == true &&
                            IsFinite(value))
                        {
                            individual.Values["parameter:" + option.Key] = value;
                        }
                    }
                    foreach (DashboardVariableOption option in selectedKpis)
                    {
                        WasperKpi kpi = FindNumericKpi(iteration, option.Key);
                        if (kpi?.Value.HasValue == true)
                            individual.Values["kpi:" + option.Key] = kpi.Value.Value;
                    }
                    dataset.Individuals.Add(individual);
                }
                return dataset;
            }

            /// <summary>
            /// Builds the render options for one card, letting any user-typed title or axis name
            /// override the automatic one. A blank override keeps the automatic label.
            /// </summary>
            private WasperChartRenderOptions DashboardRenderOptions(
                PictureBox host,
                string chartKey,
                string title,
                string xTitle,
                string yTitle)
            {
                WasperChartLabels labels = _dashboardSettings.LabelsFor(chartKey);
                title = WasperChartLabels.Resolve(labels.Title, title);
                xTitle = WasperChartLabels.Resolve(labels.XTitle, xTitle);
                yTitle = WasperChartLabels.Resolve(labels.YTitle, yTitle);
                return new WasperChartRenderOptions
                {
                    XMinimum = labels.XMinimum,
                    XMaximum = labels.XMaximum,
                    YMinimum = labels.YMinimum,
                    YMaximum = labels.YMaximum,
                    Width = Math.Max(160, host.ClientSize.Width),
                    Height = Math.Max(140, host.ClientSize.Height),
                    Dpi = 96,
                    Axis = new WasperChartAxisSettings
                    {
                        XTitle = xTitle ?? string.Empty,
                        YTitle = yTitle ?? string.Empty,
                        XTicksInteger = false,
                        YTicksInteger = false,
                        XTitleSize = 9.0,
                        YTitleSize = 9.0,
                        XTextSize = 8.0,
                        YTextSize = 8.0
                    },
                    Layout = new WasperChartLayoutSettings
                    {
                        Title = title ?? string.Empty,
                        TitleSize = 11.0,
                        Dpi = 96,
                        ShowReferences = true
                    },
                    SelectedIndividualIds = new HashSet<int>(_dashboardSelection.SelectedIds),
                    TextScale = (double)_dashboardTextSize.Value / 100.0
                };
            }

            private static WasperChartPoint DashboardKpiPoint(
                WasperStudyIteration iteration,
                int dataIndex,
                string kpiKey,
                double x)
            {
                WasperKpi kpi = FindNumericKpi(iteration, kpiKey);
                return kpi?.Value.HasValue == true
                    ? DashboardPoint(iteration, dataIndex, x, kpi.Value.Value)
                    : null;
            }

            private static WasperChartPoint DashboardPoint(
                WasperStudyIteration iteration,
                int dataIndex,
                double x,
                double y)
            {
                if (iteration == null || !IsFinite(x) || !IsFinite(y))
                    return null;
                return new WasperChartPoint
                {
                    IndividualId = iteration.Index,
                    DataIndex = dataIndex,
                    Label = string.IsNullOrWhiteSpace(iteration.SampleName)
                        ? $"Iteration {iteration.Index}"
                        : iteration.SampleName,
                    X = x,
                    Y = y,
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["status"] = iteration.Status ?? string.Empty,
                        ["captured_utc"] = iteration.CapturedUtc.ToString("u")
                    }
                };
            }

            /// <summary>
            /// Reads one variable off an iteration regardless of whether it is a study input or a
            /// captured KPI, so either scatter axis can carry either kind.
            /// </summary>
            private static bool TryGetDashboardValue(
                WasperStudyIteration iteration,
                DashboardVariableOption option,
                out double value)
            {
                value = double.NaN;
                if (iteration == null || option == null)
                    return false;
                if (option.IsParameter)
                {
                    return iteration.Parameters != null &&
                        iteration.Parameters.TryGetValue(option.Key, out value) &&
                        IsFinite(value);
                }
                WasperKpi kpi = FindNumericKpi(iteration, option.Key);
                if (kpi?.Value.HasValue != true || !IsFinite(kpi.Value.Value))
                    return false;
                value = kpi.Value.Value;
                return true;
            }

            private static WasperKpi FindNumericKpi(WasperStudyIteration iteration, string key)
            {
                return iteration?.Kpis?.FirstOrDefault(kpi =>
                    kpi?.Value.HasValue == true &&
                    string.Equals(kpi.Key, key, StringComparison.OrdinalIgnoreCase));
            }

            /// <summary>Compact numeric text for on-chart labels and the selection strip.</summary>
            private static string DashboardFormatValue(double value)
            {
                double magnitude = Math.Abs(value);
                string format = magnitude >= 1000.0 || (magnitude > 0.0 && magnitude < 0.001)
                    ? "0.###E+0"
                    : magnitude >= 100.0
                        ? "0.#"
                        : magnitude >= 1.0
                            ? "0.###"
                            : "0.####";
                return value.ToString(format, CultureInfo.InvariantCulture);
            }

            private void DashboardShowInGrasshopperClicked(object sender, EventArgs eventArgs)
            {
                if (_dashboardSelection.PrimaryId.HasValue)
                    ShowIterationRequested?.Invoke(_dashboardSelection.PrimaryId.Value);
            }

            private static bool IsFinite(double value) =>
                !double.IsNaN(value) && !double.IsInfinity(value);

            private static void ReplaceDashboardResult(
                PictureBox host,
                ref WasperChartRenderResult current,
                WasperChartRenderResult replacement)
            {
                WasperChartRenderResult previous = current;
                current = replacement;
                host.Image = replacement?.Bitmap;
                previous?.Dispose();
            }

            private void DashboardChartClicked(object sender, MouseEventArgs eventArgs)
            {
                bool right = eventArgs.Button == MouseButtons.Right;
                if (eventArgs.Button != MouseButtons.Left && !right)
                    return;
                if (ReferenceEquals(sender, _dashboardScatterChart) &&
                    DashboardLabelDragConsumedClick())
                {
                    return;
                }
                bool selected = ReferenceEquals(sender, _dashboardHistogramChart)
                    ? DashboardHistogramClicked(eventArgs, right)
                    : DashboardPointClicked(sender, eventArgs, right);
                // Right-click selects first, so the menu always acts on what is under the cursor.
                if (right && selected && sender is Control host)
                    DashboardPointMenu().Show(host, eventArgs.Location);
            }

            private bool DashboardPointClicked(object sender, MouseEventArgs eventArgs, bool right)
            {
                WasperChartRenderResult result;
                if (ReferenceEquals(sender, _dashboardHistoryChart))
                    result = _dashboardHistoryResult;
                else if (ReferenceEquals(sender, _dashboardScatterChart))
                    result = _dashboardScatterResult;
                else
                    result = _dashboardParallelResult;
                WasperChartHitTarget hit = result?.HitTest(
                    eventArgs.Location,
                    10f,
                    target => target.Kind == WasperChartHitKind.Point ||
                        target.Kind == WasperChartHitKind.Segment);
                if (hit == null)
                    return false;
                // Ctrl extends the selection, but only on the left button: a right-click that
                // toggled the item off would leave the menu acting on something else.
                if (!right && (ModifierKeys & Keys.Control) == Keys.Control)
                    _dashboardSelection.Toggle(hit.IndividualId);
                else
                    _dashboardSelection.SelectOnly(hit.IndividualId);
                return true;
            }

            /// <summary>
            /// A histogram bin stands for every individual it contains, so a bin click selects
            /// them all; Ctrl adds the bin to the current linked selection.
            /// </summary>
            private bool DashboardHistogramClicked(MouseEventArgs eventArgs, bool right)
            {
                WasperChartHitTarget hit = _dashboardHistogramResult?.HitTest(
                    eventArgs.Location,
                    2f,
                    target => target.Kind == WasperChartHitKind.Cell);
                if (hit == null ||
                    !hit.Metadata.TryGetValue(
                        WasperHistogramRenderer.IndividualIdsMetadataKey,
                        out string packed))
                {
                    return false;
                }
                List<int> ids = packed
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(text => int.TryParse(text, out int id) ? id : (int?)null)
                    .Where(id => id.HasValue)
                    .Select(id => id.Value)
                    .ToList();
                if (ids.Count == 0)
                    return false;
                if (!right && (ModifierKeys & Keys.Control) == Keys.Control)
                    ids.AddRange(_dashboardSelection.SelectedIds);
                _dashboardSelection.SetSelection(ids, ids[0]);
                return true;
            }

            // ---------------------------------------------------------------------------------
            // Draggable scatter labels
            // ---------------------------------------------------------------------------------

            /// <summary>Stored label nudges, keyed by individual, in the form the renderer wants.</summary>
            private Dictionary<int, PointF> DashboardLabelOffsets()
            {
                var offsets = new Dictionary<int, PointF>();
                foreach (WasperLabelOffset offset in
                    _dashboardSettings.ScatterLabelOffsets ?? new List<WasperLabelOffset>())
                {
                    if (offset != null)
                        offsets[offset.IndividualId] = new PointF(offset.OffsetX, offset.OffsetY);
                }
                return offsets;
            }

            /// <summary>
            /// Begins a label drag. The grab point is remembered rather than the label origin, so
            /// the label does not jump to the cursor on the first mouse move.
            /// </summary>
            private void DashboardScatterMouseDown(object sender, MouseEventArgs eventArgs)
            {
                if (eventArgs.Button != MouseButtons.Left || _dashboardScatterResult == null)
                    return;
                WasperChartHitTarget hit = _dashboardScatterResult.HitTest(
                    eventArgs.Location,
                    0f,
                    target => target.Kind == WasperChartHitKind.Label);
                if (hit == null)
                    return;
                _draggingLabelId = hit.IndividualId;
                _labelDragOrigin = eventArgs.Location;
                Dictionary<int, PointF> offsets = DashboardLabelOffsets();
                _labelDragStart = offsets.TryGetValue(hit.IndividualId, out PointF existing)
                    ? existing
                    : new PointF(hit.Bounds.X - hit.Anchor.X, hit.Bounds.Y - hit.Anchor.Y);
                _dashboardScatterChart.Cursor = Cursors.SizeAll;
            }

            private void DashboardScatterMouseMove(object sender, MouseEventArgs eventArgs)
            {
                if (!_draggingLabelId.HasValue)
                {
                    // Hovering a label advertises that it can be moved.
                    bool overLabel = _dashboardScatterResult?.HitTest(
                        eventArgs.Location,
                        0f,
                        target => target.Kind == WasperChartHitKind.Label) != null;
                    _dashboardScatterChart.Cursor = overLabel ? Cursors.SizeAll : Cursors.Hand;
                    return;
                }
                _labelDragConsumedClick = true;
                SetDashboardLabelOffset(
                    _draggingLabelId.Value,
                    new PointF(
                        _labelDragStart.X + (eventArgs.X - _labelDragOrigin.X),
                        _labelDragStart.Y + (eventArgs.Y - _labelDragOrigin.Y)));
                RenderDashboardCharts();
            }

            private void DashboardScatterMouseUp(object sender, MouseEventArgs eventArgs)
            {
                if (!_draggingLabelId.HasValue)
                    return;
                _draggingLabelId = null;
                _dashboardScatterChart.Cursor = Cursors.Hand;
                RaiseDashboardSettingsChanged();
            }

            private void SetDashboardLabelOffset(int individualId, PointF offset)
            {
                _dashboardSettings.ScatterLabelOffsets ??= new List<WasperLabelOffset>();
                WasperLabelOffset entry = _dashboardSettings.ScatterLabelOffsets.FirstOrDefault(
                    item => item != null && item.IndividualId == individualId);
                if (entry == null)
                {
                    entry = new WasperLabelOffset { IndividualId = individualId };
                    _dashboardSettings.ScatterLabelOffsets.Add(entry);
                }
                entry.OffsetX = offset.X;
                entry.OffsetY = offset.Y;
            }

            private void DashboardResetLabelPositionsClicked(object sender, EventArgs eventArgs)
            {
                _dashboardSettings.ScatterLabelOffsets = new List<WasperLabelOffset>();
                RenderDashboardCharts();
                RaiseDashboardSettingsChanged();
            }

            /// <summary>
            /// A label drag must not also count as a click, or releasing the mouse would re-select
            /// whatever sits under the cursor.
            /// </summary>
            private bool DashboardLabelDragConsumedClick()
            {
                if (!_labelDragConsumedClick)
                    return false;
                _labelDragConsumedClick = false;
                return true;
            }

            /// <summary>
            /// Right-click menu shared by every chart and by the captured-iterations grid. Built
            /// lazily and reused, so all entry points offer exactly the same actions.
            /// </summary>
            private ContextMenuStrip DashboardPointMenu()
            {
                if (_dashboardPointMenu == null)
                {
                    _dashboardPointMenu = new ContextMenuStrip();
                    var show = new ToolStripMenuItem("Show in Grasshopper");
                    show.Click += DashboardShowInGrasshopperClicked;
                    var copy = new ToolStripMenuItem("Copy sample name");
                    copy.Click += DashboardCopySampleNameClicked;
                    _dashboardPointMenu.Items.Add(show);
                    _dashboardPointMenu.Items.Add(new ToolStripSeparator());
                    _dashboardPointMenu.Items.Add(copy);
                }
                bool hasSelection = _dashboardSelection.PrimaryId.HasValue;
                foreach (ToolStripItem item in _dashboardPointMenu.Items)
                {
                    if (!(item is ToolStripSeparator))
                        item.Enabled = hasSelection;
                }
                return _dashboardPointMenu;
            }

            private void DashboardCopySampleNameClicked(object sender, EventArgs eventArgs)
            {
                if (!_dashboardSelection.PrimaryId.HasValue)
                    return;
                WasperStudyIteration iteration = _dashboardIterations.FirstOrDefault(
                    item => item.Index == _dashboardSelection.PrimaryId.Value);
                if (iteration == null)
                    return;
                string name = string.IsNullOrWhiteSpace(iteration.SampleName)
                    ? $"Iteration {iteration.Index}"
                    : iteration.SampleName;
                try
                {
                    Clipboard.SetText(name);
                }
                catch (ExternalException)
                {
                    // The clipboard is held by another process; nothing useful to report here.
                }
            }

            /// <summary>
            /// Right-click on a captured-iterations row offers the same actions as the charts.
            /// </summary>
            private void IterationGridMouseDown(object sender, DataGridViewCellMouseEventArgs eventArgs)
            {
                if (eventArgs.Button != MouseButtons.Right || eventArgs.RowIndex < 0)
                    return;
                DataGridViewRow row = _iterationGrid.Rows[eventArgs.RowIndex];
                if (!int.TryParse(row.Cells[0].Value?.ToString(), out int individualId))
                    return;
                _iterationGrid.CurrentCell = row.Cells[Math.Max(0, eventArgs.ColumnIndex)];
                row.Selected = true;
                _dashboardSelection.SelectOnly(individualId);
                DashboardPointMenu().Show(Cursor.Position);
            }

            private void DashboardLinkedSelectionChanged(
                object sender,
                WasperChartSelectionChangedEventArgs eventArgs)
            {
                SelectIterationGridRow(eventArgs.PrimaryId);
                UpdateProcessViewerSelection();
                RenderDashboardCharts();
            }

            private void IterationGridCellClicked(object sender, DataGridViewCellEventArgs eventArgs)
            {
                if (eventArgs.RowIndex < 0 ||
                    !int.TryParse(
                        _iterationGrid.Rows[eventArgs.RowIndex].Cells[0].Value?.ToString(),
                        out int individualId))
                {
                    return;
                }
                _dashboardSelection.SelectOnly(individualId);
            }

            private void SelectIterationGridRow(int? individualId)
            {
                if (!individualId.HasValue)
                    return;
                foreach (DataGridViewRow row in _iterationGrid.Rows)
                {
                    if (int.TryParse(row.Cells[0].Value?.ToString(), out int rowId) &&
                        rowId == individualId.Value)
                    {
                        row.Selected = true;
                        if (row.Cells.Count > 0)
                            _iterationGrid.CurrentCell = row.Cells[0];
                        return;
                    }
                }
            }

            private void UpdateDashboardSelectionDetails()
            {
                if (!_dashboardSelection.PrimaryId.HasValue)
                {
                    _dashboardShowInGrasshopper.Enabled = false;
                    UpdateDashboardSnapshot(null);
                    _dashboardSelectionDetails.Text =
                        "Click a chart point to inspect and link an individual across Dashboard views.";
                    return;
                }
                _dashboardShowInGrasshopper.Enabled = true;
                WasperStudyIteration iteration = _dashboardIterations.FirstOrDefault(item =>
                    item.Index == _dashboardSelection.PrimaryId.Value);
                if (iteration == null)
                    return;
                UpdateDashboardSnapshot(iteration);
                string name = string.IsNullOrWhiteSpace(iteration.SampleName)
                    ? $"Iteration {iteration.Index}"
                    : iteration.SampleName;
                string parameterText = string.Join(
                    "; ",
                    (iteration.Parameters ?? new Dictionary<string, double>())
                        .Select(pair => $"{pair.Key}={pair.Value:0.####}"));
                // The two scatter axes are what the user just clicked on, so surface them first.
                string axisText = string.Empty;
                if (_dashboardScatterX.SelectedItem is DashboardVariableOption xSelected &&
                    _dashboardScatterY.SelectedItem is DashboardVariableOption ySelected &&
                    TryGetDashboardValue(iteration, xSelected, out double xValue) &&
                    TryGetDashboardValue(iteration, ySelected, out double yValue))
                {
                    axisText =
                        $"  |  {xSelected.Label} = {DashboardFormatValue(xValue)}" +
                        $"  |  {ySelected.Label} = {DashboardFormatValue(yValue)}";
                }
                _dashboardSelectionDetails.Text =
                    $"Selected {iteration.Index}: {name}  |  {iteration.Status}  |  " +
                    $"{iteration.Kpis?.Count ?? 0} KPIs" + axisText +
                    (parameterText.Length == 0 ? string.Empty : "  |  " + parameterText);
            }

            private void DisposeDashboardResults()
            {
                _dashboardHistoryChart.Image = null;
                _dashboardScatterChart.Image = null;
                _dashboardHeatmapChart.Image = null;
                _dashboardParallelChart.Image = null;
                _dashboardHistogramChart.Image = null;
                _dashboardHistoryResult?.Dispose();
                _dashboardScatterResult?.Dispose();
                _dashboardHeatmapResult?.Dispose();
                _dashboardParallelResult?.Dispose();
                _dashboardHistogramResult?.Dispose();
                _dashboardHistoryResult = null;
                _dashboardScatterResult = null;
                _dashboardHeatmapResult = null;
                _dashboardParallelResult = null;
                _dashboardHistogramResult = null;
                _dashboardGroupPopup?.Dispose();
                _dashboardGroupPopup = null;
                _dashboardPointMenu?.Dispose();
                _dashboardPointMenu = null;
                ClearDashboardSnapshot();
                if (_dashboardGroupList.Parent == null)
                    _dashboardGroupList.Dispose();
            }

            private sealed class FabricationUnitOption
            {
                public FabricationUnitOption(WasperFabricationUnitMode mode, string text)
                {
                    Mode = mode;
                    Text = text ?? string.Empty;
                }

                public WasperFabricationUnitMode Mode { get; }
                public string Text { get; }
                public override string ToString() => Text;
            }

            private sealed class DashboardVariableOption
            {
                public DashboardVariableOption(
                    string key,
                    string label,
                    string unit,
                    string group = null,
                    bool isParameter = false)
                {
                    Key = key ?? string.Empty;
                    Label = label ?? Key;
                    Unit = unit ?? string.Empty;
                    Group = string.IsNullOrWhiteSpace(group)
                        ? (isParameter ? ParameterGroupName : "Other")
                        : group.Trim();
                    IsParameter = isParameter;
                }

                public string Key { get; }
                public string Label { get; }
                public string Unit { get; }
                public string Group { get; }
                public bool IsParameter { get; }
                public string DisplayName => string.IsNullOrWhiteSpace(Unit)
                    ? Label
                    : $"{Label} [{Unit}]";
                public override string ToString() => DisplayName;
            }

            private enum DashboardScatterStyle
            {
                Markers,
                Line,
                Both
            }

            private sealed class DashboardScatterStyleOption
            {
                public DashboardScatterStyleOption(DashboardScatterStyle style, string text)
                {
                    Style = style;
                    Text = text ?? string.Empty;
                }

                public DashboardScatterStyle Style { get; }
                public string Text { get; }
                public override string ToString() => Text;
            }

            private sealed class DashboardHistogramModeOption
            {
                public DashboardHistogramModeOption(WasperHistogramMode mode, string text)
                {
                    Mode = mode;
                    Text = text ?? string.Empty;
                }

                public WasperHistogramMode Mode { get; }
                public string Text { get; }
                public override string ToString() => Text;
            }

        }
    }
}
