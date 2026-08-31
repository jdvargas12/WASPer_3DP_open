#region Component Description
/*
    Component Name:
        wsp_Da10_Chart Axis Params

    Nickname:
        Axis Params

    Version:
        Assembly-derived (vMajor.Minor.Build)

    Category / Subcategory:
        WASPerformance / 1.1_Data Vis

    Description:
        Creates reusable X/Y axis labels, ranges, tick formatting, and typography
        for WASPer chart components such as Scatter Plot and Bar Chart.

    Inputs:
        x_title          : optional X-axis title
        y_title          : optional Y-axis title
        y2_title         : optional secondary (right-hand) Y-axis title
        x_min_max        : optional X range as 'minimum;maximum'
        y_min_max        : optional Y range as 'minimum;maximum'
        y2_min_max       : optional secondary Y range as 'minimum;maximum'
        x_tick_interval  : X major-tick spacing; 0 = automatic
        y_tick_interval  : Y major-tick spacing; 0 = automatic
        y2_tick_interval : secondary Y major-tick spacing; 0 = automatic
        x_ticks_integer  : true uses integer labels for intervals >= 1
        y_ticks_integer  : true uses integer labels for intervals >= 1
        y2_ticks_integer : true uses integer labels for intervals >= 1
        axis_title_size  : shared X/Y/Y2 axis-title font size (points)
        axis_text_size   : shared X/Y/Y2 tick-label font size (points)
        title_offset     : extra gap (points) between tick labels and axis titles; 0 = default

    Notes:
        The secondary Y axis is only drawn by a chart if that chart actually has series
        assigned to it (e.g. Da01 Scatter Plot's y_vals_2). It reuses the shared
        axis_title_size/axis_text_size typography above; there is no separate Y2 font size.
        title_offset is likewise one shared value applied to all three axis titles (X, Y, Y2).

    Output:
        axis_params : typed reusable WasperChartAxisSettings
*/
#endregion

#region Usings
using System;
using System.Reflection;

using Grasshopper.Kernel;
#endregion

namespace WASPer_3DP.Components._1_1_Data_Vis
{
    public sealed class wsp_Da10_Chart_Axis_Params : GH_Component
    {
        private readonly string _version;

        public wsp_Da10_Chart_Axis_Params()
            : base(
                "wsp_Da10_Chart Axis Params", "Axis Params",
                "Creates reusable X/Y axis labels, ranges, tick formatting, and typography " +
                "for WASPer chart components such as Scatter Plot and future Bar Chart.", global::WASPer_3DP.WASPerPalette.Performance, "1.1_Data Vis")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _version = v == null ? "v1.0.5" : $"v{v.Major}.{v.Minor}.{v.Build}";
            Message = _version;
        }

