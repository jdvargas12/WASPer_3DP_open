using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json;
using WASPer_3DP;

public class wsp_Ma01_OpaqueMaterials : GH_Component
{
    // ---------------------------------------------------------------------
    // static helpers -------------------------------------------------------
    // ---------------------------------------------------------------------
    private static readonly List<Dictionary<string, string>> _defaultLibPreview;
    private static readonly string[] _nameKeys = { "Name", "Material_Name" }; // accept both legacy & new
    private static readonly string[] _catKeys = { "Category", "Cat" };

    static wsp_Ma01_OpaqueMaterials()
    {
        // read embedded lib once (for key list in input tooltip)
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            const string res = "WASPer_3DP.Components._7_Material_Library.solid_materials_lib.json";
            using var s = asm.GetManifestResourceStream(res);
            using var sr = new StreamReader(s ?? throw new Exception("resource missing"));
            string json = sr.ReadToEnd();
            _defaultLibPreview = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(json);
        }
        catch { _defaultLibPreview = new List<Dictionary<string, string>>(); }
    }

    private static string BuildKeyDescription()
    {
        if (!_defaultLibPreview.Any()) return "Optional property-key filter. Key matching is case-insensitive and accepts partial text; filtering is applied only when value is also supplied.";
        var first = _defaultLibPreview[0];
        var keys = first.Keys.Where(k => !_nameKeys.Contains(k, StringComparer.OrdinalIgnoreCase))
                              .OrderBy(k => k);
        int i = 1;
        return "Optional property key to search. Key matching is case-insensitive and accepts partial text; filtering is applied only when value is also supplied. Available embedded-library keys:\n" + string.Join("\n", keys.Select(k => $"{i++}) {k}"));
    }

    // ---------------------------------------------------------------------
    private readonly string _versionTag;

    public wsp_Ma01_OpaqueMaterials()
      : base("wsp_Ma01_Opaque Materials (Solids)", "Opaque Mats",
             "Searches the WASPer solid-material library and returns complete, reusable material records. " +
             "Materials can be filtered by name or by any property key/value pair. By default the embedded JSON library is used; " +
             "supplying lib_path replaces it with a custom JSON library.", global::WASPer_3DP.WASPerPalette.Performance, "7_Material Library")
    {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v   = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
    }

    public override Guid ComponentGuid => new("B2A6C3B8-7E3C-4E55-B1F4-9E8A1F3C5D4F");
    protected override System.Drawing.Bitmap Icon
    {
        get
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using var stream = asm.GetManifestResourceStream("WASPer_3DP.Resources.Icons.15_solid_mat.png");
                return stream != null ? new System.Drawing.Bitmap(stream) : null;
            }
            catch { }
            return null;
        }
    }

    // ---------------------------------------------------------------------
    protected override void RegisterInputParams(GH_InputParamManager pm)
    {
        pm.AddTextParameter("name", "name", "Optional material-name filter. Matching is case-insensitive and accepts partial text. Leave empty to include every material.", GH_ParamAccess.item);
        pm[0].Optional = true;

        pm.AddTextParameter("key", "key", BuildKeyDescription(), GH_ParamAccess.item);
        pm[1].Optional = true;

        pm.AddTextParameter("value", "value", "Optional value to match under key. Matching is case-insensitive and accepts partial text. This filter is applied only when both key and value are supplied.", GH_ParamAccess.item);
        pm[2].Optional = true;

        pm.AddTextParameter("lib_path", "path", "Optional path to a custom material-library JSON file. When supplied, this library replaces the embedded WASPer library; if it cannot be read, the component reports a warning and uses the embedded library.", GH_ParamAccess.item);
        pm[3].Optional = true;

        pm.AddBooleanParameter("remove_str", "rm_str", "If true (default), prop_names and prop_vals contain only properties whose values are numeric for every selected material. If false, string-valued properties are included as well.", GH_ParamAccess.item);
        pm[4].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager pm)
    {
        pm.AddGenericParameter("wasper_mat", "wasper_mat", "Complete WASPer Material objects, one per selected solid material. Each object preserves its name, solid phase, library source, and every original key/value property for use by downstream WASPer components.", GH_ParamAccess.list);
        pm.AddTextParameter("mat_name", "mat_name", "Names of the selected solid materials, in the same order as wasper_mat and the prop_vals branches.", GH_ParamAccess.list);
        pm.AddTextParameter("prop_names", "prop_names", "Ordered property names shared by the prop_vals branches. String-valued fields are omitted when remove_str is true.", GH_ParamAccess.list);
        pm.AddTextParameter("prop_vals", "prop_vals", "Property values for the selected materials. Branch {i} corresponds to material i in wasper_mat and mat_name; items follow the order in prop_names.", GH_ParamAccess.tree);
        pm.AddTextParameter("full_mat", "full_mat", "Complete readable key/value data for each selected material. Each branch contains entries formatted as 'key : value' and corresponds to the same-index wasper_mat item.", GH_ParamAccess.tree);
        pm.AddTextParameter("categories", "cats", "Unique material categories represented by the selected results.", GH_ParamAccess.list);
    }

    // ---------------------------------------------------------------------
    protected override void SolveInstance(IGH_DataAccess DA)
    {
        string nameFilter = null, filterKey = null, filterValue = null, libPath = null;
        bool removeStr = true;
        DA.GetData(0, ref nameFilter);
        DA.GetData(1, ref filterKey);
        DA.GetData(2, ref filterValue);
        DA.GetData(3, ref libPath);
        DA.GetData(4, ref removeStr);

        // 1. decide which library to use ----------------------------------
        List<Dictionary<string, string>> library;
        if (!string.IsNullOrWhiteSpace(libPath))
        {
            library = LoadLibraryFromFile(libPath);
            if (library == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"Failed to read custom library – falling back to embedded.");
                library = LoadEmbeddedLibrary();
            }
        }
        else
        {
            library = LoadEmbeddedLibrary();
        }

        // 2. filtering -----------------------------------------------------
        if (!string.IsNullOrWhiteSpace(filterKey) && !string.IsNullOrWhiteSpace(filterValue))
        {
            library = library.Where(mat => mat.Any(kvp => kvp.Key.IndexOf(filterKey, StringComparison.OrdinalIgnoreCase) >= 0 &&
                                                       kvp.Value.IndexOf(filterValue, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        }

        if (!string.IsNullOrWhiteSpace(nameFilter))
        {
            library = library.Where(mat =>
            {
                string n = TryGet(mat, _nameKeys);
                return n != null && n.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0;
            }).ToList();
        }

        // 3. build outputs -------------------------------------------------
        var matNames = new List<string>();
        var headers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var valuesT = new GH_Structure<GH_String>();
        var fullT = new GH_Structure<GH_String>();

        foreach (var mat in library)
        {
            string n = TryGet(mat, _nameKeys) ?? "Unnamed";
            matNames.Add(n);

            foreach (var kv in mat)
            {
                if (!_nameKeys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) headers.Add(kv.Key);
            }

            string c = TryGet(mat, _catKeys);
            if (c != null) cats.Add(c);
        }

        var headersList = headers.OrderBy(h => h, StringComparer.OrdinalIgnoreCase).ToList();

        // --- remove non-numeric headers if requested ---------------------
        List<string> finalHeaders;
        if (removeStr)
        {
            finalHeaders = headersList.Where(h =>
                library.All(mat => double.TryParse(mat.TryGetValue(h, out var v) ? v : "", out _))
            ).ToList();
        }
        else
        {
            finalHeaders = headersList;
        }

        int branch = 0;
        foreach (var mat in library)
        {
            var row = finalHeaders.Select(h => new GH_String(mat.TryGetValue(h, out var v) ? v : ""));
            valuesT.AppendRange(row, new GH_Path(branch));

            var full = mat.Select(kv => new GH_String($"{kv.Key} : {kv.Value}"));
            fullT.AppendRange(full, new GH_Path(branch));
            branch++;
        }

        string source = string.IsNullOrWhiteSpace(libPath) ? "WASPer embedded solid material library" : libPath;
        var wasperMaterials = library.Select(mat => new WasperMaterialGoo(
            new WasperMaterial(TryGet(mat, _nameKeys) ?? "Unnamed", "Solid", mat, source))).ToList();

        DA.SetDataList(0, wasperMaterials);
        DA.SetDataList(1, matNames);
        DA.SetDataList(2, finalHeaders);
        DA.SetDataTree(3, valuesT);
        DA.SetDataTree(4, fullT);
        DA.SetDataList(5, cats.ToList());
    }

    // ---------------------------------------------------------------------
    private static string TryGet(Dictionary<string, string> dict, string[] keys)
    {
        foreach (var k in keys)
            if (dict.TryGetValue(k, out var v)) return v;
        return null;
    }

    private static List<Dictionary<string, string>> LoadEmbeddedLibrary()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            const string res = "WASPer_3DP.Components._7_Material_Library.solid_materials_lib.json";
            using var s = asm.GetManifestResourceStream(res);
            using var sr = new StreamReader(s ?? throw new Exception("resource missing"));
            string json = sr.ReadToEnd();
            return JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(json) ?? new();
        }
        catch (Exception ex)
        {
            throw new Exception("Cannot read embedded material library: " + ex.Message);
        }
    }

    private List<Dictionary<string, string>> LoadLibraryFromFile(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Custom library not found: {file}");
                return null;
            }
            string json = File.ReadAllText(file);
            return JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(json);
        }
        catch (Exception ex)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Error reading library: {ex.Message}");
            return null;
        }
    }
}
