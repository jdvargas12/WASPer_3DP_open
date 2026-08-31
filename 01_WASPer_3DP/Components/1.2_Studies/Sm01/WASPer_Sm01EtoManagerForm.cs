using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Eto.Drawing;
using Eto.Forms;

using Grasshopper;
using Rhino;
using Rhino.UI;

namespace WASPer_3DP.Components._1_2_Studies
{
    public sealed partial class wsp_Sm01_WASPer_Study_Manager
    {
        /// <summary>
        /// Cross-platform Eto.Forms implementation of the complete Sm01 Study Manager view.
        /// Workflow, persistence, study execution, and fabrication logic remain in the enclosing
        /// Grasshopper component and communicate with this form through ISm01ManagerView.
        /// </summary>
        private sealed class Sm01EtoManagerForm : Form, ISm01ManagerView
        {
            // ----- Lifecycle bookkeeping -----------------------------------------------------

            private bool _closed;
            // Tracked ourselves (rather than reading Location/Size back off the live window)
            // because we only want the *last normal* placement, matching KpiManagerForm's
            // _lastNormalBounds -- both feed the same Write()/manager_x/y/width/height round trip
            // in wsp_Sm01_WASPer Study Manager.cs.
            private Point _lastNormalLocation;
            private Size _lastNormalSize;
            private readonly UITimer _liveStatusPollTimer = new UITimer { Interval = 1.5 };

            // ----- KPI tab ------------------------------------------------------------------
            // Simplified from the WinForms version (WASPer_Sm01KpiTab.cs): per-group boxes and
            // per-KPI rows are plain CheckBoxes in a vertical StackLayout rather than an owner-
            // drawn CheckedListBox, and "show values" appends the value inline to each row's text
            // instead of a two-line owner-drawn row. Group ordering supports both explicit Move
            // buttons and Eto-native drag-and-drop; the old pixel-resize splitter remains omitted.
            private const string KpiGroupDragDataType = "application/x-wasper-kpi-group";
            private static readonly WasperFabricationUnitMode[] FabricationUnitModes =
            {
                WasperFabricationUnitMode.Auto,
                WasperFabricationUnitMode.Millimetres,
                WasperFabricationUnitMode.Centimetres,
                WasperFabricationUnitMode.Metres
            };
            private readonly DropDown _fabricationUnits = new DropDown();
            private bool _updatingFabricationUnits;
            private WasperFabricationUnitMode _fabricationUnitMode;
            private int? _sourceFabricationUnitCode;

            private readonly StackLayout _kpiGroupsPanel = new StackLayout
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                VerticalContentAlignment = VerticalAlignment.Top
            };
            private readonly Dictionary<string, List<CheckBox>> _kpiItemCheckBoxes =
                new Dictionary<string, List<CheckBox>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<WasperKpi>> _kpiItemRecords =
                new Dictionary<string, List<WasperKpi>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, CheckBox> _kpiGroupToggles =
                new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, Control> _kpiGroupPanels =
                new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase);
            private readonly List<string> _visibleKpiGroupOrder = new List<string>();
            private readonly Dictionary<Guid, List<Button>> _kpiSourceButtons =
                new Dictionary<Guid, List<Button>>();
            private readonly Dictionary<Guid, string> _kpiSourceNames = new Dictionary<Guid, string>();
            private string _kpiStructureKey = string.Empty;
            private bool _kpiShowValues;
            private bool _updatingKpiControls;
            private readonly Label _kpiStatus = new Label { Wrap = WrapMode.Word };
            private Button _kpiShowValuesButton;

            // ----- Export tab -----------------------------------------------------------------
            // Simplified from the WinForms version (WASPer_Sm01ExportTab.cs): the settings/preview
            // SplitContainer is a plain two-column TableLayout here (no user-draggable splitter --
            // no Eto Splitter precedent checked yet); NumericUpDown becomes NumericStepper. The
            // preview image needs an explicit conversion boundary because WasperViewportCapture.
            // Capture(...) returns a System.Drawing.Bitmap (AGENTS.md's cross-platform UI policy
            // calls this out for the Dashboard's charts too) -- see ConvertToEtoImage, a PNG-
            // via-MemoryStream round trip rather than any direct pixel interop.
            private static readonly string[] ExportFormats = { "CSV", "Excel", "JSON", "All" };
            private static readonly string[] ExportLayouts = { "Iterations in rows", "KPIs in rows" };

            private readonly TextBox _exportFileName = new TextBox();
            private readonly TextBox _exportFilePath = new TextBox();
            private readonly DropDown _exportFormat = new DropDown { DataStore = ExportFormats };
            private readonly DropDown _exportLayoutDropdown = new DropDown { DataStore = ExportLayouts };
            private Button _exportBrowse;
            private Button _exportResetFiles;
            private Button _exportWriteWithRunButton;
            private readonly Label _exportStatus = new Label { Wrap = WrapMode.Word };
            private bool _updatingExportControls;
            private bool _exportFilePathShowingDefault;
            private bool _exportWriteWithRunOn;

            private readonly CheckBox _snapshotEnabled = new CheckBox
            {
                Text = "Save viewport snapshots with each iteration"
            };
            private Button _linkVisualizationButton;
            private Button _unlinkVisualizationButton;
            private readonly Label _linkedVisualizationStatus = new Label
            {
                Wrap = WrapMode.Word,
                Text = "No visualization component linked."
            };
            private Guid _linkedVisualizationId;
            private string _linkedVisualizationName = string.Empty;
            private readonly List<string> _snapshotViewportNames = new List<string>();
            private readonly DropDown _snapshotViewport = new DropDown();
            private Button _refreshViewportsButton;
            private Button _previewSnapshotButton;
            private readonly NumericStepper _snapshotWidth = new NumericStepper
            {
                MinValue = 64,
                MaxValue = 16384,
                Value = 1920
            };
            private readonly NumericStepper _snapshotHeight = new NumericStepper
            {
                MinValue = 64,
                MaxValue = 16384,
                Value = 1080
            };
            private readonly NumericStepper _snapshotDpi = new NumericStepper
            {
                MinValue = 1,
                MaxValue = 1200,
                Value = 72
            };
            private readonly NumericStepper _snapshotWait = new NumericStepper
            {
                MinValue = 0,
                MaxValue = 10000,
                Increment = 50,
                Value = 500
            };
            private readonly ImageView _snapshotPreview = new ImageView();
            private readonly Label _snapshotStatus = new Label
            {
                Wrap = WrapMode.Word,
                Text = "Select a viewport and refresh the preview."
            };
            private bool _updatingSnapshotControls;

            // ----- Report tab -------------------------------------------------------------------

            private static readonly string[] ReportPageSizes = { "A4", "A3", "Letter", "Legal" };
            private static readonly string[] ReportOrientations = { "Portrait", "Landscape" };

            private readonly TextBox _reportTitle = new TextBox();
            private readonly TextBox _reportSubtitle = new TextBox();
            private readonly DropDown _reportPageSize = new DropDown { DataStore = ReportPageSizes };
            private readonly DropDown _reportOrientation = new DropDown { DataStore = ReportOrientations };
            private readonly CheckBox _reportSnapshot = new CheckBox
            {
                Text = "Include active Rhino viewport snapshot"
            };
            private readonly CheckBox _reportIterations = new CheckBox
            {
                Text = "Include iteration preview table"
            };
            private readonly Label _reportStatus = new Label { Wrap = WrapMode.Word };
            private bool _updatingReportControls;

            // ----- G-code tab ---------------------------------------------------------------

            private readonly DropDown _gcodeBranch = new DropDown();
            private readonly Button _saveGcode;
            private readonly TextArea _gcodeViewer = new TextArea
            {
                ReadOnly = true,
                Wrap = false,
                Font = new Font(FontFamilies.Monospace, 9f)
            };
            private readonly Label _gcodeStatus = new Label { Wrap = WrapMode.Word };
            private List<List<string>> _displayedGcodeBranches = new List<List<string>>();

            // ----- Sample Name tab -------------------------------------------------------------
            // Simplified from the WinForms version (WASPer_Sm01SampleNameTab.cs): the per-group
            // CheckedListBox filter becomes a StackLayout of per-group CheckBoxes, reusing the exact
            // pattern already used for the KPI tab's group toggles (_kpiGroupToggles). The composed
            // list supports Eto-native drag reordering alongside Move buttons, and literal text
            // segments can be selected or double-clicked and edited in place. Items are plain
            // SampleNamePropertyOption objects relying on
            // its ToString() override (== Label) for display text, the same reliance WinForms'
            // ListBox already has on that override; DataStore is (re)assigned wholesale on every
            // refresh and read back via SelectedIndex, mirroring the one proven DropDown pattern in
            // this file rather than introducing Items.Add/Clear.
            private readonly ListBox _sampleNameAvailable = new ListBox(); // Unverified -- see class doc.
            private readonly GridView _sampleNameComposed = new GridView
            {
                ShowHeader = false,
                AllowMultipleSelection = false,
                AllowEmptySelection = true,
                RowHeight = 24
            };
            private readonly StackLayout _sampleNameGroupFilterPanel = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 2,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            private readonly Dictionary<string, CheckBox> _sampleNameGroupToggles =
                new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _sampleNameHiddenGroups =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly TextBox _sampleNameTextInput = new TextBox();
            private const string SampleNameDragDataType = "application/x-wasper-sample-name-token";
            private Button _sampleNameInsertText;
            private Button _sampleNameEditText;
            private Button _sampleNameRemove;
            private Button _sampleNameMoveUp;
            private Button _sampleNameMoveDown;
            private readonly Label _sampleNamePreview = new Label { Wrap = WrapMode.Word };
            private List<SampleNamePropertyOption> _sampleNameLastOptions =
                new List<SampleNamePropertyOption>();
            private List<string> _sampleNameLastTokens = new List<string>();
            private Dictionary<string, SampleNamePropertyOption> _sampleNameOptionsByKey =
                new Dictionary<string, SampleNamePropertyOption>(StringComparer.OrdinalIgnoreCase);
            // Index into the composed list currently loaded into _sampleNameTextInput for editing --
            // mirrors KpiManagerForm's field of the same name/purpose.
            private int _sampleNameEditingTextIndex = -1;
            // Selection to restore after the next UpdateSampleNameComposer rebuild (set by
            // MoveComposedSampleNameToken so repeated Move up/down clicks keep the same item
            // selected instead of losing selection on every solve-triggered refresh).
            private int _sampleNamePendingSelectIndex = -1;
            private int _sampleNameDragIndex = -1;
            private PointF _sampleNameDragStart;
            private bool _sampleNameDragStarted;
            private bool _updatingSampleNameComposer;

            // ----- Study tab ---------------------------------------------------------------------
            // Simplified from the WinForms version (WASPer_Sm01StudyTab.cs): both DataGridViews
            // become Eto GridView, the biggest single piece of unverified API surface in this class
            // (GridView/GridColumn/TextBoxCell/CheckBoxCell/PropertyBinding<T> -- none has any
            // in-repo precedent; PropertyBinding<T>(string) was chosen over the expression-based
            // Binding.Property<T,TValue>(...) overload as the longer-standing, more conservatively
            // documented Eto API, but treat the whole grid setup as the least-confident part of this
            // file). A worthwhile simplification falls out of it, though: because GridView edits the
            // bound row object directly (two-way property binding) rather than WinForms' cell-value
            // extraction, ReadParameterConfiguration() below reads straight from _studyParameterRows
            // instead of walking DataGridViewRow.Cells. Nested Eto Splitters divide the parameter
            // grid, captured iterations, and activity log so all three regions remain adjustable.
            // RichTextBox
            // becomes the same read-only TextArea already used for the G-code viewer; AppendText's
            // caret-follow/ScrollToCaret behavior has no attempted Eto equivalent here, so the log
            // does not auto-scroll -- a known, flagged regression versus WinForms.
            private sealed class StudyParameterRow
            {
                public Guid SliderId { get; set; }
                public bool? Enabled { get; set; } = true;
                public string Name { get; set; } = string.Empty;
                public string Current { get; set; } = string.Empty;
                public string Minimum { get; set; } = string.Empty;
                public string Maximum { get; set; } = string.Empty;
                public string Samples { get; set; } = string.Empty;
            }

            private sealed class StudyIterationRow
            {
                public string Index { get; set; } = string.Empty;
                public string SampleName { get; set; } = string.Empty;
                public string Parameters { get; set; } = string.Empty;
                public string Kpis { get; set; } = string.Empty;
                public string Status { get; set; } = string.Empty;
                public string Captured { get; set; } = string.Empty;
            }

            private readonly GridView _parameterGrid = new GridView(); // Unverified -- see class doc.
            private readonly GridView _iterationGrid = new GridView(); // Unverified -- see class doc.
            private GridColumn _studyMinColumn;
            private GridColumn _studyMaxColumn;
            private GridColumn _studySamplesColumn;
            private List<StudyParameterRow> _studyParameterRows = new List<StudyParameterRow>();
            private string _studyParameterStructureKey = string.Empty;
            private List<StudyIterationRow> _studyIterationRows = new List<StudyIterationRow>();
            private bool _updatingIterationGrid;
            private bool _syncingStudyDashboardSelection;

            private Button _linkSelected;
            private Button _unlinkSelected;
            private Button _restoreDefaults;
            private readonly Label _estimatedIterations = new Label { Wrap = WrapMode.Word };
            private Button _runStudy;
            private Button _resumeStudy;
            private Button _stopStudy;
            private Button _captureIteration;
            private Button _clearIterations;
            private Button _saveStudy;
            private readonly ProgressBar _studyProgress = new ProgressBar { Width = 220 };

            private readonly DropDown _studyLibrary = new DropDown();
            private Button _refreshStudies;
            private Button _browseStudy;
            private Button _forgetStudy;
            private Button _studyCompatibility;
            private Button _loadStudy;
            private Button _resumeSavedStudy;
            private readonly Label _studyLibraryStatus = new Label { Wrap = WrapMode.Word };
            private bool _updatingStudyLibrary;

            private readonly Label _studyStatus = new Label { Wrap = WrapMode.Word };
            private readonly TextArea _studyLog = new TextArea { ReadOnly = true, Wrap = true };
            private string _lastLoggedStudyStatus = string.Empty;

            // ----- Process Viewer (XR) tab -------------------------------------------------------
            // Simplified from the WinForms version (WASPer_Sm01ProcessViewerTab.cs): matches its own
            // two 2026-08-19 scope pullbacks rather than reversing them -- the vvvv (Gamma) native
            // viewer button (EnableVvvvViewerButton = false there) and the "Dump Full Study" section
            // (IncludeDumpFullStudySection = false there) are both left out entirely here too, so
            // ProcessViewerLaunchRequested/DumpFullStudyRequested/DumpStudyOpenFolderRequested stay
            // declared-but-unraised, matching what the WinForms view itself currently does. Restore
            // Selected uses the shared Dashboard/Study-grid selection. WinForms' Focused-based
            // "has the user edited this field"
            // tracking (_processViewerFolderEdited/_processViewerJobIdEdited) becomes a
            // _updatingProcessViewer guard flag instead, the same pattern already used for every
            // other tab's Update* re-entrancy (_updatingKpiControls etc.) -- lower-risk than the
            // still-unverified Control.HasFocus/LostFocus this class has flagged since the KPI tab.
            // The FlowLayoutPanel's wrapping wrap-to-next-line QR gallery becomes a plain vertical
            // StackLayout (one mobile-access entry per row) -- no Eto wrapping-flow container has
            // in-repo precedent. WinForms' BackColor-driven state cues (green Live button, orange
            // "install .NET" warning styling) are dropped -- each already carries the same
            // information in its Text/ToolTip, which is the reliable signal across platforms.
            private readonly Label _processViewerSample = new Label
            {
                Wrap = WrapMode.Word,
                Text = "Current solution"
            };
            private readonly Label _processViewerSelection = new Label
            {
                Wrap = WrapMode.Word,
                Text = "No individual selected"
            };
            private readonly Label _processViewerPathState = new Label
            {
                Wrap = WrapMode.Word,
                Text = "No wsp_path connected"
            };
            private readonly Label _processViewerJobState = new Label
            {
                Wrap = WrapMode.Word,
                Text = "No package exported"
            };
            private readonly Label _processViewerAppState = new Label
            {
                Wrap = WrapMode.Word,
                Text = "Viewer unavailable"
            };
            private readonly Label _processViewerLiveStatus = new Label
            {
                Wrap = WrapMode.Word,
                Text = "Not connected"
            };
            private readonly TextBox _processViewerFolder = new TextBox();
            private readonly TextBox _processViewerJobId = new TextBox();
            private Button _processViewerBrowse;
            private Button _processViewerRestore;
            private Button _processViewerExport;
            private Button _processViewerOpenBrowser;
            private Button _processViewerRefresh;
            private Button _processViewerLiveToggle;
            private bool _liveToggleOn = true;
            private Button _processViewerPushChange;
            private Button _processViewerOpenFolder;
            private Button _processViewerDownloadGuide;
            private readonly Label _processViewerStatus = new Label { Wrap = WrapMode.Word, Text = "Ready." };
            private readonly Label _localAccessCaption = new Label
            {
                Wrap = WrapMode.Word,
                Text = "If the browser doesn't open automatically, copy this link into your browser:"
            };
            private readonly TextBox _localAccessUrl = new TextBox
            {
                ReadOnly = true,
                Text = "http://localhost:5252/"
            };
            private Button _localCopyLink;
            private bool _processViewerFolderEdited;
            private bool _processViewerJobIdEdited;
            private bool _updatingProcessViewer;
            private string _processViewerJsonPath = string.Empty;
            private bool _processViewerCanGoLive;
            private bool _processViewerRuntimeReady = true;
            private string _processViewerRuntimeStatus = "Web viewer runtime ready.";

            private readonly DynamicLayout _mobileAccessContainer = new DynamicLayout
            {
                Spacing = new Size(10, 10)
            };
            private readonly List<Control> _mobileAccessCards = new List<Control>();
            private int _mobileAccessColumns;
            private readonly List<Image> _mobileQrImages = new List<Image>();
            private readonly Label _mobileAccessStatus = new Label
            {
                Wrap = WrapMode.Word,
                Text = "Scan with a phone or tablet on the same network as this computer."
            };

            // ----- Dashboard tab --------------------------------------------------------------
            // Simplified from the WinForms version (WASPer_Sm01DashboardTab.cs), which is by far
            // the largest remaining tab. The actual chart math/drawing is NOT reimplemented here --
            // WasperScatterChartRenderer/WasperCorrelationHeatmapRenderer/
            // WasperParallelCoordinatesRenderer/WasperHistogramRenderer (Components/Shared/
            // Visualization/Charts/) are already a host-neutral library: each renders a
            // System.Drawing.Bitmap from a System.Drawing-typed WasperChartRenderOptions/
            // WasperChartDataset/WasperChartSelection, with no WinForms reference anywhere in that
            // library. This tab reuses all four renderers, WasperChartSelection, and
            // WasperDashboardSettings verbatim, and only has to cross the same Eto/System.Drawing
            // conversion boundary already established by ConvertToEtoImage (Export/Process Viewer
            // tabs) to display each WasperChartRenderResult.Bitmap in an ImageView. Note
            // WasperChartHitTarget/WasperChartRenderResult.HitTest(...) take a System.Drawing.PointF,
            // not Eto.Drawing.PointF -- every click handler below converts explicitly.
            //
            // Dropped entirely -- WinForms drag/resize/native-menu chrome with no Eto precedent,
            // consistent with every other tab's simplifications:
            //  - the row/column card splitters and dynamic square-card sizing
            //    (CreateDashboardRowSplitter/SetDashboardRowHeights/ApplyDashboardLayout/etc) --
            //    each chart renders at a fixed DashboardChartWidth x DashboardChartHeight instead,
            //    so there is also no resize-triggered re-render (DashboardChartResized) or
            //    _dashboardDirty tab-visibility optimization to reproduce;
            //  - the snapshot-panel drag-to-resize splitter -- fixed width instead;
            //  - draggable scatter-label repositioning (DashboardScatterMouseDown/Move/Up/
            //    SetDashboardLabelOffset/"Reset labels") -- labels always use the renderer's
            //    automatic placement, so PointLabelOffsets is always empty and
            //    WasperDashboardSettings.ScatterLabelOffsets is never populated from this view;
            // Dashboard point context actions, Ctrl+click additive selection, and the Study-grid /
            // Dashboard selection link are implemented with Eto-native events and ContextMenu.
            // The WinForms popup-based group filter (a ToolStripDropDown hosting a CheckedListBox)
            // becomes an inline StackLayout of CheckBoxes, matching the pattern already used for
            // the KPI tab's per-group toggles and the Sample Name tab's group filter.
            private const int DashboardChartMinimumWidth = 360;
            private const int DashboardChartMinimumHeight = 240;
            private const int DashboardChartMaximumWidth = 4096;
            private const int DashboardChartMaximumHeight = 2048;
            private const int DashboardMultivariateLimit = 14;
            private const int DashboardMaxCategories = 10;
            private const string DashboardParameterGroupName = "Inputs";

            private static readonly System.Drawing.Color[] DashboardCategoryPalette =
            {
                System.Drawing.Color.FromArgb(31, 119, 180),
                System.Drawing.Color.FromArgb(255, 127, 14),
                System.Drawing.Color.FromArgb(44, 160, 44),
                System.Drawing.Color.FromArgb(214, 39, 40),
                System.Drawing.Color.FromArgb(148, 103, 189),
                System.Drawing.Color.FromArgb(140, 86, 75),
                System.Drawing.Color.FromArgb(227, 119, 194),
                System.Drawing.Color.FromArgb(127, 127, 127),
                System.Drawing.Color.FromArgb(188, 189, 34),
                System.Drawing.Color.FromArgb(23, 190, 207)
            };

            private static readonly DashboardScatterStyleOption[] DashboardScatterStyleOptions =
            {
                new DashboardScatterStyleOption(DashboardScatterStyle.Markers, "Markers"),
                new DashboardScatterStyleOption(DashboardScatterStyle.Line, "Line"),
                new DashboardScatterStyleOption(DashboardScatterStyle.Both, "Line + markers")
            };
            private static readonly DashboardHistogramModeOption[] DashboardHistogramModeOptions =
            {
                new DashboardHistogramModeOption(WasperHistogramMode.Bars, "Bars"),
                new DashboardHistogramModeOption(WasperHistogramMode.Region, "Region"),
                new DashboardHistogramModeOption(WasperHistogramMode.Density, "Density")
            };

            /// <summary>Sentinel entry meaning "do not colour markers by any variable".</summary>
            private static readonly DashboardVariableOption DashboardNoColorOption =
                new DashboardVariableOption(string.Empty, "(no colour)", string.Empty);

            private readonly Label _dashboardStatus = new Label
            {
                Wrap = WrapMode.Word,
                Text = "No captured iterations yet."
            };
            private readonly DropDown _dashboardHistoryKpi = new DropDown();
            private readonly DropDown _dashboardScatterX = new DropDown();
            private readonly DropDown _dashboardScatterY = new DropDown();
            private readonly DropDown _dashboardScatterColor = new DropDown();
            private readonly DropDown _dashboardHistogramVariable = new DropDown();
            private readonly DropDown _dashboardScatterStyle = new DropDown
            {
                DataStore = DashboardScatterStyleOptions
            };
            private readonly DropDown _dashboardHistogramMode = new DropDown
            {
                DataStore = DashboardHistogramModeOptions
            };
            private readonly NumericStepper _dashboardTextSize = new NumericStepper
            {
                MinValue = 70,
                MaxValue = 200,
                Increment = 10,
                Value = 100,
                Width = 70
            };
            private readonly NumericStepper _dashboardHistogramBins = new NumericStepper
            {
                MinValue = 2,
                MaxValue = 60,
                Value = 12,
                Width = 70
            };
            private readonly NumericStepper _dashboardHistogramBandwidth = new NumericStepper
            {
                MinValue = 10,
                MaxValue = 300,
                Increment = 10,
                Value = 100,
                Width = 70,
                Visible = false
            };
            private readonly Label _dashboardHistogramParameterLabel = new Label { Text = "Bins" };
            private readonly CheckBox _dashboardScatterNames = new CheckBox { Text = "Show names" };
            private readonly CheckBox _dashboardScatterValues = new CheckBox { Text = "Show data" };
            private Button _dashboardShowInGrasshopper;
            private Button _dashboardReset;

            private readonly StackLayout _dashboardGroupFilterPanel = new StackLayout
            {
                Orientation = Orientation.Vertical,
                Spacing = 2,
                HorizontalContentAlignment = HorizontalAlignment.Stretch
            };
            private readonly Dictionary<string, CheckBox> _dashboardGroupToggles =
                new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _dashboardHiddenGroups =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private bool _updatingDashboardGroups;

            private sealed class DashboardChartView : Drawable
            {
                private Image _image;
                private Size _renderSize;

                public DashboardChartView()
                {
                    MinimumSize = new Size(DashboardChartMinimumWidth, DashboardChartMinimumHeight);
                    BackgroundColor = Colors.White;
                }

                public void ReplaceImage(Image image, Size renderSize)
                {
                    Image previous = _image;
                    _image = image;
                    _renderSize = renderSize;
                    previous?.Dispose();
                    Invalidate();
                }

                public System.Drawing.PointF ToRenderPoint(Eto.Drawing.PointF point)
                {
                    int width = Math.Max(1, ClientSize.Width);
                    int height = Math.Max(1, ClientSize.Height);
                    return new System.Drawing.PointF(
                        (float)(point.X * Math.Max(1, _renderSize.Width) / width),
                        (float)(point.Y * Math.Max(1, _renderSize.Height) / height));
                }

                protected override void OnPaint(PaintEventArgs eventArgs)
                {
                    base.OnPaint(eventArgs);
                    eventArgs.Graphics.Clear(Colors.White);
                    if (_image == null || ClientSize.Width <= 0 || ClientSize.Height <= 0)
                        return;
                    if (_renderSize.Width == ClientSize.Width &&
                        _renderSize.Height == ClientSize.Height)
                    {
                        eventArgs.Graphics.ImageInterpolation = ImageInterpolation.None;
                        eventArgs.Graphics.DrawImage(
                            _image,
                            new RectangleF(0, 0, ClientSize.Width, ClientSize.Height));
                        return;
                    }

                    // Temporary scaling is only visible during/before the debounced resize render.
                    // Medium preserves line contrast better than repeatedly filtering chart text and
                    // one-pixel grid lines with the photographic High interpolation mode.
                    eventArgs.Graphics.ImageInterpolation = ImageInterpolation.Medium;
                    eventArgs.Graphics.DrawImage(
                        _image,
                        new RectangleF(0, 0, ClientSize.Width, ClientSize.Height));
                }
            }

