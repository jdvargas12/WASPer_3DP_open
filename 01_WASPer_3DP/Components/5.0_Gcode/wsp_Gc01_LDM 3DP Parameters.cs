#region Component Description
/*
Component: wsp_Gc01_LDM 3DP Parameters
Nickname: LDM 3DP Params
Category: WASPer_3DP
SubCategory: 5.0_Gcode
Version:
    Uses the compiled assembly version in the component message via _versionTag.

GENERAL DESCRIPTION
Packs LDM (Liquid Deposition Modeling) printing parameters into a single
Wasper3dpParams object (3dp_params wire, Process = LDM) consumed by the Marlin
G-code generator. The generator itself has no scalar parameter inputs: this
object is the way to configure it. Only inputs the user actually wires travel
with the object; unset fields fall back to the generator's LDM defaults.

All inputs are optional except that the generator requires nozzle_diameter to
be set here. Non-positive numeric values are reported as a warning and treated
as unset.

INPUTS
 0) nozzle_diameter : double        Nozzle diameter in mm (required by the generator).
 1) fillament_multi : double (opt)  Filament multiplier diameter in mm used with nozzle_diameter for E-axis conversion (default 5.15).
 2) layer_w         : double (opt)  Nominal/base width override for Gc03; otherwise Gc03 uses wsp_path.LayerW or layer_h x 2.5.
 3) printing_speed  : tree   (opt)  Print feedrate in mm/min; single value or per-point
  tree matching pt_planes (default 7000).
 4) travel_speed    : double (opt)  Travel feedrate in mm/min (default 8000).
 5) z_hop           : double (opt)  Z-hop height in mm. Default 0 = disabled; use > 0 to enable.
 6) z_hop_speed     : double (opt)  Z-hop feedrate in mm/min (default 6000).
 7) split_gcode     : bool   (opt)  Split output by volume (default false).
 8) split_vol_L     : double (opt)  Split threshold, max volume per file in litres (default 4.5).
9) time_correction : double (opt)  Multiplier applied to the raw time estimate (default 1.75).
10) wsp_mat   : WasperMaterial (opt)  Material record whose density is packed into 3dp_params (default 1600 kg/m3).
                                    The material is resolved here before reaching the generator.

OUTPUTS
0) 3dp_params : Wasper3dpParams   Packed parameter object (Process = LDM).
1) info       : string            Summary of the fields that were set.
*/
#endregion

#region Usings
using System;
using System.Reflection;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using WASPer_3DP;
#endregion

namespace WASPer_3DP.Components._5_0_Gcode
{
    public sealed class wsp_Gc01_LDM_3DP_Parameters : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Gc01_LDM_3DP_Parameters()
            : base(
                "wsp_Gc01_LDM 3DP Parameters",
                "LDM 3DP Params",
                "Packs LDM printing parameters (nozzle, widths, speeds, z-hop, splitting, " +
                "time correction, density) into one 3dp_params object (Process = LDM) for " +
                "the Marlin G-code generator.\r\n" +
                "Only the inputs you wire are carried; everything else falls back to the " +
                "generator's LDM defaults.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "5.0_Gcode")
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = $"{_versionTag} - LDM";
        }

        public override Guid ComponentGuid =>
            new Guid("8B4F2D6A-0E19-4C73-B5A8-D92E647C1F30");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Gc02_LDM 3DP Parameters.png"))
                        return s != null ? new System.Drawing.Bitmap(s) : null;
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            int i;

            i = p.AddNumberParameter("nozzle_diameter", "nozzle_diameter",
                "Nozzle diameter in mm. Used in E = flow * (nozzle_diameter * layer_h * segment_length) / A_fil. Required by the Marlin G-code generator unless supplied by wsp_path.",
                GH_ParamAccess.item);
            p[i].Optional = true;

            i = p.AddNumberParameter("fillament_multi", "fillament_multi",
                "Filament diameter in mm used to calculate A_fil = pi * (fillament_multi / 2)^2 in the E-axis conversion. Generator default: 5.15.",
                GH_ParamAccess.item);
            p[i].Optional = true;

            i = p.AddNumberParameter("layer_w", "layer_w",
                "Optional nominal/base bead width before flow adjustment, in mm. When passed to Gc03 through 3dp_params, it overrides wsp_path.LayerW; if omitted, Gc03 preserves the incoming path value, or defaults to layer_h * 2.5 when no path value exists. Gc03 stores the resolved nominal width as LayerW, estimates LayerWf from local flow and layer_h, and updates per-segment PrintVol. It does not replace nozzle_diameter in the LDM E-axis calculation.",
                GH_ParamAccess.item);
            p[i].Optional = true;

            i = p.AddNumberParameter("printing_speed", "print_speed",
                "Optional explicit print feedrate in mm/min. Single value or per-point tree " +
                "matching canonical pt_planes {layer;curve}. When passed to Gc03, overrides print_speed from wsp_path. " +
                "If both are unwired, the generator default is 7000.",
                GH_ParamAccess.tree);
            p[i].Optional = true;

