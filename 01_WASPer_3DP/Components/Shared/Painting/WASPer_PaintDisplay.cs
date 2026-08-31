using System;
using System.Collections.Generic;
using System.Drawing;

using Rhino;
using Rhino.Display;
using Rhino.Geometry;

namespace WASPer_3DP.Painting
{
    internal sealed class WasperPaintConduit : DisplayConduit
    {
        public Mesh PreviewMesh;
        public IList<WasperPaintMarker> AtlasMarkers;
        public IList<WasperPaintMarker> ReferenceMarkers;
        public bool ShowField;
        public bool ShowReferences;
        public bool HasHit;
        public Point3d HitPoint;
        public Vector3d HitNormal;
        public double Radius;
        public WasperPaintTool Tool;
        public Color? ToolColorOverride;
        public Func<bool> IsActiveDocument;

        private bool CanDraw => IsActiveDocument == null || IsActiveDocument();

        protected override void CalculateBoundingBox(CalculateBoundingBoxEventArgs e)
        {
            if (!CanDraw)
                return;
            if (PreviewMesh != null)
                e.IncludeBoundingBox(PreviewMesh.GetBoundingBox(false));
            IncludeMarkers(e, AtlasMarkers);
            IncludeMarkers(e, ReferenceMarkers);
        }

        protected override void PostDrawObjects(DrawEventArgs e)
        {
            if (!CanDraw)
                return;
            if (ShowField && PreviewMesh != null &&
                PreviewMesh.VertexColors.Count == PreviewMesh.Vertices.Count)
                e.Display.DrawMeshFalseColors(PreviewMesh);
            if (ShowField)
                DrawMarkers(e, AtlasMarkers);
            if (ShowReferences)
                DrawMarkers(e, ReferenceMarkers);
        }

        protected override void DrawForeground(DrawEventArgs e)
        {
            if (!CanDraw)
                return;
            if (!HasHit || !HitPoint.IsValid || Radius <= RhinoMath.ZeroTolerance)
                return;
            Vector3d normal = HitNormal;
            if (!normal.Unitize())
                normal = Vector3d.ZAxis;
            Color brushColor = ToolColorOverride ?? WasperPaintColors.ForTool(Tool);
            var circle = new Circle(new Plane(HitPoint, normal), Radius);
            e.Display.DrawCircle(circle, Color.FromArgb(220, 25, 25, 25), 7);
            e.Display.DrawCircle(circle, brushColor, 4);
            e.Display.DrawPoint(HitPoint, PointStyle.RoundControlPoint, 6, brushColor);
        }

        private static void IncludeMarkers(
            CalculateBoundingBoxEventArgs e,
            IEnumerable<WasperPaintMarker> markers)
        {
            if (markers == null)
                return;
            foreach (WasperPaintMarker marker in markers)
                e.IncludeBoundingBox(new BoundingBox(new[] { marker.Line.From, marker.Line.To }));
        }

        private static void DrawMarkers(DrawEventArgs e, IEnumerable<WasperPaintMarker> markers)
        {
            if (markers == null)
                return;
            foreach (WasperPaintMarker marker in markers)
            {
                if (marker.Thickness >= 2 && marker.Color.A >= 200)
                {
                    e.Display.DrawLine(
                        marker.Line,
                        Color.FromArgb(210, 20, 22, 28),
                        marker.Thickness + 3);
                }
                e.Display.DrawLine(marker.Line, marker.Color, marker.Thickness);
            }
        }
    }

    internal static class WasperPaintColors
    {
        internal static readonly Color Neutral = Color.FromArgb(55, 105, 235);
        internal static readonly Color Pushed = Color.FromArgb(35, 205, 85);
        internal static readonly Color Pulled = Color.FromArgb(235, 50, 45);

        internal static Color ForTool(WasperPaintTool tool)
        {
            return tool == WasperPaintTool.Push
                ? Pushed
                : tool == WasperPaintTool.Pull
                    ? Pulled
                    : tool == WasperPaintTool.Zero
                        ? Color.White
                    : tool == WasperPaintTool.Smooth
                        ? Color.Gold
                        : Color.FromArgb(85, 125, 220);
        }

        internal static Color ForValue(double value, Interval domain)
        {
            double min = System.Math.Min(domain.T0, domain.T1);
            double max = System.Math.Max(domain.T0, domain.T1);
            if (value < 0.0 && min < 0.0)
                return Lerp(Neutral, Pushed, value / min);
            if (value > 0.0 && max > 0.0)
                return Lerp(Neutral, Pulled, value / max);
            return Neutral;
        }

        private static Color Lerp(Color from, Color to, double t)
        {
            double clamped = System.Math.Max(0.0, System.Math.Min(1.0, t));
            return Color.FromArgb(
                255,
                (int)System.Math.Round(from.R + (to.R - from.R) * clamped),
                (int)System.Math.Round(from.G + (to.G - from.G) * clamped),
                (int)System.Math.Round(from.B + (to.B - from.B) * clamped));
        }
    }
}
