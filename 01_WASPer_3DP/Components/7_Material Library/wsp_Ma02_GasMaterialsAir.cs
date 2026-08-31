using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Grasshopper.Kernel;
using WASPer_3DP;

public sealed class wsp_Ma02_GasMaterialsAir : GH_Component
{
    private sealed class AirRow
    {
        public AirRow(double t, double density, double cp, double k, double alpha, double mu, double nu, double pr)
        { T = t; Density = density; Cp = cp; K = k; Alpha = alpha; Mu = mu; Nu = nu; Pr = pr; }
        public double T, Density, Cp, K, Alpha, Mu, Nu, Pr;
    }

    private static readonly AirRow[] Air =
    {
        new(-150,2.866,983,.01171,4.158e-6,8.636e-6,3.013e-6,.7246), new(-100,2.038,966,.01582,8.036e-6,1.189e-5,5.837e-6,.7263),
        new(-50,1.582,999,.01979,1.252e-5,1.474e-5,9.319e-6,.7440), new(-40,1.514,1002,.02057,1.356e-5,1.527e-5,1.008e-5,.7436),
        new(-30,1.451,1004,.02134,1.465e-5,1.579e-5,1.087e-5,.7425), new(-20,1.394,1005,.02211,1.578e-5,1.630e-5,1.169e-5,.7408),
        new(-10,1.341,1006,.02288,1.696e-5,1.680e-5,1.252e-5,.7387), new(0,1.292,1006,.02364,1.818e-5,1.729e-5,1.338e-5,.7362),
        new(5,1.269,1006,.02401,1.880e-5,1.754e-5,1.382e-5,.7350), new(10,1.246,1006,.02439,1.944e-5,1.778e-5,1.426e-5,.7336),
        new(15,1.225,1007,.02476,2.009e-5,1.802e-5,1.470e-5,.7323), new(20,1.204,1007,.02514,2.074e-5,1.825e-5,1.516e-5,.7309),
        new(25,1.184,1007,.02551,2.141e-5,1.849e-5,1.562e-5,.7296), new(30,1.164,1007,.02588,2.208e-5,1.872e-5,1.608e-5,.7282),
        new(35,1.145,1007,.02625,2.277e-5,1.895e-5,1.655e-5,.7268), new(40,1.127,1007,.02662,2.346e-5,1.918e-5,1.702e-5,.7255),
        new(45,1.109,1007,.02699,2.416e-5,1.941e-5,1.750e-5,.7241), new(50,1.092,1007,.02735,2.487e-5,1.963e-5,1.798e-5,.7228),
        new(60,1.059,1008,.02808,2.632e-5,2.008e-5,1.896e-5,.7202), new(70,1.028,1008,.02881,2.780e-5,2.052e-5,1.995e-5,.7177),
        new(80,.9994,1008,.02953,2.931e-5,2.096e-5,2.097e-5,.7154), new(90,.9718,1008,.03024,3.086e-5,2.139e-5,2.201e-5,.7132),
        new(100,.9458,1009,.03095,3.243e-5,2.181e-5,2.306e-5,.7111), new(120,.8977,1011,.03235,3.565e-5,2.264e-5,2.522e-5,.7073),
        new(140,.8542,1013,.03374,3.898e-5,2.345e-5,2.745e-5,.7041), new(160,.8148,1016,.03511,4.241e-5,2.420e-5,2.975e-5,.7014),
        new(180,.7788,1019,.03646,4.593e-5,2.504e-5,3.212e-5,.6992), new(200,.7459,1023,.03779,4.954e-5,2.577e-5,3.455e-5,.6974),
        new(250,.6746,1033,.04104,5.890e-5,2.760e-5,4.091e-5,.6946), new(300,.6158,1044,.04418,6.871e-5,2.946e-5,4.765e-5,.6935),
        new(350,.5664,1056,.04721,7.892e-5,3.101e-5,5.475e-5,.6948), new(400,.5243,1069,.05015,8.951e-5,3.261e-5,6.219e-5,.6948)
    };

    private static readonly string[] Headers = { "Density (kg/m^3)", "Specific Heat (J/kg-K)", "Thermal Conductivity (W/m-K)", "Thermal Diffusivity (m^2/s)", "Dynamic Viscosity (kg/m-s)", "Kinematic Viscosity (m^2/s)", "Prandtl Number", "Thermal Expansion Coefficient (1/K)" };

