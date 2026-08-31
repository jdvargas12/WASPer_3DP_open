using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using WASPer_3DP;

public sealed class wsp_Ma03_CreateWasperMaterial : GH_Component
{
    public wsp_Ma03_CreateWasperMaterial()
        : base("wsp_Ma03_Create WASPer Material (Solid)", "Create Mat",
            "Creates a reusable WASPer Material directly in Grasshopper. The inputs mirror the solid-material properties used by Ma01; optional properties that are not supplied are omitted instead of receiving invented values.", global::WASPer_3DP.WASPerPalette.Performance, "7_Material Library")
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        Message = v == null ? "v1.0.x" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    public override Guid ComponentGuid => new("36885B0C-26CF-43D5-A41D-48ACB7582F01");

    protected override System.Drawing.Bitmap Icon
    {
        get
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ma03_Create WASPer Material.png");
                return stream == null ? null : new System.Drawing.Bitmap(stream);
            }
            catch { return null; }
        }
    }

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("wsp_mat_ref", "wsp_mat_ref", "Optional solid WASPer Material to use as the starting point. Its properties are copied into a new material; only explicitly supplied inputs below override them. A gas material is rejected.", GH_ParamAccess.item);
        p.AddTextParameter("mat_name", "mat_name", "Material name stored as Material_Name. This identifies the material in panels and downstream WASPer components. Default when no reference is connected: Custom Material.", GH_ParamAccess.item);
        p.AddTextParameter("category", "category", "Material family or classification, for example Earth_clays, Concrete, Insulation, or Custom. Default when no reference is connected: Custom.", GH_ParamAccess.item);
        p.AddTextParameter("function", "function", "Intended building or printing function, for example Structure, Finish, or Insulation. Default when no reference is connected: Structure.", GH_ParamAccess.item);
        p.AddNumberParameter("density", "density", "Bulk density in kg/m^3.", GH_ParamAccess.item);
        p.AddNumberParameter("specific_heat", "spec_heat", "Specific heat capacity in J/(kg*K).", GH_ParamAccess.item);
        p.AddNumberParameter("thermal_conductivity", "conductivity", "Thermal conductivity in W/(m*K).", GH_ParamAccess.item);
        p.AddNumberParameter("emissivity", "emissivity", "Long-wave thermal emissivity as a dimensionless value, normally between 0 and 1.", GH_ParamAccess.item);
        p.AddNumberParameter("solar_absorptance", "solar_abs", "Fraction of incident solar radiation absorbed by the material, normally between 0 and 1.", GH_ParamAccess.item);
        p.AddNumberParameter("thermal_absorptance", "thermal_abs", "Fraction of incident thermal radiation absorbed by the material, normally between 0 and 1.", GH_ParamAccess.item);
        p.AddNumberParameter("visible_absorptance", "visible_abs", "Fraction of incident visible radiation absorbed by the material, normally between 0 and 1.", GH_ParamAccess.item);
        p.AddTextParameter("roughness", "roughness", "Surface roughness classification, for example VeryRough, Rough, MediumRough, MediumSmooth, Smooth, or VerySmooth.", GH_ParamAccess.item);
        p.AddTextParameter("notes", "notes", "Optional free-text description, provenance, assumptions, or other material notes.", GH_ParamAccess.item);
        p.AddGenericParameter("3dp_props", "3dp_props", "Optional fresh-state and fabrication properties from Ma12. These are merged last and therefore override matching reference or solid inputs. Example: connect Ma12.3dp_props here, then connect Ma03.wasper_mat to Pr03.wsp_mat.", GH_ParamAccess.item);
        p.AddGenericParameter("moisture_props", "moisture_props", "Optional intrinsic moisture vapor transport properties from Ma13. These are merged into the material under the Moisture.* namespace, so the resulting wasper_mat carries them into Ma07 layers and downstream components.", GH_ParamAccess.item);

        for (int i = 0; i < p.ParamCount; i++) p[i].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddGenericParameter("wasper_mat", "wasper_mat", "Complete WASPer Material object containing the supplied solid-material properties, units, solid phase, and Grasshopper source metadata. Connect it directly to WASPer material-aware components or Ma04.", GH_ParamAccess.item);
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
            if (!string.Equals(reference.Phase, "Solid", StringComparison.OrdinalIgnoreCase))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Ma03 requires a solid WASPer Material. Received phase: {reference.Phase}.");
                return;
            }
        }

        string name = reference?.Name ?? "Custom Material";
        string category = GetReferenceOrDefault(reference, "Category", "Custom");
        string function = GetReferenceOrDefault(reference, "Function", "Structure");
        GetTextOverride(da, 1, ref name);
        GetTextOverride(da, 2, ref category);
        GetTextOverride(da, 3, ref function);

        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (reference != null)
            foreach (var item in reference.Properties) properties[item.Key] = item.Value;
        properties["Category"] = category;
        properties["Material_Name"] = name;
        properties["Function"] = function;

        AddNumber(da, 4, properties, "Density (kg/m\u00B3)");
        AddNumber(da, 5, properties, "Specific_Heat (J/kg\u00B7K)");
        AddNumber(da, 6, properties, "Thermal_Conductivity (W/m\u00B7K)");
        AddNumber(da, 7, properties, "Emissivity");
        AddNumber(da, 8, properties, "Solar_Absorptance");
        AddNumber(da, 9, properties, "Thermal_Absorptance");
        AddNumber(da, 10, properties, "Visible_Absorptance");
        AddText(da, 11, properties, "Roughness");
        AddText(da, 12, properties, "Notes");

        object raw3dp = null;
        if (da.GetData(13, ref raw3dp))
        {
            var props = Extract3dpProperties(raw3dp);
            if (props != null)
            {
                foreach (var item in props.Properties)
                    properties[item.Key] = item.Value;
            }
        }

        object rawMoisture = null;
        if (da.GetData(14, ref rawMoisture))
        {
            var moistureProps = ExtractMoistureTransportProperties(rawMoisture);
            if (moistureProps != null)
            {
                foreach (var item in moistureProps.Properties)
                    properties[item.Key] = item.Value;
            }
            else
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "moisture_props is not a valid Ma13 moisture transport property object and was ignored.");
            }
        }

        da.SetData(0, new WasperMaterialGoo(new WasperMaterial(name, "Solid", properties, "Created in Grasshopper by Ma03")));
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

    private static Wasper3dpProperties Extract3dpProperties(object input)
    {
        if (input is Wasper3dpProperties value) return value;
        if (input is Wasper3dpPropertiesGoo goo) return goo.Value;
        if (input is Grasshopper.Kernel.Types.GH_ObjectWrapper wrapper)
            return Extract3dpProperties(wrapper.Value);
        return null;
    }

    private static WasperMoistureTransportProperties ExtractMoistureTransportProperties(object input)
    {
        if (input is WasperMoistureTransportProperties value) return value;
        if (input is WasperMoistureTransportPropertiesGoo goo) return goo.Value;
        if (input is Grasshopper.Kernel.Types.GH_ObjectWrapper wrapper)
            return ExtractMoistureTransportProperties(wrapper.Value);
        return null;
    }
}
