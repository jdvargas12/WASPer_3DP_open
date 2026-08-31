using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

using Grasshopper;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Undo;
using Grasshopper.Kernel.Undo.Actions;

namespace WASPer_3DP.Components._1_2_Studies
{
    /// <summary>
    /// The graph half of Sm06: planning (read-only classification of every registered control) and
    /// the two mutating operations, insertion and removal.
    ///
    /// Every mutation here is an intentional document edit. Nothing in this file is reachable from
    /// SolveInstance - the plan is explicit that preparation must never run during an ordinary
    /// solve, study run, file open, or manifest export.
    /// </summary>
    public sealed partial class wsp_Sm06_Interface_Input_Builder
    {
        private const float InsertionGap = 18f;
        private const float CollisionStep = 26f;
        private const int CollisionAttempts = 16;

        // ------------------------------------------------------------------
        //  Planning (read-only)
        // ------------------------------------------------------------------

        /// <summary>
        /// Classifies every registered control against the live graph. Pure inspection: no object
        /// is created, moved, wired, or expired, so this is safe to call for a preview or for the
        /// component's own summary output.
        /// </summary>
        internal List<WasperSm06Candidate> BuildPreparationCandidates()
        {
            var candidates = new List<WasperSm06Candidate>();
            GH_Document document = ActiveDocument();
            if (document == null)
                return candidates;

            foreach (Guid controlId in _linkedControlIds)
            {
                var candidate = new WasperSm06Candidate { ControlId = controlId };
                candidate.ExistingLink = _managedLinks
                    .FirstOrDefault(link => link.ControlId == controlId);

                if (!(document.FindObject(controlId, true) is IGH_Param control))
                {
                    candidate.Status = WasperSm06Status.ControlMissing;
                    candidate.ControlNickName = candidate.ExistingLink?.ControlNickName ??
                        controlId.ToString("N").Substring(0, 8);
                    candidate.OriginalControlNickName = candidate.ControlNickName;
                    candidate.Note = "The registered control is no longer in this document. " +
                        "It is never remapped to another object automatically.";
                    candidates.Add(candidate);
                    continue;
                }

                candidate.OriginalControlNickName = DisplayName(control);
                candidate.ControlNickName = candidate.OriginalControlNickName;
                candidate.Kind = WasperSm06ContextualTypes.Classify(control);

                // What the control is actually carrying decides the recommendation for the kinds
                // that can hold more than one shape of value - a Panel above all.
                candidate.Profile = WasperSm06ContextualTypes.Inspect(control);
                candidate.Options = WasperSm06ContextualTypes.Options(
                    candidate.Kind,
                    candidate.Profile);
                candidate.RecommendedType = WasperSm06ContextualTypes.Recommend(
                    candidate.Kind,
                    candidate.Profile);

                // A previous run's deliberate override wins over the fresh inference, so reopening
                // the preview does not quietly reset a choice the author already made.
                WasperSm06ContextualType stored = candidate.ExistingLink == null
                    ? null
                    : WasperSm06ContextualTypes.FromGuid(candidate.ExistingLink.ContextualTypeGuid);
                candidate.SelectedType = candidate.ExistingLink?.TypeOverridden == true && stored != null
                    ? stored
                    : candidate.RecommendedType;
                candidate.Access = ResolveAccess(candidate);

                // Recipients are split before anything else: a contextual parameter downstream of
                // the control is a candidate for reuse, everything else is a wire to re-route.
                List<IGH_Param> recipients = control.Recipients?.ToList() ?? new List<IGH_Param>();
                candidate.ContextualRecipients.AddRange(recipients
                    .Where(recipient => recipient is IGH_ContextualParameter));
                candidate.DirectRecipients.AddRange(
                    recipients.Except(candidate.ContextualRecipients));

                // A single existing contextual parameter is the strongest evidence of the
                // author's intent. Preserve its compatible type and live access as the preview
                // defaults, even when today's source data would infer a different recommendation.
                // The recommendation is retained separately so the override remains visible.
                if (candidate.ContextualRecipients.Count == 1)
                {
                    IGH_Param existing = candidate.ContextualRecipients[0];
                    WasperSm06ContextualType existingType =
                        WasperSm06ContextualTypes.FromGuid(existing.ComponentGuid);
                    if (existingType != null && candidate.Options.Any(option =>
                            option.Type.TypeGuid == existing.ComponentGuid))
                    {
                        candidate.SelectedType = existingType;
                    }
                }

                ClassifyCandidate(candidate);
                candidates.Add(candidate);
            }

            return candidates;
        }

