#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
#endregion

namespace WASPer_3DP.Components._2_2_Fields
{
    /// <summary>
    /// wsp_Fi07_Deconstruct Field
    /// Unpacks a packed field_obj (Fi01) into its raw members so you can debug.
    /// </summary>
    public class wsp_Fi07_Deconstruct_Field : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Fi07_Deconstruct_Field()
          : base(
              "wsp_Fi07_Deconstruct Field",
              "DeField",
              "Deconstructs a packed field_obj into its parts (G, dims, plane, sizes, isoOffset) + debug info.\n" +
              "Useful to verify ranges (min/max) and whether 0-crossing exists for contouring.",
              global::WASPer_3DP.WASPerPalette.DesignFabrication,
              "2.2_Fields")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("C3E6B4A1-0BB3-4D6A-A4F9-0B8A6E1B5D11");
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Fi07_Deconstruct Field.png"))
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
        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "field_obj",
                "field",
                "Packed field object (Generic / GH_ObjectWrapper) from Fi01/Fi02/Fi03/Fi04.\n" +
                "Expected members: G, NxVerts, NyVerts, Plane, CenterXY, FrameSize, CellSize, IsoOffset.",
                GH_ParamAccess.item);
        }

        // ---------------------------------------------------------------------
        // OUTPUTS
        // ---------------------------------------------------------------------
        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddNumberParameter("g_values", "G", "Flattened g-values array (length Nx*Ny).", GH_ParamAccess.list);
            p.AddIntegerParameter("nx_verts", "nx", "Number of grid vertices in X.", GH_ParamAccess.item);
            p.AddIntegerParameter("ny_verts", "ny", "Number of grid vertices in Y.", GH_ParamAccess.item);

            p.AddPlaneParameter("plane", "pl", "Grid plane (world placement).", GH_ParamAccess.item);
            p.AddPointParameter("center_xy", "cxy", "Center of the grid in plane XY coordinates (Point2d).", GH_ParamAccess.item);

            p.AddNumberParameter("frame_size", "fs", "Frame size (square side length) in model units.", GH_ParamAccess.item);
            p.AddNumberParameter("cell_size", "cs", "Cell size (grid spacing) in model units.", GH_ParamAccess.item);
            p.AddNumberParameter("iso_offset", "iso", "IsoOffset stored in the packed field.", GH_ParamAccess.item);

            p.AddNumberParameter("g_min", "min", "Minimum g value.", GH_ParamAccess.item);
            p.AddNumberParameter("g_max", "max", "Maximum g value.", GH_ParamAccess.item);
            p.AddBooleanParameter("has_zero_crossing", "0x", "True if g crosses 0 (needed for offset=0 contours).", GH_ParamAccess.item);

            p.AddMeshParameter("field_mesh", "mesh", "Preview mesh of the field (colored by g).", GH_ParamAccess.item);
            p.AddPointParameter("grid_pts", "pts", "Grid points (world). WARNING: can be heavy.", GH_ParamAccess.list);

            // Make heavy output optional by default (user can right-click output and disable preview if needed)
        }

        // ---------------------------------------------------------------------
        // SOLVE
        // ---------------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            object input = null;
            if (!DA.GetData(0, ref input) || input == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "field_obj is null.");
                return;
            }

            // Unwrap
            object f = input is GH_ObjectWrapper w && w.Value != null ? w.Value : input;
            if (f == null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "field_obj unwrap failed.");
                return;
            }

            Type t = f.GetType();

            // Required members
            if (!HasMember(t, "G") || !HasMember(t, "NxVerts") || !HasMember(t, "NyVerts") ||
                !HasMember(t, "Plane") || !HasMember(t, "FrameSize") || !HasMember(t, "CellSize"))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Not a valid packed field. Missing required members.\n" +
                    "Expected at least: G, NxVerts, NyVerts, Plane, FrameSize, CellSize (CenterXY and IsoOffset are optional but recommended).");
                return;
            }

            double[] G = GetDoubleArray(f, "G");
            int nxv = GetInt(f, "NxVerts");
            int nyv = GetInt(f, "NyVerts");

            Plane pl = GetPlane(f, "Plane");
            double frameSize = GetDouble(f, "FrameSize");
            double cellSize = GetDouble(f, "CellSize");

            Point2d cxy = HasMember(t, "CenterXY") ? GetPoint2d(f, "CenterXY") : new Point2d(0, 0);
            double iso = HasMember(t, "IsoOffset") ? GetDouble(f, "IsoOffset") : 0.0;

            if (G == null || G.Length == 0 || nxv < 2 || nyv < 2 || !pl.IsValid ||
                frameSize <= RhinoMath.ZeroTolerance || cellSize <= RhinoMath.ZeroTolerance)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Packed field has invalid data (empty G / bad dims / invalid plane / size=0).");
                return;
            }

            int expected = nxv * nyv;
            int nPts = Math.Min(G.Length, expected);
            if (G.Length != expected)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"Warning: G.Length={G.Length} but Nx*Ny={expected}. Using n={nPts}.");

            // Stats
            double gMin = double.PositiveInfinity;
            double gMax = double.NegativeInfinity;
            for (int i = 0; i < nPts; i++)
            {
                double v = G[i];
                if (v < gMin) gMin = v;
                if (v > gMax) gMax = v;
            }

            bool hasZeroCross = (gMin <= 0.0 && gMax >= 0.0);

            // Build preview mesh (same layout as Fi01 style)
            Mesh mesh = BuildFieldMesh(pl, cxy, frameSize, cellSize, nxv, nyv, G, nPts, gMin, gMax);

            // Build grid pts (optional heavy)
            var pts = new List<Point3d>(expected);
            BuildGridPts(pl, cxy, frameSize, cellSize, nxv, nyv, pts);

            // Outputs
            DA.SetDataList(0, G.Take(nPts));
            DA.SetData(1, nxv);
            DA.SetData(2, nyv);
            DA.SetData(3, pl);
            DA.SetData(4, new Point3d(cxy.X, cxy.Y, 0.0)); // GH has no Point2d param; send as Point3d(Z=0)
            DA.SetData(5, frameSize);
            DA.SetData(6, cellSize);
            DA.SetData(7, iso);
            DA.SetData(8, gMin);
            DA.SetData(9, gMax);
            DA.SetData(10, hasZeroCross);
            DA.SetData(11, mesh);
            DA.SetDataList(12, pts);

            if (!hasZeroCross)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "No 0-crossing (g_min>0 or g_max<0). If you contour at offset=0, Fi05 will output nothing.");
        }

        // ---------------------------------------------------------------------
        // Mesh/Point builders
        // ---------------------------------------------------------------------
        private static Mesh BuildFieldMesh(
            Plane pl, Point2d cxy, double frameSize, double cell, int nxv, int nyv,
            double[] G, int nPts, double gMin, double gMax)
        {
            var m = new Mesh();
            double half = frameSize * 0.5;

            // verts
            int idx = 0;
            for (int j = 0; j < nyv; j++)
            {
                double y = cxy.Y - half + j * cell;
                for (int i = 0; i < nxv; i++, idx++)
                {
                    double x = cxy.X - half + i * cell;
                    Point3d Pw = pl.Origin + x * pl.XAxis + y * pl.YAxis;
                    m.Vertices.Add(Pw);
                }
            }

            // faces
            for (int j = 0; j < nyv - 1; j++)
                for (int i = 0; i < nxv - 1; i++)
                {
                    int a = i + j * nxv;
                    int b = (i + 1) + j * nxv;
                    int c = (i + 1) + (j + 1) * nxv;
                    int d = i + (j + 1) * nxv;
                    m.Faces.AddFace(a, b, c, d);
                }

            m.Normals.ComputeNormals();

            // colors (diverging around 0)
            m.VertexColors.CreateMonotoneMesh(Color.White);
            double amp = Math.Max(Math.Abs(gMin), Math.Abs(gMax));
            if (amp < 1e-12) amp = 1.0;

            int count = m.Vertices.Count;
            for (int k = 0; k < count; k++)
            {
                double g = (k < nPts) ? G[k] : 0.0;
                double nrm = Math.Max(-1.0, Math.Min(1.0, g / amp));
                m.VertexColors[k] = DivergingBlueWhiteRed(nrm);
            }

            return m;
        }

        private static void BuildGridPts(
            Plane pl, Point2d cxy, double frameSize, double cell, int nxv, int nyv, List<Point3d> pts)
        {
            double half = frameSize * 0.5;
            for (int j = 0; j < nyv; j++)
            {
                double y = cxy.Y - half + j * cell;
                for (int i = 0; i < nxv; i++)
                {
                    double x = cxy.X - half + i * cell;
                    pts.Add(pl.Origin + x * pl.XAxis + y * pl.YAxis);
                }
            }
        }

        private static Color DivergingBlueWhiteRed(double t)
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

        // ---------------------------------------------------------------------
        // Reflection helpers
        // ---------------------------------------------------------------------
        private static bool HasMember(Type t, string name)
        {
            return (t.GetField(name) != null) || (t.GetProperty(name) != null);
        }

        private static double[] GetDoubleArray(object o, string name)
        {
            var t = o.GetType();
            var f = t.GetField(name);
            if (f != null) return f.GetValue(o) as double[];
            var p = t.GetProperty(name);
            if (p != null) return p.GetValue(o, null) as double[];
            return null;
        }

        private static int GetInt(object o, string name)
        {
            var t = o.GetType();
            var f = t.GetField(name);
            if (f != null) return Convert.ToInt32(f.GetValue(o));
            var p = t.GetProperty(name);
            if (p != null) return Convert.ToInt32(p.GetValue(o, null));
            return 0;
        }

        private static double GetDouble(object o, string name)
        {
            var t = o.GetType();
            var f = t.GetField(name);
            if (f != null) return Convert.ToDouble(f.GetValue(o));
            var p = t.GetProperty(name);
            if (p != null) return Convert.ToDouble(p.GetValue(o, null));
            return 0.0;
        }

        private static Plane GetPlane(object o, string name)
        {
            var t = o.GetType();
            var f = t.GetField(name);
            if (f != null) return (Plane)f.GetValue(o);
            var p = t.GetProperty(name);
            if (p != null) return (Plane)p.GetValue(o, null);
            return Plane.Unset;
        }

        private static Point2d GetPoint2d(object o, string name)
        {
            var t = o.GetType();
            var f = t.GetField(name);
            if (f != null) return (Point2d)f.GetValue(o);
            var p = t.GetProperty(name);
            if (p != null) return (Point2d)p.GetValue(o, null);
            return new Point2d(0, 0);
        }
    }
}
