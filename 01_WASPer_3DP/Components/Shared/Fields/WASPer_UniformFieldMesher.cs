// WASPer_UniformFieldMesher.cs
// Shared memory-bounded uniform-grid marching-cubes mesher for WasperField.
//
// This wraps the existing WasperMarchingCubes.Extract(...) (unchanged - see
// WASPer_MarchingCubesExtractor.cs) with Z-slab sampling so that no caller
// ever has to allocate a scalar/point array sized to the *entire* domain
// grid at once. Each slab is sampled and extracted independently and the
// resulting slab meshes are appended together; adjacent slabs share one
// boundary Z-layer (the previous slab's last layer is re-sampled as the
// next slab's first layer) so the seam between them closes cleanly once the
// final CombineIdentical pass runs - both slabs evaluate the exact same
// (u, v, w) parameter for that shared layer, so its sample points are
// bit-identical and merge without gaps or duplicate skins.
//
// Note on scope: the plan that motivated this file also describes storing
// grid coordinates "implicitly" (computing each Point3d on demand instead
// of materializing a points[] array) to shrink per-sample memory further.
// That is not done here because WasperMarchingCubes.Extract's signature
// takes an explicit Point3d[] and is intentionally left unchanged (shared
// by non-Dendro callers), so avoiding the points[] allocation would require
// changing that shared extractor. Slab bounding already keeps peak memory
// proportional to one slab (nx * ny * slabDepth), not the whole grid, which
// is the actual unbounded-memory problem this file exists to fix.
//
// Dendro-independence: nothing in this file references Dendro types, directly
// or via reflection. It is pure WasperField/Mesh infrastructure shared by any
// caller (Fi3d10, Fi3d15, and future components) with or without Dendro loaded.

using System;
using System.Threading.Tasks;

using Rhino.Geometry;

namespace WASPer_3DP
{
    /// <summary>Options controlling a single WasperUniformFieldMesher.TryExtract call.</summary>
    internal sealed class WasperUniformFieldMeshOptions
    {
        /// <summary>Sampling resolution (grid spacing) in model units.</summary>
        public double Resolution = 1.0;

        /// <summary>Iso-level to extract. WASPer fields use 0 = surface, negative = inside.</summary>
        public double IsoLevel = 0.0;

        /// <summary>Vertex-welding key tolerance passed through to WasperMarchingCubes.Extract.</summary>
        public double KeyTolerance = 1e-6;

        /// <summary>Number of Z grid layers held in memory at once (bounds peak sampling memory).</summary>
        public int SlabDepth = 24;

        /// <summary>Forwarded to WasperMarchingCubes.Extract per slab. 0 = let it choose.</summary>
        public int MaxDegreeOfParallelism = 0;

        /// <summary>
        /// Forces samples on all six faces of the grid domain to the outside of the iso-surface.
        /// Use this when the finite domain is also intended to clip/seal an otherwise unbounded field.
        /// </summary>
        public bool SealDomainBoundary = false;

        /// <summary>Hard cap on total grid samples (nx*ny*nz) across the whole domain, regardless of slabbing.</summary>
        public long HardMaxSamples = 20_000_000;

        /// <summary>Soft memory budget per slab, in bytes. SlabDepth is reduced automatically to try to fit under this.</summary>
        public long WarningManagedBytes = 256L * 1024 * 1024;

        /// <summary>Hard memory budget per slab, in bytes. SlabDepth is reduced (down to a minimum of 2) to try to fit under this.</summary>
        public long HardManagedBytes = 512L * 1024 * 1024;
    }

    /// <summary>Diagnostics returned alongside a successful WasperUniformFieldMesher.TryExtract call.</summary>
    internal sealed class WasperUniformFieldMeshStats
    {
        public int Nx;
        public int Ny;
        public int Nz;
        public long Samples;
        public int SlabCount;
        public int SlabDepthUsed;
        public long SampleMs;
        public long MeshMs;
        public long EstimatedPeakManagedBytes;
        public bool MemoryBudgetExceeded;
    }

