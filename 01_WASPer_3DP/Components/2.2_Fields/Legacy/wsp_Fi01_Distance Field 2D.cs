#define USE_PARALLEL

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading.Tasks;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
#endregion

namespace WASPer_3DP.Components._2_2_Fields
{
    /// <summary>
    /// wsp_Fi01_Distance Field 2D
    /// Builds a square distance field from input curves on a working plane.
    /// Closed curves contribute signed regions; open curves contribute unsigned curve/tube distances.
    /// Outputs:
    /// - A colored preview mesh (vertex colors from g = f - iso_offset).
    /// - A packed field object (stores g-grid + metadata for blending / framing / contours / booleans).
    /// </summary>
    public class wsp_Fi01_Distance_Field_2D : GH_Component
    {
        private readonly string _versionTag;

        // Protected solve snapshot used by compatible enhanced Fi01 variants.
        // The original component behavior and outputs remain unchanged.
        protected double[] LastFieldValues = Array.Empty<double>();
        protected Mesh LastFieldMesh;
        protected int LastNxVerts;
        protected int LastNyVerts;
        protected Plane LastFieldPlane = Plane.Unset;
        protected Point2d LastCenterXY;
        protected double LastFrameSize;
        protected double LastCellSize;
        protected double LastIsoOffset;

