#region Component Description
/*
    Component Name:
        wsp_In11_Polyhedral Box Array SDF

    Nickname:
        Poly_Box_SDF

    Version:
        v1.0.5 - 260504

    Category / Subcategory:
        WASPer_3DP / 2_Infills

    Description:
        Generates polyhedral infill/lattice meshes inside a Rhino Box domain.

        The component supports two generation modes:

        1. explicit_faces
           Used automatically when:
             - thickness = 0
             - shell_thickness = 0
             - trim_geo is null

           In this mode, the component does not evaluate an SDF field and does not
           use Marching Cubes. Instead, it directly constructs the known polygonal
           faces of the selected polyhedron. This is intended for fast, clean,
           zero-thickness visualisation of the cell layout.

           Because the mesh is made from explicit polygon faces, it is planar by
           construction and avoids the instability that voxel/SDF extraction can
           produce when the requested thickness is zero.

        2. sdf_marching_cubes
           Used automatically when:
             - thickness > 0
             - shell_thickness > 0
             - or trim_geo is provided

           In this mode, the component evaluates a parametric polyhedral distance
           field inside the input box and extracts the zero-isosurface using a
           table-based Marching Cubes implementation.

           The SDF path is used for actual printable/solid infills, finite face
           thicknesses, optional boundary shells, and trimmed geometries.

    Cell Types:
        0  Truncated Octahedron
           BCC-style cell family.
           Space-filling logic.
           The cell has 14 faces:
             - 6 axis-aligned square faces
             - 8 diagonal hexagonal faces

           Recommended for continuous space-filling lattice systems.

        1  Octahedron
           SC-style cell family.
           Non-space-filling as explicit zero-thickness cells.
           The cell has 8 triangular faces.

           Recommended mainly when thickness > 0, because the SDF mode can turn
           the diagonal face network into a finite-thickness infill.

    Inputs:
        box
            Rhino Box defining the domain where the polyhedral array is generated.
            The box dimensions define the model-unit scale of the cells.

        trim_geo
            Optional clipping volume.
            Accepted types:
              - Box
              - closed Brep
              - closed Mesh
              - Extrusion convertible to a closed Brep

            When provided, the component switches to SDF mode and clips the
            generated field against this volume. Invalid or open trim geometries
            are ignored with a warning.

        type
            Integer selecting the polyhedral family:
              - 0 = Truncated Octahedron
              - 1 = Octahedron

            Values outside this range are clamped internally.

        thickness / inf_t
            Face/infill thickness in Rhino model units.
            A value of 0 uses explicit face mode when no shell or trim is active.
            A value greater than 0 uses SDF/Marching Cubes mode.

            For reliable extracted thickness, the voxel resolution should be
            fine enough relative to the requested thickness. As a rule of thumb:
              - resolution <= thickness / 3
              - preferably resolution <= thickness / 4

        shell_thickness / shell_t
            Optional inward boundary shell thickness in Rhino model units.
            A value of 0 disables the boundary shell.
            A value greater than 0 forces SDF mode.

            If shell_thickness is provided and thickness is unwired, the component
            uses shell_thickness as the effective infill thickness.

        shell_caps / caps
            Controls whether top and bottom shell caps are removed.
            When true, the shell is generated only on the lateral box boundary.
            When false, the shell includes all box boundaries.

        invert_field / invert
            Controls whether the extracted SDF region is inverted.

            false:
                Extracts the normal solid/infill region. This corresponds to the
                finite-thickness polyhedral face network plus any enabled shell.

            true:
                Extracts the complementary region of the field inside the box or
                trim volume.

            This input affects only SDF/Marching Cubes mode. It is ignored in
            explicit_faces mode.

        disjoin_mesh / disjoin
            Controls output splitting after the mesh has been generated.

            false:
                Outputs one joined mesh item, even if the resulting topology
                contains disconnected pieces.

            true:
                Splits disconnected mesh islands into separate mesh items and
                removes very small fragments.

            This input does not change the SDF field. It only changes the final
            output structure..

        count_x / cx
            Number of cell repetitions along the local X direction of the box.
            Minimum value is 1.

        count_y / cy
            Number of cell repetitions along the local Y direction of the box.
            Minimum value is 1.

        count_z / cz
            Number of cell repetitions along the local Z direction of the box.
            Minimum value is 1.

        resolution / res
            Voxel size in Rhino model units used by the SDF/Marching Cubes path.
            Smaller values produce more accurate geometry and thickness, but
            increase computation time and memory use.

            This input is ignored in explicit_faces mode.

    Outputs:
        mesh_out / mesh
            Resulting polyhedral mesh output.

            In explicit_faces mode:
                Outputs the direct polygon-face mesh representation.

            In SDF mode with invert_field = false:
                Outputs the normal solid/infill region.

            In SDF mode with invert_field = true:
                Outputs the complementary region.

            In all modes:
                If disjoin_mesh = false, the output is one joined mesh item.
                If disjoin_mesh = true, disconnected islands are output as
                separate mesh items after small-fragment cleanup.

        bound_geo / bound
            Brep representation of the input box domain.

        cell_name / cell
            Text label of the selected cell family:
              - Trunc. Octahedron
              - Octahedron

        array
            Text summary of the repetition counts in X.Y.Z format.

        info
            Diagnostic text describing:
              - selected mesh mode
              - cell type
              - thickness and shell settings
              - inversion state
              - array counts
              - voxel resolution
              - trim state
              - number of output meshes
              - removed fragments
              - mesh vertex and face counts
              - evaluation, extraction, cleanup, and total computation time

    Recent Changes / Implementation Notes:
        - Added a dedicated explicit_faces path for thickness = 0.
          This avoids using SDF extraction for zero-thickness surfaces and produces
          cleaner planar face meshes.

        - Added direct model-unit distance evaluation for the SDF path.
          The infill thickness is now evaluated as a world/model-unit distance
          from the nearest polyhedral face family instead of relying only on
          unscaled cell-space values.

        - Replaced the simplified crossing-sort Marching Cubes approach with a
          table-based Marching Cubes extraction.
          The previous method collected edge crossings per voxel, angle-sorted
          them, and triangulated the polygon as a fan. That approach could create
          unreliable local topology. The table-based implementation uses the
          standard cube-index-to-triangle lookup structure, which is more robust.

        - Added parallel SDF field evaluation.
          Scalar field values are evaluated across Z slices using Parallel.For.

        - Added parallel Marching Cubes extraction.
          Mesh extraction is processed per Z slice and then joined, reducing
          extraction time for larger grids.

        - Updated output behavior for inverted and non-inverted fields.
          When invert_field = false, the component outputs one joined mesh item.
          When invert_field = true, disconnected islands are split and output as
          separate mesh items.

        - Added fragment cleanup.
          Very small disconnected fragments can be removed during cleanup to
          reduce noise from SDF extraction near boundaries or thin features.

        - Added resolution warnings.
          The component warns when resolution is too coarse relative to thickness
          or shell_thickness, because voxel extraction cannot reliably represent
          features smaller than the grid resolution.

    Notes:
        Thickness, shell_thickness, and resolution are all interpreted in Rhino
        model units.

        The total computation cost is mainly controlled by the SDF grid size:
            nx * ny * nz

        Therefore, halving the resolution can increase the number of samples by
        roughly eight times in 3D.
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;

using Grasshopper.Kernel;

using Rhino;
using Rhino.Geometry;
#endregion

namespace WASPer_3DP.Components._3_1_Infills
{
    public sealed class wsp_In11_Polyhedral_Box_Array_SDF : GH_Component
    {
        private const string NAME   = "wsp_In11_Polyhedral Box Array SDF";
        private const string NICK   = "Poly_Box_SDF";
        private const string CAT    = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "3.1_Infills";

        private const double TRIM     = 1e9;
        private const double EPS      = 1e-10;
        private const double SDF_PHASE_NUDGE = 1e-7;
        private const double INV_SQRT3 = 0.5773502691896258;

        private readonly string _versionTag;

        public wsp_In11_Polyhedral_Box_Array_SDF()
            : base(NAME, NICK,
                "Generates a polyhedral lattice mesh inside a Rhino Box.\n" +
                "thickness=0: explicit face mesh (fast, planar).  thickness>0: SDF/Marching Cubes.\n" +
                "Cell types: 0=Truncated Octahedron (BCC), 1=Octahedron (SC).\n" +
                "Uses signed distance fields, following the same general SDF approach as Isopod.",
                CAT, SUBCAT)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v   = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.2.0";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("BBBC56A0-ABB8-4DF1-89E0-4632CAE108B3");

        public override GH_Exposure Exposure => GH_Exposure.hidden;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_In11_Polyhedral Box Array SDF.png"))
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
            pManager.AddBoxParameter(
                "box", "box",
                "Rhino Box that defines the polyhedral cell array domain.",
                GH_ParamAccess.item);

            pManager.AddGenericParameter(
                "trim_geo", "trim",
                "Optional Box or closed Brep/Mesh/Extrusion used as SDF clipping volume (forces SDF mode).",
                GH_ParamAccess.item);

            pManager.AddIntegerParameter(
                "type", "type",
                "Cell type: 0=Truncated Octahedron (BCC), 1=Octahedron (SC). Default: Octahedron.",
                GH_ParamAccess.item, 1);

            pManager.AddNumberParameter(
                "thickness", "inf_t",
                "Face thickness in Rhino model units. 0=explicit face mesh (fast); >0=SDF/Marching Cubes.",
                GH_ParamAccess.item, 0.0);

            pManager.AddNumberParameter(
                "shell_thickness", "shell_t",
                "Inward-only boundary shell thickness in model units. 0=no shell.",
                GH_ParamAccess.item, 0.0);

            pManager.AddBooleanParameter(
                "shell_caps", "caps",
                "True removes the top/bottom cap faces from the generated shell.",
                GH_ParamAccess.item, true);

            pManager.AddBooleanParameter(
                "invert_field", "invert",
                "Invert the extracted SDF region. False extracts the solid/infill region. True extracts the complementary region.",
                GH_ParamAccess.item, false);

            pManager.AddBooleanParameter(
                "disjoin_mesh", "disjoin",
                "True splits disconnected mesh islands into separate output mesh items. False outputs one joined mesh item.",
                GH_ParamAccess.item, false);

            pManager.AddIntegerParameter("count_x", "cx", "Cell repetitions along box X.", GH_ParamAccess.item, 3);
            pManager.AddIntegerParameter("count_y", "cy", "Cell repetitions along box Y.", GH_ParamAccess.item, 3);
            pManager.AddIntegerParameter("count_z", "cz", "Cell repetitions along box Z.", GH_ParamAccess.item, 3);

            pManager.AddNumberParameter(
                "resolution", "res",
                "Voxel size in model units (SDF mode only).",
                GH_ParamAccess.item, 2.0);

            pManager.AddBooleanParameter(
                "out_mesh", "mesh?",
                "When true (default), generates and outputs the lattice mesh.\n" +
                "When false, skips mesh generation for fast field-only evaluation.\n" +
                "The 'field' output is always available in SDF mode regardless of this setting.",
                GH_ParamAccess.item, true);

            pManager[1].Optional = true;
            for (int i = 2; i < pManager.ParamCount; i++)
                pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "mesh_out",
                "mesh",
                "Polyhedral lattice mesh output. If disjoin_mesh is false, outputs one joined mesh item. If disjoin_mesh is true, outputs disconnected islands as separate mesh items.",
                GH_ParamAccess.list);
            pManager.AddGenericParameter(
                "field", "field",
                "Signed distance field (negative inside, positive outside). " +
                "If disjoin_mesh is true and mesh generation is enabled, contains one mesh-derived SDF per disconnected island.",
                GH_ParamAccess.list);

            pManager.AddBrepParameter("bound_geo", "bound", "Brep of the input box domain.", GH_ParamAccess.item);
            pManager.AddTextParameter("cell_name", "cell", "Selected cell type name.", GH_ParamAccess.item);
            pManager.AddTextParameter("array", "array", "Array count as X.Y.Z.", GH_ParamAccess.item);
            pManager.AddTextParameter("info", "info", "Generation diagnostics and timing.", GH_ParamAccess.item);
        }

        // ─────────────────────────────────────────────────────────────────────
        // SolveInstance
        // ─────────────────────────────────────────────────────────────────────

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            int    type           = 1;
            double thickness      = 0.0;
            double shellThickness = 0.0;
            bool shellCaps = true;
            bool invertField = false;
            bool disjoinMesh = false;
            Box box = Box.Unset;
            object trimGeoRaw     = null;
            int    countX = 3, countY = 3, countZ = 3;
            double res            = 2.0;
            bool   outMesh        = true;
            bool   thicknessUnwired = IsInputUnwired(3);

            if (!DA.GetData(0, ref box) || !box.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Box input is required and must be valid.");
                DA.SetData(5, "ERR: invalid box."); Message = $"{_versionTag} | ERR"; return;
            }
            DA.GetData(1,  ref trimGeoRaw);
            DA.GetData(2,  ref type);
            DA.GetData(3,  ref thickness);
            DA.GetData(4,  ref shellThickness);
            DA.GetData(5, ref shellCaps);
            DA.GetData(6, ref invertField);
            DA.GetData(7, ref disjoinMesh);
            DA.GetData(8, ref countX);
            DA.GetData(9, ref countY);
            DA.GetData(10, ref countZ);
            DA.GetData(11, ref res);
            DA.GetData(12, ref outMesh);

            type           = Clamp(type, 0, 1);
            countX         = Math.Max(1, countX);
            countY         = Math.Max(1, countY);
            countZ         = Math.Max(1, countZ);
            thickness      = Math.Max(0.0, thickness);
            shellThickness = Math.Max(0.0, shellThickness);
            if (shellThickness > EPS && thicknessUnwired) thickness = shellThickness;

            // User-facing field inversion.
            // The SDF convention used by EvalParametricField is:
            // value < 0 = solid/infill region
            // value > 0 = outside/complementary region
            //
            // Therefore:
            // invert_field = false -> extract the normal solid/infill region
            // invert_field = true  -> extract the complementary region
            bool fieldInvert = invertField;

            if (res <= 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "resolution must be > 0.");
                DA.SetData(5, "ERR: resolution must be > 0."); Message = $"{_versionTag} | ERR"; return;
            }

            res = AvoidIntegerResolution(res);

            if (thickness > EPS && res > thickness * 0.5)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "resolution is too coarse for the requested thickness. For reliable model-unit thickness, use resolution <= thickness / 3, preferably thickness / 4.");
            }

            if (shellThickness > EPS && res > shellThickness * 0.5)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "resolution is too coarse for the requested shell_thickness. For reliable model-unit shell thickness, use resolution <= shell_thickness / 3, preferably shell_thickness / 4.");
            }

            double sizeX = Math.Abs(box.X.Length);
            double sizeY = Math.Abs(box.Y.Length);
            double sizeZ = Math.Abs(box.Z.Length);
            if (sizeX <= EPS || sizeY <= EPS || sizeZ <= EPS)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Box must have non-zero X, Y, Z lengths.");
                DA.SetData(5, "ERR: degenerate box."); Message = $"{_versionTag} | ERR"; return;
            }

            var sw = Stopwatch.StartNew();
            TrimVolume trimVolume = BuildTrimVolume(trimGeoRaw);
            if (trimGeoRaw != null && trimVolume == null)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "trim_geo ignored. Provide a Box, closed Brep, closed Mesh, or Extrusion.");

            // ── Decide mesh mode ──────────────────────────────────────────────
            bool useExplicit = thickness <= EPS && shellThickness <= EPS && trimVolume == null;

            // ── Pre-compute shared SDF parameters ─────────────────────────────
            bool   hasShellPre    = shellThickness > EPS;
            bool   useBoundarySdf = thickness > EPS || hasShellPre;
            double kx = countX / sizeX, ky = countY / sizeY, kz = countZ / sizeZ;

            // ── Build analytical WasperField closure (SDF mode only) ──────────
            string sourceTrace =
                "Source: In11 Polyhedral Box Array SDF\n" +
                $"type={PolyhedralTag(type)} ({type})\n" +
                $"thickness={thickness:G6}\n" +
                $"shell_thickness={shellThickness:G6}\n" +
                $"shell_caps={shellCaps}\n" +
                $"invert={invertField}\n" +
                $"counts={countX}x{countY}x{countZ}\n" +
                $"box_size={sizeX:G6},{sizeY:G6},{sizeZ:G6}\n" +
                $"trim_geo={trimVolume != null}\n" +
                $"explicit_mode={useExplicit}\n" +
                "quality=ApproximateSdf";

            WasperField analyticalField = null;
            if (!useExplicit)
            {
                Box        _cBox    = box;
                int        _cType   = type;
                double     _cThick  = thickness;
                bool       _cInvert = fieldInvert;
                int        _cCX     = countX, _cCY = countY, _cCZ = countZ;
                double     _cKx     = kx,     _cKy = ky,     _cKz = kz;
                bool       _cUBS    = useBoundarySdf;
                double     _cShellT = shellThickness;
                bool       _cShellC = shellCaps;
                TrimVolume _cTrim   = trimVolume;
                double     _cSX     = sizeX, _cSY = sizeY, _cSZ = sizeZ;

                analyticalField = new WasperField(
                    p =>
                    {
                        Point3d local = WorldToBox(p, _cBox.Plane);

                        if (!Contains(_cBox.X, local.X) ||
                            !Contains(_cBox.Y, local.Y) ||
                            !Contains(_cBox.Z, local.Z))
                            return TRIM;

                        double u = Normalize(local.X, _cBox.X);
                        double v2 = Normalize(local.Y, _cBox.Y);
                        double w = Normalize(local.Z, _cBox.Z);

                        return EvalParametricField(
                            p, u, v2, w,
                            _cType, _cThick, _cInvert,
                            _cCX, _cCY, _cCZ,
                            _cKx, _cKy, _cKz,
                            _cUBS, _cShellT, _cShellC,
                            _cTrim, _cSX, _cSY, _cSZ);
                    },
                    box.BoundingBox,
                    PolyhedralTag(type),
                    sourceTrace,
                    WasperFieldSdfQuality.ApproximateSdf);
            }

            Mesh   result         = null;
            List<Mesh> resultMeshes = new List<Mesh>();
            string meshMode       = "field_only";
            string timingInfo     = "n/a (mesh skipped)";
            int removedFragments  = 0;

            if (useExplicit)
            {
                // ── Explicit face mesh (thickness = 0) ────────────────────────
                if (invertField)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Remark,
                        "invert_field is ignored in explicit face mesh mode (thickness=0).");

                if (outMesh)
                {
                    var explicitWatch = Stopwatch.StartNew();
                    result = BuildExplicitPolyhedralMesh(
                        box, type, countX, countY, countZ,
                        sizeX, sizeY, sizeZ);
                    explicitWatch.Stop();

                    if (result != null && result.Faces.Count > 0)
                    {
                        int minFragFaces = 8;

                        if (disjoinMesh)
                        {
                            // Optional output split:
                            // separates disconnected mesh islands into individual mesh items.
                            resultMeshes = CleanAndSplitResultMeshes(
                                result,
                                30.0,
                                minFragFaces,
                                null,
                                Math.Max(res * 0.5, 1e-6),
                                out removedFragments);

                            result = JoinMeshList(resultMeshes);
                        }
                        else
                        {
                            // Default output:
                            // one joined mesh item, regardless of whether the internal topology
                            // contains disconnected pieces.
                            CleanResultMesh(
                                result,
                                180.0,
                                minFragFaces,
                                null,
                                Math.Max(res * 0.5, 1e-6),
                                out removedFragments);

                            if (result != null && result.Faces.Count > 0)
                                resultMeshes.Add(result);
                        }
                    }

                    meshMode   = "explicit_faces";
                    timingInfo = $"explicit {explicitWatch.ElapsedMilliseconds} ms | total {sw.ElapsedMilliseconds} ms";
                }
                else
                {
                    meshMode   = "explicit_faces (mesh skipped)";
                    timingInfo = "n/a";
                }
            }
            else
            {
                // ── SDF / Marching Cubes path ─────────────────────────────────
                if (outMesh)
                {
                    int nx = GridCount(sizeX, res);
                    int ny = GridCount(sizeY, res);
                    int nz = GridCount(sizeZ, res);
                    long totalSamples = (long)nx * ny * nz;

                    if (totalSamples > 20_000_000)
                    {
                        string msg = $"Grid {nx}x{ny}x{nz} = {totalSamples:N0} samples is too large. Increase resolution.";
                        AddRuntimeMessage(GH_RuntimeMessageLevel.Error, msg);
                        DA.SetData(5, "ERR: " + msg); Message = $"{_versionTag} | ERR"; return;
                    }

                    // IMPORTANT:
                    // The SDF sign convention remains stable:
                    //   sv < 0 = lattice/shell material
                    //   sv > 0 = complement / matrix
                    //
                    // However, invert_field must still be passed to EvalParametricField,
                    // because Invert=True needs boundary clamping to generate the exterior
                    // box/cap surfaces of the complementary region.
                    bool evalInvert = fieldInvert;

                    var evalWatch = Stopwatch.StartNew();
                    var scalars   = new double[nx * ny * nz];
                    var points    = new Point3d[nx * ny * nz];
                    int threads   = Math.Max(1, Environment.ProcessorCount - 1);

                    Parallel.For(0, nz, new ParallelOptions { MaxDegreeOfParallelism = threads }, iz =>
                    {
                        double w = nz <= 1 ? 0.0 : (double)iz / (nz - 1);
                        for (int iy = 0; iy < ny; iy++)
                        {
                            double v = ny <= 1 ? 0.0 : (double)iy / (ny - 1);
                            for (int ix = 0; ix < nx; ix++)
                            {
                                double u   = nx <= 1 ? 0.0 : (double)ix / (nx - 1);
                                int    idx = Idx(ix, iy, iz, nx, ny);
                                Point3d samplePoint = BoxPointAtNormalized(box, u, v, w);
                                points[idx] = samplePoint;

                                scalars[idx] = EvalParametricField(
                                    samplePoint, u, v, w,
                                    type, thickness, evalInvert,
                                    countX, countY, countZ,
                                    kx, ky, kz,
                                    useBoundarySdf, shellThickness, shellCaps,
                                    trimVolume, sizeX, sizeY, sizeZ);
                            }
                        }
                    });
                    evalWatch.Stop();

                    var meshWatch = Stopwatch.StartNew();
                    result = WasperMarchingCubes.Extract(
                        scalars,
                        points,
                        nx,
                        ny,
                        nz,
                        0.0,
                        Math.Max(res * 1e-6, 1e-9),
                        threads);
                    meshWatch.Stop();

                    var cleanWatch = Stopwatch.StartNew();

                    if (result != null && result.Faces.Count > 0)
                    {
                        int minFragFaces = 8;

                        if (disjoinMesh)
                        {
                            resultMeshes = CleanAndSplitResultMeshes(
                                result,
                                30.0,
                                minFragFaces,
                                analyticalField,
                                Math.Max(res * 0.5, 1e-6),
                                out removedFragments);

                            result = JoinMeshList(resultMeshes);
                        }
                        else
                        {
                            CleanResultMesh(
                                result,
                                180.0,
                                minFragFaces,
                                analyticalField,
                                Math.Max(res * 0.5, 1e-6),
                                out removedFragments);

                            if (result != null && result.Faces.Count > 0)
                                resultMeshes.Add(result);
                        }
                    }

                    cleanWatch.Stop();

                    meshMode =
                        $"sdf_marching_cubes  grid:{nx}x{ny}x{nz}={totalSamples:N0}  threads:{threads}  " +
                        $"disjoin:{disjoinMesh}  output_meshes:{resultMeshes.Count}  frags_removed:{removedFragments}";

                    timingInfo =
                        $"eval {evalWatch.ElapsedMilliseconds} ms | " +
                        $"extract {meshWatch.ElapsedMilliseconds} ms | " +
                        $"clean {cleanWatch.ElapsedMilliseconds} ms | " +
                        $"total {sw.ElapsedMilliseconds} ms";
                }
                else
                {
                    meshMode   = "field_only (mesh skipped)";
                    timingInfo = "n/a";
                }
            }

            sw.Stop();
            Brep bound = box.ToBrep();

            if (outMesh && (result == null || result.Faces.Count == 0))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "No lattice mesh produced. Check inputs.");

            string cell  = PolyhedralTag(type);
            string array = $"{countX}.{countY}.{countZ}";
            string info  =
                $"{NAME}  {_versionTag}\n" +
                $"mesh_mode       : {meshMode}\n" +
                $"type            : {type} ({cell})\n" +
                $"thickness       : {thickness:0.###}\n" +
                $"shell_thickness : {shellThickness:0.###}\n" +
                $"shell_caps      : {shellCaps}\n" +
                $"invert_field    : {invertField}\n" +
                $"disjoin_mesh    : {disjoinMesh}\n" +
                $"out_mesh        : {outMesh}\n" +
                $"array           : {array}\n" +
                $"resolution      : {res:0.###} model units\n" +
                $"trim_geo        : {trimVolume != null}\n" +
                $"disjoint meshes : {resultMeshes.Count:N0}\n" +
                $"frags removed   : {removedFragments:N0}\n" +
                $"mesh vertices   : {(result == null ? 0 : result.Vertices.Count):N0}\n" +
                $"mesh faces      : {(result == null ? 0 : result.Faces.Count):N0}\n" +
                $"timing          : {timingInfo}";

            var fieldOutputs = BuildFieldOutputs(
                disjoinMesh,
                outMesh,
                resultMeshes,
                analyticalField,
                result,
                cell,
                sourceTrace);

            DA.SetDataList(0, resultMeshes);
            DA.SetDataList(1, fieldOutputs);
            DA.SetData(2, bound);
            DA.SetData(3, cell);
            DA.SetData(4, array);
            DA.SetData(5, info);

            Message = !outMesh && !useExplicit
                ? $"{_versionTag} | field only"
                : result == null || result.Faces.Count == 0
                    ? $"{_versionTag} | empty"
                    : $"{_versionTag} | {cell}";
        }

        // ─────────────────────────────────────────────────────────────────────
        // Explicit face mesh (thickness = 0)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Generates a zero-thickness polyhedral face mesh directly from known cell geometry.
        /// No SDF, no voxelisation.  All face polygons are planar by construction.
        /// </summary>
        private static Mesh BuildExplicitPolyhedralMesh(
            Box box, int type,
            int countX, int countY, int countZ,
            double sizeX, double sizeY, double sizeZ)
        {
            // Local geometry (cell-fraction coords, cell centered at origin)
            double[] lv;      // flat array: x0,y0,z0, x1,y1,z1, ...
            int[][]  faces;   // each entry = vertex indices for one face (CCW from outside)
            GetCellGeometry(type, out lv, out faces);

            // BCC type 0: two interleaved SC sub-lattices.
            // Sub-lattice A: centers at (i, j, k) integers — used for offset sub-lattice
            //   so that both sit at cell-fraction-aligned centres.
            // We use centres in cell-coordinate space (0..countN) and transform via box.
            //
            // For Truncated Octahedron BCC: centres at
            //   (i+0.5, j+0.5, k+0.5)  for i in 0..countX-1  (SC sub-lattice A)
            //   (i,     j,     k    )   for i in 0..countX    (SC sub-lattice B — integer grid)
            //
            // For Octahedron SC: centres only at (i+0.5, j+0.5, k+0.5).
            //
            // We enumerate all centres whose truncated octahedron/octahedron
            // has at least one face interior to [0..countX]^3.

            var mesh = new Mesh();
            var faceKeys = new HashSet<FaceKey>();

            double tol = Math.Min(sizeX / countX,
                         Math.Min(sizeY / countY, sizeZ / countZ)) * 1e-5;

            // face interior threshold: face centre must be strictly inside domain
            double faceMargin = 1e-4;

            Action<double, double, double> addCell = (cx, cy, cz) =>
            {
                int nv = lv.Length / 3;
                foreach (int[] faceIdx in faces)
                {
                    // Compute face centre in cell coords
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

                    // Discard faces outside domain
                    if (fcx < -faceMargin || fcx > countX + faceMargin) continue;
                    if (fcy < -faceMargin || fcy > countY + faceMargin) continue;
                    if (fcz < -faceMargin || fcz > countZ + faceMargin) continue;
                    if (fcx < faceMargin || fcx > countX - faceMargin) continue;
                    if (fcy < faceMargin || fcy > countY - faceMargin) continue;
                    if (fcz < faceMargin || fcz > countZ - faceMargin) continue;

                    // Dedup by full face vertex set in cell coordinates.
                    // Face centre alone is not safe enough for truncated octahedron arrays.
                    var fk = BuildFaceKey(cx, cy, cz, lv, faceIdx);
                    if (!faceKeys.Add(fk)) continue;

                    // Resolve world-space vertices
                    int[] vIdx = new int[faceIdx.Length];
                    for (int i = 0; i < faceIdx.Length; i++)
                    {
                        int vi = faceIdx[i];
                        double wcx = cx + lv[vi * 3];
                        double wcy = cy + lv[vi * 3 + 1];
                        double wcz = cz + lv[vi * 3 + 2];
                        double u   = wcx / countX;
                        double v2  = wcy / countY;
                        double w   = wcz / countZ;
                        Point3d p  = BoxPointAtNormalized(box, u, v2, w);
                        vIdx[i] = mesh.Vertices.Add(p);
                    }

                    // Triangulate polygon (fan from v0)
                    // Winding: faceIdx is defined CCW from outside normal
                    for (int i = 1; i < vIdx.Length - 1; i++)
                        mesh.Faces.AddFace(vIdx[0], vIdx[i], vIdx[i + 1]);
                }
            };

            if (type == 0) // Truncated Octahedron BCC — two sub-lattices
            {
                // Sub-lattice A: centres at (i+0.5, j+0.5, k+0.5)
                for (int i = 0; i < countX; i++)
                for (int j = 0; j < countY; j++)
                for (int k = 0; k < countZ; k++)
                    addCell(i + 0.5, j + 0.5, k + 0.5);

                // Sub-lattice B: centres at (i, j, k) integers
                for (int i = 0; i <= countX; i++)
                for (int j = 0; j <= countY; j++)
                for (int k = 0; k <= countZ; k++)
                    addCell(i, j, k);
            }
            else // Octahedron SC — single sub-lattice
            {
                for (int i = 0; i < countX; i++)
                for (int j = 0; j < countY; j++)
                for (int k = 0; k < countZ; k++)
                    addCell(i + 0.5, j + 0.5, k + 0.5);
            }

            if (mesh.Faces.Count == 0) return null;

            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();
            mesh.Normals.ComputeNormals();
            mesh.FaceNormals.ComputeFaceNormals();
            mesh.Compact();
            return mesh;
        }

        /// <summary>
        /// Returns local vertices and face index lists for one cell (centred at origin).
        /// Vertices are in cell-fraction coordinates, range [-0.5, 0.5].
        /// Face winding is CCW when viewed from the outward normal.
        /// </summary>
        private static void GetCellGeometry(int type, out double[] lv, out int[][] faces)
        {
            if (type == 0) // Truncated Octahedron — BCC lattice, consistent with SDF field
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

                    var planes = new FacePlane[]
                    {
            new FacePlane( 1,  0,  0, 0.5),
            new FacePlane(-1,  0,  0, 0.5),
            new FacePlane( 0,  1,  0, 0.5),
            new FacePlane( 0, -1,  0, 0.5),
            new FacePlane( 0,  0,  1, 0.5),
            new FacePlane( 0,  0, -1, 0.5),

            new FacePlane( 1,  1,  1, 0.75),
            new FacePlane( 1,  1, -1, 0.75),
            new FacePlane( 1, -1,  1, 0.75),
            new FacePlane(-1,  1,  1, 0.75),
            new FacePlane(-1, -1, -1, 0.75),
            new FacePlane(-1, -1,  1, 0.75),
            new FacePlane(-1,  1, -1, 0.75),
            new FacePlane( 1, -1, -1, 0.75),
                };

                faces = BuildFacesFromPlanes(lv, planes, 1e-9);
            }
            else // Octahedron SC — 6 vertices, 8 triangular faces
            {
                lv = new double[]
                {
			/*  0 */  0.5,  0,    0,
			/*  1 */ -0.5,  0,    0,
			/*  2 */  0,    0.5,  0,
			/*  3 */  0,   -0.5,  0,
			/*  4 */  0,    0,    0.5,
			/*  5 */  0,    0,   -0.5,
                };

                var planes = new FacePlane[]
                {
            new FacePlane( 1,  1,  1, 0.5),
            new FacePlane( 1,  1, -1, 0.5),
            new FacePlane( 1, -1,  1, 0.5),
            new FacePlane( 1, -1, -1, 0.5),
            new FacePlane(-1,  1,  1, 0.5),
            new FacePlane(-1,  1, -1, 0.5),
            new FacePlane(-1, -1,  1, 0.5),
            new FacePlane(-1, -1, -1, 0.5),
                };

                faces = BuildFacesFromPlanes(lv, planes, 1e-9);
            }
        }

        private readonly struct FacePlane
        {
            public readonly double Nx;
            public readonly double Ny;
            public readonly double Nz;
            public readonly double Offset;

            public FacePlane(double nx, double ny, double nz, double offset)
            {
                Nx = nx;
                Ny = ny;
                Nz = nz;
                Offset = offset;
            }
        }

        private static int[][] BuildFacesFromPlanes(double[] lv, FacePlane[] planes, double tol)
        {
            var result = new List<int[]>();

            for (int pi = 0; pi < planes.Length; pi++)
            {
                FacePlane fp = planes[pi];

                var ids = new List<int>();
                int nv = lv.Length / 3;

                for (int vi = 0; vi < nv; vi++)
                {
                    double x = lv[vi * 3];
                    double y = lv[vi * 3 + 1];
                    double z = lv[vi * 3 + 2];

                    double d = fp.Nx * x + fp.Ny * y + fp.Nz * z;

                    if (Math.Abs(d - fp.Offset) <= tol)
                        ids.Add(vi);
                }

                if (ids.Count < 3)
                    continue;

                Point3d center = Point3d.Origin;
                for (int i = 0; i < ids.Count; i++)
                {
                    int vi = ids[i];
                    center += new Vector3d(lv[vi * 3], lv[vi * 3 + 1], lv[vi * 3 + 2]);
                }
                center /= ids.Count;

                Vector3d normal = new Vector3d(fp.Nx, fp.Ny, fp.Nz);
                if (!normal.Unitize())
                    continue;

                Vector3d axisX = Vector3d.CrossProduct(Vector3d.ZAxis, normal);
                if (!axisX.Unitize())
                {
                    axisX = Vector3d.CrossProduct(Vector3d.XAxis, normal);
                    if (!axisX.Unitize())
                        continue;
                }

                Vector3d axisY = Vector3d.CrossProduct(normal, axisX);
                if (!axisY.Unitize())
                    continue;

                var order = new int[ids.Count];
                var angle = new double[ids.Count];

                for (int i = 0; i < ids.Count; i++)
                {
                    order[i] = i;

                    int vi = ids[i];
                    Point3d p = new Point3d(lv[vi * 3], lv[vi * 3 + 1], lv[vi * 3 + 2]);
                    Vector3d dv = p - center;

                    angle[i] = Math.Atan2(dv * axisY, dv * axisX);
                }

                for (int i = 1; i < order.Length; i++)
                {
                    int key = order[i];
                    double keyAngle = angle[key];
                    int j = i - 1;

                    while (j >= 0 && angle[order[j]] > keyAngle)
                    {
                        order[j + 1] = order[j];
                        j--;
                    }

                    order[j + 1] = key;
                }

                var face = new int[ids.Count];
                for (int i = 0; i < order.Length; i++)
                    face[i] = ids[order[i]];

                if (!FaceWindingMatchesNormal(lv, face, normal))
                    Array.Reverse(face);

                result.Add(face);
            }

            return result.ToArray();
        }

        private static bool FaceWindingMatchesNormal(double[] lv, int[] face, Vector3d normal)
        {
            if (face == null || face.Length < 3)
                return false;

            Point3d p0 = LocalVertex(lv, face[0]);
            Point3d p1 = LocalVertex(lv, face[1]);
            Point3d p2 = LocalVertex(lv, face[2]);

            Vector3d fn = Vector3d.CrossProduct(p1 - p0, p2 - p0);
            if (!fn.Unitize())
                return true;

            return fn * normal >= 0.0;
        }

        private static Point3d LocalVertex(double[] lv, int index)
        {
            return new Point3d(
                lv[index * 3],
                lv[index * 3 + 1],
                lv[index * 3 + 2]);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Polyhedral SDF field evaluation - THIS SNIPPET IS VERY IMPORTANT
        // ─────────────────────────────────────────────────────────────────────
        //
        // DESCRIPTION
        // This function evaluates the scalar field used by Marching Cubes.
        //
        // The field convention in this snippet is:
        //	finalValue < 0  = region to be extracted as material
        //	finalValue = 0  = generated mesh surface
        //	finalValue > 0  = region outside the extracted material
        //
        // The lattice field is built first from the distance to the closest
        // polyhedral face family. A point becomes part of the finite-thickness
        // lattice band when its distance to the nearest face is smaller than
        // thickness / 2.
        //
        // If shell_thickness > 0, an inward shell field is also generated and
        // unioned with the lattice field using SDF union:
        //	min(lattice, shell)
        //
        // If invertField = false, the function returns the normal lattice/shell
        // material field.
        //
        // If invertField = true, the function returns the volumetric complement
        // of that material field, clipped back to the box boundary.
        //
        // trimVolume, when provided, clips the final field to the trim geometry.
        // ─────────────────────────────────────────────────────────────────────

        private static double EvalParametricField(
            Point3d point,
            double u, double v, double w,
            int type,
            double thickness,
            bool invertField,
            int countX, int countY, int countZ,
            double kx, double ky, double kz,
            bool useBoundarySdf,
            double shellThickness,
            bool shellCaps,
            TrimVolume trimVolume,
            double sizeX, double sizeY, double sizeZ)
        {
            double cx = countX * u;
            double cy = countY * v;
            double cz = countZ * w;

            // Avoid grid/lattice resonance: some resolutions place many samples
            // exactly on polyhedral zero planes, which makes Marching Cubes
            // topology depend on whether "zero" is classified inside or outside.
            // Keep boundary samples untouched so box clipping and caps remain exact.
            if (u > EPS && u < 1.0 - EPS) cx += SDF_PHASE_NUDGE;
            if (v > EPS && v < 1.0 - EPS) cy += 2.0 * SDF_PHASE_NUDGE;
            if (w > EPS && w < 1.0 - EPS) cz += 3.0 * SDF_PHASE_NUDGE;

            // Temporary change #4
            // MODIFIED SNIPPET
            double faceDistance = PolyhedralDistanceWorld(
                type,
                cx, cy, cz,
                kx, ky, kz);

            // Thickness remains in model units.
            // Do NOT add +0.5 unless you intentionally want to over-thicken the result.
            double effectiveHalfThickness = Math.Max(0.0, thickness * 0.5);

            // Normal lattice field:
            //	latticeValue < 0 = finite-thickness polyhedral face band
            //	latticeValue > 0 = outside the band
            double latticeValue = faceDistance - effectiveHalfThickness;

            bool hasShell = shellThickness > EPS;

            double boundarySdf = BoxBoundarySdf(u, v, w, sizeX, sizeY, sizeZ);

            // Build the normal infill field first.
            // This is the polyhedral lattice / infill before adding any shell.
            double infillField = latticeValue;

            // This is the final material field used by the component.
            // Without a shell, materialField = infillField.
            // With a shell, materialField = (infillField ∪ shellField) ∩ box.
            double materialField = infillField;

            // IMPORTANT:
            // In SDF mode, always clip the material field to the box.
            // Without this, the normal lattice/infill region can remain open/disconnected
            // at the box boundary, especially for diagonal/octahedral face networks.
            if (useBoundarySdf)
            {
                materialField = Math.Max(materialField, boundarySdf);
            }

            if (hasShell)
            {
                double shellBoundarySdf = shellCaps
                    ? BoxLateralBoundarySdf(u, v, sizeX, sizeY)
                    : boundarySdf;

                // Negative inside the inward shell band.
                double shellField = InwardShellSdf(shellBoundarySdf, shellThickness);

                // Keep the shell inside the box.
                shellField = Math.Max(shellField, boundarySdf);

                // At this point:
                //	infillField < 0 = finite-thickness polyhedral lattice/infill
                //	shellField  < 0 = inward boundary shell
                //
                // Union lattice + shell:
                //	(lattice ∪ shell)
                // SDF union = min(A, B)
                double infillShellUnionField = Math.Min(infillField, shellField);

                // Final safety intersection:
                //	(lattice ∪ shell) ∩ box
                // SDF intersection = max(A, B)
                //
                // This guarantees the resulting material field cannot extend outside
                // the box after the union operation.
                materialField = Math.Max(infillShellUnionField, boundarySdf);
            }

            double finalValue;

            if (!invertField)
            {
                // Normal mode:
                //	finalValue < 0 = lattice/shell material.
                finalValue = materialField;
            }
            else
            {
                // Inverted mode:
                //	finalValue < 0 = complementary matrix region.
                //
                // This creates the volumetric opposite of the lattice/shell material field.
                finalValue = -materialField;

                // Clip the complement to the box.
                // This creates exterior box/cap surfaces for the matrix.
                finalValue = Math.Max(finalValue, boundarySdf);
            }

            // END OF SNIPPET #4

            if (trimVolume != null)
            {
                // Clip final field to trim volume.
                // Inside trimVolume = negative, outside = positive.
                finalValue = Math.Max(finalValue, trimVolume.SignedDistance(point));
            }

            return finalValue;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Lattice face-plane SDF helpers
        // ─────────────────────────────────────────────────────────────────────

        private static double HalfIntFrac(double x)
        {
            double f = x - Math.Floor(x);
            return Math.Abs(f - 0.5);
        }

        private static double HexPeriodDist(double s)
        {
            double f = ((s - 0.75) % 1.5 + 1.5) % 1.5;
            return Math.Min(f, 1.5 - f);
        }

        private static double HexFaceDist(double cx, double cy, double cz)
        {
            double s1 = cx + cy + cz;
            double s2 = cx + cy - cz;
            double s3 = cx - cy + cz;
            double s4 = -cx + cy + cz;
            return Math.Min(
                Math.Min(HexPeriodDist(s1), HexPeriodDist(s2)),
                Math.Min(HexPeriodDist(s3), HexPeriodDist(s4))) * INV_SQRT3;
        }

        private static double PolyhedralDistanceWorld(
            int type,
            double cx, double cy, double cz,
            double kx, double ky, double kz)
        {
            double diagScale = Math.Sqrt(kx * kx + ky * ky + kz * kz);

            if (diagScale <= EPS)
                return TRIM;

            if (type == 0)
            {
                double squareDist = TruncatedOctaSquareDistanceWorld(cx, cy, cz, kx, ky, kz);
                double hexDist = TruncatedOctaHexDistanceWorld(cx, cy, cz, diagScale);

                return Math.Min(squareDist, hexDist);
            }

            return OctahedronDistanceWorld(cx, cy, cz, diagScale);
        }

        private static double TruncatedOctaSquareDistanceWorld(
            double cx, double cy, double cz,
            double kx, double ky, double kz)
        {
            double dx = HalfIntFrac(cx) / Math.Max(kx, EPS);
            double dy = HalfIntFrac(cy) / Math.Max(ky, EPS);
            double dz = HalfIntFrac(cz) / Math.Max(kz, EPS);

            return Math.Min(dx, Math.Min(dy, dz));
        }

        private static double TruncatedOctaHexDistanceWorld(
            double cx, double cy, double cz,
            double diagScale)
        {
            double s1 = cx + cy + cz;
            double s2 = cx + cy - cz;
            double s3 = cx - cy + cz;
            double s4 = -cx + cy + cz;

            double d = Math.Min(
                Math.Min(HexPeriodDist(s1), HexPeriodDist(s2)),
                Math.Min(HexPeriodDist(s3), HexPeriodDist(s4)));

            return d / Math.Max(diagScale, EPS);
        }

        private static double OctahedronDistanceWorld(
            double cx, double cy, double cz,
            double diagScale)
        {
            double s1 = cx + cy + cz;
            double s2 = cx + cy - cz;
            double s3 = cx - cy + cz;
            double s4 = -cx + cy + cz;

            double d = Math.Min(
                Math.Min(HalfIntFrac(s1), HalfIntFrac(s2)),
                Math.Min(HalfIntFrac(s3), HalfIntFrac(s4)));

            return d / Math.Max(diagScale, EPS);
        }
        private static double OctFamilyDist(double s)
            => HalfIntFrac(s) * INV_SQRT3;


        // ─────────────────────────────────────────────────────────────────────
        // Per-point analytical gradient scale (accurate thickness in model units)
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the world-space gradient magnitude of PolyhedralValue at (cx,cy,cz).
        ///
        /// Square faces have |∇f_world| = kN (the axis-aligned stretch factor).
        /// Hex / diagonal faces have |∇f_world| = sqrt(kx²+ky²+kz²) / sqrt(3),
        /// because HexFaceDist is normalised by INV_SQRT3.
        /// Using this per-point scale gives accurate model-unit thickness for
        /// both square and hex families even on anisotropic boxes.
        /// </summary>

        private static string PolyhedralTag(int type)
        {
            switch (type)
            {
                case 0: return "Trunc. Octahedron";
                case 1: return "Octahedron";
                default: return "?";
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Box domain helpers
        // ─────────────────────────────────────────────────────────────────────

        private static Point3d WorldToBox(Point3d p, Plane plane)
        {
            Vector3d d = p - plane.Origin;

            return new Point3d(
                d * plane.XAxis,
                d * plane.YAxis,
                d * plane.ZAxis);
        }

        private static bool Contains(Interval interval, double value)
        {
            double t0 = Math.Min(interval.T0, interval.T1);
            double t1 = Math.Max(interval.T0, interval.T1);

            return value >= t0 - EPS && value <= t1 + EPS;
        }

        private static double Normalize(double value, Interval interval)
        {
            double len = interval.T1 - interval.T0;

            return Math.Abs(len) > EPS
                ? (value - interval.T0) / len
                : 0.5;
        }

        private static double BoxBoundarySdf(double u, double v, double w, double sizeX, double sizeY, double sizeZ)
        {
            double dx = Math.Min(Clamp01(u), 1.0 - Clamp01(u)) * Math.Max(sizeX, EPS);
            double dy = Math.Min(Clamp01(v), 1.0 - Clamp01(v)) * Math.Max(sizeY, EPS);
            double dz = Math.Min(Clamp01(w), 1.0 - Clamp01(w)) * Math.Max(sizeZ, EPS);
            return -Math.Min(dx, Math.Min(dy, dz));
        }

        private static double BoxLateralBoundarySdf(double u, double v, double sizeX, double sizeY)
        {
            double dx = Math.Min(Clamp01(u), 1.0 - Clamp01(u)) * Math.Max(sizeX, EPS);
            double dy = Math.Min(Clamp01(v), 1.0 - Clamp01(v)) * Math.Max(sizeY, EPS);
            return -Math.Min(dx, dy);
        }

        private static double InwardShellSdf(double boundarySdf, double shellThickness)
        {
            shellThickness = Math.Max(shellThickness, EPS);
            return Math.Abs(boundarySdf + 0.5 * shellThickness) - 0.5 * shellThickness;
        }

        private static Point3d BoxPointAtNormalized(Box box, double u, double v, double w)
        {
            double lx = Lerp(box.X.T0, box.X.T1, u);
            double ly = Lerp(box.Y.T0, box.Y.T1, v);
            double lz = Lerp(box.Z.T0, box.Z.T1, w);
            return box.Plane.Origin
                 + box.Plane.XAxis * lx
                 + box.Plane.YAxis * ly
                 + box.Plane.ZAxis * lz;
        }

        // ─────────────────────────────────────────────────────────────────────
        // TrimVolume
        // ─────────────────────────────────────────────────────────────────────

        private static TrimVolume BuildTrimVolume(object geometry)
        {
            if (geometry == null) return null;
            if (geometry is Grasshopper.Kernel.Types.IGH_Goo goo)
            {
                object sv = goo.ScriptVariable();
                if (sv != null && !ReferenceEquals(sv, geometry)) return BuildTrimVolume(sv);
            }
            if (geometry is Box box)   return box.IsValid ? new TrimVolume(box) : null;
            if (geometry is GeometryBase gb) return BuildTrimVolumeFromGeometry(gb);
            return null;
        }

        private static TrimVolume BuildTrimVolumeFromGeometry(GeometryBase geometry)
        {
            if (geometry is Brep brep)
            {
                var b = brep.DuplicateBrep();
                return (b != null && b.IsValid && b.IsSolid) ? new TrimVolume(b, null) : null;
            }
            if (geometry is Extrusion ext)
            {
                Brep b = ext.ToBrep();
                return (b != null && b.IsValid && b.IsSolid) ? new TrimVolume(b, null) : null;
            }
            if (geometry is Mesh mesh)
            {
                var m = mesh.DuplicateMesh();
                return (m != null && m.IsValid && m.IsClosed) ? new TrimVolume(null, m) : null;
            }
            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Marching Cubes
        // ─────────────────────────────────────────────────────────────────────

        private static Mesh MarchingCubesTableParallel(
            double[] scalars,
            Box box,
            int nx,
            int ny,
            int nz,
            double step,
            bool extractComplement)
        {
            if (scalars == null || scalars.Length == 0)
                return null;

            int sliceCount = Math.Max(0, nz - 1);
            if (sliceCount <= 0)
                return null;

            var sliceMeshes = new Mesh[sliceCount];
            int threads = Math.Max(1, Environment.ProcessorCount - 1);

            Parallel.For(0, sliceCount, new ParallelOptions { MaxDegreeOfParallelism = threads }, iz =>
            {
                var localMesh = new Mesh();
                var localVertexMap = new Dictionary<VertexKey, int>();

                ProcessMarchingCubesTableSlice(
                    scalars,
                    box,
                    nx,
                    ny,
                    nz,
                    step,
                    iz,
                    extractComplement,
                    localMesh,
                    localVertexMap);

                if (localMesh.Faces.Count > 0)
                {
                    localMesh.Vertices.CombineIdentical(true, true);
                    localMesh.Faces.CullDegenerateFaces();
                    localMesh.Vertices.CullUnused();
                    localMesh.Compact();

                    sliceMeshes[iz] = localMesh;
                }
            });

            var mesh = new Mesh();

            for (int i = 0; i < sliceMeshes.Length; i++)
            {
                if (sliceMeshes[i] == null || sliceMeshes[i].Faces.Count == 0)
                    continue;

                mesh.Append(sliceMeshes[i]);
            }

            if (mesh.Faces.Count == 0)
                return null;

            mesh.Vertices.CombineIdentical(true, true);
            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();

            mesh.Weld(RhinoMath.ToRadians(180.0));
            mesh.UnifyNormals();
            mesh.Normals.ComputeNormals();
            mesh.FaceNormals.ComputeFaceNormals();
            mesh.Compact();

            return mesh;
        }

        private static void ProcessMarchingCubesTableSlice(
            double[] scalars,
            Box box,
            int nx,
            int ny,
            int nz,
            double step,
            int iz,
            bool invertField,
            Mesh mesh,
            Dictionary<VertexKey, int> vertexMap)
        {
            int[,] cubeCorners =
            {
        {0,0,0},
        {1,0,0},
        {1,1,0},
        {0,1,0},
        {0,0,1},
        {1,0,1},
        {1,1,1},
        {0,1,1}
    };

            int[,] edgeCorners =
            {
        {0,1},
        {1,2},
        {2,3},
        {3,0},
        {4,5},
        {5,6},
        {6,7},
        {7,4},
        {0,4},
        {1,5},
        {2,6},
        {3,7}
    };

            double keyTol = Math.Max(step * 1e-6, 1e-9);

            double[] sv = new double[8];
            Point3d[] cp = new Point3d[8];
            Point3d[] edgeVerts = new Point3d[12];

            for (int iy = 0; iy < ny - 1; iy++)
                for (int ix = 0; ix < nx - 1; ix++)
                {
                    int cubeIndex = 0;

                    for (int c = 0; c < 8; c++)
                    {
                        int gx = ix + cubeCorners[c, 0];
                        int gy = iy + cubeCorners[c, 1];
                        int gz = iz + cubeCorners[c, 2];

                        int idx = Idx(gx, gy, gz, nx, ny);

                        double u = nx <= 1 ? 0.0 : (double)gx / (nx - 1);
                        double v = ny <= 1 ? 0.0 : (double)gy / (ny - 1);
                        double w = nz <= 1 ? 0.0 : (double)gz / (nz - 1);

                        sv[c] = scalars[idx];
                        cp[c] = BoxPointAtNormalized(box, u, v, w);

                        // Field extraction convention:
                        //
                        // invertField = false (lattice bands):
                        //   Extract the negative region (sv < 0 = inside the face band).
                        //
                        // invertField = true (solid matrix / complement):
                        //   Extract the non-negative region (sv >= 0).
                        //   Using >= instead of > is essential for end-caps:
                        //   EvalParametricField clamps boundary pore nodes to sv = 0 via
                        //   Max(value, boundarySdf).  With sv >= 0, those nodes are treated
                        //   as solid, so MC generates a cap surface between each boundary
                        //   pore node (sv = 0) and its adjacent interior pore node (sv < 0).

                        bool inside = sv[c] < 0.0;   //Temporary change #3
                        //bool inside = invertField
                          //  ? sv[c] >= 0.0
                            //: sv[c] < 0.0;

                        if (inside)
                            cubeIndex |= 1 << c;
                    }

                    if (cubeIndex == 0 || cubeIndex == 255)
                        continue;

                    for (int e = 0; e < 12; e++)
                        edgeVerts[e] = Point3d.Unset;

                    for (int t = 0; t <= 12; t += 3)
                    {
                        int e0 = MarchingCubesClassicTable.TriTable[cubeIndex, t];

                        if (e0 < 0)
                            break;

                        int e1 = MarchingCubesClassicTable.TriTable[cubeIndex, t + 1];
                        int e2 = MarchingCubesClassicTable.TriTable[cubeIndex, t + 2];

                        // Defensive safety:
                        // Marching Cubes edge indices must always be in [0..11].
                        // If the table row is malformed or incomplete, skip safely.
                        if (e0 < 0 || e0 >= 12 ||
                            e1 < 0 || e1 >= 12 ||
                            e2 < 0 || e2 >= 12)
                            break;

                        if (!edgeVerts[e0].IsValid)
                            edgeVerts[e0] = GetInterpolatedEdgeVertex(e0, edgeCorners, cp, sv);

                        if (!edgeVerts[e1].IsValid)
                            edgeVerts[e1] = GetInterpolatedEdgeVertex(e1, edgeCorners, cp, sv);

                        if (!edgeVerts[e2].IsValid)
                            edgeVerts[e2] = GetInterpolatedEdgeVertex(e2, edgeCorners, cp, sv);

                        int a = AddVertex(mesh, vertexMap, edgeVerts[e0], keyTol);
                        int b = AddVertex(mesh, vertexMap, edgeVerts[e1], keyTol);
                        int c = AddVertex(mesh, vertexMap, edgeVerts[e2], keyTol);

                        if (a == b || b == c || c == a)
                            continue;

                        mesh.Faces.AddFace(a, c, b);
                    }
                }
        }

        private static Point3d GetInterpolatedEdgeVertex(
            int edgeIndex,
            int[,] edgeCorners,
            Point3d[] cp,
            double[] sv)
        {
            int a = edgeCorners[edgeIndex, 0];
            int b = edgeCorners[edgeIndex, 1];

            return InterpolateIsoVertex(cp[a], cp[b], sv[a], sv[b]);
        }

        private static Point3d InterpolateIsoVertex(Point3d p0, Point3d p1, double v0, double v1)
        {
            double d = v0 - v1;

            if (Math.Abs(d) < 1e-14)
            {
                return new Point3d(
                    0.5 * (p0.X + p1.X),
                    0.5 * (p0.Y + p1.Y),
                    0.5 * (p0.Z + p1.Z));
            }

            double t = v0 / d;
            t = Clamp01(t);

            return p0 + t * (p1 - p0);
        }






        private static int AddVertex(Mesh mesh, Dictionary<VertexKey,int> map, Point3d p, double tol)
        {
            var key = new VertexKey(p, tol);
            if (map.TryGetValue(key, out int idx)) return idx;
            idx = mesh.Vertices.Add(p); map[key] = idx; return idx;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Mesh cleanup
        // ─────────────────────────────────────────────────────────────────────

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

            int fc = mesh.Faces.Count;
            var v2f = new Dictionary<int, List<int>>();
            for (int fi = 0; fi < fc; fi++)
            {
                MeshFace f = mesh.Faces[fi];
                AddFV(v2f, f.A, fi); AddFV(v2f, f.B, fi); AddFV(v2f, f.C, fi);
                if (f.IsQuad) AddFV(v2f, f.D, fi);
            }

            bool[] visited = new bool[fc];
            var queue = new Queue<int>();

            for (int seed = 0; seed < fc; seed++)
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

        private static List<WasperFieldGoo> BuildFieldOutputs(
            bool disjoinMesh,
            bool outMesh,
            List<Mesh> resultMeshes,
            WasperField analyticalField,
            Mesh joinedMesh,
            string label,
            string sourceTrace)
        {
            var fields = new List<WasperFieldGoo>();

            if (disjoinMesh && outMesh && resultMeshes != null && resultMeshes.Count > 0
                && analyticalField != null)
            {
                // Each island gets the analytical field (always correct sign) clipped to
                // its own bounding box. WasperField.FromMesh on open lattice meshes gives
                // wrong signs because mesh.IsPointInside() is unreliable on non-closed meshes.
                for (int i = 0; i < resultMeshes.Count; i++)
                {
                    BoundingBox islandBB = resultMeshes[i].GetBoundingBox(true);
                    islandBB.Inflate(islandBB.Diagonal.Length * 0.005);

                    WasperField capturedField = analyticalField;
                    BoundingBox capturedBB    = islandBB;
                    WasperField f = new WasperField(
                        p => capturedBB.Contains(p) ? capturedField.Evaluate(p) : double.PositiveInfinity,
                        capturedBB,
                        $"{label}_{i + 1}",
                        capturedField.OperationTrace + Environment.NewLine + $"1. IslandClip(index={i + 1}) | quality={capturedField.SdfQuality}",
                        capturedField.SdfQuality,
                        capturedField.OperationCount + 1,
                        capturedField.CurveThickenCount);
                    fields.Add(new WasperFieldGoo(f));
                }
            }

            if (fields.Count == 0 && analyticalField != null)
                fields.Add(new WasperFieldGoo(analyticalField));

            if (fields.Count == 0 && joinedMesh != null && joinedMesh.Faces.Count > 0)
            {
                WasperField field = WasperField.FromMesh(
                    joinedMesh,
                    0.001,
                    label,
                    sourceTrace,
                    WasperFieldSdfQuality.ApproximateSdf);
                if (field != null) fields.Add(new WasperFieldGoo(field));
            }

            return fields;
        }

        private static Mesh JoinMeshList(List<Mesh> meshes)
        {
            if (meshes == null || meshes.Count == 0) return null;

            var joined = new Mesh();
            foreach (var mesh in meshes)
            {
                if (mesh != null && mesh.Faces.Count > 0)
                    joined.Append(mesh);
            }

            if (joined.Faces.Count == 0) return null;
            joined.Compact();
            return joined;
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
            int fc = mesh.Faces.Count;
            if (fc == 0) return;

            var v2f = new Dictionary<int, List<int>>();
            for (int fi = 0; fi < fc; fi++)
            {
                MeshFace f = mesh.Faces[fi];
                AddFV(v2f, f.A, fi); AddFV(v2f, f.B, fi); AddFV(v2f, f.C, fi);
                if (f.IsQuad) AddFV(v2f, f.D, fi);
            }

            bool[] visited = new bool[fc], keep = new bool[fc];
            var queue = new Queue<int>();
            for (int seed = 0; seed < fc; seed++)
            {
                if (visited[seed]) continue;
                var comp = new List<int>();
                visited[seed] = true; queue.Enqueue(seed);
                while (queue.Count > 0)
                {
                    int fi = queue.Dequeue(); comp.Add(fi);
                    MeshFace f = mesh.Faces[fi];
                    EnqueueN(f.A); EnqueueN(f.B); EnqueueN(f.C);
                    if (f.IsQuad) EnqueueN(f.D);
                    void EnqueueN(int vi)
                    {
                        if (!v2f.TryGetValue(vi, out var fl)) return;
                        foreach (int nf in fl) { if (!visited[nf]) { visited[nf] = true; queue.Enqueue(nf); } }
                    }
                }
                if (comp.Count >= minFaces) foreach (int fi in comp) keep[fi] = true;
                else removedFragments++;
            }
            if (removedFragments == 0) return;

            var clean = new Mesh();
            var vMap  = new Dictionary<int,int>();
            int MapV(int oi) { if (vMap.TryGetValue(oi, out int ni)) return ni; ni = clean.Vertices.Add(mesh.Vertices[oi]); vMap[oi] = ni; return ni; }
            for (int fi = 0; fi < fc; fi++)
            {
                if (!keep[fi]) continue;
                MeshFace f = mesh.Faces[fi];
                int a = MapV(f.A), b = MapV(f.B), c = MapV(f.C);
                if (f.IsQuad) clean.Faces.AddFace(a, b, c, MapV(f.D));
                else          clean.Faces.AddFace(a, b, c);
            }
            mesh.Vertices.Clear(); mesh.Faces.Clear(); mesh.Append(clean);
        }

        private static void AddFV(Dictionary<int,List<int>> d, int vi, int fi)
        { if (!d.TryGetValue(vi, out var lst)) d[vi] = lst = new List<int>(); lst.Add(fi); }

        // ─────────────────────────────────────────────────────────────────────
        // Utilities
        // ─────────────────────────────────────────────────────────────────────

        private bool IsInputUnwired(int index)
        {
            try { return Params != null && Params.Input != null && index >= 0 && index < Params.Input.Count && Params.Input[index].SourceCount == 0; }
            catch { return false; }
        }

        private static int GridCount(double length, double resolution)
        {
            if (resolution <= EPS) resolution = 1.0;
            return Clamp((int)Math.Ceiling(Math.Max(length, resolution) / resolution) + 1, 2, 1200);
        }

        private static double AvoidIntegerResolution(double resolution)
        {
            double nearest = Math.Round(resolution);
            if (nearest >= 1.0 && Math.Abs(resolution - nearest) <= 1e-9)
                return Math.Max(EPS, resolution - 0.01);
            return resolution;
        }

        private static double Lerp(double a, double b, double t) => a + Clamp01(t) * (b - a);
        private static int    Idx(int ix, int iy, int iz, int nx, int ny) => ix + nx * (iy + ny * iz);
        private static int    Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
        private static double Clamp01(double v) => v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;



        private static FaceKey BuildFaceKey(double cx, double cy, double cz, double[] lv, int[] faceIdx)
        {
            var keys = new List<string>();

            for (int i = 0; i < faceIdx.Length; i++)
            {
                int vi = faceIdx[i];

                double x = cx + lv[vi * 3];
                double y = cy + lv[vi * 3 + 1];
                double z = cz + lv[vi * 3 + 2];

                long qx = (long)Math.Round(x * 1000000.0);
                long qy = (long)Math.Round(y * 1000000.0);
                long qz = (long)Math.Round(z * 1000000.0);

                keys.Add($"{qx},{qy},{qz}");
            }

            keys.Sort();

            return new FaceKey(string.Join("|", keys));
        }

        // ─────────────────────────────────────────────────────────────────────
        // TrimVolume
        // ─────────────────────────────────────────────────────────────────────

        private sealed class TrimVolume
        {
            private readonly bool _hasBox;
            private readonly Box  _box;
            private readonly Brep _brep;
            private readonly Mesh _mesh;

            public TrimVolume(Brep brep, Mesh mesh) { _hasBox = false; _box = Box.Unset; _brep = brep; _mesh = mesh; }
            public TrimVolume(Box box) { _hasBox = true; _box = box; _brep = null; _mesh = null; }

            public double SignedDistance(Point3d point)
            {
                if (_hasBox)       return SignedDistanceToBox(point, _box);
                if (_brep != null) return SignedDistanceToBrep(point, _brep);
                if (_mesh != null) return SignedDistanceToMesh(point, _mesh);
                return TRIM;
            }

            private static double SignedDistanceToBox(Point3d point, Box box)
            {
                if (!box.IsValid) return TRIM;
                Vector3d v = point - box.Plane.Origin;
                double x = v * box.Plane.XAxis, y = v * box.Plane.YAxis, z = v * box.Plane.ZAxis;
                double minX = Math.Min(box.X.T0, box.X.T1), maxX = Math.Max(box.X.T0, box.X.T1);
                double minY = Math.Min(box.Y.T0, box.Y.T1), maxY = Math.Max(box.Y.T0, box.Y.T1);
                double minZ = Math.Min(box.Z.T0, box.Z.T1), maxZ = Math.Max(box.Z.T0, box.Z.T1);
                double cx = 0.5*(minX+maxX), cy = 0.5*(minY+maxY), cz2 = 0.5*(minZ+maxZ);
                double hx = 0.5*(maxX-minX), hy = 0.5*(maxY-minY), hz = 0.5*(maxZ-minZ);
                double qx = Math.Abs(x-cx)-hx, qy = Math.Abs(y-cy)-hy, qz = Math.Abs(z-cz2)-hz;
                double ox = Math.Max(qx,0), oy = Math.Max(qy,0), oz = Math.Max(qz,0);
                return Math.Sqrt(ox*ox+oy*oy+oz*oz) + Math.Min(Math.Max(qx,Math.Max(qy,qz)),0.0);
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
