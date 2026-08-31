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
            // Narrowed from the old fixed 270 per the 2026-08-19 request ("the left side menu
            // don't occupy so much space when opening the KPIs") -- combined with the drag handle
            // below (CreateKpiWidthSplitter), this is now just the starting point, not a ceiling.
            private const int DefaultKpiGroupWidth = 220;
            private const int MinKpiGroupWidth = 160;
            private const int MaxKpiGroupWidth = 480;
            private const float KpiGroupSplitterSize = 6f;

            private void ConfigureFabricationUnitOptions(
                WasperFabricationUnitMode selectedMode,
                int? sourceUnitCode)
            {
                _updatingFabricationUnits = true;
                try
                {
                    string sourceUnit = sourceUnitCode switch
                    {
                        1 => "cm",
                        2 => "m",
                        0 => "mm",
                        _ => "mm fallback"
                    };
                    _fabricationUnits.BeginUpdate();
                    _fabricationUnits.Items.Clear();
                    _fabricationUnits.Items.Add(new FabricationUnitOption(
                        WasperFabricationUnitMode.Auto,
                        $"Auto (Gc03: {sourceUnit})"));
                    _fabricationUnits.Items.Add(new FabricationUnitOption(
                        WasperFabricationUnitMode.Millimetres,
                        "Millimetres (mm)"));
                    _fabricationUnits.Items.Add(new FabricationUnitOption(
                        WasperFabricationUnitMode.Centimetres,
                        "Centimetres (cm)"));
                    _fabricationUnits.Items.Add(new FabricationUnitOption(
                        WasperFabricationUnitMode.Metres,
                        "Metres (m)"));
                    _fabricationUnits.SelectedItem = _fabricationUnits.Items
                        .Cast<FabricationUnitOption>()
                        .First(option => option.Mode == selectedMode);
                    _fabricationUnits.EndUpdate();
                    _toolTip.SetToolTip(
                        _fabricationUnits,
                        "Auto follows the kpi_units code packed into wsp_path by Gc03. " +
                        "A manual selection converts fabrication KPI values and units for " +
                        "the Manager, study capture, Dashboard, exports, and reports.");
                }
                finally
                {
                    _updatingFabricationUnits = false;
                }
            }

            public void UpdateFabricationUnits(
                WasperFabricationUnitMode selectedMode,
                int? sourceUnitCode)
            {
                FabricationUnitOption current =
                    _fabricationUnits.SelectedItem as FabricationUnitOption;
                string expectedAutoText = sourceUnitCode switch
                {
                    1 => "Auto (Gc03: cm)",
                    2 => "Auto (Gc03: m)",
                    0 => "Auto (Gc03: mm)",
                    _ => "Auto (Gc03: mm fallback)"
                };
                FabricationUnitOption autoOption = _fabricationUnits.Items
                    .Cast<FabricationUnitOption>()
                    .FirstOrDefault(option => option.Mode == WasperFabricationUnitMode.Auto);
                if (current?.Mode != selectedMode || autoOption?.Text != expectedAutoText)
                    ConfigureFabricationUnitOptions(selectedMode, sourceUnitCode);
            }

            private void FabricationUnitsChanged(object sender, EventArgs eventArgs)
            {
                if (_updatingFabricationUnits ||
                    _fabricationUnits.SelectedItem is not FabricationUnitOption option)
                {
                    return;
                }
                FabricationUnitModeChanged?.Invoke(option.Mode);
            }

            public void UpdateKpis(
                WasperKpiSet set,
                IEnumerable<string> disabledKeys,
                IEnumerable<string> disabledGroups,
                IReadOnlyDictionary<Guid, bool> sourceStates,
                bool showValues)
            {
                _updatingKpiValueDisplay = true;
                _showValues.Checked = showValues;
                _showValues.Text = showValues ? "Hide values" : "Show values";
                _updatingKpiValueDisplay = false;
                var disabled = new HashSet<string>(
                    disabledKeys ?? Enumerable.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
                var disabledBlocks = new HashSet<string>(
                    disabledGroups ?? Enumerable.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
                List<WasperKpi> incomingItems = set?.Items ?? new List<WasperKpi>();
                string structureKey = BuildKpiStructureKey(incomingItems);
                if (string.Equals(_kpiStructureKey, structureKey, StringComparison.Ordinal) &&
                    _groups.Controls.Count > 0)
                {
                    UpdateExistingKpiControls(incomingItems, disabledBlocks, sourceStates);
                    UpdateStatus(set);
                    return;
                }
                Dictionary<string, bool> previousChecks = CurrentChecks();

                _groups.SuspendLayout();
                _groups.Controls.Clear();
                _items.Clear();
                _groupToggles.Clear();
                _sourceToggles.Clear();
                _sourceToggleNames.Clear();

                IEnumerable<IGrouping<string, WasperKpi>> groupedItems =
                    incomingItems
                    .GroupBy(kpi => kpi.DisplayGroup);

                foreach (IGrouping<string, WasperKpi> group in groupedItems)
                {
                    int boxWidth = _kpiGroupWidths.TryGetValue(group.Key, out int storedWidth)
                        ? storedWidth
                        : ScaleUi(DefaultKpiGroupWidth);
                    var box = new GroupBox
                    {
                        Text = "     " + group.Key,
                        Tag = group.Key,
                        Width = boxWidth,
                        Height = Math.Max(ScaleUi(180), _groups.ClientSize.Height - ScaleUi(32)),
                        Padding = new Padding(ScaleUi(8)),
                        AllowDrop = true,
                        Cursor = Cursors.SizeAll
                    };
                    var groupToggle = new CheckBox
                    {
                        AutoSize = true,
                        Checked = !disabledBlocks.Contains(group.Key),
                        Cursor = Cursors.Default,
                        Location = new Point(ScaleUi(8), 0),
                        TabStop = false
                    };
                    var list = new KpiCheckedListBox
                    {
                        CheckOnClick = true,
                        Dock = DockStyle.Fill,
                        HorizontalScrollbar = true,
                        AllowDrop = true
                    };
                    List<WasperKpi> records = group.ToList();
                    List<IGrouping<Guid, WasperKpi>> sources = records
                        .Where(item => item.SourceInstanceId != Guid.Empty)
                        .GroupBy(item => item.SourceInstanceId)
                        .ToList();
                    foreach (WasperKpi item in records)
                    {
                        int index = list.Items.Add(KpiItemText(item));
                        bool isChecked = previousChecks.TryGetValue(item.Key, out bool prior)
                            ? prior
                            : !disabled.Contains(item.Key);
                        list.SetItemChecked(index, isChecked);
                    }
                    _items[list] = records;
                    list.Records = records;
                    ConfigureKpiValueDisplay(list);
                    list.MouseMove += ShowItemToolTip;
                    box.MouseDown += BeginGroupDrag;
                    box.DragEnter += GroupDragEnter;
                    box.DragOver += GroupDragOver;
                    box.DragDrop += GroupDragDrop;
                    list.DragEnter += GroupDragEnter;
                    list.DragOver += GroupDragOver;
                    list.DragDrop += GroupDragDrop;
                    _toolTip.SetToolTip(box, "Drag this title to reorder KPI groups.");
                    _toolTip.SetToolTip(
                        groupToggle,
                        "Enable or disable this entire KPI group without changing its individual selections.");
                    groupToggle.CheckedChanged += (sender, args) =>
                    {
                        if (!_updatingKpiControls)
                            GroupEnabledChanged?.Invoke(group.Key, groupToggle.Checked);
                    };
                    _groupToggles[group.Key] = groupToggle;
                    var content = new TableLayoutPanel
                    {
                        ColumnCount = 1,
                        Dock = DockStyle.Fill,
                        Margin = Padding.Empty,
                        Padding = Padding.Empty,
                        RowCount = sources.Count > 0 ? 2 : 1
                    };
                    content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                    if (sources.Count > 0)
                    {
                        int sourceHeight = Math.Max(
                            ScaleUi(31),
                            sources.Count * ScaleUi(29));
                        content.RowStyles.Add(new RowStyle(SizeType.Absolute, sourceHeight));
                        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                        var sourcePanel = new TableLayoutPanel
                        {
                            AutoScroll = false,
                            ColumnCount = 1,
                            Dock = DockStyle.Fill,
                            GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
                            Padding = new Padding(2, 0, 2, 0),
                            RowCount = sources.Count
                        };
                        sourcePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                        int sourceRow = 0;
                        foreach (IGrouping<Guid, WasperKpi> sourceGroup in sources)
                        {
                            WasperKpi first = sourceGroup.First();
                            string sourceName = string.IsNullOrWhiteSpace(first.SourceNickname)
                                ? first.Source
                                : first.SourceNickname;
                            if (string.IsNullOrWhiteSpace(sourceName))
                                sourceName = "KPI source";
                            bool sourceEnabled = sourceStates == null ||
                                !sourceStates.TryGetValue(sourceGroup.Key, out bool isEnabled) ||
                                isEnabled;
                            var sourceButton = new SourceActionButton
                            {
                                AutoSize = false,
                                Cursor = Cursors.Default,
                                Dock = DockStyle.Fill,
                                FlatStyle = FlatStyle.Flat,
                                Height = ScaleUi(27),
                                Margin = new Padding(0, ScaleUi(1), 0, ScaleUi(1)),
                                TextAlign = ContentAlignment.MiddleLeft,
                                UseVisualStyleBackColor = false
                            };
                            sourceButton.SourceEnabled = sourceEnabled;
                            Guid sourceId = sourceGroup.Key;
                            StyleSourceButton(sourceButton, sourceName, sourceEnabled);
                            _toolTip.SetToolTip(
                                sourceButton,
                                "Enable or disable the exact Grasshopper component that issued " +
                                "these KPIs. Disabled sources retain their last valid cached values.");
                            sourceButton.Click += (sender, args) =>
                            {
                                sourceButton.SourceEnabled = !sourceButton.SourceEnabled;
                                StyleSourceButton(
                                    sourceButton,
                                    sourceName,
                                    sourceButton.SourceEnabled);
                                if (!_updatingKpiControls)
                                    SourceEnabledChanged?.Invoke(
                                        sourceId,
                                        sourceButton.SourceEnabled);
                            };
                            if (!_sourceToggles.TryGetValue(
                                sourceId,
                                out List<SourceActionButton> buttons))
                            {
                                buttons = new List<SourceActionButton>();
                                _sourceToggles[sourceId] = buttons;
                                _sourceToggleNames[sourceId] = sourceName;
                            }
                            buttons.Add(sourceButton);
                            sourcePanel.RowStyles.Add(
                                new RowStyle(SizeType.Absolute, ScaleUi(29)));
                            sourcePanel.Controls.Add(sourceButton, 0, sourceRow);
                            sourceRow++;
                        }
                        content.Controls.Add(sourcePanel, 0, 0);
                        content.Controls.Add(list, 0, 1);
                    }
                    else
                    {
                        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                        content.Controls.Add(list, 0, 0);
                    }
                    box.Controls.Add(content);
                    box.Controls.Add(groupToggle);
                    // Added last so it docks first (outermost, at the box's right edge) --
                    // same "Dock resolves from the highest control index down" ordering the
                    // Dashboard's own splitters rely on (see WASPer_Sm01DashboardTab.cs).
                    box.Controls.Add(CreateKpiWidthSplitter(box, group.Key));
                    _groups.Controls.Add(box);
                }

                _kpiStructureKey = structureKey;
                _groups.ResumeLayout();
                ResizeKpiGroupBoxes();
                UpdateStatus(set);
            }

            private static string BuildKpiStructureKey(IEnumerable<WasperKpi> items)
            {
                return string.Join(
                    "\u001f",
                    (items ?? Enumerable.Empty<WasperKpi>())
                        .Select(item => string.Join(
                            "\u001e",
                            item?.DisplayGroup ?? string.Empty,
                            item?.Key ?? string.Empty,
                            item?.SourceInstanceId.ToString() ?? string.Empty))
                        .OrderBy(token => token, StringComparer.Ordinal));
            }

            private void UpdateExistingKpiControls(
                IList<WasperKpi> incomingItems,
                ISet<string> disabledBlocks,
                IReadOnlyDictionary<Guid, bool> sourceStates)
            {
                var byKey = (incomingItems ?? Array.Empty<WasperKpi>())
                    .Where(item => item != null)
                    .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        group => group.Key,
                        group => group.First(),
                        StringComparer.OrdinalIgnoreCase);

                _updatingKpiControls = true;
                try
                {
                    foreach (KeyValuePair<CheckedListBox, List<WasperKpi>> pair in _items)
                    {
                        for (int index = 0; index < pair.Value.Count; index++)
                        {
                            WasperKpi previous = pair.Value[index];
                            if (!byKey.TryGetValue(previous.Key, out WasperKpi current))
                                continue;
                            pair.Value[index] = current;
                            string text = KpiItemText(current);
                            if (!string.Equals(Convert.ToString(pair.Key.Items[index]), text, StringComparison.Ordinal))
                                pair.Key.Items[index] = text;
                        }
                        ConfigureKpiValueDisplay(pair.Key);
                    }

                    foreach (KeyValuePair<string, CheckBox> pair in _groupToggles)
                        pair.Value.Checked = !disabledBlocks.Contains(pair.Key);

                    foreach (KeyValuePair<Guid, List<SourceActionButton>> pair in _sourceToggles)
                    {
                        bool enabled = sourceStates == null ||
                            !sourceStates.TryGetValue(pair.Key, out bool sourceEnabled) ||
                            sourceEnabled;
                        string sourceName = _sourceToggleNames.TryGetValue(pair.Key, out string name)
                            ? name
                            : "KPI source";
                        foreach (SourceActionButton button in pair.Value)
                        {
                            button.SourceEnabled = enabled;
                            StyleSourceButton(button, sourceName, enabled);
                        }
                    }
                }
                finally
                {
                    _updatingKpiControls = false;
                }
            }

            private static string KpiItemText(WasperKpi item)
            {
                string unit = string.IsNullOrWhiteSpace(item?.Unit)
                    ? string.Empty
                    : $" [{item.Unit}]";
                return $"{item?.Label}{unit}";
            }

            private static void StyleSourceButton(
                SourceActionButton button,
                string sourceName,
                bool enabled)
            {
                if (button == null)
                    return;
                if (enabled)
                {
                    button.Text = "Disable: " + sourceName;
                    button.BackColor = Color.FromArgb(255, 225, 214);
                    button.ForeColor = Color.FromArgb(145, 45, 24);
                    button.FlatAppearance.BorderColor = Color.FromArgb(205, 92, 64);
                }
                else
                {
                    button.Text = "Enable: " + sourceName;
                    button.BackColor = Color.FromArgb(218, 242, 222);
                    button.ForeColor = Color.FromArgb(32, 105, 53);
                    button.FlatAppearance.BorderColor = Color.FromArgb(78, 151, 91);
                }
                button.Invalidate();
            }

            private void ResizeKpiGroupBoxes()
            {
                int availableHeight = Math.Max(
                    ScaleUi(180),
                    _groups.ClientSize.Height - ScaleUi(22));
                foreach (GroupBox box in _groups.Controls.OfType<GroupBox>())
                    box.Height = availableHeight;
            }

            private int ScaleUi(int logicalPixels) =>
                Math.Max(1, (int)Math.Round(logicalPixels * (CurrentDpi / 96.0)));

            /// <summary>
            /// Drag handle at a KPI group box's right edge, letting each group be narrowed or
            /// widened independently -- added 2026-08-19 so the group boxes (previously a fixed
            /// 270px each) stop dominating the KPIs tab. Mirrors the Dashboard's own pixel-width
            /// splitter (_dashboardSnapshotPanel's drag handle in WASPer_Sm01DashboardTab.cs)
            /// rather than inventing a new interaction: same Panel-based strip, same
            /// PaintDashboardGrip dot rendering, same MouseDown-origin/MouseMove-delta/MouseUp-
            /// commit shape. The chosen width is kept in _kpiGroupWidths (session-only, keyed by
            /// DisplayGroup) so re-solving or re-checking KPIs doesn't reset a size the user picked.
            /// </summary>
            private Control CreateKpiWidthSplitter(GroupBox box, string groupKey)
            {
                var strip = new Panel
                {
                    Cursor = Cursors.VSplit,
                    Dock = DockStyle.Right,
                    Width = (int)KpiGroupSplitterSize
                };
                strip.Paint += (sender, args) =>
                    PaintDashboardGrip(args, strip.ClientRectangle, false);
                strip.MouseDown += (sender, args) =>
                {
                    if (args.Button != MouseButtons.Left)
                        return;
                    _draggingKpiWidthBox = box;
                    _kpiWidthSplitterOrigin = strip.PointToScreen(args.Location);
                    _kpiWidthSplitterStartWidth = box.Width;
                };
                strip.MouseMove += (sender, args) =>
                {
                    if (_draggingKpiWidthBox != box)
                        return;
                    int delta = strip.PointToScreen(args.Location).X - _kpiWidthSplitterOrigin.X;
                    box.Width = ClampKpiGroupWidth(_kpiWidthSplitterStartWidth + delta);
                };
                strip.MouseUp += (sender, args) =>
                {
                    if (_draggingKpiWidthBox != box)
                        return;
                    _draggingKpiWidthBox = null;
                    _kpiGroupWidths[groupKey] = box.Width;
                };
                return strip;
            }

            private int ClampKpiGroupWidth(int width) =>
                Math.Max(ScaleUi(MinKpiGroupWidth), Math.Min(ScaleUi(MaxKpiGroupWidth), width));

            private void BeginGroupDrag(object sender, MouseEventArgs eventArgs)
            {
                if (sender is not GroupBox box ||
                    eventArgs.Button != MouseButtons.Left ||
                    eventArgs.Y > 24)
                {
                    return;
                }
                _draggedGroup = box;
                try
                {
                    box.DoDragDrop(box, DragDropEffects.Move);
                }
                finally
                {
                    _draggedGroup = null;
                }
            }

            private void GroupDragEnter(object sender, DragEventArgs eventArgs)
            {
                eventArgs.Effect = eventArgs.Data != null &&
                    eventArgs.Data.GetDataPresent(typeof(GroupBox))
                    ? DragDropEffects.Move
                    : DragDropEffects.None;
            }

            private void GroupDragOver(object sender, DragEventArgs eventArgs)
            {
                GroupDragEnter(sender, eventArgs);
                if (eventArgs.Effect == DragDropEffects.Move)
                    MoveDraggedGroup(_groups.PointToClient(new Point(eventArgs.X, eventArgs.Y)));
            }

            private void GroupDragDrop(object sender, DragEventArgs eventArgs)
            {
                GroupDragOver(sender, eventArgs);
                GroupOrderChanged?.Invoke(_groups.Controls
                    .OfType<GroupBox>()
                    .Select(box => Convert.ToString(box.Tag) ?? box.Text.Trim())
                    .ToList());
            }

            private void MoveDraggedGroup(Point location)
            {
                if (_draggedGroup == null || !_groups.Controls.Contains(_draggedGroup))
                    return;
                List<GroupBox> otherGroups = _groups.Controls
                    .OfType<GroupBox>()
                    .Where(box => box != _draggedGroup)
                    .ToList();
                int insertionIndex = otherGroups.FindIndex(
                    box => location.X < box.Left + (box.Width / 2));
                if (insertionIndex < 0)
                    insertionIndex = otherGroups.Count;
                _groups.Controls.SetChildIndex(_draggedGroup, insertionIndex);
                _groups.PerformLayout();
            }
            protected override void OnResize(EventArgs eventArgs)
            {
                base.OnResize(eventArgs);
                int height = Math.Max(
                    ScaleUi(180),
                    _groups.ClientSize.Height - ScaleUi(32));
                foreach (GroupBox box in _groups.Controls.OfType<GroupBox>())
                    box.Height = height;
            }

            private Dictionary<string, bool> CurrentChecks()
            {
                var checks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<CheckedListBox, List<WasperKpi>> pair in _items)
                {
                    for (int index = 0; index < pair.Value.Count; index++)
                        checks[pair.Value[index].Key] = pair.Key.GetItemChecked(index);
                }
                return checks;
            }

            private IEnumerable<string> CurrentDisabledKeys()
            {
                foreach (KeyValuePair<CheckedListBox, List<WasperKpi>> pair in _items)
                {
                    for (int index = 0; index < pair.Value.Count; index++)
                    {
                        if (!pair.Key.GetItemChecked(index))
                            yield return pair.Value[index].Key;
                    }
                }
            }

            private void SetAllChecked(bool value)
            {
                foreach (CheckedListBox list in _items.Keys)
                {
                    for (int index = 0; index < list.Items.Count; index++)
                        list.SetItemChecked(index, value);
                }
            }

            private void ShowValuesCheckedChanged(object sender, EventArgs eventArgs)
            {
                if (_updatingKpiValueDisplay)
                    return;
                _showValues.Text = _showValues.Checked ? "Hide values" : "Show values";
                foreach (CheckedListBox list in _items.Keys)
                    ConfigureKpiValueDisplay(list);
                ShowValuesChanged?.Invoke(_showValues.Checked);
            }

            private void ConfigureKpiValueDisplay(CheckedListBox list)
            {
                if (list == null)
                    return;
                bool showValues = _showValues.Checked;
                list.DrawMode = showValues
                    ? DrawMode.OwnerDrawFixed
                    : DrawMode.Normal;
                int itemHeight = showValues
                    ? Math.Max(40, (list.Font.Height * 2) + 8)
                    : Math.Max(16, list.Font.Height + 3);
                if (list is KpiCheckedListBox kpiList)
                {
                    kpiList.ShowValues = showValues;
                    kpiList.ApplyItemHeight(itemHeight);
                }
                else
                    list.ItemHeight = itemHeight;
                list.Invalidate();
            }

            private static string FormatKpiValue(WasperKpi item)
            {
                if (item == null)
                    return "—";
                string value = item.Value.HasValue
                    ? item.Value.Value.ToString("G8", CultureInfo.InvariantCulture)
                    : item.TextValue;
                if (string.IsNullOrWhiteSpace(value))
                    value = "—";
                return string.IsNullOrWhiteSpace(item.Unit)
                    ? value
                    : value + " " + item.Unit;
            }

            private void ShowItemToolTip(object sender, MouseEventArgs eventArgs)
            {
                if (sender is not CheckedListBox list ||
                    !_items.TryGetValue(list, out List<WasperKpi> records))
                {
                    return;
                }

                int index = list.IndexFromPoint(eventArgs.Location);
                if (index < 0 || index >= records.Count)
                {
                    _toolTip.SetToolTip(list, string.Empty);
                    return;
                }
                WasperKpi item = records[index];
                _toolTip.SetToolTip(list, $"{item.Description}\r\nSource: {item.Source}");
            }

            private void ApplySelection(object sender, EventArgs eventArgs)
            {
                SelectionApplied?.Invoke(CurrentDisabledKeys().ToList());
            }

        }
    }
}
