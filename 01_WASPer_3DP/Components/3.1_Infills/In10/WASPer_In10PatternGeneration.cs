using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Grasshopper.Kernel.Data;
using Rhino.Geometry;

namespace WASPer_3DP.Components._3_1_Infills
{
    public partial class wsp_In10_Layered_Multi_Infill_From_Curves
    {
        private struct TurtleCellGeometry
        {
            public double CellStart, CellEnd;
            public double TopLeft, TopRight, BottomLeft, BottomRight, TransitionOffset;
        }

        private static int GenerateInfill2DDomain(
            WasperInfill2DParams settings,
            Curve cA,
            Curve cB,
            double lenA,
            double lenB,
            double insetA,
            double insetB,
            double sampleSpacing,
            double tol,
            double eps,
            bool trimLayers,
            GH_Path layerPath,
            GH_Path branchPath,
            int domainIndex,
            List<(GH_Path path, PolylineCurve crv)> curves,
            List<(GH_Path path, Point3d pt)> points,
            ref double printArea,
            StringBuilder log,
            IReadOnlyList<double> guideSourceA,
            IReadOnlyList<double> guideWarpA,
            IReadOnlyList<double> guideSourceB,
            IReadOnlyList<double> guideWarpB)
        {
            int domType = Clamp(settings.Type, 1, 4);
            bool doFlip = settings.Flip;
            int domCount = Math.Max(1, settings.Count);
            double domPhase = Wrap01(settings.PhaseShift);
            double domRes = EffectiveRes(sampleSpacing, lenA, lenB, tol);

            Point3d BlendPoint(double s01, double yN)
            {
                Point3d pA = PointAtNormalizedLength(cA, lenA, MapGuideWarp(guideSourceA, guideWarpA, s01), tol);
                Point3d pB = PointAtNormalizedLength(cB, lenB, MapGuideWarp(guideSourceB, guideWarpB, s01), tol);

                Vector3d vAB = pB - pA;
                double gap = vAB.Length;
                if (gap <= tol)
                    return pA;

                vAB.Unitize();
                double maxInset = Math.Max(0.0, gap * 0.5 - eps);
                double ia = Math.Min(insetA, maxInset);
                double ib = Math.Min(insetB, maxInset);
                Point3d pAin = pA + vAB * ia;
                double usable = Math.Max(0.0, gap - ia - ib);
                return pAin + vAB * (yN * usable);
            }

            int made = 0;

            if (domType == 2)
            {
                for (int si = 0; si < domCount; si++)
                {
                    double sCentre = (((si + 0.5 + (doFlip ? -domPhase : domPhase)) % domCount) + domCount) % domCount / domCount;
                    Point3d stPtA = BlendPoint(sCentre, doFlip ? 1.0 : 0.0);
                    Point3d stPtB = BlendPoint(sCentre, doFlip ? 0.0 : 1.0);
                    if (stPtA.DistanceTo(stPtB) <= tol)
                        continue;

                    var stickPl = new Polyline(new[] { stPtA, stPtB });
                    if (!stickPl.IsValid || stickPl.Length <= tol)
                        continue;

                    var stickCrv = new PolylineCurve(stickPl);
                    if (stickCrv == null || !stickCrv.IsValid)
                        continue;

                    GH_Path stickPath = trimLayers ? layerPath : AppendDomainPath(branchPath, domainIndex, si);
                    curves.Add((stickPath, stickCrv));
                    points.Add((stickPath, stPtA));
                    points.Add((stickPath, stPtB));
                    printArea += stPtA.DistanceTo(stPtB) * domRes;
                    made++;
                }

                log.AppendLine(
                    $"Branch {branchPath} domain [{domainIndex}] (2D): type={WasperInfill2DParams.Tag(domType)} " +
                    $"count={domCount} phase={domPhase:0.###} flip={doFlip} res={domRes:0.###} -> {made} path(s).");
                return made;
            }

            double avgCellLength = 0.5 * (lenA + lenB) / Math.Max(1, domCount);
            int samplesPerCell = Math.Max(2, (int)Math.Ceiling(avgCellLength / Math.Max(domRes, tol * 10.0)));
            int nSamples = Math.Max(2, domCount * samplesPerCell + 1);
            var samplePositions = new List<double>(nSamples + 2 * domCount + 6);

            void AddSamplePosition(double value)
            {
                if (value < -1e-10 || value > 1.0 + 1e-10)
                    return;
                value = Math.Max(0.0, Math.Min(1.0, value));
                if (!samplePositions.Any(existing => Math.Abs(existing - value) <= 1e-10))
                    samplePositions.Add(value);
            }

            // Triangle struts must remain straight after guide edits. For triangles,
            // map only the true vertices (plus clipped domain ends) and let the
            // Polyline connect them directly. Sampling intermediate points would
            // independently warp those points and bend an otherwise rigid strut.
            if (domType != 3)
            {
                for (int si = 0; si < nSamples; si++)
                    AddSamplePosition((double)si / (double)(nSamples - 1));
            }
            else
            {
                AddSamplePosition(0.0);
                AddSamplePosition(1.0);
            }

            // Resolution controls intermediate subdivision only. Always inject the exact
            // phase landmarks where Square-S changes side and Triangle/Sine reach a guide.
            double signedPhase = doFlip ? -domPhase : domPhase;
            for (int cell = -2; cell <= domCount + 2; cell++)
            {
                AddSamplePosition((cell - signedPhase) / domCount);
                AddSamplePosition((cell + 0.5 - signedPhase) / domCount);
            }
            samplePositions.Sort();

            var polyPts = new List<Point3d>(samplePositions.Count + 4);
            foreach (double s01 in samplePositions)
            {
                double patternPos = (((domCount * s01 + signedPhase) % domCount) + domCount) % domCount;
                double cellT = patternPos - Math.Floor(patternPos);
                if (cellT <= 1e-10 || cellT >= 1.0 - 1e-10)
                    cellT = 0.0;
                else if (Math.Abs(cellT - 0.5) <= 1e-10)
                    cellT = 0.5;
                double yN = ShapeValue2D(domType, cellT);
                if (doFlip) yN = 1.0 - yN;
                polyPts.Add(BlendPoint(s01, yN));
            }

            if (!TryMakeValidPolyline(polyPts, tol, out Polyline pl))
            {
                log.AppendLine($"Branch {branchPath} domain [{domainIndex}] (2D): could not build valid polyline.");
                return 0;
            }

            pl.CollapseShortSegments(tol);
            if (!pl.IsValid || pl.Count < 2 || pl.Length <= tol)
                return 0;

            var plc = new PolylineCurve(pl);
            if (plc == null || !plc.IsValid)
                return 0;

            GH_Path domPath = trimLayers ? layerPath : AppendDomainPath(branchPath, domainIndex, -1);
            curves.Add((domPath, plc));
            for (int i = 0; i < pl.Count; i++)
                points.Add((domPath, pl[i]));
            printArea += pl.Length * domRes;
            made = 1;

            log.AppendLine(
                $"Branch {branchPath} domain [{domainIndex}] (2D): type={WasperInfill2DParams.Tag(domType)} " +
                $"count={domCount} phase={domPhase:0.###} flip={doFlip} samples={samplePositions.Count} " +
                $"semantic_events={2 * domCount} res={domRes:0.###}" +
                (domType == 3 ? " rigid_triangle_struts=true" : string.Empty) +
                $" -> {made} path(s).");
            return made;
        }

