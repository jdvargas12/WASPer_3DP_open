// -----------------------------------------------------------------------
//  GhInspector.cs
//  Read-only inspection of the active Grasshopper document.
//
//  Phase 1 public surface:
//    GetCanvasSummary()       — canvas-level counts
//    GetRuntimeErrors()       — all warnings / errors on the canvas
//    GetSelectedComponents()  — full detail of every selected object
//
//  Phase 2-A addition:
//    GetAllSliders()          — every number slider: value, range, GUID
//
//  All methods return null / empty collection if no document is open.
//  No GH types escape this file — callers only receive GhModels types.
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using Grasshopper;                  // GH_DocumentObject
using Grasshopper.Kernel;           // IGH_DocumentObject, IGH_Component, etc.
using Grasshopper.Kernel.Special;   // GH_NumberSlider

namespace WASPer_3DP.AI
{
    public static class GhInspector
    {
        // ----------------------------------------------------------------
        //  1.  Canvas summary
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns a high-level snapshot of the active canvas:
        /// object counts, selection counts, and warning/error totals.
        /// Returns null if no document is open.
        /// </summary>
        public static GhCanvasSummary GetCanvasSummary()
        {
            var doc = GhDocumentProvider.GetActiveDocument();
            if (doc == null) return null;

            var summary = new GhCanvasSummary
            {
                DocumentName = doc.DisplayName,
                ObjectCount  = doc.ObjectCount
            };

            int selectedCount  = 0;
            int componentCount = 0;
            int warningCount   = 0;
            int errorCount     = 0;

            foreach (IGH_DocumentObject obj in doc.Objects)
            {
                if (obj.Attributes != null && obj.Attributes.Selected)
                    selectedCount++;

                if (!(obj is IGH_Component)) continue;

                componentCount++;

                if (!(obj is IGH_ActiveObject activeObj)) continue;

                if (activeObj.RuntimeMessages(GH_RuntimeMessageLevel.Warning).Count > 0)
                    warningCount++;
                if (activeObj.RuntimeMessages(GH_RuntimeMessageLevel.Error).Count > 0)
                    errorCount++;
            }

            summary.SelectedCount  = selectedCount;
            summary.ComponentCount = componentCount;
            summary.WarningCount   = warningCount;
            summary.ErrorCount     = errorCount;

            return summary;
        }

        // ----------------------------------------------------------------
        //  2.  Runtime errors / warnings
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns every runtime warning and error currently visible on the canvas.
        /// One component can contribute multiple entries (one per message).
        /// Returns an empty list if the canvas is clean or no document is open.
        /// </summary>
        public static List<GhRuntimeIssue> GetRuntimeErrors()
        {
            var doc = GhDocumentProvider.GetActiveDocument();
            var issues = new List<GhRuntimeIssue>();
            if (doc == null) return issues;

            foreach (IGH_DocumentObject obj in doc.Objects)
            {
                if (!(obj is IGH_ActiveObject activeObj)) continue;

                GH_RuntimeMessageLevel[] levels =
                {
                    GH_RuntimeMessageLevel.Warning,
                    GH_RuntimeMessageLevel.Error
                };

                foreach (var level in levels)
                {
                    var messages = activeObj.RuntimeMessages(level);
                    foreach (string msg in messages)
                    {
                        issues.Add(new GhRuntimeIssue
                        {
                            ComponentName     = obj.Name,
                            ComponentNickName = obj.NickName,
                            Level             = level.ToString(),
                            Message           = msg
                        });
                    }
                }
            }

            return issues;
        }

