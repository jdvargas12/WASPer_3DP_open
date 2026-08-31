// -----------------------------------------------------------------------------
//  WASPer_GcodeTypes.cs — shared data types for the 5.0_Gcode pipeline
// -----------------------------------------------------------------------------
//  WasperFlowParams  : packs the flow assignment strategy used by Gc01_v2.
//                      Produced by Gc00_Define Flow; evaluated by Gc01_v2 after
//                      curves have been subdivided into path-plane locations.
//  WasperPrintPath   : packs canonical pt_planes / flows / layer_h trees so the
//                      whole printing-path state can travel down a single wire.
//                      Points is a compatibility projection of plane origins.
//                      Produced by Gc01 (wsp_path output); consumed by the Marlin
//                      G-code generator (and future Gc components).
//  Wasper3dpParams   : process-aware packed printing parameters (3dp_params wire).
//                      Produced by wsp_Gc02_LDM 3DP Parameters (Process = LDM) and
//                      wsp_Gc03_FDM 3DP Parameters (Process = FDM); consumed by the
//                      single Marlin G-code generator, which dispatches its LDM or
//                      FDM code path on the Process field. Common fields plus
//                      LDM-only (split, time correction) and FDM-only (fan, temps,
//                      custom start/end blocks) fields; unused fields stay null.
//
//  Both types follow the shared WasperField / WasperLayer pattern:
//  plain data class + GH_Goo wrapper in the WASPer_3DP namespace.
//  Every parameter field is nullable: only values the user explicitly set travel
//  with the object, so the generator applies field > process default without
//  ambiguity (the generator itself has no scalar parameter inputs).
//
//  DataTree contents are not serialised (recomputed on every solve), matching
//  WasperFieldGoo behaviour.
// -----------------------------------------------------------------------------

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino.Geometry;

namespace WASPer_3DP
{
    // =========================================================================
    //  WasperFlowParams
    // =========================================================================

    /// <summary>
    /// Flow assignment strategy evaluated by Gc01 after it samples path curves.
    /// Mode 1 stores global/per-layer multipliers. Mode 2 stores a normalized
    /// profile along each path curve. Mode 3 stores the same profile sampled
    /// along a reference line/curve projection. TargetRoles restricts evaluation
    /// to selected semantic path roles; [0] means All paths.
    /// </summary>
    public sealed class WasperFlowParams
    {
        public int Mode { get; }
        public List<double> Mode1Flow { get; }
        public List<double> Profile { get; }
        public Curve ReferenceCurve { get; }
        public bool ReverseReference { get; }
        public List<int> TargetRoles { get; }

        public WasperFlowParams(
            int mode,
            IEnumerable<double> mode1Flow = null,
            IEnumerable<double> profile = null,
            Curve referenceCurve = null,
            bool reverseReference = false,
            IEnumerable<int> targetRoles = null)
        {
            Mode = mode;
            Mode1Flow = CleanList(mode1Flow, 1.0);
            Profile = CleanList(profile, 1.0);
            ReferenceCurve = referenceCurve?.DuplicateCurve();
            ReverseReference = reverseReference;
            TargetRoles = CleanRoles(targetRoles);
        }

        public static WasperFlowParams Default => new WasperFlowParams(1, new[] { 1.0 });

        public WasperFlowParams Duplicate()
        {
            return new WasperFlowParams(
                Mode,
                Mode1Flow,
                Profile,
                ReferenceCurve,
                ReverseReference,
                TargetRoles);
        }

        public override string ToString()
        {
            if (Mode == 1)
                return Mode1Flow.Count == 1
                    ? string.Format(CultureInfo.InvariantCulture, "WASPer Flow Params: mode 1, global flow={0:0.###}, roles={1}", Mode1Flow[0], WasperGcodeTreeUtil.TargetRoleNames(TargetRoles))
                    : $"WASPer Flow Params: mode 1, {Mode1Flow.Count} layer flow values, roles={WasperGcodeTreeUtil.TargetRoleNames(TargetRoles)}";

            if (Mode == 2)
                return $"WASPer Flow Params: mode 2, profile ({Profile.Count} value{(Profile.Count == 1 ? "" : "s")}), roles={WasperGcodeTreeUtil.TargetRoleNames(TargetRoles)}";

            return $"WASPer Flow Params: mode 3, profile ({Profile.Count} value{(Profile.Count == 1 ? "" : "s")}), ref_crv={(ReferenceCurve != null && ReferenceCurve.IsValid ? "set" : "missing")}, roles={WasperGcodeTreeUtil.TargetRoleNames(TargetRoles)}";
        }

        private static List<double> CleanList(IEnumerable<double> values, double fallback)
        {
            var result = new List<double>();
            if (values != null)
            {
                foreach (double value in values)
                {
                    if (double.IsFinite(value))
                        result.Add(value);
                }
            }

            if (result.Count == 0)
                result.Add(fallback);

            return result;
        }

        private static List<int> CleanRoles(IEnumerable<int> values)
        {
            if (WasperGcodeTreeUtil.TryNormalizeTargetRoles(
                    values,
                    out List<int> roles,
                    out _))
                return roles;
            return new List<int> { 0 };
        }
    }

    /// <summary>Grasshopper wrapper used to wire flow strategy between Gc00 and Gc01_v2.</summary>
    public sealed class WasperFlowParamsGoo : GH_Goo<WasperFlowParams>
    {
        public WasperFlowParamsGoo() : base((WasperFlowParams)null) { }
        public WasperFlowParamsGoo(WasperFlowParams p) : base(p) { }

        public override bool IsValid => Value != null;
        public override string TypeName => "WASPer Flow Params";
        public override string TypeDescription =>
            "Packed flow assignment strategy for Gc01_v2. Stores mode/profile and target-role data; final per-point flows are evaluated after path subdivision.";
        public override IGH_Goo Duplicate() => new WasperFlowParamsGoo(Value?.Duplicate());
        public override string ToString() => Value?.ToString() ?? "Null WASPer Flow Params";

        // Not serialised — recomputed on every solve.
        public override bool Write(GH_IWriter writer) => true;
        public override bool Read(GH_IReader reader) => true;

        public override bool CastTo<T>(ref T target)
        {
            if (typeof(T) == typeof(WasperFlowParams) && Value != null)
            {
                target = (T)(object)Value;
                return true;
            }
            return base.CastTo(ref target);
        }

        public override bool CastFrom(object source)
        {
            switch (source)
            {
                case WasperFlowParams p: Value = p; return true;
                case WasperFlowParamsGoo g: Value = g.Value; return true;
                case GH_ObjectWrapper w when w.Value is WasperFlowParams fp:
                    Value = fp; return true;
                default: return false;
            }
        }
    }

    // =========================================================================
    //  WasperPrintPath
    // =========================================================================

    /// <summary>
    /// A packed printing path whose canonical geometry is the point-plane tree
    /// {layer;curve}. Points remains available as a compatibility projection of
    /// PtPlanes origins, so geometry cannot diverge between two stored trees.
    /// Legacy point-only construction is accepted and converted to World-XY
    /// planes at those points.
    /// </summary>
    public sealed class WasperPrintPath
    {
        public DataTree<Point3d> Points   { get; } // compatibility projection of PtPlanes origins; never an independent geometry source
        public DataTree<Plane>   PtPlanes { get; }
        // Optional authoritative reference frame per logical layer. Canonical
        // branches end at the logical-layer dimension and contain one Plane.
        // This stage stores supplied planes only; the contract deliberately
        // leaves room for future fitted/reconstructed plane provenance.
        public DataTree<Plane>   LayerPlanes { get; }
        public DataTree<Curve>   SourceCurves { get; } // optional exact source curve per branch, emitted by Gc01 for lossless downstream resampling
        public DataTree<int>     PathRoles { get; } // optional semantic role per branch: 0 undefined, 1 shell, 2 infill, 3 partition, 4 support, 5 transition
        public DataTree<int>     StrokeIds { get; } // optional branch-aligned continuity group; consecutive branches sharing a non-negative id form one extrusion stroke
        public DataTree<double>  Flows    { get; }
        public DataTree<double>  LayerH   { get; }
        public DataTree<double>  PrintSpeed { get; } // optional, generated by Gc08
        public DataTree<double>  PrintLoc { get; } // optional, generated by Pr01
        public DataTree<bool>    PrintGlob { get; } // optional, generated by Pr01
        public DataTree<Point3d> SupportPts { get; } // optional, generated by Pr01
        public DataTree<Vector3d> SupportVects { get; } // optional, generated by Pr01
        public DataTree<double>  Angles { get; } // optional, generated by Pr01
        public DataTree<double>  ContactWidths { get; } // optional, generated by Pr01
        public DataTree<double>  RiskMaterial { get; } // optional, generated by Pr03
        public DataTree<double>  RiskComb { get; } // optional, generated by Pr03
        public DataTree<double>  Load { get; } // optional [N], generated by Pr03
        public DataTree<double>  Capacity { get; } // optional [N], generated by Pr03
        public double? NozzleDiam { get; } // optional [mm], set by Pr01 when contact widths use the Alhussain et al. (2024) model baseline instead of the geometric one
        public DataTree<double>  DRatio { get; } // optional, generated by Pr04: span deflection / layer height
        public DataTree<double>  DLoaded { get; } // optional, generated by Pr04: deflection ratio including Pr03 accumulated load
        public DataTree<double>  BendRatio { get; } // optional, generated by Pr04: bending stress / tau_y-derived flexural strength
        public DataTree<int>     SpanClass { get; } // optional, generated by Pr04: 0 supported, 1 bridge, 2 cantilever
        public DataTree<double>  SpanLen { get; } // optional [mm], generated by Pr04: unsupported span length
        public DataTree<bool>    Collapsed { get; } // optional, generated by Pr04
        public DataTree<bool>    Cascade { get; } // optional, generated by Pr04: collapse caused by failed support below
        public DataTree<int>     CollapseGen { get; } // optional, generated by Pr04: -1 stable, 0 direct collapse, 1+ cascade generation
        public DataTree<double>  LayerW { get; } // optional nominal/base bead width [model units]
        public DataTree<double>  LayerWf { get; } // optional flow-adjusted deposited width [model units]
        public DataTree<double>  PrintVol { get; } // optional per-segment deposited volume [mm3]
        public DataTree<bool>    Torn { get; } // optional, generated by Pr04: edge i -> i+1 is separated
        public DataTree<double>  InterfaceRatio { get; } // optional, generated by Pr04: supported demand / interface capacity
        public DataTree<double>  OverturnRatio { get; } // optional, generated by Pr04: eccentricity / no-tension kern limit
        public DataTree<int>     FailureFlags { get; } // optional Pr04 mechanism bit mask
        public double? TravelSpeed { get; } // optional resolved G0 feedrate [mm/min], generated by Gc04
        public double? ZHop { get; } // optional resolved positive-Z hop distance [mm], generated by Gc04
        public double? ZHopSpeed { get; } // optional resolved Z-hop feedrate [mm/min], generated by Gc04
        public WasperMotionPlan MotionPlan { get; } // optional ordered print/travel motion plan, generated by Gc04
        public int? KpiUnits { get; } // optional KPI unit code generated by Gc04: 0 mm, 1 cm, 2 m. Does not affect G-code.
        public DataTree<double> KpiSegmentLength { get; } // optional per-segment printed length in KpiUnits length units
        public DataTree<double> KpiPrintSpeed { get; } // optional per-point print speed in KpiUnits length units/min
        public DataTree<double> KpiPrintVol { get; } // optional per-segment deposited volume in KpiUnits volume units
        public double? KpiTimeMin { get; } // optional total estimated job time [min]
        public double? KpiPathLength { get; } // optional total printed path length in KpiUnits length units
        public double? KpiVolume { get; } // optional total deposited volume in KpiUnits volume units
        public double? KpiMassKg { get; } // optional total deposited mass [kg], available when Gc03 receives material density
        public int? KpiLayers { get; } // optional total layer count generated by Gc04
        public bool IsPartial { get; } // true when the path is a simulated/reconstructed subset or cannot carry full original metadata
        public bool HasCrossLayerShellContinuity { get; } // explicit geometry flag set by Pp10 when one or more Shell loops are ramped across logical layers
        public string ContentSignature { get; } // optional deterministic producer signature used for safe downstream cache reuse

