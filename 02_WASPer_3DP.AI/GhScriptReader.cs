// -----------------------------------------------------------------------
//  GhScriptReader.cs
//  Reads source code from Grasshopper script components.
//
//  GH8 changed the script component architecture significantly.
//  There is no stable public API for reading script content across all
//  component variants (C#, Python 3, GHPython legacy, etc.).
//
//  Strategy: reflection-based property probe, tried in priority order.
//  This is intentionally defensive — it never throws, it just returns null
//  when content cannot be read, so the rest of the snapshot still works.
//
//  Known property paths probed (by GH version / component type):
//    GH1-style C# script  : SourceCode
//    GH8 ScriptComponent  : Script.Code  (via nested object)
//    GH8 ScriptComponent  : Code         (direct string property)
//    GHPython legacy      : Code
//    Generic fallback     : ScriptSource, Source
// -----------------------------------------------------------------------

using System;
using System.Reflection;
using Grasshopper.Kernel;

namespace WASPer_3DP.AI
{
    public static class GhScriptReader
    {
        // ---- Public API -----------------------------------------------

        /// <summary>
        /// Returns true when the object looks like any kind of script component.
        /// Uses type-name heuristics — does not require a hard assembly reference.
        /// </summary>
        public static bool IsScriptComponent(IGH_DocumentObject obj)
        {
            if (obj == null) return false;

            // Check the CLR type name and the GH display name
            string typeName = obj.GetType().FullName ?? string.Empty;
            string ghName   = (obj.Name ?? string.Empty).ToLowerInvariant();

            return typeName.IndexOf("Script",     StringComparison.OrdinalIgnoreCase) >= 0
                || ghName.IndexOf("script",       StringComparison.OrdinalIgnoreCase) >= 0
                || ghName.IndexOf("c# script",    StringComparison.OrdinalIgnoreCase) >= 0
                || ghName.IndexOf("python",       StringComparison.OrdinalIgnoreCase) >= 0
                || ghName.IndexOf("ironpython",   StringComparison.OrdinalIgnoreCase) >= 0
                || typeName.IndexOf("GhPython",   StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Attempts to read the source code from a script component.
        /// Returns null if the component is not a script component or
        /// if no readable code property could be found.
        /// Never throws.
        /// </summary>
        public static string TryReadCode(IGH_DocumentObject obj)
        {
            if (obj == null) return null;
            if (!IsScriptComponent(obj)) return null;

            try
            {
                return ProbeDirectStringProperties(obj)
                    ?? ProbeNestedScriptObject(obj)
                    ?? null;
            }
            catch
            {
                return null;
            }
        }

        // ---- Private probes -------------------------------------------

        /// <summary>
        /// Probes for direct string properties on the component type.
        /// Covers GH1-style and some GH8 component variants.
        /// </summary>
        private static string ProbeDirectStringProperties(IGH_DocumentObject obj)
        {
            // Priority-ordered candidate property names
            string[] candidates =
            {
                "SourceCode",   // GH1 C# script
                "Code",         // GHPython, some GH8
                "ScriptSource", // rare variant
                "Source",       // generic fallback
            };

            var type = obj.GetType();

            foreach (string propName in candidates)
            {
                string result = TryGetStringProperty(obj, type, propName);
                if (result != null) return result;
            }

            return null;
        }

        /// <summary>
        /// Probes for a nested "Script" object that itself carries a "Code" property.
        /// This covers GH8's ScriptComponent where code lives in component.Script.Code.
        /// </summary>
        private static string ProbeNestedScriptObject(IGH_DocumentObject obj)
        {
            var type = obj.GetType();

            // Names for the intermediate object
            string[] containerProps = { "Script", "ScriptInstance", "Engine" };

            foreach (string containerName in containerProps)
            {
                PropertyInfo containerProp = type.GetProperty(
                    containerName,
                    BindingFlags.Public | BindingFlags.Instance);

                if (containerProp == null) continue;

                object container;
                try { container = containerProp.GetValue(obj); }
                catch { continue; }

                if (container == null) continue;

                // Try "Code" on the nested object
                string result = TryGetStringProperty(
                    container,
                    container.GetType(),
                    "Code");

                if (result != null) return result;
            }

            return null;
        }

        // ---- Helpers --------------------------------------------------

        private static string TryGetStringProperty(object target, Type type, string propName)
        {
            PropertyInfo prop = type.GetProperty(
                propName,
                BindingFlags.Public | BindingFlags.Instance);

            if (prop == null) return null;
            if (prop.PropertyType != typeof(string)) return null;

            try
            {
                string value = prop.GetValue(target) as string;
                return string.IsNullOrEmpty(value) ? null : value;
            }
            catch
            {
                return null;
            }
        }
    }
}
