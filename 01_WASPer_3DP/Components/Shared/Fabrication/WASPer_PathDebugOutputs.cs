using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Parameters;

namespace WASPer_3DP
{
    /// <summary>
    /// Shared persistent diagnostic-output layout for compact wsp_path modifiers.
    /// The first two fixed outputs are never unregistered during ordinary toggles,
    /// preserving downstream wsp_path and summary wires.
    /// </summary>
    public static class WasperPathDebugOutputs
    {
        public static readonly string[] CoreNickNames =
        {
            "pt_planes",
            "la_planes",
            "flows",
            "layer_h",
            "layer_w",
            "layer_wf",
            "print_speed",
            "print_vol",
            "path_role",
            "stroke_id"
        };

        public static void Rebuild(
            GH_Component component,
            bool showAll,
            string summaryDescription,
            int fixedOutputCount = 2,
            IEnumerable<string> omittedCoreFields = null,
            Action<GH_Component> registerExtras = null)
        {
            if (component == null)
                return;

            fixedOutputCount = Math.Max(0, fixedOutputCount);
            while (component.Params.Output.Count > fixedOutputCount)
            {
                component.Params.UnregisterOutputParameter(
                    component.Params.Output[component.Params.Output.Count - 1],
                    true);
            }

            if (fixedOutputCount == 2 && component.Params.Output.Count < 2)
            {
                while (component.Params.Output.Count > 0)
                {
                    component.Params.UnregisterOutputParameter(
                        component.Params.Output[component.Params.Output.Count - 1],
                        true);
                }
                component.Params.RegisterOutputParam(new Param_GenericObject
                {
                    Name = "wsp_path",
                    NickName = "wsp_path",
                    Description = "Modified WASPer Print Path.",
                    Access = GH_ParamAccess.item
                });
                component.Params.RegisterOutputParam(new Param_String
                {
                    Name = "summary",
                    NickName = "summary",
                    Description = summaryDescription,
                    Access = GH_ParamAccess.item
                });
            }
            else if (fixedOutputCount == 2)
            {
                component.Params.Output[0].Name = "wsp_path";
                component.Params.Output[0].NickName = "wsp_path";
                component.Params.Output[1].Name = "summary";
                component.Params.Output[1].NickName = "summary";
                component.Params.Output[1].Description = summaryDescription;
            }

            if (showAll)
            {
                RegisterCore(component, omittedCoreFields);
                registerExtras?.Invoke(component);
            }

            component.Params.OnParametersChanged();
        }

        /// <summary>
        /// Mask-aware sibling of Rebuild(): instead of one all-or-nothing showAll flag, each
        /// field in catalog (bit i = catalog[i]) is shown independently based on visibleMask.
        /// registerExtras now receives an isVisible predicate so component-specific extra fields
        /// (beyond the shared CoreNickNames) can be gated the same way as core ones. Pair with
        /// AppendOutputVisibilityMenu to build the matching right-click submenu.
        /// </summary>
        public static void Rebuild(
            GH_Component component,
            int visibleMask,
            string summaryDescription,
            IReadOnlyList<string> catalog,
            int fixedOutputCount = 2,
            IEnumerable<string> omittedCoreFields = null,
            Action<GH_Component, Func<string, bool>> registerExtras = null)
        {
            if (component == null)
                return;

            fixedOutputCount = Math.Max(0, fixedOutputCount);
            while (component.Params.Output.Count > fixedOutputCount)
            {
                component.Params.UnregisterOutputParameter(
                    component.Params.Output[component.Params.Output.Count - 1],
                    true);
            }

            if (fixedOutputCount == 2 && component.Params.Output.Count < 2)
            {
                while (component.Params.Output.Count > 0)
                {
                    component.Params.UnregisterOutputParameter(
                        component.Params.Output[component.Params.Output.Count - 1],
                        true);
                }
                component.Params.RegisterOutputParam(new Param_GenericObject
                {
                    Name = "wsp_path",
                    NickName = "wsp_path",
                    Description = "Modified WASPer Print Path.",
                    Access = GH_ParamAccess.item
                });
                component.Params.RegisterOutputParam(new Param_String
                {
                    Name = "summary",
                    NickName = "summary",
                    Description = summaryDescription,
                    Access = GH_ParamAccess.item
                });
            }
            else if (fixedOutputCount == 2)
            {
                component.Params.Output[0].Name = "wsp_path";
                component.Params.Output[0].NickName = "wsp_path";
                component.Params.Output[1].Name = "summary";
                component.Params.Output[1].NickName = "summary";
                component.Params.Output[1].Description = summaryDescription;
            }

            bool IsVisible(string nickName)
            {
                int bit = IndexOfOrdinal(catalog, nickName);
                return bit >= 0 && (visibleMask & (1 << bit)) != 0;
            }

            RegisterCore(component, IsVisible, omittedCoreFields);
            registerExtras?.Invoke(component, IsVisible);

            component.Params.OnParametersChanged();
        }

