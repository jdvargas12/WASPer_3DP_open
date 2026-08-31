using System;
using System.Collections.Generic;
using System.Drawing;

namespace WASPer_3DP.Components._1_2_Studies
{
    public sealed partial class wsp_Sm01_WASPer_Study_Manager
    {
        /// <summary>
        /// UI-toolkit-independent contract between the Sm01 component/controller and its manager
        /// window. The legacy WinForms view implements this during migration; the final Eto view
        /// replaces it without changing study execution, persistence, or fabrication logic.
        /// </summary>
        private interface ISm01ManagerView
        {
            event Action ViewClosed;
            event Action LiveStatusPollTick;
            event Action<IEnumerable<string>> SelectionApplied;
            event Action<string, string, string> ExportSettingsChanged;
            event Action<string> ExportLayoutChanged;
            event Action<string, string, string> ResetRequested;
            event Action LinkSelectedSlidersRequested;
            event Action<IEnumerable<Guid>> UnlinkSlidersRequested;
            event Action<IEnumerable<Guid>> RestoreParameterDefaultsRequested;
            event Action<IEnumerable<WasperStudyParameter>> RunStudyRequested;
            event Action<IEnumerable<WasperStudyParameter>> ResumeStudyRequested;
            event Action RefreshStudyLibraryRequested;
            event Action BrowseStudyRequested;
            event Action<WasperStudyCatalogEntry> ForgetPinnedStudyRequested;
            event Action<WasperStudyCatalogEntry> StudyLibrarySelectionChanged;
            event Action<WasperStudyCatalogEntry> LoadSavedStudyRequested;
            event Action<WasperStudyCatalogEntry> ResumeSavedStudyRequested;
            event Action StopStudyRequested;
            event Action CaptureIterationRequested;
            event Action ClearIterationsRequested;
            event Action SaveStudyRequested;
            event Action<WasperReportSettings> GenerateReportRequested;
            event Action<WasperReportSettings> ReportSettingsChanged;
            event Action<IEnumerable<string>> SampleNameTemplateChanged;
            event Action<IEnumerable<string>> GroupOrderChanged;
            event Action GroupOrderResetRequested;
            event Action<string, bool> GroupEnabledChanged;
            event Action<Guid, bool> SourceEnabledChanged;
            event Action<bool> ShowValuesChanged;
            event Action<bool> WriteWithRunChanged;
            event Action<WasperFabricationUnitMode> FabricationUnitModeChanged;
            event Action<WasperSnapshotSettings> SnapshotSettingsChanged;
            event Action<WasperDashboardSettings> DashboardSettingsChanged;
            event Action<int> ShowIterationRequested;
            event Action LinkVisualizationRequested;
            event Action UnlinkVisualizationRequested;
            event Action<string, string> ProcessViewerExportRequested;
            event Action<string> ProcessViewerLaunchRequested;
            event Action<string> ProcessViewerOpenBrowserRequested;
            event Action ProcessViewerRefreshRequested;
            event Action<bool> ProcessViewerLiveToggleChanged;
            event Action ProcessViewerPushChangeRequested;
            event Action<string> ProcessViewerOpenFolderRequested;
            event Action<string, string, bool> DumpFullStudyRequested;
            event Action<string> DumpStudyOpenFolderRequested;

            bool IsClosed { get; }
            Rectangle LastNormalBounds { get; }
            int CurrentDpi { get; }

            void ShowOwned();
            void RestoreAndActivate();
            void Close();

            void UpdateFabricationUnits(
                WasperFabricationUnitMode selectedMode,
                int? sourceUnitCode);
            void UpdateKpis(
                WasperKpiSet set,
                IEnumerable<string> disabledKeys,
                IEnumerable<string> disabledGroups,
                IReadOnlyDictionary<Guid, bool> sourceStates,
                bool showValues);
            void UpdateExportControls(
                string fileName,
                string filePath,
                bool filePathIsDefault,
                string format,
                string layout,
                bool fileNameConnected,
                string status);
            void SetWriteStatus(string status);
            void UpdateGcode(
                IEnumerable<List<string>> branches,
                IEnumerable<string> capturedFiles);
            void UpdateSnapshotSettings(WasperSnapshotSettings settings);
            void UpdateStudyLibrary(
                IEnumerable<WasperStudyCatalogEntry> entries,
                string selectedPath);
            void UpdateStudy(
                IEnumerable<WasperStudyParameter> parameters,
                IEnumerable<WasperStudyIteration> iterations,
                string status,
                double progress,
                bool running,
                bool viewingSavedStudy);
            void UpdateSampleNameComposer(
                IEnumerable<SampleNamePropertyOption> options,
                IEnumerable<string> selectedTokens,
                bool inputConnected,
                string inputValue,
                string preview);
            void UpdateDashboardSnapshotFolder(string folder);
            void ApplyDashboardSettings(WasperDashboardSettings settings);
            void UpdateReport(WasperReportSettings settings, string status);
            void UpdateProcessViewer(
                string sampleName,
                string defaultFolder,
                string defaultJobId,
                bool hasPath,
                bool hasMotionPlan,
                int pathBranches,
                int motions,
                string jsonPath,
                bool viewerAvailable,
                string viewerStatus,
                string localViewerUrl,
                bool webViewerRuntimeAvailable,
                string webViewerRuntimeStatus);
            void SetProcessViewerResult(
                string jsonPath,
                string status,
                bool viewerAvailable,
                bool webViewerRuntimeAvailable,
                string webViewerRuntimeStatus);
            void SetLiveToggleState(bool enabled);
            void SetLiveViewerStatus(string text);
            void UpdateMobileAccess(IReadOnlyList<MobileAccessLink> links, string status);
            void UpdateDumpStudySection(string defaultFolder, string defaultName, bool canBuild);
            void SetDumpStudyResult(string status);
        }
    }
}
