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
    /// <summary>Reusable intrinsic water-vapour transport properties for a WASPer material.</summary>
    public sealed class WasperMoistureTransportProperties
    {
        private readonly ReadOnlyDictionary<string, string> _properties;

        [JsonConstructor]
        public WasperMoistureTransportProperties(IDictionary<string, string> properties)
        {
            _properties = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(properties ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase));
        }

        public IReadOnlyDictionary<string, string> Properties => _properties;

        public bool TryGet(string key, out string value) => _properties.TryGetValue(key, out value);

        public bool TryGetDouble(string key, out double value)
        {
            value = 0.0;
            return _properties.TryGetValue(key, out string text) &&
                   double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public override string ToString() => $"WASPer moisture transport properties: {_properties.Count} values";
    }

    /// <summary>Grasshopper wrapper used to wire and persist moisture transport properties.</summary>
    public sealed class WasperMoistureTransportPropertiesGoo : GH_Goo<WasperMoistureTransportProperties>
    {
        public WasperMoistureTransportPropertiesGoo() : base((WasperMoistureTransportProperties)null) { }
        public WasperMoistureTransportPropertiesGoo(WasperMoistureTransportProperties value) : base(value) { }

        public override bool IsValid => Value != null;
        public override string TypeName => "WASPer Moisture Transport Properties";
        public override string TypeDescription => "Intrinsic water-vapour transport properties for a WASPer material.";
        public override IGH_Goo Duplicate() => Value == null
            ? new WasperMoistureTransportPropertiesGoo()
            : new WasperMoistureTransportPropertiesGoo(new WasperMoistureTransportProperties(
                Value.Properties.ToDictionary(kv => kv.Key, kv => kv.Value)));
        public override string ToString() => Value?.ToString() ?? "Null WASPer moisture transport properties";

        public override bool Write(GH_IWriter writer)
        {
            if (Value != null) writer.SetString("moisture_transport_json", JsonConvert.SerializeObject(Value));
            return true;
        }

        public override bool Read(GH_IReader reader)
        {
            if (!reader.ItemExists("moisture_transport_json")) return true;
            Value = JsonConvert.DeserializeObject<WasperMoistureTransportProperties>(reader.GetString("moisture_transport_json"));
            return true;
        }

        public override bool CastTo<T>(ref T target)
        {
            if (typeof(T) == typeof(WasperMoistureTransportProperties) && Value != null)
            {
                target = (T)(object)Value;
                return true;
            }
            return base.CastTo(ref target);
        }

        public override bool CastFrom(object source)
        {
            if (source is WasperMoistureTransportProperties value) { Value = value; return true; }
            if (source is WasperMoistureTransportPropertiesGoo goo) { Value = goo.Value; return true; }
            return false;
        }
    }
}
