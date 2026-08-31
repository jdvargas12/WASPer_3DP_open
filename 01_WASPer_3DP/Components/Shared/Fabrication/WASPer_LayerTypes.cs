using System;
using System.Collections.Generic;
using System.Globalization;
using GH_IO.Serialization;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json;

namespace WASPer_3DP
{
    /// <summary>
    /// A single material layer used to package clean, ordered layer data for future multilayer
    /// assemblies (ISO 6946 steady-state resistance/U-value, ISO 13786 dynamic transfer-matrix,
    /// and equivalent cellular storage workflows). Pairs a <see cref="WasperMaterial"/> with a
    /// thickness and optional effective-conductivity and porosity overrides. Mirrors the shared
    /// <see cref="WasperMaterial"/> record: a plain, serializable data type with a matching
    /// <see cref="WasperLayerGoo"/> wrapper, living in the same shared <c>WASPer_3DP</c> namespace
    /// so both material-library and future Ht/visualization components can consume it.
    /// Thickness is stored in metres; porosity is a void volume fraction in [0, 1].
    /// <para>
    /// The source <see cref="WasperMaterial"/> is never modified: overrides (λ_eff, porosity-based
    /// storage) are computed and stored on the layer itself, so the same material can be reused
    /// across multiple layers with different thickness, λ_eff, or porosity.
    /// </para>
    /// <para>
    /// λ_eff is treated as an externally calculated, calibrated, or measured effective conductivity
    /// and is never derived from porosity alone (real conductivity depends on pore topology, shape,
    /// orientation, Fp, radiation, convection, tortuosity, and continuity). When φ is supplied, air
    /// is assumed as the void phase (ρ ≈ 1.204 kg/m³, cp ≈ 1005 J/(kg·K)); a future
    /// <c>void_mat</c>/<c>fill_mat</c> input could let other fill materials (aerogel, foam, water,
    /// custom infill) replace this default.
    /// </para>
    /// </summary>
    public sealed class WasperLayer
    {
        private const double DefaultAirDensity_kg_m3 = 1.204;
        private const double DefaultAirCp_J_kgK = 1005.0;

        public WasperMaterial Material { get; }
        public double Thickness_m { get; }
        public double Porosity { get; }
        public int Index { get; }
        public string Order { get; }

        public bool HasLambdaEffOverride { get; }
        public double LambdaEff_W_mK { get; }

        /// <summary>True when Porosity &gt; 0 and the base material has enough data (density and
        /// specific heat) to compute equivalent solid+void storage properties.</summary>
        public bool HasStorageOverride { get; private set; }
        public double RhoEff_kg_m3 { get; private set; }
        public double RhoCEff_J_m3K { get; private set; }
        public double CpEff_J_kgK { get; private set; }

        public WasperLayer(WasperMaterial material, double thickness_m, double? lambdaEff_W_mK, double porosity, int index, string order)
        {
            Material = material;
            Thickness_m = thickness_m;
            Porosity = Clamp(porosity, 0.0, 1.0);
            Index = index;
            Order = string.IsNullOrWhiteSpace(order) ? "exterior_to_interior" : order;

            HasLambdaEffOverride = lambdaEff_W_mK.HasValue && IsFinite(lambdaEff_W_mK.Value) && lambdaEff_W_mK.Value > 0.0;
            LambdaEff_W_mK = HasLambdaEffOverride ? lambdaEff_W_mK.Value : double.NaN;

            ComputeStorageOverride();
        }

        public double SolidFraction => 1.0 - Porosity;

        public string MaterialName => Material?.Name ?? "<null material>";

