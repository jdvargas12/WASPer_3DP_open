#region Component Description
/*
    Component Name:
        wsp_Ut12_Save View Image

    Nickname:
        Save Image

    Version:
        v1.0.5

    Category / Subcategory:
        WASPer_3DP / 1.0_Utils

    Description:
        Captures a Rhino viewport and saves it as a PNG image.

    Inputs:
        geom          : optional flattened geometry list used for bbox visibility check
        wait_ms       : wait time before capture, default 500 ms
        viewport_name : viewport name. Empty uses active viewport.
        file_dir      : output folder
        file_name     : output base name, .png appended if omitted
        image_res     : "width;height", default 1920;1080
        dpi           : image DPI, default 72
        run           : saves whenever true and the component recomputes

    Outputs:
        out  : status/debug message
        file : saved image path
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

using Grasshopper.Kernel;
using Rhino;
using Rhino.Display;
using Rhino.Geometry;
#endregion

namespace WASPer_3DP.Components._1_0_Utils
{
    public sealed class wsp_Ut12_Save_View_Image : GH_Component
    {
        private const string NAME   = "wsp_Ut12_Save View Image";
        private const string NICK   = "Save Image";
        private const string CAT    = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "1.0_Utils";

        private readonly string _versionTag;
        private string _lastFile = string.Empty;

        public wsp_Ut12_Save_View_Image()
            : base(
                NAME,
                NICK,
                "Saves an image from Rhino viewports, gated by run and optionally checked against flattened geometry.",
                CAT,
                SUBCAT)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("28E81D69-BE06-41F7-9EAB-257C61A41010");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ut10_Save Image from Viewport.png"))
                        return stream != null ? new Bitmap(stream) : null;
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGeometryParameter(
                "geom", "geom",
                "Optional geometry to check against the chosen viewport. Input is flattened.",
                GH_ParamAccess.list);
            pManager[0].DataMapping = GH_DataMapping.Flatten;

            pManager.AddIntegerParameter(
                "wait_ms", "wait",
                "Milliseconds to wait before capture. Default 500.",
                GH_ParamAccess.item, 500);

            pManager.AddTextParameter(
                "viewport_name", "view",
                "Viewport name to capture. Empty uses active viewport.",
                GH_ParamAccess.item, string.Empty);

            pManager.AddTextParameter(
                "file_dir", "dir",
                "Output directory for the PNG file.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "file_name", "name",
                "Output file base name. .png is appended if omitted.",
                GH_ParamAccess.item, "Default_name");

            pManager.AddTextParameter(
                "image_res", "res",
                "Output resolution as width;height. Default 1920;1080.",
                GH_ParamAccess.item, "1920;1080");

            pManager.AddIntegerParameter(
                "dpi", "dpi",
                "Image DPI. Default 72.",
                GH_ParamAccess.item, 72);

            pManager.AddBooleanParameter(
                "run", "run",
                "Save image when true. If geometry or any connected input changes while run stays true, a new image is captured.",
                GH_ParamAccess.item, false);

            for (int i = 0; i < pManager.ParamCount; i++)
                pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter(
                "out", "out",
                "Status/debug message.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "file", "file",
                "Full path of the last successfully saved image.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var geometry = new List<GeometryBase>();
            int waitMs = 500;
            string viewportName = string.Empty;
            string fileDir = string.Empty;
            string fileName = "Default_name";
            string imageRes = "1920;1080";
            int dpi = 72;
            bool run = false;

            DA.GetDataList(0, geometry);
            DA.GetData(1, ref waitMs);
            DA.GetData(2, ref viewportName);
            DA.GetData(3, ref fileDir);
            DA.GetData(4, ref fileName);
            DA.GetData(5, ref imageRes);
            DA.GetData(6, ref dpi);
            DA.GetData(7, ref run);

            if (!run)
            {
                Message = $"{_versionTag} idle";
                DA.SetData(0, "Ready - toggle 'run' to save image.");
                DA.SetData(1, _lastFile);
                return;
            }
            Message = $"{_versionTag} running";

            RhinoDoc doc = RhinoDoc.ActiveDoc;
            if (doc == null)
            {
                SetError(DA, "No active Rhino document.");
                return;
            }

            RhinoView activeView = doc.Views.ActiveView;
            RhinoView view = FindView(doc, viewportName) ?? activeView;
            if (view == null)
            {
                SetError(DA, "No Rhino viewport found.");
                return;
            }

            if (string.IsNullOrWhiteSpace(fileDir))
            {
                SetError(DA, "file_dir is empty.");
                return;
            }

            string fullPath;
            try
            {
                Directory.CreateDirectory(fileDir);
                fullPath = Path.Combine(fileDir, EnsurePngName(fileName));
            }
            catch (Exception ex)
            {
                SetError(DA, "Directory error: " + ex.Message);
                return;
            }

            BoundingBox bbox = GetBoundingBox(geometry);
            if (bbox.IsValid && !view.ActiveViewport.IsVisible(bbox))
            {
                Message = $"{_versionTag} geom not in view";
                DA.SetData(0, "Geometry bounding box is not visible in the selected viewport.");
                DA.SetData(1, _lastFile);
                return;
            }

            ParseResolution(imageRes, out int width, out int height);
            dpi = dpi > 0 ? dpi : 72;
            waitMs = waitMs > 0 ? waitMs : 500;

            try
            {
                int waited = 0;
                const int step = 50;
                while (waited < waitMs)
                {
                    doc.Views.Redraw();
                    Thread.Sleep(step);
                    waited += step;
                }

                DisplayModeDescription mode = view.ActiveViewport.DisplayMode;
                if (mode == null)
                {
                    SetError(DA, "Viewport has no display mode.");
                    return;
                }

                using (Bitmap bitmap = view.CaptureToBitmap(new Size(width, height), mode))
                {
                    if (bitmap == null)
                    {
                        SetError(DA, "Viewport capture failed.");
                        return;
                    }

                    bitmap.SetResolution(dpi, dpi);
                    bitmap.Save(fullPath, ImageFormat.Png);
                }

                _lastFile = fullPath;
                Message = $"{_versionTag} saved";
                DA.SetData(0, "Saved image to: " + fullPath);
                DA.SetData(1, fullPath);
            }
            catch (Exception ex)
            {
                SetError(DA, "Capture error: " + ex.Message);
            }
        }

        private void SetError(IGH_DataAccess DA, string message)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, message);
            Message = $"{_versionTag} error";
            DA.SetData(0, message);
            DA.SetData(1, _lastFile);
        }

        private static RhinoView FindView(RhinoDoc doc, string viewportName)
        {
            if (doc == null || string.IsNullOrWhiteSpace(viewportName))
                return null;

            foreach (RhinoView view in doc.Views)
            {
                if (view?.ActiveViewport?.Name != null &&
                    view.ActiveViewport.Name.Equals(viewportName, StringComparison.OrdinalIgnoreCase))
                    return view;
            }

            return null;
        }

        private static BoundingBox GetBoundingBox(IEnumerable<GeometryBase> geometry)
        {
            BoundingBox bbox = BoundingBox.Unset;
            if (geometry == null) return bbox;

            foreach (GeometryBase item in geometry)
            {
                if (item == null) continue;
                BoundingBox itemBox = item.GetBoundingBox(true);
                if (!itemBox.IsValid) continue;

                if (!bbox.IsValid)
                    bbox = itemBox;
                else
                    bbox.Union(itemBox);
            }

            return bbox;
        }

        private static string EnsurePngName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "Default_name";

            string ext = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(ext))
                return fileName + ".png";

            return Path.ChangeExtension(fileName, ".png");
        }

        private static void ParseResolution(string imageRes, out int width, out int height)
        {
            width = 1920;
            height = 1080;

            if (string.IsNullOrWhiteSpace(imageRes)) return;

            string[] parts = imageRes.Split(';', ',', 'x', 'X');
            if (parts.Length < 2) return;

            if (!int.TryParse(parts[0].Trim(), out width)) width = 1920;
            if (!int.TryParse(parts[1].Trim(), out height)) height = 1080;

            if (width <= 0) width = 1920;
            if (height <= 0) height = 1080;
        }
    }
}
