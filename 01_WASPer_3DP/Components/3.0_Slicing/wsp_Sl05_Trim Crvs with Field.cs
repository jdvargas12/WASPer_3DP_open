// wsp_Sl05_Trim Crvs with Field.cs
// WASPer_3DP - Subcategory: 3.0_Slicing
//
// Trims printing path curves with a WasperField.
// Default field convention: negative = inside / keep, positive = outside / remove.
// If invert is true, the kept/removed side is flipped.
//
// RENAME NOTICE (2026-08-05): renamed in place from "wsp_Sl05_Trim Paths with
// Field" to "wsp_Sl05_Trim Crvs with Field" to distinguish it from the new
// wsp_path counterpart wsp_Pp19_Trim Paths with Fields (4.0_Print Paths).
// GUID, inputs, and trimming behavior are unchanged; only the display name,
// nickname, and the output nickname (paths -> crv_paths) changed.
//
// PATH ROLE METADATA: each output fragment copies the shared WASPer.PathRole
// user-string tag (WasperPathRole / WasperPathRoleMetadata, Components\Shared\
// Geometry\WASPer_PathRole.cs) from its source curve, since trimming a shell/
// infill/partition curve does not change what it semantically is. Curve.Split()
// does not preserve user strings on its own, so the tag is re-applied to every
// kept fragment.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP.Components._3_0_Slicing
{
    public class wsp_Sl05_TrimCrvsWithField : GH_Component
    {
        private const string NAME = "wsp_Sl05_Trim Crvs with Field";
        private const string NICK = "Trim Crvs Field";
        private const string CAT = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "3.0_Slicing";

        private readonly string _versionTag;

        public wsp_Sl05_TrimCrvsWithField()
            : base(
                NAME,
                NICK,
                "Trims printing paths with a WASPer 3D field while preserving the incoming data tree structure.\n" +
                "Default field convention: field <= 0 is inside / kept; field > 0 is outside / removed.\n" +
                "Set invert=true to keep the outside field side instead.",
                CAT,
                SUBCAT)
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("4D94E447-AD67-4B7B-8572-4B1D1B47F63C");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_Fi3d06_Trim Paths with Field.png"))
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
            pManager.AddCurveParameter(
                "paths", "paths",
                "Printing paths to trim. Tree structure is preserved.\n" +
                "Designed for paths from In13-In16, slicers, or any Curve/Polyline path tree.",
                GH_ParamAccess.tree);

            pManager.AddGenericParameter(
                "trim_field", "field",
                "WASPer 3D field used as the trimming volume.\n" +
                "Default: path portions where field <= 0 are kept; portions where field > 0 are removed.\n" +
                "Use invert=true to keep field > 0 instead.",
                GH_ParamAccess.item);

            pManager.AddBooleanParameter(
                "invert",
                "invert",
                "Invert the trim side. False keeps field <= 0 (inside) and removes field > 0. True keeps field > 0 (outside) and removes field <= 0.",
                GH_ParamAccess.item,
                false);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "trimmed_paths", "crv_paths",
                "Trimmed path fragments. Output branches match the input paths branches.",
                GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            GH_Structure<GH_Curve> pathTree;
            if (!DA.GetDataTree(0, out pathTree) || pathTree == null || pathTree.PathCount == 0)
            {
                DA.SetDataTree(0, new GH_Structure<GH_Curve>());
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No input paths were provided.");
                return;
            }

            IGH_Goo fieldGoo = null;
            if (!DA.GetData(1, ref fieldGoo))
            {
                DA.SetDataTree(0, new GH_Structure<GH_Curve>());
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No trim_field was provided.");
                return;
            }

            WasperField field = ExtractField(fieldGoo);
            if (field == null || field.Evaluator == null)
            {
                DA.SetDataTree(0, new GH_Structure<GH_Curve>());
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "trim_field is not a valid WASPer field. Use a field from Fi3d01, In08-In12, or another 2.3_Fields_3D component.");
                return;
            }

            bool invert = false;
            DA.GetData(2, ref invert);

            double tol = RhinoDoc.ActiveDoc != null
                ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance
                : 1e-6;
            tol = Math.Max(tol, 1e-9);

            int branchCount = pathTree.PathCount;
            var branchPieces = new List<Curve>[branchCount];
            var branchInputCounts = new int[branchCount];
            var branchOutputCounts = new int[branchCount];
            var branchSplitCounts = new int[branchCount];

            int candidateCount = 0;
            for (int bi = 0; bi < branchCount; bi++)
            {
                IList<GH_Curve> branch = pathTree.Branches[bi];
                if (branch != null) candidateCount += branch.Count;
            }

            Action<int> processBranch = bi =>
            {
                var localPieces = new List<Curve>();
                IList<GH_Curve> branch = pathTree.Branches[bi];
                if (branch == null || branch.Count == 0)
                {
                    branchPieces[bi] = localPieces;
                    return;
                }

                foreach (GH_Curve ghCurve in branch)
                {
                    Curve curve = ghCurve?.Value;
                    if (curve == null || !curve.IsValid) continue;

                    branchInputCounts[bi]++;
                    if (IsOutsideFieldDomain(curve, field, tol))
                    {
                        if (invert)
                        {
                            Curve passthrough = curve.DuplicateCurve();
                            global::WASPer_3DP.WasperPathRoleMetadata.Copy(curve, passthrough);
                            localPieces.Add(passthrough);
                            branchOutputCounts[bi]++;
                        }
                        continue;
                    }

                    List<Curve> pieces = TrimCurveWithField(curve, field, tol, invert);
                    if (pieces.Count > 1) branchSplitCounts[bi]++;

                    foreach (Curve piece in pieces)
                    {
                        if (piece == null || !piece.IsValid) continue;
                        if (piece.GetLength() <= 2.0 * tol) continue;

                        localPieces.Add(piece);
                        branchOutputCounts[bi]++;
                    }
                }

                branchPieces[bi] = localPieces;
            };

            bool doParallel = branchCount >= 2 && candidateCount >= 24;
            if (doParallel)
            {
                Parallel.For(0, branchCount, processBranch);
            }
            else
            {
                for (int bi = 0; bi < branchCount; bi++)
                    processBranch(bi);
            }

            var outTree = new GH_Structure<GH_Curve>();
            int inputCount = 0;
            int outputCount = 0;
            int splitCount = 0;

            for (int bi = 0; bi < branchCount; bi++)
            {
                GH_Path path = pathTree.Paths[bi];
                outTree.EnsurePath(path);

                inputCount += branchInputCounts[bi];
                outputCount += branchOutputCounts[bi];
                splitCount += branchSplitCounts[bi];

                List<Curve> pieces = branchPieces[bi];
                if (pieces == null || pieces.Count == 0) continue;

                foreach (Curve piece in pieces)
                    outTree.Append(new GH_Curve(piece), path);
            }

            DA.SetDataTree(0, outTree);
            Message = doParallel
                ? $"{_versionTag} | {outputCount}/{inputCount} | P"
                : $"{_versionTag} | {outputCount}/{inputCount}";

            if (inputCount > 0 && outputCount == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    invert
                        ? "No path portions were on the inverted keep side (field > 0)."
                        : "All paths were outside the trim_field (field > 0), or the trim field is empty at those path locations.");
            }
            else if (splitCount > 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    $"{splitCount} input path(s) were split by the trim field boundary.");
            }
        }

        private static List<Curve> TrimCurveWithField(Curve source, WasperField field, double tol, bool invert)
        {
            var result = new List<Curve>();
            if (source == null || !source.IsValid) return result;

            double length = source.GetLength();
            if (length <= 2.0 * tol) return result;

            Curve curve = source.DuplicateCurve();
            global::WASPer_3DP.WasperPathRoleMetadata.Copy(source, curve);
            List<double> roots = CollectFieldZeroParameters(curve, field, length, tol);

            if (roots.Count == 0)
            {
                Point3d mid = PointAtLengthFraction(curve, length, 0.5);
                if (IsKept(field, mid, tol, invert))
                    result.Add(curve);
                return result;
            }

            Curve[] split = null;
            try { split = curve.Split(roots); }
            catch { split = null; }

            if (split == null || split.Length == 0)
            {
                Point3d mid = PointAtLengthFraction(curve, length, 0.5);
                if (IsKept(field, mid, tol, invert))
                    result.Add(curve);
                return result;
            }

            foreach (Curve piece in split)
            {
                if (piece == null || !piece.IsValid) continue;
                double pieceLen = piece.GetLength();
                if (pieceLen <= 2.0 * tol) continue;

                Point3d mid = PointAtLengthFraction(piece, pieceLen, 0.5);
                if (IsKept(field, mid, tol, invert))
                {
                    // Curve.Split() does not carry over user-string metadata, so the
                    // WASPer.PathRole tag is re-applied to each kept fragment.
                    global::WASPer_3DP.WasperPathRoleMetadata.Copy(curve, piece);
                    result.Add(piece);
                }
                else
                    piece.Dispose();
            }

            return result;
        }

        private static List<double> CollectFieldZeroParameters(Curve curve, WasperField field, double length, double tol)
        {
            var roots = new List<double>();
            int samples = EstimateSampleCount(curve, field, length, tol);

            var ts = new double[samples + 1];
            var fs = new double[samples + 1];

            for (int i = 0; i <= samples; i++)
            {
                double u = (double)i / samples;
                double t;
                if (!curve.LengthParameter(u * length, out t))
                    t = curve.Domain.ParameterAt(u);

                ts[i] = t;
                fs[i] = SafeEvaluate(field, curve.PointAt(t));
            }

            double fieldTol = Math.Max(tol, 1e-7);
            for (int i = 0; i < samples; i++)
            {
                double f0 = fs[i];
                double f1 = fs[i + 1];

                bool f0Bad = double.IsNaN(f0) || double.IsInfinity(f0);
                bool f1Bad = double.IsNaN(f1) || double.IsInfinity(f1);
                if (f0Bad && f1Bad) continue;

                if (Math.Abs(f0) <= fieldTol)
                    roots.Add(ts[i]);
                if (Math.Abs(f1) <= fieldTol)
                    roots.Add(ts[i + 1]);

                if (f0Bad || f1Bad) continue;

                bool in0 = f0 <= 0.0;
                bool in1 = f1 <= 0.0;
                if (in0 != in1)
                    roots.Add(FindFieldRoot(curve, field, ts[i], ts[i + 1], f0, fieldTol));
            }

            return CleanParameters(roots, curve.Domain, tol);
        }

        private static double FindFieldRoot(Curve curve, WasperField field, double ta, double tb, double fa, double fieldTol)
        {
            bool aInside = fa <= 0.0;

            for (int i = 0; i < 40; i++)
            {
                double tm = 0.5 * (ta + tb);
                double fm = SafeEvaluate(field, curve.PointAt(tm));

                if (!double.IsNaN(fm) && !double.IsInfinity(fm) && Math.Abs(fm) <= fieldTol)
                    return tm;

                bool mInside = fm <= 0.0;
                if (mInside == aInside)
                {
                    ta = tm;
                    fa = fm;
                    aInside = mInside;
                }
                else
                {
                    tb = tm;
                }
            }

            return 0.5 * (ta + tb);
        }

        private static bool IsOutsideFieldDomain(Curve curve, WasperField field, double tol)
        {
            if (curve == null || field == null || !field.Domain.IsValid) return false;

            BoundingBox bb = curve.GetBoundingBox(true);
            BoundingBox dom = field.Domain;
            dom.Inflate(Math.Max(tol * 10.0, dom.Diagonal.Length * 1e-6));

            return !BBoxOverlaps(bb, dom, tol);
        }

        private static bool BBoxOverlaps(BoundingBox a, BoundingBox b, double tol)
        {
            if (!a.IsValid || !b.IsValid) return true;

            return a.Min.X - tol <= b.Max.X && a.Max.X + tol >= b.Min.X &&
                   a.Min.Y - tol <= b.Max.Y && a.Max.Y + tol >= b.Min.Y &&
                   a.Min.Z - tol <= b.Max.Z && a.Max.Z + tol >= b.Min.Z;
        }

        private static List<double> CleanParameters(List<double> parameters, Interval domain, double tol)
        {
            var clean = new List<double>();
            if (parameters == null || parameters.Count == 0) return clean;

            double guard = Math.Max(domain.Length * 1e-10, tol * 1e-6);

            foreach (double t in parameters)
            {
                if (double.IsNaN(t) || double.IsInfinity(t)) continue;
                if (t <= domain.T0 + guard || t >= domain.T1 - guard) continue;
                clean.Add(t);
            }

            clean.Sort();
            var dedup = new List<double>(clean.Count);
            foreach (double t in clean)
            {
                if (dedup.Count == 0 || Math.Abs(t - dedup[dedup.Count - 1]) > guard)
                    dedup.Add(t);
            }

            return dedup;
        }

        private static int EstimateSampleCount(Curve curve, WasperField field, double length, double tol)
        {
            double step = length / 256.0;

            if (field != null && field.Domain.IsValid)
            {
                double diag = field.Domain.Diagonal.Length;
                if (diag > tol)
                    step = Math.Min(step, diag / 800.0);
            }

            step = Math.Max(step, tol * 10.0);
            int count = (int)Math.Ceiling(length / step);
            if (count < 8) count = 8;
            if (count > 4096) count = 4096;
            return count;
        }

        private static Point3d PointAtLengthFraction(Curve curve, double length, double fraction)
        {
            fraction = Math.Max(0.0, Math.Min(1.0, fraction));

            double t;
            if (!curve.LengthParameter(fraction * length, out t))
                t = curve.Domain.ParameterAt(fraction);

            return curve.PointAt(t);
        }

        private static bool IsKept(WasperField field, Point3d point, double tol, bool invert)
        {
            bool inside = SafeEvaluate(field, point) <= Math.Max(tol, 1e-7);
            return invert ? !inside : inside;
        }

        private static double SafeEvaluate(WasperField field, Point3d point)
        {
            try
            {
                double value = field.Evaluate(point);
                return (double.IsNaN(value) || double.IsInfinity(value))
                    ? double.PositiveInfinity
                    : value;
            }
            catch
            {
                return double.PositiveInfinity;
            }
        }

        private static WasperField ExtractField(IGH_Goo goo)
        {
            if (goo == null) return null;

            if (goo is WasperFieldGoo fg) return fg.Value;

            object sv = null;
            try { sv = goo.ScriptVariable(); } catch { sv = null; }

            if (sv is WasperField f) return f;
            if (sv is WasperFieldGoo fgoo) return fgoo.Value;

            if (goo is GH_ObjectWrapper wrapper)
            {
                if (wrapper.Value is WasperField wf) return wf;
                if (wrapper.Value is WasperFieldGoo wg) return wg.Value;
            }

            return null;
        }
    }
}
