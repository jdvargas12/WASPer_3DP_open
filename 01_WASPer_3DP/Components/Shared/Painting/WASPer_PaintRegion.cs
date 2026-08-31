using System;
using System.Collections.Generic;

using Rhino.Geometry;

namespace WASPer_3DP.Painting
{
    internal static class WasperPaintRegion
    {
        internal static bool Contains(
            Point3d point,
            IList<Point3d> boundary,
            Plane plane)
        {
            if (boundary == null || boundary.Count < 3)
                return false;
            Vector3d pointDelta = point - plane.Origin;
            double x = pointDelta * plane.XAxis;
            double y = pointDelta * plane.YAxis;
            bool inside = false;
            for (int i = 0, j = boundary.Count - 1; i < boundary.Count; j = i++)
            {
                Vector3d aDelta = boundary[i] - plane.Origin;
                Vector3d bDelta = boundary[j] - plane.Origin;
                double ax = aDelta * plane.XAxis;
                double ay = aDelta * plane.YAxis;
                double bx = bDelta * plane.XAxis;
                double by = bDelta * plane.YAxis;
                bool crosses = (ay > y) != (by > y) &&
                    x < (bx - ax) * (y - ay) / (by - ay) + ax;
                if (crosses)
                    inside = !inside;
            }
            return inside;
        }
    }
}