        private static int GenerateTurtleDomain(
            WasperTurtleInfillParams settings,
            Curve cA,
            Curve cB,
            double lenA,
            double lenB,
            double insetA,
            double insetB,
            double layerPosition,
            int layerCount,
            double tol,
            double eps,
            bool trimLayers,
            GH_Path layerPath,
            GH_Path branchPath,
            int domainIndex,
            List<(GH_Path path, PolylineCurve crv)> curves,
            List<(GH_Path path, Point3d pt)> points,
            ref double printArea,
            StringBuilder log,
            IReadOnlyList<double> guideSourceA,
            IReadOnlyList<double> guideWarpA,
            IReadOnlyList<double> guideSourceB,
            IReadOnlyList<double> guideWarpB)
        {
            double pathWidth = settings.PathWidth;
            int countX = settings.CountX;
            int countY = settings.CountY;
            double countZ = settings.CountZ;
            double bridge0 = settings.Bridge0;
            double bridge1 = settings.Bridge1;
            double extend = settings.ExtendEnds;
            bool teeth = settings.Teeth;
            double bandInset = pathWidth * 0.5;

            double wave = TriangleWave01(layerPosition, countZ);
            double bridge = layerCount <= 1 ? bridge0 : Lerp(bridge0, bridge1, wave);
            bridge = Clamp01(bridge);

            const double zeroTolerance = 1e-12;
            bool bridge0IsZero = Math.Abs(bridge0) <= zeroTolerance;
            bool bridge1IsZero = Math.Abs(bridge1) <= zeroTolerance;
            bool strictZero = layerCount <= 1
                ? bridge0IsZero || bridge1IsZero
                : (bridge0IsZero && bridge1IsZero && Math.Abs(bridge) <= zeroTolerance)
                  || (bridge0IsZero && !bridge1IsZero && Math.Abs(wave) <= zeroTolerance)
                  || (!bridge0IsZero && bridge1IsZero && Math.Abs(1.0 - wave) <= zeroTolerance);

            double armNominal = pathWidth * 1.4;
            double bridgeNominal = pathWidth * 1.1;
            double bridgeMinimum = pathWidth * 1.1;
            double closedNominal = 2.0 * armNominal + bridgeNominal;

            double averageGap = EstimateAverageGap(cA, cB, lenA, lenB);
            double insetGap = Math.Max(0.0, averageGap - insetA - insetB);
            double bandHeight = insetGap / Math.Max(1, countY);
            double amplitude = 0.0;
            for (int bandIndex = 0; bandIndex < countY; bandIndex++)
            {
                // The shared cleared guides already represent clear_guide/clear_in.
                // Do not add p_width/2 again at the two OUTER domain boundaries.
                // Retain it only at internal band boundaries and between the paired
                // Turtle paths so their printed widths do not overlap.
                double lowerInset = bandIndex == 0 ? 0.0 : bandInset;
                double upperInset = bandIndex == countY - 1 ? 0.0 : bandInset;
                double localBandGap = Math.Max(
                    0.0,
                    bandHeight - lowerInset - upperInset);
                amplitude = Math.Max(
                    amplitude,
                    Math.Max(0.0, localBandGap * 0.5 - bandInset));
            }

            int denominator = 4 * countX - 1;
            double strictClosed = denominator > 0 ? lenA / denominator : closedNominal;
            strictClosed = Math.Max(0.0, strictClosed);

            double maxArmForSpacing = double.MaxValue;
            if (amplitude > tol && strictClosed > tol)
            {
                double hypotenuse = Math.Sqrt(strictClosed * strictClosed + amplitude * amplitude);
                if (hypotenuse > tol)
                {
                    double sine = amplitude / hypotenuse;
                    if (sine > tol) maxArmForSpacing = pathWidth / sine;
                }
            }

            double maxArmForBridge = Math.Max(0.0, 0.5 * (strictClosed - bridgeMinimum));
            double strictArm = Math.Min(armNominal, Math.Max(0.0, 0.5 * strictClosed));
            strictArm = Math.Min(strictArm, maxArmForSpacing);
            if (strictClosed >= bridgeMinimum - tol)
                strictArm = Math.Min(strictArm, maxArmForBridge);
            strictArm = Math.Max(0.0, strictArm);

            double blend;
            if (bridge0IsZero && bridge1IsZero) blend = 0.0;
            else if (bridge0IsZero) blend = wave;
            else if (bridge1IsZero) blend = 1.0 - wave;
            else blend = 1.0;
            blend = Clamp01(blend);
            blend = blend * blend * (3.0 - 2.0 * blend);
            if (strictZero) blend = 0.0;

            double closedWidth = Lerp(strictClosed, closedNominal, blend);
            double armWidth = Lerp(strictArm, armNominal, blend);
            double bridgeWidth = Math.Max(0.0, closedWidth - 2.0 * armWidth);
            if (bridgeWidth < bridgeMinimum - tol && closedWidth >= bridgeMinimum - tol)
            {
                bridgeWidth = bridgeMinimum;
                armWidth = Math.Max(0.0, 0.5 * (closedWidth - bridgeWidth));
            }

            double minimumLength = (2.0 * countX - 1.0) * closedWidth;
            double leftover = lenA - minimumLength;
            if (leftover < -tol)
            {
                log.AppendLine($"Branch {branchPath} domain [{domainIndex}] (Turtle): guide too short for cx={countX}. Skipped.");
                return 0;
            }

            leftover = Math.Max(0.0, leftover);
            double openPadding = leftover / (2.0 * countX);
            double openWidth = closedWidth + 2.0 * openPadding;
            double toothAbsolute = amplitude <= 0.0 ? 0.0 : Math.Min(pathWidth * 0.8, 0.5 * amplitude);
            double toothNormalized = amplitude <= tol ? 0.0 : Clamp01(toothAbsolute / amplitude);
            double toothBase = teeth ? toothNormalized : 0.0;
            double bridgeY = teeth
                ? toothNormalized * 0.5 + bridge * (1.0 - toothNormalized)
                : bridge;

            var cells = new TurtleCellGeometry[countX];
            double cursor = 0.0;
            for (int cellIndex = 0; cellIndex < countX; cellIndex++)
            {
                double cellStart = cursor;
                double cellEnd = cellStart + openWidth;
                double center = 0.5 * (cellStart + cellEnd);
                double topSpan = Lerp(openWidth, closedWidth, bridge);
                double bottomSpan = Lerp(closedWidth, openWidth, bridge);
                double topLeft = center - 0.5 * topSpan;
                double topRight = center + 0.5 * topSpan;
                double bottomLeft = center - 0.5 * bottomSpan;
                double bottomRight = center + 0.5 * bottomSpan;
                double transition = 0.5 * (topSpan - closedWidth);
                if (Math.Abs(transition) < eps) transition = 0.0;
                cells[cellIndex] = new TurtleCellGeometry
                {
                    CellStart = cellStart,
                    CellEnd = cellEnd,
                    TopLeft = topLeft,
                    TopRight = topRight,
                    BottomLeft = bottomLeft,
                    BottomRight = bottomRight,
                    TransitionOffset = transition
                };
                cursor += openWidth;
                if (cellIndex < countX - 1) cursor += closedWidth;
            }

            var pattern = new List<Point2d>(12 * countX + 4 * Math.Max(0, countX - 1));
            AddUnique(pattern, cells[0].BottomLeft, 0.0, eps);
            for (int cellIndex = 0; cellIndex < countX; cellIndex++)
            {
                TurtleCellGeometry cell = cells[cellIndex];
                AddUnique(pattern, cell.BottomLeft, toothBase, eps);
                AddUnique(pattern, cell.TopLeft, 1.0 - toothBase, eps);
                AddUnique(pattern, cell.TopLeft, 1.0, eps);
                AddUnique(pattern, cell.TopLeft + armWidth, 1.0, eps);
                AddUnique(pattern, cell.TopLeft + armWidth + cell.TransitionOffset, bridgeY, eps);
                AddUnique(pattern, cell.TopLeft + armWidth + cell.TransitionOffset + bridgeWidth, bridgeY, eps);
                AddUnique(pattern, cell.TopLeft + armWidth + 2.0 * cell.TransitionOffset + bridgeWidth, 1.0, eps);
                AddUnique(pattern, cell.TopRight, 1.0, eps);
                AddUnique(pattern, cell.TopRight, 1.0 - toothBase, eps);
                AddUnique(pattern, cell.BottomRight, toothBase, eps);
                AddUnique(pattern, cell.BottomRight, 0.0, eps);

                if (cellIndex >= countX - 1) continue;
                TurtleCellGeometry next = cells[cellIndex + 1];
                double start = cell.BottomRight;
                double end = next.BottomLeft;
                double transitionY = toothBase + Math.Max(0.0, 1.0 - toothBase - bridgeY);
                double span = Math.Max(eps, end - start);
                double effectiveArm = armWidth;
                double effectiveBridge = bridgeWidth;
                effectiveArm = Math.Min(effectiveArm, 0.5 * Math.Max(0.0, span - effectiveBridge));
                if (span < effectiveBridge)
                {
                    effectiveBridge = span;
                    effectiveArm = 0.0;
                }
                double bridgeStart = start + effectiveArm
                    + 0.5 * Math.Max(0.0, span - 2.0 * effectiveArm - effectiveBridge);
                AddUnique(pattern, start + effectiveArm, 0.0, eps);
                AddUnique(pattern, bridgeStart, transitionY, eps);
                AddUnique(pattern, bridgeStart + effectiveBridge, transitionY, eps);
                AddUnique(pattern, end - effectiveArm, 0.0, eps);
                AddUnique(pattern, end, 0.0, eps);
            }

            for (int i = 0; i < pattern.Count; i++)
                pattern[i] = new Point2d(Math.Max(0.0, Math.Min(lenA, pattern[i].X)), pattern[i].Y);

            int made = 0;
            for (int bandIndex = 0; bandIndex < countY; bandIndex++)
            {
                double fraction0 = (double)bandIndex / countY;
                double fraction1 = (double)(bandIndex + 1) / countY;
                var low = new List<Point3d>(pattern.Count);
                var high = new List<Point3d>(pattern.Count);

                foreach (Point2d patternPoint in pattern)
                {
                    double sourceX = lenA <= tol ? 0.0 : patternPoint.X / lenA;
                    double warpedXA = MapGuideWarp(guideSourceA, guideWarpA, sourceX);
                    double warpedXB = MapGuideWarp(guideSourceB, guideWarpB, sourceX);
                    double distanceA = warpedXA * lenA;
                    double normalizedY = patternPoint.Y;
                    if (!cA.LengthParameter(distanceA, out double parameterA))
                        parameterA = cA.Domain.ParameterAt(warpedXA);
                    Point3d pointA = cA.PointAt(parameterA);
                    if (!cB.LengthParameter(warpedXB * lenB, out double parameterB))
                        parameterB = cB.Domain.ParameterAt(warpedXB);
                    Point3d pointB = cB.PointAt(parameterB);

                    Vector3d across = pointB - pointA;
                    double gap = across.Length;
                    if (gap <= tol)
                    {
                        low.Add(pointA);
                        high.Add(pointA);
                        continue;
                    }
                    across.Unitize();
                    double maximumInset = Math.Max(0.0, 0.5 * gap - eps);
                    double actualInsetA = Math.Min(insetA, maximumInset);
                    double actualInsetB = Math.Min(insetB, maximumInset);
                    Point3d insetPointA = pointA + across * actualInsetA;
                    double availableGap = Math.Max(0.0, gap - actualInsetA - actualInsetB);
                    if (availableGap <= tol)
                    {
                        low.Add(insetPointA);
                        high.Add(insetPointA);
                        continue;
                    }

                    double lowerBoundaryInset = bandIndex == 0 ? 0.0 : bandInset;
                    double upperBoundaryInset = bandIndex == countY - 1 ? 0.0 : bandInset;
                    double absolute0 = fraction0 * availableGap + lowerBoundaryInset;
                    double absolute1 = fraction1 * availableGap - upperBoundaryInset;
                    if (absolute0 >= absolute1 - eps)
                    {
                        Point3d midpoint = insetPointA + across * (0.5 * (fraction0 + fraction1) * availableGap);
                        low.Add(midpoint);
                        high.Add(midpoint);
                        continue;
                    }
                    double bandGap = absolute1 - absolute0;
                    double bandAmplitude = Math.Max(0.0, bandGap * 0.5 - bandInset);
                    double offset = normalizedY * bandAmplitude;
                    low.Add(insetPointA + across * (absolute0 + offset));
                    high.Add(insetPointA + across * (absolute1 - offset));
                }

                ExtendPathEnds(low, extend, tol);
                ExtendPathEnds(high, extend, tol);
                if (!TryMakeValidPolyline(low, tol, out Polyline lowPolyline)
                    || !TryMakeValidPolyline(high, tol, out Polyline highPolyline))
                    continue;

                printArea += (lowPolyline.Length + highPolyline.Length) * pathWidth;
                GH_Path lowPath = trimLayers ? layerPath : AppendBandPath(branchPath, domainIndex, bandIndex, 0);
                GH_Path highPath = trimLayers ? layerPath : AppendBandPath(branchPath, domainIndex, bandIndex, 1);
                curves.Add((lowPath, new PolylineCurve(lowPolyline)));
                curves.Add((highPath, new PolylineCurve(highPolyline)));
                for (int i = 0; i < lowPolyline.Count; i++) points.Add((lowPath, lowPolyline[i]));
                for (int i = 0; i < highPolyline.Count; i++) points.Add((highPath, highPolyline[i]));
                made += 2;
            }

            log.AppendLine(
                $"Branch {branchPath} domain [{domainIndex}] (Turtle): cx={countX} cy={countY} cz={countZ:0.###} " +
                $"p_width={pathWidth:0.###} b0={bridge0:0.###} b1={bridge1:0.###} blend={blend:0.###} " +
                $"insetA={insetA:0.###} insetB={insetB:0.###} ext={extend:0.###} teeth={teeth} " +
                $"outer_band_extra=0 internal_band_clear={bandInset:0.###} -> {made} path(s).");
            return made;
        }

