using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using ClosedXML.Excel;
using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json;
using Rhino.Geometry;


namespace WASPer_3DP.Components._1_2_Studies
{
    public sealed partial class wsp_Sm01_WASPer_Study_Manager
    {
        private void SaveStudySafely()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_studyFolder))
                    _studyFolder = ResolveStudyFolder(_study.RunName, _currentFilePath);
                string path = WasperStudyStorage.Save(_study, _studyFolder);
                if (!_lastWrittenFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
                    _lastWrittenFiles.Add(path);
            }
            catch (Exception exception)
            {
                _studyStatus = "Study save failed: " + exception.Message;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, _studyStatus);
            }
        }

        /// <summary>
        /// Manual, on-demand save requested from the Study tab's "Save Study" button. The active
        /// study is already auto-saved after every run/resume/stop/capture/clear (SaveStudySafely
        /// above), so this exists for the times a user wants to force a checkpoint to disk without
        /// changing any study state - e.g. right after editing parameter rows in the grid.
        /// </summary>
        private void SaveStudyClicked()
        {
            if (_viewedStudy != null)
            {
                _studyStatus = "Load this saved study as active before saving over it.";
                UpdateStudyWindow();
                return;
            }
            SaveStudySafely();
            if (!_studyStatus.StartsWith("Study save failed", StringComparison.OrdinalIgnoreCase))
                _studyStatus = $"Saved study '{_study.RunName}' to {_studyFolder}.";
            RefreshStudyCatalog();
            UpdateStudyWindow();
        }

        private string ResolveStudyFolder(string runName, string filePath)
        {
            string root = ResolveOutputFolder(filePath);
            string safeName = ResolveBaseName(runName);
            return Path.Combine(root, "Simulations", safeName);
        }

        private static string UniqueParameterKey(
            IDictionary<string, double> values,
            string requested,
            Guid id)
        {
            string key = string.IsNullOrWhiteSpace(requested) ? "Parameter" : requested;
            return values.ContainsKey(key) ? $"{key}_{id.ToString("N").Substring(0, 6)}" : key;
        }

        private static List<WasperKpi> CloneKpis(IEnumerable<WasperKpi> kpis)
        {
            string json = JsonConvert.SerializeObject(kpis ?? Enumerable.Empty<WasperKpi>());
            return JsonConvert.DeserializeObject<List<WasperKpi>>(json) ?? new List<WasperKpi>();
        }

        private void WriteStudyState(GH_IWriter writer)
        {
            writer.SetString(StudyJsonKey, JsonConvert.SerializeObject(_study));
            writer.SetString("study_slider_ids", string.Join(";", _linkedSliderIds));
        }

        private void ReadStudyState(GH_IReader reader)
        {
            if (reader.ItemExists(StudyJsonKey))
            {
                try
                {
                    _study = JsonConvert.DeserializeObject<WasperStudy>(
                        reader.GetString(StudyJsonKey)) ?? new WasperStudy();
                }
                catch
                {
                    _study = new WasperStudy();
                }
            }
            _linkedSliderIds.Clear();
            if (reader.ItemExists("study_slider_ids"))
            {
                foreach (string token in reader.GetString("study_slider_ids").Split(
                    new[] { ';' },
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Guid.TryParse(token, out Guid id))
                        _linkedSliderIds.Add(id);
                }
            }
            if (_linkedSliderIds.Count == 0 && _study?.Parameters != null)
                _linkedSliderIds.AddRange(_study.Parameters.Select(parameter => parameter.SliderId));
            _lastReportPath = _study?.Report?.OutputPath ?? string.Empty;
            _study ??= new WasperStudy();
            int loadedSchema = _study.SchemaVersion;
            _study.Report ??= new WasperReportSettings();
            _study.Snapshot ??= new WasperSnapshotSettings();
            _study.Dashboard ??= new WasperDashboardSettings();
            if (loadedSchema < 3)
                _study.Snapshot.Enabled = true;
            _study.SchemaVersion = Math.Max(4, _study.SchemaVersion);
            if (!string.IsNullOrWhiteSpace(_lastReportPath))
                _reportStatus = "Last PDF: " + _lastReportPath;
        }

    }
}