        /// <summary>
        /// Item or list access is inferred metadata, not an authoring choice. A control that emits
        /// several values (a multi-line Panel or a Value List in check-list mode) gets list access;
        /// every other control gets item access. Existing parameters are repaired to this live
        /// cardinality when necessary.
        /// </summary>
        private static GH_ParamAccess ResolveAccess(WasperSm06Candidate candidate)
        {
            return candidate.Profile != null && candidate.Profile.IsList
                ? GH_ParamAccess.list
                : GH_ParamAccess.item;
        }

        /// <summary>
        /// Decides a candidate's status from its currently selected type and the live wiring. Split
        /// out of the planning loop so the preview dialog can call it again whenever the author
        /// changes the type or access in the drop-down: a different type can turn an "Already
        /// prepared" row into "Ambiguous", and the author needs to see that before applying.
        /// </summary>
        internal void ClassifyCandidate(WasperSm06Candidate candidate)
        {
            if (candidate == null || candidate.Status == WasperSm06Status.ControlMissing)
                return;

            candidate.Selected = false;

            if (candidate.SelectedType == null)
            {
                candidate.Status = WasperSm06Status.Ambiguous;
                candidate.Note = $"{WasperSm06ContextualTypes.Describe(candidate.Kind)} has no " +
                    "compatible contextual parameter.";
                return;
            }

            if (candidate.DirectRecipients.Count == 0 &&
                candidate.ContextualRecipients.Count == 0)
            {
                if (!WasperSm06ContextualTypes.IsAvailable(candidate.SelectedType))
                {
                    candidate.Status = WasperSm06Status.MissingDependency;
                    candidate.Note = $"'{candidate.SelectedType.DisplayName}' is not installed " +
                        $"({candidate.SelectedType.ProviderName}). No substitute is inserted - pick " +
                        "another type or install the provider.";
                    return;
                }

                candidate.Status = WasperSm06Status.Ready;
                candidate.Note = $"Create a standalone '{candidate.SelectedType.DisplayName}' " +
                    $"named '{candidate.ControlNickName}' beside this control. It will carry the " +
                    "current local value and can be connected to a downstream input later.";
                candidate.Selected = true;
                return;
            }

            if (!WasperSm06ContextualTypes.IsAvailable(candidate.SelectedType))
            {
                candidate.Status = WasperSm06Status.MissingDependency;
                candidate.Note = $"'{candidate.SelectedType.DisplayName}' is not installed " +
                    $"({candidate.SelectedType.ProviderName}). No substitute is inserted - pick " +
                    "another type or install the provider.";
                return;
            }

            if (candidate.ContextualRecipients.Count > 1)
            {
                candidate.Status = WasperSm06Status.Ambiguous;
                candidate.Note = $"{candidate.ContextualRecipients.Count} contextual parameters " +
                    "already read this control. Reduce them to one before preparing it.";
                return;
            }

            if (candidate.ContextualRecipients.Count == 1)
            {
                IGH_Param existing = candidate.ContextualRecipients[0];
                candidate.ExistingContextualParameter = existing;

                if (existing.ComponentGuid != candidate.SelectedType.TypeGuid)
                {
                    candidate.Status = WasperSm06Status.Ambiguous;
                    candidate.Note = $"An existing '{existing.Name}' reads this control, but the " +
                        $"chosen type is '{candidate.SelectedType.DisplayName}'. Existing wiring " +
                        "is never rewritten to change a type - delete that node first, or choose " +
                        $"'{existing.Name}' here.";
                    return;
                }

                bool nickNameDrifted = !string.Equals(
                    existing.NickName,
                    candidate.ControlNickName,
                    StringComparison.Ordinal);
                bool accessChanged = existing.Access != candidate.Access;
                if (candidate.DirectRecipients.Count > 0 || nickNameDrifted ||
                    candidate.NameChanged || accessChanged)
                {
                    candidate.Status = WasperSm06Status.Repairable;
                    candidate.Note = BuildRepairNote(
                        candidate.DirectRecipients.Count,
                        nickNameDrifted,
                        candidate.NameChanged,
                        accessChanged,
                        candidate.AccessName,
                        candidate.ControlNickName);
                    candidate.Selected = true;
                }
                else
                {
                    candidate.Status = WasperSm06Status.AlreadyPrepared;
                    candidate.Note = candidate.ExistingLink == null
                        ? "A compatible contextual parameter is already in place. Preparing again " +
                            "adopts it without creating a second one."
                        : "Already prepared and managed by this component.";
                    // An unmanaged but otherwise correct node is worth adopting so that removal
                    // can later reverse it; that is a bookkeeping change only.
                    candidate.Selected = candidate.ExistingLink == null;
                }
                return;
            }

            candidate.ExistingContextualParameter = null;
            candidate.Status = WasperSm06Status.Ready;
            candidate.Note = $"Insert '{candidate.SelectedType.DisplayName}' " +
                $"({candidate.AccessName.ToLowerInvariant()} access) nicknamed " +
                $"'{candidate.ControlNickName}' in front of " +
                $"{candidate.DirectRecipients.Count} recipient input(s)." +
                (candidate.TypeOverridden
                    ? $" Overrides the detected '{candidate.RecommendedType.DisplayName}'."
                    : string.Empty);
            candidate.Selected = true;
        }

