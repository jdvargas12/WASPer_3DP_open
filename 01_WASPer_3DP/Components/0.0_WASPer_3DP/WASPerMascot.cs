using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.GUI.Canvas.Interaction;
using Grasshopper.Kernel;
using Rhino;
using Rhino.UI;
using WASPer_3DP.Components._0_0_WASPer_3DP;

namespace WASPer_3DP
{
    /// <summary>
    /// Adds a small toolbar toggle and a draggable, screen-space WASPer mascot to
    /// the Grasshopper canvas. The mascot is UI only; it never becomes part of a
    /// Grasshopper document and therefore does not affect selection or solutions.
    /// </summary>
    internal static class WasperMascotManager
    {
        private const string EnabledKey = "WASPer_3DP.Mascot.Enabled";
        private const string LocationKey = "WASPer_3DP.Mascot.Location";
        private const string ScalePercentKey = "WASPer_3DP.Mascot.ScalePercent";
        private const string ResourceName = "WASPer_3DP.Resources.Icons.00_WASPer_3DP.png";

        private static bool _initialized;
        private static bool _enabled;
        private static GH_Canvas _canvas;
        private static WasperMascotOverlay _mascot;
        private static ToolStripButton _toolbarButton;
        private static ToolStripButton _pathPreviewToolbarButton;
        private static Timer _attachTimer;
        private static Bitmap _artwork;
        private static int _scalePercent = 100;

        internal static void Initialize()
        {
            if (_initialized)
                return;

            _initialized = true;
            try
            {
                _enabled = Instances.Settings.GetValue(EnabledKey, true);
            }
            catch
            {
                _enabled = true;
            }
            try
            {
                _scalePercent = Math.Max(
                    50,
                    Math.Min(200, Instances.Settings.GetValue(ScalePercentKey, 100)));
            }
            catch
            {
                _scalePercent = 100;
            }
            _artwork = LoadArtwork();
            WasperPrintPathPreviewSettings.EnabledChanged += SyncPathPreviewToolbarButton;

            Instances.CanvasCreated += CanvasCreated;
            StartAttachRetry();

            if (Instances.ActiveCanvas != null)
                QueueAttach(Instances.ActiveCanvas);
        }

        private static void CanvasCreated(GH_Canvas canvas)
        {
            QueueAttach(canvas);
        }

        private static void QueueAttach(GH_Canvas canvas)
        {
            if (canvas == null || canvas.IsDisposed)
                return;

            StartAttachRetry();

            try
            {
                canvas.BeginInvoke(new Action(() => Attach(canvas)));
            }
            catch
            {
                // Grasshopper may be shutting down between CanvasCreated and BeginInvoke.
            }
        }

        private static void Attach(GH_Canvas canvas)
        {
            if (canvas == null || canvas.IsDisposed)
                return;

            if (_canvas != null && !ReferenceEquals(_canvas, canvas))
                DetachCanvas(_canvas);
            _canvas = canvas;
            AttachCanvas(_canvas);
            EnsureToolbarButton();
            UpdateMascot();
            WasperWorkflowMapManager.RestoreAfterGrasshopperOpen();
        }

        private static void AttachCanvas(GH_Canvas canvas)
        {
            canvas.SizeChanged -= CanvasSizeChanged;
            canvas.SizeChanged += CanvasSizeChanged;
            canvas.CanvasPostPaintWidgets -= PaintMascot;
            canvas.CanvasPostPaintWidgets += PaintMascot;
            canvas.MouseDown -= CanvasMouseDown;
            canvas.MouseDown += CanvasMouseDown;
            canvas.MouseMove -= CanvasMouseMove;
            canvas.MouseMove += CanvasMouseMove;
            canvas.MouseLeave -= CanvasMouseLeave;
            canvas.MouseLeave += CanvasMouseLeave;
        }

        private static void DetachCanvas(GH_Canvas canvas)
        {
            if (canvas == null || canvas.IsDisposed)
                return;

            canvas.SizeChanged -= CanvasSizeChanged;
            canvas.CanvasPostPaintWidgets -= PaintMascot;
            canvas.MouseDown -= CanvasMouseDown;
            canvas.MouseMove -= CanvasMouseMove;
            canvas.MouseLeave -= CanvasMouseLeave;
        }

        private static void PaintMascot(GH_Canvas canvas)
        {
            if (!_enabled || _mascot == null || _mascot.IsDisposed || canvas.Graphics == null)
                return;

            _mascot.Draw(canvas);
        }

        private static void CanvasMouseDown(object sender, MouseEventArgs e)
        {
            if (!_enabled || _mascot == null || _mascot.IsDisposed ||
                sender is not GH_Canvas canvas || !_mascot.Contains(e.Location))
                return;

            var canvasEvent = new GH_CanvasMouseEvent(canvas.Viewport, e);
            canvas.ActiveInteraction = new WasperMascotInteraction(canvas, canvasEvent, _mascot);
        }

        private static void CanvasMouseMove(object sender, MouseEventArgs e)
        {
            if (_mascot == null || _mascot.IsDisposed)
                return;

            _mascot.SetHovering(_mascot.Contains(e.Location));
        }

        private static void CanvasMouseLeave(object sender, EventArgs e)
        {
            _mascot?.SetHovering(false);
        }

        private static void CanvasSizeChanged(object sender, EventArgs e)
        {
            if (_canvas == null || _canvas.IsDisposed ||
                _mascot == null || _mascot.IsDisposed)
                return;

            _mascot.ApplyDisplayScale(ComputeEffectiveMascotScale(_canvas));
            _mascot.Location = ClampToCanvas(_mascot.Location, _mascot.Size);
        }

        private static void StartAttachRetry()
        {
            if (_attachTimer == null)
            {
                _attachTimer = new Timer { Interval = 250 };
                _attachTimer.Tick += AttachTimerTick;
            }

            if (!_attachTimer.Enabled)
                _attachTimer.Start();
        }

        private static void AttachTimerTick(object sender, EventArgs e)
        {
            GH_Canvas active = Instances.ActiveCanvas;
            if (active == null || active.IsDisposed)
                return;

            Attach(active);

            if (_toolbarButton != null && !_toolbarButton.IsDisposed)
                _attachTimer.Stop();
        }

        private static void EnsureToolbarButton()
        {
            GH_DocumentEditor editor = Instances.DocumentEditor;
            if (editor == null || editor.IsDisposed)
                return;

            if (_toolbarButton != null && !_toolbarButton.IsDisposed &&
                _pathPreviewToolbarButton != null && !_pathPreviewToolbarButton.IsDisposed)
            {
                _toolbarButton.Checked = _enabled;
                SyncPathPreviewToolbarButton();
                return;
            }

            var button = new ToolStripButton
            {
                Name = "WASPerMascotButton",
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                CheckOnClick = true,
                Checked = _enabled,
                ToolTipText = "Show WASPet",
                ImageScaling = ToolStripItemImageScaling.None,
                Image = CreateToolbarImage(),
                BackColor = Color.FromArgb(255, 247, 222)
            };
            button.Click += ToolbarButtonClick;

            var pathPreviewButton = new ToolStripButton
            {
                Name = "WASPerPathPreviewButton",
                DisplayStyle = ToolStripItemDisplayStyle.Image,
                CheckOnClick = true,
                Checked = WasperPrintPathPreviewSettings.Enabled,
                ToolTipText = "Show or hide the wsp_path preview",
                ImageScaling = ToolStripItemImageScaling.None,
                Image = CreatePathPreviewToolbarImage(WasperPrintPathPreviewSettings.Enabled),
                BackColor = Color.FromArgb(222, 240, 251)
            };
            pathPreviewButton.Click += PathPreviewToolbarButtonClick;

            ToolStripItem previewOff = FindPrivateToolStripItem(editor, "_PreviewOffButton");
            ToolStrip owner = previewOff?.Owner ?? FindCanvasToolbar(editor);
            if (owner == null)
            {
                button.Dispose();
                pathPreviewButton.Dispose();
                return;
            }

            int insertionIndex = previewOff == null
                ? owner.Items.Count
                : owner.Items.IndexOf(previewOff) + 1;

            owner.Items.Insert(insertionIndex, new ToolStripSeparator
            {
                Name = "WASPerToolbarGroupStart"
            });
            owner.Items.Insert(insertionIndex + 1, button);
            owner.Items.Insert(insertionIndex + 2, pathPreviewButton);
            owner.Items.Insert(insertionIndex + 3, new ToolStripSeparator
            {
                Name = "WASPerToolbarGroupEnd"
            });
            _toolbarButton = button;
            _pathPreviewToolbarButton = pathPreviewButton;
        }

