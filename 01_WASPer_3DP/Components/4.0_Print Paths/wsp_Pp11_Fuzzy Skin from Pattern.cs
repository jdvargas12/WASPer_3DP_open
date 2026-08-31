#region Component Description
/*
Component: wsp_Pp11_Fuzzy Skin from Pattern
Nickname: Pattern Fuzzy
Category: WASPer_3DP
SubCategory: 5.0_Gcode

Moves selected WASPer path-plane origins alternately to either side of the local
path tangent. Point, logical-layer, semantic-role, and optional closed-geometry
mask filters determine which locations move. Plane orientation and point-matched
process data are preserved.
*/
#endregion

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;
using Rhino;
using Rhino.Geometry;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using WASPer_3DP;

namespace WASPer_3DP_Components._4_0_Print_Paths
{
    public sealed class wsp_Pp11_Fuzzy_Skin_from_Pattern : GH_Component
    {
        private readonly string _versionTag;
        private bool _constrainInfill = true;
        private static readonly string[] OutputCatalog = WasperPathDebugOutputs.CoreNickNames;
        private static readonly int AllOutputsMask = (1 << OutputCatalog.Length) - 1;
        private int _visibleOutputsMask;
        private string _deformationCacheSignature = string.Empty;
        private WasperPrintPath _deformationCacheSource;
        private PatternCacheEntry _deformationCache;
        private const string ConstrainInfillKey = "wsp_gc17_constrain_infill";
        // Legacy all-or-nothing key, still read for backward compatibility with saved files.
        private const string ShowAllOutputsKey = "wsp_gc17_show_all_outputs";
        private const string VisibleOutputsMaskKey = "wsp_gc17_visible_outputs_mask";

        public wsp_Pp11_Fuzzy_Skin_from_Pattern()
            : base(
                "wsp_Pp11_Fuzzy Skin from Pattern",
                "Pattern Fuzzy",
                "Creates a deterministic fuzzy-skin path by moving selected path-plane origins " +
                "to opposite sides of the local path tangent. Pattern 0 moves in the local " +
                "tan + 90 degree direction (plane Z x tangent); pattern 1 moves in the opposite " +
                "direction. Point patterns repeat independently per path and layer patterns " +
                "repeat over logical layers. Optional pull geometry samples multi-value magnitude " +
                "profiles by normalized proximity, with one influence distance per object. " +
                "Semantic-role and closed-geometry masks further limit the operation. " +
                "By default, planar Infill is locally clipped only where changed closed-Shell " +
                "segments make previously valid paths fall outside the new boundary. " +
                "Plane orientation and point-matched process values are " +
                "preserved; geometry-dependent analysis, motion, KPI, and deposited-volume " +
                "fields are cleared and must be recomputed downstream.\r\n\r\n" +
                "Please use the Pp01 WASPer Path from Curves before using this component.\r\n\r\n"+
                "For Iyad Ghazal - TU Darmstadt",
                WASPerPalette.DesignFabrication,
                "4.0_Print Paths")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version == null
                ? "v1.0.x"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("B3BF9BE2-66D9-41A4-B0F1-E3F09C054506");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon => FuzzySkinIcon.Bitmap;

        protected override void AppendAdditionalComponentMenuItems(
            System.Windows.Forms.ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(
                menu,
                "Constrain infill to modified Shell",
                (sender, args) =>
                {
                    RecordUndoEvent("Toggle infill constraint");
                    _constrainInfill = !_constrainInfill;
                    ExpireSolution(true);
                },
                true,
                _constrainInfill);
            Menu_AppendSeparator(menu);
            WasperPathDebugOutputs.AppendOutputVisibilityMenu(
                this,
                menu,
                "Debug Outputs",
                OutputCatalog,
                () => _visibleOutputsMask,
                mask =>
                {
                    RecordUndoEvent("Toggle Pp11 debug outputs");
                    _visibleOutputsMask = mask;
                    WasperPathDebugOutputs.Rebuild(
                        this,
                        _visibleOutputsMask,
                        "Fuzzy-skin displacement, filtering, safety, and Infill-constraint report.",
                        OutputCatalog);
                    ExpireSolution(true);
                });
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetBoolean(ConstrainInfillKey, _constrainInfill);
            writer.SetInt32(VisibleOutputsMaskKey, _visibleOutputsMask);
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            // Rebuild the output topology BEFORE base.Read() restores saved wires, so the
            // optional outputs already exist when Grasshopper reconnects them (otherwise
            // saved wires to them are silently dropped on file open).
            //
            // _visibleOutputsMask migration: files saved before per-output toggles existed only
            // have the legacy boolean ShowAllOutputsKey. Map "Show all outputs" = true to every
            // bit set, so old files keep showing everything they used to.
            _constrainInfill = !reader.ItemExists(ConstrainInfillKey) ||
                               reader.GetBoolean(ConstrainInfillKey);
            if (reader.ItemExists(VisibleOutputsMaskKey))
                _visibleOutputsMask = reader.GetInt32(VisibleOutputsMaskKey);
            else if (reader.ItemExists(ShowAllOutputsKey) && reader.GetBoolean(ShowAllOutputsKey))
                _visibleOutputsMask = AllOutputsMask;
            else
                _visibleOutputsMask = 0;
            WasperPathDebugOutputs.Rebuild(
                this,
                _visibleOutputsMask,
                "Fuzzy-skin displacement, filtering, safety, and Infill-constraint report.",
                OutputCatalog);
            return base.Read(reader);
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "WASPer Print Path whose canonical pt_planes will be displaced. Plane origins " +
                "move while plane orientation and point-matched process values are preserved. Please use the Pp01 WASPer Path from Curves before using this component.",
                GH_ParamAccess.item);

