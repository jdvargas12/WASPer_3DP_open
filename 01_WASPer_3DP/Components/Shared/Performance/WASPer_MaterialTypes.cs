using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using GH_IO.Serialization;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json;

namespace WASPer_3DP
{
    /// <summary>A portable material record shared by WASPer components.</summary>
    public sealed class WasperMaterial
    {
        private readonly ReadOnlyDictionary<string, string> _properties;

        [JsonConstructor]
        public WasperMaterial(string name, string phase, IDictionary<string, string> properties, string source = null)
        {
            Name = string.IsNullOrWhiteSpace(name) ? "Unnamed" : name;
            Phase = string.IsNullOrWhiteSpace(phase) ? "Unknown" : phase;
            Source = source ?? string.Empty;
            _properties = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(properties ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase));
        }

        public string Name { get; }
        public string Phase { get; }
        public string Source { get; }
        public IReadOnlyDictionary<string, string> Properties => _properties;

        public bool TryGet(string key, out string value) => _properties.TryGetValue(key, out value);

        public bool TryGetDouble(string key, out double value)
        {
            value = 0.0;
            return _properties.TryGetValue(key, out string text) &&
                   double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public override string ToString() => $"WASPer Material: {Name} ({Phase}, {_properties.Count} properties)";
    }

    /// <summary>Grasshopper wrapper used to wire complete material records between components.</summary>
    public sealed class WasperMaterialGoo : GH_Goo<WasperMaterial>
    {
        public WasperMaterialGoo() : base((WasperMaterial)null) { }
        public WasperMaterialGoo(WasperMaterial material) : base(material) { }

        public override bool IsValid => Value != null && !string.IsNullOrWhiteSpace(Value.Name);
        public override string TypeName => "WASPer Material";
        public override string TypeDescription => "A complete WASPer material record with metadata and properties.";
        public override IGH_Goo Duplicate() => Value == null
            ? new WasperMaterialGoo()
            : new WasperMaterialGoo(new WasperMaterial(Value.Name, Value.Phase,
                Value.Properties.ToDictionary(kv => kv.Key, kv => kv.Value), Value.Source));
        public override string ToString() => Value?.ToString() ?? "Null WASPer Material";

        public override bool Write(GH_IWriter writer)
        {
            if (Value != null) writer.SetString("material_json", JsonConvert.SerializeObject(Value));
            return true;
        }

        public override bool Read(GH_IReader reader)
        {
            if (!reader.ItemExists("material_json")) return true;
            Value = JsonConvert.DeserializeObject<WasperMaterial>(reader.GetString("material_json"));
            return true;
        }

        public override bool CastTo<T>(ref T target)
        {
            if (typeof(T) == typeof(WasperMaterial) && Value != null)
            {
                target = (T)(object)Value;
                return true;
            }
            return base.CastTo(ref target);
        }

        public override bool CastFrom(object source)
        {
            if (source is WasperMaterial material) { Value = material; return true; }
            if (source is WasperMaterialGoo goo) { Value = goo.Value; return true; }
            return false;
        }
    }
}