        public WasperPrintPath(
            DataTree<Point3d> points,
            DataTree<Plane>   ptPlanes,
            DataTree<double>  flows,
            DataTree<double>  layerH,
            DataTree<double>  printSpeed = null,
            DataTree<double>  printLoc = null,
            DataTree<bool>    printGlob = null,
            DataTree<Point3d> supportPts = null,
            DataTree<Vector3d> supportVects = null,
            DataTree<double>  angles = null,
            DataTree<double>  contactWidths = null,
            DataTree<double>  riskMaterial = null,
            DataTree<double>  riskComb = null,
            DataTree<double>  load = null,
            DataTree<double>  capacity = null,
            double?           nozzleDiam = null,
            DataTree<double>  dRatio = null,
            DataTree<double>  dLoaded = null,
            DataTree<double>  bendRatio = null,
            DataTree<int>     spanClass = null,
            DataTree<double>  spanLen = null,
            DataTree<bool>    collapsed = null,
            DataTree<bool>    cascade = null,
            DataTree<int>     collapseGen = null,
            DataTree<double>  layerW = null,
            DataTree<double>  layerWf = null,
            DataTree<double>  printVol = null,
            DataTree<bool>    torn = null,
            DataTree<double>  interfaceRatio = null,
            DataTree<double>  overturnRatio = null,
            DataTree<int>     failureFlags = null,
            double?           travelSpeed = null,
            double?           zHop = null,
            double?           zHopSpeed = null,
            WasperMotionPlan   motionPlan = null,
            int?               kpiUnits = null,
            DataTree<double>   kpiSegmentLength = null,
            DataTree<double>   kpiPrintSpeed = null,
            DataTree<double>   kpiPrintVol = null,
            double?            kpiTimeMin = null,
            double?            kpiPathLength = null,
            double?            kpiVolume = null,
            int?               kpiLayers = null,
            bool               isPartial = false,
            DataTree<Curve>     sourceCurves = null,
            DataTree<int>       pathRoles = null,
            DataTree<Plane>     layerPlanes = null,
            DataTree<int>       strokeIds = null,
            bool                hasCrossLayerShellContinuity = false,
            double?             kpiMassKg = null,
            string              contentSignature = null)
        {
            PtPlanes = CanonicalPlanes(points, ptPlanes);
            Points = PointsFromPlanes(PtPlanes);
            LayerPlanes = CanonicalLayerPlanes(layerPlanes);
            SourceCurves = sourceCurves;
            PathRoles = pathRoles;
            StrokeIds = strokeIds;
            HasCrossLayerShellContinuity = hasCrossLayerShellContinuity;
            Flows = flows;
            LayerH = layerH;
            PrintSpeed = printSpeed;
            PrintLoc = printLoc;
            PrintGlob = printGlob;
            SupportPts = supportPts;
            SupportVects = supportVects;
            Angles = angles;
            ContactWidths = contactWidths;
            RiskMaterial = riskMaterial;
            RiskComb = riskComb;
            Load = load;
            Capacity = capacity;
            NozzleDiam = nozzleDiam;
            DRatio = dRatio;
            DLoaded = dLoaded;
            BendRatio = bendRatio;
            SpanClass = spanClass;
            SpanLen = spanLen;
            Collapsed = collapsed;
            Cascade = cascade;
            CollapseGen = collapseGen;
            LayerW = layerW;
            LayerWf = layerWf;
            PrintVol = printVol;
            Torn = torn;
            InterfaceRatio = interfaceRatio;
            OverturnRatio = overturnRatio;
            FailureFlags = failureFlags;
            TravelSpeed = travelSpeed;
            ZHop = zHop;
            ZHopSpeed = zHopSpeed;
            MotionPlan = motionPlan;
            KpiUnits = kpiUnits;
            KpiSegmentLength = kpiSegmentLength;
            KpiPrintSpeed = kpiPrintSpeed;
            KpiPrintVol = kpiPrintVol;
            KpiTimeMin = kpiTimeMin;
            KpiPathLength = kpiPathLength;
            KpiVolume = kpiVolume;
            KpiMassKg = kpiMassKg;
            KpiLayers = kpiLayers;
            IsPartial = isPartial;
            ContentSignature = string.IsNullOrWhiteSpace(contentSignature)
                ? null
                : contentSignature;
        }

        /// <summary>
        /// Returns the same immutable path payload with a replacement semantic-role
        /// tree. Geometry, process data, analysis, motion, KPI data, and partial
        /// state are preserved by reference because role assignment does not alter them.
        /// </summary>
        public WasperPrintPath WithPathRoles(DataTree<int> pathRoles)
        {
            return new WasperPrintPath(
                points: null,
                ptPlanes: PtPlanes,
                flows: Flows,
                layerH: LayerH,
                printSpeed: PrintSpeed,
                printLoc: PrintLoc,
                printGlob: PrintGlob,
                supportPts: SupportPts,
                supportVects: SupportVects,
                angles: Angles,
                contactWidths: ContactWidths,
                riskMaterial: RiskMaterial,
                riskComb: RiskComb,
                load: Load,
                capacity: Capacity,
                nozzleDiam: NozzleDiam,
                dRatio: DRatio,
                dLoaded: DLoaded,
                bendRatio: BendRatio,
                spanClass: SpanClass,
                spanLen: SpanLen,
                collapsed: Collapsed,
                cascade: Cascade,
                collapseGen: CollapseGen,
                layerW: LayerW,
                layerWf: LayerWf,
                printVol: PrintVol,
                torn: Torn,
                interfaceRatio: InterfaceRatio,
                overturnRatio: OverturnRatio,
                failureFlags: FailureFlags,
                travelSpeed: TravelSpeed,
                zHop: ZHop,
                zHopSpeed: ZHopSpeed,
                motionPlan: MotionPlan,
                kpiUnits: KpiUnits,
                kpiSegmentLength: KpiSegmentLength,
                kpiPrintSpeed: KpiPrintSpeed,
                kpiPrintVol: KpiPrintVol,
                kpiTimeMin: KpiTimeMin,
                kpiPathLength: KpiPathLength,
                kpiVolume: KpiVolume,
                kpiLayers: KpiLayers,
                isPartial: IsPartial,
                sourceCurves: SourceCurves,
                pathRoles: pathRoles,
                layerPlanes: LayerPlanes,
                strokeIds: StrokeIds,
                hasCrossLayerShellContinuity: HasCrossLayerShellContinuity,
                kpiMassKg: KpiMassKg);
        }

        private static DataTree<Plane> CanonicalLayerPlanes(DataTree<Plane> planes)
        {
            if (planes == null || planes.BranchCount == 0)
                return null;

            var canonical = new DataTree<Plane>();
            for (int b = 0; b < planes.BranchCount; b++)
            {
                GH_Path path = planes.Paths[b];
                IList<Plane> branch = planes.Branches[b];
                if (path == null || branch == null)
                    continue;
                Plane first = branch.FirstOrDefault(plane => plane.IsValid);
                if (first.IsValid)
                    canonical.Add(first, path);
            }
            return canonical.BranchCount > 0 ? canonical : null;
        }

