using System;
using System.Collections.Generic;
using System.Linq;

using Eto.Drawing;
using Eto.Forms;

using Grasshopper;
using Rhino;
using Rhino.UI;

namespace WASPer_3DP.PatternEditing
{
    /// <summary>
    /// Cross-platform Eto.Forms replacement for <see cref="WasperShellSeamEditorForm"/>,
    /// <see cref="WasperGuideWarpEditorForm"/>, <see cref="WasperGuideWarpCanvas"/> and
    /// <see cref="WasperShellSeamCanvas"/> (WASPer_GuideWarpEditor.cs) -- see
    /// 00_Plans/WASPER_CROSS_PLATFORM_ETO_UI_MIGRATION_PLAN.md, Workstream 3 (Guide Warp / Shell Seam
    /// Editors). Follows the exact additive/inactive-until-cutover pattern already used for
    /// WASPer_PainterEtoUI.cs (Workstream 2): this file sits alongside the still-WinForms
    /// WASPer_GuideWarpEditor.cs, which is untouched. Cutover means changing the
    /// `new WasperGuideWarpEditorForm(this)`/`new WasperShellSeamEditorForm(this)` call sites (In10's
    /// host and Pp01, respectively) to their Eto counterparts below, plus the matching
    /// `IsDisposed` -> `IsClosed` swap, nothing else -- both take the same host-interface constructor
    /// argument as their WinForms originals.
    ///
    /// Business/state logic needed NO porting: WasperGuideWarpState, WasperGuideLayerScope,
    /// WasperShellSeamSettings, IWasperShellSeamEditorHost and IWasperGuideWarpEditorHost all stay in
    /// WASPer_GuideWarpEditor.cs, referenced here unqualified since this file shares that namespace
    /// (WASPer_3DP.PatternEditing) -- matching the WasperEtoPaintForm/IWasperPainterHost precedent.
    /// Only the four WinForms UI classes are ported here, named with the same "Eto" infix precedent as
    /// WasperEtoPaintForm/WasperEtoAtlasCanvas/Sm01EtoManagerForm/WasperPlaybackForm.
    ///
    /// Ported 1:1, low risk, all following confirmed working patterns already compiled into this repo
    /// (WASPer_PainterEtoUI.cs above all, plus Sm01EtoManagerForm.cs, WASPer_Gc05PlaybackForm.cs,
    /// WASPerMascot.cs):
    /// - FlowLayoutPanel rows -> Eto.Forms.StackLayout (horizontal), matching WasperEtoPaintForm's
    ///   toolbar rows.
    /// - ComboBox(DropDownList) -> Eto.Forms.DropDown, NumericUpDown -> Eto.Forms.NumericStepper
    ///   (Value is double, not decimal -- every cast-to-decimal in the WinForms source became a plain
    ///   double read), CheckBox -> Eto.Forms.CheckBox (Checked is bool? on Eto; compared/assigned with
    ///   `== true` where the WinForms source used a plain bool).
    /// - System.Windows.Forms.Timer -> Eto.Forms.UITimer (Interval in seconds, not milliseconds; both
    ///   100ms polling timers here became UITimer with Interval = 0.1), stopped from the window's
    ///   Closed handler exactly like WasperEtoPaintForm's commit timers.
    /// - Window chrome (Owner/UseRhinoStyle/Maximizable/Topmost/ShowInTaskbar) copied verbatim from
    ///   WasperEtoPaintForm's constructor, including keeping Topmost = true alongside Owner: the
    ///   Painter regression fix initially removed Topmost, then restored it once testing showed Owner
    ///   alone let the painter fall behind a Grasshopper canvas hosted in its own top-level window: the
    ///   maintainer-confirmed-working combination (Ge17/Pp14/Fi01, 2026-08-30) is Owner + UseRhinoStyle()
    ///   + Topmost = true + Maximizable = true (WinForms had MaximizeBox = false on both these forms
    ///   too; changed to true here for the same reason it was changed on the Painter -- MaximizeBox/
    ///   Resizable=false alone was the cause of the "window not resizable" regression, not Topmost).
    /// - Unlike WasperEtoPaintForm's persistent hide-on-close pattern (Closing cancels + hides so the
    ///   window is reused), these four forms use the WinForms originals' real close-and-recreate
    ///   pattern instead: Closed (not a cancelled Closing) calls host.GuideEditorClosed() and the
    ///   consumer (In10/Pp01) constructs a fresh instance next time it checks IsClosed and finds it
    ///   true, matching WasperGuideWarpEditorForm/WasperShellSeamEditorForm's own FormClosed behavior
    ///   exactly (no behavior change from the WinForms original here, unlike the Painter).
    /// - Owner-drawn canvases (WasperEtoGuideWarpCanvas/WasperEtoShellSeamCanvas) follow
    ///   WasperEtoAtlasCanvas's Drawable/Graphics/Pen/SolidBrush pattern: CanFocus = true so Escape/
    ///   keyboard reach OnKeyDown once clicked (WinForms' IsInputKey override has no Eto equivalent and
    ///   was dropped -- Escape reaches OnKeyDown directly here since Eto controls don't treat it as a
    ///   dialog-cancel command the way a WinForms Form sometimes does), SmoothingMode.AntiAlias ->
    ///   Graphics.AntiAlias = true, DrawString -> DrawText, Pens.White -> a locally new'd
    ///   `new Pen(Colors.White, 1f)`.
    /// - WinForms' explicit mouse Capture (so a drag that leaves the control's bounds keeps delivering
    ///   MouseMove/MouseUp, and OnMouseCaptureChanged cancels an interrupted drag) has no confirmed
    ///   portable Eto.Forms.Control equivalent and no in-repo precedent, exactly as already documented
    ///   on WasperEtoAtlasCanvas. Left unset; every drag/pan/select/rubber-band state machine below still
    ///   gates purely on its own bool/int flags (_dragAnchor, _panning, _selectingRegion, _dragHandle),
    ///   so drags that stay inside the canvas keep working, but a drag released outside the canvas
    ///   bounds may not clean up until the next click inside it. Flagged for the interactive check (plan
    ///   section 6.3/9.2, same caveat already accepted for the Painter).
    /// - Cursors.Hand has no in-repo confirmed-compiling precedent (only Default/Move/SizeAll are
    ///   confirmed via a real build against WasperEtoPaintForm -- see its Cursors.SizeLeftRight comment).
    ///   Anchor/handle drags below use Cursors.SizeAll instead of the WinForms original's Cursors.Hand,
    ///   the same substitution already made and build-confirmed for the Painter's texture-handle drags.
    /// - Eto.Forms.Splitter replaces WinForms SplitContainer for the Guide Warp editor's side-by-side
    ///   guide/shell canvases. No in-repo Splitter precedent exists yet (first use in this repo); member
    ///   names (Orientation, Panel1/Panel2, Panel1MinimumSize/Panel2MinimumSize, Position) are taken from
    ///   general Eto.Forms API knowledge, not build-confirmed. Orientation.Horizontal is used for a
    ///   side-by-side (left/right) split with a vertical splitter bar, matching WinForms
    ///   SplitContainer.Orientation.Vertical's panel arrangement (WinForms and Eto name this axis
    ///   oppositely -- flagged here so a build error on this line is expected to be an
    ///   Orientation-direction fix, not a missing-member fix).
    /// - ComboBox's WinForms `.DroppedDown` guard (skip re-syncing values while the user has the
    ///   dropdown open) was dropped: no in-repo precedent confirms Eto.Forms.DropDown exposes an
    ///   equivalent property. The 100ms poll may briefly fight an open dropdown's selection on some
    ///   platforms; flagged for the interactive check rather than guessed at.
    /// - The dim-gray helper-text labels used `Color.FromArgb(105, 105, 105)` rather than a
    ///   `Colors.DimGray` named constant -- that exact ARGB tuple is already confirmed-compiling
    ///   elsewhere in WasperEtoPaintForm, whereas no in-repo usage confirms the named-color member
    ///   exists on Eto.Drawing.Colors.
    ///
    /// NOT build-verified (no Windows/.NET toolchain in this environment -- same limitation noted on
    /// every other Eto file in this repo, including WasperEtoPaintForm above, which needed two rounds of
    /// real-build fixes for exactly this kind of unverified-member guess). Expect the same here; report
    /// build errors and they'll be fixed the same way.
    /// </summary>
    internal sealed class WasperEtoShellSeamEditorForm : Form
    {
        private readonly IWasperShellSeamEditorHost _host;
        private readonly WasperEtoShellSeamCanvas _canvas;
        private readonly DropDown _scope;
        private readonly NumericStepper _from;
        private readonly NumericStepper _to;
        private readonly NumericStepper _display;
        private readonly CheckBox _xSeam;
        private readonly NumericStepper _fillet;
        private readonly UITimer _timer;
        private bool _syncing;
        private int _revision = -1;
        private bool _closed;

