#region Component Description
/*
Component: wsp_Ro05_Rotate TCP at Target
Nickname: rotate_tcp
Category: WASPer_3DP
SubCategory: 5.1_Robot Gcode

Rotates one Cartesian target plane around its own local Z normal while keeping
the TCP origin and normal fixed. A cosine-weighted blend can distribute the
rotation across neighboring Cartesian targets in the same data-tree branch.
Robots inverse kinematics then solves the affected sequence continuously.

tcp_rot is expressed in degrees:
    0   = no axial rotation
   90   = quarter-turn around local +Z
  -90   = opposite quarter-turn
  180   = half-turn

Positive angles follow the right-hand rule around each target's local Z axis.
All affected targets keep their original Linear, Process, or Joint motion.
*/
#endregion

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;

using Robots;

namespace WASPer_3DP.Components._5_1_Robot_Gcode
{
    public sealed class wsp_Ro05_Rotate_TCP_at_Target : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Ro05_Rotate_TCP_at_Target()
            : base(
                "wsp_Ro05_Rotate TCP at Target",
                "rotate_tcp",
                "Rotates a Cartesian target around its unchanged TCP origin and local Z " +
                "normal. A cosine blend can distribute tcp_rot across neighboring targets " +
                "in the same branch, and Robots solves the sequence continuously. " +
                "Example: tcp_rot=90 produces a quarter-turn around local +Z. Validate " +
                "singularities, joint limits, orientation transitions, and collisions in " +
                "Robots Program Simulation before running the program.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "5.1_Robot Gcode")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version != null
                ? $"v{version.Major}.{version.Minor}.{version.Build}"
                : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("FA01A922-D092-4998-92B7-F6211177864F");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ro05_Rotate TCP at Target.png"))
                    {
                        return stream != null
                            ? new System.Drawing.Bitmap(stream)
                            : null;
                    }
                }
                catch
                {
                    return null;
                }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "target", "target",
                "Ordered tree of native Robots targets. For t_index >= 0, the selected item " +
                "must be Cartesian. With t_index = -1, every Cartesian target is rotated. " +
                "Tree paths, order, motion, speed, zone, frame, external axes, and process " +
                "commands are preserved.",
                GH_ParamAccess.tree);

            p.AddIntegerParameter(
                "target_index", "t_index",
                "Zero-based index of the target to rotate in flattened fabrication order. " +
                "Index 0 selects the first target. Use -1 for global mode, which rotates " +
                "every Cartesian target and ignores blend. Original tree paths are retained.",
                GH_ParamAccess.item,
                0);

            p.AddGenericParameter(
                "robot", "robot",
                "Native Robots Robot System used for inverse kinematics. Use the same robot " +
                "definition and base plane used by Program Simulation and Create Program. " +
                "Ro05 currently supports one robot mechanical group.",
                GH_ParamAccess.item);

            p.AddGenericParameter(
                "tool", "tool",
                "Optional native Robots Tool override for the selected target and all " +
                "neighbors affected by blend. When disconnected, each target's stored tool " +
                "is used. A connected override is stored in every affected output target so " +
                "the TCP solutions remain consistent. In global mode it applies to every " +
                "Cartesian target.",
                GH_ParamAccess.item);
            p[3].Optional = true;

            p.AddNumberParameter(
                "tcp_rotation", "tcp_rot",
                "Axial TCP rotation in degrees around each affected target's local Z normal. " +
                "The TCP origin and Z normal remain unchanged; only the X/Y axes rotate. " +
                "Positive angles follow the right-hand rule. Examples: 90 = quarter-turn, " +
                "-90 = opposite quarter-turn, 180 = half-turn.",
                GH_ParamAccess.item,
                0.0);

            p.AddIntegerParameter(
                "blend_targets", "blend",
                "Number of neighboring Cartesian targets on each side of t_index over which " +
                "to taper tcp_rot with a cosine falloff. The selected target receives the " +
                "full rotation. Blending remains inside its data-tree branch and stops at a " +
                "JointTarget boundary. Use 0 to rotate only the selected target. This input " +
                "is ignored when t_index = -1.",
                GH_ParamAccess.item,
                5);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "target", "target",
                "Original target tree with the affected Cartesian targets rotated around " +
                "their own unchanged origins and local Z normals. With t_index = -1, every " +
                "Cartesian target is affected. " +
                "Original motion, speed, zone, frame, external axes, and process commands " +
                "are preserved.",
                GH_ParamAccess.tree);

            p.AddNumberParameter(
                "joints", "joints",
                "Coordinated robot-joint solutions in radians. Each target solution occupies " +
                "a branch formed by appending its zero-based item index to the original target " +
                "path. Selected mode outputs t_index only; global mode outputs every target. " +
                "Values are ordered J1, J2, J3, and so on.",
                GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            GH_Structure<IGH_Goo> inputTree;
            if (!DA.GetDataTree(0, out inputTree) ||
                inputTree == null ||
                inputTree.PathCount == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "'target' must contain a native Robots target tree.");
                return;
            }

            int targetIndex = 0;
            DA.GetData(1, ref targetIndex);

            IGH_Goo robotGoo = null;
            if (!DA.GetData(2, ref robotGoo) ||
                !TryReadValue(robotGoo, out RobotSystem robotSystem) ||
                robotSystem == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "'robot' must be a valid native Robots Robot System.");
                return;
            }

            Tool toolOverride = null;
            IGH_Goo toolGoo = null;
            if (DA.GetData(3, ref toolGoo) &&
                (!TryReadValue(toolGoo, out toolOverride) || toolOverride == null))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "'tool' must be a valid native Robots Tool when connected.");
                return;
            }

            double tcpRotationDegrees = 0.0;
            DA.GetData(4, ref tcpRotationDegrees);
            if (double.IsNaN(tcpRotationDegrees) ||
                double.IsInfinity(tcpRotationDegrees))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "'tcp_rot' must be a finite angle in degrees.");
                return;
            }

            int blendTargets = 5;
            DA.GetData(5, ref blendTargets);
            if (blendTargets < 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "'blend' must be zero or a positive integer.");
                return;
            }

            List<TargetEntry> entries;
            if (!TryReadTargetTree(inputTree, out entries))
                return;

            bool globalMode = targetIndex == -1;
            if (targetIndex < -1 || targetIndex >= entries.Count)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    $"'t_index' is {targetIndex}, but the flattened target tree contains " +
                    $"{entries.Count} target(s). Use -1 for global mode or an index from " +
                    $"0 to {entries.Count - 1}.");
                return;
            }

            CartesianTarget selected = globalMode
                ? null
                : entries[targetIndex].Target as CartesianTarget;
            if (!globalMode && selected == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    $"Target {targetIndex} is a JointTarget. Ro05 requires a CartesianTarget " +
                    "with a defined TCP plane.");
                return;
            }

            if (!HasSingleRobotGroup(robotSystem))
                return;

            double normalizedDegrees = NormalizeDegrees(tcpRotationDegrees);
            double fullRotationRadians = RhinoMath.ToRadians(normalizedDegrees);
            Dictionary<int, RotationEntry> rotations;
            try
            {
                rotations = globalMode
                    ? BuildGlobalRotationEntries(
                        entries,
                        fullRotationRadians,
                        toolOverride)
                    : BuildRotationEntries(
                        entries,
                        targetIndex,
                        blendTargets,
                        fullRotationRadians,
                        toolOverride);
            }
            catch (Exception exception)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    $"The blended TCP planes could not be created: {exception.Message}");
                return;
            }

            if (rotations.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "The target tree contains no Cartesian targets to rotate.");
                return;
            }

            int solveThroughIndex = globalMode
                ? entries.Count - 1
                : rotations.Keys.Max();
            Dictionary<int, CartesianTarget> modifiedTargets =
                new Dictionary<int, CartesianTarget>();
            Dictionary<int, KinematicSolution> outputSolutions =
                new Dictionary<int, KinematicSolution>();
            KinematicSolution selectedSolution = null;
            double[] previousJoints = null;

            try
            {
                for (int i = 0; i <= solveThroughIndex; i++)
                {
                    RotationEntry rotation;
                    Target sequenceTarget = rotations.TryGetValue(i, out rotation)
                        ? rotation.SolveTarget
                        : entries[i].Target;

                    KinematicSolution solution = SolveSingle(
                        robotSystem,
                        sequenceTarget,
                        previousJoints);

                    if (!ReportSolutionErrors(solution, i, "Blended posture"))
                        return;

                    if (rotation != null)
                    {
                        RobotConfigurations? outputConfiguration =
                            rotation.Source.Motion == Motions.Joint
                                ? solution.Configuration
                                : (RobotConfigurations?)null;

                        modifiedTargets[i] = CopyCartesian(
                            rotation.Source,
                            rotation.Plane,
                            outputConfiguration,
                            rotation.Tool);
                    }

                    if (i == targetIndex)
                        selectedSolution = solution;

                    if (globalMode || i == targetIndex)
                        outputSolutions[i] = solution;

                    previousJoints = solution.Joints;
                }
            }
            catch (Exception exception)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    $"The blended sequence could not be solved through target " +
                    $"{solveThroughIndex}: {exception.Message}");
                return;
            }

            if (!globalMode && selectedSolution == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    $"Robots returned no solution for selected target {targetIndex}.");
                return;
            }

            DataTree<Target> output = new DataTree<Target>();
            int flatIndex = 0;
            for (int branchIndex = 0; branchIndex < inputTree.PathCount; branchIndex++)
            {
                GH_Path path = inputTree.Paths[branchIndex];
                IList branch = inputTree.get_Branch(path);
                output.EnsurePath(path);

                for (int item = 0; item < branch.Count; item++)
                {
                    CartesianTarget modifiedTarget;
                    output.Add(
                        modifiedTargets.TryGetValue(flatIndex, out modifiedTarget)
                            ? modifiedTarget
                            : entries[flatIndex].Target,
                        path);
                    flatIndex++;
                }
            }

            DA.SetDataTree(0, output);
            DataTree<double> jointsOutput = new DataTree<double>();
            foreach (KeyValuePair<int, KinematicSolution> pair in outputSolutions)
            {
                TargetEntry entry = entries[pair.Key];
                GH_Path jointsPath = entry.Path.AppendElement(entry.ItemIndex);
                jointsOutput.EnsurePath(jointsPath);
                foreach (double joint in pair.Value.Joints)
                    jointsOutput.Add(joint, jointsPath);
            }
            DA.SetDataTree(1, jointsOutput);

            if (globalMode)
            {
                Message =
                    $"{_versionTag} | GLOBAL | {normalizedDegrees:0.###}° | " +
                    $"{modifiedTargets.Count}T";

                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"Global mode rotated all {modifiedTargets.Count} Cartesian target(s) " +
                    $"{normalizedDegrees:0.###}° around their own local Z axes. blend was " +
                    $"ignored. All {outputSolutions.Count} targets were solved sequentially.");
            }
            else
            {
                Message =
                    $"{_versionTag} | T{targetIndex} | {normalizedDegrees:0.###}° | " +
                    $"{modifiedTargets.Count}T";

                RotationEntry selectedRotation = rotations[targetIndex];
                double originDeviation = selectedRotation.Plane.Origin.DistanceTo(
                    selected.Plane.Origin);
                double normalDeviation = RhinoMath.ToDegrees(
                    Vector3d.VectorAngle(
                        selectedRotation.Plane.ZAxis,
                        selected.Plane.ZAxis));

                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"Target {targetIndex} TCP rotated {normalizedDegrees:0.###}° around local Z. " +
                    $"Cosine blending affected {modifiedTargets.Count} target(s) in branch " +
                    $"{entries[targetIndex].Path}, with up to {blendTargets} neighbor(s) per side. " +
                    $"Origin deviation: {originDeviation:0.######} model units; normal deviation: " +
                    $"{normalDeviation:0.######}°. Selected solution: " +
                    $"{selectedSolution.Configuration}.");
            }

            if (Math.Abs(normalizedDegrees) > 0.001)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Inspect the blended orientation transition and check for wrist flips, " +
                    "singularities, joint limits, and collisions in Program Simulation.");
            }

            if (!globalMode && selected.Motion != Motions.Joint)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"The selected target retained its original {selected.Motion} motion. " +
                    "A forced configuration is intentionally not stored because Robots " +
                    "ignores forced configurations on Linear/Process targets.");
            }

            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Remark,
                "Ro05 solves kinematics but does not perform collision checking. Validate " +
                "the complete program with Robots Program Simulation.");
        }

        private bool TryReadTargetTree(
            GH_Structure<IGH_Goo> tree,
            out List<TargetEntry> entries)
        {
            entries = new List<TargetEntry>();

            for (int branchIndex = 0; branchIndex < tree.PathCount; branchIndex++)
            {
                GH_Path path = tree.Paths[branchIndex];
                IList branch = tree.get_Branch(path);

                for (int item = 0; item < branch.Count; item++)
                {
                    IGH_Goo goo = branch[item] as IGH_Goo;
                    if (!TryReadValue(goo, out Target target) || target == null)
                    {
                        AddRuntimeMessage(
                            GH_RuntimeMessageLevel.Error,
                            $"'target' item {item} in branch {path} is not a valid native " +
                            "Robots target.");
                        return false;
                    }

                    entries.Add(new TargetEntry(
                        target,
                        branchIndex,
                        item,
                        path));
                }
            }

            if (entries.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "'target' contains no native Robots targets.");
                return false;
            }

            return true;
        }

        private Dictionary<int, RotationEntry> BuildGlobalRotationEntries(
            IReadOnlyList<TargetEntry> entries,
            double fullRotationRadians,
            Tool toolOverride)
        {
            Dictionary<int, RotationEntry> rotations =
                new Dictionary<int, RotationEntry>();

            for (int index = 0; index < entries.Count; index++)
            {
                if (!(entries[index].Target is CartesianTarget))
                    continue;

                AddRotationEntry(
                    rotations,
                    entries,
                    index,
                    0,
                    0,
                    fullRotationRadians,
                    toolOverride);
            }

            return rotations;
        }

        private Dictionary<int, RotationEntry> BuildRotationEntries(
            IReadOnlyList<TargetEntry> entries,
            int selectedIndex,
            int blendTargets,
            double fullRotationRadians,
            Tool toolOverride)
        {
            Dictionary<int, RotationEntry> rotations =
                new Dictionary<int, RotationEntry>();
            TargetEntry selectedEntry = entries[selectedIndex];

            AddRotationEntry(
                rotations,
                entries,
                selectedIndex,
                0,
                blendTargets,
                fullRotationRadians,
                toolOverride);

            bool continueBackward = true;
            bool continueForward = true;
            for (int distance = 1; distance <= blendTargets; distance++)
            {
                if (continueBackward)
                {
                    int index = selectedIndex - distance;
                    continueBackward = CanBlendTarget(
                        entries,
                        index,
                        selectedEntry.BranchIndex);
                    if (continueBackward)
                    {
                        AddRotationEntry(
                            rotations,
                            entries,
                            index,
                            distance,
                            blendTargets,
                            fullRotationRadians,
                            toolOverride);
                    }
                }

                if (continueForward)
                {
                    int index = selectedIndex + distance;
                    continueForward = CanBlendTarget(
                        entries,
                        index,
                        selectedEntry.BranchIndex);
                    if (continueForward)
                    {
                        AddRotationEntry(
                            rotations,
                            entries,
                            index,
                            distance,
                            blendTargets,
                            fullRotationRadians,
                            toolOverride);
                    }
                }
            }

            return rotations;
        }

        private static bool CanBlendTarget(
            IReadOnlyList<TargetEntry> entries,
            int index,
            int branchIndex)
        {
            return index >= 0 &&
                index < entries.Count &&
                entries[index].BranchIndex == branchIndex &&
                entries[index].Target is CartesianTarget;
        }

        private void AddRotationEntry(
            IDictionary<int, RotationEntry> rotations,
            IReadOnlyList<TargetEntry> entries,
            int index,
            int distance,
            int blendTargets,
            double fullRotationRadians,
            Tool toolOverride)
        {
            CartesianTarget source = entries[index].Target as CartesianTarget;
            double weight = distance == 0
                ? 1.0
                : 0.5 * (
                    1.0 + Math.Cos(
                        Math.PI * distance / (blendTargets + 1.0)));

            Plane rotatedPlane = source.Plane;
            if (!rotatedPlane.Rotate(
                fullRotationRadians * weight,
                source.Plane.ZAxis,
                source.Plane.Origin))
            {
                throw new InvalidOperationException(
                    $"Target {index} TCP plane could not be rotated around local Z.");
            }

            Tool resolvedTool = toolOverride ?? source.Tool;
            CartesianTarget solveTarget = CopyCartesian(
                source,
                rotatedPlane,
                configuration: null,
                resolvedTool);

            rotations.Add(
                index,
                new RotationEntry(
                    source,
                    rotatedPlane,
                    resolvedTool,
                    solveTarget,
                    weight));
        }

        private bool HasSingleRobotGroup(RobotSystem robotSystem)
        {
            IndustrialSystem industrial = robotSystem as IndustrialSystem;
            if (industrial != null && industrial.MechanicalGroups.Count != 1)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    $"Ro05 currently supports one robot mechanical group, but " +
                    $"'{robotSystem.Name}' contains {industrial.MechanicalGroups.Count} groups.");
                return false;
            }

            if (industrial == null && !(robotSystem is CobotSystem))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    $"Robot system type '{robotSystem.GetType().Name}' is not supported by Ro05.");
                return false;
            }

            return true;
        }

        private bool ReportSolutionErrors(
            KinematicSolution solution,
            int index,
            string label)
        {
            if (solution != null && solution.Errors.Count == 0)
                return true;

            string errors = solution == null
                ? "Robots returned no kinematic solution."
                : string.Join(" | ", solution.Errors.Distinct());

            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Error,
                $"{label} at target {index} has kinematic errors: {errors}");
            return false;
        }

        private static KinematicSolution SolveSingle(
            RobotSystem robotSystem,
            Target target,
            double[] previousJoints)
        {
            IReadOnlyList<double[]> previous = previousJoints == null
                ? null
                : new double[][] { previousJoints };

            List<KinematicSolution> solutions = robotSystem.Kinematics(
                new Target[] { target },
                previous);

            if (solutions == null || solutions.Count != 1)
            {
                throw new InvalidOperationException(
                    "Robots did not return exactly one kinematic solution.");
            }

            return solutions[0];
        }

        private static CartesianTarget CopyCartesian(
            CartesianTarget source,
            Plane plane,
            RobotConfigurations? configuration,
            Tool tool)
        {
            return new CartesianTarget(
                plane,
                configuration,
                source.Motion,
                tool,
                source.Speed,
                source.Zone,
                source.Command,
                source.Frame,
                source.External,
                source.ExternalCustom);
        }

        private static double NormalizeDegrees(double degrees)
        {
            degrees %= 360.0;
            if (degrees >= 180.0) degrees -= 360.0;
            if (degrees < -180.0) degrees += 360.0;
            return degrees;
        }

        private static bool TryReadValue<T>(IGH_Goo goo, out T result)
            where T : class
        {
            result = null;
            if (goo == null) return false;

            object value = goo;
            for (int depth = 0; depth < 5 && value is IGH_Goo current; depth++)
            {
                GH_ObjectWrapper wrapper = current as GH_ObjectWrapper;
                if (wrapper != null)
                {
                    value = wrapper.Value;
                    continue;
                }

                object scriptValue;
                try
                {
                    scriptValue = current.ScriptVariable();
                }
                catch
                {
                    scriptValue = null;
                }

                if (scriptValue == null || ReferenceEquals(scriptValue, value))
                    break;
                value = scriptValue;
            }

            result = value as T;
            if (result != null) return true;

            T castValue = null;
            if (goo.CastTo(out castValue) && castValue != null)
            {
                result = castValue;
                return true;
            }

            return false;
        }

        private sealed class TargetEntry
        {
            public TargetEntry(
                Target target,
                int branchIndex,
                int itemIndex,
                GH_Path path)
            {
                Target = target;
                BranchIndex = branchIndex;
                ItemIndex = itemIndex;
                Path = path;
            }

            public Target Target { get; }
            public int BranchIndex { get; }
            public int ItemIndex { get; }
            public GH_Path Path { get; }
        }

        private sealed class RotationEntry
        {
            public RotationEntry(
                CartesianTarget source,
                Plane plane,
                Tool tool,
                CartesianTarget solveTarget,
                double weight)
            {
                Source = source;
                Plane = plane;
                Tool = tool;
                SolveTarget = solveTarget;
                Weight = weight;
            }

            public CartesianTarget Source { get; }
            public Plane Plane { get; }
            public Tool Tool { get; }
            public CartesianTarget SolveTarget { get; }
            public double Weight { get; }
        }
    }
}
