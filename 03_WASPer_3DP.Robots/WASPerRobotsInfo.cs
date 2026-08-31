using System;
using System.Drawing;

using Grasshopper.Kernel;

namespace WASPer_3DP.RobotsIntegration
{
    public sealed class WASPerRobotsInfo : GH_AssemblyInfo
    {
        public override string Name => "WASPer_3DP.Robots";
        public override string Description =>
            "Optional Robots integration components for WASPer_3DP. " +
            "Requires the Robots Grasshopper plugin.";
        public override Guid Id =>
            new Guid("B8D6D697-D369-49FB-890B-59CC57B9F45F");
        public override string AuthorName => "Juan Diego Vargas";
        public override string AuthorContact =>
            "https://www.linkedin.com/in/juan-diego-vargas-vel/";
        public override Bitmap Icon => null;
    }
}
