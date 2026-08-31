#region Component Explanation
/*
 * Component Name: wsp_Gp02_3D Grid (multi-mesh)
 * NickName: 3DGrid_multi
 *
 * Description:
 *   Generates a strict 3D grid of points inside a bounding region and classifies
 *   every point into exactly ONE mesh branch (branches by mesh index).
 *
 *   Key rules:
 *   - The grid is ALWAYS generated inside the user-provided b_box if valid,
 *     otherwise inside a union bounding box computed from all input meshes
 *     (and bounding_geo if provided).
 *   - Optional trimming: if bounding_geo is a closed mesh, points inside it or
 *     numerically on its surface are kept.
 *   - Branch assignment:
 *       1) If a point is inside one or more meshes, assign it to the FIRST mesh (by input order) that contains it.
 *       2) Otherwise, assign it to the NEAREST mesh (by ClosestPoint distance).
 *
 *   Performance:
 *   - Uses bounding-box prechecks per mesh to reduce IsPointInside calls.
 *   - Uses parallel processing with chunking.
 *   - Merges chunk results in order to preserve point order.
 *   - grid_spacing auto-computes from the bounding box when missing or <= 0.
 *
 * Outputs:
 *   - points (Tree<Point3d>): one branch per mesh index {i}. EVERY grid point is assigned to one branch.
 *   - boundary_points (List<Point3d>): points on the b_box boundary, or closest
 *     projected points on the bounding_geo surface when bounding_geo is used.
 *   - untrimmed_grid (List<Point3d>): the grid after optional trimming by bounding_geo (order preserved).
 */
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Rhino;
using Rhino.Geometry;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
#endregion

