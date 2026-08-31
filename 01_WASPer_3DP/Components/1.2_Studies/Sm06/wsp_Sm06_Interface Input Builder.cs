using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

using GH_IO.Serialization;
using Grasshopper;
using Grasshopper.Kernel;
using Newtonsoft.Json;

namespace WASPer_3DP.Components._1_2_Studies
{
    /// <summary>
    /// First slice of the WASPer Interface Input Builder described in SELVA_INTEGRATION_PLAN
    /// section 6.3. It registers Grasshopper controls by GUID, previews the contextual parameter
    /// each one infers, inserts those parameters between the control and its recipients, or beside
    /// a disconnected control, so an external publisher (Selva today, any Rhino.Compute host
    /// later) can drive them, and removes only the nodes it created or explicitly adopted.
    ///
    /// Scope of this version, deliberately narrow:
    /// - it registers its own controls, independent of Sm01. Reading Sm01's linked sliders is a
    ///   later slice, once the graph transformation itself is trusted;
    /// - it does not write a manifest. The stored control-to-contextual-parameter relationships are
    ///   what a manifest exporter will need (plan section 4.6), but exporting is Phase 1 work;
    /// - every mutation is an explicit button press. Nothing here runs during an ordinary solve, a
    ///   study run, a file open, or an export.
    /// </summary>
    public sealed partial class wsp_Sm06_Interface_Input_Builder : GH_Component
    {
        private const string LinkedControlsKey = "sm06_linked_controls";
        private const string ManagedLinksKey = "sm06_managed_links";

        private readonly string _version;
        private readonly List<Guid> _linkedControlIds = new List<Guid>();
        private readonly List<WasperSm06ManagedLink> _managedLinks =
            new List<WasperSm06ManagedLink>();
        private WasperSm06PreviewDialog _previewWindow;
        private string _status = "Select controls on the canvas, then click Link selected.";
        private static Bitmap _icon;

        public wsp_Sm06_Interface_Input_Builder()
            : base(
                "wsp_Sm06_Interface Input Builder",
                "Interface Inputs",
                "Prepares a Grasshopper definition for a client-facing web interface. Register " +
                "Number Sliders, Value Lists, Boolean Toggles, and Panels, then insert the " +
                "matching contextual parameter (Get Number, Get Integer, Get Value List, Get " +
                "Boolean, Get String) between each control and the inputs it drives, or beside a " +
                "disconnected control. A shared name can be edited before applying and is assigned " +
                "to both objects. The original control stays connected as the local default value; " +
                "an external publisher such " +
                "as Selva overrides the same parameter when the definition is solved remotely. " +
                "Existing compatible Get parameters are recognized and can be adopted, renamed, " +
                "or have their access repaired. Insertion and removal are previewed first and " +
                "applied as one undoable step.",
                WASPerPalette.Performance,
                "1.2_Studies")
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            _version = version == null
                ? "v1.0.x"
                : $"v{version.Major}.{version.Minor}.{version.Build}";
            Message = _version;
        }

        public override Guid ComponentGuid =>
            new Guid("D7C31E4A-5B08-42F6-9E27-16A4C9F0B3D5");

        public override GH_Exposure Exposure => GH_Exposure.secondary;

        protected override Bitmap Icon => _icon ??= CreateIcon();

        public override void CreateAttributes()
        {
            m_attributes = new WasperSm06Attributes(this);
        }

        protected override void RegisterInputParams(GH_InputParamManager parameters)
        {
            parameters.AddTextParameter(
                "profile",
                "profile",
                "Optional interface profile name carried into the exported relationships, for " +
                "example Concept Review or Technical Review. One definition may need several " +
                "profiles over the same controls. Purely descriptive in this version.",
                GH_ParamAccess.item,
                "Default");
            parameters[0].Optional = true;
        }

