using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Grasshopper;
using Grasshopper.Kernel.Data;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

namespace WASPer_3DP
{
    internal sealed class WasperInfillConstraintReport
    {
        public int LayersProcessed;
        public int LayersSkipped;
        public int InfillBranchesTrimmed;
        public int InfillBranchesSplit;
        public int InfillBranchesRemoved;
        public int FragmentsCreated;
        public int OffsetFallbacks = 0;      // retained for component compatibility; offsets removed
        public int CandidateBranches;
        public int ShellsRepaired;           // self-intersecting displaced Shells repaired via boolean regions
        public int ShellsUnrepaired;         // repair failed; raw even-odd polyline classification used
        public int ShortFragmentsDropped;    // fragments below the printability floor
        public int GapsMerged;               // small-violation gaps kept to avoid confetti fragments
        public int EndsExtended;             // piece ends re-anchored to a receding Shell (option)
        public int FlowCompensatedPoints;    // samples with reduced flow instead of a cut (option)
        public long ElapsedMs;

        public bool Changed =>
            InfillBranchesTrimmed > 0 ||
            InfillBranchesSplit > 0 ||
            InfillBranchesRemoved > 0;

        public string Summary =>
            $"infill constraint: layers={LayersProcessed}, skipped={LayersSkipped}, " +
            $"trimmed={InfillBranchesTrimmed}, split={InfillBranchesSplit}, " +
            $"removed={InfillBranchesRemoved}, fragments={FragmentsCreated}, " +
            $"local candidates={CandidateBranches}, shells repaired={ShellsRepaired}, " +
            $"shells unrepaired={ShellsUnrepaired}, short fragments dropped={ShortFragmentsDropped}, " +
            $"gaps merged={GapsMerged}, ends extended={EndsExtended}, " +
            $"flow compensated pts={FlowCompensatedPoints}, elapsed_ms={ElapsedMs}";
    }

    /// <summary>
    /// Optional behavior switches. Defaults preserve deposition semantics:
    /// geometry is only ever trimmed, never extended, and flow is untouched.
    /// </summary>
    internal sealed class WasperInfillConstraintOptions
    {
        /// <summary>Extend uncut piece ends until they re-anchor on a Shell that receded outward.</summary>
        public bool ExtendToBond = false;

        /// <summary>Scale flow down across merged small-violation gaps instead of only tolerating them.</summary>
        public bool FlowCompensation = false;

        /// <summary>Largest tolerated clearance violation, as a fraction of the infill bead width, for gap merging.</summary>
        public double CompensationThreshold = 0.25;

        /// <summary>Fragments shorter than this factor times the infill bead width are dropped as unprintable.</summary>
        public double MinFragmentLengthFactor = 1.5;

        /// <summary>Gaps shorter than this factor times the infill bead width are candidates for merging.</summary>
        public double MergeGapFactor = 1.0;

        internal static readonly WasperInfillConstraintOptions Default = new WasperInfillConstraintOptions();
    }

    /// <summary>
    /// Clips planar/tilted-planar Infill centerlines to displaced Shell regions.
    /// The original shell topology associates each infill branch with its enclosing
    /// shell; the displaced shell supplies the final clipping boundary.
    ///
    /// Clearance is enforced with signed-distance queries against gridded Shell
    /// polylines instead of offset curves: a sample is valid iff it lies on the
    /// material side of every boundary with at least the bonded clearance to the
    /// Shell centerline. This is immune to the offset failures and offset
    /// self-crossings that fuzzy displaced Shells provoke, and evaluates every
    /// infill vertex instead of a single midpoint per piece.
    /// </summary>
    internal static class WasperInfillShellConstraint
    {
        private delegate double MarginEvaluator(Point3d point);

        private sealed class ShellPair
        {
            public GH_Path Path;
            public Curve Original;
            public Curve Moved;
            public Plane Plane;
            public double OriginalArea;
            public double ShellWidth;
            public bool Changed;
            public List<BoundingBox> ChangeZones;
            public ShellDistanceField OriginalField;
            public ShellDistanceField MovedField;
            public Point3d OriginalStart;
        }

        private struct BoundaryEval
        {
            public ShellDistanceField Field;
            public double Clearance;
            public bool KeepInside;
        }

        private sealed class CurrentShellBoundary
        {
            public ShellDistanceField Field;
            public Plane Plane;
            public double ShellWidth;
            public List<BoundingBox> ClosureZones;
        }

        private sealed class PieceResult
        {
            public List<Point3d> Points;
            public List<double> FlowScales; // null => all 1.0
        }

        private sealed class BranchWorkItem
        {
            public GH_Path Path;
            public int Layer;
            public IList<Plane> Planes;
            public Curve OriginalCurve;
            public Curve MovedCurve;
            public double InfillWidth;
            public List<ShellPair> LayerShells;
        }

        private sealed class BranchOutcome
        {
            public const int KindCopy = 0;
            public const int KindPieces = 1;
            public const int KindRemoved = 2;

            public int Kind = KindCopy;
            public bool MarkProcessed;
            public bool MarkSkipped;
            public bool Candidate;
            public List<PieceResult> Pieces;
            public int GapsMerged;
            public int ShortDropped;
            public int EndsExtended;
            public int FlowCompensatedPoints;
        }