        // ----------------------------------------------------------------
        //  3.  Selected components
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns detailed info for every currently selected canvas object.
        /// Works for both GH components and standalone parameters.
        /// Returns an empty list if nothing is selected or no document is open.
        /// </summary>
        public static List<GhComponentInfo> GetSelectedComponents()
        {
            var doc = GhDocumentProvider.GetActiveDocument();
            var result = new List<GhComponentInfo>();
            if (doc == null) return result;

            foreach (IGH_DocumentObject obj in doc.Objects)
            {
                // Skip unselected
                if (obj.Attributes == null || !obj.Attributes.Selected) continue;

                // TODO (Phase 2): GH_DocumentObject.Locked not in GH SDK 8.17 public API.
                const bool locked = false;

                var info = new GhComponentInfo
                {
                    Id            = obj.InstanceGuid.ToString(),
                    Name          = obj.Name,
                    NickName      = obj.NickName,
                    TypeName      = obj.GetType().Name,
                    Selected      = true,
                    Locked        = locked,
                    PivotX        = obj.Attributes.Pivot.X,
                    PivotY        = obj.Attributes.Pivot.Y,
                    ScriptContent = GhScriptReader.TryReadCode(obj)
                };

                // Category / SubCategory + param lists
                if (obj is IGH_Component comp)
                {
                    info.Category    = comp.Category;
                    info.SubCategory = comp.SubCategory;

                    foreach (IGH_Param input in comp.Params.Input)
                        info.InputNames.Add(input.Name);
                    foreach (IGH_Param output in comp.Params.Output)
                        info.OutputNames.Add(output.Name);
                }
                else if (obj is IGH_Param param)
                {
                    info.Category    = param.Category;
                    info.SubCategory = param.SubCategory;
                }

                // Hidden (preview visibility)
                if (obj is IGH_PreviewObject preview)
                    info.Hidden = preview.Hidden;

                // Runtime level
                if (obj is IGH_ActiveObject activeObj)
                {
                    if (activeObj.RuntimeMessages(GH_RuntimeMessageLevel.Error).Count > 0)
                        info.RuntimeLevel = "Error";
                    else if (activeObj.RuntimeMessages(GH_RuntimeMessageLevel.Warning).Count > 0)
                        info.RuntimeLevel = "Warning";
                    else
                        info.RuntimeLevel = "OK";
                }
                else
                {
                    info.RuntimeLevel = "N/A";
                }

                result.Add(info);
            }

            return result;
        }

        // ----------------------------------------------------------------
        //  4.  All sliders  (Phase 2-A)
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns every GH_NumberSlider on the canvas with its current value,
        /// allowed range, type, and GUID.
        /// Claude uses this list to identify which slider to target before
        /// writing a GhMutationCommand.
        /// </summary>
        public static List<GhSliderInfo> GetAllSliders()
        {
            var doc    = GhDocumentProvider.GetActiveDocument();
            var result = new List<GhSliderInfo>();
            if (doc == null) return result;

            foreach (IGH_DocumentObject obj in doc.Objects)
            {
                if (!(obj is GH_NumberSlider slider)) continue;

                result.Add(new GhSliderInfo
                {
                    Id           = slider.InstanceGuid.ToString(),
                    Name         = slider.Name,
                    NickName     = slider.NickName,
                    CurrentValue = (double)slider.CurrentValue,
                    Minimum      = (double)slider.Slider.Minimum,
                    Maximum      = (double)slider.Slider.Maximum,
                    SliderType   = slider.Slider.Type.ToString(),
                    PivotX       = slider.Attributes != null ? slider.Attributes.Pivot.X : 0,
                    PivotY       = slider.Attributes != null ? slider.Attributes.Pivot.Y : 0
                });
            }

            return result;
        }