        public wsp_Fi01_Distance_Field_2D()
            : base(
                "wsp_Fi01_Distance Field 2D",
                "2D_DistField",
                "Builds a square distance field from input curves.\n\n" +
                "Workflow:\n" +
                "1) Determine a working plane (ref_plane if valid, else infer from curves).\n" +
                "2) Project curves to the plane and to WorldXY for fast 2D distance tests.\n" +
                "3) Create a square sampling window (frame_size auto if <= 0).\n" +
                "4) Sample distance f at each grid vertex, compute g = f - iso_offset.\n" +
                "5) Build a mesh on the working plane and colorize using normalized g.\n" +
                "6) Pack the g-grid + metadata into field_obj for downstream components.\n\n" +
                "Notes:\n" +
                "- Closed planar curves generate signed fields: inside is negative, outside is positive.\n" +
                "- Open curves and lines generate unsigned distance-to-curve fields, useful as tube/band fields.\n" +
                "- iso_offset shifts the zero-contour target (g=0 corresponds to f=iso_offset).\n" +
                "- Auto-parallel sampling is enabled only when it’s likely worth it.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "2.2_Fields")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("C8B3E5B4-6A1A-4C2A-9F06-7C9B5E12A7D1");

        // Legacy compatibility component: retain the GUID so existing GH files
        // deserialize, but keep it out of the component ribbon and search menu.
        public override GH_Exposure Exposure => GH_Exposure.hidden;

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Fi01_Distance Field 2D.png"))
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
            // 0) curves (LIST)
            pManager.AddCurveParameter(
                "curves",
                "curves",
                "Input curves used to generate the distance field.\n" +
                "Closed curves define inside/outside via an even-odd rule (sign).\n" +
                "Open curves/lines generate unsigned distance-to-curve fields.",
                GH_ParamAccess.list);

            // 1) ref_plane (OPTIONAL)
            pManager.AddPlaneParameter(
                "ref_plane",
                "ref_pl",
                "Optional reference plane.\n" +
                "If valid, it defines orientation + center. If unset/invalid, plane is inferred from curves.",
                GH_ParamAccess.item);
            pManager[1].Optional = true;

            // 2) iso_offset
            pManager.AddNumberParameter(
                "iso_offset",
                "offset",
                "Distance offset (metric).\n" +
                "Field values are computed as: g = f - iso_offset.\n" +
                "Contour at g=0 corresponds to f=iso_offset.",
                GH_ParamAccess.item,
                0.0);

            // 3) resolution
            pManager.AddNumberParameter(
                "resolution",
                "res",
                "Grid cell size (units). Example: 5.0\n" +
                "If <= 0, default is used (5.0).",
                GH_ParamAccess.item,
                5.0);

            // 4) frame_size
            pManager.AddNumberParameter(
                "frame_size",
                "f_size",
                "Square sampling window side length (units).\n" +
                "If <= 0, it is computed from curve bbox + margins.",
                GH_ParamAccess.item,
                0.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            // 0) field_mesh
            pManager.AddMeshParameter(
                "field_mesh",
                "mesh",
                "Preview mesh of the field on the working plane.\n" +
                "Vertex colors represent normalized g in [-1,1] (blue?white?red).",
                GH_ParamAccess.item);

            // 1) field_obj
            pManager.AddGenericParameter(
                "field_obj",
                "field",
                "Packed field object (single).\n" +
                "Stores: g-grid, dimensions, plane, center, frame_size, cell_size, iso_offset.\n" +
                "Use this for blending, framing along curves, contours, booleans, etc.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            SolveDistanceField(DA, 0, false);
        }

        protected bool SolveDistanceField(
            IGH_DataAccess DA,
            int inputOffset,
            bool defaultToWorldOrigin,
            bool writeOutputs = true)
        {
            // -------------------------
            // 0) Read inputs
            // -------------------------
            var curves = new List<Curve>();
            Plane refPlane = Plane.Unset;
            double isoOffset = 0.0;
            double resolution = 5.0;
            double frameSize = 0.0;

            if (!DA.GetDataList(inputOffset, curves) || curves == null || curves.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No curves.");
                return false;
            }

            DA.GetData(inputOffset + 1, ref refPlane); // optional
            if (defaultToWorldOrigin &&
                (!refPlane.IsValid || refPlane == Plane.Unset))
                refPlane = Plane.WorldXY;
            DA.GetData(inputOffset + 2, ref isoOffset);
            DA.GetData(inputOffset + 3, ref resolution);
            DA.GetData(inputOffset + 4, ref frameSize);

            if (resolution <= 0.0) resolution = 5.0;

            double tol = RhinoDoc.ActiveDoc != null ? RhinoDoc.ActiveDoc.ModelAbsoluteTolerance : 1e-6;

            // -------------------------
            // 1) Determine working plane
            // -------------------------
            Plane pl;
            bool useRef = (refPlane.IsValid && refPlane != Plane.Unset);

            if (useRef)
            {
                pl = refPlane;

                // sanity check vs curve plane (non-fatal)
                if (TryGetCommonPlane(curves, out Plane cp))
                {
                    double dot = Math.Abs(cp.Normal * pl.Normal);
                    if (dot < 0.8)
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, "Warning: ref_plane normal differs significantly from curves' plane.");
                }
            }
            else
            {
                if (!TryGetCommonPlane(curves, out pl))
                {
                    var pts = SampleCurves(curves, 200);
                    if (Plane.FitPlaneToPoints(pts, out pl) != PlaneFitResult.Success)
                    {
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Could not determine a plane for the curves.");
                        return false;
                    }
                }
            }

            Transform worldToXY = Transform.PlaneToPlane(pl, Plane.WorldXY);
            Transform xyToWorld = Transform.PlaneToPlane(Plane.WorldXY, pl);

            // -------------------------
            // 2) Project curves to plane, then to WorldXY
            // -------------------------
            var crv2D = new List<Curve>();
            for (int i = 0; i < curves.Count; i++)
            {
                var c = curves[i];
                if (c == null) continue;

                Curve d = Curve.ProjectToPlane(c, pl);
                if (d == null) continue;

                d.Transform(worldToXY);
                crv2D.Add(d);
            }

            if (crv2D.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "All curves invalid after projection.");
                return false;
            }

            int closedCurveCount = 0;
            int openCurveCount = 0;
            foreach (var c in crv2D)
            {
                if (c != null && c.IsClosed) closedCurveCount++;
                else openCurveCount++;
            }

            if (openCurveCount > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{openCurveCount} open curve(s) will be treated as unsigned distance-to-curve fields.");
            }

            // -------------------------
            // 3) Define square sampling window (center + size)
            // -------------------------
            BoundingBox bb = BoundingBox.Empty;
            foreach (var c in crv2D)
                bb.Union(c.GetBoundingBox(true));

            Point2d centerXY = useRef
                ? new Point2d(0.0, 0.0)
                : new Point2d((bb.Min.X + bb.Max.X) * 0.5, (bb.Min.Y + bb.Max.Y) * 0.5);

            if (frameSize <= RhinoMath.ZeroTolerance)
            {
                double extX = bb.Max.X - bb.Min.X;
                double extY = bb.Max.Y - bb.Min.Y;

                // margin includes isoOffset magnitude + a couple of cells
                double margin = Math.Max(2.0 * resolution, Math.Abs(isoOffset) + 2.0 * resolution);

                frameSize = Math.Max(extX, extY) + 2.0 * margin;
                if (frameSize <= 0.0) frameSize = 100.0;

                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"frame_size auto: {frameSize:F2}");
            }

