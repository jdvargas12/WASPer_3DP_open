using System;
using System.Collections.Generic;
using System.Linq;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

using Rhino;
using Rhino.Geometry;

namespace WASPer_3DP
{
    /// <summary>
    /// Shared tree and metadata handling for the compiled 2D infill components.
    /// Pattern geometry remains component-specific.
    /// </summary>
    internal static class WasperLayerInfillReplacement
    {
        internal delegate Curve PatternGenerator(
            Curve boundary,
            double rotation,
            double distance,
            double clearance,
            string context);

        internal static bool TryNormalizeLayerIndices(
            GH_Component owner,
            IList<int> values,
            out HashSet<int> selected,
            out Dictionary<int, int> selectionOrder)
        {
            selected = null;
            selectionOrder = new Dictionary<int, int>();

            if (values == null || values.Count == 0 ||
                (values.Count == 1 && values[0] == -1))
                return true;

            if (values.Contains(-1))
            {
                owner.AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Error,
                    "la_index=-1 means all layers and cannot be combined with other indices.");
                return false;
            }

            selected = new HashSet<int>();
            for (int i = 0; i < values.Count; i++)
            {
                int value = values[i];
                if (value < 0)
                {
                    owner.AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Error,
                        $"la_index contains invalid negative value {value}. Use nonnegative indices, or -1 alone for all layers.");
                    return false;
                }

                if (!selected.Add(value))
                {
                    owner.AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"Duplicate la_index {value} was ignored.");
                    continue;
                }

