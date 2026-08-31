using System;

using Eto.Drawing;
using Eto.Forms;

using Grasshopper;
using Rhino;
using Rhino.UI;

namespace WASPer_3DP.Components._5_0_Gcode
{
    /// <summary>
    /// Play/stop/speed/timeline controls for wsp_Gc05_WASPer Simulation. Eto.Forms replacement for
    /// the former WinForms WasperPlaybackForm -- see
    /// 00_Plans/WASPER_CROSS_PLATFORM_ETO_UI_MIGRATION_PLAN.md, Workstream 5.
    ///
    /// Kept as its own file (rather than inside "wsp_Gc05_WASPer Simulation.cs") because that file
    /// still imports System.Windows.Forms for WasperPlaybackAttributes -- the GH_ComponentAttributes
    /// canvas overlay, which stays on the Grasshopper/WinForms host boundary per the plan's
    /// Architectural Boundary section and is not part of this migration. Mixing that using directive
    /// with Eto.Forms in one file would collide on Form/Button/Label/CheckBox/Control.
    ///
    /// Behavior preserved from the WinForms version:
    /// - One persistent instance per component (see wsp_Gc05_WASPer_Simulation.TogglePlaybackForm):
    ///   closing the window (title bar close, Alt+F4, etc.) hides it and stops playback instead of
    ///   actually closing/disposing, so the same instance is reused every time the canvas "Playback"
    ///   button is clicked again.
    /// - Speed slider range -200%..400% (negative values play the simulation backwards), default
    ///   100%, pushed straight into wsp_Gc05_WASPer_Simulation.PlaybackSpeed.
    /// - Timeline slider is 0..1000 representing normalized progress 0.0..1.0; a guard flag
    ///   (_synchronizingTimeline) prevents SetProgress (component -> UI) from re-triggering
    ///   SeekNormalized (UI -> component).
    /// - Play/Stop/Seek do not go through events -- the form holds a direct reference to the owning
    ///   component and calls its methods, exactly as before.
    /// - Playback advances via the component re-expiring its own solution on every recomputed
    ///   frame (see UpdatePlayback in the component); there is no WinForms/Eto timer here to migrate.
    /// - No settings are persisted for this window (speed/position/window placement are session-only,
    ///   matching the previous behavior -- Write/Read on the component only stores show_all_outputs).
    ///
    /// Changed from the WinForms version:
    /// - The play/pause toggle was a WinForms CheckBox styled as a button (Appearance.Button) so its
    ///   Checked state supplied the pressed look. Eto's cross-platform CheckBox does not reliably
    ///   support that button styling, so this uses a plain Button whose glyph is swapped between
    ///   play/pause by SetPlaying(bool) -- same external behavior (one click toggles play/pause, and
    ///   the icon reflects state pushed from the component), simpler implementation, no
    ///   checked-state synchronization guard needed.
    /// - Fixed pixel Location/Size per control (WinForms) replaced with a TableLayout (AGENTS.md
    ///   cross-platform UI policy: use logical Eto dimensions and TableLayout/StackLayout rather than
    ///   hard-coded pixel positions; DPI scaling is then automatic instead of hand-rolled
    ///   AutoScaleMode.Dpi/AutoScaleDimensions bookkeeping).
    /// - IsDisposed was not reused as the public "is this window still alive" check: it is unclear
    ///   from this environment (no local Windows/.NET toolchain to compile against) whether
    ///   Eto.Forms.Window already declares a member with that name, and shadowing an inherited member
    ///   is a risk not worth taking for an internal, single-file-consumed API. IsClosed here is a
    ///   private-tracked flag with a distinct name, referenced only from
    ///   wsp_Gc05_WASPer_Simulation.TogglePlaybackForm/RemovedFromDocument in the same namespace.
    /// - Cursor.Position (System.Windows.Forms/System.Drawing, Windows-only) replaced with
    ///   Eto.Forms.Mouse.Position, guarded by Mouse.IsSupported the same way
    ///   Components/0.0_WASPer_3DP/WASPerMascot.cs already does for its own context-menu placement.
    /// - Added Owner (RhinoEtoApp.MainWindowForDocument) and UseRhinoStyle(), neither of which the
    ///   WinForms version had. AGENTS.md's Cross-platform UI policy requires both for WASPer-owned
    ///   Eto windows; UseRhinoStyle() only has in-repo precedent on Eto.Forms.Dialog so far (see
    ///   WASPer_Sm01Dialogs.cs), not yet on a plain Eto.Forms.Form -- worth double-checking first if
    ///   the build fails here.
    ///
    /// NOT build-verified: this device shell has no Windows/.NET toolchain (see project memory
    /// sm06-interface-input-builder.md / selva-prepare-ui-inputs-port.md for the same limitation on
    /// prior WASPer/Selva work in this environment). Every Eto.Forms/Eto.Drawing member used below was
    /// checked against actual working usage already compiled into this repository
    /// (Components/1.2_Studies/Sm01/WASPer_Sm01Dialogs.cs, Components/0.0_WASPer_3DP/WASPerMascot.cs)
    /// rather than assumed from general Eto knowledge, but a real `dotnet build` of WASPer_3DP.csproj
    /// on Windows has not been run against this specific file. Treat as unverified until that build
    /// (and an interactive Rhino/Grasshopper check per plan section 9.2/14) has happened.
    /// </summary>
    internal sealed class WasperPlaybackForm : Form
    {
        private readonly wsp_Gc05_WASPer_Simulation _component;
        private readonly Button _play;
        private readonly Label _speedLabel;
        private readonly Slider _timeline;
        private bool _playing;
        private bool _synchronizingTimeline;
        private bool _closed;

