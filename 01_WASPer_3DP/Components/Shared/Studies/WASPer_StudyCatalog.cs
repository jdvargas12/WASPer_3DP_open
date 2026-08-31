using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;

namespace WASPer_3DP
{
    public enum WasperStudyCompatibilityLevel
    {
        Ready,
        Warning,
        MappingRequired,
        Incompatible,
        Unreadable
    }

    public sealed class WasperStudyCatalogEntry
    {
        public string FilePath { get; set; } = string.Empty;
        public bool IsCurrent { get; set; }
        public WasperStudy Study { get; set; }
        public WasperStudyCompatibilityLevel Compatibility { get; set; }
        public List<string> Issues { get; set; } = new List<string>();

        /// <summary>
        /// True when this entry came from the user manually browsing to a study.json rather than
        /// from the automatic folder scan. Pinned paths are supplied by the host (Sm01) alongside
        /// <see cref="Discover"/>'s own results, since the derived save-path folder does not cover
        /// every location a study could realistically live in (renamed/relocated/copied .gh files,
        /// studies shared from someone else's machine, etc.).
        /// </summary>
        public bool IsPinned { get; set; }

        public bool CanView => Study != null;
        public bool CanResume => Compatibility == WasperStudyCompatibilityLevel.Ready ||
            Compatibility == WasperStudyCompatibilityLevel.Warning;

        public string StatusLabel => Compatibility switch
        {
            WasperStudyCompatibilityLevel.Ready => "Ready",
            WasperStudyCompatibilityLevel.Warning => "Warnings",
            WasperStudyCompatibilityLevel.MappingRequired => "Map sliders",
            WasperStudyCompatibilityLevel.Incompatible => "View only",
            _ => "Unreadable"
        };

        public override string ToString()
        {
            if (IsCurrent)
                return $"Current study (live) - {Study?.Iterations?.Count ?? 0} iterations";
            string name = Study?.RunName;
            if (string.IsNullOrWhiteSpace(name))
                name = Path.GetFileName(Path.GetDirectoryName(FilePath)) ?? "Unreadable study";
            int iterations = Study?.Iterations?.Count ?? 0;
            string pinTag = IsPinned ? " (pinned)" : string.Empty;
            return $"{name}{pinTag} - {iterations} iterations - {StatusLabel}";
        }
    }

    public static class WasperStudyCatalog
    {
        // Must track WasperStudy.SchemaVersion's current default (WASPer_StudyTypes.cs) and the
        // floor WASPer_Sm01Persistence.ReadStudyState upgrades every loaded study to. When those
        // rise, raise this too - otherwise every study saved under the new default schema is
        // marked Incompatible here and silently cannot be loaded/resumed from the Study Library,
        // even though nothing about it is actually unreadable. Schema 4 only added the Dashboard
        // settings block (WASPer_Sm01Persistence.cs) and is a strictly additive migration.
        public const int SupportedSchemaVersion = 4;

