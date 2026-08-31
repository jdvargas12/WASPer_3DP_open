using System;
using System.Collections.Generic;

using Rhino.Geometry;

namespace WASPer_3DP.Painting
{
    internal sealed class WasperPaintMaskRegion
    {
        private readonly Func<Point3d, bool> _contains;

        private WasperPaintMaskRegion(Func<Point3d, bool> contains)
        {
            _contains = contains;
        }

        internal bool Contains(Point3d point) => _contains(point);

        internal static List<WasperPaintMaskRegion> Build(
            IEnumerable<GeometryBase> geometry,
            double tolerance,
            out int rejected)
        {
            rejected = 0;
            var result = new List<WasperPaintMaskRegion>();
            if (geometry == null)
                return result;

            foreach (GeometryBase item in geometry)
            {
                WasperPaintMaskRegion region = Create(item, tolerance);
                if (region == null)
                    rejected++;
                else
                    result.Add(region);
            }
            return result;
        }

        private static WasperPaintMaskRegion Create(GeometryBase geometry, double tolerance)
        {
            if (geometry == null || !geometry.IsValid)
                return null;
            if (geometry is Curve curve && curve.IsClosed &&
                curve.TryGetPlane(out Plane plane, tolerance))
            {
                return new WasperPaintMaskRegion(point =>
                {
                    PointContainment containment = curve.Contains(point, plane, tolerance);
                    return containment == PointContainment.Inside ||
                           containment == PointContainment.Coincident;
                });
            }
            if (geometry is Brep brep && brep.IsSolid)
                return new WasperPaintMaskRegion(point => brep.IsPointInside(point, tolerance, true));
            if (geometry is Extrusion extrusion && extrusion.IsSolid)
            {
                Brep extrusionBrep = extrusion.ToBrep();
                return extrusionBrep == null
                    ? null
                    : new WasperPaintMaskRegion(point =>
                        extrusionBrep.IsPointInside(point, tolerance, true));
            }
            if (geometry is Mesh mesh && mesh.IsClosed)
                return new WasperPaintMaskRegion(point => mesh.IsPointInside(point, tolerance, true));
            return null;
        }
    }
}
