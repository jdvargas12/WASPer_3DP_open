using System;
using System.Collections.Generic;

using Grasshopper;
using Grasshopper.Kernel.Data;

using Rhino.Geometry;

using WASPer.LiveLink;

namespace WASPer_3DP.Components.Shared.LiveLink
{
    /// <summary>
    /// Streams a <see cref="WasperPrintPath"/> straight to the live link.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the primary path into Gamma, and it is deliberately not a
    /// conversion to generic geometry. A wsp_path already is a polyline tree with
    /// per-branch semantics — role, stroke continuity, bead width, layer height —
    /// and flattening it into anonymous curves would throw all of that away and
    /// force the viewer to re-derive it, or more likely to render every path
    /// identically.
    /// </para>
    /// <para>
    /// Branch paths are preserved exactly, and the PathAttributes block is written
    /// with the same paths as the polyline block, so a receiver zips the two by
    /// branch path with no positional assumptions.
    /// </para>
    /// <para>
    /// Geometry comes from <c>Points</c>, the compatibility projection of the
    /// PtPlanes origins, because the live link is a display transport. Anything
    /// carrying fabrication semantics — motion order, timing, roles as process
    /// data — stays in the .wasperxr package, which remains authoritative.
    /// </para>
    /// </remarks>
    internal static class WasperLiveLinkPathAdapter
    {
        public const double Absent = -1.0;

        /// <summary>
        /// Appends every branch of a print path. Returns the number of polyline
        /// branches written.
        /// </summary>
        public static int Append(WasperLiveFrameBuilder builder, WasperPrintPath path, double tolerance)
        {
            if (builder == null) throw new ArgumentNullException(nameof(builder));
            if (path == null || !path.HasPoints) return 0;

            DataTree<Point3d> points = path.Points;
            int written = 0;

            double closeTolerance = tolerance > 0.0 ? tolerance : 1e-6;

            // Same factor the builder applies to coordinates. Bead width and layer
            // height are lengths too, just carried as attributes rather than as
            // geometry, and a 6 mm bead left unscaled becomes a 6 metre one.
            double lengthScale = builder.GeometryScale;

            for (int b = 0; b < points.BranchCount; b++)
            {
                List<Point3d> branch = points.Branches[b];
                if (branch == null || branch.Count == 0) continue;

                GH_Path ghPath = points.Paths[b];
                int[] indices = ghPath?.Indices ?? Array.Empty<int>();

                if (branch.Count == 1)
                {
                    // A single deposited location is still worth seeing.
                    builder.AddPoint(indices, branch[0].X, branch[0].Y, branch[0].Z);
                    continue;
                }

                var flat = new double[branch.Count * 3];
                for (int i = 0; i < branch.Count; i++)
                {
                    flat[i * 3 + 0] = branch[i].X;
                    flat[i * 3 + 1] = branch[i].Y;
                    flat[i * 3 + 2] = branch[i].Z;
                }

                bool closed = branch.Count > 2 &&
                              branch[0].DistanceTo(branch[branch.Count - 1]) <= closeTolerance;

                builder.AddPolyline(indices, flat, closed);

                // Per-point section and orientation. LayerH, LayerW and LayerWf are
                // per point, not per branch, and PtPlanes carries the layer frame at
                // every point — which is the only way a non-planar layer can be
                // rendered with the bead standing on the layer rather than on world Z.
                double[] widths = PointValues(
                    path.HasLayerWf ? path.LayerWf : (path.HasLayerW ? path.LayerW : null),
                    ghPath, b, branch.Count);
                double[] heights = PointValues(
                    path.HasLayerH ? path.LayerH : null, ghPath, b, branch.Count);
                double[] speeds = PointValues(
                    path.HasPrintSpeed ? path.PrintSpeed : null, ghPath, b, branch.Count);

                builder.AddPathPointSection(indices, widths, heights, speeds);

                double[] normals = PointNormals(path, ghPath, b, branch.Count);
                if (normals != null) builder.AddPathPointNormals(indices, normals);

                builder.AddPathAttributes(
                    indices,
                    FirstValue(path.HasPathRoles ? path.PathRoles : null, ghPath, b, Absent),
                    FirstValue(path.HasStrokeIds ? path.StrokeIds : null, ghPath, b, Absent),
                    Scaled(FirstValue(path.HasLayerH ? path.LayerH : null, ghPath, b, Absent), lengthScale),
                    Scaled(ResolveWidth(path, ghPath, b), lengthScale),
                    MeanValue(path.HasPrintSpeed ? path.PrintSpeed : null, ghPath, b, Absent),
                    closed);

                written++;
            }

            if (!string.IsNullOrEmpty(path.ContentSignature))
                builder.AddPathMeta(new[] { 0 }, path.ContentSignature);

            return written;
        }

