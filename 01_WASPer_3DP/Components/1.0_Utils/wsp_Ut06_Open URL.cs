#region Component Description
/*
    Component Name:
        wsp_Ut06_Open URL

    Nickname:
        Open url

    Version:
        v1.0.5

    Category / Subcategory:
        WASPer_3DP / 1.0_Utils

    Description:
        Opens a URL or local file path using the system shell.

    Inputs:
        link : URL or file path to open
        run  : opens once when toggled from false to true

    Output:
        info : open status
*/
#endregion

#region Usings
using System;
using System.Diagnostics;
using System.Drawing;

using Grasshopper.Kernel;
#endregion

namespace WASPer_3DP.Components._1_0_Utils
{
    public sealed class wsp_Ut06_Open_URL : GH_Component
    {
        private const string NAME   = "wsp_Ut06_Open URL";
        private const string NICK   = "Open url";
        private const string CAT    = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "1.0_Utils";

        private readonly string _versionTag;
        private bool _lastRun;

        public wsp_Ut06_Open_URL()
            : base(
                NAME,
                NICK,
                "Opens a URL or local file path using the system shell.",
                CAT,
                SUBCAT)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("79C7C9FE-6F74-4B64-B460-A72FB1670606");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ut06_Open URL.png"))
                        return stream != null ? new Bitmap(stream) : null;
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddTextParameter(
                "link", "link",
                "URL or local file path to open.",
                GH_ParamAccess.item);

            pManager.AddBooleanParameter(
                "run", "run",
                "Open once when toggled from false to true.",
                GH_ParamAccess.item, false);

            pManager[0].Optional = true;
            pManager[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter(
                "info", "info",
                "Open status.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string link = string.Empty;
            bool run = false;

            DA.GetData(0, ref link);
            DA.GetData(1, ref run);
            link = link ?? string.Empty;

            Message = _versionTag;

            if (!run)
            {
                _lastRun = false;
                DA.SetData(0, "Idle. Toggle run true to open the link.");
                return;
            }

            if (_lastRun)
            {
                DA.SetData(0, "Already opened. Toggle run false, then true to open again.");
                return;
            }

            _lastRun = true;

            if (string.IsNullOrWhiteSpace(link))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "link is empty.");
                DA.SetData(0, "ERR: link is empty.");
                Message = "ERR";
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(link) { UseShellExecute = true });
                DA.SetData(0, $"Opened: {link}");
                Message = "opened";
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Could not open link: {ex.Message}");
                DA.SetData(0, $"ERR: {ex.Message}");
                Message = "ERR";
            }
        }
    }
}
