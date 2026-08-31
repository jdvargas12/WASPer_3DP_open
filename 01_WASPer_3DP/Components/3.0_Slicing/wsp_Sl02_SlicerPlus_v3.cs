#region Component Description
/*
Component: wsp_Sl02_SlicerPlus v3
Nickname: Slicer+
Category: WASPer_3DP
SubCategory: 3.0_Slicing
Version:
    Uses the compiled assembly version in the component message via
    _versionTag, following the other v1.0.5 compiled components.

VERSION NOTICE (2026-07-27)
This is the v3 generation of SlicerPlus. The previous v2 generation was moved
to Components\3.0_Slicing\Legacy\wsp_Sl02_SlicerPlus_v2_legacy.cs, marked
GH_Exposure.hidden, and kept on its original GUID so existing saved documents
keep loading. v3 uses a NEW GUID and adds a merged full_path output plus a
right-click "Trim layers" toggle (see RegisterOutputParams / AppendAdditional-
ComponentMenuItems below).

GENERAL DESCRIPTION
This component slices shell, infill, and partition geometry or WASPer 3D fields
into per-layer toolpaths.

It supports three slicing modes:

1. Default World Z slicing
   If no ref_curve is provided, slicing planes are generated as horizontal
   World Z layers from the geometry bounding range.

2. Curve-reference perpendicular slicing
   If ref_curve is provided and slicing_mode = 1, slicing planes are generated
   along the curve at layer_h distance intervals. Each plane origin is placed
   on the curve and each plane Z axis follows the curve tangent at that station.

3. Curve-reference XY slicing
   If ref_curve is provided and slicing_mode = 2, planes are generated at the
   same curve stations, but the plane orientation remains World XY.

The component accepts Mesh, Brep, Surface, Extrusion, and WasperField inputs for
shell, infill, and partitions. Curves are intentionally not accepted as slice
geometry.

IMPORTANT BEHAVIOR
Candidate layer planes are generated first, then culled against the shell. A plane is
kept only if it actually intersects the shell. This prevents empty early/late
branches and keeps p_path_shell, p_path_infill, p_path_partition, and layer_planes
aligned with the same data tree layer indices.

After parallel layer generation, closed Shell toolpaths are normalized
sequentially. Their clockwise/counterclockwise direction matches the first valid
closed Shell, and their seams are placed nearest to that first seam after mapping
its local coordinates through each layer plane. Open Shell fragments are unchanged.
*/
#endregion

#region Usings
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;
using Rhino.Geometry.Intersect;
#endregion

namespace WASPer_3DP.Components._3_0_Slicing
{
    public sealed class wsp_Sl02_SlicerPlus_v3 : GH_Component
    {
        private const string ComponentName = "wsp_Sl02_SlicerPlus v3";
        private const string ComponentNickname = "Slicer+";
        private const string ComponentCategory = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string ComponentSubCategory = "3.0_Slicing";

        private readonly string _versionTag;

        private bool _trimLayers = true;
        private const string TrimLayersKey = "wsp_sl02_v3_trim_layers";
        private readonly Dictionary<int, ShellSliceCacheEntry> _shellCache =
            new Dictionary<int, ShellSliceCacheEntry>();

