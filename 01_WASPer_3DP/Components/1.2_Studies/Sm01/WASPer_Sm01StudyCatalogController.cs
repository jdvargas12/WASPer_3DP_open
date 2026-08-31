using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Grasshopper.Kernel;
using Newtonsoft.Json;

namespace WASPer_3DP.Components._1_2_Studies
{
    public sealed partial class wsp_Sm01_WASPer_Study_Manager
    {
        private readonly List<WasperStudyCatalogEntry> _studyCatalog =
            new List<WasperStudyCatalogEntry>();

        /// <summary>
        /// study.json paths the user has explicitly browsed to via "Browse..." because the
        /// automatic folder scan (derived from the current save path) did not reach them - for
        /// example a study saved from a renamed, relocated, or copied .gh file. Persisted with the
        /// component (see Write/Read) so a pinned study keeps appearing without re-browsing.
        /// </summary>
        private readonly List<string> _pinnedStudyPaths = new List<string>();
        private string _selectedStudyPath = string.Empty;
        private WasperStudy _viewedStudy;

        private void RefreshStudyCatalog()
        {
            _studyCatalog.Clear();
            _studyCatalog.Add(new WasperStudyCatalogEntry
            {
                IsCurrent = true,
                Study = _study,
                Compatibility = WasperStudyCompatibilityLevel.Ready
            });

            GH_Document document = OnPingDocument();
            List<WasperStudyCatalogEntry> discovered = WasperStudyCatalog.Discover(
                ResolveOutputFolder(_currentFilePath),
                document,
                _currentSet);
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (WasperStudyCatalogEntry entry in discovered)
            {
                entry.IsPinned = _pinnedStudyPaths.Contains(entry.FilePath, StringComparer.OrdinalIgnoreCase);
                seenPaths.Add(entry.FilePath);
                _studyCatalog.Add(entry);
            }
            // Pinned paths the automatic scan did not already surface (the whole point of pinning
            // one) get evaluated the same way Discover evaluates its own results, so a pinned study
            // is subject to the exact same compatibility checks as an auto-discovered one.
            foreach (string pinnedPath in _pinnedStudyPaths.Where(path => !seenPaths.Contains(path)))
            {
                WasperStudyCatalogEntry entry = WasperStudyCatalog.Evaluate(
                    pinnedPath, document, _currentSet);
                entry.IsPinned = true;
                _studyCatalog.Add(entry);
            }
            List<WasperStudyCatalogEntry> ordered = _studyCatalog
                .Where(entry => !entry.IsCurrent)
                .OrderByDescending(entry => entry.Study?.UpdatedUtc ?? DateTime.MinValue)
                .ToList();
            _studyCatalog.RemoveAll(entry => !entry.IsCurrent);
            _studyCatalog.AddRange(ordered);

            WasperStudyCatalogEntry selected = _studyCatalog.FirstOrDefault(entry => string.Equals(
                entry.FilePath ?? string.Empty,
                _selectedStudyPath ?? string.Empty,
                StringComparison.OrdinalIgnoreCase));
            if (selected == null)
            {
                _selectedStudyPath = string.Empty;
                _viewedStudy = null;
            }
            else
            {
                _viewedStudy = selected.IsCurrent ? null : CloneStudy(selected.Study);
            }
            _form?.UpdateStudyLibrary(_studyCatalog, _selectedStudyPath);
        }

        /// <summary>
        /// Lets the user point the Study Library at a study.json the automatic save-path-derived
        /// scan cannot reach - most commonly one saved from a since-renamed, relocated, or copied
        /// .gh file, or one shared from a colleague's machine. The chosen path is pinned so it
        /// keeps appearing on future refreshes without browsing again.
        /// </summary>
        private void BrowseForStudy()
        {
            string startDirectory = ResolveStudyBrowseStartDirectory();
            string selectedFile = SelectSm01StudyFile(startDirectory);
            if (string.IsNullOrWhiteSpace(selectedFile))
                return;

            string chosen = Path.GetFullPath(selectedFile);
            if (!_pinnedStudyPaths.Contains(chosen, StringComparer.OrdinalIgnoreCase))
                _pinnedStudyPaths.Add(chosen);
            _selectedStudyPath = chosen;
            _studyStatus = $"Pinned study at {chosen}.";
            OnObjectChanged(GH_ObjectEventType.Options);
            RefreshStudyCatalog();
        }

