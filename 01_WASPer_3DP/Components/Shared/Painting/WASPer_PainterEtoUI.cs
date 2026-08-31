using System;
using System.Collections.Generic;
using System.Drawing.Text;
using System.Linq;

using Eto.Drawing;
using Eto.Forms;

using Grasshopper;
using Rhino;
using Rhino.Geometry;
using Rhino.UI;

namespace WASPer_3DP.Painting
{
    /// <summary>
    /// Cross-platform Eto.Forms replacement for <see cref="WasperPaintForm"/>/<see cref="WasperAtlasCanvas"/>
    /// (WASPer_PainterUI.cs) -- see 00_Plans/WASPER_CROSS_PLATFORM_ETO_UI_MIGRATION_PLAN.md, Workstream 2
    /// (Shared Painter).
    ///
    /// ADDITIVE / INACTIVE: this file is not referenced by Ge17PaintEngine.cs or Pp14PaintEngine.cs yet.
    /// Both keep constructing the WinForms <see cref="WasperPaintForm"/> until this file is build-verified
    /// (real Visual Studio build against WASPer_3DP.csproj -- this device shell has no Windows/.NET
    /// toolchain, matching every other WASPer/Eto file in this repo) and interactively checked on both
    /// consuming components, per the plan's Workstream 2 completion gate (section 6.4). Cutover means
    /// changing the two `new WasperPaintForm(...)` call sites to `new WasperEtoPaintForm(...)`, nothing
    /// else -- both take the same <see cref="IWasperPainterHost"/> constructor argument.
    ///
    /// Business/state logic needed NO porting: IWasperPainterHost and every file under
    /// Components/Shared/Painting/ except WASPer_PainterUI.cs (WasperPaintSession, PaintRegion,
    /// PaintState, PaintBrushKernel, PaintMasks, PaintValueHistory, PaintPersistence,
    /// PaintComponentIcons, PaintDisplay, PaintUtilities, PaintTextureSource, PaintTexturePlacement) were
    /// checked and confirmed to have zero System.Windows.Forms references already -- only the two UI
    /// classes in WASPer_PainterUI.cs are WinForms-specific and need a replacement, ported below as
    /// <see cref="WasperEtoPaintForm"/> (was WasperPaintForm) and <see cref="WasperEtoAtlasCanvas"/>
    /// (was WasperAtlasCanvas). Class/member names differ from the WinForms originals only by the "Eto"
    /// infix (matching the WasperPlaybackForm/Sm01EtoManagerForm naming precedent already in this repo)
    /// so both can coexist in the same WASPer_3DP.Painting namespace during the additive period.
    ///
    /// Ported 1:1, low risk:
    /// - All 7 toolbar rows (tools / brush settings / texture / text-texture / field navigation / field
    ///   settings / session), using Eto.Forms.StackLayout rows instead of WinForms FlowLayoutPanel, each
    ///   wrapped in a Panel for BackgroundColor banding (Eto layout containers themselves don't paint a
    ///   background; Panel does -- unverified against a real build, but matches Eto.Forms.Panel's
    ///   documented BackgroundColor property).
    /// - TrackBar -> Eto.Forms.Slider (Value stays an int on both, so the existing log10-scaled
    ///   Radius/FieldResolution/FieldFrameSize slider<->number mapping math is untouched).
    /// - NumericUpDown -> Eto.Forms.NumericStepper (Value is double instead of decimal -- ClampDecimal
    ///   became a plain double Math.Max/Min clamp, everything else is the same math).
    /// - ComboBox(DropDownList) -> Eto.Forms.DropDown, System.Windows.Forms.ToolTip -> the ToolTip string
    ///   property every Eto.Forms.Control already exposes directly (no separate tooltip component
    ///   needed).
    /// - The persistent hide-not-close window pattern, Owner/UseRhinoStyle(), and ShowNearCursor via
    ///   Eto.Forms.Mouse all follow the WasperPlaybackForm precedent
    ///   (Components/5.0_Gcode/WASPer_Gc05PlaybackForm.cs) exactly.
    /// - HistoryIcon/VisibilityIcon: same 20x20 programmatic icon drawing, via Eto.Drawing.Graphics
    ///   instead of System.Drawing.Graphics.
    /// - The two debounced-commit System.Windows.Forms.Timer instances (180ms settings, 220ms field
    ///   settings) and WasperAtlasCanvas's 25ms hover-flush timer all became Eto.Forms.UITimer per
    ///   AGENTS.md's cross-platform UI policy, stopped on window Closing/Dispose.
    ///
    /// New ground, higher risk (see the class doc comment on WasperEtoAtlasCanvas below for detail):
    /// - The owner-drawn canvas itself. There is zero prior Eto.Drawable/custom-canvas-drawing precedent
    ///   anywhere in this repository before this file, unlike every other item above which had a direct
    ///   working precedent to copy from (Sm01EtoManagerForm, WasperPlaybackForm, WASPerMascot.cs).
    /// - GDI+'s 3-point-affine Graphics.DrawImage (used both for the non-distorted texture-layer draw and,
    ///   per warped grid-cell, for the distorted/warped texture draw) has no Eto.Drawing equivalent.
    ///   Replaced with an explicit affine Matrix built from the same 3 destination points GDI+ would have
    ///   taken, applied via Graphics.SaveTransform()/MultiplyTransform()/RestoreTransform() -- the same
    ///   math GDI+ performs internally, just written out by hand.
    /// - GDI+'s ImageAttributes/ColorMatrix opacity blending has no Eto.Drawing equivalent either
    ///   (Eto's DrawImage has no per-call alpha or color-matrix parameter). Replaced with a cached,
    ///   opacity-pre-baked copy of each texture layer's bitmap (built once per layer via pixel-level
    ///   Bitmap.Lock(), rebuilt only when that layer's Bitmap reference/Opacity/Revision changes -- same
    ///   invalidation granularity WinForms already used for its texture/atlas bitmap caches, so this is
    ///   not a new per-frame cost).
    /// - System.Drawing.Bitmap (WasperPaintTextureLayer.Bitmap, and the Rhino mesh/plane/marker types
    ///   drawn from) crosses into Eto.Drawing.Bitmap via the same PNG-encoded-MemoryStream round trip
    ///   Sm01EtoManagerForm.ConvertToEtoImage already established as this repo's conversion-boundary
    ///   pattern (AGENTS.md cross-platform UI policy) -- cached per layer/revision for the same reason.
    /// - Mouse wheel delta, cursor shapes, and modifier-key access all use different Eto APIs
    ///   (MouseEventArgs.Delta is a SizeF of "notches", not WinForms' /120 int; MouseEventArgs.Modifiers
    ///   replaces the static Control.ModifierKeys; Eto.Forms.Cursors' named set doesn't line up 1:1 with
    ///   System.Windows.Forms.Cursors) -- mapped as closely as each API allows and called out inline
    ///   where the mapping is approximate.
    /// - WinForms' explicit mouse Capture (so drag gestures that leave the control's bounds keep
    ///   delivering MouseMove/MouseUp) has no confirmed portable Eto.Forms.Control equivalent and no
    ///   in-repo precedent either way. Left unset; the interaction state machine still gates on the drag
    ///   flags (_painting/_panning/_textureMoving/etc.) exactly as before, so it will keep working for
    ///   drags that stay inside the canvas, but a drag that leaves the canvas bounds before mouse-up may
    ///   behave differently per platform. Flagged for the interactive check (plan section 6.3/9.2).
    ///
    /// NOT build-verified (no Windows/.NET toolchain in this environment -- same limitation noted on
    /// every other Eto file in this repo). Every Eto.Forms/Eto.Drawing member used below was either
    /// checked against actual working usage already compiled into this repository
    /// (Components/1.2_Studies/Sm01/WASPer_Sm01EtoManagerForm.cs, Components/5.0_Gcode/WASPer_Gc05PlaybackForm.cs,
    /// Components/0.0_WASPer_3DP/WASPerMascot.cs) or, where no in-repo precedent existed (Drawable/Graphics
    /// members, Matrix, Cursors, MouseEventArgs shape), taken from general Eto.Forms/Eto.Drawing API
    /// knowledge and flagged above. Expect a first real build to surface errors, exactly as happened twice
    /// for the Sm01 Eto Manager -- report them and they'll be fixed the same way.
    /// </summary>
    internal sealed class WasperEtoPaintForm : Form
    {
        private readonly IWasperPainterHost _component;
        private readonly WasperEtoAtlasCanvas _canvas;
        private readonly Dictionary<WasperPaintTool, Button> _toolButtons =
            new Dictionary<WasperPaintTool, Button>();
        private readonly Button _previewButton;
        private readonly Button _liveButton;
        private readonly Button _updateButton;
        private readonly Button _undoButton;
        private readonly Button _redoButton;
        private readonly Button _smoothSquareButton;
        private readonly Button _smoothFreeformButton;
        private readonly Button _textureButton;
        private readonly Button[] _textureLayerButtons = new Button[5];
        private readonly Button[] _textureVisibilityButtons = new Button[5];
        private readonly Bitmap _visibleTextureIcon = VisibilityIcon(true);
        private readonly Bitmap _hiddenTextureIcon = VisibilityIcon(false);
        private readonly Button _editTextureButton;
        private readonly Button _distortTextureButton;
        private readonly Button _rotateTextureButton;
        private readonly Button _flipMapButton;
        private readonly Button _rotateAtlasButton;
        private readonly Button _fitTextureButton;
        private readonly Button _applyTextureButton;
        private readonly Button _applyCompositeTextureButton;
        private readonly Button _removeTextureButton;
        private readonly Button[] _textTextureLayerButtons = new Button[5];
        private readonly Button[] _textTextureVisibilityButtons = new Button[5];
        private readonly TextBox _textTextureBox;
        private readonly DropDown _textFontBox;
        private readonly List<string> _textFontNames = new List<string>();
        private readonly NumericStepper _textFontSizeNumber;
        private readonly Button _setTextTextureButton;
        private readonly Slider _radiusSlider;
        private readonly Slider _brushStrengthSlider;
        private readonly Slider _smoothStrengthSlider;
        private readonly NumericStepper _radiusNumber;
        private readonly NumericStepper _brushStrengthNumber;
        private readonly NumericStepper _smoothStrengthNumber;
        private readonly Slider _fieldOffsetSlider;
        private readonly Slider _fieldResolutionSlider;
        private readonly Slider _fieldFrameSlider;
        private readonly NumericStepper _fieldOffsetNumber;
        private readonly NumericStepper _fieldResolutionNumber;
        private readonly NumericStepper _fieldFrameNumber;
        private readonly Label _fieldIndicator;
        private readonly Button _arrangeFieldsButton;
        private readonly UITimer _settingsCommitTimer;
        private readonly UITimer _fieldCommitTimer;
        private bool _syncingSettings;
        private bool _syncingFieldSettings;
        private bool _syncingTextTexture;
        private bool _closed;

