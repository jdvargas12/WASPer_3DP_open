#region Component Description
/*
    Component Name:
        wsp_In08_TPMS Box Array SDF_v2

    Nickname:
        TPMS_Box_SDF_v2

    Version:
        v1.0.5 - 260429

    Category / Subcategory:
        WASPer_3DP / 2_Infills

    Description:
        Generates a volumetric TPMS field and optional lattice mesh inside a
        Rhino Box. The TPMS definition comes from wsp_In15_TPMS Infill Params;
        this component retains only volumetric construction, boundary-shell,
        clipping, disjoining, resolution, and mesh-generation controls.

    Message:
        Shows assembly version and selected TPMS type.
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
#endregion

namespace WASPer_3DP.Components._3_1_Infills
{
    public sealed class wsp_In08_TPMS_Box_Array_SDF_v2 : GH_Component
    {
        private const string NAME = "wsp_In08_TPMS Box Array SDF_v2";
        private const string NICK = "TPMS_Box_SDF_v2";
        private const string CAT = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT = "3.1_Infills";

        private const double TRIM = 1e9;
        private const double TRIM_CAP = 1e6;
        private const double EPS = 1e-10;
        private const double TWO_PI = 2.0 * Math.PI;

        private readonly string _versionTag;

        public wsp_In08_TPMS_Box_Array_SDF_v2()
            : base(
                NAME,
                NICK,
                "Generates a volumetric TPMS field and optional mesh inside a Rhino Box.\n" +
                "Connect wsp_In15_TPMS Infill Params to define TPMS type, level, counts, phases, closing, and inversion. tpms_p is flattened; because the box is one domain, its first value is used.\n" +
                "This v2 component controls volumetric thickness, boundary shell/caps, optional trimming, disjoining, resolution, and mesh generation.",
                CAT,
                SUBCAT)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("5E27363A-2EFE-42AB-AF86-F3D868A3DB62");

        public override GH_Exposure Exposure => GH_Exposure.hidden;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_In08_TPMS Box Array SDF_v2.png"))
                        return s != null ? new Bitmap(s) : null;
                }
                catch { }
                return null;
            }
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddBoxParameter(
                "box", "box",
                "Rhino Box defining the volumetric TPMS domain and its local X, Y, and Z directions.",
                GH_ParamAccess.item);

            pManager.AddGenericParameter(
                "trim_geo", "trim",
                "Optional Box or closed Brep/Mesh/Extrusion used as an additional SDF clipping volume.\n" +
                "The generated TPMS field is intersected with this volume before meshing.",
                GH_ParamAccess.item);

            pManager.AddGenericParameter(
                "tpms_params", "tpms_p",
                "Required typed TPMS definition from wsp_In15_TPMS Infill Params.\n" +
                "The input is flattened. Because one box is one domain, the first parameter is used; additional values are ignored.",
                GH_ParamAccess.list);
            pManager[2].DataMapping = GH_DataMapping.Flatten;

            pManager.AddNumberParameter(
                "thickness", "inf_t",
                "TPMS infill thickness in Rhino model units. 0 generates the single TPMS mid-surface.",
                GH_ParamAccess.item, 0.0);

            pManager.AddNumberParameter(
                "shell_thickness", "shell_t",
                "Inward-only boundary shell thickness in model units. 0 = no shell.\n" +
                "If shell_t > 0 and inf_t is unwired, inf_t uses the same value.",
                GH_ParamAccess.item, 0.0);

            pManager.AddBooleanParameter(
                "shell_caps", "caps",
                "True removes the local top and bottom faces from the boundary shell, leaving only its lateral wrap.",
                GH_ParamAccess.item, true);

            pManager.AddBooleanParameter(
                "disjoin_mesh", "disjoin",
                "When true, disconnected mesh islands are output separately and the field output contains one SDF per island.\n" +
                "Disjoint fields require mesh generation; if mesh? is false, the component returns one analytical field.",
                GH_ParamAccess.item, false);

            pManager.AddNumberParameter(
                "resolution", "res",
                "Voxel size in Rhino model units. Smaller = denser mesh, more memory.",
                GH_ParamAccess.item, 2.0);

            pManager.AddBooleanParameter(
                "out_mesh", "mesh?",
                "True (default): run Marching Cubes and output the mesh.\n" +
                "False: skip MC entirely — only the analytical field is computed.\n" +
                "Use False for fast parameter sweeps (porosity, volume) without mesh cost.",
                GH_ParamAccess.item, true);

            pManager[1].Optional = true;
            pManager[3].Optional = true;
            pManager[4].Optional = true;
            pManager[5].Optional = true;
            for (int i = 6; i < pManager.ParamCount; i++)
                pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddMeshParameter(
                "mesh_out", "mesh",
                "Extracted and cleaned TPMS lattice mesh. If disjoin_mesh is true, disconnected islands are output as separate mesh items.",
                GH_ParamAccess.list);
            pManager.AddGenericParameter(
                "field", "field",
                "Signed distance field (negative inside, positive outside). " +
                "If disjoin_mesh is true, contains one mesh-derived SDF per disconnected island.",
                GH_ParamAccess.list);

            pManager.AddBrepParameter(
                "bound_geo", "bound",
                "Brep representation of the input box domain.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "cell_name", "cell",
                "Selected TPMS type name.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "array", "array",
                "Array count formatted as X.Y.Z.",
                GH_ParamAccess.item);

            pManager.AddTextParameter(
                "info", "info",
                "Generation diagnostics and timing.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            double thickness = 0.0;
            double shellThickness = 0.0;
            bool shellCaps = true;
            bool disjoinMesh = false;
            Box box = Box.Unset;
            object trimGeoRaw = null;
            var tpmsParamsRaw = new List<IGH_Goo>();
            double res = 2.0;
            bool outMesh   = true;
            bool thicknessUnwired = IsInputUnwired(3);

            if (!DA.GetData(0, ref box) || !box.IsValid)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Box input is required and must be valid.");
                DA.SetData(5, "ERR: invalid box.");
                Message = $"{_versionTag} | ERR";
                return;
            }
            DA.GetData(1, ref trimGeoRaw);
            if (!DA.GetDataList(2, tpmsParamsRaw) || tpmsParamsRaw.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "tpms_params is required. Connect the output of wsp_In15_TPMS Infill Params.");
                DA.SetData(5, "ERR: missing tpms_params.");
                Message = $"{_versionTag} | ERR";
                return;
            }

            var tpmsParams =
                global::WASPer_3DP.WasperInfillParamsTools.Unwrap(tpmsParamsRaw[0])
                as global::WASPer_3DP.WasperTpmsInfillParams;
            if (tpmsParams == null)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "tpms_params must be a WASPer TPMS parameter object from wsp_In15_TPMS Infill Params.");
                DA.SetData(5, "ERR: invalid tpms_params type.");
                Message = $"{_versionTag} | ERR";
                return;
            }
            if (tpmsParamsRaw.Count > 1)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"The box is one domain. Using tpms_p[0] and ignoring {tpmsParamsRaw.Count - 1} additional flattened value(s).");

            string paramsError = tpmsParams.Validate();
            if (paramsError != null)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, paramsError);
                DA.SetData(5, "ERR: " + paramsError);
                Message = $"{_versionTag} | ERR";
                return;
            }

            int type = tpmsParams.Type;
            double level = tpmsParams.Level;
            double countX = tpmsParams.CountX;
            double countY = tpmsParams.CountY;
            double countZ = tpmsParams.CountZ;
            double phaseX = tpmsParams.PhaseX;
            double phaseY = tpmsParams.PhaseY;
            double phaseZ = tpmsParams.PhaseZ;
            bool closeTpms = tpmsParams.CloseTpms;
            bool invertField = tpmsParams.InvertTpms;

            DA.GetData(3, ref thickness);
            DA.GetData(4, ref shellThickness);
            DA.GetData(5, ref shellCaps);
            DA.GetData(6, ref disjoinMesh);
            DA.GetData(7, ref res);
            DA.GetData(8, ref outMesh);

            thickness = Math.Max(0.0, thickness);
            shellThickness = Math.Max(0.0, shellThickness);
            if (shellThickness > EPS && thicknessUnwired)
                thickness = shellThickness;

            if (res <= 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "resolution must be > 0.");
                DA.SetData(5, "ERR: resolution must be > 0.");
                Message = $"{_versionTag} | ERR";
                return;
            }

            double sizeX = Math.Abs(box.X.Length);
            double sizeY = Math.Abs(box.Y.Length);
            double sizeZ = Math.Abs(box.Z.Length);
            if (sizeX <= EPS || sizeY <= EPS || sizeZ <= EPS)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Box intervals must have non-zero X, Y, and Z lengths.");
                DA.SetData(5, "ERR: degenerate box.");
                Message = $"{_versionTag} | ERR";
                return;
            }

            var sw = Stopwatch.StartNew();
            TrimVolume trimVolume = BuildTrimVolume(trimGeoRaw);
            if (trimGeoRaw != null && trimVolume == null)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "trim_geo was ignored. Provide a Box, closed Brep, closed Mesh, or Extrusion.");

            bool hasShell = shellThickness > EPS;
            bool useBoundarySdf = closeTpms || thickness > EPS || hasShell;
            double gradScale = global::WASPer_3DP.WasperTpmsPatternMath.ApproxGradientScale(
                countX, countY, countZ, sizeX, sizeY, sizeZ);

            // ── Analytical WasperField closure (always built, no mesh needed) ───────
            // Uses EvalField (per-point gradient via the shared TPMS helper) rather than
            // EvalParametricField (scalar gradScale approximation) so that the TPMS shell
            // thickness is applied accurately. With the scalar approximation, highly
            // non-cubic boxes produce incorrect material fractions — e.g. a Gyroid in a
            // 3:1:3 aspect-ratio box at thickness=6 was classifying ~50% of space as
            // material instead of the correct ~15%, collapsing two separate void networks
            // into one. The per-point gradient resolves this correctly.
            Box        _cBox     = box;           int    _cType    = type;
            double     _cLevel   = level;         double _cThick   = thickness;
            bool       _cInvert  = invertField;   double _cCX      = countX;
            double     _cCY      = countY;        double _cCZ      = countZ;
            double     _cPX      = phaseX;        double _cPY      = phaseY;
            double     _cPZ      = phaseZ;
            bool       _cUBS     = useBoundarySdf;double _cShellT  = shellThickness;
            bool       _cShellC  = shellCaps;     TrimVolume _cTrim = trimVolume;
            double     _cSX      = sizeX;         double _cSY      = sizeY;
            double     _cSZ      = sizeZ;         bool   _cHasShell = hasShell;

            string sourceTrace =
                "Source: In08 TPMS Box Array SDF_v2\n" +
                "TPMS parameters: wsp_In15_TPMS Infill Params\n" +
                $"type={WASPer_3DP.WasperTpmsPatternMath.Name(type)} ({type})\n" +
                $"level={level:G6}\n" +
                $"thickness={thickness:G6}\n" +
                $"shell_thickness={shellThickness:G6}\n" +
                $"shell_caps={shellCaps}\n" +
                $"invert={invertField}\n" +
                $"close_tpms={closeTpms}\n" +
                $"counts={countX}x{countY}x{countZ}\n" +
                $"phase={phaseX:G6},{phaseY:G6},{phaseZ:G6}\n" +
                "pattern_math=shared_static_tpms\n" +
                $"box_size={sizeX:G6},{sizeY:G6},{sizeZ:G6}\n" +
                $"trim_geo={trimVolume != null}\n" +
                "quality=ApproximateSdf";

            WasperField analyticalField = new WasperField(
                p =>
                {
                    // EvalField handles domain check (returns TRIM if outside) and
                    // computes the shared per-point TPMS gradient for accurate shell thickness.
                    // Defer inversion to after the shell CSG when a shell is present.
                    bool applyInvertNow = _cInvert && !_cHasShell;
                    double f = EvalField(p, _cBox, _cType, _cLevel, _cThick, applyInvertNow,
                        _cCX, _cCY, _cCZ, _cPX, _cPY, _cPZ);

                    if (f >= TRIM * 0.1) return TRIM;

                    if (_cUBS || _cHasShell)
                    {
                        Point3d local = WorldToBox(p, _cBox.Plane);
                        double u  = Normalize(local.X, _cBox.X);
                        double v2 = Normalize(local.Y, _cBox.Y);
                        double w  = Normalize(local.Z, _cBox.Z);
                        double bSdf = BoxBoundarySdf(u, v2, w, _cSX, _cSY, _cSZ);

                        if (_cHasShell)
                        {
                            double sBdrySdf = _cShellC
                                ? BoxLateralBoundarySdf(u, v2, _cSX, _cSY)
                                : bSdf;
                            double shellVal = InwardShellSdf(sBdrySdf, _cShellT);
                            shellVal = Math.Max(shellVal, bSdf);
                            f = Math.Min(f, shellVal);
                            if (_cInvert)
                            {
                                f = -f;
                                f = Math.Max(f, bSdf);
                            }
                        }
                        else
                        {
                            f = Math.Max(f, bSdf);
                        }
                    }

                    if (_cTrim != null)
                        f = Math.Max(f, _cTrim.SignedDistance(p));

                    return f;
                },
                box.BoundingBox,
                global::WASPer_3DP.WasperTpmsPatternMath.Name(type),
                sourceTrace,
                WasperFieldSdfQuality.ApproximateSdf);

            // ── Marching Cubes (skipped when out_mesh = false) ────────────────────
            Mesh   result           = null;
            var    resultMeshes     = new List<Mesh>();
            long   totalSamples     = 0;
            int    nx = 0, ny = 0, nz = 0;
            int    removedFragments = 0;
            long   evalMs = 0, meshMs = 0, cleanMs = 0;

            if (outMesh)
            {
                nx = GridCount(sizeX, res);
                ny = GridCount(sizeY, res);
                nz = GridCount(sizeZ, res);
                totalSamples = (long)nx * ny * nz;
                if (totalSamples > 20_000_000)
                {
                    string msg = $"Grid {nx}x{ny}x{nz} = {totalSamples:N0} samples is too large. Increase resolution.";
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, msg);
                    DA.SetData(5, "ERR: " + msg);
                    Message = $"{_versionTag} | ERR";
                    return;
                }

                int parallelThreads = Math.Max(1, Environment.ProcessorCount - 1);
                var evalWatch  = Stopwatch.StartNew();
                var scalars    = new double[nx * ny * nz];
                var points     = new Point3d[nx * ny * nz];

                Parallel.For(
                    0,
                    nz,
                    new ParallelOptions { MaxDegreeOfParallelism = parallelThreads },
                    iz =>
                    {
                        double ww = nz <= 1 ? 0.0 : (double)iz / (double)(nz - 1);
                        for (int iy = 0; iy < ny; iy++)
                        {
                            double vv = ny <= 1 ? 0.0 : (double)iy / (double)(ny - 1);
                            for (int ix = 0; ix < nx; ix++)
                            {
                                double uu = nx <= 1 ? 0.0 : (double)ix / (double)(nx - 1);
                                int idx = Idx(ix, iy, iz, nx, ny);
                                points[idx] = BoxPointAtNormalized(box, uu, vv, ww);
                                scalars[idx] = EvalParametricField(
                                    points[idx], uu, vv, ww, type, level, thickness, invertField,
                                    countX, countY, countZ,
                                    phaseX, phaseY, phaseZ,
                                    gradScale, useBoundarySdf,
                                    shellThickness, shellCaps,
                                    trimVolume,
                                    sizeX, sizeY, sizeZ);
                            }
                        }
                    });
                evalMs = evalWatch.ElapsedMilliseconds;

                var meshWatch = Stopwatch.StartNew();
                result = WasperMarchingCubes.Extract(
                    scalars,
                    points,
                    nx,
                    ny,
                    nz,
                    0.0,
                    Math.Max(res * 1e-6, 1e-9),
                    parallelThreads);
                meshMs = meshWatch.ElapsedMilliseconds;

                var cleanWatch = Stopwatch.StartNew();
                if (result != null && result.Faces.Count > 0)
                {
                    if (disjoinMesh)
                    {
                        resultMeshes = CleanAndSplitResultMeshes(
                            result,
                            180.0,
                            8,
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
                            8,
                            analyticalField,
                            Math.Max(res * 0.5, 1e-6),
                            out removedFragments);
                        resultMeshes = MeshListFromJoined(result);
                    }
                }
                cleanMs = cleanWatch.ElapsedMilliseconds;
            }

            if (!outMesh && disjoinMesh)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "mesh not generated because out_mesh=false. disjoin_mesh falls back to one analytical field.");
            }

            Brep bound = box.ToBrep();
            sw.Stop();

            if (outMesh && (result == null || result.Faces.Count == 0))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "No lattice mesh produced. Check level, resolution, box size, and counts.");

            string cell  = global::WASPer_3DP.WasperTpmsPatternMath.Name(type);
            string array = $"{countX:0.###}.{countY:0.###}.{countZ:0.###}";
            string timingLine = outMesh
                ? $"eval {evalMs} ms | extract {meshMs} ms | clean {cleanMs} ms | total {sw.ElapsedMilliseconds} ms"
                : $"field-only (mesh skipped) | total {sw.ElapsedMilliseconds} ms";
            string info =
                $"{NAME}  {_versionTag}\n" +
                $"type            : {type} ({cell})\n" +
                $"level           : {level:0.###}\n" +
                $"thickness       : {thickness:0.###}\n" +
                $"shell_thickness : {shellThickness:0.###}\n" +
                $"shell_caps      : {shellCaps}\n" +
                $"invert_field    : {invertField}\n" +
                $"disjoin_mesh    : {disjoinMesh}\n" +
                $"array           : {array}\n" +
                $"phase_x/y/z     : {phaseX:0.###} / {phaseY:0.###} / {phaseZ:0.###}\n" +
                "pattern_math    : shared static TPMS evaluator\n" +
                $"resolution      : {res:0.###} model units\n" +
                $"close_tpms      : {closeTpms}\n" +
                $"out_mesh        : {outMesh}\n" +
                $"trim_geo        : {(trimVolume != null)}\n" +
                $"boundary_sdf    : {useBoundarySdf}\n" +
                (outMesh ? $"grid            : {nx}x{ny}x{nz} = {totalSamples:N0} samples\n" : "") +
                (outMesh ? $"mesh vertices   : {(result == null ? 0 : result.Vertices.Count):N0}\n" : "") +
                (outMesh ? $"mesh faces      : {(result == null ? 0 : result.Faces.Count):N0}\n" : "") +
                (outMesh ? $"disjoint meshes : {resultMeshes.Count:N0}\n" : "") +
                (outMesh ? $"removed frags   : {removedFragments}\n" : "") +
                $"timing          : {timingLine}";

            var fieldOutputs = BuildFieldOutputs(
                disjoinMesh,
                outMesh,
                resultMeshes,
                analyticalField,
                cell);

            DA.SetDataList(0, resultMeshes);
            DA.SetDataList(1, fieldOutputs);
            DA.SetData(2, bound);
            DA.SetData(3, cell);
            DA.SetData(4, array);
            DA.SetData(5, info);

            Message = !outMesh
                ? $"{_versionTag} | field only"
                : (result == null || result.Faces.Count == 0
                    ? $"{_versionTag} | empty"
                    : $"{_versionTag} | {cell}");
        }

        private bool IsInputUnwired(int index)
        {
            try
            {
                return Params != null &&
                       Params.Input != null &&
                       index >= 0 &&
                       index < Params.Input.Count &&
                       Params.Input[index].SourceCount == 0;
            }
            catch
            {
                return false;
            }
        }

        private static TrimVolume BuildTrimVolume(object geometry)
        {
            if (geometry == null) return null;

            if (geometry is Grasshopper.Kernel.Types.IGH_Goo goo)
            {
                object scriptValue = goo.ScriptVariable();
                if (scriptValue != null && !ReferenceEquals(scriptValue, geometry))
                    return BuildTrimVolume(scriptValue);
            }

            if (geometry is Box box)
            {
                if (!box.IsValid) return null;
                return new TrimVolume(box);
            }

            if (geometry is GeometryBase geometryBase)
                return BuildTrimVolumeFromGeometry(geometryBase);

            return null;
        }

        private static TrimVolume BuildTrimVolumeFromGeometry(GeometryBase geometry)
        {
            if (geometry == null) return null;

            if (geometry is Brep brep)
            {
                var b = brep.DuplicateBrep();
                if (b != null && b.IsValid && b.IsSolid)
                    return new TrimVolume(b, null);
                return null;
            }

            if (geometry is Extrusion extrusion)
            {
                Brep b = extrusion.ToBrep();
                if (b != null && b.IsValid && b.IsSolid)
                    return new TrimVolume(b, null);
                return null;
            }

            if (geometry is Mesh mesh)
            {
                var m = mesh.DuplicateMesh();
                if (m != null && m.IsValid && m.IsClosed)
                    return new TrimVolume(null, m);
                return null;
            }

            return null;
        }

        private static double EvalField(
            Point3d P,
            Box box,
            int type,
            double level,
            double thickness,
            bool invertField,
            double countX,
            double countY,
            double countZ,
            double phaseX,
            double phaseY,
            double phaseZ)
        {
            Point3d local = WorldToBox(P, box.Plane);
            if (!Contains(box.X, local.X) ||
                !Contains(box.Y, local.Y) ||
                !Contains(box.Z, local.Z))
                return TRIM;

            double u = Normalize(local.X, box.X);
            double v = Normalize(local.Y, box.Y);
            double w = Normalize(local.Z, box.Z);

            double x = TWO_PI * (countX * u + phaseX);
            double y = TWO_PI * (countY * v + phaseY);
            double z = TWO_PI * (countZ * w + phaseZ);

            double field = global::WASPer_3DP.WasperTpmsPatternMath.Value(type, x, y, z) - level;

            // Skip expensive gradient evaluation when thickness is not used.
            if (thickness <= EPS)
                return invertField ? -field : field;

            double gradMag = global::WASPer_3DP.WasperTpmsPatternMath.GradientMagnitude(
                type, x, y, z,
                TWO_PI * countX / Math.Abs(box.X.Length),
                TWO_PI * countY / Math.Abs(box.Y.Length),
                TWO_PI * countZ / Math.Abs(box.Z.Length));
            return ApplyThicknessAndInvert(field, gradMag, thickness, invertField);
        }

        private static double EvalParametricField(
            Point3d point,
            double u,
            double v,
            double w,
            int type,
            double level,
            double thickness,
            bool invertField,
            double countX,
            double countY,
            double countZ,
            double phaseX,
            double phaseY,
            double phaseZ,
            double gradScale,
            bool useBoundarySdf,
            double shellThickness,
            bool shellCaps,
            TrimVolume trimVolume,
            double sizeX,
            double sizeY,
            double sizeZ)
        {
            double f = global::WASPer_3DP.WasperTpmsPatternMath.EvaluateRawNormalized(
                type,
                level,
                countX,
                countY,
                countZ,
                phaseX,
                phaseY,
                phaseZ,
                u,
                v,
                w);
            double value = thickness > EPS
                ? Math.Abs(f / Math.Max(gradScale, EPS)) - thickness * 0.5
                : f;

            bool hasShell = shellThickness > EPS;
            if (invertField && !hasShell) value = -value;

            double boundarySdf = BoxBoundarySdf(u, v, w, sizeX, sizeY, sizeZ);
            if (useBoundarySdf)
                value = Math.Max(value, boundarySdf);

            double finalValue = value;
            if (hasShell)
            {
                double shellBoundarySdf = shellCaps
                    ? BoxLateralBoundarySdf(u, v, sizeX, sizeY)
                    : boundarySdf;

                double shellValue = InwardShellSdf(shellBoundarySdf, shellThickness);
                shellValue = Math.Max(shellValue, boundarySdf);
                finalValue = Math.Min(finalValue, shellValue);

                if (invertField)
                {
                    finalValue = -finalValue;
                    finalValue = Math.Max(finalValue, boundarySdf);
                }
            }

            if (trimVolume != null)
                finalValue = Math.Max(finalValue, trimVolume.SignedDistance(point));

            return finalValue;
        }

        private static double BoxBoundarySdf(
            double u, double v, double w,
            double sizeX, double sizeY, double sizeZ)
        {
            double dx = Math.Min(Clamp01(u), 1.0 - Clamp01(u)) * Math.Max(sizeX, EPS);
            double dy = Math.Min(Clamp01(v), 1.0 - Clamp01(v)) * Math.Max(sizeY, EPS);
            double dz = Math.Min(Clamp01(w), 1.0 - Clamp01(w)) * Math.Max(sizeZ, EPS);
            return -Math.Min(dx, Math.Min(dy, dz));
        }

        private static double BoxLateralBoundarySdf(
            double u, double v,
            double sizeX, double sizeY)
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

        private static int GridCount(double length, double resolution)
        {
            if (resolution <= EPS) resolution = 1.0;
            return Clamp((int)Math.Ceiling(Math.Max(length, resolution) / resolution) + 1, 2, 1200);
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

        private static double Lerp(double a, double b, double t)
            => a + Clamp01(t) * (b - a);

        private static double ApplyThicknessAndInvert(
            double field, double gradMag, double thickness, bool invertField)
        {
            if (gradMag < EPS) gradMag = EPS;
            double value = thickness > EPS
                ? Math.Abs(field / gradMag) - thickness * 0.5
                : field;
            return invertField ? -value : value;
        }

        private static Point3d WorldToBox(Point3d p, Plane plane)
        {
            Vector3d d = p - plane.Origin;
            return new Point3d(d * plane.XAxis, d * plane.YAxis, d * plane.ZAxis);
        }

        private static bool Contains(Interval interval, double value)
        {
            double t0 = Math.Min(interval.T0, interval.T1);
            double t1 = Math.Max(interval.T0, interval.T1);
            return value >= t0 - EPS && value <= t1 + EPS;
        }

        private static double Normalize(double value, Interval interval)
        {
            double t0 = interval.T0;
            double len = interval.T1 - interval.T0;
            return Math.Abs(len) > EPS ? (value - t0) / len : 0.5;
        }

        private static Mesh MarchingCubes(
            double[] scalars,
            Point3d[] points,
            int nx,
            int ny,
            int nz,
            double step)
        {
            var mesh = new Mesh();
            var vertexMap = new Dictionary<VertexKey, int>();
            double keyTol = Math.Max(step * 1e-6, 1e-9);

            int[,] cubeCorners =
            {
                {0,0,0},{1,0,0},{1,1,0},{0,1,0},
                {0,0,1},{1,0,1},{1,1,1},{0,1,1}
            };
            int[,] edgeCorners =
            {
                {0,1},{1,2},{2,3},{3,0},
                {4,5},{5,6},{6,7},{7,4},
                {0,4},{1,5},{2,6},{3,7}
            };

            for (int iz = 0; iz < nz - 1; iz++)
            for (int iy = 0; iy < ny - 1; iy++)
            for (int ix = 0; ix < nx - 1; ix++)
            {
                double[] sv = new double[8];
                Point3d[] cp = new Point3d[8];

                for (int c = 0; c < 8; c++)
                {
                    int cx = ix + cubeCorners[c, 0];
                    int cy = iy + cubeCorners[c, 1];
                    int cz = iz + cubeCorners[c, 2];
                    int idx = Idx(cx, cy, cz, nx, ny);
                    sv[c] = scalars[idx];
                    cp[c] = points[idx];
                }

                var crossings = new List<Point3d>(12);
                for (int e = 0; e < 12; e++)
                {
                    int a = edgeCorners[e, 0];
                    int b = edgeCorners[e, 1];
                    if ((sv[a] < 0.0) == (sv[b] < 0.0)) continue;

                    double d = sv[a] - sv[b];
                    double t = Math.Abs(d) < 1e-14 ? 0.5 : sv[a] / d;
                    crossings.Add(cp[a] + Clamp01(t) * (cp[b] - cp[a]));
                }

                Vector3d gradient = EstimateWorldGradientFromCube(sv, cp);
                AddCubePolygon(mesh, vertexMap, crossings, gradient, keyTol);
            }

            return mesh.Faces.Count == 0 ? null : mesh;
        }

        private static void AddCubePolygon(
            Mesh mesh,
            Dictionary<VertexKey, int> vertexMap,
            List<Point3d> crossings,
            Vector3d gradient,
            double keyTol)
        {
            if (crossings == null || crossings.Count < 3) return;

            Point3d center = Point3d.Origin;
            for (int i = 0; i < crossings.Count; i++)
                center += (Vector3d)crossings[i];
            center /= crossings.Count;

            Vector3d normal = gradient;
            if (!normal.Unitize())
            {
                normal = Vector3d.CrossProduct(crossings[1] - crossings[0], crossings[2] - crossings[0]);
                if (!normal.Unitize()) return;
            }

            Vector3d axisX = crossings[0] - center;
            axisX -= normal * (axisX * normal);
            if (!axisX.Unitize())
            {
                axisX = Vector3d.CrossProduct(normal, Vector3d.XAxis);
                if (!axisX.Unitize())
                    axisX = Vector3d.CrossProduct(normal, Vector3d.YAxis);
                if (!axisX.Unitize()) return;
            }

            Vector3d axisY = Vector3d.CrossProduct(normal, axisX);
            if (!axisY.Unitize()) return;

            var ordered = crossings
                .Select(p =>
                {
                    Vector3d d = p - center;
                    return new { Point = p, Angle = Math.Atan2(d * axisY, d * axisX) };
                })
                .OrderBy(x => x.Angle)
                .Select(x => x.Point)
                .ToList();

            int v0 = AddVertex(mesh, vertexMap, ordered[0], keyTol);
            for (int i = 1; i < ordered.Count - 1; i++)
            {
                int v1 = AddVertex(mesh, vertexMap, ordered[i], keyTol);
                int v2 = AddVertex(mesh, vertexMap, ordered[i + 1], keyTol);
                AddOrientedFace(mesh, v0, v1, v2, normal);
            }
        }

        private static void AddOrientedFace(
            Mesh mesh, int a, int b, int c, Vector3d targetNormal)
        {
            if (a == b || b == c || c == a) return;

            Point3d pa = mesh.Vertices[a];
            Point3d pb = mesh.Vertices[b];
            Point3d pc = mesh.Vertices[c];
            Vector3d faceNormal = Vector3d.CrossProduct(pb - pa, pc - pa);

            if (faceNormal.IsValid && !faceNormal.IsZero &&
                targetNormal.IsValid && !targetNormal.IsZero &&
                faceNormal * targetNormal < 0.0)
                mesh.Faces.AddFace(a, c, b);
            else
                mesh.Faces.AddFace(a, b, c);
        }

        private static Vector3d EstimateWorldGradientFromCube(double[] s, Point3d[] p)
        {
            Vector3d grad = Vector3d.Zero;
            AccumulateGradient(ref grad, s[1] - s[0], p[1] - p[0]);
            AccumulateGradient(ref grad, s[2] - s[3], p[2] - p[3]);
            AccumulateGradient(ref grad, s[5] - s[4], p[5] - p[4]);
            AccumulateGradient(ref grad, s[6] - s[7], p[6] - p[7]);

            AccumulateGradient(ref grad, s[3] - s[0], p[3] - p[0]);
            AccumulateGradient(ref grad, s[2] - s[1], p[2] - p[1]);
            AccumulateGradient(ref grad, s[7] - s[4], p[7] - p[4]);
            AccumulateGradient(ref grad, s[6] - s[5], p[6] - p[5]);

            AccumulateGradient(ref grad, s[4] - s[0], p[4] - p[0]);
            AccumulateGradient(ref grad, s[5] - s[1], p[5] - p[1]);
            AccumulateGradient(ref grad, s[7] - s[3], p[7] - p[3]);
            AccumulateGradient(ref grad, s[6] - s[2], p[6] - p[2]);
            return grad;
        }

        private static void AccumulateGradient(ref Vector3d grad, double df, Vector3d edge)
        {
            double len2 = edge.SquareLength;
            if (len2 <= EPS) return;
            grad += (df / len2) * edge;
        }

        private static int AddVertex(Mesh mesh, Dictionary<VertexKey, int> map, Point3d p, double tol)
        {
            var key = new VertexKey(p, tol);
            if (map.TryGetValue(key, out int idx)) return idx;
            idx = mesh.Vertices.Add(p);
            map[key] = idx;
            return idx;
        }

        private static List<WasperFieldGoo> BuildFieldOutputs(
            bool disjoinMesh,
            bool outMesh,
            List<Mesh> resultMeshes,
            WasperField analyticalField,
            string label)
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

            return fields;
        }

        private static List<Mesh> MeshListFromJoined(Mesh mesh)
        {
            var result = new List<Mesh>();
            if (mesh != null && mesh.Faces.Count > 0) result.Add(mesh);
            return result;
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

        private static List<Mesh> SplitConnectedComponents(
            Mesh mesh,
            int minFaces,
            out int removedFragments)
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
            Mesh mesh, double weldAngleDeg, int minFragFaces,
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

        private static void OrientFacesByField(
            Mesh mesh, Func<Point3d, double> valueAt, double sampleDistance)
        {
            if (mesh == null || valueAt == null || mesh.Faces.Count == 0) return;

            double h = Math.Max(sampleDistance, 1e-6);

            for (int i = 0; i < mesh.Faces.Count; i++)
            {
                MeshFace f = mesh.Faces[i];
                Point3d a = mesh.Vertices[f.A];
                Point3d b = mesh.Vertices[f.B];
                Point3d c = mesh.Vertices[f.C];
                Point3d d = f.IsQuad ? mesh.Vertices[f.D] : Point3d.Unset;

                Point3d center = f.IsQuad
                    ? new Point3d(
                        (a.X + b.X + c.X + d.X) * 0.25,
                        (a.Y + b.Y + c.Y + d.Y) * 0.25,
                        (a.Z + b.Z + c.Z + d.Z) * 0.25)
                    : new Point3d(
                        (a.X + b.X + c.X) / 3.0,
                        (a.Y + b.Y + c.Y) / 3.0,
                        (a.Z + b.Z + c.Z) / 3.0);

                Vector3d n = Vector3d.CrossProduct(b - a, c - a);
                if (!n.Unitize()) continue;

                double vp = valueAt(center + n * h);
                double vm = valueAt(center - n * h);
                if (vp > TRIM * 0.1 || vm > TRIM * 0.1) continue;

                if (vp - vm < 0.0)
                    FlipFace(mesh, i, f);
            }

            mesh.Normals.ComputeNormals();
            mesh.Compact();
        }

        private static void FlipFace(Mesh mesh, int index, MeshFace face)
        {
            if (face.IsQuad)
                mesh.Faces.SetFace(index, face.A, face.D, face.C, face.B);
            else
                mesh.Faces.SetFace(index, face.A, face.C, face.B);
        }

        private static int CullOutsideBoxInPlace(Mesh mesh, Box box, double tolerance)
        {
            if (mesh == null || mesh.Faces.Count == 0) return 0;

            var clean = new Mesh();
            var map = new Dictionary<int, int>();
            int removed = 0;

            int MapVertex(int oldIndex)
            {
                if (map.TryGetValue(oldIndex, out int idx)) return idx;
                idx = clean.Vertices.Add(mesh.Vertices[oldIndex]);
                map[oldIndex] = idx;
                return idx;
            }

            for (int i = 0; i < mesh.Faces.Count; i++)
            {
                MeshFace f = mesh.Faces[i];
                if (!PointInsideBox(mesh.Vertices[f.A], box, tolerance) ||
                    !PointInsideBox(mesh.Vertices[f.B], box, tolerance) ||
                    !PointInsideBox(mesh.Vertices[f.C], box, tolerance) ||
                    (f.IsQuad && !PointInsideBox(mesh.Vertices[f.D], box, tolerance)))
                {
                    removed++;
                    continue;
                }

                int a = MapVertex(f.A);
                int b = MapVertex(f.B);
                int c = MapVertex(f.C);
                if (f.IsQuad)
                    clean.Faces.AddFace(a, b, c, MapVertex(f.D));
                else
                    clean.Faces.AddFace(a, b, c);
            }

            if (removed == 0) return 0;

            mesh.Vertices.Clear();
            mesh.Faces.Clear();
            mesh.Append(clean);
            mesh.Vertices.CullUnused();
            mesh.Normals.ComputeNormals();
            mesh.Compact();
            return removed;
        }

        private static bool PointInsideBox(Point3d point, Box box, double tolerance)
        {
            Point3d local = WorldToBox(point, box.Plane);
            return Contains(box.X, local.X, tolerance) &&
                   Contains(box.Y, local.Y, tolerance) &&
                   Contains(box.Z, local.Z, tolerance);
        }

        private static bool Contains(Interval interval, double value, double tolerance)
        {
            double t0 = Math.Min(interval.T0, interval.T1) - tolerance;
            double t1 = Math.Max(interval.T0, interval.T1) + tolerance;
            return value >= t0 && value <= t1;
        }

        private static void RemoveSmallFragmentsInPlace(
            Mesh mesh, int minFaces, out int removedFragments)
        {
            removedFragments = 0;
            int faceCount = mesh.Faces.Count;
            if (faceCount == 0) return;

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
                var comp = new List<int>();
                visited[seed] = true;
                queue.Enqueue(seed);

                while (queue.Count > 0)
                {
                    int fi = queue.Dequeue();
                    comp.Add(fi);
                    MeshFace f = mesh.Faces[fi];
                    EnqueueNeighbor(f.A);
                    EnqueueNeighbor(f.B);
                    EnqueueNeighbor(f.C);
                    if (f.IsQuad) EnqueueNeighbor(f.D);

                    void EnqueueNeighbor(int vi)
                    {
                        if (!v2f.TryGetValue(vi, out var fl)) return;
                        foreach (int nf in fl)
                        {
                            if (visited[nf]) continue;
                            visited[nf] = true;
                            queue.Enqueue(nf);
                        }
                    }
                }

                if (comp.Count >= minFaces)
                    foreach (int fi in comp) keep[fi] = true;
                else
                    removedFragments++;
            }

            if (removedFragments == 0) return;

            var clean = new Mesh();
            var vMap = new Dictionary<int, int>();
            int MapV(int oi)
            {
                if (vMap.TryGetValue(oi, out int ni)) return ni;
                ni = clean.Vertices.Add(mesh.Vertices[oi]);
                vMap[oi] = ni;
                return ni;
            }

            for (int fi = 0; fi < faceCount; fi++)
            {
                if (!keep[fi]) continue;
                MeshFace f = mesh.Faces[fi];
                int a = MapV(f.A), b = MapV(f.B), c = MapV(f.C);
                if (f.IsQuad) clean.Faces.AddFace(a, b, c, MapV(f.D));
                else clean.Faces.AddFace(a, b, c);
            }

            mesh.Vertices.Clear();
            mesh.Faces.Clear();
            mesh.Append(clean);
        }

        private static void AddFV(Dictionary<int, List<int>> d, int vi, int fi)
        {
            if (!d.TryGetValue(vi, out var lst)) d[vi] = lst = new List<int>();
            lst.Add(fi);
        }

        private static int Idx(int ix, int iy, int iz, int nx, int ny)
            => ix + nx * (iy + ny * iz);

        private static int Clamp(int v, int lo, int hi)
            => v < lo ? lo : v > hi ? hi : v;

        private static double Clamp(double v, double lo, double hi)
            => v < lo ? lo : v > hi ? hi : v;

        private static double Clamp01(double v)
            => v < 0.0 ? 0.0 : v > 1.0 ? 1.0 : v;


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
                if (_hasBox)
                    return SignedDistanceToBox(point, _box);
                if (_brep != null)
                    return SignedDistanceToBrep(point, _brep);
                if (_mesh != null)
                    return SignedDistanceToMesh(point, _mesh);
                return TRIM;
            }

            private static double SignedDistanceToBox(Point3d point, Box box)
            {
                if (!box.IsValid)
                    return TRIM;

                Vector3d v = point - box.Plane.Origin;
                double x = v * box.Plane.XAxis;
                double y = v * box.Plane.YAxis;
                double z = v * box.Plane.ZAxis;

                double minX = Math.Min(box.X.T0, box.X.T1);
                double maxX = Math.Max(box.X.T0, box.X.T1);
                double minY = Math.Min(box.Y.T0, box.Y.T1);
                double maxY = Math.Max(box.Y.T0, box.Y.T1);
                double minZ = Math.Min(box.Z.T0, box.Z.T1);
                double maxZ = Math.Max(box.Z.T0, box.Z.T1);

                double cx = 0.5 * (minX + maxX);
                double cy = 0.5 * (minY + maxY);
                double cz = 0.5 * (minZ + maxZ);
                double hx = 0.5 * (maxX - minX);
                double hy = 0.5 * (maxY - minY);
                double hz = 0.5 * (maxZ - minZ);

                double qx = Math.Abs(x - cx) - hx;
                double qy = Math.Abs(y - cy) - hy;
                double qz = Math.Abs(z - cz) - hz;

                double ox = Math.Max(qx, 0.0);
                double oy = Math.Max(qy, 0.0);
                double oz = Math.Max(qz, 0.0);
                double outside = Math.Sqrt(ox * ox + oy * oy + oz * oz);
                double inside = Math.Min(Math.Max(qx, Math.Max(qy, qz)), 0.0);
                return outside + inside;
            }

            private static double SignedDistanceToBrep(Point3d point, Brep brep)
            {
                if (brep == null || !brep.IsValid || !brep.IsSolid)
                    return TRIM;

                double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;

                Point3d closest;
                ComponentIndex ci;
                double s;
                double t;
                Vector3d normal;

                bool ok = brep.ClosestPoint(
                    point,
                    out closest,
                    out ci,
                    out s,
                    out t,
                    double.MaxValue,
                    out normal);

                if (!ok || !closest.IsValid)
                    return TRIM;

                double d = point.DistanceTo(closest);
                bool inside = brep.IsPointInside(point, tol, true);
                return inside ? -d : d;
            }

            private static double SignedDistanceToMesh(Point3d point, Mesh mesh)
            {
                if (mesh == null || !mesh.IsValid || !mesh.IsClosed)
                    return TRIM;

                double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
                MeshPoint mp = mesh.ClosestMeshPoint(point, double.MaxValue);
                if (mp == null)
                    return TRIM;

                Point3d closest = mesh.PointAt(mp);
                double d = point.DistanceTo(closest);
                bool inside = mesh.IsPointInside(point, tol, true);
                return inside ? -d : d;
            }
        }
    }
}