        internal WasperEtoShellSeamEditorForm(IWasperShellSeamEditorHost host)
        {
            _host = host;
            Title = host.GuideEditorTitle;
            ShowInTaskbar = true;
            Resizable = true;
            Maximizable = true;
            Minimizable = true;
            Topmost = true;
            RhinoDoc document = Instances.ActiveCanvas?.Document?.RhinoDocument ?? RhinoDoc.ActiveDoc;
            if (document != null)
                Owner = RhinoEtoApp.MainWindowForDocument(document);
            // Grasshopper can be hosted in a separate top-level window from Rhino. Ownership by the
            // Rhino main window alone therefore lets this editor fall behind the GH canvas when the
            // canvas is clicked. Keep it visible while it is open, matching WasperEtoPaintForm.
            this.UseRhinoStyle();

            ClientSize = new Size(900, 600);
            MinimumSize = new Size(640, 400);

            var scopeBar = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Padding = new Padding(6, 4, 6, 3),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            scopeBar.Items.Add(LabelFor("Layer scope:"));
            _scope = new DropDown();
            _scope.Items.Add("All layers");
            _scope.Items.Add("Range");
            _scope.Items.Add("Single layer");
            _scope.SelectedIndexChanged += (_, __) => CommitScope();
            scopeBar.Items.Add(_scope);
            scopeBar.Items.Add(LabelFor("From:"));
            _from = Number();
            _from.ValueChanged += (_, __) => CommitScope();
            scopeBar.Items.Add(_from);
            scopeBar.Items.Add(LabelFor("To:"));
            _to = Number();
            _to.ValueChanged += (_, __) => CommitScope();
            scopeBar.Items.Add(_to);
            scopeBar.Items.Add(LabelFor("Display layer:"));
            _display = Number();
            _display.ValueChanged += (_, __) => CommitScope();
            scopeBar.Items.Add(_display);

            var seamBar = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Padding = new Padding(6, 4, 6, 3),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            seamBar.Items.Add(LabelFor("Shell seam:"));
            _xSeam = new CheckBox { Text = "X seam" };
            _xSeam.CheckedChanged += (_, __) => { if (!_syncing) _host.SetShellXSeam(_xSeam.Checked == true); };
            seamBar.Items.Add(_xSeam);
            seamBar.Items.Add(LabelFor("Fillet radius:"));
            _fillet = new NumericStepper
            {
                DecimalPlaces = 3,
                Increment = 0.5,
                MinValue = 0,
                MaxValue = 1000000,
                Width = 90
            };
            _fillet.ValueChanged += (_, __) => { if (!_syncing) _host.SetShellFilletRadius(_fillet.Value); };
            seamBar.Items.Add(_fillet);
            seamBar.Items.Add(ActionButton("Reset seam", () => _host.ResetShellSeam(), 95));
            seamBar.Items.Add(ActionButton("Rotate 90°", () => _canvas.RotateClockwise(), 90));

            _canvas = new WasperEtoShellSeamCanvas(host);

            Content = new TableLayout
            {
                Rows =
                {
                    new TableRow(new TableCell(scopeBar, true)) { ScaleHeight = false },
                    new TableRow(new TableCell(seamBar, true)) { ScaleHeight = false },
                    new TableRow(new TableCell(_canvas, true)) { ScaleHeight = true }
                }
            };
            Sync();

            _timer = new UITimer { Interval = 0.1 };
            _timer.Elapsed += (_, __) => RefreshFromHost();
            _timer.Start();

            Closed += (_, __) =>
            {
                _closed = true;
                _timer.Stop();
                _host.GuideEditorClosed();
            };
        }

        /// <summary>True once this window has actually closed. Mirrors WasperEtoPaintForm.IsClosed's
        /// name, but here it becomes true on a real Closed (see the class doc comment) rather than
        /// staying false forever behind a cancelled Closing.</summary>
        internal bool IsClosed => _closed;

        internal void ActivateEditor()
        {
            if (!Visible)
                Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            BringToFront();
            Focus();
        }

        private void RefreshFromHost()
        {
            if (_revision == _host.GuideVisualRevision)
                return;
            _revision = _host.GuideVisualRevision;
            Sync();
            _canvas.Invalidate();
        }

        private void Sync()
        {
            if (_scope == null)
                return;
            _syncing = true;
            int maximum = Math.Max(0, _host.GuideLayerCount - 1);
            foreach (NumericStepper number in new[] { _from, _to, _display })
            {
                number.MinValue = 0;
                number.MaxValue = maximum;
            }
            _scope.SelectedIndex = (int)_host.GuideLayerScope;
            _from.Value = Math.Max(0, Math.Min(maximum, _host.GuideLayerFrom));
            _to.Value = Math.Max(0, Math.Min(maximum, _host.GuideLayerTo));
            _display.Value = Math.Max(0, Math.Min(maximum, _host.GuideDisplayLayer));
            bool range = _host.GuideLayerScope == WasperGuideLayerScope.Range;
            _from.Enabled = range;
            _to.Enabled = range;
            WasperShellSeamSettings settings = _host.ShellSeamSettings ?? new WasperShellSeamSettings();
            _xSeam.Checked = settings.XSeam;
            _fillet.Value = Math.Max(0.0, Math.Min(1000000.0, settings.FilletRadius));
            _syncing = false;
        }

        private void CommitScope()
        {
            if (_syncing || _scope.SelectedIndex < 0)
                return;
            var scope = (WasperGuideLayerScope)_scope.SelectedIndex;
            int from = (int)_from.Value;
            int to = (int)_to.Value;
            int display = (int)_display.Value;
            if (scope == WasperGuideLayerScope.Single)
                from = to = display;
            _host.SetGuideLayerScope(scope, from, to, display);
        }

        private static Label LabelFor(string text) => new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center
        };

        private static NumericStepper Number() => new NumericStepper
        {
            MinValue = 0,
            MaxValue = 0,
            DecimalPlaces = 0,
            Width = 60
        };