        public WasperEtoPaintForm(IWasperPainterHost component)
        {
            _component = component;

            Title = component.PainterTitle;
            ShowInTaskbar = true;
            Resizable = true;
            // Maximizable = true (WinForms had MaximizeBox = false): matches
            // Sm01EtoManagerForm's confirmed-working Resizable/Maximizable combination -- the
            // Painter previously set Maximizable = false, the one property difference from Sm01's
            // pattern, and the maintainer reported the window could not be resized at all. Changed
            // as the most likely fix; ask to revert to false once resizing is confirmed working, if
            // the disabled maximize button is preferred back.
            Maximizable = true;
            Minimizable = true;
            Topmost = true;
            RhinoDoc document = Instances.ActiveCanvas?.Document?.RhinoDocument ?? RhinoDoc.ActiveDoc;
            if (document != null)
                Owner = RhinoEtoApp.MainWindowForDocument(document);
            // Grasshopper can be hosted in a separate top-level window from Rhino. Ownership by the
            // Rhino main window alone therefore lets the painter fall behind the GH canvas when the
            // canvas is clicked. Keep the interactive painter visible while it is open, matching the
            // original WinForms painter behavior.
            this.UseRhinoStyle();

            ClientSize = component.SupportsTextTextures
                ? new Size(1520, 598)
                : new Size(1200, 560);
            MinimumSize = new Size(720, 420);

            var tools = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Padding = new Padding(4),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            AddToolButton(tools, component.PullToolLabel, WasperPaintTool.Pull);
            AddToolButton(tools, component.PushToolLabel, WasperPaintTool.Push);
            if (component.SupportsZeroTool)
                AddToolButton(tools, "Zero", WasperPaintTool.Zero);
            AddToolButton(tools, "Smooth", WasperPaintTool.Smooth);
            _smoothSquareButton = ActionButton(
                "Square",
                () => _component.SetSmoothRegionShape(WasperSmoothRegionShape.Square));
            _smoothSquareButton.Width = 66;
            tools.Items.Add(_smoothSquareButton);
            _smoothFreeformButton = ActionButton(
                "Freeform",
                () => _component.SetSmoothRegionShape(WasperSmoothRegionShape.Freeform));
            _smoothFreeformButton.Width = 76;
            tools.Items.Add(_smoothFreeformButton);
            AddToolButton(tools, "Erase", WasperPaintTool.Erase);
            _undoButton = IconButton(HistoryIcon(false), () => _component.UndoPaint());
            _undoButton.ToolTip = "Undo last paint change";
            _redoButton = IconButton(HistoryIcon(true), () => _component.RedoPaint());
            _redoButton.ToolTip = "Redo last undone paint change";
            tools.Items.Add(_undoButton);
            tools.Items.Add(_redoButton);
            tools.Items.Add(ActionButton("Clear", () => _component.ClearPaint()));
            tools.Items.Add(ActionButton("Fit", () => _canvas.Fit()));
            _flipMapButton = ActionButton("Flip Map", () => _component.ToggleAtlasFlipMap());
            _flipMapButton.Width = 72;
            _flipMapButton.Visible = component.SupportsAtlasTransforms;
            tools.Items.Add(_flipMapButton);
            _rotateAtlasButton = ActionButton("Rotate Atlas 90°", () => _component.RotateAtlasClockwise());
            _rotateAtlasButton.Width = 112;
            _rotateAtlasButton.Visible = component.SupportsAtlasTransforms;
            tools.Items.Add(_rotateAtlasButton);
            _previewButton = ActionButton("Preview", () => _component.TogglePreview());
            _previewButton.Width = 70;
            tools.Items.Add(_previewButton);
            _liveButton = ActionButton("Live", () => _component.ToggleLive());
            _liveButton.Width = 90;
            tools.Items.Add(_liveButton);
            _updateButton = ActionButton("Update", () => _component.UpdateAlgorithm());
            _updateButton.Width = 70;
            tools.Items.Add(_updateButton);

            var settingsTools = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Padding = new Padding(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            (_radiusSlider, _radiusNumber) = AddPainterSetting(
                settingsTools, "Brush Radious", -300, 200, 3, 0.001, 1000);
            (_brushStrengthSlider, _brushStrengthNumber) = AddPainterSetting(
                settingsTools, "Brush Strength", 0, 1000, 3, 0, 1);
            (_smoothStrengthSlider, _smoothStrengthNumber) = AddPainterSetting(
                settingsTools, "Smooth Strength", 0, 1000, 3, 0, 1);
            _radiusSlider.ValueChanged += (_, _) => SliderSettingsChanged();
            _brushStrengthSlider.ValueChanged += (_, _) => SliderSettingsChanged();
            _smoothStrengthSlider.ValueChanged += (_, _) => SliderSettingsChanged();
            _settingsCommitTimer = new UITimer { Interval = 0.18 };
            _settingsCommitTimer.Elapsed += (_, _) =>
            {
                _settingsCommitTimer.Stop();
                CommitPainterSettings();
            };
            foreach (Slider slider in new[] { _radiusSlider, _brushStrengthSlider, _smoothStrengthSlider })
                slider.MouseUp += (_, _) => CommitPainterSettings();
            _radiusNumber.ValueChanged += (_, _) => NumberSettingsChanged();
            _brushStrengthNumber.ValueChanged += (_, _) => NumberSettingsChanged();
            _smoothStrengthNumber.ValueChanged += (_, _) => NumberSettingsChanged();

            var textureTools = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Padding = new Padding(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center,
                Visible = component.SupportsTextures
            };
            textureTools.Items.Add(new Label { Text = "Pick Texture:", VerticalAlignment = VerticalAlignment.Center });
            for (int layer = 0; layer < _textureLayerButtons.Length; layer++)
            {
                int selectedLayer = layer;
                Button layerButton = ActionButton(
                    (layer + 1).ToString(),
                    () => _component.SelectTextureLayer(selectedLayer));
                layerButton.Width = 28;
                _textureLayerButtons[layer] = layerButton;
                textureTools.Items.Add(layerButton);

                Button visibilityButton = IconButton(
                    _visibleTextureIcon,
                    () => _component.ToggleTextureLayerVisibility(selectedLayer));
                visibilityButton.Width = 28;
                visibilityButton.ToolTip = $"Show or hide texture layer {layer + 1}";
                _textureVisibilityButtons[layer] = visibilityButton;
                textureTools.Items.Add(visibilityButton);
            }
            _textureButton = ActionButton("Show All", () => _component.ToggleTextureVisibility());
            _textureButton.Width = 70;
            _editTextureButton = ActionButton("Edit", () => _component.ToggleTextureEdit());
            _distortTextureButton = ActionButton("Distort", () => _component.ToggleTextureDistort());
            _distortTextureButton.Width = 70;
            _rotateTextureButton = ActionButton("Rotate", () => _component.ToggleTextureRotate());
            _rotateTextureButton.Width = 66;
            _fitTextureButton = ActionButton("Fit Texture", () => _component.FitTextureToAtlas());
            _fitTextureButton.Width = 82;
            _applyTextureButton = ActionButton("Apply Layer", () => _component.ApplyTextureToPaint());
            _applyTextureButton.Width = 88;
            _applyCompositeTextureButton = ActionButton(
                "Apply Composite", () => _component.ApplyTextureCompositeToPaint());
            _applyCompositeTextureButton.Width = 112;
            _removeTextureButton = ActionButton("Remove", () => _component.RemoveTextureOverlay());
            textureTools.Items.Add(_textureButton);
            textureTools.Items.Add(_editTextureButton);
            textureTools.Items.Add(_distortTextureButton);
            textureTools.Items.Add(_rotateTextureButton);
            textureTools.Items.Add(_fitTextureButton);
            textureTools.Items.Add(_applyTextureButton);
            textureTools.Items.Add(_applyCompositeTextureButton);
            textureTools.Items.Add(_removeTextureButton);

            var textTextureTools = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Padding = new Padding(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center,
                Visible = component.SupportsTextTextures
            };
            textTextureTools.Items.Add(new Label { Text = "Add Text:", VerticalAlignment = VerticalAlignment.Center });
            for (int layer = 0; layer < _textTextureLayerButtons.Length; layer++)
            {
                int selectedLayer = layer;
                Button layerButton = ActionButton(
                    (layer + 1).ToString(),
                    () =>
                    {
                        _component.SelectTextTextureLayer(selectedLayer);
                        SyncTextTextureControls();
                    });
                layerButton.Width = 28;
                _textTextureLayerButtons[layer] = layerButton;
                textTextureTools.Items.Add(layerButton);

                Button visibilityButton = IconButton(
                    _visibleTextureIcon,
                    () => _component.ToggleTextTextureLayerVisibility(selectedLayer));
                visibilityButton.Width = 28;
                visibilityButton.ToolTip = $"Show or hide text texture {layer + 1}";
                _textTextureVisibilityButtons[layer] = visibilityButton;
                textTextureTools.Items.Add(visibilityButton);
            }
            _textTextureBox = new TextBox { Width = 220 };
            _textTextureBox.TextChanged += (_, _) => TextTextureDraftChanged();
            textTextureTools.Items.Add(_textTextureBox);
            _textFontBox = new DropDown();
            using (var fonts = new InstalledFontCollection())
            {
                foreach (string fontName in fonts.Families
                             .Select(family => family.Name)
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                    _textFontNames.Add(fontName);
            }
            _textFontBox.DataStore = _textFontNames;
            _textFontBox.Width = 180;
            int defaultFont = _textFontNames.FindIndex(
                name => string.Equals(name, "Arial", StringComparison.OrdinalIgnoreCase));
            _textFontBox.SelectedIndex = defaultFont >= 0
                ? defaultFont
                : (_textFontNames.Count > 0 ? 0 : -1);
            _textFontBox.SelectedIndexChanged += (_, _) => TextTextureDraftChanged();
            textTextureTools.Items.Add(_textFontBox);
            textTextureTools.Items.Add(new Label { Text = "Size:", VerticalAlignment = VerticalAlignment.Center });
            _textFontSizeNumber = new NumericStepper
            {
                DecimalPlaces = 2,
                MinValue = 0.01,
                MaxValue = 100000,
                Increment = 0.5,
                Value = 10,
                Width = 82
            };
            _textFontSizeNumber.ValueChanged += (_, _) => TextTextureDraftChanged();
            _textFontSizeNumber.ToolTip = "Text height in atlas/model units before rasterizing";
            textTextureTools.Items.Add(_textFontSizeNumber);
            _setTextTextureButton = ActionButton(
                "Set as Texture",
                () => _component.CommitTextTexture(
                    _textTextureBox.Text,
                    _textFontBox.SelectedIndex >= 0 ? _textFontNames[_textFontBox.SelectedIndex] : null,
                    _textFontSizeNumber.Value));
            _setTextTextureButton.Width = 110;
            _setTextTextureButton.BackgroundColor = Color.FromArgb(72, 164, 92);
            _setTextTextureButton.TextColor = Colors.White;
            textTextureTools.Items.Add(_setTextTextureButton);
            Button duplicateText = ActionButton("Duplicate", () => _component.DuplicateTextTextureLayer());
            duplicateText.Width = 78;
            textTextureTools.Items.Add(duplicateText);
            Button removeText = ActionButton("Remove", () => _component.RemoveTextTextureLayer());
            removeText.Width = 68;
            textTextureTools.Items.Add(removeText);
            Button textUp = ActionButton("Up", () => _component.MoveTextTextureLayer(-1));
            textUp.Width = 42;
            textTextureTools.Items.Add(textUp);
            Button textDown = ActionButton("Down", () => _component.MoveTextTextureLayer(1));
            textDown.Width = 52;
            textTextureTools.Items.Add(textDown);

            var fieldNavigationTools = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Padding = new Padding(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center,
                Visible = component.SupportsFieldCollection
            };
            fieldNavigationTools.Items.Add(new Label { Text = "Fields:", VerticalAlignment = VerticalAlignment.Center });
            fieldNavigationTools.Items.Add(ActionButton("Previous", () => component.SelectPreviousField()));
            _fieldIndicator = new Label
            {
                Text = "1 / 1",
                Width = 62,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            fieldNavigationTools.Items.Add(_fieldIndicator);
            fieldNavigationTools.Items.Add(ActionButton("Next", () => component.SelectNextField()));
            Button addField = ActionButton("Add New Field", () => component.AddNewField());
            addField.Width = 108;
            fieldNavigationTools.Items.Add(addField);
            Button duplicateField = ActionButton("Duplicate", () => component.DuplicateActiveField());
            duplicateField.Width = 82;
            fieldNavigationTools.Items.Add(duplicateField);
            Button removeField = ActionButton("Remove", () => component.RemoveActiveField());
            removeField.Width = 76;
            fieldNavigationTools.Items.Add(removeField);
            _arrangeFieldsButton = ActionButton("Arrange Fields", () => component.ToggleFieldArrangeMode());
            _arrangeFieldsButton.Width = 104;
            fieldNavigationTools.Items.Add(_arrangeFieldsButton);
            Button moveUp = ActionButton("Move Up", () => component.MoveActiveFieldUp());
            moveUp.Width = 78;
            fieldNavigationTools.Items.Add(moveUp);
            Button moveDown = ActionButton("Move Down", () => component.MoveActiveFieldDown());
            moveDown.Width = 88;
            fieldNavigationTools.Items.Add(moveDown);

            var fieldSettingsTools = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Padding = new Padding(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center,
                Visible = component.SupportsFieldCollection
            };
            (_fieldOffsetSlider, _fieldOffsetNumber) = AddPainterSetting(
                fieldSettingsTools, "Field Offset", -1000, 1000, 3, -100000, 100000);
            (_fieldResolutionSlider, _fieldResolutionNumber) = AddPainterSetting(
                fieldSettingsTools, "Resolution (All)", -300, 300, 3, 0.001, 100000);
            (_fieldFrameSlider, _fieldFrameNumber) = AddPainterSetting(
                fieldSettingsTools, "Field Size (All)", -100, 500, 3, 0.1, 100000);
            _fieldOffsetSlider.ValueChanged += (_, _) => FieldSlidersChanged();
            _fieldResolutionSlider.ValueChanged += (_, _) => FieldSlidersChanged();
            _fieldFrameSlider.ValueChanged += (_, _) => FieldSlidersChanged();
            _fieldOffsetNumber.ValueChanged += (_, _) => FieldNumbersChanged();
            _fieldResolutionNumber.ValueChanged += (_, _) => FieldNumbersChanged();
            _fieldFrameNumber.ValueChanged += (_, _) => FieldNumbersChanged();
            _fieldCommitTimer = new UITimer { Interval = 0.22 };
            _fieldCommitTimer.Elapsed += (_, _) =>
            {
                _fieldCommitTimer.Stop();
                CommitFieldSettings();
            };
            foreach (Slider slider in new[] { _fieldOffsetSlider, _fieldResolutionSlider, _fieldFrameSlider })
                slider.MouseUp += (_, _) => CommitFieldSettings();

            var sessionTools = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Padding = new Padding(4, 2, 4, 2),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            sessionTools.Items.Add(new Label { Text = "Session:", VerticalAlignment = VerticalAlignment.Center });
            Button saveSession = ActionButton("Save", () => _component.SavePainterSession());
            saveSession.Width = 68;
            sessionTools.Items.Add(saveSession);
            Button loadSession = ActionButton("Load", () => _component.LoadPainterSession());
            loadSession.Width = 68;
            sessionTools.Items.Add(loadSession);
            Button saveBitmap = ActionButton(
                "Save Bitmap",
                () =>
                {
                    using System.Drawing.Bitmap bitmap = _canvas.CaptureBitmap();
                    _component.SavePainterBitmap(bitmap);
                });
            saveBitmap.Width = 92;
            sessionTools.Items.Add(saveBitmap);

            _canvas = new WasperEtoAtlasCanvas(component);
            var legend = new Label
            {
                TextAlignment = TextAlignment.Center,
                Text = component.PainterLegend + "    |    Wheel: zoom    Middle/right: pan"
            };

            Content = new TableLayout
            {
                Rows =
                {
                    new TableRow(new TableCell(BandedRow(tools, Color.FromArgb(240, 240, 240)), true))
                        { ScaleHeight = false },
                    new TableRow(new TableCell(BandedRow(settingsTools, Color.FromArgb(238, 239, 242)), true))
                        { ScaleHeight = false },
                    new TableRow(new TableCell(BandedRow(textureTools, Color.FromArgb(225, 228, 234)), true))
                        { ScaleHeight = false },
                    new TableRow(new TableCell(BandedRow(textTextureTools, Color.FromArgb(232, 235, 239)), true))
                        { ScaleHeight = false },
                    new TableRow(new TableCell(BandedRow(fieldNavigationTools, Color.FromArgb(225, 228, 234)), true))
                        { ScaleHeight = false },
                    new TableRow(new TableCell(BandedRow(fieldSettingsTools, Color.FromArgb(238, 239, 242)), true))
                        { ScaleHeight = false },
                    new TableRow(new TableCell(BandedRow(sessionTools, Color.FromArgb(225, 228, 234)), true))
                        { ScaleHeight = false },
                    new TableRow(new TableCell(_canvas, true)) { ScaleHeight = true },
                    new TableRow(new TableCell(legend, true)) { ScaleHeight = false }
                }
            };

            Shown += (_, _) => Application.Instance.AsyncInvoke(() =>
            {
                _canvas.Fit();
                RefreshCanvas();
            });

            Closing += (_, e) =>
            {
                e.Cancel = true;
                _settingsCommitTimer.Stop();
                _fieldCommitTimer.Stop();
                _component.PainterEndStroke();
                _component.EndTextureTransform();
                Visible = false;
            };
            Closed += (_, _) => _closed = true;
        }

        /// <summary>True once this window has actually been torn down, as opposed to hidden by the
        /// Closing handler above -- mirrors WasperPlaybackForm.IsClosed.</summary>
        public bool IsClosed => _closed;

        /// <summary>Wraps a toolbar row in a Panel so it gets a solid background band, matching the
        /// WinForms FlowLayoutPanel.BackColor rows. Eto layout containers (StackLayout/TableLayout)
        /// don't paint a background themselves; Panel does. Unverified against a real build.</summary>
        private static Panel BandedRow(Control content, Color backgroundColor)
        {
            // Fixed-width controls in a toolbar must not become the form's minimum width. The
            // horizontal scroller keeps every command available while allowing the painter window
            // to be resized narrower on both Windows and macOS.
            var scroll = new Scrollable
            {
                Content = content,
                ExpandContentWidth = false,
                ExpandContentHeight = true
            };
            return new Panel { BackgroundColor = backgroundColor, Content = scroll };
        }

        private void AddToolButton(StackLayout parent, string text, WasperPaintTool tool)
        {
            Button button = ActionButton(text, () => _component.SetPainterTool(tool));
            button.Width = 70;
            _toolButtons[tool] = button;
            parent.Items.Add(button);
        }

        private static (Slider Slider, NumericStepper Number) AddPainterSetting(
            StackLayout parent,
            string label,
            int sliderMinimum,
            int sliderMaximum,
            int decimalPlaces,
            double numberMinimum,
            double numberMaximum)
        {
            var slider = new Slider
            {
                Orientation = Orientation.Horizontal,
                MinValue = sliderMinimum,
                MaxValue = sliderMaximum,
                Width = 140
            };
            var number = new NumericStepper
            {
                DecimalPlaces = decimalPlaces,
                MinValue = numberMinimum,
                MaxValue = numberMaximum,
                Increment = decimalPlaces > 0 ? 0.01 : 1,
                Width = 78
            };
            var group = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                VerticalContentAlignment = VerticalAlignment.Center,
                Items =
                {
                    new Label { Text = label + ":", Width = 112, TextAlignment = TextAlignment.Right,
                        VerticalAlignment = VerticalAlignment.Center },
                    slider,
                    number
                }
            };
            parent.Items.Add(group);
            return (slider, number);
        }

        private void SliderSettingsChanged()
        {
            if (_syncingSettings)
                return;
            _syncingSettings = true;
            _radiusNumber.Value = Clamp(
                Math.Pow(10.0, _radiusSlider.Value / 100.0),
                _radiusNumber.MinValue,
                _radiusNumber.MaxValue);
            _brushStrengthNumber.Value = _brushStrengthSlider.Value / 1000.0;
            _smoothStrengthNumber.Value = _smoothStrengthSlider.Value / 1000.0;
            _syncingSettings = false;
            _component.PreviewPainterSettings(
                _radiusNumber.Value, _brushStrengthNumber.Value, _smoothStrengthNumber.Value);
            _settingsCommitTimer.Stop();
            _settingsCommitTimer.Start();
            _canvas.Invalidate();
        }

        private void NumberSettingsChanged()
        {
            if (_syncingSettings)
                return;
            SyncSlidersFromNumbers();
            CommitPainterSettings();
        }

        private void CommitPainterSettings()
        {
            _settingsCommitTimer?.Stop();
            _component.CommitPainterSettings(
                _radiusNumber.Value, _brushStrengthNumber.Value, _smoothStrengthNumber.Value);
        }

        private void SyncSlidersFromNumbers()
        {
            _syncingSettings = true;
            _radiusSlider.Value = Math.Max(
                _radiusSlider.MinValue,
                Math.Min(
                    _radiusSlider.MaxValue,
                    (int)Math.Round(Math.Log10(Math.Max(0.001, _radiusNumber.Value)) * 100.0)));
            _brushStrengthSlider.Value = Math.Max(
                0, Math.Min(1000, (int)Math.Round(_brushStrengthNumber.Value * 1000.0)));
            _smoothStrengthSlider.Value = Math.Max(
                0, Math.Min(1000, (int)Math.Round(_smoothStrengthNumber.Value * 1000.0)));
            _syncingSettings = false;
        }

        private void FieldSlidersChanged()
        {
            if (_syncingFieldSettings)
                return;
            _syncingFieldSettings = true;
            _fieldOffsetNumber.Value = Clamp(
                _fieldOffsetSlider.Value / 10.0, _fieldOffsetNumber.MinValue, _fieldOffsetNumber.MaxValue);
            _fieldResolutionNumber.Value = Clamp(
                Math.Pow(10.0, _fieldResolutionSlider.Value / 100.0),
                _fieldResolutionNumber.MinValue,
                _fieldResolutionNumber.MaxValue);
            _fieldFrameNumber.Value = Clamp(
                Math.Pow(10.0, _fieldFrameSlider.Value / 100.0),
                _fieldFrameNumber.MinValue,
                _fieldFrameNumber.MaxValue);
            _syncingFieldSettings = false;
            _component.PreviewFieldSettings(
                _fieldOffsetNumber.Value, _fieldResolutionNumber.Value, _fieldFrameNumber.Value);
            _fieldCommitTimer.Stop();
            _fieldCommitTimer.Start();
            _canvas.Invalidate();
        }

        private void FieldNumbersChanged()
        {
            if (_syncingFieldSettings)
                return;
            SyncFieldSlidersFromNumbers();
            CommitFieldSettings();
        }

        private void CommitFieldSettings()
        {
            _fieldCommitTimer?.Stop();
            _component.CommitFieldSettings(
                _fieldOffsetNumber.Value, _fieldResolutionNumber.Value, _fieldFrameNumber.Value);
        }

        private void SyncFieldSlidersFromNumbers()
        {
            _syncingFieldSettings = true;
            _fieldOffsetSlider.Value = Math.Max(
                _fieldOffsetSlider.MinValue,
                Math.Min(_fieldOffsetSlider.MaxValue, (int)Math.Round(_fieldOffsetNumber.Value * 10.0)));
            _fieldResolutionSlider.Value = Math.Max(
                _fieldResolutionSlider.MinValue,
                Math.Min(
                    _fieldResolutionSlider.MaxValue,
                    (int)Math.Round(Math.Log10(Math.Max(0.001, _fieldResolutionNumber.Value)) * 100.0)));
            _fieldFrameSlider.Value = Math.Max(
                _fieldFrameSlider.MinValue,
                Math.Min(
                    _fieldFrameSlider.MaxValue,
                    (int)Math.Round(Math.Log10(Math.Max(0.1, _fieldFrameNumber.Value)) * 100.0)));
            _syncingFieldSettings = false;
        }

        private void TextTextureDraftChanged()
        {
            if (_syncingTextTexture || !_component.SupportsTextTextures)
                return;
            _component.PreviewTextTexture(
                _textTextureBox.Text,
                _textFontBox.SelectedIndex >= 0 ? _textFontNames[_textFontBox.SelectedIndex] : null,
                _textFontSizeNumber.Value);
            _canvas?.Invalidate();
        }

        private void SyncTextTextureControls()
        {
            if (!_component.SupportsTextTextures)
                return;
            int index = Math.Max(
                0, Math.Min(_component.TextTextureLayerCount - 1, _component.ActiveTextTextureLayer));
            WasperPaintTextureLayer layer =
                _component.TextTextureLayers != null && index < _component.TextTextureLayers.Count
                    ? _component.TextTextureLayers[index]
                    : null;
            _syncingTextTexture = true;
            string text = layer?.TextContent ?? string.Empty;
            if (!string.Equals(_textTextureBox.Text, text, StringComparison.Ordinal))
                _textTextureBox.Text = text;
            string fontName = string.IsNullOrWhiteSpace(layer?.FontName) ? "Arial" : layer.FontName;
            int fontIndex = _textFontNames.FindIndex(
                name => string.Equals(name, fontName, StringComparison.OrdinalIgnoreCase));
            if (fontIndex >= 0 && _textFontBox.SelectedIndex != fontIndex)
                _textFontBox.SelectedIndex = fontIndex;
            double fontSize = Clamp(
                layer?.FontSize > 0.0 ? layer.FontSize : 10.0,
                _textFontSizeNumber.MinValue,
                _textFontSizeNumber.MaxValue);
            if (Math.Abs(_textFontSizeNumber.Value - fontSize) > 1e-9)
                _textFontSizeNumber.Value = fontSize;
            _syncingTextTexture = false;
        }

        private static double Clamp(double value, double minimum, double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static Button ActionButton(string text, Action action)
        {
            var button = new Button { Text = text, Height = 27, Width = 62 };
            button.Click += (_, _) => action();
            return button;
        }

        private static Button IconButton(Bitmap icon, Action action)
        {
            var button = new Button { Height = 27, Width = 34, Image = icon };
            button.Click += (_, _) => action();
            return button;
        }

        private static Bitmap HistoryIcon(bool redo)
        {
            var bitmap = new Bitmap(20, 20, PixelFormat.Format32bppRgba);
            using (var graphics = new Graphics(bitmap))
            {
                graphics.AntiAlias = true;
                graphics.Clear(Colors.Transparent);
                using var pen = new Pen(Color.FromArgb(45, 55, 68), 2.2f)
                {
                    LineCap = PenLineCap.Round
                };
                // GDI+'s DrawArc(rect, startAngle, sweepAngle) has no direct Eto.Drawing equivalent;
                // GraphicsPath.AddArc + DrawPath reproduces it (angles/winding match GDI+'s convention).
                using var arcPath = new GraphicsPath();
                arcPath.AddArc(4, 4, 12, 12, 205, 245);
                graphics.DrawPath(pen, arcPath);
                PointF[] arrow =
                {
                    new PointF(2.5f, 7.5f),
                    new PointF(8.5f, 6.2f),
                    new PointF(6.4f, 12.0f)
                };
                using var brush = new SolidBrush(Color.FromArgb(45, 55, 68));
                graphics.FillPolygon(brush, arrow);
            }
            return redo ? FlipHorizontal(bitmap) : bitmap;
        }

        private static Bitmap VisibilityIcon(bool visible)
        {
            var bitmap = new Bitmap(20, 20, PixelFormat.Format32bppRgba);
            using (var graphics = new Graphics(bitmap))
            {
                graphics.AntiAlias = true;
                graphics.Clear(Colors.Transparent);
                Color color = visible ? Color.FromArgb(45, 55, 68) : Color.FromArgb(105, 105, 105);
                using var pen = new Pen(color, 1.8f) { LineCap = PenLineCap.Round };
                graphics.DrawEllipse(pen, 2.5f, 6.0f, 15.0f, 8.0f);
                using var pupil = new SolidBrush(color);
                graphics.FillEllipse(pupil, 8.0f, 8.0f, 4.0f, 4.0f);
                if (!visible)
                    graphics.DrawLine(pen, 3.0f, 3.0f, 17.0f, 17.0f);
            }
            return bitmap;
        }

        /// <summary>System.Drawing.Bitmap has RotateFlip built in; Eto.Drawing.Bitmap does not, so
        /// HistoryIcon's redo variant is mirrored by drawing the finished bitmap back onto a fresh one
        /// with a horizontally-flipped transform instead.</summary>
        private static Bitmap FlipHorizontal(Bitmap source)
        {
            var flipped = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppRgba);
            using (var graphics = new Graphics(flipped))
            {
                graphics.AntiAlias = true;
                graphics.SaveTransform();
                graphics.MultiplyTransform(Eto.Drawing.Matrix.Create(-1, 0, 0, 1, source.Width, 0));
                graphics.DrawImage(source, 0, 0);
                graphics.RestoreTransform();
            }
            source.Dispose();
            return flipped;
        }

        public void RefreshCanvas()
        {
            foreach (KeyValuePair<WasperPaintTool, Button> pair in _toolButtons)
            {
                bool active = pair.Key == _component.ActiveTool;
                pair.Value.BackgroundColor = active
                    ? pair.Key == WasperPaintTool.Pull
                        ? ToEtoColor(_component.PullToolColor)
                        : pair.Key == WasperPaintTool.Push
                            ? ToEtoColor(_component.PushToolColor)
                            : pair.Key == WasperPaintTool.Zero
                                ? ToEtoColor(_component.ZeroToolColor)
                            : Color.FromArgb(245, 215, 120)
                    : SystemColors.Control;
                pair.Value.TextColor = active && GetBrightness(pair.Value.BackgroundColor) < 0.45f
                    ? Colors.White
                    : SystemColors.ControlText;
            }
            bool smoothing = _component.ActiveTool == WasperPaintTool.Smooth;
            _smoothSquareButton.BackgroundColor = smoothing &&
                _component.SmoothRegionShape == WasperSmoothRegionShape.Square
                    ? Colors.Gold
                    : SystemColors.Control;
            _smoothFreeformButton.BackgroundColor = smoothing &&
                _component.SmoothRegionShape == WasperSmoothRegionShape.Freeform
                    ? Colors.Gold
                    : SystemColors.Control;
            _fieldIndicator.Text = $"{_component.ActiveFieldIndex + 1} / {Math.Max(1, _component.FieldCount)}";
            _arrangeFieldsButton.BackgroundColor = _component.FieldArrangeMode
                ? Color.FromArgb(238, 158, 65)
                : SystemColors.Control;
            if (_component.SupportsFieldCollection)
            {
                _syncingFieldSettings = true;
                _fieldOffsetNumber.Value = Clamp(
                    _component.FieldOffset, _fieldOffsetNumber.MinValue, _fieldOffsetNumber.MaxValue);
                _fieldResolutionNumber.Value = Clamp(
                    _component.FieldResolution, _fieldResolutionNumber.MinValue, _fieldResolutionNumber.MaxValue);
                _fieldFrameNumber.Value = Clamp(
                    _component.FieldFrameSize, _fieldFrameNumber.MinValue, _fieldFrameNumber.MaxValue);
                _syncingFieldSettings = false;
                SyncFieldSlidersFromNumbers();
            }
            _previewButton.BackgroundColor = _component.PreviewEnabled
                ? Color.FromArgb(105, 145, 235)
                : SystemColors.Control;
            _previewButton.TextColor = _component.PreviewEnabled ? Colors.White : SystemColors.ControlText;

            _liveButton.Text = _component.LiveEnabled ? "▶ Live" : "Ⅱ Paused";
            _liveButton.BackgroundColor = _component.LiveEnabled
                ? Color.FromArgb(72, 164, 92)
                : Color.FromArgb(194, 70, 70);
            _liveButton.TextColor = Colors.White;

            _updateButton.Enabled = _component.UpdateEnabled;
            _updateButton.BackgroundColor = !_component.UpdateEnabled
                ? Color.FromArgb(205, 205, 205)
                : _component.HasPendingUpdate
                    ? Color.FromArgb(238, 158, 65)
                    : SystemColors.Control;
            _updateButton.TextColor = _component.UpdateEnabled && _component.HasPendingUpdate
                ? Colors.White
                : SystemColors.ControlText;
            _undoButton.Enabled = _component.CanUndoPaint;
            _redoButton.Enabled = _component.CanRedoPaint;
            _syncingSettings = true;
            _radiusNumber.Value = Clamp(_component.PainterRadius, _radiusNumber.MinValue, _radiusNumber.MaxValue);
            _brushStrengthNumber.Value = Clamp(_component.PainterBrushStrength, 0, 1);
            _smoothStrengthNumber.Value = Clamp(_component.PainterSmoothStrength, 0, 1);
            _syncingSettings = false;
            SyncSlidersFromNumbers();
            _radiusSlider.Enabled = _component.PainterRadiusEditable;
            _radiusNumber.Enabled = _component.PainterRadiusEditable;
            _brushStrengthSlider.Enabled = _component.PainterBrushStrengthEditable;
            _brushStrengthNumber.Enabled = _component.PainterBrushStrengthEditable;
            _smoothStrengthSlider.Enabled = _component.PainterSmoothStrengthEditable;
            _smoothStrengthNumber.Enabled = _component.PainterSmoothStrengthEditable;

            bool hasTexture = _component.HasTextureSource;
            bool hasAnyTexture = _component.TextureLayers != null &&
                                 _component.TextureLayers.Any(layer => layer?.Bitmap != null);
            bool hasVisibleTextures = _component.TextureLayers != null &&
                                       _component.TextureLayers.Any(layer =>
                                           layer?.Bitmap != null && layer.Visible &&
                                           (!layer.IsText || layer.TextCommitted));
            WasperPaintTextureLayer activeLayer =
                _component.TextureLayers != null &&
                _component.ActiveTextureLayer >= 0 &&
                _component.ActiveTextureLayer < _component.TextureLayers.Count
                    ? _component.TextureLayers[_component.ActiveTextureLayer]
                    : null;
            bool activeTextureApplicable = hasTexture && (activeLayer?.IsText != true || activeLayer.TextCommitted);
            for (int layer = 0; layer < _textureLayerButtons.Length; layer++)
            {
                Button button = _textureLayerButtons[layer];
                Button visibilityButton = _textureVisibilityButtons[layer];
                bool available = layer < _component.TextureLayerCount;
                WasperPaintTextureLayer textureLayer =
                    available && layer < _component.TextureLayers.Count
                        ? _component.TextureLayers[layer]
                        : null;
                button.Enabled = available;
                button.Text = (layer + 1).ToString();
                button.BackgroundColor = layer == _component.ActiveTextureLayer
                    ? Color.FromArgb(72, 142, 184)
                    : SystemColors.Control;
                button.TextColor = layer == _component.ActiveTextureLayer
                    ? Colors.White
                    : SystemColors.ControlText;
                bool layerHasTexture = textureLayer?.Bitmap != null;
                visibilityButton.Enabled = layerHasTexture;
                visibilityButton.Image = textureLayer?.Visible == false ? _hiddenTextureIcon : _visibleTextureIcon;
                visibilityButton.BackgroundColor = layerHasTexture && textureLayer.Visible
                    ? Color.FromArgb(105, 145, 235)
                    : SystemColors.Control;
            }
            _textureButton.Enabled = hasAnyTexture;
            _editTextureButton.Enabled = hasTexture;
            _distortTextureButton.Enabled = hasTexture;
            _rotateTextureButton.Enabled = hasTexture;
            _flipMapButton.Enabled = _component.PainterMesh != null;
            _rotateAtlasButton.Enabled = _component.PainterMesh != null;
            _fitTextureButton.Enabled = hasTexture;
            _applyTextureButton.Enabled = activeTextureApplicable;
            _applyCompositeTextureButton.Enabled = hasVisibleTextures;
            _removeTextureButton.Enabled = hasTexture;
            _textureButton.Text = hasVisibleTextures ? "Hide All" : "Show All";
            _textureButton.BackgroundColor = hasVisibleTextures ? Color.FromArgb(105, 145, 235) : SystemColors.Control;
            _textureButton.TextColor = hasVisibleTextures ? Colors.White : SystemColors.ControlText;
            _editTextureButton.BackgroundColor = _component.TextureEditMode
                ? Color.FromArgb(238, 158, 65)
                : SystemColors.Control;
            _editTextureButton.TextColor = _component.TextureEditMode ? Colors.White : SystemColors.ControlText;
            _distortTextureButton.BackgroundColor = _component.TextureDistortMode
                ? Color.FromArgb(176, 92, 202)
                : SystemColors.Control;
            _distortTextureButton.TextColor = _component.TextureDistortMode ? Colors.White : SystemColors.ControlText;
            _rotateTextureButton.BackgroundColor = _component.TextureRotateMode
                ? Color.FromArgb(176, 92, 202)
                : SystemColors.Control;
            _rotateTextureButton.TextColor = _component.TextureRotateMode ? Colors.White : SystemColors.ControlText;
            _flipMapButton.BackgroundColor = _component.AtlasFlipMap
                ? Color.FromArgb(72, 142, 184)
                : SystemColors.Control;
            _flipMapButton.TextColor = _component.AtlasFlipMap ? Colors.White : SystemColors.ControlText;
            _applyTextureButton.BackgroundColor = activeTextureApplicable
                ? Color.FromArgb(72, 164, 92)
                : Color.FromArgb(205, 205, 205);
            _applyTextureButton.TextColor = activeTextureApplicable ? Colors.White : SystemColors.ControlText;
            _applyCompositeTextureButton.BackgroundColor = hasVisibleTextures
                ? Color.FromArgb(72, 164, 92)
                : Color.FromArgb(205, 205, 205);
            _applyCompositeTextureButton.TextColor = hasVisibleTextures ? Colors.White : SystemColors.ControlText;
            bool textLayerActive = _component.ActiveTextureLayer >= _component.TextureLayerCount;
            for (int layer = 0; layer < _textTextureLayerButtons.Length; layer++)
            {
                Button button = _textTextureLayerButtons[layer];
                Button visibilityButton = _textTextureVisibilityButtons[layer];
                bool available = layer < _component.TextTextureLayerCount;
                WasperPaintTextureLayer textLayer =
                    available && _component.TextTextureLayers != null &&
                    layer < _component.TextTextureLayers.Count
                        ? _component.TextTextureLayers[layer]
                        : null;
                button.Enabled = available;
                button.BackgroundColor = textLayerActive && layer == _component.ActiveTextTextureLayer
                    ? Color.FromArgb(72, 142, 184)
                    : SystemColors.Control;
                button.TextColor = textLayerActive && layer == _component.ActiveTextTextureLayer
                    ? Colors.White
                    : SystemColors.ControlText;
                bool hasText = textLayer?.Bitmap != null;
                visibilityButton.Enabled = hasText;
                visibilityButton.Image = textLayer?.Visible == false ? _hiddenTextureIcon : _visibleTextureIcon;
                visibilityButton.BackgroundColor = hasText && textLayer.Visible
                    ? Color.FromArgb(105, 145, 235)
                    : SystemColors.Control;
            }
            if (textLayerActive)
                SyncTextTextureControls();
            _setTextTextureButton.Enabled = !string.IsNullOrWhiteSpace(_textTextureBox.Text);
            _setTextTextureButton.BackgroundColor = _setTextTextureButton.Enabled
                ? Color.FromArgb(72, 164, 92)
                : Color.FromArgb(205, 205, 205);
            _setTextTextureButton.TextColor = _setTextTextureButton.Enabled ? Colors.White : SystemColors.ControlText;
            _canvas.Invalidate();
        }

        /// <summary>System.Drawing.Color -> Eto.Drawing.Color, for the tool colors IWasperPainterHost
        /// exposes as System.Drawing.Color (the contract is toolkit-independent, so it uses
        /// System.Drawing.Color as the plain-data color type both WinForms and Eto UIs read from).</summary>
        private static Color ToEtoColor(System.Drawing.Color color)
        {
            return Color.FromArgb(color.R, color.G, color.B, color.A);
        }

        /// <summary>Eto.Drawing.Color has no GetBrightness() (CS1061, confirmed by a real build) --
        /// reproduces System.Drawing.Color.GetBrightness()'s HSL-lightness formula
        /// ((max + min) / 2 of the R/G/B channels) using Eto.Drawing.Color's 0..1 float channels.</summary>
        private static float GetBrightness(Color color)
        {
            float max = Math.Max(color.R, Math.Max(color.G, color.B));
            float min = Math.Min(color.R, Math.Min(color.G, color.B));
            return (max + min) / 2f;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Keys.Escape && _component.ActiveTool != WasperPaintTool.None)
            {
                _component.SetPainterTool(WasperPaintTool.None);
                _component.ClearPainterHover();
                RefreshCanvas();
                e.Handled = true;
            }
        }

        internal void FitCanvas()
        {
            _canvas.Fit();
        }

        public void PresentCanvasFrame()
        {
            _canvas.Invalidate();
        }

        /// <summary>Positions and shows the window near the current mouse cursor, matching
        /// WasperPaintForm.ShowNearCursor. Follows WasperPlaybackForm.ShowNearCursor's simpler
        /// Eto.Forms.Mouse-only approach (Components/5.0_Gcode/WASPer_Gc05PlaybackForm.cs) rather than
        /// WinForms' screen-working-area clamping: no in-repo Eto precedent confirms a
        /// Screen.FromPoint/PrimaryScreen API shaped like WinForms' Screen class exists (the one
        /// confirmed Eto.Forms.Screen member in this repo, WASPerMascot.cs's Screen.FromControl, needs a
        /// control already on screen, which this window is not yet before Show()) -- so this positions
        /// near the cursor without off-screen clamping, same tradeoff Gc05 already made.</summary>
        public void ShowNearCursor()
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            if (Mouse.IsSupported)
            {
                PointF cursor = Mouse.Position;
                Location = new Eto.Drawing.Point((int)cursor.X + 20, (int)cursor.Y - (Height / 2));
            }
            if (!Visible)
                Show();
            BringToFront();
            Focus();
            _canvas.Fit();
            RefreshCanvas();
        }
    }

