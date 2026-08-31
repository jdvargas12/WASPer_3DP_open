// wsp_Pp22_WASPer Path Transform.cs
// WASPer_3DP - Subcategory: 4.0_Print Paths
//
// Applies ONE Transform (Move, Rotate, Scale, Mirror, Orient, or any composite
// built from native Grasshopper transform components) to a packed WASPer Print
// Path. Unlike Pp21 Orient Print Path, which maps each logical layer onto a
// generated frame with its own per-layer Transform.PlaneToPlane, Pp22 applies a
// single global Transform uniformly, so it has no notion of layers or a
// reference curve.
//
// TARGET_ROLES: only branches matching target_roles are moved; non-target
// branches and their point/vector fields pass through untouched, exactly as
// in Pp06 Align Path Planes.
//
// ALWAYS TRANSFORMED on targeted branches: pt_planes, the exact Pp01 source
// curves (duplicated, transformed, role-tagged via WasperPathRoleMetadata),
// and the Pr01 geometry fields support_pts/support_vects (points fully,
// vectors by the linear part only). These are raw geometry and transform
// correctly under any invertible map.
//
// la_planes describe one authoritative frame per LOGICAL LAYER, not per role
// branch. Moving only some role branches within a layer would leave that
// frame ambiguous, so la_planes are only transported when target_roles is
// "All paths" (0); a role-restricted transform leaves la_planes untouched and
// reports this.
//
// SIMILARITY DETECTION: the incoming xform is classified as a similarity
// transform (rigid, or rigid + uniform scale, including mirrors) by checking
// that its 3x3 linear part has no perspective row and maps the three world
// axes to three equal-length, mutually orthogonal vectors. Under a similarity
// transform, every physical ratio/angle the downstream analysis depends on is
// preserved, so the Pr01 scalar outputs (print_loc, print_glob, angles,
// contact_widths), Pr03 (risk, load, capacity), Pr04 (beam deflection,
// failure state, span data), and the Gc04 motion-plan/KPI block are kept
// unchanged. Shear or non-uniform scale invalidate those physical quantities,
// so they are cleared and reported in the summary as "cleared=Pr01,Pr03,...",
// the same idiom used by Pp21.
//
// flows, layer_h, layer_w, layer_wf, print_vol, print_speed, path roles, and
// stroke ids are always preserved unchanged: recomputing bead geometry for an
// arbitrary transform is out of scope for a generic transform component
// (unlike Pp21, which has an explicit reference curve to reason about the new
// layer direction). If xform is not a similarity transform, a runtime warning
// notes that these length-based fields may no longer be physically accurate.

#region Usings
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

using WASPer_3DP;
#endregion

namespace WASPer_3DP_Components._4_0_Print_Paths
{
    public sealed class wsp_Pp22_WASPer_Path_Transform : GH_Component
    {
        private const string SummaryDescription =
            "Transform kind, target_roles, transformed/untouched/failed counts, and cleared-data report.";

        private readonly string _versionTag;