            private readonly DashboardChartView _dashboardHistoryImage = new DashboardChartView();
            private readonly DashboardChartView _dashboardScatterImage = new DashboardChartView();
            private readonly DashboardChartView _dashboardHeatmapImage = new DashboardChartView();
            private readonly DashboardChartView _dashboardParallelImage = new DashboardChartView();
            private readonly DashboardChartView _dashboardHistogramImage = new DashboardChartView();
            private readonly UITimer _dashboardResizeTimer = new UITimer { Interval = 0.2 };

            private readonly Label _dashboardSelectionDetails = new Label
            {
                Wrap = WrapMode.Word,
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
            private readonly WasperChartSelection _dashboardSelection = new WasperChartSelection();
            private ContextMenu _dashboardPointMenu;

            private WasperChartRenderResult _dashboardHistoryResult;
            private WasperChartRenderResult _dashboardScatterResult;
            private WasperChartRenderResult _dashboardHeatmapResult;
            private WasperChartRenderResult _dashboardParallelResult;
            private WasperChartRenderResult _dashboardHistogramResult;

            private WasperDashboardSettings _dashboardSettings = new WasperDashboardSettings();
            private bool _updatingDashboard;
            private List<WasperStudyIteration> _dashboardIterations = new List<WasperStudyIteration>();
            private List<WasperStudyParameter> _dashboardParameters = new List<WasperStudyParameter>();

            private readonly ImageView _dashboardSnapshotImage = new ImageView
            {
                Size = new Size(220, 220)
            };
            private readonly Label _dashboardSnapshotCaption = new Label
            {
                Wrap = WrapMode.None,
                Text = "Select a sample to preview its snapshot."
            };
            private readonly Label _dashboardSnapshotStatus = new Label { Wrap = WrapMode.None };
            private string _dashboardSnapshotPath = string.Empty;
            private string _dashboardSnapshotFolder = string.Empty;

            // DashboardVariableOption/DashboardScatterStyleOption/DashboardHistogramModeOption all
            // implement Eto.Forms.IListItem (Text + Key), not just ToString(): the Study tab's
            // _studyLibrary.DataStore = List<WasperStudyCatalogEntry> (an established pattern this
            // Dashboard tab otherwise follows for populating a DropDown from a custom object list)
            // relies on ToString() alone, and WasperStudyCatalogEntry does not override it -- if
            // Eto's default DropDown item-text binding turns out to need IListItem or a Text
            // property rather than falling back to ToString(), that dropdown would show type names
            // instead of study names. Unverified either way (no in-repo precedent for a DropDown
            // over a custom object list has been build-confirmed to actually render correctly, only
            // to compile), so these three implement IListItem defensively rather than repeating the
            // same open question a third time.
            private sealed class DashboardVariableOption : IListItem
            {
                public DashboardVariableOption(
                    string key,
                    string label,
                    string unit,
                    string group = null,
                    bool isParameter = false)
                {
                    Key = key ?? string.Empty;
                    Label = label ?? Key;
                    Unit = unit ?? string.Empty;
                    Group = string.IsNullOrWhiteSpace(group)
                        ? (isParameter ? DashboardParameterGroupName : "Other")
                        : group.Trim();
                    IsParameter = isParameter;
                }

                public string Key { get; }
                public string Label { get; }
                public string Unit { get; }
                public string Group { get; }
                public bool IsParameter { get; }
                public string DisplayName => string.IsNullOrWhiteSpace(Unit)
                    ? Label
                    : $"{Label} [{Unit}]";
                // IListItem.Text declares both get and set (confirmed by a real build: CS0535/
                // CS0551 on a get-only explicit implementation) -- the setter is a no-op since
                // DisplayName is always derived from Label/Unit.
                string IListItem.Text { get => DisplayName; set { } }
                public override string ToString() => DisplayName;
            }

            private enum DashboardScatterStyle
            {
                Markers,
                Line,
                Both
            }

            private sealed class DashboardScatterStyleOption : IListItem
            {
                public DashboardScatterStyleOption(DashboardScatterStyle style, string text)
                {
                    Style = style;
                    Text = text ?? string.Empty;
                }

                public DashboardScatterStyle Style { get; }
                // IListItem.Text declares both get and set -- see DashboardVariableOption's comment.
                public string Text { get; set; }
                string IListItem.Key => Style.ToString();
                public override string ToString() => Text;
            }

            private sealed class DashboardHistogramModeOption : IListItem
            {
                public DashboardHistogramModeOption(WasperHistogramMode mode, string text)
                {
                    Mode = mode;
                    Text = text ?? string.Empty;
                }

                public WasperHistogramMode Mode { get; }
                public string Text { get; set; }
                string IListItem.Key => Mode.ToString();
                public override string ToString() => Text;
            }

            /// <summary>One resolved colour category: its label, sort position, colour, and members.</summary>
            private sealed class DashboardCategory
            {
                public string Label { get; set; } = string.Empty;
                public double Sort { get; set; } = double.NaN;
                public System.Drawing.Color Color { get; set; } = System.Drawing.Color.SteelBlue;
                public bool HasSort => !double.IsNaN(Sort);
                public HashSet<int> IndividualIds { get; } = new HashSet<int>();
            }

            public Sm01EtoManagerForm(
                WasperKpiSet set,
                IEnumerable<string> disabledKeys,
                IEnumerable<string> disabledGroups,
                IReadOnlyDictionary<Guid, bool> sourceStates,
                bool showValues,
                bool writeWithRun,
                WasperFabricationUnitMode fabricationUnitMode,
                int? sourceFabricationUnitCode,
                int storedDpi,
                System.Drawing.Rectangle bounds,
                RhinoDoc ownerDocument)
            {
                _fabricationUnitMode = fabricationUnitMode;
                _sourceFabricationUnitCode = sourceFabricationUnitCode;

                Title = "WASPer Study Manager";
                MinimumSize = new Size(560, 320);
                Resizable = true;
                ShowInTaskbar = true;
                RhinoDoc document = ownerDocument ??
                    Instances.ActiveCanvas?.Document?.RhinoDocument ??
                    RhinoDoc.ActiveDoc;
                if (document != null)
                    Owner = RhinoEtoApp.MainWindowForDocument(document);
                this.UseRhinoStyle();

                // Normalize the first WinForms-saved bounds into Eto's logical coordinates. Once
                // this view closes it persists CurrentDpi=96, so subsequent opens are unchanged.
                if (bounds.Width > 0 && bounds.Height > 0)
                {
                    System.Drawing.Rectangle normalized = NormalizeStoredBounds(bounds, storedDpi);
                    Location = new Point(normalized.X, normalized.Y);
                    Size = new Size(normalized.Width, normalized.Height);
                }
                _lastNormalLocation = Location;
                _lastNormalSize = Size;

                _saveGcode = CommandButton("Save G-code...");
                _saveGcode.Enabled = false;

                var tabs = new TabControl();
                tabs.Pages.Add(new TabPage { Text = "KPIs", Content = CreateKpiTab() });
                tabs.Pages.Add(new TabPage { Text = "G-code", Content = CreateGcodeTab() });
                tabs.Pages.Add(new TabPage { Text = "Export", Content = CreateExportTab() });
                tabs.Pages.Add(new TabPage { Text = "Sample Name", Content = CreateSampleNameTab() });
                tabs.Pages.Add(new TabPage { Text = "Study", Content = CreateStudyTab() });
                tabs.Pages.Add(new TabPage { Text = "Dashboard", Content = CreateDashboardTab() });
                tabs.Pages.Add(new TabPage
                {
                    Text = "Process Viewer (XR)",
                    Content = CreateProcessViewerTab()
                });
                tabs.Pages.Add(new TabPage { Text = "Report", Content = CreateReportTab() });
                Content = tabs;

                _liveStatusPollTimer.Elapsed += (_, _) => LiveStatusPollTick?.Invoke();
                _dashboardResizeTimer.Elapsed += (_, _) =>
                {
                    _dashboardResizeTimer.Stop();
                    if (!_closed)
                        RenderDashboardCharts();
                };
                Closed += (_, _) =>
                {
                    _liveStatusPollTimer.Stop();
                    _dashboardResizeTimer.Stop();
                    _closed = true;
                    DisposeViewResources();
                    ViewClosed?.Invoke();
                };
                LocationChanged += (_, _) => RememberNormalBounds();
                SizeChanged += (_, _) =>
                {
                    RememberNormalBounds();
                    if (_mobileAccessCards.Count > 0)
                        RebuildMobileAccessLayout();
                };
                LogicalPixelSizeChanged += (_, _) => ScheduleDashboardRender();

                ConfigureFabricationUnitOptions(fabricationUnitMode, sourceFabricationUnitCode);
                SetExportWriteWithRun(writeWithRun);
                UpdateKpis(set, disabledKeys, disabledGroups, sourceStates, showValues);
            }

            private void RememberNormalBounds()
            {
                if (WindowState == WindowState.Normal)
                {
                    _lastNormalLocation = Location;
                    _lastNormalSize = Size;
                }
            }

            private static System.Drawing.Rectangle NormalizeStoredBounds(
                System.Drawing.Rectangle bounds,
                int storedDpi)
            {
                double scale = 96.0 / Math.Max(96, storedDpi);
                if (Math.Abs(scale - 1.0) < 0.01)
                    return bounds;
                return new System.Drawing.Rectangle(
                    (int)Math.Round(bounds.X * scale),
                    (int)Math.Round(bounds.Y * scale),
                    Math.Max(1, (int)Math.Round(bounds.Width * scale)),
                    Math.Max(1, (int)Math.Round(bounds.Height * scale)));
            }

            // ----- ISm01ManagerView: lifecycle -------------------------------------------------

            public bool IsClosed => _closed;

            public System.Drawing.Rectangle LastNormalBounds => new System.Drawing.Rectangle(
                _lastNormalLocation.X,
                _lastNormalLocation.Y,
                _lastNormalSize.Width,
                _lastNormalSize.Height);

            // Best-effort constant. The WinForms CurrentDpi feeds NormalizeStoredBounds on the next
            // open to counteract WinForms' physical-pixel Bounds when the monitor's DPI changed
            // between sessions; this view works in logical Eto coordinates throughout (see the
            // constructor note above), so there is nothing to rescale and 96 (== "no scaling") is
            // always a safe value to persist.
            public int CurrentDpi => 96;

            public void ShowOwned()
            {
                Show();
                _liveStatusPollTimer.Start();
            }

            public void RestoreAndActivate()
            {
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;
                Show();
                _liveStatusPollTimer.Start();
            }

            // ----- ISm01ManagerView: remaining events -------------------------------------------
            // Declared to satisfy the interface. Most below now fire from a migrated tab (KPI:
            // SelectionApplied/GroupOrderResetRequested/GroupEnabledChanged/SourceEnabledChanged/
            // ShowValuesChanged/FabricationUnitModeChanged; Export: ExportSettingsChanged/
            // ExportLayoutChanged/ResetRequested/WriteWithRunChanged/SnapshotSettingsChanged/
            // LinkVisualizationRequested/UnlinkVisualizationRequested; Sample Name:
            // SampleNameTemplateChanged; Study: LinkSelectedSlidersRequested/
            // UnlinkSlidersRequested/RestoreParameterDefaultsRequested/RunStudyRequested/
            // ResumeStudyRequested/StopStudyRequested/CaptureIterationRequested/
            // ClearIterationsRequested/SaveStudyRequested/RefreshStudyLibraryRequested/
            // BrowseStudyRequested/ForgetPinnedStudyRequested/StudyLibrarySelectionChanged/
            // LoadSavedStudyRequested/ResumeSavedStudyRequested; Process Viewer:
            // ProcessViewerExportRequested/ProcessViewerOpenBrowserRequested/
            // ProcessViewerRefreshRequested/ProcessViewerLiveToggleChanged/
            // ProcessViewerPushChangeRequested/ProcessViewerOpenFolderRequested; Dashboard:
            // DashboardSettingsChanged/ShowIterationRequested) -- the rest stay unraised:
            // ProcessViewerLaunchRequested (vvvv viewer parked, matching WinForms), and DumpFullStudyRequested/
            // DumpStudyOpenFolderRequested (Dump Full Study section parked, matching WinForms) --
            // see the Process Viewer tab's own field-region comment for both. Deliberately not
            // wrapped in #pragma warning disable -- the CS0067 "event is never used" warnings the
            // remaining ones still produce are the honest, visible signal of how much of Sm01
            // remains, and they disappear one at a time as tabs migrate.

            public event Action ViewClosed;
            public event Action LiveStatusPollTick;
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
            public event Action<int> ShowIterationRequested;
            public event Action LinkVisualizationRequested;
            public event Action UnlinkVisualizationRequested;
            public event Action<string, string> ProcessViewerExportRequested;
#pragma warning disable CS0067 // Current legacy feature flags keep these controls hidden.
            public event Action<string> ProcessViewerLaunchRequested;
            public event Action<string> ProcessViewerOpenBrowserRequested;
            public event Action ProcessViewerRefreshRequested;
            public event Action<bool> ProcessViewerLiveToggleChanged;
            public event Action ProcessViewerPushChangeRequested;
            public event Action<string> ProcessViewerOpenFolderRequested;
            public event Action<string, string, bool> DumpFullStudyRequested;
            public event Action<string> DumpStudyOpenFolderRequested;
#pragma warning restore CS0067

            // ----- ISm01ManagerView: events raised by the Report tab (migrated) ---------------

            public event Action<WasperReportSettings> GenerateReportRequested;
            public event Action<WasperReportSettings> ReportSettingsChanged;

            // ----- ISm01ManagerView: remaining Update*/Set* members ---------------------------
            // Every member below now has a real implementation feeding a migrated tab (all eight
            // are migrated as of this file's Dashboard tab addition -- see the class doc).

            public void UpdateFabricationUnits(WasperFabricationUnitMode selectedMode, int? sourceUnitCode)
            {
                FabricationUnitOptionsIfChanged(selectedMode, sourceUnitCode);
            }

            private void ConfigureFabricationUnitOptions(
                WasperFabricationUnitMode selectedMode,
                int? sourceUnitCode)
            {
                _fabricationUnitMode = selectedMode;
                _sourceFabricationUnitCode = sourceUnitCode;
                _updatingFabricationUnits = true;
                try
                {
                    string sourceUnit = sourceUnitCode switch
                    {
                        1 => "cm",
                        2 => "m",
                        0 => "mm",
                        _ => "mm fallback"
                    };
                    _fabricationUnits.DataStore = new[]
                    {
                        $"Auto (Gc03: {sourceUnit})",
                        "Millimetres (mm)",
                        "Centimetres (cm)",
                        "Metres (m)"
                    };
                    int index = Array.IndexOf(FabricationUnitModes, selectedMode);
                    _fabricationUnits.SelectedIndex = index >= 0 ? index : 0;
                }
                finally
                {
                    _updatingFabricationUnits = false;
                }
            }

            private void FabricationUnitOptionsIfChanged(
                WasperFabricationUnitMode selectedMode,
                int? sourceUnitCode)
            {
                // Unlike the WinForms version's UpdateFabricationUnits (which only rebuilds when the
                // selection or the "Auto (Gc03: ...)" label actually changed, to avoid combo-box
                // flicker), this always rebuilds: DataStore = a 4-item string array is cheap and
                // Eto's DropDown has shown no flicker precedent worth guarding against here.
                ConfigureFabricationUnitOptions(selectedMode, sourceUnitCode);
            }

            private Control CreateKpiTab()
            {
                _fabricationUnits.SelectedIndexChanged += (_, _) =>
                {
                    if (_updatingFabricationUnits)
                        return;
                    if (_fabricationUnits.SelectedIndex >= 0 &&
                        _fabricationUnits.SelectedIndex < FabricationUnitModes.Length)
                    {
                        FabricationUnitModeChanged?.Invoke(
                            FabricationUnitModes[_fabricationUnits.SelectedIndex]);
                    }
                };

                var selectAll = CommandButton("Select all");
                var selectNone = CommandButton("Select none");
                var apply = CommandButton("Apply");
                var resetOrder = CommandButton("Reset order");
                _kpiShowValuesButton = CommandButton("Show values");
                selectAll.Click += (_, _) => SetAllKpiChecked(true);
                selectNone.Click += (_, _) => SetAllKpiChecked(false);
                apply.Click += (_, _) => SelectionApplied?.Invoke(CurrentKpiDisabledKeys().ToList());
                resetOrder.Click += (_, _) => GroupOrderResetRequested?.Invoke();
                _kpiShowValuesButton.Click += (_, _) =>
                {
                    _kpiShowValues = !_kpiShowValues;
                    _kpiShowValuesButton.Text = _kpiShowValues ? "Hide values" : "Show values";
                    RefreshKpiCheckBoxTexts();
                    if (!_updatingKpiControls)
                        ShowValuesChanged?.Invoke(_kpiShowValues);
                };

                var footer = new TableLayout
                {
                    Padding = new Padding(10, 3, 10, 5),
                    Spacing = new Size(6, 4),
                    Rows =
                    {
                        new TableRow(new TableCell(_kpiStatus, true)) { ScaleHeight = false },
                        new TableRow(
                            new TableCell(new Label
                            {
                                Text = "Fabrication units",
                                VerticalAlignment = VerticalAlignment.Center
                            }, false),
                            new TableCell(_fabricationUnits, false),
                            new TableCell(_kpiShowValuesButton, false),
                            new TableCell(resetOrder, false),
                            new TableCell(selectAll, false),
                            new TableCell(selectNone, false),
                            new TableCell(apply, false),
                            new TableCell(null, true))
                        { ScaleHeight = false }
                    }
                };

                var groupsScroll = new Scrollable { Content = _kpiGroupsPanel };

                return new TableLayout
                {
                    Rows =
                    {
                        new TableRow(new TableCell(groupsScroll, true)) { ScaleHeight = true },
                        new TableRow(new TableCell(footer, true)) { ScaleHeight = false }
                    }
                };
            }

            public void UpdateKpis(
                WasperKpiSet set,
                IEnumerable<string> disabledKeys,
                IEnumerable<string> disabledGroups,
                IReadOnlyDictionary<Guid, bool> sourceStates,
                bool showValues)
            {
                _updatingKpiControls = true;
                try
                {
                    _kpiShowValues = showValues;
                    if (_kpiShowValuesButton != null)
                        _kpiShowValuesButton.Text = showValues ? "Hide values" : "Show values";

                    var disabled = new HashSet<string>(
                        disabledKeys ?? Enumerable.Empty<string>(),
                        StringComparer.OrdinalIgnoreCase);
                    var disabledBlocks = new HashSet<string>(
                        disabledGroups ?? Enumerable.Empty<string>(),
                        StringComparer.OrdinalIgnoreCase);
                    List<WasperKpi> incomingItems = set?.Items ?? new List<WasperKpi>();
                    string structureKey = BuildKpiStructureKey(incomingItems);

                    if (string.Equals(_kpiStructureKey, structureKey, StringComparison.Ordinal) &&
                        _kpiItemRecords.Count > 0)
                    {
                        UpdateExistingKpiControls(incomingItems, disabledBlocks, sourceStates);
                    }
                    else
                    {
                        Dictionary<string, bool> previousChecks = CurrentKpiChecks();
                        RebuildKpiGroups(incomingItems, disabled, disabledBlocks, sourceStates, previousChecks);
                        _kpiStructureKey = structureKey;
                    }
                    UpdateKpiStatus(set);
                }
                finally
                {
                    _updatingKpiControls = false;
                }
            }

            private static string BuildKpiStructureKey(IEnumerable<WasperKpi> items)
            {
                return string.Join(
                    "",
                    (items ?? Enumerable.Empty<WasperKpi>())
                        .Select(item => string.Join(
                            "",
                            item?.DisplayGroup ?? string.Empty,
                            item?.Key ?? string.Empty,
                            item?.SourceInstanceId.ToString() ?? string.Empty))
                        .OrderBy(token => token, StringComparer.Ordinal));
            }

            private Dictionary<string, bool> CurrentKpiChecks()
            {
                var checks = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, List<WasperKpi>> pair in _kpiItemRecords)
                {
                    List<CheckBox> boxes = _kpiItemCheckBoxes[pair.Key];
                    for (int index = 0; index < pair.Value.Count; index++)
                        checks[pair.Value[index].Key] = boxes[index].Checked == true;
                }
                return checks;
            }

            private IEnumerable<string> CurrentKpiDisabledKeys()
            {
                foreach (KeyValuePair<string, List<WasperKpi>> pair in _kpiItemRecords)
                {
                    List<CheckBox> boxes = _kpiItemCheckBoxes[pair.Key];
                    for (int index = 0; index < pair.Value.Count; index++)
                        if (boxes[index].Checked != true)
                            yield return pair.Value[index].Key;
                }
            }

            private void SetAllKpiChecked(bool value)
            {
                foreach (List<CheckBox> boxes in _kpiItemCheckBoxes.Values)
                    foreach (CheckBox box in boxes)
                        box.Checked = value;
            }

            private void RefreshKpiCheckBoxTexts()
            {
                foreach (KeyValuePair<string, List<WasperKpi>> pair in _kpiItemRecords)
                {
                    List<CheckBox> boxes = _kpiItemCheckBoxes[pair.Key];
                    for (int index = 0; index < pair.Value.Count; index++)
                        boxes[index].Text = KpiCheckBoxText(pair.Value[index]);
                }
            }

            private string KpiCheckBoxText(WasperKpi item)
            {
                string unit = string.IsNullOrWhiteSpace(item?.Unit) ? string.Empty : $" [{item.Unit}]";
                string label = $"{item?.Label}{unit}";
                return _kpiShowValues ? label + "  --  " + FormatKpiValue(item) : label;
            }

            private static string FormatKpiValue(WasperKpi item)
            {
                if (item == null)
                    return "--";
                string value = item.Value.HasValue
                    ? item.Value.Value.ToString("G8", CultureInfo.InvariantCulture)
                    : item.TextValue;
                if (string.IsNullOrWhiteSpace(value))
                    value = "--";
                return string.IsNullOrWhiteSpace(item.Unit) ? value : value + " " + item.Unit;
            }

            private void UpdateKpiStatus(WasperKpiSet set)
            {
                int count = set?.Items?.Count ?? 0;
                int groupCount = set?.Items?
                    .Select(item => item.DisplayGroup)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() ?? 0;
                _kpiStatus.Text = $"{count} global KPIs in {groupCount} groups";
            }

            private void RebuildKpiGroups(
                List<WasperKpi> incomingItems,
                HashSet<string> disabled,
                HashSet<string> disabledBlocks,
                IReadOnlyDictionary<Guid, bool> sourceStates,
                Dictionary<string, bool> previousChecks)
            {
                _kpiGroupsPanel.Items.Clear();
                _kpiItemCheckBoxes.Clear();
                _kpiItemRecords.Clear();
                _kpiGroupToggles.Clear();
                _kpiGroupPanels.Clear();
                _visibleKpiGroupOrder.Clear();
                _kpiSourceButtons.Clear();
                _kpiSourceNames.Clear();

                foreach (IGrouping<string, WasperKpi> group in incomingItems.GroupBy(kpi => kpi.DisplayGroup))
                {
                    List<WasperKpi> records = group.ToList();
                    List<IGrouping<Guid, WasperKpi>> sources = records
                        .Where(item => item.SourceInstanceId != Guid.Empty)
                        .GroupBy(item => item.SourceInstanceId)
                        .ToList();

                    var groupContent = new StackLayout
                    {
                        Orientation = Orientation.Vertical,
                        Spacing = 4,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch
                    };

                    var groupToggle = new CheckBox
                    {
                        Text = "Group enabled",
                        Checked = !disabledBlocks.Contains(group.Key)
                    };
                    groupToggle.CheckedChanged += (_, _) =>
                    {
                        if (!_updatingKpiControls)
                            GroupEnabledChanged?.Invoke(group.Key, groupToggle.Checked == true);
                    };
                    _kpiGroupToggles[group.Key] = groupToggle;
                    string groupName = group.Key;
                    var dragHandle = CommandButton("Drag", 58);
                    var moveUp = CommandButton("Move up", 72);
                    var moveDown = CommandButton("Move down", 82);
                    dragHandle.ToolTip = "Drag this KPI group to reorder it.";
                    dragHandle.MouseDown += (_, eventArgs) =>
                    {
                        if (eventArgs.Buttons != MouseButtons.Primary)
                            return;
                        using var dragData = new DataObject();
                        dragData.SetString(groupName, KpiGroupDragDataType);
                        dragHandle.DoDragDrop(dragData, DragEffects.Move);
                        eventArgs.Handled = true;
                    };
                    moveUp.Click += (_, _) => MoveKpiGroup(groupName, -1);
                    moveDown.Click += (_, _) => MoveKpiGroup(groupName, 1);
                    groupContent.Items.Add(new TableLayout
                    {
                        Spacing = new Size(4, 0),
                        Rows =
                        {
                            new TableRow(
                                new TableCell(groupToggle, true),
                                new TableCell(dragHandle, false),
                                new TableCell(moveUp, false),
                                new TableCell(moveDown, false))
                        }
                    });

                    foreach (IGrouping<Guid, WasperKpi> sourceGroup in sources)
                    {
                        WasperKpi first = sourceGroup.First();
                        string sourceName = string.IsNullOrWhiteSpace(first.SourceNickname)
                            ? first.Source
                            : first.SourceNickname;
                        if (string.IsNullOrWhiteSpace(sourceName))
                            sourceName = "KPI source";
                        bool sourceEnabled = sourceStates == null ||
                            !sourceStates.TryGetValue(sourceGroup.Key, out bool isEnabled) ||
                            isEnabled;
                        var sourceButton = CommandButton(
                            sourceEnabled ? "Disable: " + sourceName : "Enable: " + sourceName,
                            160);
                        sourceButton.Tag = sourceEnabled;
                        Guid sourceId = sourceGroup.Key;
                        sourceButton.Click += (_, _) =>
                        {
                            bool nowEnabled = !(sourceButton.Tag is bool current && current);
                            sourceButton.Tag = nowEnabled;
                            sourceButton.Text = nowEnabled
                                ? "Disable: " + sourceName
                                : "Enable: " + sourceName;
                            if (!_updatingKpiControls)
                                SourceEnabledChanged?.Invoke(sourceId, nowEnabled);
                        };
                        if (!_kpiSourceButtons.TryGetValue(sourceId, out List<Button> buttons))
                        {
                            buttons = new List<Button>();
                            _kpiSourceButtons[sourceId] = buttons;
                            _kpiSourceNames[sourceId] = sourceName;
                        }
                        buttons.Add(sourceButton);
                        groupContent.Items.Add(sourceButton);
                    }

                    var itemBoxes = new List<CheckBox>();
                    foreach (WasperKpi item in records)
                    {
                        var itemBox = new CheckBox
                        {
                            Text = KpiCheckBoxText(item),
                            Checked = previousChecks.TryGetValue(item.Key, out bool prior)
                                ? prior
                                : !disabled.Contains(item.Key),
                            ToolTip = $"{item.Description}\n{item.Source}"
                        };
                        itemBoxes.Add(itemBox);
                        groupContent.Items.Add(itemBox);
                    }
                    _kpiItemCheckBoxes[group.Key] = itemBoxes;
                    _kpiItemRecords[group.Key] = records;

                    var box = new GroupBox
                    {
                        Text = group.Key,
                        Width = 220,
                        Padding = new Padding(8),
                        AllowDrop = true,
                        Content = new Scrollable { Content = groupContent }
                    };
                    box.DragEnter += KpiGroupDragOver;
                    box.DragOver += KpiGroupDragOver;
                    box.DragDrop += (_, eventArgs) => DropKpiGroup(eventArgs, groupName, box);
                    _visibleKpiGroupOrder.Add(group.Key);
                    _kpiGroupPanels[group.Key] = box;
                    _kpiGroupsPanel.Items.Add(box);
                }
            }