    /// <summary>
    /// Eto.Drawable replacement for the WinForms owner-drawn <see cref="WasperAtlasCanvas"/> (a
    /// System.Windows.Forms.Control with OnPaint/OnMouseDown/etc overrides). See the class doc comment
    /// on <see cref="WasperEtoPaintForm"/> above for the full risk writeup -- summary of what changed
    /// here specifically:
    /// - GDI+'s 3-point-affine Graphics.DrawImage (both the plain skewed texture draw and, per grid
    ///   cell, the warped/distorted texture draw) is reproduced by <see cref="DrawImageAffine"/>, an
    ///   explicit affine Matrix built from the same 3 destination points GDI+ would have taken.
    /// - GDI+'s ImageAttributes/ColorMatrix opacity blending is reproduced by pre-baking each texture
    ///   layer's opacity into its alpha channel once (via Bitmap.Lock()/SetPixel), cached per layer and
    ///   rebuilt only when that layer's Bitmap reference/Opacity/Revision changes -- see
    ///   <see cref="GetOpacityBitmap"/>. This is the same "build once, invalidate on revision change"
    ///   strategy WinForms' EnsureAtlasCache/EnsureTextureCache already used, so it is not a new
    ///   per-frame cost, but the per-pixel bake loop itself (Bitmap.Lock/SetPixel) has no in-repo
    ///   precedent and needs the interactive check to confirm it performs acceptably on realistic
    ///   texture sizes.
    /// - WasperPaintTextureLayer.Bitmap/CaptureBitmap's return type are System.Drawing.Bitmap (the
    ///   toolkit-independent contract type); <see cref="ConvertToEtoBitmap"/>/<see cref="ToSystemDrawingBitmap"/>
    ///   cross that boundary via the PNG-encoded-MemoryStream round trip Sm01EtoManagerForm.ConvertToEtoImage
    ///   already established as this repo's pattern for it.
    /// - WinForms' explicit mouse Capture has no confirmed portable Eto equivalent (see the class doc
    ///   comment above) -- left unset; every drag gesture still gates on its own bool flag exactly as
    ///   before, so this only matters if a drag leaves the canvas bounds before mouse-up.
    /// - Mouse wheel zoom uses e.Delta.Height directly as a notch count (Eto's MouseEventArgs.Delta is
    ///   a SizeF, not WinForms' /120-scaled int) -- approximate, needs the interactive check.
    /// - Cursor shapes are mapped to the nearest named Eto.Forms.Cursors entry (Move for pan instead of
    ///   Hand, which Eto's named cursor set does not include) -- cosmetic only, does not affect
    ///   functionality if a mapping is off.
    /// </summary>
    internal sealed class WasperEtoAtlasCanvas : Drawable
    {
        private sealed class LayerBitmapCache
        {
            public System.Drawing.Bitmap SourceBitmap;
            public int Revision = -1;
            public double Opacity = -1.0;
            public Bitmap EtoBitmap;
        }

