using System;
using System.Reflection;
using Grasshopper.Kernel;

namespace WASPer_3DP.Components._3_1_Infills
{
    public sealed class wsp_In14_Polyhedral_Infill_Params : GH_Component
    {
        private readonly string _versionTag;

        public wsp_In14_Polyhedral_Infill_Params()
            : base(
                "wsp_In14_Polyhedral Infill Params (Volumetric)",
                "Poly Params",
                "Packs one polyhedral-cell definition into a typed WASPer volumetric infill-parameter object.\n" +
                    "Create one object to broadcast across wsp_In08/In09 volumetric domains, or one object per surface-pair domain. " +
                    "Thickness, shell, partitions, trimming, resolution, and meshing remain generator controls.",
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
            new Guid("750DB680-4475-41CC-BF88-CA9AECD769A1");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_In14_Polyhedral Infill Params.png");
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
                "type",
                "type",
                "Polyhedral cell family: 0 = Truncated Octahedron (BCC, space-filling family); " +
                "1 = Octahedron (SC diagonal-face family).",
                GH_ParamAccess.item,
                1);

            p.AddIntegerParameter(
                "count_x",
                "cx",
                "Integer cell repetitions along the volumetric domain's local X/U direction. Must be >= 1.",
                GH_ParamAccess.item,
                3);

            p.AddIntegerParameter(
                "count_y",
                "cy",
                "Integer cell repetitions along the volumetric domain's local Y/V direction. Must be >= 1.",
                GH_ParamAccess.item,
                3);

            p.AddIntegerParameter(
                "count_z",
                "cz",
                "Integer cell repetitions along the volumetric domain's local Z/W direction. Must be >= 1.",
                GH_ParamAccess.item,
                3);

            p.AddBooleanParameter(
                "invert_polyhedral",
                "invert",
                "Invert the selected polyhedral scalar field so the volumetric generator extracts its complementary region.",
                GH_ParamAccess.item,
                false);

            for (int i = 0; i < p.ParamCount; i++)
                p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddGenericParameter(
                "polyhedral_infill_params",
                "poly_p",
                "Typed polyhedral parameters for wsp_In08 Volumetric Box and wsp_In09 Volumetric Multi-Infill from Surfaces.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            int type = 1;
            int countX = 3;
            int countY = 3;
            int countZ = 3;
            bool invert = false;

            da.GetData(0, ref type);
            da.GetData(1, ref countX);
            da.GetData(2, ref countY);
            da.GetData(3, ref countZ);
            da.GetData(4, ref invert);

            var value = new global::WASPer_3DP.WasperPolyhedralInfillParams
            {
                Type = type,
                CountX = countX,
                CountY = countY,
                CountZ = countZ,
                InvertPolyhedral = invert
            };

            string error = value.Validate();
            if (error != null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error);
                Message = $"{_versionTag} | ERR";
                return;
            }

            Message =
                $"{_versionTag} | {WASPer_3DP.WasperPolyhedralInfillParams.Tag(type)}" +
                $"{(invert ? " | Inv" : "")}";
            da.SetData(
                0,
                new global::WASPer_3DP.WasperPolyhedralInfillParamsGoo(value));
        }
    }
}