            private void MoveKpiGroup(string group, int offset)
            {
                int index = _visibleKpiGroupOrder.FindIndex(
                    value => string.Equals(value, group, StringComparison.OrdinalIgnoreCase));
                int target = index + offset;
                if (index < 0 || target < 0 || target >= _visibleKpiGroupOrder.Count)
                    return;
                string moved = _visibleKpiGroupOrder[index];
                _visibleKpiGroupOrder.RemoveAt(index);
                _visibleKpiGroupOrder.Insert(target, moved);
                RefreshKpiGroupPanels();
                GroupOrderChanged?.Invoke(_visibleKpiGroupOrder.ToList());
            }

            private void KpiGroupDragOver(object sender, DragEventArgs eventArgs)
            {
                eventArgs.Effects = TryGetDraggedKpiGroup(eventArgs, out _)
                    ? DragEffects.Move
                    : DragEffects.None;
            }

            private static bool TryGetDraggedKpiGroup(
                DragEventArgs eventArgs,
                out string groupName)
            {
                groupName = eventArgs?.Data?.GetString(KpiGroupDragDataType);
                return !string.IsNullOrWhiteSpace(groupName);
            }

            private void DropKpiGroup(
                DragEventArgs eventArgs,
                string targetGroup,
                Control targetControl)
            {
                if (!TryGetDraggedKpiGroup(eventArgs, out string draggedGroup) ||
                    string.Equals(draggedGroup, targetGroup, StringComparison.OrdinalIgnoreCase))
                {
                    eventArgs.Effects = DragEffects.None;
                    return;
                }

                int sourceIndex = _visibleKpiGroupOrder.FindIndex(
                    value => string.Equals(value, draggedGroup, StringComparison.OrdinalIgnoreCase));
                if (sourceIndex < 0)
                {
                    eventArgs.Effects = DragEffects.None;
                    return;
                }

                bool insertAfter = eventArgs.Location.X >= targetControl.Size.Width / 2f;
                _visibleKpiGroupOrder.RemoveAt(sourceIndex);
                int targetIndex = _visibleKpiGroupOrder.FindIndex(
                    value => string.Equals(value, targetGroup, StringComparison.OrdinalIgnoreCase));
                if (targetIndex < 0)
                {
                    _visibleKpiGroupOrder.Insert(sourceIndex, draggedGroup);
                    eventArgs.Effects = DragEffects.None;
                    return;
                }

                _visibleKpiGroupOrder.Insert(
                    Math.Min(_visibleKpiGroupOrder.Count, targetIndex + (insertAfter ? 1 : 0)),
                    draggedGroup);
                RefreshKpiGroupPanels();
                GroupOrderChanged?.Invoke(_visibleKpiGroupOrder.ToList());
                eventArgs.Effects = DragEffects.Move;
            }

            private void RefreshKpiGroupPanels()
            {
                _kpiGroupsPanel.Items.Clear();
                foreach (string name in _visibleKpiGroupOrder)
                    if (_kpiGroupPanels.TryGetValue(name, out Control panel))
                        _kpiGroupsPanel.Items.Add(panel);
            }

            private void UpdateExistingKpiControls(
                IList<WasperKpi> incomingItems,
                HashSet<string> disabledBlocks,
                IReadOnlyDictionary<Guid, bool> sourceStates)
            {
                Dictionary<string, WasperKpi> byKey = (incomingItems ?? Array.Empty<WasperKpi>())
                    .Where(item => item != null)
                    .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                foreach (KeyValuePair<string, List<WasperKpi>> pair in _kpiItemRecords)
                {
                    List<CheckBox> boxes = _kpiItemCheckBoxes[pair.Key];
                    for (int index = 0; index < pair.Value.Count; index++)
                    {
                        if (!byKey.TryGetValue(pair.Value[index].Key, out WasperKpi current))
                            continue;
                        pair.Value[index] = current;
                        boxes[index].Text = KpiCheckBoxText(current);
                    }
                }

                foreach (KeyValuePair<string, CheckBox> pair in _kpiGroupToggles)
                    pair.Value.Checked = !disabledBlocks.Contains(pair.Key);

                foreach (KeyValuePair<Guid, List<Button>> pair in _kpiSourceButtons)
                {
                    bool enabled = sourceStates == null ||
                        !sourceStates.TryGetValue(pair.Key, out bool sourceEnabled) ||
                        sourceEnabled;
                    string sourceName = _kpiSourceNames.TryGetValue(pair.Key, out string name)
                        ? name
                        : "KPI source";
                    foreach (Button button in pair.Value)
                    {
                        button.Tag = enabled;
                        button.Text = enabled ? "Disable: " + sourceName : "Enable: " + sourceName;
                    }
                }
            }

            private static TableCell LabelCell(string text) =>
                new TableCell(new Label { Text = text, VerticalAlignment = VerticalAlignment.Center }, false);

            private Control CreateExportTab()
            {
                _exportBrowse = CommandButton("Browse...");
                _exportResetFiles = CommandButton("Reset files");
                _exportWriteWithRunButton = CommandButton("Write with run: Yes", 130);
                _linkVisualizationButton = CommandButton("Link vis component", 150);
                _unlinkVisualizationButton = CommandButton("Unlink");
                _refreshViewportsButton = CommandButton("Refresh views", 110);
                _previewSnapshotButton = CommandButton("Refresh preview", 110);
                _snapshotViewport.Width = 165;
                _snapshotWidth.Width = 90;
                _snapshotHeight.Width = 90;
                _snapshotDpi.Width = 90;
                _snapshotWait.Width = 90;
                _linkedVisualizationStatus.Width = 280;

                _exportFileName.LostFocus += (_, _) => ExportSettingChanged();
                _exportFilePath.LostFocus += (_, _) => ExportSettingChanged();
                _exportFilePath.TextChanged += (_, _) =>
                {
                    if (!_updatingExportControls)
                        _exportFilePathShowingDefault = false;
                };
                _exportFormat.SelectedIndexChanged += (_, _) => ExportSettingChanged();
                _exportLayoutDropdown.SelectedIndexChanged += (_, _) =>
                {
                    if (!_updatingExportControls)
                        ExportLayoutChanged?.Invoke(SelectedExportLayout());
                };
                _exportBrowse.Click += (_, _) => BrowseExportFolder();
                _exportResetFiles.Click += (_, _) =>
                    ResetRequested?.Invoke(_exportFileName.Text, ExportFilePathValue(), SelectedExportFormat());
                _exportWriteWithRunButton.Click += (_, _) =>
                {
                    SetExportWriteWithRun(!_exportWriteWithRunOn);
                    if (!_updatingExportControls)
                        WriteWithRunChanged?.Invoke(_exportWriteWithRunOn);
                };

                var commandBar = new TableLayout
                {
                    Padding = new Padding(10, 7, 10, 3),
                    Spacing = new Size(6, 4),
                    Rows =
                    {
                        new TableRow(
                            LabelCell("Study name"),
                            new TableCell(_exportFileName, false),
                            LabelCell("Save path"),
                            new TableCell(_exportFilePath, true),
                            new TableCell(_exportBrowse, false),
                            LabelCell("Format"),
                            new TableCell(_exportFormat, false),
                            LabelCell("Layout"),
                            new TableCell(_exportLayoutDropdown, false),
                            new TableCell(_exportResetFiles, false),
                            new TableCell(_exportWriteWithRunButton, false))
                        { ScaleHeight = false }
                    }
                };

                var visualizationRow = new TableLayout
                {
                    Spacing = new Size(6, 0),
                    Rows =
                    {
                        new TableRow(
                            new TableCell(_linkVisualizationButton, false),
                            new TableCell(_unlinkVisualizationButton, false),
                            new TableCell(null, true))
                    }
                };
                var viewportRow = new TableLayout
                {
                    Spacing = new Size(6, 0),
                    Rows =
                    {
                        new TableRow(
                            new TableCell(_snapshotViewport, false),
                            new TableCell(_refreshViewportsButton, false),
                            new TableCell(null, true))
                    }
                };

                var settingsFields = new TableLayout
                {
                    Padding = new Padding(8, 4, 8, 4),
                    Spacing = new Size(8, 6),
                    Rows =
                    {
                        new TableRow(LabelCell(string.Empty), new TableCell(_snapshotEnabled, false))
                            { ScaleHeight = false },
                        new TableRow(LabelCell("Visualization"), new TableCell(visualizationRow, false))
                            { ScaleHeight = false },
                        new TableRow(LabelCell(string.Empty), new TableCell(_linkedVisualizationStatus, false))
                            { ScaleHeight = false },
                        new TableRow(LabelCell("Viewport"), new TableCell(viewportRow, false))
                            { ScaleHeight = false },
                        new TableRow(LabelCell("Width (px)"), new TableCell(_snapshotWidth, false))
                            { ScaleHeight = false },
                        new TableRow(LabelCell("Height (px)"), new TableCell(_snapshotHeight, false))
                            { ScaleHeight = false },
                        new TableRow(LabelCell("DPI"), new TableCell(_snapshotDpi, false))
                            { ScaleHeight = false },
                        new TableRow(LabelCell("Wait (ms)"), new TableCell(_snapshotWait, false))
                            { ScaleHeight = false },
                        new TableRow(LabelCell(string.Empty), new TableCell(_previewSnapshotButton, false))
                            { ScaleHeight = false },
                        new TableRow(LabelCell(string.Empty), new TableCell(new Label
                        {
                            Text = "To link a visualization, select one component on the Grasshopper " +
                                "canvas and then click 'Link vis component'. The linked component " +
                                "supplies a readiness and visibility check. Captured PNG files are " +
                                "saved in the run's Snapshots folder. They reuse G-code base names " +
                                "when G-code exists, or the sample name when no G-code is connected.",
                            Wrap = WrapMode.Word,
                            Width = 280
                        }, false)) { ScaleHeight = false },
                        new TableRow(LabelCell(string.Empty), new TableCell(null, true))
                            { ScaleHeight = true }
                    }
                };
                var settingsScroll = new Scrollable
                {
                    Content = settingsFields,
                    ExpandContentWidth = false,
                    ExpandContentHeight = true,
                    MinimumSize = new Size(360, 360)
                };

                var previewPanel = new TableLayout
                {
                    Padding = new Padding(8),
                    Rows =
                    {
                        new TableRow(new TableCell(_snapshotPreview, true)) { ScaleHeight = true },
                        new TableRow(new TableCell(_snapshotStatus, true)) { ScaleHeight = false }
                    }
                };

                var snapshotBody = new Splitter
                {
                    Orientation = Orientation.Horizontal,
                    FixedPanel = SplitterFixedPanel.Panel1,
                    Position = 410,
                    Panel1MinimumSize = 360,
                    Panel2MinimumSize = 360,
                    Panel1 = settingsScroll,
                    Panel2 = previewPanel
                };
                var snapshotGroup = new GroupBox
                {
                    Text = "Iteration viewport snapshots",
                    Padding = new Padding(8),
                    Content = snapshotBody
                };

                _snapshotEnabled.CheckedChanged += (_, _) => SnapshotSettingChanged();
                _snapshotViewport.SelectedIndexChanged += (_, _) =>
                {
                    if (_updatingSnapshotControls)
                        return;
                    SnapshotSettingsChanged?.Invoke(ReadSnapshotSettings());
                    RefreshSnapshotPreview();
                };
                _snapshotWidth.ValueChanged += (_, _) => SnapshotSettingChanged();
                _snapshotHeight.ValueChanged += (_, _) => SnapshotSettingChanged();
                _snapshotDpi.ValueChanged += (_, _) => SnapshotSettingChanged();
                _snapshotWait.ValueChanged += (_, _) => SnapshotSettingChanged();
                _refreshViewportsButton.Click += (_, _) => RefreshViewportChoices();
                _previewSnapshotButton.Click += (_, _) => RefreshSnapshotPreview();
                _linkVisualizationButton.Click += (_, _) => LinkVisualizationRequested?.Invoke();
                _unlinkVisualizationButton.Click += (_, _) => UnlinkVisualizationRequested?.Invoke();

                RefreshViewportChoices();

                return new TableLayout
                {
                    Rows =
                    {
                        new TableRow(new TableCell(commandBar, true)) { ScaleHeight = false },
                        new TableRow(new TableCell(snapshotGroup, true)) { ScaleHeight = true },
                        new TableRow(new TableCell(_exportStatus, true)) { ScaleHeight = false }
                    }
                };
            }

            private void SetExportWriteWithRun(bool enabled)
            {
                _exportWriteWithRunOn = enabled;
                _exportWriteWithRunButton.Text = enabled ? "Write with run: Yes" : "Write with run: No";
            }

            private void ExportSettingChanged()
            {
                if (_updatingExportControls)
                    return;
                ExportSettingsChanged?.Invoke(_exportFileName.Text, ExportFilePathValue(), SelectedExportFormat());
            }

            private string ExportFilePathValue() =>
                _exportFilePathShowingDefault ? string.Empty : _exportFilePath.Text;

            private string SelectedExportFormat() =>
                _exportFormat.SelectedIndex >= 0 && _exportFormat.SelectedIndex < ExportFormats.Length
                    ? ExportFormats[_exportFormat.SelectedIndex]
                    : "All";

            private string SelectedExportLayout() =>
                _exportLayoutDropdown.SelectedIndex >= 0 &&
                    _exportLayoutDropdown.SelectedIndex < ExportLayouts.Length
                    ? ExportLayouts[_exportLayoutDropdown.SelectedIndex]
                    : "Iterations in rows";

            /// <summary>
            /// Unverified: Eto.Forms.SelectFolderDialog has no in-repo precedent. Fully qualified
            /// like the G-code tab's SaveFileDialog (see SaveGcodeClicked) since Rhino.UI likely
            /// declares a same-named type too -- not confirmed by a build yet, but pre-empting the
            /// same CS0104 ambiguity the maintainer's build already caught once.
            /// </summary>
            private void BrowseExportFolder()
            {
                using var dialog = new Eto.Forms.SelectFolderDialog
                {
                    Title = "Select the KPI export folder"
                };
                if (System.IO.Directory.Exists(_exportFilePath.Text))
                    dialog.Directory = _exportFilePath.Text;
                if (dialog.ShowDialog(Sm01DialogOwner()) != DialogResult.Ok)
                    return;
                _exportFilePathShowingDefault = false;
                _exportFilePath.Text = dialog.Directory;
                ExportSettingChanged();
            }

            public void UpdateExportControls(
                string fileName,
                string filePath,
                bool filePathIsDefault,
                string format,
                string layout,
                bool fileNameConnected,
                string status)
            {
                _updatingExportControls = true;
                try
                {
                    if (_exportFileName.Text != (fileName ?? string.Empty))
                        _exportFileName.Text = fileName ?? string.Empty;
                    if (_exportFilePath.Text != (filePath ?? string.Empty))
                        _exportFilePath.Text = filePath ?? string.Empty;
                    _exportFilePathShowingDefault = filePathIsDefault;
                    string normalized = NormalizeFormat(format);
                    int formatIndex = Array.IndexOf(ExportFormats, normalized);
                    _exportFormat.SelectedIndex = formatIndex >= 0 ? formatIndex : ExportFormats.Length - 1;
                    string normalizedLayout = NormalizeExportLayout(layout);
                    int layoutIndex = Array.IndexOf(ExportLayouts, normalizedLayout);
                    _exportLayoutDropdown.SelectedIndex = layoutIndex >= 0 ? layoutIndex : 0;
                    _exportFileName.Enabled = !fileNameConnected;
                    _exportStatus.Text = status ?? string.Empty;
                }
                finally
                {
                    _updatingExportControls = false;
                }
            }

            public void SetWriteStatus(string status)
            {
                _exportStatus.Text = status ?? string.Empty;
            }

            public void UpdateSnapshotSettings(WasperSnapshotSettings settings)
            {
                settings ??= new WasperSnapshotSettings();
                _updatingSnapshotControls = true;
                try
                {
                    _snapshotEnabled.Checked = settings.Enabled;
                    _snapshotWidth.Value = ClampSnapshotValue(settings.Width, _snapshotWidth);
                    _snapshotHeight.Value = ClampSnapshotValue(settings.Height, _snapshotHeight);
                    _snapshotDpi.Value = ClampSnapshotValue(settings.Dpi, _snapshotDpi);
                    _snapshotWait.Value = ClampSnapshotValue(settings.WaitMilliseconds, _snapshotWait);
                    _linkedVisualizationId = settings.VisualizationComponentId;
                    _linkedVisualizationName = settings.VisualizationComponentName?.Trim() ?? string.Empty;
                    _linkedVisualizationStatus.Text = _linkedVisualizationId == Guid.Empty
                        ? "No visualization component linked."
                        : "Linked: " + (string.IsNullOrWhiteSpace(_linkedVisualizationName)
                            ? _linkedVisualizationId.ToString("D")
                            : _linkedVisualizationName);
                    _unlinkVisualizationButton.Enabled = _linkedVisualizationId != Guid.Empty;
                    RefreshViewportChoices(settings.ViewportName);
                }
                finally
                {
                    _updatingSnapshotControls = false;
                }
                if (_snapshotPreview.Image == null)
                    RefreshSnapshotPreview();
            }

            private static double ClampSnapshotValue(int value, NumericStepper control) =>
                Math.Max(control.MinValue, Math.Min(control.MaxValue, value));

            private void RefreshViewportChoices(string requested = null)
            {
                string selected = requested;
                if (selected == null)
                {
                    string current = _snapshotViewport.SelectedIndex >= 0 &&
                        _snapshotViewport.SelectedIndex < _snapshotViewportNames.Count
                        ? _snapshotViewportNames[_snapshotViewport.SelectedIndex]
                        : null;
                    selected = string.Equals(
                        current,
                        WasperViewportCapture.ActiveViewportLabel,
                        StringComparison.OrdinalIgnoreCase)
                        ? string.Empty
                        : current;
                }

                bool previousUpdate = _updatingSnapshotControls;
                _updatingSnapshotControls = true;
                try
                {
                    _snapshotViewportNames.Clear();
                    _snapshotViewportNames.Add(WasperViewportCapture.ActiveViewportLabel);
                    _snapshotViewportNames.AddRange(WasperViewportCapture.ViewportNames());
                    _snapshotViewport.DataStore = _snapshotViewportNames;
                    string target = string.IsNullOrWhiteSpace(selected)
                        ? WasperViewportCapture.ActiveViewportLabel
                        : selected.Trim();
                    int index = _snapshotViewportNames.FindIndex(
                        name => string.Equals(name, target, StringComparison.OrdinalIgnoreCase));
                    _snapshotViewport.SelectedIndex = index >= 0 ? index : 0;
                }
                finally
                {
                    _updatingSnapshotControls = previousUpdate;
                }
            }

            private void SnapshotSettingChanged()
            {
                if (_updatingSnapshotControls)
                    return;
                SnapshotSettingsChanged?.Invoke(ReadSnapshotSettings());
            }

            private WasperSnapshotSettings ReadSnapshotSettings()
            {
                string viewport = _snapshotViewport.SelectedIndex >= 0 &&
                    _snapshotViewport.SelectedIndex < _snapshotViewportNames.Count
                    ? _snapshotViewportNames[_snapshotViewport.SelectedIndex]
                    : string.Empty;
                if (string.Equals(
                    viewport,
                    WasperViewportCapture.ActiveViewportLabel,
                    StringComparison.OrdinalIgnoreCase))
                {
                    viewport = string.Empty;
                }
                return new WasperSnapshotSettings
                {
                    Enabled = _snapshotEnabled.Checked == true,
                    ViewportName = viewport ?? string.Empty,
                    Width = (int)_snapshotWidth.Value,
                    Height = (int)_snapshotHeight.Value,
                    Dpi = (int)_snapshotDpi.Value,
                    WaitMilliseconds = (int)_snapshotWait.Value,
                    VisualizationComponentId = _linkedVisualizationId,
                    VisualizationComponentName = _linkedVisualizationName
                };
            }

            private void RefreshSnapshotPreview()
            {
                try
                {
                    WasperSnapshotSettings requested = ReadSnapshotSettings();
                    double scale = Math.Min(
                        1.0,
                        Math.Min(1200.0 / requested.Width, 800.0 / requested.Height));
                    var previewSettings = new WasperSnapshotSettings
                    {
                        ViewportName = requested.ViewportName,
                        Width = Math.Max(64, (int)Math.Round(requested.Width * scale)),
                        Height = Math.Max(64, (int)Math.Round(requested.Height * scale)),
                        Dpi = requested.Dpi,
                        WaitMilliseconds = 0
                    };
                    System.Drawing.Bitmap preview = WasperViewportCapture.Capture(previewSettings, false);
                    Image converted = ConvertToEtoImage(preview);
                    Image previous = _snapshotPreview.Image;
                    _snapshotPreview.Image = converted;
                    preview?.Dispose();
                    previous?.Dispose();
                    string view = string.IsNullOrWhiteSpace(requested.ViewportName)
                        ? "active viewport"
                        : requested.ViewportName;
                    _snapshotStatus.Text =
                        $"Frame preview: {view} - {requested.Width} x {requested.Height}px - {requested.Dpi} DPI";
                }
                catch (Exception exception)
                {
                    _snapshotStatus.Text = "Preview unavailable: " + exception.Message;
                }
            }

            /// <summary>
            /// Explicit Eto/System.Drawing conversion boundary (AGENTS.md cross-platform UI policy):
            /// WasperViewportCapture.Capture returns a System.Drawing.Bitmap; Eto.Forms.ImageView needs
            /// an Eto.Drawing.Image. Round-trips through a PNG-encoded MemoryStream rather than any
            /// direct pixel-buffer interop -- simplest option with no unsafe/interop code, at the cost
            /// of a PNG encode/decode per preview refresh (previews are refreshed on demand, not per
            /// frame, so this is not a hot path).
            /// </summary>
            private static Image ConvertToEtoImage(System.Drawing.Image image)
            {
                if (image == null)
                    return null;
                using var stream = new System.IO.MemoryStream();
                image.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                stream.Position = 0;
                return new Bitmap(stream);
            }

            private Control CreateSampleNameTab()
            {
                _sampleNameComposed.Columns.Add(new GridColumn
                {
                    AutoSize = true,
                    DataCell = new TextBoxCell
                    {
                        Binding = new PropertyBinding<string>("Label")
                    }
                });
                var addButton = CommandButton("Add >>", 90);
                _sampleNameMoveUp = CommandButton("Move up", 90);
                _sampleNameMoveDown = CommandButton("Move down", 90);
                _sampleNameRemove = CommandButton("Remove", 90);
                _sampleNameEditText = CommandButton("Edit text", 90);
                _sampleNameEditText.Enabled = false;
                var restoreDefault = CommandButton("Restore default", 120);
                _sampleNameInsertText = CommandButton("Insert text", 100);

                addButton.Click += (_, _) =>
                {
                    int index = _sampleNameAvailable.SelectedIndex;
                    List<SampleNamePropertyOption> available = AvailableSampleNameOptions();
                    if (index < 0 || index >= available.Count)
                        return;
                    SampleNamePropertyOption chosen = available[index];
                    List<string> tokens = ComposedSampleNameTokens();
                    if (tokens.Contains(chosen.Key, StringComparer.OrdinalIgnoreCase))
                        return;
                    int insertAt = _sampleNameComposed.SelectedRow >= 0
                        ? _sampleNameComposed.SelectedRow + 1
                        : tokens.Count;
                    tokens.Insert(Math.Max(0, Math.Min(insertAt, tokens.Count)), chosen.Key);
                    EndEditSampleNameText();
                    SampleNameTemplateChanged?.Invoke(tokens);
                };

                _sampleNameRemove.Click += (_, _) =>
                {
                    if (_sampleNameComposed.SelectedRow >= 0)
                        RemoveComposedSampleNameToken(_sampleNameComposed.SelectedRow);
                };
                _sampleNameMoveUp.Click += (_, _) => MoveComposedSampleNameToken(-1);
                _sampleNameMoveDown.Click += (_, _) => MoveComposedSampleNameToken(1);
                _sampleNameEditText.Click += (_, _) => EditSelectedSampleNameText();
                restoreDefault.Click += (_, _) =>
                {
                    EndEditSampleNameText();
                    SampleNameTemplateChanged?.Invoke(Enumerable.Empty<string>());
                };
                _sampleNameInsertText.Click += SampleNameInsertOrUpdateText;

                _sampleNameComposed.SelectedRowsChanged += (_, _) =>
                {
                    if (_updatingSampleNameComposer)
                        return;
                    List<string> tokens = ComposedSampleNameTokens();
                    int index = _sampleNameComposed.SelectedRow;
                    bool canEditText = index >= 0 && index < tokens.Count &&
                        IsSampleNameTextToken(tokens[index]);
                    _sampleNameEditText.Enabled = canEditText;
                    if (canEditText)
                        BeginEditSampleNameText(index, tokens[index]);
                    else if (_sampleNameEditingTextIndex >= 0)
                        EndEditSampleNameText();
                };
                _sampleNameComposed.MouseDoubleClick += (_, _) => EditSelectedSampleNameText();
                _sampleNameComposed.AllowDrop = true;
                _sampleNameComposed.MouseDown += SampleNameDragMouseDown;
                _sampleNameComposed.MouseMove += SampleNameDragMouseMove;
                _sampleNameComposed.MouseUp += (_, _) => ResetSampleNameDrag();
                _sampleNameComposed.DragEnter += SampleNameDragOver;
                _sampleNameComposed.DragOver += SampleNameDragOver;
                _sampleNameComposed.DragDrop += SampleNameDragDrop;

                var leftGroup = new GroupBox
                {
                    Text = "Available tokens",
                    Padding = new Padding(8),
                    Content = new TableLayout
                    {
                        Spacing = new Size(4, 4),
                        Rows =
                        {
                            new TableRow(new TableCell(
                                new Scrollable { Content = _sampleNameGroupFilterPanel, Height = 90 }))
                            { ScaleHeight = false },
                            new TableRow(new TableCell(_sampleNameAvailable, true)) { ScaleHeight = true },
                            new TableRow(new TableCell(addButton)) { ScaleHeight = false }
                        }
                    }
                };

                var rightToolbar = new TableLayout
                {
                    Spacing = new Size(4, 4),
                    Rows =
                    {
                        new TableRow(
                            new TableCell(_sampleNameMoveUp, false),
                            new TableCell(_sampleNameMoveDown, false),
                            new TableCell(_sampleNameEditText, false),
                            new TableCell(_sampleNameRemove, false),
                            new TableCell(restoreDefault, false),
                            new TableCell(null, true))
                    }
                };

                var textRow = new TableLayout
                {
                    Spacing = new Size(4, 4),
                    Rows =
                    {
                        new TableRow(
                            LabelCell("Text:"),
                            new TableCell(_sampleNameTextInput, true),
                            new TableCell(_sampleNameInsertText, false))
                    }
                };

                var rightGroup = new GroupBox
                {
                    Text = "Composed sample name",
                    Padding = new Padding(8),
                    Content = new TableLayout
                    {
                        Spacing = new Size(4, 4),
                        Rows =
                        {
                            new TableRow(new TableCell(_sampleNameComposed, true)) { ScaleHeight = true },
                            new TableRow(new TableCell(rightToolbar)) { ScaleHeight = false },
                            new TableRow(new TableCell(textRow)) { ScaleHeight = false }
                        }
                    }
                };

                var hint = new Label
                {
                    Wrap = WrapMode.Word,
                    Text = "Select a token on the left and click \"Add >>\", then use Move up/down " +
                        "or drag composed items to reorder them. Select or double-click an inserted " +
                        "text segment to edit it below. Joined with underscores; X/Y/Z cell counts " +
                        "are joined with dots instead."
                };

                var previewGroup = new GroupBox
                {
                    Text = "Sample name preview",
                    Padding = new Padding(8),
                    Content = _sampleNamePreview
                };

                var columns = new TableLayout
                {
                    Spacing = new Size(10, 0),
                    Rows = { new TableRow(new TableCell(leftGroup, true), new TableCell(rightGroup, true)) }
                };

                return new TableLayout
                {
                    Padding = new Padding(10),
                    Spacing = new Size(0, 6),
                    Rows =
                    {
                        new TableRow(new TableCell(columns, true)) { ScaleHeight = true },
                        new TableRow(new TableCell(hint)) { ScaleHeight = false },
                        new TableRow(new TableCell(previewGroup)) { ScaleHeight = false }
                    }
                };
            }