            int pointPatternIndex = p.AddIntegerParameter(
                "point pattern",
                "pt_pattern",
                "Optional repeating 0/1 pattern applied independently along every path branch. " +
                "0 moves tan + 90 degrees using mag_0; 1 moves tan - 90 degrees using mag_1. " +
                "Default: [0, 1].",
                GH_ParamAccess.list);
            p[pointPatternIndex].Optional = true;

            int layerPatternIndex = p.AddIntegerParameter(
                "layer pattern",
                "la_pattern",
                "Optional repeating logical-layer pattern. 0 leaves the layer unchanged and " +
                "1 enables fuzzy skin. The pattern follows the ordered logical layer stack, " +
                "including trees with grafted prefixes. Default: [1] (all layers).",
                GH_ParamAccess.list);
            p[layerPatternIndex].Optional = true;

            int magnitude0Index = p.AddNumberParameter(
                "magnitude 0",
                "mag_0",
                "Signed displacement profile in model units for point-pattern value 0. " +
                "One value gives a constant global magnitude. Without pull_geo, multiple values " +
                "repeat over ordered logical layers. With pull_geo, multiple values are evenly " +
                "interpolated from u=0 on the closest object to u=1 at its pull_dist. " +
                "Positive values use local tan + 90 degrees, defined as plane.ZAxis x tangent; " +
                "negative values reverse that direction. " +
                "Default: [0].",
                GH_ParamAccess.list);
            p[magnitude0Index].Optional = true;

            int magnitude1Index = p.AddNumberParameter(
                "magnitude 1",
                "mag_1",
                "Signed displacement profile in model units for point-pattern value 1. " +
                "One value gives a constant global magnitude. Without pull_geo, multiple values " +
                "repeat over ordered logical layers. With pull_geo, multiple values are evenly " +
                "interpolated from u=0 on the closest object to u=1 at its pull_dist. " +
                "Positive values use local tan - 90 degrees; negative values reverse that direction. " +
                "Default: [0].",
                GH_ParamAccess.list);
            p[magnitude1Index].Optional = true;

            p.AddParameter(global::WASPer_3DP.WasperTargetRolesParam.Create(
                "Selects which semantic path branches receive fuzzy skin. 0 = All paths (default), " +
                "1 = Shell, 2 = Infill, 3 = Partition, 4 = Support, 5 = Transition, 6 = Undefined. Supply several role-specific " +
                "values to include them and exclude the others. All paths (0) cannot be combined. " +
                "Undefined/legacy branches can be selected explicitly with 4.",
                0));

            int pullGeometryIndex = p.AddGeometryParameter(
                "pull geometry",
                "pull_geo",
                "Optional geometry objects that control the magnitude profiles by proximity. " +
                "Points, lines, open/closed curves, surfaces, meshes, Breps, and Extrusions are " +
                "supported. For each path point, the object with the smallest normalized distance " +
                "(distance / its paired pull_dist) controls profile sampling.",
                GH_ParamAccess.list);
            p[pullGeometryIndex].Optional = true;

            int pullDistanceIndex = p.AddNumberParameter(
                "pull distance",
                "pull_dist",
                "Positive influence distance in model units for each pull_geo object. One value " +
                "broadcasts to every object; matching counts pair item by item; other list " +
                "lengths repeat across pull_geo with a grey notice. Profile " +
                "coordinate u is 0 on the object and 1 at or beyond this distance. Default: 10.",
                GH_ParamAccess.list,
                10.0);

            int maskIndex = p.AddGeometryParameter(
                "mask",
                "mask",
                "Optional closed geometry used to filter points. Supported masks are closed " +
                "planar curves (tested by planar projection), solid Breps/Extrusions, and " +
                "closed meshes. Multiple masks act as a union. With no mask, every otherwise " +
                "eligible point may move.",
                GH_ParamAccess.list);
            p[maskIndex].Optional = true;

            p.AddBooleanParameter(
                "invert",
                "invert",
                "Invert the optional mask selection. False modifies points inside the mask; " +
                "True modifies points outside it. Has no effect when mask is not supplied.",
                GH_ParamAccess.item,
                false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "WASPer Print Path with displaced pt_plane origins. Tree topology, plane " +
                "orientation, roles, and point-matched process values are preserved. Exact " +
                "source-curve provenance is rebuilt from the resulting paths.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "summary",
                "summary",
                "Operation summary including filters, moved/skipped points, displacement, " +
                "large-magnitude warnings, and cleared downstream data.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            var totalWatch = Stopwatch.StartNew();
            Message = _versionTag;

            if (!WasperGcodeTreeUtil.TryGetPrintPath(da, 0, out WasperPrintPath source) ||
                source == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Supply a valid wsp_path. Please use the Pp01 WASPer Path from Curves before using this component.");
                return;
            }

            if (!source.HasPlanes || source.PtPlanes.DataCount == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "wsp_path does not contain canonical pt_planes. Please use the Pp01 WASPer Path from Curves before using this component.");
                return;
            }

