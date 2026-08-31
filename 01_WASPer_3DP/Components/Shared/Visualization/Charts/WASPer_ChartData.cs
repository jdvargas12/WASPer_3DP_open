using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace WASPer_3DP
{
    /// <summary>
    /// Describes one numeric variable that can be plotted or used to identify an individual.
    /// The contract is independent of Grasshopper trees and WasperStudy storage.
    /// </summary>
    public sealed class WasperChartVariable
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Unit { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public bool IsParameter { get; set; }

        public string DisplayName => string.IsNullOrWhiteSpace(Unit)
            ? Name
            : $"{Name} [{Unit}]";
    }

    /// <summary>
    /// One design solution or study iteration. IndividualId is the stable key used by linked
    /// Dashboard charts, grids, snapshots, and G-code views.
    /// </summary>
    public sealed class WasperChartIndividual
    {
        public int IndividualId { get; set; }
        public string Name { get; set; } = string.Empty;
        public Dictionary<string, double> Values { get; set; } =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Metadata { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool TryGetValue(string variableKey, out double value)
        {
            value = double.NaN;
            return !string.IsNullOrWhiteSpace(variableKey) &&
                Values != null &&
                Values.TryGetValue(variableKey, out value) &&
                !double.IsNaN(value) &&
                !double.IsInfinity(value);
        }
    }

    /// <summary>
    /// Normalized table consumed by Dashboard adapters and reusable renderers.
    /// </summary>
    public sealed class WasperChartDataset
    {
        public List<WasperChartVariable> Variables { get; set; } =
            new List<WasperChartVariable>();
        public List<WasperChartIndividual> Individuals { get; set; } =
            new List<WasperChartIndividual>();

        public WasperChartVariable FindVariable(string key)
        {
            return Variables?.FirstOrDefault(variable =>
                string.Equals(variable.Key, key, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// One render-ready Cartesian sample retaining the originating individual identity.
    /// </summary>
    public sealed class WasperChartPoint
    {
        public int IndividualId { get; set; } = -1;
        public int DataIndex { get; set; } = -1;
        public string Label { get; set; } = string.Empty;
        public double X { get; set; }
        public double Y { get; set; }

        /// <summary>
        /// Per-point override for the series colour, used when a host colours markers by a
        /// categorical variable. Null keeps the series colour.
        /// </summary>
        public Color? Color { get; set; }

        public Dictionary<string, string> Metadata { get; set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool IsValid =>
            !double.IsNaN(X) &&
            !double.IsInfinity(X) &&
            !double.IsNaN(Y) &&
            !double.IsInfinity(Y);
    }

    /// <summary>
    /// Render-ready line/marker series shared by Data Vis and Dashboard hosts.
    /// </summary>
    public sealed class WasperChartSeries
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public List<WasperChartPoint> Points { get; set; } = new List<WasperChartPoint>();
        public Color Color { get; set; } = Color.SteelBlue;
        public int MarkerType { get; set; }
        public double MarkerSize { get; set; } = 5.0;
        public int LineType { get; set; }
        public double LineWidth { get; set; } = 1.5;
    }
}