                selectionOrder[value] = selectionOrder.Count;
            }

            return true;
        }

        internal static bool HasRoleMetadata(GH_Structure<GH_Curve> tree)
        {
            if (tree == null)
                return false;

            foreach (GH_Curve goo in tree.AllData(true))
                if (goo?.Value != null &&
                    WasperPathRoleMetadata.Get(goo.Value) != WasperPathRole.Undefined)
                    return true;

            return false;
        }

        internal static double Resolve(IList<double> values, int ordinal, double fallback)
        {
            if (values == null || values.Count == 0)
                return fallback;

            double value = values[Math.Abs(ordinal) % values.Count];
            return double.IsNaN(value) || double.IsInfinity(value) ? fallback : value;
        }

        internal static int LogicalLayerIndex(GH_Path path)
        {
            return path == null || path.Length == 0 ? 0 : path[path.Length - 1];
        }

        internal static Curve DuplicateWithRole(Curve source)
        {
            if (source == null)
                return null;

            Curve duplicate = source.DuplicateCurve();
            WasperPathRoleMetadata.Copy(source, duplicate);
            return duplicate;
        }

        internal static void ProcessMetadataTree(
            GH_Component owner,
            GH_Structure<GH_Curve> input,
            HashSet<int> selectedLayers,
            IDictionary<int, int> selectionOrder,
            IList<double> rotations,
            IList<double> distances,
            IList<double> clearances,
            PatternGenerator generator,
            out GH_Structure<GH_Curve> generatedTree,
            out GH_Structure<GH_Curve> fullPathTree)
        {
            generatedTree = new GH_Structure<GH_Curve>();
            fullPathTree = new GH_Structure<GH_Curve>();
            int allLayerOrdinal = 0;
            Dictionary<int, int> allLayerOrder = new Dictionary<int, int>();

            for (int branchIndex = 0; branchIndex < input.PathCount; branchIndex++)
            {
                GH_Path path = input.Paths[branchIndex];
                IList<GH_Curve> branch = input.Branches[branchIndex];
                int layerIndex = LogicalLayerIndex(path);
                bool selected = selectedLayers == null || selectedLayers.Contains(layerIndex);

                if (!selected)
                {
                    AppendOriginalBranch(branch, path, fullPathTree);
                    continue;
                }

                int ordinal;
                if (selectedLayers == null)
                {
                    if (!allLayerOrder.TryGetValue(layerIndex, out ordinal))
                    {
                        ordinal = allLayerOrdinal++;
                        allLayerOrder[layerIndex] = ordinal;
                    }
                }
                else
                {
                    ordinal = selectionOrder[layerIndex];
                }

                double rotation = Resolve(rotations, ordinal, 0.0);
                double distance = Resolve(distances, ordinal, 2.0);
                double clearance = Resolve(clearances, ordinal, 0.0);

                List<Curve> shells = new List<Curve>();
                foreach (GH_Curve goo in branch)
                {
                    Curve curve = goo?.Value;
                    if (curve != null &&
                        WasperPathRoleMetadata.Get(curve) == WasperPathRole.Shell &&
                        curve.IsClosed &&
                        curve.IsPlanar())
                        shells.Add(curve);
                }

                List<Curve> boundaries = SelectOuterBoundaries(shells, out int ignoredNested);
                if (ignoredNested > 0)
                {
                    owner.AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"Layer {layerIndex}: ignored {ignoredNested} nested Shell contour(s). Hole-aware infill is not yet supported.");
                }

                if (boundaries.Count == 0)
                {
                    owner.AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"Layer {layerIndex}: no valid closed planar Shell boundary was found; the layer passed through unchanged.");
                    AppendOriginalBranch(branch, path, fullPathTree);
                    continue;
                }

                List<Curve> replacements = new List<Curve>();
                for (int i = 0; i < boundaries.Count; i++)
                {
                    Curve replacement = generator(
                        boundaries[i],
                        rotation,
                        distance,
                        clearance,
                        $"layer {layerIndex}, boundary {i}");
                    if (replacement == null)
                        continue;

                    WasperPathRoleMetadata.Set(replacement, WasperPathRole.Infill);
                    replacements.Add(replacement);
                    generatedTree.Append(
                        new GH_Curve(DuplicateWithRole(replacement)),
                        path);
                }

                if (replacements.Count == 0)
                {
                    owner.AddRuntimeMessage(
                        GH_RuntimeMessageLevel.Warning,
                        $"Layer {layerIndex}: no replacement infill could be generated; the existing layer passed through unchanged.");
                    AppendOriginalBranch(branch, path, fullPathTree);
                    continue;
                }

                int firstInfill = -1;
                int lastShell = -1;
                for (int i = 0; i < branch.Count; i++)
                {
                    WasperPathRole role = WasperPathRoleMetadata.Get(branch[i]?.Value);
                    if (role == WasperPathRole.Infill && firstInfill < 0)
                        firstInfill = i;
                    if (role == WasperPathRole.Shell)
                        lastShell = i;
                }

                int insertionIndex = firstInfill >= 0 ? firstInfill : lastShell + 1;
                bool inserted = false;
                for (int i = 0; i <= branch.Count; i++)
                {
                    if (!inserted && i == insertionIndex)
                    {
                        foreach (Curve replacement in replacements)
                            fullPathTree.Append(new GH_Curve(replacement), path);
                        inserted = true;
                    }

                    if (i == branch.Count)
                        break;

                    Curve source = branch[i]?.Value;
                    if (source == null ||
                        WasperPathRoleMetadata.Get(source) == WasperPathRole.Infill)
                        continue;

                    fullPathTree.Append(new GH_Curve(DuplicateWithRole(source)), path);
                }
            }
        }

        private static void AppendOriginalBranch(
            IList<GH_Curve> branch,
            GH_Path path,
            GH_Structure<GH_Curve> target)
        {
            target.EnsurePath(path);
            foreach (GH_Curve goo in branch)
            {
                Curve duplicate = DuplicateWithRole(goo?.Value);
                if (duplicate != null)
                    target.Append(new GH_Curve(duplicate), path);
            }
        }

        private static List<Curve> SelectOuterBoundaries(
            IList<Curve> shells,
            out int ignoredNested)
        {
            ignoredNested = 0;
            List<Curve> result = new List<Curve>();
            List<BoundaryInfo> candidates = new List<BoundaryInfo>();

            foreach (Curve curve in shells)
            {
                if (!curve.TryGetPlane(out Plane plane))
                    continue;

                AreaMassProperties amp = AreaMassProperties.Compute(curve);
                if (amp == null || amp.Area <= RhinoMath.ZeroTolerance)
                    continue;

                candidates.Add(new BoundaryInfo(curve, plane, amp.Centroid, Math.Abs(amp.Area)));
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                BoundaryInfo candidate = candidates[i];
                bool nested = false;
                for (int j = 0; j < candidates.Count; j++)
                {
                    if (i == j || candidates[j].Area <= candidate.Area)
                        continue;

                    BoundaryInfo container = candidates[j];
                    if (Math.Abs(container.Plane.DistanceTo(candidate.Centroid)) > 0.01)
                        continue;
                    if (Math.Abs(container.Plane.Normal * candidate.Plane.Normal) < 0.999)
                        continue;

                    PointContainment containment =
                        container.Curve.Contains(candidate.Centroid, container.Plane, 0.01);
                    if (containment == PointContainment.Inside ||
                        containment == PointContainment.Coincident)
                    {
                        nested = true;
                        break;
                    }
                }

                if (nested)
                    ignoredNested++;
                else
                    result.Add(candidate.Curve);
            }

            return result;
        }

        private sealed class BoundaryInfo
        {
            internal BoundaryInfo(Curve curve, Plane plane, Point3d centroid, double area)
            {
                Curve = curve;
                Plane = plane;
                Centroid = centroid;
                Area = area;
            }

            internal Curve Curve { get; }
            internal Plane Plane { get; }
            internal Point3d Centroid { get; }
            internal double Area { get; }
        }
    }
}
