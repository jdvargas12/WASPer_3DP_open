using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

namespace WASPer_3DP.Components._1_0_Utils
{
    public class wsp_Ut09_Entwine_Reindex : GH_Component, IGH_VariableParameterComponent
    {
        private readonly string _versionTag;

        // Minimum number of TREE inputs to keep alive
        private const int MinTreeInputs = 2;

        // Fixed control inputs at the end
        private const int FixedInputs = 2; // branch_level, strict

        public wsp_Ut09_Entwine_Reindex()
            : base(
                "wsp_Ut09_Entwine Reindex",
                "EntwineX",
                "Like Entwine, but reindexes paths so that each next input is appended after the previous one " +
                "by shifting ONLY the index at a chosen path level (branch_level).\n\n" +
                "Defaults:\n" +
                "- branch_level = -1 uses the last path index (classic behavior).\n" +
                "- strict = true enforces baseline group-set (first non-empty input).\n\n" +
                "Strict check:\n" +
                "- All inputs must share the same path depth.\n" +
                "- If strict=true: all inputs must share the same GROUP set as the first non-empty input,\n" +
                "  where GROUP = path with branch_level removed.\n" +
                "- If strict=false: groups can differ; missing-from-baseline groups raise warnings.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "1.0_Utils")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            this.Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("A3F1C2D4-5B6E-4A7F-8C9D-0E1F2A3B4C5D");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ut12_Entwine_Reindex.png"))
                        return s != null ? new System.Drawing.Bitmap(s) : null;
                }
                catch { }
                return null;
            }
        }

        // --------------------------------------------------------------------
        // IO Registration
        // --------------------------------------------------------------------
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            // Start with two generic tree inputs, matching Entwine behaviour
            pManager.AddGenericParameter("Branch 0", "B0", "Data tree to entwine (reindexed).", GH_ParamAccess.tree);
            pManager.AddGenericParameter("Branch 1", "B1", "Data tree to entwine (reindexed).", GH_ParamAccess.tree);

            // Controls (fixed at end; not affected by +/-)
            pManager.AddIntegerParameter("branch_level", "L",
                "0-based path index to reindex.\n" +
                "Example: path {A;B;C} -> L=2 reindexes C, L=1 reindexes B, L=0 reindexes A.\n" +
                "Default -1 means use the last index.",
                GH_ParamAccess.item, -1);

            pManager.AddBooleanParameter("strict", "S",
                "If true: enforce baseline group-set (ERROR on mismatch).\n" +
                "If false: allow new groups (WARNING) and continue.",
                GH_ParamAccess.item, true);

            // Optional so component doesn't error when missing wires
            pManager[0].Optional = true;
            pManager[1].Optional = true;
            pManager[2].Optional = true;
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("Result", "R",
                "Entwined and reindexed data tree.", GH_ParamAccess.tree);
        }

        // --------------------------------------------------------------------
        // IGH_VariableParameterComponent  (Entwine-style +/- buttons)
        // Only affects TREE inputs (all inputs except the last 2 fixed controls).
        // --------------------------------------------------------------------
        public bool CanInsertParameter(GH_ParameterSide side, int index)
        {
            if (side != GH_ParameterSide.Input)
                return false;

            int treeCount = Math.Max(0, Params.Input.Count - FixedInputs);

            // Can insert only within the tree-input range [0..treeCount]
            return index >= 0 && index <= treeCount;
        }

        public bool CanRemoveParameter(GH_ParameterSide side, int index)
        {
            if (side != GH_ParameterSide.Input)
                return false;

            int treeCount = Math.Max(0, Params.Input.Count - FixedInputs);

            // Can't remove fixed controls
            if (index >= treeCount)
                return false;

            // Keep at least MinTreeInputs
            return treeCount > MinTreeInputs;
        }

        public IGH_Param CreateParameter(GH_ParameterSide side, int index)
        {
            // Only create TREE input params
            var param = new Param_GenericObject
            {
                Name = $"Branch {index}",
                NickName = $"B{index}",
                Description = "Data tree to entwine (reindexed).",
                Access = GH_ParamAccess.tree,
                Optional = true
            };
            return param;
        }

        public bool DestroyParameter(GH_ParameterSide side, int index) => true;

        public void VariableParameterMaintenance()
        {
            int treeCount = Math.Max(0, Params.Input.Count - FixedInputs);

            // Re-label tree inputs sequentially after add/remove
            for (int i = 0; i < treeCount; i++)
            {
                Params.Input[i].Name = $"Branch {i}";
                Params.Input[i].NickName = $"B{i}";
                Params.Input[i].Optional = true;
                Params.Input[i].Access = GH_ParamAccess.tree;
            }

            // Enforce fixed controls at the end
            if (Params.Input.Count >= 2)
            {
                var pLevel = Params.Input[Params.Input.Count - 2];
                pLevel.Name = "branch_level";
                pLevel.NickName = "L";
                pLevel.Description =
                    "0-based path index to reindex.\n" +
                    "Default -1 means use the last index.";
                pLevel.Optional = true;
                pLevel.Access = GH_ParamAccess.item;

                var pStrict = Params.Input[Params.Input.Count - 1];
                pStrict.Name = "strict";
                pStrict.NickName = "S";
                pStrict.Description =
                    "If true: enforce baseline group-set (ERROR on mismatch).\n" +
                    "If false: allow new groups (WARNING) and continue.";
                pStrict.Optional = true;
                pStrict.Access = GH_ParamAccess.item;
            }
        }

        // --------------------------------------------------------------------
        // Solve
        // --------------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            int treeCount = Math.Max(0, Params.Input.Count - FixedInputs);

            // Read controls (last two inputs)
            int branchLevel = -1;
            bool strict = true;
            DA.GetData(Params.Input.Count - 2, ref branchLevel);
            DA.GetData(Params.Input.Count - 1, ref strict);

            // Collect all TREE input trees
            var inputs = new List<GH_Structure<IGH_Goo>>(treeCount);
            for (int i = 0; i < treeCount; i++)
            {
                var tree = new GH_Structure<IGH_Goo>();
                DA.GetDataTree(i, out tree);
                inputs.Add(tree);
            }

            // Find baseline (first non-empty)
            GH_Structure<IGH_Goo> baseline = null;
            for (int i = 0; i < inputs.Count; i++)
            {
                if (inputs[i] != null && inputs[i].PathCount > 0)
                {
                    baseline = inputs[i];
                    break;
                }
            }

            var outTree = new DataTree<IGH_Goo>();

            if (baseline == null || baseline.PathCount == 0)
            {
                DA.SetDataTree(0, outTree);
                return;
            }

            int pathLen = baseline.Paths[0].Length;

            // Default -1 -> last index
            if (branchLevel < 0)
                branchLevel = pathLen - 1;

            if (branchLevel < 0 || branchLevel >= pathLen)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    $"branch_level must be in [0..{pathLen - 1}] for baseline path depth {pathLen} (got {branchLevel}).");
                DA.SetDataTree(0, new DataTree<IGH_Goo>());
                return;
            }

            // Baseline group set + template path indices per group
            // GROUP = path with the branchLevel index removed
            var baselineGroups = new HashSet<string>(baseline.PathCount);
            var templateByGroup = new Dictionary<string, int[]>(baseline.PathCount);

            for (int i = 0; i < baseline.PathCount; i++)
            {
                var p = baseline.Paths[i];
                if (p.Length != pathLen)
                    continue;

                string gk = GroupKey(p, branchLevel);
                if (!baselineGroups.Contains(gk))
                {
                    baselineGroups.Add(gk);
                    templateByGroup[gk] = (int[])p.Indices.Clone();
                }
            }

            // In non-strict mode, gather templates for any groups that appear later
            if (!strict)
            {
                for (int src = 0; src < inputs.Count; src++)
                {
                    var tree = inputs[src];
                    if (tree == null || tree.PathCount == 0) continue;

                    for (int i = 0; i < tree.PathCount; i++)
                    {
                        var p = tree.Paths[i];
                        if (p.Length != pathLen) continue;

                        string gk = GroupKey(p, branchLevel);
                        if (!templateByGroup.ContainsKey(gk))
                            templateByGroup[gk] = (int[])p.Indices.Clone();
                    }
                }
            }

            // Per-group max index tracker (for the reindexed level)
            var maxIdxByGroup = new Dictionary<string, int>(baselineGroups.Count);

            // Warn once per missing group (non-strict only)
            var warnedGroups = new HashSet<string>();

            // Process each input
            for (int src = 0; src < inputs.Count; src++)
            {
                var tree = inputs[src];
                if (tree == null || tree.PathCount == 0) continue;

                // Validate all paths + check group membership (if strict)
                var groupsInThisInput = new HashSet<string>();

                for (int i = 0; i < tree.PathCount; i++)
                {
                    var p = tree.Paths[i];

                    if (p.Length != pathLen)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                            $"Input {src}: path depth {p.Length} differs from baseline depth {pathLen}.");
                        DA.SetDataTree(0, new DataTree<IGH_Goo>());
                        return;
                    }

                    string gk = GroupKey(p, branchLevel);
                    groupsInThisInput.Add(gk);

                    if (strict)
                    {
                        if (!baselineGroups.Contains(gk))
                        {
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                                $"Input {src}: group not found in baseline (branch_level={branchLevel}). Missing group: {{{gk}}}");
                            DA.SetDataTree(0, new DataTree<IGH_Goo>());
                            return;
                        }
                    }
                }

                // Non-strict warnings
                if (!strict)
                {
                    foreach (var gk in groupsInThisInput)
                    {
                        if (!baselineGroups.Contains(gk) && !warnedGroups.Contains(gk))
                        {
                            warnedGroups.Add(gk);
                            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                                $"Non-strict: Input {src} contains group not in baseline (branch_level={branchLevel}): {{{gk}}}");
                        }
                    }
                }

                // Group by groupKey, storing (idxAtLevel, pathIndex)
                var grouped = new Dictionary<string, List<(int idxAtLevel, int pathIdx)>>(groupsInThisInput.Count);

                for (int i = 0; i < tree.PathCount; i++)
                {
                    var p = tree.Paths[i];
                    string gk = GroupKey(p, branchLevel);
                    int lvl = p.Indices[branchLevel];

                    if (!grouped.TryGetValue(gk, out var list))
                    {
                        list = new List<(int, int)>(4);
                        grouped[gk] = list;
                    }
                    list.Add((lvl, i));
                }

                // Remap and bulk-copy branches
                foreach (var kv in grouped)
                {
                    string groupKey = kv.Key;
                    var records = kv.Value;

                    records.Sort((a, b) => a.idxAtLevel.CompareTo(b.idxAtLevel));

                    if (!maxIdxByGroup.TryGetValue(groupKey, out int maxIdx))
                        maxIdx = -1;

                    int start = maxIdx + 1;

                    if (!templateByGroup.TryGetValue(groupKey, out int[] template))
                    {
                        // Should only happen if non-strict and group has no template yet
                        template = (int[])tree.Paths[records[0].pathIdx].Indices.Clone();
                        templateByGroup[groupKey] = template;
                    }

                    for (int r = 0; r < records.Count; r++)
                    {
                        int newIdx = start + r;
                        GH_Path newPath = BuildPathFromTemplate(template, branchLevel, newIdx);

                        System.Collections.IList branch = tree.get_Branch(records[r].pathIdx);
                        if (branch == null) continue;

                        var items = new List<IGH_Goo>(branch.Count);
                        for (int k = 0; k < branch.Count; k++)
                            items.Add(branch[k] as IGH_Goo);

                        outTree.AddRange(items, newPath);
                    }

                    maxIdxByGroup[groupKey] = start + records.Count - 1;
                }
            }

            DA.SetDataTree(0, outTree);
        }

        // --------------------------------------------------------------------
        // Helpers
        // --------------------------------------------------------------------

        /// <summary>
        /// GROUP key = path indices with the branchLevel index removed, joined by ';'
        /// Example: {A;B;C}, branchLevel=2 -> "A;B"
        ///          {A;B;C}, branchLevel=1 -> "A;C"
        /// </summary>
        private static string GroupKey(GH_Path p, int branchLevel)
        {
            int len = p.Length;
            if (len <= 1)
                return "";

            var sb = new StringBuilder(len * 4);
            bool first = true;

            for (int i = 0; i < len; i++)
            {
                if (i == branchLevel)
                    continue;

                if (!first) sb.Append(';');
                sb.Append(p.Indices[i]);
                first = false;
            }

            return sb.ToString();
        }

        /// <summary>
        /// Builds a new path by cloning a template indices array and replacing the index at branchLevel.
        /// </summary>
        private static GH_Path BuildPathFromTemplate(int[] template, int branchLevel, int newIndexAtLevel)
        {
            int[] idx = (int[])template.Clone();
            idx[branchLevel] = newIndexAtLevel;
            return new GH_Path(idx);
        }
    }
}
