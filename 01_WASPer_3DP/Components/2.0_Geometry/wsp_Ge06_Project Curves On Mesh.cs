#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;

using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;

using Grasshopper.Kernel;
#endregion

namespace WASPer_3DP.Components._3_Geometry
{
    // =========================================================================
    //  wsp_Ge06_Project Curves On Geometry
    //  - Projects curves onto Mesh/Brep/Surface/Extrusion geometry using:
    //      (1) Directional projection if a vector is provided
    //      (2) Fallback: closest-point pull approximation
    //  Output:
    //      proj_crvs : projected curves (can output multiple per input curve)
    //  Version tag (yyMMdd): 250918
    // =========================================================================
    public class wsp_Ge06_Project_Curves_On_Mesh : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Ge06_Project_Curves_On_Mesh()
          : base(
              "wsp_Ge06_Project Curves On Geometry",
              "Project/Pull?Geo",
              "Projects curves onto Mesh, Brep, Surface, or Extrusion geometry. If a direction vector is provided, uses directional projection; otherwise falls back to closest-point pull.",
              global::WASPer_3DP.WASPerPalette.DesignFabrication,
              "2.0_Geometry")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v   = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        // ---------------------------------------------------------------------
        // GUID (IMPORTANT): generate your own (Tools > Create GUID in VS)
        // ---------------------------------------------------------------------
        public override Guid ComponentGuid => new Guid("A1E4B04E-6D4E-4F7E-8D9D-4F0E9C0E3C77");

        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        // Optional icon (set later if you want)
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ge06_Project Curves On Mesh.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        // ---------------------------------------------------------------------
        // Inputs
        // ---------------------------------------------------------------------
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "curves",
                "crvs",
                "Curves to project.",
                GH_ParamAccess.list);

            pManager.AddGeometryParameter(
                "geo",
                "geo",
                "Target geometry. Supports Mesh, Brep, Surface, and Extrusion.",
                GH_ParamAccess.item);

            pManager.AddVectorParameter(
                "vector",
                "vect",
                "Direction to project along (optional). If tiny/zero -> uses closest-point pull.",
                GH_ParamAccess.item,
                Vector3d.Zero);

            pManager[2].Optional = true;
        }

        // ---------------------------------------------------------------------
        // Outputs
        // ---------------------------------------------------------------------
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "proj_crvs",
                "proj_crvs",
                "Projected curves. Directional projection may output multiple fragments per input curve.",
                GH_ParamAccess.list);
        }

        // ---------------------------------------------------------------------
        // Solve
        // ---------------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var curves = new List<Curve>();
            GeometryBase geo = null;
            Vector3d vector = Vector3d.Zero;

            if (!DA.GetDataList(0, curves)) return;
            if (!DA.GetData(1, ref geo)) return;
            DA.GetData(2, ref vector); // optional

            var outCrvs = new List<Curve>();

            Mesh mesh;
            Brep brep;
            if (!TryGetTarget(geo, out mesh, out brep) || curves == null || curves.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Target geometry must be a valid Mesh, Brep, Surface, or Extrusion.");
                DA.SetDataList(0, outCrvs);
                return;
            }

            // Document tolerance
            double tol = 1e-6;
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null) tol = doc.ModelAbsoluteTolerance;

            bool useProjection = vector.IsValid && !vector.IsTiny();
            if (useProjection)
            {
                // Safe normalize (magnitude irrelevant, direction matters)
                if (!vector.Unitize())
                    useProjection = false;
            }

            for (int i = 0; i < curves.Count; i++)
            {
                var crv = curves[i];
                if (crv == null || !crv.IsValid)
                    continue;

                // --------------------------------------------------------------
                // 1) Directional projection.
                //    Note: can return multiple curve segments.
                // --------------------------------------------------------------
                if (useProjection)
                {
                    Curve[] proj = null;
                    try
                    {
                        if (mesh != null) proj = Curve.ProjectToMesh(crv, mesh, vector, tol);
                        else if (brep != null) proj = Curve.ProjectToBrep(crv, brep, vector, tol);
                    }
                    catch
                    {
                        proj = null;
                    }

                    if (proj != null && proj.Length > 0)
                    {
                        for (int k = 0; k < proj.Length; k++)
                        {
                            var c = proj[k];
                            if (c != null && c.IsValid)
                            {
                                double len = 0.0;
                                try { len = c.GetLength(); } catch { len = 0.0; }

                                if (len > tol)
                                    outCrvs.Add(c);
                            }
                        }

                        // Done with this curve
                        continue;
                    }

                    // If projection failed, fall through to pull
                }

                // --------------------------------------------------------------
                // 2) Fallback: closest-point pull approximation.
                // --------------------------------------------------------------
                Curve pulled = PullCurveToTarget(crv, mesh, brep, tol);
                if (pulled != null && pulled.IsValid)
                {
                    double len = 0.0;
                    try { len = pulled.GetLength(); } catch { len = 0.0; }

                    if (len > tol)
                        outCrvs.Add(pulled);
                }
            }

            DA.SetDataList(0, outCrvs);
        }

        private static bool TryGetTarget(GeometryBase geo, out Mesh mesh, out Brep brep)
        {
            mesh = null;
            brep = null;
            if (geo == null || !geo.IsValid) return false;

            mesh = geo as Mesh;
            if (mesh != null) return mesh.IsValid;

            brep = geo as Brep;
            if (brep != null) return brep.IsValid;

            Surface srf = geo as Surface;
            if (srf != null)
            {
                brep = Brep.CreateFromSurface(srf);
                return brep != null && brep.IsValid;
            }

            Extrusion ext = geo as Extrusion;
            if (ext != null)
            {
                brep = ext.ToBrep();
                return brep != null && brep.IsValid;
            }

            return false;
        }

        private static Curve PullCurveToTarget(Curve crv, Mesh mesh, Brep brep, double tol)
        {
            if (mesh != null)
            {
                try
                {
                    var pulled = crv.PullToMesh(mesh, tol);
                    if (pulled != null && pulled.IsValid) return pulled;
                }
                catch { }
            }

            int sampleCount = EstimateSampleCount(crv);
            var pts = new List<Point3d>(sampleCount + 1);

            for (int i = 0; i <= sampleCount; i++)
            {
                double t = crv.Domain.ParameterAt((double)i / sampleCount);
                Point3d p = crv.PointAt(t);

                Point3d q = Point3d.Unset;
                if (mesh != null)
                {
                    var mp = mesh.ClosestMeshPoint(p, double.MaxValue);
                    if (mp != null) q = mp.Point;
                }
                else if (brep != null)
                {
                    q = ClosestPointOnBrep(p, brep);
                }

                if (q.IsValid) pts.Add(q);
            }

            if (pts.Count < 2) return null;
            return new PolylineCurve(pts);
        }

        private static int EstimateSampleCount(Curve crv)
        {
            double length = 0.0;
            try { length = crv.GetLength(); } catch { length = 0.0; }

            if (length <= RhinoMath.ZeroTolerance) return 12;
            return Math.Max(12, Math.Min(250, (int)Math.Ceiling(length / Math.Max(length / 80.0, 1.0))));
        }

        private static Point3d ClosestPointOnBrep(Point3d pt, Brep brep)
        {
            Point3d best = Point3d.Unset;
            double bestD = double.MaxValue;

            foreach (BrepFace face in brep.Faces)
            {
                double u, v;
                if (!face.ClosestPoint(pt, out u, out v)) continue;
                Point3d p = face.PointAt(u, v);
                double d = pt.DistanceToSquared(p);
                if (d < bestD)
                {
                    bestD = d;
                    best = p;
                }
            }

            return best;
        }
    }
}