        private static DataTree<Plane> CanonicalPlanes(
            DataTree<Point3d> legacyPoints,
            DataTree<Plane> planes)
        {
            if (planes != null && planes.BranchCount > 0 && planes.DataCount > 0)
            {
                var canonical = new DataTree<Plane>();
                for (int b = 0; b < planes.BranchCount; b++)
                {
                    GH_Path path = planes.Paths[b];
                    canonical.EnsurePath(path);
                    IList<Plane> branch = planes.Branches[b];
                    if (branch == null) continue;
                    for (int i = 0; i < branch.Count; i++)
                    {
                        Plane plane = branch[i];
                        if (plane.IsValid)
                            canonical.Add(plane, path);
                    }
                }
                return canonical;
            }

            if (legacyPoints == null)
                return null;

            var generated = new DataTree<Plane>();
            for (int b = 0; b < legacyPoints.BranchCount; b++)
            {
                GH_Path path = legacyPoints.Paths[b];
                generated.EnsurePath(path);
                IList<Point3d> branch = legacyPoints.Branches[b];
                if (branch == null) continue;
                for (int i = 0; i < branch.Count; i++)
                {
                    Point3d point = branch[i];
                    if (!point.IsValid) continue;
                    Plane plane = Plane.WorldXY;
                    plane.Origin = point;
                    generated.Add(plane, path);
                }
            }
            return generated;
        }

        private static DataTree<Point3d> PointsFromPlanes(DataTree<Plane> planes)
        {
            if (planes == null)
                return null;

            var points = new DataTree<Point3d>();
            for (int b = 0; b < planes.BranchCount; b++)
            {
                GH_Path path = planes.Paths[b];
                points.EnsurePath(path);
                IList<Plane> branch = planes.Branches[b];
                if (branch == null) continue;
                for (int i = 0; i < branch.Count; i++)
                {
                    Plane plane = branch[i];
                    if (plane.IsValid)
                        points.Add(plane.Origin, path);
                }
            }
            return points;
        }
        public bool HasPoints => Points   != null && Points.BranchCount   > 0;
        public bool HasPlanes => PtPlanes != null && PtPlanes.BranchCount > 0;
        public bool HasLayerPlanes => LayerPlanes != null && LayerPlanes.BranchCount > 0;
        public bool HasSourceCurves => SourceCurves != null && SourceCurves.BranchCount > 0;
        public bool HasPathRoles => PathRoles != null && PathRoles.BranchCount > 0;
        public bool HasStrokeIds => StrokeIds != null && StrokeIds.BranchCount > 0;
        public bool HasFlows  => Flows    != null && Flows.BranchCount    > 0;
        public bool HasLayerH => LayerH   != null && LayerH.BranchCount   > 0;
        public bool HasLayerW => LayerW != null && LayerW.BranchCount > 0;
        public bool HasLayerWf => LayerWf != null && LayerWf.BranchCount > 0;
        public bool HasPrintVol => PrintVol != null && PrintVol.BranchCount > 0;
        public bool HasPrintSpeed => PrintSpeed != null && PrintSpeed.BranchCount > 0;
        public bool HasPrintAssessment => PrintLoc != null && PrintGlob != null &&
            PrintLoc.BranchCount > 0 && PrintGlob.BranchCount > 0;
        public bool HasFreshRisk => RiskComb != null && RiskComb.BranchCount > 0;
        public bool HasBeamDeflection => DRatio != null && DRatio.BranchCount > 0;
        public bool HasFailureState =>
            (Torn != null && Torn.BranchCount > 0) ||
            (InterfaceRatio != null && InterfaceRatio.BranchCount > 0) ||
            (OverturnRatio != null && OverturnRatio.BranchCount > 0) ||
            (FailureFlags != null && FailureFlags.BranchCount > 0);
        public bool HasMotionPlan => MotionPlan != null && MotionPlan.Count > 0;
        public bool HasProcessKpis =>
            (KpiSegmentLength != null && KpiSegmentLength.BranchCount > 0) ||
            (KpiPrintSpeed != null && KpiPrintSpeed.BranchCount > 0) ||
            (KpiPrintVol != null && KpiPrintVol.BranchCount > 0);
        public bool HasJobKpis =>
            KpiTimeMin.HasValue || KpiPathLength.HasValue || KpiVolume.HasValue ||
            KpiMassKg.HasValue || KpiLayers.HasValue;

        public int PointCount  => Points?.DataCount   ?? 0;
        public int BranchCount => Points?.BranchCount ?? 0;
        public override string ToString()
        {
            if (!HasPoints) return IsPartial
                ? "WASPer Print Path (empty, partial)"
                : "WASPer Print Path (empty)";
            var extras = new List<string>();
            if (IsPartial) extras.Add("partial");
            if (HasPlanes) extras.Add("planes");
            if (HasLayerPlanes) extras.Add("layer_planes");
            if (HasSourceCurves) extras.Add("source_curves");
            if (HasPathRoles) extras.Add("path_roles");
            if (HasStrokeIds) extras.Add("continuous_strokes");
            if (HasCrossLayerShellContinuity) extras.Add("cross_layer_shell");
            if (HasFlows)  extras.Add("flows");
            if (HasLayerH) extras.Add("layer_h");
            if (HasLayerW) extras.Add("layer_w");
            if (HasLayerWf) extras.Add("layer_wf");
            if (HasPrintVol) extras.Add("print_vol");
            if (NozzleDiam.HasValue) extras.Add("nozzle_diam");
            if (HasPrintSpeed) extras.Add("print_speed");
            if (HasPrintAssessment) extras.Add(NozzleDiam.HasValue ? "print_assessment (Wc model)" : "print_assessment");
            if (HasFreshRisk) extras.Add("fresh_risk");
            if (HasBeamDeflection) extras.Add("beam_deflection");
            if (HasFailureState) extras.Add("failure_state");
            if (HasMotionPlan) extras.Add($"motion_plan ({MotionPlan.Count} moves)");
            if (HasProcessKpis) extras.Add("process_kpis");
            if (HasJobKpis) extras.Add("job_kpis");
            string extraTxt = extras.Count > 0 ? " + " + string.Join(", ", extras) : "";
            return string.Format(CultureInfo.InvariantCulture,
                "WASPer Print Path: {0} plane locations / {1} curves{2}",
                PointCount, BranchCount, extraTxt);
        }
    }

    public enum WasperMotionType
    {
        Print,
        Travel,
        ZHop
    }

    /// <summary>
    /// One resolved machine movement produced by Gc04. Coordinates remain in
    /// the same model space as the source wsp_path and feedrate is in mm/min.
    /// </summary>
    public sealed class WasperMotion
    {
        public Point3d From { get; }
        public Point3d To { get; }
        public double Feedrate { get; }
        public WasperMotionType Type { get; }
        public int LayerIndex { get; }
        public int BranchIndex { get; }
        public int PointIndex { get; }

        public WasperMotion(
            Point3d from,
            Point3d to,
            double feedrate,
            WasperMotionType type,
            int layerIndex,
            int branchIndex,
            int pointIndex)
        {
            From = from;
            To = to;
            Feedrate = feedrate;
            Type = type;
            LayerIndex = layerIndex;
            BranchIndex = branchIndex;
            PointIndex = pointIndex;
        }

        public double Length => From.DistanceTo(To);
        public double DurationMinutes => Feedrate > 0.0 ? Length / Feedrate : 0.0;
    }

    /// <summary>
    /// Ordered Gc04 job movements. It starts at the safe position above the
    /// first print point (or at that point when Z-hop is disabled); printer
    /// homing and user-defined start/end G-code are intentionally excluded.
    /// </summary>
    public sealed class WasperMotionPlan
    {
        private readonly List<WasperMotion> _motions;

        public WasperMotionPlan(IEnumerable<WasperMotion> motions)
        {
            _motions = motions != null
                ? new List<WasperMotion>(motions)
                : new List<WasperMotion>();
        }

        public IReadOnlyList<WasperMotion> Motions => _motions;
        public int Count => _motions.Count;
        public double DurationMinutes
        {
            get
            {
                double duration = 0.0;
                for (int i = 0; i < _motions.Count; i++)
                    duration += _motions[i].DurationMinutes;
                return duration;
            }
        }
    }

    public static class WasperMotionPlanBuilder
    {
        /// <summary>
        /// Builds the ordered job moves emitted by Gc04 from its resolved point
        /// and speed trees. Initial homing/positioning and start/end G-code are
        /// outside the wsp_path contract and are not included.
        /// </summary>
        public static WasperMotionPlan Build(
            DataTree<Point3d> points,
            DataTree<double> printSpeed,
            double travelSpeed,
            double zHop,
            double zHopSpeed,
            DataTree<int> strokeIds = null)
        {
            var motions = new List<WasperMotion>();
            if (points == null || points.BranchCount == 0)
                return new WasperMotionPlan(motions);

            Point3d current = Point3d.Unset;
            bool hasCurrent = false;
            bool hoppedAfterPreviousBranch = false;
            double hop = Math.Max(0.0, zHop);

            for (int branchIndex = 0; branchIndex < points.Paths.Count; branchIndex++)
            {
                GH_Path path = points.Paths[branchIndex];
                IList<Point3d> branch = points.Branch(path);
                if (branch == null || branch.Count == 0)
                    continue;

                int layerIndex = path.Indices.Length > 0 ? path.Indices[0] : 0;
                Point3d start = branch[0];
                double firstPrintSpeed = ResolveSpeed(printSpeed, path, 0);
                bool continuousFromPrevious =
                    hasCurrent &&
                    branchIndex > 0 &&
                    IsContinuousJoin(
                        strokeIds,
                        points.Paths[branchIndex - 1],
                        current,
                        path,
                        start);

                if (!hasCurrent)
                {
                    current = hop > 0.0
                        ? new Point3d(start.X, start.Y, start.Z + hop)
                        : start;
                    hasCurrent = true;

                    if (!PointsCoincide(current, start))
                    {
                        AddMotion(
                            motions, ref current, start, zHopSpeed,
                            WasperMotionType.ZHop, layerIndex, branchIndex, 0);
                    }
                }
                else if (!continuousFromPrevious)
                {
                    if (hop > 0.0 && !hoppedAfterPreviousBranch)
                    {
                        double targetZ = Math.Max(current.Z, start.Z) + hop;
                        AddMotion(
                            motions, ref current,
                            new Point3d(current.X, current.Y, targetZ),
                            zHopSpeed, WasperMotionType.ZHop,
                            layerIndex, branchIndex, 0);
                    }

                    Point3d travelTarget =
                        new Point3d(start.X, start.Y, current.Z);
                    AddMotion(
                        motions, ref current, travelTarget, travelSpeed,
                        WasperMotionType.Travel, layerIndex, branchIndex, 0);

                    if (!PointsCoincide(current, start))
                    {
                        AddMotion(
                            motions, ref current, start,
                            hop > 0.0 ? zHopSpeed : firstPrintSpeed,
                            hop > 0.0 ? WasperMotionType.ZHop : WasperMotionType.Travel,
                            layerIndex, branchIndex, 0);
                    }
                }

                hoppedAfterPreviousBranch = false;

                for (int pointIndex = 1; pointIndex < branch.Count; pointIndex++)
                {
                    AddMotion(
                        motions, ref current, branch[pointIndex],
                        ResolveSpeed(printSpeed, path, pointIndex),
                        WasperMotionType.Print,
                        layerIndex, branchIndex, pointIndex);
                }

                bool isLastInLayer =
                    branchIndex == points.Paths.Count - 1 ||
                    points.Paths[branchIndex + 1].Indices.Length == 0 ||
                    points.Paths[branchIndex + 1].Indices[0] != layerIndex;

                bool continuesToNext = false;
                if (branchIndex + 1 < points.Paths.Count)
                {
                    GH_Path nextPath = points.Paths[branchIndex + 1];
                    IList<Point3d> nextBranch = points.Branch(nextPath);
                    continuesToNext = nextBranch != null && nextBranch.Count > 0 &&
                        IsContinuousJoin(strokeIds, path, current, nextPath, nextBranch[0]);
                }

                if (hop > 0.0 && isLastInLayer && !continuesToNext)
                {
                    AddMotion(
                        motions, ref current,
                        new Point3d(current.X, current.Y, current.Z + hop),
                        zHopSpeed, WasperMotionType.ZHop,
                        layerIndex, branchIndex, branch.Count - 1);
                    hoppedAfterPreviousBranch = true;
                }
            }

            return new WasperMotionPlan(motions);
        }