            i = p.AddNumberParameter("travel_speed", "travel_speed",
                "Travel feedrate in mm/min. Generator default: 8000.",
                GH_ParamAccess.item);
            p[i].Optional = true;

            i = p.AddNumberParameter("z_hop", "z_hop",
                "Z-hop height in mm. Default 0 disables z-hop completely. Input a value > 0 to enable positive-Z hop moves.",
                GH_ParamAccess.item,
                0.0);
            p[i].Optional = true;

            i = p.AddNumberParameter("z_hop_speed", "z_hop_speed",
                "Z-hop feedrate in mm/min. Generator default: 6000.",
                GH_ParamAccess.item);
            p[i].Optional = true;

            i = p.AddBooleanParameter("split_gcode", "split_gcode",
                "Split output G-code into multiple files by volume. Generator default: False.",
                GH_ParamAccess.item);
            p[i].Optional = true;

            i = p.AddNumberParameter("split_vol_L", "split_vol_L",
                "Split threshold: maximum volume per G-code file in litres. Generator default: 4.5.",
                GH_ParamAccess.item);
            p[i].Optional = true;

            i = p.AddNumberParameter("time_correction", "time_correction",
                "Multiplier applied to the raw time estimate (accounts for acceleration, " +
                "jerk and firmware overhead). Generator default: 1.75. To tune: " +
                "time_correction = actual_time / estimated_time.",
                GH_ParamAccess.item);
            p[i].Optional = true;

            i = p.AddGenericParameter("wsp_mat", "wsp_mat",
                "Material density in kg/m3 for mass estimation. Generator default: 1600. " +
                "The material is resolved here before reaching the generator.",
                GH_ParamAccess.item);
            p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("3dp_params", "3dp_params",
                "Packed WASPer 3DP parameters object (Process = LDM). Wire into the " +
                "Marlin G-code generator.",
                GH_ParamAccess.item);

            p.AddTextParameter("info", "info",
                "Summary of the fields carried by this parameter object.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var prm = new Wasper3dpParams { Process = Wasper3dpProcess.LDM };

            prm.NozzleDiameter = ReadPositive(DA, 0, "nozzle_diameter");
            prm.FillamentMulti = ReadPositive(DA, 1, "fillament_multi");
            prm.LayerW         = ReadPositive(DA, 2, "layer_w");

            if (DA.GetDataTree(3, out GH_Structure<GH_Number> speedTree) &&
                speedTree != null && !speedTree.IsEmpty)
            {
                prm.PrintSpeed = WasperGcodeTreeUtil.ToDoubleTree(speedTree);
            }


            prm.TravelSpeed    = ReadPositive(DA, 4, "travel_speed");
            prm.ZHop           = ReadNonNegative(DA, 5, "z_hop");
            prm.ZHopSpeed      = ReadPositive(DA, 6, "z_hop_speed");

            bool split = false;
            if (DA.GetData(7, ref split)) prm.SplitGcode = split;

            prm.SplitVolL      = ReadPositive(DA, 8, "split_vol_L");
            prm.TimeCorrection = ReadPositive(DA, 9, "time_correction");
            double materialDensity;
            if (WasperGcodeTreeUtil.TryGetMaterialDensity(DA, 10, out materialDensity))
            {
                prm.Density = materialDensity;
            }

            if (prm.ZHopSpeed.HasValue && prm.ZHopSpeed.Value < 60)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"'z_hop_speed' is very low ({prm.ZHopSpeed.Value} mm/min). " +
                    "Feedrates are in mm/min, not mm/s.");
            }

            if (!prm.ZHop.HasValue || prm.ZHop.Value == 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "z_hop = 0: no Z-hop moves will be generated. Input a value > 0 to enable positive-Z hops.");
            }

            if (!prm.NozzleDiameter.HasValue)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "'nozzle_diameter' is not set; the Marlin G-code generator will " +
                    "raise an error until it is supplied.");
            }

            DA.SetData(0, new Wasper3dpParamsGoo(prm));
            DA.SetData(1, prm.ToString());
        }

        /// <summary>Reads an optional item double; non-positive values warn and count as unset.</summary>
        private double? ReadPositive(IGH_DataAccess DA, int index, string name)
        {
            double v = 0.0;
            if (!DA.GetData(index, ref v)) return null;
            if (double.IsNaN(v) || double.IsInfinity(v) || v <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"'{name}' must be a positive number; the value is ignored " +
                    "(the generator default will apply).");
                return null;
            }
            return v;
        }

        /// <summary>Reads an optional item double where zero is a deliberate value.</summary>
        private double? ReadNonNegative(IGH_DataAccess DA, int index, string name)
        {
            double v = 0.0;
            if (!DA.GetData(index, ref v)) return null;
            if (double.IsNaN(v) || double.IsInfinity(v) || v < 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"'{name}' must be zero or a positive number; the value is ignored " +
                    "(the generator default will apply).");
                return null;
            }
            return v;
        }
    }
}
