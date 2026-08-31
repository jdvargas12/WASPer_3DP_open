using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Text;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Parameters;
using Grasshopper.Kernel.Types;

using Rhino.Geometry;

namespace WASPer_3DP.Components._3_0_Slicing
{
    public sealed class wsp_Sl08_Assign_Curve_Roles : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Sl08_Assign_Curve_Roles()
          : base(
                "wsp_Sl08_Assign Curve Roles",
                "Curve Roles",
                "Assigns WASPer printing roles to external Curve DataTrees so they can participate in the role-aware WASPer workflow.\r\n" +
                "Curves are duplicated; the input objects are never mutated. Tree paths and item order are preserved.\r\n" +
                "One path_role value broadcasts to every branch. Multiple flattened values cycle by input branch.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "3.0_Slicing")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version != null
                ? $"v{version.Major}.{version.Minor}.{version.Build}"
                : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("4BB166BF-9558-4940-862A-012A0511E148");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon
        {
            get
            {
                var bitmap = new Bitmap(24, 24);
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.Clear(Color.Transparent);
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;

                    using (var pathPen = new Pen(Color.Black, 2.2f))
                    {
                        pathPen.StartCap = LineCap.Round;
                        pathPen.EndCap = LineCap.Round;
                        graphics.DrawBezier(
                            pathPen,
                            new PointF(2.5f, 17.5f),
                            new PointF(7.0f, 5.0f),
                            new PointF(14.0f, 20.0f),
                            new PointF(21.5f, 7.0f));
                    }

                    using (var shellBrush = new SolidBrush(Color.FromArgb(205, 45, 38)))
                    using (var infillBrush = new SolidBrush(Color.FromArgb(237, 154, 24)))
                    using (var partitionBrush = new SolidBrush(Color.FromArgb(15, 139, 135)))
                    {
                        graphics.FillEllipse(shellBrush, 2.0f, 15.0f, 5.0f, 5.0f);
                        graphics.FillEllipse(infillBrush, 9.5f, 10.0f, 5.0f, 5.0f);
                        graphics.FillEllipse(partitionBrush, 17.0f, 4.5f, 5.0f, 5.0f);
                    }
                }

                return bitmap;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddCurveParameter(
                "path_curves",
                "path_crvs",
                "External Curve DataTree to tag. Curves may come from any Grasshopper or Rhino workflow. Paths, branch order, and item order are preserved.",
                GH_ParamAccess.tree);

            int roleIndex = p.AddIntegerParameter(
                "path_role",
                "role",
                "Flattened role list assigned by branch: 0 Undefined, 1 Shell, 2 Infill, 3 Partition, 4 Support, 5 Transition. One value broadcasts; multiple values cycle through input branches.",
                GH_ParamAccess.list,
                (int)global::WASPer_3DP.WasperPathRole.Shell);
            p[roleIndex].DataMapping = GH_DataMapping.Flatten;

            if (p[roleIndex] is Param_Integer roleParameter)
            {
                roleParameter.AddNamedValue(
                    "Undefined",
                    (int)global::WASPer_3DP.WasperPathRole.Undefined);
                roleParameter.AddNamedValue(
                    "Shell",
                    (int)global::WASPer_3DP.WasperPathRole.Shell);
                roleParameter.AddNamedValue(
                    "Infill",
                    (int)global::WASPer_3DP.WasperPathRole.Infill);
                roleParameter.AddNamedValue(
                    "Partition",
                    (int)global::WASPer_3DP.WasperPathRole.Partition);
                roleParameter.AddNamedValue(
                    "Support",
                    (int)global::WASPer_3DP.WasperPathRole.Support);
                roleParameter.AddNamedValue(
                    "Transition",
                    (int)global::WASPer_3DP.WasperPathRole.Transition);
            }
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddCurveParameter(
                "tagged_curves",
                "tagged_crvs",
                "Duplicated curves carrying the selected WASPer.PathRole. Input tree paths and item order are preserved.",
                GH_ParamAccess.tree);
            p.AddTextParameter(
                "summary",
                "summary",
                "Assignment counts by role, plus processed branch and curve totals.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_Structure<GH_Curve> input = null;
            var roleValues = new List<int>();
            if (!DA.GetDataTree(0, out input) || input == null)
                return;
            if (!DA.GetDataList(1, roleValues))
                return;

            if (roleValues.Count == 0)
                roleValues.Add((int)global::WASPer_3DP.WasperPathRole.Shell);

            foreach (int value in roleValues)
            {
                if (value < (int)global::WASPer_3DP.WasperPathRole.Undefined ||
                    value > (int)global::WASPer_3DP.WasperPathRole.Transition)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        $"Unsupported path_role value {value}. Use 0 Undefined, 1 Shell, 2 Infill, 3 Partition, 4 Support, or 5 Transition.");
                    return;
                }
            }

            var output = new GH_Structure<GH_Curve>();
            var counts = new Dictionary<global::WASPer_3DP.WasperPathRole, int>
            {
                [global::WASPer_3DP.WasperPathRole.Undefined] = 0,
                [global::WASPer_3DP.WasperPathRole.Shell] = 0,
                [global::WASPer_3DP.WasperPathRole.Infill] = 0,
                [global::WASPer_3DP.WasperPathRole.Partition] = 0,
                [global::WASPer_3DP.WasperPathRole.Support] = 0,
                [global::WASPer_3DP.WasperPathRole.Transition] = 0
            };

            int skippedNull = 0;
            for (int branchIndex = 0; branchIndex < input.PathCount; branchIndex++)
            {
                GH_Path path = input.Paths[branchIndex];
                IList<GH_Curve> branch = input.Branches[branchIndex];
                var role = (global::WASPer_3DP.WasperPathRole)
                    roleValues[branchIndex % roleValues.Count];

                output.EnsurePath(path);
                foreach (GH_Curve goo in branch)
                {
                    Curve source = goo?.Value;
                    if (source == null)
                    {
                        skippedNull++;
                        continue;
                    }

                    Curve duplicate = source.DuplicateCurve();
                    if (duplicate == null)
                    {
                        skippedNull++;
                        continue;
                    }

                    global::WASPer_3DP.WasperPathRoleMetadata.Set(duplicate, role);
                    output.Append(new GH_Curve(duplicate), path);
                    counts[role]++;
                }
            }

            if (skippedNull > 0)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{skippedNull} null or non-duplicable curve item(s) were skipped.");

            var summary = new StringBuilder();
            summary.AppendLine("wsp_Sl08_Assign Curve Roles");
            summary.AppendLine($"branches: {input.PathCount}");
            summary.AppendLine($"tagged curves: {counts.Values.Sum()}");
            summary.AppendLine($"Undefined: {counts[global::WASPer_3DP.WasperPathRole.Undefined]}");
            summary.AppendLine($"Shell: {counts[global::WASPer_3DP.WasperPathRole.Shell]}");
            summary.AppendLine($"Infill: {counts[global::WASPer_3DP.WasperPathRole.Infill]}");
            summary.AppendLine($"Partition: {counts[global::WASPer_3DP.WasperPathRole.Partition]}");
            summary.AppendLine($"Support: {counts[global::WASPer_3DP.WasperPathRole.Support]}");
            summary.AppendLine($"Transition: {counts[global::WASPer_3DP.WasperPathRole.Transition]}");
            summary.Append($"skipped: {skippedNull}");

            DA.SetDataTree(0, output);
            DA.SetData(1, summary.ToString());
            Message = _versionTag + " | " + counts.Values.Sum() + " tagged";
        }
    }
}
