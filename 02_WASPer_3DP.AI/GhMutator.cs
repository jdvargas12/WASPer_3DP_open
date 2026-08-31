// -----------------------------------------------------------------------
//  GhMutator.cs
//  Controlled write access to the active Grasshopper document.
//
//  Phase 2-A: slider value mutation only.
//  Everything else (wires, script code, component state) comes later.
//
//  Safety rules enforced here:
//    - Values are always clamped to [Minimum, Maximum].
//    - Target is resolved by GUID first, nickname as fallback.
//    - Failed mutations return a result with Success=false — they never throw.
//    - The document solution is expired after mutation so GH re-solves.
//    - Batch commands execute all entries in order; one failure does not
//      stop the rest.
//
//  Threading note:
//    SolveInstance runs on the main Rhino/GH thread.
//    GhMutator must only be called from SolveInstance (not background threads).
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace WASPer_3DP.AI
{
    public static class GhMutator
    {
        // ----------------------------------------------------------------
        //  Batch entry point
        // ----------------------------------------------------------------

        /// <summary>
        /// Executes every command in a batch sequentially.
        /// All commands are attempted even if earlier ones fail.
        /// Never throws.
        /// </summary>
        public static GhBatchResult ExecuteBatch(GhBatchCommand batch)
        {
            if (batch == null)
            {
                return new GhBatchResult
                {
                    BatchId      = null,
                    ExecutedAt   = DateTime.UtcNow,
                    AllSucceeded = false,
                    FailCount    = 1,
                    Summary      = "Batch is null.",
                    Results      = new List<GhMutationResult>()
                };
            }

            var results    = new List<GhMutationResult>();
            int successCnt = 0;
            int failCnt    = 0;

            foreach (var cmd in batch.Commands ?? new List<GhMutationCommand>())
            {
                var r = Execute(cmd);
                results.Add(r);
                if (r.Success) successCnt++;
                else           failCnt++;
            }

            return new GhBatchResult
            {
                BatchId      = batch.BatchId,
                ExecutedAt   = DateTime.UtcNow,
                AllSucceeded = failCnt == 0,
                SuccessCount = successCnt,
                FailCount    = failCnt,
                Results      = results,
                Summary      = $"{successCnt}/{results.Count} commands succeeded."
            };
        }

        // ----------------------------------------------------------------
        //  Single-command entry point — dispatches by CommandType
        // ----------------------------------------------------------------

        /// <summary>
        /// Executes a single mutation command and returns a result object.
        /// Never throws. On any failure, returns a result with Success=false.
        /// </summary>
        public static GhMutationResult Execute(GhMutationCommand cmd)
        {
            if (cmd == null)
                return Fail(null, "Command is null.");

            try
            {
                switch ((cmd.CommandType ?? string.Empty).Trim().ToLowerInvariant())
                {
                    case "setslidervalue":
                        return SetSliderValue(cmd);

                    case "setgenevalues":
                        return SetGeneValues(cmd);

                    default:
                        return Fail(cmd, $"Unknown CommandType: '{cmd.CommandType}'. " +
                                        "Supported: SetSliderValue, SetGeneValues.");
                }
            }
            catch (Exception ex)
            {
                return Fail(cmd, $"Unexpected error: {ex.Message}");
            }
        }

        // ----------------------------------------------------------------
        //  SetSliderValue
        // ----------------------------------------------------------------

        private static GhMutationResult SetSliderValue(GhMutationCommand cmd)
        {
            var doc = GhDocumentProvider.GetActiveDocument();
            if (doc == null)
                return Fail(cmd, "No active Grasshopper document.");

            // --- Resolve target slider ----------------------------------
            GH_NumberSlider slider = ResolveSlider(doc, cmd);

            if (slider == null)
            {
                string hint = !string.IsNullOrEmpty(cmd.TargetId)
                    ? $"id='{cmd.TargetId}'"
                    : $"nickname='{cmd.TargetNickName}'";
                return Fail(cmd, $"Slider not found ({hint}). " +
                                 "Check the Sliders list in the snapshot for valid IDs.");
            }

            // --- Clamp to allowed range ---------------------------------
            decimal requested = (decimal)cmd.Value;
            decimal clamped   = Math.Max(slider.Slider.Minimum,
                                Math.Min(slider.Slider.Maximum, requested));

            decimal previous  = slider.CurrentValue;

            // --- Apply --------------------------------------------------
            slider.SetSliderValue(clamped);

            // Expire the slider so GH re-solves the downstream graph
            slider.ExpireSolution(true);

            // --- Build result -------------------------------------------
            string clampNote = (clamped != requested)
                ? $" (requested {(double)requested} was clamped to range [{(double)slider.Slider.Minimum}, {(double)slider.Slider.Maximum}])"
                : string.Empty;

            return new GhMutationResult
            {
                CommandId     = cmd.CommandId,
                ExecutedAt    = DateTime.UtcNow,
                Success       = true,
                Message       = $"Set '{slider.NickName}' from {(double)previous} to {(double)clamped}{clampNote}.",
                PreviousValue = (double)previous,
                NewValue      = (double)clamped
            };
        }

        // ----------------------------------------------------------------
        //  Helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Finds the slider by GUID (preferred) then by nickname (fallback).
        /// </summary>
        private static GH_NumberSlider ResolveSlider(
            Grasshopper.Kernel.GH_Document doc,
            GhMutationCommand cmd)
        {
            // 1. Try GUID — unambiguous, fast
            if (!string.IsNullOrWhiteSpace(cmd.TargetId) &&
                Guid.TryParse(cmd.TargetId, out Guid guid))
            {
                var byId = doc.FindObject(guid, true) as GH_NumberSlider;
                if (byId != null) return byId;
            }

            // 2. Try nickname — case-insensitive, first match
            if (!string.IsNullOrWhiteSpace(cmd.TargetNickName))
            {
                foreach (IGH_DocumentObject obj in doc.Objects)
                {
                    if (obj is GH_NumberSlider s &&
                        string.Equals(s.NickName, cmd.TargetNickName,
                                      StringComparison.OrdinalIgnoreCase))
                        return s;
                }
            }

            return null;
        }

        // ----------------------------------------------------------------
        //  SetGeneValues  (reflection-based — GH_GenePool API is internal)
        // ----------------------------------------------------------------

        private static GhMutationResult SetGeneValues(GhMutationCommand cmd)
        {
            var doc = GhDocumentProvider.GetActiveDocument();
            if (doc == null)
                return Fail(cmd, "No active Grasshopper document.");

            if (cmd.GeneValues == null || cmd.GeneValues.Count == 0)
                return Fail(cmd, "GeneValues list is empty. Provide at least one value.");

            // --- Resolve target gene pool by GUID then by nickname ------
            IGH_DocumentObject pool = null;

            if (!string.IsNullOrWhiteSpace(cmd.TargetId) &&
                Guid.TryParse(cmd.TargetId, out Guid guid))
            {
                var found = doc.FindObject(guid, true);
                if (found != null && found.GetType().Name == "GH_GenePool")
                    pool = found;
            }

            if (pool == null && !string.IsNullOrWhiteSpace(cmd.TargetNickName))
            {
                foreach (IGH_DocumentObject obj in doc.Objects)
                {
                    if (obj.GetType().Name == "GH_GenePool" &&
                        string.Equals(obj.NickName, cmd.TargetNickName,
                                      StringComparison.OrdinalIgnoreCase))
                    {
                        pool = obj;
                        break;
                    }
                }
            }

            if (pool == null)
            {
                string hint = !string.IsNullOrEmpty(cmd.TargetId)
                    ? $"id='{cmd.TargetId}'"
                    : $"nickname='{cmd.TargetNickName}'";
                return Fail(cmd, $"GenePool not found ({hint}). " +
                                 "Check the GenePools list in the snapshot.");
            }

            Type poolType = pool.GetType();

            // --- Reflect gene count ------------------------------------
            int geneCount = 0;
            try
            {
                var countProp = poolType.GetProperty("GeneCount") ??
                                poolType.GetProperty("Count");
                if (countProp != null)
                    geneCount = (int)countProp.GetValue(pool);
            }
            catch (Exception ex)
            {
                return Fail(cmd, $"Could not read GeneCount via reflection: {ex.Message}");
            }

            if (geneCount == 0)
                return Fail(cmd, "GenePool has 0 genes — nothing to set.");

            // --- Resolve which indices to update -----------------------
            List<int> indices;
            if (cmd.GeneIndices != null && cmd.GeneIndices.Count > 0)
            {
                indices = cmd.GeneIndices;
                if (indices.Count != cmd.GeneValues.Count)
                    return Fail(cmd, $"GeneIndices ({indices.Count}) and GeneValues " +
                                     $"({cmd.GeneValues.Count}) must have the same length.");
            }
            else
            {
                // Apply in order to all genes (up to the list length)
                indices = new List<int>();
                for (int i = 0; i < Math.Min(cmd.GeneValues.Count, geneCount); i++)
                    indices.Add(i);
            }

            // --- Reflect set_Gene(int, double) or indexer setter -------
            MethodInfo setGene = poolType.GetMethod("set_Gene",
                BindingFlags.Public | BindingFlags.Instance,
                null, new[] { typeof(int), typeof(double) }, null);

            PropertyInfo indexerProp = poolType.GetProperty("Item");

            // Also look for a Genes list with per-gene Value setter
            PropertyInfo genesProp = poolType.GetProperty("Genes");

            int changed = 0;
            var messages = new List<string>();

            for (int k = 0; k < indices.Count; k++)
            {
                int    idx      = indices[k];
                double reqValue = cmd.GeneValues[k];

                if (idx < 0 || idx >= geneCount)
                {
                    messages.Add($"Gene {idx}: index out of range (pool has {geneCount} genes).");
                    continue;
                }

                // Clamp using the gene's own range (read via reflection)
                double lower = 0.0, upper = 1.0;
                try
                {
                    // Try to read bounds from individual gene object
                    if (setGene != null || indexerProp != null)
                    {
                        object gene = indexerProp?.GetValue(pool, new object[] { idx });
                        if (gene != null)
                        {
                            lower = GhInspector.ReflectDouble(gene, "Lower", "Minimum", "_lower");
                            upper = GhInspector.ReflectDouble(gene, "Upper", "Maximum", "_upper");
                        }
                    }
                }
                catch { /* use defaults 0,1 */ }

                double clamped = Math.Max(lower, Math.Min(upper, reqValue));

                bool applied = false;
                try
                {
                    if (setGene != null)
                    {
                        setGene.Invoke(pool, new object[] { idx, clamped });
                        applied = true;
                    }
                    else if (indexerProp != null && indexerProp.CanWrite)
                    {
                        indexerProp.SetValue(pool, clamped, new object[] { idx });
                        applied = true;
                    }
                    else if (genesProp != null)
                    {
                        // Try IList<T> approach
                        object geneList = genesProp.GetValue(pool);
                        if (geneList is System.Collections.IList list && idx < list.Count)
                        {
                            object gene = list[idx];
                            Type   gt   = gene.GetType();
                            PropertyInfo vp = gt.GetProperty("Value");
                            if (vp != null && vp.CanWrite)
                            {
                                vp.SetValue(gene, clamped);
                                applied = true;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    messages.Add($"Gene {idx}: set failed — {ex.Message}");
                    continue;
                }

                if (applied)
                {
                    changed++;
                    string clampNote = Math.Abs(clamped - reqValue) > 1e-10
                        ? $" (clamped from {reqValue})" : string.Empty;
                    messages.Add($"Gene {idx}: {clamped}{clampNote}");
                }
                else
                {
                    messages.Add($"Gene {idx}: no suitable setter found via reflection.");
                }
            }

            // Expire so GH re-solves downstream
            if (changed > 0 && pool is IGH_ActiveObject active)
                active.ExpireSolution(true);

            bool allOk = changed == indices.Count;
            return new GhMutationResult
            {
                CommandId     = cmd.CommandId,
                ExecutedAt    = DateTime.UtcNow,
                Success       = allOk,
                Message       = $"{changed}/{indices.Count} genes set on '{pool.NickName}'. " +
                                string.Join(" | ", messages)
            };
        }

        private static GhMutationResult Fail(GhMutationCommand cmd, string message)
        {
            return new GhMutationResult
            {
                CommandId  = cmd?.CommandId,
                ExecutedAt = DateTime.UtcNow,
                Success    = false,
                Message    = message
            };
        }
    }
}
