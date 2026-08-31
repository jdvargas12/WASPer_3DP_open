// wsp_Fi3d11_WASPer to Isopod Field.cs
// WASPer_3DP - Subcategory: 2.3_Fields_3D

using System;
using System.Reflection;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

namespace WASPer_3DP.Components._2_3_Fields_3D
{
    public class wsp_Fi3d11_WasperToIsopodField : GH_Component
    {
        private const string Cat = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string Subcategory = "2.3_Fields_3D";
        private readonly string _versionTag;

        public wsp_Fi3d11_WasperToIsopodField()
            : base(
                "wsp_Fi3d11_WASPer to Isopod Field",
                "WASPer -> Isopod",
                "Wraps a WASPer 3D field as a live Isopod Field. " +
                "Isopod must be installed and loaded in the current Grasshopper session.",
                Cat,
                Subcategory)
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version != null
                ? $"v{version.Major}.{version.Minor}.{version.Build}"
                : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("58789AB2-FEE1-42BA-893E-C46B5A36E823");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Fi3d11_WASPer to Isopod Field.png"))
                    using (var bitmap = stream != null ? new System.Drawing.Bitmap(stream) : null)
                        return bitmap != null ? new System.Drawing.Bitmap(bitmap) : null;
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "wasper_field",
                "field",
                "WASPer 3D field to expose as an Isopod Field.",
                GH_ParamAccess.item);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "isopod_field",
                "isopod",
                "Runtime Isopod Field whose ValueAt method evaluates the WASPer field.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "adapter_type",
                "type",
                "Generated Isopod adapter runtime type.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            IGH_Goo goo = null;
            if (!DA.GetData(0, ref goo) || goo == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Connect a WASPer 3D field.");
                return;
            }

            WasperField source = ExtractField(goo);
            if (source?.Evaluator == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input is not a valid WASPer 3D field.");
                return;
            }

            if (!WasperIsopodBridge.TryCreateIsopodField(
                    source,
                    out object isopodField,
                    out string adapterType,
                    out string error))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error);
                return;
            }

            DA.SetData(0, isopodField);
            DA.SetData(1, adapterType);
            Message = _versionTag + " | linked";
        }

        private static WasperField ExtractField(IGH_Goo goo)
        {
            object value = WasperIsopodBridge.Unwrap(goo);

            if (value is WasperField field)
                return field;

            if (value is WasperFieldGoo fieldGoo)
                return fieldGoo.Value;

            return null;
        }
    }
}