        private static double ResolveSpeed(
            DataTree<double> speeds,
            GH_Path path,
            int pointIndex)
        {
            if (speeds == null || !speeds.PathExists(path))
                return 0.0;

            IList<double> branch = speeds.Branch(path);
            if (branch == null || branch.Count == 0)
                return 0.0;

            int index = branch.Count == 1
                ? 0
                : Math.Min(pointIndex, branch.Count - 1);
            return branch[index];
        }

        private static void AddMotion(
            List<WasperMotion> motions,
            ref Point3d current,
            Point3d target,
            double feedrate,
            WasperMotionType type,
            int layerIndex,
            int branchIndex,
            int pointIndex)
        {
            if (PointsCoincide(current, target))
            {
                current = target;
                return;
            }

            motions.Add(new WasperMotion(
                current, target, feedrate, type,
                layerIndex, branchIndex, pointIndex));
            current = target;
        }

        private static bool PointsCoincide(Point3d a, Point3d b)
        {
            double tolerance = Rhino.RhinoMath.ZeroTolerance;
            return a.DistanceToSquared(b) <= tolerance * tolerance;
        }

        private static bool IsContinuousJoin(
            DataTree<int> strokeIds,
            GH_Path previousPath,
            Point3d previousEnd,
            GH_Path currentPath,
            Point3d currentStart)
        {
            int previousStroke = WasperGcodeTreeUtil.StrokeIdAt(strokeIds, previousPath);
            int currentStroke = WasperGcodeTreeUtil.StrokeIdAt(strokeIds, currentPath);
            return previousStroke >= 0 &&
                   previousStroke == currentStroke &&
                   PointsCoincide(previousEnd, currentStart);
        }
    }

    /// <summary>Grasshopper wrapper used to wire a WasperPrintPath between components.</summary>
    public sealed class WasperPrintPathGoo : GH_Goo<WasperPrintPath>, IGH_PreviewData
    {
        private List<Polyline> _previewPolylines;
        private List<WasperPathRole> _previewRoles;
        private BoundingBox _previewBounds = BoundingBox.Empty;

        public WasperPrintPathGoo() : base((WasperPrintPath)null) { }
        public WasperPrintPathGoo(WasperPrintPath p) : base(p) { }

        public override bool   IsValid         => Value != null && Value.HasPlanes;
        public override string TypeName        => "WASPer Print Path";
        public override string TypeDescription =>
            "Packed printing path whose canonical geometry is a point-plane tree, with optional process, printability, fresh-risk, deformation, and failure-state data.";
        public override IGH_Goo Duplicate()    => new WasperPrintPathGoo(Value);
        public override string ToString() => Value?.ToString() ?? "Null WASPer Print Path";

        /// <summary>
        /// Cheap native Grasshopper preview for the packed path. Branch
        /// polylines are built once per goo instance and reused on redraw.
        /// </summary>
        public BoundingBox ClippingBox
        {
            get
            {
                if (!WasperPrintPathPreviewSettings.Enabled)
                    return BoundingBox.Empty;
                EnsurePreviewCache();
                return _previewBounds;
            }
        }

        public void DrawViewportWires(GH_PreviewWireArgs args)
        {
            if (!WasperPrintPathPreviewSettings.Enabled)
                return;

            EnsurePreviewCache();
            if (_previewPolylines == null)
                return;

            // GH_PreviewWireArgs keeps its selection flag internal in the
            // Rhino 8 SDK. Its selected wire colour is characteristically
            // green, so retain that highlight and use semantic role colours
            // otherwise.
            bool selectedHighlight =
                args.Color.G > args.Color.R + 40 &&
                args.Color.G > args.Color.B + 40;
            int thickness = Math.Max(
                WasperPrintPathPreviewSettings.Thickness,
                args.Thickness);
            for (int i = 0; i < _previewPolylines.Count; i++)
            {
                Polyline polyline = _previewPolylines[i];
                System.Drawing.Color color = selectedHighlight
                    ? args.Color
                    : WasperPrintPathPreviewSettings.ResolveColor(_previewRoles[i]);
                args.Pipeline.DrawPolyline(polyline, color, thickness);
            }
        }

        public void DrawViewportMeshes(GH_PreviewMeshArgs args)
        {
            // Deliberately wire-only. Pp04 owns the detailed bead mesh preview.
        }

        private void EnsurePreviewCache()
        {
            if (_previewPolylines != null)
                return;

            _previewPolylines = new List<Polyline>();
            _previewRoles = new List<WasperPathRole>();
            _previewBounds = BoundingBox.Empty;

            DataTree<Point3d> points = Value?.Points;
            if (points == null || points.BranchCount == 0)
                return;

            foreach (GH_Path path in points.Paths)
            {
                IList<Point3d> branch = points.Branch(path);
                if (branch == null || branch.Count < 2)
                    continue;

                var polyline = new Polyline(branch.Count);
                foreach (Point3d point in branch)
                {
                    if (point.IsValid)
                        polyline.Add(point);
                }

                if (polyline.Count < 2)
                    continue;

                _previewPolylines.Add(polyline);
                WasperPathRole role = WasperGcodeTreeUtil.PathRoleAt(
                    Value?.PathRoles,
                    path);
                _previewRoles.Add(role);
                BoundingBox branchBounds = polyline.BoundingBox;
                if (branchBounds.IsValid)
                    _previewBounds.Union(branchBounds);
            }
        }

        // Trees are not serialised — recomputed on every solve.
        public override bool Write(GH_IWriter writer) => true;
        public override bool Read(GH_IReader reader)  => true;

        public override bool CastTo<T>(ref T target)
        {
            if (typeof(T) == typeof(WasperPrintPath) && Value != null)
            {
                target = (T)(object)Value;
                return true;
            }
            return base.CastTo(ref target);
        }

        public override bool CastFrom(object source)
        {
            switch (source)
            {
                case WasperPrintPath p:    Value = p;       return true;
                case WasperPrintPathGoo g: Value = g.Value; return true;
                case GH_ObjectWrapper w when w.Value is WasperPrintPath wp:
                    Value = wp; return true;
                default: return false;
            }
        }
    }

    // =========================================================================
    //  Wasper3dpParams
    // =========================================================================

    /// <summary>Printing process carried by a Wasper3dpParams object.</summary>
    public enum Wasper3dpProcess
    {
        LDM = 0,   // Liquid Deposition Modeling (clay/paste; split files, time correction)
        FDM = 1,   // Fused Deposition Modeling (filament; fan, temperatures, custom start/end)
    }

    /// <summary>
    /// Process-aware packed printing parameters for the Marlin G-code generator.
    /// Every field is nullable: only values the user explicitly set travel with
    /// the object; the generator fills the rest with process-specific defaults.
    /// PrintSpeed may be a single value or a per-point tree. FanSpeed accepts a
    /// scalar, per-layer list (single branch) or {layer}/{layer;0} tree.
    /// </summary>
    public sealed class Wasper3dpParams
    {
        public Wasper3dpProcess Process { get; set; } = Wasper3dpProcess.LDM;

        // ---- common ---------------------------------------------------------
        public double? NozzleDiameter  { get; set; }   // mm (required by the generator)
        public double? LayerW          { get; set; }   // mm; Gc04 override for nominal/base bead width, otherwise path LayerW or layer_h×2.5
        public double? FillamentMulti  { get; set; }   // mm — LDM: filament multiplier diameter (def 5.15)
                                                       //      FDM: filament diameter (def 1.75)
        public DataTree<double> PrintSpeed { get; set; } // mm/min, scalar or per-point tree
                                                       // (LDM def 7000 | FDM def 1200)
        public double? TravelSpeed     { get; set; }   // mm/min (LDM def 8000 | FDM def 5000)
        public double? ZHop            { get; set; }   // mm (0/off by default; >0 enables positive-Z hop moves)
        public double? ZHopSpeed       { get; set; }   // mm/min (LDM def 6000 | FDM def 3000)
        public double? Density         { get; set; }   // kg/m3 (LDM def 1600 | FDM def 1240); wsp_mat overrides

        // ---- LDM only -------------------------------------------------------
        public bool?   SplitGcode      { get; set; }
        public double? SplitVolL       { get; set; }   // litres
        public double? TimeCorrection  { get; set; }   // multiplier on the raw time estimate

