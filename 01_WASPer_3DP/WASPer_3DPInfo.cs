using Grasshopper;
using Grasshopper.Kernel;
using System;
using System.Drawing;

namespace WASPer_3DP
{
    public class WASPer_3DPInfo : GH_AssemblyInfo
    {
        public override string Name => "WASPer_3DP";

        //Return a 24x24 pixel bitmap to represent this GHA library.
        public override System.Drawing.Bitmap Icon
        {
            get
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.01_WASPer_3DP.png"))
                {
                    return stream != null ? new System.Drawing.Bitmap(stream) : null;
                }
            }
        }

        //Return a short string describing the purpose of this GHA library.
        public override string Description => "\"Created by Juan Diego Vargas.\\n\" +\r\n            " +
            "\"WASPer_3DP is a FREE and OPEN-SOURCE Grasshopper plugin for Marlin 3D printing using LDM technologies like the WASP40100.\\n\" +\r\n            " +
            "\"It includes tools for G-code generation, slicing, path optimization, water need estimation,\\n\" +\r\n            " +
            "\"and hygrothermal property assessments of materials like clay, concrete, and geopolymers.\\n\\n\" +\r\n            " +
            "\"The plugin also includes components for material characterization (porosity, tortuosity, etc.)\\n\" +\r\n            " +
            "\"and simulation tools for heat and moisture transfer, enabling a complete workflow from design to assessment.\"";

        public override Guid Id => new Guid("4ed63570-8a37-48cf-95c5-4b5cabc846b1");

        //Return a string identifying you or your company.
        public override string AuthorName => "Juan Diego Vargas";

        //Return a string representing your preferred contact details.
        public override string AuthorContact => "";

        //Return a string representing the version.  This returns the same version as the assembly.
        public override string AssemblyVersion => GetType().Assembly.GetName().Version.ToString();
    }
}