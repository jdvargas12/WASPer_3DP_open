#define USE_PARALLEL

#region Usings
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
#endregion

namespace WASPer_3DP.Components._2_2_Fields
{
    /// <summary>
    /// wsp_Fi03_Blend Fields
    /// Piecewise interpolation of multiple packed field_obj grids (g-space) across a list of range values.
    ///
    /// How it works:
    /// - Input fields are assumed ordered: F0, F1, F2, ...
    /// - The [0..1] range is split into (N-1) segments.
    /// - Each alpha t falls into a segment s and blends between Fs and F(s+1).
    ///
    /// Notes:
    /// - If field grids have mismatched dimensions, blending uses the minimum overlap (minNx, minNy, minLen).
    /// - Output objects are reconstructed using the SAME runtime packed type as the first field (Fi01 signature).
    /// - This component does not build a mesh preview (it only outputs packed fields).
    /// </summary>
    public class wsp_Fi03_Blend_Fields : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Fi03_Blend_Fields()
            : base(
                "wsp_Fi03_Blend Fields",
                "BlendField",
                "Interpolates multiple packed field_obj grids (g-space) into multiple steps defined by 'range'.\n" +
                "Blending is piecewise in input order:\n" +
                "- With N fields, the 0..1 domain is split into (N-1) segments.\n" +
                "- Each range value t blends between fields [s] and [s+1] of its segment.\n\n" +
                "Grid mismatches:\n" +
                "- If input grids differ in Nx/Ny/length, blending uses minimum overlap.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "2.2_Fields")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("F2A4D9A2-7DA6-47B9-9E1B-4F2D42F5D3B7");

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Fi03_Blend Fields.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        // -----------------------------------------------------------------------------
        // INPUTS
        // -----------------------------------------------------------------------------
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            // 0) field_obj (LIST)
            pManager.AddGenericParameter(
                "field_obj",
                "field",
                "List of packed field objects (Generic).\n" +
                "Each item should be a GH_ObjectWrapper of the packed field type produced by Fi01.\n" +
                "Required members: G, NxVerts, NyVerts, Plane, CenterXY, FrameSize, CellSize, IsoOffset.",
                GH_ParamAccess.list);

            // 1) range (LIST)
            pManager.AddNumberParameter(
                "range",
                "t",
                "Interpolation parameters in [0..1].\n" +
                "Any easing is allowed; values are clamped to [0..1].\n" +
                "If empty, defaults to {0, 1}.",
                GH_ParamAccess.list);
        }

        // -----------------------------------------------------------------------------
        // OUTPUTS
        // -----------------------------------------------------------------------------
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            // 0) interp_field_obj (LIST)
            pManager.AddGenericParameter(
                "interp_field_obj",
                "fields",
                "List of blended packed field objects (Generic), one per range value.",
                GH_ParamAccess.list);
        }

        // -----------------------------------------------------------------------------
        // SOLVE
        // -----------------------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // -------------------------
            // 0) Read inputs
            // -------------------------
            var fieldObjIn = new List<object>();
            var rangeIn = new List<double>();

            if (!DA.GetDataList(0, fieldObjIn) || fieldObjIn == null || fieldObjIn.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "field_obj list is empty.");
                return;
            }
            DA.GetDataList(1, rangeIn);

            // -------------------------
            // 1) Unwrap GH_ObjectWrapper + filter nulls
            // -------------------------
            var fields = new List<object>(fieldObjIn.Count);
            foreach (var o in fieldObjIn)
            {
                if (o == null) continue;

                if (o is GH_ObjectWrapper w && w.Value != null)
                    fields.Add(w.Value);
                else
                    fields.Add(o);
            }

            if (fields.Count < 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Need at least 2 field_obj items to blend.");
                return;
            }

            // -------------------------
            // 2) Default / clamp range
            // -------------------------
            if (rangeIn == null || rangeIn.Count == 0)
            {
                rangeIn = new List<double> { 0.0, 1.0 };
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "range was empty ? using {0, 1}.");
            }

            var alphas = new List<double>(rangeIn.Count);
            for (int i = 0; i < rangeIn.Count; i++)
                alphas.Add(Clamp01(rangeIn[i]));

            // -------------------------
            // 3) Packed type + required members + constructor
            // -------------------------
            object f0 = fields[0];
            Type packedType = f0.GetType();

            // Minimal required members
            if (!HasMember(packedType, "G") || !HasMember(packedType, "NxVerts") || !HasMember(packedType, "NyVerts"))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "field_obj does not match expected packed field type (missing G/NxVerts/NyVerts).");
                return;
            }

            // Constructor signature from Fi01
            var ctor = packedType.GetConstructor(new Type[]
            {
                typeof(double[]), typeof(int), typeof(int),
                typeof(Plane), typeof(Point2d),
                typeof(double), typeof(double), typeof(double)
            });

            if (ctor == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not find expected constructor on packed field type. Expected: (double[], int, int, Plane, Point2d, double, double, double).");
                return;
            }

            // -------------------------
            // 4) Extract base metadata from first field
            // -------------------------
            double[] G0 = GetDoubleArray(f0, "G");
            int nxv = GetInt(f0, "NxVerts");
            int nyv = GetInt(f0, "NyVerts");
            Plane pl = GetPlane(f0, "Plane");
            Point2d centerXY = GetPoint2d(f0, "CenterXY");
            double frameSize = GetDouble(f0, "FrameSize");
            double cellSize = GetDouble(f0, "CellSize");
            double isoOffset = GetDouble(f0, "IsoOffset");

            if (G0 == null || G0.Length == 0 || nxv <= 0 || nyv <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "First field_obj has invalid grid data.");
                return;
            }

            int nPtsRef = nxv * nyv;
            if (G0.Length != nPtsRef)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    $"Warning: First field G length ({G0.Length}) != NxVerts*NyVerts ({nPtsRef}). Using min overlap.");

            // -------------------------
            // 5) Read all fields arrays and compute overlap domain
            // -------------------------
            var allG = new List<double[]>(fields.Count);

            int minLen = int.MaxValue;
            int minNxv = nxv;
            int minNyv = nyv;

            int skipped = 0;

            for (int i = 0; i < fields.Count; i++)
            {
                object fo = fields[i];

                double[] Gi = GetDoubleArray(fo, "G");
                int nxvi = GetInt(fo, "NxVerts");
                int nyvi = GetInt(fo, "NyVerts");

                if (Gi == null || Gi.Length == 0)
                {
                    skipped++;
                    continue;
                }

                // Expected same grid; if not, we will use min overlap
                if (nxvi != nxv || nyvi != nyv)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                        $"Warning: Field {i} grid mismatch (Nx={nxvi},Ny={nyvi}) vs first (Nx={nxv},Ny={nyv}). Using minimum overlap.");
                }

                minNxv = Math.Min(minNxv, nxvi);
                minNyv = Math.Min(minNyv, nyvi);
                minLen = Math.Min(minLen, Gi.Length);

                allG.Add(Gi);
            }

            if (skipped > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"Skipped {skipped} invalid field(s).");

            if (allG.Count < 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "After filtering invalid fields, fewer than 2 remain.");
                return;
            }

            int nPts = Math.Min(minLen, minNxv * minNyv);
            if (nPts <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No overlapping grid data to blend.");
                return;
            }

            int fieldCount = allG.Count;
            int segments = fieldCount - 1;
            double segLen = (segments > 0) ? (1.0 / segments) : 1.0;

            // -------------------------
            // 6) Blend per alpha (optional parallel)
            // -------------------------
            var outArr = new object[alphas.Count];

            bool useParallel =
                Environment.ProcessorCount > 1 &&
                alphas.Count >= 16 &&
                nPts >= 25000;

