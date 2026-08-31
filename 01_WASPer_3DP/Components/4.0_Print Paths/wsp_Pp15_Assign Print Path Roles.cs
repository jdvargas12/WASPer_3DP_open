#region Component Description
/*
Component: wsp_Pp15_Assign Print Path Roles
Nickname: Add Tag
Category: WASPer_3DP
SubCategory: 5.0_Gcode

Assigns semantic path roles directly to an existing wsp_path without changing
its geometry or process data. It is intended for external paths that entered
Pp01 as Undefined, or for deliberate downstream role repair.
*/
#endregion

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

using WASPer_3DP;

namespace WASPer_3DP_Components._4_0_Print_Paths
{
    public sealed class wsp_Pp15_Assign_Print_Path_Roles : GH_Component
    {
        private readonly string _versionTag;
        private int _visibleOutputsMask;
        private const string ShowAllOutputsKey = "wsp_gc22_show_all_outputs";
        private const string VisibleOutputsMaskKey = "wsp_gc22_visible_outputs_mask";

        private static readonly string[] OutputCatalog = WasperPathDebugOutputs.CoreNickNames;
        private static readonly int AllOutputsMask = (1 << OutputCatalog.Length) - 1;

        public wsp_Pp15_Assign_Print_Path_Roles()
            : base(
                "wsp_Pp15_Assign Print Path Roles",
                "Path Roles",
                "PURPOSE\r\n" +
                "Assigns Shell, Infill, Partition, Support, Transition, or Undefined roles directly to wsp_path branches.\r\n\r\n" +
                "MAPPING\r\n" +
                "One role value broadcasts to all branches. A role tree with matching paths assigns " +
                "only those exact branches. Otherwise, flattened values repeat by wsp_path branch order.\r\n\r\n" +
                "SAFETY\r\n" +
                "overwrite is false by default, so only Undefined branches are changed. Geometry, " +
                "process fields, analysis, motion, KPI data, and partial-path state are preserved.\r\n\r\n" +
                "DEBUG\r\n" +
                "Right-click Show all outputs to inspect the tagged outgoing path.\r\n\r\n" +
                "Please use the Pp01 WASPer Path from Curves before using this component.",
                WASPerPalette.DesignFabrication,
                "4.0_Print Paths")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version == null
                ? "v1.0.x"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("A6B5D43B-1E4E-4A51-9FBF-539C60238B7A");

        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        protected override Bitmap Icon => AddTagIcon.Bitmap;

        protected override void AppendAdditionalComponentMenuItems(
            ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            WasperPathDebugOutputs.AppendOutputVisibilityMenu(
                this,
                menu,
                "Debug Outputs",
                OutputCatalog,
                () => _visibleOutputsMask,
                mask =>
                {
                    RecordUndoEvent("Toggle outputs");
                    _visibleOutputsMask = mask;
                    WasperPathDebugOutputs.Rebuild(
                        this,
                        _visibleOutputsMask,
                        "Assignment mode, changed/protected branch counts, and resulting role totals.",
                        OutputCatalog);
                    ExpireSolution(true);
                });
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetInt32(VisibleOutputsMaskKey, _visibleOutputsMask);
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            // Rebuild the output topology BEFORE base.Read() restores saved wires, so the
            // optional outputs already exist when Grasshopper reconnects them (otherwise
            // saved wires to them are silently dropped on file open).
            //
            // _visibleOutputsMask migration: files saved before per-output toggles existed only
            // have the legacy boolean ShowAllOutputsKey. Map "Show all outputs" = true to every
            // bit set, so old files keep showing everything they used to.
            if (reader.ItemExists(VisibleOutputsMaskKey))
                _visibleOutputsMask = reader.GetInt32(VisibleOutputsMaskKey);
            else if (reader.ItemExists(ShowAllOutputsKey) && reader.GetBoolean(ShowAllOutputsKey))
                _visibleOutputsMask = AllOutputsMask;
            else
                _visibleOutputsMask = 0;
            WasperPathDebugOutputs.Rebuild(
                this,
                _visibleOutputsMask,
                "Assignment mode, changed/protected branch counts, and resulting role totals.",
                OutputCatalog);
            return base.Read(reader);
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "WASPer Print Path to tag. Geometry and every non-role field pass through unchanged. Please use the Pp01 WASPer Path from Curves before using this component.",
                GH_ParamAccess.item);

