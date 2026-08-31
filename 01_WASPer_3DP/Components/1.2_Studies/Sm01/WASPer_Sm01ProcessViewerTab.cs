using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace WASPer_3DP.Components._1_2_Studies
{
    public sealed partial class wsp_Sm01_WASPer_Study_Manager
    {
        private sealed partial class KpiManagerForm
        {
            // Temporarily off per 2026-08-19 scope pullback ("standalone and dashboard can wait,
            // let's focus on the XR live-link plan") -- flip back to true to restore the UI. All
            // the section's fields/wiring/controller methods are left in place, just not attached
            // to the visible panel, so re-enabling is a one-line change.
            private static readonly bool IncludeDumpFullStudySection = false;

            // Temporarily off, same 2026-08-19 scope pullback: the vvvv (Gamma) native viewer path
            // is parked in favor of the browser viewer + a live-link, so this button stays disabled
            // regardless of whether vvvv/the patch are actually found. Flip back to true (or just
            // delete this override) to restore it to the old viewerAvailable-driven behavior.
            private static readonly bool EnableVvvvViewerButton = false;

            private readonly Label _processViewerSample = ProcessViewerValueLabel("Current solution");
            private readonly Label _processViewerSelection = ProcessViewerValueLabel("No individual selected");
            private readonly Label _processViewerPathState = ProcessViewerValueLabel("No wsp_path connected");
            private readonly Label _processViewerJobState = ProcessViewerValueLabel("No package exported");
            private readonly Label _processViewerAppState = ProcessViewerValueLabel("Viewer unavailable");
            // M5's closing deliverable, "Viewer status" (2026-08-19): whether a browser is
            // actually connected to the live push, polled from /live/status -- see
            // TryRefreshLiveViewerStatus in WASPer_Sm01ProcessViewerController.cs.
            private readonly Label _processViewerLiveStatus = ProcessViewerValueLabel("Not connected");
            private readonly TextBox _processViewerFolder = new TextBox
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Width = 720
            };
            private readonly TextBox _processViewerJobId = new TextBox
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Width = 360
            };
            private readonly Button _processViewerBrowse = new Button
            {
                AutoSize = true,
                Text = "Browse..."
            };
            private readonly Button _processViewerRestore = new Button
            {
                AutoSize = true,
                Enabled = false,
                Text = "Restore Selected"
            };
            // Secondary/optional now that the web viewer is live-linked (see
            // _processViewerOpenBrowser below) -- writes a standalone .wasperxr file for
            // offline/later use, but opening the live viewer no longer depends on it.
            private readonly Button _processViewerExport = new Button
            {
                AutoSize = true,
                Enabled = false,
                Text = "Export / Update"
            };
            private readonly Button _processViewerLaunch = new Button
            {
                AutoSize = true,
                Enabled = false,
                Text = "Open Viewer (vvvv)"
            };
            // Plain local-convenience action now (2026-08-19 rework): the web viewer server
            // starts on its own as soon as there's something to view (see
            // EnsureWebViewerServerRunning in WASPer_Sm01ProcessViewerController.cs), so this
            // button no longer needs to read as a call-to-action -- it just opens a browser tab
            // pointed at a server that's very likely already running. Renamed from the old
            // "Open Web Viewer" (which both started the server AND opened the tab, and was the
            // sole trigger for live mode -- the source of the ERR_CONNECTION_TIMED_OUT confusion
            // this rework fixes: a phone scanning the QR code before this button was ever clicked
            // got a server that had never been asked to start).
            private readonly Button _processViewerOpenBrowser = new Button
            {
                AutoSize = true,
                Enabled = false,
                Text = "Open in Browser"
            };
            private readonly Button _processViewerRefresh = new Button
            {
                AutoSize = true,
                Text = "Refresh"
            };
            // "Live" toggle (2026-08-19), on by default -- purely controls whether
            // UpdateProcessViewerWindow's automatic per-solve live push/status-poll happens (see
            // _liveEnabled in the controller). Turning it off doesn't stop the server or
            // disconnect anyone already viewing, just pauses the automatic push; Push Change
            // below is the manual alternative while it's off. Green fill when on, matching the
            // "this is actively happening" affordance the old bold/accent Open Web Viewer button
            // used to carry; plain when off so it doesn't compete visually with Push Change.
            private readonly Button _processViewerLiveToggle = new Button
            {
                AutoSize = true,
                BackColor = Color.FromArgb(210, 240, 210),
                Text = "Live: On",
                UseVisualStyleBackColor = false
            };
            // Manual one-off push, enabled only while Live is off (see ApplyLiveToggleVisual) --
            // bypasses TryPushLiveUpdate's _liveEnabled gate via PushChangeNow(manual: true) in
            // the controller.
            private readonly Button _processViewerPushChange = new Button
            {
                AutoSize = true,
                Enabled = false,
                Text = "Push Change"
            };
            private bool _liveToggleOn = true;
            private readonly Button _processViewerOpenFolder = new Button
            {
                AutoSize = true,
                Enabled = false,
                Text = "Open Folder"
            };
            private readonly Button _processViewerDownloadGuide = new Button
            {
                AutoSize = true,
                Text = "Download Guide..."
            };
            private readonly Label _processViewerStatus = new Label
            {
                // Height set explicitly (rather than relying on AutoSize) because this label
                // sits in an AutoSize TableLayoutPanel row -- with AutoSize left false, a bare
                // Label's default ~23px single-line height is what the row sizes itself to,
                // which clipped longer status messages (e.g. the launcher-failure text surfaced
                // 2026-08-19) to one line with "..." even though the label itself wraps fine.
                // ~3 lines' worth of room here; AutoEllipsis stays on as a safety net beyond that.
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(80, 80, 80),
                Height = 54,
                Padding = new Padding(0, 6, 0, 0),
                Text = "Ready."
            };
            // Fallback for when "Open in Browser" (or the server auto-starting on its own)
            // doesn't visibly pop up a tab (seen 2026-08-19: Windows won't always force a
            // window opened from a background process to the foreground, so it can land as an
            // unfocused tab in an already-open browser window with no obvious sign anything
            // happened) -- always the same fixed local URL, no LAN/QR detection needed since
            // this is for use on this machine, unlike the Mobile Access section below.
            private readonly Label _localAccessCaption = new Label
            {
                // AutoSize (rather than a fixed Height like _processViewerStatus above) because
                // this is meant to stay a single line -- with AutoSize left false, the row sized
                // itself to a bare Label's small default Size, which combined with the top
                // padding clipped the tops of the glyphs. AutoSize makes the row adopt this
                // label's real, text-driven preferred height instead.
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80),
                Padding = new Padding(0, 8, 0, 2),
                Text = "If the browser doesn't open automatically, copy this link into your browser:"
            };
            private readonly TextBox _localAccessUrl = new TextBox
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                ReadOnly = true,
                Text = "http://localhost:5252/",
                Width = 420
            };
            private readonly Button _localCopyLink = new Button
            {
                AutoSize = true,
                Text = "Copy Link"
            };
            private bool _processViewerFolderEdited;
            private bool _processViewerJobIdEdited;
            private string _processViewerJsonPath = string.Empty;
            private bool _processViewerCanGoLive;
            private bool _processViewerRuntimeReady = true;
            private string _processViewerRuntimeStatus = "Web viewer runtime ready.";

            // M6 (Mobile and QR Connection, 2026-08-19): lets someone open the live viewer from
            // a phone/tablet on the same network by scanning a QR code instead of typing a LAN
            // IP address. See WASPer_Sm01MobileAccess.cs for where this is populated. Rebuilt as
            // a dynamic list of entries (rather than one fixed QR/link pair) the same day, once
            // it became clear a single machine can have more than one address worth showing --
            // an institutional network plus a Windows Mobile Hotspot used to work around that
            // network's AP isolation, in this project's own troubleshooting.
            private readonly FlowLayoutPanel _mobileAccessContainer = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = Padding.Empty,
                WrapContents = true
            };
            private readonly List<PictureBox> _mobileQrImages = new List<PictureBox>();
            private readonly Label _mobileAccessStatus = new Label
            {
                AutoSize = true,
                ForeColor = Color.FromArgb(80, 80, 80),
                Padding = new Padding(0, 0, 0, 8),
                Text = "Scan with a phone or tablet on the same network as this computer."
            };

            private readonly TextBox _dumpStudyFolder = new TextBox
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Width = 720
            };
            private readonly TextBox _dumpStudyName = new TextBox
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Right,
                Width = 360
            };
            private readonly Button _dumpStudyBrowse = new Button
            {
                AutoSize = true,
                Text = "Browse..."
            };
            private readonly CheckBox _dumpStudyZip = new CheckBox
            {
                AutoSize = true,
                Checked = true,
                Text = "Zip when done"
            };
            private readonly Button _dumpStudyBuild = new Button
            {
                AutoSize = true,
                Enabled = false,
                Text = "Build Standalone Package"
            };
            private readonly Button _dumpStudyOpenFolder = new Button
            {
                AutoSize = true,
                Enabled = false,
                Text = "Open Folder"
            };
            private readonly Label _dumpStudyStatus = new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(80, 80, 80),
                Padding = new Padding(0, 6, 0, 0),
                Text = "Bundles the current job plus the whole study (study.json) into a " +
                    "standalone package -- no Rhino, vvvv, or .NET install needed to view it."
            };
            private bool _dumpStudyFolderEdited;
            private bool _dumpStudyNameEdited;

            public event Action<string, string> ProcessViewerExportRequested;
            public event Action<string> ProcessViewerLaunchRequested;
            public event Action<string> ProcessViewerOpenBrowserRequested;
            public event Action ProcessViewerRefreshRequested;
            public event Action<bool> ProcessViewerLiveToggleChanged;
            public event Action ProcessViewerPushChangeRequested;
            public event Action<string> ProcessViewerOpenFolderRequested;
            public event Action<string, string, bool> DumpFullStudyRequested;
            public event Action<string> DumpStudyOpenFolderRequested;

            private Control CreateProcessViewerPanel()
            {
                var panel = new TableLayoutPanel
                {
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    AutoScroll = false,
                    ColumnCount = 1,
                    Dock = DockStyle.Top,
                    Padding = new Padding(18),
                    RowCount = IncludeDumpFullStudySection ? 5 : 4
                };
                panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // context
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // external viewer package
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // mobile access
                if (IncludeDumpFullStudySection)
                    panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

                var context = ProcessViewerGroup("Current fabrication plan");
                var contextGrid = ProcessViewerGrid();
                AddProcessViewerRow(contextGrid, 0, "Solution", _processViewerSample);
                var selectionRow = new FlowLayoutPanel
                {
                    AutoSize = true,
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.LeftToRight,
                    Margin = Padding.Empty,
                    WrapContents = true
                };
                _processViewerSelection.AutoSize = true;
                _processViewerSelection.Dock = DockStyle.None;
                selectionRow.Controls.Add(_processViewerSelection);
                selectionRow.Controls.Add(_processViewerRestore);
                AddProcessViewerRow(contextGrid, 1, "Selected", selectionRow);
                AddProcessViewerRow(contextGrid, 2, "Path", _processViewerPathState);
                AddProcessViewerRow(contextGrid, 3, "Package", _processViewerJobState);
                AddProcessViewerRow(contextGrid, 4, "Application", _processViewerAppState);
                AddProcessViewerRow(contextGrid, 5, "Live viewers", _processViewerLiveStatus);
                context.Controls.Add(contextGrid);

                var package = ProcessViewerGroup("External viewer package");
                var packageGrid = ProcessViewerGrid();
                var folderRow = new TableLayoutPanel
                {
                    AutoSize = true,
                    ColumnCount = 2,
                    Dock = DockStyle.Fill,
                    Margin = Padding.Empty
                };
                folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                folderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                folderRow.Controls.Add(_processViewerFolder, 0, 0);
                folderRow.Controls.Add(_processViewerBrowse, 1, 0);
                AddProcessViewerRow(packageGrid, 0, "Folder", folderRow);
                AddProcessViewerRow(packageGrid, 1, "Job name", _processViewerJobId);

                var actions = new FlowLayoutPanel
                {
                    AutoSize = true,
                    Dock = DockStyle.Fill,
                    FlowDirection = FlowDirection.LeftToRight,
                    Margin = new Padding(0, 10, 0, 0),
                    WrapContents = true
                };
                // Open in Browser / Live / Push Change first (2026-08-19 rework), Export last/
                // secondary. vvvv stays out of the row entirely while EnableVvvvViewerButton is
                // off rather than just disabled-but-visible, so the row reads as the real
                // choices (open the viewer, control live push, optional export) plus Open Folder.
                actions.Controls.Add(_processViewerOpenBrowser);
                actions.Controls.Add(_processViewerRefresh);
                actions.Controls.Add(_processViewerLiveToggle);
                actions.Controls.Add(_processViewerPushChange);
                if (EnableVvvvViewerButton)
                    actions.Controls.Add(_processViewerLaunch);
                actions.Controls.Add(_processViewerOpenFolder);
                actions.Controls.Add(_processViewerDownloadGuide);
                actions.Controls.Add(_processViewerExport);
                packageGrid.Controls.Add(actions, 0, 2);
                packageGrid.SetColumnSpan(actions, 2);
                packageGrid.Controls.Add(_processViewerStatus, 0, 3);
                packageGrid.SetColumnSpan(_processViewerStatus, 2);
                var localLinkRow = new TableLayoutPanel
                {
                    AutoSize = true,
                    ColumnCount = 2,
                    Dock = DockStyle.Fill,
                    Margin = Padding.Empty
                };
                localLinkRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                localLinkRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                localLinkRow.Controls.Add(_localAccessUrl, 0, 0);
                localLinkRow.Controls.Add(_localCopyLink, 1, 0);
                var localLinkGroup = new TableLayoutPanel
                {
                    AutoSize = true,
                    ColumnCount = 1,
                    Dock = DockStyle.Fill,
                    Margin = Padding.Empty
                };
                localLinkGroup.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                localLinkGroup.Controls.Add(_localAccessCaption, 0, 0);
                localLinkGroup.Controls.Add(localLinkRow, 0, 1);
                packageGrid.Controls.Add(localLinkGroup, 0, 4);
                packageGrid.SetColumnSpan(localLinkGroup, 2);
                package.Controls.Add(packageGrid);

                // M6 (Mobile and QR Connection): a QR code + the same URL as plain text (in a
                // read-only, selectable TextBox rather than a Label so it can be copy-pasted by
                // hand too) per candidate LAN address (see WASPer_Sm01MobileAccess.cs -- can be
                // more than one, e.g. the institutional network plus a Mobile Hotspot workaround
                // for it), wrapping in a FlowLayoutPanel so however many there are just lay out
                // left-to-right and wrap. The server now auto-starts (2026-08-19) as soon as
                // there's something worth viewing, so a link here is very likely already live by
                // the time someone scans it -- previously this depended on "Open Web Viewer"
                // having been clicked on the desktop first, which was the source of a real
                // ERR_CONNECTION_TIMED_OUT bug on a phone that scanned before that happened.
                var mobile = ProcessViewerGroup("Mobile Access (QR code)");
                var mobileColumn = new TableLayoutPanel
                {
                    AutoSize = true,
                    ColumnCount = 1,
                    Dock = DockStyle.Fill,
                    Margin = Padding.Empty
                };
                mobileColumn.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                mobileColumn.Controls.Add(_mobileAccessStatus, 0, 0);
                mobileColumn.Controls.Add(_mobileAccessContainer, 0, 1);
                mobile.Controls.Add(mobileColumn);

                panel.Controls.Add(context, 0, 0);
                panel.Controls.Add(package, 0, 1);
                panel.Controls.Add(mobile, 0, 2);

                if (IncludeDumpFullStudySection)
                {
                    // Distinct from "External viewer package" above, which only ever
                    // exports the current single job -- this bundles that same job's
                    // .wasperxr plus the whole study's study.json (when one exists)
                    // into one self-contained, no-install-needed folder via
                    // Package-StandaloneViewer.ps1, unlocking the browser Dashboard's
                    // full study charts for anyone the package is handed to.
                    var dump = ProcessViewerGroup("Dump Full Study");
                    var dumpGrid = ProcessViewerGrid();
                    var dumpFolderRow = new TableLayoutPanel
                    {
                        AutoSize = true,
                        ColumnCount = 2,
                        Dock = DockStyle.Fill,
                        Margin = Padding.Empty
                    };
                    dumpFolderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                    dumpFolderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                    dumpFolderRow.Controls.Add(_dumpStudyFolder, 0, 0);
                    dumpFolderRow.Controls.Add(_dumpStudyBrowse, 1, 0);
                    AddProcessViewerRow(dumpGrid, 0, "Output folder", dumpFolderRow);
                    AddProcessViewerRow(dumpGrid, 1, "Package name", _dumpStudyName);

                    var dumpActions = new FlowLayoutPanel
                    {
                        AutoSize = true,
                        Dock = DockStyle.Fill,
                        FlowDirection = FlowDirection.LeftToRight,
                        Margin = new Padding(0, 10, 0, 0),
                        WrapContents = true
                    };
                    dumpActions.Controls.Add(_dumpStudyBuild);
                    dumpActions.Controls.Add(_dumpStudyOpenFolder);
                    dumpActions.Controls.Add(_dumpStudyZip);
                    dumpGrid.Controls.Add(dumpActions, 0, 2);
                    dumpGrid.SetColumnSpan(dumpActions, 2);
                    dumpGrid.Controls.Add(_dumpStudyStatus, 0, 3);
                    dumpGrid.SetColumnSpan(_dumpStudyStatus, 2);
                    dump.Controls.Add(dumpGrid);
                    panel.Controls.Add(dump, 0, 3);

                    _dumpStudyFolder.TextChanged += (sender, args) =>
                        _dumpStudyFolderEdited = _dumpStudyFolder.Focused;
                    _dumpStudyName.TextChanged += (sender, args) =>
                        _dumpStudyNameEdited = _dumpStudyName.Focused;
                    _dumpStudyBrowse.Click += BrowseDumpStudyFolder;
                    _dumpStudyBuild.Click += (sender, args) =>
                        DumpFullStudyRequested?.Invoke(
                            _dumpStudyFolder.Text.Trim(),
                            _dumpStudyName.Text.Trim(),
                            _dumpStudyZip.Checked);
                    _dumpStudyOpenFolder.Click += (sender, args) =>
                        DumpStudyOpenFolderRequested?.Invoke(_dumpStudyFolder.Text.Trim());
                }

                _processViewerFolder.TextChanged += (sender, args) =>
                    _processViewerFolderEdited = _processViewerFolder.Focused;
                _processViewerJobId.TextChanged += (sender, args) =>
                    _processViewerJobIdEdited = _processViewerJobId.Focused;
                _processViewerBrowse.Click += BrowseProcessViewerFolder;
                _processViewerRestore.Click += (sender, args) =>
                {
                    if (_dashboardSelection.PrimaryId.HasValue)
                        ShowIterationRequested?.Invoke(_dashboardSelection.PrimaryId.Value);
                };
                _processViewerExport.Click += (sender, args) =>
                    ProcessViewerExportRequested?.Invoke(
                        _processViewerFolder.Text.Trim(),
                        _processViewerJobId.Text.Trim());
                _toolTip.SetToolTip(
                    _processViewerExport,
                    "Optional: writes a standalone .wasperxr file to the folder above for " +
                    "offline/later use. The web viewer server starts and stays updated on its " +
                    "own -- this isn't required first.");
                _toolTip.SetToolTip(
                    _processViewerOpenBrowser,
                    "Opens the browser viewer. The server starts on its own as soon as there's " +
                    "something to view, so this is just a shortcut to look at it locally.");
                _processViewerLaunch.Click += (sender, args) =>
                    ProcessViewerLaunchRequested?.Invoke(_processViewerJsonPath);
                if (!EnableVvvvViewerButton)
                {
                    _toolTip.SetToolTip(
                        _processViewerLaunch,
                        "Temporarily disabled while the vvvv (Gamma) viewer path is parked -- " +
                        "use Open in Browser instead.");
                }
                _processViewerOpenBrowser.Click += (sender, args) =>
                    ProcessViewerOpenBrowserRequested?.Invoke(_processViewerJsonPath);
                _processViewerRefresh.Click += (sender, args) =>
                    ProcessViewerRefreshRequested?.Invoke();
                _toolTip.SetToolTip(
                    _processViewerRefresh,
                    "Pings the current XR scene: detects available network addresses and QR " +
                    "links again, ensures the WebViewer server is running, pushes the latest " +
                    "scene, and refreshes the signed-session connection status.");
                _processViewerLiveToggle.Click += (sender, args) =>
                {
                    _liveToggleOn = !_liveToggleOn;
                    ApplyLiveToggleVisual();
                    ProcessViewerLiveToggleChanged?.Invoke(_liveToggleOn);
                };
                _toolTip.SetToolTip(
                    _processViewerLiveToggle,
                    "When on (default), the viewer keeps streaming the current wsp_path live " +
                    "as it changes. Turn off to pause automatic updates and use Push Change " +
                    "for manual one-off pushes instead.");
                _processViewerPushChange.Click += (sender, args) =>
                    ProcessViewerPushChangeRequested?.Invoke();
                _toolTip.SetToolTip(
                    _processViewerPushChange,
                    "Manually pushes the current wsp_path to the viewer once. Only enabled " +
                    "while Live is off.");
                ApplyLiveToggleVisual();
                _processViewerOpenFolder.Click += (sender, args) =>
                    ProcessViewerOpenFolderRequested?.Invoke(_processViewerFolder.Text.Trim());
                _processViewerDownloadGuide.Click += DownloadProcessViewerGuide;
                _toolTip.SetToolTip(
                    _processViewerDownloadGuide,
                    "Saves a local copy of the Process Viewer feature, workflow, technical, " +
                    "dependency-install, and Android AR setup guide.");
                _localCopyLink.Click += (sender, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(_localAccessUrl.Text))
                        Clipboard.SetText(_localAccessUrl.Text);
                };
                return panel;
            }

            private void DownloadProcessViewerGuide(object sender, EventArgs args)
            {
                const string resourceName =
                    "WASPer_3DP.Resources.Documentation.WASPER_PROCESS_VIEWER_GUIDE.md";
                string initialFolder = Directory.Exists(_processViewerFolder.Text)
                    ? _processViewerFolder.Text
                    : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                using var dialog = new SaveFileDialog
                {
                    AddExtension = true,
                    DefaultExt = "md",
                    FileName = "WASPer_Process_Viewer_Guide.md",
                    Filter = "Markdown document (*.md)|*.md|Text document (*.txt)|*.txt|All files (*.*)|*.*",
                    InitialDirectory = initialFolder,
                    OverwritePrompt = true,
                    RestoreDirectory = true,
                    Title = "Save WASPer Process Viewer Guide"
                };
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                try
                {
                    Assembly assembly = typeof(wsp_Sm01_WASPer_Study_Manager).Assembly;
                    using Stream source = assembly.GetManifestResourceStream(resourceName);
                    if (source == null)
                        throw new InvalidOperationException("The embedded Process Viewer guide was not found.");

                    using var destination = new FileStream(
                        dialog.FileName,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None);
                    source.CopyTo(destination);
                    _processViewerStatus.Text = "Process Viewer guide saved to " + dialog.FileName;
                }
                catch (Exception exception)
                {
                    _processViewerStatus.Text = "Could not save the Process Viewer guide: " +
                        exception.Message;
                    MessageBox.Show(
                        this,
                        _processViewerStatus.Text,
                        "WASPer Process Viewer",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                }
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
                _processViewerRuntimeReady = webViewerRuntimeAvailable;
                _processViewerRuntimeStatus = string.IsNullOrWhiteSpace(webViewerRuntimeStatus)
                    ? "Web viewer runtime status unknown."
                    : webViewerRuntimeStatus;
                _localAccessUrl.Text = localViewerUrl ?? string.Empty;
                if (!_processViewerFolderEdited)
                    _processViewerFolder.Text = defaultFolder ?? string.Empty;
                if (!_processViewerJobIdEdited)
                    _processViewerJobId.Text = defaultJobId ?? string.Empty;

                _processViewerSample.Text = string.IsNullOrWhiteSpace(sampleName)
                    ? "Current solution"
                    : sampleName;
                _processViewerPathState.Text = !hasPath
                    ? "No wsp_path connected"
                    : hasMotionPlan
                        ? $"Ready - {pathBranches} branch(es), {motions} motion(s)"
                        : $"{pathBranches} branch(es) - motion plan required";
                _processViewerJsonPath = File.Exists(jsonPath) ? jsonPath : string.Empty;
                _processViewerJobState.Text = string.IsNullOrWhiteSpace(_processViewerJsonPath)
                    ? "No package exported"
                    : _processViewerJsonPath;
                _processViewerAppState.Text = !_processViewerRuntimeReady
                    ? _processViewerRuntimeStatus
                    : !EnableVvvvViewerButton
                    ? "Browser WebViewer ready. vvvv viewer temporarily disabled."
                    : viewerAvailable
                        ? "vvvv viewer ready"
                        : viewerStatus ?? "Viewer unavailable";
                _processViewerExport.Enabled = hasMotionPlan;
                _processViewerLaunch.Enabled = EnableVvvvViewerButton && viewerAvailable &&
                    !string.IsNullOrWhiteSpace(_processViewerJsonPath);
                // M5 live link: a complete wsp_path is now enough on its own -- the web viewer
                // server auto-starts and stays updated without an export click.
                // _processViewerCanGoLive is remembered so SetProcessViewerResult (called after
                // actions that don't know hasMotionPlan directly) and ApplyLiveToggleVisual
                // (Push Change's enabled state) can apply the same rule.
                _processViewerCanGoLive = hasMotionPlan;
                _processViewerOpenBrowser.Enabled = _processViewerRuntimeReady &&
                    (_processViewerCanGoLive || !string.IsNullOrWhiteSpace(_processViewerJsonPath));
                _processViewerOpenFolder.Enabled = Directory.Exists(_processViewerFolder.Text);
                ApplyLiveToggleVisual();
                ApplyRuntimeVisual();
                UpdateProcessViewerSelection();
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
                _processViewerJsonPath = File.Exists(jsonPath) ? jsonPath : string.Empty;
                _processViewerJobState.Text = string.IsNullOrWhiteSpace(_processViewerJsonPath)
                    ? "No package exported"
                    : _processViewerJsonPath;
                _processViewerStatus.Text = status ?? string.Empty;
                _processViewerLaunch.Enabled = EnableVvvvViewerButton && viewerAvailable &&
                    !string.IsNullOrWhiteSpace(_processViewerJsonPath);
                _processViewerOpenBrowser.Enabled = _processViewerRuntimeReady &&
                    (_processViewerCanGoLive || !string.IsNullOrWhiteSpace(_processViewerJsonPath));
                _processViewerOpenFolder.Enabled = Directory.Exists(_processViewerFolder.Text);
                ApplyLiveToggleVisual();
                ApplyRuntimeVisual();
            }

            // Keeps the Live button's text/color and Push Change's enabled state in sync with
            // _liveToggleOn -- called after every toggle click and after both
            // UpdateProcessViewer/SetProcessViewerResult so a newly-arrived wsp_path/export
            // immediately reflects the current Live state.
            private void ApplyLiveToggleVisual()
            {
                _processViewerLiveToggle.Text = _liveToggleOn ? "Live: On" : "Live: Off";
                _processViewerLiveToggle.BackColor = _liveToggleOn
                    ? Color.FromArgb(210, 240, 210)
                    : SystemColors.Control;
                _processViewerLiveToggle.UseVisualStyleBackColor = !_liveToggleOn;
                _processViewerPushChange.Enabled = !_liveToggleOn &&
                    _processViewerRuntimeReady &&
                    (_processViewerCanGoLive || !string.IsNullOrWhiteSpace(_processViewerJsonPath));
            }

            private void ApplyRuntimeVisual()
            {
                if (_processViewerRuntimeReady)
                {
                    _processViewerOpenBrowser.Text = "Open in Browser";
                    _processViewerOpenBrowser.BackColor = SystemColors.Control;
                    _processViewerOpenBrowser.ForeColor = SystemColors.ControlText;
                    _processViewerOpenBrowser.UseVisualStyleBackColor = true;
                    _processViewerDownloadGuide.BackColor = SystemColors.Control;
                    _processViewerDownloadGuide.ForeColor = SystemColors.ControlText;
                    _processViewerDownloadGuide.UseVisualStyleBackColor = true;
                    _toolTip.SetToolTip(
                        _processViewerOpenBrowser,
                        "Opens the browser viewer. The server starts on its own as soon as there's " +
                        "something to view, so this is just a shortcut to look at it locally.");
                    return;
                }

                Color warningBack = Color.FromArgb(255, 219, 140);
                Color warningText = Color.FromArgb(102, 58, 0);
                _processViewerOpenBrowser.Text = "Install .NET";
                _processViewerOpenBrowser.BackColor = warningBack;
                _processViewerOpenBrowser.ForeColor = warningText;
                _processViewerOpenBrowser.UseVisualStyleBackColor = false;
                _processViewerDownloadGuide.BackColor = warningBack;
                _processViewerDownloadGuide.ForeColor = warningText;
                _processViewerDownloadGuide.UseVisualStyleBackColor = false;
                _toolTip.SetToolTip(
                    _processViewerOpenBrowser,
                    _processViewerRuntimeStatus + " Save the Process Viewer guide for install steps.");
            }

            // Lets the controller (SetLiveEnabled) push a state change back into the UI -- e.g.
            // if the toggle is ever driven from somewhere other than this button's own click
            // handler. Re-enters ApplyLiveToggleVisual rather than duplicating its logic.
            public void SetLiveToggleState(bool enabled)
            {
                if (_liveToggleOn == enabled)
                    return;
                _liveToggleOn = enabled;
                ApplyLiveToggleVisual();
            }

            // M5's closing deliverable, "Viewer status" (2026-08-19). Called independently of
            // UpdateProcessViewer/SetProcessViewerResult (its own poll cadence, not tied to a
            // solve or an export click) from TryRefreshLiveViewerStatus.
            public void SetLiveViewerStatus(string text)
            {
                _processViewerLiveStatus.Text = string.IsNullOrWhiteSpace(text)
                    ? "Not connected"
                    : text;
            }

            // M6 (Mobile and QR Connection, 2026-08-19). Called once from ShowManager via
            // RefreshMobileAccess (WASPer_Sm01MobileAccess.cs) -- LAN addresses don't need
            // re-polling the way live viewer status does. One entry per candidate address
            // (2026-08-19 revision, see WASPer_Sm01MobileAccess.cs for why) -- rebuilds
            // _mobileAccessContainer from scratch each call, disposing whatever QR images were
            // shown previously (same ownership-transfer pattern as the snapshot preview image
            // elsewhere in this form, see OnFormClosed) before adding the new set.
            public void UpdateMobileAccess(IReadOnlyList<MobileAccessLink> links, string status)
            {
                foreach (PictureBox qrBox in _mobileQrImages)
                {
                    Image previous = qrBox.Image;
                    qrBox.Image = null;
                    previous?.Dispose();
                }
                _mobileQrImages.Clear();
                _mobileAccessContainer.Controls.Clear();
                foreach (MobileAccessLink link in links ?? Array.Empty<MobileAccessLink>())
                    _mobileAccessContainer.Controls.Add(BuildMobileAccessEntry(link));
                if (!string.IsNullOrWhiteSpace(status))
                    _mobileAccessStatus.Text = status;
            }

            // One QR code + label + copyable URL, for one candidate LAN address. Tracks its
            // PictureBox in _mobileQrImages purely so UpdateMobileAccess/OnFormClosed can find
            // and dispose the bitmap later -- WinForms doesn't do that automatically for images
            // handed to a PictureBox.
            private Control BuildMobileAccessEntry(MobileAccessLink link)
            {
                var qrBox = new PictureBox
                {
                    BackColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle,
                    Height = 130,
                    Image = link.Qr,
                    Margin = new Padding(0, 0, 0, 4),
                    SizeMode = PictureBoxSizeMode.Zoom,
                    Width = 130
                };
                _mobileQrImages.Add(qrBox);

                var caption = new Label
                {
                    AutoSize = true,
                    Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                    Margin = new Padding(0, 0, 0, 2),
                    Text = string.IsNullOrWhiteSpace(link.Label) ? "Network" : link.Label
                };

                var urlBox = new TextBox
                {
                    ReadOnly = true,
                    Text = link.Url,
                    Width = 172
                };
                var copyButton = new Button
                {
                    AutoSize = true,
                    Margin = new Padding(0, 4, 0, 0),
                    Text = "Copy Link"
                };
                copyButton.Click += (sender, args) => Clipboard.SetText(link.Url);

                var entry = new TableLayoutPanel
                {
                    AutoSize = true,
                    ColumnCount = 1,
                    Margin = new Padding(0, 0, 20, 12)
                };
                entry.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                entry.Controls.Add(qrBox, 0, 0);
                entry.Controls.Add(caption, 0, 1);
                entry.Controls.Add(urlBox, 0, 2);
                entry.Controls.Add(copyButton, 0, 3);
                return entry;
            }

            private void BrowseProcessViewerFolder(object sender, EventArgs args)
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "Select the WASPer Process Viewer job folder",
                    SelectedPath = Directory.Exists(_processViewerFolder.Text)
                        ? _processViewerFolder.Text
                        : string.Empty,
                    ShowNewFolderButton = true
                };
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                _processViewerFolderEdited = true;
                _processViewerFolder.Text = dialog.SelectedPath;
                _processViewerOpenFolder.Enabled = true;
            }

            public void UpdateDumpStudySection(
                string defaultFolder,
                string defaultName,
                bool canBuild)
            {
                if (!_dumpStudyFolderEdited)
                    _dumpStudyFolder.Text = defaultFolder ?? string.Empty;
                if (!_dumpStudyNameEdited)
                    _dumpStudyName.Text = defaultName ?? string.Empty;
                _dumpStudyBuild.Enabled = canBuild;
                _dumpStudyOpenFolder.Enabled = Directory.Exists(_dumpStudyFolder.Text);
            }

            public void SetDumpStudyResult(string status)
            {
                _dumpStudyStatus.Text = status ?? string.Empty;
                _dumpStudyOpenFolder.Enabled = Directory.Exists(_dumpStudyFolder.Text);
            }

            private void BrowseDumpStudyFolder(object sender, EventArgs args)
            {
                using var dialog = new FolderBrowserDialog
                {
                    Description = "Select where to build the standalone study package",
                    SelectedPath = Directory.Exists(_dumpStudyFolder.Text)
                        ? _dumpStudyFolder.Text
                        : string.Empty,
                    ShowNewFolderButton = true
                };
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                _dumpStudyFolderEdited = true;
                _dumpStudyFolder.Text = dialog.SelectedPath;
                _dumpStudyOpenFolder.Enabled = true;
            }

            private void UpdateProcessViewerSelection()
            {
                if (!_dashboardSelection.PrimaryId.HasValue)
                {
                    _processViewerSelection.Text = "No individual selected";
                    _processViewerRestore.Enabled = false;
                    return;
                }
                int id = _dashboardSelection.PrimaryId.Value;
                WasperStudyIteration iteration = _dashboardIterations.Find(item => item.Index == id);
                _processViewerSelection.Text = iteration == null ||
                    string.IsNullOrWhiteSpace(iteration.SampleName)
                        ? $"Iteration {id}"
                        : $"{id}: {iteration.SampleName}";
                _processViewerRestore.Enabled = iteration != null;
            }

            private static Label ProcessViewerValueLabel(string text) => new Label
            {
                AutoEllipsis = true,
                Dock = DockStyle.Fill,
                Margin = new Padding(4, 7, 4, 4),
                Text = text,
                TextAlign = ContentAlignment.MiddleLeft
            };

            private static GroupBox ProcessViewerGroup(string text) => new GroupBox
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                Margin = new Padding(0, 0, 0, 12),
                Padding = new Padding(12),
                Text = text
            };

            private static TableLayoutPanel ProcessViewerGrid()
            {
                var grid = new TableLayoutPanel
                {
                    AutoSize = true,
                    ColumnCount = 2,
                    Dock = DockStyle.Top,
                    RowCount = 0
                };
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                return grid;
            }

            private static void AddProcessViewerRow(
                TableLayoutPanel grid,
                int row,
                string label,
                Control value)
            {
                grid.RowCount = Math.Max(grid.RowCount, row + 1);
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                grid.Controls.Add(new Label
                {
                    AutoSize = true,
                    Font = new Font(SystemFonts.MessageBoxFont, FontStyle.Bold),
                    Margin = new Padding(0, 8, 5, 4),
                    Text = label
                }, 0, row);
                grid.Controls.Add(value, 1, row);
            }
        }
    }
}