        /// <summary>
        /// Resolves a property by role (conductivity, density, specific heat, or volumetric heat
        /// capacity) so downstream components never need to know whether a value comes from an
        /// override or from the base material:
        /// <list type="bullet">
        /// <item><description>"lambda"/conductivity → λ_eff if supplied, else material λ.</description></item>
        /// <item><description>"density" → ρ_eff if a porosity override exists, else material ρ.</description></item>
        /// <item><description>"specific_heat"/"cp" → cp_eff if a porosity override exists, else material cp.</description></item>
        /// <item><description>"rho_c" → ρc_eff if a porosity override exists, else material ρ × cp
        /// computed on the fly (materials do not normally store ρc as its own property).</description></item>
        /// </list>
        /// Any other key falls back to a direct lookup on the base material.
        /// </summary>
        public bool TryGetDouble(string key, out double value)
        {
            value = 0.0;
            string k = NormalizeKey(key);

            if (IsThermalConductivityKey(k))
            {
                if (HasLambdaEffOverride) { value = LambdaEff_W_mK; return true; }
                return Material != null && Material.TryGetDouble(key, out value) && IsFinite(value);
            }

            if (IsDensityKey(k))
            {
                if (HasStorageOverride) { value = RhoEff_kg_m3; return true; }
                return Material != null && Material.TryGetDouble(key, out value) && IsFinite(value);
            }

            if (IsSpecificHeatKey(k))
            {
                if (HasStorageOverride) { value = CpEff_J_kgK; return true; }
                return Material != null && Material.TryGetDouble(key, out value) && IsFinite(value);
            }

            if (IsVolumetricHeatCapacityKey(k))
            {
                if (HasStorageOverride) { value = RhoCEff_J_m3K; return true; }
                if (TryGetMaterialAnyDouble(Material, DensityKeys, out double rho) &&
                    TryGetMaterialAnyDouble(Material, SpecificHeatKeys, out double cp))
                {
                    value = rho * cp;
                    return true;
                }
                return false;
            }

            return Material != null && Material.TryGetDouble(key, out value) && IsFinite(value);
        }

        public override string ToString()
        {
            string lambdaText = HasLambdaEffOverride
                ? string.Format(CultureInfo.InvariantCulture, "λ_eff={0:0.####} W/mK", LambdaEff_W_mK)
                : "λ=material";
            string storageText = HasStorageOverride
                ? string.Format(CultureInfo.InvariantCulture, "ρ_eff={0:0.#} kg/m³ | ρc_eff={1:0.#} J/m³K", RhoEff_kg_m3, RhoCEff_J_m3K)
                : "ρ,cp=material";

            return string.Format(CultureInfo.InvariantCulture, "Layer {0}: {1} | d={2:0.####} m | φ={3:0.###} | {4} | {5} | order={6}",
                Index, MaterialName, Thickness_m, Porosity, lambdaText, storageText, Order);
        }

        private void ComputeStorageOverride()
        {
            HasStorageOverride = false;
            RhoEff_kg_m3 = double.NaN;
            RhoCEff_J_m3K = double.NaN;
            CpEff_J_kgK = double.NaN;

            if (Porosity <= 0.0) return;
            if (!TryGetMaterialAnyDouble(Material, DensityKeys, out double rhoSolid)) return;
            if (!TryGetMaterialAnyDouble(Material, SpecificHeatKeys, out double cpSolid)) return;

            RhoEff_kg_m3 = SolidFraction * rhoSolid + Porosity * DefaultAirDensity_kg_m3;
            RhoCEff_J_m3K = SolidFraction * rhoSolid * cpSolid + Porosity * DefaultAirDensity_kg_m3 * DefaultAirCp_J_kgK;
            CpEff_J_kgK = RhoCEff_J_m3K / Math.Max(RhoEff_kg_m3, 1e-12);
            HasStorageOverride = IsFinite(RhoEff_kg_m3) && IsFinite(RhoCEff_J_m3K) && IsFinite(CpEff_J_kgK);
        }

        private static bool TryGetMaterialAnyDouble(WasperMaterial mat, IEnumerable<string> keys, out double value)
        {
            value = 0.0;
            if (mat == null) return false;
            foreach (string key in keys)
                if (mat.TryGetDouble(key, out value) && IsFinite(value)) return true;
            return false;
        }

        private static readonly string[] DensityKeys = { "Density (kg/m³)", "Density (kg/m3)", "density", "rho", "ρ" };
        private static readonly string[] SpecificHeatKeys = { "Specific_Heat (J/kg·K)", "Specific_Heat (J/kg*K)", "Specific Heat (J/kg·K)", "specific_heat", "spec_heat", "cp" };