        public WasperPlaybackForm(wsp_Gc05_WASPer_Simulation component)
        {
            _component = component;

            Title = "Playback";
            Topmost = true;
            ShowInTaskbar = true;
            Resizable = false;
            Maximizable = false;
            Minimizable = false;
            RhinoDoc document = Instances.ActiveCanvas?.Document?.RhinoDocument ?? RhinoDoc.ActiveDoc;
            if (document != null)
                Owner = RhinoEtoApp.MainWindowForDocument(document);
            this.UseRhinoStyle();

            _play = new Button
            {
                Text = "▶",
                Font = new Font(FontFamilies.Sans, 14f),
                Size = new Size(42, 34),
                MinimumSize = new Size(42, 34)
            };
            _play.Click += (_, _) => _component.TogglePlay();

            var stop = new Button
            {
                Text = "◼",
                Font = new Font(FontFamilies.Sans, 14f),
                Size = new Size(42, 34),
                MinimumSize = new Size(42, 34)
            };
            stop.Click += (_, _) => _component.StopPlayback();

            var speed = new Slider
            {
                Orientation = Orientation.Vertical,
                MinValue = -200,
                MaxValue = 400,
                TickFrequency = 100,
                Value = 100,
                Size = new Size(45, 125)
            };
            _speedLabel = new Label
            {
                Text = "100%",
                VerticalAlignment = VerticalAlignment.Center,
                Width = 60
            };
            speed.ValueChanged += (_, _) =>
            {
                _component.PlaybackSpeed = speed.Value / 100.0;
                _speedLabel.Text = $"{speed.Value}%";
            };

            _timeline = new Slider
            {
                Orientation = Orientation.Horizontal,
                MinValue = 0,
                MaxValue = 1000,
                Value = 0
            };
            _timeline.ValueChanged += (_, _) =>
            {
                if (!_synchronizingTimeline)
                    _component.SeekNormalized(_timeline.Value / 1000.0);
            };

            var transport = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Padding = new Padding(10),
                Items =
                {
                    new StackLayoutItem(_play, VerticalAlignment.Center, false),
                    new StackLayoutItem(stop, VerticalAlignment.Center, false),
                    new StackLayoutItem(speed, VerticalAlignment.Center, false),
                    new StackLayoutItem(_speedLabel, VerticalAlignment.Center, true)
                }
            };

            var position = new TableLayout
            {
                Spacing = new Size(6, 4),
                Padding = new Padding(10, 0, 10, 10),
                Rows =
                {
                    new TableRow(new TableCell(new Label { Text = "Position" }, false))
                    {
                        ScaleHeight = false
                    },
                    new TableRow(new TableCell(_timeline, true))
                    {
                        ScaleHeight = false
                    }
                }
            };

            // The transport StackLayout centers the fixed-size buttons beside the taller speed
            // slider. Keeping the two sections in natural-height rows prevents either section from
            // absorbing unused vertical space.
            Content = new TableLayout
            {
                Rows =
                {
                    new TableRow(new TableCell(transport, true))
                    {
                        ScaleHeight = false
                    },
                    new TableRow(new TableCell(position, true))
                    {
                        ScaleHeight = false
                    }
                }
            };

            Closing += (_, e) =>
            {
                e.Cancel = true;
                Visible = false;
                _component.StopPlayback();
            };
            Closed += (_, _) => _closed = true;
        }

        /// <summary>
        /// True once this window has actually been torn down (as opposed to merely hidden -- see
        /// the Closing handler above, which cancels every user-initiated close). Referenced from
        /// wsp_Gc05_WASPer_Simulation.TogglePlaybackForm/RemovedFromDocument to decide whether the
        /// existing instance can still be reused.
        /// </summary>
        public bool IsClosed => _closed;

        /// <summary>Pushes the component's play/pause state into the toggle button's glyph.</summary>
        public void SetPlaying(bool playing)
        {
            if (_playing == playing)
                return;
            _playing = playing;
            _play.Text = playing ? "⏸" : "▶";
        }

        /// <summary>Pushes the component's normalized playback progress (0..1) into the timeline slider.</summary>
        public void SetProgress(double progress)
        {
            int value = (int)Math.Round(Math.Max(0.0, Math.Min(progress, 1.0)) * 1000.0);
            if (_timeline.Value == value)
                return;

            _synchronizingTimeline = true;
            try
            {
                _timeline.Value = value;
            }
            finally
            {
                _synchronizingTimeline = false;
            }
        }

        /// <summary>Shows (or re-shows) the window positioned near the current mouse cursor.</summary>
        public void ShowNearCursor()
        {
            if (Mouse.IsSupported)
            {
                PointF cursor = Mouse.Position;
                Location = new Point((int)cursor.X + 20, (int)cursor.Y - (Height / 2));
            }
            Show();
        }
    }
}
