#define USE_PARALLEL

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
#endregion

namespace WASPer_3DP.Components._2_2_Fields
{
    /// <summary>
    /// wsp_Fi02_Field Booleans
    /// Boolean operations on packed distance fields (field_obj) in g-space.
    ///
    /// Assumptions:
    /// - Each input field_obj contains a packed grid with members:
    ///   G (double[]), NxVerts, NyVerts, Plane, CenterXY, FrameSize, CellSize, IsoOffset.
    /// - G stores g = f - iso_offset (signed distance shifted).
    ///
    /// Operations are done in g-space, producing a new packed field with IsoOffset = 0.0.
    /// Output mesh is built on the first valid field's Plane/CenterXY/FrameSize/CellSize.
    /// </summary>
    public class wsp_Fi02_Field_Booleans : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Fi02_Field_Booleans()
            : base(
                "wsp_Fi02_Field Booleans",
                "FieldBool",
                "Boolean operations on packed field_obj grids (g-space SDF).\n\n" +
                "boolean_op:\n" +
                "  1 = Union (min)\n" +
                "  2 = Subtraction (A - union(B...))  => max(A, -min(B...))\n" +
                "  3 = Intersection (max)\n" +
                "  4 = XOR (A ? B) => max( min(A,-B), min(B,-A) )\n" +
                "  5 = Negate (-A)\n\n" +
                "Notes:\n" +
                "- Uses normalized index sampling so fields with different resolutions can still combine.\n" +
                "- Output grid/plane come from the first valid field.\n" +
                "- Output IsoOffset is set to 0.0 (result is already in g-space).",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "2.2_Fields")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("A22D8F8E-3F79-4A7E-9F2C-7C61C7A6C6B9");

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Fi02_Field Booleans.png"))
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

