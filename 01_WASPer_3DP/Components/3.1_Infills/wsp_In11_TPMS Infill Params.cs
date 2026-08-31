using System;
using System.Reflection;
using Grasshopper.Kernel;

namespace WASPer_3DP.Components._3_1_Infills
{
    public sealed class wsp_In11_TPMS_Infill_Params : GH_Component
    {
        private readonly string _versionTag;

        public wsp_In11_TPMS_Infill_Params()
            : base(
                "wsp_In11_TPMS Infill Params (Layered / Volumetric)",
                "TPMS Params",
                "Packs one TPMS pattern definition for wsp_In08/In09 volumetric fields or wsp_In10 layered paths. Create one object per domain, or one object to broadcast. invert_tpms selects the complementary TPMS phase before generator-owned shells and clipping.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "3.1_Infills")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("E97693E3-5FBC-4B5E-A4D4-1EE06D671AB6");
        public override GH_Exposure Exposure => GH_Exposure.primary;
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_In11_TPMS Infill Params.png");
                    return stream == null ? null : new System.Drawing.Bitmap(stream);
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddIntegerParameter("type", "type", "TPMS type: 0=P, 1=D, 2=Gyroid, 3=IWP, 4=Neovius, 5=Lidinoid, 6=FK-S, 7=FK-Y.", GH_ParamAccess.item, 2);
            p.AddNumberParameter("level", "level", "TPMS iso-level.", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("count_x", "cx", "Periods along the guide-curve direction. Floating-point values such as 0.5 and 1.5 are supported. Must be > 0.", GH_ParamAccess.item, 3.0);
            p.AddNumberParameter("count_y", "cy", "Periods across the guide gap. Floating-point values are supported. Must be > 0.", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("count_z", "cz", "Periods across the layer stack. Floating-point values are supported. 0 keeps the same Z phase on every layer.", GH_ParamAccess.item, 4.0);
            p.AddNumberParameter("phase_x", "px", "Normalized phase offset along X. 1.0 equals one complete period.", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("phase_y", "py", "Normalized phase offset along Y. 1.0 equals one complete period.", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("phase_z", "pz", "Normalized phase offset across layers. 1.0 equals one complete period.", GH_ParamAccess.item, 0.0);
            p.AddBooleanParameter("close_tpms", "close", "Close the selected TPMS phase along the usable domain boundary. False leaves contours open where they meet the boundary. In wsp_In10, when both guide curves of a domain are closed loops (start and end within res/2) and clear_long is 0, the along-guide closing is suppressed so the pattern runs continuously around the seam; count_x is then snapped to a whole number of periods. A clear_long > 0 opens the loop and restores the side closing.", GH_ParamAccess.item, false);
            p.AddBooleanParameter("invert_tpms", "invert", "Invert the TPMS scalar field before contouring. When close_tpms is true, this closes the opposite phase of the field.", GH_ParamAccess.item, false);
            for (int i = 0; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddGenericParameter("tpms_infill_params", "tpms_p", "Typed TPMS parameters for wsp_In08 Volumetric Box, wsp_In09 Volumetric Surfaces, or wsp_In10 Layered Multi-Infill.", GH_ParamAccess.item);

        protected override void SolveInstance(IGH_DataAccess da)
        {
            int type = 2;
            double level = 0.0, cx = 3.0, cy = 1.0, cz = 4.0, px = 0.0, py = 0.0, pz = 0.0;
            bool close = false;
            bool invert = false;
            da.GetData(0, ref type);
            da.GetData(1, ref level);
            da.GetData(2, ref cx);
            da.GetData(3, ref cy);
            da.GetData(4, ref cz);
            da.GetData(5, ref px);
            da.GetData(6, ref py);
            da.GetData(7, ref pz);
            da.GetData(8, ref close);
            da.GetData(9, ref invert);

            var value = new WasperTpmsInfillParams
            {
                Type = type,
                Level = level,
                CountX = cx,
                CountY = cy,
                CountZ = cz,
                PhaseX = px,
                PhaseY = py,
                PhaseZ = pz,
                CloseTpms = close,
                InvertTpms = invert
            };

            string error = value.Validate();
            if (error != null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error);
                return;
            }

            Message = $"{_versionTag} | {WasperTpmsInfillParams.Tag(type)}{(invert ? " | Inv" : "")}";
            da.SetData(0, new WasperTpmsInfillParamsGoo(value));
        }
    }
}