        // ---- FDM only -------------------------------------------------------
        public DataTree<double> FanSpeed { get; set; } // 0-255 or 0-100 (%), per layer
        public double? TempNozzle      { get; set; }   // °C (def 200)
        public double? TempBed         { get; set; }   // °C (def 60; 0 allowed = off)
        public List<string> CustomStartGcode { get; set; } // overrides the default FDM start block
        public List<string> CustomEndGcode   { get; set; } // overrides the default FDM end block

        public bool HasPrintSpeed => PrintSpeed != null && PrintSpeed.BranchCount > 0;
        public bool HasFanSpeed   => FanSpeed   != null && FanSpeed.BranchCount   > 0;
        public Wasper3dpParams Clone()
        {
            return new Wasper3dpParams
            {
                Process = Process,
                NozzleDiameter = NozzleDiameter,
                LayerW = LayerW,
                FillamentMulti = FillamentMulti,
                PrintSpeed = PrintSpeed,
                TravelSpeed = TravelSpeed,
                ZHop = ZHop,
                ZHopSpeed = ZHopSpeed,
                Density = Density,
                SplitGcode = SplitGcode,
                SplitVolL = SplitVolL,
                TimeCorrection = TimeCorrection,
                FanSpeed = FanSpeed,
                TempNozzle = TempNozzle,
                TempBed = TempBed,
                CustomStartGcode = CustomStartGcode,
                CustomEndGcode = CustomEndGcode
            };
        }
        public override string ToString()
        {
            var sb = new StringBuilder($"WASPer 3DP Params [{Process}]:");
            var set = new List<string>();
            if (NozzleDiameter.HasValue) set.Add(Fmt("nozzle", NozzleDiameter.Value, "mm"));
            if (LayerW.HasValue)         set.Add(Fmt("layer_w", LayerW.Value, "mm"));
            if (FillamentMulti.HasValue) set.Add(Fmt("fil_diam", FillamentMulti.Value, "mm"));
            if (HasPrintSpeed)
            {
                set.Add(PrintSpeed.DataCount == 1
                    ? Fmt("print_speed", PrintSpeed.AllData()[0], "mm/min")
                    : $"print_speed tree ({PrintSpeed.DataCount} values)");
            }
            if (TravelSpeed.HasValue)    set.Add(Fmt("travel", TravelSpeed.Value, "mm/min"));
            if (ZHop.HasValue)           set.Add(Fmt("z_hop", ZHop.Value, "mm"));
            if (ZHopSpeed.HasValue)      set.Add(Fmt("z_hop_speed", ZHopSpeed.Value, "mm/min"));
            if (Density.HasValue)        set.Add(Fmt("density", Density.Value, "kg/m3"));
            if (SplitGcode.HasValue)     set.Add($"split={(SplitGcode.Value ? "ON" : "OFF")}");
            if (SplitVolL.HasValue)      set.Add(Fmt("split_vol", SplitVolL.Value, "L"));
            if (TimeCorrection.HasValue) set.Add(Fmt("time_corr", TimeCorrection.Value, ""));
            if (HasFanSpeed)             set.Add($"fan ({FanSpeed.DataCount} value{(FanSpeed.DataCount == 1 ? "" : "s")})");
            if (TempNozzle.HasValue)     set.Add(Fmt("T_nozzle", TempNozzle.Value, "C"));
            if (TempBed.HasValue)        set.Add(Fmt("T_bed", TempBed.Value, "C"));
            if (CustomStartGcode != null && CustomStartGcode.Count > 0)
                set.Add($"custom start ({CustomStartGcode.Count} lines)");
            if (CustomEndGcode != null && CustomEndGcode.Count > 0)
                set.Add($"custom end ({CustomEndGcode.Count} lines)");

            sb.Append(set.Count == 0 ? " (all defaults)" : " " + string.Join(" | ", set));
            return sb.ToString();
        }

        private static string Fmt(string name, double v, string unit) =>
            string.Format(CultureInfo.InvariantCulture, "{0}={1:0.###}{2}",
                name, v, string.IsNullOrEmpty(unit) ? "" : " " + unit);
    }

    /// <summary>Grasshopper wrapper used to wire a Wasper3dpParams between components.</summary>
    public sealed class Wasper3dpParamsGoo : GH_Goo<Wasper3dpParams>
    {
        public Wasper3dpParamsGoo() : base((Wasper3dpParams)null) { }
        public Wasper3dpParamsGoo(Wasper3dpParams p) : base(p) { }

        public override bool   IsValid         => Value != null;
        public override string TypeName        => "WASPer 3DP Params";
        public override string TypeDescription =>
            "Process-aware packed printing parameters (LDM or FDM) for the Marlin G-code generator.";
        public override IGH_Goo Duplicate()    => new Wasper3dpParamsGoo(Value);
        public override string ToString() => Value?.ToString() ?? "Null WASPer 3DP Params";

        // Not serialised — recomputed on every solve.
        public override bool Write(GH_IWriter writer) => true;
        public override bool Read(GH_IReader reader)  => true;

        public override bool CastTo<T>(ref T target)
        {
            if (typeof(T) == typeof(Wasper3dpParams) && Value != null)
            {
                target = (T)(object)Value;
                return true;
            }
            return base.CastTo(ref target);
        }

        public override bool CastFrom(object source)
        {
            switch (source)
            {
                case Wasper3dpParams p:    Value = p;       return true;
                case Wasper3dpParamsGoo g: Value = g.Value; return true;
                case GH_ObjectWrapper w when w.Value is Wasper3dpParams wp:
                    Value = wp; return true;
                default: return false;
            }
        }
    }

    // =========================================================================
    //  Tree conversion helpers (GH_Structure → DataTree)
    // =========================================================================

    public sealed class WasperRobotSimulationCut
    {
        public WasperRobotSimulationCut(
            int completedPointCount,
            int partialBranchIndex,
            int partialPointIndex,
            Point3d partialPoint,
            double progress,
            int matchedPointCount,
            int totalPointCount,
            int currentTargetIndex,
            int targetCount,
            bool usedWorldCoordinates)
        {
            CompletedPointCount = completedPointCount;
            PartialBranchIndex = partialBranchIndex;
            PartialPointIndex = partialPointIndex;
            PartialPoint = partialPoint;
            Progress = progress;
            MatchedPointCount = matchedPointCount;
            TotalPointCount = totalPointCount;
            CurrentTargetIndex = currentTargetIndex;
            TargetCount = targetCount;
            UsedWorldCoordinates = usedWorldCoordinates;
        }

        public int CompletedPointCount { get; }
        public int PartialBranchIndex { get; }
        public int PartialPointIndex { get; }
        public Point3d PartialPoint { get; }
        public double Progress { get; }
        public int MatchedPointCount { get; }
        public int TotalPointCount { get; }
        public int CurrentTargetIndex { get; }
        public int TargetCount { get; }
        public bool UsedWorldCoordinates { get; }
        public bool HasPartialPoint =>
            PartialBranchIndex >= 0 && PartialPointIndex > 0 && PartialPoint.IsValid;
    }

    /// <summary>
    /// Optional reflection adapter for a Robots Program. The core WASPer assembly
    /// contains no Robots type references, so it remains loadable when Robots is
    /// absent. A live Program object can only exist when Robots is installed.
    /// </summary>
    public sealed class WasperRobotProgramAdapter
    {
        private readonly object _program;
        private readonly Type _programType;

        private WasperRobotProgramAdapter(object program)
        {
            _program = program;
            _programType = program.GetType();
        }

        public object ProgramObject => _program;

        public static bool TryCreate(
            object source,
            out WasperRobotProgramAdapter adapter)
        {
            adapter = null;
            object value = Unwrap(source);
            if (value == null)
                return false;

            Type type = value.GetType();
            string assemblyName = type.Assembly.GetName().Name;
            if (!string.Equals(assemblyName, "Robots", StringComparison.Ordinal) ||
                !string.Equals(type.FullName, "Robots.Program", StringComparison.Ordinal))
            {
                return false;
            }

            adapter = new WasperRobotProgramAdapter(value);
            return true;
        }

        public bool TryGetSimulationState(
            out bool hasSimulation,
            out double duration,
            out double currentTime,
            out int targetIndex,
            out string error)
        {
            hasSimulation = false;
            duration = 0.0;
            currentTime = 0.0;
            targetIndex = -1;
            error = null;

            try
            {
                hasSimulation = GetProperty<bool>(_program, "HasSimulation");
                duration = GetProperty<double>(_program, "Duration");
                if (!hasSimulation)
                    return true;

                object pose = GetProperty<object>(_program, "CurrentSimulationPose");
                currentTime = GetProperty<double>(pose, "CurrentTime");
                targetIndex = GetProperty<int>(pose, "TargetIndex");
                return true;
            }
            catch (Exception exception)
            {
                error = $"Could not read the Robots Program simulation state: {exception.Message}";
                return false;
            }
        }

        public int TargetCount
        {
            get
            {
                object targets = GetProperty<object>(_program, "Targets");
                return GetCollectionCount(targets);
            }
        }

        public bool TryGetTargetPoint(
            int targetIndex,
            bool worldCoordinates,
            out Point3d point)
        {
            point = Point3d.Unset;
            if (!TryGetTargetPlanes(
                    targetIndex,
                    out Plane localPlane,
                    out Plane worldPlane))
            {
                return false;
            }

            point = worldCoordinates ? worldPlane.Origin : localPlane.Origin;
            return point.IsValid;
        }

        public bool TryGetTargetPlanes(
            int targetIndex,
            out Plane localPlane,
            out Plane worldPlane)
        {
            localPlane = Plane.Unset;
            worldPlane = Plane.Unset;

            try
            {
                object targets = GetProperty<object>(_program, "Targets");
                object systemTarget = GetCollectionItem(targets, targetIndex);
                object programTargets =
                    GetProperty<object>(systemTarget, "ProgramTargets");
                if (GetCollectionCount(programTargets) == 0)
                    return false;

                object target = GetCollectionItem(programTargets, 0);
                localPlane = GetProperty<Plane>(target, "Plane");
                worldPlane = GetProperty<Plane>(target, "WorldPlane");
                return localPlane.IsValid && worldPlane.IsValid;
            }
            catch
            {
                return false;
            }
        }