        public wsp_Pp22_WASPer_Path_Transform()
            : base(
                "wsp_Pp22_WASPer Path Transform",
                "PathXform",
                "Applies one Transform to a packed WASPer Print Path. Feed xform from any native " +
                "Grasshopper transform component (Move, Rotate, Scale, Mirror, Orient...); the same " +
                "transform is applied uniformly to the whole path, with no per-layer variation.\n" +
                "target_roles selects which semantic branches are moved; non-target branches and their " +
                "point/vector data pass through unchanged.\n" +
                "pt_planes, exact Pp01 source curves, and the Pr01 support_pts/support_vects geometry " +
                "are always transformed on targeted branches, since raw points and vectors transform " +
                "correctly under any invertible map.\n" +
                "la_planes describe one shared frame per logical layer rather than per role, so they are " +
                "only transported when target_roles is All paths (0); a role-restricted transform leaves " +
                "la_planes untouched and reports it.\n" +
                "xform is classified as a similarity transform (rigid, or rigid plus uniform scale) by " +
                "checking that it has no perspective component and maps the world axes to equal-length, " +
                "mutually orthogonal vectors. Similarity transforms preserve every physical ratio the " +
                "downstream analysis depends on, so Pr01 (print_loc, print_glob, angles, contact_widths), " +
                "Pr03 (risk, load, capacity), Pr04 (beam deflection, failure state, span data), and the " +
                "Gc04 motion/KPI block are kept. Shear or non-uniform scale invalidate those quantities, " +
                "so they are cleared and reported as cleared=... in the summary.\n" +
                "flows, layer_h, layer_w, layer_wf, print_vol, print_speed, path roles, and stroke ids " +
                "are always preserved unchanged; a runtime warning flags when a non-similarity transform " +
                "may have made the length-based fields inaccurate.\n\n" +
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
            new Guid("7D3F2A18-6C4B-4E19-9A2D-5F8B3C1E9A47");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon => PathTransformIcon.Bitmap;

        // ---------------------------------------------------------------------
        //  Persistent "Show all outputs" layout
        // ---------------------------------------------------------------------

        private int _visibleOutputsMask;
        private const string ShowAllOutputsKey = "wsp_pp22_show_all_outputs";
        private const string VisibleOutputsMaskKey = "wsp_pp22_visible_outputs_mask";

        private static readonly string[] OutputCatalog = WasperPathDebugOutputs.CoreNickNames
            .Concat(new[] { "xform" })
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
                        registerExtras: RegisterPathTransformDebugOutputs);
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
                registerExtras: RegisterPathTransformDebugOutputs);
            return base.Read(reader);
        }

        private static void RegisterPathTransformDebugOutputs(GH_Component component, Func<string, bool> isVisible)
        {
            if (isVisible("xform"))
                component.Params.RegisterOutputParam(new Param_Transform
                {
                    Name = "transform",
                    NickName = "xform",
                    Description = "The transform applied to targeted branches, echoed back for convenience/chaining.",
                    Access = GH_ParamAccess.item
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
                "WASPer Print Path to transform.\n" +
                "Please use the Pp01 WASPer Path from Curves before using this component.",
                GH_ParamAccess.item);

            p.AddTransformParameter(
                "transform",
                "xform",
                "Transform applied uniformly to the targeted branches of wsp_path. Feed this from any " +
                "native Grasshopper transform component (Move, Rotate, Scale, Mirror, Orient...).\n" +
                "There is no per-layer variation: the same transform is used everywhere it applies.",
                GH_ParamAccess.item);

            p.AddParameter(WasperTargetRolesParam.Create(
                "Selects which semantic path branches are transformed. 0 = All paths (default), " +
                "1 = Shell, 2 = Infill, 3 = Partition, 4 = Support, 5 = Transition, 6 = Undefined. " +
                "Supply several role-specific values to include them and exclude the others. All paths " +
                "(0) cannot be combined. Non-target branches pass through unchanged."));
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "Transformed WASPer Print Path. pt_planes, source curves, and support_pts/support_vects " +
                "on targeted branches follow xform; non-target branches are unchanged. Analysis and KPI " +
                "fields are preserved when xform is a similarity transform, otherwise cleared.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "summary",
                "summary",
                SummaryDescription,
                GH_ParamAccess.item);

            // Optional debug outputs are added dynamically by Read()/the menu callback, based on the
            // persisted _visibleOutputsMask. RegisterOutputParams() runs once at construction, before
            // Read() has restored any persisted state, so a mask-gated branch here would never fire.
        }

        // ---------------------------------------------------------------------
        //  Solve
        // ---------------------------------------------------------------------

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            if (!WasperGcodeTreeUtil.TryGetPrintPath(DA, 0, out WasperPrintPath packedPath) ||
                packedPath == null ||
                !packedPath.HasPlanes)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Pp22 requires a valid wsp_path input. Please use the Pp01 WASPer Path from Curves " +
                    "before using this component.");
                return;
            }

            Transform xform = Transform.Identity;
            if (!DA.GetData(1, ref xform))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Provide a valid xform. Feed it from any native Grasshopper transform component " +
                    "(Move, Rotate, Scale, Mirror, Orient, etc.).");
                return;
            }

            double tol = Math.Max(RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001, 1e-9);

            double determinant = ApproximateDeterminant(xform);
            if (!double.IsFinite(determinant) || Math.Abs(determinant) <= tol * tol * tol)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "xform is degenerate (collapses the path to zero volume) and cannot be applied.");
                return;
            }

            var targetRolesRaw = new List<int>();
            DA.GetDataList(2, targetRolesRaw);
            if (!WasperGcodeTreeUtil.TryNormalizeTargetRoles(
                    targetRolesRaw,
                    out List<int> targetRoles,
                    out string roleError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, roleError);
                return;
            }

            bool isSimilarity = IsSimilarityTransform(xform, tol);
            double scale = ApproximateScale(xform);

            // -----------------------------------------------------------------
            //  Geometry: always transformed on targeted branches
            // -----------------------------------------------------------------

            DataTree<Plane> outPlanes = TransformPlanes(
                packedPath.PtPlanes, packedPath.PathRoles, targetRoles, xform,
                out int transformedPlanes, out int untouchedPlanes, out int failedPlanes);

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

                    bool targeted = WasperGcodeTreeUtil.MatchesTargetRoles(
                        packedPath.PathRoles, path, targetRoles);

                    for (int i = 0; i < branch.Count; i++)
                    {
                        Curve source = branch[i];
                        if (source == null)
                            continue;

                        if (!targeted)
                        {
                            outSourceCurves.Add(source, path);
                            continue;
                        }

                        Curve duplicate = source.DuplicateCurve();
                        if (duplicate == null)
                        {
                            outSourceCurves.Add(source, path);
                            continue;
                        }

                        if (duplicate.Transform(xform))
                            transformedCurves++;

                        // Transforming does not change what a curve semantically is, so the
                        // shared WASPer.PathRole tag is carried over explicitly rather than
                        // trusting DuplicateCurve/Transform to preserve user data.
                        WasperPathRoleMetadata.Copy(source, duplicate);
                        outSourceCurves.Add(duplicate, path);
                    }
                }
            }

            DataTree<Point3d> outSupportPts = TransformPoints(
                packedPath.SupportPts, packedPath.PathRoles, targetRoles, xform);
            DataTree<Vector3d> outSupportVects = TransformVectors(
                packedPath.SupportVects, packedPath.PathRoles, targetRoles, xform);

            // la_planes describe the whole layer, not any one role, so they are only
            // transported when every branch in the path is targeted.
            DataTree<Plane> outLayerPlanes = packedPath.LayerPlanes;
            bool layerPlanesTransformed = false;
            if (packedPath.HasLayerPlanes && WasperGcodeTreeUtil.TargetsAllRoles(targetRoles))
            {
                var movedLayerPlanes = new DataTree<Plane>();
                for (int b = 0; b < packedPath.LayerPlanes.BranchCount; b++)
                {
                    GH_Path path = packedPath.LayerPlanes.Paths[b];
                    IList<Plane> branch = packedPath.LayerPlanes.Branches[b];
                    if (branch == null || branch.Count == 0)
                        continue;
                    Plane stored = branch.FirstOrDefault(plane => plane.IsValid);
                    if (!stored.IsValid)
                        continue;
                    Plane moved = stored;
                    movedLayerPlanes.Add(moved.Transform(xform) ? moved : stored, path);
                }
                outLayerPlanes = movedLayerPlanes;
                layerPlanesTransformed = true;
            }

            // -----------------------------------------------------------------
            //  Derived analysis / KPI groups: preserved when xform is a rigid or
            //  uniform-scale similarity, cleared otherwise (shear / non-uniform
            //  scale invalidate the physical quantities they encode).
            // -----------------------------------------------------------------

            string clearedGroups;
            DataTree<double> printLoc; DataTree<bool> printGlob;
            DataTree<double> angles; DataTree<double> contactWidths;
            DataTree<double> riskMaterial; DataTree<double> riskComb;
            DataTree<double> load; DataTree<double> capacity;
            DataTree<double> dRatio; DataTree<double> dLoaded; DataTree<double> bendRatio;
            DataTree<int> spanClass; DataTree<double> spanLen;
            DataTree<bool> collapsed; DataTree<bool> cascade; DataTree<int> collapseGen;
            DataTree<bool> torn; DataTree<double> interfaceRatio; DataTree<double> overturnRatio;
            DataTree<int> failureFlags;
            WasperMotionPlan motionPlan;
            int? kpiUnits; DataTree<double> kpiSegmentLength; DataTree<double> kpiPrintSpeed;
            DataTree<double> kpiPrintVol; double? kpiTimeMin; double? kpiPathLength;
            double? kpiVolume; double? kpiMassKg; int? kpiLayers;

            if (isSimilarity)
            {
                clearedGroups = "none";
                printLoc = packedPath.PrintLoc; printGlob = packedPath.PrintGlob;
                angles = packedPath.Angles; contactWidths = packedPath.ContactWidths;
                riskMaterial = packedPath.RiskMaterial; riskComb = packedPath.RiskComb;
                load = packedPath.Load; capacity = packedPath.Capacity;
                dRatio = packedPath.DRatio; dLoaded = packedPath.DLoaded; bendRatio = packedPath.BendRatio;
                spanClass = packedPath.SpanClass; spanLen = packedPath.SpanLen;
                collapsed = packedPath.Collapsed; cascade = packedPath.Cascade; collapseGen = packedPath.CollapseGen;
                torn = packedPath.Torn; interfaceRatio = packedPath.InterfaceRatio; overturnRatio = packedPath.OverturnRatio;
                failureFlags = packedPath.FailureFlags;
                motionPlan = packedPath.MotionPlan;
                kpiUnits = packedPath.KpiUnits; kpiSegmentLength = packedPath.KpiSegmentLength;
                kpiPrintSpeed = packedPath.KpiPrintSpeed; kpiPrintVol = packedPath.KpiPrintVol;
                kpiTimeMin = packedPath.KpiTimeMin; kpiPathLength = packedPath.KpiPathLength;
                kpiVolume = packedPath.KpiVolume; kpiMassKg = packedPath.KpiMassKg; kpiLayers = packedPath.KpiLayers;
            }
            else
            {
                clearedGroups = DescribeClearedGroups(packedPath);
                printLoc = null; printGlob = null;
                angles = null; contactWidths = null;
                riskMaterial = null; riskComb = null;
                load = null; capacity = null;
                dRatio = null; dLoaded = null; bendRatio = null;
                spanClass = null; spanLen = null;
                collapsed = null; cascade = null; collapseGen = null;
                torn = null; interfaceRatio = null; overturnRatio = null;
                failureFlags = null;
                motionPlan = null;
                kpiUnits = null; kpiSegmentLength = null; kpiPrintSpeed = null; kpiPrintVol = null;
                kpiTimeMin = null; kpiPathLength = null; kpiVolume = null; kpiMassKg = null; kpiLayers = null;
            }

            // -----------------------------------------------------------------
            //  Repack
            // -----------------------------------------------------------------

            var outPath = new WasperPrintPath(
                points: null,
                ptPlanes: outPlanes,
                flows: packedPath.Flows,
                layerH: packedPath.LayerH,
                printSpeed: packedPath.PrintSpeed,
                printLoc: printLoc,
                printGlob: printGlob,
                supportPts: outSupportPts,
                supportVects: outSupportVects,
                angles: angles,
                contactWidths: contactWidths,
                riskMaterial: riskMaterial,
                riskComb: riskComb,
                load: load,
                capacity: capacity,
                nozzleDiam: packedPath.NozzleDiam,
                dRatio: dRatio,
                dLoaded: dLoaded,
                bendRatio: bendRatio,
                spanClass: spanClass,
                spanLen: spanLen,
                collapsed: collapsed,
                cascade: cascade,
                collapseGen: collapseGen,
                layerW: packedPath.LayerW,
                layerWf: packedPath.LayerWf,
                printVol: packedPath.PrintVol,
                torn: torn,
                interfaceRatio: interfaceRatio,
                overturnRatio: overturnRatio,
                failureFlags: failureFlags,
                travelSpeed: packedPath.TravelSpeed,
                zHop: packedPath.ZHop,
                zHopSpeed: packedPath.ZHopSpeed,
                motionPlan: motionPlan,
                kpiUnits: kpiUnits,
                kpiSegmentLength: kpiSegmentLength,
                kpiPrintSpeed: kpiPrintSpeed,
                kpiPrintVol: kpiPrintVol,
                kpiTimeMin: kpiTimeMin,
                kpiPathLength: kpiPathLength,
                kpiVolume: kpiVolume,
                kpiLayers: kpiLayers,
                isPartial: packedPath.IsPartial || !isSimilarity,
                sourceCurves: outSourceCurves,
                pathRoles: packedPath.PathRoles,
                layerPlanes: outLayerPlanes,
                strokeIds: packedPath.StrokeIds,
                hasCrossLayerShellContinuity: packedPath.HasCrossLayerShellContinuity,
                kpiMassKg: kpiMassKg);

            // -----------------------------------------------------------------
            //  Diagnostics
            // -----------------------------------------------------------------

            if (!isSimilarity)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "xform includes shear or non-uniform scale, so it is not a similarity transform. " +
                    $"Cleared groups: {clearedGroups}. Recompute them downstream after this component. " +
                    "layer_h/layer_w/layer_wf/print_vol were kept as-is and may no longer be accurate.");
            }

            if (packedPath.HasLayerPlanes && !layerPlanesTransformed &&
                !WasperGcodeTreeUtil.TargetsAllRoles(targetRoles))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "la_planes describe the whole layer and were left untouched because target_roles is " +
                    "restricted to specific roles. Use All paths (0) to transform la_planes too.");
            }

            if (failedPlanes > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{failedPlanes} pt_plane(s) could not be transformed and were left in their original position.");
            }

            string transformKind = isSimilarity
                ? (Math.Abs(scale - 1.0) <= Math.Max(tol, scale * 1e-4) ? "rigid" : $"uniform scale x{scale:0.###}")
                : "non-similarity (shear/non-uniform scale)";

            var summary = new StringBuilder();
            summary.Append("OK | xform=").Append(transformKind);
            summary.Append(" | target_roles=").Append(WasperGcodeTreeUtil.TargetRoleNames(targetRoles));
            summary.Append(" | planes transformed=").Append(transformedPlanes)
                   .Append(", untouched=").Append(untouchedPlanes)
                   .Append(", failed=").Append(failedPlanes);
            if (packedPath.HasSourceCurves)
                summary.Append(" | curves transformed=").Append(transformedCurves);
            if (outSupportPts != null)
                summary.Append(" | support_pts transformed");
            if (outSupportVects != null)
                summary.Append(" | support_vects transformed");
            summary.Append(" | la_planes=").Append(
                !packedPath.HasLayerPlanes ? "none" : layerPlanesTransformed ? "transformed" : "unchanged");
            summary.Append(" | cleared=").Append(clearedGroups);

            WasperPathDebugOutputs.Set(DA, this, outPath, summary.ToString());

            int xformIndex = WasperPathDebugOutputs.OutputIndex(this, "xform");
            if (xformIndex >= 0)
                DA.SetData(xformIndex, xform);
        }

        // ---------------------------------------------------------------------
        //  Helpers
        // ---------------------------------------------------------------------

        private static DataTree<Plane> TransformPlanes(
            DataTree<Plane> source,
            DataTree<int> pathRoles,
            IList<int> targetRoles,
            Transform xform,
            out int transformedCount,
            out int untouchedCount,
            out int failedCount)
        {
            transformedCount = 0;
            untouchedCount = 0;
            failedCount = 0;
            if (source == null)
                return null;

            var output = new DataTree<Plane>();
            for (int b = 0; b < source.BranchCount; b++)
            {
                GH_Path path = source.Paths[b];
                output.EnsurePath(path);
                IList<Plane> branch = source.Branches[b];
                if (branch == null)
                    continue;

                bool targeted = WasperGcodeTreeUtil.MatchesTargetRoles(pathRoles, path, targetRoles);

                for (int i = 0; i < branch.Count; i++)
                {
                    Plane original = branch[i];
                    if (!targeted || !original.IsValid)
                    {
                        output.Add(original, path);
                        untouchedCount++;
                        continue;
                    }

                    Plane moved = original;
                    if (!moved.Transform(xform))
                    {
                        output.Add(original, path);
                        failedCount++;
                        continue;
                    }

                    output.Add(moved, path);
                    transformedCount++;
                }
            }
            return output;
        }

        private static DataTree<Point3d> TransformPoints(
            DataTree<Point3d> source,
            DataTree<int> pathRoles,
            IList<int> targetRoles,
            Transform xform)
        {
            if (source == null)
                return null;

            var output = new DataTree<Point3d>();
            for (int b = 0; b < source.BranchCount; b++)
            {
                GH_Path path = source.Paths[b];
                output.EnsurePath(path);
                IList<Point3d> branch = source.Branches[b];
                if (branch == null)
                    continue;

                bool targeted = WasperGcodeTreeUtil.MatchesTargetRoles(pathRoles, path, targetRoles);
                for (int i = 0; i < branch.Count; i++)
                {
                    Point3d point = branch[i];
                    if (!targeted || !point.IsValid)
                    {
                        output.Add(point, path);
                        continue;
                    }

                    Point3d moved = point;
                    moved.Transform(xform);
                    output.Add(moved, path);
                }
            }
            return output.BranchCount > 0 ? output : null;
        }

        private static DataTree<Vector3d> TransformVectors(
            DataTree<Vector3d> source,
            DataTree<int> pathRoles,
            IList<int> targetRoles,
            Transform xform)
        {
            if (source == null)
                return null;

            var output = new DataTree<Vector3d>();
            for (int b = 0; b < source.BranchCount; b++)
            {
                GH_Path path = source.Paths[b];
                output.EnsurePath(path);
                IList<Vector3d> branch = source.Branches[b];
                if (branch == null)
                    continue;

                bool targeted = WasperGcodeTreeUtil.MatchesTargetRoles(pathRoles, path, targetRoles);
                for (int i = 0; i < branch.Count; i++)
                {
                    Vector3d vector = branch[i];
                    if (!targeted || !vector.IsValid)
                    {
                        output.Add(vector, path);
                        continue;
                    }

                    Vector3d moved = vector;
                    // Vector3d.Transform applies only the linear (rotation/scale) part of
                    // xform, ignoring translation, which is the correct behavior for a
                    // direction rather than a location.
                    moved.Transform(xform);
                    output.Add(moved, path);
                }
            }
            return output.BranchCount > 0 ? output : null;
        }

        /// <summary>
        /// A similarity transform (rigid, or rigid plus uniform scale, including
        /// mirrors) maps the three world axes to three equal-length, mutually
        /// orthogonal vectors and carries no perspective component. There is no
        /// existing codebase precedent for this check, so it is implemented from
        /// the raw 3x3 linear part of xform rather than relying on an unverified
        /// RhinoCommon convenience API.
        /// </summary>
        private static bool IsSimilarityTransform(Transform xform, double tolerance)
        {
            if (Math.Abs(xform.M30) > tolerance ||
                Math.Abs(xform.M31) > tolerance ||
                Math.Abs(xform.M32) > tolerance ||
                Math.Abs(xform.M33 - 1.0) > tolerance)
                return false;

            var colX = new Vector3d(xform.M00, xform.M10, xform.M20);
            var colY = new Vector3d(xform.M01, xform.M11, xform.M21);
            var colZ = new Vector3d(xform.M02, xform.M12, xform.M22);

            double lenX = colX.Length;
            double lenY = colY.Length;
            double lenZ = colZ.Length;
            if (lenX < tolerance || lenY < tolerance || lenZ < tolerance)
                return false;

            double avgLen = (lenX + lenY + lenZ) / 3.0;
            double lengthTol = Math.Max(avgLen * 1e-4, tolerance);
            if (Math.Abs(lenX - lenY) > lengthTol ||
                Math.Abs(lenX - lenZ) > lengthTol ||
                Math.Abs(lenY - lenZ) > lengthTol)
                return false;

            double orthoTol = avgLen * avgLen * 1e-4;
            if (Math.Abs(colX * colY) > orthoTol ||
                Math.Abs(colX * colZ) > orthoTol ||
                Math.Abs(colY * colZ) > orthoTol)
                return false;

            return true;
        }

        private static double ApproximateScale(Transform xform)
        {
            var colX = new Vector3d(xform.M00, xform.M10, xform.M20);
            var colY = new Vector3d(xform.M01, xform.M11, xform.M21);
            var colZ = new Vector3d(xform.M02, xform.M12, xform.M22);
            return (colX.Length + colY.Length + colZ.Length) / 3.0;
        }

        private static double ApproximateDeterminant(Transform xform)
        {
            var colX = new Vector3d(xform.M00, xform.M10, xform.M20);
            var colY = new Vector3d(xform.M01, xform.M11, xform.M21);
            var colZ = new Vector3d(xform.M02, xform.M12, xform.M22);
            return Vector3d.CrossProduct(colX, colY) * colZ;
        }

        private static string DescribeClearedGroups(WasperPrintPath path)
        {
            var groups = new List<string>();

            if (path.HasPrintAssessment || path.Angles != null || path.ContactWidths != null)
                groups.Add("Pr01");

            if (path.HasFreshRisk || path.RiskMaterial != null || path.Load != null || path.Capacity != null)
                groups.Add("Pr03");

            if (path.HasBeamDeflection || path.HasFailureState ||
                path.SpanClass != null || path.SpanLen != null || path.BendRatio != null ||
                path.InterfaceRatio != null || path.OverturnRatio != null)
                groups.Add("Pr04");

            if (path.HasMotionPlan || path.HasProcessKpis || path.HasJobKpis || path.KpiUnits.HasValue)
                groups.Add("Gc04");

            return groups.Count == 0 ? "none" : string.Join(",", groups);
        }

        private static class PathTransformIcon
        {
            private static readonly Lazy<Bitmap> Cached = new Lazy<Bitmap>(Create, true);

            public static Bitmap Bitmap => Cached.Value;

            private static Bitmap Create()
            {
                var bitmap = new Bitmap(24, 24);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.Clear(Color.Transparent);
                        
                    using (var originPen = new Pen(Color.FromArgb(91, 166, 176), 1.4f)
                    {
                        DashStyle = DashStyle.Dash
                    })
                    using (var pathPen = new Pen(Color.FromArgb(234, 124, 42), 2.2f))
                    using (var arrowPen = new Pen(Color.FromArgb(35, 72, 86), 1.6f))
                    using (var arrowBrush = new SolidBrush(Color.FromArgb(35, 72, 86)))
                    {
                        // Original path, small, lower-left.
                        graphics.DrawBezier(originPen, 2, 21, 5, 15, 9, 20, 12, 15);

                        // Transformed path, larger, upper-right.
                        graphics.DrawBezier(pathPen, 10, 10, 14, 2, 19, 9, 22, 3);

                        // Arrow indicating the applied transform.
                        graphics.DrawLine(arrowPen, 9, 14, 15, 8);
                        graphics.FillPolygon(arrowBrush, new[]
                        {
                            new PointF(15, 8),
                            new PointF(11.5f, 8.7f),
                            new PointF(14.3f, 11.5f)
                        });
                    }
                }
                return bitmap;
            }
        }
    }
}
