#region Usings
using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
#endregion

namespace WASPer_3DP.Components._2_2_Fields
{
    /// <summary>
    /// wsp_Fi06_3D Field from 2D Fields
    /// Builds a 3D field, and optionally an iso-surface mesh, from ordered packed 2D field slices.
    /// </summary>
    public class wsp_Fi06_Mesh_from_Fields : GH_Component
    {
        private readonly string _versionTag;

        public wsp_Fi06_Mesh_from_Fields()
            : base(
                "wsp_Fi06_3D Field from 2D Fields",
                "3D Field",
                "Generates a 3D WASPer field from a tree of packed 2D field slices, with optional iso-surface meshing.\n" +
                "Works with Fi04 field_obj slices and Fi08 infill-layer field slices.\n" +
                "The component converts ordered 2D fields into a curved 3D scalar grid, can resample the slice resolution, and optionally uses the shared WASPer Marching Cubes extractor.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "2.2_Fields")
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("52E56D35-1E0E-46F3-9E9C-96F73C6E1C42");

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var stream = assembly.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Fi06_Mesh from Fields.png"))
                    {
                        return stream != null ? new Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "field_obj",
                "fields",
                "2D field_obj tree. Connect the field_obj output from wsp_Fi04_Fields to Frames or wsp_Fi08_Infill Layers to Fields.\n" +
                "Each branch should contain one packed 2D field slice.\n" +
                "Expected members: G, NxVerts, NyVerts, Plane, FrameSize, CellSize, and optional CenterXY.",
                GH_ParamAccess.tree);

            p.AddNumberParameter(
                "iso_level",
                "iso",
                "Iso level in g-space. The output mesh is extracted where G == iso_level.",
                GH_ParamAccess.item,
                0.0);

            p.AddNumberParameter(
                "resolution",
                "res",
                "Optional manual slice resolution in model units.\n" +
                "If res <= 0, the native Fi04 field resolution is used.\n" +
                "If res > 0, each Fi04 slice is bilinearly resampled before meshing.",
                GH_ParamAccess.item,
                0.0);

            p.AddBooleanParameter(
                "cap_ends",
                "caps",
                "If true, adds positive ghost slices before the first field and after the last field to close the mesh ends.",
                GH_ParamAccess.item,
                true);

            p.AddBooleanParameter(
                "disjoin_mesh",
                "disjoin",
                "If true, splits disconnected mesh pieces into separate output items.",
                GH_ParamAccess.item,
                false);

            p.AddBooleanParameter(
                "clean_mesh",
                "clean",
                "If true, performs standard mesh cleanup after extraction.",
                GH_ParamAccess.item,
                true);

            p.AddBooleanParameter(
                "mesh",
                "mesh?",
                "If true, extract the iso-surface mesh. If false, skip meshing and output only the 3D WASPer field.",
                GH_ParamAccess.item,
                true);
            p[6].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddMeshParameter(
                "mesh_out",
                "mesh",
                "Generated iso-surface mesh. Empty when mesh? is false. If disjoin_mesh is true, disconnected pieces are output as separate list items.",
                GH_ParamAccess.list);

            p.AddGenericParameter(
                "field",
                "field",
                "WASPer 3D field generated directly from the sampled Fi04 field stack.\n" +
                "Outputs the shared WasperFieldGoo type used by 2.3_Fields_3D. Its zero level corresponds to the selected Fi06 iso level.",
                GH_ParamAccess.item);

            p.AddPlaneParameter(
                "sample_frames",
                "frames",
                "Input slice frames used for the 3D field grid.",
                GH_ParamAccess.list);

            p.AddTextParameter(
                "info",
                "info",
                "Mesh generation summary and warnings.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            GH_Structure<IGH_Goo> tree;
            double isoLevel = 0.0;
            double res = 0.0;
            bool capEnds = true;
            bool disjoin = false;
            bool clean = true;
            bool buildMesh = true;

            if (!DA.GetDataTree(0, out tree) || tree == null || tree.PathCount == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "field_obj tree is empty.");
                DA.SetDataList(0, new List<Mesh>());
                DA.SetData(3, "No field_obj tree supplied.");
                return;
            }

            DA.GetData(1, ref isoLevel);
            DA.GetData(2, ref res);
            DA.GetData(3, ref capEnds);
            DA.GetData(4, ref disjoin);
            DA.GetData(5, ref clean);
            DA.GetData(6, ref buildMesh);

            var warnings = new List<string>();
            var slices = ReadSlices(tree, warnings);
            if (slices.Count < 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "At least two valid field slices are required for a 3D field.");
                DA.SetDataList(0, new List<Mesh>());
                DA.SetData(1, null);
                DA.SetDataList(2, slices.Select(s => s.Plane));
                DA.SetData(3, BuildInfo(slices.Count, 0, 0, false, warnings, 0, 0, res, false, buildMesh));
                return;
            }

