// wsp_Pp02_Path Translator.cs
// WASPer_3DP - Subcategory: 4.0_Print Paths

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP.Components._4_0_Print_Paths
{
    /// <summary>
    /// Lossless, non-resampling gate between packed WASPer paths and polyline curves.
    /// </summary>
    public sealed class wsp_Pp02_Path_Translator : GH_Component
    {
        private const string CacheKey = "WASPer.PathTranslator.CacheId";
        private const string BranchKey = "WASPer.PathTranslator.Branch";
        private const string CountKey = "WASPer.PathTranslator.PointCount";
        private const int CacheCapacity = 64;

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, WasperPrintPath> PathCache =
            new Dictionary<string, WasperPrintPath>(StringComparer.Ordinal);
        private static readonly Queue<string> CacheOrder = new Queue<string>();

        private readonly string _versionTag;

        public wsp_Pp02_Path_Translator()
            : base(
                "wsp_Pp02_Path Translator",
                "Path Translator",
                "Fast two-way gate between a packed WASPer Print Path and polyline curves. Both " +
                "directions are independent, so either input or both inputs may be connected.\n\n" +
                "wsp_path -> crvs_path emits one polyline per path branch without resampling. " +
                "Branch paths, point order, segment lengths, semantic roles, and an in-session " +
                "snapshot of the complete WASPer path are retained.\n\n" +
                "crvs_path -> wsp_path accepts polylines only. Translator-generated curves restore " +
                "the complete original path when unchanged; same-topology edits preserve fabrication " +
                "metadata while clearing geometry-derived analysis. New polylines create a partial " +
                "path with lightweight tangent frames and any WASPer.PathRole metadata already present.\n\n" +
                "TREE STRUCTURE:\n" +
                "WASPer stores one printing curve per canonical branch, normally {layer;curve}. " +
                "With trim_tree=true (default), crvs_path and roles are grouped as one branch per " +
                "logical layer: {layer}[curve items] (or {prefix;layer} for grafted trees). Any path " +
                "dimensions after the layer are collapsed. When those grouped curves are packed again, each item " +
                "is expanded back to {layer;curve}. With trim_tree=false, existing paths are retained; " +
                "a branch containing multiple input curves is still expanded by appending the item index.\n\n" +
                "Use Pp01 for smooth curves or whenever subdivision, layer-height calculation, flow " +
                "calculation, or full path analysis is required.",
                WASPerPalette.DesignFabrication,
                "4.0_Print Paths")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _versionTag = version == null
                ? "v1.0.x"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
            Message = _versionTag;
        }

        public override Guid ComponentGuid =>
            new Guid("E5084BFA-7C25-4E83-9C42-F31441D8773F");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon => CreateIcon();

        protected override void RegisterInputParams(GH_InputParamManager p)
        {
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "Optional packed WASPer Print Path to translate directly into polyline curves.",
                GH_ParamAccess.item);
            p[0].Optional = true;

            p.AddCurveParameter(
                "crvs_path",
                "crvs_path",
                "Optional polyline curve tree to pack as a WASPer Print Path. With trim_tree=true, each input branch is interpreted as one layer and its curve items become separate canonical {layer;curve} branches. Smooth curves are deliberately rejected; use Pp01 when resampling is required.",
                GH_ParamAccess.tree);
            p[1].Optional = true;

            p.AddBooleanParameter(
                "trim_tree",
                "trim_tree",
                "Tree presentation mode. True (default): group crvs_path and roles into one branch per logical layer, retaining any common grafted prefix and collapsing all later curve/piece dimensions; when packing curves, append an item index so the wsp_path keeps one curve per canonical branch. False: preserve existing branch paths whenever each branch contains one curve.",
                GH_ParamAccess.item,
                true);
        }

        protected override void RegisterOutputParams(GH_OutputParamManager p)
        {
            p.AddCurveParameter(
                "crvs_path",
                "crvs_path",
                "Polyline curves translated from wsp_path. Every point-plane origin becomes exactly one vertex, so point order and segment lengths are unchanged. With trim_tree=true, paths such as {layer;curve} or {layer;curve;piece} are grouped into {layer}, with one curve per item; common grafted prefixes are retained. Item order follows the original curve-branch order.",
                GH_ParamAccess.tree);
            p.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "Packed WASPer Print Path translated from crvs_path without subdivision. Every input polyline becomes one canonical path branch. Grouped {layer}[curve items] input is expanded to {layer;curve}; unchanged translator curves restore the complete original path metadata.",
                GH_ParamAccess.item);
            p.AddIntegerParameter(
                "roles",
                "roles",
                "Integer role for every translated curve: 0 Undefined, 1 Shell, 2 Infill, 3 Partition, 4 Support, 5 Transition. Paths and item indices match crvs_path exactly when wsp_path is supplied. If only crvs_path is supplied, roles match the canonical branches packed into the output wsp_path.",
                GH_ParamAccess.tree);
        }

        protected override void SolveInstance(IGH_DataAccess da)
        {
            WasperPrintPath inputPath = null;
            bool hasPath = WasperGcodeTreeUtil.TryGetPrintPath(da, 0, out inputPath) && inputPath != null;

            GH_Structure<GH_Curve> inputCurves = null;
            bool hasCurves = da.GetDataTree(1, out inputCurves) &&
                             inputCurves != null &&
                             inputCurves.DataCount > 0;
            bool trimTree = true;
            da.GetData(2, ref trimTree);

            if (!hasPath && !hasCurves)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Supply wsp_path, crvs_path, or both. Each direction is evaluated independently.");
                Message = $"{_versionTag} | waiting";
                return;
            }

            GH_Structure<GH_Integer> rolesFromPath = null;
            if (hasPath)
            {
                GH_Structure<GH_Curve> outputCurves = TranslateToCurves(
                    inputPath,
                    trimTree,
                    out rolesFromPath);
                da.SetDataTree(0, outputCurves);
            }

            GH_Structure<GH_Integer> rolesFromCurves = null;
            if (hasCurves)
            {
                if (!TryTranslateToPath(
                        inputCurves,
                        trimTree,
                        out WasperPrintPath outputPath,
                        out rolesFromCurves))
                {
                    Message = $"{_versionTag} | invalid curves";
                    return;
                }

                da.SetData(1, new WasperPrintPathGoo(outputPath));
            }

            da.SetDataTree(2, rolesFromPath ?? rolesFromCurves ?? new GH_Structure<GH_Integer>());
            Message = hasPath && hasCurves
                ? $"{_versionTag} | two-way"
                : hasPath
                    ? $"{_versionTag} | path -> curves"
                    : $"{_versionTag} | curves -> path";
        }

        private GH_Structure<GH_Curve> TranslateToCurves(
            WasperPrintPath path,
            bool trimTree,
            out GH_Structure<GH_Integer> roles)
        {
            var curves = new GH_Structure<GH_Curve>();
            roles = new GH_Structure<GH_Integer>();

            if (path.PtPlanes == null || path.PtPlanes.BranchCount == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, "wsp_path contains no point planes.");
                return curves;
            }

            string cacheId = Guid.NewGuid().ToString("N");
            Remember(cacheId, path);
            int commonPrefixLength = WasperGcodeTreeUtil.CommonPathPrefixLength(path.PtPlanes.Paths);

            for (int branchIndex = 0; branchIndex < path.PtPlanes.BranchCount; branchIndex++)
            {
                GH_Path treePath = path.PtPlanes.Path(branchIndex);
                IList<Plane> planes = path.PtPlanes.Branch(branchIndex);
                if (planes == null || planes.Count < 2)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"wsp_path branch {treePath} has fewer than two point planes and cannot form a curve.");
                    continue;
                }

                var polyline = new Polyline(planes.Count);
                for (int i = 0; i < planes.Count; i++)
                    polyline.Add(planes[i].Origin);

                Curve curve = new PolylineCurve(polyline);
                CopyUserStrings(SourceCurve(path.SourceCurves, treePath, branchIndex), curve);

                WasperPathRole role = BranchRole(path.PathRoles, treePath, branchIndex);
                WasperPathRoleMetadata.Set(curve, role);
                curve.SetUserString(CacheKey, cacheId);
                curve.SetUserString(BranchKey, branchIndex.ToString());
                curve.SetUserString(CountKey, planes.Count.ToString());

                GH_Path outputPath = trimTree
                    ? WasperGcodeTreeUtil.LayerPlanePath(treePath, commonPrefixLength)
                    : treePath;
                curves.Append(new GH_Curve(curve), outputPath);
                roles.Append(new GH_Integer((int)role), outputPath);
            }

            return curves;
        }

        private bool TryTranslateToPath(
            GH_Structure<GH_Curve> input,
            bool trimTree,
            out WasperPrintPath output,
            out GH_Structure<GH_Integer> roles)
        {
            output = null;
            roles = new GH_Structure<GH_Integer>();

            var entries = new List<CurveEntry>();
            for (int branchIndex = 0; branchIndex < input.PathCount; branchIndex++)
            {
                GH_Path inputPath = input.Paths[branchIndex];
                IList<GH_Curve> branch = input.Branches[branchIndex];
                for (int itemIndex = 0; itemIndex < branch.Count; itemIndex++)
                {
                    Curve curve = branch[itemIndex]?.Value;
                    if (curve == null || !curve.IsValid)
                        continue;

                    if (!curve.TryGetPolyline(out Polyline polyline) || polyline.Count < 2)
                    {
                        AddRuntimeMessage(
                            GH_RuntimeMessageLevel.Error,
                            $"crvs_path {inputPath}, item {itemIndex} is not a valid polyline. " +
                            "Path Translator never resamples curves; use Pp01 for smooth curves.");
                        return false;
                    }

                    GH_Path outputTreePath = !trimTree && branch.Count == 1
                        ? inputPath
                        : inputPath.AppendElement(itemIndex);
                    entries.Add(new CurveEntry(curve, polyline, outputTreePath));
                }
            }

            if (entries.Count == 0)
            {
                AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "crvs_path contains no valid polyline curves.");
                return false;
            }

            if (TryRestoreCached(entries, out output, out bool geometryChanged))
            {
                roles = BuildRoleTree(output);
                if (geometryChanged)
                {
                    AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Remark,
                        "Translator curves were edited with unchanged vertex topology. Fabrication metadata was retained; geometry-derived analysis and motion/KPI data were cleared.");
                }
                return true;
            }

            var planes = new DataTree<Plane>();
            var sourceCurves = new DataTree<Curve>();
            var pathRoles = new DataTree<int>();

            foreach (CurveEntry entry in entries)
            {
                Plane curvePlane;
                bool planar = entry.Curve.TryGetPlane(out curvePlane);
                Vector3d preferredNormal = planar ? curvePlane.ZAxis : Vector3d.ZAxis;

                for (int i = 0; i < entry.Polyline.Count; i++)
                    planes.Add(BuildFrame(entry.Polyline, i, preferredNormal), entry.Path);

                Curve duplicate = entry.Curve.DuplicateCurve();
                WasperPathRole role = WasperPathRoleMetadata.Get(entry.Curve);
                WasperPathRoleMetadata.Set(duplicate, role);
                sourceCurves.Add(duplicate, entry.Path);
                pathRoles.Add((int)role, entry.Path);
                roles.Append(new GH_Integer((int)role), entry.Path);
            }

            output = new WasperPrintPath(
                null,
                planes,
                null,
                null,
                sourceCurves: sourceCurves,
                pathRoles: pathRoles,
                isPartial: true);

            AddRuntimeMessage(
                GH_RuntimeMessageLevel.Remark,
                "New polylines were packed as a partial wsp_path. Run Pp01 when flow, layer height, width, or analysis fields are required.");
            return true;
        }

        private static bool TryRestoreCached(
            IList<CurveEntry> entries,
            out WasperPrintPath path,
            out bool geometryChanged)
        {
            path = null;
            geometryChanged = false;
            string cacheId = entries[0].Curve.GetUserString(CacheKey);
            if (string.IsNullOrWhiteSpace(cacheId))
                return false;

            for (int i = 1; i < entries.Count; i++)
            {
                if (!string.Equals(cacheId, entries[i].Curve.GetUserString(CacheKey), StringComparison.Ordinal))
                    return false;
            }

            WasperPrintPath cached = Recall(cacheId);
            if (cached?.PtPlanes == null || entries.Count != cached.PtPlanes.BranchCount)
                return false;

            var byBranch = new Dictionary<int, CurveEntry>();
            foreach (CurveEntry entry in entries)
            {
                if (!int.TryParse(entry.Curve.GetUserString(BranchKey), out int branchIndex) ||
                    branchIndex < 0 ||
                    branchIndex >= cached.PtPlanes.BranchCount ||
                    byBranch.ContainsKey(branchIndex))
                    return false;
                byBranch.Add(branchIndex, entry);
            }

            double docTolerance = RhinoDoc.ActiveDoc?.ModelAbsoluteTolerance ?? 0.001;
            double compareTolerance = Math.Max(1e-9, docTolerance * 1e-6);
            var replacementPlanes = new DataTree<Plane>();
            var replacementCurves = new DataTree<Curve>();
            var replacementRoles = new DataTree<int>();

            for (int branchIndex = 0; branchIndex < cached.PtPlanes.BranchCount; branchIndex++)
            {
                IList<Plane> oldPlanes = cached.PtPlanes.Branch(branchIndex);
                CurveEntry entry = byBranch[branchIndex];
                if (oldPlanes.Count != entry.Polyline.Count)
                    return false;

                GH_Path treePath = cached.PtPlanes.Path(branchIndex);
                WasperPathRole curveRole = WasperPathRoleMetadata.Get(entry.Curve);
                WasperPathRole cachedRole = BranchRole(cached.PathRoles, treePath, branchIndex);
                if (curveRole != cachedRole)
                    geometryChanged = true;

                for (int i = 0; i < oldPlanes.Count; i++)
                {
                    Point3d point = entry.Polyline[i];
                    if (point.DistanceTo(oldPlanes[i].Origin) > compareTolerance)
                        geometryChanged = true;

                    Plane plane = oldPlanes[i];
                    plane.Origin = point;
                    replacementPlanes.Add(plane, treePath);
                }

                replacementCurves.Add(entry.Curve.DuplicateCurve(), treePath);
                replacementRoles.Add((int)curveRole, treePath);
            }

            if (!geometryChanged)
            {
                path = cached;
                return true;
            }

            path = CloneCoreWithGeometry(
                cached,
                replacementPlanes,
                replacementCurves,
                replacementRoles);
            return true;
        }

        private static WasperPrintPath CloneCoreWithGeometry(
            WasperPrintPath source,
            DataTree<Plane> planes,
            DataTree<Curve> sourceCurves,
            DataTree<int> pathRoles)
        {
            return new WasperPrintPath(
                null,
                planes,
                source.Flows,
                source.LayerH,
                printSpeed: source.PrintSpeed,
                nozzleDiam: source.NozzleDiam,
                layerW: source.LayerW,
                layerWf: source.LayerWf,
                travelSpeed: source.TravelSpeed,
                zHop: source.ZHop,
                zHopSpeed: source.ZHopSpeed,
                isPartial: true,
                sourceCurves: sourceCurves,
                pathRoles: pathRoles,
                strokeIds: source.StrokeIds);
        }

        private static Plane BuildFrame(Polyline polyline, int index, Vector3d preferredNormal)
        {
            Point3d point = polyline[index];
            Vector3d tangent;
            if (index == 0)
                tangent = polyline[1] - polyline[0];
            else if (index == polyline.Count - 1)
                tangent = polyline[index] - polyline[index - 1];
            else
                tangent = polyline[index + 1] - polyline[index - 1];

            if (!tangent.Unitize())
                tangent = Vector3d.XAxis;

            Vector3d normal = preferredNormal;
            normal -= Vector3d.Multiply(normal * tangent, tangent);
            if (!normal.Unitize())
            {
                normal = Math.Abs(tangent * Vector3d.ZAxis) < 0.95
                    ? Vector3d.ZAxis
                    : Vector3d.YAxis;
                normal -= Vector3d.Multiply(normal * tangent, tangent);
                normal.Unitize();
            }

            Vector3d yAxis = Vector3d.CrossProduct(normal, tangent);
            if (!yAxis.Unitize())
                yAxis = Vector3d.YAxis;
            return new Plane(point, tangent, yAxis);
        }

        private static WasperPathRole BranchRole(DataTree<int> tree, GH_Path path, int branchIndex)
        {
            if (tree == null || tree.BranchCount == 0)
                return WasperPathRole.Undefined;

            IList<int> values = tree.PathExists(path)
                ? tree.Branch(path)
                : branchIndex < tree.BranchCount
                    ? tree.Branch(branchIndex)
                    : null;
            if (values == null || values.Count == 0 || !Enum.IsDefined(typeof(WasperPathRole), values[0]))
                return WasperPathRole.Undefined;
            return (WasperPathRole)values[0];
        }

        private static Curve SourceCurve(DataTree<Curve> tree, GH_Path path, int branchIndex)
        {
            if (tree == null || tree.BranchCount == 0)
                return null;
            IList<Curve> values = tree.PathExists(path)
                ? tree.Branch(path)
                : branchIndex < tree.BranchCount
                    ? tree.Branch(branchIndex)
                    : null;
            return values != null && values.Count > 0 ? values[0] : null;
        }

        private static GH_Structure<GH_Integer> BuildRoleTree(WasperPrintPath path)
        {
            var roles = new GH_Structure<GH_Integer>();
            if (path?.PtPlanes == null)
                return roles;
            for (int i = 0; i < path.PtPlanes.BranchCount; i++)
            {
                GH_Path treePath = path.PtPlanes.Path(i);
                roles.Append(new GH_Integer((int)BranchRole(path.PathRoles, treePath, i)), treePath);
            }
            return roles;
        }

        private static void CopyUserStrings(Curve source, Curve target)
        {
            if (source == null || target == null)
                return;
            System.Collections.Specialized.NameValueCollection values = source.GetUserStrings();
            if (values == null)
                return;
            foreach (string key in values.AllKeys)
                target.SetUserString(key, values[key]);
        }

        private static void Remember(string id, WasperPrintPath path)
        {
            lock (CacheLock)
            {
                PathCache[id] = path;
                CacheOrder.Enqueue(id);
                while (CacheOrder.Count > CacheCapacity)
                    PathCache.Remove(CacheOrder.Dequeue());
            }
        }

        private static WasperPrintPath Recall(string id)
        {
            lock (CacheLock)
                return PathCache.TryGetValue(id, out WasperPrintPath path) ? path : null;
        }

        private static Bitmap CreateIcon()
        {
            var bitmap = new Bitmap(24, 24);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            using (var upper = new Pen(Color.FromArgb(35, 145, 72), 2.6f))
            using (var lower = new Pen(Color.FromArgb(20, 92, 48), 2.6f))
            using (var cap = new AdjustableArrowCap(3.2f, 4.2f, true))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                upper.CustomEndCap = cap;
                lower.CustomEndCap = cap;
                graphics.DrawArc(upper, 3, 4, 17, 9, 195, 155);
                graphics.DrawArc(lower, 4, 11, 17, 9, 15, 155);
            }
            return bitmap;
        }

        private sealed class CurveEntry
        {
            public CurveEntry(Curve curve, Polyline polyline, GH_Path path)
            {
                Curve = curve;
                Polyline = polyline;
                Path = path;
            }

            public Curve Curve { get; }
            public Polyline Polyline { get; }
            public GH_Path Path { get; }
        }
    }
}
