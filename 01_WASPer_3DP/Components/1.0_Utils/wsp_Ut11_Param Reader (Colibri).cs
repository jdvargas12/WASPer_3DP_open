// -----------------------------------------------------------------------
//  wsp_Ut11_Param Reader (Colibri).cs
//
//  GH PARAM READER
//  ---------------
//  Scans the active Grasshopper canvas for GH Groups whose NickName
//  starts with one or more user-supplied prefixes. Collects all
//  parameter containers found inside each matching group, sorts them
//  top-to-bottom by their canvas Y position, and outputs them as
//  [name, value] pairs in Colibri-compatible format.
//
//  UPDATED REFRESH LOGIC
//  ---------------------
//  - Group/container membership is scanned only when:
//      1. link becomes active for the first time,
//      2. refresh is toggled,
//      3. prefixes change,
//      4. the cache is empty.
//  - After a scan, the component subscribes to SolutionExpired on each
//    cached container.
//  - When a watched parameter container receives new data, this reader
//    schedules a safe Grasshopper recompute and only re-reads values from
//    the cached containers. It does NOT rescan the groups.
//
//  TARGET WORKFLOW
//  ---------------
//  This is mainly intended for grouped Grasshopper parameter containers:
//      - Data / Generic Data containers
//      - Number containers
//      - Integer containers
//      - Text containers
//      - Any IGH_Param that stores or relays upstream volatile data
//
//  INPUTS
//    prefix   — one or more group-name prefixes to collect from
//    link     — True = active scan/read; False = idle
//    refresh  — toggle to re-scan group membership/layout/names
//    add_pref — 1 → container name only
//               2 → group suffix + container name  (default)
//               3 → full group name + container name
//
//  OUTPUT
//    params   — list of "[name,value]" strings, Colibri-compatible
//
//  NOTES
//  · All groups matching ANY supplied prefix are collected together.
//  · Sort order: top-to-bottom (Y), then left-to-right (X).
//  · Wire-connected VolatileData takes priority.
//  · Toggle refresh after adding/removing/renaming/rearranging grouped containers.
//  · Changing the data flowing into already-cached containers does NOT need refresh.
// -----------------------------------------------------------------------

#region Usings
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Reflection;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Data;
#endregion

namespace WASPer_3DP.Components._1_0_Utils
{
    public sealed class wsp_Ut11_Param_Reader_Colibri : GH_Component
    {
        // ---- Identity ------------------------------------------------

        private const string NAME =
            "wsp_Ut11_Param Reader (Colibri)";

        private const string NICK =
            "Param Reader";

        private const string DESC =
            "Reads parameter containers from GH Groups matching one or more prefixes.\n\n" +
            "Place Data, Number, Integer, Text, or Generic parameter containers inside a named GH Group, " +
            "then supply the group name prefix here. The component collects all containers found in matching groups, " +
            "sorts them top-to-bottom by canvas Y position, and outputs [name,value] pairs in Colibri-compatible format.\n\n" +
            "Refresh behavior:\n" +
            "· Toggle refresh only after adding/removing/renaming/rearranging grouped containers.\n" +
            "· Once scanned, cached containers are watched automatically.\n" +
            "· When their upstream values change, this component updates its output without rescanning the groups.\n\n" +
            "Name formatting:\n" +
            "· add_pref = 1 : container name only\n" +
            "· add_pref = 2 : group suffix + container name  (default)\n" +
            "· add_pref = 3 : full group name + container name";

        private const string CAT =
            global::WASPer_3DP.WASPerPalette.DesignFabrication;

        private const string SUBCAT =
            "1.0_Utils";

        // ---- Persistent component state -------------------------------

        private readonly string _versionTag;

        // Stable, ordered list of watched parameter descriptors.
        private readonly List<CachedParam> _cachedParams =
            new List<CachedParam>();

        // Fast lookup from GUID to the actual Grasshopper parameter object.
        private readonly Dictionary<Guid, IGH_Param> _paramLookup =
            new Dictionary<Guid, IGH_Param>();

        // Event subscriptions to cached parameter containers.
        private readonly List<IGH_DocumentObject> _watchedObjects =
            new List<IGH_DocumentObject>();

        private string _cacheKey =
            string.Empty;

        private bool _hasRefreshState =
            false;

        private bool _lastRefreshState =
            false;

        private int _cachedGroupCount =
            0;

        // Prevents scheduling many duplicate refreshes if several watched
        // params expire in the same Grasshopper invalidation chain.
        private bool _valueRefreshScheduled =
            false;

        // ---- Cached record --------------------------------------------

