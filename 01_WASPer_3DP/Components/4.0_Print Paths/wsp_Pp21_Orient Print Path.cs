// wsp_Pp21_Orient Print Path.cs
// WASPer_3DP - Subcategory: 4.0_Print Paths
//
// wsp_path counterpart of wsp_Sl06_Orient Printing Paths: re-orients a packed
// WASPer Print Path from its stored per-layer reference frames onto a target
// frame sequence generated along a reference curve. Unlike Sl06 there is no
// tar_pl override input; ref_curve is the only frame source.
//
// The component does not create, remove, or reslice layers. One rigid
// Transform.PlaneToPlane per logical layer maps that layer's canonical
// pt_planes (and its exact source curves) from the stored layer plane onto the
// generated frame. Branch topology, branch count, and point count never change.
//
// LAYER PLANE CONTRACT: WasperPrintPath.LayerPlanes stores at most one valid
// SUPPLIED reference plane per logical layer, and missing layers remain absent
// (Components\Shared\Fabrication\WASPer_GcodeTypes.cs). Pp21 therefore never
// fits or invents a source frame, mirroring Gc21/Pp09. A path with no stored
// layer planes is refused; individual layers without a stored plane pass
// through in their original position and are reported.
//
// LAYER HEIGHT: each layer is rigid, but the layers move INDEPENDENTLY, so
// distances measured WITHIN a layer survive while distances BETWEEN layers do
// not. Re-orientation therefore invalidates layer_h, and with it the derived
// layer_wf and print_vol. Pp21 recomputes all three:
//   layer_h  = perpendicular distance from each point to the target frame of
//              the previous oriented layer. This captures the fan opened by a
//              curved reference curve: on the inside of a bend consecutive
//              frames converge and beads get thinner, on the outside they
//              diverge and beads get thicker.
//   layer 0  = measured against a datum plane at the reference-curve start
//              whose normal is the start tangent, pushed back along that normal
//              by the representative incoming first-layer height so the first
//              bead keeps its upstream bed clearance instead of collapsing to
//              zero at the curve start.
//   layer_wf = rebuilt from layer_w, the new layer_h, and flows using the same
//              stadium-section bead model as Pp01/Pp04/Pp06/Pp09.
//   print_vol= rebuilt per segment from the new layer_wf and layer_h.
// Layers left unoriented keep their incoming heights, because they did not move.
//
// METADATA: flows, layer_w, print_speed, path roles, stroke ids, nozzle
// diameter and travel/Z-hop settings are preserved unchanged; they do not
// depend on inter-layer spacing. World-orientation-dependent analysis (Pr01,
// Pr03, Pr04) and the coordinate-bound Gc04 motion/KPI block are cleared and
// must be recomputed downstream; the outgoing path is marked partial and
// carries no content signature, so no cached consumer can reuse a
// pre-orientation result.

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
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

using WASPer_3DP;
#endregion

namespace WASPer_3DP_Components._4_0_Print_Paths
{
    public sealed class wsp_Pp21_Orient_Print_Path : GH_Component
    {
        private const string SummaryDescription =
            "Layer count, frame source, distribution mode, skipped layers, and cleared-data report.";

        private readonly string _versionTag;

        public wsp_Pp21_Orient_Print_Path()
            : base(
                "wsp_Pp21_Orient Print Path",
                "OrientPath",
                "Maps a packed WASPer Print Path from its stored layer planes onto a target frame " +
                "sequence generated along a reference curve.\n" +
                "The number of target frames is inherited from the logical layers that carry an " +
                "authoritative layer plane; this component never creates, removes, or reslices layers.\n" +
                "Frame Z axes follow the reference-curve tangent and the local XY orientation is " +
                "transported along the curve, so consecutive layers keep a stable roll.\n" +
                "Each layer is moved by one rigid plane-to-plane transform, so branch topology, flows, " +
                "layer_w, print_speed, path roles, and stroke ids stay valid and unchanged.\n" +
                "Because the layers move independently, the spacing between them changes: layer_h is " +
                "recomputed as the perpendicular distance from each point to the previous layer's " +
                "target frame, so beads thin on the inside of a bend and thicken on the outside. " +
                "The first layer is measured against a datum plane at the reference-curve start, " +
                "normal to the start tangent, offset back by the incoming first-layer height. " +
                "layer_wf and print_vol are then rebuilt from the new heights.\n" +
                "Gravity-dependent analysis (Pr01, Pr03, Pr04) and the Gc04 motion/KPI block are " +
                "cleared and must be recomputed after re-orientation.\n" +
                "Layers without a stored layer plane are left in place and reported; no source frame " +
                "is ever inferred.\n\n" +
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
            new Guid("2C7A94E5-51D0-4B6E-9F32-8AD6C1E30B47");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Pp21_Orient Print Path.png"))
                    {
                        return stream != null ? new Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        // ---------------------------------------------------------------------
        //  Persistent "Show all outputs" layout
        // ---------------------------------------------------------------------

        private int _visibleOutputsMask;
        private const string ShowAllOutputsKey = "wsp_pp21_show_all_outputs";
        private const string VisibleOutputsMaskKey = "wsp_pp21_visible_outputs_mask";

        private static readonly string[] OutputCatalog = WasperPathDebugOutputs.CoreNickNames
            .Concat(new[] { "la_planes_src", "xform", "t" })
            .ToArray();
        private static readonly int AllOutputsMask = (1 << OutputCatalog.Length) - 1;

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
                    RecordUndoEvent("Toggle outputs");
                    _visibleOutputsMask = mask;
                    WasperPathDebugOutputs.Rebuild(
                        this,
                        _visibleOutputsMask,
                        SummaryDescription,
                        OutputCatalog,
                        registerExtras: RegisterOrientDebugOutputs);
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
                SummaryDescription,
                OutputCatalog,
                registerExtras: RegisterOrientDebugOutputs);
            return base.Read(reader);
        }

