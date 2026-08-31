#region Component Description
/*
Component: wsp_Pp09_Morph Path Between Surfaces
Nickname: Morph Path
Category: WASPer_3DP
SubCategory: 5.0_Gcode

Morphs an existing WASPer Print Path through one or more ordered Surface, Brep,
Extrusion, or Mesh references. One geometry uses ref_plane as its lower support.
Projection follows stored layer normals or ref_crv tangents and is oriented
upward. Cumulative source layer_h keeps the first deposited path above each
lower support while the final layer may touch the upper reference.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

using WASPer_3DP;

namespace WASPer_3DP_Components._4_0_Print_Paths
{
    public sealed class wsp_Pp09_Morph_Path_Between_Surfaces : GH_Component
    {
        private readonly string _versionTag;
        // Legacy all-or-nothing key, still read for backward compatibility with saved files.
        private const string ShowAllOutputsKey = "wsp_gc21_morph_show_all_outputs";
        private const string VisibleOutputsMaskKey = "wsp_gc21_morph_visible_outputs_mask";
        private static readonly string[] OutputCatalog =
        {
            "pt_planes",
            "la_planes",
            "la_planes_m",
            "plane_dev",
            "flows",
            "layer_h",
            "layer_w",
            "layer_wf",
            "print_speed",
            "print_vol",
            "path_role"
        };
        private static readonly int AllOutputsMask = (1 << OutputCatalog.Length) - 1;
        private int _visibleOutputsMask;

        public wsp_Pp09_Morph_Path_Between_Surfaces()
            : base(
                "wsp_Pp09_Morph Path Between Surfaces",
                "Morph Path",
                "PURPOSE\r\n" +
                "Morphs a WASPer Print Path through one or more ordered reference geometries. " +
                "Surface, Brep, Extrusion, and Mesh references are supported. With one geometry, " +
                "ref_plane becomes the lower support and the supplied geometry is the upper target.\r\n\r\n" +
                "DIRECTION\r\n" +
                "Without ref_crv, stored authoritative layer-plane normals are preferred and " +
                "ref_plane.ZAxis is the fallback. With ref_crv, logical layers sample its tangent " +
                "by normalized arc length. Direction signs follow the actual first-to-last source " +
                "layer stack. Flattened ref_geo order is audited explicitly from bottom to top; " +
                "reversed pairs stop with an error and surface normals do not define list order.\r\n\r\n" +
                "INTERPOLATION\r\n" +
                "Source layer_h is accumulated through the stack. The first deposited path remains " +
                "at least one local source layer_h above its lower support, while the last layer may " +
                "touch the upper reference. Three or more geometries form ordered piecewise intervals. " +
                "Intermediate layers interpolate continuously, while local non-planar relief is retained as " +
                "a normalized offset within the stack. When authoritative layer reference planes " +
                "exist in wsp_path, their orientation is preserved but their effective origins are " +
                "re-anchored to the actual path-layer centres. Plane metadata therefore guides direction " +
                "without constraining layer position. strength blends original and morphed geometry.\r\n\r\n" +
                "DATA\r\n" +
                "Tree topology, roles, flow, speed, and nominal layer width are preserved. " +
                "Effective source layer planes are transported and retained. Layer height is recalculated " +
                "from the morphed layer spacing: the first layer uses its actual lower-support clearance, " +
                "and later layers use the same per-point local interval scale whether or not " +
                "authoritative layer planes are present. Layer planes affect orientation only. " +
                "Flow-adjusted width, source curves, and deposited volume are then updated. " +
                "Geometry-dependent analysis, motion, and KPI results are cleared.\r\n\r\n" +
                "For Oxana Barsukova - ACHTech Hub U Minho.\r\n\r\n" +
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
            new Guid("773B83D7-6740-4850-87B4-5CAD9F8322EA");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon => MorphPathIcon.Bitmap;

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            WasperPathDebugOutputs.AppendOutputVisibilityMenu(
                this,
                menu,
                "Debug Outputs",
                OutputCatalog,
                () => _visibleOutputsMask,
                mask =>
                {
                    RecordUndoEvent("Toggle Pp09 debug outputs");
                    _visibleOutputsMask = mask;
                    WasperPathDebugOutputs.Rebuild(
                        this,
                        _visibleOutputsMask,
                        "Morph mode, projection coverage, displacement, layer-height reconstruction, and cleared-data report.",
                        OutputCatalog,
                        omittedCoreFields: WasperPathDebugOutputs.CoreNickNames,
                        registerExtras: RegisterMorphDebugOutputs);
                    ExpireSolution(true);
                });
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
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
            if (reader.ItemExists(VisibleOutputsMaskKey))
                _visibleOutputsMask = reader.GetInt32(VisibleOutputsMaskKey);
            else if (reader.ItemExists(ShowAllOutputsKey) && reader.GetBoolean(ShowAllOutputsKey))
                _visibleOutputsMask = AllOutputsMask;
            else
                _visibleOutputsMask = 0;
            WasperPathDebugOutputs.Rebuild(
                this,
                _visibleOutputsMask,
                "Morph mode, projection coverage, displacement, layer-height reconstruction, and cleared-data report.",
                OutputCatalog,
                omittedCoreFields: WasperPathDebugOutputs.CoreNickNames,
                registerExtras: RegisterMorphDebugOutputs);
            return base.Read(reader);
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "WASPer Print Path to morph. Canonical pt_planes are modified; tree topology " +
                "and point-matched process fields remain aligned. Please use the Pp01 WASPer Path from Curves before using this component.",
                GH_ParamAccess.item);

            int referenceIndex = p.AddGeometryParameter(
                "reference geometries",
                "ref_geo",
                "Flattened ordered list of one or more Surface, Brep, Extrusion, or Mesh references. " +
                "One item uses ref_plane as the lower support and the item as the upper target. " +
                "Two items define lower/upper boundaries. Additional items form consecutive piecewise " +
                "morph intervals. Supply the flattened list strictly from bottom to top. Pp09 audits " +
                "each adjacent pair and reports reversed or locally ambiguous order; geometry normals " +
                "do not define the list order.",
                GH_ParamAccess.list);
            p[referenceIndex].DataMapping = GH_DataMapping.Flatten;

            p.AddNumberParameter(
                "strength",
                "strength",
                "Morph blend from 0 to 1. 0 returns the original wsp_path; 1 reaches the full " +
                "bottom-to-top morph. Values outside this range are clamped with a grey notice.",
                GH_ParamAccess.item,
                1.0);

            p.AddPlaneParameter(
                "reference plane",
                "ref_plane",
                "Reference plane for the stack. Its Z axis is the projection direction when " +
                "ref_crv is empty and the fallback orientation when curve tangents are unstable. " +
                "Default: World XY.",
                GH_ParamAccess.item,
                Plane.WorldXY);

            int curveIndex = p.AddCurveParameter(
                "reference curve",
                "ref_crv",
                "Optional advanced interpolation spine. Logical layers are distributed along " +
                "its normalized arc length and use its local tangent as their projection direction, " +
                "following the reference-curve language used by the slicer.",
                GH_ParamAccess.item);
            p[curveIndex].Optional = true;

            p.AddParameter(WasperTargetRolesParam.Create(
                "Selects which semantic branches are morphed. 0 = All paths (default), " +
                "1 = Shell, 2 = Infill, 3 = Partition, 4 = Support, 5 = Transition, 6 = Undefined. " +
                "Several specific roles may be combined; All cannot be combined with another role. " +
                "Selecting only part of a connected toolpath system may create discontinuities.",
                0));
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "Morphed WASPer Print Path. Plane origins and orientations, layer_h, layer_wf, " +
                "source curves, and print_vol reflect the new geometry.",
                GH_ParamAccess.item);
            p.AddTextParameter(
                "summary",
                "summary",
                "Morph mode, projection coverage, displacement, layer-height reconstruction, and cleared-data report.",
                GH_ParamAccess.item);
        }

        private static void RegisterMorphDebugOutputs(GH_Component component, Func<string, bool> isVisible)
        {
            if (isVisible("pt_planes"))
                component.Params.RegisterOutputParam(new Param_Plane
                {
                    Name = "point_planes",
                    NickName = "pt_planes",
                    Description = "Canonical path planes after morphing.",
                    Access = GH_ParamAccess.tree
                });
            if (isVisible("la_planes"))
                component.Params.RegisterOutputParam(new Param_Plane
                {
                    Name = "layer_planes",
                    NickName = "la_planes",
                    Description = "Effective source layer planes. Supplied orientations are re-anchored to the actual source layer centres before morphing.",
                    Access = GH_ParamAccess.tree
                });
            if (isVisible("la_planes_m"))
                component.Params.RegisterOutputParam(new Param_Plane
                {
                    Name = "layer_planes_morphed",
                    NickName = "la_planes_m",
                    Description = "Morphed layer planes stored in the outgoing wsp_path.",
                    Access = GH_ParamAccess.tree
                });
            if (isVisible("plane_dev"))
                component.Params.RegisterOutputParam(new Param_Number
                {
                    Name = "plane_deviation",
                    NickName = "plane_dev",
                    Description = "Signed per-point morph displacement along the effective source layer-plane positive normal. Positive follows +Z; negative moves against it.",
                    Access = GH_ParamAccess.tree
                });
            if (isVisible("flows"))
                component.Params.RegisterOutputParam(new Param_Number
                {
                    Name = "flows",
                    NickName = "flows",
                    Description = "Per-location flow multipliers preserved by the morph.",
                    Access = GH_ParamAccess.tree
                });
            if (isVisible("layer_h"))
                component.Params.RegisterOutputParam(new Param_Number
                {
                    Name = "layer_height",
                    NickName = "layer_h",
                    Description = "Per-location layer height recalculated from morphed support/layer spacing.",
                    Access = GH_ParamAccess.tree
                });
            if (isVisible("layer_w"))
                component.Params.RegisterOutputParam(new Param_Number
                {
                    Name = "layer_w",
                    NickName = "layer_w",
                    Description = "Nominal per-location layer width.",
                    Access = GH_ParamAccess.tree
                });
            if (isVisible("layer_wf"))
                component.Params.RegisterOutputParam(new Param_Number
                {
                    Name = "layer_wf",
                    NickName = "layer_wf",
                    Description = "Flow-adjusted width rebuilt from the recalculated layer_h.",
                    Access = GH_ParamAccess.tree
                });
            if (isVisible("print_speed"))
                component.Params.RegisterOutputParam(new Param_Number
                {
                    Name = "print_speed",
                    NickName = "print_speed",
                    Description = "Optional per-location print speed carried by wsp_path.",
                    Access = GH_ParamAccess.tree
                });
            if (isVisible("print_vol"))
                component.Params.RegisterOutputParam(new Param_Number
                {
                    Name = "print_volume",
                    NickName = "print_vol",
                    Description = "Per-segment deposited volume rebuilt after morphing.",
                    Access = GH_ParamAccess.tree
                });
            if (isVisible("path_role"))
                component.Params.RegisterOutputParam(new Param_Integer
                {
                    Name = "path_role",
                    NickName = "path_role",
                    Description = "Stored semantic role per path branch.",
                    Access = GH_ParamAccess.tree
                });
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            WasperPrintPath source = ReadPath(da);
            if (source == null)
                return;
            if (!source.HasPlanes || source.PtPlanes.DataCount == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "wsp_path contains no canonical pt_planes.");
                return;
            }

            double strength = 1.0;
            Plane referencePlane = Plane.WorldXY;
            Curve referenceCurve = null;
            da.GetData(2, ref strength);
            da.GetData(3, ref referencePlane);
            da.GetData(4, ref referenceCurve);

            if (!double.IsFinite(strength))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "strength must be finite.");
                return;
            }
            double requestedStrength = strength;
            strength = Math.Max(0.0, Math.Min(1.0, strength));
            if (Math.Abs(strength - requestedStrength) > 1e-12)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"strength {requestedStrength:0.###} was clamped to {strength:0.###}.");
            }
            if (!referencePlane.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "ref_plane must be valid.");
                return;
            }
            if (referenceCurve != null &&
                (!referenceCurve.IsValid || referenceCurve.GetLength() <= 1e-9))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "ref_crv must be valid and have positive length when supplied.");
                return;
            }

            var referenceGoos = new List<IGH_GeometricGoo>();
            if (!da.GetDataList(1, referenceGoos) || referenceGoos.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "ref_geo requires at least one Surface, Brep, Extrusion, or Mesh.");
                return;
            }
            var suppliedReferences = new List<ReferenceGeometry>();
            for (int i = 0; i < referenceGoos.Count; i++)
            {
                ReferenceGeometry reference = ReferenceGeometry.Create(
                    referenceGoos[i]?.ScriptVariable());
                if (reference == null)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        $"ref_geo item {i} is not a valid Surface, Brep, Extrusion, or Mesh.");
                    return;
                }
                suppliedReferences.Add(reference);
            }
            var boundaries = new List<ReferenceGeometry>();
            if (suppliedReferences.Count == 1)
                boundaries.Add(ReferenceGeometry.Create(referencePlane));
            boundaries.AddRange(suppliedReferences);
            if (boundaries.Count < 2)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "At least two effective reference boundaries are required.");
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

            if (strength <= 1e-12)
            {
                const string unchanged =
                    "Morph path | strength=0 | geometry and all wsp_path data preserved unchanged.";
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, unchanged);
                SetMorphOutputs(
                    da,
                    source,
                    unchanged,
                    source.LayerPlanes,
                    source.LayerPlanes,
                    BuildZeroDeviation(source.PtPlanes));
                return;
            }

            double tolerance = Math.Max(
                RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001,
                1e-9);

            List<GH_Path> paths = source.PtPlanes.Paths.ToList();
            int commonPrefix = WasperGcodeTreeUtil.CommonPathPrefixLength(paths);
            List<int> orderedLayers = paths
                .Select(path => WasperGcodeTreeUtil.LayerFromPath(path, commonPrefix))
                .Distinct()
                .OrderBy(layer => layer)
                .ToList();
            if (orderedLayers.Count < 2)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "At least two logical layers are required for reference-geometry morphing.");
                return;
            }

            Dictionary<int, Point3d> layerCentres = BuildLayerCentres(
                source.PtPlanes,
                commonPrefix,
                orderedLayers);
            Vector3d sourceStackDirection =
                layerCentres[orderedLayers[orderedLayers.Count - 1]] -
                layerCentres[orderedLayers[0]];
            if (!sourceStackDirection.Unitize())
                sourceStackDirection = referencePlane.ZAxis;
            if (!sourceStackDirection.Unitize())
                sourceStackDirection = Vector3d.ZAxis;
            DataTree<Plane> effectiveSourceLayerPlaneTree = ReanchorLayerPlanes(
                source.LayerPlanes,
                layerCentres,
                commonPrefix);
            Dictionary<int, Plane> sourceLayerPlanes = BuildLayerPlaneMap(
                effectiveSourceLayerPlaneTree,
                commonPrefix);
            Dictionary<int, double> layerCoordinates = BuildHeightLayerCoordinates(
                source.LayerH,
                source.PtPlanes,
                layerCentres,
                orderedLayers,
                commonPrefix,
                tolerance,
                out double sourceSpan,
                out Dictionary<int, double> representativeHeights,
                out int inferredLayerHeights);
            if (layerCoordinates == null || sourceSpan <= tolerance)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "The logical layer stack has no usable cumulative layer_h or inferable layer spacing.");
                return;
            }

            BoundingBox bounds = BoundingBox.Empty;
            for (int branchIndex = 0; branchIndex < source.PtPlanes.BranchCount; branchIndex++)
            {
                foreach (Plane plane in source.PtPlanes.Branches[branchIndex])
                {
                    if (!bounds.IsValid)
                        bounds = new BoundingBox(plane.Origin, plane.Origin);
                    else
                        bounds.Union(plane.Origin);
                }
            }
            foreach (ReferenceGeometry boundary in boundaries)
                bounds.Union(boundary.Bounds);
            double rayHalfLength = Math.Max(
                bounds.IsValid ? bounds.Diagonal.Length * 3.0 : sourceSpan * 4.0,
                sourceSpan * 4.0);
            rayHalfLength = Math.Max(rayHalfLength, tolerance * 1000.0);

            if (!ValidateBoundaryOrder(
                    boundaries,
                    layerCentres,
                    layerCoordinates,
                    sourceLayerPlanes,
                    referenceCurve,
                    referencePlane,
                    sourceStackDirection,
                    sourceSpan,
                    rayHalfLength,
                    tolerance,
                    out string boundaryOrderError,
                    out string boundaryOrderNotice))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    boundaryOrderError);
                return;
            }
            if (!string.IsNullOrWhiteSpace(boundaryOrderNotice))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    boundaryOrderNotice);
            }

            DataTree<Plane> outputLayerPlanes = MorphLayerPlanes(
                effectiveSourceLayerPlaneTree,
                layerCoordinates,
                representativeHeights,
                referenceCurve,
                referencePlane,
                sourceStackDirection,
                boundaries,
                sourceSpan,
                rayHalfLength,
                strength,
                tolerance,
                out int layerPlaneMisses,
                out int layerPlaneFallbacks,
                out int layerPlaneDownward,
                out int layerPlaneHorizontal,
                out int layerPlaneInvertedIntervals);
            Dictionary<int, Plane> targetLayerPlanes = BuildLayerPlaneMap(
                outputLayerPlanes,
                commonPrefix);

            var mappedOrigins = new DataTree<Point3d>();
            var projectionDirections = new DataTree<Vector3d>();
            var localScales = new DataTree<double>();
            var movedFlags = new DataTree<bool>();
            int targetedBranches = 0;
            int targetedLocations = 0;
            int movedLocations = 0;
            int missedBottom = 0;
            int missedTop = 0;
            int invalidGaps = 0;
            int invertedIntervals = layerPlaneInvertedIntervals;
            int clampedRelief = 0;
            int clearanceConstrained = 0;
            int insufficientClearance = 0;
            int extremeScales = 0;
            int directionFallbacks = 0;
            int downwardDirections = layerPlaneDownward;
            int horizontalDirections = layerPlaneHorizontal;
            double maxDisplacement = 0.0;
            double minScale = double.PositiveInfinity;
            double maxScale = 0.0;

            for (int branchIndex = 0; branchIndex < source.PtPlanes.BranchCount; branchIndex++)
            {
                GH_Path path = source.PtPlanes.Paths[branchIndex];
                IList<Plane> branch = source.PtPlanes.Branches[branchIndex];
                bool targeted = WasperGcodeTreeUtil.MatchesTargetRoles(
                    source.PathRoles,
                    path,
                    targetRoles);
                if (targeted)
                    targetedBranches++;

                int layer = WasperGcodeTreeUtil.LayerFromPath(path, commonPrefix);
                double layerU = layerCoordinates[layer];
                Point3d layerCentre = layerCentres[layer];
                bool hasLayerPlane = sourceLayerPlanes.TryGetValue(
                    layer,
                    out Plane sourceLayerPlane);

                for (int i = 0; i < branch.Count; i++)
                {
                    Plane originalPlane = branch[i];
                    Point3d original = originalPlane.Origin;
                    Vector3d direction = ProjectionDirection(
                        referenceCurve,
                        layerU,
                        hasLayerPlane ? sourceLayerPlane.ZAxis : referencePlane.ZAxis,
                        originalPlane.ZAxis,
                        sourceStackDirection,
                        tolerance,
                        out bool usedFallback,
                        out bool flippedDownward,
                        out bool nearlyHorizontal);
                    if (usedFallback)
                        directionFallbacks++;
                    if (flippedDownward)
                        downwardDirections++;
                    if (nearlyHorizontal)
                        horizontalDirections++;

                    if (!targeted)
                    {
                        mappedOrigins.Add(original, path);
                        projectionDirections.Add(originalPlane.ZAxis, path);
                        localScales.Add(1.0, path);
                        movedFlags.Add(false, path);
                        continue;
                    }

                    targetedLocations++;
                    double residual = hasLayerPlane
                        ? (original - sourceLayerPlane.Origin) * sourceLayerPlane.ZAxis
                        : (original - layerCentre) * direction;
                    double pointU = layerU + residual / sourceSpan;
                    double boundedU = Math.Max(0.0, Math.Min(1.0, pointU));
                    if (Math.Abs(pointU - boundedU) > 1e-10)
                        clampedRelief++;
                    pointU = boundedU;

                    ResolveInterval(
                        pointU,
                        boundaries.Count,
                        out int interval,
                        out double intervalU);
                    ReferenceGeometry lower = boundaries[interval];
                    ReferenceGeometry upper = boundaries[interval + 1];
                    double sourceIntervalSpan =
                        sourceSpan / Math.Max(1, boundaries.Count - 1);
                    Point3d expectedBottom =
                        original - direction * (intervalU * sourceIntervalSpan);
                    Point3d expectedTop =
                        original + direction * ((1.0 - intervalU) * sourceIntervalSpan);
                    var line = new Line(
                        original - direction * rayHalfLength,
                        original + direction * rayHalfLength);

                    if (!TryResolveBoundaryPair(
                            lower,
                            upper,
                            line,
                            expectedBottom,
                            expectedTop,
                            direction,
                            tolerance,
                            true,
                            out Point3d bottomHit,
                            out Point3d topHit,
                            out bool hasBottomHit,
                            out bool hasTopHit,
                            out double _))
                    {
                        if (!hasBottomHit)
                            missedBottom++;
                        if (!hasTopHit)
                            missedTop++;
                        if (hasBottomHit && hasTopHit)
                            invalidGaps++;
                        AppendUnchanged(
                            mappedOrigins,
                            projectionDirections,
                            localScales,
                            movedFlags,
                            path,
                            originalPlane);
                        continue;
                    }

                    double targetGap = (topHit - bottomHit) * direction;
                    if (!double.IsFinite(targetGap) || targetGap <= tolerance)
                    {
                        if (double.IsFinite(targetGap) && targetGap < -tolerance)
                            invertedIntervals++;
                        invalidGaps++;
                        AppendUnchanged(
                            mappedOrigins,
                            projectionDirections,
                            localScales,
                            movedFlags,
                            path,
                            originalPlane);
                        continue;
                    }

                    double localHeight = ValueAt(
                        Branch(source.LayerH, path),
                        i,
                        representativeHeights[layer]);
                    if (!double.IsFinite(localHeight) || localHeight <= tolerance)
                        localHeight = representativeHeights[layer];
                    bool touchesUpper = intervalU >= 1.0 - 1e-10;
                    if (!touchesUpper && targetGap + tolerance < localHeight)
                    {
                        insufficientClearance++;
                        AppendUnchanged(
                            mappedOrigins,
                            projectionDirections,
                            localScales,
                            movedFlags,
                            path,
                            originalPlane);
                        continue;
                    }

                    double effectiveIntervalU = intervalU;
                    if (!touchesUpper)
                    {
                        double clearanceU = Math.Min(1.0, localHeight / targetGap);
                        if (clearanceU > effectiveIntervalU + 1e-10)
                        {
                            effectiveIntervalU = clearanceU;
                            clearanceConstrained++;
                        }
                    }
                    Point3d target =
                        bottomHit + effectiveIntervalU * (topHit - bottomHit);
                    Point3d mapped = original + strength * (target - original);
                    double mappedDistance = (mapped - bottomHit) * direction;
                    double minimumDistance = touchesUpper ? 0.0 : localHeight;
                    double boundedDistance = Math.Max(
                        minimumDistance,
                        Math.Min(targetGap, mappedDistance));
                    if (Math.Abs(boundedDistance - mappedDistance) > tolerance)
                    {
                        mapped = bottomHit + boundedDistance * direction;
                        clearanceConstrained++;
                    }
                    // The first deposited path is constrained by its actual
                    // clearance from the lower support, not by the spacing of
                    // the next pair of layer planes. Higher layers use the
                    // local interval stretch. Authoritative layer planes guide
                    // orientation only and never override filament height.
                    bool isFirstLayer = layer == orderedLayers[0];
                    double scale = isFirstLayer
                        ? boundedDistance / localHeight
                        : 1.0 + strength *
                            (targetGap / sourceIntervalSpan - 1.0);
                    if (!double.IsFinite(scale) || scale <= tolerance)
                    {
                        invalidGaps++;
                        AppendUnchanged(
                            mappedOrigins,
                            projectionDirections,
                            localScales,
                            movedFlags,
                            path,
                            originalPlane);
                        continue;
                    }

                    double displacement = original.DistanceTo(mapped);
                    bool moved = displacement > tolerance;
                    mappedOrigins.Add(mapped, path);
                    projectionDirections.Add(direction, path);
                    localScales.Add(scale, path);
                    movedFlags.Add(moved, path);
                    if (moved)
                    {
                        movedLocations++;
                        maxDisplacement = Math.Max(maxDisplacement, displacement);
                    }
                    minScale = Math.Min(minScale, scale);
                    maxScale = Math.Max(maxScale, scale);
                    if (scale < 0.2 || scale > 5.0)
                        extremeScales++;
                }
            }

            if (targetedLocations == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "No wsp_path branch matched the selected roles.");
                string noMatch =
                    $"Morph path | target_roles={WasperGcodeTreeUtil.TargetRoleNames(targetRoles)} | " +
                    "targeted branches=0 | geometry unchanged.";
                SetMorphOutputs(
                    da,
                    source,
                    noMatch,
                    effectiveSourceLayerPlaneTree,
                    effectiveSourceLayerPlaneTree,
                    BuildZeroDeviation(source.PtPlanes));
                return;
            }

            DataTree<Plane> outputPlanes = RebuildPlanes(
                source.PtPlanes,
                source.SourceCurves,
                mappedOrigins,
                projectionDirections,
                movedFlags,
                sourceLayerPlanes,
                targetLayerPlanes,
                commonPrefix,
                tolerance,
                out int planeFallbacks);
            DataTree<double> outputLayerH = ScaleTree(
                source.LayerH,
                source.PtPlanes,
                localScales,
                movedFlags);
            DataTree<double> outputLayerWf = RebuildLayerWf(
                source.LayerW,
                source.LayerWf,
                source.Flows,
                outputLayerH,
                source.PtPlanes,
                movedFlags,
                tolerance);
            DataTree<double> outputPrintVol = RebuildPrintVolume(
                outputPlanes,
                outputLayerH,
                outputLayerWf,
                tolerance);
            DataTree<double> planeDeviation = BuildPlaneDeviation(
                source.PtPlanes,
                mappedOrigins,
                sourceLayerPlanes,
                commonPrefix);

            bool changed = movedLocations > 0;
            WasperPrintPath output = !changed
                ? source
                : new WasperPrintPath(
                    points: null,
                    ptPlanes: outputPlanes,
                    flows: source.Flows,
                    layerH: outputLayerH,
                    printSpeed: source.PrintSpeed,
                    nozzleDiam: source.NozzleDiam,
                    layerW: source.LayerW,
                    layerWf: outputLayerWf,
                    printVol: outputPrintVol,
                    travelSpeed: source.TravelSpeed,
                    zHop: source.ZHop,
                    zHopSpeed: source.ZHopSpeed,
                    isPartial: source.IsPartial,
                    sourceCurves: BuildSourceCurves(
                        outputPlanes,
                        source.SourceCurves,
                        tolerance),
                    pathRoles: source.PathRoles,
                    layerPlanes: outputLayerPlanes);

            if (missedBottom + missedTop > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{missedBottom + missedTop} targeted location(s) could not intersect both reference " +
                    "geometries and were preserved unchanged.");
            }
            if (invalidGaps > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{invalidGaps} targeted location(s) produced a zero, invalid, or inverted morph gap " +
                    "and were preserved unchanged.");
            }
            if (invertedIntervals > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{invertedIntervals} evaluated reference interval(s) failed the verified " +
                    "bottom-to-top direction locally. Check for crossing or overlapping ref_geo.");
            }
            if (layerPlaneMisses > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{layerPlaneMisses} effective layer reference plane(s) could not intersect both " +
                    "morph boundaries and were preserved unchanged.");
            }
            if (insufficientClearance > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{insufficientClearance} targeted location(s) had less reference-boundary " +
                    "clearance than their local source layer_h and were preserved unchanged.");
            }
            if (downwardDirections > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{downwardDirections} evaluated projection direction(s) initially pointed " +
                    "against the actual first-to-last source stack and were reversed.");
            }
            if (horizontalDirections > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{horizontalDirections} evaluated projection direction(s) are nearly transverse " +
                    "to the actual source stack. Review the stored layer planes or ref_crv.");
            }
            if (!WasperGcodeTreeUtil.TargetsAllRoles(targetRoles))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Only selected roles were morphed. Connections between moved and excluded branches " +
                    "may become discontinuous.");
            }
            if (extremeScales > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{extremeScales} location(s) use a local stack scale below 0.2 or above 5. " +
                    "Review layer spacing and printability.");
            }
            if (clampedRelief > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{clampedRelief} non-planar location(s) extended beyond the source stack ends; " +
                    "their interpolation coordinate was clamped to the closest boundary.");
            }
            if (inferredLayerHeights > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{inferredLayerHeights} logical layer(s) had no positive finite layer_h values; " +
                    "their representative height was inferred from adjacent layer positions.");
            }
            if (source.IsPartial)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "The input wsp_path is partial. Morph interpolation uses only its retained logical-layer extent.");
            }
            if (changed && HasDerivedData(source))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "Morphing changed path geometry. Spatial printability, structural risk, motion-plan, " +
                    "and KPI fields were cleared; layer_h, layer_wf, source curves, and print_vol were rebuilt.");
            }

            var summary = new StringBuilder();
            summary.AppendLine("Morph Path Between Surfaces");
            summary.AppendLine(referenceCurve == null
                ? "mode: authoritative layer normal, ref_plane.ZAxis fallback"
                : "mode: ref_crv tangent by normalized arc length");
            summary.AppendLine(
                $"ordered boundaries ({boundaries.Count}): " +
                string.Join(" -> ", boundaries.Select(boundary => boundary.Kind)));
            summary.AppendLine("ref_geo order: validated bottom-to-top");
            summary.AppendLine($"logical layers: {orderedLayers.Count}");
            summary.AppendLine(
                $"source cumulative layer_h: {sourceSpan:0.###} model units; inferred layers={inferredLayerHeights}");
            summary.AppendLine(
                $"layer references: supplied orientations re-anchored={sourceLayerPlanes.Count}, " +
                $"centroid fallback={Math.Max(0, orderedLayers.Count - sourceLayerPlanes.Count)}");
            summary.AppendLine(
                $"layer reference transport: misses={layerPlaneMisses}, frame fallbacks={layerPlaneFallbacks}");
            summary.AppendLine($"strength: {strength:0.###}");
            summary.AppendLine($"target roles: {WasperGcodeTreeUtil.TargetRoleNames(targetRoles)}");
            summary.AppendLine($"targeted branches/locations: {targetedBranches}/{targetedLocations}");
            summary.AppendLine($"moved locations: {movedLocations}");
            summary.AppendLine($"maximum displacement: {maxDisplacement:0.###} model units");
            summary.AppendLine($"projection misses: bottom={missedBottom}, top={missedTop}");
            summary.AppendLine($"invalid gaps: {invalidGaps}");
            summary.AppendLine($"downward/inverted reference intervals: {invertedIntervals}");
            summary.AppendLine(
                $"lower layer_h clearance: constrained={clearanceConstrained}, insufficient={insufficientClearance}");
            summary.AppendLine(
                "layer_h reconstruction: first-layer support clearance + per-point local interval scale");
            summary.AppendLine($"clamped non-planar coordinates: {clampedRelief}");
            summary.AppendLine(
                $"directions: reversed to source stack={downwardDirections}, transverse to stack={horizontalDirections}");
            summary.AppendLine($"direction/plane fallbacks: {directionFallbacks}/{planeFallbacks}");
            summary.AppendLine(
                movedLocations > 0
                    ? $"local stack scale: {minScale:0.###} to {maxScale:0.###}"
                    : "local stack scale: unchanged");
            summary.Append($"derived spatial data cleared: {(changed ? "yes" : "no")}");

            SetMorphOutputs(
                da,
                output,
                summary.ToString(),
                effectiveSourceLayerPlaneTree,
                outputLayerPlanes,
                planeDeviation);
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
            if (raw is GH_ObjectWrapper wrapper)
            {
                if (wrapper.Value is WasperPrintPath wrappedPath)
                    return wrappedPath;
                if (wrapper.Value is WasperPrintPathGoo wrappedGoo && wrappedGoo.Value != null)
                    return wrappedGoo.Value;
            }
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Error,
                "wsp_path must be a WASPer Print Path. Please use the Pp01 WASPer Path from Curves before using this component.");
            return null;
        }

        private static Dictionary<int, Point3d> BuildLayerCentres(
            DataTree<Plane> planes,
            int commonPrefix,
            IEnumerable<int> layers)
        {
            var sums = layers.ToDictionary(layer => layer, layer => Vector3d.Zero);
            var counts = layers.ToDictionary(layer => layer, layer => 0);
            for (int b = 0; b < planes.BranchCount; b++)
            {
                GH_Path path = planes.Paths[b];
                int layer = WasperGcodeTreeUtil.LayerFromPath(path, commonPrefix);
                foreach (Plane plane in planes.Branches[b])
                {
                    sums[layer] += (Vector3d)plane.Origin;
                    counts[layer]++;
                }
            }
            return layers.ToDictionary(
                layer => layer,
                layer => counts[layer] > 0
                    ? (Point3d)(sums[layer] / counts[layer])
                    : Point3d.Origin);
        }

        private static Dictionary<int, Plane> BuildLayerPlaneMap(
            DataTree<Plane> layerPlanes,
            int commonPrefix)
        {
            var result = new Dictionary<int, Plane>();
            if (layerPlanes == null)
                return result;
            for (int b = 0; b < layerPlanes.BranchCount; b++)
            {
                IList<Plane> branch = layerPlanes.Branches[b];
                if (branch == null || branch.Count == 0 || !branch[0].IsValid)
                    continue;
                int layer = WasperGcodeTreeUtil.LayerFromPath(
                    layerPlanes.Paths[b],
                    commonPrefix);
                if (!result.ContainsKey(layer))
                    result[layer] = branch[0];
            }
            return result;
        }

        private static DataTree<Plane> ReanchorLayerPlanes(
            DataTree<Plane> layerPlanes,
            IDictionary<int, Point3d> layerCentres,
            int commonPrefix)
        {
            if (layerPlanes == null || layerCentres == null)
                return null;

            var result = new DataTree<Plane>();
            for (int b = 0; b < layerPlanes.BranchCount; b++)
            {
                GH_Path path = layerPlanes.Paths[b];
                IList<Plane> branch = layerPlanes.Branches[b];
                if (branch == null || branch.Count == 0 || !branch[0].IsValid)
                    continue;
                int layer = WasperGcodeTreeUtil.LayerFromPath(path, commonPrefix);
                if (!layerCentres.TryGetValue(layer, out Point3d centre))
                    continue;

                Plane source = branch[0];
                Plane effective = new Plane(centre, source.XAxis, source.YAxis);
                if (effective.IsValid)
                    result.Add(effective, path);
            }
            return result.BranchCount > 0 ? result : null;
        }

        private static Dictionary<int, double> BuildHeightLayerCoordinates(
            DataTree<double> layerHeights,
            DataTree<Plane> pointPlanes,
            IDictionary<int, Point3d> centres,
            IList<int> layers,
            int commonPrefix,
            double tolerance,
            out double totalHeight,
            out Dictionary<int, double> representativeHeights,
            out int inferredCount)
        {
            totalHeight = 0.0;
            inferredCount = 0;
            representativeHeights = new Dictionary<int, double>();
            var samples = layers.ToDictionary(
                layer => layer,
                layer => new List<double>());

            for (int b = 0; b < pointPlanes.BranchCount; b++)
            {
                GH_Path path = pointPlanes.Paths[b];
                int layer = WasperGcodeTreeUtil.LayerFromPath(path, commonPrefix);
                if (!samples.ContainsKey(layer))
                    continue;
                IList<double> values = Branch(layerHeights, path);
                for (int i = 0; i < pointPlanes.Branches[b].Count; i++)
                {
                    double value = ValueAt(values, i, double.NaN);
                    if (double.IsFinite(value) && value > tolerance)
                        samples[layer].Add(value);
                }
            }

            var known = new List<double>();
            foreach (int layer in layers)
            {
                if (samples[layer].Count == 0)
                    continue;
                double median = Median(samples[layer]);
                representativeHeights[layer] = median;
                known.Add(median);
            }
            double globalFallback = known.Count > 0
                ? Median(known)
                : double.NaN;

            for (int i = 0; i < layers.Count; i++)
            {
                int layer = layers[i];
                if (representativeHeights.ContainsKey(layer))
                    continue;
                double inferred = double.NaN;
                if (i > 0)
                    inferred = centres[layer].DistanceTo(centres[layers[i - 1]]);
                if ((!double.IsFinite(inferred) || inferred <= tolerance) &&
                    i + 1 < layers.Count)
                {
                    inferred = centres[layers[i + 1]].DistanceTo(centres[layer]);
                }
                if (!double.IsFinite(inferred) || inferred <= tolerance)
                    inferred = globalFallback;
                if (!double.IsFinite(inferred) || inferred <= tolerance)
                    return null;
                representativeHeights[layer] = inferred;
                inferredCount++;
            }

            foreach (int layer in layers)
                totalHeight += representativeHeights[layer];
            if (!double.IsFinite(totalHeight) || totalHeight <= tolerance)
                return null;

            double cumulative = 0.0;
            var result = new Dictionary<int, double>();
            foreach (int layer in layers)
            {
                cumulative += representativeHeights[layer];
                result[layer] = Math.Max(0.0, Math.Min(1.0, cumulative / totalHeight));
            }
            result[layers[layers.Count - 1]] = 1.0;
            return result;
        }

        private static double Median(IEnumerable<double> values)
        {
            List<double> ordered = values
                .Where(double.IsFinite)
                .OrderBy(value => value)
                .ToList();
            if (ordered.Count == 0)
                return double.NaN;
            int middle = ordered.Count / 2;
            return ordered.Count % 2 == 0
                ? 0.5 * (ordered[middle - 1] + ordered[middle])
                : ordered[middle];
        }

        private static void ResolveInterval(
            double u,
            int boundaryCount,
            out int interval,
            out double localU)
        {
            int intervalCount = Math.Max(1, boundaryCount - 1);
            double bounded = Math.Max(0.0, Math.Min(1.0, u));
            if (bounded >= 1.0 - 1e-12)
            {
                interval = intervalCount - 1;
                localU = 1.0;
                return;
            }
            double scaled = bounded * intervalCount;
            interval = Math.Max(
                0,
                Math.Min(intervalCount - 1, (int)Math.Ceiling(scaled) - 1));
            localU = scaled - interval;
            localU = Math.Max(0.0, Math.Min(1.0, localU));
        }

        private static bool ValidateBoundaryOrder(
            IList<ReferenceGeometry> boundaries,
            IDictionary<int, Point3d> layerCentres,
            IDictionary<int, double> layerCoordinates,
            IDictionary<int, Plane> sourceLayerPlanes,
            Curve referenceCurve,
            Plane referencePlane,
            Vector3d sourceStackDirection,
            double sourceSpan,
            double rayHalfLength,
            double tolerance,
            out string error,
            out string notice)
        {
            error = null;
            notice = null;
            if (boundaries == null || boundaries.Count < 2)
                return false;

            var reversedPairs = new List<string>();
            var ambiguousPairs = new List<string>();
            for (int pair = 0; pair < boundaries.Count - 1; pair++)
            {
                int positive = 0;
                int negative = 0;
                int nearZero = 0;
                int misses = 0;
                foreach (KeyValuePair<int, double> entry in layerCoordinates)
                {
                    ResolveInterval(
                        entry.Value,
                        boundaries.Count,
                        out int interval,
                        out double intervalU);
                    if (interval != pair ||
                        !layerCentres.TryGetValue(entry.Key, out Point3d origin))
                        continue;

                    Vector3d fallback = sourceLayerPlanes != null &&
                        sourceLayerPlanes.TryGetValue(entry.Key, out Plane layerPlane)
                            ? layerPlane.ZAxis
                            : referencePlane.ZAxis;
                    Vector3d direction = ProjectionDirection(
                        referenceCurve,
                        entry.Value,
                        fallback,
                        fallback,
                        sourceStackDirection,
                        tolerance,
                        out bool _,
                        out bool _,
                        out bool _);
                    double sourceIntervalSpan =
                        sourceSpan / Math.Max(1, boundaries.Count - 1);
                    Point3d expectedBottom =
                        origin - direction * (intervalU * sourceIntervalSpan);
                    Point3d expectedTop =
                        origin + direction *
                        ((1.0 - intervalU) * sourceIntervalSpan);
                    var line = new Line(
                        origin - direction * rayHalfLength,
                        origin + direction * rayHalfLength);

                    if (!TryResolveBoundaryPair(
                            boundaries[pair],
                            boundaries[pair + 1],
                            line,
                            expectedBottom,
                            expectedTop,
                            direction,
                            tolerance,
                            false,
                            out Point3d _,
                            out Point3d _,
                            out bool _,
                            out bool _,
                            out double signedGap))
                    {
                        misses++;
                    }
                    else if (signedGap > tolerance)
                    {
                        positive++;
                    }
                    else if (signedGap < -tolerance)
                    {
                        negative++;
                    }
                    else
                    {
                        nearZero++;
                    }
                }

                int orderedSamples = positive + negative;
                if (negative > positive &&
                    negative >= Math.Max(1, (int)Math.Ceiling(orderedSamples * 0.70)))
                {
                    reversedPairs.Add(
                        $"{pair}->{pair + 1} (reversed={negative}, ordered={positive}, " +
                        $"zero={nearZero}, misses={misses})");
                }
                else if (negative > 0 || nearZero > 0)
                {
                    ambiguousPairs.Add(
                        $"{pair}->{pair + 1} (ordered={positive}, reversed={negative}, " +
                        $"zero={nearZero}, misses={misses})");
                }
            }

            if (reversedPairs.Count > 0)
            {
                error =
                    "ref_geo order appears reversed for adjacent flattened item pair(s): " +
                    string.Join("; ", reversedPairs) + ". Supply ref_geo from bottom to top. " +
                    "Surface or mesh normals do not define this order.";
                return false;
            }
            if (ambiguousPairs.Count > 0)
            {
                notice =
                    "Some ref_geo pairs cross, overlap, or have locally inconsistent bottom-to-top " +
                    "ordering: " + string.Join("; ", ambiguousPairs) +
                    ". Affected locations without a positive ordered hit pair will remain unchanged.";
            }
            return true;
        }

        private static bool TryResolveBoundaryPair(
            ReferenceGeometry lower,
            ReferenceGeometry upper,
            Line line,
            Point3d expectedBottom,
            Point3d expectedTop,
            Vector3d direction,
            double tolerance,
            bool requirePositiveGap,
            out Point3d bottomHit,
            out Point3d topHit,
            out bool hasBottomHit,
            out bool hasTopHit,
            out double signedGap)
        {
            bottomHit = Point3d.Unset;
            topHit = Point3d.Unset;
            signedGap = double.NaN;
            Point3d[] bottomCandidates =
                lower?.Intersections(line, tolerance) ?? Array.Empty<Point3d>();
            Point3d[] topCandidates =
                upper?.Intersections(line, tolerance) ?? Array.Empty<Point3d>();
            hasBottomHit = bottomCandidates.Length > 0;
            hasTopHit = topCandidates.Length > 0;
            if (!hasBottomHit || !hasTopHit)
                return false;
            if (!direction.Unitize())
                return false;

            double bestScore = double.PositiveInfinity;
            foreach (Point3d bottom in bottomCandidates)
            {
                foreach (Point3d top in topCandidates)
                {
                    double gap = (top - bottom) * direction;
                    if (!double.IsFinite(gap) ||
                        (requirePositiveGap && gap <= tolerance))
                        continue;
                    double score =
                        bottom.DistanceToSquared(expectedBottom) +
                        top.DistanceToSquared(expectedTop);
                    if (score >= bestScore)
                        continue;
                    bestScore = score;
                    bottomHit = bottom;
                    topHit = top;
                    signedGap = gap;
                }
            }
            return bottomHit.IsValid && topHit.IsValid;
        }

        private static Vector3d ProjectionDirection(
            Curve referenceCurve,
            double layerU,
            Vector3d fallback,
            Vector3d originalZ,
            Vector3d sourceStackDirection,
            double tolerance,
            out bool usedFallback,
            out bool flippedDownward,
            out bool nearlyHorizontal)
        {
            usedFallback = false;
            flippedDownward = false;
            nearlyHorizontal = false;
            Vector3d direction = fallback;
            if (referenceCurve != null)
            {
                double length = referenceCurve.GetLength();
                double parameter;
                if (referenceCurve.LengthParameter(
                        Math.Max(0.0, Math.Min(1.0, layerU)) * length,
                        out parameter))
                {
                    direction = referenceCurve.TangentAt(parameter);
                }
            }
            if (!direction.IsValid || !direction.Unitize())
            {
                usedFallback = true;
                direction = fallback;
            }
            if (!direction.IsValid || direction.Length <= tolerance || !direction.Unitize())
            {
                usedFallback = true;
                direction = originalZ.IsValid ? originalZ : Vector3d.ZAxis;
                direction.Unitize();
            }
            Vector3d orientationReference = sourceStackDirection;
            if (!orientationReference.Unitize())
                orientationReference = Vector3d.ZAxis;
            double stackDot = direction * orientationReference;
            if (stackDot < 0.0)
            {
                direction = -direction;
                flippedDownward = stackDot < -tolerance;
                stackDot = -stackDot;
            }
            nearlyHorizontal = stackDot < 0.1;
            return direction;
        }

        private static DataTree<Plane> MorphLayerPlanes(
            DataTree<Plane> sourceTree,
            IDictionary<int, double> layerCoordinates,
            IDictionary<int, double> representativeHeights,
            Curve referenceCurve,
            Plane referencePlane,
            Vector3d sourceStackDirection,
            IList<ReferenceGeometry> boundaries,
            double sourceSpan,
            double rayHalfLength,
            double strength,
            double tolerance,
            out int misses,
            out int frameFallbacks,
            out int downwardDirections,
            out int horizontalDirections,
            out int invertedIntervals)
        {
            misses = 0;
            frameFallbacks = 0;
            downwardDirections = 0;
            horizontalDirections = 0;
            invertedIntervals = 0;
            if (sourceTree == null)
                return null;

            int prefix = WasperGcodeTreeUtil.CommonPathPrefixLength(
                sourceTree.Paths);
            var result = new DataTree<Plane>();
            for (int b = 0; b < sourceTree.BranchCount; b++)
            {
                GH_Path path = sourceTree.Paths[b];
                IList<Plane> branch = sourceTree.Branches[b];
                if (branch == null || branch.Count == 0 || !branch[0].IsValid)
                    continue;

                Plane sourcePlane = branch[0];
                int layer = WasperGcodeTreeUtil.LayerFromPath(path, prefix);
                if (!layerCoordinates.TryGetValue(layer, out double u))
                {
                    result.Add(sourcePlane, path);
                    misses++;
                    continue;
                }

                Vector3d direction = ProjectionDirection(
                    referenceCurve,
                    u,
                    sourcePlane.ZAxis,
                    referencePlane.ZAxis,
                    sourceStackDirection,
                    tolerance,
                    out bool usedFallback,
                    out bool flippedDownward,
                    out bool nearlyHorizontal);
                if (usedFallback)
                    frameFallbacks++;
                if (flippedDownward)
                    downwardDirections++;
                if (nearlyHorizontal)
                    horizontalDirections++;

                Point3d origin = sourcePlane.Origin;
                ResolveInterval(
                    u,
                    boundaries.Count,
                    out int interval,
                    out double intervalU);
                ReferenceGeometry lower = boundaries[interval];
                ReferenceGeometry upper = boundaries[interval + 1];
                double sourceIntervalSpan =
                    sourceSpan / Math.Max(1, boundaries.Count - 1);
                var line = new Line(
                    origin - direction * rayHalfLength,
                    origin + direction * rayHalfLength);
                Point3d expectedBottom =
                    origin - direction * (intervalU * sourceIntervalSpan);
                Point3d expectedTop =
                    origin + direction * ((1.0 - intervalU) * sourceIntervalSpan);
                if (!TryResolveBoundaryPair(
                        lower,
                        upper,
                        line,
                        expectedBottom,
                        expectedTop,
                        direction,
                        tolerance,
                        true,
                        out Point3d bottomHit,
                        out Point3d topHit,
                        out bool _,
                        out bool _,
                        out double _))
                {
                    result.Add(sourcePlane, path);
                    misses++;
                    continue;
                }

                double targetGap = (topHit - bottomHit) * direction;
                if (!double.IsFinite(targetGap) || targetGap <= tolerance)
                {
                    if (double.IsFinite(targetGap) && targetGap < -tolerance)
                        invertedIntervals++;
                    result.Add(sourcePlane, path);
                    misses++;
                    continue;
                }
                bool touchesUpper = intervalU >= 1.0 - 1e-10;
                double effectiveU = intervalU;
                if (!touchesUpper &&
                    representativeHeights.TryGetValue(layer, out double layerHeight))
                {
                    if (targetGap + tolerance < layerHeight)
                    {
                        result.Add(sourcePlane, path);
                        misses++;
                        continue;
                    }
                    effectiveU = Math.Max(
                        effectiveU,
                        Math.Min(1.0, layerHeight / targetGap));
                }
                Point3d targetOrigin =
                    bottomHit + effectiveU * (topHit - bottomHit);
                Point3d blendedOrigin =
                    origin + strength * (targetOrigin - origin);
                Vector3d targetZ =
                    (1.0 - strength) * sourcePlane.ZAxis + strength * direction;
                if (!targetZ.IsValid || !targetZ.Unitize())
                {
                    targetZ = sourcePlane.ZAxis;
                    frameFallbacks++;
                }
                Vector3d targetX =
                    sourcePlane.XAxis - targetZ * (sourcePlane.XAxis * targetZ);
                if (!targetX.IsValid || targetX.Length <= tolerance || !targetX.Unitize())
                {
                    targetX = StablePerpendicular(targetZ);
                    frameFallbacks++;
                }
                Vector3d targetY = Vector3d.CrossProduct(targetZ, targetX);
                if (!targetY.IsValid || !targetY.Unitize())
                {
                    Plane shifted = sourcePlane;
                    shifted.Origin = blendedOrigin;
                    result.Add(shifted, path);
                    frameFallbacks++;
                    continue;
                }
                result.Add(new Plane(blendedOrigin, targetX, targetY), path);
            }
            return result.BranchCount > 0 ? result : null;
        }

        private static void AppendUnchanged(
            DataTree<Point3d> origins,
            DataTree<Vector3d> directions,
            DataTree<double> scales,
            DataTree<bool> moved,
            GH_Path path,
            Plane plane)
        {
            origins.Add(plane.Origin, path);
            directions.Add(plane.ZAxis, path);
            scales.Add(1.0, path);
            moved.Add(false, path);
        }

        private static DataTree<Plane> RebuildPlanes(
            DataTree<Plane> source,
            DataTree<Curve> sourceCurves,
            DataTree<Point3d> origins,
            DataTree<Vector3d> directions,
            DataTree<bool> moved,
            IDictionary<int, Plane> sourceLayerPlanes,
            IDictionary<int, Plane> targetLayerPlanes,
            int commonPrefix,
            double tolerance,
            out int fallbacks)
        {
            fallbacks = 0;
            var result = new DataTree<Plane>();
            for (int b = 0; b < source.BranchCount; b++)
            {
                GH_Path path = source.Paths[b];
                IList<Plane> sourceBranch = source.Branches[b];
                IList<Point3d> points = origins.Branch(path);
                IList<Vector3d> zValues = directions.Branch(path);
                IList<bool> flags = moved.Branch(path);
                bool cyclic = SourceCurveIsClosed(sourceCurves, path);
                int layer = WasperGcodeTreeUtil.LayerFromPath(path, commonPrefix);
                Plane sourceLayerPlane = Plane.Unset;
                Plane targetLayerPlane = Plane.Unset;
                bool hasLayerTransport =
                    sourceLayerPlanes != null &&
                    targetLayerPlanes != null &&
                    sourceLayerPlanes.TryGetValue(layer, out sourceLayerPlane) &&
                    targetLayerPlanes.TryGetValue(layer, out targetLayerPlane);
                for (int i = 0; i < sourceBranch.Count; i++)
                {
                    Plane original = sourceBranch[i];
                    if (flags == null || i >= flags.Count || !flags[i])
                    {
                        result.Add(original, path);
                        continue;
                    }

                    Point3d origin = points[i];
                    Vector3d z;
                    Vector3d x;
                    if (hasLayerTransport)
                    {
                        z = TransportVector(
                            original.ZAxis,
                            sourceLayerPlane,
                            targetLayerPlane);
                        x = TransportVector(
                            original.XAxis,
                            sourceLayerPlane,
                            targetLayerPlane);
                    }
                    else
                    {
                        z = zValues[i];
                        x = LocalTangent(points, i, cyclic);
                    }
                    if (!z.IsValid || !z.Unitize())
                        z = original.ZAxis;
                    x -= z * (x * z);
                    if (!x.IsValid || x.Length <= tolerance || !x.Unitize())
                    {
                        x = original.XAxis - z * (original.XAxis * z);
                        fallbacks++;
                    }
                    if (!x.IsValid || x.Length <= tolerance || !x.Unitize())
                    {
                        x = StablePerpendicular(z);
                        fallbacks++;
                    }
                    if (original.XAxis.IsValid && x * original.XAxis < 0.0)
                        x = -x;
                    Vector3d y = Vector3d.CrossProduct(z, x);
                    if (!y.IsValid || !y.Unitize())
                    {
                        Plane shifted = original;
                        shifted.Origin = origin;
                        result.Add(shifted, path);
                        fallbacks++;
                        continue;
                    }
                    Plane rebuilt = new Plane(origin, x, y);
                    if (!rebuilt.IsValid)
                    {
                        rebuilt = original;
                        rebuilt.Origin = origin;
                        fallbacks++;
                    }
                    result.Add(rebuilt, path);
                }
            }
            return result;
        }

        private static Vector3d TransportVector(
            Vector3d vector,
            Plane source,
            Plane target)
        {
            return
                (vector * source.XAxis) * target.XAxis +
                (vector * source.YAxis) * target.YAxis +
                (vector * source.ZAxis) * target.ZAxis;
        }

        private static Vector3d LocalTangent(
            IList<Point3d> points,
            int index,
            bool cyclic)
        {
            if (points == null || points.Count < 2)
                return Vector3d.Unset;
            if (cyclic)
            {
                int previous = (index - 1 + points.Count) % points.Count;
                int next = (index + 1) % points.Count;
                return points[next] - points[previous];
            }
            if (index <= 0)
                return points[1] - points[0];
            if (index >= points.Count - 1)
                return points[points.Count - 1] - points[points.Count - 2];
            return points[index + 1] - points[index - 1];
        }

        private static Vector3d StablePerpendicular(Vector3d normal)
        {
            Vector3d z = normal;
            if (!z.Unitize())
                z = Vector3d.ZAxis;
            Vector3d seed =
                Math.Abs(z * Vector3d.ZAxis) < 0.9
                    ? Vector3d.ZAxis
                    : Vector3d.XAxis;
            Vector3d result = seed - z * (seed * z);
            if (!result.Unitize())
                result = Vector3d.XAxis;
            return result;
        }

        private static DataTree<double> ScaleTree(
            DataTree<double> values,
            DataTree<Plane> planes,
            DataTree<double> scales,
            DataTree<bool> moved)
        {
            if (values == null)
                return null;
            var result = new DataTree<double>();
            for (int b = 0; b < planes.BranchCount; b++)
            {
                GH_Path path = planes.Paths[b];
                IList<Plane> planeBranch = planes.Branches[b];
                IList<double> valueBranch = Branch(values, path);
                IList<double> scaleBranch = Branch(scales, path);
                IList<bool> movedBranch = Branch(moved, path);
                for (int i = 0; i < planeBranch.Count; i++)
                {
                    double value = ValueAt(valueBranch, i, 0.0);
                    bool wasMoved = ValueAt(movedBranch, i, false);
                    double scale = !wasMoved
                        ? 1.0
                        : ValueAt(scaleBranch, i, 1.0);
                    result.Add(value * scale, path);
                }
            }
            return result;
        }

        private static DataTree<double> RecomputeLayerHeights(
            DataTree<double> fallbackHeights,
            DataTree<Plane> sourcePlanes,
            DataTree<Curve> sourceCurves,
            DataTree<Point3d> mappedOrigins,
            DataTree<Vector3d> directions,
            DataTree<bool> moved,
            IList<int> orderedLayers,
            int commonPrefix,
            double tolerance,
            out int geometryCount,
            out int fallbackCount)
        {
            geometryCount = 0;
            fallbackCount = 0;
            if (fallbackHeights == null || sourcePlanes == null)
                return fallbackHeights;

            var previousLayer = new Dictionary<int, int>();
            for (int i = 1; i < orderedLayers.Count; i++)
                previousLayer[orderedLayers[i]] = orderedLayers[i - 1];

            var result = new DataTree<double>();
            for (int b = 0; b < sourcePlanes.BranchCount; b++)
            {
                GH_Path path = sourcePlanes.Paths[b];
                IList<Plane> sourceBranch = sourcePlanes.Branches[b];
                IList<double> fallbackBranch = Branch(fallbackHeights, path);
                IList<Point3d> currentPoints = mappedOrigins.PathExists(path)
                    ? mappedOrigins.Branch(path)
                    : null;
                IList<Vector3d> directionBranch = directions.PathExists(path)
                    ? directions.Branch(path)
                    : null;
                IList<bool> movedBranch = Branch(moved, path);
                int layer = WasperGcodeTreeUtil.LayerFromPath(path, commonPrefix);

                PolylineCurve previousPolyline = null;
                List<PolylineCurve> previousAlternatives = null;
                bool hasPreviousLayer =
                    previousLayer.TryGetValue(layer, out int previous);
                if (hasPreviousLayer &&
                    TryPreviousLayerPath(path, commonPrefix, previous, out GH_Path previousPath) &&
                    mappedOrigins.PathExists(previousPath))
                {
                    IList<Point3d> previousPoints = mappedOrigins.Branch(previousPath);
                    if (previousPoints != null && previousPoints.Count >= 2)
                    {
                        var polylinePoints = new List<Point3d>(previousPoints);
                        if (SourceCurveIsClosed(sourceCurves, previousPath) &&
                            polylinePoints[0].DistanceToSquared(
                                polylinePoints[polylinePoints.Count - 1]) >
                            tolerance * tolerance)
                        {
                            polylinePoints.Add(polylinePoints[0]);
                        }
                        previousPolyline = new PolylineCurve(polylinePoints);
                    }
                }
                if (previousPolyline == null && hasPreviousLayer)
                {
                    previousAlternatives = new List<PolylineCurve>();
                    for (int candidateIndex = 0;
                        candidateIndex < mappedOrigins.BranchCount;
                        candidateIndex++)
                    {
                        GH_Path candidatePath = mappedOrigins.Paths[candidateIndex];
                        if (WasperGcodeTreeUtil.LayerFromPath(
                                candidatePath,
                                commonPrefix) != previous)
                            continue;
                        IList<Point3d> candidatePoints =
                            mappedOrigins.Branches[candidateIndex];
                        if (candidatePoints == null || candidatePoints.Count < 2)
                            continue;
                        var polylinePoints = new List<Point3d>(candidatePoints);
                        if (SourceCurveIsClosed(sourceCurves, candidatePath) &&
                            polylinePoints[0].DistanceToSquared(
                                polylinePoints[polylinePoints.Count - 1]) >
                            tolerance * tolerance)
                        {
                            polylinePoints.Add(polylinePoints[0]);
                        }
                        previousAlternatives.Add(
                            new PolylineCurve(polylinePoints));
                    }
                }

                for (int i = 0; i < sourceBranch.Count; i++)
                {
                    double fallback = ValueAt(fallbackBranch, i, 0.0);
                    bool wasMoved = ValueAt(movedBranch, i, false);
                    if (!wasMoved || currentPoints == null ||
                        i >= currentPoints.Count)
                    {
                        result.Add(fallback, path);
                        if (wasMoved && previousLayer.ContainsKey(layer))
                            fallbackCount++;
                        continue;
                    }

                    Point3d current = currentPoints[i];
                    Point3d support = Point3d.Unset;
                    double closestDistanceSquared = double.PositiveInfinity;
                    if (previousPolyline != null &&
                        previousPolyline.ClosestPoint(current, out double parameter))
                    {
                        support = previousPolyline.PointAt(parameter);
                        closestDistanceSquared = current.DistanceToSquared(support);
                    }
                    if (!support.IsValid && previousAlternatives != null)
                    {
                        foreach (PolylineCurve candidate in previousAlternatives)
                        {
                            if (!candidate.ClosestPoint(
                                    current,
                                    out double candidateParameter))
                                continue;
                            Point3d candidatePoint =
                                candidate.PointAt(candidateParameter);
                            double distanceSquared =
                                current.DistanceToSquared(candidatePoint);
                            if (distanceSquared < closestDistanceSquared)
                            {
                                closestDistanceSquared = distanceSquared;
                                support = candidatePoint;
                            }
                        }
                    }
                    if (!support.IsValid)
                    {
                        result.Add(fallback, path);
                        fallbackCount++;
                        continue;
                    }

                    Vector3d direction =
                        directionBranch != null && i < directionBranch.Count
                            ? directionBranch[i]
                            : sourceBranch[i].ZAxis;
                    if (!direction.Unitize())
                        direction = sourceBranch[i].ZAxis;
                    double height = (current - support) * direction;
                    if (!double.IsFinite(height) || height <= tolerance)
                    {
                        result.Add(fallback, path);
                        fallbackCount++;
                        continue;
                    }

                    result.Add(height, path);
                    geometryCount++;
                }
            }
            return result;
        }

        private static bool TryPreviousLayerPath(
            GH_Path path,
            int layerDimension,
            int previousLayer,
            out GH_Path previousPath)
        {
            previousPath = null;
            if (path == null ||
                layerDimension < 0 ||
                layerDimension >= path.Indices.Length)
                return false;
            int[] indices = path.Indices.ToArray();
            indices[layerDimension] = previousLayer;
            previousPath = new GH_Path(indices);
            return true;
        }

        private static DataTree<double> RebuildLayerWf(
            DataTree<double> nominalWidths,
            DataTree<double> originalWidths,
            DataTree<double> flows,
            DataTree<double> heights,
            DataTree<Plane> planes,
            DataTree<bool> moved,
            double tolerance)
        {
            if (nominalWidths == null || flows == null || heights == null)
                return originalWidths;
            var result = new DataTree<double>();
            for (int b = 0; b < planes.BranchCount; b++)
            {
                GH_Path path = planes.Paths[b];
                IList<Plane> planeBranch = planes.Branches[b];
                IList<double> nominalBranch = Branch(nominalWidths, path);
                IList<double> oldBranch = Branch(originalWidths, path);
                IList<double> flowBranch = Branch(flows, path);
                IList<double> heightBranch = Branch(heights, path);
                IList<bool> movedBranch = moved != null && moved.PathExists(path)
                    ? moved.Branch(path)
                    : null;
                for (int i = 0; i < planeBranch.Count; i++)
                {
                    bool changed = movedBranch != null && i < movedBranch.Count && movedBranch[i];
                    double old = ValueAt(oldBranch, i, double.NaN);
                    if (!changed && double.IsFinite(old))
                    {
                        result.Add(old, path);
                        continue;
                    }
                    double nominal = ValueAt(nominalBranch, i, old);
                    double flow = ValueAt(flowBranch, i, 1.0);
                    double height = ValueAt(heightBranch, i, 0.0);
                    result.Add(
                        EstimateFlowAdjustedWidth(
                            nominal,
                            height,
                            flow,
                            tolerance),
                        path);
                }
            }
            return result;
        }

        private static double EstimateFlowAdjustedWidth(
            double nominalWidth,
            double height,
            double flow,
            double tolerance)
        {
            if (!double.IsFinite(nominalWidth) || nominalWidth <= tolerance)
                return nominalWidth;
            if (!double.IsFinite(height) || height <= tolerance ||
                !double.IsFinite(flow) || flow <= tolerance)
                return nominalWidth;
            double referenceWidth = Math.Max(nominalWidth, height);
            double referenceArea =
                height * (referenceWidth - height) +
                Math.PI * height * height / 4.0;
            return flow * referenceArea / height +
                   height * (1.0 - Math.PI / 4.0);
        }

        private static DataTree<double> RebuildPrintVolume(
            DataTree<Plane> planes,
            DataTree<double> heights,
            DataTree<double> widths,
            double tolerance)
        {
            if (planes == null || heights == null || widths == null)
                return null;
            var result = new DataTree<double>();
            for (int b = 0; b < planes.BranchCount; b++)
            {
                GH_Path path = planes.Paths[b];
                IList<Plane> planeBranch = planes.Branches[b];
                IList<double> heightBranch = Branch(heights, path);
                IList<double> widthBranch = Branch(widths, path);
                for (int i = 0; i < planeBranch.Count; i++)
                {
                    double volume = 0.0;
                    if (i > 0)
                    {
                        double height = ValueAt(heightBranch, i, 0.0);
                        double width = ValueAt(widthBranch, i, 0.0);
                        double length =
                            planeBranch[i - 1].Origin.DistanceTo(planeBranch[i].Origin);
                        if (height > tolerance && width > tolerance &&
                            double.IsFinite(length))
                        {
                            double area =
                                height * (width - height) +
                                Math.PI * height * height / 4.0;
                            if (double.IsFinite(area) && area > 0.0)
                                volume = area * length;
                        }
                    }
                    result.Add(volume, path);
                }
            }
            return result;
        }

        private void SetMorphOutputs(
            IGH_DataAccess da,
            WasperPrintPath path,
            string summary,
            DataTree<Plane> sourceLayerPlanes,
            DataTree<Plane> morphedLayerPlanes,
            DataTree<double> planeDeviation)
        {
            WasperPathDebugOutputs.Set(da, this, path, summary);

            int index = WasperPathDebugOutputs.OutputIndex(this, "la_planes");
            if (index >= 0 && sourceLayerPlanes != null)
                da.SetDataTree(index, WasperGcodeTreeUtil.ToPlaneStructure(sourceLayerPlanes));
            index = WasperPathDebugOutputs.OutputIndex(this, "la_planes_m");
            if (index >= 0 && morphedLayerPlanes != null)
                da.SetDataTree(index, WasperGcodeTreeUtil.ToPlaneStructure(morphedLayerPlanes));
            index = WasperPathDebugOutputs.OutputIndex(this, "plane_dev");
            if (index >= 0 && planeDeviation != null)
                da.SetDataTree(index, WasperGcodeTreeUtil.ToNumberStructure(planeDeviation));
        }

        private static DataTree<double> BuildZeroDeviation(DataTree<Plane> planes)
        {
            if (planes == null)
                return null;
            var result = new DataTree<double>();
            for (int b = 0; b < planes.BranchCount; b++)
            {
                GH_Path path = planes.Paths[b];
                for (int i = 0; i < planes.Branches[b].Count; i++)
                    result.Add(0.0, path);
            }
            return result;
        }

        private static DataTree<double> BuildPlaneDeviation(
            DataTree<Plane> originalPlanes,
            DataTree<Point3d> mappedOrigins,
            IDictionary<int, Plane> sourceLayerPlanes,
            int commonPrefix)
        {
            if (originalPlanes == null || mappedOrigins == null)
                return null;
            var result = new DataTree<double>();
            for (int b = 0; b < originalPlanes.BranchCount; b++)
            {
                GH_Path path = originalPlanes.Paths[b];
                IList<Plane> sourceBranch = originalPlanes.Branches[b];
                IList<Point3d> mappedBranch = mappedOrigins.PathExists(path)
                    ? mappedOrigins.Branch(path)
                    : null;
                int layer = WasperGcodeTreeUtil.LayerFromPath(path, commonPrefix);
                Plane layerPlane = Plane.Unset;
                bool hasLayerPlane =
                    sourceLayerPlanes != null &&
                    sourceLayerPlanes.TryGetValue(layer, out layerPlane);

                for (int i = 0; i < sourceBranch.Count; i++)
                {
                    Plane source = sourceBranch[i];
                    Point3d mapped =
                        mappedBranch != null && i < mappedBranch.Count
                            ? mappedBranch[i]
                            : source.Origin;
                    Vector3d normal = hasLayerPlane
                        ? layerPlane.ZAxis
                        : source.ZAxis;
                    if (!normal.Unitize())
                        normal = Vector3d.ZAxis;
                    result.Add((mapped - source.Origin) * normal, path);
                }
            }
            return result;
        }

        private static IList<double> Branch(DataTree<double> tree, GH_Path path)
        {
            if (tree == null || tree.BranchCount == 0)
                return null;
            if (tree.PathExists(path))
                return tree.Branch(path);
            return tree.BranchCount == 1 ? tree.Branches[0] : null;
        }

        private static IList<bool> Branch(DataTree<bool> tree, GH_Path path)
        {
            if (tree == null || tree.BranchCount == 0)
                return null;
            if (tree.PathExists(path))
                return tree.Branch(path);
            return tree.BranchCount == 1 ? tree.Branches[0] : null;
        }

        private static double ValueAt(
            IList<double> branch,
            int index,
            double fallback)
        {
            if (branch == null || branch.Count == 0)
                return fallback;
            double value = branch[branch.Count == 1 ? 0 : Math.Min(index, branch.Count - 1)];
            return double.IsFinite(value) ? value : fallback;
        }

        private static bool ValueAt(
            IList<bool> branch,
            int index,
            bool fallback)
        {
            if (branch == null || branch.Count == 0)
                return fallback;
            return branch[branch.Count == 1 ? 0 : Math.Min(index, branch.Count - 1)];
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
            var result = new DataTree<Curve>();
            for (int b = 0; b < planes.BranchCount; b++)
            {
                GH_Path path = planes.Paths[b];
                IList<Plane> branch = planes.Branches[b];
                if (branch == null || branch.Count < 2)
                    continue;
                var points = branch.Select(plane => plane.Origin).ToList();
                if (SourceCurveIsClosed(originalSourceCurves, path) &&
                    points[0].DistanceTo(points[points.Count - 1]) > tolerance)
                {
                    points.Add(points[0]);
                }
                result.Add(new PolylineCurve(points), path);
            }
            return result.BranchCount > 0 ? result : null;
        }

        private static bool HasDerivedData(WasperPrintPath path)
        {
            return path.PrintLoc != null || path.PrintGlob != null ||
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

        private sealed class ReferenceGeometry
        {
            private readonly Mesh _mesh;
            private readonly Brep _brep;
            private readonly Plane? _plane;

            private ReferenceGeometry(Mesh mesh, Brep brep, string kind)
            {
                _mesh = mesh;
                _brep = brep;
                _plane = null;
                Kind = kind;
                Bounds = mesh != null
                    ? mesh.GetBoundingBox(true)
                    : brep.GetBoundingBox(true);
            }

            private ReferenceGeometry(Plane plane)
            {
                _mesh = null;
                _brep = null;
                _plane = plane;
                Kind = "ref_plane";
                Bounds = new BoundingBox(plane.Origin, plane.Origin);
            }

            public string Kind { get; }
            public BoundingBox Bounds { get; }

            public static ReferenceGeometry Create(object geometry)
            {
                if (geometry is Plane plane && plane.IsValid)
                    return new ReferenceGeometry(plane);
                if (geometry is Mesh mesh && mesh.IsValid)
                    return new ReferenceGeometry(mesh, null, "Mesh");
                if (geometry is Brep brep && brep.IsValid)
                    return new ReferenceGeometry(null, brep, "Brep/Surface");
                if (geometry is Surface surface && surface.IsValid)
                {
                    Brep surfaceBrep = surface.ToBrep();
                    return surfaceBrep == null
                        ? null
                        : new ReferenceGeometry(null, surfaceBrep, "Surface");
                }
                if (geometry is BrepFace face && face.IsValid)
                {
                    Brep faceBrep = face.DuplicateFace(false);
                    return faceBrep == null
                        ? null
                        : new ReferenceGeometry(null, faceBrep, "BrepFace");
                }
                if (geometry is Extrusion extrusion && extrusion.IsValid)
                {
                    Brep extrusionBrep = extrusion.ToBrep();
                    return extrusionBrep == null
                        ? null
                        : new ReferenceGeometry(null, extrusionBrep, "Extrusion");
                }
                return null;
            }

            public Point3d[] Intersections(
                Line line,
                double tolerance)
            {
                if (_plane.HasValue)
                {
                    if (!Intersection.LinePlane(
                            line,
                            _plane.Value,
                            out double lineParameter))
                        return Array.Empty<Point3d>();
                    Point3d planeHit = line.PointAt(lineParameter);
                    return planeHit.IsValid
                        ? new[] { planeHit }
                        : Array.Empty<Point3d>();
                }
                Point3d[] points;
                if (_mesh != null)
                {
                    points = Intersection.MeshLine(_mesh, line, out int[] _);
                }
                else
                {
                    var lineCurve = new LineCurve(line);
                    if (!Intersection.CurveBrep(
                            lineCurve,
                            _brep,
                            tolerance,
                            out Curve[] _,
                            out points))
                    {
                        return Array.Empty<Point3d>();
                    }
                }
                if (points == null || points.Length == 0)
                    return Array.Empty<Point3d>();
                double toleranceSquared =
                    Math.Max(tolerance * tolerance, 1e-18);
                var valid = new List<Point3d>();
                foreach (Point3d point in points.Where(point => point.IsValid))
                {
                    if (valid.Any(existing =>
                            existing.DistanceToSquared(point) <= toleranceSquared))
                        continue;
                    valid.Add(point);
                }
                return valid
                    .Where(point => point.IsValid)
                    .ToArray();
            }
        }

        private static class MorphPathIcon
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
                    using (var boundaryPen = new Pen(Color.FromArgb(35, 72, 86), 2.0f))
                    using (var pathPen = new Pen(Color.FromArgb(234, 124, 42), 2.2f))
                    using (var guidePen = new Pen(Color.FromArgb(91, 166, 176), 1.2f)
                    {
                        DashStyle = DashStyle.Dot
                    })
                    {
                        graphics.DrawBezier(boundaryPen, 2, 5, 7, 1, 16, 8, 22, 4);
                        graphics.DrawBezier(boundaryPen, 2, 20, 8, 14, 16, 22, 22, 17);
                        graphics.DrawLine(guidePen, 4, 6, 4, 19);
                        graphics.DrawLine(guidePen, 20, 5, 20, 17);
                        graphics.DrawBezier(pathPen, 4, 17, 9, 7, 15, 18, 20, 7);
                    }
                }
                return bitmap;
            }
        }
    }
}
