using System;
using System.Drawing;
using System.Reflection;

using Grasshopper.Kernel;

using Rhino.Geometry;

namespace WASPer_3DP.Components._3_Geometry
{
    public sealed class wsp_Ge15_Colored_Mesh_to_Bitmap : GH_Component
    {
        private readonly string _versionTag;
        private static Bitmap _icon;

        public wsp_Ge15_Colored_Mesh_to_Bitmap()
            : base(
                "wsp_Ge15_Colored Mesh to Bitmap",
                "Mesh to Bitmap",
                "Projects a vertex-colored mesh normally onto ref_plane and rasterizes its " +
                "interpolated colors into a bitmap. size_u and size_v define the plane domain; " +
                "non-positive values auto-fit the projected mesh. Image top corresponds to " +
                "positive ref_plane Y, matching Ge16 Bitmap to Colored Mesh.",
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
            new Guid("14F68A92-74D3-4347-B261-C3B127CAB34E");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon =>
            _icon ??= WasperBitmapMeshIcons.MeshToBitmap();

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddMeshParameter(
                "mesh_col",
                "mesh_col",
                "Mesh containing exactly one vertex color per vertex.",
                GH_ParamAccess.item);
            p.AddPlaneParameter(
                "ref_plane",
                "ref_plane",
                "Projection/domain plane. Image U follows plane X and image bottom-to-top follows plane Y.",
                GH_ParamAccess.item,
                Plane.WorldXY);
            p.AddNumberParameter(
                "size_u",
                "size_u",
                "Bitmap domain width in model units along ref_plane X. Values <= 0 auto-fit the projected mesh.",
                GH_ParamAccess.item,
                0.0);
            p.AddNumberParameter(
                "size_v",
                "size_v",
                "Bitmap domain height in model units along ref_plane Y. Values <= 0 auto-fit the projected mesh.",
                GH_ParamAccess.item,
                0.0);
            p.AddIntegerParameter(
                "res_u",
                "res_u",
                "Bitmap width in pixels. Values below 2 use 512.",
                GH_ParamAccess.item,
                512);
            p.AddIntegerParameter(
                "res_v",
                "res_v",
                "Bitmap height in pixels. Values below 2 use 512.",
                GH_ParamAccess.item,
                512);
            p.AddColourParameter(
                "bg_color",
                "bg_color",
                "Background color, including alpha, for pixels whose projection line misses the mesh.",
                GH_ParamAccess.item,
                Color.Transparent);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "bitmap",
                "bitmap",
                "Rasterized System.Drawing.Bitmap with interpolated mesh colors and the requested background.",
                GH_ParamAccess.item);
            p.AddTextParameter(
                "info",
                "info",
                "Resolution, resolved physical domain, resolved plane origin, and mesh-hit pixel count.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            Message = _versionTag;

            Mesh mesh = null;
            Plane referencePlane = Plane.WorldXY;
            double sizeU = 0.0;
            double sizeV = 0.0;
            int resolutionU = 512;
            int resolutionV = 512;
            Color background = Color.Transparent;
            if (!da.GetData(0, ref mesh))
                return;
            da.GetData(1, ref referencePlane);
            da.GetData(2, ref sizeU);
            da.GetData(3, ref sizeV);
            da.GetData(4, ref resolutionU);
            da.GetData(5, ref resolutionV);
            da.GetData(6, ref background);

            if (resolutionU < 2 || resolutionV < 2)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "res_u/res_v values below 2 were replaced by 512.");
                if (resolutionU < 2) resolutionU = 512;
                if (resolutionV < 2) resolutionV = 512;
            }

            try
            {
                if (!WasperBitmapMeshTools.TryMeshToBitmap(
                        mesh,
                        referencePlane,
                        ref sizeU,
                        ref sizeV,
                        resolutionU,
                        resolutionV,
                        background,
                        out Bitmap bitmap,
                        out Plane resolvedPlane,
                        out int hitPixels,
                        out string error))
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error);
                    da.SetData(1, error);
                    return;
                }

                if (hitPixels == 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        "No bitmap projection rays intersected mesh_col; the output contains only bg_color.");
                }

                da.SetData(0, bitmap);
                da.SetData(
                    1,
                    $"res={resolutionU}x{resolutionV} px | size={sizeU:0.###}x{sizeV:0.###} model units | " +
                    $"plane_origin=({resolvedPlane.OriginX:0.###},{resolvedPlane.OriginY:0.###},{resolvedPlane.OriginZ:0.###}) | " +
                    $"mesh_pixels={hitPixels}/{resolutionU * resolutionV}");
            }
            catch (Exception exception)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    $"Mesh rasterization failed: {exception.Message}");
                da.SetData(1, $"error: {exception.Message}");
            }
        }
    }
}
