using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;

using Grasshopper.Kernel;

using Rhino.Geometry;

namespace WASPer_3DP.Components._3_0_Slicing_BJ
{
    public sealed class wsp_Sl12_Binder_Jet_Settings : GH_Component
    {
        private readonly string _versionTag;
        private static Bitmap _icon;

        public wsp_Sl12_Binder_Jet_Settings()
            : base(
                "wsp_Sl12_Binder Jet Settings",
                "Binder Settings",
                "Optional field-to-binder mapping settings for Sl09. Leave Sl09 settings empty for the normal binary defaults.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "3.0_Slicing BJ")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version == null ? "v1.0.x" : $"v{version.Major}.{version.Minor}.{version.Build}";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("38DD46F0-4FD8-4876-B6A3-58C87A8B92ED");
        public override GH_Exposure Exposure => GH_Exposure.primary;
        protected override Bitmap Icon => _icon ??= CreateIcon();

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddIntegerParameter("mode", "mode", "0 = binary; 1 = grayscale.", GH_ParamAccess.item, 0);
            p.AddNumberParameter("threshold", "level", "Binary solid boundary. Values <= level receive maximum binder.", GH_ParamAccess.item, 0.0);
            p.AddIntervalParameter("field_range", "range", "Grayscale interval: lower = maximum binder, upper = no binder.", GH_ParamAccess.item, new Interval(-1.0, 1.0));
            p.AddBooleanParameter("invert", "invert", "Reverse the black/white binder convention.", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("binder_settings", "settings", "Settings object for Sl09.", GH_ParamAccess.item);
            p.AddTextParameter("info", "info", "Resolved mapping.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            int mode = 0;
            double threshold = 0.0;
            Interval range = new Interval(-1.0, 1.0);
            bool invert = false;
            da.GetData(0, ref mode);
            da.GetData(1, ref threshold);
            da.GetData(2, ref range);
            da.GetData(3, ref invert);
            if (!double.IsFinite(threshold) || !double.IsFinite(range.T0) || !double.IsFinite(range.T1))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "level and range values must be finite.");
                return;
            }

            var settings = new WasperBinderSettings(
                mode == 1 ? WasperFieldBitmapMode.Linear : WasperFieldBitmapMode.Binary,
                threshold,
                range,
                invert);
            if (mode != 0 && mode != 1)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "mode was clamped to 0 (Binary) or 1 (Grayscale).");
            da.SetData(0, new WasperBinderSettingsGoo(settings));
            da.SetData(1, settings.Summary);
            Message = _versionTag + " | " + (settings.Mode == WasperFieldBitmapMode.Binary ? "Binary" : "Grayscale");
        }

        private static Bitmap CreateIcon()
        {
            var bitmap = new Bitmap(24, 24);
            using Graphics g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var pen = new Pen(Color.FromArgb(55, 60, 70), 1.5f);
            using var brush = new SolidBrush(Color.FromArgb(95, 100, 110));
            g.DrawEllipse(pen, 3, 3, 18, 18);
            g.FillEllipse(brush, 9, 9, 6, 6);
            g.DrawLine(pen, 12, 3, 12, 8);
            g.DrawLine(pen, 12, 16, 12, 21);
            g.DrawLine(pen, 3, 12, 8, 12);
            g.DrawLine(pen, 16, 12, 21, 12);
            return bitmap;
        }
    }
}