            if (!ValidateSlices(slices, warnings))
            {
                foreach (string w in warnings)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, w);

                DA.SetDataList(0, new List<Mesh>());
                DA.SetData(1, null);
                DA.SetDataList(2, slices.Select(s => s.Plane));
                DA.SetData(3, BuildInfo(slices.Count, 0, 0, false, warnings, 0, 0, res, false, buildMesh));
                return;
            }

            double usedRes;
            var meshSlices = PrepareSlicesForResolution(slices, res, warnings, out usedRes);

            double[] scalars;
            Point3d[] points;
            int nx = meshSlices[0].NxVerts;
            int ny = meshSlices[0].NyVerts;
            int nz;
            BuildGrid(meshSlices, isoLevel, capEnds, out scalars, out points, out nz);

            WasperField sampledField = WasperField.FromSampledPointGrid(
                scalars,
                points,
                nx,
                ny,
                nz,
                "Fi06 Mesh from Fields sampled grid");
            WasperFieldGoo outField = null;
            bool field3dCompatible = false;
            if (sampledField != null && sampledField.Evaluator != null && sampledField.Domain.IsValid)
            {
                double probe = sampledField.Evaluate(points[points.Length / 2]);
                if (!double.IsNaN(probe) && !double.IsInfinity(probe))
                {
                    outField = new WasperFieldGoo(sampledField);
                    field3dCompatible = outField.IsValid;
                }
            }

            if (!field3dCompatible)
                warnings.Add("Could not build a valid 2.3_Fields_3D-compatible WasperFieldGoo from the Fi04 field stack.");

            var outMeshes = new List<Mesh>();
            if (buildMesh)
            {
                double keyTol = Math.Max(1e-9, meshSlices[0].CellSize * 1e-5);
                Mesh mesh = WasperMarchingCubes.Extract(
                    scalars,
                    points,
                    nx,
                    ny,
                    nz,
                    0.0,
                    keyTol,
                    0);

                if (mesh != null && mesh.Faces.Count > 0)
                {
                    if (clean)
                        CleanMesh(mesh);

                    if (disjoin)
                    {
                        Mesh[] pieces = mesh.SplitDisjointPieces();
                        if (pieces != null && pieces.Length > 0)
                            outMeshes.AddRange(pieces.Where(m => m != null && m.Faces.Count > 0));
                        else
                            outMeshes.Add(mesh);
                    }
                    else
                    {
                        outMeshes.Add(mesh);
                    }
                }
            }

            if (buildMesh && outMeshes.Count == 0)
                warnings.Add("No mesh was generated. Check whether the field tree crosses the requested iso_level.");

            foreach (string w in warnings)
                AddRuntimeMessage(w.StartsWith("No mesh", StringComparison.OrdinalIgnoreCase)
                    ? GH_RuntimeMessageLevel.Warning
                    : GH_RuntimeMessageLevel.Remark, w);

