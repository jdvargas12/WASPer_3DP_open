#define USE_PARALLEL

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
#endregion

namespace WASPer_3DP.Components._2_2_Fields
{
    /// <summary>
    /// wsp_Fi04_Fields to Frames (perp) v2
    /// Places packed field_obj grids onto perpendicular frames along an axis curve.
    ///
    /// Outputs are DataTrees, one branch per step {k}, where k is ALWAYS ordered from
    /// the beginning of the curve to the end of the curve (based on range values).
    ///
    /// flip_ends:
    /// - Only swaps which FIELD is assigned to the ordered FRAMES.
    /// - The frame order stays start->end, so {0} is always the start frame.
    ///
    /// What you get (DataTrees, one branch per step = {k}):
    /// - field_mesh:   {k}[0] -> one square mesh per step, oriented on the curve frame
    /// - field_obj:    {k}[0] -> packed field re-packed with Plane = frame and CenterXY = (0,0)
    /// - field_frames: {k}[0] -> the Plane used for each step
    ///
    /// Notes:
    /// - If range count mismatches field count, even spacing is used.
    /// - Packed field type must match Fi01 signature:
    ///   ctor(double[] g, int nxVerts, int nyVerts, Plane plane, Point2d centerXY, double frameSize, double cellSize, double isoOffset)
    /// </summary>
    public class wsp_Fi04_Fields_To_Frames_v2 : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Fi04_Fields_To_Frames_v2()
            : base(
                "wsp_Fi04_Fields to Frames",
                "Fields?Frames",
                "Places packed field_obj grids onto perpendicular frames along an axis curve.\n" +
                "Outputs DataTrees (one branch per step): mesh, oriented field_obj, and the planes used per step.\n" +
                "Output order is always start?end along the curve (sorted by range). Optional flip swaps which field is mapped to start/end.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "2.2_Fields")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        // Use YOUR unique GUID for this compiled component
        public override Guid ComponentGuid => new Guid("8C5B7F61-79C8-4FAE-9C4D-2B3C6C9C9F3A");

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Fi04_Fields to Frames.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        // ---------------------------------------------------------------------
        // INPUTS
        // ---------------------------------------------------------------------
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            // 0) axis_curve
            pManager.AddCurveParameter(
                "axis_curve",
                "axis",
                "Axis curve used to generate perpendicular frames.\n" +
                "One frame will be generated per input field (or per range value if provided).",
                GH_ParamAccess.item);

            // 1) interp_field_obj (LIST)
            pManager.AddGenericParameter(
                "interp_field_obj",
                "fields",
                "List of packed field objects (Generic / GH_ObjectWrapper) to place on curve frames.\n" +
                "Each packed field must expose members: G, NxVerts, NyVerts, FrameSize, CellSize, IsoOffset.\n" +
                "(Plane and CenterXY are optional on input; output will be re-packed with Plane=frame, CenterXY=(0,0)).",
                GH_ParamAccess.list);

            // 2) range (LIST, optional)
            pManager.AddNumberParameter(
                "range",
                "t",
                "Normalized positions along the curve in [0..1].\n" +
                "If count mismatches the number of fields, even spacing is used.\n" +
                "IMPORTANT: outputs are sorted by these values so {0} is always the start of the curve.",
                GH_ParamAccess.list);
            pManager[2].Optional = true;

            // 3) flip_ends (BOOL, optional)
            pManager.AddBooleanParameter(
                "flip_ends",
                "flip",
                "If true, swaps which field is assigned to the ordered frames.\n" +
                "Frame order still stays start?end, so {0} is always the start frame.\n" +
                "Example with 2 fields:\n" +
                "- flip=false: {0}=Field0, {1}=Field1\n" +
                "- flip=true:  {0}=Field1, {1}=Field0",
                GH_ParamAccess.item,
                false);
        }

