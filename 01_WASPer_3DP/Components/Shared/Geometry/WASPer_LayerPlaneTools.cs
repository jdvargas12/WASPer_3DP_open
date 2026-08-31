// WASPer_LayerPlaneTools.cs
// Shared helpers for estimating layer planes from guide/path curves.
//
// Used by path-based infill and slicing components to keep la_planes/source
// frame behavior consistent across the plugin.

using System;
using System.Collections.Generic;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace WASPer_3DP
{
    internal static class WasperLayerPlaneTools
    {
        public static Plane EstimateLayerPlane(IList<Curve> curves, double tol)
        {
            if (curves == null || curves.Count == 0)
                return Plane.WorldXY;

            Curve first = curves[0];
            Curve last = curves[curves.Count - 1] ?? first;
            Point3d origin = PointAtNormalized(first, 0.5);

            Vector3d x = first.TangentAt(first.Domain.Mid);
            if (!x.IsValid || x.Length <= tol)
                x = PointAtNormalized(first, 1.0) - PointAtNormalized(first, 0.0);

            Vector3d y = PointAtNormalized(last, 0.5) - origin;
            if (!x.IsValid || x.Length <= tol || !y.IsValid || y.Length <= tol)
                return OrientPlaneNormalUp(FitLayerPlane(curves, origin));

            x.Unitize();
            y -= x * (y * x);
            if (!y.IsValid || y.Length <= tol)
                return OrientPlaneNormalUp(FitLayerPlane(curves, origin));

            y.Unitize();
            Plane plane = new Plane(origin, x, y);
            if (!plane.IsValid)
                return OrientPlaneNormalUp(FitLayerPlane(curves, origin));

            Plane fit = FitLayerPlane(curves, origin);
            if (fit.IsValid && fit.ZAxis.IsValid && Vector3d.Multiply(plane.ZAxis, fit.ZAxis) < 0.0)
            {
                y.Reverse();
                plane = new Plane(origin, x, y);
            }

            return OrientPlaneNormalUp(plane.IsValid ? plane : fit);
        }

        public static Plane EstimateLayerPlaneFromGhCurves(IList<GH_Curve> branch, double tol)
        {
            var curves = new List<Curve>();
            if (branch != null)
            {
                for (int i = 0; i < branch.Count; i++)
                {
                    Curve c = branch[i]?.Value;
                    if (c != null && c.IsValid)
                        curves.Add(c);
                }
            }

            return EstimateLayerPlane(curves, tol);
        }

        public static Plane EstimateLayerPlaneFromPathTreeLayer(GH_Structure<GH_Curve> tree, int layer, double tol)
        {
            var curves = new List<Curve>();
            if (tree != null)
            {
                for (int bi = 0; bi < tree.PathCount; bi++)
                {
                    GH_Path path = tree.Paths[bi];
                    if (LayerIdFromPath(path, bi) != layer) continue;
                    var branch = tree.get_Branch(path);
                    if (branch == null) continue;

                    foreach (object obj in branch)
                    {
                        var ghCurve = obj as GH_Curve;
                        Curve curve = ghCurve?.Value;
                        if (curve != null && curve.IsValid)
                            curves.Add(curve);
                    }
                }
            }

            return EstimateLayerPlane(curves, tol);
        }

        public static double MaxDeviationFromPlane(IList<Curve> curves, Plane plane)
        {
            if (curves == null || curves.Count == 0 || !plane.IsValid)
                return 0.0;

            double max = 0.0;
            for (int i = 0; i < curves.Count; i++)
            {
                Curve c = curves[i];
                if (c == null || !c.IsValid) continue;
                for (int j = 0; j <= 8; j++)
                {
                    Point3d p = PointAtNormalized(c, j / 8.0);
                    if (!p.IsValid) continue;
                    max = Math.Max(max, Math.Abs(plane.DistanceTo(p)));
                }
            }

            return max;
        }

        public static double MaxDeviationFromPlane(IList<GH_Curve> branch, Plane plane)
        {
            var curves = new List<Curve>();
            if (branch != null)
            {
                for (int i = 0; i < branch.Count; i++)
                {
                    Curve c = branch[i]?.Value;
                    if (c != null && c.IsValid)
                        curves.Add(c);
                }
            }

            return MaxDeviationFromPlane(curves, plane);
        }

        public static double PlanarityWarningTolerance(double docTol)
        {
            return Math.Max(Math.Max(docTol, 1e-6) * 10.0, 0.01);
        }

        public static int LayerIdFromPath(GH_Path path, int fallback)
        {
            return path != null && path.Length > 0 ? path.Indices[0] : fallback;
        }

        public static Point3d PointAtNormalized(Curve curve, double t01)
        {
            if (curve == null || !curve.IsValid) return Point3d.Unset;
            t01 = Math.Max(0.0, Math.Min(1.0, t01));
            double t;
            double len = curve.GetLength();
            if (len > 0.0 && curve.LengthParameter(len * t01, out t))
                return curve.PointAt(t);
            return curve.PointAt(curve.Domain.ParameterAt(t01));
        }

        private static Plane FitLayerPlane(IList<Curve> curves, Point3d fallbackOrigin)
        {
            var pts = new List<Point3d>();
            for (int i = 0; curves != null && i < curves.Count; i++)
            {
                Curve c = curves[i];
                if (c == null || !c.IsValid) continue;
                for (int j = 0; j <= 4; j++)
                    pts.Add(PointAtNormalized(c, j / 4.0));
            }

            Plane fit;
            if (pts.Count >= 3 && Plane.FitPlaneToPoints(pts, out fit) == PlaneFitResult.Success && fit.IsValid)
                return OrientPlaneNormalUp(fit);

            Plane fallback = Plane.WorldXY;
            fallback.Origin = fallbackOrigin.IsValid ? fallbackOrigin : Point3d.Origin;
            return fallback;
        }

        private static Plane OrientPlaneNormalUp(Plane plane)
        {
            if (!plane.IsValid || !plane.ZAxis.IsValid)
                return plane;

            if (Vector3d.Multiply(plane.ZAxis, Vector3d.ZAxis) < 0.0)
                plane.Flip();

            return plane;
        }
    }
}