            /// <summary>
            /// Refreshes both the left (available, filtered by category) and right (composed,
            /// ordered) lists from the component's current option set and saved token order. Called
            /// on every UpdateStudyWindow, mirroring how the rest of the manager's tabs stay a pure
            /// view of component state rather than caching their own copy.
            /// </summary>
            public void UpdateSampleNameComposer(
                IEnumerable<SampleNamePropertyOption> options,
                IEnumerable<string> selectedTokens,
                bool inputConnected,
                string inputValue,
                string preview)
            {
                _sampleNameLastOptions = (options ?? Enumerable.Empty<SampleNamePropertyOption>())
                    .Where(option => option != null && !string.IsNullOrWhiteSpace(option.Key))
                    .ToList();
                _sampleNameOptionsByKey = _sampleNameLastOptions
                    .GroupBy(option => option.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                _sampleNameLastTokens = (selectedTokens ?? Enumerable.Empty<string>())
                    .Where(token => !string.IsNullOrWhiteSpace(token))
                    .ToList();

                // An in-progress text edit is only still valid if the slot it points at still holds
                // a text token -- e.g. a background refresh mid-typing is fine, but if something
                // else changed that position (or it no longer exists) the edit buffer would silently
                // overwrite the wrong item, so cancel it instead.
                if (_sampleNameEditingTextIndex >= 0 &&
                    (_sampleNameEditingTextIndex >= _sampleNameLastTokens.Count ||
                     !IsSampleNameTextToken(_sampleNameLastTokens[_sampleNameEditingTextIndex])))
                {
                    EndEditSampleNameText();
                }

                _updatingSampleNameComposer = true;
                try
                {
                    RefreshSampleNameGroupFilter();
                    RefreshSampleNameAvailableList();

                    List<SampleNamePropertyOption> composedDisplay = _sampleNameLastTokens
                        .Select(ComposedSampleNameDisplayOption)
                        .ToList();
                    _sampleNameComposed.DataStore = composedDisplay;

                    int restoreIndex = _sampleNamePendingSelectIndex >= 0
                        ? _sampleNamePendingSelectIndex
                        : _sampleNameEditingTextIndex;
                    if (restoreIndex >= 0 && restoreIndex < composedDisplay.Count)
                        _sampleNameComposed.SelectedRow = restoreIndex;
                    _sampleNamePendingSelectIndex = -1;
                }
                finally
                {
                    _updatingSampleNameComposer = false;
                }

                UpdateSampleNameTextEditState();

                _sampleNameComposed.Enabled = !inputConnected || string.IsNullOrWhiteSpace(inputValue);
                _sampleNameAvailable.Enabled = _sampleNameComposed.Enabled;
                _sampleNamePreview.Text = inputConnected && !string.IsNullOrWhiteSpace(inputValue)
                    ? "Input override: " + preview
                    : inputConnected
                        ? "s_name is connected but empty; composer preview: " + preview
                        : "Preview: " + preview;
            }

            private static string SampleNameGroupOrOther(string group) =>
                string.IsNullOrWhiteSpace(group) ? "Other" : group;

            private void RefreshSampleNameGroupFilter()
            {
                // Distinct() keeps first-seen order, matching the WinForms version's reliance on
                // SampleNameOptions()'s already-sorted source list -- no extra sort needed here.
                List<string> groups = _sampleNameLastOptions
                    .Select(option => SampleNameGroupOrOther(option.Group))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // Rebuilt from scratch each call, same as the KPI tab's group boxes -- the group set
                // rarely changes shape once a study is loaded, so this is not a hot path.
                _sampleNameGroupFilterPanel.Items.Clear();
                _sampleNameGroupToggles.Clear();
                foreach (string group in groups)
                {
                    var toggle = new CheckBox
                    {
                        Text = group,
                        Checked = !_sampleNameHiddenGroups.Contains(group)
                    };
                    toggle.CheckedChanged += (_, _) =>
                    {
                        if (_updatingSampleNameComposer)
                            return;
                        if (toggle.Checked == true)
                            _sampleNameHiddenGroups.Remove(group);
                        else
                            _sampleNameHiddenGroups.Add(group);
                        RefreshSampleNameAvailableList();
                    };
                    _sampleNameGroupToggles[group] = toggle;
                    _sampleNameGroupFilterPanel.Items.Add(toggle);
                }
            }

            private List<SampleNamePropertyOption> AvailableSampleNameOptions() =>
                (_sampleNameAvailable.DataStore ?? Enumerable.Empty<object>())
                    .OfType<SampleNamePropertyOption>()
                    .ToList();

            private void RefreshSampleNameAvailableList()
            {
                var used = new HashSet<string>(_sampleNameLastTokens, StringComparer.OrdinalIgnoreCase);
                List<SampleNamePropertyOption> available = _sampleNameLastOptions
                    .Where(option => !_sampleNameHiddenGroups.Contains(SampleNameGroupOrOther(option.Group)))
                    .Where(option => !used.Contains(option.Key))
                    .ToList();
                _sampleNameAvailable.DataStore = available;
            }

            private List<string> ComposedSampleNameTokens() =>
                (_sampleNameComposed.DataStore ?? Enumerable.Empty<object>())
                    .OfType<SampleNamePropertyOption>()
                    .Select(option => option.Key)
                    .ToList();

            /// <summary>
            /// Builds the composed list's display item for a token. Checked in this order because a
            /// free-text token is never expected to be found in the catalog (it is user-authored,
            /// not one of SampleNameOptions()'s entries) -- looking it up there first would always
            /// miss and wrongly fall through to the "not currently available" case below, which is
            /// reserved for a catalog reference that used to resolve (e.g. an unlinked slider or a
            /// since-disabled KPI producer) and no longer does.
            /// </summary>
            private SampleNamePropertyOption ComposedSampleNameDisplayOption(string token)
            {
                if (IsSampleNameTextToken(token))
                {
                    return new SampleNamePropertyOption
                    {
                        Key = token,
                        Label = "\"" + token.Substring(SampleNameTextPrefix.Length) + "\"",
                        Group = "Text"
                    };
                }
                if (_sampleNameOptionsByKey.TryGetValue(token, out SampleNamePropertyOption match))
                    return match;
                return new SampleNamePropertyOption
                {
                    Key = token,
                    Label = token + " (not currently available)",
                    Group = "Unavailable"
                };
            }

            private void RemoveComposedSampleNameToken(int index)
            {
                List<string> tokens = ComposedSampleNameTokens();
                if (index < 0 || index >= tokens.Count)
                    return;
                if (index == _sampleNameEditingTextIndex)
                    EndEditSampleNameText();
                tokens.RemoveAt(index);
                SampleNameTemplateChanged?.Invoke(tokens);
            }

            /// <summary>
            /// Swaps the selected composed item with its neighbor in the given direction (-1 up, +1
            /// down) and remembers the moved item's new position so it stays selected across the
            /// UpdateSampleNameComposer rebuild the resulting solve triggers -- replaces the WinForms
            /// version's drag-to-reorder with explicit buttons (see class doc / tab header comment).
            /// </summary>
            private void MoveComposedSampleNameToken(int delta)
            {
                List<string> tokens = ComposedSampleNameTokens();
                int index = _sampleNameComposed.SelectedRow;
                int target = index + delta;
                if (index < 0 || index >= tokens.Count || target < 0 || target >= tokens.Count)
                    return;
                if (index == _sampleNameEditingTextIndex)
                    EndEditSampleNameText();
                string moved = tokens[index];
                tokens.RemoveAt(index);
                tokens.Insert(target, moved);
                _sampleNamePendingSelectIndex = target;
                SampleNameTemplateChanged?.Invoke(tokens);
            }

            private void EditSelectedSampleNameText()
            {
                List<string> tokens = ComposedSampleNameTokens();
                int index = _sampleNameComposed.SelectedRow;
                if (index < 0 || index >= tokens.Count || !IsSampleNameTextToken(tokens[index]))
                    return;
                BeginEditSampleNameText(index, tokens[index]);
                _sampleNameTextInput.Focus();
            }

            private void UpdateSampleNameTextEditState()
            {
                if (_sampleNameEditText == null)
                    return;
                List<string> tokens = ComposedSampleNameTokens();
                int index = _sampleNameComposed.SelectedRow;
                _sampleNameEditText.Enabled = index >= 0 && index < tokens.Count &&
                    IsSampleNameTextToken(tokens[index]);
            }

            private void SampleNameDragMouseDown(object sender, MouseEventArgs eventArgs)
            {
                if (eventArgs.Buttons != MouseButtons.Primary)
                    return;
                GridCell cell = _sampleNameComposed.GetCellAt(eventArgs.Location);
                _sampleNameDragIndex = cell?.RowIndex ?? -1;
                if (_sampleNameDragIndex >= 0)
                    _sampleNameComposed.SelectedRow = _sampleNameDragIndex;
                _sampleNameDragStart = eventArgs.Location;
                _sampleNameDragStarted = false;
            }

            private void SampleNameDragMouseMove(object sender, MouseEventArgs eventArgs)
            {
                if (_sampleNameDragIndex < 0 || _sampleNameDragStarted ||
                    (eventArgs.Buttons & MouseButtons.Primary) != MouseButtons.Primary)
                {
                    return;
                }
                if (Math.Abs(eventArgs.Location.X - _sampleNameDragStart.X) < 4f &&
                    Math.Abs(eventArgs.Location.Y - _sampleNameDragStart.Y) < 4f)
                {
                    return;
                }

                _sampleNameDragStarted = true;
                using var dragData = new DataObject();
                dragData.SetString(
                    _sampleNameDragIndex.ToString(CultureInfo.InvariantCulture),
                    SampleNameDragDataType);
                try
                {
                    _sampleNameComposed.DoDragDrop(dragData, DragEffects.Move);
                }
                finally
                {
                    ResetSampleNameDrag();
                }
            }

            private void ResetSampleNameDrag()
            {
                _sampleNameDragIndex = -1;
                _sampleNameDragStarted = false;
            }

            private static bool TryGetSampleNameDragIndex(
                DragEventArgs eventArgs,
                out int sourceIndex)
            {
                sourceIndex = -1;
                string packed = eventArgs?.Data?.GetString(SampleNameDragDataType);
                return int.TryParse(
                    packed,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out sourceIndex);
            }

            private void SampleNameDragOver(object sender, DragEventArgs eventArgs)
            {
                if (!TryGetSampleNameDragIndex(eventArgs, out _))
                {
                    eventArgs.Effects = DragEffects.None;
                    return;
                }
                GridCell target = _sampleNameComposed.GetCellAt(eventArgs.Location);
                if (target != null && target.RowIndex >= 0)
                    _sampleNameComposed.SelectedRow = target.RowIndex;
                eventArgs.Effects = DragEffects.Move;
            }

            private void SampleNameDragDrop(object sender, DragEventArgs eventArgs)
            {
                List<string> tokens = ComposedSampleNameTokens();
                if (!TryGetSampleNameDragIndex(eventArgs, out int sourceIndex) ||
                    sourceIndex < 0 || sourceIndex >= tokens.Count)
                {
                    eventArgs.Effects = DragEffects.None;
                    return;
                }

                GridCell target = _sampleNameComposed.GetCellAt(eventArgs.Location);
                int insertionIndex = target?.RowIndex ?? tokens.Count;

                string moved = tokens[sourceIndex];
                tokens.RemoveAt(sourceIndex);
                if (sourceIndex < insertionIndex)
                    insertionIndex--;
                insertionIndex = Math.Max(0, Math.Min(tokens.Count, insertionIndex));
                tokens.Insert(insertionIndex, moved);
                EndEditSampleNameText();
                _sampleNamePendingSelectIndex = insertionIndex;
                SampleNameTemplateChanged?.Invoke(tokens);
                eventArgs.Effects = DragEffects.Move;
            }

            /// <summary>
            /// Loads an existing free-text segment's literal content into the Text field for
            /// editing. "Insert text" becomes "Update text" while a segment is loaded; selecting a
            /// different composed item, removing this one, or an unrelated state change from a solve
            /// cancels the edit (see UpdateSampleNameComposer and the SelectedIndexChanged handler in
            /// CreateSampleNameTab).
            /// </summary>
            private void BeginEditSampleNameText(int index, string token)
            {
                _sampleNameEditingTextIndex = index;
                _sampleNameTextInput.Text = token.Substring(SampleNameTextPrefix.Length);
                _sampleNameInsertText.Text = "Update text";
            }

            private void EndEditSampleNameText()
            {
                _sampleNameEditingTextIndex = -1;
                if (_sampleNameInsertText != null)
                    _sampleNameInsertText.Text = "Insert text";
                _sampleNameTextInput.Text = string.Empty;
            }

            /// <summary>
            /// Inserts the Text field's content as a new literal segment (right after the selected
            /// composed item, or at the end if nothing is selected), or -- while editing an existing
            /// segment (see BeginEditSampleNameText) -- replaces that segment's content in place.
            /// </summary>
            private void SampleNameInsertOrUpdateText(object sender, EventArgs eventArgs)
            {
                string text = _sampleNameTextInput.Text ?? string.Empty;
                if (text.Length == 0)
                    return;
                string newToken = SampleNameTextPrefix + text;
                List<string> tokens = ComposedSampleNameTokens();
                if (_sampleNameEditingTextIndex >= 0 && _sampleNameEditingTextIndex < tokens.Count)
                {
                    tokens[_sampleNameEditingTextIndex] = newToken;
                }
                else
                {
                    int insertAt = _sampleNameComposed.SelectedRow >= 0
                        ? _sampleNameComposed.SelectedRow + 1
                        : tokens.Count;
                    tokens.Insert(Math.Max(0, Math.Min(insertAt, tokens.Count)), newToken);
                }
                EndEditSampleNameText();
                SampleNameTemplateChanged?.Invoke(tokens);
            }

            private Control CreateStudyTab()
            {
                _linkSelected = CommandButton("Link selected sliders", 150);
                _unlinkSelected = CommandButton("Unlink selected", 120);
                _restoreDefaults = CommandButton("Restore defaults", 120);
                _runStudy = CommandButton("Run study");
                _resumeStudy = CommandButton("Resume");
                _stopStudy = CommandButton("Stop");
                _captureIteration = CommandButton("Capture current", 110);
                _clearIterations = CommandButton("Clear iterations", 110);
                _saveStudy = CommandButton("Save Study");
                _refreshStudies = CommandButton("Refresh");
                _browseStudy = CommandButton("Browse...");
                _forgetStudy = CommandButton("Forget");
                _studyCompatibility = CommandButton("Compatibility...", 110);
                _loadStudy = CommandButton("Load as active", 110);
                _resumeSavedStudy = CommandButton("Resume selected", 120);

                var enabledColumn = new GridColumn
                {
                    HeaderText = "Use",
                    Width = 40,
                    Editable = true,
                    DataCell = new CheckBoxCell { Binding = new PropertyBinding<bool?>("Enabled") }
                };
                var nameColumn = new GridColumn
                {
                    HeaderText = "Parameter",
                    Width = 140,
                    DataCell = new TextBoxCell { Binding = new PropertyBinding<string>("Name") }
                };
                var currentColumn = new GridColumn
                {
                    HeaderText = "Current",
                    Width = 70,
                    DataCell = new TextBoxCell { Binding = new PropertyBinding<string>("Current") }
                };
                _studyMinColumn = new GridColumn
                {
                    HeaderText = "Study min",
                    Width = 70,
                    Editable = true,
                    DataCell = new TextBoxCell { Binding = new PropertyBinding<string>("Minimum") }
                };
                _studyMaxColumn = new GridColumn
                {
                    HeaderText = "Study max",
                    Width = 70,
                    Editable = true,
                    DataCell = new TextBoxCell { Binding = new PropertyBinding<string>("Maximum") }
                };
                _studySamplesColumn = new GridColumn
                {
                    HeaderText = "Samples",
                    Width = 60,
                    Editable = true,
                    DataCell = new TextBoxCell { Binding = new PropertyBinding<string>("Samples") }
                };
                _parameterGrid.Columns.Add(enabledColumn);
                _parameterGrid.Columns.Add(nameColumn);
                _parameterGrid.Columns.Add(currentColumn);
                _parameterGrid.Columns.Add(_studyMinColumn);
                _parameterGrid.Columns.Add(_studyMaxColumn);
                _parameterGrid.Columns.Add(_studySamplesColumn);
                _parameterGrid.DataStore = _studyParameterRows;

                _iterationGrid.Columns.Add(new GridColumn
                {
                    HeaderText = "ID",
                    Width = 40,
                    DataCell = new TextBoxCell { Binding = new PropertyBinding<string>("Index") }
                });
                _iterationGrid.Columns.Add(new GridColumn
                {
                    HeaderText = "Sample name",
                    Width = 110,
                    DataCell = new TextBoxCell { Binding = new PropertyBinding<string>("SampleName") }
                });
                _iterationGrid.Columns.Add(new GridColumn
                {
                    HeaderText = "Parameters",
                    Width = 160,
                    DataCell = new TextBoxCell { Binding = new PropertyBinding<string>("Parameters") }
                });
                _iterationGrid.Columns.Add(new GridColumn
                {
                    HeaderText = "KPIs",
                    Width = 45,
                    DataCell = new TextBoxCell { Binding = new PropertyBinding<string>("Kpis") }
                });
                _iterationGrid.Columns.Add(new GridColumn
                {
                    HeaderText = "Status",
                    Width = 60,
                    DataCell = new TextBoxCell { Binding = new PropertyBinding<string>("Status") }
                });
                _iterationGrid.Columns.Add(new GridColumn
                {
                    HeaderText = "Captured UTC",
                    Width = 110,
                    DataCell = new TextBoxCell { Binding = new PropertyBinding<string>("Captured") }
                });
                _iterationGrid.DataStore = _studyIterationRows;

                _linkSelected.Click += (_, _) => LinkSelectedSlidersRequested?.Invoke();
                _unlinkSelected.Click += (_, _) => UnlinkSlidersRequested?.Invoke(SelectedSliderIds());
                _restoreDefaults.Click += (_, _) =>
                    RestoreParameterDefaultsRequested?.Invoke(SelectedSliderIds());
                _runStudy.Click += RunStudyClicked;
                _resumeStudy.Click += (_, _) => ResumeStudyRequested?.Invoke(ReadParameterConfiguration());
                _stopStudy.Click += (_, _) => StopStudyRequested?.Invoke();
                _captureIteration.Click += (_, _) => CaptureIterationRequested?.Invoke();
                _clearIterations.Click += (_, _) => ClearIterationsRequested?.Invoke();
                _saveStudy.Click += (_, _) => SaveStudyRequested?.Invoke();
                _refreshStudies.Click += (_, _) => RefreshStudyLibraryRequested?.Invoke();
                _browseStudy.Click += (_, _) => BrowseStudyRequested?.Invoke();
                _forgetStudy.Click += (_, _) => ForgetPinnedStudyRequested?.Invoke(SelectedStudyEntry());
                _studyCompatibility.Click += ShowSelectedStudyCompatibility;
                _loadStudy.Click += (_, _) => LoadSavedStudyRequested?.Invoke(SelectedStudyEntry());
                _resumeSavedStudy.Click += (_, _) => ResumeSavedStudyRequested?.Invoke(SelectedStudyEntry());
                _iterationGrid.SelectionChanged += IterationGridSelectionChanged;
                _iterationGrid.MouseDown += IterationGridMouseDown;
                _studyLibrary.SelectedIndexChanged += (_, _) =>
                {
                    if (_updatingStudyLibrary)
                        return;
                    UpdateStudyLibrarySelection();
                    StudyLibrarySelectionChanged?.Invoke(SelectedStudyEntry());
                };

                var libraryBar = new TableLayout
                {
                    Spacing = new Size(4, 4),
                    Rows =
                    {
                        new TableRow(
                            LabelCell("Study"),
                            new TableCell(_studyLibrary, true),
                            new TableCell(_refreshStudies, false),
                            new TableCell(_browseStudy, false),
                            new TableCell(_forgetStudy, false),
                            new TableCell(_studyCompatibility, false),
                            new TableCell(_loadStudy, false),
                            new TableCell(_resumeSavedStudy, false))
                    }
                };
                var libraryGroup = new GroupBox
                {
                    Text = "Study Library -- auto-detected from the current save path, plus " +
                        "anything pinned via Browse...",
                    Padding = new Padding(8),
                    Content = new TableLayout
                    {
                        Spacing = new Size(0, 4),
                        Rows =
                        {
                            new TableRow(new TableCell(libraryBar)) { ScaleHeight = false },
                            new TableRow(new TableCell(_studyLibraryStatus)) { ScaleHeight = false }
                        }
                    }
                };

                var commandBar = new TableLayout
                {
                    Spacing = new Size(4, 4),
                    Rows =
                    {
                        new TableRow(
                            new TableCell(_linkSelected, false),
                            new TableCell(_unlinkSelected, false),
                            new TableCell(_restoreDefaults, false),
                            new TableCell(_runStudy, false),
                            new TableCell(_resumeStudy, false),
                            new TableCell(_stopStudy, false),
                            new TableCell(_captureIteration, false),
                            new TableCell(_clearIterations, false),
                            new TableCell(_saveStudy, false),
                            new TableCell(_studyProgress, false),
                            new TableCell(null, true))
                    }
                };

                var parameterGroup = new GroupBox
                {
                    Text = "Linked Number Sliders -- edit study minimum, maximum, and sample count",
                    Padding = new Padding(8),
                    Content = new TableLayout
                    {
                        Rows =
                        {
                            new TableRow(new TableCell(_parameterGrid, true)) { ScaleHeight = true },
                            new TableRow(new TableCell(_estimatedIterations)) { ScaleHeight = false }
                        }
                    }
                };
                var iterationGroup = new GroupBox
                {
                    Text = "Captured iterations",
                    Padding = new Padding(8),
                    Content = _iterationGrid
                };
                var grids = new Splitter
                {
                    Orientation = Orientation.Horizontal,
                    FixedPanel = SplitterFixedPanel.None,
                    RelativePosition = 0.5,
                    Panel1MinimumSize = 320,
                    Panel2MinimumSize = 320,
                    Panel1 = parameterGroup,
                    Panel2 = iterationGroup
                };

                var activityGroup = new GroupBox
                {
                    Text = "Study activity",
                    Padding = new Padding(8),
                    Content = new TableLayout
                    {
                        Rows =
                        {
                            new TableRow(new TableCell(_studyLog, true)) { ScaleHeight = true },
                            new TableRow(new TableCell(_studyStatus)) { ScaleHeight = false }
                        }
                    }
                };

                var studyWorkspace = new Splitter
                {
                    Orientation = Orientation.Vertical,
                    FixedPanel = SplitterFixedPanel.None,
                    RelativePosition = 0.78,
                    Panel1MinimumSize = 300,
                    Panel2MinimumSize = 110,
                    Panel1 = grids,
                    Panel2 = activityGroup
                };

                return new TableLayout
                {
                    Padding = new Padding(10),
                    Spacing = new Size(0, 6),
                    Rows =
                    {
                        new TableRow(new TableCell(libraryGroup)) { ScaleHeight = false },
                        new TableRow(new TableCell(commandBar)) { ScaleHeight = false },
                        new TableRow(new TableCell(studyWorkspace, true)) { ScaleHeight = true }
                    }
                };
            }

            public void UpdateStudyLibrary(IEnumerable<WasperStudyCatalogEntry> entries, string selectedPath)
            {
                _updatingStudyLibrary = true;
                try
                {
                    List<WasperStudyCatalogEntry> available =
                        (entries ?? Enumerable.Empty<WasperStudyCatalogEntry>()).ToList();
                    _studyLibrary.DataStore = available;
                    WasperStudyCatalogEntry selected = available.FirstOrDefault(entry =>
                        string.Equals(
                            entry.FilePath ?? string.Empty,
                            selectedPath ?? string.Empty,
                            StringComparison.OrdinalIgnoreCase));
                    _studyLibrary.SelectedIndex = selected != null
                        ? available.IndexOf(selected)
                        : (available.Count > 0 ? 0 : -1);
                }
                finally
                {
                    _updatingStudyLibrary = false;
                }
                UpdateStudyLibrarySelection();
            }

            private WasperStudyCatalogEntry SelectedStudyEntry()
            {
                List<WasperStudyCatalogEntry> entries = (_studyLibrary.DataStore ?? Enumerable.Empty<object>())
                    .OfType<WasperStudyCatalogEntry>()
                    .ToList();
                int index = _studyLibrary.SelectedIndex;
                return index >= 0 && index < entries.Count ? entries[index] : null;
            }

            private void UpdateStudyLibrarySelection()
            {
                WasperStudyCatalogEntry entry = SelectedStudyEntry();
                if (entry == null)
                {
                    _studyLibraryStatus.Text = "No studies found automatically. Use Browse... to " +
                        "pin one saved from a different, renamed, or relocated .gh file.";
                    _loadStudy.Enabled = false;
                    _resumeSavedStudy.Enabled = false;
                    _studyCompatibility.Enabled = false;
                    _forgetStudy.Enabled = false;
                    return;
                }
                string issue = entry.Issues?.FirstOrDefault();
                _studyLibraryStatus.Text = entry.IsCurrent
                    ? "Live study connected to this Sm01 component."
                    : $"{entry.StatusLabel}" +
                        (string.IsNullOrWhiteSpace(issue) ? string.Empty : " - " + issue);
                _loadStudy.Enabled = !entry.IsCurrent && entry.CanResume;
                _resumeSavedStudy.Enabled = !entry.IsCurrent && entry.CanResume;
                _studyCompatibility.Enabled = !entry.IsCurrent;
                _forgetStudy.Enabled = !entry.IsCurrent && entry.IsPinned;
            }

            private void ShowSelectedStudyCompatibility(object sender, EventArgs eventArgs)
            {
                WasperStudyCatalogEntry entry = SelectedStudyEntry();
                if (entry == null || entry.IsCurrent)
                    return;
                string details = entry.Issues == null || entry.Issues.Count == 0
                    ? "No compatibility issues were detected."
                    : string.Join(Environment.NewLine, entry.Issues.Select(issue => "- " + issue));
                // WinForms distinguished an Info vs. Warning icon by entry.CanResume; only a
                // warning-styled helper is available here (ShowSm01Warning, from Codex's
                // WASPer_Sm01Dialogs.cs), so both cases use it -- a minor cosmetic simplification.
                ShowSm01Warning(
                    $"Status: {entry.StatusLabel}{Environment.NewLine}{Environment.NewLine}{details}",
                    "Study compatibility");
            }

            private void RunStudyClicked(object sender, EventArgs eventArgs)
            {
                List<string> disabledSources = DisabledKpiSourceNames();
                if (disabledSources.Count > 0)
                {
                    string bullets = string.Join(
                        Environment.NewLine,
                        disabledSources.Select(name => "• " + name));
                    string message =
                        "The following KPI components are disabled:" +
                        Environment.NewLine + Environment.NewLine +
                        bullets +
                        Environment.NewLine + Environment.NewLine +
                        "Their KPIs will not be included in the study." +
                        Environment.NewLine + Environment.NewLine +
                        "Do you want to continue?";
                    AppendStudyLog(
                        "Run requested with disabled KPI components: " +
                        string.Join(", ", disabledSources) + ".");
                    if (!ConfirmSm01Warning(message, "Disabled KPI components"))
                    {
                        AppendStudyLog("Study start cancelled by the user.");
                        return;
                    }
                    AppendStudyLog("User confirmed that the study should continue.");
                }
                else
                {
                    AppendStudyLog("Run study requested. All KPI components are enabled.");
                }
                RunStudyRequested?.Invoke(ReadParameterConfiguration());
            }

            // Adapted from the WinForms _sourceToggles/_sourceToggleNames pair -- this class tracks
            // the same per-source enable state on the KPI tab's own _kpiSourceButtons/_kpiSourceNames
            // (see class doc), each button's Tag holding the current enabled bool (set in
            // RebuildKpiGroups / its Click handler).
            private List<string> DisabledKpiSourceNames()
            {
                return _kpiSourceButtons
                    .Where(pair => pair.Value.Any(button => !(button.Tag is bool enabled && enabled)))
                    .Select(pair => _kpiSourceNames.TryGetValue(pair.Key, out string name)
                        ? name
                        : pair.Key.ToString("D"))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            private void AppendStudyLog(string message)
            {
                if (string.IsNullOrWhiteSpace(message))
                    return;
                string line = $"[{DateTime.Now:HH:mm:ss}] {message.Trim()}";
                // No attempted Eto equivalent of RichTextBox.ScrollToCaret -- see class doc.
                _studyLog.Text = string.IsNullOrEmpty(_studyLog.Text)
                    ? line
                    : _studyLog.Text + Environment.NewLine + line;
            }

            private List<Guid> SelectedSliderIds()
            {
                return _parameterGrid.SelectedItems
                    .OfType<StudyParameterRow>()
                    .Select(row => row.SliderId)
                    .ToList();
            }

            private List<WasperStudyParameter> ReadParameterConfiguration()
            {
                // Unlike WinForms (which extracts values from DataGridViewRow.Cells),
                // _studyParameterRows is the GridView's own two-way-bound DataStore, so the rows
                // already hold whatever the user last typed/checked -- no cell walk needed.
                var parameters = new List<WasperStudyParameter>();
                foreach (StudyParameterRow row in _studyParameterRows)
                {
                    parameters.Add(new WasperStudyParameter
                    {
                        SliderId = row.SliderId,
                        Name = row.Name,
                        Enabled = row.Enabled == true,
                        Minimum = ParseNumber(row.Minimum),
                        Maximum = ParseNumber(row.Maximum),
                        Samples = Math.Max(1, ParseInteger(row.Samples, 3))
                    });
                }
                return parameters;
            }

            /// <summary>
            /// Live "how many iterations would this study run" preview under the parameter grid --
            /// see the WinForms version's identical-intent method for the Values()-based (not raw
            /// Samples) reasoning, unchanged here.
            /// </summary>
            private void UpdateEstimatedIterationCount()
            {
                List<WasperStudyParameter> parameters = ReadParameterConfiguration();
                List<WasperStudyParameter> enabled = parameters.Where(p => p.Enabled).ToList();
                if (enabled.Count == 0)
                {
                    _estimatedIterations.Text = "Estimated iterations: 0 (no parameters enabled)";
                    return;
                }

                const long displayCap = 2_000_000;
                long count = 1;
                foreach (WasperStudyParameter parameter in enabled)
                {
                    int values = Math.Max(1, parameter.Values().Count());
                    count = count > displayCap / Math.Max(1, values) ? displayCap + 1 : count * values;
                    if (count > displayCap)
                        break;
                }
                _estimatedIterations.Text = count > displayCap
                    ? $"Estimated iterations: {displayCap:N0}+ across {enabled.Count} enabled " +
                        "parameter(s) - consider reducing samples"
                    : $"Estimated iterations: {count:N0} across {enabled.Count} enabled parameter(s)";
            }

            private static double ParseNumber(string value)
            {
                return double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double number)
                    ? number
                    : 0.0;
            }

            private static int ParseInteger(string value, int fallback)
            {
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                    ? number
                    : fallback;
            }

            public void UpdateStudy(
                IEnumerable<WasperStudyParameter> parameters,
                IEnumerable<WasperStudyIteration> iterations,
                string status,
                double progress,
                bool running,
                bool viewingSavedStudy)
            {
                List<WasperStudyParameter> parameterList =
                    (parameters ?? Enumerable.Empty<WasperStudyParameter>()).ToList();
                List<WasperStudyIteration> iterationList =
                    (iterations ?? Enumerable.Empty<WasperStudyIteration>()).ToList();

                string structureKey = string.Join(
                    ",",
                    parameterList.Select(parameter => parameter.SliderId.ToString("D")));
                if (!string.Equals(_studyParameterStructureKey, structureKey, StringComparison.Ordinal))
                {
                    _studyParameterRows = parameterList.Select(parameter => new StudyParameterRow
                    {
                        SliderId = parameter.SliderId,
                        Enabled = parameter.Enabled,
                        Name = parameter.Name,
                        Current = parameter.OriginalValue.ToString("0.######", CultureInfo.InvariantCulture),
                        Minimum = parameter.Minimum.ToString("0.######", CultureInfo.InvariantCulture),
                        Maximum = parameter.Maximum.ToString("0.######", CultureInfo.InvariantCulture),
                        Samples = parameter.Samples.ToString(CultureInfo.InvariantCulture)
                    }).ToList();
                    _studyParameterStructureKey = structureKey;
                    _parameterGrid.DataStore = _studyParameterRows;
                }
                else
                {
                    // Same sliders as before -- update only the read-only Name/Current columns in
                    // place so any Minimum/Maximum/Samples the user is mid-typing survive a solve-
                    // triggered refresh (mirrors KpiManagerForm's IsCurrentCellInEditMode guard,
                    // without an Eto equivalent of that WinForms-specific flag).
                    Dictionary<Guid, WasperStudyParameter> byId = parameterList
                        .Where(parameter => parameter.SliderId != Guid.Empty)
                        .GroupBy(parameter => parameter.SliderId)
                        .ToDictionary(group => group.Key, group => group.First());
                    foreach (StudyParameterRow row in _studyParameterRows)
                    {
                        if (!byId.TryGetValue(row.SliderId, out WasperStudyParameter current))
                            continue;
                        row.Name = current.Name;
                        row.Current = current.OriginalValue.ToString("0.######", CultureInfo.InvariantCulture);
                    }
                    _parameterGrid.ReloadData(Enumerable.Range(0, _studyParameterRows.Count)); // Unverified.
                }
                UpdateEstimatedIterationCount();

                _studyIterationRows = iterationList
                    .AsEnumerable()
                    .Reverse()
                    .Take(250)
                    .Select(iteration => new StudyIterationRow
                    {
                        Index = iteration.Index.ToString(CultureInfo.InvariantCulture),
                        SampleName = iteration.SampleName,
                        Parameters = string.Join(
                            "; ",
                            iteration.Parameters.Select(pair => $"{pair.Key}={pair.Value:0.####}")),
                        Kpis = (iteration.Kpis?.Count ?? 0).ToString(CultureInfo.InvariantCulture),
                        Status = iteration.Status,
                        Captured = iteration.CapturedUtc.ToString("u")
                    })
                    .ToList();
                _updatingIterationGrid = true;
                try
                {
                    _iterationGrid.DataStore = _studyIterationRows;
                    SelectIterationGridRow(_dashboardSelection.PrimaryId);
                }
                finally
                {
                    _updatingIterationGrid = false;
                }

                _studyStatus.Text = status ?? string.Empty;
                string normalizedStatus = (status ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(normalizedStatus) &&
                    !string.Equals(normalizedStatus, _lastLoggedStudyStatus, StringComparison.Ordinal))
                {
                    _lastLoggedStudyStatus = normalizedStatus;
                    AppendStudyLog(normalizedStatus);
                }

                int numericKpis = iterationList
                    .SelectMany(iteration => iteration.Kpis ?? new List<WasperKpi>())
                    .Where(kpi => kpi?.Value.HasValue == true)
                    .Select(kpi => kpi.Key)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                _dashboardStatus.Text = iterationList.Count == 0
                    ? "No captured iterations yet. Run or capture a study to populate the dashboard."
                    : $"{iterationList.Count} captured iteration(s) · " +
                        $"{parameterList.Count(parameter => parameter.Enabled)} active parameter(s) · " +
                        $"{numericKpis} numeric KPI series";
                UpdateDashboardData(parameterList, iterationList);

                _studyProgress.Value = Math.Max(0, Math.Min(100, (int)Math.Round(progress * 100.0)));
                _runStudy.Enabled = !running && !viewingSavedStudy;
                _resumeStudy.Enabled = !running && !viewingSavedStudy && iterationList.Count > 0;
                _linkSelected.Enabled = !running && !viewingSavedStudy;
                _unlinkSelected.Enabled = !running && !viewingSavedStudy;
                _restoreDefaults.Enabled = !running && !viewingSavedStudy;
                _clearIterations.Enabled = !running && !viewingSavedStudy;
                _stopStudy.Enabled = running;
                _captureIteration.Enabled = !running && !viewingSavedStudy;
                _saveStudy.Enabled = !viewingSavedStudy;

                bool editable = !(running || viewingSavedStudy);
                if (_studyMinColumn != null)
                    _studyMinColumn.Editable = editable;
                if (_studyMaxColumn != null)
                    _studyMaxColumn.Editable = editable;
                if (_studySamplesColumn != null)
                    _studySamplesColumn.Editable = editable;
            }

            private void IterationGridSelectionChanged(object sender, EventArgs eventArgs)
            {
                if (_updatingIterationGrid || _syncingStudyDashboardSelection)
                    return;
                int row = _iterationGrid.SelectedRow;
                if (row < 0 || row >= _studyIterationRows.Count ||
                    !int.TryParse(
                        _studyIterationRows[row].Index,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int individualId))
                {
                    return;
                }
                _syncingStudyDashboardSelection = true;
                try
                {
                    _dashboardSelection.SelectOnly(individualId);
                }
                finally
                {
                    _syncingStudyDashboardSelection = false;
                }
                RenderDashboardCharts();
            }

            private void IterationGridMouseDown(object sender, MouseEventArgs eventArgs)
            {
                if (eventArgs.Buttons != MouseButtons.Alternate)
                    return;
                GridCell cell = _iterationGrid.GetCellAt(eventArgs.Location);
                if (cell == null || cell.RowIndex < 0 || cell.RowIndex >= _studyIterationRows.Count)
                    return;
                _iterationGrid.SelectedRow = cell.RowIndex;
                ShowDashboardPointMenu(_iterationGrid, eventArgs.Location);
                eventArgs.Handled = true;
            }

            private void SelectIterationGridRow(int? individualId)
            {
                if (!individualId.HasValue)
                    return;
                int row = _studyIterationRows.FindIndex(item =>
                    int.TryParse(
                        item.Index,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out int candidate) &&
                    candidate == individualId.Value);
                if (row < 0 || _iterationGrid.SelectedRow == row)
                    return;
                _syncingStudyDashboardSelection = true;
                try
                {
                    _iterationGrid.SelectedRow = row;
                }
                finally
                {
                    _syncingStudyDashboardSelection = false;
                }
            }

            /// <summary>Folder the component reports for the study currently on screen.</summary>
            public void UpdateDashboardSnapshotFolder(string folder)
            {
                if (string.Equals(_dashboardSnapshotFolder, folder, StringComparison.OrdinalIgnoreCase))
                    return;
                _dashboardSnapshotFolder = folder ?? string.Empty;
                // A different study may resolve the same selection to a different file.
                _dashboardSnapshotPath = string.Empty;
            }

            /// <summary>
            /// Pushes the stored Dashboard settings into the controls. Called after the study loads
            /// so the tab comes back exactly as the user left it.
            /// </summary>
            public void ApplyDashboardSettings(WasperDashboardSettings settings)
            {
                _dashboardSettings = settings ?? new WasperDashboardSettings();
                _updatingDashboard = true;
                try
                {
                    _dashboardHiddenGroups.Clear();
                    foreach (string group in _dashboardSettings.HiddenGroups ?? new List<string>())
                    {
                        if (!string.IsNullOrWhiteSpace(group))
                            _dashboardHiddenGroups.Add(group);
                    }
                    _dashboardTextSize.Value = ClampDashboardValue(
                        _dashboardSettings.TextSizePercent,
                        _dashboardTextSize.MinValue,
                        _dashboardTextSize.MaxValue);
                    _dashboardHistogramBins.Value = ClampDashboardValue(
                        _dashboardSettings.HistogramBins,
                        _dashboardHistogramBins.MinValue,
                        _dashboardHistogramBins.MaxValue);
                    _dashboardScatterNames.Checked = _dashboardSettings.ScatterShowNames;
                    _dashboardScatterValues.Checked = _dashboardSettings.ScatterShowValues;
                    _dashboardHistogramBandwidth.Value = ClampDashboardValue(
                        _dashboardSettings.HistogramBandwidthPercent,
                        _dashboardHistogramBandwidth.MinValue,
                        _dashboardHistogramBandwidth.MaxValue);
                    SelectDashboardItem(
                        _dashboardHistogramMode,
                        item => item is DashboardHistogramModeOption option &&
                            string.Equals(
                                option.Mode.ToString(),
                                _dashboardSettings.HistogramMode,
                                StringComparison.OrdinalIgnoreCase));
                    DashboardHistogramModeChanged(_dashboardHistogramMode, EventArgs.Empty);
                    SelectDashboardItem(
                        _dashboardScatterStyle,
                        item => item is DashboardScatterStyleOption option &&
                            string.Equals(
                                option.Style.ToString(),
                                _dashboardSettings.ScatterStyle,
                                StringComparison.OrdinalIgnoreCase));
                }
                finally
                {
                    _updatingDashboard = false;
                }
                // Variable selections are restored inside UpdateDashboardData, which is the only
                // place that knows which variables the captured iterations actually provide.
                UpdateDashboardData(_dashboardParameters, _dashboardIterations);
            }

            private static double ClampDashboardValue(int value, double minimum, double maximum) =>
                Math.Max(minimum, Math.Min(maximum, value));

            private static void SelectDashboardItem(DropDown dropDown, Func<object, bool> match)
            {
                var items = (dropDown.DataStore ?? Enumerable.Empty<object>()).Cast<object>().ToList();
                for (int index = 0; index < items.Count; index++)
                {
                    if (!match(items[index]))
                        continue;
                    dropDown.SelectedIndex = index;
                    return;
                }
            }

            private static DashboardVariableOption SelectedDashboardOption(DropDown dropDown)
            {
                List<DashboardVariableOption> items = (dropDown.DataStore ?? Enumerable.Empty<object>())
                    .OfType<DashboardVariableOption>()
                    .ToList();
                int index = dropDown.SelectedIndex;
                return index >= 0 && index < items.Count ? items[index] : null;
            }

            private DashboardScatterStyle SelectedScatterStyle() =>
                ((_dashboardScatterStyle.DataStore ?? Enumerable.Empty<object>())
                    .OfType<DashboardScatterStyleOption>()
                    .ElementAtOrDefault(_dashboardScatterStyle.SelectedIndex))?.Style ??
                    DashboardScatterStyle.Markers;

            private WasperHistogramMode SelectedHistogramMode() =>
                ((_dashboardHistogramMode.DataStore ?? Enumerable.Empty<object>())
                    .OfType<DashboardHistogramModeOption>()
                    .ElementAtOrDefault(_dashboardHistogramMode.SelectedIndex))?.Mode ??
                    WasperHistogramMode.Bars;

            private IEnumerable<DashboardChartView> DashboardChartViews()
            {
                yield return _dashboardHistoryImage;
                yield return _dashboardScatterImage;
                yield return _dashboardHeatmapImage;
                yield return _dashboardParallelImage;
                yield return _dashboardHistogramImage;
            }

            private void DashboardChartSizeChanged(object sender, EventArgs eventArgs)
            {
                if (_closed)
                    return;
                ScheduleDashboardRender();
            }

            private void ScheduleDashboardRender()
            {
                _dashboardResizeTimer.Stop();
                _dashboardResizeTimer.Start();
            }

            private Control CreateDashboardTab()
            {
                _dashboardShowInGrasshopper = CommandButton("Show in Grasshopper", 150);
                _dashboardShowInGrasshopper.Enabled = false;
                _dashboardReset = CommandButton("Reset Dashboard", 120);

                _dashboardScatterStyle.SelectedIndex = 0;
                _dashboardHistogramMode.SelectedIndex = 0;

                _dashboardHistoryKpi.SelectedIndexChanged += DashboardControlChanged;
                _dashboardScatterX.SelectedIndexChanged += DashboardControlChanged;
                _dashboardScatterY.SelectedIndexChanged += DashboardControlChanged;
                _dashboardScatterColor.SelectedIndexChanged += DashboardControlChanged;
                _dashboardScatterStyle.SelectedIndexChanged += DashboardControlChanged;
                _dashboardScatterNames.CheckedChanged += DashboardControlChanged;
                _dashboardScatterValues.CheckedChanged += DashboardControlChanged;
                _dashboardHistogramVariable.SelectedIndexChanged += DashboardControlChanged;
                _dashboardHistogramMode.SelectedIndexChanged += DashboardHistogramModeChanged;
                _dashboardHistogramMode.SelectedIndexChanged += DashboardControlChanged;
                _dashboardHistogramBins.ValueChanged += DashboardControlChanged;
                _dashboardHistogramBandwidth.ValueChanged += DashboardControlChanged;
                _dashboardTextSize.ValueChanged += DashboardControlChanged;

                _dashboardShowInGrasshopper.Click += (_, _) =>
                {
                    if (_dashboardSelection.PrimaryId.HasValue)
                        ShowIterationRequested?.Invoke(_dashboardSelection.PrimaryId.Value);
                };
                _dashboardReset.Click += (_, _) =>
                {
                    _dashboardSettings = new WasperDashboardSettings();
                    _dashboardHiddenGroups.Clear();
                    ApplyDashboardSettings(_dashboardSettings);
                    DashboardSettingsChanged?.Invoke(_dashboardSettings);
                };

                _dashboardHistoryImage.MouseDown += DashboardChartClicked;
                _dashboardScatterImage.MouseDown += DashboardChartClicked;
                _dashboardParallelImage.MouseDown += DashboardChartClicked;
                _dashboardHistogramImage.MouseDown += DashboardChartClicked;
                foreach (DashboardChartView chart in DashboardChartViews())
                    chart.SizeChanged += DashboardChartSizeChanged;

                DashboardHistogramModeChanged(_dashboardHistogramMode, EventArgs.Empty);

                var toolbar = new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Items =
                    {
                        new Label { Text = "Text size" },
                        _dashboardTextSize,
                        _dashboardShowInGrasshopper,
                        _dashboardReset
                    }
                };

                var topRow = new TableLayout
                {
                    Spacing = new Size(8, 0),
                    Rows =
                    {
                        new TableRow(
                            new TableCell(CreateDashboardSnapshotCard(), false),
                            new TableCell(CreateDashboardParallelCard(), true))
                    }
                };
                var middleRow = new TableLayout
                {
                    Spacing = new Size(8, 0),
                    Rows =
                    {
                        new TableRow(
                            new TableCell(CreateDashboardHistoryCard(), true),
                            new TableCell(CreateDashboardScatterCard(), true))
                    }
                };
                var bottomRow = new TableLayout
                {
                    Spacing = new Size(8, 0),
                    Rows =
                    {
                        new TableRow(
                            new TableCell(CreateDashboardHeatmapCard(), true),
                            new TableCell(CreateDashboardHistogramCard(), true))
                    }
                };

                var content = new TableLayout
                {
                    Padding = new Padding(10),
                    Spacing = new Size(0, 8),
                    Rows =
                    {
                        new TableRow(new TableCell(_dashboardStatus, true)) { ScaleHeight = false },
                        new TableRow(new TableCell(toolbar, true)) { ScaleHeight = false },
                        new TableRow(new TableCell(topRow, true)) { ScaleHeight = false },
                        new TableRow(new TableCell(middleRow, true)) { ScaleHeight = false },
                        new TableRow(new TableCell(bottomRow, true)) { ScaleHeight = false },
                        new TableRow(new TableCell(_dashboardSelectionDetails, true)) { ScaleHeight = false }
                    }
                };

                return new Scrollable
                {
                    Content = content,
                    ExpandContentWidth = true,
                    ExpandContentHeight = false
                };
            }

