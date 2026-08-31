using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
using WASPer_3DP;

public sealed class wsp_Ma07_CreateWasperLayer : GH_Component
{
    private const double SuspiciousThickness_m = 1.0;

    public wsp_Ma07_CreateWasperLayer()
        : base("wsp_Ma07_Create WASPer Layer", "Create Layer",
            "Creates WASPer layer objects by combining WASPer materials with thickness and optional effective thermal overrides. " +
            "Intended to package layer properties for downstream ISO 6946 steady multilayer and ISO 13786 dynamic transfer-matrix " +
            "workflows. This component does not calculate R-values, U-values, admittances, decrement factors, or transfer matrices; " +
            "it only prepares clean, ordered layer data for those future solvers. Materials are supplied in exterior → interior order. " +
            "Single thickness / λ_eff / porosity values are broadcast to every layer; list inputs must match the material count. The " +
            "source WasperMaterial is never modified: λ_eff and porosity-based overrides are stored on the layer itself, so the same " +
            "material can be reused across layers with different thickness, λ_eff, or porosity.", global::WASPer_3DP.WASPerPalette.Performance, "7_Material Library")
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        Message = v == null ? "v1.0.x" : $"v{v.Major}.{v.Minor}.{v.Build}";
    }

    public override Guid ComponentGuid => new("8F3B51AB-7C25-4C9F-A582-6A2F9E7F3D41");

    public override GH_Exposure Exposure => GH_Exposure.primary;

    protected override System.Drawing.Bitmap Icon
    {
        get
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ma07_Create WASPer Layer.png");
                return stream == null ? null : new System.Drawing.Bitmap(stream);
            }
            catch { return null; }
        }
    }

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddGenericParameter("wasper_mat", "wasper_mat",
            "Single WASPer material or list of WASPer materials, in exterior → interior order. Accepts outputs from Ma01, Ma02, Ma03, Ma04, or Ma06.",
            GH_ParamAccess.list);

        p.AddNumberParameter("thickness", "thickness",
            "Layer thickness in metres. Provide one value to apply the same thickness to every material, or one value per material. Values greater than 1 m trigger a warning because construction layers are normally below 1 m and a larger value may indicate that millimetres were entered without dividing by 1000.",
            GH_ParamAccess.list);

        p.AddNumberParameter("lambda_eff", "λ_eff",
            "Optional total effective conductivity in W/(m·K) for each layer. When supplied, this overrides the base material's " +
            "thermal conductivity for that layer only; the base material itself is unchanged. Provide one value to apply to every layer, " +
            "or one value per layer.",
            GH_ParamAccess.list);
        p[2].Optional = true;

        p.AddNumberParameter("porosity", "φ",
            "Optional void fraction in [0, 1] for each layer. When supplied, the layer computes equivalent ρ_eff, ρc_eff, and cp_eff " +
            "using the base material as the solid phase and air (ρ=1.204 kg/m³, cp=1005 J/(kg·K)) as the void phase. When φ is " +
            "supplied, air is always assumed as the void phase; a future void_mat input may allow other fill materials (aerogel, " +
            "foam, water, custom infill). Provide one value to apply to every layer, or one value per layer. Unwired values default " +
            "to φ = 0 (no void).",
            GH_ParamAccess.list);
        p[3].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddGenericParameter("wasper_layer", "wasper_layer",
            "Generated WASPer Layer objects, ordered exterior → interior. Each layer stores its source material plus optional " +
            "λ_eff and porosity-based effective storage overrides. Connect to Ma08 (Inspect WASPer Layer) to review a layer's " +
            "resolved values and underlying material, or to downstream multilayer solvers.",
            GH_ParamAccess.list);

        p.AddTextParameter("report", "report",
            "Human-readable layer-stack report. For each layer: material, thickness, λ with its source (λ_eff override or material), " +
            "porosity, and the resulting density/specific heat/volumetric heat capacity with their source (porosity-weighted mixture " +
            "or material).",
            GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess DA)
    {
        var rawMaterials = new List<object>();
        var thicknessRaw = new List<double>();
        var lambdaEffRaw = new List<double>();
        var porosityRaw = new List<double>();

        if (!DA.GetDataList(0, rawMaterials) || rawMaterials.Count == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Provide at least one WASPer material.");
            DA.SetData(1, "Idle: missing wasper_mat.");
            return;
        }

        if (!DA.GetDataList(1, thicknessRaw) || thicknessRaw.Count == 0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Provide thickness [m]. Use one value or one value per material.");
            DA.SetData(1, "Idle: missing thickness.");
            return;
        }

        DA.GetDataList(2, lambdaEffRaw);
        DA.GetDataList(3, porosityRaw);

        var materials = new List<WasperMaterial>();
        for (int i = 0; i < rawMaterials.Count; i++)
        {
            WasperMaterial mat = ExtractMaterial(rawMaterials[i]);
            if (mat == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Input wasper_mat[{i}] is not a valid WASPer material.");
                DA.SetData(1, "Idle: invalid material input.");
                return;
            }
            materials.Add(mat);
        }

        int n = materials.Count;

        if (!BroadcastList(thicknessRaw, n, out List<double> thicknesses))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "thickness must be either one value or one value per material.");
            DA.SetData(1, "Idle: thickness list length mismatch.");
            return;
        }

        var suspiciousThicknesses = thicknesses
            .Select((value, index) => new { value, index })
            .Where(item => IsFinite(item.value) && item.value > SuspiciousThickness_m)
            .ToList();
        if (suspiciousThicknesses.Count > 0)
        {
            string indices = string.Join(", ", suspiciousThicknesses.Select(item => item.index));
            double maximum = suspiciousThicknesses.Max(item => item.value);
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Warning,
                $"Very large thickness interpreted in metres at layer index/indices {indices} (maximum {maximum:0.###} m). " +
                "If these values are millimetres, divide them by 1000 before connecting them to thickness.");
        }

        List<double?> lambdaEffs;
        if (lambdaEffRaw.Count == 0)
        {
            lambdaEffs = Enumerable.Repeat<double?>(null, n).ToList();
        }
        else if (!BroadcastNullableList(lambdaEffRaw, n, out lambdaEffs))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "λ_eff must be either one value or one value per material.");
            DA.SetData(1, "Idle: λ_eff list length mismatch.");
            return;
        }

        List<double> porosities;
        if (porosityRaw.Count == 0)
        {
            porosities = Enumerable.Repeat(0.0, n).ToList();
        }
        else if (!BroadcastList(porosityRaw, n, out porosities))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "porosity must be either one value or one value per material.");
            DA.SetData(1, "Idle: porosity list length mismatch.");
            return;
        }

        var layers = new List<WasperLayer>();
        for (int i = 0; i < n; i++)
        {
            double d = thicknesses[i];
            double phi = Clamp(porosities[i], 0.0, 1.0);
            double? lambda = lambdaEffs[i];

            if (!IsFinite(d) || d <= 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"thickness at layer {i} must be > 0 m.");
                DA.SetData(1, "Idle: invalid thickness.");
                return;
            }

            if (lambda.HasValue && (!IsFinite(lambda.Value) || lambda.Value <= 0.0))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Invalid λ_eff for layer {i}. Material λ used instead.");
                lambda = null;
            }

            if (Math.Abs(phi - porosities[i]) > 1e-12)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"φ at layer {i} was clamped to {phi:0.###} (valid range 0-1).");

            var layer = new WasperLayer(materials[i], d, lambda, phi, i, "exterior_to_interior");
            if (phi > 0.0 && !layer.HasStorageOverride)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Layer {i} has φ > 0, but density and/or specific heat were not found in the material. Storage values fall back to material properties.");
            }
            layers.Add(layer);
        }

        DA.SetDataList(0, layers.Select(l => new WasperLayerGoo(l)));
        DA.SetData(1, BuildReport(layers));
    }

    private static string BuildReport(List<WasperLayer> layers)
    {
        var sb = new StringBuilder();

        sb.AppendLine("WASPer Layer Stack");
        sb.AppendLine("Order: exterior → interior");
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Number of layers: {0}", layers.Count));

        for (int i = 0; i < layers.Count; i++)
        {
            WasperLayer l = layers[i];
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "[{0}] {1}", i, l.MaterialName));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "\tThickness: {0:0.####} m", l.Thickness_m));

            bool hasLambda = l.TryGetDouble("lambda", out double lambda);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "\tλ: {0}", FormatOrNA(hasLambda, lambda, "W/(m·K)")));
            sb.AppendLine(l.HasLambdaEffOverride ? "\tλ source: λ_eff override" : "\tλ source: material");

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "\tφ: {0:0.###}", l.Porosity));

            if (l.HasStorageOverride)
            {
                sb.AppendLine("\tStorage source: porosity-weighted solid/air mixture");
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "\tρ_eff: {0:0.#} kg/m³", l.RhoEff_kg_m3));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "\tcp_eff: {0:0.#} J/(kg·K)", l.CpEff_J_kgK));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "\tρc_eff: {0:0.#} J/(m³·K)", l.RhoCEff_J_m3K));
            }
            else
            {
                bool hasRho = l.TryGetDouble("density", out double rho);
                bool hasCp = l.TryGetDouble("specific_heat", out double cp);
                bool hasRhoC = l.TryGetDouble("rho_c", out double rhoC);
                sb.AppendLine("\tStorage source: material");
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "\tρ: {0}", FormatOrNA(hasRho, rho, "kg/m³")));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "\tcp: {0}", FormatOrNA(hasCp, cp, "J/(kg·K)")));
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "\tρc: {0}", FormatOrNA(hasRhoC, rhoC, "J/(m³·K)")));
            }
        }

        return sb.ToString();
    }

    private static string FormatOrNA(bool hasValue, double value, string unit) =>
        hasValue ? string.Format(CultureInfo.InvariantCulture, "{0:0.####} {1}", value, unit) : "n/a";

    private static bool BroadcastList(List<double> source, int targetCount, out List<double> result)
    {
        result = new List<double>();
        if (source == null || source.Count == 0) return false;
        if (source.Count == 1) { for (int i = 0; i < targetCount; i++) result.Add(source[0]); return true; }
        if (source.Count == targetCount) { result.AddRange(source); return true; }
        return false;
    }

    private static bool BroadcastNullableList(List<double> source, int targetCount, out List<double?> result)
    {
        result = new List<double?>();
        if (source == null || source.Count == 0) return false;
        if (source.Count == 1) { for (int i = 0; i < targetCount; i++) result.Add(source[0]); return true; }
        if (source.Count == targetCount) { foreach (double value in source) result.Add(value); return true; }
        return false;
    }

    private static WasperMaterial ExtractMaterial(object input)
    {
        if (input == null) return null;
        if (input is WasperMaterial material) return material;
        if (input is WasperMaterialGoo goo) return goo.Value;
        if (input is GH_ObjectWrapper wrapper) return ExtractMaterial(wrapper.Value);
        if (input is IGH_Goo g)
        {
            object value = g.ScriptVariable();
            if (value != null && !ReferenceEquals(value, input)) return ExtractMaterial(value);
        }
        return null;
    }

    private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

    private static double Clamp(double x, double lo, double hi) => double.IsNaN(x) || double.IsInfinity(x) ? lo : x < lo ? lo : x > hi ? hi : x;
}
