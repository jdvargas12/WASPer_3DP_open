using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Grasshopper.Kernel;

using Rhino.Geometry;

namespace WASPer_3DP.Components._3_0_Slicing_BJ
{
    public sealed class wsp_Sl10_Raster_Layer_Preview : GH_Component
    {
        private readonly string _versionTag;
        private static Bitmap _icon;

        public wsp_Sl10_Raster_Layer_Preview()
            : base(
                "wsp_Sl10_Raster Layer Preview",
                "Raster Preview",
                "Generates only the requested layer bitmaps from a WASPer raster stack. " +
                "Use a small layer-index list for inspection; Sl11 is the production exporter.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "3.0_Slicing BJ")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version == null ? "v1.0.x" : $"v{version.Major}.{version.Minor}.{version.Build}";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("06C58A09-8382-452C-8B0B-9C4FA2F59294");
        public override GH_Exposure Exposure => GH_Exposure.primary;
        protected override Bitmap Icon => _icon ??= CreateIcon();

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter("raster_stack", "stack", "Raster job from Sl09.", GH_ParamAccess.item);
            p.AddIntegerParameter("layer_index", "layer", "Zero-based layer indices to preview. Empty previews layer 0.", GH_ParamAccess.list);
            p[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter("bitmap", "bitmap", "Requested 8-bit grayscale preview bitmaps.", GH_ParamAccess.list);
            p.AddPlaneParameter("layer_plane", "planes", "Physical planes at the selected layer centres.", GH_ParamAccess.list);
            p.AddRectangleParameter("frame", "frames", "Gross physical frames for the selected layers.", GH_ParamAccess.list);
            p.AddTextParameter("info", "info", "Preview indices, dimensions, and invalid sample count.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            object rawStack = null;
            if (!da.GetData(0, ref rawStack)) return;
            WasperRasterStack stack = WasperRasterData.ExtractStack(rawStack);
            if (stack == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "stack is not a valid WASPer Raster Stack.");
                return;
            }

            var requested = new List<int>();
            da.GetDataList(1, requested);
            if (requested.Count == 0) requested.Add(0);
            int[] indices = requested
                .Select(i => Math.Max(0, Math.Min(i, stack.Layout.LayerCount - 1)))
                .Distinct()
                .ToArray();
            if (!requested.SequenceEqual(indices))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Layer indices were clamped and duplicate indices were removed.");

            var bitmaps = new Bitmap[indices.Length];
            var invalid = new int[indices.Length];
            var errors = new string[indices.Length];
            Action<int> generate = i =>
            {
                try
                {
                    WasperBinderSettings s = stack.Settings;
                    bitmaps[i] = WasperFieldBitmapRasterizer.RasterizeLayer(
                        stack.Field, stack.Layout, indices[i], s.Mode, s.Threshold,
                        s.FieldRange, s.Invert, out invalid[i], false);
                }
                catch (Exception exception) { errors[i] = exception.Message; }
            };

            bool parallel = indices.Length >= 4 && Environment.ProcessorCount > 1;
            if (parallel)
                Parallel.For(0, indices.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) }, generate);
            else
                for (int i = 0; i < indices.Length; i++) generate(i);

            for (int i = 0; i < errors.Length; i++)
                if (!string.IsNullOrWhiteSpace(errors[i]))
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Layer {indices[i]}: {errors[i]}");

            da.SetDataList(0, bitmaps);
            da.SetDataList(1, indices.Select(stack.Layout.LayerPlane));
            da.SetDataList(2, indices.Select(stack.Layout.LayerFrame));
            da.SetData(3,
                $"preview={string.Join(",", indices)} | {stack.Layout.Width}x{stack.Layout.Height} px | " +
                $"invalid_samples={invalid.Sum():N0}" + (parallel ? " | parallel" : string.Empty));
            Message = $"{_versionTag} | {indices.Length} layer" + (indices.Length == 1 ? string.Empty : "s");
        }

        private static Bitmap CreateIcon()
        {
            var bitmap = new Bitmap(24, 24);
            using Graphics g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var dark = new SolidBrush(Color.FromArgb(45, 45, 45));
            using var light = new SolidBrush(Color.FromArgb(225, 225, 225));
            using var pen = new Pen(Color.FromArgb(55, 60, 70), 1.5f);
            g.FillRectangle(light, 3, 4, 18, 16);
            g.FillPie(dark, 6, 7, 12, 10, 180, 180);
            g.FillEllipse(dark, 10, 9, 4, 4);
            g.DrawRectangle(pen, 3.5f, 4.5f, 17, 15);
            return bitmap;
        }
    }
}
