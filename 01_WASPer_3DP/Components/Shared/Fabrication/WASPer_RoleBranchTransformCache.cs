using System;
using System.Collections.Generic;
using System.Linq;

using Grasshopper;
using Grasshopper.Kernel.Data;

using Rhino.Geometry;

namespace WASPer_3DP
{
    internal static class WasperRoleBranchTransformCache
    {
        internal static bool TryRestore(
            WasperPrintPath current,
            WasperPrintPath cachedSource,
            DataTree<Plane> cachedPlanes,
            IList<int> targetRoles,
            out DataTree<Plane> restored)
        {
            restored = null;
            if (current?.PtPlanes == null || cachedSource?.PtPlanes == null ||
                cachedPlanes == null)
                return false;

            var available = new Dictionary<string, Queue<IList<Plane>>>(
                StringComparer.Ordinal);
            for (int b = 0; b < cachedSource.PtPlanes.BranchCount; b++)
            {
                GH_Path path = cachedSource.PtPlanes.Paths[b];
                if (!WasperGcodeTreeUtil.MatchesTargetRoles(
                        cachedSource.PathRoles,
                        path,
                        targetRoles) ||
                    !cachedPlanes.PathExists(path))
                    continue;

                string key = BranchKey(
                    cachedSource.PtPlanes.Branches[b],
                    WasperGcodeTreeUtil.PathRoleAt(cachedSource.PathRoles, path));
                if (!available.TryGetValue(key, out Queue<IList<Plane>> matches))
                {
                    matches = new Queue<IList<Plane>>();
                    available[key] = matches;
                }
                matches.Enqueue(cachedPlanes.Branch(path).ToList());
            }

            var result = new DataTree<Plane>();
            for (int b = 0; b < current.PtPlanes.BranchCount; b++)
            {
                GH_Path path = current.PtPlanes.Paths[b];
                IList<Plane> incoming = current.PtPlanes.Branches[b];
                IList<Plane> outgoing = incoming;
                if (WasperGcodeTreeUtil.MatchesTargetRoles(
                        current.PathRoles,
                        path,
                        targetRoles))
                {
                    string key = BranchKey(
                        incoming,
                        WasperGcodeTreeUtil.PathRoleAt(current.PathRoles, path));
                    if (!available.TryGetValue(key, out Queue<IList<Plane>> matches) ||
                        matches.Count == 0)
                        return false;
                    outgoing = matches.Dequeue();
                }

                result.EnsurePath(path);
                if (outgoing != null)
                {
                    foreach (Plane plane in outgoing)
                        result.Add(plane, path);
                }
            }

            if (available.Values.Any(matches => matches.Count > 0))
                return false;
            restored = result;
            return true;
        }

        internal static string SelectedGeometrySignature(
            WasperPrintPath path,
            IList<int> targetRoles)
        {
            var keys = new List<string>();
            if (path?.PtPlanes != null)
            {
                for (int b = 0; b < path.PtPlanes.BranchCount; b++)
                {
                    GH_Path treePath = path.PtPlanes.Paths[b];
                    if (!WasperGcodeTreeUtil.MatchesTargetRoles(
                            path.PathRoles,
                            treePath,
                            targetRoles))
                        continue;
                    keys.Add(BranchKey(
                        path.PtPlanes.Branches[b],
                        WasperGcodeTreeUtil.PathRoleAt(path.PathRoles, treePath)));
                }
            }

            keys.Sort(StringComparer.Ordinal);
            WasperCacheSignature signature = WasperCacheSignature.Create();
            signature.Add(keys.Count);
            foreach (string key in keys)
                signature.Add(key);
            return signature.Finish();
        }

        private static string BranchKey(IList<Plane> planes, WasperPathRole role)
        {
            WasperCacheSignature signature = WasperCacheSignature.Create();
            signature.Add((int)role);
            signature.Add(planes?.Count ?? 0);
            if (planes != null)
            {
                foreach (Plane plane in planes)
                    signature.Add(plane);
            }
            return signature.Finish();
        }
    }
}
