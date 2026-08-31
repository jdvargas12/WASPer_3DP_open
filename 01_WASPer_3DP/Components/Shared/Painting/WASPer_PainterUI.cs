using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

using Rhino.Geometry;

namespace WASPer_3DP.Painting
{
    internal sealed class WasperPaintForm : Form
    {
        private readonly IWasperPainterHost _component;
        private readonly WasperAtlasCanvas _canvas;
        private readonly Dictionary<WasperPaintTool, Button> _toolButtons =
            new Dictionary<WasperPaintTool, Button>();
        private readonly Button _previewButton;
        private readonly Button _liveButton;
        private readonly Button _updateButton;
        private readonly Button _undoButton;
        private readonly Button _redoButton;
        private readonly Button _smoothSquareButton;
        private readonly Button _smoothFreeformButton;
        private readonly ToolTip _historyToolTip = new ToolTip();
        private readonly Button _textureButton;
        private readonly Button[] _textureLayerButtons = new Button[5];
        private readonly Button[] _textureVisibilityButtons = new Button[5];
        private readonly ToolTip _textureToolTip = new ToolTip();
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
        private readonly ComboBox _textFontBox;
        private readonly NumericUpDown _textFontSizeNumber;
        private readonly Button _setTextTextureButton;
        private readonly TrackBar _radiusSlider;
        private readonly TrackBar _brushStrengthSlider;
        private readonly TrackBar _smoothStrengthSlider;
        private readonly NumericUpDown _radiusNumber;
        private readonly NumericUpDown _brushStrengthNumber;
        private readonly NumericUpDown _smoothStrengthNumber;
        private readonly TrackBar _fieldOffsetSlider;
        private readonly TrackBar _fieldResolutionSlider;
        private readonly TrackBar _fieldFrameSlider;
        private readonly NumericUpDown _fieldOffsetNumber;
        private readonly NumericUpDown _fieldResolutionNumber;
        private readonly NumericUpDown _fieldFrameNumber;
        private readonly Label _fieldIndicator;
        private readonly Button _arrangeFieldsButton;
        private readonly System.Windows.Forms.Timer _settingsCommitTimer;
        private readonly System.Windows.Forms.Timer _fieldCommitTimer;
        private bool _syncingSettings;
        private bool _syncingFieldSettings;
        private bool _syncingTextTexture;

        public WasperPaintForm(IWasperPainterHost component)
        {
            _component = component;
            // Declares that these pixel sizes were authored at 96 DPI. Without the paired
            // AutoScaleDimensions, AutoScaleMode.Dpi has no baseline to scale from, so hand-coded
            // heights stay at their literal pixel values and every toolbar row is squeezed on a
            // high-DPI monitor while its text renders larger.
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = component.PainterTitle;
            TopMost = true;
            ShowInTaskbar = true;
            MinimizeBox = true;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            ClientSize = component.SupportsTextTextures
                ? new Size(1520, 598)
                : new Size(1200, 560);

            var tools = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                WrapContents = false,
                Padding = new Padding(4)
            };
            AddToolButton(tools, component.PullToolLabel, WasperPaintTool.Pull);
            AddToolButton(tools, component.PushToolLabel, WasperPaintTool.Push);
            if (component.SupportsZeroTool)
                AddToolButton(tools, "Zero", WasperPaintTool.Zero);
            AddToolButton(tools, "Smooth", WasperPaintTool.Smooth);
            _smoothSquareButton = ActionButton(
                "Square",
                () =>
                {
                    _component.SetSmoothRegionShape(WasperSmoothRegionShape.Square);
                });
            _smoothSquareButton.Width = 66;
            tools.Controls.Add(_smoothSquareButton);
            _smoothFreeformButton = ActionButton(
                "Freeform",
                () =>
                {
                    _component.SetSmoothRegionShape(WasperSmoothRegionShape.Freeform);
                });
            _smoothFreeformButton.Width = 76;
            tools.Controls.Add(_smoothFreeformButton);
            AddToolButton(tools, "Erase", WasperPaintTool.Erase);
            _undoButton = IconButton(
                HistoryIcon(false),
                () => _component.UndoPaint());
            _redoButton = IconButton(
                HistoryIcon(true),
                () => _component.RedoPaint());
            _historyToolTip.SetToolTip(_undoButton, "Undo last paint change");
            _historyToolTip.SetToolTip(_redoButton, "Redo last undone paint change");
            tools.Controls.Add(_undoButton);
            tools.Controls.Add(_redoButton);
            tools.Controls.Add(ActionButton("Clear", () => _component.ClearPaint()));
            tools.Controls.Add(ActionButton("Fit", () => _canvas.Fit()));
            _flipMapButton = ActionButton(
                "Flip Map",
                () => _component.ToggleAtlasFlipMap());
            _flipMapButton.Width = 72;
            _flipMapButton.Visible = component.SupportsAtlasTransforms;
            tools.Controls.Add(_flipMapButton);
            _rotateAtlasButton = ActionButton(
                "Rotate Atlas 90°",
                () => _component.RotateAtlasClockwise());
            _rotateAtlasButton.Width = 112;
            _rotateAtlasButton.Visible = component.SupportsAtlasTransforms;
            tools.Controls.Add(_rotateAtlasButton);
            _previewButton = ActionButton("Preview", () => _component.TogglePreview());
            _previewButton.Width = 70;
            tools.Controls.Add(_previewButton);
            _liveButton = ActionButton("Live", () => _component.ToggleLive());
            _liveButton.Width = 90;
            tools.Controls.Add(_liveButton);
            _updateButton = ActionButton("Update", () => _component.UpdateAlgorithm());
            _updateButton.Width = 70;
            tools.Controls.Add(_updateButton);

            var settingsTools = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                WrapContents = false,
                Padding = new Padding(4, 2, 4, 2),
                BackColor = Color.FromArgb(238, 239, 242)
            };
            (_radiusSlider, _radiusNumber) = AddPainterSetting(
                settingsTools,
                "Brush Radious",
                -300,
                200,
                3,
                0.001m,
                1000m);
            (_brushStrengthSlider, _brushStrengthNumber) = AddPainterSetting(
                settingsTools,
                "Brush Strength",
                0,
                1000,
                3,
                0m,
                1m);
            (_smoothStrengthSlider, _smoothStrengthNumber) = AddPainterSetting(
                settingsTools,
                "Smooth Strength",
                0,
                1000,
                3,
                0m,
                1m);
            _radiusSlider.ValueChanged += (_, _) => SliderSettingsChanged();
            _brushStrengthSlider.ValueChanged += (_, _) => SliderSettingsChanged();
            _smoothStrengthSlider.ValueChanged += (_, _) => SliderSettingsChanged();
            _settingsCommitTimer = new System.Windows.Forms.Timer { Interval = 180 };
            _settingsCommitTimer.Tick += (_, _) =>
            {
                _settingsCommitTimer.Stop();
                CommitPainterSettings();
            };
            _radiusSlider.MouseUp += (_, _) => CommitPainterSettings();
            _brushStrengthSlider.MouseUp += (_, _) => CommitPainterSettings();
            _smoothStrengthSlider.MouseUp += (_, _) => CommitPainterSettings();
            _radiusSlider.KeyUp += (_, _) => CommitPainterSettings();
            _brushStrengthSlider.KeyUp += (_, _) => CommitPainterSettings();
            _smoothStrengthSlider.KeyUp += (_, _) => CommitPainterSettings();
            _radiusSlider.MouseWheel += (_, _) => CommitPainterSettings();
            _brushStrengthSlider.MouseWheel += (_, _) => CommitPainterSettings();
            _smoothStrengthSlider.MouseWheel += (_, _) => CommitPainterSettings();
            _radiusNumber.ValueChanged += (_, _) => NumberSettingsChanged();
            _brushStrengthNumber.ValueChanged += (_, _) => NumberSettingsChanged();
            _smoothStrengthNumber.ValueChanged += (_, _) => NumberSettingsChanged();

