#region Component Explanation
/*
 * Component Name: wsp_Gp04_3D Grid (multi-field)
 * NickName: 3DGrid_fields
 *
 * Description:
 *   SDF-based counterpart of Gp02. Generates a strict 3D grid and assigns EVERY
 *   point to exactly ONE field branch, using WasperField evaluation instead of
 *   mesh ray-casting / closest-point queries.
 *
 *   Convention: f(pt) <= 0  →  inside (material); f(pt) > 0  →  outside.
 *
 *   Branch assignment:
 *     1) If f_i(pt) <= 0 for at least one field, assign to the FIRST such field
 *        (lowest index). This preserves user-defined material priority.
 *     2) Otherwise, assign to the field with the MINIMUM evaluated SDF value.
 *        Since SDF magnitude approximates distance to the nearest surface, this
 *        is equivalent to "nearest field" — no mesh.ClosestPoint() needed.
 *
 *   Optional bounding_field trim: keeps points where f <= surfaceTol (inside
 *   or exactly on the zero level set).
 *   If b_box is not provided it is computed from the union of all field domains.
 *
 * Inputs:
 *   - fields        : WasperFields for classification (list, flattened).
 *                     One output branch per field.
 *   - bounding_field: Optional WasperField defining an irregular trim boundary.
 *   - b_box         : Optional bounding box. Auto-computed from field domains if missing.
 *   - grid_spacing  : Spacing between grid nodes. Auto-computed if <= 0.
 *
 * Outputs:
 *   - out_fields     : Input fields passed through.
 *   - out_b_box      : Bounding box used.
 *   - points         : Data tree — one branch per field. EVERY grid point assigned.
 *   - boundary_points: Points near the bounding_field zero level set that neighbour
 *                      an exterior cell. Falls back to bbox-face points when no
 *                      bounding_field is supplied.
 *   - untrimmed_grid : All grid points after optional bounding_field trim.
 *
 * Change log:
 *   v1.0.0 - Initial implementation, SDF-based counterpart of Gp02.
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

namespace WASPer_3DP
{
    public class wsp_Gp04_3DGrid_multi_field : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Gp04_3DGrid_multi_field()
          : base("wsp_Gp04_3D Grid (multi-field)", "3DGrid_fields",
              "Generates a 3D grid and assigns EVERY point to exactly one WasperField branch. " +
              "Assignment: inside first (f ≤ 0), else minimum SDF value (nearest field surface).", global::WASPer_3DP.WASPerPalette.Performance, "6_Grids of points")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v   = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid
            => new Guid("C4E2A871-9F3D-4B6C-B1E7-2D5A8C0F1E93");

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.14_Grid_from_mesh_mult.png"))
                    using (var baseIcon = stream != null ? new System.Drawing.Bitmap(stream) : null)
                        return baseIcon != null ? FieldGridIcon.Create(baseIcon) : null;
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
            pManager.AddGenericParameter("fields", "fields",
                "WasperFields for classification (list, flattened). One output branch per field. " +
                "Assignment: inside first (f ≤ 0), else nearest by minimum SDF value.",
                GH_ParamAccess.list);
            pManager[0].DataMapping = GH_DataMapping.Flatten;

            pManager.AddGenericParameter("bounding_field", "bounding_field",
                "Optional WasperField defining an irregular trim boundary. " +
                "Points where f ≤ surfaceTol are kept.",
                GH_ParamAccess.item);
            pManager[1].Optional = true;

            pManager.AddBoxParameter("b_box", "b_box",
                "Optional bounding box. If invalid or missing, computed from the union of all field domains.",
                GH_ParamAccess.item);
            pManager[2].Optional = true;

            pManager.AddNumberParameter("grid_spacing", "grid_spacing",
                "Distance between adjacent grid points in model units. If missing or <= 0, auto-computed.",
                GH_ParamAccess.item);
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("out_fields", "out_fields",
                "Input WasperFields passed through for reference.", GH_ParamAccess.list);
            pManager.AddBoxParameter("out_b_box", "out_b_box",
                "The bounding box used for generating the grid.", GH_ParamAccess.item);
            pManager.AddPointParameter("points", "points",
                "Data tree of grid points — one branch per field. EVERY grid point is assigned to one branch.",
                GH_ParamAccess.tree);
            pManager.AddPointParameter("boundary_points", "boundary_points",
                "Points near the bounding_field zero level set that neighbour an exterior cell. " +
                "Falls back to bounding-box face points when no bounding_field is supplied.",
                GH_ParamAccess.list);
            pManager.AddPointParameter("untrimmed_grid", "untrimmed_grid",
                "All grid points after optional trimming by bounding_field (order preserved).",
                GH_ParamAccess.list);
        }

        // -----------------------------------------------------------------------
        // Solve
        // -----------------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
            this.Message = _versionTag;

            // --- Inputs ---
            var fieldGoos = new List<IGH_Goo>();
            if (!DA.GetDataList(0, fieldGoos)) return;

            IGH_Goo boundingGoo = null;
            DA.GetData(1, ref boundingGoo);

            Box b_box = new Box();
            DA.GetData(2, ref b_box);

            double grid_spacing = 0.0;
            DA.GetData(3, ref grid_spacing);

            // --- Unwrap fields, warn on wrong types ---
            var fields = new List<WasperField>();
            var badFieldTypes = new System.Collections.Generic.HashSet<string>();
            foreach (var g in fieldGoos)
            {
                if (g == null) continue;
                var f = ExtractField(g);
                if (f != null)
                    fields.Add(f);
                else
                    badFieldTypes.Add(g.TypeName);
            }
            if (badFieldTypes.Count > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"'fields' received non-WasperField input(s): {string.Join(", ", badFieldTypes)}. " +
                    "Connect WasperFields from Fi3d01, In08–In12, or similar. Invalid items are ignored.");
            if (fields.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "No valid WasperField inputs.");
                return;
            }

            WasperField boundingField = ExtractField(boundingGoo);
            if (boundingGoo != null && boundingField == null)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    $"'bounding_field' received a non-WasperField input ({boundingGoo.TypeName}). " +
                    "It will be ignored. Connect a WasperField from Fi3d01, In08–In12, or similar.");

            double tol        = RhinoDoc.ActiveDoc == null ? 0.01 : RhinoDoc.ActiveDoc.ModelAbsoluteTolerance;
            double boundaryTol = 1e-9;
            double surfaceTol  = Math.Max(tol * 2.0, boundaryTol);

            // ------------------------------------------------------------------
            // 1. Build bounding box
            // ------------------------------------------------------------------
            if (!b_box.IsValid)
            {
                BoundingBox bb = BoundingBox.Empty;
                foreach (var f in fields)
                    if (f.Domain.IsValid) bb.Union(f.Domain);
                if (boundingField != null && boundingField.Domain.IsValid)
                    bb.Union(boundingField.Domain);

                if (!bb.IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        "Field domains are invalid. Please provide a b_box.");
                    return;
                }
                b_box = new Box(bb);
            }
            else
            {
                // Wrap rotated boxes into world-axis-aligned bbox for solver stability
                double angTol    = RhinoMath.ToRadians(1.0);
                bool isAligned   = b_box.Plane.ZAxis.IsParallelTo(Vector3d.ZAxis, angTol) != 0 &&
                                   b_box.Plane.XAxis.IsParallelTo(Vector3d.XAxis, angTol) != 0 &&
                                   b_box.Plane.YAxis.IsParallelTo(Vector3d.YAxis, angTol) != 0;
                BoundingBox safeBB = b_box.BoundingBox;
                if (!safeBB.IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input b_box produced an invalid BoundingBox.");
                    return;
                }
                b_box = new Box(safeBB);
                if (!isAligned)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        "Input b_box is rotated. Grid is generated inside its world-axis-aligned BoundingBox.");
            }

            if (!b_box.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Invalid bounding box.");
                return;
            }

            Interval xDom = b_box.X;
            Interval yDom = b_box.Y;
            Interval zDom = b_box.Z;
            double xLen   = xDom.Length;
            double yLen   = yDom.Length;
            double zLen   = zDom.Length;

            if (grid_spacing <= 0.0)
            {
                grid_spacing = AutoGridSpacing(xLen, yLen, zLen, tol);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                    "grid_spacing was missing or <= 0. Auto grid spacing = " + grid_spacing.ToString("0.###") + ".");
            }

            // ------------------------------------------------------------------
            // 2. Generate grid
            // ------------------------------------------------------------------
            int stepsX = (int)Math.Floor(xLen / grid_spacing) + 1;
            int stepsY = (int)Math.Floor(yLen / grid_spacing) + 1;
            int stepsZ = (int)Math.Floor(zLen / grid_spacing) + 1;

            var allPoints = new List<Point3d>(Math.Max(1, stepsX * stepsY * stepsZ));

            for (int i = 0; i < stepsX; i++)
            {
                double xVal = xDom.T0 + i * grid_spacing;
                if (xVal > xDom.T1) xVal = xDom.T1;

                for (int j = 0; j < stepsY; j++)
                {
                    double yVal = yDom.T0 + j * grid_spacing;
                    if (yVal > yDom.T1) yVal = yDom.T1;

                    for (int k = 0; k < stepsZ; k++)
                    {
                        double zVal = zDom.T0 + k * grid_spacing;
                        if (zVal > zDom.T1) zVal = zDom.T1;

                        allPoints.Add(new Point3d(xVal, yVal, zVal));
                    }
                }
            }

            // ------------------------------------------------------------------
            // 3. Optional trim by bounding_field
            // ------------------------------------------------------------------
            List<Point3d> finalGrid;
            bool[] trimKeepMask  = null;
            bool hasBoundingField = boundingField != null;
            const int parallelThreshold = 5000;

            if (hasBoundingField)
            {
                bool[] keep = new bool[allPoints.Count];

                if (allPoints.Count < parallelThreshold)
                {
                    for (int i = 0; i < allPoints.Count; i++)
                        keep[i] = SafeEvaluate(boundingField, allPoints[i]) <= surfaceTol;
                }
                else
                {
                    Parallel.For(0, allPoints.Count, i =>
                        keep[i] = SafeEvaluate(boundingField, allPoints[i]) <= surfaceTol);
                }

                trimKeepMask = keep;
                finalGrid    = new List<Point3d>(allPoints.Count);
                for (int i = 0; i < allPoints.Count; i++)
                    if (keep[i]) finalGrid.Add(allPoints[i]);
            }
            else
            {
                finalGrid = allPoints;
            }

            // ------------------------------------------------------------------
            // 4. Classify with order-safe parallel chunking
            // ------------------------------------------------------------------
            int count     = finalGrid.Count;
            int cpu       = Math.Max(1, Environment.ProcessorCount);
            int chunkSize = Math.Max(2000, count / (cpu * 4));
            int chunkCount = (int)Math.Ceiling(count / (double)chunkSize);

            bool collectBoxBoundary = !hasBoundingField;

            var chunkFieldPts = new List<Point3d>[chunkCount][];
            var chunkBoundary = new List<Point3d>[chunkCount];

            Parallel.For(0, chunkCount, c =>
            {
                int start = c * chunkSize;
                int end   = Math.Min(start + chunkSize, count);

                var localPerField = new List<Point3d>[fields.Count];
                for (int fi = 0; fi < fields.Count; fi++)
                    localPerField[fi] = new List<Point3d>();

                var localBoundary = new List<Point3d>();

                for (int pi = start; pi < end; pi++)
                {
                    Point3d pt = finalGrid[pi];

                    // ── Assignment ───────────────────────────────────────────
                    // Pass 1: find first field with f(pt) <= 0 (inside).
                    int assignedField = -1;
                    for (int fi = 0; fi < fields.Count; fi++)
                    {
                        if (SafeEvaluate(fields[fi], pt) <= 0.0)
                        {
                            assignedField = fi;
                            break;
                        }
                    }

                    // Pass 2: if no field contains pt, assign to the one with
                    // the minimum SDF value (= nearest surface, since SDF ≈ distance).
                    if (assignedField < 0)
                    {
                        double bestVal = double.MaxValue;
                        for (int fi = 0; fi < fields.Count; fi++)
                        {
                            double fVal = SafeEvaluate(fields[fi], pt);
                            if (fVal < bestVal)
                            {
                                bestVal       = fVal;
                                assignedField = fi;
                            }
                        }
                    }

                    if (assignedField < 0) assignedField = 0; // fallback: should not happen
                    localPerField[assignedField].Add(pt);

                    // ── Bbox-face boundary (only when no bounding_field) ─────
                    if (collectBoxBoundary &&
                        (Math.Abs(pt.X - xDom.T0) < boundaryTol || Math.Abs(pt.X - xDom.T1) < boundaryTol ||
                         Math.Abs(pt.Y - yDom.T0) < boundaryTol || Math.Abs(pt.Y - yDom.T1) < boundaryTol ||
                         Math.Abs(pt.Z - zDom.T0) < boundaryTol || Math.Abs(pt.Z - zDom.T1) < boundaryTol))
                    {
                        localBoundary.Add(pt);
                    }
                }

                chunkFieldPts[c] = localPerField;
                chunkBoundary[c] = localBoundary;
            });

            // Merge chunks preserving order within each branch
            var perFieldPts = new List<Point3d>[fields.Count];
            for (int fi = 0; fi < fields.Count; fi++)
                perFieldPts[fi] = new List<Point3d>();

            var boundaryPts = new List<Point3d>();

            for (int c = 0; c < chunkCount; c++)
            {
                if (chunkFieldPts[c] != null)
                    for (int fi = 0; fi < fields.Count; fi++)
                        if (chunkFieldPts[c][fi].Count > 0)
                            perFieldPts[fi].AddRange(chunkFieldPts[c][fi]);

                if (chunkBoundary[c]?.Count > 0)
                    boundaryPts.AddRange(chunkBoundary[c]);
            }

            // ------------------------------------------------------------------
            // 5. Boundary points for SDF-trimmed grids
            // ------------------------------------------------------------------
            if (hasBoundingField && trimKeepMask != null)
            {
                double boundaryBand = 0.5 * Math.Sqrt(
                    grid_spacing * grid_spacing * 3.0) + surfaceTol;

                boundaryPts = BuildFieldBoundaryPoints(
                    allPoints, trimKeepMask,
                    stepsX, stepsY, stepsZ,
                    boundingField, boundaryBand);
            }

            // ------------------------------------------------------------------
            // 6. Build GH data tree
            // ------------------------------------------------------------------
            var pointTree = new GH_Structure<GH_Point>();
            for (int fi = 0; fi < fields.Count; fi++)
            {
                var path = new GH_Path(fi);
                foreach (var p in perFieldPts[fi])
                    pointTree.Append(new GH_Point(p), path);
            }

            // ------------------------------------------------------------------
            // 7. Outputs
            // ------------------------------------------------------------------
            var outGoos = new List<WasperFieldGoo>(fields.Count);
            foreach (var f in fields) outGoos.Add(new WasperFieldGoo(f));

            DA.SetDataList(0, outGoos);
            DA.SetData(1, b_box);
            DA.SetDataTree(2, pointTree);
            DA.SetDataList(3, boundaryPts);
            DA.SetDataList(4, finalGrid);
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Returns grid points inside the bounding_field that neighbour an exterior
        /// cell AND are within <paramref name="boundaryBand"/> of the zero level set.
        /// </summary>
        private static List<Point3d> BuildFieldBoundaryPoints(
            List<Point3d> allPoints,
            bool[] keepMask,
            int nxCount, int nyCount, int nzCount,
            WasperField boundingField,
            double boundaryBand)
        {
            var result = new List<Point3d>();
            if (allPoints == null || keepMask == null || boundingField == null)
                return result;

            var    seen   = new HashSet<string>();
            double keyTol = Math.Max(boundaryBand * 0.01, 1e-9);

            for (int ix = 0; ix < nxCount; ix++)
            for (int iy = 0; iy < nyCount; iy++)
            for (int iz = 0; iz < nzCount; iz++)
            {
                int index = GridIndex(ix, iy, iz, nyCount, nzCount);
                if (index < 0 || index >= keepMask.Length) continue;
                if (!keepMask[index]) continue;
                if (!HasExteriorNeighbor(keepMask, ix, iy, iz, nxCount, nyCount, nzCount)) continue;

                Point3d pt   = allPoints[index];
                double  fVal = SafeEvaluate(boundingField, pt);
                if (Math.Abs(fVal) > boundaryBand) continue;

                string key = QuantizedKey(pt, keyTol);
                if (seen.Add(key)) result.Add(pt);
            }

            return result;
        }

        private static bool HasExteriorNeighbor(
            bool[] keepMask,
            int ix, int iy, int iz,
            int nxCount, int nyCount, int nzCount)
        {
            if (ix == 0 || ix == nxCount - 1 ||
                iy == 0 || iy == nyCount - 1 ||
                iz == 0 || iz == nzCount - 1)
                return true;

            return !keepMask[GridIndex(ix - 1, iy,     iz,     nyCount, nzCount)] ||
                   !keepMask[GridIndex(ix + 1, iy,     iz,     nyCount, nzCount)] ||
                   !keepMask[GridIndex(ix,     iy - 1, iz,     nyCount, nzCount)] ||
                   !keepMask[GridIndex(ix,     iy + 1, iz,     nyCount, nzCount)] ||
                   !keepMask[GridIndex(ix,     iy,     iz - 1, nyCount, nzCount)] ||
                   !keepMask[GridIndex(ix,     iy,     iz + 1, nyCount, nzCount)];
        }

        private static int GridIndex(int ix, int iy, int iz, int nyCount, int nzCount)
            => ((ix * nyCount) + iy) * nzCount + iz;

        private static double AutoGridSpacing(double xLen, double yLen, double zLen, double tol)
        {
            double maxLen = Math.Max(Math.Abs(xLen), Math.Max(Math.Abs(yLen), Math.Abs(zLen)));
            if (maxLen <= RhinoMath.ZeroTolerance)
                return Math.Max(tol * 10.0, 1.0);
            return Math.Max(maxLen / 30.0, Math.Max(tol * 10.0, RhinoMath.ZeroTolerance));
        }

        private static double SafeEvaluate(WasperField field, Point3d pt)
        {
            try
            {
                double v = field.Evaluate(pt);
                return (double.IsNaN(v) || double.IsInfinity(v)) ? double.PositiveInfinity : v;
            }
            catch { return double.PositiveInfinity; }
        }

        private static WasperField ExtractField(IGH_Goo goo)
        {
            if (goo == null) return null;
            if (goo is WasperFieldGoo fg) return fg.Value;
            object val = null;
            if (goo is GH_ObjectWrapper ow) val = ow.Value;
            if (val is WasperField wf)    return wf;
            if (val is WasperFieldGoo wg)  return wg.Value;
            return null;
        }

        private static string QuantizedKey(Point3d pt, double tol)
        {
            long x = (long)Math.Round(pt.X / tol);
            long y = (long)Math.Round(pt.Y / tol);
            long z = (long)Math.Round(pt.Z / tol);
            return x + "|" + y + "|" + z;
        }
    }
}
