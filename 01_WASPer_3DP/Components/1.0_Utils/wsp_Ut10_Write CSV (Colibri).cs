using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace WASPer_3DP.Components._1_0_Utils
{
    /// <summary>
    /// Writes one Colibri-style input/output record per run to a CSV file.
    ///
    /// Behaviour:
    ///   • <run> must be true to write. Edge-trigger it from a button or Colibri
    ///     iterator so each design iteration appends exactly one row.
    ///   • Each item in <input_data> / <output_data> is a string in the form
    ///     "[column_name, value]" as produced by Colibri's TTToolBox outputs.
    ///   • The "Iteration" column is auto-incremented by counting existing data
    ///     rows — no external counter needed.
    ///   • <reset> deletes the CSV file so the next write starts at iteration 1.
    ///   • When run = false the component still mirrors the current file content
    ///     (headers + all rows as a data tree) so the result is always visible.
    /// </summary>
    public sealed class wsp_Ut10_Write_CSV_Colibri : GH_Component
    {
        // ── version ──────────────────────────────────────────────────────────
        private readonly string _versionTag;

        public wsp_Ut10_Write_CSV_Colibri()
            : base(
                "wsp_Ut10_Write CSV (Colibri)",
                "Write CSV",
                "Writes one Colibri-style input/output record per run to a CSV file.\n" +
                "Input/output data items must be strings in '[name, value]' format\n" +
                "(as produced by Colibri TTToolBox). The Iteration column auto-increments.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "1.0_Utils")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v   = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("D1CC698D-8123-4A04-814D-6DE680270202");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ut02_Write to CSV.png"))
                        return s != null ? new Bitmap(s) : null;
                }
                catch { }
                return null;
            }
        }

        // ── inputs ───────────────────────────────────────────────────────────
        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "input_data", "inD",
                "Input data items in '[column_name, value]' format.\n" +
                "Connect Colibri TTToolBox inputs directly here.",
                GH_ParamAccess.list);

            p.AddGenericParameter(
                "output_data", "outD",
                "Output data items in '[column_name, value]' format.\n" +
                "Connect Colibri TTToolBox outputs directly here.",
                GH_ParamAccess.list);

            p.AddTextParameter(
                "path", "path",
                "Destination folder. Created automatically if it does not exist.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "file_name", "file",
                "CSV file name. The '.csv' extension is added automatically if omitted.",
                GH_ParamAccess.item, "colibri_data.csv");

            p.AddBooleanParameter(
                "reset", "reset",
                "Delete the CSV file and start fresh when true.",
                GH_ParamAccess.item, false);

            p.AddBooleanParameter(
                "run", "run",
                "Write one record to the CSV when true.\n" +
                "Connect to a Colibri iterator or a Button for edge-triggered writes.",
                GH_ParamAccess.item, false);

            p[0].Optional = true; // input_data
            p[1].Optional = true; // output_data
            p[3].Optional = true; // file_name
            p[4].Optional = true; // reset
        }

        // ── outputs ──────────────────────────────────────────────────────────
        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter(
                "csv_file", "csv",
                "Full path to the CSV file.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "csv_headers", "headers",
                "Column header row of the CSV file.",
                GH_ParamAccess.list);

            p.AddTextParameter(
                "csv_data", "data",
                "All data rows as a DataTree. Branch {i} contains the cells of row i.",
                GH_ParamAccess.tree);

            p.AddTextParameter(
                "info", "info",
                "Status message.",
                GH_ParamAccess.item);
        }

        // ── solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── 1. read inputs ──────────────────────────────────────────────
            var inputData  = new List<IGH_Goo>();
            var outputData = new List<IGH_Goo>();
            string folder  = null;
            string fileName = "colibri_data.csv";
            bool reset     = false;
            bool run       = false;

            DA.GetDataList(0, inputData);
            DA.GetDataList(1, outputData);
            DA.GetData    (2, ref folder);
            DA.GetData    (3, ref fileName);
            DA.GetData    (4, ref reset);
            DA.GetData    (5, ref run);

            // ── 2. resolve path ─────────────────────────────────────────────
            string csvPath = ResolveCsvPath(folder, fileName);

            // ── 3. reset ────────────────────────────────────────────────────
            if (reset && csvPath != null && File.Exists(csvPath))
            {
                File.Delete(csvPath);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "CSV file deleted — reset to iteration 1.");
            }

            // ── 4. idle ──────────────────────────────────────────────────────
            if (!run)
            {
                Message = _versionTag;
                DA.SetData(3, "idle — run = false");
                if (csvPath != null && File.Exists(csvPath))
                    MirrorFileContent(DA, csvPath);
                return;
            }

            // ── 5. validate ─────────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(folder))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Provide a valid path.");
                return;
            }
            if (csvPath == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not resolve the CSV file path.");
                return;
            }

            // ── 6. parse Colibri data ────────────────────────────────────────
            var inPairs  = ParseColibriData(inputData);
            var outPairs = ParseColibriData(outputData);

            // ── 7. build header and data row ─────────────────────────────────
            var headers = new List<string> { "Iteration" };
            headers.AddRange(inPairs .Select(kv => kv[0]));
            headers.AddRange(outPairs.Select(kv => kv[0]));

            var allValues = inPairs.Concat(outPairs)
                .Select(kv => kv[1])
                .ToList();

            // ── 8. determine iteration number ────────────────────────────────
            bool fileExisted = File.Exists(csvPath);
            int  iteration   = 1;

            if (fileExisted)
            {
                var existingLines = File.ReadAllLines(csvPath, Encoding.UTF8);
                iteration = existingLines.Skip(1)
                    .Count(l => !string.IsNullOrWhiteSpace(l)) + 1;
            }

            // ── 9. write ─────────────────────────────────────────────────────
            try
            {
                string dir = Path.GetDirectoryName(csvPath);
                if (!string.IsNullOrWhiteSpace(dir))
                    Directory.CreateDirectory(dir);

                using (var sw = new StreamWriter(csvPath, append: fileExisted, Encoding.UTF8))
                {
                    if (!fileExisted)
                        sw.WriteLine(string.Join(",", headers.Select(EscapeCsv)));

                    var row = new List<string> { iteration.ToString() };
                    row.AddRange(allValues.Select(EscapeCsv));
                    sw.WriteLine(string.Join(",", row));
                }

                Message = $"{_versionTag} | iter {iteration}";
                DA.SetData(3, $"Wrote iteration {iteration} → {csvPath}");
                MirrorFileContent(DA, csvPath);
            }
            catch (Exception ex)
            {
                Message = _versionTag + " | error";
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message);
            }
        }

        // ── helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Parses a list of Colibri data items into [key, value] pairs.
        /// Each item is expected to be a string in "[column_name, value]" format,
        /// e.g. "[Wall_Thickness, 2.5]" → key="Wall_Thickness", value="2.5".
        ///
        /// Also handles native [key, value] lists/tuples for robustness.
        /// Falls back to "field_N" naming when the format cannot be parsed.
        /// </summary>
        private static List<string[]> ParseColibriData(List<IGH_Goo> data)
        {
            var result = new List<string[]>();
            if (data == null) return result;

            foreach (var goo in data)
            {
                if (goo == null) continue;
                string raw = goo.ToString() ?? "";

                // strip surrounding whitespace and brackets
                string stripped = raw.Trim().TrimStart('[').TrimEnd(']').Trim();

                var parts = stripped.Split(',');
                if (parts.Length >= 2)
                {
                    string key   = parts[0].Trim();
                    string value = string.Join(",", parts.Skip(1)).Trim();
                    if (string.IsNullOrEmpty(key))
                        key = $"field_{result.Count + 1}";
                    result.Add(new[] { key, value });
                }
                else
                {
                    // single token — use as value with auto-generated key
                    result.Add(new[] { $"field_{result.Count + 1}", stripped });
                }
            }

            return result;
        }

        /// <summary>
        /// Reads the CSV file and populates outputs 0 (path), 1 (headers), 2 (data tree).
        /// </summary>
        private static void MirrorFileContent(IGH_DataAccess DA, string csvPath)
        {
            try
            {
                var lines = File.ReadAllLines(csvPath, Encoding.UTF8);
                if (lines.Length == 0) return;

                DA.SetData    (0, csvPath);
                DA.SetDataList(1, ParseCsvLine(lines[0]));

                var tree = new GH_Structure<GH_String>();
                for (int r = 1; r < lines.Length; r++)
                {
                    if (string.IsNullOrWhiteSpace(lines[r])) continue;
                    var branch = new GH_Path(r - 1);
                    foreach (var cell in ParseCsvLine(lines[r]))
                        tree.Append(new GH_String(cell), branch);
                }
                DA.SetDataTree(2, tree);
            }
            catch { /* leave outputs empty if read fails */ }
        }

        /// <summary>Resolves folder + file name into a full .csv path.</summary>
        private static string ResolveCsvPath(string folder, string name)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            if (string.IsNullOrWhiteSpace(name))   name = "colibri_data.csv";
            if (!name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) name += ".csv";
            return Path.Combine(folder.Trim(), name.Trim());
        }

        /// <summary>Minimal CSV field parser that handles double-quoted fields.</summary>
        private static List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            int i = 0;
            while (i < line.Length)
            {
                if (line[i] == '"')
                {
                    var sb = new StringBuilder();
                    i++; // skip opening quote
                    while (i < line.Length)
                    {
                        if (line[i] == '"' && i + 1 < line.Length && line[i + 1] == '"')
                        { sb.Append('"'); i += 2; }
                        else if (line[i] == '"')
                        { i++; break; }
                        else
                        { sb.Append(line[i++]); }
                    }
                    result.Add(sb.ToString());
                    if (i < line.Length && line[i] == ',') i++;
                }
                else
                {
                    int start = i;
                    while (i < line.Length && line[i] != ',') i++;
                    result.Add(line.Substring(start, i - start));
                    if (i < line.Length) i++; // skip comma
                }
            }
            return result;
        }

        /// <summary>Wraps a CSV field in quotes when it contains special characters.</summary>
        private static string EscapeCsv(string value)
        {
            value = value ?? "";
            bool needsQuote = value.Contains(",") || value.Contains("\"")
                           || value.Contains("\r")  || value.Contains("\n");
            value = value.Replace("\"", "\"\"");
            return needsQuote ? $"\"{value}\"" : value;
        }
    }
}