namespace WASPer_3DP.Components._6_Grids_of_points
{
    public class wsp_Gp02_3DGrid_multi : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Gp02_3DGrid_multi()
            : base(
                "wsp_Gp02_3D Grid (multi-mesh)",
                "3DGrid_multi",
                "Generates a 3D grid of points within a bounding region and assigns EVERY point to exactly one mesh branch.\n" +
                "Optional trimming by a closed bounding_geo mesh.\n" +
                "Strict solver-safe spacing only. grid_spacing can auto-compute from the bounding box.",
                global::WASPer_3DP.WASPerPalette.Performance,
                "6_Grids of points")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null
                ? $"v{v.Major}.{v.Minor}.{v.Build}"
                : "v1.0.x";
            this.Message = _versionTag;
        }

        public override Guid ComponentGuid
        {
            get { return new Guid("3F9A9B82-0E4B-4D6F-A4E5-ABF52E3C8B4E"); }
        }

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.14_Grid_from_mesh_mult.png"))
                    {
                        return stream != null ? new System.Drawing.Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        // --------------------------------------------------------------------
        // IO
        // --------------------------------------------------------------------
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddMeshParameter("meshes", "meshes", "Meshes used for classification (one branch per mesh index).", GH_ParamAccess.list);
            pManager[0].DataMapping = GH_DataMapping.Flatten;

            pManager.AddMeshParameter("bounding_geo", "bounding_geo", "Optional closed mesh to trim the grid. Points inside it, or numerically on its surface, are kept.", GH_ParamAccess.item);
            pManager[1].Optional = true;

            pManager.AddBoxParameter("b_box", "b_box", "Optional bounding box. If invalid, computed as union bounding box of all meshes (and bounding_geo).", GH_ParamAccess.item);
            pManager[2].Optional = true;

            pManager.AddNumberParameter("grid_spacing", "grid_spacing", "Grid spacing in model units. If missing or <= 0, it is auto-computed from the bounding box size.", GH_ParamAccess.item);
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter("out_meshes", "out_meshes", "Meshes passed through for reference.", GH_ParamAccess.list);
            pManager.AddBoxParameter("out_b_box", "out_b_box", "Bounding box used for generating the grid.", GH_ParamAccess.item);

            pManager.AddPointParameter("points", "points", "Data tree of grid points (branches by mesh). EVERY grid point is assigned to one branch.", GH_ParamAccess.tree);

            pManager.AddPointParameter("boundary_points", "boundary_points", "Points on the bounding box boundary. If bounding_geo is provided, closest projected points on the actual bounding_geo surface.", GH_ParamAccess.list);
            pManager.AddPointParameter("untrimmed_grid", "untrimmed_grid", "All grid points after optional trimming by bounding_geo (order preserved).", GH_ParamAccess.list);
        }

        // --------------------------------------------------------------------
        // Solve
        // --------------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            this.Message = _versionTag;

            var meshes = new List<Mesh>();
            if (!DA.GetDataList(0, meshes)) return;
            meshes.RemoveAll(m => m == null);

            if (meshes.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No valid meshes provided.");
                return;
            }

            Mesh boundingGeo = null;
            DA.GetData(1, ref boundingGeo);

            Box b_box = new Box();
            DA.GetData(2, ref b_box);

            double grid_spacing = 0.0;
            DA.GetData(3, ref grid_spacing);

            double tol = RhinoDoc.ActiveDoc == null ? 0.01 : RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
            double boundaryTol = 1e-9;
            double surfaceTol = Math.Max(tol * 2.0, boundaryTol);

            // ----------------------------------------------------------------
            // 1) Determine bounding box
            // ----------------------------------------------------------------
            // ----------------------------------------------------------------
            // Safety: wrap user Box into a world-axis-aligned BoundingBox
            // This prevents rotated boxes from producing "unexpected" grids and
            // makes the heat solver more stable.
            // ----------------------------------------------------------------
            bool userProvidedBox = b_box.IsValid;

            if (userProvidedBox)
            {
                double angTol = RhinoMath.ToRadians(1.0);

                bool zParallel = b_box.Plane.ZAxis.IsParallelTo(Vector3d.ZAxis, angTol) != 0;
                bool xParallel = b_box.Plane.XAxis.IsParallelTo(Vector3d.XAxis, angTol) != 0;
                bool yParallel = b_box.Plane.YAxis.IsParallelTo(Vector3d.YAxis, angTol) != 0;

                bool boxIsWorldAligned = zParallel && xParallel && yParallel;

                BoundingBox safeBB = b_box.BoundingBox;
                if (!safeBB.IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input b_box produced an invalid BoundingBox.");
                    return;
                }

                // Always wrap for safety (as requested)
                b_box = new Box(safeBB);

                if (!boxIsWorldAligned)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "Input b_box is rotated. For safety, the grid is generated inside its world-axis-aligned BoundingBox.");
                }
            }
            else
            {
                // No valid box: compute union bbox from meshes (and bounding_geo if present)
                BoundingBox bb = BoundingBox.Empty;

                for (int i = 0; i < meshes.Count; i++)
                    bb.Union(meshes[i].GetBoundingBox(true));

                if (boundingGeo != null)
                    bb.Union(boundingGeo.GetBoundingBox(true));

                if (!bb.IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Could not compute a valid bounding box.");
                    return;
                }

                b_box = new Box(bb);
            }

            if (!b_box.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid bounding box.");
                return;
            }

            Interval xDom = b_box.X;
            Interval yDom = b_box.Y;
            Interval zDom = b_box.Z;

            double xLen = xDom.Length;
            double yLen = yDom.Length;
            double zLen = zDom.Length;

            if (grid_spacing <= 0.0)
            {
                grid_spacing = AutoGridSpacing(xLen, yLen, zLen, tol);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "grid_spacing was missing or <= 0. Auto grid spacing = " + grid_spacing.ToString("0.###") + ".");
            }

            // ----------------------------------------------------------------
            // 2) Grid steps
            // ----------------------------------------------------------------
            int stepsX, stepsY, stepsZ;
            double stepX, stepY, stepZ;

            // Strict spacing only: exact grid_spacing steps. This avoids the
            // adaptive per-axis step changes that can break numerical solvers.
            stepsX = (int)Math.Floor(xLen / grid_spacing) + 1;
            stepsY = (int)Math.Floor(yLen / grid_spacing) + 1;
            stepsZ = (int)Math.Floor(zLen / grid_spacing) + 1;

            stepX = grid_spacing;
            stepY = grid_spacing;
            stepZ = grid_spacing;

            // ----------------------------------------------------------------
            // 3) Generate full grid (ORDERED)
            // ----------------------------------------------------------------
            var allPoints = new List<Point3d>(Math.Max(1, stepsX * stepsY * stepsZ));

            for (int i = 0; i < stepsX; i++)
            {
                double xVal = xDom.T0 + i * stepX;
                if (xVal > xDom.T1) xVal = xDom.T1;

                for (int j = 0; j < stepsY; j++)
                {
                    double yVal = yDom.T0 + j * stepY;
                    if (yVal > yDom.T1) yVal = yDom.T1;

                    for (int k = 0; k < stepsZ; k++)
                    {
                        double zVal = zDom.T0 + k * stepZ;
                        if (zVal > zDom.T1) zVal = zDom.T1;

                        allPoints.Add(new Point3d(xVal, yVal, zVal));
                    }
                }
            }

            // ----------------------------------------------------------------
            // 4) Optional trim by bounding_geo (ORDER PRESERVED)
            // ----------------------------------------------------------------
            List<Point3d> finalGrid;
            bool[] trimKeepMask = null;
            bool hasClosedBoundingGeo = boundingGeo != null && boundingGeo.IsValid && boundingGeo.IsClosed;

            if (boundingGeo != null && !hasClosedBoundingGeo)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "bounding_geo was provided but is not a valid closed mesh. Grid was not trimmed by bounding_geo.");
            }

            if (hasClosedBoundingGeo)
            {
                bool[] keep = new bool[allPoints.Count];
                BoundingBox trimBB = boundingGeo.GetBoundingBox(true);
                trimBB.Inflate(surfaceTol);

                int parallelThreshold = 5000;
                if (allPoints.Count < parallelThreshold)
                {
                    for (int i = 0; i < allPoints.Count; i++)
                        keep[i] = IsInsideOrOnMesh(boundingGeo, trimBB, allPoints[i], tol, surfaceTol);
                }
                else
                {
                    Parallel.For(0, allPoints.Count, i =>
                    {
                        keep[i] = IsInsideOrOnMesh(boundingGeo, trimBB, allPoints[i], tol, surfaceTol);
                    });
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

            // ----------------------------------------------------------------
            // 5) Classification per point with order-safe parallel chunking
            // ----------------------------------------------------------------
            var meshBBs = new BoundingBox[meshes.Count];
            for (int mi = 0; mi < meshes.Count; mi++)
                meshBBs[mi] = meshes[mi].GetBoundingBox(true);

            int count = finalGrid.Count;
            int cpu = Math.Max(1, Environment.ProcessorCount);
            int chunkSize = Math.Max(2000, count / (cpu * 4));
            int chunkCount = (int)Math.Ceiling(count / (double)chunkSize);

            var chunkMeshPts = new List<Point3d>[chunkCount][];
            var chunkBoundary = new List<Point3d>[chunkCount];
            bool collectBoxBoundary = !hasClosedBoundingGeo;

            Parallel.For(0, chunkCount, c =>
            {
                int start = c * chunkSize;
                int end = Math.Min(start + chunkSize, count);

                var localPerMesh = new List<Point3d>[meshes.Count];
                for (int mi = 0; mi < meshes.Count; mi++)
                    localPerMesh[mi] = new List<Point3d>();

                var localBoundary = new List<Point3d>();

                for (int pi = start; pi < end; pi++)
                {
                    Point3d pt = finalGrid[pi];

                    // Assign: inside-first, else nearest
                    bool assignedInside = false;

                    for (int mi = 0; mi < meshes.Count; mi++)
                    {
                        if (!meshBBs[mi].Contains(pt)) continue;

                        if (meshes[mi].IsPointInside(pt, tol, false))
                        {
                            localPerMesh[mi].Add(pt);
                            assignedInside = true;
                            break;
                        }
                    }

                    if (!assignedInside)
                    {
                        int best = 0;
                        double bestDist = double.MaxValue;

                        for (int mi = 0; mi < meshes.Count; mi++)
                        {
                            Point3d cp = meshes[mi].ClosestPoint(pt);
                            double d = pt.DistanceTo(cp);
                            if (d < bestDist)
                            {
                                bestDist = d;
                                best = mi;
                            }
                        }

                        localPerMesh[best].Add(pt);
                    }

                    // Boundary check (box-local coords). If bounding_geo is used,
                    // boundary_points are rebuilt from the actual mesh surface.
                    if (collectBoxBoundary &&
                        (Math.Abs(pt.X - xDom.T0) < boundaryTol || Math.Abs(pt.X - xDom.T1) < boundaryTol ||
                         Math.Abs(pt.Y - yDom.T0) < boundaryTol || Math.Abs(pt.Y - yDom.T1) < boundaryTol ||
                         Math.Abs(pt.Z - zDom.T0) < boundaryTol || Math.Abs(pt.Z - zDom.T1) < boundaryTol))
                    {
                        localBoundary.Add(pt);
                    }
                }

                chunkMeshPts[c] = localPerMesh;
                chunkBoundary[c] = localBoundary;
            });

            // Merge chunks in order to preserve order in each branch
            var perMeshPts = new List<Point3d>[meshes.Count];
            for (int mi = 0; mi < meshes.Count; mi++)
                perMeshPts[mi] = new List<Point3d>();

            var boundaryPts = new List<Point3d>();

            for (int c = 0; c < chunkCount; c++)
            {
                var cm = chunkMeshPts[c];
                if (cm != null)
                {
                    for (int mi = 0; mi < meshes.Count; mi++)
                        if (cm[mi].Count > 0)
                            perMeshPts[mi].AddRange(cm[mi]);
                }

                if (chunkBoundary[c] != null && chunkBoundary[c].Count > 0)
                    boundaryPts.AddRange(chunkBoundary[c]);
            }

            if (hasClosedBoundingGeo && trimKeepMask != null)
            {
                double boundaryBand = 0.5 * Math.Sqrt(
                    stepX * stepX +
                    stepY * stepY +
                    stepZ * stepZ) + surfaceTol;

                boundaryPts = BuildMeshBoundaryPoints(
                    allPoints,
                    trimKeepMask,
                    stepsX, stepsY, stepsZ,
                    boundingGeo,
                    boundaryBand,
                    surfaceTol);
            }

            // ----------------------------------------------------------------
            // 6) Build GH trees
            // ----------------------------------------------------------------
            var pointTree = new GH_Structure<GH_Point>();
            for (int mi = 0; mi < meshes.Count; mi++)
            {
                var path = new GH_Path(mi);
                foreach (var p in perMeshPts[mi])
                    pointTree.Append(new GH_Point(p), path);
            }

            // ----------------------------------------------------------------
            // 7) Outputs
            // ----------------------------------------------------------------
            DA.SetDataList(0, meshes);
            DA.SetData(1, b_box);
            DA.SetDataTree(2, pointTree);
            DA.SetDataList(3, boundaryPts);
            DA.SetDataList(4, finalGrid);
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
