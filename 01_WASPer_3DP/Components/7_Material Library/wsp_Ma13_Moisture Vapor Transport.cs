using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Grasshopper.Kernel;
using WASPer_3DP;

public sealed class wsp_Ma13_MoistureVaporTransport : GH_Component
{
    private const string VaporPermeabilityKey = "Moisture.Vapor_Permeability (kg/(m·s·Pa))";
    private const string MuKey = "Moisture.Mu (-)";
    private const string DiffusivityKey = "Moisture.D_vapor (m²/s)";
    private const string ReferenceTemperatureKey = "Moisture.T_ref (°C)";
    private const string RhRangeKey = "Moisture.RH_range (%)";

    public wsp_Ma13_MoistureVaporTransport()
        : base("wsp_Ma13_Moisture Vapor Transport", "Moisture Transport",
            "Creates reusable intrinsic water-vapour transport properties for a WASPer material. " +
            "Connect the moisture_props output to Ma03_Create WASPer Material to embed these values in wasper_mat. " +
            "The properties are material data; layer thickness, porosity, and boundary resistances remain outside this component.",
            global::WASPer_3DP.WASPerPalette.Performance, "7_Material Library")
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        Message = v == null ? "v1.0.x" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    public override Guid ComponentGuid => new("7D9A0F6E-2B2A-4F6E-9D50-6D99B8C9A32F");
    public override GH_Exposure Exposure => GH_Exposure.secondary;

    protected override System.Drawing.Bitmap Icon
    {
        get
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                    "WASPer_3DP.Resources.Icons.wsp_Ma13_Moisture Vapor Transport.png");
                return stream == null ? null : new System.Drawing.Bitmap(stream);
            }
            catch { return null; }
        }
    }

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddNumberParameter("vapor_permeability", "δv",
            "Water-vapour permeability in kg/(m·s·Pa). This is the primary transport property; enter a measured value for the material and test condition.",
            GH_ParamAccess.item);
        p.AddNumberParameter("mu", "μ",
            "Water-vapour resistance factor, dimensionless. Optional measured or derived value; it is not a layer sd-value and does not include thickness.",
            GH_ParamAccess.item);
        p.AddNumberParameter("D_vapor", "D_vapor",
            "Water-vapour diffusivity in m²/s. Optional measured or derived value; keep its concentration/temperature basis in the notes of the source data.",
            GH_ParamAccess.item);
        p.AddNumberParameter("T_ref", "T_ref",
            "Reference temperature in °C for the reported transport properties. Default = 23 °C.",
            GH_ParamAccess.item, 23.0);
        p.AddNumberParameter("RH_range", "RH_range",
            "Relative-humidity range in % over which the transport values were measured or calibrated. Provide two values, [RH_min, RH_max].",
            GH_ParamAccess.list);

        p[0].Optional = true;
        p[1].Optional = true;
        p[2].Optional = true;
        p[4].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddGenericParameter("moisture_props", "moisture_props",
            "Typed reusable moisture transport property object. Connect to Ma03 to merge the values into a complete wasper_mat.", GH_ParamAccess.item);
        p.AddTextParameter("summary", "summary",
            "Readable one-property-per-line summary containing supplied moisture transport values and units. The complete material record can be inspected with Ma05 after Ma03 merges these properties.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        double value = 0.0;
        if (da.GetData(0, ref value))
        {
            if (IsPositiveFinite(value)) properties[VaporPermeabilityKey] = Format(value);
            else warnings.Add("vapor_permeability must be positive and finite.");
        }
        if (da.GetData(1, ref value))
        {
            if (IsPositiveFinite(value)) properties[MuKey] = Format(value);
            else warnings.Add("mu must be positive and finite.");
        }
        if (da.GetData(2, ref value))
        {
            if (IsPositiveFinite(value)) properties[DiffusivityKey] = Format(value);
            else warnings.Add("D_vapor must be positive and finite.");
        }
        if (da.GetData(3, ref value))
        {
            if (double.IsFinite(value)) properties[ReferenceTemperatureKey] = Format(value);
            else warnings.Add("T_ref must be finite.");
        }

        var rh = new List<double>();
        da.GetDataList(4, rh);
        if (rh.Count > 0)
        {
            if (rh.Count != 2)
                warnings.Add("RH_range must contain exactly two values: [RH_min, RH_max].");
            else if (!rh.All(double.IsFinite) || rh[0] < 0 || rh[1] > 100 || rh[0] >= rh[1])
                warnings.Add("RH_range must satisfy 0 ≤ RH_min < RH_max ≤ 100 (%).");
            else
                properties[RhRangeKey] = $"[{Format(rh[0])}, {Format(rh[1])}]";
        }

        if (!properties.Keys.Any(k => k == VaporPermeabilityKey || k == MuKey || k == DiffusivityKey))
            warnings.Add("Supply at least one transport value: vapor_permeability, mu, or D_vapor.");

        var typed = new WasperMoistureTransportProperties(properties);
        da.SetData(0, new WasperMoistureTransportPropertiesGoo(typed));
        da.SetData(1, BuildReport(properties));

        Message = properties.Count == 0 ? "No values" : $"{properties.Count} values";
        if (warnings.Count > 0)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, string.Join(" ", warnings));
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0.0;

    private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string BuildReport(IReadOnlyDictionary<string, string> properties)
    {
        if (properties.Count == 0) return "Moisture vapor transport properties: no values supplied.";
        return "Moisture vapor transport properties\n" +
               string.Join("\n", properties.Select(kv => $"{kv.Key}: {kv.Value}"));
    }
}