        private static GH_Path AppendBandPath(GH_Path basePath, int domain, int band, int side)
        {
            int[] source = basePath.Indices;
            int[] indices = new int[source.Length + 3];
            Array.Copy(source, indices, source.Length);
            indices[source.Length] = domain;
            indices[source.Length + 1] = band;
            indices[source.Length + 2] = side;
            return new GH_Path(indices);
        }

        private static GH_Path AppendDomainPath(GH_Path basePath, int domain, int child)
        {
            int[] source = basePath.Indices;
            int extra = child >= 0 ? 2 : 1;
            int[] indices = new int[source.Length + extra];
            Array.Copy(source, indices, source.Length);
            indices[source.Length] = domain;
            if (child >= 0)
                indices[source.Length + 1] = child;
            return new GH_Path(indices);
        }

        private static void AddUnique(List<Point2d> points, double x, double y, double tol)
        {
            var point = new Point2d(x, y);
            if (points.Count == 0 || points[points.Count - 1].DistanceTo(point) > tol)
                points.Add(point);
        }

        private static void ExtendPathEnds(List<Point3d> points, double distance, double tol)
        {
            if (points == null || points.Count < 2 || distance <= tol) return;
            Vector3d start = points[0] - points[1];
            if (start.IsValid && start.Length > tol)
            {
                start.Unitize();
                points[0] += start * distance;
            }
            int last = points.Count - 1;
            Vector3d end = points[last] - points[last - 1];
            if (end.IsValid && end.Length > tol)
            {
                end.Unitize();
                points[last] += end * distance;
            }
        }

        private static double Clamp01(double value) =>
            value < 0.0 ? 0.0 : value > 1.0 ? 1.0 : value;

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private static double TriangleWave01(double normalizedPosition, double cycles)
        {
            if (cycles <= 0.0) return 0.0;
            double fraction = normalizedPosition * cycles - Math.Floor(normalizedPosition * cycles);
            return Clamp01(1.0 - Math.Abs(2.0 * fraction - 1.0));
        }

        private static double ShapeValue2D(int type, double cellT)
        {
            switch (type)
            {
                case 1:
                    return cellT < 0.5 ? 0.0 : 1.0;
                case 3:
                    return cellT < 0.5 ? 2.0 * cellT : 2.0 * (1.0 - cellT);
                case 4:
                default:
                    return 0.5 * (1.0 - Math.Cos(2.0 * Math.PI * cellT));
            }
        }

    }
}