    /// <summary>
    /// Memory-bounded uniform-grid marching-cubes extraction for WasperField, shared by every
    /// component that meshes a field's zero iso-surface on a box domain (Isopod bridge, Dendro
    /// bridge, and any future caller). Delegates the actual cube-marching to the existing
    /// WasperMarchingCubes.Extract(...) one Z-slab at a time.
    /// </summary>
    internal static class WasperUniformFieldMesher
    {
        /// <summary>Computes grid sample counts for a box domain at a given resolution. Matches the
        /// convention used across WASPer's field-meshing components: ceil(length / resolution) + 1,
        /// clamped to a minimum of 2 per axis.</summary>
        internal static void BuildGridCounts(Box box, double resolution, out int nx, out int ny, out int nz)
        {
            double r = Math.Max(resolution, 1e-9);
            nx = Math.Max(2, (int)Math.Ceiling(Math.Abs(box.X.Length) / r) + 1);
            ny = Math.Max(2, (int)Math.Ceiling(Math.Abs(box.Y.Length) / r) + 1);
            nz = Math.Max(2, (int)Math.Ceiling(Math.Abs(box.Z.Length) / r) + 1);
        }

        /// <summary>Estimated managed bytes for `samples` grid points: one Point3d (24 bytes) plus one
        /// double scalar (8 bytes) per sample, plus a small fixed allowance for array headers.</summary>
        internal static long EstimateGridManagedBytes(long samples)
        {
            if (samples <= 0) return 64L;
            return samples * 32L + 64L;
        }

        /// <summary>
        /// Suggests a coarser resolution that would bring a box domain's total sample count under
        /// `sampleCap`, using the domain's volume as a continuous approximation of nx*ny*nz. Applies a
        /// small safety margin so the actual (ceil + 1)-per-axis grid still lands under the cap.
        /// </summary>
        internal static double SuggestResolutionForSampleCap(Box box, long sampleCap)
        {
            if (!box.IsValid || sampleCap < 8) return double.NaN;

            double lx = Math.Max(Math.Abs(box.X.Length), 1e-9);
            double ly = Math.Max(Math.Abs(box.Y.Length), 1e-9);
            double lz = Math.Max(Math.Abs(box.Z.Length), 1e-9);
            double volume = lx * ly * lz;

            double suggested = Math.Cbrt(volume / Math.Max(sampleCap, 8));
            return suggested * 1.05;
        }