        public static WasperPrintPath Apply(
            WasperPrintPath original,
            WasperPrintPath moved,
            double tolerance,
            out WasperInfillConstraintReport report,
            WasperInfillConstraintOptions options = null)
        {
            report = new WasperInfillConstraintReport();
            options = options ?? WasperInfillConstraintOptions.Default;
            if (original?.PtPlanes == null || moved?.PtPlanes == null ||
                moved.PathRoles == null || moved.PathRoles.BranchCount == 0)
                return moved;

            Stopwatch watch = Stopwatch.StartNew();
            tolerance = Math.Max(tolerance, 1e-9);
            List<GH_Path> allPaths = moved.PtPlanes.Paths.ToList();
            int commonPrefix = WasperGcodeTreeUtil.CommonPathPrefixLength(allPaths);

            // ----------------------------------------------------------------
            // Phase 1 (sequential): collect original/displaced Shell pairs per
            // layer, repair self-intersecting displaced Shells where possible,
            // and build the signed-distance fields used by every later query.
            // ----------------------------------------------------------------
            var shellsByLayer = new Dictionary<int, List<ShellPair>>();
            for (int branchIndex = 0; branchIndex < moved.PtPlanes.BranchCount; branchIndex++)
            {
                GH_Path path = moved.PtPlanes.Paths[branchIndex];
                if (RoleAt(moved.PathRoles, path) != (int)WasperPathRole.Shell)
                    continue;

                Curve originalCurve = CurveAt(original, path, tolerance);
                Curve movedCurve = CurveAt(moved, path, tolerance);
                if (originalCurve == null || movedCurve == null ||
                    !originalCurve.IsClosed || !movedCurve.IsClosed ||
                    !originalCurve.TryGetPlane(out Plane originalPlane, tolerance * 10.0) ||
                    !movedCurve.TryGetPlane(out Plane movedPlane, tolerance * 10.0))
                    continue;

                if (Math.Abs(originalPlane.ZAxis * movedPlane.ZAxis) < 0.999)
                    continue;

                Curve originalProjected = Curve.ProjectToPlane(originalCurve, movedPlane);
                Curve movedProjected = Curve.ProjectToPlane(movedCurve, movedPlane);
                double area = ClosedArea(originalProjected);
                if (originalProjected == null || movedProjected == null ||
                    !originalProjected.IsClosed || !movedProjected.IsClosed ||
                    area <= tolerance * tolerance)
                    continue;

                // Self-intersecting displaced Shells (typical with aggressive
                // fuzz) are repaired instead of skipped; when repair fails the
                // raw polyline still yields a usable even-odd classification,
                // so the layer keeps its constraint instead of silently
                // allowing protrusions.
                movedProjected = ResolveSelfIntersections(
                    movedProjected, movedPlane, tolerance, report);
                originalProjected = ResolveSelfIntersections(
                    originalProjected, movedPlane, tolerance, null);

                bool changed = TryChangedZones(
                    original.PtPlanes.PathExists(path)
                        ? original.PtPlanes.Branch(path)
                        : null,
                    moved.PtPlanes.Branch(path),
                    tolerance,
                    out List<BoundingBox> changeZones);

                ShellDistanceField originalField = ShellDistanceField.Create(
                    originalProjected, movedPlane, tolerance);
                ShellDistanceField movedField = changed
                    ? ShellDistanceField.Create(movedProjected, movedPlane, tolerance)
                    : originalField;
                if (originalField == null || movedField == null)
                    continue;

                int layer = WasperGcodeTreeUtil.LayerFromPath(path, commonPrefix);
                if (!shellsByLayer.TryGetValue(layer, out List<ShellPair> pairs))
                {
                    pairs = new List<ShellPair>();
                    shellsByLayer[layer] = pairs;
                }
                pairs.Add(new ShellPair
                {
                    Path = path,
                    Original = originalProjected,
                    Moved = movedProjected,
                    Plane = movedPlane,
                    OriginalArea = area,
                    ShellWidth = MedianAt(moved.LayerWf, path),
                    Changed = changed,
                    ChangeZones = changeZones,
                    OriginalField = originalField,
                    MovedField = movedField,
                    OriginalStart = originalProjected.PointAtStart
                });
            }

            // ----------------------------------------------------------------
            // Phase 2a (sequential): prefetch per-branch data so the parallel
            // sweep only touches plain, branch-local objects.
            // ----------------------------------------------------------------
            int branchCount = moved.PtPlanes.BranchCount;
            var workItems = new BranchWorkItem[branchCount];
            var outcomes = new BranchOutcome[branchCount];
            for (int branchIndex = 0; branchIndex < branchCount; branchIndex++)
            {
                GH_Path path = moved.PtPlanes.Paths[branchIndex];
                if (RoleAt(moved.PathRoles, path) != (int)WasperPathRole.Infill)
                {
                    outcomes[branchIndex] = new BranchOutcome();
                    continue;
                }

                int layer = WasperGcodeTreeUtil.LayerFromPath(path, commonPrefix);
                shellsByLayer.TryGetValue(layer, out List<ShellPair> layerShells);
                workItems[branchIndex] = new BranchWorkItem
                {
                    Path = path,
                    Layer = layer,
                    Planes = moved.PtPlanes.Branch(path),
                    OriginalCurve = CurveAt(original, path, tolerance),
                    MovedCurve = CurveAt(moved, path, tolerance),
                    InfillWidth = MedianAt(moved.LayerWf, path),
                    LayerShells = layerShells
                };
            }

            // ----------------------------------------------------------------
            // Phase 2b (parallel): per-branch clearance sweep.
            // ----------------------------------------------------------------
            Parallel.For(0, branchCount, branchIndex =>
            {
                if (workItems[branchIndex] != null)
                    outcomes[branchIndex] = EvaluateBranch(
                        workItems[branchIndex], tolerance, options);
            });

            // ----------------------------------------------------------------
            // Phase 3 (sequential): aggregate stats and assemble output trees.
            // ----------------------------------------------------------------
            var outPlanes = new DataTree<Plane>();
            var outFlows = NewTreeIf(moved.Flows);
            var outLayerH = NewTreeIf(moved.LayerH);
            var outSpeed = NewTreeIf(moved.PrintSpeed);
            var outLayerW = NewTreeIf(moved.LayerW);
            var outLayerWf = NewTreeIf(moved.LayerWf);
            var outCurves = new DataTree<Curve>();
            var outRoles = new DataTree<int>();
            DataTree<int> outStrokeIds = moved.HasStrokeIds ? new DataTree<int>() : null;
            var usedPaths = new HashSet<string>(allPaths.Select(PathKey));
            var processedLayers = new HashSet<int>();
            var skippedLayers = new HashSet<int>();

            for (int branchIndex = 0; branchIndex < branchCount; branchIndex++)
            {
                GH_Path path = moved.PtPlanes.Paths[branchIndex];
                BranchOutcome outcome = outcomes[branchIndex] ?? new BranchOutcome();
                BranchWorkItem item = workItems[branchIndex];

                if (outcome.Candidate) report.CandidateBranches++;
                if (item != null)
                {
                    if (outcome.MarkProcessed) processedLayers.Add(item.Layer);
                    if (outcome.MarkSkipped) skippedLayers.Add(item.Layer);
                }
                report.GapsMerged += outcome.GapsMerged;
                report.ShortFragmentsDropped += outcome.ShortDropped;
                report.EndsExtended += outcome.EndsExtended;
                report.FlowCompensatedPoints += outcome.FlowCompensatedPoints;

                if (outcome.Kind == BranchOutcome.KindCopy)
                {
                    CopyBranch(moved, path, path, outPlanes, outFlows, outLayerH, outSpeed,
                        outLayerW, outLayerWf, outCurves, outRoles, tolerance);
                    CopyStrokeId(moved, path, path, outStrokeIds);
                    continue;
                }

                report.InfillBranchesTrimmed++;
                if (outcome.Kind == BranchOutcome.KindRemoved ||
                    outcome.Pieces == null || outcome.Pieces.Count == 0)
                {
                    report.InfillBranchesRemoved++;
                    continue;
                }
                if (outcome.Pieces.Count > 1)
                    report.InfillBranchesSplit++;
                report.FragmentsCreated += outcome.Pieces.Count;

                IList<Plane> sourcePlanes = moved.PtPlanes.Branch(path);
                for (int pieceIndex = 0; pieceIndex < outcome.Pieces.Count; pieceIndex++)
                {
                    GH_Path outputPath = pieceIndex == 0
                        ? path
                        : UniquePiecePath(path, pieceIndex, usedPaths);
                    usedPaths.Add(PathKey(outputPath));
                    AppendPiece(
                        outcome.Pieces[pieceIndex].Points,
                        outcome.Pieces[pieceIndex].FlowScales,
                        sourcePlanes,
                        moved,
                        path,
                        outputPath,
                        outPlanes,
                        outFlows,
                        outLayerH,
                        outSpeed,
                        outLayerW,
                        outLayerWf,
                        outCurves,
                        outRoles,
                        tolerance);
                    CopyStrokeId(moved, path, outputPath, outStrokeIds);
                }
            }

            report.LayersProcessed = processedLayers.Count;
            report.LayersSkipped = skippedLayers.Count;
            watch.Stop();
            report.ElapsedMs = watch.ElapsedMilliseconds;
            if (!report.Changed)
                return moved;

            return new WasperPrintPath(
                points: null,
                ptPlanes: outPlanes,
                flows: outFlows,
                layerH: outLayerH,
                printSpeed: outSpeed,
                nozzleDiam: moved.NozzleDiam,
                layerW: outLayerW,
                layerWf: outLayerWf,
                printVol: null,
                travelSpeed: moved.TravelSpeed,
                zHop: moved.ZHop,
                zHopSpeed: moved.ZHopSpeed,
                isPartial: moved.IsPartial,
                sourceCurves: outCurves,
                pathRoles: outRoles,
                layerPlanes: moved.LayerPlanes,
                strokeIds: outStrokeIds);
        }