            int roleIndex = p.AddIntegerParameter(
                "path role",
                "role",
                "Role assignment: 0 Undefined, 1 Shell, 2 Infill, 3 Partition, 4 Support, 5 Transition. One value " +
                "broadcasts; matching tree paths assign exact branches; otherwise values repeat " +
                "by wsp_path branch order.",
                GH_ParamAccess.tree);
            if (p[roleIndex] is Param_Integer roleParameter)
            {
                roleParameter.AddNamedValue("Undefined", (int)WasperPathRole.Undefined);
                roleParameter.AddNamedValue("Shell", (int)WasperPathRole.Shell);
                roleParameter.AddNamedValue("Infill", (int)WasperPathRole.Infill);
                roleParameter.AddNamedValue("Partition", (int)WasperPathRole.Partition);
                roleParameter.AddNamedValue("Support", (int)WasperPathRole.Support);
                roleParameter.AddNamedValue("Transition", (int)WasperPathRole.Transition);
            }

            p.AddBooleanParameter(
                "overwrite",
                "overwrite",
                "False (default) changes only branches currently stored as Undefined. True also " +
                "replaces existing Shell, Infill, Partition, Support, or Transition roles.",
                GH_ParamAccess.item,
                false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "Input WASPer Print Path with the requested semantic role assignments.",
                GH_ParamAccess.item);
            p.AddTextParameter(
                "summary",
                "summary",
                "Assignment mode, changed/protected branch counts, and resulting role totals.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            WasperPrintPath source = ReadPath(da);
            if (source == null)
                return;
            if (!source.HasPlanes || source.PtPlanes.BranchCount == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "wsp_path contains no pt_planes branches to tag.");
                return;
            }

            GH_Structure<GH_Integer> roleTree = null;
            if (!da.GetDataTree(1, out roleTree) ||
                roleTree == null ||
                roleTree.DataCount == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "role requires at least one value: 0 Undefined, 1 Shell, 2 Infill, 3 Partition, 4 Support, or 5 Transition.");
                return;
            }

            bool overwrite = false;
            da.GetData(2, ref overwrite);

