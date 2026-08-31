using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Grasshopper.Kernel;
using WASPer_3DP;

public sealed class wsp_Ma12_3DP_Properties : GH_Component
{
    public wsp_Ma12_3DP_Properties()
        : base("wsp_Ma12_3DP Properties", "3DP Props",
            "Creates optional fresh-state and fabrication properties for WASPer printing workflows. Presets are starting points only and require calibration for a specific material and process.",
            global::WASPer_3DP.WASPerPalette.Performance, "7_Material Library")
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        Message = v == null ? "v1.0.x" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    public override Guid ComponentGuid => new("C69E9B56-8E72-4B11-9B66-4F9D3A0B5B21");

    public override GH_Exposure Exposure => GH_Exposure.secondary;

    protected override System.Drawing.Bitmap Icon
    {
        get
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ma12_3DP Properties.png");
                return stream == null ? null : new System.Drawing.Bitmap(stream);
            }
            catch { return null; }
        }
    }

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddTextParameter("preset", "preset", "Material preset label. Examples: Generic clay, Soft clay, Stiff clay, Mortar, Paste, FDM polymer, or Custom. The label does not replace calibration.", GH_ParamAccess.item, "Generic clay");
        p.AddNumberParameter("tau_y0", "tau_y0", "Initial fresh yield stress in Pa: the approximate stress required to start flow immediately after deposition. Example: 1000 Pa for a generic clay starting point.", GH_ParamAccess.item, 1000.0);
        p.AddNumberParameter("A_thix", "A_thix", "Linear structuration rate in Pa/s: the approximate increase of yield stress while the material rests between layers. Example: 2 Pa/s.", GH_ParamAccess.item, 2.0);
        p.AddNumberParameter("E_fresh", "E_fresh", "Fresh elastic modulus in Pa: an optional small-strain stiffness before full hardening. Example: 10000 Pa. It is currently stored for reporting and is not used by Pr03 v1.", GH_ParamAccess.item, 10000.0);
        p.AddNumberParameter("density_wet", "density_wet", "Wet/fresh bulk density in kg/m^3, including water. Example: 1800 kg/m^3 for a generic clay or paste mix.", GH_ParamAccess.item, 1800.0);
        p.AddNumberParameter("w_wet", "w_wet", "Wet-basis water content as a fraction of total wet mass, between 0 and 1. Example: 0.25 means 25% of the wet mass is water.", GH_ParamAccess.item, 0.25);
        p.AddNumberParameter("k_shape", "k_shape", "Positive dimensionless section-shape calibration factor used by Pr04 to scale the nominal bead area and second moment of area. Use 1.0 for the nominal rectangular-bead approximation; calibrate against fresh-bead or bridge tests when available.", GH_ParamAccess.item, 1.0);
        p.AddNumberParameter("k_fix", "k_fix", "Positive dimensionless rotational-restraint calibration factor used by Pr04. Use 1.0 as the neutral starting value; values above 1.0 produce more predicted deflection and values below 1.0 produce less. Calibrate against printing tests rather than treating it as a material constant.", GH_ParamAccess.item, 1.0);
        p.AddNumberParameter("tau_interface", "tau_interface", "Optional initial fresh interlayer shear/yield capacity in Pa. Pr04 compares the accumulated fabrication load on each connected contact patch with this age-dependent capacity; values below the demand can trigger loss of vertical support and further collapse. Use a measured value for the same material, nozzle, layer time, and surface condition. When omitted, Pr04 clearly reports its uncalibrated tau_y0 fallback.", GH_ParamAccess.item);
        p.AddNumberParameter("A_interface", "A_interface", "Non-negative interface structuration rate in Pa/s: the assumed increase of tau_interface while the supporting contact ages. Pr04 uses tau_interface(t) = tau_interface + A_interface*t with age measured from deposition of the supporting layer. Use 0 when no time-dependent interface-strength data are available.", GH_ParamAccess.item, 0.0);
        for (int i = 1; i < p.ParamCount; i++) p[i].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddGenericParameter("3dp_props", "3dp_props", "Packed optional 3D-printing properties.", GH_ParamAccess.item);
        p.AddTextParameter("summary", "summary", "Resolved property summary.", GH_ParamAccess.item);
        p.AddTextParameter("warnings", "warnings", "Validation warnings.", GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        string preset = "Custom";
        da.GetData(0, ref preset);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["preset"] = string.IsNullOrWhiteSpace(preset) ? "Custom" : preset
        };

        AddNumber(da, 1, values, "tau_y0");
        AddNumber(da, 2, values, "A_thix");
        AddNumber(da, 3, values, "E_fresh");
        AddNumber(da, 4, values, "density_wet");
        AddNumber(da, 5, values, "w_wet");
        AddNumber(da, 6, values, "k_shape");
        AddNumber(da, 7, values, "k_fix");
        AddNumber(da, 8, values, "tau_interface");
        AddNumber(da, 9, values, "A_interface");

        var warnings = new List<string>();
        ValidatePositive(values, "tau_y0", warnings);
        ValidatePositive(values, "density_wet", warnings);
        ValidatePositive(values, "k_shape", warnings);
        ValidatePositive(values, "k_fix", warnings);
        ValidatePositive(values, "tau_interface", warnings);
        ValidateNonNegative(values, "A_interface", warnings);
        ValidateFraction(values, "w_wet", warnings);

        var props = new Wasper3dpProperties(values);
        da.SetData(0, new Wasper3dpPropertiesGoo(props));
        da.SetData(1, BuildSummary(values));
        da.SetDataList(2, warnings);
        Message = warnings.Count == 0 ? values["preset"] : $"{values["preset"]} ({warnings.Count} warning{(warnings.Count == 1 ? "" : "s")})";
    }

    private static void AddNumber(IGH_DataAccess da, int index, IDictionary<string, string> values, string key)
    {
        double value = 0;
        if (da.GetData(index, ref value)) values[key] = value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static void ValidatePositive(IDictionary<string, string> values, string key, ICollection<string> warnings)
    {
        if (values.TryGetValue(key, out var text) &&
            (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value <= 0))
            warnings.Add($"{key} must be positive and finite.");
    }

    private static void ValidateFraction(IDictionary<string, string> values, string key, ICollection<string> warnings)
    {
        if (values.TryGetValue(key, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && (value < 0 || value >= 1))
            warnings.Add($"{key} must be in the interval [0, 1).");
    }

    private static void ValidateNonNegative(IDictionary<string, string> values, string key, ICollection<string> warnings)
    {
        if (values.TryGetValue(key, out var text) &&
            (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value < 0))
            warnings.Add($"{key} must be finite and zero or greater.");
    }

    private static string BuildSummary(IReadOnlyDictionary<string, string> values)
    {
        return string.Join("\n", values.Select(kv => $"{kv.Key}: {kv.Value}"));
    }

}