        /// <summary>
        /// Constrains Infill locally to newly closed Shell loops. Only Infill
        /// branches whose bounds meet a newly added closing segment are swept;
        /// unrelated sections pass through unchanged. Material regions use
        /// even-odd loop parity, supporting disjoint outer loops and nested holes
        /// on the same logical layer.
        /// </summary>
        public static WasperPrintPath ApplyCurrentShellUnion(
            WasperPrintPath moved,
            IList<GH_Path> newlyClosedShellPaths,
            double tolerance,
            out WasperInfillConstraintReport report,
            WasperInfillConstraintOptions options = null,
            IDictionary<GH_Path, IList<BoundingBox>> explicitClosureZones = null)
        {
            report = new WasperInfillConstraintReport();
            options = options ?? WasperInfillConstraintOptions.Default;
            if (moved?.PtPlanes == null || moved.PathRoles == null || moved.PathRoles.BranchCount == 0)
                return moved;

            var closurePathKeys = new HashSet<string>(
                (newlyClosedShellPaths ?? Array.Empty<GH_Path>())
                    .Where(path => path != null)
                    .Select(PathKey));
            if (closurePathKeys.Count == 0)
                return moved;

            Stopwatch watch = Stopwatch.StartNew();
            tolerance = Math.Max(tolerance, 1e-9);
            List<GH_Path> allPaths = moved.PtPlanes.Paths.ToList();
            int commonPrefix = WasperGcodeTreeUtil.CommonPathPrefixLength(allPaths);
            var boundariesByLayer = new Dictionary<int, List<CurrentShellBoundary>>();

            for (int branchIndex = 0; branchIndex < moved.PtPlanes.BranchCount; branchIndex++)
            {
                GH_Path path = moved.PtPlanes.Paths[branchIndex];
                if (!closurePathKeys.Contains(PathKey(path)) ||
                    RoleAt(moved.PathRoles, path) != (int)WasperPathRole.Shell)
                    continue;

                IList<Plane> shellPlanes = moved.PtPlanes.Branch(path);
                if (shellPlanes == null || shellPlanes.Count < 3)
                    continue;

                Curve shell = CurveAt(moved, path, tolerance);
                if (shell == null || !shell.IsClosed ||
                    !shell.TryGetPlane(out Plane plane, tolerance * 10.0))
                    continue;

                Curve projected = Curve.ProjectToPlane(shell, plane);
                if (projected == null || !projected.IsClosed || ClosedArea(projected) <= tolerance * tolerance)
                    continue;

                projected = ResolveSelfIntersections(projected, plane, tolerance, report);
                ShellDistanceField field = ShellDistanceField.Create(projected, plane, tolerance);
                if (field == null)
                    continue;

                var closureZones = new List<BoundingBox>();
                if (explicitClosureZones != null &&
                    explicitClosureZones.TryGetValue(path, out IList<BoundingBox> suppliedZones) &&
                    suppliedZones != null)
                {
                    closureZones.AddRange(suppliedZones.Where(zone => zone.IsValid));
                }
                if (closureZones.Count == 0)
                {
                    BoundingBox closureZone = BoundingBox.Empty;
                    closureZone.Union(shellPlanes[shellPlanes.Count - 2].Origin);
                    closureZone.Union(shellPlanes[shellPlanes.Count - 1].Origin);
                    if (closureZone.IsValid)
                        closureZones.Add(closureZone);
                }
                if (closureZones.Count == 0)
                    continue;

                int layer = WasperGcodeTreeUtil.LayerFromPath(path, commonPrefix);
                if (!boundariesByLayer.TryGetValue(layer, out List<CurrentShellBoundary> boundaries))
                {
                    boundaries = new List<CurrentShellBoundary>();
                    boundariesByLayer[layer] = boundaries;
                }
                boundaries.Add(new CurrentShellBoundary
                {
                    Field = field,
                    Plane = plane,
                    ShellWidth = MedianAt(moved.LayerWf, path),
                    ClosureZones = closureZones
                });
            }

            int branchCount = moved.PtPlanes.BranchCount;
            var outcomes = new BranchOutcome[branchCount];
            var workItems = new BranchWorkItem[branchCount];
            var boundarySets = new CurrentShellBoundary[branchCount][];
            for (int branchIndex = 0; branchIndex < branchCount; branchIndex++)
            {
                GH_Path path = moved.PtPlanes.Paths[branchIndex];
                if (RoleAt(moved.PathRoles, path) != (int)WasperPathRole.Infill)
                {
                    outcomes[branchIndex] = new BranchOutcome();
                    continue;
                }

                int layer = WasperGcodeTreeUtil.LayerFromPath(path, commonPrefix);
                boundariesByLayer.TryGetValue(layer, out List<CurrentShellBoundary> layerBoundaries);
                workItems[branchIndex] = new BranchWorkItem
                {
                    Path = path,
                    Layer = layer,
                    Planes = moved.PtPlanes.Branch(path),
                    InfillWidth = MedianAt(moved.LayerWf, path),
                    LayerShells = null
                };

                if (layerBoundaries == null || layerBoundaries.Count == 0)
                {
                    outcomes[branchIndex] = new BranchOutcome { MarkSkipped = true };
                    continue;
                }

                BoundingBox infillBounds = BranchBounds(workItems[branchIndex].Planes);
                bool touchesClosure = layerBoundaries.Any(boundary =>
                {
                    double clearance = BondedCenterlineClearance(
                        workItems[branchIndex].InfillWidth,
                        boundary.ShellWidth);
                    return boundary.ClosureZones != null &&
                        boundary.ClosureZones.Any(zone =>
                        {
                            BoundingBox affected = zone;
                            affected.Inflate(clearance + tolerance);
                            return BBoxOverlaps(infillBounds, affected, tolerance);
                        });
                });
                if (!touchesClosure)
                {
                    outcomes[branchIndex] = new BranchOutcome();
                    continue;
                }

                workItems[branchIndex].MovedCurve = CurveAt(moved, path, tolerance);
                if (workItems[branchIndex].MovedCurve == null)
                {
                    outcomes[branchIndex] = new BranchOutcome { MarkSkipped = true };
                    continue;
                }

                boundarySets[branchIndex] = layerBoundaries.ToArray();
            }

            Parallel.For(0, branchCount, branchIndex =>
            {
                BranchWorkItem item = workItems[branchIndex];
                CurrentShellBoundary[] boundaries = boundarySets[branchIndex];
                if (item == null || boundaries == null || boundaries.Length == 0 ||
                    outcomes[branchIndex] != null)
                    return;

                var outcome = new BranchOutcome { Candidate = true, MarkProcessed = true };
                MarginEvaluator marginAt = point => CurrentShellUnionMargin(
                    point,
                    boundaries,
                    item.InfillWidth);
                SweepBranch(
                    item.MovedCurve,
                    marginAt,
                    item.InfillWidth,
                    tolerance,
                    options,
                    outcome);
                outcomes[branchIndex] = outcome;
            });

            var outPlanes = new DataTree<Plane>();
            var outFlows = NewTreeIf(moved.Flows);
            var outLayerH = NewTreeIf(moved.LayerH);
            var outSpeed = NewTreeIf(moved.PrintSpeed);
            var outLayerW = NewTreeIf(moved.LayerW);
            var outLayerWf = NewTreeIf(moved.LayerWf);
            var outCurves = new DataTree<Curve>();
            var outRoles = new DataTree<int>();
            DataTree<int> currentOutStrokeIds = moved.HasStrokeIds ? new DataTree<int>() : null;
            var usedPaths = new HashSet<string>(allPaths.Select(PathKey));
            var processedLayers = new HashSet<int>();
            var skippedLayers = new HashSet<int>();

            for (int branchIndex = 0; branchIndex < branchCount; branchIndex++)
            {
                GH_Path path = moved.PtPlanes.Paths[branchIndex];
                BranchOutcome outcome = outcomes[branchIndex] ?? new BranchOutcome();
                BranchWorkItem item = workItems[branchIndex];
                if (outcome.Candidate) report.CandidateBranches++;
                if (item != null)
                {
                    if (outcome.MarkProcessed) processedLayers.Add(item.Layer);
                    if (outcome.MarkSkipped) skippedLayers.Add(item.Layer);
                }
                report.GapsMerged += outcome.GapsMerged;
                report.ShortFragmentsDropped += outcome.ShortDropped;
                report.EndsExtended += outcome.EndsExtended;
                report.FlowCompensatedPoints += outcome.FlowCompensatedPoints;

                if (outcome.Kind == BranchOutcome.KindCopy)
                {
                    CopyBranch(moved, path, path, outPlanes, outFlows, outLayerH, outSpeed,
                        outLayerW, outLayerWf, outCurves, outRoles, tolerance);
                    CopyStrokeId(moved, path, path, currentOutStrokeIds);
                    continue;
                }

                report.InfillBranchesTrimmed++;
                if (outcome.Kind == BranchOutcome.KindRemoved ||
                    outcome.Pieces == null || outcome.Pieces.Count == 0)
                {
                    report.InfillBranchesRemoved++;
                    continue;
                }
                if (outcome.Pieces.Count > 1)
                    report.InfillBranchesSplit++;
                report.FragmentsCreated += outcome.Pieces.Count;

                IList<Plane> sourcePlanes = moved.PtPlanes.Branch(path);
                for (int pieceIndex = 0; pieceIndex < outcome.Pieces.Count; pieceIndex++)
                {
                    GH_Path outputPath = pieceIndex == 0
                        ? path
                        : UniquePiecePath(path, pieceIndex, usedPaths);
                    usedPaths.Add(PathKey(outputPath));
                    AppendPiece(
                        outcome.Pieces[pieceIndex].Points,
                        outcome.Pieces[pieceIndex].FlowScales,
                        sourcePlanes,
                        moved,
                        path,
                        outputPath,
                        outPlanes,
                        outFlows,
                        outLayerH,
                        outSpeed,
                        outLayerW,
                        outLayerWf,
                        outCurves,
                        outRoles,
                        tolerance);
                    CopyStrokeId(moved, path, outputPath, currentOutStrokeIds);
                }
            }

            report.LayersProcessed = processedLayers.Count;
            report.LayersSkipped = skippedLayers.Count;
            watch.Stop();
            report.ElapsedMs = watch.ElapsedMilliseconds;
            if (!report.Changed)
                return moved;

            return new WasperPrintPath(
                points: null,
                ptPlanes: outPlanes,
                flows: outFlows,
                layerH: outLayerH,
                printSpeed: outSpeed,
                nozzleDiam: moved.NozzleDiam,
                layerW: outLayerW,
                layerWf: outLayerWf,
                printVol: null,
                travelSpeed: moved.TravelSpeed,
                zHop: moved.ZHop,
                zHopSpeed: moved.ZHopSpeed,
                isPartial: true,
                sourceCurves: outCurves,
                pathRoles: outRoles,
                layerPlanes: moved.LayerPlanes,
                strokeIds: currentOutStrokeIds);
        }

