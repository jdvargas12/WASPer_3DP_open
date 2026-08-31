using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace WASPer_3DP.Components._1_2_Studies
{
    public sealed partial class wsp_Sm01_WASPer_Study_Manager
    {
        private sealed partial class KpiManagerForm
        {
            /// <summary>
            /// Drag payload carried when reordering an item already in the composed (right) list.
            /// Distinguished from a <see cref="SampleNamePropertyOption"/> payload (dragged in from
            /// the left/available list) purely by its .NET type, the same way DragEventArgs.Data
            /// already distinguishes a dragged KPI GroupBox from anything else (WASPer_Sm01KpiTab.cs).
            /// </summary>
            private sealed class SampleNameReorderPayload
            {
                public SampleNameReorderPayload(int sourceIndex)
                {
                    SourceIndex = sourceIndex;
                }

                public int SourceIndex { get; }
            }

            private Control CreateSampleNamePanel()
            {
                // GroupBox.Text is a single-line native caption - it does not wrap, and a long
                // instructional sentence there either clips or (depending on theme/DPI) spills down
                // and overlaps whatever sits just inside the border. Instructions belong in a real
                // Label instead: AutoSize=false with an explicit fixed Height, the same fix already
                // used for the Dashboard's correlation-heatmap description (AutoSize=true wraps
                // against MaximumSize, not the width it is later stretched to, and can clip).
                var leftHint = new Label
                {
                    AutoSize = false,
                    Dock = DockStyle.Top,
                    Height = 32,
                    Text = "Drag a token onto the list to the right to add it, or drag a used " +
                        "one back here to remove it.",
                    TextAlign = ContentAlignment.MiddleLeft
                };
                var leftLayout = new Panel { Dock = DockStyle.Fill };
                leftLayout.Controls.Add(_sampleNameAvailable);
                leftLayout.Controls.Add(_sampleNameGroupFilter);
                leftLayout.Controls.Add(leftHint);
                var leftGroup = new GroupBox
                {
                    Text = "Available tokens",
                    Dock = DockStyle.Fill,
                    Padding = new Padding(8)
                };
                leftGroup.Controls.Add(leftLayout);

                var rightToolbar = new FlowLayoutPanel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Top,
                    FlowDirection = FlowDirection.LeftToRight,
                    Padding = new Padding(0, 0, 0, 4),
                    WrapContents = true
                };
                rightToolbar.Controls.Add(_sampleNameRemove);
                rightToolbar.Controls.Add(new Label
                {
                    Text = "Text:",
                    AutoSize = true,
                    Margin = new Padding(9, 7, 3, 0)
                });
                rightToolbar.Controls.Add(_sampleNameTextInput);
                rightToolbar.Controls.Add(_sampleNameInsertText);
                rightToolbar.Controls.Add(_sampleNameRestoreDefault);
                var rightHint = new Label
                {
                    AutoSize = false,
                    Dock = DockStyle.Top,
                    Height = 48,
                    Text = "Drag to reorder, or drag back to the left to remove a parameter. " +
                        "Double-click a parameter to remove it, or a text segment to edit it. " +
                        "Delete removes the selected item. Joined with underscores; X/Y/Z cell " +
                        "counts are joined with dots instead.",
                    TextAlign = ContentAlignment.MiddleLeft
                };
                var rightLayout = new Panel { Dock = DockStyle.Fill };
                rightLayout.Controls.Add(_sampleNameComposed);
                rightLayout.Controls.Add(rightToolbar);
                rightLayout.Controls.Add(rightHint);
                var rightGroup = new GroupBox
                {
                    Text = "Composed sample name",
                    Dock = DockStyle.Fill,
                    Padding = new Padding(8)
                };
                rightGroup.Controls.Add(rightLayout);

                var split = new SplitContainer
                {
                    Dock = DockStyle.Fill,
                    Orientation = Orientation.Vertical,
                    SplitterDistance = 340
                };
                split.Panel1.Controls.Add(leftGroup);
                split.Panel2.Controls.Add(rightGroup);

                var panel = new Panel { Dock = DockStyle.Fill };
                panel.Controls.Add(split);
                panel.Controls.Add(_sampleNamePreview);
                return panel;
            }

            private void WireSampleNameEvents()
            {
                _sampleNameGroupFilter.ItemCheck += SampleNameGroupItemCheck;
                _sampleNameRestoreDefault.Click += (sender, args) =>
                {
                    // An empty template resolves to the built-in default (just the iteration
                    // number) inside StoreSampleNameTemplate, so there is a single source of truth
                    // for what "default" means instead of duplicating it here.
                    EndEditSampleNameText();
                    SampleNameTemplateChanged?.Invoke(Enumerable.Empty<string>());
                };

                _sampleNameAvailable.MouseDown += SampleNameAvailableMouseDown;
                _sampleNameAvailable.DragEnter += SampleNameAvailableDragEnter;
                _sampleNameAvailable.DragOver += SampleNameAvailableDragEnter;
                _sampleNameAvailable.DragDrop += SampleNameAvailableDragDrop;

                _sampleNameComposed.MouseDown += SampleNameComposedMouseDown;
                _sampleNameComposed.DragEnter += SampleNameComposedDragEnter;
                _sampleNameComposed.DragOver += SampleNameComposedDragEnter;
                _sampleNameComposed.DragDrop += SampleNameComposedDragDrop;
                _sampleNameComposed.MouseDoubleClick += (sender, args) =>
                {
                    int index = _sampleNameComposed.IndexFromPoint(args.Location);
                    if (index < 0)
                        return;
                    if (_sampleNameComposed.Items[index] is SampleNamePropertyOption option &&
                        IsSampleNameTextToken(option.Key))
                    {
                        BeginEditSampleNameText(index, option);
                    }
                    else
                    {
                        RemoveComposedSampleNameToken(index);
                    }
                };
                _sampleNameComposed.KeyDown += (sender, args) =>
                {
                    if (args.KeyCode != Keys.Delete || _sampleNameComposed.SelectedIndex < 0)
                        return;
                    RemoveComposedSampleNameToken(_sampleNameComposed.SelectedIndex);
                    args.Handled = true;
                };
                _sampleNameComposed.SelectedIndexChanged += (sender, args) =>
                {
                    // Guard against UpdateSampleNameComposer's own Items.Clear()/Add() cycle, which
                    // would otherwise transiently select -1 and look exactly like the user having
                    // clicked away, cancelling every in-progress edit on the next background
                    // refresh.
                    if (_updatingSampleNameComposer)
                        return;
                    if (_sampleNameEditingTextIndex >= 0 &&
                        _sampleNameComposed.SelectedIndex != _sampleNameEditingTextIndex)
                    {
                        EndEditSampleNameText();
                    }
                };

                _sampleNameRemove.Click += (sender, args) =>
                {
                    if (_sampleNameComposed.SelectedIndex >= 0)
                        RemoveComposedSampleNameToken(_sampleNameComposed.SelectedIndex);
                };
                _sampleNameInsertText.Click += SampleNameInsertOrUpdateText;
                _sampleNameTextInput.KeyDown += (sender, args) =>
                {
                    if (args.KeyCode != Keys.Enter)
                        return;
                    args.SuppressKeyPress = true;
                    SampleNameInsertOrUpdateText(sender, EventArgs.Empty);
                };
            }

            /// <summary>
            /// Refreshes both the left (available, filtered by category) and right (composed,
            /// ordered) lists from the component's current option set and saved token order. Called
            /// on every UpdateStudyWindow, mirroring how the rest of the manager's tabs stay a pure
            /// view of component state rather than caching their own copy.
            /// </summary>
            public void UpdateSampleNameComposer(
                IEnumerable<SampleNamePropertyOption> options,
                IEnumerable<string> selectedTokens,
                bool inputConnected,
                string inputValue,
                string preview)
            {
                _sampleNameLastOptions = (options ?? Enumerable.Empty<SampleNamePropertyOption>())
                    .Where(option => option != null && !string.IsNullOrWhiteSpace(option.Key))
                    .ToList();
                _sampleNameOptionsByKey = _sampleNameLastOptions
                    .GroupBy(option => option.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                _sampleNameLastTokens = (selectedTokens ?? Enumerable.Empty<string>())
                    .Where(token => !string.IsNullOrWhiteSpace(token))
                    .ToList();

                // An in-progress text edit is only still valid if the slot it points at still
                // holds a text token - e.g. a background refresh mid-typing is fine, but if
                // something else changed that position (or it no longer exists) the edit buffer
                // would silently overwrite the wrong item, so cancel it instead.
                if (_sampleNameEditingTextIndex >= 0 &&
                    (_sampleNameEditingTextIndex >= _sampleNameLastTokens.Count ||
                     !IsSampleNameTextToken(_sampleNameLastTokens[_sampleNameEditingTextIndex])))
                {
                    EndEditSampleNameText();
                }

                _updatingSampleNameComposer = true;
                try
                {
                    RefreshSampleNameGroupFilter();
                    RefreshSampleNameAvailableList();

                    _sampleNameComposed.BeginUpdate();
                    _sampleNameComposed.Items.Clear();
                    foreach (string token in _sampleNameLastTokens)
                    {
                        _sampleNameComposed.Items.Add(ComposedSampleNameDisplayOption(token));
                    }
                    // Items.Clear() above reset selection to -1; restore it while still guarded so
                    // an in-progress text edit keeps its visible selection across the rebuild.
                    if (_sampleNameEditingTextIndex >= 0 &&
                        _sampleNameEditingTextIndex < _sampleNameComposed.Items.Count)
                    {
                        _sampleNameComposed.SelectedIndex = _sampleNameEditingTextIndex;
                    }
                    _sampleNameComposed.EndUpdate();
                }
                finally
                {
                    _updatingSampleNameComposer = false;
                }

                _sampleNameComposed.Enabled = !inputConnected || string.IsNullOrWhiteSpace(inputValue);
                _sampleNameAvailable.Enabled = _sampleNameComposed.Enabled;
                _sampleNamePreview.Text = inputConnected && !string.IsNullOrWhiteSpace(inputValue)
                    ? "Input override: " + preview
                    : inputConnected
                        ? "s_name is connected but empty; composer preview: " + preview
                        : "Preview: " + preview;
            }

            private static string SampleNameGroupOrOther(string group) =>
                string.IsNullOrWhiteSpace(group) ? "Other" : group;

            private void RefreshSampleNameGroupFilter()
            {
                // Distinct() keeps first-seen order, which is already General -> Infill ->
                // Parameters -> each KPI DisplayGroup because SampleNameOptions() sorts the source
                // list that way - no extra sort needed here.
                List<string> groups = _sampleNameLastOptions
                    .Select(option => SampleNameGroupOrOther(option.Group))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _sampleNameGroupFilter.BeginUpdate();
                _sampleNameGroupFilter.Items.Clear();
                foreach (string group in groups)
                    _sampleNameGroupFilter.Items.Add(group, !_sampleNameHiddenGroups.Contains(group));
                _sampleNameGroupFilter.EndUpdate();
            }

            private void RefreshSampleNameAvailableList()
            {
                var used = new HashSet<string>(_sampleNameLastTokens, StringComparer.OrdinalIgnoreCase);
                _sampleNameAvailable.BeginUpdate();
                _sampleNameAvailable.Items.Clear();
                foreach (SampleNamePropertyOption option in _sampleNameLastOptions
                    .Where(option => !_sampleNameHiddenGroups.Contains(SampleNameGroupOrOther(option.Group)))
                    .Where(option => !used.Contains(option.Key)))
                {
                    _sampleNameAvailable.Items.Add(option);
                }
                _sampleNameAvailable.EndUpdate();
            }

            private void SampleNameGroupItemCheck(object sender, ItemCheckEventArgs eventArgs)
            {
                if (_updatingSampleNameComposer)
                    return;
                if (_sampleNameGroupFilter.Items[eventArgs.Index] is not string group)
                    return;
                if (eventArgs.NewValue == CheckState.Checked)
                    _sampleNameHiddenGroups.Remove(group);
                else
                    _sampleNameHiddenGroups.Add(group);
                // ItemCheck fires before the clicked item's own check state is committed, so the
                // available-list rebuild (which does not touch the filter list itself) is safe to
                // run inline; only a same-control redraw would need the BeginInvoke deferral the
                // Dashboard's equivalent filter uses.
                RefreshSampleNameAvailableList();
            }

            private List<string> ComposedSampleNameTokens() =>
                _sampleNameComposed.Items
                    .Cast<SampleNamePropertyOption>()
                    .Select(option => option.Key)
                    .ToList();

            /// <summary>
            /// Builds the composed list's display item for a token. Checked in this order because a
            /// free-text token is never expected to be found in the catalog (it is user-authored,
            /// not one of SampleNameOptions()'s entries) - looking it up there first would always
            /// miss and wrongly fall through to the "not currently available" case below, which is
            /// reserved for a catalog reference that used to resolve (e.g. an unlinked slider or a
            /// since-disabled KPI producer) and no longer does.
            /// </summary>
            private SampleNamePropertyOption ComposedSampleNameDisplayOption(string token)
            {
                if (IsSampleNameTextToken(token))
                {
                    return new SampleNamePropertyOption
                    {
                        Key = token,
                        Label = "\"" + token.Substring(SampleNameTextPrefix.Length) + "\"",
                        Group = "Text"
                    };
                }
                if (_sampleNameOptionsByKey.TryGetValue(token, out SampleNamePropertyOption match))
                    return match;
                return new SampleNamePropertyOption
                {
                    Key = token,
                    Label = token + " (not currently available)",
                    Group = "Unavailable"
                };
            }

            private void RemoveComposedSampleNameToken(int index)
            {
                List<string> tokens = ComposedSampleNameTokens();
                if (index < 0 || index >= tokens.Count)
                    return;
                if (index == _sampleNameEditingTextIndex)
                    EndEditSampleNameText();
                tokens.RemoveAt(index);
                SampleNameTemplateChanged?.Invoke(tokens);
            }

            /// <summary>
            /// Loads an existing free-text segment's literal content into the Text field for
            /// editing. "Insert text" becomes "Update text" while a segment is loaded; selecting a
            /// different composed item, removing this one, or an unrelated state change from a
            /// solve cancels the edit (see UpdateSampleNameComposer and the SelectedIndexChanged
            /// handler in WireSampleNameEvents).
            /// </summary>
            private void BeginEditSampleNameText(int index, SampleNamePropertyOption option)
            {
                _sampleNameEditingTextIndex = index;
                _sampleNameTextInput.Text = option.Key.Substring(SampleNameTextPrefix.Length);
                _sampleNameInsertText.Text = "Update text";
                _sampleNameComposed.SelectedIndex = index;
                _sampleNameTextInput.Focus();
                _sampleNameTextInput.SelectAll();
            }

            private void EndEditSampleNameText()
            {
                _sampleNameEditingTextIndex = -1;
                _sampleNameInsertText.Text = "Insert text";
                _sampleNameTextInput.Clear();
            }

            /// <summary>
            /// Inserts the Text field's content as a new literal segment (right after the selected
            /// composed item, or at the end if nothing is selected), or - while editing an existing
            /// segment (see BeginEditSampleNameText) - replaces that segment's content in place.
            /// </summary>
            private void SampleNameInsertOrUpdateText(object sender, EventArgs eventArgs)
            {
                string text = _sampleNameTextInput.Text ?? string.Empty;
                if (text.Length == 0)
                    return;
                string newToken = SampleNameTextPrefix + text;
                List<string> tokens = ComposedSampleNameTokens();
                if (_sampleNameEditingTextIndex >= 0 && _sampleNameEditingTextIndex < tokens.Count)
                {
                    tokens[_sampleNameEditingTextIndex] = newToken;
                }
                else
                {
                    int insertAt = _sampleNameComposed.SelectedIndex >= 0
                        ? _sampleNameComposed.SelectedIndex + 1
                        : tokens.Count;
                    tokens.Insert(Math.Max(0, Math.Min(insertAt, tokens.Count)), newToken);
                }
                EndEditSampleNameText();
                SampleNameTemplateChanged?.Invoke(tokens);
            }

            private void SampleNameAvailableDragEnter(object sender, DragEventArgs eventArgs)
            {
                // Only accepts an item dragged out of the composed list (to remove it); dropping an
                // available item onto itself has no meaning.
                eventArgs.Effect = eventArgs.Data?.GetDataPresent(typeof(SampleNameReorderPayload)) == true
                    ? DragDropEffects.Move
                    : DragDropEffects.None;
            }

            private void SampleNameAvailableDragDrop(object sender, DragEventArgs eventArgs)
            {
                if (eventArgs.Data?.GetData(typeof(SampleNameReorderPayload)) is SampleNameReorderPayload payload)
                    RemoveComposedSampleNameToken(payload.SourceIndex);
            }

            private void SampleNameAvailableMouseDown(object sender, MouseEventArgs eventArgs)
            {
                if (eventArgs.Button != MouseButtons.Left)
                    return;
                int index = _sampleNameAvailable.IndexFromPoint(eventArgs.Location);
                if (index < 0 || _sampleNameAvailable.Items[index] is not SampleNamePropertyOption option)
                    return;
                _sampleNameAvailable.DoDragDrop(option, DragDropEffects.Copy);
            }

            private void SampleNameComposedMouseDown(object sender, MouseEventArgs eventArgs)
            {
                if (eventArgs.Button != MouseButtons.Left)
                    return;
                int index = _sampleNameComposed.IndexFromPoint(eventArgs.Location);
                if (index < 0)
                    return;
                _sampleNameComposed.SelectedIndex = index;
                _sampleNameComposed.DoDragDrop(new SampleNameReorderPayload(index), DragDropEffects.Move);
            }

            private void SampleNameComposedDragEnter(object sender, DragEventArgs eventArgs)
            {
                eventArgs.Effect =
                    eventArgs.Data?.GetDataPresent(typeof(SampleNamePropertyOption)) == true
                        ? DragDropEffects.Copy
                        : eventArgs.Data?.GetDataPresent(typeof(SampleNameReorderPayload)) == true
                            ? DragDropEffects.Move
                            : DragDropEffects.None;
            }

            private void SampleNameComposedDragDrop(object sender, DragEventArgs eventArgs)
            {
                Point client = _sampleNameComposed.PointToClient(new Point(eventArgs.X, eventArgs.Y));
                int target = _sampleNameComposed.IndexFromPoint(client);
                if (target < 0)
                    target = _sampleNameComposed.Items.Count;

                if (eventArgs.Data?.GetData(typeof(SampleNamePropertyOption)) is SampleNamePropertyOption dropped)
                {
                    List<string> tokens = ComposedSampleNameTokens();
                    if (tokens.Contains(dropped.Key, StringComparer.OrdinalIgnoreCase))
                        return;
                    tokens.Insert(Math.Max(0, Math.Min(target, tokens.Count)), dropped.Key);
                    SampleNameTemplateChanged?.Invoke(tokens);
                    return;
                }
                if (eventArgs.Data?.GetData(typeof(SampleNameReorderPayload)) is SampleNameReorderPayload payload)
                {
                    List<string> tokens = ComposedSampleNameTokens();
                    if (payload.SourceIndex < 0 || payload.SourceIndex >= tokens.Count)
                        return;
                    string moved = tokens[payload.SourceIndex];
                    tokens.RemoveAt(payload.SourceIndex);
                    int adjusted = payload.SourceIndex < target ? target - 1 : target;
                    tokens.Insert(Math.Max(0, Math.Min(adjusted, tokens.Count)), moved);
                    SampleNameTemplateChanged?.Invoke(tokens);
                }
            }
        }
    }
}
