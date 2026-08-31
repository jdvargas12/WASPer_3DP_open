#region Usings
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Drawing;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
#endregion

namespace WASPer_3DP.Components._3_0_Slicing
{
    public class wsp_Sl04_Trim_Printing_Paths : GH_Component
    {
        // ---------------------------------------------------------------------
        // META
        // ---------------------------------------------------------------------
        private readonly string _versionTag;
        public wsp_Sl04_Trim_Printing_Paths()
          : base(
              "wsp_Sl04_Trim Printing Paths (A?B)",
              "TrimA>B_fast",
              "Branch-aware trimming of printing paths:\n" +
              "- If A has one branch -> trims ALL B branches.\n" +
              "- Otherwise A[{i}] trims B[{i}] (by branch index).\n" +
              "- Closed planar A = containment boundary (keep in/out).\n" +
              "- Open A = splitter (splits B at intersections).\n" +
              "- Output is converted to polylines at the end (without reducing intersection accuracy).",
              global::WASPer_3DP.WASPerPalette.DesignFabrication,
              "3.0_Slicing")
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null
                ? $"{v.Major}.{v.Minor}.{v.Build}"
                : "v1.0.x";
            this.Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("A67B8F3F-5C5D-4B5E-9A9B-5B5D3E2A1B10");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Sl04_Trim Printing Paths.png"))
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
                "p_paths_A",
                "A",
                "Cutters (printing paths). Closed & planar -> containment boundaries; open -> splitters.\n" +
                "TREE access.",
                GH_ParamAccess.tree);

            pManager.AddCurveParameter(
                "p_paths_B",
                "B",
                "Targets (printing paths) to be trimmed.\nTREE access.",
                GH_ParamAccess.tree);

            pManager.AddBooleanParameter(
                "reverse_trim",
                "rev",
                "false = keep INSIDE closed A boundaries; true = keep OUTSIDE.",
                GH_ParamAccess.item,
                false);

            pManager.AddNumberParameter(
                "extend_len",
                "ext",
                "Extension length for OPEN A ends (0 = no extension).",
                GH_ParamAccess.item,
                0.0);

            pManager.AddBooleanParameter(
                "join_AB",
                "join",
                "If true: joins (extended A + kept B pieces). If false: returns kept B pieces only.",
                GH_ParamAccess.item,
                false);

            pManager.AddNumberParameter(
                "min_keep_len",
                "minL",
                "Discard B pieces shorter than this length (0 = keep all).",
                GH_ParamAccess.item,
                0.0);

            pManager.AddNumberParameter(
                "join_tol",
                "jTol",
                "Tolerance used by JoinCurves; if <= 0 uses document tolerance.",
                GH_ParamAccess.item,
                0.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "p_paths_B_trim",
                "B_trim",
                "Trimmed (or joined) printing paths. Same branch structure as B.\n" +
                "Output is polylines when possible.",
                GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_Structure<GH_Curve> treeA = null;
            GH_Structure<GH_Curve> treeB = null;

            bool reverseTrim = false;
            double extendLen = 0.0;
            bool joinAB = false;
            double minKeepLen = 0.0;
            double joinTol = 0.0;

            if (!DA.GetDataTree(0, out treeA)) return;
            if (!DA.GetDataTree(1, out treeB)) return;

            DA.GetData(2, ref reverseTrim);
            DA.GetData(3, ref extendLen);
            DA.GetData(4, ref joinAB);
            DA.GetData(5, ref minKeepLen);
            DA.GetData(6, ref joinTol);

            double docTol = RhinoDoc.ActiveDoc != null ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 1e-6;
            double tol = docTol;

            // A pairing: if A has exactly one non-empty branch -> use it for all B branches.
            bool singleAForAll = (treeA.PathCount == 1 && treeA.get_Branch(treeA.Paths[0]) != null && treeA.get_Branch(treeA.Paths[0]).Count > 0);

            var outTree = new GH_Structure<GH_Curve>();

            // -----------------------------------------------------------------------------
            // Iterate B branches by index (same behavior as your script)
            // -----------------------------------------------------------------------------
            for (int bi = 0; bi < treeB.PathCount; bi++)
            {
                GH_Path bPath = treeB.Paths[bi];

                // -----------------------------
                // Collect B curves (targets)
                // -----------------------------
                var bList = new List<Curve>();

                // NOTE: get_Branch returns non-generic IList -> items come as object
                var bBranch = treeB.get_Branch(bPath);
                if (bBranch != null)
                {
                    foreach (object obj in bBranch)
                    {
                        var ghc = obj as GH_Curve;
                        if (ghc == null) continue;

                        Curve c = ghc.Value;
                        if (c != null && c.IsValid)
                            bList.Add(c);
                    }
                }

                // -----------------------------
                // Collect A cutters for branch
                // -----------------------------
                var aCutters = new List<Curve>();

                if (singleAForAll)
                {
                    // A[0] trims all B branches
                    if (treeA.PathCount > 0)
                    {
                        var aBranch0 = treeA.get_Branch(treeA.Paths[0]);
                        if (aBranch0 != null)
                        {
                            foreach (object obj in aBranch0)
                            {
                                var ghc = obj as GH_Curve;
                                if (ghc == null) continue;

                                Curve c = ghc.Value;
                                if (c != null && c.IsValid)
                                    aCutters.Add(c);
                            }
                        }
                    }
                }
                else
                {
                    // Match by branch INDEX
                    if (bi < treeA.PathCount)
                    {
                        GH_Path aPath = treeA.Paths[bi];
                        var aBranch = treeA.get_Branch(aPath);

                        if (aBranch != null)
                        {
                            foreach (object obj in aBranch)
                            {
                                var ghc = obj as GH_Curve;
                                if (ghc == null) continue;

                                Curve c = ghc.Value;
                                if (c != null && c.IsValid)
                                    aCutters.Add(c);
                            }
                        }
                    }
                    // else: no matching A branch => no cutters => no trimming for this B branch
                }

                // -----------------------------
                // Trim
                // -----------------------------
                var trimmed = TrimBranch(
                    aCutters,
                    bList,
                    reverseTrim,
                    extendLen,
                    joinAB,
                    minKeepLen,
                    joinTol,
                    tol);

                // -----------------------------
                // Output (convert to polyline only at the end)
                // -----------------------------
                if (trimmed != null && trimmed.Count > 0)
                {
                    foreach (Curve c in trimmed)
                    {
                        if (c == null || !c.IsValid) continue;

                        Curve outC = ToPolylineIfPossible(c, tol);
                        outTree.Append(new GH_Curve(outC), bPath);
                    }
                }
                else
                {
                    // Keep empty branch structure
                    outTree.EnsurePath(bPath);
                }
            }


            Message = "v1.0.4 - 260210";
            DA.SetDataTree(0, outTree);
        }

