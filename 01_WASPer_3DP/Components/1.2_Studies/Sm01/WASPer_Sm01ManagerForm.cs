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
using System.Windows.Forms;

using ClosedXML.Excel;
using Grasshopper;
using Grasshopper.GUI;
using Grasshopper.GUI.Canvas;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Attributes;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Types;

namespace WASPer_3DP.Components._1_2_Studies
{
    public sealed partial class wsp_Sm01_WASPer_Study_Manager
    {
        private sealed partial class KpiManagerForm : Form, ISm01ManagerView
        {
            // M5's "Viewer status" row (Process Viewer tab) otherwise only refreshes on a
            // Grasshopper solve -- UpdateProcessViewerWindow (and so TryRefreshLiveViewerStatus)
            // only ever runs from SolveInstance or when this form is first shown. That leaves
            // the label stuck at whatever it read on the last solve even while the manager
            // window sits open and a browser connects/disconnects with nothing in GH changing.
            // This timer (started/stopped alongside the form itself, below and in
            // OnFormClosed) closes that gap by prompting a poll on a plain wall-clock cadence,
            // independent of whether anything in the document ever recomputes.
            private readonly Timer _liveStatusPollTimer = new Timer { Interval = 1500 };
            public event Action ViewClosed;
            public event Action LiveStatusPollTick;

