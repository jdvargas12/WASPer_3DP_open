using System;
using System.Reflection;
using Grasshopper.Kernel;

public sealed class wsp_Ma11_WaterNeedDry : GH_Component
{
    public wsp_Ma11_WaterNeedDry()
        : base("wsp_Ma11_Water Need Dry", "WaterDry",
            "Calculates how much water must be added to a completely dry clay/material mass to reach a desired final wet-basis water-content fraction.", global::WASPer_3DP.WASPerPalette.Performance, "7_Material Library")
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Message = version == null ? "v1.0.x" : $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    public override Guid ComponentGuid => new("328FF67A-3A00-49C1-A759-40B145F058BD");
    public override GH_Exposure Exposure => GH_Exposure.secondary;

    protected override System.Drawing.Bitmap Icon
    {
        get
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WASPer_3DP.Resources.Icons.07_WaterDry.png");
                return stream == null ? null : new System.Drawing.Bitmap(stream);
            }
            catch { return null; }
        }
    }

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddNumberParameter("water_cont_des", "water_cont_des", "Desired final wet-basis water-content fraction [-]. It must be in [0, 1). Example: 0.26 means 26%. Default: 0.26.", GH_ParamAccess.item, 0.26);
        p.AddNumberParameter("clay_dry_mass", "clay_dry_mass", "Completely dry material mass. Use any mass unit consistently; water_need uses the same unit. Default: 1000.", GH_ParamAccess.item, 1000.0);
        p.AddIntegerParameter("decimals", "decimals", "Number of decimal places used to round water_need. Valid range: 0-15. Default: 2.", GH_ParamAccess.item, 2);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddNumberParameter("water_need", "water_need", "Water mass required to reach water_cont_des, in the same mass unit as clay_dry_mass.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        double desired = 0.26, dryMass = 1000.0;
        int decimals = 2;
        if (!da.GetData(0, ref desired) || !da.GetData(1, ref dryMass) || !da.GetData(2, ref decimals)) return;

        if (!IsFinite(desired) || desired < 0.0 || desired >= 1.0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "water_cont_des must be a finite fraction in [0, 1).");
            return;
        }
        if (!IsFinite(dryMass) || dryMass <= 0.0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "clay_dry_mass must be a finite value greater than zero.");
            return;
        }
        if (decimals < 0 || decimals > 15)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "decimals must be between 0 and 15.");
            return;
        }

        double waterNeed = Math.Round((desired * dryMass) / (1.0 - desired), decimals, MidpointRounding.AwayFromZero);
        da.SetData(0, waterNeed);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