        public override Guid ComponentGuid => new Guid("1E2FD2DD-54BD-405B-85C9-437F7489938F");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Da10_Chart Axis Params.png");
                    return s == null ? null : new System.Drawing.Bitmap(s);
                }
                catch { return null; }
            }
        }

        // ── inputs ────────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddTextParameter("x_title", "x_title", "Optional X-axis title. Empty omits it.", GH_ParamAccess.item, "");
            p.AddTextParameter("y_title", "y_title", "Optional Y-axis title. Empty omits it.", GH_ParamAccess.item, "");
            p.AddTextParameter("z_title", "z_title", "Optional Z-axis title for Da07 3D Graph. Empty omits it.", GH_ParamAccess.item, "");
            p.AddTextParameter("y2_title", "y2_title", "Optional secondary (right-hand) Y-axis title. Empty omits it.", GH_ParamAccess.item, "");
            p.AddTextParameter("x_min_max", "x_range", "Optional X range as 'minimum;maximum'. Empty derives it from data.", GH_ParamAccess.item, "");
            p.AddTextParameter("y_min_max", "y_range", "Optional Y range as 'minimum;maximum'. Empty derives it from data.", GH_ParamAccess.item, "");
            p.AddTextParameter("z_min_max", "z_range", "Optional Z range as 'minimum;maximum' for Da07 3D Graph.", GH_ParamAccess.item, "");
            p.AddTextParameter("y2_min_max", "y2_range", "Optional secondary Y range as 'minimum;maximum'. Empty derives it from axis-2 data.", GH_ParamAccess.item, "");
            p.AddNumberParameter("x_tick_interval", "x_tick", "X major-tick spacing. Zero selects automatic spacing.", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("y_tick_interval", "y_tick", "Y major-tick spacing. Zero selects automatic spacing.", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("z_tick_interval", "z_tick", "Z major-tick spacing for Da07. Zero selects automatic spacing.", GH_ParamAccess.item, 0.0);
            p.AddNumberParameter("y2_tick_interval", "y2_tick", "Secondary Y major-tick spacing. Zero selects automatic spacing.", GH_ParamAccess.item, 0.0);
            p.AddBooleanParameter("x_ticks_integer", "x_int", "Use integer X labels when the interval is >=1.", GH_ParamAccess.item, true);
            p.AddBooleanParameter("y_ticks_integer", "y_int", "Use integer Y labels when the interval is >=1.", GH_ParamAccess.item, true);
            p.AddBooleanParameter("z_ticks_integer", "z_int", "Use integer Z labels when the interval is >=1.", GH_ParamAccess.item, true);
            p.AddBooleanParameter("y2_ticks_integer", "y2_int", "Use integer secondary-Y labels when the interval is >=1.", GH_ParamAccess.item, true);
            p.AddNumberParameter("axis_title_size", "title_size", "Shared X/Y/Z/Y2 axis-title font size in typographic points.", GH_ParamAccess.item, 12.0);
            p.AddNumberParameter("axis_text_size", "text_size", "Shared X/Y/Z/Y2 tick-label font size in typographic points.", GH_ParamAccess.item, 10.0);
            p.AddNumberParameter("title_offset", "title_offset", "Extra gap between tick labels and axis titles, shared by all axes.", GH_ParamAccess.item, 0.0);
            p.AddIntegerParameter("line_type", "line_type", "Axis line type for Da07: 0 solid, 1 dash, 2 dot, 3 dash-dot.", GH_ParamAccess.item, 0);

            for (int i = 0; i < p.ParamCount; i++) p[i].Optional = true;
        }

        // ── outputs ───────────────────────────────────────────────────────────
        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddGenericParameter("axis_params", "axis_p", "Typed reusable WASPer chart-axis settings. Connect to Da01 Scatter Plot, Da02 Bar Chart, or another WASPer chart.", GH_ParamAccess.item);

        // ── solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess da)
        {
            var s = new WasperChartAxisSettings();

            string xl = "", yl = "", zl = "", y2l = "", xr = "", yr = "", zr = "", y2r = "";
            double xt = 0, yt = 0, zt = 0;
            bool xi = true, yi = true, zi = true;
            double title = 12, text = 10;
            double y2t = 0;
            bool y2i = true;
            double titleOffset = 0;
            int lineType = 0;

            da.GetData(0, ref xl);
            da.GetData(1, ref yl);
            da.GetData(2, ref zl); da.GetData(3, ref y2l);
            da.GetData(4, ref xr); da.GetData(5, ref yr); da.GetData(6, ref zr); da.GetData(7, ref y2r);
            da.GetData(8, ref xt); da.GetData(9, ref yt); da.GetData(10, ref zt); da.GetData(11, ref y2t);
            da.GetData(12, ref xi); da.GetData(13, ref yi); da.GetData(14, ref zi); da.GetData(15, ref y2i);
            da.GetData(16, ref title); da.GetData(17, ref text); da.GetData(18, ref titleOffset); da.GetData(19, ref lineType);

            s.XTitle = xl;
            s.YTitle = yl;
            s.XRange = xr;
            s.YRange = yr;
            s.ZRange = zr;
            s.XTickInterval = xt;
            s.YTickInterval = yt;
            s.ZTickInterval = zt;
            s.XTicksInteger = xi;
            s.YTicksInteger = yi;
            s.ZTicksInteger = zi;
            s.XTitleSize = s.YTitleSize = title > 0 ? title : 12;
            s.XTextSize = s.YTextSize = text > 0 ? text : 10;
            s.Y2Title = y2l;
            s.Y2Range = y2r;
            s.Y2TickInterval = y2t;
            s.Y2TicksInteger = y2i;
            s.ZTitle = zl;
            s.TitleOffset = titleOffset;
            s.LineType = Math.Max(0, Math.Min(3, lineType));

            Message = _version;
            da.SetData(0, new WasperChartAxisSettingsGoo(s));
        }
    }
}