        private static void RegisterOrientDebugOutputs(GH_Component component, Func<string, bool> isVisible)
        {
            if (isVisible("la_planes_src"))
                component.Params.RegisterOutputParam(new Param_Plane
                {
                    Name = "source_layer_planes",
                    NickName = "la_planes_src",
                    Description =
                        "Stored source layer plane used for each oriented layer, before the transform. " +
                        "Layers without a stored plane are absent.",
                    Access = GH_ParamAccess.tree
                });
            if (isVisible("xform"))
                component.Params.RegisterOutputParam(new Param_Transform
                {
                    Name = "transforms",
                    NickName = "xform",
                    Description =
                        "Plane-to-plane transform applied to each oriented layer, from its stored layer " +
                        "plane to its generated target frame. One transform per oriented layer.",
                    Access = GH_ParamAccess.tree
                });
            if (isVisible("t"))
                component.Params.RegisterOutputParam(new Param_Number
                {
                    Name = "parameters",
                    NickName = "t",
                    Description =
                        "Normalized reference-curve parameters used for the target frames, in ascending " +
                        "layer order. Values are 0..1 by arc length after distribution has been applied.",
                    Access = GH_ParamAccess.list
                });
        }

        // ---------------------------------------------------------------------
        //  Parameters
        // ---------------------------------------------------------------------

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "WASPer Print Path to re-orient. Its stored la_planes provide the source frame of " +
                "each logical layer and its canonical pt_planes are transformed onto the generated " +
                "target frames.\n" +
                "Layer planes are read as supplied/authoritative data only: layers without one are " +
                "left in place rather than fitted.\n" +
                "Please use the Pp01 WASPer Path from Curves before using this component.",
                GH_ParamAccess.item);

            p.AddCurveParameter(
                "ref_curve",
                "ref",
                "Reference curve used to generate the target frame sequence.\n" +
                "The number of target frames is inherited from the layers that carry a stored layer " +
                "plane; this component does not create new layers.\n" +
                "Frame Z axes follow the curve tangent; local XY axes are transported along the curve " +
                "for a stable plane-to-plane mapping.",
                GH_ParamAccess.item);

            p.AddIntegerParameter(
                "distribution",
                "dist",
                "How target frames are distributed along ref_curve:\n" +
                "0 = source spacing ratios: preserve the relative spacing measured between the stored " +
                "layer-plane origins.\n" +
                "1 = uniform: place all oriented layers evenly along the reference curve by arc length.\n" +
                "2 = curvature weighted: place more frames where the reference curve bends more.",
                GH_ParamAccess.item,
                0);

            p.AddNumberParameter(
                "curv_weight",
                "cW",
                "Curvature weighting strength used only when distribution = 2.\n" +
                "0 behaves like uniform distribution. Higher values attract more layer frames toward " +
                "high-curvature regions. A value around 1-3 is usually a good starting range.",
                GH_ParamAccess.item,
                1.0);

            p.AddVectorParameter(
                "up_vector",
                "up",
                "Reference up vector used to initialize and recover the target frame roll around the " +
                "reference-curve tangent.\n" +
                "The first generated frame projects this vector onto the plane perpendicular to the " +
                "tangent as local Y; subsequent frames transport that XY orientation along the curve.\n" +
                "If the vector is parallel to the tangent, a safe perpendicular fallback is used.",
                GH_ParamAccess.item,
                Vector3d.ZAxis);

            p.AddNumberParameter(
                "twist",
                "twist",
                "Extra rotation around each target frame Z axis, in degrees.\n" +
                "Accepts a list: one value is global; multiple values cycle by ascending layer order.\n" +
                "Useful for tuning nozzle/road orientation after the tangent frame is generated.",
                GH_ParamAccess.list,
                0.0);