        private static double CurrentShellUnionMargin(
            Point3d point,
            IList<CurrentShellBoundary> boundaries,
            double infillWidth)
        {
            int containingLoops = 0;
            double nearestClearanceMargin = double.PositiveInfinity;
            for (int i = 0; i < (boundaries?.Count ?? 0); i++)
            {
                double signed = boundaries[i].Field.SignedDistance(point);
                if (signed >= 0.0)
                    containingLoops++;
                double clearance = BondedCenterlineClearance(
                    infillWidth,
                    boundaries[i].ShellWidth);
                nearestClearanceMargin = Math.Min(
                    nearestClearanceMargin,
                    Math.Abs(signed) - clearance);
            }

            if (!double.IsFinite(nearestClearanceMargin))
                return double.NegativeInfinity;
            return containingLoops % 2 == 1
                ? nearestClearanceMargin
                : -Math.Abs(nearestClearanceMargin);
        }

        private static void CopyStrokeId(
            WasperPrintPath source,
            GH_Path sourcePath,
            GH_Path outputPath,
            DataTree<int> output)
        {
            if (output == null)
                return;
            int strokeId = WasperGcodeTreeUtil.StrokeIdAt(source.StrokeIds, sourcePath);
            if (strokeId >= 0)
                output.Add(strokeId, outputPath);
        }

        // ====================================================================
        // Per-branch evaluation (thread-safe: only branch-local objects and
        // read-only shared fields are touched).
        // ====================================================================
        private static BranchOutcome EvaluateBranch(
            BranchWorkItem item,
            double tolerance,
            WasperInfillConstraintOptions options)
        {
            var outcome = new BranchOutcome();
            List<ShellPair> shellPairs = item.LayerShells;
            if (shellPairs == null || shellPairs.Count == 0)
            {
                outcome.MarkSkipped = true;
                return outcome;
            }

            List<ShellPair> changedShells = shellPairs
                .Where(shell => shell.Changed)
                .ToList();
            if (changedShells.Count == 0)
                return outcome;

            double infillWidth = item.InfillWidth;
            BoundingBox infillBounds = BranchBounds(item.Planes);
            bool touchesChangedZone = changedShells.Any(shell =>
            {
                double clearance = BondedCenterlineClearance(infillWidth, shell.ShellWidth);
                return shell.ChangeZones.Any(zone =>
                {
                    BoundingBox affected = zone;
                    affected.Inflate(clearance + tolerance);
                    return BBoxOverlaps(infillBounds, affected, tolerance);
                });
            });
            if (!touchesChangedZone)
                return outcome;

            outcome.Candidate = true;
            if (item.OriginalCurve == null || item.MovedCurve == null ||
                !TryRepresentative(item.OriginalCurve, out Point3d representative))
            {
                outcome.MarkSkipped = true;
                return outcome;
            }

            ShellPair container = FindSmallestContainer(shellPairs, representative, tolerance);
            if (container == null || !CurveNearPlane(item.MovedCurve, container.Plane, tolerance))
            {
                outcome.MarkSkipped = true;
                return outcome;
            }

            var boundaries = new List<BoundaryEval>
            {
                new BoundaryEval
                {
                    Field = container.MovedField,
                    Clearance = BondedCenterlineClearance(infillWidth, container.ShellWidth),
                    KeepInside = true
                }
            };

            foreach (ShellPair candidate in shellPairs)
            {
                if (ReferenceEquals(candidate, container))
                    continue;
                bool nestedInContainer =
                    container.OriginalField.SignedDistance(candidate.OriginalStart) >= -tolerance;
                bool representativeInsideCandidate =
                    candidate.OriginalField.SignedDistance(representative) >= -tolerance;
                if (!nestedInContainer || representativeInsideCandidate)
                    continue;

                boundaries.Add(new BoundaryEval
                {
                    Field = candidate.MovedField,
                    Clearance = BondedCenterlineClearance(infillWidth, candidate.ShellWidth),
                    KeepInside = false
                });
            }

            Curve projectedInfill = Curve.ProjectToPlane(item.MovedCurve, container.Plane);
            if (projectedInfill == null)
            {
                outcome.MarkSkipped = true;
                return outcome;
            }

            outcome.MarkProcessed = true;
            SweepBranch(projectedInfill, boundaries, infillWidth, tolerance, options, outcome);
            return outcome;
        }

        private static double Margin(Point3d point, List<BoundaryEval> boundaries)
        {
            double margin = double.PositiveInfinity;
            for (int i = 0; i < boundaries.Count; i++)
            {
                double signed = boundaries[i].Field.SignedDistance(point);
                double value = boundaries[i].KeepInside
                    ? signed - boundaries[i].Clearance
                    : -signed - boundaries[i].Clearance;
                if (value < margin)
                    margin = value;
            }
            return margin;
        }

        /// <summary>
        /// Signed-clearance sweep along the infill polyline: samples every
        /// vertex (densified to roughly half a bead width), keeps maximal valid
        /// runs with interpolated crossing endpoints, merges small-violation
        /// gaps, applies the printability floor, and optionally extends uncut
        /// ends to re-anchor on receding Shells.
        /// </summary>
        private static void SweepBranch(
            Curve projectedInfill,
            List<BoundaryEval> boundaries,
            double infillWidth,
            double tolerance,
            WasperInfillConstraintOptions options,
            BranchOutcome outcome)
        {
            SweepBranch(
                projectedInfill,
                point => Margin(point, boundaries),
                infillWidth,
                tolerance,
                options,
                outcome);
        }