            var pointPattern = new List<int>();
            da.GetDataList(1, pointPattern);
            if (pointPattern.Count == 0)
                pointPattern.AddRange(new[] { 0, 1 });
            if (pointPattern.Any(value => value != 0 && value != 1))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "pt_pattern accepts only 0 and 1.");
                return;
            }

            var layerPattern = new List<int>();
            da.GetDataList(2, layerPattern);
            if (layerPattern.Count == 0)
                layerPattern.Add(1);
            if (layerPattern.Any(value => value != 0 && value != 1))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "la_pattern accepts only 0 and 1.");
                return;
            }

            var magnitude0 = new List<double>();
            var magnitude1 = new List<double>();
            da.GetDataList(3, magnitude0);
            da.GetDataList(4, magnitude1);
            if (magnitude0.Count == 0)
                magnitude0.Add(0.0);
            if (magnitude1.Count == 0)
                magnitude1.Add(0.0);
            if (magnitude0.Any(value => !IsFiniteMagnitude(value)) ||
                magnitude1.Any(value => !IsFiniteMagnitude(value)))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Every mag_0 and mag_1 profile value must be finite. Positive values follow " +
                    "the pt_pattern direction; negative values reverse it.");
                return;
            }

            var targetRoles = new List<int>();
            da.GetDataList(5, targetRoles);
            if (!WasperGcodeTreeUtil.TryNormalizeTargetRoles(
                    targetRoles,
                    out targetRoles,
                    out string roleError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, roleError);
                return;
            }

            var rawPullGeometry = new List<IGH_GeometricGoo>();
            var pullDistances = new List<double>();
            da.GetDataList(6, rawPullGeometry);
            da.GetDataList(7, pullDistances);

            if (rawPullGeometry.Count > 0)
            {
                if (pullDistances.Count == 0)
                    pullDistances.Add(10.0);

                if (pullDistances.Count != 1 &&
                    pullDistances.Count != rawPullGeometry.Count)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Remark,
                        $"pull_dist contains {pullDistances.Count} values for {rawPullGeometry.Count} pull_geo objects. " +
                        "The distance list is repeated across the objects.");
                }

                if (pullDistances.Any(value => !double.IsFinite(value) || value <= 0.0))
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        "Every pull_dist value must be positive and finite.");
                    return;
                }
            }
            List<PullRegion> pullRegions = BuildPullRegions(
                rawPullGeometry,
                pullDistances,
                out int rejectedPullGeometry);
            if (rawPullGeometry.Count > 0 && pullRegions.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "pull_geo contains no supported valid geometry.");
                return;
            }
            if (rejectedPullGeometry > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{rejectedPullGeometry} unsupported or invalid pull_geo item(s) were ignored.");
            }

            var rawMasks = new List<GeometryBase>();
            da.GetDataList(8, rawMasks);
            bool invert = false;
            da.GetData(9, ref invert);

            double tolerance = Math.Max(
                RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001,
                1e-9);

            List<MaskRegion> masks = BuildMasks(rawMasks, tolerance, out int rejectedMasks);
            if (rawMasks.Count > 0 && masks.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "mask contains no supported closed geometry. Use closed planar curves, solid Breps/Extrusions, or closed meshes.");
                return;
            }
            if (rejectedMasks > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{rejectedMasks} unsupported, open, or invalid mask item(s) were ignored.");
            }

            List<GH_Path> paths = source.PtPlanes.Paths.ToList();
            int commonPrefix = WasperGcodeTreeUtil.CommonPathPrefixLength(paths);
            List<int> orderedLayers = paths
                .Select(path => WasperGcodeTreeUtil.LayerFromPath(path, commonPrefix))
                .Distinct()
                .OrderBy(layer => layer)
                .ToList();
            var layerOrdinals = orderedLayers
                .Select((layer, ordinal) => new { layer, ordinal })
                .ToDictionary(item => item.layer, item => item.ordinal);

            var outputPlanes = new DataTree<Plane>();
            int targetedBranches = 0;
            int roleExcluded = 0;
            int layerExcluded = 0;
            int maskExcluded = 0;
            int moved = 0;
            int zeroMagnitude = 0;
            int tangentFailures = 0;
            int largeMagnitudeMoves = 0;
            double maximumDisplacement = 0.0;
            string deformationSignature = BuildDeformationCacheSignature(
                source,
                targetRoles,
                pointPattern,
                layerPattern,
                magnitude0,
                magnitude1,
                rawPullGeometry,
                pullDistances,
                rawMasks,
                invert,
                tolerance);
            DataTree<Plane> cachedOutputPlanes = null;
            bool deformationCacheHit =
                _deformationCache != null &&
                string.Equals(
                    deformationSignature,
                    _deformationCacheSignature,
                    StringComparison.Ordinal) &&
                WasperRoleBranchTransformCache.TryRestore(
                    source,
                    _deformationCacheSource,
                    _deformationCache.Planes,
                    targetRoles,
                    out cachedOutputPlanes);

            if (deformationCacheHit)
            {
                outputPlanes = cachedOutputPlanes;
                targetedBranches = _deformationCache.TargetedBranches;
                roleExcluded = _deformationCache.RoleExcluded;
                layerExcluded = _deformationCache.LayerExcluded;
                maskExcluded = _deformationCache.MaskExcluded;
                moved = _deformationCache.Moved;
                zeroMagnitude = _deformationCache.ZeroMagnitude;
                tangentFailures = _deformationCache.TangentFailures;
                largeMagnitudeMoves = _deformationCache.LargeMagnitudeMoves;
                maximumDisplacement = _deformationCache.MaximumDisplacement;
            }

            if (!deformationCacheHit)
            for (int branchIndex = 0; branchIndex < source.PtPlanes.BranchCount; branchIndex++)
            {
                GH_Path path = source.PtPlanes.Paths[branchIndex];
                IList<Plane> incoming = source.PtPlanes.Branches[branchIndex];
                outputPlanes.EnsurePath(path);

                bool roleMatch = WasperGcodeTreeUtil.MatchesTargetRoles(
                    source.PathRoles,
                    path,
                    targetRoles);
                if (!roleMatch)
                {
                    roleExcluded += incoming?.Count ?? 0;
                    AppendPlanes(outputPlanes, path, incoming);
                    continue;
                }
                targetedBranches++;

                int layer = WasperGcodeTreeUtil.LayerFromPath(path, commonPrefix);
                int layerOrdinal = layerOrdinals.TryGetValue(layer, out int ordinal)
                    ? ordinal
                    : 0;
                bool layerEnabled = layerPattern[layerOrdinal % layerPattern.Count] == 1;
                if (!layerEnabled)
                {
                    layerExcluded += incoming?.Count ?? 0;
                    AppendPlanes(outputPlanes, path, incoming);
                    continue;
                }

                if (incoming == null || incoming.Count == 0)
                    continue;

                bool duplicateClosure = incoming.Count > 2 &&
                    incoming[0].Origin.DistanceTo(incoming[incoming.Count - 1].Origin) <= tolerance;
                bool sourceClosed = SourceCurveIsClosed(source.SourceCurves, path);
                bool cyclic = sourceClosed || duplicateClosure;
                int logicalCount = duplicateClosure ? incoming.Count - 1 : incoming.Count;

                var movedBranch = new List<Plane>(incoming.Count);
                for (int pointIndex = 0; pointIndex < logicalCount; pointIndex++)
                {
                    Plane plane = incoming[pointIndex];
                    Point3d point = plane.Origin;

                    bool maskMatch = masks.Count == 0 || masks.Any(mask => mask.Contains(point));
                    if (masks.Count > 0 && invert)
                        maskMatch = !maskMatch;
                    if (!maskMatch)
                    {
                        maskExcluded++;
                        movedBranch.Add(plane);
                        continue;
                    }

                    int patternValue = pointPattern[pointIndex % pointPattern.Count];
                    IList<double> magnitudeProfile = patternValue == 0
                        ? magnitude0
                        : magnitude1;
                    double magnitude;
                    if (pullRegions.Count == 0)
                    {
                        magnitude = magnitudeProfile[
                            layerOrdinal % magnitudeProfile.Count];
                    }
                    else
                    {
                        double pullCoordinate = PullCoordinate(point, pullRegions);
                        magnitude = EvaluateProfile(
                            magnitudeProfile,
                            pullCoordinate);
                    }
                    double absoluteMagnitude = Math.Abs(magnitude);
                    if (absoluteMagnitude <= tolerance * 1e-6)
                    {
                        zeroMagnitude++;
                        movedBranch.Add(plane);
                        continue;
                    }

                    Vector3d tangent = LocalTangent(incoming, pointIndex, logicalCount, cyclic);
                    Vector3d side = Vector3d.CrossProduct(plane.ZAxis, tangent);
                    if (!tangent.Unitize() || !side.Unitize())
                    {
                        tangentFailures++;
                        movedBranch.Add(plane);
                        continue;
                    }

                    if (patternValue == 1)
                        side.Reverse();

                    double localLength = LocalShortestSegment(
                        incoming,
                        pointIndex,
                        logicalCount,
                        cyclic);
                    if (localLength > tolerance && absoluteMagnitude > localLength * 0.5)
                        largeMagnitudeMoves++;

                    plane.Origin = point + side * magnitude;
                    movedBranch.Add(plane);
                    moved++;
                    maximumDisplacement = Math.Max(maximumDisplacement, absoluteMagnitude);
                }

                if (duplicateClosure)
                {
                    Plane closingPlane = incoming[incoming.Count - 1];
                    if (movedBranch.Count > 0)
                        closingPlane.Origin = movedBranch[0].Origin;
                    movedBranch.Add(closingPlane);
                    if (closingPlane.Origin.DistanceTo(incoming[incoming.Count - 1].Origin) > tolerance)
                        moved++;
                }

                foreach (Plane plane in movedBranch)
                    outputPlanes.Add(plane, path);
            }

            if (!deformationCacheHit)
            {
                _deformationCacheSignature = deformationSignature;
                _deformationCacheSource = source;
                _deformationCache = new PatternCacheEntry
                {
                    Planes = outputPlanes,
                    TargetedBranches = targetedBranches,
                    RoleExcluded = roleExcluded,
                    LayerExcluded = layerExcluded,
                    MaskExcluded = maskExcluded,
                    Moved = moved,
                    ZeroMagnitude = zeroMagnitude,
                    TangentFailures = tangentFailures,
                    LargeMagnitudeMoves = largeMagnitudeMoves,
                    MaximumDisplacement = maximumDisplacement
                };
            }

            if (targetedBranches == 0)
            {
                int undefinedBranches = source.PtPlanes.Paths.Count(path =>
                    WasperGcodeTreeUtil.PathRoleAt(source.PathRoles, path) ==
                    WasperPathRole.Undefined);
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"No {WasperGcodeTreeUtil.TargetRoleNames(targetRoles)} branches were found. " +
                    "The input wsp_path passes through unchanged." +
                    (undefinedBranches > 0 &&
                     !WasperGcodeTreeUtil.TargetsAllRoles(targetRoles) &&
                     !targetRoles.Contains(4)
                        ? $" {undefinedBranches} branch(es) are Undefined; use Sl08 before Pp01 " +
                          "or Pp15 Assign Print Path Roles afterward if they require semantic targeting."
                        : string.Empty));
                WasperPathDebugOutputs.Set(
                    da,
                    this,
                    source,
                    $"Pattern fuzzy skin | target_roles={WasperGcodeTreeUtil.TargetRoleNames(targetRoles)} | " +
                    $"targeted branches=0 | unchanged");
                return;
            }

            if (largeMagnitudeMoves > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{largeMagnitudeMoves} moved location(s) use a magnitude greater than half a neighbouring " +
                    "segment length. Review the result for folded or crossing segments.");
            }
            if (tangentFailures > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{tangentFailures} eligible location(s) had no stable lateral direction and were preserved.");
            }

            bool clearedDerivedData = moved > 0 && HasDerivedData(source);
            if (clearedDerivedData)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "Pattern Fuzzy Skin changed path geometry. Spatial printability, structural risk, motion-plan, " +
                    "KPI, and deposited-volume fields were cleared because they no longer describe the result.");
            }

            WasperPrintPath output = moved == 0
                ? source
                : new WasperPrintPath(
                    points: null,
                    ptPlanes: outputPlanes,
                    flows: source.Flows,
                    layerH: source.LayerH,
                    printSpeed: source.PrintSpeed,
                    nozzleDiam: source.NozzleDiam,
                    layerW: source.LayerW,
                    layerWf: source.LayerWf,
                    printVol: null,
                    travelSpeed: source.TravelSpeed,
                    zHop: source.ZHop,
                    zHopSpeed: source.ZHopSpeed,
                    isPartial: source.IsPartial,
                    sourceCurves: BuildSourceCurves(outputPlanes, source.SourceCurves, tolerance),
                    pathRoles: source.PathRoles,
                    layerPlanes: source.LayerPlanes);

            WasperInfillConstraintReport constraintReport = null;
            bool shellWasTargeted = targetRoles.Contains(0) || targetRoles.Contains(1);
            if (_constrainInfill && moved > 0 && shellWasTargeted)
            {
                output = WasperInfillShellConstraint.Apply(
                    source,
                    output,
                    tolerance,
                    out constraintReport);
                if (constraintReport.LayersSkipped > 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"{constraintReport.LayersSkipped} logical layer(s) could not be safely " +
                        "constrained because they lacked a matching closed planar Shell or used " +
                        "non-planar geometry. Their Infill branches were preserved unchanged.");
                }
                if (constraintReport.OffsetFallbacks > 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Remark,
                        $"{constraintReport.OffsetFallbacks} Shell clearance offset(s) were not " +
                        "valid; those boundaries fell back to centerline clipping.");
                }
            }

            string maskLabel = masks.Count == 0
                ? "none"
                : $"{masks.Count} valid ({(invert ? "outside" : "inside")})";
            totalWatch.Stop();
            string summary =
                $"Pattern fuzzy skin | branches={source.PtPlanes.BranchCount} | " +
                $"target_roles={WasperGcodeTreeUtil.TargetRoleNames(targetRoles)} | " +
                $"targeted branches={targetedBranches} | layers={orderedLayers.Count} | " +
                $"pt_pattern=[{string.Join(",", pointPattern)}] | " +
                $"la_pattern=[{string.Join(",", layerPattern)}] | " +
                $"mag_0=[{string.Join(",", magnitude0.Select(value => value.ToString("0.###")))}] | " +
                $"mag_1=[{string.Join(",", magnitude1.Select(value => value.ToString("0.###")))}] model units | " +
                $"magnitude mode={(pullRegions.Count == 0 ? "repeating logical layers" : "normalized pull distance")} | " +
                $"pull objects={pullRegions.Count} | " +
                $"pull_dist={(pullRegions.Count == 0 ? "n/a" : "[" + string.Join(",", pullDistances.Select(value => value.ToString("0.###"))) + "]")} | " +
                $"mask={maskLabel} | moved={moved} | max displacement={maximumDisplacement:0.###} | " +
                $"role excluded={roleExcluded} | layer excluded={layerExcluded} | " +
                $"mask excluded={maskExcluded} | zero magnitude={zeroMagnitude} | " +
                $"direction failures={tangentFailures} | large-magnitude moves={largeMagnitudeMoves} | " +
                $"shell deformation cache={(deformationCacheHit ? "reused" : "rebuilt")} | " +
                $"{(_constrainInfill ? constraintReport?.Summary ?? "infill constraint: not applicable" : "infill constraint: off")} | " +
                $"derived data cleared={(clearedDerivedData ? "yes" : "no")}.\n" +
                $"performance [ms]: total={totalWatch.Elapsed.TotalMilliseconds:0.###}";

            WasperPathDebugOutputs.Set(da, this, output, summary);
            Message = _versionTag;
        }

        private static bool IsFiniteMagnitude(double value) =>
            double.IsFinite(value);

        private static void AppendPlanes(
            DataTree<Plane> destination,
            GH_Path path,
            IList<Plane> planes)
        {
            if (planes == null)
                return;
            foreach (Plane plane in planes)
                destination.Add(plane, path);
        }

        private static Vector3d LocalTangent(
            IList<Plane> planes,
            int index,
            int count,
            bool cyclic)
        {
            if (planes == null || count < 2)
                return Vector3d.Unset;

            if (cyclic)
            {
                int previous = (index - 1 + count) % count;
                int next = (index + 1) % count;
                return planes[next].Origin - planes[previous].Origin;
            }

            if (index <= 0)
                return planes[1].Origin - planes[0].Origin;
            if (index >= count - 1)
                return planes[count - 1].Origin - planes[count - 2].Origin;
            return planes[index + 1].Origin - planes[index - 1].Origin;
        }

        private static double LocalShortestSegment(
            IList<Plane> planes,
            int index,
            int count,
            bool cyclic)
        {
            double before = double.PositiveInfinity;
            double after = double.PositiveInfinity;

            if (index > 0)
                before = planes[index].Origin.DistanceTo(planes[index - 1].Origin);
            else if (cyclic && count > 1)
                before = planes[0].Origin.DistanceTo(planes[count - 1].Origin);

            if (index < count - 1)
                after = planes[index].Origin.DistanceTo(planes[index + 1].Origin);
            else if (cyclic && count > 1)
                after = planes[count - 1].Origin.DistanceTo(planes[0].Origin);

            double result = Math.Min(before, after);
            return double.IsFinite(result) ? result : 0.0;
        }

        private static bool SourceCurveIsClosed(
            DataTree<Curve> sourceCurves,
            GH_Path path)
        {
            if (sourceCurves == null || !sourceCurves.PathExists(path))
                return false;
            IList<Curve> branch = sourceCurves.Branch(path);
            return branch != null && branch.Count > 0 &&
                   branch[0] != null && branch[0].IsClosed;
        }

        private static DataTree<Curve> BuildSourceCurves(
            DataTree<Plane> planes,
            DataTree<Curve> originalSourceCurves,
            double tolerance)
        {
            var curves = new DataTree<Curve>();
            for (int branchIndex = 0; branchIndex < planes.BranchCount; branchIndex++)
            {
                GH_Path path = planes.Paths[branchIndex];
                IList<Plane> branch = planes.Branches[branchIndex];
                if (branch == null || branch.Count < 2)
                    continue;
                var points = branch.Select(plane => plane.Origin).ToList();
                if (SourceCurveIsClosed(originalSourceCurves, path) &&
                    points[0].DistanceTo(points[points.Count - 1]) > tolerance)
                {
                    points.Add(points[0]);
                }
                curves.Add(new PolylineCurve(points), path);
            }
            return curves.BranchCount > 0 ? curves : null;
        }

        private static List<MaskRegion> BuildMasks(
            IEnumerable<GeometryBase> geometry,
            double tolerance,
            out int rejected)
        {
            var masks = new List<MaskRegion>();
            rejected = 0;
            if (geometry == null)
                return masks;

            foreach (GeometryBase item in geometry)
            {
                MaskRegion mask = MaskRegion.Create(item, tolerance);
                if (mask == null)
                    rejected++;
                else
                    masks.Add(mask);
            }
            return masks;
        }

        private static List<PullRegion> BuildPullRegions(
            IList<IGH_GeometricGoo> geometry,
            IList<double> distances,
            out int rejected)
        {
            var regions = new List<PullRegion>();
            rejected = 0;
            if (geometry == null || geometry.Count == 0)
                return regions;

            for (int i = 0; i < geometry.Count; i++)
            {
                object value = geometry[i]?.ScriptVariable();
                double influenceDistance = distances.Count == 1
                    ? distances[0]
                    : distances[i % distances.Count];
                PullRegion region = PullRegion.Create(value, influenceDistance);
                if (region == null)
                    rejected++;
                else
                    regions.Add(region);
            }
            return regions;
        }

        private static double PullCoordinate(
            Point3d point,
            IList<PullRegion> regions)
        {
            if (regions == null || regions.Count == 0)
                return 0.0;

            double minimumNormalizedDistance = double.PositiveInfinity;
            foreach (PullRegion region in regions)
            {
                if (!region.TryNormalizedDistance(point, out double normalizedDistance))
                    continue;
                minimumNormalizedDistance = Math.Min(
                    minimumNormalizedDistance,
                    normalizedDistance);
            }

            if (!double.IsFinite(minimumNormalizedDistance))
                return 1.0;
            return Math.Max(0.0, Math.Min(1.0, minimumNormalizedDistance));
        }

        private static double EvaluateProfile(
            IList<double> profile,
            double coordinate)
        {
            if (profile == null || profile.Count == 0)
                return 0.0;
            if (profile.Count == 1)
                return profile[0];

            double u = Math.Max(0.0, Math.Min(1.0, coordinate));
            double station = u * (profile.Count - 1);
            int lower = (int)Math.Floor(station);
            if (lower >= profile.Count - 1)
                return profile[profile.Count - 1];

            double fraction = station - lower;
            return profile[lower] +
                   fraction * (profile[lower + 1] - profile[lower]);
        }

        private static bool HasDerivedData(WasperPrintPath path)
        {
            return path.PrintVol != null ||
                   path.PrintLoc != null || path.PrintGlob != null ||
                   path.SupportPts != null || path.SupportVects != null ||
                   path.Angles != null || path.ContactWidths != null ||
                   path.RiskMaterial != null || path.RiskComb != null ||
                   path.Load != null || path.Capacity != null ||
                   path.DRatio != null || path.DLoaded != null ||
                   path.BendRatio != null || path.SpanClass != null ||
                   path.SpanLen != null || path.Collapsed != null ||
                   path.Cascade != null || path.CollapseGen != null ||
                   path.Torn != null || path.InterfaceRatio != null ||
                   path.OverturnRatio != null || path.FailureFlags != null ||
                   path.MotionPlan != null || path.KpiSegmentLength != null ||
                   path.KpiPrintSpeed != null || path.KpiPrintVol != null ||
                   path.KpiTimeMin.HasValue || path.KpiPathLength.HasValue ||
                   path.KpiVolume.HasValue || path.KpiLayers.HasValue;
        }

        private sealed class MaskRegion
        {
            private readonly Func<Point3d, bool> _contains;

            private MaskRegion(Func<Point3d, bool> contains)
            {
                _contains = contains;
            }

            public bool Contains(Point3d point) => _contains(point);

            public static MaskRegion Create(GeometryBase geometry, double tolerance)
            {
                if (geometry == null || !geometry.IsValid)
                    return null;

                if (geometry is Curve curve &&
                    curve.IsClosed &&
                    curve.TryGetPlane(out Plane plane, tolerance))
                {
                    return new MaskRegion(point =>
                    {
                        PointContainment containment = curve.Contains(point, plane, tolerance);
                        return containment == PointContainment.Inside ||
                               containment == PointContainment.Coincident;
                    });
                }

                if (geometry is Brep brep && brep.IsSolid)
                    return new MaskRegion(point => brep.IsPointInside(point, tolerance, true));

                if (geometry is Extrusion extrusion && extrusion.IsSolid)
                {
                    Brep extrusionBrep = extrusion.ToBrep();
                    return extrusionBrep == null
                        ? null
                        : new MaskRegion(point => extrusionBrep.IsPointInside(point, tolerance, true));
                }

                if (geometry is Mesh mesh && mesh.IsClosed)
                    return new MaskRegion(point => mesh.IsPointInside(point, tolerance, true));

                return null;
            }
        }

        private sealed class PullRegion
        {
            private readonly Func<Point3d, double> _distance;

            private PullRegion(
                Func<Point3d, double> distance,
                double influenceDistance)
            {
                _distance = distance;
                InfluenceDistance = influenceDistance;
            }

            private double InfluenceDistance { get; }

            public bool TryNormalizedDistance(
                Point3d point,
                out double normalizedDistance)
            {
                normalizedDistance = double.PositiveInfinity;
                double distance = _distance(point);
                if (!double.IsFinite(distance) || distance < 0.0)
                    return false;
                normalizedDistance = distance / InfluenceDistance;
                return double.IsFinite(normalizedDistance);
            }

            public static PullRegion Create(
                object geometry,
                double influenceDistance)
            {
                if (!double.IsFinite(influenceDistance) || influenceDistance <= 0.0)
                    return null;

                if (geometry is Point3d point && point.IsValid)
                    return new PullRegion(test => test.DistanceTo(point), influenceDistance);

                if (geometry is Rhino.Geometry.Point pointGeometry &&
                    pointGeometry.IsValid)
                {
                    Point3d location = pointGeometry.Location;
                    return new PullRegion(test => test.DistanceTo(location), influenceDistance);
                }

                if (geometry is Line line && line.IsValid)
                {
                    var lineCurve = new LineCurve(line);
                    return FromCurve(lineCurve, influenceDistance);
                }

                if (geometry is Polyline polyline && polyline.IsValid)
                {
                    var polylineCurve = new PolylineCurve(polyline);
                    return FromCurve(polylineCurve, influenceDistance);
                }

                if (geometry is Curve curve && curve.IsValid)
                    return FromCurve(curve, influenceDistance);

                if (geometry is Mesh mesh && mesh.IsValid)
                {
                    return new PullRegion(
                        test =>
                        {
                            Point3d closest = mesh.ClosestPoint(test);
                            return closest.IsValid
                                ? test.DistanceTo(closest)
                                : double.PositiveInfinity;
                        },
                        influenceDistance);
                }

                if (geometry is Brep brep && brep.IsValid)
                    return FromBrep(brep, influenceDistance);

                if (geometry is Extrusion extrusion && extrusion.IsValid)
                {
                    Brep extrusionBrep = extrusion.ToBrep();
                    return extrusionBrep == null
                        ? null
                        : FromBrep(extrusionBrep, influenceDistance);
                }

                if (geometry is Surface surface && surface.IsValid)
                {
                    return new PullRegion(
                        test =>
                        {
                            if (!surface.ClosestPoint(test, out double u, out double v))
                                return double.PositiveInfinity;
                            u = Math.Max(surface.Domain(0).T0, Math.Min(surface.Domain(0).T1, u));
                            v = Math.Max(surface.Domain(1).T0, Math.Min(surface.Domain(1).T1, v));
                            return test.DistanceTo(surface.PointAt(u, v));
                        },
                        influenceDistance);
                }

                return null;
            }

            private static PullRegion FromCurve(
                Curve curve,
                double influenceDistance)
            {
                return new PullRegion(
                    test =>
                    {
                        if (!curve.ClosestPoint(test, out double parameter))
                            return double.PositiveInfinity;
                        return test.DistanceTo(curve.PointAt(parameter));
                    },
                    influenceDistance);
            }

            private static PullRegion FromBrep(
                Brep brep,
                double influenceDistance)
            {
                return new PullRegion(
                    test =>
                    {
                        bool found = brep.ClosestPoint(
                            test,
                            out Point3d closest,
                            out ComponentIndex _,
                            out double _,
                            out double _,
                            double.MaxValue,
                            out Vector3d _);
                        return found && closest.IsValid
                            ? test.DistanceTo(closest)
                            : double.PositiveInfinity;
                    },
                    influenceDistance);
            }
        }

        private static string BuildDeformationCacheSignature(
            WasperPrintPath source,
            IList<int> targetRoles,
            IList<int> pointPattern,
            IList<int> layerPattern,
            IList<double> magnitude0,
            IList<double> magnitude1,
            IList<IGH_GeometricGoo> pullGeometry,
            IList<double> pullDistances,
            IList<GeometryBase> masks,
            bool invert,
            double tolerance)
        {
            WasperCacheSignature signature = WasperCacheSignature.Create();
            signature.Add(WasperRoleBranchTransformCache.SelectedGeometrySignature(
                source,
                targetRoles));
            signature.Add(targetRoles);
            signature.Add(pointPattern);
            signature.Add(layerPattern);
            signature.Add(magnitude0);
            signature.Add(magnitude1);
            signature.Add(pullDistances);
            signature.Add(invert);
            signature.Add(tolerance);
            signature.Add(pullGeometry?.Count ?? 0);
            if (pullGeometry != null)
            {
                foreach (IGH_GeometricGoo goo in pullGeometry)
                {
                    if (goo != null && goo.CastTo(out GeometryBase geometry))
                        signature.Add(geometry);
                    else
                        signature.Add(goo?.ToString());
                }
            }
            signature.Add(masks?.Count ?? 0);
            if (masks != null)
            {
                foreach (GeometryBase mask in masks)
                    signature.Add(mask);
            }
            return signature.Finish();
        }

        private sealed class PatternCacheEntry
        {
            public DataTree<Plane> Planes;
            public int TargetedBranches;
            public int RoleExcluded;
            public int LayerExcluded;
            public int MaskExcluded;
            public int Moved;
            public int ZeroMagnitude;
            public int TangentFailures;
            public int LargeMagnitudeMoves;
            public double MaximumDisplacement;
        }

        private static class FuzzySkinIcon
        {
            private static Bitmap _bitmap;

            public static Bitmap Bitmap
            {
                get
                {
                    if (_bitmap != null)
                        return _bitmap;

                    _bitmap = new Bitmap(24, 24);
                    using Graphics graphics = Graphics.FromImage(_bitmap);
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.Clear(Color.Transparent);

                    using var centerPen = new Pen(Color.FromArgb(55, 78, 92), 1.2f)
                    {
                        DashStyle = DashStyle.Dash
                    };
                    using var pathPen = new Pen(Color.FromArgb(231, 135, 29), 2.2f);
                    using var pointBrush = new SolidBrush(Color.FromArgb(44, 157, 195));

                    graphics.DrawLine(centerPen, 2, 12, 22, 12);
                    PointF[] zigzag =
                    {
                        new PointF(2, 12),
                        new PointF(5, 7),
                        new PointF(8, 17),
                        new PointF(11, 7),
                        new PointF(14, 17),
                        new PointF(17, 7),
                        new PointF(22, 12)
                    };
                    graphics.DrawLines(pathPen, zigzag);
                    foreach (PointF point in zigzag.Skip(1).Take(5))
                        graphics.FillEllipse(pointBrush, point.X - 1.6f, point.Y - 1.6f, 3.2f, 3.2f);

                    return _bitmap;
                }
            }
        }
    }
}
