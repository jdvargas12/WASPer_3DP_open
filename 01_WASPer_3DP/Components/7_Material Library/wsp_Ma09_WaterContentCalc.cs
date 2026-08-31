using System;
using System.Reflection;
using Grasshopper.Kernel;

public sealed class wsp_Ma09_WaterContentCalc : GH_Component
{
    public wsp_Ma09_WaterContentCalc()
        : base("wsp_Ma09_Water Content Calc", "Water Calc",
            "Calculates water mass and wet-basis water content from the measured wet and dry masses of a sample. Water content is returned as a fraction of total wet mass: (mass_wet - mass_dry) / mass_wet.", global::WASPer_3DP.WASPerPalette.Performance, "7_Material Library")
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        Message = version == null ? "v1.0.x" : $"v{version.Major}.{version.Minor}.{version.Build}";
    }

    public override Guid ComponentGuid => new("6B00D5C7-18AE-491E-A8FA-F505B8329209");
    public override GH_Exposure Exposure => GH_Exposure.secondary;

    protected override System.Drawing.Bitmap Icon
    {
        get
        {
            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ma09_Water Content Calc.png");
                return stream == null ? null : new System.Drawing.Bitmap(stream);
            }
            catch { return null; }
        }
    }

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddNumberParameter("mass_wet", "mass_wet", "Wet sample mass before drying. Use any mass unit consistently; water_mass is returned in the same unit. Default: 1.", GH_ParamAccess.item, 1.0);
        p.AddNumberParameter("mass_dry", "mass_dry", "Dry sample mass after removing water, in the same unit as mass_wet. It must be between zero and mass_wet. Default: 0.5.", GH_ParamAccess.item, 0.5);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddNumberParameter("water_cont_in", "water_cont_in", "Wet-basis water-content fraction [-], calculated as water_mass / mass_wet. Multiply by 100 for percent.", GH_ParamAccess.item);
        p.AddNumberParameter("water_mass", "water_mass", "Water mass in the sample, calculated as mass_wet - mass_dry, in the same mass unit as the inputs.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        double wetMass = 1.0, dryMass = 0.5;
        if (!da.GetData(0, ref wetMass) || !da.GetData(1, ref dryMass)) return;

        if (!IsFinite(wetMass) || wetMass <= 0.0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "mass_wet must be a finite value greater than zero.");
            return;
        }
        if (!IsFinite(dryMass) || dryMass < 0.0)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "mass_dry must be a finite non-negative value.");
            return;
        }
        if (dryMass > wetMass)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "mass_dry cannot be greater than mass_wet.");
            return;
        }

        double waterMass = wetMass - dryMass;
        da.SetData(0, waterMass / wetMass);
        da.SetData(1, waterMass);
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}
