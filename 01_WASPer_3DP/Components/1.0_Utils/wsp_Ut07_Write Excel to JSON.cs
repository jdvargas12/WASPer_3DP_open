using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using Newtonsoft.Json;
using Grasshopper;
using Grasshopper.Kernel;

namespace WASPer_3DP.Components._1_0_Utils
{
    /// <summary>
    /// Converts a structured Excel material list to a JSON file.
    /// Behaviour:
    ///   • <excel_path> is mandatory.
    ///   • If <file_path> is empty  ? JSON is written next to the Excel file.
    ///   • If <file_name> is empty ? "mat_lib.json" is used.
    ///   • Optional <ws_name> lets the user pick a worksheet; if empty the first sheet is used.
    ///   • Output returns only the written file path.  Errors are shown as runtime messages.
    /// </summary>
    public class wsp_Ut07_Write_Excel_to_JSON : GH_Component
    {
        private readonly string _versionTag;
        private bool _lastRun;
        private string _lastJsonPath = "";

        public wsp_Ut07_Write_Excel_to_JSON()
          : base("wsp_Ut07_Write Excel to JSON",
                 "xls_to_json",
                 "Exports a structured material Excel sheet to a JSON file.",
                 global::WASPer_3DP.WASPerPalette.DesignFabrication,
                 "1.0_Utils")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v   = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("EFD22885-64F7-4E70-B89A-527E5C32BCE3");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ut09_Write_Excel to Json.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        #region IO
        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            // mandatory ----------------------------------------------------------
            p.AddTextParameter("excel_path", "excel_path", "Path to the Excel (.xlsx) file.", GH_ParamAccess.item);

            // optional -----------------------------------------------------------
            p.AddTextParameter("ws_name", "ws_name", "Optional worksheet name. If empty, the first worksheet is used.", GH_ParamAccess.item);
            p.AddTextParameter("file_name", "file_name", "Optional JSON file name (default: mat_lib.json).", GH_ParamAccess.item);
            p.AddTextParameter("file_path", "file_path", "Optional target folder. If empty, uses the Excel file's folder.", GH_ParamAccess.item);

            // mark strings as optional so missing wires don't throw warnings
            p[1].Optional = true; // ws_name
            p[2].Optional = true; // file_name
            p[3].Optional = true; // file_path

            // booleans -----------------------------------------------------------
            p.AddBooleanParameter("override", "override", "If false and file exists, export is cancelled.", GH_ParamAccess.item, false);
            p.AddBooleanParameter("run", "run", "Set true to execute the export.", GH_ParamAccess.item, false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter("json_file", "json_file", "Full path to the exported JSON file.", GH_ParamAccess.item);
        }
        #endregion

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // --- 1. Read inputs --------------------------------------------------
            string excelPath = null;
            string wsName = null;
            string fileName = null;
            string filePath = null;
            bool overwrite = false;
            bool run = false;

            if (!DA.GetData(0, ref excelPath)) excelPath = null;
            DA.GetData(1, ref wsName);
            DA.GetData(2, ref fileName);
            DA.GetData(3, ref filePath);
            DA.GetData(4, ref overwrite);
            DA.GetData(5, ref run);

            // default output is empty
            DA.SetData(0, "");
            if (!run)
            {
                _lastRun = false;
                Message = _versionTag;
                return;
            }

            if (_lastRun)
            {
                DA.SetData(0, _lastJsonPath);
                Message = "done";
                return;
            }

            // --- 2. Validate & defaults ----------------------------------------
            if (string.IsNullOrWhiteSpace(excelPath) || !File.Exists(excelPath))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Excel file path is invalid or missing.");
                return;
            }

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "mat_lib.json";
            else if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                fileName += ".json";

            if (string.IsNullOrWhiteSpace(filePath))
                filePath = Path.GetDirectoryName(excelPath);

            if (string.IsNullOrWhiteSpace(filePath) || !Directory.Exists(filePath))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid output folder path.");
                return;
            }

            string fullPath = Path.Combine(filePath, fileName);
            if (File.Exists(fullPath) && !overwrite)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"File already exists: {fullPath}. Enable 'override' input to overwrite.");
                return;
            }

            // --- 3. Read Excel & write JSON ------------------------------------
            try
            {
                var totalWatch = Stopwatch.StartNew();
                var stepWatch = Stopwatch.StartNew();
                long loadMs, readMs, jsonMs, writeMs;
                int rowCount = 0;
                int materialCount = 0;

                using (var workbook = new XLWorkbook(excelPath))
                {
                    stepWatch.Stop();
                    loadMs = stepWatch.ElapsedMilliseconds;

                    IXLWorksheet sheet;

                    // worksheet selection
                    if (string.IsNullOrWhiteSpace(wsName))
                        sheet = workbook.Worksheets.First();
                    else if (!workbook.TryGetWorksheet(wsName, out sheet))
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Worksheet '{wsName}' not found.");
                        return;
                    }

                    stepWatch.Restart();

                    // header row
                    var headerRow = sheet.FirstRowUsed();
                    var lastRow = sheet.LastRowUsed();
                    var lastColumn = sheet.LastColumnUsed();

                    if (headerRow == null || lastRow == null || lastColumn == null)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Worksheet has no used cells.");
                        return;
                    }

                    _lastRun = true;

                    int headerRowNumber = headerRow.RowNumber();
                    int lastRowNumber = lastRow.RowNumber();
                    int lastColumnNumber = lastColumn.ColumnNumber();

                    var headers = new List<string>();
                    for (int col = 1; col <= lastColumnNumber; col++)
                        headers.Add(headerRow.Cell(col).GetString().Trim());

                    var materials = new List<Dictionary<string, object>>();
                    for (int rowNumber = headerRowNumber + 2; rowNumber <= lastRowNumber; rowNumber++) // skip header + units row
                    {
                        var row = sheet.Row(rowNumber);
                        rowCount++;
                        var entry = new Dictionary<string, object>();
                        for (int i = 0; i < headers.Count; i++)
                        {
                            if (string.IsNullOrWhiteSpace(headers[i])) continue;
                            var v = row.Cell(i + 1).Value;
                            if (v.IsBlank || string.IsNullOrWhiteSpace(v.ToString())) continue;
                            entry[headers[i]] = double.TryParse(v.ToString(), out double num) ? (object)num : v.ToString();
                        }
                        if (entry.ContainsKey("Material_Name"))
                        {
                            materials.Add(entry);
                            materialCount++;
                        }
                    }

                    stepWatch.Stop();
                    readMs = stepWatch.ElapsedMilliseconds;

                    stepWatch.Restart();
                    string json = JsonConvert.SerializeObject(materials, Formatting.Indented);
                    stepWatch.Stop();
                    jsonMs = stepWatch.ElapsedMilliseconds;

                    stepWatch.Restart();
                    File.WriteAllText(fullPath, json);
                    stepWatch.Stop();
                    writeMs = stepWatch.ElapsedMilliseconds;

                    totalWatch.Stop();

                    _lastJsonPath = fullPath;
                    Message = $"{totalWatch.Elapsed.TotalSeconds:0.0}s";
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Remark,
                        $"Excel export complete. Rows scanned: {rowCount}, materials: {materialCount}. " +
                        $"Load: {loadMs} ms, read: {readMs} ms, JSON: {jsonMs} ms, write: {writeMs} ms, total: {totalWatch.ElapsedMilliseconds} ms.");

                    // success ? output only the path
                    DA.SetData(0, fullPath);
                }
            }
            catch (Exception ex)
            {
                _lastRun = false;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }
    }
}