        public bool TryGetLastPlane(int group, out Plane plane)
        {
            plane = Plane.Unset;
            try
            {
                object pose = GetProperty<object>(_program, "CurrentSimulationPose");
                MethodInfo method = pose.GetType().GetMethod(
                    "GetLastPlane",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(int) },
                    null);
                if (method == null)
                    return false;

                object result = method.Invoke(pose, new object[] { group });
                if (result is Plane value)
                {
                    plane = value;
                    return plane.IsValid;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }

        public bool TryAnimate(
            double timeSeconds,
            out List<Mesh> meshes,
            out string error)
        {
            meshes = new List<Mesh>();
            error = null;

            try
            {
                object meshPoser = GetProperty<object>(_program, "MeshPoser");
                if (meshPoser == null)
                {
                    object robotSystem = GetProperty<object>(_program, "RobotSystem");
                    Type poserType = _programType.Assembly.GetType(
                        "Robots.RhinoMeshPoser",
                        throwOnError: false);
                    if (poserType == null)
                    {
                        error = "The installed Robots version does not provide RhinoMeshPoser.";
                        return false;
                    }

                    meshPoser = Activator.CreateInstance(
                        poserType,
                        new[] { robotSystem });
                    PropertyInfo poserProperty =
                        _programType.GetProperty("MeshPoser");
                    poserProperty?.SetValue(_program, meshPoser);
                }

                MethodInfo animate = _programType.GetMethod(
                    "Animate",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    new[] { typeof(double), typeof(bool) },
                    null);
                if (animate == null)
                {
                    error = "The installed Robots version does not expose Program.Animate.";
                    return false;
                }

                animate.Invoke(_program, new object[] { timeSeconds, false });
                object meshValues = GetProperty<object>(meshPoser, "Meshes");
                if (meshValues is IEnumerable enumerable)
                {
                    foreach (object item in enumerable)
                    {
                        if (item is Mesh mesh)
                            meshes.Add(mesh);
                    }
                }

                if (meshes.Count == 0)
                {
                    error = "The Robots Program produced no simulation meshes.";
                    return false;
                }
                return true;
            }
            catch (TargetInvocationException exception)
            {
                error = exception.InnerException?.Message ?? exception.Message;
                return false;
            }
            catch (Exception exception)
            {
                error = $"Could not animate the Robots Program: {exception.Message}";
                return false;
            }
        }

        private static object Unwrap(object source)
        {
            object value = source;
            for (int depth = 0; depth < 4 && value is IGH_Goo goo; depth++)
            {
                if (goo is GH_ObjectWrapper wrapper)
                {
                    value = wrapper.Value;
                    continue;
                }

                object scriptValue;
                try { scriptValue = goo.ScriptVariable(); }
                catch { scriptValue = null; }

                if (scriptValue == null || ReferenceEquals(scriptValue, value))
                    break;
                value = scriptValue;
            }
            return value;
        }

        private static T GetProperty<T>(object instance, string name)
        {
            if (instance == null)
                throw new InvalidOperationException($"{name} owner is null.");

            PropertyInfo property = instance.GetType().GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public);
            if (property == null)
                throw new MissingMemberException(instance.GetType().FullName, name);

            object value = property.GetValue(instance);
            if (value == null)
                return default;
            return (T)value;
        }

        private static int GetCollectionCount(object collection)
        {
            if (collection == null)
                return 0;
            if (collection is ICollection nonGeneric)
                return nonGeneric.Count;

            PropertyInfo property = collection.GetType().GetProperty("Count");
            return property == null
                ? 0
                : Convert.ToInt32(property.GetValue(collection), CultureInfo.InvariantCulture);
        }

        private static object GetCollectionItem(object collection, int index)
        {
            if (collection is IList list)
                return list[index];

            PropertyInfo indexer = collection.GetType().GetProperty("Item");
            if (indexer == null)
                throw new MissingMemberException(collection.GetType().FullName, "Item");
            return indexer.GetValue(collection, new object[] { index });
        }
    }

    /// <summary>Small conversions shared by the Gcode components.</summary>
    public static class WasperGcodeTreeUtil
    {
        public static bool TryGetSimulationProgress(
            IGH_DataAccess da,
            int index,
            out double progress,
            out bool fromRobotProgram,
            out string error)
        {
            WasperRobotProgramAdapter program;
            bool success = TryGetSimulationInput(
                da, index, out progress, out program, out error);
            fromRobotProgram = program != null;
            return success;
        }

        public static bool TryGetSimulationInput(
            IGH_DataAccess da,
            int index,
            out double progress,
            out WasperRobotProgramAdapter program,
            out string error)
        {
            progress = 1.0;
            program = null;
            error = null;

            IGH_Goo goo = null;
            if (!da.GetData(index, ref goo) || goo == null)
                return true;

            if (WasperRobotProgramAdapter.TryCreate(goo, out program))
            {
                if (!program.TryGetSimulationState(
                        out bool hasSimulation,
                        out double duration,
                        out double time,
                        out _,
                        out error))
                {
                    return false;
                }

                if (!hasSimulation)
                {
                    error = "The Robots Program has no simulation data.";
                    return false;
                }

                if (double.IsNaN(duration) || double.IsInfinity(duration) || duration < 0.0 ||
                    double.IsNaN(time) || double.IsInfinity(time))
                {
                    error = "The Robots Program contains an invalid simulation time or duration.";
                    return false;
                }

                progress = duration > Rhino.RhinoMath.ZeroTolerance
                    ? Math.Max(0.0, Math.Min(1.0, time / duration))
                    : 1.0;
                return true;
            }

            try
            {
                object value = goo.ScriptVariable();
                double numeric = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(numeric) || double.IsInfinity(numeric))
                {
                    error = "sim must be a finite number from 0 to 1 or a Robots Program Simulation output.";
                    return false;
                }

                progress = Math.Max(0.0, Math.Min(1.0, numeric));
                return true;
            }
            catch
            {
                error = "sim must be a number from 0 to 1 or the Program (P) output from Robots Program Simulation.";
                return false;
            }
        }

        public static bool TryGetRobotSimulationCut(
            WasperRobotProgramAdapter program,
            IList<List<Point3d>> pointBranches,
            double tolerance,
            out WasperRobotSimulationCut cut,
            out string error)
        {
            cut = null;
            error = null;

            if (program == null ||
                !program.TryGetSimulationState(
                    out bool hasSimulation,
                    out double duration,
                    out double currentTime,
                    out int currentTargetIndex,
                    out error) ||
                !hasSimulation)
            {
                error ??= "The Robots Program has no simulation data.";
                return false;
            }

            if (pointBranches == null)
            {
                error = "The WASPer path has no point branches.";
                return false;
            }

            var pathPoints = new List<WasperPathPointRef>();
            for (int branch = 0; branch < pointBranches.Count; branch++)
            {
                List<Point3d> points = pointBranches[branch];
                if (points == null) continue;
                for (int point = 0; point < points.Count; point++)
                    pathPoints.Add(new WasperPathPointRef(branch, point, points[point]));
            }

            if (pathPoints.Count == 0)
            {
                error = "The WASPer path has no valid points to match against the Robots Program.";
                return false;
            }

            double matchTolerance = !double.IsNaN(tolerance) &&
                                    !double.IsInfinity(tolerance) &&
                                    tolerance > 0.0
                ? Math.Max(tolerance * 10.0, 1e-4)
                : 1e-4;

            int[] localMap = BuildProgramPointMap(
                program, pathPoints, matchTolerance, false, out int localMatched);
            int[] worldMap = BuildProgramPointMap(
                program, pathPoints, matchTolerance, true, out int worldMatched);
            bool useWorldCoordinates = worldMatched > localMatched;
            int[] pointTargetIndices = useWorldCoordinates ? worldMap : localMap;
            int matched = useWorldCoordinates ? worldMatched : localMatched;

            if (matched == 0)
            {
                error =
                    "No ordered Robots target coordinates matched the wsp_path points. " +
                    "Use the same wsp_path that generated the program targets and keep their order.";
                return false;
            }

            if (currentTargetIndex < 0 || currentTargetIndex >= program.TargetCount)
            {
                error = "The Robots Program reports an invalid current simulation target index.";
                return false;
            }

            bool atEnd = duration <= Rhino.RhinoMath.ZeroTolerance ||
                         currentTime >= duration - Rhino.RhinoMath.ZeroTolerance;

            int completed = 0;
            if (atEnd)
            {
                completed = pathPoints.Count;
            }
            else
            {
                while (completed < pointTargetIndices.Length &&
                       pointTargetIndices[completed] >= 0 &&
                       pointTargetIndices[completed] < currentTargetIndex)
                {
                    completed++;
                }
            }

            int partialPathIndex = -1;
            Point3d partialPoint = Point3d.Unset;
            double partialFraction = 0.0;

            if (!atEnd &&
                completed < pointTargetIndices.Length &&
                pointTargetIndices[completed] == currentTargetIndex &&
                pathPoints[completed].PointIndex > 0 &&
                completed > 0 &&
                pathPoints[completed - 1].BranchIndex == pathPoints[completed].BranchIndex)
            {
                partialPathIndex = completed;
                Plane simulatedPlane;
                if (!program.TryGetLastPlane(0, out simulatedPlane))
                    simulatedPlane = Plane.Unset;

                if (!useWorldCoordinates)
                {
                    if (!program.TryGetTargetPlanes(
                            currentTargetIndex,
                            out Plane localPlane,
                            out Plane worldPlane))
                    {
                        simulatedPlane = Plane.Unset;
                    }
                    else
                    {
                        Transform toLocal = Transform.PlaneToPlane(
                            worldPlane, localPlane);
                        if (!simulatedPlane.Transform(toLocal))
                            simulatedPlane = Plane.Unset;
                    }
                }

                if (simulatedPlane.IsValid)
                {
                    partialPoint = simulatedPlane.Origin;
                    Point3d previous = pathPoints[completed - 1].Point;
                    Point3d destination = pathPoints[completed].Point;
                    Vector3d segment = destination - previous;
                    double lengthSquared = segment.SquareLength;
                    if (lengthSquared > Rhino.RhinoMath.ZeroTolerance)
                    {
                        partialFraction = Math.Max(0.0, Math.Min(1.0,
                            Vector3d.Multiply(partialPoint - previous, segment) /
                            lengthSquared));
                    }
                }
                else
                {
                    partialPathIndex = -1;
                }
            }

            double progress = pathPoints.Count == 0
                ? 1.0
                : Math.Max(0.0, Math.Min(1.0,
                    (completed + partialFraction) / pathPoints.Count));

            WasperPathPointRef partialRef = partialPathIndex >= 0
                ? pathPoints[partialPathIndex]
                : null;
            cut = new WasperRobotSimulationCut(
                completed,
                partialRef != null ? partialRef.BranchIndex : -1,
                partialRef != null ? partialRef.PointIndex : -1,
                partialPoint,
                progress,
                matched,
                pathPoints.Count,
                currentTargetIndex,
                program.TargetCount,
                useWorldCoordinates);
            return true;
        }

