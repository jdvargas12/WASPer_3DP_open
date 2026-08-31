using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Grasshopper;
using Grasshopper.GUI.Base;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Data;
using Grasshopper.Kernel.Special;
using Grasshopper.Kernel.Types;

namespace WASPer_3DP.Components._1_2_Studies
{
    /// <summary>
    /// The kinds of Grasshopper control Sm06 can register and prepare. Kept deliberately small:
    /// SELVA_INTEGRATION_PLAN section 6.3 only defines local-default/remote-override behavior for
    /// sources that already emit a single, well-typed value. Geometry, files, colors, and custom
    /// producers are out of scope until their behavior is verified.
    /// </summary>
    internal enum WasperSm06ControlKind
    {
        Unknown,
        FloatSlider,
        IntegerSlider,
        ValueList,
        BooleanToggle,
        Panel
    }

    /// <summary>
    /// What the control's current values actually look like. A Panel is the interesting case: it is
    /// a text object, but its content is very often a number, so the shape of the data - not the
    /// object type - is what suggests the best contextual parameter.
    /// </summary>
    internal enum WasperSm06DataShape
    {
        Unknown,
        Integer,
        Number,
        Boolean,
        Text
    }

    /// <summary>
    /// Candidate classification from SELVA_INTEGRATION_PLAN section 6.3 "Idempotency and repair",
    /// plus ControlMissing for a registered GUID whose control has been deleted from the document.
    /// Only Ready and Repairable are actionable; everything else is reported and left untouched.
    /// </summary>
    internal enum WasperSm06Status
    {
        Ready,
        AlreadyPrepared,
        Repairable,
        Ambiguous,
        MissingDependency,
        Unused,
        ControlMissing
    }

    /// <summary>
    /// One entry of the control-to-contextual-parameter mapping table. The GUIDs are the component
    /// GUIDs of the contextual parameters as they appear in a saved definition; they are resolved
    /// through Grasshopper's installed component server at run time, never assumed present.
    /// </summary>
    internal sealed class WasperSm06ContextualType
    {
        internal WasperSm06ContextualType(
            Guid typeGuid,
            string displayName,
            string providerName)
        {
            TypeGuid = typeGuid;
            DisplayName = displayName;
            ProviderName = providerName;
        }

        internal Guid TypeGuid { get; }

        /// <summary>Name as it appears on the Grasshopper ribbon, e.g. "Get Number".</summary>
        internal string DisplayName { get; }

        /// <summary>Plug-in that must be installed for this type to resolve, for diagnostics.</summary>
        internal string ProviderName { get; }

        public override string ToString() => DisplayName;
    }

    /// <summary>
    /// One selectable contextual type for a given control, with the reason it is offered. The
    /// recommended option is the one Sm06 inferred; the others are compatible alternatives the
    /// author may prefer, each carrying the consequence of choosing it.
    /// </summary>
    internal sealed class WasperSm06TypeOption
    {
        internal WasperSm06TypeOption(
            WasperSm06ContextualType type,
            string note,
            bool recommended)
        {
            Type = type;
            Note = note ?? string.Empty;
            Recommended = recommended;
        }

        internal WasperSm06ContextualType Type { get; }

        /// <summary>What choosing this type means for the value the web control sends.</summary>
        internal string Note { get; }

        internal bool Recommended { get; }

        internal bool Available => WasperSm06ContextualTypes.IsAvailable(Type);

        /// <summary>Label shown in the drop-down; also how a chosen row is matched back.</summary>
        internal string Label =>
            Type.DisplayName +
            (Recommended ? "  (detected)" : string.Empty) +
            (Available ? string.Empty : "  - not installed");

        public override string ToString() => Label;
    }

    /// <summary>
    /// What inspecting a control's current output told us: how many values it carries and what they
    /// look like. Read from the parameter's volatile data, so it reflects the last solution rather
    /// than a guess from the object type.
    /// </summary>
    internal sealed class WasperSm06SourceProfile
    {
        internal WasperSm06DataShape Shape { get; set; } = WasperSm06DataShape.Unknown;

        internal int ValueCount { get; set; }

        internal string Sample { get; set; } = string.Empty;

        internal bool IsList => ValueCount > 1;