        private static string BuildRepairNote(
            int strayRecipients,
            bool nickNameDrifted,
            bool controlNameChanged,
            bool accessChanged,
            string requestedAccess,
            string expectedNickName)
        {
            var parts = new List<string>();
            if (strayRecipients > 0)
            {
                parts.Add($"{strayRecipients} recipient input(s) still read the control directly " +
                    "and would be routed through the existing contextual parameter");
            }
            if (nickNameDrifted)
                parts.Add($"the contextual parameter would be renamed to '{expectedNickName}'");
            if (controlNameChanged)
                parts.Add($"the source control would be renamed to '{expectedNickName}'");
            if (accessChanged)
                parts.Add($"the contextual parameter access would change to {requestedAccess}");
            return char.ToUpperInvariant(parts[0][0]) + parts[0].Substring(1) +
                (parts.Count > 1 ? "; " + string.Join("; ", parts.Skip(1)) : string.Empty) + ".";
        }

        /// <summary>
        /// Classifies every managed link for removal. A link only becomes actionable when the graph
        /// still matches what was recorded: the control still feeds the contextual parameter, that
        /// parameter has no other source, and its type is unchanged. Anything else is Ambiguous and
        /// is left alone (plan section 6.3 "Reversible removal").
        /// </summary>
        internal List<WasperSm06Candidate> BuildRemovalCandidates()
        {
            var candidates = new List<WasperSm06Candidate>();
            GH_Document document = ActiveDocument();
            if (document == null)
                return candidates;

            foreach (WasperSm06ManagedLink link in _managedLinks)
            {
                var candidate = new WasperSm06Candidate
                {
                    ControlId = link.ControlId,
                    OriginalControlNickName = link.ControlNickName,
                    ControlNickName = link.ControlNickName,
                    ExistingLink = link,
                    SelectedType = WasperSm06ContextualTypes.FromGuid(link.ContextualTypeGuid) ??
                        new WasperSm06ContextualType(
                            link.ContextualTypeGuid,
                            link.ContextualTypeName,
                            string.Empty),
                    Access = string.Equals(link.Access, "list", StringComparison.OrdinalIgnoreCase)
                        ? GH_ParamAccess.list
                        : GH_ParamAccess.item
                };
                if (Enum.TryParse(link.ControlKind, out WasperSm06ControlKind kind))
                    candidate.Kind = kind;

                IGH_Param control = document.FindObject(link.ControlId, true) as IGH_Param;
                IGH_Param contextual =
                    document.FindObject(link.ContextualParameterId, true) as IGH_Param;
                candidate.Profile = WasperSm06ContextualTypes.Inspect(control);

                if (contextual == null)
                {
                    // Nothing to undo on the canvas; the stale bookkeeping row is dropped instead.
                    candidate.Status = WasperSm06Status.AlreadyPrepared;
                    candidate.Note = "The contextual parameter is already gone. Removing clears " +
                        "the stored relationship only.";
                    candidate.Selected = true;
                    candidates.Add(candidate);
                    continue;
                }

                candidate.ExistingContextualParameter = contextual;
                candidate.DirectRecipients.AddRange(
                    contextual.Recipients?.ToList() ?? new List<IGH_Param>());

                if (control == null)
                {
                    candidate.Status = WasperSm06Status.Ambiguous;
                    candidate.Note = "The original control is gone, so its wiring cannot be " +
                        "restored. The contextual parameter is left in place.";
                }
                else if (contextual.ComponentGuid != link.ContextualTypeGuid)
                {
                    candidate.Status = WasperSm06Status.Ambiguous;
                    candidate.Note = "The contextual parameter's type no longer matches the " +
                        "stored relationship.";
                }
                else if (contextual.Sources == null ||
                    !contextual.Sources.Any(source => source.InstanceGuid == link.ControlId))
                {
                    candidate.Status = WasperSm06Status.Ambiguous;
                    candidate.Note = "The original control no longer feeds this contextual " +
                        "parameter; it appears to have been repurposed.";
                }
                else if (contextual.SourceCount > 1)
                {
                    candidate.Status = WasperSm06Status.Ambiguous;
                    candidate.Note = $"The contextual parameter has {contextual.SourceCount} " +
                        "sources. Only a single-source relationship can be reversed safely.";
                }
                else
                {
                    candidate.Status = WasperSm06Status.Ready;
                    candidate.Note = $"Reconnect the control to {candidate.DirectRecipients.Count} " +
                        $"recipient input(s), then delete '{contextual.NickName}'.";
                    candidate.Selected = true;
                }

                candidates.Add(candidate);
            }

            return candidates;
        }