        /// <summary>
        /// One value per path point, padded with the last known value and filled with
        /// -1 when the tree is absent. Trees shorter than the point count are common
        /// where a quantity is per segment rather than per point.
        /// </summary>
        private static double[] PointValues(DataTree<double> tree, GH_Path ghPath, int index, int count)
        {
            var values = new double[count];

            List<double> branch = ResolveBranch(tree, ghPath, index);
            if (branch == null || branch.Count == 0)
            {
                for (int i = 0; i < count; i++) values[i] = Absent;
                return values;
            }

            for (int i = 0; i < count; i++)
                values[i] = i < branch.Count ? branch[i] : branch[branch.Count - 1];

            return values;
        }

        /// <summary>
        /// Layer normal per point, from the PtPlanes Z axis. Null when the path
        /// carries no planes, in which case the viewer falls back to world Z.
        /// </summary>
        private static double[] PointNormals(WasperPrintPath path, GH_Path ghPath, int index, int count)
        {
            if (!path.HasPlanes) return null;

            List<Plane> branch = ResolveBranch(path.PtPlanes, ghPath, index);
            if (branch == null || branch.Count == 0) return null;

            var normals = new double[count * 3];

            for (int i = 0; i < count; i++)
            {
                Plane plane = i < branch.Count ? branch[i] : branch[branch.Count - 1];
                Vector3d z = plane.ZAxis;

                if (!z.IsValid || z.IsZero) z = Vector3d.ZAxis;
                else z.Unitize();

                normals[i * 3 + 0] = z.X;
                normals[i * 3 + 1] = z.Y;
                normals[i * 3 + 2] = z.Z;
            }

            return normals;
        }

        /// <summary>Scales a length, leaving the -1 absent marker alone.</summary>
        private static double Scaled(double value, double scale)
        {
            return value > 0.0 ? value * scale : value;
        }

        /// <summary>
        /// Flow-adjusted deposited width when the pipeline produced it, otherwise
        /// the nominal width. A viewer drawing beads wants what was actually
        /// deposited, not what was nominally requested.
        /// </summary>
        private static double ResolveWidth(WasperPrintPath path, GH_Path ghPath, int index)
        {
            if (path.HasLayerWf)
            {
                double adjusted = FirstValue(path.LayerWf, ghPath, index, Absent);
                if (adjusted > 0.0) return adjusted;
            }

            if (path.HasLayerW)
            {
                double nominal = FirstValue(path.LayerW, ghPath, index, Absent);
                if (nominal > 0.0) return nominal;
            }

            return Absent;
        }

        private static double FirstValue(DataTree<double> tree, GH_Path ghPath, int index, double fallback)
        {
            List<double> branch = ResolveBranch(tree, ghPath, index);
            return branch == null || branch.Count == 0 ? fallback : branch[0];
        }

        private static double FirstValue(DataTree<int> tree, GH_Path ghPath, int index, double fallback)
        {
            List<int> branch = ResolveBranch(tree, ghPath, index);
            return branch == null || branch.Count == 0 ? fallback : branch[0];
        }

        private static double MeanValue(DataTree<double> tree, GH_Path ghPath, int index, double fallback)
        {
            List<double> branch = ResolveBranch(tree, ghPath, index);
            if (branch == null || branch.Count == 0) return fallback;

            double total = 0.0;
            int counted = 0;
            for (int i = 0; i < branch.Count; i++)
            {
                if (double.IsNaN(branch[i])) continue;
                total += branch[i];
                counted++;
            }

            return counted == 0 ? fallback : total / counted;
        }

        /// <summary>
        /// Optional trees are branch-aligned by contract, so the index is the fast
        /// path. Matching by path is the fallback for trees that were filtered or
        /// rebuilt upstream and no longer line up positionally.
        /// </summary>
        private static List<T> ResolveBranch<T>(DataTree<T> tree, GH_Path ghPath, int index)
        {
            if (tree == null || tree.BranchCount == 0) return null;

            if (ghPath != null)
            {
                List<T> byPath = tree.Branch(ghPath);
                if (byPath != null && byPath.Count > 0) return byPath;
            }

            if (index >= 0 && index < tree.BranchCount) return tree.Branches[index];

            return null;
        }
    }
}