            private readonly FlowLayoutPanel _groups = new BufferedFlowLayoutPanel
            {
                AutoScroll = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Padding = new Padding(10),
                WrapContents = false
            };
            private readonly Label _status = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            private readonly Button _all = new Button { Text = "Select all", AutoSize = true };
            private readonly Button _none = new Button { Text = "Select none", AutoSize = true };
            private readonly Button _apply = new Button { Text = "Apply", AutoSize = true };
            private readonly CheckBox _showValues = new CheckBox
            {
                Appearance = Appearance.Button,
                AutoSize = true,
                Text = "Show values",
                TextAlign = ContentAlignment.MiddleCenter
            };
            private readonly Button _resetGroupOrder = new Button
            {
                Text = "Reset order",
                AutoSize = true
            };
            private readonly ComboBox _fabricationUnits = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 155
            };
            private readonly TextBox _fileName = new TextBox { Width = 170 };
            private readonly TextBox _filePath = new TextBox { Width = 300 };
            private readonly ComboBox _format = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 80
            };
            private readonly ComboBox _exportLayout = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 165
            };
            private readonly Button _browse = new Button { Text = "Browse...", AutoSize = true };
            private readonly Button _resetFiles = new Button { Text = "Reset files", AutoSize = true };
            private readonly CheckBox _writeFiles = new CheckBox
            {
                Appearance = Appearance.Button,
                AutoSize = true,
                Checked = true,
                Text = "Write with run",
                TextAlign = ContentAlignment.MiddleCenter,
                UseVisualStyleBackColor = false
            };
            private readonly CheckBox _snapshotEnabled = new CheckBox
            {
                AutoSize = true,
                Checked = true,
                Text = "Save viewport snapshots with each iteration"
            };
            private readonly Button _linkVisualization = new Button
            {
                AutoSize = true,
                Text = "Link vis component"
            };
            private readonly Button _unlinkVisualization = new Button
            {
                AutoSize = true,
                Text = "Unlink"
            };
            private readonly Label _linkedVisualizationStatus = new Label
            {
                AutoEllipsis = true,
                AutoSize = false,
                Height = 22,
                Width = 245,
                Text = "No visualization component linked.",
                TextAlign = ContentAlignment.MiddleLeft
            };
            private readonly ComboBox _snapshotViewport = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 190
            };
            private readonly NumericUpDown _snapshotWidth = new NumericUpDown
            {
                Minimum = 64,
                Maximum = 16384,
                Value = 1920,
                Width = 90
            };
            private readonly NumericUpDown _snapshotHeight = new NumericUpDown
            {
                Minimum = 64,
                Maximum = 16384,
                Value = 1080,
                Width = 90
            };
            private readonly NumericUpDown _snapshotDpi = new NumericUpDown
            {
                Minimum = 1,
                Maximum = 1200,
                Value = 72,
                Width = 75
            };
            private readonly NumericUpDown _snapshotWait = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 10000,
                Increment = 50,
                Value = 500,
                Width = 85
            };
            private readonly Button _refreshViewports = new Button
            {
                AutoSize = true,
                Text = "Refresh views"
            };
            private readonly Button _previewSnapshot = new Button
            {
                AutoSize = true,
                Text = "Refresh preview"
            };
            private readonly PictureBox _snapshotPreview = new PictureBox
            {
                BackColor = Color.FromArgb(42, 42, 42),
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom
            };
            private readonly Label _snapshotStatus = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Bottom,
                Height = 24,
                Text = "Select a viewport and refresh the preview.",
                TextAlign = ContentAlignment.MiddleLeft
            };
            private readonly DataGridView _parameterGrid = new BufferedDataGridView
            {
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                Dock = DockStyle.Fill,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            private readonly DataGridView _iterationGrid = new BufferedDataGridView
            {
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                Dock = DockStyle.Fill,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
            private readonly Button _linkSelected = new Button { Text = "Link selected sliders", AutoSize = true };
            private readonly Button _unlinkSelected = new Button { Text = "Unlink selected", AutoSize = true };
            private readonly Button _restoreDefaults = new Button
            {
                Text = "Restore defaults",
                AutoSize = true
            };
            private readonly Label _estimatedIterations = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Bottom,
                Height = 20,
                TextAlign = ContentAlignment.MiddleLeft
            };
            private readonly Button _runStudy = new Button { Text = "Run study", AutoSize = true };
            private readonly Button _resumeStudy = new Button { Text = "Resume", AutoSize = true };
            private readonly Button _stopStudy = new Button { Text = "Stop", AutoSize = true };
            private readonly Button _captureIteration = new Button { Text = "Capture current", AutoSize = true };
            private readonly Button _clearIterations = new Button { Text = "Clear iterations", AutoSize = true };
            private readonly Button _saveStudy = new Button { Text = "Save Study", AutoSize = true };
            private readonly ComboBox _studyLibrary = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 440
            };
            private readonly ComboBox _dashboardStudyLibrary = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 420
            };
            private readonly Button _refreshStudies = new Button { Text = "Refresh", AutoSize = true };
            private readonly Button _browseStudy = new Button { Text = "Browse...", AutoSize = true };
            private readonly Button _forgetStudy = new Button { Text = "Forget", AutoSize = true };
            private readonly Button _studyCompatibility = new Button { Text = "Compatibility...", AutoSize = true };
            private readonly Button _loadStudy = new Button { Text = "Load as active", AutoSize = true };
            private readonly Button _resumeSavedStudy = new Button { Text = "Resume selected", AutoSize = true };
            private readonly Label _studyLibraryStatus = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Bottom,
                Height = 23,
                TextAlign = ContentAlignment.MiddleLeft
            };
            private readonly ProgressBar _studyProgress = new ProgressBar { Width = 220, Height = 20 };
            private readonly Label _studyStatus = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            private readonly RichTextBox _studyLog = new RichTextBox
            {
                BackColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                DetectUrls = false,
                Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 9f),
                ReadOnly = true,
                WordWrap = true
            };
            /// <summary>
            /// Sample Name tab: left panel is a togglable-category filter (mirrors the Dashboard's
            /// KPI group filter) plus the list of tokens it currently admits; the right panel is the
            /// ordered, composed template. Both are plain ListBoxes so items can be drag-dropped
            /// between them (add) and within the right one (reorder) using the same DoDragDrop
            /// mechanism already used for KPI group reordering (see WASPer_Sm01KpiTab.cs).
            /// </summary>
            private readonly CheckedListBox _sampleNameGroupFilter = new CheckedListBox
            {
                CheckOnClick = true,
                Dock = DockStyle.Top,
                Height = 92,
                HorizontalScrollbar = true
            };
            private readonly ListBox _sampleNameAvailable = new ListBox
            {
                AllowDrop = true,
                Dock = DockStyle.Fill,
                IntegralHeight = false
            };
            private readonly ListBox _sampleNameComposed = new ListBox
            {
                AllowDrop = true,
                Dock = DockStyle.Fill,
                IntegralHeight = false
            };
            private readonly Button _sampleNameRemove = new Button
            {
                Text = "Remove",
                AutoSize = true
            };
            private readonly TextBox _sampleNameTextInput = new TextBox { Width = 150 };
            private readonly Button _sampleNameInsertText = new Button
            {
                Text = "Insert text",
                AutoSize = true
            };
            private readonly Button _sampleNameRestoreDefault = new Button
            {
                Text = "Restore default",
                AutoSize = true
            };
            private readonly Label _sampleNamePreview = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Bottom,
                Height = 25,
                TextAlign = ContentAlignment.MiddleLeft
            };
            private readonly Label _exportStatus = new Label
            {
                AutoEllipsis = true,
                AutoSize = false,
                Height = 22,
                Width = 920,
                TextAlign = ContentAlignment.MiddleLeft
            };
            private readonly TextBox _reportTitle = new TextBox { Width = 300 };
            private readonly TextBox _reportSubtitle = new TextBox { Width = 300 };
            private readonly ComboBox _reportPageSize = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 95
            };
            private readonly ComboBox _reportOrientation = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 105
            };
            private readonly CheckBox _reportSnapshot = new CheckBox
            {
                Text = "Include active Rhino viewport snapshot",
                AutoSize = true
            };
            private readonly CheckBox _reportIterations = new CheckBox
            {
                Text = "Include iteration preview table",
                AutoSize = true
            };
            private readonly Button _generateReport = new Button
            {
                Text = "Generate PDF",
                AutoSize = true
            };
            private readonly Label _reportStatus = new Label
            {
                AutoEllipsis = true,
                AutoSize = false,
                Height = 42,
                Width = 860,
                TextAlign = ContentAlignment.MiddleLeft
            };
            private readonly ComboBox _gcodeBranch = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 150
            };
            private readonly Button _saveGcode = new Button
            {
                AutoSize = true,
                Enabled = false,
                Margin = new Padding(12, 3, 3, 3),
                Text = "Save G-code..."
            };
            private readonly RichTextBox _gcodeViewer = new RichTextBox
            {
                BackColor = Color.White,
                DetectUrls = false,
                Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 9f),
                ReadOnly = true,
                WordWrap = false
            };
            private readonly Label _gcodeStatus = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft
            };
            private readonly Label _dashboardStatus = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Top,
                Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                Height = 34,
                Padding = new Padding(12, 8, 12, 4),
                Text = "No captured iterations yet."
            };
            private readonly ComboBox _dashboardHistoryKpi = DashboardComboBox();
            private readonly ComboBox _dashboardScatterX = DashboardComboBox();
            private readonly ComboBox _dashboardScatterY = DashboardComboBox();
            private readonly NumericUpDown _dashboardTextSize = new NumericUpDown
            {
                Increment = 10,
                Margin = new Padding(2),
                Maximum = 200,
                Minimum = 70,
                Value = 100,
                Width = 62
            };
            private readonly ComboBox _dashboardHistogramVariable = DashboardComboBox();
            private readonly NumericUpDown _dashboardHistogramBins = new NumericUpDown
            {
                Margin = new Padding(2),
                Maximum = 60,
                Minimum = 2,
                Value = 12,
                Width = 56
            };
            private readonly ComboBox _dashboardScatterStyle = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(2),
                Width = 108
            };
            private readonly ComboBox _dashboardScatterColor = DashboardComboBox();
            private readonly CheckBox _dashboardScatterNames = new CheckBox
            {
                Appearance = Appearance.Button,
                AutoSize = true,
                Margin = new Padding(12, 2, 2, 2),
                Text = "Show names",
                TextAlign = ContentAlignment.MiddleCenter
            };
            private readonly CheckBox _dashboardScatterValues = new CheckBox
            {
                Appearance = Appearance.Button,
                AutoSize = true,
                Margin = new Padding(4, 2, 2, 2),
                Text = "Show data",
                TextAlign = ContentAlignment.MiddleCenter
            };
            private readonly Button _dashboardShowInGrasshopper = new Button
            {
                AutoSize = true,
                Enabled = false,
                Margin = new Padding(12, 2, 2, 2),
                Text = "Show in Grasshopper"
            };
            private readonly Button _dashboardReset = new Button
            {
                AutoSize = true,
                Margin = new Padding(10, 2, 2, 2),
                Text = "Reset Dashboard"
            };
            private readonly NumericUpDown _dashboardHistogramBandwidth = new NumericUpDown
            {
                Increment = 10,
                Margin = new Padding(2),
                Maximum = 300,
                Minimum = 10,
                Value = 100,
                Visible = false,
                Width = 62
            };
            private readonly Label _dashboardHistogramParameterLabel = new Label
            {
                AutoSize = true,
                Margin = new Padding(12, 7, 5, 2),
                Text = "Bins"
            };
            private readonly ComboBox _dashboardHistogramMode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Margin = new Padding(2),
                Width = 90
            };
            private readonly Button _dashboardGroupFilter = new Button
            {
                AutoSize = false,
                Margin = new Padding(2),
                Text = "KPI groups ▾",
                TextAlign = ContentAlignment.MiddleLeft,
                Width = 190
            };
            private readonly CheckedListBox _dashboardGroupList = new CheckedListBox
            {
                BorderStyle = BorderStyle.None,
                CheckOnClick = true,
                Height = 190,
                IntegralHeight = false,
                Width = 250
            };
            private readonly PictureBox _dashboardHistoryChart = DashboardPictureBox();
            private readonly PictureBox _dashboardScatterChart = DashboardPictureBox();
            private readonly PictureBox _dashboardHeatmapChart = DashboardPictureBox();
            private readonly PictureBox _dashboardParallelChart = DashboardPictureBox();
            private readonly PictureBox _dashboardHistogramChart = DashboardPictureBox();
            private readonly Label _dashboardSelectionDetails = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Bottom,
                Height = 42,
                Padding = new Padding(12, 5, 12, 5),
                Text = "Click a chart point to inspect and link an individual across Dashboard views."
            };
            private readonly WasperScatterChartRenderer _dashboardRenderer =
                new WasperScatterChartRenderer();
            private readonly WasperCorrelationHeatmapRenderer _dashboardHeatmapRenderer =
                new WasperCorrelationHeatmapRenderer { MaxVariables = DashboardMultivariateLimit };
            private readonly WasperParallelCoordinatesRenderer _dashboardParallelRenderer =
                new WasperParallelCoordinatesRenderer { MaxVariables = DashboardMultivariateLimit };
            private readonly WasperHistogramRenderer _dashboardHistogramRenderer =
                new WasperHistogramRenderer();
            private readonly WasperChartSelection _dashboardSelection =
                new WasperChartSelection();
            private WasperChartRenderResult _dashboardHistoryResult;
            private WasperChartRenderResult _dashboardScatterResult;
            private WasperChartRenderResult _dashboardHeatmapResult;
            private WasperChartRenderResult _dashboardParallelResult;
            private WasperChartRenderResult _dashboardHistogramResult;
            private ToolStripDropDown _dashboardGroupPopup;
            private TableLayoutPanel _dashboardCharts;
            private bool _updatingDashboardRows;
            private WasperDashboardSettings _dashboardSettings = new WasperDashboardSettings();
            private ContextMenuStrip _dashboardPointMenu;
            private int? _draggingLabelId;
            private Point _labelDragOrigin;
            private PointF _labelDragStart;
            private bool _labelDragConsumedClick;
            private int? _splitterRowIndex;
            private Point _splitterOrigin;
            private float _splitterStart;
            private bool _draggingColumnSplitter;
            private bool _dashboardLayoutPinned;
            private bool _draggingSnapshotSplitter;
            private bool _snapshotWidthPinned;
            private int _snapshotSplitterStartWidth;
            private Point _snapshotSplitterOrigin;
            private readonly PictureBox _dashboardSnapshotImage = new PictureBox
            {
                BackColor = Color.FromArgb(42, 42, 42),
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill,
                SizeMode = PictureBoxSizeMode.Zoom
            };
            // Two single-line labels rather than one two-line label: AutoEllipsis forces WinForms
            // to render a Label as a single line, so an embedded newline collapses and everything
            // after it is swallowed by the ellipsis.
            private readonly Label _dashboardSnapshotCaption = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Bottom,
                Height = 18,
                Padding = new Padding(2, 0, 2, 0),
                Text = "Select a sample to preview its snapshot.",
                TextAlign = ContentAlignment.MiddleLeft
            };
            private readonly Label _dashboardSnapshotStatus = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Bottom,
                ForeColor = Color.FromArgb(105, 105, 105),
                Height = 18,
                Padding = new Padding(2, 0, 2, 2),
                TextAlign = ContentAlignment.MiddleLeft
            };
            private string _dashboardSnapshotPath = string.Empty;
            private string _dashboardSnapshotFolder = string.Empty;
            private readonly Label _dashboardSnapshotEmpty = new Label
            {
                BackColor = Color.Transparent,
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(190, 190, 190),
                Text = "Snapshot not available",
                TextAlign = ContentAlignment.MiddleCenter,
                Visible = false
            };
            private Panel _dashboardSnapshotPanel;
            private readonly Button _dashboardResetLabelPositions = new Button
            {
                AutoSize = true,
                Enabled = false,
                Margin = new Padding(4, 2, 2, 2),
                Text = "Reset labels"
            };

            /// <summary>Sentinel entry meaning "do not colour markers by any variable".</summary>
            private static readonly DashboardVariableOption DashboardNoColorOption =
                new DashboardVariableOption(string.Empty, "(no colour)", string.Empty);
            private readonly HashSet<string> _dashboardHiddenGroups =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private List<WasperStudyIteration> _dashboardIterations =
                new List<WasperStudyIteration>();
            private List<WasperStudyParameter> _dashboardParameters =
                new List<WasperStudyParameter>();
            private bool _updatingDashboard;
            private bool _updatingDashboardGroups;
            // Set by RenderDashboardCharts when the Dashboard tab isn't the
            // visible one -- the actual GDI+ render is skipped and deferred
            // until the user switches to it (see the TabControl.
            // SelectedIndexChanged wiring near its construction), instead of
            // paying that cost on every Grasshopper solve regardless of
            // which tab is showing.
            private bool _dashboardDirty;
            private TabControl _tabs;
            private TabPage _dashboardTab;
            private List<List<string>> _displayedGcodeBranches = new List<List<string>>();
            private readonly Dictionary<CheckedListBox, List<WasperKpi>> _items =
                new Dictionary<CheckedListBox, List<WasperKpi>>();
            private readonly Dictionary<string, CheckBox> _groupToggles =
                new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<Guid, List<SourceActionButton>> _sourceToggles =
                new Dictionary<Guid, List<SourceActionButton>>();
            private readonly Dictionary<Guid, string> _sourceToggleNames =
                new Dictionary<Guid, string>();
            // Per-group KPI box width, in pixels, set by dragging that group's resize handle
            // (see CreateKpiWidthSplitter in WASPer_Sm01KpiTab.cs). Session-only, like the
            // Dashboard's HiddenGroups filter -- groups not yet dragged fall back to
            // DefaultKpiGroupWidth. Keyed by DisplayGroup, same key UpdateKpis already groups by.
            private readonly Dictionary<string, int> _kpiGroupWidths =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private GroupBox _draggingKpiWidthBox;
            private Point _kpiWidthSplitterOrigin;
            private int _kpiWidthSplitterStartWidth;
            private readonly ToolTip _toolTip = new ToolTip
            {
                AutoPopDelay = 12000,
                InitialDelay = 350,
                ReshowDelay = 100
            };
            private Rectangle _lastNormalBounds;
            private bool _updatingExportControls;
            private bool _filePathShowingDefault;
            private bool _updatingReportControls;
            private bool _updatingSampleNameComposer;
            private List<SampleNamePropertyOption> _sampleNameLastOptions = new List<SampleNamePropertyOption>();
            private List<string> _sampleNameLastTokens = new List<string>();
            private Dictionary<string, SampleNamePropertyOption> _sampleNameOptionsByKey =
                new Dictionary<string, SampleNamePropertyOption>(StringComparer.OrdinalIgnoreCase);
            // Index into the composed list currently loaded into _sampleNameTextInput for editing
            // ("Insert text" becomes "Update text"), or -1 when the field is for a brand new
            // segment. See BeginEditSampleNameText / SampleNameInsertOrUpdateText.
            private int _sampleNameEditingTextIndex = -1;
            // Which-categories-are-shown state for the Sample Name tab's available-tokens filter.
            // Deliberately session-only (not persisted with the document) - unlike the Dashboard's
            // HiddenGroups this only affects what is easy to find while composing a template, not
            // any stored study data, so it is not worth a schema/GH_IO round trip.
            private readonly HashSet<string> _sampleNameHiddenGroups =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private bool _updatingKpiValueDisplay;
            private bool _updatingKpiControls;
            private bool _updatingFabricationUnits;
            private bool _updatingSnapshotControls;
            private bool _updatingStudyLibrary;
            private Guid _linkedVisualizationId;
            private string _linkedVisualizationName = string.Empty;
            private string _lastLoggedStudyStatus = string.Empty;
            private string _kpiStructureKey = string.Empty;
            private GroupBox _draggedGroup;

            public event Action<IEnumerable<string>> SelectionApplied;
            public event Action<string, string, string> ExportSettingsChanged;
            public event Action<string> ExportLayoutChanged;
            public event Action<string, string, string> ResetRequested;
            public event Action LinkSelectedSlidersRequested;
            public event Action<IEnumerable<Guid>> UnlinkSlidersRequested;
            public event Action<IEnumerable<Guid>> RestoreParameterDefaultsRequested;
            public event Action<IEnumerable<WasperStudyParameter>> RunStudyRequested;
            public event Action<IEnumerable<WasperStudyParameter>> ResumeStudyRequested;
            public event Action RefreshStudyLibraryRequested;
            public event Action BrowseStudyRequested;
            public event Action<WasperStudyCatalogEntry> ForgetPinnedStudyRequested;
            public event Action<WasperStudyCatalogEntry> StudyLibrarySelectionChanged;
            public event Action<WasperStudyCatalogEntry> LoadSavedStudyRequested;
            public event Action<WasperStudyCatalogEntry> ResumeSavedStudyRequested;
            public event Action StopStudyRequested;
            public event Action CaptureIterationRequested;
            public event Action ClearIterationsRequested;
            public event Action SaveStudyRequested;
            public event Action<WasperReportSettings> GenerateReportRequested;
            public event Action<WasperReportSettings> ReportSettingsChanged;
            public event Action<IEnumerable<string>> SampleNameTemplateChanged;
            public event Action<IEnumerable<string>> GroupOrderChanged;
            public event Action GroupOrderResetRequested;
            public event Action<string, bool> GroupEnabledChanged;
            public event Action<Guid, bool> SourceEnabledChanged;
            public event Action<bool> ShowValuesChanged;
            public event Action<bool> WriteWithRunChanged;
            public event Action<WasperFabricationUnitMode> FabricationUnitModeChanged;
            public event Action<WasperSnapshotSettings> SnapshotSettingsChanged;
            public event Action<WasperDashboardSettings> DashboardSettingsChanged;

            /// <summary>Raised with the iteration index the user wants restored onto the sliders.</summary>
            public event Action<int> ShowIterationRequested;
            public event Action LinkVisualizationRequested;
            public event Action UnlinkVisualizationRequested;

            public Rectangle LastNormalBounds => WindowState == FormWindowState.Normal
                ? Bounds
                : _lastNormalBounds;
            public bool IsClosed => IsDisposed;
            public int CurrentDpi
            {
                get
                {
                    if (!IsHandleCreated)
                        return CurrentSystemDpi();
                    return GetHandleDeviceDpi();
                }
            }

            private int GetHandleDeviceDpi()
            {
                try
                {
                    // Resolve by name to avoid a direct get_DeviceDpi reference on Rhino for Mac.
                    PropertyInfo property = GetType().GetProperty(
                        "DeviceDpi",
                        BindingFlags.Instance | BindingFlags.Public);
                    if (property?.GetValue(this) is int dpi && dpi > 0)
                        return Math.Max(96, dpi);
                }
                catch
                {
                    // Fall through to the host-compatible graphics DPI query.
                }
                return CurrentSystemDpi();
            }

            /// <summary>
            /// Wraps a tab's content in an AutoScroll host so the tab gets a scrollbar instead of
            /// clipping/squishing its fixed-height elements when the manager window is shrunk below
            /// the content's natural size. The content is intentionally not Dock=Fill: WinForms can
            /// resize fill-docked children to the viewport before AutoScroll computes its range,
            /// which makes the scrollbar disappear on tabs with fixed minimum layouts.
            /// minimumHeight/Width are logical (96 DPI) pixels, scaled here the same way every other
            /// fixed size in this form is.
            /// </summary>
            private Control WrapWithVerticalScroll(Control content, int minimumHeight, int minimumWidth = 0)
            {
                int scaledMinimumWidth = Math.Max(content.MinimumSize.Width, ScaleUi(minimumWidth));
                int scaledMinimumHeight = Math.Max(content.MinimumSize.Height, ScaleUi(minimumHeight));
                content.Dock = DockStyle.None;
                content.Location = Point.Empty;
                content.MinimumSize = new Size(scaledMinimumWidth, scaledMinimumHeight);

                var scroller = new Panel { AutoScroll = true, Dock = DockStyle.Fill };
                void ResizeScrollableContent()
                {
                    int width = Math.Max(scaledMinimumWidth, scroller.ClientSize.Width);
                    int height = Math.Max(scaledMinimumHeight, scroller.ClientSize.Height);
                    Size targetSize = new Size(width, height);
                    if (content.Size != targetSize)
                        content.Size = targetSize;
                }

                scroller.Controls.Add(content);
                ResizeScrollableContent();
                scroller.Resize += (sender, args) => ResizeScrollableContent();
                return scroller;
            }

            /// <summary>
            /// Scroll host for the Process Viewer, whose QR section has a dynamic preferred
            /// height. A Fill-docked child can be treated by WinForms as viewport-sized and
            /// therefore produce no scroll range. Top docking plus an explicit AutoScrollMinSize
            /// gives the host a real document extent and keeps mouse-wheel/scrollbar navigation
            /// working as QR rows are added.
            /// </summary>
            private Control WrapProcessViewerWithVerticalScroll(
                Control content,
                int minimumHeight)
            {
                int scaledMinimumHeight = ScaleUi(minimumHeight);
                content.AutoSize = true;
                content.Dock = DockStyle.Top;
                content.MinimumSize = new Size(0, scaledMinimumHeight);

                var scroller = new Panel
                {
                    AutoScroll = true,
                    AutoScrollMinSize = new Size(0, scaledMinimumHeight),
                    Dock = DockStyle.Fill
                };
                scroller.Controls.Add(content);
                return scroller;
            }

            public KpiManagerForm(
                WasperKpiSet set,
                IEnumerable<string> disabledKeys,
                IEnumerable<string> disabledGroups,
                IReadOnlyDictionary<Guid, bool> sourceStates,
                bool showValues,
                bool writeWithRun,
                WasperFabricationUnitMode fabricationUnitMode,
                int? sourceFabricationUnitCode,
                int storedDpi,
                Rectangle bounds)
            {
                Text = "WASPer Study Manager";
                AutoScaleMode = AutoScaleMode.Dpi;
                AutoScaleDimensions = new SizeF(96f, 96f);
                MinimumSize = new Size(560, 320);
                StartPosition = FormStartPosition.Manual;
                Bounds = NormalizeStoredBounds(bounds, storedDpi);
                _lastNormalBounds = Bounds;
                FormBorderStyle = FormBorderStyle.Sizable;
                MaximizeBox = true;
                MinimizeBox = true;
                ShowInTaskbar = true;
                _groups.AllowDrop = true;
                ConfigureFabricationUnitOptions(
                    fabricationUnitMode,
                    sourceFabricationUnitCode);
                SetWriteWithRun(writeWithRun);

                var footer = new TableLayoutPanel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    ColumnCount = 1,
                    Dock = DockStyle.Bottom,
                    Padding = new Padding(10, 3, 10, 5),
                    RowCount = 2
                };
                footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                var footerActions = new FlowLayoutPanel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.LeftToRight,
                    WrapContents = true
                };
                footerActions.Controls.Add(new Label
                {
                    AutoSize = true,
                    Margin = new Padding(3, 7, 4, 0),
                    Text = "Fabrication units"
                });
                footerActions.Controls.Add(_fabricationUnits);
                footerActions.Controls.Add(_showValues);
                footerActions.Controls.Add(_resetGroupOrder);
                footerActions.Controls.Add(_all);
                footerActions.Controls.Add(_none);
                footerActions.Controls.Add(_apply);
                footer.Controls.Add(_status, 0, 0);
                footer.Controls.Add(footerActions, 0, 1);

                TabControl tabs = new TabControl { Dock = DockStyle.Fill };
                var studyTab = new TabPage("Study");
                var gcodeTab = new TabPage("G-code");
                var kpiTab = new TabPage("KPIs");
                var exportTab = new TabPage("Export");
                var sampleNameTab = new TabPage("Sample Name");
                var dashboardTab = new TabPage("Dashboard");
                var processViewerTab = new TabPage("Process Viewer (XR)");
                var reportTab = new TabPage("Report");

                Control studyPanel = CreateStudyPanel();
                studyTab.Controls.Add(WrapWithVerticalScroll(studyPanel, 520));
                gcodeTab.Controls.Add(WrapWithVerticalScroll(CreateGcodePanel(), 320));
                var kpiPanel = new Panel { Dock = DockStyle.Fill };
                kpiPanel.Controls.Add(_groups);
                kpiPanel.Controls.Add(footer);
                kpiTab.Controls.Add(WrapWithVerticalScroll(kpiPanel, 420));
                exportTab.Controls.Add(WrapWithVerticalScroll(CreateExportPanel(), 560));
                sampleNameTab.Controls.Add(WrapWithVerticalScroll(CreateSampleNamePanel(), 340));
                dashboardTab.Controls.Add(
                    WrapWithVerticalScroll(CreateDashboardPanel(), 620, 680));
                processViewerTab.Controls.Add(
                    WrapProcessViewerWithVerticalScroll(CreateProcessViewerPanel(), 780));
                reportTab.Controls.Add(WrapWithVerticalScroll(CreateReportPanel(), 420));
                tabs.TabPages.Add(kpiTab);
                tabs.TabPages.Add(gcodeTab);
                tabs.TabPages.Add(exportTab);
                tabs.TabPages.Add(sampleNameTab);
                tabs.TabPages.Add(studyTab);
                tabs.TabPages.Add(dashboardTab);
                tabs.TabPages.Add(processViewerTab);
                tabs.TabPages.Add(reportTab);
                Controls.Add(tabs);
                _tabs = tabs;
                _dashboardTab = dashboardTab;
                // Catches up the one Dashboard render that was skipped while the
                // tab wasn't visible (see RenderDashboardCharts's early-out) --
                // without this, switching to the Dashboard tab after a solve
                // happened elsewhere would show stale charts until the next
                // solve.
                tabs.SelectedIndexChanged += (sender, args) =>
                {
                    if (_dashboardDirty && tabs.SelectedTab == dashboardTab)
                        RenderDashboardCharts();
                };
                _all.Click += (sender, args) => SetAllChecked(true);
                _none.Click += (sender, args) => SetAllChecked(false);
                _apply.Click += ApplySelection;
                _showValues.CheckedChanged += ShowValuesCheckedChanged;
                _fabricationUnits.SelectedIndexChanged += FabricationUnitsChanged;
                _resetGroupOrder.Click += (sender, args) => GroupOrderResetRequested?.Invoke();
                _browse.Click += BrowseFolder;
                _resetFiles.Click += (sender, args) =>
                    ResetRequested?.Invoke(_fileName.Text, ExportFilePathValue(), SelectedFormat());
                _writeFiles.CheckedChanged += (sender, args) =>
                {
                    StyleWriteWithRun();
                    WriteWithRunChanged?.Invoke(_writeFiles.Checked);
                };
                _fileName.Leave += ExportSettingChanged;
                _filePath.Leave += ExportSettingChanged;
                _filePath.TextChanged += (sender, args) =>
                {
                    if (!_updatingExportControls)
                        _filePathShowingDefault = false;
                };
                _format.SelectedIndexChanged += ExportSettingChanged;
                _exportLayout.SelectedIndexChanged += (sender, args) =>
                {
                    if (!_updatingExportControls)
                        ExportLayoutChanged?.Invoke(SelectedExportLayout());
                };
                _snapshotEnabled.CheckedChanged += SnapshotSettingChanged;
                _snapshotViewport.SelectedIndexChanged += SnapshotViewportChanged;
                _snapshotWidth.ValueChanged += SnapshotSettingChanged;
                _snapshotHeight.ValueChanged += SnapshotSettingChanged;
                _snapshotDpi.ValueChanged += SnapshotSettingChanged;
                _snapshotWait.ValueChanged += SnapshotSettingChanged;
                _refreshViewports.Click += (sender, args) => RefreshViewportChoices();
                _previewSnapshot.Click += (sender, args) => RefreshSnapshotPreview();
                _linkVisualization.Click += (sender, args) =>
                    LinkVisualizationRequested?.Invoke();
                _unlinkVisualization.Click += (sender, args) =>
                    UnlinkVisualizationRequested?.Invoke();
                _linkSelected.Click += (sender, args) => LinkSelectedSlidersRequested?.Invoke();
                _unlinkSelected.Click += (sender, args) =>
                    UnlinkSlidersRequested?.Invoke(SelectedSliderIds());
                _restoreDefaults.Click += (sender, args) =>
                    RestoreParameterDefaultsRequested?.Invoke(SelectedSliderIds());
                _parameterGrid.CellValueChanged += (sender, args) => UpdateEstimatedIterationCount();
                _parameterGrid.CurrentCellDirtyStateChanged += (sender, args) =>
                {
                    // The "Use" checkbox column commits on click, not on leaving the cell/row -
                    // without forcing the commit here, CellValueChanged (and so the iteration
                    // estimate) would not fire until the user clicked a different row.
                    if (_parameterGrid.IsCurrentCellDirty)
                        _parameterGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                };
                _runStudy.Click += RunStudyClicked;
                _resumeStudy.Click += (sender, args) =>
                    ResumeStudyRequested?.Invoke(ReadParameterConfiguration());
                _stopStudy.Click += (sender, args) => StopStudyRequested?.Invoke();
                _captureIteration.Click += (sender, args) => CaptureIterationRequested?.Invoke();
                _clearIterations.Click += (sender, args) => ClearIterationsRequested?.Invoke();
                _saveStudy.Click += (sender, args) => SaveStudyRequested?.Invoke();
                _refreshStudies.Click += (sender, args) => RefreshStudyLibraryRequested?.Invoke();
                _browseStudy.Click += (sender, args) => BrowseStudyRequested?.Invoke();
                _forgetStudy.Click += (sender, args) =>
                    ForgetPinnedStudyRequested?.Invoke(SelectedStudyEntry());
                _studyCompatibility.Click += ShowSelectedStudyCompatibility;
                _studyLibrary.SelectedIndexChanged += (sender, args) =>
                {
                    if (!_updatingStudyLibrary)
                        StudyLibrarySelectionChangedFrom(_studyLibrary);
                };
                _dashboardStudyLibrary.SelectedIndexChanged += (sender, args) =>
                {
                    if (!_updatingStudyLibrary)
                        StudyLibrarySelectionChangedFrom(_dashboardStudyLibrary);
                };
                _loadStudy.Click += (sender, args) =>
                    LoadSavedStudyRequested?.Invoke(SelectedStudyEntry());
                _resumeSavedStudy.Click += (sender, args) =>
                    ResumeSavedStudyRequested?.Invoke(SelectedStudyEntry());
                _dashboardHistoryKpi.SelectedIndexChanged += DashboardSelectionChanged;
                _dashboardScatterX.SelectedIndexChanged += DashboardSelectionChanged;
                _dashboardScatterY.SelectedIndexChanged += DashboardSelectionChanged;
                _dashboardTextSize.ValueChanged += DashboardSelectionChanged;
                _dashboardScatterStyle.SelectedIndexChanged += DashboardSelectionChanged;
                _dashboardScatterColor.SelectedIndexChanged += DashboardSelectionChanged;
                _dashboardScatterNames.CheckedChanged += DashboardSelectionChanged;
                _dashboardScatterValues.CheckedChanged += DashboardSelectionChanged;
                _dashboardShowInGrasshopper.Click += DashboardShowInGrasshopperClicked;
                _dashboardResetLabelPositions.Click += DashboardResetLabelPositionsClicked;
                _dashboardScatterChart.MouseDown += DashboardScatterMouseDown;
                _dashboardScatterChart.MouseMove += DashboardScatterMouseMove;
                _dashboardScatterChart.MouseUp += DashboardScatterMouseUp;
                _dashboardReset.Click += DashboardResetClicked;
                _dashboardHistogramVariable.SelectedIndexChanged += DashboardSelectionChanged;
                _dashboardHistogramMode.SelectedIndexChanged += DashboardSelectionChanged;
                _dashboardHistogramBins.ValueChanged += DashboardSelectionChanged;
                _dashboardHistogramBandwidth.ValueChanged += DashboardSelectionChanged;
                _dashboardHistogramMode.SelectedIndexChanged += DashboardHistogramModeChanged;
                _dashboardGroupFilter.Click += DashboardGroupFilterClicked;
                _dashboardGroupList.ItemCheck += DashboardGroupItemChecked;
                _dashboardHistoryChart.MouseClick += DashboardChartClicked;
                _dashboardScatterChart.MouseClick += DashboardChartClicked;
                _dashboardParallelChart.MouseClick += DashboardChartClicked;
                _dashboardHistogramChart.MouseClick += DashboardChartClicked;
                _dashboardHistoryChart.Resize += DashboardChartResized;
                _dashboardScatterChart.Resize += DashboardChartResized;
                _dashboardHeatmapChart.Resize += DashboardChartResized;
                _dashboardParallelChart.Resize += DashboardChartResized;
                _dashboardHistogramChart.Resize += DashboardChartResized;
                _iterationGrid.CellClick += IterationGridCellClicked;
                _iterationGrid.CellMouseDown += IterationGridMouseDown;
                _dashboardSelection.SelectionChanged += DashboardLinkedSelectionChanged;
                FormClosed += (sender, args) => DisposeDashboardResults();
                _generateReport.Click += (sender, args) =>
                    GenerateReportRequested?.Invoke(ReadReportSettings());
                _reportTitle.Leave += ReportSettingChanged;
                _reportSubtitle.Leave += ReportSettingChanged;
                _reportPageSize.SelectedIndexChanged += ReportSettingChanged;
                _reportOrientation.SelectedIndexChanged += ReportSettingChanged;
                _reportSnapshot.CheckedChanged += ReportSettingChanged;
                _reportIterations.CheckedChanged += ReportSettingChanged;
                _gcodeBranch.SelectedIndexChanged += (sender, args) => RenderGcodeBranch();
                _saveGcode.Click += SaveGcodeClicked;
                WireSampleNameEvents();
                _liveStatusPollTimer.Tick += (sender, args) => LiveStatusPollTick?.Invoke();
                _liveStatusPollTimer.Start();
                _groups.Resize += (sender, args) => ResizeKpiGroupBoxes();
                _groups.DragEnter += GroupDragEnter;
                _groups.DragOver += GroupDragOver;
                _groups.DragDrop += GroupDragDrop;
                Shown += (sender, args) => BeginInvoke((Action)(() =>
                {
                    Bounds = EnsureVisible(Bounds);
                    ResizeKpiGroupBoxes();
                    LayoutDashboardRows();
                }));
                Resize += RememberNormalBounds;
                Move += RememberNormalBounds;
                UpdateKpis(set, disabledKeys, disabledGroups, sourceStates, showValues);
            }

            public void RestoreAndActivate()
            {
                if (WindowState == FormWindowState.Minimized)
                {
                    Rectangle restoreBounds = EnsureVisible(_lastNormalBounds);
                    WindowState = FormWindowState.Normal;
                    Bounds = restoreBounds;
                    _lastNormalBounds = restoreBounds;
                }
                Show();
                Activate();
                BringToFront();
            }

            public void ShowOwned()
            {
                IWin32Window owner = Instances.DocumentEditor ??
                    Instances.ActiveCanvas?.FindForm();
                if (owner != null)
                    Show(owner);
                else
                    Show();
            }
            private void UpdateStatus(WasperKpiSet set)
            {
                int count = set?.Items?.Count ?? 0;
                int groupCount = set?.Items?
                    .Select(item => item.DisplayGroup)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() ?? 0;
                _status.Text = $"{count} global KPIs in {groupCount} groups";
            }

            private void RememberNormalBounds(object sender, EventArgs eventArgs)
            {
                if (WindowState == FormWindowState.Normal)
                    _lastNormalBounds = Bounds;
            }

            private static Rectangle EnsureVisible(Rectangle requested)
            {
                Screen target = Screen.AllScreens.FirstOrDefault(screen =>
                    screen.WorkingArea.Contains(requested.Location)) ?? Screen.PrimaryScreen;
                Rectangle area = target.WorkingArea;
                int margin = 12;
                int width = Math.Min(
                    Math.Max(560, requested.Width),
                    Math.Max(560, area.Width - (margin * 2)));
                int height = Math.Min(
                    Math.Max(320, requested.Height),
                    Math.Max(320, area.Height - (margin * 2)));
                int x = Math.Max(area.Left + margin, Math.Min(requested.X, area.Right - width - margin));
                int y = Math.Max(area.Top + margin, Math.Min(requested.Y, area.Bottom - height - margin));
                return new Rectangle(x, y, width, height);
            }

            private static Rectangle NormalizeStoredBounds(Rectangle bounds, int storedDpi)
            {
                double scale = 96.0 / Math.Max(96, storedDpi);
                if (Math.Abs(scale - 1.0) < 0.01)
                    return bounds;
                return new Rectangle(
                    (int)Math.Round(bounds.X * scale),
                    (int)Math.Round(bounds.Y * scale),
                    Math.Max(1, (int)Math.Round(bounds.Width * scale)),
                    Math.Max(1, (int)Math.Round(bounds.Height * scale)));
            }

            private static int CurrentSystemDpi()
            {
                try
                {
                    using Graphics graphics = Graphics.FromHwnd(IntPtr.Zero);
                    return Math.Max(96, (int)Math.Round(graphics.DpiX));
                }
                catch
                {
                    return 96;
                }
            }

            protected override void OnFormClosed(FormClosedEventArgs eventArgs)
            {
                Image preview = _snapshotPreview.Image;
                _snapshotPreview.Image = null;
                preview?.Dispose();
                foreach (PictureBox qrBox in _mobileQrImages)
                {
                    Image qrImage = qrBox.Image;
                    qrBox.Image = null;
                    qrImage?.Dispose();
                }
                _liveStatusPollTimer.Stop();
                _liveStatusPollTimer.Dispose();
                base.OnFormClosed(eventArgs);
                ViewClosed?.Invoke();
            }

            private sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
            {
                internal BufferedFlowLayoutPanel()
                {
                    DoubleBuffered = true;
                    ResizeRedraw = true;
                }
            }

            // Fixes a visible top-to-bottom "sweep" repaint of _parameterGrid/
            // _iterationGrid whenever the Study tab becomes visible again after
            // switching away and back -- a plain DataGridView isn't double-
            // buffered by default, so WinForms' standard "hidden TabPage gets
            // fully invalidated and repainted on show" behavior paints each row
            // individually instead of all at once. Same fix as KpiCheckedListBox
            // below, applied to DataGridView instead of CheckedListBox.
            private sealed class BufferedDataGridView : DataGridView
            {
                internal BufferedDataGridView()
                {
                    SetStyle(
                        ControlStyles.AllPaintingInWmPaint |
                        ControlStyles.OptimizedDoubleBuffer,
                        true);
                }
            }

            private sealed class SourceActionButton : Button
            {
                internal bool SourceEnabled { get; set; }
            }

            private sealed class KpiCheckedListBox : CheckedListBox
            {
                private const int LbSetItemHeight = 0x01A0;
                private int _requestedItemHeight;

                internal List<WasperKpi> Records { get; set; } = new List<WasperKpi>();
                internal bool ShowValues { get; set; }

                internal KpiCheckedListBox()
                {
                    IntegralHeight = false;
                    SetStyle(
                        ControlStyles.AllPaintingInWmPaint |
                        ControlStyles.OptimizedDoubleBuffer,
                        true);
                }

                internal void ApplyItemHeight(int itemHeight)
                {
                    _requestedItemHeight = Math.Max(1, itemHeight);
                    ItemHeight = _requestedItemHeight;
                    ApplyNativeItemHeight();
                }

                protected override void OnHandleCreated(EventArgs eventArgs)
                {
                    base.OnHandleCreated(eventArgs);
                    ApplyNativeItemHeight();
                }

                protected override void OnDrawItem(DrawItemEventArgs eventArgs)
                {
                    if (!ShowValues ||
                        eventArgs.Index < 0 ||
                        eventArgs.Index >= Records.Count)
                    {
                        base.OnDrawItem(eventArgs);
                        return;
                    }

                    eventArgs.DrawBackground();
                    WasperKpi item = Records[eventArgs.Index];
                    bool isChecked = GetItemChecked(eventArgs.Index);
                    var checkState = isChecked
                        ? System.Windows.Forms.VisualStyles.CheckBoxState.CheckedNormal
                        : System.Windows.Forms.VisualStyles.CheckBoxState.UncheckedNormal;
                    Size checkSize = CheckBoxRenderer.GetGlyphSize(
                        eventArgs.Graphics,
                        checkState);
                    int checkY = eventArgs.Bounds.Top + 4;
                    CheckBoxRenderer.DrawCheckBox(
                        eventArgs.Graphics,
                        new Point(eventArgs.Bounds.Left + 2, checkY),
                        checkState);

                    int textLeft = eventArgs.Bounds.Left + checkSize.Width + 7;
                    int textWidth = Math.Max(1, eventArgs.Bounds.Right - textLeft - 3);
                    int lineHeight = Math.Max(Font.Height + 3, 18);
                    bool selected = (eventArgs.State & DrawItemState.Selected) != 0;
                    Color labelColor = selected ? SystemColors.HighlightText : ForeColor;
                    Color valueColor = selected
                        ? SystemColors.HighlightText
                        : Color.FromArgb(70, 70, 70);
                    TextRenderer.DrawText(
                        eventArgs.Graphics,
                        Convert.ToString(Items[eventArgs.Index]),
                        Font,
                        new Rectangle(
                            textLeft,
                            eventArgs.Bounds.Top + 2,
                            textWidth,
                            lineHeight),
                        labelColor,
                        TextFormatFlags.EndEllipsis |
                        TextFormatFlags.NoPrefix |
                        TextFormatFlags.SingleLine);
                    TextRenderer.DrawText(
                        eventArgs.Graphics,
                        FormatKpiValue(item),
                        Font,
                        new Rectangle(
                            textLeft + 9,
                            eventArgs.Bounds.Top + lineHeight + 2,
                            Math.Max(1, textWidth - 9),
                            Math.Max(lineHeight, eventArgs.Bounds.Height - lineHeight - 3)),
                        valueColor,
                        TextFormatFlags.EndEllipsis |
                        TextFormatFlags.NoPrefix |
                        TextFormatFlags.SingleLine);
                    eventArgs.DrawFocusRectangle();
                }

                private void ApplyNativeItemHeight()
                {
                    if (!IsHandleCreated || _requestedItemHeight <= 0)
                        return;
                    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                        return;

                    try
                    {
                        SendMessage(
                            Handle,
                            LbSetItemHeight,
                            IntPtr.Zero,
                            new IntPtr(_requestedItemHeight));
                    }
                    catch
                    {
                        // ItemHeight already provides a cross-platform layout fallback.
                    }
                }

                [DllImport("user32.dll", CharSet = CharSet.Auto)]
                private static extern IntPtr SendMessage(
                    IntPtr window,
                    int message,
                    IntPtr wordParameter,
                    IntPtr longParameter);
            }
        }
    }
}