        private static void ToolbarButtonClick(object sender, EventArgs e)
        {
            // Mono's ToolStrip compatibility layer does not consistently update
            // CheckOnClick before Click. Toggle our own state and mirror it back.
            _enabled = !_enabled;
            if (_toolbarButton != null && !_toolbarButton.IsDisposed)
                _toolbarButton.Checked = _enabled;
            Instances.Settings.SetValue(EnabledKey, _enabled);
            UpdateMascot();
        }

        private static void PathPreviewToolbarButtonClick(object sender, EventArgs e)
        {
            bool enabled = !WasperPrintPathPreviewSettings.Enabled;
            WasperPrintPathPreviewSettings.Enabled = enabled;
            if (_pathPreviewToolbarButton != null && !_pathPreviewToolbarButton.IsDisposed)
                _pathPreviewToolbarButton.Checked = enabled;
        }

        private static void SyncPathPreviewToolbarButton()
        {
            if (_pathPreviewToolbarButton == null || _pathPreviewToolbarButton.IsDisposed)
                return;
            bool enabled = WasperPrintPathPreviewSettings.Enabled;
            _pathPreviewToolbarButton.Checked = enabled;
            _pathPreviewToolbarButton.ToolTipText = enabled
                ? "Hide the wsp_path preview"
                : "Show the wsp_path preview";
            Image oldImage = _pathPreviewToolbarButton.Image;
            _pathPreviewToolbarButton.Image = CreatePathPreviewToolbarImage(enabled);
            oldImage?.Dispose();
        }

        private static void UpdateMascot()
        {
            if (_canvas == null || _canvas.IsDisposed)
                return;

            if (!_enabled)
            {
                RemoveMascot();
                return;
            }

            if (_mascot != null && !_mascot.IsDisposed && ReferenceEquals(_mascot.Canvas, _canvas))
            {
                _mascot.ApplyDisplayScale(ComputeEffectiveMascotScale(_canvas));
                _mascot.Location = ClampToCanvas(_mascot.Location, _mascot.Size);
                _canvas.Invalidate();
                return;
            }

            RemoveMascot();

            _mascot = new WasperMascotOverlay(
                _canvas,
                _artwork,
                SaveMascotLocation,
                HideMascot,
                SetMascotScalePercent,
                _scalePercent,
                ComputeEffectiveMascotScale(_canvas));
            Point fallback = new Point(
                36,
                Math.Max(8, _canvas.ClientSize.Height - _mascot.Height - 36));
            Point saved = Instances.Settings.GetValue(LocationKey, fallback);
            _mascot.Location = ClampToCanvas(saved, _mascot.Size);
            _canvas.Invalidate();
        }

        private static float ComputeMascotScale(Control canvas)
        {
            if (canvas == null || canvas.IsDisposed)
                return 1f;

            try
            {
                Rectangle workingArea = Screen.FromControl(canvas).WorkingArea;
                float resolutionScale = Math.Min(
                    workingArea.Width / 1920f,
                    workingArea.Height / 1080f);
                float dpiScale = GetDeviceDpiScale(canvas);
                return Math.Max(0.78f, Math.Min(1.50f, resolutionScale * dpiScale));
            }
            catch
            {
                return 1f;
            }
        }

        private static float GetDeviceDpiScale(Control canvas)
        {
            try
            {
                // Access by name so the assembly contains no direct call to get_DeviceDpi.
                // Rhino for Mac's WinForms compatibility layer does not implement that getter.
                PropertyInfo property = canvas.GetType().GetProperty(
                    "DeviceDpi",
                    BindingFlags.Instance | BindingFlags.Public);
                if (property?.GetValue(canvas) is int dpi && dpi > 0)
                    return Math.Max(0.75f, dpi / 96f);
            }
            catch
            {
                // Use standard 96-DPI scaling when the host does not expose DeviceDpi.
            }
            return 1f;
        }

        private static float ComputeEffectiveMascotScale(Control canvas)
        {
            return ComputeMascotScale(canvas) * (_scalePercent / 100f);
        }

        private static void SetMascotScalePercent(int percent)
        {
            _scalePercent = Math.Max(50, Math.Min(200, percent));
            try { Instances.Settings.SetValue(ScalePercentKey, _scalePercent); }
            catch { }

            if (_mascot == null || _mascot.IsDisposed)
                return;
            _mascot.ApplyDisplayScale(ComputeEffectiveMascotScale(_canvas));
            _mascot.Location = ClampToCanvas(_mascot.Location, _mascot.Size);
            SaveMascotLocation(_mascot.Location);
        }

        private static void SaveMascotLocation(Point location)
        {
            Point clamped = ClampToCanvas(location, _mascot?.Size ?? Size.Empty);
            Instances.Settings.SetValue(LocationKey, clamped);
        }

        private static void HideMascot()
        {
            _enabled = false;
            Instances.Settings.SetValue(EnabledKey, false);
            if (_toolbarButton != null && !_toolbarButton.IsDisposed)
                _toolbarButton.Checked = false;
            RemoveMascot();
        }

        private static void RemoveMascot()
        {
            if (_mascot == null)
                return;

            _mascot.Dispose();
            _mascot = null;
            _canvas?.Invalidate();
        }

        private static Point ClampToCanvas(Point location, Size mascotSize)
        {
            if (_canvas == null || _canvas.IsDisposed)
                return location;

            int maxX = Math.Max(0, _canvas.ClientSize.Width - mascotSize.Width);
            int maxY = Math.Max(0, _canvas.ClientSize.Height - mascotSize.Height);
            return new Point(
                Math.Max(0, Math.Min(maxX, location.X)),
                Math.Max(0, Math.Min(maxY, location.Y)));
        }

        private static ToolStripItem FindPrivateToolStripItem(
            GH_DocumentEditor editor,
            string fieldName)
        {
            try
            {
                FieldInfo field = editor.GetType().GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.NonPublic);
                return field?.GetValue(editor) as ToolStripItem;
            }
            catch
            {
                return null;
            }
        }

