using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Threading.Tasks;

using Grasshopper.Kernel;

using Rhino.Geometry;

namespace WASPer_3DP.Components._3_Geometry
{
    public sealed class wsp_Ge16_Bitmap_to_Colored_Mesh : GH_Component
    {
        private readonly string _versionTag;
        private static Bitmap _icon;

        public wsp_Ge16_Bitmap_to_Colored_Mesh()
            : base(
                "wsp_Ge16_Bitmap to Colored Mesh",
                "Bitmap to Mesh",
                "Builds vertex-colored quad meshes in World XY from a bitmap list. size_u and " +
                "size_v define its physical dimensions; non-positive values use the bitmap " +
                "pixel dimensions. step downsamples both image axes while always retaining " +
                "the final row and column. Independent bitmaps are processed in parallel while " +
                "input order and Grasshopper branch topology are preserved. Image top maps to " +
                "positive mesh Y so Ge15 can round-trip the orientation.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "2.0_Geometry")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version == null
                ? "v1.0.x"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("AC5DC87E-6B26-4E3A-B2EC-55D172D8B26F");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon =>
            _icon ??= WasperBitmapMeshIcons.BitmapToMesh();

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "bitmap",
                "bitmap",
                "System.Drawing.Bitmap list. Image objects and image file paths are also accepted. " +
                "Each input branch produces one ordered mesh list.",
                GH_ParamAccess.list);
            p.AddNumberParameter(
                "size_u",
                "size_u",
                "Physical mesh width in model units. Values <= 0 use the bitmap width in pixels.",
                GH_ParamAccess.item,
                0.0);
            p.AddNumberParameter(
                "size_v",
                "size_v",
                "Physical mesh height in model units. Values <= 0 use the bitmap height in pixels.",
                GH_ParamAccess.item,
                0.0);
            p.AddIntegerParameter(
                "step",
                "step",
                "Pixel downsampling step. 1 uses every pixel; larger values reduce mesh density while retaining image boundaries.",
                GH_ParamAccess.item,
                1);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddMeshParameter(
                "mesh",
                "mesh",
                "Ordered World XY quad meshes carrying the sampled bitmap colors as per-vertex colors.",
                GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            Message = _versionTag;

            var rawBitmaps = new List<object>();
            double sizeU = 0.0;
            double sizeV = 0.0;
            int step = 1;
            if (!da.GetDataList(0, rawBitmaps) || rawBitmaps.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No bitmap items were supplied.");
                return;
            }
            da.GetData(1, ref sizeU);
            da.GetData(2, ref sizeV);
            da.GetData(3, ref step);

            if (!double.IsFinite(sizeU) || !double.IsFinite(sizeV))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "size_u and size_v must be finite.");
                return;
            }
            if (step < 1)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "step was below 1 and was replaced by 1.");
                step = 1;
            }

            int count = rawBitmaps.Count;
            var meshes = new Mesh[count];
            var errors = new string[count];
            int resolvedStep = step;

            Action<int> convert = index =>
            {
                if (!WasperBitmapMeshTools.TryGetBitmap(
                        rawBitmaps[index],
                        out Bitmap bitmap,
                        out string error))
                {
                    errors[index] = error;
                    return;
                }

                using (bitmap)
                {
                    if (bitmap.Width < 2 || bitmap.Height < 2)
                    {
                        errors[index] = "bitmap must contain at least 2 x 2 pixels to create mesh faces.";
                        return;
                    }

                    double localSizeU = sizeU > 0.0 ? sizeU : bitmap.Width;
                    double localSizeV = sizeV > 0.0 ? sizeV : bitmap.Height;
                    try
                    {
                        Mesh mesh = WasperBitmapMeshTools.BitmapToMesh(
                            bitmap,
                            localSizeU,
                            localSizeV,
                            resolvedStep);
                        if (mesh == null || !mesh.IsValid || mesh.Faces.Count == 0)
                        {
                            errors[index] = "The bitmap could not be converted into a valid colored mesh.";
                            return;
                        }
                        meshes[index] = mesh;
                    }
                    catch (Exception exception)
                    {
                        errors[index] = exception.Message;
                    }
                }
            };

            bool parallel = count >= 4 && Environment.ProcessorCount > 1;
            if (parallel)
                Parallel.For(0, count, convert);
            else
                for (int i = 0; i < count; i++) convert(i);

            int failed = 0;
            for (int i = 0; i < errors.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(errors[i])) continue;
                failed++;
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"Bitmap {i}: {errors[i]}");
            }

            da.SetDataList(0, meshes);
            int made = count - failed;
            Message = $"{_versionTag} | {made}/{count} mesh" + (parallel ? " | parallel" : string.Empty);
        }
    }
}