        private readonly IWasperPainterHost _component;
        private double _scale = 1.0;
        private double _panX;
        private double _panY;
        private bool _fitted;
        private bool _painting;
        private bool _selectingSmoothRegion;
        private readonly List<PointF> _smoothRegionPoints = new List<PointF>();
        private bool _panning;
        private bool _textureMoving;
        private bool _fieldDragging;
        private int _textureHandle = -1;
        private PointF _lastMouse;
        private PointF _hoverMouse;
        private Bitmap _atlasCache;
        private Bitmap _textureCache;
        private int _cachedPaintRevision = -1;
        private int _viewRevision;
        private int _cachedViewRevision = -1;
        private int _cachedTextureRevision = -1;
        private int _cachedTextureViewRevision = -1;
        private readonly Dictionary<WasperPaintTextureLayer, LayerBitmapCache> _layerBitmapCache =
            new Dictionary<WasperPaintTextureLayer, LayerBitmapCache>();
        private readonly UITimer _hoverTimer;
        private Point3d _pendingHoverPoint = Point3d.Unset;
        private bool _hoverPending;
        // Self-tracked rather than querying a UITimer.Started/IsStarted property: no in-repo precedent
        // confirms such a property exists (WASPerMascot.cs/Sm01EtoManagerForm's UITimer usage only ever
        // calls Start()/Stop(), never reads a running-state back), so this avoids relying on an
        // unverified member the way Gc05PlaybackForm.IsClosed avoided IsDisposed for the same reason.
        private bool _hoverTimerRunning;
        private readonly Font _dimensionFont = new Font(FontFamilies.Sans, 9f, FontStyle.Bold);

