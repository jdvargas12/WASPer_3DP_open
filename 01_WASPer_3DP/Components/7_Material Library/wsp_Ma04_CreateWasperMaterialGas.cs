using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using WASPer_3DP;

public sealed class wsp_Ma04_CreateWasperMaterialGas : GH_Component
{
    public wsp_Ma04_CreateWasperMaterialGas()
        : base("wsp_Ma04_Create WASPer Material (Gas)", "Create Gas Mat",
            "Creates a reusable WASPer Material for a gas directly in Grasshopper. The inputs mirror the eight tabulated air properties " +
            "used by Ma02 (density, specific heat, thermal conductivity, thermal diffusivity, dynamic viscosity, kinematic viscosity, " +
            "Prandtl number, and thermal-expansion coefficient), so a custom gas created here can be read by Ma05 (Inspect) and Ma06 " +
            "(Air Equivalent λ) exactly like Ma02's air output. Optional properties that are not supplied are omitted instead of " +
            "receiving invented values.", global::WASPer_3DP.WASPerPalette.Performance, "7_Material Library")
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        Message = v == null ? "v1.0.x" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    public override Guid ComponentGuid => new("5E4A2F1C-9B3D-4A67-8C05-1F6E3D9A7B24");

    protected override System.Drawing.Bitmap Icon
    {
        get
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ma04_Create WASPer Material (Gas).png");
                return stream == null ? null : new System.Drawing.Bitmap(stream);
            }
            catch { return null; }
        }
    }

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("wsp_mat_ref", "wsp_mat_ref", "Optional gas WASPer Material to use as the starting point. Its properties are copied into a new gas material; only explicitly supplied inputs below override them. A solid material is rejected.", GH_ParamAccess.item);
        p.AddTextParameter("mat_name", "mat_name", "Material name stored as Material_Name. This identifies the material in panels and downstream WASPer components. Default when no reference is connected: Custom Gas.", GH_ParamAccess.item);
        p.AddTextParameter("category", "category", "Gas family or classification, for example Air, CO2, Argon, or Custom. Default when no reference is connected: Custom.", GH_ParamAccess.item);
        p.AddTextParameter("function", "function", "Intended building or printing function, for example Insulation Void, Air Cavity, or Fluid. Default when no reference is connected: Insulation Void.", GH_ParamAccess.item);
        p.AddNumberParameter("density", "density", "Gas density in kg/m^3.", GH_ParamAccess.item);
        p.AddNumberParameter("specific_heat", "spec_heat", "Specific heat capacity in J/(kg*K).", GH_ParamAccess.item);
        p.AddNumberParameter("thermal_conductivity", "conductivity", "Thermal conductivity in W/(m*K).", GH_ParamAccess.item);
        p.AddNumberParameter("thermal_diffusivity", "diffusivity", "Thermal diffusivity in m^2/s.", GH_ParamAccess.item);
        p.AddNumberParameter("dynamic_viscosity", "dyn_visc", "Dynamic viscosity in kg/(m*s).", GH_ParamAccess.item);
        p.AddNumberParameter("kinematic_viscosity", "kin_visc", "Kinematic viscosity in m^2/s.", GH_ParamAccess.item);
        p.AddNumberParameter("prandtl_number", "Pr", "Prandtl number, dimensionless.", GH_ParamAccess.item);
        p.AddNumberParameter("thermal_expansion_coeff", "beta", "Thermal expansion coefficient in 1/K. For an ideal gas this equals 1 / (temperature_C + 273.15).", GH_ParamAccess.item);
        p.AddNumberParameter("pressure_kPa", "pressure", "Reference pressure in kPa. Default when no reference is connected: 101.325 (standard atmosphere).", GH_ParamAccess.item);
        p.AddNumberParameter("temperature_C", "temp", "Reference temperature in degrees C that the supplied properties correspond to. Stored as metadata only; no lookup or interpolation is performed.", GH_ParamAccess.item);
        p.AddTextParameter("notes", "notes", "Optional free-text description, provenance, assumptions, or other material notes.", GH_ParamAccess.item);

        for (int i = 0; i < p.ParamCount; i++) p[i].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddGenericParameter("wasper_mat", "wasper_mat", "Complete WASPer Material object containing the supplied gas properties, units, gas phase, and Grasshopper source metadata. Connect it directly to WASPer material-aware components or Ma05.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        object rawReference = null;
        WasperMaterial reference = null;
        if (da.GetData(0, ref rawReference))
        {
            reference = ExtractMaterial(rawReference);
            if (reference == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "wsp_mat_ref is not a valid WASPer Material.");
                return;
            }
            if (!string.Equals(reference.Phase, "Gas", StringComparison.OrdinalIgnoreCase))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Ma04 requires a gas WASPer Material. Received phase: {reference.Phase}.");
                return;
            }
        }

        string name = reference?.Name ?? "Custom Gas";
        string category = GetReferenceOrDefault(reference, "Category", "Custom");
        string function = GetReferenceOrDefault(reference, "Function", "Insulation Void");
        GetTextOverride(da, 1, ref name);
        GetTextOverride(da, 2, ref category);
        GetTextOverride(da, 3, ref function);

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (reference != null)
            foreach (var item in reference.Properties) properties[item.Key] = item.Value;
        properties["Category"] = category;
        properties["Material_Name"] = name;
        properties["Function"] = function;

        AddNumber(da, 4, properties, "Density (kg/m^3)");
        AddNumber(da, 5, properties, "Specific Heat (J/kg-K)");
        AddNumber(da, 6, properties, "Thermal Conductivity (W/m-K)");
        AddNumber(da, 7, properties, "Thermal Diffusivity (m^2/s)");
        AddNumber(da, 8, properties, "Dynamic Viscosity (kg/m-s)");
        AddNumber(da, 9, properties, "Kinematic Viscosity (m^2/s)");
        AddNumber(da, 10, properties, "Prandtl Number");
        AddNumber(da, 11, properties, "Thermal Expansion Coefficient (1/K)");
        AddNumber(da, 12, properties, "Pressure (kPa)");
        if (!properties.ContainsKey("Pressure (kPa)")) properties["Pressure (kPa)"] = "101.325";
        AddNumber(da, 13, properties, "Requested Temperature (°C)");
        AddText(da, 14, properties, "Notes");

        da.SetData(0, new WasperMaterialGoo(new WasperMaterial(name, "Gas", properties, "Created in Grasshopper by Ma04")));
    }

    private static void AddNumber(IGH_DataAccess da, int index, IDictionary<string, string> properties, string key)
    {
        double value = 0.0;
        if (da.GetData(index, ref value)) properties[key] = value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static void AddText(IGH_DataAccess da, int index, IDictionary<string, string> properties, string key)
    {
        string value = null;
        if (da.GetData(index, ref value) && !string.IsNullOrWhiteSpace(value)) properties[key] = value;
    }

    private static void GetTextOverride(IGH_DataAccess da, int index, ref string value)
    {
        string candidate = null;
        if (da.GetData(index, ref candidate) && !string.IsNullOrWhiteSpace(candidate)) value = candidate;
    }

    private static string GetReferenceOrDefault(WasperMaterial material, string key, string fallback)
    {
        return material != null && material.TryGet(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
    }

    private static WasperMaterial ExtractMaterial(object input)
    {
        if (input is WasperMaterial value) return value;
        if (input is WasperMaterialGoo goo) return goo.Value;
        if (input is GH_ObjectWrapper wrapper) return ExtractMaterial(wrapper.Value);
        return null;
    }
}
