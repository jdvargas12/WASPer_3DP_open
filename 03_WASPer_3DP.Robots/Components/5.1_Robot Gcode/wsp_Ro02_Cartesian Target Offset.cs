#region Component Description
/*
Component: wsp_Ro02_Cartesian Target Offset
Nickname: target_offset
Category: WASPer_3DP
SubCategory: 5.1_Robot Gcode

Native Robots counterpart of KUKA|prc Cartesian Offset. It inserts a
command-free linear Cartesian target before the first input target and/or after
the last input target. XYZ offsets are expressed in the target frame coordinate
system. Optional speeds are Robots translation speeds in mm/s:
  1 value  = rapid, approach, retract
  2 values = rapid, approach/retract
  3 values = rapid, approach, retract

The original targets retain their Robots properties and process commands. When
a start offset is enabled, the first original target becomes an exact linear
approach target so its existing post-move extrusion commands execute only after
the offset-to-start movement has completed.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Reflection;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino.Geometry;

using Robots;
using Robots.Commands;

namespace WASPer_3DP.Components._5_1_Robot_Gcode
{
    public sealed class wsp_Ro02_Cartesian_Target_Offset : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Ro02_Cartesian_Target_Offset()
            : base(
                "wsp_Ro02_Cartesian Target Offset",
                "target_offset",
                "Adds command-free linear Cartesian offset targets before and/or after a " +
                "list of native Robots targets. Useful for approach, retract, and Cartesian " +
                "home/park positions. Validate all generated positions in Robots simulation.",
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
            new Guid("0888D34E-B582-4966-958B-B2B24336E6EC");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ro02_Cartesian Target Offset.png"))
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
                "Ordered native Robots targets. Flatten before this component to add one " +
                "global start/end offset; preserve branches to process each branch separately.",
                GH_ParamAccess.list);

            p.AddNumberParameter(
                "x", "x",
                "Cartesian X offset in model units, expressed in the target Frame coordinates.",
                GH_ParamAccess.item,
                0.0);

            p.AddNumberParameter(
                "y", "y",
                "Cartesian Y offset in model units, expressed in the target Frame coordinates.",
                GH_ParamAccess.item,
                0.0);

            p.AddNumberParameter(
                "z", "z",
                "Cartesian Z offset in model units, expressed in the target Frame coordinates.",
                GH_ParamAccess.item,
                50.0);

            p.AddBooleanParameter(
                "start", "start",
                "True adds an offset linear target before the first target.",
                GH_ParamAccess.item,
                true);

            p.AddBooleanParameter(
                "end", "end",
                "True adds an offset linear target after the last target.",
                GH_ParamAccess.item,
                true);

            p.AddNumberParameter(
                "offset_vel", "offset_vel",
                "Optional Robots translation speeds in mm/s: [rapid, approach, retract]. " +
                "One value applies to all three; two values use the second for approach and " +
                "retract. When disconnected, source target speeds are reused.",
                GH_ParamAccess.list);
            p[6].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "target", "target",
                "Native Robots targets with optional command-free Cartesian start/end offsets.",
                GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            var targetGoos = new List<IGH_Goo>();
            if (!DA.GetDataList(0, targetGoos) || targetGoos.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "'target' must contain at least one native Robots target.");
                return;
            }

            var targets = new List<Target>(targetGoos.Count);
            for (int i = 0; i < targetGoos.Count; i++)
            {
                Target target;
                if (!TryReadTarget(targetGoos[i], out target) || target == null)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        $"'target' item {i} is not a valid native Robots target.");
                    return;
                }

                targets.Add(target);
            }

            double x = 0.0;
            double y = 0.0;
            double z = 50.0;
            bool addStart = true;
            bool addEnd = true;
            DA.GetData(1, ref x);
            DA.GetData(2, ref y);
            DA.GetData(3, ref z);
            DA.GetData(4, ref addStart);
            DA.GetData(5, ref addEnd);

            if (!IsFinite(x) || !IsFinite(y) || !IsFinite(z))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "x, y, and z must be finite Cartesian offsets.");
                return;
            }

            var offsetVelocities = new List<double>();
            DA.GetDataList(6, offsetVelocities);
            if (offsetVelocities.Count > 3)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "offset_vel uses only its first three values: rapid, approach, retract.");
                offsetVelocities.RemoveRange(3, offsetVelocities.Count - 3);
            }

            for (int i = 0; i < offsetVelocities.Count; i++)
            {
                if (!IsFinite(offsetVelocities[i]) || offsetVelocities[i] <= 0.0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        $"offset_vel item {i} must be a positive finite speed in mm/s.");
                    return;
                }
            }

            CartesianTarget first = targets[0] as CartesianTarget;
            CartesianTarget last = targets[targets.Count - 1] as CartesianTarget;
            if (addStart && first == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "A start offset requires the first input item to be a CartesianTarget.");
                return;
            }

            if (addEnd && last == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "An end offset requires the last input item to be a CartesianTarget.");
                return;
            }

            if (!addStart && !addEnd)
            {
                DA.SetDataList(0, targets);
                Message = $"{_versionTag} | pass";
                return;
            }

            Vector3d offset = new Vector3d(x, y, z);
            if (offset.SquareLength <= Rhino.RhinoMath.ZeroTolerance)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "The Cartesian offset is zero; enabled start/end targets duplicate their source positions.");
            }

            Speed rapidSpeed = first != null
                ? ResolveSpeed(first.Speed, offsetVelocities, 0)
                : Speed.Default;
            Speed approachSpeed = first != null
                ? ResolveSpeed(first.Speed, offsetVelocities, offsetVelocities.Count > 1 ? 1 : 0)
                : Speed.Default;
            int retractVelocityIndex = offsetVelocities.Count > 2
                ? 2
                : offsetVelocities.Count > 1 ? 1 : 0;
            Speed retractSpeed = last != null
                ? ResolveSpeed(last.Speed, offsetVelocities, retractVelocityIndex)
                : Speed.Default;

            var output = new List<Target>(
                targets.Count + (addStart ? 1 : 0) + (addEnd ? 1 : 0));

            if (addStart)
                output.Add(CreateOffsetTarget(first, offset, rapidSpeed));

            for (int i = 0; i < targets.Count; i++)
            {
                if (i == 0 && addStart)
                    output.Add(CreateApproachTarget(first, approachSpeed));
                else
                    output.Add(targets[i]);
            }

            if (addEnd)
                output.Add(CreateOffsetTarget(last, offset, retractSpeed));

            DA.SetDataList(0, output);
            Message = $"{_versionTag} | {output.Count} targets";
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Remark,
                $"Added {(addStart ? "start" : string.Empty)}" +
                $"{(addStart && addEnd ? " + " : string.Empty)}" +
                $"{(addEnd ? "end" : string.Empty)} Cartesian offset target(s). " +
                "Added targets are linear stop points with no process commands.");
        }

        private static Target CreateOffsetTarget(
            CartesianTarget source,
            Vector3d offset,
            Speed speed)
        {
            Plane plane = source.Plane;
            plane.Origin += offset;
            return new CartesianTarget(
                plane,
                source.Configuration,
                Motions.Linear,
                source.Tool,
                speed,
                Zone.Default,
                Command.Default,
                source.Frame,
                source.External,
                source.ExternalCustom);
        }

        private static Target CreateApproachTarget(
            CartesianTarget source,
            Speed speed)
        {
            return new CartesianTarget(
                source.Plane,
                source.Configuration,
                Motions.Linear,
                source.Tool,
                speed,
                Zone.Default,
                source.Command,
                source.Frame,
                source.External,
                source.ExternalCustom);
        }

        private static Speed ResolveSpeed(
            Speed fallback,
            IList<double> values,
            int index)
        {
            if (values == null || values.Count == 0)
                return fallback;

            int resolvedIndex = Math.Max(0, Math.Min(index, values.Count - 1));
            Speed source = fallback ?? Speed.Default;
            return new Speed(
                values[resolvedIndex],
                source.RotationSpeed,
                source.TranslationExternal,
                source.RotationExternal,
                translationAccel: source.TranslationAccel,
                axisAccel: source.AxisAccel,
                time: source.Time);
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

        private static bool IsFinite(double value) =>
            !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
