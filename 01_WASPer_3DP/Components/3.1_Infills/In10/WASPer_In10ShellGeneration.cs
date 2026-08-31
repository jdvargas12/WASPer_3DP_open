using System;
using System.Collections.Generic;
using System.Linq;
using Rhino.Geometry;
using WASPer_3DP.PatternEditing;

namespace WASPer_3DP.Components._3_1_Infills
{
    public partial class wsp_In10_Layered_Multi_Infill_From_Curves
    {
        private static List<PolylineCurve> CloseShellPairs(List<PolylineCurve> side0, List<PolylineCurve> sideN, double tol)
        {
            var result = new List<PolylineCurve>();
            int n = Math.Min(side0.Count, sideN.Count);
            for (int i = 0; i < n; i++)
            {
                var poly0 = side0[i].ToPolyline();
                var polyN = sideN[i].ToPolyline();
                if (poly0 == null || polyN == null) continue;
                var pts0 = new List<Point3d>(poly0);
                var ptsN = new List<Point3d>(polyN);
                if (pts0.Count < 2 || ptsN.Count < 2) continue;

                bool reverseN = pts0[pts0.Count - 1].DistanceTo(ptsN[ptsN.Count - 1]) <=
                                pts0[pts0.Count - 1].DistanceTo(ptsN[0]);

                var combined = new List<Point3d>(pts0.Count + ptsN.Count + 1);
                combined.AddRange(pts0);
                if (reverseN)
                    for (int k = ptsN.Count - 1; k >= 0; k--) combined.Add(ptsN[k]);
                else
                    combined.AddRange(ptsN);
                combined.Add(pts0[0]);

                if (combined.Count >= 3)
                    result.Add(new PolylineCurve(combined));
            }
            return result;
        }

        private static PolylineCurve EnsureClosedShellPath(
            PolylineCurve source,
            double tolerance)
        {
            if (source == null || !source.IsValid)
                return source;
            Polyline polyline = source.ToPolyline();
            if (polyline == null || polyline.Count < 2)
                return source;
            if (polyline[0].DistanceTo(polyline[polyline.Count - 1]) > tolerance)
                polyline.Add(polyline[0]);
            else
                polyline[polyline.Count - 1] = polyline[0];
            return new PolylineCurve(polyline);
        }

        private static PolylineCurve TryApplyShellSeam(
            PolylineCurve source,
            WasperShellSeamSettings settings,
            Plane layerPlane,
            double tol)
        {
            if (source == null || !source.IsValid)
                return null;

            // Keep untouched seam settings as a strict no-op. For edited settings,
            // normalize a sampled loop whose endpoints are within document tolerance
            // before handing it to the shared authoritative seam implementation. Rhino
            // can otherwise report a visually closed sampled shell as open and silently
            // bypass X-seam generation.
            if (settings == null ||
                (!settings.XSeam && Math.Abs(Wrap01(settings.SeamU)) <= 1e-12))
                return new PolylineCurve(source.ToPolyline());

            Polyline canonical = source.ToPolyline();
            if (canonical == null || canonical.Count < 2)
                return new PolylineCurve(source.ToPolyline());
            double closureTolerance = Math.Max(1e-9, tol);
            bool effectivelyClosed = source.IsClosed ||
                canonical[0].DistanceTo(canonical[canonical.Count - 1]) <= closureTolerance;
            if (!effectivelyClosed)
                return new PolylineCurve(canonical);
            canonical[canonical.Count - 1] = canonical[0];
            var canonicalCurve = new PolylineCurve(canonical);

            try
            {
                PolylineCurve edited = WasperShellSeamMetadata.Apply(
                    canonicalCurve,
                    settings,
                    layerPlane,
                    closureTolerance);
                return edited != null && edited.IsValid
                    ? edited
                    : new PolylineCurve(canonical);
            }
            catch
            {
                // Seam editing must never invalidate the base shell solve. Preserve
                // the original path if Rhino rejects a seam operation on this curve.
                return new PolylineCurve(canonical);
            }
        }

        private static List<PolylineCurve> BuildShellPaths(
            Curve outerGuide, Curve innerGuide,
            int nShell, double wShell,
            double sampleRes, double tol,
            bool insetEnds = false)
        {
            var result = new List<PolylineCurve>();
            if (outerGuide == null || innerGuide == null) return result;
            if (!outerGuide.IsValid || !innerGuide.IsValid) return result;
            if (nShell < 1 || wShell <= tol) return result;

            double lenO = outerGuide.GetLength();
            double lenI = innerGuide.GetLength();
            if (lenO <= tol || lenI <= tol) return result;
            int    n    = ShellSampleCount(lenO, lenI, sampleRes, tol);

            for (int si = 1; si <= nShell; si++)
            {
                double offset = (si - 0.5) * wShell;
                var pts = new List<Point3d>(n);

                for (int j = 0; j < n; j++)
                {
                    double u = (n == 1) ? 0.5 : (double)j / (double)(n - 1);

                    double sO = u * lenO;
                    double sI = u * lenI;
                    if (insetEnds)
                    {
                        double endO = Math.Min(offset, Math.Max(0.0, 0.5 * lenO - tol));
                        double endI = Math.Min(offset, Math.Max(0.0, 0.5 * lenI - tol));
                        sO = endO + u * Math.Max(0.0, lenO - 2.0 * endO);
                        sI = endI + u * Math.Max(0.0, lenI - 2.0 * endI);
                    }

                    double tO;
                    if (!outerGuide.LengthParameter(sO, out tO))
                        tO = outerGuide.Domain.ParameterAt(u);
                    Point3d pO = outerGuide.PointAt(tO);

                    double tI;
                    if (!innerGuide.LengthParameter(sI, out tI))
                        tI = innerGuide.Domain.ParameterAt(u);
                    Point3d pI = innerGuide.PointAt(tI);

                    Vector3d vOI = pI - pO;
                    double   gap = vOI.Length;
                    if (gap <= tol) { pts.Add(pO); continue; }
                    vOI.Unitize();

                    pts.Add(pO + vOI * Math.Min(offset, gap));
                }

                if (pts.Count >= 2)
                    result.Add(new PolylineCurve(new Polyline(pts)));
            }

            return result;
        }

        private static int ShellSampleCount(double lenA, double lenB, double sampleRes, double tol)
        {
            double len = Math.Max(lenA, lenB);
            if (len <= tol) return 2;

            double res = sampleRes;
            if (double.IsNaN(res) || double.IsInfinity(res) || res <= tol)
                res = Math.Max(tol * 10.0, len / 63.0);

            return Math.Max(8, (int)Math.Ceiling(len / res) + 1);
        }

        private static void EnsureListDefault<T>(List<T> list, T defaultValue)
        {
            if (list != null && list.Count == 0) list.Add(defaultValue);
        }

    }
}
