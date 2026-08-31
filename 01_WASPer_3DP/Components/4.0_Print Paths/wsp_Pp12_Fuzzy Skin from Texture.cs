#region Component Description
/*
Component: wsp_Pp12_Fuzzy Skin from Texture
Nickname: Texture Fuzzy
Category: WASPer_3DP
SubCategory: 5.0_Gcode

Maps bitmap luminance or colored-mesh vertex colors to signed lateral path
displacement. Bitmap X follows normalized branch arclength and bitmap Y follows
the ordered logical-layer stack. Colored meshes are sampled at their closest
points and fade to zero at ref_dist.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Reflection;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;

using WASPer_3DP;

namespace WASPer_3DP_Components._4_0_Print_Paths
{
    public sealed class wsp_Pp12_Fuzzy_Skin_from_Texture : GH_Component
    {
        private readonly string _versionTag;
        private bool _constrainInfill = true;
        private static readonly string[] OutputCatalog = WasperPathDebugOutputs.CoreNickNames;
        private static readonly int AllOutputsMask = (1 << OutputCatalog.Length) - 1;
        private int _visibleOutputsMask;
        private string _deformationCacheSignature = string.Empty;
        private WasperPrintPath _deformationCacheSource;
        private TextureCacheEntry _deformationCache;
        private const string ConstrainInfillKey = "wsp_gc18_constrain_infill";
        // Legacy all-or-nothing key, still read for backward compatibility with saved files.
        private const string ShowAllOutputsKey = "wsp_gc18_show_all_outputs";
        private const string VisibleOutputsMaskKey = "wsp_gc18_visible_outputs_mask";

        public wsp_Pp12_Fuzzy_Skin_from_Texture()
            : base(
                "wsp_Pp12_Fuzzy Skin from Texture",
                "Texture Fuzzy",
                "PURPOSE\r\n" +
                "Creates fuzzy-skin displacement from a bitmap or colored mesh.\r\n\r\n" +
                "TEXTURE MAPPING\r\n" +
                "For bitmaps, X follows normalized branch arclength and Y follows the ordered " +
                "logical-layer stack. Colored meshes use interpolated vertex color at the closest " +
                "mesh point and fade to zero at ref_dist.\r\n\r\n" +
                "DISPLACEMENT\r\n" +
                "Black maps to the start of mag_domain and white to its end; alpha multiplies the " +
                "result. One mag_domain number means 0 to that value, including negative values. " +
                "pt_pattern alternates the local tan +90/-90 direction.\r\n\r\n" +
                "DATA AND SAFETY\r\n" +
                "Planar Infill is locally clipped only where a changed closed Shell makes a previously " +
                "valid path invalid. Plane orientation and point-matched process values are preserved; " +
                "stale geometry-dependent analysis, motion, KPI, and deposited-volume fields are cleared.\r\n\r\n" +
                "For Rodrigo Chiesse - ACTech Hub U Minho.\r\n\r\n" +
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
            new Guid("64CD46E8-2BBB-4880-9F57-1D692F8BEAAB");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon => TextureFuzzyIcon.Bitmap;

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
                    RecordUndoEvent("Toggle Pp12 debug outputs");
                    _visibleOutputsMask = mask;
                    WasperPathDebugOutputs.Rebuild(
                        this,
                        _visibleOutputsMask,
                        "Texture displacement, filtering, safety, and Infill-constraint report.",
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
                "Texture displacement, filtering, safety, and Infill-constraint report.",
                OutputCatalog);
            return base.Read(reader);
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "WASPer Print Path whose canonical pt_plane origins will be displaced. Plane " +
                "orientation and point-matched process values are preserved. Please use the Pp01 WASPer Path from Curves before using this component.",
                GH_ParamAccess.item);

            int pointPatternIndex = p.AddIntegerParameter(
                "point pattern",
                "pt_pattern",
                "Optional repeating 0/1 pattern applied independently along each branch. " +
                "0 uses local tan + 90 degrees (plane Z x tangent); 1 uses tan - 90 degrees. " +
                "Signed values from mag_domain may reverse either direction. Default: [0].",
                GH_ParamAccess.list,
                0);

            int layerPatternIndex = p.AddIntegerParameter(
                "layer pattern",
                "la_pattern",
                "Optional repeating logical-layer pattern. 0 preserves the layer and 1 enables " +
                "texture displacement. Default: [1] (all layers).",
                GH_ParamAccess.list);
            p[layerPatternIndex].Optional = true;

            int magnitudeDomainIndex = p.AddGenericParameter(
                "magnitude domain",
                "mag_domain",
                "Signed displacement domain in model units. Supply a Grasshopper Domain/Interval " +
                "to map black to its start and white to its end. A single number x is interpreted " +
                "as the ordered domain 0 to x, whether x is positive or negative. If omitted, " +
                "defaults to 0 to 0.",
                GH_ParamAccess.item);
            p[magnitudeDomainIndex].Optional = true;

            p.AddParameter(global::WASPer_3DP.WasperTargetRolesParam.Create(
                "Selects which semantic path branches receive texture fuzzy skin. 0 = All paths, " +
                "1 = Shell (default), 2 = Infill, 3 = Partition, 4 = Support, 5 = Transition, 6 = Undefined. Supply several role-specific " +
                "values to include them and exclude the others. All paths (0) cannot be combined. " +
                "Undefined/legacy branches can be selected explicitly with 4.",
                1));

            p.AddGenericParameter(
                "bitmap or colored mesh",
                "bit_or_mesh",
                "Texture reference. Accepts a System.Drawing bitmap, an image file path, or one " +
                "valid Mesh with one vertex color per vertex. Bitmap X maps along normalized " +
                "branch arclength and Y maps from the first logical layer at the image bottom to " +
                "the last layer at the image top. Closed branches wrap horizontally. Colored " +
                "meshes are sampled spatially at the closest mesh point.",
                GH_ParamAccess.item);

            p.AddNumberParameter(
                "reference distance",
                "ref_dist",
                "Positive mesh influence distance in model units. Colored-mesh displacement " +
                "fades linearly from full strength on the mesh to zero at ref_dist. Bitmap " +
                "path-space mapping does not use this distance. Default: 10.",
                GH_ParamAccess.item,
                10.0);

            int maskIndex = p.AddGeometryParameter(
                "mask",
                "mask",
                "Optional closed geometry used to filter points. Supported masks are closed " +
                "planar curves, solid Breps/Extrusions, and closed meshes. Multiple masks act " +
                "as a union. With no mask, every otherwise eligible point may move.",
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
                "WASPer Print Path with texture-displaced pt_plane origins. Tree topology, " +
                "plane orientation, roles, and point-matched process values are preserved. " +
                "Source-curve provenance is rebuilt from the resulting paths.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "summary",
                "summary",
                "Texture mapping, filtering, displacement, rejected-reference, and cleared-data summary.",
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
                pointPattern.Add(0);
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

            if (!TryGetMagnitudeDomain(da, 3, out Interval magnitudeDomain, out string domainError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, domainError);
                return;
            }

            var targetRoles = new List<int>();
            da.GetDataList(4, targetRoles);
            if (!WasperGcodeTreeUtil.TryNormalizeTargetRoles(
                    targetRoles,
                    out targetRoles,
                    out string roleError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, roleError);
                return;
            }

            object rawReference = null;
            if (!da.GetData(5, ref rawReference) || rawReference == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Supply one bitmap, image file path, or colored mesh to bit_or_mesh.");
                return;
            }

            double referenceDistance = 10.0;
            da.GetData(6, ref referenceDistance);
            if (!double.IsFinite(referenceDistance) || referenceDistance <= 0.0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "ref_dist must be positive and finite.");
                return;
            }

            if (!TextureField.TryCreate(
                    rawReference,
                    out TextureField texture,
                    out string textureError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, textureError);
                return;
            }

            var rawMasks = new List<GeometryBase>();
            da.GetDataList(7, rawMasks);
            bool invert = false;
            da.GetData(8, ref invert);

            double tolerance = Math.Max(
                RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001,
                1e-9);

            List<MaskRegion> masks = BuildMasks(rawMasks, tolerance, out int rejectedMasks);
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

            var outputPlanes = new DataTree<Plane>();
            int targetedBranches = 0;
            int roleExcluded = 0;
            int layerExcluded = 0;
            int maskExcluded = 0;
            int referenceExcluded = 0;
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
                magnitudeDomain,
                texture.CacheSignature,
                referenceDistance,
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
                referenceExcluded = _deformationCache.ReferenceExcluded;
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
                double[] arcCoordinates = NormalizedArcCoordinates(
                    incoming,
                    logicalCount,
                    cyclic);
                double layerCoordinate = orderedLayers.Count <= 1
                    ? 0.0
                    : layerOrdinal / (double)(orderedLayers.Count - 1);

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

                    if (!texture.TrySample(
                            point,
                            arcCoordinates[pointIndex],
                            layerCoordinate,
                            cyclic,
                            referenceDistance,
                            out double textureValue,
                            out double textureWeight))
                    {
                        referenceExcluded++;
                        movedBranch.Add(plane);
                        continue;
                    }

                    double mappedMagnitude =
                        magnitudeDomain.T0 +
                        textureValue * (magnitudeDomain.T1 - magnitudeDomain.T0);
                    double magnitude = mappedMagnitude * textureWeight;
                    if (Math.Abs(magnitude) <= tolerance * 1e-6)
                    {
                        zeroMagnitude++;
                        movedBranch.Add(plane);
                        continue;
                    }

                    Vector3d tangent = LocalTangent(
                        incoming,
                        pointIndex,
                        logicalCount,
                        cyclic);
                    Vector3d side = Vector3d.CrossProduct(plane.ZAxis, tangent);
                    if (!tangent.Unitize() || !side.Unitize())
                    {
                        tangentFailures++;
                        movedBranch.Add(plane);
                        continue;
                    }

                    int patternValue = pointPattern[pointIndex % pointPattern.Count];
                    if (patternValue == 1)
                        side.Reverse();

                    double absoluteMagnitude = Math.Abs(magnitude);
                    double localLength = LocalShortestSegment(
                        incoming,
                        pointIndex,
                        logicalCount,
                        cyclic);
                    if (localLength > tolerance &&
                        absoluteMagnitude > localLength * 0.5)
                    {
                        largeMagnitudeMoves++;
                    }

                    plane.Origin = point + side * magnitude;
                    movedBranch.Add(plane);
                    moved++;
                    maximumDisplacement = Math.Max(
                        maximumDisplacement,
                        absoluteMagnitude);
                }

                if (duplicateClosure)
                {
                    Plane closingPlane = incoming[incoming.Count - 1];
                    if (movedBranch.Count > 0)
                        closingPlane.Origin = movedBranch[0].Origin;
                    movedBranch.Add(closingPlane);
                    if (closingPlane.Origin.DistanceTo(
                            incoming[incoming.Count - 1].Origin) > tolerance)
                    {
                        moved++;
                    }
                }

                foreach (Plane plane in movedBranch)
                    outputPlanes.Add(plane, path);
            }

            if (!deformationCacheHit)
            {
                _deformationCacheSignature = deformationSignature;
                _deformationCacheSource = source;
                _deformationCache = new TextureCacheEntry
                {
                    Planes = outputPlanes,
                    TargetedBranches = targetedBranches,
                    RoleExcluded = roleExcluded,
                    LayerExcluded = layerExcluded,
                    MaskExcluded = maskExcluded,
                    ReferenceExcluded = referenceExcluded,
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
                    $"Texture fuzzy skin | target_roles={WasperGcodeTreeUtil.TargetRoleNames(targetRoles)} | " +
                    "targeted branches=0 | unchanged");
                return;
            }

            if (largeMagnitudeMoves > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{largeMagnitudeMoves} moved location(s) use an absolute magnitude greater " +
                    "than half a neighbouring segment length. Review the result for folded or crossing segments.");
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
                    "Texture Fuzzy Skin changed path geometry. Spatial printability, structural " +
                    "risk, motion-plan, KPI, and deposited-volume fields were cleared because " +
                    "they no longer describe the result.");
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
                    sourceCurves: BuildSourceCurves(
                        outputPlanes,
                        source.SourceCurves,
                        tolerance),
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
                    // Remark, not Warning: a layer without a matching closed planar Shell is a
                    // normal outcome on non-planar or open-shell prints. The Infill passes
                    // through unchanged, so nothing is lost and no action is required.
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Remark,
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
            string referenceLabel = texture.IsBitmap
                ? $"{texture.Description}; path-space mapping; ref_dist=n/a"
                : $"{texture.Description}; ref_dist={referenceDistance:0.###}";
            totalWatch.Stop();
            string summary =
                $"Texture fuzzy skin | branches={source.PtPlanes.BranchCount} | " +
                $"target_roles={WasperGcodeTreeUtil.TargetRoleNames(targetRoles)} | " +
                $"targeted branches={targetedBranches} | layers={orderedLayers.Count} | " +
                $"pt_pattern=[{string.Join(",", pointPattern)}] | " +
                $"la_pattern=[{string.Join(",", layerPattern)}] | " +
                $"mag_domain={magnitudeDomain.T0:0.###} to {magnitudeDomain.T1:0.###} model units | " +
                $"reference={referenceLabel} | mask={maskLabel} | moved={moved} | " +
                $"max displacement={maximumDisplacement:0.###} | role excluded={roleExcluded} | " +
                $"layer excluded={layerExcluded} | mask excluded={maskExcluded} | " +
                $"reference excluded={referenceExcluded} | zero magnitude={zeroMagnitude} | " +
                $"direction failures={tangentFailures} | large-magnitude moves={largeMagnitudeMoves} | " +
                $"shell deformation cache={(deformationCacheHit ? "reused" : "rebuilt")} | " +
                $"{(_constrainInfill ? constraintReport?.Summary ?? "infill constraint: not applicable" : "infill constraint: off")} | " +
                $"derived data cleared={(clearedDerivedData ? "yes" : "no")}.\n" +
                $"performance [ms]: total={totalWatch.Elapsed.TotalMilliseconds:0.###}";

            WasperPathDebugOutputs.Set(da, this, output, summary);
            Message = _versionTag;
        }

        private static bool TryGetMagnitudeDomain(
            IGH_DataAccess da,
            int inputIndex,
            out Interval domain,
            out string error)
        {
            domain = new Interval(0.0, 0.0);
            error = null;

            object raw = null;
            if (!da.GetData(inputIndex, ref raw) || raw == null)
                return true;

            object value = Unwrap(raw);
            if (value is Interval interval)
            {
                domain = interval;
            }
            else if (value is GH_Interval ghInterval)
            {
                domain = ghInterval.Value;
            }
            else if (TryConvertNumber(value, out double number))
            {
                domain = new Interval(0.0, number);
            }
            else
            {
                error = "mag_domain must be one Domain/Interval or one number.";
                return false;
            }

            if (!double.IsFinite(domain.T0) || !double.IsFinite(domain.T1))
            {
                error = "mag_domain endpoints must be finite.";
                return false;
            }
            return true;
        }

        private static bool TryConvertNumber(object value, out double number)
        {
            number = 0.0;
            if (value is GH_Number ghNumber)
            {
                number = ghNumber.Value;
                return true;
            }
            if (value is double d) { number = d; return true; }
            if (value is float f) { number = f; return true; }
            if (value is int i) { number = i; return true; }
            if (value is long l) { number = l; return true; }
            return false;
        }

        private static object Unwrap(object value)
        {
            object current = value;
            for (int i = 0; i < 4 && current is IGH_Goo goo; i++)
            {
                object next = goo.ScriptVariable();
                if (next == null || ReferenceEquals(next, current))
                    break;
                current = next;
            }
            return current;
        }

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

        private static double[] NormalizedArcCoordinates(
            IList<Plane> planes,
            int count,
            bool cyclic)
        {
            var coordinates = new double[Math.Max(0, count)];
            if (planes == null || count <= 1)
                return coordinates;

            double length = 0.0;
            for (int i = 1; i < count; i++)
            {
                length += planes[i - 1].Origin.DistanceTo(planes[i].Origin);
                coordinates[i] = length;
            }
            double total = length;
            if (cyclic)
                total += planes[count - 1].Origin.DistanceTo(planes[0].Origin);
            if (!double.IsFinite(total) || total <= 1e-12)
                return coordinates;
            for (int i = 0; i < coordinates.Length; i++)
                coordinates[i] /= total;
            return coordinates;
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
                        PointContainment containment =
                            curve.Contains(point, plane, tolerance);
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
                        : new MaskRegion(point =>
                            extrusionBrep.IsPointInside(point, tolerance, true));
                }
                if (geometry is Mesh mesh && mesh.IsClosed)
                    return new MaskRegion(point => mesh.IsPointInside(point, tolerance, true));
                return null;
            }
        }

        private static string BuildDeformationCacheSignature(
            WasperPrintPath source,
            IList<int> targetRoles,
            IList<int> pointPattern,
            IList<int> layerPattern,
            Interval magnitudeDomain,
            string textureSignature,
            double referenceDistance,
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
            signature.Add(magnitudeDomain.T0);
            signature.Add(magnitudeDomain.T1);
            signature.Add(textureSignature);
            signature.Add(referenceDistance);
            signature.Add(invert);
            signature.Add(tolerance);
            signature.Add(masks?.Count ?? 0);
            if (masks != null)
            {
                foreach (GeometryBase mask in masks)
                    signature.Add(mask);
            }
            return signature.Finish();
        }

        private sealed class TextureCacheEntry
        {
            public DataTree<Plane> Planes;
            public int TargetedBranches;
            public int RoleExcluded;
            public int LayerExcluded;
            public int MaskExcluded;
            public int ReferenceExcluded;
            public int Moved;
            public int ZeroMagnitude;
            public int TangentFailures;
            public int LargeMagnitudeMoves;
            public double MaximumDisplacement;
        }

        private abstract class TextureField
        {
            public abstract bool IsBitmap { get; }
            public abstract string Description { get; }
            public abstract string CacheSignature { get; }

            public abstract bool TrySample(
                Point3d point,
                double branchCoordinate,
                double layerCoordinate,
                bool wrapBranch,
                double referenceDistance,
                out double value,
                out double weight);

            public static bool TryCreate(
                object raw,
                out TextureField texture,
                out string error)
            {
                texture = null;
                error = null;
                object value = Unwrap(raw);

                if (value is Mesh mesh)
                {
                    if (!mesh.IsValid)
                    {
                        error = "bit_or_mesh contains an invalid mesh.";
                        return false;
                    }
                    if (mesh.VertexColors.Count != mesh.Vertices.Count ||
                        mesh.VertexColors.Count == 0)
                    {
                        error = "The reference mesh must contain one vertex color per mesh vertex.";
                        return false;
                    }
                    texture = new ColoredMeshField(mesh.DuplicateMesh());
                    return true;
                }

                try
                {
                    if (value is Bitmap bitmap)
                    {
                        texture = new BitmapField(bitmap, "bitmap");
                        return true;
                    }
                    if (value is Image image)
                    {
                        using var bitmapFromImage = new Bitmap(image);
                        texture = new BitmapField(bitmapFromImage, "image");
                        return true;
                    }
                    if (value is string filePath)
                    {
                        if (!File.Exists(filePath))
                        {
                            error = $"Image file does not exist: {filePath}";
                            return false;
                        }
                        using Image loaded = Image.FromFile(filePath);
                        using var loadedBitmap = new Bitmap(loaded);
                        texture = new BitmapField(
                            loadedBitmap,
                            $"bitmap {Path.GetFileName(filePath)}");
                        return true;
                    }
                }
                catch (Exception exception)
                {
                    error = $"Could not read bitmap reference: {exception.Message}";
                    return false;
                }

                error = "bit_or_mesh must contain one bitmap, image file path, or colored mesh.";
                return false;
            }

            protected static double ColorLuminance(Color color)
            {
                return Math.Max(
                    0.0,
                    Math.Min(
                        1.0,
                        (0.2126 * color.R +
                         0.7152 * color.G +
                         0.0722 * color.B) / 255.0));
            }

            protected static double ColorAlpha(Color color) =>
                Math.Max(0.0, Math.Min(1.0, color.A / 255.0));
        }

        private sealed class ColoredMeshField : TextureField
        {
            private readonly Mesh _mesh;

            public ColoredMeshField(Mesh mesh)
            {
                _mesh = mesh;
            }

            public override bool IsBitmap => false;
            public override string Description =>
                $"colored mesh ({_mesh.Vertices.Count} vertices)";
            public override string CacheSignature
            {
                get
                {
                    WasperCacheSignature signature = WasperCacheSignature.Create();
                    signature.Add(_mesh);
                    return signature.Finish();
                }
            }

            public override bool TrySample(
                Point3d point,
                double branchCoordinate,
                double layerCoordinate,
                bool wrapBranch,
                double referenceDistance,
                out double value,
                out double weight)
            {
                value = 0.0;
                weight = 0.0;
                MeshPoint meshPoint =
                    _mesh.ClosestMeshPoint(point, referenceDistance);
                if (meshPoint == null)
                    return false;

                double distance = point.DistanceTo(meshPoint.Point);
                if (!double.IsFinite(distance) || distance > referenceDistance)
                    return false;

                Color color = _mesh.ColorAt(meshPoint);
                value = ColorLuminance(color);
                double falloff = Math.Max(
                    0.0,
                    Math.Min(1.0, 1.0 - distance / referenceDistance));
                weight = ColorAlpha(color) * falloff;
                return true;
            }
        }

        private sealed class BitmapField : TextureField
        {
            private readonly int _width;
            private readonly int _height;
            private readonly double[] _luminance;
            private readonly double[] _alpha;
            private readonly string _description;
            private readonly string _cacheSignature;

            public BitmapField(Bitmap source, string description)
            {
                if (source == null || source.Width < 1 || source.Height < 1)
                    throw new ArgumentException("Bitmap is empty.");

                _width = source.Width;
                _height = source.Height;
                _description = $"{description} ({_width}x{_height})";
                _luminance = new double[_width * _height];
                _alpha = new double[_width * _height];

                using var converted = new Bitmap(
                    _width,
                    _height,
                    PixelFormat.Format32bppArgb);
                using (Graphics graphics = Graphics.FromImage(converted))
                    graphics.DrawImageUnscaled(source, 0, 0);

                Rectangle bounds = new Rectangle(0, 0, _width, _height);
                BitmapData data = converted.LockBits(
                    bounds,
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);
                try
                {
                    int stride = Math.Abs(data.Stride);
                    var bytes = new byte[stride * _height];
                    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                    for (int y = 0; y < _height; y++)
                    {
                        int sourceRow = data.Stride >= 0
                            ? y * stride
                            : (_height - 1 - y) * stride;
                        for (int x = 0; x < _width; x++)
                        {
                            int sourceIndex = sourceRow + x * 4;
                            Color color = Color.FromArgb(
                                bytes[sourceIndex + 3],
                                bytes[sourceIndex + 2],
                                bytes[sourceIndex + 1],
                                bytes[sourceIndex]);
                            int targetIndex = y * _width + x;
                            _luminance[targetIndex] = ColorLuminance(color);
                            _alpha[targetIndex] = ColorAlpha(color);
                        }
                    }
                }
                finally
                {
                    converted.UnlockBits(data);
                }
                WasperCacheSignature signature = WasperCacheSignature.Create();
                signature.Add(_width);
                signature.Add(_height);
                signature.Add(_luminance);
                signature.Add(_alpha);
                _cacheSignature = signature.Finish();
            }

            public override bool IsBitmap => true;
            public override string Description => _description;
            public override string CacheSignature => _cacheSignature;

            public override bool TrySample(
                Point3d point,
                double branchCoordinate,
                double layerCoordinate,
                bool wrapBranch,
                double referenceDistance,
                out double value,
                out double weight)
            {
                double u = wrapBranch
                    ? branchCoordinate - Math.Floor(branchCoordinate)
                    : Math.Max(0.0, Math.Min(1.0, branchCoordinate));
                double v = Math.Max(0.0, Math.Min(1.0, layerCoordinate));
                double x = u * (_width - 1);
                double y = (1.0 - v) * (_height - 1);

                value = Bilinear(_luminance, x, y);
                weight = Bilinear(_alpha, x, y);
                return true;
            }

            private double Bilinear(double[] values, double x, double y)
            {
                int x0 = Math.Max(0, Math.Min(_width - 1, (int)Math.Floor(x)));
                int y0 = Math.Max(0, Math.Min(_height - 1, (int)Math.Floor(y)));
                int x1 = Math.Min(_width - 1, x0 + 1);
                int y1 = Math.Min(_height - 1, y0 + 1);
                double tx = x - x0;
                double ty = y - y0;
                double a = values[y0 * _width + x0];
                double b = values[y0 * _width + x1];
                double c = values[y1 * _width + x0];
                double d = values[y1 * _width + x1];
                return
                    a * (1.0 - tx) * (1.0 - ty) +
                    b * tx * (1.0 - ty) +
                    c * (1.0 - tx) * ty +
                    d * tx * ty;
            }
        }

        private static class TextureFuzzyIcon
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

                    Rectangle gradientBox = new Rectangle(3, 3, 18, 7);
                    using (var gradient = new LinearGradientBrush(
                               gradientBox,
                               Color.FromArgb(35, 48, 58),
                               Color.FromArgb(244, 167, 44),
                               0.0f))
                    {
                        graphics.FillRectangle(gradient, gradientBox);
                    }
                    using var outline = new Pen(Color.FromArgb(55, 78, 92), 1.0f);
                    graphics.DrawRectangle(outline, gradientBox);

                    PointF[] texturedPath =
                    {
                        new PointF(2, 18),
                        new PointF(5, 14),
                        new PointF(8, 20),
                        new PointF(11, 13),
                        new PointF(14, 19),
                        new PointF(18, 12),
                        new PointF(22, 17)
                    };
                    using var pathPen = new Pen(Color.FromArgb(44, 157, 195), 2.1f);
                    graphics.DrawLines(pathPen, texturedPath);
                    using var pointBrush = new SolidBrush(Color.FromArgb(231, 135, 29));
                    foreach (PointF point in texturedPath.Skip(1).Take(5))
                        graphics.FillEllipse(
                            pointBrush,
                            point.X - 1.3f,
                            point.Y - 1.3f,
                            2.6f,
                            2.6f);

                    return _bitmap;
                }
            }
        }
    }
}
