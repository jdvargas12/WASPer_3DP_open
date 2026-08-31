#region Usings
using System;
using System.Collections.Generic;
using System.Linq;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
#endregion

namespace WASPer_3DP.Components._1_0_Utils
{
    public class wsp_Ut08_Cartesian_Product : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Ut08_Cartesian_Product()
          : base(
              "wsp_Ut08_Cartesian Product",
              "CartProd",
              "Generates the Cartesian product of multiple input lists.\n\n" +
              "INPUT FORMAT:\n" +
              "  - Provide a DataTree where each BRANCH represents one list.\n" +
              "  - Example: {0}=materials, {1}=thicknesses, {2}=colors.\n\n" +
              "OUTPUT FORMAT:\n" +
              "  - Each output BRANCH is one combination.\n" +
              "  - Branch path is {i} where i is the combination index.\n" +
              "  - Items inside that branch are the picked items (one per input list).",
              global::WASPer_3DP.WASPerPalette.DesignFabrication,
              "1.0_Utils")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v   = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("9B78B6E2-5F50-4B59-BB58-6F4DA4A2E8F1");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ut11_Cartesian Product.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "lists",
                "lists",
                "DataTree of lists.\nEach branch is treated as an independent list.\nThe component will compute the Cartesian product across branches.",
                GH_ParamAccess.tree);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "combinations",
                "combinations",
                "Cartesian product combinations as a DataTree.\nEach branch {i} is one combination, containing one item per input list.",
                GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_Structure<IGH_Goo> listsTree;
            if (!DA.GetDataTree(0, out listsTree) || listsTree == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No input tree provided.");
                DA.SetDataTree(0, new GH_Structure<IGH_Goo>());
                return;
            }

            int branchCount = listsTree.PathCount;
            if (branchCount == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Input tree has no branches.");
                DA.SetDataTree(0, new GH_Structure<IGH_Goo>());
                return;
            }

            // Convert each branch to a simple list
            var inputLists = new List<List<IGH_Goo>>(branchCount);
            for (int i = 0; i < branchCount; i++)
            {
                var branch = listsTree.Branches[i];
                // If any branch is empty -> product is empty
                if (branch == null || branch.Count == 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"Branch {listsTree.Paths[i]} is empty. Cartesian product is empty.");
                    DA.SetDataTree(0, new GH_Structure<IGH_Goo>());
                    return;
                }

                // Keep items as IGH_Goo
                inputLists.Add(branch.ToList());
            }

            // Estimate size (may overflow)
            double est = 1.0;
            foreach (var l in inputLists) est *= Math.Max(1, l.Count);

            if (est > 2_000_000)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"Large Cartesian product (~{est:N0} combos). This may be slow / memory-heavy.");

            // Build combinations (iterative odometer approach)
            var result = new GH_Structure<IGH_Goo>();
            var indices = new int[inputLists.Count];

            int comboIndex = 0;

            while (true)
            {
                // Emit one combination
                var path = new GH_Path(comboIndex);
                for (int k = 0; k < inputLists.Count; k++)
                {
                    result.Append(inputLists[k][indices[k]], path);
                }

                comboIndex++;

                // Increment "odometer"
                int pos = inputLists.Count - 1;
                while (pos >= 0)
                {
                    indices[pos]++;
                    if (indices[pos] < inputLists[pos].Count)
                        break;

                    indices[pos] = 0;
                    pos--;
                }

                // Finished
                if (pos < 0)
                    break;
            }

            DA.SetDataTree(0, result);
        }
    }
}