        private static void SweepBranch(
            Curve projectedInfill,
            MarginEvaluator marginAt,
            double infillWidth,
            double tolerance,
            WasperInfillConstraintOptions options,
            BranchOutcome outcome)
        {
            List<Point3d> points = CurveToPoints(projectedInfill, tolerance);
            if (points.Count < 2)
                return; // Kind stays Copy: nothing usable to evaluate.

            bool closed = points.Count > 3 &&
                points[0].DistanceTo(points[points.Count - 1]) <= tolerance * 10.0;
            if (closed)
                points.RemoveAt(points.Count - 1);

            double maxSpacing = infillWidth > tolerance
                ? infillWidth * 0.5
                : tolerance * 100.0;

            var samples = new List<Point3d>();
            int vertexCount = points.Count;
            int segmentCount = closed ? vertexCount : vertexCount - 1;
            for (int i = 0; i < segmentCount; i++)
            {
                Point3d a = points[i];
                Point3d b = points[(i + 1) % vertexCount];
                samples.Add(a);
                double length = a.DistanceTo(b);
                int subdivisions = (int)Math.Floor(length / maxSpacing);
                for (int s = 1; s <= subdivisions; s++)
                {
                    double t = (double)s / (subdivisions + 1);
                    samples.Add(a + (b - a) * t);
                }
            }
            if (!closed)
                samples.Add(points[vertexCount - 1]);

            int n = samples.Count;
            var margins = new double[n];
            bool anyInvalid = false;
            for (int i = 0; i < n; i++)
            {
                margins[i] = marginAt(samples[i]);
                if (margins[i] < 0.0)
                    anyInvalid = true;
            }

            if (!anyInvalid)
                return; // fully valid: branch copied untouched.

            // For closed loops rotate the scan so it starts on an invalid
            // sample; runs then never wrap around the seam.
            int start = 0;
            if (closed)
            {
                for (int i = 0; i < n; i++)
                {
                    if (margins[i] < 0.0) { start = i; break; }
                }
            }

            int Index(int i) => (start + i) % n;
            int scanCount = closed ? n : n - start; // start == 0 for open curves.

            // Collect valid runs as sample-index ranges with optional
            // interpolated crossing points at cut ends.
            var runs = new List<Run>();
            Run current = null;
            for (int i = 0; i < scanCount; i++)
            {
                int index = Index(i);
                bool valid = margins[index] >= 0.0;
                if (valid && current == null)
                {
                    current = new Run { FirstSample = i };
                    if (i > 0 || closed)
                    {
                        int previous = Index((i - 1 + n) % n);
                        if (margins[previous] < 0.0)
                        {
                            current.StartCut = true;
                            current.StartPoint = CrossingPoint(
                                samples[previous], margins[previous],
                                samples[index], margins[index],
                                marginAt);
                        }
                    }
                }
                else if (!valid && current != null)
                {
                    current.LastSample = i - 1;
                    current.EndCut = true;
                    int lastIndex = Index(i - 1);
                    current.EndPoint = CrossingPoint(
                        samples[index], margins[index],
                        samples[lastIndex], margins[lastIndex],
                        marginAt);
                    runs.Add(current);
                    current = null;
                }
            }
            if (current != null)
            {
                current.LastSample = scanCount - 1;
                if (closed)
                {
                    // Scan started on an invalid sample, so a trailing open run
                    // ends against it.
                    current.EndCut = true;
                    current.EndPoint = CrossingPoint(
                        samples[Index(0)], margins[Index(0)],
                        samples[Index(scanCount - 1)], margins[Index(scanCount - 1)],
                        marginAt);
                }
                runs.Add(current);
            }

            // Merge consecutive runs across short, mildly violating gaps to
            // avoid confetti fragments; optionally compensate flow over the
            // merged span instead of cutting.
            double mergeGap = options.MergeGapFactor * infillWidth;
            double maxViolation = options.CompensationThreshold * infillWidth;
            var merged = new List<Run>();
            foreach (Run run in runs)
            {
                if (merged.Count == 0)
                {
                    merged.Add(run);
                    continue;
                }

                Run previous = merged[merged.Count - 1];
                double gapLength = 0.0;
                double worstViolation = 0.0;
                for (int i = previous.LastSample; i < run.FirstSample; i++)
                {
                    Point3d a = samples[Index(i)];
                    Point3d b = samples[Index(i + 1)];
                    gapLength += a.DistanceTo(b);
                    double margin = margins[Index(i)];
                    if (margin < 0.0 && -margin > worstViolation)
                        worstViolation = -margin;
                }

                if (infillWidth > tolerance &&
                    gapLength <= mergeGap &&
                    worstViolation <= maxViolation)
                {
                    int gapStart = previous.LastSample;
                    previous.LastSample = run.LastSample;
                    previous.EndCut = run.EndCut;
                    previous.EndPoint = run.EndPoint;
                    if (previous.CompensatedSamples == null)
                        previous.CompensatedSamples = new List<int>();
                    if (run.CompensatedSamples != null)
                        previous.CompensatedSamples.AddRange(run.CompensatedSamples);
                    for (int i = gapStart; i < run.FirstSample; i++)
                    {
                        if (margins[Index(i)] < 0.0)
                            previous.CompensatedSamples.Add(i);
                    }
                    outcome.GapsMerged++;
                }
                else
                {
                    merged.Add(run);
                }
            }

            // Build pieces, applying the printability floor.
            double minLength = infillWidth > tolerance
                ? options.MinFragmentLengthFactor * infillWidth
                : tolerance * 10.0;
            var pieces = new List<PieceResult>();
            foreach (Run run in merged)
            {
                var piecePoints = new List<Point3d>();
                List<double> flowScales = null;
                if (run.StartCut && run.StartPoint.IsValid)
                    piecePoints.Add(run.StartPoint);
                for (int i = run.FirstSample; i <= run.LastSample; i++)
                    piecePoints.Add(samples[Index(i)]);
                if (run.EndCut && run.EndPoint.IsValid)
                    piecePoints.Add(run.EndPoint);

                if (options.FlowCompensation &&
                    run.CompensatedSamples != null &&
                    run.CompensatedSamples.Count > 0 &&
                    infillWidth > tolerance)
                {
                    flowScales = Enumerable.Repeat(1.0, piecePoints.Count).ToList();
                    int offset = run.StartCut && run.StartPoint.IsValid ? 1 : 0;
                    foreach (int sampleScan in run.CompensatedSamples)
                    {
                        int local = sampleScan - run.FirstSample + offset;
                        if (local < 0 || local >= flowScales.Count)
                            continue;
                        double violation = -margins[Index(sampleScan)];
                        double scale = 1.0 - Math.Min(violation / infillWidth, 0.5);
                        flowScales[local] = Math.Min(flowScales[local], Math.Max(scale, 0.5));
                        outcome.FlowCompensatedPoints++;
                    }
                }

                double length = PolylineLength(piecePoints);
                if (length < minLength)
                {
                    outcome.ShortDropped++;
                    continue;
                }

                if (options.ExtendToBond && infillWidth > tolerance)
                {
                    if (!run.StartCut &&
                        TryExtendEnd(piecePoints, atStart: true, marginAt, infillWidth, out Point3d extendedStart))
                    {
                        piecePoints.Insert(0, extendedStart);
                        flowScales?.Insert(0, flowScales.Count > 0 ? flowScales[0] : 1.0);
                        outcome.EndsExtended++;
                    }
                    if (!run.EndCut &&
                        TryExtendEnd(piecePoints, atStart: false, marginAt, infillWidth, out Point3d extendedEnd))
                    {
                        piecePoints.Add(extendedEnd);
                        flowScales?.Add(flowScales.Count > 0 ? flowScales[flowScales.Count - 1] : 1.0);
                        outcome.EndsExtended++;
                    }
                }

                pieces.Add(new PieceResult { Points = piecePoints, FlowScales = flowScales });
            }

            outcome.Kind = pieces.Count == 0
                ? BranchOutcome.KindRemoved
                : BranchOutcome.KindPieces;
            outcome.Pieces = pieces;
        }

        private sealed class Run
        {
            public int FirstSample;
            public int LastSample;
            public bool StartCut;
            public bool EndCut;
            public Point3d StartPoint = Point3d.Unset;
            public Point3d EndPoint = Point3d.Unset;
            public List<int> CompensatedSamples;
        }

        /// <summary>Linear interpolation of the zero crossing, refined once via a field query.</summary>
        private static Point3d CrossingPoint(
            Point3d invalidPoint,
            double invalidMargin,
            Point3d validPoint,
            double validMargin,
            MarginEvaluator marginAt)
        {
            double denominator = validMargin - invalidMargin;
            if (Math.Abs(denominator) < 1e-12)
                return validPoint;
            double t = validMargin / denominator; // fraction from valid toward invalid
            Point3d crossing = validPoint + (invalidPoint - validPoint) * Math.Max(0.0, Math.Min(1.0, t));

            double refined = marginAt(crossing);
            if (refined >= 0.0)
            {
                double d2 = refined - invalidMargin;
                if (Math.Abs(d2) > 1e-12)
                {
                    double t2 = refined / d2;
                    crossing = crossing + (invalidPoint - crossing) * Math.Max(0.0, Math.Min(1.0, t2));
                }
            }
            else
            {
                double d2 = validMargin - refined;
                if (Math.Abs(d2) > 1e-12)
                {
                    double t2 = -refined / d2;
                    crossing = crossing + (validPoint - crossing) * Math.Max(0.0, Math.Min(1.0, t2));
                }
            }
            return crossing;
        }