        public static List<WasperStudyCatalogEntry> Discover(
            string outputRoot,
            GH_Document document,
            WasperKpiSet currentKpis)
        {
            var entries = new List<WasperStudyCatalogEntry>();
            string simulations = string.IsNullOrWhiteSpace(outputRoot)
                ? string.Empty
                : Path.Combine(outputRoot, "Simulations");
            if (!Directory.Exists(simulations))
                return entries;

            foreach (string filePath in Directory
                .EnumerateFiles(simulations, "study.json", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(Evaluate(filePath, document, currentKpis));
            }
            return entries
                .OrderByDescending(entry => entry.Study?.UpdatedUtc ?? DateTime.MinValue)
                .ToList();
        }

        public static WasperStudyCatalogEntry Evaluate(
            string filePath,
            GH_Document document,
            WasperKpiSet currentKpis)
        {
            var entry = new WasperStudyCatalogEntry { FilePath = filePath };
            try
            {
                entry.Study = WasperStudyStorage.Load(filePath);
                if (entry.Study == null)
                    throw new InvalidDataException("The file does not contain a WASPer study.");
            }
            catch (Exception exception)
            {
                entry.Compatibility = WasperStudyCompatibilityLevel.Unreadable;
                entry.Issues.Add(exception.Message);
                return entry;
            }

            entry.Compatibility = WasperStudyCompatibilityLevel.Ready;
            if (entry.Study.SchemaVersion <= 0 ||
                entry.Study.SchemaVersion > SupportedSchemaVersion)
            {
                SetLevel(entry, WasperStudyCompatibilityLevel.Incompatible);
                entry.Issues.Add(
                    $"Study schema {entry.Study.SchemaVersion} is not supported by schema " +
                    $"{SupportedSchemaVersion}.");
            }

            string currentDefinition = document?.FilePath ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(entry.Study.DefinitionPath) &&
                !string.IsNullOrWhiteSpace(currentDefinition) &&
                !PathsEqual(entry.Study.DefinitionPath, currentDefinition))
            {
                SetLevel(entry, WasperStudyCompatibilityLevel.Warning);
                entry.Issues.Add("The study was created from a different Grasshopper definition.");
            }

            foreach (WasperStudyParameter parameter in
                entry.Study.Parameters?.Where(item => item.Enabled) ??
                Enumerable.Empty<WasperStudyParameter>())
            {
                if (parameter.Samples < 1 || parameter.Maximum < parameter.Minimum)
                {
                    SetLevel(entry, WasperStudyCompatibilityLevel.Incompatible);
                    entry.Issues.Add($"Parameter '{parameter.Name}' has an invalid saved domain.");
                    continue;
                }
                GH_NumberSlider slider = document?.FindObject(parameter.SliderId, true) as GH_NumberSlider;
                if (slider?.Slider == null)
                {
                    SetLevel(entry, WasperStudyCompatibilityLevel.MappingRequired);
                    entry.Issues.Add($"Slider '{parameter.Name}' is missing.");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(parameter.SliderAccuracy) &&
                    !string.Equals(
                        parameter.SliderAccuracy,
                        slider.Slider.Type.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    SetLevel(entry, WasperStudyCompatibilityLevel.Incompatible);
                    entry.Issues.Add($"Slider '{parameter.Name}' changed numeric accuracy type.");
                }
                if (parameter.SliderDecimalPlaces > 0 &&
                    parameter.SliderDecimalPlaces != slider.Slider.DecimalPlaces)
                {
                    SetLevel(entry, WasperStudyCompatibilityLevel.Incompatible);
                    entry.Issues.Add($"Slider '{parameter.Name}' changed decimal precision.");
                }
                double minimum = (double)slider.Slider.Minimum;
                double maximum = (double)slider.Slider.Maximum;
                if (parameter.Minimum < minimum - 1e-9 || parameter.Maximum > maximum + 1e-9)
                {
                    SetLevel(entry, WasperStudyCompatibilityLevel.Incompatible);
                    entry.Issues.Add($"Slider '{parameter.Name}' no longer contains the saved range.");
                }
                string currentName = string.IsNullOrWhiteSpace(slider.NickName)
                    ? slider.Name
                    : slider.NickName;
                if (!string.Equals(parameter.Name, currentName, StringComparison.Ordinal))
                {
                    SetLevel(entry, WasperStudyCompatibilityLevel.Warning);
                    entry.Issues.Add($"Slider '{parameter.Name}' is now named '{currentName}'.");
                }
            }

            List<Guid> duplicateSliderIds = (entry.Study.Parameters ?? new List<WasperStudyParameter>())
                .Where(parameter => parameter.Enabled && parameter.SliderId != Guid.Empty)
                .GroupBy(parameter => parameter.SliderId)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();
            if (duplicateSliderIds.Count > 0)
            {
                SetLevel(entry, WasperStudyCompatibilityLevel.Incompatible);
                entry.Issues.Add("Multiple saved parameters resolve to the same Number Slider.");
            }

            HashSet<string> currentKeys = (currentKpis?.Items ?? new List<WasperKpi>())
                .Where(kpi => kpi != null)
                .Select(kpi => kpi.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<string> savedKeys = (entry.Study.Iterations?.LastOrDefault()?.Kpis ??
                new List<WasperKpi>())
                .Where(kpi => kpi != null)
                .Select(kpi => kpi.Key)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            int missingKpis = savedKeys.Count(key => !currentKeys.Contains(key));
            if (missingKpis > 0)
            {
                SetLevel(entry, WasperStudyCompatibilityLevel.Warning);
                entry.Issues.Add($"{missingKpis} saved KPI field(s) are not present in the live definition.");
            }
            return entry;
        }

        private static void SetLevel(
            WasperStudyCatalogEntry entry,
            WasperStudyCompatibilityLevel requested)
        {
            if ((int)requested > (int)entry.Compatibility)
                entry.Compatibility = requested;
        }

        private static bool PathsEqual(string first, string second)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(first),
                    Path.GetFullPath(second),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}
