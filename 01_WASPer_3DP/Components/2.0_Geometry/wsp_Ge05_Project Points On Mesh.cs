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
    //  wsp_Ge05_Project Points On Geometry
    //  - Projects points onto Mesh/Brep/Surface/Extrusion geometry using:
    //      (1) Directional projection along a provided vector
    //      (2) Fallback: closest point
    //  Outputs:
    //      proj_pts  : projected points
    //      distances : distance moved per point (tiny moves snapped to 0)
    //  Version tag (yyMMdd): 251023
    // =========================================================================
    public class wsp_Ge05_Project_Points_On_Mesh : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Ge05_Project_Points_On_Mesh()
          : base(
              "wsp_Ge05_Project Points On Geometry",
              "Project/Pull?Geo (Pts)",
              "Projects points onto Mesh, Brep, Surface, or Extrusion geometry. If a direction vector is provided, uses directional projection; otherwise falls back to closest point.",
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
        public override Guid ComponentGuid => new Guid("F3C7B2A8-7B1F-4D6B-9A3D-0B5F8C2C0A19");

        public override GH_Exposure Exposure => GH_Exposure.tertiary;

        // Optional icon (set later if you want)
        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Ge05_Project Points On Mesh.png"))
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
            pManager.AddPointParameter(
                "points",
                "pts",
                "Points to project.",
                GH_ParamAccess.list);

            pManager.AddGeometryParameter(
                "geo",
                "geo",
                "Target geometry. Supports Mesh, Brep, Surface, and Extrusion.",
                GH_ParamAccess.item);

            pManager.AddVectorParameter(
                "vector",
                "vect",
                "Direction to project along (optional). If tiny/zero -> uses closest point.",
                GH_ParamAccess.item,
                Vector3d.Zero);

            // Make vector optional (so users can leave it disconnected)
            pManager[2].Optional = true;
        }

        // ---------------------------------------------------------------------
        // Outputs
        // ---------------------------------------------------------------------
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddPointParameter(
                "proj_pts",
                "proj_pts",
                "Projected points or closest geometry points (fallback).",
                GH_ParamAccess.list);

            pManager.AddNumberParameter(
                "distances",
                "dist",
                "Distance each point moved (model units). Tiny moves (< tol) are snapped to 0.",
                GH_ParamAccess.list);
        }

        // ---------------------------------------------------------------------
        // Solve
        // ---------------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var points = new List<Point3d>();
            GeometryBase geo = null;
            Vector3d vector = Vector3d.Zero;

            if (!DA.GetDataList(0, points)) return;
            if (!DA.GetData(1, ref geo)) return;
            DA.GetData(2, ref vector); // optional

            var outPts = new List<Point3d>(points.Count);
            var outDists = new List<double>(points.Count);

            Mesh mesh;
            Brep brep;
            if (!TryGetTarget(geo, out mesh, out brep) || points == null || points.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "Target geometry must be a valid Mesh, Brep, Surface, or Extrusion.");
                DA.SetDataList(0, outPts);
                DA.SetDataList(1, outDists);
                return;
            }

            // Document tolerance
            double tol = 1e-6;
            var doc = RhinoDoc.ActiveDoc;
            if (doc != null) tol = doc.ModelAbsoluteTolerance;

            bool useProjection = vector.IsValid && !vector.IsTiny();
            if (useProjection)
            {
                // Safe normalize
                if (!vector.Unitize())
                    useProjection = false;
            }

            for (int i = 0; i < points.Count; i++)
            {
                var pt = points[i];

                if (!pt.IsValid)
                {
                    outPts.Add(Point3d.Unset);
                    outDists.Add(0.0);
                    continue;
                }

                Point3d finalPt = Point3d.Unset;

                // --------------------------
                // 1) Directional projection
                // --------------------------
                if (useProjection)
                {
                    if (mesh != null) finalPt = ProjectPointToMesh(pt, mesh, vector);
                    else if (brep != null) finalPt = ProjectPointToBrep(pt, brep, vector, tol);
                }

                // --------------------------
                // 2) Fallback: closest point
                // --------------------------
                if (!finalPt.IsValid)
                {
                    try
                    {
                        if (mesh != null)
                        {
                            var mp = mesh.ClosestMeshPoint(pt, double.MaxValue);
                            if (mp != null && mp.Point.IsValid)
                                finalPt = mp.Point;
                        }
                        else if (brep != null)
                        {
                            finalPt = ClosestPointOnBrep(pt, brep);
                        }
                    }
                    catch
                    {
                        // keep invalid -> will fallback to original point
                    }
                }

                // --------------------------
                // 3) If still invalid: keep original
                // --------------------------
                if (!finalPt.IsValid)
                    finalPt = pt;

                outPts.Add(finalPt);

                double d = pt.DistanceTo(finalPt);
                if (d < tol) d = 0.0;
                outDists.Add(d);
            }

            DA.SetDataList(0, outPts);
            DA.SetDataList(1, outDists);
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

        private static Point3d ProjectPointToMesh(Point3d pt, Mesh mesh, Vector3d dir)
        {
            double bestT = double.MaxValue;
            Point3d bestHit = Point3d.Unset;

            try
            {
                double tF = Intersection.MeshRay(mesh, new Ray3d(pt, dir));
                if (tF >= 0.0)
                {
                    bestT = tF;
                    bestHit = pt + dir * tF;
                }

                double tB = Intersection.MeshRay(mesh, new Ray3d(pt, -dir));
                if (tB >= 0.0 && tB < bestT)
                    bestHit = pt - dir * tB;
            }
            catch { }

            return bestHit;
        }

        private static Point3d ProjectPointToBrep(Point3d pt, Brep brep, Vector3d dir, double tol)
        {
            BoundingBox bb = brep.GetBoundingBox(true);
            double length = Math.Max(bb.Diagonal.Length * 4.0, tol * 100.0);
            var line = new LineCurve(pt - dir * length, pt + dir * length);

            Curve[] overlapCurves;
            Point3d[] hitPts;
            if (!Intersection.CurveBrep(line, brep, tol, out overlapCurves, out hitPts) || hitPts == null || hitPts.Length == 0)
                return Point3d.Unset;

            Point3d best = Point3d.Unset;
            double bestD = double.MaxValue;
            for (int i = 0; i < hitPts.Length; i++)
            {
                double d = pt.DistanceToSquared(hitPts[i]);
                if (d < bestD)
                {
                    bestD = d;
                    best = hitPts[i];
                }
            }

            return best;
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