        /// <summary>
        /// Steps an uncut piece end outward along its tangent while clearance
        /// margin remains positive, re-anchoring infill to a Shell that receded
        /// outward. Capped at two bead widths.
        /// </summary>
        private static bool TryExtendEnd(
            IReadOnlyList<Point3d> piecePoints,
            bool atStart,
            MarginEvaluator marginAt,
            double infillWidth,
            out Point3d extended)
        {
            extended = Point3d.Unset;
            if (piecePoints.Count < 2)
                return false;

            Point3d end = atStart ? piecePoints[0] : piecePoints[piecePoints.Count - 1];
            Point3d inner = atStart ? piecePoints[1] : piecePoints[piecePoints.Count - 2];
            Vector3d direction = end - inner;
            if (!direction.Unitize())
                return false;

            // Only extend when there is a meaningful gap to the boundary.
            if (marginAt(end) < 0.25 * infillWidth)
                return false;

            double step = 0.25 * infillWidth;
            double maxExtension = 2.0 * infillWidth;
            Point3d last = end;
            bool moved = false;
            for (double distance = step; distance <= maxExtension; distance += step)
            {
                Point3d candidate = end + direction * distance;
                if (marginAt(candidate) < 0.0)
                    break;
                last = candidate;
                moved = true;
            }

            if (!moved)
                return false;
            extended = last;
            return true;
        }

        private static List<Point3d> CurveToPoints(Curve curve, double tolerance)
        {
            var points = new List<Point3d>();
            if (curve == null)
                return points;
            if (!curve.TryGetPolyline(out Polyline polyline))
            {
                Curve converted = curve.ToPolyline(tolerance, tolerance, 0.0, 0.0);
                if (converted == null || !converted.TryGetPolyline(out polyline))
                    return points;
            }
            foreach (Point3d point in polyline)
            {
                if (points.Count == 0 ||
                    points[points.Count - 1].DistanceTo(point) > tolerance)
                    points.Add(point);
            }
            return points;
        }

        private static double PolylineLength(IReadOnlyList<Point3d> points)
        {
            double length = 0.0;
            for (int i = 1; i < points.Count; i++)
                length += points[i - 1].DistanceTo(points[i]);
            return length;
        }

        // ====================================================================
        // Shell repair
        // ====================================================================
        private static Curve ResolveSelfIntersections(
            Curve curve,
            Plane plane,
            double tolerance,
            WasperInfillConstraintReport report)
        {
            CurveIntersections selfIntersections = Intersection.CurveSelf(curve, tolerance);
            if (selfIntersections == null || selfIntersections.Count == 0)
                return curve;

            try
            {
                CurveBooleanRegions regions = Curve.CreateBooleanRegions(
                    new[] { curve }, plane, true, tolerance);
                if (regions != null && regions.RegionCount > 0)
                {
                    Curve best = null;
                    double bestArea = 0.0;
                    for (int i = 0; i < regions.RegionCount; i++)
                    {
                        Curve[] loops = regions.RegionCurves(i);
                        if (loops == null) continue;
                        foreach (Curve loop in loops)
                        {
                            if (loop == null || !loop.IsClosed) continue;
                            double area = ClosedArea(loop);
                            if (area > bestArea)
                            {
                                bestArea = area;
                                best = loop;
                            }
                        }
                    }

                    if (best != null)
                    {
                        CurveIntersections check = Intersection.CurveSelf(best, tolerance);
                        if (check == null || check.Count == 0)
                        {
                            if (report != null) report.ShellsRepaired++;
                            return best;
                        }
                    }
                }
            }
            catch
            {
                // Fall through to the raw polyline: even-odd classification on
                // the signed-distance field still behaves sensibly.
            }

            if (report != null) report.ShellsUnrepaired++;
            return curve;
        }

        // ====================================================================
        // Signed-distance field over a gridded 2D shell polyline.
        // Positive inside, negative outside. Immutable after construction, so
        // concurrent queries are safe.
        // ====================================================================
        private sealed class ShellDistanceField
        {
            private readonly Plane _plane;
            private readonly double[] _ax, _ay, _bx, _by;
            private readonly int _segmentCount;
            private readonly double _minX, _minY, _cellSize;
            private readonly int _cols, _rows;
            private readonly List<int>[] _cellSegments;
            private readonly List<int>[] _rowSegments;

            private ShellDistanceField(
                Plane plane,
                double[] ax, double[] ay, double[] bx, double[] by,
                double minX, double minY, double cellSize, int cols, int rows,
                List<int>[] cellSegments, List<int>[] rowSegments)
            {
                _plane = plane;
                _ax = ax; _ay = ay; _bx = bx; _by = by;
                _segmentCount = ax.Length;
                _minX = minX; _minY = minY; _cellSize = cellSize;
                _cols = cols; _rows = rows;
                _cellSegments = cellSegments;
                _rowSegments = rowSegments;
            }

            internal static ShellDistanceField Create(Curve loop, Plane plane, double tolerance)
            {
                List<Point3d> points = CurveToPoints(loop, tolerance);
                if (points.Count < 3)
                    return null;
                if (points[0].DistanceTo(points[points.Count - 1]) <= tolerance)
                    points.RemoveAt(points.Count - 1);
                if (points.Count < 3)
                    return null;

                int count = points.Count;
                var ax = new List<double>(count);
                var ay = new List<double>(count);
                var bx = new List<double>(count);
                var by = new List<double>(count);
                double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
                double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
                double totalLength = 0.0;

                var mapped = new (double X, double Y)[count];
                for (int i = 0; i < count; i++)
                {
                    plane.ClosestParameter(points[i], out double u, out double v);
                    mapped[i] = (u, v);
                    if (u < minX) minX = u;
                    if (u > maxX) maxX = u;
                    if (v < minY) minY = v;
                    if (v > maxY) maxY = v;
                }

                for (int i = 0; i < count; i++)
                {
                    (double X, double Y) a = mapped[i];
                    (double X, double Y) b = mapped[(i + 1) % count];
                    double dx = b.X - a.X;
                    double dy = b.Y - a.Y;
                    double length = Math.Sqrt(dx * dx + dy * dy);
                    if (length <= tolerance * 0.01)
                        continue;
                    ax.Add(a.X); ay.Add(a.Y); bx.Add(b.X); by.Add(b.Y);
                    totalLength += length;
                }
                if (ax.Count < 3)
                    return null;

                double width = Math.Max(maxX - minX, 1e-9);
                double height = Math.Max(maxY - minY, 1e-9);
                double averageSegment = totalLength / ax.Count;
                double cellSize = Math.Max(averageSegment, Math.Max(width, height) / 128.0);
                int cols = Math.Max(1, Math.Min(256, (int)Math.Ceiling(width / cellSize) + 1));
                int rows = Math.Max(1, Math.Min(256, (int)Math.Ceiling(height / cellSize) + 1));
                cellSize = Math.Max(width / cols, height / rows);
                cellSize = Math.Max(cellSize, 1e-9);

                var cellSegments = new List<int>[cols * rows];
                var rowSegments = new List<int>[rows];
                for (int i = 0; i < ax.Count; i++)
                {
                    double sMinX = Math.Min(ax[i], bx[i]);
                    double sMaxX = Math.Max(ax[i], bx[i]);
                    double sMinY = Math.Min(ay[i], by[i]);
                    double sMaxY = Math.Max(ay[i], by[i]);
                    int c0 = Clamp((int)((sMinX - minX) / cellSize), 0, cols - 1);
                    int c1 = Clamp((int)((sMaxX - minX) / cellSize), 0, cols - 1);
                    int r0 = Clamp((int)((sMinY - minY) / cellSize), 0, rows - 1);
                    int r1 = Clamp((int)((sMaxY - minY) / cellSize), 0, rows - 1);
                    for (int r = r0; r <= r1; r++)
                    {
                        (rowSegments[r] ?? (rowSegments[r] = new List<int>())).Add(i);
                        for (int c = c0; c <= c1; c++)
                        {
                            int cell = r * cols + c;
                            (cellSegments[cell] ?? (cellSegments[cell] = new List<int>())).Add(i);
                        }
                    }
                }

                return new ShellDistanceField(
                    plane,
                    ax.ToArray(), ay.ToArray(), bx.ToArray(), by.ToArray(),
                    minX, minY, cellSize, cols, rows, cellSegments, rowSegments);
            }

