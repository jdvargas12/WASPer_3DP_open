#region Component Description
/*
    Component Name:
        wsp_Da11_Chart Legend Params

    Nickname:
        Legend Params

    Version:
        Assembly-derived (vMajor.Minor.Build)

    Category / Subcategory:
        WASPerformance / 1.1_Data Vis

    Description:
        Creates reusable series labels, legend placement, spacing, wrapping, and
        typography for any WASPer chart type.

    Inputs:
        labels    : optional dataset labels in series order; empty hides the legend
        location  : 0=auto/top-right; 1..6=inside positions; 7=outside bottom;
                    8=right; 9=left; 10=top; 11=bottom
        distance  : legend-to-plot gap as 0..0.5 of the shorter plot dimension
        wrap_rows : true wraps legend items using the requested column count
        columns   : maximum legend columns when wrapping (minimum 1)
        text_size : legend font size in typographic points

    Output:
        legend_params : typed reusable WasperChartLegendSettings
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Reflection;

using Grasshopper.Kernel;
#endregion

namespace WASPer_3DP.Components._1_1_Data_Vis
{
    public sealed class wsp_Da11_Chart_Legend_Params : GH_Component
    {
        private readonly string _version;

        public wsp_Da11_Chart_Legend_Params()
            : base(
                "wsp_Da11_Chart Legend Params", "Legend Params",
                "Creates reusable series labels, legend placement, spacing, wrapping, " +
                "and typography for any WASPer chart type.", global::WASPer_3DP.WASPerPalette.Performance, "1.1_Data Vis")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _version = v == null ? "v1.0.5" : $"v{v.Major}.{v.Minor}.{v.Build}";
            Message = _version;
        }

        public override Guid ComponentGuid => new Guid("6F62CB31-4977-45A6-A491-3EEB0D255F7D");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Da11_Chart Legend Params.png");
                    return s == null ? null : new System.Drawing.Bitmap(s);
                }
                catch { return null; }
            }
        }

        // ── inputs ────────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddTextParameter("labels", "labels", "Optional dataset labels in series order. Empty labels hide the legend.", GH_ParamAccess.list);
            p.AddIntegerParameter("location", "location", "0=auto/top-right; 1..6=inside positions; 7=outside bottom; 8=right; 9=left; 10=top; 11=bottom.", GH_ParamAccess.item, 5);
            p.AddNumberParameter("distance", "distance", "Legend-to-plot gap as 0..0.5 of the shorter plot dimension.", GH_ParamAccess.item, 0.2);
            p.AddBooleanParameter("wrap_rows", "wrap", "True wraps legend items using the requested column count.", GH_ParamAccess.item, false);
            p.AddIntegerParameter("columns", "columns", "Maximum legend columns when wrapping. Minimum 1.", GH_ParamAccess.item, 3);
            p.AddNumberParameter("text_size", "text_size", "Legend font size in typographic points.", GH_ParamAccess.item, 10.0);

            for (int i = 0; i < p.ParamCount; i++) p[i].Optional = true;
        }

        // ── outputs ───────────────────────────────────────────────────────────
        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddGenericParameter("legend_params", "legend_p", "Typed reusable WASPer chart-legend settings.", GH_ParamAccess.item);

        // ── solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess da)
        {
            var s = new WasperChartLegendSettings();
            var labels = new List<string>();

            int loc = 5, cols = 3;
            double dist = .2, size = 10;
            bool wrap = false;

            da.GetDataList(0, labels);
            da.GetData(1, ref loc);
            da.GetData(2, ref dist);
            da.GetData(3, ref wrap);
            da.GetData(4, ref cols);
            da.GetData(5, ref size);

            s.Location = loc;
            s.Distance = dist;
            s.WrapRows = wrap;
            s.Columns = cols;
            s.TextSize = size;

            s.Labels = labels;
            s.Location = Math.Max(0, Math.Min(11, s.Location));
            s.Distance = Math.Max(0, Math.Min(0.5, s.Distance));
            s.Columns = Math.Max(1, s.Columns);
            if (s.TextSize <= 0) s.TextSize = 10;

            Message = _version;
            da.SetData(0, new WasperChartLegendSettingsGoo(s));
        }
    }
}
