using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ClosedXML.Excel;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;
using Newtonsoft.Json;

namespace WASPer_3DP.Components._1_2_Studies
{
    /// <summary>
    /// Merges global KPI sets, extracts built-in wsp_path KPIs, and lets the user
    /// select the records that should continue to a data sheet or report component.
    /// </summary>
    public sealed partial class wsp_Sm01_WASPer_Study_Manager : GH_Component
    {
        private readonly string _version;
        private readonly HashSet<string> _disabledKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _disabledKpiGroups =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _kpiGroupOrder = new List<string>();
        private bool _showKpiValues;
        private WasperFabricationUnitMode _fabricationUnitMode =
            WasperFabricationUnitMode.Auto;
        private int? _currentPathKpiUnitCode;
        private ISm01ManagerView _form;
        private WasperKpiSet _currentSet;
        private WasperKpiSet _cachedSourceSet = new WasperKpiSet
        {
            SourceComponent = "Sm01 KPI source cache"
        };
        private Rectangle _managerBounds = new Rectangle(120, 80, 1400, 800);
        private int _managerDpi = 96;
        private string _editorFileName = "WASPer_Study";
        private string _editorFilePath = string.Empty;
        private string _editorFormat = "All";
        private string _editorExportLayout = "Iterations in rows";
        private bool _writeWithRun = true;
        private string _activeRunNameOverride = string.Empty;
        private string _currentFileName = "WASPer_Study";
        private string _currentFilePath = string.Empty;
        private string _currentSampleNameOverride = string.Empty;
        private bool _sampleNameInputConnected;
        private readonly List<string> _sampleNameTokens = new List<string>
        {
            "iteration",
            "kpi:infill.cell_name_short",
            "kpi:infill.cell_count_x",
            "kpi:infill.cell_count_y",
            "kpi:infill.cell_count_z"
        };
        private string _lastWriteInfo = "Ready.";
        private List<string> _lastWrittenFiles = new List<string>();
        private static Bitmap _icon;

        public wsp_Sm01_WASPer_Study_Manager()
            : base(
                "wsp_Sm01_WASPer Study Manager",
                "Study Manager",
                "Central WASPer design-study manager. Links Grasshopper Number Sliders, runs " +
                "scheduled parameter iterations, captures global KPI sets, stores study data, " +
                "captures per-solution G-code, and exports selected results. Use the button " +
                "below the component to open the " +
                "persistent Study Manager window.",
                WASPerPalette.Performance,
                "1.2_Studies")
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            _version = version == null
                ? "v1.0.x"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
            Message = _version;
        }

        public override Guid ComponentGuid =>
            new Guid("F2D4C8B6-2A4E-4F92-9D54-8B2E6C7A1F30");

        public override GH_Exposure Exposure => GH_Exposure.primary;

        protected override Bitmap Icon => _icon ??= CreateIcon();

        public override void CreateAttributes()
        {
            m_attributes = new KpiManagerAttributes(this);
        }

        protected override void RegisterInputParams(GH_InputParamManager parameters)
        {
            parameters.AddGenericParameter(
                "wsp_path",
                "wsp_path",
                "Optional WASPer Print Path. Its built-in global Fabrication KPIs are " +
                "extracted automatically and merged with the connected KPI sets.",
                GH_ParamAccess.item);
            parameters.AddGenericParameter(
                "kpi_sets",
                "kpi_sets",
                "Optional list of global KPI sets emitted by performance components, Sm02 " +
                "identified KPI subsets, or Sm03 custom KPI sets. Subsets of the same KPI " +
                "type remain independent. The list's first-occurrence order defines the default " +
                "left-to-right Manager group order; drag group titles to customize it. " +
                "For example: Pr01 print_kpis, Pr03 risk_kpis, Ch04 por_kpis, " +
                "Ch07 fp_kpis, In08-In10 in_kpis, and Ht01 thermal_kpis.",
                GH_ParamAccess.list);
            parameters.AddTextParameter(
                "run_name",
                "run_name",
                "Study/run name and export base name. When unconnected, use the run-name " +
                "control in the Study Manager. Example: wall_iteration_study.",
                GH_ParamAccess.item);
            parameters.AddTextParameter(
                "sample_name",
                "s_name",
                "Optional explicit sample name for the current solution. When connected and " +
                "non-empty, this value overrides the Manager's property-based name composer. " +
                "When unconnected, the default template is iteration_cell-short_countX.countY.countZ, " +
                "for example 1_Di_3.1.3.",
                GH_ParamAccess.item);
            parameters.AddTextParameter(
                "gcode",
                "gcode",
                "Optional G-code text tree, normally connected directly from Gc03 g_code. " +
                "Each branch represents one output file. The current code can be inspected " +
                "in the G-code tab; during a study, every recomputed solution is saved under " +
                "WASPer_<definition name>\\Simulations\\<run_name>\\Gcodes.",
                GH_ParamAccess.tree);
            parameters.AddGenericParameter(
                "xr_pack",
                "xr_pack",
                "Optional bundle from Sm05 XR Scene Params: context geometry, materials, and " +
                "an externally-driven simulation parameter for the XR export/live viewer. When " +
                "Sm05's own sim_par input is connected, Sm01 disables the web viewer's Play/" +
                "Stop/time-slider controls, since an external source (typically Gc05) already " +
                "owns the simulated print position.",
                GH_ParamAccess.item);
            for (int index = 0; index < parameters.ParamCount; index++)
                parameters[index].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager parameters)
        {
            parameters.AddGenericParameter(
                "kpi_set",
                "kpi_set",
                "Filtered, ordered global KPI set selected in the KPI Manager.",
                GH_ParamAccess.item);
            parameters.AddGenericParameter(
                "study_data",
                "study",
                "Versioned WASPer Study containing linked parameter definitions and every " +
                "captured iteration with its global KPI snapshot and saved G-code paths.",
                GH_ParamAccess.item);
            parameters.AddTextParameter(
                "files",
                "files",
                "Most recently written data, report, or G-code file paths.",
                GH_ParamAccess.list);
            parameters.AddNumberParameter(
                "progress",
                "progress",
                "Study progress from 0.0 to 1.0.",
                GH_ParamAccess.item);
            parameters.AddTextParameter(
                "info",
                "info",
                "Combined KPI, study-run, G-code-capture, export, and XR scene status summary.",
                GH_ParamAccess.item);
        }

