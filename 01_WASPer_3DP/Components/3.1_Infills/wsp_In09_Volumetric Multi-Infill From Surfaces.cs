#region Component Description
/*
    Component Name:
        wsp_In09_Volumetric Multi-Infill (From Surfaces)

    Nickname:
        Vol_Infill_Surfs

    Version:
        v1.0.5 - 260502

    Category / Subcategory:
        WASPer_3DP / 3.1_Infills

    Description:
        Generates a volumetric field and optional mesh directly between
        N surfaces (>= 2). Pattern settings come from typed TPMS or polyhedral
        infill parameters. Consecutive surface pairs define independent slabs; the field
        is evaluated inside each slab using that pair's surface UV as
        in-plane phases and the local between-surface coordinate as the
        through-thickness phase.

        Architecture: curvilinear UVT parametric grid.
        Grid points are computed by direct interpolation between surface pairs
        (P = pA + t*(pB-pA)), so every voxel is inside the domain by construction.
        No lateral or depth domain-membership checks are needed.

        Surfaces pre-processing:
          - UV normalized to [0,1] for consistent phase mapping across surfaces.
          - Normals auto-oriented through the surface stack using the global
            stacking axis derived from surface centroids.
          - UV axis alignment checked across surfaces; a warning is issued
            when misalignment exceeds 45 degrees.
          - UvPairMap aligns UV orientation between consecutive surface pairs
            automatically (checks all swap/flip combinations, picks best fit).

        Accepts Surface or single-face Brep inputs.
        No external dependencies (no Isopod).

    Shell (shell_thickness > 0):
        Adds an inward boundary shell of the given thickness using CSG Min
        (union) with the TPMS infill field. The shell encloses the infill.

        Infill offset (DEACTIVATED – work in progress):
        The intent is to automatically inset the infill domain by shell_thickness
        so infill and shell do not overlap:
          - shell_caps=True  : inset on lateral faces only (no inset at top/bottom).
          - shell_caps=False : inset on all faces including top/bottom.
        Currently disabled; infill and shell are combined without a gap offset.

    invert_tpms (from tpms_params):
        - No shell: negates the TPMS field to yield the complementary solid.
        - With shell: negates the combined (infill + shell) field after the CSG
          union, outputting the void/holes space instead of the solid.
          The slab domain boundary is re-applied after negation so the void mesh
          is properly capped at the top and bottom surfaces.

    Inputs:
        surfaces      : list of >= 2 surfaces / single-face Breps (ordered)
        trim_geo      : optional additional closed clipping volume
        tpms_params   : flattened list from wsp_In15; one broadcasts, multiple cycle by domain
        thickness     : implicit field band thickness; 0 = single surface
        shell_thickness : inward boundary shell thickness; 0 = no shell.
                          If > 0 and 'thickness' is unwired, thickness = shell_thickness.
        partition_thickness : total thickness centered on internal guide surfaces;
                              model-space offsets of part_t/2 extend into each adjacent pair; 0 = none
        shell_caps    : True removes top/bottom shell faces, retaining the lateral wrap
        disjoin_mesh  : flattened list paired/cycled with infill_p; split disconnected
                        mesh islands for the corresponding surface domains
        transpose     : swap in-plane UV axes
        flip          : flip through-thickness direction
        resolution    : voxel size in model units
        out_mesh      : generate mesh when true; return analytical field only when false

    Outputs:
        mesh_out  : one extracted TPMS lattice mesh per consecutive surface pair
                    when part_t=0; one seam-welded continuous mesh when part_t>0
        field     : analytical WASPer field, or mesh-derived fields when disjoined
        bound_geo : one closed boundary Brep per consecutive surface pair
        cell      : TPMS type name (e.g. Gyroid)
        array     : repeat counts formatted as "cu.cv.cn"
        info      : diagnostics and timing

    Recent changes (2026-05-02):
        - Architecture rewritten from hybrid-containment SDF to curvilinear UVT
          parametric grid. Domain containment is structural, not checked per-point.
        - Added shell_thickness and shell_caps inputs.
        - Infill offset (WIP, currently deactivated): intent is to inset the infill
          by shell_thickness so it fits inside the shell without overlap.
        - invert_field with shell now outputs void/holes (negates combined field),
          with domain re-clip to prevent open top/bottom.
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
#endregion

namespace WASPer_3DP.Components._3_1_Infills
{
    public sealed class wsp_In09_Volumetric_Multi_Infill_From_Surfaces : GH_Component
    {
        private const string NAME    = "wsp_In09_Volumetric Multi-Infill (From Surfaces)";
        private const string NICK    = "Vol_Infill_Surfs";
        private const string CAT     = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string SUBCAT  = "3.1_Infills";
        private readonly string _versionTag;

        private const double TRIM     = 1e9;
        private const double TRIM_CAP = 1e6;   // cap for boundary voxels
        private const double EPS      = 1e-10;
        private const double TWO_PI   = 2.0 * Math.PI;
        private const int    BOUNDARY_SAMPLE_COUNT = 128;

        public wsp_In09_Volumetric_Multi_Infill_From_Surfaces()
            : base(
                NAME,
                NICK,
                "Generates a volumetric field and optional mesh between N surfaces (>= 2).\n" +
                "Connect a flattened list of TPMS, Polyhedral, and/or Brick-like Infill Params: one definition broadcasts; multiple definitions assign/cycle by consecutive domain.\n" +
                "Each consecutive surface pair forms an independent curvilinear UVT field (S0→S1, S1→S2, etc.); c_z and p_z restart per pair.\n" +
                "Optional part_t creates a centered solid partition at every internal guide surface. disjoin is flattened and paired/cycled with infill_p so each assigned pattern can control its domain cleanup independently.",
                CAT,
                SUBCAT)
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var v   = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("8AE97C0D-24F2-49E2-A390-4F170441BE36");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = System.Reflection.Assembly.GetExecutingAssembly();
                    using (var s = asm.GetManifestResourceStream(
                        "WASPer_3DP.Resources.Icons.wsp_In09_Volumetric Multi-Infill From Surfaces.png"))
                        return s != null ? new Bitmap(s) : null;
                }
                catch { }
                return null;
            }
        }

        // ── Registration ─────────────────────────────────────────────────

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGeometryParameter(
                "surfaces", "surfs",
                "List of >= 2 surfaces or single-face Breps, ordered from the first to the last boundary.\n" +
                "Consecutive pairs define independent slabs.\n" +
                "Normals are auto-oriented through the stack; UV is normalized to [0,1].",
                GH_ParamAccess.list);

            pManager.AddGenericParameter(
                "trim_geo", "trim",
                "Optional Box or closed Brep/Mesh/Extrusion used as an additional SDF clipping volume.\n" +
                "The final pattern + optional shell field is intersected with this volume before meshing.",
                GH_ParamAccess.item);

            pManager.AddGenericParameter(
                "infill_params", "infill_p",
                "Flattened typed definitions from wsp_In11 TPMS, wsp_In14 Polyhedral, and/or wsp_In15 Brick-like Infill Params.\n" +
                "One object broadcasts to every consecutive surface domain; multiple objects assign/cycle domain-by-domain.\n" +
                "Maps c_x/c_y/c_z to surface U/V/local-pair counts. TPMS parameters also provide level, phases, and closing.\n" +
                "Default axis map is X→U, Y→V, Z→N. trans swaps U/V and flip reverses N.\n" +
                "c_z and p_z restart in every consecutive surface pair: S0→S1, S1→S2, etc.",
                GH_ParamAccess.list);
            pManager[2].DataMapping = GH_DataMapping.Flatten;

            pManager.AddNumberParameter(
                "thickness", "inf_t",
                "Pattern thickness in Rhino model units. For TPMS, 0 generates its implicit level surface. Polyhedral and Brick-like rib networks require a positive thickness.",
                GH_ParamAccess.item, 0.0);

            pManager.AddNumberParameter(
                "shell_thickness", "shell_t",
                "Inward-only boundary shell thickness in model units. 0 = no shell.\n" +
                "If shell_t > 0 and inf_t is unwired, inf_t uses the same value.",
                GH_ParamAccess.item, 0.0);

            pManager.AddNumberParameter(
                "partition_thickness", "part_t",
                "Optional total thickness centered on every internal guide surface.\n" +
                "The partition volume is bounded by model-space offsets of part_t/2 on both sides: one half extends into each adjacent surface-pair domain.\n" +
                "The first and last surfaces are excluded. 0 = no partitions.",
                GH_ParamAccess.item, 0.0);

            pManager.AddBooleanParameter(
                "shell_caps", "caps",
                "True removes the top and bottom cap faces from the generated shell, leaving only the lateral wrap.",
                GH_ParamAccess.item, true);

            pManager.AddBooleanParameter(
                "disjoin_mesh", "disjoin",
                "Flattened list paired with infill_p. One value broadcasts; multiple values pair/cycle by infill_p index, and each surface domain inherits the value of its assigned pattern.\n" +
                "True splits that domain's disconnected mesh islands. Disjoining requires mesh generation; with mesh?=false the analytical multi-domain field is returned.",
                GH_ParamAccess.list);
            pManager[7].DataMapping = GH_DataMapping.Flatten;

            pManager.AddBooleanParameter(
                "transpose", "trans",
                "Swap the two in-plane UV axes.",
                GH_ParamAccess.item, false);

            pManager.AddBooleanParameter(
                "flip", "flip",
                "Flip the through-thickness direction.",
                GH_ParamAccess.item, false);

            pManager.AddNumberParameter(
                "resolution", "res",
                "Voxel size in Rhino model units. Smaller = denser mesh, more memory.",
                GH_ParamAccess.item, 2.0);

            pManager.AddBooleanParameter(
                "out_mesh", "mesh?",
                "When true (default), generates and outputs the volumetric infill mesh.\n" +
                "When false, skips mesh generation for fast field-only evaluation.\n" +
                "The 'field' output is always available regardless of this setting.",
                GH_ParamAccess.item, true);

            // surfaces and infill_params are required; all others are optional
            pManager[1].Optional = true;
            for (int i = 3; i < pManager.ParamCount; i++)
                pManager[i].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            // 0
            pManager.AddMeshParameter(
                "mesh_out", "mesh",
                "Extracted and cleaned volumetric infill meshes. By default, one mesh item is returned per consecutive surface domain: S0→S1, S1→S2, etc.\n" +
                "When part_t > 0, coincident internal caps are removed and the domains are welded into one continuous partitioned mesh.\n" +
                "Domains whose paired disjoin value is true contribute disconnected islands separately; other domains contribute one cleaned mesh. With part_t>0 the domains are physically welded, so any true value splits the joined result globally.",
                GH_ParamAccess.list);
            pManager.AddGenericParameter(
                "field", "field",
                "Signed distance fields (negative inside, positive outside). If any paired disjoin value is true, output fields follow the resulting mesh items and are clipped per mesh/island; otherwise one analytical multi-domain field is returned.",
                GH_ParamAccess.list);

            // 1
            pManager.AddBrepParameter(
                "bound_geo", "bound",
                "Closed Brep boundary domains built from consecutive surface pairs. Returns S0→S1, S1→S2, etc.",
                GH_ParamAccess.list);

            // 2
            pManager.AddTextParameter(
                "cell", "cell",
                "Per-domain volumetric pattern mapping, formatted as domain_index:type.",
                GH_ParamAccess.item);

            // 3
            pManager.AddTextParameter(
                "array", "array",
                "Per-domain repeat-count mapping, formatted as domain_index:cu.cv.cn.",
                GH_ParamAccess.item);

            // 4
            pManager.AddTextParameter(
                "info", "info",
                "Generation diagnostics and timing.",
                GH_ParamAccess.item);

            pManager.AddGenericParameter(
                "infill_kpis", "in_kpis",
                "Global infill KPI set for the WASPer Study Manager. Each consecutive surface " +
                "pair contributes an aligned cell name, short name, X/Y/Z repeat count, and " +
                "local U/V/N domain dimension. Multiple domain values are comma-separated in " +
                "surface-pair order; domain count is included when greater than one.",
                GH_ParamAccess.item);
        }

        // ── Solve ─────────────────────────────────────────────────────────

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            var rawList      = new List<IGH_Goo>();
            object trimGeoRaw = null;
            var infillParamsRaw = new List<IGH_Goo>();
            var infillPatterns = new List<global::WASPer_3DP.WasperVolumetricPatternDescriptor>();
            double thickness = 0.0;
            var disjoinValues = new List<bool>();
            bool transpose   = false;
            bool flip        = false;
            double res       = 2.0;
            double shellThickness = 0.0;
            double partitionThickness = 0.0;
            bool shellCaps = true;
            bool outMesh     = true;
            bool thicknessUnwired = IsInputUnwired(3);

            if (!DA.GetDataList(0, rawList) || rawList.Count < 2)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                    "Provide at least 2 surfaces or single-face Breps.");
                DA.SetData(5, "ERR: Need >= 2 surfaces.");
                Message = "ERR";
                return;
            }

            DA.GetData( 1, ref trimGeoRaw);
            if (!DA.GetDataList(2, infillParamsRaw) || infillParamsRaw.Count == 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "infill_params is required. Connect TPMS, Polyhedral, or Brick-like Infill Params.");
                DA.SetData(5, "ERR: missing infill_params.");
                Message = $"{_versionTag} | ERR";
                return;
            }

            foreach (IGH_Goo rawParam in infillParamsRaw)
            {
                var parsed = global::WASPer_3DP.WasperInfillParamsTools.Unwrap(rawParam);
                if (!global::WASPer_3DP.WasperVolumetricPatternDescriptor.TryCreate(
                    parsed,
                    out global::WASPer_3DP.WasperVolumetricPatternDescriptor pattern,
                    out string validation))
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        validation);
                    DA.SetData(5, "ERR: " + validation);
                    Message = $"{_versionTag} | ERR";
                    return;
                }
                infillPatterns.Add(pattern);
            }

            DA.GetData( 3, ref thickness);
            DA.GetData( 4, ref shellThickness);
            DA.GetData( 5, ref partitionThickness);
            DA.GetData( 6, ref shellCaps);
            DA.GetDataList(7, disjoinValues);
            DA.GetData( 8, ref transpose);
            DA.GetData( 9, ref flip);
            DA.GetData(10, ref res);
            DA.GetData(11, ref outMesh);
            if (disjoinValues.Count == 0)
                disjoinValues.Add(false);
            if (disjoinValues.Count != 1 && disjoinValues.Count != infillPatterns.Count)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{disjoinValues.Count} flattened disjoin values are cycling across {infillPatterns.Count} infill_p values.");

            thickness = Math.Max(0.0, thickness);
            shellThickness = Math.Max(0.0, shellThickness);
            partitionThickness = Math.Max(0.0, partitionThickness);
            if (shellThickness > EPS && thicknessUnwired)
                thickness = shellThickness;
            if (thickness <= EPS
                && infillPatterns.Any(pattern =>
                    pattern.Kind == global::WASPer_3DP.WasperVolumetricPatternKind.Brick))
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Brick-like infill with inf_t = 0 is only a zero-width partition field and normally produces no mesh. Set inf_t > 0 to create volumetric ribs.");
            }

            if (res <= 0.0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "resolution must be > 0.");
                DA.SetData(5, "ERR: resolution must be > 0.");
                Message = "ERR";
                return;
            }

            // ── Parse surfaces ────────────────────────────────────────────
            var probes = new List<SurfProbe>();
            for (int i = 0; i < rawList.Count; i++)
            {
                GeometryBase gb = ExtractGeometry(rawList[i]);
                SurfProbe p = SurfProbe.Wrap(gb);
                if (p == null)
                {
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error,
                        $"Item [{i}] is not a Surface or single-face Brep. Multi-face Breps are not supported.");
                    DA.SetData(5, $"ERR: unsupported geometry at index {i}.");
                    Message = "ERR";
                    return;
                }
                probes.Add(p);
            }

            // ── Pre-processing ────────────────────────────────────────────

            // 1. Global stacking axis from centroid chain
            var centroids = probes.Select(p => p.GetCentroid()).ToArray();
            var stackAxis = Vector3d.Zero;
            for (int i = 0; i < centroids.Length - 1; i++)
                stackAxis += centroids[i + 1] - centroids[i];
            if (!stackAxis.Unitize()) stackAxis = Vector3d.ZAxis;

            // 2. Auto-orient normals: flag surfaces whose average normal opposes the stack
            for (int i = 0; i < probes.Count; i++)
            {
                Vector3d avgN = probes[i].GetAverageNormal();
                if (avgN * stackAxis < 0.0)
                    probes[i].FlipNormal = true;
            }

            // 3. UV axis alignment check — warn if any surface is > 45° off surface[0]
            Vector3d refU = probes[0].GetUAxis();
            for (int i = 1; i < probes.Count; i++)
            {
                double dot = refU * probes[i].GetUAxis();
                if (dot < 0.707)
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                        $"Surface [{i}] U-axis is misaligned with surface [0] " +
                        $"(alignment={dot:0.00}). The volumetric pattern may be discontinuous " +
                        "at this slab boundary. Use 'transpose' to correct, or " +
                        "re-orient the surface in Rhino.");
            }

            // ── Bounding box ──────────────────────────────────────────────
            var sw = Stopwatch.StartNew();

            var probeArr = probes.ToArray();
            var domains  = BuildSlabDomains(probeArr);
            var capMaps  = BuildCapDomainMaps(probeArr);
            var pairMaps = BuildPairMaps(probeArr, capMaps);
            int slabCount = probeArr.Length - 1;
            var slabPatterns = Enumerable.Range(0, slabCount)
                .Select(i => infillPatterns.Count == 1
                    ? infillPatterns[0]
                    : infillPatterns[i % infillPatterns.Count])
                .ToArray();
            var slabDisjoin = Enumerable.Range(0, slabCount)
                .Select(i =>
                {
                    int patternIndex = infillPatterns.Count == 1
                        ? 0
                        : i % infillPatterns.Count;
                    return disjoinValues.Count == 1
                        ? disjoinValues[0]
                        : disjoinValues[patternIndex % disjoinValues.Count];
                })
                .ToArray();
            bool anyDisjoin = slabDisjoin.Any(value => value);
            if (partitionThickness > EPS && anyDisjoin && slabDisjoin.Any(value => !value))
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "part_t welds the surface domains into one physical mesh. Mixed disjoin values therefore split the joined result globally because individual source domains no longer remain separate mesh objects.");
            if (infillPatterns.Count != 1 && infillPatterns.Count != slabCount)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{infillPatterns.Count} flattened infill_p values are cycling across {slabCount} surface domains.");

            var slabSizeU = new double[slabCount];
            var slabSizeV = new double[slabCount];
            var slabSizeW = new double[slabCount];
            for (int domainIndex = 0; domainIndex < slabCount; domainIndex++)
            {
                EstimateSlabSize(
                    probeArr,
                    capMaps,
                    pairMaps,
                    domainIndex,
                    out slabSizeU[domainIndex],
                    out slabSizeV[domainIndex],
                    out slabSizeW[domainIndex]);
            }

            bool useBoundarySdf = slabPatterns.Any(p => p.RequiresBoundaryClip(thickness));
            TrimVolume trimVolume = BuildTrimVolume(trimGeoRaw);
            if (trimGeoRaw != null && trimVolume == null)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "trim_geo was ignored. Provide a Box, closed Brep, closed Mesh, or Extrusion.");

            // Build the real boundary before field evaluation. In SDF mode this
            // becomes the clipping field, so the mesh is intersected with the
            // same closed volume emitted from bound_geo.
            List<Brep> boundDomains = MakeBoundGeoDomains(probeArr);
            Brep bound = JoinBoundDomains(boundDomains);
            if (useBoundarySdf && (bound == null || !bound.IsSolid))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "SDF clipping requested, but bound_geo is not a closed solid. " +
                    "Falling back to slab/domain SDF only.");
            }

            // ── Build analytical WasperField closure ──────────────────────
            SurfProbe[]    _cProbes   = probeArr;
            SlabDomain[]   _cDomains  = domains;
            CapDomainMap[] _cCapMaps  = capMaps;
            UvPairMap[]    _cPairMaps = pairMaps;
            Brep           _cBound    = bound;
            TrimVolume     _cTrim     = trimVolume;
            double _cThick     = thickness;
            bool   _cTranspose = transpose; bool  _cFlip   = flip;
            global::WASPer_3DP.WasperVolumetricPatternDescriptor[] _cSlabPatterns = slabPatterns;
            double[] _cSlabSizeU = slabSizeU;
            double[] _cSlabSizeV = slabSizeV;
            double[] _cSlabSizeW = slabSizeW;
            double _cPartT = partitionThickness;
            Brep[] _cPartitions = probeArr
                .Skip(1)
                .Take(Math.Max(0, probeArr.Length - 2))
                .Select(p => p.AsCapBrep())
                .Where(b => b != null && b.IsValid)
                .ToArray();

            BoundingBox analyticDomain = _cBound != null && _cBound.IsValid
                ? _cBound.GetBoundingBox(true) : BoundingBox.Unset;

            string sourceTrace =
                "Source: In09 Volumetric Multi-Infill (From Surfaces)\n" +
                $"domain_params={string.Join(" | ", slabPatterns.Select((p, i) => $"{i}:{p.PatternKind}:{p.Trace()}"))}\n" +
                "pattern_math=shared_volumetric_dispatch\n" +
                $"thickness={thickness:G6}\n" +
                $"shell_thickness={shellThickness:G6}\n" +
                $"partition_thickness={partitionThickness:G6}\n" +
                $"shell_caps={shellCaps}\n" +
                $"transpose={transpose}\n" +
                $"flip={flip}\n" +
                "phase_scope=local_per_surface_pair\n" +
                $"surfaces={probeArr.Length}\n" +
                $"slabs={Math.Max(0, probeArr.Length - 1)}\n" +
                $"trim_geo={trimVolume != null}\n" +
                "quality=ApproximateSdf";

            WasperField analyticalField = new WasperField(
                p =>
                {
                    double f = TRIM;
                    for (int domainIndex = 0; domainIndex < _cSlabPatterns.Length; domainIndex++)
                    {
                        var domainPattern = _cSlabPatterns[domainIndex];
                        double candidate = EvalPatternField(
                            p,
                            domainPattern,
                            _cThick,
                            new[] { _cProbes[domainIndex], _cProbes[domainIndex + 1] },
                            new[] { _cDomains[domainIndex] },
                            _cTranspose, _cFlip,
                            _cSlabSizeU[domainIndex],
                            _cSlabSizeV[domainIndex],
                            _cSlabSizeW[domainIndex],
                            domainPattern.RequiresBoundaryClip(_cThick));
                        f = Math.Min(f, candidate);
                    }
                    if (_cPartT > EPS && _cPartitions.Length > 0)
                    {
                        double partitionSdf = TRIM;
                        foreach (Brep partition in _cPartitions)
                            partitionSdf = Math.Min(
                                partitionSdf,
                                UnsignedDistanceToBrep(p, partition) - _cPartT * 0.5);
                        f = Math.Min(f, partitionSdf);
                    }
                    if (_cTrim != null) f = Math.Max(f, _cTrim.SignedDistance(p));
                    return f;
                },
                analyticDomain,
                slabPatterns.Select(p => p.PatternName).Distinct().Count() == 1
                    ? slabPatterns[0].PatternName
                    : "Multi-Pattern",
                sourceTrace,
                WasperFieldSdfQuality.ApproximateSdf);

            // ── Evaluate scalar field ─────────────────────────────────────
            int parallelThreads = Math.Max(1, Environment.ProcessorCount - 1);

            Mesh   result      = null;
            var    resultMeshes = new List<Mesh>();
            long   totalSamples = 0;
            string gridReport  = "n/a (mesh skipped)";
            long   evalMs      = 0;
            long   extractMs   = 0;
            var    domainMeshes = new List<Mesh>();

            if (outMesh)
            {
                result = BuildContinuousCurvilinearPatternMesh(
                    probeArr, capMaps, pairMaps, slabPatterns,
                    thickness,
                    transpose, flip,
                    res,
                    shellThickness, shellCaps, partitionThickness,
                    trimVolume,
                    parallelThreads,
                    out domainMeshes,
                    out totalSamples,
                    out gridReport,
                    out evalMs,
                    out extractMs);

                if (totalSamples > 20_000_000)
                {
                    string msg = $"Parametric grid = {totalSamples:N0} samples is too large. Increase resolution.";
                    AddRuntimeMessage(GH_RuntimeMessageLevel.Error, msg);
                    DA.SetData(5, "ERR: " + msg);
                    Message = "ERR";
                    return;
                }
            }

            // ── Extract mesh ──────────────────────────────────────────────
            int removedFragments = 0;
            var cleanWatch = Stopwatch.StartNew();
            if (result != null && result.Faces.Count > 0)
            {
                var cleanedDomains = domainMeshes.Count > 0
                    ? domainMeshes
                    : new List<Mesh> { result };
                foreach (Mesh domainMesh in cleanedDomains)
                    CleanMeshFromSampledGrid(domainMesh);

                if (anyDisjoin)
                {
                    resultMeshes = new List<Mesh>();
                    for (int domainIndex = 0; domainIndex < cleanedDomains.Count; domainIndex++)
                    {
                        Mesh domainMesh = cleanedDomains[domainIndex];
                        if (domainMesh == null || domainMesh.Faces.Count == 0)
                            continue;
                        bool splitDomain = partitionThickness > EPS
                            ? anyDisjoin
                            : domainIndex < slabDisjoin.Length && slabDisjoin[domainIndex];
                        if (!splitDomain)
                        {
                            resultMeshes.Add(domainMesh);
                            continue;
                        }
                        Mesh[] pieces = domainMesh.SplitDisjointPieces();
                        if (pieces == null || pieces.Length == 0)
                        {
                            resultMeshes.Add(domainMesh);
                            continue;
                        }

                        foreach (Mesh piece in pieces)
                        {
                            if (piece == null || piece.Faces.Count < 8)
                            {
                                removedFragments++;
                                continue;
                            }
                            CleanMeshFromSampledGrid(piece);
                            resultMeshes.Add(piece);
                        }
                    }
                    result = JoinMeshList(resultMeshes);
                }
                else
                {
                    // One output mesh per consecutive surface domain:
                    // [0]=surface 0→1, [1]=surface 1→2, and so on.
                    resultMeshes = cleanedDomains
                        .Where(mesh => mesh != null && mesh.Faces.Count > 0)
                        .ToList();
                    result = JoinMeshList(resultMeshes);
                }
            }
            cleanWatch.Stop();

            if (!outMesh && anyDisjoin)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    "mesh not generated because out_mesh=false. disjoin_mesh falls back to one analytical field.");
            }

            sw.Stop();

            if (outMesh && (result == null || result.Faces.Count == 0))
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning,
                    "No lattice mesh produced. Check level, resolution, " +
                    "surface orientation, and trim settings.");

            string cellName = string.Join(
                " | ",
                slabPatterns.Select((p, i) => $"{i}:{p.PatternName}"));
            string arrayStr = string.Join(
                " | ",
                slabPatterns.Select((p, i) => $"{i}:{p.CountsText}"));
            string info =
                $"{NAME}  {_versionTag}\n" +
                $"surfaces        : {probeArr.Length}  ({slabCount} slab{(slabCount != 1 ? "s" : "")})\n" +
                $"infill_p values : {infillPatterns.Count} ({(infillPatterns.Count == 1 ? "broadcast" : infillPatterns.Count == slabCount ? "one per domain" : "cycled")})\n" +
                $"domain cells    : {cellName}\n" +
                $"thickness       : {thickness:0.###}\n" +
                $"shell_thickness : {shellThickness:0.###}\n" +
                $"partition_t     : {partitionThickness:0.###}\n" +
                $"partition_mode  : two-sided model-space offset (part_t/2 + part_t/2)\n" +
                $"partition_join  : {(partitionThickness > EPS ? "internal caps removed; domains welded" : "disabled; per-domain meshes retained")}\n" +
                $"shell_caps      : {shellCaps}\n" +
                $"disjoin values : {disjoinValues.Count} ({(disjoinValues.Count == 1 ? "broadcast" : disjoinValues.Count == infillPatterns.Count ? "one per infill_p" : "cycled")})\n" +
                $"domain disjoin : {string.Join(" / ", slabDisjoin)}\n" +
                $"out_mesh        : {outMesh}\n" +
                $"domain arrays   : {arrayStr}\n" +
                "pattern_math    : shared volumetric dispatcher\n" +
                $"axis_map        : X→U, Y→V, Z→N{(transpose ? " | U/V swapped" : "")}{(flip ? " | N reversed" : "")}\n" +
                "phase_scope     : local to each surface pair\n" +
                $"resolution      : {res:0.###} model units\n" +
                $"explicit_close  : {string.Join(" / ", slabPatterns.Select(p => p.ExplicitlyCloseDomain))}\n" +
                $"trim_geo        : {(trimVolume != null)}\n" +
                $"sdf_intersect   : {useBoundarySdf}\n" +
                $"domain_mode     : independent consecutive-pair UVT fields\n" +
                $"rebuilt_domain  : {capMaps.Count(m => m != null)} / {capMaps.Length} caps\n" +
                $"uv_pair_align   : auto\n" +
                $"grid            : {gridReport} = {totalSamples:N0} samples\n" +
                $"eval threads    : {parallelThreads}\n" +
                $"mesh vertices   : {(result == null ? 0 : result.Vertices.Count):N0}\n" +
                $"mesh faces      : {(result == null ? 0 : result.Faces.Count):N0}\n" +
                $"disjoint meshes : {resultMeshes.Count:N0}\n" +
                $"surface domains : {Math.Max(0, probeArr.Length - 1):N0}\n" +
                $"bound domains   : {boundDomains.Count:N0}\n" +
                $"removed frags   : {removedFragments}\n" +
                "cleanup mode    : Fi06-style sampled-grid cleanup (no inverse surface queries)\n" +
                $"timing          : eval {evalMs} ms | " +
                $"extract {extractMs} ms | " +
                $"clean {cleanWatch.ElapsedMilliseconds} ms | " +
                $"total {sw.ElapsedMilliseconds} ms";

            var fieldOutputs = BuildFieldOutputs(
                anyDisjoin,
                outMesh,
                resultMeshes,
                analyticalField,
                cellName);

            DA.SetDataList(0, resultMeshes);
            DA.SetDataList(1, fieldOutputs);
            DA.SetDataList(2, boundDomains);
            DA.SetData(3, cellName);
            DA.SetData(4, arrayStr);
            DA.SetData(5, info);
            DA.SetData(
                6,
                new global::WASPer_3DP.WasperKpiSetGoo(
                    global::WASPer_3DP.WasperInfillKpiFactory.Create(
                        Name,
                        _versionTag,
                        Enumerable.Range(0, slabCount).Select(domainIndex =>
                            global::WASPer_3DP.WasperInfillKpiFactory.FromVolumetric(
                                slabPatterns[domainIndex],
                                slabSizeU[domainIndex],
                                slabSizeV[domainIndex],
                                slabSizeW[domainIndex])),
                        slabCount),
                    this));

            Message = !outMesh
                ? $"{_versionTag} | field only"
                : (result == null || result.Faces.Count == 0)
                    ? $"{_versionTag} | empty"
                    : $"{_versionTag} | {cellName}";
        }

        // ── Geometry extractor ────────────────────────────────────────────

        private static GeometryBase ExtractGeometry(IGH_Goo goo)
        {
            if (goo == null) return null;
            if (goo is GH_Surface ghSrf && ghSrf.Value != null) return ghSrf.Value;
            if (goo is GH_Brep    ghBrp && ghBrp.Value != null) return ghBrp.Value;
            // Fallback via ScriptVariable (handles wrapped types)
            object sv = goo.ScriptVariable();
            if (sv is Surface s) return s;
            if (sv is Brep    b) return b;
            return null;
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

            if (geometry is IGH_Goo goo)
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

        // ── Field evaluation — multi-slab ─────────────────────────────────

        private static double EvalPatternField(
            Point3d point,
            global::WASPer_3DP.WasperVolumetricPatternDescriptor pattern,
            double thickness,
            SurfProbe[] probes,
            SlabDomain[] domains,
            bool transpose,
            bool flip,
            double sizeU,
            double sizeV,
            double sizeW,
            bool useBoundarySdf)
        {
            if (probes == null || probes.Length < 2)
                return TRIM;

            SurfProbe a = probes[0];
            SurfProbe b = probes[1];
            if (!a.Closest(
                    point,
                    out double uA,
                    out double vA,
                    out Point3d pointA,
                    out Vector3d normalA) ||
                !b.Closest(
                    point,
                    out double _,
                    out double _,
                    out Point3d pointB,
                    out Vector3d normalB))
                return TRIM;

            Vector3d between = pointB - pointA;
            double depth = between.Length;
            if (depth <= EPS)
                return TRIM;

            if (a.FlipNormal) normalA = -normalA;
            if (b.FlipNormal) normalB = -normalB;
            if (normalA * between < 0.0) normalA = -normalA;
            if (normalB * between > 0.0) normalB = -normalB;

            double distanceA = (point - pointA) * normalA;
            double distanceB = (point - pointB) * normalB;
            if (distanceA < 0.0 || distanceB < 0.0)
                return TRIM;

            SlabDomain domain = domains != null && domains.Length > 0
                ? domains[0]
                : null;
            if (domain != null && !domain.Contains(point))
                return TRIM;

            double denominator = distanceA + distanceB;
            if (Math.Abs(denominator) < EPS * depth)
                denominator = EPS * depth;
            double w = Clamp01(distanceA / denominator);
            if (flip) w = 1.0 - w;

            double u = Clamp01(uA);
            double v = Clamp01(vA);
            if (transpose)
            {
                double swap = u;
                u = v;
                v = swap;
            }

            double value = pattern.EvaluateNormalized(
                u, v, w,
                Math.Max(sizeU, EPS),
                Math.Max(sizeV, EPS),
                Math.Max(sizeW, EPS),
                thickness,
                true);

            if (useBoundarySdf)
            {
                double boundarySdf = Math.Max(-distanceA, -distanceB);
                if (domain != null)
                    boundarySdf = Math.Max(boundarySdf, domain.SignedDistance(point));
                value = Math.Max(value, boundarySdf);
            }

            return value;
        }

        private static double EvalField(
            Point3d P,
            int type, double level, double thickness, bool invertField,
            SurfProbe[] probes,
            SlabDomain[] domains,
            bool transpose, bool flip,
            double countU, double countV, double countN,
            double phaseU, double phaseV, double phaseN,
            bool trimDomain, double trimOff, bool trimLat,
            bool useBoundarySdf,
            Brep bound,
            CapDomainMap[] capMaps,
            UvPairMap[] pairMaps)
        {
            if (useBoundarySdf)
                return EvalFieldWithBoundarySdf(
                    P, type, level, thickness, invertField, probes, domains,
                    transpose, flip,
                    countU, countV, countN,
                    phaseU, phaseV, phaseN,
                    trimDomain, trimOff, trimLat,
                    bound,
                    capMaps,
                    pairMaps);

            double field = EvalSignedField(
                P, type, level, probes, domains,
                transpose, flip,
                countU, countV, countN,
                phaseU, phaseV, phaseN,
                trimDomain, trimOff, trimLat);

            if (field > TRIM * 0.1)
                return TRIM;

            if (thickness <= EPS)
                return invertField ? -field : field;

            double gradMag = NumericalGradientMagnitude(
                P, type, level, probes, domains,
                transpose, flip,
                countU, countV, countN,
                phaseU, phaseV, phaseN,
                trimDomain, trimOff, trimLat);

            return ApplyThicknessAndInvert(field, gradMag, thickness, invertField);
        }

        private static double EvalFieldWithBoundarySdf(
            Point3d P,
            int type, double level, double thickness, bool invertField,
            SurfProbe[] probes,
            SlabDomain[] domains,
            bool transpose, bool flip,
            double countU, double countV, double countN,
            double phaseU, double phaseV, double phaseN,
            bool trimDomain, double trimOff, bool trimLat,
            Brep bound,
            CapDomainMap[] capMaps,
            UvPairMap[] pairMaps)
        {
            double threshold = trimDomain ? -trimOff : 0.0;
            double best = TRIM;
            for (int i = 0; i < probes.Length - 1; i++)
            {
                double field;
                double boundSdf;
                if (!TryEvaluateSlab(P, i, type, level, probes, domains, capMaps, pairMaps,
                    transpose, flip, countU, countV, countN,
                    phaseU, phaseV, phaseN,
                    threshold, trimLat, out field, out boundSdf))
                    continue;

                double gradMag = NumericalGradientMagnitudeMapped(
                    P, type, level, probes, domains, capMaps, pairMaps,
                    transpose, flip,
                    countU, countV, countN,
                    phaseU, phaseV, phaseN);

                if (gradMag < EPS) gradMag = EPS;

                double tpmsDistance = thickness > EPS
                    ? Math.Abs(field / gradMag) - thickness * 0.5
                    : field / gradMag;

                if (invertField) tpmsDistance = -tpmsDistance;

                // Clip against this pair, not the outermost joined envelope.
                double candidate = Math.Max(tpmsDistance, boundSdf);
                if (candidate < best) best = candidate;
            }

            return best;
        }

        private static bool TryEvaluateSlab(
            Point3d P,
            int slabIndex,
            int type, double level,
            SurfProbe[] probes,
            SlabDomain[] domains,
            CapDomainMap[] capMaps,
            UvPairMap[] pairMaps,
            bool transpose, bool flip,
            double countU, double countV, double countN,
            double phaseU, double phaseV, double phaseN,
            double threshold,
            bool trimLat,
            out double field,
            out double boundSdf)
        {
            field = TRIM;
            boundSdf = TRIM;

            if (probes == null || slabIndex < 0 || slabIndex >= probes.Length - 1)
                return false;

            SurfProbe A = probes[slabIndex];
            SurfProbe B = probes[slabIndex + 1];

            if (!A.Closest(P, out double uA, out double vA,
                           out Point3d CA, out Vector3d NA)) return false;
            if (!B.Closest(P, out double uB, out double vB,
                           out Point3d CB, out Vector3d NB)) return false;

            bool gotA = TryEvaluateSlabAtUv(
                P, slabIndex, Clamp01(uA), Clamp01(vA),
                type, level, probes, domains, capMaps, pairMaps,
                transpose, flip, countU, countV, countN,
                phaseU, phaseV, phaseN,
                threshold, trimLat,
                out double fieldA, out double boundA);

            UvPairMap map = GetPairMap(pairMaps, slabIndex);
            double bCanonU, bCanonV;
            map.MapFromB(Clamp01(uB), Clamp01(vB), out bCanonU, out bCanonV);

            bool gotB = TryEvaluateSlabAtUv(
                P, slabIndex, bCanonU, bCanonV,
                type, level, probes, domains, capMaps, pairMaps,
                transpose, flip, countU, countV, countN,
                phaseU, phaseV, phaseN,
                threshold, trimLat,
                out double fieldB, out double boundB);

            if (!gotA && !gotB) return false;

            if (gotA && (!gotB || boundA <= boundB))
            {
                field = fieldA;
                boundSdf = boundA;
            }
            else
            {
                field = fieldB;
                boundSdf = boundB;
            }

            return true;
        }

        private static bool TryEvaluateSlabAtUv(
            Point3d P,
            int slabIndex,
            double u01,
            double v01,
            int type, double level,
            SurfProbe[] probes,
            SlabDomain[] domains,
            CapDomainMap[] capMaps,
            UvPairMap[] pairMaps,
            bool transpose, bool flip,
            double countU, double countV, double countN,
            double phaseU, double phaseV, double phaseN,
            double threshold,
            bool trimLat,
            out double field,
            out double boundSdf)
        {
            field = TRIM;
            boundSdf = TRIM;

            SurfProbe A = probes[slabIndex];
            SurfProbe B = probes[slabIndex + 1];
            UvPairMap map = GetPairMap(pairMaps, slabIndex);
            double bU, bV;
            map.MapToB(u01, v01, out bU, out bV);

            if (!EvaluateMappedCap(A, capMaps, slabIndex, u01, v01, out Point3d CA, out Vector3d NA)) return false;
            if (!EvaluateMappedCap(B, capMaps, slabIndex + 1, bU, bV, out Point3d CB, out Vector3d NB)) return false;

            Vector3d AB = CB - CA;
            double abLen = AB.Length;
            if (abLen < EPS) return false;

            if (A.FlipNormal) NA = -NA;
            if (B.FlipNormal) NB = -NB;
            if (NA * AB < 0) NA = -NA;
            if (NB * AB > 0) NB = -NB;

            double d0 = (P - CA) * NA;
            double d1 = (P - CB) * NB;

            double depthSdf = Math.Max(threshold - d0, threshold - d1);
            boundSdf = depthSdf;

            if (trimLat)
            {
                SlabDomain domain = domains != null && slabIndex < domains.Length ? domains[slabIndex] : null;
                if (domain != null)
                {
                    boundSdf = Math.Max(boundSdf, domain.SignedDistance(P));
                }
                else
                {
                    bool insideCaps = A.IsInside(P, u01, v01, CA) && B.IsInside(P, bU, bV, CB);
                    if (!insideCaps)
                        boundSdf = Math.Max(boundSdf, abLen);
                }
            }

            double denom = d0 + d1;
            if (Math.Abs(denom) < EPS * abLen)
                denom = EPS * abLen;
            double t = d0 / denom;
            t = Clamp01(t);
            if (flip) t = 1.0 - t;

            double fu = u01;
            double fv = v01;
            if (transpose) { double tmp = fu; fu = fv; fv = tmp; }

            double x = TWO_PI * (countU * fu + phaseU);
            double y = TWO_PI * (countV * fv + phaseV);
            double z = TWO_PI * (countN * t + phaseN);

            field = global::WASPer_3DP.WasperTpmsPatternMath.Value(type, x, y, z) - level;
            return true;
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

        private static double UnsignedDistanceToBrep(Point3d point, Brep brep)
        {
            if (brep == null || !brep.IsValid)
                return TRIM;

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
            return ok && closest.IsValid ? point.DistanceTo(closest) : TRIM;
        }

        private static double EvalSignedField(
            Point3d P,
            int type, double level,
            SurfProbe[] probes,
            SlabDomain[] domains,
            bool transpose, bool flip,
            double countU, double countV, double countN,
            double phaseU, double phaseV, double phaseN,
            bool trimDomain, double trimOff, bool trimLat)
        {
            double threshold = trimDomain ? -trimOff : 0.0;

            for (int i = 0; i < probes.Length - 1; i++)
            {
                SurfProbe A = probes[i];
                SurfProbe B = probes[i + 1];

                if (!A.Closest(P, out double uA, out double vA,
                               out Point3d CA, out Vector3d NA)) continue;
                if (!B.Closest(P, out double uB, out double vB,
                               out Point3d CB, out Vector3d NB)) continue;

                Vector3d AB = CB - CA;
                double abLen = AB.Length;
                if (abLen < EPS) continue;

                // Apply pre-computed global flip, then local AB-based correction
                if (A.FlipNormal) NA = -NA;
                if (B.FlipNormal) NB = -NB;
                if (NA * AB < 0) NA = -NA;
                if (NB * AB > 0) NB = -NB;

                double d0 = (P - CA) * NA;
                double d1 = (P - CB) * NB;

                // Slab membership: both half-spaces must be satisfied
                if (d0 < threshold || d1 < threshold) continue;

                // Lateral trim
                if (trimLat)
                {
                    SlabDomain domain = domains != null && i < domains.Length ? domains[i] : null;
                    if (domain != null)
                    {
                        if (!domain.Contains(P)) return TRIM;
                    }
                    else if (!A.IsInside(P, uA, vA, CA) || !B.IsInside(P, uB, vB, CB))
                    {
                        return TRIM;
                    }
                }

                // Through-thickness coordinate t ∈ [0,1]
                double denom = d0 + d1;
                if (Math.Abs(denom) < EPS * abLen)
                    denom = EPS * abLen;
                double t = d0 / denom;
                t = Clamp01(t);
                if (flip) t = 1.0 - t;

                // In-plane phases — UV already normalized to [0,1] by SurfProbe
                double u01 = uA;
                double v01 = vA;
                if (transpose) { double tmp = u01; u01 = v01; v01 = tmp; }

                double x = TWO_PI * (countU * u01 + phaseU);
                double y = TWO_PI * (countV * v01 + phaseV);
                double z = TWO_PI * (countN * t + phaseN);

                double field = global::WASPer_3DP.WasperTpmsPatternMath.Value(type, x, y, z) - level;
                return field;
            }

            return TRIM;
        }

        private static double NumericalGradientMagnitude(
            Point3d P,
            int type, double level,
            SurfProbe[] probes,
            SlabDomain[] domains,
            bool transpose, bool flip,
            double countU, double countV, double countN,
            double phaseU, double phaseV, double phaseN,
            bool trimDomain, double trimOff, bool trimLat)
        {
            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
            double h = Math.Max(tol * 10.0, 1e-4);

            double fx0 = EvalSignedField(new Point3d(P.X - h, P.Y,     P.Z    ), type, level, probes, domains, transpose, flip, countU, countV, countN, phaseU, phaseV, phaseN, trimDomain, trimOff, trimLat);
            double fx1 = EvalSignedField(new Point3d(P.X + h, P.Y,     P.Z    ), type, level, probes, domains, transpose, flip, countU, countV, countN, phaseU, phaseV, phaseN, trimDomain, trimOff, trimLat);
            double fy0 = EvalSignedField(new Point3d(P.X,     P.Y - h, P.Z    ), type, level, probes, domains, transpose, flip, countU, countV, countN, phaseU, phaseV, phaseN, trimDomain, trimOff, trimLat);
            double fy1 = EvalSignedField(new Point3d(P.X,     P.Y + h, P.Z    ), type, level, probes, domains, transpose, flip, countU, countV, countN, phaseU, phaseV, phaseN, trimDomain, trimOff, trimLat);
            double fz0 = EvalSignedField(new Point3d(P.X,     P.Y,     P.Z - h), type, level, probes, domains, transpose, flip, countU, countV, countN, phaseU, phaseV, phaseN, trimDomain, trimOff, trimLat);
            double fz1 = EvalSignedField(new Point3d(P.X,     P.Y,     P.Z + h), type, level, probes, domains, transpose, flip, countU, countV, countN, phaseU, phaseV, phaseN, trimDomain, trimOff, trimLat);

            if (fx0 > TRIM * 0.1 || fx1 > TRIM * 0.1 ||
                fy0 > TRIM * 0.1 || fy1 > TRIM * 0.1 ||
                fz0 > TRIM * 0.1 || fz1 > TRIM * 0.1)
                return EPS;

            double gx = (fx1 - fx0) / (2.0 * h);
            double gy = (fy1 - fy0) / (2.0 * h);
            double gz = (fz1 - fz0) / (2.0 * h);
            return Math.Sqrt(gx * gx + gy * gy + gz * gz);
        }

        private static double NumericalGradientMagnitudeMapped(
            Point3d P,
            int type, double level,
            SurfProbe[] probes,
            SlabDomain[] domains,
            CapDomainMap[] capMaps,
            UvPairMap[] pairMaps,
            bool transpose, bool flip,
            double countU, double countV, double countN,
            double phaseU, double phaseV, double phaseN)
        {
            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
            double h = Math.Max(tol * 10.0, 1e-4);

            double fx0 = EvalMappedSignedField(new Point3d(P.X - h, P.Y,     P.Z    ), type, level, probes, domains, capMaps, pairMaps, transpose, flip, countU, countV, countN, phaseU, phaseV, phaseN);
            double fx1 = EvalMappedSignedField(new Point3d(P.X + h, P.Y,     P.Z    ), type, level, probes, domains, capMaps, pairMaps, transpose, flip, countU, countV, countN, phaseU, phaseV, phaseN);
            double fy0 = EvalMappedSignedField(new Point3d(P.X,     P.Y - h, P.Z    ), type, level, probes, domains, capMaps, pairMaps, transpose, flip, countU, countV, countN, phaseU, phaseV, phaseN);
            double fy1 = EvalMappedSignedField(new Point3d(P.X,     P.Y + h, P.Z    ), type, level, probes, domains, capMaps, pairMaps, transpose, flip, countU, countV, countN, phaseU, phaseV, phaseN);
            double fz0 = EvalMappedSignedField(new Point3d(P.X,     P.Y,     P.Z - h), type, level, probes, domains, capMaps, pairMaps, transpose, flip, countU, countV, countN, phaseU, phaseV, phaseN);
            double fz1 = EvalMappedSignedField(new Point3d(P.X,     P.Y,     P.Z + h), type, level, probes, domains, capMaps, pairMaps, transpose, flip, countU, countV, countN, phaseU, phaseV, phaseN);

            if (fx0 > TRIM * 0.1 || fx1 > TRIM * 0.1 ||
                fy0 > TRIM * 0.1 || fy1 > TRIM * 0.1 ||
                fz0 > TRIM * 0.1 || fz1 > TRIM * 0.1)
                return EPS;

            double gx = (fx1 - fx0) / (2.0 * h);
            double gy = (fy1 - fy0) / (2.0 * h);
            double gz = (fz1 - fz0) / (2.0 * h);
            return Math.Sqrt(gx * gx + gy * gy + gz * gz);
        }

        private static double EvalMappedSignedField(
            Point3d P,
            int type, double level,
            SurfProbe[] probes,
            SlabDomain[] domains,
            CapDomainMap[] capMaps,
            UvPairMap[] pairMaps,
            bool transpose, bool flip,
            double countU, double countV, double countN,
            double phaseU, double phaseV, double phaseN)
        {
            double best = TRIM;
            for (int i = 0; i < probes.Length - 1; i++)
            {
                if (TryEvaluateSlab(P, i, type, level, probes, domains, capMaps, pairMaps,
                    transpose, flip, countU, countV, countN,
                    phaseU, phaseV, phaseN,
                    0.0, false, out double field, out double boundSdf))
                {
                    if (Math.Abs(boundSdf) < Math.Abs(best))
                        best = field;
                }
            }
            return best;
        }

        private static double ApplyThicknessAndInvert(
            double field, double gradMag, double thickness, bool invertField)
        {
            if (gradMag < EPS) gradMag = EPS;
            double value = thickness > EPS
                ? Math.Abs(field / gradMag) - thickness * 0.5
                : field;
            return invertField ? -value : value;
        }

        // ── Bound geometry — multi-surface chain ──────────────────────────

        private static SlabDomain[] BuildSlabDomains(SurfProbe[] probes)
        {
            if (probes == null || probes.Length < 2)
                return new SlabDomain[0];

            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
            var domains = new SlabDomain[probes.Length - 1];

            for (int i = 0; i < probes.Length - 1; i++)
            {
                Brep cap = probes[i].AsCapBrep();
                if (cap == null || cap.Faces.Count == 0) continue;

                Curve outer = JoinLoopAsCurve(cap.Faces[0].OuterLoop, tol);
                if (outer == null || !outer.IsClosed) continue;

                Plane plane;
                if (!cap.Faces[0].TryGetPlane(out plane))
                {
                    var pts = SampleCurvePoints(outer, BOUNDARY_SAMPLE_COUNT);
                    if (pts.Count < 3 ||
                        Plane.FitPlaneToPoints(pts, out plane) != PlaneFitResult.Success)
                        continue;
                }

                domains[i] = SlabDomain.Create(outer, plane, BOUNDARY_SAMPLE_COUNT);
            }

            return domains;
        }

        private static List<Point3d> SampleCurvePoints(Curve curve, int count)
        {
            var pts = new List<Point3d>();
            if (curve == null || count < 4) return pts;

            for (int i = 0; i < count; i++)
            {
                double normalized = i / (double)count;
                if (curve.NormalizedLengthParameter(normalized, out double t))
                    pts.Add(curve.PointAt(t));
            }

            return pts;
        }

        private static CapDomainMap[] BuildCapDomainMaps(SurfProbe[] probes)
        {
            if (probes == null) return new CapDomainMap[0];

            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
            var maps = new CapDomainMap[probes.Length];
            for (int i = 0; i < probes.Length; i++)
                maps[i] = CapDomainMap.TryCreate(probes[i], tol);
            return maps;
        }

        private static bool EvaluateMappedCap(
            SurfProbe probe,
            CapDomainMap[] maps,
            int index,
            double u,
            double v,
            out Point3d point,
            out Vector3d normal)
        {
            point = Point3d.Unset;
            normal = Vector3d.Unset;

            if (maps != null && index >= 0 && index < maps.Length && maps[index] != null)
                return maps[index].Evaluate(u, v, out point, out normal);

            return probe != null && probe.EvaluateAt(u, v, out point, out normal);
        }

        private static UvPairMap[] BuildPairMaps(SurfProbe[] probes, CapDomainMap[] capMaps)
        {
            if (probes == null || probes.Length < 2)
                return new UvPairMap[0];

            var maps = new UvPairMap[probes.Length - 1];
            for (int i = 0; i < probes.Length - 1; i++)
                maps[i] = UvPairMap.FindBest(probes, capMaps, i);
            return maps;
        }

        private static UvPairMap GetPairMap(UvPairMap[] maps, int index)
        {
            if (maps == null || index < 0 || index >= maps.Length)
                return UvPairMap.Identity;
            return maps[index];
        }

        private struct UvPairMap
        {
            public bool Swap;
            public bool FlipU;
            public bool FlipV;

            public static readonly UvPairMap Identity = new UvPairMap
            {
                Swap = false,
                FlipU = false,
                FlipV = false
            };

            public void MapToB(double u, double v, out double bu, out double bv)
            {
                double x = Swap ? v : u;
                double y = Swap ? u : v;
                if (FlipU) x = 1.0 - x;
                if (FlipV) y = 1.0 - y;
                bu = Clamp01(x);
                bv = Clamp01(y);
            }

            public void MapFromB(double bu, double bv, out double u, out double v)
            {
                double x = FlipU ? 1.0 - bu : bu;
                double y = FlipV ? 1.0 - bv : bv;
                u = Clamp01(Swap ? y : x);
                v = Clamp01(Swap ? x : y);
            }

            public static UvPairMap FindBest(SurfProbe[] probes, CapDomainMap[] capMaps, int pairIndex)
            {
                UvPairMap best = Identity;
                double bestScore = double.PositiveInfinity;

                bool[] flags = { false, true };
                foreach (bool swap in flags)
                foreach (bool flipU in flags)
                foreach (bool flipV in flags)
                {
                    var map = new UvPairMap { Swap = swap, FlipU = flipU, FlipV = flipV };
                    double score = Score(probes, capMaps, pairIndex, map);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        best = map;
                    }
                }

                return best;
            }

            private static double Score(SurfProbe[] probes, CapDomainMap[] capMaps, int pairIndex, UvPairMap map)
            {
                if (probes == null || pairIndex < 0 || pairIndex >= probes.Length - 1)
                    return double.PositiveInfinity;

                SurfProbe a = probes[pairIndex];
                SurfProbe b = probes[pairIndex + 1];
                if (a == null || b == null) return double.PositiveInfinity;

                double[,] samples =
                {
                    {0.0, 0.0}, {1.0, 0.0}, {1.0, 1.0}, {0.0, 1.0},
                    {0.5, 0.0}, {1.0, 0.5}, {0.5, 1.0}, {0.0, 0.5},
                    {0.5, 0.5}
                };

                double score = 0.0;
                int count = 0;
                for (int i = 0; i < samples.GetLength(0); i++)
                {
                    double u = samples[i, 0];
                    double v = samples[i, 1];
                    if (!EvaluateMappedCap(a, capMaps, pairIndex, u, v, out Point3d pa, out Vector3d _)) continue;

                    map.MapToB(u, v, out double bu, out double bv);
                    if (!EvaluateMappedCap(b, capMaps, pairIndex + 1, bu, bv, out Point3d pb, out Vector3d _)) continue;

                    score += pa.DistanceToSquared(pb);
                    count++;
                }

                return count > 0 ? score / count : double.PositiveInfinity;
            }
        }

        private sealed class CapDomainMap
        {
            private readonly Curve _bottom;
            private readonly Curve _right;
            private readonly Curve _top;
            private readonly Curve _left;
            private readonly Point3d _p00;
            private readonly Point3d _p10;
            private readonly Point3d _p11;
            private readonly Point3d _p01;

            private CapDomainMap(Curve bottom, Curve right, Curve top, Curve left)
            {
                _bottom = bottom;
                _right = right;
                _top = top;
                _left = left;

                _p00 = _bottom.PointAtStart;
                _p10 = _bottom.PointAtEnd;
                _p11 = _right.PointAtEnd;
                _p01 = _left.PointAtEnd;
            }

            public static CapDomainMap TryCreate(SurfProbe probe, double tol)
            {
                if (probe == null) return null;

                Brep cap = probe.AsCapBrep();
                if (cap == null || cap.Faces.Count == 0) return null;

                List<Curve> edges;
                List<Point3d> corners;
                if (!OuterEdgesAndCorners(cap.Faces[0], tol, out edges, out corners))
                    return null;

                if (edges.Count != 4 || corners.Count != 4)
                    return null;

                Curve bottom = edges[0].DuplicateCurve();
                Curve right = edges[1].DuplicateCurve();
                Curve top = edges[2].DuplicateCurve();
                Curve left = edges[3].DuplicateCurve();
                if (bottom == null || right == null || top == null || left == null)
                    return null;

                top.Reverse();
                left.Reverse();

                return new CapDomainMap(bottom, right, top, left);
            }

            public bool Evaluate(double u, double v, out Point3d point, out Vector3d normal)
            {
                u = Clamp01(u);
                v = Clamp01(v);

                point = Coons(u, v);

                const double h = 1e-4;
                Point3d pu0 = Coons(Clamp01(u - h), v);
                Point3d pu1 = Coons(Clamp01(u + h), v);
                Point3d pv0 = Coons(u, Clamp01(v - h));
                Point3d pv1 = Coons(u, Clamp01(v + h));

                Vector3d du = pu1 - pu0;
                Vector3d dv = pv1 - pv0;
                normal = Vector3d.CrossProduct(du, dv);
                return normal.Unitize();
            }

            private Point3d Coons(double u, double v)
            {
                Point3d c0 = PointAtNormalizedLength(_bottom, u);
                Point3d c1 = PointAtNormalizedLength(_top, u);
                Point3d d0 = PointAtNormalizedLength(_left, v);
                Point3d d1 = PointAtNormalizedLength(_right, v);

                Point3d ruledA = c0 + v * (c1 - c0);
                Point3d ruledB = d0 + u * (d1 - d0);

                Point3d bilinear =
                    (1.0 - u) * (1.0 - v) * _p00 +
                    u * (1.0 - v) * _p10 +
                    u * v * _p11 +
                    (1.0 - u) * v * _p01;

                return ruledA + (Vector3d)ruledB - (Vector3d)bilinear;
            }

            private static Point3d PointAtNormalizedLength(Curve curve, double t01)
            {
                if (curve == null) return Point3d.Unset;
                if (curve.NormalizedLengthParameter(Clamp01(t01), out double t))
                    return curve.PointAt(t);
                return curve.PointAt(curve.Domain.ParameterAt(Clamp01(t01)));
            }
        }

        private sealed class SlabDomain
        {
            private readonly Plane _plane;
            private readonly Point2d[] _poly;
            private readonly double _minX, _maxX, _minY, _maxY;

            private SlabDomain(Plane plane, Point2d[] poly)
            {
                _plane = plane;
                _poly  = poly;
                _minX  = _minY = double.MaxValue;
                _maxX  = _maxY = double.MinValue;

                foreach (Point2d p in poly)
                {
                    if (p.X < _minX) _minX = p.X;
                    if (p.Y < _minY) _minY = p.Y;
                    if (p.X > _maxX) _maxX = p.X;
                    if (p.Y > _maxY) _maxY = p.Y;
                }
            }

            public static SlabDomain Create(Curve boundary, Plane plane, int sampleCount)
            {
                var pts = SampleCurvePoints(boundary, sampleCount);
                if (pts.Count < 3) return null;

                var poly = new List<Point2d>();
                foreach (Point3d p in pts)
                {
                    Vector3d d = p - plane.Origin;
                    poly.Add(new Point2d(d * plane.XAxis, d * plane.YAxis));
                }

                return new SlabDomain(plane, poly.ToArray());
            }

            public bool Contains(Point3d point)
            {
                Vector3d d = point - _plane.Origin;
                double x = d * _plane.XAxis;
                double y = d * _plane.YAxis;

                if (x < _minX || x > _maxX || y < _minY || y > _maxY)
                    return false;

                return PointInPolygon(x, y, _poly);
            }

            public double SignedDistance(Point3d point)
            {
                Vector3d d = point - _plane.Origin;
                double x = d * _plane.XAxis;
                double y = d * _plane.YAxis;

                bool inside = PointInPolygon(x, y, _poly);
                double dist = DistanceToPolyline(x, y, _poly);
                return inside ? -dist : dist;
            }

            private static double DistanceToPolyline(double x, double y, Point2d[] poly)
            {
                if (poly == null || poly.Length == 0) return TRIM;

                double best = double.MaxValue;
                int n = poly.Length;
                for (int i = 0; i < n; i++)
                {
                    Point2d a = poly[i];
                    Point2d b = poly[(i + 1) % n];
                    double d = DistancePointSegment(x, y, a.X, a.Y, b.X, b.Y);
                    if (d < best) best = d;
                }

                return best;
            }

            private static double DistancePointSegment(
                double px, double py,
                double ax, double ay,
                double bx, double by)
            {
                double vx = bx - ax;
                double vy = by - ay;
                double wx = px - ax;
                double wy = py - ay;

                double len2 = vx * vx + vy * vy;
                double t = len2 <= EPS ? 0.0 : (wx * vx + wy * vy) / len2;
                if (t < 0.0) t = 0.0;
                else if (t > 1.0) t = 1.0;

                double dx = px - (ax + t * vx);
                double dy = py - (ay + t * vy);
                return Math.Sqrt(dx * dx + dy * dy);
            }

            private static bool PointInPolygon(double x, double y, Point2d[] poly)
            {
                bool inside = false;
                int n = poly.Length;

                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double yi = poly[i].Y;
                    double yj = poly[j].Y;
                    if ((yi > y) == (yj > y)) continue;

                    double xi = poly[i].X;
                    double xj = poly[j].X;
                    double xCross = (xj - xi) * (y - yi) / (yj - yi + EPS) + xi;
                    if (x < xCross) inside = !inside;
                }

                return inside;
            }
        }

        private static Brep MakeBoundGeoMulti(SurfProbe[] probes)
        {
            return JoinBoundDomains(MakeBoundGeoDomains(probes));
        }

        private static List<Brep> MakeBoundGeoDomains(SurfProbe[] probes)
        {
            var solids = new List<Brep>();
            if (probes == null || probes.Length < 2)
                return solids;

            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
            for (int i = 0; i < probes.Length - 1; i++)
            {
                Brep capA = probes[i].AsCapBrep();
                Brep capB = probes[i + 1].AsCapBrep();
                if (capA == null || capB == null) continue;

                Brep solid = BuildSolidBetweenCaps(capA, capB, tol);
                if (solid != null)
                    solids.Add(solid);
            }
            return solids;
        }

        private static Brep JoinBoundDomains(List<Brep> solids)
        {
            if (solids == null || solids.Count == 0)
                return null;
            if (solids.Count == 1)
                return solids[0];

            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
            Brep[] joined = Brep.JoinBreps(solids, tol);
            return joined != null && joined.Length > 0
                ? joined[0]
                : Brep.MergeBreps(solids, tol);
        }

        private static Brep BuildSolidBetweenCaps(Brep capA, Brep capB, double tol)
        {
            if (capA == null || capB == null ||
                capA.Faces.Count == 0 || capB.Faces.Count == 0) return null;

            List<Curve> eA, eB;
            List<Point3d> vA, vB;
            bool gotA = OuterEdgesAndCorners(capA.Faces[0], tol, out eA, out vA);
            bool gotB = OuterEdgesAndCorners(capB.Faces[0], tol, out eB, out vB);

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
                    Brep[] side = Brep.CreateFromLoft(
                        new Curve[] { eA[i], eB[i] },
                        Point3d.Unset, Point3d.Unset,
                        LoftType.Straight, false);
                    if (side != null && side.Length > 0 && side[0] != null)
                        parts.Add(side[0]);
                }

                Brep[] joined = Brep.JoinBreps(parts, tol);
                Brep solid = joined != null && joined.Length > 0
                    ? joined[0]
                    : Brep.MergeBreps(parts, tol);
                return CleanBrep(solid, tol);
            }

            return CleanBrep(BuildSolidByLoopLoft(capA, capB, tol), tol);
        }

        private static bool OuterEdgesAndCorners(
            BrepFace face, double tol,
            out List<Curve> edges, out List<Point3d> corners)
        {
            edges   = new List<Curve>();
            corners = new List<Point3d>();
            if (face?.OuterLoop == null) return false;

            foreach (BrepTrim trim in face.OuterLoop.Trims)
            {
                BrepEdge edge = trim.Edge;
                if (edge == null) continue;

                bool rev = trim.IsReversed();
                BrepVertex vtx = rev ? edge.EndVertex : edge.StartVertex;
                if (vtx == null) continue;

                Curve c = edge.DuplicateCurve();
                if (c == null) continue;
                if (rev) c.Reverse();
                if (c.PointAtStart.DistanceTo(vtx.Location) > tol) c.Reverse();

                corners.Add(vtx.Location);
                edges.Add(c);
            }

            return edges.Count >= 3 && corners.Count == edges.Count;
        }

        private static void AlignCornerSets(
            List<Point3d> a, List<Point3d> b,
            out bool useReverse, out int shift)
        {
            useReverse = false; shift = 0;
            int n = a.Count;

            double bestF = double.PositiveInfinity; int sF = 0;
            for (int k = 0; k < n; k++)
            {
                double score = 0.0;
                for (int i = 0; i < n; i++)
                    score += a[i].DistanceToSquared(b[(k + i) % n]);
                if (score < bestF) { bestF = score; sF = k; }
            }

            var br = new List<Point3d>(b); br.Reverse();
            double bestR = double.PositiveInfinity; int sR = 0;
            for (int k = 0; k < n; k++)
            {
                double score = 0.0;
                for (int i = 0; i < n; i++)
                    score += a[i].DistanceToSquared(br[(k + i) % n]);
                if (score < bestR) { bestR = score; sR = k; }
            }

            if (bestR < bestF) { useReverse = true; shift = sR; }
            else               { shift = sF; }
        }

        private static Brep BuildSolidByLoopLoft(Brep capA, Brep capB, double tol)
        {
            Curve loopA = JoinLoopAsCurve(capA.Faces[0].OuterLoop, tol);
            Curve loopB = JoinLoopAsCurve(capB.Faces[0].OuterLoop, tol);
            if (loopA == null || loopB == null) return null;

            AlignClosedPair(loopA, loopB);

            Brep[] loft = Brep.CreateFromLoft(
                new Curve[] { loopA, loopB },
                Point3d.Unset, Point3d.Unset,
                LoftType.Normal, false);
            if (loft == null || loft.Length == 0 || loft[0] == null) return null;

            var parts = new List<Brep> { capA, capB, loft[0] };
            Brep[] joined = Brep.JoinBreps(parts, tol);
            return joined != null && joined.Length > 0
                ? joined[0]
                : Brep.MergeBreps(parts, tol);
        }

        private static Curve JoinLoopAsCurve(BrepLoop loop, double tol)
        {
            if (loop == null) return null;
            var segs = new List<Curve>();
            foreach (BrepTrim trim in loop.Trims)
            {
                BrepEdge edge = trim.Edge;
                if (edge == null) continue;
                Curve c = edge.DuplicateCurve();
                if (c == null) continue;
                if (trim.IsReversed()) c.Reverse();
                segs.Add(c);
            }
            if (segs.Count == 0) return null;

            Curve[] joined = Curve.JoinCurves(segs, tol, false);
            if (joined != null && joined.Length > 0)
            {
                Curve best = joined.OrderByDescending(c => c.GetLength()).First();
                if (!best.IsClosed) best.MakeClosed(tol);
                return best;
            }

            var pc = new PolyCurve();
            foreach (Curve c in segs) pc.AppendSegment(c);
            if (!pc.IsClosed) pc.MakeClosed(tol);
            return pc;
        }

        private static void AlignClosedPair(Curve c0, Curve c1)
        {
            if (c0 == null || c1 == null || !c0.IsClosed || !c1.IsClosed) return;
            if (c1.ClosestPoint(c0.PointAtStart, out double t)) c1.ChangeClosedCurveSeam(t);
            Vector3d t0 = c0.TangentAtStart;
            Vector3d t1 = c1.TangentAtStart;
            if (!t0.IsZero && !t1.IsZero && t0 * t1 < 0.0) c1.Reverse();
        }

        private static Brep CleanBrep(Brep brep, double tol)
        {
            if (brep == null) return null;
            try { brep.Faces.ShrinkFaces(); }              catch { }
            try { brep.Faces.SplitKinkyFaces(RhinoMath.ToRadians(0.5), true); } catch { }
            try { brep.MergeCoplanarFaces(tol); }          catch { }
            try { brep.Compact(); }                         catch { }
            return brep;
        }

        private static List<T> RotateList<T>(List<T> list, int shift)
        {
            int n = list.Count;
            var result = new List<T>(n);
            for (int i = 0; i < n; i++)
                result.Add(list[(shift + i) % n]);
            return result;
        }

        // ══════════════════════════════════════════════════════════════════
        // SURFACE PROBE ABSTRACTION
        // ══════════════════════════════════════════════════════════════════

        private abstract class SurfProbe
        {
            /// <summary>Pre-computed global flip flag set during preprocessing.</summary>
            public bool FlipNormal { get; set; }

            /// <summary>
            /// Closest point query.
            /// Returns UV normalized to [0,1] regardless of the surface domain.
            /// </summary>
            public abstract bool Closest(
                Point3d P,
                out double u, out double v,
                out Point3d C, out Vector3d N);

            /// <summary>
            /// Evaluate at normalized UV [0,1]. Used to keep slab columns matched
            /// between consecutive reference surfaces.
            /// </summary>
            public abstract bool EvaluateAt(
                double u, double v,
                out Point3d C, out Vector3d N);

            /// <summary>
            /// True if UV (normalized [0,1]) is inside the surface boundary.
            /// UV rectangle for Surface; trimmed boundary for BrepFace.
            /// </summary>
            public abstract bool IsInside(Point3d P, double u, double v, Point3d C);

            /// <summary>Surface centroid for stacking axis computation.</summary>
            public abstract Point3d GetCentroid();

            /// <summary>Average surface normal for auto-orientation.</summary>
            public abstract Vector3d GetAverageNormal();

            /// <summary>U-axis direction at surface centre for alignment check.</summary>
            public abstract Vector3d GetUAxis();

            /// <summary>Bounding box for voxel grid sizing.</summary>
            public abstract BoundingBox GetBoundingBox();

            /// <summary>Surface as a capped Brep for bound_geo.</summary>
            public abstract Brep AsCapBrep();

            /// <summary>Boundary loops ordered outer-first for lofting.</summary>
            public abstract List<Curve> BoundaryLoops();

            public static SurfProbe Wrap(GeometryBase geo)
            {
                if (geo is Surface srf)
                    return new SurfProbe_Surface(srf);
                if (geo is Brep brep && brep.Faces.Count == 1)
                    return new SurfProbe_Face(brep.Faces[0]);
                return null;
            }

            protected static double NormU(double u, Interval dom)
                => dom.Length > EPS ? (u - dom.T0) / dom.Length : 0.5;

            protected static double DenormU(double u01, Interval dom)
                => dom.T0 + u01 * dom.Length;

            // ── Closest-point seed cache ──────────────────────────────────
            // Closest() is the hot path for analytical field evaluation (Ghost Field
            // preview, marching cubes): it is called once or twice per sample point,
            // per surface pair. A full ClosestPoint search re-derives the answer from
            // scratch every call. Grid/scan-line samples are spatially coherent, so the
            // previous call's converged UV is normally an excellent seed for a much
            // cheaper local Newton search. Cached per calling thread (Parallel.For
            // workers call Closest concurrently on the same SurfProbe instance) and
            // periodically re-anchored with a full search to bound any drift from a
            // stale/wrong seed (e.g. after a large jump between samples).
            protected const int SeedReanchorInterval = 64;

            protected struct ClosestSeed
            {
                public bool HasSeed;
                public double U;
                public double V;
                public int Age;
            }
        }

        // ── Untrimmed Surface ─────────────────────────────────────────────

        private sealed class SurfProbe_Surface : SurfProbe
        {
            private readonly Surface _srf;
            private readonly Interval _uDom;
            private readonly Interval _vDom;
            private readonly ThreadLocal<ClosestSeed> _seedCache =
                new ThreadLocal<ClosestSeed>(() => default);

            public SurfProbe_Surface(Surface srf)
            {
                _srf  = srf;
                _uDom = srf.Domain(0);
                _vDom = srf.Domain(1);
            }

            public override bool Closest(
                Point3d P,
                out double u, out double v,
                out Point3d C, out Vector3d N)
            {
                u = v = 0.0; C = Point3d.Unset; N = Vector3d.Unset;

                ClosestSeed seed = _seedCache.Value;
                double ru, rv;
                if (seed.HasSeed && seed.Age < SeedReanchorInterval &&
                    _srf.LocalClosestPoint(P, seed.U, seed.V, out ru, out rv))
                {
                    seed.Age++;
                }
                else if (_srf.ClosestPoint(P, out ru, out rv))
                {
                    seed.Age = 0;
                }
                else
                {
                    _seedCache.Value = default;
                    return false;
                }

                seed.HasSeed = true;
                seed.U = ru;
                seed.V = rv;
                _seedCache.Value = seed;

                C = _srf.PointAt(ru, rv);
                N = _srf.NormalAt(ru, rv);
                if (!N.Unitize()) return false;
                u = NormU(ru, _uDom);
                v = NormU(rv, _vDom);
                return true;
            }

            public override bool EvaluateAt(
                double u, double v,
                out Point3d C, out Vector3d N)
            {
                double ru = DenormU(Clamp01(u), _uDom);
                double rv = DenormU(Clamp01(v), _vDom);
                C = _srf.PointAt(ru, rv);
                N = _srf.NormalAt(ru, rv);
                return N.Unitize();
            }

            public override bool IsInside(Point3d P, double u, double v, Point3d C)
            {
                const double eps = 1e-6;
                double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;

                if (u < -eps || u > 1.0 + eps ||
                    v < -eps || v > 1.0 + eps)
                    return false;

                double ru = DenormU(u, _uDom);
                double rv = DenormU(v, _vDom);

                if (!_srf.Evaluate(ru, rv, 1, out Point3d _, out Vector3d[] ders) ||
                    ders == null || ders.Length < 2)
                {
                    return u >= -eps && u <= 1.0 + eps &&
                           v >= -eps && v <= 1.0 + eps;
                }

                Vector3d du = ders[0];
                Vector3d dv = ders[1];
                if (!du.Unitize() || !dv.Unitize())
                    return true;

                double edgeTol = Math.Max(tol * 2.0, 1e-6);
                Vector3d tangentOffset = P - C;

                if (u <= eps          && tangentOffset * du < -edgeTol) return false;
                if (u >= 1.0 - eps    && tangentOffset * du >  edgeTol) return false;
                if (v <= eps          && tangentOffset * dv < -edgeTol) return false;
                if (v >= 1.0 - eps    && tangentOffset * dv >  edgeTol) return false;

                return true;
            }

            public override Point3d GetCentroid()
                => _srf.PointAt(_uDom.Mid, _vDom.Mid);

            public override Vector3d GetAverageNormal()
            {
                var sum = Vector3d.Zero; int n = 0;
                for (int i = 0; i <= 2; i++)
                for (int j = 0; j <= 2; j++)
                {
                    Vector3d nm = _srf.NormalAt(
                        _uDom.T0 + _uDom.Length * i / 2.0,
                        _vDom.T0 + _vDom.Length * j / 2.0);
                    if (nm.Unitize()) { sum += nm; n++; }
                }
                if (n == 0) return Vector3d.ZAxis;
                sum.Unitize(); return sum;
            }

            public override Vector3d GetUAxis()
            {
                if (_srf.Evaluate(_uDom.Mid, _vDom.Mid, 1,
                    out Point3d _, out Vector3d[] ders)
                    && ders != null && ders.Length > 0)
                {
                    Vector3d du = ders[0];
                    if (du.Unitize()) return du;
                }
                return Vector3d.XAxis;
            }

            public override BoundingBox GetBoundingBox()
                => _srf.GetBoundingBox(false);

            public override Brep AsCapBrep()
                => _srf.ToBrep();

            public override List<Curve> BoundaryLoops()
            {
                double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;
                var edges = new Curve[]
                {
                    _srf.IsoCurve(0, _vDom.T0),
                    _srf.IsoCurve(0, _vDom.T1),
                    _srf.IsoCurve(1, _uDom.T0),
                    _srf.IsoCurve(1, _uDom.T1)
                };
                Curve[] joined = Curve.JoinCurves(edges, tol);
                var list = new List<Curve>();
                if (joined != null)
                    foreach (var c in joined)
                        if (c != null && c.IsClosed) list.Add(c);
                return list;
            }
        }

        // ── Trimmed BrepFace ──────────────────────────────────────────────

        private sealed class SurfProbe_Face : SurfProbe
        {
            private readonly BrepFace _face;
            private readonly Interval _uDom;
            private readonly Interval _vDom;
            private readonly ThreadLocal<ClosestSeed> _seedCache =
                new ThreadLocal<ClosestSeed>(() => default);

            public SurfProbe_Face(BrepFace face)
            {
                _face = face;
                _uDom = face.Domain(0);
                _vDom = face.Domain(1);
            }

            public override bool Closest(
                Point3d P,
                out double u, out double v,
                out Point3d C, out Vector3d N)
            {
                u = v = 0.0; C = Point3d.Unset; N = Vector3d.Unset;

                // LocalClosestPoint operates on the underlying surface and is not
                // trim-aware, unlike BrepFace.ClosestPoint's full search. The periodic
                // reanchor below (SeedReanchorInterval) falls back to the trim-aware
                // full search regularly, bounding how far a seeded result can drift
                // from the true trimmed-face closest point near trim edges.
                ClosestSeed seed = _seedCache.Value;
                double ru, rv;
                if (seed.HasSeed && seed.Age < SeedReanchorInterval &&
                    _face.LocalClosestPoint(P, seed.U, seed.V, out ru, out rv))
                {
                    seed.Age++;
                }
                else if (_face.ClosestPoint(P, out ru, out rv))
                {
                    seed.Age = 0;
                }
                else
                {
                    _seedCache.Value = default;
                    return false;
                }

                seed.HasSeed = true;
                seed.U = ru;
                seed.V = rv;
                _seedCache.Value = seed;

                C = _face.PointAt(ru, rv);
                N = _face.NormalAt(ru, rv);
                if (!N.Unitize()) return false;
                u = NormU(ru, _uDom);
                v = NormU(rv, _vDom);
                return true;
            }

            public override bool EvaluateAt(
                double u, double v,
                out Point3d C, out Vector3d N)
            {
                double ru = DenormU(Clamp01(u), _uDom);
                double rv = DenormU(Clamp01(v), _vDom);
                C = _face.PointAt(ru, rv);
                N = _face.NormalAt(ru, rv);
                return N.Unitize();
            }

            public override bool IsInside(Point3d P, double u, double v, Point3d C)
            {
                // De-normalize back to face domain for IsPointOnFace
                double ru = DenormU(u, _uDom);
                double rv = DenormU(v, _vDom);
                PointFaceRelation relation = _face.IsPointOnFace(ru, rv);
                return relation == PointFaceRelation.Interior ||
                       relation == PointFaceRelation.Boundary;
            }

            public override Point3d GetCentroid()
                => _face.PointAt(_uDom.Mid, _vDom.Mid);

            public override Vector3d GetAverageNormal()
            {
                var sum = Vector3d.Zero; int n = 0;
                for (int i = 0; i <= 2; i++)
                for (int j = 0; j <= 2; j++)
                {
                    Vector3d nm = _face.NormalAt(
                        _uDom.T0 + _uDom.Length * i / 2.0,
                        _vDom.T0 + _vDom.Length * j / 2.0);
                    if (nm.Unitize()) { sum += nm; n++; }
                }
                if (n == 0) return Vector3d.ZAxis;
                sum.Unitize(); return sum;
            }

            public override Vector3d GetUAxis()
            {
                if (_face.Evaluate(_uDom.Mid, _vDom.Mid, 1,
                    out Point3d _, out Vector3d[] ders)
                    && ders != null && ders.Length > 0)
                {
                    Vector3d du = ders[0];
                    if (du.Unitize()) return du;
                }
                return Vector3d.XAxis;
            }

            public override BoundingBox GetBoundingBox()
                => _face.GetBoundingBox(false);

            public override Brep AsCapBrep()
                => _face.DuplicateFace(true);

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
                        if (trim.Edge != null)
                            seg = trim.Edge.DuplicateCurve();
                        else
                        {
                            switch (trim.IsoStatus)
                            {
                                case IsoStatus.North: seg = _face.IsoCurve(0, _vDom.T1); break;
                                case IsoStatus.South: seg = _face.IsoCurve(0, _vDom.T0); break;
                                case IsoStatus.East:  seg = _face.IsoCurve(1, _uDom.T1); break;
                                case IsoStatus.West:  seg = _face.IsoCurve(1, _uDom.T0); break;
                            }
                        }
                        if (seg != null) segs.Add(seg);
                    }
                    if (segs.Count > 0)
                    {
                        Curve[] joined = Curve.JoinCurves(segs, tol);
                        if (joined != null)
                            foreach (var c in joined)
                                if (c != null && c.IsClosed) loops.Add(c);
                    }
                }

                return loops
                    .OrderByDescending(c => Math.Abs(
                        AreaMassProperties.Compute(c)?.Area ?? 0.0))
                    .ToList();
            }
        }

        [Flags]
        private enum BoundaryFace
        {
            None = 0,
            U0 = 1,
            U1 = 2,
            V0 = 4,
            V1 = 8,
            T0 = 16,
            T1 = 32,
            All = U0 | U1 | V0 | V1 | T0 | T1
        }

        // ══════════════════════════════════════════════════════════════════
        // MARCHING CUBES  (table-free cube edge crossing polygonization)
        // ══════════════════════════════════════════════════════════════════

        private static Mesh BuildContinuousCurvilinearPatternMesh(
            SurfProbe[] probes,
            CapDomainMap[] capMaps,
            UvPairMap[] pairMaps,
            global::WASPer_3DP.WasperVolumetricPatternDescriptor[] slabPatterns,
            double thickness,
            bool transpose,
            bool flip,
            double resolution,
            double shellThickness,
            bool shellCaps,
            double partitionThickness,
            TrimVolume trimVolume,
            int parallelThreads,
            out List<Mesh> domainMeshes,
            out long totalSamples,
            out string gridReport,
            out long evalMs,
            out long extractMs)
        {
            domainMeshes = new List<Mesh>();
            totalSamples = 0;
            evalMs = 0;
            extractMs = 0;
            gridReport = "none";

            if (probes == null || probes.Length < 2)
                return null;

            int surfaceCount = probes.Length;
            int slabCount = surfaceCount - 1;
            var slabDepths = new double[slabCount];
            double maxLenU = 0.0;
            double maxLenV = 0.0;
            double totalDepth = 0.0;

            for (int slab = 0; slab < slabCount; slab++)
            {
                EstimateSlabSize(
                    probes,
                    capMaps,
                    pairMaps,
                    slab,
                    out double lenU,
                    out double lenV,
                    out double depth);

                maxLenU = Math.Max(maxLenU, lenU);
                maxLenV = Math.Max(maxLenV, lenV);
                slabDepths[slab] = Math.Max(depth, resolution);
                totalDepth += slabDepths[slab];
            }

            int nx = GridCount(maxLenU, resolution);
            int ny = GridCount(maxLenV, resolution);
            var slabSteps = new int[slabCount];
            for (int slab = 0; slab < slabCount; slab++)
                slabSteps[slab] = Math.Max(1, GridCount(slabDepths[slab], resolution) - 1);

            int evaluatedSlices = slabSteps.Sum(steps => steps + 1);
            totalSamples = (long)nx * ny * evaluatedSlices;
            gridReport = $"{nx}x{ny}x{evaluatedSlices} pair-local ({slabCount} domains; {surfaceCount} shared anchors)";
            if (totalSamples > 20_000_000)
                return null;

            int uvCount = nx * ny;
            var anchorPoints = new Point3d[surfaceCount * uvCount];
            var anchorValid = new bool[surfaceCount * uvCount];
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, parallelThreads)
            };

            var evalWatch = Stopwatch.StartNew();

            // Sample every anchor surface once in a UV coordinate system carried
            // cumulatively from surface 0. Adjacent slabs therefore share the
            // exact same point array at their common surface.
            Parallel.For(0, surfaceCount * uvCount, parallelOptions, anchorIndex =>
            {
                int surfaceIndex = anchorIndex / uvCount;
                int uvIndex = anchorIndex - surfaceIndex * uvCount;
                int iy = uvIndex / nx;
                int ix = uvIndex - iy * nx;
                double u = nx <= 1 ? 0.0 : ix / (double)(nx - 1);
                double v = ny <= 1 ? 0.0 : iy / (double)(ny - 1);

                MapReferenceUvToSurface(pairMaps, surfaceIndex, u, v, out double su, out double sv);
                if (EvaluateMappedCap(
                    probes[surfaceIndex],
                    capMaps,
                    surfaceIndex,
                    su,
                    sv,
                    out Point3d point,
                    out Vector3d _))
                {
                    anchorPoints[anchorIndex] = point;
                    anchorValid[anchorIndex] = point.IsValid;
                }
                else
                {
                    anchorPoints[anchorIndex] = Point3d.Unset;
                    anchorValid[anchorIndex] = false;
                }
            });

            shellThickness = Math.Max(0.0, shellThickness);
            partitionThickness = Math.Max(0.0, partitionThickness);
            bool hasShell = shellThickness > EPS;
            bool hasPartitions = partitionThickness > EPS && slabCount > 1;
            BoundaryFace shellFaceMask = BoundaryFace.All;
            if (hasShell && shellCaps)
                shellFaceMask = BuildContinuousShellFaceMask(
                    anchorPoints,
                    anchorValid,
                    surfaceCount,
                    nx,
                    ny);
            evalWatch.Stop();
            evalMs = evalWatch.ElapsedMilliseconds;

            // Each consecutive pair receives its own scalar grid. Geometry
            // anchors remain shared, but the common surface can now be t=1 for
            // one pair and t=0 for the next, which is essential for fractional
            // c_z and p_z values.
            for (int slab = 0; slab < slabCount; slab++)
            {
                global::WASPer_3DP.WasperVolumetricPatternDescriptor domainPattern =
                    slabPatterns != null && slab < slabPatterns.Length
                        ? slabPatterns[slab]
                        : throw new InvalidOperationException("Missing infill parameters for a surface domain.");
                bool slabInvert = domainPattern.Invert;
                bool clipPatternToPair = domainPattern.RequiresBoundaryClip(thickness);

                int slabNz = slabSteps[slab] + 1;
                int slabValueCount = uvCount * slabNz;
                var slabScalars = new double[slabValueCount];
                var slabPoints = new Point3d[slabValueCount];
                double slabDepth = slabDepths[slab];
                double patternGradientScale =
                    domainPattern.ApproximateTpmsGradientScale(
                        maxLenU,
                        maxLenV,
                        slabDepth);
                var slabEvalWatch = Stopwatch.StartNew();
                Parallel.For(0, slabNz, parallelOptions, localIz =>
                {
                    double t = localIz / (double)(slabNz - 1);
                    double w = (slab + t) / slabCount;
                    double fieldT = flip ? 1.0 - t : t;

                    for (int iy = 0; iy < ny; iy++)
                    {
                        double v = ny <= 1 ? 0.0 : iy / (double)(ny - 1);
                        for (int ix = 0; ix < nx; ix++)
                        {
                            double u = nx <= 1 ? 0.0 : ix / (double)(nx - 1);
                            int uvIndex = ix + iy * nx;
                            int anchorA = slab * uvCount + uvIndex;
                            int anchorB = (slab + 1) * uvCount + uvIndex;
                            int index = Idx(ix, iy, localIz, nx, ny);

                            if (!anchorValid[anchorA] || !anchorValid[anchorB])
                            {
                                slabPoints[index] = Point3d.Unset;
                                slabScalars[index] = TRIM;
                                continue;
                            }

                            Point3d point = anchorPoints[anchorA] +
                                t * (anchorPoints[anchorB] - anchorPoints[anchorA]);
                            slabPoints[index] = point;

                            double fu = u;
                            double fv = v;
                            if (transpose)
                            {
                                double swap = fu;
                                fu = fv;
                                fv = swap;
                            }

                            double value = domainPattern.EvaluateNormalized(
                                fu,
                                fv,
                                fieldT,
                                maxLenU,
                                maxLenV,
                                slabDepth,
                                thickness,
                                !hasShell,
                                patternGradientScale);

                            double pairBoundarySdf = ParametricBoundarySdf(
                                u, v, t, maxLenU, maxLenV, slabDepth);
                            double stackBoundarySdf = ParametricBoundarySdf(
                                u, v, w, maxLenU, maxLenV, totalDepth);
                            if (clipPatternToPair)
                                value = Math.Max(value, pairBoundarySdf);

                            double finalValue = value;
                            if (hasShell)
                            {
                                double shellBoundarySdf = shellCaps
                                    ? ShellBoundarySdfFromMask(
                                        u, v, w, maxLenU, maxLenV, totalDepth, shellFaceMask)
                                    : stackBoundarySdf;
                                double shellValue = InwardShellSdf(shellBoundarySdf, shellThickness);
                                shellValue = Math.Max(shellValue, stackBoundarySdf);
                                finalValue = Math.Min(finalValue, shellValue);

                                if (slabInvert)
                                {
                                    finalValue = -finalValue;
                                    finalValue = Math.Max(finalValue, stackBoundarySdf);
                                }
                            }

                            if (hasPartitions)
                            {
                                // A partition is the two-sided offset band of an
                                // internal guide surface. Measure in model space
                                // from the actual sampled guide point, rather than
                                // scaling local t by an average slab depth.
                                double partitionSdf = TRIM;
                                if (slab > 0)
                                    partitionSdf = Math.Min(
                                        partitionSdf,
                                        point.DistanceTo(anchorPoints[anchorA])
                                            - partitionThickness * 0.5);
                                if (slab < slabCount - 1)
                                    partitionSdf = Math.Min(
                                        partitionSdf,
                                        point.DistanceTo(anchorPoints[anchorB])
                                            - partitionThickness * 0.5);

                                // Clip only at the side perimeter. Do not clip at
                                // t=0/1: the negative field reaches the shared
                                // guide, so the two part_t/2 halves meet without
                                // an artificial zero-level gap.
                                double lateralBoundarySdf = ParametricLateralBoundarySdf(
                                    u, v, maxLenU, maxLenV);
                                partitionSdf = Math.Max(partitionSdf, lateralBoundarySdf);
                                finalValue = Math.Min(finalValue, partitionSdf);
                            }

                            if (trimVolume != null)
                                finalValue = Math.Max(
                                    finalValue,
                                    trimVolume.SignedDistance(point));
                            slabScalars[index] = finalValue;
                        }
                    }
                });
                slabEvalWatch.Stop();
                evalMs += slabEvalWatch.ElapsedMilliseconds;

                var slabExtractWatch = Stopwatch.StartNew();
                Mesh domainMesh = WasperMarchingCubes.Extract(
                    slabScalars,
                    slabPoints,
                    nx,
                    ny,
                    slabNz,
                    0.0,
                    Math.Max(1e-9, resolution * 1e-6),
                    Math.Max(1, parallelThreads),
                    true,
                    TRIM * 0.1);
                slabExtractWatch.Stop();
                extractMs += slabExtractWatch.ElapsedMilliseconds;
                // Preserve one slot per surface-pair domain so per-domain
                // disjoin settings stay aligned even when a pattern is empty.
                domainMeshes.Add(
                    domainMesh != null && domainMesh.Faces.Count > 0
                        ? domainMesh
                        : null);
            }

            Mesh joined = JoinMeshList(domainMeshes);
            if (hasPartitions && joined != null && joined.Faces.Count > 0)
            {
                int removedSeamFaces = RemoveCoincidentFacePairsInPlace(joined);
                CleanMeshFromSampledGrid(joined);
                domainMeshes = new List<Mesh> { joined };
                gridReport += $" | partition seam faces removed={removedSeamFaces}";
            }
            return joined;
        }

        private static int RemoveCoincidentFacePairsInPlace(Mesh mesh)
        {
            if (mesh == null || mesh.Faces.Count == 0)
                return 0;

            // Appended pair meshes have separate vertex indices even where their
            // internal partition caps coincide. Merge identical positions first,
            // then remove both copies of every face with the same unordered vertex
            // set. Opposite cap faces disappear and the remaining shells can weld
            // into one continuous partition volume.
            mesh.Vertices.CombineIdentical(true, true);
            var firstFaceByKey = new Dictionary<string, int>();
            var duplicateFaces = new HashSet<int>();

            for (int i = 0; i < mesh.Faces.Count; i++)
            {
                MeshFace face = mesh.Faces[i];
                int[] vertices = face.IsQuad
                    ? new[] { face.A, face.B, face.C, face.D }
                    : new[] { face.A, face.B, face.C };
                Array.Sort(vertices);
                string key = string.Join(",", vertices);

                if (firstFaceByKey.TryGetValue(key, out int first))
                {
                    duplicateFaces.Add(first);
                    duplicateFaces.Add(i);
                }
                else
                {
                    firstFaceByKey[key] = i;
                }
            }

            if (duplicateFaces.Count == 0)
                return 0;

            mesh.Faces.DeleteFaces(duplicateFaces.OrderBy(i => i).ToArray());
            mesh.Vertices.CullUnused();
            mesh.Compact();
            return duplicateFaces.Count;
        }

        private static BoundaryFace BuildContinuousShellFaceMask(
            Point3d[] anchors,
            bool[] valid,
            int surfaceCount,
            int nx,
            int ny)
        {
            BoundaryFace[] faces =
            {
                BoundaryFace.U0,
                BoundaryFace.U1,
                BoundaryFace.V0,
                BoundaryFace.V1,
                BoundaryFace.T0,
                BoundaryFace.T1
            };
            var sumZ = new double[faces.Length];
            var counts = new int[faces.Length];
            int uvCount = nx * ny;

            for (int surface = 0; surface < surfaceCount; surface++)
            {
                for (int iy = 0; iy < ny; iy++)
                {
                    for (int ix = 0; ix < nx; ix++)
                    {
                        int index = surface * uvCount + ix + iy * nx;
                        if (index < 0 || index >= anchors.Length ||
                            index >= valid.Length || !valid[index] ||
                            !anchors[index].IsValid)
                            continue;

                        double z = anchors[index].Z;
                        if (ix == 0) Add(0, z);
                        if (ix == nx - 1) Add(1, z);
                        if (iy == 0) Add(2, z);
                        if (iy == ny - 1) Add(3, z);
                        if (surface == 0) Add(4, z);
                        if (surface == surfaceCount - 1) Add(5, z);
                    }
                }
            }

            int low = -1;
            int high = -1;
            double lowZ = double.PositiveInfinity;
            double highZ = double.NegativeInfinity;
            for (int i = 0; i < faces.Length; i++)
            {
                if (counts[i] == 0) continue;
                double average = sumZ[i] / counts[i];
                if (average < lowZ)
                {
                    lowZ = average;
                    low = i;
                }
                if (average > highZ)
                {
                    highZ = average;
                    high = i;
                }
            }

            BoundaryFace mask = BoundaryFace.All;
            if (low >= 0) mask &= ~faces[low];
            if (high >= 0 && high != low) mask &= ~faces[high];
            return mask;

            void Add(int faceIndex, double z)
            {
                sumZ[faceIndex] += z;
                counts[faceIndex]++;
            }
        }

        private static void MapReferenceUvToSurface(
            UvPairMap[] pairMaps,
            int surfaceIndex,
            double u,
            double v,
            out double mappedU,
            out double mappedV)
        {
            mappedU = Clamp01(u);
            mappedV = Clamp01(v);
            for (int pair = 0; pair < surfaceIndex; pair++)
            {
                UvPairMap map = GetPairMap(pairMaps, pair);
                map.MapToB(mappedU, mappedV, out double nextU, out double nextV);
                mappedU = nextU;
                mappedV = nextV;
            }
        }

        private static Mesh BuildCurvilinearTpmsMesh(
            SurfProbe[] probes,
            CapDomainMap[] capMaps,
            UvPairMap[] pairMaps,
            int type,
            double level,
            double thickness,
            bool invertField,
            bool transpose,
            bool flip,
            double countU,
            double countV,
            double countN,
            double phaseU,
            double phaseV,
            double phaseN,
            double resolution,
            bool closeTpms,
            bool useBoundarySdf,
            double shellThickness,
            bool shellCaps,
            TrimVolume trimVolume,
            int parallelThreads,
            out long totalSamples,
            out string gridReport,
            out long evalMs,
            out long extractMs)
        {
            totalSamples = 0;
            evalMs = 0;
            extractMs = 0;
            var gridTags = new List<string>();

            if (probes == null || probes.Length < 2)
            {
                gridReport = "none";
                return null;
            }

            var result = new Mesh();
            shellThickness = Math.Max(0.0, shellThickness);
            bool hasShell = shellThickness > EPS;
            bool applyBoundary = useBoundarySdf || closeTpms || thickness > EPS || hasShell;
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, parallelThreads)
            };

            for (int slab = 0; slab < probes.Length - 1; slab++)
            {
                EstimateSlabSize(probes, capMaps, pairMaps, slab,
                    out double lenU, out double lenV, out double depth);

                int nx = GridCount(lenU, resolution);
                int ny = GridCount(lenV, resolution);
                int nz = GridCount(depth, resolution);

                long slabSamples = (long)nx * ny * nz;
                totalSamples += slabSamples;
                gridTags.Add($"{nx}x{ny}x{nz}");
                if (totalSamples > 20_000_000)
                {
                    gridReport = string.Join(" + ", gridTags);
                    return null;
                }

                var scalars = new double[slabSamples];
                var points = new Point3d[slabSamples];
                var capA = new Point3d[nx * ny];
                var capB = new Point3d[nx * ny];
                var uvValid = new bool[nx * ny];
                double gradScale = global::WASPer_3DP.WasperTpmsPatternMath.ApproxGradientScale(
                    countU, countV, countN, lenU, lenV, depth);

                BoundaryFace shellFaceMask = BoundaryFace.All;

                if (hasShell && shellCaps)
                    shellFaceMask = BuildShellFaceMaskForSlab(probes, capMaps, pairMaps, slab);

                var evalWatch = Stopwatch.StartNew();
                Parallel.For(0, ny, parallelOptions, iy =>
                {
                    double v = ny <= 1 ? 0.0 : (double)iy / (double)(ny - 1);
                    for (int ix = 0; ix < nx; ix++)
                    {
                        double u = nx <= 1 ? 0.0 : (double)ix / (double)(nx - 1);
                        int uvIdx = iy * nx + ix;

                        if (EvaluateSlabCaps(probes, capMaps, pairMaps, slab, u, v, out Point3d pA, out Point3d pB))
                        {
                            capA[uvIdx] = pA;
                            capB[uvIdx] = pB;
                            uvValid[uvIdx] = true;
                        }
                    }
                });

                Parallel.For(0, nz, parallelOptions, iz =>
                {
                    double t = nz <= 1 ? 0.0 : (double)iz / (double)(nz - 1);
                    double ft = flip ? 1.0 - t : t;
                    double zPhase = TWO_PI * (countN * ft + phaseN);
                    for (int iy = 0; iy < ny; iy++)
                    {
                        double v = ny <= 1 ? 0.0 : (double)iy / (double)(ny - 1);
                        for (int ix = 0; ix < nx; ix++)
                        {
                            double u = nx <= 1 ? 0.0 : (double)ix / (double)(nx - 1);
                            int idx = Idx(ix, iy, iz, nx, ny);
                            int uvIdx = iy * nx + ix;

                            if (uvValid[uvIdx])
                            {
                                Point3d pA = capA[uvIdx];
                                Point3d pB = capB[uvIdx];
                                Point3d p = pA + t * (pB - pA);
                                points[idx] = p;

                                double fu = u;
                                double fv = v;
                                if (transpose) { double tmp = fu; fu = fv; fv = tmp; }

                                double f = global::WASPer_3DP.WasperTpmsPatternMath.Value(
                                    type,
                                    TWO_PI * (countU * fu + phaseU),
                                    TWO_PI * (countV * fv + phaseV),
                                    zPhase) - level;

                                double value = f;
                                if (thickness > EPS)
                                {
                                    value = Math.Abs(f / gradScale) - thickness * 0.5;
                                }

                                // When no shell: invert the TPMS field directly.
                                // When shell is present: invert is applied after shell
                                // combination so that invert=True yields the void/holes
                                // of the infill+shell solid rather than the inverted
                                // TPMS field unioned with the original shell.
                                if (invertField && !hasShell) value = -value;

                                double boundarySdf = ParametricBoundarySdf(u, v, t, lenU, lenV, depth);

                                // Pass 1 – domain boundary clip (all 6 faces).
                                // Always runs when applyBoundary is true. Handles close_tpms
                                // capping and keeps the TPMS inside the slab volume at T0/T1.
                                if (applyBoundary)
                                    value = Math.Max(value, boundarySdf);

                                // Pass 2 – shell inset clip (DEACTIVATED – work in progress).
                                // Intent: shrink the infill domain by shellThickness so infill
                                // and shell don't overlap.
                                //   caps=True  → lateral faces only (T faces not inset)
                                //   caps=False → all faces inset
                                // Currently disabled until the face-selective offset produces
                                // the correct visual result across all surface configurations.
                                //
                                // if (hasShell)
                                // {
                                //     double shellInsetSdf = shellCaps
                                //         ? ParametricLateralBoundarySdf(u, v, lenU, lenV) + shellThickness
                                //         : boundarySdf + shellThickness;
                                //     value = Math.Max(value, shellInsetSdf);
                                // }

                                double finalValue = value;

                                if (hasShell)
                                {
                                    double shellBoundarySdf = shellCaps
                                        ? ShellBoundarySdfFromMask(u, v, t, lenU, lenV, depth, shellFaceMask)
                                        : boundarySdf;

                                    if (shellBoundarySdf < TRIM * 0.1)
                                    {
                                        double shellValue = InwardShellSdf(shellBoundarySdf, shellThickness);

                                        // Keep the shell clipped to the full slab volume.
                                        // This creates a narrow sealing rim at removed cap cuts,
                                        // but does not create a full top/bottom cap.
                                        shellValue = Math.Max(shellValue, boundarySdf);

                                        finalValue = Math.Min(finalValue, shellValue);
                                    }

                                    // invert=True with a shell: negate the combined solid so
                                    // the output is the void/holes rather than the solid.
                                    // After negation, re-clip to the slab domain so the void
                                    // mesh is bounded at the cap surfaces instead of being open.
                                    // (The pre-invert Max(value, boundarySdf) becomes a Min after
                                    // negation and lets the inverted field leak outside the domain;
                                    // the second Max restores the correct domain boundary.)
                                    if (invertField)
                                    {
                                        finalValue = -finalValue;
                                        finalValue = Math.Max(finalValue, boundarySdf);
                                    }
                                }

                                if (trimVolume != null)
                                    finalValue = Math.Max(finalValue, trimVolume.SignedDistance(p));

                                scalars[idx] = finalValue;
                            }
                            else
                            {
                                points[idx] = Point3d.Unset;
                                scalars[idx] = TRIM;
                            }
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

        private static int GridCount(double length, double resolution)
        {
            if (resolution <= EPS) resolution = 1.0;
            return Clamp((int)Math.Ceiling(Math.Max(length, resolution) / resolution) + 1, 2, 1200);
        }

        private static bool EvaluateCurvilinearNode(
            SurfProbe[] probes,
            CapDomainMap[] capMaps,
            UvPairMap[] pairMaps,
            int slab,
            double u,
            double v,
            double t,
            int type,
            double level,
            bool transpose,
            bool flip,
            double countU,
            double countV,
            double countN,
            double phaseU,
            double phaseV,
            double phaseN,
            double lenU,
            double lenV,
            double depth,
            out Point3d point,
            out double field,
            out double boundarySdf)
        {
            point = Point3d.Unset;
            field = TRIM;
            boundarySdf = TRIM;

            if (probes == null || slab < 0 || slab >= probes.Length - 1)
                return false;

            UvPairMap map = GetPairMap(pairMaps, slab);
            map.MapToB(u, v, out double bU, out double bV);

            if (!EvaluateMappedCap(probes[slab], capMaps, slab, u, v, out Point3d pA, out Vector3d _))
                return false;
            if (!EvaluateMappedCap(probes[slab + 1], capMaps, slab + 1, bU, bV, out Point3d pB, out Vector3d _))
                return false;

            point = pA + t * (pB - pA);

            double fu = u;
            double fv = v;
            if (transpose) { double tmp = fu; fu = fv; fv = tmp; }
            double ft = flip ? 1.0 - t : t;

            field = global::WASPer_3DP.WasperTpmsPatternMath.Value(
                type,
                TWO_PI * (countU * fu + phaseU),
                TWO_PI * (countV * fv + phaseV),
                TWO_PI * (countN * ft + phaseN)) - level;

            boundarySdf = ParametricBoundarySdf(u, v, t, lenU, lenV, depth);
            return true;
        }

        private static BoundaryFace BuildShellFaceMaskForSlab(
            SurfProbe[] probes,
            CapDomainMap[] capMaps,
            UvPairMap[] pairMaps,
            int slab)
        {
            BoundaryFace[] faces =
            {
        BoundaryFace.U0,
        BoundaryFace.U1,
        BoundaryFace.V0,
        BoundaryFace.V1,
        BoundaryFace.T0,
        BoundaryFace.T1
    };

            double[] avgZ = new double[faces.Length];

            for (int i = 0; i < faces.Length; i++)
                avgZ[i] = SampleBoundaryFaceAverageZ(probes, capMaps, pairMaps, slab, faces[i]);

            int lowIndex = 0;
            int highIndex = 0;

            for (int i = 1; i < avgZ.Length; i++)
            {
                if (avgZ[i] < avgZ[lowIndex])
                    lowIndex = i;

                if (avgZ[i] > avgZ[highIndex])
                    highIndex = i;
            }

            BoundaryFace mask = BoundaryFace.All;

            mask &= ~faces[lowIndex];

            if (highIndex != lowIndex)
                mask &= ~faces[highIndex];

            return mask;
        }

        private static double SampleBoundaryFaceAverageZ(
            SurfProbe[] probes,
            CapDomainMap[] capMaps,
            UvPairMap[] pairMaps,
            int slab,
            BoundaryFace face)
        {
            double[] samples = { 0.0, 0.5, 1.0 };

            double sum = 0.0;
            int count = 0;

            foreach (double a in samples)
                foreach (double b in samples)
                {
                    double u = 0.5;
                    double v = 0.5;
                    double t = 0.5;

                    switch (face)
                    {
                        case BoundaryFace.U0:
                            u = 0.0;
                            v = a;
                            t = b;
                            break;

                        case BoundaryFace.U1:
                            u = 1.0;
                            v = a;
                            t = b;
                            break;

                        case BoundaryFace.V0:
                            u = a;
                            v = 0.0;
                            t = b;
                            break;

                        case BoundaryFace.V1:
                            u = a;
                            v = 1.0;
                            t = b;
                            break;

                        case BoundaryFace.T0:
                            u = a;
                            v = b;
                            t = 0.0;
                            break;

                        case BoundaryFace.T1:
                            u = a;
                            v = b;
                            t = 1.0;
                            break;
                    }

                    if (!EvaluateSlabCaps(
                        probes,
                        capMaps,
                        pairMaps,
                        slab,
                        u,
                        v,
                        out Point3d pA,
                        out Point3d pB))
                    {
                        continue;
                    }

                    Point3d p = pA + t * (pB - pA);

                    if (!p.IsValid)
                        continue;

                    sum += p.Z;
                    count++;
                }

            return count > 0 ? sum / count : 0.0;
        }

        private static double ShellBoundarySdfFromMask(
            double u,
            double v,
            double t,
            double lenU,
            double lenV,
            double depth,
            BoundaryFace mask)
        {
            lenU = Math.Max(lenU, EPS);
            lenV = Math.Max(lenV, EPS);
            depth = Math.Max(depth, EPS);

            double best = double.MaxValue;

            if ((mask & BoundaryFace.U0) != 0)
                best = Math.Min(best, Clamp01(u) * lenU);

            if ((mask & BoundaryFace.U1) != 0)
                best = Math.Min(best, (1.0 - Clamp01(u)) * lenU);

            if ((mask & BoundaryFace.V0) != 0)
                best = Math.Min(best, Clamp01(v) * lenV);

            if ((mask & BoundaryFace.V1) != 0)
                best = Math.Min(best, (1.0 - Clamp01(v)) * lenV);

            if ((mask & BoundaryFace.T0) != 0)
                best = Math.Min(best, Clamp01(t) * depth);

            if ((mask & BoundaryFace.T1) != 0)
                best = Math.Min(best, (1.0 - Clamp01(t)) * depth);

            if (best == double.MaxValue)
                return TRIM;

            return -best;
        }
        private static double ParametricBoundarySdf(double u, double v, double t, double lenU, double lenV, double depth)
        {
            lenU = Math.Max(lenU, EPS);
            lenV = Math.Max(lenV, EPS);
            depth = Math.Max(depth, EPS);

            double du = Math.Min(Clamp01(u), 1.0 - Clamp01(u)) * lenU;
            double dv = Math.Min(Clamp01(v), 1.0 - Clamp01(v)) * lenV;
            double dt = Math.Min(Clamp01(t), 1.0 - Clamp01(t)) * depth;
            return -Math.Min(du, Math.Min(dv, dt));
        }

        private static double ParametricLateralBoundarySdf(double u, double v, double lenU, double lenV)
        {
            lenU = Math.Max(lenU, EPS);
            lenV = Math.Max(lenV, EPS);

            double du = Math.Min(Clamp01(u), 1.0 - Clamp01(u)) * lenU;
            double dv = Math.Min(Clamp01(v), 1.0 - Clamp01(v)) * lenV;
            return -Math.Min(du, dv);
        }

        private static double ParametricCapBoundarySdf(double t, double depth)
        {
            depth = Math.Max(depth, EPS);
            double dt = Math.Min(Clamp01(t), 1.0 - Clamp01(t)) * depth;
            return -dt;
        }

        private static double InwardShellSdf(double boundarySdf, double shellThickness)
        {
            shellThickness = Math.Max(shellThickness, EPS);
            return Math.Abs(boundarySdf + 0.5 * shellThickness) - 0.5 * shellThickness;
        }

        private static double EstimateCurvilinearGradient(
            double[] raw, Point3d[] points,
            int nx, int ny, int nz,
            int ix, int iy, int iz)
        {
            double gu = AxisGradient(raw, points, nx, ny, nz, Math.Max(0, ix - 1), iy, iz, Math.Min(nx - 1, ix + 1), iy, iz);
            double gv = AxisGradient(raw, points, nx, ny, nz, ix, Math.Max(0, iy - 1), iz, ix, Math.Min(ny - 1, iy + 1), iz);
            double gt = AxisGradient(raw, points, nx, ny, nz, ix, iy, Math.Max(0, iz - 1), ix, iy, Math.Min(nz - 1, iz + 1));
            double g = Math.Sqrt(gu * gu + gv * gv + gt * gt);
            return g > EPS ? g : EPS;
        }

        private static double AxisGradient(
            double[] raw, Point3d[] points,
            int nx, int ny, int nz,
            int ax, int ay, int az,
            int bx, int by, int bz)
        {
            int ia = Idx(ax, ay, az, nx, ny);
            int ib = Idx(bx, by, bz, nx, ny);
            if (ia == ib) return 0.0;
            if (raw[ia] > TRIM * 0.1 || raw[ib] > TRIM * 0.1) return 0.0;
            if (!points[ia].IsValid || !points[ib].IsValid) return 0.0;

            double d = points[ia].DistanceTo(points[ib]);
            if (d <= EPS) return 0.0;
            return (raw[ib] - raw[ia]) / d;
        }

        private static void EstimateSlabSize(
            SurfProbe[] probes,
            CapDomainMap[] capMaps,
            UvPairMap[] pairMaps,
            int slab,
            out double lenU,
            out double lenV,
            out double depth)
        {
            lenU = lenV = depth = 0.0;
            int countU = 0, countV = 0, countD = 0;
            double[] samples = { 0.0, 0.5, 1.0 };

            foreach (double v in samples)
            {
                lenU += MeasureSlabLine(probes, capMaps, pairMaps, slab, true, v, false);
                lenU += MeasureSlabLine(probes, capMaps, pairMaps, slab, true, v, true);
                countU += 2;
            }

            foreach (double u in samples)
            {
                lenV += MeasureSlabLine(probes, capMaps, pairMaps, slab, false, u, false);
                lenV += MeasureSlabLine(probes, capMaps, pairMaps, slab, false, u, true);
                countV += 2;
            }

            for (int iu = 0; iu <= 2; iu++)
            for (int iv = 0; iv <= 2; iv++)
            {
                if (EvaluateSlabCaps(probes, capMaps, pairMaps, slab, iu / 2.0, iv / 2.0, out Point3d pA, out Point3d pB))
                {
                    depth += pA.DistanceTo(pB);
                    countD++;
                }
            }

            lenU = countU > 0 ? lenU / countU : 1.0;
            lenV = countV > 0 ? lenV / countV : 1.0;
            depth = countD > 0 ? depth / countD : 1.0;
        }

        private static double MeasureSlabLine(
            SurfProbe[] probes,
            CapDomainMap[] capMaps,
            UvPairMap[] pairMaps,
            int slab,
            bool alongU,
            double fixedParam,
            bool useB)
        {
            const int steps = 24;
            double len = 0.0;
            Point3d prev = Point3d.Unset;
            bool havePrev = false;

            for (int i = 0; i <= steps; i++)
            {
                double s = i / (double)steps;
                double u = alongU ? s : fixedParam;
                double v = alongU ? fixedParam : s;

                if (!EvaluateSlabCaps(probes, capMaps, pairMaps, slab, u, v, out Point3d pA, out Point3d pB))
                    continue;

                Point3d p = useB ? pB : pA;
                if (havePrev) len += prev.DistanceTo(p);
                prev = p;
                havePrev = true;
            }

            return len;
        }

        private static bool EvaluateSlabCaps(
            SurfProbe[] probes,
            CapDomainMap[] capMaps,
            UvPairMap[] pairMaps,
            int slab,
            double u,
            double v,
            out Point3d pA,
            out Point3d pB)
        {
            pA = Point3d.Unset;
            pB = Point3d.Unset;
            if (probes == null || slab < 0 || slab >= probes.Length - 1)
                return false;

            UvPairMap map = GetPairMap(pairMaps, slab);
            map.MapToB(u, v, out double bU, out double bV);

            return EvaluateMappedCap(probes[slab], capMaps, slab, u, v, out pA, out Vector3d _) &&
                   EvaluateMappedCap(probes[slab + 1], capMaps, slab + 1, bU, bV, out pB, out Vector3d _);
        }

        private static Mesh MarchingCubesCurvilinear(
            double[] scalars,
            Point3d[] points,
            int nx, int ny, int nz)
        {
            var mesh = new Mesh();
            var vertexMap = new Dictionary<VertexKey, int>();
            double keyTol = 1e-8;

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
                bool hasTrim = false;

                for (int c = 0; c < 8; c++)
                {
                    int cx = ix + cubeCorners[c, 0];
                    int cy = iy + cubeCorners[c, 1];
                    int cz = iz + cubeCorners[c, 2];
                    int idx = Idx(cx, cy, cz, nx, ny);
                    sv[c] = scalars[idx];
                    cp[c] = points[idx];
                    if (sv[c] > TRIM * 0.1 || !cp[c].IsValid)
                        hasTrim = true;
                }

                if (hasTrim) continue;

                var crossings = new List<Point3d>(12);
                for (int e = 0; e < 12; e++)
                {
                    int a = edgeCorners[e, 0];
                    int b = edgeCorners[e, 1];
                    if ((sv[a] < 0.0) == (sv[b] < 0.0)) continue;

                    double d = sv[a] - sv[b];
                    double tt = Math.Abs(d) < 1e-14 ? 0.5 : sv[a] / d;
                    crossings.Add(cp[a] + Clamp01(tt) * (cp[b] - cp[a]));
                }

                Vector3d normal = EstimateWorldGradientFromCube(cp, sv);
                AddCubePolygon(mesh, vertexMap, crossings, normal, keyTol);
            }

            return mesh.Faces.Count == 0 ? null : mesh;
        }

        private static Vector3d EstimateWorldGradientFromCube(Point3d[] p, double[] s)
        {
            Vector3d g = Vector3d.Zero;
            AccumulateGradient(ref g, p[0], p[1], s[0], s[1]);
            AccumulateGradient(ref g, p[3], p[2], s[3], s[2]);
            AccumulateGradient(ref g, p[4], p[5], s[4], s[5]);
            AccumulateGradient(ref g, p[7], p[6], s[7], s[6]);

            AccumulateGradient(ref g, p[0], p[3], s[0], s[3]);
            AccumulateGradient(ref g, p[1], p[2], s[1], s[2]);
            AccumulateGradient(ref g, p[4], p[7], s[4], s[7]);
            AccumulateGradient(ref g, p[5], p[6], s[5], s[6]);

            AccumulateGradient(ref g, p[0], p[4], s[0], s[4]);
            AccumulateGradient(ref g, p[1], p[5], s[1], s[5]);
            AccumulateGradient(ref g, p[2], p[6], s[2], s[6]);
            AccumulateGradient(ref g, p[3], p[7], s[3], s[7]);

            return g;
        }

        private static void AccumulateGradient(ref Vector3d g, Point3d a, Point3d b, double fa, double fb)
        {
            if (!a.IsValid || !b.IsValid) return;
            Vector3d d = b - a;
            double len2 = d.SquareLength;
            if (len2 <= EPS) return;
            g += d * ((fb - fa) / len2);
        }

        private static Mesh MarchingCubes(
            double[] scalars, int nx, int ny, int nz,
            Point3d origin, double step, bool closeTpms)
        {
            var mesh      = new Mesh();
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
                double[]  sv = new double[8];
                Point3d[] cp = new Point3d[8];
                bool hasTrim = false;

                for (int c = 0; c < 8; c++)
                {
                    int cx = ix + cubeCorners[c, 0];
                    int cy = iy + cubeCorners[c, 1];
                    int cz = iz + cubeCorners[c, 2];
                    double raw = scalars[Idx(cx, cy, cz, nx, ny)];
                    if (raw > TRIM * 0.1)
                    {
                        hasTrim = true;
                        sv[c] = closeTpms ? TRIM_CAP : raw;
                    }
                    else
                    {
                        sv[c] = raw;
                    }
                    cp[c] = new Point3d(
                        origin.X + cx * step,
                        origin.Y + cy * step,
                        origin.Z + cz * step);
                }

                if (!closeTpms && hasTrim)
                    continue;

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

                AddCubePolygon(mesh, vertexMap, crossings, sv, keyTol);
            }

            return mesh.Faces.Count == 0 ? null : mesh;
        }

        private static void AddCubePolygon(
            Mesh mesh,
            Dictionary<VertexKey, int> vertexMap,
            List<Point3d> crossings,
            double[] sv,
            double keyTol)
        {
            AddCubePolygon(mesh, vertexMap, crossings, EstimateCubeGradient(sv), keyTol);
        }

        private static void AddCubePolygon(
            Mesh mesh,
            Dictionary<VertexKey, int> vertexMap,
            List<Point3d> crossings,
            Vector3d normal,
            double keyTol)
        {
            if (crossings == null || crossings.Count < 3) return;

            Point3d center = Point3d.Origin;
            for (int i = 0; i < crossings.Count; i++)
                center += (Vector3d)crossings[i];
            center /= crossings.Count;

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
                int v1 = AddVertex(mesh, vertexMap, ordered[i],     keyTol);
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

        private static Vector3d EstimateCubeGradient(double[] s)
        {
            double sx0 = (s[0] + s[3] + s[4] + s[7]) * 0.25;
            double sx1 = (s[1] + s[2] + s[5] + s[6]) * 0.25;
            double sy0 = (s[0] + s[1] + s[4] + s[5]) * 0.25;
            double sy1 = (s[2] + s[3] + s[6] + s[7]) * 0.25;
            double sz0 = (s[0] + s[1] + s[2] + s[3]) * 0.25;
            double sz1 = (s[4] + s[5] + s[6] + s[7]) * 0.25;
            return new Vector3d(sx1 - sx0, sy1 - sy0, sz1 - sz0);
        }

        private static int AddVertex(
            Mesh mesh, Dictionary<VertexKey, int> map,
            Point3d p, double tol)
        {
            var key = new VertexKey(p, tol);
            if (map.TryGetValue(key, out int idx)) return idx;
            idx = mesh.Vertices.Add(p);
            map[key] = idx;
            return idx;
        }

        // ══════════════════════════════════════════════════════════════════
        // MESH CLEANUP  (from wsp_In05 — verbatim)
        // ══════════════════════════════════════════════════════════════════

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

        private static void CleanMeshFromSampledGrid(Mesh mesh)
        {
            if (mesh == null || mesh.Faces.Count == 0)
                return;

            mesh.Vertices.CombineIdentical(true, true);
            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();
            mesh.Weld(Math.PI);
            mesh.UnifyNormals();
            mesh.Normals.ComputeNormals();
            mesh.FaceNormals.ComputeFaceNormals();
            mesh.Compact();
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

                // Marching Cubes already provides locally coherent faces. First
                // unify each connected island, then perform one field-gradient
                // orientation pass to select the outward direction. The former
                // two-pass order evaluated this expensive surface field twice.
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
            Mesh mesh,
            double weldAngleDeg,
            int minFragFaces,
            WasperField field,
            double normalStep,
            out int removedFragments)
        {
            removedFragments = 0;

            if (mesh == null || mesh.Faces.Count == 0)
                return;

            mesh.Vertices.CombineIdentical(true, true);
            mesh.Faces.CullDegenerateFaces();
            mesh.Vertices.CullUnused();

            if (minFragFaces > 0)
                RemoveSmallFragmentsInPlace(mesh, minFragFaces, out removedFragments);

            // RemoveSmallFragmentsInPlace rebuilds the mesh only when it removes
            // something. In that case, cull the newly unused vertices without
            // repeating the earlier combine/degenerate cleanup.
            if (removedFragments > 0)
                mesh.Vertices.CullUnused();

            // One field pass is sufficient after local normal unification and
            // halves the costly closest-surface evaluations during cleanup.
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
                AddFV(v2f, f.A, fi); AddFV(v2f, f.B, fi);
                AddFV(v2f, f.C, fi); if (f.IsQuad) AddFV(v2f, f.D, fi);
            }

            bool[] visited = new bool[faceCount];
            bool[] keep    = new bool[faceCount];
            var queue      = new Queue<int>();

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
                    EnqN(f.A); EnqN(f.B); EnqN(f.C);
                    if (f.IsQuad) EnqN(f.D);

                    void EnqN(int vi)
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
            var vMap  = new Dictionary<int, int>();
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
                else          clean.Faces.AddFace(a, b, c);
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

        // ══════════════════════════════════════════════════════════════════
        // UTILITIES  (from wsp_In05 — verbatim)
        // ══════════════════════════════════════════════════════════════════

        private static int Idx(int ix, int iy, int iz, int nx, int ny)
            => ix + nx * (iy + ny * iz);

        private static int    Clamp(int v, int lo, int hi)
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
