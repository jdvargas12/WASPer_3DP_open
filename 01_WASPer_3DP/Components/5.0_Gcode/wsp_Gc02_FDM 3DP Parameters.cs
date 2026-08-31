#region Component Description
/*
Component: wsp_Gc02_FDM 3DP Parameters
Nickname: FDM 3DP Params
Category: WASPer_3DP
SubCategory: 5.0_Gcode
Version:
    Uses the compiled assembly version in the component message via _versionTag.

GENERAL DESCRIPTION
Packs FDM (Fused Deposition Modeling) printing parameters into a single
Wasper3dpParams object (3dp_params wire, Process = FDM) consumed by the Marlin
G-code generator, which switches to its FDM code path: fan control per layer
(M106), nozzle/bed temperature blocks, optional custom start/end G-code, FDM
extrusion model (E = V / A_fil, 5 decimals), no volume splitting.

Only inputs the user actually wires travel with the object; unset fields fall
back to the generator's FDM defaults (filament 1.75 mm, print 1200 mm/min,
travel 5000, z-hop speed 3000, z-hop 0/off, layer_w from wsp_path or layer_h x 2.5,
T_nozzle 200 C, T_bed 60 C, density 1240 kg/m3).

INPUTS
 0) custom_start_gcode : text tree (opt)  Override for the START block. Accepts single
                                          (multi-line) text, a list, or a tree of strings.
                                          Empty -> internal default FDM start sequence.
 1) custom_end_gcode   : text tree (opt)  Override for the END block. Same formats.
 2) nozzle_diameter    : double           Nozzle diameter in mm (required by the generator).
 3) fillament_multi    : double (opt)     Filament diameter in mm for E-axis conversion.
                                          Default 1.75 (standard FDM).
 4) layer_w            : double (opt)     Nominal/base width override for Gc03; otherwise Gc03 uses wsp_path.LayerW or layer_h x 2.5.
 5) printing_speed     : tree   (opt)     Print feedrate mm/min; value or per-point tree.
                                          Default 1200.
 6) travel_speed       : double (opt)     Travel feedrate mm/min. Default 5000.
 7) z_hop              : double (opt)     Z-hop in mm. Default 0 = disabled; use > 0 to enable.
 8) z_hop_speed        : double (opt)     Z-hop feedrate mm/min. Default 3000.
 9) fan_speed          : tree   (opt)     Cooling fan per layer (M106 S). Scalar, per-layer
                                          list, or {layer}/{layer;0} tree. 0-255 Marlin units;
                                          values <= 100 are treated as % and scaled to 0-255.
10) temp_nozzle        : double (opt)     Nozzle setpoint in C. Default 200.
11) temp_bed           : double (opt)     Bed setpoint in C. Default 60. 0 is allowed (off).
12) wsp_mat      : WasperMaterial (opt)     Material record whose density is packed into 3dp_params. Default 1240 kg/m3
                                          (PLA-like). The material is resolved here before reaching the generator.

OUTPUTS
0) 3dp_params : Wasper3dpParams   Packed parameter object (Process = FDM).
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
    public sealed class wsp_Gc02_FDM_3DP_Parameters : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Gc02_FDM_3DP_Parameters()
            : base(
                "wsp_Gc02_FDM 3DP Parameters",
                "FDM 3DP Params",
                "Packs FDM printing parameters (nozzle, filament, widths, speeds, z-hop, " +
                "fan, temperatures, custom start/end blocks, density) into one 3dp_params " +
                "object (Process = FDM) for the Marlin G-code generator.\r\n" +
                "Only the inputs you wire are carried; everything else falls back to the " +
                "generator's FDM defaults.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "5.0_Gcode")
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = $"{_versionTag} - FDM";
        }

        public override Guid ComponentGuid =>
            new Guid("9A3D5F82-7C16-4E4B-B8D2-4F0A6C315E97");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Gc03_FDM 3DP Parameters.png"))
                        return s != null ? new System.Drawing.Bitmap(s) : null;
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            int i;

            i = p.AddTextParameter("custom_start_gcode", "custom_start",
                "Optional override for the START block. Accepts a single (multi-line) " +
                "text, a list, or a tree of strings. Blank lines are dropped. " +
                "Empty -> the generator's default FDM start sequence (temperatures, " +
                "homing, purge lines).",
                GH_ParamAccess.tree);
            p[i].Optional = true;

            i = p.AddTextParameter("custom_end_gcode", "custom_end",
                "Optional override for the END block. Same accepted formats as " +
                "custom_start_gcode. Empty -> the generator's default FDM end sequence.",
                GH_ParamAccess.tree);
            p[i].Optional = true;

            i = p.AddNumberParameter("nozzle_diameter", "nozzle_diameter",
                "Nozzle diameter in mm. It defines the nominal deposited cross-section used for E = V / A_fil through the process volume model. Required by the Marlin G-code generator.",
                GH_ParamAccess.item);
            p[i].Optional = true;

            i = p.AddNumberParameter("fillament_multi", "filament_diam",
                "Filament diameter in mm. It defines A_fil = pi * (fillament_multi / 2)^2 for E = V / A_fil. Default: 1.75.",
                GH_ParamAccess.item);
            p[i].Optional = true;

            i = p.AddNumberParameter("layer_w", "layer_w",
                "Optional nominal/base bead width before flow adjustment, in mm. When passed to Gc03 through 3dp_params, it overrides wsp_path.LayerW; if omitted, Gc03 preserves the incoming path value, or defaults to layer_h * 2.5 when no path value exists. Gc03 stores the resolved nominal width as LayerW, estimates LayerWf from local flow and layer_h, and updates per-segment PrintVol.",
                GH_ParamAccess.item);
            p[i].Optional = true;

            i = p.AddNumberParameter("printing_speed", "print_speed",
                "Optional explicit print feedrate in mm/min. Single value or per-point tree " +
                "matching canonical pt_planes {layer;curve}. When passed to Gc03, overrides print_speed from wsp_path. " +
                "If both are unwired, the generator default is 1200.",
                GH_ParamAccess.tree);
            p[i].Optional = true;

            i = p.AddNumberParameter("travel_speed", "travel_speed",
                "Travel feedrate in mm/min (G0). Generator default: 5000.",
                GH_ParamAccess.item);
            p[i].Optional = true;

            i = p.AddNumberParameter("z_hop", "z_hop",
                "Z-hop height in mm for travels. Default 0 disables z-hop completely. Input a value > 0 to enable positive-Z hop moves.",
                GH_ParamAccess.item,
                0.0);
            p[i].Optional = true;

            i = p.AddNumberParameter("z_hop_speed", "z_hop_speed",
                "Z-hop feedrate in mm/min. Generator default: 3000.",
                GH_ParamAccess.item);
            p[i].Optional = true;

            i = p.AddNumberParameter("fan_speed", "fan_speed",
                "Cooling fan speed applied per layer (M106 S). Accepts a single value " +
                "(all layers), a per-layer list (last value repeats if shorter), or a " +
                "{layer} / {layer;0} tree. 0-255 Marlin units; values <= 100 are treated " +
                "as % and auto-scaled to 0-255.",
                GH_ParamAccess.tree);
            p[i].Optional = true;

            i = p.AddNumberParameter("temp_nozzle", "temp_nozzle",
                "Nozzle temperature setpoint in Celsius. Generator default: 200.",
                GH_ParamAccess.item);
            p[i].Optional = true;

            i = p.AddNumberParameter("temp_bed", "temp_bed",
                "Bed temperature setpoint in Celsius. Generator default: 60. " +
                "0 is allowed (bed off).",
                GH_ParamAccess.item);
            p[i].Optional = true;

            i = p.AddGenericParameter("wsp_mat", "wsp_mat",
                "Material density in kg/m3 for mass estimation. Generator default: 1240 " +
                "(PLA-like). The material is resolved here before reaching the generator.",
                GH_ParamAccess.item);
            p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("3dp_params", "3dp_params",
                "Packed WASPer 3DP parameters object (Process = FDM). Wire into the " +
                "Marlin G-code generator.",
                GH_ParamAccess.item);

            p.AddTextParameter("info", "info",
                "Summary of the fields carried by this parameter object.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var prm = new Wasper3dpParams { Process = Wasper3dpProcess.FDM };

            if (DA.GetDataTree(0, out GH_Structure<GH_String> startS))
                prm.CustomStartGcode = WasperGcodeTreeUtil.FlattenGcodeText(startS);
            if (DA.GetDataTree(1, out GH_Structure<GH_String> endS))
                prm.CustomEndGcode = WasperGcodeTreeUtil.FlattenGcodeText(endS);

            prm.NozzleDiameter = ReadPositive(DA, 2, "nozzle_diameter");
            prm.FillamentMulti = ReadPositive(DA, 3, "fillament_multi");
            prm.LayerW         = ReadPositive(DA, 4, "layer_w");

            if (DA.GetDataTree(5, out GH_Structure<GH_Number> speedTree) &&
                speedTree != null && !speedTree.IsEmpty)
            {
                prm.PrintSpeed = WasperGcodeTreeUtil.ToDoubleTree(speedTree);
            }


            prm.TravelSpeed = ReadPositive(DA, 6, "travel_speed");
            prm.ZHop        = ReadNonNegative(DA, 7, "z_hop");
            prm.ZHopSpeed   = ReadPositive(DA, 8, "z_hop_speed");

            if (DA.GetDataTree(9, out GH_Structure<GH_Number> fanTree) &&
                fanTree != null && !fanTree.IsEmpty)
            {
                prm.FanSpeed = WasperGcodeTreeUtil.ToDoubleTree(fanTree);
            }

            prm.TempNozzle = ReadPositive(DA, 10, "temp_nozzle");

            // temp_bed: 0 is a valid value (bed off) — python allow_zero=True
            double bed = 0.0;
            if (DA.GetData(11, ref bed))
            {
                if (double.IsNaN(bed) || double.IsInfinity(bed) || bed < 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "'temp_bed' must be >= 0 C; the value is ignored " +
                        "(the generator default of 60 C will apply).");
                }
                else
                {
                    prm.TempBed = bed;
                }
            }

            double materialDensity;
            if (WasperGcodeTreeUtil.TryGetMaterialDensity(DA, 12, out materialDensity))
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
