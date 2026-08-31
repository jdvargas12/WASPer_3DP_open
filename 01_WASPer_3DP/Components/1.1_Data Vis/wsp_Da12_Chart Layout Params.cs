#region Component Description
/*
    Component Name:
        wsp_Da12_Chart Layout Params

    Nickname:
        Layout Params

    Version:
        Assembly-derived (vMajor.Minor.Build)

    Category / Subcategory:
        WASPerformance / 1.1_Data Vis

    Description:
        Creates reusable chart title, image dimensions, DPI, background, and
        output-file settings for any WASPer chart type.

    Inputs:
        title       : optional chart title; empty omits it
        title_size  : chart-title font size in typographic points
        dimensions  : physical image size as 'width_mm;height_mm' (default 160;100)
        dpi         : image resolution from 36 to 1200 DPI (default 150)
        transparent : true creates transparent PNG pixels; JPEG is flattened onto white
        show_refs   : show X/Y reference grid lines (default true)
        file_name   : optional output name with .png/.jpg/.jpeg
        file_path   : optional output directory

    Output:
        layout_params : typed reusable WasperChartLayoutSettings
*/
#endregion

#region Usings
using System;
using System.Reflection;

using Grasshopper.Kernel;
#endregion

namespace WASPer_3DP.Components._1_1_Data_Vis
{
    public sealed class wsp_Da12_Chart_Layout_Params : GH_Component
    {
        private readonly string _version;

        public wsp_Da12_Chart_Layout_Params()
            : base(
                "wsp_Da12_Chart Layout Params", "Layout Params",
                "Creates reusable chart title, image dimensions, DPI, background, " +
                "and output-file settings for any WASPer chart type.", global::WASPer_3DP.WASPerPalette.Performance, "1.1_Data Vis")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _version = v == null ? "v1.0.5" : $"v{v.Major}.{v.Minor}.{v.Build}";
            Message = _version;
        }

        public override Guid ComponentGuid => new Guid("911A9B73-3C25-473E-B6B1-FBAE62413E70");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Da12_Chart Layout Params.png");
                    return s == null ? null : new System.Drawing.Bitmap(s);
                }
                catch { return null; }
            }
        }

        // ── inputs ────────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddTextParameter("title", "title", "Optional chart title. Empty omits it.", GH_ParamAccess.item, "");
            p.AddNumberParameter("title_size", "title_size", "Chart-title font size in typographic points.", GH_ParamAccess.item, 14.0);
            p.AddTextParameter("dimensions", "dims", "Physical image size as 'width_mm;height_mm'. Default 160;100.", GH_ParamAccess.item, "160;100");
            p.AddIntegerParameter("dpi", "dpi", "Image resolution from 36 to 1200 DPI. Default 150.", GH_ParamAccess.item, 150);
            p.AddBooleanParameter("transparent", "transparent", "True creates transparent PNG pixels; JPEG is flattened onto white.", GH_ParamAccess.item, false);
            p.AddBooleanParameter("show_refs", "show_refs", "Show X/Y reference grid lines in the chart. Default true.", GH_ParamAccess.item, true);
            p.AddTextParameter("file_name", "file_name", "Optional output name with .png/.jpg/.jpeg. Empty uses title or a chart-type fallback.", GH_ParamAccess.item, "");
            p.AddTextParameter("file_path", "file_path", "Optional output directory. Empty lets the chart component choose its saved-definition/temp default.", GH_ParamAccess.item, "");

            for (int i = 0; i < p.ParamCount; i++) p[i].Optional = true;
        }

        // ── outputs ───────────────────────────────────────────────────────────
        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddGenericParameter("layout_params", "layout_p", "Typed reusable WASPer chart layout/output settings.", GH_ParamAccess.item);

        // ── solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess da)
        {
            var s = new WasperChartLayoutSettings();

            string title = "", dims = "160;100", name = "", path = "";
            double size = 14;
            int dpi = 150;
            bool transparent = false;

            da.GetData(0, ref title);
            da.GetData(1, ref size);
            da.GetData(2, ref dims);
            da.GetData(3, ref dpi);
            da.GetData(4, ref transparent);
            bool showRefs = true;
            da.GetData(5, ref showRefs);
            da.GetData(6, ref name);
            da.GetData(7, ref path);

            s.Title = title;
            s.TitleSize = size;
            s.Dimensions = dims;
            s.Dpi = dpi;
            s.TransparentBackground = transparent;
            s.ShowReferences = showRefs;
            s.FileName = name;
            s.FilePath = path;

            if (s.TitleSize <= 0) s.TitleSize = 14;
            s.Dpi = Math.Max(36, Math.Min(1200, s.Dpi));

            Message = _version;
            da.SetData(0, new WasperChartLayoutSettingsGoo(s));
        }
    }
}
