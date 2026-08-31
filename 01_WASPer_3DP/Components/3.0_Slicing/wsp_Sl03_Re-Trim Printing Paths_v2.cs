#region Component Description
/*
Component: wsp_Sl03_Re-Trim Printing Paths v2
Nickname: ReTrim
Category: WASPer_3DP
SubCategory: 3.0_Slicing
Version:
    Uses the compiled assembly version in the component message via
    _versionTag, following the other compiled components.

VERSION NOTICE (2026-07-28)
This is the v2 generation of Sl03 Re-Trim Printing Paths. The previous v1
generation was moved to Components\3.0_Slicing\Legacy\wsp_Sl03_Re-Trim Printing
Paths_legacy.cs, marked GH_Exposure.hidden, and kept on its original GUID so
existing saved documents keep loading. v2 uses a NEW GUID and:
  - Accepts an optional full_path reference as input 0. Tagged Shell/Infill/
    Partition curves provide per-role fallbacks, while Support, Transition, and
    Undefined reference curves pass through the rebuilt full_path.
  - Renames the shell/infill/partition INPUT nicknames to shell_p/infill_p/
    partition_p, so they read differently from the identically-named outputs
    on the same component.
  - Adds a merged full_path output (shell+infill+partition per layer, same
    convention as Sl02 SlicerPlus v3) plus a right-click "Trim layers" toggle
    controlling its tree shape.
  - Reorders outputs to full_path, la_planes, shell, infill, partition, info.

GENERAL DESCRIPTION
This component applies the Sl02 SlicerPlus per-layer trimming core
(WasperTrimCore, Components\Shared\Geometry\WASPer_TrimCore.cs) to EXISTING printing
paths supplied as data trees, instead of slicing geometry. It enables:

1. Re-trimming paths after downstream edits (Sl04 Trim, Sl06 Orient, manual
   curve edits in Rhino/GH).
2. Mixing a sliced shell with custom curve infill (field lines, external
   patterns, paths from another slicer).
3. Importing per-layer toolpaths and making them consistent with WASPer
   shell/infill/partition semantics before Gcode.

IO CONTRACT
Inputs and outputs mirror Sl02's output contract: full_path / p_path_shell /
p_path_infill / p_path_partition / layer_planes as {layer} or {item;layer}
trees. Branch paths are preserved verbatim, so the component can be inserted
anywhere in the slicing chain without adapters.

BRANCH ALIGNMENT
The union of branch paths across the three curve inputs defines the layer
set; missing branches in the other inputs are treated as empty. layer_planes:
exact path match preferred; a single-branch plane tree broadcasts to all
layers (Sl04 Trim's one-branch rule); otherwise the plane is estimated from
that branch's curves via WasperLayerPlaneTools.

IMPORTANT BEHAVIOR
- Closed shell loops define the containment window. With shell_path_width = 0
  the window equals the raw shell loops, so users feeding already-offset shell
  centerlines should either provide the ORIGINAL outer loops as shell or
  accept centerline-based windows.
- A shell branch with only open curves cannot define a window: open curves
  pass through to p_path_shell and the layer produces no infill/partition
  output (same rule as Sl02).
- Widths = 0 mean pass-through (trim/split only); widths > 0 rebuild
  centerlines / centered contours exactly as Sl02 does.

PATH ROLE METADATA
Output curves are tagged with the shared WASPer.PathRole user-string
(WasperPathRole / WasperPathRoleMetadata, Components\Shared\Geometry\
WASPer_PathRole.cs) same as Sl02 SlicerPlus v3, In10, and Gc01 v2: p_path_shell
curves are tagged Shell, p_path_infill Infill, p_path_partition Partition.
This lets downstream role-aware components (Sl07 Printing Path Visualizer,
Gc11 Visualize Path v2) auto-detect and color re-trimmed paths the same way
they do freshly sliced ones.
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
#endregion

namespace WASPer_3DP.Components._3_0_Slicing
{
    public sealed class wsp_Sl03_ReTrim_Printing_Paths_v2 : GH_Component
    {
        private const string ComponentName = "wsp_Sl03_Re-Trim Printing Paths v2";
        private const string ComponentNickname = "ReTrim";
        private const string ComponentCategory = global::WASPer_3DP.WASPerPalette.DesignFabrication;
        private const string ComponentSubCategory = "3.0_Slicing";

        private readonly string _versionTag;

        private bool _trimLayers = true;
        private const string TrimLayersKey = "wsp_sl03_v2_trim_layers";

        public wsp_Sl03_ReTrim_Printing_Paths_v2()
            : base(
                ComponentName,
                ComponentNickname,
                "Applies the SlicerPlus trimming core to existing printing paths. An optional full_path reference preserves complete layer context and non-trimmed roles; explicit shell_p, infill_p, and partition_p branches override their corresponding tagged reference roles. Tree in, tree out; layer branch structure is preserved.",
                ComponentCategory,
                ComponentSubCategory)
        {
            var asm = Assembly.GetExecutingAssembly();
            var v = asm.GetName().Version;
            _versionTag = v != null ? $"v{v.Major}.{v.Minor}.{v.Build}" : "v1.0.x";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("7AA0F22D-3E22-47A6-B04E-3BFEE6C27732");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon
        {
            get
            {
                try
                {
                    var asm = Assembly.GetExecutingAssembly();
                    using (var stream = asm.GetManifestResourceStream("WASPer_3DP.Resources.Icons.wsp_Sl03_Re-Trim Printing Paths.png"))
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
                    RecordUndoEvent("Toggle Sl03 full_path trim_layers");
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
            pManager.AddCurveParameter(
                "full_path_reference",
                "full_path",
                "Optional complete metadata-aware path from a previous slicer. Tagged Shell, Infill, and Partition curves are used as fallbacks when the corresponding explicit input branch is absent. Support, Transition, Undefined, and other non-trimmed reference curves are preserved in the output full_path. Trimmed {layer} trees are preferred; role-separated {layer;role} trees are normalized when their metadata identifies the role.",
                GH_ParamAccess.tree);
            pManager[0].Optional = true;

            pManager.AddCurveParameter(
                "p_path_shell",
                "shell_p",
                "Existing shell printing paths per layer. Closed loops define the containment window used to trim infill and partitions. Open curves pass through with a remark.\nTREE access; branch structure is preserved.",
                GH_ParamAccess.tree);
            pManager[1].Optional = true;

            pManager.AddCurveParameter(
                "p_path_infill",
                "infill_p",
                "Existing infill printing paths per layer. Trimmed to the shell window and split at partition cutters.\nTREE access; branch structure is preserved.",
                GH_ParamAccess.tree);
            pManager[2].Optional = true;

            pManager.AddCurveParameter(
                "p_path_partition",
                "partition_p",
                "Existing partition/stiffener paths per layer. Trimmed to the shell window. Closed loops become blocking bands; open curves become bands (w_part > 0) or splitters (w_part = 0).\nTREE access; branch structure is preserved.",
                GH_ParamAccess.tree);
            pManager[3].Optional = true;

            pManager.AddPlaneParameter(
                "layer_planes",
                "la_planes",
                "Optional layer plane per branch. Exact branch path match preferred; a single-branch tree broadcasts to all layers. If empty or unmatched, the plane is estimated from that branch's curves.",
                GH_ParamAccess.tree);
            pManager[4].Optional = true;

            pManager.AddNumberParameter(
                "shell_path_width",
                "w_shell",
                "Shell bead/path width. 0 = pass shell loops through unchanged (window equals the raw loops). >0 = rebuild shell centerlines from the loops, and inset the window by n_shell * w_shell.",
                GH_ParamAccess.item,
                0.0);

            pManager.AddIntegerParameter(
                "shell_n_contours",
                "n_shell",
                "Number of shell contours. Only used when w_shell > 0; also sets the window inset (n_shell * w_shell).",
                GH_ParamAccess.item,
                1);

            pManager.AddNumberParameter(
                "infill_path_width",
                "w_infill",
                "Infill contour spacing. 0 = trim/split only. >0 = re-contour accepted infill pieces (centered contours), then re-split and re-cull.",
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
                "Partition bead/path width. 0 = split-only partitions (open curves act as splitters). >0 = partition centerlines plus blocking bands that remove infill.",
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
        }

        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
            pManager.AddCurveParameter(
                "full_path",
                "full_path",
                "Complete per-layer toolpath merging shell, infill, and partition curves in that order. " +
                "Non-trimmed Support, Transition, and Undefined curves from the optional full_path reference are preserved after those roles. " +
                "Right-click 'Trim layers' controls the tree shape: trimmed (default) collapses each layer " +
                "into a single flat branch {layer}; untrimmed keeps {layer;0=shell,1=infill,2=partition} " +
                "and {layer;3=preserved reference} sub-branches. Curves are collected only, not joined or reordered for travel moves.",
                GH_ParamAccess.tree);
            pManager.AddPlaneParameter("layer_planes", "la_planes", "Layer plane per branch (echoed input, broadcast, or estimated).", GH_ParamAccess.tree);
            pManager.AddCurveParameter("p_path_shell", "shell", "Re-trimmed shell paths. Same branch structure as the inputs.", GH_ParamAccess.tree);
            pManager.AddCurveParameter("p_path_infill", "infill", "Re-trimmed infill paths. Same branch structure as the inputs.", GH_ParamAccess.tree);
            pManager.AddCurveParameter("p_path_partition", "partition", "Re-trimmed partition paths. Same branch structure as the inputs.", GH_ParamAccess.tree);
            pManager.AddTextParameter("info", "info", "Status information.", GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess DA)
        {
            Message = _versionTag;

            GH_Structure<GH_Curve> fullReferenceTree = null;
            GH_Structure<GH_Curve> shellTree = null;
            GH_Structure<GH_Curve> infillTree = null;
            GH_Structure<GH_Curve> partTree = null;
            GH_Structure<GH_Plane> planeTree = null;

            DA.GetDataTree(0, out fullReferenceTree);
            DA.GetDataTree(1, out shellTree);
            DA.GetDataTree(2, out infillTree);
            DA.GetDataTree(3, out partTree);
            DA.GetDataTree(4, out planeTree);

            double shellPathWidth = 0.0;
            int shellNContours = 1;
            double infillPathWidth = 0.0;
            int infillNContours = 1;
            double partitionPathWidth = 0.0;
            int partitionNContours = 1;
            bool cleanShort = false;
            double cleanLen = 1.0;

            DA.GetData(5, ref shellPathWidth);
            DA.GetData(6, ref shellNContours);
            DA.GetData(7, ref infillPathWidth);
            DA.GetData(8, ref infillNContours);
            DA.GetData(9, ref partitionPathWidth);
            DA.GetData(10, ref partitionNContours);
            DA.GetData(11, ref cleanShort);
            DA.GetData(12, ref cleanLen);

            double tol = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 1e-6;

            shellNContours = Math.Max(1, shellNContours);
            infillNContours = Math.Max(1, infillNContours);
            partitionNContours = Math.Max(1, partitionNContours);

            double minLen = cleanShort ? Math.Max(cleanLen, 2.0 * tol) : 2.0 * tol;

            var outFullPath = new GH_Structure<GH_Curve>();
            var outShell = new GH_Structure<GH_Curve>();
            var outInfill = new GH_Structure<GH_Curve>();
            var outPart = new GH_Structure<GH_Curve>();
            var outPlanes = new GH_Structure<GH_Plane>();

            // Union of explicit role branches and normalized full_path reference layers.
            var branchPaths = new List<GH_Path>();
            var seen = new HashSet<string>();
            int rejected = 0;
            CollectPaths(shellTree, branchPaths, seen);
            CollectPaths(infillTree, branchPaths, seen);
            CollectPaths(partTree, branchPaths, seen);
            Dictionary<string, ReferenceLayer> referenceLayers = BuildReferenceLayers(
                fullReferenceTree,
                branchPaths,
                ref rejected);
            foreach (ReferenceLayer reference in referenceLayers.Values)
            {
                if (seen.Add(reference.Path.ToString()))
                    branchPaths.Add(reference.Path);
            }
            branchPaths.Sort();

            if (branchPaths.Count == 0)
            {
                SetOutputs(DA, outFullPath, outPlanes, outShell, outInfill, outPart, "No input paths.");
                return;
            }

            bool planeBroadcast = planeTree != null && planeTree.PathCount == 1 && planeTree.Branches[0].Count > 0;

            int layerCount = branchPaths.Count;
            var shellByLayer = new List<Curve>[layerCount];
            var infillByLayer = new List<Curve>[layerCount];
            var partByLayer = new List<Curve>[layerCount];
            var planeByLayer = new Plane[layerCount];
            var planeEstimated = new bool[layerCount];
            var openOnlyShell = new bool[layerCount];
            var nonPlanarCounts = new int[layerCount];

            // Gather branch data sequentially (GH_Structure is not thread-safe).
            var shellIn = new List<Curve>[layerCount];
            var infillIn = new List<Curve>[layerCount];
            var partIn = new List<Curve>[layerCount];
            var preservedReferenceIn = new List<Curve>[layerCount];
            int referenceFallbackBranches = 0;
            int preservedReferenceCurves = 0;

            for (int i = 0; i < layerCount; i++)
            {
                var path = branchPaths[i];
                referenceLayers.TryGetValue(path.ToString(), out ReferenceLayer reference);

                bool hasExplicitShell = shellTree != null && shellTree.PathExists(path);
                bool hasExplicitInfill = infillTree != null && infillTree.PathExists(path);
                bool hasExplicitPart = partTree != null && partTree.PathExists(path);
                shellIn[i] = hasExplicitShell
                    ? BranchCurves(shellTree, path, ref rejected)
                    : DuplicateCurves(reference?.Shell);
                infillIn[i] = hasExplicitInfill
                    ? BranchCurves(infillTree, path, ref rejected)
                    : DuplicateCurves(reference?.Infill);
                partIn[i] = hasExplicitPart
                    ? BranchCurves(partTree, path, ref rejected)
                    : DuplicateCurves(reference?.Partition);
                preservedReferenceIn[i] = DuplicateCurves(reference?.Other);

                if ((!hasExplicitShell && shellIn[i].Count > 0) ||
                    (!hasExplicitInfill && infillIn[i].Count > 0) ||
                    (!hasExplicitPart && partIn[i].Count > 0))
                    referenceFallbackBranches++;
                preservedReferenceCurves += preservedReferenceIn[i].Count;

                planeByLayer[i] = ResolvePlane(
                    planeTree, path, planeBroadcast,
                    shellIn[i], infillIn[i], partIn[i], preservedReferenceIn[i],
                    tol, out planeEstimated[i]);
            }

            bool anyPartitionCurves = partIn.Any(list => list != null && list.Count > 0);
            if (anyPartitionCurves && !(partitionPathWidth > RhinoMath.ZeroTolerance))
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Partitions detected but w_part is 0 (default). Partitions act as split-only cutters and no blocking bands are built. To trim with blocking bands, set w_part > 0.");

            double planarityTol = WasperLayerPlaneTools.PlanarityWarningTolerance(tol);

            Action<int> work = i =>
            {
                Plane pl = planeByLayer[i];

                nonPlanarCounts[i] =
                    CountNonPlanar(shellIn[i], pl, planarityTol) +
                    CountNonPlanar(infillIn[i], pl, planarityTol) +
                    CountNonPlanar(partIn[i], pl, planarityTol) +
                    CountNonPlanar(preservedReferenceIn[i], pl, planarityTol);

                bool hasShell = shellIn[i].Count > 0;
                var shellClosed = new List<Curve>();
                var shellOpen = new List<Curve>();

                if (hasShell)
                {
                    shellClosed = WasperTrimCore.CloseAndCullCurves(shellIn[i], tol, minLen, out shellOpen);
                    openOnlyShell[i] = shellClosed.Count == 0;
                }

                var partitionGroups = new List<List<Curve>> { partIn[i] };
                var infillGroups = new List<List<Curve>> { infillIn[i] };

                List<Curve> localShell, localInfill, localPart;
                WasperTrimCore.TrimLayer(
                    hasShell,
                    shellClosed,
                    shellOpen,
                    partitionGroups,
                    infillGroups,
                    pl,
                    shellPathWidth,
                    shellNContours,
                    infillPathWidth,
                    infillNContours,
                    partitionPathWidth,
                    partitionNContours,
                    tol,
                    minLen,
                    out localShell,
                    out localInfill,
                    out localPart);

                shellByLayer[i] = localShell;
                infillByLayer[i] = localInfill;
                partByLayer[i] = localPart;
            };

            if (layerCount >= 4)
                Parallel.For(0, layerCount, work);
            else
                for (int i = 0; i < layerCount; i++) work(i);

            for (int i = 0; i < layerCount; i++)
            {
                var path = branchPaths[i];

                TagCurves(shellByLayer[i], global::WASPer_3DP.WasperPathRole.Shell);
                TagCurves(infillByLayer[i], global::WASPer_3DP.WasperPathRole.Infill);
                TagCurves(partByLayer[i], global::WASPer_3DP.WasperPathRole.Partition);

                outPlanes.Append(new GH_Plane(planeByLayer[i]), path);

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
                if (_trimLayers)
                {
                    var merged = new List<Curve>();
                    if (shellByLayer[i] != null) merged.AddRange(shellByLayer[i]);
                    if (infillByLayer[i] != null) merged.AddRange(infillByLayer[i]);
                    if (partByLayer[i] != null) merged.AddRange(partByLayer[i]);
                    if (preservedReferenceIn[i] != null) merged.AddRange(preservedReferenceIn[i]);

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

                    if (preservedReferenceIn[i] != null && preservedReferenceIn[i].Count > 0)
                        outFullPath.AppendRange(preservedReferenceIn[i].Select(c => new GH_Curve(c)), path.AppendElement(3));
                }
            }

            int estimatedCount = planeEstimated.Count(b => b);
            int openOnlyCount = openOnlyShell.Count(b => b);
            int nonPlanarTotal = nonPlanarCounts.Sum();

            if (rejected > 0)
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, $"{rejected} null/invalid curve(s) ignored.");

            if (openOnlyCount > 0)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{openOnlyCount} layer(s) have only OPEN shell curves: no containment window, open curves passed through, infill/partitions dropped on those layers (Sl02 rule).");

            if (nonPlanarTotal > 0)
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{nonPlanarTotal} curve(s) deviate from their layer plane by more than {planarityTol:0.###}. They are processed anyway; containment tests use the plane projection.");

            string info =
                $"Layers: {layerCount}\n" +
                $"Planes: {(planeTree == null || planeTree.IsEmpty ? "estimated" : planeBroadcast ? "single-branch broadcast" : "matched by branch path")}" +
                (estimatedCount > 0 ? $" ({estimatedCount} estimated)\n" : "\n") +
                $"Open-only shell layers: {openOnlyCount}\n" +
                $"Non-planar curves: {nonPlanarTotal}\n" +
                $"Reference role fallbacks: {referenceFallbackBranches} branch role(s)\n" +
                $"Preserved reference curves: {preservedReferenceCurves}\n" +
                $"Shell w/n: {shellPathWidth}/{shellNContours}  Infill w/n: {infillPathWidth}/{infillNContours}  Partition w/n: {partitionPathWidth}/{partitionNContours}";

            SetOutputs(DA, outFullPath, outPlanes, outShell, outInfill, outPart, info);
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

        private static void CollectPaths(GH_Structure<GH_Curve> tree, List<GH_Path> paths, HashSet<string> seen)
        {
            if (tree == null) return;

            foreach (var path in tree.Paths)
            {
                if (path == null) continue;
                if (seen.Add(path.ToString()))
                    paths.Add(path);
            }
        }

        private sealed class ReferenceLayer
        {
            public GH_Path Path;
            public readonly List<Curve> Shell = new List<Curve>();
            public readonly List<Curve> Infill = new List<Curve>();
            public readonly List<Curve> Partition = new List<Curve>();
            public readonly List<Curve> Other = new List<Curve>();
        }

        private static Dictionary<string, ReferenceLayer> BuildReferenceLayers(
            GH_Structure<GH_Curve> tree,
            IList<GH_Path> explicitPaths,
            ref int rejected)
        {
            var result = new Dictionary<string, ReferenceLayer>();
            if (tree == null || tree.IsEmpty)
                return result;

            var explicitKeys = new HashSet<string>(
                (explicitPaths ?? Array.Empty<GH_Path>())
                    .Where(path => path != null)
                    .Select(path => path.ToString()));
            HashSet<string> roleSeparatedParents = DetectRoleSeparatedParents(tree);

            for (int b = 0; b < tree.PathCount; b++)
            {
                GH_Path sourcePath = tree.Paths[b];
                IList<GH_Curve> branch = tree.Branches[b];
                GH_Path layerPath = NormalizeReferencePath(
                    sourcePath,
                    branch,
                    explicitKeys,
                    roleSeparatedParents);
                string key = layerPath.ToString();
                if (!result.TryGetValue(key, out ReferenceLayer layer))
                {
                    layer = new ReferenceLayer { Path = layerPath };
                    result.Add(key, layer);
                }

                if (branch == null)
                    continue;
                foreach (GH_Curve goo in branch)
                {
                    Curve curve = goo?.Value;
                    if (curve == null || !curve.IsValid)
                    {
                        if (goo != null) rejected++;
                        continue;
                    }

                    switch (global::WASPer_3DP.WasperPathRoleMetadata.Get(curve))
                    {
                        case global::WASPer_3DP.WasperPathRole.Shell:
                            layer.Shell.Add(curve);
                            break;
                        case global::WASPer_3DP.WasperPathRole.Infill:
                            layer.Infill.Add(curve);
                            break;
                        case global::WASPer_3DP.WasperPathRole.Partition:
                            layer.Partition.Add(curve);
                            break;
                        default:
                            layer.Other.Add(curve);
                            break;
                    }
                }
            }
            return result;
        }

        private static GH_Path NormalizeReferencePath(
            GH_Path sourcePath,
            IList<GH_Curve> branch,
            HashSet<string> explicitKeys,
            HashSet<string> roleSeparatedParents)
        {
            if (sourcePath == null)
                return new GH_Path(0);
            if (explicitKeys.Contains(sourcePath.ToString()) || sourcePath.Length < 2)
                return new GH_Path(sourcePath.Indices);

            GH_Path parent = new GH_Path(sourcePath.Indices.Take(sourcePath.Length - 1).ToArray());
            if (explicitKeys.Contains(parent.ToString()))
                return parent;

            int suffix = sourcePath[sourcePath.Length - 1];
            bool roleSeparated = roleSeparatedParents.Contains(parent.ToString()) &&
                BranchMatchesRoleSuffix(branch, suffix);
            return roleSeparated ? parent : new GH_Path(sourcePath.Indices);
        }

        private static HashSet<string> DetectRoleSeparatedParents(
            GH_Structure<GH_Curve> tree)
        {
            var suffixesByParent = new Dictionary<string, HashSet<int>>();
            if (tree == null)
                return new HashSet<string>();

            for (int b = 0; b < tree.PathCount; b++)
            {
                GH_Path path = tree.Paths[b];
                if (path == null || path.Length < 2)
                    continue;
                int suffix = path[path.Length - 1];
                if (!BranchMatchesRoleSuffix(tree.Branches[b], suffix))
                    continue;
                GH_Path parent = new GH_Path(path.Indices.Take(path.Length - 1).ToArray());
                string key = parent.ToString();
                if (!suffixesByParent.TryGetValue(key, out HashSet<int> suffixes))
                {
                    suffixes = new HashSet<int>();
                    suffixesByParent.Add(key, suffixes);
                }
                suffixes.Add(suffix);
            }

            return new HashSet<string>(
                suffixesByParent
                    .Where(pair => pair.Value.Count >= 2)
                    .Select(pair => pair.Key));
        }

        private static bool BranchMatchesRoleSuffix(
            IList<GH_Curve> branch,
            int suffix)
        {
            return branch != null && branch.Count > 0 && suffix >= 0 && suffix <= 3 &&
                branch.All(goo =>
                {
                    if (goo?.Value == null)
                        return false;
                    global::WASPer_3DP.WasperPathRole role =
                        global::WASPer_3DP.WasperPathRoleMetadata.Get(goo.Value);
                    return suffix switch
                    {
                        0 => role == global::WASPer_3DP.WasperPathRole.Shell,
                        1 => role == global::WASPer_3DP.WasperPathRole.Infill,
                        2 => role == global::WASPer_3DP.WasperPathRole.Partition,
                        3 => role != global::WASPer_3DP.WasperPathRole.Shell &&
                             role != global::WASPer_3DP.WasperPathRole.Infill &&
                             role != global::WASPer_3DP.WasperPathRole.Partition,
                        _ => false
                    };
                });
        }

        private static List<Curve> DuplicateCurves(IEnumerable<Curve> source)
        {
            var result = new List<Curve>();
            if (source == null)
                return result;
            foreach (Curve curve in source)
            {
                if (curve == null || !curve.IsValid)
                    continue;
                Curve duplicate = curve.DuplicateCurve();
                if (duplicate == null || !duplicate.IsValid)
                    continue;
                global::WASPer_3DP.WasperPathRoleMetadata.Copy(curve, duplicate);
                result.Add(duplicate);
            }
            return result;
        }

        private static List<Curve> BranchCurves(GH_Structure<GH_Curve> tree, GH_Path path, ref int rejected)
        {
            var result = new List<Curve>();
            if (tree == null || !tree.PathExists(path)) return result;

            foreach (var goo in tree.get_Branch(path))
            {
                var ghCurve = goo as GH_Curve;
                var curve = ghCurve?.Value;

                if (curve == null || !curve.IsValid)
                {
                    if (goo != null) rejected++;
                    continue;
                }

                result.Add(curve);
            }

            return result;
        }

        private static Plane ResolvePlane(
            GH_Structure<GH_Plane> planeTree,
            GH_Path path,
            bool broadcast,
            List<Curve> shellCurves,
            List<Curve> infillCurves,
            List<Curve> partCurves,
            List<Curve> referenceCurves,
            double tol,
            out bool estimated)
        {
            estimated = false;

            if (planeTree != null && !planeTree.IsEmpty)
            {
                if (planeTree.PathExists(path))
                {
                    var branch = planeTree.get_Branch(path);
                    foreach (var goo in branch)
                        if (goo is GH_Plane ghPlane && ghPlane.Value.IsValid)
                            return ghPlane.Value;
                }

                if (broadcast)
                {
                    foreach (var goo in planeTree.Branches[0])
                        if (goo is GH_Plane ghPlane && ghPlane.Value.IsValid)
                            return ghPlane.Value;
                }
            }

            // Fall back to estimating the plane from this branch's curves,
            // preferring shell, then infill, then partitions.
            estimated = true;
            var source = shellCurves.Count > 0 ? shellCurves
                       : infillCurves.Count > 0 ? infillCurves
                       : partCurves.Count > 0 ? partCurves
                       : referenceCurves;

            return WasperLayerPlaneTools.EstimateLayerPlane(source, tol);
        }

        private static int CountNonPlanar(List<Curve> curves, Plane plane, double planarityTol)
        {
            if (curves == null || curves.Count == 0) return 0;

            double deviation = WasperLayerPlaneTools.MaxDeviationFromPlane(curves, plane);
            if (deviation <= planarityTol) return 0;

            int count = 0;
            foreach (var curve in curves)
            {
                var single = new List<Curve> { curve };
                if (WasperLayerPlaneTools.MaxDeviationFromPlane(single, plane) > planarityTol)
                    count++;
            }

            return count;
        }
    }
}
