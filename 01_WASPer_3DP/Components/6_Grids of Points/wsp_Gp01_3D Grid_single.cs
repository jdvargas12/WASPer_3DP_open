#region Component Explanation
/*
 * Component Name: wsp_Gp01_3D Grid (Single-mesh)
 * NickName: 3DGrid_single
 *
 * Description:
 *   Generates a strict 3D grid of points within a bounding box (b_box).
 *   Strict spacing keeps the numerical solver stable because dx/dy/dz remain
 *   predictable and are not adjusted independently per axis.
 *
 *   Grid can optionally be trimmed by a closed mesh (bounding_geo). Points that
 *   are numerically on the boundary surface are kept instead of being lost by
 *   strict inside tests.
 *   If b_box is not provided, it is computed from the union bounding box of all input meshes.
 *
 * Inputs:
 *   - mesh        : One or more closed meshes for classification (flattened list).
 *   - bounding_geo: Optional closed mesh boundary for trimming.
 *   - b_box       : Optional bounding box (computed from meshes if missing).
 *   - grid_spacing: Distance between adjacent grid points. If missing or <= 0,
 *                   it is auto-computed from the bounding box size.
 *
 * Outputs:
 *   - out_mesh       : Input meshes (pass-through, list).
 *   - out_b_box      : Bounding box used.
 *   - points_1       : Points inside ANY of the input meshes.
 *   - points_2       : Points not inside any input mesh.
 *   - boundary_points: Points on the bounding box boundary, or closest projected
 *                      points on the bounding_geo surface when bounding_geo is used.
 *   - untrimmed_grid : All grid points (after optional bounding_geo trim).
 *
 * Change log:
 *   v1.2.0 - mesh input changed from single item to flattened list.
 *            points_1 = inside ANY mesh; points_2 = the rest.
 *            Bounding box now computed from the union of all input meshes.
 *            Per-mesh bbox pre-computed for cheap reject before IsPointInside().
 *   v1.2.1 - More robust bounding_geo trim; boundary_points now follow the
 *            actual bounding_geo surface instead of the rectangular b_box.
 *   v1.2.2 - Removed grid_type. Grid is always strict. grid_spacing now
 *            auto-computes from the bounding box when missing or <= 0.
 *   v1.1.0 - Fixed parallel trimming order, removed b_box.PointAt(), tightened
 *            boundary tolerance, fixed strict-mode end-snap that broke heat solver.
 */
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Rhino;
using Rhino.Geometry;

using Grasshopper.Kernel;
#endregion