            int n = Math.Max(2, (int)Math.Ceiling(frameSize / resolution)); // quads per side
            double half = frameSize * 0.5;
            double dxy = frameSize / n;                                     // dx = dy
            int nx = n, ny = n;                                             // quads
            int nxv = nx + 1, nyv = ny + 1;                                 // vertices
            int nPts = nxv * nyv;

            // -------------------------
            // 4) Sample field f and compute g = f - isoOffset
            //    (auto-parallel)
            // -------------------------
            double minG = double.PositiveInfinity;
            double maxG = double.NegativeInfinity;

            var fFlat = new double[nPts];
            var gFlat = new double[nPts];
            var pwFlat = new Point3d[nPts];

            bool useParallel =
                Environment.ProcessorCount > 1 &&
                (nPts >= 40000 || (nPts >= 12000 && crv2D.Count >= 25));

            object minMaxLock = new object();

            if (useParallel)
            {
#if USE_PARALLEL
                Parallel.For(0, nPts,
                    () => new double[] { double.PositiveInfinity, double.NegativeInfinity }, // localMin, localMax
                    (idx, state, local) =>
                    {
                        int j = idx / nxv;
                        int i = idx - j * nxv;

                        double x = centerXY.X - half + i * dxy;
                        double y = centerXY.Y - half + j * dxy;
                        Point3d Pxy = new Point3d(x, y, 0);

                        double f = DistanceFieldToCurvesXY(Pxy, crv2D, tol);
                        double g = f - isoOffset;

                        fFlat[idx] = f;
                        gFlat[idx] = g;

                        Point3d Pw = Pxy;
                        Pw.Transform(xyToWorld);
                        pwFlat[idx] = Pw;

                        if (g < local[0]) local[0] = g;
                        if (g > local[1]) local[1] = g;

                        return local;
                    },
                    local =>
                    {
                        lock (minMaxLock)
                        {
                            if (local[0] < minG) minG = local[0];
                            if (local[1] > maxG) maxG = local[1];
                        }
                    });
#else
				useParallel = false;
#endif
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, $"Auto-parallel sampling ON (pts={nPts}, curves={crv2D.Count}, cores={Environment.ProcessorCount})");
            }

            if (!useParallel)
            {
                for (int j = 0; j <= ny; j++)
                {
                    double y = centerXY.Y - half + j * dxy;

                    for (int i = 0; i <= nx; i++)
                    {
                        int idx = i + j * nxv;
                        double x = centerXY.X - half + i * dxy;

                        Point3d Pxy = new Point3d(x, y, 0);

                        double f = DistanceFieldToCurvesXY(Pxy, crv2D, tol);
                        double g = f - isoOffset;

                        fFlat[idx] = f;
                        gFlat[idx] = g;

                        if (g < minG) minG = g;
                        if (g > maxG) maxG = g;

                        Point3d Pw = Pxy;
                        Pw.Transform(xyToWorld);
                        pwFlat[idx] = Pw;
                    }
                }
            }

            // -------------------------
            // 5) Build mesh vertices + faces
            // -------------------------
            var outMesh = new Mesh();