            internal double SignedDistance(Point3d worldPoint)
            {
                _plane.ClosestParameter(worldPoint, out double x, out double y);
                double distance = UnsignedDistance(x, y);
                return IsInside(x, y) ? distance : -distance;
            }

            private double UnsignedDistance(double x, double y)
            {
                int col = Clamp((int)((x - _minX) / _cellSize), 0, _cols - 1);
                int row = Clamp((int)((y - _minY) / _cellSize), 0, _rows - 1);
                double best = double.PositiveInfinity;
                int maxRing = Math.Max(_cols, _rows);

                for (int ring = 0; ring <= maxRing; ring++)
                {
                    if (best < double.PositiveInfinity &&
                        (ring - 1) * _cellSize > best)
                        break;

                    int c0 = col - ring, c1 = col + ring;
                    int r0 = row - ring, r1 = row + ring;
                    for (int r = r0; r <= r1; r++)
                    {
                        if (r < 0 || r >= _rows) continue;
                        bool edgeRow = r == r0 || r == r1;
                        for (int c = c0; c <= c1; c++)
                        {
                            if (c < 0 || c >= _cols) continue;
                            if (!edgeRow && c != c0 && c != c1) continue;
                            List<int> bucket = _cellSegments[r * _cols + c];
                            if (bucket == null) continue;
                            foreach (int segment in bucket)
                            {
                                double d = PointSegmentDistance(x, y, segment);
                                if (d < best) best = d;
                            }
                        }
                    }
                }

                if (double.IsPositiveInfinity(best))
                {
                    for (int segment = 0; segment < _segmentCount; segment++)
                    {
                        double d = PointSegmentDistance(x, y, segment);
                        if (d < best) best = d;
                    }
                }
                return best;
            }

            private double PointSegmentDistance(double x, double y, int segment)
            {
                double ax = _ax[segment], ay = _ay[segment];
                double dx = _bx[segment] - ax, dy = _by[segment] - ay;
                double lengthSquared = dx * dx + dy * dy;
                double t = lengthSquared > 1e-18
                    ? Math.Max(0.0, Math.Min(1.0, ((x - ax) * dx + (y - ay) * dy) / lengthSquared))
                    : 0.0;
                double px = ax + t * dx - x;
                double py = ay + t * dy - y;
                return Math.Sqrt(px * px + py * py);
            }

            private bool IsInside(double x, double y)
            {
                int row = (int)((y - _minY) / _cellSize);
                if (row < 0 || row >= _rows)
                    return false;
                List<int> bucket = _rowSegments[row];
                if (bucket == null)
                    return false;

                bool inside = false;
                foreach (int segment in bucket)
                {
                    double ay = _ay[segment], by = _by[segment];
                    if ((ay <= y) == (by <= y))
                        continue;
                    double ax = _ax[segment], bx = _bx[segment];
                    double crossX = ax + (y - ay) / (by - ay) * (bx - ax);
                    if (crossX > x)
                        inside = !inside;
                }
                return inside;
            }

            private static int Clamp(int value, int min, int max) =>
                value < min ? min : (value > max ? max : value);
        }

        // ====================================================================
        // Output assembly (unchanged semantics from the offset-based version).
        // ====================================================================
        private static void AppendPiece(
            List<Point3d> piecePoints,
            IList<double> flowScales,
            IList<Plane> sourcePlanes,
            WasperPrintPath source,
            GH_Path sourcePath,
            GH_Path outputPath,
            DataTree<Plane> planes,
            DataTree<double> flows,
            DataTree<double> layerH,
            DataTree<double> speed,
            DataTree<double> layerW,
            DataTree<double> layerWf,
            DataTree<Curve> curves,
            DataTree<int> roles,
            double tolerance)
        {
            var points = new List<Point3d>();
            var scales = new List<double>();
            for (int i = 0; i < piecePoints.Count; i++)
            {
                Point3d point = piecePoints[i];
                if (points.Count == 0 ||
                    points[points.Count - 1].DistanceTo(point) > tolerance)
                {
                    points.Add(point);
                    scales.Add(flowScales != null && i < flowScales.Count ? flowScales[i] : 1.0);
                }
            }
            if (points.Count < 2)
                return;

            planes.EnsurePath(outputPath);
            curves.Add(new PolylineCurve(points), outputPath);
            roles.Add((int)WasperPathRole.Infill, outputPath);

            for (int i = 0; i < points.Count; i++)
            {
                Point3d point = points[i];
                double scale = scales[i];
                ClosestSegment(sourcePlanes, point, out int segment, out double fraction);
                planes.Add(InterpolatePlane(sourcePlanes, segment, fraction, point), outputPath);
                AddSample(source.Flows, sourcePath, flows, outputPath, segment, fraction, sourcePlanes.Count, scale);
                AddSample(source.LayerH, sourcePath, layerH, outputPath, segment, fraction, sourcePlanes.Count);
                AddSample(source.PrintSpeed, sourcePath, speed, outputPath, segment, fraction, sourcePlanes.Count);
                AddSample(source.LayerW, sourcePath, layerW, outputPath, segment, fraction, sourcePlanes.Count);
                AddSample(source.LayerWf, sourcePath, layerWf, outputPath, segment, fraction, sourcePlanes.Count, scale);
            }
        }

        private static void ClosestSegment(
            IList<Plane> planes,
            Point3d point,
            out int segment,
            out double fraction)
        {
            segment = 0;
            fraction = 0.0;
            if (planes == null || planes.Count < 2)
                return;

            double best = double.PositiveInfinity;
            for (int i = 0; i < planes.Count - 1; i++)
            {
                Point3d a = planes[i].Origin;
                Point3d b = planes[i + 1].Origin;
                Vector3d ab = b - a;
                double lengthSquared = ab.SquareLength;
                double t = lengthSquared > 1e-18
                    ? Math.Max(0.0, Math.Min(1.0, ((point - a) * ab) / lengthSquared))
                    : 0.0;
                Point3d closest = a + t * ab;
                double distance = closest.DistanceToSquared(point);
                if (distance < best)
                {
                    best = distance;
                    segment = i;
                    fraction = t;
                }
            }
        }

        private static Plane InterpolatePlane(
            IList<Plane> planes,
            int segment,
            double fraction,
            Point3d origin)
        {
            if (planes == null || planes.Count == 0)
            {
                Plane fallback = Plane.WorldXY;
                fallback.Origin = origin;
                return fallback;
            }
            if (planes.Count == 1)
            {
                Plane only = planes[0];
                only.Origin = origin;
                return only;
            }

            int next = Math.Min(segment + 1, planes.Count - 1);
            Vector3d z = (1.0 - fraction) * planes[segment].ZAxis +
                         fraction * planes[next].ZAxis;
            Vector3d x = (1.0 - fraction) * planes[segment].XAxis +
                         fraction * planes[next].XAxis;
            if (!z.Unitize()) z = planes[segment].ZAxis;
            x -= (x * z) * z;
            if (!x.Unitize()) x = planes[segment].XAxis;
            Vector3d y = Vector3d.CrossProduct(z, x);
            if (!y.Unitize()) y = planes[segment].YAxis;
            return new Plane(origin, x, y);
        }

        private static void AddSample(
            DataTree<double> source,
            GH_Path sourcePath,
            DataTree<double> destination,
            GH_Path outputPath,
            int segment,
            double fraction,
            int planeCount,
            double scale = 1.0)
        {
            if (source == null || destination == null || !source.PathExists(sourcePath))
                return;
            IList<double> values = source.Branch(sourcePath);
            if (values == null || values.Count == 0)
                return;
            if (values.Count == 1)
            {
                destination.Add(values[0] * scale, outputPath);
                return;
            }

            double normalized = planeCount > 1
                ? (segment + fraction) / (planeCount - 1.0)
                : 0.0;
            double index = normalized * (values.Count - 1);
            int lower = Math.Max(0, Math.Min(values.Count - 1, (int)Math.Floor(index)));
            int upper = Math.Min(values.Count - 1, lower + 1);
            double local = index - lower;
            destination.Add(((1.0 - local) * values[lower] + local * values[upper]) * scale, outputPath);
        }

