using System;
using System.Reflection;
using System.Text;

using Grasshopper.Kernel;

namespace WASPer_3DP_Components._4_1_Printability
{
    public class wsp_Pr02_3DCPVelocity : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Pr02_3DCPVelocity()
            : base(
                "wsp_Pr02_3DCP Velocity",
                "3DCPSpeed",
                BuildDescription(),
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "4.1_Printability")
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = (v != null) ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("0B0722D6-59A0-44E3-A1B3-9D1E9F25F0B6");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Pr02_3DCP Velocity.png"))
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                }
                catch { }

                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddNumberParameter(
                "Nozzle Height",
                "Hn",
                "Nozzle height above the previous layer in millimetres (mm). Accepts either a scalar or a per-point data tree from Gc01 'layer_h' or Gc05 'opt_layer_h'; Grasshopper data matching preserves the incoming branches in all outputs. For stable layer pressing, Hn approximates filament height.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "Material Flow Velocity",
                "Vm",
                "Material flow velocity through the circular nozzle in millimetres per second (mm/s).",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "Nozzle Diameter",
                "D",
                "Internal diameter of the circular nozzle in millimetres (mm).",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "Target Filament Width",
                "W",
                "Target deposited filament width in millimetres (mm).",
                GH_ParamAccess.item);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddNumberParameter("Nozzle Velocity (mm/s)", "Vn", "Required nozzle velocity Vn in millimetres per second (mm/s).", GH_ParamAccess.item);
            pManager.AddNumberParameter("Nozzle Velocity (mm/min)", "Vn/min", "Required nozzle velocity Vn in millimetres per minute (mm/min), suitable for common G-code workflows.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Nozzle Velocity (m/s)", "Vn(m/s)", "Required nozzle velocity Vn in metres per second (m/s).", GH_ParamAccess.item);
            pManager.AddNumberParameter("Velocity Ratio", "V*", "Dimensionless velocity ratio V* = Vn / Vm.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Nozzle Height Ratio", "H*", "Dimensionless nozzle-height ratio H* = Hn / D.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Filament Width (mm)", "W", "Target filament width W in millimetres (mm), repeated for a self-contained result set.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Filament Width Ratio", "W*", "Dimensionless filament-width ratio W* = W / D.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Contact Width (mm)", "Wc", "Predicted contact width Wc in millimetres (mm), physically bounded to 0 <= Wc <= W.", GH_ParamAccess.item);
            pManager.AddNumberParameter("Contact Width Ratio", "Wc*", "Dimensionless bounded contact-width ratio Wc* = Wc / D.", GH_ParamAccess.item);
            pManager.AddIntegerParameter("Filament Class Estimate", "Class", "Approximate Figure 9 class flag: 0 = outside published classifier range; 1 = inconsistent/poor-contact region; 2 = ideal stable and consistent region; 3 = rounded/stacking-instability region. This is a digitized range estimate, not the unpublished QDA classifier.", GH_ParamAccess.item);
            pManager.AddTextParameter("Validity Information", "info", "Model-domain status, raw contact-width prediction, clamp status, and modelling assumptions.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            double materialFlowVelocity = 0.0;
            double nozzleDiameter = 0.0;
            double filamentWidth = 0.0;
            double nozzleHeight = 0.0;

            if (!DA.GetData(0, ref nozzleHeight)) return;
            if (!DA.GetData(1, ref materialFlowVelocity)) return;
            if (!DA.GetData(2, ref nozzleDiameter)) return;
            if (!DA.GetData(3, ref filamentWidth)) return;

            if (materialFlowVelocity <= 0.0 || nozzleDiameter <= 0.0 || filamentWidth <= 0.0 || nozzleHeight <= 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Vm, D, W, and Hn must all be greater than zero.");
                return;
            }

            double nozzleHeightRatio = nozzleHeight / nozzleDiameter;
            double filamentWidthRatio = filamentWidth / nozzleDiameter;
            double denominator = nozzleHeightRatio * (filamentWidthRatio - 0.0139 - 0.2784 * nozzleHeightRatio);

            if (!double.IsFinite(denominator) || denominator <= 1e-9)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Invalid parameter combination: the target width/height ratios produce a non-positive or unstable model denominator.");
                return;
            }

            double velocityRatio = 0.7188 / denominator;
            double nozzleVelocityMmS = materialFlowVelocity * velocityRatio;

            if (!double.IsFinite(velocityRatio) || velocityRatio <= 0.0 ||
                !double.IsFinite(nozzleVelocityMmS) || nozzleVelocityMmS <= 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The model produced a non-finite or non-positive nozzle velocity.");
                return;
            }

            double rawContactWidthRatio =
                0.7059 / (velocityRatio * nozzleHeightRatio) -
                0.0783 * nozzleHeightRatio -
                0.0935;
            double rawContactWidthMm = rawContactWidthRatio * nozzleDiameter;

            if (!double.IsFinite(rawContactWidthRatio) || !double.IsFinite(rawContactWidthMm))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "The model produced a non-finite contact-width prediction.");
                return;
            }

            double contactWidthMm = Math.Max(0.0, Math.Min(rawContactWidthMm, filamentWidth));
            double contactWidthRatio = contactWidthMm / nozzleDiameter;
            bool clampedLow = rawContactWidthMm < 0.0;
            bool clampedHigh = rawContactWidthMm > filamentWidth;
            bool velocityOutsideDomain = velocityRatio >= 2.0;
            bool heightOutsideDomain = nozzleHeightRatio >= 1.6;
            int filamentClass = EstimateFilamentClass(velocityRatio, nozzleHeightRatio);

            if (velocityOutsideDomain || heightOutsideDomain)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"Prediction is outside the paper's documented confidence range (V* < 2.0, Hn* < 1.6). Current: V*={velocityRatio:F4}, Hn*={nozzleHeightRatio:F4}.");
            }

            if (clampedLow)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"Predicted contact width was negative ({rawContactWidthMm:F4} mm) and has been clamped to 0 mm. This may indicate poor or unstable interlayer contact.");
            }
            else if (clampedHigh)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"Predicted contact width ({rawContactWidthMm:F4} mm) exceeded filament width W ({filamentWidth:F4} mm) and has been clamped to W.");
            }

            double nozzleVelocityMmMin = nozzleVelocityMmS * 60.0;
            double nozzleVelocityMS = nozzleVelocityMmS / 1000.0;

            var validity = new StringBuilder();
            validity.AppendLine("Alhussain et al. (2024) analytical-regression model");
            validity.AppendLine($"Model domain: {(velocityOutsideDomain || heightOutsideDomain ? "OUTSIDE documented confidence range" : "inside documented confidence range")}");
            validity.AppendLine($"V*={velocityRatio:G8} (documented V* < 2.0)");
            validity.AppendLine($"Hn*={nozzleHeightRatio:G8} (documented Hn* < 1.6)");
            validity.AppendLine($"Raw Wc*={rawContactWidthRatio:G8}");
            validity.AppendLine($"Raw Wc={rawContactWidthMm:G8} mm");
            validity.AppendLine($"Bounded Wc={contactWidthMm:G8} mm; bounded Wc*={contactWidthRatio:G8}");
            validity.AppendLine($"Clamp: {(clampedLow ? "clamped to 0" : clampedHigh ? "clamped to W" : "none")}");
            validity.AppendLine($"Class estimate: {filamentClass} ({DescribeFilamentClass(filamentClass)})");
            validity.AppendLine("Classifier note: approximate quadratic boundaries digitized from Figure 9; published QDA coefficients are unavailable.");
            validity.Append("Assumptions: layer pressing, circular nozzle, and buildable material.");

            DA.SetData(0, nozzleVelocityMmS);
            DA.SetData(1, nozzleVelocityMmMin);
            DA.SetData(2, nozzleVelocityMS);
            DA.SetData(3, velocityRatio);
            DA.SetData(4, nozzleHeightRatio);
            DA.SetData(5, filamentWidth);
            DA.SetData(6, filamentWidthRatio);
            DA.SetData(7, contactWidthMm);
            DA.SetData(8, contactWidthRatio);
            DA.SetData(9, filamentClass);
            DA.SetData(10, validity.ToString());

            Message = _versionTag;
        }

        private static int EstimateFilamentClass(double velocityRatio, double nozzleHeightRatio)
        {
            // Supplementary Table 1 experimental range used to train/plot the classifier.
            if (velocityRatio < 0.23 || velocityRatio > 1.84 ||
                nozzleHeightRatio < 0.23 || nozzleHeightRatio > 1.23)
                return 0;

            // Quadratic least-squares fits digitized from the two QDA region boundaries in Figure 9.
            // These reproduce the published plot for transparent range flagging; they are not QDA coefficients.
            double class1UpperBoundary =
                -0.50237176 * velocityRatio * velocityRatio -
                0.13081588 * velocityRatio +
                0.78832893;

            double class3LowerBoundary =
                0.18479071 * velocityRatio * velocityRatio -
                0.85462930 * velocityRatio +
                1.57075747;

            if (nozzleHeightRatio < class1UpperBoundary) return 1;
            if (nozzleHeightRatio > class3LowerBoundary) return 3;
            return 2;
        }

        private static string DescribeFilamentClass(int filamentClass)
        {
            switch (filamentClass)
            {
                case 1: return "inconsistent / poor-contact region";
                case 2: return "ideal stable and consistent region";
                case 3: return "rounded / stacking-instability region";
                default: return "outside published classifier range";
            }
        }

        private static string BuildDescription()
        {
            return
                "Predict nozzle velocity, interlayer contact width, and an approximate filament-class flag for layer-pressed 3D concrete printing from nozzle height, material flow velocity, circular-nozzle diameter, and target filament width. " +
                "The analytical-regression equations are from Alhussain et al. (2024), 'Developing a data-driven filament shape prediction and classification model for extrusion-based 3D printing'. " +
                "The model reports dimensionless ratios separately from dimensional values. Hn accepts scalar values or the per-point layer_h/opt_layer_h trees from Gc01/Gc05, with output paths preserved by Grasshopper data matching.\n\n" +
                "IMPORTANT LIMITATIONS:\n" +
                "- Applicable to the layer-pressing approach with a circular nozzle and buildable material; it does not directly represent infinite-brick/free-flow extrusion or non-circular nozzles.\n" +
                "- Regression predictions lose documented accuracy at V* >= 2.0 or H* >= 1.6. Values are still returned, but the component issues a warning.\n" +
                "- The Class output is only assigned inside the experimental classifier range 0.23 <= V* <= 1.84 and 0.23 <= H* <= 1.23; outside this range Class = 0.\n" +
                "- The paper does not publish the trained QDA coefficients. Classes 1-3 are approximate range flags based on quadratic fits digitized from Figure 9, not the original QDA classifier.\n" +
                "- Treating nozzle height Hn as filament height is most reliable for stable, consistent Class 2 filaments.\n" +
                "- Contact width is bounded to 0 <= Wc <= W for operational use. The unbounded regression result and any clamp reason are reported in info.\n\n" +
                "Component implemented in WASPer_3DP from the Pondskater 3DCP Velocity component with attribution to Filipe Brandao, Lab2PT, University of Minho.";
        }
    }
}