        protected override void RegisterOutputParams(GH_OutputParamManager parameters)
        {
            parameters.AddTextParameter(
                "controls",
                "controls",
                "One line per registered control: interface key, control kind, contextual " +
                "parameter (marked when the author overrode the inferred one), item/list " +
                "access, recipient count, and current status.",
                GH_ParamAccess.list);
            parameters.AddTextParameter(
                "report",
                "report",
                "Result of the last preparation or removal, plus the counts of created, reused, " +
                "repaired, removed, skipped, and failed items.",
                GH_ParamAccess.item);
        }

        /// <summary>
        /// Read-only. Solving reports the current state of the registered controls and never edits
        /// the document - the plan is explicit that preparation must be an intentional, manual act.
        /// </summary>
        protected override void SolveInstance(IGH_DataAccess dataAccess)
        {
            string profile = "Default";
            dataAccess.GetData(0, ref profile);

            List<WasperSm06Candidate> candidates = BuildPreparationCandidates();
            var lines = new List<string>();
            foreach (WasperSm06Candidate candidate in candidates)
            {
                WasperSm06ManagedLink link = _managedLinks
                    .FirstOrDefault(managed => managed.ControlId == candidate.ControlId);
                string key = link?.Key ??
                    BuildKey(candidate.ControlNickName, candidate.ControlId);
                lines.Add(
                    $"{key} | {WasperSm06ContextualTypes.Describe(candidate.Kind)} | " +
                    $"{candidate.TypeName}" +
                    (candidate.TypeOverridden ? " (overridden)" : string.Empty) +
                    $" | {candidate.AccessName.ToLowerInvariant()} | " +
                    $"{candidate.RecipientCount} recipient(s) | {candidate.StatusText}");
            }

            int ambiguous = candidates.Count(candidate =>
                candidate.Status == WasperSm06Status.Ambiguous);
            int missing = candidates.Count(candidate =>
                candidate.Status == WasperSm06Status.MissingDependency);
            if (ambiguous > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Remark,
                    $"{ambiguous} registered control(s) have wiring this component will not " +
                    "rewrite automatically. Open the preview to see why.");
            }
            if (missing > 0)
            {
                AddRuntimeMessage(
                    GH_RuntimeMessageLevel.Warning,
                    $"{missing} registered control(s) need a contextual parameter type that is " +
                    "not installed. Nothing is substituted for it.");
            }