        public wsp_Sl02_SlicerPlus_v3()
            : base(
                ComponentName,
                ComponentNickname,
                "Slices shell, infill, and partition geometry or WASPer 3D fields. Optional curve reference supports perpendicular or XY slicing.\r\n" +
                "full_path merges shell/infill/partition per layer; right-click 'Trim layers' controls whether it collapses to {layer} (default) or keeps {layer;0=shell,1=infill,2=partition} sub-branches.\r\n" +
                "All final path curves carry hidden WASPer.PathRole metadata. Closed Shell curves are automatically direction-normalized and their seams are aligned through the layer planes from the first valid Shell seam. Normalization is applied only to validated duplicates; an unsafe candidate leaves the original Shell unchanged.",
                ComponentCategory,
                ComponentSubCategory)
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.5";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("715F4013-2DA1-40B8-95B7-E708BB86BCC7");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var stream = asm.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Sl01_SlicerPlus.png"))
                    {
                        return stream != null ? new Bitmap(stream) : null;
                    }
                }
                catch { }
                return null;
            }
        }

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(
                menu,
                "Trim layers (full_path -> {layer})",
                (sender, args) =>
                {
                    RecordUndoEvent("Toggle SlicerPlus full_path trim_layers");
                    _trimLayers = !_trimLayers;
                    ExpireSolution(true);
                },
                true,
                _trimLayers);
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetBoolean(TrimLayersKey, _trimLayers);
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            bool result = base.Read(reader);
            _trimLayers = !reader.ItemExists(TrimLayersKey) || reader.GetBoolean(TrimLayersKey);
            return result;
        }

        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddGenericParameter(
                "geo_shell",
                "shell",
                "Main printable shell source. Accepts Mesh, Brep, Surface, Extrusion, or WASPer 3D Field. Curves are ignored. List access groups outputs by item.",
                GH_ParamAccess.list);
            pManager[0].Optional = true;
            pManager[0].DataMapping = GH_DataMapping.Flatten;

            pManager.AddGenericParameter(
                "geo_infill",
                "infill",
                "Optional infill source. Accepts Mesh, Brep, Surface, Extrusion, or WASPer 3D Field. Curves are ignored. If supplied with multiple shells, item counts must match.",
                GH_ParamAccess.list);
            pManager[1].Optional = true;
            pManager[1].DataMapping = GH_DataMapping.Flatten;

            pManager.AddGenericParameter(
                "geo_partition",
                "partition",
                "Optional partition/stiffener source for internal walls, vertical reinforcements, or split/blocking curves. Accepts Mesh, Brep, Surface, Extrusion, or WASPer 3D Field. Curves are ignored. If using more than one partition object for the same item, join geometry before input or combine fields upstream. If supplied with multiple shells, item counts must match.",
                GH_ParamAccess.list);
            pManager[2].Optional = true;
            pManager[2].DataMapping = GH_DataMapping.Flatten;

            pManager.AddCurveParameter(
                "ref_curve",
                "ref",
                "Optional slicing reference curve. If empty, World Z slicing is used.",
                GH_ParamAccess.item);
            pManager[3].Optional = true;

            pManager.AddIntegerParameter(
                "slicing_mode",
                "mode",
                "Reference curve mode. 1 = planes perpendicular to curve tangent. 2 = World XY planes located at curve sample points.",
                GH_ParamAccess.item,
                1);

            pManager.AddNumberParameter(
                "layer_h",
                "la_h",
                "Layer spacing in model units. In curve modes this is distance along ref_curve.",
                GH_ParamAccess.item,
                2.0);

            pManager.AddNumberParameter(
                "shell_path_width",
                "w_shell",
                "Nominal shell bead/path width. 0 outputs raw shell loops.",
                GH_ParamAccess.item,
                0.0);

            pManager.AddIntegerParameter(
                "shell_n_contours",
                "n_shell",
                "Number of shell contours.",
                GH_ParamAccess.item,
                1);

            pManager.AddNumberParameter(
                "infill_path_width",
                "w_infill",
                "Optional infill contour spacing. 0 outputs raw accepted infill segments.",
                GH_ParamAccess.item,
                0.0);

            pManager.AddIntegerParameter(
                "infill_n_contours",
                "n_infill",
                "Number of centered infill contours.",
                GH_ParamAccess.item,
                1);

            pManager.AddNumberParameter(
                "partition_path_width",
                "w_part",
                "Nominal partition bead/path width. 0 means split-only partitions.",
                GH_ParamAccess.item,
                0.0);

            pManager.AddIntegerParameter(
                "partition_n_contours",
                "n_part",
                "Number of partition contours.",
                GH_ParamAccess.item,
                1);

            pManager.AddBooleanParameter(
                "clean_short",
                "clean",
                "Remove very short fragments.",
                GH_ParamAccess.item,
                false);

            pManager.AddNumberParameter(
                "clean_len",
                "minLen",
                "Minimum curve length when clean_short is true.",
                GH_ParamAccess.item,
                1.0);

            pManager.AddNumberParameter(
                "field_res",
                "f_res",
                "Sampling resolution used only when slicing WASPer fields.\n" +
                "Default 5.0 keeps field slicing responsive. Smaller values increase detail but can be much slower.\n" +
                "Set <= 0 to use the automatic path-width/layer-height based resolution.",
                GH_ParamAccess.item,
                5.0);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "full_path",
                "full_path",
                "Complete per-layer toolpath merging shell, infill, and partition curves in that order. " +
                "Every curve remains an ordinary Rhino Curve and carries hidden WASPer.PathRole metadata " +
                "(Shell, Infill, or Partition), including when role branches are trimmed away. " +
                "Right-click 'Trim layers' controls the tree shape: trimmed (default) collapses each layer " +
                "into a single flat branch {layer}; untrimmed keeps shell/infill/partition as separate " +
                "sub-branches {layer;0=shell,1=infill,2=partition}. Curves are collected only, not joined " +
                "or reordered for travel moves.",
                GH_ParamAccess.tree);
            pManager.AddPlaneParameter("layer_planes", "la_planes", "Slicing planes per valid layer.", GH_ParamAccess.tree);
            pManager.AddCurveParameter("p_path_shell", "shell", "Shell toolpaths per valid layer. Closed curves share the first valid Shell's direction and use the closest seam obtained by mapping its seam through each layer plane. Changes are accepted only after duplicate-curve safety validation; otherwise the original Shell is preserved. Open fragments remain unchanged. Curves carry WASPer.PathRole=Shell metadata.", GH_ParamAccess.tree);
            pManager.AddCurveParameter("p_path_infill", "infill", "Infill toolpaths per valid layer. Curves carry WASPer.PathRole=Infill metadata.", GH_ParamAccess.tree);
            pManager.AddCurveParameter("p_path_partition", "partition", "Partition toolpaths per valid layer. Curves carry WASPer.PathRole=Partition metadata.", GH_ParamAccess.tree);
            pManager.AddTextParameter("info", "info", "Status information.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            var shellInputs = new List<IGH_Goo>();
            var infillInputs = new List<IGH_Goo>();
            var partitionInputs = new List<IGH_Goo>();
            Curve refCurve = null;
            int slicingMode = 1;
            double layerH = 2.0;
            double shellPathWidth = 0.0;
            int shellNContours = 1;
            double infillPathWidth = 0.0;
            int infillNContours = 1;
            double partitionPathWidth = 0.0;
            int partitionNContours = 1;
            bool cleanShort = false;
            double cleanLen = 1.0;
            double fieldRes = 5.0;

            DA.GetDataList("geo_shell", shellInputs);
            DA.GetDataList("geo_infill", infillInputs);
            DA.GetDataList("geo_partition", partitionInputs);
            DA.GetData("ref_curve", ref refCurve);
            DA.GetData("slicing_mode", ref slicingMode);
            DA.GetData("layer_h", ref layerH);
            DA.GetData("shell_path_width", ref shellPathWidth);
            DA.GetData("shell_n_contours", ref shellNContours);
            DA.GetData("infill_path_width", ref infillPathWidth);
            DA.GetData("infill_n_contours", ref infillNContours);
            DA.GetData("partition_path_width", ref partitionPathWidth);
            DA.GetData("partition_n_contours", ref partitionNContours);
            DA.GetData("clean_short", ref cleanShort);
            DA.GetData("clean_len", ref cleanLen);
            DA.GetData("field_res", ref fieldRes);

            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;

            if (layerH <= RhinoMath.ZeroTolerance)
                layerH = 2.0;

            slicingMode = slicingMode == 2 ? 2 : 1;
            shellNContours = Math.Max(1, shellNContours);
            infillNContours = Math.Max(1, infillNContours);
            partitionNContours = Math.Max(1, partitionNContours);

            double minLen = cleanShort ? Math.Max(cleanLen, 2.0 * tol) : 2.0 * tol;

            int rejected = 0;
            var shells = ToSupportedSourceList(shellInputs, ref rejected);
            var infills = ToSupportedSourceList(infillInputs, ref rejected);
            var parts = ToSupportedSourceList(partitionInputs, ref rejected);

            var outFullPath = new GH_Structure<GH_Curve>();
            var outShell = new GH_Structure<GH_Curve>();
            var outInfill = new GH_Structure<GH_Curve>();
            var outPart = new GH_Structure<GH_Curve>();
            var outPlanes = new GH_Structure<GH_Plane>();

            if (rejected > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{rejected} unsupported input item(s) ignored. Use Mesh, Brep, Surface, Extrusion, or WASPer 3D Field. Curves are intentionally not supported as slice sources.");
            }

            if (shells.Count == 0 && infills.Count == 0 && parts.Count == 0)
            {
                SetOutputs(DA, outFullPath, outPlanes, outShell, outInfill, outPart, "No valid geometry.");
                return;
            }

            int itemCount;
            string countError;
            if (!TryGetItemGroupCount(shells, infills, parts, out itemCount, out countError))
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, countError);
                SetOutputs(DA, outFullPath, outPlanes, outShell, outInfill, outPart, countError);
                return;
            }

            int totalCandidatePlanes = 0;
            int totalCulledPlanes = 0;
            int totalLayers = 0;
            int totalOpenShellSlices = 0;
            int totalReversedShells = 0;
            int totalAlignedShellSeams = 0;
            int totalRejectedShellNormalizations = 0;
            int shellCacheHits = 0;

            for (int itemIndex = 0; itemIndex < itemCount; itemIndex++)
            {
                SliceSource shell = shells.Count > 0 ? shells[itemIndex] : null;
                var itemInfills = infills.Count > 0 ? new List<SliceSource> { infills[itemIndex] } : new List<SliceSource>();
                var itemParts = parts.Count > 0 ? new List<SliceSource> { parts[itemIndex] } : new List<SliceSource>();

                ProcessItemGroup(
                    itemIndex,
                    itemCount,
                    shell,
                    itemInfills,
                    itemParts,
                    refCurve,
                    slicingMode,
                    layerH,
                    shellPathWidth,
                    shellNContours,
                    infillPathWidth,
                    infillNContours,
                    partitionPathWidth,
                    partitionNContours,
                    fieldRes,
                    tol,
                    minLen,
                    _trimLayers,
                    outFullPath,
                    outShell,
                    outInfill,
                    outPart,
                    outPlanes,
                    out int candidatePlaneCount,
                    out int culledPlaneCount,
                    out int layerCount,
                    out int openShellCount,
                    out int reversedShellCount,
                    out int alignedShellSeamCount,
                    out int rejectedShellNormalizationCount,
                    out bool shellCacheHit);

                totalCandidatePlanes += candidatePlaneCount;
                totalCulledPlanes += culledPlaneCount;
                totalLayers += layerCount;
                totalOpenShellSlices += openShellCount;
                totalReversedShells += reversedShellCount;
                totalAlignedShellSeams += alignedShellSeamCount;
                totalRejectedShellNormalizations += rejectedShellNormalizationCount;
                if (shellCacheHit) shellCacheHits++;
            }

            foreach (int staleIndex in _shellCache.Keys.Where(index => index >= itemCount).ToList())
                _shellCache.Remove(staleIndex);

            if (totalLayers == 0)
            {
                SetOutputs(DA, outFullPath, outPlanes, outShell, outInfill, outPart, "No valid slicing layers generated.");
                return;
            }

            if (totalOpenShellSlices > 0)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"Shell produced {totalOpenShellSlices} open slice curve(s). These cannot define a closed infill trim window.");

            if (totalRejectedShellNormalizations > 0)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{totalRejectedShellNormalizations} closed Shell normalization candidate(s) failed safety validation. Their original curves were preserved unchanged.");

            string mode = "world Z";
            if (refCurve != null && refCurve.IsValid)
                mode = slicingMode == 2 ? "curve reference - XY planes" : "curve reference - perpendicular planes";

            string info =
                "OK | mode=" + mode +
                " | data=" + SummarizeSourceTypes(shells, infills, parts) +
                " | item_groups=" + itemCount +
                " | candidate_planes=" + totalCandidatePlanes +
                " | culled_planes=" + totalCulledPlanes +
                " | layers=" + totalLayers +
                " | open_shell_slices=" + totalOpenShellSlices +
                " | shell_reversed=" + totalReversedShells +
                " | shell_seams_aligned=" + totalAlignedShellSeams +
                " | shell_normalization_rejected=" + totalRejectedShellNormalizations +
                " | shell_cache=" + shellCacheHits + "/" + itemCount +
                " | field_res=" + (fieldRes > tol ? fieldRes.ToString("F3") : "auto") +
                " | path_roles=curve metadata";

            SetOutputs(DA, outFullPath, outPlanes, outShell, outInfill, outPart, info);
            Message = _versionTag + " | " + SummarizeSourceTypes(shells, infills, parts);
        }

        private void ProcessItemGroup(
            int itemIndex,
            int itemCount,
            SliceSource shell,
            List<SliceSource> infills,
            List<SliceSource> parts,
            Curve refCurve,
            int slicingMode,
            double layerH,
            double shellPathWidth,
            int shellNContours,
            double infillPathWidth,
            int infillNContours,
            double partitionPathWidth,
            int partitionNContours,
            double fieldRes,
            double tol,
            double minLen,
            bool trimLayers,
            GH_Structure<GH_Curve> outFullPath,
            GH_Structure<GH_Curve> outShell,
            GH_Structure<GH_Curve> outInfill,
            GH_Structure<GH_Curve> outPart,
            GH_Structure<GH_Plane> outPlanes,
            out int candidatePlaneCount,
            out int culledPlaneCount,
            out int layerCount,
            out int openShellCount,
            out int reversedShellCount,
            out int alignedShellSeamCount,
            out int rejectedShellNormalizationCount,
            out bool shellCacheHit)
        {
            candidatePlaneCount = 0;
            culledPlaneCount = 0;
            layerCount = 0;
            openShellCount = 0;
            reversedShellCount = 0;
            alignedShellSeamCount = 0;
            rejectedShellNormalizationCount = 0;
            shellCacheHit = false;

            List<Plane> slicePlanes;
            WasperPreparedTrimShell[] preparedShells = null;
            List<Curve>[] cachedShells = null;
            int[] openShellCounts;

            if (shell != null && shell.IsValid)
            {
                string shellKey = BuildShellCacheKey(
                    shell,
                    refCurve,
                    slicingMode,
                    layerH,
                    shellPathWidth,
                    shellNContours,
                    fieldRes,
                    tol,
                    minLen);

                if (_shellCache.TryGetValue(itemIndex, out ShellSliceCacheEntry cached) &&
                    string.Equals(cached.Key, shellKey, StringComparison.Ordinal))
                {
                    shellCacheHit = true;
                    candidatePlaneCount = cached.CandidatePlaneCount;
                    culledPlaneCount = cached.CulledPlaneCount;
                    reversedShellCount = cached.ReversedShellCount;
                    alignedShellSeamCount = cached.AlignedShellSeamCount;
                    rejectedShellNormalizationCount = cached.RejectedShellNormalizationCount;
                    slicePlanes = cached.SlicePlanes.Select(plane => new Plane(plane)).ToList();
                    preparedShells = cached.PreparedShells;
                    cachedShells = DuplicateCurveLayers(cached.ShellsByLayer);
                    openShellCounts = (int[])cached.OpenShellCounts.Clone();
                }
                else
                {
                    List<Plane> candidates = BuildSlicePlanes(
                        refCurve,
                        slicingMode,
                        layerH,
                        shell,
                        new List<SliceSource>(),
                        new List<SliceSource>(),
                        tol);
                    candidatePlaneCount = candidates.Count;
                    double shellResolution = ComputeFieldSampleResolution(
                        shell,
                        null,
                        null,
                        layerH,
                        shellPathWidth,
                        0.0,
                        0.0,
                        fieldRes,
                        tol);

                    var keptPlanes = new List<Plane>();
                    var keptPrepared = new List<WasperPreparedTrimShell>();
                    var keptOpenCounts = new List<int>();
                    foreach (Plane plane in candidates)
                    {
                        List<Curve> raw = SliceSourceCurves(shell, plane, tol, minLen, shellResolution);
                        if (raw.Count == 0)
                        {
                            culledPlaneCount++;
                            continue;
                        }

                        List<Curve> closed = WasperTrimCore.CloseAndCullCurves(raw, tol, minLen, out List<Curve> open);
                        keptPlanes.Add(plane);
                        keptOpenCounts.Add(open.Count);
                        keptPrepared.Add(WasperTrimCore.PrepareShell(
                            true,
                            closed,
                            open,
                            plane,
                            shellPathWidth,
                            shellNContours,
                            tol,
                            minLen));
                    }

                    slicePlanes = keptPlanes;
                    preparedShells = keptPrepared.ToArray();
                    openShellCounts = keptOpenCounts.ToArray();
                    cachedShells = preparedShells
                        .Select(prepared => prepared.OutputShell.Select(curve => curve.DuplicateCurve()).ToList())
                        .ToArray();

                    NormalizeClosedShells(
                        cachedShells,
                        slicePlanes,
                        tol,
                        out reversedShellCount,
                        out alignedShellSeamCount,
                        out rejectedShellNormalizationCount);

                    for (int i = 0; i < preparedShells.Length; i++)
                    {
                        preparedShells[i].OutputShell.Clear();
                        preparedShells[i].OutputShell.AddRange(
                            cachedShells[i].Select(curve => curve.DuplicateCurve()));
                    }

                    _shellCache[itemIndex] = new ShellSliceCacheEntry(
                        shellKey,
                        candidatePlaneCount,
                        culledPlaneCount,
                        slicePlanes,
                        preparedShells,
                        cachedShells,
                        openShellCounts,
                        reversedShellCount,
                        alignedShellSeamCount,
                        rejectedShellNormalizationCount);
                }
            }
            else
            {
                _shellCache.Remove(itemIndex);
                List<Plane> candidates = BuildSlicePlanes(refCurve, slicingMode, layerH, shell, infills, parts, tol);
                candidatePlaneCount = candidates.Count;
                slicePlanes = candidates;
                openShellCounts = new int[slicePlanes.Count];
            }

            if (slicePlanes.Count == 0)
                return;

            double infillSampleRes = ComputeFieldSampleResolution(null, infills, null, layerH, 0.0, infillPathWidth, 0.0, fieldRes, tol);
            double partitionSampleRes = ComputeFieldSampleResolution(null, null, parts, layerH, 0.0, 0.0, partitionPathWidth, fieldRes, tol);

            layerCount = slicePlanes.Count;
            var shellByLayer = new List<Curve>[layerCount];
            var infillByLayer = new List<Curve>[layerCount];
            var partByLayer = new List<Curve>[layerCount];

            // Per-layer trimming is delegated to the shared WasperTrimCore
            // (Components\Shared\Geometry\WASPer_TrimCore.cs) so Sl02 SlicerPlus and
            // Sl03 Re-Trim Printing Paths share identical behavior.
            Action<int> work = i =>
            {
                Plane pl = slicePlanes[i];

                bool hasShell = preparedShells != null;

                var partitionGroups = new List<List<Curve>>();
                foreach (var g in parts)
                    partitionGroups.Add(SliceSourceCurves(g, pl, tol, minLen, partitionSampleRes));

                var infillGroups = new List<List<Curve>>();
                foreach (var g in infills)
                    infillGroups.Add(SliceSourceCurves(g, pl, tol, minLen, infillSampleRes));

                List<Curve> localShell, localInfill, localPart;
                WasperTrimCore.TrimLayer(
                    hasShell ? preparedShells[i] : new WasperPreparedTrimShell(),
                    partitionGroups,
                    infillGroups,
                    pl,
                    infillPathWidth,
                    infillNContours,
                    partitionPathWidth,
                    partitionNContours,
                    tol,
                    minLen,
                    out localShell,
                    out localInfill,
                    out localPart);

                shellByLayer[i] = hasShell
                    ? cachedShells[i].Select(curve => curve.DuplicateCurve()).ToList()
                    : localShell;
                infillByLayer[i] = localInfill;
                partByLayer[i] = localPart;
            };

            if (layerCount >= 4)
                Parallel.For(0, layerCount, work);
            else
                for (int i = 0; i < layerCount; i++) work(i);

            if (preparedShells == null)
            {
                NormalizeClosedShells(
                    shellByLayer,
                    slicePlanes,
                    tol,
                    out reversedShellCount,
                    out alignedShellSeamCount,
                    out rejectedShellNormalizationCount);
            }

            for (int i = 0; i < layerCount; i++)
            {
                var path = itemCount == 1 ? new GH_Path(i) : new GH_Path(itemIndex, i);

                TagCurves(shellByLayer[i], global::WASPer_3DP.WasperPathRole.Shell);
                TagCurves(infillByLayer[i], global::WASPer_3DP.WasperPathRole.Infill);
                TagCurves(partByLayer[i], global::WASPer_3DP.WasperPathRole.Partition);

                outPlanes.Append(new GH_Plane(slicePlanes[i]), path);

                if (shellByLayer[i] != null)
                    outShell.AppendRange(shellByLayer[i].Select(c => new GH_Curve(c)), path);

                if (infillByLayer[i] != null)
                    outInfill.AppendRange(infillByLayer[i].Select(c => new GH_Curve(c)), path);

                if (partByLayer[i] != null)
                    outPart.AppendRange(partByLayer[i].Select(c => new GH_Curve(c)), path);

                // full_path: merges shell, infill, and partition curves for this layer in that
                // order. trimLayers = true collapses everything into the same {layer} branch used
                // by the other outputs; trimLayers = false keeps them separated by source under
                // {layer;0=shell,1=infill,2=partition} so downstream consumers can tell them apart.
                if (trimLayers)
                {
                    var merged = new List<Curve>();
                    if (shellByLayer[i] != null) merged.AddRange(shellByLayer[i]);
                    if (infillByLayer[i] != null) merged.AddRange(infillByLayer[i]);
                    if (partByLayer[i] != null) merged.AddRange(partByLayer[i]);

                    if (merged.Count > 0)
                        outFullPath.AppendRange(merged.Select(c => new GH_Curve(c)), path);
                }
                else
                {
                    if (shellByLayer[i] != null && shellByLayer[i].Count > 0)
                        outFullPath.AppendRange(shellByLayer[i].Select(c => new GH_Curve(c)), path.AppendElement(0));

                    if (infillByLayer[i] != null && infillByLayer[i].Count > 0)
                        outFullPath.AppendRange(infillByLayer[i].Select(c => new GH_Curve(c)), path.AppendElement(1));

                    if (partByLayer[i] != null && partByLayer[i].Count > 0)
                        outFullPath.AppendRange(partByLayer[i].Select(c => new GH_Curve(c)), path.AppendElement(2));
                }
            }

            openShellCount = openShellCounts.Sum();
        }

        private static string BuildShellCacheKey(
            SliceSource shell,
            Curve refCurve,
            int slicingMode,
            double layerH,
            double shellPathWidth,
            int shellNContours,
            double fieldRes,
            double tolerance,
            double minLength)
        {
            WasperCacheSignature signature = WasperCacheSignature.Create();
            shell?.AddToSignature(ref signature);
            signature.Add(refCurve);
            signature.Add(slicingMode);
            signature.Add(layerH);
            signature.Add(shellPathWidth);
            signature.Add(shellNContours);
            signature.Add(fieldRes);
            signature.Add(tolerance);
            signature.Add(minLength);
            return signature.Finish();
        }

        private static List<Curve>[] DuplicateCurveLayers(IReadOnlyList<List<Curve>> layers)
        {
            if (layers == null) return Array.Empty<List<Curve>>();
            return layers
                .Select(layer => (layer ?? new List<Curve>())
                    .Where(curve => curve != null && curve.IsValid)
                    .Select(curve => curve.DuplicateCurve())
                    .ToList())
                .ToArray();
        }

        private static void NormalizeClosedShells(
            IList<List<Curve>> shellsByLayer,
            IList<Plane> layerPlanes,
            double tolerance,
            out int reversedCount,
            out int alignedSeamCount,
            out int rejectedCount)
        {
            reversedCount = 0;
            alignedSeamCount = 0;
            rejectedCount = 0;

            if (shellsByLayer == null || layerPlanes == null)
                return;

            Curve referenceCurve = null;
            Plane referencePlane = Plane.Unset;
            CurveOrientation referenceOrientation = CurveOrientation.Undefined;

            for (int layerIndex = 0;
                 layerIndex < shellsByLayer.Count && layerIndex < layerPlanes.Count;
                 layerIndex++)
            {
                List<Curve> layerShells = shellsByLayer[layerIndex];
                if (layerShells == null)
                    continue;

                foreach (Curve curve in layerShells)
                {
                    if (curve == null || !curve.IsValid || !curve.IsClosed)
                        continue;

                    referenceCurve = curve;
                    referencePlane = layerPlanes[layerIndex];
                    referenceOrientation = curve.ClosedCurveOrientation(referencePlane);
                    break;
                }

                if (referenceCurve != null)
                    break;
            }

            if (referenceCurve == null ||
                !referencePlane.IsValid ||
                !referencePlane.ClosestParameter(
                    referenceCurve.PointAtStart,
                    out double referenceU,
                    out double referenceV))
                return;

            double seamTolerance = Math.Max(tolerance, RhinoMath.ZeroTolerance);

            for (int layerIndex = 0;
                 layerIndex < shellsByLayer.Count && layerIndex < layerPlanes.Count;
                 layerIndex++)
            {
                List<Curve> layerShells = shellsByLayer[layerIndex];
                Plane layerPlane = layerPlanes[layerIndex];
                if (layerShells == null || !layerPlane.IsValid)
                    continue;

                Point3d seamTarget = layerPlane.PointAt(referenceU, referenceV);
                for (int curveIndex = 0; curveIndex < layerShells.Count; curveIndex++)
                {
                    Curve curve = layerShells[curveIndex];
                    if (curve == null ||
                        !curve.IsValid ||
                        !curve.IsClosed ||
                        ReferenceEquals(curve, referenceCurve))
                        continue;

                    Curve candidate = curve.DuplicateCurve();
                    if (candidate == null || !candidate.IsValid || !candidate.IsClosed)
                    {
                        candidate?.Dispose();
                        rejectedCount++;
                        continue;
                    }

                    bool reversed = false;
                    CurveOrientation orientation = candidate.ClosedCurveOrientation(layerPlane);
                    if (referenceOrientation != CurveOrientation.Undefined &&
                        orientation != CurveOrientation.Undefined &&
                        orientation != referenceOrientation)
                    {
                        if (!candidate.Reverse())
                        {
                            candidate.Dispose();
                            rejectedCount++;
                            continue;
                        }
                        reversed = true;
                    }

                    if (!candidate.ClosestPoint(seamTarget, out double seamParameter))
                    {
                        candidate.Dispose();
                        rejectedCount++;
                        continue;
                    }

                    Point3d closestSeam = candidate.PointAt(seamParameter);
                    bool seamNeedsChange =
                        candidate.PointAtStart.DistanceTo(closestSeam) > seamTolerance;
                    if (seamNeedsChange &&
                        !candidate.ChangeClosedCurveSeam(seamParameter))
                    {
                        candidate.Dispose();
                        rejectedCount++;
                        continue;
                    }

                    if (!reversed && !seamNeedsChange)
                    {
                        candidate.Dispose();
                        continue;
                    }

                    if (!IsSafeNormalizedShell(curve, candidate, seamTolerance))
                    {
                        candidate.Dispose();
                        rejectedCount++;
                        continue;
                    }

                    layerShells[curveIndex] = candidate;
                    if (reversed)
                        reversedCount++;
                    if (seamNeedsChange)
                        alignedSeamCount++;
                    curve.Dispose();
                }
            }
        }

        private static bool IsSafeNormalizedShell(
            Curve original,
            Curve candidate,
            double tolerance)
        {
            if (original == null ||
                candidate == null ||
                !original.IsValid ||
                !candidate.IsValid ||
                !original.IsClosed ||
                !candidate.IsClosed)
                return false;

            try
            {
                double originalLength = original.GetLength();
                double candidateLength = candidate.GetLength();
                if (!double.IsFinite(originalLength) ||
                    !double.IsFinite(candidateLength) ||
                    originalLength <= tolerance ||
                    candidateLength <= tolerance)
                    return false;

                double lengthTolerance = Math.Max(
                    tolerance * 10.0,
                    originalLength * 1e-8);
                if (Math.Abs(originalLength - candidateLength) > lengthTolerance)
                    return false;

                BoundingBox originalBox = original.GetBoundingBox(true);
                BoundingBox candidateBox = candidate.GetBoundingBox(true);
                if (!originalBox.IsValid || !candidateBox.IsValid)
                    return false;

                double boxTolerance = Math.Max(tolerance * 10.0, 1e-8);
                if (originalBox.Min.DistanceTo(candidateBox.Min) > boxTolerance ||
                    originalBox.Max.DistanceTo(candidateBox.Max) > boxTolerance)
                    return false;

                double[] fractions = { 0.0, 0.25, 0.5, 0.75, 1.0 };
                foreach (double fraction in fractions)
                {
                    double distance = candidateLength * fraction;
                    double parameter;
                    if (!candidate.LengthParameter(distance, out parameter))
                        parameter = candidate.Domain.ParameterAt(fraction);

                    Point3d point = candidate.PointAt(parameter);
                    if (!point.IsValid)
                        return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void TagCurves(
            IEnumerable<Curve> curves,
            global::WASPer_3DP.WasperPathRole role)
        {
            if (curves == null)
                return;

            foreach (Curve curve in curves)
                global::WASPer_3DP.WasperPathRoleMetadata.Set(curve, role);
        }

        private static void SetOutputs(
            IGH_DataAccess da,
            GH_Structure<GH_Curve> fullPath,
            GH_Structure<GH_Plane> planes,
            GH_Structure<GH_Curve> shell,
            GH_Structure<GH_Curve> infill,
            GH_Structure<GH_Curve> part,
            string info)
        {
            da.SetDataTree(0, fullPath);
            da.SetDataTree(1, planes);
            da.SetDataTree(2, shell);
            da.SetDataTree(3, infill);
            da.SetDataTree(4, part);
            da.SetData(5, info);
        }

        private static bool TryGetItemGroupCount(
            List<SliceSource> shells,
            List<SliceSource> infills,
            List<SliceSource> parts,
            out int itemCount,
            out string error)
        {
            itemCount = Math.Max(shells?.Count ?? 0, Math.Max(infills?.Count ?? 0, parts?.Count ?? 0));
            error = null;

            if (itemCount == 0)
            {
                error = "No valid geometry.";
                return false;
            }

            if (shells != null && shells.Count > 0 && shells.Count != itemCount)
            {
                error = $"geo_shell count ({shells.Count}) must match the other non-empty source input count ({itemCount}).";
                return false;
            }

            if (infills != null && infills.Count > 0 && infills.Count != itemCount)
            {
                error = $"geo_infill count ({infills.Count}) must match the other non-empty source input count ({itemCount}).";
                return false;
            }

            if (parts != null && parts.Count > 0 && parts.Count != itemCount)
            {
                error = $"geo_partition count ({parts.Count}) must match the other non-empty source input count ({itemCount}).";
                return false;
            }

            return true;
        }

        private static SliceSource ToSupportedSource(IGH_Goo goo, ref int rejectedCount)
        {
            if (goo == null) return null;

            var field = ExtractField(goo);
            if (field != null && field.Evaluator != null)
                return SliceSource.FromField(field);

            GeometryBase geometry;
            if (!goo.CastTo(out geometry) || geometry == null)
            {
                if (goo is GH_ObjectWrapper wrapper && wrapper.Value is GeometryBase wrappedGeometry)
                    geometry = wrappedGeometry;
            }

            if (geometry == null)
            {
                rejectedCount++;
                return null;
            }

            if (!geometry.IsValid)
            {
                rejectedCount++;
                return null;
            }

            if (geometry is Curve)
            {
                rejectedCount++;
                return null;
            }

            if (geometry is Mesh mesh) return SliceSource.FromGeometry(mesh.DuplicateMesh());
            if (geometry is Brep brep) return SliceSource.FromGeometry(brep.DuplicateBrep());
            if (geometry is Surface || geometry is Extrusion) return SliceSource.FromGeometry(geometry);

            rejectedCount++;
            return null;
        }

        private static List<SliceSource> ToSupportedSourceList(IEnumerable<IGH_Goo> goos, ref int rejectedCount)
        {
            var result = new List<SliceSource>();
            if (goos == null) return result;

            foreach (var goo in goos)
            {
                var supported = ToSupportedSource(goo, ref rejectedCount);
                if (supported != null)
                    result.Add(supported);
            }

            return result;
        }

        private static List<Plane> BuildSlicePlanes(
            Curve refCurve,
            int slicingMode,
            double layerH,
            SliceSource shell,
            List<SliceSource> infills,
            List<SliceSource> parts,
            double tol)
        {
            var result = new List<Plane>();

            if (refCurve != null && refCurve.IsValid)
            {
                double length = refCurve.GetLength();
                if (length <= tol) return result;

                int count = (int)Math.Floor(length / layerH);

                for (int i = 1; i <= count; i++)
                {
                    double dist = i * layerH;
                    if (dist > length + tol) break;

                    double t;
                    if (!refCurve.LengthParameter(dist, out t)) continue;

                    Point3d origin = refCurve.PointAt(t);

                    if (slicingMode == 2)
                    {
                        Plane pl = Plane.WorldXY;
                        pl.Origin = origin;
                        result.Add(pl);
                    }
                    else
                    {
                        Vector3d tangent = refCurve.TangentAt(t);
                        if (tangent.IsZero) continue;
                        tangent.Unitize();

                        result.Add(WasperTrimCore.OrthoPlane(origin, tangent));
                    }
                }

                return result;
            }

            Vector3d dir = Vector3d.ZAxis;
            double dMin, dMax;
            GetProjectionRange(dir, shell, infills, parts, out dMin, out dMax);

            if (!(dMax > dMin + tol)) return result;

            int layerCount = (int)Math.Floor((dMax - dMin) / layerH);
            Plane basePlane = WasperTrimCore.OrthoPlane(Point3d.Origin, dir);

            for (int i = 0; i < layerCount; i++)
            {
                double d = dMin + (i + 1) * layerH;
                Plane pl = new Plane(basePlane);
                pl.Translate(basePlane.ZAxis * d);
                result.Add(pl);
            }

            return result;
        }

        private static List<Plane> CullPlanesToShell(
            IEnumerable<Plane> candidates,
            SliceSource shell,
            double tol,
            double minLen,
            out int culledCount)
        {
            var result = new List<Plane>();
            culledCount = 0;

            if (candidates == null) return result;

            if (shell == null || !shell.IsValid)
            {
                result.AddRange(candidates);
                return result;
            }

            double fieldSampleRes = ComputeFieldSampleResolution(shell, null, null, minLen, 0.0, 0.0, 0.0, 5.0, tol);
            foreach (var pl in candidates)
            {
                var hits = SliceSourceCurves(shell, pl, tol, minLen, fieldSampleRes);
                if (hits.Count > 0)
                    result.Add(pl);
                else
                    culledCount++;
            }

            return result;
        }

        private static List<Curve> SliceGeometry(GeometryBase geometry, Plane plane, double tol, double minLen)
        {
            var result = new List<Curve>();
            if (geometry == null || !geometry.IsValid) return result;

            if (geometry is Mesh mesh)
            {
                var polylines = Intersection.MeshPlane(mesh, plane);
                if (polylines == null) return result;

                foreach (var polyline in polylines)
                {
                    if (!polyline.IsValid || polyline.Count < 2) continue;

                    var curve = new PolylineCurve(polyline);
                    if (curve.IsValid && curve.GetLength() >= minLen)
                        result.Add(curve);
                }

                return result;
            }

            Brep brep = null;
            if (geometry is Brep b) brep = b;
            if (geometry is Surface s) brep = Brep.CreateFromSurface(s);
            if (geometry is Extrusion e) brep = e.ToBrep();

            if (brep == null || !brep.IsValid) return result;

            Curve[] curves;
            Point3d[] points;
            if (!Intersection.BrepPlane(brep, plane, tol, out curves, out points) || curves == null)
                return result;

            foreach (var curve in curves)
            {
                if (curve == null || !curve.IsValid) continue;
                if (curve.GetLength() < minLen) continue;

                result.Add(curve.DuplicateCurve());
            }

            return result;
        }

        private static List<Curve> SliceSourceCurves(SliceSource source, Plane plane, double tol, double minLen, double fieldSampleRes)
        {
            if (source == null || !source.IsValid) return new List<Curve>();
            if (source.Kind == SliceSourceKind.Field)
                return SliceField(source.Field, plane, tol, minLen, fieldSampleRes);
            return SliceGeometry(source.Geometry, plane, tol, minLen);
        }

        private static WasperField ExtractField(IGH_Goo goo)
        {
            if (goo == null) return null;
            if (goo is WasperFieldGoo fg) return fg.Value;

            object value;
            if (goo.CastTo(out value) && value != null)
            {
                if (value is WasperField field) return field;
                if (value is WasperFieldGoo fieldGoo) return fieldGoo.Value;
                if (value is GH_ObjectWrapper wrapper)
                {
                    if (wrapper.Value is WasperField wrappedField) return wrappedField;
                    if (wrapper.Value is WasperFieldGoo wrappedGoo) return wrappedGoo.Value;
                }
            }

            return null;
        }

        private static List<Curve> SliceField(WasperField field, Plane plane, double tol, double minLen, double sampleRes)
        {
            var result = new List<Curve>();
            if (field == null || field.Evaluator == null || !field.Domain.IsValid) return result;

            double xMin, xMax, yMin, yMax;
            if (!TryGetPlaneSampleExtents(field.Domain, plane, sampleRes, out xMin, out xMax, out yMin, out yMax))
                return result;

            int nx = Math.Max(2, (int)Math.Ceiling((xMax - xMin) / sampleRes) + 1);
            int ny = Math.Max(2, (int)Math.Ceiling((yMax - yMin) / sampleRes) + 1);

            const int maxAxisSamples = 700;
            if (nx > maxAxisSamples || ny > maxAxisSamples)
            {
                double scale = Math.Max((double)nx / maxAxisSamples, (double)ny / maxAxisSamples);
                sampleRes *= scale;
                nx = Math.Max(2, (int)Math.Ceiling((xMax - xMin) / sampleRes) + 1);
                ny = Math.Max(2, (int)Math.Ceiling((yMax - yMin) / sampleRes) + 1);
            }

            var values = new double[nx * ny];
            for (int j = 0; j < ny; j++)
            {
                double y = yMin + j * sampleRes;
                for (int i = 0; i < nx; i++)
                {
                    double x = xMin + i * sampleRes;
                    Point3d p = plane.Origin + x * plane.XAxis + y * plane.YAxis;
                    values[i + j * nx] = SafeEvaluate(field, p);
                }
            }

            var segments = MarchingSquaresSegments(values, nx, ny, xMin, yMin, sampleRes, plane);
            if (segments.Count == 0) return result;

            var polylines = ChainSegments(segments, Math.Max(tol, sampleRes * 1e-3));
            foreach (var polyline in polylines)
            {
                if (polyline == null || polyline.Count < 2) continue;

                var curve = new PolylineCurve(polyline);
                if (curve.IsValid && curve.GetLength() >= minLen)
                    result.Add(curve);
            }

            return result;
        }

        private static bool TryGetPlaneSampleExtents(BoundingBox box, Plane plane, double pad, out double xMin, out double xMax, out double yMin, out double yMax)
        {
            xMin = yMin = double.PositiveInfinity;
            xMax = yMax = double.NegativeInfinity;

            foreach (Point3d corner in box.GetCorners())
            {
                Vector3d d = corner - plane.Origin;
                double x = d * plane.XAxis;
                double y = d * plane.YAxis;

                if (x < xMin) xMin = x;
                if (x > xMax) xMax = x;
                if (y < yMin) yMin = y;
                if (y > yMax) yMax = y;
            }

            if (double.IsInfinity(xMin) || double.IsInfinity(yMin)) return false;

            xMin -= pad; xMax += pad;
            yMin -= pad; yMax += pad;
            return xMax > xMin && yMax > yMin;
        }

        private static void GetProjectionRange(
            Vector3d dir,
            SliceSource shell,
            IEnumerable<SliceSource> infills,
            IEnumerable<SliceSource> parts,
            out double dMin,
            out double dMax)
        {
            dMin = double.PositiveInfinity;
            dMax = double.NegativeInfinity;

            AccumulateProjection(shell, dir, ref dMin, ref dMax);

            if (infills != null)
                foreach (var g in infills)
                    AccumulateProjection(g, dir, ref dMin, ref dMax);

            if (parts != null)
                foreach (var g in parts)
                    AccumulateProjection(g, dir, ref dMin, ref dMax);

            if (double.IsInfinity(dMin) || double.IsInfinity(dMax))
            {
                dMin = 0;
                dMax = 0;
            }
        }

        private static void AccumulateProjection(SliceSource source, Vector3d dir, ref double dMin, ref double dMax)
        {
            if (source == null || !source.IsValid) return;

            var bb = source.GetBoundingBox();
            foreach (var point in bb.GetCorners())
            {
                double d = dir.X * point.X + dir.Y * point.Y + dir.Z * point.Z;

                if (d < dMin) dMin = d;
                if (d > dMax) dMax = d;
            }
        }

        private static double ComputeFieldSampleResolution(
            SliceSource shell,
            IEnumerable<SliceSource> infills,
            IEnumerable<SliceSource> parts,
            double layerH,
            double shellPathWidth,
            double infillPathWidth,
            double partitionPathWidth,
            double fieldRes,
            double tol)
        {
            if (!ContainsField(shell, infills, parts))
                return Math.Max(layerH, tol * 10.0);

            if (fieldRes > tol)
                return Math.Max(fieldRes, tol * 10.0);

            double res = double.PositiveInfinity;
            if (layerH > tol) res = Math.Min(res, layerH);
            if (shellPathWidth > tol) res = Math.Min(res, shellPathWidth * 0.5);
            if (infillPathWidth > tol) res = Math.Min(res, infillPathWidth * 0.5);
            if (partitionPathWidth > tol) res = Math.Min(res, partitionPathWidth * 0.5);

            BoundingBox domain = BoundingBox.Empty;
            AccumulateSourceDomain(shell, ref domain);
            if (infills != null)
                foreach (var f in infills) AccumulateSourceDomain(f, ref domain);
            if (parts != null)
                foreach (var f in parts) AccumulateSourceDomain(f, ref domain);

            if (double.IsInfinity(res))
                res = domain.IsValid ? domain.Diagonal.Length / 160.0 : 1.0;

            return Math.Max(res, tol * 10.0);
        }

        private static bool ContainsField(SliceSource shell, IEnumerable<SliceSource> infills, IEnumerable<SliceSource> parts)
        {
            if (shell != null && shell.Kind == SliceSourceKind.Field) return true;
            if (infills != null && infills.Any(s => s != null && s.Kind == SliceSourceKind.Field)) return true;
            if (parts != null && parts.Any(s => s != null && s.Kind == SliceSourceKind.Field)) return true;
            return false;
        }

        private static void AccumulateSourceDomain(SliceSource source, ref BoundingBox domain)
        {
            if (source == null || !source.IsValid) return;
            BoundingBox box = source.GetBoundingBox();
            if (!box.IsValid) return;
            if (!domain.IsValid) domain = box;
            else domain.Union(box);
        }

        private static double SafeEvaluate(WasperField field, Point3d point)
        {
            try
            {
                double v = field.Evaluate(point);
                return double.IsNaN(v) || double.IsInfinity(v) ? 1e9 : v;
            }
            catch
            {
                return 1e9;
            }
        }

        private static List<Line> MarchingSquaresSegments(double[] values, int nx, int ny, double xMin, double yMin, double cell, Plane plane)
        {
            var segments = new List<Line>();
            if (values == null || nx < 2 || ny < 2) return segments;

            Func<int, int, int> idx = (i, j) => i + j * nx;
            Func<double, double, Point3d> pointAt = (x, y) => plane.Origin + x * plane.XAxis + y * plane.YAxis;

            for (int j = 0; j < ny - 1; j++)
            {
                double y0 = yMin + j * cell;
                double y1 = yMin + (j + 1) * cell;
                for (int i = 0; i < nx - 1; i++)
                {
                    double x0 = xMin + i * cell;
                    double x1 = xMin + (i + 1) * cell;
                    double v00 = values[idx(i, j)];
                    double v10 = values[idx(i + 1, j)];
                    double v11 = values[idx(i + 1, j + 1)];
                    double v01 = values[idx(i, j + 1)];

                    int code = 0;
                    if (v00 > 0) code |= 1;
                    if (v10 > 0) code |= 2;
                    if (v11 > 0) code |= 4;
                    if (v01 > 0) code |= 8;
                    if (code == 0 || code == 15) continue;

                    Point3d p00 = pointAt(x0, y0);
                    Point3d p10 = pointAt(x1, y0);
                    Point3d p11 = pointAt(x1, y1);
                    Point3d p01 = pointAt(x0, y1);

                    Func<Point3d> e0 = () => LerpIso(p00, p10, v00, v10);
                    Func<Point3d> e1 = () => LerpIso(p10, p11, v10, v11);
                    Func<Point3d> e2 = () => LerpIso(p11, p01, v11, v01);
                    Func<Point3d> e3 = () => LerpIso(p01, p00, v01, v00);

                    switch (code)
                    {
                        case 1: segments.Add(new Line(e3(), e0())); break;
                        case 2: segments.Add(new Line(e0(), e1())); break;
                        case 3: segments.Add(new Line(e3(), e1())); break;
                        case 4: segments.Add(new Line(e1(), e2())); break;
                        case 5: segments.Add(new Line(e3(), e0())); segments.Add(new Line(e1(), e2())); break;
                        case 6: segments.Add(new Line(e0(), e2())); break;
                        case 7: segments.Add(new Line(e3(), e2())); break;
                        case 8: segments.Add(new Line(e2(), e3())); break;
                        case 9: segments.Add(new Line(e0(), e2())); break;
                        case 10: segments.Add(new Line(e0(), e1())); segments.Add(new Line(e2(), e3())); break;
                        case 11: segments.Add(new Line(e1(), e2())); break;
                        case 12: segments.Add(new Line(e3(), e1())); break;
                        case 13: segments.Add(new Line(e0(), e1())); break;
                        case 14: segments.Add(new Line(e3(), e0())); break;
                    }
                }
            }

            return segments;
        }

        private static Point3d LerpIso(Point3d a, Point3d b, double va, double vb)
        {
            double denom = va - vb;
            double t = Math.Abs(denom) < 1e-16 ? 0.5 : va / denom;
            if (t < 0.0) t = 0.0;
            if (t > 1.0) t = 1.0;
            return a + (b - a) * t;
        }

        private static List<Polyline> ChainSegments(List<Line> segments, double tol)
        {
            var result = new List<Polyline>();
            if (segments == null || segments.Count == 0) return result;

            double q = Math.Max(tol, 1e-9);
            Func<Point3d, string> key = p =>
            {
                long x = (long)Math.Round(p.X / q);
                long y = (long)Math.Round(p.Y / q);
                long z = (long)Math.Round(p.Z / q);
                return x + "|" + y + "|" + z;
            };

            var endpointMap = new Dictionary<string, List<int>>();
            for (int i = 0; i < segments.Count; i++)
            {
                string a = key(segments[i].From);
                string b = key(segments[i].To);
                if (!endpointMap.ContainsKey(a)) endpointMap[a] = new List<int>();
                if (!endpointMap.ContainsKey(b)) endpointMap[b] = new List<int>();
                endpointMap[a].Add(i);
                endpointMap[b].Add(i);
            }

            var used = new bool[segments.Count];
            for (int i = 0; i < segments.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;

                var pts = new List<Point3d> { segments[i].From, segments[i].To };
                ExtendPolyline(pts, endpointMap, used, segments, key, tol);
                pts.Reverse();
                ExtendPolyline(pts, endpointMap, used, segments, key, tol);

                if (pts.Count > 2 && pts[0].DistanceTo(pts[pts.Count - 1]) <= tol)
                    pts[pts.Count - 1] = pts[0];

                result.Add(new Polyline(pts));
            }

            return result;
        }

        private static void ExtendPolyline(List<Point3d> pts, Dictionary<string, List<int>> endpointMap, bool[] used, List<Line> segments, Func<Point3d, string> key, double tol)
        {
            while (true)
            {
                Point3d tip = pts[pts.Count - 1];
                string k = key(tip);
                if (!endpointMap.ContainsKey(k)) break;

                int next = -1;
                bool reversed = false;
                foreach (int candidate in endpointMap[k])
                {
                    if (used[candidate]) continue;
                    Line line = segments[candidate];
                    if (line.From.DistanceTo(tip) <= tol) { next = candidate; reversed = false; break; }
                    if (line.To.DistanceTo(tip) <= tol) { next = candidate; reversed = true; break; }
                }

                if (next < 0) break;
                used[next] = true;
                Point3d nextPoint = reversed ? segments[next].From : segments[next].To;
                if (nextPoint.DistanceTo(tip) > 1e-12) pts.Add(nextPoint);
                else break;
            }
        }

        private static string SummarizeSourceTypes(IEnumerable<SliceSource> shells, IEnumerable<SliceSource> infills, IEnumerable<SliceSource> parts)
        {
            int geometry = 0;
            int fields = 0;
            CountSourceTypes(shells, ref geometry, ref fields);
            CountSourceTypes(infills, ref geometry, ref fields);
            CountSourceTypes(parts, ref geometry, ref fields);

            if (geometry > 0 && fields > 0) return "mixed";
            if (fields > 0) return "fields";
            if (geometry > 0) return "geometry";
            return "none";
        }

        private static void CountSourceTypes(IEnumerable<SliceSource> sources, ref int geometry, ref int fields)
        {
            if (sources == null) return;
            foreach (var source in sources)
            {
                if (source == null) continue;
                if (source.Kind == SliceSourceKind.Field) fields++;
                else if (source.Kind == SliceSourceKind.Geometry) geometry++;
            }
        }

        private enum SliceSourceKind
        {
            Geometry,
            Field
        }

        private sealed class SliceSource
        {
            public readonly SliceSourceKind Kind;
            public readonly GeometryBase Geometry;
            public readonly WasperField Field;

            private SliceSource(SliceSourceKind kind, GeometryBase geometry, WasperField field)
            {
                Kind = kind;
                Geometry = geometry;
                Field = field;
            }

            public bool IsValid =>
                Kind == SliceSourceKind.Field
                    ? Field != null && Field.Evaluator != null && Field.Domain.IsValid
                    : Geometry != null && Geometry.IsValid;

            public static SliceSource FromGeometry(GeometryBase geometry) =>
                new SliceSource(SliceSourceKind.Geometry, geometry, null);

            public static SliceSource FromField(WasperField field) =>
                new SliceSource(SliceSourceKind.Field, null, field);

            public BoundingBox GetBoundingBox()
            {
                return Kind == SliceSourceKind.Field
                    ? Field.Domain
                    : Geometry.GetBoundingBox(true);
            }

            public void AddToSignature(ref WasperCacheSignature signature)
            {
                signature.Add((int)Kind);
                if (Kind == SliceSourceKind.Field)
                    signature.Add(Field);
                else
                    signature.Add(Geometry);
            }
        }

        private sealed class ShellSliceCacheEntry
        {
            internal readonly string Key;
            internal readonly int CandidatePlaneCount;
            internal readonly int CulledPlaneCount;
            internal readonly List<Plane> SlicePlanes;
            internal readonly WasperPreparedTrimShell[] PreparedShells;
            internal readonly List<Curve>[] ShellsByLayer;
            internal readonly int[] OpenShellCounts;
            internal readonly int ReversedShellCount;
            internal readonly int AlignedShellSeamCount;
            internal readonly int RejectedShellNormalizationCount;

            internal ShellSliceCacheEntry(
                string key,
                int candidatePlaneCount,
                int culledPlaneCount,
                IEnumerable<Plane> slicePlanes,
                WasperPreparedTrimShell[] preparedShells,
                IReadOnlyList<List<Curve>> shellsByLayer,
                int[] openShellCounts,
                int reversedShellCount,
                int alignedShellSeamCount,
                int rejectedShellNormalizationCount)
            {
                Key = key;
                CandidatePlaneCount = candidatePlaneCount;
                CulledPlaneCount = culledPlaneCount;
                SlicePlanes = slicePlanes.Select(plane => new Plane(plane)).ToList();
                PreparedShells = preparedShells;
                ShellsByLayer = DuplicateCurveLayers(shellsByLayer);
                OpenShellCounts = (int[])openShellCounts.Clone();
                ReversedShellCount = reversedShellCount;
                AlignedShellSeamCount = alignedShellSeamCount;
                RejectedShellNormalizationCount = rejectedShellNormalizationCount;
            }
        }

    }
}
