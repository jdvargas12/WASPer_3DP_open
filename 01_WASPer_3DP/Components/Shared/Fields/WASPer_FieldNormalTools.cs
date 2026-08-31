// WASPer_FieldNormalTools.cs
// Shared mesh-normal cleanup helpers for WasperField-based meshes.
//
// Performance notes (2026-05-16):
//   Old approach: full central-difference gradient = 6 field evaluations per face,
//   single-threaded.  With the double-call pattern used by callers this meant
//   12 evaluations × face count, all on one thread → ~8× slowdown on large meshes.
//
//   New approach:
//     1. Directional derivative along the face normal (2 evaluations per face, not 6).
//        We only need the SIGN of ∇f · n̂, not the full gradient vector.
//        fPos - fNeg > 0  →  gradient already follows normal  →  no flip.
//        fPos - fNeg < 0  →  gradient opposes normal          →  flip.
//     2. Parallel.For over faces: gradient queries are embarrassingly parallel
//        (WasperField.Evaluate is stateless / read-only).
//        Flip flags are collected in a bool[] and applied single-threaded
//        because Mesh is not thread-safe for writes.
//
//   Net improvement: ~3× (fewer evaluations) × ~4-8× (CPU cores) ≈ 12-24× faster,
//   more than recovering the original overhead.

using System;
using System.Threading.Tasks;
using Rhino.Geometry;

namespace WASPer_3DP
{
    internal static class WasperFieldNormalTools
    {
        /// <summary>
        /// Reorients individual mesh faces so their normals follow the positive SDF gradient.
        /// WASPer fields use negative = material/inside and positive = outside, so the
        /// gradient points outward from the extracted material surface.
        /// </summary>
        public static void OrientFacesByFieldGradient(Mesh mesh, WasperField field, double gradientStep)
        {
            if (mesh == null || mesh.Faces.Count == 0) return;
            if (field == null || field.Evaluator == null) return;

            double h = Math.Max(gradientStep, 1e-6);
            int faceCount = mesh.Faces.Count;

            mesh.FaceNormals.ComputeFaceNormals();

            // Snapshot vertex positions into a plain array so the parallel loop
            // reads from a thread-safe structure instead of the Mesh object.
            Point3d[] verts = mesh.Vertices.ToPoint3dArray();

            // Snapshot face normals into a plain array for the same reason.
            var faceNormals = new Vector3d[faceCount];
            for (int i = 0; i < faceCount; i++)
                faceNormals[i] = mesh.FaceNormals[i];

            // --- Parallel phase: compute flip flags ---
            // Each face needs 2 field evaluations (directional derivative along its
            // normal) instead of the old 6 (full central-difference gradient).
            var flipFace = new bool[faceCount];

            Parallel.For(0, faceCount, i =>
            {
                Vector3d n = faceNormals[i];
                if (!n.IsValid || n.IsZero) return;
                n.Unitize();

                MeshFace face = mesh.Faces[i];
                Point3d center = FaceCenter(verts, face);

                // Directional derivative: sample field just ahead and just behind
                // the face center along its normal.
                double fPos = SafeEvaluate(field, center + n * h);
                double fNeg = SafeEvaluate(field, center - n * h);

                if (!IsFinite(fPos) || !IsFinite(fNeg)) return;

                // fPos > fNeg  →  field increases along normal  →  normal points outward  →  OK.
                // fPos < fNeg  →  field decreases along normal  →  normal points inward   →  flip.
                if (fPos < fNeg)
                    flipFace[i] = true;
            });

            // --- Serial phase: apply face flips (Mesh writes are not thread-safe) ---
            for (int i = 0; i < faceCount; i++)
            {
                if (!flipFace[i]) continue;

                MeshFace face = mesh.Faces[i];
                if (face.IsQuad)
                    mesh.Faces.SetFace(i, face.A, face.D, face.C, face.B);
                else
                    mesh.Faces.SetFace(i, face.A, face.C, face.B);
            }

            mesh.FaceNormals.ComputeFaceNormals();
            mesh.Normals.ComputeNormals();
        }

        // Uses a pre-extracted vertex array to avoid Mesh property access in parallel loops.
        private static Point3d FaceCenter(Point3d[] verts, MeshFace face)
        {
            Point3d a = verts[face.A];
            Point3d b = verts[face.B];
            Point3d c = verts[face.C];

            if (!face.IsQuad)
            {
                return new Point3d(
                    (a.X + b.X + c.X) / 3.0,
                    (a.Y + b.Y + c.Y) / 3.0,
                    (a.Z + b.Z + c.Z) / 3.0);
            }

            Point3d d = verts[face.D];
            return new Point3d(
                (a.X + b.X + c.X + d.X) * 0.25,
                (a.Y + b.Y + c.Y + d.Y) * 0.25,
                (a.Z + b.Z + c.Z + d.Z) * 0.25);
        }

        private static double SafeEvaluate(WasperField field, Point3d point)
        {
            double value;
            try { value = field.Evaluate(point); }
            catch { return double.PositiveInfinity; }

            return IsFinite(value) ? value : double.PositiveInfinity;
        }

        private static bool IsFinite(double value)
        {
            return !(double.IsNaN(value) || double.IsInfinity(value));
        }
    }
}