        private static ToolStrip FindCanvasToolbar(GH_DocumentEditor editor)
        {
            try
            {
                FieldInfo field = editor.GetType().GetField(
                    "_CanvasToolbar",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                if (field?.GetValue(editor) is ToolStrip toolbar)
                    return toolbar;
            }
            catch
            {
                // Fall through to the control-tree search.
            }

            return FindToolStrip(editor);
        }

        private static ToolStrip FindToolStrip(Control root)
        {
            foreach (Control child in root.Controls)
            {
                if (child is ToolStrip strip)
                    return strip;

                ToolStrip nested = FindToolStrip(child);
                if (nested != null)
                    return nested;
            }

            return null;
        }

        private static Bitmap LoadArtwork()
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream(ResourceName);
                if (stream == null)
                    return null;

                using var source = new Bitmap(stream);
                var copy = new Bitmap(source);
                copy.MakeTransparent(Color.White);
                return copy;
            }
            catch
            {
                return null;
            }
        }

        private static Bitmap CreateToolbarImage()
        {
            var result = new Bitmap(28, 28);
            using Graphics graphics = Graphics.FromImage(result);
            graphics.Clear(Color.Transparent);
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var tile = new SolidBrush(Color.FromArgb(255, 242, 196)))
                graphics.FillEllipse(tile, 0, 0, 27, 27);

            if (_artwork != null)
            {
                Rectangle source = OpaqueBounds(_artwork);
                if (!source.IsEmpty)
                {
                    float scale = Math.Min(27f / source.Width, 27f / source.Height);
                    int width = Math.Max(1, (int)Math.Round(source.Width * scale));
                    int height = Math.Max(1, (int)Math.Round(source.Height * scale));
                    var destination = new Rectangle(
                        (28 - width) / 2,
                        (28 - height) / 2,
                        width,
                        height);
                    graphics.DrawImage(
                        _artwork,
                        destination,
                        source.X,
                        source.Y,
                        source.Width,
                        source.Height,
                        GraphicsUnit.Pixel);
                }
            }
            else
                DrawFallbackWasp(graphics, new Rectangle(1, 1, 26, 26));

            return result;
        }

        private static Bitmap CreatePathPreviewToolbarImage(bool enabled)
        {
            var result = new Bitmap(28, 28);
            using Graphics graphics = Graphics.FromImage(result);
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var tile = new SolidBrush(Color.FromArgb(200, 231, 249)))
                graphics.FillEllipse(tile, 0, 0, 27, 27);

            Color eyeColor = enabled
                ? Color.FromArgb(25, 101, 151)
                : Color.FromArgb(105, 116, 124);
            using var outline = new Pen(eyeColor, 2.1f);
            using var pupil = new SolidBrush(eyeColor);
            using var eye = new GraphicsPath();
            eye.AddBezier(3, 14, 8, 7, 20, 7, 25, 14);
            eye.AddBezier(25, 14, 20, 21, 8, 21, 3, 14);
            eye.CloseFigure();
            graphics.DrawPath(outline, eye);
            graphics.FillEllipse(pupil, 11, 10, 7, 8);
            if (!enabled)
            {
                using var slash = new Pen(Color.FromArgb(215, 78, 67), 2.8f);
                graphics.DrawLine(slash, 5, 5, 23, 23);
            }
            return result;
        }

        private static Rectangle OpaqueBounds(Bitmap bitmap)
        {
            int left = bitmap.Width;
            int top = bitmap.Height;
            int right = -1;
            int bottom = -1;
            for (int y = 0; y < bitmap.Height; y++)
            {
                for (int x = 0; x < bitmap.Width; x++)
                {
                    if (bitmap.GetPixel(x, y).A <= 16)
                        continue;
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }
            return right < left || bottom < top
                ? Rectangle.Empty
                : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }

        private static void DrawFallbackWasp(Graphics graphics, Rectangle bounds)
        {
            using var yellow = new SolidBrush(Color.FromArgb(255, 184, 35));
            using var black = new SolidBrush(Color.FromArgb(35, 35, 35));
            graphics.FillEllipse(yellow, bounds);
            int stripeWidth = Math.Max(2, bounds.Width / 7);
            graphics.FillRectangle(
                black,
                bounds.X + bounds.Width / 2 - stripeWidth / 2,
                bounds.Y + 3,
                stripeWidth,
                bounds.Height - 6);
        }
    }

    internal sealed class WasperMascotInteraction : GH_AbstractInteraction
    {
        private readonly WasperMascotOverlay _mascot;
        private readonly bool _dragging;
        private bool _finished;

        internal WasperMascotInteraction(
            GH_Canvas canvas,
            GH_CanvasMouseEvent initialEvent,
            WasperMascotOverlay mascot)
            : base(canvas, initialEvent, true)
        {
            _mascot = mascot;
            _dragging = initialEvent.Button == MouseButtons.Left;

            if (_dragging)
            {
                _mascot.BeginDrag(initialEvent.ControlLocation);
                canvas.Cursor = Cursors.Hand;
            }
            else if (initialEvent.Button == MouseButtons.Right)
            {
                Point screenPoint = canvas.PointToScreen(initialEvent.ControlLocation);
                _mascot.ShowContextMenu(screenPoint);
            }
        }

        public override bool DeactivateOnFocusLoss => false;

        public override GH_ObjectResponse RespondToMouseMove(
            GH_Canvas sender,
            GH_CanvasMouseEvent e)
        {
            if (_dragging)
                _mascot.DragTo(e.ControlLocation);
            return GH_ObjectResponse.Handled;
        }

        public override GH_ObjectResponse RespondToMouseUp(
            GH_Canvas sender,
            GH_CanvasMouseEvent e)
        {
            Finish(sender);
            return GH_ObjectResponse.Release;
        }

        public override void Destroy()
        {
            Finish(Canvas);
            base.Destroy();
        }

        private void Finish(GH_Canvas canvas)
        {
            if (_finished)
                return;
            _finished = true;
            if (_dragging)
                _mascot.EndDrag();
            canvas?.ResetCursor();
            canvas?.Invalidate();
        }
    }

    internal sealed class WasperMascotOverlay : IDisposable
    {
        private readonly GH_Canvas _canvas;
        private readonly Bitmap _artwork;
        private readonly Bitmap _bodyArtwork;
        private readonly Bitmap _leftWingArtwork;
        private readonly Bitmap _rightWingArtwork;
        private readonly Action<Point> _locationChanged;
        private readonly Action _hideRequested;
        private readonly Action<int> _scaleChanged;
        private int _scalePercent;
        private readonly Eto.Forms.UITimer _animationTimer;
        private bool _disposed;
        private bool _dragging;
        private bool _hovering;
        private Point _dragMouseScreen;
        private Point _dragControlOrigin;
        private double _phase;
        private double _wingPhase;
        private GH_Document _previewSnapshotDocument;
        private Dictionary<Guid, bool> _previewHiddenSnapshot;

        internal WasperMascotOverlay(
            GH_Canvas canvas,
            Bitmap artwork,
            Action<Point> locationChanged,
            Action hideRequested,
            Action<int> scaleChanged,
            int scalePercent,
            float displayScale)
        {
            _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
            _artwork = artwork;
            BuildWingLayers(
                artwork,
                out _bodyArtwork,
                out _leftWingArtwork,
                out _rightWingArtwork);
            _locationChanged = locationChanged;
            _hideRequested = hideRequested;
            _scaleChanged = scaleChanged;
            _scalePercent = scalePercent;

            ApplyDisplayScale(displayScale);
            _animationTimer = new Eto.Forms.UITimer { Interval = 0.04 };
            _animationTimer.Elapsed += AnimationTick;
            _animationTimer.Start();
        }

        internal GH_Canvas Canvas => _canvas;
        internal bool IsDisposed => _disposed;
        internal Point Location { get; set; }
        internal Size Size { get; private set; }
        internal int Width => Size.Width;
        internal int Height => Size.Height;
        internal Rectangle Bounds => new Rectangle(Location, Size);

        internal bool Contains(Point point)
        {
            return Bounds.Contains(point);
        }

        internal void ShowContextMenu(Point screenPoint)
        {
            if (_disposed)
                return;

            Eto.Forms.ContextMenu menu = CreateContextMenu();
            Eto.Drawing.PointF menuPoint = Eto.Forms.Mouse.IsSupported
                ? Eto.Forms.Mouse.Position
                : new Eto.Drawing.PointF(screenPoint.X, screenPoint.Y);
            menu.Show(menuPoint);
        }

        private Eto.Forms.ContextMenu CreateContextMenu()
        {
            var menu = new Eto.Forms.ContextMenu();
            menu.Items.Add(MenuItem("Hide WASPet", () => _hideRequested?.Invoke()));
            menu.Items.Add(MenuItem("Scale WASPet...", ShowScaleDialog));
            menu.Items.Add(MenuItem("Reset position", ResetPosition));
            menu.Items.Add(new Eto.Forms.SeparatorMenuItem());
            menu.Items.Add(CreateExamplesMenu());
            menu.Items.Add(new Eto.Forms.SeparatorMenuItem());
            menu.Items.Add(MenuItem("WASPer Display...", ShowDisplaySettingsDialog));

            var workflowMap = new Eto.Forms.CheckMenuItem
            {
                Text = "Show WASPer structure map in Rhino",
                Checked = WasperWorkflowMapManager.Enabled
            };
            workflowMap.Click += (_, _) =>
            {
                WasperWorkflowMapManager.Enabled = !WasperWorkflowMapManager.Enabled;
                workflowMap.Checked = WasperWorkflowMapManager.Enabled;
            };
            menu.Items.Add(workflowMap);

            var workflowLibrary = new Eto.Forms.ButtonMenuItem { Text = "Workflow library" };
            workflowLibrary.Items.Add(MenuItem(
                "Choose workflow folder...",
                WasperWorkflowMapManager.ChooseUserWorkflowFolder));
            workflowLibrary.Items.Add(MenuItem(
                "Rescan workflow folder",
                WasperWorkflowMapManager.RefreshUserWorkflows));
            menu.Items.Add(workflowLibrary);
            menu.Items.Add(new Eto.Forms.SeparatorMenuItem());
            menu.Items.Add(CreateInformationMenu());
            return menu;
        }

        private static Eto.Forms.ButtonMenuItem MenuItem(string text, Action action)
        {
            var item = new Eto.Forms.ButtonMenuItem { Text = text };
            if (action != null)
                item.Click += (_, _) => action();
            return item;
        }

        internal void Draw(GH_Canvas canvas)
        {
            Graphics graphics = canvas?.Graphics;
            if (_disposed || graphics == null)
                return;

            RectangleF canvasBounds = canvas.Viewport.UnprojectRectangle(
                new RectangleF(Location.X, Location.Y, Width, Height));
            GraphicsState rootState = graphics.Save();
            graphics.TranslateTransform(canvasBounds.X, canvasBounds.Y);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            GraphicsState displayState = graphics.Save();
            graphics.ScaleTransform(canvasBounds.Width / 116f, canvasBounds.Height / 124f);

            float bob = (float)(Math.Sin(_phase) * 3.0);
            float scale = 1.0f + (float)(Math.Sin(_phase * 0.72) * 0.018);
            float angle = (float)(Math.Sin(_phase * 0.58) * 1.5);

            using (var shadow = new SolidBrush(Color.FromArgb(34, 0, 0, 0)))
                graphics.FillEllipse(shadow, 35, 108, 46, 7);

            GraphicsState state = graphics.Save();
            // Painting happens in the fixed 116 x 124 logical design space.
            // Width has already been applied by displayState above, so using
            // Width / 2 here would scale the horizontal origin a second time
            // and clip the artwork on high-DPI displays.
            graphics.TranslateTransform(58f, 57f + bob);
            graphics.RotateTransform(angle);
            graphics.ScaleTransform(scale, scale);

            var destination = new Rectangle(-50, -50, 100, 100);
            if (_artwork != null)
            {
                bool flap = (_hovering || _dragging) &&
                            _bodyArtwork != null &&
                            _leftWingArtwork != null &&
                            _rightWingArtwork != null;
                if (flap)
                    DrawFlappingWasp(graphics, destination);
                else
                    graphics.DrawImage(_artwork, destination);
            }
            else
                DrawFallbackMascot(graphics, destination);

            graphics.Restore(state);
            graphics.Restore(displayState);
            graphics.Restore(rootState);
        }

        internal void ApplyDisplayScale(float scale)
        {
            scale = Math.Max(0.39f, Math.Min(3.00f, scale));
            var target = new Size(
                Math.Max(1, (int)Math.Round(116 * scale)),
                Math.Max(1, (int)Math.Round(124 * scale)));
            if (Size == target)
                return;
            Size = target;
            _canvas.Invalidate();
        }

        internal void SetHovering(bool hovering)
        {
            if (_hovering == hovering)
                return;

            _hovering = hovering;
            if (hovering)
                _wingPhase = 0.0;
            _canvas.Invalidate();
        }

        internal void BeginDrag(Point controlPoint)
        {
            _dragging = true;
            _dragMouseScreen = controlPoint;
            _dragControlOrigin = Location;
            _canvas.Invalidate();
        }

        internal void DragTo(Point controlPoint)
        {
            if (!_dragging)
                return;

            Point candidate = new Point(
                _dragControlOrigin.X + controlPoint.X - _dragMouseScreen.X,
                _dragControlOrigin.Y + controlPoint.Y - _dragMouseScreen.Y);
            Location = ClampToCanvas(candidate);
            _canvas.Invalidate();
        }

        internal void EndDrag()
        {
            if (!_dragging)
                return;
            _dragging = false;
            _locationChanged?.Invoke(Location);
            _canvas.Invalidate();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _animationTimer.Stop();
            _animationTimer.Elapsed -= AnimationTick;
            _bodyArtwork?.Dispose();
            _leftWingArtwork?.Dispose();
            _rightWingArtwork?.Dispose();
        }

        private void AnimationTick(object sender, EventArgs e)
        {
            _phase += 0.13;
            if (_hovering || _dragging)
                _wingPhase += _dragging ? 0.95 : 0.72;
            if (_phase > Math.PI * 200.0)
                _phase = 0.0;
            if (_wingPhase > Math.PI * 200.0)
                _wingPhase = 0.0;
            _canvas.Invalidate();
        }

        private void DrawFlappingWasp(Graphics graphics, Rectangle destination)
        {
            float flapAngle = (float)(Math.Sin(_wingPhase) * 11.0);
            float imageScale = destination.Width / (float)_artwork.Width;
            float leftPivotX = destination.X + _artwork.Width * 0.40f * imageScale;
            float rightPivotX = destination.X + _artwork.Width * 0.60f * imageScale;
            float pivotY = destination.Y + _artwork.Height * 0.33f * imageScale;

            DrawRotatedLayer(
                graphics,
                _leftWingArtwork,
                destination,
                leftPivotX,
                pivotY,
                -flapAngle);
            DrawRotatedLayer(
                graphics,
                _rightWingArtwork,
                destination,
                rightPivotX,
                pivotY,
                flapAngle);

            // The body is drawn last so it cleanly covers the rotating wing roots.
            graphics.DrawImage(_bodyArtwork, destination);
        }

        private static void DrawRotatedLayer(
            Graphics graphics,
            Bitmap layer,
            Rectangle destination,
            float pivotX,
            float pivotY,
            float angle)
        {
            GraphicsState state = graphics.Save();
            graphics.TranslateTransform(pivotX, pivotY);
            graphics.RotateTransform(angle);
            graphics.TranslateTransform(-pivotX, -pivotY);
            graphics.DrawImage(layer, destination);
            graphics.Restore(state);
        }

        private static void BuildWingLayers(
            Bitmap artwork,
            out Bitmap body,
            out Bitmap leftWing,
            out Bitmap rightWing)
        {
            body = null;
            leftWing = null;
            rightWing = null;

            if (artwork == null || artwork.Width < 16 || artwork.Height < 16)
                return;

            var bodyCandidate = new Bitmap(artwork);
            var leftCandidate = new Bitmap(artwork.Width, artwork.Height);
            var rightCandidate = new Bitmap(artwork.Width, artwork.Height);

            bool leftFound = ExtractConnectedPart(
                artwork,
                bodyCandidate,
                leftCandidate,
                new Point(
                    (int)(artwork.Width * 0.08),
                    (int)(artwork.Height * 0.19)));
            bool rightFound = ExtractConnectedPart(
                artwork,
                bodyCandidate,
                rightCandidate,
                new Point(
                    (int)(artwork.Width * 0.92),
                    (int)(artwork.Height * 0.19)));

            if (!leftFound || !rightFound)
            {
                bodyCandidate.Dispose();
                leftCandidate.Dispose();
                rightCandidate.Dispose();
                return;
            }

            body = bodyCandidate;
            leftWing = leftCandidate;
            rightWing = rightCandidate;
        }

        private static bool ExtractConnectedPart(
            Bitmap source,
            Bitmap body,
            Bitmap part,
            Point approximateSeed)
        {
            Point seed = FindOpaqueSeed(source, approximateSeed, 50);
            if (seed.X < 0)
                return false;

            int width = source.Width;
            int height = source.Height;
            var visited = new bool[width * height];
            var pending = new Queue<int>();
            int seedIndex = seed.Y * width + seed.X;
            visited[seedIndex] = true;
            pending.Enqueue(seedIndex);
            int pixelCount = 0;

            while (pending.Count > 0)
            {
                int index = pending.Dequeue();
                int x = index % width;
                int y = index / width;
                Color color = source.GetPixel(x, y);
                if (color.A <= 16)
                    continue;

                part.SetPixel(x, y, color);
                body.SetPixel(x, y, Color.Transparent);
                pixelCount++;

                EnqueuePixel(x - 1, y, width, height, visited, pending);
                EnqueuePixel(x + 1, y, width, height, visited, pending);
                EnqueuePixel(x, y - 1, width, height, visited, pending);
                EnqueuePixel(x, y + 1, width, height, visited, pending);
            }

            return pixelCount > width * height / 200;
        }

        private static Point FindOpaqueSeed(Bitmap bitmap, Point center, int radius)
        {
            for (int distance = 0; distance <= radius; distance++)
            {
                int minX = Math.Max(0, center.X - distance);
                int maxX = Math.Min(bitmap.Width - 1, center.X + distance);
                int minY = Math.Max(0, center.Y - distance);
                int maxY = Math.Min(bitmap.Height - 1, center.Y + distance);

                for (int x = minX; x <= maxX; x++)
                {
                    if (bitmap.GetPixel(x, minY).A > 16)
                        return new Point(x, minY);
                    if (bitmap.GetPixel(x, maxY).A > 16)
                        return new Point(x, maxY);
                }

                for (int y = minY + 1; y < maxY; y++)
                {
                    if (bitmap.GetPixel(minX, y).A > 16)
                        return new Point(minX, y);
                    if (bitmap.GetPixel(maxX, y).A > 16)
                        return new Point(maxX, y);
                }
            }

            return new Point(-1, -1);
        }

        private static void EnqueuePixel(
            int x,
            int y,
            int width,
            int height,
            bool[] visited,
            Queue<int> pending)
        {
            if (x < 0 || y < 0 || x >= width || y >= height)
                return;

            int index = y * width + x;
            if (visited[index])
                return;

            visited[index] = true;
            pending.Enqueue(index);
        }

        private void ResetPosition()
        {
            Location = new Point(
                36,
                Math.Max(8, _canvas.ClientSize.Height - Height - 36));
            _locationChanged?.Invoke(Location);
            _canvas.Invalidate();
        }

        private Eto.Forms.ButtonMenuItem CreateExamplesMenu()
        {
            var examplesItem = new Eto.Forms.ButtonMenuItem { Text = "Insert WASPer example" };
            WASPerExampleLibrary.ExampleFileGroups groups =
                WASPerExampleLibrary.GetExampleFileGroups();

            if (groups.BuiltIn.Count == 0 && groups.User.Count == 0)
            {
                examplesItem.Items.Add(new Eto.Forms.ButtonMenuItem
                {
                    Text = "No example files found",
                    Enabled = false
                });
                return examplesItem;
            }

            AddExamplesDirectly(examplesItem, groups.BuiltIn);
            if (groups.BuiltIn.Count > 0 && groups.User.Count > 0)
                examplesItem.Items.Add(new Eto.Forms.SeparatorMenuItem());
            AddExampleGroup(examplesItem, "User examples", groups.User);
            return examplesItem;
        }

        private void AddExamplesDirectly(
            Eto.Forms.ButtonMenuItem parent,
            IList<string> fileNames)
        {
            foreach (string fileName in fileNames)
            {
                string capturedName = fileName;
                parent.Items.Add(MenuItem(
                    capturedName,
                    () => InsertExampleFromPet(capturedName)));
            }
        }

        private void AddExampleGroup(
            Eto.Forms.ButtonMenuItem parent,
            string label,
            IList<string> fileNames)
        {
            if (fileNames.Count == 0)
                return;

            var group = new Eto.Forms.ButtonMenuItem { Text = label };
            foreach (string fileName in fileNames)
            {
                string capturedName = fileName;
                group.Items.Add(MenuItem(
                    capturedName,
                    () => InsertExampleFromPet(capturedName)));
            }

            parent.Items.Add(group);
        }

        private void InsertExampleFromPet(string fileName)
        {
            var targetDocument = Instances.ActiveCanvas?.Document;
            if (WASPerExampleLibrary.BeginExamplePlacement(
                    fileName,
                    targetDocument,
                    out string error))
                return;

            Eto.Forms.MessageBox.Show(
                EtoParent(),
                error,
                "WASPet",
                Eto.Forms.MessageBoxType.Error);
        }


        private static int ProfileStepFromExponent(int exponent)
        {
            if (exponent <= 2) return 1;
            if (exponent == 3) return 2;
            if (exponent <= 5) return 3;
            return 4;
        }

        private static int ProfileExponentFromStep(int step)
        {
            if (step <= 1) return 2;
            if (step == 2) return 3;
            if (step == 3) return 4;
            return 6;
        }

        private void ShowScaleDialog()
        {
            var dialog = CreateDialog("Scale WASPet", new Eto.Drawing.Size(320, 125));
            var valueLabel = new Eto.Forms.Label
            {
                Text = _scalePercent + "%",
                TextAlignment = Eto.Forms.TextAlignment.Center
            };
            var slider = new Eto.Forms.Slider
            {
                MinValue = 50,
                MaxValue = 200,
                Value = Math.Max(50, Math.Min(200, _scalePercent))
            };
            slider.ValueChanged += (_, _) =>
            {
                _scalePercent = slider.Value;
                valueLabel.Text = slider.Value + "%";
                _scaleChanged?.Invoke(slider.Value);
            };

            var close = CreateCommandButton("Close", 90);
            close.Click += (_, _) => dialog.Close();
            var content = new Eto.Forms.TableLayout
            {
                Padding = new Eto.Drawing.Padding(14),
                Spacing = new Eto.Drawing.Size(0, 10),
                Rows =
                {
                    new Eto.Forms.TableRow(new Eto.Forms.TableCell(valueLabel, true)),
                    new Eto.Forms.TableRow(new Eto.Forms.TableCell(slider, true))
                }
            };
            dialog.Content = CreateDialogRoot(content, null, close);
            dialog.DefaultButton = close;
            dialog.AbortButton = close;
            ShowDialog(dialog);
        }

        private void ShowDisplaySettingsDialog()
        {
            var dialog = CreateDialog("WASPer Display", new Eto.Drawing.Size(640, 600));
            dialog.MinimumSize = new Eto.Drawing.Size(520, 480);
            dialog.Resizable = true;
            var content = new Eto.Forms.StackLayout
            {
                Padding = new Eto.Drawing.Padding(14),
                Spacing = 9
            };

            GH_Document document = _canvas.Document;
            bool canRestore = document != null &&
                              ReferenceEquals(document, _previewSnapshotDocument) &&
                              _previewHiddenSnapshot != null;
            var hidePreviews = CreateCommandButton("Hide all WASPer previews", 210);
            hidePreviews.Enabled = document != null;
            hidePreviews.Click += (_, _) => HideWasperPreviews();
            var restorePreviews = CreateCommandButton(
                "Restore previous WASPer previews",
                250);
            restorePreviews.Enabled = canRestore;
            restorePreviews.Click += (_, _) => RestoreWasperPreviews();
            content.Items.Add(CreateTwoColumnRow(hidePreviews, restorePreviews));

            var pathPreview = new Eto.Forms.CheckBox
            {
                Text = "Show wsp_path preview",
                Checked = WasperPrintPathPreviewSettings.Enabled
            };
            pathPreview.CheckedChanged += (_, _) =>
                WasperPrintPathPreviewSettings.Enabled = pathPreview.Checked == true;
            content.Items.Add(pathPreview);

            string[] paletteLabels =
            {
                "WASPer blue",
                "Path roles - classic",
                "Path roles - vivid",
                "Clay raw - gray redware",
                "Clay fired - gray redware",
                "Clay raw - red earthenware",
                "Clay fired - red earthenware",
                "Clay raw - buff earthenware",
                "Clay fired - buff earthenware",
                "Clay raw - white stoneware",
                "Clay fired - white stoneware",
                "Clay raw - pink clay",
                "Clay fired - pink clay",
                "Path roles - brighter vivid",
                "Path roles - color blind",
                "Neutral gray",
                "Custom color",
                "Custom colors by role"
            };
            WasperPrintPathPreviewMode[] paletteModes =
            {
                WasperPrintPathPreviewMode.WasperBlue,
                WasperPrintPathPreviewMode.RoleClassic,
                WasperPrintPathPreviewMode.RoleVivid,
                WasperPrintPathPreviewMode.ClayRawGrayRedware,
                WasperPrintPathPreviewMode.ClayFiredGrayRedware,
                WasperPrintPathPreviewMode.ClayRawRedEarthenware,
                WasperPrintPathPreviewMode.ClayFiredRedEarthenware,
                WasperPrintPathPreviewMode.ClayRawBuffEarthenware,
                WasperPrintPathPreviewMode.ClayFiredBuffEarthenware,
                WasperPrintPathPreviewMode.ClayRawWhiteStoneware,
                WasperPrintPathPreviewMode.ClayFiredWhiteStoneware,
                WasperPrintPathPreviewMode.ClayRawPinkClay,
                WasperPrintPathPreviewMode.ClayFiredPinkClay,
                WasperPrintPathPreviewMode.RoleBright,
                WasperPrintPathPreviewMode.RoleColorBlind,
                WasperPrintPathPreviewMode.NeutralGray,
                WasperPrintPathPreviewMode.Custom,
                WasperPrintPathPreviewMode.CustomByRole
            };
            var palette = new Eto.Forms.DropDown { DataStore = paletteLabels };
            int selectedPalette = Array.IndexOf(
                paletteModes,
                WasperPrintPathPreviewSettings.Mode);
            palette.SelectedIndex = selectedPalette < 0 ? 0 : selectedPalette;
            palette.SelectedIndexChanged += (_, _) =>
            {
                if (palette.SelectedIndex >= 0 && palette.SelectedIndex < paletteModes.Length)
                    WasperPrintPathPreviewSettings.Mode = paletteModes[palette.SelectedIndex];
            };
            content.Items.Add(CreateLabeledRow("wsp_path palette", palette));

            var customColor = CreateCommandButton("Choose custom color...", 190);
            customColor.Click += (_, _) => ChoosePrintPathColor(dialog);
            var roleColors = CreateCommandButton("Edit role colors...", 160);
            roleColors.Click += (_, _) => ShowRoleColorsDialog();
            content.Items.Add(CreateTwoColumnRow(customColor, roleColors));

            var applyToVisualizers = new Eto.Forms.CheckBox
            {
                Text = "Apply palette to Pp04 and Sl07 when role_colors is empty",
                Checked = WasperPrintPathPreviewSettings.ApplyToVisualizers
            };
            applyToVisualizers.CheckedChanged += (_, _) =>
                WasperPrintPathPreviewSettings.ApplyToVisualizers =
                    applyToVisualizers.Checked == true;
            content.Items.Add(applyToVisualizers);

            AddSliderRow(
                content,
                "Line thickness",
                1,
                5,
                WasperPrintPathPreviewSettings.Thickness,
                value => value + " px",
                value => WasperPrintPathPreviewSettings.Thickness = value);
            AddSliderRow(
                content,
                "Bead profile exponent",
                1,
                4,
                ProfileStepFromExponent(WasperPrintPathPreviewSettings.BeadProfileExponent),
                value => ProfileExponentFromStep(value).ToString(),
                value => WasperPrintPathPreviewSettings.BeadProfileExponent =
                    ProfileExponentFromStep(value));
            AddSliderRow(
                content,
                "Ambient",
                0,
                100,
                (int)Math.Round(WasperPrintPathPreviewSettings.Ambient * 100.0),
                value => (value / 100.0).ToString("0.00"),
                value => WasperPrintPathPreviewSettings.Ambient = value / 100.0);
            AddSliderRow(
                content,
                "Shade strength",
                0,
                100,
                (int)Math.Round(WasperPrintPathPreviewSettings.ShadeStrength * 100.0),
                value => (value / 100.0).ToString("0.00"),
                value => WasperPrintPathPreviewSettings.ShadeStrength = value / 100.0);
            AddSliderRow(
                content,
                "Light azimuth",
                -180,
                180,
                (int)Math.Round(WasperPrintPathPreviewSettings.LightAzimuth),
                value => value + " deg",
                value => WasperPrintPathPreviewSettings.LightAzimuth = value);
            AddSliderRow(
                content,
                "Light altitude",
                -90,
                90,
                (int)Math.Round(WasperPrintPathPreviewSettings.LightAltitude),
                value => value + " deg",
                value => WasperPrintPathPreviewSettings.LightAltitude = value);

            var reset = CreateCommandButton("Reset display settings", 170);
            reset.Click += (_, _) => WasperPrintPathPreviewSettings.Reset();
            var close = CreateCommandButton("Close", 90);
            close.Click += (_, _) => dialog.Close();
            dialog.Content = CreateDialogRoot(content, reset, close, true);
            dialog.DefaultButton = close;
            dialog.AbortButton = close;
            ShowDialog(dialog);
        }

        private static void AddSliderRow(
            Eto.Forms.StackLayout layout,
            string label,
            int minimum,
            int maximum,
            int value,
            Func<int, string> format,
            Action<int> changed)
        {
            var valueLabel = new Eto.Forms.Label
            {
                Text = format(value),
                Width = 64,
                TextAlignment = Eto.Forms.TextAlignment.Right
            };
            var slider = new Eto.Forms.Slider
            {
                MinValue = minimum,
                MaxValue = maximum,
                Value = Math.Max(minimum, Math.Min(maximum, value))
            };
            slider.ValueChanged += (_, _) =>
            {
                valueLabel.Text = format(slider.Value);
                changed(slider.Value);
            };
            var labelControl = new Eto.Forms.Label { Text = label, Width = 170 };
            layout.Items.Add(new Eto.Forms.TableLayout
            {
                Spacing = new Eto.Drawing.Size(10, 0),
                Rows =
                {
                    new Eto.Forms.TableRow(
                        new Eto.Forms.TableCell(labelControl, false),
                        new Eto.Forms.TableCell(slider, true),
                        new Eto.Forms.TableCell(valueLabel, false))
                }
            });
        }

        private static Eto.Forms.Button CreateCommandButton(string text, int minimumWidth)
        {
            return new Eto.Forms.Button
            {
                Text = text,
                Height = 30,
                MinimumSize = new Eto.Drawing.Size(minimumWidth, 30)
            };
        }

        private static Eto.Forms.Control CreateTwoColumnRow(
            Eto.Forms.Control left,
            Eto.Forms.Control right)
        {
            return new Eto.Forms.TableLayout
            {
                Spacing = new Eto.Drawing.Size(8, 0),
                Rows =
                {
                    new Eto.Forms.TableRow(
                        new Eto.Forms.TableCell(left, true),
                        new Eto.Forms.TableCell(right, true))
                }
            };
        }

        private static Eto.Forms.Control CreateLabeledRow(
            string label,
            Eto.Forms.Control control)
        {
            return new Eto.Forms.TableLayout
            {
                Spacing = new Eto.Drawing.Size(10, 0),
                Rows =
                {
                    new Eto.Forms.TableRow(
                        new Eto.Forms.TableCell(
                            new Eto.Forms.Label { Text = label, Width = 170 },
                            false),
                        new Eto.Forms.TableCell(control, true))
                }
            };
        }

        private static Eto.Forms.Control CreateDialogRoot(
            Eto.Forms.Control content,
            Eto.Forms.Button secondary,
            Eto.Forms.Button close,
            bool scrollContent = false)
        {
            Eto.Forms.Control body = scrollContent
                ? new Eto.Forms.Scrollable { Content = content }
                : content;
            var footer = new Eto.Forms.TableLayout
            {
                Padding = new Eto.Drawing.Padding(14, 8),
                Spacing = new Eto.Drawing.Size(8, 0),
                Rows =
                {
                    new Eto.Forms.TableRow(
                        new Eto.Forms.TableCell(secondary, false),
                        new Eto.Forms.TableCell(null, true),
                        new Eto.Forms.TableCell(close, false))
                }
            };
            return new Eto.Forms.TableLayout
            {
                Rows =
                {
                    new Eto.Forms.TableRow(new Eto.Forms.TableCell(body, true))
                    {
                        ScaleHeight = true
                    },
                    new Eto.Forms.TableRow(new Eto.Forms.TableCell(footer, true))
                }
            };
        }

        private void ShowRoleColorsDialog()
        {
            var dialog = CreateDialog("wsp_path role colors", new Eto.Drawing.Size(400, 340));
            var content = new Eto.Forms.StackLayout
            {
                Padding = new Eto.Drawing.Padding(14),
                Spacing = 8
            };
            AddRoleColorRow(content, dialog, "Shell", WasperPathRole.Shell);
            AddRoleColorRow(content, dialog, "Infill", WasperPathRole.Infill);
            AddRoleColorRow(content, dialog, "Partition", WasperPathRole.Partition);
            AddRoleColorRow(content, dialog, "Support", WasperPathRole.Support);
            AddRoleColorRow(content, dialog, "Transition", WasperPathRole.Transition);
            AddRoleColorRow(content, dialog, "Undefined", WasperPathRole.Undefined);
            var usePalette = CreateCommandButton("Use custom role palette", 190);
            usePalette.Click += (_, _) =>
                WasperPrintPathPreviewSettings.Mode = WasperPrintPathPreviewMode.CustomByRole;
            var close = CreateCommandButton("Close", 90);
            close.Click += (_, _) => dialog.Close();
            dialog.Content = CreateDialogRoot(content, usePalette, close);
            dialog.DefaultButton = close;
            dialog.AbortButton = close;
            ShowDialog(dialog);
        }

        private void AddRoleColorRow(
            Eto.Forms.StackLayout layout,
            Eto.Forms.Window owner,
            string label,
            WasperPathRole role)
        {
            var button = CreateCommandButton("Choose...", 100);
            button.Click += (_, _) => ChoosePrintPathRoleColor(owner, role);
            layout.Items.Add(CreateLabeledRow(label, button));
        }

        private void ChoosePrintPathColor(Eto.Forms.Window owner)
        {
            var picker = new Eto.Forms.ColorDialog
            {
                Color = ToEtoColor(WasperPrintPathPreviewSettings.CustomColor)
            };
            if (picker.ShowDialog(owner) != Eto.Forms.DialogResult.Ok)
                return;
            WasperPrintPathPreviewSettings.CustomColor = ToSystemColor(picker.Color);
            WasperPrintPathPreviewSettings.Mode = WasperPrintPathPreviewMode.Custom;
        }

        private void ChoosePrintPathRoleColor(Eto.Forms.Window owner, WasperPathRole role)
        {
            var picker = new Eto.Forms.ColorDialog
            {
                Color = ToEtoColor(WasperPrintPathPreviewSettings.CustomRoleColor(role))
            };
            if (picker.ShowDialog(owner) != Eto.Forms.DialogResult.Ok)
                return;
            WasperPrintPathPreviewSettings.SetCustomRoleColor(role, ToSystemColor(picker.Color));
            WasperPrintPathPreviewSettings.Mode = WasperPrintPathPreviewMode.CustomByRole;
        }

        private static Eto.Drawing.Color ToEtoColor(Color color)
        {
            return Eto.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B);
        }

        private static Color ToSystemColor(Eto.Drawing.Color color)
        {
            return Color.FromArgb(color.Ab, color.Rb, color.Gb, color.Bb);
        }

        private void HideWasperPreviews()
        {
            GH_Document document = Instances.ActiveCanvas?.Document;
            if (document == null)
                return;

            if (!ReferenceEquals(document, _previewSnapshotDocument) ||
                _previewHiddenSnapshot == null)
            {
                _previewSnapshotDocument = document;
                _previewHiddenSnapshot = new Dictionary<Guid, bool>();
                foreach (IGH_DocumentObject obj in document.Objects)
                {
                    if (IsWasperPreviewObject(obj, out IGH_PreviewObject preview))
                        _previewHiddenSnapshot[obj.InstanceGuid] = preview.Hidden;
                }
            }

            SetWasperPreviewStates(document, null, true, "Hide WASPer previews");
        }

        private void RestoreWasperPreviews()
        {
            GH_Document document = Instances.ActiveCanvas?.Document;
            if (document == null ||
                !ReferenceEquals(document, _previewSnapshotDocument) ||
                _previewHiddenSnapshot == null)
                return;

            SetWasperPreviewStates(
                document,
                _previewHiddenSnapshot,
                false,
                "Restore WASPer previews");
            _previewSnapshotDocument = null;
            _previewHiddenSnapshot = null;
        }

        private static void SetWasperPreviewStates(
            GH_Document document,
            Dictionary<Guid, bool> states,
            bool fallback,
            string undoName)
        {
            int undoCount = 0;
            foreach (IGH_DocumentObject obj in document.Objects)
            {
                if (!IsWasperPreviewObject(obj, out IGH_PreviewObject preview))
                    continue;

                bool target = fallback;
                if (states != null &&
                    !states.TryGetValue(obj.InstanceGuid, out target))
                    continue;
                if (preview.Hidden == target)
                    continue;

                if (obj is IGH_ActiveObject active)
                {
                    document.UndoUtil.RecordPreviewEvent(undoName, active);
                    undoCount++;
                }
                preview.Hidden = target;
            }

            if (undoCount > 1)
                document.UndoUtil.MergeRecords(undoCount);
            WasperPrintPathPreviewSettings.Redraw();
            Instances.ActiveCanvas?.Invalidate();
        }

        private static bool IsWasperPreviewObject(
            IGH_DocumentObject obj,
            out IGH_PreviewObject preview)
        {
            preview = obj as IGH_PreviewObject;
            return preview != null &&
                    obj.GetType().Assembly == typeof(WasperMascotOverlay).Assembly;
        }


        private Eto.Forms.ButtonMenuItem CreateInformationMenu()
        {
            var information = new Eto.Forms.ButtonMenuItem { Text = "About WASPer_3DP" };
            information.Items.Add(MenuItem(
                "About WASPer_3DP...",
                () => ShowInformationDialog("About WASPer_3DP", AboutText())));
            information.Items.Add(new Eto.Forms.ButtonMenuItem
            {
                Text = "Version " + VersionTag(),
                Enabled = false
            });
            information.Items.Add(MenuItem(
                "Component structure...",
                () => ShowInformationDialog(
                    "WASPer_3DP component structure",
                    LoadStructureText())));
            information.Items.Add(MenuItem(
                "WASPer path (wsp_path)...",
                () => ShowInformationDialog(
                    "WASPer path (wsp_path)",
                    LoadWspPathGuideText())));
            information.Items.Add(new Eto.Forms.SeparatorMenuItem());
            information.Items.Add(MenuItem(
                "YouTube",
                () => OpenWebLink("https://www.youtube.com/@WASPer_3DP")));
            information.Items.Add(MenuItem(
                "Instagram",
                () => OpenWebLink(
                    "https://www.instagram.com/wasper_3dp?igsh=MTAwenRtcDV6c2Q3dw==")));
            information.Items.Add(MenuItem(
                "LinkedIn",
                () => OpenWebLink("https://www.linkedin.com/in/juan-diego-vargas-vel/")));
            return information;
        }

        private void ShowInformationDialog(string title, string text)
        {
            var dialog = CreateDialog(title, new Eto.Drawing.Size(820, 640));
            dialog.MinimumSize = new Eto.Drawing.Size(560, 380);
            dialog.Resizable = true;
            var content = new Eto.Forms.TextArea
            {
                ReadOnly = true,
                Wrap = true,
                Text = NormalizeDialogText(text)
            };
            var close = CreateCommandButton("Close", 90);
            close.Click += (_, _) => dialog.Close();
            var contentLayout = new Eto.Forms.TableLayout
            {
                Padding = new Eto.Drawing.Padding(12),
                Rows =
                {
                    new Eto.Forms.TableRow(new Eto.Forms.TableCell(content, true))
                    {
                        ScaleHeight = true
                    }
                }
            };
            dialog.Content = CreateDialogRoot(contentLayout, null, close);
            dialog.DefaultButton = close;
            dialog.AbortButton = close;
            ShowDialog(dialog);
        }

        private Eto.Forms.Dialog CreateDialog(string title, Eto.Drawing.Size size)
        {
            var dialog = new Eto.Forms.Dialog
            {
                Title = title,
                ClientSize = size,
                ShowInTaskbar = false,
                Resizable = false
            };
            dialog.UseRhinoStyle();
            return dialog;
        }

        private void ShowDialog(Eto.Forms.Dialog dialog)
        {
            Eto.Forms.Control parent = EtoParent();
            if (parent != null)
                dialog.ShowModal(parent);
            else
                dialog.ShowModal();
        }

        private Eto.Forms.Control EtoParent()
        {
            RhinoDoc document = _canvas.Document?.RhinoDocument;
            return document == null ? null : RhinoEtoApp.MainWindowForDocument(document);
        }

        private static string NormalizeDialogText(string text)
        {
            return (text ?? string.Empty)
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Replace("\n", Environment.NewLine);
        }

        private void OpenWebLink(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Eto.Forms.MessageBox.Show(
                    EtoParent(),
                    "Could not open the link.\n\n" + ex.Message,
                    "WASPet",
                    Eto.Forms.MessageBoxType.Error);
            }
        }

        private static string VersionTag()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null
                ? "v1.0.x"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
        }

        private static string AboutText()
        {
            return
                "Created by Juan Diego Vargas.\r\n\r\n" +
                "WASPer_3DP is a Rhino 8 / Grasshopper plugin for Design for " +
                "Additive Manufacturing (DfAM). It supports computational design, " +
                "digital fabrication, and early-stage performance feedback for " +
                "3D-printed building components, with a particular focus on large-scale " +
                "Liquid Deposition Modeling (LDM), clay-based printing, and WASP40100-" +
                "style workflows.\r\n\r\n" +
                "The plugin connects geometry processing, implicit fields, infill " +
                "generation, slicing, print-path preparation, G-code generation, " +
                "material/layer data, visualization, and selected research-oriented " +
                "analysis tools inside one parametric environment.\r\n\r\n" +
                "It also supports robotic fabrication workflows: generated wsp_path " +
                "toolpaths contain fabrication data such as planes/targets, layer " +
                "heights, roles, process values, and metadata that can be used in " +
                "combination with plugins such as Robots or KUKA|prc. WASPer_3DP also " +
                "includes a dedicated 5.1_Robot Gcode category with components " +
                "compatible with the open-source Robots plugin.\r\n\r\n" +
                "A central concept in recent versions is the WASPer path (wsp_path): " +
                "a structured fabrication-path container that keeps curves, layers, " +
                "point-level process values, roles, motion information, and metadata " +
                "together as one reusable object.\r\n\r\n" +
                "WASPer_3DP is intended for researchers, architects, engineers, " +
                "fabricators, and makers exploring additive manufacturing for " +
                "architecture and construction. It is an independent research project " +
                "developed at Politecnico di Torino and currently enhanced in " +
                "collaboration with ACTech Hub at the University of Minho.\r\n\r\n" +
                "Main capabilities include:\r\n" +
                "- Planar, non-planar, and surface-aware slicing workflows for " +
                "3D-printing paths, supporting both 3-axis and selected multi-axis " +
                "fabrication workflows\r\n" +
                "- 2D and 3D infill generation, including S-patterns, spiral paths, " +
                "conformal paths, TPMS, polyhedral/cellular systems, brick-like " +
                "cavities, and custom SDF-based infills\r\n" +
                "- Signed-distance-field tools for implicit geometry, field booleans, " +
                "offsets, shells, contouring, meshing, field transformation, " +
                "Isopod/WASPer field exchange, and field-based path operations\r\n" +
                "- Mesh, image, bitmap, and painting tools for color fields, scalar " +
                "fields, local mesh deformation, and texture-driven geometry workflows\r\n" +
                "- Printing-path preparation, path optimization, curvature/proximity-" +
                "based point reduction, flow assignment, velocity utilities, fuzzy-skin " +
                "and fuzzy-pocket tools, proximity-based flow estimation, and path " +
                "visualization\r\n" +
                "- Marlin G-code generation, parsing, saving, and process simulation for " +
                "LDM/FDM workflows, currently focused on 3-axis printer workflows\r\n" +
                "- Robots-compatible components for target generation from wsp_path, " +
                "target utilities, tool/base helpers, KRL merging, and KUKA post-" +
                "processing support. These components are functional but still require " +
                "further testing across robot setups, tools, and fabrication scenarios\r\n" +
                "- Facade and panelization tools for UV-based panels, joints, weighted " +
                "tile layouts, and SDF-informed facade systems\r\n" +
                "- Material and layer utilities for opaque/gas materials, equivalent air " +
                "conductivity, water-content calculations, 3D-printing material " +
                "properties, and reusable material/layer records\r\n" +
                "- Morphological and fabrication-oriented characterization tools, " +
                "including porosity, tortuosity, surface area, printability proxies, " +
                "shrinkage, pore-flow relation metrics, fresh-deformation risk proxies, " +
                "and beam-deflection proxy checks\r\n" +
                "- Building-physics research tools, including analytical steady/dynamic " +
                "thermal calculations, numerical conduction solvers, weather/solar " +
                "series generators, thermal-field visualization, U-value/equivalent-" +
                "conductivity utilities, and adaptive comfort calculations. These " +
                "methods are still under experimental validation at Politecnico di " +
                "Torino and should be used as research and early-stage comparison " +
                "tools, not as certified engineering solvers\r\n" +
                "- Experimental moisture and structural proxy components, also under " +
                "experimental validation at Politecnico di Torino\r\n" +
                "- Native data-visualization tools, CSV/Excel/JSON utilities, Grasshopper " +
                "document inspection helpers, viewport/image export, and workflow " +
                "automation utilities\r\n" +
                "- Study Manager workflows for comparing design alternatives, capturing " +
                "iterations, linking Grasshopper sliders, exporting study data, " +
                "generating reports, and reviewing design/performance KPIs from a " +
                "dedicated manager interface\r\n" +
                "- Web-based XR/AR Process Viewer tools for packaging and live-previewing " +
                "fabrication jobs, including path/mesh playback, contextual printer or " +
                "scene geometry, mobile access through QR links, and browser/AR " +
                "inspection of 3D-printing processes. This is currently intended as an " +
                "experimental visualization and communication tool rather than a " +
                "metrology, collision-safety, or machine-control system; AR scale, " +
                "alignment, browser performance, device compatibility, and live " +
                "synchronization depend on the receiving device and network conditions. " +
                "Sharing is currently local-network/localhost based and does not include " +
                "an internet-hosted/public cloud sharing service.\r\n\r\n" +
                "WASPer_3DP is still under active development. Some components may " +
                "contain bugs, edge cases, changing interfaces, or methods that require " +
                "further validation. Feedback, bug reports, suggestions, and examples " +
                "of use are always welcome.\r\n\r\n" +
                "Version: " + VersionTag() + "\r\n" +
                "Author: Juan Diego Vargas";
        }

        private static string LoadStructureText()
        {
            return LoadEmbeddedText(
                "WASPer_3DP_structure.txt",
                "WASPer_3DP structure file");
        }

        private static string LoadWspPathGuideText()
        {
            return LoadEmbeddedText(
                "WASPER_WSP_PATH_GUIDE.md",
                "WASPer path guide");
        }

        private static string LoadEmbeddedText(string resourceSuffix, string label)
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                string resourceName = null;
                foreach (string name in assembly.GetManifestResourceNames())
                {
                    if (name.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        resourceName = name;
                        break;
                    }
                }

                if (resourceName == null)
                    return "The embedded " + label + " was not found.";

                using Stream stream = assembly.GetManifestResourceStream(resourceName);
                if (stream == null)
                    return "The embedded " + label + " could not be opened.";

                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Trim();
            }
            catch (Exception ex)
            {
                return "The " + label + " could not be loaded.\r\n\r\n" + ex.Message;
            }
        }

        private Point ClampToCanvas(Point candidate)
        {
            return new Point(
                Math.Max(0, Math.Min(_canvas.ClientSize.Width - Width, candidate.X)),
                Math.Max(0, Math.Min(_canvas.ClientSize.Height - Height, candidate.Y)));
        }

        private static void DrawFallbackMascot(Graphics graphics, Rectangle bounds)
        {
            using var yellow = new SolidBrush(Color.FromArgb(255, 184, 35));
            using var black = new SolidBrush(Color.FromArgb(35, 35, 35));
            graphics.FillEllipse(yellow, bounds);
            graphics.FillEllipse(
                black,
                bounds.X + bounds.Width / 3,
                bounds.Y + bounds.Height / 4,
                bounds.Width / 3,
                bounds.Height / 2);
        }
    }
}