            private Control CreateDashboardSnapshotCard()
            {
                return new GroupBox
                {
                    Text = "Selected sample",
                    Padding = new Padding(8),
                    Content = new TableLayout
                    {
                        Spacing = new Size(0, 4),
                        Rows =
                        {
                            new TableRow(new TableCell(_dashboardSnapshotImage, true)) { ScaleHeight = false },
                            new TableRow(new TableCell(_dashboardSnapshotCaption, true)) { ScaleHeight = false },
                            new TableRow(new TableCell(_dashboardSnapshotStatus, true)) { ScaleHeight = false }
                        }
                    }
                };
            }

            private Control CreateDashboardHistoryCard()
            {
                var toolbar = new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Items =
                    {
                        new Label { Text = "KPI" },
                        _dashboardHistoryKpi,
                        DashboardLabelsButton(WasperDashboardSettings.HistoryChart, "KPI history", true, true, true)
                    }
                };
                return new GroupBox
                {
                    Text = "KPI history",
                    Padding = new Padding(8),
                    Content = new TableLayout
                    {
                        Spacing = new Size(0, 6),
                        Rows =
                        {
                            new TableRow(new TableCell(_dashboardHistoryImage, true)) { ScaleHeight = true },
                            new TableRow(new TableCell(toolbar, true)) { ScaleHeight = false }
                        }
                    }
                };
            }

            private Control CreateDashboardScatterCard()
            {
                var toolbarRow1 = new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Items =
                    {
                        new Label { Text = "X" }, _dashboardScatterX,
                        new Label { Text = "Y" }, _dashboardScatterY,
                        new Label { Text = "Style" }, _dashboardScatterStyle
                    }
                };
                var toolbarRow2 = new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Items =
                    {
                        new Label { Text = "Colour by" }, _dashboardScatterColor,
                        _dashboardScatterNames,
                        _dashboardScatterValues,
                        DashboardLabelsButton(WasperDashboardSettings.ScatterChart, "X vs Y", true, true, true)
                    }
                };
                return new GroupBox
                {
                    Text = "X vs Y",
                    Padding = new Padding(8),
                    Content = new TableLayout
                    {
                        Spacing = new Size(0, 6),
                        Rows =
                        {
                            new TableRow(new TableCell(_dashboardScatterImage, true)) { ScaleHeight = true },
                            new TableRow(new TableCell(toolbarRow1, true)) { ScaleHeight = false },
                            new TableRow(new TableCell(toolbarRow2, true)) { ScaleHeight = false }
                        }
                    }
                };
            }

            private Control CreateDashboardHeatmapCard()
            {
                var description = new Label
                {
                    Wrap = WrapMode.Word,
                    Text = "Pearson correlation between the shown inputs and KPIs, from -1 to +1 " +
                        "(diagonal = 1.00); follows the group filter on the Parallel coordinates card."
                };
                var toolbar = new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Items =
                    {
                        DashboardLabelsButton(WasperDashboardSettings.HeatmapChart, "Correlation heatmap", false, false)
                    }
                };
                return new GroupBox
                {
                    Text = "Correlation heatmap",
                    Padding = new Padding(8),
                    Content = new TableLayout
                    {
                        Spacing = new Size(0, 6),
                        Rows =
                        {
                            new TableRow(new TableCell(_dashboardHeatmapImage, true)) { ScaleHeight = true },
                            new TableRow(new TableCell(description, true)) { ScaleHeight = false },
                            new TableRow(new TableCell(toolbar, true)) { ScaleHeight = false }
                        }
                    }
                };
            }

            private Control CreateDashboardParallelCard()
            {
                var groupsBox = new GroupBox
                {
                    Text = "Groups shown (also drives the correlation heatmap)",
                    Padding = new Padding(6),
                    Content = new Scrollable { Content = _dashboardGroupFilterPanel, Height = 90 }
                };
                var toolbar = new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Items =
                    {
                        DashboardLabelsButton(WasperDashboardSettings.ParallelChart, "Parallel coordinates", false, false)
                    }
                };
                return new GroupBox
                {
                    Text = "Parallel coordinates",
                    Padding = new Padding(8),
                    Content = new TableLayout
                    {
                        Spacing = new Size(0, 6),
                        Rows =
                        {
                            new TableRow(new TableCell(_dashboardParallelImage, true)) { ScaleHeight = true },
                            new TableRow(new TableCell(groupsBox, true)) { ScaleHeight = false },
                            new TableRow(new TableCell(toolbar, true)) { ScaleHeight = false }
                        }
                    }
                };
            }

            private Control CreateDashboardHistogramCard()
            {
                var toolbarRow1 = new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Items =
                    {
                        new Label { Text = "Variable" }, _dashboardHistogramVariable,
                        new Label { Text = "Style" }, _dashboardHistogramMode
                    }
                };
                var toolbarRow2 = new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Items =
                    {
                        _dashboardHistogramParameterLabel,
                        _dashboardHistogramBins,
                        _dashboardHistogramBandwidth,
                        DashboardLabelsButton(WasperDashboardSettings.HistogramChart, "Distribution", true, true, true)
                    }
                };
                return new GroupBox
                {
                    Text = "Distribution",
                    Padding = new Padding(8),
                    Content = new TableLayout
                    {
                        Spacing = new Size(0, 6),
                        Rows =
                        {
                            new TableRow(new TableCell(_dashboardHistogramImage, true)) { ScaleHeight = true },
                            new TableRow(new TableCell(toolbarRow1, true)) { ScaleHeight = false },
                            new TableRow(new TableCell(toolbarRow2, true)) { ScaleHeight = false }
                        }
                    }
                };
            }

            /// <summary>
            /// Opens Codex's already-Eto ChartLabelsDialog (WASPer_Sm01Dialogs.cs, nested directly on
            /// the enclosing wsp_Sm01_WASPer_Study_Manager partial class, so it is accessible here the
            /// same way CommandButton/Sm01DialogOwner are -- see the class doc).
            /// </summary>
            private Button DashboardLabelsButton(
                string chartKey,
                string chartName,
                bool showXTitle,
                bool showYTitle,
                bool showRange = false)
            {
                var button = CommandButton("Labels...", 80);
                button.Click += (_, _) =>
                {
                    WasperChartLabels edited = ChartLabelsDialog.Show(
                        chartName,
                        _dashboardSettings.LabelsFor(chartKey),
                        showXTitle,
                        showYTitle,
                        showRange);
                    if (edited == null)
                        return;
                    _dashboardSettings.SetLabels(chartKey, edited);
                    RenderDashboardCharts();
                    RaiseDashboardSettingsChanged();
                };
                return button;
            }

            private void DashboardControlChanged(object sender, EventArgs eventArgs)
            {
                if (_updatingDashboard)
                    return;
                RenderDashboardCharts();
                RaiseDashboardSettingsChanged();
            }

            /// <summary>
            /// Bars and Region are binned; Density is bin-free and governed by a bandwidth. Only the
            /// control that actually applies is shown, and the caption follows it.
            /// </summary>
            private void DashboardHistogramModeChanged(object sender, EventArgs eventArgs)
            {
                bool density = SelectedHistogramMode() == WasperHistogramMode.Density;
                _dashboardHistogramParameterLabel.Text = density ? "Smoothing" : "Bins";
                _dashboardHistogramBins.Visible = !density;
                _dashboardHistogramBandwidth.Visible = density;
            }

            // ---------------------------------------------------------------------------------
            // Options / group filter
            // ---------------------------------------------------------------------------------

            private void UpdateDashboardData(
                IReadOnlyList<WasperStudyParameter> parameters,
                IReadOnlyList<WasperStudyIteration> iterations)
            {
                _dashboardParameters = (parameters ?? Array.Empty<WasperStudyParameter>()).ToList();
                _dashboardIterations = (iterations ?? Array.Empty<WasperStudyIteration>()).ToList();
                _updatingDashboard = true;
                try
                {
                    List<DashboardVariableOption> kpis = DashboardKpiOptions();
                    List<DashboardVariableOption> all = DashboardAllVariableOptions();
                    SetDashboardOptions(
                        _dashboardScatterX,
                        all,
                        OptionFor(all, _dashboardSettings.ScatterX) ?? SelectedDashboardOption(_dashboardScatterX));
                    SetDashboardOptions(
                        _dashboardScatterY,
                        all,
                        OptionFor(all, _dashboardSettings.ScatterY) ?? SelectedDashboardOption(_dashboardScatterY),
                        kpis.FirstOrDefault());
                    SetDashboardOptions(
                        _dashboardHistoryKpi,
                        kpis,
                        OptionFor(kpis, _dashboardSettings.HistoryKpi) ?? SelectedDashboardOption(_dashboardHistoryKpi));
                    SetDashboardOptions(
                        _dashboardHistogramVariable,
                        all,
                        OptionFor(all, _dashboardSettings.HistogramVariable) ??
                            SelectedDashboardOption(_dashboardHistogramVariable));
                    List<DashboardVariableOption> colorOptions = DashboardColorOptions(all);
                    SetDashboardOptions(
                        _dashboardScatterColor,
                        colorOptions,
                        OptionFor(colorOptions, _dashboardSettings.ScatterColor) ??
                            SelectedDashboardOption(_dashboardScatterColor));
                    RefreshDashboardGroupList();
                }
                finally
                {
                    _updatingDashboard = false;
                }

                if (_dashboardSelection.PrimaryId.HasValue &&
                    !_dashboardIterations.Any(iteration =>
                        iteration.Index == _dashboardSelection.PrimaryId.Value))
                {
                    _dashboardSelection.Clear();
                }
                RenderDashboardCharts();
            }

            private List<DashboardVariableOption> DashboardParameterOptions()
            {
                var result = new List<DashboardVariableOption>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (WasperStudyParameter parameter in _dashboardParameters)
                {
                    if (parameter == null || string.IsNullOrWhiteSpace(parameter.Name) ||
                        !seen.Add(parameter.Name))
                    {
                        continue;
                    }
                    result.Add(new DashboardVariableOption(
                        parameter.Name, parameter.Name, string.Empty, DashboardParameterGroupName, true));
                }
                foreach (string key in _dashboardIterations
                    .SelectMany(iteration => iteration.Parameters?.Keys ?? Enumerable.Empty<string>()))
                {
                    if (!string.IsNullOrWhiteSpace(key) && seen.Add(key))
                    {
                        result.Add(new DashboardVariableOption(
                            key, key, string.Empty, DashboardParameterGroupName, true));
                    }
                }
                return result;
            }

            private List<DashboardVariableOption> DashboardKpiOptions()
            {
                var result = new List<DashboardVariableOption>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (WasperKpi kpi in _dashboardIterations
                    .SelectMany(iteration => iteration.Kpis ?? new List<WasperKpi>()))
                {
                    if (kpi?.Value.HasValue != true || string.IsNullOrWhiteSpace(kpi.Key) ||
                        !seen.Add(kpi.Key))
                    {
                        continue;
                    }
                    string group = string.IsNullOrWhiteSpace(kpi.DisplayGroup) ? "Other" : kpi.DisplayGroup;
                    result.Add(new DashboardVariableOption(
                        kpi.Key,
                        group + ": " + (string.IsNullOrWhiteSpace(kpi.Label) ? kpi.Key : kpi.Label),
                        kpi.Unit,
                        group));
                }
                return result;
            }

            private List<DashboardVariableOption> DashboardAllVariableOptions()
            {
                var result = new List<DashboardVariableOption>();
                result.AddRange(DashboardParameterOptions());
                result.AddRange(DashboardKpiOptions());
                return result;
            }

            private List<string> DashboardGroupNames()
            {
                var result = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (DashboardParameterOptions().Count > 0 && seen.Add(DashboardParameterGroupName))
                    result.Add(DashboardParameterGroupName);
                foreach (DashboardVariableOption option in DashboardKpiOptions())
                {
                    if (seen.Add(option.Group))
                        result.Add(option.Group);
                }
                return result;
            }

            private List<DashboardVariableOption> DashboardColorOptions(
                List<DashboardVariableOption> numericOptions)
            {
                var result = new List<DashboardVariableOption> { DashboardNoColorOption };
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DashboardVariableOption option in numericOptions ??
                    new List<DashboardVariableOption>())
                {
                    result.Add(option);
                    if (!option.IsParameter)
                        seen.Add(option.Key);
                }
                foreach (WasperKpi kpi in _dashboardIterations
                    .SelectMany(iteration => iteration.Kpis ?? new List<WasperKpi>()))
                {
                    if (kpi == null ||
                        string.IsNullOrWhiteSpace(kpi.Key) ||
                        string.IsNullOrWhiteSpace(kpi.TextValue) ||
                        !seen.Add(kpi.Key))
                    {
                        continue;
                    }
                    string group = string.IsNullOrWhiteSpace(kpi.DisplayGroup) ? "Other" : kpi.DisplayGroup;
                    result.Add(new DashboardVariableOption(
                        kpi.Key,
                        group + ": " + (string.IsNullOrWhiteSpace(kpi.Label) ? kpi.Key : kpi.Label),
                        string.Empty,
                        group));
                }
                return result;
            }

            /// <summary>
            /// Rebuilds the inline group-filter checkboxes -- an Eto StackLayout of CheckBoxes in
            /// place of the WinForms popup (a ToolStripDropDown hosting a CheckedListBox).
            /// </summary>
            private void RefreshDashboardGroupList()
            {
                List<string> groups = DashboardGroupNames();
                _updatingDashboardGroups = true;
                try
                {
                    _dashboardGroupFilterPanel.Items.Clear();
                    _dashboardGroupToggles.Clear();
                    foreach (string group in groups)
                    {
                        var toggle = new CheckBox
                        {
                            Text = group,
                            Checked = !_dashboardHiddenGroups.Contains(group)
                        };
                        toggle.CheckedChanged += DashboardGroupToggleChanged;
                        _dashboardGroupToggles[group] = toggle;
                        _dashboardGroupFilterPanel.Items.Add(toggle);
                    }
                }
                finally
                {
                    _updatingDashboardGroups = false;
                }
            }

            private void DashboardGroupToggleChanged(object sender, EventArgs eventArgs)
            {
                if (_updatingDashboardGroups || !(sender is CheckBox toggle))
                    return;
                string group = _dashboardGroupToggles
                    .FirstOrDefault(pair => ReferenceEquals(pair.Value, toggle)).Key;
                if (string.IsNullOrEmpty(group))
                    return;
                if (toggle.Checked == true)
                    _dashboardHiddenGroups.Remove(group);
                else
                    _dashboardHiddenGroups.Add(group);
                RenderDashboardCharts();
                RaiseDashboardSettingsChanged();
            }

            private bool IsDashboardGroupVisible(DashboardVariableOption option) =>
                option != null && !_dashboardHiddenGroups.Contains(option.Group);

            private static void SetDashboardOptions(
                DropDown dropDown,
                List<DashboardVariableOption> options,
                DashboardVariableOption previous,
                DashboardVariableOption fallback = null)
            {
                dropDown.DataStore = options;
                DashboardVariableOption match = SameVariable(options, previous);
                DashboardVariableOption resolved =
                    match ?? SameVariable(options, fallback) ?? options.FirstOrDefault();
                dropDown.SelectedIndex = resolved == null ? -1 : options.IndexOf(resolved);
                dropDown.Enabled = options.Count > 0;
            }

            private static DashboardVariableOption SameVariable(
                List<DashboardVariableOption> options,
                DashboardVariableOption wanted)
            {
                return wanted == null
                    ? null
                    : options.FirstOrDefault(option =>
                        option.IsParameter == wanted.IsParameter &&
                        string.Equals(option.Key, wanted.Key, StringComparison.OrdinalIgnoreCase));
            }

            private DashboardVariableOption OptionFor(
                List<DashboardVariableOption> options,
                WasperDashboardVariableRef reference)
            {
                return reference == null || reference.IsEmpty
                    ? null
                    : options.FirstOrDefault(option =>
                        option.IsParameter == reference.IsInput &&
                        string.Equals(option.Key, reference.Key, StringComparison.OrdinalIgnoreCase));
            }

            private static WasperDashboardVariableRef VariableRef(DropDown dropDown)
            {
                DashboardVariableOption option = SelectedDashboardOption(dropDown);
                return option == null ? null : WasperDashboardVariableRef.Create(option.Key, option.IsParameter);
            }

            // ---------------------------------------------------------------------------------
            // Persisted settings
            // ---------------------------------------------------------------------------------

            private void CaptureDashboardSettings()
            {
                _dashboardSettings.HiddenGroups = _dashboardHiddenGroups
                    .OrderBy(group => group, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                _dashboardSettings.TextSizePercent = (int)_dashboardTextSize.Value;
                _dashboardSettings.HistogramBins = (int)_dashboardHistogramBins.Value;
                _dashboardSettings.HistogramBandwidthPercent = (int)_dashboardHistogramBandwidth.Value;
                _dashboardSettings.ScatterShowNames = _dashboardScatterNames.Checked == true;
                _dashboardSettings.ScatterShowValues = _dashboardScatterValues.Checked == true;
                _dashboardSettings.HistogramMode = SelectedHistogramMode().ToString();
                _dashboardSettings.ScatterStyle = SelectedScatterStyle().ToString();
                _dashboardSettings.HistoryKpi = VariableRef(_dashboardHistoryKpi);
                _dashboardSettings.ScatterX = VariableRef(_dashboardScatterX);
                _dashboardSettings.ScatterY = VariableRef(_dashboardScatterY);
                _dashboardSettings.ScatterColor = VariableRef(_dashboardScatterColor);
                _dashboardSettings.HistogramVariable = VariableRef(_dashboardHistogramVariable);
            }

            private void RaiseDashboardSettingsChanged()
            {
                if (_updatingDashboard)
                    return;
                CaptureDashboardSettings();
                DashboardSettingsChanged?.Invoke(_dashboardSettings);
            }

            // ---------------------------------------------------------------------------------
            // Render pipeline -- reuses the shared, host-neutral chart renderers verbatim; see the
            // "Dashboard tab" field-region comment above.
            // ---------------------------------------------------------------------------------

            private void RenderDashboardCharts()
            {
                if (_updatingDashboard)
                    return;
                _updatingDashboard = true;
                try
                {
                    RenderDashboardHistory();
                    RenderDashboardScatter();
                    RenderDashboardHeatmap();
                    RenderDashboardParallel();
                    RenderDashboardHistogram();
                    UpdateDashboardSelectionDetails();
                }
                finally
                {
                    _updatingDashboard = false;
                }
            }

            private void RenderDashboardHistory()
            {
                DashboardVariableOption selected = SelectedDashboardOption(_dashboardHistoryKpi);
                List<WasperChartPoint> points = selected == null
                    ? new List<WasperChartPoint>()
                    : _dashboardIterations
                        .Select((iteration, index) => DashboardKpiPoint(
                            iteration, index, selected.Key, iteration.Index))
                        .Where(point => point != null)
                        .ToList();
                var series = new WasperChartSeries
                {
                    Key = selected?.Key ?? string.Empty,
                    Label = selected?.Label ?? string.Empty,
                    Color = System.Drawing.Color.FromArgb(31, 119, 180),
                    LineWidth = 2.0,
                    MarkerSize = 5.0,
                    Points = points
                };
                WasperChartRenderOptions options = DashboardRenderOptions(
                    _dashboardHistoryImage,
                    WasperDashboardSettings.HistoryChart,
                    "KPI history",
                    "Iteration",
                    selected?.DisplayName ?? "KPI");
                ReplaceDashboardResult(
                    _dashboardHistoryImage,
                    ref _dashboardHistoryResult,
                    _dashboardRenderer.Render(new[] { series }, options));
            }

            private void RenderDashboardScatter()
            {
                DashboardVariableOption xOption = SelectedDashboardOption(_dashboardScatterX);
                DashboardVariableOption yOption = SelectedDashboardOption(_dashboardScatterY);
                var points = new List<WasperChartPoint>();
                if (xOption != null && yOption != null)
                {
                    for (int index = 0; index < _dashboardIterations.Count; index++)
                    {
                        WasperStudyIteration iteration = _dashboardIterations[index];
                        if (!TryGetDashboardValue(iteration, xOption, out double x) ||
                            !TryGetDashboardValue(iteration, yOption, out double y))
                        {
                            continue;
                        }
                        WasperChartPoint point = DashboardPoint(iteration, index, x, y);
                        if (point != null)
                            points.Add(point);
                    }
                }
                DashboardVariableOption colorOption = SelectedDashboardOption(_dashboardScatterColor);
                if (colorOption != null && colorOption.Key.Length == 0)
                    colorOption = null;
                List<DashboardCategory> categories = DashboardCategories(
                    colorOption, out List<WasperChartLegendEntry> legend);
                DashboardScatterStyle style = SelectedScatterStyle();
                double lineWidth = style == DashboardScatterStyle.Markers ? 0.0 : 1.6;
                double markerSize = style == DashboardScatterStyle.Line ? 0.0 : 6.0;

                var seriesList = new List<WasperChartSeries>();
                if (categories.Count > 0)
                {
                    foreach (DashboardCategory category in categories)
                    {
                        List<WasperChartPoint> members = DashboardLinePoints(
                            points.Where(point => category.IndividualIds.Contains(point.IndividualId)),
                            lineWidth > 0.0);
                        if (members.Count == 0)
                            continue;
                        seriesList.Add(new WasperChartSeries
                        {
                            Key = (yOption?.Key ?? string.Empty) + "|" + category.Label,
                            Label = category.Label,
                            Color = category.Color,
                            LineWidth = lineWidth,
                            MarkerSize = markerSize,
                            Points = members
                        });
                    }
                }
                else
                {
                    seriesList.Add(new WasperChartSeries
                    {
                        Key = yOption?.Key ?? string.Empty,
                        Label = yOption?.Label ?? string.Empty,
                        Color = System.Drawing.Color.FromArgb(214, 92, 37),
                        LineWidth = lineWidth,
                        MarkerSize = markerSize,
                        Points = DashboardLinePoints(points, lineWidth > 0.0)
                    });
                }
                bool showNames = _dashboardScatterNames.Checked == true;
                bool showValues = _dashboardScatterValues.Checked == true;
                if (showValues)
                {
                    foreach (WasperChartPoint point in points)
                    {
                        string values =
                            $"({DashboardFormatValue(point.X)}, {DashboardFormatValue(point.Y)})";
                        point.Label = showNames && !string.IsNullOrWhiteSpace(point.Label)
                            ? point.Label + " " + values
                            : values;
                    }
                }
                WasperChartRenderOptions options = DashboardRenderOptions(
                    _dashboardScatterImage,
                    WasperDashboardSettings.ScatterChart,
                    "X vs Y",
                    xOption?.DisplayName ?? "X",
                    yOption?.DisplayName ?? "Y");
                options.LegendEntries = legend;
                options.ShowPointLabels = showNames || showValues;
                ReplaceDashboardResult(
                    _dashboardScatterImage,
                    ref _dashboardScatterResult,
                    _dashboardRenderer.Render(seriesList, options));
            }

            private void RenderDashboardHeatmap()
            {
                WasperChartRenderOptions options = DashboardRenderOptions(
                    _dashboardHeatmapImage,
                    WasperDashboardSettings.HeatmapChart, "Correlation heatmap", string.Empty, string.Empty);
                ReplaceDashboardResult(
                    _dashboardHeatmapImage,
                    ref _dashboardHeatmapResult,
                    _dashboardHeatmapRenderer.Render(DashboardDataset(), options));
            }

            private void RenderDashboardParallel()
            {
                WasperChartRenderOptions options = DashboardRenderOptions(
                    _dashboardParallelImage,
                    WasperDashboardSettings.ParallelChart, "Parallel coordinates", string.Empty, string.Empty);
                ReplaceDashboardResult(
                    _dashboardParallelImage,
                    ref _dashboardParallelResult,
                    _dashboardParallelRenderer.Render(DashboardDataset(), options));
            }

            private void RenderDashboardHistogram()
            {
                DashboardVariableOption selected = SelectedDashboardOption(_dashboardHistogramVariable);
                WasperHistogramMode mode = SelectedHistogramMode();
                string variableKey = selected == null
                    ? string.Empty
                    : (selected.IsParameter ? "parameter:" : "kpi:") + selected.Key;
                WasperChartDataset dataset = selected == null
                    ? new WasperChartDataset()
                    : selected.IsParameter
                        ? DashboardDataset(new[] { selected }, Array.Empty<DashboardVariableOption>())
                        : DashboardDataset(Array.Empty<DashboardVariableOption>(), new[] { selected });
                var request = new WasperHistogramRequest
                {
                    Dataset = dataset,
                    VariableKey = variableKey,
                    BinCount = (int)_dashboardHistogramBins.Value,
                    Mode = mode,
                    BandwidthScale = _dashboardHistogramBandwidth.Value / 100.0
                };
                WasperChartRenderOptions options = DashboardRenderOptions(
                    _dashboardHistogramImage,
                    WasperDashboardSettings.HistogramChart,
                    "Distribution",
                    selected?.DisplayName ?? "Variable",
                    mode == WasperHistogramMode.Density ? "Density" : "Count");
                ReplaceDashboardResult(
                    _dashboardHistogramImage,
                    ref _dashboardHistogramResult,
                    _dashboardHistogramRenderer.Render(request, options));
            }

            /// <summary>
            /// Multivariate dataset shared by the parallel-coordinate and correlation charts. Only
            /// variables from checked groups are included, and a constant variable is dropped (its
            /// correlation is undefined and its parallel axis is degenerate).
            /// </summary>
            private WasperChartDataset DashboardDataset()
            {
                List<DashboardVariableOption> parameters = DashboardParameterOptions()
                    .Where(IsDashboardGroupVisible)
                    .Where(DashboardVariableVaries)
                    .ToList();
                List<DashboardVariableOption> kpis = DashboardKpiOptions()
                    .Where(IsDashboardGroupVisible)
                    .Where(DashboardVariableVaries)
                    .ToList();
                int parameterLimit = kpis.Count > 0
                    ? Math.Min(DashboardMultivariateLimit / 2, parameters.Count)
                    : Math.Min(DashboardMultivariateLimit, parameters.Count);
                List<DashboardVariableOption> selectedParameters = parameters.Take(parameterLimit).ToList();
                List<DashboardVariableOption> selectedKpis = kpis
                    .Take(Math.Max(0, DashboardMultivariateLimit - selectedParameters.Count))
                    .ToList();
                return DashboardDataset(selectedParameters, selectedKpis);
            }

            private bool DashboardVariableVaries(DashboardVariableOption option)
            {
                double first = double.NaN;
                bool seen = false;
                foreach (WasperStudyIteration iteration in _dashboardIterations)
                {
                    if (!TryGetDashboardValue(iteration, option, out double value))
                        continue;
                    if (!seen)
                    {
                        first = value;
                        seen = true;
                        continue;
                    }
                    if (Math.Abs(value - first) > 1e-12)
                        return true;
                }
                return false;
            }

            private WasperChartDataset DashboardDataset(
                IReadOnlyList<DashboardVariableOption> selectedParameters,
                IReadOnlyList<DashboardVariableOption> selectedKpis)
            {
                selectedParameters ??= Array.Empty<DashboardVariableOption>();
                selectedKpis ??= Array.Empty<DashboardVariableOption>();
                var dataset = new WasperChartDataset();
                foreach (DashboardVariableOption option in selectedParameters)
                {
                    dataset.Variables.Add(new WasperChartVariable
                    {
                        Key = "parameter:" + option.Key,
                        Name = option.Label,
                        Unit = option.Unit,
                        Group = option.Group,
                        IsParameter = true
                    });
                }
                foreach (DashboardVariableOption option in selectedKpis)
                {
                    dataset.Variables.Add(new WasperChartVariable
                    {
                        Key = "kpi:" + option.Key,
                        Name = option.Label,
                        Unit = option.Unit,
                        Group = option.Group
                    });
                }
                foreach (WasperStudyIteration iteration in _dashboardIterations)
                {
                    var individual = new WasperChartIndividual
                    {
                        IndividualId = iteration.Index,
                        Name = string.IsNullOrWhiteSpace(iteration.SampleName)
                            ? $"Iteration {iteration.Index}"
                            : iteration.SampleName
                    };
                    foreach (DashboardVariableOption option in selectedParameters)
                    {
                        if (iteration.Parameters?.TryGetValue(option.Key, out double value) == true &&
                            IsFinite(value))
                        {
                            individual.Values["parameter:" + option.Key] = value;
                        }
                    }
                    foreach (DashboardVariableOption option in selectedKpis)
                    {
                        WasperKpi kpi = FindNumericKpi(iteration, option.Key);
                        if (kpi?.Value.HasValue == true)
                            individual.Values["kpi:" + option.Key] = kpi.Value.Value;
                    }
                    dataset.Individuals.Add(individual);
                }
                return dataset;
            }

            /// <summary>
            /// Builds the render options for one card at its current Eto client size. A small
            /// bounded fallback is used before the control has completed its first layout pass.
            /// </summary>
            private WasperChartRenderOptions DashboardRenderOptions(
                DashboardChartView host,
                string chartKey,
                string title,
                string xTitle,
                string yTitle)
            {
                WasperChartLabels labels = _dashboardSettings.LabelsFor(chartKey);
                title = WasperChartLabels.Resolve(labels.Title, title);
                xTitle = WasperChartLabels.Resolve(labels.XTitle, xTitle);
                yTitle = WasperChartLabels.Resolve(labels.YTitle, yTitle);
                int hostWidth = host?.ClientSize.Width ?? 0;
                int hostHeight = host?.ClientSize.Height ?? 0;
                int width = hostWidth > 0
                    ? Math.Min(DashboardChartMaximumWidth, hostWidth)
                    : DashboardChartMinimumWidth;
                int height = hostHeight > 0
                    ? Math.Min(DashboardChartMaximumHeight, hostHeight)
                    : DashboardChartMinimumHeight;
                return new WasperChartRenderOptions
                {
                    XMinimum = labels.XMinimum,
                    XMaximum = labels.XMaximum,
                    YMinimum = labels.YMinimum,
                    YMaximum = labels.YMaximum,
                    Width = width,
                    Height = height,
                    Dpi = 96,
                    PixelScale = Math.Max(1f, LogicalPixelSize),
                    Axis = new WasperChartAxisSettings
                    {
                        XTitle = xTitle ?? string.Empty,
                        YTitle = yTitle ?? string.Empty,
                        XTicksInteger = false,
                        YTicksInteger = false,
                        XTitleSize = 9.0,
                        YTitleSize = 9.0,
                        XTextSize = 8.0,
                        YTextSize = 8.0
                    },
                    Layout = new WasperChartLayoutSettings
                    {
                        Title = title ?? string.Empty,
                        TitleSize = 11.0,
                        Dpi = 96,
                        ShowReferences = true
                    },
                    SelectedIndividualIds = new HashSet<int>(_dashboardSelection.SelectedIds),
                    TextScale = _dashboardTextSize.Value / 100.0
                };
            }

            private static WasperChartPoint DashboardKpiPoint(
                WasperStudyIteration iteration,
                int dataIndex,
                string kpiKey,
                double x)
            {
                WasperKpi kpi = FindNumericKpi(iteration, kpiKey);
                return kpi?.Value.HasValue == true
                    ? DashboardPoint(iteration, dataIndex, x, kpi.Value.Value)
                    : null;
            }

            private static WasperChartPoint DashboardPoint(
                WasperStudyIteration iteration,
                int dataIndex,
                double x,
                double y)
            {
                if (iteration == null || !IsFinite(x) || !IsFinite(y))
                    return null;
                return new WasperChartPoint
                {
                    IndividualId = iteration.Index,
                    DataIndex = dataIndex,
                    Label = string.IsNullOrWhiteSpace(iteration.SampleName)
                        ? $"Iteration {iteration.Index}"
                        : iteration.SampleName,
                    X = x,
                    Y = y,
                    Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["status"] = iteration.Status ?? string.Empty,
                        ["captured_utc"] = iteration.CapturedUtc.ToString("u")
                    }
                };
            }

            private static bool TryGetDashboardValue(
                WasperStudyIteration iteration,
                DashboardVariableOption option,
                out double value)
            {
                value = double.NaN;
                if (iteration == null || option == null)
                    return false;
                if (option.IsParameter)
                {
                    return iteration.Parameters != null &&
                        iteration.Parameters.TryGetValue(option.Key, out value) &&
                        IsFinite(value);
                }
                WasperKpi kpi = FindNumericKpi(iteration, option.Key);
                if (kpi?.Value.HasValue != true || !IsFinite(kpi.Value.Value))
                    return false;
                value = kpi.Value.Value;
                return true;
            }

            private static WasperKpi FindNumericKpi(WasperStudyIteration iteration, string key)
            {
                return iteration?.Kpis?.FirstOrDefault(kpi =>
                    kpi?.Value.HasValue == true &&
                    string.Equals(kpi.Key, key, StringComparison.OrdinalIgnoreCase));
            }

            private static string DashboardFormatValue(double value)
            {
                double magnitude = Math.Abs(value);
                string format = magnitude >= 1000.0 || (magnitude > 0.0 && magnitude < 0.001)
                    ? "0.###E+0"
                    : magnitude >= 100.0
                        ? "0.#"
                        : magnitude >= 1.0
                            ? "0.###"
                            : "0.####";
                return value.ToString(format, CultureInfo.InvariantCulture);
            }

            private static bool IsFinite(double value) =>
                !double.IsNaN(value) && !double.IsInfinity(value);

            private static void ReplaceDashboardResult(
                DashboardChartView host,
                ref WasperChartRenderResult current,
                WasperChartRenderResult replacement)
            {
                WasperChartRenderResult previous = current;
                current = replacement;
                Size renderSize = replacement?.Bitmap == null
                    ? Size.Empty
                    : new Size(
                        Math.Max(1, (int)Math.Round(
                            replacement.Bitmap.Width / Math.Max(1f, replacement.PixelScale))),
                        Math.Max(1, (int)Math.Round(
                            replacement.Bitmap.Height / Math.Max(1f, replacement.PixelScale))));
                host.ReplaceImage(ConvertToEtoImage(replacement?.Bitmap), renderSize);
                previous?.Dispose();
            }

            /// <summary>
            /// Click handling for the four point-based charts. WasperChartRenderResult.HitTest(...)
            /// takes a System.Drawing.PointF, not Eto.Drawing.PointF, so the Eto MouseEventArgs
            /// location is converted explicitly.
            /// </summary>
            private void DashboardChartClicked(object sender, MouseEventArgs eventArgs)
            {
                if (eventArgs.Buttons != MouseButtons.Primary &&
                    eventArgs.Buttons != MouseButtons.Alternate)
                    return;
                bool selected = ReferenceEquals(sender, _dashboardHistogramImage)
                    ? DashboardHistogramClicked(eventArgs)
                    : DashboardPointClicked(sender, eventArgs);
                if (!selected)
                    return;
                RenderDashboardCharts();
                if (eventArgs.Buttons == MouseButtons.Alternate && sender is Control control)
                {
                    ShowDashboardPointMenu(control, eventArgs.Location);
                    eventArgs.Handled = true;
                }
            }

            private bool DashboardPointClicked(object sender, MouseEventArgs eventArgs)
            {
                if (!(sender is DashboardChartView host))
                    return false;
                WasperChartRenderResult result = ReferenceEquals(sender, _dashboardHistoryImage)
                    ? _dashboardHistoryResult
                    : ReferenceEquals(sender, _dashboardScatterImage)
                        ? _dashboardScatterResult
                        : _dashboardParallelResult;
                System.Drawing.PointF location = host.ToRenderPoint(eventArgs.Location);
                WasperChartHitTarget hit = result?.HitTest(
                    location,
                    10f,
                    target => target.Kind == WasperChartHitKind.Point ||
                        target.Kind == WasperChartHitKind.Segment);
                if (hit == null)
                    return false;
                if ((eventArgs.Modifiers & Keys.Control) == Keys.Control)
                    _dashboardSelection.Toggle(hit.IndividualId);
                else
                    _dashboardSelection.SelectOnly(hit.IndividualId);
                return true;
            }

            /// <summary>A histogram bin stands for every individual it contains, so a bin click selects them all.</summary>
            private bool DashboardHistogramClicked(MouseEventArgs eventArgs)
            {
                System.Drawing.PointF location = _dashboardHistogramImage.ToRenderPoint(
                    eventArgs.Location);
                WasperChartHitTarget hit = _dashboardHistogramResult?.HitTest(
                    location, 2f, target => target.Kind == WasperChartHitKind.Cell);
                if (hit == null ||
                    !hit.Metadata.TryGetValue(
                        WasperHistogramRenderer.IndividualIdsMetadataKey,
                        out string packed))
                {
                    return false;
                }
                List<int> ids = packed
                    .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(text => int.TryParse(text, out int id) ? id : (int?)null)
                    .Where(id => id.HasValue)
                    .Select(id => id.Value)
                    .ToList();
                if (ids.Count == 0)
                    return false;
                if ((eventArgs.Modifiers & Keys.Control) == Keys.Control)
                {
                    ids.AddRange(_dashboardSelection.SelectedIds);
                    ids = ids.Distinct().ToList();
                }
                _dashboardSelection.SetSelection(ids, ids[0]);
                return true;
            }

            private void ShowDashboardPointMenu(Control control, Eto.Drawing.PointF location)
            {
                if (!_dashboardSelection.PrimaryId.HasValue)
                    return;
                if (_dashboardPointMenu == null)
                {
                    var show = new ButtonMenuItem { Text = "Show in Grasshopper" };
                    show.Click += (_, _) =>
                    {
                        if (_dashboardSelection.PrimaryId.HasValue)
                            ShowIterationRequested?.Invoke(_dashboardSelection.PrimaryId.Value);
                    };
                    var copy = new ButtonMenuItem { Text = "Copy sample name" };
                    copy.Click += (_, _) => CopySelectedDashboardSampleName();
                    _dashboardPointMenu = new ContextMenu();
                    _dashboardPointMenu.Items.Add(show);
                    _dashboardPointMenu.Items.Add(copy);
                }
                _dashboardPointMenu.Show(control, location);
            }

            private void CopySelectedDashboardSampleName()
            {
                if (!_dashboardSelection.PrimaryId.HasValue)
                    return;
                WasperStudyIteration iteration = _dashboardIterations.FirstOrDefault(item =>
                    item.Index == _dashboardSelection.PrimaryId.Value);
                if (iteration == null)
                    return;
                Clipboard.Instance.Text = string.IsNullOrWhiteSpace(iteration.SampleName)
                    ? $"Iteration {iteration.Index}"
                    : iteration.SampleName;
            }

            // ---------------------------------------------------------------------------------
            // Colour-by-variable categories, shared by the scatter chart
            // ---------------------------------------------------------------------------------

            private static List<WasperChartPoint> DashboardLinePoints(
                IEnumerable<WasperChartPoint> points,
                bool joined)
            {
                IEnumerable<WasperChartPoint> source = points ?? Enumerable.Empty<WasperChartPoint>();
                return joined
                    ? source.OrderBy(point => point.X).ThenBy(point => point.DataIndex).ToList()
                    : source.ToList();
            }

            private static bool TryGetDashboardCategory(
                WasperStudyIteration iteration,
                DashboardVariableOption option,
                out string label,
                out double sort)
            {
                label = string.Empty;
                sort = double.NaN;
                if (iteration == null || option == null)
                    return false;
                if (!option.IsParameter)
                {
                    WasperKpi kpi = iteration.Kpis?.FirstOrDefault(item =>
                        item != null &&
                        string.Equals(item.Key, option.Key, StringComparison.OrdinalIgnoreCase));
                    if (kpi != null && !string.IsNullOrWhiteSpace(kpi.TextValue))
                    {
                        label = kpi.TextValue.Trim();
                        return true;
                    }
                }
                if (!TryGetDashboardValue(iteration, option, out double value))
                    return false;
                sort = value;
                label = value.ToString("0.####", CultureInfo.InvariantCulture);
                return true;
            }

            private List<DashboardCategory> DashboardCategories(
                DashboardVariableOption option,
                out List<WasperChartLegendEntry> legend)
            {
                legend = new List<WasperChartLegendEntry>();
                var empty = new List<DashboardCategory>();
                if (option == null || string.IsNullOrEmpty(option.Key))
                    return empty;
                var categories = new Dictionary<string, DashboardCategory>(StringComparer.Ordinal);
                foreach (WasperStudyIteration iteration in _dashboardIterations)
                {
                    if (!TryGetDashboardCategory(iteration, option, out string label, out double sort))
                        continue;
                    if (!categories.TryGetValue(label, out DashboardCategory category))
                    {
                        category = new DashboardCategory { Label = label, Sort = sort };
                        categories[label] = category;
                    }
                    category.IndividualIds.Add(iteration.Index);
                }
                if (categories.Count == 0)
                    return empty;
                List<DashboardCategory> ordered = categories.Values
                    .OrderBy(category => category.HasSort ? 0 : 1)
                    .ThenBy(category => category.HasSort ? category.Sort : 0.0)
                    .ThenBy(category => category.Label, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                int named = Math.Min(ordered.Count, DashboardMaxCategories);
                bool pooled = ordered.Count > DashboardMaxCategories;
                if (pooled)
                    named = DashboardMaxCategories - 1;
                var result = new List<DashboardCategory>();
                for (int index = 0; index < named; index++)
                {
                    ordered[index].Color =
                        DashboardCategoryPalette[index % DashboardCategoryPalette.Length];
                    result.Add(ordered[index]);
                    legend.Add(new WasperChartLegendEntry
                    {
                        Color = ordered[index].Color,
                        Label = ordered[index].Label
                    });
                }
                if (!pooled)
                    return result;
                var other = new DashboardCategory
                {
                    Label = $"Other ({ordered.Count - named})",
                    Color = DashboardCategoryPalette[
                        (DashboardMaxCategories - 1) % DashboardCategoryPalette.Length]
                };
                for (int index = named; index < ordered.Count; index++)
                {
                    foreach (int id in ordered[index].IndividualIds)
                        other.IndividualIds.Add(id);
                }
                result.Add(other);
                legend.Add(new WasperChartLegendEntry
                {
                    Color = other.Color,
                    Label = other.Label
                });
                return result;
            }

            // ---------------------------------------------------------------------------------
            // Selected-sample details and snapshot preview
            // ---------------------------------------------------------------------------------

            private void UpdateDashboardSelectionDetails()
            {
                if (!_dashboardSelection.PrimaryId.HasValue)
                {
                    _dashboardShowInGrasshopper.Enabled = false;
                    UpdateDashboardSnapshot(null);
                    _dashboardSelectionDetails.Text =
                        "Click a chart point to inspect and link an individual across Dashboard views.";
                    return;
                }
                _dashboardShowInGrasshopper.Enabled = true;
                WasperStudyIteration iteration = _dashboardIterations.FirstOrDefault(item =>
                    item.Index == _dashboardSelection.PrimaryId.Value);
                if (iteration == null)
                    return;
                UpdateDashboardSnapshot(iteration);
                string name = string.IsNullOrWhiteSpace(iteration.SampleName)
                    ? $"Iteration {iteration.Index}"
                    : iteration.SampleName;
                string parameterText = string.Join(
                    "; ",
                    (iteration.Parameters ?? new Dictionary<string, double>())
                        .Select(pair => $"{pair.Key}={pair.Value:0.####}"));
                string axisText = string.Empty;
                DashboardVariableOption xSelected = SelectedDashboardOption(_dashboardScatterX);
                DashboardVariableOption ySelected = SelectedDashboardOption(_dashboardScatterY);
                if (xSelected != null && ySelected != null &&
                    TryGetDashboardValue(iteration, xSelected, out double xValue) &&
                    TryGetDashboardValue(iteration, ySelected, out double yValue))
                {
                    axisText =
                        $"  |  {xSelected.Label} = {DashboardFormatValue(xValue)}" +
                        $"  |  {ySelected.Label} = {DashboardFormatValue(yValue)}";
                }
                _dashboardSelectionDetails.Text =
                    $"Selected {iteration.Index}: {name}  |  {iteration.Status}  |  " +
                    $"{iteration.Kpis?.Count ?? 0} KPIs" + axisText +
                    (parameterText.Length == 0 ? string.Empty : "  |  " + parameterText);
            }

            /// <summary>
            /// Shows the selected sample's captured snapshot. Mirrors WASPer_Sm01DashboardTab.cs'
            /// UpdateDashboardSnapshot -- the file discovery/fallback logic is pure and portable
            /// verbatim; only the final Bitmap-to-Image step crosses the Eto/System.Drawing boundary
            /// (ConvertToEtoImage). No empty-state placeholder image is shown here (the WinForms
            /// version overlays a "Snapshot not available" label on the PictureBox) -- the ImageView
            /// is simply left blank, matching the Export tab's simpler preview.
            /// </summary>
            private void UpdateDashboardSnapshot(WasperStudyIteration iteration)
            {
                if (iteration == null)
                {
                    ClearDashboardSnapshot();
                    _dashboardSnapshotCaption.Text = "Select a sample to preview its snapshot.";
                    _dashboardSnapshotStatus.Text = string.Empty;
                    return;
                }
                string name = string.IsNullOrWhiteSpace(iteration.SampleName)
                    ? $"Iteration {iteration.Index}"
                    : iteration.SampleName;
                _dashboardSnapshotCaption.Text = name;

                List<string> recorded = (iteration.SnapshotFiles ?? new List<string>())
                    .Where(file => !string.IsNullOrWhiteSpace(file))
                    .ToList();
                string path =
                    recorded.FirstOrDefault(System.IO.File.Exists) ??
                    recorded.Select(ResolveMovedSnapshot).FirstOrDefault(file => file != null) ??
                    DiscoverSnapshot(iteration, name);
                if (path == null)
                {
                    ClearDashboardSnapshot();
                    _dashboardSnapshotStatus.Text = recorded.Count == 0
                        ? "No snapshot recorded or found on disk."
                        : "Snapshot file not found: " + System.IO.Path.GetFileName(recorded[0]);
                    return;
                }
                if (string.Equals(_dashboardSnapshotPath, path, StringComparison.OrdinalIgnoreCase) &&
                    _dashboardSnapshotImage.Image != null)
                {
                    return;
                }
                ClearDashboardSnapshot();
                try
                {
                    using var stream = new System.IO.FileStream(
                        path,
                        System.IO.FileMode.Open,
                        System.IO.FileAccess.Read,
                        System.IO.FileShare.ReadWrite);
                    using var loaded = System.Drawing.Image.FromStream(stream);
                    using var snapshot = new System.Drawing.Bitmap(loaded);
                    _dashboardSnapshotImage.Image = ConvertToEtoImage(snapshot);
                    _dashboardSnapshotPath = path;
                    _dashboardSnapshotStatus.Text = System.IO.Path.GetFileName(path);
                }
                catch (Exception exception) when (
                    exception is System.IO.IOException ||
                    exception is UnauthorizedAccessException ||
                    exception is ArgumentException ||
                    exception is OutOfMemoryException)
                {
                    // OutOfMemoryException is what GDI+ throws for a corrupt or non-image file.
                    _dashboardSnapshotStatus.Text = "Snapshot could not be read.";
                }
            }

            private string ResolveMovedSnapshot(string recordedPath)
            {
                string fileName = System.IO.Path.GetFileName(recordedPath ?? string.Empty);
                if (string.IsNullOrWhiteSpace(fileName))
                    return null;
                foreach (string folder in DashboardSnapshotFolders())
                {
                    string candidate = System.IO.Path.Combine(folder, fileName);
                    if (System.IO.File.Exists(candidate))
                        return candidate;
                }
                return null;
            }

            private string DiscoverSnapshot(WasperStudyIteration iteration, string sampleName)
            {
                var candidates = new List<string>();
                if (!string.IsNullOrWhiteSpace(sampleName))
                    candidates.Add(sampleName);
                foreach (string gcode in iteration.GcodeFiles ?? new List<string>())
                {
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(gcode ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(baseName))
                        candidates.Add(baseName);
                }
                foreach (string folder in DashboardSnapshotFolders())
                {
                    foreach (string candidate in candidates)
                    {
                        string exact = System.IO.Path.Combine(folder, candidate + ".png");
                        if (System.IO.File.Exists(exact))
                            return exact;
                        string suffixed = SafeEnumerateFiles(folder, candidate + "_*.png")
                            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                            .FirstOrDefault();
                        if (suffixed != null)
                            return suffixed;
                    }
                    string byIndex = SafeEnumerateFiles(folder, $"*_{iteration.Index + 1:0000}.png")
                        .FirstOrDefault();
                    if (byIndex != null)
                        return byIndex;
                }
                return null;
            }

            private static IEnumerable<string> SafeEnumerateFiles(string folder, string pattern)
            {
                try
                {
                    return System.IO.Directory.Exists(folder)
                        ? System.IO.Directory.EnumerateFiles(folder, pattern)
                        : Enumerable.Empty<string>();
                }
                catch (Exception exception) when (
                    exception is System.IO.IOException ||
                    exception is UnauthorizedAccessException ||
                    exception is ArgumentException)
                {
                    return Enumerable.Empty<string>();
                }
            }

            private IEnumerable<string> DashboardSnapshotFolders()
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(_dashboardSnapshotFolder) &&
                    seen.Add(_dashboardSnapshotFolder))
                {
                    yield return _dashboardSnapshotFolder;
                }
                foreach (WasperStudyIteration iteration in Enumerable.Reverse(_dashboardIterations))
                {
                    foreach (string file in iteration?.SnapshotFiles ?? new List<string>())
                    {
                        if (string.IsNullOrWhiteSpace(file) || !System.IO.File.Exists(file))
                            continue;
                        string folder = System.IO.Path.GetDirectoryName(file);
                        if (!string.IsNullOrWhiteSpace(folder) && seen.Add(folder))
                            yield return folder;
                    }
                }
            }

            private void ClearDashboardSnapshot()
            {
                Image previous = _dashboardSnapshotImage.Image;
                _dashboardSnapshotImage.Image = null;
                previous?.Dispose();
                _dashboardSnapshotPath = string.Empty;
            }

            private static void DisposeImage(ImageView host)
            {
                Image previous = host?.Image;
                if (host != null)
                    host.Image = null;
                previous?.Dispose();
            }

            private static void DisposeImage(DashboardChartView host)
            {
                host?.ReplaceImage(null, Size.Empty);
            }

            private void DisposeViewResources()
            {
                DisposeDashboardResults();
                DisposeImage(_snapshotPreview);
                foreach (Image image in _mobileQrImages)
                    image?.Dispose();
                _mobileQrImages.Clear();
            }

            /// <summary>Disposes every dashboard render result and converted Eto image.</summary>
            private void DisposeDashboardResults()
            {
                DisposeImage(_dashboardHistoryImage);
                DisposeImage(_dashboardScatterImage);
                DisposeImage(_dashboardHeatmapImage);
                DisposeImage(_dashboardParallelImage);
                DisposeImage(_dashboardHistogramImage);
                _dashboardHistoryResult?.Dispose();
                _dashboardScatterResult?.Dispose();
                _dashboardHeatmapResult?.Dispose();
                _dashboardParallelResult?.Dispose();
                _dashboardHistogramResult?.Dispose();
                _dashboardHistoryResult = null;
                _dashboardScatterResult = null;
                _dashboardHeatmapResult = null;
                _dashboardParallelResult = null;
                _dashboardHistogramResult = null;
                ClearDashboardSnapshot();
            }

            private Control CreateProcessViewerTab()
            {
                _processViewerRestore = CommandButton("Restore Selected", 110);
                _processViewerRestore.Enabled = false;
                _processViewerRestore.ToolTip = "Restores the Dashboard tab's selected " +
                    "individual's slider values, which recomputes the definition. Select a " +
                    "point on a Dashboard chart first.";
                _processViewerRestore.Click += (_, _) =>
                {
                    if (_dashboardSelection.PrimaryId.HasValue)
                        ShowIterationRequested?.Invoke(_dashboardSelection.PrimaryId.Value);
                };
                // _dashboardSelection now exists (Dashboard tab migrated) -- keep this button's
                // label/enabled state in sync with it the same way WASPer_Sm01ProcessViewerTab.cs'
                // UpdateProcessViewerSelection does, without reproducing the grid cross-link half of
                // WinForms' DashboardLinkedSelectionChanged (dropped, see the Dashboard field-region
                // comment).
                _dashboardSelection.SelectionChanged += (_, _) =>
                {
                    SelectIterationGridRow(_dashboardSelection.PrimaryId);
                    UpdateProcessViewerSelection();
                };
                _processViewerBrowse = CommandButton("Browse...");
                _processViewerExport = CommandButton("Export / Update", 110);
                _processViewerExport.Enabled = false;
                _processViewerExport.ToolTip = "Optional: writes a standalone .wasperxr file to " +
                    "the folder above for offline/later use. The web viewer server starts and " +
                    "stays updated on its own -- this isn't required first.";
                _processViewerOpenBrowser = CommandButton("Open in Browser", 110);
                _processViewerOpenBrowser.Enabled = false;
                _processViewerRefresh = CommandButton("Refresh");
                _processViewerRefresh.ToolTip = "Pings the current XR scene: detects available " +
                    "network addresses and QR links again, ensures the WebViewer server is " +
                    "running, pushes the latest scene, and refreshes the signed-session " +
                    "connection status.";
                _processViewerLiveToggle = CommandButton("Live: On", 90);
                _processViewerLiveToggle.ToolTip = "When on (default), the viewer keeps " +
                    "streaming the current wsp_path live as it changes. Turn off to pause " +
                    "automatic updates and use Push Change for manual one-off pushes instead.";
                _processViewerPushChange = CommandButton("Push Change", 100);
                _processViewerPushChange.Enabled = false;
                _processViewerPushChange.ToolTip = "Manually pushes the current wsp_path to the " +
                    "viewer once. Only enabled while Live is off.";
                _processViewerOpenFolder = CommandButton("Open Folder", 100);
                _processViewerOpenFolder.Enabled = false;
                _processViewerDownloadGuide = CommandButton("Download Guide...", 120);
                _processViewerDownloadGuide.ToolTip = "Saves a local copy of the Process Viewer " +
                    "feature, workflow, technical, dependency-install, and Android AR setup guide.";
                _localCopyLink = CommandButton("Copy Link", 90);

                _processViewerFolder.TextChanged += (_, _) =>
                {
                    if (!_updatingProcessViewer)
                        _processViewerFolderEdited = true;
                };
                _processViewerJobId.TextChanged += (_, _) =>
                {
                    if (!_updatingProcessViewer)
                        _processViewerJobIdEdited = true;
                };
                _processViewerBrowse.Click += BrowseProcessViewerFolder;
                _processViewerExport.Click += (_, _) => ProcessViewerExportRequested?.Invoke(
                    _processViewerFolder.Text.Trim(),
                    _processViewerJobId.Text.Trim());
                _processViewerOpenBrowser.Click += (_, _) =>
                    ProcessViewerOpenBrowserRequested?.Invoke(_processViewerJsonPath);
                _processViewerRefresh.Click += (_, _) => ProcessViewerRefreshRequested?.Invoke();
                _processViewerLiveToggle.Click += (_, _) =>
                {
                    _liveToggleOn = !_liveToggleOn;
                    ApplyLiveToggleVisual();
                    ProcessViewerLiveToggleChanged?.Invoke(_liveToggleOn);
                };
                _processViewerPushChange.Click += (_, _) => ProcessViewerPushChangeRequested?.Invoke();
                _processViewerOpenFolder.Click += (_, _) =>
                    ProcessViewerOpenFolderRequested?.Invoke(_processViewerFolder.Text.Trim());
                _processViewerDownloadGuide.Click += DownloadProcessViewerGuide;
                _localCopyLink.Click += (_, _) => CopyProcessViewerLink(_localAccessUrl.Text);
                ApplyLiveToggleVisual();

                var selectedRow = new TableLayout
                {
                    Spacing = new Size(6, 0),
                    Rows =
                    {
                        new TableRow(
                            new TableCell(_processViewerSelection, true),
                            new TableCell(_processViewerRestore, false))
                    }
                };
                var contextGroup = new GroupBox
                {
                    Text = "Current fabrication plan",
                    Padding = new Padding(8),
                    Content = new TableLayout
                    {
                        Spacing = new Size(6, 4),
                        Rows =
                        {
                            new TableRow(LabelCell("Solution:"), new TableCell(_processViewerSample, true)),
                            new TableRow(LabelCell("Selected:"), new TableCell(selectedRow, true)),
                            new TableRow(LabelCell("Path:"), new TableCell(_processViewerPathState, true)),
                            new TableRow(LabelCell("Package:"), new TableCell(_processViewerJobState, true)),
                            new TableRow(
                                LabelCell("Application:"),
                                new TableCell(_processViewerAppState, true)),
                            new TableRow(
                                LabelCell("Live viewers:"),
                                new TableCell(_processViewerLiveStatus, true))
                        }
                    }
                };

                var folderRow = new TableLayout
                {
                    Spacing = new Size(4, 0),
                    Rows =
                    {
                        new TableRow(
                            new TableCell(_processViewerFolder, true),
                            new TableCell(_processViewerBrowse, false))
                    }
                };
                var actionsBar = new TableLayout
                {
                    Spacing = new Size(4, 4),
                    Rows =
                    {
                        new TableRow(
                            new TableCell(_processViewerOpenBrowser, false),
                            new TableCell(_processViewerRefresh, false),
                            new TableCell(_processViewerLiveToggle, false),
                            new TableCell(_processViewerPushChange, false),
                            new TableCell(_processViewerOpenFolder, false),
                            new TableCell(_processViewerDownloadGuide, false),
                            new TableCell(_processViewerExport, false),
                            new TableCell(null, true))
                    }
                };
                var localLinkRow = new TableLayout
                {
                    Spacing = new Size(4, 0),
                    Rows =
                    {
                        new TableRow(
                            new TableCell(_localAccessUrl, true),
                            new TableCell(_localCopyLink, false))
                    }
                };
                var packageGroup = new GroupBox
                {
                    Text = "External viewer package",
                    Padding = new Padding(8),
                    Content = new TableLayout
                    {
                        Spacing = new Size(0, 6),
                        Rows =
                        {
                            new TableRow(LabelCell("Folder:"), new TableCell(folderRow, true)),
                            new TableRow(LabelCell("Job name:"), new TableCell(_processViewerJobId, true)),
                            new TableRow(new TableCell(actionsBar)) { ScaleHeight = false },
                            new TableRow(new TableCell(_processViewerStatus)) { ScaleHeight = false },
                            new TableRow(new TableCell(_localAccessCaption)) { ScaleHeight = false },
                            new TableRow(new TableCell(localLinkRow)) { ScaleHeight = false }
                        }
                    }
                };

                var mobileGroup = new GroupBox
                {
                    Text = "Mobile Access (QR code)",
                    Padding = new Padding(8),
                    Content = new TableLayout
                    {
                        Spacing = new Size(0, 6),
                        Rows =
                        {
                            new TableRow(new TableCell(_mobileAccessStatus)) { ScaleHeight = false },
                            new TableRow(new TableCell(_mobileAccessContainer)) { ScaleHeight = false }
                        }
                    }
                };
                var content = new TableLayout
                {
                    Padding = new Padding(10),
                    Spacing = new Size(0, 10),
                    Rows =
                    {
                        new TableRow(new TableCell(contextGroup)) { ScaleHeight = false },
                        new TableRow(new TableCell(packageGroup)) { ScaleHeight = false },
                        new TableRow(new TableCell(mobileGroup, true)) { ScaleHeight = false },
                        new TableRow(new TableCell(null, true)) { ScaleHeight = true }
                    }
                };
                return new Scrollable { Content = content };
            }

            private void BrowseProcessViewerFolder(object sender, EventArgs eventArgs)
            {
                using var dialog = new Eto.Forms.SelectFolderDialog
                {
                    Title = "Select the WASPer Process Viewer job folder"
                };
                if (System.IO.Directory.Exists(_processViewerFolder.Text))
                    dialog.Directory = _processViewerFolder.Text;
                if (dialog.ShowDialog(Sm01DialogOwner()) != DialogResult.Ok)
                    return;
                _processViewerFolderEdited = true;
                _processViewerFolder.Text = dialog.Directory;
                _processViewerOpenFolder.Enabled = true;
            }

            private void DownloadProcessViewerGuide(object sender, EventArgs eventArgs)
            {
                const string resourceName =
                    "WASPer_3DP.Resources.Documentation.WASPER_PROCESS_VIEWER_GUIDE.md";
                string initialFolder = System.IO.Directory.Exists(_processViewerFolder.Text)
                    ? _processViewerFolder.Text
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                // Fully qualified -- see SaveGcodeClicked's comment on the CS0104 ambiguity
                // between Eto.Forms.SaveFileDialog and Rhino.UI.SaveFileDialog the maintainer's
                // build already caught once for this exact pattern.
                using var dialog = new Eto.Forms.SaveFileDialog
                {
                    FileName = "WASPer_Process_Viewer_Guide.md",
                    Directory = new Uri(initialFolder),
                    Title = "Save WASPer Process Viewer Guide"
                };
                dialog.Filters.Add(new FileFilter("Markdown document (*.md)", ".md"));
                dialog.Filters.Add(new FileFilter("Text document (*.txt)", ".txt"));
                dialog.Filters.Add(new FileFilter("All files", ".*"));
                if (dialog.ShowDialog(Sm01DialogOwner()) != DialogResult.Ok)
                    return;

                try
                {
                    System.Reflection.Assembly assembly = typeof(wsp_Sm01_WASPer_Study_Manager).Assembly;
                    using System.IO.Stream source = assembly.GetManifestResourceStream(resourceName);
                    if (source == null)
                    {
                        throw new InvalidOperationException(
                            "The embedded Process Viewer guide was not found.");
                    }
                    using var destination = new System.IO.FileStream(
                        dialog.FileName,
                        System.IO.FileMode.Create,
                        System.IO.FileAccess.Write,
                        System.IO.FileShare.None);
                    source.CopyTo(destination);
                    _processViewerStatus.Text = "Process Viewer guide saved to " + dialog.FileName;
                }
                catch (Exception exception)
                {
                    _processViewerStatus.Text = "Could not save the Process Viewer guide: " +
                        exception.Message;
                    ShowSm01Warning(_processViewerStatus.Text, "WASPer Process Viewer");
                }
            }

            /// <summary>Unverified: Eto.Forms.Clipboard has no in-repo precedent.</summary>
            private static void CopyProcessViewerLink(string text)
            {
                if (string.IsNullOrWhiteSpace(text))
                    return;
                Clipboard.Instance.Text = text;
            }

            public void UpdateProcessViewer(
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
                string webViewerRuntimeStatus)
            {
                _updatingProcessViewer = true;
                try
                {
                    _processViewerRuntimeReady = webViewerRuntimeAvailable;
                    _processViewerRuntimeStatus = string.IsNullOrWhiteSpace(webViewerRuntimeStatus)
                        ? "Web viewer runtime status unknown."
                        : webViewerRuntimeStatus;
                    _localAccessUrl.Text = localViewerUrl ?? string.Empty;
                    if (!_processViewerFolderEdited)
                        _processViewerFolder.Text = defaultFolder ?? string.Empty;
                    if (!_processViewerJobIdEdited)
                        _processViewerJobId.Text = defaultJobId ?? string.Empty;
                }
                finally
                {
                    _updatingProcessViewer = false;
                }

                _processViewerSample.Text = string.IsNullOrWhiteSpace(sampleName)
                    ? "Current solution"
                    : sampleName;
                _processViewerPathState.Text = !hasPath
                    ? "No wsp_path connected"
                    : hasMotionPlan
                        ? $"Ready - {pathBranches} branch(es), {motions} motion(s)"
                        : $"{pathBranches} branch(es) - motion plan required";
                _processViewerJsonPath = System.IO.File.Exists(jsonPath) ? jsonPath : string.Empty;
                _processViewerJobState.Text = string.IsNullOrWhiteSpace(_processViewerJsonPath)
                    ? "No package exported"
                    : _processViewerJsonPath;
                // EnableVvvvViewerButton stays false here (see class doc / tab header comment) --
                // the vvvv branch of the WinForms text is never reachable, so it is dropped.
                _processViewerAppState.Text = !_processViewerRuntimeReady
                    ? _processViewerRuntimeStatus
                    : "Browser WebViewer ready. vvvv viewer not available in this Eto build.";
                _processViewerExport.Enabled = hasMotionPlan;
                _processViewerCanGoLive = hasMotionPlan;
                _processViewerOpenBrowser.Enabled = _processViewerRuntimeReady &&
                    (_processViewerCanGoLive || !string.IsNullOrWhiteSpace(_processViewerJsonPath));
                _processViewerOpenFolder.Enabled = System.IO.Directory.Exists(_processViewerFolder.Text);
                ApplyLiveToggleVisual();
                ApplyRuntimeVisual();
            }

            public void SetProcessViewerResult(
                string jsonPath,
                string status,
                bool viewerAvailable,
                bool webViewerRuntimeAvailable,
                string webViewerRuntimeStatus)
            {
                _processViewerRuntimeReady = webViewerRuntimeAvailable;
                _processViewerRuntimeStatus = string.IsNullOrWhiteSpace(webViewerRuntimeStatus)
                    ? "Web viewer runtime status unknown."
                    : webViewerRuntimeStatus;
                _processViewerJsonPath = System.IO.File.Exists(jsonPath) ? jsonPath : string.Empty;
                _processViewerJobState.Text = string.IsNullOrWhiteSpace(_processViewerJsonPath)
                    ? "No package exported"
                    : _processViewerJsonPath;
                _processViewerStatus.Text = status ?? string.Empty;
                _processViewerOpenBrowser.Enabled = _processViewerRuntimeReady &&
                    (_processViewerCanGoLive || !string.IsNullOrWhiteSpace(_processViewerJsonPath));
                _processViewerOpenFolder.Enabled = System.IO.Directory.Exists(_processViewerFolder.Text);
                ApplyLiveToggleVisual();
                ApplyRuntimeVisual();
            }

            /// <summary>
            /// Keeps the Live button's text and Push Change's enabled state in sync with
            /// _liveToggleOn -- called after every toggle click and after both
            /// UpdateProcessViewer/SetProcessViewerResult so a newly-arrived wsp_path/export
            /// immediately reflects the current Live state. Unlike WinForms' ApplyLiveToggleVisual,
            /// there is no BackColor to update (see tab header comment).
            /// </summary>
            private void ApplyLiveToggleVisual()
            {
                if (_processViewerLiveToggle == null)
                    return;
                _processViewerLiveToggle.Text = _liveToggleOn ? "Live: On" : "Live: Off";
                if (_processViewerPushChange != null)
                {
                    _processViewerPushChange.Enabled = !_liveToggleOn &&
                        _processViewerRuntimeReady &&
                        (_processViewerCanGoLive || !string.IsNullOrWhiteSpace(_processViewerJsonPath));
                }
            }

            private void ApplyRuntimeVisual()
            {
                if (_processViewerOpenBrowser == null)
                    return;
                if (_processViewerRuntimeReady)
                {
                    _processViewerOpenBrowser.Text = "Open in Browser";
                    _processViewerOpenBrowser.ToolTip = "Opens the browser viewer. The server " +
                        "starts on its own as soon as there's something to view, so this is just " +
                        "a shortcut to look at it locally.";
                    return;
                }
                _processViewerOpenBrowser.Text = "Install .NET";
                _processViewerOpenBrowser.ToolTip =
                    _processViewerRuntimeStatus + " Save the Process Viewer guide for install steps.";
            }

            /// <summary>
            /// Keeps the "Restore Selected" row in sync with the Dashboard tab's selection --
            /// mirrors WASPer_Sm01ProcessViewerTab.cs' UpdateProcessViewerSelection.
            /// </summary>
            private void UpdateProcessViewerSelection()
            {
                if (!_dashboardSelection.PrimaryId.HasValue)
                {
                    _processViewerSelection.Text = "No individual selected";
                    _processViewerRestore.Enabled = false;
                    return;
                }
                int id = _dashboardSelection.PrimaryId.Value;
                WasperStudyIteration iteration = _dashboardIterations.FirstOrDefault(item => item.Index == id);
                _processViewerSelection.Text = iteration == null || string.IsNullOrWhiteSpace(iteration.SampleName)
                    ? $"Iteration {id}"
                    : $"{id}: {iteration.SampleName}";
                _processViewerRestore.Enabled = iteration != null;
            }

            public void SetLiveToggleState(bool enabled)
            {
                if (_liveToggleOn == enabled)
                    return;
                _liveToggleOn = enabled;
                ApplyLiveToggleVisual();
            }

            public void SetLiveViewerStatus(string text)
            {
                _processViewerLiveStatus.Text = string.IsNullOrWhiteSpace(text)
                    ? "Not connected"
                    : text;
            }

            public void UpdateMobileAccess(IReadOnlyList<MobileAccessLink> links, string status)
            {
                foreach (Image image in _mobileQrImages)
                    image?.Dispose();
                _mobileQrImages.Clear();
                _mobileAccessCards.Clear();
                foreach (MobileAccessLink link in links ?? Array.Empty<MobileAccessLink>())
                    _mobileAccessCards.Add(BuildMobileAccessEntry(link));
                RebuildMobileAccessLayout(force: true);
                if (!string.IsNullOrWhiteSpace(status))
                    _mobileAccessStatus.Text = status;
            }

            private void RebuildMobileAccessLayout(bool force = false)
            {
                int availableWidth = Math.Max(350, ClientSize.Width - 80);
                int columns = Math.Max(1, Math.Min(_mobileAccessCards.Count, availableWidth / 350));
                if (!force && columns == _mobileAccessColumns)
                    return;

                _mobileAccessColumns = columns;
                _mobileAccessContainer.Clear();
                for (int index = 0; index < _mobileAccessCards.Count; index += columns)
                {
                    var row = new Control[columns];
                    for (int column = 0; column < columns; column++)
                    {
                        int cardIndex = index + column;
                        row[column] = cardIndex < _mobileAccessCards.Count
                            ? _mobileAccessCards[cardIndex]
                            : null;
                    }
                    _mobileAccessContainer.AddSeparateRow(
                        padding: null,
                        spacing: new Size(10, 10),
                        xscale: false,
                        yscale: false,
                        controls: row);
                }
                _mobileAccessContainer.Create();
            }

            /// <summary>
            /// One QR code + label + copyable URL, for one candidate LAN address. The QR bitmap
            /// crosses the same Eto/System.Drawing conversion boundary as the Export tab's snapshot
            /// preview (see ConvertToEtoImage) -- MobileAccessLink.Qr is a System.Drawing.Bitmap
            /// (WASPer_Sm01MobileAccess.cs, which uses QRCoder). Converted copies are tracked in
            /// _mobileQrImages purely so UpdateMobileAccess can dispose them on the next rebuild,
            /// mirroring WinForms' own PictureBox.Image ownership-transfer pattern here.
            /// </summary>
            private Control BuildMobileAccessEntry(MobileAccessLink link)
            {
                Image qrImage;
                try
                {
                    qrImage = ConvertToEtoImage(link.Qr);
                }
                finally
                {
                    link.Qr?.Dispose();
                }
                if (qrImage != null)
                    _mobileQrImages.Add(qrImage);

                var qrView = new ImageView { Image = qrImage, Size = new Size(130, 130) };
                var caption = new Label
                {
                    // Unverified: FontStyle.Bold combined with FontFamilies.Sans has no in-repo
                    // precedent (only plain FontFamilies.Sans/.Monospace do -- see class doc).
                    Font = new Font(FontFamilies.Sans, 9f, FontStyle.Bold),
                    Text = string.IsNullOrWhiteSpace(link.Label) ? "Network" : link.Label
                };
                var urlBox = new TextBox { ReadOnly = true, Text = link.Url, Width = 300 };
                var copyButton = CommandButton("Copy Link", 90);
                copyButton.Click += (_, _) => CopyProcessViewerLink(link.Url);

                var qrRow = new StackLayout
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    Items = { qrView }
                };
                return new GroupBox
                {
                    Text = string.IsNullOrWhiteSpace(link.Label) ? "Network" : link.Label,
                    Padding = new Padding(8),
                    Width = 340,
                    Content = new TableLayout
                    {
                        Spacing = new Size(0, 4),
                        Rows =
                        {
                            new TableRow(new TableCell(qrRow, true)) { ScaleHeight = false },
                            new TableRow(new TableCell(caption, false)) { ScaleHeight = false },
                            new TableRow(new TableCell(urlBox, false)) { ScaleHeight = false },
                            new TableRow(
                                new TableCell(copyButton, false),
                                new TableCell(null, true)) { ScaleHeight = false }
                        }
                    }
                };
            }

            /// <summary>
            /// The "Dump Full Study" section is parked here the same way it currently is in
            /// WinForms (IncludeDumpFullStudySection = false in WASPer_Sm01ProcessViewerTab.cs) --
            /// not a migration gap, a matching feature-flag state. DumpFullStudyRequested /
            /// DumpStudyOpenFolderRequested stay declared but unraised until that section is turned
            /// back on in both views.
            /// </summary>
            public void UpdateDumpStudySection(string defaultFolder, string defaultName, bool canBuild)
            {
                // Intentionally no-op -- see method doc.
            }

            public void SetDumpStudyResult(string status)
            {
                // Intentionally no-op -- see method doc.
            }

            // ----- Report tab (migrated) -------------------------------------------------------

            private Control CreateReportTab()
            {
                var fields = new DynamicLayout
                {
                    Padding = new Padding(18),
                    Spacing = new Size(10, 8)
                };
                fields.AddRow(new Label { Text = "Report title", VerticalAlignment = VerticalAlignment.Center }, _reportTitle);
                fields.AddRow(new Label { Text = "Subtitle", VerticalAlignment = VerticalAlignment.Center }, _reportSubtitle);
                fields.AddRow(new Label { Text = "Page size", VerticalAlignment = VerticalAlignment.Center }, _reportPageSize);
                fields.AddRow(new Label { Text = "Orientation", VerticalAlignment = VerticalAlignment.Center }, _reportOrientation);
                fields.AddRow(_reportSnapshot);
                fields.AddRow(_reportIterations);

                var generate = CommandButton("Generate PDF");
                generate.Click += (_, _) => GenerateReportRequested?.Invoke(ReadReportSettings());

                var help = new Label
                {
                    Text = "Creates a native PDF in WASPer_<run name>\\Reports. The report summarizes " +
                        "the study, enabled KPI groups, and an optional preview of captured iterations. " +
                        "Full iteration data remains available through the CSV, Excel, and JSON exports.",
                    Wrap = WrapMode.Word
                };

                fields.AddRow(generate);
                fields.AddRow(help);
                fields.AddRow(_reportStatus);
                fields.AddSpace();

                _reportTitle.LostFocus += (_, _) => ReportSettingChanged();
                _reportSubtitle.LostFocus += (_, _) => ReportSettingChanged();
                _reportPageSize.SelectedIndexChanged += (_, _) => ReportSettingChanged();
                _reportOrientation.SelectedIndexChanged += (_, _) => ReportSettingChanged();
                _reportSnapshot.CheckedChanged += (_, _) => ReportSettingChanged();
                _reportIterations.CheckedChanged += (_, _) => ReportSettingChanged();

                return new Scrollable { Content = fields };
            }

            private WasperReportSettings ReadReportSettings()
            {
                return new WasperReportSettings
                {
                    Title = string.IsNullOrWhiteSpace(_reportTitle.Text)
                        ? "WASPer Study Report"
                        : _reportTitle.Text.Trim(),
                    Subtitle = _reportSubtitle.Text?.Trim() ?? string.Empty,
                    PageSize = _reportPageSize.SelectedIndex >= 0
                        ? ReportPageSizes[_reportPageSize.SelectedIndex]
                        : "A4",
                    Landscape = _reportOrientation.SelectedIndex == 1,
                    IncludeSnapshot = _reportSnapshot.Checked == true,
                    IncludeIterationTable = _reportIterations.Checked == true
                };
            }

            private void ReportSettingChanged()
            {
                if (!_updatingReportControls)
                    ReportSettingsChanged?.Invoke(ReadReportSettings());
            }

            public void UpdateReport(WasperReportSettings settings, string status)
            {
                settings ??= new WasperReportSettings();
                _updatingReportControls = true;
                if (!_reportTitle.HasFocus)
                    _reportTitle.Text = settings.Title ?? "WASPer Study Report";
                if (!_reportSubtitle.HasFocus)
                    _reportSubtitle.Text = settings.Subtitle ?? string.Empty;
                int pageSizeIndex = Array.IndexOf(ReportPageSizes, settings.PageSize ?? "A4");
                _reportPageSize.SelectedIndex = pageSizeIndex >= 0 ? pageSizeIndex : 0;
                _reportOrientation.SelectedIndex = settings.Landscape ? 1 : 0;
                _reportSnapshot.Checked = settings.IncludeSnapshot;
                _reportIterations.Checked = settings.IncludeIterationTable;
                _reportStatus.Text = status ?? string.Empty;
                _updatingReportControls = false;
            }

            // ----- G-code tab (migrated) -------------------------------------------------------

            private Control CreateGcodeTab()
            {
                var header = new TableLayout
                {
                    Padding = new Padding(8, 6, 8, 4),
                    Spacing = new Size(8, 0),
                    Rows =
                    {
                        new TableRow(
                            new TableCell(new Label
                            {
                                Text = "Input branch",
                                VerticalAlignment = VerticalAlignment.Center
                            }, false),
                            new TableCell(_gcodeBranch, false),
                            new TableCell(_saveGcode, false),
                            new TableCell(null, true))
                    }
                };

                var viewerGroup = new GroupBox { Text = "Current recomputed G-code", Padding = new Padding(8) };
                viewerGroup.Content = _gcodeViewer;

                var footer = new TableLayout
                {
                    Padding = new Padding(9, 3, 9, 3),
                    Rows = { new TableRow(new TableCell(_gcodeStatus, true)) }
                };

                _gcodeBranch.SelectedIndexChanged += (_, _) => RenderGcodeBranch();
                _saveGcode.Click += SaveGcodeClicked;

                return new TableLayout
                {
                    Rows =
                    {
                        new TableRow(new TableCell(header, true)) { ScaleHeight = false },
                        new TableRow(new TableCell(viewerGroup, true)) { ScaleHeight = true },
                        new TableRow(new TableCell(footer, true)) { ScaleHeight = false }
                    }
                };
            }

            public void UpdateGcode(IEnumerable<List<string>> branches, IEnumerable<string> capturedFiles)
            {
                int selected = Math.Max(0, _gcodeBranch.SelectedIndex);
                _displayedGcodeBranches = (branches ?? Enumerable.Empty<List<string>>())
                    .Select(branch => branch?.ToList() ?? new List<string>())
                    .ToList();

                var items = new List<string>();
                for (int index = 0; index < _displayedGcodeBranches.Count; index++)
                    items.Add($"Branch {index} ({_displayedGcodeBranches[index].Count:N0} lines)");
                _gcodeBranch.DataStore = items;
                if (items.Count > 0)
                    _gcodeBranch.SelectedIndex = Math.Min(selected, items.Count - 1);
                // Always refresh, even when the selected index is unchanged: the branch at that index
                // may have new content after a Grasshopper recompute, and the Save button's enabled
                // state has to track it either way.
                RenderGcodeBranch();

                List<string> files = (capturedFiles ?? Enumerable.Empty<string>()).ToList();
                string capture = files.Count == 0
                    ? "No iteration G-code has been captured yet."
                    : $"Latest iteration: {files.Count} G-code file(s) saved in " +
                        $"{System.IO.Path.GetDirectoryName(files[0])}.";
                _gcodeStatus.Text = _displayedGcodeBranches.Count == 0
                    ? "Connect Gc03 g_code to the Sm01 gcode input. " + capture
                    : $"{_displayedGcodeBranches.Count} input branch(es). " + capture;
            }

            private void RenderGcodeBranch()
            {
                int index = _gcodeBranch.SelectedIndex;
                if (index < 0 || index >= _displayedGcodeBranches.Count)
                {
                    _gcodeViewer.Text = string.Empty;
                    _saveGcode.Enabled = false;
                    return;
                }
                _saveGcode.Enabled = _displayedGcodeBranches[index]
                    .Any(line => !string.IsNullOrWhiteSpace(line));

                const int maximumLines = 20000;
                const int maximumCharacters = 2000000;
                var builder = new System.Text.StringBuilder();
                int displayedLines = 0;
                foreach (string line in _displayedGcodeBranches[index])
                {
                    if (displayedLines >= maximumLines ||
                        builder.Length + (line?.Length ?? 0) + Environment.NewLine.Length > maximumCharacters)
                    {
                        builder.AppendLine("; Preview truncated to keep the manager responsive.");
                        break;
                    }
                    builder.AppendLine(line ?? string.Empty);
                    displayedLines++;
                }
                _gcodeViewer.Text = builder.ToString();
            }

            /// <summary>
            /// Manual, on-demand save of the branch currently shown in the viewer -- independent of the
            /// automatic per-iteration G-code capture used while a study runs.
            ///
            /// The migrated Export tab's current output folder seeds the dialog when it exists.
            /// </summary>
            private void SaveGcodeClicked(object sender, EventArgs eventArgs)
            {
                int index = _gcodeBranch.SelectedIndex;
                if (index < 0 || index >= _displayedGcodeBranches.Count)
                    return;
                List<string> lines = _displayedGcodeBranches[index];
                if (lines == null || !lines.Any(line => !string.IsNullOrWhiteSpace(line)))
                {
                    _gcodeStatus.Text = "Nothing to save: the selected branch has no G-code lines.";
                    return;
                }

                string suggestedName = _gcodeBranch.DataStore is ICollection<string> branchNames &&
                    branchNames.Count > 1
                    ? $"Gcode_Branch{index + 1}.gcode"
                    : "Gcode.gcode";
                // Fully qualified: Rhino.UI (imported for RhinoEtoApp/RhinoDoc elsewhere in this
                // file) also declares a SaveFileDialog, which made the unqualified name ambiguous
                // (CS0104, caught by the maintainer's 2026-08-29 build).
                using var dialog = new Eto.Forms.SaveFileDialog
                {
                    FileName = suggestedName,
                    Title = "Save G-code"
                };
                string exportFolder = ExportFilePathValue();
                if (System.IO.Directory.Exists(exportFolder))
                    dialog.Directory = new Uri(exportFolder);
                dialog.Filters.Add(new FileFilter("G-code files (*.gcode)", ".gcode"));
                dialog.Filters.Add(new FileFilter("All files", ".*"));
                if (dialog.ShowDialog(Sm01DialogOwner()) != DialogResult.Ok)
                    return;

                try
                {
                    System.IO.File.WriteAllLines(dialog.FileName, lines, new System.Text.UTF8Encoding(false));
                    _gcodeStatus.Text = $"Saved G-code to {dialog.FileName}.";
                }
                catch (Exception exception)
                {
                    string message = "G-code save failed: " + exception.Message;
                    _gcodeStatus.Text = message;
                    ShowSm01Error(message, "Save G-code");
                }
            }
        }
    }
}
