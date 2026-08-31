// -----------------------------------------------------------------------------
//  wsp_Pp05_Define Flow
// -----------------------------------------------------------------------------
//  Packs the flow assignment settings used by Pp01 into a typed flow_p wire.
// -----------------------------------------------------------------------------

#region Usings
using System;
using System.Collections.Generic;
using System.Reflection;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino.Geometry;
#endregion

namespace WASPer_3DP.Components._4_0_Print_Paths
{
    public sealed class wsp_Pp05_Define_Flow : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Pp05_Define_Flow()
          : base(
                "wsp_Pp05_Define Flow",
                "FlowDef",
                "Defines the flow assignment strategy for WASPer print paths.\r\n" +
                "Outputs a packed flow_params / flow_p object consumed by Pp01.\r\n" +
                "Mode 1: global or per-layer flow multipliers.\r\n" +
                "Mode 2: normalized flow profile along each source curve.\r\n" +
                "Mode 3: normalized flow profile sampled along a reference line or curve.\r\n" +
                "Optional target roles restrict the strategy to selected semantic path types.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "4.0_Print Paths")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = $"{_versionTag} - FlowDef";
        }

        public override Guid ComponentGuid => new Guid("17C37290-1683-4F64-98C3-FFFD3270A5D0");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream("WASPer_3DP.Resources.Icons.02_GenGcode.png"))
                        return s != null ? new System.Drawing.Bitmap(s) : null;
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddIntegerParameter(
                "flow_mode",
                "f_mode",
                "Flow assignment mode:\r\n" +
                "1 = m1_flow multipliers (global or per-layer, resolved in Pp01).\r\n" +
                "2 = flow profile along each curve, sampled from m2_m3_flow_range.\r\n" +
                "3 = flow profile along a reference Line or Curve, sampled from m2_m3_flow_range.",
                GH_ParamAccess.item,
                1);

            p.AddNumberParameter(
                "m1_flow",
                "m1_fl",
                "Mode 1 flow multipliers. Use one value for a global multiplier or one value per detected logical layer in Pp01. Ignored by modes 2 and 3.",
                GH_ParamAccess.list,
                1.0);
            p[1].DataMapping = GH_DataMapping.Flatten;

            p.AddNumberParameter(
                "m2_m3_flow_range",
                "m2_m3_fl_range",
                "Flow profile values for modes 2 and 3. A single value acts as a constant profile. Multiple values are linearly interpolated from start to end.",
                GH_ParamAccess.list,
                1.0);
            p[2].DataMapping = GH_DataMapping.Flatten;

            p.AddGenericParameter(
                "flow_crv",
                "flow_crv",
                "Reference Line or Curve used only in flow_mode 3. Pp01 projects each sampled point to this geometry and reads the normalized profile by arc length.",
                GH_ParamAccess.item);
            p[3].Optional = true;

            p.AddBooleanParameter(
                "reverse_crv",
                "rev_crv",
                "When true, reverses flow_crv internally before mode 3 projection/sampling.",
                GH_ParamAccess.item,
                false);

            p.AddParameter(global::WASPer_3DP.WasperTargetRolesParam.Create(
                "Semantic path roles that receive the defined flow in Pp01. " +
                "0 = All paths (default), 1 = Shell, 2 = Infill, 3 = Partition, 4 = Support, 5 = Transition, 6 = Undefined. " +
                "Supply one or several role-specific values to include those roles and exclude " +
                "the others. All paths (0) is mutually exclusive and cannot be combined."));
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "flow_params",
                "flow_p",
                "Packed WASPer flow assignment strategy. Connect to Pp01 flow_p. If omitted in Pp01, flow defaults to 1.",
                GH_ParamAccess.item);

            p.AddTextParameter(
                "summary",
                "summary",
                "Human-readable summary of the packed flow strategy.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            int mode = 1;
            var m1 = new List<double>();
            var profile = new List<double>();
            IGH_Goo referenceInput = null;
            bool reverse = false;
            var targetRoles = new List<int>();

            if (!da.GetData(0, ref mode)) return;
            da.GetDataList(1, m1);
            da.GetDataList(2, profile);
            bool hasReference = da.GetData(3, ref referenceInput);
            if (!da.GetData(4, ref reverse)) return;
            da.GetDataList(5, targetRoles);

            if (!global::WASPer_3DP.WasperGcodeTreeUtil.TryNormalizeTargetRoles(
                    targetRoles,
                    out targetRoles,
                    out string roleError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, roleError);
                return;
            }

            if (mode < 1 || mode > 3)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"flow_mode {mode} is outside [1..3]. Reset to 1.");
                mode = 1;
            }

            Curve referenceCurve = null;
            if (mode == 3)
            {
                if (!hasReference || !TryGetReferenceCurve(referenceInput, out referenceCurve) ||
                    referenceCurve == null || !referenceCurve.IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "flow_mode 3 needs a valid flow_crv. Output is still created, but Pp01 will not solve mode 3 until flow_crv is valid.");
                }
            }

            var flowParams = new global::WASPer_3DP.WasperFlowParams(
                mode,
                m1,
                profile,
                referenceCurve,
                reverse,
                targetRoles);

            da.SetData(0, new global::WASPer_3DP.WasperFlowParamsGoo(flowParams));
            da.SetData(1, flowParams.ToString());
            Message =
                $"{_versionTag}\nFlow mode {mode} | " +
                global::WASPer_3DP.WasperGcodeTreeUtil.TargetRoleNames(targetRoles);
        }

        private static bool TryGetReferenceCurve(IGH_Goo goo, out Curve curve)
        {
            curve = null;

            if (goo is GH_Curve ghCurve && ghCurve.Value != null)
            {
                curve = ghCurve.Value.DuplicateCurve();
                return curve != null && curve.IsValid;
            }

            if (goo is GH_Line ghLine && ghLine.Value.IsValid)
            {
                curve = new LineCurve(ghLine.Value);
                return true;
            }

            if (goo is GH_ObjectWrapper wrapper)
            {
                if (wrapper.Value is Curve objectCurve)
                {
                    curve = objectCurve.DuplicateCurve();
                    return curve != null && curve.IsValid;
                }

                if (wrapper.Value is Line objectLine && objectLine.IsValid)
                {
                    curve = new LineCurve(objectLine);
                    return true;
                }
            }

            return false;
        }
    }
}
