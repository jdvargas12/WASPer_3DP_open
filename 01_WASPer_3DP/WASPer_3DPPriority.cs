using System.Drawing;
using System.Reflection;
using Grasshopper;
using Grasshopper.Kernel;

namespace WASPer_3DP
{
    /// <summary>
    /// Registers the Grasshopper ribbon TAB icons for the WASPer palette split
    /// (WASPerPalette.DesignFabrication / WASPerPalette.Performance).
    ///
    /// This is intentionally separate from WASPer_3DPInfo.Icon: GH_AssemblyInfo.Icon only
    /// supplies the plugin/package icon shown in the Package Manager and the About component.
    /// The small icon shown next to a category name at the top of the Grasshopper ribbon is a
    /// different registration - Instances.ComponentServer.AddCategoryIcon/AddCategorySymbolName -
    /// and Grasshopper only picks it up reliably when it is called from a GH_AssemblyPriority
    /// override, before components are added to the ribbon.
    /// </summary>
    public class WASPer_3DPPriority : GH_AssemblyPriority
    {
        public override GH_LoadingInstruction PriorityLoad()
        {
            // "WASPer_3DP" keeps the original yellow WASPer glyph.
            RegisterTabIcon(WASPerPalette.DesignFabrication, "WASPer_3DP.Resources.Icons.01_WASPer_3DP.png", 'W');

            // "WASPerformance" reuses the same glyph with the yellow re-hued to red.
            RegisterTabIcon(WASPerPalette.Performance, "WASPer_3DP.Resources.Icons.02_WASPer_Performance.png", 'P');

            // The WASPet is optional UI. A platform-specific UI failure must never prevent
            // Grasshopper from loading the WASPer components.
            try
            {
                WasperMascotManager.Initialize();
            }
            catch
            {
                // Keep loading the plugin without the mascot.
            }

            return GH_LoadingInstruction.Proceed;
        }

        private static void RegisterTabIcon(string category, string embeddedResourceName, char fallbackSymbol)
        {
            // Fallback single-letter symbol shown if the bitmap can't be loaded for any reason.
            Instances.ComponentServer.AddCategorySymbolName(category, fallbackSymbol);

            var icon = LoadEmbeddedIcon(embeddedResourceName);
            if (icon != null)
            {
                Instances.ComponentServer.AddCategoryIcon(category, icon);
            }
        }

        private static Bitmap LoadEmbeddedIcon(string resourceName)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (var stream = assembly.GetManifestResourceStream(resourceName))
                {
                    return stream != null ? new Bitmap(stream) : null;
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
