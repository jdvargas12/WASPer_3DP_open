using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

namespace WASPer_3DP.Components._1_Studies
{
    /// <summary>
    /// Creates a standard WASPer KPI set from user-defined names and numeric or text values.
    /// </summary>
    public sealed class wsp_Sm03_Custom_KPIs : GH_Component
    {
        private readonly string _version;
        private static Bitmap _icon;

        public wsp_Sm03_Custom_KPIs()
            : base(
                "wsp_Sm03_Custom KPIs",
                "Custom KPIs",
                "Creates a WASPer-compatible KPI set from custom headers, values, and optional " +
                "units. The set can be connected directly to Sm01 kpi_sets or passed through " +
                "Sm02 to identify several custom subsets of the same type.",
                WASPerPalette.Performance,
                "1.2_Studies")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _version = version == null
                ? "v1.0.x"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
            Message = _version;
        }

        public override Guid ComponentGuid =>
            new Guid("6F9A45C3-144A-4A29-B8B5-7E6DC8F3B217");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon => _icon ??= CreateIcon();

        protected override void RegisterInputParams(GH_InputParamManager parameters)
        {
            parameters.AddTextParameter(
                "KPI type name",
                "type_name",
                "Name used to group these KPIs in the Sm01 Manager and in exports. " +
                "Example: Experimental, Cost, Embodied Carbon, or Thermal Validation. " +
                "When omitted, Custom is used.",
                GH_ParamAccess.item,
                "Custom");
            parameters.AddTextParameter(
                "KPI headers",
                "kpi_head",
                "Readable KPI names, in the same order as kpi_val. Example: [Mass, Print time, " +
                "Pass]. Empty or missing names are generated as KPI 1, KPI 2, and so on. " +
                "Duplicate names are accepted and receive unique internal keys.",
                GH_ParamAccess.list);
            parameters.AddGenericParameter(
                "KPI values",
                "kpi_val",
                "Numeric or text KPI values. Example: [12.5, 43.2, True]. Each value is paired " +
                "with the header at the same list index. Missing headers are generated; extra " +
                "headers without values are ignored with a warning.",
                GH_ParamAccess.list);
            parameters.AddTextParameter(
                "KPI units",
                "kpi_unit",
                "Optional unit for each value, in the same order as kpi_val. Example: " +
                "[kg, s, %]. Paired with the value at the same list index; indices beyond the " +
                "end of this list get no unit.",
                GH_ParamAccess.list);
            parameters[0].Optional = true;
            parameters[1].Optional = true;
            parameters[2].Optional = true;
            parameters[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager parameters)
        {
            parameters.AddGenericParameter(
                "Custom KPI set",
                "kpi_set",
                "WASPer-compatible custom KPI set. Connect directly to Sm01 kpi_sets, combine " +
                "it with native KPI outputs, or pass it through Sm02 KPIs Subset.",
                GH_ParamAccess.item);
            parameters.AddTextParameter(
                "Summary",
                "summary",
                "Creation summary reporting the KPI type, number of numeric and text records, " +
                "and any generated or ignored headers.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess dataAccess)
        {
            string typeName = "Custom";
            var headers = new List<string>();
            var values = new List<IGH_Goo>();
            var units = new List<string>();
            dataAccess.GetData(0, ref typeName);
            dataAccess.GetDataList(1, headers);
            dataAccess.GetDataList(2, values);
            dataAccess.GetDataList(3, units);

            typeName = string.IsNullOrWhiteSpace(typeName) ? "Custom" : typeName.Trim();
            var kpiSet = new WasperKpiSet
            {
                SchemaVersion = 2,
                SourceComponent = Name,
                SourceVersion = _version
            };
            var notices = new List<string>();

            if (values.Count == 0)
            {
                string emptySummary = $"Custom KPI set '{typeName}': 0 KPIs. Connect kpi_val.";
                if (Params.Input[2].SourceCount > 0)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "kpi_val contains no values.");
                dataAccess.SetData(0, new WasperKpiSetGoo(kpiSet, this));
                dataAccess.SetData(1, emptySummary);
                Message = _version;
                return;
            }

            if (headers.Count < values.Count)
            {
                int generated = values.Count - headers.Count;
                notices.Add($"generated {generated} missing header{Plural(generated)}");
            }
            else if (headers.Count > values.Count)
            {
                int ignored = headers.Count - values.Count;
                notices.Add($"ignored {ignored} header{Plural(ignored)} without a value");
            }

            var usedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int numericCount = 0;
            int textCount = 0;
            for (int index = 0; index < values.Count; index++)
            {
                string header = index < headers.Count ? headers[index]?.Trim() : string.Empty;
                if (string.IsNullOrWhiteSpace(header))
                {
                    header = $"KPI {index + 1}";
                    if (index < headers.Count)
                        notices.Add($"replaced empty header {index + 1}");
                }

                string key = UniqueKey(typeName, header, usedKeys);
                string unit = index < units.Count ? units[index]?.Trim() ?? string.Empty : string.Empty;
                IGH_Goo value = values[index];
                if (TryGetFiniteNumber(value, out double number))
                {
                    kpiSet.Add(WasperKpi.Scalar(
                        key,
                        header,
                        typeName,
                        unit,
                        number,
                        "User-defined numeric KPI.",
                        Name));
                    numericCount++;
                }
                else
                {
                    string text = KpiText(value);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        notices.Add($"ignored empty value {index + 1}");
                        continue;
                    }
                    WasperKpi textKpi = WasperKpi.Text(
                        key,
                        header,
                        typeName,
                        text,
                        "User-defined text KPI.",
                        Name);
                    if (!string.IsNullOrWhiteSpace(unit))
                        textKpi.Unit = unit;
                    kpiSet.Add(textKpi);
                    textCount++;
                }
            }

            foreach (string notice in notices)
            {
                kpiSet.AddWarning(notice);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, notice + ".");
            }

            string summary = $"Custom KPI set '{typeName}': {kpiSet.Items.Count} KPIs " +
                $"({numericCount} numeric, {textCount} text).";
            if (notices.Count > 0)
                summary += " " + string.Join("; ", notices) + ".";

            dataAccess.SetData(0, new WasperKpiSetGoo(kpiSet, this));
            dataAccess.SetData(1, summary);
            Message = $"{_version} | {kpiSet.Items.Count} KPI{Plural(kpiSet.Items.Count)}";
        }

