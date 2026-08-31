#region Component Description
/*
Component: wsp_Ro04_Merge KRL
Nickname: merge_krl
Category: WASPer_3DP
SubCategory: 5.1_Robot Gcode

Robots 2.x-compatible replacement for Robots Extended "Merge KRL". It merges
the KUKA code tree emitted by Robots Create Program into one SRC file while
moving DAT declarations and their initial values into the merged program.

Both code representations are supported:
  - Robots 1.x: module preambles packed into multiline tree items.
  - Robots 2.x: every KRL line stored as a separate tree item.

The component menu retains the original Fold option. Saving is controlled by
the always-visible path and save inputs.
*/
#endregion

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

using GH_IO.Serialization;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace WASPer_3DP.Components._5_1_Robot_Gcode
{
    public sealed class wsp_Ro04_Merge_KRL : GH_Component
    {
        private readonly string _versionTag;

        private bool _fold;
        private bool _saveError;
        private string _saveMessage = string.Empty;

        public wsp_Ro04_Merge_KRL()
            : base(
                "wsp_Ro04_Merge KRL",
                "merge_krl",
                "Merges the KUKA code tree from Robots Create Program into one KRL SRC " +
                "file. Compatible with both multiline Robots 1.x code and the " +
                "single-line-per-item format used by Robots 2.x.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "5.1_Robot Gcode")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version != null
                ? $"v{version.Major}.{version.Minor}.{version.Build}"
                : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("3DCADE84-5407-4C52-B819-E479537A5753");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    using (Stream stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ro04_Merge KRL.png"))
                    {
                        return stream != null
                            ? new System.Drawing.Bitmap(stream)
                            : null;
                    }
                }
                catch
                {
                    return null;
                }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddTextParameter(
                "code", "code",
                "KUKA Code tree from Robots Create Program. Keep the module branches; " +
                "deeply nested paths are accepted and do not need to be simplified.",
                GH_ParamAccess.tree);

            p.AddTextParameter(
                "path", "path",
                "Save location for the merged KRL file. Supply either an existing folder, " +
                "which uses the generated file_name, or a complete .src file path. When " +
                "empty, the generated file is saved on the Desktop.",
                GH_ParamAccess.item,
                string.Empty);
            p[1].Optional = true;

            p.AddBooleanParameter(
                "save", "save",
                "Connect a Button or Boolean Toggle. True writes the merged KRL code to path; " +
                "false only previews file_name and krl.",
                GH_ParamAccess.item,
                false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter(
                "file_name", "file_name",
                "Merged KRL SRC file name.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "krl", "krl",
                "Merged KRL code, with one output item per line.",
                GH_ParamAccess.list);
        }

        protected override void AppendAdditionalComponentMenuItems(
            ToolStripDropDown menu)
        {
            Menu_AppendItem(
                menu,
                "Fold",
                (sender, args) =>
                {
                    RecordUndoEvent("Toggle KRL fold");
                    _fold = !_fold;
                    ExpireSolution(true);
                },
                true,
                _fold);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            GH_Structure<GH_String> code;
            if (!DA.GetDataTree(0, out code) ||
                code == null ||
                code.PathCount < 3)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "'code' must be the KUKA code tree from Robots Create Program " +
                    "and contain at least Main, DAT, and one SRC branch.");
                return;
            }

            List<List<string>> modules = new List<List<string>>(code.PathCount);
            for (int i = 0; i < code.PathCount; i++)
                modules.Add(ReadLines(code.Branches[i]));

            List<string> wrapper = modules[0];
            List<string> dat = modules[1];
            List<List<string>> sourceModules = modules.Skip(2).ToList();

            int wrapperDef = FindDefinition(wrapper, "DEF ");
            if (wrapperDef < 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "The first code branch does not contain a KUKA DEF declaration.");
                return;
            }

            string wrapperModuleName = ReadModuleName(wrapper[wrapperDef], "DEF ");
            if (string.IsNullOrWhiteSpace(wrapperModuleName))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "The KUKA program name could not be read from the first code branch.");
                return;
            }

            string mergedName = RemoveRobotGroupSuffix(wrapperModuleName);
            if (string.IsNullOrWhiteSpace(mergedName))
                mergedName = wrapperModuleName;

            List<SourceModule> sources = new List<SourceModule>();
            for (int i = 0; i < sourceModules.Count; i++)
            {
                List<string> source = sourceModules[i];
                int definition = FindDefinition(source, "DEF ");
                if (definition < 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"Code branch {i + 2} has no DEF declaration and was ignored.");
                    continue;
                }

                string moduleName = ReadModuleName(source[definition], "DEF ");
                int end = FindLastKeyword(source, "END");
                if (end <= definition)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        $"Code branch {i + 2} has no closing END after its DEF declaration.");
                    return;
                }

                sources.Add(
                    new SourceModule(
                        moduleName,
                        source.GetRange(definition + 1, end - definition - 1)));
            }

            if (sources.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "No valid KUKA SRC submodule was found after the Main and DAT branches.");
                return;
            }

            List<Declaration> declarations = ReadDeclarations(dat);
            HashSet<string> sourceCalls = new HashSet<string>(
                sources
                    .Where(source => !string.IsNullOrWhiteSpace(source.Name))
                    .Select(source => source.Name),
                StringComparer.OrdinalIgnoreCase);

            List<string> wrapperInitialization = new List<string>();
            int wrapperEnd = FindLastKeyword(wrapper, "END");
            int wrapperLimit = wrapperEnd > wrapperDef ? wrapperEnd : wrapper.Count;
            for (int i = wrapperDef + 1; i < wrapperLimit; i++)
            {
                string line = wrapper[i];
                if (!IsSourceCall(line, sourceCalls))
                    wrapperInitialization.Add(line);
            }

            List<string> body = new List<string>();
            foreach (SourceModule source in sources)
                body.AddRange(source.Body);

            ApplyLegacyMergeTransforms(body, declarations);

            int markerIndex = Math.Min(2, body.Count);
            body.Insert(markerIndex, _fold ? ";FOLD" : ";START PROG");
            if (_fold)
                body.Add(";ENDFOLD");

            List<string> merged = new List<string>();
            for (int i = 0; i < wrapperDef; i++)
                merged.Add(wrapper[i]);

            merged.Add($"DEF {mergedName}()");
            merged.Add(";DAT DECL");
            foreach (Declaration declaration in declarations)
                merged.Add(declaration.Code);

            merged.Add(";INI");
            foreach (Declaration declaration in declarations)
            {
                if (declaration.HasValue)
                    merged.Add($"{declaration.VariableName} = {declaration.Value}");
            }

            merged.AddRange(wrapperInitialization);
            merged.AddRange(body);
            merged.Add("END");

            string fileName = mergedName + ".src";
            SaveIfRequested(DA, fileName, merged);

            DA.SetData(0, fileName);
            DA.SetDataList(1, merged);
            Message = $"{_versionTag} | {sources.Count} SRC";

            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Remark,
                $"Merged {sources.Count} SRC module(s) and {declarations.Count} DAT " +
                "declaration(s). Robots 1.x multiline and Robots 2.x line-item code are supported.");
        }

        private void SaveIfRequested(
            IGH_DataAccess DA,
            string fileName,
            IList<string> code)
        {
            bool save = false;
            if (!DA.GetData(2, ref save) || !save)
                return;

            string requestedPath = string.Empty;
            DA.GetData(1, ref requestedPath);

            try
            {
                string outputPath = ResolveOutputPath(
                    requestedPath,
                    fileName);
                string directory = Path.GetDirectoryName(outputPath);
                if (!Directory.Exists(directory))
                    throw new DirectoryNotFoundException(
                        $"The save directory does not exist: {directory}");

                File.WriteAllLines(outputPath, code);
                _saveError = false;
                _saveMessage =
                    $"{DateTime.Now:HH:mm:ss} Saved as " +
                    $"{Path.GetFileName(outputPath)}{Environment.NewLine}@{outputPath}";
            }
            catch (Exception exception)
            {
                _saveError = true;
                _saveMessage = exception.Message;
            }

            AddRuntimeMessage(
                _saveError
                    ? GH_RuntimeMessageLevel.Error
                    : GH_RuntimeMessageLevel.Remark,
                _saveMessage);
        }

        private static string ResolveOutputPath(
            string requestedPath,
            string generatedFileName)
        {
            if (string.IsNullOrWhiteSpace(requestedPath))
            {
                return Path.Combine(
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.Desktop),
                    generatedFileName);
            }

            string expanded = Environment.ExpandEnvironmentVariables(
                requestedPath.Trim().Trim('"'));
            string fullPath = Path.GetFullPath(expanded);

            if (Directory.Exists(fullPath) ||
                EndsWithDirectorySeparator(expanded))
            {
                return Path.Combine(fullPath, generatedFileName);
            }

            if (string.Equals(
                Path.GetExtension(fullPath),
                ".src",
                StringComparison.OrdinalIgnoreCase))
            {
                return fullPath;
            }

            return Path.Combine(fullPath, generatedFileName);
        }

        private static bool EndsWithDirectorySeparator(string path)
        {
            return path.EndsWith(
                    Path.DirectorySeparatorChar.ToString(),
                    StringComparison.Ordinal) ||
                path.EndsWith(
                    Path.AltDirectorySeparatorChar.ToString(),
                    StringComparison.Ordinal);
        }

        private static List<string> ReadLines(IEnumerable<GH_String> items)
        {
            List<string> lines = new List<string>();
            if (items == null) return lines;

            string[] separators =
            {
                "\r\n",
                "\n",
                "\r"
            };

            foreach (GH_String item in items)
            {
                if (item == null || item.Value == null) continue;

                string[] itemLines = item.Value.Split(
                    separators,
                    StringSplitOptions.None);

                foreach (string line in itemLines)
                    lines.Add(line.TrimEnd());
            }

            return lines;
        }

        private static int FindDefinition(IList<string> lines, string keyword)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].TrimStart().StartsWith(
                    keyword,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private static int FindLastKeyword(IList<string> lines, string keyword)
        {
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (string.Equals(
                    lines[i].Trim(),
                    keyword,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private static string ReadModuleName(string definition, string keyword)
        {
            string trimmed = definition.Trim();
            if (!trimmed.StartsWith(keyword, StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            string name = trimmed.Substring(keyword.Length).Trim();
            int parenthesis = name.IndexOf('(');
            if (parenthesis >= 0)
                name = name.Substring(0, parenthesis);

            return name.Trim();
        }

        private static string RemoveRobotGroupSuffix(string name)
        {
            int suffix = name.IndexOf(
                "_T_ROB",
                StringComparison.OrdinalIgnoreCase);
            return suffix > 0 ? name.Substring(0, suffix) : name;
        }

        private static bool IsSourceCall(
            string line,
            ISet<string> sourceNames)
        {
            string trimmed = line.Trim();
            int parenthesis = trimmed.IndexOf('(');
            if (parenthesis <= 0) return false;

            string callName = trimmed.Substring(0, parenthesis).Trim();
            return sourceNames.Contains(callName);
        }

        private static List<Declaration> ReadDeclarations(IList<string> dat)
        {
            List<Declaration> declarations = new List<Declaration>();
            HashSet<string> seen = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (string sourceLine in dat)
            {
                string line = sourceLine.Trim();
                if (!line.StartsWith("DECL ", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("GLOBAL DECL ", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string normalized = ReplaceOrdinalIgnoreCase(
                    line,
                    "GLOBAL ",
                    string.Empty);

                int equals = normalized.IndexOf('=');
                string declarationCode = equals >= 0
                    ? normalized.Substring(0, equals).TrimEnd()
                    : normalized;
                string value = equals >= 0
                    ? normalized.Substring(equals + 1).Trim()
                    : string.Empty;

                string variableName = ReadDeclaredVariable(declarationCode);
                if (string.IsNullOrWhiteSpace(variableName) ||
                    !seen.Add(declarationCode))
                {
                    continue;
                }

                declarations.Add(
                    new Declaration(
                        declarationCode,
                        variableName,
                        value));
            }

            return declarations;
        }

        private static string ReadDeclaredVariable(string declaration)
        {
            string[] tokens = declaration.Split(
                new[] { ' ', '\t' },
                StringSplitOptions.RemoveEmptyEntries);
            return tokens.Length > 0 ? tokens[tokens.Length - 1] : string.Empty;
        }

        private static string ReplaceOrdinalIgnoreCase(
            string source,
            string oldValue,
            string newValue)
        {
            int index = source.IndexOf(
                oldValue,
                StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                source =
                    source.Substring(0, index) +
                    newValue +
                    source.Substring(index + oldValue.Length);
                index = source.IndexOf(
                    oldValue,
                    index + newValue.Length,
                    StringComparison.OrdinalIgnoreCase);
            }

            return source;
        }

        private static void ApplyLegacyMergeTransforms(
            List<string> body,
            IList<Declaration> declarations)
        {
            bool trigger = declarations.Any(
                declaration => declaration.Code.IndexOf(
                    "Zone000",
                    StringComparison.OrdinalIgnoreCase) >= 0);

            if (!trigger) return;

            for (int i = 0; i < body.Count - 1; i++)
            {
                if (!string.Equals(
                    body[i].Trim(),
                    "CONTINUE",
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string next = body[i + 1].TrimStart();
                if (next.StartsWith("WAIT", StringComparison.OrdinalIgnoreCase) ||
                    next.StartsWith("END", StringComparison.OrdinalIgnoreCase) ||
                    next.StartsWith("CONTINUE", StringComparison.OrdinalIgnoreCase))
                {
                    body.RemoveAt(i);
                    i--;
                    continue;
                }

                body[i] =
                    "TRIGGER WHEN DISTANCE=0 DELAY=0 DO " +
                    body[i + 1].Trim();
                body.RemoveAt(i + 1);
            }
        }

        public override bool Write(GH_IWriter writer)
        {
            writer.SetBoolean("FoldKRL", _fold);
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            _fold =
                reader.ItemExists("FoldKRL") &&
                reader.GetBoolean("FoldKRL");
            return base.Read(reader);
        }

        private sealed class SourceModule
        {
            public SourceModule(string name, List<string> body)
            {
                Name = name;
                Body = body;
            }

            public string Name { get; }
            public List<string> Body { get; }
        }

        private sealed class Declaration
        {
            public Declaration(
                string code,
                string variableName,
                string value)
            {
                Code = code;
                VariableName = variableName;
                Value = value;
            }

            public string Code { get; }
            public string VariableName { get; }
            public string Value { get; }
            public bool HasValue => !string.IsNullOrWhiteSpace(Value);
        }
    }
}