            for (int j = 0; j <= ny; j++)
            {
                for (int i = 0; i <= nx; i++)
                {
                    int idx = i + j * nxv;
                    outMesh.Vertices.Add(pwFlat[idx]);
                }
            }

            for (int j = 0; j < ny; j++)
            {
                for (int i = 0; i < nx; i++)
                {
                    int a = i + j * (nx + 1);
                    int b = (i + 1) + j * (nx + 1);
                    int c = (i + 1) + (j + 1) * (nx + 1);
                    int d = i + (j + 1) * (nx + 1);
                    outMesh.Faces.AddFace(a, b, c, d);
                }
            }

            outMesh.Normals.ComputeNormals();

            // -------------------------
            // 6) Colorize preview from g (normalized to [-1,1])
            // -------------------------
            outMesh.VertexColors.CreateMonotoneMesh(Color.White);

            double amp = Math.Max(Math.Abs(minG), Math.Abs(maxG));
            if (amp < 1e-12) amp = 1.0;

            for (int v = 0; v < outMesh.Vertices.Count; v++)
            {
                double nrm = Math.Max(-1.0, Math.Min(1.0, gFlat[v] / amp));
                outMesh.VertexColors[v] = DivergingBlueWhiteRed(nrm);
            }

            // -------------------------
            // 7) Optional metadata stored on mesh (non-fatal)
            // -------------------------
            try
            {
                outMesh.SetUserString("grid_vertices_side", (nx + 1).ToString());
                outMesh.SetUserString("grid_quads_side", nx.ToString());
                outMesh.SetUserString("frame_size", frameSize.ToString(CultureInfo.InvariantCulture));
                outMesh.SetUserString("cell_size", dxy.ToString(CultureInfo.InvariantCulture));
                outMesh.SetUserString("plane_origin", $"{pl.OriginX},{pl.OriginY},{pl.OriginZ}");
                outMesh.SetUserString("plane_normal", $"{pl.ZAxis.X},{pl.ZAxis.Y},{pl.ZAxis.Z}");
            }
            catch { /* non-fatal */ }

            // -------------------------
            // 8) Pack field_obj (g-grid + metadata)
            // -------------------------
            var fieldPack = new WspFieldGrid2D(
                gFlat,
                nxv,
                nyv,
                pl,
                centerXY,
                frameSize,
                dxy,
                isoOffset);

            LastFieldValues = (double[])gFlat.Clone();
            LastFieldMesh = outMesh.DuplicateMesh();
            LastNxVerts = nxv;
            LastNyVerts = nyv;
            LastFieldPlane = pl;
            LastCenterXY = centerXY;
            LastFrameSize = frameSize;
            LastCellSize = dxy;
            LastIsoOffset = isoOffset;

            // wrap for GH "Generic" safety
            var wrappedField = new GH_ObjectWrapper(fieldPack);

            // -------------------------
            // 9) Set outputs (ONLY these two)
            // -------------------------
            if (writeOutputs)
            {
                DA.SetData(0, outMesh);
                DA.SetData(1, wrappedField);
            }