#if USE_PARALLEL
            if (useParallel)
            {
                Parallel.For(0, alphas.Count, k =>
                {
                    outArr[k] = BuildBlendedFieldObject(alphas[k], allG, segments, segLen, nPts, ctor, minNxv, minNyv, pl, centerXY, frameSize, cellSize, isoOffset);
                });
            }
            else
            {
                for (int k = 0; k < alphas.Count; k++)
                    outArr[k] = BuildBlendedFieldObject(alphas[k], allG, segments, segLen, nPts, ctor, minNxv, minNyv, pl, centerXY, frameSize, cellSize, isoOffset);
            }
#else
			useParallel = false;
			for (int k = 0; k < alphas.Count; k++)
				outArr[k] = BuildBlendedFieldObject(alphas[k], allG, segments, segLen, nPts, ctor, minNxv, minNyv, pl, centerXY, frameSize, cellSize, isoOffset);
#endif

            // Collect results (filter any nulls, but should not happen)
            var outList = new List<object>(outArr.Length);
            for (int i = 0; i < outArr.Length; i++)
            {
                if (outArr[i] != null)
                    outList.Add(outArr[i]);
            }

            DA.SetDataList(0, outList);

            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                $"Blended {outList.Count} step(s) from {fieldCount} field(s) (segments={segments}). Parallel={(useParallel ? "ON" : "OFF")}.");
        }

        // -----------------------------------------------------------------------------
        // Build one blended packed field for a given alpha t
        // -----------------------------------------------------------------------------
        private object BuildBlendedFieldObject(
            double t,
            List<double[]> allG,
            int segments,
            double segLen,
            int nPts,
            System.Reflection.ConstructorInfo ctor,
            int nxv,
            int nyv,
            Plane pl,
            Point2d centerXY,
            double frameSize,
            double cellSize,
            double isoOffset)
        {
            // Map t into segment s and local alpha in [0..1]
            int s = (segments == 0) ? 0 : Math.Min(segments - 1, (int)Math.Floor(t / segLen));
            double t0 = s * segLen;
            double local = (segments == 0) ? 0.0 : (t - t0) / segLen;
            local = Clamp01(local);

            double[] A = allG[s];
            double[] B = allG[s + 1];

            // Blend over overlap length
            var gOut = new double[nPts];
            double w0 = 1.0 - local;
            double w1 = local;

            for (int idx = 0; idx < nPts; idx++)
                gOut[idx] = w0 * A[idx] + w1 * B[idx];

            // Create packed output object (same type/signature as Fi01)
            object packedOut = ctor.Invoke(new object[]
            {
                gOut, nxv, nyv,
                pl, centerXY,
                frameSize, cellSize,
                isoOffset
            });

            return new GH_ObjectWrapper(packedOut);
        }

        // =========================================================================
        // Reflection Helpers
        // =========================================================================
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

        private double Clamp01(double x)
        {
            if (x < 0.0) return 0.0;
            if (x > 1.0) return 1.0;
            return x;
        }
    }
}