        internal string Describe()
        {
            string shape;
            switch (Shape)
            {
                case WasperSm06DataShape.Integer:
                    shape = "whole numbers";
                    break;
                case WasperSm06DataShape.Number:
                    shape = "decimal numbers";
                    break;
                case WasperSm06DataShape.Boolean:
                    shape = "true/false";
                    break;
                case WasperSm06DataShape.Text:
                    shape = "text";
                    break;
                default:
                    shape = "no data yet";
                    break;
            }
            string count = ValueCount == 0
                ? "no values"
                : ValueCount == 1 ? "1 value" : $"{ValueCount} values";
            return string.IsNullOrEmpty(Sample)
                ? $"{count}, {shape}"
                : $"{count}, {shape} (e.g. {Sample})";
        }
    }

    /// <summary>
    /// Control kind to contextual parameter mapping, the data inspection behind the recommendation,
    /// and the run-time resolution that decides whether an option is usable at all.
    ///
    /// "Get Number", "Get Integer", "Get Boolean" and "Get String" are external Hops /
    /// Rhino.Compute contextual parameters; "Get Value List" is supplied by the Selva plug-in.
    /// None of them are referenced at compile time - Sm06 asks the component server for them by
    /// GUID (falling back to a ribbon-name lookup) and then verifies that whatever came back really
    /// is an IGH_ContextualParameter before it is allowed near the document.
    /// </summary>
    internal static class WasperSm06ContextualTypes
    {
        internal static readonly WasperSm06ContextualType GetNumber = new WasperSm06ContextualType(
            new Guid("7b36b876-9451-46f5-8220-a200d969cc66"),
            "Get Number",
            "Hops / Rhino.Compute");

        internal static readonly WasperSm06ContextualType GetInteger = new WasperSm06ContextualType(
            new Guid("b228887e-0852-4d9f-bd46-2591646e0d7c"),
            "Get Integer",
            "Hops / Rhino.Compute");

        internal static readonly WasperSm06ContextualType GetBoolean = new WasperSm06ContextualType(
            new Guid("51ef601d-f86e-4ee4-bcf2-3d459d3e95e9"),
            "Get Boolean",
            "Hops / Rhino.Compute");

        internal static readonly WasperSm06ContextualType GetString = new WasperSm06ContextualType(
            new Guid("fed87bdd-8327-49cd-949c-09d70f3c345c"),
            "Get String",
            "Hops / Rhino.Compute");

        internal static readonly WasperSm06ContextualType GetValueList = new WasperSm06ContextualType(
            new Guid("0CC81276-5DB7-4306-9968-086524EC0C6E"),
            "Get Value List",
            "Selva");

        private static readonly WasperSm06ContextualType[] AllTypes =
        {
            GetNumber,
            GetInteger,
            GetBoolean,
            GetString,
            GetValueList
        };

        // Availability is asked on every solve, once per registered control, and again for every
        // drop-down row; emitting a throwaway object each time would be wasteful. The installed
        // component set does not change while Rhino is running, so the answer is cached.
        private static readonly Dictionary<Guid, bool> AvailabilityCache =
            new Dictionary<Guid, bool>();

        /// <summary>
        /// Classifies a document object as one of the supported control kinds. Number sliders split
        /// on their accuracy so an integer-like slider never becomes a floating-point web control.
        /// </summary>
        internal static WasperSm06ControlKind Classify(IGH_DocumentObject documentObject)
        {
            switch (documentObject)
            {
                case GH_NumberSlider slider:
                    return slider.Slider != null &&
                        slider.Slider.Type != GH_SliderAccuracy.Float
                        ? WasperSm06ControlKind.IntegerSlider
                        : WasperSm06ControlKind.FloatSlider;
                case GH_ValueList _:
                    return WasperSm06ControlKind.ValueList;
                case GH_BooleanToggle _:
                    return WasperSm06ControlKind.BooleanToggle;
                case GH_Panel _:
                    return WasperSm06ControlKind.Panel;
                default:
                    return WasperSm06ControlKind.Unknown;
            }
        }

