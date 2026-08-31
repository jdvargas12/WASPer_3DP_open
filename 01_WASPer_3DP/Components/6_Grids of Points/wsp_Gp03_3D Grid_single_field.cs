#region Component Explanation
/*
 * Component Name: wsp_Gp03_3D Grid (single-field)
 * NickName: 3DGrid_field
 *
 * Description:
 *   SDF-based counterpart of Gp01. Generates a strict 3D grid and classifies
 *   every point using WasperField evaluation instead of mesh ray-casting.
 *
 *   Convention: f(pt) <= 0  →  inside (material); f(pt) > 0  →  outside.
 *
 *   Grid can optionally be trimmed by a bounding_field (keep points where
 *   f <= surfaceTol so points exactly on the zero level set are not lost).
 *   If b_box is not provided it is computed from the union of all field domains.
 *
 * Advantages over Gp01:
 *   - Classification is O(1) per point (analytic evaluation) vs O(faces) ray-cast.
 *   - Bounding trim requires no closest-point mesh query.
 *   - Boundary detection uses the SDF iso-band (|f| <= band) instead of
 *     projecting grid points onto a mesh surface.
 *   - All evaluations are thread-safe → Parallel.For is fully exploited.
 *
 * Inputs:
 *   - field        : One or more WasperFields for classification (list, flattened).
 *                    Points where ANY field evaluates ≤ 0 → points_1.
 *   - bounding_field: Optional WasperField defining an irregular trim boundary.
 *                    Points where f <= surfaceTol are kept.
 *   - b_box        : Optional bounding box. If invalid, computed from field domains.
 *   - grid_spacing : Distance between adjacent grid points. Auto-computed if <= 0.
 *
 * Outputs:
 *   - out_field      : Input fields passed through.
 *   - out_b_box      : Bounding box used.
 *   - points_1       : Points inside ANY field (f <= 0).
 *   - points_2       : Points not inside any field.
 *   - boundary_points: Points near the bounding_field zero level set (|f| <= band)
 *                      that neighbour an exterior cell. Falls back to bbox-face
 *                      boundary points when no bounding_field is supplied.
 *   - untrimmed_grid : All grid points (after optional bounding_field trim).
 *
 * Change log:
 *   v1.0.0 - Initial implementation, SDF-based counterpart of Gp01.
 */
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Rhino;
using Rhino.Geometry;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;
#endregion

namespace WASPer_3DP
{
    public class wsp_Gp03_3DGrid_single_field : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Gp03_3DGrid_single_field()
          : base("wsp_Gp03_3D Grid (single-field)", "3DGrid_field",
              "Generates a 3D grid of points within a bounding box and classifies them using WasperField SDF evaluation. " +
              "Points inside ANY input field (f ≤ 0) → points_1; rest → points_2.", global::WASPer_3DP.WASPerPalette.Performance, "6_Grids of points")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v   = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid
            => new Guid("B7D1F4A2-3C8E-4F5B-A9D2-1E6C7B0F3A82");

        protected override System.Drawing.Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.13_Grid_from_mesh.png"))
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
            pManager.AddGenericParameter("field", "field",
                "One or more WasperFields for classification (list, flattened). " +
                "Points where ANY field evaluates ≤ 0 go to points_1; all others to points_2.",
                GH_ParamAccess.list);
            pManager[0].DataMapping = GH_DataMapping.Flatten;

            pManager.AddGenericParameter("bounding_field", "bounding_field",
                "Optional WasperField defining an irregular trim boundary. " +
                "Points where f ≤ surfaceTol are kept. The zero level set of this field defines boundary_points.",
                GH_ParamAccess.item);
            pManager[1].Optional = true;

            pManager.AddBoxParameter("b_box", "b_box",
                "Optional bounding box. If invalid or missing, computed from the union of all field domains.",
                GH_ParamAccess.item);
            pManager[2].Optional = true;

