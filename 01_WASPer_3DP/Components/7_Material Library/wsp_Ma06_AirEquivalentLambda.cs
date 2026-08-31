using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using WASPer_3DP;

public sealed class wsp_Ma06_AirEquivalentLambda : GH_Component
{
    private static readonly string[] ConductivityKeys =
    {
        "Thermal Conductivity (W/m-K)",
        "Thermal_Conductivity (W/m·K)",
        "Thermal Conductivity (W/mK)",
        "Thermal_Conductivity (W/mK)"
    };

    public wsp_Ma06_AirEquivalentLambda()
        : base("wsp_Ma06_Air Equivalent λ (Cond+Rad+Conv)", "Air Eq λ",
            "Creates an equivalent WASPer air material for numerical heat-transfer models by combining base air conduction, natural-convection enhancement, and radiation. It is designed to work directly with outputs from wsp_Ht01_Analytical Solver Steady (ISO 6946_2017): λvoid_eff, Nu_values, and λrad_add. If λ_void_eff is supplied, that already-combined value takes precedence. Otherwise the component calculates λ_eq = Nu × λ_air + λ_rad_add.", global::WASPer_3DP.WASPerPalette.Performance, "7_Material Library")
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Message = version == null ? "v1.0.x" : $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    public override Guid ComponentGuid => new("2C86E8F5-6E0A-436F-A64A-D3BF0B645E72");

    protected override System.Drawing.Bitmap Icon
    {
        get
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ma06_Air Equivalent Lambda.png");
                return stream == null ? null : new System.Drawing.Bitmap(stream);
            }
            catch { return null; }
        }
    }

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("wasper_air", "wasper_air", "WASPer gas material (typically air) produced by Ma02 or Ma04. Its base thermal conductivity λ_air and all other properties are preserved. Combine this input with conductivity outputs from wsp_Ht01_Analytical Solver Steady (ISO 6946_2017).", GH_ParamAccess.item);
        p.AddNumberParameter("λ_void_eff", "λ_void_eff", "Optional equivalent void/air thermal conductivity in W/(m*K). Connect the λvoid_eff output from wsp_Ht01_Analytical Solver Steady (ISO 6946_2017) to use Ht01's already-combined conduction, convection, and radiation result directly. When supplied, Nu and λ_rad_add are not added again.", GH_ParamAccess.item);
        p[1].Optional = true;
        p.AddNumberParameter("Nu", "Nu", "Nusselt number used to enhance base air conductivity for natural convection. Connect Nu_values from wsp_Ht01_Analytical Solver Steady (ISO 6946_2017). Nu = 1 means conduction only. Used only when λ_void_eff is not supplied. Default: 1.", GH_ParamAccess.item, 1.0);
        p.AddNumberParameter("λ_rad_add", "λ_rad_add", "Radiative conductivity add-on in W/(m*K). Connect λrad_add from wsp_Ht01_Analytical Solver Steady (ISO 6946_2017). It is added after the Nu enhancement only when λ_void_eff is not supplied. Default: 0.", GH_ParamAccess.item, 0.0);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddGenericParameter("eq_air_wasper_mat", "eq_air_wasper_mat", "Equivalent WASPer air material whose Thermal Conductivity property is λ_eq. The object preserves the source air data and records λ_air, Nu, λ_rad_add, λ_eq, and whether the value was direct or calculated.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        object input = null;
        if (!da.GetData(0, ref input)) return;
        WasperMaterial air = ExtractMaterial(input);
        if (air == null)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "wasper_air is not a valid WASPer Material. Connect the wasper_mat output from Ma02 or Ma04.");
            return;
        }
        if (!string.Equals(air.Phase, "Gas", StringComparison.OrdinalIgnoreCase))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"wasper_air must be a gas material; received phase '{air.Phase}'.");
            return;
        }

        double directLambda = 0.0;
        bool hasDirectLambda = da.GetData(1, ref directLambda);
        double nu = 1.0, lambdaRadiation = 0.0;
        da.GetData(2, ref nu);
        da.GetData(3, ref lambdaRadiation);

        if (hasDirectLambda && directLambda <= 0.0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "λ_void_eff must be greater than zero when supplied.");
            return;
        }
        if (!hasDirectLambda && nu < 1.0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Nu must be at least 1.0 for this natural-convection enhancement model.");
            return;
        }
        if (!hasDirectLambda && lambdaRadiation < 0.0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "λ_rad_add cannot be negative.");
            return;
        }

        double lambdaAir = 0.0;
        bool hasLambdaAir = TryGetDouble(air, ConductivityKeys, out lambdaAir);
        if (!hasDirectLambda && (!hasLambdaAir || lambdaAir <= 0.0))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "wasper_air does not contain a valid positive base Thermal Conductivity property.");
            return;
        }

        double equivalentLambda = hasDirectLambda ? directLambda : nu * lambdaAir + lambdaRadiation;
        var properties = air.Properties.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        foreach (string key in ConductivityKeys) properties.Remove(key);

        if (hasLambdaAir)
            properties["Base Air Thermal Conductivity (W/m-K)"] = Format(lambdaAir);
        properties["Thermal Conductivity (W/m-K)"] = Format(equivalentLambda);
        properties["Equivalent Air Conductivity (W/m-K)"] = Format(equivalentLambda);
        properties["Equivalent Conductivity Method"] = hasDirectLambda
            ? "Direct λ_void_eff"
            : "λ_eq = Nu * λ_air + λ_rad_add";

        if (hasDirectLambda)
        {
            properties["Input λ_void_eff (W/m-K)"] = Format(directLambda);
            if (Params.Input[2].SourceCount > 0 || Params.Input[3].SourceCount > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "λ_void_eff is connected, so Nu and λ_rad_add are intentionally not added again.");
        }
        else
        {
            properties["Nusselt Number"] = Format(nu);
            properties["Radiative Conductivity Add-On (W/m-K)"] = Format(lambdaRadiation);
        }

        var equivalentAir = new WasperMaterial(
            $"{air.Name} Equivalent (Cond+Rad+Conv)",
            "Gas",
            properties,
            $"Ma06 from {air.Source}");
        da.SetData(0, new WasperMaterialGoo(equivalentAir));
    }

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static bool TryGetDouble(WasperMaterial material, IEnumerable<string> keys, out double value)
    {
        foreach (string key in keys)
            if (material.TryGetDouble(key, out value)) return true;
        value = 0.0;
        return false;
    }

    private static WasperMaterial ExtractMaterial(object input)
    {
        if (input is WasperMaterial material) return material;
        if (input is WasperMaterialGoo materialGoo) return materialGoo.Value;
        if (input is GH_ObjectWrapper wrapper) return ExtractMaterial(wrapper.Value);
        if (input is IGH_Goo goo)
        {
            object value = goo.ScriptVariable();
            if (value != null && !ReferenceEquals(value, input)) return ExtractMaterial(value);
        }
        return null;
    }
}
