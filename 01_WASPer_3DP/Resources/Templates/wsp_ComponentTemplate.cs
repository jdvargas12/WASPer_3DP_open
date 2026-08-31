// -----------------------------------------------------------------------------
//  WASPer_3DP — GH Component Template
// -----------------------------------------------------------------------------

#region Usings
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Rhino;
using Rhino.Geometry;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
#endregion

namespace WASPer_3DP.Components._TEMPLATE
{
    public sealed class wsp_XXYY_ComponentName : GH_Component
    {
        private const string NAME = "wsp_XXYY_Component Name";   // TODO
        private const string NICK = "Short Name";                 // TODO
        private const string DESC = "Description of the component"; // TODO
        private const string CAT = "WASPer_3DP";
        private const string SUBCAT = "X_Group";                    // TODO

        public wsp_XXYY_ComponentName()
            : base(NAME, NICK, DESC, CAT, SUBCAT) { }

        public override Guid ComponentGuid => new Guid("00000000-0000-0000-0000-000000000000"); // TODO
        protected override System.Drawing.Bitmap Icon => null;

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddBooleanParameter("run", "run", "Execute when true.", GH_ParamAccess.item, true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddTextParameter("info", "i", "Status and timing info.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            bool run = true;
            if (!DA.GetData("run", ref run)) run = true;
            if (!run)
            {
                DA.SetData("i", "idle (run = false)");
                UpdateMessage(); // show version
                return;
            }

            var sw = Stopwatch.StartNew();
            try
            {
                double tol = DocTol;

                // ==== your logic here ====

                sw.Stop();
                DA.SetData("i", $"OK | tol={tol:e2} | {sw.ElapsedMilliseconds} ms");
                UpdateMessage();
            }
            catch (Exception ex)
            {
                sw.Stop();
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, ex.Message, this);
                DA.SetData("i", $"ERR | {ex.GetType().Name}: {ex.Message}");
                this.Message = "ERR";
            }
        }
    }
}
