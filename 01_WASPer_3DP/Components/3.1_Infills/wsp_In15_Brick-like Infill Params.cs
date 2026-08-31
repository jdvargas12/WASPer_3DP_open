using System;
using System.Reflection;
using Grasshopper.Kernel;

namespace WASPer_3DP.Components._3_1_Infills
{
    public sealed class wsp_In15_Brick_like_Infill_Params : GH_Component
    {
        private readonly string _versionTag;

        public wsp_In15_Brick_like_Infill_Params()
            : base(
                "wsp_In15_Brick-like Infill Params (Volumetric)",
                "Brick Params",
                "Packs the cavity counts, cavity run direction, and phase inversion for a volumetric Brick-like rib network. Connect it to wsp_In08 or wsp_In09; rib thickness, boundary shell, caps, resolution, trimming, and meshing remain generator controls.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "3.1_Infills")
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version != null
                ? $"v{version.Major}.{version.Minor}.{version.Build}"
                : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("C35F282F-9F26-4560-977D-40844E5AE880");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_In15_Brick-like Infill Params.png");
                    return stream == null ? null : new System.Drawing.Bitmap(stream);
                }
                catch
                {
                    return null;
                }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddIntegerParameter(
                "count_u",
                "c_u",
                "Cavity count in the first direction perpendicular to cav_dir. This is a cavity count, not a rib count. Default 3.",
                GH_ParamAccess.item,
                3);
            p.AddIntegerParameter(
                "count_v",
                "c_v",
                "Cavity count in the second direction perpendicular to cav_dir. This is a cavity count, not a rib count. Default 2.",
                GH_ParamAccess.item,
                2);
            p.AddIntegerParameter(
                "cavity_direction",
                "cav_dir",
                "Cavity run direction in the generator's local domain: 1=local W (grid U/V), 2=local U (grid V/W), 3=local V (grid U/W). For In09, W is the direction between each consecutive surface pair.",
                GH_ParamAccess.item,
                1);
            p.AddBooleanParameter(
                "invert",
                "invert",
                "False selects the internal rib phase. True selects the complementary cavity phase inside the generator boundary.",
                GH_ParamAccess.item,
                false);

            for (int i = 0; i < p.ParamCount; i++)
                p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p) =>
            p.AddGenericParameter(
                "brick_infill_params",
                "brick_p",
                "Typed Brick-like volumetric parameters for wsp_In08 Volumetric Box or wsp_In09 Volumetric Multi-Infill from Surfaces.",
                GH_ParamAccess.item);

        protected override void SolveInstance(IGH_DataAccess da)
        {
            int countU = 3;
            int countV = 2;
            int cavityDirection = 1;
            bool invert = false;
            da.GetData(0, ref countU);
            da.GetData(1, ref countV);
            da.GetData(2, ref cavityDirection);
            da.GetData(3, ref invert);

            var value = new WasperBrickInfillParams
            {
                CountU = countU,
                CountV = countV,
                CavityDirection = cavityDirection,
                Invert = invert
            };

            string error = value.Validate();
            if (error != null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error);
                return;
            }

            Message =
                $"{_versionTag} | {countU}x{countV} " +
                $"{WasperBrickInfillParams.DirectionName(cavityDirection)}" +
                (invert ? " | Inv" : string.Empty);
            da.SetData(0, new WasperBrickInfillParamsGoo(value));
        }
    }
}