namespace WASPer_3DP
{
    public class wsp_Gp01_3DGrid_single : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Gp01_3DGrid_single()
          : base("wsp_Gp01_3D Grid (Single-mesh)", "3DGrid_single",
              "Generates a 3D grid of points within a bounding box. Points inside ANY input mesh ? points_1; rest ? points_2. Accepts one or more meshes (list, flattened).", global::WASPer_3DP.WASPerPalette.Performance, "6_Grids of points")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v   = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
            this.Message = _versionTag;
        }

        public override Guid ComponentGuid
            => new Guid("A9C9E63A-EB9F-4E99-B1E7-8E04E84A2D31");

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.13_Grid_from_mesh.png"))
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                }
                catch { }
                return null;
            }
        }

        // -----------------------------------------------------------------------
        // IO
        // -----------------------------------------------------------------------
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddMeshParameter("mesh", "mesh",
                "One or more closed meshes for classification (list, flattened). " +
                "Points inside ANY mesh go to points_1; all others go to points_2.",
                GH_ParamAccess.list);
            pManager[0].DataMapping = GH_DataMapping.Flatten;

            pManager.AddMeshParameter("bounding_geo", "bounding_geo",
                "Optional closed mesh defining an irregular boundary. Points inside it, or numerically on its surface, are kept.",
                GH_ParamAccess.item);
            pManager[1].Optional = true;

            pManager.AddBoxParameter("b_box", "b_box",
                "Optional bounding box. If invalid or missing, computed from the union of all input meshes.",
                GH_ParamAccess.item);
            pManager[2].Optional = true;

            pManager.AddNumberParameter("grid_spacing", "grid_spacing",
                "Distance between adjacent grid points in model units. If missing or <= 0, it is auto-computed from the bounding box size.",
                GH_ParamAccess.item);
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter("out_mesh", "out_mesh",
                "The input mesh(es) passed through for reference.", GH_ParamAccess.list);
            pManager.AddBoxParameter("out_b_box", "out_b_box",
                "The bounding box used for generating the grid.", GH_ParamAccess.item);
            pManager.AddPointParameter("points_1", "points_1",
                "Grid points inside ANY of the input meshes.", GH_ParamAccess.list);
            pManager.AddPointParameter("points_2", "points_2",
                "Grid points not inside any input mesh.", GH_ParamAccess.list);
            pManager.AddPointParameter("boundary_points", "boundary_points",
                "Points on the bounding box boundary. If bounding_geo is provided, closest projected points on the actual bounding_geo surface.",
                GH_ParamAccess.list);
            pManager.AddPointParameter("untrimmed_grid", "untrimmed_grid",
                "All generated grid points (after trimming by bounding_geo, if provided).",
                GH_ParamAccess.list);
        }

        // -----------------------------------------------------------------------
        // Solve
        // -----------------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            // --- Inputs ---
            var meshes = new List<Mesh>();
            if (!DA.GetDataList(0, meshes)) return;

            Mesh bounding_geo = null;
            DA.GetData(1, ref bounding_geo);

            Box b_box = new Box();
            DA.GetData(2, ref b_box);

            double grid_spacing = 0.0;
            DA.GetData(3, ref grid_spacing);

            // --- Validate ---
            meshes.RemoveAll(m => m == null);
            if (meshes.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No valid meshes provided.");
                return;
            }

            // --- Build bounding box from union of all meshes if not provided ---
            if (!b_box.IsValid)
            {
                BoundingBox unionBB = BoundingBox.Empty;
                foreach (Mesh m in meshes)
                {
                    BoundingBox bb = m.GetBoundingBox(true);
                    if (bb.IsValid) unionBB.Union(bb);
                }

                if (!unionBB.IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        "Could not compute a valid bounding box from the input meshes.");
                    return;
                }

                b_box = new Box(unionBB);
            }

            if (!b_box.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Bounding box is invalid.");
                return;
            }

            Interval xDom = b_box.X;
            Interval yDom = b_box.Y;
            Interval zDom = b_box.Z;
            double xLen = xDom.Length;
            double yLen = yDom.Length;
            double zLen = zDom.Length;

            double tol = RhinoDoc.ActiveDoc == null ? 0.01 : RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;

            if (grid_spacing <= 0.0)
            {
                grid_spacing = AutoGridSpacing(xLen, yLen, zLen, tol);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "grid_spacing was missing or <= 0. Auto grid spacing = " + grid_spacing.ToString("0.###") + ".");
            }

            // Tight bbox tolerance catches only points truly sitting on a bbox face
            // (not interior points near a face). Mesh-surface trimming uses model
            // tolerance so boundary-coincident points are not discarded.
            const double boundaryTol = 1e-6;
            double surfaceTol = Math.Max(tol * 2.0, boundaryTol);

            int parallelThreshold = 5000;

            // Pre-compute per-mesh bounding boxes for cheap reject before IsPointInside()
            var meshBBs = new BoundingBox[meshes.Count];
            for (int i = 0; i < meshes.Count; i++)
                meshBBs[i] = meshes[i].GetBoundingBox(true);

            // ------------------------------------------------------------------
            // 1. Generate all grid points (direct world coordinates — no PointAt)
            // ------------------------------------------------------------------
            List<Point3d> allPoints;
            int nxCount, nyCount, nzCount;
            double stepXUsed, stepYUsed, stepZUsed;

            // STRICT: exact grid_spacing steps.
            // We do NOT force-snap the last point to T1 — that would create a
            // non-uniform final gap which corrupts the heat-solver's dx/dy/dz
            // detection via MeanPositiveGap(). The clamp below only fires when a
            // point overshoots T1 by less than boundaryTol (pure FP rounding).
            int nx = (int)Math.Floor(xLen / grid_spacing) + 1;
            int ny = (int)Math.Floor(yLen / grid_spacing) + 1;
            int nz = (int)Math.Floor(zLen / grid_spacing) + 1;
            nxCount = nx;
            nyCount = ny;
            nzCount = nz;
            stepXUsed = grid_spacing;
            stepYUsed = grid_spacing;
            stepZUsed = grid_spacing;

            allPoints = new List<Point3d>(nx * ny * nz);

            for (int i = 0; i < nx; i++)
            {
                double xVal = xDom.T0 + i * grid_spacing;
                if (xVal > xDom.T1 + boundaryTol) break;
                if (xVal > xDom.T1) xVal = xDom.T1;

                for (int j = 0; j < ny; j++)
                {
                    double yVal = yDom.T0 + j * grid_spacing;
                    if (yVal > yDom.T1 + boundaryTol) break;
                    if (yVal > yDom.T1) yVal = yDom.T1;

                    for (int k = 0; k < nz; k++)
                    {
                        double zVal = zDom.T0 + k * grid_spacing;
                        if (zVal > zDom.T1 + boundaryTol) break;
                        if (zVal > zDom.T1) zVal = zDom.T1;

                        allPoints.Add(new Point3d(xVal, yVal, zVal));
                    }
                }
            }

            // ------------------------------------------------------------------
            // 2. Trim by bounding_geo — bool[] preserves point order
            // ------------------------------------------------------------------
            List<Point3d> finalGrid;
            bool[] trimKeepMask = null;
            bool hasClosedBoundingGeo = bounding_geo != null && bounding_geo.IsValid && bounding_geo.IsClosed;

            if (bounding_geo != null && !hasClosedBoundingGeo)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "bounding_geo was provided but is not a valid closed mesh. Grid was not trimmed by bounding_geo.");
            }

            if (hasClosedBoundingGeo)
            {
                bool[] keep = new bool[allPoints.Count];
                BoundingBox trimBB = bounding_geo.GetBoundingBox(true);
                trimBB.Inflate(surfaceTol);

                if (allPoints.Count < parallelThreshold)
                {
                    for (int i = 0; i < allPoints.Count; i++)
                        keep[i] = IsInsideOrOnMesh(bounding_geo, trimBB, allPoints[i], tol, surfaceTol);
                }
                else
                {
                    Parallel.For(0, allPoints.Count, i =>
                        keep[i] = IsInsideOrOnMesh(bounding_geo, trimBB, allPoints[i], tol, surfaceTol));
                }

                trimKeepMask = keep;
                finalGrid = new List<Point3d>(allPoints.Count);
                for (int i = 0; i < allPoints.Count; i++)
                    if (keep[i]) finalGrid.Add(allPoints[i]);
            }
            else
            {
                finalGrid = allPoints;
            }

            // ------------------------------------------------------------------
            // 3. Classify: inside ANY mesh ? points_1, rest ? points_2, + boundary
            // ------------------------------------------------------------------
            var insidePts = new List<Point3d>(finalGrid.Count / 2);
            var outsidePts = new List<Point3d>(finalGrid.Count / 2);
            var boundaryPts = new List<Point3d>();

            bool collectBoxBoundary = !hasClosedBoundingGeo;

            if (finalGrid.Count < parallelThreshold)
            {
                for (int i = 0; i < finalGrid.Count; i++)
                    ClassifyPoint(finalGrid[i], meshes, meshBBs, tol,
                                  xDom, yDom, zDom, boundaryTol,
                                  collectBoxBoundary,
                                  insidePts, outsidePts, boundaryPts);
            }
            else
            {
                // Chunked parallel: each chunk writes to an indexed slot ? merge in order
                int chunkSize = Math.Max(1, finalGrid.Count / (Environment.ProcessorCount * 4));
                int numChunks = (int)Math.Ceiling((double)finalGrid.Count / chunkSize);

                var localIn = new List<Point3d>[numChunks];
                var localOut = new List<Point3d>[numChunks];
                var localBnd = new List<Point3d>[numChunks];

                Parallel.For(0, numChunks, chunk =>
                {
                    int start = chunk * chunkSize;
                    int end = Math.Min(start + chunkSize, finalGrid.Count);

                    var lIn = new List<Point3d>();
                    var lOut = new List<Point3d>();
                    var lBnd = new List<Point3d>();

                    for (int i = start; i < end; i++)
                        ClassifyPoint(finalGrid[i], meshes, meshBBs, tol,
                                      xDom, yDom, zDom, boundaryTol,
                                      collectBoxBoundary,
                                      lIn, lOut, lBnd);

                    localIn[chunk] = lIn;
                    localOut[chunk] = lOut;
                    localBnd[chunk] = lBnd;
                });

                for (int c = 0; c < numChunks; c++)
                {
                    if (localIn[c] != null && localIn[c].Count > 0) insidePts.AddRange(localIn[c]);
                    if (localOut[c] != null && localOut[c].Count > 0) outsidePts.AddRange(localOut[c]);
                    if (localBnd[c] != null && localBnd[c].Count > 0) boundaryPts.AddRange(localBnd[c]);
                }
            }

            if (hasClosedBoundingGeo && trimKeepMask != null)
            {
                double boundaryBand = 0.5 * Math.Sqrt(
                    stepXUsed * stepXUsed +
                    stepYUsed * stepYUsed +
                    stepZUsed * stepZUsed) + surfaceTol;

                boundaryPts = BuildMeshBoundaryPoints(
                    allPoints,
                    trimKeepMask,
                    nxCount, nyCount, nzCount,
                    bounding_geo,
                    boundaryBand,
                    surfaceTol);
            }

            // ------------------------------------------------------------------
            // 4. Outputs
            // ------------------------------------------------------------------
            DA.SetDataList(0, meshes);
            DA.SetData(1, b_box);
            DA.SetDataList(2, insidePts);
            DA.SetDataList(3, outsidePts);
            DA.SetDataList(4, boundaryPts);
            DA.SetDataList(5, finalGrid);
        }

        // -----------------------------------------------------------------------
        // Helper
        // -----------------------------------------------------------------------

        /// <summary>
        /// Classify a single grid point:
        ///   • inside ANY mesh  ? inside list  (stops at first match)
        ///   • otherwise        ? outside list
        ///   • on any bbox face ? boundary list (independent of inside/outside)
        /// </summary>
        private static void ClassifyPoint(
            Point3d pt,
            List<Mesh> meshes, BoundingBox[] meshBBs, double tol,
            Interval xDom, Interval yDom, Interval zDom, double boundaryTol,
            bool collectBoxBoundary,
            List<Point3d> inside, List<Point3d> outside, List<Point3d> boundary)
        {
            bool isInsideAny = false;
            for (int i = 0; i < meshes.Count; i++)
            {
                if (!meshBBs[i].Contains(pt)) continue;         // cheap reject
                if (meshes[i].IsPointInside(pt, tol, false))
                {
                    isInsideAny = true;
                    break;
                }
            }

            if (isInsideAny) inside.Add(pt);
            else outside.Add(pt);

            if (collectBoxBoundary &&
                (Math.Abs(pt.X - xDom.T0) <= boundaryTol || Math.Abs(pt.X - xDom.T1) <= boundaryTol ||
                 Math.Abs(pt.Y - yDom.T0) <= boundaryTol || Math.Abs(pt.Y - yDom.T1) <= boundaryTol ||
                 Math.Abs(pt.Z - zDom.T0) <= boundaryTol || Math.Abs(pt.Z - zDom.T1) <= boundaryTol))
            {
                boundary.Add(pt);
            }
        }

        private static bool IsInsideOrOnMesh(
            Mesh mesh,
            BoundingBox inflatedBB,
            Point3d pt,
            double insideTol,
            double surfaceTol)
        {
            if (mesh == null || !mesh.IsValid) return false;
            if (inflatedBB.IsValid && !inflatedBB.Contains(pt)) return false;

            if (mesh.IsPointInside(pt, insideTol, false)) return true;

            Point3d closest;
            double dist;
            return TryClosestPointOnMesh(mesh, pt, surfaceTol, out closest, out dist) && dist <= surfaceTol;
        }

        private static double AutoGridSpacing(double xLen, double yLen, double zLen, double tol)
        {
            double maxLen = Math.Max(Math.Abs(xLen), Math.Max(Math.Abs(yLen), Math.Abs(zLen)));
            if (maxLen <= RhinoMath.ZeroTolerance)
                return Math.Max(tol * 10.0, 1.0);

            return Math.Max(maxLen / 30.0, Math.Max(tol * 10.0, RhinoMath.ZeroTolerance));
        }

        private static List<Point3d> BuildMeshBoundaryPoints(
            List<Point3d> allPoints,
            bool[] keepMask,
            int nxCount,
            int nyCount,
            int nzCount,
            Mesh boundaryMesh,
            double boundaryBand,
            double dedupeTol)
        {
            var result = new List<Point3d>();
            if (allPoints == null || keepMask == null || boundaryMesh == null || !boundaryMesh.IsValid)
                return result;

            var seen = new HashSet<string>();
            double keyTol = Math.Max(dedupeTol, 1e-9);

            for (int ix = 0; ix < nxCount; ix++)
            {
                for (int iy = 0; iy < nyCount; iy++)
                {
                    for (int iz = 0; iz < nzCount; iz++)
                    {
                        int index = GridIndex(ix, iy, iz, nyCount, nzCount);
                        if (index < 0 || index >= keepMask.Length || !keepMask[index]) continue;
                        if (!HasExteriorNeighbor(keepMask, ix, iy, iz, nxCount, nyCount, nzCount)) continue;

                        Point3d closest;
                        double dist;
                        if (!TryClosestPointOnMesh(boundaryMesh, allPoints[index], boundaryBand, out closest, out dist)) continue;
                        if (dist > boundaryBand) continue;

                        string key = QuantizedKey(closest, keyTol);
                        if (seen.Add(key)) result.Add(closest);
                    }
                }
            }

            return result;
        }

        private static bool HasExteriorNeighbor(
            bool[] keepMask,
            int ix,
            int iy,
            int iz,
            int nxCount,
            int nyCount,
            int nzCount)
        {
            if (ix == 0 || ix == nxCount - 1 ||
                iy == 0 || iy == nyCount - 1 ||
                iz == 0 || iz == nzCount - 1)
                return true;

            return !keepMask[GridIndex(ix - 1, iy, iz, nyCount, nzCount)] ||
                   !keepMask[GridIndex(ix + 1, iy, iz, nyCount, nzCount)] ||
                   !keepMask[GridIndex(ix, iy - 1, iz, nyCount, nzCount)] ||
                   !keepMask[GridIndex(ix, iy + 1, iz, nyCount, nzCount)] ||
                   !keepMask[GridIndex(ix, iy, iz - 1, nyCount, nzCount)] ||
                   !keepMask[GridIndex(ix, iy, iz + 1, nyCount, nzCount)];
        }

        private static int GridIndex(int ix, int iy, int iz, int nyCount, int nzCount)
        {
            return ((ix * nyCount) + iy) * nzCount + iz;
        }

        private static bool TryClosestPointOnMesh(Mesh mesh, Point3d pt, double maxDistance, out Point3d closest, out double distance)
        {
            closest = Point3d.Unset;
            distance = double.PositiveInfinity;

            if (mesh == null || !mesh.IsValid) return false;

            MeshPoint mp = mesh.ClosestMeshPoint(pt, Math.Max(0.0, maxDistance));
            if (mp == null) return false;

            closest = mesh.PointAt(mp);
            if (!closest.IsValid) return false;

            distance = pt.DistanceTo(closest);
            return true;
        }

        private static string QuantizedKey(Point3d pt, double tol)
        {
            long x = (long)Math.Round(pt.X / tol);
            long y = (long)Math.Round(pt.Y / tol);
            long z = (long)Math.Round(pt.Z / tol);
            return x.ToString() + "|" + y.ToString() + "|" + z.ToString();
        }
    }
}