        // ---------------------------------------------------------------------
        // OUTPUTS (TREES: one branch per step {k})
        // ---------------------------------------------------------------------
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "field_mesh",
                "mesh",
                "Tree output: one branch per step {k} (ordered start?end).\n" +
                "Each branch contains one mesh oriented on the axis curve frame.\n" +
                "Vertex colors are derived from g values (diverging blue-white-red).",
                GH_ParamAccess.tree);

            pManager.AddGenericParameter(
                "field_obj",
                "field",
                "Tree output: one branch per step {k} (ordered start?end).\n" +
                "Each branch contains one packed field object (Generic / GH_ObjectWrapper).\n" +
                "Plane is set to the step frame, CenterXY is set to (0,0).",
                GH_ParamAccess.tree);

            pManager.AddPlaneParameter(
                "field_frames",
                "frames",
                "Tree output: one branch per step {k} (ordered start?end).\n" +
                "Each branch contains the plane used for that step (aligned with mesh + field).",
                GH_ParamAccess.tree);
        }

        // ---------------------------------------------------------------------
        // SOLVE
        // ---------------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // =========================================================================
            // 0) Read inputs
            // =========================================================================
            Curve axis = null;
            var interpFieldObj = new List<object>();
            var range = new List<double>();
            bool flipEnds = false;

            if (!DA.GetData(0, ref axis) || axis == null || !axis.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "axis_curve is null/invalid.");
                return;
            }

            if (!DA.GetDataList(1, interpFieldObj) || interpFieldObj == null || interpFieldObj.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "interp_field_obj is empty.");
                return;
            }

            DA.GetDataList(2, range);
            DA.GetData(3, ref flipEnds);

            // =========================================================================
            // 1) Unwrap GH_ObjectWrapper, filter nulls
            // =========================================================================
            var rawFields = new List<object>(interpFieldObj.Count);
            foreach (var o in interpFieldObj)
            {
                if (o == null) continue;

                if (o is GH_ObjectWrapper w && w.Value != null)
                    rawFields.Add(w.Value);
                else
                    rawFields.Add(o);
            }

            if (rawFields.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "interp_field_obj contains only nulls.");
                return;
            }

            int K = rawFields.Count;

            // =========================================================================
            // 2) Range validation (even spacing if mismatch) + clamp
            // =========================================================================
            if (range == null || range.Count != K)
            {
                var def = new List<double>(K);
                for (int i = 0; i < K; i++)
                    def.Add(K == 1 ? 0.0 : (double)i / (K - 1));

                range = def;

                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"range length mismatch (got {(range == null ? 0 : range.Count)}, need {K}) ? using even spacing.");
            }

            for (int i = 0; i < range.Count; i++)
                range[i] = Clamp01(range[i]);

            // =========================================================================
            // 3) Build ORDER so outputs are always start?end along curve
            // =========================================================================
            // order[k] = original index (into rawFields/range) for the k-th output step.
            // We sort by range value so k=0 is smallest t (curve start), k=K-1 is largest t (curve end).
            int[] order = Enumerable.Range(0, K)
                                    .OrderBy(i => range[i])
                                    .ToArray();

            // =========================================================================
            // 4) Read packed runtime type + constructor (Fi01 signature)
            // =========================================================================
            object f0 = rawFields[order[0]];
            Type packedType = f0.GetType();

            if (!HasMember(packedType, "G") ||
                !HasMember(packedType, "NxVerts") ||
                !HasMember(packedType, "NyVerts") ||
                !HasMember(packedType, "FrameSize") ||
                !HasMember(packedType, "CellSize") ||
                !HasMember(packedType, "IsoOffset"))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Packed field_obj is missing required members. Expected: G, NxVerts, NyVerts, FrameSize, CellSize, IsoOffset.");
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
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "Could not find expected constructor on packed field type (Fi01).\n" +
                    "Expected: (double[] g, int nxVerts, int nyVerts, Plane plane, Point2d centerXY, double frameSize, double cellSize, double isoOffset).");
                return;
            }

            // =========================================================================
            // 5) Prepare arrays for parallel build (keeps output alignment)
            // =========================================================================
            var meshArr = new Mesh[K];
            var objArr = new IGH_Goo[K];
            var frameArr = new Plane[K];

            bool useParallel = Environment.ProcessorCount > 1 && K >= 4;

            // Each output step k:
            // - frameIndex: which range value to use (ordered along curve)
            // - fieldIndex: which field to attach to that frame (optionally flipped)
            Action<int> buildStep = (k) =>
            {
                int frameIndex = order[k];                         // start?end by range
                int fieldIndex = flipEnds ? order[K - 1 - k]       // reversed assignment
                                          : order[k];

                object fk = rawFields[fieldIndex];

                // --- read packed members
                double[] G = GetDoubleArray(fk, "G");
                int nxv = GetInt(fk, "NxVerts");
                int nyv = GetInt(fk, "NyVerts");
                double fSize = GetDouble(fk, "FrameSize");
                double cell = GetDouble(fk, "CellSize");
                double iso = GetDouble(fk, "IsoOffset");

                // --- guard invalid fields
                if (G == null || G.Length == 0 || nxv < 2 || nyv < 2 ||
                    fSize <= RhinoMath.ZeroTolerance || cell <= RhinoMath.ZeroTolerance)
                    return;

                // --- build perpendicular frame at normalized position (always start?end in output order)
                double s = range[frameIndex];
                Plane Pk;
                if (!GetPerpFrameAtNormalized(axis, s, out Pk))
                    return;

                frameArr[k] = Pk;

                // --- build mesh in this frame
                var m = new Mesh();
                double half = fSize * 0.5;

                double gMin = double.PositiveInfinity;
                double gMax = double.NegativeInfinity;

                int nPts = Math.Min(G.Length, nxv * nyv);

                int idx = 0;
                for (int j = 0; j < nyv; j++)
                {
                    double y = -half + j * cell;
                    for (int i = 0; i < nxv; i++, idx++)
                    {
                        double x = -half + i * cell;

                        Point3d Pw = Pk.Origin + x * Pk.XAxis + y * Pk.YAxis;
                        m.Vertices.Add(Pw);

                        if (idx < nPts)
                        {
                            double g = G[idx];
                            if (g < gMin) gMin = g;
                            if (g > gMax) gMax = g;
                        }
                    }
                }

                // --- faces (quads)
                for (int jj = 0; jj < nyv - 1; jj++)
                {
                    for (int ii = 0; ii < nxv - 1; ii++)
                    {
                        int a = ii + jj * nxv;
                        int b = (ii + 1) + jj * nxv;
                        int c = (ii + 1) + (jj + 1) * nxv;
                        int d = ii + (jj + 1) * nxv;
                        m.Faces.AddFace(a, b, c, d);
                    }
                }

                m.Normals.ComputeNormals();

                // --- vertex colors from g (normalize around 0)
                double amp = Math.Max(Math.Abs(gMin), Math.Abs(gMax));
                if (amp < 1e-12) amp = 1.0;

                m.VertexColors.CreateMonotoneMesh(Color.White);

                idx = 0;
                for (int j = 0; j < nyv; j++)
                {
                    for (int i = 0; i < nxv; i++, idx++)
                    {
                        double g = (idx < nPts) ? G[idx] : 0.0;
                        double nrm = Math.Max(-1.0, Math.Min(1.0, g / amp));
                        m.VertexColors[idx] = DivergingBlueWhiteRed(nrm);
                    }
                }

                // --- pack oriented field: Plane = Pk, CenterXY = (0,0)
                object packedOut = ctor.Invoke(new object[]
                {
                    G, nxv, nyv,
                    Pk, new Point2d(0.0, 0.0),
                    fSize, cell,
                    iso
                });

                meshArr[k] = m;
                objArr[k] = new GH_ObjectWrapper(packedOut);
            };

