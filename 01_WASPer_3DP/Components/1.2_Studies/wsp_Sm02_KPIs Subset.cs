using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

namespace WASPer_3DP.Components._1_Studies
{
    /// <summary>
    /// Adds a persistent subset identifier to one or more KPI sets so repeated KPI types
    /// remain distinct when they are merged by the WASPer Study Manager.
    /// </summary>
    public sealed class wsp_Sm02_KPIs_Subset : GH_Component
    {
        private readonly string _version;
        private static Bitmap _icon;

        public wsp_Sm02_KPIs_Subset()
            : base(
                "wsp_Sm02_KPIs Subset",
                "KPIs Subset",
                "Creates identified KPI subsets for comparing repeated analyses of the same " +
                "KPI type in Sm01. For example, two Thermal - Numerical sets identified as " +
                "Dh and Gyroid appear as Thermal - Numerical (Dh) and " +
                "Thermal - Numerical (Gyroid). Both inputs accept lists.",
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
            new Guid("B7E37D1A-95BC-4C62-93D8-31AF6B0F82E4");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon => _icon ??= CreateIcon();

        protected override void RegisterInputParams(GH_InputParamManager parameters)
        {
            parameters.AddGenericParameter(
                "kpi_set",
                "kpi_set",
                "KPI set or list of KPI sets to identify as subsets. Connect outputs such as " +
                "Ht03 thermal_kpis or Ch04 por_kpis. A single set can be repeated for several " +
                "identifiers, and several sets can share one identifier.",
                GH_ParamAccess.list);
            parameters.AddTextParameter(
                "identifier",
                "id",
                "Subset identifier or list of identifiers shown in parentheses after the KPI " +
                "type in Sm01. Example: id = Dh changes Thermal - Numerical to " +
                "Thermal - Numerical (Dh). Matching lists pair item-by-item; shorter lists " +
                "repeat their final item. When omitted, the identifier is Subset.",
                GH_ParamAccess.list);
            parameters[0].Optional = true;
            parameters[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager parameters)
        {
            parameters.AddGenericParameter(
                "KPI subsets",
                "kpi_subset",
                "Identified KPI subset list. Connect this output directly to Sm01 kpi_sets. " +
                "Each subset retains the original KPI values, names, units, type, method, and " +
                "source metadata while receiving an independent subset identity.",
                GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess dataAccess)
        {
            var inputGoos = new List<IGH_Goo>();
            var identifiers = new List<string>();
            dataAccess.GetDataList(0, inputGoos);
            dataAccess.GetDataList(1, identifiers);

            List<WasperKpiSet> sets = inputGoos
                .OfType<WasperKpiSetGoo>()
                .Where(goo => goo.Value != null)
                .Select(goo => goo.Value)
                .ToList();

            if (sets.Count == 0)
            {
                if (Params.Input[0].SourceCount > 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        "No valid WASPer KPI sets were found in kpi_set.");
                }
                Message = _version;
                dataAccess.SetDataList(0, Array.Empty<WasperKpiSetGoo>());
                return;
            }

            identifiers = identifiers
                .Select(identifier => identifier?.Trim() ?? string.Empty)
                .ToList();
            if (identifiers.Count == 0)
                identifiers.Add("Subset");

            int outputCount = Math.Max(sets.Count, identifiers.Count);
            var output = new List<WasperKpiSetGoo>(outputCount);
            for (int index = 0; index < outputCount; index++)
            {
                WasperKpiSet source = sets[Math.Min(index, sets.Count - 1)];
                string identifier = identifiers[Math.Min(index, identifiers.Count - 1)];
                if (string.IsNullOrWhiteSpace(identifier))
                {
                    identifier = "Subset";
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"Identifier {Math.Min(index, identifiers.Count - 1)} was empty; used Subset.");
                }
                output.Add(new WasperKpiSetGoo(source.CreateSubset(identifier)));
            }

            dataAccess.SetDataList(0, output);
            Message = $"{_version} | {output.Count} subset{(output.Count == 1 ? string.Empty : "s")}";
        }

        private static Bitmap CreateIcon()
        {
            var bitmap = new Bitmap(24, 24);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var orangeBrush = new SolidBrush(Color.FromArgb(242, 166, 44));
            using var lightBrush = new SolidBrush(Color.FromArgb(255, 224, 169));
            using var darkPen = new Pen(Color.FromArgb(55, 55, 55), 1.5f);
            using var textBrush = new SolidBrush(Color.FromArgb(55, 55, 55));

            graphics.FillRoundedRectangle(lightBrush, new RectangleF(3, 3, 14, 14), 2.5f);
            graphics.DrawRoundedRectangle(darkPen, new RectangleF(3, 3, 14, 14), 2.5f);
            graphics.FillRoundedRectangle(orangeBrush, new RectangleF(7, 7, 14, 14), 2.5f);
            graphics.DrawRoundedRectangle(darkPen, new RectangleF(7, 7, 14, 14), 2.5f);

            using var font = new Font(FontFamily.GenericSansSerif, 7.5f, FontStyle.Bold);
            graphics.DrawString("()", font, textBrush, new PointF(8.3f, 9.6f));
            return bitmap;
        }
    }

    internal static class KpiSubsetIconExtensions
    {
        public static void FillRoundedRectangle(
            this Graphics graphics,
            Brush brush,
            RectangleF bounds,
            float radius)
        {
            using GraphicsPath path = RoundedRectangle(bounds, radius);
            graphics.FillPath(brush, path);
        }

        public static void DrawRoundedRectangle(
            this Graphics graphics,
            Pen pen,
            RectangleF bounds,
            float radius)
        {
            using GraphicsPath path = RoundedRectangle(bounds, radius);
            graphics.DrawPath(pen, path);
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