        internal static string Describe(WasperSm06ControlKind kind)
        {
            switch (kind)
            {
                case WasperSm06ControlKind.FloatSlider:
                    return "Number Slider (floating point)";
                case WasperSm06ControlKind.IntegerSlider:
                    return "Number Slider (integer/even/odd)";
                case WasperSm06ControlKind.ValueList:
                    return "Value List";
                case WasperSm06ControlKind.BooleanToggle:
                    return "Boolean Toggle";
                case WasperSm06ControlKind.Panel:
                    return "Panel";
                default:
                    return "Unsupported";
            }
        }

        // ------------------------------------------------------------------
        //  Inspection
        // ------------------------------------------------------------------

        /// <summary>
        /// Looks at what the control is currently carrying: how many values, and whether they read
        /// as whole numbers, decimals, booleans, or text. Volatile data is the honest source - it is
        /// what the recipients actually received on the last solve. A Panel that has never solved
        /// falls back to its own text, so a fresh definition still gets a sensible recommendation.
        /// </summary>
        internal static WasperSm06SourceProfile Inspect(IGH_Param control)
        {
            var profile = new WasperSm06SourceProfile();
            if (control == null)
                return profile;

            List<string> values = ReadVolatileValues(control);
            if (values.Count == 0 && control is GH_Panel panel)
            {
                values = (panel.UserText ?? string.Empty)
                    .Replace("\r\n", "\n")
                    .Split('\n')
                    .Select(line => line.Trim())
                    .Where(line => line.Length > 0)
                    .ToList();
            }

            profile.ValueCount = values.Count;
            profile.Sample = values.Count == 0
                ? string.Empty
                : Truncate(values[0], 24);
            profile.Shape = DetectShape(values);
            return profile;
        }

        private static List<string> ReadVolatileValues(IGH_Param control)
        {
            var values = new List<string>();
            try
            {
                IGH_Structure data = control.VolatileData;
                if (data == null || data.DataCount == 0)
                    return values;
                foreach (object item in (IEnumerable)data.AllData(true))
                {
                    if (item is IGH_Goo goo && goo.IsValid)
                        values.Add(goo.ToString());
                }
            }
            catch
            {
                // Reading volatile data must never be able to break a preview; an empty profile
                // simply falls back to the object-type recommendation.
                values.Clear();
            }
            return values;
        }

        private static WasperSm06DataShape DetectShape(IReadOnlyList<string> values)
        {
            if (values == null || values.Count == 0)
                return WasperSm06DataShape.Unknown;

            bool allIntegers = true;
            bool allNumbers = true;
            bool allBooleans = true;
            foreach (string value in values)
            {
                string trimmed = (value ?? string.Empty).Trim();
                if (!bool.TryParse(trimmed, out _))
                    allBooleans = false;
                if (!double.TryParse(
                        trimmed,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out double number))
                {
                    allNumbers = false;
                    allIntegers = false;
                    continue;
                }
                if (Math.Abs(number - Math.Round(number)) > 1e-9)
                    allIntegers = false;
            }

            if (allBooleans)
                return WasperSm06DataShape.Boolean;
            if (allIntegers && allNumbers)
                return WasperSm06DataShape.Integer;
            if (allNumbers)
                return WasperSm06DataShape.Number;
            return WasperSm06DataShape.Text;
        }

        private static string Truncate(string value, int length)
        {
            string cleaned = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return cleaned.Length <= length ? cleaned : cleaned.Substring(0, length - 3) + "...";
        }

        // ------------------------------------------------------------------
        //  Options and recommendation
        // ------------------------------------------------------------------