        private sealed class CachedParam
        {
            public Guid Id;
            public string GroupName;
            public string ContainerName;
            public float X;
            public float Y;
        }

        // ---- Constructor ----------------------------------------------

        public wsp_Ut11_Param_Reader_Colibri()
            : base(NAME, NICK, DESC, CAT, SUBCAT)
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;

            _versionTag =
                v != null
                    ? $"v{v.Major}.{v.Minor}.{v.Build}"
                    : "v1.0.x";

            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("C5E7A9B1-3D2F-4E6C-8A0B-1F3D5E7C9A2B");

        public override GH_Exposure Exposure =>
            GH_Exposure.secondary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();

                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ut13_GH_Param_Reader.png"))
                    {
                        return s != null
                            ? new System.Drawing.Bitmap(s)
                            : null;
                    }
                }
                catch
                {
                    return null;
                }
            }
        }

        // ---- Parameters -----------------------------------------------

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddTextParameter(
                "prefix",
                "prefix",
                "One or more GH Group name prefixes to collect from.\n\n" +
                "Every group on the canvas whose NickName starts with any of these strings will be scanned.\n\n" +
                "Example:\n" +
                "· 'params' matches 'params_therm_An'\n" +
                "· ['params','samp'] collects from both prefix families.",
                GH_ParamAccess.list);

            p.AddBooleanParameter(
                "link",
                "link",
                "Enables or disables the watcher.\n\n" +
                "True  → component scans/caches grouped containers and keeps their values updated.\n" +
                "False → component goes idle, clears cache, and removes event subscriptions.",
                GH_ParamAccess.item,
                false);

            p.AddBooleanParameter(
                "refresh",
                "refresh",
                "Manual membership re-scan trigger.\n\n" +
                "Toggle this boolean to force the component to re-scan matching groups.\n" +
                "Use refresh after:\n" +
                "· adding or removing grouped containers,\n" +
                "· renaming groups,\n" +
                "· renaming containers,\n" +
                "· rearranging the container order on the canvas.\n\n" +
                "Changing the data flowing into already-cached containers does NOT require refresh.",
                GH_ParamAccess.item,
                false);

            p.AddIntegerParameter(
                "add_pref",
                "add_pref",
                "Controls how each output parameter name is composed.\n\n" +
                "1 → container name only\n" +
                "    Example: λ_total_parallel\n\n" +
                "2 → group suffix + container name  (default)\n" +
                "    prefix='params', group='params_therm_An'\n" +
                "    Example: therm_An_λ_total_parallel\n\n" +
                "3 → full group name + container name\n" +
                "    Example: params_therm_An_λ_total_parallel",
                GH_ParamAccess.item,
                2);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter(
                "params",
                "params",
                "List of [name,value] pairs collected from all parameter containers found inside matching GH Groups.\n\n" +
                "Format examples:\n" +
                "· [λ_total_parallel,0.046]\n" +
                "· [therm_An_λ_total_parallel,0.046]\n" +
                "· [params_therm_An_λ_total_parallel,0.046]\n\n" +
                "Items are ordered top-to-bottom by canvas Y position, then left-to-right by X position.\n" +
                "Compatible with Colibri-style parameter aggregation.",
                GH_ParamAccess.list);
        }

        // ---- Solve ----------------------------------------------------

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var prefixes = new List<string>();

            bool link = false;
            bool refresh = false;
            int addPref = 2;

            DA.GetDataList("prefix", prefixes);
            DA.GetData("link", ref link);
            DA.GetData("refresh", ref refresh);
            DA.GetData("add_pref", ref addPref);

            // ---- Idle state -------------------------------------------

            if (!link)
            {
                ClearAllCacheAndSubscriptions();

                DA.SetDataList(
                    "params",
                    new[] { "idle — set link = true to scan grouped parameter containers." });

                Message = "idle";
                return;
            }

            // ---- Validate prefixes ------------------------------------

            prefixes = prefixes
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .ToList();

            if (prefixes.Count == 0)
            {
                ClearAllCacheAndSubscriptions();

                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "At least one prefix is required to search for GH Groups.");

                DA.SetDataList("params", new string[0]);
                Message = "no prefix";
                return;
            }

            // ---- Active document --------------------------------------

            var doc = OnPingDocument();

            if (doc == null)
            {
                ClearAllCacheAndSubscriptions();

                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "No active Grasshopper document found.");

                DA.SetDataList("params", new string[0]);
                Message = "no doc";
                return;
            }

            try
            {
                string nextCacheKey =
                    BuildCacheKey(prefixes);

                bool refreshChanged =
                    !_hasRefreshState ||
                    refresh != _lastRefreshState;

                bool needsScan =
                    refreshChanged ||
                    _cachedParams.Count == 0 ||
                    !string.Equals(
                        _cacheKey,
                        nextCacheKey,
                        StringComparison.Ordinal);

                // ---- Membership scan: only when needed ----------------

                if (needsScan)
                {
                    RebuildCacheAndSubscriptions(
                        doc,
                        prefixes,
                        nextCacheKey,
                        refresh);
                }
                else
                {
                    _lastRefreshState = refresh;
                }

                // ---- Report empty scans --------------------------------

                if (_cachedGroupCount == 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"No GH Groups found matching prefixes: {string.Join(", ", prefixes)}.");

                    DA.SetDataList("params", new string[0]);
                    Message = "no groups";
                    return;
                }

                if (_cachedParams.Count == 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        "Matching groups contain no readable parameter containers.");

                    DA.SetDataList("params", new string[0]);
                    Message = "empty groups";
                    return;
                }

                // ---- Read current values from cached containers --------

                int missingCount;

                var outputList =
                    BuildOutputList(
                        doc,
                        prefixes,
                        addPref,
                        out missingCount);

                if (missingCount > 0)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"{missingCount} cached parameter container(s) no longer exist. Toggle refresh to rebuild the watched list.");
                }

                DA.SetDataList("params", outputList);

                Message =
                    $"{outputList.Count} params | {_cachedGroupCount} group(s)";
            }
            catch (Exception ex)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    ex.Message);

                Message = "ERR";
            }
        }

        // ---- Lifecycle cleanup ----------------------------------------

        public override void RemovedFromDocument(GH_Document document)
        {
            ClearAllCacheAndSubscriptions();
            base.RemovedFromDocument(document);
        }

        // ---- Cache / scan helpers -------------------------------------

        private void RebuildCacheAndSubscriptions(
            GH_Document doc,
            List<string> prefixes,
            string nextCacheKey,
            bool refresh)
        {
            // Remove old listeners before rebuilding the watched set.
            ClearWatchedSubscriptions();

            _cachedParams.Clear();
            _paramLookup.Clear();

            _cachedGroupCount =
                ScanWatchedParams(
                    doc,
                    prefixes,
                    _cachedParams,
                    _paramLookup);

            SubscribeToWatchedParams();

            _cacheKey = nextCacheKey;
            _hasRefreshState = true;
            _lastRefreshState = refresh;
        }

        private void ClearAllCacheAndSubscriptions()
        {
            ClearWatchedSubscriptions();

            _cachedParams.Clear();
            _paramLookup.Clear();

            _cacheKey = string.Empty;
            _hasRefreshState = false;
            _lastRefreshState = false;
            _cachedGroupCount = 0;
            _valueRefreshScheduled = false;
        }

        private static string BuildCacheKey(IEnumerable<string> prefixes)
        {
            return string.Join(
                "|",
                prefixes
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(p => p.Trim())
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        }

        private static int ScanWatchedParams(
            GH_Document doc,
            List<string> prefixes,
            List<CachedParam> output,
            Dictionary<Guid, IGH_Param> lookup)
        {
            if (doc == null || prefixes == null || output == null)
                return 0;

            // ---- Find all matching groups ------------------------------

            var matchingGroups =
                doc.Objects
                    .OfType<GH_Group>()
                    .Where(g =>
                        !string.IsNullOrEmpty(g.NickName) &&
                        prefixes.Any(px =>
                            g.NickName.StartsWith(
                                px,
                                StringComparison.OrdinalIgnoreCase)))
                    .ToList();

            // ---- Map grouped object GUID → group name ------------------

            var guidToGroupName =
                new Dictionary<Guid, string>();

            foreach (var group in matchingGroups)
            {
                foreach (var id in group.ObjectIDs)
                    guidToGroupName[id] = group.NickName;
            }

            // ---- Collect only IGH_Param containers ---------------------

            foreach (var obj in doc.Objects)
            {
                if (!(obj is IGH_Param param))
                    continue;

                if (!guidToGroupName.TryGetValue(
                    obj.InstanceGuid,
                    out string groupName))
                    continue;

                string containerName =
                    !string.IsNullOrEmpty(param.NickName)
                        ? param.NickName
                        : (!string.IsNullOrEmpty(param.Name)
                            ? param.Name
                            : "unnamed");

                output.Add(new CachedParam
                {
                    Id = obj.InstanceGuid,
                    GroupName = groupName,
                    ContainerName = containerName,
                    X = obj.Attributes?.Pivot.X ?? 0f,
                    Y = obj.Attributes?.Pivot.Y ?? 0f
                });

                if (lookup != null)
                    lookup[obj.InstanceGuid] = param;
            }

            // Deterministic sort:
            // Y first, then X, then GUID string to prevent unstable swaps
            // if two containers share identical canvas coordinates.
            output.Sort((a, b) =>
            {
                int y = a.Y.CompareTo(b.Y);
                if (y != 0) return y;

                int x = a.X.CompareTo(b.X);
                if (x != 0) return x;

                return string.Compare(
                    a.Id.ToString("N"),
                    b.Id.ToString("N"),
                    StringComparison.Ordinal);
            });

            return matchingGroups.Count;
        }

        // ---- Watcher event subscriptions -------------------------------

        private void SubscribeToWatchedParams()
        {
            ClearWatchedSubscriptions();

            foreach (var param in _paramLookup.Values)
            {
                if (param == null)
                    continue;

                var docObj =
                    param as IGH_DocumentObject;

                if (docObj == null)
                    continue;

                try
                {
                    docObj.SolutionExpired += OnWatchedParamSolutionExpired;
                    _watchedObjects.Add(docObj);
                }
                catch
                {
                    // Ignore unusual Grasshopper objects that refuse subscription.
                }
            }
        }

        private void ClearWatchedSubscriptions()
        {
            foreach (var obj in _watchedObjects)
            {
                if (obj == null)
                    continue;

                try
                {
                    obj.SolutionExpired -= OnWatchedParamSolutionExpired;
                }
                catch
                {
                    // Ignore stale/deleted objects.
                }
            }

            _watchedObjects.Clear();
            _valueRefreshScheduled = false;
        }

        private void OnWatchedParamSolutionExpired(
            IGH_DocumentObject sender,
            GH_SolutionExpiredEventArgs e)
        {
            RequestValueRefresh();
        }

        private void RequestValueRefresh()
        {
            if (_valueRefreshScheduled)
                return;

            var doc = OnPingDocument();

            if (doc == null)
                return;

            _valueRefreshScheduled = true;

            // Schedule a safe follow-up solution. The callback expires only this
            // component, so it re-reads current cached param values without
            // forcing a new group membership scan.
            doc.ScheduleSolution(1, d =>
            {
                _valueRefreshScheduled = false;

                try
                {
                    ExpireSolution(false);
                }
                catch
                {
                    // Avoid breaking the document if the component disappears
                    // between scheduling and callback execution.
                }
            });
        }

        // ---- Output builder -------------------------------------------

        private List<string> BuildOutputList(
            GH_Document doc,
            List<string> prefixes,
            int addPref,
            out int missingCount)
        {
            missingCount = 0;

            var outputList =
                new List<string>(_cachedParams.Count);

            // Never skip cached entries. Skipping would shift all following
            // column positions and scramble Colibri's positional mapping.
            // If a cached parameter is missing, emit an empty placeholder.
            foreach (var e in _cachedParams)
            {
                string paramName =
                    BuildParamName(
                        e,
                        prefixes,
                        addPref);

                IGH_Param param =
                    ResolveParam(
                        doc,
                        e.Id);

                if (param == null)
                {
                    missingCount++;
                    outputList.Add($"[{paramName},]");
                    continue;
                }

                string value =
                    GetParamValue(param);

                outputList.Add($"[{paramName},{value}]");
            }

            return outputList;
        }

        private static string BuildParamName(
            CachedParam e,
            List<string> prefixes,
            int addPref)
        {
            switch (addPref)
            {
                case 1:
                    return e.ContainerName;

                case 3:
                    return e.GroupName + "_" + e.ContainerName;

                default:
                    string matchedPx =
                        prefixes.FirstOrDefault(px =>
                            e.GroupName.StartsWith(
                                px,
                                StringComparison.OrdinalIgnoreCase)) ?? "";

                    string groupSuffix =
                        e.GroupName
                            .Substring(matchedPx.Length)
                            .TrimStart('_');

                    return string.IsNullOrEmpty(groupSuffix)
                        ? e.ContainerName
                        : groupSuffix + "_" + e.ContainerName;
            }
        }

        private IGH_Param ResolveParam(
            GH_Document doc,
            Guid id)
        {
            // Fast path: cached reference rebuilt on each scan.
            if (_paramLookup.TryGetValue(id, out var cached) && cached != null)
                return cached;

            // Rare fallback: find it again by GUID if the lookup was lost
            // before the next explicit scan.
            if (doc == null)
                return null;

            var obj =
                doc.Objects.FirstOrDefault(o =>
                    o.InstanceGuid == id);

            return obj as IGH_Param;
        }

        // ---- Parameter value reader -----------------------------------

        /// <summary>
        /// Extracts a compact string value from a Grasshopper parameter container.
        ///
        /// Main target:
        /// - Data / Generic Data containers
        /// - Number containers
        /// - Integer containers
        /// - Text containers
        ///
        /// Strategy:
        /// 1. Read VolatileData first — this covers the containers shown in the intended workflow.
        /// 2. If empty and the param has incoming wires, call CollectData() and retry.
        /// 3. If still empty, fall back to PersistentData through reflection.
        ///
        /// A few common special widgets are also supported directly for robustness.
        /// </summary>
        private static string GetParamValue(IGH_Param param)
        {
            if (param == null)
                return string.Empty;

            try
            {
                // ---- 1. Main path: grouped Data / Number / Text containers ----

                string volatileText =
                    ReadVolatile(param);

                if (!string.IsNullOrEmpty(volatileText))
                    return volatileText;

                // If upstream wires exist but VolatileData has not updated yet,
                // force local data collection and retry.
                if (param.SourceCount > 0)
                {
                    try
                    {
                        param.CollectData();
                    }
                    catch
                    {
                        // Ignore and continue to fallback.
                    }

                    string retry =
                        ReadVolatile(param);

                    if (!string.IsNullOrEmpty(retry))
                        return retry;
                }

                // ---- 2. Widget-specific fallbacks -----------------------------

                if (param is GH_Panel panel)
                {
                    return CleanString(
                        panel.UserText ?? string.Empty);
                }

                if (param is GH_NumberSlider slider)
                {
                    return CleanString(
                        slider.CurrentValue.ToString(
                            CultureInfo.InvariantCulture));
                }

                if (param is GH_BooleanToggle toggle)
                {
                    return toggle.Value
                        ? "True"
                        : "False";
                }

                if (param is GH_ValueList valueList)
                {
                    var item =
                        valueList.FirstSelectedItem;

                    if (item == null)
                        return string.Empty;

                    string raw =
                        !string.IsNullOrEmpty(item.Expression)
                            ? item.Expression
                            : item.Name;

                    return CleanString(
                        raw ?? string.Empty);
                }

                if (param is GH_ColourSwatch swatch)
                {
                    var c = swatch.SwatchColour;
                    return $"{c.R},{c.G},{c.B},{c.A}";
                }

                // ---- 3. Final fallback: persistent locally-set data ------------

                return ReadPersistent(param) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Joins all VolatileData items into one compact semicolon-separated string.
        /// Single values are returned directly.
        /// </summary>
        private static string ReadVolatile(IGH_Param param)
        {
            try
            {
                if (param?.VolatileData == null || param.VolatileData.IsEmpty)
                    return string.Empty;

                var allData =
                    param.VolatileData
                        .AllData(true)
                        .ToList();

                if (allData.Count == 0)
                    return string.Empty;

                if (allData.Count == 1)
                {
                    return CleanString(
                        allData[0]?.ToString() ?? string.Empty);
                }

                return string.Join(
                    ";",
                    allData.Select(d =>
                        CleanString(d?.ToString() ?? string.Empty)));
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// PersistentData is implemented on generic GH_PersistentParam<T>,
        /// not exposed uniformly by IGH_Param. Reflection allows this reader
        /// to support Number, Integer, String, Generic, etc. without hard-coding
        /// each concrete param type.
        /// </summary>
        private static string ReadPersistent(IGH_Param param)
        {
            try
            {
                var prop =
                    param.GetType().GetProperty(
                        "PersistentData",
                        BindingFlags.Instance |
                        BindingFlags.Public |
                        BindingFlags.NonPublic);

                if (prop == null)
                    return string.Empty;

                var pd =
                    prop.GetValue(param) as IGH_Structure;

                if (pd == null || pd.IsEmpty)
                    return string.Empty;

                var items =
                    new List<string>();

                foreach (var goo in pd.AllData(true))
                {
                    items.Add(
                        CleanString(
                            goo?.ToString() ?? string.Empty));
                }

                if (items.Count == 0)
                    return string.Empty;

                if (items.Count == 1)
                    return items[0];

                return string.Join(";", items);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Removes line breaks and trims whitespace so values are safe inside
        /// Colibri-style [name,value] strings.
        /// </summary>
        private static string CleanString(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;

            return s
                .Replace("\r\n", " ")
                .Replace("\n", " ")
                .Replace("\r", " ")
                .Trim();
        }
    }
}