using System;
using System.Drawing;
using System.IO;
using System.Reflection;

using Grasshopper.Kernel;

namespace WASPer_3DP.Components._3_Geometry
{
    public sealed class wsp_Ge14_Image_to_Bitmap : GH_Component
    {
        private readonly string _versionTag;
        private string _cachedPath;
        private DateTime _cachedStamp = DateTime.MinValue;
        private Bitmap _cachedBitmap;
        private static Bitmap _icon;

        public wsp_Ge14_Image_to_Bitmap()
            : base(
                "wsp_Ge14_Image to Bitmap",
                "Img to Bitmap",
                "Loads an image file into a System.Drawing.Bitmap without locking the source file. " +
                "Optional pixel resolution and grayscale conversion are applied to an output copy. " +
                "If only res_u or res_v is positive, the other dimension is calculated from the " +
                "source aspect ratio. The source image is cached until its path or modification " +
                "timestamp changes; reload forces a fresh read.",
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
            new Guid("AE3BBAEC-0585-4C72-9811-05BEF61EC162");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon =>
            _icon ??= WasperBitmapMeshIcons.ImageToBitmap();

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddTextParameter(
                "file_path",
                "file_path",
                "Full path to an image file readable by System.Drawing, such as PNG, JPEG, BMP, GIF, or TIFF.",
                GH_ParamAccess.item);
            p.AddIntegerParameter(
                "res_u",
                "res_u",
                "Optional output width in pixels. Values <= 0 preserve the original width unless res_v alone defines an aspect-ratio-preserving resize.",
                GH_ParamAccess.item,
                0);
            p.AddIntegerParameter(
                "res_v",
                "res_v",
                "Optional output height in pixels. Values <= 0 preserve the original height unless res_u alone defines an aspect-ratio-preserving resize.",
                GH_ParamAccess.item,
                0);
            p.AddBooleanParameter(
                "to_gray",
                "to_gray",
                "Convert RGB to Rec.709 luminance grayscale while preserving alpha.",
                GH_ParamAccess.item,
                false);
            p.AddBooleanParameter(
                "reload",
                "reload",
                "Force the source file to reload even when its path and modification timestamp are unchanged.",
                GH_ParamAccess.item,
                false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "bitmap",
                "bitmap",
                "Loaded and optionally processed System.Drawing.Bitmap.",
                GH_ParamAccess.item);
            p.AddTextParameter(
                "info",
                "info",
                "File, pixel size, color mode, cache/reload state, and modification timestamp.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            Message = _versionTag;

            string filePath = null;
            int resolutionU = 0;
            int resolutionV = 0;
            bool toGray = false;
            bool reload = false;
            if (!da.GetData(0, ref filePath))
                return;
            da.GetData(1, ref resolutionU);
            da.GetData(2, ref resolutionV);
            da.GetData(3, ref toGray);
            da.GetData(4, ref reload);

            if (string.IsNullOrWhiteSpace(filePath))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "file_path is empty.");
                da.SetData(1, "file not found");
                return;
            }

            try
            {
                filePath = Path.GetFullPath(filePath);
            }
            catch (Exception exception)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    $"file_path is invalid: {exception.Message}");
                da.SetData(1, "invalid file path");
                return;
            }

            if (!File.Exists(filePath))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    $"Image file does not exist: {filePath}");
                da.SetData(1, "file not found");
                return;
            }

            DateTime stamp;
            try
            {
                stamp = File.GetLastWriteTimeUtc(filePath);
            }
            catch
            {
                stamp = DateTime.MinValue;
            }

            bool cacheMiss =
                _cachedBitmap == null ||
                !string.Equals(
                    _cachedPath,
                    filePath,
                    StringComparison.OrdinalIgnoreCase) ||
                _cachedStamp != stamp;
            bool reloaded = reload || cacheMiss;

            try
            {
                if (reloaded)
                {
                    Bitmap loaded =
                        WasperBitmapMeshTools.LoadUnlocked(filePath);
                    _cachedBitmap?.Dispose();
                    _cachedBitmap = loaded;
                    _cachedPath = filePath;
                    _cachedStamp = stamp;
                }

                Bitmap work = new Bitmap(_cachedBitmap);
                int targetWidth = resolutionU;
                int targetHeight = resolutionV;
                if (targetWidth > 0 || targetHeight > 0)
                {
                    if (targetWidth <= 0)
                    {
                        targetWidth = Math.Max(
                            1,
                            (int)Math.Round(
                                work.Width *
                                (targetHeight / (double)work.Height)));
                    }
                    else if (targetHeight <= 0)
                    {
                        targetHeight = Math.Max(
                            1,
                            (int)Math.Round(
                                work.Height *
                                (targetWidth / (double)work.Width)));
                    }

                    Bitmap resized =
                        WasperBitmapMeshTools.Resize(
                            work,
                            targetWidth,
                            targetHeight);
                    work.Dispose();
                    work = resized;
                }

                if (toGray)
                {
                    Bitmap grayscale =
                        WasperBitmapMeshTools.ToGrayscale(work);
                    work.Dispose();
                    work = grayscale;
                }

                da.SetData(0, work);
                da.SetData(
                    1,
                    $"file=\"{Path.GetFileName(filePath)}\" | size={work.Width}x{work.Height} px | " +
                    $"mode={(toGray ? "grayscale" : "color")} | source={(reloaded ? "reloaded" : "cached")} | " +
                    $"modified_utc={_cachedStamp:yyyy-MM-dd HH:mm:ss}");
            }
            catch (Exception exception)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    $"Could not load/process image: {exception.Message}");
                da.SetData(1, $"error: {exception.Message}");
            }
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            _cachedBitmap?.Dispose();
            _cachedBitmap = null;
            base.RemovedFromDocument(document);
        }
    }
}
