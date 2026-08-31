using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP.Components._2_2_Fields
{
    /// <summary>
    /// wsp_Fi05_Contours_From_Fields
    /// Extract iso-contours from packed field_obj grids using marching squares.
    /// Contour level is defined in g-space: contour where (G - offset) == 0.
    /// </summary>
    public class wsp_Fi05_Contours_From_Fields : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Fi05_Contours_From_Fields()
            : base(
                "wsp_Fi05_Contours From Fields",
                "Contours",
                "Extracts iso-contours from packed field_obj grids using marching squares.\n" +
                "Level is set by 'offset' in g-space (contour where field_obj.G == offset).\n\n" +
                "Inputs:\n" +
                "- field_obj: List of packed fields (Generic). Each item must expose: G, NxVerts, NyVerts, Plane, FrameSize, CellSize (CenterXY optional).\n" +
                "- offset: Iso level in g-space.\n\n" +
                "Outputs:\n" +
                "- contours: Extracted iso-contours as curves (world space).",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "2.2_Fields")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        // New GUID for compiled component
        public override Guid ComponentGuid => new Guid("7D53B3F2-7B29-4B9E-A4D9-7A1DF7B9D9A2");

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Fi05_Contours From Fields.png"))
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
            // 0) field_obj
            pManager.AddGenericParameter(
                "field_obj",
                "field_obj",
                "List of packed fields (Generic). Items are typically GH_ObjectWrapper.\n" +
                "Each packed field must expose:\n" +
                "- double[] G\n" +
                "- int NxVerts, NyVerts\n" +
                "- Plane Plane\n" +
                "- double FrameSize, CellSize\n" +
                "- (optional) Point2d CenterXY",
                GH_ParamAccess.list);

            // 1) offset
            pManager.AddNumberParameter(
                "offset",
                "offset",
                "Iso level in g-space (contour where G == offset).",
                GH_ParamAccess.item,
                0.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "contours",
                "contours",
                "Iso-contours extracted from the input fields (world space).",
                GH_ParamAccess.list);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // ---------------------------------------------------------------------
            // 0) Read inputs
            // ---------------------------------------------------------------------
            var gooList = new List<IGH_Goo>();
            double offset = 0.0;

            if (!DA.GetDataList(0, gooList) || gooList == null || gooList.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No field_obj provided.");
                DA.SetDataList(0, new List<Curve>());
                return;
            }
            DA.GetData(1, ref offset);

            // ---------------------------------------------------------------------
            // 1) Unwrap GH wrappers + filter nulls
            // ---------------------------------------------------------------------
            var fields = new List<object>(gooList.Count);
            for (int i = 0; i < gooList.Count; i++)
            {
                var g = gooList[i];
                if (g == null) continue;

                if (g is GH_ObjectWrapper w && w.Value != null)
                    fields.Add(w.Value);
                else
                {
                    // Sometimes GH already provides a runtime object directly
                    object val;
                    if (g.CastTo(out val) && val != null) fields.Add(val);
                }
            }

            if (fields.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "field_obj contains only null/invalid items.");
                DA.SetDataList(0, new List<Curve>());
                return;
            }

            // ---------------------------------------------------------------------
            // 2) Validate packed field members on first item
            // ---------------------------------------------------------------------
            var t0 = fields[0].GetType();

            if (!HasMember(t0, "G") ||
                !HasMember(t0, "NxVerts") ||
                !HasMember(t0, "NyVerts") ||
                !HasMember(t0, "Plane") ||
                !HasMember(t0, "FrameSize") ||
                !HasMember(t0, "CellSize"))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "field_obj is not the expected packed field.\n" +
                    "Missing one or more required members: G, NxVerts, NyVerts, Plane, FrameSize, CellSize.");
                DA.SetDataList(0, new List<Curve>());
                return;
            }

            // ---------------------------------------------------------------------
            // 3) Marching squares per field (auto-parallel on number of fields)
            // ---------------------------------------------------------------------
            double docTol = RhinoDoc.ActiveDoc != null ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 1e-6;

            int K = fields.Count;
            bool useParallel = Environment.ProcessorCount > 1 && K >= 4;

            var outCrvs = new List<Curve>();
            object mergeLock = new object();
            int totalSegs = 0;
            int totalCrvCount = 0;

            Action<int> solveOne = (k) =>
            {
                object fk = fields[k];

                double[] g = GetDoubleArray(fk, "G");
                int nxv = GetInt(fk, "NxVerts");
                int nyv = GetInt(fk, "NyVerts");
                Plane pl = GetPlane(fk, "Plane");
                double frameSize = GetDouble(fk, "FrameSize");
                double cell = GetDouble(fk, "CellSize");

                // CenterXY is optional (older packs might not have it)
                Point2d centerXY = HasMember(fk.GetType(), "CenterXY")
                    ? GetPoint2d(fk, "CenterXY")
                    : new Point2d(0, 0);

                if (g == null || g.Length == 0 || nxv < 2 || nyv < 2 || !pl.IsValid)
                    return;
                if (frameSize <= RhinoMath.ZeroTolerance || cell <= RhinoMath.ZeroTolerance)
                    return;

                int nPts = Math.Min(g.Length, nxv * nyv);
                if (nPts < 4) return;

                // Build scalar field for marching squares:
                // val = g - offset, so contour is where val == 0
                var val = new double[nPts];
                for (int i = 0; i < nPts; i++)
                    val[i] = g[i] - offset;

                var segs = MarchingSquaresSegments_Field(pl, centerXY, frameSize, cell, nxv, nyv, val);
                if (segs.Count == 0) return;

                double chainTol = Math.Max(docTol, cell * 1e-3);
                var polys = ChainSegments(segs, chainTol);

                var localCrvs = new List<Curve>(polys.Count);
                for (int i = 0; i < polys.Count; i++)
                {
                    if (polys[i].Count < 2) continue;
                    localCrvs.Add(polys[i].ToNurbsCurve());
                }

                lock (mergeLock)
                {
                    totalSegs += segs.Count;
                    totalCrvCount += localCrvs.Count;
                    outCrvs.AddRange(localCrvs);
                }
            };

            if (useParallel)
                Parallel.For(0, K, solveOne);
            else
                for (int k = 0; k < K; k++) solveOne(k);

            // ---------------------------------------------------------------------
            // 4) Output + runtime note
            // ---------------------------------------------------------------------
            DA.SetDataList(0, outCrvs);

            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Remark,
                $"Contours: {totalCrvCount} curve(s) from {totalSegs} segment(s). Parallel={(useParallel ? "ON" : "OFF")}.");
        }

        // =====================================================================
        // Marching Squares (field_obj)
        // =====================================================================

        /// <summary>
        /// Builds marching-squares line segments in world space for a scalar field 'val'
        /// sampled on a (nxv x nyv) vertex grid.
        /// Grid vertex positions are derived in plane-local XY around centerXY with size frameSize.
        /// </summary>
        private List<Line> MarchingSquaresSegments_Field(
            Plane pl,
            Point2d centerXY,
            double frameSize,
            double cell,
            int nxv,
            int nyv,
            double[] val)
        {
            var segs = new List<Line>();
            if (nxv < 2 || nyv < 2) return segs;

            double half = frameSize * 0.5;

            Func<int, int, int> IDX = (i, j) => i + j * nxv;

            Func<int, int, Point3d> PtIJ = (i, j) =>
            {
                double x = centerXY.X - half + i * cell;
                double y = centerXY.Y - half + j * cell;
                return pl.Origin + x * pl.XAxis + y * pl.YAxis;
            };

            for (int j = 0; j < nyv - 1; j++)
            {
                for (int i = 0; i < nxv - 1; i++)
                {
                    int i00 = IDX(i, j);
                    int i10 = IDX(i + 1, j);
                    int i11 = IDX(i + 1, j + 1);
                    int i01 = IDX(i, j + 1);

                    double v00 = val[i00];
                    double v10 = val[i10];
                    double v11 = val[i11];
                    double v01 = val[i01];

                    // Marching-squares case code (sign test)
                    int code = 0;
                    if (v00 > 0) code |= 1;
                    if (v10 > 0) code |= 2;
                    if (v11 > 0) code |= 4;
                    if (v01 > 0) code |= 8;

                    if (code == 0 || code == 15) continue;

                    Point3d p00 = PtIJ(i, j);
                    Point3d p10 = PtIJ(i + 1, j);
                    Point3d p11 = PtIJ(i + 1, j + 1);
                    Point3d p01 = PtIJ(i, j + 1);

                    Func<Point3d> E0 = () => LerpIso(p00, p10, v00, v10);
                    Func<Point3d> E1 = () => LerpIso(p10, p11, v10, v11);
                    Func<Point3d> E2 = () => LerpIso(p11, p01, v11, v01);
                    Func<Point3d> E3 = () => LerpIso(p01, p00, v01, v00);

                    switch (code)
                    {
                        case 1: segs.Add(new Line(E3(), E0())); break;
                        case 2: segs.Add(new Line(E0(), E1())); break;
                        case 3: segs.Add(new Line(E3(), E1())); break;
                        case 4: segs.Add(new Line(E1(), E2())); break;
                        case 5: segs.Add(new Line(E3(), E0())); segs.Add(new Line(E1(), E2())); break;
                        case 6: segs.Add(new Line(E0(), E2())); break;
                        case 7: segs.Add(new Line(E3(), E2())); break;
                        case 8: segs.Add(new Line(E2(), E3())); break;
                        case 9: segs.Add(new Line(E0(), E2())); break;
                        case 10: segs.Add(new Line(E0(), E1())); segs.Add(new Line(E2(), E3())); break;
                        case 11: segs.Add(new Line(E1(), E2())); break;
                        case 12: segs.Add(new Line(E3(), E1())); break;
                        case 13: segs.Add(new Line(E0(), E1())); break;
                        case 14: segs.Add(new Line(E3(), E0())); break;
                    }
                }
            }

            return segs;
        }

        /// <summary>
        /// Linear interpolation point where iso==0 crosses edge (a..b) given scalar values (va..vb).
        /// </summary>
        private Point3d LerpIso(Point3d a, Point3d b, double va, double vb)
        {
            double denom = (va - vb);
            double t = (Math.Abs(denom) < 1e-16) ? 0.5 : (va / denom);
            t = Math.Max(0.0, Math.Min(1.0, t));
            return a + (b - a) * t;
        }

        // =====================================================================
        // Segment chaining
        // =====================================================================

        /// <summary>
        /// Chains unordered line segments into polylines using a hashed endpoint map.
        /// </summary>
        private List<Polyline> ChainSegments(List<Line> segs, double tol)
        {
            var result = new List<Polyline>();
            if (segs == null || segs.Count == 0) return result;

            Func<Point3d, string> key = p =>
            {
                double q = Math.Max(1e-9, tol);
                long x = (long)Math.Round(p.X / q);
                long y = (long)Math.Round(p.Y / q);
                long z = (long)Math.Round(p.Z / q);
                return x + "|" + y + "|" + z;
            };

            var keyMap = new Dictionary<string, List<int>>();
            for (int i = 0; i < segs.Count; i++)
            {
                var s = segs[i];
                string kA = key(s.From);
                string kB = key(s.To);

                if (!keyMap.ContainsKey(kA)) keyMap[kA] = new List<int>();
                if (!keyMap.ContainsKey(kB)) keyMap[kB] = new List<int>();

                keyMap[kA].Add(i);
                keyMap[kB].Add(i);
            }

            var used = new bool[segs.Count];

            for (int i = 0; i < segs.Count; i++)
            {
                if (used[i]) continue;

                var s = segs[i];
                used[i] = true;

                var poly = new List<Point3d> { s.From, s.To };

                Extend(poly, keyMap, used, segs, tol);

                poly.Reverse();
                Extend(poly, keyMap, used, segs, tol);

                // Close if endpoints match within tolerance
                if (poly.Count > 2 && poly[0].DistanceTo(poly[poly.Count - 1]) <= tol)
                    poly[poly.Count - 1] = poly[0];

                result.Add(new Polyline(poly));
            }

            return result;
        }

        /// <summary>
        /// Extends a polyline forward by following unused segments connected to its tip.
        /// </summary>
        private void Extend(List<Point3d> poly, Dictionary<string, List<int>> keyMap, bool[] used, List<Line> segs, double tol)
        {
            Func<Point3d, string> key = p =>
            {
                double q = Math.Max(1e-9, tol);
                long x = (long)Math.Round(p.X / q);
                long y = (long)Math.Round(p.Y / q);
                long z = (long)Math.Round(p.Z / q);
                return x + "|" + y + "|" + z;
            };

            while (true)
            {
                Point3d tip = poly[poly.Count - 1];
                string kt = key(tip);

                if (!keyMap.ContainsKey(kt)) break;

                int nextIdx = -1;
                bool reversed = false;

                var cand = keyMap[kt];
                for (int c = 0; c < cand.Count; c++)
                {
                    int idx = cand[c];
                    if (used[idx]) continue;

                    var ln = segs[idx];

                    if (ln.From.DistanceTo(tip) <= tol) { nextIdx = idx; reversed = false; break; }
                    if (ln.To.DistanceTo(tip) <= tol) { nextIdx = idx; reversed = true; break; }
                }

                if (nextIdx < 0) break;

                used[nextIdx] = true;

                var nx = segs[nextIdx];
                Point3d nextPt = reversed ? nx.From : nx.To;

                if (nextPt.DistanceTo(tip) > 1e-12)
                    poly.Add(nextPt);
                else
                    break;
            }
        }

        // =====================================================================
        // Reflection helpers (packed field access)
        // =====================================================================

        private bool HasMember(Type t, string name)
        {
            return (t.GetField(name) != null) || (t.GetProperty(name) != null);
        }

        private double[] GetDoubleArray(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();

            var f = t.GetField(name);
            if (f != null) return f.GetValue(o) as double[];

            var p = t.GetProperty(name);
            if (p != null) return p.GetValue(o, null) as double[];

            return null;
        }

        private int GetInt(object o, string name)
        {
            if (o == null) return 0;
            var t = o.GetType();

            var f = t.GetField(name);
            if (f != null) return Convert.ToInt32(f.GetValue(o));

            var p = t.GetProperty(name);
            if (p != null) return Convert.ToInt32(p.GetValue(o, null));

            return 0;
        }

        private double GetDouble(object o, string name)
        {
            if (o == null) return 0.0;
            var t = o.GetType();

            var f = t.GetField(name);
            if (f != null) return Convert.ToDouble(f.GetValue(o));

            var p = t.GetProperty(name);
            if (p != null) return Convert.ToDouble(p.GetValue(o, null));

            return 0.0;
        }

        private Plane GetPlane(object o, string name)
        {
            if (o == null) return Plane.Unset;
            var t = o.GetType();

            var f = t.GetField(name);
            if (f != null) return (Plane)f.GetValue(o);

            var p = t.GetProperty(name);
            if (p != null) return (Plane)p.GetValue(o, null);

            return Plane.Unset;
        }

        private Point2d GetPoint2d(object o, string name)
        {
            if (o == null) return new Point2d(0, 0);
            var t = o.GetType();

            var f = t.GetField(name);
            if (f != null) return (Point2d)f.GetValue(o);

            var p = t.GetProperty(name);
            if (p != null) return (Point2d)p.GetValue(o, null);

            return new Point2d(0, 0);
        }
    }
}
