using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using WASPer_3DP;

public sealed class wsp_Ma05_InspectWasperMaterial : GH_Component
{
    private readonly string _versionTag;

    public wsp_Ma05_InspectWasperMaterial()
        : base("wsp_Ma05_Inspect WASPer Material", "Inspect Mat",
            "Inspects any WASPer Material, including solids and gases, and returns two aligned lists containing every property stored by that material. Numeric properties remain numbers; descriptive properties remain text.", global::WASPer_3DP.WASPerPalette.Performance, "7_Material Library")
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        _versionTag = version == null ? "v1.0.x" : $"v{version.Major}.{version.Minor}.{version.Build}";
        Message = _versionTag;
    }

    public override Guid ComponentGuid => new("30AA3AD5-B7EE-4859-97AF-A78A57995835");

    protected override System.Drawing.Bitmap Icon
    {
        get
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ma05_Inspect WASPer Material.png");
                return stream == null ? null : new System.Drawing.Bitmap(stream);
            }
            catch { return null; }
        }
    }

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("wasper_mat", "wasper_mat", "Any WASPer Material object, including a solid from Ma01/Ma03, a gas from Ma02/Ma04, or equivalent air from Ma06.", GH_ParamAccess.item);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddTextParameter("mat_name", "mat_name", "Name of the connected WASPer Material.", GH_ParamAccess.item);
        p.AddTextParameter("prop_names", "prop_names", "Names of every property stored in wasper_mat. Item order corresponds exactly to prop_vals.", GH_ParamAccess.list);
        p.AddGenericParameter("prop_vals", "prop_vals", "Values of every stored material property in prop_names order. Numeric values are output as numbers; text values are output as strings.", GH_ParamAccess.list);
        p.AddTextParameter("full_mat_p", "full_mat_p", "Readable material summary combining metadata and every property as 'property [unit] = value'. Units already contained in property names are preserved; known dimensionless properties are marked [-].", GH_ParamAccess.list);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        object input = null;
        if (!da.GetData(0, ref input))
        {
            Message = _versionTag;
            return;
        }

        WasperMaterial material = Extract(input);
        if (material == null)
        {
            Message = _versionTag;
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is not a valid WASPer Material. Connect a material output from Ma01, Ma02, Ma03, Ma04, or Ma06.");
            return;
        }

        Message = $"{_versionTag}\n{material.Name}";
        var names = new List<string>(material.Properties.Count);
        var values = new List<IGH_Goo>(material.Properties.Count);
        var fullMaterial = new List<string>(material.Properties.Count + 3)
        {
            $"Material Name = {material.Name}",
            $"Phase = {material.Phase}",
            $"Source = {material.Source}"
        };

        foreach (var property in material.Properties)
        {
            names.Add(property.Key);
            fullMaterial.Add($"{DisplayNameWithUnits(property.Key)} = {property.Value}");
            if (double.TryParse(property.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double numeric))
                values.Add(new GH_Number(numeric));
            else
                values.Add(new GH_String(property.Value ?? string.Empty));
        }

        da.SetData(0, material.Name);
        da.SetDataList(1, names);
        da.SetDataList(2, values);
        da.SetDataList(3, fullMaterial);
    }

    private static string DisplayNameWithUnits(string propertyName)
    {
        if (propertyName.IndexOf('(') >= 0 || propertyName.IndexOf('[') >= 0)
            return propertyName;

        switch (propertyName)
        {
            case "Emissivity":
            case "Solar_Absorptance":
            case "Thermal_Absorptance":
            case "Visible_Absorptance":
            case "Prandtl Number":
            case "Nusselt Number":
                return propertyName + " [-]";
            default:
                return propertyName;
        }
    }

    private static WasperMaterial Extract(object input)
    {
        if (input is WasperMaterial material) return material;
        if (input is WasperMaterialGoo materialGoo) return materialGoo.Value;
        if (input is GH_ObjectWrapper wrapper) return Extract(wrapper.Value);
        if (input is IGH_Goo goo)
        {
            object value = goo.ScriptVariable();
            if (value != null && !ReferenceEquals(value, input)) return Extract(value);
        }
        return null;
    }
}