#if USE_PARALLEL
            if (useParallel)
                Parallel.For(0, K, buildStep);
            else
                for (int k = 0; k < K; k++) buildStep(k);
#else
            useParallel = false;
            for (int k = 0; k < K; k++) buildStep(k);
#endif

            // =========================================================================
            // 6) Collect outputs as DataTrees (one branch per ordered step {k})
            // =========================================================================
            var meshTree = new GH_Structure<GH_Mesh>();
            var fieldTree = new GH_Structure<IGH_Goo>();
            var frameTree = new GH_Structure<GH_Plane>();

            int nullCount = 0;
            int builtCount = 0;

            for (int k = 0; k < K; k++)
            {
                if (meshArr[k] == null || objArr[k] == null || frameArr[k] == Plane.Unset)
                {
                    nullCount++;
                    continue;
                }

                var path = new GH_Path(k); // IMPORTANT: k is ordered start?end
                meshTree.Append(new GH_Mesh(meshArr[k]), path);
                fieldTree.Append(objArr[k], path);
                frameTree.Append(new GH_Plane(frameArr[k]), path);

                builtCount++;
            }

            if (nullCount > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    $"Warning: {nullCount} step(s) could not be built (invalid packed fields).");

            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                $"Built {builtCount} step(s) as DataTrees. Ordered=start?end. flip_ends={(flipEnds ? "ON" : "OFF")}. Parallel={(useParallel ? "ON" : "OFF")}.");

            DA.SetDataTree(0, meshTree);
            DA.SetDataTree(1, fieldTree);
            DA.SetDataTree(2, frameTree);
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

        private double Clamp01(double x)
        {
            if (x < 0.0) return 0.0;
            if (x > 1.0) return 1.0;
            return x;
        }

        // =========================================================================
        // Geometry helpers
        // =========================================================================
        private bool GetPerpFrameAtNormalized(Curve crv, double sNorm, out Plane frame)
        {
            frame = Plane.Unset;

            if (crv == null || !crv.IsValid) return false;

            sNorm = Math.Max(0.0, Math.Min(1.0, sNorm));

            double t;
            if (!crv.NormalizedLengthParameter(sNorm, out t))
                t = crv.Domain.ParameterAt(sNorm);

            // Primary
            if (crv.PerpendicularFrameAt(t, out frame))
            {
                frame.Origin = crv.PointAt(t);
                return true;
            }

            // Fallback
            Vector3d tan = crv.TangentAt(t);
            if (!tan.Unitize()) tan = Vector3d.XAxis;

            Vector3d up = Math.Abs(Vector3d.Multiply(Vector3d.ZAxis, tan)) > 0.95 ? Vector3d.YAxis : Vector3d.ZAxis;

            Vector3d x = tan;

            Vector3d z = Vector3d.CrossProduct(x, up);
            if (!z.Unitize()) z = Vector3d.ZAxis;

            Vector3d y = Vector3d.CrossProduct(z, x);
            if (!y.Unitize()) y = Vector3d.YAxis;

            frame = new Plane(crv.PointAt(t), x, y);
            return true;
        }

        // =========================================================================
        // Color map
        // =========================================================================
        private Color DivergingBlueWhiteRed(double t)
        {
            t = Math.Max(-1.0, Math.Min(1.0, t));

            if (t >= 0.0)
            {
                int r = 255;
                int g = (int)(255 * (1.0 - 0.5 * t));
                int b = (int)(255 * (1.0 - t));
                return Color.FromArgb(r, g, b);
            }
            else
            {
                double u = -t;
                int r = (int)(255 * (1.0 - u));
                int g = (int)(255 * (1.0 - 0.5 * u));
                int b = 255;
                return Color.FromArgb(r, g, b);
            }
        }
    }
}