        private string ResolveStudyBrowseStartDirectory()
        {
            string simulations = Path.Combine(ResolveOutputFolder(_currentFilePath), "Simulations");
            if (Directory.Exists(simulations))
                return simulations;
            string documentPath = OnPingDocument()?.FilePath;
            string documentFolder = string.IsNullOrWhiteSpace(documentPath)
                ? string.Empty
                : Path.GetDirectoryName(documentPath);
            return !string.IsNullOrWhiteSpace(documentFolder) && Directory.Exists(documentFolder)
                ? documentFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        private void ForgetPinnedStudy(WasperStudyCatalogEntry entry)
        {
            if (entry == null || entry.IsCurrent || !entry.IsPinned)
                return;
            _pinnedStudyPaths.RemoveAll(path =>
                string.Equals(path, entry.FilePath, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(_selectedStudyPath, entry.FilePath, StringComparison.OrdinalIgnoreCase))
                _selectedStudyPath = string.Empty;
            _studyStatus = $"Removed pinned study '{entry.Study?.RunName}' from the Study Library.";
            OnObjectChanged(GH_ObjectEventType.Options);
            RefreshStudyCatalog();
        }

        private void SelectCatalogStudy(WasperStudyCatalogEntry entry)
        {
            if (entry == null || entry.IsCurrent)
            {
                _selectedStudyPath = string.Empty;
                _viewedStudy = null;
            }
            else if (entry.CanView)
            {
                _selectedStudyPath = entry.FilePath;
                _viewedStudy = CloneStudy(entry.Study);
            }
            OnObjectChanged(GH_ObjectEventType.Options);
            UpdateStudyWindow();
        }

        private void LoadCatalogStudy(WasperStudyCatalogEntry entry)
        {
            if (!TryLoadCatalogStudy(entry))
                return;
            _studyStatus = $"Loaded saved study '{_study.RunName}' as the active study.";
            OnObjectChanged(GH_ObjectEventType.Options);
            RefreshStudyCatalog();
            UpdateStudyWindow();
        }

        private void ResumeCatalogStudy(WasperStudyCatalogEntry entry)
        {
            if (!TryLoadCatalogStudy(entry))
                return;
            _studyStatus = $"Resuming saved study '{_study.RunName}'.";
            OnObjectChanged(GH_ObjectEventType.Options);
            RefreshStudyCatalog();
            ResumeStudy(_study.Parameters);
        }

        private bool TryLoadCatalogStudy(WasperStudyCatalogEntry entry)
        {
            if (entry == null || entry.IsCurrent || entry.Study == null)
                return false;
            if (!entry.CanResume)
            {
                ShowSm01Warning(
                    "This study cannot be loaded for execution:\r\n\r\n" +
                    string.Join("\r\n", entry.Issues.Select(issue => "- " + issue)),
                    "Study compatibility");
                return false;
            }
            if (entry.Compatibility == WasperStudyCompatibilityLevel.Warning)
            {
                bool confirmed = ConfirmSm01Warning(
                    "This study has compatibility warnings:\r\n\r\n" +
                    string.Join("\r\n", entry.Issues.Select(issue => "- " + issue)) +
                    "\r\n\r\nDo you want to continue?",
                    "Study compatibility");
                if (!confirmed)
                    return false;
            }

            _study = CloneStudy(entry.Study);
            _studyFolder = Path.GetDirectoryName(entry.FilePath) ?? string.Empty;
            _linkedSliderIds.Clear();
            _linkedSliderIds.AddRange(_study.Parameters
                .Where(parameter => parameter.SliderId != Guid.Empty)
                .Select(parameter => parameter.SliderId));
            _selectedStudyPath = string.Empty;
            _viewedStudy = null;
            SyncLinkedParameters();
            return true;
        }

        private static WasperStudy CloneStudy(WasperStudy study)
        {
            if (study == null)
                return new WasperStudy();
            string json = JsonConvert.SerializeObject(study);
            return JsonConvert.DeserializeObject<WasperStudy>(json) ?? new WasperStudy();
        }
    }
}
