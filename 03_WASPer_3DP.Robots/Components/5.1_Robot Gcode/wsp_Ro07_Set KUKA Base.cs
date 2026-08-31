#region Component Description
/*
Component: wsp_Ro07_Set KUKA Base
Nickname: kuka_base
Category: WASPer_3DP
SubCategory: 5.1_Robot Gcode

Converts a native Robots Frame into a KUKA controller-resident base reference.
The frame plane and coupling information are preserved for Robots simulation,
while Create Program is instructed to emit:
  $BASE = BASE_DATA[base_no]

The selected controller slot must already contain the matching calibrated base
on the physical KUKA controller.
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
    public sealed class wsp_Ro07_Set_KUKA_Base : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Ro07_Set_KUKA_Base()
            : base(
                "wsp_Ro07_Set KUKA Base",
                "kuka_base",
                "Assigns a KUKA controller BASE_DATA number to a native Robots Frame while " +
                "preserving its plane and coupling information for simulation.",
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
            new Guid("1E5E5FC0-9AA7-4DC3-818C-C51C31ABF67F");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ro07_Set KUKA Base.png"))
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
                "frame", "frame",
                "Native Robots Frame, normally produced by Create Frame. Its plane and any " +
                "mechanical coupling information are preserved for simulation.",
                GH_ParamAccess.item);

            p.AddIntegerParameter(
                "base_number", "base_no",
                "KUKA controller base number. Create Program will reference BASE_DATA[n]. " +
                "The same calibrated base must already exist in this slot on the physical " +
                "controller.",
                GH_ParamAccess.item,
                4);

            p.AddNumberParameter(
                "b_XYZ", "b_XYZ",
                "Optional list of exactly three KUKA base translation values [X,Y,Z] in mm. " +
                "Connect together with b_ABC to override the incoming Frame plane for " +
                "Robots simulation.",
                GH_ParamAccess.list);
            p[2].Optional = true;

            p.AddNumberParameter(
                "b_ABC", "b_ABC",
                "Optional list of exactly three KUKA base orientation values [A,B,C] in " +
                "degrees, using KUKA's Euler Z-Y-X convention. Connect together with b_XYZ " +
                "to override the incoming Frame plane for Robots simulation.",
                GH_ParamAccess.list);
            p[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "frame", "frame",
                "Native Robots Frame configured to use the selected KUKA BASE_DATA controller " +
                "slot. Connect it to the Frame input of Create Target.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            IGH_Goo goo = null;
            if (!DA.GetData(0, ref goo) || !TryReadValue(goo, out Frame source))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "'frame' must be a native Robots Frame, such as the output of Create Frame.");
                return;
            }

            int number = 4;
            DA.GetData(1, ref number);
            if (number < 1)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "'base_no' must be 1 or greater because KUKA BASE_DATA is one-based.");
                return;
            }

            Plane framePlane = source.Plane;
            var xyz = new List<double>();
            var abc = new List<double>();
            bool hasXyz = DA.GetDataList(2, xyz) && xyz.Count > 0;
            bool hasAbc = DA.GetDataList(3, abc) && abc.Count > 0;

            if (hasXyz != hasAbc)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "'b_XYZ' and 'b_ABC' must either both be connected or both be disconnected.");
                return;
            }

            if (hasXyz)
            {
                if (!TryCreateKukaPlane(xyz, abc, out framePlane, out string planeError))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, planeError);
                    return;
                }
            }

            Frame controllerFrame;
            try
            {
                controllerFrame = new Frame(
                    framePlane,
                    source.CoupledMechanism,
                    source.CoupledMechanicalGroup,
                    source.HasName ? source.Name : $"KukaBase{number}",
                    useController: true,
                    number: number);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Could not create the KUKA controller base: " + ex.Message);
                return;
            }

            DA.SetData(0, controllerFrame);
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Remark,
                $"KUKA controller base set: $BASE = BASE_DATA[{number}]. Verify that the " +
                "controller slot matches the simulated frame plane." +
                (hasXyz ? " Frame geometry was overridden from b_XYZ/b_ABC." : ""));
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
                error = "'b_XYZ' must contain exactly three numbers ordered X, Y, Z.";
                return false;
            }

            if (abc == null || abc.Count != 3)
            {
                error = "'b_ABC' must contain exactly three numbers ordered A, B, C.";
                return false;
            }

            var values = new double[6];
            for (int i = 0; i < 3; i++)
            {
                if (double.IsNaN(xyz[i]) || double.IsInfinity(xyz[i]) ||
                    double.IsNaN(abc[i]) || double.IsInfinity(abc[i]))
                {
                    error = "'b_XYZ' and 'b_ABC' values must all be finite numbers.";
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
                    error = "The b_XYZ/b_ABC values produced an invalid base plane.";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "Could not convert b_XYZ/b_ABC into a KUKA base plane: " + ex.Message;
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
