#region Component Description
/*
Component: wsp_Ro06_Set KUKA Tool
Nickname: kuka_tool
Category: WASPer_3DP
SubCategory: 5.1_Robot Gcode

Converts a native Robots Tool into a KUKA controller-resident tool reference.
The TCP, mass data, and preview/collision geometry are preserved for Robots
simulation, while Create Program is instructed to emit:
  $TOOL = TOOL_DATA[tool_no]
  $LOAD = LOAD_DATA[tool_no]

The selected controller slots must already contain the matching calibrated tool
and load data on the physical KUKA controller.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Reflection;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino.Geometry;

using Robots;

namespace WASPer_3DP.Components._5_1_Robot_Gcode
{
    public sealed class wsp_Ro06_Set_KUKA_Tool : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Ro06_Set_KUKA_Tool()
            : base(
                "wsp_Ro06_Set KUKA Tool",
                "kuka_tool",
                "Assigns a KUKA controller TOOL_DATA/LOAD_DATA number to a native Robots " +
                "Tool while preserving its TCP, load, and simulation geometry.",
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
            new Guid("83AC2F6D-2D86-46BF-B3A0-2F9388CC846D");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ro06_Set KUKA Tool.png"))
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
                "tool", "tool",
                "Native Robots Tool, normally produced by Create Tool. Its TCP, mass data, " +
                "preview mesh, and collision mesh are preserved for simulation.",
                GH_ParamAccess.item);

            p.AddIntegerParameter(
                "tool_number", "tool_no",
                "KUKA controller tool number. Create Program will reference TOOL_DATA[n] " +
                "and LOAD_DATA[n]. The same calibrated tool/load must already exist in these " +
                "slots on the physical controller.",
                GH_ParamAccess.item,
                15);

            p.AddNumberParameter(
                "t_XYZ", "t_XYZ",
                "Optional list of exactly three KUKA TCP translation values [X,Y,Z] in mm, " +
                "relative to the robot flange. Connect together with t_ABC to override the " +
                "incoming Tool TCP for Robots simulation.",
                GH_ParamAccess.list);
            p[2].Optional = true;

            p.AddNumberParameter(
                "t_ABC", "t_ABC",
                "Optional list of exactly three KUKA TCP orientation values [A,B,C] in " +
                "degrees, using KUKA's Euler Z-Y-X convention. Connect together with t_XYZ " +
                "to override the incoming Tool TCP for Robots simulation.",
                GH_ParamAccess.list);
            p[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "tool", "tool",
                "Native Robots Tool configured to use the selected KUKA TOOL_DATA and " +
                "LOAD_DATA controller slots. Connect it to the Tool input of Create Target.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            IGH_Goo goo = null;
            if (!DA.GetData(0, ref goo) || !TryReadValue(goo, out Tool source))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "'tool' must be a native Robots Tool, such as the output of Create Tool.");
                return;
            }

            int number = 15;
            DA.GetData(1, ref number);
            if (number < 1)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "'tool_no' must be 1 or greater because KUKA TOOL_DATA is one-based.");
                return;
            }

            Plane tcp = source.Tcp;
            var xyz = new List<double>();
            var abc = new List<double>();
            bool hasXyz = DA.GetDataList(2, xyz) && xyz.Count > 0;
            bool hasAbc = DA.GetDataList(3, abc) && abc.Count > 0;

            if (hasXyz != hasAbc)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "'t_XYZ' and 't_ABC' must either both be connected or both be disconnected.");
                return;
            }

            if (hasXyz)
            {
                if (!TryCreateKukaPlane(xyz, abc, out tcp, out string planeError))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, planeError);
                    return;
                }
            }

            Tool controllerTool;
            try
            {
                controllerTool = new Tool(
                    tcp,
                    source.HasName ? source.Name : $"KukaTool{number}",
                    source.Weight,
                    source.Centroid,
                    source.Mesh,
                    calibrationPlanes: null,
                    useController: true,
                    number: number,
                    collisionMesh: source.CollisionMesh);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Could not create the KUKA controller tool: " + ex.Message);
                return;
            }

            DA.SetData(0, controllerTool);
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Remark,
                $"KUKA controller tool set: $TOOL = TOOL_DATA[{number}] and " +
                $"$LOAD = LOAD_DATA[{number}]. Verify that both controller slots match " +
                "the simulated TCP and load data." +
                (hasXyz ? " TCP geometry was overridden from t_XYZ/t_ABC." : ""));
        }

        private static bool TryCreateKukaPlane(
            IList<double> xyz,
            IList<double> abc,
            out Rhino.Geometry.Plane plane,
            out string error)
        {
            plane = Rhino.Geometry.Plane.Unset;
            error = null;

            if (xyz == null || xyz.Count != 3)
            {
                error = "'t_XYZ' must contain exactly three numbers ordered X, Y, Z.";
                return false;
            }

            if (abc == null || abc.Count != 3)
            {
                error = "'t_ABC' must contain exactly three numbers ordered A, B, C.";
                return false;
            }

            var values = new double[6];
            for (int i = 0; i < 3; i++)
            {
                if (double.IsNaN(xyz[i]) || double.IsInfinity(xyz[i]) ||
                    double.IsNaN(abc[i]) || double.IsInfinity(abc[i]))
                {
                    error = "'t_XYZ' and 't_ABC' values must all be finite numbers.";
                    return false;
                }

                values[i] = xyz[i];
                values[i + 3] = abc[i];
            }

            try
            {
                plane = KukaXyzAbcToPlane(values);
                if (!plane.IsValid)
                {
                    error = "The t_XYZ/t_ABC values produced an invalid TCP plane.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not convert t_XYZ/t_ABC into a KUKA TCP plane: " + ex.Message;
                return false;
            }
        }

        private static Plane KukaXyzAbcToPlane(double[] values)
        {
            double a = -values[3] * Math.PI / 180.0;
            double b = -values[4] * Math.PI / 180.0;
            double c = -values[5] * Math.PI / 180.0;
            double ca = Math.Cos(a);
            double sa = Math.Sin(a);
            double cb = Math.Cos(b);
            double sb = Math.Sin(b);
            double cc = Math.Cos(c);
            double sc = Math.Sin(c);

            double m00 = ca * cb;
            double m01 = sa * cc + ca * sb * sc;
            double m10 = -sa * cb;
            double m11 = ca * cc - sa * sb * sc;
            double m20 = sb;
            double m21 = -cb * sc;

            return new Plane(
                new Point3d(values[0], values[1], values[2]),
                new Vector3d(m00, m10, m20),
                new Vector3d(m01, m11, m21));
        }

        private static bool TryReadValue<T>(IGH_Goo goo, out T result)
            where T : class
        {
            result = null;
            if (goo == null) return false;

            object value = goo;
            for (int depth = 0; depth < 5 && value is IGH_Goo current; depth++)
            {
                if (current is GH_ObjectWrapper wrapper)
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
    }
}
