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
    internal sealed class KpiManagerAttributes : GH_ComponentAttributes
    {
        private const float LinkAnchorRadius = 7f;
        private RectangleF _button;
        private bool _pressed;

        private wsp_Sm01_WASPer_Study_Manager Component => Owner as wsp_Sm01_WASPer_Study_Manager;

        internal KpiManagerAttributes(wsp_Sm01_WASPer_Study_Manager owner)
            : base(owner)
        {
        }

        protected override void Layout()
        {
            base.Layout();
            Rectangle bounds = GH_Convert.ToRectangle(Bounds);
            _button = new RectangleF(bounds.X + 3, bounds.Bottom, bounds.Width - 6, 18);
            bounds.Height += 21;
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
                RenderSliderLinks(graphics);
                RenderKpiSourceLinks(graphics);
                RenderVisualizationLink(graphics);
                return;
            }
            if (channel != GH_CanvasChannel.Objects)
                return;

            RenderLinkAnchors(graphics);
            bool disabled = Owner.Locked || Component == null;
            using GH_Capsule capsule = GH_Capsule.CreateTextCapsule(
                _button,
                _button,
                GH_Palette.Black,
                "Open Study Manager",
                GH_FontServer.StandardAdjusted,
                3,
                _pressed ? 0 : 8);
            capsule.Render(graphics, false, disabled, false);
        }

        private void RenderLinkAnchors(Graphics graphics)
        {
            float centerX = Bounds.Left + (Bounds.Width * 0.5f);
            DrawHalfSphere(
                graphics,
                new PointF(centerX, Bounds.Top),
                true,
                Color.FromArgb(235, 184, 48, 190));
            DrawHalfSphere(
                graphics,
                new PointF(centerX, Bounds.Bottom),
                false,
                Color.FromArgb(230, 63, 112, 178));
        }

        private static void DrawHalfSphere(
            Graphics graphics,
            PointF center,
            bool top,
            Color fillColor)
        {
            RectangleF circle = new RectangleF(
                center.X - LinkAnchorRadius,
                center.Y - LinkAnchorRadius,
                LinkAnchorRadius * 2f,
                LinkAnchorRadius * 2f);
            using var path = new GraphicsPath();
            path.AddArc(circle, top ? 180f : 0f, 180f);
            path.CloseFigure();
            using var fill = new SolidBrush(fillColor);
            using var outline = new Pen(Color.FromArgb(230, 45, 48, 54), 1.4f);
            graphics.FillPath(fill, path);
            graphics.DrawPath(outline, path);
        }

        private void RenderSliderLinks(Graphics graphics)
        {
            IReadOnlyList<RectangleF> sliderBounds = Component?.LinkedSliderBounds();
            if (sliderBounds == null || sliderBounds.Count == 0)
                return;

            PointF target = BottomLinkTarget();
            using var pen = new Pen(Color.FromArgb(220, 221, 92, 32), 2.0f)
            {
                DashStyle = DashStyle.Dash,
                CustomEndCap = new AdjustableArrowCap(4.5f, 5.5f, true)
            };
            foreach (RectangleF bounds in sliderBounds)
            {
                DrawLinkBox(graphics, pen, bounds);
                DrawBottomLink(graphics, pen, bounds, target);
            }
        }

        private void RenderKpiSourceLinks(Graphics graphics)
        {
            IReadOnlyList<RectangleF> sourceBounds = Component?.LinkedKpiSourceBounds();
            if (sourceBounds == null || sourceBounds.Count == 0)
                return;

            PointF target = BottomLinkTarget();
            using var pen = new Pen(Color.FromArgb(220, 63, 112, 178), 2.0f)
            {
                DashStyle = DashStyle.Dot,
                CustomEndCap = new AdjustableArrowCap(4.5f, 5.5f, true)
            };
            foreach (RectangleF bounds in sourceBounds)
            {
                DrawLinkBox(graphics, pen, bounds);
                DrawBottomLink(graphics, pen, bounds, target);
            }
        }

        private void RenderVisualizationLink(Graphics graphics)
        {
            RectangleF? visualizationBounds = Component?.LinkedVisualizationBounds();
            if (!visualizationBounds.HasValue)
                return;

            RectangleF bounds = visualizationBounds.Value;
            PointF target = BottomLinkTarget();
            using var pen = new Pen(Color.FromArgb(225, 0, 145, 150), 2.2f)
            {
                DashStyle = DashStyle.DashDot,
                CustomEndCap = new AdjustableArrowCap(4.5f, 5.5f, true)
            };
            DrawLinkBox(graphics, pen, bounds);
            DrawBottomLink(graphics, pen, bounds, target);
        }

        /// <summary>
        /// Dashed outline around a linked slider, KPI source, or visualization component, in the
        /// same color and dash style as the arrow connecting it back to Sm01. Makes every linked
        /// object identifiable at a glance, not just the arrow pointing to it. A rectangle is a
        /// closed figure, so the pen's arrow CustomEndCap (only meaningful on open paths) is
        /// simply ignored here - reusing the same Pen as the arrow is safe.
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
                eventArgs.Button == MouseButtons.Left &&
                _button.Contains(eventArgs.CanvasLocation))
            {
                _pressed = true;
                sender.Invalidate();
                return GH_ObjectResponse.Capture;
            }
            return base.RespondToMouseDown(sender, eventArgs);
        }

        public override GH_ObjectResponse RespondToMouseUp(
            GH_Canvas sender,
            GH_CanvasMouseEvent eventArgs)
        {
            if (!_pressed)
                return base.RespondToMouseUp(sender, eventArgs);
            _pressed = false;
            sender.Invalidate();
            if (_button.Contains(eventArgs.CanvasLocation))
                Component?.ShowManager();
            return GH_ObjectResponse.Release;
        }
    }
}
