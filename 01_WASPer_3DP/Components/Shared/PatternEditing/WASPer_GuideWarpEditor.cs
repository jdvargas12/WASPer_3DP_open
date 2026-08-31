using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace WASPer_3DP.PatternEditing
{
    internal sealed class WasperGuideWarpState
    {
        internal const int DefaultAnchorCount = 5;
        private readonly Dictionary<int, List<double>> _domains =
            new Dictionary<int, List<double>>();

        internal IReadOnlyList<double> Get(int guide, int anchorCount = DefaultAnchorCount)
        {
            return Get(
                guide,
                Enumerable.Range(0, Math.Max(2, anchorCount))
                    .Select(i => (double)i / (Math.Max(2, anchorCount) - 1))
                    .ToArray());
        }

        internal IReadOnlyList<double> Get(int guide, IReadOnlyList<double> sourceStations)
        {
            IReadOnlyList<double> source = sourceStations != null && sourceStations.Count >= 2
                ? sourceStations
                : new[] { 0.0, 1.0 };
            if (!_domains.TryGetValue(Math.Max(0, guide), out List<double> values))
                return source.ToArray();
            if (values.Count == source.Count)
                return values;
            return source.Select(u => MapUniform(values, u)).ToArray();
        }

        internal void SetAnchor(int guide, int anchorCount, int index, double value)
        {
            SetAnchor(
                guide,
                Enumerable.Range(0, Math.Max(2, anchorCount))
                    .Select(i => (double)i / (Math.Max(2, anchorCount) - 1))
                    .ToArray(),
                index,
                value);
        }

        internal void SetAnchor(
            int guide,
            IReadOnlyList<double> sourceStations,
            int index,
            double value)
        {
            List<double> values = Get(guide, sourceStations).ToList();
            if (index <= 0 || index >= values.Count - 1)
                return;
            const double minimumGap = 0.0025;
            values[index] = Math.Max(
                values[index - 1] + minimumGap,
                Math.Min(values[index + 1] - minimumGap, value));
            _domains[Math.Max(0, guide)] = values;
        }

        internal void Reset(int guide) => _domains.Remove(Math.Max(0, guide));

        internal Dictionary<int, List<double>> Snapshot() => _domains.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToList());

        internal void Restore(Dictionary<int, List<double>> snapshot)
        {
            _domains.Clear();
            if (snapshot == null)
                return;
            foreach (KeyValuePair<int, List<double>> pair in snapshot)
            {
                List<double> clean = Validate(pair.Value);
                if (pair.Key >= 0 && clean != null)
                    _domains[pair.Key] = clean;
            }
        }

        private static List<double> Validate(IList<double> source)
        {
            if (source == null || source.Count < 2)
                return null;
            var values = source.Select(v => Math.Max(0.0, Math.Min(1.0, v))).ToList();
            values[0] = 0.0;
            values[values.Count - 1] = 1.0;
            for (int i = 1; i < values.Count; i++)
                if (values[i] <= values[i - 1])
                    return null;
            return values;
        }

        private static double MapUniform(IReadOnlyList<double> values, double u)
        {
            double position = Math.Max(0.0, Math.Min(1.0, u)) * (values.Count - 1);
            int index = Math.Min(values.Count - 2, Math.Max(0, (int)Math.Floor(position)));
            double local = position - index;
            return values[index] + (values[index + 1] - values[index]) * local;
        }
    }

    public enum WasperGuideLayerScope
    {
        All = 0,
        Range = 1,
        Single = 2
    }

    public sealed class WasperShellSeamSettings
    {
        public double SeamU { get; set; }
        public bool XSeam { get; set; }
        public double StartOffset { get; set; }
        public double EndOffset { get; set; }
        public double StartTangentialOffset { get; set; }
        public double EndTangentialOffset { get; set; }
        public double FilletRadius { get; set; }

        public WasperShellSeamSettings Clone() => new WasperShellSeamSettings
        {
            SeamU = SeamU,
            XSeam = XSeam,
            StartOffset = StartOffset,
            EndOffset = EndOffset,
            StartTangentialOffset = StartTangentialOffset,
            EndTangentialOffset = EndTangentialOffset,
            FilletRadius = FilletRadius
        };
    }

    public interface IWasperShellSeamEditorHost
    {
        string GuideEditorTitle { get; }
        int GuideVisualRevision { get; }
        IReadOnlyList<IReadOnlyList<PointF>> ShellEditorCurves { get; }
        IReadOnlyList<IReadOnlyList<PointF>> ShellPartitionEditorCurves { get; }
        WasperShellSeamSettings ShellSeamSettings { get; }
        int GuideLayerCount { get; }
        WasperGuideLayerScope GuideLayerScope { get; }
        int GuideLayerFrom { get; }
        int GuideLayerTo { get; }
        int GuideDisplayLayer { get; }
        void SetGuideLayerScope(
            WasperGuideLayerScope scope,
            int fromLayer,
            int toLayer,
            int displayLayer);
        void GuideEditorClosed();
        void BeginShellSeamEdit();
        void PreviewShellSeam(double seamU);
        void PreviewShellOffset(
            bool startPoint,
            double inwardOffset,
            double tangentialOffset);
        void CommitShellSeamEdit();
        void CancelShellSeamEdit();
        void SetShellXSeam(bool enabled);
        void SetShellFilletRadius(double radius);
        void ResetShellSeam();
    }

    public interface IWasperGuideWarpEditorHost : IWasperShellSeamEditorHost
    {
        bool GuideLiveEnabled { get; }
        bool HasPendingGuideUpdate { get; }
        void ToggleGuideLive();
        void ApplyPendingGuideUpdate();
        int GuideDomainCount { get; }
        IReadOnlyList<IReadOnlyList<PointF>> GuideEditorCurves { get; }
        int GetGuideAnchorCount(int guide);
        IReadOnlyList<double> GetGuideSourceStations(int guide);
        IReadOnlyList<double> GetGuideWarp(int domain);
        bool IsGuidePrimaryStation(int guide, int stationIndex);
        void SelectGuide(int guide);
        bool GuideSupportsControlDensity(int guide);
        int GetGuideControlDensity(int guide);
        bool HasGuideControlDensityOverride(int guide);
        void SetGuideControlDensity(int guide, int density);
        void ResetGuideControlDensity(int guide);
        void BeginGuideWarpEdit();
        void PreviewGuideWarpAnchor(int domain, int anchor, double value);
        void CommitGuideWarpEdit();
        void CancelGuideWarpEdit();
        bool CanUndoGuideWarp { get; }
        bool CanRedoGuideWarp { get; }
        void UndoGuideWarp();
        void RedoGuideWarp();
        void ResetGuideWarp(int domain);
        void ResetAllGuideWarps();
    }

    internal sealed class WasperShellSeamEditorForm : Form
    {
        private readonly IWasperShellSeamEditorHost _host;
        private readonly WasperShellSeamCanvas _canvas;
        private readonly ComboBox _scope;
        private readonly NumericUpDown _from;
        private readonly NumericUpDown _to;
        private readonly NumericUpDown _display;
        private readonly CheckBox _xSeam;
        private readonly NumericUpDown _fillet;
        private readonly Timer _timer;
        private bool _syncing;
        private int _revision = -1;

        internal WasperShellSeamEditorForm(IWasperShellSeamEditorHost host)
        {
            _host = host;
            // Declares that these pixel sizes were authored at 96 DPI. Without the paired
            // AutoScaleDimensions, AutoScaleMode.Dpi has no baseline to scale from, so hand-coded
            // heights stay at their literal pixel values and every toolbar row is squeezed on a
            // high-DPI monitor while its text renders larger.
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = host.GuideEditorTitle;
            TopMost = true;
            ShowInTaskbar = true;
            MinimizeBox = true;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(640, 400);
            ClientSize = new Size(900, 600);

            var scopeBar = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                WrapContents = false,
                Padding = new Padding(6, 4, 6, 3)
            };
            scopeBar.Controls.Add(LabelFor("Layer scope:"));
            _scope = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110 };
            _scope.Items.AddRange(new object[] { "All layers", "Range", "Single layer" });
            _scope.SelectedIndexChanged += (_, __) => CommitScope();
            scopeBar.Controls.Add(_scope);
            scopeBar.Controls.Add(LabelFor("From:"));
            _from = Number(); _from.ValueChanged += (_, __) => CommitScope();
            scopeBar.Controls.Add(_from);
            scopeBar.Controls.Add(LabelFor("To:"));
            _to = Number(); _to.ValueChanged += (_, __) => CommitScope();
            scopeBar.Controls.Add(_to);
            scopeBar.Controls.Add(LabelFor("Display layer:"));
            _display = Number(); _display.ValueChanged += (_, __) => CommitScope();
            scopeBar.Controls.Add(_display);

            var seamBar = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                WrapContents = false,
                Padding = new Padding(6, 4, 6, 3)
            };
            seamBar.Controls.Add(LabelFor("Shell seam:"));
            _xSeam = new CheckBox { Text = "X seam", AutoSize = true, Margin = new Padding(4, 6, 8, 0) };
            _xSeam.CheckedChanged += (_, __) => { if (!_syncing) _host.SetShellXSeam(_xSeam.Checked); };
            seamBar.Controls.Add(_xSeam);
            seamBar.Controls.Add(LabelFor("Fillet radius:"));
            _fillet = new NumericUpDown
            {
                DecimalPlaces = 3,
                Increment = 0.5M,
                Minimum = 0,
                Maximum = 1000000,
                Width = 90
            };
            _fillet.ValueChanged += (_, __) => { if (!_syncing) _host.SetShellFilletRadius((double)_fillet.Value); };
            seamBar.Controls.Add(_fillet);
            seamBar.Controls.Add(Button("Reset seam", () => _host.ResetShellSeam(), 95));
            seamBar.Controls.Add(Button("Rotate 90°", () => _canvas.RotateClockwise(), 90));

            _canvas = new WasperShellSeamCanvas(host) { Dock = DockStyle.Fill };
            Controls.Add(_canvas);
            Controls.Add(seamBar);
            Controls.Add(scopeBar);
            Sync();

            _timer = new Timer { Interval = 100 };
            _timer.Tick += (_, __) => RefreshFromHost();
            _timer.Start();
            FormClosed += (_, __) =>
            {
                _timer.Dispose();
                _host.GuideEditorClosed();
            };
        }

        internal void ActivateEditor()
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
        }

        private void RefreshFromHost()
        {
            if (_revision == _host.GuideVisualRevision) return;
            _revision = _host.GuideVisualRevision;
            Sync();
            _canvas.Invalidate();
        }

        private void Sync()
        {
            if (_scope == null || _scope.DroppedDown) return;
            _syncing = true;
            int maximum = Math.Max(0, _host.GuideLayerCount - 1);
            foreach (NumericUpDown number in new[] { _from, _to, _display })
            {
                number.Minimum = 0;
                number.Maximum = maximum;
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
            _fillet.Value = (decimal)Math.Max(0.0, Math.Min(1000000.0, settings.FilletRadius));
            _syncing = false;
        }

        private void CommitScope()
        {
            if (_syncing || _scope.SelectedIndex < 0) return;
            var scope = (WasperGuideLayerScope)_scope.SelectedIndex;
            int from = (int)_from.Value;
            int to = (int)_to.Value;
            int display = (int)_display.Value;
            if (scope == WasperGuideLayerScope.Single) from = to = display;
            _host.SetGuideLayerScope(scope, from, to, display);
        }

        private static Label LabelFor(string text) => new Label
        {
            AutoSize = true,
            Text = text,
            Margin = new Padding(5, 7, 3, 0)
        };

        private static NumericUpDown Number() => new NumericUpDown
        {
            Minimum = 0,
            Maximum = 0,
            Width = 60
        };

        private static Button Button(string text, Action action, int width)
        {
            var button = new Button { Text = text, Width = width, Height = 27 };
            button.Click += (_, __) => action();
            return button;
        }
    }

    internal sealed class WasperGuideWarpEditorForm : Form
    {
        private readonly IWasperGuideWarpEditorHost _host;
        private readonly ComboBox _domainSelector;
        private readonly WasperGuideWarpCanvas _canvas;
        private readonly WasperShellSeamCanvas _shellCanvas;
        private readonly Button _undo;
        private readonly Button _redo;
        private readonly Button _live;
        private readonly Button _update;
        private readonly NumericUpDown _densityNumber;
        private readonly Button _densityAuto;
        private readonly ComboBox _scopeSelector;
        private readonly NumericUpDown _scopeFrom;
        private readonly NumericUpDown _scopeTo;
        private readonly NumericUpDown _displayLayer;
        private readonly CheckBox _xSeam;
        private readonly NumericUpDown _seamFillet;
        private readonly Timer _refreshTimer;
        private int _lastRevision = -1;
        private bool _syncingDensity;
        private bool _syncingScope;
        private bool _syncingShell;

        internal WasperGuideWarpEditorForm(IWasperGuideWarpEditorHost host)
        {
            _host = host;
            // Declares that these pixel sizes were authored at 96 DPI. Without the paired
            // AutoScaleDimensions, AutoScaleMode.Dpi has no baseline to scale from, so hand-coded
            // heights stay at their literal pixel values and every toolbar row is squeezed on a
            // high-DPI monitor while its text renders larger.
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = host.GuideEditorTitle;
            TopMost = true;
            ShowInTaskbar = true;
            MinimizeBox = true;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize = new Size(720, 390);
            ClientSize = new Size(1200, 560);

            var toolbar = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                WrapContents = false,
                Padding = new Padding(6, 6, 6, 4)
            };
            toolbar.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "Shared guide:",
                Margin = new Padding(2, 7, 4, 0)
            });
            _domainSelector = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 145
            };
            _domainSelector.SelectedIndexChanged += (_, __) =>
            {
                _canvas.Guide = ActiveDomain;
                _host.SelectGuide(ActiveDomain);
                SyncDensity();
            };
            toolbar.Controls.Add(_domainSelector);
            _live = ActionButton("▶ Live", () => _host.ToggleGuideLive(), 78);
            _update = ActionButton("Update", () => _host.ApplyPendingGuideUpdate(), 72);
            toolbar.Controls.Add(_live);
            toolbar.Controls.Add(_update);
            _undo = ActionButton("Undo", () => _host.UndoGuideWarp());
            _redo = ActionButton("Redo", () => _host.RedoGuideWarp());
            toolbar.Controls.Add(_undo);
            toolbar.Controls.Add(_redo);
            toolbar.Controls.Add(ActionButton("Reset all guides", () => _host.ResetAllGuideWarps(), 118));
            toolbar.Controls.Add(ActionButton("Rotate 90°", () =>
            {
                _canvas.RotateClockwise();
                _shellCanvas.RotateClockwise();
            }, 92));
            toolbar.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "Controls/cell:",
                Margin = new Padding(12, 7, 3, 0)
            });
            _densityNumber = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 32,
                Width = 50,
                Height = 27
            };
            _densityNumber.ValueChanged += (_, __) =>
            {
                if (!_syncingDensity && _densityNumber.Enabled)
                    _host.SetGuideControlDensity(ActiveDomain, (int)_densityNumber.Value);
            };
            toolbar.Controls.Add(_densityNumber);
            _densityAuto = ActionButton(
                "Auto",
                () => _host.ResetGuideControlDensity(ActiveDomain),
                55);
            toolbar.Controls.Add(_densityAuto);
            toolbar.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "Select and drag stations directly on any guide curve. Endpoints remain fixed.",
                ForeColor = Color.DimGray,
                Margin = new Padding(18, 7, 0, 0)
            });

            _canvas = new WasperGuideWarpCanvas(host) { Dock = DockStyle.Fill };
            _shellCanvas = new WasperShellSeamCanvas(host) { Dock = DockStyle.Fill };
            _canvas.GuideSelected += guide =>
            {
                if (guide >= 0 && guide < _domainSelector.Items.Count)
                    _domainSelector.SelectedIndex = guide;
            };
            var scopeBar = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                WrapContents = false,
                Padding = new Padding(6, 4, 6, 3)
            };
            scopeBar.Controls.Add(ScopeLabel("Layer scope:"));
            _scopeSelector = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 105
            };
            _scopeSelector.Items.AddRange(new object[] { "All layers", "Range", "Single layer" });
            _scopeSelector.SelectedIndexChanged += (_, __) => CommitScope();
            scopeBar.Controls.Add(_scopeSelector);
            scopeBar.Controls.Add(ScopeLabel("From:"));
            _scopeFrom = ScopeNumber();
            _scopeFrom.ValueChanged += (_, __) => CommitScope();
            scopeBar.Controls.Add(_scopeFrom);
            scopeBar.Controls.Add(ScopeLabel("To:"));
            _scopeTo = ScopeNumber();
            _scopeTo.ValueChanged += (_, __) => CommitScope();
            scopeBar.Controls.Add(_scopeTo);
            scopeBar.Controls.Add(ScopeLabel("Display layer:"));
            _displayLayer = ScopeNumber();
            _displayLayer.ValueChanged += (_, __) => CommitScope();
            scopeBar.Controls.Add(_displayLayer);
            scopeBar.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "Edits use normalized arc length across the selected layers.",
                ForeColor = Color.DimGray,
                Margin = new Padding(15, 7, 0, 0)
            });
            var shellBar = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                WrapContents = false,
                Padding = new Padding(6, 4, 6, 3)
            };
            shellBar.Controls.Add(ScopeLabel("Shell seam:"));
            _xSeam = new CheckBox
            {
                Text = "X seam",
                AutoSize = true,
                Margin = new Padding(4, 6, 8, 0)
            };
            _xSeam.CheckedChanged += (_, __) =>
            {
                if (!_syncingShell)
                    _host.SetShellXSeam(_xSeam.Checked);
            };
            shellBar.Controls.Add(_xSeam);
            shellBar.Controls.Add(ScopeLabel("Fillet radius:"));
            _seamFillet = new NumericUpDown
            {
                DecimalPlaces = 3,
                Increment = 0.5M,
                Minimum = 0,
                Maximum = 1000000,
                Width = 85,
                Height = 27
            };
            _seamFillet.ValueChanged += (_, __) =>
            {
                if (!_syncingShell)
                    _host.SetShellFilletRadius((double)_seamFillet.Value);
            };
            shellBar.Controls.Add(_seamFillet);
            shellBar.Controls.Add(ActionButton("Reset seam", () => _host.ResetShellSeam(), 95));
            shellBar.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "Drag the seam around the shell; X-seam endpoints move freely in the shell plane.",
                ForeColor = Color.DimGray,
                Margin = new Padding(15, 7, 0, 0)
            });
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                Size = new Size(ClientSize.Width, Math.Max(300, ClientSize.Height - 118)),
                Panel1MinSize = 240,
                Panel2MinSize = 240,
                SplitterDistance = ClientSize.Width / 2
            };
            split.Panel1.Controls.Add(_canvas);
            split.Panel2.Controls.Add(_shellCanvas);
            Controls.Add(split);
            Controls.Add(shellBar);
            Controls.Add(scopeBar);
            Controls.Add(toolbar);
            RefreshDomains();
            SyncScope();
            SyncShell();

            _refreshTimer = new Timer { Interval = 100 };
            _refreshTimer.Tick += (_, __) => RefreshFromHost();
            _refreshTimer.Start();
            FormClosed += (_, __) =>
            {
                _refreshTimer.Dispose();
                _host.GuideEditorClosed();
            };
        }

        internal void ActivateEditor()
        {
            RefreshDomains();
            if (!Visible)
                Show();
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
        }

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
            _live.BackColor = live ? Color.FromArgb(70, 180, 100) : SystemColors.Control;
            _live.ForeColor = live ? Color.White : SystemColors.ControlText;
            _update.Text = pending ? "Update *" : "Update";
            _update.Enabled = !live;
        }

        private void SyncScope()
        {
            if (_scopeSelector == null || _scopeSelector.DroppedDown)
                return;
            _syncingScope = true;
            int maximum = Math.Max(0, _host.GuideLayerCount - 1);
            foreach (NumericUpDown number in new[] { _scopeFrom, _scopeTo, _displayLayer })
            {
                number.Maximum = maximum;
                number.Minimum = 0;
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
            _densityAuto.Enabled = supported &&
                _host.HasGuideControlDensityOverride(ActiveDomain);
            decimal density = Math.Max(
                (int)_densityNumber.Minimum,
                Math.Min(
                    (int)_densityNumber.Maximum,
                    _host.GetGuideControlDensity(ActiveDomain)));
            _densityNumber.Value = density;
            _syncingDensity = false;
        }

        private void SyncShell()
        {
            if (_xSeam == null || _seamFillet == null)
                return;
            _syncingShell = true;
            WasperShellSeamSettings settings =
                _host.ShellSeamSettings ?? new WasperShellSeamSettings();
            _xSeam.Checked = settings.XSeam;
            _seamFillet.Value = (decimal)Math.Max(
                0.0,
                Math.Min(1000000.0, settings.FilletRadius));
            _syncingShell = false;
        }

        private static Button ActionButton(string text, Action action, int width = 72)
        {
            var button = new Button { Text = text, Width = width, Height = 27 };
            button.Click += (_, __) => action();
            return button;
        }

        private static Label ScopeLabel(string text) => new Label
        {
            AutoSize = true,
            Text = text,
            Margin = new Padding(5, 7, 3, 0)
        };

        private static NumericUpDown ScopeNumber() => new NumericUpDown
        {
            Minimum = 0,
            Maximum = 0,
            Width = 60,
            Height = 27
        };
    }

    internal sealed class WasperGuideWarpCanvas : Control
    {
        private readonly IWasperGuideWarpEditorHost _host;
        private int _guide;
        private int _dragAnchor = -1;
        private int _dragGuide = -1;
        private Point _dragMouseStart;
        private bool _dragMoved;
        private readonly HashSet<GuideAnchorKey> _selectedAnchors =
            new HashSet<GuideAnchorKey>();
        private readonly Dictionary<GuideAnchorKey, double> _dragBaseValues =
            new Dictionary<GuideAnchorKey, double>();
        private readonly Dictionary<int, List<double>> _dragGuideBaseValues =
            new Dictionary<int, List<double>>();
        private bool _selectingRegion;
        private bool _addRegionSelection;
        private Point _selectionStart;
        private Point _selectionCurrent;
        private readonly List<PointF[]> _screenCurves = new List<PointF[]>();
        private float _zoom = 1.0f;
        private PointF _pan = PointF.Empty;
        private bool _panning;
        private Point _panStart;
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

        internal WasperGuideWarpCanvas(IWasperGuideWarpEditorHost host)
        {
            _host = host;
            BackColor = Color.FromArgb(246, 246, 246);
            TabStop = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
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
            g.SmoothingMode = SmoothingMode.AntiAlias;
            BuildScreenCurves();
            using var guidePen = new Pen(Color.FromArgb(85, 85, 85), 1.8f);
            using var selectedPen = new Pen(Color.FromArgb(48, 105, 152), 3.2f);
            using var originalPen = new Pen(Color.FromArgb(165, 165, 165), 1.2f) { DashStyle = DashStyle.Dot };
            using var endpointBrush = new SolidBrush(Color.FromArgb(70, 70, 70));
            using var primaryHandleBrush = new SolidBrush(Color.FromArgb(235, 120, 25));
            using var secondaryHandleBrush = new SolidBrush(Color.FromArgb(244, 177, 105));
            using var otherPrimaryBrush = new SolidBrush(Color.FromArgb(55, 135, 185));
            using var otherSecondaryBrush = new SolidBrush(Color.FromArgb(135, 190, 220));
            using var selectionPen = new Pen(Color.FromArgb(25, 85, 135), 2.5f);
            using var selectionFill = new SolidBrush(Color.FromArgb(45, 70, 145, 205));
            using var textBrush = new SolidBrush(Color.FromArgb(75, 75, 75));

            if (_screenCurves.Count == 0)
            {
                g.DrawString("Solve valid guide curves before opening the editor.", Font, textBrush, 30, 30);
                return;
            }
            _selectedAnchors.RemoveWhere(key =>
                key.Guide < 0 || key.Guide >= _screenCurves.Count ||
                key.Anchor <= 0 ||
                key.Anchor >= _host.GetGuideWarp(key.Guide).Count - 1);

            for (int guide = 0; guide < _screenCurves.Count; guide++)
            {
                PointF[] curve = _screenCurves[guide];
                if (curve.Length < 2)
                    continue;
                g.DrawLines(guide == _guide ? selectedPen : guidePen, curve);
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
                        Pens.White,
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
                PointF label = curve[0];
                g.DrawString($"G{guide}", Font, textBrush, label.X + 8f, label.Y + 8f);
            }

            g.DrawString(
                "Primary stations = larger/darker · secondary stations = smaller/lighter\n" +
                "Ctrl+click toggles · drag empty space selects a region · Ctrl+drag adds · Esc clears\n" +
                "Drag any selected station to move the group · wheel zoom · middle/right drag pan.",
                Font,
                textBrush,
                22,
                Height - 64);

            if (_selectingRegion)
            {
                Rectangle region = SelectionRectangle();
                using var regionFill = new SolidBrush(Color.FromArgb(35, 55, 135, 190));
                using var regionPen = new Pen(Color.FromArgb(55, 135, 190), 1.4f)
                {
                    DashStyle = DashStyle.Dash
                };
                g.FillRectangle(regionFill, region);
                g.DrawRectangle(regionPen, region);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right)
            {
                _panning = true;
                _panStart = e.Location;
                _panOrigin = _pan;
                Capture = true;
                Cursor = Cursors.SizeAll;
                return;
            }
            if (e.Button != MouseButtons.Left)
                return;
            GuideAnchorKey hit = HitGuideAnchor(e.Location);
            bool control = (ModifierKeys & Keys.Control) == Keys.Control;
            if (!hit.IsValid)
            {
                _selectingRegion = true;
                _addRegionSelection = control;
                _selectionStart = e.Location;
                _selectionCurrent = e.Location;
                if (!control)
                    _selectedAnchors.Clear();
                Capture = true;
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
            Capture = true;
            _host.BeginGuideWarpEdit();
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_panning)
            {
                _pan = new PointF(
                    _panOrigin.X + e.X - _panStart.X,
                    _panOrigin.Y + e.Y - _panStart.Y);
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
                Cursor = HitHandle(e.Location) ? Cursors.Hand : Cursors.Default;
                return;
            }
            if (!_dragMoved)
            {
                int dx = e.X - _dragMouseStart.X;
                int dy = e.Y - _dragMouseStart.Y;
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
            if (_panning && (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right))
            {
                _panning = false;
                Capture = false;
                Cursor = Cursors.Default;
                return;
            }
            if (_selectingRegion && e.Button == MouseButtons.Left)
            {
                _selectionCurrent = e.Location;
                ApplyRegionSelection();
                _selectingRegion = false;
                Capture = false;
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
            Capture = false;
            if (_dragMoved)
                _host.CommitGuideWarpEdit();
            else
                _host.CancelGuideWarpEdit();
            _dragMoved = false;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (_panning)
            {
                _panning = false;
                Cursor = Cursors.Default;
            }
            if (_selectingRegion)
            {
                _selectingRegion = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
            if (_dragAnchor < 0)
                return;
            _dragAnchor = -1;
            _dragGuide = -1;
            _dragMoved = false;
            _dragBaseValues.Clear();
            _dragGuideBaseValues.Clear();
            _host.CancelGuideWarpEdit();
            Cursor = Cursors.Default;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Escape)
                return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode != Keys.Escape)
                return;
            if (_dragAnchor >= 0)
            {
                _dragAnchor = -1;
                _dragGuide = -1;
                _dragMoved = false;
                _dragBaseValues.Clear();
                _dragGuideBaseValues.Clear();
                Capture = false;
                _host.CancelGuideWarpEdit();
            }
            _selectingRegion = false;
            _selectedAnchors.Clear();
            Cursor = Cursors.Default;
            e.Handled = true;
            e.SuppressKeyPress = true;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float factor = e.Delta > 0 ? 1.18f : 1f / 1.18f;
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
            foreach (IGrouping<int, GuideAnchorKey> group in
                _selectedAnchors.GroupBy(key => key.Guide))
            {
                List<double> values = _host.GetGuideWarp(group.Key)?.ToList() ??
                    new List<double>();
                if (values.Count < 3)
                    continue;
                _dragGuideBaseValues[group.Key] = values;
                foreach (GuideAnchorKey key in group)
                    if (key.Anchor > 0 && key.Anchor < values.Count - 1)
                        _dragBaseValues[key] = values[key.Anchor];
            }
        }

        private void PreviewGroupDrag(Point location)
        {
            var dragged = new GuideAnchorKey(_dragGuide, _dragAnchor);
            if (!_dragBaseValues.TryGetValue(dragged, out double draggedStart) ||
                _dragGuide < 0 || _dragGuide >= _screenCurves.Count)
                return;
            double requestedDelta =
                ClosestU(_screenCurves[_dragGuide], location) - draggedStart;
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
                        minimumDelta = Math.Max(
                            minimumDelta,
                            values[anchor - 1] + minimumGap - values[anchor]);
                    if (!selected.Contains(anchor + 1))
                        maximumDelta = Math.Min(
                            maximumDelta,
                            values[anchor + 1] - minimumGap - values[anchor]);
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
                    _host.PreviewGuideWarpAnchor(
                        edit.Key.Guide,
                        edit.Key.Anchor,
                        edit.Value + delta);
            }
        }

        private GuideAnchorKey HitGuideAnchor(Point location)
        {
            double best = 16.0;
            GuideAnchorKey hit = GuideAnchorKey.Invalid;
            for (int guide = 0; guide < _screenCurves.Count; guide++)
            {
                IReadOnlyList<double> values = _host.GetGuideWarp(guide);
                for (int anchor = 1; anchor < values.Count - 1; anchor++)
                {
                    double distance = Distance(
                        PointAt(_screenCurves[guide], values[anchor]),
                        location);
                    if (distance >= best)
                        continue;
                    best = distance;
                    hit = new GuideAnchorKey(guide, anchor);
                }
            }
            return hit;
        }

        private Rectangle SelectionRectangle()
        {
            int left = Math.Min(_selectionStart.X, _selectionCurrent.X);
            int top = Math.Min(_selectionStart.Y, _selectionCurrent.Y);
            return new Rectangle(
                left,
                top,
                Math.Abs(_selectionCurrent.X - _selectionStart.X),
                Math.Abs(_selectionCurrent.Y - _selectionStart.Y));
        }

        private void ApplyRegionSelection()
        {
            Rectangle region = SelectionRectangle();
            if (!_addRegionSelection)
                _selectedAnchors.Clear();
            if (region.Width < 2 && region.Height < 2)
                return;
            for (int guide = 0; guide < _screenCurves.Count; guide++)
            {
                IReadOnlyList<double> values = _host.GetGuideWarp(guide);
                for (int anchor = 1; anchor < values.Count - 1; anchor++)
                    if (region.Contains(Point.Round(
                        PointAt(_screenCurves[guide], values[anchor]))))
                        _selectedAnchors.Add(new GuideAnchorKey(guide, anchor));
            }
        }

        private void BuildScreenCurves()
        {
            _screenCurves.Clear();
            IReadOnlyList<IReadOnlyList<PointF>> source = _host.GuideEditorCurves;
            if (source == null || source.Count == 0)
                return;
            List<PointF> sourcePoints = source
                .Where(curve => curve != null)
                .SelectMany(curve => curve)
                .ToList();
            if (sourcePoints.Count < 2)
                return;
            var origin = new PointF(
                sourcePoints.Average(point => point.X),
                sourcePoints.Average(point => point.Y));
            var rotated = source.Select(curve =>
                (IReadOnlyList<PointF>)(curve ?? Array.Empty<PointF>())
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
            foreach (IReadOnlyList<PointF> curve in rotated)
            {
                if (curve == null || curve.Count < 2)
                {
                    _screenCurves.Add(Array.Empty<PointF>());
                    continue;
                }
                _screenCurves.Add(curve.Select(point => new PointF(
                    offsetX + (point.X - minX) * scale,
                    offsetY + (maxY - point.Y) * scale)).ToArray());
            }
        }

        private PointF RotatePoint(PointF point, PointF origin)
        {
            float x = point.X - origin.X;
            float y = point.Y - origin.Y;
            PointF rotated = _quarterTurns switch
            {
                1 => new PointF(-y, x),
                2 => new PointF(-x, -y),
                3 => new PointF(y, -x),
                _ => new PointF(x, y)
            };
            return new PointF(rotated.X + origin.X, rotated.Y + origin.Y);
        }

        private bool HitHandle(Point location)
        {
            return HitGuideAnchor(location).IsValid;
        }

        private static PointF PointAt(IReadOnlyList<PointF> curve, double u)
        {
            if (curve == null || curve.Count == 0)
                return PointF.Empty;
            if (curve.Count == 1)
                return curve[0];
            double position = Math.Max(0.0, Math.Min(1.0, u)) * (curve.Count - 1);
            int index = Math.Min(curve.Count - 2, Math.Max(0, (int)Math.Floor(position)));
            double local = position - index;
            return new PointF(
                (float)(curve[index].X + (curve[index + 1].X - curve[index].X) * local),
                (float)(curve[index].Y + (curve[index + 1].Y - curve[index].Y) * local));
        }

        private static double ClosestU(IReadOnlyList<PointF> curve, Point point)
        {
            if (curve == null || curve.Count < 2)
                return 0.0;
            double bestDistance = double.MaxValue;
            double bestU = 0.0;
            for (int i = 0; i < curve.Count - 1; i++)
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
                    bestU = (i + t) / (curve.Count - 1);
                }
            }
            return bestU;
        }

        private static double Distance(PointF a, Point b)
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

            public bool Equals(GuideAnchorKey other) =>
                Guide == other.Guide && Anchor == other.Anchor;

            public override bool Equals(object obj) =>
                obj is GuideAnchorKey other && Equals(other);

            public override int GetHashCode() => HashCode.Combine(Guide, Anchor);
        }
    }

    internal sealed class WasperShellSeamCanvas : Control
    {
        private readonly IWasperShellSeamEditorHost _host;
        private readonly List<PointF[]> _screenCurves = new List<PointF[]>();
        private readonly List<PointF[]> _screenPartitions = new List<PointF[]>();
        private float _zoom = 1.0f;
        private PointF _pan = PointF.Empty;
        private bool _panning;
        private Point _panStart;
        private PointF _panOrigin;
        private int _quarterTurns;
        private int _dragHandle = -1; // 0 seam, 1 start, 2 end
        private float _screenScale = 1.0f;

        internal WasperShellSeamCanvas(IWasperShellSeamEditorHost host)
        {
            _host = host;
            BackColor = Color.FromArgb(250, 250, 250);
            TabStop = true;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
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
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var shellPen = new Pen(Color.FromArgb(55, 105, 85), 2.2f);
            using var partitionPen = new Pen(Color.FromArgb(125, 125, 125), 1.5f)
            {
                DashStyle = DashStyle.Dash
            };
            using var armPen = new Pen(Color.FromArgb(205, 105, 35), 2.8f);
            using var seamBrush = new SolidBrush(Color.FromArgb(225, 120, 25));
            using var startBrush = new SolidBrush(Color.FromArgb(55, 145, 190));
            using var endBrush = new SolidBrush(Color.FromArgb(185, 75, 135));
            using var textBrush = new SolidBrush(Color.FromArgb(75, 75, 75));

            if (_screenCurves.Count == 0)
            {
                g.DrawString("Enable shell output to edit its seam.", Font, textBrush, 30, 30);
                return;
            }
            foreach (PointF[] partition in _screenPartitions)
                if (partition.Length >= 2)
                    g.DrawLines(partitionPen, partition);
            foreach (PointF[] curve in _screenCurves)
                if (curve.Length >= 2)
                    g.DrawLines(shellPen, curve);

            PointF[] active = _screenCurves.FirstOrDefault(curve => curve.Length >= 2);
            if (active == null)
                return;
            WasperShellSeamSettings settings =
                _host.ShellSeamSettings ?? new WasperShellSeamSettings();
            PointF seam = PointAt(active, Wrap01(settings.SeamU));
            DrawHandle(g, seamBrush, seam, 9f);
            g.DrawString("Seam", Font, textBrush, seam.X + 10f, seam.Y + 8f);

            if (settings.XSeam)
            {
                PointF inward = InwardNormal(active, settings.SeamU);
                PointF tangent = Tangent(active, settings.SeamU);
                double length = Math.Max(1e-9, PolylineLength(active) / Math.Max(1e-9f, _screenScale));
                double du = Math.Min(0.24, Math.Max(0.0, settings.FilletRadius) / length);
                PointF attachStart = PointAt(active, Wrap01(settings.SeamU + du));
                PointF attachEnd = PointAt(active, Wrap01(settings.SeamU - du));
                PointF start = Add(
                    Add(seam, inward, settings.StartOffset * _screenScale),
                    tangent,
                    settings.StartTangentialOffset * _screenScale);
                PointF end = Add(
                    Add(seam, inward, settings.EndOffset * _screenScale),
                    tangent,
                    settings.EndTangentialOffset * _screenScale);
                g.DrawLine(armPen, start, attachStart);
                g.DrawLine(armPen, attachEnd, end);
                DrawHandle(g, startBrush, start, 8f);
                DrawHandle(g, endBrush, end, 8f);
                g.DrawString("Start", Font, textBrush, start.X + 9f, start.Y - 18f);
                g.DrawString("End", Font, textBrush, end.X + 9f, end.Y + 5f);
            }

            g.DrawString(
                "Shell seam editor · dashed grey = partitions · orange = seam · blue/magenta = X endpoints\n" +
                "Drag seam along curve · drag endpoints freely · wheel zooms at cursor · middle/right pan.",
                Font,
                textBrush,
                22,
                Height - 48);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right)
            {
                _panning = true;
                _panStart = e.Location;
                _panOrigin = _pan;
                Capture = true;
                Cursor = Cursors.SizeAll;
                return;
            }
            if (e.Button != MouseButtons.Left || _screenCurves.Count == 0)
                return;
            PointF[] active = _screenCurves.FirstOrDefault(curve => curve.Length >= 2);
            if (active == null)
                return;
            WasperShellSeamSettings settings =
                _host.ShellSeamSettings ?? new WasperShellSeamSettings();
            PointF seam = PointAt(active, Wrap01(settings.SeamU));
            _dragHandle = Distance(seam, e.Location) <= 16.0 ? 0 : -1;
            if (settings.XSeam)
            {
                PointF inward = InwardNormal(active, settings.SeamU);
                PointF tangent = Tangent(active, settings.SeamU);
                PointF start = Add(
                    Add(seam, inward, settings.StartOffset * _screenScale),
                    tangent,
                    settings.StartTangentialOffset * _screenScale);
                PointF end = Add(
                    Add(seam, inward, settings.EndOffset * _screenScale),
                    tangent,
                    settings.EndTangentialOffset * _screenScale);
                if (Distance(start, e.Location) <= 16.0) _dragHandle = 1;
                else if (Distance(end, e.Location) <= 16.0) _dragHandle = 2;
            }
            if (_dragHandle < 0)
                return;
            _host.BeginShellSeamEdit();
            Capture = true;
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_panning)
            {
                _pan = new PointF(
                    _panOrigin.X + e.X - _panStart.X,
                    _panOrigin.Y + e.Y - _panStart.Y);
                Invalidate();
                return;
            }
            if (_dragHandle < 0 || _screenCurves.Count == 0)
                return;
            PointF[] active = _screenCurves.First(curve => curve.Length >= 2);
            WasperShellSeamSettings settings =
                _host.ShellSeamSettings ?? new WasperShellSeamSettings();
            if (_dragHandle == 0)
                _host.PreviewShellSeam(ClosestU(active, e.Location));
            else
            {
                PointF seam = PointAt(active, Wrap01(settings.SeamU));
                PointF inward = InwardNormal(active, settings.SeamU);
                PointF tangent = Tangent(active, settings.SeamU);
                double inwardOffset = ((e.X - seam.X) * inward.X + (e.Y - seam.Y) * inward.Y) /
                    Math.Max(1e-9f, _screenScale);
                double tangentialOffset = ((e.X - seam.X) * tangent.X + (e.Y - seam.Y) * tangent.Y) /
                    Math.Max(1e-9f, _screenScale);
                _host.PreviewShellOffset(
                    _dragHandle == 1,
                    inwardOffset,
                    tangentialOffset);
            }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_panning)
            {
                _panning = false;
                Capture = false;
                Cursor = Cursors.Default;
                return;
            }
            if (_dragHandle < 0)
                return;
            OnMouseMove(e);
            _dragHandle = -1;
            Capture = false;
            Cursor = Cursors.Default;
            _host.CommitShellSeamEdit();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (_panning) _panning = false;
            if (_dragHandle >= 0)
            {
                _dragHandle = -1;
                _host.CancelShellSeamEdit();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            float oldZoom = _zoom;
            _zoom = Math.Max(0.2f, Math.Min(20f, _zoom * (e.Delta > 0 ? 1.18f : 1f / 1.18f)));
            float factor = _zoom / Math.Max(1e-9f, oldZoom);
            var centre = new PointF(Width * 0.5f, Height * 0.5f);
            _pan = new PointF(
                e.X - centre.X - factor * (e.X - centre.X - _pan.X),
                e.Y - centre.Y - factor * (e.Y - centre.Y - _pan.Y));
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
            IReadOnlyList<IReadOnlyList<PointF>> source = _host.ShellEditorCurves;
            IReadOnlyList<IReadOnlyList<PointF>> partitionSource =
                _host.ShellPartitionEditorCurves ?? Array.Empty<IReadOnlyList<PointF>>();
            if (source == null || source.Count == 0)
                return;
            List<PointF> all = source
                .Concat(partitionSource)
                .Where(curve => curve != null)
                .SelectMany(curve => curve)
                .ToList();
            if (all.Count < 2)
                return;
            var origin = new PointF(all.Average(point => point.X), all.Average(point => point.Y));
            var rotated = source.Select(curve =>
                (IReadOnlyList<PointF>)(curve ?? Array.Empty<PointF>())
                    .Select(point => RotatePoint(point, origin)).ToArray()).ToList();
            var rotatedPartitions = partitionSource.Select(curve =>
                (IReadOnlyList<PointF>)(curve ?? Array.Empty<PointF>())
                    .Select(point => RotatePoint(point, origin)).ToArray()).ToList();
            List<PointF> points = rotated
                .Concat(rotatedPartitions)
                .SelectMany(curve => curve)
                .ToList();
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
            foreach (IReadOnlyList<PointF> curve in rotated)
                _screenCurves.Add(curve.Select(point => new PointF(
                    offsetX + (point.X - minX) * _screenScale,
                    offsetY + (maxY - point.Y) * _screenScale)).ToArray());
            foreach (IReadOnlyList<PointF> curve in rotatedPartitions)
                _screenPartitions.Add(curve.Select(point => new PointF(
                    offsetX + (point.X - minX) * _screenScale,
                    offsetY + (maxY - point.Y) * _screenScale)).ToArray());
        }

        private PointF RotatePoint(PointF point, PointF origin)
        {
            float x = point.X - origin.X;
            float y = point.Y - origin.Y;
            PointF rotated = _quarterTurns switch
            {
                1 => new PointF(-y, x),
                2 => new PointF(-x, -y),
                3 => new PointF(y, -x),
                _ => new PointF(x, y)
            };
            return new PointF(rotated.X + origin.X, rotated.Y + origin.Y);
        }

        private static PointF InwardNormal(IReadOnlyList<PointF> curve, double u)
        {
            double du = 1.0 / Math.Max(16.0, curve.Count * 2.0);
            PointF before = PointAt(curve, Wrap01(u - du));
            PointF after = PointAt(curve, Wrap01(u + du));
            float tx = after.X - before.X;
            float ty = after.Y - before.Y;
            float length = (float)Math.Sqrt(tx * tx + ty * ty);
            if (length <= 1e-6f) return new PointF(0f, -1f);
            var normal = new PointF(-ty / length, tx / length);
            PointF seam = PointAt(curve, Wrap01(u));
            PointF center = new PointF(curve.Average(point => point.X), curve.Average(point => point.Y));
            if ((center.X - seam.X) * normal.X + (center.Y - seam.Y) * normal.Y < 0f)
                normal = new PointF(-normal.X, -normal.Y);
            return normal;
        }

        private static PointF Tangent(IReadOnlyList<PointF> curve, double u)
        {
            double du = 1.0 / Math.Max(16.0, curve.Count * 2.0);
            PointF before = PointAt(curve, Wrap01(u - du));
            PointF after = PointAt(curve, Wrap01(u + du));
            float tx = after.X - before.X;
            float ty = after.Y - before.Y;
            float length = (float)Math.Sqrt(tx * tx + ty * ty);
            return length <= 1e-6f
                ? new PointF(1f, 0f)
                : new PointF(tx / length, ty / length);
        }

        private static PointF Add(PointF point, PointF direction, double distance) =>
            new PointF(
                (float)(point.X + direction.X * distance),
                (float)(point.Y + direction.Y * distance));

        private static void DrawHandle(Graphics graphics, Brush brush, PointF point, float radius)
        {
            graphics.FillEllipse(brush, point.X - radius, point.Y - radius, radius * 2f, radius * 2f);
            graphics.DrawEllipse(Pens.White, point.X - radius, point.Y - radius, radius * 2f, radius * 2f);
        }

        private static double PolylineLength(IReadOnlyList<PointF> curve)
        {
            double length = 0.0;
            for (int i = 1; i < curve.Count; i++)
            {
                double dx = curve[i].X - curve[i - 1].X;
                double dy = curve[i].Y - curve[i - 1].Y;
                length += Math.Sqrt(dx * dx + dy * dy);
            }
            return length;
        }

        private static PointF PointAt(IReadOnlyList<PointF> curve, double u)
        {
            if (curve == null || curve.Count == 0) return PointF.Empty;
            if (curve.Count == 1) return curve[0];
            double total = PolylineLength(curve);
            if (total <= 1e-12) return curve[0];
            double target = Math.Max(0.0, Math.Min(1.0, u)) * total;
            double travelled = 0.0;
            for (int i = 0; i < curve.Count - 1; i++)
            {
                double dx = curve[i + 1].X - curve[i].X;
                double dy = curve[i + 1].Y - curve[i].Y;
                double segment = Math.Sqrt(dx * dx + dy * dy);
                if (travelled + segment >= target || i == curve.Count - 2)
                {
                    double local = segment <= 1e-12
                        ? 0.0
                        : (target - travelled) / segment;
                    local = Math.Max(0.0, Math.Min(1.0, local));
                    return new PointF(
                        (float)(curve[i].X + dx * local),
                        (float)(curve[i].Y + dy * local));
                }
                travelled += segment;
            }
            return curve[curve.Count - 1];
        }

        private static double ClosestU(IReadOnlyList<PointF> curve, Point point)
        {
            double totalLength = PolylineLength(curve);
            if (totalLength <= 1e-12)
                return 0.0;
            double bestDistance = double.MaxValue;
            double bestU = 0.0;
            double travelled = 0.0;
            for (int i = 0; i < curve.Count - 1; i++)
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

        private static double Distance(PointF a, Point b)
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
