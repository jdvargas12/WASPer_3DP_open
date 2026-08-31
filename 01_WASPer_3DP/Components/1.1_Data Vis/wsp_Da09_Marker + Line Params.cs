#region Component Description
/*
    Component Name:
        wsp_Da09_Marker + Line Params

    Nickname:
        Marker + Line

    Category / Subcategory:
        WASPerformance / 1.1_Data Vis

    Description:
        Creates reusable marker and line style settings for WASPer chart components.
        Da01 Scatter Plot uses these settings together with its mode input:
        0 = markers only, 1 = line and markers, 2 = lines only.

    Inputs:
        marker_colors : optional native colours in dataset order. Missing colours use the chart palette.
        marker_size   : marker area in squared typographic points. Empty = 30; one value applies to all datasets.
        marker_type   : marker symbol by dataset. 0 circle, 1 square, 2 diamond, 3 triangle, 4 cross, 5 plus.
        line_colors   : optional native line colours in dataset order. Missing values follow marker colours.
        line_width    : line stroke width in typographic points. Empty = 1.2; one value applies to all datasets.
        line_type     : line pattern by dataset. 0 solid, 1 dash, 2 dot, 3 dash-dot.

    Output:
        marker_params : typed reusable marker and line settings
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;

using Grasshopper.Kernel;
#endregion

namespace WASPer_3DP.Components._1_1_Data_Vis
{
    public sealed class wsp_Da09_Marker_Line_Params : GH_Component
    {
        private readonly string _version;

        public wsp_Da09_Marker_Line_Params()
            : base(
                "wsp_Da09_Marker + Line Params", "Marker + Line",
                "Creates reusable marker and line styling for WASPer charts: colours, marker size/type, line width, and line pattern.", global::WASPer_3DP.WASPerPalette.Performance, "1.1_Data Vis")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _version = v == null ? "v1.0.5" : $"v{v.Major}.{v.Minor}.{v.Build}";
            Message = _version;
        }

        public override Guid ComponentGuid => new Guid("69D4B3B4-22D6-4A98-B5F2-879C6D6938F1");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Da09_Marker + Line Params.png");
                    return s == null ? null : new Bitmap(s);
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddColourParameter("marker_colors", "m_colors", "Optional marker colours in dataset order. Missing colours use the chart's built-in palette.", GH_ParamAccess.list);
            p.AddNumberParameter("marker_size", "m_size", "Marker area in squared typographic points. Empty uses 30; one value applies to every dataset; the last value repeats.", GH_ParamAccess.list);
            p.AddIntegerParameter("marker_type", "m_type", "Marker symbol by dataset: 0 circle, 1 square, 2 diamond, 3 triangle, 4 cross, 5 plus. The last value repeats.", GH_ParamAccess.list);
            p.AddColourParameter("line_colors", "l_colors", "Optional line colours in dataset order. Missing values follow marker colours / palette colours.", GH_ParamAccess.list);
            p.AddNumberParameter("line_width", "l_width", "Line stroke width in typographic points. Empty uses 1.2; one value applies to every dataset; the last value repeats.", GH_ParamAccess.list);
            p.AddIntegerParameter("line_type", "l_type", "Line pattern by dataset: 0 solid, 1 dash, 2 dot, 3 dash-dot. The last value repeats.", GH_ParamAccess.list);

            p[0].DataMapping = GH_DataMapping.Flatten;
            p[1].DataMapping = GH_DataMapping.Flatten;
            p[2].DataMapping = GH_DataMapping.Flatten;
            p[3].DataMapping = GH_DataMapping.Flatten;
            p[4].DataMapping = GH_DataMapping.Flatten;
            p[5].DataMapping = GH_DataMapping.Flatten;
            for (int i = 0; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddGenericParameter("marker_params", "marker_p", "Typed reusable WASPer marker and line settings. Connect to WASPer chart marker_p inputs.", GH_ParamAccess.item);

        protected override void SolveInstance(IGH_DataAccess da)
        {
            var markerColors = new List<Color>();
            var markerSizes = new List<double>();
            var markerTypes = new List<int>();
            var lineColors = new List<Color>();
            var lineWidths = new List<double>();
            var lineTypes = new List<int>();

            da.GetDataList(0, markerColors);
            da.GetDataList(1, markerSizes);
            da.GetDataList(2, markerTypes);
            da.GetDataList(3, lineColors);
            da.GetDataList(4, lineWidths);
            da.GetDataList(5, lineTypes);

            var settings = new WasperChartMarkerLineSettings
            {
                MarkerColorsArgb = markerColors.Select(WasperChartMarkerLineSettings.ToArgb).ToList(),
                MarkerSizes = markerSizes.Where(IsFinite).Select(v => Math.Max(1.0, v)).ToList(),
                MarkerTypes = markerTypes.Select(v => Math.Max(0, Math.Min(5, v))).ToList(),
                LineColorsArgb = lineColors.Select(WasperChartMarkerLineSettings.ToArgb).ToList(),
                LineWidths = lineWidths.Where(IsFinite).Select(v => Math.Max(0.1, v)).ToList(),
                LineTypes = lineTypes.Select(v => Math.Max(0, Math.Min(3, v))).ToList()
            };

            Message = _version;
            da.SetData(0, new WasperChartMarkerLineSettingsGoo(settings));
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    }
}
