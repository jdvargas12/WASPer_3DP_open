// -----------------------------------------------------------------------
//  GhCapture.cs
//  Aggregates all inspection results into a single GhSnapshot.
//
//  This is the public entry point for the file-based bridge:
//    1. Call GhCapture.GetSnapshot() to get everything at once.
//    2. The caller (main plugin component) serializes it to JSON.
//    3. Claude reads the JSON from disk on demand.
//
//  GhCapture itself has no serialization dependency — it only knows
//  about GhInspector and GhModels. JSON is handled by the main plugin
//  which already has Newtonsoft.Json as a dependency.
// -----------------------------------------------------------------------

using System;

namespace WASPer_3DP.AI
{
    public static class GhCapture
    {
        /// <summary>
        /// Builds a complete point-in-time snapshot of the active canvas.
        /// Returns null if no Grasshopper document is open.
        /// Never throws — all inspector calls are wrapped defensively.
        /// </summary>
        public static GhSnapshot GetSnapshot()
        {
            var doc = GhDocumentProvider.GetActiveDocument();
            if (doc == null) return null;

            GhCanvasSummary summary = null;
            try { summary = GhInspector.GetCanvasSummary(); }
            catch { /* ignore — partial snapshot is better than no snapshot */ }

            var issues = new System.Collections.Generic.List<GhRuntimeIssue>();
            try { issues = GhInspector.GetRuntimeErrors(); }
            catch { }

            var selected = new System.Collections.Generic.List<GhComponentInfo>();
            try { selected = GhInspector.GetSelectedComponents(); }
            catch { }

            var sliders = new System.Collections.Generic.List<GhSliderInfo>();
            try { sliders = GhInspector.GetAllSliders(); }
            catch { }

            var genePools = new System.Collections.Generic.List<GhGenePoolInfo>();
            try { genePools = GhInspector.GetAllGenePools(); }
            catch { }

            var allObjects = new System.Collections.Generic.List<GhObjectSnapshot>();
            try { allObjects = GhInspector.GetAllObjects(); }
            catch { }

            return new GhSnapshot
            {
                CapturedAt         = DateTime.UtcNow,
                Summary            = summary,
                Issues             = issues,
                SelectedComponents = selected,
                Sliders            = sliders,
                GenePools          = genePools,
                AllObjects         = allObjects
            };
        }
    }
}