        private static ShellPair FindSmallestContainer(
            IEnumerable<ShellPair> shells,
            Point3d point,
            double tolerance)
        {
            return shells
                .Where(shell => shell.OriginalField.SignedDistance(point) >= -tolerance)
                .OrderBy(shell => shell.OriginalArea)
                .FirstOrDefault();
        }

        private static bool TryChangedZones(
            IList<Plane> original,
            IList<Plane> moved,
            double tolerance,
            out List<BoundingBox> zones)
        {
            zones = new List<BoundingBox>();
            if (original == null || moved == null ||
                original.Count == 0 || moved.Count == 0)
                return false;

            int count = Math.Min(original.Count, moved.Count);
            var changedSegments = new HashSet<int>();
            bool changedClosingSegment = false;
            for (int i = 0; i < count; i++)
            {
                if (original[i].Origin.DistanceTo(moved[i].Origin) > tolerance)
                {
                    if (i > 0) changedSegments.Add(i - 1);
                    if (i + 1 < count) changedSegments.Add(i);
                    if (i == 0 || i == count - 1)
                        changedClosingSegment = true;
                }
            }
            if (original.Count != moved.Count)
            {
                for (int i = 0; i < count - 1; i++)
                    changedSegments.Add(i);
            }
            if (changedSegments.Count == 0)
                return false;

            foreach (int segment in changedSegments.OrderBy(value => value))
            {
                BoundingBox zone = BoundingBox.Empty;
                zone.Union(original[segment].Origin);
                zone.Union(original[segment + 1].Origin);
                zone.Union(moved[segment].Origin);
                zone.Union(moved[segment + 1].Origin);
                if (zone.IsValid)
                    zones.Add(zone);
            }
            if (changedClosingSegment && count > 1)
            {
                BoundingBox closingZone = BoundingBox.Empty;
                closingZone.Union(original[count - 1].Origin);
                closingZone.Union(original[0].Origin);
                closingZone.Union(moved[count - 1].Origin);
                closingZone.Union(moved[0].Origin);
                if (closingZone.IsValid)
                    zones.Add(closingZone);
            }
            return zones.Count > 0;
        }

        private static BoundingBox BranchBounds(IList<Plane> planes)
        {
            BoundingBox bounds = BoundingBox.Empty;
            if (planes == null)
                return bounds;
            foreach (Plane plane in planes)
                bounds.Union(plane.Origin);
            return bounds;
        }

        private static bool BBoxOverlaps(
            BoundingBox a,
            BoundingBox b,
            double tolerance)
        {
            if (!a.IsValid || !b.IsValid)
                return false;
            return a.Min.X - tolerance <= b.Max.X &&
                   a.Max.X + tolerance >= b.Min.X &&
                   a.Min.Y - tolerance <= b.Max.Y &&
                   a.Max.Y + tolerance >= b.Min.Y &&
                   a.Min.Z - tolerance <= b.Max.Z &&
                   a.Max.Z + tolerance >= b.Min.Z;
        }

        private static bool CurveNearPlane(Curve curve, Plane plane, double tolerance)
        {
            BoundingBox box = curve.GetBoundingBox(true);
            double scale = Math.Max(box.Diagonal.Length, 1.0);
            double allowed = Math.Max(tolerance * 10.0, scale * 1e-6);
            double[] parameters = curve.DivideByCount(16, true);
            if (parameters == null || parameters.Length == 0)
                parameters = new[] { curve.Domain.Min, curve.Domain.Max };
            foreach (double parameter in parameters)
            {
                if (Math.Abs(plane.DistanceTo(curve.PointAt(parameter))) > allowed)
                    return false;
            }
            return true;
        }

        private static bool TryRepresentative(Curve curve, out Point3d point)
        {
            point = Point3d.Unset;
            if (curve == null || !curve.IsValid)
                return false;
            point = curve.PointAtNormalizedLength(0.5);
            return point.IsValid;
        }

        private static double ClosedArea(Curve curve)
        {
            if (curve == null || !curve.IsClosed)
                return 0.0;
            using (AreaMassProperties properties = AreaMassProperties.Compute(curve))
                return properties?.Area ?? 0.0;
        }

        private static Curve CurveAt(
            WasperPrintPath path,
            GH_Path treePath,
            double tolerance)
        {
            if (path?.SourceCurves != null && path.SourceCurves.PathExists(treePath))
            {
                IList<Curve> curves = path.SourceCurves.Branch(treePath);
                if (curves != null && curves.Count > 0 && curves[0] != null)
                    return curves[0].DuplicateCurve();
            }
            if (path?.PtPlanes == null || !path.PtPlanes.PathExists(treePath))
                return null;
            IList<Plane> planes = path.PtPlanes.Branch(treePath);
            if (planes == null || planes.Count < 2)
                return null;
            var points = planes.Select(plane => plane.Origin).ToList();
            return new PolylineCurve(points);
        }

        private static int RoleAt(DataTree<int> roles, GH_Path path)
        {
            if (roles == null || !roles.PathExists(path))
                return (int)WasperPathRole.Undefined;
            IList<int> branch = roles.Branch(path);
            return branch != null && branch.Count > 0
                ? branch[0]
                : (int)WasperPathRole.Undefined;
        }

        private static double MedianAt(DataTree<double> tree, GH_Path path)
        {
            if (tree == null || !tree.PathExists(path))
                return 0.0;
            List<double> values = tree.Branch(path)
                .Where(value => double.IsFinite(value) && value > 0.0)
                .OrderBy(value => value)
                .ToList();
            if (values.Count == 0) return 0.0;
            int middle = values.Count / 2;
            return values.Count % 2 == 1
                ? values[middle]
                : 0.5 * (values[middle - 1] + values[middle]);
        }

        private static double BondedCenterlineClearance(
            double infillWidth,
            double shellWidth)
        {
            if (!double.IsFinite(infillWidth) || infillWidth <= 0.0 ||
                !double.IsFinite(shellWidth) || shellWidth <= 0.0)
                return 0.0;

            // Keep half of the smaller bead embedded in the shell for bonding.
            return 0.5 * Math.Max(infillWidth, shellWidth);
        }

        private static DataTree<double> NewTreeIf(DataTree<double> source) =>
            source == null ? null : new DataTree<double>();

        private static void CopyBranch(
            WasperPrintPath source,
            GH_Path sourcePath,
            GH_Path outputPath,
            DataTree<Plane> planes,
            DataTree<double> flows,
            DataTree<double> layerH,
            DataTree<double> speed,
            DataTree<double> layerW,
            DataTree<double> layerWf,
            DataTree<Curve> curves,
            DataTree<int> roles,
            double tolerance)
        {
            planes.EnsurePath(outputPath);
            foreach (Plane plane in source.PtPlanes.Branch(sourcePath))
                planes.Add(plane, outputPath);
            CopyDouble(source.Flows, sourcePath, flows, outputPath);
            CopyDouble(source.LayerH, sourcePath, layerH, outputPath);
            CopyDouble(source.PrintSpeed, sourcePath, speed, outputPath);
            CopyDouble(source.LayerW, sourcePath, layerW, outputPath);
            CopyDouble(source.LayerWf, sourcePath, layerWf, outputPath);
            Curve curve = CurveAt(source, sourcePath, tolerance);
            if (curve != null) curves.Add(curve, outputPath);
            roles.Add(RoleAt(source.PathRoles, sourcePath), outputPath);
        }

        private static void CopyDouble(
            DataTree<double> source,
            GH_Path sourcePath,
            DataTree<double> destination,
            GH_Path outputPath)
        {
            if (source == null || destination == null || !source.PathExists(sourcePath))
                return;
            foreach (double value in source.Branch(sourcePath))
                destination.Add(value, outputPath);
        }

        private static GH_Path UniquePiecePath(
            GH_Path source,
            int pieceIndex,
            HashSet<string> used)
        {
            GH_Path candidate = source.AppendElement(pieceIndex);
            while (used.Contains(PathKey(candidate)))
                candidate = candidate.AppendElement(pieceIndex);
            return candidate;
        }

        private static string PathKey(GH_Path path) => path?.ToString() ?? string.Empty;
    }
}
