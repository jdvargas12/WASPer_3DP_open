// WASPer_TrimCore.cs
// Shared per-layer printing-path trimming core.
//
// Extracted 2026-07-14 from wsp_Sl02_SlicerPlus_v2.cs (pre-refactor snapshot:
// Components\3.0_Slicing\Archive\wsp_Sl02_SlicerPlus_v2_archive_20260714_pre_shared_trimcore.cs)
// so that Sl02 SlicerPlus and Sl03 Re-Trim Printing Paths share identical
// shell-window / partition-band / infill-acceptance behavior.
//
// TrimLayer() is the per-layer entry point. All helper logic is byte-for-byte
// the original Sl02 implementation; only accessibility changed (private -> internal).

using System;
using System.Collections.Generic;
using System.Linq;

using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace WASPer_3DP
{
    internal sealed class WasperPreparedTrimShell
    {
        internal bool HasShell { get; set; }
        internal bool HasClosedShell { get; set; }
        internal bool HasClosedWindow { get; set; }
        internal List<Curve> OutputShell { get; } = new List<Curve>();
        internal List<Curve> TrimOuters { get; } = new List<Curve>();
        internal List<Curve> TrimHoles { get; } = new List<Curve>();
    }

    internal static class WasperTrimCore
    {
        // -----------------------------------------------------------------
        // Per-layer entry point (extracted from the Sl02 layer work lambda)
        // -----------------------------------------------------------------
        //
        // hasShell distinguishes "no shell supplied" (infill/partitions are
        // processed without a containment window) from "shell supplied but no
        // closed loops on this layer" (open shell curves pass through and the
        // layer produces no infill/partition output, matching Sl02).
        //
        // partitionGroups / infillGroups: one inner list per source object,
        // preserving Sl02's per-source band/splitter fallback rule.
        internal static void TrimLayer(
            bool hasShell,
            List<Curve> shellClosed,
            List<Curve> shellOpen,
            List<List<Curve>> partitionGroups,
            List<List<Curve>> infillGroups,
            Plane pl,
            double shellPathWidth,
            int shellNContours,
            double infillPathWidth,
            int infillNContours,
            double partitionPathWidth,
            int partitionNContours,
            double tol,
            double minLen,
            out List<Curve> outShell,
            out List<Curve> outInfill,
            out List<Curve> outPartition)
        {
            WasperPreparedTrimShell preparedShell = PrepareShell(
                hasShell,
                shellClosed,
                shellOpen,
                pl,
                shellPathWidth,
                shellNContours,
                tol,
                minLen);

            TrimLayer(
                preparedShell,
                partitionGroups,
                infillGroups,
                pl,
                infillPathWidth,
                infillNContours,
                partitionPathWidth,
                partitionNContours,
                tol,
                minLen,
                out outShell,
                out outInfill,
                out outPartition);
        }

        internal static WasperPreparedTrimShell PrepareShell(
            bool hasShell,
            List<Curve> shellClosed,
            List<Curve> shellOpen,
            Plane pl,
            double shellPathWidth,
            int shellNContours,
            double tol,
            double minLen)
        {
            var prepared = new WasperPreparedTrimShell { HasShell = hasShell };

            if (!hasShell)
                return prepared;

            if (shellClosed == null || shellClosed.Count == 0)
            {
                if (shellOpen != null)
                    prepared.OutputShell.AddRange(shellOpen.Select(c => c.DuplicateCurve()));
                return prepared;
            }

            prepared.HasClosedShell = true;

            SplitLoopsByNesting(shellClosed, pl, tol, out List<Curve> outers, out List<Curve> holes);

            if (shellPathWidth > RhinoMath.ZeroTolerance)
                prepared.OutputShell.AddRange(ShellCenterlines(outers, holes, pl, shellPathWidth, shellNContours, tol, minLen));
            else
                prepared.OutputShell.AddRange(shellClosed.Select(c => c.DuplicateCurve()));

            double shellInset = shellNContours * shellPathWidth;
            BuildShellWindow(outers, holes, pl, shellInset, tol, out List<Curve> trimOu, out List<Curve> trimHo);
            prepared.TrimOuters.AddRange(trimOu);
            prepared.TrimHoles.AddRange(trimHo);
            prepared.HasClosedWindow = prepared.TrimOuters.Count > 0;
            return prepared;
        }

        internal static void TrimLayer(
            WasperPreparedTrimShell preparedShell,
            List<List<Curve>> partitionGroups,
            List<List<Curve>> infillGroups,
            Plane pl,
            double infillPathWidth,
            int infillNContours,
            double partitionPathWidth,
            int partitionNContours,
            double tol,
            double minLen,
            out List<Curve> outShell,
            out List<Curve> outInfill,
            out List<Curve> outPartition)
        {
            preparedShell = preparedShell ?? new WasperPreparedTrimShell();
            outShell = preparedShell.OutputShell.Select(c => c.DuplicateCurve()).ToList();
            outInfill = new List<Curve>();
            outPartition = new List<Curve>();

            var trimOu = preparedShell.TrimOuters;
            var trimHo = preparedShell.TrimHoles;
            bool hasShell = preparedShell.HasShell;
            bool hasWindow = preparedShell.HasClosedWindow;

            if (hasShell && !preparedShell.HasClosedShell)
                return;

            var partitionBands = new List<Curve>();
            var splitters = new List<Curve>();

            if (partitionGroups != null)
            {
                foreach (var raw in partitionGroups)
                {
                    if (raw == null) continue;

                    var trimmed = hasWindow ? TrimCurvesToWindow(raw, trimOu, trimHo, pl, tol, minLen) : raw;

                    List<Curve> closedPartitions;
                    List<Curve> openPartitions;
                    SplitClosedAndOpenPartitionCurves(trimmed, tol, minLen, out closedPartitions, out openPartitions);

                    if (partitionPathWidth > RhinoMath.ZeroTolerance)
                    {
                        if (closedPartitions.Count > 0)
                        {
                            List<Curve> partOuters, partHoles;
                            SplitLoopsByNesting(closedPartitions, pl, tol, out partOuters, out partHoles);

                            outPartition.AddRange(ShellCenterlines(partOuters, partHoles, pl, partitionPathWidth, partitionNContours, tol, minLen));

                            // Closed partition/stiffener geometry acts as occupied material.
                            // Add its outer regions as blocking loops so infill is removed there.
                            partitionBands.AddRange(partOuters.Select(c => c.DuplicateCurve()));
                        }

                        outPartition.AddRange(CenteredContours(openPartitions, pl, partitionPathWidth, partitionNContours, tol, minLen));

                        double halfW = 0.5 * Math.Max(1, partitionNContours) * partitionPathWidth;
                        int bandCountBeforeOpen = partitionBands.Count;
                        foreach (var c in openPartitions)
                            partitionBands.AddRange(BuildBandLoops(c, pl, halfW, tol));

                        if (partitionBands.Count == bandCountBeforeOpen && openPartitions.Count > 0)
                            splitters.AddRange(openPartitions.Select(c => c.DuplicateCurve()));
                    }
                    else
                    {
                        outPartition.AddRange(trimmed.Select(c => c.DuplicateCurve()));
                        splitters.AddRange(trimmed.Select(c => c.DuplicateCurve()));
                    }
                }
            }

            List<Curve> cutters;
            BoundingBox[] cutterBB;
            BuildCutters(hasWindow, trimOu, trimHo, partitionBands, splitters, out cutters, out cutterBB);

            var accepted = new List<Curve>();

            if (infillGroups != null)
            {
                foreach (var rawSlices in infillGroups)
                {
                    if (rawSlices == null) continue;

                    foreach (var raw in rawSlices)
                    {
                        var ts = CollectSplitParameters(raw, cutters, cutterBB, tol);
                        var pieces = SplitByTs(raw, ts);

                        foreach (var pc in pieces)
                        {
                            if (pc == null || !pc.IsValid) continue;
                            if (pc.GetLength() < minLen) continue;

                            if (AcceptInfillPiece(pc, hasWindow, trimOu, trimHo, partitionBands, pl, tol))
                                accepted.Add(pc);
                        }
                    }
                }
            }

            if (infillPathWidth > RhinoMath.ZeroTolerance)
            {
                var contoured = CenteredContours(accepted, pl, infillPathWidth, infillNContours, tol, minLen);

                foreach (var c in contoured)
                {
                    var ts = CollectSplitParameters(c, cutters, cutterBB, tol);
                    var pieces = SplitByTs(c, ts);

                    foreach (var pc in pieces)
                    {
                        if (pc == null || !pc.IsValid) continue;
                        if (pc.GetLength() < minLen) continue;

                        if (AcceptInfillPiece(pc, hasWindow, trimOu, trimHo, partitionBands, pl, tol))
                            outInfill.Add(pc);
                    }
                }
            }
            else
            {
                outInfill.AddRange(accepted);
            }
        }

        // -----------------------------------------------------------------
        // Geometry / loop utilities
        // -----------------------------------------------------------------

        internal static Plane OrthoPlane(Point3d origin, Vector3d z)
        {
            if (z.IsZero) z = Vector3d.ZAxis;
            z.Unitize();

            Vector3d x = Vector3d.CrossProduct(z, Vector3d.XAxis);
            if (x.IsTiny()) x = Vector3d.CrossProduct(z, Vector3d.YAxis);
            x.Unitize();

            Vector3d y = Vector3d.CrossProduct(z, x);
            y.Unitize();

            return new Plane(origin, x, y);
        }

        internal static List<Curve> CloseAndCullCurves(
            IEnumerable<Curve> curves,
            double tol,
            double minLen,
            out List<Curve> open)
        {
            var closed = new List<Curve>();
            open = new List<Curve>();

            if (curves == null) return closed;

            foreach (var source in curves)
            {
                if (source == null || !source.IsValid) continue;
                if (source.GetLength() < minLen) continue;

                var curve = source.DuplicateCurve();

                if (!curve.IsClosed && curve.PointAtStart.DistanceTo(curve.PointAtEnd) <= tol)
                    curve.MakeClosed(tol);

                if (curve.IsClosed)
                    closed.Add(curve);
                else
                    open.Add(curve);
            }

            return closed;
        }

        internal static void SplitLoopsByNesting(
            List<Curve> loops,
            Plane plane,
            double tol,
            out List<Curve> outers,
            out List<Curve> holes)
        {
            outers = new List<Curve>();
            holes = new List<Curve>();

            for (int i = 0; i < loops.Count; i++)
            {
                Point3d p = PointAtNormalized(loops[i], 0.5);
                int insideCount = 0;

                for (int j = 0; j < loops.Count; j++)
                {
                    if (i == j) continue;

                    var containment = loops[j].Contains(p, plane, tol);
                    if (containment == PointContainment.Inside || containment == PointContainment.Coincident)
                        insideCount++;
                }

                if (insideCount % 2 == 0) outers.Add(loops[i]);
                else holes.Add(loops[i]);
            }
        }

        internal static Point3d PointAtNormalized(Curve curve, double t01)
        {
            double t = curve.Domain.T0 + t01 * curve.Domain.Length;
            return curve.PointAt(t);
        }

        // -----------------------------------------------------------------
        // Shell window and offsets
        // -----------------------------------------------------------------

        internal static IEnumerable<Curve> ShellCenterlines(
            IEnumerable<Curve> outers,
            IEnumerable<Curve> holes,
            Plane plane,
            double width,
            int count,
            double tol,
            double minLen)
        {
            var result = new List<Curve>();
            var outerList = outers != null ? outers.ToList() : new List<Curve>();
            var holeList = holes != null ? holes.ToList() : new List<Curve>();
            var shellLoops = BuildShellLoopOffsetInfo(outerList, holeList, width, plane, tol);

            for (int k = 0; k < count; k++)
            {
                double distance = (k + 0.5) * width;

                foreach (var loop in shellLoops)
                    result.AddRange(OffsetInsideShellMaterial(loop, distance, outerList, holeList, plane, tol, minLen));
            }

            return result;
        }

        internal static void BuildShellWindow(
            IEnumerable<Curve> outers,
            IEnumerable<Curve> holes,
            Plane plane,
            double inset,
            double tol,
            out List<Curve> trimOu,
            out List<Curve> trimHo)
        {
            trimOu = new List<Curve>();
            trimHo = new List<Curve>();
            var outerList = outers != null ? outers.ToList() : new List<Curve>();
            var holeList = holes != null ? holes.ToList() : new List<Curve>();

            if (inset > RhinoMath.ZeroTolerance)
            {
                var shellLoops = BuildShellLoopOffsetInfo(outerList, holeList, inset, plane, tol);
                var outerInfos = shellLoops.Where(info => info.IsOuter);
                var holeInfos = shellLoops.Where(info => !info.IsOuter);

                foreach (var loop in outerInfos)
                    trimOu.AddRange(OffsetInsideShellMaterial(loop, inset, outerList, holeList, plane, tol, 0.0));

                foreach (var loop in holeInfos)
                    trimHo.AddRange(OffsetInsideShellMaterial(loop, inset, outerList, holeList, plane, tol, 0.0));
            }
            else
            {
                trimOu.AddRange(outerList);
                trimHo.AddRange(holeList);
            }
        }

        internal struct ShellLoopOffsetInfo
        {
            public Curve Curve;
            public int Sign;
            public bool IsOuter;

            public ShellLoopOffsetInfo(Curve curve, int sign, bool isOuter)
            {
                Curve = curve;
                Sign = sign;
                IsOuter = isOuter;
            }
        }

        internal static List<ShellLoopOffsetInfo> BuildShellLoopOffsetInfo(
            List<Curve> outers,
            List<Curve> holes,
            double distance,
            Plane plane,
            double tol)
        {
            var result = new List<ShellLoopOffsetInfo>();

            if (outers != null)
                foreach (var curve in outers)
                    result.Add(new ShellLoopOffsetInfo(
                        curve,
                        DetectMaterialOffsetSign(curve, distance, outers, holes, plane, tol),
                        true));

            if (holes != null)
                foreach (var curve in holes)
                    result.Add(new ShellLoopOffsetInfo(
                        curve,
                        DetectMaterialOffsetSign(curve, distance, outers, holes, plane, tol),
                        false));

            return result;
        }

        internal static int DetectMaterialOffsetSign(
            Curve curve,
            double distance,
            List<Curve> outers,
            List<Curve> holes,
            Plane plane,
            double tol)
        {
            if (curve == null || !curve.IsValid) return -1;

            double probe = Math.Max(tol * 10.0, Math.Min(Math.Abs(distance) * 0.05, Math.Abs(distance)));
            if (probe <= RhinoMath.ZeroTolerance) probe = Math.Max(tol * 10.0, RhinoMath.ZeroTolerance);

            if (OffsetHasSampleInShellMaterial(curve, probe, outers, holes, plane, tol)) return 1;
            if (OffsetHasSampleInShellMaterial(curve, -probe, outers, holes, plane, tol)) return -1;

            return -1;
        }

        internal static bool OffsetHasSampleInShellMaterial(
            Curve curve,
            double distance,
            List<Curve> outers,
            List<Curve> holes,
            Plane plane,
            double tol)
        {
            foreach (var offset in SafeOffset(curve, plane, distance, tol))
            {
                if (offset == null || !offset.IsValid || !offset.IsClosed) continue;

                Point3d sample = PointAtNormalized(offset, 0.5);
                if (InsideAny(sample, outers, plane, tol) && !InsideAny(sample, holes, plane, tol))
                    return true;
            }

            return false;
        }

        internal static IEnumerable<Curve> OffsetInsideShellMaterial(
            ShellLoopOffsetInfo loop,
            double distance,
            List<Curve> outers,
            List<Curve> holes,
            Plane plane,
            double tol,
            double minLen)
        {
            var result = new List<Curve>();
            Curve curve = loop.Curve;
            if (curve == null || !curve.IsValid || distance <= RhinoMath.ZeroTolerance)
                return result;

            foreach (var offset in SafeOffset(curve, plane, loop.Sign * distance, tol))
            {
                if (offset == null || !offset.IsValid || !offset.IsClosed) continue;
                if (minLen > 0.0 && offset.GetLength() < minLen) continue;

                Point3d sample = PointAtNormalized(offset, 0.5);
                if (InsideAny(sample, outers, plane, tol) && !InsideAny(sample, holes, plane, tol))
                    result.Add(offset);
            }

            return result;
        }

        internal static IEnumerable<Curve> SafeOffset(Curve curve, Plane plane, double distance, double tol)
        {
            var result = new List<Curve>();
            if (curve == null || !curve.IsValid) return result;

            Curve[] offsets;
            try
            {
                offsets = curve.Offset(plane, distance, tol, CurveOffsetCornerStyle.Sharp);
            }
            catch
            {
                offsets = null;
            }

            if (offsets == null || offsets.Length == 0) return result;

            var joined = Curve.JoinCurves(offsets, tol);
            if (joined == null || joined.Length == 0) joined = offsets;

            foreach (var joinedCurve in joined)
            {
                if (joinedCurve == null || !joinedCurve.IsValid) continue;

                if (!joinedCurve.IsClosed && joinedCurve.PointAtStart.DistanceTo(joinedCurve.PointAtEnd) <= tol)
                    joinedCurve.MakeClosed(tol);

                result.Add(joinedCurve);
            }

            return result;
        }

        internal static IEnumerable<Curve> CenteredContours(
            IEnumerable<Curve> bases,
            Plane plane,
            double width,
            int count,
            double tol,
            double minLen)
        {
            var result = new List<Curve>();
            if (bases == null) return result;

            if (width <= RhinoMath.ZeroTolerance || count <= 1)
            {
                foreach (var curve in bases)
                    if (curve != null && curve.IsValid && curve.GetLength() >= minLen)
                        result.Add(curve.DuplicateCurve());

                return result;
            }

            double n = count;

            foreach (var curve in bases)
            {
                if (curve == null || !curve.IsValid) continue;

                for (int k = 0; k < count; k++)
                {
                    double distance = (k - (n - 1.0) / 2.0) * width;

                    if (Math.Abs(distance) <= RhinoMath.ZeroTolerance)
                    {
                        if (curve.GetLength() >= minLen)
                            result.Add(curve.DuplicateCurve());
                    }
                    else
                    {
                        foreach (var offset in SafeOffset(curve, plane, distance, tol))
                            if (offset.GetLength() >= minLen)
                                result.Add(offset);
                    }
                }
            }

            return result;
        }

        // -----------------------------------------------------------------
        // Partition / infill trimming
        // -----------------------------------------------------------------

        internal static void SplitClosedAndOpenPartitionCurves(
            IEnumerable<Curve> curves,
            double tol,
            double minLen,
            out List<Curve> closed,
            out List<Curve> open)
        {
            closed = new List<Curve>();
            open = new List<Curve>();
            if (curves == null) return;

            foreach (var source in curves)
            {
                if (source == null || !source.IsValid) continue;
                if (source.GetLength() < minLen) continue;

                Curve curve = source.DuplicateCurve();
                if (!curve.IsClosed && curve.PointAtStart.DistanceTo(curve.PointAtEnd) <= tol)
                    curve.MakeClosed(tol);

                if (curve.IsClosed)
                    closed.Add(curve);
                else
                    open.Add(curve);
            }
        }

        internal static List<Curve> TrimCurvesToWindow(
            IEnumerable<Curve> source,
            List<Curve> outers,
            List<Curve> holes,
            Plane plane,
            double tol,
            double minLen)
        {
            var result = new List<Curve>();
            if (source == null) return result;

            var boundary = JoinLists(outers, holes);

            foreach (var curve in source)
            {
                var ts = CollectSplitParameters(curve, boundary, null, tol);
                var pieces = SplitByTs(curve, ts);

                foreach (var piece in pieces)
                {
                    if (piece == null || !piece.IsValid) continue;
                    if (piece.GetLength() < minLen) continue;

                    Point3d mid = PointAtNormalized(piece, 0.5);

                    if (InsideAny(mid, outers, plane, tol) && !InsideAny(mid, holes, plane, tol))
                        result.Add(piece);
                }
            }

            return result;
        }

        internal static bool AcceptInfillPiece(
            Curve curve,
            bool hasWindow,
            List<Curve> outers,
            List<Curve> holes,
            List<Curve> bands,
            Plane plane,
            double tol)
        {
            if (curve == null || !curve.IsValid) return false;

            Point3d mid = PointAtNormalized(curve, 0.5);

            if (hasWindow)
            {
                if (!InsideAny(mid, outers, plane, tol)) return false;
                if (InsideAny(mid, holes, plane, tol)) return false;
            }

            if (InsideAny(mid, bands, plane, tol)) return false;

            return true;
        }

        internal static List<Curve> BuildBandLoops(Curve curve, Plane plane, double halfWidth, double tol)
        {
            var result = new List<Curve>();
            if (curve == null || !curve.IsValid || halfWidth <= RhinoMath.ZeroTolerance) return result;

            var a = SafeOffset(curve, plane, -halfWidth, tol).ToList();
            var b = SafeOffset(curve, plane, halfWidth, tol).ToList();

            if (a.Count == 0 || b.Count == 0) return result;

            foreach (var ca in a)
            {
                Curve best = null;
                bool reverse = false;
                double bestScore = double.PositiveInfinity;

                foreach (var cb in b)
                {
                    double same = ca.PointAtStart.DistanceTo(cb.PointAtStart) + ca.PointAtEnd.DistanceTo(cb.PointAtEnd);
                    double rev = ca.PointAtStart.DistanceTo(cb.PointAtEnd) + ca.PointAtEnd.DistanceTo(cb.PointAtStart);

                    if (same < bestScore)
                    {
                        bestScore = same;
                        best = cb;
                        reverse = false;
                    }

                    if (rev < bestScore)
                    {
                        bestScore = rev;
                        best = cb;
                        reverse = true;
                    }
                }

                if (best == null) continue;

                var cbBest = best.DuplicateCurve();
                if (reverse) cbBest.Reverse();

                var pack = new List<Curve>
                {
                    ca.DuplicateCurve(),
                    cbBest
                };

                if (!ca.IsClosed && !cbBest.IsClosed)
                {
                    pack.Add(new LineCurve(ca.PointAtStart, cbBest.PointAtStart));
                    pack.Add(new LineCurve(ca.PointAtEnd, cbBest.PointAtEnd));
                }

                var joined = Curve.JoinCurves(pack, tol);
                if (joined == null) continue;

                foreach (var joinedCurve in joined)
                {
                    if (joinedCurve == null || !joinedCurve.IsValid) continue;

                    if (!joinedCurve.IsClosed && joinedCurve.PointAtStart.DistanceTo(joinedCurve.PointAtEnd) <= tol)
                        joinedCurve.MakeClosed(tol);

                    if (joinedCurve.IsClosed)
                        result.Add(joinedCurve);
                }
            }

            return result;
        }

        internal static void BuildCutters(
            bool hasWindow,
            List<Curve> outers,
            List<Curve> holes,
            List<Curve> bands,
            List<Curve> splitters,
            out List<Curve> cutters,
            out BoundingBox[] cutterBB)
        {
            cutters = new List<Curve>();

            if (hasWindow)
            {
                if (outers != null) cutters.AddRange(outers);
                if (holes != null) cutters.AddRange(holes);
            }

            if (bands != null) cutters.AddRange(bands);
            if (splitters != null) cutters.AddRange(splitters);

            cutterBB = new BoundingBox[cutters.Count];

            for (int i = 0; i < cutters.Count; i++)
                cutterBB[i] = cutters[i].GetBoundingBox(true);
        }

        internal static List<Curve> JoinLists(List<Curve> a, List<Curve> b)
        {
            var result = new List<Curve>();

            if (a != null) result.AddRange(a);
            if (b != null) result.AddRange(b);

            return result;
        }

        internal static List<double> CollectSplitParameters(
            Curve raw,
            List<Curve> cutters,
            BoundingBox[] cutterBB,
            double tol)
        {
            var ts = new List<double>();
            if (raw == null || cutters == null || cutters.Count == 0) return ts;

            BoundingBox rawBB = raw.GetBoundingBox(true);

            for (int i = 0; i < cutters.Count; i++)
            {
                var cutter = cutters[i];
                if (cutter == null || !cutter.IsValid) continue;

                if (cutterBB != null && cutterBB.Length == cutters.Count)
                    if (!BBoxOverlaps(rawBB, cutterBB[i], tol)) continue;

                var events = Intersection.CurveCurve(raw, cutter, tol, tol);
                if (events == null) continue;

                foreach (var intersectionEvent in events)
                {
                    if (intersectionEvent.IsPoint && IsInterior(raw.Domain, intersectionEvent.ParameterA, tol))
                        ts.Add(intersectionEvent.ParameterA);

                    if (intersectionEvent.IsOverlap && IsInterior(raw.Domain, intersectionEvent.ParameterA, tol))
                        ts.Add(intersectionEvent.ParameterA);
                }
            }

            ts.Sort();

            var dedup = new List<double>();
            double eps = Math.Max(1e-10, raw.Domain.Length * 1e-12);

            foreach (double t in ts)
                if (dedup.Count == 0 || Math.Abs(t - dedup[dedup.Count - 1]) > eps)
                    dedup.Add(t);

            return dedup;
        }

        internal static Curve[] SplitByTs(Curve raw, List<double> ts)
        {
            if (raw == null || !raw.IsValid) return new Curve[0];
            if (ts == null || ts.Count == 0) return new[] { raw.DuplicateCurve() };

            var split = raw.Split(ts);
            return split ?? new Curve[0];
        }

        internal static bool InsideAny(Point3d point, List<Curve> loops, Plane plane, double tol)
        {
            if (loops == null || loops.Count == 0) return false;

            foreach (var loop in loops)
            {
                if (loop == null || !loop.IsValid || !loop.IsClosed) continue;

                var containment = loop.Contains(point, plane, tol);
                if (containment == PointContainment.Inside || containment == PointContainment.Coincident)
                    return true;
            }

            return false;
        }

        internal static bool IsInterior(Interval domain, double t, double tol)
        {
            double eps = Math.Max(tol * 10.0, 1e-12 * Math.Max(1.0, Math.Abs(domain.Length)));
            return t > domain.T0 + eps && t < domain.T1 - eps;
        }

        internal static bool BBoxOverlaps(BoundingBox a, BoundingBox b, double tol)
        {
            return a.Min.X - tol <= b.Max.X && a.Max.X + tol >= b.Min.X &&
                   a.Min.Y - tol <= b.Max.Y && a.Max.Y + tol >= b.Min.Y &&
                   a.Min.Z - tol <= b.Max.Z && a.Max.Z + tol >= b.Min.Z;
        }
    }
}
