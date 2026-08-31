#region Component Description
/*
    Component Name:
        wsp_Ut02_Round Number

    Nickname:
        Round

    Version:
        v1.0.5

    Category / Subcategory:
        WASPer_3DP / 1.0_Utils

    Description:
        Rounds a list of numbers to a specified number of decimal places.

    Inputs:
        num : list of numbers to round
        d   : decimal places. Default is 1.

    Output:
        rn  : rounded numbers
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;

using Grasshopper.Kernel;
#endregion

namespace WASPer_3DP.Components._1_0_Utils
{
    public sealed class wsp_Ut02_Round_Number : GH_Component
    {
        private const string NAME   = "wsp_Ut02_Round Number";
        private const string NICK   = "Round";
        private const string CAT    = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "1.0_Utils";

        private readonly string _versionTag;

        public wsp_Ut02_Round_Number()
            : base(
                NAME,
                NICK,
                "Rounds numbers to a specified number of decimal places.",
                CAT,
                SUBCAT)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("68A13196-13FB-40B8-9660-93F7A2DB0505");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Ut05_Round Number.png"))
                        return stream != null ? new Bitmap(stream) : null;
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter(
                "num", "num",
                "Numbers to round.",
                GH_ParamAccess.list);

            pManager.AddIntegerParameter(
                "d", "d",
                "Number of decimal places. Default is 1.",
                GH_ParamAccess.item, 1);

            pManager[0].Optional = true;
            pManager[1].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter(
                "rn", "rn",
                "Rounded numbers.",
                GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var numbers = new List<double>();
            int decimals = 1;

            DA.GetDataList(0, numbers);
            DA.GetData(1, ref decimals);
            decimals = Math.Max(0, Math.Min(15, decimals));

            var rounded = new List<double>(numbers.Count);
            foreach (double number in numbers)
                rounded.Add(Math.Round(number, decimals));

            DA.SetDataList(0, rounded);
            Message = _versionTag;
        }
    }
}
