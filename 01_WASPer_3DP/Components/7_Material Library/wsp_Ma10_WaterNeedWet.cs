using System;
using System.Reflection;
using Grasshopper.Kernel;

public sealed class wsp_Ma10_WaterNeedWet : GH_Component
{
    public wsp_Ma10_WaterNeedWet()
        : base("wsp_Ma10_Water Need Wet", "WaterWet",
            "Calculates how much water must be added to a wet clay/material sample to reach a desired total wet-basis water-content fraction. The current sample mass already includes its existing water.", global::WASPer_3DP.WASPerPalette.Performance, "7_Material Library")
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Message = version == null ? "v1.0.x" : $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    public override Guid ComponentGuid => new("A12CC063-BD82-4FB8-A46F-6C4D96AE4137");
    public override GH_Exposure Exposure => GH_Exposure.secondary;

    protected override System.Drawing.Bitmap Icon
    {
        get
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WASPer_3DP.Resources.Icons.05_WaterWet.png");
                return stream == null ? null : new System.Drawing.Bitmap(stream);
            }
            catch { return null; }
        }
    }

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddNumberParameter("water_cont_in", "water_cont_in", "Current wet-basis water-content fraction [-] of the wet sample, normally obtained from Ma09. Example: 0.10 means 10%. Default: 0.10.", GH_ParamAccess.item, 0.10);
        p.AddNumberParameter("water_cont_des", "water_cont_des", "Desired final wet-basis water-content fraction [-]. It must be greater than or equal to water_cont_in and less than 1. Example: 0.26 means 26%. Default: 0.26.", GH_ParamAccess.item, 0.26);
        p.AddNumberParameter("clay_wet_mass", "clay_wet_mass", "Current wet sample mass, including its existing water. Use any mass unit consistently; outputs use the same unit. Default: 1000.", GH_ParamAccess.item, 1000.0);
        p.AddIntegerParameter("decimals", "decimals", "Number of decimal places used to round water_need and new_wet_mass. Valid range: 0-15. Default: 2.", GH_ParamAccess.item, 2);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddNumberParameter("water_need", "water_need", "Additional water mass required to reach water_cont_des, in the same mass unit as clay_wet_mass.", GH_ParamAccess.item);
        p.AddNumberParameter("new_wet_mass", "new_wet_mass", "Final total wet mass after adding water_need, in the same mass unit as clay_wet_mass.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        double current = 0.10, desired = 0.26, wetMass = 1000.0;
        int decimals = 2;
        if (!da.GetData(0, ref current) || !da.GetData(1, ref desired) || !da.GetData(2, ref wetMass) || !da.GetData(3, ref decimals)) return;

        if (!ValidFraction(current) || !ValidFraction(desired))
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "water_cont_in and water_cont_des must be finite fractions in [0, 1).");
            return;
        }
        if (desired < current)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "water_cont_des is lower than water_cont_in. This component adds water; it cannot remove it.");
            return;
        }
        if (!IsFinite(wetMass) || wetMass <= 0.0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "clay_wet_mass must be a finite value greater than zero.");
            return;
        }
        if (decimals < 0 || decimals > 15)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "decimals must be between 0 and 15.");
            return;
        }

        double waterNeed = Math.Round(((desired - current) * wetMass) / (1.0 - desired), decimals, MidpointRounding.AwayFromZero);
        double newWetMass = Math.Round(wetMass + waterNeed, decimals, MidpointRounding.AwayFromZero);
        da.SetData(0, waterNeed);
        da.SetData(1, newWetMass);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
    private static bool ValidFraction(double value) => IsFinite(value) && value >= 0.0 && value < 1.0;
}