        // ------------------------------------------------------------------
        //  Insertion
        // ------------------------------------------------------------------

        /// <summary>
        /// Inserts or repairs the selected contextual parameters as one grouped, undoable batch and
        /// schedules a single solution afterwards. Wire order is preserved by ReplaceSource, so a
        /// recipient input keeps any other sources it already had.
        /// </summary>
        internal WasperSm06Report ApplyPreparation(IReadOnlyList<WasperSm06Candidate> candidates)
        {
            var report = new WasperSm06Report();
            GH_Document document = ActiveDocument();
            if (document == null)
            {
                report.Messages.Add("No active Grasshopper document was found.");
                return report;
            }

            var record = new GH_UndoRecord("WASPer Sm06: insert contextual inputs");
            var expired = new List<IGH_Param>();
            bool mutated = false;

            foreach (WasperSm06Candidate candidate in candidates ??
                Array.Empty<WasperSm06Candidate>())
            {
                if (!candidate.Selected)
                {
                    report.Skipped++;
                    continue;
                }

                if (!(document.FindObject(candidate.ControlId, true) is IGH_Param control))
                {
                    report.Failed++;
                    report.Messages.Add($"{candidate.ControlNickName}: the control disappeared " +
                        "before the operation ran.");
                    continue;
                }

                try
                {
                    switch (candidate.Status)
                    {
                        case WasperSm06Status.Ready:
                            mutated |= InsertContextualParameter(
                                document, record, control, candidate, report, expired);
                            break;
                        case WasperSm06Status.Repairable:
                            mutated |= RepairContextualParameter(
                                record, control, candidate, report, expired);
                            break;
                        case WasperSm06Status.AlreadyPrepared
                            when candidate.ExistingContextualParameter != null:
                            // Bookkeeping-only adoption of a correct, unmanaged node.
                            AdoptLink(control, candidate, candidate.ExistingContextualParameter);
                            report.Reused++;
                            break;
                        default:
                            report.Skipped++;
                            break;
                    }
                }
                catch (Exception exception)
                {
                    report.Failed++;
                    report.Messages.Add($"{candidate.ControlNickName}: {exception.Message}");
                }
            }

            if (mutated)
                document.UndoServer.PushUndoRecord(record);
            ScheduleRefresh(document, expired);
            return report;
        }

