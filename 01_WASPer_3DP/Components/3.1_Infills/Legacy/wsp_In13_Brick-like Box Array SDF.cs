#region Component Description
/*
    Component:
        wsp_In13_Brick-like Box Array SDF

    Purpose:
        Generates hollow-brick-style rectangular cavity layouts inside a Rhino Box
        using an analytic SDF and Marching Cubes extraction.

    Field convention:
        value < 0 = extracted phase
        value = 0 = generated surface
        value > 0 = outside extracted phase

    Normal mode:
        invert = false outputs the solid phase:
            external shell + internal ribs

    Inverted mode:
        invert = true outputs the complementary cavity / void phase.

    Notes:
        - count_u and count_v are cavity counts, not rib counts.
        - cav_dir controls the cavity run direction:
            1 = Z, grid plane XY
            2 = X, grid plane YZ
            3 = Y, grid plane XZ
        - shell_caps=false removes the shell faces perpendicular to cav_dir, so
          the cavities become open channels along cav_dir.
        - trim_geo is applied at the end, so it clips both shell and ribs.
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
#endregion

namespace WASPer_3DP.Components._3_1_Infills
{
    public sealed class wsp_In13_Brick_like_Box_Array_SDF : GH_Component
    {
        private const double EPS = 1e-9;
        private const double TRIM = 1e6;
        private const int MIN_FRAGMENT_FACES = 8;

        private readonly string _versionTag;

        public wsp_In13_Brick_like_Box_Array_SDF()
            : base(
                "wsp_In13_Brick-like Box Array SDF",
                "Brick_Box_SDF",
                "Generates hollow-brick-style rectangular cavity layouts inside a Rhino Box. " +
                "The component creates an external shell and internal rib network, supports " +
                "cavity direction along X/Y/Z, optional trim geometry, shell caps, field " +
                "inversion, disjoint mesh splitting, and SDF/Marching Cubes extraction.",
                global::WASPer_3DP.WASPerPalette.DesignFabrication,
                "3.1_Infills")
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid => new Guid("461B74DB-FEF6-4EB7-A390-4324F3C250D7");

        public override GH_Exposure Exposure => GH_Exposure.hidden;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_In13_Brick_like_Box_Array_SDF.png"))
                    {
                        return s != null ? new Bitmap(s) : null;
                    }
                }
                catch { return null; }
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBoxParameter(
                "box",
                "box",
                "Rhino Box defining the brick-like SDF domain. The box dimensions define the total component size.",
                GH_ParamAccess.item);

            pManager.AddGenericParameter(
                "trim_geo",
                "trim",
                "Optional clipping volume applied at the end of the SDF. Accepted: Box, closed Brep, closed Mesh, or closed Extrusion/Brep geometry.",
                GH_ParamAccess.item);
            pManager[1].Optional = true;

            pManager.AddNumberParameter(
                "inf_t",
                "inf_t",
                "Internal rib / partition thickness in Rhino model units. Default = 7.0.",
                GH_ParamAccess.item,
                7.0);

            pManager.AddNumberParameter(
                "shell_t",
                "shell_t",
                "External boundary shell thickness in Rhino model units. Default = 7.0.",
                GH_ParamAccess.item,
                7.0);

            pManager.AddBooleanParameter(
                "shell_caps",
                "caps",
                "If true, shell is generated on all six box faces. If false, shell faces perpendicular to cav_dir are removed, leaving open channels.",
                GH_ParamAccess.item,
                false);

            pManager.AddBooleanParameter(
                "invert",
                "invert",
                "False outputs the solid phase (shell + ribs). True outputs the complementary cavity/void phase.",
                GH_ParamAccess.item,
                false);

            pManager.AddBooleanParameter(
                "disjoin",
                "disjoin",
                "If true, split disconnected mesh islands and remove very small fragments after mesh generation.",
                GH_ParamAccess.item,
                false);

            pManager.AddIntegerParameter(
                "count_u",
                "count_u",
                "Number of cavities in the first direction perpendicular to cav_dir. This is cavity count, not rib count. Default = 3.",
                GH_ParamAccess.item,
                3);

            pManager.AddIntegerParameter(
                "count_v",
                "count_v",
                "Number of cavities in the second direction perpendicular to cav_dir. This is cavity count, not rib count. Default = 2.",
                GH_ParamAccess.item,
                2);

            pManager.AddIntegerParameter(
                "cav_dir",
                "cav_dir",
                "Cavity run direction: 1=Z (grid XY), 2=X (grid YZ), 3=Y (grid XZ). Default = 1.",
                GH_ParamAccess.item,
                1);

            pManager.AddNumberParameter(
                "res",
                "res",
                "Voxel size in Rhino model units for SDF/Marching Cubes extraction. Default = 2.0.",
                GH_ParamAccess.item,
                2.0);

            pManager.AddBooleanParameter(
                "out_mesh",
                "mesh?",
                "True generates a mesh. False skips Marching Cubes and outputs only the SDF field.",
                GH_ParamAccess.item,
                true);

            for (int i = 2; i < pManager.ParamCount; i++)
                pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "mesh_out",
                "mesh",
                "Generated mesh. invert=false outputs solid shell+ribs; invert=true outputs cavity/void phase. If disjoin=true, disconnected islands are output separately.",
                GH_ParamAccess.list);

            pManager.AddGenericParameter(
                "field",
                "field",
                "Signed distance field output. value < 0 is the extracted phase, value = 0 is the generated surface, value > 0 is outside.",
                GH_ParamAccess.list);

            pManager.AddBrepParameter(
                "bound_geo",
                "bound",
                "Brep representation of the input Box domain.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "array",
                "array",
                "Text summary of the cavity array, for example U=3 | V=2 | dir=Z.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "cavity_size",
                "cavity",
                "Computed cavity sizes as [cav_u, cav_v, cav_length] in Rhino model units.",
                GH_ParamAccess.list);

            pManager.AddNumberParameter(
                "rib_t",
                "rib_t",
                "Effective internal rib thickness in Rhino model units.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "shell_t_out",
                "shell_t",
                "Effective external shell thickness in Rhino model units.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "solid_vol_est",
                "solid_vol",
                "Estimated untrimmed solid volume in model units cubed.",
                GH_ParamAccess.item);

            pManager.AddNumberParameter(
                "void_vol_est",
                "void_vol",
                "Estimated untrimmed void/cavity volume in model units cubed.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "info",
                "info",
                "Diagnostic information: version, box size, cavity direction, counts, effective sizes, trim state, mesh stats, timings, and warnings.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var timer = Stopwatch.StartNew();

            Box box = Box.Unset;
            object trimGeoRaw = null;
            double infT = 7.0;
            double shellT = 7.0;
            bool shellCaps = true;
            bool invert = false;
            bool disjoin = false;
            int countU = 3;
            int countV = 2;
            int cavDir = 1;
            double res = 2.0;
            bool outMesh = true;

            if (!DA.GetData(0, ref box) || !box.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "A valid Rhino Box is required.");
                return;
            }

            DA.GetData(1, ref trimGeoRaw);
            DA.GetData(2, ref infT);
            DA.GetData(3, ref shellT);
            DA.GetData(4, ref shellCaps);
            DA.GetData(5, ref invert);
            DA.GetData(6, ref disjoin);
            DA.GetData(7, ref countU);
            DA.GetData(8, ref countV);
            DA.GetData(9, ref cavDir);
            DA.GetData(10, ref res);
            DA.GetData(11, ref outMesh);

            var warnings = new List<string>();

            if (cavDir < 1 || cavDir > 3)
            {
                warnings.Add("Invalid cav_dir. Using default cav_dir = 1 = Z.");
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, warnings[warnings.Count - 1]);
                cavDir = 1;
            }

            int originalCountU = countU;
            int originalCountV = countV;
            countU = Math.Max(1, countU);
            countV = Math.Max(1, countV);
            if (countU != originalCountU || countV != originalCountV)
            {
                warnings.Add("count_u and count_v were clamped to at least 1.");
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, warnings[warnings.Count - 1]);
            }

            infT = Math.Max(0.0, infT);
            shellT = Math.Max(0.0, shellT);
            res = Math.Max(res, RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6);

            var dims = BoxDimensions.FromBox(box);
            if (!dims.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Input box has invalid or zero dimensions.");
                return;
            }

            var map = AxisMap.FromCavDir(cavDir);
            double sizeU = dims.Size(map.U);
            double sizeV = dims.Size(map.V);
            double sizeW = dims.Size(map.W);

            double innerU = sizeU - 2.0 * shellT;
            double innerV = sizeV - 2.0 * shellT;
            double cavU = (innerU - (countU - 1) * infT) / countU;
            double cavV = (innerV - (countV - 1) * infT) / countV;
            double cavLength = shellCaps ? sizeW - 2.0 * shellT : sizeW;

            if (cavU <= EPS || cavV <= EPS || cavLength <= EPS)
            {
                string msg = "Invalid cavity layout. shell_t and/or inf_t are too large for the selected box size, cavity direction, and cavity counts.";
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, msg);
                DA.SetData(9, msg);
                return;
            }

            double minFeature = MinPositive(infT, shellT);
            if (minFeature > EPS && res > minFeature / 2.0)
            {
                string msg = "Resolution is too coarse for the requested rib/shell thickness. Use res <= min(inf_t, shell_t) / 3, preferably /4.";
                warnings.Add(msg);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, msg);
            }

            if (cavU < res * 2.0 || cavV < res * 2.0)
            {
                string msg = "Cavity size is close to the voxel resolution. Geometry may be poorly resolved.";
                warnings.Add(msg);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, msg);
            }

            TrimVolume trimVolume = BuildTrimVolume(trimGeoRaw);
            if (trimGeoRaw != null && trimVolume == null)
            {
                string msg = "trim_geo ignored. Use Box, closed Brep, closed Mesh, or closed Extrusion.";
                warnings.Add(msg);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, msg);
            }

            BrickSdfData data = new BrickSdfData(box, dims, map, countU, countV, infT, shellT, shellCaps, invert, trimVolume);
            string sourceTrace =
                "Source: In13 Brick-like Box Array SDF\n" +
                $"inf_t={infT:G6}\n" +
                $"shell_t={shellT:G6}\n" +
                $"shell_caps={shellCaps}\n" +
                $"invert={invert}\n" +
                $"counts={countU}x{countV}\n" +
                $"cav_dir={cavDir}\n" +
                $"box_size={dims.SizeX:G6},{dims.SizeY:G6},{dims.SizeZ:G6}\n" +
                $"trim_geo={trimVolume != null}\n" +
                "quality=ApproximateSdf";
            WasperField analyticalField = new WasperField(
                p => EvaluateBrickField(p, data),
                box.BoundingBox,
                "Brick-like Box Array SDF",
                sourceTrace,
                WasperFieldSdfQuality.ApproximateSdf);

            double grossVolume = dims.Volume;
            double voidVolume = Math.Max(0.0, countU * countV * cavU * cavV * cavLength);
            double solidVolume = Math.Max(0.0, grossVolume - voidVolume);
            double porosity = grossVolume > EPS ? Clamp01(voidVolume / grossVolume) : 0.0;

            var resultMeshes = new List<Mesh>();
            int vertices = 0;
            int faces = 0;
            int removedFragments = 0;
            string meshMode = outMesh ? "sdf_marching_cubes" : "field_only";
            long totalSamples = 0;
            string gridReport = "-";
            int threads = 0;
            double sampleMs = 0.0;
            double mcMs = 0.0;
            double cleanupMs = 0.0;

            if (outMesh)
            {
                var sw = Stopwatch.StartNew();
                BuildGrid(box, dims, res, out int nx, out int ny, out int nz, out Point3d[] points, out double[] scalars);
                gridReport = $"{nx}x{ny}x{nz}";
                totalSamples = (long)nx * ny * nz;

                if (totalSamples > 20_000_000)
                {
                    string msg = $"Grid {gridReport} = {totalSamples:N0} samples is too large. Increase resolution.";
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, msg);
                    DA.SetData(9, msg);
                    return;
                }

                threads = Math.Max(1, Environment.ProcessorCount - 1);
                Parallel.For(0, nz, new ParallelOptions { MaxDegreeOfParallelism = threads }, iz =>
                {
                    int zOffset = nx * ny * iz;
                    for (int iy = 0; iy < ny; iy++)
                    {
                        int yOffset = nx * iy;
                        for (int ix = 0; ix < nx; ix++)
                        {
                            int idx = zOffset + yOffset + ix;
                            scalars[idx] = analyticalField.Evaluate(points[idx]);
                        }
                    }
                });
                sampleMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                Mesh raw = WasperMarchingCubes.Extract(
                    scalars,
                    points,
                    nx,
                    ny,
                    nz,
                    0.0,
                    Math.Max(res * 1e-6, 1e-9),
                    threads);
                mcMs = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                if (raw != null && raw.Faces.Count > 0)
                {
                    if (disjoin)
                    {
                        resultMeshes = CleanAndSplitResultMeshes(raw, 180.0, MIN_FRAGMENT_FACES, analyticalField, res * 0.75, out removedFragments);
                    }
                    else
                    {
                        CleanResultMesh(raw, 180.0, MIN_FRAGMENT_FACES, analyticalField, res * 0.75, out removedFragments);
                        if (raw.Faces.Count > 0) resultMeshes.Add(raw);
                    }
                }
                cleanupMs = sw.Elapsed.TotalMilliseconds;

                vertices = resultMeshes.Sum(m => m?.Vertices.Count ?? 0);
                faces = resultMeshes.Sum(m => m?.Faces.Count ?? 0);
            }
            else if (disjoin)
            {
                string msg = "disjoin needs out_mesh=true. Outputting one analytical field instead.";
                warnings.Add(msg);
                AddRuntimeMessage(GH_RuntimeMessageLevel.Remark, msg);
            }

            var fields = BuildFieldOutputs(disjoin, outMesh, resultMeshes, analyticalField, "Brick-like Box Array SDF");
            string arrayText = $"U={countU} | V={countV} | dir={map.WLabel}";
            Brep bound = box.ToBrep();
            timer.Stop();

            string info = BuildInfo(
                _versionTag,
                meshMode,
                dims,
                map,
                countU,
                countV,
                infT,
                shellT,
                shellCaps,
                invert,
                disjoin,
                res,
                trimVolume != null,
                cavU,
                cavV,
                cavLength,
                porosity,
                resultMeshes.Count,
                vertices,
                faces,
                gridReport,
                totalSamples,
                threads,
                removedFragments,
                sampleMs,
                mcMs,
                cleanupMs,
                timer.Elapsed.TotalMilliseconds,
                warnings);

            DA.SetDataList(0, resultMeshes);
            DA.SetDataList(1, fields);
            DA.SetData(2, bound);
            DA.SetData(3, arrayText);
            DA.SetDataList(4, new[] { cavU, cavV, cavLength });
            DA.SetData(5, infT);
            DA.SetData(6, shellT);
            DA.SetData(7, solidVolume);
            DA.SetData(8, voidVolume);
            DA.SetData(9, info);
        }

        private static double EvaluateBrickField(Point3d point, BrickSdfData data)
        {
            double boxSdf = SignedDistanceToBox(point, data.Box, data.Dims);

            LocalPoint local = ToLocal(point, data.Box);
            double shellField = ShellField(local, data);
            double ribField = RibField(local, data);

            double solidField = Math.Min(shellField, ribField);
            solidField = Math.Max(solidField, boxSdf);

            double value = data.Invert
                ? Math.Max(-solidField, boxSdf)
                : solidField;

            if (data.TrimVolume != null)
                value = Math.Max(value, data.TrimVolume.SignedDistance(point));

            return value;
        }

        private static double ShellField(LocalPoint local, BrickSdfData data)
        {
            if (data.ShellT <= EPS)
                return TRIM;

            double boxSdf = SignedDistanceToLocalBox(local, data.Dims);

            if (data.ShellCaps)
            {
                double hx = data.Dims.HalfX - data.ShellT;
                double hy = data.Dims.HalfY - data.ShellT;
                double hz = data.Dims.HalfZ - data.ShellT;

                if (hx <= EPS || hy <= EPS || hz <= EPS)
                    return boxSdf;

                double inner = SdfBox(
                    local.X - data.Dims.MidX,
                    local.Y - data.Dims.MidY,
                    local.Z - data.Dims.MidZ,
                    hx,
                    hy,
                    hz);

                return Math.Max(boxSdf, -inner);
            }

            double u = data.Map.Get(local, data.Map.U) - data.Dims.Min(data.Map.U);
            double v = data.Map.Get(local, data.Map.V) - data.Dims.Min(data.Map.V);
            double w = data.Map.Get(local, data.Map.W) - data.Dims.Min(data.Map.W);

            double halfU = 0.5 * data.Dims.Size(data.Map.U) - data.ShellT;
            double halfV = 0.5 * data.Dims.Size(data.Map.V) - data.ShellT;
            double halfW = 0.5 * data.Dims.Size(data.Map.W) + data.ShellT * 2.0 + EPS;

            if (halfU <= EPS || halfV <= EPS)
                return boxSdf;

            double centerU = 0.5 * data.Dims.Size(data.Map.U);
            double centerV = 0.5 * data.Dims.Size(data.Map.V);
            double centerW = 0.5 * data.Dims.Size(data.Map.W);

            double innerLateral = SdfBox(u - centerU, v - centerV, w - centerW, halfU, halfV, halfW);
            return Math.Max(boxSdf, -innerLateral);
        }

        private static double RibField(LocalPoint local, BrickSdfData data)
        {
            if (data.InfT <= EPS)
                return TRIM;

            double u = data.Map.Get(local, data.Map.U) - data.Dims.Min(data.Map.U);
            double v = data.Map.Get(local, data.Map.V) - data.Dims.Min(data.Map.V);
            double w = data.Map.Get(local, data.Map.W) - data.Dims.Min(data.Map.W);

            double sizeU = data.Dims.Size(data.Map.U);
            double sizeV = data.Dims.Size(data.Map.V);
            double sizeW = data.Dims.Size(data.Map.W);
            double innerU = sizeU - 2.0 * data.ShellT;
            double innerV = sizeV - 2.0 * data.ShellT;

            if (innerU <= EPS || innerV <= EPS)
                return TRIM;

            double cavU = (innerU - (data.CountU - 1) * data.InfT) / data.CountU;
            double cavV = (innerV - (data.CountV - 1) * data.InfT) / data.CountV;
            if (cavU <= EPS || cavV <= EPS)
                return TRIM;

            double field = TRIM;
            double halfRib = 0.5 * data.InfT;
            double centerV = data.ShellT + 0.5 * innerV;
            double centerU = data.ShellT + 0.5 * innerU;
            double centerW = 0.5 * sizeW;
            double halfW = 0.5 * sizeW + EPS;
            double shellOverlap = Math.Max(EPS, Math.Min(data.ShellT, data.InfT));
            double ribHalfU = 0.5 * innerU + shellOverlap;
            double ribHalfV = 0.5 * innerV + shellOverlap;

            for (int i = 1; i < data.CountU; i++)
            {
                double center = data.ShellT + i * cavU + (i - 0.5) * data.InfT;
                double rib = SdfBox(u - center, v - centerV, w - centerW, halfRib, ribHalfV, halfW);
                field = Math.Min(field, rib);
            }

            for (int j = 1; j < data.CountV; j++)
            {
                double center = data.ShellT + j * cavV + (j - 0.5) * data.InfT;
                double rib = SdfBox(u - centerU, v - center, w - centerW, ribHalfU, halfRib, halfW);
                field = Math.Min(field, rib);
            }

            return field;
        }

        private static void BuildGrid(
            Box box,
            BoxDimensions dims,
            double res,
            out int nx,
            out int ny,
            out int nz,
            out Point3d[] points,
            out double[] scalars)
        {
            nx = Math.Max(2, (int)Math.Ceiling(dims.SizeX / res) + 1);
            ny = Math.Max(2, (int)Math.Ceiling(dims.SizeY / res) + 1);
            nz = Math.Max(2, (int)Math.Ceiling(dims.SizeZ / res) + 1);

            points = new Point3d[nx * ny * nz];
            scalars = new double[points.Length];

            for (int iz = 0; iz < nz; iz++)
            {
                double z = dims.MinZ + dims.SizeZ * iz / (nz - 1);
                for (int iy = 0; iy < ny; iy++)
                {
                    double y = dims.MinY + dims.SizeY * iy / (ny - 1);
                    for (int ix = 0; ix < nx; ix++)
                    {
                        double x = dims.MinX + dims.SizeX * ix / (nx - 1);
                        points[Idx(ix, iy, iz, nx, ny)] = box.Plane.PointAt(x, y, z);
                    }
                }
            }
        }

        private static List<Mesh> CleanAndSplitResultMeshes(
            Mesh mesh,
            double weldAngleDeg,
            int minFragFaces,
            WasperField field,
            double normalStep,
            out int removedFragments)
        {
            removedFragments = 0;
            var result = new List<Mesh>();
            if (mesh == null || mesh.Faces.Count == 0) return result;

            mesh.Vertices.CombineIdentical(true, true);
            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();
            mesh.Compact();

            var components = SplitConnectedComponents(mesh, minFragFaces, out removedFragments);
            double weldAngle = RhinoMath.ToRadians(Clamp(weldAngleDeg, 0.0, 180.0));

            foreach (var component in components)
            {
                if (component == null || component.Faces.Count == 0) continue;

                component.Vertices.CombineIdentical(true, true);
                component.Faces.CullDegenerateFaces();
                component.Vertices.CullUnused();
                WasperFieldNormalTools.OrientFacesByFieldGradient(component, field, normalStep);
                component.UnifyNormals();
                WasperFieldNormalTools.OrientFacesByFieldGradient(component, field, normalStep);
                component.Weld(weldAngle);
                component.Normals.ComputeNormals();
                component.Compact();

                if (component.Faces.Count > 0)
                    result.Add(component);
            }

            return result;
        }

        private static List<Mesh> SplitConnectedComponents(Mesh mesh, int minFaces, out int removedFragments)
        {
            removedFragments = 0;
            var result = new List<Mesh>();
            if (mesh == null || mesh.Faces.Count == 0) return result;

            int faceCount = mesh.Faces.Count;
            var v2f = new Dictionary<int, List<int>>();
            for (int fi = 0; fi < faceCount; fi++)
            {
                MeshFace f = mesh.Faces[fi];
                AddFV(v2f, f.A, fi);
                AddFV(v2f, f.B, fi);
                AddFV(v2f, f.C, fi);
                if (f.IsQuad) AddFV(v2f, f.D, fi);
            }

            var visited = new bool[faceCount];
            var queue = new Queue<int>();

            for (int seed = 0; seed < faceCount; seed++)
            {
                if (visited[seed]) continue;

                var compFaces = new List<int>();
                visited[seed] = true;
                queue.Enqueue(seed);

                while (queue.Count > 0)
                {
                    int fi = queue.Dequeue();
                    compFaces.Add(fi);
                    MeshFace f = mesh.Faces[fi];
                    EnqueueNeighbors(f.A);
                    EnqueueNeighbors(f.B);
                    EnqueueNeighbors(f.C);
                    if (f.IsQuad) EnqueueNeighbors(f.D);

                    void EnqueueNeighbors(int vi)
                    {
                        if (!v2f.TryGetValue(vi, out var faces)) return;
                        foreach (int nf in faces)
                        {
                            if (visited[nf]) continue;
                            visited[nf] = true;
                            queue.Enqueue(nf);
                        }
                    }
                }

                if (compFaces.Count < minFaces)
                {
                    removedFragments++;
                    continue;
                }

                var component = new Mesh();
                var vMap = new Dictionary<int, int>();

                int MapVertex(int oldIndex)
                {
                    if (vMap.TryGetValue(oldIndex, out int newIndex)) return newIndex;
                    newIndex = component.Vertices.Add(mesh.Vertices[oldIndex]);
                    vMap[oldIndex] = newIndex;
                    return newIndex;
                }

                foreach (int fi in compFaces)
                {
                    MeshFace f = mesh.Faces[fi];
                    int a = MapVertex(f.A);
                    int b = MapVertex(f.B);
                    int c = MapVertex(f.C);
                    if (f.IsQuad) component.Faces.AddFace(a, b, c, MapVertex(f.D));
                    else component.Faces.AddFace(a, b, c);
                }

                result.Add(component);
            }

            return result;
        }

        private static void CleanResultMesh(
            Mesh mesh,
            double weldAngleDeg,
            int minFragFaces,
            WasperField field,
            double normalStep,
            out int removedFragments)
        {
            removedFragments = 0;
            if (mesh == null || mesh.Faces.Count == 0) return;

            mesh.Vertices.CombineIdentical(true, true);
            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();

            if (minFragFaces > 0)
                RemoveSmallFragmentsInPlace(mesh, minFragFaces, out removedFragments);

            WasperFieldNormalTools.OrientFacesByFieldGradient(mesh, field, normalStep);
            mesh.UnifyNormals();
            WasperFieldNormalTools.OrientFacesByFieldGradient(mesh, field, normalStep);
            mesh.Weld(RhinoMath.ToRadians(Clamp(weldAngleDeg, 0.0, 180.0)));
            mesh.Normals.ComputeNormals();
            mesh.Compact();
        }

        private static void RemoveSmallFragmentsInPlace(Mesh mesh, int minFaces, out int removedFragments)
        {
            removedFragments = 0;
            int faceCount = mesh == null ? 0 : mesh.Faces.Count;
            if (faceCount == 0 || minFaces <= 0) return;

            var v2f = new Dictionary<int, List<int>>();
            for (int fi = 0; fi < faceCount; fi++)
            {
                MeshFace f = mesh.Faces[fi];
                AddFV(v2f, f.A, fi);
                AddFV(v2f, f.B, fi);
                AddFV(v2f, f.C, fi);
                if (f.IsQuad) AddFV(v2f, f.D, fi);
            }

            bool[] visited = new bool[faceCount];
            bool[] keep = new bool[faceCount];
            var queue = new Queue<int>();

            for (int seed = 0; seed < faceCount; seed++)
            {
                if (visited[seed]) continue;

                var component = new List<int>();
                visited[seed] = true;
                queue.Enqueue(seed);

                while (queue.Count > 0)
                {
                    int fi = queue.Dequeue();
                    component.Add(fi);
                    MeshFace f = mesh.Faces[fi];
                    VisitFaceVertex(f.A);
                    VisitFaceVertex(f.B);
                    VisitFaceVertex(f.C);
                    if (f.IsQuad) VisitFaceVertex(f.D);

                    void VisitFaceVertex(int vi)
                    {
                        if (!v2f.TryGetValue(vi, out var faces)) return;
                        for (int k = 0; k < faces.Count; k++)
                        {
                            int nf = faces[k];
                            if (visited[nf]) continue;
                            visited[nf] = true;
                            queue.Enqueue(nf);
                        }
                    }
                }

                bool shouldKeep = component.Count >= minFaces;
                if (!shouldKeep) removedFragments++;
                foreach (int fi in component) keep[fi] = shouldKeep;
            }

            if (removedFragments == 0) return;

            var clean = new Mesh();
            var map = new Dictionary<int, int>();
            int MapVertex(int oldIndex)
            {
                if (map.TryGetValue(oldIndex, out int idx)) return idx;
                idx = clean.Vertices.Add(mesh.Vertices[oldIndex]);
                map[oldIndex] = idx;
                return idx;
            }

            for (int fi = 0; fi < faceCount; fi++)
            {
                if (!keep[fi]) continue;
                MeshFace f = mesh.Faces[fi];
                if (f.IsQuad)
                    clean.Faces.AddFace(MapVertex(f.A), MapVertex(f.B), MapVertex(f.C), MapVertex(f.D));
                else
                    clean.Faces.AddFace(MapVertex(f.A), MapVertex(f.B), MapVertex(f.C));
            }

            mesh.Vertices.Clear();
            mesh.Faces.Clear();
            mesh.Append(clean);
            mesh.Vertices.CullUnused();
        }

        private static void AddFV(Dictionary<int, List<int>> map, int vertex, int face)
        {
            if (!map.TryGetValue(vertex, out var faces))
            {
                faces = new List<int>();
                map[vertex] = faces;
            }
            faces.Add(face);
        }

        private static List<WasperFieldGoo> BuildFieldOutputs(
            bool disjoin,
            bool outMesh,
            List<Mesh> resultMeshes,
            WasperField analyticalField,
            string label)
        {
            var fields = new List<WasperFieldGoo>();

            if (disjoin && outMesh && resultMeshes != null && resultMeshes.Count > 0 && analyticalField != null)
            {
                for (int i = 0; i < resultMeshes.Count; i++)
                {
                    BoundingBox islandBB = resultMeshes[i].GetBoundingBox(true);
                    islandBB.Inflate(islandBB.Diagonal.Length * 0.005);

                    WasperField capturedField = analyticalField;
                    BoundingBox capturedBB = islandBB;
                    fields.Add(new WasperFieldGoo(new WasperField(
                        p => capturedBB.Contains(p) ? capturedField.Evaluate(p) : double.PositiveInfinity,
                        capturedBB,
                        $"{label}_{i + 1}",
                        capturedField.OperationTrace + Environment.NewLine + $"1. IslandClip(index={i + 1}) | quality={capturedField.SdfQuality}",
                        capturedField.SdfQuality,
                        capturedField.OperationCount + 1,
                        capturedField.CurveThickenCount)));
                }
            }

            if (fields.Count == 0 && analyticalField != null)
                fields.Add(new WasperFieldGoo(analyticalField));

            return fields;
        }

        private static TrimVolume BuildTrimVolume(object geometry)
        {
            if (geometry == null) return null;

            if (geometry is IGH_Goo goo)
            {
                object sv = goo.ScriptVariable();
                if (sv != null && !ReferenceEquals(sv, geometry))
                    return BuildTrimVolume(sv);
            }

            if (geometry is Box box)
                return box.IsValid ? new TrimVolume(box) : null;

            if (geometry is GeometryBase gb)
                return BuildTrimVolumeFromGeometry(gb);

            return null;
        }

        private static TrimVolume BuildTrimVolumeFromGeometry(GeometryBase geometry)
        {
            if (geometry is Brep brep)
            {
                Brep b = brep.DuplicateBrep();
                return b != null && b.IsValid && b.IsSolid ? new TrimVolume(b, null) : null;
            }

            if (geometry is Extrusion extrusion)
            {
                Brep b = extrusion.ToBrep();
                return b != null && b.IsValid && b.IsSolid ? new TrimVolume(b, null) : null;
            }

            if (geometry is Mesh mesh)
            {
                Mesh m = mesh.DuplicateMesh();
                return m != null && m.IsValid && m.IsClosed ? new TrimVolume(null, m) : null;
            }

            return null;
        }

        private static string BuildInfo(
            string version,
            string meshMode,
            BoxDimensions dims,
            AxisMap map,
            int countU,
            int countV,
            double infT,
            double shellT,
            bool shellCaps,
            bool invert,
            bool disjoin,
            double res,
            bool hasTrim,
            double cavU,
            double cavV,
            double cavLength,
            double porosity,
            int meshCount,
            int vertices,
            int faces,
            string gridReport,
            long totalSamples,
            int threads,
            int removedFragments,
            double sampleMs,
            double mcMs,
            double cleanupMs,
            double totalMs,
            List<string> warnings)
        {
            return
                $"wsp_In13_Brick-like Box Array SDF {version}\n" +
                $"mesh_mode       : {meshMode}\n" +
                $"box_size        : X={dims.SizeX:0.###}, Y={dims.SizeY:0.###}, Z={dims.SizeZ:0.###}\n" +
                $"cav_dir         : {map.WLabel}\n" +
                $"grid_plane      : U={map.ULabel}, V={map.VLabel}\n" +
                $"count_u         : {countU}\n" +
                $"count_v         : {countV}\n" +
                $"inf_t           : {infT:0.###}\n" +
                $"shell_t         : {shellT:0.###}\n" +
                $"shell_caps      : {shellCaps}\n" +
                $"invert          : {invert}\n" +
                $"disjoin         : {disjoin}\n" +
                $"resolution      : {res:0.###}\n" +
                $"trim_geo        : {hasTrim}\n" +
                $"cav_u           : {cavU:0.###}\n" +
                $"cav_v           : {cavV:0.###}\n" +
                $"cav_length      : {cavLength:0.###}\n" +
                $"estimated_phi   : {porosity:0.###}\n" +
                $"output_meshes   : {meshCount:N0}\n" +
                $"mesh vertices   : {vertices:N0}\n" +
                $"mesh faces      : {faces:N0}\n" +
                $"grid            : {gridReport} ({totalSamples:N0} samples)\n" +
                $"threads         : {threads}\n" +
                $"removed_frags   : {removedFragments:N0}\n" +
                $"timing          : sample={sampleMs:0.#}ms | mc={mcMs:0.#}ms | cleanup={cleanupMs:0.#}ms | total={totalMs:0.#}ms\n" +
                (warnings != null && warnings.Count > 0
                    ? "warnings        : " + string.Join(" | ", warnings) + "\n"
                    : "warnings        : none\n");
        }

        private static Point3d BoxPointAtNormalized(Box box, double u, double v, double w)
        {
            double x = box.X.T0 + (box.X.T1 - box.X.T0) * u;
            double y = box.Y.T0 + (box.Y.T1 - box.Y.T0) * v;
            double z = box.Z.T0 + (box.Z.T1 - box.Z.T0) * w;
            return box.Plane.PointAt(x, y, z);
        }

        private static LocalPoint ToLocal(Point3d point, Box box)
        {
            Vector3d v = point - box.Plane.Origin;
            return new LocalPoint(v * box.Plane.XAxis, v * box.Plane.YAxis, v * box.Plane.ZAxis);
        }

        private static double SignedDistanceToBox(Point3d point, Box box, BoxDimensions dims)
        {
            return SignedDistanceToLocalBox(ToLocal(point, box), dims);
        }

        private static double SignedDistanceToLocalBox(LocalPoint p, BoxDimensions dims)
        {
            return SdfBox(p.X - dims.MidX, p.Y - dims.MidY, p.Z - dims.MidZ, dims.HalfX, dims.HalfY, dims.HalfZ);
        }

        private static double SdfBox(double x, double y, double z, double hx, double hy, double hz)
        {
            double qx = Math.Abs(x) - Math.Max(hx, EPS);
            double qy = Math.Abs(y) - Math.Max(hy, EPS);
            double qz = Math.Abs(z) - Math.Max(hz, EPS);
            double ox = Math.Max(qx, 0.0);
            double oy = Math.Max(qy, 0.0);
            double oz = Math.Max(qz, 0.0);
            return Math.Sqrt(ox * ox + oy * oy + oz * oz) + Math.Min(Math.Max(qx, Math.Max(qy, qz)), 0.0);
        }

        private static int Idx(int ix, int iy, int iz, int nx, int ny)
        {
            return ix + nx * (iy + ny * iz);
        }

        private static double Clamp(double value, double min, double max)
        {
            return value < min ? min : value > max ? max : value;
        }

        private static double Clamp01(double value)
        {
            return Clamp(value, 0.0, 1.0);
        }

        private static double MinPositive(double a, double b)
        {
            bool ap = a > EPS;
            bool bp = b > EPS;
            if (ap && bp) return Math.Min(a, b);
            if (ap) return a;
            if (bp) return b;
            return 0.0;
        }

        private readonly struct LocalPoint
        {
            public readonly double X;
            public readonly double Y;
            public readonly double Z;

            public LocalPoint(double x, double y, double z)
            {
                X = x;
                Y = y;
                Z = z;
            }
        }

        private sealed class BrickSdfData
        {
            public readonly Box Box;
            public readonly BoxDimensions Dims;
            public readonly AxisMap Map;
            public readonly int CountU;
            public readonly int CountV;
            public readonly double InfT;
            public readonly double ShellT;
            public readonly bool ShellCaps;
            public readonly bool Invert;
            public readonly TrimVolume TrimVolume;

            public BrickSdfData(Box box, BoxDimensions dims, AxisMap map, int countU, int countV, double infT, double shellT, bool shellCaps, bool invert, TrimVolume trimVolume)
            {
                Box = box;
                Dims = dims;
                Map = map;
                CountU = countU;
                CountV = countV;
                InfT = infT;
                ShellT = shellT;
                ShellCaps = shellCaps;
                Invert = invert;
                TrimVolume = trimVolume;
            }
        }

        private readonly struct BoxDimensions
        {
            public readonly double MinX;
            public readonly double MaxX;
            public readonly double MinY;
            public readonly double MaxY;
            public readonly double MinZ;
            public readonly double MaxZ;
            public readonly double SizeX;
            public readonly double SizeY;
            public readonly double SizeZ;
            public readonly double MidX;
            public readonly double MidY;
            public readonly double MidZ;
            public readonly double HalfX;
            public readonly double HalfY;
            public readonly double HalfZ;
            public readonly double Volume;
            public readonly bool IsValid;

            private BoxDimensions(Box box)
            {
                MinX = Math.Min(box.X.T0, box.X.T1);
                MaxX = Math.Max(box.X.T0, box.X.T1);
                MinY = Math.Min(box.Y.T0, box.Y.T1);
                MaxY = Math.Max(box.Y.T0, box.Y.T1);
                MinZ = Math.Min(box.Z.T0, box.Z.T1);
                MaxZ = Math.Max(box.Z.T0, box.Z.T1);
                SizeX = MaxX - MinX;
                SizeY = MaxY - MinY;
                SizeZ = MaxZ - MinZ;
                MidX = 0.5 * (MinX + MaxX);
                MidY = 0.5 * (MinY + MaxY);
                MidZ = 0.5 * (MinZ + MaxZ);
                HalfX = 0.5 * SizeX;
                HalfY = 0.5 * SizeY;
                HalfZ = 0.5 * SizeZ;
                Volume = SizeX * SizeY * SizeZ;
                IsValid = box.IsValid && SizeX > EPS && SizeY > EPS && SizeZ > EPS;
            }

            public static BoxDimensions FromBox(Box box)
            {
                return new BoxDimensions(box);
            }

            public double Min(int axis)
            {
                return axis == 0 ? MinX : axis == 1 ? MinY : MinZ;
            }

            public double Size(int axis)
            {
                return axis == 0 ? SizeX : axis == 1 ? SizeY : SizeZ;
            }
        }

        private readonly struct AxisMap
        {
            public readonly int U;
            public readonly int V;
            public readonly int W;
            public readonly string ULabel;
            public readonly string VLabel;
            public readonly string WLabel;

            private AxisMap(int u, int v, int w, string uLabel, string vLabel, string wLabel)
            {
                U = u;
                V = v;
                W = w;
                ULabel = uLabel;
                VLabel = vLabel;
                WLabel = wLabel;
            }

            public static AxisMap FromCavDir(int cavDir)
            {
                if (cavDir == 2) return new AxisMap(1, 2, 0, "Y", "Z", "X");
                if (cavDir == 3) return new AxisMap(0, 2, 1, "X", "Z", "Y");
                return new AxisMap(0, 1, 2, "X", "Y", "Z");
            }

            public double Get(LocalPoint p, int axis)
            {
                return axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;
            }
        }

        private sealed class TrimVolume
        {
            private readonly bool _hasBox;
            private readonly Box _box;
            private readonly Brep _brep;
            private readonly Mesh _mesh;

            public TrimVolume(Brep brep, Mesh mesh)
            {
                _hasBox = false;
                _box = Box.Unset;
                _brep = brep;
                _mesh = mesh;
            }

            public TrimVolume(Box box)
            {
                _hasBox = true;
                _box = box;
                _brep = null;
                _mesh = null;
            }

            public double SignedDistance(Point3d point)
            {
                if (_hasBox) return SignedDistanceToBox(point, _box);
                if (_brep != null) return SignedDistanceToBrep(point, _brep);
                if (_mesh != null) return SignedDistanceToMesh(point, _mesh);
                return TRIM;
            }

            private static double SignedDistanceToBox(Point3d point, Box box)
            {
                if (!box.IsValid) return TRIM;
                BoxDimensions dims = BoxDimensions.FromBox(box);
                return dims.IsValid ? wsp_In13_Brick_like_Box_Array_SDF.SignedDistanceToBox(point, box, dims) : TRIM;
            }

            private static double SignedDistanceToBrep(Point3d point, Brep brep)
            {
                if (brep == null || !brep.IsValid || !brep.IsSolid) return TRIM;
                double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
                bool ok = brep.ClosestPoint(point, out Point3d closest, out _, out _, out _, double.MaxValue, out _);
                if (!ok || !closest.IsValid) return TRIM;
                double d = point.DistanceTo(closest);
                return brep.IsPointInside(point, tol, true) ? -d : d;
            }

            private static double SignedDistanceToMesh(Point3d point, Mesh mesh)
            {
                if (mesh == null || !mesh.IsValid || !mesh.IsClosed) return TRIM;
                double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
                MeshPoint mp = mesh.ClosestMeshPoint(point, double.MaxValue);
                if (mp == null) return TRIM;
                double d = point.DistanceTo(mesh.PointAt(mp));
                return mesh.IsPointInside(point, tol, true) ? -d : d;
            }
        }
    }
}