    public wsp_Ma02_GasMaterialsAir() : base("wsp_Ma02_Gas Materials (Air)", "Gas Mat", "Creates a reusable WASPer air-material record at a requested temperature and standard atmospheric pressure (101.325 kPa). Thermophysical properties are taken from the nearest available temperature row in the built-in air table; the thermal-expansion coefficient is calculated at the exact requested temperature.", global::WASPer_3DP.WASPerPalette.Performance, "7_Material Library")
    { var v = Assembly.GetExecutingAssembly().GetName().Version; Message = v == null ? "v1.0.x" : $"v{v.Major}.{v.Minor}.{v.Build}"; }

    public override Guid ComponentGuid => new("8D7D44E1-9A2B-46EE-AC4A-58EAD99B5812");
    protected override System.Drawing.Bitmap Icon { get { try { using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ma02_Gas Materials_Air.png"); return s == null ? null : new System.Drawing.Bitmap(s); } catch { return null; } } }

    protected override void RegisterInputParams(GH_InputParamManager p)
    {
        p.AddNumberParameter("temperature", "temp", "Requested air temperature in degrees C. Default: 20. Tabulated properties use the nearest available row from -150 to 400 degrees C; thermal expansion uses this exact input. Values at or below absolute zero are invalid.", GH_ParamAccess.item, 20.0);
        p.AddBooleanParameter("scientific_notation", "sci", "Controls only the formatting of report: true uses scientific notation and false uses fixed decimal notation. It does not alter the numeric prop_vals or wasper_mat data. Default: true.", GH_ParamAccess.item, true);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager p)
    {
        p.AddGenericParameter("wasper_mat", "wasper_mat", "Complete WASPer Material object for air. It contains the gas phase, source, requested and tabulated temperatures, pressure, lookup method, units, and all calculated or selected thermophysical properties for downstream components.", GH_ParamAccess.item);
        p.AddTextParameter("prop_names", "prop_names", "Names and units of the eight returned air properties, ordered to correspond item-by-item with prop_vals.", GH_ParamAccess.list);
        p.AddNumberParameter("prop_vals", "prop_vals", "Numeric values for density, specific heat, thermal conductivity, thermal diffusivity, dynamic viscosity, kinematic viscosity, Prandtl number, and thermal-expansion coefficient, in prop_names order.", GH_ParamAccess.list);
        p.AddTextParameter("report", "report", "Human-readable multiline summary of the requested temperature, standard pressure, and all properties. Formatting is controlled by scientific_notation.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess da)
    {
        double temp = 20.0; bool scientific = true;
        if (!da.GetData(0, ref temp) || !da.GetData(1, ref scientific)) return;
        if (temp <= -273.15) { AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Temperature must be above absolute zero (-273.15 °C)."); return; }

        AirRow row = Air.OrderBy(x => Math.Abs(x.T - temp)).First();
        var values = new[] { row.Density, row.Cp, row.K, row.Alpha, row.Mu, row.Nu, row.Pr, 1.0 / (temp + 273.15) };
        string F(double x) => x.ToString(scientific ? "0.000000e+00" : "0.000000", CultureInfo.InvariantCulture);
        var properties = Headers.Zip(values, (h, v) => new { h, v }).ToDictionary(x => x.h, x => x.v.ToString("R", CultureInfo.InvariantCulture));
        properties["Requested Temperature (°C)"] = temp.ToString("R", CultureInfo.InvariantCulture);
        properties["Tabulated Temperature (°C)"] = row.T.ToString("R", CultureInfo.InvariantCulture);
        properties["Pressure (kPa)"] = "101.325";
        properties["Lookup Method"] = "Nearest tabulated temperature";
        var material = new WasperMaterial("Air", "Gas", properties, "Air properties table at 101.325 kPa");
        string report = $"Air Properties at Temperature = {temp:G}°C and Pressure = 101.325 kPa\n" + string.Join("\n", Headers.Zip(values, (h, v) => $"{h} = {F(v)}"));

        da.SetData(0, new WasperMaterialGoo(material));
        da.SetDataList(1, Headers);
        da.SetDataList(2, values);
        da.SetData(3, report);
    }
}