        /// <summary>
        /// The compatible contextual types for a control, most appropriate first, each with the
        /// consequence of choosing it. The first entry is what Sm06 inferred; the rest are genuine
        /// alternatives, so an author who knows a Panel really holds a count can pick Get Integer
        /// rather than accepting text.
        ///
        /// Unavailable types stay in the list rather than being hidden: seeing "Get Value List -
        /// not installed" explains why a Value List fell back to text, where an absent row would
        /// just look arbitrary.
        /// </summary>
        internal static List<WasperSm06TypeOption> Options(
            WasperSm06ControlKind kind,
            WasperSm06SourceProfile profile)
        {
            WasperSm06ContextualType recommended = Recommend(kind, profile);
            var ordered = new List<WasperSm06ContextualType>();
            var notes = new Dictionary<Guid, string>();

            switch (kind)
            {
                case WasperSm06ControlKind.FloatSlider:
                case WasperSm06ControlKind.IntegerSlider:
                    ordered.Add(GetNumber);
                    ordered.Add(GetInteger);
                    ordered.Add(GetString);
                    notes[GetNumber.TypeGuid] = kind == WasperSm06ControlKind.IntegerSlider
                        ? "a continuous web control; the slider's whole-number step is not enforced remotely"
                        : "a continuous web control matching the slider";
                    notes[GetInteger.TypeGuid] = kind == WasperSm06ControlKind.FloatSlider
                        ? "a stepped web control; decimal values sent from the web are rounded"
                        : "a stepped web control matching the slider";
                    notes[GetString.TypeGuid] =
                        "a free text box; downstream inputs must accept text or coerce it";
                    break;

                case WasperSm06ControlKind.BooleanToggle:
                    ordered.Add(GetBoolean);
                    ordered.Add(GetInteger);
                    ordered.Add(GetString);
                    notes[GetBoolean.TypeGuid] = "a checkbox matching the toggle";
                    notes[GetInteger.TypeGuid] = "exposes the toggle as 0 or 1";
                    notes[GetString.TypeGuid] = "exposes the toggle as the text true or false";
                    break;

                case WasperSm06ControlKind.ValueList:
                    ordered.Add(GetValueList);
                    ordered.Add(GetString);
                    ordered.Add(GetInteger);
                    ordered.Add(GetNumber);
                    notes[GetValueList.TypeGuid] =
                        "a real drop-down carrying the list's own options and selection";
                    notes[GetString.TypeGuid] =
                        "sends the selected value as text; the option list is not published";
                    notes[GetInteger.TypeGuid] =
                        "sends the selected value as a whole number; the option list is not published";
                    notes[GetNumber.TypeGuid] =
                        "sends the selected value as a number; the option list is not published";
                    break;

                case WasperSm06ControlKind.Panel:
                    // Ordered by what the panel currently holds, so a numeric panel offers its
                    // numeric types first and a label-style panel stays text. Get Value List is
                    // intentionally excluded: Selva's parameter reads option metadata from an
                    // actual GH_ValueList, which a Panel cannot provide.
                    ordered.Add(GetString);
                    ordered.Add(GetNumber);
                    ordered.Add(GetInteger);
                    ordered.Add(GetBoolean);
                    notes[GetString.TypeGuid] = "a text box, matching the panel's own type";
                    notes[GetNumber.TypeGuid] =
                        "a numeric control; the panel's content must always parse as a number";
                    notes[GetInteger.TypeGuid] =
                        "a stepped numeric control; the content must always be whole numbers";
                    notes[GetBoolean.TypeGuid] =
                        "a checkbox; the content must always be true or false";
                    break;

                default:
                    return new List<WasperSm06TypeOption>();
            }

            // The detected type is promoted to the top of whatever the kind's natural order is.
            if (recommended != null && ordered.Remove(recommended))
                ordered.Insert(0, recommended);

            return ordered
                .Select(type => new WasperSm06TypeOption(
                    type,
                    notes.TryGetValue(type.TypeGuid, out string note) ? note : string.Empty,
                    type == recommended))
                .ToList();
        }

        /// <summary>
        /// The inferred best fit. Sliders and toggles are decided by the object itself; Panels and
        /// Value Lists are decided by their current content, because both can legitimately carry
        /// numbers, booleans, or text.
        /// </summary>
        internal static WasperSm06ContextualType Recommend(
            WasperSm06ControlKind kind,
            WasperSm06SourceProfile profile)
        {
            switch (kind)
            {
                case WasperSm06ControlKind.FloatSlider:
                    return GetNumber;
                case WasperSm06ControlKind.IntegerSlider:
                    return GetInteger;
                case WasperSm06ControlKind.BooleanToggle:
                    return GetBoolean;
                case WasperSm06ControlKind.ValueList:
                    // A drop-down is only meaningful if Selva's parameter is installed; without it
                    // the honest fallback is whatever the selected values actually are.
                    return IsAvailable(GetValueList) ? GetValueList : FromShape(profile);
                case WasperSm06ControlKind.Panel:
                    return FromShape(profile);
                default:
                    return null;
            }
        }

