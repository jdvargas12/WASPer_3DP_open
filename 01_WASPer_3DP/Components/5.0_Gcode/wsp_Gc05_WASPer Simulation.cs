#region Component Description
/*
Component: wsp_Gc05_WASPer Simulation
Nickname: WASPer Sim
Category: WASPer_3DP
SubCategory: 5.0_Gcode

Animates either the resolved print, travel, and Z-hop motion plan carried by
the wsp_path input, or the separate Robots program input in Robot mode. Its playback
interaction was created based on the original Program Simulation component from
Robots, while the core implementation uses optional reflection and remains
loadable without Robots. Its wsp_path output contains the deposited state at the
current simulation time and is explicitly marked partial.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Reflection;
using System.Windows.Forms;

using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino.Geometry;

using WASPer_3DP;


namespace WASPer_3DP.Components._5_0_Gcode
{
    public sealed class wsp_Gc05_WASPer_Simulation : GH_Component
    {
        private WasperSimulationTimeline _timeline;
        private WasperPlaybackForm _form;
        private DateTime? _lastTick;
        private double _timeSeconds;
        private double _durationSeconds;
        private double _lastPreviewTime = 1.0;
        private double _lastStartTime;
        private double _startTimeSeconds;
        private bool _lastNormalized = true;
        private bool _hasInputState;
        private bool _restartFromStartOnPlay = true;
        private object _lastSource;
        private bool _showAllOutputs;
        private const string ShowAllOutputsKey = "wsp_gc15_show_all_outputs";

        internal double PlaybackSpeed { get; set; } = 1.0;

        public wsp_Gc05_WASPer_Simulation()
            : base(
                "wsp_Gc05_WASPer Simulation",
                "WASPer Sim",
                "Animates Cartesian or Delta 3D-printer motion from wsp_path, or the separate " +
                "Robots program input when printer=2. Printer modes 0/1 use plane " +
                "and print_dims; Robot mode uses the robot-system meshes. Use the Playback button " +
                "for play, stop, speed, and timeline controls. Created based on the original " +
                "Program Simulation component from Robots; optional Program support is detected " +
                "without creating a hard Robots dependency in the WASPer core. The wsp_path " +
                "output is the deposited current-state path and carries IsPartial=true. " +
                "By default only printer_meshes, wsp_path, and sim_path are shown. Right-click " +
                "and enable Show all outputs to expose motion diagnostics, job KPIs, and the " +
                "common outgoing-path debug fields. The chosen layout is saved with the definition.\r\n\r\n" +
                "Please use the Pp01 WASPer Path from Curves before using this component. " +
                "For Cartesian and Delta simulation, enrich that path through Gc03 first.",
                WASPerPalette.DesignFabrication,
                "5.0_Gcode")
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            Message = version == null
                ? "v1.0.x"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
        }

        public override Guid ComponentGuid =>
            new Guid("8F635BE2-5C2B-43D8-92D7-617B4A40F315");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon => WasperPlayIcon.Bitmap;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            int pathIndex = p.AddGenericParameter(
                "wasper_path",
                "wsp_path",
                "Gc03-enriched WASPer Print Path containing nozzle, speed, travel, " +
                "Z-hop, and motion-plan data. Required for Cartesian/Delta simulation. " +
                "Please use the Pp01 WASPer Path from Curves before using this component. Then enrich the path through Gc03. " +
                "Optional in Robot mode, where it can pass through with sim_path to Pp04.",
                GH_ParamAccess.item);
            p[pathIndex].Optional = true;

            int programIndex = p.AddGenericParameter(
                "robot_program",
                "program",
                "Optional Robots Program used only when printer=2. Connect the Program " +
                "output from Robots Create Program. Leave empty for Cartesian/Delta modes.",
                GH_ParamAccess.item);
            p[programIndex].Optional = true;

            p.AddPlaneParameter(
                "origin_plane",
                "plane",
                "Printer origin and orientation. Cartesian: minimum XYZ build-volume " +
                "corner. Delta: center of the circular print bed. Plane Z is the build " +
                "direction. Example: World XY places a Cartesian printer from (0,0,0) " +
                "toward +X,+Y,+Z, or centers a Delta bed at (0,0,0).",
                GH_ParamAccess.item,
                Plane.WorldXY);

            p.AddIntegerParameter(
                "printer",
                "printer",
                "Simulation family: 0 = Cartesian fixed-bed gantry, 1 = Delta, " +
                "2 = Robot.",
                GH_ParamAccess.item,
                0);

            int dimensionsIndex = p.AddGenericParameter(
                "print_dimensions",
                "print_dims",
                "Maximum gross print dimensions in path/model units. Accepts either a " +
                "Grasshopper number list or one comma-separated text value. Cartesian " +
                "requires [X, Y, Z], for example the list {220; 220; 250} or the text " +
                "\"220, 220, 250\". Delta requires [diameter, height], for example the " +
                "list {300; 400} or the text \"300, 400\".",
                GH_ParamAccess.list);
            p[dimensionsIndex].Optional = true;

            p.AddNumberParameter(
                "preview_time",
                "prev_time",
                "Time shown while playback is not running. It is also the position restored " +
                "by the Stop button. With normalized=true, use 0 to 1 (for example, 1 shows " +
                "the completed simulation and 0.5 shows its halfway-time state). With " +
                "normalized=false, enter seconds (for example, 120 shows the state at " +
                "120 seconds).",
                GH_ParamAccess.item,
                1.0);

            p.AddNumberParameter(
                "start_time",
                "s_time",
                "Time from which playback begins when Play is pressed after loading, changing " +
                "an input, or pressing Stop. With normalized=true, use 0 to 1 (for example, " +
                "0 starts at the beginning and 0.25 starts one quarter through the total " +
                "duration). With normalized=false, enter seconds. Seeking with the Playback " +
                "slider overrides this start position so playback can continue from the " +
                "manually selected time.",
                GH_ParamAccess.item,
                0.0);

            p.AddBooleanParameter(
                "normalized",
                "normalized",
                "Treat time as normalized progress from 0 to 1. Example: true for a " +
                "0-to-1 slider; false when time is supplied directly in seconds.",
                GH_ParamAccess.item,
                true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddMeshParameter(
                "printer_meshes",
                "printer_meshes",
                "Lightweight posed printer meshes at the current simulation time.",
                GH_ParamAccess.list);

            p.AddGenericParameter(
                "wasper_path",
                "wsp_path",
                "Partial WASPer Print Path representing deposition completed at the current " +
                "simulation time. The output carries IsPartial=true and retains matching " +
                "per-point metadata for the deposited prefix. In Robot mode, deposition is " +
                "trimmed using the matched Robots target sequence when available.",
                GH_ParamAccess.item);

            p.AddGenericParameter(
                "sim_path",
                "sim_path",
                "Simulation value for Pp04. Cartesian/Delta modes output normalized " +
                "deposited-path progress. Robot mode passes through the animated Robots " +
                "Program so Pp04 can use its existing target-to-wsp_path matching and " +
                "exclude home, approach, travel, and hop movements from the printed result.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            IGH_Goo pathGoo = null;
            IGH_Goo programGoo = null;
            da.GetData(0, ref pathGoo);
            da.GetData(1, ref programGoo);

            Plane origin = Plane.WorldXY;
            int printerValue = 0;
            double previewTime = 1.0;
            double startTime = 0.0;
            bool normalized = true;

            if (!da.GetData(2, ref origin) || !origin.IsValid)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "plane must be a valid printer-origin plane.");
                return;
            }

            da.GetData(3, ref printerValue);
            if (printerValue < 0 || printerValue > 2)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "printer must be 0 (Cartesian), 1 (Delta), or 2 (Robot).");
                return;
            }

            var family = (WasperPrinterFamily)printerValue;
            da.GetData(5, ref previewTime);
            da.GetData(6, ref startTime);
            da.GetData(7, ref normalized);

            bool hasPath =
                WasperGcodeTreeUtil.TryGetPrintPath(da, 0, out WasperPrintPath path) &&
                path != null &&
                path.HasPoints;
            bool hasRobotProgram =
                WasperRobotProgramAdapter.TryCreate(
                    programGoo,
                    out WasperRobotProgramAdapter robotProgram);

            if (family == WasperPrinterFamily.Robot)
            {
                if (!hasRobotProgram)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        "printer=2 requires the Program output from Robots Create Program.");
                    return;
                }

                SolveRobotProgram(
                    da,
                    robotProgram,
                    hasPath ? path : null,
                    previewTime,
                    startTime,
                    normalized);
                return;
            }

            if (!hasPath)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "printer 0/1 requires a valid Gc03-enriched WASPer Print Path " +
                    "connected to wsp_path. Please use the Pp01 WASPer Path from Curves before using this component. Then enrich the path through Gc03.");
                return;
            }

            var dimensions = new List<double>();
            if (!TryReadDimensions(da, 4, dimensions))
                return;

            if (family == WasperPrinterFamily.Delta && dimensions.Count == 3)
            {
                double ignored = dimensions[2];
                dimensions.RemoveAt(2);
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"Delta print_dims uses only [diameter, height]. The third value " +
                    $"({ignored.ToString("G", CultureInfo.InvariantCulture)}) was ignored.");
            }

            int expectedDimensions = family == WasperPrinterFamily.Delta ? 2 : 3;
            if (!ValidateDimensions(dimensions, expectedDimensions))
                return;

            if (!path.HasMotionPlan)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "wsp_path has no resolved travel motion data. Use the wsp_path output " +
                    "from Gc03 Marlin Gcode for full printer simulation.");
                Pause();
                return;
            }

            if (_timeline == null || !ReferenceEquals(_timeline.Plan, path.MotionPlan))
                _timeline = new WasperSimulationTimeline(path);

            if (!PreparePlayback(
                    path.MotionPlan,
                    _timeline.DurationSeconds,
                    previewTime,
                    startTime,
                    normalized,
                    "The Gc03 motion plan has no positive-duration movements."))
            {
                return;
            }

            WasperSimulationPose pose = _timeline.Evaluate(_timeSeconds);
            double nozzleDiameter = ResolveNozzleDiameter(path, dimensions, family);

            double tolerance = Math.Max(
                Rhino.RhinoMath.ZeroTolerance,
                Math.Min(dimensions[0], dimensions[dimensions.Count - 1]) * 1e-8);
            int outsideCount = WasperPrinterEnvelope.CountOutside(
                path.MotionPlan,
                origin,
                family,
                dimensions,
                tolerance);
            if (outsideCount > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{outsideCount} motion endpoint(s) lie outside print_dims. Check plane, " +
                    "printer family, model units, and machine dimensions.");
            }

            if (!path.TravelSpeed.HasValue || path.TravelSpeed.Value <= 0.0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "wsp_path has no valid resolved travel speed. Use the wsp_path output from Gc03.");
            }

            WarnForUnsupportedToolOrientations(path, origin);

            double representativeDimension = family == WasperPrinterFamily.Delta
                ? Math.Min(dimensions[0], dimensions[1])
                : Math.Min(dimensions[0], Math.Min(dimensions[1], dimensions[2]));
            if (nozzleDiameter > representativeDimension * 0.1)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Nozzle diameter is unusually large relative to print_dims. Check model units " +
                    "and printer dimensions.");
            }

            List<Mesh> meshes = WasperPrinterMeshFactory.Create(
                family,
                origin,
                dimensions,
                pose.Position,
                nozzleDiameter);
            var nozzlePlane = new Plane(
                pose.Position,
                origin.XAxis,
                origin.YAxis);

            da.SetDataList(0, meshes);
            int completedPointCount = (int)Math.Round(
                pose.PathProgress * path.PointCount,
                MidpointRounding.AwayFromZero);
            bool hasPartialPrintPoint =
                pose.MotionType == WasperMotionType.Print &&
                pose.MotionIndex >= 0 &&
                pose.MotionIndex < path.MotionPlan.Count;
            WasperMotion activeMotion = hasPartialPrintPoint
                ? path.MotionPlan.Motions[pose.MotionIndex]
                : null;
            WasperPrintPath partialPath = CreatePartialPath(
                path,
                completedPointCount,
                activeMotion?.BranchIndex ?? -1,
                activeMotion?.PointIndex ?? -1,
                hasPartialPrintPoint ? pose.Position : (Point3d?)null);

            da.SetData(1, new WasperPrintPathGoo(partialPath));
            WasperPathDebugOutputs.SetCore(da, this, partialPath);
            da.SetData(2, pose.PathProgress);
            SetOptionalData(da, "nozzle_plane", nozzlePlane);
            SetOptionalData(da, "motion", pose.MotionType.ToString());
            SetOptionalData(da, "index", pose.MotionIndex);
            SetOptionalData(da, "progress", pose.TimeProgress);
            SetOptionalData(da, "seconds", pose.CurrentTimeSeconds);
            SetJobKpiOutputs(da, path);

            Message = $"{family} | {pose.TimeProgress:P0}";
            _form?.SetProgress(pose.TimeProgress);
            UpdatePlayback();
        }

        private void SolveRobotProgram(
            IGH_DataAccess da,
            WasperRobotProgramAdapter program,
            WasperPrintPath path,
            double previewTime,
            double startTime,
            bool normalized)
        {
            if (!program.TryGetSimulationState(
                    out bool hasSimulation,
                    out double duration,
                    out _,
                    out _,
                    out string stateError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, stateError);
                return;
            }

            if (!hasSimulation)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "The Robots Program has no simulation data.");
                return;
            }

            _timeline = null;
            if (!PreparePlayback(
                    program.ProgramObject,
                    duration,
                    previewTime,
                    startTime,
                    normalized,
                    "The Robots Program has no positive simulation duration."))
            {
                return;
            }

            if (!program.TryAnimate(
                    _timeSeconds,
                    out List<Mesh> meshes,
                    out string animationError))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    animationError);
                Pause();
                return;
            }

            if (!program.TryGetSimulationState(
                    out _,
                    out duration,
                    out double currentTime,
                    out int targetIndex,
                    out stateError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, stateError);
                return;
            }

            double progress = duration > Rhino.RhinoMath.ZeroTolerance
                ? Rhino.RhinoMath.Clamp(currentTime / duration, 0.0, 1.0)
                : 1.0;
            double depositionProgress = progress;
            string motion = "Robot";
            WasperRobotSimulationCut robotCut = null;

            if (path != null)
            {
                var pointBranches = new List<List<Point3d>>(path.Points.BranchCount);
                for (int branchIndex = 0;
                     branchIndex < path.Points.BranchCount;
                     branchIndex++)
                {
                    IList<Point3d> sourceBranch = path.Points.Branches[branchIndex];
                    pointBranches.Add(sourceBranch != null
                        ? new List<Point3d>(sourceBranch)
                        : new List<Point3d>());
                }

                double tolerance = Rhino.RhinoDoc.ActiveDoc != null
                    ? Rhino.RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
                    : Rhino.RhinoMath.SqrtEpsilon;

                if (WasperGcodeTreeUtil.TryGetRobotSimulationCut(
                        program,
                        pointBranches,
                        tolerance,
                        out robotCut,
                        out string mappingError))
                {
                    depositionProgress = robotCut.Progress;
                    motion = robotCut.HasPartialPoint ? "Print" : "Travel";

                    if (robotCut.MatchedPointCount < robotCut.TotalPointCount)
                    {
                        AddRuntimeMessage(
                            GH_RuntimeMessageLevel.Warning,
                            $"Robots simulation matched {robotCut.MatchedPointCount}/" +
                            $"{robotCut.TotalPointCount} ordered wsp_path points. Printing " +
                            "progress is reliable through the matched prefix; verify that " +
                            "the same wsp_path and point order generated the program.");
                    }
                }
                else
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        mappingError + " Gc05 cannot identify deposition from robot motion, " +
                        "but Pp04 can still retry the mapping from the Program output.");
                }
            }

            Plane tcpPlane = Plane.Unset;
            program.TryGetLastPlane(0, out tcpPlane);

            da.SetDataList(0, meshes);
            if (path != null)
            {
                int completedPointCount = robotCut != null
                    ? robotCut.CompletedPointCount
                    : (int)Math.Floor(depositionProgress * path.PointCount);
                WasperPrintPath partialPath = CreatePartialPath(
                    path,
                    completedPointCount,
                    robotCut?.PartialBranchIndex ?? -1,
                    robotCut?.PartialPointIndex ?? -1,
                    robotCut != null && robotCut.HasPartialPoint
                        ? robotCut.PartialPoint
                        : (Point3d?)null);
                da.SetData(1, new WasperPrintPathGoo(partialPath));
                WasperPathDebugOutputs.SetCore(da, this, partialPath);
            }
            // Keep the live Program on sim_path in Robot mode. Pp04 already knows
            // how to match its targets against wsp_path points, including the
            // partially deposited current segment, while ignoring non-print moves.
            da.SetData(2, program.ProgramObject);
            if (tcpPlane.IsValid)
                SetOptionalData(da, "nozzle_plane", tcpPlane);
            SetOptionalData(da, "motion", motion);
            SetOptionalData(da, "index", targetIndex);
            SetOptionalData(da, "progress", progress);
            SetOptionalData(da, "seconds", currentTime);
            SetJobKpiOutputs(da, path);

            Message = path == null
                ? $"Robot | time {progress:P0}"
                : $"Robot | print {depositionProgress:P0} | time {progress:P0}";
            _form?.SetProgress(progress);
            UpdatePlayback();
        }

        private void SetJobKpiOutputs(IGH_DataAccess da, WasperPrintPath path)
        {
            if (path == null) return;
            if (path.KpiTimeMin.HasValue) SetOptionalData(da, "p_time_min", path.KpiTimeMin.Value);
            if (path.KpiPathLength.HasValue) SetOptionalData(da, "path_length", path.KpiPathLength.Value);
            if (path.KpiVolume.HasValue) SetOptionalData(da, "p_vol", path.KpiVolume.Value);
            if (path.KpiMassKg.HasValue) SetOptionalData(da, "p_mass", path.KpiMassKg.Value);
            if (path.KpiLayers.HasValue) SetOptionalData(da, "layers", path.KpiLayers.Value);
            SetOptionalData(da, "kpi_units", KpiUnitLabel(path.KpiUnits));
        }

        private void SetOptionalData(IGH_DataAccess da, string nickName, object value)
        {
            int index = WasperPathDebugOutputs.OutputIndex(this, nickName);
            if (index >= 0)
                da.SetData(index, value);
        }

        private static string KpiUnitLabel(int? units)
        {
            return units switch
            {
                1 => "cm",
                2 => "m",
                _ => "mm"
            };
        }

        private static WasperPrintPath CreatePartialPath(
            WasperPrintPath source,
            int completedPointCount,
            int partialBranchIndex,
            int partialPointIndex,
            Point3d? partialPoint)
        {
            if (source == null)
                return null;

            int branchCount = source.Points?.BranchCount ?? 0;
            var retainedCounts = new int[branchCount];
            var points = new DataTree<Point3d>();
            int remaining = Math.Max(0, Math.Min(completedPointCount, source.PointCount));

            for (int b = 0; b < branchCount; b++)
            {
                GH_Path path = source.Points.Paths[b];
                points.EnsurePath(path);
                IList<Point3d> sourceBranch = source.Points.Branches[b];
                int sourceCount = sourceBranch?.Count ?? 0;
                int take = Math.Min(sourceCount, remaining);
                retainedCounts[b] = take;
                for (int i = 0; i < take; i++)
                    points.Add(sourceBranch[i], path);
                remaining -= take;
            }

            bool appendPartial =
                partialPoint.HasValue &&
                partialPoint.Value.IsValid &&
                partialBranchIndex >= 0 &&
                partialBranchIndex < branchCount;
            if (appendPartial)
            {
                GH_Path path = source.Points.Paths[partialBranchIndex];
                IList<Point3d> branch = points.Branch(path);
                Point3d candidate = partialPoint.Value;
                if (branch == null ||
                    branch.Count == 0 ||
                    branch[branch.Count - 1].DistanceTo(candidate) >
                        Rhino.RhinoMath.SqrtEpsilon)
                {
                    points.Add(candidate, path);
                    retainedCounts[partialBranchIndex]++;
                }
                else
                {
                    appendPartial = false;
                }
            }

            return new WasperPrintPath(
                points,
                TrimPlanes(
                    source.PtPlanes,
                    source.Points,
                    retainedCounts,
                    appendPartial ? partialBranchIndex : -1,
                    partialPointIndex,
                    partialPoint),
                TrimTree(source.Flows, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                TrimTree(source.LayerH, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                printSpeed: TrimTree(source.PrintSpeed, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                printLoc: TrimTree(source.PrintLoc, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                printGlob: TrimTree(source.PrintGlob, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                supportPts: TrimTree(source.SupportPts, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                supportVects: TrimTree(source.SupportVects, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                angles: TrimTree(source.Angles, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                contactWidths: TrimTree(source.ContactWidths, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                riskMaterial: TrimTree(source.RiskMaterial, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                riskComb: TrimTree(source.RiskComb, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                load: TrimTree(source.Load, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                capacity: TrimTree(source.Capacity, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                nozzleDiam: source.NozzleDiam,
                dRatio: TrimTree(source.DRatio, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                dLoaded: TrimTree(source.DLoaded, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                bendRatio: TrimTree(source.BendRatio, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                spanClass: TrimTree(source.SpanClass, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                spanLen: TrimTree(source.SpanLen, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                collapsed: TrimTree(source.Collapsed, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                cascade: TrimTree(source.Cascade, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                collapseGen: TrimTree(source.CollapseGen, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                layerW: TrimTree(source.LayerW, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                layerWf: TrimTree(source.LayerWf, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                printVol: TrimTree(source.PrintVol, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                torn: TrimTree(source.Torn, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                interfaceRatio: TrimTree(source.InterfaceRatio, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                overturnRatio: TrimTree(source.OverturnRatio, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                failureFlags: TrimTree(source.FailureFlags, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                travelSpeed: source.TravelSpeed,
                zHop: source.ZHop,
                zHopSpeed: source.ZHopSpeed,
                kpiUnits: source.KpiUnits,
                kpiSegmentLength: TrimTree(source.KpiSegmentLength, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                kpiPrintSpeed: TrimTree(source.KpiPrintSpeed, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                kpiPrintVol: TrimTree(source.KpiPrintVol, source.Points, retainedCounts, appendPartial ? partialBranchIndex : -1, partialPointIndex),
                isPartial: true,
                pathRoles: WasperGcodeTreeUtil.FilterPathRoles(
                    source.PathRoles,
                    points.Paths),
                layerPlanes: WasperGcodeTreeUtil.FilterLayerPlanes(
                    source.LayerPlanes,
                    points.Paths,
                    WasperGcodeTreeUtil.CommonPathPrefixLength(
                        source.Points.Paths)));
        }

        private static DataTree<T> TrimTree<T>(
            DataTree<T> source,
            DataTree<Point3d> sourcePoints,
            int[] retainedCounts,
            int partialBranchIndex,
            int partialPointIndex)
        {
            if (source == null || sourcePoints == null)
                return null;

            var result = new DataTree<T>();
            bool globalScalar = source.BranchCount == 1 &&
                (source.Branches[0]?.Count ?? 0) == 1;
            T scalar = globalScalar ? source.Branches[0][0] : default;

            for (int b = 0; b < sourcePoints.BranchCount; b++)
            {
                GH_Path path = sourcePoints.Paths[b];
                result.EnsurePath(path);
                IList<T> branch = FindBranch(source, path, b);
                int count = b < retainedCounts.Length ? retainedCounts[b] : 0;
                int ordinaryCount = b == partialBranchIndex
                    ? Math.Max(0, count - 1)
                    : count;

                for (int i = 0; i < ordinaryCount; i++)
                {
                    if (globalScalar) result.Add(scalar, path);
                    else if (branch != null && i < branch.Count) result.Add(branch[i], path);
                }

                if (b == partialBranchIndex)
                {
                    if (globalScalar) result.Add(scalar, path);
                    else if (branch != null && branch.Count > 0)
                    {
                        int index = Math.Max(0, Math.Min(partialPointIndex, branch.Count - 1));
                        result.Add(branch[index], path);
                    }
                }
            }

            return result;
        }

        private static DataTree<Plane> TrimPlanes(
            DataTree<Plane> source,
            DataTree<Point3d> sourcePoints,
            int[] retainedCounts,
            int partialBranchIndex,
            int partialPointIndex,
            Point3d? partialPoint)
        {
            DataTree<Plane> result = TrimTree(
                source,
                sourcePoints,
                retainedCounts,
                partialBranchIndex,
                partialPointIndex);
            if (result == null ||
                !partialPoint.HasValue ||
                partialBranchIndex < 0 ||
                partialBranchIndex >= sourcePoints.BranchCount)
            {
                return result;
            }

            GH_Path path = sourcePoints.Paths[partialBranchIndex];
            IList<Plane> branch = result.Branch(path);
            if (branch != null && branch.Count > 0)
            {
                Plane plane = branch[branch.Count - 1];
                plane.Origin = partialPoint.Value;
                branch[branch.Count - 1] = plane;
            }
            return result;
        }

        private static IList<T> FindBranch<T>(
            DataTree<T> source,
            GH_Path path,
            int branchIndex)
        {
            if (source == null || source.BranchCount == 0)
                return null;
            if (source.PathExists(path))
                return source.Branch(path);
            return branchIndex >= 0 && branchIndex < source.BranchCount
                ? source.Branches[branchIndex]
                : null;
        }

        private bool PreparePlayback(
            object source,
            double durationSeconds,
            double previewTime,
            double startTime,
            bool normalized,
            string invalidDurationMessage)
        {
            if (double.IsNaN(durationSeconds) ||
                double.IsInfinity(durationSeconds) ||
                durationSeconds <= Rhino.RhinoMath.ZeroTolerance)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    invalidDurationMessage);
                Pause();
                return false;
            }

            if (double.IsNaN(previewTime) ||
                double.IsInfinity(previewTime) ||
                double.IsNaN(startTime) ||
                double.IsInfinity(startTime))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "prev_time and s_time must both be finite numbers.");
                Pause();
                return false;
            }

            if (!ReferenceEquals(_lastSource, source))
            {
                Pause();
                _lastSource = source;
                _hasInputState = false;
                _restartFromStartOnPlay = true;
            }

            _durationSeconds = durationSeconds;
            double previewSeconds = ToSimulationSeconds(
                previewTime,
                normalized,
                durationSeconds);
            double startSeconds = ToSimulationSeconds(
                startTime,
                normalized,
                durationSeconds);

            if (!_hasInputState ||
                Math.Abs(_lastPreviewTime - previewTime) > Rhino.RhinoMath.ZeroTolerance ||
                Math.Abs(_lastStartTime - startTime) > Rhino.RhinoMath.ZeroTolerance ||
                _lastNormalized != normalized)
            {
                Pause();
                _timeSeconds = previewSeconds;
                _startTimeSeconds = startSeconds;
                _lastPreviewTime = previewTime;
                _lastStartTime = startTime;
                _lastNormalized = normalized;
                _hasInputState = true;
                _restartFromStartOnPlay = true;
            }
            else
            {
                _startTimeSeconds = startSeconds;
            }

            if (_timeSeconds < 0.0 || _timeSeconds > durationSeconds)
            {
                _timeSeconds = Rhino.RhinoMath.Clamp(
                    _timeSeconds,
                    0.0,
                    durationSeconds);
                Pause();
            }

            return true;
        }

        private static double ToSimulationSeconds(
            double value,
            bool normalized,
            double durationSeconds)
        {
            double seconds = normalized
                ? Rhino.RhinoMath.Clamp(value, 0.0, 1.0) * durationSeconds
                : value;
            return Rhino.RhinoMath.Clamp(seconds, 0.0, durationSeconds);
        }

        private bool ValidateDimensions(List<double> dimensions, int expectedCount)
        {
            if (dimensions == null || dimensions.Count != expectedCount)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    expectedCount == 2
                        ? "Delta print_dims requires exactly 2 values: diameter, height."
                        : "Cartesian print_dims requires exactly 3 values: X, Y, Z.");
                return false;
            }

            for (int i = 0; i < dimensions.Count; i++)
            {
                double value = dimensions[i];
                if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        "Every print_dims value must be finite and greater than zero.");
                    return false;
                }
            }

            return true;
        }

        private bool TryReadDimensions(
            IGH_DataAccess da,
            int inputIndex,
            List<double> dimensions)
        {
            var items = new List<IGH_Goo>();
            if (!da.GetDataList(inputIndex, items) || items.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "print_dims is required. Supply a number list or comma-separated text.");
                return false;
            }

            foreach (IGH_Goo item in items)
            {
                if (item == null)
                    continue;

                if (item is GH_Number number)
                {
                    dimensions.Add(number.Value);
                    continue;
                }

                if (item is GH_Integer integer)
                {
                    dimensions.Add(integer.Value);
                    continue;
                }

                string text = item is GH_String textGoo
                    ? textGoo.Value
                    : item.ToString();
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                string[] tokens = text.Split(
                    new[] { ',', ';', '\r', '\n', '\t', ' ' },
                    StringSplitOptions.RemoveEmptyEntries);
                foreach (string token in tokens)
                {
                    if (double.TryParse(
                            token,
                            NumberStyles.Float,
                            CultureInfo.InvariantCulture,
                            out double value) ||
                        double.TryParse(
                            token,
                            NumberStyles.Float,
                            CultureInfo.CurrentCulture,
                            out value))
                    {
                        dimensions.Add(value);
                    }
                    else
                    {
                        AddRuntimeMessage(
                            GH_RuntimeMessageLevel.Error,
                            $"print_dims contains a value that is not numeric: \"{token}\".");
                        return false;
                    }
                }
            }

            if (dimensions.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "print_dims contains no numeric dimensions.");
                return false;
            }

            return true;
        }

        private double ResolveNozzleDiameter(
            WasperPrintPath path,
            IReadOnlyList<double> dimensions,
            WasperPrinterFamily family)
        {
            if (path.NozzleDiam.HasValue && path.NozzleDiam.Value > 0.0)
                return path.NozzleDiam.Value;

            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Warning,
                "wsp_path has no resolved nozzle diameter. Use the wsp_path output from Gc03 " +
                "with a valid nozzle_diameter in 3dp_params. A schematic fallback nozzle is shown.");

            double representative = family == WasperPrinterFamily.Delta
                ? Math.Min(dimensions[0], dimensions[1])
                : Math.Min(dimensions[0], Math.Min(dimensions[1], dimensions[2]));
            return representative * 0.005;
        }

        private void WarnForUnsupportedToolOrientations(
            WasperPrintPath path,
            Plane origin)
        {
            if (!path.HasPlanes)
                return;

            const double angularToleranceRadians = Math.PI / 90.0;
            int unsupported = 0;

            for (int branch = 0; branch < path.PtPlanes.BranchCount; branch++)
            {
                IList<Plane> planes = path.PtPlanes.Branches[branch];
                if (planes == null)
                    continue;

                for (int i = 0; i < planes.Count; i++)
                {
                    Plane plane = planes[i];
                    if (!plane.IsValid)
                        continue;

                    double angle = Vector3d.VectorAngle(
                        origin.ZAxis,
                        plane.ZAxis);
                    if (angle > angularToleranceRadians)
                    {
                        unsupported++;
                        break;
                    }
                }
            }

            if (unsupported > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "wsp_path contains non-parallel nozzle orientations. Cartesian and Delta " +
                    "simulation uses a fixed nozzle axis aligned with plane Z.");
            }
        }

        internal void TogglePlay()
        {
            if (_lastTick.HasValue)
            {
                Pause();
            }
            else
            {
                if (_restartFromStartOnPlay)
                {
                    _timeSeconds = _startTimeSeconds;
                    _restartFromStartOnPlay = false;
                }
                _lastTick = DateTime.Now;
                ExpireSolution(true);
            }
        }

        internal void StopPlayback()
        {
            Pause();
            if (_durationSeconds > Rhino.RhinoMath.ZeroTolerance)
            {
                _timeSeconds = ToSimulationSeconds(
                    _lastPreviewTime,
                    _lastNormalized,
                    _durationSeconds);
            }
            _restartFromStartOnPlay = true;
            ExpireSolution(true);
        }

        internal void SeekNormalized(double progress)
        {
            if (_durationSeconds <= Rhino.RhinoMath.ZeroTolerance)
                return;

            Pause();
            _timeSeconds =
                Rhino.RhinoMath.Clamp(progress, 0.0, 1.0) *
                _durationSeconds;
            _restartFromStartOnPlay = false;
            ExpireSolution(true);
        }

        private void Pause()
        {
            if (_form != null)
                _form.SetPlaying(false);
            _lastTick = null;
        }

        private void UpdatePlayback()
        {
            if (!_lastTick.HasValue)
                return;

            DateTime now = DateTime.Now;
            double delta = (now - _lastTick.Value).TotalSeconds;
            _lastTick = now;
            _timeSeconds += delta * PlaybackSpeed;

            if (_durationSeconds > Rhino.RhinoMath.ZeroTolerance &&
                (_timeSeconds <= 0.0 || _timeSeconds >= _durationSeconds))
            {
                _timeSeconds = Rhino.RhinoMath.Clamp(
                    _timeSeconds,
                    0.0,
                    _durationSeconds);
                Pause();
                _restartFromStartOnPlay = true;
            }

            ExpireSolution(true);
        }

        public override void CreateAttributes()
        {
            m_attributes = new WasperPlaybackAttributes(
                this,
                "Playback",
                TogglePlaybackForm);
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(
                menu,
                "Show all outputs",
                (_, _) =>
                {
                    RecordUndoEvent("Toggle Gc05 outputs");
                    _showAllOutputs = !_showAllOutputs;
                    RebuildOutputs();
                    ExpireSolution(true);
                },
                true,
                _showAllOutputs);
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetBoolean(ShowAllOutputsKey, _showAllOutputs);
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            // Rebuild the output topology BEFORE base.Read() restores saved wires, so the
            // optional outputs already exist when Grasshopper reconnects them (otherwise
            // saved wires to them are silently dropped on file open).
            _showAllOutputs =
                reader.ItemExists(ShowAllOutputsKey) &&
                reader.GetBoolean(ShowAllOutputsKey);
            RebuildOutputs();
            return base.Read(reader);
        }

        private void RebuildOutputs()
        {
            const int fixedOutputCount = 3;
            while (Params.Output.Count > fixedOutputCount)
            {
                Params.UnregisterOutputParameter(
                    Params.Output[Params.Output.Count - 1],
                    true);
            }

            if (_showAllOutputs)
            {
                RegisterExpandedOutputs();
                WasperPathDebugOutputs.RegisterCore(this);
            }

            Params.OnParametersChanged();
        }

        private void RegisterExpandedOutputs()
        {
            Params.RegisterOutputParam(new Grasshopper.Kernel.Parameters.Param_Plane
            {
                Name = "nozzle_plane",
                NickName = "nozzle_plane",
                Description = "Current nozzle plane. Its origin is the nozzle tip.",
                Access = GH_ParamAccess.item
            });
            Params.RegisterOutputParam(new Grasshopper.Kernel.Parameters.Param_String
            {
                Name = "motion",
                NickName = "motion",
                Description = "Current movement type: Print, Travel, or ZHop.",
                Access = GH_ParamAccess.item
            });
            Params.RegisterOutputParam(new Grasshopper.Kernel.Parameters.Param_Integer
            {
                Name = "motion_index",
                NickName = "index",
                Description = "Current index in the ordered Gc03 motion plan.",
                Access = GH_ParamAccess.item
            });
            Params.RegisterOutputParam(new Grasshopper.Kernel.Parameters.Param_Number
            {
                Name = "progress",
                NickName = "progress",
                Description = "Normalized elapsed motion time from 0 to 1.",
                Access = GH_ParamAccess.item
            });
            Params.RegisterOutputParam(new Grasshopper.Kernel.Parameters.Param_Number
            {
                Name = "time_seconds",
                NickName = "seconds",
                Description = "Current physical simulation time in seconds.",
                Access = GH_ParamAccess.item
            });
            Params.RegisterOutputParam(new Grasshopper.Kernel.Parameters.Param_Number
            {
                Name = "p_time_min",
                NickName = "p_time_min",
                Description = "Total estimated job time in minutes, packed by Gc03 when available.",
                Access = GH_ParamAccess.item
            });
            Params.RegisterOutputParam(new Grasshopper.Kernel.Parameters.Param_Number
            {
                Name = "path_length",
                NickName = "path_length",
                Description = "Total printed path length in the kpi_units selected in Gc03.",
                Access = GH_ParamAccess.item
            });
            Params.RegisterOutputParam(new Grasshopper.Kernel.Parameters.Param_Number
            {
                Name = "p_vol",
                NickName = "p_vol",
                Description = "Total deposited volume in the kpi_units volume unit selected in Gc03.",
                Access = GH_ParamAccess.item
            });
            Params.RegisterOutputParam(new Grasshopper.Kernel.Parameters.Param_Number
            {
                Name = "p_mass",
                NickName = "p_mass",
                Description = "Total deposited mass in kg, packed by Gc03 when a connected WASPer Material supplied density.",
                Access = GH_ParamAccess.item
            });
            Params.RegisterOutputParam(new Grasshopper.Kernel.Parameters.Param_Integer
            {
                Name = "layers",
                NickName = "layers",
                Description = "Total generated layer count packed by Gc03.",
                Access = GH_ParamAccess.item
            });
            Params.RegisterOutputParam(new Grasshopper.Kernel.Parameters.Param_String
            {
                Name = "kpi_units",
                NickName = "kpi_units",
                Description = "KPI unit label from Gc03: mm, cm, or m. G-code itself remains in machine mm/mm-min.",
                Access = GH_ParamAccess.item
            });
        }

        private void TogglePlaybackForm()
        {
            if (_form == null || _form.IsClosed)
                _form = new WasperPlaybackForm(this);

            if (_form.Visible)
            {
                _form.Visible = false;
                StopPlayback();
            }
            else
            {
                _form.ShowNearCursor();
            }
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            base.RemovedFromDocument(document);
            Pause();
            if (_form != null && !_form.IsClosed)
                _form.Visible = false;
        }
    }

    internal sealed class WasperPlaybackAttributes : GH_ComponentAttributes
    {
        private const int ButtonHeight = 18;
        private readonly string _label;
        private readonly Action _action;
        private RectangleF _buttonBounds;
        private bool _mouseDown;

        public WasperPlaybackAttributes(
            GH_Component owner,
            string label,
            Action action)
            : base(owner)
        {
            _label = label;
            _action = action;
        }

        protected override void Layout()
        {
            base.Layout();

            const int margin = 3;
            Rectangle bounds = GH_Convert.ToRectangle(Bounds);
            Rectangle button = bounds;
            button.X += margin;
            button.Width -= margin * 2;
            button.Y = bounds.Bottom;
            button.Height = ButtonHeight;
            bounds.Height += ButtonHeight + margin;

            Bounds = bounds;
            _buttonBounds = button;
        }

        protected override void Render(
            GH_Canvas canvas,
            Graphics graphics,
            GH_CanvasChannel channel)
        {
            base.Render(canvas, graphics, channel);

            if (channel != GH_CanvasChannel.Objects)
                return;

            Font prototype = GH_FontServer.StandardAdjusted;
            Font font = GH_FontServer.NewFont(
                prototype,
                6f / GH_GraphicsUtil.UiScale);
            int highlight = _mouseDown ? 0 : 8;

            using GH_Capsule button = GH_Capsule.CreateTextCapsule(
                _buttonBounds,
                _buttonBounds,
                GH_Palette.Black,
                _label,
                font,
                3,
                highlight);
            button.Render(graphics, false, Owner.Locked, false);
            font.Dispose();
        }

        public override GH_ObjectResponse RespondToMouseDown(
            GH_Canvas sender,
            GH_CanvasMouseEvent e)
        {
            if (!Owner.Locked &&
                e.Button == MouseButtons.Left &&
                _buttonBounds.Contains(e.CanvasLocation))
            {
                _mouseDown = true;
                sender.Invalidate();
                return GH_ObjectResponse.Capture;
            }

            return base.RespondToMouseDown(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseUp(
            GH_Canvas sender,
            GH_CanvasMouseEvent e)
        {
            if (_mouseDown)
            {
                _mouseDown = false;
                sender.Invalidate();
                if (_buttonBounds.Contains(e.CanvasLocation))
                    _action();
                return GH_ObjectResponse.Release;
            }

            return base.RespondToMouseUp(sender, e);
        }

        public override GH_ObjectResponse RespondToMouseMove(
            GH_Canvas sender,
            GH_CanvasMouseEvent e)
        {
            if (_mouseDown && !_buttonBounds.Contains(e.CanvasLocation))
            {
                _mouseDown = false;
                sender.Invalidate();
            }

            return base.RespondToMouseMove(sender, e);
        }
    }

    internal static class WasperPlayIcon
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

                using var background = new SolidBrush(Color.FromArgb(44, 98, 112));
                using var play = new SolidBrush(Color.White);
                graphics.FillEllipse(background, 2, 2, 20, 20);
                graphics.FillPolygon(
                    play,
                    new[]
                    {
                        new PointF(9, 7),
                        new PointF(9, 17),
                        new PointF(17, 12)
                    });
                return _bitmap;
            }
        }
    }
}
