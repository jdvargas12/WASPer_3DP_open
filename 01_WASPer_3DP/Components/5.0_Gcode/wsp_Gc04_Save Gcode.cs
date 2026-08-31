#region Component Description
/*
Component: wsp_Gc04_Save Gcode
Nickname: Save Gcode
Category: WASPer_3DP
SubCategory: 5.0_Gcode
Version:
    Uses the compiled assembly version in the component message via _versionTag.

cENERAL DESCRIPTION
Saves one or more c-code sets to disk from a Grasshopper data tree.
Each branch of the input c-code tree is saved as a separate file.

If the input tree contains:
- One branch: the file is saved using the provided file_name.
- More than one branch: each file gets a numeric suffix:
    file_name_1.gcode
    file_name_2.gcode
    file_name_3.gcode

INPUTS
0) g_code : DataTree<string>
   c-code lines organized by branch. Each branch is saved as one file.

1) file_path : string
   Directory where the file(s) will be saved.

2) file_name : string
   Base file name without extension. Defaults to "DefaultFileName".

3) file_extension : string
   Desired extension, such as ".gcode". Defaults to ".gcode".

4) save : bool
   Triggers the save operation.

5) override : bool
   If true, existing files with the same name are overwritten.

OUTPUTS
0) out : string
   Summary of saved files, skipped files, warnings, or errors.
*/
#endregion

#region Usings
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
#endregion

namespace WASPer_3DP.Components._5_0_Gcode
{
    public sealed class wsp_Gc04_Save_Gcode : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Gc04_Save_Gcode()
            : base(
                "wsp_Gc04_Save Gcode",
                "Save Gcode",
                "Saves each branch of a c-code text tree as one .gcode file.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "5.0_Gcode")
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("E12C3E67-56F0-4B63-B16E-9B973A8D1F41");

        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var stream = asm.GetManifestResourceStream("WASPer_3DP.Resources.Icons.03_SaveDcode.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddTextParameter(
                "g_code",
                "g_code",
                "c-code lines organized by branch. Each branch is saved as one file.",
                GH_ParamAccess.tree);

            pManager.AddTextParameter(
                "file_path",
                "path",
                "Directory path where the file(s) will be saved.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "file_name",
                "name",
                "Base file name without extension.",
                GH_ParamAccess.item,
                "DefaultFileName");

            pManager.AddTextParameter(
                "file_extension",
                "ext",
                "Desired file extension. Example: .gcode",
                GH_ParamAccess.item,
                ".gcode");

            pManager.AddBooleanParameter(
                "save",
                "save",
                "Set to true to save the c-code file(s).",
                GH_ParamAccess.item,
                false);

            pManager.AddBooleanParameter(
                "override",
                "override",
                "If true, existing files with the same name are overwritten.",
                GH_ParamAccess.item,
                false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter(
                "out",
                "out",
                "Summary of saved files, skipped files, warnings, or errors.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            GH_Structure<GH_String> gcodeTree;
            if (!DA.GetDataTree(0, out gcodeTree))
            {
                DA.SetData(0, "Warning: No c-code branches found. Nothing was saved.");
                return;
            }

            string filePath = null;
            string fileName = "DefaultFileName";
            string fileExtension = ".gcode";
            bool save = false;
            bool overwrite = false;

            DA.GetData(1, ref filePath);
            DA.GetData(2, ref fileName);
            DA.GetData(3, ref fileExtension);
            DA.GetData(4, ref save);
            DA.GetData(5, ref overwrite);

            if (!save)
            {
                DA.SetData(0, "Ready. Set save to true to write c-code file(s).");
                return;
            }

            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !Directory.Exists(filePath))
                {
                    DA.SetData(0, "Error: The specified folder does not exist.");
                    return;
                }

                fileName = SanitizeBaseName(fileName);
                fileExtension = EnsureExtension(fileExtension);

                var branches = TreeToBranchLists(gcodeTree);
                if (branches.Count == 0)
                {
                    DA.SetData(0, "Warning: No c-code branches found. Nothing was saved.");
                    return;
                }

                bool multipleFiles = branches.Count > 1;
                var messages = new StringBuilder();

                for (int i = 0; i < branches.Count; i++)
                {
                    string currentName = multipleFiles
                        ? $"{fileName}_{i + 1}"
                        : fileName;

                    string fullFilePath = Path.Combine(filePath, currentName + fileExtension);

                    if (File.Exists(fullFilePath) && !overwrite)
                    {
                        AppendLine(messages, "Skipped existing file: " + fullFilePath);
                        continue;
                    }

                    try
                    {
                        File.WriteAllLines(fullFilePath, branches[i]);
                        AppendLine(messages, "Saved: " + fullFilePath);
                    }
                    catch (Exception writeError)
                    {
                        AppendLine(messages, $"Error writing '{fullFilePath}': {writeError.Message}");
                    }
                }

                DA.SetData(0, messages.ToString());
            }
            catch (Exception ex)
            {
                DA.SetData(0, "Error: " + ex.Message);
            }
        }

        private static string EnsureExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
                return ".gcode";

            extension = extension.Trim();
            return extension.StartsWith(".", StringComparison.Ordinal)
                ? extension
                : "." + extension;
        }

        private static string SanitizeBaseName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return "DefaultFileName";

            fileName = fileName.Trim();
            foreach (char invalid in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(invalid, '_');

            return string.IsNullOrWhiteSpace(fileName)
                ? "DefaultFileName"
                : fileName;
        }

        private static List<List<string>> TreeToBranchLists(GH_Structure<GH_String> tree)
        {
            var branchLists = new List<List<string>>();
            if (tree == null || tree.PathCount == 0)
                return branchLists;

            for (int i = 0; i < tree.PathCount; i++)
            {
                IList branch = tree.get_Branch(tree.Paths[i]);
                var lines = new List<string>();

                if (branch != null)
                {
                    foreach (object item in branch)
                    {
                        if (item == null) continue;

                        var ghString = item as GH_String;
                        lines.Add(ghString != null ? ghString.Value : item.ToString());
                    }
                }

                branchLists.Add(lines);
            }

            return branchLists;
        }

        private static void AppendLine(StringBuilder builder, string line)
        {
            if (builder.Length > 0)
                builder.AppendLine();

            builder.Append(line);
        }
    }
}
