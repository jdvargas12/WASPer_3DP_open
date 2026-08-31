using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

using Grasshopper.Kernel;

namespace WASPer_3DP.Components._1_0_Utils
{
    /// <summary>
    /// Installs Python packages into Rhino 8's bundled Python 3.9 environment
    /// using the pip3.exe located in ~/.rhinocode/py39-rh8/Scripts/.
    ///
    /// Behaviour:
    ///   • The installed-package list is always refreshed on every solve so you
    ///     can see what is available without pressing run.
    ///   • run = true triggers installation of each library/version pair.
    ///   • override = true forces a reinstall even when the library is already found.
    ///   • Versions are optional — leave the corresponding version slot empty (or
    ///     supply fewer versions than libraries) to install the latest release.
    /// </summary>
    public sealed class wsp_Ut13_Install_Py_Libs : GH_Component
    {
        // ── version ──────────────────────────────────────────────────────────
        private readonly string _versionTag;

        public wsp_Ut13_Install_Py_Libs()
            : base(
                "wsp_Ut13_Install Py Libs",
                "Install Py Libs",
                "Installs Python packages into Rhino 8's bundled Python 3.9 environment.\n" +
                "Always shows the currently installed packages; press run to install.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "1.0_Utils")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v   = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("6C1E5DB6-8306-4B68-8E53-22036B9F0707");

        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ut07_Install Py Libs.png"))
                        return s != null ? new Bitmap(s) : null;
                }
                catch { }
                return null;
            }
        }

        // ── inputs ────────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddTextParameter(
                "libraries", "libs",
                "Python package names to install (one per item).",
                GH_ParamAccess.list);

            p.AddTextParameter(
                "versions", "vers",
                "Version strings for each library (e.g. '1.2.3').\n" +
                "Supply fewer items than libraries to leave the rest unpinned.\n" +
                "Leave an individual item empty to install the latest release.",
                GH_ParamAccess.list);

            p.AddBooleanParameter(
                "override", "override",
                "Force reinstall even when the library is already installed.",
                GH_ParamAccess.item, false);

            p.AddBooleanParameter(
                "run", "run",
                "Install the listed libraries when true.\n" +
                "Connect to a Button for a clean one-shot trigger.",
                GH_ParamAccess.item, false);

            p[0].Optional = true; // libraries
            p[1].Optional = true; // versions
            p[2].Optional = true; // override
        }

        // ── outputs ───────────────────────────────────────────────────────────
        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter(
                "inst_path", "path",
                "Path to the Rhino Python 3.9 Scripts folder used for pip.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "existing_libs", "libs",
                "Packages currently installed in the Rhino Python 3.9 environment.",
                GH_ParamAccess.list);

            p.AddTextParameter(
                "info", "info",
                "Installation log or status message.",
                GH_ParamAccess.item);
        }

        // ── solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── 1. read inputs ──────────────────────────────────────────────
            var libraries = new List<string>();
            var versions  = new List<string>();
            bool overrideInstall = false;
            bool run             = false;

            DA.GetDataList(0, libraries);
            DA.GetDataList(1, versions);
            DA.GetData    (2, ref overrideInstall);
            DA.GetData    (3, ref run);

            // ── 2. resolve scripts folder ───────────────────────────────────
            string userFolder     = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string scriptsFolder  = Path.Combine(userFolder, ".rhinocode", "py39-rh8", "Scripts");
            string pip3           = Path.Combine(scriptsFolder, "pip3.exe");

            DA.SetData(0, scriptsFolder);

            // ── 3. always list installed packages ───────────────────────────
            var existingLibs = ListInstalledPackages(pip3, scriptsFolder);
            DA.SetDataList(1, existingLibs);

            // ── 4. idle ──────────────────────────────────────────────────────
            if (!run)
            {
                Message = _versionTag;
                DA.SetData(2, "idle — run = false");
                return;
            }

            // ── 5. validate ─────────────────────────────────────────────────
            if (!File.Exists(pip3))
            {
                string msg = $"pip3.exe not found at:\n{pip3}\n" +
                             "Check your Rhino 8 / RhinoCode installation.";
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, msg);
                DA.SetData(2, msg);
                return;
            }

            libraries.RemoveAll(string.IsNullOrWhiteSpace);
            if (libraries.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No libraries provided.");
                DA.SetData(2, "No libraries provided.");
                return;
            }

            // ── 6. install each library ──────────────────────────────────────
            var log = new StringBuilder();
            int installed = 0, skipped = 0, failed = 0;

            for (int i = 0; i < libraries.Count; i++)
            {
                string lib = libraries[i].Trim();
                string ver = (i < versions.Count) ? (versions[i] ?? "").Trim() : "";
                string spec = string.IsNullOrEmpty(ver) ? lib : $"{lib}=={ver}";

                // check if already installed (simple name match against pip list output)
                bool alreadyInstalled = existingLibs.Any(l =>
                    l.StartsWith(lib + " ", StringComparison.OrdinalIgnoreCase) ||
                    l.StartsWith(lib + "\t", StringComparison.OrdinalIgnoreCase));

                if (alreadyInstalled && !overrideInstall)
                {
                    log.AppendLine($"[SKIP] {lib} already installed (override = false).");
                    skipped++;
                    continue;
                }

                if (alreadyInstalled && overrideInstall)
                {
                    log.AppendLine($"[UNINSTALL] {lib}…");
                    string uninstallOut = RunPip(pip3, $"uninstall -y {lib}");
                    log.AppendLine(uninstallOut);
                }

                log.AppendLine($"[INSTALL] {spec}…");
                string installOut = RunPip(pip3, $"install {spec}");
                log.AppendLine(installOut);

                if (installOut.Contains("Successfully installed") ||
                    installOut.Contains("already satisfied"))
                    installed++;
                else
                    failed++;
            }

            string summary = $"Done — installed: {installed}, skipped: {skipped}, failed: {failed}";
            log.AppendLine(summary);
            Message = $"{_versionTag} | {summary}";
            DA.SetData(2, log.ToString().TrimEnd());

            // refresh installed list after installation
            DA.SetDataList(1, ListInstalledPackages(pip3, scriptsFolder));
        }

        // ── helpers ───────────────────────────────────────────────────────────

        /// <summary>Runs pip3.exe with the given arguments; returns stdout + stderr.</summary>
        private static string RunPip(string pip3, string args)
        {
            var psi = new ProcessStartInfo(pip3, args)
            {
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true
            };
            var sb = new StringBuilder();
            try
            {
                using (var proc = Process.Start(psi))
                {
                    sb.Append(proc.StandardOutput.ReadToEnd());
                    sb.Append(proc.StandardError.ReadToEnd());
                    proc.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Process error: {ex.Message}");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Returns a list of installed package strings from pip3 list,
        /// filtered to remove the header lines.
        /// </summary>
        private static List<string> ListInstalledPackages(string pip3, string scriptsFolder)
        {
            var result = new List<string>();
            if (!File.Exists(pip3)) return result;

            string raw = RunPip(pip3, "list");
            foreach (var line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("Package") || line.StartsWith("---")) continue;
                if (!string.IsNullOrWhiteSpace(line))
                    result.Add(line);
            }
            return result;
        }
    }
}