        private static bool TryGetFiniteNumber(IGH_Goo value, out double number)
        {
            number = 0.0;
            return value != null &&
                value is not GH_Boolean &&
                GH_Convert.ToDouble(value, out number, GH_Conversion.Both) &&
                !double.IsNaN(number) &&
                !double.IsInfinity(number);
        }

        private static string KpiText(IGH_Goo value)
        {
            if (value == null)
                return "Null";
            try
            {
                object scriptValue = value.ScriptVariable();
                return Convert.ToString(scriptValue, CultureInfo.InvariantCulture) ?? string.Empty;
            }
            catch
            {
                return value.ToString() ?? string.Empty;
            }
        }

        private static string UniqueKey(
            string typeName,
            string header,
            ISet<string> usedKeys)
        {
            string root = "custom." + KeyToken(typeName) + "." + KeyToken(header);
            string key = root;
            int suffix = 2;
            while (!usedKeys.Add(key))
                key = root + "_" + suffix++;
            return key;
        }

        private static string KeyToken(string value)
        {
            var builder = new StringBuilder();
            bool separatorPending = false;
            foreach (char character in (value ?? string.Empty).Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(character))
                {
                    if (separatorPending && builder.Length > 0)
                        builder.Append('_');
                    builder.Append(character);
                    separatorPending = false;
                }
                else
                {
                    separatorPending = true;
                }
            }
            return builder.Length == 0 ? "kpi" : builder.ToString();
        }

        private static string Plural(int count)
        {
            return count == 1 ? string.Empty : "s";
        }

        private static Bitmap CreateIcon()
        {
            var bitmap = new Bitmap(24, 24);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var orangeBrush = new SolidBrush(Color.FromArgb(242, 166, 44));
            using var paleBrush = new SolidBrush(Color.FromArgb(255, 236, 201));
            using var darkPen = new Pen(Color.FromArgb(55, 55, 55), 1.4f);
            using var whitePen = new Pen(Color.White, 1.5f);
            using GraphicsPath card = RoundedRectangle(new RectangleF(2.5f, 3f, 19f, 18f), 3f);

            graphics.FillPath(paleBrush, card);
            graphics.DrawPath(darkPen, card);
            graphics.FillEllipse(orangeBrush, 5f, 6f, 5f, 5f);
            graphics.FillEllipse(orangeBrush, 5f, 13f, 5f, 5f);
            graphics.DrawLine(darkPen, 12f, 8.5f, 19f, 8.5f);
            graphics.DrawLine(darkPen, 12f, 15.5f, 19f, 15.5f);
            graphics.DrawLine(whitePen, 6.5f, 8.5f, 8.5f, 8.5f);
            graphics.DrawLine(whitePen, 7.5f, 7.5f, 7.5f, 9.5f);
            return bitmap;
        }

        private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
        {
            float diameter = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
