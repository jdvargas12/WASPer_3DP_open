#region Component Description
/*
Component: wsp_Pp13_Fuzzy Pockets from Points
Nickname: Fuzzy Pockets
Category: WASPer_3DP
SubCategory: 5.0_Gcode

Projects point anchors onto the nearest Shell branch and creates smooth signed
deformations: positive depth moves outward and negative depth moves inward.
rad_h controls distance along the Shell and rad_v controls distance through
the local layer stack, producing an elliptical cosine influence. pocket_t
selects the pocket shape per anchor: 0 Regular (full), 1 Half pocket downward
(anchor layer and below, upper-open profile), 2 Half pocket upward (anchor
layer and above, lower-open profile). Core point-matched process fields
remain aligned.

POCKET_T (2026-08-06): renamed/retyped from the old Boolean half_pocket input
(same list index) to an Integer with 0/1/2 named values, adding the new
upward half-pocket direction. GH casts Boolean->Integer as false->0, true->1,
so existing Toggle wiring keeps its old meaning (off=Regular, on=downward);
a slider/panel is needed to reach the new upward value 2.

BRANCH ELIGIBILITY (2026-08-06): Shell branches no longer need to be closed
or planar. Closed, planar branches keep the original precise point-in-polygon
material-side test (rad_h distance wraps cyclically around the loop). Open
and/or non-planar branches instead resolve inward vs outward per point from
the side of the nearest same-layer Infill sample (rad_h distance runs along
the open path only, no wraparound). If a wsp_path has no Shell-tagged
branches at all, pockets fall back to branches of any role, using the same
open-branch heuristic; in that fallback mode the infill-constraint
right-click option has nothing Shell-specific to constrain to and is skipped.

MASK (2026-08-06): optional mask/invert inputs, same pattern as Pp12/Pp14.
mask accepts closed planar curves, solid Breps/Extrusions, or closed meshes
(union of multiple items); invert flips inside/outside. pocket_pts excluded
by the mask are skipped before projection and never create a pocket.

MAX_DIST (2026-08-06): optional per-point/broadcast distance cap. A single
value applies to every pocket point (broadcast); a list matching pocket_pts
applies per point; other counts repeat. Points whose nearest eligible Shell
is farther than the cap are excluded before projection (no anchor, no
pocket) rather than being force-projected onto a distant branch. Omitted
list means unlimited distance, unchanged from prior behavior.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;

using Rhino;
using Rhino.Geometry;

using WASPer_3DP;
using WASPer_3DP.Painting;

namespace WASPer_3DP_Components._4_0_Print_Paths
{
    public sealed class wsp_Pp13_Fuzzy_Pockets_from_Points : GH_Component
    {
        private readonly string _versionTag;
        private bool _constrainInfill = true;
        private static readonly string[] OutputCatalog = WasperPathDebugOutputs.CoreNickNames;
        private static readonly int AllOutputsMask = (1 << OutputCatalog.Length) - 1;
        private int _visibleOutputsMask;
        private string _deformationCacheSignature = string.Empty;
        private WasperPrintPath _deformationCacheSource;
        private PocketCacheEntry _deformationCache;
        private const string ConstrainInfillKey = "wsp_gc19_constrain_infill";
        // Legacy all-or-nothing key, still read for backward compatibility with saved files.
        private const string ShowAllOutputsKey = "wsp_gc19_show_all_outputs";
        private const string VisibleOutputsMaskKey = "wsp_gc19_visible_outputs_mask";

        public wsp_Pp13_Fuzzy_Pockets_from_Points()
            : base(
                "wsp_Pp13_Fuzzy Pockets from Points",
                "Fuzzy Pockets",
                "PURPOSE\r\n" +
                "Creates localized signed Shell pockets from projected point anchors.\r\n\r\n" +
                "DIRECTION\r\n" +
                "Positive depth moves outward, negative depth moves inward, and zero makes no movement. " +
                "Closed planar branches use exact point-in-polygon material-side classification; open " +
                "and/or non-planar branches (and the any-role fallback below) instead use the side of " +
                "the nearest same-layer Infill sample.\r\n\r\n" +
                "SHAPE AND LISTS\r\n" +
                "rad_h controls distance along the Shell (cyclic for closed branches, straight-line for " +
                "open branches); rad_v controls distance through the local layer stack. Full pockets use " +
                "an elliptical cosine influence. Radii, depth, and pocket_t accept broadcast, " +
                "point-matched, or repeating lists. pocket_t selects the anchor's shape: 0 Regular (full), " +
                "1 Half pocket downward (anchor layer and lower logical layers, upper-open profile), " +
                "2 Half pocket upward (anchor layer and upper logical layers, lower-open profile). " +
                "Overlaps keep the signed influence with the greatest absolute displacement.\r\n\r\n" +
                "MASK\r\n" +
                "Optional closed planar curves, solid Breps/Extrusions, or closed meshes that filter " +
                "pocket_pts before projection. Multiple masks act as a union. invert flips the selection " +
                "from inside to outside. Excluded points are not projected and create no pocket. With no " +
                "mask, every pocket point is eligible.\r\n\r\n" +
                "MAXIMUM DISTANCE\r\n" +
                "Optional max_dist caps how far a pocket point may be from the nearest eligible Shell " +
                "before it is excluded instead of force-projected. One value broadcasts to all pocket " +
                "points; a matching list pairs one-to-one; other non-empty counts repeat. If omitted, " +
                "projection distance is unlimited.\r\n\r\n" +
                "BRANCH ELIGIBILITY\r\n" +
                "Shell branches no longer need to be closed or planar. If a wsp_path has no Shell-tagged " +
                "branches at all, pockets fall back to branches of any role.\r\n\r\n" +
                "LIMITATIONS\r\n" +
                "By default, newly invalid planar Infill is locally constrained to the modified Shell " +
                "(skipped automatically when the any-role fallback is used, since there is no Shell to " +
                "constrain to). The component does not certify cavity watertightness, wall thickness, or " +
                "overhang printability.\r\n\r\n" +
                "For Francisca Aroso - ACTech Hub U Minho.\r\n\r\n" +
                "Please use the Pp01 WASPer Path from Curves before using this component.",
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
            new Guid("7BBB8E81-7F65-4955-A4AA-19899AA9E30D");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon => FuzzyPocketsIcon.Bitmap;

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
                    RecordUndoEvent("Toggle Pp13 debug outputs");
                    _visibleOutputsMask = mask;
                    WasperPathDebugOutputs.Rebuild(
                        this,
                        _visibleOutputsMask,
                        "Pocket anchors, list mapping, displacement, safety, and Infill-constraint report.",
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
                "Pocket anchors, list mapping, displacement, safety, and Infill-constraint report.",
                OutputCatalog);
            return base.Read(reader);
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "WASPer Print Path containing branches tagged as Shell. Branches no longer need to be " +
                "closed or planar; open/non-planar branches use a nearest-Infill-point heuristic instead " +
                "of point-in-polygon classification. If no Shell-tagged branches are found, pockets fall " +
                "back to branches of any role. Pocket geometry is written back into its canonical " +
                "pt_planes. Please use the Pp01 WASPer Path from Curves before using this component.",
                GH_ParamAccess.item);
            int pocketPointsIndex = p.AddPointParameter(
                "pocket points",
                "pocket_pts",
                "Pocket anchor points. Each point is projected onto its nearest valid Shell branch " +
                "(or, if none are eligible, the nearest branch of any role) and affects only that " +
                "corresponding stack.",
                GH_ParamAccess.list);
            p[pocketPointsIndex].DataMapping = GH_DataMapping.Flatten;
            p.AddNumberParameter(
                "horizontal radius",
                "rad_h",
                "Positive horizontal influence radii in model units, measured cyclically along " +
                "each closed Shell path. One value broadcasts to all pocket " +
                "points; a matching list pairs one-to-one; other non-empty counts repeat with a " +
                "grey notice.",
                GH_ParamAccess.list);
            p.AddNumberParameter(
                "vertical radius",
                "rad_v",
                "Optional positive vertical influence radii in model units, measured through the " +
                "local Shell layer stack rather than World Z. If omitted, each pocket uses its " +
                "rad_h value. One value broadcasts; matching counts pair; other non-empty counts repeat.",
                GH_ParamAccess.list);
            p[3].Optional = true;
            p.AddNumberParameter(
                "depth",
                "depth",
                "Signed pocket displacement in model units: positive moves outward, negative " +
                "moves inward, and zero makes no movement. One value broadcasts, matching counts " +
                "pair one-to-one, and other non-empty counts repeat.",
                GH_ParamAccess.list);
            int pocketTypeIndex = p.AddIntegerParameter(
                "pocket type",
                "pocket_t",
                "Optional pocket shape list. 0 = Regular: a full smooth pocket around its point. " +
                "1 = Half pocket downward: affects only the anchor layer and lower logical layers, " +
                "using the point as the upper opening centre (upper-open profile). 2 = Half pocket " +
                "upward: affects only the anchor layer and upper logical layers, using the point as " +
                "the lower opening centre (lower-open profile). One value broadcasts; other lists " +
                "pair or repeat.",
                GH_ParamAccess.list,
                0);
            if (p[pocketTypeIndex] is Param_Integer pocketTypeParameter)
            {
                pocketTypeParameter.AddNamedValue("Regular", 0);
                pocketTypeParameter.AddNamedValue("Half pocket downward", 1);
                pocketTypeParameter.AddNamedValue("Half pocket upward", 2);
            }
            p[pocketTypeIndex].Optional = true;
            int maskIndex = p.AddGeometryParameter(
                "mask",
                "mask",
                "Optional closed geometry used to filter pocket_pts. Supported masks are closed " +
                "planar curves, solid Breps/Extrusions, and closed meshes. Multiple masks act as " +
                "a union. Points excluded by the mask are not projected and do not create a pocket. " +
                "With no mask, every pocket point may affect its projected Shell.",
                GH_ParamAccess.list);
            p[maskIndex].Optional = true;
            p.AddBooleanParameter(
                "invert",
                "invert",
                "Invert the optional mask selection. False keeps pocket points inside the mask; " +
                "True keeps points outside it. Has no effect when mask is not supplied.",
                GH_ParamAccess.item,
                false);
            int maxDistanceIndex = p.AddNumberParameter(
                "maximum distance",
                "max_dist",
                "Optional positive projection-distance cap in model units. A pocket point farther " +
                "than this from every eligible Shell branch is excluded entirely (no anchor, no " +
                "pocket) instead of being force-projected onto a distant branch. One value " +
                "broadcasts to all pocket points; a matching list pairs one-to-one; other " +
                "non-empty counts repeat with a grey notice. If omitted, projection distance is " +
                "unlimited.",
                GH_ParamAccess.list);
            p[maxDistanceIndex].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "WASPer Print Path with displaced Shell pocket geometry and locally constrained " +
                "planar Infill when the right-click option is enabled.",
                GH_ParamAccess.item);
            p.AddTextParameter(
                "summary",
                "summary",
                "Pocket anchors, list mapping, displacement, safety, and Infill-constraint report.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            var totalWatch = Stopwatch.StartNew();
            double setupMs = 0.0;
            double projectionMs = 0.0;
            double deformationMs = 0.0;
            double packingMs = 0.0;
            double constraintMs = 0.0;
            var setupWatch = Stopwatch.StartNew();

            WasperPrintPath source = ReadPath(da);
            if (source == null)
                return;
            if (!source.HasPlanes || source.PtPlanes.DataCount == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "wsp_path contains no pt_planes.");
                return;
            }
            if (!source.HasPathRoles)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "wsp_path contains no semantic path roles. Build it from role-tagged Shell/" +
                    "Infill curves before creating pockets.");
                return;
            }

            var pocketPoints = new List<Point3d>();
            var horizontalRadii = new List<double>();
            var verticalRadii = new List<double>();
            var depths = new List<double>();
            var pocketTypes = new List<int>();
            da.GetDataList(1, pocketPoints);
            da.GetDataList(2, horizontalRadii);
            da.GetDataList(3, verticalRadii);
            da.GetDataList(4, depths);
            da.GetDataList(5, pocketTypes);
            var rawMasks = new List<GeometryBase>();
            da.GetDataList(6, rawMasks);
            bool invertMask = false;
            da.GetData(7, ref invertMask);
            var maxDistances = new List<double>();
            da.GetDataList(8, maxDistances);
            if (pocketTypes.Count == 0)
                pocketTypes.Add(0);
            bool verticalRadiusDefaulted = verticalRadii.Count == 0;
            if (verticalRadiusDefaulted)
                verticalRadii.AddRange(horizontalRadii);

            if (pocketPoints.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "pocket_pts requires at least one point.");
                return;
            }
            if (horizontalRadii.Count == 0 || depths.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "rad_h and depth each require at least one value.");
                return;
            }
            for (int i = 0; i < pocketPoints.Count; i++)
            {
                if (!pocketPoints[i].IsValid)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        $"pocket_pts contains an invalid point at item {i}.");
                    return;
                }
            }
            if (!ValidatePositiveList(horizontalRadii, "rad_h") ||
                !ValidatePositiveList(verticalRadii, "rad_v") ||
                !ValidateFiniteList(depths, "depth") ||
                !ValidatePocketTypeList(pocketTypes, "pocket_t") ||
                (maxDistances.Count > 0 && !ValidatePositiveList(maxDistances, "max_dist")))
                return;

            NoticeRepeatingCount(horizontalRadii.Count, pocketPoints.Count, "rad_h");
            if (!verticalRadiusDefaulted)
                NoticeRepeatingCount(verticalRadii.Count, pocketPoints.Count, "rad_v");
            NoticeRepeatingCount(depths.Count, pocketPoints.Count, "depth");
            NoticeRepeatingCount(pocketTypes.Count, pocketPoints.Count, "pocket_t");
            if (maxDistances.Count > 0)
                NoticeRepeatingCount(maxDistances.Count, pocketPoints.Count, "max_dist");

            double tolerance = Math.Max(
                RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001,
                1e-9);

            List<WasperPaintMaskRegion> masks = WasperPaintMaskRegion.Build(
                rawMasks, tolerance, out int rejectedMasks);
            if (rawMasks.Count > 0 && masks.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "mask contains no supported closed geometry. Use closed planar curves, " +
                    "solid Breps/Extrusions, or closed meshes.");
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

            Dictionary<int, List<Point3d>> infillPointsByLayer =
                BuildInfillPointsByLayer(source, commonPrefix);
            List<ShellBranch> shellBranches = BuildShellBranches(
                source,
                commonPrefix,
                layerOrdinals,
                infillPointsByLayer,
                tolerance,
                shellRoleOnly: true,
                out int rejectedShellBranches,
                out int inwardFallbackBranches,
                out int openOrNonPlanarBranches);

            bool usedAnyRoleFallback = false;
            if (shellBranches.Count == 0)
            {
                usedAnyRoleFallback = true;
                shellBranches = BuildShellBranches(
                    source,
                    commonPrefix,
                    layerOrdinals,
                    infillPointsByLayer,
                    tolerance,
                    shellRoleOnly: false,
                    out rejectedShellBranches,
                    out inwardFallbackBranches,
                    out openOrNonPlanarBranches);
                if (shellBranches.Count > 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Remark,
                        $"No Shell-tagged branches were eligible; pockets fell back to " +
                        $"{shellBranches.Count} branch(es) of any role.");
                }
            }
            setupWatch.Stop();
            setupMs = setupWatch.Elapsed.TotalMilliseconds;
            if (shellBranches.Count == 0)
            {
                int undefinedBranches = source.PtPlanes.Paths.Count(path =>
                    WasperGcodeTreeUtil.PathRoleAt(source.PathRoles, path) ==
                    WasperPathRole.Undefined);
                if (undefinedBranches > 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Remark,
                        $"{undefinedBranches} branch(es) are Undefined. If they represent Shells, " +
                        "assign their role with Sl08 before Pp01 or Pp15 Assign Print Path Roles afterward.");
                }
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "No valid branches were found to pocket (checked Shell-tagged branches, then any role).");
                return;
            }
            if (rejectedShellBranches > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{rejectedShellBranches} branch(es) were invalid or too short and were not " +
                    "eligible for pockets.");
            }
            if (openOrNonPlanarBranches > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{openOrNonPlanarBranches} branch(es) are open and/or non-planar; their " +
                    "inward direction is resolved per point from the nearest same-layer Infill " +
                    "sample instead of closed-loop containment.");
            }
            if (inwardFallbackBranches > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{inwardFallbackBranches} closed planar Shell branch(es) had no same-layer " +
                    "Infill sample for material-side classification; closed-curve interior was used.");
            }

            var shellRoles = new List<int> { (int)WasperPathRole.Shell };
            string deformationSignature = BuildDeformationCacheSignature(
                source,
                shellRoles,
                shellBranches,
                pocketPoints,
                horizontalRadii,
                verticalRadii,
                depths,
                pocketTypes,
                infillPointsByLayer,
                usedAnyRoleFallback,
                rawMasks,
                invertMask,
                maxDistances,
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
                    shellRoles,
                    out cachedOutputPlanes);

            List<PocketAnchor> anchors;
            double maximumProjectionDistance = 0.0;
            int distantAnchors = 0;
            var outputPlanes = new DataTree<Plane>();
            int movedLocations = 0;
            int affectedBranches = 0;
            int largeMoves = 0;
            int directionFailures = 0;
            int unclassifiedDirections = 0;
            int maskExcluded = 0;
            int distanceExcluded = 0;
            double maximumMove = 0.0;

            if (deformationCacheHit)
            {
                anchors = _deformationCache.Anchors;
                maximumProjectionDistance = _deformationCache.MaximumProjectionDistance;
                distantAnchors = _deformationCache.DistantAnchors;
                outputPlanes = cachedOutputPlanes;
                movedLocations = _deformationCache.MovedLocations;
                affectedBranches = _deformationCache.AffectedBranches;
                largeMoves = _deformationCache.LargeMoves;
                directionFailures = _deformationCache.DirectionFailures;
                unclassifiedDirections = _deformationCache.UnclassifiedDirections;
                maskExcluded = _deformationCache.MaskExcluded;
                distanceExcluded = _deformationCache.DistanceExcluded;
                maximumMove = _deformationCache.MaximumMove;
            }
            else
            {
            var projectionWatch = Stopwatch.StartNew();
            anchors = new List<PocketAnchor>();
            for (int i = 0; i < pocketPoints.Count; i++)
            {
                Point3d rawPoint = pocketPoints[i];
                bool maskMatch = masks.Count == 0 || masks.Any(mask => mask.Contains(rawPoint));
                if (masks.Count > 0 && invertMask)
                    maskMatch = !maskMatch;
                if (!maskMatch)
                {
                    maskExcluded++;
                    continue;
                }

                double horizontalRadius = ListAt(horizontalRadii, i);
                double verticalRadius = ListAt(verticalRadii, i);
                double depth = ListAt(depths, i);
                int pocketType = ListAt(pocketTypes, i);
                PocketAnchor anchor = ProjectAnchor(
                    rawPoint,
                    horizontalRadius,
                    verticalRadius,
                    depth,
                    pocketType,
                    shellBranches);
                if (anchor == null)
                    continue;
                if (maxDistances.Count > 0)
                {
                    double allowedDistance = ListAt(maxDistances, i);
                    if (anchor.ProjectionDistance > allowedDistance)
                    {
                        distanceExcluded++;
                        continue;
                    }
                }
                anchors.Add(anchor);
                maximumProjectionDistance = Math.Max(
                    maximumProjectionDistance,
                    anchor.ProjectionDistance);
                if (anchor.ProjectionDistance > Math.Max(horizontalRadius, verticalRadius))
                    distantAnchors++;
            }
            projectionWatch.Stop();
            projectionMs = projectionWatch.Elapsed.TotalMilliseconds;
            if (anchors.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    maskExcluded >= pocketPoints.Count
                        ? "All pocket_pts were excluded by mask."
                        : distanceExcluded >= pocketPoints.Count - maskExcluded
                            ? "All remaining pocket_pts were excluded by max_dist."
                            : "None of the pocket points could be projected onto a valid Shell.");
                return;
            }
            if (distantAnchors > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{distantAnchors} pocket point(s) were farther from their selected Shell " +
                    "than both of their influence radii. They were still projected; verify the selected location.");
            }
            if (distanceExcluded > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{distanceExcluded} pocket point(s) were farther than max_dist from every " +
                    "eligible Shell branch and were excluded; no anchor was created for them.");
            }

            var deformationWatch = Stopwatch.StartNew();

            var branchLookup = shellBranches.ToDictionary(
                branch => PathKey(branch.Path),
                branch => branch);
            Dictionary<string, List<PocketInfluence>> influencesByBranch =
                BuildInfluencesByBranch(shellBranches, anchors);
            for (int branchIndex = 0; branchIndex < source.PtPlanes.BranchCount; branchIndex++)
            {
                GH_Path path = source.PtPlanes.Paths[branchIndex];
                IList<Plane> incoming = source.PtPlanes.Branches[branchIndex];
                outputPlanes.EnsurePath(path);
                if (!branchLookup.TryGetValue(PathKey(path), out ShellBranch shell))
                {
                    AppendPlanes(outputPlanes, path, incoming);
                    continue;
                }

                if (!influencesByBranch.TryGetValue(
                        PathKey(path),
                        out List<PocketInfluence> influences))
                {
                    AppendPlanes(outputPlanes, path, incoming);
                    continue;
                }

                bool duplicateClosure = incoming.Count > 2 &&
                    incoming[0].Origin.DistanceTo(incoming[incoming.Count - 1].Origin) <= tolerance;
                int logicalCount = duplicateClosure ? incoming.Count - 1 : incoming.Count;
                bool branchMoved = false;
                var displaced = new List<Plane>(incoming.Count);
                infillPointsByLayer.TryGetValue(shell.Layer, out List<Point3d> layerInfillPoints);

                for (int pointIndex = 0; pointIndex < logicalCount; pointIndex++)
                {
                    Plane plane = incoming[pointIndex];
                    double signedMagnitude = 0.0;
                    double governingRadius = 0.0;
                    foreach (PocketInfluence influence in influences)
                    {
                        if (!TryEllipticalDistance(
                            shell,
                            pointIndex,
                            influence,
                            out double normalizedDistance))
                            continue;
                        double u = Math.Max(0.0, Math.Min(1.0, normalizedDistance));
                        PocketAnchor anchor = influence.Anchor;
                        double candidate = anchor.Depth * 0.5 * (1.0 + Math.Cos(Math.PI * u));
                        if (Math.Abs(candidate) > Math.Abs(signedMagnitude))
                        {
                            signedMagnitude = candidate;
                            governingRadius = Math.Min(
                                anchor.HorizontalRadius,
                                anchor.VerticalRadius);
                        }
                    }

                    double absoluteMagnitude = Math.Abs(signedMagnitude);
                    if (absoluteMagnitude <= tolerance)
                    {
                        displaced.Add(plane);
                        continue;
                    }

                    Vector3d tangent = LocalTangent(
                        incoming,
                        pointIndex,
                        logicalCount,
                        cyclic: shell.IsClosedLoop);
                    if (!tangent.Unitize())
                    {
                        displaced.Add(plane);
                        directionFailures++;
                        continue;
                    }
                    Vector3d lateral = Vector3d.CrossProduct(plane.ZAxis, tangent);
                    if (!lateral.Unitize())
                    {
                        displaced.Add(plane);
                        directionFailures++;
                        continue;
                    }
                    Vector3d inward = shell.InwardDirection(
                        plane.Origin,
                        lateral,
                        tolerance,
                        layerInfillPoints,
                        out bool directionClassified);
                    if (!directionClassified)
                        unclassifiedDirections++;
                    if (!inward.Unitize())
                    {
                        displaced.Add(plane);
                        directionFailures++;
                        continue;
                    }

                    double localSegment = LocalShortestSegment(
                        incoming,
                        pointIndex,
                        logicalCount,
                        cyclic: shell.IsClosedLoop);
                    if ((localSegment > tolerance && absoluteMagnitude > 0.5 * localSegment) ||
                        (governingRadius > tolerance && absoluteMagnitude > governingRadius))
                        largeMoves++;

                    // Inward is the material-side direction. Positive depth uses the opposite
                    // direction (outward); negative depth follows the inward direction.
                    plane.Origin -= inward * signedMagnitude;
                    displaced.Add(plane);
                    movedLocations++;
                    branchMoved = true;
                    maximumMove = Math.Max(maximumMove, absoluteMagnitude);
                }

                if (duplicateClosure)
                {
                    Plane closing = incoming[incoming.Count - 1];
                    if (displaced.Count > 0)
                        closing.Origin = displaced[0].Origin;
                    displaced.Add(closing);
                }
                if (branchMoved)
                    affectedBranches++;
                AppendPlanes(outputPlanes, path, displaced);
            }
            deformationWatch.Stop();
            deformationMs = deformationWatch.Elapsed.TotalMilliseconds;
            _deformationCacheSignature = deformationSignature;
            _deformationCacheSource = source;
            _deformationCache = new PocketCacheEntry
            {
                Planes = outputPlanes,
                Anchors = anchors,
                MaximumProjectionDistance = maximumProjectionDistance,
                DistantAnchors = distantAnchors,
                MovedLocations = movedLocations,
                AffectedBranches = affectedBranches,
                LargeMoves = largeMoves,
                DirectionFailures = directionFailures,
                UnclassifiedDirections = unclassifiedDirections,
                MaskExcluded = maskExcluded,
                DistanceExcluded = distanceExcluded,
                MaximumMove = maximumMove
            };
            }

            if (directionFailures > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{directionFailures} eligible Shell location(s) had no stable inward " +
                    "direction and were preserved.");
            }
            if (unclassifiedDirections > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{unclassifiedDirections} location(s) on open/non-planar branches had no " +
                    "same-layer Infill sample to classify a material side; the default outward " +
                    "lateral direction was used.");
            }
            if (largeMoves > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{largeMoves} moved location(s) use absolute depth larger than the governing radius " +
                    "or half a neighbouring segment. Review folds, overhangs, and crossings.");
            }
            int halfDownCount = anchors.Count(anchor => anchor.PocketType == 1);
            int halfUpCount = anchors.Count(anchor => anchor.PocketType == 2);
            int halfCount = halfDownCount + halfUpCount;
            if (halfCount > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{halfDownCount} half-pocket-downward (upper-open) and {halfUpCount} " +
                    "half-pocket-upward (lower-open) anchor(s) use an experimental open profile. " +
                    "The result is not automatically certified as watertight or printable.");
            }

            bool clearedDerivedData = movedLocations > 0 && HasDerivedData(source);
            if (clearedDerivedData)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "Fuzzy Pockets changed Shell geometry. Deposited volume, printability, " +
                    "structural-risk, motion-plan, and KPI fields were cleared.");
            }

            var packingWatch = Stopwatch.StartNew();
            WasperPrintPath output = movedLocations == 0
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
                    sourceCurves: BuildSourceCurves(
                        outputPlanes,
                        source.SourceCurves,
                        tolerance),
                    pathRoles: source.PathRoles,
                    layerPlanes: source.LayerPlanes);
            packingWatch.Stop();
            packingMs = packingWatch.Elapsed.TotalMilliseconds;

            WasperInfillConstraintReport constraintReport = null;
            if (_constrainInfill && movedLocations > 0 && !usedAnyRoleFallback)
            {
                var constraintWatch = Stopwatch.StartNew();
                output = WasperInfillShellConstraint.Apply(
                    source,
                    output,
                    tolerance,
                    out constraintReport);
                if (constraintReport.LayersSkipped > 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Remark,
                        $"{constraintReport.LayersSkipped} affected logical layer(s) could not " +
                        "be safely constrained. Their Infill passed through unchanged.");
                }
                if (constraintReport.OffsetFallbacks > 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Remark,
                        $"{constraintReport.OffsetFallbacks} clearance offset(s) fell back to " +
                        "the Shell centerline.");
                }
                constraintWatch.Stop();
                constraintMs = constraintWatch.Elapsed.TotalMilliseconds;
            }
            else if (_constrainInfill && movedLocations > 0 && usedAnyRoleFallback)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "Infill constraint skipped: pockets were applied to non-Shell branches " +
                    "(no Shell-tagged branches were found), so there is no Shell to constrain Infill to.");
            }

            int outwardAnchors = anchors.Count(anchor => anchor.Depth > tolerance);
            int inwardAnchors = anchors.Count(anchor => anchor.Depth < -tolerance);
            int zeroDepthAnchors = anchors.Count - outwardAnchors - inwardAnchors;
            string maskLabel = masks.Count == 0
                ? "none"
                : $"{masks.Count} valid ({(invertMask ? "outside" : "inside")})";
            totalWatch.Stop();
            string performance =
                $"performance [ms]: total={totalWatch.Elapsed.TotalMilliseconds:0.###}, " +
                $"setup={setupMs:0.###}, projection={projectionMs:0.###}, " +
                $"deformation={deformationMs:0.###}, packing={packingMs:0.###}, " +
                $"infill_constraint={constraintMs:0.###}";
            string summary =
                $"Fuzzy pockets | requested={pocketPoints.Count} | projected={anchors.Count} | " +
                $"full={anchors.Count - halfCount} | half_down={halfDownCount} | half_up={halfUpCount} | " +
                $"outward={outwardAnchors} | inward={inwardAnchors} | zero depth={zeroDepthAnchors} | " +
                $"rad_h count={horizontalRadii.Count} | rad_v count={verticalRadii.Count}" +
                $"{(verticalRadiusDefaulted ? " (from rad_h)" : string.Empty)} | depth count={depths.Count} | " +
                $"mask={maskLabel} | mask excluded={maskExcluded} | " +
                $"max_dist={(maxDistances.Count == 0 ? "unlimited" : $"{maxDistances.Count} value(s)")} | " +
                $"distance excluded={distanceExcluded} | " +
                $"affected branches={affectedBranches} | moved locations={movedLocations} | " +
                $"max displacement={maximumMove:0.###} | max anchor projection={maximumProjectionDistance:0.###} | " +
                $"direction failures={directionFailures} | large moves={largeMoves} | " +
                $"shell deformation cache={(deformationCacheHit ? "reused" : "rebuilt")} | " +
                $"{(_constrainInfill ? constraintReport?.Summary ?? "infill constraint: not applicable" : "infill constraint: off")} | " +
                $"derived data cleared={(clearedDerivedData ? "yes" : "no")}.\n" +
                performance;
            WasperPathDebugOutputs.Set(da, this, output, summary);
            Message = _versionTag;
        }

        private WasperPrintPath ReadPath(IGH_DataAccess da)
        {
            object raw = null;
            if (!da.GetData(0, ref raw) || raw == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "wsp_path is required. Please use the Pp01 WASPer Path from Curves before using this component.");
                return null;
            }
            if (raw is WasperPrintPath path)
                return path;
            if (raw is WasperPrintPathGoo goo && goo.Value != null)
                return goo.Value;
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Error,
                "wsp_path must be a WASPer Print Path. Please use the Pp01 WASPer Path from Curves before using this component.");
            return null;
        }

        private bool ValidatePositiveList(IList<double> values, string name)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (!double.IsFinite(values[i]) || values[i] <= 0.0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        $"{name} must contain positive finite values. Bad item {i}.");
                    return false;
                }
            }
            return true;
        }

        private bool ValidateFiniteList(IList<double> values, string name)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (!double.IsFinite(values[i]))
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        $"{name} must contain finite values. Bad item {i}.");
                    return false;
                }
            }
            return true;
        }

        private bool ValidatePocketTypeList(IList<int> values, string name)
        {
            for (int i = 0; i < values.Count; i++)
            {
                if (values[i] < 0 || values[i] > 2)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        $"{name} must be 0 (Regular), 1 (Half pocket downward), or 2 " +
                        $"(Half pocket upward). Bad item {i}.");
                    return false;
                }
            }
            return true;
        }

        private void NoticeRepeatingCount(int count, int pocketCount, string name)
        {
            if (count == 1 || count == pocketCount)
                return;
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Remark,
                $"{name} contains {count} value(s) for {pocketCount} pocket point(s); " +
                "the list repeats by item index.");
        }

        private static double ListAt(IList<double> values, int index) =>
            values.Count == 1 ? values[0] : values[index % values.Count];

        private static bool ListAt(IList<bool> values, int index) =>
            values.Count == 1 ? values[0] : values[index % values.Count];

        private static int ListAt(IList<int> values, int index) =>
            values.Count == 1 ? values[0] : values[index % values.Count];

        private static Dictionary<int, List<Point3d>> BuildInfillPointsByLayer(
            WasperPrintPath path,
            int commonPrefix)
        {
            var result = new Dictionary<int, List<Point3d>>();
            for (int b = 0; b < path.PtPlanes.BranchCount; b++)
            {
                GH_Path treePath = path.PtPlanes.Paths[b];
                if (RoleAt(path.PathRoles, treePath) != (int)WasperPathRole.Infill)
                    continue;
                int layer = WasperGcodeTreeUtil.LayerFromPath(treePath, commonPrefix);
                if (!result.TryGetValue(layer, out List<Point3d> points))
                {
                    points = new List<Point3d>();
                    result[layer] = points;
                }
                foreach (Plane plane in path.PtPlanes.Branches[b])
                    points.Add(plane.Origin);
            }
            return result;
        }

        private static List<ShellBranch> BuildShellBranches(
            WasperPrintPath path,
            int commonPrefix,
            IDictionary<int, int> layerOrdinals,
            IDictionary<int, List<Point3d>> infillPointsByLayer,
            double tolerance,
            bool shellRoleOnly,
            out int rejected,
            out int inwardFallbacks,
            out int openOrNonPlanarBranches)
        {
            rejected = 0;
            inwardFallbacks = 0;
            openOrNonPlanarBranches = 0;
            var result = new List<ShellBranch>();
            for (int b = 0; b < path.PtPlanes.BranchCount; b++)
            {
                GH_Path treePath = path.PtPlanes.Paths[b];
                if (shellRoleOnly && RoleAt(path.PathRoles, treePath) != (int)WasperPathRole.Shell)
                    continue;
                IList<Plane> planes = path.PtPlanes.Branches[b];
                Curve curve = CurveAt(path, treePath);
                if (planes == null || planes.Count < 3 ||
                    curve == null || !curve.IsValid || curve.GetLength() <= tolerance)
                {
                    rejected++;
                    continue;
                }

                // Closed + planar branches get the precise point-in-polygon material-side
                // test (unchanged from before). Open and/or non-planar branches (no polygon
                // interior to test against) fall back to a local nearest-Infill-point side
                // heuristic, resolved per point later in InwardDirection.
                bool hasPlane = curve.TryGetPlane(out Plane curvePlane, tolerance * 10.0);
                bool usePolygon = curve.IsClosed && hasPlane;
                Curve workingCurve = curve;

                if (usePolygon)
                {
                    Curve projected = Curve.ProjectToPlane(curve, curvePlane);
                    if (projected != null && projected.IsClosed)
                        workingCurve = projected;
                    else
                        usePolygon = false;
                }
                if (!usePolygon)
                    openOrNonPlanarBranches++;

                int layer = WasperGcodeTreeUtil.LayerFromPath(treePath, commonPrefix);
                bool materialInside = true;
                if (usePolygon)
                {
                    bool classified = false;
                    if (infillPointsByLayer.TryGetValue(layer, out List<Point3d> infillPoints) &&
                        infillPoints.Count > 0)
                    {
                        classified = true;
                        materialInside = false;
                        foreach (Point3d point in infillPoints)
                        {
                            PointContainment containment =
                                workingCurve.Contains(point, curvePlane, tolerance);
                            if (containment == PointContainment.Inside)
                            {
                                materialInside = true;
                                break;
                            }
                        }
                    }
                    if (!classified)
                        inwardFallbacks++;
                }

                result.Add(new ShellBranch(
                    treePath,
                    StackSignature(treePath, commonPrefix),
                    layer,
                    layerOrdinals[layer],
                    planes,
                    workingCurve,
                    curvePlane,
                    materialInside,
                    usePolygon));
            }
            return result;
        }

        private static PocketAnchor ProjectAnchor(
            Point3d input,
            double horizontalRadius,
            double verticalRadius,
            double depth,
            int pocketType,
            IEnumerable<ShellBranch> branches)
        {
            ShellBranch closestBranch = null;
            Point3d closestPoint = Point3d.Unset;
            double closestDistance = double.PositiveInfinity;
            foreach (ShellBranch branch in branches)
            {
                if (!branch.Curve.ClosestPoint(input, out double parameter))
                    continue;
                Point3d projected = branch.Curve.PointAt(parameter);
                double distance = projected.DistanceTo(input);
                if (distance < closestDistance)
                {
                    closestDistance = distance;
                    closestPoint = projected;
                    closestBranch = branch;
                }
            }
            return closestBranch == null
                ? null
                : new PocketAnchor(
                    input,
                    closestPoint,
                    closestDistance,
                    horizontalRadius,
                    verticalRadius,
                    depth,
                    pocketType,
                    closestBranch.StackSignature,
                    closestBranch.LayerOrdinal);
        }

        private static Dictionary<string, List<PocketInfluence>> BuildInfluencesByBranch(
            IEnumerable<ShellBranch> branches,
            IEnumerable<PocketAnchor> anchors)
        {
            var result = new Dictionary<string, List<PocketInfluence>>();
            List<IGrouping<int, ShellBranch>> layers = branches
                .GroupBy(branch => branch.LayerOrdinal)
                .OrderBy(group => group.Key)
                .ToList();

            foreach (PocketAnchor anchor in anchors)
            {
                foreach (IGrouping<int, ShellBranch> layer in layers)
                {
                    if (anchor.PocketType == 1 && layer.Key > anchor.LayerOrdinal)
                        continue; // Half pocket downward: anchor layer and below only.
                    if (anchor.PocketType == 2 && layer.Key < anchor.LayerOrdinal)
                        continue; // Half pocket upward: anchor layer and above only.

                    ShellBranch bestBranch = null;
                    PocketInfluence bestInfluence = null;
                    foreach (ShellBranch branch in layer)
                    {
                        if (!branch.TryCreateInfluence(anchor, out PocketInfluence influence))
                            continue;
                        if (bestInfluence == null ||
                            influence.VerticalDistance < bestInfluence.VerticalDistance)
                        {
                            bestBranch = branch;
                            bestInfluence = influence;
                        }
                    }

                    if (bestBranch == null || bestInfluence == null)
                        continue;
                    string key = PathKey(bestBranch.Path);
                    if (!result.TryGetValue(key, out List<PocketInfluence> matched))
                    {
                        matched = new List<PocketInfluence>();
                        result[key] = matched;
                    }
                    matched.Add(bestInfluence);
                }
            }
            return result;
        }

        private static bool TryEllipticalDistance(
            ShellBranch shell,
            int pointIndex,
            PocketInfluence influence,
            out double normalizedDistance)
        {
            normalizedDistance = double.PositiveInfinity;
            PocketAnchor anchor = influence?.Anchor;
            if (shell == null ||
                anchor == null ||
                anchor.HorizontalRadius <= 0.0 ||
                anchor.VerticalRadius <= 0.0 ||
                pointIndex < 0 ||
                pointIndex >= shell.PointStations.Count)
                return false;

            double verticalDistance = influence.VerticalDistance;
            if (verticalDistance > anchor.VerticalRadius)
                return false;

            double totalLength = shell.TotalLength;
            if (!double.IsFinite(totalLength) || totalLength <= 0.0)
                return false;

            double forwardLength = Math.Abs(
                shell.PointStations[pointIndex] - influence.ReferenceStation);
            double horizontalDistance = shell.IsClosedLoop
                ? Math.Min(forwardLength, totalLength - forwardLength)
                : forwardLength;
            if (horizontalDistance > anchor.HorizontalRadius)
                return false;

            double horizontalRatio = horizontalDistance / anchor.HorizontalRadius;
            double verticalRatio = verticalDistance / anchor.VerticalRadius;
            normalizedDistance = Math.Sqrt(
                horizontalRatio * horizontalRatio +
                verticalRatio * verticalRatio);
            return double.IsFinite(normalizedDistance) && normalizedDistance <= 1.0;
        }

        private static Curve CurveAt(WasperPrintPath path, GH_Path treePath)
        {
            if (path.SourceCurves != null && path.SourceCurves.PathExists(treePath))
            {
                IList<Curve> branch = path.SourceCurves.Branch(treePath);
                if (branch != null && branch.Count > 0 && branch[0] != null)
                    return branch[0].DuplicateCurve();
            }
            if (!path.PtPlanes.PathExists(treePath))
                return null;
            IList<Plane> planes = path.PtPlanes.Branch(treePath);
            if (planes == null || planes.Count < 2)
                return null;
            return new PolylineCurve(planes.Select(plane => plane.Origin));
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

        private static string StackSignature(GH_Path path, int layerPosition)
        {
            var indices = new List<int>();
            for (int i = 0; i < path.Length; i++)
            {
                if (i != layerPosition)
                    indices.Add(path[i]);
            }
            return string.Join(";", indices);
        }

        private static string PathKey(GH_Path path) => path?.ToString() ?? string.Empty;

        private static void AppendPlanes(
            DataTree<Plane> destination,
            GH_Path path,
            IEnumerable<Plane> planes)
        {
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
            if (index <= 0) return planes[1].Origin - planes[0].Origin;
            if (index >= count - 1) return planes[count - 1].Origin - planes[count - 2].Origin;
            return planes[index + 1].Origin - planes[index - 1].Origin;
        }

        private static double LocalShortestSegment(
            IList<Plane> planes,
            int index,
            int count,
            bool cyclic)
        {
            double before = index > 0
                ? planes[index].Origin.DistanceTo(planes[index - 1].Origin)
                : cyclic ? planes[0].Origin.DistanceTo(planes[count - 1].Origin) : double.PositiveInfinity;
            double after = index < count - 1
                ? planes[index].Origin.DistanceTo(planes[index + 1].Origin)
                : cyclic ? planes[count - 1].Origin.DistanceTo(planes[0].Origin) : double.PositiveInfinity;
            double result = Math.Min(before, after);
            return double.IsFinite(result) ? result : 0.0;
        }

        private static DataTree<Curve> BuildSourceCurves(
            DataTree<Plane> planes,
            DataTree<Curve> originalSourceCurves,
            double tolerance)
        {
            var curves = new DataTree<Curve>();
            for (int b = 0; b < planes.BranchCount; b++)
            {
                GH_Path path = planes.Paths[b];
                IList<Plane> branch = planes.Branches[b];
                if (branch == null || branch.Count < 2)
                    continue;
                var points = branch.Select(plane => plane.Origin).ToList();
                if (SourceCurveIsClosed(originalSourceCurves, path) &&
                    points[0].DistanceTo(points[points.Count - 1]) > tolerance)
                    points.Add(points[0]);
                curves.Add(new PolylineCurve(points), path);
            }
            return curves.BranchCount > 0 ? curves : null;
        }

        private static bool SourceCurveIsClosed(
            DataTree<Curve> curves,
            GH_Path path)
        {
            if (curves == null || !curves.PathExists(path))
                return false;
            IList<Curve> branch = curves.Branch(path);
            return branch != null && branch.Count > 0 &&
                   branch[0] != null && branch[0].IsClosed;
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

        private static string BuildDeformationCacheSignature(
            WasperPrintPath source,
            IList<int> targetRoles,
            IList<ShellBranch> shellBranches,
            IList<Point3d> pocketPoints,
            IList<double> horizontalRadii,
            IList<double> verticalRadii,
            IList<double> depths,
            IList<int> pocketTypes,
            IDictionary<int, List<Point3d>> infillPointsByLayer,
            bool usedAnyRoleFallback,
            IList<GeometryBase> masks,
            bool invert,
            IList<double> maxDistances,
            double tolerance)
        {
            WasperCacheSignature signature = WasperCacheSignature.Create();
            signature.Add(WasperRoleBranchTransformCache.SelectedGeometrySignature(
                source,
                targetRoles));
            signature.Add(usedAnyRoleFallback);
            signature.Add(invert);
            signature.Add(masks?.Count ?? 0);
            if (masks != null)
            {
                foreach (GeometryBase mask in masks)
                    signature.Add(mask);
            }
            signature.Add(maxDistances);
            signature.Add(tolerance);
            signature.Add(pocketPoints?.Count ?? 0);
            if (pocketPoints != null)
            {
                foreach (Point3d point in pocketPoints)
                    signature.Add(point);
            }
            signature.Add(horizontalRadii);
            signature.Add(verticalRadii);
            signature.Add(depths);
            signature.Add(pocketTypes?.Count ?? 0);
            if (pocketTypes != null)
            {
                foreach (int pocketType in pocketTypes)
                    signature.Add(pocketType);
            }

            var classifications = new List<string>();
            if (shellBranches != null)
            {
                foreach (ShellBranch shell in shellBranches)
                {
                    WasperCacheSignature shellSignature = WasperCacheSignature.Create();
                    shellSignature.Add(shell.Curve);
                    shellSignature.Add(shell.MaterialInside);
                    shellSignature.Add(shell.IsClosedLoop);
                    shellSignature.Add(shell.UsePolygonClassification);
                    classifications.Add(shellSignature.Finish());
                }
            }
            classifications.Sort(StringComparer.Ordinal);
            signature.Add(classifications.Count);
            foreach (string classification in classifications)
                signature.Add(classification);

            // Open/non-planar branches resolve their inward direction from the nearest
            // same-layer Infill sample at solve time (not baked into MaterialInside above),
            // so the raw Infill points must also participate in the cache signature.
            var infillLayers = new List<int>(infillPointsByLayer?.Keys ?? Array.Empty<int>());
            infillLayers.Sort();
            signature.Add(infillLayers.Count);
            foreach (int layer in infillLayers)
            {
                signature.Add(layer);
                List<Point3d> points = infillPointsByLayer[layer];
                signature.Add(points?.Count ?? 0);
                if (points != null)
                    foreach (Point3d point in points)
                        signature.Add(point);
            }
            return signature.Finish();
        }

        private sealed class PocketCacheEntry
        {
            public DataTree<Plane> Planes;
            public List<PocketAnchor> Anchors;
            public double MaximumProjectionDistance;
            public int DistantAnchors;
            public int MovedLocations;
            public int AffectedBranches;
            public int LargeMoves;
            public int DirectionFailures;
            public int UnclassifiedDirections;
            public int MaskExcluded;
            public int DistanceExcluded;
            public double MaximumMove;
        }

        private sealed class PocketAnchor
        {
            public PocketAnchor(
                Point3d inputPoint,
                Point3d projectedPoint,
                double projectionDistance,
                double horizontalRadius,
                double verticalRadius,
                double depth,
                int pocketType,
                string stackSignature,
                int layerOrdinal)
            {
                InputPoint = inputPoint;
                ProjectedPoint = projectedPoint;
                ProjectionDistance = projectionDistance;
                HorizontalRadius = horizontalRadius;
                VerticalRadius = verticalRadius;
                Depth = depth;
                PocketType = pocketType;
                StackSignature = stackSignature;
                LayerOrdinal = layerOrdinal;
            }

            public Point3d InputPoint { get; }
            public Point3d ProjectedPoint { get; }
            public double ProjectionDistance { get; }
            public double HorizontalRadius { get; }
            public double VerticalRadius { get; }
            public double Depth { get; }
            // 0 = Regular, 1 = Half pocket downward (anchor layer and below), 2 = Half pocket
            // upward (anchor layer and above).
            public int PocketType { get; }
            public string StackSignature { get; }
            public int LayerOrdinal { get; }
        }

        private sealed class PocketInfluence
        {
            public PocketInfluence(
                PocketAnchor anchor,
                double referenceStation,
                double verticalDistance)
            {
                Anchor = anchor;
                ReferenceStation = referenceStation;
                VerticalDistance = verticalDistance;
            }

            public PocketAnchor Anchor { get; }
            public double ReferenceStation { get; }
            public double VerticalDistance { get; }
        }

        private sealed class ShellBranch
        {
            private readonly Plane _plane;
            private readonly bool _materialInside;
            private readonly bool _usePolygonClassification;

            public ShellBranch(
                GH_Path path,
                string stackSignature,
                int layer,
                int layerOrdinal,
                IList<Plane> planes,
                Curve curve,
                Plane plane,
                bool materialInside,
                bool usePolygonClassification)
            {
                Path = path;
                StackSignature = stackSignature;
                Layer = layer;
                LayerOrdinal = layerOrdinal;
                Planes = planes;
                Curve = curve;
                _plane = plane;
                _materialInside = materialInside;
                _usePolygonClassification = usePolygonClassification;
                IsClosedLoop = curve.IsClosed;
                TotalLength = curve.GetLength();
                PointStations = BuildPointStations(planes, TotalLength, 1e-12);
            }

            public GH_Path Path { get; }
            public string StackSignature { get; }
            public int Layer { get; }
            public int LayerOrdinal { get; }
            public IList<Plane> Planes { get; }
            public Curve Curve { get; }
            public bool IsClosedLoop { get; }
            public double TotalLength { get; }
            public IReadOnlyList<double> PointStations { get; }
            public bool MaterialInside => _materialInside;
            public bool UsePolygonClassification => _usePolygonClassification;

            public bool TryCreateInfluence(
                PocketAnchor anchor,
                out PocketInfluence influence)
            {
                influence = null;
                if (anchor == null || Curve == null ||
                    !Curve.ClosestPoint(anchor.ProjectedPoint, out double parameter))
                    return false;

                Point3d layerReference = Curve.PointAt(parameter);
                double verticalDistance = layerReference.DistanceTo(anchor.ProjectedPoint);
                if (!double.IsFinite(verticalDistance) ||
                    verticalDistance > anchor.VerticalRadius)
                    return false;

                double referenceStation = Curve.GetLength(
                    new Interval(Curve.Domain.T0, parameter));
                if (!double.IsFinite(referenceStation))
                    return false;

                influence = new PocketInfluence(
                    anchor,
                    Math.Max(0.0, Math.Min(TotalLength, referenceStation)),
                    verticalDistance);
                return true;
            }

            private static IReadOnlyList<double> BuildPointStations(
                IList<Plane> planes,
                double exactLength,
                double tolerance)
            {
                int count = planes?.Count ?? 0;
                var stations = new double[count];
                if (count < 2)
                    return stations;

                bool duplicateClosure = count > 2 &&
                    planes[0].Origin.DistanceToSquared(planes[count - 1].Origin) <=
                    tolerance * tolerance;
                int logicalCount = duplicateClosure ? count - 1 : count;
                double chordLength = 0.0;
                for (int i = 1; i < logicalCount; i++)
                {
                    chordLength += planes[i - 1].Origin.DistanceTo(planes[i].Origin);
                    stations[i] = chordLength;
                }
                if (logicalCount > 1)
                    chordLength += planes[logicalCount - 1].Origin.DistanceTo(planes[0].Origin);

                double scale = chordLength > tolerance && exactLength > tolerance
                    ? exactLength / chordLength
                    : 1.0;
                for (int i = 1; i < logicalCount; i++)
                    stations[i] *= scale;
                if (duplicateClosure)
                    stations[count - 1] = exactLength;
                return stations;
            }

            /// <summary>
            /// Resolves which side of <paramref name="lateral"/> is the material side.
            /// Closed, planar branches use the exact point-in-polygon test against the
            /// projected loop. Open and/or non-planar branches (no polygon interior to
            /// test) instead use the side of the nearest same-layer Infill sample as a
            /// local material-side heuristic. <paramref name="classified"/> is false when
            /// neither method could resolve a side (no Infill reference available), in
            /// which case the outward (+lateral) default is returned.
            /// </summary>
            public Vector3d InwardDirection(
                Point3d point,
                Vector3d lateral,
                double tolerance,
                IReadOnlyList<Point3d> layerInfillPoints,
                out bool classified)
            {
                classified = true;
                if (_usePolygonClassification)
                {
                    double probe = Math.Max(tolerance * 10.0, 1e-6);
                    PointContainment containment = Curve.Contains(
                        point + probe * lateral,
                        _plane,
                        tolerance);
                    bool plusInside = containment == PointContainment.Inside;
                    return plusInside == _materialInside ? lateral : -lateral;
                }

                if (layerInfillPoints == null || layerInfillPoints.Count == 0)
                {
                    classified = false;
                    return lateral;
                }

                Point3d nearest = Point3d.Unset;
                double nearestDistanceSquared = double.PositiveInfinity;
                for (int i = 0; i < layerInfillPoints.Count; i++)
                {
                    double distanceSquared = layerInfillPoints[i].DistanceToSquared(point);
                    if (distanceSquared < nearestDistanceSquared)
                    {
                        nearestDistanceSquared = distanceSquared;
                        nearest = layerInfillPoints[i];
                    }
                }
                if (!nearest.IsValid)
                {
                    classified = false;
                    return lateral;
                }

                Vector3d toInfill = nearest - point;
                double side = Vector3d.Multiply(lateral, toInfill);
                return side >= 0.0 ? lateral : -lateral;
            }
        }

        private static class FuzzyPocketsIcon
        {
            private static readonly Lazy<Bitmap> Cached =
                new Lazy<Bitmap>(Create, true);

            public static Bitmap Bitmap => Cached.Value;

            private static Bitmap Create()
            {
                var bitmap = new Bitmap(24, 24);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.Clear(Color.Transparent);
                    using (var shellPen = new Pen(Color.FromArgb(35, 72, 86), 2.2f))
                    using (var pocketPen = new Pen(Color.FromArgb(234, 124, 42), 2.4f))
                    using (var pointBrush = new SolidBrush(Color.FromArgb(247, 191, 53)))
                    {
                        graphics.DrawLine(shellPen, 3, 4, 3, 20);
                        graphics.DrawBezier(pocketPen, 3, 6, 17, 7, 17, 17, 3, 18);
                        graphics.FillEllipse(pointBrush, 8, 3, 5, 5);
                        graphics.DrawEllipse(shellPen, 8, 3, 5, 5);
                    }
                }
                return bitmap;
            }
        }
    }
}
