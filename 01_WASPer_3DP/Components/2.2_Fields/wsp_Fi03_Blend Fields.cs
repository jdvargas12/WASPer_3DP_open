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
    /// Overview
    /// --------
    /// This component blends (interpolates) a set of packed field objects along a 0..1 domain.
    /// For each requested parameter t (range), it returns a blended packed field object.
    ///
    /// Keying modes
    /// ------------
    /// 1) Evenly spaced keys (default):
    ///		- If field_range_ref is empty, fields are distributed evenly:
    ///			keys[i] = i / (N-1)
    ///
    /// 2) Custom keys (field_range_ref):
    ///		- If field_range_ref has N values (same as field count), those values place each field along the domain.
    ///		- Keys are clamped to [0..1], then sorted.
    ///		- Fields are REORDERED to match the sorted keys (pairs are sorted together).
    ///		- Duplicate keys create zero-length segments; blending behaves like step transitions there (warning shown).
    ///
    /// Field rotation (field_rotation)
    /// -------------------------------
    /// Optional per-field rotations (degrees) applied to the scalar grid G BEFORE blending.
    /// IMPORTANT: This rotates the PATTERN inside the same Nx*Ny grid (metadata plane/frame stays unchanged).
    /// Implementation uses bilinear sampling in grid index space and clamps samples to edge.
    ///
    /// Notes
    /// -----
    /// - Input fields are assumed to be packed objects produced by Fi01 (same constructor signature).
    /// - If field grids have mismatched dimensions, blending uses the minimum overlap (minNx, minNy, minLen).
    /// - Output objects are reconstructed using the SAME runtime packed type as the first valid field.
    /// - This component does not build a mesh preview (it only outputs packed fields).
    /// </summary>
    public class wsp_Fi03_Blend_Fields : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Fi03_Blend_Fields()
            : base(
                "wsp_Fi03_Blend Fields",
                "BlendField",
                "Blends multiple packed field_obj grids (g-space) into multiple steps defined by 'range'.\n" +
                "Blending is piecewise across the 0..1 domain:\n" +
                "- Default: fields are keyed evenly (0..1).\n" +
                "- Optional: supply field_range_ref to place each field at custom key positions.\n\n" +
                "Key behavior:\n" +
                "- field_range_ref is clamped to [0..1], then sorted; field_obj order is reordered to match.\n" +
                "- Duplicate keys create zero-length segments (step-like behavior).\n\n" +
                "Field rotation:\n" +
                "- Optional field_rotation (degrees) rotates the scalar grid pattern per field before blending.\n" +
                "- Frame/plane metadata is NOT rotated (only G values are resampled).\n\n" +
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
                "Values are clamped to [0..1].\n" +
                "If empty, defaults to {0, 1}.",
                GH_ParamAccess.list);

            // 2) field_range_ref (LIST, OPTIONAL)
            pManager.AddNumberParameter(
                "field_range_ref",
                "keys",
                "Optional key positions for each field_obj in [0..1].\n" +
                "- If empty: fields are distributed evenly.\n" +
                "- If count < field_obj count: warning + fallback to evenly spaced.\n" +
                "- If count > field_obj count: extra values ignored (remark).\n" +
                "Keys are clamped and sorted; fields are reordered to match the sorted keys.\n" +
                "Duplicate keys produce zero-length segments (step-like behavior).",
                GH_ParamAccess.list);
            pManager[2].Optional = true;

            // 3) field_rotation (LIST, OPTIONAL)
            pManager.AddNumberParameter(
                "field_rotation",
                "rot",
                "Optional per-field rotation (degrees) applied to the scalar grid pattern BEFORE blending.\n" +
                "IMPORTANT:\n" +
                "- Rotates the G values within the same Nx*Ny grid (metadata plane/frame is NOT rotated).\n" +
                "- Uses bilinear sampling in grid index space.\n" +
                "- Samples outside grid are clamped to edge.\n\n" +
                "Count rules:\n" +
                "- If empty: all rotations = 0.\n" +
                "- If count < field_obj count: warning; missing rotations assumed 0.\n" +
                "- If count > field_obj count: extra values ignored (remark).",
                GH_ParamAccess.list);
            pManager[3].Optional = true;
        }

        // -----------------------------------------------------------------------------
        // OUTPUTS
        // -----------------------------------------------------------------------------
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
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
            var keysIn = new List<double>();
            var rotIn = new List<double>();

            if (!DA.GetDataList(0, fieldObjIn) || fieldObjIn == null || fieldObjIn.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "field_obj list is empty.");
                return;
            }

            DA.GetDataList(1, rangeIn);
            DA.GetDataList(2, keysIn);
            DA.GetDataList(3, rotIn);

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
            // 3) Rotations: normalize list length to field count
            // -------------------------
            int fieldCountInput = fields.Count;

            var rotations = new List<double>(fieldCountInput);

            if (rotIn == null || rotIn.Count == 0)
            {
                for (int i = 0; i < fieldCountInput; i++)
                    rotations.Add(0.0);
            }
            else
            {
                if (rotIn.Count < fieldCountInput)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"field_rotation has fewer values ({rotIn.Count}) than field_obj ({fieldCountInput}). Missing rotations assumed 0°.");

                if (rotIn.Count > fieldCountInput)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                        $"field_rotation has more values ({rotIn.Count}) than field_obj ({fieldCountInput}). Extra values will be ignored.");

                for (int i = 0; i < fieldCountInput; i++)
                {
                    double r = (i < rotIn.Count) ? rotIn[i] : 0.0;
                    rotations.Add(r);
                }
            }

            // -------------------------
            // 4) Keys: build + validate + sort (paired with fields AND rotations)
            // -------------------------
            bool usingCustomKeys = false;
            bool keysWereReordered = false;
            bool duplicateKeysDetected = false;

            List<double> keys;
            List<object> fieldsOrdered;
            List<double> rotationsOrdered;

            if (keysIn == null || keysIn.Count == 0)
            {
                keys = BuildEvenKeys(fieldCountInput);
                fieldsOrdered = fields;
                rotationsOrdered = rotations;
                usingCustomKeys = false;
            }
            else if (keysIn.Count < fieldCountInput)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"field_range_ref has fewer values ({keysIn.Count}) than field_obj ({fieldCountInput}). Falling back to evenly spaced keys.");

                keys = BuildEvenKeys(fieldCountInput);
                fieldsOrdered = fields;
                rotationsOrdered = rotations;
                usingCustomKeys = false;
            }
            else
            {
                if (keysIn.Count > fieldCountInput)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                        $"field_range_ref has more values ({keysIn.Count}) than field_obj ({fieldCountInput}). Extra values will be ignored.");
                }

                usingCustomKeys = true;

                var triplets = new List<(double key, object field, double rot, int originalIndex)>(fieldCountInput);
                for (int i = 0; i < fieldCountInput; i++)
                    triplets.Add((Clamp01(keysIn[i]), fields[i], rotations[i], i));

                var sorted = triplets.OrderBy(t => t.key).ToList();

                for (int i = 0; i < sorted.Count; i++)
                {
                    if (sorted[i].originalIndex != i)
                    {
                        keysWereReordered = true;
                        break;
                    }
                }

                sorted.Sort((a, b) => a.key.CompareTo(b.key));

                keys = new List<double>(fieldCountInput);
                fieldsOrdered = new List<object>(fieldCountInput);
                rotationsOrdered = new List<double>(fieldCountInput);

                for (int i = 0; i < sorted.Count; i++)
                {
                    keys.Add(sorted[i].key);
                    fieldsOrdered.Add(sorted[i].field);
                    rotationsOrdered.Add(sorted[i].rot);
                }

                for (int i = 1; i < keys.Count; i++)
                {
                    if (Math.Abs(keys[i] - keys[i - 1]) < 1e-12)
                    {
                        duplicateKeysDetected = true;
                        break;
                    }
                }

                if (keysWereReordered)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "field_range_ref was sorted; field_obj order (and field_rotation pairing) were reordered to match the sorted keys.");

                if (duplicateKeysDetected)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "field_range_ref contains duplicate key values. This creates zero-length segments; blending behaves like steps at those locations.");
            }

            fields = fieldsOrdered;
            rotations = rotationsOrdered;

            if (keys.Count != fields.Count || rotations.Count != fields.Count)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Internal error: keys/fields/rotations counts do not match.");
                return;
            }

            // -------------------------
            // 5) Packed type + required members + constructor
            // -------------------------
            object f0 = fields[0];
            Type packedType = f0.GetType();

            if (!HasMember(packedType, "G") || !HasMember(packedType, "NxVerts") || !HasMember(packedType, "NyVerts"))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "field_obj does not match expected packed field type (missing G/NxVerts/NyVerts).");
                return;
            }

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
            // 6) Extract base metadata from first field
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
            // 7) Read all fields arrays and compute overlap domain
            //		(No skipping: skipping would desync key/rotation mapping.)
            // -------------------------
            var allG = new List<double[]>(fields.Count);

            int minLen = int.MaxValue;
            int minNxv = nxv;
            int minNyv = nyv;

            for (int i = 0; i < fields.Count; i++)
            {
                object fo = fields[i];

                double[] Gi = GetDoubleArray(fo, "G");
                int nxvi = GetInt(fo, "NxVerts");
                int nyvi = GetInt(fo, "NyVerts");

                if (Gi == null || Gi.Length == 0 || nxvi <= 0 || nyvi <= 0)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, $"Field {i} has invalid grid data (G/NxVerts/NyVerts). Fix input list.");
                    return;
                }

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

            int nPts = Math.Min(minLen, minNxv * minNyv);
            if (nPts <= 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No overlapping grid data to blend.");
                return;
            }

            // -------------------------
            // 8) Apply per-field rotation (pattern-only) BEFORE blending
            // -------------------------
            var allGRot = new double[allG.Count][];

            bool anyRotation =
                rotations != null &&
                rotations.Count == allG.Count &&
                rotations.Any(r => Math.Abs(NormalizeDeg(r)) > 1e-12);

            bool useParallelRotate =
                Environment.ProcessorCount > 1 &&
                allG.Count >= 4 &&
                nPts >= 50000;

