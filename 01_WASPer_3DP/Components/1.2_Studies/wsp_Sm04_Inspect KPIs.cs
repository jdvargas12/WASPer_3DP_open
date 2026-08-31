using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Reflection;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace WASPer_3DP.Components._1_Studies
{
    /// <summary>
    /// Unpacks one or more WASPer KPI sets back into parallel type name, header, and value
    /// lists. The inverse of Sm03 Custom KPIs.
    /// </summary>
    public sealed class wsp_Sm04_Inspect_KPIs : GH_Component
    {
        private readonly string _version;
        private static Bitmap _icon;

        public wsp_Sm04_Inspect_KPIs()
            : base(
                "wsp_Sm04_Inspect KPIs",
                "Inspect KPIs",
                "Unpacks a KPI set or list of KPI sets back into type names, headers, and " +
                "values. Each KPI set becomes its own branch. This is the inverse of Sm03 " +
                "Custom KPIs.",
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
            new Guid("409824C4-F9D4-4C18-A3CB-3AB89BED5ECD");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon => _icon ??= CreateIcon();

        protected override void RegisterInputParams(GH_InputParamManager parameters)
        {
            parameters.AddGenericParameter(
                "kpi_set",
                "kpi_set",
                "KPI set or list of KPI sets to inspect. Connect outputs such as Sm03 kpi_set " +
                "or any other WASPer KPI-producing component. Each KPI set is unpacked into " +
                "its own branch.",
                GH_ParamAccess.list);
            parameters[0].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager parameters)
        {
            parameters.AddTextParameter(
                "Type names",
                "t_name",
                "KPI type name for each item, one branch per input KPI set.",
                GH_ParamAccess.tree);
            parameters.AddTextParameter(
                "KPI headers",
                "heads",
                "Readable KPI names, one branch per input KPI set, aligned with vals.",
                GH_ParamAccess.tree);
            parameters.AddGenericParameter(
                "KPI values",
                "vals",
                "Numeric or text KPI values, one branch per input KPI set, aligned with heads.",
                GH_ParamAccess.tree);
            parameters.AddTextParameter(
                "KPI units",
                "units",
                "Unit for each KPI value, one branch per input KPI set, aligned with heads " +
                "and vals.",
                GH_ParamAccess.tree);
            parameters.AddTextParameter(
                "Full KPI text",
                "full",
                "Full KPI description per item, formatted as \"type head: value unit\", one " +
                "branch per input KPI set.",
                GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess dataAccess)
        {
            var inputGoos = new List<IGH_Goo>();
            dataAccess.GetDataList(0, inputGoos);

            List<WasperKpiSet> sets = inputGoos
                .OfType<WasperKpiSetGoo>()
                .Where(goo => goo.Value != null)
                .Select(goo => goo.Value)
                .ToList();

            var typeNames = new DataTree<string>();
            var headers = new DataTree<string>();
            var values = new DataTree<IGH_Goo>();
            var units = new DataTree<string>();
            var full = new DataTree<string>();

            if (sets.Count == 0)
            {
                if (Params.Input[0].SourceCount > 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        "No valid WASPer KPI sets were found in kpi_set.");
                }
                dataAccess.SetDataTree(0, typeNames);
                dataAccess.SetDataTree(1, headers);
                dataAccess.SetDataTree(2, values);
                dataAccess.SetDataTree(3, units);
                dataAccess.SetDataTree(4, full);
                Message = _version;
                return;
            }

            int itemCount = 0;
            for (int setIndex = 0; setIndex < sets.Count; setIndex++)
            {
                var path = new GH_Path(setIndex);
                List<WasperKpi> items = sets[setIndex].Items ?? new List<WasperKpi>();
                foreach (WasperKpi kpi in items)
                {
                    if (kpi == null)
                        continue;

                    typeNames.Add(kpi.Group ?? string.Empty, path);
                    headers.Add(kpi.Label ?? kpi.Key ?? string.Empty, path);
                    values.Add(KpiValueGoo(kpi), path);
                    units.Add(kpi.Unit ?? string.Empty, path);
                    full.Add(FormatFull(kpi), path);
                    itemCount++;
                }
            }

            dataAccess.SetDataTree(0, typeNames);
            dataAccess.SetDataTree(1, headers);
            dataAccess.SetDataTree(2, values);
            dataAccess.SetDataTree(3, units);
            dataAccess.SetDataTree(4, full);
            Message = $"{_version} | {sets.Count} set{(sets.Count == 1 ? string.Empty : "s")}, " +
                $"{itemCount} KPI{(itemCount == 1 ? string.Empty : "s")}";
        }

        private static IGH_Goo KpiValueGoo(WasperKpi kpi)
        {
            if (kpi.Value.HasValue)
                return new GH_Number(kpi.Value.Value);
            return new GH_String(kpi.TextValue ?? string.Empty);
        }

        private static string FormatFull(WasperKpi kpi)
        {
            string typeName = kpi.Group ?? string.Empty;
            string header = kpi.Label ?? kpi.Key ?? string.Empty;
            string label = string.IsNullOrWhiteSpace(typeName)
                ? header
                : $"{typeName} {header}";
            string value = kpi.Value.HasValue
                ? kpi.Value.Value.ToString("G", CultureInfo.InvariantCulture)
                : (kpi.TextValue ?? string.Empty);
            string unit = string.IsNullOrWhiteSpace(kpi.Unit) ? string.Empty : " " + kpi.Unit;
            return $"{label}: {value}{unit}";
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
            graphics.DrawLine(darkPen, 5f, 8.5f, 12f, 8.5f);
            graphics.DrawLine(darkPen, 5f, 15.5f, 12f, 15.5f);
            graphics.FillEllipse(orangeBrush, 14f, 6f, 5f, 5f);
            graphics.FillEllipse(orangeBrush, 14f, 13f, 5f, 5f);
            graphics.DrawLine(whitePen, 15.5f, 8.5f, 17.5f, 8.5f);
            graphics.DrawLine(whitePen, 16.5f, 7.5f, 16.5f, 9.5f);
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