            DA.SetDataList(0, outMeshes);
            DA.SetData(1, outField);
            DA.SetDataList(2, slices.Select(s => s.Plane));
            DA.SetData(3, BuildInfo(slices.Count, nx, ny, capEnds, warnings, outMeshes.Count, nz, usedRes, field3dCompatible, buildMesh));
        }

        private static List<FieldSlice> ReadSlices(GH_Structure<IGH_Goo> tree, List<string> warnings)
        {
            var slices = new List<FieldSlice>();

            for (int p = 0; p < tree.PathCount; p++)
            {
                IList branch = tree.get_Branch(p);
                if (branch == null || branch.Count == 0)
                    continue;

                object raw = null;
                for (int i = 0; i < branch.Count; i++)
                {
                    raw = Unwrap(branch[i] as IGH_Goo);
                    if (raw != null) break;
                }

                if (raw == null)
                {
                    warnings.Add($"Skipped branch {tree.get_Path(p)} because it contains no valid field object.");
                    continue;
                }

                FieldSlice slice;
                if (TryReadSlice(raw, tree.get_Path(p), out slice, warnings))
                    slices.Add(slice);
            }

            return slices;
        }

        private static object Unwrap(IGH_Goo goo)
        {
            if (goo == null) return null;
            if (goo is GH_ObjectWrapper wrapper && wrapper.Value != null)
                return wrapper.Value;

            object value;
            if (goo.CastTo(out value) && value != null)
            {
                if (value is GH_ObjectWrapper nested && nested.Value != null)
                    return nested.Value;
                return value;
            }

            return null;
        }

        private static bool TryReadSlice(object o, GH_Path path, out FieldSlice slice, List<string> warnings)
        {
            slice = null;
            Type t = o.GetType();

            if (!HasMember(t, "G") ||
                !HasMember(t, "NxVerts") ||
                !HasMember(t, "NyVerts") ||
                !HasMember(t, "Plane") ||
                !HasMember(t, "FrameSize") ||
                !HasMember(t, "CellSize"))
            {
                warnings.Add($"Skipped branch {path}: field object is missing required members.");
                return false;
            }

            double[] g = GetDoubleArray(o, "G");
            int nx = GetInt(o, "NxVerts");
            int ny = GetInt(o, "NyVerts");
            Plane plane = GetPlane(o, "Plane");
            double frameSize = GetDouble(o, "FrameSize");
            double cellSize = GetDouble(o, "CellSize");
            Point2d center = HasMember(t, "CenterXY") ? GetPoint2d(o, "CenterXY") : new Point2d(0, 0);

            if (g == null || g.Length == 0 || nx < 2 || ny < 2 || !plane.IsValid ||
                frameSize <= RhinoMath.ZeroTolerance || cellSize <= RhinoMath.ZeroTolerance)
            {
                warnings.Add($"Skipped branch {path}: field object has invalid grid data.");
                return false;
            }

            int expected = nx * ny;
            if (g.Length < expected)
            {
                warnings.Add($"Skipped branch {path}: G.Length={g.Length}, expected at least {expected}.");
                return false;
            }

            slice = new FieldSlice(g, nx, ny, plane, center, frameSize, cellSize, path);
            return true;
        }

        private static bool ValidateSlices(List<FieldSlice> slices, List<string> warnings)
        {
            FieldSlice first = slices[0];
            double dimTol = Math.Max(1e-7, first.CellSize * 1e-4);

            for (int i = 1; i < slices.Count; i++)
            {
                FieldSlice s = slices[i];
                if (s.NxVerts != first.NxVerts || s.NyVerts != first.NyVerts)
                {
                    warnings.Add($"Grid mismatch at slice {i} ({s.Path}): got {s.NxVerts}x{s.NyVerts}, expected {first.NxVerts}x{first.NyVerts}.");
                    return false;
                }

                if (Math.Abs(s.FrameSize - first.FrameSize) > dimTol ||
                    Math.Abs(s.CellSize - first.CellSize) > dimTol)
                {
                    warnings.Add($"Grid spacing mismatch at slice {i} ({s.Path}). All Fi04 slices must share frame_size and cell_size.");
                    return false;
                }
            }

            return true;
        }

        private static List<FieldSlice> PrepareSlicesForResolution(List<FieldSlice> slices, double res, List<string> warnings, out double usedRes)
        {
            usedRes = slices[0].CellSize;
            if (res <= 0.0)
                return slices;

            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;
            double target = Math.Max(res, tol * 10.0);
            FieldSlice first = slices[0];

            int n = Math.Max(2, (int)Math.Ceiling(first.FrameSize / target) + 1);
            double cell = first.FrameSize / (n - 1);
            usedRes = cell;

            if (Math.Abs(cell - first.CellSize) <= Math.Max(tol, first.CellSize * 1e-6) && n == first.NxVerts && n == first.NyVerts)
                return slices;

            var resampled = new List<FieldSlice>(slices.Count);
            for (int i = 0; i < slices.Count; i++)
                resampled.Add(ResampleSlice(slices[i], n, n, cell));

            warnings.Add($"Manual res active: requested {res:F4}, using grid {n}x{n} with cell {cell:F4}. Fi04 native cell was {first.CellSize:F4}.");
            return resampled;
        }

        private static FieldSlice ResampleSlice(FieldSlice source, int nx, int ny, double cell)
        {
            var g = new double[nx * ny];
            double half = source.FrameSize * 0.5;

            for (int j = 0; j < ny; j++)
            {
                double y = source.CenterXY.Y - half + j * cell;
                for (int i = 0; i < nx; i++)
                {
                    double x = source.CenterXY.X - half + i * cell;
                    g[i + j * nx] = BilinearSample(source, x, y);
                }
            }

            return new FieldSlice(g, nx, ny, source.Plane, source.CenterXY, source.FrameSize, cell, source.Path);
        }

        private static double BilinearSample(FieldSlice s, double x, double y)
        {
            double half = s.FrameSize * 0.5;
            double u = (x - (s.CenterXY.X - half)) / s.CellSize;
            double v = (y - (s.CenterXY.Y - half)) / s.CellSize;

            if (u < 0.0) u = 0.0;
            if (v < 0.0) v = 0.0;
            if (u > s.NxVerts - 1) u = s.NxVerts - 1;
            if (v > s.NyVerts - 1) v = s.NyVerts - 1;

            int i0 = (int)Math.Floor(u);
            int j0 = (int)Math.Floor(v);
            if (i0 >= s.NxVerts - 1) i0 = s.NxVerts - 2;
            if (j0 >= s.NyVerts - 1) j0 = s.NyVerts - 2;
            if (i0 < 0) i0 = 0;
            if (j0 < 0) j0 = 0;

            int i1 = i0 + 1;
            int j1 = j0 + 1;
            double tx = u - i0;
            double ty = v - j0;

            double g00 = s.G[i0 + j0 * s.NxVerts];
            double g10 = s.G[i1 + j0 * s.NxVerts];
            double g01 = s.G[i0 + j1 * s.NxVerts];
            double g11 = s.G[i1 + j1 * s.NxVerts];

            double gx0 = g00 + tx * (g10 - g00);
            double gx1 = g01 + tx * (g11 - g01);
            return gx0 + ty * (gx1 - gx0);
        }

        private static void BuildGrid(List<FieldSlice> slices, double isoLevel, bool capEnds, out double[] scalars, out Point3d[] points, out int nz)
        {
            int nx = slices[0].NxVerts;
            int ny = slices[0].NyVerts;
            int realNz = slices.Count;
            int zOffset = capEnds ? 1 : 0;
            nz = realNz + (capEnds ? 2 : 0);

            scalars = new double[nx * ny * nz];
            points = new Point3d[nx * ny * nz];

            if (capEnds)
            {
                FillGhostSlice(slices[0], slices[1], true, isoLevel, scalars, points, nx, ny, 0);
                FillGhostSlice(slices[realNz - 1], slices[realNz - 2], false, isoLevel, scalars, points, nx, ny, nz - 1);
            }

            for (int k = 0; k < realNz; k++)
            {
                FieldSlice s = slices[k];
                int iz = k + zOffset;
                FillRealSlice(s, isoLevel, scalars, points, nx, ny, iz);
            }
        }

        private static void FillRealSlice(FieldSlice s, double isoLevel, double[] scalars, Point3d[] points, int nx, int ny, int iz)
        {
            double half = s.FrameSize * 0.5;
            for (int j = 0; j < ny; j++)
            {
                double y = s.CenterXY.Y - half + j * s.CellSize;
                for (int i = 0; i < nx; i++)
                {
                    double x = s.CenterXY.X - half + i * s.CellSize;
                    int src = i + j * nx;
                    int dst = Idx(i, j, iz, nx, ny);

                    scalars[dst] = s.G[src] - isoLevel;
                    points[dst] = s.Plane.Origin + x * s.Plane.XAxis + y * s.Plane.YAxis;
                }
            }
        }

        private static void FillGhostSlice(FieldSlice end, FieldSlice next, bool beforeStart, double isoLevel, double[] scalars, Point3d[] points, int nx, int ny, int iz)
        {
            Vector3d offset = end.Plane.Origin - next.Plane.Origin;
            if (!offset.Unitize())
                offset = end.Plane.ZAxis;

            if (!beforeStart)
                offset.Reverse();

            double distance = Math.Max(end.CellSize, end.Plane.Origin.DistanceTo(next.Plane.Origin));
            Plane ghostPlane = end.Plane;
            ghostPlane.Origin = end.Plane.Origin + offset * distance;

            double positive = ComputePositiveOutsideValue(end.G, isoLevel, end.CellSize);
            double half = end.FrameSize * 0.5;

            for (int j = 0; j < ny; j++)
            {
                double y = end.CenterXY.Y - half + j * end.CellSize;
                for (int i = 0; i < nx; i++)
                {
                    double x = end.CenterXY.X - half + i * end.CellSize;
                    int dst = Idx(i, j, iz, nx, ny);
                    scalars[dst] = positive;
                    points[dst] = ghostPlane.Origin + x * ghostPlane.XAxis + y * ghostPlane.YAxis;
                }
            }
        }

        private static double ComputePositiveOutsideValue(double[] g, double isoLevel, double fallback)
        {
            double maxAbs = Math.Max(Math.Abs(fallback), 1.0);
            for (int i = 0; i < g.Length; i++)
                maxAbs = Math.Max(maxAbs, Math.Abs(g[i] - isoLevel));

            return maxAbs + Math.Max(Math.Abs(fallback), 1e-6);
        }

        private static void CleanMesh(Mesh mesh)
        {
            if (mesh == null) return;
            mesh.Vertices.CombineIdentical(true, true);
            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();
            mesh.Weld(Math.PI);
            mesh.UnifyNormals();
            mesh.Normals.ComputeNormals();
            mesh.FaceNormals.ComputeFaceNormals();
            mesh.Compact();
        }

        private static string BuildInfo(
            int sliceCount,
            int nx,
            int ny,
            bool capEnds,
            List<string> warnings,
            int meshCount = 0,
            int nz = 0,
            double usedRes = 0.0,
            bool field3dCompatible = false,
            bool buildMesh = true)
        {
            return
                $"Fi06 3D Field from 2D Fields\n" +
                $"slices={sliceCount}, grid={nx}x{ny}x{nz}, res={usedRes:F4}, caps={(capEnds ? "ON" : "OFF")}, mesh?={(buildMesh ? "ON" : "OFF")}, meshes={meshCount}\n" +
                $"field={(field3dCompatible ? "2.3_Fields_3D compatible (WasperFieldGoo; zero=Fi06 iso)" : "not available")}\n" +
                $"warnings={warnings.Count}" +
                (warnings.Count > 0 ? "\n" + string.Join("\n", warnings) : "");
        }

        private static int Idx(int ix, int iy, int iz, int nx, int ny)
        {
            return ix + nx * (iy + ny * iz);
        }

        private static bool HasMember(Type t, string name)
        {
            return t.GetField(name) != null || t.GetProperty(name) != null;
        }

        private static double[] GetDoubleArray(object o, string name)
        {
            Type t = o.GetType();
            var f = t.GetField(name);
            if (f != null) return f.GetValue(o) as double[];
            var p = t.GetProperty(name);
            if (p != null) return p.GetValue(o, null) as double[];
            return null;
        }

        private static int GetInt(object o, string name)
        {
            Type t = o.GetType();
            var f = t.GetField(name);
            if (f != null) return Convert.ToInt32(f.GetValue(o));
            var p = t.GetProperty(name);
            if (p != null) return Convert.ToInt32(p.GetValue(o, null));
            return 0;
        }

        private static double GetDouble(object o, string name)
        {
            Type t = o.GetType();
            var f = t.GetField(name);
            if (f != null) return Convert.ToDouble(f.GetValue(o));
            var p = t.GetProperty(name);
            if (p != null) return Convert.ToDouble(p.GetValue(o, null));
            return 0.0;
        }

        private static Plane GetPlane(object o, string name)
        {
            Type t = o.GetType();
            var f = t.GetField(name);
            if (f != null) return (Plane)f.GetValue(o);
            var p = t.GetProperty(name);
            if (p != null) return (Plane)p.GetValue(o, null);
            return Plane.Unset;
        }

        private static Point2d GetPoint2d(object o, string name)
        {
            Type t = o.GetType();
            var f = t.GetField(name);
            if (f != null) return (Point2d)f.GetValue(o);
            var p = t.GetProperty(name);
            if (p != null) return (Point2d)p.GetValue(o, null);
            return new Point2d(0, 0);
        }

        private sealed class FieldSlice
        {
            public readonly double[] G;
            public readonly int NxVerts;
            public readonly int NyVerts;
            public readonly Plane Plane;
            public readonly Point2d CenterXY;
            public readonly double FrameSize;
            public readonly double CellSize;
            public readonly GH_Path Path;

            public FieldSlice(double[] g, int nxVerts, int nyVerts, Plane plane, Point2d centerXY, double frameSize, double cellSize, GH_Path path)
            {
                G = g;
                NxVerts = nxVerts;
                NyVerts = nyVerts;
                Plane = plane;
                CenterXY = centerXY;
                FrameSize = frameSize;
                CellSize = cellSize;
                Path = path;
            }
        }
    }
}
