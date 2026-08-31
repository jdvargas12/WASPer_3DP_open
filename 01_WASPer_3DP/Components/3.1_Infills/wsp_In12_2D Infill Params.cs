using System;
using System.Reflection;
using Grasshopper.Kernel;

namespace WASPer_3DP.Components._3_1_Infills
{
    public sealed class wsp_In12_2D_Infill_Params : GH_Component
    {
        private readonly string _versionTag;

        public wsp_In12_2D_Infill_Params()
            : base(
                "wsp_In12_2D Infill Params (Layered)",
                "2D Params",
                "Packs a lightweight 2D centreline definition for wsp_In10 Layered Multi-Infill. Type 1 restores the original open square-wave / Square-S path; the component does not create volumetric ribs.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "3.1_Infills")
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("93B72042-D072-487E-8F21-8AAEF688497E");
        public override GH_Exposure Exposure => GH_Exposure.secondary;
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_In12_Planar Infill Params.png");
                    return stream == null ? null : new System.Drawing.Bitmap(stream);
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddIntegerParameter("type", "type", "2D centreline pattern: 1=Square S/open square wave, 2=Sticks, 3=Triangle, 4=Sine.", GH_ParamAccess.item, 4);
            p.AddBooleanParameter("flip", "flip", "Flip the path direction across the guide domain. false = guide A to guide B; true = guide B to guide A.", GH_ParamAccess.item, false);
            p.AddIntegerParameter("count", "count", "Number of pattern cells along the guides. Must be >= 1.", GH_ParamAccess.item, 4);
            p.AddNumberParameter("phase_shift", "phase", "Normalized phase offset. 0.5 is a half-cell shift; 1 wraps to 0.", GH_ParamAccess.item, 0.0);
            for (int i = 0; i < p.ParamCount; i++) p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddGenericParameter("2D_infill_params", "2D_p", "Typed 2D centreline parameters for wsp_In10 Layered Multi-Infill. One value broadcasts; a list can assign/cycle by guide domain.", GH_ParamAccess.item);

        protected override void SolveInstance(IGH_DataAccess da)
        {
            int type = 4;
            bool flip = false;
            int count = 4;
            double phase = 0.0;
            da.GetData(0, ref type);
            da.GetData(1, ref flip);
            da.GetData(2, ref count);
            da.GetData(3, ref phase);

            var value = new WasperInfill2DParams
            {
                Type = type,
                Flip = flip,
                Count = count,
                PhaseShift = phase
            };

            string error = value.Validate();
            if (error != null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error);
                return;
            }

            Message = $"{_versionTag} | {WasperInfill2DParams.Tag(type)} x{count}";
            da.SetData(0, new WasperInfill2DParamsGoo(value));
        }
    }
}