            var flatRoles = new List<int>();
            foreach (IList<GH_Integer> branch in roleTree.Branches)
            {
                foreach (GH_Integer item in branch)
                {
                    if (item != null)
                        flatRoles.Add(item.Value);
                }
            }
            if (flatRoles.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "role contains no valid integer items.");
                return;
            }
            int invalidRole = flatRoles.FirstOrDefault(value =>
                value < (int)WasperPathRole.Undefined ||
                value > (int)WasperPathRole.Transition);
            if (flatRoles.Any(value =>
                value < (int)WasperPathRole.Undefined ||
                value > (int)WasperPathRole.Transition))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    $"Unsupported role value {invalidRole}. Use 0 Undefined, 1 Shell, 2 Infill, 3 Partition, 4 Support, or 5 Transition.");
                return;
            }

            List<GH_Path> paths = source.PtPlanes.Paths.ToList();
            bool broadcast = flatRoles.Count == 1;
            bool exactTree = !broadcast && paths.Any(path => roleTree.PathExists(path));
            string mappingMode = broadcast
                ? "broadcast"
                : exactTree
                    ? "exact matching tree paths"
                    : "repeating branch order";

            var outputRoles = new DataTree<int>();
            int changed = 0;
            int protectedExisting = 0;
            int unassignedByTree = 0;
            for (int branchIndex = 0; branchIndex < paths.Count; branchIndex++)
            {
                GH_Path path = paths[branchIndex];
                WasperPathRole current =
                    WasperGcodeTreeUtil.PathRoleAt(source.PathRoles, path);
                int? requested = null;

                if (broadcast)
                {
                    requested = flatRoles[0];
                }
                else if (exactTree)
                {
                    if (roleTree.PathExists(path))
                    {
                        System.Collections.IList branch = roleTree.get_Branch(path);
                        if (branch != null && branch.Count > 0 &&
                            branch[0] is GH_Integer item)
                            requested = item.Value;
                    }
                    if (!requested.HasValue)
                        unassignedByTree++;
                }
                else
                {
                    requested = flatRoles[branchIndex % flatRoles.Count];
                }

                WasperPathRole result = current;
                if (requested.HasValue)
                {
                    if (!overwrite && current != WasperPathRole.Undefined)
                    {
                        protectedExisting++;
                    }
                    else
                    {
                        result = (WasperPathRole)requested.Value;
                        if (result != current)
                            changed++;
                    }
                }
                outputRoles.Add((int)result, path);
            }

            WasperPrintPath output = source.WithPathRoles(outputRoles);
            var resulting = outputRoles.AllData().ToList();
            var summary = new StringBuilder();
            summary.AppendLine("wsp_Pp15_Assign Print Path Roles");
            summary.AppendLine($"mapping: {mappingMode}");
            summary.AppendLine($"overwrite: {overwrite}");
            summary.AppendLine($"branches: {paths.Count}");
            summary.AppendLine($"changed: {changed}");
            summary.AppendLine($"protected existing roles: {protectedExisting}");
            summary.AppendLine($"unassigned by exact tree: {unassignedByTree}");
            summary.AppendLine(
                $"roles: Undefined={resulting.Count(value => value == 0)}, " +
                $"Shell={resulting.Count(value => value == 1)}, " +
                $"Infill={resulting.Count(value => value == 2)}, " +
                $"Partition={resulting.Count(value => value == 3)}, " +
                $"Support={resulting.Count(value => value == 4)}, " +
                $"Transition={resulting.Count(value => value == 5)}");
            summary.Append("geometry/process/analysis data preserved: yes");

            if (changed == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    overwrite
                        ? "No branch role changed."
                        : "No Undefined branch role changed. Existing semantic roles were preserved.");
            }

            da.SetData(0, new WasperPrintPathGoo(output));
            da.SetData(1, summary.ToString());
            WasperPathDebugOutputs.SetCore(da, this, output);
        }

        private WasperPrintPath ReadPath(IGH_DataAccess da)
        {
            object raw = null;
            if (!da.GetData(0, ref raw) || raw == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "wsp_path is required. Please use the Pp01 WASPer Path from Curves before using this component.");
                return null;
            }
            if (raw is WasperPrintPath path)
                return path;
            if (raw is WasperPrintPathGoo goo && goo.Value != null)
                return goo.Value;
            if (raw is GH_ObjectWrapper wrapper)
            {
                if (wrapper.Value is WasperPrintPath wrappedPath)
                    return wrappedPath;
                if (wrapper.Value is WasperPrintPathGoo wrappedGoo && wrappedGoo.Value != null)
                    return wrappedGoo.Value;
            }
            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Error,
                "wsp_path must be a WASPer Print Path. Please use the Pp01 WASPer Path from Curves before using this component.");
            return null;
        }

        private static class AddTagIcon
        {
            private static readonly Lazy<Bitmap> Cached = new Lazy<Bitmap>(Create, true);
            public static Bitmap Bitmap => Cached.Value;

            private static Bitmap Create()
            {
                var bitmap = new Bitmap(24, 24);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    graphics.Clear(Color.Transparent);
                    using (var pathPen = new Pen(Color.FromArgb(35, 72, 86), 2.2f))
                    using (var tagBrush = new SolidBrush(Color.FromArgb(236, 147, 35)))
                    using (var holeBrush = new SolidBrush(Color.White))
                    {
                        graphics.DrawBezier(pathPen, 2, 18, 7, 7, 14, 19, 22, 7);
                        var tag = new[]
                        {
                            new PointF(5, 4),
                            new PointF(15, 4),
                            new PointF(20, 9),
                            new PointF(14, 15),
                            new PointF(5, 15)
                        };
                        graphics.FillPolygon(tagBrush, tag);
                        graphics.FillEllipse(holeBrush, 7, 7, 3.5f, 3.5f);
                    }
                }
                return bitmap;
            }
        }
    }
}
