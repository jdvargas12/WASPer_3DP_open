// wsp_Fi3d08_Field History.cs
// WASPer_3DP - Subcategory: 2.3_Fields_3D
//
// Reads provenance metadata carried by WasperField objects.

using System;
using System.Collections.Generic;
using System.Reflection;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino.Geometry;

namespace WASPer_3DP.Components._2_3_Fields_3D
{
    public class wsp_Fi3d08_FieldHistory : GH_Component
    {
        private const string NAME = "wsp_Fi3d08_Field History";
        private const string NICK = "Field History";
        private const string CAT = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "2.3_Fields_3D";

        private readonly string _versionTag;

        public wsp_Fi3d08_FieldHistory()
            : base(
                NAME,
                NICK,
                "Inspects WASPer 3D field provenance, SDF quality, and stacked operation history.",
                CAT,
                SUBCAT)
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("B155B7F3-75C0-4C58-9B4E-7A2C9326B5F8");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Fi3d08_Field History.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch { }

                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "fields",
                "fields",
                "WASPer 3D field or fields to inspect.",
                GH_ParamAccess.list);
            pManager[0].DataMapping = GH_DataMapping.Flatten;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddTextParameter(
                "labels",
                "labels",
                "Field labels.",
                GH_ParamAccess.list);

            pManager.AddTextParameter(
                "sdf_quality",
                "quality",
                "Tracked SDF quality: ExactSdf, ApproximateSdf, ImplicitScalarField, or Unknown.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter(
                "operation_count",
                "ops",
                "Number of tracked operations applied after the source field.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter(
                "curve_thicken_count",
                "curve_ops",
                "Number of tracked curve-thickening operations in the field history.",
                GH_ParamAccess.list);

            pManager.AddTextParameter(
                "trace",
                "trace",
                "Full operation trace for each field.",
                GH_ParamAccess.list);

            pManager.AddTextParameter(
                "info",
                "info",
                "Compact field-history diagnostics.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            var goos = new List<IGH_Goo>();
            if (!DA.GetDataList(0, goos) || goos.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No field input provided.");
                return;
            }

            var fields = new List<WasperField>();
            int rejected = 0;
            foreach (var goo in goos)
            {
                var field = ExtractField(goo);
                if (field != null && field.Evaluator != null) fields.Add(field);
                else rejected++;
            }

            if (rejected > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"{rejected} input item(s) were not valid WASPer fields and were ignored.");

            if (fields.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No valid WASPer fields found.");
                return;
            }

            var labels = new List<string>();
            var qualities = new List<string>();
            var operationCounts = new List<int>();
            var curveCounts = new List<int>();
            var traces = new List<string>();
            var infoLines = new List<string>();

            int stackedCurveFields = 0;
            int implicitFields = 0;

            for (int i = 0; i < fields.Count; i++)
            {
                WasperField field = fields[i];
                string label = string.IsNullOrEmpty(field.Label) ? "field_" + i : field.Label;
                string quality = field.SdfQuality.ToString();
                string trace = string.IsNullOrWhiteSpace(field.OperationTrace)
                    ? "(no trace)"
                    : field.OperationTrace;

                labels.Add(label);
                qualities.Add(quality);
                operationCounts.Add(field.OperationCount);
                curveCounts.Add(field.CurveThickenCount);
                traces.Add(trace);

                if (field.CurveThickenCount > 1)
                    stackedCurveFields++;
                if (field.SdfQuality == WasperFieldSdfQuality.ImplicitScalarField)
                    implicitFields++;

                BoundingBox bb = field.Domain;
                string domain = bb.IsValid
                    ? $"{bb.Min.X:F3},{bb.Min.Y:F3},{bb.Min.Z:F3} -> {bb.Max.X:F3},{bb.Max.Y:F3},{bb.Max.Z:F3}"
                    : "(invalid)";

                infoLines.Add(
                    $"{label}: quality={quality}, ops={field.OperationCount}, " +
                    $"curve_ops={field.CurveThickenCount}, domain={domain}");
            }

            if (stackedCurveFields > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    $"{stackedCurveFields} field(s) have stacked curve-thickening operations.");

            if (implicitFields > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    $"{implicitFields} field(s) are tracked as implicit scalar fields. Mesh extraction is valid, but exact offset/shell distances may be approximate.");

            DA.SetDataList(0, labels);
            DA.SetDataList(1, qualities);
            DA.SetDataList(2, operationCounts);
            DA.SetDataList(3, curveCounts);
            DA.SetDataList(4, traces);
            DA.SetData(5,
                "Field History\n" +
                $"version         : {(Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown")}\n" +
                $"fields          : {fields.Count}\n" +
                $"rejected        : {rejected}\n" +
                $"stacked_curves  : {stackedCurveFields}\n" +
                $"implicit_fields : {implicitFields}\n" +
                string.Join("\n", infoLines));

            Message = $"{_versionTag} | {fields.Count} field";
        }

        private static WasperField ExtractField(IGH_Goo goo)
        {
            if (goo == null) return null;

            if (goo is WasperFieldGoo fg) return fg.Value;

            object sv = null;
            try { sv = goo.ScriptVariable(); } catch { sv = null; }

            if (sv is WasperField f) return f;
            if (sv is WasperFieldGoo fgoo) return fgoo.Value;

            var wrapper = goo as GH_ObjectWrapper;
            if (wrapper != null)
            {
                if (wrapper.Value is WasperField wf) return wf;
                if (wrapper.Value is WasperFieldGoo wg) return wg.Value;
            }

            return null;
        }
    }
}