            // 1) boolean_op (INT)
            pManager.AddIntegerParameter(
                "boolean_op",
                "op",
                "Boolean operation:\n" +
                "1=Union(min)\n" +
                "2=Subtraction(A-B)\n" +
                "3=Intersection(max)\n" +
                "4=XOR(A?B)\n" +
                "5=Negate(-A)",
                GH_ParamAccess.item,
                1);
        }

        // -----------------------------------------------------------------------------
        // OUTPUTS
        // -----------------------------------------------------------------------------
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            // 0) field_obj_out
            pManager.AddGenericParameter(
                "field_obj",
                "field",
                "SINGLE packed field (result).\n" +
                "Same packed type as Fi01, but IsoOffset is set to 0.0 (result already in g-space).",
                GH_ParamAccess.item);

            // 1) field_mesh
            pManager.AddMeshParameter(
                "field_mesh",
                "mesh",
                "Preview mesh of the result field (colored from result values).",
                GH_ParamAccess.item);
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
            int booleanOp = 1;

            if (!DA.GetDataList(0, fieldObjIn) || fieldObjIn == null || fieldObjIn.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "field_obj is empty.");
                return;
            }
            DA.GetData(1, ref booleanOp);

            // Clamp op
            if (booleanOp < 1 || booleanOp > 5)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "boolean_op out of range. Using 1 (Union).");
                booleanOp = 1;
            }

            // -------------------------
            // 1) Unwrap GH_ObjectWrapper
            // -------------------------
            var rawFields = new List<object>(fieldObjIn.Count);
            foreach (var o in fieldObjIn)
            {
                if (o == null) continue;

                if (o is GH_ObjectWrapper w && w.Value != null)
                    rawFields.Add(w.Value);
                else
                    rawFields.Add(o);
            }

            if (rawFields.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "field_obj contains only nulls.");
                return;
            }

            // -------------------------
            // 2) Validate packed field type & constructor
            // -------------------------
            object f0 = rawFields[0];
            Type packedType = f0.GetType();

            if (!HasMember(packedType, "G") ||
                !HasMember(packedType, "NxVerts") ||
                !HasMember(packedType, "NyVerts") ||
                !HasMember(packedType, "Plane") ||
                !HasMember(packedType, "CenterXY") ||
                !HasMember(packedType, "FrameSize") ||
                !HasMember(packedType, "CellSize") ||
                !HasMember(packedType, "IsoOffset"))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Packed field_obj missing required members. Expected: G, NxVerts, NyVerts, Plane, CenterXY, FrameSize, CellSize, IsoOffset.");
                return;
            }

            // Constructor signature from Fi01:
            // (double[] g, int nxVerts, int nyVerts, Plane plane, Point2d centerXY, double frameSize, double cellSize, double isoOffset)
            var ctor = packedType.GetConstructor(new Type[]
            {
                typeof(double[]), typeof(int), typeof(int),
                typeof(Plane), typeof(Point2d),
                typeof(double), typeof(double), typeof(double)
            });

            if (ctor == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Could not find expected constructor on packed field type (Fi01). Must match: (double[], int, int, Plane, Point2d, double, double, double).");
                return;
            }

            // -------------------------
            // 3) Read base grid (output domain) from first field
            // -------------------------
            var baseG = GetDoubleArray(f0, "G");
            int nxv0 = GetInt(f0, "NxVerts");
            int nyv0 = GetInt(f0, "NyVerts");
            Plane pl0 = GetPlane(f0, "Plane");
            Point2d c0 = GetPoint2d(f0, "CenterXY");
            double frameSize0 = GetDouble(f0, "FrameSize");
            double cell0 = GetDouble(f0, "CellSize");

            if (baseG == null || baseG.Length == 0 || nxv0 < 2 || nyv0 < 2 || !pl0.IsValid ||
                frameSize0 <= RhinoMath.ZeroTolerance || cell0 <= RhinoMath.ZeroTolerance)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "First field_obj is invalid.");
                return;
            }

            int nPts0 = nxv0 * nyv0;

            // -------------------------
            // 4) Build cache for all valid fields
            // -------------------------
            var fields = new List<FieldCache>(rawFields.Count);
            int invalidCount = 0;

            for (int k = 0; k < rawFields.Count; k++)
            {
                var fk = rawFields[k];
                if (fk == null) { invalidCount++; continue; }

                var Gk = GetDoubleArray(fk, "G");
                int nxvk = GetInt(fk, "NxVerts");
                int nyvk = GetInt(fk, "NyVerts");
                double fs = GetDouble(fk, "FrameSize");
                double cs = GetDouble(fk, "CellSize");
                Plane pk = GetPlane(fk, "Plane");
                Point2d ck = HasMember(fk.GetType(), "CenterXY") ? GetPoint2d(fk, "CenterXY") : new Point2d(0, 0);

                if (Gk == null || Gk.Length == 0 || nxvk < 2 || nyvk < 2 ||
                    fs <= RhinoMath.ZeroTolerance || cs <= RhinoMath.ZeroTolerance || !pk.IsValid)
                {
                    invalidCount++;
                    continue;
                }

                fields.Add(new FieldCache(Gk, nxvk, nyvk, fs, cs, pk, ck));
            }

            if (fields.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "All packed fields are invalid.");
                return;
            }

            if (invalidCount > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"Warning: skipped {invalidCount} invalid field(s).");

            // XOR/Subtraction need at least 2
            if ((booleanOp == 2 || booleanOp == 4) && fields.Count < 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "boolean_op requires at least 2 fields. Falling back to 1 (Union).");
                booleanOp = 1;
            }

            if (booleanOp == 4 && fields.Count > 2)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "XOR uses only the first two valid fields; extra fields are ignored.");

            // -------------------------
            // 5) Compute boolean per vertex on base grid
            // -------------------------
            var res = new double[nPts0];

            bool useParallel =
                Environment.ProcessorCount > 1 &&
                (nPts0 >= 60000 || (nPts0 >= 20000 && fields.Count >= 4));

            // Sample each field in normalized index space (u,v in [0..1])
            double Sample(FieldCache fc, double u, double v)
            {
                double x = u * (fc.Nx - 1);
                double y = v * (fc.Ny - 1);

                int i0 = (int)Math.Floor(x);
                int j0 = (int)Math.Floor(y);

                if (i0 < 0) i0 = 0;
                if (j0 < 0) j0 = 0;
                if (i0 > fc.Nx - 2) i0 = fc.Nx - 2;
                if (j0 > fc.Ny - 2) j0 = fc.Ny - 2;

                int i1 = i0 + 1;
                int j1 = j0 + 1;

                double fx = x - i0;
                double fy = y - j0;

                double v00 = fc.G[fc.Index(i0, j0)];
                double v10 = fc.G[fc.Index(i1, j0)];
                double v01 = fc.G[fc.Index(i0, j1)];
                double v11 = fc.G[fc.Index(i1, j1)];

                double a0 = v00 + (v10 - v00) * fx;
                double a1 = v01 + (v11 - v01) * fx;
                return a0 + (a1 - a0) * fy;
            }

            void SolveIdx(int idx)
            {
                int j = idx / nxv0;
                int i = idx - j * nxv0;

                double u = (nxv0 == 1) ? 0.0 : (double)i / (nxv0 - 1);
                double v = (nyv0 == 1) ? 0.0 : (double)j / (nyv0 - 1);

                double outVal;

                if (booleanOp == 5)
                {
                    // Negate: -A
                    double a = Sample(fields[0], u, v);
                    outVal = -a;
                }
                else if (booleanOp == 4)
                {
                    // XOR(A,B) = max( min(A,-B), min(B,-A) )
                    double a = Sample(fields[0], u, v);
                    double b = Sample(fields[1], u, v);
                    double t1 = Math.Min(a, -b);
                    double t2 = Math.Min(b, -a);
                    outVal = Math.Max(t1, t2);
                }
                else if (booleanOp == 2)
                {
                    // Subtraction: A - union(B...) => max(A, -min(B...))
                    double a = Sample(fields[0], u, v);

                    double bMin = double.PositiveInfinity;
                    for (int k = 1; k < fields.Count; k++)
                    {
                        double b = Sample(fields[k], u, v);
                        if (b < bMin) bMin = b;
                    }
                    if (double.IsInfinity(bMin)) bMin = 1e9;

                    outVal = Math.Max(a, -bMin);
                }
                else if (booleanOp == 3)
                {
                    // Intersection = max
                    double m = double.NegativeInfinity;
                    for (int k = 0; k < fields.Count; k++)
                    {
                        double a = Sample(fields[k], u, v);
                        if (a > m) m = a;
                    }
                    outVal = m;
                }
                else
                {
                    // Union = min
                    double m = double.PositiveInfinity;
                    for (int k = 0; k < fields.Count; k++)
                    {
                        double a = Sample(fields[k], u, v);
                        if (a < m) m = a;
                    }
                    outVal = m;
                }

                res[idx] = outVal;
            }

