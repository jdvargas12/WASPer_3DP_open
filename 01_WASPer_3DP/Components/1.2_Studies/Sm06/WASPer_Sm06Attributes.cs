using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;

namespace WASPer_3DP.Components._1_2_Studies
{
    /// <summary>
    /// Three capsule buttons under the component, in the same visual language as Sm01's "Open Study
    /// Manager": register, prepare, remove. While the component is selected, registered controls and
    /// the contextual parameters it manages are outlined on the canvas and linked back to it, so
    /// what the component owns is visible before any button is pressed.
    /// </summary>
    internal sealed class WasperSm06Attributes : GH_ComponentAttributes
    {
        private const float LinkAnchorRadius = 7f;
        private const float ButtonHeight = 18f;
        private const float ButtonSpacing = 3f;

        private RectangleF _linkButton;
        private RectangleF _prepareButton;
        private RectangleF _removeButton;
        private int _pressed = -1;

        private wsp_Sm06_Interface_Input_Builder Component =>
            Owner as wsp_Sm06_Interface_Input_Builder;

        internal WasperSm06Attributes(wsp_Sm06_Interface_Input_Builder owner)
            : base(owner)
        {
        }

        protected override void Layout()
        {
            base.Layout();
            Rectangle bounds = GH_Convert.ToRectangle(Bounds);
            float width = bounds.Width - 6;
            float top = bounds.Bottom;

            _linkButton = new RectangleF(bounds.X + 3, top, width, ButtonHeight);
            _prepareButton = new RectangleF(
                bounds.X + 3,
                top + ButtonHeight + ButtonSpacing,
                width,
                ButtonHeight);
            _removeButton = new RectangleF(
                bounds.X + 3,
                top + ((ButtonHeight + ButtonSpacing) * 2f),
                width,
                ButtonHeight);

            bounds.Height += (int)Math.Ceiling((ButtonHeight * 3f) + (ButtonSpacing * 2f) + 3f);
            Bounds = bounds;
        }

        protected override void Render(
            GH_Canvas canvas,
            Graphics graphics,
            GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);

            if (channel == GH_CanvasChannel.Wires && Selected)
            {
                RenderRegisteredControlLinks(graphics);
                RenderManagedParameterLinks(graphics);
                return;
            }
            if (channel != GH_CanvasChannel.Objects)
                return;

            RenderLinkAnchor(graphics);

            bool disabled = Owner.Locked || Component == null;
            DrawButton(graphics, _linkButton, "Link selected controls", disabled, 0);
            DrawButton(
                graphics,
                _prepareButton,
                Component == null || Component.RegisteredCount == 0
                    ? "Prepare inputs"
                    : $"Prepare inputs ({Component.RegisteredCount})",
                disabled || Component == null || Component.RegisteredCount == 0,
                1);
            DrawButton(
                graphics,
                _removeButton,
                Component == null || Component.ManagedCount == 0
                    ? "Remove Get inputs"
                    : $"Remove Get inputs ({Component.ManagedCount})",
                disabled || Component == null || Component.ManagedCount == 0,
                2);
        }

        private void DrawButton(
            Graphics graphics,
            RectangleF area,
            string caption,
            bool disabled,
            int index)
        {
            using GH_Capsule capsule = GH_Capsule.CreateTextCapsule(
                area,
                area,
                GH_Palette.Black,
                caption,
                GH_FontServer.StandardAdjusted,
                3,
                _pressed == index ? 0 : 8);
            capsule.Render(graphics, false, disabled, false);
        }