            pManager.AddNumberParameter("grid_spacing", "grid_spacing",
                "Distance between adjacent grid points in model units. If missing or <= 0, auto-computed from the bounding box size.",
                GH_ParamAccess.item);
            pManager[3].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddGenericParameter("out_field", "out_field",
                "The input WasperField(s) passed through for reference.", GH_ParamAccess.list);
            pManager.AddBoxParameter("out_b_box", "out_b_box",
                "The bounding box used for generating the grid.", GH_ParamAccess.item);
            pManager.AddPointParameter("points_1", "points_1",
                "Grid points inside ANY of the input fields (f ≤ 0).", GH_ParamAccess.list);
            pManager.AddPointParameter("points_2", "points_2",
                "Grid points not inside any input field.", GH_ParamAccess.list);
            pManager.AddPointParameter("boundary_points", "boundary_points",
                "Points near the bounding_field zero level set (|f| ≤ band) that neighbour an exterior cell. " +
                "Falls back to bounding-box face points when no bounding_field is supplied.",
                GH_ParamAccess.list);
            pManager.AddPointParameter("untrimmed_grid", "untrimmed_grid",
                "All generated grid points (after trimming by bounding_field, if provided).",
                GH_ParamAccess.list);
        }

        // -----------------------------------------------------------------------
        // Solve
        // -----------------------------------------------------------------------
        protected override void SolveInstance(IGH_DataAccess DA)
        {
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
                    $"'field' received non-WasperField input(s): {string.Join(", ", badFieldTypes)}. " +
                    "Connect a WasperField from Fi3d01, In08–In12, or similar. Invalid items are ignored.");
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

            // --- Build bounding box from field domains if not provided ---
            if (!b_box.IsValid)
            {
                BoundingBox unionBB = BoundingBox.Empty;
                foreach (var f in fields)
                    if (f.Domain.IsValid) unionBB.Union(f.Domain);
                if (boundingField != null && boundingField.Domain.IsValid)
                    unionBB.Union(boundingField.Domain);

                if (!unionBB.IsValid)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        "Field domains are invalid. Please provide a b_box.");
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

            const double boundaryTol = 1e-6;
            // surfaceTol: accept points that are right on the SDF zero level set
            double surfaceTol = Math.Max(tol * 2.0, boundaryTol);

            const int parallelThreshold = 5000;

            // ------------------------------------------------------------------
            // 1. Generate all grid points
            // ------------------------------------------------------------------
            int nx = (int)Math.Floor(xLen / grid_spacing) + 1;
            int ny = (int)Math.Floor(yLen / grid_spacing) + 1;
            int nz = (int)Math.Floor(zLen / grid_spacing) + 1;

            var allPoints = new List<Point3d>(nx * ny * nz);

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
            // 2. Trim by bounding_field (keep where f <= surfaceTol)
            // ------------------------------------------------------------------
            List<Point3d> finalGrid;
            bool[] trimKeepMask = null;
            bool hasBoundingField = boundingField != null;

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
                finalGrid = new List<Point3d>(allPoints.Count);
                for (int i = 0; i < allPoints.Count; i++)
                    if (keep[i]) finalGrid.Add(allPoints[i]);
            }
            else
            {
                finalGrid = allPoints;
            }

            // ------------------------------------------------------------------
            // 3. Classify: inside ANY field (f <= 0) → points_1, rest → points_2
            //    Bbox-face boundary collected here when no bounding_field.
            // ------------------------------------------------------------------
            var insidePts   = new List<Point3d>(finalGrid.Count / 2);
            var outsidePts  = new List<Point3d>(finalGrid.Count / 2);
            var boundaryPts = new List<Point3d>();

            bool collectBoxBoundary = !hasBoundingField;

            if (finalGrid.Count < parallelThreshold)
            {
                for (int i = 0; i < finalGrid.Count; i++)
                    ClassifyPoint(finalGrid[i], fields,
                                  xDom, yDom, zDom, boundaryTol,
                                  collectBoxBoundary,
                                  insidePts, outsidePts, boundaryPts);
            }
            else
            {
                int chunkSize = Math.Max(1, finalGrid.Count / (Environment.ProcessorCount * 4));
                int numChunks = (int)Math.Ceiling((double)finalGrid.Count / chunkSize);

                var localIn  = new List<Point3d>[numChunks];
                var localOut = new List<Point3d>[numChunks];
                var localBnd = new List<Point3d>[numChunks];

                Parallel.For(0, numChunks, chunk =>
                {
                    int start = chunk * chunkSize;
                    int end   = Math.Min(start + chunkSize, finalGrid.Count);

                    var lIn  = new List<Point3d>();
                    var lOut = new List<Point3d>();
                    var lBnd = new List<Point3d>();

                    for (int i = start; i < end; i++)
                        ClassifyPoint(finalGrid[i], fields,
                                      xDom, yDom, zDom, boundaryTol,
                                      collectBoxBoundary,
                                      lIn, lOut, lBnd);

                    localIn[chunk]  = lIn;
                    localOut[chunk] = lOut;
                    localBnd[chunk] = lBnd;
                });

                for (int c = 0; c < numChunks; c++)
                {
                    if (localIn[c]?.Count  > 0) insidePts.AddRange(localIn[c]);
                    if (localOut[c]?.Count > 0) outsidePts.AddRange(localOut[c]);
                    if (localBnd[c]?.Count > 0) boundaryPts.AddRange(localBnd[c]);
                }
            }

            // ------------------------------------------------------------------
            // 4. Boundary points for SDF-trimmed grids:
            //    Grid nodes near the zero level set (|f| <= band) that also
            //    neighbour at least one exterior cell in the keepMask.
            // ------------------------------------------------------------------
            if (hasBoundingField && trimKeepMask != null)
            {
                double boundaryBand = 0.5 * Math.Sqrt(
                    grid_spacing * grid_spacing * 3.0) + surfaceTol;

                boundaryPts = BuildFieldBoundaryPoints(
                    allPoints, trimKeepMask,
                    nx, ny, nz,
                    boundingField, boundaryBand);
            }

            // ------------------------------------------------------------------
            // 5. Outputs
            // ------------------------------------------------------------------
            var outGoos = new List<WasperFieldGoo>(fields.Count);
            foreach (var f in fields) outGoos.Add(new WasperFieldGoo(f));

            DA.SetDataList(0, outGoos);
            DA.SetData(1, b_box);
            DA.SetDataList(2, insidePts);
            DA.SetDataList(3, outsidePts);
            DA.SetDataList(4, boundaryPts);
            DA.SetDataList(5, finalGrid);
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Classify a grid point.
        ///   f(pt) ≤ 0 for ANY field  → inside list
        ///   otherwise                → outside list
        ///   on any bbox face          → boundary list (only when collectBoxBoundary)
        /// </summary>
        private static void ClassifyPoint(
            Point3d pt,
            List<WasperField> fields,
            Interval xDom, Interval yDom, Interval zDom,
            double boundaryTol,
            bool collectBoxBoundary,
            List<Point3d> inside, List<Point3d> outside, List<Point3d> boundary)
        {
            bool isInsideAny = false;
            for (int i = 0; i < fields.Count; i++)
            {
                if (SafeEvaluate(fields[i], pt) <= 0.0)
                {
                    isInsideAny = true;
                    break;
                }
            }

            if (isInsideAny) inside.Add(pt);
            else             outside.Add(pt);

            if (collectBoxBoundary &&
                (Math.Abs(pt.X - xDom.T0) <= boundaryTol || Math.Abs(pt.X - xDom.T1) <= boundaryTol ||
                 Math.Abs(pt.Y - yDom.T0) <= boundaryTol || Math.Abs(pt.Y - yDom.T1) <= boundaryTol ||
                 Math.Abs(pt.Z - zDom.T0) <= boundaryTol || Math.Abs(pt.Z - zDom.T1) <= boundaryTol))
            {
                boundary.Add(pt);
            }
        }

        /// <summary>
        /// Returns grid points that:
        ///   1. Are inside the bounding field (keepMask[i] = true), AND
        ///   2. Neighbour at least one exterior cell, AND
        ///   3. Are within <paramref name="boundaryBand"/> of the zero level set.
        /// No mesh projection needed — the SDF already gives the approximate distance.
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

            var seen = new HashSet<string>();
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
            if (val is WasperField wf)   return wf;
            if (val is WasperFieldGoo wg) return wg.Value;
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