        public static void Set(
            IGH_DataAccess da,
            GH_Component component,
            WasperPrintPath path,
            string summary)
        {
            da.SetData(0, new WasperPrintPathGoo(path));
            da.SetData(1, summary);
            SetCore(da, component, path);
        }

        public static int OutputIndex(GH_Component component, string nickName)
        {
            if (component == null || string.IsNullOrWhiteSpace(nickName))
                return -1;
            for (int i = 0; i < component.Params.Output.Count; i++)
            {
                if (string.Equals(
                    component.Params.Output[i].NickName,
                    nickName,
                    StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }

        public static void SetCore(
            IGH_DataAccess da,
            GH_Component component,
            WasperPrintPath path)
        {
            if (da == null || component == null || path == null)
                return;

            int index = OutputIndex(component, "pt_planes");
            if (index >= 0 && path.HasPlanes)
                da.SetDataTree(index, WasperGcodeTreeUtil.ToPlaneStructure(path.PtPlanes));
            index = OutputIndex(component, "la_planes");
            if (index >= 0 && path.HasLayerPlanes)
                da.SetDataTree(index, WasperGcodeTreeUtil.ToPlaneStructure(path.LayerPlanes));
            index = OutputIndex(component, "flows");
            if (index >= 0 && path.HasFlows)
                da.SetDataTree(index, WasperGcodeTreeUtil.ToNumberStructure(path.Flows));
            index = OutputIndex(component, "layer_h");
            if (index >= 0 && path.HasLayerH)
                da.SetDataTree(index, WasperGcodeTreeUtil.ToNumberStructure(path.LayerH));
            index = OutputIndex(component, "layer_w");
            if (index >= 0 && path.LayerW != null)
                da.SetDataTree(index, WasperGcodeTreeUtil.ToNumberStructure(path.LayerW));
            index = OutputIndex(component, "layer_wf");
            if (index >= 0 && path.LayerWf != null)
                da.SetDataTree(index, WasperGcodeTreeUtil.ToNumberStructure(path.LayerWf));
            index = OutputIndex(component, "print_speed");
            if (index >= 0 && path.PrintSpeed != null)
                da.SetDataTree(index, WasperGcodeTreeUtil.ToNumberStructure(path.PrintSpeed));
            index = OutputIndex(component, "print_vol");
            if (index >= 0 && path.PrintVol != null)
                da.SetDataTree(index, WasperGcodeTreeUtil.ToNumberStructure(path.PrintVol));
            index = OutputIndex(component, "path_role");
            if (index >= 0 && path.PathRoles != null)
                da.SetDataTree(index, path.PathRoles);
            index = OutputIndex(component, "stroke_id");
            if (index >= 0 && path.StrokeIds != null)
                da.SetDataTree(index, path.StrokeIds);
        }

        public static void RegisterCore(
            GH_Component component,
            IEnumerable<string> omittedCoreFields = null)
        {
            RegisterCore(component, null, omittedCoreFields);
        }

        /// <summary>
        /// Registers core debug outputs, optionally gated per-field by <paramref name="isVisible"/>
        /// (null means "visible whenever not omitted", matching the legacy all-or-nothing behavior).
        /// Field definitions live here once so both the legacy 2-argument overload and mask-aware
        /// callers stay in sync.
        /// </summary>
        public static void RegisterCore(
            GH_Component component,
            Func<string, bool> isVisible,
            IEnumerable<string> omittedCoreFields = null)
        {
            var omitted = new HashSet<string>(
                omittedCoreFields ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            void RegisterIfVisible(string nickName, Func<IGH_Param> factory)
            {
                if (omitted.Contains(nickName))
                    return;
                if (isVisible != null && !isVisible(nickName))
                    return;
                component.Params.RegisterOutputParam(factory());
            }

            RegisterIfVisible("pt_planes", () => new Param_Plane
            {
                Name = "point_planes",
                NickName = "pt_planes",
                Description = "Canonical path planes after the modification.",
                Access = GH_ParamAccess.tree
            });
            RegisterIfVisible("la_planes", () => new Param_Plane
            {
                Name = "layer_planes",
                NickName = "la_planes",
                Description = "Optional authoritative reference plane per logical layer. Branch paths end at the logical-layer dimension; one plane is stored per available layer.",
                Access = GH_ParamAccess.tree
            });
            RegisterIfVisible("flows", () => new Param_Number
            {
                Name = "flows",
                NickName = "flows",
                Description = "Per-location flow multipliers preserved by the modification.",
                Access = GH_ParamAccess.tree
            });
            RegisterIfVisible("layer_h", () => new Param_Number
            {
                Name = "layer_height",
                NickName = "layer_h",
                Description = "Per-location layer heights preserved by the modification.",
                Access = GH_ParamAccess.tree
            });
            RegisterIfVisible("layer_w", () => new Param_Number
            {
                Name = "layer_w",
                NickName = "layer_w",
                Description = "Nominal per-location layer width.",
                Access = GH_ParamAccess.tree
            });
            RegisterIfVisible("layer_wf", () => new Param_Number
            {
                Name = "layer_wf",
                NickName = "layer_wf",
                Description = "Flow-adjusted per-location layer width.",
                Access = GH_ParamAccess.tree
            });
            RegisterIfVisible("print_speed", () => new Param_Number
            {
                Name = "print_speed",
                NickName = "print_speed",
                Description = "Optional per-location print speed carried by wsp_path.",
                Access = GH_ParamAccess.tree
            });
            RegisterIfVisible("print_vol", () => new Param_Number
            {
                Name = "print_volume",
                NickName = "print_vol",
                Description = "Optional per-segment deposited volume carried by the outgoing wsp_path.",
                Access = GH_ParamAccess.tree
            });
            RegisterIfVisible("path_role", () => new Param_Integer
            {
                Name = "path_role",
                NickName = "path_role",
                Description = "Stored semantic role per path branch: 0 Undefined, 1 Shell, 2 Infill, 3 Partition, 4 Support, 5 Transition.",
                Access = GH_ParamAccess.tree
            });
            RegisterIfVisible("stroke_id", () => new Param_Integer
            {
                Name = "stroke_id",
                NickName = "stroke_id",
                Description = "Optional branch-aligned continuity group. Consecutive branches sharing a non-negative id are emitted as one uninterrupted extrusion stroke.",
                Access = GH_ParamAccess.tree
            });
        }

        /// <summary>
        /// Builds a "Debug Outputs" style submenu with one checkbox per catalog field (bit i
        /// corresponds to catalog[i]) plus a "Hide unconnected outputs" command. The submenu is
        /// kept open across clicks (ToolStripDropDown.Closing is cancelled for ItemClicked) so
        /// several outputs can be toggled in one visit instead of reopening the menu each time.
        /// getMask/applyMask are the only points of contact with the component's own persisted
        /// state, so this helper never needs access to protected GH_Component members like
        /// RecordUndoEvent/ExpireSolution - the component's applyMask delegate is responsible for
        /// recording undo, storing the mask, rebuilding outputs, and expiring the solution.
        /// </summary>
        public static void AppendOutputVisibilityMenu(
            GH_Component component,
            ToolStripDropDown menu,
            string submenuLabel,
            IReadOnlyList<string> catalog,
            Func<int> getMask,
            Action<int> applyMask,
            int fixedOutputCount = 2)
        {
            if (component == null || menu == null || catalog == null || catalog.Count == 0 ||
                getMask == null || applyMask == null)
                return;

            var header = new ToolStripMenuItem(submenuLabel ?? "Debug Outputs");
            menu.Items.Add(header);
            header.DropDown.Closing += KeepOpenOnItemClick;

            var items = new ToolStripMenuItem[catalog.Count];

            var hideUnconnected = new ToolStripMenuItem("Hide unconnected outputs");
            hideUnconnected.Click += (sender, args) =>
            {
                int mask = getMask();
                int updated = HideUnconnected(component, mask, catalog, fixedOutputCount);
                if (updated == mask)
                    return;
                applyMask(updated);
                for (int i = 0; i < catalog.Count; i++)
                    items[i].Checked = (updated & (1 << i)) != 0;
            };
            header.DropDownItems.Add(hideUnconnected);
            header.DropDownItems.Add(new ToolStripSeparator());

            for (int i = 0; i < catalog.Count; i++)
            {
                int bit = i;
                var item = new ToolStripMenuItem(catalog[i])
                {
                    Checked = (getMask() & (1 << bit)) != 0
                };
                item.Click += (sender, args) =>
                {
                    int mask = getMask() ^ (1 << bit);
                    applyMask(mask);
                    item.Checked = (mask & (1 << bit)) != 0;
                };
                items[i] = item;
                header.DropDownItems.Add(item);
            }
        }

        private static void KeepOpenOnItemClick(object sender, ToolStripDropDownClosingEventArgs e)
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked)
                e.Cancel = true;
        }

        /// <summary>
        /// Clears the mask bit for every currently-registered output (beyond fixedOutputCount)
        /// that has no downstream wire connected. Pure function - the caller decides how to
        /// apply the returned mask (undo recording, persistence, rebuild, expire solution).
        /// </summary>
        public static int HideUnconnected(
            GH_Component component,
            int mask,
            IReadOnlyList<string> catalog,
            int fixedOutputCount = 2)
        {
            if (component == null || catalog == null)
                return mask;

            for (int i = fixedOutputCount; i < component.Params.Output.Count; i++)
            {
                IGH_Param param = component.Params.Output[i];
                if (param.Recipients.Count > 0)
                    continue;
                int bit = IndexOfOrdinal(catalog, param.NickName);
                if (bit >= 0)
                    mask &= ~(1 << bit);
            }
            return mask;
        }

        private static int IndexOfOrdinal(IReadOnlyList<string> catalog, string nickName)
        {
            for (int i = 0; i < catalog.Count; i++)
            {
                if (string.Equals(catalog[i], nickName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }
    }
}