        /// <summary>
        /// Extracts a field's zero (or `options.IsoLevel`) iso-surface on a box domain using bounded
        /// per-slab sampling memory. On success, `mesh` may still have zero faces if the field never
        /// crosses the iso-level inside the domain - callers should check `mesh.Faces.Count`.
        /// </summary>
        internal static bool TryExtract(
            WasperField field,
            Box domain,
            WasperUniformFieldMeshOptions options,
            out Mesh mesh,
            out WasperUniformFieldMeshStats stats,
            out string error)
        {
            mesh = null;
            stats = new WasperUniformFieldMeshStats();
            error = "";

            if (field?.Evaluator == null)
            {
                error = "Field has no evaluator.";
                return false;
            }

            if (!domain.IsValid)
            {
                error = "Domain box is not valid.";
                return false;
            }

            if (options == null)
                options = new WasperUniformFieldMeshOptions();

            BuildGridCounts(domain, options.Resolution, out int nx, out int ny, out int nz);
            long samples = (long)nx * ny * nz;

            stats.Nx = nx;
            stats.Ny = ny;
            stats.Nz = nz;
            stats.Samples = samples;

            if (samples > options.HardMaxSamples)
            {
                double suggested = SuggestResolutionForSampleCap(domain, options.HardMaxSamples);
                error =
                    $"Mesh extraction would create {samples:N0} samples at resolution {options.Resolution:F4} " +
                    $"(limit {options.HardMaxSamples:N0}). Try a resolution of about {suggested:F4} or larger, " +
                    "or reduce the domain.";
                return false;
            }

            int slabDepth = Math.Max(2, Math.Min(options.SlabDepth, nz));
            long slabSamples = (long)nx * ny * slabDepth;

            while (slabDepth > 2 && EstimateGridManagedBytes((long)nx * ny * slabDepth) > options.HardManagedBytes)
                slabDepth--;

            slabSamples = (long)nx * ny * slabDepth;
            long peakBytes = EstimateGridManagedBytes(slabSamples);

            // Even at the minimum slab depth (2), a single Z-layer's (nx * ny) footprint alone can still
            // exceed the hard budget when the domain's X/Y extent is huge relative to resolution - reducing
            // slabDepth further can't fix that, so refuse rather than allocate anyway (review finding R2).
            if (peakBytes > options.HardManagedBytes)
            {
                double suggested = SuggestResolutionForSampleCap(domain, options.HardMaxSamples);
                error =
                    $"Even the minimum slab (nx*ny*2 = {(long)nx * ny * 2:N0} samples, " +
                    $"~{peakBytes / (1024.0 * 1024.0):F1} MiB) exceeds the hard memory budget " +
                    $"({options.HardManagedBytes / (1024.0 * 1024.0):F1} MiB). Try a coarser resolution " +
                    $"(about {suggested:F4} or larger) or a smaller domain.";
                stats.SlabDepthUsed = slabDepth;
                stats.EstimatedPeakManagedBytes = peakBytes;
                stats.MemoryBudgetExceeded = true;
                return false;
            }

            bool budgetWarning = peakBytes > options.WarningManagedBytes;

            var result = new Mesh();
            long sampleMsAccum = 0;
            long meshMsAccum = 0;
            int slabCount = 0;

            try
            {
                int z0 = 0;
                while (z0 < nz - 1)
                {
                    int z1 = Math.Min(z0 + slabDepth - 1, nz - 1);
                    int localDepth = z1 - z0 + 1;
                    if (localDepth < 2)
                        break;

                    int localCount = nx * ny * localDepth;
                    var scalars = new double[localCount];
                    var points = new Point3d[localCount];

                    var swSample = System.Diagnostics.Stopwatch.StartNew();
                    int z0Local = z0;
                    Parallel.For(0, localDepth, li =>
                    {
                        int iz = z0Local + li;
                        double w = nz <= 1 ? 0.0 : (double)iz / (nz - 1);
                        for (int iy = 0; iy < ny; iy++)
                        {
                            double v = ny <= 1 ? 0.0 : (double)iy / (ny - 1);
                            for (int ix = 0; ix < nx; ix++)
                            {
                                double u = nx <= 1 ? 0.0 : (double)ix / (nx - 1);
                                int index = ix + nx * (iy + ny * li);
                                Point3d point = domain.PointAt(u, v, w);
                                points[index] = point;

                                bool domainBoundary = options.SealDomainBoundary &&
                                    (ix == 0 || ix == nx - 1 ||
                                     iy == 0 || iy == ny - 1 ||
                                     iz == 0 || iz == nz - 1);

                                // A positive boundary clips the negative/inside region to the finite
                                // sampling box. This closes periodic and otherwise unbounded implicit
                                // fields without inventing an arbitrary post-meshing FillHoles cap.
                                scalars[index] = domainBoundary
                                    ? options.IsoLevel + Math.Max(options.Resolution, 1e-6)
                                    : WasperFieldOps.SafeEvaluate(field, point);
                            }
                        }
                    });
                    swSample.Stop();
                    sampleMsAccum += swSample.ElapsedMilliseconds;

                    var swMesh = System.Diagnostics.Stopwatch.StartNew();
                    Mesh slabMesh = WasperMarchingCubes.Extract(
                        scalars,
                        points,
                        nx,
                        ny,
                        localDepth,
                        options.IsoLevel,
                        options.KeyTolerance,
                        options.MaxDegreeOfParallelism);
                    swMesh.Stop();
                    meshMsAccum += swMesh.ElapsedMilliseconds;

                    if (slabMesh != null && slabMesh.Faces.Count > 0)
                        result.Append(slabMesh);

                    slabCount++;
                    z0 = z1;
                }
            }
            catch (Exception ex)
            {
                error = "Field sampling / meshing failed: " + InnermostMessage(ex);
                return false;
            }

            result.Vertices.CombineIdentical(true, true);
            result.Faces.CullDegenerateFaces();
            result.Vertices.CullUnused();

            // Each marching-cubes slab arrives with its own computed normals. Appending the slabs and
            // then combining seam vertices changes the final topology, leaving those copied normal
            // arrays stale; a seam vertex can consequently retain a zero-length ON_Mesh normal and
            // make otherwise valid geometry fail Rhino's Mesh.IsValid check. The shared mesher returns
            // topology here, so discard the stale normals. Callers that need preview normals already
            // compute them after their own orientation/cleanup pass; Dendro voxelization needs none.
            result.Normals.Clear();
            result.FaceNormals.Clear();
            result.Compact();

            mesh = result;
            stats.SlabCount = slabCount;
            stats.SlabDepthUsed = slabDepth;
            stats.SampleMs = sampleMsAccum;
            stats.MeshMs = meshMsAccum;
            stats.EstimatedPeakManagedBytes = peakBytes;
            stats.MemoryBudgetExceeded = budgetWarning;
            return true;
        }

        /// <summary>Unwraps an exception chain down to its innermost message (matches the pattern
        /// duplicated across WASPer's field-meshing components, centralized here).</summary>
        internal static string InnermostMessage(Exception exception)
        {
            Exception current = exception;
            while (current.InnerException != null)
                current = current.InnerException;
            return current.Message;
        }
    }
}