            var textureTools = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                WrapContents = false,
                Padding = new Padding(4, 2, 4, 2),
                BackColor = Color.FromArgb(225, 228, 234)
            };
            textureTools.Controls.Add(new Label
            {
                Text = "Pick Texture:",
                AutoSize = true,
                Height = 27,
                Padding = new Padding(3, 6, 3, 0),
                Margin = new Padding(2)
            });
            for (int layer = 0; layer < _textureLayerButtons.Length; layer++)
            {
                int selectedLayer = layer;
                Button layerButton = ActionButton(
                    (layer + 1).ToString(),
                    () => _component.SelectTextureLayer(selectedLayer));
                layerButton.Width = 28;
                _textureLayerButtons[layer] = layerButton;
                textureTools.Controls.Add(layerButton);

                Button visibilityButton = IconButton(
                    _visibleTextureIcon,
                    () => _component.ToggleTextureLayerVisibility(selectedLayer));
                visibilityButton.Width = 28;
                _textureToolTip.SetToolTip(
                    visibilityButton,
                    $"Show or hide texture layer {layer + 1}");
                _textureVisibilityButtons[layer] = visibilityButton;
                textureTools.Controls.Add(visibilityButton);
            }
            _textureButton = ActionButton(
                "Show All",
                () => _component.ToggleTextureVisibility());
            _textureButton.Width = 70;
            _editTextureButton = ActionButton(
                "Edit",
                () => _component.ToggleTextureEdit());
            _distortTextureButton = ActionButton(
                "Distort",
                () => _component.ToggleTextureDistort());
            _distortTextureButton.Width = 70;
            _rotateTextureButton = ActionButton(
                "Rotate",
                () => _component.ToggleTextureRotate());
            _rotateTextureButton.Width = 66;
            _fitTextureButton = ActionButton(
                "Fit Texture",
                () => _component.FitTextureToAtlas());
            _fitTextureButton.Width = 82;
            _applyTextureButton = ActionButton(
                "Apply Layer",
                () => _component.ApplyTextureToPaint());
            _applyTextureButton.Width = 88;
            _applyCompositeTextureButton = ActionButton(
                "Apply Composite",
                () => _component.ApplyTextureCompositeToPaint());
            _applyCompositeTextureButton.Width = 112;
            _removeTextureButton = ActionButton(
                "Remove",
                () => _component.RemoveTextureOverlay());
            textureTools.Controls.Add(_textureButton);
            textureTools.Controls.Add(_editTextureButton);
            textureTools.Controls.Add(_distortTextureButton);
            textureTools.Controls.Add(_rotateTextureButton);
            textureTools.Controls.Add(_fitTextureButton);
            textureTools.Controls.Add(_applyTextureButton);
            textureTools.Controls.Add(_applyCompositeTextureButton);
            textureTools.Controls.Add(_removeTextureButton);
            textureTools.Visible = component.SupportsTextures;

            var textTextureTools = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                WrapContents = false,
                Padding = new Padding(4, 2, 4, 2),
                BackColor = Color.FromArgb(232, 235, 239),
                Visible = component.SupportsTextTextures
            };
            textTextureTools.Controls.Add(new Label
            {
                Text = "Add Text:",
                AutoSize = true,
                Height = 27,
                Padding = new Padding(3, 6, 3, 0),
                Margin = new Padding(2)
            });
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
                textTextureTools.Controls.Add(layerButton);

                Button visibilityButton = IconButton(
                    _visibleTextureIcon,
                    () => _component.ToggleTextTextureLayerVisibility(selectedLayer));
                visibilityButton.Width = 28;
                _textureToolTip.SetToolTip(
                    visibilityButton,
                    $"Show or hide text texture {layer + 1}");
                _textTextureVisibilityButtons[layer] = visibilityButton;
                textTextureTools.Controls.Add(visibilityButton);
            }
            _textTextureBox = new TextBox
            {
                Width = 220,
                Height = 27,
                Margin = new Padding(4, 3, 2, 0)
            };
            _textTextureBox.TextChanged += (_, _) => TextTextureDraftChanged();
            textTextureTools.Controls.Add(_textTextureBox);
            _textFontBox = new ComboBox
            {
                Width = 180,
                Height = 27,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(2, 3, 2, 0)
            };
            using (var fonts = new InstalledFontCollection())
            {
                foreach (string fontName in fonts.Families
                             .Select(family => family.Name)
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                    _textFontBox.Items.Add(fontName);
            }
            int defaultFont = _textFontBox.FindStringExact("Arial");
            _textFontBox.SelectedIndex = defaultFont >= 0
                ? defaultFont
                : (_textFontBox.Items.Count > 0 ? 0 : -1);
            _textFontBox.SelectedIndexChanged += (_, _) => TextTextureDraftChanged();
            textTextureTools.Controls.Add(_textFontBox);
            textTextureTools.Controls.Add(new Label
            {
                Text = "Size:",
                AutoSize = true,
                Height = 27,
                Padding = new Padding(3, 6, 0, 0),
                Margin = new Padding(2)
            });
            _textFontSizeNumber = new NumericUpDown
            {
                DecimalPlaces = 2,
                Minimum = 0.01m,
                Maximum = 100000m,
                Increment = 0.5m,
                Value = 10m,
                Width = 82,
                Height = 27,
                Margin = new Padding(0, 3, 2, 0)
            };
            _textFontSizeNumber.ValueChanged += (_, _) => TextTextureDraftChanged();
            _textureToolTip.SetToolTip(
                _textFontSizeNumber,
                "Text height in atlas/model units before rasterizing");
            textTextureTools.Controls.Add(_textFontSizeNumber);
            _setTextTextureButton = ActionButton(
                "Set as Texture",
                () => _component.CommitTextTexture(
                    _textTextureBox.Text,
                    _textFontBox.SelectedItem?.ToString(),
                    (double)_textFontSizeNumber.Value));
            _setTextTextureButton.Width = 110;
            _setTextTextureButton.BackColor = Color.FromArgb(72, 164, 92);
            _setTextTextureButton.ForeColor = Color.White;
            textTextureTools.Controls.Add(_setTextTextureButton);
            Button duplicateText = ActionButton(
                "Duplicate",
                _component.DuplicateTextTextureLayer);
            duplicateText.Width = 78;
            textTextureTools.Controls.Add(duplicateText);
            Button removeText = ActionButton(
                "Remove",
                _component.RemoveTextTextureLayer);
            removeText.Width = 68;
            textTextureTools.Controls.Add(removeText);
            Button textUp = ActionButton(
                "Up",
                () => _component.MoveTextTextureLayer(-1));
            textUp.Width = 42;
            textTextureTools.Controls.Add(textUp);
            Button textDown = ActionButton(
                "Down",
                () => _component.MoveTextTextureLayer(1));
            textDown.Width = 52;
            textTextureTools.Controls.Add(textDown);

            var fieldNavigationTools = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                WrapContents = false,
                Padding = new Padding(4, 2, 4, 2),
                BackColor = Color.FromArgb(225, 228, 234),
                Visible = component.SupportsFieldCollection
            };
            fieldNavigationTools.Controls.Add(new Label
            {
                Text = "Fields:",
                AutoSize = true,
                Height = 27,
                Padding = new Padding(3, 6, 3, 0),
                Margin = new Padding(2)
            });
            fieldNavigationTools.Controls.Add(ActionButton("Previous", component.SelectPreviousField));
            _fieldIndicator = new Label
            {
                Text = "1 / 1",
                Width = 62,
                Height = 27,
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(2)
            };
            fieldNavigationTools.Controls.Add(_fieldIndicator);
            fieldNavigationTools.Controls.Add(ActionButton("Next", component.SelectNextField));
            Button addField = ActionButton("Add New Field", component.AddNewField);
            addField.Width = 108;
            fieldNavigationTools.Controls.Add(addField);
            Button duplicateField = ActionButton(
                "Duplicate",
                component.DuplicateActiveField);
            duplicateField.Width = 82;
            fieldNavigationTools.Controls.Add(duplicateField);
            Button removeField = ActionButton(
                "Remove",
                component.RemoveActiveField);
            removeField.Width = 76;
            fieldNavigationTools.Controls.Add(removeField);
            _arrangeFieldsButton = ActionButton(
                "Arrange Fields",
                component.ToggleFieldArrangeMode);
            _arrangeFieldsButton.Width = 104;
            fieldNavigationTools.Controls.Add(_arrangeFieldsButton);
            Button moveUp = ActionButton("Move Up", component.MoveActiveFieldUp);
            moveUp.Width = 78;
            fieldNavigationTools.Controls.Add(moveUp);
            Button moveDown = ActionButton("Move Down", component.MoveActiveFieldDown);
            moveDown.Width = 88;
            fieldNavigationTools.Controls.Add(moveDown);

            var fieldSettingsTools = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                WrapContents = false,
                Padding = new Padding(4, 2, 4, 2),
                BackColor = Color.FromArgb(238, 239, 242),
                Visible = component.SupportsFieldCollection
            };
            (_fieldOffsetSlider, _fieldOffsetNumber) = AddPainterSetting(
                fieldSettingsTools, "Field Offset", -1000, 1000, 3, -100000m, 100000m);
            (_fieldResolutionSlider, _fieldResolutionNumber) = AddPainterSetting(
                fieldSettingsTools, "Resolution (All)", -300, 300, 3, 0.001m, 100000m);
            (_fieldFrameSlider, _fieldFrameNumber) = AddPainterSetting(
                fieldSettingsTools, "Field Size (All)", -100, 500, 3, 0.1m, 100000m);
            _fieldOffsetSlider.ValueChanged += (_, _) => FieldSlidersChanged();
            _fieldResolutionSlider.ValueChanged += (_, _) => FieldSlidersChanged();
            _fieldFrameSlider.ValueChanged += (_, _) => FieldSlidersChanged();
            _fieldOffsetNumber.ValueChanged += (_, _) => FieldNumbersChanged();
            _fieldResolutionNumber.ValueChanged += (_, _) => FieldNumbersChanged();
            _fieldFrameNumber.ValueChanged += (_, _) => FieldNumbersChanged();
            _fieldCommitTimer = new System.Windows.Forms.Timer { Interval = 220 };
            _fieldCommitTimer.Tick += (_, _) =>
            {
                _fieldCommitTimer.Stop();
                CommitFieldSettings();
            };
            foreach (TrackBar slider in new[]
                     { _fieldOffsetSlider, _fieldResolutionSlider, _fieldFrameSlider })
                slider.MouseUp += (_, _) => CommitFieldSettings();

            var sessionTools = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                WrapContents = false,
                Padding = new Padding(4, 2, 4, 2),
                BackColor = Color.FromArgb(225, 228, 234)
            };
            sessionTools.Controls.Add(new Label
            {
                Text = "Session:",
                AutoSize = true,
                Height = 27,
                Padding = new Padding(3, 6, 3, 0),
                Margin = new Padding(2)
            });
            Button saveSession = ActionButton(
                "Save",
                () => _component.SavePainterSession());
            saveSession.Width = 68;
            sessionTools.Controls.Add(saveSession);
            Button loadSession = ActionButton(
                "Load",
                () => _component.LoadPainterSession());
            loadSession.Width = 68;
            sessionTools.Controls.Add(loadSession);
            Button saveBitmap = ActionButton(
                "Save Bitmap",
                () =>
                {
                    using Bitmap bitmap = _canvas.CaptureBitmap();
                    _component.SavePainterBitmap(bitmap);
                });
            saveBitmap.Width = 92;
            sessionTools.Controls.Add(saveBitmap);

            _canvas = new WasperAtlasCanvas(component)
            {
                Dock = DockStyle.Fill
            };
            var legend = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 25,
                TextAlign = ContentAlignment.MiddleCenter,
                Text = component.PainterLegend +
                    "    |    Wheel: zoom    Middle/right: pan"
            };
            Controls.Add(_canvas);
            Controls.Add(legend);
            Controls.Add(sessionTools);
            Controls.Add(fieldNavigationTools);
            Controls.Add(fieldSettingsTools);
            Controls.Add(textTextureTools);
            Controls.Add(textureTools);
            Controls.Add(settingsTools);
            Controls.Add(tools);

            FormClosing += (_, e) =>
            {
                e.Cancel = true;
                _settingsCommitTimer.Stop();
                _fieldCommitTimer.Stop();
                _component.PainterEndStroke();
                _component.EndTextureTransform();
                Hide();
            };
        }

        private void AddToolButton(
            Control parent,
            string text,
            WasperPaintTool tool)
        {
            Button button = ActionButton(text, () => _component.SetPainterTool(tool));
            button.Width = 70;
            _toolButtons[tool] = button;
            parent.Controls.Add(button);
        }

        private static (TrackBar Slider, NumericUpDown Number) AddPainterSetting(
            Control parent,
            string label,
            int sliderMinimum,
            int sliderMaximum,
            int decimalPlaces,
            decimal numberMinimum,
            decimal numberMaximum)
        {
            var group = new FlowLayoutPanel
            {
                Width = 338,
                Height = 36,
                WrapContents = false,
                Margin = new Padding(2, 0, 2, 0)
            };
            group.Controls.Add(new Label
            {
                Text = label + ":",
                Width = 112,
                Height = 28,
                TextAlign = ContentAlignment.MiddleRight,
                Margin = new Padding(0, 2, 2, 0)
            });
            var slider = new TrackBar
            {
                Minimum = sliderMinimum,
                Maximum = sliderMaximum,
                TickStyle = TickStyle.None,
                Width = 140,
                Height = 30,
                SmallChange = 1,
                LargeChange = 10,
                Margin = new Padding(0, 1, 2, 0)
            };
            var number = new NumericUpDown
            {
                DecimalPlaces = decimalPlaces,
                Minimum = numberMinimum,
                Maximum = numberMaximum,
                Increment = decimalPlaces > 0 ? 0.01m : 1m,
                Width = 78,
                Height = 27,
                Margin = new Padding(0, 3, 0, 0)
            };
            group.Controls.Add(slider);
            group.Controls.Add(number);
            parent.Controls.Add(group);
            return (slider, number);
        }

        private void SliderSettingsChanged()
        {
            if (_syncingSettings)
                return;
            _syncingSettings = true;
            _radiusNumber.Value = ClampDecimal(
                (decimal)Math.Pow(10.0, _radiusSlider.Value / 100.0),
                _radiusNumber.Minimum,
                _radiusNumber.Maximum);
            _brushStrengthNumber.Value =
                _brushStrengthSlider.Value / 1000m;
            _smoothStrengthNumber.Value =
                _smoothStrengthSlider.Value / 1000m;
            _syncingSettings = false;
            _component.PreviewPainterSettings(
                (double)_radiusNumber.Value,
                (double)_brushStrengthNumber.Value,
                (double)_smoothStrengthNumber.Value);
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
                (double)_radiusNumber.Value,
                (double)_brushStrengthNumber.Value,
                (double)_smoothStrengthNumber.Value);
        }

        private void SyncSlidersFromNumbers()
        {
            _syncingSettings = true;
            _radiusSlider.Value = Math.Max(
                _radiusSlider.Minimum,
                Math.Min(
                    _radiusSlider.Maximum,
                    (int)Math.Round(
                        Math.Log10(
                            Math.Max(0.001, (double)_radiusNumber.Value)) *
                        100.0)));
            _brushStrengthSlider.Value = Math.Max(
                0,
                Math.Min(
                    1000,
                    (int)Math.Round(
                        (double)_brushStrengthNumber.Value * 1000.0)));
            _smoothStrengthSlider.Value = Math.Max(
                0,
                Math.Min(
                    1000,
                    (int)Math.Round(
                        (double)_smoothStrengthNumber.Value * 1000.0)));
            _syncingSettings = false;
        }

        private void FieldSlidersChanged()
        {
            if (_syncingFieldSettings)
                return;
            _syncingFieldSettings = true;
            _fieldOffsetNumber.Value = ClampDecimal(
                _fieldOffsetSlider.Value / 10m,
                _fieldOffsetNumber.Minimum,
                _fieldOffsetNumber.Maximum);
            _fieldResolutionNumber.Value = ClampDecimal(
                (decimal)Math.Pow(10.0, _fieldResolutionSlider.Value / 100.0),
                _fieldResolutionNumber.Minimum,
                _fieldResolutionNumber.Maximum);
            _fieldFrameNumber.Value = ClampDecimal(
                (decimal)Math.Pow(10.0, _fieldFrameSlider.Value / 100.0),
                _fieldFrameNumber.Minimum,
                _fieldFrameNumber.Maximum);
            _syncingFieldSettings = false;
            _component.PreviewFieldSettings(
                (double)_fieldOffsetNumber.Value,
                (double)_fieldResolutionNumber.Value,
                (double)_fieldFrameNumber.Value);
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
                (double)_fieldOffsetNumber.Value,
                (double)_fieldResolutionNumber.Value,
                (double)_fieldFrameNumber.Value);
        }

        private void SyncFieldSlidersFromNumbers()
        {
            _syncingFieldSettings = true;
            _fieldOffsetSlider.Value = Math.Max(
                _fieldOffsetSlider.Minimum,
                Math.Min(_fieldOffsetSlider.Maximum,
                    (int)Math.Round((double)_fieldOffsetNumber.Value * 10.0)));
            _fieldResolutionSlider.Value = Math.Max(
                _fieldResolutionSlider.Minimum,
                Math.Min(_fieldResolutionSlider.Maximum,
                    (int)Math.Round(Math.Log10(
                        Math.Max(0.001, (double)_fieldResolutionNumber.Value)) * 100.0)));
            _fieldFrameSlider.Value = Math.Max(
                _fieldFrameSlider.Minimum,
                Math.Min(_fieldFrameSlider.Maximum,
                    (int)Math.Round(Math.Log10(
                        Math.Max(0.1, (double)_fieldFrameNumber.Value)) * 100.0)));
            _syncingFieldSettings = false;
        }

        private void TextTextureDraftChanged()
        {
            if (_syncingTextTexture || !_component.SupportsTextTextures)
                return;
            _component.PreviewTextTexture(
                _textTextureBox.Text,
                _textFontBox.SelectedItem?.ToString(),
                (double)_textFontSizeNumber.Value);
            _canvas?.Invalidate();
        }

        private void SyncTextTextureControls()
        {
            if (!_component.SupportsTextTextures)
                return;
            int index = Math.Max(
                0,
                Math.Min(
                    _component.TextTextureLayerCount - 1,
                    _component.ActiveTextTextureLayer));
            WasperPaintTextureLayer layer =
                _component.TextTextureLayers != null &&
                index < _component.TextTextureLayers.Count
                    ? _component.TextTextureLayers[index]
                    : null;
            _syncingTextTexture = true;
            string text = layer?.TextContent ?? string.Empty;
            if (!string.Equals(_textTextureBox.Text, text, StringComparison.Ordinal))
                _textTextureBox.Text = text;
            string fontName = string.IsNullOrWhiteSpace(layer?.FontName)
                ? "Arial"
                : layer.FontName;
            int fontIndex = _textFontBox.FindStringExact(fontName);
            if (fontIndex >= 0 && _textFontBox.SelectedIndex != fontIndex)
                _textFontBox.SelectedIndex = fontIndex;
            decimal fontSize = ClampDecimal(
                (decimal)(layer?.FontSize > 0.0 ? layer.FontSize : 10.0),
                _textFontSizeNumber.Minimum,
                _textFontSizeNumber.Maximum);
            if (_textFontSizeNumber.Value != fontSize)
                _textFontSizeNumber.Value = fontSize;
            _syncingTextTexture = false;
        }

        private static decimal ClampDecimal(
            decimal value,
            decimal minimum,
            decimal maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }

        private static Button ActionButton(string text, Action action)
        {
            var button = new Button
            {
                Text = text,
                Height = 27,
                Width = 62,
                Margin = new Padding(2),
                UseVisualStyleBackColor = false
            };
            button.Click += (_, _) => action();
            return button;
        }

        private static Button IconButton(Bitmap icon, Action action)
        {
            var button = new Button
            {
                Height = 27,
                Width = 34,
                Margin = new Padding(2),
                Image = icon,
                UseVisualStyleBackColor = false
            };
            button.Click += (_, _) => action();
            return button;
        }

        private static Bitmap HistoryIcon(bool redo)
        {
            var bitmap = new Bitmap(20, 20);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var pen = new Pen(Color.FromArgb(45, 55, 68), 2.2f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawArc(pen, 4, 4, 12, 12, 205, 245);
            PointF[] arrow =
            {
                new PointF(2.5f, 7.5f),
                new PointF(8.5f, 6.2f),
                new PointF(6.4f, 12.0f)
            };
            using var brush = new SolidBrush(Color.FromArgb(45, 55, 68));
            graphics.FillPolygon(brush, arrow);
            if (redo)
                bitmap.RotateFlip(RotateFlipType.RotateNoneFlipX);
            return bitmap;
        }

        private static Bitmap VisibilityIcon(bool visible)
        {
            var bitmap = new Bitmap(20, 20);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            Color color = visible
                ? Color.FromArgb(45, 55, 68)
                : Color.FromArgb(105, 105, 105);
            using var pen = new Pen(color, 1.8f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            graphics.DrawEllipse(pen, 2.5f, 6.0f, 15.0f, 8.0f);
            using var pupil = new SolidBrush(color);
            graphics.FillEllipse(pupil, 8.0f, 8.0f, 4.0f, 4.0f);
            if (!visible)
                graphics.DrawLine(pen, 3.0f, 3.0f, 17.0f, 17.0f);
            return bitmap;
        }

        public void RefreshCanvas()
        {
            if (IsDisposed)
                return;
            foreach (KeyValuePair<WasperPaintTool, Button> pair in _toolButtons)
            {
                bool active = pair.Key == _component.ActiveTool;
                pair.Value.BackColor = active
                    ? pair.Key == WasperPaintTool.Pull
                        ? _component.PullToolColor
                        : pair.Key == WasperPaintTool.Push
                            ? _component.PushToolColor
                            : pair.Key == WasperPaintTool.Zero
                                ? _component.ZeroToolColor
                            : Color.FromArgb(245, 215, 120)
                    : SystemColors.Control;
                pair.Value.ForeColor = active &&
                    pair.Value.BackColor.GetBrightness() < 0.45f
                        ? Color.White
                        : SystemColors.ControlText;
            }
            bool smoothing = _component.ActiveTool == WasperPaintTool.Smooth;
            _smoothSquareButton.BackColor = smoothing &&
                _component.SmoothRegionShape == WasperSmoothRegionShape.Square
                    ? Color.Gold
                    : SystemColors.Control;
            _smoothFreeformButton.BackColor = smoothing &&
                _component.SmoothRegionShape == WasperSmoothRegionShape.Freeform
                    ? Color.Gold
                    : SystemColors.Control;
            _fieldIndicator.Text = $"{_component.ActiveFieldIndex + 1} / " +
                Math.Max(1, _component.FieldCount);
            _arrangeFieldsButton.BackColor = _component.FieldArrangeMode
                ? Color.FromArgb(238, 158, 65)
                : SystemColors.Control;
            if (_component.SupportsFieldCollection)
            {
                _syncingFieldSettings = true;
                _fieldOffsetNumber.Value = ClampDecimal(
                    (decimal)_component.FieldOffset,
                    _fieldOffsetNumber.Minimum,
                    _fieldOffsetNumber.Maximum);
                _fieldResolutionNumber.Value = ClampDecimal(
                    (decimal)_component.FieldResolution,
                    _fieldResolutionNumber.Minimum,
                    _fieldResolutionNumber.Maximum);
                _fieldFrameNumber.Value = ClampDecimal(
                    (decimal)_component.FieldFrameSize,
                    _fieldFrameNumber.Minimum,
                    _fieldFrameNumber.Maximum);
                _syncingFieldSettings = false;
                SyncFieldSlidersFromNumbers();
            }
            _previewButton.BackColor = _component.PreviewEnabled
                ? Color.FromArgb(105, 145, 235)
                : SystemColors.Control;
            _previewButton.ForeColor = _component.PreviewEnabled
                ? Color.White
                : SystemColors.ControlText;

            _liveButton.Text = _component.LiveEnabled
                ? "\u25B6 Live"
                : "\u2161 Paused";
            _liveButton.BackColor = _component.LiveEnabled
                ? Color.FromArgb(72, 164, 92)
                : Color.FromArgb(194, 70, 70);
            _liveButton.ForeColor = Color.White;

            _updateButton.Enabled = _component.UpdateEnabled;
            _updateButton.BackColor = !_component.UpdateEnabled
                ? Color.FromArgb(205, 205, 205)
                : _component.HasPendingUpdate
                    ? Color.FromArgb(238, 158, 65)
                    : SystemColors.Control;
            _updateButton.ForeColor =
                _component.UpdateEnabled && _component.HasPendingUpdate
                    ? Color.White
                    : SystemColors.ControlText;
            _undoButton.Enabled = _component.CanUndoPaint;
            _redoButton.Enabled = _component.CanRedoPaint;
            _syncingSettings = true;
            _radiusNumber.Value = ClampDecimal(
                (decimal)_component.PainterRadius,
                _radiusNumber.Minimum,
                _radiusNumber.Maximum);
            _brushStrengthNumber.Value = ClampDecimal(
                (decimal)_component.PainterBrushStrength,
                0m,
                1m);
            _smoothStrengthNumber.Value = ClampDecimal(
                (decimal)_component.PainterSmoothStrength,
                0m,
                1m);
            _syncingSettings = false;
            SyncSlidersFromNumbers();
            _radiusSlider.Enabled = _component.PainterRadiusEditable;
            _radiusNumber.Enabled = _component.PainterRadiusEditable;
            _brushStrengthSlider.Enabled =
                _component.PainterBrushStrengthEditable;
            _brushStrengthNumber.Enabled =
                _component.PainterBrushStrengthEditable;
            _smoothStrengthSlider.Enabled =
                _component.PainterSmoothStrengthEditable;
            _smoothStrengthNumber.Enabled =
                _component.PainterSmoothStrengthEditable;

            bool hasTexture = _component.HasTextureSource;
            bool hasAnyTexture = _component.TextureLayers != null &&
                                 _component.TextureLayers.Any(layer =>
                                     layer?.Bitmap != null);
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
            bool activeTextureApplicable = hasTexture &&
                (activeLayer?.IsText != true || activeLayer.TextCommitted);
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
                button.BackColor = layer == _component.ActiveTextureLayer
                    ? Color.FromArgb(72, 142, 184)
                    : SystemColors.Control;
                button.ForeColor = layer == _component.ActiveTextureLayer
                    ? Color.White
                    : SystemColors.ControlText;
                bool layerHasTexture = textureLayer?.Bitmap != null;
                visibilityButton.Enabled = layerHasTexture;
                visibilityButton.Image = textureLayer?.Visible == false
                    ? _hiddenTextureIcon
                    : _visibleTextureIcon;
                visibilityButton.BackColor = layerHasTexture && textureLayer.Visible
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
            _textureButton.BackColor = hasVisibleTextures
                ? Color.FromArgb(105, 145, 235)
                : SystemColors.Control;
            _textureButton.ForeColor = hasVisibleTextures
                ? Color.White
                : SystemColors.ControlText;
            _editTextureButton.BackColor = _component.TextureEditMode
                ? Color.FromArgb(238, 158, 65)
                : SystemColors.Control;
            _editTextureButton.ForeColor = _component.TextureEditMode
                ? Color.White
                : SystemColors.ControlText;
            _distortTextureButton.BackColor = _component.TextureDistortMode
                ? Color.FromArgb(176, 92, 202)
                : SystemColors.Control;
            _distortTextureButton.ForeColor = _component.TextureDistortMode
                ? Color.White
                : SystemColors.ControlText;
            _rotateTextureButton.BackColor = _component.TextureRotateMode
                ? Color.FromArgb(176, 92, 202)
                : SystemColors.Control;
            _rotateTextureButton.ForeColor = _component.TextureRotateMode
                ? Color.White
                : SystemColors.ControlText;
            _flipMapButton.BackColor = _component.AtlasFlipMap
                ? Color.FromArgb(72, 142, 184)
                : SystemColors.Control;
            _flipMapButton.ForeColor = _component.AtlasFlipMap
                ? Color.White
                : SystemColors.ControlText;
            _applyTextureButton.BackColor = activeTextureApplicable
                ? Color.FromArgb(72, 164, 92)
                : Color.FromArgb(205, 205, 205);
            _applyTextureButton.ForeColor = activeTextureApplicable
                ? Color.White
                : SystemColors.ControlText;
            _applyCompositeTextureButton.BackColor = hasVisibleTextures
                ? Color.FromArgb(72, 164, 92)
                : Color.FromArgb(205, 205, 205);
            _applyCompositeTextureButton.ForeColor = hasVisibleTextures
                ? Color.White
                : SystemColors.ControlText;
            bool textLayerActive = _component.ActiveTextureLayer >=
                                   _component.TextureLayerCount;
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
                button.BackColor = textLayerActive &&
                                   layer == _component.ActiveTextTextureLayer
                    ? Color.FromArgb(72, 142, 184)
                    : SystemColors.Control;
                button.ForeColor = textLayerActive &&
                                   layer == _component.ActiveTextTextureLayer
                    ? Color.White
                    : SystemColors.ControlText;
                bool hasText = textLayer?.Bitmap != null;
                visibilityButton.Enabled = hasText;
                visibilityButton.Image = textLayer?.Visible == false
                    ? _hiddenTextureIcon
                    : _visibleTextureIcon;
                visibilityButton.BackColor = hasText && textLayer.Visible
                    ? Color.FromArgb(105, 145, 235)
                    : SystemColors.Control;
            }
            if (textLayerActive)
                SyncTextTextureControls();
            _setTextTextureButton.Enabled =
                !string.IsNullOrWhiteSpace(_textTextureBox.Text);
            _setTextTextureButton.BackColor = _setTextTextureButton.Enabled
                ? Color.FromArgb(72, 164, 92)
                : Color.FromArgb(205, 205, 205);
            _setTextTextureButton.ForeColor = _setTextTextureButton.Enabled
                ? Color.White
                : SystemColors.ControlText;
            _canvas.Invalidate();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if ((keyData & Keys.KeyCode) == Keys.Escape &&
                _component.ActiveTool != WasperPaintTool.None)
            {
                _component.SetPainterTool(WasperPaintTool.None);
                _component.ClearPainterHover();
                RefreshCanvas();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        internal void FitCanvas()
        {
            if (!IsDisposed && !_canvas.IsDisposed)
                _canvas.Fit();
        }

        public void PresentCanvasFrame()
        {
            if (IsDisposed || _canvas.IsDisposed)
                return;
            _canvas.Refresh();
        }

        public void ShowNearCursor()
        {
            System.Drawing.Point cursor = Cursor.Position;
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Rectangle workingArea = Screen.FromPoint(cursor).WorkingArea;
            int x = Math.Max(
                workingArea.Left,
                Math.Min(
                    cursor.X + 20,
                    workingArea.Right - Math.Max(Width, 240)));
            int y = Math.Max(
                workingArea.Top,
                Math.Min(
                    cursor.Y + 20,
                    workingArea.Bottom - Math.Max(Height, 180)));
            Location = new System.Drawing.Point(x, y);
            if (!Visible)
                Show();
            BringToFront();
            Activate();
            _canvas.Fit();
            RefreshCanvas();
        }
    }

    internal sealed class WasperAtlasCanvas : Control
    {
        private readonly IWasperPainterHost _component;
        private double _scale = 1.0;
        private double _panX;
        private double _panY;
        private bool _fitted;
        private bool _painting;
        private bool _selectingSmoothRegion;
        private readonly List<System.Drawing.Point> _smoothRegionPoints =
            new List<System.Drawing.Point>();
        private bool _panning;
        private bool _textureMoving;
        private bool _fieldDragging;
        private int _textureHandle = -1;
        private System.Drawing.Point _lastMouse;
        private System.Drawing.Point _hoverMouse;
        private Bitmap _atlasCache;
        private Bitmap _textureCache;
        private int _cachedPaintRevision = -1;
        private int _viewRevision;
        private int _cachedViewRevision = -1;
        private int _cachedTextureRevision = -1;
        private int _cachedTextureViewRevision = -1;
        private readonly System.Windows.Forms.Timer _hoverTimer;
        private Point3d _pendingHoverPoint = Point3d.Unset;
        private bool _hoverPending;

        public WasperAtlasCanvas(IWasperPainterHost component)
        {
            _component = component;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(32, 35, 42);
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint,
            true);
            _hoverTimer = new System.Windows.Forms.Timer { Interval = 25 };
            _hoverTimer.Tick += (_, _) => FlushPainterHover();
        }

        private void QueuePainterHover(Point3d atlasPoint)
        {
            _pendingHoverPoint = atlasPoint;
            _hoverPending = true;
            if (!_hoverTimer.Enabled)
                _hoverTimer.Start();
        }

        private void FlushPainterHover()
        {
            if (!_hoverPending)
            {
                _hoverTimer.Stop();
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
        }

        public Bitmap CaptureBitmap()
        {
            var bitmap = new Bitmap(
                Math.Max(1, ClientSize.Width),
                Math.Max(1, ClientSize.Height));
            DrawToBitmap(
                bitmap,
                new Rectangle(
                    System.Drawing.Point.Empty,
                    bitmap.Size));
            return bitmap;
        }

        public void Fit()
        {
            Mesh mesh = _component.PainterMesh;
            if (mesh == null || mesh.Vertices.Count == 0 || ClientSize.Width <= 0)
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
                Math.Min(
                    (ClientSize.Width - fitPadding) / width,
                    (ClientSize.Height - fitPadding) / height));
            _panX = ClientSize.Width * 0.5 - (minX + maxX) * 0.5 * _scale;
            _panY = ClientSize.Height * 0.5 + (minY + maxY) * 0.5 * _scale;
            _fitted = true;
            _viewRevision++;
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            _viewRevision++;
            if (!_fitted)
                Fit();
            else
                Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Mesh mesh = _component.PainterMesh;
            if (mesh == null || !mesh.IsValid)
                return;
            if (!_fitted)
                Fit();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            EnsureAtlasCache(mesh);
            if (_atlasCache != null)
                DrawAtlasCache(e.Graphics);

            if (_textureMoving || _textureHandle >= 0)
            {
                DrawTextureOverlay(e.Graphics, true);
            }
            else
            {
                EnsureTextureCache();
                if (_textureCache != null)
                    e.Graphics.DrawImageUnscaled(_textureCache, 0, 0);
            }
            DrawActiveFieldSelection(e.Graphics);
            DrawBrushCursor(e.Graphics);
            DrawSmoothRegion(e.Graphics);
            DrawAtlasDimensions(e.Graphics, mesh);
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
            using var pen = new Pen(Color.FromArgb(225, 240, 240, 245), 1.5f);
            using var brush = new SolidBrush(Color.FromArgb(245, 245, 245));
            using var font = new Font(Font.FontFamily, 9f, FontStyle.Bold);
            foreach (WasperPaintAtlasBounds item in bounds)
                DrawAtlasDimension(graphics, item, plane, pen, brush, font);
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
            using var shadow = new Pen(Color.FromArgb(230, 16, 20, 28), 7f)
            {
                LineJoin = LineJoin.Round
            };
            using var highlight = new Pen(Color.DeepSkyBlue, 4f)
            {
                LineJoin = LineJoin.Round
            };
            graphics.DrawPolygon(shadow, outline);
            graphics.DrawPolygon(highlight, outline);
        }

        private void DrawAtlasDimension(
            Graphics graphics,
            WasperPaintAtlasBounds bounds,
            Plane plane,
            Pen pen,
            Brush brush,
            Font font)
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
            string width = $"{Math.Abs(maxX - minX):0.###}";
            SizeF widthSize = graphics.MeasureString(width, font);
            graphics.DrawString(
                width,
                font,
                brush,
                (bottomLeft.X + bottomRight.X - widthSize.Width) * 0.5f,
                dimensionY + 3);

            float dimensionX = Math.Min(bottomLeft.X, topLeft.X) - gap;
            graphics.DrawLine(pen, dimensionX, bottomLeft.Y, dimensionX, topLeft.Y);
            graphics.DrawLine(pen, dimensionX - 5, bottomLeft.Y, dimensionX + 5, bottomLeft.Y);
            graphics.DrawLine(pen, dimensionX - 5, topLeft.Y, dimensionX + 5, topLeft.Y);
            string height = $"{Math.Abs(maxY - minY):0.###}";
            SizeF heightSize = graphics.MeasureString(height, font);
            GraphicsState state = graphics.Save();
            graphics.TranslateTransform(
                dimensionX - heightSize.Height - 3,
                (bottomLeft.Y + topLeft.Y) * 0.5f);
            graphics.RotateTransform(-90f);
            graphics.DrawString(height, font, brush, -heightSize.Width * 0.5f, 0);
            graphics.Restore(state);
        }

        private void DrawSmoothRegion(Graphics graphics)
        {
            if (!_selectingSmoothRegion || _smoothRegionPoints.Count < 2)
                return;
            System.Drawing.Point[] boundary = SmoothRegionScreenBoundary();
            if (boundary.Length < 2)
                return;
            using var fill = new SolidBrush(Color.FromArgb(45, 255, 205, 35));
            using var outline = new Pen(Color.Gold, 2) { DashStyle = DashStyle.Dash };
            if (boundary.Length >= 3)
                graphics.FillPolygon(fill, boundary);
            if (boundary.Length >= 3)
                graphics.DrawPolygon(outline, boundary);
            else
                graphics.DrawLines(outline, boundary);
        }

        private System.Drawing.Point[] SmoothRegionScreenBoundary()
        {
            if (_component.SmoothRegionShape == WasperSmoothRegionShape.Freeform)
                return _smoothRegionPoints.ToArray();
            System.Drawing.Point start = _smoothRegionPoints[0];
            System.Drawing.Point end = _smoothRegionPoints[_smoothRegionPoints.Count - 1];
            return new[]
            {
                start,
                new System.Drawing.Point(end.X, start.Y),
                end,
                new System.Drawing.Point(start.X, end.Y)
            };
        }

        private void EnsureTextureCache()
        {
            int stackRevision = TextureStackRevision();
            bool anyVisible = _component.TextureLayers != null &&
                              _component.TextureLayers.Any(layer =>
                                  layer?.Bitmap != null && layer.Visible);
            if (!anyVisible)
            {
                _textureCache?.Dispose();
                _textureCache = null;
                _cachedTextureRevision = stackRevision;
                _cachedTextureViewRevision = _viewRevision;
                return;
            }
            int width = Math.Max(1, ClientSize.Width);
            int height = Math.Max(1, ClientSize.Height);
            bool sizeChanged = _textureCache == null ||
                               _textureCache.Width != width ||
                               _textureCache.Height != height;
            if (!sizeChanged &&
                _cachedTextureRevision == stackRevision &&
                _cachedTextureViewRevision == _viewRevision)
                return;
            _textureCache?.Dispose();
            _textureCache = new Bitmap(width, height);
            using Graphics graphics = Graphics.FromImage(_textureCache);
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            DrawTextureOverlay(graphics, false);
            _cachedTextureRevision = stackRevision;
            _cachedTextureViewRevision = _viewRevision;
        }

        private int TextureStackRevision()
        {
            unchecked
            {
                int revision = _component.ActiveTextureLayer;
                foreach (WasperPaintTextureLayer layer in
                         _component.TextureLayers ?? Array.Empty<WasperPaintTextureLayer>())
                {
                    revision = revision * 31 + (layer?.Revision ?? 0);
                    revision = revision * 31 + (layer?.Visible == true ? 1 : 0);
                }
                return revision;
            }
        }

        private void DrawAtlasCache(Graphics graphics)
        {
            graphics.DrawImageUnscaled(_atlasCache, 0, 0);
        }

        private void EnsureAtlasCache(Mesh mesh)
        {
            int width = Math.Max(1, ClientSize.Width);
            int height = Math.Max(1, ClientSize.Height);
            bool sizeChanged = _atlasCache == null ||
                               _atlasCache.Width != width ||
                               _atlasCache.Height != height;
            if (!sizeChanged &&
                _cachedPaintRevision == _component.PainterVisualRevision &&
                _cachedViewRevision == _viewRevision)
                return;

            _atlasCache?.Dispose();
            _atlasCache = new Bitmap(width, height);
            using Graphics graphics = Graphics.FromImage(_atlasCache);
            graphics.Clear(BackColor);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            DrawAtlasField(graphics, mesh);
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
                var colors = new Color[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    polygon[i] = ToAtlasScreen(mesh.Vertices[vertices[i]]);
                    colors[i] = mesh.VertexColors.Count == mesh.Vertices.Count
                        ? mesh.VertexColors[vertices[i]]
                        : Color.RoyalBlue;
                }
                using var brush = new SolidBrush(AverageColor(colors));
                graphics.FillPolygon(brush, polygon);
            }

            if (_component.PainterMarkers != null)
            {
                foreach (WasperPaintMarker marker in _component.PainterMarkers)
                {
                    using var pen = new Pen(marker.Color, marker.Thickness);
                    if (marker.Thickness >= 2 && marker.Color.A >= 200)
                    {
                        using var outline = new Pen(
                            Color.FromArgb(210, 20, 22, 28),
                            marker.Thickness + 3);
                        graphics.DrawLine(
                            outline,
                            ToAtlasScreen(marker.Line.From),
                            ToAtlasScreen(marker.Line.To));
                    }
                    graphics.DrawLine(
                        pen,
                        ToAtlasScreen(marker.Line.From),
                        ToAtlasScreen(marker.Line.To));
                }
            }
        }

        private static Color AverageColor(IList<Color> colors)
        {
            int alpha = 0;
            int red = 0;
            int green = 0;
            int blue = 0;
            foreach (Color color in colors)
            {
                alpha += color.A;
                red += color.R;
                green += color.G;
                blue += color.B;
            }
            int count = Math.Max(1, colors.Count);
            return Color.FromArgb(
                alpha / count,
                red / count,
                green / count,
                blue / count);
        }

        private void DrawBrushCursor(Graphics graphics)
        {
            if (_component.ActiveTool != WasperPaintTool.None &&
                _component.ActiveTool != WasperPaintTool.Smooth)
            {
                float radius = (float)Math.Max(1.0, _component.PainterRadius * _scale);
                Color color = _component.ActiveTool == WasperPaintTool.Pull
                    ? _component.PullToolColor
                    : _component.ActiveTool == WasperPaintTool.Push
                        ? _component.PushToolColor
                        : _component.ActiveTool == WasperPaintTool.Zero
                            ? _component.ZeroToolColor
                        : _component.ActiveTool == WasperPaintTool.Smooth
                            ? Color.Gold
                            : Color.FromArgb(85, 125, 220);
                using var outline = new Pen(Color.Black, 5);
                using var inner = new Pen(color, 3);
                var circle = new RectangleF(
                    _hoverMouse.X - radius,
                    _hoverMouse.Y - radius,
                    radius * 2,
                    radius * 2);
                graphics.DrawEllipse(outline, circle);
                graphics.DrawEllipse(inner, circle);
            }
        }

        private void DrawTextureOverlay(
            Graphics graphics,
            bool interactive)
        {
            IList<WasperPaintTextureLayer> layers = _component.TextureLayers;
            if (layers == null)
                return;
            for (int index = 0; index < layers.Count; index++)
            {
                WasperPaintTextureLayer layer = layers[index];
                if (layer?.Bitmap == null || !layer.Visible)
                    continue;
                DrawTextureLayer(
                    graphics,
                    layer,
                    index == _component.ActiveTextureLayer,
                    interactive);
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
            using var attributes = new ImageAttributes();
            var matrix = new ColorMatrix
            {
                Matrix33 = (float)(0.68 * Math.Max(0.0, Math.Min(1.0, layer.Opacity)))
            };
            attributes.SetColorMatrix(matrix);
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            if (layer.Placement.IsDistorted)
            {
                DrawWarpedTexture(
                    graphics,
                    layer.Bitmap,
                    corners,
                    attributes,
                    interactive ? 7 : 16);
            }
            else
            {
                graphics.DrawImage(
                    layer.Bitmap,
                    new[] { topLeft, topRight, bottomLeft },
                    new Rectangle(
                        0,
                        0,
                        layer.Bitmap.Width,
                        layer.Bitmap.Height),
                    GraphicsUnit.Pixel,
                    attributes);
            }
            if (!selected)
                return;
            using var border = new Pen(Color.White, 2);
            border.DashStyle = DashStyle.Dash;
            graphics.DrawPolygon(
                border,
                new[] { topLeft, topRight, bottomRight, bottomLeft });
            if (!_component.TextureHandlesVisible)
                return;

            Color handleColor = _component.TextureDistortMode
                ? Color.FromArgb(176, 92, 202)
                : _component.TextureRotateMode
                    ? Color.FromArgb(45, 175, 120)
                    : Color.FromArgb(238, 158, 65);
            using var fill = new SolidBrush(handleColor);
            using var outline = new Pen(Color.Black, 2);
            foreach (PointF point in new[] { bottomLeft, bottomRight, topRight, topLeft })
            {
                var handle = new RectangleF(point.X - 6, point.Y - 6, 12, 12);
                graphics.FillRectangle(fill, handle);
                graphics.DrawRectangle(outline, handle.X, handle.Y, handle.Width, handle.Height);
            }
            if (_component.TextureDistortMode &&
                _component.SupportsTextureEdgeHandles)
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

        private void DrawWarpedTexture(
            Graphics graphics,
            Bitmap bitmap,
            IList<Point2d> corners,
            ImageAttributes attributes,
            int divisions)
        {
            divisions = Math.Max(2, divisions);
            for (int row = 0; row < divisions; row++)
            {
                double v0 = row / (double)divisions;
                double v1 = (row + 1) / (double)divisions;
                float sourceY = (float)(v0 * bitmap.Height);
                float sourceHeight = (float)((v1 - v0) * bitmap.Height);
                for (int column = 0; column < divisions; column++)
                {
                    double u0 = column / (double)divisions;
                    double u1 = (column + 1) / (double)divisions;
                    PointF topLeft = TextureScreenPoint(
                        BilinearTexturePoint(corners, u0, v0));
                    PointF topRight = TextureScreenPoint(
                        BilinearTexturePoint(corners, u1, v0));
                    PointF bottomLeft = TextureScreenPoint(
                        BilinearTexturePoint(corners, u0, v1));
                    float sourceX = (float)(u0 * bitmap.Width);
                    float sourceWidth = (float)((u1 - u0) * bitmap.Width);
                    graphics.DrawImage(
                        bitmap,
                        new[] { topLeft, topRight, bottomLeft },
                        new RectangleF(
                            sourceX,
                            sourceY,
                            sourceWidth,
                            sourceHeight),
                        GraphicsUnit.Pixel,
                        attributes);
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            _lastMouse = e.Location;
            _hoverMouse = e.Location;
            if (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right)
            {
                _panning = true;
                Cursor = Cursors.Hand;
            }
            else if (e.Button == MouseButtons.Left)
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
                    _component.BeginTextureMove(
                        ToTextureAtlasPoint(e.Location));
                    Cursor = Cursors.SizeAll;
                    _component.ClearPainterHover();
                }
                else
                {
                    if (_component.SupportsFieldCollection)
                        _component.SelectFieldAt(ToAtlasPoint(e.Location));
                    if (_component.SupportsFieldCollection &&
                        _component.FieldArrangeMode)
                    {
                        _fieldDragging = _component.BeginFieldDrag(
                            ToAtlasPoint(e.Location));
                        if (_fieldDragging)
                        {
                            Capture = true;
                            Cursor = Cursors.SizeWE;
                        }
                    }
                    else if (_component.ActiveTool == WasperPaintTool.Smooth)
                    {
                        _selectingSmoothRegion = true;
                        _smoothRegionPoints.Clear();
                        _smoothRegionPoints.Add(e.Location);
                        Capture = true;
                    }
                    else
                    {
                        _painting = _component.PainterBeginStroke(ToAtlasPoint(e.Location));
                    }
                    if (_painting)
                        Capture = true;
                }
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            _hoverMouse = e.Location;
            if (_panning)
            {
                _panX += e.X - _lastMouse.X;
                _panY += e.Y - _lastMouse.Y;
                _lastMouse = e.Location;
                _viewRevision++;
            }
            else if (_textureHandle >= 0 && (e.Button & MouseButtons.Left) != 0)
            {
                _component.MoveTextureCorner(
                    _textureHandle,
                    ToTextureAtlasPoint(e.Location),
                    (ModifierKeys & Keys.Shift) != 0);
            }
            else if (_textureMoving && (e.Button & MouseButtons.Left) != 0)
            {
                _component.MoveTexture(
                    ToTextureAtlasPoint(e.Location));
            }
            else if (_fieldDragging && (e.Button & MouseButtons.Left) != 0)
            {
                _component.MoveFieldDrag(ToAtlasPoint(e.Location));
            }
            else if (_painting && (e.Button & MouseButtons.Left) != 0)
            {
                _component.PainterContinueStroke(ToAtlasPoint(e.Location));
            }
            else if (_selectingSmoothRegion && (e.Button & MouseButtons.Left) != 0)
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
                    Cursor =
                        HitTextureHandle(e.Location) >= 0 ||
                        HitTextureBody(e.Location)
                            ? Cursors.SizeAll
                            : Cursors.Default;
                }
                QueuePainterHover(ToAtlasPoint(e.Location));
            }
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
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
            if (_painting && e.Button == MouseButtons.Left)
            {
                _painting = false;
                _component.PainterEndStroke();
            }
            if (_selectingSmoothRegion && e.Button == MouseButtons.Left)
            {
                _selectingSmoothRegion = false;
                if (_smoothRegionPoints.Count == 1)
                    _smoothRegionPoints.Add(e.Location);
                System.Drawing.Point[] screenBoundary = SmoothRegionScreenBoundary();
                if (screenBoundary.Length >= 3)
                    _component.ApplySmoothRegion(
                        screenBoundary.Select(ToAtlasPoint).ToList());
                _smoothRegionPoints.Clear();
            }
            if (_textureHandle >= 0 && e.Button == MouseButtons.Left)
            {
                _component.EndTextureTransform();
                _textureHandle = -1;
                Cursor = Cursors.Default;
            }
            if (_textureMoving && e.Button == MouseButtons.Left)
            {
                _component.EndTextureTransform();
                _textureMoving = false;
                Cursor = Cursors.Default;
            }
            if (_fieldDragging && e.Button == MouseButtons.Left)
            {
                _component.EndFieldDrag();
                _fieldDragging = false;
                Cursor = Cursors.Default;
            }
            if (_panning &&
                (e.Button == MouseButtons.Middle || e.Button == MouseButtons.Right))
            {
                _panning = false;
                Cursor = Cursors.Default;
            }
            if (e.Button == MouseButtons.Left)
                Capture = false;
            Invalidate();
        }

        private static int DistanceSquared(
            System.Drawing.Point a,
            System.Drawing.Point b)
        {
            int dx = a.X - b.X;
            int dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            double previous = _scale;
            _scale *= Math.Pow(1.15, e.Delta / 120.0);
            _scale = Math.Max(1e-5, Math.Min(1e5, _scale));
            if (previous > 0.0)
            {
                double ratio = _scale / previous;
                _panX = e.X - (e.X - _panX) * ratio;
                _panY = e.Y - (e.Y - _panY) * ratio;
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
                _hoverTimer?.Stop();
                _hoverTimer?.Dispose();
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

        private Point3d ToAtlasPoint(System.Drawing.Point point)
        {
            return _component.InverseTransformAtlasPoint(
                ToTextureAtlasPoint(point));
        }

        private Point3d ToTextureAtlasPoint(System.Drawing.Point point)
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

        private int HitTextureHandle(System.Drawing.Point mouse)
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
                double dx = handles[i].X - mouse.X;
                double dy = handles[i].Y - mouse.Y;
                if (dx * dx + dy * dy <= 144.0)
                    return i;
            }
            if (_component.TextureDistortMode &&
                _component.SupportsTextureEdgeHandles)
            {
                PointF[] edgeHandles = TextureEdgeHandlePoints(corners);
                for (int edge = 0; edge < edgeHandles.Length; edge++)
                {
                    double dx = edgeHandles[edge].X - mouse.X;
                    double dy = edgeHandles[edge].Y - mouse.Y;
                    if (dx * dx + dy * dy <= 144.0)
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
                handles[edge] = TextureScreenPoint(new Point2d(
                    (first.X + second.X) * 0.5,
                    (first.Y + second.Y) * 0.5));
            }
            return handles;
        }

        private bool HitTextureBody(System.Drawing.Point mouse)
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
            for (int first = 0, second = polygon.Length - 1;
                 first < polygon.Length;
                 second = first++)
            {
                PointF a = polygon[first];
                PointF b = polygon[second];
                bool crosses =
                    (a.Y > mouse.Y) != (b.Y > mouse.Y) &&
                    mouse.X <
                    (b.X - a.X) * (mouse.Y - a.Y) /
                    (b.Y - a.Y) +
                    a.X;
                if (crosses)
                    inside = !inside;
            }
            return inside;
        }

        private PointF TextureScreenPoint(Point2d point)
        {
            return ToScreen(AtlasPoint(point.X, point.Y));
        }

        private static Point2d BilinearTexturePoint(
            IList<Point2d> corners,
            double u,
            double v)
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