        private static string NormalizeKey(string key) =>
            string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim().ToLowerInvariant().Replace(" ", "_");

        private static bool IsThermalConductivityKey(string k) =>
            k.Contains("thermal_conductivity") || k.Contains("conductivity") || k == "lambda" || k == "λ" || k.Contains("w/m");

        private static bool IsDensityKey(string k) => k.Contains("density") || k == "rho" || k == "ρ";

        private static bool IsSpecificHeatKey(string k) => k.Contains("specific_heat") || k.Contains("spec_heat") || k == "cp";

        private static bool IsVolumetricHeatCapacityKey(string k) => k.Contains("volumetric_heat") || k.Contains("rho_c") || k.Contains("rhoc") || k.Contains("rho*c") || k.Contains("ρc");

        private static bool IsFinite(double x) => !double.IsNaN(x) && !double.IsInfinity(x);

        private static double Clamp(double x, double lo, double hi) => double.IsNaN(x) || double.IsInfinity(x) ? lo : x < lo ? lo : x > hi ? hi : x;
    }

    /// <summary>Grasshopper wrapper used to wire and persist complete <see cref="WasperLayer"/> objects
    /// between components, mirroring <see cref="WasperMaterialGoo"/>.</summary>
    public sealed class WasperLayerGoo : GH_Goo<WasperLayer>
    {
        public WasperLayerGoo() : base((WasperLayer)null) { }
        public WasperLayerGoo(WasperLayer layer) : base(layer) { }

        public override bool IsValid => Value != null && Value.Material != null;
        public override string TypeName => "WASPer Layer";
        public override string TypeDescription => "A WASPer material layer with thickness and optional effective-property overrides.";
        public override string ToString() => Value?.ToString() ?? "Null WASPer Layer";

        public override IGH_Goo Duplicate() => Value == null
            ? new WasperLayerGoo()
            : new WasperLayerGoo(new WasperLayer(
                Value.Material, Value.Thickness_m,
                Value.HasLambdaEffOverride ? (double?)Value.LambdaEff_W_mK : null,
                Value.Porosity, Value.Index, Value.Order));

        public override bool Write(GH_IWriter writer)
        {
            if (Value == null) return true;
            writer.SetString("material_json", JsonConvert.SerializeObject(Value.Material));
            writer.SetDouble("thickness_m", Value.Thickness_m);
            writer.SetBoolean("has_lambda_eff", Value.HasLambdaEffOverride);
            writer.SetDouble("lambda_eff", Value.HasLambdaEffOverride ? Value.LambdaEff_W_mK : 0.0);
            writer.SetDouble("porosity", Value.Porosity);
            writer.SetInt32("index", Value.Index);
            writer.SetString("order", Value.Order ?? string.Empty);
            return true;
        }

        public override bool Read(GH_IReader reader)
        {
            if (!reader.ItemExists("material_json")) return true;

            WasperMaterial material = JsonConvert.DeserializeObject<WasperMaterial>(reader.GetString("material_json"));
            double thickness = reader.GetDouble("thickness_m");
            bool hasLambda = reader.GetBoolean("has_lambda_eff");
            double lambda = reader.GetDouble("lambda_eff");
            double porosity = reader.GetDouble("porosity");
            int index = reader.GetInt32("index");
            string order = reader.GetString("order");

            Value = new WasperLayer(material, thickness, hasLambda ? (double?)lambda : null, porosity, index, order);
            return true;
        }

        public override bool CastTo<T>(ref T target)
        {
            if (typeof(T) == typeof(WasperLayer) && Value != null)
            {
                target = (T)(object)Value;
                return true;
            }
            return base.CastTo(ref target);
        }

        public override bool CastFrom(object source)
        {
            if (source is WasperLayer layer) { Value = layer; return true; }
            if (source is WasperLayerGoo goo) { Value = goo.Value; return true; }
            return false;
        }
    }
}