        private bool InsertContextualParameter(
            GH_Document document,
            GH_UndoRecord record,
            IGH_Param control,
            WasperSm06Candidate candidate,
            WasperSm06Report report,
            List<IGH_Param> expired)
        {
            IGH_Param contextual = WasperSm06ContextualTypes.Emit(candidate.SelectedType);
            if (contextual == null)
            {
                report.Failed++;
                report.Messages.Add($"{candidate.ControlNickName}: " +
                    $"'{candidate.SelectedType.DisplayName}' could not be created " +
                    $"({candidate.SelectedType.ProviderName} not installed).");
                return false;
            }

            // Snapshot the recipients and their wire state before anything changes: a GH_WireAction
            // records the sources a parameter has at the moment it is constructed.
            List<IGH_Param> recipients = candidate.DirectRecipients
                .Where(recipient => recipient != null)
                .ToList();
            var wireActions = recipients
                .Select(recipient => new GH_WireAction(recipient))
                .ToList();

            contextual.NickName = candidate.ControlNickName;
            TryApplyAccess(contextual, candidate.Access);
            document.AddObject(contextual, false);
            PlaceContextualParameter(document, contextual, control, recipients);

            // The new parameter is validated in the document before any existing wire is touched.
            if (document.FindObject(contextual.InstanceGuid, true) == null)
            {
                report.Failed++;
                report.Messages.Add($"{candidate.ControlNickName}: the contextual parameter " +
                    "could not be added to the document; no wire was changed.");
                return false;
            }

            RenameControl(record, control, candidate.ControlNickName);
            contextual.AddSource(control);
            foreach (IGH_Param recipient in recipients)
            {
                recipient.ReplaceSource(control, contextual);
                expired.Add(recipient);
            }

            foreach (GH_WireAction wireAction in wireActions)
                record.AddAction(wireAction);
            // Added last so undo removes the object first and the wire actions then restore the
            // recipients' original sources.
            record.AddAction(new GH_AddObjectAction(contextual));

            AdoptLink(control, candidate, contextual, adopted: false, recipients: recipients);
            report.Created++;
            return true;
        }

        private bool RepairContextualParameter(
            GH_UndoRecord record,
            IGH_Param control,
            WasperSm06Candidate candidate,
            WasperSm06Report report,
            List<IGH_Param> expired)
        {
            IGH_Param contextual = candidate.ExistingContextualParameter;
            if (contextual == null)
            {
                report.Failed++;
                report.Messages.Add($"{candidate.ControlNickName}: the existing contextual " +
                    "parameter could not be resolved.");
                return false;
            }

            List<IGH_Param> recipients = candidate.DirectRecipients
                .Where(recipient => recipient != null)
                .ToList();
            var wireActions = recipients
                .Select(recipient => new GH_WireAction(recipient))
                .ToList();

            bool changed = false;
            changed |= RenameControl(record, control, candidate.ControlNickName);
            if (!string.Equals(
                    contextual.NickName,
                    candidate.ControlNickName,
                    StringComparison.Ordinal))
            {
                record.AddAction(new GH_NickNameAction(contextual));
                contextual.NickName = candidate.ControlNickName;
                changed = true;
            }

            // Access is a safe repair: it changes how the same node reads its data, not the graph.
            if (contextual.Access != candidate.Access && TryApplyAccess(contextual, candidate.Access))
                changed = true;

            foreach (IGH_Param recipient in recipients)
            {
                recipient.ReplaceSource(control, contextual);
                expired.Add(recipient);
                changed = true;
            }

            foreach (GH_WireAction wireAction in wireActions)
                record.AddAction(wireAction);

            if (changed)
            {
                AdoptLink(control, candidate, contextual, adopted: true, recipients: recipients);
                report.Repaired++;
            }
            else
            {
                report.Skipped++;
            }
            return changed;
        }

        /// <summary>
        /// Keeps the local source label and contextual input label identical. The nickname action
        /// joins the same batch undo record as the graph edits, so one Undo restores both names.
        /// </summary>
        private static bool RenameControl(
            GH_UndoRecord record,
            IGH_Param control,
            string requestedName)
        {
            string name = (requestedName ?? string.Empty).Trim();
            if (control == null || name.Length == 0 ||
                string.Equals(DisplayName(control), name, StringComparison.Ordinal))
            {
                return false;
            }

            record.AddAction(new GH_NickNameAction(control));
            control.NickName = name;
            return true;
        }

        // ------------------------------------------------------------------
        //  Removal
        // ------------------------------------------------------------------