        private static int[] BuildProgramPointMap(
            WasperRobotProgramAdapter program,
            IList<WasperPathPointRef> pathPoints,
            double tolerance,
            bool useWorldCoordinates,
            out int matched)
        {
            var map = new int[pathPoints.Count];
            for (int i = 0; i < map.Length; i++) map[i] = -1;

            matched = 0;
            for (int targetIndex = 0;
                 targetIndex < program.TargetCount && matched < pathPoints.Count;
                 targetIndex++)
            {
                if (!program.TryGetTargetPoint(
                        targetIndex,
                        useWorldCoordinates,
                        out Point3d targetPoint))
                {
                    continue;
                }
                if (targetPoint.DistanceTo(pathPoints[matched].Point) > tolerance)
                    continue;

                map[matched] = targetIndex;
                matched++;

                while (matched < pathPoints.Count &&
                       pathPoints[matched].Point.DistanceTo(targetPoint) <= tolerance)
                {
                    map[matched] = targetIndex;
                    matched++;
                }
            }

            return map;
        }

        private sealed class WasperPathPointRef
        {
            public WasperPathPointRef(int branchIndex, int pointIndex, Point3d point)
            {
                BranchIndex = branchIndex;
                PointIndex = pointIndex;
                Point = point;
            }

            public int BranchIndex { get; }
            public int PointIndex { get; }
            public Point3d Point { get; }
        }

        public static bool TryGetPrintPath(IGH_DataAccess da, int index, out WasperPrintPath path)
        {
            path = null;
            IGH_Goo goo = null;
            if (!da.GetData(index, ref goo) || goo == null) return false;

            if (goo is WasperPrintPathGoo printPathGoo && printPathGoo.Value != null)
            {
                path = printPathGoo.Value;
                return path.HasPoints;
            }

            if (goo is GH_ObjectWrapper wrapper && wrapper.Value is WasperPrintPath wrappedPath)
            {
                path = wrappedPath;
                return path.HasPoints;
            }

            return false;
        }

        public static WasperPrintPath PackPrintPath(
            GH_Structure<GH_Point> points,
            GH_Structure<GH_Plane> planes,
            GH_Structure<GH_Number> flows,
            GH_Structure<GH_Number> layerH,
            GH_Structure<GH_Number> printSpeed = null)
        {
            return new WasperPrintPath(
                ToPointTree(points),
                ToPlaneTree(planes),
                ToDoubleTree(flows),
                ToDoubleTree(layerH),
                ToDoubleTree(printSpeed));
        }

        /// <summary>
        /// Length of the common leading index prefix shared by every path, e.g. 1 for
        /// grafted {0;layer} trees. The logical layer id is the first VARYING index
        /// after this prefix (Gc01 convention: "the first varying path index after any
        /// common grafted prefix"). Always keeps at least one index available as the
        /// layer, so a tree whose paths are all identical returns length - 1.
        /// Consumers: Pr01/Pr03/Pr04 layer resolution. Using a bare path[0] breaks on
        /// grafted trees: every branch reads as layer 0, layer-to-layer support and
        /// load accumulation silently collapse.
        /// </summary>
        public static int CommonPathPrefixLength(IList<GH_Path> paths)
        {
            if (paths == null || paths.Count == 0) return 0;
            int minLen = int.MaxValue;
            foreach (var p in paths)
                if (p != null && p.Length < minLen) minLen = p.Length;
            if (minLen == int.MaxValue || minLen == 0) return 0;

            int prefix = 0;
            for (int pos = 0; pos < minLen; pos++)
            {
                int first = paths[0][pos];
                bool allSame = true;
                foreach (var p in paths)
                {
                    if (p[pos] != first) { allSame = false; break; }
                }
                if (!allSame) break;
                prefix++;
            }
            return Math.Min(prefix, minLen - 1);
        }

        /// <summary>Layer id of a path given the tree's common prefix length.</summary>
        public static int LayerFromPath(GH_Path path, int prefixLength)
        {
            if (path == null || path.Length == 0) return 0;
            return path[Math.Min(Math.Max(0, prefixLength), path.Length - 1)];
        }

        /// <summary>
        /// Canonical reference-plane path for one point/path branch. It retains
        /// any common grafted prefix and ends at the logical-layer dimension.
        /// Example: {0;3;2} with prefixLength 1 becomes {0;3}.
        /// </summary>
        public static GH_Path LayerPlanePath(GH_Path pointPath, int prefixLength)
        {
            if (pointPath == null || pointPath.Length == 0)
                return new GH_Path(0);
            int length = Math.Min(
                pointPath.Length,
                Math.Max(1, prefixLength + 1));
            var indices = new int[length];
            for (int i = 0; i < length; i++)
                indices[i] = pointPath[i];
            return new GH_Path(indices);
        }

        /// <summary>
        /// Resolves the authoritative layer reference plane associated with a
        /// point/path branch. LayerPlanes is intentionally sparse.
        /// </summary>
        public static bool TryLayerPlaneAt(
            DataTree<Plane> layerPlanes,
            GH_Path pointPath,
            int prefixLength,
            out Plane plane)
        {
            plane = Plane.Unset;
            if (layerPlanes == null || pointPath == null)
                return false;
            GH_Path layerPath = LayerPlanePath(pointPath, prefixLength);
            if (!layerPlanes.PathExists(layerPath))
                return false;
            IList<Plane> branch = layerPlanes.Branch(layerPath);
            if (branch == null)
                return false;
            for (int i = 0; i < branch.Count; i++)
            {
                if (!branch[i].IsValid)
                    continue;
                plane = branch[i];
                return true;
            }
            return false;
        }

        /// <summary>
        /// Keeps one authoritative reference plane for every logical layer
        /// represented by the retained point/path branches.
        /// </summary>
        public static DataTree<Plane> FilterLayerPlanes(
            DataTree<Plane> layerPlanes,
            IEnumerable<GH_Path> retainedPointPaths,
            int prefixLength)
        {
            if (layerPlanes == null || retainedPointPaths == null)
                return null;

            var result = new DataTree<Plane>();
            var seen = new HashSet<GH_Path>();
            foreach (GH_Path pointPath in retainedPointPaths)
            {
                GH_Path layerPath = LayerPlanePath(pointPath, prefixLength);
                if (!seen.Add(layerPath) || !layerPlanes.PathExists(layerPath))
                    continue;
                IList<Plane> branch = layerPlanes.Branch(layerPath);
                if (branch == null)
                    continue;
                Plane plane = branch.FirstOrDefault(candidate => candidate.IsValid);
                if (plane.IsValid)
                    result.Add(plane, layerPath);
            }
            return result.BranchCount > 0 ? result : null;
        }

        /// <summary>
        /// Keeps one semantic path-role value for each requested branch path.
        /// Used when a simulation or reconstruction returns only a subset of
        /// the original wsp_path branches.
        /// </summary>
        public static DataTree<int> FilterPathRoles(
            DataTree<int> roles,
            IEnumerable<GH_Path> paths)
        {
            if (roles == null || paths == null)
                return null;

            var filtered = new DataTree<int>();
            foreach (GH_Path path in paths)
            {
                if (path == null || !roles.PathExists(path))
                    continue;

                IList<int> branch = roles.Branch(path);
                if (branch != null && branch.Count > 0)
                    filtered.Add(branch[0], path);
            }

            return filtered.BranchCount > 0 ? filtered : null;
        }

        /// <summary>Returns the semantic role stored for one path branch.</summary>
        public static WasperPathRole PathRoleAt(
            DataTree<int> roles,
            GH_Path path)
        {
            if (roles == null || path == null || !roles.PathExists(path))
                return WasperPathRole.Undefined;

            IList<int> branch = roles.Branch(path);
            if (branch == null || branch.Count == 0 ||
                !Enum.IsDefined(typeof(WasperPathRole), branch[0]))
                return WasperPathRole.Undefined;

            return (WasperPathRole)branch[0];
        }

        /// <summary>
        /// True when a branch should be processed by a target_role selector.
        /// targetRole 0 processes every branch, including legacy/undefined ones.
        /// Values 1-5 process the corresponding semantic role; selector value 6
        /// processes stored role 0 (Undefined) without conflicting with All.
        /// </summary>
        public static bool MatchesTargetRole(
            DataTree<int> roles,
            GH_Path path,
            int targetRole)
        {
            return targetRole == 0 ||
                (targetRole == 6 &&
                 PathRoleAt(roles, path) == WasperPathRole.Undefined) ||
                (targetRole >= 1 && targetRole <= 5 &&
                 (int)PathRoleAt(roles, path) == targetRole);
        }