            dataAccess.SetDataList(0, lines);
            dataAccess.SetData(1, $"[{profile}] {_status}");
            Message = _linkedControlIds.Count == 0
                ? _version
                : $"{_managedLinks.Count}/{_linkedControlIds.Count} prepared";
        }

        // ------------------------------------------------------------------
        //  Canvas actions
        // ------------------------------------------------------------------

        /// <summary>
        /// Registers every supported control currently selected on the canvas, plus the supported
        /// source controls of any selected contextual parameters. Connected recognized Get nodes
        /// are selected as immediate visual feedback and are adopted later by Prepare inputs.
        /// Registration is bookkeeping only: no wire is touched here.
        /// </summary>
        internal void LinkSelectedControls()
        {
            GH_Document document = ActiveDocument();
            if (document == null)
            {
                SetStatus("No active Grasshopper document was found.");
                return;
            }

            List<IGH_Param> selectedParameters = document.Objects
                .OfType<IGH_Param>()
                .Where(parameter => parameter.Attributes?.Selected == true)
                .ToList();

            List<IGH_Param> selected = selectedParameters
                .Where(parameter => WasperSm06ContextualTypes.Classify(parameter) !=
                    WasperSm06ControlKind.Unknown)
                .Concat(selectedParameters
                    .Where(parameter => parameter is IGH_ContextualParameter)
                    .SelectMany(parameter => parameter.Sources ?? Array.Empty<IGH_Param>())
                    .Where(source => WasperSm06ContextualTypes.Classify(source) !=
                        WasperSm06ControlKind.Unknown))
                .GroupBy(parameter => parameter.InstanceGuid)
                .Select(group => group.First())
                .OrderBy(parameter => parameter.Attributes.Bounds.Y)
                .ThenBy(parameter => parameter.Attributes.Bounds.X)
                .ToList();

            int added = 0;
            var detectedGetIds = new HashSet<Guid>();
            foreach (IGH_Param control in selected)
            {
                if (!_linkedControlIds.Contains(control.InstanceGuid))
                {
                    _linkedControlIds.Add(control.InstanceGuid);
                    added++;
                }

                foreach (IGH_Param contextual in (control.Recipients ?? Array.Empty<IGH_Param>())
                    .Where(recipient => recipient is IGH_ContextualParameter)
                    .Where(recipient => WasperSm06ContextualTypes.FromGuid(
                        recipient.ComponentGuid) != null))
                {
                    detectedGetIds.Add(contextual.InstanceGuid);
                    if (contextual.Attributes != null)
                        contextual.Attributes.Selected = true;
                }
            }

            SetStatus(selected.Count == 0
                ? "Select one or more Number Sliders, Value Lists, Boolean Toggles, or Panels, " +
                    "then click Link selected."
                : $"Registered {added} new control(s); {_linkedControlIds.Count} total." +
                    (detectedGetIds.Count > 0
                        ? $" Detected and selected {detectedGetIds.Count} connected Get input(s)."
                        : string.Empty));
        }

        /// <summary>
        /// Unregisters the selected controls. This is the separate "Unlink selected controls"
        /// command from the plan: it does not touch any contextual parameter already on the canvas,
        /// so a still-managed node is reported rather than silently orphaned.
        /// </summary>
        internal void UnlinkSelectedControls()
        {
            GH_Document document = ActiveDocument();
            if (document == null)
            {
                SetStatus("No active Grasshopper document was found.");
                return;
            }

            var selectedIds = new HashSet<Guid>(document.Objects
                .Where(documentObject => documentObject.Attributes?.Selected == true)
                .Select(documentObject => documentObject.InstanceGuid));
            int stillManaged = _managedLinks.Count(link => selectedIds.Contains(link.ControlId));
            int removed = _linkedControlIds.RemoveAll(selectedIds.Contains);
            _managedLinks.RemoveAll(link => selectedIds.Contains(link.ControlId));

            SetStatus(removed == 0
                ? "None of the selected objects were registered."
                : $"Unregistered {removed} control(s)." + (stillManaged > 0
                    ? $" {stillManaged} inserted contextual parameter(s) were left on the canvas; " +
                        "delete them by hand or re-register the control first."
                    : string.Empty));
        }

        internal void ClearRegistrations()
        {
            _linkedControlIds.Clear();
            _managedLinks.Clear();
            SetStatus("All registrations cleared. Nothing on the canvas was changed.");
        }

        /// <summary>Preview then, on confirmation, apply the insertion batch.</summary>
        internal void ShowPreparationPreview()
        {
            List<WasperSm06Candidate> candidates = BuildPreparationCandidates();
            if (candidates.Count == 0)
            {
                SetStatus("No controls are registered yet.");
                return;
            }

            var dialog = new WasperSm06PreviewDialog(
                "Prepare interface inputs",
                "Insert a contextual parameter between each registered control and the inputs it " +
                "drives, or beside a disconnected control for later wiring. The type is inferred " +
                "from the control and from the data it currently holds; its shared name, type, and " +
                "shared name and type can be edited before applying; Item/List access is inferred " +
                "from the live data. Only Ready and Repairable rows can be " +
                "applied.",
                "Apply preparation",
                candidates,
                ApplyPreparationFromPreview,
                ClassifyCandidate,
                RefreshPreparationCandidates);
            ShowPreviewWindow(dialog);
        }

        /// <summary>Preview then, on confirmation, apply the removal batch.</summary>
        internal void ShowRemovalPreview()
        {
            List<WasperSm06Candidate> candidates = BuildRemovalCandidates();
            if (candidates.Count == 0)
            {
                SetStatus("This component has not inserted or adopted any contextual parameters.");
                return;
            }

            var dialog = new WasperSm06PreviewDialog(
                "Remove managed interface inputs",
                "Reconnect each control directly to its recipients and delete the contextual " +
                "parameter this component manages. The controls themselves are never removed and " +
                "stay registered, so they can be prepared again later.",
                "Remove Get inputs",
                candidates,
                ApplyRemovalFromPreview,
                reclassify: null,
                refresh: BuildRemovalCandidates);
            ShowPreviewWindow(dialog);
        }

        private void ApplyPreparationFromPreview(
            IReadOnlyList<WasperSm06Candidate> candidates)
        {
            WasperSm06Report report = ApplyPreparation(candidates);
            SetStatus(Compose(report.Summarize("Preparation"), report), expire: false);
        }

        private void ApplyRemovalFromPreview(IReadOnlyList<WasperSm06Candidate> candidates)
        {
            WasperSm06Report report = ApplyRemoval(candidates);
            SetStatus(Compose(report.Summarize("Removal"), report), expire: false);
        }

        private void ShowPreviewWindow(WasperSm06PreviewDialog dialog)
        {
            if (_previewWindow != null && !_previewWindow.IsDisposed)
            {
                dialog.Dispose();
                _previewWindow.Activate();
                return;
            }

            _previewWindow = dialog;
            dialog.FormClosed += (sender, arguments) =>
            {
                if (ReferenceEquals(_previewWindow, sender))
                    _previewWindow = null;
            };

            if (Instances.DocumentEditor != null)
                dialog.Show(Instances.DocumentEditor);
            else
                dialog.Show();
        }

        /// <summary>
        /// Rebuilds the preparation preview from the live document and removes stale registrations
        /// whose source controls no longer exist. This is deliberately explicit: opening or solving
        /// a definition never mutates the stored registration list.
        /// </summary>
        private IEnumerable<WasperSm06Candidate> RefreshPreparationCandidates()
        {
            GH_Document document = ActiveDocument();
            if (document == null)
                return Array.Empty<WasperSm06Candidate>();

            var missing = new HashSet<Guid>(_linkedControlIds.Where(id =>
                !(document.FindObject(id, true) is IGH_Param)));
            if (missing.Count > 0)
            {
                _linkedControlIds.RemoveAll(missing.Contains);
                _managedLinks.RemoveAll(link => missing.Contains(link.ControlId));
                OnObjectChanged(GH_ObjectEventType.Options);
            }
            return BuildPreparationCandidates();
        }

        private static string Compose(string summary, WasperSm06Report report)
        {
            return report.Messages.Count == 0
                ? summary
                : summary + " " + string.Join(" ", report.Messages);
        }

        /// <summary>
        /// Records the outcome and refreshes the component. After a graph operation the solution is
        /// already scheduled for the whole batch, so <paramref name="expire"/> is false there: one
        /// scheduled solve per batch, never a second synchronous one on top of it.
        /// </summary>
        private void SetStatus(string status, bool expire = true)
        {
            _status = status;
            OnObjectChanged(GH_ObjectEventType.Options);
            if (expire)
                ExpireSolution(true);
            Instances.RedrawCanvas();
        }

        // ------------------------------------------------------------------
        //  Canvas link rendering support
        // ------------------------------------------------------------------

        /// <summary>Bounds of every registered control, for the dashed selection overlay.</summary>
        internal IReadOnlyList<RectangleF> LinkedControlBounds()
        {
            GH_Document document = ActiveDocument();
            if (document == null)
                return Array.Empty<RectangleF>();
            return _linkedControlIds
                .Select(id => document.FindObject(id, true))
                .Where(documentObject => documentObject?.Attributes != null)
                .Select(documentObject => documentObject.Attributes.Bounds)
                .ToList();
        }

        /// <summary>Bounds of every contextual parameter this component manages.</summary>
        internal IReadOnlyList<RectangleF> ManagedContextualBounds()
        {
            GH_Document document = ActiveDocument();
            if (document == null)
                return Array.Empty<RectangleF>();
            return _managedLinks
                .Select(link => document.FindObject(link.ContextualParameterId, true))
                .Where(documentObject => documentObject?.Attributes != null)
                .Select(documentObject => documentObject.Attributes.Bounds)
                .ToList();
        }

        internal int RegisteredCount => _linkedControlIds.Count;

        internal int ManagedCount => _managedLinks.Count;

        // ------------------------------------------------------------------
        //  Menu and persistence
        // ------------------------------------------------------------------

        protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalComponentMenuItems(menu);
            Menu_AppendSeparator(menu);
            Menu_AppendItem(
                menu,
                "Link selected controls",
                (sender, arguments) => LinkSelectedControls());
            Menu_AppendItem(
                menu,
                "Unlink selected controls",
                (sender, arguments) => UnlinkSelectedControls());
            Menu_AppendItem(
                menu,
                "Clear all registrations",
                (sender, arguments) => ClearRegistrations());
            Menu_AppendSeparator(menu);
            Menu_AppendItem(
                menu,
                "Preview interface inputs...",
                (sender, arguments) => ShowPreparationPreview());
            Menu_AppendItem(
                menu,
                "Preview removal...",
                (sender, arguments) => ShowRemovalPreview(),
                _managedLinks.Count > 0);
        }

        public override bool Write(GH_IWriter writer)
        {
            writer.SetString(
                LinkedControlsKey,
                string.Join(";", _linkedControlIds.Select(id => id.ToString())));
            writer.SetString(ManagedLinksKey, JsonConvert.SerializeObject(_managedLinks));
            return base.Write(writer);
        }

        public override bool Read(GH_IReader reader)
        {
            _linkedControlIds.Clear();
            if (reader.ItemExists(LinkedControlsKey))
            {
                foreach (string token in reader.GetString(LinkedControlsKey).Split(
                    new[] { ';' },
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Guid.TryParse(token, out Guid id) && !_linkedControlIds.Contains(id))
                        _linkedControlIds.Add(id);
                }
            }

            _managedLinks.Clear();
            if (reader.ItemExists(ManagedLinksKey))
            {
                try
                {
                    List<WasperSm06ManagedLink> stored =
                        JsonConvert.DeserializeObject<List<WasperSm06ManagedLink>>(
                            reader.GetString(ManagedLinksKey));
                    if (stored != null)
                        _managedLinks.AddRange(stored.Where(link => link != null));
                }
                catch
                {
                    // A relationship table that cannot be read is dropped rather than guessed at:
                    // removal would otherwise operate on nodes it cannot verify.
                    _managedLinks.Clear();
                }
            }

            // A registration can exist without a managed link, but never the reverse.
            foreach (WasperSm06ManagedLink link in _managedLinks)
            {
                if (!_linkedControlIds.Contains(link.ControlId))
                    _linkedControlIds.Add(link.ControlId);
            }

            return base.Read(reader);
        }

        /// <summary>
        /// A control capsule with an arrow passing through a small contextual node, in the warm
        /// Sm-family palette: what the component does to a wire, drawn literally.
        /// </summary>
        private static Bitmap CreateIcon()
        {
            var bitmap = new Bitmap(24, 24);
            using Graphics graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var controlBrush = new SolidBrush(Color.FromArgb(255, 236, 201));
            using var nodeBrush = new SolidBrush(Color.FromArgb(242, 166, 44));
            using var darkPen = new Pen(Color.FromArgb(55, 55, 55), 1.3f);
            using var wirePen = new Pen(Color.FromArgb(90, 90, 90), 1.5f)
            {
                CustomEndCap = new AdjustableArrowCap(3f, 3.6f, true)
            };

            // Source control on the left.
            graphics.FillRectangle(controlBrush, 1.5f, 8f, 7.5f, 8f);
            graphics.DrawRectangle(darkPen, 1.5f, 8f, 7.5f, 8f);

            // Wire into the inserted contextual node, and on to the recipient.
            graphics.DrawLine(wirePen, 9f, 12f, 13f, 12f);
            graphics.FillEllipse(nodeBrush, 12.5f, 8.5f, 7f, 7f);
            graphics.DrawEllipse(darkPen, 12.5f, 8.5f, 7f, 7f);
            graphics.DrawLine(wirePen, 19.5f, 12f, 22.5f, 12f);

            // Recipient stub.
            graphics.DrawLine(darkPen, 22.5f, 6f, 22.5f, 18f);

            return bitmap;
        }
    }
}