            string where = useRef ? "ref_plane origin" : "geometry center";
            AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                $"Field: {nxv}×{nyv} verts, size={frameSize:F2}, cell={dxy:F2}, closed={closedCurveCount}, open={openCurveCount}, centered at {where} on plane [{pl.OriginX:F1},{pl.OriginY:F1},{pl.OriginZ:F1}]");
            return true;
        }

        // =====================================================================
        // Packed Field Object
        // =====================================================================
        /// <summary>
        /// Lightweight container to carry the offset-distance grid (g) as a single object.
        /// g = f - iso_offset, row-major: idx = i + j*NxVerts
        /// </summary>
        public class WspFieldGrid2D
        {
            public readonly double[] G;     // g = f - iso_offset (row-major)
            public readonly int NxVerts;    // (nx+1)
            public readonly int NyVerts;    // (ny+1)
            public readonly Plane Plane;    // field plane in world
            public readonly Point2d CenterXY;   // center in field-XY coords
            public readonly double FrameSize;   // side length
            public readonly double CellSize;    // dxy
            public readonly double IsoOffset;   // stored iso_offset (traceability)

            public WspFieldGrid2D(
                double[] g,
                int nxVerts,
                int nyVerts,
                Plane plane,
                Point2d centerXY,
                double frameSize,
                double cellSize,
                double isoOffset)
            {
                G = g;
                NxVerts = nxVerts;
                NyVerts = nyVerts;
                Plane = plane;
                CenterXY = centerXY;
                FrameSize = frameSize;
                CellSize = cellSize;
                IsoOffset = isoOffset;
            }

            public int Index(int i, int j)
            {
                return i + j * NxVerts;
            }

            public double GAt(int i, int j)
            {
                return G[Index(i, j)];
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        /// <summary>
        /// Try to find a single plane shared by all curves (fast check).
        /// </summary>
        private bool TryGetCommonPlane(List<Curve> crvs, out Plane plane)
        {
            plane = Plane.Unset;

            for (int i = 0; i < crvs.Count; i++)
            {
                var c = crvs[i];
                if (c == null) continue;

                if (!c.TryGetPlane(out Plane p)) continue;

                bool ok = true;

                for (int j = 0; j < crvs.Count; j++)
                {
                    if (i == j) continue;
                    var cj = crvs[j];
                    if (cj == null) continue;

                    if (!cj.TryGetPlane(out Plane pj)) { ok = false; break; }
                    if (Math.Abs(pj.Normal * p.Normal) < 0.999) { ok = false; break; }
                }

                if (ok)
                {
                    plane = p;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Normalized arc-length sampling (used for plane fitting fallback).
        /// </summary>
        private List<Point3d> SampleCurves(List<Curve> crvs, int totalTarget)
        {
            var pts = new List<Point3d>(totalTarget);
            int per = Math.Max(3, totalTarget / Math.Max(1, crvs.Count));

            foreach (var c in crvs)
            {
                if (c == null || !c.IsValid) continue;

                for (int i = 0; i < per; i++)
                {
                    double s = (per == 1) ? 0.0 : (double)i / (per - 1);

                    if (!c.NormalizedLengthParameter(s, out double t))
                        t = c.Domain.ParameterAt(s);

                    pts.Add(c.PointAt(t));
                }
            }

            return pts;
        }

        /// <summary>
        /// Distance field to a set of 2D curves in WorldXY.
        /// Closed curves contribute signed distance using an even-odd sign rule.
        /// Open curves contribute unsigned nearest-distance fields.
        /// The final field is the minimum contribution, so open curves act like
        /// tube/band fields and closed curves act like profile regions.
        /// </summary>
        private double DistanceFieldToCurvesXY(Point3d Pxy, List<Curve> crv2D, double tol)
        {
            double openUnsignedMin = double.PositiveInfinity;
            double closedBoundaryMin = double.PositiveInfinity;
            bool insideClosed = false;

            foreach (var c in crv2D)
            {
                if (!c.ClosestPoint(Pxy, out double t)) continue;

                double dist = Pxy.DistanceTo(c.PointAt(t));
                if (c.IsClosed)
                {
                    if (dist < closedBoundaryMin) closedBoundaryMin = dist;

                    var pc = c.Contains(Pxy, Plane.WorldXY, tol);
                    if (pc == PointContainment.Inside || pc == PointContainment.Coincident)
                        insideClosed = !insideClosed;
                }
                else
                {
                    if (dist < openUnsignedMin) openUnsignedMin = dist;
                }
            }

            double result = double.PositiveInfinity;

            if (!double.IsInfinity(closedBoundaryMin))
            {
                double signedClosed = insideClosed ? -closedBoundaryMin : closedBoundaryMin;
                result = Math.Min(result, signedClosed);
            }

            if (!double.IsInfinity(openUnsignedMin))
            {
                result = Math.Min(result, openUnsignedMin);
            }

            return double.IsInfinity(result) ? 1e9 : result;
        }

        /// <summary>
        /// Diverging colormap: blue (-1) ? white (0) ? red (+1)
        /// </summary>
        protected static Color DivergingBlueWhiteRed(double t)
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