            for (int i = 2; i < p.ParamCount; i++)
                p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "Re-oriented WASPer Print Path. Canonical pt_planes, source curves, and stored layer " +
                "planes follow the generated frames; layer_h, layer_wf, and print_vol are recomputed " +
                "for the new layer spacing; flows, layer_w, print_speed, roles, and stroke ids are " +
                "preserved; stale analysis/motion/KPI data is cleared.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "summary",
                "summary",
                SummaryDescription,
                GH_ParamAccess.item);
        }

        // ---------------------------------------------------------------------
        //  Solve
        // ---------------------------------------------------------------------

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            WasperPrintPath packedPath = null;
            if (!WasperGcodeTreeUtil.TryGetPrintPath(DA, 0, out packedPath) ||
                packedPath == null ||
                !packedPath.HasPlanes)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Pp21 requires a valid wsp_path input. Please use the Pp01 WASPer Path from Curves " +
                    "before using this component.");
                return;
            }

            Curve refCurve = null;
            if (!DA.GetData(1, ref refCurve) || refCurve == null || !refCurve.IsValid)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Provide a valid ref_curve. Pp21 generates its target frames along that curve.");
                return;
            }

            int distribution = 0;
            double curvWeight = 1.0;
            Vector3d up = Vector3d.ZAxis;
            var twistDeg = new List<double>();

            DA.GetData(2, ref distribution);
            DA.GetData(3, ref curvWeight);
            DA.GetData(4, ref up);
            DA.GetDataList(5, twistDeg);

            distribution = WasperFrameSequence.ClampDistribution(distribution);
            curvWeight = double.IsNaN(curvWeight) ? 0.0 : Math.Max(0.0, curvWeight);
            up = WasperFrameSequence.SanitizeUpVector(up);
            if (twistDeg.Count == 0) twistDeg.Add(0.0);

            if (!packedPath.HasLayerPlanes)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Pp21 requires authoritative layer planes on the incoming wsp_path. Wire la_planes " +
                    "from Sl02 SlicerPlus or a path-based infill into Pp01 WASPer Path from Curves, or " +
                    "supply them through Pp20 Construct WASPer Path. Source frames are never inferred.");
                return;
            }

            double tol = RhinoDoc.ActiveDoc != null
                ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
                : 1e-6;
            tol = Math.Max(tol, 1e-9);

            int prefix = WasperGcodeTreeUtil.CommonPathPrefixLength(packedPath.PtPlanes.Paths);

            // Stored source frame per logical layer. Supplied data only: absent
            // layers stay absent, exactly as the LayerPlanes contract requires.
            var sourceByLayer = new Dictionary<int, Plane>();
            for (int b = 0; b < packedPath.LayerPlanes.BranchCount; b++)
            {
                IList<Plane> branch = packedPath.LayerPlanes.Branches[b];
                if (branch == null || branch.Count == 0)
                    continue;
                Plane stored = branch.FirstOrDefault(plane => plane.IsValid);
                if (!stored.IsValid)
                    continue;
                int layer = WasperGcodeTreeUtil.LayerFromPath(
                    packedPath.LayerPlanes.Paths[b],
                    prefix);
                if (!sourceByLayer.ContainsKey(layer))
                    sourceByLayer[layer] = stored;
            }

            // Logical layers actually present in the geometry, ascending.
            var geometryLayers = new SortedSet<int>();
            for (int b = 0; b < packedPath.PtPlanes.BranchCount; b++)
            {
                geometryLayers.Add(WasperGcodeTreeUtil.LayerFromPath(
                    packedPath.PtPlanes.Paths[b],
                    prefix));
            }

            var orientedLayers = new List<int>();
            var skippedLayers = new List<int>();
            foreach (int layer in geometryLayers)
            {
                if (sourceByLayer.ContainsKey(layer))
                    orientedLayers.Add(layer);
                else
                    skippedLayers.Add(layer);
            }

            if (orientedLayers.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "None of the logical layers in this wsp_path carry a stored layer plane, so no " +
                    "source frame could be resolved. Check that la_planes were packed for the same " +
                    "layer indices as the path geometry.");
                return;
            }

            // Target frames.
            var sourceOrigins = new List<Point3d>(orientedLayers.Count);
            for (int i = 0; i < orientedLayers.Count; i++)
                sourceOrigins.Add(sourceByLayer[orientedLayers[i]].Origin);

            List<double> normalizedParameters = WasperFrameSequence.BuildDistributionParameters(
                sourceOrigins, refCurve, distribution, curvWeight, tol);
            List<Plane> targetFrames = WasperFrameSequence.BuildFramesOnCurve(
                refCurve, normalizedParameters, up, tol);
            WasperFrameSequence.ApplyTwist(targetFrames, twistDeg);

            if (targetFrames.Count != orientedLayers.Count)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "The target frame sequence could not be generated along ref_curve. Check that the " +
                    "curve is valid and has a measurable length.");
                return;
            }

            var xformByLayer = new Dictionary<int, Transform>();
            var targetByLayer = new Dictionary<int, Plane>();
            for (int i = 0; i < orientedLayers.Count; i++)
            {
                int layer = orientedLayers[i];
                Plane source = sourceByLayer[layer];
                Plane target = targetFrames[i];
                targetByLayer[layer] = target;
                xformByLayer[layer] = Transform.PlaneToPlane(source, target);
            }

            // -----------------------------------------------------------------
            //  Rigid per-layer transform of the canonical geometry
            // -----------------------------------------------------------------

            var outPlanes = new DataTree<Plane>();
            int transformedPoints = 0;
            int untouchedPoints = 0;
            int failedPlanes = 0;

            for (int b = 0; b < packedPath.PtPlanes.BranchCount; b++)
            {
                GH_Path path = packedPath.PtPlanes.Paths[b];
                outPlanes.EnsurePath(path);

                IList<Plane> branch = packedPath.PtPlanes.Branches[b];
                if (branch == null)
                    continue;

                int layer = WasperGcodeTreeUtil.LayerFromPath(path, prefix);
                bool oriented = xformByLayer.TryGetValue(layer, out Transform xform);

                for (int i = 0; i < branch.Count; i++)
                {
                    Plane original = branch[i];
                    if (!oriented || !original.IsValid)
                    {
                        outPlanes.Add(original, path);
                        untouchedPoints++;
                        continue;
                    }

                    Plane moved = original;
                    if (!moved.Transform(xform))
                    {
                        outPlanes.Add(original, path);
                        failedPlanes++;
                        continue;
                    }

                    outPlanes.Add(moved, path);
                    transformedPoints++;
                }
            }

            // Exact source curves travel with the geometry, so downstream
            // resampling (Pp06, Gc21) cannot contradict the moved points.
            DataTree<Curve> outSourceCurves = null;
            int transformedCurves = 0;
            if (packedPath.HasSourceCurves)
            {
                outSourceCurves = new DataTree<Curve>();
                for (int b = 0; b < packedPath.SourceCurves.BranchCount; b++)
                {
                    GH_Path path = packedPath.SourceCurves.Paths[b];
                    outSourceCurves.EnsurePath(path);

                    IList<Curve> branch = packedPath.SourceCurves.Branches[b];
                    if (branch == null)
                        continue;

                    int layer = WasperGcodeTreeUtil.LayerFromPath(path, prefix);
                    bool oriented = xformByLayer.TryGetValue(layer, out Transform xform);

                    for (int i = 0; i < branch.Count; i++)
                    {
                        Curve source = branch[i];
                        if (source == null)
                            continue;

                        Curve duplicate = source.DuplicateCurve();
                        if (duplicate == null)
                        {
                            outSourceCurves.Add(source, path);
                            continue;
                        }

                        if (oriented && duplicate.Transform(xform))
                            transformedCurves++;

                        // Re-orienting does not change what a curve semantically
                        // is, so the shared WASPer.PathRole tag is carried over
                        // explicitly rather than trusting DuplicateCurve/Transform.
                        WasperPathRoleMetadata.Copy(source, duplicate);
                        outSourceCurves.Add(duplicate, path);
                    }
                }
            }

            // Stored layer planes are deliberately transported: oriented layers
            // receive their generated frame, skipped layers keep their plane.
            var outLayerPlanes = new DataTree<Plane>();
            for (int b = 0; b < packedPath.LayerPlanes.BranchCount; b++)
            {
                IList<Plane> branch = packedPath.LayerPlanes.Branches[b];
                if (branch == null || branch.Count == 0)
                    continue;
                Plane stored = branch.FirstOrDefault(plane => plane.IsValid);
                if (!stored.IsValid)
                    continue;

                GH_Path path = packedPath.LayerPlanes.Paths[b];
                int layer = WasperGcodeTreeUtil.LayerFromPath(path, prefix);
                outLayerPlanes.Add(
                    targetByLayer.TryGetValue(layer, out Plane target) ? target : stored,
                    path);
            }

            // -----------------------------------------------------------------
            //  Layer height, flow-adjusted width, and deposited volume
            // -----------------------------------------------------------------
            //  Each layer is rigid on its own, but the layers moved
            //  independently, so the spacing between them is no longer the
            //  spacing the incoming layer_h describes.

            var orientedIndexByLayer = new Dictionary<int, int>();
            for (int i = 0; i < orientedLayers.Count; i++)
                orientedIndexByLayer[orientedLayers[i]] = i;

            double firstHeight = ResolveFirstLayerHeight(
                packedPath, orientedLayers[0], targetFrames, prefix, tol);
            Plane baseDatum = BuildBaseDatumPlane(refCurve, targetFrames[0], firstHeight);

            DataTree<double> outLayerH = RecomputeLayerHeights(
                packedPath,
                outPlanes,
                orientedIndexByLayer,
                targetFrames,
                baseDatum,
                prefix,
                tol,
                out int recomputedHeights,
                out int fallbackHeights);

            DataTree<double> outLayerWf = null;
            DataTree<double> outPrintVol = null;
            bool heightsRebuilt = outLayerH != null && recomputedHeights > 0;

            if (heightsRebuilt && packedPath.HasLayerW && packedPath.HasFlows)
            {
                outLayerWf = RebuildFlowAdjustedWidth(
                    packedPath.LayerW, outLayerH, packedPath.Flows, tol);
                outPrintVol = RebuildPrintVolume(outPlanes, outLayerH, outLayerWf, tol);
            }
            else if (!heightsRebuilt)
            {
                // Nothing moved relative to anything else, so the incoming
                // derived fields remain exactly valid.
                outLayerWf = packedPath.LayerWf;
                outPrintVol = packedPath.PrintVol;
            }

            // -----------------------------------------------------------------
            //  Repack
            // -----------------------------------------------------------------

            string clearedGroups = DescribeClearedGroups(packedPath);

            var outPath = new WasperPrintPath(
                points: null,
                ptPlanes: outPlanes,
                flows: packedPath.Flows,
                layerH: outLayerH,
                printSpeed: packedPath.PrintSpeed,
                nozzleDiam: packedPath.NozzleDiam,
                layerW: packedPath.LayerW,
                layerWf: outLayerWf,
                printVol: outPrintVol,
                travelSpeed: packedPath.TravelSpeed,
                zHop: packedPath.ZHop,
                zHopSpeed: packedPath.ZHopSpeed,
                isPartial: true,
                sourceCurves: outSourceCurves,
                pathRoles: packedPath.PathRoles,
                layerPlanes: outLayerPlanes,
                strokeIds: packedPath.StrokeIds,
                hasCrossLayerShellContinuity: packedPath.HasCrossLayerShellContinuity);

            // -----------------------------------------------------------------
            //  Diagnostics
            // -----------------------------------------------------------------

            int overlapLayer = FindFirstOverlapLayer(
                packedPath,
                orientedLayers,
                sourceByLayer,
                normalizedParameters,
                refCurve,
                prefix,
                out double overlapExtent,
                out double overlapRadius);

            var summary = new StringBuilder();
            summary.Append("OK | layers=").Append(orientedLayers.Count)
                   .Append('/').Append(geometryLayers.Count);
            summary.Append(" | frames=ref_curve");
            summary.Append(" | orientation=rotation-minimizing XY transport");
            summary.Append(" | distribution=").Append(WasperFrameSequence.DistributionName(distribution));
            summary.Append(" | curv_weight=").Append(curvWeight.ToString("0.###"));
            summary.Append(" | twist_values=").Append(twistDeg.Count);
            summary.Append(" | branches=").Append(packedPath.PtPlanes.BranchCount);
            summary.Append(" | points=").Append(transformedPoints)
                   .Append('/').Append(transformedPoints + untouchedPoints + failedPlanes);
            if (outSourceCurves != null)
                summary.Append(" | source_curves=").Append(transformedCurves);
            summary.Append(" | layers_skipped=").Append(skippedLayers.Count);
            if (failedPlanes > 0)
                summary.Append(" | planes_failed=").Append(failedPlanes);
            summary.Append(" | layer_h=").Append(heightsRebuilt ? "recomputed" : "unchanged");
            if (heightsRebuilt)
            {
                summary.Append(" (").Append(recomputedHeights).Append(" pts");
                if (fallbackHeights > 0)
                    summary.Append(", ").Append(fallbackHeights).Append(" fallback");
                summary.Append(", datum=curve start -").Append(firstHeight.ToString("0.###")).Append(')');
                summary.Append(" | layer_wf=").Append(outLayerWf != null ? "rebuilt" : "cleared");
                summary.Append(" | print_vol=").Append(outPrintVol != null ? "rebuilt" : "cleared");
            }
            summary.Append(" | cleared=").Append(clearedGroups);

            WasperPathDebugOutputs.Set(DA, this, outPath, summary.ToString());

            int index = WasperPathDebugOutputs.OutputIndex(this, "la_planes_src");
            if (index >= 0)
            {
                var sourceTree = new DataTree<Plane>();
                for (int i = 0; i < orientedLayers.Count; i++)
                {
                    int layer = orientedLayers[i];
                    sourceTree.Add(sourceByLayer[layer], new GH_Path(layer));
                }
                DA.SetDataTree(index, WasperGcodeTreeUtil.ToPlaneStructure(sourceTree));
            }

            index = WasperPathDebugOutputs.OutputIndex(this, "xform");
            if (index >= 0)
            {
                var transformStructure = new GH_Structure<GH_Transform>();
                for (int i = 0; i < orientedLayers.Count; i++)
                {
                    int layer = orientedLayers[i];
                    transformStructure.Append(
                        new GH_Transform(xformByLayer[layer]),
                        new GH_Path(layer));
                }
                DA.SetDataTree(index, transformStructure);
            }

            index = WasperPathDebugOutputs.OutputIndex(this, "t");
            if (index >= 0)
                DA.SetDataList(index, normalizedParameters);

            Message = skippedLayers.Count > 0
                ? $"{_versionTag} | {orientedLayers.Count} layers | {skippedLayers.Count} skip"
                : $"{_versionTag} | {orientedLayers.Count} layers";

            if (skippedLayers.Count > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{skippedLayers.Count} logical layer(s) have no stored layer plane and were left " +
                    $"in their original position: {DescribeLayers(skippedLayers)}. Pp21 does not infer " +
                    "source frames; pack la_planes for those layers to orient them.");
            }

            if (failedPlanes > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{failedPlanes} point plane(s) could not be transformed and kept their original " +
                    "position. Check the stored layer planes of those layers for degenerate axes.");
            }

            if (overlapLayer >= 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"Layer {overlapLayer} extends {overlapExtent:0.###} from its frame origin while the " +
                    $"local reference-curve radius is {overlapRadius:0.###}. Re-oriented layers will " +
                    "overlap on the inside of that bend. Use a straighter reference curve or a smaller " +
                    "cross-section there.");
            }

            if (heightsRebuilt)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "layer_h was recomputed against the new frame spacing: the layers are individually " +
                    "rigid but moved independently, so the distance between them changed. The first " +
                    $"layer was measured against a datum at the reference-curve start offset by " +
                    $"{firstHeight:0.###}. " +
                    (outLayerWf != null
                        ? "layer_wf and print_vol were rebuilt from the new heights."
                        : "layer_wf and print_vol could not be rebuilt because the incoming path has no " +
                          "layer_w and/or flows, so they were cleared; supply them through Pp20 or " +
                          "recompute downstream."));
            }

            if (fallbackHeights > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{fallbackHeights} point(s) could not be measured against a previous frame and kept " +
                    "their incoming layer_h.");
            }

            if (!string.Equals(clearedGroups, "none", StringComparison.Ordinal))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"Re-orientation invalidated data from: {clearedGroups}. Those fields were cleared " +
                    "and should be recomputed downstream. Flows, layer_w, print_speed, roles, and " +
                    "stroke ids are unaffected: they do not depend on inter-layer spacing.");
            }
        }

        // ---------------------------------------------------------------------
        //  Helpers
        // ---------------------------------------------------------------------

        /// <summary>
        /// Representative incoming layer height used to push the first-layer datum
        /// plane back from the reference-curve start. Prefers the median height of
        /// the first oriented layer, then the median of the whole path, then the
        /// spacing between the first two generated frames.
        /// </summary>
        private static double ResolveFirstLayerHeight(
            WasperPrintPath path,
            int firstLayer,
            IList<Plane> targetFrames,
            int prefix,
            double tol)
        {
            var firstLayerValues = new List<double>();
            var allValues = new List<double>();

            if (path.HasLayerH)
            {
                for (int b = 0; b < path.LayerH.BranchCount; b++)
                {
                    IList<double> branch = path.LayerH.Branches[b];
                    if (branch == null || branch.Count == 0)
                        continue;

                    int layer = WasperGcodeTreeUtil.LayerFromPath(path.LayerH.Paths[b], prefix);
                    for (int i = 0; i < branch.Count; i++)
                    {
                        double value = branch[i];
                        if (!double.IsFinite(value) || value <= tol)
                            continue;
                        allValues.Add(value);
                        if (layer == firstLayer)
                            firstLayerValues.Add(value);
                    }
                }
            }

            double median = Median(firstLayerValues);
            if (median > tol)
                return median;

            median = Median(allValues);
            if (median > tol)
                return median;

            if (targetFrames.Count >= 2)
            {
                double spacing = targetFrames[0].Origin.DistanceTo(targetFrames[1].Origin);
                if (double.IsFinite(spacing) && spacing > tol)
                    return spacing;
            }

            return 0.0;
        }

        private static double Median(List<double> values)
        {
            if (values == null || values.Count == 0)
                return 0.0;
            values.Sort();
            int mid = values.Count / 2;
            return values.Count % 2 == 1
                ? values[mid]
                : 0.5 * (values[mid - 1] + values[mid]);
        }

        /// <summary>
        /// Datum plane for the first oriented layer: positioned at the reference
        /// curve start with the start tangent as its normal, then pushed back
        /// along that normal by the representative first-layer height. Without the
        /// offset the first frame would sit exactly on the datum and every
        /// first-layer height would collapse to zero.
        /// </summary>
        private static Plane BuildBaseDatumPlane(Curve refCurve, Plane firstFrame, double firstHeight)
        {
            Point3d origin = refCurve.PointAtStart;
            Vector3d normal = refCurve.TangentAtStart;
            if (!normal.IsValid || !normal.Unitize())
                normal = firstFrame.ZAxis;

            var datum = new Plane(origin, normal);
            if (!datum.IsValid)
            {
                datum = firstFrame;
                normal = firstFrame.ZAxis;
            }

            if (double.IsFinite(firstHeight) && firstHeight > 0.0)
                datum.Origin = datum.Origin - normal * firstHeight;

            return datum;
        }

        /// <summary>
        /// Per-point layer height measured perpendicular to the target frame of the
        /// previous oriented layer, which is what actually changed: the layers are
        /// individually rigid but moved independently. Unoriented layers and
        /// unmeasurable points fall back to their incoming height.
        /// </summary>
        private static DataTree<double> RecomputeLayerHeights(
            WasperPrintPath path,
            DataTree<Plane> movedPlanes,
            IDictionary<int, int> orientedIndexByLayer,
            IList<Plane> targetFrames,
            Plane baseDatum,
            int prefix,
            double tol,
            out int recomputed,
            out int fallbacks)
        {
            recomputed = 0;
            fallbacks = 0;

            if (movedPlanes == null || movedPlanes.BranchCount == 0)
                return path.LayerH;

            var result = new DataTree<double>();
            bool anyValue = false;

            for (int b = 0; b < movedPlanes.BranchCount; b++)
            {
                GH_Path branchPath = movedPlanes.Paths[b];
                result.EnsurePath(branchPath);

                IList<Plane> branch = movedPlanes.Branches[b];
                if (branch == null)
                    continue;

                IList<double> incoming =
                    path.LayerH != null && path.LayerH.PathExists(branchPath)
                        ? path.LayerH.Branch(branchPath)
                        : null;

                int layer = WasperGcodeTreeUtil.LayerFromPath(branchPath, prefix);
                bool oriented = orientedIndexByLayer.TryGetValue(layer, out int orientedIndex);

                Plane datum = Plane.Unset;
                bool hasDatum = false;
                if (oriented)
                {
                    datum = orientedIndex == 0 ? baseDatum : targetFrames[orientedIndex - 1];
                    hasDatum = datum.IsValid;
                }

                for (int i = 0; i < branch.Count; i++)
                {
                    double incomingValue = incoming != null && incoming.Count > 0
                        ? incoming[Math.Min(i, incoming.Count - 1)]
                        : double.NaN;

                    if (!hasDatum || !branch[i].IsValid)
                    {
                        result.Add(double.IsFinite(incomingValue) ? incomingValue : 0.0, branchPath);
                        if (!double.IsFinite(incomingValue))
                            fallbacks++;
                        anyValue = true;
                        continue;
                    }

                    double height = Math.Abs(datum.DistanceTo(branch[i].Origin));
                    if (!double.IsFinite(height) || height <= tol)
                    {
                        result.Add(double.IsFinite(incomingValue) ? incomingValue : 0.0, branchPath);
                        fallbacks++;
                        anyValue = true;
                        continue;
                    }

                    result.Add(height, branchPath);
                    recomputed++;
                    anyValue = true;
                }
            }

            return anyValue ? result : path.LayerH;
        }

        /// <summary>
        /// Flow-adjusted deposited width from nominal width, the new height, and
        /// flow. Same stadium-section bead model as Pp01, Pp04, Pp06, and Pp09.
        /// </summary>
        private static DataTree<double> RebuildFlowAdjustedWidth(
            DataTree<double> widths,
            DataTree<double> heights,
            DataTree<double> flows,
            double tolerance)
        {
            if (widths == null || heights == null || flows == null)
                return null;

            var output = new DataTree<double>();
            foreach (GH_Path path in widths.Paths)
            {
                output.EnsurePath(path);
                if (!heights.PathExists(path) || !flows.PathExists(path))
                    continue;

                IList<double> widthBranch = widths.Branch(path);
                IList<double> heightBranch = heights.Branch(path);
                IList<double> flowBranch = flows.Branch(path);
                int count = Math.Min(
                    widthBranch?.Count ?? 0,
                    Math.Min(heightBranch?.Count ?? 0, flowBranch?.Count ?? 0));

                for (int i = 0; i < count; i++)
                {
                    output.Add(
                        EstimateFlowAdjustedWidth(
                            widthBranch[i],
                            heightBranch[i],
                            flowBranch[i],
                            tolerance),
                        path);
                }
            }
            return output;
        }

        /// <summary>
        /// Per-segment deposited volume from the new flow-adjusted width and
        /// height. Index i holds the volume of the segment from i-1 to i.
        /// </summary>
        private static DataTree<double> RebuildPrintVolume(
            DataTree<Plane> planes,
            DataTree<double> heights,
            DataTree<double> widths,
            double tolerance)
        {
            if (planes == null || heights == null || widths == null)
                return null;

            var output = new DataTree<double>();
            for (int b = 0; b < planes.BranchCount; b++)
            {
                GH_Path path = planes.Paths[b];
                output.EnsurePath(path);
                IList<Plane> planeBranch = planes.Branch(path);
                if (planeBranch == null)
                    continue;

                IList<double> heightBranch = heights.PathExists(path) ? heights.Branch(path) : null;
                IList<double> widthBranch = widths.PathExists(path) ? widths.Branch(path) : null;

                for (int i = 0; i < planeBranch.Count; i++)
                {
                    double volume = 0.0;
                    if (i > 0 && heightBranch != null && widthBranch != null &&
                        heightBranch.Count > 0 && widthBranch.Count > 0)
                    {
                        double height = heightBranch[Math.Min(i, heightBranch.Count - 1)];
                        double width = widthBranch[Math.Min(i, widthBranch.Count - 1)];
                        double length = planeBranch[i - 1].Origin.DistanceTo(planeBranch[i].Origin);
                        double area = BeadArea(width, height, tolerance);
                        if (double.IsFinite(length) && length > tolerance)
                            volume = length * area;
                    }
                    output.Add(volume, path);
                }
            }
            return output;
        }

        private static double EstimateFlowAdjustedWidth(
            double nominalWidth,
            double height,
            double flow,
            double tolerance)
        {
            if (nominalWidth <= tolerance || height <= tolerance || flow <= tolerance ||
                !double.IsFinite(nominalWidth) || !double.IsFinite(height) || !double.IsFinite(flow))
                return nominalWidth * Math.Max(flow, 1.0);

            double referenceWidth = Math.Max(nominalWidth, height);
            double referenceArea = BeadArea(referenceWidth, height, tolerance);
            return (flow * referenceArea) / height
                + height * (1.0 - Math.PI / 4.0);
        }

        private static double BeadArea(double width, double height, double tolerance)
        {
            if (!double.IsFinite(width) || !double.IsFinite(height) ||
                width <= tolerance || height <= tolerance)
                return 0.0;

            double effectiveWidth = Math.Max(
                width,
                height * (1.0 - Math.PI / 4.0));
            double area = height * (effectiveWidth - height)
                + Math.PI * height * height / 4.0;
            return double.IsFinite(area) && area > 0.0 ? area : 0.0;
        }

        /// <summary>
        /// Names the optional data groups that the incoming path carried and that
        /// re-orientation makes stale. Returns "none" when nothing was cleared.
        /// </summary>
        private static string DescribeClearedGroups(WasperPrintPath path)
        {
            var groups = new List<string>();

            if (path.HasPrintAssessment ||
                path.SupportPts != null ||
                path.SupportVects != null ||
                path.Angles != null ||
                path.ContactWidths != null)
                groups.Add("Pr01");

            if (path.HasFreshRisk ||
                path.RiskMaterial != null ||
                path.Load != null ||
                path.Capacity != null)
                groups.Add("Pr03");

            if (path.HasBeamDeflection ||
                path.HasFailureState ||
                path.SpanClass != null ||
                path.SpanLen != null ||
                path.BendRatio != null ||
                path.InterfaceRatio != null ||
                path.OverturnRatio != null)
                groups.Add("Pr04");

            if (path.HasMotionPlan ||
                path.HasProcessKpis ||
                path.HasJobKpis ||
                path.KpiUnits.HasValue)
                groups.Add("Gc04");

            return groups.Count == 0 ? "none" : string.Join(",", groups);
        }

        private static string DescribeLayers(IList<int> layers)
        {
            const int maxNamed = 8;
            if (layers.Count <= maxNamed)
                return string.Join(", ", layers);
            return string.Join(", ", layers.Take(maxNamed)) + $", ... (+{layers.Count - maxNamed})";
        }

        /// <summary>
        /// First oriented layer whose radial extent exceeds the local radius of
        /// curvature of the reference curve, meaning the re-oriented layer folds
        /// over itself on the inside of the bend. Returns -1 when nothing is at
        /// risk. Purely advisory: it never changes the output.
        /// </summary>
        private static int FindFirstOverlapLayer(
            WasperPrintPath path,
            IList<int> orientedLayers,
            IDictionary<int, Plane> sourceByLayer,
            IList<double> normalizedParameters,
            Curve refCurve,
            int prefix,
            out double extent,
            out double radius)
        {
            extent = 0.0;
            radius = double.PositiveInfinity;

            if (path == null || refCurve == null || orientedLayers == null)
                return -1;

            var extentByLayer = new Dictionary<int, double>();
            for (int b = 0; b < path.PtPlanes.BranchCount; b++)
            {
                IList<Plane> branch = path.PtPlanes.Branches[b];
                if (branch == null || branch.Count == 0)
                    continue;

                int layer = WasperGcodeTreeUtil.LayerFromPath(path.PtPlanes.Paths[b], prefix);
                if (!sourceByLayer.TryGetValue(layer, out Plane source))
                    continue;

                double best = extentByLayer.TryGetValue(layer, out double stored) ? stored : 0.0;
                for (int i = 0; i < branch.Count; i++)
                {
                    if (!branch[i].IsValid)
                        continue;
                    double distance = source.Origin.DistanceTo(branch[i].Origin);
                    if (distance > best)
                        best = distance;
                }
                extentByLayer[layer] = best;
            }

            double length = refCurve.GetLength();
            for (int i = 0; i < orientedLayers.Count && i < normalizedParameters.Count; i++)
            {
                int layer = orientedLayers[i];
                if (!extentByLayer.TryGetValue(layer, out double layerExtent))
                    continue;

                double t = WasperFrameSequence.CurveParameterAtNormalized(
                    refCurve, normalizedParameters[i], length);
                double layerRadius = WasperFrameSequence.CurvatureRadiusAt(refCurve, t);

                if (layerExtent > layerRadius)
                {
                    extent = layerExtent;
                    radius = layerRadius;
                    return layer;
                }
            }

            return -1;
        }
    }
}
