using System;
using System.Collections.Generic;
using System.Linq;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace WASPer_3DP.Components._3_1_Infills
{
    public partial class wsp_In10_Layered_Multi_Infill_From_Curves
    {
        private enum GuidePairTopology
        {
            OpenOpen,
            ClosedClosed,
            OpenClosed,
            ClosedOpen
        }

        private static void AlignGuideDirections(
            IList<Curve> curves,
            double closureTolerance)
        {
            if (curves == null || curves.Count < 2 || curves[0] == null)
                return;

            for (int i = 1; i < curves.Count; i++)
            {
                Curve previous = curves[i - 1];
                Curve curve = curves[i];
                if (previous == null || curve == null)
                    continue;

                bool previousClosed = IsEffectivelyClosed(previous, closureTolerance);
                bool currentClosed = IsEffectivelyClosed(curve, closureTolerance);

                // Open guides are already the exact physical pieces supplied by the user.
                // Choose only their traversal direction, using their endpoints rather than
                // local tangents (which are unreliable on circular/jagged openings).
                if (!previousClosed && !currentClosed)
                {
                    double direct =
                        previous.PointAtStart.DistanceTo(curve.PointAtStart) +
                        previous.PointAtEnd.DistanceTo(curve.PointAtEnd);
                    double reversed =
                        previous.PointAtStart.DistanceTo(curve.PointAtEnd) +
                        previous.PointAtEnd.DistanceTo(curve.PointAtStart);
                    if (reversed + closureTolerance < direct)
                        curve.Reverse();
                    continue;
                }

                // Closed loops have no meaningful endpoints until their seams match.
                // Align only Closed-Closed neighbours here; mixed pairs are handled by
                // TryPrepareGuidePair, which extracts the closed arc matching the open piece.
                if (previousClosed && currentClosed)
                {
                    if (curve.ClosestPoint(previous.PointAtStart, out double seamT))
                    {
                        try { curve.ChangeClosedCurveSeam(seamT); }
                        catch { }
                    }

                    Vector3d previousTangent = previous.TangentAtStart;
                    Vector3d currentTangent = curve.TangentAtStart;
                    if (previousTangent.IsValid && currentTangent.IsValid)
                    {
                        previousTangent.Unitize();
                        currentTangent.Unitize();
                        if (Vector3d.Multiply(previousTangent, currentTangent) < 0.0)
                            curve.Reverse();
                    }
                }
            }
        }

        private static bool IsEffectivelyClosed(Curve curve, double closureTolerance)
        {
            return curve != null && curve.IsValid &&
                (curve.IsClosed ||
                 curve.PointAtStart.DistanceTo(curve.PointAtEnd) <= closureTolerance);
        }

        private static GuidePairTopology ClassifyGuidePair(
            Curve first,
            Curve second,
            double closureTolerance)
        {
            bool firstClosed = IsEffectivelyClosed(first, closureTolerance);
            bool secondClosed = IsEffectivelyClosed(second, closureTolerance);
            if (firstClosed && secondClosed)
                return GuidePairTopology.ClosedClosed;
            if (firstClosed)
                return GuidePairTopology.ClosedOpen;
            if (secondClosed)
                return GuidePairTopology.OpenClosed;
            return GuidePairTopology.OpenOpen;
        }

        private static bool HasMixedGuideTopology(
            GH_Structure<GH_Curve> guideTree,
            double closureTolerance)
        {
            if (guideTree == null)
                return false;

            foreach (List<GH_Curve> branch in guideTree.Branches)
            {
                if (branch == null || branch.Count < 2)
                    continue;
                for (int i = 0; i < branch.Count - 1; i++)
                {
                    Curve first = branch[i]?.Value;
                    Curve second = branch[i + 1]?.Value;
                    GuidePairTopology topology = ClassifyGuidePair(
                        first,
                        second,
                        closureTolerance);
                    if (topology == GuidePairTopology.OpenClosed ||
                        topology == GuidePairTopology.ClosedOpen)
                        return true;
                }
            }

            // Also flag a topology transition at the same guide index across layers.
            // This disables the editor because its station topology cannot be shared,
            // but it must not alter, crop, or remap the supplied curves.
            int guideCount = guideTree.Branches
                .Where(branch => branch != null)
                .Select(branch => branch.Count)
                .DefaultIfEmpty(0)
                .Max();
            for (int guide = 0; guide < guideCount; guide++)
            {
                bool foundOpen = false;
                bool foundClosed = false;
                foreach (List<GH_Curve> branch in guideTree.Branches)
                {
                    Curve curve = branch != null && guide < branch.Count
                        ? branch[guide]?.Value
                        : null;
                    if (curve == null || !curve.IsValid)
                        continue;
                    if (IsEffectivelyClosed(curve, closureTolerance))
                        foundClosed = true;
                    else
                        foundOpen = true;
                    if (foundOpen && foundClosed)
                        return true;
                }
            }
            return false;
        }

        private static Curve PrepareEffectivelyClosedCurve(
            Curve source,
            double closureTolerance)
        {
            Curve result = source?.DuplicateCurve();
            if (result == null || !result.IsValid)
                return null;
            if (!result.IsClosed &&
                result.PointAtStart.DistanceTo(result.PointAtEnd) <= closureTolerance)
            {
                try { result.MakeClosed(closureTolerance); }
                catch { }
            }
            return result;
        }

        private static bool TryPrepareGuidePair(
            Curve sourceA,
            Curve sourceB,
            double closureTolerance,
            double tolerance,
            out Curve mappedA,
            out Curve mappedB,
            out GuidePairTopology topology,
            out string note)
        {
            mappedA = null;
            mappedB = null;
            note = string.Empty;
            topology = ClassifyGuidePair(sourceA, sourceB, closureTolerance);

            if (topology == GuidePairTopology.OpenOpen)
            {
                mappedA = sourceA?.DuplicateCurve();
                mappedB = sourceB?.DuplicateCurve();
                return mappedA != null && mappedB != null;
            }

            if (topology == GuidePairTopology.ClosedClosed)
            {
                mappedA = PrepareEffectivelyClosedCurve(sourceA, closureTolerance);
                mappedB = PrepareEffectivelyClosedCurve(sourceB, closureTolerance);
                if (mappedA == null || mappedB == null)
                {
                    note = "could not duplicate the closed guides";
                    return false;
                }
                if (!mappedA.IsClosed || !mappedB.IsClosed)
                {
                    note = "an effectively closed guide could not be converted to a periodic curve";
                    return false;
                }

                if (mappedB.IsClosed &&
                    mappedB.ClosestPoint(mappedA.PointAtStart, out double seamT))
                {
                    try
                    {
                        if (!mappedB.ChangeClosedCurveSeam(seamT))
                        {
                            note = "could not align the second closed-guide seam";
                            return false;
                        }
                    }
                    catch
                    {
                        note = "could not align the second closed-guide seam";
                        return false;
                    }
                }
                return true;
            }

            Curve openGuide = topology == GuidePairTopology.OpenClosed ? sourceA : sourceB;
            Curve closedGuide = topology == GuidePairTopology.OpenClosed ? sourceB : sourceA;
            if (!TryExtractMatchingClosedArc(
                    openGuide,
                    closedGuide,
                    closureTolerance,
                    tolerance,
                    out Curve closedArc,
                    out note))
                return false;

            if (topology == GuidePairTopology.OpenClosed)
            {
                mappedA = openGuide.DuplicateCurve();
                mappedB = closedArc;
            }
            else
            {
                mappedA = closedArc;
                mappedB = openGuide.DuplicateCurve();
            }
            return mappedA != null && mappedB != null &&
                mappedA.IsValid && mappedB.IsValid;
        }

        private static bool TryExtractMatchingClosedArc(
            Curve openGuide,
            Curve closedGuide,
            double closureTolerance,
            double tolerance,
            out Curve matchingArc,
            out string note)
        {
            matchingArc = null;
            note = string.Empty;
            if (openGuide == null || closedGuide == null ||
                !openGuide.IsValid || !closedGuide.IsValid)
            {
                note = "invalid open/closed guide pair";
                return false;
            }

            Curve loop = PrepareEffectivelyClosedCurve(closedGuide, closureTolerance);
            if (loop == null || !loop.IsClosed)
            {
                note = "the nominally closed guide could not be made periodic";
                return false;
            }
            if (!loop.ClosestPoint(openGuide.PointAtStart, out double startT))
            {
                note = "could not project the open-guide start onto the closed guide";
                return false;
            }

            try
            {
                if (!loop.ChangeClosedCurveSeam(startT))
                {
                    note = "could not align the closed-guide seam";
                    return false;
                }
            }
            catch
            {
                note = "could not align the closed-guide seam";
                return false;
            }
            if (!loop.ClosestPoint(openGuide.PointAtEnd, out double endT))
            {
                note = "could not project the open-guide end onto the closed guide";
                return false;
            }

            double parameterTolerance = Math.Max(1e-12, loop.Domain.Length * 1e-10);
            var candidates = new List<Curve>(2);
            if (endT > loop.Domain.T0 + parameterTolerance)
            {
                Curve forward = loop.Trim(loop.Domain.T0, endT);
                if (forward != null && forward.IsValid && forward.GetLength() > tolerance)
                    candidates.Add(forward);
            }
            if (endT < loop.Domain.T1 - parameterTolerance)
            {
                Curve complementary = loop.Trim(endT, loop.Domain.T1);
                if (complementary != null && complementary.IsValid &&
                    complementary.GetLength() > tolerance)
                {
                    complementary.Reverse();
                    candidates.Add(complementary);
                }
            }
            if (candidates.Count == 0)
            {
                note = "the projected endpoints did not define a usable closed-guide arc";
                return false;
            }

            double openLength = openGuide.GetLength();
            matchingArc = candidates
                .OrderBy(candidate => GuideCorrespondenceScore(
                    openGuide,
                    openLength,
                    candidate,
                    candidate.GetLength(),
                    tolerance))
                .FirstOrDefault();
            if (matchingArc == null)
            {
                note = "could not select a matching closed-guide arc";
                return false;
            }

            note = $"mapped the open guide to the best of two closed-guide arcs ({matchingArc.GetLength():0.###} model units)";
            return true;
        }

        private static double GuideCorrespondenceScore(
            Curve first,
            double firstLength,
            Curve second,
            double secondLength,
            double tolerance)
        {
            const int sampleCount = 9;
            double score = 0.0;
            for (int i = 0; i < sampleCount; i++)
            {
                double u = (double)i / (sampleCount - 1);
                Point3d a = PointAtNormalizedLength(first, firstLength, u, tolerance);
                Point3d b = PointAtNormalizedLength(second, secondLength, u, tolerance);
                score += a.DistanceToSquared(b);
            }
            return score / sampleCount;
        }

        private static double EstimateAverageGap(Curve bottom, Curve top, double lenB, double lenT)
        {
            double[] u = { 0.0, 0.25, 0.5, 0.75, 1.0 };
            double sum = 0.0; int n = 0;
            foreach (double uu in u)
            {
                double tB, tT;
                if (!bottom.LengthParameter(uu * lenB, out tB)) tB = bottom.Domain.ParameterAt(uu);
                if (!top.LengthParameter(uu * lenT, out tT)) tT = top.Domain.ParameterAt(uu);
                sum += bottom.PointAt(tB).DistanceTo(top.PointAt(tT));
                n++;
            }
            return n > 0 ? sum / n : 0.0;
        }

        private static bool TryCreateClearanceGuidePair(
            Curve sourceA,
            Curve sourceB,
            Plane layerPlane,
            double insetA,
            double insetB,
            double longitudinalInset,
            double lateralShellEndInset,
            double tolerance,
            out Curve clearedA,
            out Curve clearedB,
            out string note)
        {
            clearedA = null;
            clearedB = null;
            note = string.Empty;
            if (sourceA == null || sourceB == null ||
                !sourceA.IsValid || !sourceB.IsValid || !layerPlane.IsValid)
            {
                note = "invalid guide curve or layer plane";
                return false;
            }

            if (!TryOffsetGuideToward(
                    sourceA,
                    sourceB,
                    layerPlane,
                    Math.Max(0.0, insetA),
                    tolerance,
                    out clearedA))
            {
                note = $"could not offset first guide by {insetA:0.###}";
                return false;
            }
            if (!TryOffsetGuideToward(
                    sourceB,
                    sourceA,
                    layerPlane,
                    Math.Max(0.0, insetB),
                    tolerance,
                    out clearedB))
            {
                note = $"could not offset second guide by {insetB:0.###}";
                return false;
            }

            AlignCurveDirection(clearedA, sourceA);
            AlignCurveDirection(clearedB, sourceB);
            if (longitudinalInset <= tolerance)
                return true;

            // A closed/closed domain has no physical lateral shell segments: its start
            // and end are the same seam. An explicitly supplied clear_long deliberately
            // opens that seam and therefore retains the seam arc-length behavior.
            if (sourceA.IsClosed && sourceB.IsClosed)
            {
                Curve seamA = TrimCurveEnds(clearedA, longitudinalInset, tolerance);
                Curve seamB = TrimCurveEnds(clearedB, longitudinalInset, tolerance);
                if (seamA == null || seamB == null)
                {
                    note = $"seam clearance {longitudinalInset:0.###} removed the closed domain";
                    return false;
                }
                clearedA = seamA;
                clearedB = seamB;
                return true;
            }

            return TryApplyLateralBoundaryClearance(
                sourceA,
                sourceB,
                clearedA,
                clearedB,
                layerPlane,
                longitudinalInset,
                lateralShellEndInset,
                tolerance,
                out clearedA,
                out clearedB,
                out note);
        }

        /// <summary>
        /// Applies clear_long as a true inward offset of the two lateral domain edges:
        /// sourceA.Start-sourceB.Start and sourceA.End-sourceB.End. The already-offset
        /// transverse guides are intersected with those shifted boundaries. Selection is
        /// anchored at the physical start and end of each open curve, so a curved guide
        /// cannot jump to the complementary arc merely because that arc is longer.
        /// </summary>
        private static bool TryApplyLateralBoundaryClearance(
            Curve sourceA,
            Curve sourceB,
            Curve guideA,
            Curve guideB,
            Plane layerPlane,
            double distance,
            double lateralShellEndInset,
            double tolerance,
            out Curve trimmedA,
            out Curve trimmedB,
            out string note)
        {
            trimmedA = null;
            trimmedB = null;
            note = string.Empty;
            if (distance <= tolerance)
            {
                trimmedA = guideA?.DuplicateCurve();
                trimmedB = guideB?.DuplicateCurve();
                return trimmedA != null && trimmedB != null;
            }

            Point3d center = 0.5 * (
                PointAtNormalizedLength(sourceA, sourceA.GetLength(), 0.5, tolerance) +
                PointAtNormalizedLength(sourceB, sourceB.GetLength(), 0.5, tolerance));
            Point3d startA = PointAtEndInset(sourceA, lateralShellEndInset, true, tolerance);
            Point3d startB = PointAtEndInset(sourceB, lateralShellEndInset, true, tolerance);
            Point3d endA = PointAtEndInset(sourceA, lateralShellEndInset, false, tolerance);
            Point3d endB = PointAtEndInset(sourceB, lateralShellEndInset, false, tolerance);
            if (!TryCreateLateralClearancePlane(
                    startA,
                    startB,
                    center,
                    layerPlane,
                    distance,
                    tolerance,
                    out Plane startPlane) ||
                !TryCreateLateralClearancePlane(
                    endA,
                    endB,
                    center,
                    layerPlane,
                    distance,
                    tolerance,
                    out Plane endPlane))
            {
                note = "could not construct inward offsets of the lateral shell segments";
                return false;
            }

            trimmedA = TrimCurveBetweenLateralBoundaries(
                guideA,
                startPlane,
                endPlane,
                tolerance);
            trimmedB = TrimCurveBetweenLateralBoundaries(
                guideB,
                startPlane,
                endPlane,
                tolerance);
            if (trimmedA == null || trimmedB == null ||
                !trimmedA.IsValid || !trimmedB.IsValid)
            {
                note = $"lateral-shell clearance {distance:0.###} removed the domain";
                return false;
            }
            return true;
        }

        private static Point3d PointAtEndInset(
            Curve curve,
            double inset,
            bool fromStart,
            double tolerance)
        {
            double length = curve.GetLength();
            if (inset <= tolerance || length <= tolerance)
                return fromStart ? curve.PointAtStart : curve.PointAtEnd;
            double bounded = Math.Min(inset, Math.Max(0.0, 0.5 * length - tolerance));
            double arcLength = fromStart ? bounded : length - bounded;
            if (!curve.LengthParameter(arcLength, out double parameter))
                parameter = curve.Domain.ParameterAt(arcLength / length);
            return curve.PointAt(parameter);
        }

        private static bool TryCreateLateralClearancePlane(
            Point3d boundaryA,
            Point3d boundaryB,
            Point3d domainCenter,
            Plane layerPlane,
            double distance,
            double tolerance,
            out Plane clearancePlane)
        {
            Point3d midpoint = 0.5 * (boundaryA + boundaryB);
            Vector3d lateral = boundaryB - boundaryA;
            Vector3d inward = Vector3d.CrossProduct(layerPlane.Normal, lateral);
            if (!inward.Unitize())
            {
                inward = domainCenter - midpoint;
                inward -= layerPlane.Normal * Vector3d.Multiply(inward, layerPlane.Normal);
                if (!inward.Unitize())
                {
                    clearancePlane = Plane.Unset;
                    return false;
                }
            }
            if (Vector3d.Multiply(inward, domainCenter - midpoint) < 0.0)
                inward.Reverse();
            if (Vector3d.Multiply(inward, domainCenter - midpoint) <= distance + tolerance)
            {
                clearancePlane = Plane.Unset;
                return false;
            }
            clearancePlane = new Plane(midpoint + inward * distance, inward);
            return clearancePlane.IsValid;
        }

        private static Curve TrimCurveBetweenLateralBoundaries(
            Curve curve,
            Plane startPlane,
            Plane endPlane,
            double tolerance)
        {
            if (curve == null || !curve.IsValid)
                return null;

            if (!TryFindFirstInsideParameter(curve, startPlane, tolerance, out double t0) ||
                !TryFindLastInsideParameter(curve, endPlane, tolerance, out double t1) ||
                t1 <= t0 + 1e-12)
                return null;

            Curve trimmed = curve.Trim(t0, t1);
            if (trimmed == null || !trimmed.IsValid || trimmed.GetLength() <= tolerance)
                return null;

            // Reject an accidental complementary/wrapped interval.
            if (startPlane.DistanceTo(trimmed.PointAtStart) < -tolerance * 2.0 ||
                endPlane.DistanceTo(trimmed.PointAtStart) < -tolerance * 2.0 ||
                startPlane.DistanceTo(trimmed.PointAtEnd) < -tolerance * 2.0 ||
                endPlane.DistanceTo(trimmed.PointAtEnd) < -tolerance * 2.0)
                return null;
            return trimmed;
        }

        private static bool TryFindFirstInsideParameter(
            Curve curve,
            Plane boundary,
            double tolerance,
            out double parameter)
        {
            List<double> parameters = ClearanceIntersectionParameters(curve, boundary, tolerance);
            for (int i = 0; i < parameters.Count - 1; i++)
            {
                double a = parameters[i];
                double b = parameters[i + 1];
                if (b > a + 1e-12 &&
                    boundary.DistanceTo(curve.PointAt(0.5 * (a + b))) >= -tolerance)
                {
                    parameter = a;
                    return true;
                }
            }
            parameter = double.NaN;
            return false;
        }

        private static bool TryFindLastInsideParameter(
            Curve curve,
            Plane boundary,
            double tolerance,
            out double parameter)
        {
            List<double> parameters = ClearanceIntersectionParameters(curve, boundary, tolerance);
            for (int i = parameters.Count - 2; i >= 0; i--)
            {
                double a = parameters[i];
                double b = parameters[i + 1];
                if (b > a + 1e-12 &&
                    boundary.DistanceTo(curve.PointAt(0.5 * (a + b))) >= -tolerance)
                {
                    parameter = b;
                    return true;
                }
            }
            parameter = double.NaN;
            return false;
        }

        private static List<double> ClearanceIntersectionParameters(
            Curve curve,
            Plane boundary,
            double tolerance)
        {
            var parameters = new List<double> { curve.Domain.Min, curve.Domain.Max };
            Rhino.Geometry.Intersect.CurveIntersections intersections =
                Rhino.Geometry.Intersect.Intersection.CurvePlane(curve, boundary, tolerance);
            if (intersections != null)
            {
                foreach (Rhino.Geometry.Intersect.IntersectionEvent intersection in intersections)
                    if (intersection.IsPoint)
                        parameters.Add(Math.Max(
                            curve.Domain.Min,
                            Math.Min(curve.Domain.Max, intersection.ParameterA)));
            }
            parameters.Sort();
            var unique = new List<double>();
            foreach (double value in parameters)
                if (unique.Count == 0 || Math.Abs(unique[unique.Count - 1] - value) > 1e-10)
                    unique.Add(value);
            return unique;
        }

        private static bool TryOffsetGuideToward(
            Curve source,
            Curve target,
            Plane plane,
            double distance,
            double tolerance,
            out Curve result)
        {
            result = null;
            if (distance <= tolerance)
            {
                result = source.DuplicateCurve();
                return result != null && result.IsValid;
            }

            double sourceLength = source.GetLength();
            bool sourceClosed = source.IsClosed;
            double originalTargetDistance = AverageCurveDistance(source, target, tolerance);
            double bestScore = double.MaxValue;
            foreach (double signedDistance in new[] { distance, -distance })
            {
                Curve[] raw;
                try
                {
                    raw = source.Offset(
                        plane,
                        signedDistance,
                        tolerance,
                        CurveOffsetCornerStyle.Sharp);
                }
                catch
                {
                    raw = null;
                }
                if (raw == null || raw.Length == 0)
                    continue;
                Curve[] joined = Curve.JoinCurves(raw, tolerance);
                IEnumerable<Curve> candidates = joined != null && joined.Length > 0
                    ? joined.Concat(raw)
                    : raw;
                foreach (Curve candidateSource in candidates)
                {
                    if (candidateSource == null || !candidateSource.IsValid)
                        continue;
                    Curve candidate = candidateSource.DuplicateCurve();
                    // Rhino offset/join can occasionally close an open guide or return a
                    // periodic complementary candidate. That destroys the meaning of the
                    // two physical ends and makes clear_long trim at an arbitrary seam.
                    if (candidate.IsClosed != sourceClosed)
                        continue;
                    double candidateLength = candidate.GetLength();
                    if (candidateLength <= tolerance || candidateLength < sourceLength * 0.2)
                        continue;
                    AlignCurveDirection(candidate, source);
                    double endpointPenalty = 0.0;
                    if (!sourceClosed)
                    {
                        double startDrift = candidate.PointAtStart.DistanceTo(source.PointAtStart);
                        double endDrift = candidate.PointAtEnd.DistanceTo(source.PointAtEnd);
                        double maximumEndpointDrift = Math.Max(
                            tolerance * 20.0,
                            Math.Max(distance * 4.0, sourceLength * 0.05));
                        if (startDrift > maximumEndpointDrift ||
                            endDrift > maximumEndpointDrift)
                            continue;
                        endpointPenalty = 0.25 * (startDrift + endDrift);
                    }
                    double targetDistance = AverageCurveDistance(candidate, target, tolerance);
                    double sourceDistance = AverageCurveDistance(candidate, source, tolerance);
                    double score = targetDistance + endpointPenalty +
                        0.25 * Math.Abs(sourceDistance - distance) +
                        0.05 * Math.Abs(candidateLength - sourceLength);
                    if (targetDistance > originalTargetDistance + distance * 0.25 + tolerance ||
                        score >= bestScore)
                        continue;
                    bestScore = score;
                    result = candidate;
                }
            }
            return result != null && result.IsValid;
        }

        private static double AverageCurveDistance(
            Curve source,
            Curve target,
            double tolerance)
        {
            if (source == null || target == null)
                return double.MaxValue;
            double sourceLength = source.GetLength();
            double sum = 0.0;
            const int sampleCount = 17;
            for (int i = 0; i < sampleCount; i++)
            {
                Point3d point = PointAtNormalizedLength(
                    source,
                    sourceLength,
                    (double)i / (sampleCount - 1),
                    tolerance);
                if (!target.ClosestPoint(point, out double parameter))
                    return double.MaxValue;
                sum += point.DistanceTo(target.PointAt(parameter));
            }
            return sum / sampleCount;
        }

        private static void AlignCurveDirection(Curve curve, Curve reference)
        {
            if (curve == null || reference == null)
                return;
            double same = curve.PointAtStart.DistanceToSquared(reference.PointAtStart) +
                curve.PointAtEnd.DistanceToSquared(reference.PointAtEnd);
            double reversed = curve.PointAtStart.DistanceToSquared(reference.PointAtEnd) +
                curve.PointAtEnd.DistanceToSquared(reference.PointAtStart);
            if (reversed < same)
                curve.Reverse();
        }

        private static Curve TrimCurveEnds(Curve crv, double amount, double tol)
        {
            double len = crv.GetLength();
            if (len <= 2.0 * amount + tol) return null;
            double t0, t1;
            if (!crv.LengthParameter(amount, out t0)) t0 = crv.Domain.ParameterAt(amount / len);
            if (!crv.LengthParameter(len - amount, out t1)) t1 = crv.Domain.ParameterAt((len - amount) / len);
            if (t1 <= t0 + tol) return null;
            return crv.Trim(t0, t1);
        }

    }
}