#if USE_PARALLEL
            if (anyRotation && useParallelRotate)
            {
                Parallel.For(0, allG.Count, i =>
                {
                    allGRot[i] = RotateGridPattern(allG[i], minNxv, minNyv, rotations[i]);
                });
            }
            else
            {
                for (int i = 0; i < allG.Count; i++)
                {
                    allGRot[i] = anyRotation
                        ? RotateGridPattern(allG[i], minNxv, minNyv, rotations[i])
                        : allG[i];
                }
            }
#else
			for (int i = 0; i < allG.Count; i++)
			{
				allGRot[i] = anyRotation
					? RotateGridPattern(allG[i], minNxv, minNyv, rotations[i])
					: allG[i];
			}
#endif

            // -------------------------
            // 9) Blend per alpha (optional parallel)
            // -------------------------
            var outArr = new object[alphas.Count];

            bool useParallelBlend =
                Environment.ProcessorCount > 1 &&
                alphas.Count >= 16 &&
                nPts >= 25000;

#if USE_PARALLEL
            if (useParallelBlend)
            {
                Parallel.For(0, alphas.Count, k =>
                {
                    outArr[k] = BuildBlendedFieldObject(
                        alphas[k],
                        allGRot,
                        keys,
                        nPts,
                        ctor,
                        minNxv,
                        minNyv,
                        pl,
                        centerXY,
                        frameSize,
                        cellSize,
                        isoOffset);
                });
            }
            else
            {
                for (int k = 0; k < alphas.Count; k++)
                {
                    outArr[k] = BuildBlendedFieldObject(
                        alphas[k],
                        allGRot,
                        keys,
                        nPts,
                        ctor,
                        minNxv,
                        minNyv,
                        pl,
                        centerXY,
                        frameSize,
                        cellSize,
                        isoOffset);
                }
            }