        /// <summary>
        /// Reverses the insertion for the selected managed links: every recipient is reconnected to
        /// the original control first, and only then is the contextual parameter deleted. The
        /// control itself is never removed, and the registration survives so it can be prepared
        /// again later.
        /// </summary>
        internal WasperSm06Report ApplyRemoval(IReadOnlyList<WasperSm06Candidate> candidates)
        {
            var report = new WasperSm06Report();
            GH_Document document = ActiveDocument();
            if (document == null)
            {
                report.Messages.Add("No active Grasshopper document was found.");
                return report;
            }

            var record = new GH_UndoRecord("WASPer Sm06: remove contextual inputs");
            var expired = new List<IGH_Param>();
            var clearedLinks = new List<WasperSm06ManagedLink>();
            bool mutated = false;

            foreach (WasperSm06Candidate candidate in candidates ??
                Array.Empty<WasperSm06Candidate>())
            {
                if (!candidate.Selected || candidate.ExistingLink == null)
                {
                    if (!candidate.Selected)
                        report.Skipped++;
                    continue;
                }

                if (candidate.Status == WasperSm06Status.Ambiguous)
                {
                    report.Skipped++;
                    continue;
                }

                IGH_Param contextual = candidate.ExistingContextualParameter;
                if (contextual == null)
                {
                    clearedLinks.Add(candidate.ExistingLink);
                    report.Removed++;
                    continue;
                }

                if (!(document.FindObject(candidate.ControlId, true) is IGH_Param control))
                {
                    report.Skipped++;
                    continue;
                }

                try
                {
                    List<IGH_Param> recipients = contextual.Recipients?.ToList() ??
                        new List<IGH_Param>();
                    var wireActions = recipients
                        .Select(recipient => new GH_WireAction(recipient))
                        .ToList();
                    var removeAction = new GH_RemoveObjectAction(contextual);

                    foreach (IGH_Param recipient in recipients)
                    {
                        recipient.ReplaceSource(contextual, control);
                        expired.Add(recipient);
                    }

                    // RemoveObject detaches the parameter's own wires; the control keeps every
                    // other recipient it had, and is never itself removed.
                    document.RemoveObject(contextual, false);

                    foreach (GH_WireAction wireAction in wireActions)
                        record.AddAction(wireAction);
                    // Added last, and so undone first: a record replays its actions in reverse, and
                    // the wire actions can only restore a recipient's source list once the
                    // contextual parameter is back in the document.
                    record.AddAction(removeAction);

                    clearedLinks.Add(candidate.ExistingLink);
                    report.Removed++;
                    mutated = true;
                }
                catch (Exception exception)
                {
                    report.Failed++;
                    report.Messages.Add($"{candidate.ControlNickName}: {exception.Message}");
                }
            }

            foreach (WasperSm06ManagedLink link in clearedLinks)
                _managedLinks.Remove(link);

            if (mutated)
                document.UndoServer.PushUndoRecord(record);
            ScheduleRefresh(document, expired);
            return report;
        }

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Records (or refreshes) the relationship between a control and its contextual parameter.
        /// One entry per control: preparing twice updates the existing row instead of adding a
        /// second one.
        /// </summary>
        private void AdoptLink(
            IGH_Param control,
            WasperSm06Candidate candidate,
            IGH_Param contextual,
            bool adopted = true,
            IReadOnlyList<IGH_Param> recipients = null)
        {
            WasperSm06ManagedLink link = _managedLinks
                .FirstOrDefault(existing => existing.ControlId == candidate.ControlId);
            if (link == null)
            {
                link = new WasperSm06ManagedLink { ControlId = candidate.ControlId };
                _managedLinks.Add(link);
            }

            link.ControlNickName = DisplayName(control);
            link.ControlKind = candidate.Kind.ToString();
            link.Key = string.IsNullOrWhiteSpace(link.Key)
                ? BuildKey(link.ControlNickName, candidate.ControlId)
                : link.Key;
            link.ContextualParameterId = contextual.InstanceGuid;
            link.ContextualTypeGuid = contextual.ComponentGuid;
            link.ContextualTypeName = candidate.SelectedType?.DisplayName ?? contextual.Name;
            link.TypeOverridden = candidate.TypeOverridden;
            link.Access = contextual.Access == GH_ParamAccess.list ? "list" : "item";
            link.Adopted = adopted;

            IReadOnlyList<IGH_Param> recorded = recipients ??
                (IReadOnlyList<IGH_Param>)(contextual.Recipients?.ToList() ??
                    new List<IGH_Param>());
            link.RecipientParameterIds = recorded
                .Where(recipient => recipient != null)
                .Select(recipient => recipient.InstanceGuid)
                .Distinct()
                .ToList();
        }

