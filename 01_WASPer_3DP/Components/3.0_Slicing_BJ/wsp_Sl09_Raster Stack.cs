using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;

using Grasshopper.Kernel;

using Rhino.Geometry;

namespace WASPer_3DP.Components._3_0_Slicing_BJ
{
    public sealed class wsp_Sl09_Raster_Stack : GH_Component
    {
        private readonly string _versionTag;
        private static Bitmap _icon;

        public wsp_Sl09_Raster_Stack()
            : base(
                "wsp_Sl09_Raster Stack",
                "Raster Stack",
                "Creates a lightweight binder-jet raster job from a WASPer 3D field. " +
                "No pixels are evaluated here; use Sl10 for selected previews or Sl11 for streaming export.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "3.0_Slicing BJ")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version == null ? "v1.0.x" : $"v{version.Major}.{version.Minor}.{version.Build}";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("916FBAC2-47D8-4EF5-AB76-2020E139B658");
        public override GH_Exposure Exposure => GH_Exposure.primary;
        protected override Bitmap Icon => _icon ??= CreateIcon();

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("field", "field", "WASPer 3D Field. Negative values conventionally describe the interior.", GH_ParamAccess.item);
            p.AddRectangleParameter("print_frame", "frame", "Optional fixed printer-bed rectangle. X/Y define image axes and Z defines stacking.", GH_ParamAccess.item);
            p[1].Optional = true;
            p.AddNumberParameter("layer_h", "layer_h", "Physical layer height in model units.", GH_ParamAccess.item, 1.0);
            p.AddNumberParameter("pixel_size", "pixel", "Target maximum pixel pitch. The gross frame dimensions remain fixed.", GH_ParamAccess.item, 0.2);
            p.AddGenericParameter("binder_settings", "settings", "Optional settings from Sl12. Empty uses binary, threshold 0, black=binder.", GH_ParamAccess.item);
            p[4].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("raster_stack", "stack", "Lazy raster job consumed by Sl10 and Sl11.", GH_ParamAccess.item);
            p.AddIntegerParameter("layer_count", "layers", "Number of physical layers.", GH_ParamAccess.item);
            p.AddRectangleParameter("print_frame", "frame", "Resolved gross printer-bed frame at the first layer centre.", GH_ParamAccess.item);
            p.AddTextParameter("info", "info", "Resolved job dimensions and mapping.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            object rawField = null;
            if (!da.GetData(0, ref rawField)) return;
            WasperField field = WasperRasterData.ExtractField(rawField);
            if (field?.Evaluator == null || !field.Domain.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "field is not a valid WASPer 3D Field.");
                return;
            }

            Rectangle3d requestedFrame = default;
            bool hasFrame = da.GetData(1, ref requestedFrame) && requestedFrame.IsValid;
            double layerHeight = 1.0;
            double pixelSize = 0.2;
            object rawSettings = null;
            da.GetData(2, ref layerHeight);
            da.GetData(3, ref pixelSize);
            da.GetData(4, ref rawSettings);
            WasperBinderSettings settings = WasperRasterData.ExtractSettings(rawSettings) ?? WasperBinderSettings.Default;

            if (!WasperFieldBitmapRasterizer.TryCreateLayout(
                    field, requestedFrame, hasFrame, layerHeight, pixelSize, 0.0,
                    out WasperFieldBitmapLayout layout, out string error))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error);
                da.SetData(3, error);
                return;
            }

            var stack = new WasperRasterStack(field, layout, settings, hasFrame);
            da.SetData(0, new WasperRasterStackGoo(stack));
            da.SetData(1, layout.LayerCount);
            da.SetData(2, layout.LayerFrame(0));
            da.SetData(3, stack.Summary + " | lazy=true");
            Message = $"{_versionTag} | {layout.LayerCount} x {layout.Width}x{layout.Height}";
        }

        private static Bitmap CreateIcon()
        {
            var bitmap = new Bitmap(24, 24);
            using Graphics g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var outline = new Pen(Color.FromArgb(55, 60, 70), 1.4f);
            using var light = new SolidBrush(Color.FromArgb(220, 220, 220));
            using var mid = new SolidBrush(Color.FromArgb(125, 125, 125));
            using var dark = new SolidBrush(Color.FromArgb(45, 45, 45));
            g.FillRectangle(light, 5, 3, 14, 5);
            g.FillRectangle(mid, 4, 9, 16, 5);
            g.FillRectangle(dark, 3, 15, 18, 6);
            g.DrawRectangle(outline, 4.5f, 2.5f, 14, 18);
            return bitmap;
        }
    }
}