#else
			useParallelBlend = false;
			for (int k = 0; k < alphas.Count; k++)
			{
				outArr[k] = BuildBlendedFieldObject(
					alphas[k],
					allGRot,
					keys,
					nPts,
					ctor,
					minNxv,
					minNyv,
					pl,
					centerXY,
					frameSize,
					cellSize,
					isoOffset);
			}
#endif

            var outList = new List<object>(outArr.Length);
            for (int i = 0; i < outArr.Length; i++)
            {
                if (outArr[i] != null)
                    outList.Add(outArr[i]);
            }

            DA.SetDataList(0, outList);

            string mode = usingCustomKeys ? "CustomKeys" : "EvenKeys";
            string rotMode = anyRotation ? "Rot=ON" : "Rot=OFF";

            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                $"Blended {outList.Count} step(s) from {allGRot.Length} field(s). Mode={mode}. {rotMode}. ParallelBlend={(useParallelBlend ? "ON" : "OFF")}.");
        }

        // -----------------------------------------------------------------------------
        // Build one blended packed field for a given alpha t (key-based)
        // -----------------------------------------------------------------------------
        private object BuildBlendedFieldObject(
            double t,
            double[][] allG,
            List<double> keys,
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
            int fieldCount = allG.Length;
            int segments = fieldCount - 1;
            if (segments <= 0)
                return null;

            t = Clamp01(t);

            if (t <= keys[0])
                return WrapPacked(ctor, allG[0], nPts, nxv, nyv, pl, centerXY, frameSize, cellSize, isoOffset);

            if (t >= keys[keys.Count - 1])
                return WrapPacked(ctor, allG[fieldCount - 1], nPts, nxv, nyv, pl, centerXY, frameSize, cellSize, isoOffset);

            int s = 0;
            for (int i = 0; i < segments; i++)
            {
                if (t >= keys[i] && t < keys[i + 1])
                {
                    s = i;
                    break;
                }
            }

            double k0 = keys[s];
            double k1 = keys[s + 1];

            double denom = (k1 - k0);
            double local;

            if (Math.Abs(denom) < 1e-12)
            {
                // Zero-length segment -> step behavior (choose B)
                local = 1.0;
            }
            else
            {
                local = (t - k0) / denom;
                local = Clamp01(local);
            }

            double[] A = allG[s];
            double[] B = allG[s + 1];

            var gOut = new double[nPts];
            double w0 = 1.0 - local;
            double w1 = local;

            for (int idx = 0; idx < nPts; idx++)
                gOut[idx] = w0 * A[idx] + w1 * B[idx];

            object packedOut = ctor.Invoke(new object[]
            {
                gOut, nxv, nyv,
                pl, centerXY,
                frameSize, cellSize,
                isoOffset
            });

            return new GH_ObjectWrapper(packedOut);
        }

        private object WrapPacked(
            System.Reflection.ConstructorInfo ctor,
            double[] g,
            int nPts,
            int nxv,
            int nyv,
            Plane pl,
            Point2d centerXY,
            double frameSize,
            double cellSize,
            double isoOffset)
        {
            var gOut = new double[nPts];
            Array.Copy(g, gOut, nPts);

            object packedOut = ctor.Invoke(new object[]
            {
                gOut, nxv, nyv,
                pl, centerXY,
                frameSize, cellSize,
                isoOffset
            });

            return new GH_ObjectWrapper(packedOut);
        }

        // -----------------------------------------------------------------------------
        // Rotate scalar grid pattern within the same Nx*Ny (pattern-only, metadata unchanged)
        // -----------------------------------------------------------------------------
        private double[] RotateGridPattern(double[] g, int nxv, int nyv, double deg)
        {
            double a = NormalizeDeg(deg);
            if (Math.Abs(a) < 1e-12)
                return g;

            int n = nxv * nyv;
            int len = Math.Min(g.Length, n);

            // If g is longer than nxv*nyv, we only rotate the overlapped rectangle
            // and ignore trailing values (consistent with min overlap blending).
            var src = new double[n];
            Array.Copy(g, src, Math.Min(len, n));

            var dst = new double[n];

            double rad = a * Math.PI / 180.0;
            double cos = Math.Cos(rad);
            double sin = Math.Sin(rad);

            // Center in index space
            double cx = (nxv - 1) * 0.5;
            double cy = (nyv - 1) * 0.5;

            for (int iy = 0; iy < nyv; iy++)
            {
                for (int ix = 0; ix < nxv; ix++)
                {
                    // Destination point relative to center
                    double x = ix - cx;
                    double y = iy - cy;

                    // Inverse rotation to sample from source (rotate pattern by +a => sample with -a)
                    // xs = cos*x + sin*y
                    // ys = -sin*x + cos*y
                    double xs = cos * x + sin * y + cx;
                    double ys = -sin * x + cos * y + cy;

                    double v = SampleBilinearClamp(src, nxv, nyv, xs, ys);

                    dst[iy * nxv + ix] = v;
                }
            }

            return dst;
        }

        private double SampleBilinearClamp(double[] g, int nxv, int nyv, double x, double y)
        {
            // Clamp continuous coords to valid range
            if (x < 0.0) x = 0.0;
            if (y < 0.0) y = 0.0;
            if (x > nxv - 1) x = nxv - 1;
            if (y > nyv - 1) y = nyv - 1;

            int x0 = (int)Math.Floor(x);
            int y0 = (int)Math.Floor(y);
            int x1 = x0 + 1;
            int y1 = y0 + 1;

            if (x1 >= nxv) x1 = nxv - 1;
            if (y1 >= nyv) y1 = nyv - 1;

            double tx = x - x0;
            double ty = y - y0;

            double v00 = g[y0 * nxv + x0];
            double v10 = g[y0 * nxv + x1];
            double v01 = g[y1 * nxv + x0];
            double v11 = g[y1 * nxv + x1];

            double a = v00 * (1.0 - tx) + v10 * tx;
            double b = v01 * (1.0 - tx) + v11 * tx;

            return a * (1.0 - ty) + b * ty;
        }

        private double NormalizeDeg(double deg)
        {
            // Keep it stable for large values
            double a = deg % 360.0;
            if (a < -180.0) a += 360.0;
            if (a > 180.0) a -= 360.0;
            return a;
        }

        // =========================================================================
        // Reflection + misc helpers
        // =========================================================================
        private List<double> BuildEvenKeys(int n)
        {
            var keys = new List<double>(n);

            if (n <= 1)
            {
                keys.Add(0.0);
                return keys;
            }

            double denom = (double)(n - 1);
            for (int i = 0; i < n; i++)
                keys.Add(i / denom);

            return keys;
        }

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