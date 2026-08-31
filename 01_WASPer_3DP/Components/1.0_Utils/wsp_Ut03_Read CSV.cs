#region Component Description
/*
    Component Name:
        wsp_Ut03_Read CSV

    Nickname:
        Read CSV

    Version:
        v1.0.5

    Category / Subcategory:
        WASPer_3DP / 1.0_Utils

    Description:
        Reads a CSV file and outputs either a selected row or selected column,
        plus the full CSV as a DataTree.

    Inputs:
        file_name         : CSV file name, with or without .csv extension
        file_path         : folder path containing the CSV file
        row_column        : 1-based row/column index
        switch_row_column : true reads a column; false reads a row
        refresh           : dummy input to force recomputation

    Outputs:
        data      : selected row or column values
        data_tree : full CSV, branched by columns when switch_row_column is true,
                    otherwise branched by rows
        info      : read status and dimensions
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
#endregion

namespace WASPer_3DP.Components._1_0_Utils
{
    public sealed class wsp_Ut03_Read_CSV : GH_Component
    {
        private const string NAME   = "wsp_Ut03_Read CSV";
        private const string NICK   = "Read CSV";
        private const string CAT    = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "1.0_Utils";

        private readonly string _versionTag;

        public wsp_Ut03_Read_CSV()
            : base(
                NAME,
                NICK,
                "Reads a CSV file and outputs a selected row/column plus the full CSV as a DataTree.",
                CAT,
                SUBCAT)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("FA338C90-C497-4E31-8EB1-A190C75D0303");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ut03_Read from CSV.png"))
                        return stream != null ? new Bitmap(stream) : null;
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddTextParameter(
                "file_name", "file_name",
                "Name of the CSV file. The .csv extension can be included or omitted.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "file_path", "file_path",
                "Folder path containing the CSV file.",
                GH_ParamAccess.item);

            pManager.AddIntegerParameter(
                "row_column", "idx",
                "1-based row or column index to read. Minimum is 1.",
                GH_ParamAccess.item, 1);

            pManager.AddBooleanParameter(
                "switch_row_column", "column",
                "True reads a column. False reads a row.",
                GH_ParamAccess.item, true);

            pManager.AddBooleanParameter(
                "refresh", "refresh",
                "Dummy input used to force recomputation when toggled.",
                GH_ParamAccess.item, false);

            pManager[0].Optional = true;
            pManager[1].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
            pManager[4].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter(
                "data", "data",
                "Selected row or column values.",
                GH_ParamAccess.list);

            pManager.AddTextParameter(
                "data_tree", "tree",
                "Full CSV as a DataTree. Branches are columns when switch_row_column is true; otherwise rows.",
                GH_ParamAccess.tree);

            pManager.AddTextParameter(
                "info", "info",
                "CSV read status, dimensions, and selected index.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            string fileName = string.Empty;
            string filePath = string.Empty;
            int rowColumn = 1;
            bool switchRowColumn = true;
            bool refresh = false;

            DA.GetData(0, ref fileName);
            DA.GetData(1, ref filePath);
            DA.GetData(2, ref rowColumn);
            DA.GetData(3, ref switchRowColumn);
            DA.GetData(4, ref refresh);

            var selected = new List<string>();
            var tree = new DataTree<string>();

            fileName = fileName ?? string.Empty;
            filePath = filePath ?? string.Empty;

            if (string.IsNullOrWhiteSpace(fileName))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "file_name is empty.");
                DA.SetDataList(0, selected);
                DA.SetDataTree(1, tree);
                DA.SetData(2, "No CSV file name provided.");
                Message = _versionTag;
                return;
            }

            if (!fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                fileName += ".csv";

            string fullPath = Path.Combine(filePath, fileName);
            if (!File.Exists(fullPath))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Could not read file: {fullPath}");
                DA.SetDataList(0, selected);
                DA.SetDataTree(1, tree);
                DA.SetData(2, $"Could not read file: {fullPath}");
                Message = "ERR";
                return;
            }

            List<List<string>> csvData;
            try
            {
                csvData = ReadCsv(fullPath);
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Could not read CSV: {ex.Message}");
                DA.SetDataList(0, selected);
                DA.SetDataTree(1, tree);
                DA.SetData(2, $"Could not read CSV: {ex.Message}");
                Message = "ERR";
                return;
            }

            int rowCount = csvData.Count;
            int columnCount = 0;
            foreach (var row in csvData)
                columnCount = Math.Max(columnCount, row?.Count ?? 0);

            int index = Math.Max(rowColumn, 1) - 1;

            if (switchRowColumn)
            {
                for (int r = 0; r < rowCount; r++)
                    if (index < csvData[r].Count)
                        selected.Add(csvData[r][index]);

                for (int c = 0; c < columnCount; c++)
                {
                    var path = new GH_Path(c);
                    for (int r = 0; r < rowCount; r++)
                        if (c < csvData[r].Count)
                            tree.Add(csvData[r][c], path);
                }
            }
            else
            {
                if (index < rowCount)
                    selected.AddRange(csvData[index]);

                for (int r = 0; r < rowCount; r++)
                {
                    var path = new GH_Path(r);
                    foreach (string item in csvData[r])
                        tree.Add(item, path);
                }
            }

            string mode = switchRowColumn ? "column" : "row";
            string info =
                $"{NAME}  {_versionTag}\n" +
                $"file        : {fullPath}\n" +
                $"rows        : {rowCount}\n" +
                $"columns     : {columnCount}\n" +
                $"selected    : {mode} {index + 1}\n" +
                $"items       : {selected.Count}\n" +
                $"refresh     : {refresh}";

            DA.SetDataList(0, selected);
            DA.SetDataTree(1, tree);
            DA.SetData(2, info);
            Message = _versionTag;
        }

        private static List<List<string>> ReadCsv(string path)
        {
            var rows = new List<List<string>>();
            using (var reader = new StreamReader(path, Encoding.UTF8, true))
            {
                var row = new List<string>();
                var cell = new StringBuilder();
                bool inQuotes = false;

                while (reader.Peek() >= 0)
                {
                    char ch = (char)reader.Read();
                    if (ch == '"')
                    {
                        if (inQuotes && reader.Peek() == '"')
                        {
                            reader.Read();
                            cell.Append('"');
                        }
                        else
                        {
                            inQuotes = !inQuotes;
                        }
                    }
                    else if (ch == ',' && !inQuotes)
                    {
                        row.Add(cell.ToString());
                        cell.Clear();
                    }
                    else if ((ch == '\n' || ch == '\r') && !inQuotes)
                    {
                        if (ch == '\r' && reader.Peek() == '\n')
                            reader.Read();

                        row.Add(cell.ToString());
                        cell.Clear();
                        rows.Add(row);
                        row = new List<string>();
                    }
                    else
                    {
                        cell.Append(ch);
                    }
                }

                if (cell.Length > 0 || row.Count > 0)
                {
                    row.Add(cell.ToString());
                    rows.Add(row);
                }
            }

            return rows;
        }
    }
}