        /// <summary>
        /// True when a branch matches any selected role. [0] means All paths and
        /// is mutually exclusive with role-specific selections.
        /// </summary>
        public static bool MatchesTargetRoles(
            DataTree<int> roles,
            GH_Path path,
            IList<int> targetRoles)
        {
            return MatchesTargetRoles(PathRoleAt(roles, path), targetRoles);
        }

        public static bool MatchesTargetRoles(
            WasperPathRole role,
            IList<int> targetRoles)
        {
            return targetRoles == null || targetRoles.Count == 0 ||
                   targetRoles.Contains(0) ||
                   (role == WasperPathRole.Undefined && targetRoles.Contains(6)) ||
                   (role != WasperPathRole.Undefined &&
                    targetRoles.Contains((int)role));
        }

        public static bool IsValidTargetRole(int targetRole) =>
            targetRole >= 0 && targetRole <= 6;

        public static bool TryNormalizeTargetRoles(
            IEnumerable<int> values,
            out List<int> targetRoles,
            out string error)
        {
            targetRoles = values == null
                ? new List<int>()
                : values.Distinct().ToList();
            error = null;

            if (targetRoles.Count == 0)
                targetRoles.Add(0);

            int invalid = targetRoles.FirstOrDefault(value => !IsValidTargetRole(value));
            if (targetRoles.Any(value => !IsValidTargetRole(value)))
            {
                error = $"Role {invalid} is invalid. Use 0 All, 1 Shell, 2 Infill, 3 Partition, 4 Support, 5 Transition, or 6 Undefined.";
                return false;
            }

            if (targetRoles.Contains(0) && targetRoles.Count > 1)
            {
                error = "All paths (0) is mutually exclusive and cannot be combined with Shell, Infill, Partition, Support, Transition, or Undefined.";
                return false;
            }

            targetRoles.Sort();
            return true;
        }

        public static bool TargetsAllRoles(IList<int> targetRoles) =>
            targetRoles == null || targetRoles.Count == 0 || targetRoles.Contains(0);

        public static string TargetRoleName(int targetRole)
        {
            switch (targetRole)
            {
                case 1: return "Shell";
                case 2: return "Infill";
                case 3: return "Partition";
                case 4: return "Support";
                case 5: return "Transition";
                case 6: return "Undefined";
                default: return "All paths";
            }
        }

        /// <summary>Returns the stored continuity group for one branch, or -1.</summary>
        public static int StrokeIdAt(DataTree<int> strokeIds, GH_Path path)
        {
            if (strokeIds == null || path == null || !strokeIds.PathExists(path))
                return -1;

            IList<int> branch = strokeIds.Branch(path);
            return branch != null && branch.Count > 0 ? branch[0] : -1;
        }

        /// <summary>
        /// Detects the main mixed-strategy hazard introduced by continuous Shell
        /// printing: one or more Shell strokes span logical layers while other
        /// deposited path roles remain separate. This is a warning condition,
        /// not a proof of invalidity; the interior may have been designed for it.
        /// </summary>
        public static bool TryGetContinuousShellInteriorWarning(
            WasperPrintPath path,
            out string warning)
        {
            warning = null;
            if (path?.PtPlanes == null || path.PathRoles == null)
                return false;

            List<GH_Path> paths = path.PtPlanes.Paths.ToList();
            if (paths.Count == 0)
                return false;
            int prefixLength = CommonPathPrefixLength(paths);
            var shellStrokeLayers = new Dictionary<int, HashSet<int>>();
            var companionRoles = new HashSet<WasperPathRole>();

            foreach (GH_Path branchPath in paths)
            {
                WasperPathRole role = PathRoleAt(path.PathRoles, branchPath);
                int strokeId = StrokeIdAt(path.StrokeIds, branchPath);
                if (role == WasperPathRole.Shell && strokeId >= 0)
                {
                    if (!shellStrokeLayers.TryGetValue(strokeId, out HashSet<int> layers))
                    {
                        layers = new HashSet<int>();
                        shellStrokeLayers.Add(strokeId, layers);
                    }
                    layers.Add(LayerFromPath(branchPath, prefixLength));
                }
                else if (role != WasperPathRole.Transition)
                {
                    companionRoles.Add(role);
                }
            }

            int spanningShellStrokes = path.HasCrossLayerShellContinuity
                ? Math.Max(1, shellStrokeLayers.Count(pair => pair.Value.Count > 1))
                : shellStrokeLayers.Count(pair => pair.Value.Count > 1);
            if (spanningShellStrokes == 0 || companionRoles.Count == 0)
                return false;

            string companions = string.Join(
                ", ",
                companionRoles
                    .OrderBy(role => (int)role)
                    .Select(WasperPathRoleMetadata.RoleName));
            warning =
                $"{spanningShellStrokes} continuous Shell stroke(s) span multiple layers while {companions} paths remain separate.\n" +
                "This mixed strategy is not automatically print-safe.\n" +
                "Interior paths may intersect or protrude through the rising Shell, lose support, or create nozzle collisions.\n" +
                "Inspect the complete path in Pp04 and adapt, trim, or regenerate the interior before producing machine code.";
            return true;
        }

        public static string TargetRoleNames(IList<int> targetRoles)
        {
            if (TargetsAllRoles(targetRoles))
                return "All paths";
            return string.Join(
                " + ",
                targetRoles.Select(TargetRoleName));
        }

        public static bool TryGetMaterialDensity(
            IGH_DataAccess da,
            int index,
            out double density)
        {
            density = 0.0;
            if (da == null) return false;

            IGH_Goo goo = null;
            if (!da.GetData(index, ref goo) || goo == null) return false;

            WasperMaterial material = null;
            if (goo is WasperMaterialGoo materialGoo)
                material = materialGoo.Value;
            else if (goo is GH_ObjectWrapper wrapper)
                material = wrapper.Value as WasperMaterial;

            if (material == null) return false;

            if (material.TryGetDouble("density", out density) && density > 0.0 &&
                !double.IsNaN(density) && !double.IsInfinity(density))
                return true;

            foreach (var property in material.Properties)
            {
                if (property.Key.IndexOf("density", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (double.TryParse(property.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out density) &&
                    density > 0.0 && !double.IsNaN(density) && !double.IsInfinity(density))
                    return true;
            }

            return false;
        }
        public static GH_Structure<GH_Point> ToPointStructure(DataTree<Point3d> tree)
        {
            var result = new GH_Structure<GH_Point>();
            if (tree == null) return result;
            foreach (GH_Path path in tree.Paths)
                foreach (Point3d point in tree.Branch(path))
                    result.Append(new GH_Point(point), path);
            return result;
        }

        public static GH_Structure<GH_Plane> ToPlaneStructure(DataTree<Plane> tree)
        {
            var result = new GH_Structure<GH_Plane>();
            if (tree == null) return result;
            foreach (GH_Path path in tree.Paths)
                foreach (Plane plane in tree.Branch(path))
                    result.Append(new GH_Plane(plane), path);
            return result;
        }

        public static GH_Structure<GH_Number> ToNumberStructure(DataTree<double> tree)
        {
            var result = new GH_Structure<GH_Number>();
            if (tree == null) return result;
            foreach (GH_Path path in tree.Paths)
                foreach (double value in tree.Branch(path))
                    result.Append(new GH_Number(value), path);
            return result;
        }

        public static DataTree<Point3d> ToPointTree(GH_Structure<GH_Point> s)
        {
            var t = new DataTree<Point3d>();
            if (s == null) return t;
            for (int i = 0; i < s.PathCount; i++)
            {
                var path = s.Paths[i];
                foreach (var item in s.Branches[i])
                    if (item != null) t.Add(item.Value, path);
            }
            return t;
        }

        public static DataTree<Plane> ToPlaneTree(GH_Structure<GH_Plane> s)
        {
            var t = new DataTree<Plane>();
            if (s == null) return t;
            for (int i = 0; i < s.PathCount; i++)
            {
                var path = s.Paths[i];
                foreach (var item in s.Branches[i])
                    if (item != null) t.Add(item.Value, path);
            }
            return t;
        }

        public static DataTree<double> ToDoubleTree(GH_Structure<GH_Number> s)
        {
            var t = new DataTree<double>();
            if (s == null) return t;
            for (int i = 0; i < s.PathCount; i++)
            {
                var path = s.Paths[i];
                foreach (var item in s.Branches[i])
                    t.Add(item != null ? item.Value : double.NaN, path);
            }
            return t;
        }

        /// <summary>Generic goo tree → doubles (numbers, booleans as 1/0, parseable strings).</summary>
        public static DataTree<double> ToDoubleTreeLoose(GH_Structure<IGH_Goo> s)
        {
            var t = new DataTree<double>();
            if (s == null) return t;
            for (int i = 0; i < s.PathCount; i++)
            {
                var path = s.Paths[i];
                foreach (var item in s.Branches[i])
                {
                    if (item == null) { t.Add(double.NaN, path); continue; }
                    double v = double.NaN;
                    if (!GH_Convert.ToDouble(item, out v, GH_Conversion.Both))
                        v = double.NaN;
                    t.Add(v, path);
                }
            }
            return t;
        }

        /// <summary>
        /// Flattens a custom start/end G-code text tree into clean lines:
        /// items may contain embedded newlines; blank lines are dropped.
        /// Returns null when nothing usable is supplied (python _flatten_gcode_input).
        /// </summary>
        public static List<string> FlattenGcodeText(GH_Structure<GH_String> s)
        {
            if (s == null || s.IsEmpty) return null;
            var lines = new List<string>();
            foreach (var branch in s.Branches)
            {
                foreach (var item in branch)
                {
                    if (item == null || item.Value == null) continue;
                    string str = item.Value;
                    if (str.IndexOf('\n') >= 0)
                    {
                        foreach (var ln in str.Split('\n'))
                        {
                            var clean = ln.TrimEnd('\r');
                            if (clean.Trim().Length > 0) lines.Add(clean);
                        }
                    }
                    else if (str.Trim().Length > 0)
                    {
                        lines.Add(str);
                    }
                }
            }
            return lines.Count > 0 ? lines : null;
        }
    }
}
