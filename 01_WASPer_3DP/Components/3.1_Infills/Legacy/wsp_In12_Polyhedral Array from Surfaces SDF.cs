#region Component Description
/*
    Component Name:
        wsp_In12_Polyhedral Array from Surfaces SDF

    Nickname:
        Poly_Surfs_SDF

    Version:
        v1.0.0 - 260505

    Category / Subcategory:
        WASPer_3DP / 2_Infills

    Description:
        Generates a polyhedral lattice mesh between N surfaces (>= 2).
        Consecutive surface pairs define independent slabs. The polyhedral
        lattice is evaluated inside each slab using that pair's surface UV
        as in-plane (U,V) coordinates and the local between-surface
        coordinate as the through-thickness (N) coordinate.

        Architecture: curvilinear UVT parametric grid (same as wsp_In09).
        Grid points: P = pA + t*(pB-pA), so domain containment is structural.

        Two mesh modes:
          - explicit_faces      : thickness=0, shell_thickness=0, trim_geo=null.
            Direct polygon faces from known cell geometry — no SDF, no voxelisation.
            Fast, clean planar faces per slab.
          - sdf_marching_cubes  : thickness>0, shell present, or trim_geo set.
            Polyhedral SDF + Marching Cubes in the curvilinear UVT domain.
            countN repeats are applied per slab (same convention as wsp_In09).

        Cell types (same as wsp_In11):
          0  Truncated Octahedron  BCC lattice  14 faces
          1  Octahedron            SC lattice    8 faces

        Surfaces pre-processing (same as wsp_In09):
          - UV normalised to [0,1].
          - Normals auto-oriented through the surface stack.
          - UV axes alignment checked; warning if > 45 deg misalignment.
          - UvPairMap auto-aligns consecutive surface pairs.
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
#endregion

namespace WASPer_3DP.Components._3_1_Infills
{
    public sealed class wsp_In12_Polyhedral_Array_from_Surfaces_SDF : GH_Component
    {
        private const string NAME   = "wsp_In12_Polyhedral Array from Surfaces SDF";
        private const string NICK   = "Poly_Surfs_SDF";
        private const string CAT    = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "3.1_Infills";

        private const double TRIM      = 1e9;
        private const double EPS       = 1e-10;
        private const double SDF_PHASE_NUDGE = 1e-7;
        private const double INV_SQRT3 = 0.5773502691896258;
        private const double TWO_PI    = 2.0 * Math.PI;
        private const int    BOUNDARY_SAMPLE_COUNT = 128;

        private readonly string _versionTag;

        public wsp_In12_Polyhedral_Array_from_Surfaces_SDF()
            : base(NAME, NICK,
                "Generates a polyhedral lattice mesh between N surfaces (>= 2).\n" +
                "thickness=0: explicit face mesh (fast, planar).  thickness>0: SDF/Marching Cubes.\n" +
                "Cell types: 0=Truncated Octahedron (BCC), 1=Octahedron (SC).\n" +
                "Uses signed distance fields, following the same general SDF approach as Isopod.",
                CAT, SUBCAT)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v   = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.0";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("C2D3E4F5-A6B7-8901-BCDE-F12345678902");

        public override GH_Exposure Exposure => GH_Exposure.hidden;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_In12_Polyhedral Array from Surfaces SDF.png"))
                        return s != null ? new Bitmap(s) : null;
                }
                catch { }
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Parameters
        // ─────────────────────────────────────────────────────────────────────

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGeometryParameter(
                "surfaces", "surfs",
                "List of >= 2 surfaces or single-face Breps, ordered from first to last boundary.\n" +
                "Consecutive pairs define independent slabs.",
                GH_ParamAccess.list);

            pManager.AddGenericParameter(
                "trim_geo", "trim",
                "Optional Box or closed Brep/Mesh/Extrusion as SDF clipping volume (forces SDF mode).",
                GH_ParamAccess.item);

            pManager.AddIntegerParameter(
                "type", "type",
                "Cell type: 0=Truncated Octahedron (BCC), 1=Octahedron (SC). Default: 1.",
                GH_ParamAccess.item, 1);

            pManager.AddNumberParameter(
                "thickness", "inf_t",
                "Face thickness in Rhino model units. 0=explicit face mesh; >0=SDF/Marching Cubes.",
                GH_ParamAccess.item, 2.0);

            pManager.AddNumberParameter(
                "shell_thickness", "shell_t",
                "Inward boundary shell thickness in model units. 0=no shell.",
                GH_ParamAccess.item, 0);

            pManager.AddBooleanParameter(
                "shell_caps", "caps",
                "True removes the top/bottom cap faces from the shell.",
                GH_ParamAccess.item, true);

            pManager.AddBooleanParameter(
                "invert_field", "invert",
                "Invert the field sign (SDF mode only).",
                GH_ParamAccess.item, false);

            pManager.AddBooleanParameter(
                "disjoin_mesh", "disjoin",
                "True splits disconnected mesh islands into separate output mesh items. False outputs one joined mesh item.",
                GH_ParamAccess.item, false);

            pManager.AddBooleanParameter(
                "transpose", "trans",
                "Swap the in-plane UV axes.",
                GH_ParamAccess.item, false);

            pManager.AddBooleanParameter(
                "flip", "flip",
                "Flip the through-thickness direction.",
                GH_ParamAccess.item, false);

            pManager.AddIntegerParameter("count_u", "cu", "Cell repetitions along surface U.", GH_ParamAccess.item, 3);
            pManager.AddIntegerParameter("count_v", "cv", "Cell repetitions along surface V.", GH_ParamAccess.item, 3);
            pManager.AddIntegerParameter("count_n", "cn", "Cell repetitions through thickness (per slab).", GH_ParamAccess.item, 3);

            pManager.AddNumberParameter(
                "resolution", "res",
                "Voxel size in model units (SDF mode only).",
                GH_ParamAccess.item, 2.0);

            pManager.AddBooleanParameter(
                "out_mesh", "mesh?",
                "When true (default), generates and outputs the lattice mesh.\n" +
                "When false, skips mesh generation (no field output available for curvilinear polyhedral mode).",
                GH_ParamAccess.item, true);

            // surfaces required; all others optional
            for (int i = 1; i < pManager.ParamCount; i++)
                pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter("mesh_out", "mesh", "Polyhedral lattice mesh output. If disjoin_mesh is false, outputs one joined mesh item. If disjoin_mesh is true, outputs disconnected islands as separate mesh items.", GH_ParamAccess.list);
            pManager.AddGenericParameter(
                "field", "field",
                "Signed distance field (negative inside, positive outside). " +
                "If disjoin_mesh is true, contains one mesh-derived SDF per disconnected island.",
                GH_ParamAccess.list);
            pManager.AddBrepParameter("bound_geo", "bound", "Closed Brep boundary volume across all surface pairs.", GH_ParamAccess.item);
            pManager.AddTextParameter("cell_name", "cell", "Selected cell type name.", GH_ParamAccess.item);
            pManager.AddTextParameter("array", "array", "Array count as cu.cv.cn.", GH_ParamAccess.item);
            pManager.AddTextParameter("info", "info", "Generation diagnostics and timing.", GH_ParamAccess.item);
        }

        // ─────────────────────────────────────────────────────────────────────
        // SolveInstance
        // ─────────────────────────────────────────────────────────────────────

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var rawList       = new List<IGH_Goo>();
            object trimGeoRaw = null;
            int    type           = 1;
            double thickness      = 0.0;
            double shellThickness = 0.0;
            bool   shellCaps      = true;
            bool   invertField    = false;
            bool   disjoinMesh    = false;
            bool   transpose      = false;
            bool   flip           = false;
            int    countU = 3, countV = 3, countN = 3;
            double res            = 2.0;
            bool   outMesh        = true;
            bool   thicknessUnwired = IsInputUnwired(3);

            if (!DA.GetDataList(0, rawList) || rawList.Count < 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Provide at least 2 surfaces or single-face Breps.");
                DA.SetData(5, "ERR: Need >= 2 surfaces."); Message = "ERR"; return;
            }

            DA.GetData( 1, ref trimGeoRaw);
            DA.GetData( 2, ref type);
            DA.GetData( 3, ref thickness);
            DA.GetData( 4, ref shellThickness);
            DA.GetData( 5, ref shellCaps);
            DA.GetData( 6, ref invertField);
            DA.GetData( 7, ref disjoinMesh);
            DA.GetData( 8, ref transpose);
            DA.GetData( 9, ref flip);
            DA.GetData(10, ref countU);
            DA.GetData(11, ref countV);
            DA.GetData(12, ref countN);
            DA.GetData(13, ref res);
            DA.GetData(14, ref outMesh);

            type           = Clamp(type, 0, 1);
            countU         = Math.Max(1, countU);
            countV         = Math.Max(1, countV);
            countN         = Math.Max(1, countN);
            thickness      = Math.Max(0.0, thickness);
            shellThickness = Math.Max(0.0, shellThickness);
            if (shellThickness > EPS && thicknessUnwired) thickness = shellThickness;

            if (res <= 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "resolution must be > 0.");
                DA.SetData(5, "ERR: resolution must be > 0."); Message = "ERR"; return;
            }

            res = AvoidIntegerResolution(res);

            if (thickness > EPS && res > thickness * 0.5)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "resolution is too coarse for the requested thickness. Use resolution <= thickness / 3.");
            if (shellThickness > EPS && res > shellThickness * 0.5)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "resolution is too coarse for shell_thickness. Use resolution <= shell_thickness / 3.");

            // ── Parse surfaces ────────────────────────────────────────────
            var probeList = new List<SurfProbe>();
            for (int i = 0; i < rawList.Count; i++)
            {
                GeometryBase gb = ExtractGeometry(rawList[i]);
                SurfProbe p = SurfProbe.Wrap(gb);
                if (p == null)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        $"Item [{i}] is not a Surface or single-face Brep.");
                    DA.SetData(5, $"ERR: unsupported geometry at index {i}."); Message = "ERR"; return;
                }
                probeList.Add(p);
            }

            // ── Pre-processing ────────────────────────────────────────────
            var centroids = probeList.Select(p => p.GetCentroid()).ToArray();
            var stackAxis = Vector3d.Zero;
            for (int i = 0; i < centroids.Length - 1; i++) stackAxis += centroids[i+1] - centroids[i];
            if (!stackAxis.Unitize()) stackAxis = Vector3d.ZAxis;

            for (int i = 0; i < probeList.Count; i++)
            {
                if (probeList[i].GetAverageNormal() * stackAxis < 0.0)
                    probeList[i].FlipNormal = true;
            }

            Vector3d refU = probeList[0].GetUAxis();
            for (int i = 1; i < probeList.Count; i++)
            {
                double dot = refU * probeList[i].GetUAxis();
                if (dot < 0.707)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"Surface [{i}] U-axis misaligned with surface [0] (dot={dot:0.00}). Use 'transpose' to correct.");
            }

            var sw = Stopwatch.StartNew();

            var probeArr = probeList.ToArray();
            var capMaps  = BuildCapDomainMaps(probeArr);
            var pairMaps = BuildPairMaps(probeArr, capMaps);
            TrimVolume trimVolume = BuildTrimVolume(trimGeoRaw);
            if (trimGeoRaw != null && trimVolume == null)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "trim_geo ignored. Provide a Box, closed Brep, closed Mesh, or Extrusion.");

            Brep bound = MakeBoundGeoMulti(probeArr);

            // ── Decide mesh mode ──────────────────────────────────────────
            bool useExplicit = thickness <= EPS && shellThickness <= EPS && trimVolume == null;

            List<Mesh> resultMeshes = new List<Mesh>();
            string meshMode   = "mesh skipped";
            string timingInfo = "n/a";
            int removedFragments = 0;

            if (outMesh)
            {
                if (useExplicit)
                {
                    if (invertField)
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                            "invert_field is ignored in explicit face mesh mode (thickness=0).");

                    var explWatch = Stopwatch.StartNew();
                    Mesh explicitMesh = BuildExplicitPolyhedralMeshCurvilinear(
                        probeArr, capMaps, pairMaps, type, countU, countV, countN, transpose);
                    explWatch.Stop();

                    var splitMeshes = CleanAndSplitResultMeshes(explicitMesh, 30.0, 0, out removedFragments);
                    Mesh explicitJoined = JoinMeshList(splitMeshes);
                    resultMeshes = disjoinMesh ? splitMeshes : MeshListFromJoined(explicitJoined);
                    meshMode   = $"explicit_faces  disjoin:{disjoinMesh}  output_meshes:{resultMeshes.Count}  frags_removed:{removedFragments}";
                    timingInfo = $"explicit {explWatch.ElapsedMilliseconds} ms | total {sw.ElapsedMilliseconds} ms";
                }
                else
                {
                    bool useBoundarySdf = thickness > EPS || shellThickness > EPS;
                    int threads = Math.Max(1, Environment.ProcessorCount - 1);

                    Mesh sdfMesh = BuildCurvilinearPolyhedralMesh(
                        probeArr, capMaps, pairMaps,
                        type, thickness, invertField, transpose, flip,
                        countU, countV, countN,
                        res, useBoundarySdf, shellThickness, shellCaps,
                        trimVolume, threads,
                        out long totalSamples, out string gridReport,
                        out long evalMs, out long extractMs);

                    if (totalSamples > 20_000_000)
                    {
                        string msg = $"Grid {gridReport} = {totalSamples:N0} samples too large. Increase resolution.";
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, msg);
                        DA.SetData(5, "ERR: " + msg); Message = "ERR"; return;
                    }

                    int minFragFaces = 8;
                    var splitMeshes = CleanAndSplitResultMeshes(sdfMesh, 30.0, minFragFaces, out removedFragments);
                    Mesh sdfJoined = JoinMeshList(splitMeshes);
                    resultMeshes = disjoinMesh ? splitMeshes : MeshListFromJoined(sdfJoined);
                    meshMode   = $"sdf_marching_cubes  grid:{gridReport}={totalSamples:N0}  threads:{threads}  disjoin:{disjoinMesh}  output_meshes:{resultMeshes.Count}  frags_removed:{removedFragments}";
                    timingInfo = $"eval {evalMs} ms | extract {extractMs} ms | total {sw.ElapsedMilliseconds} ms";
                }
            }

            sw.Stop();
            Mesh joined = JoinMeshList(resultMeshes);

            if (outMesh && (joined == null || joined.Faces.Count == 0))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "No lattice mesh produced. Check inputs.");

            string cell  = PolyhedralTag(type);
            string array = $"{countU}.{countV}.{countN}";
            string sourceTrace =
                "Source: In12 Polyhedral Array from Surfaces SDF\n" +
                $"type={cell} ({type})\n" +
                $"thickness={thickness:G6}\n" +
                $"shell_thickness={shellThickness:G6}\n" +
                $"shell_caps={shellCaps}\n" +
                $"invert={invertField}\n" +
                $"transpose={transpose}\n" +
                $"flip={flip}\n" +
                $"counts={countU}x{countV}x{countN}\n" +
                $"surfaces={probeArr.Length}\n" +
                $"slabs={Math.Max(0, probeArr.Length - 1)}\n" +
                $"trim_geo={trimVolume != null}\n" +
                $"mesh_mode={meshMode}\n" +
                "quality=ApproximateSdf";
            string info  =
                $"{NAME}  {_versionTag}\n" +
                $"mesh_mode       : {meshMode}\n" +
                $"surfaces        : {probeArr.Length}  ({probeArr.Length-1} slab{(probeArr.Length-1 != 1 ? "s" : "")})\n" +
                $"type            : {type} ({cell})\n" +
                $"thickness       : {thickness:0.###}\n" +
                $"shell_thickness : {shellThickness:0.###}\n" +
                $"shell_caps      : {shellCaps}\n" +
                $"invert_field    : {invertField}\n" +
                $"disjoin_mesh    : {disjoinMesh}\n" +
                $"out_mesh        : {outMesh}\n" +
                $"array (per slab): {array}\n" +
                $"resolution      : {res:0.###} model units\n" +
                $"trim_geo        : {trimVolume != null}\n" +
                $"disjoint meshes : {resultMeshes.Count:N0}\n" +
                $"frags removed   : {removedFragments:N0}\n" +
                $"mesh vertices   : {(joined == null ? 0 : joined.Vertices.Count):N0}\n" +
                $"mesh faces      : {(joined == null ? 0 : joined.Faces.Count):N0}\n" +
                $"timing          : {timingInfo}";

            var fieldOutputs = BuildFieldOutputs(
                disjoinMesh,
                outMesh,
                resultMeshes,
                joined,
                cell,
                sourceTrace);

            DA.SetDataList(0, resultMeshes);
            DA.SetDataList(1, fieldOutputs);
            DA.SetData(2, bound);
            DA.SetData(3, cell);
            DA.SetData(4, array);
            DA.SetData(5, info);

            Message = !outMesh
                ? $"{_versionTag} | mesh skipped"
                : joined == null || joined.Faces.Count == 0
                    ? $"{_versionTag} | empty"
                    : $"{_versionTag} | {cell}";
        }

        // ─────────────────────────────────────────────────────────────────────
        // Explicit face mesh — curvilinear domain
        // ─────────────────────────────────────────────────────────────────────

        private static Mesh BuildExplicitPolyhedralMeshCurvilinear(
            SurfProbe[] probes, CapDomainMap[] capMaps, UvPairMap[] pairMaps,
            int type, int countU, int countV, int countN, bool transpose)
        {
            int nSlabs = probes.Length - 1;
            double[] lv;
            int[][] faces;
            GetCellGeometry(type, out lv, out faces);

            var mesh     = new Mesh();
            var faceKeys = new HashSet<FaceKey>();
            double faceMargin = 1e-4;

            // For each slab: cells are placed with countN repeats through that slab.
            // Cell coords cx in [0..countU], cy in [0..countV], cz in [0..countN].
            // Normalised: u = cx/countU, v = cy/countV, t = cz/countN.
            for (int slab = 0; slab < nSlabs; slab++)
            {
                int slabCapture = slab; // for lambda capture

                Func<double, double, double, Point3d> cellToWorld = (cx, cy, cz) =>
                {
                    double u  = Clamp01(cx / countU);
                    double v  = Clamp01(cy / countV);
                    double tl = Clamp01(cz / countN);

                    double fu = transpose ? v : u;
                    double fv = transpose ? u : v;

                    if (!EvaluateMappedCap(probes[slabCapture], capMaps, slabCapture, fu, fv, out Point3d pA, out Vector3d _))
                        return Point3d.Unset;
                    UvPairMap map = GetPairMap(pairMaps, slabCapture);
                    map.MapToB(fu, fv, out double bU, out double bV);
                    if (!EvaluateMappedCap(probes[slabCapture + 1], capMaps, slabCapture + 1, bU, bV, out Point3d pB, out Vector3d _))
                        return Point3d.Unset;

                    return pA + tl * (pB - pA);
                };

                Action<double, double, double> addCell = (cx, cy, cz) =>
                {
                    foreach (int[] faceIdx in faces)
                    {
                        double fcx = 0, fcy = 0, fcz = 0;
                        for (int i = 0; i < faceIdx.Length; i++)
                        {
                            int vi = faceIdx[i];
                            fcx += cx + lv[vi * 3];
                            fcy += cy + lv[vi * 3 + 1];
                            fcz += cz + lv[vi * 3 + 2];
                        }
                        fcx /= faceIdx.Length;
                        fcy /= faceIdx.Length;
                        fcz /= faceIdx.Length;

                        if (fcx < faceMargin || fcx > countU - faceMargin) continue;
                        if (fcy < faceMargin || fcy > countV - faceMargin) continue;
                        if (fcz < faceMargin || fcz > countN - faceMargin) continue;

                        // Compute world-space vertices
                        var wv = new Point3d[faceIdx.Length];
                        bool valid = true;
                        for (int i = 0; i < faceIdx.Length; i++)
                        {
                            int vi = faceIdx[i];
                            wv[i] = cellToWorld(cx + lv[vi * 3], cy + lv[vi * 3 + 1], cz + lv[vi * 3 + 2]);
                            if (!wv[i].IsValid) { valid = false; break; }
                        }
                        if (!valid) continue;

                        FaceKey fk = BuildFaceKeyFromWorld(wv);
                        if (!faceKeys.Add(fk)) continue;

                        int[] vIdx = new int[faceIdx.Length];
                        for (int i = 0; i < faceIdx.Length; i++)
                            vIdx[i] = mesh.Vertices.Add(wv[i]);

                        for (int i = 1; i < vIdx.Length - 1; i++)
                            mesh.Faces.AddFace(vIdx[0], vIdx[i], vIdx[i + 1]);
                    }
                };

                if (type == 0) // Truncated Octahedron BCC
                {
                    for (int i = 0; i < countU; i++)
                    for (int j = 0; j < countV; j++)
                    for (int k = 0; k < countN; k++)
                        addCell(i + 0.5, j + 0.5, k + 0.5);

                    for (int i = 0; i <= countU; i++)
                    for (int j = 0; j <= countV; j++)
                    for (int k = 0; k <= countN; k++)
                        addCell(i, j, k);
                }
                else // Octahedron SC
                {
                    for (int i = 0; i < countU; i++)
                    for (int j = 0; j < countV; j++)
                    for (int k = 0; k < countN; k++)
                        addCell(i + 0.5, j + 0.5, k + 0.5);
                }
            }

            if (mesh.Faces.Count == 0) return null;
            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();
            mesh.Normals.ComputeNormals();
            mesh.FaceNormals.ComputeFaceNormals();
            mesh.Compact();
            return mesh;
        }

        // ─────────────────────────────────────────────────────────────────────
        // SDF / Marching Cubes — curvilinear domain
        // ─────────────────────────────────────────────────────────────────────

        private static Mesh BuildCurvilinearPolyhedralMesh(
            SurfProbe[] probes, CapDomainMap[] capMaps, UvPairMap[] pairMaps,
            int type,
            double thickness, bool invertField,
            bool transpose, bool flip,
            int countU, int countV, int countN,
            double resolution,
            bool useBoundarySdf,
            double shellThickness, bool shellCaps,
            TrimVolume trimVolume,
            int parallelThreads,
            out long totalSamples, out string gridReport,
            out long evalMs, out long extractMs)
        {
            totalSamples = 0; evalMs = 0; extractMs = 0;
            var gridTags = new List<string>();
            var result   = new Mesh();

            if (probes == null || probes.Length < 2)
            { gridReport = "none"; return null; }

            bool hasShell    = shellThickness > EPS;
            bool applyBoundary = useBoundarySdf || thickness > EPS || hasShell;

            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelThreads) };

            for (int slab = 0; slab < probes.Length - 1; slab++)
            {
                EstimateSlabSize(probes, capMaps, pairMaps, slab, out double lenU, out double lenV, out double depth);

                int nx = GridCount(lenU, resolution);
                int ny = GridCount(lenV, resolution);
                int nz = GridCount(depth, resolution);

                long slabSamples = (long)nx * ny * nz;
                totalSamples += slabSamples;
                gridTags.Add($"{nx}x{ny}x{nz}");
                if (totalSamples > 20_000_000) { gridReport = string.Join(" + ", gridTags); return null; }

                // Per-slab gradient scales (approximate cell density per model unit)
                double kU = lenU > EPS ? countU / lenU : 1.0;
                double kV = lenV > EPS ? countV / lenV : 1.0;
                double kN = depth > EPS ? countN / depth : 1.0;

                var scalars  = new double[slabSamples];
                var points   = new Point3d[slabSamples];
                var capA     = new Point3d[nx * ny];
                var capB     = new Point3d[nx * ny];
                var uvValid  = new bool[nx * ny];

                BoundaryFace shellFaceMask = BoundaryFace.All;
                if (hasShell && shellCaps)
                    shellFaceMask = BuildShellFaceMaskForSlab(probes, capMaps, pairMaps, slab);

                // Pre-compute cap surface points
                var evalWatch = Stopwatch.StartNew();
                Parallel.For(0, ny, parallelOptions, iy =>
                {
                    double v = ny <= 1 ? 0.0 : (double)iy / (ny - 1);
                    for (int ix = 0; ix < nx; ix++)
                    {
                        double u     = nx <= 1 ? 0.0 : (double)ix / (nx - 1);
                        int    uvIdx = iy * nx + ix;
                        if (EvaluateSlabCaps(probes, capMaps, pairMaps, slab, u, v, out Point3d pA, out Point3d pB))
                        { capA[uvIdx] = pA; capB[uvIdx] = pB; uvValid[uvIdx] = true; }
                    }
                });

                // Evaluate polyhedral field at every voxel
                Parallel.For(0, nz, parallelOptions, iz =>
                {
                    double t  = nz <= 1 ? 0.0 : (double)iz / (nz - 1);
                    double ft = flip ? 1.0 - t : t;
                    double cz = countN * ft;

                    for (int iy = 0; iy < ny; iy++)
                    {
                        double v = ny <= 1 ? 0.0 : (double)iy / (ny - 1);
                        for (int ix = 0; ix < nx; ix++)
                        {
                            double u     = nx <= 1 ? 0.0 : (double)ix / (nx - 1);
                            int    idx   = Idx(ix, iy, iz, nx, ny);
                            int    uvIdx = iy * nx + ix;

                            if (!uvValid[uvIdx])
                            { points[idx] = Point3d.Unset; scalars[idx] = TRIM; continue; }

                            points[idx] = capA[uvIdx] + t * (capB[uvIdx] - capA[uvIdx]);

                            double fu = transpose ? v : u;
                            double fv = transpose ? u : v;
                            double cx = countU * fu;
                            double cy = countV * fv;

                            // Avoid grid/lattice resonance: some resolutions place
                            // many samples exactly on polyhedral zero planes. Keep
                            // slab boundaries untouched so clipping/caps stay exact.
                            if (u > EPS && u < 1.0 - EPS) cx += SDF_PHASE_NUDGE;
                            if (v > EPS && v < 1.0 - EPS) cy += 2.0 * SDF_PHASE_NUDGE;
                            if (t > EPS && t < 1.0 - EPS) cz += 3.0 * SDF_PHASE_NUDGE;

                            double faceDist = PolyhedralDistanceWorld(type, cx, cy, cz, kU, kV, kN);
                            double effectiveHalfThickness = Math.Max(0.0, thickness * 0.5);
                            double latticeValue = faceDist - effectiveHalfThickness;
                            double boundarySdf = ParametricBoundarySdf(u, v, t, lenU, lenV, depth);

                            // Keep the same field convention as In11:
                            // finalValue < 0 is always the region extracted by Marching Cubes.
                            double materialField = latticeValue;

                            if (applyBoundary)
                                materialField = Math.Max(materialField, boundarySdf);

                            if (hasShell)
                            {
                                double shellBoundarySdf = shellCaps
                                    ? ShellBoundarySdfFromMask(u, v, t, lenU, lenV, depth, shellFaceMask)
                                    : boundarySdf;

                                double shellValue = TRIM;
                                if (shellBoundarySdf < TRIM * 0.1)
                                {
                                    shellValue = InwardShellSdf(shellBoundarySdf, shellThickness);
                                    shellValue = Math.Max(shellValue, boundarySdf);
                                }

                                // Union lattice + inward shell, then clamp the union back to the slab.
                                materialField = Math.Max(Math.Min(latticeValue, shellValue), boundarySdf);
                            }

                            double finalValue;
                            if (!invertField)
                            {
                                finalValue = materialField;
                            }
                            else
                            {
                                // Complement of the lattice/shell material, clipped back to the slab.
                                finalValue = -materialField;
                                finalValue = Math.Max(finalValue, boundarySdf);
                            }

                            if (trimVolume != null)
                                finalValue = Math.Max(finalValue, trimVolume.SignedDistance(points[idx]));

                            scalars[idx] = finalValue;
                        }
                    }
                });
                evalWatch.Stop();
                evalMs += evalWatch.ElapsedMilliseconds;

                var extractWatch = Stopwatch.StartNew();
                Mesh slabMesh = WasperMarchingCubes.Extract(
                    scalars,
                    points,
                    nx,
                    ny,
                    nz,
                    0.0,
                    1e-8,
                    Math.Max(1, parallelThreads),
                    true,
                    TRIM * 0.1);
                extractWatch.Stop();
                extractMs += extractWatch.ElapsedMilliseconds;

                if (slabMesh != null && slabMesh.Faces.Count > 0)
                    result.Append(slabMesh);
            }

            gridReport = string.Join(" + ", gridTags);
            return result.Faces.Count == 0 ? null : result;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Polyhedral SDF field math  (from wsp_In11)
        // ─────────────────────────────────────────────────────────────────────

        private static double PolyhedralDistanceWorld(
            int type, double cx, double cy, double cz,
            double kU, double kV, double kN)
        {
            double diagScale = Math.Sqrt(kU*kU + kV*kV + kN*kN);
            if (diagScale <= EPS) return TRIM;

            if (type == 0)
            {
                double sqDist  = TruncOctaSquareDistWorld(cx, cy, cz, kU, kV, kN);
                double hexDist = TruncOctaHexDistWorld(cx, cy, cz, diagScale);
                return Math.Min(sqDist, hexDist);
            }
            return OctahedronDistWorld(cx, cy, cz, diagScale);
        }

        private static double TruncOctaSquareDistWorld(
            double cx, double cy, double cz,
            double kU, double kV, double kN)
        {
            double dx = HalfIntFrac(cx) / Math.Max(kU, EPS);
            double dy = HalfIntFrac(cy) / Math.Max(kV, EPS);
            double dz = HalfIntFrac(cz) / Math.Max(kN, EPS);
            return Math.Min(dx, Math.Min(dy, dz));
        }

        private static double TruncOctaHexDistWorld(
            double cx, double cy, double cz, double diagScale)
        {
            double d = Math.Min(
                Math.Min(HexPeriodDist(cx+cy+cz), HexPeriodDist(cx+cy-cz)),
                Math.Min(HexPeriodDist(cx-cy+cz), HexPeriodDist(-cx+cy+cz)));
            return d / Math.Max(diagScale, EPS);
        }

        private static double OctahedronDistWorld(
            double cx, double cy, double cz, double diagScale)
        {
            double d = Math.Min(
                Math.Min(HalfIntFrac(cx+cy+cz), HalfIntFrac(cx+cy-cz)),
                Math.Min(HalfIntFrac(cx-cy+cz), HalfIntFrac(-cx+cy+cz)));
            return d / Math.Max(diagScale, EPS);
        }

        private static double HalfIntFrac(double x)
        { double f = x - Math.Floor(x); return Math.Abs(f - 0.5); }

        private static double HexPeriodDist(double s)
        { double f = ((s - 0.75) % 1.5 + 1.5) % 1.5; return Math.Min(f, 1.5 - f); }

        private static string PolyhedralTag(int type)
        { switch (type) { case 0: return "Trunc. Octahedron"; case 1: return "Octahedron"; default: return "?"; } }

        // ─────────────────────────────────────────────────────────────────────
        // Cell geometry  (from wsp_In11)
        // ─────────────────────────────────────────────────────────────────────

        private static void GetCellGeometry(int type, out double[] lv, out int[][] faces)
        {
            if (type == 0) // Truncated Octahedron
            {
                lv = new double[]
                {
                    /*  0 */  0,     0.25,  0.5,
                    /*  1 */  0,    -0.25,  0.5,
                    /*  2 */  0,     0.25, -0.5,
                    /*  3 */  0,    -0.25, -0.5,
                    /*  4 */  0,     0.5,   0.25,
                    /*  5 */  0,    -0.5,   0.25,
                    /*  6 */  0,     0.5,  -0.25,
                    /*  7 */  0,    -0.5,  -0.25,
                    /*  8 */  0.25,  0,     0.5,
                    /*  9 */ -0.25,  0,     0.5,
                    /* 10 */  0.25,  0,    -0.5,
                    /* 11 */ -0.25,  0,    -0.5,
                    /* 12 */  0.5,   0,     0.25,
                    /* 13 */ -0.5,   0,     0.25,
                    /* 14 */  0.5,   0,    -0.25,
                    /* 15 */ -0.5,   0,    -0.25,
                    /* 16 */  0.25,  0.5,   0,
                    /* 17 */ -0.25,  0.5,   0,
                    /* 18 */  0.25, -0.5,   0,
                    /* 19 */ -0.25, -0.5,   0,
                    /* 20 */  0.5,   0.25,  0,
                    /* 21 */ -0.5,   0.25,  0,
                    /* 22 */  0.5,  -0.25,  0,
                    /* 23 */ -0.5,  -0.25,  0,
                };
                faces = BuildFacesFromPlanes(lv, new FacePlane[]
                {
                    new FacePlane( 1, 0, 0,0.5), new FacePlane(-1, 0, 0,0.5),
                    new FacePlane( 0, 1, 0,0.5), new FacePlane( 0,-1, 0,0.5),
                    new FacePlane( 0, 0, 1,0.5), new FacePlane( 0, 0,-1,0.5),
                    new FacePlane( 1, 1, 1,0.75), new FacePlane( 1, 1,-1,0.75),
                    new FacePlane( 1,-1, 1,0.75), new FacePlane(-1, 1, 1,0.75),
                    new FacePlane(-1,-1,-1,0.75), new FacePlane(-1,-1, 1,0.75),
                    new FacePlane(-1, 1,-1,0.75), new FacePlane( 1,-1,-1,0.75),
                }, 1e-9);
            }
            else // Octahedron SC
            {
                lv = new double[]
                {
                    /* 0 */  0.5, 0,   0,
                    /* 1 */ -0.5, 0,   0,
                    /* 2 */  0,   0.5, 0,
                    /* 3 */  0,  -0.5, 0,
                    /* 4 */  0,   0,   0.5,
                    /* 5 */  0,   0,  -0.5,
                };
                faces = BuildFacesFromPlanes(lv, new FacePlane[]
                {
                    new FacePlane( 1, 1, 1,0.5), new FacePlane( 1, 1,-1,0.5),
                    new FacePlane( 1,-1, 1,0.5), new FacePlane( 1,-1,-1,0.5),
                    new FacePlane(-1, 1, 1,0.5), new FacePlane(-1, 1,-1,0.5),
                    new FacePlane(-1,-1, 1,0.5), new FacePlane(-1,-1,-1,0.5),
                }, 1e-9);
            }
        }

        private readonly struct FacePlane
        {
            public readonly double Nx, Ny, Nz, Offset;
            public FacePlane(double nx, double ny, double nz, double offset)
            { Nx=nx; Ny=ny; Nz=nz; Offset=offset; }
        }

        private static int[][] BuildFacesFromPlanes(double[] lv, FacePlane[] planes, double tol)
        {
            var result = new List<int[]>();
            int nv = lv.Length / 3;

            for (int pi = 0; pi < planes.Length; pi++)
            {
                FacePlane fp = planes[pi];
                var ids = new List<int>();
                for (int vi = 0; vi < nv; vi++)
                {
                    double d = fp.Nx*lv[vi*3] + fp.Ny*lv[vi*3+1] + fp.Nz*lv[vi*3+2];
                    if (Math.Abs(d - fp.Offset) <= tol) ids.Add(vi);
                }
                if (ids.Count < 3) continue;

                Point3d center = Point3d.Origin;
                foreach (int vi in ids) center += new Vector3d(lv[vi*3], lv[vi*3+1], lv[vi*3+2]);
                center /= ids.Count;

                Vector3d normal = new Vector3d(fp.Nx, fp.Ny, fp.Nz);
                if (!normal.Unitize()) continue;

                Vector3d axisX = Vector3d.CrossProduct(Vector3d.ZAxis, normal);
                if (!axisX.Unitize()) { axisX = Vector3d.CrossProduct(Vector3d.XAxis, normal); if (!axisX.Unitize()) continue; }
                Vector3d axisY = Vector3d.CrossProduct(normal, axisX);
                if (!axisY.Unitize()) continue;

                // Sort vertex indices by polar angle around face centre
                int[] sortedIds = ids.ToArray();
                Array.Sort(sortedIds, (a, b) =>
                {
                    Vector3d da = new Point3d(lv[a*3], lv[a*3+1], lv[a*3+2]) - center;
                    Vector3d db = new Point3d(lv[b*3], lv[b*3+1], lv[b*3+2]) - center;
                    return Math.Atan2(da*axisY, da*axisX).CompareTo(Math.Atan2(db*axisY, db*axisX));
                });

                // check winding
                Point3d p0 = new Point3d(lv[sortedIds[0]*3], lv[sortedIds[0]*3+1], lv[sortedIds[0]*3+2]);
                Point3d p1 = new Point3d(lv[sortedIds[1]*3], lv[sortedIds[1]*3+1], lv[sortedIds[1]*3+2]);
                Point3d p2 = new Point3d(lv[sortedIds[2]*3], lv[sortedIds[2]*3+1], lv[sortedIds[2]*3+2]);
                Vector3d fn = Vector3d.CrossProduct(p1-p0, p2-p0);
                fn.Unitize();
                if (fn * normal < 0.0) Array.Reverse(sortedIds);

                result.Add(sortedIds);
            }
            return result.ToArray();
        }

        // ─────────────────────────────────────────────────────────────────────
        // Surface probe abstraction  (from wsp_In09 — verbatim)
        // ─────────────────────────────────────────────────────────────────────

        private abstract class SurfProbe
        {
            public bool FlipNormal { get; set; }

            public abstract bool Closest(Point3d P, out double u, out double v, out Point3d C, out Vector3d N);
            public abstract bool EvaluateAt(double u, double v, out Point3d C, out Vector3d N);
            public abstract bool IsInside(Point3d P, double u, double v, Point3d C);
            public abstract Point3d GetCentroid();
            public abstract Vector3d GetAverageNormal();
            public abstract Vector3d GetUAxis();
            public abstract BoundingBox GetBoundingBox();
            public abstract Brep AsCapBrep();
            public abstract List<Curve> BoundaryLoops();

            public static SurfProbe Wrap(GeometryBase geo)
            {
                if (geo is Surface srf) return new SurfProbe_Surface(srf);
                if (geo is Brep brep && brep.Faces.Count == 1) return new SurfProbe_Face(brep.Faces[0]);
                return null;
            }

            protected static double NormU(double u, Interval dom) => dom.Length > EPS ? (u - dom.T0) / dom.Length : 0.5;
            protected static double DenormU(double u01, Interval dom) => dom.T0 + u01 * dom.Length;
        }

        private sealed class SurfProbe_Surface : SurfProbe
        {
            private readonly Surface _srf;
            private readonly Interval _uDom, _vDom;

            public SurfProbe_Surface(Surface srf) { _srf = srf; _uDom = srf.Domain(0); _vDom = srf.Domain(1); }

            public override bool Closest(Point3d P, out double u, out double v, out Point3d C, out Vector3d N)
            {
                u = v = 0; C = Point3d.Unset; N = Vector3d.Unset;
                if (!_srf.ClosestPoint(P, out double ru, out double rv)) return false;
                C = _srf.PointAt(ru, rv); N = _srf.NormalAt(ru, rv);
                if (!N.Unitize()) return false;
                u = NormU(ru, _uDom); v = NormU(rv, _vDom); return true;
            }

            public override bool EvaluateAt(double u, double v, out Point3d C, out Vector3d N)
            {
                double ru = DenormU(Clamp01(u), _uDom); double rv = DenormU(Clamp01(v), _vDom);
                C = _srf.PointAt(ru, rv); N = _srf.NormalAt(ru, rv); return N.Unitize();
            }

            public override bool IsInside(Point3d P, double u, double v, Point3d C)
            {
                const double eps = 1e-6;
                if (u < -eps || u > 1+eps || v < -eps || v > 1+eps) return false;
                double ru = DenormU(u, _uDom); double rv = DenormU(v, _vDom);
                if (!_srf.Evaluate(ru, rv, 1, out Point3d _, out Vector3d[] ders) || ders == null || ders.Length < 2)
                    return u >= -eps && u <= 1+eps && v >= -eps && v <= 1+eps;
                Vector3d du = ders[0]; Vector3d dv = ders[1];
                if (!du.Unitize() || !dv.Unitize()) return true;
                double et = Math.Max((RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6)*2, 1e-6);
                Vector3d off = P - C;
                if (u <= eps && off*du < -et) return false; if (u >= 1-eps && off*du > et) return false;
                if (v <= eps && off*dv < -et) return false; if (v >= 1-eps && off*dv > et) return false;
                return true;
            }

            public override Point3d GetCentroid() => _srf.PointAt(_uDom.Mid, _vDom.Mid);

            public override Vector3d GetAverageNormal()
            {
                var sum = Vector3d.Zero; int n = 0;
                for (int i = 0; i <= 2; i++) for (int j = 0; j <= 2; j++)
                { Vector3d nm = _srf.NormalAt(_uDom.T0+_uDom.Length*i/2.0, _vDom.T0+_vDom.Length*j/2.0); if (nm.Unitize()) { sum+=nm; n++; } }
                if (n==0) return Vector3d.ZAxis; sum.Unitize(); return sum;
            }

            public override Vector3d GetUAxis()
            {
                if (_srf.Evaluate(_uDom.Mid, _vDom.Mid, 1, out Point3d _, out Vector3d[] ders) && ders != null && ders.Length > 0)
                { Vector3d du = ders[0]; if (du.Unitize()) return du; }
                return Vector3d.XAxis;
            }

            public override BoundingBox GetBoundingBox() => _srf.GetBoundingBox(false);
            public override Brep AsCapBrep() => _srf.ToBrep();

            public override List<Curve> BoundaryLoops()
            {
                double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
                var edges = new Curve[] { _srf.IsoCurve(0,_vDom.T0), _srf.IsoCurve(0,_vDom.T1), _srf.IsoCurve(1,_uDom.T0), _srf.IsoCurve(1,_uDom.T1) };
                Curve[] joined = Curve.JoinCurves(edges, tol);
                var list = new List<Curve>();
                if (joined != null) foreach (var c in joined) if (c != null && c.IsClosed) list.Add(c);
                return list;
            }
        }

        private sealed class SurfProbe_Face : SurfProbe
        {
            private readonly BrepFace _face;
            private readonly Interval _uDom, _vDom;

            public SurfProbe_Face(BrepFace face) { _face = face; _uDom = face.Domain(0); _vDom = face.Domain(1); }

            public override bool Closest(Point3d P, out double u, out double v, out Point3d C, out Vector3d N)
            {
                u = v = 0; C = Point3d.Unset; N = Vector3d.Unset;
                if (!_face.ClosestPoint(P, out double ru, out double rv)) return false;
                C = _face.PointAt(ru, rv); N = _face.NormalAt(ru, rv);
                if (!N.Unitize()) return false;
                u = NormU(ru, _uDom); v = NormU(rv, _vDom); return true;
            }

            public override bool EvaluateAt(double u, double v, out Point3d C, out Vector3d N)
            {
                double ru = DenormU(Clamp01(u), _uDom); double rv = DenormU(Clamp01(v), _vDom);
                C = _face.PointAt(ru, rv); N = _face.NormalAt(ru, rv); return N.Unitize();
            }

            public override bool IsInside(Point3d P, double u, double v, Point3d C)
            {
                double ru = DenormU(u, _uDom); double rv = DenormU(v, _vDom);
                PointFaceRelation r = _face.IsPointOnFace(ru, rv);
                return r == PointFaceRelation.Interior || r == PointFaceRelation.Boundary;
            }

            public override Point3d GetCentroid() => _face.PointAt(_uDom.Mid, _vDom.Mid);

            public override Vector3d GetAverageNormal()
            {
                var sum = Vector3d.Zero; int n = 0;
                for (int i = 0; i <= 2; i++) for (int j = 0; j <= 2; j++)
                { Vector3d nm = _face.NormalAt(_uDom.T0+_uDom.Length*i/2.0, _vDom.T0+_vDom.Length*j/2.0); if (nm.Unitize()) { sum+=nm; n++; } }
                if (n==0) return Vector3d.ZAxis; sum.Unitize(); return sum;
            }

            public override Vector3d GetUAxis()
            {
                if (_face.Evaluate(_uDom.Mid, _vDom.Mid, 1, out Point3d _, out Vector3d[] ders) && ders != null && ders.Length > 0)
                { Vector3d du = ders[0]; if (du.Unitize()) return du; }
                return Vector3d.XAxis;
            }

            public override BoundingBox GetBoundingBox() => _face.GetBoundingBox(false);
            public override Brep AsCapBrep() => _face.DuplicateFace(true);

            public override List<Curve> BoundaryLoops()
            {
                double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
                var loops = new List<Curve>();
                foreach (BrepLoop loop in _face.Loops)
                {
                    var segs = new List<Curve>();
                    foreach (BrepTrim trim in loop.Trims)
                    {
                        Curve seg = null;
                        if (trim.Edge != null) seg = trim.Edge.DuplicateCurve();
                        else switch (trim.IsoStatus)
                        {
                            case IsoStatus.North: seg = _face.IsoCurve(0, _vDom.T1); break;
                            case IsoStatus.South: seg = _face.IsoCurve(0, _vDom.T0); break;
                            case IsoStatus.East:  seg = _face.IsoCurve(1, _uDom.T1); break;
                            case IsoStatus.West:  seg = _face.IsoCurve(1, _uDom.T0); break;
                        }
                        if (seg != null) segs.Add(seg);
                    }
                    if (segs.Count > 0)
                    {
                        Curve[] j = Curve.JoinCurves(segs, tol);
                        if (j != null)
                        {
                            foreach (var c in j)
                            {
                                if (c != null && c.IsClosed)
                                {
                                    loops.Add(c);
                                }
                            }
                        }
                    }
                }
                return loops.OrderByDescending(c => Math.Abs(AreaMassProperties.Compute(c)?.Area ?? 0.0)).ToList();
            }
        }

        [Flags]
        private enum BoundaryFace { None=0, U0=1, U1=2, V0=4, V1=8, T0=16, T1=32, All=U0|U1|V0|V1|T0|T1 }

        // ─────────────────────────────────────────────────────────────────────
        // CapDomainMap  (from wsp_In09 — verbatim)
        // ─────────────────────────────────────────────────────────────────────

        private sealed class CapDomainMap
        {
            private readonly Curve _bottom, _right, _top, _left;
            private readonly Point3d _p00, _p10, _p11, _p01;

            private CapDomainMap(Curve bottom, Curve right, Curve top, Curve left)
            {
                _bottom=bottom; _right=right; _top=top; _left=left;
                _p00=_bottom.PointAtStart; _p10=_bottom.PointAtEnd;
                _p11=_right.PointAtEnd;    _p01=_left.PointAtEnd;
            }

            public static CapDomainMap TryCreate(SurfProbe probe, double tol)
            {
                if (probe == null) return null;
                Brep cap = probe.AsCapBrep();
                if (cap == null || cap.Faces.Count == 0) return null;
                if (!OuterEdgesAndCorners(cap.Faces[0], tol, out List<Curve> edges, out List<Point3d> _)) return null;
                if (edges.Count != 4) return null;
                Curve b = edges[0].DuplicateCurve(); Curve r = edges[1].DuplicateCurve();
                Curve t = edges[2].DuplicateCurve(); Curve l = edges[3].DuplicateCurve();
                if (b==null||r==null||t==null||l==null) return null;
                t.Reverse(); l.Reverse();
                return new CapDomainMap(b, r, t, l);
            }

            public bool Evaluate(double u, double v, out Point3d point, out Vector3d normal)
            {
                u = Clamp01(u); v = Clamp01(v);
                point = Coons(u, v);
                const double h = 1e-4;
                Vector3d du = Coons(Clamp01(u+h), v) - Coons(Clamp01(u-h), v);
                Vector3d dv2 = Coons(u, Clamp01(v+h)) - Coons(u, Clamp01(v-h));
                normal = Vector3d.CrossProduct(du, dv2);
                return normal.Unitize();
            }

            private Point3d Coons(double u, double v)
            {
                Point3d c0 = PtNLen(_bottom, u); Point3d c1 = PtNLen(_top, u);
                Point3d d0 = PtNLen(_left, v);   Point3d d1 = PtNLen(_right, v);
                Point3d rA = c0 + v*(c1-c0);     Point3d rB = d0 + u*(d1-d0);
                Point3d bi = (1-u)*(1-v)*_p00 + u*(1-v)*_p10 + u*v*_p11 + (1-u)*v*_p01;
                return rA + (Vector3d)rB - (Vector3d)bi;
            }

            private static Point3d PtNLen(Curve c, double t01)
            {
                if (c == null) return Point3d.Unset;
                if (c.NormalizedLengthParameter(Clamp01(t01), out double t)) return c.PointAt(t);
                return c.PointAt(c.Domain.ParameterAt(Clamp01(t01)));
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // UvPairMap  (from wsp_In09 — verbatim)
        // ─────────────────────────────────────────────────────────────────────

        private struct UvPairMap
        {
            public bool Swap, FlipU, FlipV;
            public static readonly UvPairMap Identity = new UvPairMap { Swap=false, FlipU=false, FlipV=false };

            public void MapToB(double u, double v, out double bu, out double bv)
            {
                double x = Swap ? v : u; double y = Swap ? u : v;
                if (FlipU) x = 1-x; if (FlipV) y = 1-y;
                bu = Clamp01(x); bv = Clamp01(y);
            }

            public void MapFromB(double bu, double bv, out double u, out double v)
            {
                double x = FlipU ? 1-bu : bu; double y = FlipV ? 1-bv : bv;
                u = Clamp01(Swap ? y : x); v = Clamp01(Swap ? x : y);
            }

            public static UvPairMap FindBest(SurfProbe[] probes, CapDomainMap[] capMaps, int pairIndex)
            {
                UvPairMap best = Identity; double bestScore = double.PositiveInfinity;
                bool[] flags = { false, true };
                foreach (bool sw in flags) foreach (bool fu in flags) foreach (bool fv in flags)
                {
                    var map = new UvPairMap { Swap=sw, FlipU=fu, FlipV=fv };
                    double score = Score(probes, capMaps, pairIndex, map);
                    if (score < bestScore) { bestScore = score; best = map; }
                }
                return best;
            }

            private static double Score(SurfProbe[] probes, CapDomainMap[] capMaps, int pairIndex, UvPairMap map)
            {
                if (probes == null || pairIndex < 0 || pairIndex >= probes.Length-1) return double.PositiveInfinity;
                SurfProbe a = probes[pairIndex]; SurfProbe b = probes[pairIndex+1];
                if (a==null||b==null) return double.PositiveInfinity;
                double[,] samples = {{0,0},{1,0},{1,1},{0,1},{0.5,0},{1,0.5},{0.5,1},{0,0.5},{0.5,0.5}};
                double score = 0; int count = 0;
                for (int i = 0; i < samples.GetLength(0); i++)
                {
                    double u = samples[i,0]; double v = samples[i,1];
                    if (!EvaluateMappedCap(a, capMaps, pairIndex, u, v, out Point3d pa, out Vector3d _)) continue;
                    map.MapToB(u, v, out double bu, out double bv);
                    if (!EvaluateMappedCap(b, capMaps, pairIndex+1, bu, bv, out Point3d pb, out Vector3d _)) continue;
                    score += pa.DistanceToSquared(pb); count++;
                }
                return count > 0 ? score/count : double.PositiveInfinity;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // SlabDomain  (from wsp_In09 — verbatim)
        // ─────────────────────────────────────────────────────────────────────

        private sealed class SlabDomain
        {
            private readonly Plane _plane;
            private readonly Point2d[] _poly;
            private readonly double _minX, _maxX, _minY, _maxY;

            private SlabDomain(Plane plane, Point2d[] poly)
            {
                _plane=plane; _poly=poly;
                _minX=_minY=double.MaxValue; _maxX=_maxY=double.MinValue;
                foreach (Point2d p in poly)
                { if(p.X<_minX)_minX=p.X; if(p.Y<_minY)_minY=p.Y; if(p.X>_maxX)_maxX=p.X; if(p.Y>_maxY)_maxY=p.Y; }
            }

            public static SlabDomain Create(Curve boundary, Plane plane, int sampleCount)
            {
                var pts = SampleCurvePoints(boundary, sampleCount); if (pts.Count < 3) return null;
                var poly = new List<Point2d>();
                foreach (Point3d p in pts) { Vector3d d = p-plane.Origin; poly.Add(new Point2d(d*plane.XAxis, d*plane.YAxis)); }
                return new SlabDomain(plane, poly.ToArray());
            }

            public bool Contains(Point3d point)
            {
                Vector3d d = point-_plane.Origin; double x=d*_plane.XAxis; double y=d*_plane.YAxis;
                if (x<_minX||x>_maxX||y<_minY||y>_maxY) return false;
                return PtInPoly(x, y, _poly);
            }

            public double SignedDistance(Point3d point)
            {
                Vector3d d = point-_plane.Origin; double x=d*_plane.XAxis; double y=d*_plane.YAxis;
                bool inside = PtInPoly(x,y,_poly); double dist = DistToPoly(x,y,_poly);
                return inside ? -dist : dist;
            }

            private static double DistToPoly(double x, double y, Point2d[] poly)
            {
                double best = double.MaxValue; int n = poly.Length;
                for (int i = 0; i < n; i++)
                {
                    Point2d a=poly[i]; Point2d b=poly[(i+1)%n];
                    double vx=b.X-a.X; double vy=b.Y-a.Y; double wx=x-a.X; double wy=y-a.Y;
                    double l2=vx*vx+vy*vy; double t = l2<=EPS ? 0 : (wx*vx+wy*vy)/l2;
                    t=Math.Max(0,Math.Min(1,t)); double dx=x-(a.X+t*vx); double dy=y-(a.Y+t*vy);
                    double d=Math.Sqrt(dx*dx+dy*dy); if(d<best) best=d;
                }
                return best;
            }

            private static bool PtInPoly(double x, double y, Point2d[] poly)
            {
                bool inside=false; int n=poly.Length;
                for (int i=0, j=n-1; i<n; j=i++)
                {
                    double yi=poly[i].Y; double yj=poly[j].Y;
                    if ((yi>y)==(yj>y)) continue;
                    double xCross=(poly[j].X-poly[i].X)*(y-yi)/(yj-yi+EPS)+poly[i].X;
                    if (x<xCross) inside=!inside;
                }
                return inside;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Slab domain builders  (from wsp_In09 — verbatim)
        // ─────────────────────────────────────────────────────────────────────

        private static CapDomainMap[] BuildCapDomainMaps(SurfProbe[] probes)
        {
            if (probes == null) return new CapDomainMap[0];
            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
            var maps = new CapDomainMap[probes.Length];
            for (int i = 0; i < probes.Length; i++) maps[i] = CapDomainMap.TryCreate(probes[i], tol);
            return maps;
        }

        private static UvPairMap[] BuildPairMaps(SurfProbe[] probes, CapDomainMap[] capMaps)
        {
            if (probes == null || probes.Length < 2) return new UvPairMap[0];
            var maps = new UvPairMap[probes.Length - 1];
            for (int i = 0; i < probes.Length - 1; i++) maps[i] = UvPairMap.FindBest(probes, capMaps, i);
            return maps;
        }

        private static UvPairMap GetPairMap(UvPairMap[] maps, int index)
        {
            if (maps == null || index < 0 || index >= maps.Length) return UvPairMap.Identity;
            return maps[index];
        }

        private static bool EvaluateMappedCap(
            SurfProbe probe, CapDomainMap[] maps, int index,
            double u, double v, out Point3d point, out Vector3d normal)
        {
            point = Point3d.Unset; normal = Vector3d.Unset;
            if (maps != null && index >= 0 && index < maps.Length && maps[index] != null)
                return maps[index].Evaluate(u, v, out point, out normal);
            return probe != null && probe.EvaluateAt(u, v, out point, out normal);
        }

        private static bool EvaluateSlabCaps(
            SurfProbe[] probes, CapDomainMap[] capMaps, UvPairMap[] pairMaps,
            int slab, double u, double v, out Point3d pA, out Point3d pB)
        {
            pA = pB = Point3d.Unset;
            if (probes == null || slab < 0 || slab >= probes.Length-1) return false;
            UvPairMap map = GetPairMap(pairMaps, slab);
            map.MapToB(u, v, out double bU, out double bV);
            return EvaluateMappedCap(probes[slab],   capMaps, slab,   u,  v,  out pA, out Vector3d _) &&
                   EvaluateMappedCap(probes[slab+1], capMaps, slab+1, bU, bV, out pB, out Vector3d _);
        }

        private static void EstimateSlabSize(
            SurfProbe[] probes, CapDomainMap[] capMaps, UvPairMap[] pairMaps, int slab,
            out double lenU, out double lenV, out double depth)
        {
            lenU = lenV = depth = 0; int cU=0, cV=0, cD=0;
            double[] s = {0.0, 0.5, 1.0};
            foreach (double v in s)
            {
                lenU += MeasureSlabLine(probes, capMaps, pairMaps, slab, true, v, false);
                lenU += MeasureSlabLine(probes, capMaps, pairMaps, slab, true, v, true);
                cU += 2;
            }
            foreach (double u in s)
            {
                lenV += MeasureSlabLine(probes, capMaps, pairMaps, slab, false, u, false);
                lenV += MeasureSlabLine(probes, capMaps, pairMaps, slab, false, u, true);
                cV += 2;
            }
            for (int iu = 0; iu <= 2; iu++)
            {
                for (int iv = 0; iv <= 2; iv++)
                {
                    if (EvaluateSlabCaps(probes, capMaps, pairMaps, slab, iu / 2.0, iv / 2.0, out Point3d a, out Point3d b))
                    {
                        depth += a.DistanceTo(b);
                        cD++;
                    }
                }
            }
            lenU = cU>0 ? lenU/cU : 1.0; lenV = cV>0 ? lenV/cV : 1.0; depth = cD>0 ? depth/cD : 1.0;
        }

        private static double MeasureSlabLine(
            SurfProbe[] probes, CapDomainMap[] capMaps, UvPairMap[] pairMaps,
            int slab, bool alongU, double fixedParam, bool useB)
        {
            const int steps = 24; double len = 0; Point3d prev = Point3d.Unset; bool havePrev = false;
            for (int i = 0; i <= steps; i++)
            {
                double s = i/(double)steps; double u=alongU?s:fixedParam; double v=alongU?fixedParam:s;
                if (!EvaluateSlabCaps(probes, capMaps, pairMaps, slab, u, v, out Point3d pA, out Point3d pB)) continue;
                Point3d p = useB ? pB : pA;
                if (havePrev) len += prev.DistanceTo(p);
                prev = p; havePrev = true;
            }
            return len;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Bound geometry builder  (from wsp_In09 — verbatim)
        // ─────────────────────────────────────────────────────────────────────

        private static Brep MakeBoundGeoMulti(SurfProbe[] probes)
        {
            if (probes == null || probes.Length < 2) return null;
            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
            var solids = new List<Brep>();
            for (int i = 0; i < probes.Length-1; i++)
            {
                Brep capA = probes[i].AsCapBrep(); Brep capB = probes[i+1].AsCapBrep();
                if (capA==null||capB==null) continue;
                Brep solid = BuildSolidBetweenCaps(capA, capB, tol);
                if (solid != null) solids.Add(solid);
            }
            if (solids.Count == 0) return null;
            if (solids.Count == 1) return solids[0];
            Brep[] joined = Brep.JoinBreps(solids, tol);
            return joined != null && joined.Length > 0 ? joined[0] : Brep.MergeBreps(solids, tol);
        }

        private static Brep BuildSolidBetweenCaps(Brep capA, Brep capB, double tol)
        {
            if (capA==null||capB==null||capA.Faces.Count==0||capB.Faces.Count==0) return null;
            bool gotA = OuterEdgesAndCorners(capA.Faces[0], tol, out List<Curve> eA, out List<Point3d> vA);
            bool gotB = OuterEdgesAndCorners(capB.Faces[0], tol, out List<Curve> eB, out List<Point3d> vB);

            if (gotA && gotB && vA.Count == vB.Count && vA.Count >= 3)
            {
                AlignCornerSets(vA, vB, out bool useReverse, out int shift);
                if (useReverse) { vB.Reverse(); eB.Reverse(); }
                if (shift != 0) { vB = RotateList(vB, shift); eB = RotateList(eB, shift); }
                for (int i = 0; i < eA.Count; i++)
                {
                    if (eA[i].PointAtStart.DistanceTo(vA[i]) > tol) eA[i].Reverse();
                    if (eB[i].PointAtStart.DistanceTo(vB[i]) > tol) eB[i].Reverse();
                }
                var parts = new List<Brep> { capA, capB };
                for (int i = 0; i < eA.Count; i++)
                {
                    Brep[] side = Brep.CreateFromLoft(new Curve[]{eA[i],eB[i]}, Point3d.Unset, Point3d.Unset, LoftType.Straight, false);
                    if (side != null && side.Length > 0 && side[0] != null) parts.Add(side[0]);
                }
                Brep[] j = Brep.JoinBreps(parts, tol);
                Brep solid = j!=null&&j.Length>0 ? j[0] : Brep.MergeBreps(parts, tol);
                return CleanBrep(solid, tol);
            }
            return CleanBrep(BuildSolidByLoopLoft(capA, capB, tol), tol);
        }

        private static bool OuterEdgesAndCorners(BrepFace face, double tol, out List<Curve> edges, out List<Point3d> corners)
        {
            edges = new List<Curve>(); corners = new List<Point3d>();
            if (face?.OuterLoop == null) return false;
            foreach (BrepTrim trim in face.OuterLoop.Trims)
            {
                BrepEdge edge = trim.Edge; if (edge == null) continue;
                bool rev = trim.IsReversed(); BrepVertex vtx = rev ? edge.EndVertex : edge.StartVertex;
                if (vtx == null) continue;
                Curve c = edge.DuplicateCurve(); if (c == null) continue;
                if (rev) c.Reverse();
                if (c.PointAtStart.DistanceTo(vtx.Location) > tol) c.Reverse();
                corners.Add(vtx.Location); edges.Add(c);
            }
            return edges.Count >= 3 && corners.Count == edges.Count;
        }

        private static void AlignCornerSets(List<Point3d> a, List<Point3d> b, out bool useReverse, out int shift)
        {
            useReverse=false; shift=0; int n=a.Count;
            double bestF=double.PositiveInfinity; int sF=0;
            for (int k=0; k<n; k++) { double sc=0; for (int i=0; i<n; i++) sc+=a[i].DistanceToSquared(b[(k+i)%n]); if(sc<bestF){bestF=sc;sF=k;} }
            var br=new List<Point3d>(b); br.Reverse();
            double bestR=double.PositiveInfinity; int sR=0;
            for (int k=0; k<n; k++) { double sc=0; for (int i=0; i<n; i++) sc+=a[i].DistanceToSquared(br[(k+i)%n]); if(sc<bestR){bestR=sc;sR=k;} }
            if (bestR < bestF) { useReverse=true; shift=sR; } else { shift=sF; }
        }

        private static Brep BuildSolidByLoopLoft(Brep capA, Brep capB, double tol)
        {
            Curve loopA = JoinLoopAsCurve(capA.Faces[0].OuterLoop, tol);
            Curve loopB = JoinLoopAsCurve(capB.Faces[0].OuterLoop, tol);
            if (loopA==null||loopB==null) return null;
            AlignClosedPair(loopA, loopB);
            Brep[] loft = Brep.CreateFromLoft(new Curve[]{loopA,loopB}, Point3d.Unset, Point3d.Unset, LoftType.Normal, false);
            if (loft==null||loft.Length==0||loft[0]==null) return null;
            var parts = new List<Brep> { capA, capB, loft[0] };
            Brep[] j = Brep.JoinBreps(parts, tol);
            return j!=null&&j.Length>0 ? j[0] : Brep.MergeBreps(parts, tol);
        }

        private static Curve JoinLoopAsCurve(BrepLoop loop, double tol)
        {
            if (loop == null) return null;
            var segs = new List<Curve>();
            foreach (BrepTrim trim in loop.Trims)
            {
                BrepEdge e = trim.Edge;
                if (e == null)
                    continue;
                Curve c = e.DuplicateCurve();
                if (c == null)
                    continue;
                if (trim.IsReversed())
                    c.Reverse();
                segs.Add(c);
            }
            if (segs.Count == 0) return null;
            Curve[] j = Curve.JoinCurves(segs, tol, false);
            if (j != null && j.Length > 0)
            {
                Curve best = j.OrderByDescending(c => c.GetLength()).First();
                if (!best.IsClosed)
                    best.MakeClosed(tol);
                return best;
            }
            var pc = new PolyCurve(); foreach (Curve c in segs) pc.AppendSegment(c); if(!pc.IsClosed) pc.MakeClosed(tol); return pc;
        }

        private static void AlignClosedPair(Curve c0, Curve c1)
        {
            if (c0==null||c1==null||!c0.IsClosed||!c1.IsClosed) return;
            if (c1.ClosestPoint(c0.PointAtStart, out double t)) c1.ChangeClosedCurveSeam(t);
            Vector3d t0=c0.TangentAtStart; Vector3d t1=c1.TangentAtStart;
            if (!t0.IsZero && !t1.IsZero && t0*t1 < 0) c1.Reverse();
        }

        private static Brep CleanBrep(Brep brep, double tol)
        {
            if (brep==null) return null;
            try { brep.Faces.ShrinkFaces(); } catch {}
            try { brep.Faces.SplitKinkyFaces(RhinoMath.ToRadians(0.5), true); } catch {}
            try { brep.MergeCoplanarFaces(tol); } catch {}
            try { brep.Compact(); } catch {}
            return brep;
        }

        private static List<T> RotateList<T>(List<T> list, int shift)
        {
            int n = list.Count; var result = new List<T>(n);
            for (int i = 0; i < n; i++) result.Add(list[(shift+i)%n]);
            return result;
        }

        private static List<Point3d> SampleCurvePoints(Curve curve, int count)
        {
            var pts = new List<Point3d>(); if (curve==null||count<4) return pts;
            for (int i = 0; i < count; i++)
            { if (curve.NormalizedLengthParameter(i/(double)count, out double t)) pts.Add(curve.PointAt(t)); }
            return pts;
        }

        // ─────────────────────────────────────────────────────────────────────
        // SDF boundary helpers  (from wsp_In09 — verbatim)
        // ─────────────────────────────────────────────────────────────────────

        private static double ParametricBoundarySdf(double u, double v, double t, double lenU, double lenV, double depth)
        {
            lenU=Math.Max(lenU,EPS); lenV=Math.Max(lenV,EPS); depth=Math.Max(depth,EPS);
            double du=Math.Min(Clamp01(u),1-Clamp01(u))*lenU;
            double dv=Math.Min(Clamp01(v),1-Clamp01(v))*lenV;
            double dt=Math.Min(Clamp01(t),1-Clamp01(t))*depth;
            return -Math.Min(du, Math.Min(dv, dt));
        }

        private static double InwardShellSdf(double boundarySdf, double shellThickness)
        { shellThickness=Math.Max(shellThickness,EPS); return Math.Abs(boundarySdf+0.5*shellThickness)-0.5*shellThickness; }

        private static double ShellBoundarySdfFromMask(
            double u, double v, double t, double lenU, double lenV, double depth, BoundaryFace mask)
        {
            lenU=Math.Max(lenU,EPS); lenV=Math.Max(lenV,EPS); depth=Math.Max(depth,EPS);
            double best = double.MaxValue;
            if ((mask&BoundaryFace.U0)!=0) best=Math.Min(best, Clamp01(u)*lenU);
            if ((mask&BoundaryFace.U1)!=0) best=Math.Min(best, (1-Clamp01(u))*lenU);
            if ((mask&BoundaryFace.V0)!=0) best=Math.Min(best, Clamp01(v)*lenV);
            if ((mask&BoundaryFace.V1)!=0) best=Math.Min(best, (1-Clamp01(v))*lenV);
            if ((mask&BoundaryFace.T0)!=0) best=Math.Min(best, Clamp01(t)*depth);
            if ((mask&BoundaryFace.T1)!=0) best=Math.Min(best, (1-Clamp01(t))*depth);
            return best==double.MaxValue ? TRIM : -best;
        }

        private static BoundaryFace BuildShellFaceMaskForSlab(
            SurfProbe[] probes, CapDomainMap[] capMaps, UvPairMap[] pairMaps, int slab)
        {
            BoundaryFace[] faces = { BoundaryFace.U0, BoundaryFace.U1, BoundaryFace.V0, BoundaryFace.V1, BoundaryFace.T0, BoundaryFace.T1 };
            double[] avgZ = new double[faces.Length];
            for (int i = 0; i < faces.Length; i++) avgZ[i] = SampleFaceAvgZ(probes, capMaps, pairMaps, slab, faces[i]);
            int low=0, high=0;
            for (int i=1; i<avgZ.Length; i++) { if(avgZ[i]<avgZ[low]) low=i; if(avgZ[i]>avgZ[high]) high=i; }
            BoundaryFace mask = BoundaryFace.All;
            mask &= ~faces[low];
            if (high != low) mask &= ~faces[high];
            return mask;
        }

        private static double SampleFaceAvgZ(
            SurfProbe[] probes, CapDomainMap[] capMaps, UvPairMap[] pairMaps, int slab, BoundaryFace face)
        {
            double[] s = {0, 0.5, 1}; double sum=0; int count=0;
            foreach (double a in s) foreach (double b in s)
            {
                double u=0.5, v=0.5, t=0.5;
                switch (face)
                {
                    case BoundaryFace.U0: u=0;   v=a; t=b; break;
                    case BoundaryFace.U1: u=1;   v=a; t=b; break;
                    case BoundaryFace.V0: u=a;   v=0; t=b; break;
                    case BoundaryFace.V1: u=a;   v=1; t=b; break;
                    case BoundaryFace.T0: u=a;   v=b; t=0; break;
                    case BoundaryFace.T1: u=a;   v=b; t=1; break;
                }
                if (!EvaluateSlabCaps(probes, capMaps, pairMaps, slab, u, v, out Point3d pA, out Point3d pB)) continue;
                Point3d p = pA + t*(pB-pA); if (!p.IsValid) continue;
                sum += p.Z; count++;
            }
            return count > 0 ? sum/count : 0;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Marching Cubes — curvilinear  (from wsp_In09 — verbatim)
        // ─────────────────────────────────────────────────────────────────────

        private static Mesh MarchingCubesCurvilinear(double[] scalars, Point3d[] points, int nx, int ny, int nz)
        {
            var mesh = new Mesh();
            var vMap = new Dictionary<VertexKey, int>();
            double keyTol = 1e-8;

            int[,] cubeCorners = {{0,0,0},{1,0,0},{1,1,0},{0,1,0},{0,0,1},{1,0,1},{1,1,1},{0,1,1}};
            int[,] edgeCorners = {{0,1},{1,2},{2,3},{3,0},{4,5},{5,6},{6,7},{7,4},{0,4},{1,5},{2,6},{3,7}};

            for (int iz=0; iz<nz-1; iz++) for (int iy=0; iy<ny-1; iy++) for (int ix=0; ix<nx-1; ix++)
            {
                double[] sv = new double[8]; Point3d[] cp = new Point3d[8]; bool hasTrim=false;
                for (int c=0; c<8; c++)
                {
                    int ci=Idx(ix+cubeCorners[c,0], iy+cubeCorners[c,1], iz+cubeCorners[c,2], nx, ny);
                    sv[c]=scalars[ci]; cp[c]=points[ci];
                    if (sv[c]>TRIM*0.1||!cp[c].IsValid) hasTrim=true;
                }
                if (hasTrim) continue;

                var crossings = new List<Point3d>(12);
                for (int e=0; e<12; e++)
                {
                    int a=edgeCorners[e,0]; int b2=edgeCorners[e,1];
                    if ((sv[a]<0)==(sv[b2]<0)) continue;
                    double d=sv[a]-sv[b2]; double tt=Math.Abs(d)<1e-14?0.5:sv[a]/d;
                    crossings.Add(cp[a]+Clamp01(tt)*(cp[b2]-cp[a]));
                }
                Vector3d normal = EstimateWorldGradFromCube(cp, sv);
                AddCubePolygon(mesh, vMap, crossings, normal, keyTol);
            }
            return mesh.Faces.Count == 0 ? null : mesh;
        }

        private static Vector3d EstimateWorldGradFromCube(Point3d[] p, double[] s)
        {
            Vector3d g = Vector3d.Zero;
            void Acc(double df, Vector3d edge) { double l2=edge.SquareLength; if(l2>EPS) g+=(df/l2)*edge; }
            Acc(s[1]-s[0],p[1]-p[0]); Acc(s[2]-s[3],p[2]-p[3]); Acc(s[5]-s[4],p[5]-p[4]); Acc(s[6]-s[7],p[6]-p[7]);
            Acc(s[3]-s[0],p[3]-p[0]); Acc(s[2]-s[1],p[2]-p[1]); Acc(s[7]-s[4],p[7]-p[4]); Acc(s[6]-s[5],p[6]-p[5]);
            Acc(s[4]-s[0],p[4]-p[0]); Acc(s[5]-s[1],p[5]-p[1]); Acc(s[7]-s[3],p[7]-p[3]); Acc(s[6]-s[2],p[6]-p[2]);
            return g;
        }

        private static void AddCubePolygon(Mesh mesh, Dictionary<VertexKey,int> vMap, List<Point3d> crossings, Vector3d normal, double keyTol)
        {
            if (crossings == null || crossings.Count < 3) return;
            Point3d center = Point3d.Origin;
            for (int i=0; i<crossings.Count; i++) center+=(Vector3d)crossings[i];
            center /= crossings.Count;

            Vector3d norm = normal;
            if (!norm.Unitize()) { norm=Vector3d.CrossProduct(crossings[1]-crossings[0], crossings[2]-crossings[0]); if(!norm.Unitize()) return; }

            Vector3d axX = crossings[0]-center; axX -= norm*(axX*norm);
            if (!axX.Unitize())
            {
                axX = Vector3d.CrossProduct(norm, Vector3d.XAxis);
                if (!axX.Unitize())
                    axX = Vector3d.CrossProduct(norm, Vector3d.YAxis);
                if (!axX.Unitize())
                    return;
            }
            Vector3d axY = Vector3d.CrossProduct(norm, axX); if (!axY.Unitize()) return;

            int n = crossings.Count;
            double[] angles = new double[n]; int[] order = new int[n];
            for (int i=0; i<n; i++) { order[i]=i; Vector3d dv=crossings[i]-center; angles[i]=Math.Atan2(dv*axY, dv*axX); }
            for (int i = 1; i < n; i++)
            {
                int key = order[i];
                double ka = angles[key];
                int j = i - 1;
                while (j >= 0 && angles[order[j]] > ka)
                {
                    order[j + 1] = order[j];
                    j--;
                }
                order[j + 1] = key;
            }

            int v0 = AddVertex(mesh, vMap, crossings[order[0]], keyTol);
            for (int i=1; i<n-1; i++)
            {
                int v1=AddVertex(mesh, vMap, crossings[order[i]], keyTol);
                int v2=AddVertex(mesh, vMap, crossings[order[i+1]], keyTol);
                AddOrientedFace(mesh, v0, v1, v2, norm);
            }
        }

        private static void AddOrientedFace(Mesh mesh, int a, int b, int c, Vector3d targetNormal)
        {
            if (a==b||b==c||c==a) return;
            Vector3d fn = Vector3d.CrossProduct(mesh.Vertices[b]-mesh.Vertices[a], mesh.Vertices[c]-mesh.Vertices[a]);
            if (fn.IsValid && !fn.IsZero && targetNormal.IsValid && !targetNormal.IsZero && fn*targetNormal < 0)
                mesh.Faces.AddFace(a,c,b); else mesh.Faces.AddFace(a,b,c);
        }

        private static int AddVertex(Mesh mesh, Dictionary<VertexKey,int> map, Point3d p, double tol)
        {
            var key = new VertexKey(p, tol);
            if (map.TryGetValue(key, out int idx)) return idx;
            idx = mesh.Vertices.Add(p); map[key]=idx; return idx;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Mesh cleanup  (from wsp_In11)
        // ─────────────────────────────────────────────────────────────────────

        private static List<Mesh> CleanAndSplitResultMeshes(Mesh mesh, double weldAngleDeg, int minFragFaces, out int removedFragments)
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

            foreach (var comp in components)
            {
                if (comp == null || comp.Faces.Count == 0) continue;
                comp.Vertices.CombineIdentical(true, true);
                comp.Faces.CullDegenerateFaces();
                comp.Vertices.CullUnused();
                comp.UnifyNormals();
                comp.Weld(weldAngle);
                comp.Normals.ComputeNormals();
                comp.Compact();
                if (comp.Faces.Count > 0) result.Add(comp);
            }
            return result;
        }

        private static List<Mesh> SplitConnectedComponents(Mesh mesh, int minFaces, out int removedFragments)
        {
            removedFragments = 0;
            var result = new List<Mesh>();
            if (mesh == null || mesh.Faces.Count == 0) return result;

            int fc = mesh.Faces.Count;
            var v2f = new Dictionary<int, List<int>>();
            for (int fi = 0; fi < fc; fi++)
            {
                MeshFace f = mesh.Faces[fi];
                AddFV(v2f,f.A,fi); AddFV(v2f,f.B,fi); AddFV(v2f,f.C,fi);
                if (f.IsQuad) AddFV(v2f,f.D,fi);
            }

            bool[] visited = new bool[fc];
            var queue = new Queue<int>();

            for (int seed = 0; seed < fc; seed++)
            {
                if (visited[seed]) continue;
                var compFaces = new List<int>();
                visited[seed] = true; queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    int fi = queue.Dequeue(); compFaces.Add(fi);
                    MeshFace f = mesh.Faces[fi];
                    EnqN(f.A); EnqN(f.B); EnqN(f.C); if (f.IsQuad) EnqN(f.D);
                    void EnqN(int vi)
                    {
                        if (!v2f.TryGetValue(vi, out var fl))
                            return;
                        foreach (int nf in fl)
                        {
                            if (!visited[nf])
                            {
                                visited[nf] = true;
                                queue.Enqueue(nf);
                            }
                        }
                    }
                }
                if (compFaces.Count < minFaces && minFaces > 0) { removedFragments++; continue; }

                var comp = new Mesh(); var vMap = new Dictionary<int,int>();
                int MapV(int oi) { if(vMap.TryGetValue(oi,out int ni)) return ni; ni=comp.Vertices.Add(mesh.Vertices[oi]); vMap[oi]=ni; return ni; }
                foreach (int fi in compFaces)
                {
                    MeshFace f = mesh.Faces[fi];
                    int a = MapV(f.A),
                        b = MapV(f.B),
                        c = MapV(f.C);
                    if (f.IsQuad)
                    {
                        comp.Faces.AddFace(a, b, c, MapV(f.D));
                    }
                    else
                    {
                        comp.Faces.AddFace(a, b, c);
                    }
                }
                result.Add(comp);
            }
            return result;
        }

        private static List<WasperFieldGoo> BuildFieldOutputs(
            bool disjoinMesh,
            bool outMesh,
            List<Mesh> resultMeshes,
            Mesh joinedMesh,
            string label,
            string sourceTrace)
        {
            var fields = new List<WasperFieldGoo>();

            if (disjoinMesh && outMesh && resultMeshes != null && resultMeshes.Count > 0)
            {
                // In12 has no analytical field — the only SDF source is the mesh.
                // Individual island fragments are open surfaces; mesh.IsPointInside() on
                // them gives wrong signs.  Build the field once from the joined (more
                // complete) mesh, then clip each island field to its own bounding box.
                WasperField baseField = (joinedMesh != null && joinedMesh.Faces.Count > 0)
                    ? WasperField.FromMesh(joinedMesh, 0.001, label, sourceTrace, WasperFieldSdfQuality.ApproximateSdf)
                    : null;

                for (int i = 0; i < resultMeshes.Count; i++)
                {
                    BoundingBox islandBB = resultMeshes[i].GetBoundingBox(true);
                    islandBB.Inflate(islandBB.Diagonal.Length * 0.005);

                    if (baseField != null)
                    {
                        WasperField capturedBase = baseField;
                        BoundingBox capturedBB   = islandBB;
                        WasperField f = new WasperField(
                            p => capturedBB.Contains(p) ? capturedBase.Evaluate(p) : double.PositiveInfinity,
                            capturedBB,
                            $"{label}_{i + 1}",
                            capturedBase.OperationTrace + Environment.NewLine + $"1. IslandClip(index={i + 1}) | quality={capturedBase.SdfQuality}",
                            capturedBase.SdfQuality,
                            capturedBase.OperationCount + 1,
                            capturedBase.CurveThickenCount);
                        fields.Add(new WasperFieldGoo(f));
                    }
                    else
                    {
                        // Last resort: individual open mesh (may have sign issues).
                        WasperField f = WasperField.FromMesh(
                            resultMeshes[i],
                            0.001,
                            $"{label}_{i + 1}",
                            sourceTrace + Environment.NewLine + $"1. IslandMeshSource(index={i + 1}) | quality=ApproximateSdf",
                            WasperFieldSdfQuality.ApproximateSdf);
                        if (f != null) fields.Add(new WasperFieldGoo(f));
                    }
                }
            }

            if (fields.Count == 0 && joinedMesh != null && joinedMesh.Faces.Count > 0)
            {
                WasperField field = WasperField.FromMesh(joinedMesh, 0.001, label, sourceTrace, WasperFieldSdfQuality.ApproximateSdf);
                if (field != null) fields.Add(new WasperFieldGoo(field));
            }

            return fields;
        }

        private static List<Mesh> MeshListFromJoined(Mesh mesh)
        {
            var result = new List<Mesh>();
            if (mesh != null && mesh.Faces.Count > 0)
                result.Add(mesh);
            return result;
        }

        private static Mesh JoinMeshList(List<Mesh> meshes)
        {
            if (meshes == null || meshes.Count == 0) return null;
            var joined = new Mesh();
            foreach (var m in meshes) if (m != null && m.Faces.Count > 0) joined.Append(m);
            if (joined.Faces.Count == 0) return null;
            joined.Compact(); return joined;
        }

        private static void AddFV(Dictionary<int,List<int>> d, int vi, int fi)
        { if (!d.TryGetValue(vi, out var lst)) d[vi]=lst=new List<int>(); lst.Add(fi); }

        // ─────────────────────────────────────────────────────────────────────
        // TrimVolume  (from wsp_In11 — verbatim)
        // ─────────────────────────────────────────────────────────────────────

        private static TrimVolume BuildTrimVolume(object geometry)
        {
            if (geometry == null) return null;
            if (geometry is IGH_Goo goo) { object sv=goo.ScriptVariable(); if(sv!=null&&!ReferenceEquals(sv,geometry)) return BuildTrimVolume(sv); }
            if (geometry is Box box) return box.IsValid ? new TrimVolume(box) : null;
            if (geometry is GeometryBase gb) return BuildTrimVolumeFromGeometry(gb);
            return null;
        }

        private static TrimVolume BuildTrimVolumeFromGeometry(GeometryBase geometry)
        {
            if (geometry == null) return null;
            if (geometry is Brep brep) { var b=brep.DuplicateBrep(); return (b!=null&&b.IsValid&&b.IsSolid) ? new TrimVolume(b,null) : null; }
            if (geometry is Extrusion ext) { Brep b=ext.ToBrep(); return (b!=null&&b.IsValid&&b.IsSolid) ? new TrimVolume(b,null) : null; }
            if (geometry is Mesh mesh) { var m=mesh.DuplicateMesh(); return (m!=null&&m.IsValid&&m.IsClosed) ? new TrimVolume(null,m) : null; }
            return null;
        }

        private sealed class TrimVolume
        {
            private readonly bool _hasBox; private readonly Box _box; private readonly Brep _brep; private readonly Mesh _mesh;
            public TrimVolume(Brep brep, Mesh mesh) { _hasBox=false; _box=Box.Unset; _brep=brep; _mesh=mesh; }
            public TrimVolume(Box box) { _hasBox=true; _box=box; _brep=null; _mesh=null; }

            public double SignedDistance(Point3d point)
            {
                if (_hasBox)        return SdBox(point, _box);
                if (_brep != null)  return SdBrep(point, _brep);
                if (_mesh != null)  return SdMesh(point, _mesh);
                return TRIM;
            }

            private static double SdBox(Point3d point, Box box)
            {
                if (!box.IsValid) return TRIM;
                Vector3d v=point-box.Plane.Origin;
                double x=v*box.Plane.XAxis, y=v*box.Plane.YAxis, z=v*box.Plane.ZAxis;
                double minX=Math.Min(box.X.T0,box.X.T1), maxX=Math.Max(box.X.T0,box.X.T1);
                double minY=Math.Min(box.Y.T0,box.Y.T1), maxY=Math.Max(box.Y.T0,box.Y.T1);
                double minZ=Math.Min(box.Z.T0,box.Z.T1), maxZ=Math.Max(box.Z.T0,box.Z.T1);
                double cx2=0.5*(minX+maxX), cy2=0.5*(minY+maxY), cz2=0.5*(minZ+maxZ);
                double hx=0.5*(maxX-minX), hy=0.5*(maxY-minY), hz=0.5*(maxZ-minZ);
                double qx=Math.Abs(x-cx2)-hx, qy=Math.Abs(y-cy2)-hy, qz=Math.Abs(z-cz2)-hz;
                double ox=Math.Max(qx,0), oy=Math.Max(qy,0), oz=Math.Max(qz,0);
                return Math.Sqrt(ox*ox+oy*oy+oz*oz)+Math.Min(Math.Max(qx,Math.Max(qy,qz)),0.0);
            }

            private static double SdBrep(Point3d point, Brep brep)
            {
                if (brep==null||!brep.IsValid||!brep.IsSolid) return TRIM;
                double tol=RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance??1e-6;
                bool ok=brep.ClosestPoint(point, out Point3d closest, out _, out _, out _, double.MaxValue, out _);
                if (!ok||!closest.IsValid) return TRIM;
                double d=point.DistanceTo(closest);
                return brep.IsPointInside(point, tol, true) ? -d : d;
            }

            private static double SdMesh(Point3d point, Mesh mesh)
            {
                if (mesh==null||!mesh.IsValid||!mesh.IsClosed) return TRIM;
                double tol=RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance??1e-6;
                MeshPoint mp=mesh.ClosestMeshPoint(point, double.MaxValue);
                if (mp==null) return TRIM;
                double d=point.DistanceTo(mesh.PointAt(mp));
                return mesh.IsPointInside(point, tol, true) ? -d : d;
            }
        }


        /// <summary>Build a face key from world-space vertices (order-independent, quantised).</summary>
        private static FaceKey BuildFaceKeyFromWorld(Point3d[] pts, double tol = 1e-4)
        {
            var keys = new List<string>(pts.Length);
            foreach (var p in pts)
            {
                long qx = (long)Math.Round(p.X / Math.Max(tol, 1e-12));
                long qy = (long)Math.Round(p.Y / Math.Max(tol, 1e-12));
                long qz = (long)Math.Round(p.Z / Math.Max(tol, 1e-12));
                keys.Add($"{qx},{qy},{qz}");
            }
            keys.Sort();
            return new FaceKey(string.Join("|", keys));
        }

        private static GeometryBase ExtractGeometry(IGH_Goo goo)
        {
            if (goo == null) return null;
            if (goo is GH_Surface ghSrf && ghSrf.Value != null) return ghSrf.Value;
            if (goo is GH_Brep    ghBrp && ghBrp.Value != null) return ghBrp.Value;
            object sv = goo.ScriptVariable();
            if (sv is Surface s) return s;
            if (sv is Brep    b) return b;
            return null;
        }

        private bool IsInputUnwired(int index)
        {
            try { return Params!=null&&Params.Input!=null&&index>=0&&index<Params.Input.Count&&Params.Input[index].SourceCount==0; }
            catch { return false; }
        }

        private static int    GridCount(double length, double resolution)
        { if (resolution<=EPS) resolution=1.0; return Clamp((int)Math.Ceiling(Math.Max(length,resolution)/resolution)+1, 2, 1200); }

        private static double AvoidIntegerResolution(double resolution)
        {
            double nearest = Math.Round(resolution);
            if (nearest >= 1.0 && Math.Abs(resolution - nearest) <= 1e-9)
                return Math.Max(EPS, resolution - 0.01);
            return resolution;
        }

        private static int    Idx(int ix, int iy, int iz, int nx, int ny) => ix + nx*(iy + ny*iz);
        private static int    Clamp(int v, int lo, int hi) => v<lo?lo:v>hi?hi:v;
        private static double Clamp(double v, double lo, double hi) => v<lo?lo:v>hi?hi:v;
        private static double Clamp01(double v) => v<0?0:v>1?1:v;
    }
}