        /// <summary>
        /// Requests item or list access on a contextual parameter. Selva reads the parameter's own
        /// access when it builds a schema input, so this is what makes a multi-line Panel or a
        /// check-list Value List arrive as a list rather than a single value. Some providers
        /// override Access themselves, so the result is checked rather than assumed, and a refusal
        /// is not treated as a failure of the insertion.
        /// </summary>
        private static bool TryApplyAccess(IGH_Param contextual, GH_ParamAccess access)
        {
            if (contextual == null || contextual.Access == access)
                return false;
            try
            {
                contextual.Access = access;
                return contextual.Access == access;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Default position: a fixed visual gap immediately to the right of the control, with both
        /// objects vertically center-aligned. A simple downward collision pass is used only when
        /// that preferred position is occupied. Existing objects are never moved.
        /// </summary>
        private static void PlaceContextualParameter(
            GH_Document document,
            IGH_Param contextual,
            IGH_Param control,
            IReadOnlyList<IGH_Param> recipients)
        {
            if (contextual.Attributes == null)
                contextual.CreateAttributes();
            if (contextual.Attributes == null || control?.Attributes == null)
                return;

            RectangleF controlBounds = control.Attributes.Bounds;
            contextual.Attributes.ExpireLayout();
            contextual.Attributes.PerformLayout();
            RectangleF contextualBounds = contextual.Attributes.Bounds;
            PointF initialPivot = contextual.Attributes.Pivot;
            float pivotOffsetX = initialPivot.X - contextualBounds.Left;
            float pivotOffsetY = initialPivot.Y - contextualBounds.Top;
            float targetX = controlBounds.Right + InsertionGap + pivotOffsetX;
            float targetTop = controlBounds.Top +
                ((controlBounds.Height - contextualBounds.Height) * 0.5f);
            float targetY = targetTop + pivotOffsetY;
            for (int attempt = 0; attempt < CollisionAttempts; attempt++)
            {
                contextual.Attributes.Pivot = new PointF(targetX, targetY);
                contextual.Attributes.ExpireLayout();
                contextual.Attributes.PerformLayout();
                if (!Overlaps(document, contextual))
                    return;
                targetY += CollisionStep;
            }
        }

        private static bool Overlaps(GH_Document document, IGH_Param contextual)
        {
            RectangleF bounds = RectangleF.Inflate(contextual.Attributes.Bounds, 4f, 4f);
            return document.Objects.Any(other =>
                other != null &&
                other.InstanceGuid != contextual.InstanceGuid &&
                other.Attributes != null &&
                other.Attributes.Bounds.IntersectsWith(bounds));
        }

        /// <summary>
        /// One scheduled solution for the whole batch, never one per wire change.
        /// </summary>
        private void ScheduleRefresh(GH_Document document, IReadOnlyList<IGH_Param> expired)
        {
            foreach (IGH_Param parameter in expired.Distinct())
                parameter?.ExpireSolution(false);
            ExpireSolution(false);
            document.ScheduleSolution(10, scheduled => scheduled.NewSolution(false));
            Instances.RedrawCanvas();
        }

        internal GH_Document ActiveDocument()
        {
            return OnPingDocument() ?? Instances.ActiveCanvas?.Document;
        }

        internal static string DisplayName(IGH_DocumentObject documentObject)
        {
            if (documentObject == null)
                return string.Empty;
            return string.IsNullOrWhiteSpace(documentObject.NickName)
                ? documentObject.Name
                : documentObject.NickName;
        }

        /// <summary>
        /// A stable, lower-case interface key derived from the control nickname, disambiguated by a
        /// GUID fragment so two identically nicknamed controls never collide.
        /// </summary>
        internal static string BuildKey(string nickName, Guid controlId)
        {
            string cleaned = new string((nickName ?? string.Empty)
                .Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '_')
                .ToArray())
                .Trim('_');
            while (cleaned.Contains("__"))
                cleaned = cleaned.Replace("__", "_");
            if (string.IsNullOrEmpty(cleaned))
                cleaned = "input";
            return $"{cleaned}_{controlId.ToString("N").Substring(0, 6)}";
        }
    }
}