        // ----------------------------------------------------------------
        //  5.  All gene pools
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns every GH_GenePool component on the canvas, with all gene
        /// values and their allowed ranges.
        /// Uses reflection because GH_GenePool's internal gene API is not
        /// part of the stable public SDK surface.
        /// Returns an empty list (never null) if no pools exist or no document is open.
        /// </summary>
        public static List<GhGenePoolInfo> GetAllGenePools()
        {
            var doc    = GhDocumentProvider.GetActiveDocument();
            var result = new List<GhGenePoolInfo>();
            if (doc == null) return result;

            foreach (IGH_DocumentObject obj in doc.Objects)
            {
                // Match by type name — avoids hard assembly dependency
                if (obj.GetType().Name != "GH_GenePool") continue;

                Type poolType = obj.GetType();

                // ---- Gene count ----------------------------------------
                int geneCount = 0;
                try
                {
                    PropertyInfo countProp =
                        poolType.GetProperty("GeneCount") ??
                        poolType.GetProperty("Count");
                    if (countProp != null)
                        geneCount = (int)countProp.GetValue(obj);
                }
                catch { continue; }

                if (geneCount <= 0) continue;

                var poolInfo = new GhGenePoolInfo
                {
                    Id        = obj.InstanceGuid.ToString(),
                    Name      = obj.Name,
                    NickName  = obj.NickName,
                    GeneCount = geneCount,
                    Genes     = new List<GhGeneInfo>(),
                    PivotX    = obj.Attributes != null ? obj.Attributes.Pivot.X : 0,
                    PivotY    = obj.Attributes != null ? obj.Attributes.Pivot.Y : 0
                };

                // ---- Per-gene data -------------------------------------
                // Strategy 1: public indexer  pool[i]
                PropertyInfo indexer = poolType.GetProperty("Item");
                // Strategy 2: get_Gene(int) method
                MethodInfo getGene =
                    poolType.GetMethod("get_Gene",
                        BindingFlags.Public | BindingFlags.Instance,
                        null, new[] { typeof(int) }, null);

                for (int i = 0; i < geneCount; i++)
                {
                    try
                    {
                        object gene = null;

                        if (getGene != null)
                            gene = getGene.Invoke(obj, new object[] { i });
                        else if (indexer != null)
                            gene = indexer.GetValue(obj, new object[] { i });

                        if (gene == null) continue;

                        Type geneType = gene.GetType();

                        double val   = ReadDouble(gene, geneType, "Value",   "_value",  "m_value");
                        double lower = ReadDouble(gene, geneType, "Lower",   "Minimum", "_lower",  "m_lower");
                        double upper = ReadDouble(gene, geneType, "Upper",   "Maximum", "_upper",  "m_upper");

                        poolInfo.Genes.Add(new GhGeneInfo
                        {
                            Index = i,
                            Value = val,
                            Lower = lower,
                            Upper = upper
                        });
                    }
                    catch { /* skip bad gene, keep rest */ }
                }

                result.Add(poolInfo);
            }

            return result;
        }

        // ----------------------------------------------------------------
        //  6.  Full canvas object map
        // ----------------------------------------------------------------

