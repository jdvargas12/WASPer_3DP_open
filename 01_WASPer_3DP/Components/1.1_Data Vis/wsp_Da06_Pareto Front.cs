#region Component Description
/* Da06 performs Pareto analysis across all parameters and exposes up to three selected parameters as points. */
#endregion
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Rhino.Geometry;

namespace WASPer_3DP.Components._1_1_Data_Vis
{
    public sealed class wsp_Da06_Pareto_Front : GH_Component
    {
        private readonly string _version;

        public wsp_Da06_Pareto_Front()
            : base(
                "wsp_Da06_Pareto Front",
                "Pareto Front",
                "Performs Pareto analysis across all sample parameters and exposes up to three selected parameters as points.",
                global::WASPer_3DP.WASPerPalette.Performance,
                "1.1_Data Vis"
            )
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _version = v == null ? "v1.0.5" : $"v{v.Major}.{v.Minor}.{v.Build}";
            Message = _version;
        }

        public override Guid ComponentGuid => new Guid("A6C6A3CF-0CF3-43C4-9B08-1C4DAA7D7C06");
        public override GH_Exposure Exposure => GH_Exposure.primary;
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    using var s = Assembly
                        .GetExecutingAssembly()
                        .GetManifestResourceStream(
                            "WASPer_3DP.Resources.Icons.wsp_Da06_Pareto Front.png"
                        );
                    return s == null ? null : new System.Drawing.Bitmap(s);
                }
                catch
                {
                    return null;
                }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddNumberParameter(
                "samples",
                "samples",
                "Solutions as a tree: branch {0} might be [10, 3.2, 0.4] and branch {1} [12, 3.6, 0.7]. Each item is a parameter; all branches must have equal length. Pareto analysis uses every parameter.",
                GH_ParamAccess.tree
            );
            p.AddTextParameter(
                "samp_names",
                "samp_names",
                "Optional solution identifiers in branch order, for example [\"Option A\", \"Option B\"]. Names do not affect calculations.",
                GH_ParamAccess.list
            );
            p.AddTextParameter(
                "param_names",
                "param_names",
                "Optional parameter names in item order, for example [\"cost\", \"energy\", \"comfort\"]. Used to document the analysis.",
                GH_ParamAccess.list
            );
            p.AddBooleanParameter(
                "obj_logic",
                "obj_logic",
                "Flattened direction flags in parameter order: [False, False, True] means minimize parameter 0, minimize parameter 1, and maximize parameter 2. Missing flags default to minimize.",
                GH_ParamAccess.list
            );
            p.AddIntegerParameter(
                "param_sel",
                "param_sel",
                "Zero-based parameter indices used only for Point3d outputs. Example [0, 2, 4] maps parameters 0/2/4 to X/Y/Z; if more than three are supplied, only the first three are used.",
                GH_ParamAccess.list
            );
            p.AddTextParameter(
                "norm_range",
                "norm_range",
                "Optional normalization target. Leave disconnected for raw values; connect 10 to map every parameter to 0..10, or connect 0;1 for a 0..1 range. Normalized values are used for analysis and point outputs.",
                GH_ParamAccess.item,
                ""
            );
            p[3].DataMapping = GH_DataMapping.Flatten;
            p[4].DataMapping = GH_DataMapping.Flatten;
            for (int i = 0; i < p.ParamCount; i++)
                p[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddPointParameter(
                "samp_pts",
                "samp_pts",
                "All valid solutions represented by the selected parameters as Point3d coordinates. Coordinates are normalized only when norm_range is connected.",
                GH_ParamAccess.list
            );
            p.AddPointParameter(
                "pf_pts",
                "pf_pts",
                "Non-dominated solutions represented using the selected point parameters.",
                GH_ParamAccess.list
            );
            p.AddPointParameter(
                "dom_pts",
                "dom_pts",
                "Dominated solutions represented using the selected point parameters.",
                GH_ParamAccess.list
            );
            p.AddIntegerParameter(
                "pf_inds",
                "pf_inds",
                "Zero-based indices into the valid samp_pts list for Pareto-front solutions.",
                GH_ParamAccess.list
            );
            p.AddIntegerParameter(
                "dom_inds",
                "dom_inds",
                "Zero-based indices into the valid samp_pts list for dominated solutions.",
                GH_ParamAccess.list
            );
            p.AddBooleanParameter(
                "pf_bool",
                "pf_bool",
                "One Boolean per valid solution: True means non-dominated Pareto front; False means dominated.",
                GH_ParamAccess.list
            );
            p.AddTextParameter(
                "summary",
                "summary",
                "Analysis summary including parameter count, selected point parameters, direction flags, normalization, and validation notes.",
                GH_ParamAccess.item
            );
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            GH_Structure<GH_Number> tree;
            if (!da.GetDataTree(0, out tree) || tree == null || tree.PathCount == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Connect a samples data tree to run the Pareto analysis."
                );
                return;
            }
            var sampleNames = new List<string>();
            var paramNames = new List<string>();
            var logic = new List<bool>();
            var selection = new List<int>();
            string normText = "";
            da.GetDataList(1, sampleNames);
            da.GetDataList(2, paramNames);
            da.GetDataList(3, logic);
            da.GetDataList(4, selection);
            da.GetData(5, ref normText);
            int parameterCount = tree.Branches.Max(b => b?.Count ?? 0);
            if (parameterCount == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Samples branches are empty; connect numeric parameter data."
                );
                return;
            }
            if (logic.Count > parameterCount)
                logic = logic.Take(parameterCount).ToList();
            while (logic.Count < parameterCount)
                logic.Add(false);
            var notes = new List<string>();
            var values = new List<double[]>();
            for (int i = 0; i < tree.PathCount; i++)
            {
                var b = tree.Branches[i];
                if (b == null || b.Count != parameterCount)
                {
                    notes.Add(
                        $"Solution {i} has {b?.Count ?? 0} parameters; expected {parameterCount} and was skipped."
                    );
                    continue;
                }
                var a = b.Select(x => x.Value).ToArray();
                if (a.Any(x => double.IsNaN(x) || double.IsInfinity(x)))
                {
                    notes.Add($"Solution {i} contains non-finite values and was skipped.");
                    continue;
                }
                values.Add(a);
            }
            if (values.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "No valid, consistently sized solutions were found."
                );
                return;
            }
            double loTarget = 0,
                hiTarget = 1;
            bool doNormalize = !string.IsNullOrWhiteSpace(normText);
            if (
                doNormalize
                && (!ParseRange(normText, ref loTarget, ref hiTarget) || hiTarget <= loTarget)
            )
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "norm_range must be a single positive maximum or minimum;maximum."
                );
                return;
            }
            var work = values.Select(a => (double[])a.Clone()).ToList();
            if (doNormalize)
                for (int d = 0; d < parameterCount; d++)
                {
                    double lo = work.Min(a => a[d]),
                        hi = work.Max(a => a[d]);
                    for (int i = 0; i < work.Count; i++)
                        work[i][d] =
                            Math.Abs(hi - lo) < 1e-12
                                ? (loTarget + hiTarget) * .5
                                : loTarget + (work[i][d] - lo) / (hi - lo) * (hiTarget - loTarget);
                }
            if (selection.Count == 0)
                selection = Enumerable.Range(0, Math.Min(3, parameterCount)).ToList();
            if (selection.Count > 3)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "param_sel contains more than three indices; only the first three are used for point outputs."
                );
                selection = selection.Take(3).ToList();
            }
            selection = selection.Where(i => i >= 0 && i < parameterCount).Distinct().ToList();
            if (selection.Count == 0)
                selection = Enumerable.Range(0, Math.Min(3, parameterCount)).ToList();
            var front = Enumerable.Repeat(true, work.Count).ToList();
            for (int i = 0; i < work.Count; i++)
            {
                for (int j = 0; j < work.Count && front[i]; j++)
                {
                    if (i != j && Dominates(work[j], work[i], logic, parameterCount))
                    {
                        front[i] = false;
                    }
                }
            }
            var allPts = work.Select(a => ToPoint(a, selection)).ToList();
            var pfPts = allPts.Where((p, i) => front[i]).ToList();
            var domPts = allPts.Where((p, i) => !front[i]).ToList();
            var pfI = Enumerable.Range(0, front.Count).Where(i => front[i]).ToList();
            var domI = Enumerable.Range(0, front.Count).Where(i => !front[i]).ToList();
            string selectedText = string.Join(",", selection);
            string summary =
                $"{work.Count} valid solutions; {pfPts.Count} Pareto-front; {domPts.Count} dominated; parameters={parameterCount}; param_sel=[{selectedText}]; logic=[{string.Join(",", logic.Select(x => x ? "max" : "min"))}]; normalization={(doNormalize ? loTarget.ToString("G4", CultureInfo.InvariantCulture) + ".." + hiTarget.ToString("G4", CultureInfo.InvariantCulture) : "off")}."
                + (notes.Count == 0 ? "" : " " + string.Join(" ", notes));
            if (notes.Count > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, string.Join(" ", notes));
            Message = _version + " | " + pfPts.Count + " PF";
            da.SetDataList(0, allPts);
            da.SetDataList(1, pfPts);
            da.SetDataList(2, domPts);
            da.SetDataList(3, pfI);
            da.SetDataList(4, domI);
            da.SetDataList(5, front);
            da.SetData(6, summary);
        }

        private static Point3d ToPoint(double[] a, IList<int> sel) =>
            new Point3d(a[sel[0]], sel.Count > 1 ? a[sel[1]] : 0, sel.Count > 2 ? a[sel[2]] : 0);

        private static bool Dominates(double[] a, double[] b, IList<bool> logic, int n)
        {
            bool strict = false;
            for (int d = 0; d < n; d++)
            {
                bool better = logic[d] ? a[d] >= b[d] : a[d] <= b[d];
                if (!better)
                    return false;
                if (a[d] != b[d])
                    strict = true;
            }
            return strict;
        }

        private static bool ParseRange(string s, ref double a, ref double b)
        {
            var p = s.Split(';');
            if (
                p.Length == 1
                && double.TryParse(
                    p[0],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double single
                )
            )
            {
                a = 0;
                b = single;
                return true;
            }
            return p.Length == 2
                && double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out a)
                && double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b);
        }
    }
}
