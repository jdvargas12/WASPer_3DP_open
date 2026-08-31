#region Component Description
/*
    Component Name:
        wsp_Ut04_Write CSV

    Nickname:
        Write to CSV

    Version:
        v1.0.5

    Category / Subcategory:
        WASPer_3DP / 1.0_Utils

    Description:
        Writes a Grasshopper data tree to a CSV file. Each branch is treated as
        one CSV column, and headers must match the number of branches.

    Inputs:
        data      : DataTree. Each branch is one CSV column.
        headers   : column headers. Count must match branch count.
        file_name : output CSV file name, with or without .csv
        file_path : output folder. Defaults to the GH document folder if empty.
        reset     : true clears the file
        write     : true appends headers and data rows

    Output:
        file      : full CSV path
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
#endregion

namespace WASPer_3DP.Components._1_0_Utils
{
    public sealed class wsp_Ut04_Write_CSV : GH_Component
    {
        private const string NAME   = "wsp_Ut04_Write CSV";
        private const string NICK   = "Write CSV";
        private const string CAT    = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "1.0_Utils";

        private readonly string _versionTag;

        public wsp_Ut04_Write_CSV()
            : base(
                NAME,
                NICK,
                "Writes a data tree to CSV. Each branch becomes one column.",
                CAT,
                SUBCAT)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("4A70E0CB-4E0E-4A7C-B364-DAC00DDC0202");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ut02_Write to CSV.png"))
                        return stream != null ? new Bitmap(stream) : null;
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "data", "data",
                "DataTree to write. Each branch is one CSV column.",
                GH_ParamAccess.tree);

            pManager.AddTextParameter(
                "headers", "headers",
                "Column headers. Count must match data branch count.",
                GH_ParamAccess.list);

            pManager.AddTextParameter(
                "file_name", "file_name",
                "CSV file name, with or without .csv. Default is default_name.",
                GH_ParamAccess.item, "default_name");

            pManager.AddTextParameter(
                "file_path", "file_path",
                "Output folder. If empty, uses the Grasshopper document folder or current working directory.",
                GH_ParamAccess.item);

            pManager.AddBooleanParameter(
                "reset", "reset",
                "True clears the CSV file content.",
                GH_ParamAccess.item, false);

            pManager.AddBooleanParameter(
                "write", "write",
                "True appends headers and data rows to the CSV.",
                GH_ParamAccess.item, false);

            for (int i = 0; i < pManager.ParamCount; i++)
                pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter(
                "file", "file",
                "Full path to the CSV file.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "info", "info",
                "Write status.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_Structure<IGH_Goo> data;
            var headers = new List<string>();
            string fileName = "default_name";
            string filePath = string.Empty;
            bool reset = false;
            bool write = false;

            if (!DA.GetDataTree(0, out data) || data == null)
                data = new GH_Structure<IGH_Goo>();
            DA.GetDataList(1, headers);
            DA.GetData(2, ref fileName);
            DA.GetData(3, ref filePath);
            DA.GetData(4, ref reset);
            DA.GetData(5, ref write);

            string fullPath = ResolveCsvPath(fileName, filePath, OnPingDocument()?.FilePath);
            DA.SetData(0, fullPath);
            Message = _versionTag;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Could not create output folder: " + ex.Message);
                DA.SetData(1, "ERR: " + ex.Message);
                Message = "ERR";
                return;
            }

            if (reset)
            {
                File.WriteAllText(fullPath, string.Empty, Encoding.UTF8);
                DA.SetData(1, "Reset CSV file: " + fullPath);
                Message = "reset";
                if (!write) return;
            }

            if (!write)
            {
                DA.SetData(1, "Ready. Toggle write to append data.");
                return;
            }

            int branchCount = data.Branches.Count;
            if (headers.Count != branchCount)
            {
                string msg = "Number of headers must match number of branches in data tree.";
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, msg);
                DA.SetData(1, msg);
                Message = "ERR";
                return;
            }

            var columns = new List<List<string>>();
            int maxLength = 0;
            for (int i = 0; i < branchCount; i++)
            {
                var column = data.Branches[i]
                    .Select(ToCsvText)
                    .ToList();
                columns.Add(column);
                maxLength = Math.Max(maxLength, column.Count);
            }

            foreach (var column in columns)
                while (column.Count < maxLength)
                    column.Add(string.Empty);

            var lines = new List<string>();
            lines.Add(string.Join(",", headers.Select(EscapeCsv)));

            for (int row = 0; row < maxLength; row++)
            {
                var cells = new string[branchCount];
                for (int col = 0; col < branchCount; col++)
                    cells[col] = EscapeCsv(columns[col][row]);
                lines.Add(string.Join(",", cells));
            }

            File.AppendAllLines(fullPath, lines, Encoding.UTF8);
            DA.SetData(1, $"Appended {maxLength} data row(s) + headers to: {fullPath}");
            Message = "written";
        }

        private static string ResolveCsvPath(string fileName, string filePath, string ghDocumentPath)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "default_name";
            if (!fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                fileName += ".csv";

            if (string.IsNullOrWhiteSpace(filePath))
            {
                if (!string.IsNullOrWhiteSpace(ghDocumentPath))
                    filePath = Path.GetDirectoryName(ghDocumentPath);
                if (string.IsNullOrWhiteSpace(filePath))
                    filePath = Environment.CurrentDirectory;
            }

            return Path.Combine(filePath, fileName);
        }

        private static string ToCsvText(IGH_Goo goo)
        {
            if (goo == null) return string.Empty;
            object value = goo.ScriptVariable();
            return value?.ToString() ?? goo.ToString();
        }

        private static string EscapeCsv(string value)
        {
            value = value ?? string.Empty;
            bool quote = value.Contains(",") || value.Contains("\"") ||
                         value.Contains("\r") || value.Contains("\n");
            value = value.Replace("\"", "\"\"");
            return quote ? "\"" + value + "\"" : value;
        }
    }
}