        private static Button ActionButton(string text, Action action, int width)
        {
            var button = new Button { Text = text, Width = width };
            button.Click += (_, __) => action();
            return button;
        }
    }

    internal sealed class WasperEtoGuideWarpEditorForm : Form
    {
        private readonly IWasperGuideWarpEditorHost _host;
        private readonly DropDown _domainSelector;
        private readonly WasperEtoGuideWarpCanvas _canvas;
        private readonly WasperEtoShellSeamCanvas _shellCanvas;
        private readonly Button _undo;
        private readonly Button _redo;
        private readonly Button _live;
        private readonly Button _update;
        private readonly NumericStepper _densityNumber;
        private readonly Button _densityAuto;
        private readonly DropDown _scopeSelector;
        private readonly NumericStepper _scopeFrom;
        private readonly NumericStepper _scopeTo;
        private readonly NumericStepper _displayLayer;
        private readonly CheckBox _xSeam;
        private readonly NumericStepper _seamFillet;
        private readonly UITimer _refreshTimer;
        private int _lastRevision = -1;
        private bool _syncingDensity;
        private bool _syncingScope;
        private bool _syncingShell;
        private bool _closed;

        internal WasperEtoGuideWarpEditorForm(IWasperGuideWarpEditorHost host)
        {
            _host = host;
            Title = host.GuideEditorTitle;
            ShowInTaskbar = true;
            Resizable = true;
            Maximizable = true;
            Minimizable = true;
            Topmost = true;
            RhinoDoc document = Instances.ActiveCanvas?.Document?.RhinoDocument ?? RhinoDoc.ActiveDoc;
            if (document != null)
                Owner = RhinoEtoApp.MainWindowForDocument(document);
            this.UseRhinoStyle();

            ClientSize = new Size(1200, 560);
            MinimumSize = new Size(720, 390);

            var toolbar = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Padding = new Padding(6, 6, 6, 4),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            toolbar.Items.Add(new Label { Text = "Shared guide:", VerticalAlignment = VerticalAlignment.Center });
            _domainSelector = new DropDown { Width = 145 };
            _domainSelector.SelectedIndexChanged += (_, __) =>
            {
                _canvas.Guide = ActiveDomain;
                _host.SelectGuide(ActiveDomain);
                SyncDensity();
            };
            toolbar.Items.Add(_domainSelector);
            _live = ActionButton("▶ Live", () => _host.ToggleGuideLive(), 78);
            _update = ActionButton("Update", () => _host.ApplyPendingGuideUpdate(), 72);
            toolbar.Items.Add(_live);
            toolbar.Items.Add(_update);
            _undo = ActionButton("Undo", () => _host.UndoGuideWarp());
            _redo = ActionButton("Redo", () => _host.RedoGuideWarp());
            toolbar.Items.Add(_undo);
            toolbar.Items.Add(_redo);
            toolbar.Items.Add(ActionButton("Reset all guides", () => _host.ResetAllGuideWarps(), 118));
            toolbar.Items.Add(ActionButton("Rotate 90°", () =>
            {
                _canvas.RotateClockwise();
                _shellCanvas.RotateClockwise();
            }, 92));
            toolbar.Items.Add(new Label { Text = "Controls/cell:", VerticalAlignment = VerticalAlignment.Center });
            _densityNumber = new NumericStepper
            {
                MinValue = 1,
                MaxValue = 32,
                DecimalPlaces = 0,
                Width = 50
            };
            _densityNumber.ValueChanged += (_, __) =>
            {
                if (!_syncingDensity && _densityNumber.Enabled)
                    _host.SetGuideControlDensity(ActiveDomain, (int)_densityNumber.Value);
            };
            toolbar.Items.Add(_densityNumber);
            _densityAuto = ActionButton("Auto", () => _host.ResetGuideControlDensity(ActiveDomain), 55);
            toolbar.Items.Add(_densityAuto);
            toolbar.Items.Add(new Label
            {
                Text = "Select and drag stations directly on any guide curve. Endpoints remain fixed.",
                TextColor = Color.FromArgb(105, 105, 105),
                VerticalAlignment = VerticalAlignment.Center
            });

            _canvas = new WasperEtoGuideWarpCanvas(host);
            _shellCanvas = new WasperEtoShellSeamCanvas(host);
            _canvas.GuideSelected += guide =>
            {
                if (guide >= 0 && guide < _domainSelector.Items.Count)
                    _domainSelector.SelectedIndex = guide;
            };

            var scopeBar = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Padding = new Padding(6, 4, 6, 3),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            scopeBar.Items.Add(ScopeLabel("Layer scope:"));
            _scopeSelector = new DropDown { Width = 105 };
            _scopeSelector.Items.Add("All layers");
            _scopeSelector.Items.Add("Range");
            _scopeSelector.Items.Add("Single layer");
            _scopeSelector.SelectedIndexChanged += (_, __) => CommitScope();
            scopeBar.Items.Add(_scopeSelector);
            scopeBar.Items.Add(ScopeLabel("From:"));
            _scopeFrom = ScopeNumber();
            _scopeFrom.ValueChanged += (_, __) => CommitScope();
            scopeBar.Items.Add(_scopeFrom);
            scopeBar.Items.Add(ScopeLabel("To:"));
            _scopeTo = ScopeNumber();
            _scopeTo.ValueChanged += (_, __) => CommitScope();
            scopeBar.Items.Add(_scopeTo);
            scopeBar.Items.Add(ScopeLabel("Display layer:"));
            _displayLayer = ScopeNumber();
            _displayLayer.ValueChanged += (_, __) => CommitScope();
            scopeBar.Items.Add(_displayLayer);
            scopeBar.Items.Add(new Label
            {
                Text = "Edits use normalized arc length across the selected layers.",
                TextColor = Color.FromArgb(105, 105, 105),
                VerticalAlignment = VerticalAlignment.Center
            });

            var shellBar = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Padding = new Padding(6, 4, 6, 3),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            shellBar.Items.Add(ScopeLabel("Shell seam:"));
            _xSeam = new CheckBox { Text = "X seam" };
            _xSeam.CheckedChanged += (_, __) =>
            {
                if (!_syncingShell)
                    _host.SetShellXSeam(_xSeam.Checked == true);
            };
            shellBar.Items.Add(_xSeam);
            shellBar.Items.Add(ScopeLabel("Fillet radius:"));
            _seamFillet = new NumericStepper
            {
                DecimalPlaces = 3,
                Increment = 0.5,
                MinValue = 0,
                MaxValue = 1000000,
                Width = 85
            };
            _seamFillet.ValueChanged += (_, __) =>
            {
                if (!_syncingShell)
                    _host.SetShellFilletRadius(_seamFillet.Value);
            };
            shellBar.Items.Add(_seamFillet);
            shellBar.Items.Add(ActionButton("Reset seam", () => _host.ResetShellSeam(), 95));
            shellBar.Items.Add(new Label
            {
                Text = "Drag the seam around the shell; X-seam endpoints move freely in the shell plane.",
                TextColor = Color.FromArgb(105, 105, 105),
                VerticalAlignment = VerticalAlignment.Center
            });

            var split = new Splitter
            {
                Orientation = Orientation.Horizontal,
                Panel1 = _canvas,
                Panel2 = _shellCanvas,
                Panel1MinimumSize = 240,
                Panel2MinimumSize = 240,
                Position = ClientSize.Width / 2
            };

            Content = new TableLayout
            {
                Rows =
                {
                    new TableRow(new TableCell(toolbar, true)) { ScaleHeight = false },
                    new TableRow(new TableCell(scopeBar, true)) { ScaleHeight = false },
                    new TableRow(new TableCell(shellBar, true)) { ScaleHeight = false },
                    new TableRow(new TableCell(split, true)) { ScaleHeight = true }
                }
            };
            RefreshDomains();
            SyncScope();
            SyncShell();

            _refreshTimer = new UITimer { Interval = 0.1 };
            _refreshTimer.Elapsed += (_, __) => RefreshFromHost();
            _refreshTimer.Start();

            Closed += (_, __) =>
            {
                _closed = true;
                _refreshTimer.Stop();
                _host.GuideEditorClosed();
            };
        }

        /// <summary>See WasperEtoShellSeamEditorForm.IsClosed's doc comment -- same real-close
        /// semantics.</summary>
        internal bool IsClosed => _closed;

        internal void ActivateEditor()
        {
            RefreshDomains();
            if (!Visible)
                Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            BringToFront();
            Focus();
        }

        /// <summary>Host-facing immediate-refresh entry point (WinForms Control.Refresh has no
        /// Eto.Forms equivalent -- Eto's Form/Control expose no method of this name). The host calls
        /// this right after bumping GuideVisualRevision so the editor updates immediately instead of
        /// waiting for the next 100ms poll.</summary>
        internal void Refresh() => RefreshFromHost();

        private int ActiveDomain => Math.Max(0, _domainSelector.SelectedIndex);

        private void RefreshDomains()
        {
            int selected = ActiveDomain;
            int count = Math.Max(0, _host.GuideDomainCount);
            if (_domainSelector.Items.Count != count)
            {
                _domainSelector.Items.Clear();
                for (int i = 0; i < count; i++)
                    _domainSelector.Items.Add($"Guide {i}");
                if (count > 0)
                    _domainSelector.SelectedIndex = Math.Min(selected, count - 1);
            }
            _canvas.Guide = ActiveDomain;
        }

        private void RefreshFromHost()
        {
            RefreshDomains();
            _undo.Enabled = _host.CanUndoGuideWarp;
            _redo.Enabled = _host.CanRedoGuideWarp;
            SyncLiveControls();
            if (_lastRevision != _host.GuideVisualRevision)
            {
                _lastRevision = _host.GuideVisualRevision;
                SyncDensity();
                SyncScope();
                SyncShell();
                _canvas.Invalidate();
                _shellCanvas.Invalidate();
            }
        }

        private void SyncLiveControls()
        {
            if (_live == null || _update == null)
                return;
            bool live = _host.GuideLiveEnabled;
            bool pending = _host.HasPendingGuideUpdate;
            _live.Text = live ? "▶ Live" : "Ⅱ Paused";
            _live.BackgroundColor = live ? Color.FromArgb(70, 180, 100) : SystemColors.Control;
            _live.TextColor = live ? Colors.White : SystemColors.ControlText;
            _update.Text = pending ? "Update *" : "Update";
            _update.Enabled = !live;
        }

        private void SyncScope()
        {
            if (_scopeSelector == null)
                return;
            _syncingScope = true;
            int maximum = Math.Max(0, _host.GuideLayerCount - 1);
            foreach (NumericStepper number in new[] { _scopeFrom, _scopeTo, _displayLayer })
            {
                number.MaxValue = maximum;
                number.MinValue = 0;
            }
            _scopeSelector.SelectedIndex = (int)_host.GuideLayerScope;
            _scopeFrom.Value = Math.Max(0, Math.Min(maximum, _host.GuideLayerFrom));
            _scopeTo.Value = Math.Max(0, Math.Min(maximum, _host.GuideLayerTo));
            _displayLayer.Value = Math.Max(0, Math.Min(maximum, _host.GuideDisplayLayer));
            bool range = _host.GuideLayerScope == WasperGuideLayerScope.Range;
            _scopeFrom.Enabled = range;
            _scopeTo.Enabled = range;
            _displayLayer.Enabled = true;
            _syncingScope = false;
        }

        private void CommitScope()
        {
            if (_syncingScope || _scopeSelector.SelectedIndex < 0)
                return;
            var scope = (WasperGuideLayerScope)_scopeSelector.SelectedIndex;
            int from = (int)_scopeFrom.Value;
            int to = (int)_scopeTo.Value;
            int display = (int)_displayLayer.Value;
            if (scope == WasperGuideLayerScope.Single)
            {
                from = display;
                to = display;
            }
            _host.SetGuideLayerScope(scope, from, to, display);
        }

        private void SyncDensity()
        {
            if (_densityNumber == null || _densityAuto == null)
                return;
            _syncingDensity = true;
            bool supported = _host.GuideSupportsControlDensity(ActiveDomain);
            _densityNumber.Enabled = supported;
            _densityAuto.Enabled = supported && _host.HasGuideControlDensityOverride(ActiveDomain);
            double density = Math.Max(
                _densityNumber.MinValue,
                Math.Min(_densityNumber.MaxValue, _host.GetGuideControlDensity(ActiveDomain)));
            _densityNumber.Value = density;
            _syncingDensity = false;
        }

        private void SyncShell()
        {
            if (_xSeam == null || _seamFillet == null)
                return;
            _syncingShell = true;
            WasperShellSeamSettings settings = _host.ShellSeamSettings ?? new WasperShellSeamSettings();
            _xSeam.Checked = settings.XSeam;
            _seamFillet.Value = Math.Max(0.0, Math.Min(1000000.0, settings.FilletRadius));
            _syncingShell = false;
        }

        private static Button ActionButton(string text, Action action, int width = 72)
        {
            var button = new Button { Text = text, Width = width };
            button.Click += (_, __) => action();
            return button;
        }

        private static Label ScopeLabel(string text) => new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center
        };

        private static NumericStepper ScopeNumber() => new NumericStepper
        {
            MinValue = 0,
            MaxValue = 0,
            DecimalPlaces = 0,
            Width = 60
        };
    }

    internal sealed class WasperEtoGuideWarpCanvas : Drawable
    {
        private readonly IWasperGuideWarpEditorHost _host;
        private int _guide;
        private int _dragAnchor = -1;
        private int _dragGuide = -1;
        private PointF _dragMouseStart;
        private bool _dragMoved;
        private readonly HashSet<GuideAnchorKey> _selectedAnchors = new HashSet<GuideAnchorKey>();
        private readonly Dictionary<GuideAnchorKey, double> _dragBaseValues =
            new Dictionary<GuideAnchorKey, double>();
        private readonly Dictionary<int, List<double>> _dragGuideBaseValues =
            new Dictionary<int, List<double>>();
        private bool _selectingRegion;
        private bool _addRegionSelection;
        private PointF _selectionStart;
        private PointF _selectionCurrent;
        private readonly List<System.Drawing.PointF[]> _screenCurves = new List<System.Drawing.PointF[]>();
        private float _zoom = 1.0f;
        private PointF _pan = PointF.Empty;
        private bool _panning;
        private PointF _panStart;
        private PointF _panOrigin;
        private int _quarterTurns;

        internal event Action<int> GuideSelected;

        internal void RotateClockwise()
        {
            _quarterTurns = (_quarterTurns + 1) % 4;
            _zoom = 1.0f;
            _pan = PointF.Empty;
            Invalidate();
        }

        // Drawable (unlike a WinForms Control) has no inherited Font property to draw text with --
        // matches WasperEtoAtlasCanvas's own _dimensionFont workaround (WASPer_PainterEtoUI.cs).
        private readonly Font _uiFont = new Font(FontFamilies.Sans, 9f);

        internal WasperEtoGuideWarpCanvas(IWasperGuideWarpEditorHost host)
        {
            _host = host;
            CanFocus = true;
            BackgroundColor = Color.FromArgb(246, 246, 246);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _uiFont?.Dispose();
            base.Dispose(disposing);
        }

        internal int Guide
        {
            get => _guide;
            set
            {
                int next = Math.Max(0, value);
                if (_guide == next)
                    return;
                _guide = next;
                _dragAnchor = -1;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.AntiAlias = true;
            g.Clear(BackgroundColor);
            BuildScreenCurves();
            using var guidePen = new Pen(Color.FromArgb(85, 85, 85), 1.8f);
            using var selectedPen = new Pen(Color.FromArgb(48, 105, 152), 3.2f);
            using var originalPen = new Pen(Color.FromArgb(165, 165, 165), 1.2f) { DashStyle = DashStyles.Dot };
            using var endpointBrush = new SolidBrush(Color.FromArgb(70, 70, 70));
            using var primaryHandleBrush = new SolidBrush(Color.FromArgb(235, 120, 25));
            using var secondaryHandleBrush = new SolidBrush(Color.FromArgb(244, 177, 105));
            using var otherPrimaryBrush = new SolidBrush(Color.FromArgb(55, 135, 185));
            using var otherSecondaryBrush = new SolidBrush(Color.FromArgb(135, 190, 220));
            using var selectionPen = new Pen(Color.FromArgb(25, 85, 135), 2.5f);
            using var selectionFill = new SolidBrush(Color.FromArgb(45, 70, 145, 205));
            using var textBrush = new SolidBrush(Color.FromArgb(75, 75, 75));
            using var whitePen = new Pen(Colors.White, 1f);

            if (_screenCurves.Count == 0)
            {
                g.DrawText(_uiFont, textBrush, 30, 30, "Solve valid guide curves before opening the editor.");
                return;
            }
            _selectedAnchors.RemoveWhere(key =>
                key.Guide < 0 || key.Guide >= _screenCurves.Count ||
                key.Anchor <= 0 ||
                key.Anchor >= _host.GetGuideWarp(key.Guide).Count - 1);

            for (int guide = 0; guide < _screenCurves.Count; guide++)
            {
                System.Drawing.PointF[] curve = _screenCurves[guide];
                if (curve.Length < 2)
                    continue;
                g.DrawLines(guide == _guide ? selectedPen : guidePen, ToEtoPoints(curve));
                IReadOnlyList<double> values = _host.GetGuideWarp(guide);
                IReadOnlyList<double> sourceStations = _host.GetGuideSourceStations(guide);
                for (int i = 0; i < values.Count; i++)
                {
                    double sourceU = i < sourceStations.Count
                        ? sourceStations[i]
                        : (double)i / (values.Count - 1);
                    PointF original = PointAt(curve, sourceU);
                    PointF moved = PointAt(curve, values[i]);
                    const float originalRadius = 3.0f;
                    bool endpoint = i == 0 || i == values.Count - 1;
                    bool primary = endpoint || _host.IsGuidePrimaryStation(guide, i);
                    float handleRadius = endpoint ? 8.5f : primary ? 9.5f : 6.3f;
                    g.DrawEllipse(
                        originalPen,
                        original.X - originalRadius,
                        original.Y - originalRadius,
                        2f * originalRadius,
                        2f * originalRadius);
                    Brush brush = endpoint
                        ? endpointBrush
                        : guide == _guide
                            ? primary ? primaryHandleBrush : secondaryHandleBrush
                            : primary ? otherPrimaryBrush : otherSecondaryBrush;
                    g.FillEllipse(
                        brush,
                        moved.X - handleRadius,
                        moved.Y - handleRadius,
                        2f * handleRadius,
                        2f * handleRadius);
                    g.DrawEllipse(
                        whitePen,
                        moved.X - handleRadius,
                        moved.Y - handleRadius,
                        2f * handleRadius,
                        2f * handleRadius);
                    if (!endpoint && _selectedAnchors.Contains(new GuideAnchorKey(guide, i)))
                    {
                        float selectionRadius = handleRadius + 4f;
                        g.FillEllipse(
                            selectionFill,
                            moved.X - selectionRadius,
                            moved.Y - selectionRadius,
                            2f * selectionRadius,
                            2f * selectionRadius);
                        g.DrawEllipse(
                            selectionPen,
                            moved.X - selectionRadius,
                            moved.Y - selectionRadius,
                            2f * selectionRadius,
                            2f * selectionRadius);
                    }
                }
                PointF label = ToEtoPoint(curve[0]);
                g.DrawText(_uiFont, textBrush, label.X + 8f, label.Y + 8f, $"G{guide}");
            }

            g.DrawText(
                _uiFont,
                textBrush,
                22,
                Height - 64,
                "Primary stations = larger/darker · secondary stations = smaller/lighter\n" +
                "Ctrl+click toggles · drag empty space selects a region · Ctrl+drag adds · Esc clears\n" +
                "Drag any selected station to move the group · wheel zoom · middle/right drag pan.");

            if (_selectingRegion)
            {
                RectangleF region = SelectionRectangle();
                using var regionFill = new SolidBrush(Color.FromArgb(35, 55, 135, 190));
                using var regionPen = new Pen(Color.FromArgb(55, 135, 190), 1.4f) { DashStyle = DashStyles.Dash };
                g.FillRectangle(regionFill, region);
                g.DrawRectangle(regionPen, region);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Buttons == MouseButtons.Middle || e.Buttons == MouseButtons.Alternate)
            {
                _panning = true;
                _panStart = e.Location;
                _panOrigin = _pan;
                Cursor = Cursors.Move;
                return;
            }
            if (e.Buttons != MouseButtons.Primary)
                return;
            GuideAnchorKey hit = HitGuideAnchor(e.Location);
            bool control = (e.Modifiers & Keys.Control) == Keys.Control;
            if (!hit.IsValid)
            {
                _selectingRegion = true;
                _addRegionSelection = control;
                _selectionStart = e.Location;
                _selectionCurrent = e.Location;
                if (!control)
                    _selectedAnchors.Clear();
                Invalidate();
                return;
            }

            if (control && _selectedAnchors.Contains(hit))
            {
                _selectedAnchors.Remove(hit);
                Invalidate();
                return;
            }
            if (!control && !_selectedAnchors.Contains(hit))
                _selectedAnchors.Clear();
            _selectedAnchors.Add(hit);
            _guide = hit.Guide;
            _dragGuide = hit.Guide;
            _dragAnchor = hit.Anchor;
            _dragMouseStart = e.Location;
            _dragMoved = false;
            GuideSelected?.Invoke(_guide);
            CaptureDragBaseValues();
            _host.BeginGuideWarpEdit();
            // Cursors.Hand has no in-repo confirmed-compiling precedent; Cursors.SizeAll is used
            // instead, the same build-confirmed substitution already made for the Painter's
            // texture-handle drags (see the class doc comment above).
            Cursor = Cursors.SizeAll;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_panning)
            {
                _pan = new PointF(
                    _panOrigin.X + e.Location.X - _panStart.X,
                    _panOrigin.Y + e.Location.Y - _panStart.Y);
                Invalidate();
                return;
            }
            if (_selectingRegion)
            {
                _selectionCurrent = e.Location;
                Invalidate();
                return;
            }
            if (_dragAnchor < 0)
            {
                Cursor = HitHandle(e.Location) ? Cursors.SizeAll : Cursors.Default;
                return;
            }
            if (!_dragMoved)
            {
                float dx = e.Location.X - _dragMouseStart.X;
                float dy = e.Location.Y - _dragMouseStart.Y;
                _dragMoved = dx * dx + dy * dy >= 9;
                if (!_dragMoved)
                    return;
            }
            PreviewGroupDrag(e.Location);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_panning && (e.Buttons == MouseButtons.Middle || e.Buttons == MouseButtons.Alternate))
            {
                _panning = false;
                Cursor = Cursors.Default;
                return;
            }
            if (_selectingRegion && e.Buttons == MouseButtons.Primary)
            {
                _selectionCurrent = e.Location;
                ApplyRegionSelection();
                _selectingRegion = false;
                Cursor = Cursors.Default;
                Invalidate();
                return;
            }
            if (_dragAnchor < 0)
                return;
            if (_dragMoved)
                PreviewGroupDrag(e.Location);
            _dragAnchor = -1;
            _dragGuide = -1;
            _dragBaseValues.Clear();
            _dragGuideBaseValues.Clear();
            if (_dragMoved)
                _host.CommitGuideWarpEdit();
            else
                _host.CancelGuideWarpEdit();
            _dragMoved = false;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key != Keys.Escape)
                return;
            if (_dragAnchor >= 0)
            {
                _dragAnchor = -1;
                _dragGuide = -1;
                _dragMoved = false;
                _dragBaseValues.Clear();
                _dragGuideBaseValues.Clear();
                _host.CancelGuideWarpEdit();
            }
            _selectingRegion = false;
            _selectedAnchors.Clear();
            Cursor = Cursors.Default;
            e.Handled = true;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float factor = e.Delta.Height > 0 ? 1.18f : 1f / 1.18f;
            _zoom = Math.Max(0.2f, Math.Min(20f, _zoom * factor));
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            _zoom = 1.0f;
            _pan = PointF.Empty;
            Invalidate();
        }

        private void CaptureDragBaseValues()
        {
            _dragBaseValues.Clear();
            _dragGuideBaseValues.Clear();
            foreach (IGrouping<int, GuideAnchorKey> group in _selectedAnchors.GroupBy(key => key.Guide))
            {
                List<double> values = _host.GetGuideWarp(group.Key)?.ToList() ?? new List<double>();
                if (values.Count < 3)
                    continue;
                _dragGuideBaseValues[group.Key] = values;
                foreach (GuideAnchorKey key in group)
                    if (key.Anchor > 0 && key.Anchor < values.Count - 1)
                        _dragBaseValues[key] = values[key.Anchor];
            }
        }

        private void PreviewGroupDrag(PointF location)
        {
            var dragged = new GuideAnchorKey(_dragGuide, _dragAnchor);
            if (!_dragBaseValues.TryGetValue(dragged, out double draggedStart) ||
                _dragGuide < 0 || _dragGuide >= _screenCurves.Count)
                return;
            double requestedDelta = ClosestU(_screenCurves[_dragGuide], location) - draggedStart;
            double minimumDelta = double.NegativeInfinity;
            double maximumDelta = double.PositiveInfinity;
            const double minimumGap = 0.0025;

            foreach (KeyValuePair<int, List<double>> guide in _dragGuideBaseValues)
            {
                HashSet<int> selected = _dragBaseValues.Keys
                    .Where(key => key.Guide == guide.Key)
                    .Select(key => key.Anchor)
                    .ToHashSet();
                List<double> values = guide.Value;
                foreach (int anchor in selected)
                {
                    if (!selected.Contains(anchor - 1))
                        minimumDelta = Math.Max(minimumDelta, values[anchor - 1] + minimumGap - values[anchor]);
                    if (!selected.Contains(anchor + 1))
                        maximumDelta = Math.Min(maximumDelta, values[anchor + 1] - minimumGap - values[anchor]);
                }
            }

            double delta = Math.Max(minimumDelta, Math.Min(maximumDelta, requestedDelta));
            foreach (IGrouping<int, KeyValuePair<GuideAnchorKey, double>> group in
                _dragBaseValues.GroupBy(pair => pair.Key.Guide))
            {
                IReadOnlyList<double> current = _host.GetGuideWarp(group.Key);
                KeyValuePair<GuideAnchorKey, double> reference = group.First();
                double currentValue = reference.Key.Anchor < current.Count
                    ? current[reference.Key.Anchor]
                    : reference.Value;
                bool movingForward = reference.Value + delta >= currentValue;
                IEnumerable<KeyValuePair<GuideAnchorKey, double>> ordered = movingForward
                    ? group.OrderByDescending(pair => pair.Key.Anchor)
                    : group.OrderBy(pair => pair.Key.Anchor);
                foreach (KeyValuePair<GuideAnchorKey, double> edit in ordered)
                    _host.PreviewGuideWarpAnchor(edit.Key.Guide, edit.Key.Anchor, edit.Value + delta);
            }
        }

        private GuideAnchorKey HitGuideAnchor(PointF location)
        {
            double best = 16.0;
            GuideAnchorKey hit = GuideAnchorKey.Invalid;
            for (int guide = 0; guide < _screenCurves.Count; guide++)
            {
                IReadOnlyList<double> values = _host.GetGuideWarp(guide);
                for (int anchor = 1; anchor < values.Count - 1; anchor++)
                {
                    double distance = Distance(PointAt(_screenCurves[guide], values[anchor]), location);
                    if (distance >= best)
                        continue;
                    best = distance;
                    hit = new GuideAnchorKey(guide, anchor);
                }
            }
            return hit;
        }

        private RectangleF SelectionRectangle()
        {
            float left = Math.Min(_selectionStart.X, _selectionCurrent.X);
            float top = Math.Min(_selectionStart.Y, _selectionCurrent.Y);
            return new RectangleF(
                left,
                top,
                Math.Abs(_selectionCurrent.X - _selectionStart.X),
                Math.Abs(_selectionCurrent.Y - _selectionStart.Y));
        }

        private void ApplyRegionSelection()
        {
            RectangleF region = SelectionRectangle();
            if (!_addRegionSelection)
                _selectedAnchors.Clear();
            if (region.Width < 2 && region.Height < 2)
                return;
            for (int guide = 0; guide < _screenCurves.Count; guide++)
            {
                IReadOnlyList<double> values = _host.GetGuideWarp(guide);
                for (int anchor = 1; anchor < values.Count - 1; anchor++)
                    if (region.Contains(PointAt(_screenCurves[guide], values[anchor])))
                        _selectedAnchors.Add(new GuideAnchorKey(guide, anchor));
            }
        }

        private void BuildScreenCurves()
        {
            _screenCurves.Clear();
            IReadOnlyList<IReadOnlyList<System.Drawing.PointF>> source = _host.GuideEditorCurves;
            if (source == null || source.Count == 0)
                return;
            List<System.Drawing.PointF> sourcePoints = source
                .Where(curve => curve != null)
                .SelectMany(curve => curve)
                .ToList();
            if (sourcePoints.Count < 2)
                return;
            var origin = new System.Drawing.PointF(
                sourcePoints.Average(point => point.X),
                sourcePoints.Average(point => point.Y));
            var rotated = source.Select(curve =>
                (IReadOnlyList<System.Drawing.PointF>)(curve ?? Array.Empty<System.Drawing.PointF>())
                    .Select(point => RotatePoint(point, origin))
                    .ToArray()).ToList();
            var points = rotated.SelectMany(curve => curve).ToList();
            if (points.Count < 2)
                return;
            float minX = points.Min(p => p.X);
            float maxX = points.Max(p => p.X);
            float minY = points.Min(p => p.Y);
            float maxY = points.Max(p => p.Y);
            float spanX = Math.Max(1e-4f, maxX - minX);
            float spanY = Math.Max(1e-4f, maxY - minY);
            var area = new RectangleF(55, 35, Math.Max(100, Width - 110), Math.Max(100, Height - 115));
            float baseScale = Math.Min(area.Width / spanX, area.Height / spanY);
            float scale = baseScale * _zoom;
            float drawW = spanX * scale;
            float drawH = spanY * scale;
            float offsetX = area.Left + 0.5f * (area.Width - drawW) + _pan.X;
            float offsetY = area.Top + 0.5f * (area.Height - drawH) + _pan.Y;
            foreach (IReadOnlyList<System.Drawing.PointF> curve in rotated)
            {
                if (curve == null || curve.Count < 2)
                {
                    _screenCurves.Add(Array.Empty<System.Drawing.PointF>());
                    continue;
                }
                _screenCurves.Add(curve.Select(point => new System.Drawing.PointF(
                    offsetX + (point.X - minX) * scale,
                    offsetY + (maxY - point.Y) * scale)).ToArray());
            }
        }

        private System.Drawing.PointF RotatePoint(System.Drawing.PointF point, System.Drawing.PointF origin)
        {
            float x = point.X - origin.X;
            float y = point.Y - origin.Y;
            System.Drawing.PointF rotated = _quarterTurns switch
            {
                1 => new System.Drawing.PointF(-y, x),
                2 => new System.Drawing.PointF(-x, -y),
                3 => new System.Drawing.PointF(y, -x),
                _ => new System.Drawing.PointF(x, y)
            };
            return new System.Drawing.PointF(rotated.X + origin.X, rotated.Y + origin.Y);
        }

        private bool HitHandle(PointF location) => HitGuideAnchor(location).IsValid;

        private static PointF ToEtoPoint(System.Drawing.PointF point) => new PointF(point.X, point.Y);

        private static PointF[] ToEtoPoints(System.Drawing.PointF[] curve)
        {
            var result = new PointF[curve.Length];
            for (int i = 0; i < curve.Length; i++)
                result[i] = ToEtoPoint(curve[i]);
            return result;
        }

        private static PointF PointAt(System.Drawing.PointF[] curve, double u)
        {
            if (curve == null || curve.Length == 0)
                return PointF.Empty;
            if (curve.Length == 1)
                return ToEtoPoint(curve[0]);
            double position = Math.Max(0.0, Math.Min(1.0, u)) * (curve.Length - 1);
            int index = Math.Min(curve.Length - 2, Math.Max(0, (int)Math.Floor(position)));
            double local = position - index;
            return new PointF(
                (float)(curve[index].X + (curve[index + 1].X - curve[index].X) * local),
                (float)(curve[index].Y + (curve[index + 1].Y - curve[index].Y) * local));
        }

        private static double ClosestU(System.Drawing.PointF[] curve, PointF point)
        {
            if (curve == null || curve.Length < 2)
                return 0.0;
            double bestDistance = double.MaxValue;
            double bestU = 0.0;
            for (int i = 0; i < curve.Length - 1; i++)
            {
                double ax = curve[i].X;
                double ay = curve[i].Y;
                double bx = curve[i + 1].X;
                double by = curve[i + 1].Y;
                double dx = bx - ax;
                double dy = by - ay;
                double lengthSquared = dx * dx + dy * dy;
                double t = lengthSquared <= 1e-12
                    ? 0.0
                    : ((point.X - ax) * dx + (point.Y - ay) * dy) / lengthSquared;
                t = Math.Max(0.0, Math.Min(1.0, t));
                double px = ax + dx * t;
                double py = ay + dy * t;
                double distance = (point.X - px) * (point.X - px) + (point.Y - py) * (point.Y - py);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestU = (i + t) / (curve.Length - 1);
                }
            }
            return bestU;
        }

        private static double Distance(PointF a, PointF b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private readonly struct GuideAnchorKey : IEquatable<GuideAnchorKey>
        {
            internal static readonly GuideAnchorKey Invalid = new GuideAnchorKey(-1, -1);
            internal readonly int Guide;
            internal readonly int Anchor;
            internal bool IsValid => Guide >= 0 && Anchor >= 0;

            internal GuideAnchorKey(int guide, int anchor)
            {
                Guide = guide;
                Anchor = anchor;
            }

            public bool Equals(GuideAnchorKey other) => Guide == other.Guide && Anchor == other.Anchor;

            public override bool Equals(object obj) => obj is GuideAnchorKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(Guide, Anchor);
        }
    }

    internal sealed class WasperEtoShellSeamCanvas : Drawable
    {
        private readonly IWasperShellSeamEditorHost _host;
        private readonly List<System.Drawing.PointF[]> _screenCurves = new List<System.Drawing.PointF[]>();
        private readonly List<System.Drawing.PointF[]> _screenPartitions = new List<System.Drawing.PointF[]>();
        private float _zoom = 1.0f;
        private PointF _pan = PointF.Empty;
        private bool _panning;
        private PointF _panStart;
        private PointF _panOrigin;
        private int _quarterTurns;
        private int _dragHandle = -1; // 0 seam, 1 start, 2 end
        private float _screenScale = 1.0f;

        // See WasperEtoGuideWarpCanvas's matching field: Drawable has no inherited Font property.
        private readonly Font _uiFont = new Font(FontFamilies.Sans, 9f);

        internal WasperEtoShellSeamCanvas(IWasperShellSeamEditorHost host)
        {
            _host = host;
            CanFocus = true;
            BackgroundColor = Color.FromArgb(250, 250, 250);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _uiFont?.Dispose();
            base.Dispose(disposing);
        }

        internal void RotateClockwise()
        {
            _quarterTurns = (_quarterTurns + 1) % 4;
            _zoom = 1.0f;
            _pan = PointF.Empty;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            BuildScreenCurves();
            Graphics g = e.Graphics;
            g.AntiAlias = true;
            g.Clear(BackgroundColor);
            using var shellPen = new Pen(Color.FromArgb(55, 105, 85), 2.2f);
            using var partitionPen = new Pen(Color.FromArgb(125, 125, 125), 1.5f) { DashStyle = DashStyles.Dash };
            using var armPen = new Pen(Color.FromArgb(205, 105, 35), 2.8f);
            using var seamBrush = new SolidBrush(Color.FromArgb(225, 120, 25));
            using var startBrush = new SolidBrush(Color.FromArgb(55, 145, 190));
            using var endBrush = new SolidBrush(Color.FromArgb(185, 75, 135));
            using var textBrush = new SolidBrush(Color.FromArgb(75, 75, 75));
            using var whitePen = new Pen(Colors.White, 1f);

            if (_screenCurves.Count == 0)
            {
                g.DrawText(_uiFont, textBrush, 30, 30, "Enable shell output to edit its seam.");
                return;
            }
            foreach (System.Drawing.PointF[] partition in _screenPartitions)
                if (partition.Length >= 2)
                    g.DrawLines(partitionPen, ToEtoPoints(partition));
            foreach (System.Drawing.PointF[] curve in _screenCurves)
                if (curve.Length >= 2)
                    g.DrawLines(shellPen, ToEtoPoints(curve));

            System.Drawing.PointF[] active = _screenCurves.FirstOrDefault(curve => curve.Length >= 2);
            if (active == null)
                return;
            WasperShellSeamSettings settings = _host.ShellSeamSettings ?? new WasperShellSeamSettings();
            PointF seam = PointAt(active, Wrap01(settings.SeamU));
            DrawHandle(g, seamBrush, whitePen, seam, 9f);
            g.DrawText(_uiFont, textBrush, seam.X + 10f, seam.Y + 8f, "Seam");

            if (settings.XSeam)
            {
                PointF inward = InwardNormal(active, settings.SeamU);
                PointF tangent = Tangent(active, settings.SeamU);
                double length = Math.Max(1e-9, PolylineLength(active) / Math.Max(1e-9f, _screenScale));
                double du = Math.Min(0.24, Math.Max(0.0, settings.FilletRadius) / length);
                PointF attachStart = PointAt(active, Wrap01(settings.SeamU + du));
                PointF attachEnd = PointAt(active, Wrap01(settings.SeamU - du));
                PointF start = Add(Add(seam, inward, settings.StartOffset * _screenScale), tangent, settings.StartTangentialOffset * _screenScale);
                PointF end = Add(Add(seam, inward, settings.EndOffset * _screenScale), tangent, settings.EndTangentialOffset * _screenScale);
                g.DrawLine(armPen, start, attachStart);
                g.DrawLine(armPen, attachEnd, end);
                DrawHandle(g, startBrush, whitePen, start, 8f);
                DrawHandle(g, endBrush, whitePen, end, 8f);
                g.DrawText(_uiFont, textBrush, start.X + 9f, start.Y - 18f, "Start");
                g.DrawText(_uiFont, textBrush, end.X + 9f, end.Y + 5f, "End");
            }

            g.DrawText(
                _uiFont,
                textBrush,
                22,
                Height - 48,
                "Shell seam editor · dashed grey = partitions · orange = seam · blue/magenta = X endpoints\n" +
                "Drag seam along curve · drag endpoints freely · wheel zooms at cursor · middle/right pan.");
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Buttons == MouseButtons.Middle || e.Buttons == MouseButtons.Alternate)
            {
                _panning = true;
                _panStart = e.Location;
                _panOrigin = _pan;
                Cursor = Cursors.Move;
                return;
            }
            if (e.Buttons != MouseButtons.Primary || _screenCurves.Count == 0)
                return;
            System.Drawing.PointF[] active = _screenCurves.FirstOrDefault(curve => curve.Length >= 2);
            if (active == null)
                return;
            WasperShellSeamSettings settings = _host.ShellSeamSettings ?? new WasperShellSeamSettings();
            PointF seam = PointAt(active, Wrap01(settings.SeamU));
            _dragHandle = Distance(seam, e.Location) <= 16.0 ? 0 : -1;
            if (settings.XSeam)
            {
                PointF inward = InwardNormal(active, settings.SeamU);
                PointF tangent = Tangent(active, settings.SeamU);
                PointF start = Add(Add(seam, inward, settings.StartOffset * _screenScale), tangent, settings.StartTangentialOffset * _screenScale);
                PointF end = Add(Add(seam, inward, settings.EndOffset * _screenScale), tangent, settings.EndTangentialOffset * _screenScale);
                if (Distance(start, e.Location) <= 16.0) _dragHandle = 1;
                else if (Distance(end, e.Location) <= 16.0) _dragHandle = 2;
            }
            if (_dragHandle < 0)
                return;
            _host.BeginShellSeamEdit();
            // See the class doc comment above: Cursors.Hand is not confirmed to compile, SizeAll is.
            Cursor = Cursors.SizeAll;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_panning)
            {
                _pan = new PointF(
                    _panOrigin.X + e.Location.X - _panStart.X,
                    _panOrigin.Y + e.Location.Y - _panStart.Y);
                Invalidate();
                return;
            }
            if (_dragHandle < 0 || _screenCurves.Count == 0)
                return;
            System.Drawing.PointF[] active = _screenCurves.First(curve => curve.Length >= 2);
            WasperShellSeamSettings settings = _host.ShellSeamSettings ?? new WasperShellSeamSettings();
            if (_dragHandle == 0)
                _host.PreviewShellSeam(ClosestU(active, e.Location));
            else
            {
                PointF seam = PointAt(active, Wrap01(settings.SeamU));
                PointF inward = InwardNormal(active, settings.SeamU);
                PointF tangent = Tangent(active, settings.SeamU);
                double inwardOffset = ((e.Location.X - seam.X) * inward.X + (e.Location.Y - seam.Y) * inward.Y) /
                    Math.Max(1e-9f, _screenScale);
                double tangentialOffset = ((e.Location.X - seam.X) * tangent.X + (e.Location.Y - seam.Y) * tangent.Y) /
                    Math.Max(1e-9f, _screenScale);
                _host.PreviewShellOffset(_dragHandle == 1, inwardOffset, tangentialOffset);
            }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_panning)
            {
                _panning = false;
                Cursor = Cursors.Default;
                return;
            }
            if (_dragHandle < 0)
                return;
            OnMouseMove(e);
            _dragHandle = -1;
            Cursor = Cursors.Default;
            _host.CommitShellSeamEdit();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float oldZoom = _zoom;
            _zoom = Math.Max(0.2f, Math.Min(20f, _zoom * (e.Delta.Height > 0 ? 1.18f : 1f / 1.18f)));
            float factor = _zoom / Math.Max(1e-9f, oldZoom);
            var centre = new PointF(Width * 0.5f, Height * 0.5f);
            _pan = new PointF(
                e.Location.X - centre.X - factor * (e.Location.X - centre.X - _pan.X),
                e.Location.Y - centre.Y - factor * (e.Location.Y - centre.Y - _pan.Y));
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            _zoom = 1.0f;
            _pan = PointF.Empty;
            Invalidate();
        }

        private void BuildScreenCurves()
        {
            _screenCurves.Clear();
            _screenPartitions.Clear();
            IReadOnlyList<IReadOnlyList<System.Drawing.PointF>> source = _host.ShellEditorCurves;
            IReadOnlyList<IReadOnlyList<System.Drawing.PointF>> partitionSource =
                _host.ShellPartitionEditorCurves ?? Array.Empty<IReadOnlyList<System.Drawing.PointF>>();
            if (source == null || source.Count == 0)
                return;
            List<System.Drawing.PointF> all = source
                .Concat(partitionSource)
                .Where(curve => curve != null)
                .SelectMany(curve => curve)
                .ToList();
            if (all.Count < 2)
                return;
            var origin = new System.Drawing.PointF(all.Average(point => point.X), all.Average(point => point.Y));
            var rotated = source.Select(curve =>
                (IReadOnlyList<System.Drawing.PointF>)(curve ?? Array.Empty<System.Drawing.PointF>())
                    .Select(point => RotatePoint(point, origin)).ToArray()).ToList();
            var rotatedPartitions = partitionSource.Select(curve =>
                (IReadOnlyList<System.Drawing.PointF>)(curve ?? Array.Empty<System.Drawing.PointF>())
                    .Select(point => RotatePoint(point, origin)).ToArray()).ToList();
            List<System.Drawing.PointF> points = rotated.Concat(rotatedPartitions).SelectMany(curve => curve).ToList();
            float minX = points.Min(point => point.X);
            float maxX = points.Max(point => point.X);
            float minY = points.Min(point => point.Y);
            float maxY = points.Max(point => point.Y);
            float spanX = Math.Max(1e-4f, maxX - minX);
            float spanY = Math.Max(1e-4f, maxY - minY);
            var area = new RectangleF(55, 35, Math.Max(100, Width - 110), Math.Max(100, Height - 115));
            _screenScale = Math.Min(area.Width / spanX, area.Height / spanY) * _zoom;
            float offsetX = area.Left + 0.5f * (area.Width - spanX * _screenScale) + _pan.X;
            float offsetY = area.Top + 0.5f * (area.Height - spanY * _screenScale) + _pan.Y;
            foreach (IReadOnlyList<System.Drawing.PointF> curve in rotated)
                _screenCurves.Add(curve.Select(point => new System.Drawing.PointF(
                    offsetX + (point.X - minX) * _screenScale,
                    offsetY + (maxY - point.Y) * _screenScale)).ToArray());
            foreach (IReadOnlyList<System.Drawing.PointF> curve in rotatedPartitions)
                _screenPartitions.Add(curve.Select(point => new System.Drawing.PointF(
                    offsetX + (point.X - minX) * _screenScale,
                    offsetY + (maxY - point.Y) * _screenScale)).ToArray());
        }

        private System.Drawing.PointF RotatePoint(System.Drawing.PointF point, System.Drawing.PointF origin)
        {
            float x = point.X - origin.X;
            float y = point.Y - origin.Y;
            System.Drawing.PointF rotated = _quarterTurns switch
            {
                1 => new System.Drawing.PointF(-y, x),
                2 => new System.Drawing.PointF(-x, -y),
                3 => new System.Drawing.PointF(y, -x),
                _ => new System.Drawing.PointF(x, y)
            };
            return new System.Drawing.PointF(rotated.X + origin.X, rotated.Y + origin.Y);
        }

        private static PointF ToEtoPoint(System.Drawing.PointF point) => new PointF(point.X, point.Y);

        private static PointF[] ToEtoPoints(System.Drawing.PointF[] curve)
        {
            var result = new PointF[curve.Length];
            for (int i = 0; i < curve.Length; i++)
                result[i] = ToEtoPoint(curve[i]);
            return result;
        }

        private static PointF InwardNormal(System.Drawing.PointF[] curve, double u)
        {
            double du = 1.0 / Math.Max(16.0, curve.Length * 2.0);
            PointF before = PointAt(curve, Wrap01(u - du));
            PointF after = PointAt(curve, Wrap01(u + du));
            float tx = after.X - before.X;
            float ty = after.Y - before.Y;
            float length = (float)Math.Sqrt(tx * tx + ty * ty);
            if (length <= 1e-6f) return new PointF(0f, -1f);
            var normal = new PointF(-ty / length, tx / length);
            PointF seam = PointAt(curve, Wrap01(u));
            var center = new PointF(curve.Average(point => point.X), curve.Average(point => point.Y));
            if ((center.X - seam.X) * normal.X + (center.Y - seam.Y) * normal.Y < 0f)
                normal = new PointF(-normal.X, -normal.Y);
            return normal;
        }

        private static PointF Tangent(System.Drawing.PointF[] curve, double u)
        {
            double du = 1.0 / Math.Max(16.0, curve.Length * 2.0);
            PointF before = PointAt(curve, Wrap01(u - du));
            PointF after = PointAt(curve, Wrap01(u + du));
            float tx = after.X - before.X;
            float ty = after.Y - before.Y;
            float length = (float)Math.Sqrt(tx * tx + ty * ty);
            return length <= 1e-6f ? new PointF(1f, 0f) : new PointF(tx / length, ty / length);
        }

        private static PointF Add(PointF point, PointF direction, double distance) =>
            new PointF((float)(point.X + direction.X * distance), (float)(point.Y + direction.Y * distance));

        private static void DrawHandle(Graphics graphics, Brush brush, Pen outline, PointF point, float radius)
        {
            graphics.FillEllipse(brush, point.X - radius, point.Y - radius, radius * 2f, radius * 2f);
            graphics.DrawEllipse(outline, point.X - radius, point.Y - radius, radius * 2f, radius * 2f);
        }

        private static double PolylineLength(System.Drawing.PointF[] curve)
        {
            double length = 0.0;
            for (int i = 1; i < curve.Length; i++)
            {
                double dx = curve[i].X - curve[i - 1].X;
                double dy = curve[i].Y - curve[i - 1].Y;
                length += Math.Sqrt(dx * dx + dy * dy);
            }
            return length;
        }

        private static PointF PointAt(System.Drawing.PointF[] curve, double u)
        {
            if (curve == null || curve.Length == 0) return PointF.Empty;
            if (curve.Length == 1) return ToEtoPoint(curve[0]);
            double total = PolylineLength(curve);
            if (total <= 1e-12) return ToEtoPoint(curve[0]);
            double target = Math.Max(0.0, Math.Min(1.0, u)) * total;
            double travelled = 0.0;
            for (int i = 0; i < curve.Length - 1; i++)
            {
                double dx = curve[i + 1].X - curve[i].X;
                double dy = curve[i + 1].Y - curve[i].Y;
                double segment = Math.Sqrt(dx * dx + dy * dy);
                if (travelled + segment >= target || i == curve.Length - 2)
                {
                    double local = segment <= 1e-12 ? 0.0 : (target - travelled) / segment;
                    local = Math.Max(0.0, Math.Min(1.0, local));
                    return new PointF(
                        (float)(curve[i].X + dx * local),
                        (float)(curve[i].Y + dy * local));
                }
                travelled += segment;
            }
            return ToEtoPoint(curve[curve.Length - 1]);
        }

        private static double ClosestU(System.Drawing.PointF[] curve, PointF point)
        {
            double totalLength = PolylineLength(curve);
            if (totalLength <= 1e-12)
                return 0.0;
            double bestDistance = double.MaxValue;
            double bestU = 0.0;
            double travelled = 0.0;
            for (int i = 0; i < curve.Length - 1; i++)
            {
                double ax = curve[i].X, ay = curve[i].Y;
                double dx = curve[i + 1].X - ax, dy = curve[i + 1].Y - ay;
                double lengthSquared = dx * dx + dy * dy;
                double segmentLength = Math.Sqrt(lengthSquared);
                double t = lengthSquared <= 1e-12 ? 0.0 :
                    ((point.X - ax) * dx + (point.Y - ay) * dy) / lengthSquared;
                t = Math.Max(0.0, Math.Min(1.0, t));
                double px = ax + dx * t, py = ay + dy * t;
                double distance = (point.X - px) * (point.X - px) + (point.Y - py) * (point.Y - py);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestU = (travelled + segmentLength * t) / totalLength;
                }
                travelled += segmentLength;
            }
            return bestU;
        }

        private static double Distance(PointF a, PointF b)
        {
            double dx = a.X - b.X, dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static double Wrap01(double value)
        {
            value -= Math.Floor(value);
            return value < 0.0 ? value + 1.0 : value;
        }
    }
}