        // ---------------------------------------------------------------------
        // CORE (same logic as your script; list-based)
        // ---------------------------------------------------------------------
        private List<Curve> TrimBranch(
            List<Curve> crvsA,
            List<Curve> crvsB,
            bool reverseTrim,
            double extendLen,
            bool joinAB,
            double minKeepLen,
            double joinTol,
            double tol)
        {
            double jtol = (joinTol > 0.0) ? joinTol : tol;
            double minLen = Math.Max(0.0, minKeepLen);

            // extend open cutters (keep as CURVES; don't polyline them here)
            var extCutters = new List<Curve>();
            foreach (var c in crvsA)
            {
                if (c == null || !c.IsValid) continue;
                extCutters.Add(ExtendCurveBy(c, extendLen, tol));
            }

            // classify cutters
            var openCurves = new List<Curve>();
            var openBoxes = new List<BoundingBox>();
            var closedCurves = new List<Curve>();
            var closedPlanes = new List<Plane>();
            var closedBoxes = new List<BoundingBox>();

            foreach (var c in extCutters)
            {
                if (c == null || !c.IsValid) continue;

                if (c.IsClosed && c.TryGetPlane(out Plane pl, tol))
                {
                    closedCurves.Add(c);
                    closedPlanes.Add(pl);

                    var bb = c.GetBoundingBox(true);
                    bb.Inflate(tol);
                    closedBoxes.Add(bb);
                }
                else
                {
                    openCurves.Add(c);

                    var bb = c.GetBoundingBox(true);
                    bb.Inflate(tol);
                    openBoxes.Add(bb);
                }
            }

            bool singleClosedFast = (closedCurves.Count == 1 && openCurves.Count == 0);
            BoundingBox singleClosedBB = BoundingBox.Empty;
            if (singleClosedFast) singleClosedBB = closedBoxes[0];

            var outList = new List<Curve>();
            if (crvsB == null || crvsB.Count == 0) return outList;

            for (int i = 0; i < crvsB.Count; i++)
            {
                var b = crvsB[i];
                if (b == null || !b.IsValid) continue;

                var bDup = b.DuplicateCurve();
                var bDom = bDup.Domain;

                var bbB = bDup.GetBoundingBox(true);
                bbB.Inflate(tol);

                // bbox candidate selection
                var candOpen = new List<int>();
                var candClosed = new List<int>();

                for (int j = 0; j < openCurves.Count; j++)
                    if (BBoxOverlaps(openBoxes[j], bbB, tol)) candOpen.Add(j);

                for (int j = 0; j < closedCurves.Count; j++)
                    if (BBoxOverlaps(closedBoxes[j], bbB, tol)) candClosed.Add(j);

                // single-closed fast skip if no bbox overlap
                if (singleClosedFast && !BBoxOverlaps(singleClosedBB, bbB, tol))
                {
                    bool inside = IsInsideBoundary(bDup, closedCurves[0], closedPlanes[0], tol);
                    bool keep = reverseTrim ? !inside : inside;
                    if (keep) outList.Add(bDup);
                    continue;
                }

                var candCurves = new List<Curve>(candOpen.Count + candClosed.Count);
                foreach (int j in candOpen) candCurves.Add(openCurves[j]);
                foreach (int j in candClosed) candCurves.Add(closedCurves[j]);

                // no candidates -> containment only (if any closed)
                if (candCurves.Count == 0)
                {
                    if (candClosed.Count > 0)
                    {
                        bool insideAny = InsideAnyBoundary(bDup, candClosed, closedCurves, closedPlanes, closedBoxes, tol);
                        bool keep = reverseTrim ? !insideAny : insideAny;
                        if (keep) outList.Add(bDup);
                    }
                    else
                    {
                        outList.Add(bDup);
                    }
                    continue;
                }

                // intersections (IMPORTANT: use curve-curve on original curves; no polyline reduction here)
                var tHits = new List<double>(8);

                foreach (var cutter in candCurves)
                {
                    var evs = Intersection.CurveCurve(bDup, cutter, tol, tol);
                    if (evs == null) continue;

                    foreach (var ev in evs)
                    {
                        if (ev == null) continue;

                        if (ev.IsPoint)
                        {
                            double t = ev.ParameterA;
                            if (IsInteriorParameter(bDom, t, tol)) tHits.Add(t);
                        }
                        else if (ev.IsOverlap)
                        {
                            if (IsInteriorParameter(bDom, ev.ParameterA, tol)) tHits.Add(ev.ParameterA);

                            // try to also read overlap interval end parameters (reflection fallback kept)
                            try
                            {
                                var propB = ev.GetType().GetProperty("OverlapB");
                                if (propB != null)
                                {
                                    var val = propB.GetValue(ev, null);
                                    if (val is Interval ibB)
                                    {
                                        if (IsInteriorParameter(bDom, ibB.T0, tol)) tHits.Add(ibB.T0);
                                        if (IsInteriorParameter(bDom, ibB.T1, tol)) tHits.Add(ibB.T1);
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                }

                var splitTs = DedupAndSortParameters(tHits, bDom, tol);

                if (splitTs.Count == 0)
                {
                    if (candClosed.Count > 0)
                    {
                        bool insideAny = InsideAnyBoundary(bDup, candClosed, closedCurves, closedPlanes, closedBoxes, tol);
                        bool keep = reverseTrim ? !insideAny : insideAny;
                        if (keep) outList.Add(bDup);
                    }
                    else
                    {
                        outList.Add(bDup);
                    }
                    continue;
                }

                var pieces = bDup.Split(splitTs);
                if (pieces != null && pieces.Length > 0)
                {
                    foreach (var piece in pieces)
                    {
                        if (piece == null || !piece.IsValid) continue;
                        if (piece.GetLength() <= tol) continue;

                        if (candClosed.Count > 0)
                        {
                            bool insideAny = InsideAnyBoundary(piece, candClosed, closedCurves, closedPlanes, closedBoxes, tol);
                            bool keep = reverseTrim ? !insideAny : insideAny;
                            if (keep) outList.Add(piece);
                        }
                        else
                        {
                            outList.Add(piece);
                        }
                    }
                }
            }

            if (joinAB)
            {
                var pool = new List<Curve>(extCutters.Count + outList.Count);
                pool.AddRange(extCutters);

                if (minLen > 0.0)
                {
                    foreach (var c in outList)
                    {
                        if (c == null || !c.IsValid) continue;
                        if (c.GetLength() + RhinoMath.ZeroTolerance >= minLen) pool.Add(c);
                    }
                }
                else
                {
                    pool.AddRange(outList);
                }

                var joined = Curve.JoinCurves(pool, jtol);
                return new List<Curve>(joined ?? Array.Empty<Curve>());
            }

            return outList;
        }

        // ---------------------------------------------------------------------
        // HELPERS
        // ---------------------------------------------------------------------
        private bool InsideAnyBoundary(
            Curve test,
            List<int> candClosedIdx,
            List<Curve> closedCurves,
            List<Plane> closedPlanes,
            List<BoundingBox> closedBoxes,
            double tol)
        {
            var pieceBB = test.GetBoundingBox(true);
            pieceBB.Inflate(tol);

            for (int k = 0; k < candClosedIdx.Count; k++)
            {
                int idx = candClosedIdx[k];
                if (!BBoxOverlaps(closedBoxes[idx], pieceBB, tol)) continue;

                if (IsInsideBoundary(test, closedCurves[idx], closedPlanes[idx], tol))
                    return true;
            }

            return false;
        }

        private bool IsInteriorParameter(Interval dom, double t, double tol)
        {
            double eps = Math.Max(tol * 10.0, 1e-12 * Math.Max(1.0, Math.Abs(dom.Length)));
            return (t > dom.T0 + eps) && (t < dom.T1 - eps);
        }

        private List<double> DedupAndSortParameters(List<double> ts, Interval dom, double tol)
        {
            var list = ts.Where(v => IsInteriorParameter(dom, v, tol)).ToList();
            list.Sort();

            var dedup = new List<double>(list.Count);
            double? prev = null;
            double eps = Math.Max(1e-10, dom.Length * 1e-12);

            foreach (var t in list)
            {
                if (!prev.HasValue || Math.Abs(t - prev.Value) > eps)
                {
                    dedup.Add(t);
                    prev = t;
                }
            }

            return dedup;
        }

        private bool BBoxOverlaps(BoundingBox a, BoundingBox b, double tol)
        {
            return (a.Min.X - tol <= b.Max.X && a.Max.X + tol >= b.Min.X) &&
                   (a.Min.Y - tol <= b.Max.Y && a.Max.Y + tol >= b.Min.Y) &&
                   (a.Min.Z - tol <= b.Max.Z && a.Max.Z + tol >= b.Min.Z);
        }

        private bool IsInsideBoundary(Curve c, Curve boundary, Plane plane, double tol)
        {
            double tMid = 0.5 * (c.Domain.T0 + c.Domain.T1);
            var p = c.PointAt(tMid);

            var pl = plane;
            if (!pl.IsValid && !boundary.TryGetPlane(out pl, tol)) return false;

            var state = boundary.Contains(p, pl, tol);
            return state == PointContainment.Inside || state == PointContainment.Coincident;
        }

        private Curve ExtendCurveBy(Curve c, double len, double tol)
        {
            if (c == null || !c.IsValid) return c;
            if (len <= 0.0) return c;
            if (c.IsClosed) return c;

            var dup = c.DuplicateCurve();
            double before = dup.GetLength();

            try
            {
                dup.Extend(CurveEnd.Start, len, CurveExtensionStyle.Line);
                dup.Extend(CurveEnd.End, len, CurveExtensionStyle.Line);
            }
            catch { }

            double after = dup.GetLength();
            if (!double.IsNaN(before) && !double.IsNaN(after) && after > before + RhinoMath.ZeroTolerance)
                return dup;

            // fallback: line stubs -> polycurve
            var dom = dup.Domain;
            var p0 = dup.PointAt(dom.T0);
            var p1 = dup.PointAt(dom.T1);

            var t0 = SafeTangentAt(dup, dom.T0);
            var t1 = SafeTangentAt(dup, dom.T1);

            LineCurve s0 = null, s1 = null;
            if (t0.IsValid && t0.SquareLength > 0.0) s0 = new LineCurve(p0 - t0 * len, p0);
            if (t1.IsValid && t1.SquareLength > 0.0) s1 = new LineCurve(p1, p1 + t1 * len);

            var pc = new PolyCurve();
            if (s0 != null) pc.Append(s0);
            pc.Append(dup);
            if (s1 != null) pc.Append(s1);

            if (pc.SegmentCount == 1) return dup;

            pc.Simplify(CurveSimplifyOptions.All, tol, RhinoMath.ToRadians(0.5));
            return pc;
        }

        private Vector3d SafeTangentAt(Curve c, double t)
        {
            try
            {
                var tan = c.TangentAt(t);
                if (tan.IsValid && tan.Unitize()) return tan;
            }
            catch { }

            double eps = Math.Max(1e-6, c.Domain.Length * 1e-6);
            double t1 = RhinoMath.Clamp(t + eps, c.Domain.T0, c.Domain.T1);
            double t0 = RhinoMath.Clamp(t - eps, c.Domain.T0, c.Domain.T1);

            var v = c.PointAt(t1) - c.PointAt(t0);
            if (v.Unitize()) return v;

            return Vector3d.Unset;
        }

        /// <summary>
        /// Convert to polyline curve only if the curve ALREADY has an exact polyline representation.
        /// This avoids "resolution loss" that breaks intersections.
        /// </summary>
        private Curve ToPolylineIfPossible(Curve c, double tol)
        {
            if (c == null || !c.IsValid) return c;

            // Already a polyline curve
            if (c is PolylineCurve plc)
                return plc.DuplicateCurve();

            // Exact polyline form available
            if (c.TryGetPolyline(out Polyline pl))
                return new PolylineCurve(pl);

            // Otherwise, do NOT approximate -> keep original geometry
            return c.DuplicateCurve();
        }
    }
}