        private void RenderLinkAnchor(Graphics graphics)
        {
            float centerX = Bounds.Left + (Bounds.Width * 0.5f);
            RectangleF circle = new RectangleF(
                centerX - LinkAnchorRadius,
                Bounds.Bottom - LinkAnchorRadius,
                LinkAnchorRadius * 2f,
                LinkAnchorRadius * 2f);
            using var path = new GraphicsPath();
            path.AddArc(circle, 0f, 180f);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(230, 221, 92, 32));
            using var outline = new Pen(Color.FromArgb(230, 45, 48, 54), 1.4f);
            graphics.FillPath(fill, path);
            graphics.DrawPath(outline, path);
        }

        /// <summary>Registered controls: dashed, in the Sm01 slider-link orange.</summary>
        private void RenderRegisteredControlLinks(Graphics graphics)
        {
            IReadOnlyList<RectangleF> bounds = Component?.LinkedControlBounds();
            if (bounds == null || bounds.Count == 0)
                return;

            PointF target = BottomLinkTarget();
            using var pen = new Pen(Color.FromArgb(220, 221, 92, 32), 2.0f)
            {
                DashStyle = DashStyle.Dash,
                CustomEndCap = new AdjustableArrowCap(4.5f, 5.5f, true)
            };
            foreach (RectangleF box in bounds)
            {
                DrawLinkBox(graphics, pen, box);
                DrawBottomLink(graphics, pen, box, target);
            }
        }

        /// <summary>Contextual parameters this component owns: dash-dot, in the Selva teal.</summary>
        private void RenderManagedParameterLinks(Graphics graphics)
        {
            IReadOnlyList<RectangleF> bounds = Component?.ManagedContextualBounds();
            if (bounds == null || bounds.Count == 0)
                return;

            PointF target = BottomLinkTarget();
            using var pen = new Pen(Color.FromArgb(225, 0, 145, 150), 2.2f)
            {
                DashStyle = DashStyle.DashDot,
                CustomEndCap = new AdjustableArrowCap(4.5f, 5.5f, true)
            };
            foreach (RectangleF box in bounds)
            {
                DrawLinkBox(graphics, pen, box);
                DrawBottomLink(graphics, pen, box, target);
            }
        }

        /// <summary>
        /// Dashed outline around a linked object, matching the arrow that connects it back to the
        /// component. A rectangle is a closed figure, so the pen's arrow cap is ignored here and the
        /// same Pen can be reused for both.
        /// </summary>
        private static void DrawLinkBox(Graphics graphics, Pen pen, RectangleF bounds)
        {
            RectangleF box = RectangleF.Inflate(bounds, 4f, 4f);
            graphics.DrawRectangle(pen, box.X, box.Y, box.Width, box.Height);
        }

        private PointF BottomLinkTarget() => new PointF(
            Bounds.Left + (Bounds.Width * 0.5f),
            Bounds.Bottom + LinkAnchorRadius);

        private static void DrawBottomLink(
            Graphics graphics,
            Pen pen,
            RectangleF sourceBounds,
            PointF target)
        {
            bool sourceIsLeft = sourceBounds.Left + (sourceBounds.Width * 0.5f) < target.X;
            PointF source = new PointF(
                sourceIsLeft ? sourceBounds.Right : sourceBounds.Left,
                sourceBounds.Top + (sourceBounds.Height * 0.5f));
            float horizontalBend = Math.Max(40f, Math.Abs(target.X - source.X) * 0.35f);
            float verticalBend = Math.Max(38f, Math.Abs(target.Y - source.Y) * 0.28f);
            float direction = sourceIsLeft ? 1f : -1f;
            graphics.DrawBezier(
                pen,
                source,
                new PointF(source.X + (horizontalBend * direction), source.Y),
                new PointF(target.X, target.Y + verticalBend),
                target);
        }

        public override GH_ObjectResponse RespondToMouseDown(
            GH_Canvas sender,
            GH_CanvasMouseEvent eventArgs)
        {
            if (!Owner.Locked &&
                Component != null &&
                eventArgs.Button == MouseButtons.Left)
            {
                int index = HitTest(eventArgs.CanvasLocation);
                if (index >= 0)
                {
                    _pressed = index;
                    sender.Invalidate();
                    return GH_ObjectResponse.Capture;
                }
            }
            return base.RespondToMouseDown(sender, eventArgs);
        }

        public override GH_ObjectResponse RespondToMouseUp(
            GH_Canvas sender,
            GH_CanvasMouseEvent eventArgs)
        {
            if (_pressed < 0)
                return base.RespondToMouseUp(sender, eventArgs);

            int pressed = _pressed;
            _pressed = -1;
            sender.Invalidate();

            if (HitTest(eventArgs.CanvasLocation) != pressed || Component == null)
                return GH_ObjectResponse.Release;

            switch (pressed)
            {
                case 0:
                    Component.LinkSelectedControls();
                    break;
                case 1:
                    Component.ShowPreparationPreview();
                    break;
                case 2:
                    Component.ShowRemovalPreview();
                    break;
            }
            return GH_ObjectResponse.Release;
        }

        private int HitTest(PointF location)
        {
            if (_linkButton.Contains(location))
                return 0;
            if (_prepareButton.Contains(location))
                return 1;
            if (_removeButton.Contains(location))
                return 2;
            return -1;
        }
    }
}
