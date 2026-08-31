#region Component Description
/*
Component: wsp_Ro01_Robot Targets (WASP LDM XL 3.0)
Nickname: LDM XL Targets
Category: WASPer_3DP
SubCategory: 5.1_Robot Gcode
Version:
    Uses the compiled assembly version in the component message via _versionTag.

Converts an LDM WasperPrintPath into native Robots CartesianTarget objects. The
targets carry KUKA process commands for the WASP XL extruder:
  FLOW_RATE = E / dt [mm/s]
  $OUT[1] = TRUE/FALSE

Constant speed and flow values are represented by shared Robots properties and
commands are emitted only at curve starts or actual process changes. Z-hop planes
are offset along positive World Z. Optional plane flipping reverses local Y/Z for
a downward-facing nozzle TCP while preserving a right-handed plane.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino.Geometry;

using Robots;
using Robots.Commands;

namespace WASPer_3DP.Components._5_1_Robot_Gcode
{
    public sealed class wsp_Ro01_Robot_Targets_WASP_LDM_XL_3_0 : GH_Component
    {
        private const double DefaultPrintSpeedMmMin = 7000.0;
        private const double DefaultTravelSpeedMmMin = 8000.0;
        private const double DefaultZHopSpeedMmMin = 6000.0;
        private const double DefaultFeedDiameterMm = 6.0;
        private const double MaxFlowRateMmSec = 9000.0 / 60.0;
        private const double FlowTolerance = 0.001;
        private const double NumericTolerance = 1e-9;
        private const double ZoneQuantizationMm = 0.1;

        private readonly string _versionTag;
        private readonly Dictionary<long, Speed> _speedCache = new Dictionary<long, Speed>();
        private readonly Dictionary<long, Zone> _zoneCache = new Dictionary<long, Zone>();

        public wsp_Ro01_Robot_Targets_WASP_LDM_XL_3_0()
            : base(
                "wsp_Ro01_Robot Targets (WASP LDM XL 3.0)",
                "LDM XL Targets",
                "Converts an LDM wsp_path into native Robots CartesianTarget objects for the " +
                "WASP LDM XL Extruder 3.0 with KUKAflow. Print moves are linear; travel is " +
                "joint; z-hop moves upward in positive World Z. Target planes can be flipped " +
                "for a downward-facing nozzle TCP. KUKA commands set " +
                "FLOW_RATE in mm/s and $OUT[1] only when the extrusion state or rate changes.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "5.1_Robot Gcode")
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version != null
                ? $"v{version.Major}.{version.Minor}.{version.Build}"
                : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("9482F4BF-2E76-4E4E-A122-769B6DDD88FA");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ro01_Robot Targets (WASP LDM XL 3.0).png"))
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "wasper_path", "wsp_path",
                "WASPer Print Path containing aligned points, point planes, flows and layer heights. " +
                "Optional packed print_speed, layer_w/layer_wf and nozzle_diam metadata is used.",
                GH_ParamAccess.item);

            p.AddGenericParameter(
                "3dp_params", "3dp_params",
                "LDM WASPer 3DP Params. Print/travel/z-hop speeds are read in mm/min; generated " +
                "Robots speeds and KUKA FLOW_RATE are converted to mm/s.",
                GH_ParamAccess.item);

            p.AddBooleanParameter(
                "raw_target", "raw",
                "Boolean input: False (default) outputs production targets with motions, speeds, zones, FLOW_RATE, " +
                "$OUT[1], and comments. True outputs a RAW Robot target with coordinate-only CartesianTargets and default " +
                "Robots properties for manual downstream configuration.",
                GH_ParamAccess.item,
                false);

            p.AddBooleanParameter(
                "flip_normals", "flip",
                "Boolean input: True (default) rotates every incoming WASPer point plane 180 degrees around its " +
                "local X-axis, preserving X while reversing Y and Z. Use this when the tool TCP +Z " +
                "points outward through the nozzle toward the print surface. False preserves the " +
                "incoming point-plane orientation.",
                GH_ParamAccess.item,
                true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "wasper_path", "wsp_path",
                "WASPer Print Path passed through for downstream use. When flip is True, its " +
                "pt_planes are replaced with the same flipped planes used by robot_target.",
                GH_ParamAccess.item);

            p.AddGenericParameter(
                "robot_target", "robot_target",
                "Native Robots CartesianTarget tree preserving the wsp_path branch structure. " +
                "Uses a generic parameter to avoid a hard Robots.Grasshopper UI-assembly dependency. Connect directly " +
                "to Robots components and flatten before Create Program when one ordered target list is required.",
                GH_ParamAccess.tree);

            p.AddNumberParameter(
                "flow_rate", "f_rate",
                "Effective calculated KUKA FLOW_RATE in mm/s for every robot_target item, " +
                "with identical tree paths and item counts. Print-move targets receive " +
                "f_rate = flow * nozzle_diameter * layer_h * print_speed / " +
                "(60 * pi * (fillament_multi / 2)^2). Approach, travel, z-hop, and " +
                "non-extruding targets receive 0.",
                GH_ParamAccess.tree);

            p.AddTextParameter(
                "prev_krl", "prev_krl",
                "Inspection-only KRL-style preview of the generated targets. Coordinates use the raw " +
                "target planes and PTP configuration/speed are unresolved; use Robots Create Program " +
                "Code for validated executable KRL.",
                GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            _speedCache.Clear();
            _zoneCache.Clear();

            WasperPrintPath path;
            if (!WasperGcodeTreeUtil.TryGetPrintPath(DA, 0, out path) || path == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "'wsp_path' must be a valid WASPer Print Path.");
                return;
            }

            Wasper3dpParams parameters = ReadParams(DA, 1);
            if (parameters == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "'3dp_params' must be a valid WASPer 3DP Params object.");
                return;
            }

            if (parameters.Process != Wasper3dpProcess.LDM)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Ro01 supports LDM parameters for the WASP LDM XL Extruder 3.0 / KUKAflow workflow.");
                return;
            }

            if (!ValidatePath(path)) return;

            bool rawTarget = false;
            DA.GetData(2, ref rawTarget);
            bool flip = true;
            DA.GetData(3, ref flip);

            WasperPrintPath outputPath = flip
                ? ClonePathWithPlanes(path, FlipPlaneTree(path.PtPlanes))
                : path;
            DA.SetData(0, new WasperPrintPathGoo(outputPath));
            if (flip)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "Plane normals were flipped for robot targets and the output wsp_path.");
            }

            double nozzleDiameter = PositiveOrFallback(
                parameters.NozzleDiameter, path.NozzleDiam ?? 0.0);
            if (nozzleDiameter <= 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "A positive nozzle_diameter is required in 3dp_params or wsp_path.");
                return;
            }

            double feedDiameter = PositiveOrFallback(parameters.FillamentMulti, DefaultFeedDiameterMm);
            double feedArea = Math.PI * Math.Pow(feedDiameter * 0.5, 2.0);
            double travelSpeed = PositiveOrFallback(parameters.TravelSpeed, DefaultTravelSpeedMmMin);
            double zHopSpeed = PositiveOrFallback(parameters.ZHopSpeed, DefaultZHopSpeedMmMin);
            double representativeLayerHeight = PositiveMean(path.LayerH);
            double zHop = parameters.ZHop.HasValue && IsFinite(parameters.ZHop.Value)
                ? Math.Max(0.0, parameters.ZHop.Value)
                : 0.0;
            if (zHop <= NumericTolerance)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "z_hop = 0: no Z-hop targets will be generated. Input a value > 0 in 3dp_params to enable positive-Z hops.");
            }
            DataTree<double> speedTree = parameters.HasPrintSpeed
                ? parameters.PrintSpeed
                : path.PrintSpeed;

            var targetTree = new DataTree<Target>();
            var flowRateTree = new DataTree<double>();
            var previewTree = new DataTree<string>();
            var allTargets = new List<Target>();
            var sourcePaths = path.Points.Paths;
            int commonPrefix = WasperGcodeTreeUtil.CommonPathPrefixLength(sourcePaths);
            var curveCountByLayer = new Dictionary<int, int>();
            var warnedFields = new HashSet<string>();
            int validCurves = 0;

            foreach (GH_Path branchPath in sourcePaths)
            {
                IList<Point3d> points = path.Points.Branch(branchPath);
                if (points == null || points.Count < 2)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"Skipped branch {branchPath}: at least two points are required.");
                    continue;
                }

                if (!path.PtPlanes.PathExists(branchPath) ||
                    path.PtPlanes.Branch(branchPath).Count != points.Count)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        $"Branch {branchPath} must contain one valid pt_plane per point.");
                    return;
                }

                IList<Plane> sourcePlanes = path.PtPlanes.Branch(branchPath);
                if (sourcePlanes.Any(plane => !plane.IsValid))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        $"Branch {branchPath} contains an invalid pt_plane.");
                    return;
                }
                List<Plane> planes = flip
                    ? sourcePlanes.Select(FlipPlaneForExtrusion).ToList()
                    : new List<Plane>(sourcePlanes);

                List<double> flows = ResolveBranch(
                    path.Flows, branchPath, points.Count, 1.0, "flows", warnedFields);
                List<double> heights = ResolveBranch(
                    path.LayerH, branchPath, points.Count, representativeLayerHeight,
                    "layer_h", warnedFields);
                List<double> speeds = ResolveBranch(
                    speedTree, branchPath, points.Count, DefaultPrintSpeedMmMin,
                    "print_speed", warnedFields);
                List<double> widths = ResolveWidthBranch(
                    path, parameters, branchPath, points.Count, nozzleDiameter, warnedFields);

                var segments = new SegmentProcess[points.Count - 1];
                for (int i = 1; i < points.Count; i++)
                {
                    if (!IsPositiveFinite(heights[i]) || !IsPositiveFinite(speeds[i]))
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                            $"Branch {branchPath} has non-positive layer_h or print_speed at point {i}.");
                        return;
                    }

                    double flow = IsFinite(flows[i]) ? Math.Max(0.0, flows[i]) : 0.0;
                    double flowRate = flow * nozzleDiameter * heights[i] * speeds[i] /
                                      (60.0 * feedArea);
                    bool extruding = points[i - 1].DistanceTo(points[i]) > NumericTolerance &&
                                     flowRate > FlowTolerance;

                    if (flowRate > MaxFlowRateMmSec + FlowTolerance)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                            $"Branch {branchPath}, segment {i}: FLOW_RATE {Fmt(flowRate)} mm/s " +
                            $"exceeds the WASP XL limit {Fmt(MaxFlowRateMmSec)} mm/s " +
                            "(9000 mm/min converted to mm/s).");
                        return;
                    }

                    segments[i - 1] = new SegmentProcess(
                        extruding, flowRate, speeds[i] / 60.0);
                }

                int layer = WasperGcodeTreeUtil.LayerFromPath(branchPath, commonPrefix);
                int curve;
                if (!curveCountByLayer.TryGetValue(layer, out curve)) curve = 0;
                curveCountByLayer[layer] = curve + 1;
                bool firstCurveInLayer = curve == 0;
                double branchLayerHeight = heights.Where(IsPositiveFinite).DefaultIfEmpty(
                    representativeLayerHeight).Average();

                var curveTargets = new List<Target>();
                var curveFlowRates = new List<double>();
                AddCurveTargets(
                    curveTargets, curveFlowRates, planes, points, segments, widths,
                    travelSpeed / 60.0, zHopSpeed / 60.0, zHop,
                    layer + 1, curve + 1, branchLayerHeight, firstCurveInLayer);

                if (curveFlowRates.Count != curveTargets.Count)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        $"Internal FLOW_RATE alignment error in branch {branchPath}: " +
                        $"{curveFlowRates.Count} rates for {curveTargets.Count} targets.");
                    return;
                }

                List<Target> outputTargets = rawTarget
                    ? curveTargets.Select(ToRawTarget).ToList()
                    : curveTargets;
                targetTree.AddRange(outputTargets, branchPath);
                flowRateTree.AddRange(curveFlowRates, branchPath);
                previewTree.AddRange(BuildKrlPreview(outputTargets, branchPath), branchPath);
                allTargets.AddRange(outputTargets);
                validCurves++;
            }

            if (allTargets.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "No valid robot targets were generated.");
                return;
            }

            DA.SetDataTree(1, targetTree);
            DA.SetDataTree(2, flowRateTree);
            DA.SetDataTree(3, previewTree);

            Message = $"{_versionTag} - {allTargets.Count} {(rawTarget ? "raw " : string.Empty)}targets" +
                      (flip ? " - flipped" : string.Empty);
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                $"Generated {allTargets.Count} Robots targets in {validCurves} tree branches. " +
                (rawTarget
                    ? "Raw mode removes commands, assigned speeds, zones, and motion settings. "
                    : "FLOW_RATE is mm/s; process-change targets use stop zones. ") +
                "Assign the production tool and frame with Robots components before Create Program.");
        }

        private static List<string> BuildKrlPreview(IReadOnlyList<Target> targets, GH_Path path)
        {
            var code = new List<string>
            {
                "; WASPer Ro01 KRL PREVIEW ONLY - branch " + path,
                "; Raw target planes; tool, frame, robot kinematics, S/T bits and PTP timing are unresolved."
            };

            Speed currentSpeed = null;
            Zone currentZone = null;

            for (int i = 0; i < targets.Count; i++)
            {
                var target = targets[i] as CartesianTarget;
                if (target == null) continue;

                List<Command> commands = FlattenPreviewCommands(target.Command).ToList();
                foreach (Command command in commands.Where(command => command.RunBefore))
                    code.Add(PreviewCommandCode(command));

                if (!ReferenceEquals(currentSpeed, target.Speed))
                {
                    if (target.Motion == Motions.Linear)
                    {
                        code.Add("$VEL.CP=" + Fmt(target.Speed.TranslationSpeed / 1000.0) +
                                 " ; " + Fmt(target.Speed.TranslationSpeed) + " mm/s");
                    }
                    else
                    {
                        code.Add("; PTP speed request " + Fmt(target.Speed.TranslationSpeed) +
                                 " mm/s - resolved by Robots kinematics");
                    }
                    currentSpeed = target.Speed;
                }

                if (target.Zone.IsFlyBy && !ReferenceEquals(currentZone, target.Zone))
                {
                    code.Add("$APO.CDIS=" + Fmt(target.Zone.Distance));
                    currentZone = target.Zone;
                }

                double[] pose = PlaneToKukaPreview(target.Plane);
                string position = "{X " + Fmt(pose[0]) +
                                  ",Y " + Fmt(pose[1]) +
                                  ",Z " + Fmt(pose[2]) +
                                  ",A " + Fmt(pose[3]) +
                                  ",B " + Fmt(pose[4]) +
                                  ",C " + Fmt(pose[5]) + "}";
                string motion = target.Motion == Motions.Joint ? "PTP" : "LIN";
                string approximation = target.Zone.IsFlyBy
                    ? (target.Motion == Motions.Joint ? " C_PTP" : " C_DIS")
                    : string.Empty;

                code.Add(motion + " " + position + approximation + " ; target " + i);

                foreach (Command command in commands.Where(command => !command.RunBefore))
                    code.Add(PreviewCommandCode(command));
            }

            return code;
        }

        private static Target ToRawTarget(Target target)
        {
            var cartesian = target as CartesianTarget;
            return cartesian == null
                ? target
                : new CartesianTarget(cartesian.Plane);
        }

        private static double[] PlaneToKukaPreview(Plane plane)
        {
            double m00 = plane.XAxis.X;
            double m10 = plane.XAxis.Y;
            double m20 = plane.XAxis.Z;
            double m01 = plane.YAxis.X;
            double m11 = plane.YAxis.Y;
            double m21 = plane.YAxis.Z;
            double m22 = plane.ZAxis.Z;

            double a = Math.Atan2(-m10, m00);
            double multiplier = Math.Max(0.0, 1.0 - m20 * m20);
            double b = Math.Atan2(m20, Math.Sqrt(multiplier));
            double c = Math.Atan2(-m21, m22);

            const double singularTolerance = 1e-12;
            if (m20 < -1.0 + singularTolerance)
            {
                a = Math.Atan2(m01, m11);
                b = -Math.PI * 0.5;
                c = 0.0;
            }
            else if (m20 > 1.0 - singularTolerance)
            {
                a = Math.Atan2(m01, m11);
                b = Math.PI * 0.5;
                c = 0.0;
            }

            const double radiansToDegrees = 180.0 / Math.PI;
            return new[]
            {
                plane.OriginX, plane.OriginY, plane.OriginZ,
                -a * radiansToDegrees, -b * radiansToDegrees, -c * radiansToDegrees
            };
        }

        private static IEnumerable<Command> FlattenPreviewCommands(Command command)
        {
            if (command == null || ReferenceEquals(command, Command.Default)) yield break;

            var group = command as Group;
            if (group != null)
            {
                foreach (Command child in group)
                    foreach (Command flattened in FlattenPreviewCommands(child))
                        yield return flattened;
                yield break;
            }

            yield return command;
        }

        private static string PreviewCommandCode(Command command)
        {
            var output = command as KukaOutputCommand;
            if (output != null) return output.PreviewCode;

            var flow = command as KukaFlowRateCommand;
            if (flow != null) return flow.PreviewCode;

            var comment = command as KukaCommentCommand;
            if (comment != null) return comment.PreviewCode;

            return "; Unsupported preview command: " + command;
        }

        private void AddCurveTargets(
            List<Target> targets,
            List<double> targetFlowRates,
            IList<Plane> planes,
            IList<Point3d> points,
            SegmentProcess[] segments,
            IList<double> widths,
            double travelSpeed,
            double zHopSpeed,
            double zHop,
            int layerNumber,
            int curveNumber,
            double layerHeight,
            bool firstCurveInLayer)
        {
            Plane start = planes[0];
            Plane startHop = OffsetAlongPositiveWorldZ(start, zHop);

            var approachBefore = new List<Command>
            {
                new KukaOutputCommand(false, true),
                new KukaFlowRateCommand(0.0, true)
            };

            if (firstCurveInLayer)
            {
                approachBefore.Add(new KukaCommentCommand(
                    $"===== Layer {layerNumber} | height (repr.) {Fmt(layerHeight)} mm =====", true));
            }
            approachBefore.Add(new KukaCommentCommand(
                $"--- Curve {curveNumber} / Layer {layerNumber}", true));

            targets.Add(new CartesianTarget(
                zHop > NumericTolerance ? startHop : start,
                motion: Motions.Joint,
                speed: GetSpeed(travelSpeed),
                zone: Zone.Default,
                command: MakeGroup(approachBefore)));
            targetFlowRates.Add(0.0);

            var startAfter = CommandsForTransition(
                new SegmentProcess(false, 0.0, segments[0].SpeedMmSec), segments[0], false);

            if (zHop > NumericTolerance)
            {
                targets.Add(new CartesianTarget(
                    start,
                    motion: Motions.Linear,
                    speed: GetSpeed(zHopSpeed),
                    zone: Zone.Default,
                    command: MakeGroup(startAfter)));
                targetFlowRates.Add(0.0);
            }
            else
            {
                ReplaceLastCommand(targets, MakeGroup(approachBefore.Concat(startAfter).ToList()));
            }

            for (int i = 1; i < points.Count; i++)
            {
                SegmentProcess current = segments[i - 1];
                bool isEnd = i == points.Count - 1;
                var after = new List<Command>();

                if (isEnd)
                {
                    if (current.Extruding)
                        after.Add(new KukaOutputCommand(false));
                    after.Add(new KukaFlowRateCommand(0.0));
                    after.Add(new KukaCommentCommand(
                        $"--- End of Curve {curveNumber} / Layer {layerNumber}"));
                }
                else
                {
                    after.AddRange(CommandsForTransition(current, segments[i], false));
                }

                double zoneDistance = after.Count > 0
                    ? 0.0
                    : AdaptiveZone(points, widths, i);

                targets.Add(new CartesianTarget(
                    planes[i],
                    motion: Motions.Linear,
                    speed: GetSpeed(current.SpeedMmSec),
                    zone: GetZone(zoneDistance),
                    command: MakeGroup(after)));
                targetFlowRates.Add(
                    current.Extruding
                        ? current.FlowRateMmSec
                        : 0.0);
            }

            if (zHop > NumericTolerance)
            {
                Plane endHop = OffsetAlongPositiveWorldZ(planes[planes.Count - 1], zHop);
                targets.Add(new CartesianTarget(
                    endHop,
                    motion: Motions.Linear,
                    speed: GetSpeed(zHopSpeed),
                    zone: Zone.Default));
                targetFlowRates.Add(0.0);
            }
        }

        private static void ReplaceLastCommand(List<Target> targets, Command command)
        {
            var oldTarget = (CartesianTarget)targets[targets.Count - 1];
            targets[targets.Count - 1] = new CartesianTarget(
                oldTarget.Plane, oldTarget.Configuration, oldTarget.Motion,
                oldTarget.Tool, oldTarget.Speed, oldTarget.Zone, command,
                oldTarget.Frame, oldTarget.External, oldTarget.ExternalCustom);
        }

        private static List<Command> CommandsForTransition(
            SegmentProcess current, SegmentProcess next, bool runBefore)
        {
            var commands = new List<Command>();

            if (!current.Extruding && next.Extruding)
            {
                commands.Add(new KukaOutputCommand(true, runBefore));
                commands.Add(new KukaFlowRateCommand(next.FlowRateMmSec, runBefore));
            }
            else if (current.Extruding && !next.Extruding)
            {
                commands.Add(new KukaOutputCommand(false, runBefore));
                commands.Add(new KukaFlowRateCommand(0.0, runBefore));
            }
            else if (current.Extruding && next.Extruding &&
                     Math.Abs(current.FlowRateMmSec - next.FlowRateMmSec) > FlowTolerance)
            {
                commands.Add(new KukaFlowRateCommand(next.FlowRateMmSec, runBefore));
            }

            return commands;
        }

        private static Command MakeGroup(IReadOnlyList<Command> commands)
        {
            return commands == null || commands.Count == 0
                ? Command.Default
                : new Group(commands);
        }

        private Speed GetSpeed(double speedMmSec)
        {
            double safe = Math.Max(speedMmSec, 0.001);
            long key = Quantize(safe, 1e-6);
            Speed value;
            if (!_speedCache.TryGetValue(key, out value))
            {
                value = new Speed(safe);
                _speedCache.Add(key, value);
            }
            return value;
        }

        private Zone GetZone(double distance)
        {
            if (distance <= NumericTolerance) return Zone.Default;

            // Quantize downward so simplification never enlarges the adaptive
            // blend radius allowed by local width and adjacent segment lengths.
            double quantizedDistance =
                Math.Floor((distance + NumericTolerance) / ZoneQuantizationMm) *
                ZoneQuantizationMm;
            if (quantizedDistance <= NumericTolerance) return Zone.Default;

            long key = Quantize(quantizedDistance, ZoneQuantizationMm);
            Zone value;
            if (!_zoneCache.TryGetValue(key, out value))
            {
                value = new Zone(quantizedDistance);
                _zoneCache.Add(key, value);
            }
            return value;
        }

        private static double AdaptiveZone(
            IList<Point3d> points, IList<double> widths, int pointIndex)
        {
            if (pointIndex <= 0 || pointIndex >= points.Count - 1) return 0.0;
            double previousLength = points[pointIndex - 1].DistanceTo(points[pointIndex]);
            double nextLength = points[pointIndex].DistanceTo(points[pointIndex + 1]);
            double width = pointIndex < widths.Count && IsPositiveFinite(widths[pointIndex])
                ? widths[pointIndex]
                : 0.0;
            if (width <= 0.0) return 0.0;
            return Math.Max(0.0, Math.Min(0.25 * width,
                Math.Min(0.5 * previousLength, 0.5 * nextLength)));
        }

        private List<double> ResolveWidthBranch(
            WasperPrintPath path,
            Wasper3dpParams parameters,
            GH_Path branchPath,
            int count,
            double nozzleDiameter,
            HashSet<string> warnedFields)
        {
            double fallback = PositiveOrFallback(parameters.LayerW, nozzleDiameter * 1.5);
            if (path.LayerWf != null && path.LayerWf.BranchCount > 0)
                return ResolveBranch(path.LayerWf, branchPath, count, fallback, "layer_wf", warnedFields);
            if (path.LayerW != null && path.LayerW.BranchCount > 0)
                return ResolveBranch(path.LayerW, branchPath, count, fallback, "layer_w", warnedFields);
            return Enumerable.Repeat(fallback, count).ToList();
        }

        private List<double> ResolveBranch(
            DataTree<double> tree,
            GH_Path path,
            int count,
            double fallback,
            string fieldName,
            HashSet<string> warnedFields)
        {
            if (tree == null || tree.BranchCount == 0)
                return Enumerable.Repeat(fallback, count).ToList();

            if (tree.DataCount == 1)
                return Enumerable.Repeat(tree.AllData()[0], count).ToList();

            if (!tree.PathExists(path) || tree.Branch(path).Count == 0)
            {
                WarnOnce(warnedFields, fieldName + ":missing",
                    $"Some '{fieldName}' branches are missing; the representative/default value is used.");
                return Enumerable.Repeat(fallback, count).ToList();
            }

            IList<double> source = tree.Branch(path);
            if (source.Count == count) return new List<double>(source);
            if (source.Count == 1) return Enumerable.Repeat(source[0], count).ToList();

            WarnOnce(warnedFields, fieldName + ":count",
                $"Some '{fieldName}' branch lengths do not match wsp_path points; " +
                "the branch's first value is broadcast.");
            return Enumerable.Repeat(source[0], count).ToList();
        }

        private void WarnOnce(HashSet<string> warnings, string key, string message)
        {
            if (warnings.Add(key))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, message);
        }

        private bool ValidatePath(WasperPrintPath path)
        {
            var missing = new List<string>();
            if (!path.HasPoints) missing.Add("points");
            if (!path.HasPlanes) missing.Add("pt_planes");
            if (!path.HasFlows) missing.Add("flows");
            if (!path.HasLayerH) missing.Add("layer_h");

            if (missing.Count == 0) return true;
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                "wsp_path is missing required data: " + string.Join(", ", missing) + ".");
            return false;
        }

        private Wasper3dpParams ReadParams(IGH_DataAccess DA, int index)
        {
            IGH_Goo goo = null;
            if (!DA.GetData(index, ref goo) || goo == null) return null;
            var typed = goo as Wasper3dpParamsGoo;
            if (typed != null) return typed.Value;
            var wrapper = goo as GH_ObjectWrapper;
            if (wrapper != null) return wrapper.Value as Wasper3dpParams;
            Wasper3dpParams value = null;
            return goo.CastTo(out value) ? value : null;
        }

        private static Plane OffsetAlongPositiveWorldZ(Plane plane, double distance)
        {
            Plane result = plane;
            result.Origin += Vector3d.ZAxis * Math.Max(0.0, distance);
            return result;
        }

        private static Plane FlipPlaneForExtrusion(Plane plane) =>
            new Plane(plane.Origin, plane.XAxis, -plane.YAxis);

        private static DataTree<Plane> FlipPlaneTree(DataTree<Plane> source)
        {
            var result = new DataTree<Plane>();
            foreach (GH_Path path in source.Paths)
                result.AddRange(source.Branch(path).Select(FlipPlaneForExtrusion), path);
            return result;
        }

        private static WasperPrintPath ClonePathWithPlanes(
            WasperPrintPath source,
            DataTree<Plane> planes)
        {
            return new WasperPrintPath(
                source.Points,
                planes,
                source.Flows,
                source.LayerH,
                printSpeed: source.PrintSpeed,
                printLoc: source.PrintLoc,
                printGlob: source.PrintGlob,
                supportPts: source.SupportPts,
                supportVects: source.SupportVects,
                angles: source.Angles,
                contactWidths: source.ContactWidths,
                riskMaterial: source.RiskMaterial,
                riskComb: source.RiskComb,
                load: source.Load,
                capacity: source.Capacity,
                nozzleDiam: source.NozzleDiam,
                dRatio: source.DRatio,
                dLoaded: source.DLoaded,
                bendRatio: source.BendRatio,
                spanClass: source.SpanClass,
                spanLen: source.SpanLen,
                collapsed: source.Collapsed,
                cascade: source.Cascade,
                collapseGen: source.CollapseGen,
                layerW: source.LayerW,
                layerWf: source.LayerWf,
                printVol: source.PrintVol,
                torn: source.Torn,
                interfaceRatio: source.InterfaceRatio,
                overturnRatio: source.OverturnRatio,
                failureFlags: source.FailureFlags,
                pathRoles: source.PathRoles,
                layerPlanes: source.LayerPlanes);
        }

        private static double PositiveMean(DataTree<double> tree)
        {
            if (tree == null) return 0.0;
            var values = tree.AllData().Where(IsPositiveFinite).ToList();
            return values.Count == 0 ? 0.0 : values.Average();
        }

        private static double PositiveOrFallback(double? value, double fallback)
        {
            return value.HasValue && IsPositiveFinite(value.Value) ? value.Value : fallback;
        }

        private static bool IsPositiveFinite(double value) => IsFinite(value) && value > 0.0;
        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        private static long Quantize(double value, double tolerance) =>
            (long)Math.Round(value / tolerance, MidpointRounding.AwayFromZero);
        private static string Fmt(double value) =>
            value.ToString("0.###", CultureInfo.InvariantCulture);

        private sealed class SegmentProcess
        {
            public SegmentProcess(bool extruding, double flowRateMmSec, double speedMmSec)
            {
                Extruding = extruding;
                FlowRateMmSec = flowRateMmSec;
                SpeedMmSec = speedMmSec;
            }

            public bool Extruding { get; }
            public double FlowRateMmSec { get; }
            public double SpeedMmSec { get; }
        }

        private sealed class KukaOutputCommand : Robots.Commands.Custom
        {
            private readonly bool _enabled;

            public KukaOutputCommand(bool enabled, bool runBefore = false)
                : base(
                    "WASP_OUT_1",
                    Manufacturers.KUKA,
                    "$OUT[1] = " + (enabled ? "TRUE" : "FALSE"),
                    runBefore: runBefore)
            {
                _enabled = enabled;
            }

            public override string ToString() =>
                $"KUKA Extrusion {(_enabled ? "ON" : "OFF")}";

            public string PreviewCode => "$OUT[1] = " + (_enabled ? "TRUE" : "FALSE");
        }

        private sealed class KukaFlowRateCommand : Robots.Commands.Custom
        {
            private readonly double _flowRate;

            public KukaFlowRateCommand(double flowRate, bool runBefore = false)
                : base(
                    "WASP_FLOW_RATE",
                    Manufacturers.KUKA,
                    "FLOW_RATE=" + Fmt(Math.Max(0.0, flowRate)),
                    runBefore: runBefore)
            {
                _flowRate = Math.Max(0.0, flowRate);
            }

            public override string ToString() =>
                $"KUKA FLOW_RATE {Fmt(_flowRate)} mm/s";

            public string PreviewCode => "FLOW_RATE=" + Fmt(_flowRate);
        }

        private sealed class KukaCommentCommand : Robots.Commands.Custom
        {
            private readonly string _comment;

            public KukaCommentCommand(string comment, bool runBefore = false)
                : base(
                    "WASP_COMMENT",
                    Manufacturers.KUKA,
                    "; " + SanitizeComment(comment),
                    runBefore: runBefore)
            {
                _comment = SanitizeComment(comment);
            }

            public override string ToString() => "KUKA Comment";

            public string PreviewCode => "; " + _comment;

            private static string SanitizeComment(string comment) =>
                (comment ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
