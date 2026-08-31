using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

using Grasshopper.Kernel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WASPer_3DP.Components._1_0_Utils
{
    /// <summary>
    /// Reads a JSON file and optionally drills into it using primary / secondary keys.
    ///
    /// Behaviour:
    ///   • <file_path> is the folder; <file_name> is the file name (without or with .json).
    ///   • If both keys are omitted, the entire JSON is returned.
    ///   • If only <primary_key> is given, the value at that top-level key is returned.
    ///   • If both keys are given, the value at primary_key → secondary_key is returned.
    ///   • Outputs include a flattened <keys> list and <values> list so the content
    ///     of any JSON object can be inspected without extra components.
    /// </summary>
    public sealed class wsp_Ut05_Read_JSON : GH_Component
    {
        // ── version ──────────────────────────────────────────────────────────
        private readonly string _versionTag;

        public wsp_Ut05_Read_JSON()
            : base(
                "wsp_Ut05_Read JSON",
                "Read JSON",
                "Reads a JSON file and extracts data using optional primary / secondary keys.\n" +
                "Returns the selected value as JSON text plus its keys and values as lists.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "1.0_Utils")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v   = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("A3487B5A-31D1-4626-899C-7EB9D9D20404");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ut04_Read Json.png"))
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
                "file_path", "path",
                "Folder containing the JSON file.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "file_name", "file",
                "JSON file name. The '.json' extension is added automatically if omitted.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "primary_key", "key1",
                "Optional top-level key to extract. Leave empty to return the whole document.",
                GH_ParamAccess.item, "");

            p.AddTextParameter(
                "secondary_key", "key2",
                "Optional second-level key within the primary_key value.",
                GH_ParamAccess.item, "");

            p[2].Optional = true; // primary_key
            p[3].Optional = true; // secondary_key
        }

        // ── outputs ───────────────────────────────────────────────────────────
        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter(
                "data", "data",
                "Selected JSON value — pretty-printed if it is an object or array,\n" +
                "or the raw primitive value (string / number / boolean).",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "keys", "keys",
                "Property names of the selected value when it is a JSON object.",
                GH_ParamAccess.list);

            p.AddTextParameter(
                "values", "values",
                "Property values of the selected value when it is a JSON object,\n" +
                "or the array items when it is a JSON array.",
                GH_ParamAccess.list);

            p.AddTextParameter(
                "info", "info",
                "Status message and resolved file path.",
                GH_ParamAccess.item);
        }

        // ── solve ─────────────────────────────────────────────────────────────
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ── 1. read inputs ──────────────────────────────────────────────
            string folder       = null;
            string fileName     = null;
            string primaryKey   = "";
            string secondaryKey = "";

            if (!DA.GetData(0, ref folder))   return;
            if (!DA.GetData(1, ref fileName)) return;
            DA.GetData(2, ref primaryKey);
            DA.GetData(3, ref secondaryKey);

            // ── 2. resolve path ─────────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(fileName))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Provide both file_path and file_name.");
                return;
            }

            fileName = fileName.Trim();
            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                fileName += ".json";

            string fullPath = Path.Combine(folder.Trim(), fileName);

            if (!File.Exists(fullPath))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"File not found:\n{fullPath}");
                DA.SetData(3, $"File not found: {fullPath}");
                return;
            }

            // ── 3. parse JSON ───────────────────────────────────────────────
            JToken root;
            try
            {
                string text = File.ReadAllText(fullPath);
                root = JToken.Parse(text);
            }
            catch (JsonException ex)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"JSON parse error: {ex.Message}");
                return;
            }

            // ── 4. drill down ───────────────────────────────────────────────
            JToken selected = root;
            string selectionPath = "(root)";

            if (!string.IsNullOrWhiteSpace(primaryKey))
            {
                if (root is JObject rootObj && rootObj.ContainsKey(primaryKey))
                {
                    selected      = rootObj[primaryKey];
                    selectionPath = primaryKey;

                    if (!string.IsNullOrWhiteSpace(secondaryKey))
                    {
                        if (selected is JObject inner && inner.ContainsKey(secondaryKey))
                        {
                            selected      = inner[secondaryKey];
                            selectionPath = $"{primaryKey} → {secondaryKey}";
                        }
                        else
                        {
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                                $"secondary_key '{secondaryKey}' not found in '{primaryKey}'. " +
                                "Returning primary_key value.");
                        }
                    }
                }
                else
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"primary_key '{primaryKey}' not found. Returning full document.");
                }
            }

            // ── 5. extract keys / values ────────────────────────────────────
            var outKeys   = new List<string>();
            var outValues = new List<string>();

            if (selected is JObject selObj)
            {
                foreach (var prop in selObj.Properties())
                {
                    outKeys.Add(prop.Name);
                    outValues.Add(TokenToString(prop.Value));
                }
            }
            else if (selected is JArray arr)
            {
                for (int i = 0; i < arr.Count; i++)
                    outValues.Add(TokenToString(arr[i]));
            }

            // ── 6. set outputs ──────────────────────────────────────────────
            DA.SetData    (0, TokenToString(selected));
            DA.SetDataList(1, outKeys);
            DA.SetDataList(2, outValues);
            DA.SetData    (3, $"OK — {fullPath} [{selectionPath}]");

            Message = _versionTag;
        }

        // ── helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// Converts a JToken to a human-readable string.
        /// Objects and arrays are pretty-printed; primitives are returned as their raw value.
        /// </summary>
        private static string TokenToString(JToken token)
        {
            if (token == null) return "";
            switch (token.Type)
            {
                case JTokenType.Object:
                case JTokenType.Array:
                    return token.ToString(Formatting.Indented);
                case JTokenType.String:
                    return token.Value<string>() ?? "";
                default:
                    return token.ToString();
            }
        }
    }
}
