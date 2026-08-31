using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

namespace WASPer_3DP
{
    /// <summary>
    /// Integer-list parameter with a persistent, checkable editor for WASPer path roles.
    /// It remains a Param_Integer so existing integer sources and saved definitions stay compatible.
    /// </summary>
    public sealed class WasperTargetRolesParam : Param_Integer
    {
        // A Param_Integer subclass must declare its own ID. Otherwise it inherits
        // Grasshopper's built-in Integer ID and triggers a component-ID conflict.
        public override Guid ComponentGuid =>
            new Guid("225C14D0-1F31-4ADD-B362-32D835AFA6C0");

        public override GH_Exposure Exposure => GH_Exposure.hidden;

        private static readonly (int Value, string Label)[] RoleOptions =
        {
            (0, "All paths"),
            (1, "Shell"),
            (2, "Infill"),
            (3, "Partition"),
            (4, "Support"),
            (5, "Transition"),
            (6, "Undefined")
        };

        public static WasperTargetRolesParam Create(string description, int defaultRole = 0)
        {
            if (defaultRole < 0 || defaultRole > 6)
                defaultRole = 0;

            var parameter = new WasperTargetRolesParam
            {
                Name = "target_roles",
                NickName = "roles",
                Description = description +
                    " When no source is connected, right-click this input to select one or several roles.",
                Access = GH_ParamAccess.list,
                Optional = true,
                DataMapping = GH_DataMapping.Flatten
            };
            parameter.PersistentData.Append(new GH_Integer(defaultRole), new GH_Path(0));
            return parameter;
        }

        public override void AppendAdditionalMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalMenuItems(menu);
            Menu_AppendSeparator(menu);

            bool canEdit = SourceCount == 0;
            var selected = GetPersistentSelection();
            var roleMenu = Menu_AppendItem(menu, "Select path roles");

            foreach (var option in RoleOptions)
            {
                int role = option.Value;
                Menu_AppendItem(
                    roleMenu.DropDown,
                    option.Label,
                    (sender, args) => ToggleRole(role),
                    canEdit,
                    selected.Contains(role));
            }

            if (!canEdit)
            {
                Menu_AppendSeparator(roleMenu.DropDown);
                Menu_AppendItem(
                    roleMenu.DropDown,
                    "Disconnect sources to edit the saved selection",
                    null,
                    false);
            }
        }

        private HashSet<int> GetPersistentSelection()
        {
            var selected = new HashSet<int>();
            foreach (var branch in PersistentData.Branches)
            {
                foreach (GH_Integer item in branch)
                {
                    if (item != null && item.Value >= 0 && item.Value <= 6)
                        selected.Add(item.Value);
                }
            }

            if (selected.Count == 0 || selected.Contains(0))
                return new HashSet<int> { 0 };

            return selected;
        }

        private void ToggleRole(int role)
        {
            if (SourceCount > 0)
                return;

            var selected = GetPersistentSelection();
            if (role == 0)
            {
                selected.Clear();
                selected.Add(0);
            }
            else
            {
                selected.Remove(0);
                if (!selected.Add(role))
                    selected.Remove(role);

                if (selected.Count == 0)
                    selected.Add(0);
            }

            RecordUndoEvent("Change target roles");
            PersistentData.Clear();
            foreach (int value in selected.OrderBy(value => value))
                PersistentData.Append(new GH_Integer(value), new GH_Path(0));

            OnObjectChanged(GH_ObjectEventType.PersistentData);
            ExpireSolution(true);
        }
    }
}