        /// <summary>
        /// Returns a snapshot of every meaningful object on the canvas:
        /// components, panels, sliders, and standalone params.
        /// Each entry includes its input/output param names and the wire
        /// sources for every connected input, giving Claude full topology.
        ///
        /// Groups and pure annotation objects are skipped to keep the list lean.
        /// Never throws — individual object failures are silently skipped.
        /// </summary>
        public static List<GhObjectSnapshot> GetAllObjects()
        {
            var doc    = GhDocumentProvider.GetActiveDocument();
            var result = new List<GhObjectSnapshot>();
            if (doc == null) return result;

            // ---- Pre-pass: build param-GUID → owner-GUID map ----------------
            var paramOwner = new Dictionary<Guid, Guid>();
            foreach (IGH_DocumentObject obj in doc.Objects)
            {
                if (!(obj is IGH_Component comp)) continue;
                foreach (IGH_Param p in comp.Params.Input)
                    if (p != null) paramOwner[p.InstanceGuid] = obj.InstanceGuid;
                foreach (IGH_Param p in comp.Params.Output)
                    if (p != null) paramOwner[p.InstanceGuid] = obj.InstanceGuid;
            }

            // ---- Main pass --------------------------------------------------
            foreach (IGH_DocumentObject obj in doc.Objects)
            {
                try
                {
                    string typeName = obj.GetType().Name;
                    if (typeName == "GH_Group" ||
                        typeName == "GH_Relay" ||
                        typeName == "GH_Annotation") continue;

                    var snap = new GhObjectSnapshot
                    {
                        Id       = obj.InstanceGuid.ToString(),
                        Name     = obj.Name     ?? string.Empty,
                        NickName = obj.NickName ?? string.Empty,
                        TypeName = typeName,
                        PivotX   = obj.Attributes != null ? obj.Attributes.Pivot.X : 0,
                        PivotY   = obj.Attributes != null ? obj.Attributes.Pivot.Y : 0
                    };

                    // Runtime level
                    if (obj is IGH_ActiveObject active)
                    {
                        snap.RuntimeLevel =
                            active.RuntimeMessages(GH_RuntimeMessageLevel.Error).Count   > 0 ? "Error"   :
                            active.RuntimeMessages(GH_RuntimeMessageLevel.Warning).Count > 0 ? "Warning" :
                            "OK";
                    }
                    else { snap.RuntimeLevel = "N/A"; }

                    // Component: full I/O topology
                    if (obj is IGH_Component ghComp)
                    {
                        snap.Category    = ghComp.Category    ?? string.Empty;
                        snap.SubCategory = ghComp.SubCategory ?? string.Empty;

                        foreach (IGH_Param input in ghComp.Params.Input)
                        {
                            if (input == null) continue;
                            var pSnap = new GhParamSnapshot
                            {
                                Name     = input.Name     ?? string.Empty,
                                NickName = input.NickName ?? string.Empty
                            };
                            foreach (IGH_Param src in input.Sources)
                            {
                                if (src == null) continue;
                                try
                                {
                                    string ownerId = paramOwner.TryGetValue(src.InstanceGuid, out Guid og)
                                        ? og.ToString()
                                        : src.InstanceGuid.ToString();
                                    pSnap.Sources.Add(new GhWireSource
                                    {
                                        SourceId    = ownerId,
                                        SourceParam = src.Name ?? string.Empty
                                    });
                                }
                                catch { }
                            }
                            snap.Inputs.Add(pSnap);
                        }

                        foreach (IGH_Param output in ghComp.Params.Output)
                        {
                            if (output == null) continue;
                            snap.Outputs.Add(new GhParamSnapshot
                            {
                                Name     = output.Name     ?? string.Empty,
                                NickName = output.NickName ?? string.Empty
                            });
                        }
                    }
                    // Standalone param (panel, slider, data param)
                    else if (obj is IGH_Param standaloneParam)
                    {
                        snap.Category    = standaloneParam.Category    ?? string.Empty;
                        snap.SubCategory = standaloneParam.SubCategory ?? string.Empty;

                        foreach (IGH_Param src in standaloneParam.Sources)
                        {
                            if (src == null) continue;
                            try
                            {
                                string ownerId = paramOwner.TryGetValue(src.InstanceGuid, out Guid og)
                                    ? og.ToString()
                                    : src.InstanceGuid.ToString();
                                snap.Inputs.Add(new GhParamSnapshot
                                {
                                    Name    = "input",
                                    Sources = new List<GhWireSource>
                                    {
                                        new GhWireSource { SourceId = ownerId, SourceParam = src.Name ?? string.Empty }
                                    }
                                });
                            }
                            catch { }
                        }

                        // Current value for panels and sliders
                        try
                        {
                            if (obj is Grasshopper.Kernel.Special.GH_Panel panel)
                                snap.Value = panel.UserText;
                            else if (obj is Grasshopper.Kernel.Special.GH_NumberSlider slider)
                                snap.Value = slider.CurrentValue.ToString();
                        }
                        catch { }
                    }

                    result.Add(snap);
                }
                catch { }
            }

            return result;
        }

        // ----------------------------------------------------------------
        //  Private helpers
        // ----------------------------------------------------------------

        /// <summary>
        /// Reads a double from the first property or field name that resolves.
        /// Tries public properties first, then non-public fields.
        /// Also callable from GhMutator for clamping gene values.
        /// </summary>
        internal static double ReflectDouble(object obj, params string[] names)
            => ReadDouble(obj, obj.GetType(), names);

        private static double ReadDouble(object obj, Type type, params string[] names)
        {
            const BindingFlags pub    = BindingFlags.Public   | BindingFlags.Instance;
            const BindingFlags nonpub = BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (string name in names)
            {
                try
                {
                    PropertyInfo prop = type.GetProperty(name, pub);
                    if (prop != null)
                    {
                        object v = prop.GetValue(obj);
                        if (v != null) return Convert.ToDouble(v);
                    }

                    FieldInfo field = type.GetField(name, pub) ??
                                      type.GetField(name, nonpub);
                    if (field != null)
                    {
                        object v = field.GetValue(obj);
                        if (v != null) return Convert.ToDouble(v);
                    }
                }
                catch { /* try next name */ }
            }
            return 0.0;
        }
    }
}
