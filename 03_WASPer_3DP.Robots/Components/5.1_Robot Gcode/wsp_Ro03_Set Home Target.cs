#region Component Description
/*
Component: wsp_Ro03_Set Home Target
Nickname: set_home
Category: WASPer_3DP
SubCategory: 5.1_Robot Gcode

Inserts arbitrary Cartesian start/end home planes into a native Robots target
tree. Absolute mode inserts one start home before the first non-empty branch and
one end home after the last non-empty branch. Per-branch mode inserts both around
every non-empty branch.

Home targets are command-safe joint-motion stop targets. Before every home move,
they explicitly emit:
  $OUT[1] = FALSE
  FLOW_RATE=0
so an active extrusion state cannot be carried into a home/park move.
*/
#endregion

using System;
using System.Collections.Generic;
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
    public sealed class wsp_Ro03_Set_Home_Target : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Ro03_Set_Home_Target()
            : base(
                "wsp_Ro03_Set Home Target",
                "set_home",
                "Adds arbitrary command-safe Cartesian home/park targets at the start and end " +
                "of a native Robots target tree. Home moves explicitly stop extrusion first.",
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
            new Guid("BFD19704-BEBE-4D14-8E1A-C1D27A8F026E");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ro03_Set Home Target.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
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
                "Native Robots target tree. Existing paths and target order are preserved.",
                GH_ParamAccess.tree);

            p.AddPlaneParameter(
                "start_plane", "s_plane",
                "Arbitrary Cartesian home/park plane inserted before the target sequence.",
                GH_ParamAccess.item);

            p.AddPlaneParameter(
                "end_plane", "e_plane",
                "Arbitrary Cartesian home/park plane inserted after the target sequence.",
                GH_ParamAccess.item);

            p.AddBooleanParameter(
                "absolute", "absolute",
                "True (default): add start_plane only to the first non-empty branch and " +
                "end_plane only to the last non-empty branch. False: add both planes to " +
                "every non-empty branch.",
                GH_ParamAccess.item,
                true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "target", "target",
                "Native Robots target tree with command-safe start/end home targets.",
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

            Plane startPlane = Plane.Unset;
            Plane endPlane = Plane.Unset;
            bool absolute = true;
            if (!DA.GetData(1, ref startPlane) || !startPlane.IsValid)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "'s_plane' must be a valid start plane.");
                return;
            }

            if (!DA.GetData(2, ref endPlane) || !endPlane.IsValid)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "'e_plane' must be a valid end plane.");
                return;
            }

            DA.GetData(3, ref absolute);

            var branches = new List<TargetBranch>(inputTree.PathCount);
            int firstNonEmpty = -1;
            int lastNonEmpty = -1;

            for (int branchIndex = 0; branchIndex < inputTree.PathCount; branchIndex++)
            {
                GH_Path path = inputTree.Paths[branchIndex];
                System.Collections.IList source = inputTree.get_Branch(path);
                var targets = new List<Target>(source != null ? source.Count : 0);

                if (source != null)
                {
                    for (int item = 0; item < source.Count; item++)
                    {
                        Target target;
                        if (!TryReadTarget(source[item] as IGH_Goo, out target) || target == null)
                        {
                            AddRuntimeMessage(
                                GH_RuntimeMessageLevel.Error,
                                $"'target' item {item} in branch {path} is not a valid native Robots target.");
                            return;
                        }

                        targets.Add(target);
                    }
                }

                if (targets.Count > 0)
                {
                    if (firstNonEmpty < 0) firstNonEmpty = branchIndex;
                    lastNonEmpty = branchIndex;
                }

                branches.Add(new TargetBranch(path, targets));
            }

            if (firstNonEmpty < 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "'target' contains no native Robots targets.");
                return;
            }

            var output = new DataTree<Target>();
            int inserted = 0;

            for (int branchIndex = 0; branchIndex < branches.Count; branchIndex++)
            {
                TargetBranch branch = branches[branchIndex];
                output.EnsurePath(branch.Path);
                if (branch.Targets.Count == 0) continue;

                bool insertStart = absolute
                    ? branchIndex == firstNonEmpty
                    : true;
                bool insertEnd = absolute
                    ? branchIndex == lastNonEmpty
                    : true;

                if (insertStart)
                {
                    output.Add(
                        CreateHomeTarget(startPlane, branch.Targets[0]),
                        branch.Path);
                    inserted++;
                }

                output.AddRange(branch.Targets, branch.Path);

                if (insertEnd)
                {
                    output.Add(
                        CreateHomeTarget(
                            endPlane,
                            branch.Targets[branch.Targets.Count - 1]),
                        branch.Path);
                    inserted++;
                }
            }

            DA.SetDataTree(0, output);
            Message = $"{_versionTag} | {inserted} home";

            if (absolute)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "Absolute mode: start_plane was inserted only before the first non-empty " +
                    "target branch and end_plane only after the last non-empty branch.");
            }
            else
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "Per-branch mode: start_plane and end_plane were inserted around every " +
                    "non-empty target branch.");
            }

            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Remark,
                "Home targets are joint-motion stop points and run $OUT[1] = FALSE plus " +
                "FLOW_RATE=0 before moving.");
        }

        private static Target CreateHomeTarget(Plane plane, Target boundary)
        {
            return new CartesianTarget(
                plane,
                configuration: null,
                motion: Motions.Joint,
                tool: boundary.Tool,
                speed: boundary.Speed,
                zone: Zone.Default,
                command: CreateNonExtrudeCommand(),
                frame: boundary.Frame,
                external: boundary.External,
                externalCustom: boundary.ExternalCustom);
        }

        private static Command CreateNonExtrudeCommand()
        {
            return new Group(new Command[]
            {
                new Robots.Commands.Custom(
                    "WASP_HOME_OUT_1_OFF",
                    Manufacturers.KUKA,
                    "$OUT[1] = FALSE",
                    runBefore: true),
                new Robots.Commands.Custom(
                    "WASP_HOME_FLOW_ZERO",
                    Manufacturers.KUKA,
                    "FLOW_RATE=0",
                    runBefore: true)
            });
        }

        private static bool TryReadTarget(IGH_Goo goo, out Target target)
        {
            target = null;
            if (goo == null) return false;

            object value = goo;
            for (int depth = 0; depth < 4 && value is IGH_Goo current; depth++)
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

            target = value as Target;
            if (target != null) return true;

            Target castTarget = null;
            if (goo.CastTo(out castTarget) && castTarget != null)
            {
                target = castTarget;
                return true;
            }

            return false;
        }

        private sealed class TargetBranch
        {
            public TargetBranch(GH_Path path, List<Target> targets)
            {
                Path = path;
                Targets = targets;
            }

            public GH_Path Path { get; }
            public List<Target> Targets { get; }
        }
    }
}