#if USE_PARALLEL
            if (useParallel)
                Parallel.For(0, nPts0, SolveIdx);
            else
                for (int idx = 0; idx < nPts0; idx++) SolveIdx(idx);
#else
			for (int idx = 0; idx < nPts0; idx++) SolveIdx(idx);
			useParallel = false;
#endif

            // -------------------------
            // 6) Build preview mesh on base plane
            // -------------------------
            var mesh = new Mesh();
            double half = frameSize0 * 0.5;

            double vMin = double.PositiveInfinity;
            double vMax = double.NegativeInfinity;

            for (int j = 0; j < nyv0; j++)
            {
                double y = c0.Y - half + j * cell0;

                for (int i = 0; i < nxv0; i++)
                {
                    double x = c0.X - half + i * cell0;
                    Point3d Pw = pl0.Origin + x * pl0.XAxis + y * pl0.YAxis;
                    mesh.Vertices.Add(Pw);

                    double vv = res[i + j * nxv0];
                    if (vv < vMin) vMin = vv;
                    if (vv > vMax) vMax = vv;
                }
            }

            for (int j = 0; j < nyv0 - 1; j++)
            {
                for (int i = 0; i < nxv0 - 1; i++)
                {
                    int a = i + j * nxv0;
                    int b = (i + 1) + j * nxv0;
                    int c = (i + 1) + (j + 1) * nxv0;
                    int d = i + (j + 1) * nxv0;
                    mesh.Faces.AddFace(a, b, c, d);
                }
            }

            mesh.Normals.ComputeNormals();

            // -------------------------
            // 7) Colorize mesh from res
            // -------------------------
            mesh.VertexColors.CreateMonotoneMesh(Color.White);

            double amp = Math.Max(Math.Abs(vMin), Math.Abs(vMax));
            if (amp < 1e-12) amp = 1.0;

            for (int idx = 0; idx < nPts0; idx++)
            {
                double nrm = Math.Max(-1.0, Math.Min(1.0, res[idx] / amp));
                mesh.VertexColors[idx] = DivergingBlueWhiteRed(nrm);
            }

            // -------------------------
            // 8) Metadata (optional)
            // -------------------------
            try
            {
                mesh.SetUserString("grid_rows", nyv0.ToString());
                mesh.SetUserString("grid_cols", nxv0.ToString());
                mesh.SetUserString("frame_size", frameSize0.ToString(CultureInfo.InvariantCulture));
                mesh.SetUserString("resolution", cell0.ToString(CultureInfo.InvariantCulture));
                mesh.SetUserString("plane_origin", $"{pl0.OriginX},{pl0.OriginY},{pl0.OriginZ}");
                mesh.SetUserString("plane_normal", $"{pl0.ZAxis.X},{pl0.ZAxis.Y},{pl0.ZAxis.Z}");

                var sb = new StringBuilder(nPts0 * 8);
                for (int idx = 0; idx < nPts0; idx++)
                {
                    if (idx != 0) sb.Append(',');
                    sb.Append(res[idx].ToString("R", CultureInfo.InvariantCulture));
                }
                mesh.SetUserString("values_flat", sb.ToString());
            }
            catch { /* non-fatal */ }

            // -------------------------
            // 9) Pack output field_obj (result)
            // -------------------------
            // IMPORTANT:
            // This is already g-space. IsoOffset is not meaningful after boolean ops, so set to 0.0.
            object packedOut = ctor.Invoke(new object[]
            {
                res, nxv0, nyv0,
                pl0, c0,
                frameSize0, cell0,
                0.0
            });

            // Wrap for GH safety
            var wrapped = new GH_ObjectWrapper(packedOut);

            // -------------------------
            // 10) Set outputs
            // -------------------------
            DA.SetData(0, wrapped);
            DA.SetData(1, mesh);

            string opName =
                booleanOp == 1 ? "Union(min)" :
                booleanOp == 2 ? "Subtraction(A-B)" :
                booleanOp == 3 ? "Intersection(max)" :
                booleanOp == 4 ? "XOR(A?B)" :
                "Negate(-A)";

            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                $"Field boolean: {opName}. Fields used={fields.Count}. Grid={nyv0}×{nxv0}. Parallel={(useParallel ? "ON" : "OFF")}.");
        }

        // =========================================================================
        // Field Cache (lightweight)
        // =========================================================================
        private class FieldCache
        {
            public readonly double[] G;
            public readonly int Nx;
            public readonly int Ny;
            public readonly double FrameSize;
            public readonly double CellSize;
            public readonly Plane Plane;
            public readonly Point2d CenterXY;

            public FieldCache(double[] g, int nx, int ny, double frameSize, double cellSize, Plane plane, Point2d centerXY)
            {
                G = g;
                Nx = nx;
                Ny = ny;
                FrameSize = frameSize;
                CellSize = cellSize;
                Plane = plane;
                CenterXY = centerXY;
            }

            public int Index(int i, int j)
            {
                return i + j * Nx;
            }
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

        // =========================================================================
        // Colormap
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
