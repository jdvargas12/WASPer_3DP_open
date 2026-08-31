using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

using Grasshopper;
using Grasshopper.Kernel.Data;

using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP.Components._5_0_Gcode
{
    internal static class WasperXrBinaryPackage
    {
        internal const string SchemaVersion = "0.2.0";
        internal const string Extension = ".wasperxr";
        private const string Magic = "WSPXRBN1";
        private const int ContainerVersion = 1;
        private const byte GzipCompression = 1;

        internal static void WriteAtomic(
            string finalPath,
            WasperPrintPath path,
            string jobId,
            int revision,
            string pluginVersion,
            UnitSystem units,
            double metresPerUnit,
            IEnumerable<WasperKpi> kpis = null,
            bool disablePlayback = false,
            double simulationParameter = 1.0,
            WasperXrScenePack scenePack = null)
        {
            string temporaryPath = finalPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                Write(
                    temporaryPath,
                    path,
                    jobId,
                    revision,
                    pluginVersion,
                    units,
                    metresPerUnit,
                    kpis,
                    disablePlayback,
                    simulationParameter,
                    scenePack);
                File.Move(temporaryPath, finalPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        internal static int ReadRevision(string path)
        {
            if (!File.Exists(path))
                return 0;

            try
            {
                using (var stream = File.OpenRead(path))
                using (var header = new BinaryReader(stream, Encoding.UTF8, true))
                {
                    string magic = Encoding.ASCII.GetString(header.ReadBytes(8));
                    if (!string.Equals(magic, Magic, StringComparison.Ordinal))
                        return 0;
                    if (header.ReadInt32() != ContainerVersion || header.ReadByte() != GzipCompression)
                        return 0;

                    using (var gzip = new GZipStream(stream, CompressionMode.Decompress, true))
                    using (var reader = new BinaryReader(gzip, Encoding.UTF8, false))
                    {
                        reader.ReadString();
                        reader.ReadString();
                        reader.ReadString();
                        return Math.Max(0, reader.ReadInt32());
                    }
                }
            }
            catch
            {
                return 0;
            }
        }

        // Live-link support (M5): the exact same container this method writes to disk, built
        // in memory instead so a caller can push it straight over a WebSocket (see
        // wsp_Gc07_Export_XR_Package.TryBuildLivePackageBytes / WasperLiveViewerClient). No
        // change to the on-disk format or the existing Write/WriteAtomic callers -- this just
        // exposes the same bytes a different way.
        internal static byte[] WriteToBytes(
            WasperPrintPath path,
            string jobId,
            int revision,
            string pluginVersion,
            UnitSystem units,
            double metresPerUnit,
            IEnumerable<WasperKpi> kpis = null,
            bool disablePlayback = false,
            double simulationParameter = 1.0,
            WasperXrScenePack scenePack = null,
            bool includeContextNormals = true)
        {
            using var stream = new MemoryStream();
            WriteToStream(
                stream,
                path,
                jobId,
                revision,
                pluginVersion,
                units,
                metresPerUnit,
                kpis,
                disablePlayback,
                simulationParameter,
                scenePack,
                includeContextNormals);
            return stream.ToArray();
        }

        private static void Write(
            string pathName,
            WasperPrintPath path,
            string jobId,
            int revision,
            string pluginVersion,
            UnitSystem units,
            double metresPerUnit,
            IEnumerable<WasperKpi> kpis = null,
            bool disablePlayback = false,
            double simulationParameter = 1.0,
            WasperXrScenePack scenePack = null)
        {
            using (var stream = new FileStream(pathName, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                WriteToStream(
                    stream,
                    path,
                    jobId,
                    revision,
                    pluginVersion,
                    units,
                    metresPerUnit,
                    kpis,
                    disablePlayback,
                    simulationParameter,
                    scenePack,
                    includeContextNormals: true);
            }
        }

        private static void WriteToStream(
            Stream stream,
            WasperPrintPath path,
            string jobId,
            int revision,
            string pluginVersion,
            UnitSystem units,
            double metresPerUnit,
            IEnumerable<WasperKpi> kpis = null,
            bool disablePlayback = false,
            double simulationParameter = 1.0,
            WasperXrScenePack scenePack = null,
            bool includeContextNormals = true)
        {
            BoundingBox bounds = ResolveBounds(path.PtPlanes);
            Point3d origin = bounds.IsValid ? bounds.Min : Point3d.Origin;
            List<int> validBranches = Enumerable.Range(0, path.PtPlanes.BranchCount)
                .Where(index => path.PtPlanes.Branches[index]?.Count > 0)
                .ToList();

            using (var header = new BinaryWriter(stream, Encoding.UTF8, true))
            {
                header.Write(Encoding.ASCII.GetBytes(Magic));
                header.Write(ContainerVersion);
                header.Write(GzipCompression);
                header.Flush();

                using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, true))
                using (var writer = new BinaryWriter(gzip, Encoding.UTF8, false))
                {
                    writer.Write(SchemaVersion);
                    writer.Write("wasper.xr.printPlan");
                    writer.Write(jobId ?? "wasper-job");
                    writer.Write(Math.Max(0, revision));
                    writer.Write(DateTime.UtcNow.ToString("O"));
                    writer.Write(string.IsNullOrWhiteSpace(pluginVersion) ? "v1.0.x" : pluginVersion);
                    writer.Write("WASPer");
                    writer.Write(units.ToString());
                    writer.Write(metresPerUnit);
                    writer.Write("right");
                    writer.Write("+Z");
                    WritePoint64(writer, origin);
                    writer.Write(bounds.IsValid);
                    if (bounds.IsValid)
                    {
                        WritePoint64(writer, bounds.Min);
                        WritePoint64(writer, bounds.Max);
                    }

                    writer.Write(validBranches.Count);
                    foreach (int branchIndex in validBranches)
                        WriteBranch(writer, path, branchIndex, origin);

                    writer.Write(path.MotionPlan.Count);
                    foreach (WasperMotion motion in path.MotionPlan.Motions)
                        WriteMotion(writer, motion, origin);

                    writer.Write(CountLogicalLayers(path.Points));
                    writer.Write(path.PointCount);
                    writer.Write(path.MotionPlan.DurationMinutes * 60.0);

                    // KPI section (added for the Process Viewer KPI feature).
                    // Written last and read with an EOF-tolerant reader on
                    // the WASPer.XR.Core side, so packages written before
                    // this feature (which simply end at DurationMinutes
                    // above) still import cleanly against the new reader.
                    // Takes a plain IEnumerable rather than a WasperKpiSet so
                    // a caller can hand in exactly the items it wants
                    // written (e.g. Sm01 passing only its EnabledItems from
                    // a larger merged set) without this method needing to
                    // know anything about enable/disable filtering itself.
                    List<WasperKpi> kpiItems = (kpis ?? Enumerable.Empty<WasperKpi>())
                        .Where(kpi => kpi != null)
                        .ToList();
                    writer.Write(kpiItems.Count);
                    foreach (WasperKpi kpi in kpiItems)
                        WriteKpi(writer, kpi);

                    // DisablePlayback flag (added for Sm05 XR Scene Params, 2026-08-19). Written
                    // last and read with the same EOF-tolerant pattern as the KPI section above,
                    // so packages written before this feature still import cleanly. True means
                    // an external source (Sm05's sim_par, typically fed by Gc05) already owns
                    // the simulated print position -- the web viewer should hide its own
                    // Play/Stop/time-slider controls rather than run a second, conflicting clock.
                    writer.Write(disablePlayback);

                    // SimulationParameter (added same day, right after DisablePlayback): the
                    // actual 0-1 progress value from Sm05's sim_par, not just whether it's
                    // connected. Only meaningful to a viewer when disablePlayback is true, but
                    // written unconditionally (defaulting to 1.0, "fully printed") so old and
                    // new readers agree on a value either way. Same EOF-tolerant read pattern.
                    // The browser multiplies this by the job's own duration to pick a
                    // currentTime and reuses its existing Mesh-mode "printed so far" rendering
                    // -- no path-trimming happens here in the writer, the full path is still
                    // sent every time, exactly as before this field was added.
                    writer.Write(Math.Max(0.0, Math.Min(1.0, simulationParameter)));

                    List<WasperXrContextMeshData> contextMeshes = scenePack?.ContextMeshes ??
                        new List<WasperXrContextMeshData>();
                    writer.Write(contextMeshes.Count);
                    foreach (WasperXrContextMeshData contextMesh in contextMeshes)
                        WriteContextMesh(writer, contextMesh, origin, includeContextNormals);

                    WriteViewerStyle(writer);
                }
            }
        }

        private static void WriteViewerStyle(BinaryWriter writer)
        {
            System.Drawing.Color[] colors = WasperPrintPathPreviewSettings.ResolveRolePalette();
            writer.Write(WasperPrintPathPreviewSettings.Mode.ToString());
            foreach (System.Drawing.Color color in colors)
                writer.Write((color.R << 16) | (color.G << 8) | color.B);
        }

        private static void WriteContextMesh(
            BinaryWriter writer,
            WasperXrContextMeshData contextMesh,
            Point3d origin,
            bool includeNormals)
        {
            Mesh mesh = contextMesh.Mesh;
            writer.Write(contextMesh.Id ?? string.Empty);
            writer.Write(contextMesh.Color.R);
            writer.Write(contextMesh.Color.G);
            writer.Write(contextMesh.Color.B);
            writer.Write(contextMesh.Color.A);

            writer.Write(mesh.Vertices.Count);
            foreach (Point3f vertex in mesh.Vertices)
                WriteRelativePoint32(writer, new Point3d(vertex), origin);

            int normalCount = includeNormals ? mesh.Normals.Count : 0;
            writer.Write(normalCount);
            if (includeNormals)
            {
                foreach (Vector3f normal in mesh.Normals)
                    WriteVector32(writer, new Vector3d(normal));
            }

            writer.Write(mesh.Faces.Count * 3);
            foreach (MeshFace face in mesh.Faces)
            {
                writer.Write(face.A);
                writer.Write(face.B);
                writer.Write(face.C);
            }
        }

        private static void WriteKpi(BinaryWriter writer, WasperKpi kpi)
        {
            writer.Write(kpi.Key ?? string.Empty);
            writer.Write(kpi.Label ?? string.Empty);
            writer.Write(kpi.Group ?? string.Empty);
            writer.Write(kpi.Unit ?? string.Empty);
            writer.Write(kpi.Value.HasValue);
            if (kpi.Value.HasValue)
                writer.Write(kpi.Value.Value);
            bool hasText = !string.IsNullOrEmpty(kpi.TextValue);
            writer.Write(hasText);
            if (hasText)
                writer.Write(kpi.TextValue);
        }

        private static void WriteBranch(
            BinaryWriter writer,
            WasperPrintPath path,
            int branchIndex,
            Point3d origin)
        {
            GH_Path branchPath = path.PtPlanes.Paths[branchIndex];
            IList<Plane> planes = path.PtPlanes.Branches[branchIndex];
            int role = ResolveScalar(path.PathRoles, branchPath, 0);
            int strokeId = ResolveScalar(path.StrokeIds, branchPath, -1);
            bool closed = planes.Count > 2 &&
                planes[0].Origin.DistanceToSquared(planes[planes.Count - 1].Origin) <=
                RhinoMath.ZeroTolerance * RhinoMath.ZeroTolerance;

            writer.Write(branchIndex);
            writer.Write(branchPath.ToString());
            writer.Write(branchPath.Indices.Length > 0 ? branchPath.Indices[0] : 0);
            writer.Write(role);
            writer.Write(WasperPathRoleMetadata.RoleName(
                Enum.IsDefined(typeof(WasperPathRole), role)
                    ? (WasperPathRole)role
                    : WasperPathRole.Undefined));
            writer.Write(strokeId);
            writer.Write(closed);
            writer.Write(planes.Count);

            foreach (Plane plane in planes)
                WriteRelativePoint32(writer, plane.Origin, origin);
            foreach (Plane plane in planes)
                WriteVector32(writer, plane.ZAxis);

            WriteSeries(writer, ResolveValues(path.LayerH, branchPath, planes.Count), planes.Count);
            WriteSeries(writer, ResolveValues(path.LayerW, branchPath, planes.Count), planes.Count);
            WriteSeries(writer, ResolveValues(path.LayerWf, branchPath, planes.Count), planes.Count);
        }

        private static void WriteMotion(BinaryWriter writer, WasperMotion motion, Point3d origin)
        {
            writer.Write((byte)motion.Type);
            writer.Write(motion.LayerIndex);
            writer.Write(motion.BranchIndex);
            writer.Write(motion.PointIndex);
            writer.Write(motion.DurationMinutes * 60.0);
            WriteRelativePoint32(writer, motion.From, origin);
            WriteRelativePoint32(writer, motion.To, origin);
        }

        private static void WriteSeries(BinaryWriter writer, IList<double> values, int count)
        {
            if (values == null || values.Count == 0 || count <= 0)
            {
                writer.Write((byte)0);
                return;
            }

            double first = values[0];
            bool constant = true;
            for (int i = 1; i < count; i++)
            {
                double value = values[values.Count == 1 ? 0 : Math.Min(i, values.Count - 1)];
                if (Math.Abs(value - first) > 1e-9)
                {
                    constant = false;
                    break;
                }
            }

            if (constant)
            {
                writer.Write((byte)1);
                writer.Write(ToFiniteFloat(first));
                return;
            }

            writer.Write((byte)2);
            writer.Write(count);
            for (int i = 0; i < count; i++)
                writer.Write(ToFiniteFloat(values[values.Count == 1 ? 0 : Math.Min(i, values.Count - 1)]));
        }

        private static IList<double> ResolveValues(
            DataTree<double> tree,
            GH_Path path,
            int count)
        {
            if (tree == null || !tree.PathExists(path))
                return null;
            IList<double> values = tree.Branch(path);
            return values == null || values.Count == 0 ? null : values;
        }

        private static int ResolveScalar(DataTree<int> tree, GH_Path path, int fallback)
        {
            if (tree == null || !tree.PathExists(path))
                return fallback;
            IList<int> values = tree.Branch(path);
            return values == null || values.Count == 0 ? fallback : values[0];
        }

        private static BoundingBox ResolveBounds(DataTree<Plane> planes)
        {
            BoundingBox bounds = BoundingBox.Empty;
            for (int i = 0; i < planes.BranchCount; i++)
            {
                IList<Plane> branch = planes.Branches[i];
                if (branch == null)
                    continue;
                foreach (Plane plane in branch)
                    bounds.Union(plane.Origin);
            }
            return bounds;
        }

        private static int CountLogicalLayers(DataTree<Point3d> points)
        {
            var layers = new HashSet<int>();
            foreach (GH_Path path in points.Paths)
                layers.Add(path.Indices.Length > 0 ? path.Indices[0] : 0);
            return layers.Count;
        }

        private static void WritePoint64(BinaryWriter writer, Point3d point)
        {
            writer.Write(point.X);
            writer.Write(point.Y);
            writer.Write(point.Z);
        }

        private static void WriteRelativePoint32(BinaryWriter writer, Point3d point, Point3d origin)
        {
            writer.Write(ToFiniteFloat(point.X - origin.X));
            writer.Write(ToFiniteFloat(point.Y - origin.Y));
            writer.Write(ToFiniteFloat(point.Z - origin.Z));
        }

        private static void WriteVector32(BinaryWriter writer, Vector3d vector)
        {
            writer.Write(ToFiniteFloat(vector.X));
            writer.Write(ToFiniteFloat(vector.Y));
            writer.Write(ToFiniteFloat(vector.Z));
        }

        private static float ToFiniteFloat(double value) =>
            double.IsNaN(value) || double.IsInfinity(value) ? 0f : (float)value;
    }
}