        private static WasperSm06ContextualType FromShape(WasperSm06SourceProfile profile)
        {
            switch (profile?.Shape ?? WasperSm06DataShape.Unknown)
            {
                case WasperSm06DataShape.Integer:
                    return GetInteger;
                case WasperSm06DataShape.Number:
                    return GetNumber;
                case WasperSm06DataShape.Boolean:
                    return GetBoolean;
                default:
                    return GetString;
            }
        }

        internal static WasperSm06ContextualType FromGuid(Guid typeGuid)
        {
            return AllTypes.FirstOrDefault(type => type.TypeGuid == typeGuid);
        }

        // ------------------------------------------------------------------
        //  Resolution
        // ------------------------------------------------------------------

        /// <summary>
        /// True when the contextual parameter type is installed. Resolution is by component GUID
        /// first; if that misses, the ribbon name is tried, because a Hops or Selva update could
        /// in principle re-issue a type under a new GUID. A proxy that does not emit an
        /// IGH_ContextualParameter is treated as absent rather than substituted.
        /// </summary>
        internal static bool IsAvailable(WasperSm06ContextualType type)
        {
            if (type == null)
                return false;
            if (AvailabilityCache.TryGetValue(type.TypeGuid, out bool cached))
                return cached;
            bool available = Emit(type) != null;
            AvailabilityCache[type.TypeGuid] = available;
            return available;
        }

        /// <summary>
        /// Creates a fresh, unparented instance of the contextual parameter, or null when the
        /// provider is not installed. Callers must add the returned object to a document
        /// themselves; nothing here touches the canvas.
        /// </summary>
        internal static IGH_Param Emit(WasperSm06ContextualType type)
        {
            if (type == null)
                return null;

            IGH_Param byGuid = Verify(
                Instances.ComponentServer?.EmitObject(type.TypeGuid));
            if (byGuid != null)
                return byGuid;

            IGH_ObjectProxy proxy = Instances.ComponentServer?.ObjectProxies?
                .FirstOrDefault(candidate =>
                    candidate?.Desc != null &&
                    string.Equals(
                        candidate.Desc.Name,
                        type.DisplayName,
                        StringComparison.OrdinalIgnoreCase));
            return proxy == null
                ? null
                : Verify(Instances.ComponentServer.EmitObject(proxy.Guid));
        }

        /// <summary>
        /// A contextual parameter is only usable here if it is both an IGH_Param (so it can carry
        /// wires) and an IGH_ContextualParameter (so Selva's schema discovery will find it). An
        /// object that satisfies only one of the two is discarded, never wired in.
        /// </summary>
        private static IGH_Param Verify(IGH_DocumentObject emitted)
        {
            return emitted is IGH_Param parameter && emitted is IGH_ContextualParameter
                ? parameter
                : null;
        }
    }

    /// <summary>
    /// The persisted relationship between an original control and the contextual parameter Sm06
    /// created (or explicitly adopted) for it. Plan section 6.3 "Reversible removal" requires all
    /// four fields so removal can verify that the graph still matches what was recorded before it
    /// touches anything.
    /// </summary>
    internal sealed class WasperSm06ManagedLink
    {
        public Guid ControlId { get; set; }

        public string ControlNickName { get; set; } = string.Empty;

        public string ControlKind { get; set; } = WasperSm06ControlKind.Unknown.ToString();

        /// <summary>
        /// Stable WASPer key for the interface item. Kept alongside the GUID so a later manifest
        /// can survive the author replacing the Grasshopper object (plan section 4.6).
        /// </summary>
        public string Key { get; set; } = string.Empty;

        public Guid ContextualParameterId { get; set; }

        public Guid ContextualTypeGuid { get; set; }

        public string ContextualTypeName { get; set; } = string.Empty;

        /// <summary>
        /// True when the author overrode the inferred type. Recorded so a later re-run does not
        /// quietly reset a deliberate choice, and so a manifest can report it.
        /// </summary>
        public bool TypeOverridden { get; set; }

        /// <summary>"item" or "list": the access the contextual parameter was created with.</summary>
        public string Access { get; set; } = "item";

        /// <summary>Recipient input parameters the control fed before insertion.</summary>
        public List<Guid> RecipientParameterIds { get; set; } = new List<Guid>();