        protected override void SolveInstance(IGH_DataAccess dataAccess)
        {
            IGH_Goo pathGoo = null;
            WasperPrintPath path = null;
            if (dataAccess.GetData(0, ref pathGoo) && pathGoo is WasperPrintPathGoo typedPath)
                path = typedPath.Value;
            if (path != null && path.IsPartial)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Input wsp_path is marked partial. Sm01 will extract Fabrication KPIs and " +
                    "pass through only the partial/reconstructed path state, not necessarily the " +
                    "full original print. Typically produced by Gc05 WASPer Simulation or Pp04 " +
                    "Visualize Print Path when trimmed by a simulation time or progress value.");
            }
            IGH_Goo xrPackGoo = null;
            bool xrPackConnected = Params.Input[5].SourceCount > 0;
            _currentXrScenePack = xrPackConnected &&
                dataAccess.GetData(5, ref xrPackGoo)
                    ? (xrPackGoo as WasperXrScenePackGoo)?.Value
                    : null;

            _currentProcessViewerPath = path;
            _currentPathKpiUnitCode = path?.KpiUnits;
            int resolvedFabricationUnitCode = _fabricationUnitMode == WasperFabricationUnitMode.Auto
                ? _currentPathKpiUnitCode ?? 0
                : (int)_fabricationUnitMode;
            foreach (WasperStudyIteration iteration in
                _study?.Iterations ?? new List<WasperStudyIteration>())
            {
                WasperPathKpiExtractor.ConvertFabricationKpis(
                    iteration.Kpis,
                    resolvedFabricationUnitCode);
            }

            var gooSets = new List<IGH_Goo>();
            dataAccess.GetDataList(1, gooSets);

            var sets = new List<WasperKpiSet>();
            foreach (IGH_Goo goo in gooSets)
            {
                if (goo is WasperKpiSetGoo setGoo && setGoo.Value != null)
                    sets.Add(setGoo.Value);
            }

            UpdateSourceCache(sets);
            WasperKpiSet cachedFallback = CachedFallbackFor(sets);
            if (cachedFallback.Items.Count > 0)
                sets.Add(cachedFallback);

            if (path != null)
            {
                sets.Insert(0, WasperPathKpiExtractor.Extract(
                    path,
                    "wsp_path",
                    _fabricationUnitMode));
            }

            _currentSet = WasperKpiSet.Merge(sets, Name);
            ApplyKpiGroupOrder(_currentSet);
            ApplySelection(_currentSet);

            string fileName = _editorFileName;
            string filePath = _editorFilePath;
            string sampleName = string.Empty;
            dataAccess.GetData(2, ref fileName);
            dataAccess.GetData(3, ref sampleName);
            GH_Structure<GH_String> gcodeTree;
            _currentGcodeBranches = dataAccess.GetDataTree(4, out gcodeTree) && gcodeTree != null
                ? gcodeTree.Branches
                    .Select(branch => branch
                        .Select(item => item?.Value ?? string.Empty)
                        .ToList())
                    .Where(branch => branch.Any(line => !string.IsNullOrWhiteSpace(line)))
                    .ToList()
                : new List<List<string>>();

            bool fileNameConnected = Params.Input[2].SourceCount > 0;
            _sampleNameInputConnected = Params.Input[3].SourceCount > 0;

            if (!fileNameConnected)
                fileName = _editorFileName;
            _currentFileName = fileName;
            _currentFilePath = filePath;
            _currentSampleNameOverride = _sampleNameInputConnected
                ? sampleName?.Trim() ?? string.Empty
                : string.Empty;

            if (_form != null && !_form.IsClosed)
            {
                _form.UpdateKpis(
                    _currentSet,
                    _disabledKeys,
                    _disabledKpiGroups,
                    KpiSourceStates(_currentSet),
                    _showKpiValues);
                _form.UpdateFabricationUnits(
                    _fabricationUnitMode,
                    _currentPathKpiUnitCode);
                _form.UpdateGcode(_currentGcodeBranches, CurrentGcodeFiles());
                _form.UpdateExportControls(
                    fileName,
                    string.IsNullOrWhiteSpace(filePath)
                        ? ResolveOutputFolder(filePath)
                        : filePath,
                    string.IsNullOrWhiteSpace(filePath),
                    _editorFormat,
                    _editorExportLayout,
                    fileNameConnected,
                    _lastWriteInfo);
            }

            List<WasperKpi> enabled = _currentSet.EnabledItems.ToList();
            dataAccess.SetData(0, new WasperKpiSetGoo(_currentSet));
            HandleStudySolve(fileName, filePath);
            UpdateProcessViewerWindow();
            dataAccess.SetData(1, new WasperStudyGoo(_study));
            dataAccess.SetDataList(2, _lastWrittenFiles);
            dataAccess.SetData(3, StudyProgress);
            dataAccess.SetData(4, StudyInfo(enabled.Count));
            Message = StudyMessage($"{_version} | {enabled.Count}/{_currentSet.Items.Count} enabled");
        }

        private void ApplySelection(WasperKpiSet set)
        {
            foreach (WasperKpi item in set.Items)
            {
                item.Enabled = !_disabledKpiGroups.Contains(item.DisplayGroup) &&
                    !_disabledKeys.Contains(item.Key) &&
                    IsKpiSourceEnabled(item.SourceInstanceId);
            }
        }

        private Dictionary<Guid, bool> KpiSourceStates(WasperKpiSet set)
        {
            return (set?.Items ?? new List<WasperKpi>())
                .Where(item => item != null && item.SourceInstanceId != Guid.Empty)
                .Select(item => item.SourceInstanceId)
                .Distinct()
                .ToDictionary(id => id, IsKpiSourceEnabled);
        }

        private void UpdateSourceCache(IEnumerable<WasperKpiSet> liveSets)
        {
            List<WasperKpiSet> refreshSets = (liveSets ?? Enumerable.Empty<WasperKpiSet>())
                .Where(set => set != null &&
                    set.SourceInstanceId != Guid.Empty &&
                    IsKpiSourceEnabled(set.SourceInstanceId))
                .ToList();
            HashSet<Guid> refreshedSources = refreshSets
                .Select(set => set.SourceInstanceId)
                .ToHashSet();
            if (refreshedSources.Count == 0)
                return;

            List<WasperKpi> retained = (_cachedSourceSet?.Items ?? new List<WasperKpi>())
                .Where(item => item != null && !refreshedSources.Contains(item.SourceInstanceId))
                .ToList();
            retained.AddRange(CloneKpiItems(refreshSets.SelectMany(
                set => set.Items ?? new List<WasperKpi>())));
            _cachedSourceSet = new WasperKpiSet
            {
                SourceComponent = "Sm01 KPI source cache",
                Items = retained
            };
        }

        private WasperKpiSet CachedFallbackFor(IEnumerable<WasperKpiSet> liveSets)
        {
            HashSet<string> liveKeys = (liveSets ?? Enumerable.Empty<WasperKpiSet>())
                .Where(set => set?.Items != null)
                .SelectMany(set => set.Items)
                .Where(item => item != null)
                .Select(item => item.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return new WasperKpiSet
            {
                SourceComponent = "Sm01 KPI source cache",
                Items = CloneKpiItems((_cachedSourceSet?.Items ?? new List<WasperKpi>())
                    .Where(item => item != null && !liveKeys.Contains(item.Key)))
            };
        }

        private static List<WasperKpi> CloneKpiItems(IEnumerable<WasperKpi> items)
        {
            string json = JsonConvert.SerializeObject(items ?? Enumerable.Empty<WasperKpi>());
            return JsonConvert.DeserializeObject<List<WasperKpi>>(json) ?? new List<WasperKpi>();
        }

        private bool IsKpiSourceEnabled(Guid sourceId)
        {
            if (sourceId == Guid.Empty)
                return true;
            GH_Document document = OnPingDocument() ?? Instances.ActiveCanvas?.Document;
            GH_ActiveObject source = document?.FindObject(sourceId, true) as GH_ActiveObject;
            return source != null && !source.Locked;
        }

        private void SetKpiSourceEnabled(Guid sourceId, bool enabled)
        {
            if (sourceId == Guid.Empty)
                return;
            GH_Document document = OnPingDocument() ?? Instances.ActiveCanvas?.Document;
            if (document == null)
                return;

            document.ScheduleSolution(1, scheduledDocument =>
            {
                GH_ActiveObject source = scheduledDocument.FindObject(sourceId, true) as GH_ActiveObject;
                if (source == null)
                    return;
                source.Locked = !enabled;
                if (enabled)
                    source.ExpireSolution(false);
                ApplySelection(_currentSet);
                ExpireSolution(true);
                Instances.ActiveCanvas?.Invalidate();
            });
        }

        internal IReadOnlyList<RectangleF> LinkedKpiSourceBounds()
        {
            GH_Document document = OnPingDocument() ?? Instances.ActiveCanvas?.Document;
            if (document == null)
                return Array.Empty<RectangleF>();
            return (_currentSet?.Items ?? new List<WasperKpi>())
                .Where(item => item != null && item.SourceInstanceId != Guid.Empty)
                .Select(item => item.SourceInstanceId)
                .Distinct()
                .Select(id => document.FindObject(id, true) as GH_ActiveObject)
                .Where(source => source?.Attributes != null)
                .Select(source => source.Attributes.Bounds)
                .ToList();
        }

        private void ApplyKpiGroupOrder(WasperKpiSet set)
        {
            if (set?.Items == null || _kpiGroupOrder.Count == 0)
                return;
            var ranks = _kpiGroupOrder
                .Select((group, index) => new { group, index })
                .GroupBy(item => item.group, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.First().index,
                    StringComparer.OrdinalIgnoreCase);
            set.Items = set.Items
                .Select((item, index) => new { item, index })
                .OrderBy(entry => ranks.TryGetValue(entry.item.DisplayGroup, out int rank)
                    ? rank
                    : int.MaxValue)
                .ThenBy(entry => entry.index)
                .Select(entry => entry.item)
                .ToList();
        }

        internal void ShowManager()
        {
            if (_form != null && !_form.IsClosed)
            {
                _form.RestoreAndActivate();
                return;
            }

            try
            {
                _form = new Sm01EtoManagerForm(
                    _currentSet ?? new WasperKpiSet(),
                    _disabledKeys,
                    _disabledKpiGroups,
                    KpiSourceStates(_currentSet),
                    _showKpiValues,
                    _writeWithRun,
                    _fabricationUnitMode,
                    _currentPathKpiUnitCode,
                    _managerDpi,
                    _managerBounds,
                    OnPingDocument()?.RhinoDocument);
            }
            catch (Exception exception)
            {
                _form = null;
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    "Study Manager could not open: " + exception.Message);
                Instances.RedrawCanvas();
                return;
            }
            _form.SelectionApplied += disabled =>
            {
                StoreSelection(disabled);
                ExpireSolution(true);
            };
            _form.GroupOrderChanged += StoreKpiGroupOrder;
            _form.GroupOrderResetRequested += ResetKpiGroupOrder;
            _form.GroupEnabledChanged += StoreKpiGroupEnabled;
            _form.SourceEnabledChanged += SetKpiSourceEnabled;
            _form.ShowValuesChanged += StoreShowKpiValues;
            _form.FabricationUnitModeChanged += StoreFabricationUnitMode;
            _form.WriteWithRunChanged += StoreWriteWithRun;
            _form.ExportSettingsChanged += (fileName, filePath, format) =>
            {
                _editorFileName = fileName;
                _editorFilePath = filePath;
                _editorFormat = NormalizeFormat(format);
                OnObjectChanged(GH_ObjectEventType.Options);
                RefreshStudyCatalog();
            };
            _form.ExportLayoutChanged += layout =>
            {
                _editorExportLayout = NormalizeExportLayout(layout);
                OnObjectChanged(GH_ObjectEventType.Options);
            };
            _form.ResetRequested += (fileName, filePath, format) =>
            {
                StoreEditorExportSettings(fileName, filePath, format);
                ResetExports(fileName, filePath, format);
                _form?.SetWriteStatus(_lastWriteInfo);
                ExpireSolution(true);
            };
            _form.LinkSelectedSlidersRequested += LinkSelectedSliders;
            _form.UnlinkSlidersRequested += UnlinkSliders;
            _form.RestoreParameterDefaultsRequested += RestoreParameterDefaults;
            _form.RunStudyRequested += StartStudyWithCollisionResolution;
            _form.ResumeStudyRequested += ResumeStudy;
            _form.RefreshStudyLibraryRequested += RefreshStudyCatalog;
            _form.BrowseStudyRequested += BrowseForStudy;
            _form.ForgetPinnedStudyRequested += ForgetPinnedStudy;
            _form.StudyLibrarySelectionChanged += SelectCatalogStudy;
            _form.LoadSavedStudyRequested += LoadCatalogStudy;
            _form.ResumeSavedStudyRequested += ResumeCatalogStudy;
            _form.StopStudyRequested += () => StopStudy(true);
            _form.CaptureIterationRequested += CaptureCurrentStudyIteration;
            _form.ClearIterationsRequested += ClearStudyIterations;
            _form.SaveStudyRequested += SaveStudyClicked;
            _form.GenerateReportRequested += GenerateStudyReport;
            _form.ReportSettingsChanged += StoreReportSettings;
            _form.SnapshotSettingsChanged += StoreSnapshotSettings;
            _form.DashboardSettingsChanged += StoreDashboardSettings;
            _form.ShowIterationRequested += ShowIterationInGrasshopper;
            _form.LinkVisualizationRequested += LinkSelectedVisualizationComponent;
            _form.UnlinkVisualizationRequested += UnlinkVisualizationComponent;
            _form.SampleNameTemplateChanged += StoreSampleNameTemplate;
            _form.ProcessViewerExportRequested += ExportProcessViewerJob;
            _form.ProcessViewerLaunchRequested += LaunchProcessViewer;
            _form.ProcessViewerOpenBrowserRequested += OpenWebViewerInBrowser;
            _form.ProcessViewerRefreshRequested += RefreshProcessViewerScene;
            _form.ProcessViewerLiveToggleChanged += SetLiveEnabled;
            _form.ProcessViewerPushChangeRequested += PushChangeNow;
            _form.ProcessViewerOpenFolderRequested += OpenProcessViewerFolder;
            _form.DumpFullStudyRequested += BuildStandalonePackage;
            _form.DumpStudyOpenFolderRequested += OpenDumpStudyFolder;
            // Keeps the "Live viewers" row current while the manager window sits open with
            // nothing in the document recomputing -- see the timer's own comment in
            // WASPer_Sm01ManagerForm.cs for why this can't just ride SolveInstance like the
            // rest of the live link does.
            _form.LiveStatusPollTick += () => TryRefreshLiveViewerStatus();
            _form.ViewClosed += ManagerClosed;
            _form.ShowOwned();
            _form.UpdateExportControls(
                _currentFileName,
                string.IsNullOrWhiteSpace(_currentFilePath)
                    ? ResolveOutputFolder(_currentFilePath)
                    : _currentFilePath,
                string.IsNullOrWhiteSpace(_currentFilePath),
                _editorFormat,
                _editorExportLayout,
                Params.Input[2].SourceCount > 0,
                _lastWriteInfo);
            _form.UpdateGcode(_currentGcodeBranches, CurrentGcodeFiles());
            RefreshStudyCatalog();
            UpdateStudyWindow();
            UpdateProcessViewerWindow();
            RefreshMobileAccess();
        }

        private void StoreSelection(IEnumerable<string> disabled)
        {
            _disabledKeys.Clear();
            foreach (string key in disabled ?? Enumerable.Empty<string>())
                _disabledKeys.Add(key);
        }

        private void StartStudyWithCollisionResolution(
            IEnumerable<WasperStudyParameter> configuredParameters)
        {
            string requestedName = ResolveBaseName(_currentFileName);
            string requestedFolder = ResolveStudyFolder(requestedName, _currentFilePath);
            string resolvedName = requestedName;
            if (Directory.Exists(requestedFolder))
            {
                StudyCollisionChoice choice = StudyCollisionDialog.Show(requestedName);
                if (choice == StudyCollisionChoice.Cancel)
                    return;
                if (choice == StudyCollisionChoice.Override)
                {
                    try
                    {
                        Directory.Delete(requestedFolder, true);
                    }
                    catch (Exception exception)
                    {
                        ShowSm01Error(
                            "The existing study folder could not be overridden:" +
                                Environment.NewLine + Environment.NewLine + exception.Message,
                            "WASPer Study");
                        return;
                    }
                }
                else
                {
                    resolvedName = NextSerializedStudyName(requestedName, _currentFilePath);
                }
            }

            _activeRunNameOverride = resolvedName;
            _study.RunName = resolvedName;
            _studyFolder = ResolveStudyFolder(resolvedName, _currentFilePath);
            if (!string.Equals(resolvedName, requestedName, StringComparison.OrdinalIgnoreCase) &&
                Params.Input[2].SourceCount == 0)
            {
                _editorFileName = resolvedName;
                _currentFileName = resolvedName;
                _form?.UpdateExportControls(
                    resolvedName,
                    string.IsNullOrWhiteSpace(_currentFilePath)
                        ? ResolveOutputFolder(_currentFilePath)
                        : _currentFilePath,
                    string.IsNullOrWhiteSpace(_currentFilePath),
                    _editorFormat,
                    _editorExportLayout,
                    false,
                    _lastWriteInfo);
            }
            RunStudyOptions runOptions = RunStudyOptionsDialog.Show(
                _study.GcodeEnabled,
                _study.Snapshot?.Enabled ?? true,
                _study.XrPathsEnabled);
            if (runOptions == null)
                return;
            _study.GcodeEnabled = runOptions.IncludeGcode;
            if (_study.Snapshot != null)
                _study.Snapshot.Enabled = runOptions.IncludeSnapshots;
            _study.XrPathsEnabled = runOptions.IncludeXrPaths;
            _form?.UpdateSnapshotSettings(_study.Snapshot);

            StartStudy(configuredParameters);
            if (!_studyRunning)
                _activeRunNameOverride = string.Empty;
        }

        private string NextSerializedStudyName(string baseName, string filePath)
        {
            int serial = 1;
            string candidate;
            do
            {
                candidate = $"{baseName}_{serial}";
                serial++;
            }
            while (Directory.Exists(ResolveStudyFolder(candidate, filePath)));
            return candidate;
        }

        private void StoreFabricationUnitMode(WasperFabricationUnitMode mode)
        {
            if (_fabricationUnitMode == mode)
                return;
            int targetUnitCode = mode == WasperFabricationUnitMode.Auto
                ? _currentPathKpiUnitCode ?? 0
                : (int)mode;
            foreach (WasperStudyIteration iteration in
                _study?.Iterations ?? new List<WasperStudyIteration>())
            {
                WasperPathKpiExtractor.ConvertFabricationKpis(
                    iteration.Kpis,
                    targetUnitCode);
            }
            if (_study != null)
                _study.UpdatedUtc = DateTime.UtcNow;
            _fabricationUnitMode = mode;
            OnObjectChanged(GH_ObjectEventType.Options);
            ExpireSolution(true);
        }

        private void StoreWriteWithRun(bool enabled)
        {
            if (_writeWithRun == enabled)
                return;
            _writeWithRun = enabled;
            OnObjectChanged(GH_ObjectEventType.Options);
        }

        private void StoreKpiGroupOrder(IEnumerable<string> groups)
        {
            List<string> requested = (groups ?? Enumerable.Empty<string>())
                .Where(group => !string.IsNullOrWhiteSpace(group))
                .Select(group => group.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (_kpiGroupOrder.SequenceEqual(requested, StringComparer.OrdinalIgnoreCase))
                return;
            _kpiGroupOrder.Clear();
            _kpiGroupOrder.AddRange(requested);
            OnObjectChanged(GH_ObjectEventType.Options);
            ExpireSolution(true);
        }

        private void ResetKpiGroupOrder()
        {
            if (_kpiGroupOrder.Count == 0)
                return;
            _kpiGroupOrder.Clear();
            OnObjectChanged(GH_ObjectEventType.Options);
            ExpireSolution(true);
        }

        private void StoreKpiGroupEnabled(string group, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(group))
                return;
            bool changed = enabled
                ? _disabledKpiGroups.Remove(group)
                : _disabledKpiGroups.Add(group);
            if (!changed)
                return;
            ApplySelection(_currentSet);
            OnObjectChanged(GH_ObjectEventType.Options);
            ExpireSolution(true);
        }

        private void StoreShowKpiValues(bool showValues)
        {
            if (_showKpiValues == showValues)
                return;
            _showKpiValues = showValues;
            OnObjectChanged(GH_ObjectEventType.Options);
        }

        private void StoreEditorExportSettings(string fileName, string filePath, string format)
        {
            _editorFileName = string.IsNullOrWhiteSpace(fileName) ? "WASPer_KPIs" : fileName.Trim();
            _editorFilePath = filePath?.Trim() ?? string.Empty;
            _editorFormat = NormalizeFormat(format);
            OnObjectChanged(GH_ObjectEventType.Options);
        }

        private void ManagerClosed()
        {
            if (_form != null)
            {
                _managerBounds = _form.LastNormalBounds;
                _managerDpi = _form.CurrentDpi;
                OnObjectChanged(GH_ObjectEventType.Options);
            }
            _form = null;
        }

        public override void RemovedFromDocument(GH_Document document)
        {
            StopStudy(true);
            if (_form != null && !_form.IsClosed)
                _form.Close();
            _liveViewerClient?.Dispose();
            _liveViewerClient = null;
            base.RemovedFromDocument(document);
        }

        public override bool Write(GH_IO.Serialization.GH_IWriter writer)
        {
            writer.SetString("disabled_kpi_keys", string.Join(";", _disabledKeys));
            writer.SetString(
                "disabled_kpi_groups",
                JsonConvert.SerializeObject(_disabledKpiGroups.ToList()));
            Rectangle bounds = _form != null && !_form.IsClosed
                ? _form.LastNormalBounds
                : _managerBounds;
            writer.SetInt32("manager_x", bounds.X);
            writer.SetInt32("manager_y", bounds.Y);
            writer.SetInt32("manager_width", bounds.Width);
            writer.SetInt32("manager_height", bounds.Height);
            writer.SetInt32(
                "manager_dpi",
                _form != null && !_form.IsClosed ? _form.CurrentDpi : _managerDpi);
            writer.SetString("editor_file_name", _editorFileName ?? string.Empty);
            writer.SetString("editor_file_path", _editorFilePath ?? string.Empty);
            writer.SetString("editor_format", _editorFormat ?? "All");
            writer.SetString("editor_export_layout", _editorExportLayout ?? "Iterations in rows");
            // JSON rather than the semicolon-join used elsewhere in this file (e.g.
            // pinned_study_paths below) because free-text template segments added from the Sample
            // Name tab can legitimately contain ';' or whitespace that a naive split would corrupt.
            writer.SetString("sample_name_tokens", JsonConvert.SerializeObject(_sampleNameTokens));
            writer.SetString("kpi_group_order", JsonConvert.SerializeObject(_kpiGroupOrder));
            writer.SetBoolean("show_kpi_values", _showKpiValues);
            writer.SetInt32("fabrication_unit_mode", (int)_fabricationUnitMode);
            writer.SetBoolean("write_with_run", _writeWithRun);
            writer.SetString("selected_study_path", _selectedStudyPath ?? string.Empty);
            writer.SetString("pinned_study_paths", string.Join(";", _pinnedStudyPaths));
            writer.SetString(
                "cached_kpi_sources",
                JsonConvert.SerializeObject(_cachedSourceSet ?? new WasperKpiSet()));
            WriteStudyState(writer);
            return base.Write(writer);
        }

        public override bool Read(GH_IO.Serialization.GH_IReader reader)
        {
            _disabledKeys.Clear();
            if (reader.ItemExists("disabled_kpi_keys"))
            {
                string stored = reader.GetString("disabled_kpi_keys");
                foreach (string key in stored.Split(
                    new[] { ';' },
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    _disabledKeys.Add(key);
                }
            }
            _disabledKpiGroups.Clear();
            if (reader.ItemExists("disabled_kpi_groups"))
            {
                try
                {
                    List<string> disabledGroups = JsonConvert.DeserializeObject<List<string>>(
                        reader.GetString("disabled_kpi_groups")) ?? new List<string>();
                    foreach (string group in disabledGroups.Where(group => !string.IsNullOrWhiteSpace(group)))
                        _disabledKpiGroups.Add(group.Trim());
                }
                catch
                {
                    _disabledKpiGroups.Clear();
                }
            }

            int x = reader.ItemExists("manager_x")
                ? reader.GetInt32("manager_x")
                : _managerBounds.X;
            int y = reader.ItemExists("manager_y")
                ? reader.GetInt32("manager_y")
                : _managerBounds.Y;
            int width = reader.ItemExists("manager_width")
                ? reader.GetInt32("manager_width")
                : _managerBounds.Width;
            int height = reader.ItemExists("manager_height")
                ? reader.GetInt32("manager_height")
                : _managerBounds.Height;
            _managerBounds = new Rectangle(
                x,
                y,
                Math.Max(560, width),
                Math.Max(320, height));
            _managerDpi = reader.ItemExists("manager_dpi")
                ? Math.Max(96, reader.GetInt32("manager_dpi"))
                : 96;
            if (reader.ItemExists("editor_file_name"))
                _editorFileName = reader.GetString("editor_file_name");
            if (reader.ItemExists("editor_file_path"))
                _editorFilePath = reader.GetString("editor_file_path");
            if (reader.ItemExists("editor_format"))
                _editorFormat = NormalizeFormat(reader.GetString("editor_format"));
            if (reader.ItemExists("editor_export_layout"))
            {
                _editorExportLayout = NormalizeExportLayout(
                    reader.GetString("editor_export_layout"));
            }
            if (reader.ItemExists("selected_study_path"))
                _selectedStudyPath = reader.GetString("selected_study_path") ?? string.Empty;
            _pinnedStudyPaths.Clear();
            if (reader.ItemExists("pinned_study_paths"))
            {
                _pinnedStudyPaths.AddRange(reader.GetString("pinned_study_paths")
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(path => path.Trim())
                    .Where(path => path.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase));
            }
            if (reader.ItemExists("sample_name_tokens"))
            {
                string stored = reader.GetString("sample_name_tokens") ?? string.Empty;
                List<string> storedTokens;
                try
                {
                    // Current format: a JSON string array (see Write above). Older files saved
                    // before free-text template segments existed used a semicolon-joined string,
                    // which is never valid JSON, so it deserializes to null/throws and falls
                    // through to the legacy split below.
                    storedTokens = JsonConvert.DeserializeObject<List<string>>(stored);
                }
                catch
                {
                    storedTokens = null;
                }
                if (storedTokens == null)
                {
                    storedTokens = stored
                        .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(token => token.Trim())
                        .Where(token => token.Length > 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                storedTokens = storedTokens.Where(token => !string.IsNullOrEmpty(token)).ToList();
                if (storedTokens.Count > 0)
                {
                    _sampleNameTokens.Clear();
                    _sampleNameTokens.AddRange(storedTokens);
                }
            }
            _kpiGroupOrder.Clear();
            if (reader.ItemExists("kpi_group_order"))
            {
                try
                {
                    List<string> storedOrder = JsonConvert.DeserializeObject<List<string>>(
                        reader.GetString("kpi_group_order")) ?? new List<string>();
                    _kpiGroupOrder.AddRange(storedOrder
                        .Where(group => !string.IsNullOrWhiteSpace(group))
                        .Select(group => group.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                }
                catch
                {
                    _kpiGroupOrder.Clear();
                }
            }
            _showKpiValues = reader.ItemExists("show_kpi_values") &&
                reader.GetBoolean("show_kpi_values");
            if (reader.ItemExists("fabrication_unit_mode"))
            {
                int storedUnitMode = reader.GetInt32("fabrication_unit_mode");
                _fabricationUnitMode = storedUnitMode switch
                {
                    0 => WasperFabricationUnitMode.Millimetres,
                    1 => WasperFabricationUnitMode.Centimetres,
                    2 => WasperFabricationUnitMode.Metres,
                    _ => WasperFabricationUnitMode.Auto
                };
            }
            _writeWithRun = !reader.ItemExists("write_with_run") ||
                reader.GetBoolean("write_with_run");
            if (reader.ItemExists("cached_kpi_sources"))
            {
                try
                {
                    _cachedSourceSet = JsonConvert.DeserializeObject<WasperKpiSet>(
                        reader.GetString("cached_kpi_sources")) ?? new WasperKpiSet();
                }
                catch
                {
                    _cachedSourceSet = new WasperKpiSet
                    {
                        SourceComponent = "Sm01 KPI source cache"
                    };
                }
            }
            ReadStudyState(reader);
            return base.Read(reader);
        }

        private void WriteExports(string fileName, string filePath, string format)
        {
            try
            {
                string baseName = ResolveBaseName(fileName);
                List<WasperKpi> items = _currentSet?.EnabledItems.ToList() ?? new List<WasperKpi>();
                bool exportStudy = _study?.Iterations?.Count > 0;
                string folder = exportStudy
                    ? ResolveStudyFolder(_study.RunName, filePath)
                    : ResolveOutputFolder(filePath);
                Directory.CreateDirectory(folder);
                List<string> formats = ExpandedFormats(format);
                var written = new List<string>();
                bool wideLayout = string.Equals(
                    NormalizeExportLayout(_editorExportLayout),
                    "Iterations in rows",
                    StringComparison.OrdinalIgnoreCase);

                foreach (string selectedFormat in formats)
                {
                    string extension = selectedFormat == "Excel"
                        ? ".xlsx"
                        : "." + selectedFormat.ToLowerInvariant();
                    string outputPath = Path.Combine(folder, baseName + extension);
                    if (exportStudy && selectedFormat == "CSV")
                    {
                        if (wideLayout)
                            WriteStudyCsv(outputPath, _study);
                        else
                            WriteStudyCsvLong(outputPath, _study);
                    }
                    else if (exportStudy && selectedFormat == "Excel")
                    {
                        if (wideLayout)
                            WriteStudyExcel(outputPath, _study);
                        else
                            WriteStudyExcelLong(outputPath, _study);
                    }
                    else if (exportStudy)
                        WriteStudyJson(outputPath, _study);
                    else if (selectedFormat == "CSV")
                    {
                        if (wideLayout)
                            WriteKpiSnapshotCsvWide(outputPath, items);
                        else
                            WriteCsv(outputPath, items);
                    }
                    else if (selectedFormat == "Excel")
                    {
                        if (wideLayout)
                            WriteKpiSnapshotExcelWide(outputPath, items);
                        else
                            WriteExcel(outputPath, items);
                    }
                    else
                        WriteJson(outputPath, items);
                    written.Add(outputPath);
                }

                _lastWrittenFiles = written;
                _lastWriteInfo = exportStudy
                    ? $"Wrote {_study.Iterations.Count} study iterations to {written.Count} file(s)."
                    : $"Wrote {items.Count} enabled KPI records to {written.Count} file(s).";
            }
            catch (Exception exception)
            {
                _lastWriteInfo = "Write failed: " + exception.Message;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, _lastWriteInfo);
            }
        }

        private void ResetExports(string fileName, string filePath, string format)
        {
            try
            {
                string folder = _study?.Iterations?.Count > 0
                    ? ResolveStudyFolder(_study.RunName, filePath)
                    : ResolveOutputFolder(filePath);
                string baseName = ResolveBaseName(fileName);
                var resetFiles = new List<string>();
                foreach (string selectedFormat in ExpandedFormats(format))
                {
                    string extension = selectedFormat == "Excel"
                        ? ".xlsx"
                        : "." + selectedFormat.ToLowerInvariant();
                    string outputPath = Path.Combine(folder, baseName + extension);
                    if (File.Exists(outputPath))
                        File.Delete(outputPath);
                    resetFiles.Add(outputPath);
                }
                _lastWrittenFiles = resetFiles;
                _lastWriteInfo = $"Reset {resetFiles.Count} KPI export target(s).";
            }
            catch (Exception exception)
            {
                _lastWriteInfo = "Reset failed: " + exception.Message;
                AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, _lastWriteInfo);
            }
        }

        private string ResolveOutputFolder(string filePath)
        {
            if (!string.IsNullOrWhiteSpace(filePath))
                return Path.GetFullPath(filePath.Trim());
            string documentPath = OnPingDocument()?.FilePath;
            if (!string.IsNullOrWhiteSpace(documentPath))
            {
                string parent = Path.GetDirectoryName(documentPath);
                string fileName = Path.GetFileNameWithoutExtension(documentPath);
                if (!string.IsNullOrWhiteSpace(parent) && !string.IsNullOrWhiteSpace(fileName))
                    return Path.Combine(parent, "WASPer_" + fileName);
            }
            return Path.Combine(Path.GetTempPath(), "WASPer_3DP");
        }

        private static string ResolveBaseName(string fileName)
        {
            string name = string.IsNullOrWhiteSpace(fileName) ? "WASPer_KPIs" : fileName.Trim();
            name = Path.GetFileName(name);
            string extension = Path.GetExtension(name);
            if (extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) ||
                extension.Equals(".json", StringComparison.OrdinalIgnoreCase))
            {
                name = Path.GetFileNameWithoutExtension(name);
            }
            foreach (char invalid in Path.GetInvalidFileNameChars())
                name = name.Replace(invalid, '_');
            return string.IsNullOrWhiteSpace(name) ? "WASPer_KPIs" : name;
        }

        private static string NormalizeFormat(string format)
        {
            if (string.Equals(format, "CSV", StringComparison.OrdinalIgnoreCase))
                return "CSV";
            if (string.Equals(format, "Excel", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(format, "XLSX", StringComparison.OrdinalIgnoreCase))
                return "Excel";
            if (string.Equals(format, "JSON", StringComparison.OrdinalIgnoreCase))
                return "JSON";
            return "All";
        }

        private static string NormalizeExportLayout(string layout)
        {
            return string.Equals(layout, "KPIs in rows", StringComparison.OrdinalIgnoreCase)
                ? "KPIs in rows"
                : "Iterations in rows";
        }

        private static List<string> ExpandedFormats(string format)
        {
            string normalized = NormalizeFormat(format);
            return normalized == "All"
                ? new List<string> { "CSV", "Excel", "JSON" }
                : new List<string> { normalized };
        }

        private static void WriteCsv(string outputPath, IList<WasperKpi> items)
        {
            var lines = new List<string>
            {
                "Group,Method,Subset ID,Key,Name,Value,Text Value,Unit,Source,Source Component,Source Nickname,Source Instance ID,Scope,Description"
            };
            lines.AddRange(items.Select(item => string.Join(",", new[]
            {
                EscapeCsv(item.Group),
                EscapeCsv(item.Method),
                EscapeCsv(item.SubsetId),
                EscapeCsv(item.Key),
                EscapeCsv(item.Label),
                EscapeCsv(item.Value?.ToString("G17", CultureInfo.InvariantCulture) ?? string.Empty),
                EscapeCsv(item.TextValue),
                EscapeCsv(item.Unit),
                EscapeCsv(item.Source),
                EscapeCsv(item.SourceComponentId == Guid.Empty ? string.Empty : item.SourceComponentId.ToString()),
                EscapeCsv(item.SourceNickname),
                EscapeCsv(item.SourceInstanceId == Guid.Empty ? string.Empty : item.SourceInstanceId.ToString()),
                EscapeCsv(item.Scope),
                EscapeCsv(item.Description)
            })));
            File.WriteAllLines(outputPath, lines, new UTF8Encoding(true));
        }

        private static void WriteExcel(string outputPath, IList<WasperKpi> items)
        {
            using var workbook = new XLWorkbook();
            IXLWorksheet sheet = workbook.Worksheets.Add("KPIs");
            string[] headers =
            {
                "Group", "Method", "Subset ID", "Key", "Name", "Value", "Text Value",
                "Unit", "Source", "Source Component", "Source Nickname", "Source Instance ID",
                "Scope", "Description"
            };
            for (int column = 0; column < headers.Length; column++)
                sheet.Cell(1, column + 1).Value = headers[column];

            for (int row = 0; row < items.Count; row++)
            {
                WasperKpi item = items[row];
                sheet.Cell(row + 2, 1).Value = item.Group ?? string.Empty;
                sheet.Cell(row + 2, 2).Value = item.Method ?? string.Empty;
                sheet.Cell(row + 2, 3).Value = item.SubsetId ?? string.Empty;
                sheet.Cell(row + 2, 4).Value = item.Key ?? string.Empty;
                sheet.Cell(row + 2, 5).Value = item.Label ?? string.Empty;
                if (item.Value.HasValue)
                    sheet.Cell(row + 2, 6).Value = item.Value.Value;
                sheet.Cell(row + 2, 7).Value = item.TextValue ?? string.Empty;
                sheet.Cell(row + 2, 8).Value = item.Unit ?? string.Empty;
                sheet.Cell(row + 2, 9).Value = item.Source ?? string.Empty;
                sheet.Cell(row + 2, 10).Value = item.SourceComponentId == Guid.Empty
                    ? string.Empty
                    : item.SourceComponentId.ToString();
                sheet.Cell(row + 2, 11).Value = item.SourceNickname ?? string.Empty;
                sheet.Cell(row + 2, 12).Value = item.SourceInstanceId == Guid.Empty
                    ? string.Empty
                    : item.SourceInstanceId.ToString();
                sheet.Cell(row + 2, 13).Value = item.Scope ?? string.Empty;
                sheet.Cell(row + 2, 14).Value = item.Description ?? string.Empty;
            }

            IXLRange tableRange = sheet.Range(1, 1, Math.Max(2, items.Count + 1), headers.Length);
            tableRange.CreateTable("WASPerKPIs");
            sheet.SheetView.FreezeRows(1);
            sheet.Columns(1, 13).AdjustToContents();
            sheet.Column(14).Width = 60;
            sheet.Column(14).Style.Alignment.WrapText = true;
            workbook.SaveAs(outputPath);
        }

        private static void WriteJson(string outputPath, IList<WasperKpi> items)
        {
            var export = new
            {
                schema_version = 2,
                exported_utc = DateTime.UtcNow,
                kpis = items
            };
            File.WriteAllText(
                outputPath,
                JsonConvert.SerializeObject(export, Formatting.Indented),
                new UTF8Encoding(true));
        }

        private static string EscapeCsv(string value)
        {
            value ??= string.Empty;
            bool quote = value.Contains(",") ||
                value.Contains("\"") ||
                value.Contains("\r") ||
                value.Contains("\n");
            string escaped = value.Replace("\"", "\"\"");
            return quote ? "\"" + escaped + "\"" : escaped;
        }

        private static Bitmap CreateIcon()
        {
            var bitmap = new Bitmap(24, 24);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var panelBrush = new SolidBrush(Color.FromArgb(242, 166, 44));
            using var darkPen = new Pen(Color.FromArgb(55, 55, 55), 1.6f);
            using var checkPen = new Pen(Color.White, 1.8f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            using GraphicsPath panel = RoundedRectangle(new RectangleF(2, 3, 20, 18), 3);

            graphics.FillPath(panelBrush, panel);
            graphics.DrawPath(darkPen, panel);
            for (int row = 0; row < 3; row++)
            {
                float y = 7 + (row * 5);
                graphics.DrawRectangle(darkPen, 5, y - 2, 3, 3);
                graphics.DrawLine(darkPen, 11, y - 0.5f, 19, y - 0.5f);
            }
            graphics.DrawLines(checkPen, new[]
            {
                new PointF(5.4f, 6.4f),
                new PointF(6.6f, 7.6f),
                new PointF(8.5f, 4.9f)
            });
            return bitmap;
        }

        private static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
        {
            float diameter = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

    }
}
