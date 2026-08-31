using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Newtonsoft.Json;

namespace WASPer_3DP
{
    /// <summary>
    /// One global, component-level KPI. Per-point and per-layer arrays remain in the
    /// existing typed Grasshopper tree outputs; this contract stores their global summaries.
    /// </summary>
    public sealed class WasperKpi
    {
        public string Key { get; set; } = string.Empty;
        public string SourceKey { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public string SubsetId { get; set; } = string.Empty;
        public Guid SourceInstanceId { get; set; } = Guid.Empty;
        public Guid SourceComponentId { get; set; } = Guid.Empty;
        public string SourceNickname { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string Scope { get; set; } = "Job";
        public double? Value { get; set; }
        public string TextValue { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;

        [JsonIgnore]
        public bool HasValue => Value.HasValue || !string.IsNullOrWhiteSpace(TextValue);

        [JsonIgnore]
        public string DisplayGroup
        {
            get
            {
                string group = string.IsNullOrWhiteSpace(Group) ? "Other" : Group.Trim();
                string type = string.IsNullOrWhiteSpace(Method)
                    ? group
                    : group + " - " + Method.Trim();
                return string.IsNullOrWhiteSpace(SubsetId)
                    ? type
                    : type + " (" + SubsetId.Trim() + ")";
            }
        }

        public override string ToString()
        {
            string value = Value.HasValue
                ? Value.Value.ToString("G", CultureInfo.InvariantCulture)
                : TextValue;
            return string.IsNullOrWhiteSpace(Unit) ? $"{Label}: {value}" : $"{Label}: {value} {Unit}";
        }

        public static WasperKpi Scalar(
            string key,
            string label,
            string group,
            string unit,
            double value,
            string description,
            string source,
            bool enabled = true)
        {
            return new WasperKpi
            {
                Key = key ?? string.Empty,
                Label = label ?? key ?? string.Empty,
                Group = group ?? string.Empty,
                Unit = unit ?? string.Empty,
                Value = value,
                Description = description ?? string.Empty,
                Source = source ?? string.Empty,
                Enabled = enabled
            };
        }

        public static WasperKpi Scalar(
            string key,
            string label,
            string group,
            double value,
            string unit,
            string description,
            string source,
            bool enabled = true)
        {
            return Scalar(key, label, group, unit, value, description, source, enabled);
        }

        public static WasperKpi Text(
            string key,
            string label,
            string group,
            string text,
            string description,
            string source,
            bool enabled = true)
        {
            return new WasperKpi
            {
                Key = key ?? string.Empty,
                Label = label ?? key ?? string.Empty,
                Group = group ?? string.Empty,
                TextValue = text ?? string.Empty,
                Description = description ?? string.Empty,
                Source = source ?? string.Empty,
                Enabled = enabled
            };
        }
    }

    /// <summary>
    /// Ordered collection of global KPI records emitted by one component or merged by Ut17.
    /// </summary>
    public sealed class WasperKpiSet
    {
        public int SchemaVersion { get; set; } = 2;
        public string SourceComponent { get; set; } = string.Empty;
        public string SourceVersion { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
        public string SubsetId { get; set; } = string.Empty;
        public Guid SourceInstanceId { get; set; } = Guid.Empty;
        public Guid SourceComponentId { get; set; } = Guid.Empty;
        public string SourceNickname { get; set; } = string.Empty;
        public string SampleId { get; set; } = string.Empty;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public List<WasperKpi> Items { get; set; } = new List<WasperKpi>();
        public List<string> Warnings { get; set; } = new List<string>();

        [JsonIgnore]
        public IEnumerable<WasperKpi> EnabledItems => Items?.Where(item => item != null && item.Enabled) ?? Enumerable.Empty<WasperKpi>();

        public void Add(WasperKpi kpi)
        {
            if (kpi == null || string.IsNullOrWhiteSpace(kpi.Key) || !kpi.HasValue)
                return;
            if (kpi.Value.HasValue &&
                (double.IsNaN(kpi.Value.Value) || double.IsInfinity(kpi.Value.Value)))
                return;
            if (string.IsNullOrWhiteSpace(kpi.Method))
                kpi.Method = Method ?? string.Empty;
            Items ??= new List<WasperKpi>();
            Items.Add(kpi);
        }

        public void AddWarning(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                Warnings ??= new List<string>();
                Warnings.Add(message);
            }
        }

        public WasperKpiSet BindSource(GH_Component source)
        {
            if (source == null)
                return this;
            SourceInstanceId = source.InstanceGuid;
            SourceComponentId = source.ComponentGuid;
            SourceNickname = string.IsNullOrWhiteSpace(source.NickName)
                ? source.Name ?? string.Empty
                : source.NickName.Trim();
            if (string.IsNullOrWhiteSpace(SourceComponent))
                SourceComponent = source.Name ?? string.Empty;
            foreach (WasperKpi item in Items ?? new List<WasperKpi>())
            {
                if (item == null)
                    continue;
                item.SourceInstanceId = SourceInstanceId;
                item.SourceComponentId = SourceComponentId;
                item.SourceNickname = SourceNickname;
                if (string.IsNullOrWhiteSpace(item.Source))
                    item.Source = SourceComponent;
            }
            return this;
        }

        public WasperKpiSet CreateSubset(string subsetId)
        {
            string identifier = string.IsNullOrWhiteSpace(subsetId) ? "Subset" : subsetId.Trim();
            string keyToken = Uri.EscapeDataString(identifier);
            var subset = new WasperKpiSet
            {
                SchemaVersion = Math.Max(2, SchemaVersion),
                SourceComponent = SourceComponent ?? string.Empty,
                SourceVersion = SourceVersion ?? string.Empty,
                Method = Method ?? string.Empty,
                SubsetId = identifier,
                SourceInstanceId = SourceInstanceId,
                SourceComponentId = SourceComponentId,
                SourceNickname = SourceNickname ?? string.Empty,
                SampleId = SampleId ?? string.Empty,
                CreatedUtc = CreatedUtc,
                Warnings = new List<string>(Warnings ?? new List<string>())
            };

            foreach (WasperKpi item in Items ?? new List<WasperKpi>())
            {
                if (item == null)
                    continue;
                string sourceKey = string.IsNullOrWhiteSpace(item.SourceKey)
                    ? item.Key ?? string.Empty
                    : item.SourceKey;
                subset.Items.Add(new WasperKpi
                {
                    Key = "subset:" + keyToken + ":" + sourceKey,
                    SourceKey = sourceKey,
                    Label = item.Label ?? string.Empty,
                    Group = item.Group ?? string.Empty,
                    Method = item.Method ?? string.Empty,
                    SubsetId = identifier,
                    SourceInstanceId = item.SourceInstanceId,
                    SourceComponentId = item.SourceComponentId,
                    SourceNickname = item.SourceNickname ?? string.Empty,
                    Unit = item.Unit ?? string.Empty,
                    Description = item.Description ?? string.Empty,
                    Source = item.Source ?? string.Empty,
                    Scope = item.Scope ?? "Job",
                    Value = item.Value,
                    TextValue = item.TextValue ?? string.Empty,
                    Enabled = item.Enabled
                });
            }
            return subset;
        }

        public static WasperKpiSet Merge(IEnumerable<WasperKpiSet> sets, string sourceComponent = "Ut17 KPI Manager")
        {
            var merged = new WasperKpiSet { SourceComponent = sourceComponent ?? string.Empty };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sets == null)
                return merged;

            foreach (WasperKpiSet set in sets)
            {
                if (set == null)
                    continue;
                foreach (WasperKpi item in set.Items ?? new List<WasperKpi>())
                {
                    if (item != null && seen.Add(item.Key))
                        merged.Add(item);
                    else if (item != null)
                        merged.AddWarning($"Duplicate KPI key ignored: {item.Key}.");
                }
                foreach (string warning in set.Warnings ?? new List<string>())
                    merged.AddWarning(warning);
            }
            return merged;
        }

        public override string ToString()
        {
            string count = $"{Items?.Count ?? 0} global metrics";
            return string.IsNullOrWhiteSpace(SubsetId)
                ? $"WASPer KPI Set ({count})"
                : $"WASPer KPI Subset ({SubsetId}, {count})";
        }
    }

    public sealed class WasperKpiSetGoo : WasperJsonGoo<WasperKpiSet>
    {
        public WasperKpiSetGoo() { }
        public WasperKpiSetGoo(WasperKpiSet value) : base(value) { }
        public WasperKpiSetGoo(WasperKpiSet value, GH_Component source)
            : base(value?.BindSource(source)) { }
        protected override string StorageKey => "wasper_kpi_set_json";
        protected override WasperJsonGoo<WasperKpiSet> Create(WasperKpiSet value) => new WasperKpiSetGoo(value);
        public override string TypeName => "WASPer KPI Set";
        public override string TypeDescription => "Global scalar KPIs emitted by WASPer performance components.";
    }

    /// <summary>
    /// One infill definition and the spatial domain dimensions used to evaluate it.
    /// Multiple entries are serialized into aligned comma-separated KPI values.
    /// </summary>
    public sealed class WasperInfillKpiEntry
    {
        public string CellName { get; set; } = string.Empty;
        public string CellNameShort { get; set; } = string.Empty;
        public double CountX { get; set; }
        public double CountY { get; set; }
        public double CountZ { get; set; }
        public double DimensionX { get; set; }
        public double DimensionY { get; set; }
        public double DimensionZ { get; set; }
    }

    public static class WasperInfillKpiFactory
    {
        public static WasperKpiSet Create(
            string sourceComponent,
            string sourceVersion,
            IEnumerable<WasperInfillKpiEntry> entries,
            int domainCount)
        {
            List<WasperInfillKpiEntry> values = (entries ?? Enumerable.Empty<WasperInfillKpiEntry>())
                .Where(entry => entry != null)
                .ToList();
            var set = new WasperKpiSet
            {
                SourceComponent = sourceComponent ?? string.Empty,
                SourceVersion = sourceVersion ?? string.Empty
            };
            if (values.Count == 0)
                return set;

            set.Add(WasperKpi.Text(
                "infill.cell_name",
                "Cell name",
                "Infill",
                JoinText(values.Select(entry => entry.CellName)),
                "Full infill-cell name. Multiple evaluated definitions are comma-separated in assignment order.",
                sourceComponent));
            set.Add(WasperKpi.Text(
                "infill.cell_name_short",
                "Cell name (short)",
                "Infill",
                JoinText(values.Select(entry => entry.CellNameShort)),
                "Compact infill-cell identifier. Example: Gyr for Gyroid or Di for Diamond.",
                sourceComponent));
            AddNumberSeries(set, "infill.cell_count_x", "Cell count X", "-", values.Select(entry => entry.CountX), "Number of pattern repetitions along the local X/U direction.", sourceComponent);
            AddNumberSeries(set, "infill.cell_count_y", "Cell count Y", "-", values.Select(entry => entry.CountY), "Number of pattern repetitions along the local Y/V direction.", sourceComponent);
            AddNumberSeries(set, "infill.cell_count_z", "Cell count Z", "-", values.Select(entry => entry.CountZ), "Number of pattern repetitions along the local Z/W or layer direction.", sourceComponent);
            AddNumberSeries(set, "infill.dimension_x", "Dimension X", "model units", values.Select(entry => entry.DimensionX), "Evaluated infill-domain span along the local X/U direction.", sourceComponent);
            AddNumberSeries(set, "infill.dimension_y", "Dimension Y", "model units", values.Select(entry => entry.DimensionY), "Evaluated infill-domain span along the local Y/V direction.", sourceComponent);
            AddNumberSeries(set, "infill.dimension_z", "Dimension Z", "model units", values.Select(entry => entry.DimensionZ), "Evaluated infill-domain span along the local Z/W direction.", sourceComponent);

            if (domainCount > 1)
            {
                set.Add(WasperKpi.Scalar(
                    "infill.domain_count",
                    "Domain count",
                    "Infill",
                    "count",
                    domainCount,
                    "Number of distinct infill domains evaluated by the component.",
                    sourceComponent));
            }

            return set;
        }

        public static WasperInfillKpiEntry FromVolumetric(
            WasperVolumetricPatternDescriptor pattern,
            double dimensionX,
            double dimensionY,
            double dimensionZ)
        {
            return new WasperInfillKpiEntry
            {
                CellName = pattern.PatternName,
                CellNameShort = VolumetricShortName(pattern),
                CountX = pattern.CountU,
                CountY = pattern.CountV,
                CountZ = pattern.CountW,
                DimensionX = Math.Abs(dimensionX),
                DimensionY = Math.Abs(dimensionY),
                DimensionZ = Math.Abs(dimensionZ)
            };
        }

        public static WasperInfillKpiEntry FromParameters(
            IWasperInfillParams parameters,
            double dimensionX,
            double dimensionY,
            double dimensionZ)
        {
            string name = parameters?.InfillKind ?? "Unknown";
            string shortName = name;
            double countX = 1.0;
            double countY = 1.0;
            double countZ = 1.0;

            if (parameters is WasperTpmsInfillParams tpms)
            {
                name = WasperTpmsPatternMath.Name(tpms.Type);
                shortName = TpmsShortName(tpms.Type);
                countX = tpms.CountX;
                countY = tpms.CountY;
                countZ = tpms.CountZ;
            }
            else if (parameters is WasperPolyhedralInfillParams polyhedral)
            {
                name = WasperPolyhedralPatternMath.Name(polyhedral.Type);
                shortName = WasperPolyhedralInfillParams.Tag(polyhedral.Type);
                countX = polyhedral.CountX;
                countY = polyhedral.CountY;
                countZ = polyhedral.CountZ;
            }
            else if (parameters is WasperBrickInfillParams brick)
            {
                name = "Brick-like";
                shortName = "Brick";
                countX = brick.CountU;
                countY = brick.CountV;
                countZ = 1.0;
            }
            else if (parameters is WasperInfill2DParams planar)
            {
                name = WasperPlanarPatternMath.Name(planar.Type);
                shortName = WasperInfill2DParams.Tag(planar.Type);
                countX = planar.Count;
            }
            else if (parameters is WasperTurtleInfillParams turtle)
            {
                name = "Turtle";
                shortName = "Turtle";
                countX = turtle.CountX;
                countY = turtle.CountY;
                countZ = turtle.CountZ;
            }

            return new WasperInfillKpiEntry
            {
                CellName = name,
                CellNameShort = shortName,
                CountX = countX,
                CountY = countY,
                CountZ = countZ,
                DimensionX = Math.Abs(dimensionX),
                DimensionY = Math.Abs(dimensionY),
                DimensionZ = Math.Abs(dimensionZ)
            };
        }

        private static string VolumetricShortName(WasperVolumetricPatternDescriptor pattern)
        {
            if (pattern.Tpms != null)
                return TpmsShortName(pattern.Tpms.Type);
            if (pattern.Polyhedral != null)
                return WasperPolyhedralInfillParams.Tag(pattern.Polyhedral.Type);
            return "Brick";
        }

        private static string TpmsShortName(int type)
        {
            return type switch
            {
                0 => "P",
                1 => "Di",
                2 => "Gyr",
                3 => "IWP",
                4 => "Neo",
                5 => "Lidi",
                6 => "FK-S",
                7 => "FK-Y",
                _ => "?"
            };
        }

        private static void AddNumberSeries(
            WasperKpiSet set,
            string key,
            string label,
            string unit,
            IEnumerable<double> sourceValues,
            string description,
            string sourceComponent)
        {
            List<double> values = (sourceValues ?? Enumerable.Empty<double>())
                .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
                .ToList();
            if (values.Count == 0)
                return;
            if (values.Count == 1)
            {
                set.Add(WasperKpi.Scalar(key, label, "Infill", unit, values[0], description, sourceComponent));
                return;
            }

            set.Add(new WasperKpi
            {
                Key = key,
                Label = label,
                Group = "Infill",
                Unit = unit,
                TextValue = string.Join(", ", values.Select(FormatNumber)),
                Description = description + " Multiple values are comma-separated in cell/domain assignment order.",
                Source = sourceComponent ?? string.Empty
            });
        }

        private static string JoinText(IEnumerable<string> values)
        {
            return string.Join(", ", (values ?? Enumerable.Empty<string>())
                .Select(value => value?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value)));
        }

        private static string FormatNumber(double value) =>
            value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    public enum WasperFabricationUnitMode
    {
        Auto = -1,
        Millimetres = 0,
        Centimetres = 1,
        Metres = 2
    }

    public static class WasperPathKpiExtractor
    {
        public static WasperKpiSet Extract(
            WasperPrintPath path,
            string sourceComponent = "wsp_path",
            WasperFabricationUnitMode unitMode = WasperFabricationUnitMode.Auto)
        {
            var set = new WasperKpiSet { SourceComponent = sourceComponent ?? "wsp_path" };
            if (path == null)
            {
                set.AddWarning("No WASPer Print Path was supplied.");
                return set;
            }

            UnitScale sourceScale = UnitScale.FromCode(path.KpiUnits ?? 0);
            UnitScale targetScale = unitMode == WasperFabricationUnitMode.Auto
                ? sourceScale
                : UnitScale.FromCode((int)unitMode);
            double packedLengthFactor = targetScale.LengthFromMillimetres /
                sourceScale.LengthFromMillimetres;
            double packedVolumeFactor = targetScale.VolumeFromCubicMillimetres /
                sourceScale.VolumeFromCubicMillimetres;
            double rawLengthFactor = targetScale.LengthFromMillimetres;
            double? meanPackedSpeed = Mean(path.KpiPrintSpeed);
            double? meanSpeed = meanPackedSpeed.HasValue
                ? meanPackedSpeed.Value * packedLengthFactor
                : Scale(Mean(path.PrintSpeed), rawLengthFactor);

            Add(set, "fabrication.point_count", "Point count", "Fabrication", "count", path.Points?.DataCount, "Number of stored printing points.");
            Add(set, "fabrication.layer_count", "Layer count", "Fabrication", "count", path.KpiLayers ?? path.PtPlanes?.BranchCount, "Number of logical print layers.");
            Add(set, "fabrication.path_length", "Printed path length", "Fabrication", targetScale.LengthUnit, Scale(path.KpiPathLength, packedLengthFactor), "Total printed path length from the packed path KPI fields.");
            Add(set, "fabrication.print_time", "Estimated print time", "Fabrication", "min", path.KpiTimeMin, "Estimated fabrication time.");
            Add(set, "fabrication.deposited_volume", "Deposited volume", "Fabrication", targetScale.VolumeUnit, Scale(path.KpiVolume, packedVolumeFactor), "Total deposited material volume.");
            Add(set, "fabrication.deposited_mass", "Deposited mass", "Fabrication", "kg", path.KpiMassKg, "Total deposited material mass calculated from deposited volume and connected WASPer Material density.");
            Add(set, "fabrication.mean_layer_height", "Mean layer height", "Fabrication", targetScale.LengthUnit, Scale(Mean(path.LayerH), rawLengthFactor), "Mean valid layer height.");
            Add(set, "fabrication.mean_layer_width", "Mean nominal layer width", "Fabrication", targetScale.LengthUnit, Scale(Mean(path.LayerW), rawLengthFactor), "Mean nominal bead width.");
            Add(set, "fabrication.mean_print_speed", "Mean print speed", "Fabrication", targetScale.SpeedUnit, meanSpeed, "Mean valid deposition speed.");
            Add(set, "fabrication.mean_flow", "Mean flow", "Fabrication", "-", Mean(path.Flows), "Mean valid flow value.");
            return set;
        }

        public static void ConvertFabricationKpis(
            IEnumerable<WasperKpi> items,
            int targetUnitCode)
        {
            UnitScale target = UnitScale.FromCode(targetUnitCode);
            foreach (WasperKpi kpi in items ?? Enumerable.Empty<WasperKpi>())
            {
                if (kpi?.Value.HasValue != true ||
                    !string.Equals(kpi.Group, "Fabrication", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                int dimension = kpi.Key switch
                {
                    "fabrication.deposited_volume" => 3,
                    "fabrication.path_length" => 1,
                    "fabrication.mean_layer_height" => 1,
                    "fabrication.mean_layer_width" => 1,
                    "fabrication.mean_print_speed" => 1,
                    _ => 0
                };
                if (dimension == 0 || !TryUnitCode(kpi.Unit, out int sourceCode))
                    continue;
                UnitScale source = UnitScale.FromCode(sourceCode);
                double factor = target.LengthFromMillimetres /
                    source.LengthFromMillimetres;
                if (dimension == 3)
                    factor = factor * factor * factor;
                kpi.Value *= factor;
                kpi.Unit = dimension == 3
                    ? target.VolumeUnit
                    : kpi.Key == "fabrication.mean_print_speed"
                        ? target.SpeedUnit
                        : target.LengthUnit;
            }
        }

        private static bool TryUnitCode(string unit, out int code)
        {
            string normalized = (unit ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized.StartsWith("mm", StringComparison.Ordinal))
            {
                code = 0;
                return true;
            }
            if (normalized.StartsWith("cm", StringComparison.Ordinal))
            {
                code = 1;
                return true;
            }
            if (normalized.StartsWith("m", StringComparison.Ordinal))
            {
                code = 2;
                return true;
            }
            code = 0;
            return false;
        }

        private static double? Scale(double? value, double factor) =>
            value.HasValue && IsFinite(value.Value)
                ? value.Value * factor
                : (double?)null;

        private static void Add(WasperKpiSet set, string key, string label, string group, string unit, double? value, string description)
        {
            if (value.HasValue && IsFinite(value.Value))
                set.Add(WasperKpi.Scalar(key, label, group, unit, value.Value, description, set.SourceComponent));
        }

        private static double? Mean<T>(DataTree<T> tree) where T : struct
        {
            if (tree == null)
                return null;
            double sum = 0;
            int count = 0;
            for (int i = 0; i < tree.BranchCount; i++)
            {
                foreach (T item in tree.Branches[i])
                {
                    double value = Convert.ToDouble(item, CultureInfo.InvariantCulture);
                    if (!IsFinite(value))
                        continue;
                    sum += value;
                    count++;
                }
            }
            return count == 0 ? (double?)null : sum / count;
        }

        private readonly struct UnitScale
        {
            private UnitScale(int code, string lengthUnit, double lengthFromMillimetres)
            {
                Code = code;
                LengthUnit = lengthUnit;
                LengthFromMillimetres = lengthFromMillimetres;
            }

            public int Code { get; }
            public string LengthUnit { get; }
            public string VolumeUnit => LengthUnit + "3";
            public string SpeedUnit => LengthUnit + "/min";
            public double LengthFromMillimetres { get; }
            public double VolumeFromCubicMillimetres =>
                LengthFromMillimetres * LengthFromMillimetres * LengthFromMillimetres;

            public static UnitScale FromCode(int code)
            {
                return code switch
                {
                    1 => new UnitScale(1, "cm", 0.1),
                    2 => new UnitScale(2, "m", 0.001),
                    _ => new UnitScale(0, "mm", 1.0)
                };
            }
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