        /// <summary>
        /// True when the contextual parameter already existed and Sm06 took ownership of it rather
        /// than creating it. Recorded so a future version can choose to leave adopted nodes behind.
        /// </summary>
        public bool Adopted { get; set; }

        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// One transient preview row. Built fresh on every preview so it always reflects the live
    /// graph; never persisted. The type and access are editable in the dialog, which is why the
    /// status has to be recomputed whenever they change.
    /// </summary>
    internal sealed class WasperSm06Candidate
    {
        internal Guid ControlId { get; set; }

        /// <summary>Name currently stored on the source control when the preview opens.</summary>
        internal string OriginalControlNickName { get; set; } = string.Empty;

        /// <summary>
        /// Shared authoring name requested in the preview. Preparation applies it to both the
        /// source control and its contextual parameter, so the Grasshopper and client-interface
        /// labels stay synchronized.
        /// </summary>
        internal string ControlNickName { get; set; } = string.Empty;

        internal bool NameChanged => !string.Equals(
            OriginalControlNickName,
            ControlNickName,
            StringComparison.Ordinal);

        internal WasperSm06ControlKind Kind { get; set; } = WasperSm06ControlKind.Unknown;

        internal WasperSm06SourceProfile Profile { get; set; } = new WasperSm06SourceProfile();

        /// <summary>Compatible contextual types, inferred one first.</summary>
        internal List<WasperSm06TypeOption> Options { get; set; } =
            new List<WasperSm06TypeOption>();

        /// <summary>What Sm06 inferred, kept so an override can be reported as such.</summary>
        internal WasperSm06ContextualType RecommendedType { get; set; }

        /// <summary>What will actually be inserted. Editable in the preview dialog.</summary>
        internal WasperSm06ContextualType SelectedType { get; set; }

        internal bool TypeOverridden =>
            SelectedType != null && RecommendedType != null && SelectedType != RecommendedType;

        /// <summary>Item or list access requested on the inserted parameter.</summary>
        internal GH_ParamAccess Access { get; set; } = GH_ParamAccess.item;

        internal WasperSm06Status Status { get; set; } = WasperSm06Status.Ambiguous;

        /// <summary>Recipient inputs still wired directly to the control.</summary>
        internal List<IGH_Param> DirectRecipients { get; } = new List<IGH_Param>();

        /// <summary>Contextual parameters already reading this control.</summary>
        internal List<IGH_Param> ContextualRecipients { get; } = new List<IGH_Param>();

        /// <summary>An existing compatible contextual parameter, when one was found.</summary>
        internal IGH_Param ExistingContextualParameter { get; set; }

        internal WasperSm06ManagedLink ExistingLink { get; set; }

        internal string Note { get; set; } = string.Empty;

        /// <summary>Preview rows default to selected only when acting on them is safe.</summary>
        internal bool Selected { get; set; }

        internal bool IsActionable =>
            Status == WasperSm06Status.Ready || Status == WasperSm06Status.Repairable;

        internal string TypeName => SelectedType?.DisplayName ?? "-";

        internal string AccessName => Access == GH_ParamAccess.list ? "List" : "Item";

        internal int RecipientCount => DirectRecipients.Count;

        internal string StatusText
        {
            get
            {
                switch (Status)
                {
                    case WasperSm06Status.Ready:
                        return "Ready";
                    case WasperSm06Status.AlreadyPrepared:
                        return "Already prepared";
                    case WasperSm06Status.Repairable:
                        return "Repairable";
                    case WasperSm06Status.Ambiguous:
                        return "Ambiguous";
                    case WasperSm06Status.MissingDependency:
                        return "Missing dependency";
                    case WasperSm06Status.Unused:
                        return "Unused";
                    default:
                        return "Control missing";
                }
            }
        }
    }

    /// <summary>
    /// Counts for the completion report required by plan section 6.3 "User experience and undo".
    /// </summary>
    internal sealed class WasperSm06Report
    {
        internal int Created { get; set; }

        internal int Reused { get; set; }

        internal int Repaired { get; set; }

        internal int Removed { get; set; }

        internal int Skipped { get; set; }

        internal int Failed { get; set; }

        internal List<string> Messages { get; } = new List<string>();

        internal string Summarize(string operation)
        {
            return $"{operation}: {Created} created, {Reused} reused, {Repaired} repaired, " +
                $"{Removed} removed, {Skipped} skipped, {Failed} failed.";
        }
    }
}
