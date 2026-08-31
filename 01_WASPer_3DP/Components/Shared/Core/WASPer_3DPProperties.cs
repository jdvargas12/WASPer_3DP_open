using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

namespace WASPer_3DP
{
    /// <summary>Optional fresh-state and fabrication properties for 3D printing.</summary>
    public sealed class Wasper3dpProperties
    {
        public Wasper3dpProperties(IDictionary<string, string> properties)
        {
            Properties = new Dictionary<string, string>(
                properties ?? new Dictionary<string, string>(),
                StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyDictionary<string, string> Properties { get; }

        public bool TryGetDouble(string key, out double value)
        {
            value = 0.0;
            return Properties.TryGetValue(key, out var text) &&
                double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        public override string ToString() => $"WASPer 3DP properties ({Properties.Count} values)";
    }

    public sealed class Wasper3dpPropertiesGoo : GH_Goo<Wasper3dpProperties>
    {
        public Wasper3dpPropertiesGoo() : base((Wasper3dpProperties)null) { }
        public Wasper3dpPropertiesGoo(Wasper3dpProperties value) : base(value) { }

        public override bool IsValid => Value != null;
        public override string TypeName => "WASPer 3DP Properties";
        public override string TypeDescription => "Optional fresh-state and fabrication properties.";
        public override IGH_Goo Duplicate() => Value == null
            ? new Wasper3dpPropertiesGoo()
            : new Wasper3dpPropertiesGoo(new Wasper3dpProperties(Value.Properties.ToDictionary(kv => kv.Key, kv => kv.Value)));
        public override string ToString() => Value?.ToString() ?? "Null WASPer 3DP Properties";

        public override bool CastTo<T>(ref T target)
        {
            if (typeof(T) == typeof(Wasper3dpProperties) && Value != null)
            {
                target = (T)(object)Value;
                return true;
            }
            return base.CastTo(ref target);
        }

        public override bool CastFrom(object source)
        {
            if (source is Wasper3dpProperties value) { Value = value; return true; }
            if (source is Wasper3dpPropertiesGoo goo) { Value = goo.Value; return true; }
            return base.CastFrom(source);
        }
    }
}
