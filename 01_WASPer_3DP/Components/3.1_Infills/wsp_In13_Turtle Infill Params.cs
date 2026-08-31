using System;
using System.Reflection;
using Grasshopper.Kernel;

namespace WASPer_3DP.Components._3_1_Infills
{
    public sealed class wsp_In13_Turtle_Infill_Params : GH_Component
    {
        private readonly string _versionTag;

        public wsp_In13_Turtle_Infill_Params()
            : base(
                "wsp_In13_Turtle Infill Params (Layered)",
                "Turtle Params",
                "Packs one Turtle-cell pattern definition for wsp_In10 Layered Multi-Infill. Create one object per curve domain, or broadcast one object across all domains.\n\n" +
                "Generates 'turtle-graphics' infill paths between consecutive guide-curve pairs in each branch.\n\n" +
                "Reference: 'Additive Manufacturing of Thermally Enhanced Lightweight Concrete Wall Elements with Closed Cellular Structures' Dielemans 2021",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "3.1_Infills")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("76B51EFC-BEDA-484D-A1D4-9EAE79CC1EBF");
        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_In13_Turtle Infill Params.png");
                    return stream == null ? null : new System.Drawing.Bitmap(stream);
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddNumberParameter("p_width", "p_width", "Printed Turtle-path width used for cell geometry, spacing between paired/internal Turtle bands, and porosity estimation. The outer Turtle centrelines use In10's cleared guides directly, without an additional half-width inset. Must be > 0.", GH_ParamAccess.item, 4.0);
            p.AddIntegerParameter("cell_count_tan", "cx", "Number of Turtle cells along each guide domain. Must be >= 1.", GH_ParamAccess.item, 6);
            p.AddIntegerParameter("cell_count_perp", "cy", "Number of repeated Turtle bands across each guide domain. Must be >= 1.", GH_ParamAccess.item, 1);
            p.AddNumberParameter("cell_count_z", "cz", "Triangle-wave cycles across the layer stack. Floating-point values are supported; 0 locks the bridge state to bridge_p_0.", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("bridge_p_0", "b0", "Normalized bridge position at the start of the Z modulation, from 0 to 1.", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("bridge_p_1", "b1", "Normalized bridge position at the alternate Z state, from 0 to 1.", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("extend_ends", "ext", "Distance added to both ends of every generated Turtle path. Must be >= 0.", GH_ParamAccess.item, 0.0);
            p.AddBooleanParameter("teeth", "teeth", "Add the Turtle tooth modulation around cell transitions.", GH_ParamAccess.item, false);
            for (int i = 0; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddGenericParameter("turtle_infill_params", "turtle_p", "Typed Turtle parameters for wsp_In10 Layered Multi-Infill.", GH_ParamAccess.item);

        protected override void SolveInstance(IGH_DataAccess da)
        {
            double pathWidth = 4.0, countZ = 1.0, bridge0 = 0.0, bridge1 = 1.0, extendEnds = 0.0;
            int countX = 6, countY = 1;
            bool teeth = false;
            da.GetData(0, ref pathWidth);
            da.GetData(1, ref countX);
            da.GetData(2, ref countY);
            da.GetData(3, ref countZ);
            da.GetData(4, ref bridge0);
            da.GetData(5, ref bridge1);
            da.GetData(6, ref extendEnds);
            da.GetData(7, ref teeth);

            var value = new WasperTurtleInfillParams
            {
                PathWidth = pathWidth,
                CountX = countX,
                CountY = countY,
                CountZ = countZ,
                Bridge0 = bridge0,
                Bridge1 = bridge1,
                ExtendEnds = extendEnds,
                Teeth = teeth
            };

            string error = value.Validate();
            if (error != null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error);
                return;
            }

            Message = $"{_versionTag} | Turtle {countX}x{countY}";
            da.SetData(0, new WasperTurtleInfillParamsGoo(value));
        }
    }
}