        public WasperEtoAtlasCanvas(IWasperPainterHost component)
        {
            _component = component;
            CanFocus = true;
            MinimumSize = new Size(320, 220);
            BackgroundColor = Color.FromArgb(32, 35, 42);
            _hoverTimer = new UITimer { Interval = 0.025 };
            _hoverTimer.Elapsed += (_, _) => FlushPainterHover();
        }

        private void QueuePainterHover(Point3d atlasPoint)
        {
            _pendingHoverPoint = atlasPoint;
            _hoverPending = true;
            if (!_hoverTimerRunning)
            {
                _hoverTimer.Start();
                _hoverTimerRunning = true;
            }
        }

        private void FlushPainterHover()
        {
            if (!_hoverPending)
            {
                _hoverTimer.Stop();
                _hoverTimerRunning = false;
                return;
            }
            Point3d point = _pendingHoverPoint;
            _hoverPending = false;
            _component.PainterHover(point);
        }

        private void CancelPainterHover()
        {
            _hoverPending = false;
            _pendingHoverPoint = Point3d.Unset;
            _hoverTimer.Stop();
            _hoverTimerRunning = false;
        }

        /// <summary>Renders the current frame into a System.Drawing.Bitmap for
        /// IWasperPainterHost.SavePainterBitmap -- see the class doc comment for the conversion-boundary
        /// pattern.</summary>
        public System.Drawing.Bitmap CaptureBitmap()
        {
            int width = Math.Max(1, Width);
            int height = Math.Max(1, Height);
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgba);
            using (var graphics = new Graphics(bitmap))
            {
                graphics.Clear(BackgroundColor);
                Render(graphics);
            }
            return ToSystemDrawingBitmap(bitmap);
        }

        public void Fit()
        {
            Mesh mesh = _component.PainterMesh;
            if (mesh == null || mesh.Vertices.Count == 0 || Width <= 0)
                return;
            Plane plane = _component.PainterPlane;
            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;
            foreach (Point3f vertex in mesh.Vertices)
            {
                Point3d point = _component.TransformAtlasPoint(vertex);
                Vector3d delta = point - plane.Origin;
                double x = delta * plane.XAxis;
                double y = delta * plane.YAxis;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
            double width = Math.Max(maxX - minX, 1e-6);
            double height = Math.Max(maxY - minY, 1e-6);
            double fitPadding = _component.ShowAtlasDimensions ? 120.0 : 50.0;
            _scale = Math.Max(
                1e-6,
                Math.Min((Width - fitPadding) / width, (Height - fitPadding) / height));
            _panX = Width * 0.5 - (minX + maxX) * 0.5 * _scale;
            _panY = Height * 0.5 + (minY + maxY) * 0.5 * _scale;
            _fitted = true;
            _viewRevision++;
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            _viewRevision++;
            // Refit after every real layout change. This also handles the first Eto layout pass,
            // where ShowNearCursor can run before the Drawable has received its final dimensions.
            Fit();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.AntiAlias = true;
            // Explicit clear before Render(), matching CaptureBitmap()'s existing pattern -- Render()
            // returns immediately without drawing anything when PainterMesh is null/invalid (e.g.
            // before the host has computed a mesh yet), and unlike WinForms' Control.BackColor, Eto's
            // Drawable does not appear to reliably auto-paint BackgroundColor on its own, so that
            // empty state was rendering as unstyled default gray rather than this canvas's intended
            // dark background -- easy to misread as "the atlas isn't showing" even in a legitimate
            // not-yet-computed state.
            e.Graphics.Clear(BackgroundColor);
            Render(e.Graphics);
        }

        private void Render(Graphics graphics)
        {
            Mesh mesh = _component.PainterMesh;
            if (mesh == null || !mesh.IsValid)
                return;
            if (!_fitted)
                Fit();
            EnsureAtlasCache(mesh);
            if (_atlasCache != null)
                graphics.DrawImage(_atlasCache, 0, 0);

            if (_textureMoving || _textureHandle >= 0)
            {
                DrawTextureOverlay(graphics, true);
            }
            else
            {
                EnsureTextureCache();
                if (_textureCache != null)
                    graphics.DrawImage(_textureCache, 0, 0);
            }
            DrawActiveFieldSelection(graphics);
            DrawBrushCursor(graphics);
            DrawSmoothRegion(graphics);
            DrawAtlasDimensions(graphics, mesh);
        }

        private void DrawAtlasDimensions(Graphics graphics, Mesh mesh)
        {
            if (!_component.ShowAtlasDimensions || mesh == null || mesh.Vertices.Count == 0)
                return;
            Plane plane = _component.PainterPlane;
            IList<WasperPaintAtlasBounds> requested = _component.AtlasDimensionBounds;
            var bounds = requested == null || requested.Count == 0
                ? new List<WasperPaintAtlasBounds>()
                : requested.ToList();
            if (bounds.Count == 0)
            {
                BoundingBox box = mesh.GetBoundingBox(plane);
                bounds.Add(new WasperPaintAtlasBounds
                {
                    MinX = box.Min.X,
                    MinY = box.Min.Y,
                    MaxX = box.Max.X,
                    MaxY = box.Max.Y
                });
            }
            using var pen = new Pen(Color.FromArgb(240, 240, 245, 225), 1.5f);
            using var brush = new SolidBrush(Color.FromArgb(245, 245, 245));
            foreach (WasperPaintAtlasBounds item in bounds)
                DrawAtlasDimension(graphics, item, plane, pen, brush);
        }

        private void DrawActiveFieldSelection(Graphics graphics)
        {
            if (!_component.SupportsFieldCollection)
                return;
            IList<WasperPaintAtlasBounds> bounds = _component.AtlasDimensionBounds;
            int index = _component.ActiveFieldIndex;
            if (bounds == null || index < 0 || index >= bounds.Count)
                return;
            WasperPaintAtlasBounds selected = bounds[index];
            PointF[] outline =
            {
                ToAtlasScreen(AtlasPoint(selected.MinX, selected.MinY)),
                ToAtlasScreen(AtlasPoint(selected.MaxX, selected.MinY)),
                ToAtlasScreen(AtlasPoint(selected.MaxX, selected.MaxY)),
                ToAtlasScreen(AtlasPoint(selected.MinX, selected.MaxY))
            };
            using var shadow = new Pen(Color.FromArgb(16, 20, 28, 230), 7f) { LineJoin = PenLineJoin.Round };
            using var highlight = new Pen(Colors.DeepSkyBlue, 4f) { LineJoin = PenLineJoin.Round };
            graphics.DrawPolygon(shadow, outline);
            graphics.DrawPolygon(highlight, outline);
        }

        private void DrawAtlasDimension(
            Graphics graphics,
            WasperPaintAtlasBounds bounds,
            Plane plane,
            Pen pen,
            Brush brush)
        {
            Point3d[] corners =
            {
                AtlasPoint(bounds.MinX, bounds.MinY),
                AtlasPoint(bounds.MaxX, bounds.MinY),
                AtlasPoint(bounds.MaxX, bounds.MaxY),
                AtlasPoint(bounds.MinX, bounds.MaxY)
            };
            double minX = double.PositiveInfinity;
            double minY = double.PositiveInfinity;
            double maxX = double.NegativeInfinity;
            double maxY = double.NegativeInfinity;
            foreach (Point3d corner in corners)
            {
                Point3d transformed = _component.TransformAtlasPoint(corner);
                Vector3d delta = transformed - plane.Origin;
                double x = delta * plane.XAxis;
                double y = delta * plane.YAxis;
                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
            PointF bottomLeft = ToScreen(AtlasPoint(minX, minY));
            PointF bottomRight = ToScreen(AtlasPoint(maxX, minY));
            PointF topLeft = ToScreen(AtlasPoint(minX, maxY));
            const float gap = 22f;

            float dimensionY = Math.Max(bottomLeft.Y, bottomRight.Y) + gap;
            graphics.DrawLine(pen, bottomLeft.X, dimensionY, bottomRight.X, dimensionY);
            graphics.DrawLine(pen, bottomLeft.X, dimensionY - 5, bottomLeft.X, dimensionY + 5);
            graphics.DrawLine(pen, bottomRight.X, dimensionY - 5, bottomRight.X, dimensionY + 5);
            string widthText = $"{Math.Abs(maxX - minX):0.###}";
            SizeF widthSize = graphics.MeasureString(_dimensionFont, widthText);
            graphics.DrawText(
                _dimensionFont, brush, (bottomLeft.X + bottomRight.X - widthSize.Width) * 0.5f, dimensionY + 3,
                widthText);

            float dimensionX = Math.Min(bottomLeft.X, topLeft.X) - gap;
            graphics.DrawLine(pen, dimensionX, bottomLeft.Y, dimensionX, topLeft.Y);
            graphics.DrawLine(pen, dimensionX - 5, bottomLeft.Y, dimensionX + 5, bottomLeft.Y);
            graphics.DrawLine(pen, dimensionX - 5, topLeft.Y, dimensionX + 5, topLeft.Y);
            string heightText = $"{Math.Abs(maxY - minY):0.###}";
            SizeF heightSize = graphics.MeasureString(_dimensionFont, heightText);
            graphics.SaveTransform();
            graphics.TranslateTransform(
                dimensionX - heightSize.Height - 3, (bottomLeft.Y + topLeft.Y) * 0.5f);
            graphics.RotateTransform(-90f);
            graphics.DrawText(_dimensionFont, brush, -heightSize.Width * 0.5f, 0, heightText);
            graphics.RestoreTransform();
        }

        private void DrawSmoothRegion(Graphics graphics)
        {
            if (!_selectingSmoothRegion || _smoothRegionPoints.Count < 2)
                return;
            PointF[] boundary = SmoothRegionScreenBoundary();
            if (boundary.Length < 2)
                return;
            using var fill = new SolidBrush(Color.FromArgb(255, 205, 35, 45));
            using var outline = new Pen(Colors.Gold, 2) { DashStyle = DashStyles.Dash };
            if (boundary.Length >= 3)
            {
                graphics.FillPolygon(fill, boundary);
                graphics.DrawPolygon(outline, boundary);
            }
            else
            {
                graphics.DrawLines(outline, boundary);
            }
        }

        private PointF[] SmoothRegionScreenBoundary()
        {
            if (_component.SmoothRegionShape == WasperSmoothRegionShape.Freeform)
                return _smoothRegionPoints.ToArray();
            PointF start = _smoothRegionPoints[0];
            PointF end = _smoothRegionPoints[_smoothRegionPoints.Count - 1];
            return new[]
            {
                start,
                new PointF(end.X, start.Y),
                end,
                new PointF(start.X, end.Y)
            };
        }

        private void EnsureTextureCache()
        {
            int stackRevision = TextureStackRevision();
            bool anyVisible = _component.TextureLayers != null &&
                              _component.TextureLayers.Any(layer => layer?.Bitmap != null && layer.Visible);
            if (!anyVisible)
            {
                _textureCache?.Dispose();
                _textureCache = null;
                _cachedTextureRevision = stackRevision;
                _cachedTextureViewRevision = _viewRevision;
                return;
            }
            int width = Math.Max(1, Width);
            int height = Math.Max(1, Height);
            bool sizeChanged = _textureCache == null || _textureCache.Width != width || _textureCache.Height != height;
            if (!sizeChanged && _cachedTextureRevision == stackRevision && _cachedTextureViewRevision == _viewRevision)
                return;
            _textureCache?.Dispose();
            _textureCache = new Bitmap(width, height, PixelFormat.Format32bppRgba);
            using (var graphics = new Graphics(_textureCache))
            {
                graphics.AntiAlias = true;
                DrawTextureOverlay(graphics, false);
            }
            _cachedTextureRevision = stackRevision;
            _cachedTextureViewRevision = _viewRevision;
        }

        private int TextureStackRevision()
        {
            unchecked
            {
                int revision = _component.ActiveTextureLayer;
                foreach (WasperPaintTextureLayer layer in
                         _component.TextureLayers ?? new List<WasperPaintTextureLayer>())
                {
                    revision = revision * 31 + (layer?.Revision ?? 0);
                    revision = revision * 31 + (layer?.Visible == true ? 1 : 0);
                }
                return revision;
            }
        }

        private void EnsureAtlasCache(Mesh mesh)
        {
            int width = Math.Max(1, Width);
            int height = Math.Max(1, Height);
            bool sizeChanged = _atlasCache == null || _atlasCache.Width != width || _atlasCache.Height != height;
            if (!sizeChanged &&
                _cachedPaintRevision == _component.PainterVisualRevision &&
                _cachedViewRevision == _viewRevision)
                return;

            _atlasCache?.Dispose();
            _atlasCache = new Bitmap(width, height, PixelFormat.Format32bppRgba);
            using (var graphics = new Graphics(_atlasCache))
            {
                graphics.AntiAlias = true;
                graphics.Clear(BackgroundColor);
                DrawAtlasField(graphics, mesh);
            }
            _cachedPaintRevision = _component.PainterVisualRevision;
            _cachedViewRevision = _viewRevision;
        }

        private void DrawAtlasField(Graphics graphics, Mesh mesh)
        {
            for (int faceIndex = 0; faceIndex < mesh.Faces.Count; faceIndex++)
            {
                MeshFace face = mesh.Faces[faceIndex];
                int[] vertices = face.IsQuad
                    ? new[] { face.A, face.B, face.C, face.D }
                    : new[] { face.A, face.B, face.C };
                var polygon = new PointF[vertices.Length];
                var colors = new System.Drawing.Color[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    polygon[i] = ToAtlasScreen(mesh.Vertices[vertices[i]]);
                    colors[i] = mesh.VertexColors.Count == mesh.Vertices.Count
                        ? mesh.VertexColors[vertices[i]]
                        : System.Drawing.Color.RoyalBlue;
                }
                using var brush = new SolidBrush(ToEtoColor(AverageColor(colors)));
                graphics.FillPolygon(brush, polygon);
            }

            if (_component.PainterMarkers != null)
            {
                foreach (WasperPaintMarker marker in _component.PainterMarkers)
                {
                    Color markerColor = ToEtoColor(marker.Color);
                    using var pen = new Pen(markerColor, (float)marker.Thickness);
                    if (marker.Thickness >= 2 && marker.Color.A >= 200)
                    {
                        using var outline = new Pen(
                            Color.FromArgb(20, 22, 28, 210), (float)marker.Thickness + 3);
                        graphics.DrawLine(outline, ToAtlasScreen(marker.Line.From), ToAtlasScreen(marker.Line.To));
                    }
                    graphics.DrawLine(pen, ToAtlasScreen(marker.Line.From), ToAtlasScreen(marker.Line.To));
                }
            }
        }

        private static System.Drawing.Color AverageColor(IList<System.Drawing.Color> colors)
        {
            int alpha = 0;
            int red = 0;
            int green = 0;
            int blue = 0;
            foreach (System.Drawing.Color color in colors)
            {
                alpha += color.A;
                red += color.R;
                green += color.G;
                blue += color.B;
            }
            int count = Math.Max(1, colors.Count);
            return System.Drawing.Color.FromArgb(alpha / count, red / count, green / count, blue / count);
        }

        private void DrawBrushCursor(Graphics graphics)
        {
            if (_component.ActiveTool == WasperPaintTool.None)
                return;
            float radius = (float)Math.Max(1.0, _component.PainterRadius * _scale);
            Color color = _component.ActiveTool == WasperPaintTool.Pull
                ? ToEtoColor(_component.PullToolColor)
                : _component.ActiveTool == WasperPaintTool.Push
                    ? ToEtoColor(_component.PushToolColor)
                    : _component.ActiveTool == WasperPaintTool.Zero
                        ? ToEtoColor(_component.ZeroToolColor)
                    : _component.ActiveTool == WasperPaintTool.Smooth
                        ? Colors.Gold
                        : Color.FromArgb(85, 125, 220);
            using var outline = new Pen(Colors.Black, 5);
            using var inner = new Pen(color, 3);
            var circle = new RectangleF(
                _hoverMouse.X - radius, _hoverMouse.Y - radius, radius * 2, radius * 2);
            graphics.DrawEllipse(outline, circle);
            graphics.DrawEllipse(inner, circle);
        }

        private void DrawTextureOverlay(Graphics graphics, bool interactive)
        {
            IList<WasperPaintTextureLayer> layers = _component.TextureLayers;
            if (layers == null)
                return;
            for (int index = 0; index < layers.Count; index++)
            {
                WasperPaintTextureLayer layer = layers[index];
                if (layer?.Bitmap == null || !layer.Visible)
                    continue;
                DrawTextureLayer(graphics, layer, index == _component.ActiveTextureLayer, interactive);
            }
        }

        private void DrawTextureLayer(
            Graphics graphics,
            WasperPaintTextureLayer layer,
            bool selected,
            bool interactive)
        {
            IList<Point2d> corners = layer.Placement.Corners;
            PointF bottomLeft = TextureScreenPoint(corners[0]);
            PointF bottomRight = TextureScreenPoint(corners[1]);
            PointF topRight = TextureScreenPoint(corners[2]);
            PointF topLeft = TextureScreenPoint(corners[3]);
            Bitmap opacityBitmap = GetOpacityBitmap(layer);
            if (opacityBitmap != null)
            {
                if (layer.Placement.IsDistorted)
                    DrawWarpedTexture(graphics, opacityBitmap, corners, interactive ? 7 : 16);
                else
                    DrawImageAffine(graphics, opacityBitmap, topLeft, topRight, bottomLeft);
            }
            if (!selected)
                return;
            using var border = new Pen(Colors.White, 2) { DashStyle = DashStyles.Dash };
            graphics.DrawPolygon(border, new[] { topLeft, topRight, bottomRight, bottomLeft });
            if (!_component.TextureHandlesVisible)
                return;

            Color handleColor = _component.TextureDistortMode
                ? Color.FromArgb(176, 92, 202)
                : _component.TextureRotateMode
                    ? Color.FromArgb(45, 175, 120)
                    : Color.FromArgb(238, 158, 65);
            using var fill = new SolidBrush(handleColor);
            using var outline = new Pen(Colors.Black, 2);
            foreach (PointF point in new[] { bottomLeft, bottomRight, topRight, topLeft })
            {
                var handle = new RectangleF(point.X - 6, point.Y - 6, 12, 12);
                graphics.FillRectangle(fill, handle);
                graphics.DrawRectangle(outline, handle);
            }
            if (_component.TextureDistortMode && _component.SupportsTextureEdgeHandles)
            {
                PointF[] edgeHandles = TextureEdgeHandlePoints(corners);
                foreach (PointF point in edgeHandles)
                {
                    var handle = new RectangleF(point.X - 5, point.Y - 5, 10, 10);
                    graphics.FillEllipse(fill, handle);
                    graphics.DrawEllipse(outline, handle);
                }
            }
        }

        /// <summary>Reproduces GDI+'s 3-point-affine Graphics.DrawImage(image, destPoints) -- see the
        /// class doc comment. destTopLeft/destTopRight/destBottomLeft correspond to GDI+'s
        /// destPoints[0]/[1]/[2] (source rect corners (0,0), (width,0), (0,height)).</summary>
        private static void DrawImageAffine(
            Graphics graphics,
            Bitmap bitmap,
            PointF destTopLeft,
            PointF destTopRight,
            PointF destBottomLeft)
        {
            if (bitmap.Width <= 0 || bitmap.Height <= 0)
                return;
            float iHatX = (destTopRight.X - destTopLeft.X) / bitmap.Width;
            float iHatY = (destTopRight.Y - destTopLeft.Y) / bitmap.Width;
            float jHatX = (destBottomLeft.X - destTopLeft.X) / bitmap.Height;
            float jHatY = (destBottomLeft.Y - destTopLeft.Y) / bitmap.Height;
            graphics.SaveTransform();
            graphics.MultiplyTransform(
                Eto.Drawing.Matrix.Create(iHatX, iHatY, jHatX, jHatY, destTopLeft.X, destTopLeft.Y));
            graphics.DrawImage(bitmap, 0, 0);
            graphics.RestoreTransform();
        }

        /// <summary>Per-grid-cell version of the warped/distorted texture draw: crops the source bitmap
        /// to each cell's source sub-rectangle (Bitmap.Clone(Rectangle)) and 3-point-affine-draws that
        /// crop onto the cell's bilinear-warped destination quad, reproducing WinForms' DrawWarpedTexture
        /// (which used GDI+'s DrawImage(image, destPoints, srcRect, ...) overload -- Eto.Drawing has no
        /// such overload, hence the explicit crop-then-affine-draw here).</summary>
        private void DrawWarpedTexture(Graphics graphics, Bitmap bitmap, IList<Point2d> corners, int divisions)
        {
            divisions = Math.Max(2, divisions);
            for (int row = 0; row < divisions; row++)
            {
                double v0 = row / (double)divisions;
                double v1 = (row + 1) / (double)divisions;
                int sourceY = Math.Min(bitmap.Height - 1, (int)Math.Round(v0 * bitmap.Height));
                int sourceHeight = Math.Max(1, Math.Min(bitmap.Height - sourceY, (int)Math.Round((v1 - v0) * bitmap.Height)));
                for (int column = 0; column < divisions; column++)
                {
                    double u0 = column / (double)divisions;
                    double u1 = (column + 1) / (double)divisions;
                    PointF topLeft = TextureScreenPoint(BilinearTexturePoint(corners, u0, v0));
                    PointF topRight = TextureScreenPoint(BilinearTexturePoint(corners, u1, v0));
                    PointF bottomLeft = TextureScreenPoint(BilinearTexturePoint(corners, u0, v1));
                    int sourceX = Math.Min(bitmap.Width - 1, (int)Math.Round(u0 * bitmap.Width));
                    int sourceWidth = Math.Max(1, Math.Min(bitmap.Width - sourceX, (int)Math.Round((u1 - u0) * bitmap.Width)));
                    using Bitmap cell = bitmap.Clone(new Rectangle(sourceX, sourceY, sourceWidth, sourceHeight));
                    DrawImageAffine(graphics, cell, topLeft, topRight, bottomLeft);
                }
            }
        }

        /// <summary>Builds (or returns the cached) opacity-pre-baked Eto.Drawing.Bitmap for a texture
        /// layer -- see the class doc comment's opacity-blending note. Cache key is the layer's own
        /// object identity (WasperPaintTextureLayer does not override Equals/GetHashCode, so the
        /// dictionary uses reference equality, which is exactly what's wanted here).</summary>
        private Bitmap GetOpacityBitmap(WasperPaintTextureLayer layer)
        {
            if (layer?.Bitmap == null)
                return null;
            if (!_layerBitmapCache.TryGetValue(layer, out LayerBitmapCache cache))
            {
                cache = new LayerBitmapCache();
                _layerBitmapCache[layer] = cache;
            }
            if (cache.SourceBitmap != layer.Bitmap ||
                cache.Revision != layer.Revision ||
                Math.Abs(cache.Opacity - layer.Opacity) > 1e-6)
            {
                cache.EtoBitmap?.Dispose();
                cache.EtoBitmap = BuildOpacityBitmap(layer.Bitmap, layer.Opacity);
                cache.SourceBitmap = layer.Bitmap;
                cache.Revision = layer.Revision;
                cache.Opacity = layer.Opacity;
            }
            return cache.EtoBitmap;
        }

        private static Bitmap BuildOpacityBitmap(System.Drawing.Bitmap source, double opacity)
        {
            Bitmap converted = ConvertToEtoBitmap(source);
            if (converted == null)
                return null;
            // Matches WinForms' ImageAttributes ColorMatrix Matrix33 = 0.68 * opacity, which scaled
            // every pixel's existing alpha by that factor -- baked in per-pixel here instead since
            // Eto.Drawing's DrawImage has no color-matrix/alpha parameter.
            double multiplier = Math.Max(0.0, Math.Min(1.0, opacity)) * 0.68;
            using (BitmapData data = converted.Lock())
            {
                for (int y = 0; y < converted.Height; y++)
                {
                    for (int x = 0; x < converted.Width; x++)
                    {
                        Color pixel = data.GetPixel(x, y);
                        data.SetPixel(x, y, Color.FromArgb(
                            pixel.Rb, pixel.Gb, pixel.Bb, (byte)(pixel.Ab * multiplier)));
                    }
                }
            }
            return converted;
        }

        private static Bitmap ConvertToEtoBitmap(System.Drawing.Bitmap source)
        {
            if (source == null)
                return null;
            using var stream = new System.IO.MemoryStream();
            source.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            stream.Position = 0;
            return new Bitmap(stream);
        }

        private static System.Drawing.Bitmap ToSystemDrawingBitmap(Bitmap bitmap)
        {
            using var stream = new System.IO.MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            stream.Position = 0;
            return new System.Drawing.Bitmap(stream);
        }

        private static Color ToEtoColor(System.Drawing.Color color)
        {
            return Color.FromArgb(color.R, color.G, color.B, color.A);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            _lastMouse = e.Location;
            _hoverMouse = e.Location;
            if (e.Buttons == MouseButtons.Middle || e.Buttons == MouseButtons.Alternate)
            {
                _panning = true;
                Cursor = Cursors.Move;
            }
            else if (e.Buttons == MouseButtons.Primary)
            {
                CancelPainterHover();
                _textureHandle = HitTextureHandle(e.Location);
                if (_textureHandle >= 0)
                {
                    _component.BeginTextureTransform(_textureHandle);
                    Cursor = Cursors.SizeAll;
                    _component.ClearPainterHover();
                }
                else if (HitTextureBody(e.Location))
                {
                    _textureMoving = true;
                    _component.BeginTextureMove(ToTextureAtlasPoint(e.Location));
                    Cursor = Cursors.SizeAll;
                    _component.ClearPainterHover();
                }
                else
                {
                    if (_component.SupportsFieldCollection)
                        _component.SelectFieldAt(ToAtlasPoint(e.Location));
                    if (_component.SupportsFieldCollection && _component.FieldArrangeMode)
                    {
                        _fieldDragging = _component.BeginFieldDrag(ToAtlasPoint(e.Location));
                        if (_fieldDragging)
                            // Cursors.SizeLeftRight does not exist on Eto.Forms.Cursors (CS0117,
                            // confirmed by a real build); SizeAll is the nearest already-used member
                            // in this file (texture drag uses it too) rather than guessing another
                            // unverified name.
                            Cursor = Cursors.SizeAll;
                    }
                    else if (_component.ActiveTool == WasperPaintTool.Smooth)
                    {
                        _selectingSmoothRegion = true;
                        _smoothRegionPoints.Clear();
                        _smoothRegionPoints.Add(e.Location);
                    }
                    else
                    {
                        _painting = _component.PainterBeginStroke(ToAtlasPoint(e.Location));
                    }
                }
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _hoverMouse = e.Location;
            bool primaryDown = (e.Buttons & MouseButtons.Primary) != 0;
            if (_panning)
            {
                _panX += e.Location.X - _lastMouse.X;
                _panY += e.Location.Y - _lastMouse.Y;
                _lastMouse = e.Location;
                _viewRevision++;
            }
            else if (_textureHandle >= 0 && primaryDown)
            {
                _component.MoveTextureCorner(
                    _textureHandle, ToTextureAtlasPoint(e.Location), (e.Modifiers & Keys.Shift) == Keys.Shift);
            }
            else if (_textureMoving && primaryDown)
            {
                _component.MoveTexture(ToTextureAtlasPoint(e.Location));
            }
            else if (_fieldDragging && primaryDown)
            {
                _component.MoveFieldDrag(ToAtlasPoint(e.Location));
            }
            else if (_painting && primaryDown)
            {
                _component.PainterContinueStroke(ToAtlasPoint(e.Location));
            }
            else if (_selectingSmoothRegion && primaryDown)
            {
                if (_component.SmoothRegionShape == WasperSmoothRegionShape.Square)
                {
                    if (_smoothRegionPoints.Count == 1)
                        _smoothRegionPoints.Add(e.Location);
                    else
                        _smoothRegionPoints[_smoothRegionPoints.Count - 1] = e.Location;
                }
                else if (_smoothRegionPoints.Count == 0 ||
                         DistanceSquared(_smoothRegionPoints[_smoothRegionPoints.Count - 1], e.Location) >= 9)
                {
                    _smoothRegionPoints.Add(e.Location);
                }
            }
            else
            {
                if (_component.TextureHandlesVisible)
                {
                    Cursor = HitTextureHandle(e.Location) >= 0 || HitTextureBody(e.Location)
                        ? Cursors.SizeAll
                        : Cursors.Default;
                }
                QueuePainterHover(ToAtlasPoint(e.Location));
            }
            Invalidate();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            CancelPainterHover();
            _component.ClearPainterHover();
            if (!_textureMoving && _textureHandle < 0 && !_panning)
                Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_painting && e.Buttons == MouseButtons.Primary)
            {
                _painting = false;
                _component.PainterEndStroke();
            }
            if (_selectingSmoothRegion && e.Buttons == MouseButtons.Primary)
            {
                _selectingSmoothRegion = false;
                if (_smoothRegionPoints.Count == 1)
                    _smoothRegionPoints.Add(e.Location);
                PointF[] screenBoundary = SmoothRegionScreenBoundary();
                if (screenBoundary.Length >= 3)
                    _component.ApplySmoothRegion(screenBoundary.Select(ToAtlasPoint).ToList());
                _smoothRegionPoints.Clear();
            }
            if (_textureHandle >= 0 && e.Buttons == MouseButtons.Primary)
            {
                _component.EndTextureTransform();
                _textureHandle = -1;
                Cursor = Cursors.Default;
            }
            if (_textureMoving && e.Buttons == MouseButtons.Primary)
            {
                _component.EndTextureTransform();
                _textureMoving = false;
                Cursor = Cursors.Default;
            }
            if (_fieldDragging && e.Buttons == MouseButtons.Primary)
            {
                _component.EndFieldDrag();
                _fieldDragging = false;
                Cursor = Cursors.Default;
            }
            if (_panning && (e.Buttons == MouseButtons.Middle || e.Buttons == MouseButtons.Alternate))
            {
                _panning = false;
                Cursor = Cursors.Default;
            }
            Invalidate();
        }

        private static double DistanceSquared(PointF a, PointF b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            double previous = _scale;
            // Eto's MouseEventArgs.Delta is a SizeF of "notches" rather than WinForms' /120-scaled int
            // -- Delta.Height is used directly as the notch count. Approximate; needs the interactive
            // check to confirm the zoom rate feels right on each platform.
            _scale *= Math.Pow(1.15, e.Delta.Height);
            _scale = Math.Max(1e-5, Math.Min(1e5, _scale));
            if (previous > 0.0)
            {
                double ratio = _scale / previous;
                _panX = e.Location.X - (e.Location.X - _panX) * ratio;
                _panY = e.Location.Y - (e.Location.Y - _panY) * ratio;
            }
            _fitted = true;
            _viewRevision++;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _atlasCache?.Dispose();
                _atlasCache = null;
                _textureCache?.Dispose();
                _textureCache = null;
                foreach (LayerBitmapCache cache in _layerBitmapCache.Values)
                    cache.EtoBitmap?.Dispose();
                _layerBitmapCache.Clear();
                _hoverTimer?.Stop();
                _dimensionFont?.Dispose();
            }
            base.Dispose(disposing);
        }

        private PointF ToAtlasScreen(Point3d point)
        {
            return ToScreen(_component.TransformAtlasPoint(point));
        }

        private PointF ToScreen(Point3d point)
        {
            Plane plane = _component.PainterPlane;
            Vector3d delta = point - plane.Origin;
            return new PointF(
                (float)(_panX + (delta * plane.XAxis) * _scale),
                (float)(_panY - (delta * plane.YAxis) * _scale));
        }

        private Point3d ToAtlasPoint(PointF point)
        {
            return _component.InverseTransformAtlasPoint(ToTextureAtlasPoint(point));
        }

        private Point3d ToTextureAtlasPoint(PointF point)
        {
            double x = (point.X - _panX) / _scale;
            double y = (_panY - point.Y) / _scale;
            Plane plane = _component.PainterPlane;
            return plane.Origin + plane.XAxis * x + plane.YAxis * y;
        }

        private Point3d AtlasPoint(double x, double y)
        {
            Plane plane = _component.PainterPlane;
            return plane.Origin + plane.XAxis * x + plane.YAxis * y;
        }

        private int HitTextureHandle(PointF mouse)
        {
            if (!_component.TextureHandlesVisible || _component.TextureRotateMode)
                return -1;
            IList<Point2d> corners = _component.TextureCorners;
            PointF[] handles =
            {
                TextureScreenPoint(corners[0]),
                TextureScreenPoint(corners[1]),
                TextureScreenPoint(corners[2]),
                TextureScreenPoint(corners[3])
            };
            for (int i = 0; i < handles.Length; i++)
            {
                if (DistanceSquared(handles[i], mouse) <= 144.0)
                    return i;
            }
            if (_component.TextureDistortMode && _component.SupportsTextureEdgeHandles)
            {
                PointF[] edgeHandles = TextureEdgeHandlePoints(corners);
                for (int edge = 0; edge < edgeHandles.Length; edge++)
                {
                    if (DistanceSquared(edgeHandles[edge], mouse) <= 144.0)
                        return 4 + edge;
                }
            }
            return -1;
        }

        private PointF[] TextureEdgeHandlePoints(IList<Point2d> corners)
        {
            var handles = new PointF[4];
            for (int edge = 0; edge < handles.Length; edge++)
            {
                Point2d first = corners[edge];
                Point2d second = corners[(edge + 1) % corners.Count];
                handles[edge] = TextureScreenPoint(
                    new Point2d((first.X + second.X) * 0.5, (first.Y + second.Y) * 0.5));
            }
            return handles;
        }

        private bool HitTextureBody(PointF mouse)
        {
            if (!_component.TextureHandlesVisible)
                return false;
            IList<Point2d> corners = _component.TextureCorners;
            var polygon = new[]
            {
                TextureScreenPoint(corners[0]),
                TextureScreenPoint(corners[1]),
                TextureScreenPoint(corners[2]),
                TextureScreenPoint(corners[3])
            };
            bool inside = false;
            for (int first = 0, second = polygon.Length - 1; first < polygon.Length; second = first++)
            {
                PointF a = polygon[first];
                PointF b = polygon[second];
                bool crosses =
                    (a.Y > mouse.Y) != (b.Y > mouse.Y) &&
                    mouse.X < (b.X - a.X) * (mouse.Y - a.Y) / (b.Y - a.Y) + a.X;
                if (crosses)
                    inside = !inside;
            }
            return inside;
        }

        private PointF TextureScreenPoint(Point2d point)
        {
            return ToScreen(AtlasPoint(point.X, point.Y));
        }

        private static Point2d BilinearTexturePoint(IList<Point2d> corners, double u, double v)
        {
            Point2d topLeft = corners[3];
            Point2d topRight = corners[2];
            Point2d bottomRight = corners[1];
            Point2d bottomLeft = corners[0];
            double topWeight = 1.0 - v;
            double leftWeight = 1.0 - u;
            return new Point2d(
                topLeft.X * leftWeight * topWeight +
                topRight.X * u * topWeight +
                bottomRight.X * u * v +
                bottomLeft.X * leftWeight * v,
                topLeft.Y * leftWeight * topWeight +
                topRight.Y * u * topWeight +
                bottomRight.Y * u * v +
                bottomLeft.Y * leftWeight * v);
        }
    }
}
